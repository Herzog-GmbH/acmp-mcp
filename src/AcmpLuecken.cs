// AcmpLuecken.cs - Erweiterung vom 25.10.2026 (partial AcmpJobs, gleiche Machart wie AcmpMsw.cs):
//  - acmp_job_create: Client Command als Dauerauftrag (JobKind 4) an einem Container wie der ACMP-Server beim Speichern in der Console.
//    Beleg: Mitschnitt 1 vom 22.10.2026 18:44:34: Upsert SYS_Templates
//    "Job template for container: {Id}", INSERT SYS_Jobs (JobKind 4, Origin 1, JobAction TCCJobAction), Upsert SYS_Jobs_Logs (State 1),
//    Upsert SYS_Templates_Jobs, JobAction.modify(TestRing), INSERT SYS_Containers_Templates - dieselbe Folge wie acmp_msw_job_create.
//    Commands mit Parametern lehnt das Tool ab: wie die Console die Werte eines Container-Jobs in TCCJobAction/TParameterList ablegt, ist
//    nicht belegt (keine Sicherung mit Beispiel, JobAction ist fuer den Lese-Login gesperrt).
//  - acmp_job_log: Schritt-Protokoll eines Client-Command-Laufs (CLT_SCRIPT_EXECRESULT.XmlLogFile, Raw-Deflate, XML <LE>-Baum) als lesbare
//    Schritte mit Meldungen. Ersetzt das Entpacken von Hand (acmp_db_script_blob und eigenes Entpacken).
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Newtonsoft.Json.Linq;

partial class AcmpJobs
{
    public const int CcKind = 4;
    public static bool IsCcJobTool(string n) { return n == "acmp_job_create"; }

    // ================= Hilfen ohne DB (offline getestet in LueckenTest.cs) =================

    // JobAction eines Client-Command-Dauerauftrags wie die Console (Mitschnitt 22.10.2026, Reihenfolge der Elemente unveraendert)
    public static string CcJobActionXml(string actionId, string scriptId, string name, int testRing)
    {
        if (GuidBraces(actionId) == null || GuidBraces(scriptId) == null) throw new Exception("CcJobActionXml: Id ist keine GUID.");
        var d = new XmlDocument();
        XmlElement r = d.CreateElement("TCCJobAction");
        d.AppendChild(r);
        AddEl(r, "Id", GuidBraces(actionId));
        AddEl(r, "Name", name);
        AddEl(r, "CallerType", null);
        AddEl(r, "ContainerId", null);
        AddEl(r, "ConsoleParams", null);
        AddEl(r, "CCLevel", "0");
        AddEl(r, "ContainerPath", null);
        AddEl(r, "ExecuteConsoleScript", "true");
        AddEl(r, "LogMethod", "lmDefinedByClientCommand");
        AddEl(r, "TParameterList", null);
        AddEl(r, "ChildExecId", null);
        AddEl(r, "MessageCode", "-1");
        AddEl(r, "ErrorCode", "0");
        AddEl(r, "ParentJobId", null);
        AddEl(r, "ParentJobExecId", null);
        AddEl(r, "ContentId", GuidBraces(scriptId));
        AddEl(r, "JobExecId", null);
        AddEl(r, "CanRunParallel", "false");
        AddEl(r, "TestRing", testRing.ToString(CultureInfo.InvariantCulture));
        AddEl(r, "IsAgentInRollout", "false");
        AddEl(r, "JobExecMode", "emAlways");
        AddEl(r, "Action", "eaNone");
        return d.OuterXml;
    }

    // Ergebnis eines Laufs (CLT_SCRIPT_EXECRESULT.EXECRESULT) wie die Console (SYS_DisplayFields_AvailableValues, 25.10.2026)
    public static string ExecResultText(int r)
    {
        switch (r)
        {
            case 0: return "Erfolgreich";
            case 1001: return "Fehler";
            case 1002: return "Bedingungen nicht erfuellt";
            case 1003: return "Script nicht interaktiv";
            case 1004: return "Script nicht verfuegbar";
            case 1005: return "Keine Lizenz";
            case 1006: return "Unerwarteter Fehler";
            case 1007: return "Leeres oder unbekanntes Script";
            case 1008: return "Server Fehler";
            case 1009: return "Fehler bei der Scriptausfuehrung";
            case 1010: return "Server ist beschaeftigt";
            case 1011: return "Vom Anwender abgebrochen";
            case 1012: return "Time out";
            case 1013: return "Script deaktiviert";
            case 1014: return "Client Update notwendig";
            case 1015: return "GetFullScript fehlgeschlagen";
            case 1016: return "Kein File Repository fuer Client";
        }
        return "Ergebnis " + r;
    }

