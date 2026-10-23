// AcmpCcWriter: Client Commands direkt in der ACMP-DB lesen (Decode) und als ENTWURF anlegen/aendern.
// Standardmaessig AUS (ccWriter.enabled). Direktes Schreiben in die ACMP-DB ist von Aagon nicht vorgesehen.
// Sicherheitsregeln:
//  - Schreiben nur mit eigenem, eng berechtigtem SQL-Login (ccWriter.connectionString), nie mit dem Lese-Login.
//  - Nur Antragsteller aus ccWriter.allowedRequesters und nur nach bestandener Kompatibilitaetspruefung (AcmpCcPreflight);
//    jede Schreibaktion steht im Aktivitaetsprotokoll, die Notbremse sperrt alle.
//  - Neu angelegt wird immer als Entwurf (State 1), nie freigegeben. Aendern nur eigene Entwuerfe
//    (in cc-drafts.json registriert UND State 1). Freigeben passiert in der ACMP-Console.
//  - Vor jeder Aenderung Sicherung der betroffenen Spalten nach ccWriter.backupDir; ohne Sicherung kein Schreiben.
//  - PASSWORD, CONDITIONS und Flags werden serverseitig aus einer Vorlage-Zeile kopiert (INSERT ... SELECT)
//    und verlassen die DB nie.
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Formatting = Newtonsoft.Json.Formatting;

// Optionen fuer BuildScript bei neuen Versionen (acmp_cc_new_version): Schritt-IDs behalten, '***' in Kennwortfeldern durch das
// gespeicherte Kennwort desselben Schritts der Basisversion ersetzen. Fuer alle anderen Tools null (neue IDs, '***' abgelehnt).
class CcBuildCtx
{
    public bool KeepIds;
    public HashSet<string> UsedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    // Schritt-ID -> (Klasse, Kennwortfeld -> gespeicherter Wert); null = keine Uebernahme
    public Dictionary<string, KeyValuePair<string, Dictionary<string, string>>> Secrets;
    public string SecretSource;     // fuer Hinweise, z.B. "v3"
    public int SecretsTaken;
}

partial class AcmpCcWriter
{
    readonly string _readCs, _writeCs, _templateId, _backupDir, _registryFile;
    readonly HashSet<string> _allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _allowedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    // acmp_cc_move / acmp_cc_add_folder nur innerhalb dieser Ordner samt Unterordnern (leer = ganzer Baum), AcmpCcFolders.cs
    readonly HashSet<string> _moveScope = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    // acmp_cc_new_version nur fuer Commands in diesen Ordnern samt Unterordnern (leer = alle ausser Aagon-Standardordnern), AcmpCcVersion.cs
    readonly HashSet<string> _versionScope = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    // Dateien anhaengen: nur aus diesen Freigaben (sonst koennte man z.B. config.local.json in einen CC kopieren)
    readonly List<string> _fileSourceRoots = new List<string>();
    readonly string _fileRepoRoot;
    readonly long _maxFileBytes;
    readonly Action<string> _log;
    readonly Func<string> _acmpVersion;      // z.B. "6.10.4" (GetACMPServerInfo), fuer die Kompatibilitaetspruefung
    readonly bool _allowUntested;            // ccWriter.allowUntested: ungetestete ACMP-Version / unbekannte Trigger bewusst zulassen
    readonly int _preflightSample;
    JObject _preflight; DateTime _preflightAt;
    static readonly object _lock = new object();
    static readonly object _pfLock = new object();
    const int PreflightMaxAgeMinutes = 60;

    // ConfigOk: ccWriter eingeschaltet und vollstaendig konfiguriert (Schreib-Tools werden angeboten).
    // Geschrieben wird trotzdem nur, wenn zusaetzlich die Kompatibilitaetspruefung bestanden ist (CheckWrite).
    public bool ConfigOk { get; private set; }
    public bool WriteEnabled { get { return ConfigOk; } }
    public string DisabledReason { get; private set; }
    public IEnumerable<string> FileSourceRoots { get { return _fileSourceRoots; } }
    public ICollection<string> AllowedFolders { get { return _allowedFolders; } }
    public ICollection<string> AllowedRequesters { get { return _allowed; } }
    public ICollection<string> MoveScope { get { return _moveScope; } }
    public ICollection<string> VersionScope { get { return _versionScope; } }

    static readonly string[][] CommonFields = {
        new[] { "Description", null }, new[] { "_CommandComment", "" }, new[] { "ID", null }, new[] { "_DataVersion", "1" },
        new[] { "HasBreakpoint", "False" }, new[] { "Timeout_after", "0" }, new[] { "Enabled", "True" }, new[] { "Ignore_error", "False" },
        new[] { "isSystem", "True" }, new[] { "Command_relation", "Run_always" }, new[] { "HighestAvailableRights", "False" },
        new[] { "Execute_as", "teUser" }, new[] { "_CaseValue", "" }, new[] { "_SwitchValue", "" } };

    public AcmpCcWriter(string readCs, JObject cfg, string cfgDir, Action<string> log, Func<string> acmpVersion)
    {
        _readCs = readCs; _log = log; _acmpVersion = acmpVersion;
        _registryFile = Path.Combine(cfgDir, "cc-drafts.json");
        string bd = cfg != null && cfg["backupDir"] != null ? (string)cfg["backupDir"] : "cc-backups";
        _backupDir = Path.IsPathRooted(bd) ? bd : Path.Combine(cfgDir, bd);
        // Kein Standard: Quellen fuer Dateien muessen bewusst eingetragen werden (UNC-Freigaben mit Installationsdateien)
        JArray fs = cfg != null ? cfg["fileSourceRoots"] as JArray : null;
        if (fs != null) foreach (JToken t in fs) { string s = ((string)t ?? "").Trim().TrimEnd('\\'); if (s.StartsWith(@"\\")) _fileSourceRoots.Add(s); }
        // Kein Standardpfad: das File Repository liegt in jeder Installation woanders (<Repository>\ClientCommands\Projects).
        // Ohne Angabe sind nur die Datei-Tools gesperrt.
        _fileRepoRoot = cfg != null && cfg["fileRepositoryRoot"] != null && ((string)cfg["fileRepositoryRoot"]).Trim().Length > 0
            ? ((string)cfg["fileRepositoryRoot"]).Trim().TrimEnd('\\') : null;
        _maxFileBytes = (cfg != null && cfg["maxFileMB"] != null ? (long)cfg["maxFileMB"] : 2048L) * 1024 * 1024;
        _allowUntested = cfg != null && cfg["allowUntested"] != null && (bool)cfg["allowUntested"];
        _preflightSample = cfg != null && cfg["preflightSample"] != null ? (int)cfg["preflightSample"] : 50;
        if (cfg == null || cfg["enabled"] == null || !(bool)cfg["enabled"]) { DisabledReason = "ccWriter.enabled ist nicht true (schreibende Aktionen ausgeschaltet)"; return; }
        if (cfg["approvers"] != null || cfg["approvalGate"] != null) _log("Hinweis: ccWriter.approvers/approvalGate werden nicht mehr verwendet - die Vier-Augen-Freigabe ist abgeschafft (Aktivitaetsprotokoll und Notbremse auf der Webseite).");
        _writeCs = (string)cfg["connectionString"];
        _templateId = GuidBraces((string)cfg["templateScriptId"]);
        JArray ar = cfg["allowedRequesters"] as JArray;
        if (ar != null) foreach (JToken t in ar) _allowed.Add((string)t);
        JArray af = cfg["allowedFolderIds"] as JArray;
        if (af != null) foreach (JToken t in af) _allowedFolders.Add(GuidBraces((string)t));
        JArray ms = cfg["moveScopeFolderIds"] as JArray;
        if (ms != null) foreach (JToken t in ms) { string g = GuidBraces((string)t); if (g != null) _moveScope.Add(g); }
        JArray vs = cfg["versionScopeFolderIds"] as JArray;
        if (vs != null) foreach (JToken t in vs) { string g = GuidBraces((string)t); if (g != null) _versionScope.Add(g); }
        if (string.IsNullOrEmpty(_readCs)) { DisabledReason = "db.connectionString fehlt (Lesezugang fuer Pruefung und Decoder)"; return; }
        if (string.IsNullOrEmpty(_writeCs) || _writeCs.Contains("HIER_")) { DisabledReason = "ccWriter.connectionString fehlt"; return; }
        if (_templateId == null) { DisabledReason = "ccWriter.templateScriptId fehlt"; return; }
        if (_allowed.Count == 0) { DisabledReason = "ccWriter.allowedRequesters ist leer"; return; }
        ConfigOk = true;
    }

    // ================= Kompatibilitaetspruefung =================
    // Nur fuer Offline-Tests ohne Datenbank: ein Pruefergebnis vorgeben (gilt wie eine frische Pruefung 60 Minuten).
    public void UsePreflightResult(JObject pf) { lock (_pfLock) { _preflight = pf; _preflightAt = DateTime.UtcNow; } }

    // Laeuft beim Start und spaetestens alle 60 Minuten vor einem Schreibvorgang erneut (z.B. nach einem ACMP-Update).
    public JObject Preflight(bool refresh)
    {
        if (!ConfigOk) return null;
        lock (_pfLock)
        {
            if (!refresh && _preflight != null && (DateTime.UtcNow - _preflightAt).TotalMinutes < PreflightMaxAgeMinutes) return _preflight;
            string v = null;
            try { v = _acmpVersion != null ? _acmpVersion() : null; } catch { }
            _preflight = AcmpCcPreflight.Run(_readCs, _writeCs, v, _allowUntested, _templateId, _allowedFolders, _fileRepoRoot, _preflightSample);
            _preflightAt = DateTime.UtcNow;
            _log("CC-Writer Kompatibilitaetspruefung (ACMP " + (v ?? "?") + "): " + (string)_preflight["summary"]);
            return _preflight;
        }
    }

    // ================= Lesen =================

