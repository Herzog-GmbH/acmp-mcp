// AcmpWumWatch: Waechter des Fehler-Stopps fuer Windows-Updates (seit 23.10.2026). Laeuft im MCP-Server alle
// intervalMinutes (Config-Section wumWatch, Standard 60), wertet die Regeln aus (AcmpWumRings.Evaluate) und
//  - haelt Updates an (mode=hold; mode=report meldet nur),
//  - laesst eigene Stopps wegen "ungetestet" (S2) automatisch weiterlaufen, sobald ein Testrechner das Update hat; scheitert ein solches
//    Update inzwischen, wird der Stopp zu S1 (ein Mensch entscheidet),
//  - merkt, wenn ein Mensch ein angehaltenes Update weiterlaufen laesst - ueber MCP/Webseite (NoteManual) oder in der Console (Uebergang
//    war manuell, ist jetzt automatisch). Die Fehlschlaege, die er dabei in Kauf nimmt (Testrechner + Fehler), halten es nicht wieder an;
//    ein anderer Rechner oder ein anderer Fehler schon,
//  - legt je neuer Meldung einen offenen Punkt an (Bereich Updates) und schliesst ihn, wenn sich die Sache erledigt hat,
//  - schickt je Lauf hoechstens EINE Mail mit allen noch nicht gemailten Meldungen (Stopp, Rechnerdefekt, Fehler nach der Freigabe); scheitert
//    der Versand, versucht der naechste Lauf es wieder.
// Erster Lauf (noch keine wum-waechter.json): vorhandene Befunde ausser Stopps gelten als bekannt - sie stehen auf der Seite, ohne Mail und
// ohne offenen Punkt.
// Fail-open: Faellt der Waechter aus, laeuft ACMP wie bisher (die Updates gehen nach Plan weiter); Seite und Dashboard zeigen "still" bzw. "Fehler".
// Notbremse: kein Schreiben (weder anhalten noch weiterlaufen lassen), nur melden - geprueft vor jedem Schreiben. Aktivitaetsprotokoll:
// Bediener "wum-waechter", Start-Eintrag vor dem Schreiben (nicht schreibbar -> nichts geschrieben).
// Zustand in wum-waechter.json im Server-Ordner (Temp-Datei + Ersetzen, Sicherung .bak). Ist sie unlesbar, gilt die Sicherung; sind beide
// unlesbar, meldet der Waechter nur, bis ein Bediener auf der Seite "Jetzt pruefen" drueckt.
// net48 / C# 5 (csc v4.0.30319): keine string-Interpolation, keine expression-bodied member, kein ?.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Mail;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

class AcmpWumWatch
{
    public const string FileName = "wum-waechter.json";
    public const string Who = AcmpWumRings.Waechter;
    public const int MaxEvents = 500;
    const string Held = "angehalten";

    readonly AcmpWumRings _rings;
    readonly AcmpActivity _act;
    readonly AcmpOpenItems _open;
    readonly Action<string> _log;
    readonly string _file;
    readonly string _publicUrl;
    readonly object _runLock = new object();
    readonly object _stLock = new object();
    Timer _timer;
    JObject _st;
    bool _running;
    DateTime _nextRun, _startedAt;

    public bool Enabled { get; private set; }
    public string Mode { get; private set; }            // hold | report
    public int IntervalMinutes { get; private set; }
    public int FirstDelayMinutes { get; private set; }
    public string SmtpHost { get; private set; }
    public int SmtpPort { get; private set; }
    public string MailFrom { get; private set; }
    public List<string> MailTo = new List<string>();
    public string DisabledReason { get; private set; }
    // Nur fuer Tests: Stand, Schreiben und Mail ersetzen (sonst DB ueber AcmpWumRings bzw. SMTP)
    public Func<AcmpWumRings.Snapshot> LoadHook;
    public Func<string, AcmpWumRings.Verdict, string, string, string> WriteHook;
    public Func<List<JObject>, bool> MailHook;

    public AcmpWumWatch(AcmpWumRings rings, JObject cfg, string cfgDir, AcmpActivity act, AcmpOpenItems open, string publicUrl, Action<string> log)
    {
        _rings = rings; _act = act; _open = open; _log = log; _publicUrl = (publicUrl ?? "").TrimEnd('/');
        _file = Path.Combine(cfgDir ?? ".", FileName);
        Mode = "hold"; IntervalMinutes = 60; FirstDelayMinutes = 3; SmtpPort = 25;
        if (cfg != null)
        {
            if (cfg["mode"] != null && cfg["mode"].Type == JTokenType.String)
            {
                string m = ((string)cfg["mode"]).Trim().ToLowerInvariant();
                Mode = m == "report" || m == "melden" ? "report" : "hold";
            }
            if (cfg["intervalMinutes"] != null) { try { IntervalMinutes = Math.Max(15, Math.Min(720, (int)cfg["intervalMinutes"])); } catch { } }
            if (cfg["firstDelayMinutes"] != null) { try { FirstDelayMinutes = Math.Max(1, Math.Min(60, (int)cfg["firstDelayMinutes"])); } catch { } }
            JObject mail = cfg["mail"] as JObject;
            if (mail != null)
            {
                SmtpHost = mail["smtpHost"] != null ? ((string)mail["smtpHost"] ?? "").Trim() : null;
                if (mail["port"] != null) { try { SmtpPort = (int)mail["port"]; } catch { } }
                MailFrom = mail["from"] != null ? ((string)mail["from"] ?? "").Trim() : null;
                JArray to = mail["to"] as JArray;
                if (to != null) foreach (JToken t in to) if (t.Type == JTokenType.String && ((string)t).Contains("@")) MailTo.Add(((string)t).Trim());
            }
        }
        if (cfg == null || cfg["enabled"] == null || cfg["enabled"].Type != JTokenType.Boolean || !(bool)cfg["enabled"]) { DisabledReason = "wumWatch.enabled ist nicht true"; return; }
        if (rings == null || (!rings.CanRead && cfg["_test"] == null)) { DisabledReason = "keine Datenbank-Anbindung"; return; }
        Enabled = true;
    }

