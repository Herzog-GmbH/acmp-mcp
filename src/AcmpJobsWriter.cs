// AcmpJobsWriter: Dauerauftraege abschalten/einschalten (acmp_job_set_enabled), loeschen (acmp_job_delete) und zuruecknehmen (acmp_job_restore).
// Grundlage: SQL-Mitschnitt der ACMP-Console 6.10 vom 22.10.2026. Die Console schreibt nicht selbst, der ACMP-Server tut es:
//  - Abschalten/Einschalten = UPDATE SYS_Jobs (alle Spalten, geaendert nur Conditions und ConditionCheckSum) + UPDATE SYS_Jobs_Logs SET ConditionText
//    WHERE JobId. In Conditions: Enabled der Bedingung false/true, Action eaEdit, ConditionText "Disabled" bzw. Text der Bedingung, Action der
//    Huelle eaEdit. Keine weiteren Tabellen, kein Signal an die Agents - die holen ihre Job-Liste ueber af_Jobs_CalcJobsOfTemplates aus SYS_Jobs.
//  - ap_Job_IsJobActive haelt einen Container-Job fuer aktiv, solange IRGENDEIN <Enabled> "true" ist (auch im inneren Zeitfenster), deshalb
//    setzt das Abschalten ALLE Enabled auf false.
//  - ConditionCheckSum (32 Hex, wird an die Agents durchgereicht) ist kein einfacher Hash ueber die XML (an 7 Mitschnitt-Paaren und 44 DB-Zeilen
//    geprueft). Ist der Wert der Console fuer genau diese Startbedingung bekannt (zweiter Mitschnitt bzw. eindeutig in der DB, siehe KnownChecksum),
//    nimmt der MCP ihn, sonst einen eigenen (MD5 ueber die neue XML, UTF-16LE), damit Agents die Aenderung erkennen; acmp_job_restore stellt
//    alte XML und alte Pruefsumme wieder her.
//  - Loeschen: die Console loescht beim letzten Job des Containers die ganze Job-Vorlage (DELETE SYS_Templates, Kaskade). Der MCP loescht nur
//    die Zeile in SYS_Templates_Jobs; der Trigger TG_Delete_SYS_Templates_Jobs loescht dann SYS_Jobs und setzt SYS_Jobs_Logs.State = 2 (fertig).
//    Eine leere Job-Vorlage bleibt und stoert nicht.
// Startbedingung setzen (acmp_job_set_condition) und Client-Beschraenkung (acmp_container_set_restriction), zweiter Mitschnitt vom 22.10.2026:
//  - Startbedingung: die Console fuehrt sie je Container. Beim Speichern ersetzt der ACMP-Server die Bedingungsliste durch genau eine neue
//    Bedingung (neue Id, Action eaAdd), setzt CanIgnoreMaintenanceTimes, ConditionText (englisch) und Action eaEdit der Huelle, schreibt
//    SYS_Jobs (alle Spalten, geaendert Conditions + ConditionCheckSum) und SYS_Jobs_Logs.ConditionText. Die uebrigen Teile der Huelle bleiben.
//  - Client-Beschraenkung: UPDATE CLT_CONTAINER_STRUCTURE (alle Spalten, geaendert nur ClientRestriction: 1 = nur Clients der
//    Betriebssystemfamilie des Containers, 0 = alle). Ausgewertet wird sie im ACMP-Server (keine SQL-Prozedur liest die Spalte).
// Regeln: nur Dauerauftraege (IsManualJob 0, keine Agentenplaner-Scanner), loeschen nur Jobs mit genau einer Container-Vorlage, hoechstens MaxIds
// je Auftrag. dryRun zeigt den Plan; sonst wird er direkt ausgefuehrt (seit 22.10.2026 ohne Vier-Augen-Freigabe, dafuer Aktivitaetsprotokoll und Notbremse): Sicherung
// vorher (jobs-backups\), eine Transaktion je Job (Startbedingung: eine fuer alle Jobs des Containers), der Stand aus dem Plan wird in der
// Transaktion erneut geprueft, Kontrolle danach.
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

partial class AcmpJobs
{
    public const int MaxIds = 10;
    public static readonly string[] WriterTestedVersions = { "6.10" };
    public static readonly string[] KnownTriggers = { "TG_Delete_SYS_Templates_Jobs" };   // Mitschnitt/DB 22.10.2026; auf SYS_Jobs und SYS_Jobs_Logs keine
    public static readonly string[] KnownContainerTriggers = { "TG_CLT_CONTAINER_STRUCTURE_UpdateTenantNo" };   // setzt nur TenantNo nach
    public static readonly int[] DeletableKinds = { 4, 58, 59 };                          // Client Command, WUM-Sammlung, Managed Software (mit Container-Vorlage)
    public const int MaxContainerJobs = 25;                                               // Startbedingung setzen: hoechstens so viele Jobs je Container

    public static readonly string IdsHelp =
        "Liste von Job-Ids (GUID), 1-" + MaxIds + ", z.B. [\"{6880889F-...}\"]. Die Job-Id steht in acmp_jobs (jobId). Namen sind nicht eindeutig " +
        "(z.B. 'Client - Standard' haengt an 8 Containern), deshalb nur Ids.";

    string _writeCs, _backupDir;
    readonly HashSet<string> _allowedReq = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    Func<string> _acmpVersion;
    bool _allowUntested;
    JObject _wpf; DateTime _wpfAt;
    readonly object _wpfLock = new object();
    static readonly object _jwLock = new object();

    public bool WriterConfigOk { get; private set; }
    public string WriterDisabledReason { get; private set; }

    // Schreib-Teil einrichten: jobWriter-Section, sonst Schreib-Login und Bediener wie ccWriter
    public void ConfigureWriter(JObject cfg, JObject ccCfg, string cfgDir, Func<string> acmpVersion)
    {
        _acmpVersion = acmpVersion;
        string bd = cfg != null && cfg["backupDir"] != null ? (string)cfg["backupDir"] : "jobs-backups";
        _backupDir = Path.IsPathRooted(bd) ? bd : Path.Combine(cfgDir ?? ".", bd);
        _allowUntested = cfg != null && cfg["allowUntested"] != null && (bool)cfg["allowUntested"];
        if (cfg == null || cfg["enabled"] == null || !(bool)cfg["enabled"]) { WriterDisabledReason = "jobWriter.enabled ist nicht true (nur Lesen und dryRun)"; return; }
        _writeCs = cfg["connectionString"] != null ? (string)cfg["connectionString"] : (ccCfg != null ? (string)ccCfg["connectionString"] : null);
        JArray ar = (cfg["allowedRequesters"] as JArray) ?? (ccCfg != null ? ccCfg["allowedRequesters"] as JArray : null);
        if (ar != null) foreach (JToken t in ar) _allowedReq.Add((string)t);
        if (string.IsNullOrEmpty(_readCs)) { WriterDisabledReason = "db.connectionString fehlt"; return; }
        if (string.IsNullOrEmpty(_writeCs) || _writeCs.StartsWith("HIER_")) { WriterDisabledReason = "kein Schreib-Login (jobWriter.connectionString oder ccWriter.connectionString)"; return; }
        if (_allowedReq.Count == 0) { WriterDisabledReason = "keine erlaubten Antragsteller (jobWriter.allowedRequesters oder ccWriter.allowedRequesters)"; return; }
        WriterConfigOk = true;
    }

    public static bool IsWriteTool(string name)
    {
        return name == "acmp_job_set_enabled" || name == "acmp_job_delete" || name == "acmp_job_restore" || name == "acmp_job_set_condition" || name == "acmp_container_set_restriction";
    }

    // ================= Hilfen ohne DB (offline getestet in JobsTest.cs) =================

