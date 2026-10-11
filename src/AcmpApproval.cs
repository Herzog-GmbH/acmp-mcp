// Vier-Augen-Freigabe fuer schreibende/ausfuehrende ACMP-Kommandos.
// - Klassifiziert jeden Tool-Aufruf in Read / Write / Destructive (serverseitig, nicht im LLM).
// - Read laeuft sofort. Write/Destructive werden NICHT ausgefuehrt, sondern als Freigabe-Anfrage
//   eingefroren (exakter Payload + SHA256). Erst wenn genug verschiedene IT-Mitarbeiter ueber die
//   Web-Freigabeseite (out-of-band, nicht ueber das LLM) zustimmen, fuehrt ein Worker den
//   EINGEFRORENEN Payload aus. Alles wird ins Audit-Log geschrieben.
// net48 / C# 5 (csc v4.0.30319): keine string-Interpolation, keine expression-bodied member, kein ?.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

enum RiskTier { Read, Write, Destructive }

class AcmpApproval
{
    public bool Enabled;
    public bool NotifyEnabled;

    // Einstellungsseite: persistentes Speichern und Tool-Uebersicht
    public string CfgPath;
    public HashSet<string> NoApprovalTools;
    public bool BypassAll;
    public Dictionary<string, string> GatedToolTiers;
    public Func<JArray> GetAllTools;
    public Dictionary<string, int> ToolApprovalCounts;
    Dictionary<string, DateTime> _readApprovals = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

    string _dir;                 // Verzeichnis fuer die Freigabe-Anfragen (*.json)
    string _auditFile;
    string _publicUrl;           // Basis-URL der Freigabeseite (fuer Links an die Menschen)
    int _writeApprovers;         // benoetigte Zustimmungen fuer Write
    int _destructiveApprovers;   // benoetigte Zustimmungen fuer Destructive
    int _expiryMinutes;          // offene Anfrage verfaellt nach X Minuten
    bool _blockDangerous;        // true => gefaehrliche Muster hart blockieren (nie zur Freigabe zulassen)

    Dictionary<string, string> _approvers;   // Name -> Token (jeder IT-Freigeber)
    Dictionary<string, string> _emails;      // Name -> E-Mail-Adresse (fuer Benachrichtigung)
    string _setupToken;                      // != null => Ersteinrichtung aktiv (kein Freigeber konfiguriert); erlaubt /setup ohne Login
    List<Regex> _blockPatterns;              // zusaetzliche harte Denylist aus der Config
    List<string> _extraDestructive;          // zusaetzliche Kommando-Praefixe als Destructive

    bool _notify;                            // E-Mail-Benachrichtigung an Freigeber aktiv
    string _smtpHost; int _smtpPort; bool _smtpSsl;
    string _smtpUser; string _smtpPass; string _mailFrom;
    string _requester;                       // Antragsteller (Betreiber des MCP); bei 1 noetiger Zustimmung wird NUR er benachrichtigt

    Func<JObject, string> _executor;         // fuehrt den eingefrorenen Payload aus -> Roh-XML
    Action<string> _log;

    // Optionale Aufloeser GUID -> lesbarer Name (vom Server gesetzt), fuer verstaendliche Freigabe-Beschreibungen.
    public Func<string, string> ResolveClientName;
    public Func<string, string> ResolveClientUser;
    public Func<string, string> ResolveCommandName;

    readonly object _gate = new object();
    Dictionary<string, JObject> _reqs = new Dictionary<string, JObject>();

    static readonly string[] ReadPrefixes = new string[] {
        "get", "list", "enum", "find", "search", "query", "describe",
        "has", "is", "check", "count", "export", "download", "read", "load"
    };
    static readonly string[] DestructivePrefixes = new string[] {
        "delete", "remove", "uninstall", "reset", "wipe", "clear", "purge",
        "drop", "format", "shutdown", "reboot", "restart", "kill", "revoke", "erase"
    };
    // Gefaehrliche Muster in PowerShell-/Befehlsinhalten (fuer Beschreibung + optionalen Hard-Block).
    static readonly string[] DangerSrc = new string[] {
        @"remove-item",        @"\brd\s+/s",          @"\brmdir\s+/s",
        @"format-volume",      @"clear-disk",         @"\bdiskpart\b",
        @"cipher\s+/w",        @"reg\s+delete",       @"reg\.exe\s+delete",
        @"stop-computer",      @"restart-computer",   @"stop-service",
        @"set-service",        @"\bbcdedit\b",        @"vssadmin\s+delete",
        @"wevtutil\s+cl",      @"remove-aduser",      @"remove-mailbox",
        @"net\s+user\s+.*\s+/delete", @"uninstall-",  @"set-executionpolicy",
        @"downloadstring",     @"invoke-expression",  @"\biex\b",
        @"-recurse",           @"-force"
    };
    List<Regex> _danger;

    public AcmpApproval(JObject cfg, string baseDir, string publicUrl, Func<JObject, string> executor, Action<string> log)
    {
        _log = log;
        _executor = executor;
        _approvers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _emails = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _blockPatterns = new List<Regex>();
        _extraDestructive = new List<string>();
        _danger = new List<Regex>();
        foreach (string d in DangerSrc)
        {
            try { _danger.Add(new Regex(d, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)); }
            catch { }
        }

        if (cfg == null) { Enabled = false; return; }
        Enabled = cfg["enabled"] == null || (bool)cfg["enabled"];
        if (!Enabled) return;

        string d2 = cfg["pendingDir"] != null ? (string)cfg["pendingDir"] : "pending";
        _dir = Path.IsPathRooted(d2) ? d2 : Path.Combine(baseDir, d2);
        Directory.CreateDirectory(_dir);

        string af = cfg["auditFile"] != null ? (string)cfg["auditFile"] : "approval-audit.log";
        _auditFile = Path.IsPathRooted(af) ? af : Path.Combine(baseDir, af);

        _publicUrl = (cfg["publicUrl"] != null && ((string)cfg["publicUrl"]).Length > 0) ? ((string)cfg["publicUrl"]).TrimEnd('/') : publicUrl;
        _writeApprovers = cfg["writeApprovers"] != null ? (int)cfg["writeApprovers"] : 1;
        _destructiveApprovers = cfg["destructiveApprovers"] != null ? (int)cfg["destructiveApprovers"] : 2;
        _expiryMinutes = cfg["expiryMinutes"] != null ? (int)cfg["expiryMinutes"] : 120;
        _blockDangerous = cfg["blockDangerous"] != null && (bool)cfg["blockDangerous"];

        JArray ap = cfg["approvers"] as JArray;
        if (ap != null)
            foreach (JObject a in ap)
            {
                string nm = (string)a["name"]; string tk = (string)a["token"];
                if (!string.IsNullOrEmpty(nm) && !string.IsNullOrEmpty(tk) && !tk.StartsWith("HIER_")) _approvers[nm] = tk;
                if (!string.IsNullOrEmpty(nm) && a["email"] != null && ((string)a["email"]).Length > 0) _emails[nm] = (string)a["email"];
            }

        // Ersteinrichtung: kein Freigeber konfiguriert -> einmaligen Setup-Token erzeugen und loggen,
        // damit der allererste Administrator ohne manuelles JSON-Editieren per Weboberflaeche angelegt werden kann.
        if (_approvers.Count == 0)
        {
            _setupToken = RandomToken(24);
            _log("=== ERSTEINRICHTUNG NOETIG === Kein Freigeber konfiguriert. Oeffne " + _publicUrl + "/setup?token=" + _setupToken + " im Browser, um den ersten Administrator/Freigeber anzulegen.");
        }

        // E-Mail-Benachrichtigung (SMTP). Der Server-Prozess versendet selbst, nicht ueber MCP/M365-Tools.
        JObject nf = cfg["notify"] as JObject;
        if (nf != null && (nf["enabled"] == null || (bool)nf["enabled"]))
        {
            _smtpHost = nf["smtpHost"] != null ? (string)nf["smtpHost"] : null;
            if (_smtpHost != null && (_smtpHost.Length == 0 || _smtpHost.StartsWith("HIER_"))) _smtpHost = null;
            _smtpPort = nf["smtpPort"] != null ? (int)nf["smtpPort"] : 25;
            _smtpSsl = nf["useSsl"] != null && (bool)nf["useSsl"];
            _smtpUser = nf["username"] != null ? (string)nf["username"] : null;
            if (_smtpUser != null && (_smtpUser.Length == 0 || _smtpUser.StartsWith("HIER_"))) _smtpUser = null;
            _smtpPass = nf["password"] != null ? (string)nf["password"] : null;
            _mailFrom = nf["from"] != null ? (string)nf["from"] : null;
            _notify = _smtpHost != null && !string.IsNullOrEmpty(_mailFrom);
        }
        NotifyEnabled = _notify;

        JArray bp = cfg["blockPatterns"] as JArray;
        if (bp != null)
            foreach (JToken t in bp)
            {
                string s = (string)t; if (string.IsNullOrEmpty(s)) continue;
                try { _blockPatterns.Add(new Regex(s, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)); }
                catch { _blockPatterns.Add(new Regex(Regex.Escape(s), RegexOptions.IgnoreCase)); }
            }

        JArray ed = cfg["extraDestructive"] as JArray;
        if (ed != null)
            foreach (JToken t in ed) { string s = (string)t; if (!string.IsNullOrEmpty(s)) _extraDestructive.Add(s.ToLowerInvariant()); }

        _requester = cfg["requester"] != null ? (string)cfg["requester"] : null;
        if (_requester != null && (_requester.Length == 0 || _requester.StartsWith("HIER_"))) _requester = null;
    }

    // ---------- Klassifizierung ----------
    public RiskTier Classify(string command)
    {
        if (string.IsNullOrEmpty(command)) return RiskTier.Write;
        string c = command.Trim().ToLowerInvariant();
        foreach (string p in DestructivePrefixes) if (c.StartsWith(p)) return RiskTier.Destructive;
        foreach (string p in _extraDestructive) if (c.StartsWith(p)) return RiskTier.Destructive;
        foreach (string p in ReadPrefixes) if (c.StartsWith(p)) return RiskTier.Read;
        return RiskTier.Write;
    }

    public int ApproversNeeded(RiskTier tier)
    {
        int n = tier == RiskTier.Destructive ? _destructiveApprovers : _writeApprovers;
        if (n < 1) n = 1;
        return n;
    }

    // Harte Denylist: matched ein gefaehrliches Muster (nur wenn blockDangerous) oder ein Config-blockPattern.
    public bool IsBlocked(string command, string payload, out string reason)
    {
        reason = null;
        string hay = (command != null ? command : "") + "\n" + (payload != null ? payload : "");
        foreach (Regex r in _blockPatterns)
            if (r.IsMatch(hay)) { reason = "blockPattern '" + r.ToString() + "'"; return true; }
        if (_blockDangerous)
            foreach (Regex r in _danger)
                if (r.IsMatch(hay)) { reason = "gefaehrliches Muster '" + r.ToString() + "'"; return true; }
        return false;
    }

    List<string> MatchedDangers(string hay)
    {
        var hits = new List<string>();
        if (hay == null) return hits;
        foreach (Regex r in _danger) if (r.IsMatch(hay)) hits.Add(r.ToString());
        return hits;
    }

    // Erfasst strukturierte Metadaten (Client/Benutzer/Aktion/Variablen/Warnungen) EINMAL bei Anlage,
    // loest GUIDs in lesbare Namen auf und legt sie in der Anfrage ab. Quelle fuer Beschreibung, Schritte und Mail.
    void PopulateMeta(JObject f, string payloadText, string scanExtra, string tool, string command, RiskTier tier, string intent, string requester)
    {
        string p = payloadText != null ? payloadText : "";
        f["tool"] = tool;
        f["intent"] = intent != null ? intent : "";
        string req = !string.IsNullOrEmpty(requester) ? requester : _requester;
        if (req != null) f["requester"] = req;

        Match cm = Regex.Match(p, "<ClientId>([^<]+)</ClientId>", RegexOptions.IgnoreCase);
        if (cm.Success)
        {
            string g = cm.Groups[1].Value;
            f["clientGuid"] = g;
            string nm = SafeResolve(ResolveClientName, g); if (nm != null) f["clientName"] = nm;
            string us = SafeResolve(ResolveClientUser, g); if (us != null) f["clientUser"] = us;
        }
        Match ccm = Regex.Match(p, "<ClientCommandId>([^<]+)</ClientCommandId>", RegexOptions.IgnoreCase);
        if (ccm.Success)
        {
            string g = ccm.Groups[1].Value;
            f["commandGuid"] = g;
            string nm = SafeResolve(ResolveCommandName, g); if (nm != null) f["commandName"] = nm;
        }
        f["ignoreMaintenance"] = Regex.IsMatch(p, "<CanIgnoreMaintenanceTimes>\\s*true", RegexOptions.IgnoreCase);

        JArray vs = new JArray();
        foreach (Match v in Regex.Matches(p, "<TClientCommandVariable_V1><Name>([^<]*)</Name><Value>(.*?)</Value></TClientCommandVariable_V1>", RegexOptions.Singleline))
        {
            JObject vo = new JObject(); vo["name"] = v.Groups[1].Value; vo["value"] = Unesc(v.Groups[2].Value); vs.Add(vo);
        }
        f["vars"] = vs;

        if (!cm.Success && !ccm.Success)
        {
            JArray ids = new JArray();
            foreach (Match m in Regex.Matches(p, "<guid>([^<]+)</guid>", RegexOptions.IgnoreCase)) ids.Add(m.Groups[1].Value);
            if (ids.Count > 0) f["objectIds"] = ids;
        }

        List<string> hits = MatchedDangers(p + "\n" + (scanExtra != null ? scanExtra : ""));
        JArray da = new JArray(); foreach (string h in hits) da.Add(h); f["dangers"] = da;

        f["summary"] = BuildSummary(f, command, tier);
    }

