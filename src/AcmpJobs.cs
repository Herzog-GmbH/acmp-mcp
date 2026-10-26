// AcmpJobs: Dauerauftraege (Jobs an Containern) lesen - acmp_jobs (Liste mit Ziel, Startbedingung als Text, Laeufen und Hinweisen)
// und acmp_get_jobs (Jobs eines Clients; ersetzt den Agenten-Befehl GetJobs, der fuer API-Benutzer nur "Invalid request" liefert).
// Grundlage: eine Pruefung aller Dauerauftraege einer ACMP-Installation (22.10.2026) und die DB:
//  - SYS_Jobs: eine Zeile je Job. IsManualJob 0 = Dauerauftrag bzw. Agentenplaner-Aufgabe, 1 = einmal von Hand gestartet.
//    ContentId = Client Command (SYS_SCRIPTS.SCRIPTID), Managed Software, WUM-Sammlung oder Bericht; ContainerId = Ziel.
//    JobAction bleibt ausgeblendet (kann gespeicherte Kennwoerter enthalten). CreationDate = OLE-Datum (Ortszeit).
//  - Ziel: Job-Vorlage "Job template for container {Id}" (SYS_Templates) mit SYS_Templates_Jobs und SYS_Containers_Templates.
//    Am 22.10.2026 stimmte SYS_Jobs.ContainerId bei allen 39 Container-Jobs mit dieser Vorlage ueberein; beide Wege werden gelesen,
//    eine Abweichung wird gemeldet.
//  - Laeufe: SYS_Jobs_Logs_Items, eine Zeile je Ausfuehrung und Client (ExecId), JobId = SYS_Jobs.Id, LastActivity = OLE-Datum (Ortszeit).
//  - Startbedingung (SYS_Jobs.Conditions): TStartConditionFacade mit TStartConditionList. ALLE Bedingungen muessen gelten (Console-Text
//    z.B. "Every week on Monday on Agent start and only when connection is not metered"). Je Bedingung Enabled und RunOnce; sind alle aus,
//    schreibt die Console "Disabled". CanIgnoreMaintenanceTimes = ignoriert Wartungsfenster. ConditionText ist der englische Console-Text.
//  - Agentenplaner-Aufgaben (Scanner, JobKind 11) zeigt acmp_agent_tasks; hier nur mit includeAgentTasks.
//  - Nicht-Windows-Clients: Unix-Agent (CLT_UnixClients) bzw. Linux/macOS im Betriebssystem-Inventar, dazu die OSD-Platzhalter "[NETWORK BOOT]".
//    Container: OsFamily 0 = Windows, 1 = Unix (wie SYS_Templates.OsFamily).
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Text;
using System.Xml;
using Newtonsoft.Json.Linq;

partial class AcmpJobs   // Schreiben (abschalten, loeschen, zuruecknehmen): AcmpJobsWriter.cs
{
    public const int MaxListed = 500;
    public const int DefaultListed = 200;
    public const string ContainerTemplatePrefix = "Job template for container";
    const int SqlTimeout = 120;

    readonly string _readCs;
    readonly Action<string> _log;

    public AcmpJobs(string readCs, Action<string> log) { _readCs = readCs; _log = log; }

    // ================= Hilfen ohne DB (offline getestet in JobsTest.cs) =================

    public static string KindName(int k)
    {
        switch (k)
        {
            case 3: return "Bericht";
            case 4: return "Client Command";
            case 11: return "Scanner (Agentenplaner)";
            case 13: return "Konfigurationsprofil";
            case 58: return "Windows-Updates (WUM-Sammlung)";
            case 59: return "Managed Software";
            case 68: return "Unix Script";
            default: return "Art " + k;
        }
    }

    // Jobs, die nur auf Windows-Clients sinnvoll sind (fuer den Hinweis "Nicht-Windows-Clients im Container")
    public static bool IsWindowsKind(int k) { return k == 4 || k == 13 || k == 58 || k == 59; }

    public static string GuidBraces(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        Guid g; if (!Guid.TryParse(s.Trim().Trim('{', '}'), out g)) return null;
        return "{" + g.ToString().ToUpperInvariant() + "}";
    }