    // Auftrag -> Liste normalisierter Job-Ids: jobIds=[...] (auch als JSON-Text) oder jobId="..."
    public static List<string> IdList(JObject a)
    {
        bool hasList = a["jobIds"] != null && a["jobIds"].Type != JTokenType.Null, hasOne = IsStr(a["jobId"]);
        if (hasList && hasOne) throw new Exception("Entweder jobIds oder jobId angeben, nicht beides.");
        var raw = new List<string>();
        if (hasList)
        {
            JToken t = a["jobIds"];
            if (t.Type == JTokenType.String)
            {
                string s = ((string)t).Trim();
                if (s.StartsWith("[")) { try { t = JToken.Parse(s); } catch (Exception ex) { throw new Exception("jobIds ist kein gueltiges JSON: " + ex.Message); } }
                else t = new JArray(s);
            }
            JArray arr = t as JArray;
            if (arr == null) throw new Exception("jobIds muss eine Liste sein: " + IdsHelp);
            foreach (JToken x in arr) { string v = x.Type == JTokenType.String ? ((string)x).Trim() : ""; if (v.Length == 0) throw new Exception("jobIds: leere Angabe. " + IdsHelp); raw.Add(v); }
        }
        else if (hasOne) raw.Add(((string)a["jobId"]).Trim());
        else throw new Exception("Welcher Job? " + IdsHelp);
        if (raw.Count == 0) throw new Exception("jobIds ist leer.");
        var ids = new List<string>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string v in raw)
        {
            string g = GuidBraces(v);
            if (g == null) throw new Exception("\"" + v + "\" ist keine Job-Id (GUID). " + IdsHelp);
            if (seen.Add(g)) ids.Add(g);
        }
        if (ids.Count > MaxIds) throw new Exception(ids.Count + " Jobs - hoechstens " + MaxIds + " je Auftrag, bitte aufteilen.");
        return ids;
    }

    static XmlElement Kid(XmlElement e, string name) { return e != null ? e[name] : null; }

    // Wert eines direkten Kind-Elements setzen; fehlt es, wird es am Ende angehaengt
    static void SetKid(XmlElement e, string name, string value)
    {
        XmlElement c = e[name];
        if (c == null) { c = e.OwnerDocument.CreateElement(name); e.AppendChild(c); }
        c.InnerText = value;
    }

    static XmlDocument LoadConditions(string xml)
    {
        if (string.IsNullOrEmpty(xml)) throw new Exception("Der Job hat keine Startbedingung gespeichert - nichts zu schalten.");
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        if (doc.DocumentElement.Name != "TStartConditionFacade") throw new Exception("Startbedingung hat ein unbekanntes Format (" + doc.DocumentElement.Name + ").");
        return doc;
    }

    static List<XmlElement> Conditions(XmlDocument doc)
    {
        var l = new List<XmlElement>();
        XmlElement list = doc.DocumentElement["TStartConditionList"];
        if (list != null) foreach (XmlNode n in list.ChildNodes) { var e = n as XmlElement; if (e != null) l.Add(e); }
        return l;
    }

    // Abschalten wie die Console (Enabled false, Action eaEdit, ConditionText "Disabled"), zusaetzlich ALLE Enabled (auch innere Zeitfenster)
    // auf false, damit ap_Job_IsJobActive den Job nicht mehr fuer aktiv haelt
    public static string DisableXml(string xml)
    {
        XmlDocument doc = LoadConditions(xml);
        List<XmlElement> conds = Conditions(doc);
        if (conds.Count == 0) throw new Exception("Der Job hat keine Startbedingung - er laeuft ohnehin nicht.");
        foreach (XmlNode n in doc.GetElementsByTagName("Enabled")) n.InnerText = "false";
        foreach (XmlElement c in conds) SetKid(c, "Action", "eaEdit");
        SetKid(doc.DocumentElement, "ConditionText", "Disabled");
        SetKid(doc.DocumentElement, "Action", "eaEdit");
        return doc.OuterXml;
    }

    // Einschalten ohne Sicherung des MCP: alle Bedingungen der Liste einschalten (innere Zeitfenster bleiben, wie sie sind), Action eaEdit,
    // ConditionText nach dem Muster der Console (englisch)
    public static string EnableXml(string xml)
    {
        XmlDocument doc = LoadConditions(xml);
        List<XmlElement> conds = Conditions(doc);
        if (conds.Count == 0) throw new Exception("Der Job hat keine Startbedingung - zum Einschalten in der Console eine Startbedingung anlegen.");
        foreach (XmlElement c in conds) { SetKid(c, "Enabled", "true"); SetKid(c, "Action", "eaEdit"); }
        SetKid(doc.DocumentElement, "ConditionText", EnglishText(doc));
        SetKid(doc.DocumentElement, "Action", "eaEdit");
        return doc.OuterXml;
    }

    static readonly Dictionary<string, string> DaysEn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "wdMonday", "Monday" }, { "wdTuesday", "Tuesday" }, { "wdWednesday", "Wednesday" }, { "wdThursday", "Thursday" },
        { "wdFriday", "Friday" }, { "wdSaturday", "Saturday" }, { "wdSunday", "Sunday" }
    };

    // Text der eingeschalteten Bedingungen wie die Console ihn schreibt (Beispiele aus der DB: "Start every hour", "Start every 3 days",
    // "Once on ACMP Agent start", "Always on ACMP Agent start", "As soon as possible", "Within Maintenance Windows", "From 12:00 to 12:30 run once",
    // "Every week on Thursday, Friday from 12:00 to 13:00", "On every 2., 3., 4. Thursday at 23:00", "... and only when connection is not metered")
    public static string EnglishText(XmlDocument doc)
    {
        var lead = new List<string>(); var rest = new List<string>(); string net = null;
        foreach (XmlElement c in Conditions(doc))
        {
            if (!Bool(Child(c, "Enabled"))) continue;
            bool once = Bool(Child(c, "RunOnce"));
            string t = CondType(c).ToLowerInvariant();
            switch (t)
            {
                case "costartinterval":
                {
                    string n = (Child(c, "Interval") ?? "1").Trim();
                    string u = (Child(c, "IntervalUnit") ?? "").Trim().ToLowerInvariant();
                    string unit = u == "ivminute" ? "minute" : u == "ivhour" ? "hour" : u == "ivday" ? "day" : u == "ivweek" ? "week" : u == "ivmonth" ? "month" : "interval";
                    rest.Add("Start every " + (n == "1" ? unit : n + " " + unit + "s"));
                    break;
                }
                case "costartup": rest.Add(once ? "Once on ACMP Agent start" : "Always on ACMP Agent start"); break;
                case "cologin": rest.Add(once ? "Once on user login" : "On user login"); break;
                case "conow": rest.Add("As soon as possible"); break;
                case "comaintenancewindow": rest.Add("Within Maintenance Windows"); break;
                case "coafterjoblistload": rest.Add("After loading the job list"); break;
                case "cotimeframe": rest.Add("from " + TimeText(Child(c, "StartTime")) + " to " + TimeText(Child(c, "EndTime")) + (once ? " run once" : "")); break;
                case "cospecifictime": rest.Add("at " + TimeText(Child(c, "TimeValue"))); break;
                case "codayofweek":
                {
                    var d = new List<string>();
                    foreach (string o in Options(c, "WeekDays")) { string v; d.Add(DaysEn.TryGetValue(o, out v) ? v : o); }
                    if (string.Equals((Child(c, "WeekMode") ?? "").Trim(), "wmSpecificOccurrence", StringComparison.OrdinalIgnoreCase))
                    {
                        var oc = new List<string>();
                        foreach (string o in Options(c, "DayOccurrences")) { string v; oc.Add(Occurrences.TryGetValue(o, out v) ? (v == "letzten" ? "last" : v) : o); }
                        lead.Add("On every " + string.Join(", ", oc.ToArray()) + " " + string.Join(", ", d.ToArray()));
                    }
                    else lead.Add("Every week on " + string.Join(", ", d.ToArray()));
                    break;
                }
                case "conetworkconnection": net = "only when connection is not metered"; break;
                default: rest.Add(CondType(c)); break;
            }
        }
        var all = new List<string>(lead); all.AddRange(rest);
        string s = string.Join(" ", all.ToArray());
        if (net != null) s = (s.Length > 0 ? s + " and " : "") + net;
        if (s.Length > 0) s = char.ToUpperInvariant(s[0]) + s.Substring(1);
        return s.Length > 0 ? s : "Disabled";
    }

    // Neue Pruefsumme fuer eine vom MCP geaenderte Startbedingung (ACMPs eigene Formel ist nicht bekannt): MD5 ueber UTF-16LE, 32 Hex gross
    public static string NewChecksum(string xml)
    {
        using (var md5 = MD5.Create())
        {
            byte[] h = md5.ComputeHash(Encoding.Unicode.GetBytes(xml ?? ""));
            var sb = new StringBuilder(); foreach (byte b in h) sb.Append(b.ToString("X2")); return sb.ToString();
        }
    }

    // ---------- Pruefsumme wie die Console (wo bekannt) ----------
    // Die Formel von ConditionCheckSum ist unbekannt. Belegt (Mitschnitte und DB, 22.10.2026): die Bedingungs-Id geht NICHT ein (ein vorhandener Job und
    // die Test-Bedingung aus dem zweiten Mitschnitt haben verschiedene Ids und denselben Wert, 10 Jobs teilen ihn), Action schon (erster Mitschnitt: Anlegen und
    // Einschalten unterscheiden sich nur in Action und haben verschiedene Werte). Einige DB-Zeilen mit eaNone tragen Werte, die nicht zu ihrem
    // XML passen (offenbar spaeter umgeschrieben). Deshalb nur bei exakt gleichem XML ohne Id-Werte: zuerst die Werte frischer Console-Speicherungen
    // aus dem zweiten Mitschnitt, sonst der Wert aus der DB, wenn alle Zeilen gleichen Inhalts ihn teilen und keine davon der MCP selbst berechnet hat.
    static readonly object[][] ConsoleChecksums = {
        new object[] { "once", 1, false, "5D70DD46B91267627BD4BABAD99D42E0" },
        new object[] { "startup", 1, false, "7FCCFC6179A826D9F4A36C78E8BA77A2" },
        new object[] { "daily", 1, true, "AD89B9D5787979C2152C47B78239CD80" },
        new object[] { "maintenance", 1, false, "B52254AE494C5785CD7F90DA62059A0F" } };
    public const string OwnChecksumSource = "eigene (MD5, Formel der Console unbekannt)";
    static readonly Regex HexRx = new Regex("^[0-9A-F]{32}$");

    // Startbedingung ohne Id-Werte, einheitlich serialisiert (Vergleichsschluessel); null = nicht lesbar
    public static string ConditionKey(string xml)
    {
        if (string.IsNullOrEmpty(xml)) return null;
        try
        {
            var d = new XmlDocument(); d.LoadXml(xml);
            foreach (XmlNode n in d.GetElementsByTagName("Id")) n.InnerText = "";
            return d.OuterXml.Replace(" />", "/>");
        }
        catch { return null; }
    }

    // Bekannte Pruefsumme der Console fuer genau diese Startbedingung, sonst null. rows: je Job { Name, Conditions, ConditionCheckSum };
    // own: Pruefsummen, die der MCP selbst berechnet hat (zaehlen nicht)
    public static string KnownChecksum(string xml, IEnumerable<string[]> rows, ICollection<string> own, out string source)
    {
        source = null;
        string key = ConditionKey(xml);
        if (key == null) return null;
        foreach (object[] k in ConsoleChecksums)
            if (ConditionKey(ConditionXml(null, (string)k[0], (int)k[1], (bool)k[2], "{00000000-0000-0000-0000-000000000000}")) == key)
            { source = "wie die Console (Mitschnitt 22.10.2026)"; return (string)k[3]; }
        if (rows == null) return null;
        string val = null, example = null; int n = 0;
        foreach (string[] r in rows)
        {
            if (r == null || r.Length < 3 || r[2] == null || !HexRx.IsMatch(r[2])) continue;
            if (own != null && own.Contains(r[2])) continue;
            if (ConditionKey(r[1]) != key) continue;
            if (val == null) { val = r[2]; example = r[0]; }
            else if (val != r[2]) { source = null; return null; }   // gleicher Inhalt, verschiedene Werte: nicht eindeutig
            n++;
        }
        if (val == null) return null;
        source = "wie " + n + " Job(s) mit gleicher Startbedingung in der DB (z.B. \"" + example + "\")";
        return val;
    }

    static readonly Regex BackupRx = new Regex(@"^\d{8}-\d{6}-\d{3}_(disable|enable|delete|condition|restriction)_[0-9A-F]{8}\.json$");
    public static bool IsBackupName(string s) { return s != null && BackupRx.IsMatch(s); }

    // ---------- Startbedingung neu setzen (Bausteine wortgleich aus dem Mitschnitt 22.10.2026) ----------
    // once = "Sobald wie moeglich" (einmalig), startup = einmalig beim Agentenstart, daily/hourly = alle <every> Tage/Stunden,
    // maintenance = in jedem Wartungsfenster
    public static readonly string[] ConditionKinds = { "once", "startup", "daily", "hourly", "maintenance" };

    public static string ConditionKind(string s)
    {
        if (s == null) return null;
        switch (s.Trim().ToLowerInvariant().Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("-", "").Replace(" ", ""))
        {
            case "once": case "now": case "einmalig": case "sofort": case "sobaldwiemoeglich": case "asap": return "once";
            case "startup": case "agentstart": case "agentenstart": case "start": return "startup";
            case "daily": case "day": case "taeglich": return "daily";
            case "hourly": case "hour": case "stuendlich": return "hourly";
            case "maintenance": case "maintenancewindow": case "wartungsfenster": case "wartungszeitfenster": return "maintenance";
        }
        return null;
    }

    const string WakeUpXml = "<WakeUpConfig><TStartConditionWakeUpConfig><IsWakeUpActive>false</IsWakeUpActive><WakeUpOffsetHours>0</WakeUpOffsetHours>" +
        "<WakeUpOffsetMinutes>0</WakeUpOffsetMinutes><WOLPort>7</WOLPort><UseBatching>false</UseBatching><RelativeBatchSize>10</RelativeBatchSize>" +
        "<WaitPeriodBetweenBatchesInSeconds>5</WaitPeriodBetweenBatchesInSeconds><Action>eaNone</Action></TStartConditionWakeUpConfig></WakeUpConfig>";
    const string TimeFrameOffXml = "<CheckTimeFrame>false</CheckTimeFrame><TimeFrame><TStartConditionOnTimeFrameBase><StartTime>00:00:00.000</StartTime>" +
        "<EndTime>00:00:00.000</EndTime><IsRandomizedTimeFrame>false</IsRandomizedTimeFrame><Id/><ConditionType>coTimeFrame</ConditionType><RunOnce>false</RunOnce>" +
        "<Enabled>false</Enabled><ReEvaluateConditionBeforeRun>false</ReEvaluateConditionBeforeRun><Action>eaEdit</Action></TStartConditionOnTimeFrameBase></TimeFrame>";
    const string FacadeTailXml = "<AllowedExecutionDelays>0</AllowedExecutionDelays><SupportedExecutionDelayOptions><option>doAsSoonAsPossible</option>" +
        "<option>doDelay10Minutes</option><option>doDelay30Minutes</option><option>doDelayOneHour</option><option>doDelayFourHours</option>" +
        "<option>doRunAtShutdown</option></SupportedExecutionDelayOptions><DelayDescription/>";

    static string CondCommon(string id, string type, bool once)
    {
        return "<Id>" + id + "</Id><ConditionType>" + type + "</ConditionType><RunOnce>" + (once ? "true" : "false") + "</RunOnce><Enabled>true</Enabled>" +
               "<ReEvaluateConditionBeforeRun>false</ReEvaluateConditionBeforeRun><Action>eaAdd</Action>";
    }

    static void CheckCondition(string kind, int every, bool ignoreMw)
    {
        if (Array.IndexOf(ConditionKinds, kind) < 0) throw new Exception("Unbekannte Startbedingung '" + kind + "' (erlaubt: " + string.Join(", ", ConditionKinds) + ").");
        int max = kind == "daily" ? 365 : kind == "hourly" ? 24 : 1;
        if (every < 1 || every > max) throw new Exception(kind == "daily" || kind == "hourly" ? "every muss 1-" + max + " sein (" + every + ")." : "every gibt es nur bei daily und hourly.");
        if (ignoreMw && kind == "maintenance") throw new Exception("'In Wartungsfenstern' und 'Wartungsfenster ignorieren' schliessen sich aus.");
    }

    // Element der neuen Bedingung (Id mit geschweiften Klammern, wie die Console)
    public static string ConditionElement(string kind, int every, string id)
    {
        switch (kind)
        {
            case "once": return "<TStartConditionOnNow>" + WakeUpXml + CondCommon(id, "coNow", true) + "</TStartConditionOnNow>";
            case "startup": return "<TStartConditionOnStartUp>" + TimeFrameOffXml + CondCommon(id, "coStartUp", true) + "</TStartConditionOnStartUp>";
            case "daily":
            case "hourly":
                return "<TStartConditionOnStartInterval><Interval>" + every + "</Interval><IntervalUnit>" + (kind == "daily" ? "ivDay" : "ivHour") + "</IntervalUnit>" +
                       TimeFrameOffXml + CondCommon(id, "coStartInterval", false) + "</TStartConditionOnStartInterval>";
            case "maintenance": return "<TStartConditionMaintenanceWindow>" + CondCommon(id, "coMaintenanceWindow", false) + "</TStartConditionMaintenanceWindow>";
        }
        throw new Exception("Unbekannte Startbedingung '" + kind + "'.");
    }

    // ConditionText wie die Console 6.10.4 ihn schreibt ("As soon as possible", "Once upon Agent start", "Start every day (ignores Maintenance Windows)",
    // "Within Maintenance Windows"; "Start every hour"/"Start every 3 days" aus der DB)
    public static string ConditionTextFor(string kind, int every, bool ignoreMw)
    {
        string t;
        switch (kind)
        {
            case "once": t = "As soon as possible"; break;
            case "startup": t = "Once upon Agent start"; break;
            case "daily": t = every == 1 ? "Start every day" : "Start every " + every + " days"; break;
            case "hourly": t = every == 1 ? "Start every hour" : "Start every " + every + " hours"; break;
            case "maintenance": t = "Within Maintenance Windows"; break;
            default: throw new Exception("Unbekannte Startbedingung '" + kind + "'.");
        }
        return ignoreMw ? t + " (ignores Maintenance Windows)" : t;
    }

    // Neue Startbedingung fuer einen Job: Huelle des Jobs behalten (Verschiebe-Optionen), Liste durch die eine neue Bedingung ersetzen.
    // Ohne lesbare Huelle die Huelle der Console (Mitschnitt).
    public static string ConditionXml(string existingXml, string kind, int every, bool ignoreMw, string id)
    {
        CheckCondition(kind, every, ignoreMw);
        string elem = ConditionElement(kind, every, id), text = ConditionTextFor(kind, every, ignoreMw);
        XmlDocument doc = null;
        if (!string.IsNullOrEmpty(existingXml)) { try { doc = LoadConditions(existingXml); } catch { doc = null; } }
        if (doc == null)
            return "<TStartConditionFacade><TStartConditionList>" + elem + "</TStartConditionList><CanIgnoreMaintenanceTimes>" + (ignoreMw ? "true" : "false") +
                   "</CanIgnoreMaintenanceTimes><ConditionText>" + SecurityElement.Escape(text) + "</ConditionText>" + FacadeTailXml + "<Action>eaEdit</Action></TStartConditionFacade>";
        XmlElement root = doc.DocumentElement, list = root["TStartConditionList"];
        if (list == null) { list = doc.CreateElement("TStartConditionList"); root.PrependChild(list); }
        list.RemoveAll();
        XmlDocumentFragment frag = doc.CreateDocumentFragment(); frag.InnerXml = elem; list.AppendChild(frag);
        SetKidAfter(root, "CanIgnoreMaintenanceTimes", ignoreMw ? "true" : "false", list);
        SetKidAfter(root, "ConditionText", text, root["CanIgnoreMaintenanceTimes"]);
        SetKid(root, "Action", "eaEdit");
        return doc.OuterXml;
    }

    // Wie SetKid, ein fehlendes Element aber hinter 'after' einfuegen (Reihenfolge der Console)
    static void SetKidAfter(XmlElement e, string name, string value, XmlNode after)
    {
        XmlElement c = e[name];
        if (c == null) { c = e.OwnerDocument.CreateElement(name); if (after != null) e.InsertAfter(c, after); else e.AppendChild(c); }
        c.InnerText = value;
    }

    public static string RestrictionText(int r) { return r == 1 ? "nur Clients der Betriebssystemfamilie des Containers" : r == 0 ? "alle Clients erlaubt" : "unbekannt (" + r + ")"; }
    public static string OsFamilyText(int f) { return f == 0 ? "Windows" : f == 1 ? "Unix (Linux/macOS)" : "unbekannt (" + f + ")"; }

    // Darf der Job geschaltet/geloescht werden? null = ja, sonst Grund
    public static string RefuseSet(int kind, bool manual)
    {
        if (manual) return "ist ein einmaliger, von Hand gestarteter Job (IsManualJob) - kein Dauerauftrag";
        if (kind == 11) return "ist eine Agentenplaner-Aufgabe (Scanner) - die gehoert zur Vorlage im Agentenplaner (acmp_agent_tasks), bitte in der Console aendern";
        return null;
    }

    public static string RefuseDelete(int kind, bool manual, int templateLinks)
    {
        string s = RefuseSet(kind, manual); if (s != null) return s;
        if (Array.IndexOf(DeletableKinds, kind) < 0) return "ist ein " + KindName(kind) + " - Loeschen per MCP nur fuer Client Commands, WUM-Sammlungen und Managed Software an Containern, sonst in der Console";
        if (templateLinks != 1) return "haengt an " + templateLinks + " Job-Vorlagen (erwartet genau 1) - bitte in der Console loeschen";
        return null;
    }

    // ================= Vorpruefung =================
    static void Add(JArray checks, string check, string result, string detail)
    {
        var o = new JObject(); o["check"] = check; o["result"] = result; o["detail"] = detail; checks.Add(o);
    }

    static string Scalar(SqlConnection c, string sql)
    {
        var q = c.CreateCommand(); q.CommandText = sql; q.CommandTimeout = SqlTimeout;
        object o = q.ExecuteScalar();
        return o == null || o is DBNull ? null : Convert.ToString(o, CultureInfo.InvariantCulture);
    }

    static bool HasPerm(SqlConnection c, string obj, string perm, string column)
    {
        var q = c.CreateCommand();
        q.CommandText = column == null ? "SELECT HAS_PERMS_BY_NAME(@o, 'OBJECT', @p)" : "SELECT HAS_PERMS_BY_NAME(@o, 'OBJECT', @p, @c, 'COLUMN')";
        q.Parameters.AddWithValue("@o", obj); q.Parameters.AddWithValue("@p", perm);
        if (column != null) q.Parameters.AddWithValue("@c", column);
        object o = q.ExecuteScalar();
        return o != null && !(o is DBNull) && Convert.ToInt32(o) == 1;
    }

    public JObject WriterPreflight(bool refresh)
    {
        if (!WriterConfigOk) return null;
        lock (_wpfLock)
        {
            if (!refresh && _wpf != null && (DateTime.UtcNow - _wpfAt).TotalMinutes < 60) return _wpf;
            string v = null;
            try { v = _acmpVersion != null ? _acmpVersion() : null; } catch { }
            _wpf = RunWriterPreflight(v);
            _wpfAt = DateTime.UtcNow;
            _log("Job-Writer Vorpruefung (ACMP " + (v ?? "?") + "): " + (string)_wpf["summary"]);
            return _wpf;
        }
    }

    JObject RunWriterPreflight(string v)
    {
        var checks = new JArray();
        bool setOk = false, delOk = false, restOk = false, restrOk = false;
        try
        {
            string mm = null;
            if (!string.IsNullOrEmpty(v)) { string[] p = v.Split('.'); if (p.Length >= 2) mm = p[0] + "." + p[1]; }
            if (mm != null && Array.IndexOf(WriterTestedVersions, mm) >= 0) Add(checks, "ACMP-Version", "ok", v);
            else Add(checks, "ACMP-Version", _allowUntested ? "warn" : "block", (mm == null ? "nicht ermittelbar" : v + " ist nicht getestet") + " (getestet: " + string.Join(", ", WriterTestedVersions) + ")");
            using (var rc = new SqlConnection(_readCs))
            using (var wc = new SqlConnection(_writeCs))
            {
                rc.Open();
                try { wc.Open(); }
                catch (Exception ex) { Add(checks, "Schreib-Login", "block", "Anmeldung fehlgeschlagen: " + ex.Message); return WriterResult(checks, false, false, false, false); }
                string a = Scalar(rc, "SELECT @@SERVERNAME + '/' + DB_NAME()"), b = Scalar(wc, "SELECT @@SERVERNAME + '/' + DB_NAME()");
                bool same = string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
                Add(checks, "Datenbank", same ? "ok" : "block", a + (same ? " (Lese- und Schreib-Login gleich)" : " / Schreib-Login auf " + b));
                string jc = Scalar(rc, "SELECT STUFF((SELECT ',' + COLUMN_NAME + ':' + DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='SYS_Jobs' AND COLUMN_NAME IN ('Conditions','ConditionCheckSum','JobAction') ORDER BY COLUMN_NAME FOR XML PATH('')), 1, 1, '')");
                Add(checks, "Tabelle SYS_Jobs", jc == "ConditionCheckSum:varchar,Conditions:xml,JobAction:xml" ? "ok" : "block", "Spalten: " + (jc ?? "keine") + " (erwartet ConditionCheckSum:varchar,Conditions:xml,JobAction:xml)");
                string tc = Scalar(rc, "SELECT STUFF((SELECT ',' + COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='SYS_Templates_Jobs' ORDER BY COLUMN_NAME FOR XML PATH('')), 1, 1, '')");
                Add(checks, "Tabelle SYS_Templates_Jobs", tc == "Id,JobId,JobKind,TemplateId" ? "ok" : "block", "Spalten: " + (tc ?? "keine") + " (erwartet Id,JobId,JobKind,TemplateId)");
                // Trigger auf den beteiligten Tabellen (mit dem Schreib-Login gelesen: Metadaten sieht nur, wer Rechte auf die Tabelle hat)
                var trg = new List<string>();
                var tq = wc.CreateCommand();
                tq.CommandText = "SELECT OBJECT_NAME(t.parent_id) + '.' + t.name FROM sys.triggers t WHERE t.parent_id IN (OBJECT_ID('dbo.SYS_Jobs'), OBJECT_ID('dbo.SYS_Templates_Jobs'), OBJECT_ID('dbo.SYS_Jobs_Logs')) AND t.is_disabled = 0";
                using (var r = tq.ExecuteReader()) while (r.Read()) trg.Add(r.GetString(0));
                var unknown = new List<string>(); bool delTrigger = false;
                foreach (string t in trg)
                {
                    string n = t.Substring(t.IndexOf('.') + 1);
                    if (Array.IndexOf(KnownTriggers, n) < 0) unknown.Add(t); else if (t.StartsWith("SYS_Templates_Jobs.")) delTrigger = true;
                }
                Add(checks, "Trigger", unknown.Count == 0 ? "ok" : "block", unknown.Count == 0 ? "nur bekannte: " + (trg.Count > 0 ? string.Join(", ", trg.ToArray()) : "keine sichtbar") : "unbekannte Trigger: " + string.Join(", ", unknown.ToArray()) + " - erst pruefen");
                Add(checks, "Loesch-Trigger", delTrigger ? "ok" : "warn", delTrigger ? "TG_Delete_SYS_Templates_Jobs loescht den Job aus SYS_Jobs und setzt die Protokolle auf fertig"
                    : "TG_Delete_SYS_Templates_Jobs fuer den Schreib-Login nicht sichtbar oder fehlt - acmp_job_delete gesperrt (grant-jobs-writer.sql)");
                bool selJobs = HasPerm(wc, "dbo.SYS_Jobs", "SELECT", null);
                bool updCond = HasPerm(wc, "dbo.SYS_Jobs", "UPDATE", "Conditions") && HasPerm(wc, "dbo.SYS_Jobs", "UPDATE", "ConditionCheckSum");
                bool selLogs = HasPerm(wc, "dbo.SYS_Jobs_Logs", "SELECT", null);
                bool updLogText = HasPerm(wc, "dbo.SYS_Jobs_Logs", "UPDATE", "ConditionText");
                bool updLogState = HasPerm(wc, "dbo.SYS_Jobs_Logs", "UPDATE", "State");
                bool selTj = HasPerm(wc, "dbo.SYS_Templates_Jobs", "SELECT", null), delTj = HasPerm(wc, "dbo.SYS_Templates_Jobs", "DELETE", null), insTj = HasPerm(wc, "dbo.SYS_Templates_Jobs", "INSERT", null);
                bool insJobs = HasPerm(wc, "dbo.SYS_Jobs", "INSERT", null), selTpl = HasPerm(wc, "dbo.SYS_Templates", "SELECT", null);
                setOk = selJobs && updCond && selLogs && updLogText;
                Add(checks, "Rechte Schalten", setOk ? "ok" : "warn", setOk ? "SELECT/UPDATE(Conditions, ConditionCheckSum) auf SYS_Jobs, UPDATE(ConditionText) auf SYS_Jobs_Logs vorhanden"
                    : "fehlen - acmp_job_set_enabled gesperrt (grant-jobs-writer.sql)");
                delOk = selJobs && selTj && delTj && delTrigger;
                Add(checks, "Rechte Loeschen", delOk ? "ok" : "warn", delOk ? "SELECT/DELETE auf SYS_Templates_Jobs vorhanden" : "fehlen - acmp_job_delete gesperrt (grant-jobs-writer.sql)");
                restOk = setOk && insJobs && insTj && selTj && selTpl && updLogState;
                Add(checks, "Rechte Zuruecknehmen", restOk ? "ok" : "warn", restOk ? "INSERT auf SYS_Jobs und SYS_Templates_Jobs, UPDATE(State) auf SYS_Jobs_Logs vorhanden" : "fehlen - acmp_job_restore gesperrt (grant-jobs-writer.sql)");
                // Client-Beschraenkung: eigene Pruefung, sperrt nur acmp_container_set_restriction
                string cc = Scalar(rc, "SELECT STUFF((SELECT ',' + COLUMN_NAME + ':' + DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='CLT_CONTAINER_STRUCTURE' AND COLUMN_NAME IN ('ClientRestriction','OsFamily') ORDER BY COLUMN_NAME FOR XML PATH('')), 1, 1, '')");
                bool colsOk = cc == "ClientRestriction:tinyint,OsFamily:tinyint";
                Add(checks, "Tabelle CLT_CONTAINER_STRUCTURE", colsOk ? "ok" : "warn", "Spalten: " + (cc ?? "keine") + (colsOk ? "" : " (erwartet ClientRestriction:tinyint,OsFamily:tinyint) - acmp_container_set_restriction gesperrt"));
                bool selCont = HasPerm(wc, "dbo.CLT_CONTAINER_STRUCTURE", "SELECT", null), updRestr = HasPerm(wc, "dbo.CLT_CONTAINER_STRUCTURE", "UPDATE", "ClientRestriction");
                var ctrg = new List<string>(); var cunknown = new List<string>();
                if (selCont)
                {
                    var cq = wc.CreateCommand();
                    cq.CommandText = "SELECT t.name FROM sys.triggers t WHERE t.parent_id = OBJECT_ID('dbo.CLT_CONTAINER_STRUCTURE') AND t.is_disabled = 0";
                    using (var r = cq.ExecuteReader()) while (r.Read()) ctrg.Add(r.GetString(0));
                    foreach (string t in ctrg) if (Array.IndexOf(KnownContainerTriggers, t) < 0) cunknown.Add(t);
                    Add(checks, "Trigger Container", cunknown.Count == 0 ? "ok" : "warn", cunknown.Count == 0 ? "nur bekannte: " + (ctrg.Count > 0 ? string.Join(", ", ctrg.ToArray()) : "keine")
                        : "unbekannte Trigger: " + string.Join(", ", cunknown.ToArray()) + " - acmp_container_set_restriction gesperrt, erst pruefen");
                }
                restrOk = colsOk && selCont && updRestr && cunknown.Count == 0;
                Add(checks, "Rechte Client-Beschraenkung", selCont && updRestr ? "ok" : "warn", selCont && updRestr ? "SELECT und UPDATE(ClientRestriction) auf CLT_CONTAINER_STRUCTURE vorhanden"
                    : "fehlen - acmp_container_set_restriction gesperrt (grant-jobs-writer2.sql)");
                var extra = new List<string>();
                if (HasPerm(wc, "dbo.SYS_Jobs", "DELETE", null)) extra.Add("DELETE auf SYS_Jobs (das Tool loescht nur ueber SYS_Templates_Jobs)");
                if (HasPerm(wc, "dbo.SYS_Jobs", "UPDATE", "JobAction")) extra.Add("UPDATE auf SYS_Jobs.JobAction");
                if (HasPerm(wc, "dbo.CLT_CONTAINER_STRUCTURE", "UPDATE", "SQLFilter")) extra.Add("UPDATE auf CLT_CONTAINER_STRUCTURE.SQLFilter");
                if (Scalar(wc, "SELECT CAST(IS_ROLEMEMBER('db_owner') AS varchar(5))") == "1") extra.Add("Mitglied von db_owner");
                if (extra.Count > 0) Add(checks, "Rechte (zu viele)", "warn", string.Join(", ", extra.ToArray()));
            }
        }
        catch (Exception ex) { Add(checks, "Pruefung", "block", "Abgebrochen: " + ex.Message); }
        return WriterResult(checks, setOk, delOk, restOk, restrOk);
    }

    static JObject WriterResult(JArray checks, bool setOk, bool delOk, bool restOk, bool restrOk)
    {
        var blocks = new List<string>(); int warns = 0;
        foreach (JObject c in checks)
        {
            if ((string)c["result"] == "block") blocks.Add((string)c["check"] + ": " + (string)c["detail"]);
            else if ((string)c["result"] == "warn") warns++;
        }
        bool ok = blocks.Count == 0;
        var o = new JObject();
        o["ok"] = ok;
        o["checkedLocal"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        o["summary"] = ok ? "bestanden" + (warns > 0 ? " (" + warns + " Hinweis(e))" : "") : "GESPERRT: " + string.Join(" | ", blocks.ToArray());
        o["setEnabledTool"] = ok && setOk; o["deleteTool"] = ok && delOk; o["restoreTool"] = ok && restOk;
        o["conditionTool"] = ok && setOk; o["restrictionTool"] = ok && restrOk;
        o["checks"] = checks;
        return o;
    }

    public JObject WriterStatus(bool refresh)
    {
        var o = new JObject();
        o["enabled"] = WriterConfigOk;
        if (!WriterConfigOk) { o["status"] = "aus"; o["reason"] = WriterDisabledReason; return o; }
        JObject pf = WriterPreflight(refresh);
        o["status"] = (bool)pf["ok"] ? "aktiv" : "gesperrt";
        o["allowedRequesters"] = new JArray(new List<string>(_allowedReq).ToArray());
        o["setEnabledTool"] = pf["setEnabledTool"]; o["deleteTool"] = pf["deleteTool"]; o["restoreTool"] = pf["restoreTool"];
        o["conditionTool"] = pf["conditionTool"]; o["restrictionTool"] = pf["restrictionTool"];
        o["sicherungen"] = _backupDir;
        o["vorpruefung"] = pf;
        return o;
    }

    void CheckWrite(string requester, string tool)
    {
        if (!WriterConfigOk) throw new Exception("Jobs schreiben ist ausgeschaltet: " + WriterDisabledReason + ". Nur dryRun moeglich - sonst in der ACMP-Console.");
        if (requester == null || !_allowedReq.Contains(requester)) throw new Exception("Bediener '" + requester + "' darf Jobs nicht aendern (jobWriter.allowedRequesters bzw. ccWriter.allowedRequesters).");
        JObject pf = WriterPreflight(false);
        if (pf == null || !(bool)pf["ok"]) throw new Exception("Jobs schreiben gesperrt - Vorpruefung nicht bestanden: " + (pf != null ? (string)pf["summary"] : "keine Pruefung") + ". Nichts geschrieben.");
        string key = tool == "acmp_job_delete" ? "deleteTool" : tool == "acmp_job_restore" ? "restoreTool" : tool == "acmp_job_set_condition" ? "conditionTool"
                   : tool == "acmp_container_set_restriction" ? "restrictionTool" : "setEnabledTool";
        if (pf[key] == null || !(bool)pf[key]) throw new Exception(tool + " gesperrt - Rechte des Schreib-Logins fehlen (" + (key == "restrictionTool" ? "grant-jobs-writer2.sql" : "grant-jobs-writer.sql") +
            ", Details: acmp_jobs mit status=true). Nichts geschrieben.");
    }

    // ================= Lesen fuer den Plan =================
    // Stand eines Jobs (null = gibt es nicht). Ohne JobAction (Kennwoerter); die steht nur in der Sicherung vor dem Loeschen.
    JObject LookupJob(SqlConnection c, string id)
    {
        var q = Cmd(c,
            "SELECT CAST(j.Id AS varchar(36)), j.Name, j.JobKind, j.IsManualJob, CAST(j.ContainerId AS varchar(36)), CAST(j.Conditions AS nvarchar(max)), j.ConditionCheckSum, s.Path, " +
            "(SELECT COUNT(*) FROM CLT_CONTAINER_ITEMS ci JOIN CLT_CLIENTS_TABLE cl ON cl.CLIENTID = ci.LinkID WHERE ci.CONTAINERID = j.ContainerId AND cl.IsRetired = 0), " +
            "(SELECT COUNT(*) FROM SYS_Templates_Jobs tj WHERE tj.JobId = j.Id), " +
            "(SELECT TOP 1 CAST(tj.Id AS varchar(36)) FROM SYS_Templates_Jobs tj WHERE tj.JobId = j.Id), " +
            "(SELECT TOP 1 CAST(tj.TemplateId AS varchar(36)) FROM SYS_Templates_Jobs tj WHERE tj.JobId = j.Id), " +
            "(SELECT TOP 1 tj.JobKind FROM SYS_Templates_Jobs tj WHERE tj.JobId = j.Id), " +
            "(SELECT COUNT(*) FROM SYS_Jobs_Logs_Items i WHERE i.JobId = j.Id AND i.State BETWEEN 2 AND 5 AND i.LastActivity >= CAST(GETDATE() AS float) + 2 - 7), " +
            "(SELECT COUNT(*) FROM SYS_Jobs_Logs_Items i WHERE i.JobId = j.Id AND i.State < 2), " +
            "(SELECT TOP 1 l.ConditionText FROM SYS_Jobs_Logs l WHERE l.JobId = j.Id), (SELECT TOP 1 l.State FROM SYS_Jobs_Logs l WHERE l.JobId = j.Id), " +
            "CASE j.JobKind WHEN 4 THEN (SELECT TOP 1 x.DESCRIPTION FROM SYS_SCRIPTS x WHERE TRY_CONVERT(uniqueidentifier, x.SCRIPTID) = j.ContentId ORDER BY x.ScriptVersion DESC) " +
            " WHEN 59 THEN (SELECT TOP 1 m.Name FROM SYS_ManagedSoftware_Catalog m WHERE m.Id = j.ContentId) " +
            " WHEN 58 THEN (SELECT TOP 1 w.Name FROM SYS_Wum_Collections w WHERE w.Id = j.ContentId) END " +
            "FROM SYS_Jobs j LEFT JOIN CLT_CONTAINER_STRUCTURE s ON s.ContainerID = j.ContainerId WHERE j.Id = @id");
        q.Parameters.AddWithValue("@id", new Guid(id.Trim('{', '}')));
        using (var r = q.ExecuteReader())
        {
            if (!r.Read()) return null;
            var o = new JObject();
            o["id"] = Gid(r, 0); o["name"] = Str(r, 1) ?? ""; o["kind"] = Int(r, 2); o["manual"] = Int(r, 3) == 1;
            o["containerId"] = Gid(r, 4); o["conditions"] = Str(r, 5); o["checksum"] = Str(r, 6) ?? "";
            o["path"] = Str(r, 7); o["members"] = r.IsDBNull(4) ? -1 : Int(r, 8);
            o["templateLinks"] = Int(r, 9); o["templateJobId"] = Gid(r, 10); o["templateId"] = Gid(r, 11); o["templateJobKind"] = Int(r, 12);
            o["runs7"] = Int(r, 13); o["waiting"] = Int(r, 14);
            o["logText"] = Str(r, 15); o["logState"] = Int(r, 16); o["content"] = Str(r, 17);
            return o;
        }
    }

    // Vollstaendige Zeile fuer die Sicherung vor dem Loeschen (mit JobAction - bleibt in der Sicherungsdatei auf dem ACMP-Server)
    JObject FullJobRow(SqlConnection c, string id)
    {
        var q = Cmd(c, "SELECT CAST(Id AS varchar(36)), Name, Description, Priority, CAST(Conditions AS nvarchar(max)), ConditionCheckSum, CAST(JobAction AS nvarchar(max)), " +
            "CanRunParallel, CAST(ContentId AS varchar(36)), JobKind, CAST(ContainerId AS varchar(36)), Origin, IsManualJob, CAST(ServerId AS varchar(36)), CreationDate, CAST(FolderId AS varchar(36)) " +
            "FROM SYS_Jobs WHERE Id = @id");
        q.Parameters.AddWithValue("@id", new Guid(id.Trim('{', '}')));
        using (var r = q.ExecuteReader())
        {
            if (!r.Read()) return null;
            var o = new JObject();
            o["Id"] = Str(r, 0); o["Name"] = Str(r, 1); o["Description"] = Str(r, 2); o["Priority"] = Int(r, 3);
            o["Conditions"] = Str(r, 4); o["ConditionCheckSum"] = Str(r, 5); o["JobAction"] = Str(r, 6); o["CanRunParallel"] = Int(r, 7) == 1;
            o["ContentId"] = Str(r, 8); o["JobKind"] = Int(r, 9); o["ContainerId"] = Str(r, 10); o["Origin"] = Int(r, 11); o["IsManualJob"] = Int(r, 12) == 1;
            o["ServerId"] = Str(r, 13); o["CreationDate"] = r.IsDBNull(14) ? null : (JToken)Dbl(r, 14); o["FolderId"] = Str(r, 15);
            return o;
        }
    }

    static string JobLabel(JObject j)
    {
        return "\"" + (string)j["name"] + "\" " + j["id"] + " (" + KindName((int)j["kind"]) + (IsNullTok(j["content"]) ? "" : ": " + (string)j["content"]) + ")";
    }

    static bool IsNullTok(JToken t) { return t == null || t.Type == JTokenType.Null; }

    static string TargetText(JObject j)
    {
        return IsNullTok(j["containerId"]) ? "kein Container" : (IsNullTok(j["path"]) ? j["containerId"] + " (Container fehlt)" : (string)j["path"] + " (" + j["members"] + " Mitglieder)");
    }

    // Alle Jobs mit 32-Hex-Pruefsumme (fuer KnownChecksum), ohne JobAction
    List<string[]> LoadChecksumRows(SqlConnection c)
    {
        var l = new List<string[]>();
        var q = Cmd(c, "SELECT Name, CAST(Conditions AS nvarchar(max)), ConditionCheckSum FROM SYS_Jobs WHERE LEN(ISNULL(ConditionCheckSum, '')) = 32");
        using (var r = q.ExecuteReader()) while (r.Read()) l.Add(new[] { Str(r, 0) ?? "", Str(r, 1), Str(r, 2) });
        return l;
    }

    // Pruefsummen, die der MCP selbst berechnet hat (aus den Sicherungen: after.checksumQuelle "eigene ..." oder ohne Angabe)
    HashSet<string> OwnChecksums()
    {
        var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(_backupDir) || !Directory.Exists(_backupDir)) return s;
        foreach (string f in Directory.GetFiles(_backupDir, "*.json"))
        {
            try
            {
                JObject b = JObject.Parse(File.ReadAllText(f));
                JObject after = b["after"] as JObject;
                if (after == null || !IsStr(after["checksum"])) continue;
                string src = IsStr(after["checksumQuelle"]) ? (string)after["checksumQuelle"] : null;
                if (src == null || src.StartsWith("eigene")) s.Add((string)after["checksum"]);
            }
            catch { }
        }
        return s;
    }

    static string Cs8(string cs) { return string.IsNullOrEmpty(cs) ? "(leer)" : cs.Length > 8 ? cs.Substring(0, 8) + "..." : cs; }

    // Pruefsumme fuer eine vom MCP gebaute Startbedingung: bekannter Console-Wert, sonst eigener
    static string ChecksumFor(string xml, List<string[]> rows, HashSet<string> own, out string source)
    {
        string cs = KnownChecksum(xml, rows, own, out source);
        if (cs != null) return cs;
        source = OwnChecksumSource;
        return NewChecksum(xml);
    }

    // Letzte Sicherung des MCP, die genau den jetzigen Stand erzeugt hat (fuer "einschalten = zuruecknehmen")
    JObject FindOwnBackup(string jobId, string kind, string currentChecksum)
    {
        if (string.IsNullOrEmpty(_backupDir) || !Directory.Exists(_backupDir)) return null;
        string key = jobId.Trim('{', '}').Substring(0, 8).ToUpperInvariant();
        string[] files = Directory.GetFiles(_backupDir, "*_" + kind + "_" + key + ".json");
        Array.Sort(files, StringComparer.Ordinal); Array.Reverse(files);
        foreach (string f in files)
        {
            try
            {
                JObject b = JObject.Parse(File.ReadAllText(f));
                if (string.Equals((string)b["jobId"], jobId, StringComparison.OrdinalIgnoreCase) && (string)b["after"]["checksum"] == currentChecksum)
                { b["_file"] = Path.GetFileName(f); return b; }
            }
            catch { }
        }
        return null;
    }

    // ================= Planen =================
    public JObject PlanWrite(string tool, JObject a)
    {
        if (tool == "acmp_job_set_enabled") return PlanSet(a);
        if (tool == "acmp_job_delete") return PlanDelete(a);
        if (tool == "acmp_job_restore") return PlanRestore(a);
        if (tool == "acmp_job_set_condition") return PlanCondition(a);
        if (tool == "acmp_container_set_restriction") return PlanRestriction(a);
        throw new Exception("Unbekanntes Job-Tool: " + tool);
    }

    // Container (Pfad, Betriebssystemfamilie, Beschraenkung, Mitglieder ohne ausgemusterte Clients); null = gibt es nicht
    JObject LookupContainer(SqlConnection c, string id)
    {
        var q = Cmd(c, "SELECT CAST(s.ContainerID AS varchar(36)), s.Path, s.Name, s.OsFamily, s.ClientRestriction, " +
            "(SELECT COUNT(*) FROM CLT_CONTAINER_ITEMS ci JOIN CLT_CLIENTS_TABLE cl ON cl.CLIENTID = ci.LinkID WHERE ci.CONTAINERID = s.ContainerID AND cl.IsRetired = 0) " +
            "FROM CLT_CONTAINER_STRUCTURE s WHERE s.ContainerID = @c");
        q.Parameters.AddWithValue("@c", new Guid(id.Trim('{', '}')));
        using (var r = q.ExecuteReader())
        {
            if (!r.Read()) return null;
            var o = new JObject();
            o["id"] = Gid(r, 0); o["path"] = Str(r, 1); o["name"] = Str(r, 2); o["osFamily"] = Int(r, 3); o["restriction"] = Int(r, 4); o["members"] = Int(r, 5);
            return o;
        }
    }

    // containerId (GUID) oder container (Pfad bzw. eindeutiger Name) oder jobId (Container des Jobs) -> Container-Id
    string ResolveContainer(SqlConnection c, JObject a, bool allowJob)
    {
        bool hasId = IsStr(a["containerId"]), hasName = IsStr(a["container"]), hasJob = allowJob && IsStr(a["jobId"]);
        if ((hasId ? 1 : 0) + (hasName ? 1 : 0) + (hasJob ? 1 : 0) != 1)
            throw new Exception("Genau eines angeben: containerId (GUID aus acmp_jobs), container (Pfad wie '\\2. Softwareverteilung\\2.1 Installation\\Beispiel-Software\\' oder eindeutiger Name)" + (allowJob ? " oder jobId" : "") + ".");
        if (hasId)
        {
            string g = GuidBraces((string)a["containerId"]);
            if (g == null) throw new Exception("containerId \"" + a["containerId"] + "\" ist keine GUID.");
            return g;
        }
        if (hasJob)
        {
            string g = GuidBraces((string)a["jobId"]);
            if (g == null) throw new Exception("jobId \"" + a["jobId"] + "\" ist keine GUID. " + IdsHelp);
            JObject j = LookupJob(c, g);
            if (j == null) throw new Exception("Job " + g + " gibt es nicht (SYS_Jobs).");
            if (IsNullTok(j["containerId"])) throw new Exception(JobLabel(j) + " haengt an keinem Container.");
            return (string)j["containerId"];
        }
        string s = ((string)a["container"]).Trim();
        string p = s.StartsWith("\\") ? (s.EndsWith("\\") ? s : s + "\\") : null;
        var q = Cmd(c, "SELECT CAST(ContainerID AS varchar(36)), Path FROM CLT_CONTAINER_STRUCTURE WHERE " + (p != null ? "Path = @p" : "Name = @n"));
        if (p != null) q.Parameters.AddWithValue("@p", p); else q.Parameters.AddWithValue("@n", s);
        var hits = new List<string>(); var paths = new List<string>();
        using (var r = q.ExecuteReader()) while (r.Read()) { hits.Add(Gid(r, 0)); paths.Add(Str(r, 1)); }
        if (hits.Count == 0) throw new Exception("Container \"" + s + "\" gibt es nicht (Pfad mit \\ am Anfang oder genauer Name).");
        if (hits.Count > 1) throw new Exception("\"" + s + "\" ist nicht eindeutig: " + string.Join(", ", paths.ToArray()) + " - bitte containerId oder Pfad angeben.");
        return hits[0];
    }

    static int IntArg(JObject a, string name, int dflt)
    {
        JToken t = a[name];
        if (t == null || t.Type == JTokenType.Null) return dflt;
        int v;
        if (t.Type == JTokenType.Integer) return (int)t;
        if (t.Type == JTokenType.String && int.TryParse(((string)t).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
        throw new Exception(name + " muss eine ganze Zahl sein.");
    }

    static bool BoolArg(JObject a, string name, bool dflt)
    {
        JToken t = a[name];
        if (t == null || t.Type == JTokenType.Null) return dflt;
        if (t.Type == JTokenType.Boolean) return (bool)t;
        if (t.Type == JTokenType.String) { string s = ((string)t).Trim().ToLowerInvariant(); if (s == "true") return true; if (s == "false") return false; }
        throw new Exception(name + " muss true oder false sein.");
    }

    JObject PlanCondition(JObject a)
    {
        string kind = ConditionKind(IsStr(a["condition"]) ? (string)a["condition"] : null);
        if (kind == null) throw new Exception("condition fehlt oder ist unbekannt. Erlaubt: once (sofort, einmalig je Client), startup (einmalig beim Agentenstart), daily, hourly (mit every = alle n Tage/Stunden), maintenance (in jedem Wartungsfenster).");
        int every = IntArg(a, "every", 1);
        bool ignore = BoolArg(a, "ignoreMaintenanceWindows", false);
        CheckCondition(kind, every, ignore);
        string condId = "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}";
        string text = ConditionTextFor(kind, every, ignore);
        var items = new JArray(); var lines = new List<string>(); int same = 0;
        JObject cont;
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            string cid = ResolveContainer(c, a, true);
            cont = LookupContainer(c, cid);
            if (cont == null) throw new Exception("Container " + cid + " gibt es nicht - nichts geplant.");
            var ids = new List<string>();
            var jq = Cmd(c, "SELECT CAST(Id AS varchar(36)) FROM SYS_Jobs WHERE ContainerId = @c AND IsManualJob = 0 ORDER BY Name");
            jq.Parameters.AddWithValue("@c", new Guid(cid.Trim('{', '}')));
            using (var r = jq.ExecuteReader()) while (r.Read()) ids.Add(Gid(r, 0));
            if (ids.Count == 0) throw new Exception("Container " + cont["path"] + " hat keine Dauerauftraege - nichts zu setzen.");
            if (ids.Count > MaxContainerJobs) throw new Exception("Container " + cont["path"] + " hat " + ids.Count + " Dauerauftraege (hoechstens " + MaxContainerJobs + ") - bitte in der Console.");
            List<string[]> csRows = LoadChecksumRows(c); HashSet<string> own = OwnChecksums();
            int no = 0;
            foreach (string id in ids)
            {
                JObject j = LookupJob(c, id);
                if (j == null) continue;
                string why = RefuseSet((int)j["kind"], (bool)j["manual"]);
                if (why != null) throw new Exception(JobLabel(j) + " " + why + ". Nichts geplant.");
                string newXml = ConditionXml((string)j["conditions"], kind, every, ignore, condId);
                CondInfo before = ParseConditions((string)j["conditions"]), after = ParseConditions(newXml);
                if (!after.Active) throw new Exception(JobLabel(j) + ": neue Startbedingung waere nicht aktiv - nichts geplant.");
                if (before.Readable && before.Active && before.Text == after.Text && (string)j["logText"] == text) same++;
                var it = new JObject();
                it["jobId"] = j["id"]; it["name"] = j["name"]; it["kind"] = j["kind"]; it["target"] = TargetText(j);
                it["oldXml"] = j["conditions"]; it["oldChecksum"] = j["checksum"]; it["oldLogText"] = j["logText"];
                string csSrc, newCs = ChecksumFor(newXml, csRows, own, out csSrc);
                it["newXml"] = newXml; it["newChecksum"] = newCs; it["checksumQuelle"] = csSrc; it["logText"] = text;
                items.Add(it);
                lines.Add(++no + ". " + JobLabel(j));
                lines.Add("   vorher: " + (before.Readable ? before.Text : "(nicht lesbar)") + (before.Console.Length > 0 ? " [Console: " + before.Console + "]" : "") + (before.Readable && !before.Active ? " - wird damit wieder aktiv" : ""));
                lines.Add("   Pruefsumme " + Cs8(newCs) + " " + csSrc);
                lines.Add("   " + j["runs7"] + " Laeufe in den letzten 7 Tagen" + ((int)j["waiting"] > 0 ? ", " + j["waiting"] + " Client(s) warten noch" : "") + ".");
            }
        }
        if (items.Count == 0) throw new Exception("Container " + cont["path"] + ": keine Dauerauftraege gefunden.");
        if (same == items.Count) throw new Exception("Nichts zu tun: alle " + items.Count + " Job(s) an " + cont["path"] + " haben schon diese Startbedingung (" + text + ").");
        var plan = new JObject();
        plan["action"] = "condition"; plan["containerId"] = cont["id"]; plan["path"] = cont["path"]; plan["items"] = items; plan["conditionText"] = text;
        var pv = new JArray();
        pv.Add("Startbedingung des Containers " + cont["path"] + " (" + cont["members"] + " Mitglieder) SETZEN auf: " + ParseConditions(((JObject)items[0])["newXml"].ToString()).Text +
               " [Console: " + text + "]. Die Console fuehrt die Startbedingung je Container - geaendert werden alle " + items.Count + " Dauerauftraege des Containers, in einer Transaktion, Sicherung je Job vorher.");
        foreach (string l in lines) pv.Add(l);
        pv.Add("Wirkung: Die Agents holen die neue Startbedingung mit ihrer naechsten Job-Liste (Job-Scanner stuendlich, Agentenstart). Die Bedingung bekommt wie in der Console eine neue Id; " +
               "ob Clients, auf denen der Job schon lief, ihn bei 'einmalig' noch einmal ausfuehren, ist nicht belegt - nach dem Umstellen mit acmp_jobs (laeufe7) beobachten. Zurueck mit acmp_job_restore je Sicherung.");
        plan["vorschau"] = pv;
        return plan;
    }

    JObject PlanRestriction(JObject a)
    {
        if (a["onlyOsFamily"] == null || a["onlyOsFamily"].Type == JTokenType.Null) throw new Exception("onlyOsFamily fehlt: true = nur Clients der Betriebssystemfamilie des Containers, false = alle Clients erlauben.");
        bool only = BoolArg(a, "onlyOsFamily", false);
        int nv = only ? 1 : 0;
        JObject cont; var odd = new List<string>(); int jobs;
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            string cid = ResolveContainer(c, a, false);
            cont = LookupContainer(c, cid);
            if (cont == null) throw new Exception("Container " + cid + " gibt es nicht - nichts geplant.");
            int fam = (int)cont["osFamily"];
            if (fam != 0 && fam != 1) throw new Exception("Container " + cont["path"] + " hat eine unbekannte Betriebssystemfamilie (" + fam + ") - bitte in der Console.");
            // Mitglieder, die nicht zur Familie passen: Unix-Clients im Windows-Container bzw. Nicht-Unix-Clients im Unix-Container
            var q = Cmd(c, "SELECT cl.COMPUTERNAME FROM CLT_CONTAINER_ITEMS ci JOIN CLT_CLIENTS_TABLE cl ON cl.CLIENTID = ci.LinkID " +
                "WHERE ci.CONTAINERID = @c AND cl.IsRetired = 0 AND " + (fam == 0 ? "" : "NOT ") + "EXISTS (SELECT 1 FROM CLT_UnixClients u WHERE u.ClientId = cl.CLIENTID) ORDER BY cl.COMPUTERNAME");
            q.Parameters.AddWithValue("@c", new Guid(cid.Trim('{', '}')));
            using (var r = q.ExecuteReader()) while (r.Read()) odd.Add(Str(r, 0) ?? "?");
            var jq = Cmd(c, "SELECT COUNT(*) FROM SYS_Jobs WHERE ContainerId = @c AND IsManualJob = 0");
            jq.Parameters.AddWithValue("@c", new Guid(cid.Trim('{', '}')));
            jobs = Convert.ToInt32(jq.ExecuteScalar());
        }
        int ov = (int)cont["restriction"];
        if (ov == nv) throw new Exception("Nichts zu tun: " + cont["path"] + " steht schon auf '" + RestrictionText(nv) + "'.");
        var plan = new JObject();
        plan["action"] = "restriction"; plan["containerId"] = cont["id"]; plan["path"] = cont["path"]; plan["oldValue"] = ov; plan["newValue"] = nv;
        plan["osFamily"] = cont["osFamily"]; plan["passtNicht"] = new JArray(odd.ToArray());
        var pv = new JArray();
        string fam2 = OsFamilyText((int)cont["osFamily"]);
        pv.Add("Client-Beschraenkung von " + cont["path"] + " (Betriebssystemfamilie " + fam2 + ", " + cont["members"] + " Mitglieder, " + jobs + " Dauerauftraege) SETZEN: " +
               RestrictionText(ov) + " -> " + RestrictionText(nv) + ". Nur die Spalte ClientRestriction wird geaendert, wie in der Console. Sicherung vorher.");
        if (nv == 1)
            pv.Add(odd.Count == 0 ? "Alle Mitglieder gehoeren zur Familie " + fam2 + "."
                : odd.Count + " Mitglied(er) passen nicht zur Familie " + fam2 + " und sollten danach herausfallen: " + string.Join(", ", odd.ToArray()) + ".");
        else if (odd.Count > 0) pv.Add("Hinweis: " + odd.Count + " Mitglied(er) passen nicht zur Familie " + fam2 + " (" + string.Join(", ", odd.ToArray()) + ") - die Beschraenkung wird aufgehoben.");
        pv.Add("Wirkung: Die Beschraenkung wertet der ACMP-Server aus (keine SQL-Prozedur liest sie), erwartet bei der naechsten Container-Neuberechnung. Pruefen mit acmp_container_members bzw. acmp_jobs. Zurueck mit acmp_job_restore.");
        plan["vorschau"] = pv;
        return plan;
    }

    JObject PlanSet(JObject a)
    {
        if (a["enabled"] == null || a["enabled"].Type != JTokenType.Boolean) throw new Exception("enabled fehlt: false = abschalten, true = einschalten.");
        bool enable = (bool)a["enabled"];
        List<string> ids = IdList(a);
        var items = new JArray(); var skipped = new List<string>(); var lines = new List<string>();
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            List<string[]> csRows = LoadChecksumRows(c); HashSet<string> own = OwnChecksums();
            int no = 0;
            foreach (string id in ids)
            {
                JObject j = LookupJob(c, id);
                if (j == null) throw new Exception("Job " + id + " gibt es nicht (SYS_Jobs) - nichts geplant.");
                string why = RefuseSet((int)j["kind"], (bool)j["manual"]);
                if (why != null) throw new Exception(JobLabel(j) + " " + why + ". Nichts geplant.");
                CondInfo before = ParseConditions((string)j["conditions"]);
                if (!before.Readable) throw new Exception(JobLabel(j) + ": Startbedingung nicht lesbar - in der Console aendern.");
                if (before.Active == enable) { skipped.Add(JobLabel(j) + " ist schon " + (enable ? "eingeschaltet" : "abgeschaltet")); continue; }
                var it = new JObject();
                it["jobId"] = j["id"]; it["name"] = j["name"]; it["kind"] = j["kind"]; it["target"] = TargetText(j);
                it["oldXml"] = j["conditions"]; it["oldChecksum"] = j["checksum"]; it["oldLogText"] = j["logText"];
                string newXml, newCs, how, csSrc;
                JObject ownBk = enable ? FindOwnBackup((string)j["id"], "disable", (string)j["checksum"]) : null;
                if (ownBk != null)
                {
                    newXml = (string)ownBk["before"]["xml"]; newCs = (string)ownBk["before"]["checksum"]; how = "wie vor dem Abschalten durch den MCP (Sicherung " + ownBk["_file"] + ")";
                    csSrc = "alte Pruefsumme aus der Sicherung";
                    it["logText"] = ownBk["before"]["logText"];
                }
                else
                {
                    newXml = enable ? EnableXml((string)j["conditions"]) : DisableXml((string)j["conditions"]);
                    newCs = ChecksumFor(newXml, csRows, own, out csSrc); how = enable ? "alle Startbedingungen eingeschaltet" : "alle Startbedingungen abgeschaltet";
                    it["logText"] = ParseConditionsConsole(newXml);
                }
                it["checksumQuelle"] = csSrc;
                CondInfo after = ParseConditions(newXml);
                if (after.Active != enable) throw new Exception(JobLabel(j) + ": neue Startbedingung waere nicht " + (enable ? "aktiv" : "aus") + " - nichts geplant.");
                it["newXml"] = newXml; it["newChecksum"] = newCs;
                items.Add(it);
                lines.Add(++no + ". " + JobLabel(j) + " an " + TargetText(j));
                lines.Add("   vorher: " + before.Text + (before.Console.Length > 0 ? " [Console: " + before.Console + "]" : ""));
                lines.Add("   nachher: " + after.Text + " [" + how + "]; Pruefsumme " + Cs8(newCs) + " " + csSrc);
                if (!enable) lines.Add("   " + j["runs7"] + " Laeufe in den letzten 7 Tagen" + ((int)j["waiting"] > 0 ? ", " + j["waiting"] + " Client(s) warten noch" : "") + ".");
            }
        }
        if (items.Count == 0) throw new Exception("Nichts zu tun: " + string.Join("; ", skipped.ToArray()));
        var plan = new JObject();
        plan["action"] = enable ? "enable" : "disable"; plan["items"] = items; plan["skipped"] = new JArray(skipped.ToArray());
        var pv = new JArray();
        pv.Add((enable ? "Job(s) EINSCHALTEN" : "Job(s) ABSCHALTEN") + " wie in der Console (Startbedingung " + (enable ? "aktiv" : "'Disabled'") + "): " + items.Count + " Job(s), je eine Transaktion, Sicherung vorher.");
        foreach (string l in lines) pv.Add(l);
        foreach (string s in skipped) pv.Add("Uebersprungen: " + s);
        pv.Add(enable ? "Wirkung: Die Agents fuehren den Job nach ihrer naechsten Job-Liste wieder aus (Job-Scanner stuendlich bzw. Agentenstart)."
                      : "Wirkung: ACMP haelt den Job nicht mehr fuer aktiv; die Agents holen die geaenderte Startbedingung mit der naechsten Job-Liste. Zurueck mit acmp_job_restore (Sicherung) oder acmp_job_set_enabled enabled=true.");
        plan["vorschau"] = pv;
        return plan;
    }

    // ConditionText nach dem Muster der Console fuer eine fertige Startbedingung
    static string ParseConditionsConsole(string xml)
    {
        XmlDocument d = LoadConditions(xml);
        return Child(d.DocumentElement, "ConditionText") ?? "";
    }

    JObject PlanDelete(JObject a)
    {
        List<string> ids = IdList(a);
        var items = new JArray(); var lines = new List<string>();
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            int no = 0;
            foreach (string id in ids)
            {
                JObject j = LookupJob(c, id);
                if (j == null) throw new Exception("Job " + id + " gibt es nicht (SYS_Jobs) - nichts geplant.");
                string why = RefuseDelete((int)j["kind"], (bool)j["manual"], (int)j["templateLinks"]);
                if (why != null) throw new Exception(JobLabel(j) + " " + why + ". Nichts geplant.");
                var it = new JObject();
                it["jobId"] = j["id"]; it["name"] = j["name"]; it["kind"] = j["kind"]; it["target"] = TargetText(j);
                it["checksum"] = j["checksum"]; it["templateJobId"] = j["templateJobId"]; it["templateId"] = j["templateId"]; it["templateJobKind"] = j["templateJobKind"];
                items.Add(it);
                CondInfo ci = ParseConditions((string)j["conditions"]);
                lines.Add(++no + ". " + JobLabel(j) + " an " + TargetText(j));
                lines.Add("   Startbedingung: " + ci.Text + "; " + j["runs7"] + " Laeufe in 7 Tagen" + ((int)j["waiting"] > 0 ? ", " + j["waiting"] + " Client(s) warten noch (ihre Eintraege bleiben, der Job ist dann weg)" : "") + ".");
            }
        }
        var plan = new JObject();
        plan["action"] = "delete"; plan["items"] = items;
        var pv = new JArray();
        pv.Add("Job(s) LOESCHEN: " + items.Count + " Job(s). Geloescht wird die Zeile in SYS_Templates_Jobs, der ACMP-Trigger loescht dann den Job aus SYS_Jobs und setzt seine Protokolle auf 'fertig'. Je eine Transaktion, Sicherung vorher (vollstaendige Zeile).");
        foreach (string l in lines) pv.Add(l);
        pv.Add("Die Job-Historie (Job-Monitor) bleibt. Die Job-Vorlage des Containers bleibt, auch wenn sie leer wird. Zurueck mit acmp_job_restore (legt den Job mit derselben Id wieder an).");
        plan["vorschau"] = pv;
        return plan;
    }

    JObject PlanRestore(JObject a)
    {
        string name = IsStr(a["backup"]) ? Path.GetFileName(((string)a["backup"]).Trim()) : null;
        if (name == null || !IsBackupName(name)) throw new Exception("backup fehlt oder ist kein Sicherungsname (z.B. 20261022-210000-123_disable_6880889F.json, steht im Ergebnis von acmp_job_set_enabled/acmp_job_delete).");
        string file = Path.Combine(_backupDir ?? "", name);
        if (!File.Exists(file)) throw new Exception("Sicherung " + name + " gibt es nicht in " + _backupDir + ".");
        JObject b = JObject.Parse(File.ReadAllText(file));
        string kind = (string)b["kind"], id = (string)b["jobId"];
        var plan = new JObject(); plan["backup"] = name; plan["jobId"] = id; plan["kind"] = kind;
        var pv = new JArray();
        if (kind == "restriction")
        {
            string cid = (string)b["containerId"];
            int was = (int)b["before"]["clientRestriction"], set = (int)b["after"]["clientRestriction"];
            using (var c = new SqlConnection(_readCs))
            {
                c.Open();
                JObject cont = LookupContainer(c, cid);
                if (cont == null) throw new Exception("Container " + cid + " gibt es nicht mehr - die Sicherung " + name + " laesst sich nicht zuruecknehmen.");
                if ((int)cont["restriction"] != set) throw new Exception(cont["path"] + " wurde seit der Sicherung geaendert (jetzt '" + RestrictionText((int)cont["restriction"]) + "') - nichts geplant.");
                plan["action"] = "restore_restriction"; plan["containerId"] = cid; plan["path"] = cont["path"]; plan["oldValue"] = set; plan["newValue"] = was;
                pv.Add("Client-Beschraenkung von " + cont["path"] + " ZURUECK: " + RestrictionText(set) + " -> " + RestrictionText(was) + " (Sicherung " + name + " vom " + b["zeit"] + ").");
            }
            plan["vorschau"] = pv;
            return plan;
        }
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            JObject now = LookupJob(c, id);
            if (kind == "disable" || kind == "enable" || kind == "condition")
            {
                if (now == null) throw new Exception("Job " + id + " gibt es nicht mehr - die Sicherung " + name + " laesst sich nicht zuruecknehmen.");
                if ((string)now["checksum"] != (string)b["after"]["checksum"])
                    throw new Exception(JobLabel(now) + " wurde seit der Sicherung geaendert (Pruefsumme " + now["checksum"] + " statt " + b["after"]["checksum"] + ") - nichts geplant. In der Console pruefen.");
                plan["action"] = "restore_set"; plan["currentChecksum"] = now["checksum"]; plan["currentXml"] = now["conditions"];
                plan["newXml"] = b["before"]["xml"]; plan["newChecksum"] = b["before"]["checksum"]; plan["logText"] = b["before"]["logText"];
                pv.Add("Startbedingung von " + JobLabel(now) + " ZURUECK auf den Stand vor " + (kind == "disable" ? "dem Abschalten" : kind == "enable" ? "dem Einschalten" : "dem Setzen der Startbedingung") +
                       " am " + b["zeit"] + " (Sicherung " + name + "):");
                pv.Add("   jetzt: " + ParseConditions((string)now["conditions"]).Text);
                pv.Add("   danach: " + ParseConditions((string)b["before"]["xml"]).Text + " (alte Pruefsumme " + b["before"]["checksum"] + ")");
            }
            else if (kind == "delete")
            {
                if (now != null) throw new Exception("Job " + id + " gibt es wieder (" + JobLabel(now) + ") - nichts zurueckzunehmen.");
                JObject row = (JObject)b["row"];
                var tq = Cmd(c, "SELECT COUNT(*) FROM SYS_Templates WHERE Id = @t");
                tq.Parameters.AddWithValue("@t", new Guid(((string)b["templateId"]).Trim('{', '}')));
                if (Convert.ToInt32(tq.ExecuteScalar()) != 1) throw new Exception("Die Job-Vorlage " + b["templateId"] + " des Containers gibt es nicht mehr (in der Console geaendert?) - den Job bitte in der Console neu anlegen.");
                if (!IsNullTok(row["ContainerId"]))
                {
                    var cq = Cmd(c, "SELECT COUNT(*) FROM CLT_CONTAINER_STRUCTURE WHERE ContainerID = @c");
                    cq.Parameters.AddWithValue("@c", new Guid(((string)row["ContainerId"]).Trim('{', '}')));
                    if (Convert.ToInt32(cq.ExecuteScalar()) != 1) throw new Exception("Der Container " + row["ContainerId"] + " des Jobs ist geloescht - nicht wiederherstellbar.");
                }
                plan["action"] = "restore_delete";
                pv.Add("Geloeschten Job WIEDER ANLEGEN mit derselben Id: \"" + row["Name"] + "\" " + id + " an " + b["target"] + " (Sicherung " + name + ", geloescht am " + b["zeit"] + ").");
                pv.Add("   Startbedingung: " + ParseConditions((string)row["Conditions"]).Text);
            }
            else throw new Exception("Unbekannte Art der Sicherung: " + kind);
        }
        plan["vorschau"] = pv;
        return plan;
    }

    // ================= Ausfuehren =================
    // Einstieg fuer den Server: dryRun -> nur Plan; sonst pruefen, planen, direkt ausfuehren (keine Vier-Augen-Freigabe mehr)
    public JObject RunWrite(string tool, JObject a, string requester)
    {
        if (a["dryRun"] != null && a["dryRun"].Type == JTokenType.Boolean && (bool)a["dryRun"])
        {
            JObject p = PlanWrite(tool, a);
            p["dryRun"] = true;
            StripPlan(p);
            if (!WriterConfigOk) p["hinweis"] = "Schreiben ist aus: " + WriterDisabledReason;
            return p;
        }
        CheckWrite(requester, tool);
        JObject plan = PlanWrite(tool, a);
        if (tool == "acmp_job_restore" && (string)plan["action"] == "restore_restriction") CheckWrite(requester, "acmp_container_set_restriction");
        lock (_jwLock)
        {
            if (tool == "acmp_job_delete") return ExecDelete(plan, requester);
            if (tool == "acmp_job_restore") return ExecRestore(plan, requester);
            if (tool == "acmp_job_set_condition") return ExecCondition(plan, requester);
            if (tool == "acmp_container_set_restriction") return ExecRestriction(plan, requester);
            return ExecSet(plan, requester);
        }
    }

    // XML-Inhalte gehoeren nicht in die Antwort (lang; die Vorschau zeigt sie lesbar)
    static void StripPlan(JObject p)
    {
        JArray items = p["items"] as JArray;
        if (items != null) foreach (JObject it in items) { it.Remove("oldXml"); it.Remove("newXml"); }
        p.Remove("currentXml"); p.Remove("newXml");
    }

    string SaveBackup(JObject b, string kind, string jobId)
    {
        Directory.CreateDirectory(_backupDir);
        string file = Path.Combine(_backupDir, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "_" + kind + "_" + jobId.Trim('{', '}').Substring(0, 8).ToUpperInvariant() + ".json");
        File.WriteAllText(file, b.ToString(Newtonsoft.Json.Formatting.Indented), new UTF8Encoding(false));
        if (new FileInfo(file).Length < 40) throw new Exception("Sicherung unvollstaendig - nichts geaendert.");
        return file;
    }

    static SqlCommand TxCmd(SqlConnection c, SqlTransaction tx, string sql)
    {
        var q = c.CreateCommand(); q.Transaction = tx; q.CommandText = sql; q.CommandTimeout = SqlTimeout; return q;
    }

    static SqlParameter XmlPar(string name, string value)
    {
        var p = new SqlParameter(name, SqlDbType.Xml); p.Value = value == null ? (object)DBNull.Value : value; return p;
    }

    // Startbedingung + Pruefsumme setzen, wenn der Stand noch dem erwarteten entspricht; ConditionText im Job-Monitor nachziehen
    static void SwapConditions(SqlConnection wc, SqlTransaction tx, string id, string expXml, string expCs, string newXml, string newCs, string logText, string label)
    {
        Guid g = new Guid(id.Trim('{', '}'));
        var sq = TxCmd(wc, tx, "SELECT CAST(Conditions AS nvarchar(max)), ISNULL(ConditionCheckSum, '') FROM SYS_Jobs WITH (UPDLOCK, ROWLOCK) WHERE Id = @id");
        sq.Parameters.AddWithValue("@id", g);
        string curXml = null, curCs = null; bool found = false;
        using (var r = sq.ExecuteReader()) if (r.Read()) { found = true; curXml = r.IsDBNull(0) ? null : r.GetString(0); curCs = r.GetString(1); }
        if (!found) throw new Exception(label + " gibt es nicht mehr - zurueckgerollt, nichts geschrieben.");
        if (curXml != expXml || curCs != (expCs ?? "")) throw new Exception(label + " wurde seit der Planung geaendert - zurueckgerollt, nichts geschrieben. Bitte neu aufrufen.");
        var uq = TxCmd(wc, tx, "UPDATE SYS_Jobs SET Conditions = @c, ConditionCheckSum = @cs WHERE Id = @id");
        uq.Parameters.Add(XmlPar("@c", newXml)); uq.Parameters.AddWithValue("@cs", newCs); uq.Parameters.AddWithValue("@id", g);
        if (uq.ExecuteNonQuery() != 1) throw new Exception(label + ": Startbedingung nicht eindeutig geschrieben - zurueckgerollt.");
        var lq = TxCmd(wc, tx, "UPDATE SYS_Jobs_Logs SET ConditionText = @t WHERE JobId = @id");
        lq.Parameters.AddWithValue("@t", (object)logText ?? DBNull.Value); lq.Parameters.AddWithValue("@id", g);
        lq.ExecuteNonQuery();   // 0 Zeilen ist moeglich (Job lief noch nie)
        var vq = TxCmd(wc, tx, "SELECT ISNULL(ConditionCheckSum, '') FROM SYS_Jobs WHERE Id = @id");
        vq.Parameters.AddWithValue("@id", g);
        if ((string)vq.ExecuteScalar() != newCs) throw new Exception(label + ": Kontrolle nach dem Schreiben fehlgeschlagen - zurueckgerollt.");
    }

    JObject ExecSet(JObject plan, string requester)
    {
        string action = (string)plan["action"];
        var done = new JArray(); var backups = new JArray();
        using (var wc = new SqlConnection(_writeCs))
        {
            wc.Open();
            foreach (JObject it in (JArray)plan["items"])
            {
                string id = (string)it["jobId"], label = "Job \"" + it["name"] + "\" " + id;
                var b = new JObject();
                b["kind"] = action; b["tool"] = "acmp_job_set_enabled"; b["von"] = requester; b["zeit"] = DateTime.Now.ToString("s"); b["jobId"] = id; b["name"] = it["name"]; b["target"] = it["target"];
                var before = new JObject(); before["xml"] = it["oldXml"]; before["checksum"] = it["oldChecksum"]; before["logText"] = it["oldLogText"]; b["before"] = before;
                var after = new JObject(); after["xml"] = it["newXml"]; after["checksum"] = it["newChecksum"]; after["checksumQuelle"] = it["checksumQuelle"]; after["logText"] = it["logText"]; b["after"] = after;
                string bf = SaveBackup(b, action, id);
                using (SqlTransaction tx = wc.BeginTransaction())
                {
                    SwapConditions(wc, tx, id, (string)it["oldXml"], (string)it["oldChecksum"], (string)it["newXml"], (string)it["newChecksum"], (string)it["logText"], label);
                    tx.Commit();
                }
                var o = new JObject(); o["jobId"] = id; o["name"] = it["name"]; o["ziel"] = it["target"];
                o["startbedingung"] = ParseConditions((string)it["newXml"]).Text; o["pruefsumme"] = Cs8((string)it["newChecksum"]) + " " + it["checksumQuelle"]; o["sicherung"] = Path.GetFileName(bf);
                done.Add(o); backups.Add(Path.GetFileName(bf));
                _log("acmp_job_set_enabled (" + action + ") von " + requester + ": " + label + ", Sicherung " + Path.GetFileName(bf));
            }
        }
        var res = new JObject();
        res["ok"] = true; res["aktion"] = action == "disable" ? "abgeschaltet" : "eingeschaltet"; res["jobs"] = done;
        if (((JArray)plan["skipped"]).Count > 0) res["uebersprungen"] = plan["skipped"];
        res["zurueck"] = "acmp_job_restore mit backup=<sicherung> stellt die alte Startbedingung samt Pruefsumme wieder her.";
        res["hinweis"] = "Die Agents holen die Aenderung mit ihrer naechsten Job-Liste (Job-Scanner stuendlich, Agentenstart). Pruefen: acmp_jobs name=... (laeufe7 sinkt) bzw. acmp_job_results.";
        return res;
    }

    // Startbedingung fuer alle Jobs des Containers: Sicherung je Job, dann eine Transaktion fuer alle (der Container hat danach eine Bedingung)
    JObject ExecCondition(JObject plan, string requester)
    {
        var done = new JArray(); var files = new List<string>();
        foreach (JObject it in (JArray)plan["items"])
        {
            string id = (string)it["jobId"];
            var b = new JObject();
            b["kind"] = "condition"; b["tool"] = "acmp_job_set_condition"; b["von"] = requester; b["zeit"] = DateTime.Now.ToString("s"); b["jobId"] = id; b["name"] = it["name"]; b["target"] = it["target"];
            b["containerId"] = plan["containerId"];
            var before = new JObject(); before["xml"] = it["oldXml"]; before["checksum"] = it["oldChecksum"]; before["logText"] = it["oldLogText"]; b["before"] = before;
            var after = new JObject(); after["xml"] = it["newXml"]; after["checksum"] = it["newChecksum"]; after["checksumQuelle"] = it["checksumQuelle"]; after["logText"] = it["logText"]; b["after"] = after;
            files.Add(Path.GetFileName(SaveBackup(b, "condition", id)));
        }
        using (var wc = new SqlConnection(_writeCs))
        {
            wc.Open();
            using (SqlTransaction tx = wc.BeginTransaction())
            {
                foreach (JObject it in (JArray)plan["items"])
                    SwapConditions(wc, tx, (string)it["jobId"], (string)it["oldXml"], (string)it["oldChecksum"], (string)it["newXml"], (string)it["newChecksum"], (string)it["logText"], "Job \"" + it["name"] + "\" " + it["jobId"]);
                tx.Commit();
            }
        }
        int i = 0;
        foreach (JObject it in (JArray)plan["items"])
        {
            var o = new JObject(); o["jobId"] = it["jobId"]; o["name"] = it["name"]; o["pruefsumme"] = Cs8((string)it["newChecksum"]) + " " + it["checksumQuelle"]; o["sicherung"] = files[i++];
            done.Add(o);
        }
        string txt = ParseConditions((string)((JObject)((JArray)plan["items"])[0])["newXml"]).Text;
        _log("acmp_job_set_condition von " + requester + ": " + plan["path"] + " -> " + plan["conditionText"] + " (" + done.Count + " Job(s)), Sicherungen " + string.Join(", ", files.ToArray()));
        var res = new JObject();
        res["ok"] = true; res["aktion"] = "Startbedingung gesetzt"; res["container"] = plan["path"]; res["startbedingung"] = txt; res["consoleText"] = plan["conditionText"]; res["jobs"] = done;
        res["zurueck"] = "acmp_job_restore mit backup=<sicherung> je Job stellt die alte Startbedingung samt Pruefsumme wieder her.";
        res["hinweis"] = "Die Agents holen die Aenderung mit ihrer naechsten Job-Liste (Job-Scanner stuendlich, Agentenstart). Pruefen: acmp_jobs container=... (Startbedingung, laeufe7).";
        return res;
    }

    // ClientRestriction setzen, wenn der Stand noch dem erwarteten entspricht (der Trigger TG_CLT_CONTAINER_STRUCTURE_UpdateTenantNo setzt nur TenantNo nach)
    static void SwapRestriction(SqlConnection wc, SqlTransaction tx, string cid, int expect, int value, string label)
    {
        Guid g = new Guid(cid.Trim('{', '}'));
        var sq = TxCmd(wc, tx, "SELECT ClientRestriction FROM CLT_CONTAINER_STRUCTURE WITH (UPDLOCK, ROWLOCK) WHERE ContainerID = @id");
        sq.Parameters.AddWithValue("@id", g);
        object cur = sq.ExecuteScalar();
        if (cur == null || cur is DBNull) throw new Exception(label + " gibt es nicht mehr - zurueckgerollt, nichts geschrieben.");
        if (Convert.ToInt32(cur) != expect) throw new Exception(label + " wurde seit der Planung geaendert - zurueckgerollt, nichts geschrieben. Bitte neu aufrufen.");
        var uq = TxCmd(wc, tx, "UPDATE CLT_CONTAINER_STRUCTURE SET ClientRestriction = @v WHERE ContainerID = @id AND ClientRestriction = @o");
        uq.Parameters.AddWithValue("@v", (byte)value); uq.Parameters.AddWithValue("@id", g); uq.Parameters.AddWithValue("@o", (byte)expect);
        uq.ExecuteNonQuery();   // Zeilenzahl enthaelt die des Triggers - Kontrolle unten
        var vq = TxCmd(wc, tx, "SELECT ClientRestriction FROM CLT_CONTAINER_STRUCTURE WHERE ContainerID = @id");
        vq.Parameters.AddWithValue("@id", g);
        object now = vq.ExecuteScalar();
        if (now == null || now is DBNull || Convert.ToInt32(now) != value) throw new Exception(label + ": Kontrolle nach dem Schreiben fehlgeschlagen - zurueckgerollt.");
    }

    JObject ExecRestriction(JObject plan, string requester)
    {
        string cid = (string)plan["containerId"], label = "Container " + plan["path"];
        int ov = (int)plan["oldValue"], nv = (int)plan["newValue"];
        var b = new JObject();
        b["kind"] = "restriction"; b["tool"] = "acmp_container_set_restriction"; b["von"] = requester; b["zeit"] = DateTime.Now.ToString("s");
        b["containerId"] = cid; b["path"] = plan["path"]; b["osFamily"] = plan["osFamily"];
        var before = new JObject(); before["clientRestriction"] = ov; b["before"] = before;
        var after = new JObject(); after["clientRestriction"] = nv; b["after"] = after;
        string bf = Path.GetFileName(SaveBackup(b, "restriction", cid));
        using (var wc = new SqlConnection(_writeCs))
        {
            wc.Open();
            using (SqlTransaction tx = wc.BeginTransaction()) { SwapRestriction(wc, tx, cid, ov, nv, label); tx.Commit(); }
        }
        _log("acmp_container_set_restriction von " + requester + ": " + plan["path"] + " " + ov + " -> " + nv + ", Sicherung " + bf);
        var res = new JObject();
        res["ok"] = true; res["aktion"] = "Client-Beschraenkung gesetzt"; res["container"] = plan["path"]; res["vorher"] = RestrictionText(ov); res["nachher"] = RestrictionText(nv); res["sicherung"] = bf;
        if (nv == 1 && ((JArray)plan["passtNicht"]).Count > 0) res["erwartet"] = "Bei der naechsten Container-Neuberechnung fallen heraus: " + string.Join(", ", ((JArray)plan["passtNicht"]).ToObject<string[]>());
        res["zurueck"] = "acmp_job_restore mit backup=" + bf;
        res["hinweis"] = "Pruefen nach der naechsten Container-Neuberechnung: acmp_container_members bzw. acmp_jobs container=...";
        return res;
    }

    JObject ExecDelete(JObject plan, string requester)
    {
        var done = new JArray();
        using (var rc = new SqlConnection(_readCs))
        using (var wc = new SqlConnection(_writeCs))
        {
            rc.Open(); wc.Open();
            foreach (JObject it in (JArray)plan["items"])
            {
                string id = (string)it["jobId"], label = "Job \"" + it["name"] + "\" " + id;
                JObject row = FullJobRow(rc, id);
                if (row == null) throw new Exception(label + " gibt es nicht mehr - nichts geloescht.");
                var lq = Cmd(rc, "SELECT TOP 1 State, ConditionText FROM SYS_Jobs_Logs WHERE JobId = @id");
                lq.Parameters.AddWithValue("@id", new Guid(id.Trim('{', '}')));
                JToken logState = JValue.CreateNull(), logText = JValue.CreateNull();
                using (var r = lq.ExecuteReader()) if (r.Read()) { logState = Int(r, 0); logText = Str(r, 1); }
                var b = new JObject();
                b["kind"] = "delete"; b["tool"] = "acmp_job_delete"; b["von"] = requester; b["zeit"] = DateTime.Now.ToString("s"); b["jobId"] = id; b["name"] = it["name"]; b["target"] = it["target"];
                b["row"] = row; b["templateJobId"] = it["templateJobId"]; b["templateId"] = it["templateId"]; b["templateJobKind"] = it["templateJobKind"];
                b["logState"] = logState; b["logText"] = logText;
                string bf = SaveBackup(b, "delete", id);
                Guid g = new Guid(id.Trim('{', '}')), tj = new Guid(((string)it["templateJobId"]).Trim('{', '}'));
                using (SqlTransaction tx = wc.BeginTransaction())
                {
                    var cq = TxCmd(wc, tx, "SELECT ISNULL(ConditionCheckSum, '') FROM SYS_Jobs WITH (UPDLOCK, ROWLOCK) WHERE Id = @id");
                    cq.Parameters.AddWithValue("@id", g);
                    object cur = cq.ExecuteScalar();
                    if (cur == null) throw new Exception(label + " gibt es nicht mehr - zurueckgerollt.");
                    if ((string)cur != (string)it["checksum"]) throw new Exception(label + " wurde seit der Planung geaendert - zurueckgerollt, nichts geloescht.");
                    var dq = TxCmd(wc, tx, "DELETE FROM SYS_Templates_Jobs WHERE Id = @tj AND JobId = @id");
                    dq.Parameters.AddWithValue("@tj", tj); dq.Parameters.AddWithValue("@id", g);
                    if (dq.ExecuteNonQuery() != 1) throw new Exception(label + ": Verknuepfung mit der Job-Vorlage nicht eindeutig - zurueckgerollt.");
                    var vq = TxCmd(wc, tx, "SELECT COUNT(*) FROM SYS_Jobs WHERE Id = @id");
                    vq.Parameters.AddWithValue("@id", g);
                    if (Convert.ToInt32(vq.ExecuteScalar()) != 0) throw new Exception(label + ": der ACMP-Trigger hat den Job nicht geloescht - zurueckgerollt (TG_Delete_SYS_Templates_Jobs pruefen).");
                    tx.Commit();
                }
                var o = new JObject(); o["jobId"] = id; o["name"] = it["name"]; o["ziel"] = it["target"]; o["sicherung"] = Path.GetFileName(bf);
                done.Add(o);
                _log("acmp_job_delete von " + requester + ": " + label + " an " + it["target"] + ", Sicherung " + Path.GetFileName(bf));
            }
        }
        var res = new JObject();
        res["ok"] = true; res["aktion"] = "geloescht"; res["jobs"] = done;
        res["zurueck"] = "acmp_job_restore mit backup=<sicherung> legt den Job mit derselben Id wieder an (solange Container und Job-Vorlage bestehen).";
        return res;
    }

    JObject ExecRestore(JObject plan, string requester)
    {
        string id = (string)plan["jobId"], label = "Job " + id;
        string action = (string)plan["action"];
        using (var wc = new SqlConnection(_writeCs))
        {
            wc.Open();
            if (action == "restore_restriction")
            {
                label = "Container " + plan["path"];
                using (SqlTransaction tx = wc.BeginTransaction()) { SwapRestriction(wc, tx, (string)plan["containerId"], (int)plan["oldValue"], (int)plan["newValue"], label); tx.Commit(); }
            }
            else if (action == "restore_set")
            {
                using (SqlTransaction tx = wc.BeginTransaction())
                {
                    SwapConditions(wc, tx, id, (string)plan["currentXml"], (string)plan["currentChecksum"], (string)plan["newXml"], (string)plan["newChecksum"], (string)plan["logText"], label);
                    tx.Commit();
                }
            }
            else
            {
                JObject b = JObject.Parse(File.ReadAllText(Path.Combine(_backupDir, (string)plan["backup"])));
                JObject row = (JObject)b["row"];
                Guid g = new Guid(id.Trim('{', '}'));
                using (SqlTransaction tx = wc.BeginTransaction())
                {
                    var iq = TxCmd(wc, tx, "INSERT INTO SYS_Jobs (Id, Name, Description, Priority, Conditions, ConditionCheckSum, JobAction, CanRunParallel, ContentId, JobKind, ContainerId, Origin, IsManualJob, ServerId, CreationDate, FolderId) " +
                        "VALUES (@id, @n, @d, @p, @c, @cs, @ja, @crp, @ct, @k, @cont, @o, @m, @srv, @cd, @f)");
                    iq.Parameters.AddWithValue("@id", g);
                    iq.Parameters.AddWithValue("@n", (object)(string)row["Name"] ?? DBNull.Value);
                    iq.Parameters.AddWithValue("@d", (object)(string)row["Description"] ?? DBNull.Value);
                    iq.Parameters.AddWithValue("@p", (int)row["Priority"]);
                    iq.Parameters.Add(XmlPar("@c", (string)row["Conditions"]));
                    iq.Parameters.AddWithValue("@cs", (object)(string)row["ConditionCheckSum"] ?? DBNull.Value);
                    iq.Parameters.Add(XmlPar("@ja", (string)row["JobAction"]));
                    iq.Parameters.AddWithValue("@crp", (bool)row["CanRunParallel"]);
                    iq.Parameters.AddWithValue("@ct", new Guid(((string)row["ContentId"]).Trim('{', '}')));
                    iq.Parameters.AddWithValue("@k", (byte)(int)row["JobKind"]);
                    iq.Parameters.AddWithValue("@cont", IsNullTok(row["ContainerId"]) ? (object)DBNull.Value : new Guid(((string)row["ContainerId"]).Trim('{', '}')));
                    iq.Parameters.AddWithValue("@o", (byte)(int)row["Origin"]);
                    iq.Parameters.AddWithValue("@m", (bool)row["IsManualJob"]);
                    iq.Parameters.AddWithValue("@srv", IsNullTok(row["ServerId"]) ? (object)DBNull.Value : new Guid(((string)row["ServerId"]).Trim('{', '}')));
                    iq.Parameters.AddWithValue("@cd", IsNullTok(row["CreationDate"]) ? (object)DBNull.Value : (double)row["CreationDate"]);
                    iq.Parameters.AddWithValue("@f", IsNullTok(row["FolderId"]) ? (object)DBNull.Value : new Guid(((string)row["FolderId"]).Trim('{', '}')));
                    iq.ExecuteNonQuery();
                    var tq = TxCmd(wc, tx, "INSERT INTO SYS_Templates_Jobs (Id, TemplateId, JobId, JobKind) VALUES (@tj, @t, @id, @k)");
                    tq.Parameters.AddWithValue("@tj", new Guid(((string)b["templateJobId"]).Trim('{', '}')));
                    tq.Parameters.AddWithValue("@t", new Guid(((string)b["templateId"]).Trim('{', '}')));
                    tq.Parameters.AddWithValue("@id", g);
                    tq.Parameters.AddWithValue("@k", (byte)(int)b["templateJobKind"]);
                    tq.ExecuteNonQuery();
                    if (!IsNullTok(b["logState"]))
                    {
                        var lq = TxCmd(wc, tx, "UPDATE SYS_Jobs_Logs SET State = @s, ConditionText = @t WHERE JobId = @id");
                        lq.Parameters.AddWithValue("@s", (byte)(int)b["logState"]); lq.Parameters.AddWithValue("@t", (object)(string)b["logText"] ?? DBNull.Value); lq.Parameters.AddWithValue("@id", g);
                        lq.ExecuteNonQuery();
                    }
                    var vq = TxCmd(wc, tx, "SELECT COUNT(*) FROM SYS_Templates_Jobs WHERE JobId = @id");
                    vq.Parameters.AddWithValue("@id", g);
                    if (Convert.ToInt32(vq.ExecuteScalar()) != 1) throw new Exception(label + ": Kontrolle nach dem Anlegen fehlgeschlagen - zurueckgerollt.");
                    tx.Commit();
                }
            }
        }
        _log("acmp_job_restore von " + requester + ": " + label + " aus " + plan["backup"]);
        var res = new JObject();
        res["ok"] = true; res["aktion"] = action == "restore_delete" ? "wieder angelegt" : action == "restore_restriction" ? "Client-Beschraenkung zurueckgesetzt" : "Startbedingung zurueckgesetzt";
        if (action == "restore_restriction") res["container"] = plan["path"]; else res["jobId"] = id;
        res["sicherung"] = plan["backup"];
        return res;
    }
}