    // Lesbare Beschreibung (mit echten Umlauten) aus den strukturierten Metadaten.
    string BuildSummary(JObject f, string command, RiskTier tier)
    {
        var sb = new StringBuilder();
        string intent = f["intent"] != null ? (string)f["intent"] : "";
        if (intent.Length > 0) sb.Append("Angegebene Absicht (vom Aufrufer): ").Append(intent).Append("\n");
        sb.Append("Tool: ").Append((string)f["tool"]).Append("  |  Kommando: ").Append(command).Append("  |  Risiko: ").Append(tier.ToString()).Append("\n");
        if (f["clientGuid"] != null)
        {
            string g = (string)f["clientGuid"]; string nm = f["clientName"] != null ? (string)f["clientName"] : null;
            sb.Append("Zielclient: ").Append(nm != null ? nm + " (" + g + ")" : g).Append("\n");
        }
        if (f["clientUser"] != null) sb.Append("Letzter angemeldeter Benutzer: ").Append((string)f["clientUser"]).Append("\n");
        if (f["commandGuid"] != null)
        {
            string g = (string)f["commandGuid"]; string nm = f["commandName"] != null ? (string)f["commandName"] : null;
            sb.Append("Client Command (Aktion): ").Append(nm != null ? nm + " (" + g + ")" : g).Append("\n");
        }
        JArray vs = f["vars"] as JArray;
        if (vs != null) foreach (JObject v in vs) sb.Append("Variable '").Append((string)v["name"]).Append("' = ").Append(DisplayVar((string)v["value"])).Append("\n");
        if (f["ignoreMaintenance"] != null && (bool)f["ignoreMaintenance"]) sb.Append("Wartungsfenster wird ignoriert (sofortige Ausführung).\n");
        JArray da = f["dangers"] as JArray;
        if (da != null && da.Count > 0) { var l = new List<string>(); foreach (JToken t in da) l.Add((string)t); sb.Append("WARNUNG – potenziell destruktive Muster erkannt: ").Append(string.Join(", ", l.ToArray())).Append("\n"); }
        sb.Append("WICHTIG: Prüfe immer den Roh-Befehl, nicht nur diese Beschreibung.");
        return sb.ToString();
    }

    // Base64-Wrapper (WrapScriptOneLine im Server) nur fuer die ANZEIGE entschluesseln, damit Freigeber
    // das echte Skript lesen. Passt der Wert nicht exakt auf das Wrapper-Muster, wird er unveraendert gezeigt.
    static readonly System.Text.RegularExpressions.Regex WrapRx = new System.Text.RegularExpressions.Regex(
        @"^\$s=\[Text\.Encoding\]::UTF8\.GetString\(\[Convert\]::FromBase64String\('([A-Za-z0-9+/=]+)'\)\);Invoke-Expression \$s$");
    static string DisplayVar(string val)
    {
        if (string.IsNullOrEmpty(val)) return val;
        var m = WrapRx.Match(val.Trim());
        if (!m.Success) return val;
        try
        {
            string plain = Encoding.UTF8.GetString(Convert.FromBase64String(m.Groups[1].Value));
            return "[Base64-verpackt übergeben, hier entschlüsselt – so läuft es auf dem Client:]\n" + plain;
        }
        catch { return val; }
    }

    // Schritt-für-Schritt, was der Befehl konkret tut – aus den Metadaten (deterministisch, keine LLM-Deutung).
    List<string> BuildSteps(JObject f)
    {
        var steps = new List<string>();
        string tool = f["tool"] != null ? (string)f["tool"] : "";
        bool hasClient = f["clientGuid"] != null;
        bool hasCmd = f["commandGuid"] != null;

        if (hasClient)
        {
            string c = f["clientName"] != null ? (string)f["clientName"] : (string)f["clientGuid"];
            string s = "Zielrechner: " + c;
            if (f["clientUser"] != null) s += " (zuletzt angemeldet: " + (string)f["clientUser"] + ")";
            steps.Add(s);
        }
        if (hasCmd)
        {
            string a = f["commandName"] != null ? (string)f["commandName"] : (string)f["commandGuid"];
            steps.Add("Führt dort die Aktion „" + a + "“ aus.");
        }
        JArray vs = f["vars"] as JArray;
        if (vs != null)
            foreach (JObject v in vs)
            {
                string nm = (string)v["name"]; string val = DisplayVar(v["value"] != null ? (string)v["value"] : "");
                bool isScript = tool.IndexOf("powershell", StringComparison.OrdinalIgnoreCase) >= 0 || val.IndexOf('\n') >= 0;
                if (isScript)
                {
                    steps.Add("Übergibt der Aktion das Skript in Variable „" + nm + "“ – Zeile für Zeile:");
                    string[] lines = val.Replace("\r", "").Split('\n');
                    int shown = 0;
                    foreach (string ln in lines)
                    {
                        if (ln.Trim().Length == 0) continue;
                        if (shown++ >= 40) { steps.Add("    … (weitere Zeilen, siehe Roh-Befehl links)"); break; }
                        steps.Add("    Zeile " + shown + ": " + ln.Trim());
                    }
                }
                else steps.Add("Setzt Variable „" + nm + "“ = " + val);
            }
        JArray ids = f["objectIds"] as JArray;
        if (ids != null && ids.Count > 0)
        {
            var l = new List<string>(); foreach (JToken t in ids) l.Add((string)t);
            steps.Add("Betrifft folgende Objekte: " + string.Join(", ", l.ToArray()));
        }
        if (f["ignoreMaintenance"] != null && (bool)f["ignoreMaintenance"])
            steps.Add("Ignoriert das Wartungsfenster → läuft sofort, nicht erst im nächsten Wartungszeitraum.");
        if (!hasClient && !hasCmd && (ids == null || ids.Count == 0))
            steps.Add("Führt das Kommando „" + (string)f["command"] + "“ mit dem links gezeigten Inhalt aus.");
        string tier = f["tier"] != null ? (string)f["tier"] : "";
        steps.Add("Einstufung: " + tier + (tier == "Destructive" ? " – reale Ausführung/Änderung auf dem Zielsystem." : "."));
        JArray da = f["dangers"] as JArray;
        if (da != null && da.Count > 0)
        {
            var l = new List<string>(); foreach (JToken t in da) l.Add((string)t);
            steps.Add("⚠ Achtung: potenziell destruktive Muster erkannt (" + string.Join(", ", l.ToArray()) + ").");
        }
        return steps;
    }

    // ---------- XML hübsch + Schritt-Erklärung mit Zeilenbezug ----------
    // Parst das ICQL-/Inner-XML, baut farbig hervorgehobenes HTML mit Zeilennummern UND eine
    // kontextbezogene Schritt-Liste (gleiche Zeilennummern). Liefert false, wenn nicht parsebar.
    static bool BuildXmlView(string xml, out string html, out List<string[]> steps)
    {
        html = null; steps = null;
        if (string.IsNullOrEmpty(xml)) return false;
        string body = xml.Trim();
        if (body.StartsWith("<?")) { int i = body.IndexOf("?>"); if (i >= 0) body = body.Substring(i + 2).Trim(); }
        var doc = new System.Xml.XmlDocument(); doc.PreserveWhitespace = false;
        // xsi-Prefix deklarieren – die Inner-Fragmente nutzen xsi:type ohne eigene Namespace-Deklaration.
        try { doc.LoadXml("<__r xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" + body + "</__r>"); } catch { return false; }
        var lines = new List<string>();
        var infos = new List<JObject>();
        foreach (System.Xml.XmlNode child in doc.DocumentElement.ChildNodes)
            WalkXml(child, 0, lines, infos);
        if (lines.Count == 0) return false;
        var sb = new StringBuilder("<div class=\"xmlbox\">");
        for (int i = 0; i < lines.Count; i++)
            sb.Append("<div class=\"xln\"><span class=\"g\">").Append(i + 1).Append("</span><span class=\"c\">").Append(lines[i]).Append("</span></div>");
        sb.Append("</div>");
        html = sb.ToString();
        steps = BuildXmlSteps(infos);
        return true;
    }

    static int[] WalkXml(System.Xml.XmlNode n, int depth, List<string> lines, List<JObject> infos)
    {
        if (n.NodeType != System.Xml.XmlNodeType.Element) return null;
        string ind = new string(' ', depth * 2);
        var kids = new List<System.Xml.XmlNode>();
        foreach (System.Xml.XmlNode c in n.ChildNodes) if (c.NodeType == System.Xml.XmlNodeType.Element) kids.Add(c);
        bool leaf = kids.Count == 0;
        var info = new JObject(); info["depth"] = depth; info["name"] = n.Name; info["leaf"] = leaf;
        if (leaf)
        {
            string text = ""; bool hasText = false;
            foreach (System.Xml.XmlNode c in n.ChildNodes)
                if (c.NodeType == System.Xml.XmlNodeType.Text || c.NodeType == System.Xml.XmlNodeType.CDATA) { hasText = true; text += c.Value; }
            bool empty = !hasText || text.Trim().Length == 0;
            info["empty"] = empty; info["value"] = text;
            string lh = empty ? ind + TagHtml(n, "/>")
                              : ind + TagHtml(n, ">") + "<span class=\"tx\">" + Esc(text) + "</span>" + CloseHtml(n);
            lines.Add(lh); int ln = lines.Count;
            info["start"] = ln; info["end"] = ln; infos.Add(info);
            return new[] { ln, ln };
        }
        lines.Add(ind + TagHtml(n, ">")); int start = lines.Count;
        info["empty"] = false; info["value"] = ""; info["start"] = start; info["end"] = start;
        infos.Add(info);
        foreach (System.Xml.XmlNode c in kids) WalkXml(c, depth + 1, lines, infos);
        lines.Add(ind + CloseHtml(n)); int end = lines.Count;
        info["end"] = end; info["childcount"] = kids.Count;
        return new[] { start, end };
    }

    // Öffnendes Tag (oder self-closing, wenn close=="/>"), inkl. Attribute, farbig.
    static string TagHtml(System.Xml.XmlNode n, string close)
    {
        var sb = new StringBuilder();
        sb.Append("<span class=\"pn\">&lt;</span><span class=\"tg\">").Append(Esc(n.Name)).Append("</span>");
        if (n.Attributes != null)
            foreach (System.Xml.XmlAttribute a in n.Attributes)
                sb.Append(" <span class=\"at\">").Append(Esc(a.Name)).Append("</span><span class=\"pn\">=</span><span class=\"av\">\"").Append(Esc(a.Value)).Append("\"</span>");
        sb.Append("<span class=\"pn\">").Append(close == "/>" ? "/&gt;" : "&gt;").Append("</span>");
        return sb.ToString();
    }
    static string CloseHtml(System.Xml.XmlNode n)
    {
        return "<span class=\"pn\">&lt;/</span><span class=\"tg\">" + Esc(n.Name) + "</span><span class=\"pn\">&gt;</span>";
    }

    static string Range(int a, int b) { return a == b ? a.ToString() : a + "–" + b; }

    // Aus den Knoten-Infos kontextbezogene Schritte bauen: Container/Datensatz/Feld, leere Felder gruppiert.
    static List<string[]> BuildXmlSteps(List<JObject> infos)
    {
        var steps = new List<string[]>();
        var emptyBuf = new List<JObject>();
        Action flush = delegate()
        {
            if (emptyBuf.Count == 0) return;
            int a = (int)emptyBuf[0]["start"]; int b = (int)emptyBuf[emptyBuf.Count - 1]["end"];
            int d = (int)emptyBuf[0]["depth"];
            var names = new List<string>(); foreach (JObject e in emptyBuf) names.Add(Friendly((string)e["name"]));
            steps.Add(new[] { d.ToString(), Range(a, b), "leer gelassen / nicht gesetzt: " + string.Join(", ", names.ToArray()) });
            emptyBuf.Clear();
        };
        foreach (JObject info in infos)
        {
            int depth = (int)info["depth"]; bool leaf = (bool)info["leaf"];
            bool empty = info["empty"] != null && (bool)info["empty"];
            int start = (int)info["start"], end = (int)info["end"]; string name = (string)info["name"];
            if (leaf && empty) { emptyBuf.Add(info); continue; }
            flush();
            string text;
            if (!leaf)
            {
                if (depth == 0) text = "▸ " + Friendly(name) + " – Sammelaktion, enthält die zu schreibenden Daten.";
                else if (depth == 1) text = "Datensatz „" + Friendly(name) + "“ – das wird angelegt/geändert.";
                else { int cc = info["childcount"] != null ? (int)info["childcount"] : 0; text = Friendly(name) + " (" + cc + " " + (cc == 1 ? "Eintrag" : "Einträge") + ")."; }
            }
            else
            {
                string val = info["value"] != null ? ((string)info["value"]).Trim() : "";
                if (val.Length > 80) val = val.Substring(0, 77) + "…";
                text = Friendly(name) + " = " + val;
            }
            steps.Add(new[] { depth.ToString(), Range(start, end), text });
        }
        flush();
        return steps;
    }

    // Klartext-Label für ACMP-Tags (Fallback: Tag-Name selbst).
    static string Friendly(string tag)
    {
        switch (tag)
        {
            case "ReturnAssets": return "Asset-Rückgabe";
            case "LendAssets": return "Asset-Ausleihe";
            case "Contacts": return "Kontakte";
            case "Companies": return "Firmen";
            case "Locations": return "Standorte";
            case "Departments": return "Abteilungen";
            case "Containers": return "Container";
            case "TReturnAsset_V1": return "Rückgabe eines Assets";
            case "TLendAsset_V1": return "Ausleihe eines Assets";
            case "TContact_V3": case "TContact_V2": return "Kontakt";
            case "TCompany_V1": return "Firma";
            case "TLocation_V1": return "Standort";
            case "TDepartment_V1": return "Abteilung";
            case "TAcmpContainer_V1": return "Container";
            case "Id": case "ID": return "ID (GUID)";
            case "AssetId": return "Asset (GUID)";
            case "RentalId": return "Ausleih-Vorgang (GUID)";
            case "LentToContactId": return "Ausgeliehen an (Kontakt-GUID)";
            case "ContactId": return "Kontakt (GUID)";
            case "LentDate": return "Ausleihdatum";
            case "EstimatedReturnDate": return "Voraussichtliche Rückgabe";
            case "LentReturnDate": return "Rückgabedatum";
            case "Name": return "Name";
            case "FamilyName": return "Nachname";
            case "FullName": return "Anzeigename";
            case "Email": return "E-Mail";
            case "Phone": return "Telefon";
            case "Phone2": return "Telefon 2"; case "Phone3": return "Telefon 3"; case "Phone4": return "Telefon 4";
            case "Fax": return "Fax";
            case "Position": return "Position";
            case "Titles": return "Anreden/Titel";
            case "CompanyId": return "Firma (GUID)";
            case "CompanyName": return "Firmenname";
            case "DepartmentId": return "Abteilung (GUID)";
            case "DepartmentName": return "Abteilungsname";
            case "LocationId": return "Standort (GUID)";
            case "LocationName": return "Standortname";
            case "CostCenterId": return "Kostenstelle (GUID)";
            case "CostCenterName": return "Kostenstellenname";
            case "Address": return "Adresse";
            case "Zipcode": case "PostalCode": return "PLZ";
            case "City": return "Ort";
            case "Country": return "Land";
            case "Street": return "Straße";
            case "Description": return "Beschreibung";
            case "CustomerId": return "Kundennummer";
            case "PreferredContactMethod": return "Bevorzugter Kontaktweg";
            case "HTMLComment": return "Kommentar";
            case "ContactSpecificValue": return "Zusatzwert";
            case "IsUser": return "Ist Benutzer";
            case "HasDeleteFlag": return "Löschmarkierung";
            case "ADObjectGuid": return "AD-Objekt (GUID)";
            case "IsManufacturer": return "Hersteller-Flag";
            case "IsVendor": return "Lieferant-Flag";
            case "IsServicePartner": return "Servicepartner-Flag";
            case "CompanyKinds": return "Firmenarten";
            case "ParentId": return "Übergeordnet (GUID)";
            case "Abbreviation": return "Kürzel";
            case "Path": return "Pfad"; case "FullPath": return "Voller Pfad";
            case "LocationKind": return "Standort-Typ";
            case "option": return "Eintrag";
            default: return tag != null ? tag : "";
        }
    }