    public static string OleText(double d)
    {
        if (d <= 0) return null;
        try { return DateTime.FromOADate(d).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture); }
        catch { return d.ToString(CultureInfo.InvariantCulture); }
    }

    static bool IsStr(JToken t) { return t != null && t.Type == JTokenType.String && ((string)t).Trim().Length > 0; }

    static bool Flag(JObject a, string n) { return a[n] != null && a[n].Type == JTokenType.Boolean && (bool)a[n]; }

    static string Num(int n) { return n.ToString("N0", CultureInfo.GetCultureInfo("de-DE")); }

    // Uhrzeit aus der Startbedingung: Tagesbruchteil ("0.5", "0.999305555555556") oder "12:00:00.000" -> "12:00"
    public static string TimeText(string v)
    {
        if (string.IsNullOrEmpty(v)) return "?";
        v = v.Trim();
        if (v.IndexOf(':') > 0) return v.Length >= 5 ? v.Substring(0, 5) : v;
        double d;
        if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return v;
        int min = (int)Math.Round((d - Math.Floor(d)) * 1440.0);
        if (min >= 1440) min = 1439;
        return (min / 60).ToString("00") + ":" + (min % 60).ToString("00");
    }

    static string Child(XmlElement e, string name)
    {
        if (e == null) return null;
        XmlElement c = e[name];
        return c != null ? c.InnerText : null;
    }

    static bool Bool(string s) { return string.Equals((s ?? "").Trim(), "true", StringComparison.OrdinalIgnoreCase); }

    static List<string> Options(XmlElement e, string name)
    {
        var l = new List<string>();
        XmlElement c = e != null ? e[name] : null;
        if (c != null) foreach (XmlNode n in c.ChildNodes) if (n is XmlElement) l.Add(n.InnerText.Trim());
        return l;
    }

    static readonly Dictionary<string, string> Days = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "wdMonday", "Mo" }, { "wdTuesday", "Di" }, { "wdWednesday", "Mi" }, { "wdThursday", "Do" },
        { "wdFriday", "Fr" }, { "wdSaturday", "Sa" }, { "wdSunday", "So" }
    };

    static readonly Dictionary<string, string> Occurrences = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "doFirstInMonth", "1." }, { "doSecondInMonth", "2." }, { "doThirdInMonth", "3." }, { "doFourthInMonth", "4." }, { "doLastInMonth", "letzten" }
    };

    // Ergebnis der Auswertung einer Startbedingung (nur aktive Bedingungen setzen die Merkmale)
    public class CondInfo
    {
        public string Text = "";             // deutsch, z.B. "stuendlich, ignoriert Wartungsfenster"
        public string Console = "";          // ConditionText der Console (englisch)
        public bool Active;                  // mindestens eine Bedingung eingeschaltet
        public bool RunOnce;                 // eine eingeschaltete Bedingung ist "einmalig"
        public bool IgnoresMaintenance;      // CanIgnoreMaintenanceTimes
        public bool Hourly;                  // Startintervall von hoechstens einer Stunde
        public bool OnStart, OnLogin, InMaintenance, AfterJobList, Now;
        public double IntervalHours;         // 0 = kein Intervall
        public string IntervalText;          // z.B. "stuendlich", "alle 3 Tage"
        public int Delays;                   // AllowedExecutionDelays
        public int Conditions, Enabled;      // Zahl der Bedingungen / davon eingeschaltet
        public bool Readable = true;
        public bool Recurring { get { return Active && !RunOnce && (IntervalHours > 0 || OnStart || OnLogin || InMaintenance || AfterJobList || _repeats); } }
        internal bool _repeats;              // Zeitfenster/Wochentag/Uhrzeit ohne "einmalig"
    }

    public static CondInfo ParseConditions(string xml)
    {
        var ci = new CondInfo();
        if (string.IsNullOrEmpty(xml) || xml.Trim().Length == 0) { ci.Text = "keine Startbedingung gespeichert"; return ci; }
        var doc = new XmlDocument();
        try { doc.LoadXml(xml); }
        catch (Exception ex) { ci.Readable = false; ci.Text = "Startbedingung nicht lesbar (" + ex.Message + ")"; return ci; }
        XmlElement f = doc.DocumentElement;
        ci.Console = Child(f, "ConditionText") ?? "";
        ci.IgnoresMaintenance = Bool(Child(f, "CanIgnoreMaintenanceTimes"));
        int dl; if (int.TryParse(Child(f, "AllowedExecutionDelays"), out dl)) ci.Delays = dl;
        var on = new List<string>(); var off = new List<string>();
        XmlElement list = f["TStartConditionList"];
        if (list != null)
            foreach (XmlNode n in list.ChildNodes)
            {
                var e = n as XmlElement; if (e == null) continue;
                ci.Conditions++;
                bool en = Bool(Child(e, "Enabled")), once = Bool(Child(e, "RunOnce"));
                string kind;
                string t = OneCondition(e, out kind);
                if (once) t += " (einmalig)";
                if (!en) { off.Add(t); continue; }
                ci.Enabled++;
                on.Add(t);
                if (once) ci.RunOnce = true;
                switch (kind)
                {
                    case "interval":
                        double h = IntervalHoursOf(e);
                        if (h > 0 && (ci.IntervalHours == 0 || h < ci.IntervalHours)) { ci.IntervalHours = h; ci.IntervalText = IntervalTextOf(e); }
                        if (h > 0 && h <= 1.0) ci.Hourly = true;
                        break;
                    case "startup": ci.OnStart = true; break;
                    case "login": ci.OnLogin = true; break;
                    case "maintenance": ci.InMaintenance = true; break;
                    case "joblist": ci.AfterJobList = true; break;
                    case "now": ci.Now = true; break;
                    case "timeframe": case "dayofweek": case "time": ci._repeats = true; break;
                }
            }
        ci.Active = ci.Enabled > 0;
        var sb = new StringBuilder();
        if (!ci.Active)
        {
            sb.Append(ci.Conditions == 0 ? "AUS (keine Startbedingung)" : "AUS (alle Startbedingungen abgeschaltet)");
            if (off.Count > 0) sb.Append(" - eingestellt war: ").Append(string.Join(", ", off.ToArray()));
        }
        else
        {
            sb.Append(string.Join(", ", on.ToArray()));
            if (ci.IgnoresMaintenance) sb.Append(", ignoriert Wartungsfenster");
            if (ci.Delays > 0) sb.Append(", Benutzer darf bis zu " + ci.Delays + "-mal verschieben");
            if (off.Count > 0) sb.Append(" [abgeschaltet: ").Append(string.Join(", ", off.ToArray())).Append("]");
        }
        ci.Text = sb.ToString();
        return ci;
    }

    static string CondType(XmlElement e)
    {
        string t = Child(e, "ConditionType");
        if (!string.IsNullOrEmpty(t)) return t.Trim();
        string n = e.Name;
        return n.StartsWith("TStartConditionOn") ? "co" + n.Substring(17) : n.StartsWith("TStartCondition") ? "co" + n.Substring(15) : n;
    }

    static double IntervalHoursOf(XmlElement e)
    {
        double n;
        if (!double.TryParse(Child(e, "Interval"), NumberStyles.Float, CultureInfo.InvariantCulture, out n) || n <= 0) return 0;
        switch ((Child(e, "IntervalUnit") ?? "").Trim().ToLowerInvariant())
        {
            case "ivminute": return n / 60.0;
            case "ivhour": return n;
            case "ivday": return n * 24;
            case "ivweek": return n * 168;
            case "ivmonth": return n * 720;
            default: return 0;
        }
    }

    static string IntervalTextOf(XmlElement e)
    {
        string n = (Child(e, "Interval") ?? "?").Trim();
        string u = (Child(e, "IntervalUnit") ?? "").Trim().ToLowerInvariant();
        bool one = n == "1";
        switch (u)
        {
            case "ivminute": return one ? "jede Minute" : "alle " + n + " Minuten";
            case "ivhour": return one ? "stuendlich" : "alle " + n + " Stunden";
            case "ivday": return one ? "taeglich" : "alle " + n + " Tage";
            case "ivweek": return one ? "woechentlich" : "alle " + n + " Wochen";
            case "ivmonth": return one ? "monatlich" : "alle " + n + " Monate";
            default: return "alle " + n + " " + (Child(e, "IntervalUnit") ?? "?");
        }
    }

    // Zeitfenster innerhalb von Startintervall/Agentenstart/Anmeldung (nur wenn CheckTimeFrame an ist)
    static string InnerTimeFrame(XmlElement e)
    {
        if (!Bool(Child(e, "CheckTimeFrame"))) return "";
        XmlElement tf = e["TimeFrame"] != null ? e["TimeFrame"]["TStartConditionOnTimeFrameBase"] : null;
        if (tf == null) return "";
        return " zwischen " + TimeText(Child(tf, "StartTime")) + " und " + TimeText(Child(tf, "EndTime")) + (Bool(Child(tf, "IsRandomizedTimeFrame")) ? " (zufaellig)" : "");
    }

    // Text einer einzelnen Bedingung und ihre Art (interval, startup, login, now, timeframe, time, dayofweek, network, maintenance, joblist, ?)
    static string OneCondition(XmlElement e, out string kind)
    {
        string t = CondType(e).ToLowerInvariant();
        switch (t)
        {
            case "costartinterval":
                kind = "interval";
                return IntervalTextOf(e) + InnerTimeFrame(e);
            case "costartup":
                kind = "startup";
                return "bei Agentenstart" + InnerTimeFrame(e);
            case "cologin":
                kind = "login";
                return "bei Benutzeranmeldung" + InnerTimeFrame(e);
            case "conow":
                kind = "now";
                return "sofort";
            case "cotimeframe":
            {
                kind = "timeframe";
                string a = TimeText(Child(e, "StartTime")), b = TimeText(Child(e, "EndTime"));
                return (a == "00:00" && (b == "23:59" || b == "00:00") ? "ganztaegig (" + a + "-" + b + ")" : "zwischen " + a + " und " + b) + (Bool(Child(e, "IsRandomizedTimeFrame")) ? " (zufaellig)" : "");
            }
            case "cospecifictime":
                kind = "time";
                return "um " + TimeText(Child(e, "TimeValue"));
            case "codayofweek":
            {
                kind = "dayofweek";
                var d = new List<string>();
                foreach (string o in Options(e, "WeekDays")) { string v; d.Add(Days.TryGetValue(o, out v) ? v : o); }
                string days = d.Count > 0 ? string.Join(", ", d.ToArray()) : "?";
                string mode = (Child(e, "WeekMode") ?? "").Trim();
                if (string.Equals(mode, "wmSpecificOccurrence", StringComparison.OrdinalIgnoreCase))
                {
                    var oc = new List<string>();
                    foreach (string o in Options(e, "DayOccurrences")) { string v; oc.Add(Occurrences.TryGetValue(o, out v) ? v : o); }
                    return "am " + (oc.Count > 0 ? string.Join(", ", oc.ToArray()) : "?") + " " + days + " im Monat";
                }
                return (mode.Length == 0 || string.Equals(mode, "wmEveryWeek", StringComparison.OrdinalIgnoreCase) ? "jede Woche " : mode + " ") + days;
            }
            case "conetworkconnection":
            {
                kind = "network";
                string o = (Child(e, "ConnectionOption") ?? "").Trim();
                return string.Equals(o, "ncOnlyOnFullBandwidth", StringComparison.OrdinalIgnoreCase) ? "nur bei nicht getakteter Verbindung" : "Netzwerk " + o;
            }
            case "comaintenancewindow":
                kind = "maintenance";
                return "im Wartungsfenster";
            case "coafterjoblistload":
                kind = "joblist";
                return "nach dem Laden der Job-Liste";
            default:
                kind = "?";
                return "Bedingung " + CondType(e);
        }
    }

    // Ein Job, wie ihn die Hinweise brauchen (aus der DB gefuellt; in JobsTest.cs von Hand)
    public class JobView
    {
        public string Id, Name, ContentId, ContainerId, ContainerPath, ContentName;
        public int Kind, Members = -1, OsFamily = -1;
        public bool IsManual, ContainerKnown, ContentChecked, ContentExists = true, ContentReleased = true;
        public CondInfo Cond = new CondInfo();
        public int Runs7, Err7, Clients7, Runs30, Err30, Clients30, Waiting;
        public double LastRun, OldestWaiting, Created;
        public List<string> NonWindows = new List<string>();     // "linux-web-01 (Linux)", "[NETWORK BOOT]"
        public List<string> Servers = new List<string>();        // Computernamen mit Server-Betriebssystem
        public List<string> Duplicates = new List<string>();     // fertige Texte "an <Pfad> (Job ..., ...): N gemeinsame Clients"
        public List<string> TemplatePaths = new List<string>();  // Ziel laut Job-Vorlage(n)
        public bool TemplateMismatch;
    }

    static readonly string[] Severity = { "rot", "tot", "gelb", "info" };

    public static int Rank(string hint)
    {
        for (int i = 0; i < Severity.Length; i++) if (hint.StartsWith(Severity[i] + ":")) return i;
        return Severity.Length;
    }

    // gleiche Namen zusammenfassen ("3x [NETWORK BOOT]"), Reihenfolge des ersten Auftretens
    public static List<string> Grouped(List<string> l)
    {
        var count = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); var order = new List<string>();
        foreach (string s in l) { int n; if (count.TryGetValue(s, out n)) count[s] = n + 1; else { count[s] = 1; order.Add(s); } }
        var o = new List<string>();
        foreach (string s in order) o.Add(count[s] > 1 ? count[s] + "x " + s : s);
        return o;
    }

    static string Names(List<string> l, int max)
    {
        l = Grouped(l);
        if (l.Count <= max) return string.Join(", ", l.ToArray());
        return string.Join(", ", l.GetRange(0, max).ToArray()) + " und " + (l.Count - max) + " weitere";
    }

    static string RunsText(int runs, int days, int clients)
    {
        if (runs <= 0) return "";
        string s = Num(runs) + " Laeufe in " + days + " Tagen";
        if (clients > 0) s += " auf " + Num(clients) + " Client(s), im Schnitt " + (runs / (double)clients).ToString("0.#", CultureInfo.GetCultureInfo("de-DE")) + " je Client";
        return s;
    }

    // Hinweise je Job, schwerste zuerst. Stufen: rot (abstellen/reparieren), tot (laeuft nie), gelb (aendern/klaeren), info.
    public static List<string> Hints(JobView j, double now)
    {
        var h = new List<string>();
        CondInfo c = j.Cond;
        bool container = !string.IsNullOrEmpty(j.ContainerId);
        if (!c.Readable) h.Add("gelb: " + c.Text);
        else if (!c.Active) h.Add("tot: Startbedingung abgeschaltet - der Job laeuft nie. Loeschen oder bewusst wieder einschalten.");
        if (j.Kind == 4 && j.ContentChecked && !j.ContentExists)
            h.Add("tot: Client Command existiert nicht mehr (ContentId " + j.ContentId + " nicht in SYS_SCRIPTS) - der Job kann nicht laufen. Loeschen.");
        else if (j.Kind == 4 && j.ContentChecked && !j.ContentReleased)
            h.Add("gelb: Client Command \"" + j.ContentName + "\" hat keine freigegebene Version - der Job laeuft erst nach der Freigabe.");
        if (container && !j.ContainerKnown)
            h.Add("tot: Ziel-Container " + j.ContainerId + " gibt es nicht mehr (nicht in CLT_CONTAINER_STRUCTURE).");
        else if (container && j.Members == 0)
            h.Add("tot: Container ist leer - der Job laeuft ins Leere.");
        if (j.TemplateMismatch)
            h.Add("gelb: Ziel laut Job (SYS_Jobs.ContainerId) und laut Job-Vorlage weichen ab: Vorlage " + (j.TemplatePaths.Count > 0 ? string.Join(", ", j.TemplatePaths.ToArray()) : "(keine)") + ".");
        if (c.Active && !c.RunOnce)
        {
            if (c.Hourly)
                h.Add("rot: " + (c.IntervalText ?? "stuendlich") + " ohne 'einmalig' - laeuft rund um die Uhr immer wieder" +
                      (j.Runs7 > 0 ? " (" + RunsText(j.Runs7, 7, j.Clients7) + ", ~" + Num(j.Runs7 / 7) + " je Tag)" : "") + ".");
            if (c.OnStart)
                h.Add((j.Kind == 4 ? "gelb" : "info") + ": bei jedem Agentenstart ohne 'einmalig'" + (j.Runs30 > 0 ? " (" + RunsText(j.Runs30, 30, j.Clients30) + ")" : "") + "." +
                      (j.Kind == 4 ? " Ein Command, der etwas einstellt oder installiert, muss dann selbst erkennen, dass es schon erledigt ist." : ""));
            if (c.OnLogin)
                h.Add((j.Kind == 4 ? "gelb" : "info") + ": bei jeder Benutzeranmeldung ohne 'einmalig'" + (j.Runs30 > 0 ? " (" + RunsText(j.Runs30, 30, j.Clients30) + ")" : "") + ".");
            if (c.InMaintenance && (j.Kind == 4 || j.Kind == 59))
                h.Add((j.Kind == 4 ? "gelb" : "info") + ": in jedem Wartungsfenster ohne 'einmalig' - laeuft bei jedem Fenster erneut" + (j.Runs30 > 0 ? " (" + RunsText(j.Runs30, 30, j.Clients30) + ")" : "") + "." +
                      (j.Kind == 4 ? " Bei Installationen pruefen, ob der Command vorher erkennt, dass die Software schon da ist." : ""));
        }
        if (j.NonWindows.Count > 0 && j.OsFamily != 1 && IsWindowsKind(j.Kind))
            h.Add("gelb: " + j.NonWindows.Count + " Nicht-Windows-Client(s) im Windows-Container: " + Names(j.NonWindows, 8) +
                  ". Fuer sie ist der Job sinnlos. Abhilfe: in den Container-Eigenschaften 'Nur Clients erlauben, die der Betriebssystemfamilie des Containers entsprechen' setzen.");
        foreach (string d in j.Duplicates) h.Add("gelb: doppelt - dieselbe Aufgabe haengt auch " + d + ".");
        if (c.Active && c.IgnoresMaintenance && !c.InMaintenance && j.Servers.Count > 0 && (j.Kind == 4 || j.Kind == 59))
            h.Add("gelb: ignoriert Wartungsfenster, und im Container sind " + j.Servers.Count + " Server (" + Names(j.Servers, 6) + ") - der Job laeuft dort auch tagsueber.");
        if (j.Runs30 > 0 && j.Err30 > 0)
        {
            double rate = j.Err30 / (double)j.Runs30;
            string pct = (rate * 100).ToString("0.#", CultureInfo.GetCultureInfo("de-DE")) + " %";
            string wumNote = j.Kind == 58 ? " Bei WUM-Jobs zaehlt ein Lauf als Fehler, wenn mindestens ein Update darin scheiterte - auch voruebergehend (Office offen, Neustart); Einzelheiten: acmp_wum_rings bzw. acmp_failed_updates." : "";
            if (j.Err30 >= 3 && rate >= 0.2) h.Add("gelb: viele Fehler - " + Num(j.Err30) + " von " + Num(j.Runs30) + " Laeufen in 30 Tagen (" + pct + ")." + wumNote);
            else h.Add("info: " + Num(j.Err30) + " Fehler in 30 Tagen (" + pct + " der Laeufe)." + wumNote);
        }
        if (j.Waiting > 0 && j.OldestWaiting > 0 && now - j.OldestWaiting > 30)
            h.Add("info: " + Num(j.Waiting) + " Client(s) warten, der aelteste seit " + OleText(j.OldestWaiting) + " (Client lange offline oder Job haengt).");
        if (c.Active && container && j.ContainerKnown && j.Members > 0 && j.Runs30 == 0 && !(j.Kind == 4 && j.ContentChecked && !j.ContentExists))
            h.Add(c.RunOnce ? "info: keine Laeufe in 30 Tagen (einmalig - vermutlich auf den Mitgliedern schon gelaufen)."
                            : "gelb: keine Laeufe in 30 Tagen, obwohl eingeschaltet und " + Num(j.Members) + " Mitglied(er) - Startbedingung und Clients pruefen.");
        return SortHints(h);
    }

    // stabil nach Stufe sortieren (List.Sort ist nicht stabil)
    static List<string> SortHints(List<string> h)
    {
        var o = new List<string>();
        for (int r = 0; r <= Severity.Length; r++) foreach (string s in h) if (Rank(s) == r) o.Add(s);
        return o;
    }

    public static bool Notable(List<string> hints)
    {
        foreach (string s in hints) if (Rank(s) < 3) return true;
        return false;
    }

    // ================= Lesen =================

    static int Int(SqlDataReader r, int i) { return r.IsDBNull(i) ? -1 : Convert.ToInt32(r.GetValue(i), CultureInfo.InvariantCulture); }
    static double Dbl(SqlDataReader r, int i) { return r.IsDBNull(i) ? 0 : Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture); }
    static string Str(SqlDataReader r, int i) { return r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture); }
    static string Gid(SqlDataReader r, int i) { return r.IsDBNull(i) ? null : GuidBraces(Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture)); }

    SqlCommand Cmd(SqlConnection c, string sql)
    {
        var q = c.CreateCommand(); q.CommandText = sql; q.CommandTimeout = SqlTimeout; return q;
    }

    // Filter auf SYS_Jobs (Alias j): Dauerauftraege, optional auch manuelle Jobs und Agentenplaner-Aufgaben
    const string JobFilter = "(@manual = 1 OR j.IsManualJob = 0) AND (@agent = 1 OR j.JobKind <> 11)";

    static void FilterParams(SqlCommand q, bool manual, bool agent)
    {
        q.Parameters.AddWithValue("@manual", manual ? 1 : 0);
        q.Parameters.AddWithValue("@agent", agent ? 1 : 0);
    }

    // Client per Name oder GUID -> (ClientId "{...}", Computername); null = nicht gefunden
    string[] FindClient(SqlConnection c, string client)
    {
        string g = GuidBraces(client);
        var q = Cmd(c, g != null ? "SELECT TOP 1 CAST(CLIENTID AS varchar(36)), COMPUTERNAME FROM CLT_CLIENTS_TABLE WHERE CLIENTID = @id"
                                 : "SELECT TOP 1 CAST(CLIENTID AS varchar(36)), COMPUTERNAME FROM CLT_CLIENTS_TABLE WHERE COMPUTERNAME = @n AND IsRetired = 0");
        if (g != null) q.Parameters.AddWithValue("@id", new Guid(g.Trim('{', '}'))); else q.Parameters.AddWithValue("@n", client.Trim());
        using (var r = q.ExecuteReader())
            if (r.Read()) return new[] { "{" + r.GetString(0).ToUpperInvariant() + "}", Str(r, 1) };
        return null;
    }

    // acmp_jobs: Dauerauftraege mit Ziel, Startbedingung, Laeufen und Hinweisen. Mit client: nur Jobs, deren Container den Client enthaelt,
    // dazu der letzte Stand auf diesem Client (acmp_get_jobs).
    public JObject List(JObject a)
    {
        if (string.IsNullOrEmpty(_readCs)) throw new Exception("db.connectionString fehlt.");
        bool manual = Flag(a, "includeManual"), agent = Flag(a, "includeAgentTasks"), notable = Flag(a, "onlyNotable");
        string nameF = IsStr(a["name"]) ? ((string)a["name"]).Trim() : null;
        string contF = IsStr(a["container"]) ? ((string)a["container"]).Trim() : null;
        string clientArg = IsStr(a["client"]) ? ((string)a["client"]).Trim() : (IsStr(a["clientId"]) ? ((string)a["clientId"]).Trim() : null);
        int top = DefaultListed;
        if (a["top"] != null && a["top"].Type != JTokenType.Null) { try { top = Math.Max(1, Math.Min(MaxListed, (int)a["top"])); } catch { throw new Exception("top muss eine Zahl sein."); } }
        double now = DateTime.Now.ToOADate();
        var jobs = new List<JobView>(); var byId = new Dictionary<string, JobView>(StringComparer.OrdinalIgnoreCase);
        var released = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // Job -> freigegebene Version des Client Commands
        var templates = new JArray();
        string[] client = null;
        var clientContainers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clientRuns = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            if (clientArg != null)
            {
                client = FindClient(c, clientArg);
                if (client == null) throw new Exception("Client nicht gefunden: " + clientArg + " (Computername oder ClientId).");
                var cq = Cmd(c, "SELECT CAST(CONTAINERID AS varchar(36)) FROM CLT_CONTAINER_ITEMS WHERE LinkID = @cid");
                cq.Parameters.AddWithValue("@cid", new Guid(client[0].Trim('{', '}')));
                using (var r = cq.ExecuteReader()) while (r.Read()) clientContainers.Add(Gid(r, 0));
            }

            // 1) Jobs mit Ziel-Container und Inhalt
            var q = Cmd(c,
                "SELECT CAST(j.Id AS varchar(36)), j.Name, j.JobKind, CAST(j.ContentId AS varchar(36)), CAST(j.ContainerId AS varchar(36)), j.IsManualJob, j.CreationDate, " +
                "CAST(j.Conditions AS nvarchar(max)), s.Path, s.OsFamily, " +
                "(SELECT COUNT(*) FROM CLT_CONTAINER_ITEMS ci JOIN CLT_CLIENTS_TABLE cl ON cl.CLIENTID = ci.LinkID WHERE ci.CONTAINERID = j.ContainerId AND cl.IsRetired = 0), " +
                "CASE j.JobKind " +
                " WHEN 4 THEN (SELECT TOP 1 x.DESCRIPTION FROM SYS_SCRIPTS x WHERE TRY_CONVERT(uniqueidentifier, x.SCRIPTID) = j.ContentId ORDER BY x.ScriptVersion DESC) " +
                " WHEN 59 THEN (SELECT TOP 1 m.Name FROM SYS_ManagedSoftware_Catalog m WHERE m.Id = j.ContentId) " +
                " WHEN 58 THEN (SELECT TOP 1 w.Name FROM SYS_Wum_Collections w WHERE w.Id = j.ContentId) " +
                " WHEN 3 THEN (SELECT TOP 1 rp.Caption FROM SYS_RE_Reports rp WHERE rp.ReportId = j.ContentId) END, " +
                "CASE WHEN j.JobKind = 4 THEN (SELECT COUNT(*) FROM SYS_SCRIPTS x WHERE TRY_CONVERT(uniqueidentifier, x.SCRIPTID) = j.ContentId) END, " +
                "CASE WHEN j.JobKind = 4 THEN (SELECT MAX(x.ScriptVersion) FROM SYS_SCRIPTS x WHERE TRY_CONVERT(uniqueidentifier, x.SCRIPTID) = j.ContentId AND CAST(x.State AS int) = 7) END, " +
                "CASE WHEN s.ContainerID IS NULL THEN 0 ELSE 1 END " +
                "FROM SYS_Jobs j LEFT JOIN CLT_CONTAINER_STRUCTURE s ON s.ContainerID = j.ContainerId " +
                "WHERE " + JobFilter + " ORDER BY j.Name");
            FilterParams(q, manual, agent);
            using (var r = q.ExecuteReader())
                while (r.Read())
                {
                    var j = new JobView();
                    j.Id = Gid(r, 0); j.Name = Str(r, 1) ?? ""; j.Kind = Int(r, 2); j.ContentId = Gid(r, 3); j.ContainerId = Gid(r, 4);
                    j.IsManual = Int(r, 5) == 1; j.Created = Dbl(r, 6);
                    j.Cond = ParseConditions(Str(r, 7));
                    j.ContainerPath = Str(r, 8); j.OsFamily = Int(r, 9); j.Members = j.ContainerId != null ? Int(r, 10) : -1;
                    j.ContentName = Str(r, 11);
                    if (j.Kind == 4) { j.ContentChecked = true; j.ContentExists = Int(r, 12) > 0; j.ContentReleased = Int(r, 13) > 0; }
                    j.ContainerKnown = Int(r, 14) == 1;
                    jobs.Add(j); byId[j.Id] = j;
                    if (j.Kind == 4 && Int(r, 13) > 0) released[j.Id] = Int(r, 13);
                }

            // 2) Ziel laut Job-Vorlage "Job template for container {Id}"
            var tq = Cmd(c,
                "SELECT CAST(tj.JobId AS varchar(36)), CAST(ct.ContainerId AS varchar(36)), s.Path FROM SYS_Templates_Jobs tj " +
                "JOIN SYS_Templates t ON t.Id = tj.TemplateId JOIN SYS_Containers_Templates ct ON ct.TemplateId = t.Id " +
                "LEFT JOIN CLT_CONTAINER_STRUCTURE s ON s.ContainerID = ct.ContainerId WHERE t.Name LIKE '" + ContainerTemplatePrefix + "%'");
            var tplContainers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            using (var r = tq.ExecuteReader())
                while (r.Read())
                {
                    string jid = Gid(r, 0); JobView j;
                    if (jid == null || !byId.TryGetValue(jid, out j)) continue;
                    string cid = Gid(r, 1);
                    List<string> l; if (!tplContainers.TryGetValue(jid, out l)) tplContainers[jid] = l = new List<string>();
                    l.Add(cid);
                    j.TemplatePaths.Add(Str(r, 2) ?? cid);
                }
            foreach (JobView j in jobs)
            {
                // manuelle Jobs und Konfigurationsprofile (BitLocker, JobKind 13) haben keine Container-Vorlage; sonst muss genau die Vorlage
                // des eigenen Containers passen
                List<string> l;
                bool hasTpl = tplContainers.TryGetValue(j.Id, out l);
                if (j.ContainerId != null && !j.IsManual)
                    j.TemplateMismatch = hasTpl ? l.Count != 1 || !string.Equals(l[0], j.ContainerId, StringComparison.OrdinalIgnoreCase)
                                                : (j.Kind == 4 || j.Kind == 58 || j.Kind == 59);
                else if (j.ContainerId == null && hasTpl) j.TemplateMismatch = true;
            }

            // 3) Laeufe je Job (7 und 30 Tage, letzter Lauf, wartende Clients). Fehler: Ergebnis 2/3 oder abgebrochen/Timeout -
            //    bei WUM-Jobs (JobKind 58) aber nur, wenn ein Update im Lauf wirklich scheiterte (Unterergebnis Result 2) oder der Lauf selbst Ergebnis 2 hat. Deren
            //    Job-Ergebnis 3/4 heisst "fertig, nichts zu tun" (belegt an mehreren tausend Laeufen, fast alle Unterergebnisse Erfolg).
            const string Err = "(i.State IN (4, 5) OR (jj.JobKind = 58 AND (sf.ParentExecId IS NOT NULL OR i.Result = 2)) OR (jj.JobKind <> 58 AND i.Result IN (2, 3)))";
            var rq = Cmd(c,
                "DECLARE @now float = CAST(GETDATE() AS float) + 2; " +
                "SELECT CAST(i.JobId AS varchar(36)), " +
                "SUM(CASE WHEN i.State BETWEEN 2 AND 5 AND i.LastActivity >= @now - 7 THEN 1 ELSE 0 END), " +
                "SUM(CASE WHEN i.State BETWEEN 2 AND 5 AND i.LastActivity >= @now - 7 AND " + Err + " THEN 1 ELSE 0 END), " +
                "COUNT(DISTINCT CASE WHEN i.State BETWEEN 2 AND 5 AND i.LastActivity >= @now - 7 THEN i.ClientId END), " +
                "SUM(CASE WHEN i.State BETWEEN 2 AND 5 AND i.LastActivity >= @now - 30 THEN 1 ELSE 0 END), " +
                "SUM(CASE WHEN i.State BETWEEN 2 AND 5 AND i.LastActivity >= @now - 30 AND " + Err + " THEN 1 ELSE 0 END), " +
                "COUNT(DISTINCT CASE WHEN i.State BETWEEN 2 AND 5 AND i.LastActivity >= @now - 30 THEN i.ClientId END), " +
                "MAX(CASE WHEN i.State BETWEEN 2 AND 5 THEN i.LastActivity END), " +
                "SUM(CASE WHEN i.State < 2 THEN 1 ELSE 0 END), MIN(CASE WHEN i.State < 2 THEN i.LastActivity END) " +
                "FROM SYS_Jobs_Logs_Items i JOIN SYS_Jobs jj ON jj.Id = i.JobId " +
                "LEFT JOIN (SELECT DISTINCT s.ParentExecId FROM SYS_Jobs_Logs_SubItems s WHERE s.JobKind = 58 AND s.Result = 2) sf ON sf.ParentExecId = i.ExecId " +
                "WHERE i.JobId IN (SELECT j.Id FROM SYS_Jobs j WHERE " + JobFilter + ") GROUP BY i.JobId");
            FilterParams(rq, manual, agent);
            using (var r = rq.ExecuteReader())
                while (r.Read())
                {
                    JobView j; if (!byId.TryGetValue(Gid(r, 0) ?? "", out j)) continue;
                    j.Runs7 = Int(r, 1); j.Err7 = Int(r, 2); j.Clients7 = Int(r, 3);
                    j.Runs30 = Int(r, 4); j.Err30 = Int(r, 5); j.Clients30 = Int(r, 6);
                    j.LastRun = Dbl(r, 7); j.Waiting = Math.Max(0, Int(r, 8)); j.OldestWaiting = Dbl(r, 9);
                }

            // 4) Besondere Mitglieder der Ziel-Container: Nicht-Windows (Unix-Agent, Linux/macOS, [NETWORK BOOT]) und Server
            var mq = Cmd(c,
                "SELECT CAST(ci.CONTAINERID AS varchar(36)), cl.COMPUTERNAME, " +
                "CASE WHEN EXISTS (SELECT 1 FROM CLT_UnixClients u WHERE u.ClientId = cl.CLIENTID) THEN 'Linux/Unix-Agent' " +
                " WHEN cl.COMPUTERNAME = '[NETWORK BOOT]' THEN 'NETWORK BOOT' " +
                " WHEN o.OSSTRING LIKE '%Linux%' OR o.OSSTRING LIKE '%Ubuntu%' OR o.OSSTRING LIKE '%Debian%' OR o.OSSTRING LIKE '%mac%OS%' THEN 'Linux/macOS' " +
                " WHEN o.OSSTRING LIKE '%Server%' THEN 'Server' END " +
                "FROM CLT_CONTAINER_ITEMS ci JOIN CLT_CLIENTS_TABLE cl ON cl.CLIENTID = ci.LinkID AND cl.IsRetired = 0 " +
                "LEFT JOIN CLT_OPERATINGSYSTEM o ON o.CLIENTID = cl.CLIENTID " +
                "WHERE ci.CONTAINERID IN (SELECT j.ContainerId FROM SYS_Jobs j WHERE j.ContainerId IS NOT NULL AND " + JobFilter + ") " +
                "AND (EXISTS (SELECT 1 FROM CLT_UnixClients u WHERE u.ClientId = cl.CLIENTID) OR cl.COMPUTERNAME = '[NETWORK BOOT]' " +
                " OR o.OSSTRING LIKE '%Linux%' OR o.OSSTRING LIKE '%Ubuntu%' OR o.OSSTRING LIKE '%Debian%' OR o.OSSTRING LIKE '%mac%OS%' OR o.OSSTRING LIKE '%Server%') " +
                "ORDER BY cl.COMPUTERNAME");
            FilterParams(mq, manual, agent);
            var nonWin = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var servers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            using (var r = mq.ExecuteReader())
                while (r.Read())
                {
                    string cid = Gid(r, 0), cn = Str(r, 1) ?? "?", art = Str(r, 2);
                    var target = art == "Server" ? servers : nonWin;
                    List<string> l; if (!target.TryGetValue(cid, out l)) target[cid] = l = new List<string>();
                    l.Add(art == "Server" || art == "NETWORK BOOT" ? cn : cn + " (" + art + ")");
                }
            foreach (JobView j in jobs)
            {
                if (j.ContainerId == null) continue;
                List<string> l;
                if (nonWin.TryGetValue(j.ContainerId, out l)) j.NonWindows.AddRange(l);
                if (servers.TryGetValue(j.ContainerId, out l)) j.Servers.AddRange(l);
            }

            // 5) Doppelte Auftraege: dieselbe Aufgabe (JobKind + ContentId) an einem anderen Container mit gemeinsamen Clients
            var dq = Cmd(c,
                "SELECT CAST(a.Id AS varchar(36)), CAST(b.Id AS varchar(36)), COUNT(DISTINCT ia.LinkID) FROM SYS_Jobs a " +
                "JOIN SYS_Jobs b ON b.ContentId = a.ContentId AND b.JobKind = a.JobKind AND b.Id <> a.Id AND b.IsManualJob = 0 AND b.ContainerId IS NOT NULL AND b.ContainerId <> a.ContainerId " +
                "JOIN CLT_CONTAINER_ITEMS ia ON ia.CONTAINERID = a.ContainerId JOIN CLT_CONTAINER_ITEMS ib ON ib.CONTAINERID = b.ContainerId AND ib.LinkID = ia.LinkID " +
                "WHERE a.IsManualJob = 0 AND a.ContainerId IS NOT NULL GROUP BY a.Id, b.Id");
            using (var r = dq.ExecuteReader())
                while (r.Read())
                {
                    JobView ja, jb;
                    if (!byId.TryGetValue(Gid(r, 0) ?? "", out ja) || !byId.TryGetValue(Gid(r, 1) ?? "", out jb)) continue;
                    ja.Duplicates.Add("an " + (jb.ContainerPath ?? jb.ContainerId) + " (" + (jb.Cond.Active ? jb.Cond.Text : "dort abgeschaltet") + "): " + Num(Int(r, 2)) + " gemeinsame Client(s) bekommen sie mehrfach");
                }

            // 6) Container mit Job-Vorlage, aber ohne Job
            var vq = Cmd(c,
                "SELECT s.Path, CAST(ct.ContainerId AS varchar(36)), " +
                "(SELECT COUNT(*) FROM CLT_CONTAINER_ITEMS ci JOIN CLT_CLIENTS_TABLE cl ON cl.CLIENTID = ci.LinkID WHERE ci.CONTAINERID = ct.ContainerId AND cl.IsRetired = 0) " +
                "FROM SYS_Containers_Templates ct JOIN SYS_Templates t ON t.Id = ct.TemplateId LEFT JOIN CLT_CONTAINER_STRUCTURE s ON s.ContainerID = ct.ContainerId " +
                "WHERE t.Name LIKE '" + ContainerTemplatePrefix + "%' AND NOT EXISTS (SELECT 1 FROM SYS_Templates_Jobs tj JOIN SYS_Jobs j ON j.Id = tj.JobId WHERE tj.TemplateId = t.Id) " +
                "ORDER BY s.Path");
            using (var r = vq.ExecuteReader())
                while (r.Read())
                {
                    var o = new JObject();
                    o["container"] = Str(r, 0) ?? Gid(r, 1) + " (nicht in Containerstruktur)";
                    o["mitglieder"] = Int(r, 2);
                    o["containerId"] = Gid(r, 1);
                    templates.Add(o);
                }

            // 7) Mit client: letzter Stand dieses Clients je Job
            if (client != null)
            {
                var lq = Cmd(c,
                    "DECLARE @now float = CAST(GETDATE() AS float) + 2; " +
                    "SELECT x.JobId, x.State, x.Result, x.LastActivity, x.StateMsg, x.n7, x.n30 FROM (" +
                    "SELECT CAST(i.JobId AS varchar(36)) AS JobId, i.State, i.Result, i.LastActivity, i.StateMsg, " +
                    "ROW_NUMBER() OVER (PARTITION BY i.JobId ORDER BY i.LastActivity DESC) AS rn, " +
                    "SUM(CASE WHEN i.State BETWEEN 2 AND 5 AND i.LastActivity >= @now - 7 THEN 1 ELSE 0 END) OVER (PARTITION BY i.JobId) AS n7, " +
                    "SUM(CASE WHEN i.State BETWEEN 2 AND 5 AND i.LastActivity >= @now - 30 THEN 1 ELSE 0 END) OVER (PARTITION BY i.JobId) AS n30 " +
                    "FROM SYS_Jobs_Logs_Items i WHERE i.ClientId = @cid) x WHERE x.rn = 1");
                lq.Parameters.AddWithValue("@cid", new Guid(client[0].Trim('{', '}')));
                using (var r = lq.ExecuteReader())
                    while (r.Read())
                    {
                        var o = new JObject();
                        o["status"] = JobState(Int(r, 1)); o["ergebnis"] = JobResult(Int(r, 2));
                        o["zeit"] = OleText(Dbl(r, 3));
                        string m = Str(r, 4); if (!string.IsNullOrEmpty(m)) o["meldung"] = m.Length > 300 ? m.Substring(0, 297) + "..." : m;
                        o["laeufe7"] = Int(r, 5); o["laeufe30"] = Int(r, 6);
                        clientRuns[Gid(r, 0) ?? ""] = o;
                    }
            }
        }

        // Filtern, Hinweise, Ausgabe
        var rows = new List<KeyValuePair<int, JObject>>();
        int notableCount = 0, total = 0;
        foreach (JobView j in jobs)
        {
            if (nameF != null && j.Name.IndexOf(nameF, StringComparison.OrdinalIgnoreCase) < 0 &&
                (j.ContentName == null || j.ContentName.IndexOf(nameF, StringComparison.OrdinalIgnoreCase) < 0)) continue;
            if (contF != null)
            {
                bool hit = j.ContainerPath != null && j.ContainerPath.IndexOf(contF, StringComparison.OrdinalIgnoreCase) >= 0;
                foreach (string p in j.TemplatePaths) if (p.IndexOf(contF, StringComparison.OrdinalIgnoreCase) >= 0) hit = true;
                if (!hit) continue;
            }
            if (client != null && (j.ContainerId == null || !clientContainers.Contains(j.ContainerId))) continue;
            List<string> hints = Hints(j, now);
            bool isNotable = Notable(hints);
            total++;
            if (isNotable) notableCount++;
            if (notable && !isNotable) continue;
            var o = new JObject();
            o["name"] = j.Name;
            o["art"] = KindName(j.Kind);
            o["inhalt"] = j.Kind == 4 && j.ContentChecked && !j.ContentExists ? "(Client Command geloescht)" : j.ContentName;
            if (j.Kind == 4 && j.ContentChecked && j.ContentExists)
            {
                int v; o["freigegeben"] = released.TryGetValue(j.Id, out v) ? "Version " + v : "keine freigegebene Version";
            }
            o["ziel"] = j.ContainerId == null ? (j.Kind == 3 ? "(Bericht, kein Container)" : "(kein Container)")
                       : (j.ContainerKnown ? j.ContainerPath : j.ContainerId + " (Container fehlt)");
            o["mitglieder"] = j.Members >= 0 ? (JToken)j.Members : JValue.CreateNull();
            o["startbedingung"] = j.Cond.Text;
            if (!string.IsNullOrEmpty(j.Cond.Console)) o["konsole"] = j.Cond.Console;
            o["aktiv"] = j.Cond.Active;
            o["einmalig"] = j.Cond.RunOnce;
            o["ignoriertWartungsfenster"] = j.Cond.IgnoresMaintenance;
            o["angelegt"] = OleText(j.Created);
            o["laeufe7"] = j.Runs7; o["fehler7"] = j.Err7; o["clients7"] = j.Clients7;
            o["laeufe30"] = j.Runs30; o["fehler30"] = j.Err30; o["clients30"] = j.Clients30;
            o["letzterLauf"] = OleText(j.LastRun);
            if (j.Waiting > 0) o["wartend"] = j.Waiting;
            if (client != null) { JObject cr; o["aufDiesemClient"] = clientRuns.TryGetValue(j.Id, out cr) ? (JToken)cr : (JToken)"noch kein Eintrag im Job-Monitor"; }
            o["hinweise"] = new JArray(hints.ToArray());
            if (j.IsManual) o["manuell"] = true;
            o["jobId"] = j.Id;
            o["containerId"] = j.ContainerId;
            o["contentId"] = j.ContentId;
            int rank = hints.Count > 0 ? Rank(hints[0]) : Severity.Length;
            rows.Add(new KeyValuePair<int, JObject>(rank, o));
        }
        // auffaellige zuerst (rot, tot, gelb), dann Ziel und Name; stabile Sortierung
        var idx = new Dictionary<JObject, int>(); for (int i = 0; i < rows.Count; i++) idx[rows[i].Value] = i;
        rows.Sort(delegate(KeyValuePair<int, JObject> x, KeyValuePair<int, JObject> y)
        {
            int d = Math.Min(x.Key, 3).CompareTo(Math.Min(y.Key, 3)); if (d != 0) return d;
            d = string.Compare((string)x.Value["ziel"], (string)y.Value["ziel"], StringComparison.OrdinalIgnoreCase); if (d != 0) return d;
            d = string.Compare((string)x.Value["name"], (string)y.Value["name"], StringComparison.OrdinalIgnoreCase); if (d != 0) return d;
            return idx[x.Value].CompareTo(idx[y.Value]);
        });
        var arr = new JArray(); foreach (var kv in rows) { if (arr.Count >= top) break; arr.Add(kv.Value); }
        var res = new JObject();
        res["stand"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        if (client != null) { res["client"] = client[1]; res["clientId"] = client[0]; }
        res["auswahl"] = (manual ? "alle Jobs inkl. manueller" : "Dauerauftraege (IsManualJob = 0)") + (agent ? " und Agentenplaner-Aufgaben" : ", ohne Agentenplaner-Scanner (dafuer acmp_agent_tasks)") +
                         (nameF != null ? ", Name enthaelt \"" + nameF + "\"" : "") + (contF != null ? ", Container enthaelt \"" + contF + "\"" : "") +
                         (client != null ? ", nur Jobs an Containern von " + client[1] : "") + (notable ? ", nur auffaellige" : "");
        res["gefunden"] = total; res["auffaellig"] = notableCount; res["gezeigt"] = arr.Count;
        if (arr.Count < (notable ? notableCount : total)) res["gekuerzt"] = "top=" + top + " - mit top (max. " + MaxListed + ") oder Filtern mehr zeigen.";
        res["legende"] = "hinweise: 'rot' abstellen/reparieren, 'tot' laeuft nie, 'gelb' aendern/klaeren, 'info' zur Kenntnis. Laeufe aus SYS_Jobs_Logs_Items " +
                         "(fehler = Ergebnis Warnung/Fehlgeschlagen/Fehler oder abgebrochen; bei WUM-Sammlungen nur Laeufe, in denen ein Update scheiterte). Ein 'Erfolg' heisst bei Commands ohne Exit-Code-Auswertung nur, dass der Befehl startete." +
                         (client != null ? " aufDiesemClient = letzter Eintrag im Job-Monitor fuer diesen Client; alle Laeufe (auch einmalige Jobs): acmp_job_results." : "");
        res["jobs"] = arr;
        if (client == null && nameF == null && contF == null) res["vorlagenOhneJob"] = templates;
        return res;
    }

    public static string JobState(int s) { return s == 0 ? "Wartend" : s == 1 ? "Zugewiesen" : s == 2 ? "Laeuft" : s == 3 ? "Abgeschlossen" : s == 4 ? "Abgebrochen" : s == 5 ? "Timeout/Fehler" : "Status " + s; }
    public static string JobResult(int r) { return r == 0 ? "Offen" : r == 1 ? "Erfolg" : r == 2 ? "Warnung/Fehlgeschlagen" : r == 3 ? "Fehler" : "Ergebnis " + r; }
}