    // XmlLogFile entpacken: Raw-Deflate (ohne zlib-Kopf), notfalls ab Byte 2; Text mit BOM-Erkennung
    public static string InflateLog(byte[] data)
    {
        if (data == null || data.Length == 0) return null;
        foreach (int off in new[] { 0, 2 })
        {
            if (off >= data.Length) break;
            try
            {
                using (var ms = new MemoryStream(data, off, data.Length - off))
                using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
                using (var outp = new MemoryStream())
                {
                    ds.CopyTo(outp);
                    byte[] raw = outp.ToArray();
                    if (raw.Length == 0) continue;
                    if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE) return Encoding.Unicode.GetString(raw, 2, raw.Length - 2);
                    if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF) return Encoding.UTF8.GetString(raw, 3, raw.Length - 3);
                    if (raw.Length >= 2 && raw[1] == 0) return Encoding.Unicode.GetString(raw);
                    return Encoding.UTF8.GetString(raw);
                }
            }
            catch { }
        }
        return null;
    }

    const string SecretName = @"[A-Za-z0-9_\-\.]*(?:PASS|KENNW|PWD|SECRET|TOKEN|APIKEY|API_KEY|CREDENTIAL|PRIVATE)[A-Za-z0-9_\-\.]*";
    // "NAME = wert" (protokollierte Variablen, auch in jeder Zeile einer mehrzeiligen Meldung)
    static readonly Regex SecretVarRx = new Regex(@"^(\s*" + SecretName + @")\s*=.*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    // Set variable "NAME" to "wert" (Agent beim Setzen einer Projektvariable; Review 25.10.2026)
    static readonly Regex SecretSetRx = new Regex(@"(Set variable\s+""" + SecretName + @"""\s+to\s+).*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    static readonly Regex SecretInlineRx = new Regex(@"(?i)(-(?:Password|Passwort|Kennwort|Secret|Token)\s+)(""[^""]*""|'[^']*'|\S+)");

    // Kennwoerter in einer Meldung maskieren: "NAME = wert" und Set variable "NAME" to "wert" mit verdaechtigem Variablennamen, -Password <x> o.ae.
    public static string MaskSecrets(string m)
    {
        if (string.IsNullOrEmpty(m)) return m;
        string s = SecretVarRx.Replace(m, "$1 = ***");
        s = SecretSetRx.Replace(s, "$1\"***\"");
        return SecretInlineRx.Replace(s, "$1***");
    }

    static string LeChild(XmlElement e, string name) { XmlElement c = e[name]; return c == null ? null : c.InnerText; }

    static double MsBetween(string st, string et)
    {
        DateTimeOffset a, b;
        if (!DateTimeOffset.TryParse(st, CultureInfo.InvariantCulture, DateTimeStyles.None, out a) ||
            !DateTimeOffset.TryParse(et, CultureInfo.InvariantCulture, DateTimeStyles.None, out b)) return -1;
        return Math.Round((b - a).TotalMilliseconds);
    }

    static string LocalTime(string iso)
    {
        DateTimeOffset a;
        if (!DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out a)) return iso;
        return a.LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);
    }

    // Meldungen unterhalb eines Knotens sammeln, ohne in weitere Schritte (letCommand) abzusteigen
    static void CollectMessages(XmlElement le, List<string> msgs, int max, ref int dropped)
    {
        foreach (XmlNode n in le.ChildNodes)
        {
            var c = n as XmlElement;
            if (c == null || c.Name != "LE") continue;
            string let = LeChild(c, "LET") ?? "";
            if (let == "letCommand" || c["A"] != null) continue;
            string m = LeChild(c, "M");
            if (!string.IsNullOrEmpty(m))
            {
                if (msgs.Count < max) msgs.Add(MaskSecrets(m)); else dropped++;
            }
            CollectMessages(c, msgs, max, ref dropped);
        }
    }

    // Schritte (letCommand bzw. Knoten mit A) in Reihenfolge, mit Tiefe fuer verschachtelte Bloecke (If/Case/Loop)
    static void CollectSteps(XmlElement le, int depth, JArray steps, int maxMsgs, ref int truncated)
    {
        foreach (XmlNode n in le.ChildNodes)
        {
            var c = n as XmlElement;
            if (c == null || c.Name != "LE") continue;
            string let = LeChild(c, "LET") ?? "";
            if (let == "letCommand" || c["A"] != null)
            {
                var s = new JObject();
                string line = LeChild(c, "L");
                if (line != null) s["zeile"] = line;
                s["schritt"] = LeChild(c, "D") ?? "";
                s["klasse"] = LeChild(c, "A") ?? "";
                if (depth > 0) s["ebene"] = depth;
                int rr; s["ergebnis"] = int.TryParse(LeChild(c, "R"), out rr) ? (rr == 0 ? "ok" : "Code " + rr) : "?";
                string st = LeChild(c, "ST"), et = LeChild(c, "ET");
                if (st != null) s["start"] = LocalTime(st);
                double ms = MsBetween(st, et); if (ms >= 0) s["dauerMs"] = ms;
                var msgs = new List<string>(); int dropped = 0;
                CollectMessages(c, msgs, maxMsgs, ref dropped);
                if (msgs.Count > 0) s["meldungen"] = new JArray(msgs.ToArray());
                if (dropped > 0) { s["weitereMeldungen"] = dropped; truncated += dropped; }
                steps.Add(s);
                CollectSteps(c, depth + 1, steps, maxMsgs, ref truncated);
            }
            else if (let != "letMessage" && let != "letMessageRoot")
                CollectSteps(c, depth, steps, maxMsgs, ref truncated);
        }
    }

    // Ein entpacktes XmlLogFile lesbar machen: Kopf (Rechner, Command, Version, Zeiten), Schritte, optional protokollierte Variablen
    public static JObject ParseScriptLog(string xml, bool withVariables, int maxMsgs)
    {
        var o = new JObject();
        var doc = new XmlDocument();
        doc.XmlResolver = null;
        doc.LoadXml(xml);
        XmlElement root = doc.DocumentElement;
        if (root == null || root.Name != "LE") throw new Exception("Protokoll hat nicht das erwartete Format (<LE>).");
        if (root.HasAttribute("ACMP_Version")) o["acmpVersion"] = root.GetAttribute("ACMP_Version");
        if (root.HasAttribute("ClientCommandVersion")) o["versionImProtokoll"] = root.GetAttribute("ClientCommandVersion");
        string st = LeChild(root, "ST"), et = LeChild(root, "ET");
        if (st != null) o["start"] = LocalTime(st);
        if (et != null) o["ende"] = LocalTime(et);
        double ms = MsBetween(st, et); if (ms >= 0) o["dauerMs"] = ms;
        int rr; o["ergebnisProtokoll"] = int.TryParse(LeChild(root, "R"), out rr) ? (rr == 0 ? "ok" : "Code " + rr) : "?";
        // protokollierte Variablen (Block "Logged Variables")
        var vars = new List<string>();
        foreach (XmlNode n in root.ChildNodes)
        {
            var c = n as XmlElement;
            if (c == null || c.Name != "LE" || LeChild(c, "M") != "Logged Variables") continue;
            int d = 0; CollectMessages(c, vars, 400, ref d);
        }
        if (withVariables) o["variablen"] = new JArray(vars.ToArray());
        else o["variablenAnzahl"] = vars.Count;
        var steps = new JArray(); int trunc = 0;
        CollectSteps(root, 0, steps, maxMsgs, ref trunc);
        o["schritte"] = steps;
        if (trunc > 0) o["hinweis"] = trunc + " Meldung(en) gekuerzt - mehr mit maxMessages (bis 500) oder raw=true.";
        return o;
    }

    // ================= acmp_job_log =================

    public JObject JobLog(JObject a)
    {
        string client = StrArg(a, "client"), command = StrArg(a, "command"), resultId = StrArg(a, "resultId"), execId = StrArg(a, "jobExecId");
        int last = Math.Max(1, Math.Min(10, IntArg(a, "last", 1)));
        int maxMsgs = Math.Max(5, Math.Min(500, IntArg(a, "maxMessages", 40)));
        bool vars = BoolArg(a, "variables", false), raw = BoolArg(a, "raw", false), onlyErrors = BoolArg(a, "onlyErrors", false);
        int maxChars = Math.Max(2000, Math.Min(200000, IntArg(a, "maxChars", 40000)));
        if (client == null && resultId == null && execId == null)
            throw new Exception("Welcher Lauf? client (Computername oder ClientId, optional command), resultId (aus acmp_script_history) oder jobExecId angeben.");
        var res = new JObject(); var runs = new JArray();
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            var sql = new StringBuilder("SELECT TOP (@n) r.RESULTID, cl.COMPUTERNAME, r.DESCRIPTION, r.RUNDATE, r.EXECRESULT, r.SCRIPTID, r.ScriptVersion, " +
                "CAST(r.JobExecId AS varchar(36)), r.XmlLogFile, r.LOGFILE FROM CLT_SCRIPT_EXECRESULT r JOIN CLT_CLIENTS_TABLE cl ON cl.CLIENTID = r.CLIENTID WHERE 1 = 1");
            var q = Cmd(c, "");
            q.Parameters.AddWithValue("@n", last);
            if (resultId != null)
            {
                string g = GuidBraces(resultId); if (g == null) throw new Exception("resultId '" + resultId + "' ist keine GUID.");
                sql.Append(" AND r.RESULTID = @rid"); q.Parameters.AddWithValue("@rid", g);
            }
            if (execId != null)
            {
                string g = GuidBraces(execId); if (g == null) throw new Exception("jobExecId '" + execId + "' ist keine GUID.");
                sql.Append(" AND r.JobExecId = @xid"); q.Parameters.AddWithValue("@xid", G(g));
            }
            if (client != null)
            {
                string cid = GuidBraces(client);
                if (cid == null)
                {
                    var cq = Cmd(c, "SELECT TOP 2 CAST(CLIENTID AS varchar(36)) FROM CLT_CLIENTS_TABLE WHERE COMPUTERNAME = @cn ORDER BY IsRetired");
                    cq.Parameters.AddWithValue("@cn", client);
                    using (var rr = cq.ExecuteReader()) if (rr.Read()) cid = Gid(rr, 0);
                    if (cid == null) throw new Exception("Client '" + client + "' nicht gefunden.");
                }
                sql.Append(" AND r.CLIENTID = @cl"); q.Parameters.AddWithValue("@cl", G(cid));
            }
            if (command != null)
            {
                string sg = GuidBraces(command);
                if (sg != null) { sql.Append(" AND r.SCRIPTID = @sid"); q.Parameters.AddWithValue("@sid", sg); }
                else { sql.Append(" AND r.DESCRIPTION LIKE @cmd"); q.Parameters.AddWithValue("@cmd", "%" + command + "%"); }
            }
            if (onlyErrors) sql.Append(" AND r.EXECRESULT <> 0");
            sql.Append(" ORDER BY r.RUNDATE DESC");
            q.CommandText = sql.ToString();
            using (var r = q.ExecuteReader())
                while (r.Read())
                {
                    var o = new JObject();
                    o["rechner"] = Str(r, 1); o["command"] = Str(r, 2);
                    o["version"] = r.IsDBNull(6) ? (JToken)JValue.CreateNull() : Int(r, 6);
                    double rd = r.IsDBNull(3) ? 0 : Convert.ToDouble(r.GetValue(3), CultureInfo.InvariantCulture);
                    if (rd > 0) o["zeit"] = DateTime.FromOADate(rd).ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);
                    int er = r.IsDBNull(4) ? -1 : Int(r, 4);
                    o["ergebnis"] = er < 0 ? "?" : ExecResultText(er) + (er != 0 ? " (" + er + ")" : "");
                    o["resultId"] = Str(r, 0); o["scriptId"] = Str(r, 5); o["jobExecId"] = Gid(r, 7);
                    byte[] xmlBlob = r.IsDBNull(8) ? null : (byte[])r.GetValue(8);
                    byte[] logBlob = r.IsDBNull(9) ? null : (byte[])r.GetValue(9);
                    string text = InflateLog(xmlBlob);
                    if (text == null && logBlob != null) text = InflateLog(logBlob);
                    if (text == null)
                        o["protokoll"] = xmlBlob == null && logBlob == null ? "kein Protokoll gespeichert (Einstellung des Commands: Protokoll nur bei Fehler/nie?)" : "Protokoll nicht entpackbar";
                    else
                    {
                        try
                        {
                            JObject p = ParseScriptLog(text, vars, maxMsgs);
                            foreach (var kv in p) o[kv.Key] = kv.Value;
                        }
                        catch (Exception ex) { o["protokollFehler"] = ex.Message; }
                        if (raw)
                        {
                            // ganze Meldungen maskieren, auch mehrzeilige (Singleline), danach verbliebene Zeilen ausserhalb von <M> nicht anfassen
                            string xs = Regex.Replace(text, @"<M>(.*?)</M>", mm => "<M>" + SecurityElementEscape(MaskSecrets(System.Net.WebUtility.HtmlDecode(mm.Groups[1].Value))) + "</M>", RegexOptions.Singleline);
                            o["xml"] = xs.Length > maxChars ? xs.Substring(0, maxChars) + "\n... (gekuerzt, " + xs.Length + " Zeichen)" : xs;
                        }
                    }
                    runs.Add(o);
                }
        }
        res["laeufe"] = runs;
        if (runs.Count == 0) res["hinweis"] = "Kein Lauf gefunden. Laeufe eines Clients: acmp_script_history client=... (zeigt auch resultId).";
        else res["lesehilfe"] = "ergebnis = Ergebnis des Laufs wie in der Console; je Schritt ergebnis ok/Code n und die Meldungen des Agents (Ausgabe, ExitCode, gesetzte Variablen). Kennwoerter in Variablen und -Password-Argumenten sind maskiert.";
        return res;
    }

    static string SecurityElementEscape(string s) { return System.Security.SecurityElement.Escape(s ?? ""); }

    // Hat der Container schon genau diese Startbedingung? Erst der Text der Console, sonst inhaltlich fuer den Agentenstart, weil aeltere
    // Console-Versionen "Once/Always on ACMP Agent start" schrieben und 6.10.4 "... upon Agent start" (Review 25.10.2026)
    static bool SameCondition(CondInfo have, string haveText, string kind, int every, bool ignore)
    {
        if (string.Equals(ConditionTextFor(kind, every, ignore), haveText, StringComparison.Ordinal)) return true;
        if ((kind == "startup" || kind == "always") && have.Active && have.Enabled == 1 && have.OnStart && have.IgnoresMaintenance == ignore)
            return kind == "startup" ? have.RunOnce : !have.RunOnce;
        return false;
    }

    // ================= acmp_job_create (Client Command als Dauerauftrag) =================

    JObject PlanCcJob(JObject a)
    {
        string condArg = StrArg(a, "condition");
        string kind = condArg != null ? ConditionKind(condArg) : null;
        if (condArg != null && kind == null) throw new Exception("condition '" + condArg + "' unbekannt. Erlaubt: " + string.Join(", ", ConditionKinds) + " (wie acmp_job_set_condition).");
        int every = IntArg(a, "every", 1);
        bool ignore = BoolArg(a, "ignoreMaintenanceWindows", false);
        bool hasEnabled = a["enabled"] != null && a["enabled"].Type != JTokenType.Null;
        bool enabled = BoolArg(a, "enabled", true);
        string cmdArg = StrArg(a, "command");
        if (cmdArg == null) throw new Exception("command fehlt: Name oder ScriptId eines freigegebenen Client Commands (acmp_get_client_commands, acmp_cc_folders commands=true).");
        var plan = new JObject(); var pv = new JArray(); var warn = new List<string>();
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            // Client Command: neueste Version je ScriptId, freigegeben (State 7)
            string sid = GuidBraces(cmdArg);
            var cq = Cmd(c, "WITH v AS (SELECT SCRIPTID, DESCRIPTION, ScriptVersion, State, FOLDERID, ROW_NUMBER() OVER (PARTITION BY SCRIPTID ORDER BY ScriptVersion DESC) AS rn FROM SYS_SCRIPTS) " +
                "SELECT TOP 5 v.SCRIPTID, v.DESCRIPTION, v.ScriptVersion, v.State, (SELECT MAX(x.ScriptVersion) FROM SYS_SCRIPTS x WHERE x.SCRIPTID = v.SCRIPTID AND x.State = 7), " +
                "(SELECT TOP 1 f.Description FROM SYS_SCRIPT_STRUCTURE f WHERE f.ScriptStructureID = v.FOLDERID), " +
                "(SELECT TOP 1 x.COMMANDENABLED FROM SYS_SCRIPTS x WHERE x.SCRIPTID = v.SCRIPTID AND x.State = 7 ORDER BY x.ScriptVersion DESC), " +
                "(SELECT TOP 1 x.HASCLIENTSCRIPT FROM SYS_SCRIPTS x WHERE x.SCRIPTID = v.SCRIPTID AND x.State = 7 ORDER BY x.ScriptVersion DESC) " +
                "FROM v WHERE v.rn = 1 AND " + (sid != null ? "v.SCRIPTID = @s" : "v.DESCRIPTION = @s"));
            cq.Parameters.AddWithValue("@s", sid ?? cmdArg);
            var hits = new List<JObject>();
            using (var r = cq.ExecuteReader())
                while (r.Read())
                {
                    var h = new JObject(); h["id"] = GuidBraces(Str(r, 0)); h["name"] = Str(r, 1); h["latest"] = Int(r, 2); h["state"] = Int(r, 3);
                    h["released"] = r.IsDBNull(4) ? -1 : Int(r, 4); h["folder"] = Str(r, 5);
                    h["enabled"] = r.IsDBNull(6) ? -1 : Int(r, 6); h["clientScript"] = r.IsDBNull(7) ? -1 : Int(r, 7); hits.Add(h);
                }
            if (hits.Count == 0) throw new Exception("Client Command '" + cmdArg + "' nicht gefunden (Name genau wie in der Console oder ScriptId).");
            if (hits.Count > 1) throw new Exception("'" + cmdArg + "' ist nicht eindeutig (" + hits.Count + " Commands) - bitte die ScriptId angeben: " + string.Join(", ", hits.ConvertAll(h => (string)h["id"] + " (" + h["folder"] + ")").ToArray()));
            JObject cc = hits[0];
            if ((int)cc["released"] < 1) throw new Exception("'" + cc["name"] + "' hat keine freigegebene Version - erst freigeben (acmp_cc_release bzw. Console), sonst laeuft der Job nicht.");
            if ((int)cc["enabled"] == 0) throw new Exception("'" + cc["name"] + "' ist in der Console deaktiviert - ein Job wuerde auf jedem Client mit 'Script deaktiviert' (1013) enden.");
            if ((int)cc["clientScript"] == 0) throw new Exception("'" + cc["name"] + "' hat kein Client-Skript (nur Console-Skript) - als Dauerauftrag an Clients sinnlos.");
            if ((int)cc["latest"] != (int)cc["released"]) warn.Add("'" + cc["name"] + "' hat einen neueren Entwurf (v" + cc["latest"] + ") - der Job fuehrt die freigegebene Version v" + cc["released"] + " aus.");
            var pq = Cmd(c, "SELECT COUNT(*) FROM SYS_Jobs_Parameters_Declarations WHERE ContentId = @g");
            pq.Parameters.AddWithValue("@g", G((string)cc["id"]));
            int npar = Convert.ToInt32(pq.ExecuteScalar());
            if (npar > 0) throw new Exception("'" + cc["name"] + "' hat " + npar + " Parameter. Wie die Console Parameterwerte eines Container-Jobs speichert, ist noch nicht belegt - diesen Job bitte in der Console anlegen.");

            string cid = ResolveContainer(c, a, false);
            JObject cont = LookupContainer(c, cid);
            if (cont == null) throw new Exception("Container " + cid + " gibt es nicht - nichts geplant.");
            if ((int)cont["osFamily"] != 0) throw new Exception("Container " + cont["path"] + " ist kein Windows-Container (" + OsFamilyText((int)cont["osFamily"]) + ") - Client Commands laufen nur auf Windows; Linux: Unix Scripts.");

            var ex = new List<JObject>();
            var eq = Cmd(c, "SELECT CAST(j.Id AS varchar(36)), j.Name, j.JobKind, CAST(j.ContentId AS varchar(36)), CAST(j.Conditions AS nvarchar(max)), ISNULL(j.ConditionCheckSum, ''), " +
                "(SELECT TOP 1 l.TestRing FROM SYS_Jobs_Logs l WHERE l.JobId = j.Id), (SELECT TOP 1 CAST(tj.TemplateId AS varchar(36)) FROM SYS_Templates_Jobs tj WHERE tj.JobId = j.Id), " +
                "(SELECT COUNT(*) FROM SYS_Templates_Jobs tj WHERE tj.JobId = j.Id) FROM SYS_Jobs j WHERE j.ContainerId = @c AND j.IsManualJob = 0 ORDER BY j.CreationDate");
            eq.Parameters.AddWithValue("@c", G(cid));
            using (var r = eq.ExecuteReader())
                while (r.Read())
                {
                    var o = new JObject();
                    o["id"] = Gid(r, 0); o["name"] = Str(r, 1); o["kind"] = Int(r, 2); o["contentId"] = Gid(r, 3); o["conditions"] = Str(r, 4); o["checksum"] = Str(r, 5);
                    o["ring"] = r.IsDBNull(6) ? -1 : Int(r, 6); o["templateId"] = Gid(r, 7); o["links"] = Int(r, 8);
                    ex.Add(o);
                }
            foreach (JObject o in ex)
                if ((int)o["kind"] == CcKind && string.Equals((string)o["contentId"], (string)cc["id"], StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Am Container " + cont["path"] + " gibt es schon einen Dauerauftrag fuer '" + cc["name"] + "' (" + o["id"] + ") - nichts geplant. Aendern: acmp_job_set_condition / acmp_job_set_enabled.");
            if (ex.Count + 1 > MaxContainerJobs) throw new Exception("Container " + cont["path"] + " hat schon " + ex.Count + " Dauerauftraege (hoechstens " + MaxContainerJobs + ") - bitte in der Console.");

            // Verteilungsring wie die vorhandenen Jobs (die Console setzt ihn je Container: JobAction.modify(TestRing) fuer alle Jobs), sonst Freigabering
            int ring = RingAll; var rings = new HashSet<int>();
            foreach (JObject o in ex) if ((int)o["ring"] >= 0) rings.Add((int)o["ring"]);
            if (rings.Count > 1) throw new Exception("Die Jobs von " + cont["path"] + " haben verschiedene Verteilungsringe - bitte in der Console klaeren.");
            foreach (int x in rings) ring = x;
            if (ring != RingAll) warn.Add("Der Container laeuft mit " + RingText(ring) + " (wie seine anderen Jobs) - fuer Client Commands spielt der Ring keine Rolle, er wird nur wie in der Console mitgefuehrt.");

            string condXml, cs, csSrc;
            if (ex.Count > 0)
            {
                var keys = new HashSet<string>();
                foreach (JObject o in ex) keys.Add(ConditionKey((string)o["conditions"]) ?? "?");
                if (keys.Count > 1) throw new Exception("Die Dauerauftraege von " + cont["path"] + " haben verschiedene Startbedingungen - bitte zuerst angleichen (acmp_job_set_condition).");
                condXml = (string)ex[0]["conditions"]; cs = (string)ex[0]["checksum"];
                csSrc = "wie der vorhandene Job \"" + ex[0]["name"] + "\" des Containers";
                CondInfo have = ParseConditions(condXml);
                if (kind != null)
                {
                    CheckCondition(kind, every, ignore);
                    string haveText = have.Readable ? ParseConditionsConsole(condXml) : "?";
                    if (!have.Active || !SameCondition(have, haveText, kind, every, ignore))
                        throw new Exception("Container " + cont["path"] + " hat schon die Startbedingung '" + have.Text + "' [Console: " + haveText + "]; die Console fuehrt sie je Container. " +
                                            "Ohne condition aufrufen (der neue Job uebernimmt sie) oder danach acmp_job_set_condition (gilt dann fuer alle Jobs des Containers).");
                }
                if (hasEnabled && enabled != have.Active)
                    throw new Exception("Die Startbedingung von " + cont["path"] + " ist " + (have.Active ? "an" : "aus") + " und gilt je Container - enabled weglassen oder danach acmp_job_set_enabled.");
                if (!have.Active) warn.Add("Die Startbedingung des Containers ist AUS ('" + have.Text + "') - der neue Job laeuft erst, wenn sie eingeschaltet wird.");
            }
            else
            {
                if (kind == null) throw new Exception("condition fehlt (der Container hat noch keine Dauerauftraege): " + string.Join(", ", ConditionKinds) + ", optional every und ignoreMaintenanceWindows.");
                CheckCondition(kind, every, ignore);
                condXml = ConditionXml(null, kind, every, ignore, NewId());
                if (!enabled) condXml = DisableXml(condXml);
                cs = ChecksumFor(condXml, LoadChecksumRows(c), OwnChecksums(), out csSrc);
            }
            string condText = ParseConditionsConsole(condXml);
            CondInfo ci = ParseConditions(condXml);
            if (ci.Hourly || (ci.OnStart && !ci.RunOnce) || (ci.InMaintenance && !ci.RunOnce))
                warn.Add("Die Startbedingung wiederholt sich (" + ci.Text + ") - der Command laeuft immer wieder auf jedem Mitglied. Fuer eine einmalige Verteilung condition=once oder startup.");

            // Job-Vorlage des Containers
            string tplId = null; byte[] kindsOld = null; int tplCount = 0;
            var tq = Cmd(c, "SELECT CAST(t.Id AS varchar(36)), t.JobKinds, t.OsFamily FROM SYS_Containers_Templates ct JOIN SYS_Templates t ON t.Id = ct.TemplateId " +
                "WHERE ct.ContainerId = @c AND t.Name LIKE '" + ContainerTemplatePrefix + "%' AND ISNULL(t.IsReferenceCounted, 0) = 0 AND t.FolderId IS NULL");
            tq.Parameters.AddWithValue("@c", G(cid));
            using (var r = tq.ExecuteReader())
                while (r.Read()) { tplCount++; tplId = Gid(r, 0); kindsOld = r.IsDBNull(1) ? null : (byte[])r.GetValue(1); if (Int(r, 2) != 0) throw new Exception("Die Job-Vorlage von " + cont["path"] + " ist nicht fuer Windows - bitte in der Console."); }
            if (tplCount > 1) throw new Exception("Container " + cont["path"] + " hat " + tplCount + " Job-Vorlagen - bitte in der Console.");
            foreach (JObject o in ex)
                if ((int)o["links"] != 1 || tplId == null || !string.Equals((string)o["templateId"], tplId, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Job \"" + o["name"] + "\" haengt nicht (nur) an der Job-Vorlage des Containers - bitte in der Console.");
            bool tplNew = tplId == null;
            if (tplNew) tplId = NewId();
            byte[] kindsNew = KindsMask(kindsOld, CcKind);

            string jobId = NewId();
            string jobAction = CcJobActionXml(NewId(), (string)cc["id"], (string)cc["name"], ring);

            // Mitglieder
            int mem = 0, other = 0; var names = new List<string>(); var memberIds = new List<string>();
            var mq = Cmd(c, "SELECT cl.COMPUTERNAME, CASE WHEN EXISTS (SELECT 1 FROM CLT_UnixClients u WHERE u.ClientId = cl.CLIENTID) OR cl.COMPUTERNAME LIKE '[[]NETWORK BOOT]%' THEN 1 ELSE 0 END, " +
                "CAST(cl.CLIENTID AS varchar(36)) FROM CLT_CONTAINER_ITEMS ci JOIN CLT_CLIENTS_TABLE cl ON cl.CLIENTID = ci.LinkID AND cl.IsRetired = 0 WHERE ci.CONTAINERID = @c ORDER BY cl.COMPUTERNAME");
            mq.Parameters.AddWithValue("@c", G(cid));
            using (var r = mq.ExecuteReader())
                while (r.Read()) { mem++; memberIds.Add(Str(r, 2)); if (Int(r, 1) == 1) other++; if (names.Count < 40) names.Add(Str(r, 0)); }
            var others = new List<string>();
            var oq = Cmd(c, "SELECT s.Path, CAST(j.Conditions AS nvarchar(max)) FROM SYS_Jobs j LEFT JOIN CLT_CONTAINER_STRUCTURE s ON s.ContainerID = j.ContainerId " +
                "WHERE j.JobKind = 4 AND j.IsManualJob = 0 AND j.ContentId = @g");
            oq.Parameters.AddWithValue("@g", G((string)cc["id"]));
            using (var r = oq.ExecuteReader()) while (r.Read()) others.Add((Str(r, 0) ?? "?") + " (" + ParseConditions(Str(r, 1)).Text + ")");
            // Anders als bei Managed Software kann ein Client Command beliebiges Skript sein: Container mit dem ACMP-/MCP-Server lehnt das Tool ab
            // (wie acmp_run_powershell und EnqueueClientCommand; Review 25.10.2026). Dort bitte in der Console arbeiten.
            if (memberIds.Count > 0)
            {
                if (GuardTargets == null) throw new Exception("Pruefung auf geschuetzte Clients nicht verfuegbar - nichts geplant.");
                try { GuardTargets(string.Join(" ", memberIds.ToArray())); }
                catch (Exception gx) { throw new Exception("Container " + cont["path"] + " enthaelt einen geschuetzten Client (ACMP-/MCP-Server): " + gx.Message.Split('(')[0].Trim() + " Ein Client-Command-Job dort nur in der ACMP-Console."); }
            }
            string srv = LatestServerId(c);

            plan["action"] = "ccjob";
            plan["jobId"] = jobId; plan["name"] = cc["name"]; plan["contentId"] = cc["id"]; plan["releasedVersion"] = cc["released"];
            plan["containerId"] = cont["id"]; plan["path"] = cont["path"]; plan["testRing"] = ring;
            plan["templateId"] = tplId; plan["templateNew"] = tplNew; plan["templateName"] = ContainerTemplatePrefix + ": " + cont["id"];
            plan["kindsOld"] = HexOf(kindsOld); plan["kindsNew"] = HexOf(kindsNew);
            plan["templateJobId"] = NewId(); plan["containerTemplateId"] = NewId();
            plan["conditionsXml"] = condXml; plan["checksum"] = cs; plan["checksumQuelle"] = csSrc; plan["conditionText"] = condText;
            plan["jobActionXml"] = jobAction; plan["serverId"] = srv;
            var exIds = new JArray(); foreach (JObject o in ex) exIds.Add(o["id"]);
            plan["existingJobIds"] = exIds;

            pv.Add("CLIENT-COMMAND-DAUERAUFTRAG ANLEGEN wie die Console: '" + cc["name"] + "' (freigegebene Version v" + cc["released"] + ", Ordner " + (cc["folder"] ?? "?") + ") am Container " + cont["path"] + " (" + mem + " Mitglieder).");
            pv.Add("   Startbedingung: " + ci.Text + " [Console: " + condText + "], Pruefsumme " + Cs8(cs) + " " + csSrc + ".");
            pv.Add("   Job-Vorlage: " + (tplNew ? "neu ('" + plan["templateName"] + "', mit dem Container verknuepft)" : "vorhandene des Containers" + (HexOf(kindsOld) != HexOf(kindsNew) ? ", Art Client Command wird ergaenzt" : "")) +
                   (ex.Count > 0 ? "; dort schon: " + ex.Count + " Job(s)" : "") + ".");
            pv.Add("Wirkung: Der Command laeuft auf allen Mitgliedern" + (names.Count > 0 ? ": " + string.Join(", ", names.ToArray()) + (mem > names.Count ? ", ..." : "") : "") + ".");
            if (other > 0) warn.Add(other + " Mitglied(er) sind Linux-Clients bzw. [NETWORK BOOT] - dort laeuft kein Client Command (acmp_container_set_restriction).");
            if (mem == 0) warn.Add("Der Container hat keine Mitglieder - der Job laeuft erst, wenn Clients dazukommen.");
            if (others.Count > 0) warn.Add("Derselbe Command hat schon Dauerauftraege an: " + string.Join("; ", others.ToArray()) + ".");
            if (srv == null) warn.Add("ServerId nicht ermittelbar - der Job wird ohne ServerId angelegt.");
        }
        foreach (string w in warn) pv.Add("Hinweis: " + w);
        pv.Add("Ablauf: Sicherung, dann eine Transaktion (Job-Vorlage, SYS_Jobs, Job-Monitor-Eintrag, Verknuepfung). Die Agents holen den Job mit ihrer naechsten Job-Liste. " +
               "Zurueck: acmp_job_restore mit der Sicherung (loescht den neuen Job und eine neu angelegte, leere Vorlage) oder acmp_job_delete.");
        plan["vorschau"] = pv;
        return plan;
    }

    JObject ExecCcJob(JObject plan, string requester)
    {
        string id = (string)plan["jobId"], cid = (string)plan["containerId"], tpl = (string)plan["templateId"];
        bool tplNew = (bool)plan["templateNew"];
        string label = "CC-Job \"" + plan["name"] + "\" an " + plan["path"];
        var b = new JObject();
        b["kind"] = "ccjob"; b["tool"] = "acmp_job_create"; b["von"] = requester; b["zeit"] = DateTime.Now.ToString("s"); b["jobId"] = id; b["name"] = plan["name"];
        b["target"] = plan["path"]; b["containerId"] = cid; b["contentId"] = plan["contentId"]; b["templateId"] = tpl; b["templateNew"] = tplNew;
        b["templateJobId"] = plan["templateJobId"]; b["containerTemplateId"] = plan["containerTemplateId"]; b["kindsOld"] = plan["kindsOld"]; b["kindsNew"] = plan["kindsNew"];
        var after = new JObject(); after["checksum"] = plan["checksum"]; after["checksumQuelle"] = plan["checksumQuelle"]; after["xml"] = plan["conditionsXml"]; after["logText"] = plan["conditionText"];
        b["after"] = after; b["jobAction"] = plan["jobActionXml"]; b["testRing"] = plan["testRing"];
        string bf = Path.GetFileName(SaveBackup(b, "ccjob", id));
        double now = DateTime.Now.ToOADate();
        using (var wc = new SqlConnection(_writeCs))
        {
            wc.Open();
            using (SqlTransaction tx = wc.BeginTransaction())
            {
                var k = TxCmd(wc, tx, "SELECT COUNT(*) FROM CLT_CONTAINER_STRUCTURE WHERE ContainerID = @c");
                k.Parameters.AddWithValue("@c", G(cid));
                if (Convert.ToInt32(k.ExecuteScalar()) != 1) throw new Exception(label + ": Container gibt es nicht mehr - zurueckgerollt.");
                var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase); bool dup = false;
                var jq = TxCmd(wc, tx, "SELECT CAST(Id AS varchar(36)), CAST(ContentId AS varchar(36)), JobKind FROM SYS_Jobs WITH (UPDLOCK, HOLDLOCK) WHERE ContainerId = @c AND IsManualJob = 0");
                jq.Parameters.AddWithValue("@c", G(cid));
                using (var r = jq.ExecuteReader()) while (r.Read()) { have.Add(Gid(r, 0)); if (Int(r, 2) == CcKind && string.Equals(Gid(r, 1), (string)plan["contentId"], StringComparison.OrdinalIgnoreCase)) dup = true; }
                if (dup) throw new Exception(label + ": inzwischen gibt es dort schon einen Job fuer den Command - zurueckgerollt, nichts angelegt.");
                var want = new HashSet<string>(StringComparer.OrdinalIgnoreCase); foreach (JToken t in (JArray)plan["existingJobIds"]) want.Add((string)t);
                if (!have.SetEquals(want)) throw new Exception(label + ": die Dauerauftraege des Containers haben sich seit der Planung geaendert - zurueckgerollt, bitte neu aufrufen.");
                var sq = TxCmd(wc, tx, "SELECT COUNT(*) FROM SYS_SCRIPTS WHERE SCRIPTID = @s AND State = 7 AND ScriptVersion = @v");
                sq.Parameters.AddWithValue("@s", (string)plan["contentId"]); sq.Parameters.AddWithValue("@v", (int)plan["releasedVersion"]);
                if (Convert.ToInt32(sq.ExecuteScalar()) != 1) throw new Exception(label + ": die freigegebene Version des Commands hat sich seit der Planung geaendert - zurueckgerollt, bitte neu aufrufen.");
                var tq = TxCmd(wc, tx, "SELECT CAST(t.Id AS varchar(36)), t.JobKinds FROM SYS_Containers_Templates ct JOIN SYS_Templates t WITH (UPDLOCK) ON t.Id = ct.TemplateId " +
                    "WHERE ct.ContainerId = @c AND t.Name LIKE '" + ContainerTemplatePrefix + "%' AND ISNULL(t.IsReferenceCounted, 0) = 0 AND t.FolderId IS NULL");
                tq.Parameters.AddWithValue("@c", G(cid));
                string curTpl = null; byte[] curKinds = null; int n = 0;
                using (var r = tq.ExecuteReader()) while (r.Read()) { n++; curTpl = Gid(r, 0); curKinds = r.IsDBNull(1) ? null : (byte[])r.GetValue(1); }
                if (tplNew ? n != 0 : (n != 1 || !string.Equals(curTpl, tpl, StringComparison.OrdinalIgnoreCase)))
                    throw new Exception(label + ": die Job-Vorlage des Containers hat sich seit der Planung geaendert - zurueckgerollt, bitte neu aufrufen.");
                byte[] kinds = BytesOfHex((string)plan["kindsNew"]);
                if (tplNew)
                {
                    var iq = TxCmd(wc, tx, "INSERT INTO SYS_Templates (Id, Name, Description, IsSystemTemplate, IsClientSpecific, IsReferenceCounted, FolderId, JobKinds, OsFamily) VALUES (@t, @n, N'', 0, 0, 0, NULL, @k, 0)");
                    iq.Parameters.AddWithValue("@t", G(tpl)); iq.Parameters.AddWithValue("@n", (string)plan["templateName"]);
                    iq.Parameters.Add(new SqlParameter("@k", SqlDbType.Binary, 16) { Value = kinds });
                    iq.ExecuteNonQuery();
                }
                else if (HexOf(curKinds) != HexOf(kinds))
                {
                    if (HexOf(curKinds) != (string)plan["kindsOld"]) throw new Exception(label + ": JobKinds der Vorlage geaendert - zurueckgerollt.");
                    var uq = TxCmd(wc, tx, "UPDATE SYS_Templates SET JobKinds = @k WHERE Id = @t");
                    uq.Parameters.Add(new SqlParameter("@k", SqlDbType.Binary, 16) { Value = kinds }); uq.Parameters.AddWithValue("@t", G(tpl));
                    if (uq.ExecuteNonQuery() != 1) throw new Exception(label + ": Job-Vorlage nicht eindeutig - zurueckgerollt.");
                }
                var jq2 = TxCmd(wc, tx, "INSERT INTO SYS_Jobs (Id, ContainerId, Conditions, ConditionCheckSum, JobAction, ContentId, IsManualJob, CanRunParallel, Name, Description, Priority, Origin, JobKind, ServerId, CreationDate) " +
                    "VALUES (@id, @c, @cond, @cs, @ja, @ct, 0, 0, @n, N'', 0, 1, 4, @srv, @cd)");
                jq2.Parameters.AddWithValue("@id", G(id)); jq2.Parameters.AddWithValue("@c", G(cid));
                jq2.Parameters.Add(XmlPar("@cond", (string)plan["conditionsXml"])); jq2.Parameters.AddWithValue("@cs", (string)plan["checksum"]);
                jq2.Parameters.Add(XmlPar("@ja", (string)plan["jobActionXml"])); jq2.Parameters.AddWithValue("@ct", G((string)plan["contentId"]));
                jq2.Parameters.AddWithValue("@n", (string)plan["name"]);
                jq2.Parameters.AddWithValue("@srv", string.IsNullOrEmpty((string)plan["serverId"]) ? (object)DBNull.Value : G((string)plan["serverId"]));
                jq2.Parameters.AddWithValue("@cd", now);
                jq2.ExecuteNonQuery();
                var lq = TxCmd(wc, tx, "INSERT INTO SYS_Jobs_Logs (JobId, JobName, CreatorName, CreationDate, ContentId, ContainerId, State, ConditionText, TestRing, JobKind, Origin) " +
                    "VALUES (@id, @n, @who, @cd, @ct, @c, 1, @txt, @ring, 4, 1)");
                lq.Parameters.AddWithValue("@id", G(id)); lq.Parameters.AddWithValue("@n", (string)plan["name"]); lq.Parameters.AddWithValue("@who", "MCP " + requester);
                lq.Parameters.AddWithValue("@cd", now); lq.Parameters.AddWithValue("@ct", G((string)plan["contentId"])); lq.Parameters.AddWithValue("@c", G(cid));
                lq.Parameters.AddWithValue("@txt", (string)plan["conditionText"] ?? ""); lq.Parameters.AddWithValue("@ring", (byte)(int)plan["testRing"]);
                lq.ExecuteNonQuery();
                var vq = TxCmd(wc, tx, "INSERT INTO SYS_Templates_Jobs (Id, TemplateId, JobId, JobKind) VALUES (@tj, @t, @id, 4)");
                vq.Parameters.AddWithValue("@tj", G((string)plan["templateJobId"])); vq.Parameters.AddWithValue("@t", G(tpl)); vq.Parameters.AddWithValue("@id", G(id));
                vq.ExecuteNonQuery();
                if (tplNew)
                {
                    var cq = TxCmd(wc, tx, "INSERT INTO SYS_Containers_Templates (Id, TemplateId, ContainerId) VALUES (@x, @t, @c)");
                    cq.Parameters.AddWithValue("@x", G((string)plan["containerTemplateId"])); cq.Parameters.AddWithValue("@t", G(tpl)); cq.Parameters.AddWithValue("@c", G(cid));
                    cq.ExecuteNonQuery();
                }
                var ck = TxCmd(wc, tx, "SELECT COUNT(*) FROM SYS_Jobs j JOIN SYS_Templates_Jobs tj ON tj.JobId = j.Id JOIN SYS_Containers_Templates ct ON ct.TemplateId = tj.TemplateId " +
                    "WHERE j.Id = @id AND ct.ContainerId = @c AND j.ContainerId = @c AND j.JobKind = 4");
                ck.Parameters.AddWithValue("@id", G(id)); ck.Parameters.AddWithValue("@c", G(cid));
                if (Convert.ToInt32(ck.ExecuteScalar()) != 1) throw new Exception(label + ": Kontrolle nach dem Anlegen fehlgeschlagen - zurueckgerollt.");
                tx.Commit();
            }
        }
        _log("acmp_job_create von " + requester + ": " + label + " (" + plan["conditionText"] + "), Job " + id + ", Sicherung " + bf);
        var res = new JObject();
        res["ok"] = true; res["aktion"] = "Client-Command-Dauerauftrag angelegt"; res["jobId"] = id; res["command"] = plan["name"]; res["container"] = plan["path"];
        res["startbedingung"] = ParseConditions((string)plan["conditionsXml"]).Text;
        res["pruefsumme"] = Cs8((string)plan["checksum"]) + " " + plan["checksumQuelle"]; res["sicherung"] = bf;
        res["zurueck"] = "acmp_job_restore backup=" + bf + " (loescht den Job" + (tplNew ? " und die neu angelegte Vorlage" : "") + ") oder acmp_job_delete jobId=" + id + ".";
        res["hinweis"] = "Die Agents holen den Job mit ihrer naechsten Job-Liste. Pruefen: acmp_jobs name=\"" + plan["name"] + "\", Laeufe mit acmp_job_log client=... command=\"" + plan["name"] + "\" und in der Console (Container, Reiter Jobs).";
        return res;
    }
}