    // ---------- Anfrage anlegen ----------
    public string EnqueueCall(string tool, string command, int version, string inner, string scanExtra, RiskTier tier, string intent, string requester)
    {
        JObject r = new JObject();
        r["mode"] = "call"; r["command"] = command; r["version"] = version; r["inner"] = inner; r["raw"] = null;
        return Enqueue(r, tool, command, inner, scanExtra, tier, intent, requester);
    }

    public string EnqueuePublish(string tool, string raw, RiskTier tier, string intent, string requester)
    {
        JObject r = new JObject();
        r["mode"] = "publish"; r["command"] = "(rawIcql)"; r["version"] = 0; r["inner"] = null; r["raw"] = raw;
        return Enqueue(r, tool, "(rawIcql)", raw, null, tier, intent, requester);
    }

    string Enqueue(JObject frozen, string tool, string command, string payloadText, string scanExtra, RiskTier tier, string intent, string requester)
    {
        string id = Guid.NewGuid().ToString();
        int needed = GetToolApprovalCount(tool, tier);
        frozen["id"] = id;
        frozen["tool"] = tool;
        frozen["tier"] = tier.ToString();
        frozen["intent"] = intent != null ? intent : "";
        PopulateMeta(frozen, payloadText, scanExtra, tool, command, tier, intent, requester);
        frozen["needed"] = needed;
        frozen["approvals"] = new JArray();
        frozen["status"] = "pending";
        frozen["createdUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        frozen["rejectedBy"] = null;
        frozen["result"] = null;
        frozen["resultIsError"] = false;
        frozen["hash"] = Hash(frozen);
        lock (_gate) { _reqs[id] = frozen; Persist(frozen); }
        Audit("CREATE id=" + id + " tool=" + tool + " cmd=" + command + " tier=" + tier + " needed=" + needed + " intent=" + (intent != null ? intent : ""));
        NotifyApprovers(frozen);
        return id;
    }

    // Schickt jedem Freigeber eine E-Mail mit SEINEM persoenlichen Freigabe-Link (im Hintergrund, blockiert nicht).
    void NotifyApprovers(JObject req)
    {
        if (!_notify) return;
        // Bei nur EINER noetigen Zustimmung nur den Antragsteller benachrichtigen (sofern als Freigeber konfiguriert),
        // bei zwei oder mehr (Vier-Augen) alle Freigeber. Antragsteller kommt aus der Anfrage (Identitaet via API-Key).
        int needed = (int)req["needed"];
        string reqName = req["requester"] != null ? (string)req["requester"] : null;
        bool onlyRequester = needed <= 1 && !string.IsNullOrEmpty(reqName) && _approvers.ContainsKey(reqName) && _emails.ContainsKey(reqName);
        ThreadPool.QueueUserWorkItem(delegate(object o)
        {
            string id = (string)req["id"];
            string subj = "[ACMP Freigabe noetig] " + (string)req["tier"] + " - " + (string)req["tool"];
            foreach (KeyValuePair<string, string> kv in _approvers)
            {
                string name = kv.Key; string token = kv.Value; string email;
                if (onlyRequester && !string.Equals(name, reqName, StringComparison.OrdinalIgnoreCase)) continue;
                if (!_emails.TryGetValue(name, out email) || string.IsNullOrEmpty(email)) continue;
                string link = _publicUrl + "/approval?id=" + Uri.EscapeDataString(id) + "&approver=" + Uri.EscapeDataString(name) + "&token=" + Uri.EscapeDataString(token);
                try { SendMail(email, subj, MailBody(req, name, link)); Audit("NOTIFY-SENT id=" + id + " to=" + email); }
                catch (Exception ex) { Audit("NOTIFY-FAIL id=" + id + " to=" + email + " " + ex.Message); }
            }
        });
    }

    string MailBody(JObject req, string name, string link)
    {
        var sb = new StringBuilder();
        sb.Append("Hallo ").Append(name).Append(",\r\n\r\n");
        sb.Append("über den ACMP-MCP-Server wurde ein freigabepflichtiger Befehl angefordert. Es wurde NICHTS ausgeführt.\r\n");
        sb.Append("Es müssen ").Append((int)req["needed"]).Append(" VERSCHIEDENE IT-Mitarbeiter zustimmen.\r\n\r\n");
        sb.Append((string)req["summary"]).Append("\r\n\r\n");
        sb.Append("Schritt für Schritt, was passiert:\r\n");
        List<string> steps = BuildSteps(req);
        int n = 1;
        foreach (string s in steps) sb.Append("  ").Append(n++).Append(". ").Append(s).Append("\r\n");
        sb.Append("\r\nPrüfen und freigeben/ablehnen (persönlicher Link, nur für dich):\r\n").Append(link).Append("\r\n\r\n");
        sb.Append("Hinweis: Prüfe immer den Roh-Befehl auf der Seite. Gib diesen Link nicht weiter.\r\n");
        return sb.ToString();
    }

    void SendMail(string to, string subject, string body)
    {
        using (MailMessage m = new MailMessage(_mailFrom, to, subject, body))
        using (SmtpClient c = new SmtpClient(_smtpHost, _smtpPort))
        {
            m.SubjectEncoding = Encoding.UTF8;
            m.BodyEncoding = Encoding.UTF8;
            c.EnableSsl = _smtpSsl;
            if (!string.IsNullOrEmpty(_smtpUser)) c.Credentials = new NetworkCredential(_smtpUser, _smtpPass);
            c.Send(m);
        }
    }

    static string Hash(JObject r)
    {
        string s = (string)r["mode"] + "|" + (string)r["command"] + "|" + (r["version"] != null ? r["version"].ToString() : "") + "|" + (r["inner"] != null ? (string)r["inner"] : "") + "|" + (r["raw"] != null ? (string)r["raw"] : "");
        using (SHA256 h = SHA256.Create())
        {
            byte[] b = h.ComputeHash(Encoding.UTF8.GetBytes(s));
            var sb = new StringBuilder();
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }
    }

    public string PendingMessage(string id)
    {
        JObject r; lock (_gate) { _reqs.TryGetValue(id, out r); }
        int needed = r != null ? (int)r["needed"] : 0;
        var sb = new StringBuilder();
        sb.Append("FREIGABE ERFORDERLICH (Vier-Augen-Prinzip). Es wurde NICHTS ausgefuehrt.\n");
        sb.Append("Anfrage-Id: ").Append(id).Append("\n");
        sb.Append("Benoetigte Zustimmungen: ").Append(needed).Append(" verschiedene IT-Mitarbeiter.\n");
        sb.Append("Freigabe-Seite (im Browser oeffnen, NICHT ueber das LLM bestaetigen): ").Append(_publicUrl).Append("/approval?id=").Append(id).Append("\n");
        sb.Append("Status spaeter pruefen mit Tool: acmp_approval_status { \"id\": \"").Append(id).Append("\" }");
        return sb.ToString();
    }

    public string StatusText(string id)
    {
        JObject r; lock (_gate) { _reqs.TryGetValue(id, out r); }
        if (r == null) return "Keine Anfrage mit Id " + id + " gefunden.";
        var sb = new StringBuilder();
        sb.Append("Anfrage ").Append(id).Append("\n");
        sb.Append("Status: ").Append((string)r["status"]).Append("\n");
        sb.Append("Tool: ").Append((string)r["tool"]).Append("  Kommando: ").Append((string)r["command"]).Append("  Risiko: ").Append((string)r["tier"]).Append("\n");
        JArray ap = r["approvals"] as JArray;
        sb.Append("Zustimmungen: ").Append(ap != null ? ap.Count : 0).Append(" / ").Append((int)r["needed"]);
        if (ap != null && ap.Count > 0) { var ns = new List<string>(); foreach (JObject a in ap) ns.Add((string)a["name"]); sb.Append(" (").Append(string.Join(", ", ns.ToArray())).Append(")"); }
        sb.Append("\n");
        if (r["rejectedBy"] != null && r["rejectedBy"].Type != JTokenType.Null) sb.Append("Abgelehnt von: ").Append((string)r["rejectedBy"]).Append("\n");
        string st = (string)r["status"];
        if (st == "done" || st == "failed")
        {
            sb.Append((bool)r["resultIsError"] ? "Ergebnis (FEHLER):\n" : "Ergebnis:\n");
            sb.Append(Truncate(r["result"] != null ? (string)r["result"] : "", 4000));
        }
        return sb.ToString();
    }

    // ---------- Freigabe / Ablehnung (out-of-band ueber die Web-Seite) ----------
    public bool ValidateApprover(string name, string token)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(token)) return false;
        string t; if (!_approvers.TryGetValue(name, out t)) return false;
        return t == token;
    }

    bool Approve(string id, string name, out string msg)
    {
        lock (_gate)
        {
            JObject r; if (!_reqs.TryGetValue(id, out r)) { msg = "Anfrage nicht gefunden."; return false; }
            if ((string)r["status"] != "pending") { msg = "Anfrage ist nicht mehr offen (Status: " + (string)r["status"] + ")."; return false; }
            JArray ap = r["approvals"] as JArray;
            foreach (JObject a in ap) if (string.Equals((string)a["name"], name, StringComparison.OrdinalIgnoreCase)) { msg = "Du hast bereits zugestimmt."; return false; }
            JObject e = new JObject(); e["name"] = name; e["utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            ap.Add(e);
            Audit("APPROVE id=" + id + " by=" + name + " count=" + ap.Count + "/" + (int)r["needed"]);
            if (ap.Count >= (int)r["needed"]) { r["status"] = "ready"; Audit("READY id=" + id); }
            Persist(r);
            msg = ap.Count >= (int)r["needed"] ? "Zustimmung erfasst. Genug Zustimmungen - wird ausgefuehrt." : "Zustimmung erfasst (" + ap.Count + "/" + (int)r["needed"] + "). Es fehlt noch mind. eine weitere Person.";
            return true;
        }
    }

    bool Reject(string id, string name, out string msg)
    {
        lock (_gate)
        {
            JObject r; if (!_reqs.TryGetValue(id, out r)) { msg = "Anfrage nicht gefunden."; return false; }
            if ((string)r["status"] != "pending") { msg = "Anfrage ist nicht mehr offen (Status: " + (string)r["status"] + ")."; return false; }
            r["status"] = "rejected"; r["rejectedBy"] = name; Persist(r);
            Audit("REJECT id=" + id + " by=" + name);
            msg = "Anfrage abgelehnt.";
            return true;
        }
    }

    // ---------- Worker: fuehrt freigegebene Anfragen aus ----------
    public void StartWorker()
    {
        Thread t = new Thread(new ThreadStart(WorkerLoop));
        t.IsBackground = true;
        t.Start();
    }

    void WorkerLoop()
    {
        while (true)
        {
            try { Tick(); } catch (Exception ex) { if (_log != null) _log("Approval-Worker-Fehler: " + ex.Message); }
            Thread.Sleep(3000);
        }
    }

    void Tick()
    {
        // 1) Abgelaufene offene Anfragen verfallen lassen.
        DateTime now = DateTime.UtcNow;
        lock (_gate)
        {
            foreach (JObject r in new List<JObject>(_reqs.Values))
            {
                if ((string)r["status"] != "pending") continue;
                DateTime created;
                if (DateTime.TryParse((string)r["createdUtc"], null, DateTimeStyles.RoundtripKind, out created))
                    if ((now - created).TotalMinutes > _expiryMinutes)
                    { r["status"] = "expired"; Persist(r); Audit("EXPIRE id=" + (string)r["id"]); }
            }
        }

        // 2) Eine freigegebene Anfrage ausfuehren (Payload eingefroren + Hash-geprueft).
        JObject job = null;
        lock (_gate)
        {
            foreach (JObject r in _reqs.Values)
                if ((string)r["status"] == "ready") { r["status"] = "executing"; Persist(r); job = r; break; }
        }
        if (job == null) return;

        string id = (string)job["id"];
        bool isErr = false; string result;
        try
        {
            if (Hash(job) != (string)job["hash"]) throw new Exception("Integritaetsfehler: Payload-Hash weicht ab - Ausfuehrung abgebrochen.");
            Audit("EXEC id=" + id + " cmd=" + (string)job["command"]);
            result = _executor(job);
            Match m = Regex.Match(result != null ? result : "", "<ResultCode>(\\d+)</ResultCode>");
            if (m.Success && m.Groups[1].Value != "0") isErr = true;
        }
        catch (Exception ex) { result = ex.Message; isErr = true; }

        lock (_gate)
        {
            job["result"] = result;
            job["resultIsError"] = isErr;
            job["status"] = isErr ? "failed" : "done";
            Persist(job);
        }
        Audit((isErr ? "FAILED" : "DONE") + " id=" + id);
    }

    public void LoadPending()
    {
        if (_dir == null || !Directory.Exists(_dir)) return;
        int loaded = 0;
        foreach (string f in Directory.GetFiles(_dir, "*.json"))
        {
            try
            {
                JObject r = JObject.Parse(File.ReadAllText(f));
                string id = (string)r["id"]; if (string.IsNullOrEmpty(id)) continue;
                // Halb ausgefuehrte beim Neustart nicht blind wiederholen -> als failed markieren.
                if ((string)r["status"] == "executing") { r["status"] = "failed"; r["result"] = "Server-Neustart waehrend Ausfuehrung - bitte manuell pruefen."; r["resultIsError"] = true; }
                _reqs[id] = r; loaded++;
            }
            catch { }
        }
        if (_log != null) _log("Freigabe-Anfragen geladen: " + loaded + " (offen: " + CountPending() + ")");
    }

    int CountPending() { int n = 0; foreach (JObject r in _reqs.Values) if ((string)r["status"] == "pending") n++; return n; }

    void Persist(JObject r)
    {
        try { File.WriteAllText(Path.Combine(_dir, (string)r["id"] + ".json"), r.ToString(Formatting.Indented)); }
        catch (Exception ex) { if (_log != null) _log("Persist-Fehler: " + ex.Message); }
    }

    readonly object _auditLock = new object();
    void Audit(string m)
    {
        string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + m;
        if (_log != null) _log("AUDIT " + m);
        if (_auditFile != null) { try { lock (_auditLock) { File.AppendAllText(_auditFile, line + Environment.NewLine); } } catch { } }
    }

    static string Truncate(string s, int n) { if (s == null) return ""; return s.Length <= n ? s : s.Substring(0, n) + "..."; }

    static string SafeResolve(Func<string, string> f, string arg)
    {
        if (f == null) return null;
        try { string r = f(arg); return string.IsNullOrEmpty(r) ? null : r; } catch { return null; }
    }

    static string Unesc(string s)
    {
        if (s == null) return "";
        return s.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&amp;", "&");
    }

    // ---------- Web-Freigabeseite (GET, eigene Token-Auth, NICHT der MCP-Bearer-Key) ----------
    public bool IsApprovalPath(string pathQuery)
    {
        string p = PathOnly(pathQuery);
        return p == "/dashboard" || p == "/approvals" || p == "/approval" || p == "/approve" || p == "/reject" || p == "/history" || p == "/settings" || p == "/changelog" || p == "/settings-toggle" || p == "/settings-bypass" || p == "/settings-approval" || p == "/onboarding" || p == "/onboarding-create" || p == "/setup" || p == "/setup-create";
    }

    static string PathOnly(string pathQuery)
    {
        if (pathQuery == null) return "/";
        int q = pathQuery.IndexOf('?');
        return q >= 0 ? pathQuery.Substring(0, q) : pathQuery;
    }

    static Dictionary<string, string> ParseQuery(string pathQuery)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (pathQuery == null) return d;
        int q = pathQuery.IndexOf('?');
        if (q < 0) return d;
        string[] parts = pathQuery.Substring(q + 1).Split('&');
        foreach (string p in parts)
        {
            if (p.Length == 0) continue;
            int eq = p.IndexOf('=');
            string k = eq >= 0 ? p.Substring(0, eq) : p;
            string v = eq >= 0 ? p.Substring(eq + 1) : "";
            try { d[Uri.UnescapeDataString(k)] = Uri.UnescapeDataString(v.Replace('+', ' ')); } catch { d[k] = v; }
        }
        return d;
    }

    public void HandleHttp(string pathQuery, out int code, out string ctype, out string body)
    {
        code = 200; ctype = "text/html; charset=utf-8";
        string path = PathOnly(pathQuery);
        Dictionary<string, string> q = ParseQuery(pathQuery);

        // Ersteinrichtung: eigener, ungesicherter Pfad (kein Freigeber existiert ja noch) - nur per Setup-Token erreichbar.
        if (path == "/setup" || path == "/setup-create") { body = HandleSetup(path, q); return; }

        string approver; q.TryGetValue("approver", out approver);
        string token; q.TryGetValue("token", out token);

        if (!ValidateApprover(approver, token)) { body = LoginPage(); return; }
        string qs = "approver=" + Uri.EscapeDataString(approver) + "&token=" + Uri.EscapeDataString(token);

        if (path == "/approve" || path == "/reject")
        {
            string id; q.TryGetValue("id", out id);
            string msg; bool ok;
            if (path == "/approve") ok = Approve(id, approver, out msg); else ok = Reject(id, approver, out msg);
            body = NoticePage((ok ? "OK" : "Hinweis"), msg, approver, qs);
            return;
        }
        if (path == "/approval")
        {
            string id; q.TryGetValue("id", out id);
            body = DetailPage(id, approver, qs);
            return;
        }
        if (path == "/dashboard") { body = DashboardPage(approver, qs); return; }
        if (path == "/approvals") { body = ListPage(approver, qs); return; }
        if (path == "/history") { body = HistoryPage(approver, qs); return; }
        if (path == "/changelog") { body = ChangelogPage(approver, qs); return; }
        if (path == "/onboarding") { body = OnboardingPage(approver, token, qs); return; }
        if (path == "/onboarding-create") { body = OnboardingCreate(q, approver, qs); return; }
        if (path == "/settings") { body = SettingsPage(approver, qs); return; }
        if (path == "/settings-toggle")
        {
            string stTool; q.TryGetValue("tool", out stTool);
            if (!string.IsNullOrEmpty(stTool) && NoApprovalTools != null)
            {
                lock (_gate) { if (NoApprovalTools.Contains(stTool)) NoApprovalTools.Remove(stTool); else NoApprovalTools.Add(stTool); }
                SaveConfig();
            }
            body = RedirectPage("/settings?" + qs); return;
        }
        if (path == "/settings-bypass")
        {
            string bpVal; q.TryGetValue("enabled", out bpVal);
            BypassAll = (bpVal == "1");
            SaveConfig();
            body = RedirectPage("/settings?" + qs); return;
        }
        if (path == "/settings-approval")
        {
            string spTool; q.TryGetValue("tool", out spTool);
            string spCountStr; q.TryGetValue("count", out spCountStr);
            int spCount;
            if (!string.IsNullOrEmpty(spTool) && int.TryParse(spCountStr, out spCount) && spCount >= 0 && spCount <= 2 && ToolApprovalCounts != null)
            {
                lock (_gate) { ToolApprovalCounts[spTool] = spCount; if (NoApprovalTools != null) { if (spCount == 0) NoApprovalTools.Add(spTool); else NoApprovalTools.Remove(spTool); } }
                SaveConfig();
            }
            body = RedirectPage("/settings?" + qs); return;
        }
        body = DashboardPage(approver, qs);
    }

    // Ordner der laufenden EXE (= Installationsordner) fuer Copy-Paste-Anleitungen.
    static string InstallDir() { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'); }

    static string Esc(string s)
    {
        if (s == null) return "";
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }

    // Vollstaendiges HTML-Geruest mit Kopfbereich (Brand oder Navigationsleiste) + Footer.
    string Render(string header, string inner)
    {
        return "<!doctype html><html lang=\"de\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
             + "<title>ACMP – Freigabe-Konsole</title>"
             + "<link rel=\"preconnect\" href=\"https://fonts.googleapis.com\"><link rel=\"preconnect\" href=\"https://fonts.gstatic.com\" crossorigin>"
             + "<link href=\"https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700;800&family=JetBrains+Mono:wght@400;600&display=swap\" rel=\"stylesheet\">"
             + "<style>"
             + "*{box-sizing:border-box}"
             + "body{font-family:'Inter',-apple-system,system-ui,'Segoe UI',Arial,sans-serif;color:#0a0a0a;margin:0;min-height:100vh;font-size:16px;line-height:1.65;-webkit-font-smoothing:antialiased;background:#ffffff;background-image:radial-gradient(1100px 480px at 85% -12%,rgba(0,69,154,.10),transparent 60%),radial-gradient(800px 420px at 8% 4%,rgba(0,102,204,.07),transparent 60%)}"
             + ".wrap{position:relative;z-index:1;max-width:1100px;margin:0 auto;padding:0 20px 80px}"
             + ".brand{font-size:12px;letter-spacing:.14em;color:#00459a;text-transform:uppercase;font-weight:600;margin:30px 0 16px}"
             + "h1{font-size:28px;font-weight:700;letter-spacing:-.01em;color:#0a0a0a;margin:0 0 10px}"
             + "h2{font-size:13px;font-weight:700;letter-spacing:.08em;text-transform:uppercase;color:#00459a;margin:26px 0 10px}"
             + "p{line-height:1.65;color:#1c2430}b{color:#0a0a0a;font-weight:600}small{color:#7a838c}"
             + "a{color:#00459a;text-decoration:none;font-weight:500}a:hover{text-decoration:underline}"
             // --- Navigationsleiste ---
             + ".nav{position:sticky;top:0;z-index:20;display:flex;align-items:center;gap:4px;background:rgba(255,255,255,.82);backdrop-filter:blur(12px);-webkit-backdrop-filter:blur(12px);border-bottom:1px solid rgba(10,15,35,.1);padding:0 20px;margin:0 -20px 26px;height:62px}"
             + ".nav .logo{font-weight:800;color:#00459a;letter-spacing:-.01em;font-size:16px;margin-right:18px;white-space:nowrap}.nav .logo span{color:#0a0a0a}"
             + ".navlink{padding:8px 13px;border-radius:9px;color:#545e66;font-weight:600;font-size:14px;display:inline-flex;align-items:center;gap:7px;white-space:nowrap}"
             + ".navlink:hover{background:#eef2f7;text-decoration:none;color:#0a0a0a}.navlink.active{background:#00459a;color:#fff}"
             + ".nav .spacer{flex:1}.nav .who{font-size:13px;color:#7a838c;margin-right:4px}.nav .who b{color:#0a0a0a}"
             + ".badge{display:inline-flex;min-width:20px;height:20px;padding:0 6px;align-items:center;justify-content:center;border-radius:10px;background:#dc2626;color:#fff;font-size:12px;font-weight:700}.navlink.active .badge{background:#fff;color:#00459a}"
             // --- Statistik-Karten ---
             + ".cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:16px;margin:18px 0 10px}"
             + ".card{background:#fff;border:1px solid rgba(10,15,35,.1);border-radius:14px;padding:18px 20px;box-shadow:0 10px 30px rgba(10,15,35,.05);text-decoration:none;display:block;transition:transform .15s,box-shadow .15s}"
             + "a.card:hover{transform:translateY(-2px);box-shadow:0 14px 36px rgba(10,15,35,.1);text-decoration:none}"
             + ".card .k{font-size:12px;letter-spacing:.06em;text-transform:uppercase;color:#7a838c;font-weight:700;margin:0 0 8px}"
             + ".card .v{font-size:30px;font-weight:800;color:#0a0a0a;letter-spacing:-.02em;line-height:1.05}"
             + ".card.compact .v{font-size:20px;letter-spacing:-.01em;white-space:nowrap}"
             + ".card .s{font-size:13px;color:#7a838c;margin-top:5px}.card.alert .v{color:#dc2626}.card.ok .v{color:#15803d}.card.warnc .v{color:#b45309}"
             + "table{border-collapse:separate;border-spacing:0;width:100%;background:#fff;border:1px solid rgba(10,15,35,.10);border-radius:12px;overflow:hidden;box-shadow:0 10px 30px rgba(10,15,35,.05)}"
             + "td,th{border-bottom:1px solid rgba(10,15,35,.08);padding:11px 14px;text-align:left;font-size:14px;vertical-align:top}"
             + "tr:last-child td{border-bottom:none}"
             + "th{background:#f6f8fb;color:#545e66;font-weight:700;letter-spacing:.04em;text-transform:uppercase;font-size:12px}"
             + "tr:hover td{background:#f6f8fb}"
             + "pre{background:#f6f8fb;border:1px solid rgba(10,15,35,.10);border-left:3px solid #00459a;padding:13px 14px;border-radius:8px;overflow:auto;font-size:13px;color:#1c2430;white-space:pre-wrap;word-break:break-word;font-family:'JetBrains Mono',Consolas,'Courier New',monospace;line-height:1.55}"
             + "pre.script{border:1px solid rgba(245,158,11,.45);border-left:4px solid #f59e0b;background:#fff8ec;color:#3a2c10;font-size:13.5px}"
             // --- XML-Highlighting (Roh-Befehl) ---
             + ".cmdhdr{font-size:13px;color:#545e66;margin:0 0 8px}.cmdhdr .vtag{font-family:'JetBrains Mono',Consolas,monospace;font-size:12px;background:#eaf1fb;color:#00459a;border-radius:5px;padding:1px 6px;font-weight:700}"
             + ".xmlbox{background:#f8fafc;border:1px solid rgba(10,15,35,.1);border-left:3px solid #00459a;border-radius:8px;overflow:auto;font-family:'JetBrains Mono',Consolas,'Courier New',monospace;font-size:12.5px;line-height:1.5;padding:8px 0}"
             + ".xln{display:flex;white-space:pre}.xln:hover{background:rgba(0,69,154,.06)}"
             + ".xln .g{flex:0 0 auto;width:34px;text-align:right;padding:0 10px 0 8px;color:#b6bdc6;-webkit-user-select:none;user-select:none}"
             + ".xln .c{flex:1;padding-right:14px;white-space:pre;color:#1c2430}"
             + ".tg{color:#0b62c4}.at{color:#7c3aed}.av{color:#15803d}.tx{color:#0a0a0a}.pn{color:#9aa3ab}"
             // --- Schritt-für-Schritt mit Zeilenbezug ---
             + ".expl{margin:4px 0}"
             + ".xstep{display:flex;gap:10px;align-items:flex-start;margin:5px 0;font-size:14px;line-height:1.5;color:#1c2430}"
             + ".xstep .lc{flex:0 0 auto;min-width:52px;text-align:right;font-family:'JetBrains Mono',Consolas,monospace;font-size:11.5px;color:#00459a;font-weight:700;background:#eaf1fb;border-radius:6px;padding:2px 7px}"
             + ".runbox{background:#f6f8fb;border:1px solid rgba(10,15,35,.10);border-radius:10px;padding:14px 16px;margin:14px 0}"
             + ".runbox .lbl{font-size:12px;letter-spacing:.06em;text-transform:uppercase;color:#545e66;font-weight:600;margin:0 0 8px}"
             + ".tier-Destructive{color:#c81e1e;font-weight:700}.tier-Write{color:#b45309;font-weight:700}.tier-Read{color:#15803d;font-weight:700}"
             + ".warn{color:#c81e1e;font-weight:500}"
             + ".pill{display:inline-block;padding:2px 10px;border-radius:999px;font-size:12px;font-weight:700}.pill-on{background:#fde8e8;color:#c81e1e}.pill-off{background:#e7f6ec;color:#15803d}"
             // --- Patchverlauf / Timeline ---
             + ".timeline{position:relative;margin:20px 0 8px;padding-left:6px}"
             + ".tl-item{position:relative;padding:0 0 24px 30px;border-left:2px solid rgba(0,69,154,.18)}.tl-item:last-child{border-left-color:transparent;padding-bottom:4px}"
             + ".tl-dot{position:absolute;left:-7px;top:2px;width:12px;height:12px;border-radius:50%;background:#00459a;box-shadow:0 0 0 3px #fff}"
             + ".tl-head{display:flex;align-items:center;gap:10px;flex-wrap:wrap}"
             + ".tl-date{font-family:'JetBrains Mono',Consolas,monospace;font-size:12.5px;color:#7a838c;font-weight:600}"
             + ".tl-title{font-weight:700;font-size:15.5px;color:#0a0a0a}"
             + ".tag{display:inline-block;padding:1px 9px;border-radius:999px;font-size:11px;font-weight:700;background:#eaf1fb;color:#00459a;text-transform:uppercase;letter-spacing:.04em}.tag.fix{background:#fff1e6;color:#b45309}.tag.ui{background:#ecfdf3;color:#15803d}"
             + ".tl-items{margin:9px 0;padding-left:18px}.tl-items li{font-size:14px;color:#1c2430;margin:4px 0;line-height:1.55}"
             + ".chips{display:flex;flex-wrap:wrap;gap:6px;margin-top:8px}"
             + ".chip{font-family:'JetBrains Mono',Consolas,monospace;font-size:12px;background:#f6f8fb;border:1px solid rgba(10,15,35,.12);color:#1c2430;padding:2px 8px;border-radius:7px}"
             + ".cols{display:flex;gap:20px;flex-wrap:wrap;align-items:flex-start}.col{flex:1;min-width:320px}"
             + "ol.steps{padding-left:20px}ol.steps li{margin:7px 0;font-size:14px;line-height:1.6;color:#1c2430}"
             + "input{background:#fff;border:1px solid rgba(10,15,35,.22);color:#0a0a0a;padding:10px 12px;border-radius:8px;font-family:inherit;font-size:15px;width:320px;max-width:100%}input:focus{outline:none;border-color:#00459a;box-shadow:0 0 0 3px rgba(0,69,154,.15)}"
             + ".actions{display:flex;gap:14px;flex-wrap:wrap;margin:26px 0;align-items:center}"
             + ".btn{display:inline-flex;align-items:center;justify-content:center;gap:8px;padding:12px 24px;border:1px solid transparent;cursor:pointer;border-radius:10px;text-decoration:none;font-family:inherit;font-weight:600;font-size:.95rem;letter-spacing:normal;text-transform:none;color:#fff;transition:transform .15s,box-shadow .15s,background .15s}"
             + ".btn-approve{background:#16a34a;box-shadow:0 4px 16px rgba(22,163,74,.28)}"
             + ".btn-approve:hover{background:#15803d;transform:translateY(-1px);box-shadow:0 8px 24px rgba(22,163,74,.35);text-decoration:none}"
             + ".btn-reject{background:#dc2626;box-shadow:0 4px 16px rgba(220,38,38,.26)}"
             + ".btn-reject:hover{background:#b91c1c;transform:translateY(-1px);box-shadow:0 8px 24px rgba(220,38,38,.34);text-decoration:none}"
             + ".btn-ghost{background:#f6f8fb;color:#0a0a0a;border:1px solid rgba(10,15,35,.15);box-shadow:none}.btn-ghost:hover{background:#eef2f7;text-decoration:none}"
             + ".foot{margin-top:54px;padding-top:18px;border-top:1px solid rgba(10,15,35,.08);color:#9aa3ab;font-size:12.5px;display:flex;gap:14px;flex-wrap:wrap;align-items:center}.foot b{color:#7a838c}"
             + "@media(max-width:680px){.nav .who{display:none}.navlink{padding:8px 10px}}"
             + "</style></head><body><div class=\"wrap\">" + header + inner + Footer() + "</div></body></html>";
    }

    // Seite ohne Navigation (Login, einfache Hinweise) – nur Brand-Zeile.
    string Page(string inner) { return Render("<div class=\"brand\">ACMP &middot; Freigabe-Konsole</div>", inner); }

    // Seite mit Navigationsleiste. 'active' = Schluessel der aktiven Seite (dashboard|approvals|history|settings).
    string PageNav(string inner, string approver, string qs, string active) { return Render(NavBar(approver, qs, active), inner); }

    string NavBar(string approver, string qs, string active)
    {
        int open = OpenCount();
        string badge = open > 0 ? "<span class=\"badge\">" + open + "</span>" : "";
        var sb = new StringBuilder();
        sb.Append("<div class=\"nav\">");
        sb.Append("<a class=\"logo\" href=\"/dashboard?").Append(qs).Append("\" style=\"text-decoration:none\">ACMP <span>Freigabe</span></a>");
        sb.Append(NavLink("/dashboard", "Übersicht", "dashboard", active, qs, ""));
        sb.Append(NavLink("/approvals", "Freigaben", "approvals", active, qs, badge));
        sb.Append(NavLink("/history", "Verlauf", "history", active, qs, ""));
        sb.Append(NavLink("/settings", "Einstellungen", "settings", active, qs, ""));
        sb.Append(NavLink("/onboarding", "Onboarding", "onboarding", active, qs, ""));
        sb.Append(NavLink("/changelog", "Patchverlauf", "changelog", active, qs, ""));
        sb.Append("<div class=\"spacer\"></div>");
        sb.Append("<div class=\"who\">Angemeldet: <b>").Append(Esc(approver)).Append("</b></div>");
        sb.Append("<a class=\"navlink\" href=\"/approvals\" title=\"Abmelden\">Abmelden &rsaquo;</a>");
        sb.Append("</div>");
        return sb.ToString();
    }

    string NavLink(string href, string label, string key, string active, string qs, string extra)
    {
        string cls = (key == active) ? "navlink active" : "navlink";
        return "<a class=\"" + cls + "\" href=\"" + href + "?" + qs + "\">" + Esc(label) + extra + "</a>";
    }

    string Footer()
    {
        return "<div class=\"foot\"><b>ACMP MCP</b> &middot; Freigabe-Konsole &middot; Vier-Augen-Prinzip"
             + "<span style=\"flex:1\"></span>"
             + "<span>Freigaben erfolgen out-of-band über diese Seite – nicht über das LLM.</span></div>";
    }

    int OpenCount()
    {
        int n = 0;
        lock (_gate) { foreach (JObject r in _reqs.Values) if ((string)r["status"] == "pending") n++; }
        return n;
    }

    string LoginPage()
    {
        string hint = _setupToken != null
            ? "<div class=\"callout ok\" style=\"margin:0 0 16px;\">Noch kein Freigeber konfiguriert — den Ersteinrichtungs-Link findest du in der Server-Konsole/<code>server.log</code> (Zeile <code>ERSTEINRICHTUNG NOETIG</code>) beim Start des Servers.</div>"
            : "";
        string form = "<h1>ACMP Freigabe-Konsole</h1>" + hint + "<p>Bitte mit Freigeber-Name und Token anmelden.</p>"
            + "<form method=\"get\" action=\"/dashboard\">"
            + "<p>Name:<br><input name=\"approver\"></p>"
            + "<p>Token:<br><input name=\"token\" type=\"password\"></p>"
            + "<p class=\"actions\"><button class=\"btn btn-approve\" type=\"submit\">Anmelden</button></p></form>";
        return Page(form);
    }

    string NoticePage(string title, string msg, string approver, string qs)
    {
        return PageNav("<h1>" + Esc(title) + "</h1><p>" + Esc(msg) + "</p><p><a href=\"/approvals?" + qs + "\">&larr; Zurück zu den Freigaben</a> &nbsp;|&nbsp; <a href=\"/dashboard?" + qs + "\">Übersicht</a></p>", approver, qs, "approvals");
    }

    string Card(string href, string qs, string cls, string k, string v, string s)
    {
        string c = string.IsNullOrEmpty(cls) ? "card" : "card " + cls;
        string head = href != null ? "<a class=\"" + c + "\" href=\"" + href + "?" + qs + "\">" : "<div class=\"" + c + "\">";
        string tail = href != null ? "</a>" : "</div>";
        return head + "<div class=\"k\">" + Esc(k) + "</div><div class=\"v\">" + Esc(v) + "</div><div class=\"s\">" + Esc(s) + "</div>" + tail;
    }

    // Liest changelog.json aus dem Server-Ordner (datengetrieben, kein Recompile noetig). Neueste zuerst erwartet.
    JArray LoadChangelog()
    {
        try
        {
            if (CfgPath == null) return new JArray();
            string dir = Path.GetDirectoryName(Path.GetFullPath(CfgPath));
            string p = Path.Combine(dir, "changelog.json");
            if (!File.Exists(p)) return new JArray();
            return JArray.Parse(File.ReadAllText(p, Encoding.UTF8));
        }
        catch { return new JArray(); }
    }

    string ChangelogPage(string approver, string qs)
    {
        JArray cl = LoadChangelog();
        var sb = new StringBuilder();
        sb.Append("<h1>Patchverlauf</h1><p>Änderungen am ACMP-MCP-Server – neue Tools, Features und Fixes. Gepflegt in <code>changelog.json</code>.</p>");
        if (cl.Count == 0)
        {
            sb.Append("<p>Noch keine Einträge. Lege <code>changelog.json</code> im Server-Ordner an (Liste von Objekten mit <code>date</code>, <code>title</code>, optional <code>tags</code>, <code>items</code>, <code>tools</code>).</p>");
            return PageNav(sb.ToString(), approver, qs, "changelog");
        }
        sb.Append("<div class=\"timeline\">");
        foreach (JToken tok in cl)
        {
            JObject e = tok as JObject; if (e == null) continue;
            string date = e["date"] != null ? (string)e["date"] : "";
            string title = e["title"] != null ? (string)e["title"] : "(ohne Titel)";
            sb.Append("<div class=\"tl-item\"><div class=\"tl-dot\"></div><div class=\"tl-body\">");
            sb.Append("<div class=\"tl-head\"><span class=\"tl-date\">").Append(Esc(date)).Append("</span><span class=\"tl-title\">").Append(Esc(title)).Append("</span>");
            JArray tags = e["tags"] as JArray;
            if (tags != null) foreach (JToken tg in tags)
            {
                string tn = (string)tg; string tcls = tn != null && tn.Equals("Fix", StringComparison.OrdinalIgnoreCase) ? "tag fix" : (tn != null && tn.Equals("UI", StringComparison.OrdinalIgnoreCase) ? "tag ui" : "tag");
                sb.Append("<span class=\"").Append(tcls).Append("\">").Append(Esc(tn)).Append("</span>");
            }
            sb.Append("</div>");
            JArray items = e["items"] as JArray;
            if (items != null && items.Count > 0)
            {
                sb.Append("<ul class=\"tl-items\">");
                foreach (JToken it in items) sb.Append("<li>").Append(Esc((string)it)).Append("</li>");
                sb.Append("</ul>");
            }
            JArray tools = e["tools"] as JArray;
            if (tools != null && tools.Count > 0)
            {
                sb.Append("<div class=\"chips\">");
                foreach (JToken tl in tools) sb.Append("<span class=\"chip\">").Append(Esc((string)tl)).Append("</span>");
                sb.Append("</div>");
            }
            sb.Append("</div></div>");
        }
        sb.Append("</div>");
        return PageNav(sb.ToString(), approver, qs, "changelog");
    }

    string DashboardPage(string approver, string qs)
    {
        int open = 0, doneTotal = 0, doneToday = 0, rejected = 0;
        string today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var recent = new List<JObject>();
        lock (_gate)
        {
            foreach (JObject r in _reqs.Values)
            {
                string st = (string)r["status"];
                if (st == "pending") open++;
                else if (st == "rejected") rejected++;
                else if (st == "done")
                {
                    doneTotal++;
                    string cu = r["createdUtc"] != null ? (string)r["createdUtc"] : "";
                    if (cu.StartsWith(today)) doneToday++;
                }
                recent.Add(r);
            }
        }
        recent.Sort(delegate(JObject a, JObject b) { return string.CompareOrdinal((string)b["createdUtc"], (string)a["createdUtc"]); });
        int toolCount = GetAllTools != null ? GetAllTools().Count : 0;
        int approverCount = _approvers != null ? _approvers.Count : 0;

        var sb = new StringBuilder();
        sb.Append("<h1>Übersicht</h1><p>Willkommen, <b>").Append(Esc(approver)).Append("</b>. Status der ACMP-MCP-Freigabe-Konsole auf einen Blick.</p>");

        sb.Append("<div class=\"cards\">");
        sb.Append(Card("/approvals", qs, open > 0 ? "alert" : "ok", "Offene Freigaben", open.ToString(), open > 0 ? "warten auf Zustimmung" : "alles erledigt"));
        sb.Append(Card("/history", qs, "ok", "Ausgeführt", doneTotal.ToString(), doneToday + " heute"));
        sb.Append(Card("/history", qs, rejected > 0 ? "warnc" : null, "Abgelehnt", rejected.ToString(), "gesamt"));
        sb.Append(Card("/settings", qs, null, "Aktive Tools", toolCount.ToString(), "im MCP registriert"));
        sb.Append(Card("/settings", qs, null, "Freigeber", approverCount.ToString(), "berechtigt"));
        sb.Append(Card("/settings", qs, BypassAll ? "alert" : "ok", "Vier-Augen-Gate", BypassAll ? "Bypass AN" : "Aktiv", BypassAll ? "Freigaben übersprungen!" : "Schreib-Tools gesichert"));
        JArray cl = LoadChangelog();
        if (cl.Count > 0)
        {
            JObject last = cl[0] as JObject;
            string lDate = last != null && last["date"] != null ? (string)last["date"] : "—";
            string lTitle = last != null && last["title"] != null ? (string)last["title"] : "";
            sb.Append(Card("/changelog", qs, "compact", "Letztes Update", lDate, lTitle));
        }
        sb.Append("</div>");

        sb.Append("<h2>Schnellzugriff</h2><p class=\"actions\">");
        sb.Append("<a class=\"btn btn-approve\" href=\"/approvals?").Append(qs).Append("\">Offene Freigaben prüfen").Append(open > 0 ? " (" + open + ")" : "").Append("</a>");
        sb.Append("<a class=\"btn btn-ghost\" href=\"/history?").Append(qs).Append("\">Verlauf</a>");
        sb.Append("<a class=\"btn btn-ghost\" href=\"/settings?").Append(qs).Append("\">Einstellungen</a></p>");

        sb.Append("<h2>Letzte Aktivität</h2>");
        sb.Append("<table><tr><th>Erstellt (UTC)</th><th>Tool / Kommando</th><th>Risiko</th><th>Status</th><th></th></tr>");
        if (recent.Count == 0) sb.Append("<tr><td colspan=\"5\">Noch keine Anfragen.</td></tr>");
        int shown = 0;
        foreach (JObject r in recent)
        {
            if (shown++ >= 6) break;
            sb.Append("<tr><td>").Append(Esc((string)r["createdUtc"])).Append("</td>")
              .Append("<td>").Append(Esc((string)r["tool"])).Append("<br><small>").Append(Esc((string)r["command"])).Append("</small></td>")
              .Append("<td class=\"tier-").Append(Esc((string)r["tier"])).Append("\">").Append(Esc((string)r["tier"])).Append("</td>")
              .Append("<td>").Append(Esc(StatusLabel((string)r["status"]))).Append("</td>")
              .Append("<td><a href=\"/approval?id=").Append(Esc((string)r["id"])).Append("&").Append(qs).Append("\">Details &rarr;</a></td></tr>");
        }
        sb.Append("</table>");
        if (recent.Count > 6) sb.Append("<p><a href=\"/history?").Append(qs).Append("\">Gesamten Verlauf ansehen &rarr;</a></p>");

        return PageNav(sb.ToString(), approver, qs, "dashboard");
    }

    string ListPage(string approver, string qs)
    {
        var sb = new StringBuilder();
        sb.Append("<h1>Offene Freigaben</h1><p>Anfragen, die auf deine Zustimmung warten. Freigaben erfolgen out-of-band über diese Seite, nicht über das LLM.</p>");
        sb.Append("<table><tr><th>Erstellt (UTC)</th><th>Tool / Kommando</th><th>Risiko</th><th>Zust.</th><th></th></tr>");
        var rows = new List<JObject>();
        lock (_gate) { foreach (JObject r in _reqs.Values) if ((string)r["status"] == "pending") rows.Add(r); }
        rows.Sort(delegate(JObject a, JObject b) { return string.CompareOrdinal((string)a["createdUtc"], (string)b["createdUtc"]); });
        if (rows.Count == 0) sb.Append("<tr><td colspan=\"5\">Keine offenen Anfragen.</td></tr>");
        foreach (JObject r in rows)
        {
            JArray ap = r["approvals"] as JArray;
            sb.Append("<tr><td>").Append(Esc((string)r["createdUtc"])).Append("</td><td>").Append(Esc((string)r["tool"])).Append("<br><small>").Append(Esc((string)r["command"])).Append("</small></td>")
              .Append("<td class=\"tier-").Append(Esc((string)r["tier"])).Append("\">").Append(Esc((string)r["tier"])).Append("</td>")
              .Append("<td>").Append(ap != null ? ap.Count : 0).Append("/").Append((int)r["needed"]).Append("</td>")
              .Append("<td><a href=\"/approval?id=").Append(Esc((string)r["id"])).Append("&").Append(qs).Append("\">Prüfen &rarr;</a></td></tr>");
        }
        sb.Append("</table>");
        return PageNav(sb.ToString(), approver, qs, "approvals");
    }

    static string StatusLabel(string st)
    {
        if (st == "pending") return "offen";
        if (st == "ready") return "freigegeben (wartet)";
        if (st == "executing") return "läuft";
        if (st == "done") return "ausgeführt";
        if (st == "failed") return "fehlgeschlagen";
        if (st == "rejected") return "abgelehnt";
        if (st == "expired") return "abgelaufen";
        return st;
    }

    string HistoryPage(string approver, string qs)
    {
        var sb = new StringBuilder();
        sb.Append("<h1>Verlauf der Freigaben</h1><p>Alle bisherigen Anfragen – ausgeführt, abgelehnt, abgelaufen.</p>");
        sb.Append("<table><tr><th>Erstellt (UTC)</th><th>Tool / Kommando</th><th>Zielrechner</th><th>Risiko</th><th>Status</th><th>Beteiligte</th><th>Ergebnis</th><th></th></tr>");
        var rows = new List<JObject>();
        lock (_gate) { foreach (JObject r in _reqs.Values) rows.Add(r); }
        rows.Sort(delegate(JObject a, JObject b) { return string.CompareOrdinal((string)b["createdUtc"], (string)a["createdUtc"]); });
        if (rows.Count == 0) sb.Append("<tr><td colspan=\"8\">Noch keine Anfragen.</td></tr>");
        int shown = 0;
        foreach (JObject r in rows)
        {
            if (shown++ >= 300) break;
            string st = (string)r["status"];
            string client = r["clientName"] != null ? (string)r["clientName"] : (r["clientGuid"] != null ? (string)r["clientGuid"] : "-");
            JArray ap = r["approvals"] as JArray;
            var who = new List<string>(); if (ap != null) foreach (JObject a in ap) who.Add((string)a["name"]);
            string beteiligte;
            if (st == "rejected" && r["rejectedBy"] != null && r["rejectedBy"].Type != JTokenType.Null) beteiligte = "abgelehnt von " + (string)r["rejectedBy"];
            else beteiligte = (ap != null ? ap.Count : 0) + "/" + (int)r["needed"] + (who.Count > 0 ? " (" + string.Join(", ", who.ToArray()) + ")" : "");
            string ergebnis = "";
            if (st == "done") ergebnis = "OK";
            else if (st == "failed") ergebnis = "Fehler";
            sb.Append("<tr><td>").Append(Esc((string)r["createdUtc"])).Append("</td>")
              .Append("<td>").Append(Esc((string)r["tool"])).Append("<br><small>").Append(Esc((string)r["command"])).Append("</small></td>")
              .Append("<td>").Append(Esc(client)).Append("</td>")
              .Append("<td class=\"tier-").Append(Esc((string)r["tier"])).Append("\">").Append(Esc((string)r["tier"])).Append("</td>")
              .Append("<td>").Append(Esc(StatusLabel(st))).Append("</td>")
              .Append("<td><small>").Append(Esc(beteiligte)).Append("</small></td>")
              .Append("<td>").Append(Esc(ergebnis)).Append("</td>")
              .Append("<td><a href=\"/approval?id=").Append(Esc((string)r["id"])).Append("&").Append(qs).Append("\">Details &rarr;</a></td></tr>");
        }
        sb.Append("</table>");
        if (rows.Count > 300) sb.Append("<p><small>Es werden die 300 neuesten Anfragen angezeigt (gesamt: ").Append(rows.Count).Append(").</small></p>");
        return PageNav(sb.ToString(), approver, qs, "history");
    }

    string DetailPage(string id, string approver, string qs)
    {
        JObject r; lock (_gate) { _reqs.TryGetValue(id, out r); }
        if (r == null) return PageNav("<h1>Nicht gefunden</h1><p>Diese Anfrage existiert nicht (mehr).</p><p><a href=\"/approvals?" + qs + "\">&larr; Zurück zu den Freigaben</a></p>", approver, qs, "approvals");
        var sb = new StringBuilder();
        sb.Append("<h1>Freigabe prüfen</h1>");
        sb.Append("<p>Risiko: <span class=\"tier-").Append(Esc((string)r["tier"])).Append("\">").Append(Esc((string)r["tier"])).Append("</span> &nbsp; Status: ").Append(Esc(StatusLabel((string)r["status"]))).Append("</p>");
        string intent = r["intent"] != null ? (string)r["intent"] : "";
        if (intent.Length > 0) { sb.Append("<h2>Angegebene Absicht</h2><pre>").Append(Esc(intent)).Append("</pre>"); }

        // Eckdaten in Klartext (aufgeloeste GUIDs)
        sb.Append("<h2>Eckdaten</h2><table>");
        if (r["requester"] != null) sb.Append("<tr><th>Antragsteller</th><td>").Append(Esc((string)r["requester"])).Append("</td></tr>");
        if (r["clientGuid"] != null)
        {
            string cn = r["clientName"] != null ? (string)r["clientName"] : "(Name unbekannt)";
            sb.Append("<tr><th>Zielrechner</th><td>").Append(Esc(cn)).Append(" <small>").Append(Esc((string)r["clientGuid"])).Append("</small></td></tr>");
        }
        if (r["clientUser"] != null) sb.Append("<tr><th>Letzter Benutzer</th><td>").Append(Esc((string)r["clientUser"])).Append("</td></tr>");
        if (r["commandGuid"] != null)
        {
            string an = r["commandName"] != null ? (string)r["commandName"] : "(Name unbekannt)";
            sb.Append("<tr><th>Aktion</th><td>").Append(Esc(an)).Append(" <small>").Append(Esc((string)r["commandGuid"])).Append("</small></td></tr>");
        }
        if (r["ignoreMaintenance"] != null && (bool)r["ignoreMaintenance"]) sb.Append("<tr><th>Wartungsfenster</th><td>wird ignoriert (sofortige Ausführung)</td></tr>");
        sb.Append("</table>");

        // Prominent: WAS genau ausgefuehrt wird (Skript/Variablen im Klartext, nicht im XML vergraben).
        JArray dvars = r["vars"] as JArray;
        bool isPs = ((string)r["tool"]).IndexOf("powershell", StringComparison.OrdinalIgnoreCase) >= 0;
        if (dvars != null && dvars.Count > 0)
        {
            sb.Append("<h2>Was wird ausgeführt</h2>");
            foreach (JObject v in dvars)
            {
                string nm = (string)v["name"]; string val = DisplayVar(v["value"] != null ? (string)v["value"] : "");
                bool isScript = isPs || val.IndexOf('\n') >= 0;
                sb.Append("<div class=\"runbox\"><div class=\"lbl\">")
                  .Append(isScript ? "Skript in Variable „" + Esc(nm) + "“ — läuft als SYSTEM auf dem Client" : "Variable „" + Esc(nm) + "“")
                  .Append("</div><pre class=\"script\">").Append(Esc(val.Length > 0 ? val : "(leer)")).Append("</pre></div>");
            }
        }
        else if (r["commandGuid"] != null)
        {
            string an = r["commandName"] != null ? (string)r["commandName"] : (string)r["commandGuid"];
            sb.Append("<h2>Was wird ausgeführt</h2><div class=\"runbox\"><p style=\"margin:0\">Vordefinierte ACMP-Aktion <b>").Append(Esc(an)).Append("</b> ohne Ad-hoc-Parameter. Ihre internen Schritte sind in ACMP hinterlegt und hier nicht im Klartext einsehbar — geprüft wird, dass genau diese benannte Aktion ausgeführt wird.</p></div>");
        }

        // Zwei Spalten: links der Roh-Befehl (formatiert + Highlighting), rechts Schritt fuer Schritt mit Zeilenbezug.
        string rmode = (string)r["mode"];
        string xmlPayload = rmode == "publish" ? (string)r["raw"] : (string)r["inner"];
        JArray rvars = r["vars"] as JArray;
        bool hasScript = rvars != null && rvars.Count > 0;
        string xHtml = null; List<string[]> xSteps = null;
        bool xmlOk = false;
        if (!hasScript) xmlOk = BuildXmlView(xmlPayload, out xHtml, out xSteps);

        sb.Append("<div class=\"cols\">");
        sb.Append("<div class=\"col\"><h2>Roh-Befehl (genau das wird ausgeführt)</h2>");
        if (rmode != "publish")
            sb.Append("<div class=\"cmdhdr\">Kommando <b>").Append(Esc((string)r["command"])).Append("</b> <span class=\"vtag\">v").Append(Esc(r["version"] != null ? r["version"].ToString() : "1")).Append("</span></div>");
        if (xmlOk) sb.Append(xHtml);
        else { sb.Append("<pre>").Append(Esc(rmode == "publish" ? (string)r["raw"] : (string)r["inner"])).Append("</pre>"); }
        sb.Append("</div>");

        sb.Append("<div class=\"col\"><h2>Schritt für Schritt</h2>");
        if (xmlOk)
        {
            sb.Append("<p style=\"margin:0 0 8px\"><small>Zeilennummern beziehen sich auf den Roh-Befehl links.</small></p><div class=\"expl\">");
            foreach (string[] st in xSteps)
            {
                int d = 0; int.TryParse(st[0], out d);
                sb.Append("<div class=\"xstep\" style=\"margin-left:").Append(d * 16).Append("px\"><span class=\"lc\">").Append(Esc(st[1])).Append("</span><span>").Append(Esc(st[2])).Append("</span></div>");
            }
            sb.Append("</div>");
        }
        else
        {
            sb.Append("<ol class=\"steps\">");
            foreach (string step in BuildSteps(r)) sb.Append("<li>").Append(Esc(step)).Append("</li>");
            sb.Append("</ol>");
        }
        sb.Append("</div></div>");

        string status = (string)r["status"];
        if (status == "done" || status == "failed")
        {
            sb.Append("<h2>").Append((bool)r["resultIsError"] ? "Ergebnis (Fehler)" : "Ergebnis der Ausführung").Append("</h2><pre>");
            sb.Append(Esc(Truncate(r["result"] != null ? (string)r["result"] : "", 6000))).Append("</pre>");
        }

        JArray ap = r["approvals"] as JArray;
        sb.Append("<p>Zustimmungen: ").Append(ap != null ? ap.Count : 0).Append(" / ").Append((int)r["needed"]);
        if (ap != null && ap.Count > 0) { var ns = new List<string>(); foreach (JObject a in ap) ns.Add((string)a["name"]); sb.Append(" (").Append(Esc(string.Join(", ", ns.ToArray()))).Append(")"); }
        sb.Append("</p>");

        bool already = false;
        if (ap != null) foreach (JObject a in ap) if (string.Equals((string)a["name"], approver, StringComparison.OrdinalIgnoreCase)) already = true;
        if ((string)r["status"] != "pending") sb.Append("<p class=\"warn\">Diese Anfrage ist nicht mehr offen.</p>");
        else if (already) sb.Append("<p class=\"warn\">Du hast dieser Anfrage bereits zugestimmt. Eine zweite, andere Person muss zustimmen.</p>");
        else sb.Append("<p class=\"actions\"><a class=\"btn btn-approve\" href=\"/approve?id=").Append(Esc(id)).Append("&").Append(qs).Append("\">Freigeben</a>")
                .Append("<a class=\"btn btn-reject\" href=\"/reject?id=").Append(Esc(id)).Append("&").Append(qs).Append("\">Ablehnen</a></p>");
        sb.Append("<p><a href=\"/approvals?").Append(qs).Append("\">&larr; Offene Freigaben</a> &nbsp;|&nbsp; <a href=\"/history?").Append(qs).Append("\">Verlauf</a></p>");
        return PageNav(sb.ToString(), approver, qs, "approvals");
    }

    string RedirectPage(string url)
    {
        return "<!doctype html><html><head><meta charset=\"utf-8\"><meta http-equiv=\"refresh\" content=\"0;url=" + url + "\"></head><body></body></html>";
    }

    void SaveConfig()
    {
        if (CfgPath == null || !File.Exists(CfgPath)) return;
        try
        {
            lock (_gate)
            {
                var scCfg = JObject.Parse(File.ReadAllText(CfgPath, Encoding.UTF8));
                var scAppr = scCfg["approval"] as JObject;
                if (scAppr == null) { scAppr = new JObject(); scCfg["approval"] = scAppr; }
                var scArr = new JArray();
                if (NoApprovalTools != null) foreach (string t in NoApprovalTools) scArr.Add(t);
                scAppr["noApprovalTools"] = scArr;
                scAppr["bypassAll"] = BypassAll;
                if (ToolApprovalCounts != null)
                {
                    var scTc = new JObject();
                    foreach (KeyValuePair<string, int> kv in ToolApprovalCounts) scTc[kv.Key] = kv.Value;
                    scAppr["toolApprovalCounts"] = scTc;
                }
                File.WriteAllText(CfgPath, scCfg.ToString(Formatting.Indented), Encoding.UTF8);
            }
            _log("Einstellungen gespeichert: bypassAll=" + BypassAll + ", noApprovalTools=" + (NoApprovalTools != null ? NoApprovalTools.Count : 0));
        }
        catch (Exception ex) { _log("SaveConfig Fehler: " + ex.Message); }
    }

    // ---------- Onboarding: neue Bediener anlegen (MCP-API-Key + optional ACMP-Mapping + optional Freigeber) ----------
    static readonly Regex OnbIdRe = new Regex("^[A-Za-z0-9_.-]{2,32}$");

    static string RandomToken(int numBytes)
    {
        byte[] b = new byte[numBytes];
        using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(b);
        string s = Convert.ToBase64String(b);
        return s.Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    JObject ReadCfgSafe()
    {
        try { lock (_gate) { return JObject.Parse(File.ReadAllText(CfgPath, Encoding.UTF8)); } }
        catch (Exception ex) { _log("Onboarding ReadCfgSafe Fehler: " + ex.Message); return new JObject(); }
    }

    string OnboardingPage(string approver, string token, string qs)
    {
        JObject cfg = ReadCfgSafe();
        JObject mcp = cfg["mcp"] as JObject;
        JObject apprCfg = cfg["approval"] as JObject;
        var approverNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        JArray apprArr = apprCfg != null ? apprCfg["approvers"] as JArray : null;
        if (apprArr != null) foreach (JObject a in apprArr) { string n = a["name"] != null ? (string)a["name"] : null; if (!string.IsNullOrEmpty(n)) approverNames.Add(n); }

        var sb = new StringBuilder();
        sb.Append("<h1>Onboarding</h1><p>Neuen IT-Mitarbeiter für den ACMP-MCP-Server einrichten: eigener MCP-API-Key, Verknüpfung mit seinem lokalen ACMP-User, optional Freigabe-Zugang.</p>");

        sb.Append("<h2>Bestehende Bediener</h2><table><tr><th>Name</th><th>ACMP-User</th><th>Freigeber</th></tr>");
        string defaultReq = apprCfg != null && apprCfg["requester"] != null ? (string)apprCfg["requester"] : null;
        string defaultAcmpUser = mcp != null && mcp["defaultAcmpUser"] != null ? (string)mcp["defaultAcmpUser"] : null;
        if (!string.IsNullOrEmpty(defaultReq))
            sb.Append("<tr><td>").Append(Esc(defaultReq)).Append(" <small>(Standard-Key)</small></td><td>").Append(Esc(string.IsNullOrEmpty(defaultAcmpUser) ? "-" : defaultAcmpUser)).Append("</td><td>").Append(approverNames.Contains(defaultReq) ? "ja" : "-").Append("</td></tr>");
        JArray usersArr = mcp != null ? mcp["users"] as JArray : null;
        if (usersArr != null)
            foreach (JObject u in usersArr)
            {
                string nm = u["name"] != null ? (string)u["name"] : "";
                string au = u["acmpUser"] != null ? (string)u["acmpUser"] : null;
                sb.Append("<tr><td>").Append(Esc(nm)).Append("</td><td>").Append(Esc(string.IsNullOrEmpty(au) ? "- (noch offen)" : au)).Append("</td><td>").Append(approverNames.Contains(nm) ? "ja" : "-").Append("</td></tr>");
            }
        sb.Append("</table>");

        sb.Append("<h2>Neuen Mitarbeiter anlegen</h2>");
        sb.Append("<p class=\"s\">Voraussetzung: In ACMP existiert bereits ein lokaler ACMP-User für die Person (in der ACMP-Konsole angelegt) — der MCP-Server legt keine ACMP-User an, nur die Verknüpfung.</p>");
        sb.Append("<form method=\"get\" action=\"/onboarding-create\">");
        sb.Append("<input type=\"hidden\" name=\"approver\" value=\"").Append(Esc(approver)).Append("\">");
        sb.Append("<input type=\"hidden\" name=\"token\" value=\"").Append(Esc(token)).Append("\">");
        sb.Append("<p>Kurzname (für MCP-Requester &amp; Logs, z.B. <code>mmustermann</code>):<br><input name=\"name\" pattern=\"[A-Za-z0-9_.-]{2,32}\" required></p>");
        sb.Append("<p>Lokaler ACMP-Username (in ACMP bereits angelegt, z.B. <code>admin_mm</code>):<br><input name=\"acmpUser\" pattern=\"[A-Za-z0-9_.-]{2,32}\" required></p>");
        sb.Append("<p><label><input type=\"checkbox\" name=\"isApprover\" value=\"1\"> Soll auch Vier-Augen-Freigeber werden</label></p>");
        sb.Append("<p>E-Mail (nur nötig als Freigeber, für Benachrichtigungen):<br><input name=\"email\" type=\"email\"></p>");
        sb.Append("<p class=\"actions\"><button class=\"btn btn-approve\" type=\"submit\">Anlegen</button></p>");
        sb.Append("</form>");
        return PageNav(sb.ToString(), approver, qs, "onboarding");
    }

    string OnboardingCreate(Dictionary<string, string> q, string approver, string qs)
    {
        string name; q.TryGetValue("name", out name);
        string acmpUser; q.TryGetValue("acmpUser", out acmpUser);
        string email; q.TryGetValue("email", out email);
        bool isApprover = q.ContainsKey("isApprover") && q["isApprover"] == "1";

        name = name != null ? name.Trim() : null;
        acmpUser = acmpUser != null ? acmpUser.Trim() : null;
        email = email != null ? email.Trim() : null;

        if (string.IsNullOrEmpty(name) || !OnbIdRe.IsMatch(name))
            return NoticePage("Fehler", "Ungültiger oder fehlender Kurzname (nur Buchstaben/Zahlen/._- , 2-32 Zeichen).", approver, qs);
        if (string.IsNullOrEmpty(acmpUser) || !OnbIdRe.IsMatch(acmpUser))
            return NoticePage("Fehler", "Ungültiger oder fehlender ACMP-Username.", approver, qs);
        if (isApprover && (string.IsNullOrEmpty(email) || email.IndexOf('@') < 0))
            return NoticePage("Fehler", "Freigeber brauchen eine gültige E-Mail-Adresse für Benachrichtigungen.", approver, qs);

        string newApiKey = RandomToken(32);
        string newApprovalToken = isApprover ? RandomToken(24) : null;

        string err;
        if (!SaveOnboardingUser(name, acmpUser, isApprover, email, newApiKey, newApprovalToken, out err))
            return NoticePage("Fehler", "Konnte config.local.json nicht schreiben: " + err, approver, qs);

        _log("Onboarding: neuer Bediener '" + name + "' angelegt (acmpUser=" + acmpUser + ", Freigeber=" + isApprover + ") durch " + approver);

        var sb = new StringBuilder();
        sb.Append("<h1>Mitarbeiter angelegt: ").Append(Esc(name)).Append("</h1>");
        sb.Append("<div class=\"callout ok\" style=\"margin:12px 0;\"><b>Wichtig:</b> Diese Werte sind nur JETZT sichtbar. Sicher an ").Append(Esc(name)).Append(" weitergeben (nicht per unverschlüsseltem Chat/Mail).</div>");

        sb.Append("<h2>1. MCP-Zugang für sein Claude/Codex</h2>");
        sb.Append("<p>Sein persönlicher API-Key:</p><p><input readonly style=\"width:100%;font-family:monospace\" value=\"").Append(Esc(newApiKey)).Append("\" onclick=\"this.select()\"></p>");
        sb.Append("<p><b>Claude Code (CLI):</b></p><pre>claude mcp add --transport http acmp ").Append(Esc(_publicUrl)).Append("/ --header \"Authorization: Bearer ").Append(Esc(newApiKey)).Append("\"</pre>");
        sb.Append("<p><b>Claude Desktop</b> (claude_desktop_config.json):</p><pre>{\n  \"mcpServers\": {\n    \"acmp\": {\n      \"command\": \"npx\",\n      \"args\": [\"-y\", \"mcp-remote\", \"").Append(Esc(_publicUrl)).Append("/\", \"--header\", \"Authorization: Bearer ").Append(Esc(newApiKey)).Append("\"]\n    }\n  }\n}</pre>");

        sb.Append("<h2>2. ACMP-Identität verknüpfen (muss er selbst tun)</h2>");
        sb.Append("<p>Per RDP auf euren ACMP-Host verbinden, dort einmalig:</p>");
        sb.Append("<pre>cd ").Append(Esc(InstallDir())).Append("\n.\\acmp-token-setup.exe</pre>");
        sb.Append("<p>Eingaben: <b>Domain-Benutzername</b> = <code>").Append(Esc(acmpUser)).Append("</code>, <b>Domain</b> = leer lassen (Enter), <b>Passwort</b> = sein ACMP-Passwort. Wird nicht gespeichert, nur ein Token.</p>");

        int step = 3;
        if (isApprover)
        {
            sb.Append("<h2>").Append(step++).Append(". Freigabe-Zugang</h2>");
            sb.Append("<p>Sein Freigabe-Token:</p><p><input readonly style=\"width:100%;font-family:monospace\" value=\"").Append(Esc(newApprovalToken)).Append("\" onclick=\"this.select()\"></p>");
            sb.Append("<p>Direkter Link: <code>").Append(Esc(_publicUrl)).Append("/dashboard?approver=").Append(Uri.EscapeDataString(name)).Append("&amp;token=").Append(Uri.EscapeDataString(newApprovalToken)).Append("</code></p>");
        }

        sb.Append("<h2>").Append(step).Append(". Aktivieren</h2>");
        sb.Append("<p>Erst nach einem Neustart des Server-Tasks (Admin, auf dem Host) werden neuer API-Key und ACMP-Mapping wirksam:</p>");
        sb.Append("<pre>schtasks /End /TN \"ACMP-MCP-Server\"\nschtasks /Run /TN \"ACMP-MCP-Server\"</pre>");
        sb.Append("<p class=\"actions\"><a class=\"btn btn-ghost\" href=\"/onboarding?").Append(qs).Append("\">&larr; Zurück zum Onboarding</a></p>");

        return PageNav(sb.ToString(), approver, qs, "onboarding");
    }

    bool SaveOnboardingUser(string name, string acmpUser, bool isApprover, string email, string apiKey, string approvalToken, out string err)
    {
        err = null;
        if (CfgPath == null || !File.Exists(CfgPath)) { err = "CfgPath fehlt"; return false; }
        try
        {
            lock (_gate)
            {
                var cfg = JObject.Parse(File.ReadAllText(CfgPath, Encoding.UTF8));
                var mcp = cfg["mcp"] as JObject;
                if (mcp == null) { mcp = new JObject(); cfg["mcp"] = mcp; }
                var users = mcp["users"] as JArray;
                if (users == null) { users = new JArray(); mcp["users"] = users; }

                string defaultReq = cfg["approval"] != null && cfg["approval"]["requester"] != null ? (string)cfg["approval"]["requester"] : null;
                if (string.Equals(defaultReq, name, StringComparison.OrdinalIgnoreCase)) { err = "Name kollidiert mit dem Standard-Bediener."; return false; }
                foreach (JObject u in users)
                    if (string.Equals(u["name"] != null ? (string)u["name"] : null, name, StringComparison.OrdinalIgnoreCase)) { err = "Name existiert bereits."; return false; }

                var nu = new JObject();
                nu["name"] = name;
                nu["apiKey"] = apiKey;
                nu["acmpUser"] = string.IsNullOrEmpty(acmpUser) ? null : acmpUser;
                users.Add(nu);

                if (isApprover)
                {
                    var appr = cfg["approval"] as JObject;
                    if (appr == null) { appr = new JObject(); cfg["approval"] = appr; }
                    var apprArr = appr["approvers"] as JArray;
                    if (apprArr == null) { apprArr = new JArray(); appr["approvers"] = apprArr; }
                    var na = new JObject();
                    na["name"] = name;
                    na["token"] = approvalToken;
                    na["email"] = email;
                    apprArr.Add(na);
                }

                File.WriteAllText(CfgPath, cfg.ToString(Formatting.Indented), Encoding.UTF8);
            }
            return true;
        }
        catch (Exception ex) { err = ex.Message; _log("Onboarding SaveOnboardingUser Fehler: " + ex.Message); return false; }
    }

    // ---------- Ersteinrichtung: allerersten Administrator/Freigeber ohne bestehenden Login anlegen ----------
    string HandleSetup(string path, Dictionary<string, string> q)
    {
        if (_setupToken == null)
            return Page("<h1>Ersteinrichtung nicht verfügbar</h1><p>Es ist bereits mindestens ein Freigeber konfiguriert. Neue Bediener über <a href=\"/onboarding\">/onboarding</a> anlegen (Login nötig).</p>");
        string tok; q.TryGetValue("token", out tok);
        if (string.IsNullOrEmpty(tok) || tok != _setupToken)
            return Page("<h1>Ersteinrichtung</h1><p>Ungültiger oder fehlender Setup-Token. Den Token findest du in der Server-Konsole/<code>server.log</code> beim Start (Zeile <code>ERSTEINRICHTUNG NOETIG</code>).</p>");
        return path == "/setup" ? SetupPage(tok) : SetupCreate(q, tok);
    }

    string SetupPage(string tok)
    {
        var sb = new StringBuilder();
        sb.Append("<h1>Ersteinrichtung</h1><p>Kein Freigeber konfiguriert — lege den ersten Administrator/Freigeber für diesen ACMP-MCP-Server an. Diese Seite deaktiviert sich danach automatisch.</p>");
        sb.Append("<form method=\"get\" action=\"/setup-create\">");
        sb.Append("<input type=\"hidden\" name=\"token\" value=\"").Append(Esc(tok)).Append("\">");
        sb.Append("<p>Kurzname (für MCP-Requester, Logs &amp; Freigabe-Login, z.B. <code>admin</code>):<br><input name=\"name\" pattern=\"[A-Za-z0-9_.-]{2,32}\" required></p>");
        sb.Append("<p>Lokaler ACMP-Username (optional — falls noch kein ACMP-User existiert, hier leer lassen und später über /onboarding nachtragen):<br><input name=\"acmpUser\" pattern=\"[A-Za-z0-9_.-]{0,32}\"></p>");
        sb.Append("<p>E-Mail (für Freigabe-Benachrichtigungen):<br><input name=\"email\" type=\"email\" required></p>");
        sb.Append("<p class=\"actions\"><button class=\"btn btn-approve\" type=\"submit\">Ersten Administrator anlegen</button></p>");
        sb.Append("</form>");
        return Page(sb.ToString());
    }

    string SetupCreate(Dictionary<string, string> q, string tok)
    {
        string name; q.TryGetValue("name", out name);
        string acmpUser; q.TryGetValue("acmpUser", out acmpUser);
        string email; q.TryGetValue("email", out email);
        name = name != null ? name.Trim() : null;
        acmpUser = acmpUser != null ? acmpUser.Trim() : null;
        email = email != null ? email.Trim() : null;
        string back = "<p><a href=\"/setup?token=" + Uri.EscapeDataString(tok) + "\">&larr; Zurück</a></p>";

        if (string.IsNullOrEmpty(name) || !OnbIdRe.IsMatch(name))
            return Page("<h1>Fehler</h1><p>Ungültiger oder fehlender Kurzname (nur Buchstaben/Zahlen/._- , 2-32 Zeichen).</p>" + back);
        if (!string.IsNullOrEmpty(acmpUser) && !OnbIdRe.IsMatch(acmpUser))
            return Page("<h1>Fehler</h1><p>Ungültiger ACMP-Username.</p>" + back);
        if (string.IsNullOrEmpty(email) || email.IndexOf('@') < 0)
            return Page("<h1>Fehler</h1><p>Gültige E-Mail-Adresse nötig (für Freigabe-Benachrichtigungen).</p>" + back);

        string newApiKey = RandomToken(32);
        string newApprovalToken = RandomToken(24);
        string err;
        if (!SaveOnboardingUser(name, acmpUser, true, email, newApiKey, newApprovalToken, out err))
            return Page("<h1>Fehler</h1><p>Konnte config.local.json nicht schreiben: " + Esc(err) + "</p>" + back);

        _setupToken = null; // Ersteinrichtung dauerhaft schliessen (fuer diesen laufenden Prozess)
        _log("Ersteinrichtung: erster Administrator/Freigeber '" + name + "' angelegt (acmpUser=" + (string.IsNullOrEmpty(acmpUser) ? "-" : acmpUser) + ").");

        var sb = new StringBuilder();
        sb.Append("<h1>Willkommen, ").Append(Esc(name)).Append("!</h1>");
        sb.Append("<div class=\"callout ok\" style=\"margin:12px 0;\"><b>Wichtig:</b> Diese Werte sind nur JETZT sichtbar — sicher aufbewahren.</div>");

        sb.Append("<h2>Dein MCP-API-Key</h2><p><input readonly style=\"width:100%;font-family:monospace\" value=\"").Append(Esc(newApiKey)).Append("\" onclick=\"this.select()\"></p>");
        sb.Append("<p><b>Claude Code (CLI):</b></p><pre>claude mcp add --transport http acmp ").Append(Esc(_publicUrl)).Append("/ --header \"Authorization: Bearer ").Append(Esc(newApiKey)).Append("\"</pre>");
        sb.Append("<p><b>Claude Desktop</b> (claude_desktop_config.json):</p><pre>{\n  \"mcpServers\": {\n    \"acmp\": {\n      \"command\": \"npx\",\n      \"args\": [\"-y\", \"mcp-remote\", \"").Append(Esc(_publicUrl)).Append("/\", \"--header\", \"Authorization: Bearer ").Append(Esc(newApiKey)).Append("\"]\n    }\n  }\n}</pre>");

        sb.Append("<h2>Dein Freigabe-Login</h2><p><input readonly style=\"width:100%;font-family:monospace\" value=\"").Append(Esc(newApprovalToken)).Append("\" onclick=\"this.select()\"></p>");
        sb.Append("<p>Direkter Link: <code>").Append(Esc(_publicUrl)).Append("/dashboard?approver=").Append(Uri.EscapeDataString(name)).Append("&amp;token=").Append(Uri.EscapeDataString(newApprovalToken)).Append("</code> — am besten als Lesezeichen speichern.</p>");

        if (string.IsNullOrEmpty(acmpUser))
            sb.Append("<h2>ACMP-Identität verknüpfen</h2><p>Noch kein ACMP-User verknüpft. Sobald einer existiert, über <a href=\"/dashboard?approver=").Append(Uri.EscapeDataString(name)).Append("&token=").Append(Uri.EscapeDataString(newApprovalToken)).Append("\">Dashboard</a> → Onboarding nachtragen.</p>");
        else
        {
            sb.Append("<h2>ACMP-Identität verknüpfen (muss du selbst tun)</h2>");
            sb.Append("<p>Per RDP auf den ACMP-Host verbinden, dort einmalig:</p><pre>cd ").Append(Esc(InstallDir())).Append("\n.\\acmp-token-setup.exe</pre>");
            sb.Append("<p>Eingaben: <b>Domain-Benutzername</b> = <code>").Append(Esc(acmpUser)).Append("</code>, <b>Domain</b> = leer lassen (Enter), <b>Passwort</b> = dein ACMP-Passwort. Wird nicht gespeichert, nur ein Token.</p>");
        }

        sb.Append("<h2>Aktivieren</h2><p>Server-Task einmal neu starten (Admin, auf dem Host), damit alles greift:</p>");
        sb.Append("<pre>schtasks /End /TN \"ACMP-MCP-Server\"\nschtasks /Run /TN \"ACMP-MCP-Server\"</pre>");
        sb.Append("<p class=\"actions\"><a class=\"btn btn-approve\" href=\"/dashboard?approver=").Append(Uri.EscapeDataString(name)).Append("&token=").Append(Uri.EscapeDataString(newApprovalToken)).Append("\">Weiter zum Dashboard</a></p>");
        return Page(sb.ToString());
    }

    public void ApproveReadTool(string tool)
    {
        lock (_gate) { _readApprovals[tool] = DateTime.UtcNow; }
    }

    public bool ConsumeReadApproval(string tool)
    {
        lock (_gate)
        {
            DateTime dt;
            if (!_readApprovals.TryGetValue(tool, out dt)) return false;
            _readApprovals.Remove(tool);
            if ((DateTime.UtcNow - dt).TotalSeconds > 3600) return false;
            return true;
        }
    }

    public string EnqueueReadGate(string tool, string intent, string requester)
    {
        JObject r = new JObject();
        r["mode"] = "read-gate"; r["command"] = "(read-gate)"; r["version"] = 0; r["inner"] = null; r["raw"] = null;
        return Enqueue(r, tool, "(read-gate)", tool, null, RiskTier.Read, intent, requester);
    }

    int GetToolApprovalCount(string tool, RiskTier tier)
    {
        if (ToolApprovalCounts != null) { int n; if (ToolApprovalCounts.TryGetValue(tool, out n)) return n; }
        if (NoApprovalTools != null && NoApprovalTools.Contains(tool)) return 0;
        if (tier == RiskTier.Read) return 0;
        return ApproversNeeded(tier);
    }

    string SettingsPage(string approver, string qs)
    {
        var sb = new StringBuilder();
        sb.Append("<style>.cntg{display:inline-flex;border-radius:6px;overflow:hidden;border:1px solid rgba(10,15,35,.2)}.cnt{padding:4px 12px;text-decoration:none;color:#545e66;background:#f6f8fb;font-weight:500;font-size:13px}.cnt:hover{background:#e8ecf0;text-decoration:none}.cnt-active{background:#00459a!important;color:#fff!important;font-weight:700}.cnt-active:hover{text-decoration:none}</style>");
        sb.Append("<h1>Einstellungen</h1>");
        sb.Append("<p>Vier-Augen-Freigabe pro Tool steuern. Änderungen werden sofort in <code>config.local.json</code> gespeichert.</p>");

        sb.Append("<h2>Globaler Schalter</h2>");
        sb.Append("<table><tr><th>Einstellung</th><th>Status</th><th>Aktion</th></tr><tr>");
        sb.Append("<td><b>Alle Freigaben &uuml;berspringen</b><br><small>Write- und Destructive-Tools laufen sofort ohne Vier-Augen-Gate durch</small></td>");
        sb.Append("<td>").Append(BypassAll ? "<span class=\"tier-Destructive\">AN</span>" : "<span class=\"tier-Read\">AUS</span>").Append("</td>");
        sb.Append("<td>");
        if (BypassAll) sb.Append("<a class=\"btn btn-approve\" href=\"/settings-bypass?enabled=0&").Append(qs).Append("\">Freigaben reaktivieren</a>");
        else sb.Append("<a class=\"btn btn-reject\" href=\"/settings-bypass?enabled=1&").Append(qs).Append("\">Alle Freigaben deaktivieren</a>");
        sb.Append("</td></tr></table>");
        sb.Append("<p><small>0&nbsp;= direkt ohne Gate &nbsp;&nbsp; 1&nbsp;= ein Freigeber &nbsp;&nbsp; 2&nbsp;= zwei Freigeber. &Auml;nderungen werden sofort in config.local.json gespeichert.</small></p>");
        JArray allTools2 = GetAllTools != null ? GetAllTools() : null;
        if (allTools2 == null) { sb.Append("<p>Keine Tools.</p>"); return PageNav(sb.ToString(), approver, qs, "settings"); }
        // Kategorisierung nach ZIEL/Entitaet - Lesen und Schreiben derselben Daten stehen zusammen.
        // Reihenfolge der Pruefungen = Prioritaet (erste Treffer gewinnt); spezifischere Begriffe zuerst.
        Func<string, string> catOf = delegate(string nm) {
            if (nm.Contains("ticket") || nm.Contains("helpdesk") || nm.Contains("assignee") || nm.Contains("queries") || nm.Contains("by_query")) return "Helpdesk & Tickets";
            if (nm.Contains("contact") || nm.Contains("compan") || nm.Contains("location") || nm.Contains("department")
                || nm.Contains("cost_center") || nm.Contains("contract") || nm.Contains("custom_field")) return "Stammdaten";
            if (nm.Contains("container")) return "Container";
            if (nm.Contains("asset")) return "Asset-Management";
            if (nm.Contains("vulnerab") || nm.Contains("defender") || nm.Contains("bitlocker") || nm.Contains("oval_scan") || nm.Contains("license") || nm.Contains("compliance")) return "Sicherheit & Compliance";
            if (nm.Contains("teamviewer")) return "Fernwartung";
            if (nm.Contains("powershell") || nm.Contains("client_command") || nm.Contains("enqueue") || nm.Contains("job") || nm.Contains("script_history")) return "Client-Kommandos & Jobs";
            if (nm.Contains("hardware") || nm.Contains("software") || nm.Contains("os_info") || nm.Contains("update")
                || nm.Contains("disk") || nm.Contains("agent") || nm.Contains("offline") || nm.Contains("client")) return "Clients & Inventar";
            if (nm == "acmp_call" || nm.Contains("list_commands") || nm.Contains("server_info") || nm.Contains("describe") || nm.Contains("approval") || nm.Contains("discover")) return "System & Generisch";
            return "Sonstiges";
        };
        // Sortierschluessel innerhalb einer Kategorie: Verb wegstreichen, Plural-s normalisieren
        // -> create_contact / update_contact / get_contacts clustern unter "contact".
        Func<string, string> subjOf = delegate(string nm) {
            string s = nm.StartsWith("acmp_") ? nm.Substring(5) : nm;
            string[] verbs = new string[] { "create_", "update_", "get_", "save_", "delete_", "add_", "close_", "reopen_", "lend_", "return_", "run_", "enqueue_", "list_", "discover_" };
            foreach (string v in verbs) { if (s.StartsWith(v)) { s = s.Substring(v.Length); break; } }
            if (s.EndsWith("s")) s = s.Substring(0, s.Length - 1);
            return s;
        };
        string[] catOrder2 = new string[] { "Clients & Inventar", "Client-Kommandos & Jobs", "Sicherheit & Compliance", "Helpdesk & Tickets", "Asset-Management", "Container", "Stammdaten", "Fernwartung", "System & Generisch", "Sonstiges" };
        var catMap = new Dictionary<string, List<JObject>>();
        foreach (string c in catOrder2) catMap[c] = new List<JObject>();
        foreach (JToken tok2 in allTools2)
        {
            var t2 = tok2 as JObject; if (t2 == null) continue;
            string cat = catOf((string)t2["name"]);
            List<JObject> lst2; if (!catMap.TryGetValue(cat, out lst2)) { lst2 = new List<JObject>(); catMap[cat] = lst2; }
            lst2.Add(t2);
        }
        // Innerhalb jeder Kategorie nach Datenobjekt gruppieren (gleiche Daten untereinander).
        foreach (List<JObject> lstS in catMap.Values)
            lstS.Sort(delegate(JObject x, JObject y) {
                string sx = subjOf((string)x["name"]), sy = subjOf((string)y["name"]);
                int c = string.Compare(sx, sy, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : string.Compare((string)x["name"], (string)y["name"], StringComparison.OrdinalIgnoreCase);
            });
        foreach (string cat2 in catOrder2)
        {
            List<JObject> lst3; catMap.TryGetValue(cat2, out lst3);
            if (lst3 == null || lst3.Count == 0) continue;
            sb.Append("<h2>").Append(Esc(cat2)).Append("</h2>");
            sb.Append("<table><tr><th>Tool</th><th>Risiko</th><th>Freigaben</th></tr>");
            foreach (JObject t3 in lst3)
            {
                string tName3 = (string)t3["name"];
                string tierStr3 = "Read";
                bool gated3 = GatedToolTiers != null && GatedToolTiers.TryGetValue(tName3, out tierStr3);
                if (!gated3) tierStr3 = "Read";
                RiskTier rtier3 = (!gated3) ? RiskTier.Read : (tierStr3 == "Destructive" ? RiskTier.Destructive : RiskTier.Write);
                int curCount = GetToolApprovalCount(tName3, rtier3);
                string desc3 = t3["description"] != null ? (string)t3["description"] : "";
                if (desc3.Length > 80) desc3 = desc3.Substring(0, 77) + "...";
                sb.Append("<tr><td><b>").Append(Esc(tName3)).Append("</b><br><small>").Append(Esc(desc3)).Append("</small></td>");
                sb.Append("<td class=\"tier-").Append(Esc(tierStr3)).Append("\">").Append(Esc(tierStr3)).Append("</td>");
                if (BypassAll)
                {
                    sb.Append("<td><span class=\"tier-Destructive\">&Uuml;bersprungen (globaler Schalter)</span></td>");
                }
                else
                {
                    sb.Append("<td><div class=\"cntg\">");
                    for (int ci = 0; ci <= 2; ci++)
                    {
                        string cls3 = (curCount == ci) ? " cnt-active" : "";
                        sb.Append("<a href=\"/settings-approval?tool=").Append(Uri.EscapeDataString(tName3)).Append("&count=").Append(ci).Append("&").Append(qs).Append("\" class=\"cnt").Append(cls3).Append("\">").Append(ci).Append("</a>");
                    }
                    string lbl3 = curCount == 0 ? "kein Gate" : (curCount == 1 ? "1 Freigeber" : "2 Freigeber");
                    sb.Append("</div>&nbsp;<small>").Append(lbl3).Append("</small></td>");
                }
                sb.Append("</tr>");
            }
            sb.Append("</table>");
        }
        return PageNav(sb.ToString(), approver, qs, "settings");
    }
}
