// AcmpWumRings: Verteilungsringe der Windows-Updates (CAWUM) - acmp_wum_rings (lesen), acmp_wum_ring_hold (Uebergang anhalten),
// acmp_wum_ring_release (weiterlaufen lassen oder sofort freigeben) - und die Regeln des Fehler-Stopps (seit 23.10.2026;
// Beschreibung in docs/USAGE.md).
// Grundlage: SQL-Mitschnitt der Console 6.10.4 am 23.10.2026 (an einem Treiber-Update),
// geschrieben hat jeweils der ACMP-Server (ACMPServer.exe), nicht die Console:
//  - "Automatischen Uebergangsmodus deaktivieren/aktivieren" = UPDATE SYS_TestRings SET TransitionMode = 0/1 WHERE Id IN (...). Mehr nicht,
//    LastChangeDate bleibt. Ein Update mit TransitionMode 0 bleibt im Ring (belegt an einem Feature-Update, das so wochenlang in Ring 1 blieb).
//  - "Verteilungsring wechseln" -> Freigegeben, Haken "Elemente automatisch in den naechsten Verteilungsring verschieben" =
//    UPDATE SYS_TestRings SET ItemState = <bisher>, TransitionMode = 1, ItemKind = 21, LastChangeDate = jetzt (OLE, Ortszeit), TestRing = 255,
//    danach Flags neu (UPDATE Sys_Wum_Updates ... FROM VW_Wum_Updates_Flags = ap_Wum_Rebuild_UpdateFlags). Das Tool schreibt nur die
//    Spalten, die sich aendern (TestRing, TransitionMode, LastChangeDate).
// Ringe: 0 = noch in keinem Ring (nicht heruntergeladen), 1/2 = Testring, 255 = freigegeben. Die Verweildauer je Ring steht in der
// Ring-Konfiguration (SYS_Config -> WumConfig/TestRingCollections, Name = SYS_TestRings.CollectionName); der Server-Job
// "Auf Elemente mit neuem Verteilungsring pruefen" schiebt stuendlich weiter. Ein WUM-Job hat einen Ring (SYS_Jobs_Logs.TestRing):
// ein Job mit Ring 1 verteilt Updates ab Ring 1, ein Job mit 255 nur freigegebene. Testrechner = Mitglieder von Containern mit
// eingeschaltetem WUM-Job, dessen Ring < 255 ist und dessen Sammlung das Update enthaelt.
// Zustand je Testrechner und Update: CLT_Wum_InstalledUpdates / _NotInstalledUpdates / _FailedUpdates (ReportDate = UTC, Scan-Zeit).
// Fehlertexte stehen in SYS_Jobs_Logs_SubItems (JobKind 58, Result 2); ContentId ist dort nur bei manchen Zeilen die Update-Id, sonst
// wird der Text ueber die Zeit zugeordnet. SYS_Config wird nur im Knoten WumConfig gelesen (dort stehen sonst auch Kennwoerter).
// Regeln (Evaluate, offline getestet in WumRingTest.cs):
//  S1 Fehler      - ein Testrechner meldet das Update seit Eintritt in den Ring (bzw. seit dem letzten Weiterlassen) als fehlgeschlagen,
//                   der Fehler ist nicht voruebergehend, und das Update ist dort nicht installiert -> anhalten.
//                   Scheitert es nur auf Rechnern mit Rechnerdefekt und hat kein Testrechner es geschafft -> ebenfalls anhalten.
//  S2 ungetestet  - weniger als hoursBeforeDue Stunden bis zur Weitergabe, mindestens ein Testrechner braucht es, keiner hat es installiert.
//                   Ein vom Waechter mit S2 angehaltenes Update laeuft automatisch weiter, sobald ein Testrechner es ohne festen Fehler hat.
//  haengt         - voruebergehender Fehler auf einem Testrechner seit stuckDays Tagen, nicht installiert (nur Meldung).
//  defekt         - ein Rechner meldet in defectDays Tagen mindestens defectMinUpdates verschiedene fehlgeschlagene Updates mit festem oder
//                   unbekanntem Fehler (eine Meldung je Rechner, kein Stopp).
//  nach Freigabe  - binnen postReleaseDays nach der Freigabe scheitert ein Update auf mindestens postReleaseMinClients Rechnern mit
//                   demselben festen/unbekannten Fehler (Meldung, Ablehnen erwaegen).
// Voruebergehend (keine Meldung): Office offen, Herunterfahren (0x8024001E), Download/Netz, abgebrochen - Liste wumWatch.transientPatterns.
// net48 / C# 5 (csc v4.0.30319): keine string-Interpolation, keine expression-bodied member, kein ?.
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

class AcmpWumRings
{
    public const int ItemKind = 21;               // SYS_TestRings.ItemKind der Windows-Updates
    public const int RingReleased = 255;
    public const int MaxIds = 10;
    public const int MinCommentLength = 8;
    public const int MaxCommentLength = 500;
    public static readonly string[] TestedVersions = { "6.10" };
    public static readonly string[] KnownTriggers = { };   // auf SYS_TestRings keine Trigger (Pruefung Unix-Writer 22.10.2026)
    public const string ColsRings = "Id:uniqueidentifier,TestRing:int,ItemKind:int,LastChangeDate:float,TransitionMode:tinyint,ItemState:tinyint,CollectionName:nvarchar";
    public const string Waechter = "wum-waechter";

    public static readonly string IdsHelp =
        "Liste von Update-Ids (GUID), 1-" + MaxIds + ", z.B. [\"{A1B2C3D4-E5F6-4711-8899-AABBCCDDEEFF}\"]. Die Ids stehen in acmp_wum_rings, acmp_wum_declined (scope=open) und acmp_failed_updates.";

    public static bool IsWriteTool(string name) { return name == "acmp_wum_ring_hold" || name == "acmp_wum_ring_release"; }

    readonly string _readCs, _writeCs, _backupDir;
    readonly HashSet<string> _allowedReq = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    readonly Action<string> _log;
    readonly Func<string> _acmpVersion;
    readonly bool _allowUntested;
    JObject _preflight; DateTime _preflightAt; string _lastVersion;
    readonly object _pfLock = new object();
    static readonly object _wLock = new object();
    const int PreflightMaxAgeMinutes = 60;
    public Options Opt = new Options();
    // Nach jedem Schreiben: (aktion hold|resume|now, updateId ohne Klammern, wer, kommentar, in Kauf genommene Fehlschlaege) - der Waechter
    // merkt sich Grund, Zeitpunkt und Fehlschlaege
    public Action<string, string, string, string, JArray> OnWrite;
    // Von einem Menschen weitergelassen (null = nie): Zeitpunkt und in Kauf genommene Fehlschlaege - vom Waechter, fuer S1/S2
    public Func<string, ResumeInfo> ResumeOf;
    // Haelt der Waechter dieses Update an (Id ohne Klammern)? Fuer "Stopp uebernehmen" durch einen Menschen
    public Func<string, bool> HeldByWatcher;
    // Notbremse gezogen? Execute schreibt dann nichts (zusaetzlich zur Pruefung in CallTool, auf der Webseite und im Waechter)
    public Func<bool> BrakeActive;

    public bool ConfigOk { get; private set; }
    public string DisabledReason { get; private set; }
    public bool CanRead { get { return !string.IsNullOrEmpty(_readCs); } }
    public ICollection<string> AllowedRequesters { get { return _allowedReq; } }

    public AcmpWumRings(string readCs, JObject cfg, JObject ccCfg, JObject watchCfg, string cfgDir, Action<string> log, Func<string> acmpVersion)
    {
        _readCs = readCs; _log = log; _acmpVersion = acmpVersion;
        Opt = Options.From(watchCfg);
        string bd = cfg != null && cfg["backupDir"] != null ? (string)cfg["backupDir"] : "wum-backups";
        _backupDir = Path.IsPathRooted(bd) ? bd : Path.Combine(cfgDir ?? ".", bd);
        _allowUntested = cfg != null && cfg["allowUntested"] != null && (bool)cfg["allowUntested"];
        if (cfg == null || cfg["enabled"] == null || !(bool)cfg["enabled"]) { DisabledReason = "wumWriter.enabled ist nicht true (nur Lesen und dryRun)"; return; }
        _writeCs = cfg["connectionString"] != null ? (string)cfg["connectionString"] : (ccCfg != null ? (string)ccCfg["connectionString"] : null);
        JArray ar = (cfg["allowedRequesters"] as JArray) ?? (ccCfg != null ? ccCfg["allowedRequesters"] as JArray : null);
        if (ar != null) foreach (JToken t in ar) _allowedReq.Add((string)t);
        if (string.IsNullOrEmpty(_readCs)) { DisabledReason = "db.connectionString fehlt"; return; }
        if (string.IsNullOrEmpty(_writeCs) || _writeCs.StartsWith("HIER_")) { DisabledReason = "kein Schreib-Login (wumWriter.connectionString oder ccWriter.connectionString)"; return; }
        if (_allowedReq.Count == 0) { DisabledReason = "keine erlaubten Antragsteller (wumWriter.allowedRequesters oder ccWriter.allowedRequesters)"; return; }
        ConfigOk = true;
    }

    // ================= Einstellungen der Regeln (Config-Section wumWatch) =================
    public class Options
    {
        public double HoursBeforeDue = 24;
        public int StuckDays = 3;
        public int PostReleaseDays = 7;
        public int PostReleaseMinClients = 3;
        public int DefectDays = 14;
        public int DefectMinUpdates = 3;
        public int ErrorDays = 14;
        public List<string> Transient = new List<string>(DefaultTransient);
        public List<string> ExcludeClassifications = new List<string>(new[] { "Definition Updates" });

        public static readonly string[] DefaultTransient = {
            "Blocked because of running applications", "Failed to lock package", "heruntergefahren", "shutting down", "8024001E",
            "Download failed", "8024001F", "80072EE2", "80072EE7", "80072EFD", "80072EFE", "80072F8F", "8024401C", "8024402C", "80244022",
            "80240438", "abgebrochen", "canceled", "cancelled", "80240016" };

        public static Options From(JObject c)
        {
            var o = new Options();
            if (c == null) return o;
            o.HoursBeforeDue = Num(c, "hoursBeforeDue", o.HoursBeforeDue, 1, 168);
            o.StuckDays = (int)Num(c, "stuckDays", o.StuckDays, 1, 30);
            o.PostReleaseDays = (int)Num(c, "postReleaseDays", o.PostReleaseDays, 1, 30);
            o.PostReleaseMinClients = (int)Num(c, "postReleaseMinClients", o.PostReleaseMinClients, 1, 100);
            o.DefectDays = (int)Num(c, "defectDays", o.DefectDays, 1, 60);
            o.DefectMinUpdates = (int)Num(c, "defectMinUpdates", o.DefectMinUpdates, 2, 50);
            JArray tp = c["transientPatterns"] as JArray;
            if (tp != null) { o.Transient.Clear(); foreach (JToken t in tp) if (t.Type == JTokenType.String && ((string)t).Trim().Length > 0) o.Transient.Add(((string)t).Trim()); }
            JArray ex = c["excludeClassifications"] as JArray;
            if (ex != null) { o.ExcludeClassifications.Clear(); foreach (JToken t in ex) if (t.Type == JTokenType.String) o.ExcludeClassifications.Add(((string)t).Trim()); }
            return o;
        }