    public bool MailOn { get { return MailHook != null || (!string.IsNullOrEmpty(SmtpHost) && !string.IsNullOrEmpty(MailFrom) && MailTo.Count > 0); } }
    public string MailText { get { return MailOn ? "an " + string.Join(", ", MailTo.ToArray()) + " über " + SmtpHost + ":" + SmtpPort : "aus (wumWatch.mail: smtpHost, from und to setzen)"; } }

    public void Start()
    {
        if (!Enabled) return;
        lock (_stLock) _st = LoadState();
        _startedAt = DateTime.Now;
        _nextRun = _startedAt.AddMinutes(FirstDelayMinutes);
        _timer = new Timer(delegate(object o) { Tick(); }, null, TimeSpan.FromMinutes(FirstDelayMinutes), TimeSpan.FromMinutes(IntervalMinutes));
        _log("WUM-Waechter: aktiv, Modus " + (Mode == "hold" ? "anhalten" : "nur melden") + ", alle " + IntervalMinutes + " min (erster Lauf in " + FirstDelayMinutes + " min), Mail " + MailText);
    }

    void Tick()
    {
        _nextRun = DateTime.Now.AddMinutes(IntervalMinutes);
        try { RunNow("Zeitplan"); }
        catch (Exception ex) { _log("WUM-Waechter: Lauf fehlgeschlagen: " + ex.Message); }
    }

    // ================= Zustand =================
    JObject LoadState()
    {
        foreach (string f in new[] { _file, _file + ".bak" })
        {
            try
            {
                if (!File.Exists(f)) continue;
                JObject o = JObject.Parse(File.ReadAllText(f, Encoding.UTF8));
                foreach (string k in new[] { "holds", "resumed", "events", "seen" }) if (!(o[k] is JObject)) o[k] = new JObject();
                if (f != _file) _log("WUM-Waechter: " + FileName + " war unlesbar - Zustand aus der Sicherung " + FileName + ".bak geladen");
                return o;
            }
            catch (Exception ex)
            {
                _log("WUM-Waechter: " + Path.GetFileName(f) + " nicht lesbar (" + ex.Message + "), bleibt als .kaputt liegen");
                try { File.Copy(f, f + ".kaputt", true); } catch { }
            }
        }
        var n = new JObject();
        n["version"] = 1; n["runs"] = 0;
        n["holds"] = new JObject(); n["resumed"] = new JObject(); n["events"] = new JObject(); n["seen"] = new JObject();
        if (File.Exists(_file) || File.Exists(_file + ".bak"))
            n["nurMelden"] = "Zustandsdatei und Sicherung waren unlesbar (" + DateTime.Now.ToString("dd.MM. HH:mm", CultureInfo.InvariantCulture) + ") - nur melden, bis ein Bediener auf der Seite Updates 'Jetzt pruefen' drueckt";
        return n;
    }

    void SaveState()
    {
        string text = _st.ToString(Formatting.Indented);
        string tmp = _file + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        JObject.Parse(File.ReadAllText(tmp, Encoding.UTF8));
        if (File.Exists(_file)) File.Replace(tmp, _file, _file + ".bak");
        else File.Move(tmp, _file);
    }

    JObject St { get { lock (_stLock) { if (_st == null) _st = LoadState(); return _st; } } }