    public JObject DecodeCommand(string idOrName, int version, bool fullXml)
    {
        if (string.IsNullOrEmpty(_readCs)) throw new Exception("DB-Lesezugang (db.connectionString) fehlt.");
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            var cmd = c.CreateCommand();
            string id = GuidBraces(idOrName);
            cmd.CommandText = "SELECT TOP 1 SCRIPTID, ScriptVersion, DESCRIPTION, FOLDERID, State, Creator, LastModifier, CLIENTSCRIPT, CONSOLESCRIPT, FileRepoVersion " +
                "FROM SYS_SCRIPTS WHERE " + (id != null ? "SCRIPTID=@k" : "DESCRIPTION=@k") +
                (version > 0 ? " AND ScriptVersion=@v" : "") + " ORDER BY ScriptVersion DESC";
            cmd.Parameters.AddWithValue("@k", id != null ? id : idOrName);
            if (version > 0) cmd.Parameters.AddWithValue("@v", version);
            JObject o; string sid; int repoVer;
            using (var r = cmd.ExecuteReader())
            {
                if (!r.Read()) throw new Exception("Client Command nicht gefunden: " + idOrName);
                o = new JObject();
                sid = r.GetString(0);
                repoVer = r.IsDBNull(9) ? 0 : Convert.ToInt32(r.GetValue(9));
                o["scriptId"] = r.GetString(0);
                o["version"] = r.GetInt32(1);
                o["name"] = r.IsDBNull(2) ? "" : r.GetString(2);
                o["folderId"] = r.IsDBNull(3) ? "" : r.GetString(3);
                int st = Convert.ToInt32(r.GetValue(4));
                o["state"] = st + (st == 1 ? " (Entwurf)" : st == 7 ? " (freigegeben)" : st == 8 ? " (abgeloest)" : "");
                o["creator"] = r.IsDBNull(5) ? "" : r.GetString(5);
                o["ownMcpDraft"] = IsRegistered(r.GetString(0));
                if (!r.IsDBNull(7)) o["clientScript"] = ScriptToJson(CcFormat.Decode((byte[])r.GetValue(7)), fullXml);
                if (!r.IsDBNull(8))
                {
                    var cs = CcFormat.Decode((byte[])r.GetValue(8));
                    if (cs.Roots.Count > 0) o["consoleScript"] = ScriptToJson(cs, fullXml);
                }
            }
            // Angehaengte Dateien (File Repository, Ordner "private") dieser Dateiversion
            var fq = c.CreateCommand();
            fq.CommandText = "SELECT Path, CAST(IsFolder AS bit) FROM SYS_SCRIPT_FILES WHERE ScriptID=@id AND FileRepoVersion=@rv ORDER BY Path";
            fq.Parameters.AddWithValue("@id", sid); fq.Parameters.AddWithValue("@rv", repoVer);
            var files = new JArray();
            using (var r = fq.ExecuteReader()) while (r.Read()) files.Add(r.GetBoolean(1) ? r.GetString(0) + "\\" : r.GetString(0));
            if (files.Count > 0) { o["fileRepoVersion"] = repoVer; o["files"] = files; }
            return o;
        }
    }

    // ================= Freigegebene Version lesbar =================
    // Die Version, die ACMP beim Einreihen ausfuehrt (neueste freigegebene, State 7), lesbar (Vorschau).
    // hash = SHA-256 des CLIENTSCRIPT-Blobs; der Server vergleicht ihn vor der Ausfuehrung, damit nach der Freigabe
    // kein geaenderter Command laeuft. Kennwoerter maskiert (NodeToJson).
    public JObject ReleasedSnapshot(string scriptId)
    {
        if (string.IsNullOrEmpty(_readCs)) return null;
        string id = GuidBraces(scriptId); if (id == null) return null;
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT TOP 1 ScriptVersion, DESCRIPTION, CLIENTSCRIPT, FileRepoVersion FROM SYS_SCRIPTS WHERE SCRIPTID=@k AND State=7 ORDER BY ScriptVersion DESC";
            cmd.Parameters.AddWithValue("@k", id);
            var o = new JObject(); o["scriptId"] = id;
            byte[] blob = null; int repoVer = 0;
            using (var r = cmd.ExecuteReader())
            {
                if (!r.Read()) { o["released"] = false; return o; }
                o["released"] = true;
                o["version"] = r.GetInt32(0);
                o["name"] = r.IsDBNull(1) ? "" : r.GetString(1);
                if (!r.IsDBNull(2)) blob = (byte[])r.GetValue(2);
                repoVer = r.IsDBNull(3) ? 0 : Convert.ToInt32(r.GetValue(3));
            }
            var lines = new List<string>();
            if (blob != null)
            {
                using (var sha = SHA256.Create()) o["hash"] = BitConverter.ToString(sha.ComputeHash(blob)).Replace("-", "");
                JObject js = ScriptToJson(CcFormat.Decode(blob), false);
                JArray vars = js["variables"] as JArray;
                if (vars != null && vars.Count > 0)
                {
                    var vl = new List<string>();
                    foreach (JObject v in vars) vl.Add((string)v["name"] + (((string)v["value"] ?? "").Length > 0 ? " = \"" + (string)v["value"] + "\"" : ""));
                    lines.Add("Variablen: " + string.Join(", ", vl.ToArray()));
                }
                JArray steps = js["steps"] as JArray;
                o["topSteps"] = steps != null ? steps.Count : 0;
                o["totalSteps"] = js["totalSteps"];
                if (steps != null) RenderSteps(steps, "", 0, lines);
            }
            if (repoVer > 0)
            {
                var fq = c.CreateCommand();
                fq.CommandText = "SELECT Path FROM SYS_SCRIPT_FILES WHERE ScriptID=@id AND FileRepoVersion=@rv AND IsFolder=0 ORDER BY Path";
                fq.Parameters.AddWithValue("@id", id); fq.Parameters.AddWithValue("@rv", repoVer);
                var fl = new List<string>();
                using (var r = fq.ExecuteReader()) while (r.Read()) fl.Add(r.GetString(0));
                if (fl.Count > 0) lines.Add("Dateien im Command: " + string.Join(", ", fl.ToArray()));
            }
            const int maxLines = 400;
            if (lines.Count > maxLines) { int rest = lines.Count - maxLines; lines.RemoveRange(maxLines, rest); lines.Add("... (" + rest + " weitere Zeilen, vollstaendig mit acmp_cc_decode)"); }
            o["lines"] = new JArray(lines.ToArray());
            return o;
        }
    }

    // Lesbare Zeilen: Nummer, Beschreibung, Klasse, gesetzte Felder (leere weggelassen); Kinder eingerueckt (1.1, 1.2 ...).
    static void RenderSteps(JArray steps, string prefix, int depth, List<string> lines)
    {
        int i = 0;
        string ind = new string(' ', depth * 4);
        foreach (JObject s in steps)
        {
            string num = prefix + (++i);
            lines.Add(ind + num + ". " + (string)s["description"] + "  [" + (string)s["class"] + "]");
            JObject f = s["fields"] as JObject;
            if (f != null)
                foreach (JProperty p in f.Properties())
                {
                    if (p.Name == "_DataVersion") continue;
                    if (p.Value.Type == JTokenType.Array)
                    {
                        JArray a = (JArray)p.Value; if (a.Count == 0) continue;
                        lines.Add(ind + "      " + p.Name + ":");
                        foreach (JToken l in a) lines.Add(ind + "        | " + (string)l);
                    }
                    else if (p.Value.Type == JTokenType.Object)
                        lines.Add(ind + "      " + p.Name + " = (XML, " + ((string)p.Value["innerXml"] ?? "").Length + " Zeichen)");
                    else
                    {
                        string v = (string)p.Value;
                        if (string.IsNullOrEmpty(v)) continue;
                        if (v.Length > 300) v = v.Substring(0, 297) + "...";
                        lines.Add(ind + "      " + p.Name + " = " + v);
                    }
                }
            JArray ch = s["children"] as JArray;
            if (ch != null) RenderSteps(ch, num + ".", depth + 1, lines);
        }
    }

    static JObject ScriptToJson(CcScript s, bool fullXml)
    {
        var o = new JObject();
        var vars = new JArray();
        foreach (var v in s.Vars) { var j = new JObject(); j["name"] = v.Name; j["value"] = v.Value; if (v.ReadOnly) j["readOnly"] = true; if (v.Origin != 0) j["origin"] = (int)v.Origin; vars.Add(j); }
        o["variables"] = vars;
        var steps = new JArray();
        foreach (var n in s.Roots) steps.Add(NodeToJson(n, fullXml));
        o["steps"] = steps;
        o["totalSteps"] = s.TotalSteps;
        if (s.Format != 13 || s.TreeVersion != 3) o["format"] = "Altformat " + s.Format + " / VirtualTreeView " + s.TreeVersion + " (nur lesbar)";
        o["changeLogEntries"] = s.ChangeLog != null && s.ChangeLog.Length >= 4 ? BitConverter.ToInt32(s.ChangeLog, 0) : 0;
        if (s.Head1Raw != null || s.Head2Raw != null || s.Head6Raw != null)
        {
            // meist leer; Block 1 = Namensliste (vermutlich beobachtete Variablen des Debuggers), wird beim Schreiben unveraendert uebernommen
            var h = new JObject();
            if (s.Head1Raw != null) h["1"] = CcFormat.HeadText(s.Head1Raw);
            if (s.Head2Raw != null) h["2"] = CcFormat.HeadText(s.Head2Raw);
            if (s.Head6Raw != null) h["6"] = CcFormat.HeadText(s.Head6Raw);
            o["kopfbloecke"] = h;
        }
        if (s.Warnings.Count > 0) o["warnings"] = new JArray(s.Warnings.ToArray());
        return o;
    }

    static JObject NodeToJson(CcNode n, bool fullXml)
    {
        var o = new JObject();
        o["class"] = n.Class;
        var doc = new XmlDocument();
        doc.LoadXml(n.Xml);
        MaskSecrets(doc.DocumentElement);   // gespeicherte Kennwoerter (z.B. RunAsPassword) nie ausgeben
        var fields = new JObject();
        foreach (XmlNode e in doc.DocumentElement.ChildNodes)
        {
            if (e.NodeType != XmlNodeType.Element) continue;
            if (e.Name == "Description") { o["description"] = e.InnerText; continue; }
            if (e.Name == "ID") { o["id"] = e.InnerText; continue; }
            string def = CommonDefault(e.Name);
            JToken val = ElementValue(e);
            if (def != null && val.Type == JTokenType.String && (string)val == def) continue;   // Standardwerte weglassen
            fields[e.Name] = val;
        }
        o["fields"] = fields;
        if (n.Children.Count > 0)
        {
            var ch = new JArray();
            foreach (var k in n.Children) ch.Add(NodeToJson(k, fullXml));
            o["children"] = ch;
        }
        if (fullXml) o["xml"] = doc.DocumentElement.OuterXml;
        return o;
    }

    static JToken ElementValue(XmlNode e)
    {
        bool hasElem = false;
        foreach (XmlNode k in e.ChildNodes) if (k.NodeType == XmlNodeType.Element) { hasElem = true; break; }
        if (!hasElem) return new JValue(e.InnerText);
        bool allString = true;
        foreach (XmlNode k in e.ChildNodes) if (k.NodeType == XmlNodeType.Element && k.Name != "string") { allString = false; break; }
        if (allString) { var a = new JArray(); foreach (XmlNode k in e.ChildNodes) if (k.NodeType == XmlNodeType.Element) a.Add(k.InnerText); return a; }
        var x = new JObject(); x["innerXml"] = e.InnerXml; return x;
    }

    static string CommonDefault(string name)
    {
        foreach (var f in CommonFields) if (f[0] == name) return f[1];
        return null;
    }

    // ================= Katalog =================
    // Geht die neueste Version aller Client Commands durch (Client- und Console-Skript) und fasst je Schrittklasse
    // zusammen: Haeufigkeit, Felder mit Beispielwerten, Beispiel-Beschreibungen. Grundlage fuer neue Schritttypen
    // im Writer und fuer automatisch erzeugte Beschreibungen. Werte gekuerzt, Kennwort-Felder maskiert, Skriptzeilen nur gezaehlt.

    class CatalogClass
    {
        public string Name;
        public int Steps, Client, Console;
        public HashSet<string> Commands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, CatalogField> Fields = new Dictionary<string, CatalogField>();
        public List<JObject> Examples = new List<JObject>();
    }
    class CatalogField { public int Count; public List<string> Samples = new List<string>(); }

    public JObject Catalog(string classFilter, int examples)
    {
        if (string.IsNullOrEmpty(_readCs)) throw new Exception("DB-Lesezugang (db.connectionString) fehlt.");
        bool detail = !string.IsNullOrEmpty(classFilter);
        if (examples <= 0) examples = detail ? 5 : 1;
        if (examples > 20) examples = 20;
        var stats = new Dictionary<string, CatalogClass>(StringComparer.Ordinal);
        var errors = new JArray();
        int commands = 0, steps = 0;
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            var cmd = c.CreateCommand();
            cmd.CommandTimeout = 120;
            cmd.CommandText = "SELECT SCRIPTID, DESCRIPTION, CLIENTSCRIPT, CONSOLESCRIPT FROM (" +
                "SELECT SCRIPTID, DESCRIPTION, CLIENTSCRIPT, CONSOLESCRIPT, ROW_NUMBER() OVER (PARTITION BY SCRIPTID ORDER BY ScriptVersion DESC) AS rn " +
                "FROM SYS_SCRIPTS) x WHERE rn = 1";
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    commands++;
                    string cc = r.IsDBNull(1) ? r.GetString(0) : r.GetString(1);
                    for (int col = 2; col <= 3; col++)
                    {
                        if (r.IsDBNull(col)) continue;
                        CcScript s;
                        try { s = CcFormat.Decode((byte[])r.GetValue(col)); }
                        catch (Exception ex) { errors.Add(cc + " (" + (col == 2 ? "Client" : "Console") + "): " + ex.Message); continue; }
                        steps += CatalogNodes(s.Roots, cc, col == 2, stats, examples);
                    }
                }
        }

        var list = new List<CatalogClass>(stats.Values);
        if (detail) list = list.FindAll(k => k.Name.IndexOf(classFilter, StringComparison.OrdinalIgnoreCase) >= 0);
        list.Sort((x, y) => y.Steps != x.Steps ? y.Steps.CompareTo(x.Steps) : string.CompareOrdinal(x.Name, y.Name));

        var o = new JObject();
        o["commands"] = commands; o["steps"] = steps; o["classes"] = stats.Count;
        if (detail) o["filter"] = classFilter;
        var arr = new JArray();
        foreach (var k in list)
        {
            var j = new JObject();
            j["class"] = k.Name; j["steps"] = k.Steps; j["commands"] = k.Commands.Count;
            if (k.Console > 0) j["inConsoleScript"] = k.Console;
            var fl = new List<KeyValuePair<string, CatalogField>>(k.Fields);
            fl.Sort((x, y) => y.Value.Count != x.Value.Count ? y.Value.Count.CompareTo(x.Value.Count) : string.CompareOrdinal(x.Key, y.Key));
            if (detail)
            {
                var names = new List<string>(k.Commands); names.Sort(StringComparer.OrdinalIgnoreCase);
                j["commandNames"] = new JArray(names.GetRange(0, Math.Min(15, names.Count)).ToArray());
                var fo = new JObject();
                foreach (var f in fl) { var fj = new JObject(); fj["count"] = f.Value.Count; fj["samples"] = new JArray(f.Value.Samples.ToArray()); fo[f.Key] = fj; }
                j["fields"] = fo;
                j["examples"] = new JArray(k.Examples.ToArray());
            }
            else
            {
                var fn = new JArray(); foreach (var f in fl) fn.Add(f.Key);
                j["fields"] = fn;
                if (k.Examples.Count > 0) j["example"] = k.Examples[0]["description"];
            }
            arr.Add(j);
        }
        o["stepClasses"] = arr;
        if (errors.Count > 0) o["decodeErrors"] = errors;
        return o;
    }

    static int CatalogNodes(List<CcNode> nodes, string cc, bool client, Dictionary<string, CatalogClass> stats, int examples)
    {
        int n = 0;
        foreach (var node in nodes)
        {
            n++;
            CatalogClass k;
            if (!stats.TryGetValue(node.Class, out k)) { k = new CatalogClass(); k.Name = node.Class; stats[node.Class] = k; }
            k.Steps++; if (client) k.Client++; else k.Console++;
            bool newCommand = k.Commands.Add(cc);
            string desc = "";
            var exFields = new JObject();
            try
            {
                var doc = new XmlDocument(); doc.LoadXml(node.Xml);
                foreach (XmlNode e in doc.DocumentElement.ChildNodes)
                {
                    if (e.NodeType != XmlNodeType.Element || e.Name == "ID") continue;
                    if (e.Name == "Description") { desc = e.InnerText; continue; }
                    string def = CommonDefault(e.Name);
                    if (def != null && !HasElementChild(e) && e.InnerText == def) continue;   // Standardwerte weglassen
                    string sample = CatalogValue(e);
                    CatalogField f;
                    if (!k.Fields.TryGetValue(e.Name, out f)) { f = new CatalogField(); k.Fields[e.Name] = f; }
                    f.Count++;
                    if (f.Samples.Count < 3 && !f.Samples.Contains(sample)) f.Samples.Add(sample);
                    exFields[e.Name] = sample;
                }
            }
            catch (Exception) { desc = "(Schritt-XML nicht lesbar)"; }
            if (newCommand && k.Examples.Count < examples)
            {
                var ex = new JObject(); ex["command"] = cc; ex["description"] = desc; ex["fields"] = exFields;
                k.Examples.Add(ex);
            }
            n += CatalogNodes(node.Children, cc, client, stats, examples);
        }
        return n;
    }

    static bool HasElementChild(XmlNode e)
    {
        foreach (XmlNode k in e.ChildNodes) if (k.NodeType == XmlNodeType.Element) return true;
        return false;
    }

    static readonly System.Text.RegularExpressions.Regex SecretField =
        new System.Text.RegularExpressions.Regex("pass|kennw|pwd|secret|token|credential|apikey", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // Decode-Ansicht: Kennwort-Felder eines Schritts durch *** ersetzen (Feld bleibt sichtbar, Wert nicht).
    static void MaskSecrets(XmlElement step)
    {
        foreach (XmlNode e in step.ChildNodes)
            if (e.NodeType == XmlNodeType.Element && !HasElementChild(e) && e.InnerText.Length > 0 && SecretField.IsMatch(e.Name))
                e.InnerText = "***";
    }

    // Writer: den Platzhalter *** aus der Decode-Ansicht nie als Kennwort speichern.
    static void RejectMaskedSecret(string cls, string name, string value)
    {
        if (value == "***" && SecretField.IsMatch(name))
            throw new Exception("Schritt " + cls + ", Feld " + name + ": '***' ist der maskierte Platzhalter aus acmp_cc_decode, kein Kennwort. Feld leer lassen und das Kennwort in der ACMP-Console eintragen.");
    }

    // Feldwert fuer den Katalog: Listen/XML nur zaehlen, Kennwort-Felder maskieren, lange Texte kuerzen.
    static string CatalogValue(XmlNode e)
    {
        if (HasElementChild(e))
        {
            int lines = 0; bool allString = true;
            foreach (XmlNode k in e.ChildNodes) if (k.NodeType == XmlNodeType.Element) { lines++; if (k.Name != "string") allString = false; }
            return allString ? "[" + lines + " Zeilen]" : "[XML, " + e.InnerXml.Length + " Zeichen]";
        }
        string v = e.InnerText;
        if (v.Length > 0 && SecretField.IsMatch(e.Name)) return "***";
        v = v.Replace("\r\n", " / ").Replace("\n", " / ");
        return v.Length > 80 ? v.Substring(0, 80) + "…" : v;
    }

    // ================= Aufbau aus JSON =================

    // steps: [{ class, description, fields:{...}, children:[...] }]  bzw. { class, xml } fuer rohes Schritt-XML
    public CcScript BuildScript(JArray steps, JArray variables, out List<string> notes)
    {
        return BuildScript(steps, variables, out notes, null);
    }

    // ctx (nur acmp_cc_new_version): Schritt-IDs behalten, '***' in Kennwortfeldern aus der Basisversion uebernehmen
    public CcScript BuildScript(JArray steps, JArray variables, out List<string> notes, CcBuildCtx ctx)
    {
        notes = new List<string>();
        var s = new CcScript(); s.Kind = 1;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (variables != null)
            foreach (JToken vt in variables)
            {
                var v = new CcVar();
                if (vt.Type == JTokenType.String) v.Name = (string)vt;
                else
                {
                    var vo = (JObject)vt; v.Name = (string)vo["name"]; v.Value = vo["value"] != null ? vo["value"].ToString() : ""; v.ReadOnly = vo["readOnly"] != null && (bool)vo["readOnly"];
                    if (vo["origin"] != null && vo["origin"].Type == JTokenType.Integer) v.Origin = (byte)(int)vo["origin"];   // wie acmp_cc_decode es ausgibt
                }
                if (string.IsNullOrEmpty(v.Name)) throw new Exception("Variable ohne Namen.");
                if (!names.Add(v.Name)) throw new Exception("Variable doppelt: " + v.Name);
                s.Vars.Add(v);
            }
        if (steps == null || steps.Count == 0) throw new Exception("steps fehlt oder ist leer.");
        foreach (JToken t in steps)
        {
            if (!(t is JObject)) throw new Exception("Schritt ist kein Objekt: " + t.ToString(Formatting.None));
            s.Roots.Add(BuildNode((JObject)t, notes, ctx));
        }
        // Variablen, in die ein Schritt schreibt, muessen als Projektvariable existieren (die Console legt sie ebenso an).
        var used = new List<string>(); CollectSetVars(s.Roots, used);
        foreach (var u in used)
            if (names.Add(u)) { var v = new CcVar(); v.Name = u; s.Vars.Add(v); notes.Add("Projektvariable '" + u + "' automatisch ergaenzt (von einem Schritt gesetzt)."); }
        // Die Console speichert die Variablenliste alphabetisch sortiert.
        s.Vars.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return s;
    }

    // Felder, in die ein Schritt sein Ergebnis schreibt (Variablenname ohne %). Bestaetigt per Console-Speichern:
    // TSetProjectVar, TExecuteCommand (13.10.2026); uebrige nach Feldname und Beispielwerten aus acmp_cc_catalog.
    static readonly Dictionary<string, string[]> TargetVarFields = new Dictionary<string, string[]> {
        { "TSetProjectVar", new[] { "Variable_name" } },
        { "TSetProjectVarFromRegistry", new[] { "Variable_name" } },
        { "TSetProjectVarFromInifile", new[] { "Variable_name" } },
        { "TSetProjectVarFromEnvString", new[] { "Variable_name" } },
        { "TExecuteCommand", new[] { "Target_variable", "ExitCode" } },
        { "TExecuteBatchCommand", new[] { "ExitCode", "OutputVar" } },
        { "TExecutePowerShell", new[] { "ExitCode", "OutputLines" } },
        { "TControlNTService", new[] { "Variable_name" } },
        { "TWMIQuery", new[] { "Variable_name" } },
        { "TMessageDialog", new[] { "Variable_name" } },
        { "TIterateSelectedClients", new[] { "Target_variable" } },
        { "TIterateVariableProperties", new[] { "Target_name_variable", "Target_value_variable" } },
        { "TFileIteratorDir", new[] { "Variable_name" } },
        { "TReplaceStringCommand", new[] { "Variable_Name" } },
        { "TTrimStringCommand", new[] { "Variable_Name" } },
        { "TCutStringCommand", new[] { "Variable_name" } },
        { "TCutFilenameCommand", new[] { "Variable_name" } },
        { "TSplitStringCommand", new[] { "ResultVariable" } },
        { "TMathFunctionAddition", new[] { "ResultVariable" } },
        { "TMathFunctionDivision", new[] { "ResultVariable" } },
        { "TMathFunctionMultiplication", new[] { "ResultVariable" } },
        { "TMathFunctionRound", new[] { "ResultVariable" } },
        { "TStringListCommand", new[] { "Variable_To" } },
        { "TGetDateTime", new[] { "Variable_name" } },
        { "TExecuteQuery", new[] { "Variable_Name" } },
        { "TPing", new[] { "VarName" } },
        { "TLDAPSearch", new[] { "VarName", "CountName" } },
        { "TIterateIPRanges", new[] { "Variable_name" } } };

    // Standardbeschreibungen der deutschen ACMP-Console (ACMP 6.9.3): Texte aus den String-Tabellen
    // Sys\ScripterCommands\*.DE, Zuordnung Klasse -> Text ueber Beispiele in der DB bzw. die Bedeutung.
    // Die Console vergibt die Beschreibung beim Anlegen und erzeugt sie spaeter NICHT neu.
    static readonly Dictionary<string, string> DefaultDescriptions = new Dictionary<string, string> {
        { "TActionIf", "If .. Then" }, { "TActionElse", "Else" }, { "TActionExitScript", "Skript beenden" },
        { "TActionGroup", "Action group" }, { "TComment", "Kommentar" },
        { "TSetProjectVarFromRegistry", "Variable aus Registry setzen" }, { "TSetProjectVarFromEnvString", "Umgebungsvariable auslesen" },
        { "TModifyReg", "Registrywerte bearbeiten" }, { "TModifyIni", "INI-Datei bearbeiten" },
        { "TIfRegKeyExists", "Wenn Registryschlüssel (nicht) existiert" }, { "TIfRegValueNameExists", "Wenn Registry Wertename (nicht) existiert" },
        { "TExecuteCommand", "Befehl ausführen" }, { "TExecuteBatchCommand", "Batchdatei ausführen" },
        { "TExecutePowerShell", "Powershell-Script ausführen" }, { "TAddSharedPrinter", "Freigegebenen Drucker hinzufügen" },
        { "TControlNTService", "NT Dienst steuern" }, { "TWMIQuery", "WMI Abfrage" }, { "TEnqueueShutDown", "Herunterfahren einreihen" },
        { "TDownloadFile", "Datei herunterladen" }, { "TUploadFile", "Datei hochladen" },
        { "TDeleteFilesCommand", "Datei(en) löschen" }, { "TCopySingleFile", "Einzelne Datei kopieren" }, { "TCopyFileList", "Dateiliste kopieren" },
        { "TCreateLink", "Verknüpfung erstellen" }, { "TRenameFile", "Datei umbenennen" }, { "TRenameFolder", "Ordner umbenennen" },
        { "TMakeDir", "Ordner erstellen" }, { "TDeleteDir", "Ordner löschen" }, { "TCopyFolder", "Verzeichnis kopieren" },
        { "TIfFileExists", "Wenn Datei (nicht) existiert" }, { "TIfDirectoryExists", "Wenn Ordner (nicht) existiert" },
        { "TIsSoftwareInstalledCommand", "Wenn Software (nicht) installiert ist" }, { "TisOPSystem", "Betriebssystem ist (nicht)" },
        { "TIsOsArchitecture", "Wenn Betriebssystem Architektur ist" },
        { "TShowProgressForm", "Fortschrittsfenster zeigen" }, { "THideProgressForm", "Fortschrittsfenster verstecken" },
        { "TSetProgressFormProperties", "Fortschrittsfenster-Eigenschaften setzen" }, { "TWriteToLog", "Log schreiben" },
        { "TWait", "Warte" }, { "TGetDateTime", "Datum/Zeit ermitteln" }, { "TMessageDialog", "Dialoge" },
        { "TIterateVariableProperties", "Variableneigenschaften durchlaufen" }, { "TIncVarCommand", "Variable erhöhen" },
        { "TReplaceStringCommand", "Text ersetzen" },
        { "TMathFunctionAddition", "Addition" }, { "TMathFunctionDivision", "Division" }, { "TMathFunctionMultiplication", "Multiplikation" },
        { "TMathFunctionRound", "Runden" }, { "TMathOperationFloatToDateTime", "Gleitkommazahl zu Zeitformat" },
        { "TInstallMsiPackage", "MSI Paket installieren" }, { "TUninstallMSIProduct", "MSI Paket deinstallieren" },
        { "TUpdateWindowsCommand", "Windows Updates installieren" }, { "TInstallFontsCommand", "Schriftart installieren" },
        { "TExtractZipArchive", "ZIP-Archiv entpacken" }, { "TCreateZipArchiveCommand", "ZIP-Archiv erstellen" },
        { "TSetNTFSFolderPermissions", "NTFS Ordnerberechtigungen setzen" },
        { "TEnqueueClientTask", "Geplante Aufgabe ausführen" }, { "TRunClientCommand", "Client Command starten" },
        { "TIterateSelectedClients", "Ausgewählte Clients durchlaufen" }, { "TSetSoftwareInstalled", "Software als installiert kennzeichnen (Client)" },
        { "TStoreValueOnACMPServer", "Wert auf Server speichern" }, { "TUndoScript", "Skript Informationen zurücksetzen" },
        { "TExecuteQuery", "SQL-Abfrage" }, { "TSendSMTPMail", "SMTP Mail senden" }, { "THTTPGet", "HTTP get" },
        { "TPing", "Ping" }, { "TWakeOnLAN", "Wake-on-LAN" }, { "TLDAPSearch", "LDAP-Verzeichnis durchsuchen" },
        { "TDelphiScript", "Delphi script" } };

    // Beschreibung, wie die Console sie beim Anlegen vergibt; null = unbekannt.
    // Dynamisch nur, wo das Muster in der DB belegt ist (TSetProjectVar einzeilig, Case, Switch).
    static string DefaultDescription(string cls, JObject f)
    {
        if (cls == "TSetProjectVar")
            return FieldText(f, "Content_option") == "MultipleLines"
                ? "Projektvariable setzen " + FieldText(f, "Variable_name")
                : "Projektvariable setzen " + FieldText(f, "Variable_name") + " zu \"" + FieldText(f, "Content") + "\"";
        if (cls == "TActionCase") return "Case (" + FieldText(f, "_CaseValue") + ")";
        if (cls == "TActionSwitch") return "Switch (" + FieldText(f, "_SwitchValue") + ")";
        string d;
        return DefaultDescriptions.TryGetValue(cls, out d) ? d : null;
    }

    static string FieldText(JObject f, string key)
    {
        JToken t = f[key];
        return t == null || t.Type == JTokenType.Null ? "" : t.ToString();
    }

    static readonly System.Text.RegularExpressions.Regex VarNameRx = new System.Text.RegularExpressions.Regex("^[A-Za-z_][A-Za-z0-9_]*$");

    static void CollectSetVars(List<CcNode> nodes, List<string> outList)
    {
        foreach (var n in nodes)
        {
            string[] fl;
            if (TargetVarFields.TryGetValue(n.Class, out fl))
            {
                var doc = new XmlDocument(); doc.LoadXml(n.Xml);
                foreach (var f in fl)
                {
                    var e = doc.DocumentElement[f];
                    string name = e != null ? e.InnerText.Trim() : "";
                    // nur einfache Namen; %AUSDRUECKE% oder leere Felder werden nicht als Variable angelegt
                    if (VarNameRx.IsMatch(name) && !outList.Contains(name)) outList.Add(name);
                }
            }
            CollectSetVars(n.Children, outList);
        }
    }

    CcNode BuildNode(JObject st, List<string> notes, CcBuildCtx ctx)
    {
        var n = new CcNode();
        n.Class = (string)st["class"];
        if (string.IsNullOrEmpty(n.Class) || !n.Class.StartsWith("T")) throw new Exception("Schritt ohne gueltige Klasse (z.B. TSetProjectVar): " + st.ToString(Formatting.None));
        string raw = st["xml"] != null ? (string)st["xml"] : null;
        if (!string.IsNullOrEmpty(raw))
        {
            // Rohes XML: neue Schritt-ID vergeben, damit keine IDs aus anderen Commands doppelt vorkommen (neue Version: ID behalten)
            var doc = new XmlDocument(); doc.LoadXml(raw);
            if (doc.DocumentElement.Name != n.Class) throw new Exception("XML-Wurzel " + doc.DocumentElement.Name + " passt nicht zu class " + n.Class);
            var idEl = doc.DocumentElement["ID"];
            string given = idEl != null ? idEl.InnerText : null;
            string sid = StepId(ctx, given, notes);
            var descEl = doc.DocumentElement["Description"];
            foreach (XmlNode e in doc.DocumentElement.ChildNodes)
                if (e.NodeType == XmlNodeType.Element && !HasElementChild(e))
                {
                    string nv = SecretOrReject(ctx, sid, given, n.Class, e.Name, e.InnerText, descEl != null ? descEl.InnerText : "", notes);
                    if (nv != e.InnerText) e.InnerText = nv;   // nur ersetzte Kennwoerter anfassen, sonst bleibt das XML wie geliefert
                }
            if (idEl != null) idEl.InnerText = sid;
            n.Xml = CcFormat.StepXml(doc.DocumentElement.OuterXml);
        }
        else
        {
            JObject fields = (JObject)(st["fields"] as JObject ?? new JObject()).DeepClone();
            string given = st["id"] != null && st["id"].Type == JTokenType.String ? (string)st["id"] : null;
            string sid = StepId(ctx, given, notes);
            string descGiven = st["description"] != null ? (string)st["description"] : "";
            foreach (JProperty p in new List<JProperty>(fields.Properties()))
                if (p.Value.Type == JTokenType.String)
                {
                    string ov = (string)p.Value, nv = SecretOrReject(ctx, sid, given, n.Class, p.Name, ov, descGiven, notes);
                    if (nv != ov) p.Value = new JValue(nv);
                }
            string desc = st["description"] != null ? (string)st["description"] : null;
            if (string.IsNullOrEmpty(desc))
            {
                desc = DefaultDescription(n.Class, fields);
                if (desc != null) notes.Add("Schritt " + n.Class + ": Beschreibung wie in der Console erzeugt - '" + desc + "'.");
                else { desc = n.Class.Substring(1); notes.Add("Schritt " + n.Class + ": keine description angegeben und keine Standardbeschreibung bekannt - '" + desc + "' verwendet."); }
            }
            var sb = new StringBuilder();
            sb.Append('<').Append(n.Class).Append('>');
            var done = new HashSet<string>();
            foreach (var cf in CommonFields)
            {
                string name = cf[0];
                JToken v = fields[name];
                if (name == "Description") AppendField(sb, name, new JValue(desc));
                else if (name == "ID") AppendField(sb, name, new JValue(sid));
                else AppendField(sb, name, v != null ? v : new JValue(cf[1]));
                done.Add(name);
            }
            foreach (JProperty p in fields.Properties())
                if (!done.Contains(p.Name)) AppendField(sb, p.Name, p.Value);
            sb.Append("</").Append(n.Class).Append('>');
            n.Xml = CcFormat.StepXml(sb.ToString());
            new XmlDocument().LoadXml(n.Xml);   // Wohlgeformtheit pruefen
        }
        JArray ch = st["children"] as JArray;
        if (ch != null)
            foreach (JToken k in ch)
            {
                if (!(k is JObject)) throw new Exception("Schritt ist kein Objekt: " + k.ToString(Formatting.None));
                n.Children.Add(BuildNode((JObject)k, notes, ctx));
            }
        return n;
    }

    // Schritt-ID: neu vergeben (Standard) bzw. bei neuen Versionen die angegebene behalten, solange sie gueltig und eindeutig ist
    static string StepId(CcBuildCtx ctx, string given, List<string> notes)
    {
        if (ctx == null || !ctx.KeepIds) return NewGuid();
        string g = GuidBraces(given);
        if (g != null && ctx.UsedIds.Add(g)) return g;
        if (g != null) notes.Add("Schritt-ID " + g + " kommt mehrfach vor - neue ID vergeben.");
        string n = NewGuid(); ctx.UsedIds.Add(n);
        return n;
    }

    // '***' in einem Kennwortfeld: bei neuen Versionen das gespeicherte Kennwort desselben Schritts (gleiche ID und Klasse)
    // aus der Basisversion nehmen, sonst ablehnen wie bisher. Der Wert erscheint nie in Hinweisen oder Vorschau.
    static string SecretOrReject(CcBuildCtx ctx, string sid, string given, string cls, string field, string value, string desc, List<string> notes)
    {
        if (value != "***" || !SecretField.IsMatch(field)) return value;
        string g = GuidBraces(given);
        KeyValuePair<string, Dictionary<string, string>> e; string stored;
        if (ctx != null && ctx.Secrets != null && g != null && g == sid && ctx.Secrets.TryGetValue(g, out e) && e.Key == cls &&
            e.Value.TryGetValue(field, out stored) && !string.IsNullOrEmpty(stored))
        {
            ctx.SecretsTaken++;
            notes.Add("Schritt \"" + desc + "\" [" + cls + "], Feld " + field + ": gespeichertes Kennwort aus " + (ctx.SecretSource ?? "der Basisversion") + " uebernommen (gleiche Schritt-ID).");
            return stored;
        }
        if (ctx != null && ctx.Secrets != null)
            throw new Exception("Schritt " + cls + " \"" + desc + "\", Feld " + field + ": '***' ist der maskierte Platzhalter aus acmp_cc_decode. Uebernommen wird ein gespeichertes Kennwort nur fuer einen Schritt mit derselben id und Klasse wie in der Basisversion - sonst Feld leer lassen und das Kennwort in der ACMP-Console eintragen.");
        RejectMaskedSecret(cls, field, value);
        return value;
    }

    static void AppendField(StringBuilder sb, string name, JToken v)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$")) throw new Exception("Ungueltiger Feldname: " + name);
        if (v == null || v.Type == JTokenType.Null || (v.Type == JTokenType.String && ((string)v).Length == 0)) { sb.Append('<').Append(name).Append("/>"); return; }
        if (v.Type == JTokenType.Array)
        {
            if (((JArray)v).Count == 0) { sb.Append('<').Append(name).Append("/>"); return; }
            sb.Append('<').Append(name).Append('>');
            foreach (JToken i in (JArray)v) sb.Append("<string>").Append(Esc(i.ToString())).Append("</string>");
            sb.Append("</").Append(name).Append('>');
            return;
        }
        if (v.Type == JTokenType.Object && v["innerXml"] != null)
        {
            string ix = (string)v["innerXml"];
            new XmlDocument().LoadXml("<x>" + ix + "</x>");
            sb.Append('<').Append(name).Append('>').Append(ix).Append("</").Append(name).Append('>');
            return;
        }
        string s = v.Type == JTokenType.Boolean ? ((bool)v ? "True" : "False") : v.ToString();
        sb.Append('<').Append(name).Append('>').Append(Esc(s)).Append("</").Append(name).Append('>');
    }

    static string Esc(string s) { return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;"); }

    // ================= Schreiben =================

    // Vor jeder Schreibaktion (und schon beim Planen): eingeschaltet, Antragsteller erlaubt,
    // Kompatibilitaetspruefung bestanden; bei Datei-Aktionen zusaetzlich Datei-Rechte und File Repository.
    public void CheckWrite(string requester, bool fileOp)
    {
        if (!ConfigOk) throw new Exception("CC-Schreiben ist ausgeschaltet: " + DisabledReason);
        if (requester == null || !_allowed.Contains(requester)) throw new Exception("Antragsteller '" + requester + "' darf keine Client Commands schreiben (ccWriter.allowedRequesters).");
        JObject pf = Preflight(false);
        if (pf == null || !(bool)pf["ok"]) throw new Exception("CC-Schreiben gesperrt - Kompatibilitaetspruefung nicht bestanden: " + (pf != null ? (string)pf["summary"] : "keine Pruefung") + " (Details: acmp_cc_writer_status). Nichts geschrieben.");
        if (fileOp && !(bool)pf["fileTools"]) throw new Exception("Datei-Tools gesperrt (Datei-Rechte oder File Repository fehlen, siehe acmp_cc_writer_status). Nichts geschrieben.");
    }
    void CheckWrite(string requester) { CheckWrite(requester, false); }

    // Lesbare Vorschau eines Skripts (gleiches Format wie die Anzeige freigegebener Commands)
    public JArray PreviewScript(List<string> head, CcScript s)
    {
        var lines = new List<string>(head);
        JObject js = ScriptToJson(s, false);
        JArray vars = js["variables"] as JArray;
        if (vars != null && vars.Count > 0)
        {
            var vl = new List<string>();
            foreach (JObject v in vars) vl.Add((string)v["name"] + (((string)v["value"] ?? "").Length > 0 ? " = \"" + (string)v["value"] + "\"" : ""));
            lines.Add("Variablen: " + string.Join(", ", vl.ToArray()));
        }
        RenderSteps((JArray)js["steps"], "", 0, lines);
        return new JArray(lines.ToArray());
    }

    // Vorschau aus Schritten im Decode-Format (z.B. dryRun von acmp_lib_install)
    public static JArray PreviewSteps(List<string> head, JArray steps)
    {
        var lines = new List<string>(head);
        if (steps != null) RenderSteps(steps, "", 0, lines);
        return new JArray(lines.ToArray());
    }

    // Wiederherstellen: Inhalt der Sicherung fuer die Vorschau
    public JArray PreviewRestore(string backupName)
    {
        if (string.IsNullOrEmpty(backupName) || backupName.IndexOfAny(new[] { '/', '\\' }) >= 0 || !backupName.EndsWith(".json"))
            throw new Exception("backup = Dateiname aus cc-backups (ohne Pfad), z.B. aus der Antwort von acmp_cc_update_draft.");
        string file = Path.Combine(_backupDir, backupName);
        if (!File.Exists(file)) throw new Exception("Sicherung nicht gefunden: " + backupName);
        JObject b = JObject.Parse(File.ReadAllText(file));
        CheckRestorable(b, backupName);
        var head = new List<string> { "Entwurf " + (string)b["scriptId"] + " v" + b["version"] + " auf die Sicherung " + backupName + " zuruecksetzen.",
                                      "Name danach: " + (string)b["DESCRIPTION"] + " | Inhalt danach:" };
        return PreviewScript(head, CcFormat.Decode(B64((string)b["CLIENTSCRIPT"])));
    }

    // Zuruecksetzen: Sicherungen eigener Entwuerfe (ohne kind, von acmp_cc_update_draft/restore_draft) und fremder Entwuerfe,
    // die acmp_cc_new_version mit replaceDraft ueberschrieben hat (kind "newversion-draft"). Andere Sicherungen gehoeren zu anderen Tools.
    void CheckRestorable(JObject b, string backupName)
    {
        string kind = b["kind"] != null && b["kind"].Type == JTokenType.String ? (string)b["kind"] : null;
        if (kind == "newversion-insert")
            throw new Exception(backupName + " gehoert zu einer neu angelegten Version (acmp_cc_new_version) - dabei wurde nichts ueberschrieben. " +
                "Die neue Version ist ein Entwurf: in der Console verwerfen oder mit acmp_cc_new_version erneut ueberschreiben.");
        if (kind != null && kind != "newversion-draft") throw new Exception(backupName + " ist keine Entwurfs-Sicherung (Art \"" + kind + "\") - dafuer gibt es das undo des jeweiligen Tools.");
        if (b["scriptId"] == null || b["version"] == null || b["CLIENTSCRIPT"] == null) throw new Exception(backupName + " ist keine Entwurfs-Sicherung.");
        if (kind == null && !IsRegistered((string)b["scriptId"])) throw new Exception("Nur vom MCP angelegte Entwuerfe duerfen zurueckgesetzt werden.");
    }

    // Datei anhaengen: vor der Freigabe pruefen und Pruefsumme festhalten (die Ausfuehrung vergleicht sie)
    public JObject PrepareAddFile(string scriptId, string source, string targetName, string requester)
    {
        CheckWrite(requester, true);
        string id = GuidBraces(scriptId);
        if (id == null) throw new Exception("scriptId ist keine GUID.");
        if (!IsRegistered(id)) throw new Exception("Dateien nur an vom MCP angelegte Entwuerfe anhaengen (" + id + " ist nicht in cc-drafts.json).");
        string src = CheckSource(source);
        var fi = new FileInfo(src);
        if (!fi.Exists) throw new Exception("Quelldatei nicht gefunden oder kein Lesezugriff fuer den Dienst (Computerkonto): " + src);
        if (fi.Length > _maxFileBytes) throw new Exception("Datei ist " + (fi.Length / 1048576) + " MB gross, erlaubt sind " + (_maxFileBytes / 1048576) + " MB (ccWriter.maxFileMB).");
        string name = string.IsNullOrEmpty(targetName) ? fi.Name : targetName.Trim();
        CheckFileName(name);
        string hash;
        using (var s = File.OpenRead(src)) using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "");
        var o = new JObject(); o["scriptId"] = id; o["source"] = src; o["name"] = name; o["bytes"] = fi.Length; o["sha256"] = hash;
        return o;
    }

    public JObject CreateDraft(string name, string folder, string longDesc, CcScript client, string requester, List<string> notes)
    {
        CheckWrite(requester);
        if (string.IsNullOrEmpty(name)) throw new Exception("name fehlt.");
        lock (_lock)
        using (var c = new SqlConnection(_writeCs))
        {
            c.Open();
            string folderId = ResolveFolder(c, folder);
            if (_allowedFolders.Count > 0 && !_allowedFolders.Contains(folderId)) throw new Exception("Ordner " + folderId + " ist nicht in ccWriter.allowedFolderIds.");
            var chk = c.CreateCommand();
            chk.CommandText = "SELECT TOP 1 CAST(HasConditions AS int) FROM SYS_SCRIPTS WHERE SCRIPTID=@t ORDER BY ScriptVersion DESC";
            chk.Parameters.AddWithValue("@t", _templateId);
            object hc = chk.ExecuteScalar();
            if (hc == null) throw new Exception("Vorlage " + _templateId + " nicht gefunden (ccWriter.templateScriptId).");
            if (Convert.ToInt32(hc) != 0) throw new Exception("Vorlage hat Bedingungen - als Vorlage nur einen Command ohne Bedingungen verwenden.");
            var dup = c.CreateCommand();
            dup.CommandText = "SELECT COUNT(*) FROM SYS_SCRIPTS WHERE DESCRIPTION=@n AND FOLDERID=@f";
            dup.Parameters.AddWithValue("@n", name); dup.Parameters.AddWithValue("@f", folderId);
            if (Convert.ToInt32(dup.ExecuteScalar()) > 0) throw new Exception("Im Ordner gibt es bereits einen Command '" + name + "'.");

            string id = NewGuid();
            client.Kind = 1;
            byte[] cb = CcFormat.Encode(client);
            var console = new CcScript(); console.Kind = 0; console.Vars = client.Vars;
            byte[] ob = CcFormat.Encode(console);
            CcFormat.Decode(cb); CcFormat.Decode(ob);   // Selbsttest vor dem Schreiben
            string who = "MCP " + requester;
            double now = DateTime.Now.ToOADate();
            int aclCount;

            using (var tx = c.BeginTransaction())
            {
                var ins = c.CreateCommand(); ins.Transaction = tx;
                ins.CommandText =
                    "INSERT INTO SYS_SCRIPTS (SCRIPTID, ScriptVersion, Predecessor, FileRepoVersion, MaxUsedFileRepoVersion, DESCRIPTION, LONGDESCRIPTION, PASSWORD, FOLDERID, " +
                    "CLIENTSCRIPT, CONSOLESCRIPT, CONDITIONS, ISQUERYSHORTCUT, ISACTBARSHORTCUT, DOSTORECLIENTLOG, DOSHOWCONSOLELOG, HASCONSOLESCRIPT, ABOUTINFOS, HASCLIENTSCRIPT, " +
                    "PRIORITY, VERSION, ICONID, ALLOWINTERACTIVEUSE, COMMANDENABLED, HasConditions, State, Creator, CreationDate, LastModifier, LastModifiedDate, FileRepoSize, " +
                    "IsFileRepoVersionIncreased, IsParallelCC, ConsoleRelations, ClientRelations, LicensedCommands) " +
                    "SELECT TOP 1 @id, 1, 0, 1, 1, @name, @ld, t.PASSWORD, @folder, @cs, @os, t.CONDITIONS, 0, 0, t.DOSTORECLIENTLOG, t.DOSHOWCONSOLELOG, 0, t.ABOUTINFOS, 1, " +
                    "t.PRIORITY, 0, t.ICONID, t.ALLOWINTERACTIVEUSE, t.COMMANDENABLED, 0, 1, @who, @now, @who, @now, 0, 1, 0, 0, 1, 0 " +
                    "FROM SYS_SCRIPTS t WHERE t.SCRIPTID=@tpl ORDER BY t.ScriptVersion DESC";
                ins.Parameters.AddWithValue("@id", id);
                ins.Parameters.AddWithValue("@name", name);
                ins.Parameters.AddWithValue("@ld", longDesc ?? "");
                ins.Parameters.AddWithValue("@folder", folderId);
                ins.Parameters.Add("@cs", SqlDbType.VarBinary, -1).Value = cb;
                ins.Parameters.Add("@os", SqlDbType.VarBinary, -1).Value = ob;
                ins.Parameters.AddWithValue("@who", who);
                ins.Parameters.AddWithValue("@now", now);
                ins.Parameters.AddWithValue("@tpl", _templateId);
                if (ins.ExecuteNonQuery() != 1) throw new Exception("INSERT hat keine Zeile angelegt.");
                InsertVars(c, tx, id, 1, client.Vars);
                aclCount = CopyFolderAcl(c, tx, id, folderId);
                if (aclCount == 0) throw new Exception("Ordner " + folderId + " hat keine eigenen Rechte-Eintraege (SYS_Folders_AclEntries_*) - der CC waere in der Console unsichtbar. Nichts geschrieben.");
                tx.Commit();
            }
            Register(id, name, requester);
            _log("CC-Entwurf angelegt: " + id + " '" + name + "' von " + requester);
            var o = new JObject();
            o["result"] = "Entwurf angelegt (State 1, nicht freigegeben). In der ACMP-Console pruefen und dort freigeben.";
            o["scriptId"] = id; o["name"] = name; o["folderId"] = folderId;
            o["clientScriptBytes"] = cb.Length; o["totalSteps"] = CountSteps(client.Roots);
            o["aclEntries"] = aclCount;
            if (notes.Count > 0) o["hinweise"] = new JArray(notes.ToArray());
            return o;
        }
    }

    // acmp_cc_update_draft wie acmp_cc_new_version (seit 22.10.2026; vorher wurden ohne 'variables' alle Projektvariablen geloescht und
    // Schritt-IDs immer neu vergeben): ohne 'variables' bleiben die Projektvariablen des Entwurfs samt Startwerten, Schritt-IDs des Entwurfs
    // bleiben (fremde IDs werden neu vergeben), '***' in Kennwortfeldern uebernimmt das gespeicherte Kennwort desselben Schritts.
    // Schreibt Schritte (Kurzform aufgeloest, IDs fest) und Variablen in 'a' zurueck: das sind die festgehaltenen Argumente,
    // die Ausfuehrung erzeugt daraus genau den Inhalt der Vorschau (_stepIdsFixed: IDs nicht noch einmal gegen den Entwurf pruefen).
    public CcScript BuildDraftUpdate(JObject a, out List<string> notes)
    {
        string id = GuidBraces(a["scriptId"] != null && a["scriptId"].Type == JTokenType.String ? (string)a["scriptId"] : null);
        if (id == null) throw new Exception("scriptId ist keine GUID.");
        if (!IsRegistered(id)) throw new Exception("Nur vom MCP angelegte Entwuerfe duerfen geaendert werden (" + id + " ist nicht in cc-drafts.json). Fuer eine neue Version eines vorhandenen (auch freigegebenen oder fremden) Commands: acmp_cc_new_version.");
        if (string.IsNullOrEmpty(_readCs)) throw new Exception("DB-Lesezugang (db.connectionString) fehlt.");
        JArray steps = a["steps"] as JArray;
        if (steps == null && a["steps"] != null && a["steps"].Type == JTokenType.String && ((string)a["steps"]).Trim().StartsWith("["))
            steps = JArray.Parse((string)a["steps"]);
        CcScript draft; int ver;
        var cfNotes = new List<string>();
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            List<CcVersionRow> rows = LoadVersions(c, null, id);
            if (rows.Count == 0) throw new Exception("Client Command " + id + " nicht gefunden.");
            if (rows[0].State != 1) throw new Exception("Command ist kein Entwurf mehr (State " + rows[0].State + ") - fuer Aenderungen eine neue Version anlegen: acmp_cc_new_version.");
            ver = rows[0].Version;
            try { draft = rows[0].Client != null ? CcFormat.Decode(rows[0].Client) : null; }
            catch (Exception ex) { throw new Exception("Client-Skript des Entwurfs v" + ver + " nicht lesbar: " + ex.Message + " - nichts geaendert."); }
            steps = ExpandCustomFieldSteps(c, steps, cfNotes);
        }
        CcScript s = ComposeDraftUpdate(a, steps, draft, ver, out notes);
        notes.InsertRange(0, cfNotes);
        return s;
    }

    // Kern von BuildDraftUpdate ohne DB (offline getestet in CfParamTest.cs): steps = Kurzform schon aufgeloest, draft = Client-Skript des Entwurfs
    internal CcScript ComposeDraftUpdate(JObject a, JArray steps, CcScript draft, int ver, out List<string> notes)
    {
        var draftIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (draft != null) CollectStepIds(draft.Roots, draftIds);
        bool fixedIds = a["_stepIdsFixed"] != null && a["_stepIdsFixed"].Type == JTokenType.Boolean && (bool)a["_stepIdsFixed"];
        JArray norm = NormalizeStepIds(steps, NewGuid, fixedIds ? null : draftIds);
        JArray vars = a["variables"] as JArray;
        bool varsFromDraft = vars == null;
        if (varsFromDraft) vars = draft != null ? VarsToJson(draft.Vars) : new JArray();
        var ctx = new CcBuildCtx();
        ctx.KeepIds = true;
        ctx.Secrets = CollectSecrets(draft);
        ctx.SecretSource = "dem Entwurf v" + ver;
        CcScript s = BuildScript(norm, vars, out notes, ctx);
        ApplyStoredHeader(s, norm);
        if (!fixedIds)
        {
            var newIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase); CollectStepIds(s.Roots, newIds);
            int kept = 0; foreach (string g in newIds) if (draftIds.Contains(g)) kept++;
            notes.Add("Schritt-IDs: " + kept + " von " + newIds.Count + " aus dem Entwurf v" + ver + " behalten" + (newIds.Count > kept ? ", " + (newIds.Count - kept) + " neu vergeben (neue Schritte oder ID nicht im Entwurf)" : "") + ".");
            if (varsFromDraft)
            {
                var names = new List<string>(); foreach (CcVar v in draft != null ? draft.Vars : new List<CcVar>()) names.Add(v.Name);
                notes.Add("Projektvariablen aus dem Entwurf v" + ver + " uebernommen (" + names.Count + (names.Count > 0 ? ": " + string.Join(", ", names.ToArray()) : "") + ").");
            }
        }
        a["steps"] = norm; a["variables"] = vars; a["_stepIdsFixed"] = true;
        return s;
    }

    static void CollectStepIds(List<CcNode> nodes, HashSet<string> ids)
    {
        foreach (CcNode n in nodes)
        {
            var doc = new XmlDocument(); doc.LoadXml(n.Xml);
            var idEl = doc.DocumentElement["ID"];
            string g = idEl != null ? GuidBraces(idEl.InnerText) : null;
            if (g != null) ids.Add(g);
            CollectStepIds(n.Children, ids);
        }
    }

    public JObject UpdateDraft(string scriptId, string newName, string longDesc, CcScript client, string requester, List<string> notes)
    {
        CheckWrite(requester);
        string id = GuidBraces(scriptId);
        if (id == null) throw new Exception("scriptId ist keine GUID.");
        if (!IsRegistered(id)) throw new Exception("Nur vom MCP angelegte Entwuerfe duerfen geaendert werden (" + id + " ist nicht in cc-drafts.json). Fuer eine neue Version eines vorhandenen (auch freigegebenen oder fremden) Commands: acmp_cc_new_version.");
        lock (_lock)
        using (var c = new SqlConnection(_writeCs))
        {
            c.Open();
            int ver; string backup = BackupDraft(c, id, out ver, "vor Aenderung");
            client.Kind = 1;
            byte[] cb = CcFormat.Encode(client);
            var console = new CcScript(); console.Kind = 0; console.Vars = client.Vars;
            byte[] ob = CcFormat.Encode(console);
            CcFormat.Decode(cb); CcFormat.Decode(ob);
            using (var tx = c.BeginTransaction())
            {
                var up = c.CreateCommand(); up.Transaction = tx;
                up.CommandText = "UPDATE SYS_SCRIPTS SET CLIENTSCRIPT=@cs, CONSOLESCRIPT=@os, LastModifier=@who, LastModifiedDate=@now" +
                    (newName != null ? ", DESCRIPTION=@name" : "") + (longDesc != null ? ", LONGDESCRIPTION=@ld" : "") +
                    " WHERE SCRIPTID=@id AND ScriptVersion=@v AND State=1";
                up.Parameters.Add("@cs", SqlDbType.VarBinary, -1).Value = cb;
                up.Parameters.Add("@os", SqlDbType.VarBinary, -1).Value = ob;
                up.Parameters.AddWithValue("@who", "MCP " + requester);
                up.Parameters.AddWithValue("@now", DateTime.Now.ToOADate());
                if (newName != null) up.Parameters.AddWithValue("@name", newName);
                if (longDesc != null) up.Parameters.AddWithValue("@ld", longDesc);
                up.Parameters.AddWithValue("@id", id); up.Parameters.AddWithValue("@v", ver);
                if (up.ExecuteNonQuery() != 1) throw new Exception("UPDATE traf keine Zeile (inzwischen freigegeben?). Nichts geaendert.");
                var del = c.CreateCommand(); del.Transaction = tx;
                del.CommandText = "DELETE FROM SYS_SCRIPTS_VARIABLES WHERE ScriptId=@id AND ScriptVersion=@v";
                del.Parameters.AddWithValue("@id", id); del.Parameters.AddWithValue("@v", ver);
                del.ExecuteNonQuery();
                InsertVars(c, tx, id, ver, client.Vars);
                tx.Commit();
            }
            _log("CC-Entwurf geaendert: " + id + " v" + ver + " von " + requester + " (Sicherung " + Path.GetFileName(backup) + ")");
            var o = new JObject();
            o["result"] = "Entwurf geaendert."; o["scriptId"] = id; o["version"] = ver;
            o["backup"] = Path.GetFileName(backup); o["clientScriptBytes"] = cb.Length; o["totalSteps"] = CountSteps(client.Roots);
            if (notes.Count > 0) o["hinweise"] = new JArray(notes.ToArray());
            return o;
        }
    }

    public JObject RestoreDraft(string backupName, string requester)
    {
        CheckWrite(requester);
        if (string.IsNullOrEmpty(backupName) || backupName.IndexOfAny(new[] { '/', '\\' }) >= 0 || !backupName.EndsWith(".json"))
            throw new Exception("backup = Dateiname aus cc-backups (ohne Pfad), z.B. aus der Antwort von acmp_cc_update_draft.");
        string file = Path.Combine(_backupDir, backupName);
        if (!File.Exists(file)) throw new Exception("Sicherung nicht gefunden: " + backupName);
        JObject b = JObject.Parse(File.ReadAllText(file));
        CheckRestorable(b, backupName);
        string id = (string)b["scriptId"]; int ver = (int)b["version"];
        lock (_lock)
        using (var c = new SqlConnection(_writeCs))
        {
            c.Open();
            int curVer; string pre = BackupDraft(c, id, out curVer, "vor Wiederherstellung von " + backupName);
            if (curVer != ver) throw new Exception("Version hat sich geaendert (" + ver + " -> " + curVer + "), Wiederherstellung abgebrochen.");
            using (var tx = c.BeginTransaction())
            {
                var up = c.CreateCommand(); up.Transaction = tx;
                up.CommandText = "UPDATE SYS_SCRIPTS SET DESCRIPTION=@name, LONGDESCRIPTION=@ld, FOLDERID=@f, CLIENTSCRIPT=@cs, CONSOLESCRIPT=@os, LastModifier=@lm, LastModifiedDate=@ld2 " +
                    "WHERE SCRIPTID=@id AND ScriptVersion=@v AND State=1";
                up.Parameters.AddWithValue("@name", (string)b["DESCRIPTION"] ?? "");
                up.Parameters.AddWithValue("@ld", (string)b["LONGDESCRIPTION"] ?? "");
                up.Parameters.AddWithValue("@f", (string)b["FOLDERID"] ?? "");
                up.Parameters.Add("@cs", SqlDbType.VarBinary, -1).Value = B64((string)b["CLIENTSCRIPT"]);
                up.Parameters.Add("@os", SqlDbType.VarBinary, -1).Value = B64((string)b["CONSOLESCRIPT"]);
                up.Parameters.AddWithValue("@lm", (object)(string)b["LastModifier"] ?? DBNull.Value);
                up.Parameters.AddWithValue("@ld2", b["LastModifiedDate"] != null && b["LastModifiedDate"].Type != JTokenType.Null ? (object)(double)b["LastModifiedDate"] : DBNull.Value);
                up.Parameters.AddWithValue("@id", id); up.Parameters.AddWithValue("@v", ver);
                if (up.ExecuteNonQuery() != 1) throw new Exception("UPDATE traf keine Zeile (inzwischen freigegeben?). Nichts geaendert.");
                var del = c.CreateCommand(); del.Transaction = tx;
                del.CommandText = "DELETE FROM SYS_SCRIPTS_VARIABLES WHERE ScriptId=@id AND ScriptVersion=@v";
                del.Parameters.AddWithValue("@id", id); del.Parameters.AddWithValue("@v", ver);
                del.ExecuteNonQuery();
                var vars = new List<CcVar>();
                foreach (JObject vj in (JArray)b["variables"]) { var v = new CcVar(); v.Name = (string)vj["Name"]; v.Origin = (byte)(int)vj["Origin"]; v.ReadOnly = (bool)vj["ReadOnly"]; vars.Add(v); }
                InsertVars(c, tx, id, ver, vars);
                tx.Commit();
            }
            _log("CC-Entwurf wiederhergestellt: " + id + " aus " + backupName + " von " + requester);
            var o = new JObject(); o["result"] = "Entwurf aus Sicherung wiederhergestellt."; o["scriptId"] = id; o["backupVorher"] = Path.GetFileName(pre);
            return o;
        }
    }

    // ================= Dateien (File Repository, Ordner "private") =================
    // Wie die Console (beobachtet 13.10.2026): Datei liegt unter <fileRepositoryRoot>\{ScriptId}\<FileRepoVersion>\<Name>,
    // dazu eine Zeile in SYS_SCRIPT_FILES mit SHA-256 des Inhalts. Im Schritt TDownloadFile steht "private\<Name>".

    public JObject AddFile(string scriptId, string source, string targetName, string requester)
    {
        return AddFile(scriptId, source, targetName, requester, null);
    }

    // expectSha256: bei der Freigabe festgehaltene Pruefsumme; weicht die Datei ab, wird nichts angehaengt
    public JObject AddFile(string scriptId, string source, string targetName, string requester, string expectSha256)
    {
        CheckWrite(requester, true);
        return Attach(scriptId, CheckSource(source), targetName, requester, expectSha256);
    }

    // Nur fuer AcmpCtl: Datei aus dem CTL-Zwischenspeicher. Herkunft (Hersteller-URL bzw. Vorlagen-Ordner) und SHA-256
    // hat AcmpCtl geprueft; die Kopie im File Repository wird hier nochmals gegen die Pruefsumme verglichen.
    public JObject AddFileTrusted(string scriptId, string localFile, string targetName, string requester, string expectSha256)
    {
        CheckWrite(requester, true);
        if (string.IsNullOrEmpty(expectSha256)) throw new Exception("SHA-256 fehlt - Dateien aus Vorlagen nur mit Pruefsumme anhaengen.");
        if (string.IsNullOrEmpty(localFile) || !Path.IsPathRooted(localFile)) throw new Exception("Pfad der Datei im CTL-Zwischenspeicher fehlt.");
        return Attach(scriptId, localFile, targetName, requester, expectSha256);
    }

    public long MaxFileBytes { get { return _maxFileBytes; } }

    JObject Attach(string scriptId, string src, string targetName, string requester, string expectSha256)
    {
        string id = GuidBraces(scriptId);
        if (id == null) throw new Exception("scriptId ist keine GUID.");
        if (!IsRegistered(id)) throw new Exception("Dateien nur an vom MCP angelegte Entwuerfe anhaengen (" + id + " ist nicht in cc-drafts.json).");
        if (!string.IsNullOrEmpty(targetName)) CheckFileName(targetName.Trim());
        var fi = new FileInfo(src);
        if (!fi.Exists) throw new Exception("Quelldatei nicht gefunden oder kein Lesezugriff fuer den Dienst (Computerkonto): " + src);
        if (fi.Length > _maxFileBytes) throw new Exception("Datei ist " + (fi.Length / 1048576) + " MB gross, erlaubt sind " + (_maxFileBytes / 1048576) + " MB (ccWriter.maxFileMB).");
        string name = string.IsNullOrEmpty(targetName) ? fi.Name : targetName.Trim();
        CheckFileName(name);
        if (!Directory.Exists(_fileRepoRoot)) throw new Exception("File Repository nicht gefunden: " + _fileRepoRoot + " (ccWriter.fileRepositoryRoot).");
        lock (_lock)
        using (var c = new SqlConnection(_writeCs))
        {
            c.Open();
            int ver, repoVer; DraftRepo(c, id, out ver, out repoVer);
            var dup = c.CreateCommand();
            dup.CommandText = "SELECT COUNT(*) FROM SYS_SCRIPT_FILES WHERE ScriptID=@id AND FileRepoVersion=@rv AND Path=@p";
            dup.Parameters.AddWithValue("@id", id); dup.Parameters.AddWithValue("@rv", repoVer); dup.Parameters.AddWithValue("@p", name);
            if (Convert.ToInt32(dup.ExecuteScalar()) > 0) throw new Exception("Datei '" + name + "' ist bereits angehaengt - erst mit acmp_cc_remove_file entfernen.");
            string dir = Path.Combine(_fileRepoRoot, id, repoVer.ToString());
            string dest = Path.Combine(dir, name);
            if (File.Exists(dest)) throw new Exception("Im File Repository liegt bereits " + dest + ", aber ohne DB-Eintrag - bitte pruefen. Nichts geaendert.");
            Directory.CreateDirectory(dir);
            string tmp = dest + ".mcp-upload";
            if (File.Exists(tmp)) File.Delete(tmp);
            File.Copy(src, tmp);
            byte[] hash;
            using (var s = File.OpenRead(tmp)) using (var sha = SHA256.Create()) hash = sha.ComputeHash(s);
            long len = new FileInfo(tmp).Length;
            if (len != fi.Length) { File.Delete(tmp); throw new Exception("Kopie unvollstaendig (" + len + " statt " + fi.Length + " Byte). Nichts geaendert."); }
            if (!string.IsNullOrEmpty(expectSha256) && !string.Equals(BitConverter.ToString(hash).Replace("-", ""), expectSha256, StringComparison.OrdinalIgnoreCase))
            { File.Delete(tmp); throw new Exception("Quelldatei hat sich seit der Planung geaendert (SHA-256 weicht ab). Nichts angehaengt - bitte erneut aufrufen."); }
            File.Move(tmp, dest);
            Guid fileId = Guid.NewGuid();
            int kb;
            try
            {
                using (var tx = c.BeginTransaction())
                {
                    var ins = c.CreateCommand(); ins.Transaction = tx;
                    ins.CommandText = "INSERT INTO SYS_SCRIPT_FILES (FileID, ScriptID, FileRepoVersion, Path, IsFolder, PreviousFileID, sha256) VALUES (@fid, @id, @rv, @p, 0, NULL, @h)";
                    ins.Parameters.Add("@fid", SqlDbType.UniqueIdentifier).Value = fileId;
                    ins.Parameters.AddWithValue("@id", id); ins.Parameters.AddWithValue("@rv", repoVer); ins.Parameters.AddWithValue("@p", name);
                    ins.Parameters.Add("@h", SqlDbType.Binary, 32).Value = hash;
                    ins.ExecuteNonQuery();
                    kb = UpdateRepoSize(c, tx, id, ver, dir);
                    tx.Commit();
                }
            }
            catch { try { File.Delete(dest); } catch { } throw; }
            _log("CC-Datei angehaengt: " + id + " private\\" + name + " (" + len + " Byte) aus " + src + " von " + requester);
            var o = new JObject();
            o["result"] = "Datei angehaengt (Ordner private).";
            o["scriptId"] = id; o["file"] = "private\\" + name; o["bytes"] = len;
            o["sha256"] = BitConverter.ToString(hash).Replace("-", "");
            o["fileId"] = fileId.ToString(); o["fileRepoVersion"] = repoVer; o["fileRepoSizeKB"] = kb;
            var step = new JObject(); step["class"] = "TDownloadFile";
            var f = new JObject();
            f["_DataVersion"] = "2"; f["Files_To_Download"] = new JArray("private\\" + name);
            f["Global_Files_To_Download"] = ""; f["Current_Project_Files_To_Download"] = ""; f["Target_Path"] = "%PROJECTDIR%";
            f["Overwrite_Existing_Files"] = "False"; f["AlwaysUseAcmpServer"] = "False"; f["KeepFolderStructure"] = "True"; f["Validate_checksum_after_download"] = "True";
            step["fields"] = f;
            o["downloadStep"] = step;
            o["hinweis"] = "Der Schritt 'downloadStep' laedt die Datei nach %PROJECTDIR%; Aufruf dann z.B. \"%PROJECTDIR%" + name + "\".";
            return o;
        }
    }

    public JObject RemoveFile(string scriptId, string name, string requester)
    {
        CheckWrite(requester, true);
        string id = GuidBraces(scriptId);
        if (id == null) throw new Exception("scriptId ist keine GUID.");
        if (!IsRegistered(id)) throw new Exception("Dateien nur bei vom MCP angelegten Entwuerfen entfernen (" + id + " ist nicht in cc-drafts.json).");
        if (name != null && name.StartsWith("private\\", StringComparison.OrdinalIgnoreCase)) name = name.Substring(8);
        CheckFileName(name ?? "");
        lock (_lock)
        using (var c = new SqlConnection(_writeCs))
        {
            c.Open();
            int ver, repoVer; DraftRepo(c, id, out ver, out repoVer);
            string dir = Path.Combine(_fileRepoRoot, id, repoVer.ToString());
            string file = Path.Combine(dir, name);
            string moved = null;
            int kb;
            using (var tx = c.BeginTransaction())
            {
                var del = c.CreateCommand(); del.Transaction = tx;
                del.CommandText = "DELETE FROM SYS_SCRIPT_FILES WHERE ScriptID=@id AND FileRepoVersion=@rv AND Path=@p AND IsFolder=0";
                del.Parameters.AddWithValue("@id", id); del.Parameters.AddWithValue("@rv", repoVer); del.Parameters.AddWithValue("@p", name);
                if (del.ExecuteNonQuery() != 1) throw new Exception("Datei '" + name + "' ist nicht angehaengt. Nichts geaendert.");
                // Datei nicht loeschen, sondern in den Sicherungsordner verschieben (die Quelldatei bleibt ohnehin erhalten)
                if (File.Exists(file))
                {
                    string bdir = Path.Combine(_backupDir, "files");
                    Directory.CreateDirectory(bdir);
                    moved = Path.Combine(bdir, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "_" + id.Trim('{', '}') + "_" + name);
                    File.Move(file, moved);
                }
                try { kb = UpdateRepoSize(c, tx, id, ver, dir); tx.Commit(); }
                catch { if (moved != null) try { File.Move(moved, file); } catch { } throw; }
            }
            _log("CC-Datei entfernt: " + id + " private\\" + name + " von " + requester + (moved != null ? " (gesichert: " + moved + ")" : " (Datei fehlte auf der Platte)"));
            var o = new JObject();
            o["result"] = "Datei entfernt (DB-Eintrag geloescht" + (moved != null ? ", Datei in den Sicherungsordner verschoben" : ", Datei lag nicht im Repository") + ").";
            o["scriptId"] = id; o["file"] = "private\\" + name; o["fileRepoSizeKB"] = kb;
            if (moved != null) o["sicherung"] = moved;
            o["hinweis"] = "Download-Schritte, die private\\" + name + " verwenden, mit acmp_cc_update_draft anpassen.";
            return o;
        }
    }

    // Erlaubt nur Dateien unterhalb der konfigurierten Freigaben (ccWriter.fileSourceRoots, nur UNC).
    string CheckSource(string source)
    {
        if (string.IsNullOrEmpty(source)) throw new Exception(@"source fehlt (UNC-Pfad, z.B. \\server\freigabe\Software\...\setup.msi).");
        if (_fileSourceRoots.Count == 0) throw new Exception("ccWriter.fileSourceRoots ist leer - erlaubte UNC-Freigaben fuer Quelldateien in config.local.json eintragen. Nichts geaendert.");
        string full;
        try { full = Path.GetFullPath(source.Trim()); }
        catch (Exception ex) { throw new Exception("Ungueltiger Pfad '" + source + "': " + ex.Message); }
        foreach (string root in _fileSourceRoots)
            if (full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return full;
        throw new Exception("Quelle muss unterhalb von " + string.Join(" oder ", _fileSourceRoots.ToArray()) + " liegen (ccWriter.fileSourceRoots).");
    }

    // Nur ein reiner Dateiname (keine Unterordner) - so legt die Console Dateien in "private" ab.
    static void CheckFileName(string name)
    {
        if (name.Length == 0 || name.Length > 200 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name == "." || name == ".." || name.EndsWith(".") || name.EndsWith(" ") || name.EndsWith(".mcp-upload", StringComparison.OrdinalIgnoreCase))
            throw new Exception("Ungueltiger Dateiname '" + name + "' (nur ein Dateiname ohne Pfad).");
    }

    // Version und FileRepoVersion des neuesten Eintrags; nur Entwuerfe (State 1).
    static void DraftRepo(SqlConnection c, string id, out int ver, out int repoVer)
    {
        var q = c.CreateCommand();
        q.CommandText = "SELECT TOP 1 ScriptVersion, State, FileRepoVersion FROM SYS_SCRIPTS WHERE SCRIPTID=@id ORDER BY ScriptVersion DESC";
        q.Parameters.AddWithValue("@id", id);
        using (var r = q.ExecuteReader())
        {
            if (!r.Read()) throw new Exception("Client Command " + id + " nicht gefunden.");
            ver = r.GetInt32(0);
            int st = Convert.ToInt32(r.GetValue(1));
            if (st != 1) throw new Exception("Command ist kein Entwurf mehr (State " + st + ") - Dateien nur an Entwuerfe haengen.");
            repoVer = Convert.ToInt32(r.GetValue(2));
            if (repoVer < 1) throw new Exception("FileRepoVersion " + repoVer + " unerwartet - nichts geaendert.");
        }
    }

    // FileRepoSize wie bei der Freigabe in der Console: Summe aller Dateien dieser Dateiversion in KB, abgerundet,
    // aber mindestens 1, sobald es Dateien gibt (ohne Dateien 0). Belegt am 13.10.2026: 4 B -> 1, 1.152 B -> 1,
    // 304.886 B -> 297, 207.273.008 B -> 202415; mehrere Dateien: 735 + 8.694.592 B -> 8491 (Summe, nicht je Datei).
    static int UpdateRepoSize(SqlConnection c, SqlTransaction tx, string id, int ver, string dir)
    {
        long sum = 0; int count = 0;
        if (Directory.Exists(dir))
            foreach (var f in new DirectoryInfo(dir).GetFiles("*", SearchOption.AllDirectories))
                if (!f.Name.EndsWith(".mcp-upload", StringComparison.OrdinalIgnoreCase)) { sum += f.Length; count++; }
        int kb = count == 0 ? 0 : Math.Max(1, (int)(sum / 1024));
        var up = c.CreateCommand(); up.Transaction = tx;
        up.CommandText = "UPDATE SYS_SCRIPTS SET FileRepoSize=@kb WHERE SCRIPTID=@id AND ScriptVersion=@v AND State=1";
        up.Parameters.AddWithValue("@kb", kb); up.Parameters.AddWithValue("@id", id); up.Parameters.AddWithValue("@v", ver);
        if (up.ExecuteNonQuery() != 1) throw new Exception("FileRepoSize konnte nicht gesetzt werden (inzwischen freigegeben?).");
        return kb;
    }

    // Sichert die Spalten, die dieses Tool aendert, plus Variablen. Wirft, wenn kein eigener Entwurf (State 1) oder Sicherung scheitert.
    string BackupDraft(SqlConnection c, string id, out int ver, string reason)
    {
        var q = c.CreateCommand();
        q.CommandText = "SELECT TOP 1 ScriptVersion, State, DESCRIPTION, LONGDESCRIPTION, FOLDERID, CLIENTSCRIPT, CONSOLESCRIPT, LastModifier, LastModifiedDate " +
            "FROM SYS_SCRIPTS WHERE SCRIPTID=@id ORDER BY ScriptVersion DESC";
        q.Parameters.AddWithValue("@id", id);
        var b = new JObject();
        using (var r = q.ExecuteReader())
        {
            if (!r.Read()) throw new Exception("Client Command " + id + " nicht gefunden.");
            ver = r.GetInt32(0);
            int st = Convert.ToInt32(r.GetValue(1));
            if (st != 1) throw new Exception("Command ist kein Entwurf mehr (State " + st + ") - fuer Aenderungen eine neue Version anlegen: acmp_cc_new_version.");
            b["scriptId"] = id; b["version"] = ver; b["grund"] = reason; b["zeit"] = DateTime.Now.ToString("s");
            b["DESCRIPTION"] = r.IsDBNull(2) ? null : r.GetString(2);
            b["LONGDESCRIPTION"] = r.IsDBNull(3) ? null : r.GetString(3);
            b["FOLDERID"] = r.IsDBNull(4) ? null : r.GetString(4);
            b["CLIENTSCRIPT"] = r.IsDBNull(5) ? null : Convert.ToBase64String((byte[])r.GetValue(5));
            b["CONSOLESCRIPT"] = r.IsDBNull(6) ? null : Convert.ToBase64String((byte[])r.GetValue(6));
            b["LastModifier"] = r.IsDBNull(7) ? null : r.GetString(7);
            b["LastModifiedDate"] = r.IsDBNull(8) ? null : (JToken)Convert.ToDouble(r.GetValue(8));
        }
        var vq = c.CreateCommand();
        vq.CommandText = "SELECT Name, CAST(Origin AS int) AS Origin, CAST(ReadOnly AS bit) AS ReadOnly FROM SYS_SCRIPTS_VARIABLES WHERE ScriptId=@id AND ScriptVersion=@v ORDER BY Name";
        vq.Parameters.AddWithValue("@id", id); vq.Parameters.AddWithValue("@v", ver);
        var va = new JArray();
        using (var r = vq.ExecuteReader())
            while (r.Read()) { var v = new JObject(); v["Name"] = r.GetString(0); v["Origin"] = r.GetInt32(1); v["ReadOnly"] = r.GetBoolean(2); va.Add(v); }
        b["variables"] = va;
        Directory.CreateDirectory(_backupDir);
        string file = Path.Combine(_backupDir, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "_" + id.Trim('{', '}') + "_v" + ver + ".json");
        File.WriteAllText(file, b.ToString(Formatting.Indented), new UTF8Encoding(false));
        if (new FileInfo(file).Length < 50) throw new Exception("Sicherung unvollstaendig - nichts geaendert.");
        return file;
    }

    static void InsertVars(SqlConnection c, SqlTransaction tx, string id, int ver, List<CcVar> vars)
    {
        foreach (var v in vars)
        {
            var iv = c.CreateCommand(); iv.Transaction = tx;
            iv.CommandText = "INSERT INTO SYS_SCRIPTS_VARIABLES (ScriptId, ScriptVersion, Name, Origin, ReadOnly) VALUES (@id, @v, @n, @o, @r)";
            iv.Parameters.AddWithValue("@id", id); iv.Parameters.AddWithValue("@v", ver);
            iv.Parameters.AddWithValue("@n", v.Name); iv.Parameters.AddWithValue("@o", (int)v.Origin); iv.Parameters.AddWithValue("@r", v.ReadOnly);
            iv.ExecuteNonQuery();
        }
    }

    // Rechte: Die Console gibt einem neuen CC die Rechte seines Ordners (SYS_Folders_AclEntries_* -> SYS_AccessControl_AclEntries_*).
    // Ohne Eintrag mit VisibleState blendet die Console den CC aus.
    static int CopyFolderAcl(SqlConnection c, SqlTransaction tx, string id, string folderId)
    {
        int n = 0;
        foreach (string kind in new[] { "Users", "Groups" })
        {
            var cmd = c.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO SYS_AccessControl_AclEntries_" + kind + " (ObjectID, AccessID, EditState, VisibleState) " +
                "SELECT @id, f.AccessID, f.EditState, f.VisibleState FROM SYS_Folders_AclEntries_" + kind + " f WHERE f.ObjectID=@f";
            cmd.Parameters.AddWithValue("@id", id); cmd.Parameters.AddWithValue("@f", folderId);
            n += cmd.ExecuteNonQuery();
        }
        return n;
    }

    // Ordner: GUID oder Name (eindeutig) aus SYS_SCRIPT_STRUCTURE
    static string ResolveFolder(SqlConnection c, string folder)
    {
        if (string.IsNullOrEmpty(folder)) throw new Exception("folder fehlt (GUID oder Name eines Client-Command-Ordners).");
        var q = c.CreateCommand();
        string g = GuidBraces(folder);
        q.CommandText = g != null ? "SELECT ScriptStructureID FROM SYS_SCRIPT_STRUCTURE WHERE ScriptStructureID=@f"
                                  : "SELECT ScriptStructureID FROM SYS_SCRIPT_STRUCTURE WHERE Description=@f";
        q.Parameters.AddWithValue("@f", g != null ? g : folder);
        var ids = new List<string>();
        using (var r = q.ExecuteReader()) while (r.Read()) ids.Add(r.GetString(0));
        if (ids.Count == 0) throw new Exception("Ordner nicht gefunden: " + folder);
        if (ids.Count > 1) throw new Exception("Ordnername nicht eindeutig: " + folder + " - bitte GUID angeben.");
        return ids[0];
    }

    // ---------- Register eigener Entwuerfe ----------
    bool IsRegistered(string id)
    {
        lock (_lock)
        {
            if (!File.Exists(_registryFile)) return false;
            foreach (JObject e in JArray.Parse(File.ReadAllText(_registryFile)))
                if (string.Equals((string)e["scriptId"], id, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    void Register(string id, string name, string requester)
    {
        JArray a = File.Exists(_registryFile) ? JArray.Parse(File.ReadAllText(_registryFile)) : new JArray();
        var e = new JObject(); e["scriptId"] = id; e["name"] = name; e["requester"] = requester; e["created"] = DateTime.Now.ToString("s");
        a.Add(e);
        File.WriteAllText(_registryFile, a.ToString(Formatting.Indented), new UTF8Encoding(false));
    }

    // ---------- Hilfen ----------
    static int CountSteps(List<CcNode> nodes) { int c = 0; foreach (var n in nodes) c += 1 + CountSteps(n.Children); return c; }
    static string NewGuid() { return "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}"; }
    static byte[] B64(string s) { return string.IsNullOrEmpty(s) ? new byte[0] : Convert.FromBase64String(s); }
    static string GuidBraces(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        Guid g; if (!Guid.TryParse(s.Trim().Trim('{', '}'), out g)) return null;
        return "{" + g.ToString().ToUpperInvariant() + "}";
    }
}