        static double Num(JObject c, string k, double def, double min, double max)
        {
            if (c[k] == null || c[k].Type == JTokenType.Null) return def;
            double v;
            try { v = (double)c[k]; } catch { return def; }
            return Math.Max(min, Math.Min(max, v));
        }
    }

    // ================= Datenmodell (offline testbar) =================
    public class RingConfig
    {
        public string Name; public int Priority;
        public double[] Days = new double[3];
        public bool[] Auto = { true, true, true };
    }

    public class Item
    {
        public string Id, Name, Classification, Products, Config;
        public int? Kb, Severity;
        public int Ring, ItemState;
        public bool Auto = true, Superseded, Expired, Revoked, Declined;
        public DateTime LastChange;            // Ortszeit
        public double LastChangeRaw;           // OLE-Wert wie in der DB (fuer den Plan-Vergleich)
        public int Requiring, Installed, Failed;
    }

    public class Tester
    {
        public string ClientId, Name, JobId, JobName, ContainerPath, CollectionId, JobCondition;
        public int JobRing;
        public bool JobActive;
    }

    public class State { public string ClientId, UpdateId; public char Kind; public DateTime When; }   // i installiert, n benoetigt, f fehlgeschlagen; When Ortszeit

    public class Err { public string ClientId, Computer, UpdateId, Text, Title; public DateTime When; public int Ring, Code; }

    public class Snapshot
    {
        public DateTime Now;
        public List<RingConfig> Configs = new List<RingConfig>();
        public List<Item> Items = new List<Item>();
        public List<Tester> Testers = new List<Tester>();
        public List<State> States = new List<State>();
        public List<Err> Errors = new List<Err>();                 // Unterergebnisse mit Result 2 (Fehlertexte)
        public List<State> FleetFails = new List<State>();         // CLT_Wum_FailedUpdates der letzten Tage, alle Rechner
        public HashSet<string> Links = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // "collectionId|updateId"
        public Dictionary<string, string> ClientNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public double LoadMs;
    }

    // Kind: ok, offen, fehler, unklar (Fehlschlag ohne Fehlertext), defekt (Rechnerproblem), voruebergehend, akzeptiert (beim Weiterlassen in Kauf genommen)
    public class TesterResult { public string Name, ClientId, Kind, Error, ErrorClass, ErrorKey; public DateTime? When; }

    public class Verdict
    {
        public Item It;
        public string Status;                  // im-test | getestet | ungetestet | fehler | gestoppt | kein-testrechner | ausgenommen | erledigt | freigegeben
        public string Text;                    // lesbare Begruendung
        public DateTime? ReleaseAt;            // geplante Weitergabe nach Freigegeben (null = keine automatische)
        public string Rule;                    // S1 / S2, wenn angehalten werden soll
        public bool Hold;                      // Waechter soll anhalten
        public bool CanAutoResume;             // vom Waechter (S2) angehalten und jetzt getestet
        public bool UpgradeS1;                 // vom Waechter (S2) angehalten und scheitert jetzt -> Regel S1, ein Mensch entscheidet
        public List<TesterResult> Testers = new List<TesterResult>();
        public int Ok, Hard, Soft, Pending, HardDefectOnly, Unclear, Accepted;
    }

    public class Finding
    {
        public string Key, Rule, Severity, Title, Text, UpdateId, ClientId;
        public bool Mail;
        public bool OpenItem = true;           // als offener Punkt anlegen (haengt: nur auf der Seite)
    }

    public class Evaluation
    {
        public DateTime Now;
        public List<Verdict> Verdicts = new List<Verdict>();
        public List<Verdict> Released = new List<Verdict>();
        public List<Finding> Findings = new List<Finding>();
        public Dictionary<string, string> Defective = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // ClientId -> Fehler
        public List<Tester> Testers = new List<Tester>();
        public List<RingConfig> Configs = new List<RingConfig>();
    }

    // ================= Hilfen ohne DB =================
    public static string GuidBraces(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        Guid g; if (!Guid.TryParse(s.Trim().Trim('{', '}'), out g)) return null;
        return "{" + g.ToString().ToUpperInvariant() + "}";
    }

    static Guid G(string s) { return new Guid(s.Trim('{', '}')); }
    static string Bare(string s) { return s == null ? null : s.Trim('{', '}').ToUpperInvariant(); }

    public static string RingText(int r) { return r == RingReleased ? "Freigegeben" : r == 0 ? "Kein Ring" : "Testring " + r; }