    // Update-Id (ohne Klammern, gross) -> Regel, mit der der Waechter angehalten hat
    public IDictionary<string, string> HoldsByWatcher()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        lock (_stLock)
        {
            JObject h = St["holds"] as JObject;
            if (h != null) foreach (JProperty p in h.Properties()) { JObject o = p.Value as JObject; if (o != null && (string)o["by"] == Who) d[p.Name] = (string)o["rule"]; }
        }
        return d;
    }

    public bool IsHeldByWatcher(string bareId) { return HoldsByWatcher().ContainsKey(bareId); }

    // Grund eines Stopps (Waechter oder Mensch) fuer die Seite
    public JObject HoldInfo(string bareId)
    {
        lock (_stLock) { JObject h = St["holds"] as JObject; return h != null ? h[bareId] as JObject : null; }
    }

    // Von einem Menschen weitergelassen: Zeitpunkt und in Kauf genommene Fehlschlaege
    public AcmpWumRings.ResumeInfo ResumeOf(string bareId)
    {
        lock (_stLock)
        {
            JObject r = St["resumed"] as JObject;
            JObject o = r != null ? r[bareId] as JObject : null;
            if (o == null) return null;
            DateTime d;
            if (!DateTime.TryParse((string)o["at"], CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return null;
            var ri = new AcmpWumRings.ResumeInfo(); ri.At = d;
            JArray acc = o["accepted"] as JArray;
            if (acc != null) foreach (JToken t in acc) if (t.Type == JTokenType.String) ri.Accepted.Add((string)t);
            return ri;
        }
    }

    static string Now() { return DateTime.Now.ToString("s", CultureInfo.InvariantCulture); }

    static JArray AcceptedFrom(AcmpWumRings.Verdict v)
    {
        var a = new JArray();
        if (v != null)
            foreach (AcmpWumRings.TesterResult tr in v.Testers)
                if (tr.Kind == "fehler" || tr.Kind == "defekt" || tr.Kind == "unklar" || tr.Kind == "akzeptiert") a.Add(AcmpWumRings.AcceptKey(tr.ClientId, tr.ErrorKey));
        return a;
    }

    // Nach einem Schreiben ueber die Tools oder die Webseite (AcmpWumRings.OnWrite)
    public void NoteManual(string action, string bareId, string who, string comment, JArray accepted)
    {
        if (who == Who) return;   // eigene Schritte traegt der Lauf selbst ein
        bool close = false;
        lock (_stLock)
        {
            JObject h = (JObject)St["holds"], r = (JObject)St["resumed"], seen = (JObject)St["seen"];
            if (action == "hold")
            {
                var o = new JObject(); o["by"] = who; o["rule"] = "manuell"; o["reason"] = comment; o["at"] = Now(); h[bareId] = o;
                seen[bareId] = false;
            }
            else
            {
                h.Remove(bareId);
                var o = new JObject(); o["at"] = Now(); o["by"] = who; o["comment"] = comment; o["how"] = action;
                o["accepted"] = accepted != null ? (JArray)accepted.DeepClone() : new JArray();
                r[bareId] = o;
                seen[bareId] = true;
                close = true;
            }
            try { SaveState(); } catch (Exception ex) { _log("WUM-Waechter: Zustand nicht gespeichert: " + ex.Message); }
        }
        if (close)
        {
            string note = "Erledigt: " + (action == "now" ? "sofort freigegeben" : "weitergelassen") + " von " + who + (string.IsNullOrEmpty(comment) ? "" : " (" + comment + ")") + ".";
            CloseEvent("stopp|" + bareId, note);
            CloseEvent("s1|" + bareId, note);
            lock (_stLock) { try { SaveState(); } catch { } }
        }
    }

    // ================= Status =================
    public JObject Status()
    {
        var o = new JObject();
        o["aktiv"] = Enabled;
        if (!Enabled) { o["grund"] = DisabledReason; return o; }
        o["modus"] = Mode == "hold" ? "anhalten" : "nur melden";
        o["intervallMinuten"] = IntervalMinutes;
        o["mail"] = MailText;
        lock (_stLock)
        {
            JObject st = St;
            o["laeufe"] = st["runs"];
            o["letzterLauf"] = st["lastRun"]; o["letzterErfolg"] = st["lastOk"]; o["letzteDauerMs"] = st["lastMs"]; o["letzterFehler"] = st["lastError"];
            o["letzteZusammenfassung"] = st["lastSummary"];
            if (st["nurMelden"] != null) o["nurMelden"] = st["nurMelden"];
            int holds = 0; foreach (JProperty p in ((JObject)st["holds"]).Properties()) if ((string)((JObject)p.Value)["by"] == Who) holds++;
            o["angehaltenVomWaechter"] = holds;
            int open = 0; foreach (JProperty p in ((JObject)st["events"]).Properties()) if (((JObject)p.Value)["closed"] == null && ((JObject)p.Value)["baseline"] == null) open++;
            o["offeneMeldungen"] = open;
        }
        o["naechsterLauf"] = _nextRun == DateTime.MinValue ? null : (JToken)_nextRun.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        o["still"] = Stale;
        o["fehler"] = Failing;
        return o;
    }

    // Seit mehr als zwei Intervallen (+10 min) kein erfolgreicher Lauf - auch, wenn seit dem Start noch keiner geklappt hat
    public bool Stale
    {
        get
        {
            if (!Enabled) return false;
            TimeSpan limit = TimeSpan.FromMinutes(IntervalMinutes * 2 + 10);
            lock (_stLock)
            {
                DateTime d;
                if (DateTime.TryParse((string)St["lastOk"], CultureInfo.InvariantCulture, DateTimeStyles.None, out d) && (_startedAt == DateTime.MinValue || d >= _startedAt.AddDays(-30)))
                    return DateTime.Now - d > limit;
                return _startedAt != DateTime.MinValue && DateTime.Now - _startedAt > limit + TimeSpan.FromMinutes(FirstDelayMinutes);
            }
        }
    }

    // Der letzte Lauf ist gescheitert
    public bool Failing
    {
        get
        {
            lock (_stLock)
            {
                if (St["lastError"] == null || St["lastError"].Type == JTokenType.Null) return false;
                return string.CompareOrdinal((string)St["lastRun"] ?? "", (string)St["lastOk"] ?? "") > 0;
            }
        }
    }

    // ================= Ein Lauf =================
    public JObject RunNow(string trigger)
    {
        if (!Enabled) throw new Exception("WUM-Waechter ist aus: " + DisabledReason);
        lock (_runLock)
        {
            if (_running) throw new Exception("Der Waechter laeuft gerade.");
            _running = true;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var summary = new JObject();
        var notes = new List<string>();
        try
        {
            JObject st; bool firstRun;
            lock (_stLock)
            {
                st = St;
                firstRun = st["runs"] == null || (int)st["runs"] == 0;
                if (st["nurMelden"] != null && trigger != null && trigger.StartsWith("Webseite")) { notes.Add("Nur-melden nach kaputter Zustandsdatei aufgehoben (" + trigger + ")"); st.Remove("nurMelden"); }
            }
            AcmpWumRings.Snapshot snap = LoadHook != null ? LoadHook() : _rings.Load(0);
            AcmpWumRings.Evaluation ev = AcmpWumRings.Evaluate(snap, _rings.Opt, ResumeOf, HoldsByWatcher());
            bool writerOk = WriteHook != null || _rings.HoldAllowed;
            string degraded; lock (_stLock) degraded = (string)st["nurMelden"];
            Func<string> whyNot = delegate()
            {
                if (Mode != "hold") return "Modus nur melden";
                if (_act != null && _act.BrakeActive) return "Notbremse gezogen";
                if (degraded != null) return degraded;
                if (!writerOk) return _rings.ConfigOk ? "Rechte des Schreib-Logins fehlen oder Vorpruefung gesperrt (acmp_wum_rings writerStatus=true)" : "Ringe aendern aus (" + _rings.DisabledReason + ")";
                return null;
            };
            var byId = new Dictionary<string, AcmpWumRings.Verdict>(StringComparer.OrdinalIgnoreCase);
            Action index = delegate()
            {
                byId.Clear();
                foreach (AcmpWumRings.Verdict v in ev.Verdicts) byId[v.It.Id.Trim('{', '}')] = v;
                foreach (AcmpWumRings.Verdict v in ev.Released) byId[v.It.Id.Trim('{', '}')] = v;
            };
            index();
            var autoNow = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);   // Uebergang nach den eigenen Schritten dieses Laufs
            var holdResult = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int held = 0, resumed = 0, humanResumed = 0;

            // 1) Gemerkte Stopps: Mensch hat weitergelassen, freigegeben oder abgelehnt?
            List<string> holdIds;
            lock (_stLock) { holdIds = new List<string>(); foreach (JProperty p in ((JObject)st["holds"]).Properties()) holdIds.Add(p.Name); }
            foreach (string id in holdIds)
            {
                AcmpWumRings.Verdict v; byId.TryGetValue(id, out v);
                bool stillHeld = v != null && !v.It.Auto && v.It.Ring != AcmpWumRings.RingReleased && v.Status != "erledigt";
                if (stillHeld) continue;
                string how = v == null ? "nicht mehr im Testring" : v.It.Ring == AcmpWumRings.RingReleased ? "freigegeben" : v.Status == "erledigt" ? v.Text : "läuft wieder automatisch";
                lock (_stLock)
                {
                    ((JObject)st["holds"]).Remove(id);
                    if (v != null && v.It.Auto && v.It.Ring != AcmpWumRings.RingReleased) { NoteResume(st, id, v, "Console (vom Wächter bemerkt)"); humanResumed++; }
                }
                string note = "Erledigt: " + how + " (bemerkt " + DateTime.Now.ToString("dd.MM. HH:mm", CultureInfo.InvariantCulture) + ").";
                CloseEvent("stopp|" + id, note); CloseEvent("s1|" + id, note);
                notes.Add("Stopp beendet: " + id + " (" + how + ")");
            }
            // 1b) In der Console angehaltene Updates (nicht in der Liste), die jetzt wieder automatisch laufen
            foreach (AcmpWumRings.Verdict v in ev.Verdicts)
            {
                string id = v.It.Id.Trim('{', '}');
                if (!v.It.Auto || v.Status == "erledigt") continue;
                JToken prev; lock (_stLock) prev = ((JObject)st["seen"])[id];
                if (prev == null || prev.Type != JTokenType.Boolean || (bool)prev) continue;
                AcmpWumRings.ResumeInfo ri = ResumeOf(id);
                if (ri != null && DateTime.Now - ri.At < TimeSpan.FromMinutes(IntervalMinutes * 2 + 10)) continue;   // schon ueber MCP/Webseite gemerkt
                lock (_stLock) NoteResume(st, id, v, "Console (vom Wächter bemerkt)");
                humanResumed++;
                notes.Add("In der Console weitergelassen: " + AcmpWumRings.Short(v.It.Name));
            }
            // Wer von Hand weitergelassen hat, gilt ab jetzt: neu auswerten, damit die in Kauf genommenen Fehler nicht sofort wieder anhalten
            if (humanResumed > 0) { ev = AcmpWumRings.Evaluate(snap, _rings.Opt, ResumeOf, HoldsByWatcher()); index(); }

            // 2) Eigene S2-Stopps: scheitern sie inzwischen -> S1 (Mensch entscheidet); sind sie getestet -> weiterlaufen lassen
            foreach (AcmpWumRings.Verdict v in ev.Verdicts)
            {
                string id = v.It.Id.Trim('{', '}');
                if (v.UpgradeS1)
                {
                    lock (_stLock) { JObject h = ((JObject)st["holds"])[id] as JObject; if (h != null) { h["rule"] = "S1"; h["reason"] = v.Text; } }
                    continue;
                }
                if (!v.CanAutoResume) continue;
                string why = whyNot();
                if (why != null) { notes.Add("Nicht weitergelassen (" + why + "): " + AcmpWumRings.Short(v.It.Name)); continue; }
                string err = Write("acmp_wum_ring_release", v, "resume", "Waechter: inzwischen auf " + v.Ok + " Testrechner(n) installiert - laeuft automatisch weiter", snap);
                if (err != null) { notes.Add("Weiterlassen fehlgeschlagen: " + AcmpWumRings.Short(v.It.Name) + " - " + err); continue; }
                lock (_stLock) { ((JObject)st["holds"]).Remove(id); }
                autoNow[id] = true;
                CloseEvent("stopp|" + id, "Automatisch weitergelassen: inzwischen auf " + v.Ok + " Testrechner(n) installiert.");
                resumed++;
            }

            // 3) Neue Stopps (Notbremse vor jedem Schreiben pruefen)
            foreach (AcmpWumRings.Verdict v in ev.Verdicts)
            {
                if (!v.Hold || !v.It.Auto) continue;
                string id = v.It.Id.Trim('{', '}');
                string why = whyNot();
                if (why != null) { holdResult[id] = "NICHT angehalten (" + why + ")"; notes.Add("Haette angehalten (" + why + "): " + AcmpWumRings.Short(v.It.Name)); continue; }
                string err = Write("acmp_wum_ring_hold", v, null, "Waechter " + v.Rule + ": " + v.Text, snap);
                if (err != null) { holdResult[id] = "Anhalten FEHLGESCHLAGEN (" + err + ")"; notes.Add("Anhalten fehlgeschlagen: " + AcmpWumRings.Short(v.It.Name) + " - " + err); continue; }
                lock (_stLock)
                {
                    var o = new JObject(); o["by"] = Who; o["rule"] = v.Rule; o["reason"] = v.Text; o["at"] = Now(); o["name"] = v.It.Name;
                    ((JObject)st["holds"])[id] = o;
                }
                holdResult[id] = Held;
                autoNow[id] = false;
                held++;
            }

            // 4) Meldungen: neue anlegen, bekannte auffrischen (Aktion nachziehen; ein spaeter doch gelungener Stopp wird nachgemeldet)
            var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int newEvents = 0;
            foreach (AcmpWumRings.Finding f in ev.Findings)
            {
                current.Add(f.Key);
                string action = null;
                if (f.Key.StartsWith("stopp|")) { string r; action = holdResult.TryGetValue(f.UpdateId ?? "", out r) ? r : null; }
                else if (f.Key.StartsWith("s1|")) action = "bleibt angehalten, ein Mensch entscheidet";
                JObject e;
                lock (_stLock) e = ((JObject)st["events"])[f.Key] as JObject;
                if (e != null && e["closed"] == null)
                {
                    lock (_stLock)
                    {
                        e["lastSeen"] = Now(); e["misses"] = 0; e["text"] = f.Text; e["title"] = f.Title;
                        if (action != null && (string)e["action"] != action)
                        {
                            bool nowHeld = action == Held && (string)e["action"] != Held;
                            e["action"] = action;
                            if (nowHeld && e["baseline"] == null) e["mailed"] = null;   // "doch angehalten" nachmelden
                        }
                    }
                    continue;
                }
                e = new JObject();
                e["rule"] = f.Rule; e["severity"] = f.Severity; e["title"] = f.Title; e["text"] = f.Text; e["first"] = Now(); e["lastSeen"] = Now(); e["misses"] = 0;
                e["mail"] = f.Mail;
                if (f.UpdateId != null) e["updateId"] = f.UpdateId;
                if (f.ClientId != null) e["clientId"] = f.ClientId;
                if (action != null) e["action"] = action;
                bool isStop = f.Key.StartsWith("stopp|") || f.Key.StartsWith("s1|");
                if (firstRun && !isStop) e["baseline"] = true;   // war schon vor dem ersten Lauf so
                else
                {
                    newEvents++;
                    if (f.OpenItem) e["openItem"] = OpenItem(f, e);
                }
                lock (_stLock) ((JObject)st["events"])[f.Key] = e;
            }

            // 5) Erledigte Meldungen schliessen: Stopps, die noch angehalten sind, bleiben offen (Schritt 1/2 schliessen sie);
            //    alles andere (auch Stopp-Meldungen im Modus "nur melden") nach zwei Laeufen ohne Befund
            var toClose = new List<string>();
            lock (_stLock)
                foreach (JProperty p in ((JObject)st["events"]).Properties())
                {
                    JObject e = (JObject)p.Value;
                    if (e["closed"] != null || current.Contains(p.Name)) continue;
                    string uid = (string)e["updateId"];
                    if ((p.Name.StartsWith("stopp|") || p.Name.StartsWith("s1|")) && uid != null && ((JObject)st["holds"])[uid] != null) continue;
                    int m = e["misses"] != null ? (int)e["misses"] + 1 : 1;
                    e["misses"] = m;
                    if (m >= 2) toClose.Add(p.Name);
                }
            foreach (string k in toClose) CloseEvent(k, "Erledigt: seit zwei Läufen kein Befund mehr.");
            PruneEvents();

            // 6) Eine Mail mit allen offenen, noch nicht gemailten Meldungen (auch die, deren Versand zuletzt scheiterte)
            var pending = new List<JObject>();
            lock (_stLock)
                foreach (JProperty p in ((JObject)st["events"]).Properties())
                {
                    JObject e = (JObject)p.Value;
                    if (e["closed"] == null && e["baseline"] == null && e["mail"] != null && e["mail"].Type == JTokenType.Boolean && (bool)e["mail"] &&
                        (e["mailed"] == null || e["mailed"].Type == JTokenType.Null)) pending.Add(e);
                }
            if (pending.Count > 0) notes.Add(MailOn ? SendMail(pending) : "keine Mail (" + MailText + ")");

            // 7) Uebergang je Update merken (fuer "in der Console weitergelassen")
            lock (_stLock)
            {
                var seen = new JObject();
                foreach (AcmpWumRings.Verdict v in ev.Verdicts)
                {
                    if (v.Status == "erledigt") continue;
                    string id = v.It.Id.Trim('{', '}');
                    bool a; seen[id] = autoNow.TryGetValue(id, out a) ? a : v.It.Auto;
                }
                st["seen"] = seen;
            }

            summary["zeit"] = Now(); summary["anlass"] = trigger;
            summary["imTestring"] = ev.Verdicts.Count; summary["angehalten"] = held; summary["weitergelassen"] = resumed; summary["vonHandWeiter"] = humanResumed;
            summary["neueMeldungen"] = newEvents; summary["mailOffen"] = pending.Count; summary["meldungen"] = ev.Findings.Count;
            if (notes.Count > 0) summary["hinweise"] = new JArray(notes.ToArray());
            lock (_stLock)
            {
                st["runs"] = (st["runs"] != null ? (int)st["runs"] : 0) + 1;
                st["lastRun"] = Now(); st["lastOk"] = Now(); st["lastMs"] = sw.ElapsedMilliseconds; st["lastError"] = null; st["lastSummary"] = summary;
                SaveState();
            }
            _log("WUM-Waechter (" + trigger + "): " + ev.Verdicts.Count + " im Testring, " + held + " angehalten, " + resumed + " automatisch weiter, " + humanResumed + " von Hand weiter, " +
                 pending.Count + " Meldung(en) zu mailen" + (notes.Count > 0 ? " | " + string.Join(" | ", notes.ToArray()) : "") + " (" + sw.ElapsedMilliseconds + " ms)");
            return summary;
        }
        catch (Exception ex)
        {
            lock (_stLock)
            {
                St["lastRun"] = Now(); St["lastError"] = ex.Message; St["lastMs"] = sw.ElapsedMilliseconds;
                try { SaveState(); } catch { }
            }
            _log("WUM-Waechter (" + trigger + "): FEHLER " + ex.Message);
            throw;
        }
        finally { lock (_runLock) _running = false; }
    }

    // Weiterlassen durch einen Menschen merken (unter _stLock aufrufen)
    void NoteResume(JObject st, string id, AcmpWumRings.Verdict v, string by)
    {
        var r = new JObject(); r["at"] = Now(); r["by"] = by; r["how"] = "resume"; r["accepted"] = AcceptedFrom(v);
        ((JObject)st["resumed"])[id] = r;
    }

    // Anhalten bzw. weiterlaufen lassen als Waechter, mit Start- und Abschlusszeile im Aktivitaetsprotokoll; Rueckgabe null = ok, sonst Fehlertext
    string Write(string tool, AcmpWumRings.Verdict v, string mode, string reason, AcmpWumRings.Snapshot snap)
    {
        if (_act != null && _act.BrakeActive) return "Notbremse gezogen";
        if (WriteHook != null) return WriteHook(tool, v, mode, reason);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string target = AcmpWumRings.Short(v.It.Name) + " " + v.It.Id;
        string aid = null;
        if (_act != null)
        {
            try { aid = _act.Begin(Who, tool, "Write", reason, target, null); }
            catch (Exception ex) { return "Aktivitaetsprotokoll nicht schreibbar - nichts geschrieben (" + ex.Message + ")"; }
        }
        try
        {
            var a = new JObject(); a["updateId"] = v.It.Id; a["comment"] = reason.Length > AcmpWumRings.MaxCommentLength ? reason.Substring(0, AcmpWumRings.MaxCommentLength) : reason;
            if (mode != null) a["mode"] = mode;
            JObject plan = _rings.Plan(tool, a, Who, snap);
            var p = new JObject(); p["_plan"] = plan;
            JObject res = _rings.Execute(tool, p, Who, true);
            bool ok = res["ok"] == null || res["ok"].Type != JTokenType.Boolean || (bool)res["ok"];
            if (_act != null && aid != null) _act.End(aid, Who, tool, "Write", reason, target, null, ok, res.ToString(Formatting.None), sw.ElapsedMilliseconds);
            return ok ? null : "geschrieben, aber die Kontrolle weicht ab";
        }
        catch (Exception ex)
        {
            if (_act != null && aid != null) _act.End(aid, Who, tool, "Write", reason, target, null, false, ex.Message, sw.ElapsedMilliseconds);
            return ex.Message;
        }
    }

    // ================= Offene Punkte =================
    string OpenItem(AcmpWumRings.Finding f, JObject e)
    {
        if (_open == null) return null;
        try
        {
            var a = new JObject();
            a["action"] = "anlegen";
            a["titel"] = f.Title.Length > 200 ? f.Title.Substring(0, 199) + "…" : f.Title;
            string how = f.Rule == "S1" || f.Rule == "S2"
                ? " Entscheiden: auf der Seite Updates im Dashboard (weiterlaufen lassen, sofort freigeben) oder per MCP acmp_wum_ring_release; ablehnen mit acmp_wum_decline. Der Punkt schließt sich, sobald das Update weiterläuft, freigegeben oder abgelehnt ist."
                : f.Rule == "nach" ? " Ablehnen erwägen (acmp_wum_decline). Der Punkt schließt sich, wenn die Fehlschläge verschwinden."
                : " Der Punkt schließt sich, wenn der Befund zwei Läufe lang weg ist.";
            string d = f.Text + (e["action"] != null ? " Wächter: " + (string)e["action"] + "." : "") + how;
            a["details"] = d.Length > 2000 ? d.Substring(0, 1999) + "…" : d;
            a["bereich"] = "Updates";
            a["prio"] = f.Severity == "rot" ? "hoch" : "normal";
            a["link"] = "/dashboard/updates";
            a["sitzung"] = "WUM-Wächter";
            JObject r = _open.Set(a, Who, "Waechter");
            JObject pt = r["punkt"] as JObject;
            return pt != null ? (string)pt["id"] : null;
        }
        catch (Exception ex) { _log("WUM-Waechter: offener Punkt nicht angelegt: " + ex.Message); return null; }
    }

    void CloseEvent(string key, string note)
    {
        string itemId = null;
        lock (_stLock)
        {
            JObject e = ((JObject)St["events"])[key] as JObject;
            if (e == null || e["closed"] != null) return;
            e["closed"] = Now(); e["closeNote"] = note;
            itemId = (string)e["openItem"];
        }
        if (itemId == null || _open == null) return;
        try
        {
            var a = new JObject(); a["action"] = "erledigen"; a["id"] = itemId; a["notiz"] = note.Length > 500 ? note.Substring(0, 499) + "…" : note;
            _open.Set(a, Who, "Waechter");
        }
        catch (Exception ex) { _log("WUM-Waechter: offener Punkt " + itemId + " nicht geschlossen (" + ex.Message + ")"); }
    }

    // Geschlossene Meldungen nach 30 Tagen vergessen; bei mehr als MaxEvents die aeltesten GESCHLOSSENEN (offene bleiben immer)
    void PruneEvents()
    {
        lock (_stLock)
        {
            JObject ev = (JObject)St["events"];
            var drop = new List<string>();
            string limit = DateTime.Now.AddDays(-30).ToString("s", CultureInfo.InvariantCulture);
            foreach (JProperty p in ev.Properties()) { string c = (string)((JObject)p.Value)["closed"]; if (c != null && string.CompareOrdinal(c, limit) < 0) drop.Add(p.Name); }
            foreach (string k in drop) ev.Remove(k);
            if (ev.Count > MaxEvents)
            {
                var closed = new List<JProperty>();
                foreach (JProperty p in ev.Properties()) if (((JObject)p.Value)["closed"] != null) closed.Add(p);
                closed.Sort(delegate(JProperty a, JProperty b) { return string.CompareOrdinal((string)((JObject)a.Value)["closed"], (string)((JObject)b.Value)["closed"]); });
                for (int i = 0; i < closed.Count && ev.Count > MaxEvents; i++) ev.Remove(closed[i].Name);
            }
            JObject res = (JObject)St["resumed"];
            var rd = new List<string>();
            string rl = DateTime.Now.AddDays(-60).ToString("s", CultureInfo.InvariantCulture);
            foreach (JProperty p in res.Properties()) { JObject o = p.Value as JObject; string at = o != null ? (string)o["at"] : null; if (at == null || string.CompareOrdinal(at, rl) < 0) rd.Add(p.Name); }
            foreach (string k in rd) res.Remove(k);
        }
    }

    // Aktuelle Meldungen fuer Seite und Tool (offen, neueste zuerst)
    public List<JObject> OpenEvents()
    {
        var l = new List<JObject>();
        lock (_stLock) foreach (JProperty p in ((JObject)St["events"]).Properties()) { JObject e = (JObject)p.Value; if (e["closed"] == null) { var c = (JObject)e.DeepClone(); c["key"] = p.Name; l.Add(c); } }
        l.Sort(delegate(JObject a, JObject b) { return string.CompareOrdinal((string)b["first"], (string)a["first"]); });
        return l;
    }

    // ================= Mail =================
    // Gibt einen Text fuer Protokoll/Seite zurueck; "mailed" wird nur bei Erfolg gesetzt
    string SendMail(List<JObject> events)
    {
        try
        {
            if (MailHook != null)
            {
                if (!MailHook(events)) throw new Exception("Testversand abgelehnt");
            }
            else
            {
                int stopsHeld = 0, stopsNot = 0;
                foreach (JObject e in events)
                {
                    string r = (string)e["rule"];
                    if (r != "S1" && r != "S2") continue;
                    if ((string)e["action"] == Held || ((string)e["action"] ?? "").StartsWith("bleibt angehalten")) stopsHeld++; else stopsNot++;
                }
                var parts = new List<string>();
                if (stopsHeld > 0) parts.Add(stopsHeld + " Update(s) angehalten");
                if (stopsNot > 0) parts.Add(stopsNot + " Update(s) sollten angehalten werden");
                int other = events.Count - stopsHeld - stopsNot;
                if (other > 0) parts.Add(other + (parts.Count > 0 ? " weitere" : "") + " Meldung(en)");
                string subject = "[ACMP Updates] " + string.Join(", ", parts.ToArray());
                var sb = new StringBuilder();
                sb.Append("Der Update-Wächter des ACMP MCP meldet ").Append(events.Count).Append(events.Count == 1 ? " Befund" : " Befunde").Append(" (").Append(DateTime.Now.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)).Append("):\r\n\r\n");
                int i = 0;
                foreach (JObject e in events)
                {
                    sb.Append(++i).Append(". ").Append((string)e["title"]).Append("\r\n   ").Append((string)e["text"]).Append("\r\n");
                    if (e["action"] != null) sb.Append("   Wächter: ").Append((string)e["action"]).Append("\r\n");
                    sb.Append("\r\n");
                }
                sb.Append("Entscheiden und Einzelheiten: ").Append(_publicUrl).Append("/dashboard/updates\r\n");
                sb.Append("Per Claude/MCP: acmp_wum_rings (Stand), acmp_wum_ring_release (weiterlaufen lassen oder sofort freigeben), acmp_wum_decline (ablehnen).\r\n\r\n");
                sb.Append("Gemeldet werden nur Stopps, Rechnerdefekte und Fehler nach der Freigabe – vorübergehende Fehler (Office offen, Neustart, Download) nicht.\r\n");
                sb.Append("Regeln: docs/USAGE.md des ACMP MCP, Abschnitt „Fehler-Stopp der Windows-Updates“.\r\n");
                using (var m = new MailMessage())
                {
                    m.From = new MailAddress(MailFrom, "ACMP Update-Wächter");
                    foreach (string t in MailTo) m.To.Add(t);
                    m.Subject = subject; m.SubjectEncoding = Encoding.UTF8;
                    m.Body = sb.ToString(); m.BodyEncoding = Encoding.UTF8; m.IsBodyHtml = false;
                    using (var c = new SmtpClient(SmtpHost, SmtpPort)) { c.EnableSsl = false; c.Timeout = 30000; c.Send(m); }
                }
            }
            lock (_stLock) { foreach (JObject e in events) e["mailed"] = Now(); }
            return "Mail an " + (MailTo.Count > 0 ? string.Join(", ", MailTo.ToArray()) : "Test") + " gesendet (" + events.Count + " Meldung(en))";
        }
        catch (Exception ex) { _log("WUM-Waechter: Mail fehlgeschlagen: " + ex.Message); return "Mail fehlgeschlagen (naechster Lauf versucht es wieder): " + ex.Message; }
    }

    // Testmail (Webseite): prueft Relay und Empfaenger
    public string TestMail(string who)
    {
        if (!MailOn) return "Mail ist aus: " + MailText;
        var e = new JObject(); e["title"] = "Testmail des Update-Wächters"; e["text"] = "Ausgelöst von " + who + ". Wenn diese Mail ankommt, erreichen die Meldungen des Wächters die IT."; e["rule"] = "test";
        return SendMail(new List<JObject> { e });
    }
}