    public static string OleText(DateTime d) { return d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture); }

    public static string DeText(DateTime d) { return d.ToString("dd.MM. HH:mm", CultureInfo.InvariantCulture); }

    // Ring-Konfigurationen aus dem XML-Knoten TestRingCollections (SYS_Config -> WumConfig)
    public static List<RingConfig> ParseConfigs(string xml)
    {
        var l = new List<RingConfig>();
        if (string.IsNullOrEmpty(xml) || xml.Trim().Length == 0) return l;
        var doc = new XmlDocument();
        doc.LoadXml("<x>" + xml + "</x>");
        foreach (XmlNode n in doc.GetElementsByTagName("TWumTestRingConfigCollection"))
        {
            var c = new RingConfig();
            XmlElement e = (XmlElement)n;
            c.Name = Txt(e, "Name") ?? "";
            int p; c.Priority = int.TryParse(Txt(e, "Priority"), NumberStyles.Integer, CultureInfo.InvariantCulture, out p) ? p : 99;
            foreach (XmlNode rn in e.GetElementsByTagName("TTestRingConfig"))
            {
                XmlElement r = (XmlElement)rn;
                int ring; if (!int.TryParse(Txt(r, "TestRing"), NumberStyles.Integer, CultureInfo.InvariantCulture, out ring) || ring < 0 || ring > 2) continue;
                double d; if (double.TryParse(Txt(r, "DurationOfStay"), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) c.Days[ring] = d;
                c.Auto[ring] = !string.Equals(Txt(r, "TransitionMode"), "tmManual", StringComparison.OrdinalIgnoreCase);
            }
            l.Add(c);
        }
        l.Sort(delegate(RingConfig a, RingConfig b) { return a.Priority.CompareTo(b.Priority); });
        return l;
    }

    static string Txt(XmlElement e, string name)
    {
        foreach (XmlNode c in e.ChildNodes) if (c.NodeType == XmlNodeType.Element && c.LocalName == name) return c.InnerText;
        return null;
    }

    public static RingConfig FindConfig(List<RingConfig> l, string name)
    {
        foreach (RingConfig c in l) if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
        foreach (RingConfig c in l) if (string.Equals(c.Name, "Default", StringComparison.OrdinalIgnoreCase)) return c;
        return null;
    }

    // Geplante automatische Weitergabe nach Freigegeben (null = keine, weil manuell oder Konfiguration unbekannt)
    public static DateTime? ReleaseAt(Item it, RingConfig c)
    {
        if (!it.Auto || c == null || it.Ring < 0 || it.Ring > 2) return null;
        double days = 0;
        for (int r = it.Ring; r <= 2; r++) { if (!c.Auto[r]) return null; days += c.Days[r]; }
        return it.LastChange.AddDays(days);
    }

    // Wann kam das Update in den ersten Testring (fuer Ring 2 geschaetzt)
    public static DateTime RingEntry(Item it, RingConfig c)
    {
        if (it.Ring == 2 && c != null) return it.LastChange.AddDays(-c.Days[1]);
        return it.LastChange;
    }

    // Fehlertext -> voruebergehend | fest | unbekannt
    public static string ErrorClass(string text, Options o)
    {
        if (string.IsNullOrEmpty(text) || text.Trim().Trim('-', '\r', '\n', ' ').Length == 0) return "unbekannt";
        foreach (string p in o.Transient) if (text.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) return "voruebergehend";
        return "fest";
    }

    static readonly Regex HexCode = new Regex(@"(?i)(?:0x|OLE error |HResult error 0x)([0-9A-F]{8})");

    // Kurzer Schluessel fuer gleiche Fehler (Fehlercode, sonst die ersten Woerter)
    public static string ErrorKey(string text)
    {
        if (string.IsNullOrEmpty(text)) return "ohne Fehlertext";
        Match m = HexCode.Match(text);
        if (m.Success) return "0x" + m.Groups[1].Value.ToUpperInvariant();
        string t = Regex.Replace(text, @"\s+", " ").Trim().Trim('-', ' ');
        return t.Length > 60 ? t.Substring(0, 60) : (t.Length == 0 ? "ohne Fehlertext" : t);
    }

    public static string OneLine(string s, int max)
    {
        string t = Regex.Replace(s ?? "", @"[\s\-]+(?=\s)", " ");
        t = Regex.Replace(t, @"\s+", " ").Trim();
        return t.Length > max ? t.Substring(0, max - 1) + "…" : t;
    }

    // Fehlertext zu einem Fehlschlag (Rechner, Update, Zeit des Fehlschlags laut Scan). Gesucht wird im Fenster ErrorWindowBefore Stunden davor
    // bis ErrorWindowAfter Stunden danach (der Job scheitert vor dem Scan; Wochenenden dazwischen), nie vor notBefore (Ring-Eintritt bzw.
    // Weiterlassen). Ein Unterergebnis mit derselben Update-Id geht vor, sonst das zeitlich naechste ohne Update-Id (Phase Suchen/Laden/
    // Installieren). Unterergebnisse eines ANDEREN Updates werden nie zugeordnet.
    public const double ErrorWindowBefore = 96, ErrorWindowAfter = 6;
    public static Err ErrorFor(Snapshot s, string clientId, string updateId, DateTime when, DateTime notBefore)
    {
        Err exact = null, near = null; double de = double.MaxValue, dn = double.MaxValue;
        string cb = Bare(clientId), ub = Bare(updateId);
        foreach (Err e in s.Errors)
        {
            if (!string.Equals(Bare(e.ClientId), cb, StringComparison.OrdinalIgnoreCase)) continue;
            double h = (when - e.When).TotalHours;    // > 0: Fehlertext vor dem Fehlschlag
            if (h > ErrorWindowBefore || h < -ErrorWindowAfter || e.When < notBefore) continue;
            double d = Math.Abs(h);
            if (e.UpdateId != null)
            {
                if (string.Equals(Bare(e.UpdateId), ub, StringComparison.OrdinalIgnoreCase) && d < de) { exact = e; de = d; }
                continue;
            }
            if (d < dn) { near = e; dn = d; }
        }
        return exact ?? near;
    }

    public static Err ErrorFor(Snapshot s, string clientId, string updateId, DateTime when) { return ErrorFor(s, clientId, updateId, when, DateTime.MinValue); }

    static bool Excluded(Item it, Options o)
    {
        foreach (string c in o.ExcludeClassifications) if (string.Equals(c, it.Classification, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // Von einem Menschen weitergelassen: Zeitpunkt und die Fehlschlaege, die er dabei in Kauf genommen hat ("Rechner|Fehlerschluessel").
    // Derselbe Fehler auf demselben Testrechner haelt das Update danach nicht wieder an; ein anderer Rechner oder ein anderer Fehler schon.
    public class ResumeInfo
    {
        public DateTime At;
        public HashSet<string> Accepted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public static string AcceptKey(string clientId, string errorKey) { return Bare(clientId) + "|" + (errorKey ?? "ohne Fehlertext"); }

    // ================= Regeln =================
    // holdsByWatcher: Update-Id (ohne Klammern, gross) -> Regel (S1/S2), mit der der Waechter angehalten hat; resumeInfo: von Menschen weitergelassen
    public static Evaluation Evaluate(Snapshot s, Options o, Func<string, ResumeInfo> resumeInfo, IDictionary<string, string> holdsByWatcher)
    {
        var ev = new Evaluation();
        ev.Now = s.Now; ev.Testers = s.Testers; ev.Configs = s.Configs;
        DateTime now = s.Now;

        // Zustaende nach Rechner|Update
        var st = new Dictionary<string, List<State>>(StringComparer.OrdinalIgnoreCase);
        foreach (State x in s.States)
        {
            string k = Bare(x.ClientId) + "|" + Bare(x.UpdateId);
            List<State> l; if (!st.TryGetValue(k, out l)) { l = new List<State>(); st[k] = l; }
            l.Add(x);
        }

        // Rechnerdefekte: viele verschiedene fehlgeschlagene Updates mit festem/unbekanntem Fehler (je Rechner: Update -> Fehlerschluessel)
        var perClient = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (State f in s.FleetFails)
        {
            if (f.When < now.AddDays(-o.DefectDays)) continue;
            Err e = ErrorFor(s, f.ClientId, f.UpdateId, f.When);
            string cls = ErrorClass(e != null ? e.Text : null, o);
            if (cls == "voruebergehend") continue;
            Dictionary<string, string> d; if (!perClient.TryGetValue(Bare(f.ClientId), out d)) { d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); perClient[Bare(f.ClientId)] = d; }
            d[Bare(f.UpdateId)] = e != null ? ErrorKey(e.Text) : "ohne Fehlertext";
        }
        foreach (KeyValuePair<string, Dictionary<string, string>> kv in perClient)
        {
            if (kv.Value.Count < o.DefectMinUpdates) continue;
            var keys = new Dictionary<string, int>();
            foreach (string k in kv.Value.Values) { int n; keys.TryGetValue(k, out n); keys[k] = n + 1; }
            string top = null; int topN = 0;
            foreach (KeyValuePair<string, int> k in keys) if (k.Value > topN) { top = k.Key; topN = k.Value; }
            ev.Defective[kv.Key] = top;
            string name; s.ClientNames.TryGetValue(kv.Key, out name);
            var fd = new Finding();
            fd.Rule = "defekt"; fd.Severity = "gelb"; fd.ClientId = kv.Key; fd.Mail = true;
            fd.Key = "defekt|" + kv.Key;
            fd.Title = "Rechnerproblem: " + (name ?? kv.Key) + " – " + kv.Value.Count + " Updates scheitern";
            fd.Text = (name ?? kv.Key) + " meldet in " + o.DefectDays + " Tagen " + kv.Value.Count + " verschiedene fehlgeschlagene Updates (häufigster Fehler: " + top + "). " +
                      "Das spricht für ein Problem am Rechner (z. B. beschädigter Komponentenspeicher), nicht an den Updates. Ist er Testrechner, hält sein Fehlschlag allein ein Update " +
                      "nur dann an, wenn kein anderer Testrechner es geschafft hat oder ein zweiter Testrechner denselben Fehler meldet.";
            ev.Findings.Add(fd);
        }

        foreach (Item it in s.Items)
        {
            if (it.Ring == RingReleased) { ev.Released.Add(PostRelease(s, it, o, ev)); continue; }
            var v = new Verdict(); v.It = it;
            ev.Verdicts.Add(v);
            RingConfig cfg = FindConfig(s.Configs, it.Config);
            v.ReleaseAt = ReleaseAt(it, cfg);
            if (it.Declined || it.Superseded || it.Expired || it.Revoked)
            {
                v.Status = "erledigt";
                v.Text = it.Declined ? "abgelehnt" : it.Superseded ? "durch ein neueres Update abgelöst" : it.Revoked ? "von Microsoft zurückgezogen" : "abgelaufen";
                continue;
            }
            string bareId = Bare(it.Id);
            ResumeInfo resumed = resumeInfo != null ? resumeInfo(bareId) : null;
            DateTime floor = RingEntry(it, cfg);
            if (resumed != null && resumed.At > floor) floor = resumed.At;
            var seenClients = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var keyCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // Fehlerschluessel -> Testrechner mit diesem Fehler
            foreach (Tester t in s.Testers)
            {
                if (!t.JobActive || t.JobRing > it.Ring || t.ClientId == null) continue;
                if (t.CollectionId == null || !s.Links.Contains(Bare(t.CollectionId) + "|" + bareId)) continue;
                if (!seenClients.Add(Bare(t.ClientId))) continue;
                List<State> l; st.TryGetValue(Bare(t.ClientId) + "|" + bareId, out l);
                var tr = new TesterResult(); tr.Name = t.Name; tr.ClientId = t.ClientId;
                State inst = null, fail = null, need = null;
                if (l != null) foreach (State x in l) { if (x.Kind == 'i') inst = x; else if (x.Kind == 'f') fail = x; else if (x.Kind == 'n') need = x; }
                if (inst != null) { tr.Kind = "ok"; tr.When = inst.When; v.Ok++; }
                else if (fail != null && fail.When >= floor)
                {
                    Err e = ErrorFor(s, t.ClientId, it.Id, fail.When, floor.AddHours(-ErrorWindowBefore));
                    tr.When = fail.When; tr.Error = e != null ? OneLine(e.Text, 160) : null; tr.ErrorClass = ErrorClass(e != null ? e.Text : null, o);
                    tr.ErrorKey = e != null ? ErrorKey(e.Text) : "ohne Fehlertext";
                    // Rechnerdefekt: ohne das bewertete Update gezaehlt (gemeinsame Ursache am Patchday soll nicht als Rechnerproblem durchgehen)
                    Dictionary<string, string> pc; bool defect = false;
                    if (perClient.TryGetValue(Bare(t.ClientId), out pc)) defect = pc.Count - (pc.ContainsKey(bareId) ? 1 : 0) >= o.DefectMinUpdates;
                    if (tr.ErrorClass == "voruebergehend") { tr.Kind = "voruebergehend"; v.Soft++; }
                    else if (resumed != null && resumed.Accepted.Contains(AcceptKey(t.ClientId, tr.ErrorKey))) { tr.Kind = "akzeptiert"; v.Accepted++; }
                    else
                    {
                        if (defect) { tr.Kind = "defekt"; v.HardDefectOnly++; }
                        else if (tr.ErrorClass == "unbekannt") { tr.Kind = "unklar"; v.Unclear++; }
                        else { tr.Kind = "fehler"; v.Hard++; }
                        int kc; keyCount.TryGetValue(tr.ErrorKey, out kc); keyCount[tr.ErrorKey] = kc + 1;
                    }
                }
                else if (need != null || fail != null) { tr.Kind = "offen"; tr.When = need != null ? need.When : fail.When; v.Pending++; }
                else continue;   // braucht es nicht
                v.Testers.Add(tr);
            }
            int sameKey = 0; foreach (int n in keyCount.Values) if (n > sameKey) sameKey = n;
            // S1: fester Fehler auf einem gesunden Testrechner, derselbe Fehler auf zwei Testrechnern, Fehler ohne Text auf zwei Testrechnern,
            //     oder Fehler nur auf Rechnern mit Rechnerproblem, und kein Testrechner hat es geschafft. Ein einzelner Fehler ohne Text haelt
            //     nicht sofort an (oft fehlt nur die Zuordnung) - kurz vor der Weitergabe greift dann S2.
            bool hardAny = v.Hard > 0 || sameKey >= 2 || v.Unclear >= 2 || (v.HardDefectOnly > 0 && v.Ok == 0);
            string rule = null;
            bool byWatcher = holdsByWatcher != null && holdsByWatcher.TryGetValue(bareId, out rule);
            bool excluded = Excluded(it, o);
            if (!it.Auto)
            {
                v.Status = "gestoppt";
                v.Text = byWatcher ? "vom Wächter angehalten (" + (rule == "S2" ? "ungetestet" : rule == "S1" ? "Fehler auf Testrechner" : "Grund siehe Seite") + ")" : "Übergang steht auf manuell (von Hand angehalten)";
                if (byWatcher && rule == "S2" && hardAny) { v.UpgradeS1 = true; v.Text += " – scheitert inzwischen auf " + FailNames(v); }
                else if (byWatcher && rule == "S2" && v.Ok > 0) { v.CanAutoResume = true; v.Text += " – inzwischen getestet, läuft automatisch weiter"; }
                continue;
            }
            if (excluded) { v.Status = "ausgenommen"; v.Text = "Klassifizierung " + it.Classification + " prüft der Wächter nicht (wumWatch.excludeClassifications)"; continue; }
            if (hardAny)
            {
                v.Status = "fehler"; v.Rule = "S1"; v.Hold = true;
                v.Text = "scheitert auf " + FailNames(v);
                continue;
            }
            int applicable = v.Ok + v.Soft + v.Pending + v.HardDefectOnly + v.Unclear + v.Accepted;
            if (v.Ok > 0) { v.Status = "getestet"; v.Text = "installiert auf " + v.Ok + " Testrechner(n)" + (v.Accepted > 0 ? ", " + v.Accepted + " Fehlschlag in Kauf genommen" : ""); continue; }
            if (applicable == 0) { v.Status = "kein-testrechner"; v.Text = "kein Testrechner braucht es – wird ungetestet weitergegeben"; continue; }
            bool due = v.ReleaseAt.HasValue && now >= v.ReleaseAt.Value.AddHours(-o.HoursBeforeDue);
            if (due && resumed == null)
            {
                v.Status = "ungetestet"; v.Rule = "S2"; v.Hold = true;
                v.Text = "Weitergabe " + (v.ReleaseAt.Value <= now ? "überfällig" : "am " + DeText(v.ReleaseAt.Value)) + ", aber noch auf keinem Testrechner installiert (" + applicable + " brauchen es" +
                         (v.Unclear > 0 ? ", " + v.Unclear + " mit Fehlschlag ohne Fehlertext" : "") + ")";
                continue;
            }
            v.Status = due ? "ungetestet" : "im-test";
            v.Text = due ? "ungetestet, aber von Hand weitergelassen am " + DeText(resumed.At) : applicable + " Testrechner brauchen es, noch keiner fertig" + (v.Unclear > 0 ? " (" + v.Unclear + " Fehlschlag ohne Fehlertext)" : "");
            // haengt: voruebergehender Fehler seit stuckDays (nur auf der Seite, ohne Mail und ohne offenen Punkt)
            foreach (TesterResult tr in v.Testers)
                if (tr.Kind == "voruebergehend" && tr.When.HasValue && tr.When.Value <= now.AddDays(-o.StuckDays))
                {
                    var fd = new Finding();
                    fd.Rule = "haengt"; fd.Severity = "info"; fd.UpdateId = bareId; fd.ClientId = Bare(tr.ClientId); fd.Mail = false; fd.OpenItem = false;
                    fd.Key = "haengt|" + bareId + "|" + Bare(tr.ClientId);
                    fd.Title = "Update hängt auf " + tr.Name + ": " + Short(it.Name);
                    fd.Text = it.Name + " scheitert auf dem Testrechner " + tr.Name + " seit " + DeText(tr.When.Value) + " vorübergehend (" + (tr.Error ?? "ohne Fehlertext") + ").";
                    ev.Findings.Add(fd);
                }
        }
        // Meldungen fuer Updates, die angehalten werden sollen, und fuer S2-Stopps, die jetzt scheitern
        foreach (Verdict v in ev.Verdicts)
        {
            if (v.Hold)
            {
                var fd = new Finding();
                fd.Rule = v.Rule; fd.Severity = "rot"; fd.UpdateId = Bare(v.It.Id); fd.Mail = true;
                fd.Key = "stopp|" + Bare(v.It.Id);
                fd.Title = (v.Rule == "S1" ? "Fehler im Testring: " : "Ungetestet kurz vor der Weitergabe: ") + Short(v.It.Name);
                fd.Text = Title(v.It) + " – " + v.Text + ".";
                ev.Findings.Add(fd);
            }
            else if (v.UpgradeS1)
            {
                var fd = new Finding();
                fd.Rule = "S1"; fd.Severity = "rot"; fd.UpdateId = Bare(v.It.Id); fd.Mail = true;
                fd.Key = "s1|" + Bare(v.It.Id);
                fd.Title = "Angehaltenes Update scheitert jetzt im Testring: " + Short(v.It.Name);
                fd.Text = Title(v.It) + " war angehalten, weil es ungetestet war, und " + v.Text.Substring(v.Text.IndexOf("scheitert", StringComparison.Ordinal)) + ". Es läuft nicht mehr automatisch weiter – bitte entscheiden.";
                ev.Findings.Add(fd);
            }
        }
        ev.Verdicts.Sort(delegate(Verdict a, Verdict b)
        {
            int d = Rank(a.Status).CompareTo(Rank(b.Status)); if (d != 0) return d;
            DateTime x = a.ReleaseAt ?? DateTime.MaxValue, y = b.ReleaseAt ?? DateTime.MaxValue;
            return x.CompareTo(y);
        });
        return ev;
    }

    static string FailNames(Verdict v)
    {
        var names = new List<string>();
        foreach (TesterResult tr in v.Testers)
            if (tr.Kind == "fehler" || tr.Kind == "defekt" || tr.Kind == "unklar")
                names.Add(tr.Name + (tr.Error != null ? " (" + tr.Error + ")" : " (ohne Fehlertext)") + (tr.Kind == "defekt" ? " [Rechnerproblem]" : ""));
        return string.Join(", ", names.ToArray());
    }

    static int Rank(string status)
    {
        switch (status)
        {
            case "fehler": return 0;
            case "gestoppt": return 1;
            case "ungetestet": return 2;
            case "im-test": return 3;
            case "getestet": return 4;
            case "kein-testrechner": return 5;
            case "ausgenommen": return 6;
            default: return 7;
        }
    }

    public static string Short(string name)
    {
        string n = name ?? "(ohne Namen)";
        return n.Length > 90 ? n.Substring(0, 89) + "…" : n;
    }

    // Name mit KB-Nummer, wenn sie nicht schon im Namen steht
    public static string Title(Item it)
    {
        string n = it.Name ?? "(ohne Namen)";
        if (it.Kb.HasValue && n.IndexOf("KB" + it.Kb.Value, StringComparison.OrdinalIgnoreCase) < 0) n += " (KB" + it.Kb.Value + ")";
        return n;
    }

    // Nach der Freigabe: gleicher fester/unbekannter Fehler auf mehreren Rechnern
    static Verdict PostRelease(Snapshot s, Item it, Options o, Evaluation ev)
    {
        var v = new Verdict(); v.It = it; v.Status = "freigegeben";
        v.Text = "freigegeben am " + DeText(it.LastChange);
        if (it.LastChange < s.Now.AddDays(-o.PostReleaseDays)) return v;
        var byKey = new Dictionary<string, List<string>>();
        foreach (State f in s.FleetFails)
        {
            if (!string.Equals(Bare(f.UpdateId), Bare(it.Id), StringComparison.OrdinalIgnoreCase) || f.When < it.LastChange) continue;
            Err e = ErrorFor(s, f.ClientId, it.Id, f.When);
            if (ErrorClass(e != null ? e.Text : null, o) == "voruebergehend") { v.Soft++; continue; }
            string k = e != null ? ErrorKey(e.Text) : "ohne Fehlertext";
            List<string> l; if (!byKey.TryGetValue(k, out l)) { l = new List<string>(); byKey[k] = l; }
            string name; s.ClientNames.TryGetValue(Bare(f.ClientId), out name);
            if (!l.Contains(name ?? f.ClientId)) l.Add(name ?? f.ClientId);
            v.Hard++;
        }
        foreach (KeyValuePair<string, List<string>> kv in byKey)
        {
            if (kv.Value.Count < o.PostReleaseMinClients) continue;
            var fd = new Finding();
            fd.Rule = "nach"; fd.Severity = "rot"; fd.UpdateId = Bare(it.Id); fd.Mail = true;
            fd.Key = "nach|" + Bare(it.Id);
            fd.Title = "Freigegebenes Update scheitert: " + Short(it.Name);
            fd.Text = Title(it) + " ist seit " + DeText(it.LastChange) + " freigegeben und scheitert auf " + kv.Value.Count +
                      " Rechnern mit demselben Fehler (" + kv.Key + "): " + string.Join(", ", kv.Value.ToArray()) + ". Ablehnen erwägen (acmp_wum_decline), damit es nicht weiter verteilt wird.";
            ev.Findings.Add(fd);
            v.Text += " – scheitert auf " + kv.Value.Count + " Rechnern (" + kv.Key + ")";
            break;
        }
        return v;
    }

    // ================= Lesen aus der DB =================
    readonly object _snapLock = new object();
    Snapshot _snap; DateTime _snapAt;

    // Stand aus der DB; maxAgeSeconds > 0 nimmt einen frischen Zwischenstand
    public Snapshot Load(int maxAgeSeconds)
    {
        lock (_snapLock)
        {
            if (maxAgeSeconds > 0 && _snap != null && (DateTime.UtcNow - _snapAt).TotalSeconds < maxAgeSeconds) return _snap;
            _snap = LoadNow();
            _snapAt = DateTime.UtcNow;
            return _snap;
        }
    }

    static SqlCommand Cmd(SqlConnection c, string sql)
    {
        var q = c.CreateCommand(); q.CommandText = sql; q.CommandTimeout = 120; return q;
    }

    static string GuidList(IEnumerable<string> ids)
    {
        var sb = new StringBuilder(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in ids)
        {
            string g = GuidBraces(id); if (g == null || !seen.Add(g)) continue;
            if (sb.Length > 0) sb.Append(",");
            sb.Append("'").Append(g.Trim('{', '}')).Append("'");   // nur gepruefte GUIDs
        }
        return sb.Length == 0 ? "NULL" : sb.ToString();
    }

    static DateTime Ole(double d) { try { return DateTime.FromOADate(d); } catch { return DateTime.MinValue; } }

    Snapshot LoadNow()
    {
        if (string.IsNullOrEmpty(_readCs)) throw new Exception("db.connectionString fehlt.");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var s = new Snapshot(); s.Now = DateTime.Now;
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            // 1) Ring-Konfigurationen (nur der Knoten TestRingCollections der Update-Verwaltung)
            var cq = Cmd(c, "SELECT TOP 1 CAST(CAST(Config AS xml).query('(/TAcmpConfig/WumConfig/TWindowsUpdateManagementConfig/TestRingCollections)[1]') AS nvarchar(max)) FROM SYS_Config");
            object cx = cq.ExecuteScalar();
            // unlesbare Ring-Konfiguration: ohne Weitergabe-Zeitpunkte weiter (S2 greift dann nicht, S1 schon)
            try { s.Configs = ParseConfigs(cx == null || cx is DBNull ? null : (string)cx); }
            catch (Exception ex) { _log("WUM-Ringe: Ring-Konfiguration nicht lesbar (" + ex.Message + ")"); s.Configs = new List<RingConfig>(); }

            // 2) Updates in Testring 1/2 und die in den letzten Tagen freigegebenen
            var iq = Cmd(c,
                "DECLARE @since float = CAST(GETDATE() AS float) + 2 - @days; " +
                "SELECT CAST(r.Id AS varchar(36)), r.TestRing, r.TransitionMode, r.LastChangeDate, r.CollectionName, r.ItemState, " +
                "u.KBId, u.Severity, u.IsSuperseded, u.IsExpired, u.IsRevoked, u.IsDeclined, " +
                "(SELECT TOP 1 l.Name FROM SYS_Wum_Updates_Localization l WHERE l.Id = r.Id), " +
                "g.Classification, g.ProductNames, g.ClientCountRequiring, g.ClientCountInstalled, g.ClientCountFailed " +
                "FROM SYS_TestRings r " +
                "CROSS APPLY (SELECT TOP 1 x.KBId, x.Severity, x.IsSuperseded, x.IsExpired, x.IsRevoked, x.IsDeclined FROM Sys_Wum_Updates x WHERE x.Id = r.Id ORDER BY x.RevisionNumber DESC) u " +
                "LEFT JOIN VW_Wum_UpdateGrid g ON g.Id = r.Id " +
                "WHERE r.ItemKind = " + ItemKind + " AND (r.TestRing IN (1, 2) OR (r.TestRing = " + RingReleased + " AND r.LastChangeDate >= @since))");
            iq.Parameters.AddWithValue("@days", (double)Math.Max(Opt.PostReleaseDays, 1));
            using (var r = iq.ExecuteReader())
                while (r.Read())
                {
                    var it = new Item();
                    it.Id = "{" + r.GetString(0).ToUpperInvariant() + "}";
                    it.Ring = Convert.ToInt32(r.GetValue(1));
                    it.Auto = Convert.ToInt32(r.GetValue(2)) != 0;
                    it.LastChangeRaw = r.GetDouble(3); it.LastChange = Ole(it.LastChangeRaw);
                    it.Config = r.IsDBNull(4) ? null : r.GetString(4);
                    it.ItemState = Convert.ToInt32(r.GetValue(5));
                    it.Kb = r.IsDBNull(6) ? (int?)null : Convert.ToInt32(r.GetValue(6));
                    it.Severity = r.IsDBNull(7) ? (int?)null : Convert.ToInt32(r.GetValue(7));
                    it.Superseded = !r.IsDBNull(8) && r.GetBoolean(8); it.Expired = !r.IsDBNull(9) && r.GetBoolean(9);
                    it.Revoked = !r.IsDBNull(10) && r.GetBoolean(10); it.Declined = !r.IsDBNull(11) && r.GetBoolean(11);
                    it.Name = r.IsDBNull(12) ? null : r.GetString(12);
                    it.Classification = r.IsDBNull(13) ? null : r.GetString(13);
                    it.Products = r.IsDBNull(14) ? null : r.GetString(14);
                    it.Requiring = r.IsDBNull(15) ? 0 : Convert.ToInt32(r.GetValue(15));
                    it.Installed = r.IsDBNull(16) ? 0 : Convert.ToInt32(r.GetValue(16));
                    it.Failed = r.IsDBNull(17) ? 0 : Convert.ToInt32(r.GetValue(17));
                    s.Items.Add(it);
                }

            // 3) Testrechner: Mitglieder von Containern mit WUM-Job (JobKind 58), dessen Ring < 255 ist
            var tq = Cmd(c,
                "SELECT CAST(j.Id AS varchar(36)), j.Name, cs.Path, CAST(j.ContentId AS varchar(36)), l.TestRing, CAST(j.Conditions AS nvarchar(max)), " +
                "CAST(vc.ClientID AS varchar(36)), cl.COMPUTERNAME " +
                "FROM SYS_Jobs j JOIN SYS_Jobs_Logs l ON l.JobId = j.Id " +
                "LEFT JOIN CLT_CONTAINER_STRUCTURE cs ON cs.ContainerID = j.ContainerId " +
                "LEFT JOIN VW_Container_Clients vc ON vc.ContainerID = j.ContainerId " +
                "LEFT JOIN CLT_CLIENTS cl ON cl.CLIENTID = vc.ClientID " +
                "WHERE j.JobKind = 58 AND j.IsManualJob = 0 AND l.TestRing < " + RingReleased + " ORDER BY l.TestRing, cs.Path, cl.COMPUTERNAME");
            using (var r = tq.ExecuteReader())
                while (r.Read())
                {
                    var t = new Tester();
                    t.JobId = "{" + r.GetString(0).ToUpperInvariant() + "}"; t.JobName = r.IsDBNull(1) ? null : r.GetString(1);
                    t.ContainerPath = r.IsDBNull(2) ? null : r.GetString(2);
                    t.CollectionId = r.IsDBNull(3) ? null : "{" + r.GetString(3).ToUpperInvariant() + "}";
                    t.JobRing = Convert.ToInt32(r.GetValue(4));
                    AcmpJobs.CondInfo ci = AcmpJobs.ParseConditions(r.IsDBNull(5) ? null : r.GetString(5));
                    t.JobActive = ci.Active; t.JobCondition = ci.Text;
                    t.ClientId = r.IsDBNull(6) ? null : "{" + r.GetString(6).ToUpperInvariant() + "}";
                    t.Name = r.IsDBNull(7) ? null : r.GetString(7);
                    s.Testers.Add(t);
                    if (t.ClientId != null && t.Name != null) s.ClientNames[Bare(t.ClientId)] = t.Name;
                }

            var itemIds = new List<string>(); foreach (Item it in s.Items) if (it.Ring != RingReleased) itemIds.Add(it.Id);
            var clientIds = new List<string>(); var colIds = new List<string>();
            foreach (Tester t in s.Testers) { if (t.ClientId != null) clientIds.Add(t.ClientId); if (t.CollectionId != null) colIds.Add(t.CollectionId); }

            // 4) Zustand der Testrechner je Update und Sammlungen der Test-Jobs
            if (itemIds.Count > 0 && clientIds.Count > 0)
            {
                string cl = GuidList(clientIds), il = GuidList(itemIds);
                var sq = Cmd(c,
                    "SELECT 'i', CAST(ClientId AS varchar(36)), CAST(UpdateId AS varchar(36)), ReportDate FROM CLT_Wum_InstalledUpdates WHERE ClientId IN (" + cl + ") AND UpdateId IN (" + il + ") " +
                    "UNION ALL SELECT 'n', CAST(ClientId AS varchar(36)), CAST(UpdateId AS varchar(36)), ReportDate FROM CLT_Wum_NotInstalledUpdates WHERE ClientId IN (" + cl + ") AND UpdateId IN (" + il + ") " +
                    "UNION ALL SELECT 'f', CAST(ClientId AS varchar(36)), CAST(UpdateId AS varchar(36)), ReportDate FROM CLT_Wum_FailedUpdates WHERE ClientId IN (" + cl + ") AND UpdateId IN (" + il + ")");
                using (var r = sq.ExecuteReader())
                    while (r.Read())
                    {
                        var x = new State();
                        x.Kind = r.GetString(0)[0];
                        x.ClientId = "{" + r.GetString(1).ToUpperInvariant() + "}"; x.UpdateId = "{" + r.GetString(2).ToUpperInvariant() + "}";
                        x.When = r.IsDBNull(3) ? DateTime.MinValue : DateTime.SpecifyKind(r.GetDateTime(3), DateTimeKind.Utc).ToLocalTime();
                        s.States.Add(x);
                    }
            }
            if (itemIds.Count > 0 && colIds.Count > 0)
            {
                var lq = Cmd(c, "SELECT CAST(CollectionId AS varchar(36)), CAST(UpdateId AS varchar(36)) FROM SYS_Wum_Collections_Links WHERE CollectionId IN (" + GuidList(colIds) + ") AND UpdateId IN (" + GuidList(itemIds) + ")");
                using (var r = lq.ExecuteReader())
                    while (r.Read()) s.Links.Add(r.GetString(0).ToUpperInvariant() + "|" + r.GetString(1).ToUpperInvariant());
            }

            // 5) Fehlschlaege aller Rechner der letzten Tage (Rechnerdefekt, nach Freigabe)
            int days = Math.Max(Math.Max(Opt.DefectDays, Opt.PostReleaseDays), Opt.ErrorDays);
            var fq = Cmd(c,
                "SELECT CAST(f.ClientId AS varchar(36)), cl.COMPUTERNAME, CAST(f.UpdateId AS varchar(36)), f.ReportDate FROM CLT_Wum_FailedUpdates f " +
                "LEFT JOIN CLT_CLIENTS cl ON cl.CLIENTID = f.ClientId WHERE f.ReportDate >= DATEADD(day, -@d, GETUTCDATE())");
            fq.Parameters.AddWithValue("@d", days);
            using (var r = fq.ExecuteReader())
                while (r.Read())
                {
                    var x = new State(); x.Kind = 'f';
                    x.ClientId = "{" + r.GetString(0).ToUpperInvariant() + "}"; x.UpdateId = "{" + r.GetString(2).ToUpperInvariant() + "}";
                    x.When = DateTime.SpecifyKind(r.GetDateTime(3), DateTimeKind.Utc).ToLocalTime();
                    s.FleetFails.Add(x);
                    if (!r.IsDBNull(1)) s.ClientNames[Bare(x.ClientId)] = r.GetString(1);
                }

            // 6) Fehlertexte der WUM-Jobs (Unterergebnisse mit Result 2; LastUpdate ist datetime in Ortszeit, StartDate/EndDate OLE)
            var eq = Cmd(c,
                "SELECT CAST(i.ClientId AS varchar(36)), i.ComputerName, CAST(s.ContentId AS varchar(36)), s.MessageCode, s.LastUpdate, s.TestRing, " +
                "CAST(s.NativeErrorMessage AS nvarchar(2000)), CAST(s.NativeMessage AS nvarchar(400)) " +
                "FROM SYS_Jobs_Logs_SubItems s JOIN SYS_Jobs_Logs_Items i ON i.ExecId = s.ParentExecId " +
                "WHERE s.JobKind = 58 AND s.Result = 2 AND s.LastUpdate >= DATEADD(day, -@d, GETDATE())");
            eq.Parameters.AddWithValue("@d", days);
            using (var r = eq.ExecuteReader())
                while (r.Read())
                {
                    var e = new Err();
                    e.ClientId = r.IsDBNull(0) ? null : "{" + r.GetString(0).ToUpperInvariant() + "}";
                    e.Computer = r.IsDBNull(1) ? null : r.GetString(1);
                    e.UpdateId = r.IsDBNull(2) ? null : "{" + r.GetString(2).ToUpperInvariant() + "}";
                    e.Code = r.IsDBNull(3) ? 0 : Convert.ToInt32(r.GetValue(3));
                    e.When = r.IsDBNull(4) ? DateTime.MinValue : r.GetDateTime(4);
                    e.Ring = r.IsDBNull(5) ? 0 : Convert.ToInt32(r.GetValue(5));
                    e.Text = r.IsDBNull(6) ? null : r.GetString(6);
                    e.Title = r.IsDBNull(7) ? null : r.GetString(7);
                    if (e.ClientId != null) s.Errors.Add(e);
                    if (e.ClientId != null && e.Computer != null) s.ClientNames[Bare(e.ClientId)] = e.Computer;
                }
        }
        s.LoadMs = sw.Elapsed.TotalMilliseconds;
        return s;
    }

    // ================= acmp_wum_rings =================
    public JObject List(JObject a, IDictionary<string, string> holdsByWatcher, JObject watchStatus)
    {
        string idArg = Arg(a, "id") != null ? GuidBraces(Arg(a, "id")) : null;
        if (Arg(a, "id") != null && idArg == null) throw new Exception("id ist keine GUID.");
        string search = Arg(a, "search");
        string statusArg = Arg(a, "status");
        bool released = a != null && a["released"] != null && a["released"].Type == JTokenType.Boolean && (bool)a["released"];
        int top = 50;
        if (a != null && a["top"] != null && a["top"].Type != JTokenType.Null) { try { top = Math.Max(1, Math.Min(300, (int)a["top"])); } catch { throw new Exception("top muss eine Zahl sein."); } }
        Snapshot s = Load(30);
        Evaluation ev = Evaluate(s, Opt, ResumeOf, holdsByWatcher);
        var res = new JObject();
        res["stand"] = s.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        res["ringKonfiguration"] = ConfigsJson(ev.Configs);
        res["testrechner"] = TestersJson(ev.Testers);
        var counts = new JObject();
        foreach (Verdict v in ev.Verdicts) { int n = counts[v.Status] != null ? (int)counts[v.Status] : 0; counts[v.Status] = n + 1; }
        res["anzahl"] = counts;
        var arr = new JArray(); int found = 0;
        foreach (Verdict v in released ? Concat(ev.Verdicts, ev.Released) : ev.Verdicts)
        {
            if (idArg != null && !string.Equals(v.It.Id, idArg, StringComparison.OrdinalIgnoreCase)) continue;
            if (search != null && (v.It.Name ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0 && !(v.It.Kb.HasValue && ("KB" + v.It.Kb).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
            if (statusArg != null && !string.Equals(v.Status, statusArg, StringComparison.OrdinalIgnoreCase)) continue;
            found++;
            if (arr.Count < top) arr.Add(VerdictJson(v));
        }
        res["gefunden"] = found; res["gezeigt"] = arr.Count;
        res["updates"] = arr;
        var fa = new JArray(); foreach (Finding f in ev.Findings) fa.Add(FindingJson(f));
        res["meldungen"] = fa;
        if (watchStatus != null) res["waechter"] = watchStatus;
        res["hinweis"] = "Status: fehler/ungetestet = der Waechter haelt an (Regeln S1/S2), gestoppt = Uebergang manuell, getestet = auf Testrechnern installiert, " +
                         "kein-testrechner = kein Testrechner braucht es. Anhalten: acmp_wum_ring_hold; weiterlaufen lassen oder sofort freigeben: acmp_wum_ring_release; ablehnen: acmp_wum_decline. " +
                         "Seite im Dashboard: /dashboard/updates.";
        return res;
    }

    static IEnumerable<Verdict> Concat(List<Verdict> a, List<Verdict> b) { foreach (Verdict v in a) yield return v; foreach (Verdict v in b) yield return v; }

    public static JArray ConfigsJson(List<RingConfig> l)
    {
        var arr = new JArray();
        foreach (RingConfig c in l)
        {
            var o = new JObject(); o["name"] = c.Name; o["prioritaet"] = c.Priority;
            o["tageRing1"] = c.Days[1]; o["tageRing2"] = c.Days[2];
            o["uebergang"] = c.Auto[1] && c.Auto[2] ? "automatisch" : "manuell";
            arr.Add(o);
        }
        return arr;
    }

    public static JArray TestersJson(List<Tester> l)
    {
        var arr = new JArray();
        var byJob = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
        foreach (Tester t in l)
        {
            JObject o;
            if (!byJob.TryGetValue(t.JobId, out o))
            {
                o = new JObject(); o["job"] = t.JobName; o["container"] = t.ContainerPath; o["ring"] = t.JobRing; o["aktiv"] = t.JobActive; o["startbedingung"] = t.JobCondition;
                o["rechner"] = new JArray(); byJob[t.JobId] = o; arr.Add(o);
            }
            if (t.Name != null) ((JArray)o["rechner"]).Add(t.Name);
        }
        return arr;
    }

    public static JObject VerdictJson(Verdict v)
    {
        var o = new JObject();
        o["id"] = v.It.Id; o["name"] = v.It.Name; o["kb"] = v.It.Kb.HasValue ? (JToken)v.It.Kb.Value : null;
        o["klassifizierung"] = v.It.Classification; o["ring"] = RingText(v.It.Ring);
        o["seit"] = OleText(v.It.LastChange); o["konfiguration"] = v.It.Config;
        o["uebergang"] = v.It.Auto ? "automatisch" : "manuell";
        o["status"] = v.Status; o["text"] = v.Text;
        if (v.ReleaseAt.HasValue) o["weitergabe"] = OleText(v.ReleaseAt.Value);
        if (v.Rule != null) o["regel"] = v.Rule;
        o["flotte"] = new JObject(new JProperty("brauchen", v.It.Requiring), new JProperty("installiert", v.It.Installed), new JProperty("fehlgeschlagen", v.It.Failed));
        var t = new JArray();
        foreach (TesterResult tr in v.Testers)
        {
            var x = new JObject(); x["rechner"] = tr.Name; x["stand"] = tr.Kind;
            if (tr.When.HasValue) x["zeit"] = OleText(tr.When.Value);
            if (tr.Error != null) x["fehler"] = tr.Error;
            t.Add(x);
        }
        o["testrechner"] = t;
        return o;
    }

    public static JObject FindingJson(Finding f)
    {
        var o = new JObject(); o["regel"] = f.Rule; o["stufe"] = f.Severity; o["titel"] = f.Title; o["text"] = f.Text;
        if (f.UpdateId != null) o["updateId"] = "{" + f.UpdateId + "}";
        o["mail"] = f.Mail;
        return o;
    }

    static string Arg(JObject a, string k)
    {
        if (a == null || a[k] == null || a[k].Type == JTokenType.Null) return null;
        string s = a[k].Type == JTokenType.String ? (string)a[k] : a[k].ToString();
        s = s.Trim();
        return s.Length == 0 ? null : s;
    }

    // ================= Vorpruefung =================
    public void UsePreflightResult(JObject pf) { lock (_pfLock) { _preflight = pf; _preflightAt = DateTime.UtcNow; } }

    public JObject Preflight(bool refresh)
    {
        if (!ConfigOk) return null;
        lock (_pfLock)
        {
            if (!refresh && _preflight != null && (DateTime.UtcNow - _preflightAt).TotalMinutes < PreflightMaxAgeMinutes) return _preflight;
            string v = null;
            try { v = _acmpVersion != null ? _acmpVersion() : null; } catch { }
            if (string.IsNullOrEmpty(v)) v = _lastVersion;   // SICS kurz gestoert: zuletzt erkannte Version weiterverwenden
            else _lastVersion = v;
            _preflight = RunPreflight(v);
            _preflightAt = DateTime.UtcNow;
            _log("WUM-Ringe Vorpruefung (ACMP " + (v ?? "?") + "): " + (string)_preflight["summary"]);
            return _preflight;
        }
    }

    static void Add(JArray checks, string check, string result, string detail)
    {
        var o = new JObject(); o["check"] = check; o["result"] = result; o["detail"] = detail; checks.Add(o);
    }

    static string Scalar(SqlConnection c, string sql)
    {
        var q = c.CreateCommand(); q.CommandText = sql;
        object o = q.ExecuteScalar();
        return o == null || o is DBNull ? null : Convert.ToString(o, CultureInfo.InvariantCulture);
    }

    static bool HasPerm(SqlConnection c, string obj, string perm, string col)
    {
        var q = c.CreateCommand();
        q.CommandText = col == null ? "SELECT HAS_PERMS_BY_NAME(@o, 'OBJECT', @p)" : "SELECT HAS_PERMS_BY_NAME(@o, 'OBJECT', @p, @c, 'COLUMN')";
        q.Parameters.AddWithValue("@o", obj); q.Parameters.AddWithValue("@p", perm);
        if (col != null) q.Parameters.AddWithValue("@c", col);
        object o = q.ExecuteScalar();
        return o != null && !(o is DBNull) && Convert.ToInt32(o) == 1;
    }

    JObject RunPreflight(string v)
    {
        var checks = new JArray();
        bool holdOk = false, releaseOk = false, flagsOk = false;
        try
        {
            string mm = null;
            if (!string.IsNullOrEmpty(v)) { string[] p = v.Split('.'); if (p.Length >= 2) mm = p[0] + "." + p[1]; }
            if (mm != null && Array.IndexOf(TestedVersions, mm) >= 0) Add(checks, "ACMP-Version", "ok", v);
            else Add(checks, "ACMP-Version", _allowUntested ? "warn" : "block", (mm == null ? "nicht ermittelbar" : v + " ist nicht getestet") + " (getestet: " + string.Join(", ", TestedVersions) + ")" + (_allowUntested ? " - per wumWriter.allowUntested freigegeben" : ""));
            using (var rc = new SqlConnection(_readCs))
            using (var wc = new SqlConnection(_writeCs))
            {
                rc.Open();
                try { wc.Open(); }
                catch (Exception ex) { Add(checks, "Schreib-Login", "block", "Anmeldung fehlgeschlagen: " + ex.Message); return Result(checks, false, false, false); }
                string a = Scalar(rc, "SELECT @@SERVERNAME + '/' + DB_NAME()"), b = Scalar(wc, "SELECT @@SERVERNAME + '/' + DB_NAME()");
                bool same = string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
                Add(checks, "Datenbank", same ? "ok" : "block", a + (same ? " (Lese- und Schreib-Login gleich)" : " / Schreib-Login auf " + b));
                string cols = Scalar(rc, "SELECT STUFF((SELECT ',' + COLUMN_NAME + ':' + DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='SYS_TestRings' ORDER BY ORDINAL_POSITION FOR XML PATH('')), 1, 1, '')");
                Add(checks, "Tabelle SYS_TestRings", cols == ColsRings ? "ok" : "block", "Spalten: " + (cols ?? "keine") + " (erwartet " + ColsRings + ")");
                var trg = new List<string>();
                var tq = rc.CreateCommand(); tq.CommandText = "SELECT t.name FROM sys.triggers t WHERE t.parent_id = OBJECT_ID('dbo.SYS_TestRings')";
                using (var r = tq.ExecuteReader()) while (r.Read()) trg.Add(r.GetString(0));
                var unknown = new List<string>(); foreach (string t in trg) if (Array.IndexOf(KnownTriggers, t) < 0) unknown.Add(t);
                Add(checks, "Trigger", unknown.Count == 0 ? "ok" : "block", unknown.Count == 0 ? "keine Trigger auf SYS_TestRings (wie am 22./23.10.2026)" : "unbekannte Trigger: " + string.Join(", ", unknown.ToArray()) + " - erst pruefen");
                string cfgOk = Scalar(rc, "SELECT CAST(CAST(Config AS xml).exist('/TAcmpConfig/WumConfig/TWindowsUpdateManagementConfig/TestRingCollections') AS varchar(2)) FROM SYS_Config");
                Add(checks, "Ring-Konfiguration", cfgOk == "1" ? "ok" : "warn", cfgOk == "1" ? "WumConfig/TestRingCollections lesbar" : "nicht gefunden - Weitergabe-Zeitpunkte unbekannt (Regel 'ungetestet' greift nicht)");
                bool sel = HasPerm(wc, "dbo.SYS_TestRings", "SELECT", null);
                bool updMode = HasPerm(wc, "dbo.SYS_TestRings", "UPDATE", "TransitionMode");
                bool updRing = HasPerm(wc, "dbo.SYS_TestRings", "UPDATE", "TestRing") && HasPerm(wc, "dbo.SYS_TestRings", "UPDATE", "LastChangeDate");
                holdOk = sel && updMode;
                releaseOk = sel && updMode && updRing;
                Add(checks, "Rechte Anhalten", holdOk ? "ok" : "warn", holdOk ? "SELECT und UPDATE(TransitionMode) auf SYS_TestRings vorhanden"
                    : "fehlen: " + (sel ? "" : "SELECT ") + (updMode ? "" : "UPDATE(TransitionMode)") + " auf SYS_TestRings - acmp_wum_ring_hold/_release gesperrt (grant-wum-rings.sql)");
                Add(checks, "Rechte Freigeben", releaseOk ? "ok" : "warn", releaseOk ? "UPDATE(TestRing, LastChangeDate) auf SYS_TestRings vorhanden"
                    : "fehlt UPDATE(TestRing, LastChangeDate) - acmp_wum_ring_release mode=now gesperrt (grant-wum-rings.sql bzw. grant-unix-writer.sql)");
                flagsOk = HasPerm(wc, "dbo.ap_Wum_Rebuild_UpdateFlags", "EXECUTE", null);
                Add(checks, "Rechte Flags", flagsOk ? "ok" : "warn", flagsOk ? "EXECUTE auf ap_Wum_Rebuild_UpdateFlags vorhanden" : "fehlt - nach dem Freigeben holt ACMP das Flag TestRing beim naechsten Lauf nach (grant-wum-writer.sql)");
                var extra = new List<string>();
                if (HasPerm(wc, "dbo.SYS_TestRings", "DELETE", null)) extra.Add("DELETE auf SYS_TestRings");
                if (HasPerm(wc, "dbo.SYS_TestRings", "UPDATE", "ItemKind")) extra.Add("UPDATE auf SYS_TestRings.ItemKind");
                if (Scalar(wc, "SELECT CAST(IS_ROLEMEMBER('db_owner') AS varchar(5))") == "1") extra.Add("Mitglied von db_owner");
                if (extra.Count > 0) Add(checks, "Rechte (zu viele)", "warn", string.Join(", ", extra.ToArray()));
            }
        }
        catch (Exception ex) { Add(checks, "Pruefung", "block", "Abgebrochen: " + ex.Message); }
        return Result(checks, holdOk, releaseOk, flagsOk);
    }

    static JObject Result(JArray checks, bool holdOk, bool releaseOk, bool flagsOk)
    {
        var blocks = new List<string>(); int warns = 0;
        foreach (JObject c in checks)
        {
            if ((string)c["result"] == "block") blocks.Add((string)c["check"] + ": " + (string)c["detail"]);
            else if ((string)c["result"] == "warn") warns++;
        }
        var o = new JObject();
        bool ok = blocks.Count == 0;
        o["ok"] = ok;
        o["checkedLocal"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        o["summary"] = ok ? "bestanden" + (warns > 0 ? " (" + warns + " Hinweis(e))" : "") : "GESPERRT: " + string.Join(" | ", blocks.ToArray());
        o["holdTool"] = ok && holdOk;
        o["releaseTool"] = ok && releaseOk;
        o["rebuildFlags"] = flagsOk;
        o["checks"] = checks;
        return o;
    }

    public JObject Status(bool refresh)
    {
        var o = new JObject();
        o["enabled"] = ConfigOk;
        if (!ConfigOk) { o["status"] = "aus"; o["reason"] = DisabledReason; return o; }
        JObject pf = Preflight(refresh);
        o["status"] = (bool)pf["ok"] ? "aktiv" : "gesperrt";
        o["allowedRequesters"] = new JArray(new List<string>(_allowedReq).ToArray());
        o["holdTool"] = pf["holdTool"]; o["releaseTool"] = pf["releaseTool"];
        o["vorpruefung"] = pf;
        return o;
    }

    // system = Waechter (kein Bediener)
    public void CheckWrite(string requester, string tool, bool system)
    {
        if (!ConfigOk) throw new Exception("Ringe aendern ist ausgeschaltet: " + DisabledReason);
        if (!system && (requester == null || !_allowedReq.Contains(requester))) throw new Exception("Bediener '" + requester + "' darf Update-Ringe nicht aendern (wumWriter.allowedRequesters bzw. ccWriter.allowedRequesters).");
        JObject pf = Preflight(false);
        if (pf == null || !(bool)pf["ok"]) throw new Exception("Ringe aendern gesperrt - Vorpruefung nicht bestanden: " + (pf != null ? (string)pf["summary"] : "keine Pruefung") + " (Details: acmp_wum_rings status=true). Nichts geschrieben.");
        if (pf["holdTool"] == null || !(bool)pf["holdTool"]) throw new Exception(tool + " gesperrt - Rechte des Schreib-Logins fehlen (grant-wum-rings.sql, Details: acmp_wum_rings status=true). Nichts geschrieben.");
    }

    public bool CanWebWrite(string user) { return ConfigOk && user != null && _allowedReq.Contains(user); }

    // Darf der Schreib-Login anhalten (Vorpruefung bestanden, UPDATE(TransitionMode))? Fuer den Waechter.
    public bool HoldAllowed
    {
        get
        {
            if (!ConfigOk) return false;
            try { JObject pf = Preflight(false); return pf != null && (bool)pf["ok"] && pf["holdTool"] != null && (bool)pf["holdTool"]; }
            catch { return false; }
        }
    }

    // ================= Planen =================
    public static List<string> IdList(JObject a)
    {
        bool hasList = a["updateIds"] != null && a["updateIds"].Type != JTokenType.Null, hasOne = a["updateId"] != null && a["updateId"].Type == JTokenType.String && ((string)a["updateId"]).Trim().Length > 0;
        if (hasList && hasOne) throw new Exception("Entweder updateIds oder updateId angeben, nicht beides.");
        var raw = new List<string>();
        if (hasList)
        {
            JToken t = a["updateIds"];
            if (t.Type == JTokenType.String)
            {
                string s = ((string)t).Trim();
                if (s.StartsWith("[")) { try { t = JToken.Parse(s); } catch (Exception ex) { throw new Exception("updateIds ist kein gueltiges JSON: " + ex.Message); } }
                else t = new JArray(s);
            }
            JArray arr = t as JArray;
            if (arr == null) throw new Exception("updateIds muss eine Liste sein: " + IdsHelp);
            foreach (JToken x in arr) raw.Add(x.Type == JTokenType.String ? ((string)x).Trim() : "");
        }
        else if (hasOne) raw.Add(((string)a["updateId"]).Trim());
        else throw new Exception("Welches Update? " + IdsHelp);
        var ids = new List<string>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string v in raw)
        {
            string g = GuidBraces(v);
            if (g == null) throw new Exception("\"" + v + "\" ist keine Update-Id (GUID). " + IdsHelp);
            if (seen.Add(g)) ids.Add(g);
        }
        if (ids.Count == 0) throw new Exception("updateIds ist leer.");
        if (ids.Count > MaxIds) throw new Exception(ids.Count + " Updates - hoechstens " + MaxIds + " je Auftrag.");
        return ids;
    }

    public static string Comment(JObject a, bool required)
    {
        string c = a["comment"] != null && a["comment"].Type == JTokenType.String ? ((string)a["comment"]).Trim() : "";
        if (c.Length == 0 && !required) return null;
        if (c.Length < MinCommentLength) throw new Exception("comment fehlt oder ist zu kurz (mindestens " + MinCommentLength + " Zeichen): Warum? Der Text steht im Protokoll und im Dashboard.");
        if (c.Length > MaxCommentLength) throw new Exception("comment ist zu lang (hoechstens " + MaxCommentLength + " Zeichen).");
        return AcmpOpenItems.Mask(c);
    }

    // Plan: hold (anhalten) | resume (weiterlaufen lassen) | now (sofort freigeben). snap = Stand des Waechter-Laufs (sonst neu lesen);
    // Exec prueft ohnehin gegen den Stand der DB.
    public JObject Plan(string tool, JObject a, string requester) { return Plan(tool, a, requester, null); }

    public JObject Plan(string tool, JObject a, string requester, Snapshot snap)
    {
        string action;
        if (tool == "acmp_wum_ring_hold") action = "hold";
        else if (tool == "acmp_wum_ring_release")
        {
            string m = (Arg(a, "mode") ?? "").ToLowerInvariant();
            if (m == "resume" || m == "weiter") action = "resume";
            else if (m == "now" || m == "sofort" || m == "release") action = "now";
            else throw new Exception("mode: resume (Uebergang wieder automatisch - das Update laeuft nach Plan weiter) oder now (sofort freigeben, Ring 255).");
        }
        else throw new Exception("Unbekanntes Ring-Tool: " + tool);
        List<string> ids = IdList(a);
        string comment = Comment(a, true);
        bool force = a["force"] != null && a["force"].Type == JTokenType.Boolean && (bool)a["force"];
        Snapshot s = snap ?? Load(0);
        Evaluation ev = Evaluate(s, Opt, ResumeOf, null);
        var byId = new Dictionary<string, Verdict>(StringComparer.OrdinalIgnoreCase);
        foreach (Verdict v in ev.Verdicts) byId[v.It.Id] = v;
        foreach (Verdict v in ev.Released) byId[v.It.Id] = v;
        var items = new JArray(); var lines = new List<string>(); var skipped = new List<string>();
        int no = 0;
        foreach (string id in ids)
        {
            Verdict v;
            if (!byId.TryGetValue(id, out v))
            {
                Item it0 = LookupItem(id);
                if (it0 == null) throw new Exception("Update " + id + " hat keinen Verteilungsring (SYS_TestRings, ItemKind 21) - nichts geplant.");
                if (it0.Ring == RingReleased) { skipped.Add(Label(it0) + " ist schon freigegeben"); continue; }
                if (it0.Ring == 0) throw new Exception(Label(it0) + " steht noch in keinem Ring (nicht heruntergeladen, in keiner Sammlung) - nichts zu tun.");
                v = new Verdict(); v.It = it0; v.Status = "?";
            }
            Item it = v.It;
            if (it.Ring == RingReleased) { skipped.Add(Label(it) + " ist schon freigegeben (" + DeText(it.LastChange) + ")"); continue; }
            if (it.Declined) throw new Exception(Label(it) + " ist abgelehnt - erst acmp_wum_restore, dann Ring aendern.");
            // Anhalten eines Updates, das der Waechter schon anhaelt = Stopp uebernehmen (kein Schreiben in ACMP; der Waechter laesst es dann
            // nicht mehr automatisch weiterlaufen)
            bool takeOver = action == "hold" && !it.Auto && HeldByWatcher != null && HeldByWatcher(Bare(it.Id));
            if (action == "hold" && !it.Auto && !takeOver) { skipped.Add(Label(it) + " ist schon angehalten (Uebergang manuell)"); continue; }
            if (action == "resume" && it.Auto) { skipped.Add(Label(it) + " laeuft schon automatisch"); continue; }
            bool failing = v.Status == "fehler" || v.Hard > 0 || v.UpgradeS1 || (v.HardDefectOnly > 0 && v.Ok == 0);
            if (action == "now" && failing && !force)
                throw new Exception(Label(it) + " scheitert auf Testrechnern (" + v.Text + ") - sofort freigeben nur mit force=true. Nichts geplant.");
            var p = new JObject();
            p["id"] = it.Id; p["name"] = it.Name; p["kb"] = it.Kb.HasValue ? (JToken)it.Kb.Value : null;
            p["ring"] = it.Ring; p["auto"] = it.Auto; p["lastChange"] = it.LastChangeRaw; p["itemState"] = it.ItemState;
            if (takeOver) p["takeOver"] = true;
            // Fehlschlaege, die ein Mensch beim Weiterlassen in Kauf nimmt (Rechner|Fehlerschluessel) - der Waechter haelt dafuer nicht wieder an
            var acc = new JArray();
            foreach (TesterResult tr in v.Testers) if (tr.Kind == "fehler" || tr.Kind == "defekt" || tr.Kind == "unklar" || tr.Kind == "akzeptiert") acc.Add(AcceptKey(tr.ClientId, tr.ErrorKey));
            if (action != "hold" && acc.Count > 0) p["accepted"] = acc;
            items.Add(p);
            if (takeOver) lines.Add("   Der Wächter hält es schon an – der Stopp geht auf " + requester + " über, es läuft dann nicht mehr automatisch weiter. In ACMP ändert sich nichts.");
            if (action != "hold" && acc.Count > 0) lines.Add("   In Kauf genommen (hält danach nicht wieder an): " + acc.Count + " Fehlschlag/Fehlschläge auf Testrechnern; ein anderer Rechner oder ein anderer Fehler hält es wieder an.");
            lines.Add(++no + ". " + Label(it) + " – " + RingText(it.Ring) + " seit " + DeText(it.LastChange) + ", Übergang " + (it.Auto ? "automatisch" : "manuell") + ", Konfiguration " + (it.Config ?? "?"));
            lines.Add("   Testring-Stand: " + v.Status + (v.Text != null ? " – " + v.Text : "") + (v.ReleaseAt.HasValue ? "; geplante Weitergabe " + DeText(v.ReleaseAt.Value) : ""));
            lines.Add("   Flotte: " + it.Requiring + " brauchen es, " + it.Installed + " installiert, " + it.Failed + " fehlgeschlagen.");
            if (action == "now" && v.Status != "getestet") lines.Add("   ACHTUNG: noch auf keinem Testrechner installiert (" + v.Status + ").");
            if (action == "resume" && v.ReleaseAt.HasValue && v.ReleaseAt.Value <= s.Now) lines.Add("   Hinweis: Die Verweildauer ist abgelaufen - ACMP gibt es beim naechsten stuendlichen Lauf frei.");
        }
        if (items.Count == 0) throw new Exception("Nichts zu tun: " + string.Join("; ", skipped.ToArray()));
        var plan = new JObject();
        plan["action"] = action; plan["comment"] = comment; plan["items"] = items; plan["skipped"] = new JArray(skipped.ToArray());
        var pv = new JArray();
        if (action == "hold") pv.Add("Verteilungsring ANHALTEN (wie 'Automatischen Uebergangsmodus deaktivieren' in der Console): " + items.Count + " Update(s) bleiben in ihrem Testring, bis jemand sie weiterlaufen laesst oder freigibt. UPDATE SYS_TestRings SET TransitionMode = 0.");
        else if (action == "resume") pv.Add("Uebergang wieder AUTOMATISCH (wie 'Automatischen Uebergangsmodus aktivieren'): " + items.Count + " Update(s) laufen nach der Ring-Konfiguration weiter. UPDATE SYS_TestRings SET TransitionMode = 1.");
        else pv.Add("SOFORT FREIGEBEN (wie 'Verteilungsring wechseln' -> Freigegeben): " + items.Count + " Update(s) gehen in Ring 255, der Freigabering bekommt sie beim naechsten Lauf seines WUM-Jobs. UPDATE SYS_TestRings SET TestRing = 255, TransitionMode = 1, LastChangeDate = jetzt; danach Flags neu.");
        pv.Add("Kommentar: " + comment);
        foreach (string l in lines) pv.Add(l);
        foreach (string sk in skipped) pv.Add("Uebersprungen: " + sk);
        pv.Add(action == "hold" ? "Zurueck: acmp_wum_ring_release mode=resume." : action == "resume" ? "Zurueck: acmp_wum_ring_hold." : "Zurueck geht nur in der Console (Verteilungsring wechseln -> Testring 1); Clients, die es schon installiert haben, behalten es.");
        plan["vorschau"] = pv;
        return plan;
    }

    static string Label(Item it) { return "\"" + (it.Name ?? "(ohne Namen)") + "\" " + it.Id + (it.Kb.HasValue ? " (KB" + it.Kb + ")" : ""); }

    Item LookupItem(string id)
    {
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            var q = Cmd(c,
                "SELECT r.TestRing, r.TransitionMode, r.LastChangeDate, r.CollectionName, r.ItemState, " +
                "(SELECT TOP 1 l.Name FROM SYS_Wum_Updates_Localization l WHERE l.Id = r.Id), " +
                "(SELECT TOP 1 u.KBId FROM Sys_Wum_Updates u WHERE u.Id = r.Id ORDER BY u.RevisionNumber DESC), " +
                "(SELECT TOP 1 CAST(u.IsDeclined AS int) FROM Sys_Wum_Updates u WHERE u.Id = r.Id ORDER BY u.RevisionNumber DESC) " +
                "FROM SYS_TestRings r WHERE r.Id = @id AND r.ItemKind = " + ItemKind);
            q.Parameters.AddWithValue("@id", G(id));
            using (var r = q.ExecuteReader())
            {
                if (!r.Read()) return null;
                var it = new Item(); it.Id = id;
                it.Ring = Convert.ToInt32(r.GetValue(0)); it.Auto = Convert.ToInt32(r.GetValue(1)) != 0;
                it.LastChangeRaw = r.GetDouble(2); it.LastChange = Ole(it.LastChangeRaw);
                it.Config = r.IsDBNull(3) ? null : r.GetString(3); it.ItemState = Convert.ToInt32(r.GetValue(4));
                it.Name = r.IsDBNull(5) ? null : r.GetString(5);
                it.Kb = r.IsDBNull(6) ? (int?)null : Convert.ToInt32(r.GetValue(6));
                it.Declined = !r.IsDBNull(7) && Convert.ToInt32(r.GetValue(7)) != 0;
                return it;
            }
        }
    }

    // ================= Schreiben =================
    public JObject Execute(string tool, JObject a, string requester, bool system)
    {
        CheckWrite(requester, tool, system);
        if (BrakeActive != null && BrakeActive()) throw new Exception("Notbremse gezogen - Update-Ringe werden nicht geaendert. Nichts geschrieben.");
        JObject p = a["_plan"] as JObject;
        if (p == null) throw new Exception(tool + " schreibt nur mit einem vom Server erstellten Plan - nichts geschrieben.");
        string action = (string)p["action"];
        if ((tool == "acmp_wum_ring_hold") != (action == "hold")) throw new Exception("Plan passt nicht zum Tool (" + action + " / " + tool + ") - nichts geschrieben.");
        if (action == "now")
        {
            JObject pf = Preflight(false);
            if (pf == null || pf["releaseTool"] == null || !(bool)pf["releaseTool"]) throw new Exception("Sofort freigeben gesperrt - UPDATE(TestRing, LastChangeDate) fehlt (grant-wum-rings.sql). Nichts geschrieben.");
        }
        JObject res;
        lock (_wLock) res = Exec(p, requester);
        lock (_snapLock) _snap = null;   // naechster Aufruf liest neu
        if (OnWrite != null)
            foreach (JObject it in (JArray)p["items"])
                try { OnWrite(action, Bare((string)it["id"]), requester, (string)p["comment"], it["accepted"] as JArray); } catch (Exception ex) { _log("WUM-Ringe: Nachtrag im Waechter fehlgeschlagen: " + ex.Message); }
        return res;
    }

    string SaveBackup(JObject b, string kind)
    {
        Directory.CreateDirectory(_backupDir);
        string file = Path.Combine(_backupDir, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "_ring_" + kind + ".json");
        File.WriteAllText(file, b.ToString(Newtonsoft.Json.Formatting.Indented), new UTF8Encoding(false));
        if (new FileInfo(file).Length < 40) throw new Exception("Sicherung unvollstaendig - nichts geaendert.");
        return file;
    }

    JObject Exec(JObject p, string requester)
    {
        string action = (string)p["action"];
        var items = (JArray)p["items"];
        string backup; var done = new JArray();
        double now0 = DateTime.Now.ToOADate();
        using (var wc = new SqlConnection(_writeCs))
        {
            wc.Open();
            using (SqlTransaction tx = wc.BeginTransaction())
            {
                var before = new JArray();
                foreach (JObject it in items)
                {
                    var q = wc.CreateCommand(); q.Transaction = tx;
                    q.CommandText = "SELECT TestRing, TransitionMode, LastChangeDate, ItemState FROM SYS_TestRings WITH (UPDLOCK) WHERE Id = @id AND ItemKind = " + ItemKind;
                    q.Parameters.AddWithValue("@id", G((string)it["id"]));
                    using (var r = q.ExecuteReader())
                    {
                        if (!r.Read()) throw new Exception("Ring-Zeile von " + it["name"] + " fehlt - nichts geschrieben.");
                        int ring = Convert.ToInt32(r.GetValue(0)); bool auto = Convert.ToInt32(r.GetValue(1)) != 0; double lc = r.GetDouble(2);
                        if (ring != (int)it["ring"] || auto != (bool)it["auto"] || Math.Abs(lc - (double)it["lastChange"]) > 1e-9)
                            throw new Exception("\"" + it["name"] + "\" wurde seit der Planung geaendert (jetzt " + RingText(ring) + ", Uebergang " + (auto ? "automatisch" : "manuell") + ") - nichts geschrieben, bitte erneut aufrufen.");
                        var b0 = new JObject(); b0["id"] = it["id"]; b0["name"] = it["name"]; b0["testRing"] = ring; b0["transitionMode"] = auto ? 1 : 0; b0["lastChangeDate"] = lc; b0["itemState"] = Convert.ToInt32(r.GetValue(3));
                        before.Add(b0);
                    }
                }
                var b = new JObject(); b["kind"] = "ring_" + action; b["von"] = requester; b["zeit"] = DateTime.Now.ToString("s"); b["comment"] = p["comment"]; b["vorher"] = before;
                backup = SaveBackup(b, action);
                foreach (JObject it in items)
                {
                    if (it["takeOver"] != null && (bool)it["takeOver"]) continue;   // Stopp uebernehmen: steht schon auf manuell (oben geprueft)
                    var u = wc.CreateCommand(); u.Transaction = tx;
                    if (action == "hold") u.CommandText = "UPDATE SYS_TestRings SET TransitionMode = 0 WHERE Id = @id AND ItemKind = " + ItemKind + " AND TransitionMode = 1 AND TestRing = @ring";
                    else if (action == "resume") u.CommandText = "UPDATE SYS_TestRings SET TransitionMode = 1 WHERE Id = @id AND ItemKind = " + ItemKind + " AND TransitionMode = 0 AND TestRing = @ring";
                    else
                    {
                        u.CommandText = "UPDATE SYS_TestRings SET TestRing = " + RingReleased + ", TransitionMode = 1, LastChangeDate = @now WHERE Id = @id AND ItemKind = " + ItemKind + " AND TestRing = @ring";
                        u.Parameters.AddWithValue("@now", now0);
                    }
                    u.Parameters.AddWithValue("@id", G((string)it["id"])); u.Parameters.AddWithValue("@ring", (int)it["ring"]);
                    if (u.ExecuteNonQuery() != 1) throw new Exception("UPDATE an \"" + it["name"] + "\" hat nicht genau eine Zeile geaendert - zurueckgerollt, nichts geschrieben.");
                }
                tx.Commit();
            }
            // Flags neu (nur beim Freigeben aendert sich Sys_Wum_Updates.TestRing; nicht fatal)
            if (action == "now")
            {
                try { var f = wc.CreateCommand(); f.CommandType = CommandType.StoredProcedure; f.CommandText = "dbo.ap_Wum_Rebuild_UpdateFlags"; f.CommandTimeout = 120; f.ExecuteNonQuery(); }
                catch (Exception ex) { _log("WUM-Ringe: Flags nicht neu aufgebaut (" + ex.Message + ") - ACMP holt das beim naechsten Lauf nach."); }
            }
        }
        // Kontrolle mit dem Lese-Login
        using (var rc = new SqlConnection(_readCs))
        {
            rc.Open();
            foreach (JObject it in items)
            {
                var q = rc.CreateCommand(); q.CommandText = "SELECT TestRing, TransitionMode FROM SYS_TestRings WHERE Id = @id AND ItemKind = " + ItemKind;
                q.Parameters.AddWithValue("@id", G((string)it["id"]));
                string check;
                using (var r = q.ExecuteReader())
                {
                    if (!r.Read()) check = "Zeile fehlt";
                    else
                    {
                        int ring = Convert.ToInt32(r.GetValue(0)); int mode = Convert.ToInt32(r.GetValue(1));
                        bool ok = action == "hold" ? mode == 0 : action == "resume" ? mode == 1 : ring == RingReleased && mode == 1;
                        check = ok ? "ok" : "steht auf " + RingText(ring) + ", Uebergang " + (mode == 1 ? "automatisch" : "manuell");
                    }
                }
                var d = new JObject(); d["id"] = it["id"]; d["name"] = it["name"]; d["kontrolle"] = check; done.Add(d);
            }
        }
        bool allOk = true;
        foreach (JObject d in done) if ((string)d["kontrolle"] != "ok") allOk = false;
        _log("WUM-Ringe " + action + " von " + requester + ": " + items.Count + " Update(s), Sicherung " + Path.GetFileName(backup) + (allOk ? "" : ", KONTROLLE WEICHT AB"));
        var o = new JObject();
        o["ok"] = allOk;
        if (!allOk) o["hinweis"] = "Geschrieben, aber die Kontrolle mit dem Lese-Login weicht ab - Stand mit acmp_wum_rings bzw. in der Console pruefen.";
        o["aktion"] = action == "hold" ? "angehalten" : action == "resume" ? "laeuft wieder automatisch" : "freigegeben";
        o["updates"] = done;
        o["sicherung"] = Path.GetFileName(backup);
        o["zurueck"] = action == "hold" ? "acmp_wum_ring_release mode=resume" : action == "resume" ? "acmp_wum_ring_hold" : "nur in der Console (Verteilungsring wechseln)";
        return o;
    }
}
