// AcmpCtl: Vorlagen aus der ACMP CTL (Community Template Library) als Client-Command-ENTWURF installieren.
// Ablauf: Vorlage laden (ctl.sources: lokaler Ordner oder https-Basis, oder inline) -> pruefen -> Parameter ({{NAME}}),
// globalen Account ({{GLOBAL_ACCOUNT_ID}}) und individuelle Felder ({{CF:<Feld>:<Eigenschaft>}}) einsetzen
// -> AcmpCcWriter.BuildScript / CreateDraft.
// Geschrieben wird ausschliesslich ueber AcmpCcWriter.CreateDraft und damit unter denselben Regeln
// (allowedRequesters, allowedFolderIds, immer nur Entwurf, freigeben mit acmp_cc_release oder in der Console).
// Dateien (files): url (https, Download beim Hersteller) oder path (im Vorlagen-Ordner der Quelle), immer mit SHA-256.
// Sie landen zuerst im Zwischenspeicher <cfgDir>\ctl-cache\<sha256> (schon beim dryRun),
// erst nach dem Anlegen des Entwurfs haengt AcmpCcWriter.AddFileTrusted sie an (File Repository, Ordner private).
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Formatting = Newtonsoft.Json.Formatting;

class AcmpCtl
{
    const string SchemaId = "acmp-ctl/template@1";
    const string DefaultSource = "https://raw.githubusercontent.com/Herzog-GmbH/acmp-ctl/main";
    const string GlobalAccount = "GLOBAL_ACCOUNT_ID";
    static readonly Regex IdRx = new Regex("^[a-z0-9]+(-[a-z0-9]+)*$");
    static readonly Regex ParamRx = new Regex(@"\{\{([A-Z][A-Z0-9_]*)\}\}");
    static readonly Regex CfRx = new Regex(@"\{\{CF:([^:{}]+):([A-Za-z]+)\}\}");
    static readonly Regex AnyPh = new Regex(@"\{\{[^{}]*\}\}");
    static readonly Regex SecretKey = new Regex("(pass(wort|word)?|kennwort|pwd|secret|token|api.?key|credential)$", RegexOptions.IgnoreCase);
    static readonly HashSet<string> CfProps = new HashSet<string> { "FieldID", "TableID", "FolderID", "FolderParentID", "Tablename", "Fieldname" };
    static readonly Regex Sha256Rx = new Regex("^[0-9a-fA-F]{64}$");
    // Verweis auf eine Datei im Bereich private: ganzer Wert (Eintrag in Files_To_Download) oder Inhalt eines XML-Elements
    static readonly Regex PrivateRef = new Regex(@"^\s*private\\([^\\/:*?""<>|\r\n]+?)\s*$|>\s*private\\([^\\/:*?""<>|\r\n]+?)\s*<", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    // SYS_CustomFields.InputMethod -> Bezeichnung (an den Feldern dieser Installation gegen GetCustomFieldList abgeglichen)
    static readonly Dictionary<int, string> InputMethods = new Dictionary<int, string> {
        { 1, "Liste" }, { 3, "Datum" }, { 7, "IPv4" }, { 9, "Mehrzeilig" }, { 10, "Text" }, { 13, "Ganzzahl" } };

    readonly AcmpCcWriter _ccw;
    readonly string _readCs, _logFile, _cacheDir;
    readonly long _maxFileBytes;
    readonly List<string> _sources = new List<string>();
    readonly Action<string> _log;
    static readonly object _lock = new object();

    public AcmpCtl(AcmpCcWriter ccw, string readCs, JObject cfg, string cfgDir, Action<string> log)
    {
        _ccw = ccw; _readCs = readCs; _log = log;
        _logFile = Path.Combine(cfgDir, "ctl-installs.json");
        string cd = cfg != null && cfg["cacheDir"] != null ? (string)cfg["cacheDir"] : "ctl-cache";
        _cacheDir = Path.IsPathRooted(cd) ? cd : Path.Combine(cfgDir, cd);
        _maxFileBytes = ccw != null ? ccw.MaxFileBytes : 2048L * 1024 * 1024;
        JArray src = cfg != null ? cfg["sources"] as JArray : null;
        if (src != null) { foreach (JToken t in src) { string s = ((string)t ?? "").Trim().TrimEnd('/', '\\'); if (s.Length > 0) _sources.Add(s); } }
        else { _sources.Add(Path.Combine(cfgDir, "ctl")); _sources.Add(DefaultSource); }
    }

    public string SourceList { get { return string.Join(", ", _sources.ToArray()); } }

    class CfInfo
    {
        public string Name, FieldID, TableID, FolderID, FolderParentID, Tablename, Fieldname, Table, Folder, Type;
        public string Diag;  // wo die Aufloesung Tabelle -> Ordner -> Elternordner abgebrochen ist
        public int MaxLength;
        public string Get(string prop)
        {
            switch (prop)
            {
                case "FieldID": return FieldID;
                case "TableID": return TableID;
                case "FolderID": return FolderID;
                case "FolderParentID": return FolderParentID;
                case "Tablename": return Tablename;
                case "Fieldname": return Fieldname;
            }
            return null;
        }
        public JObject ToJson()
        {
            var o = new JObject();
            o["fieldId"] = FieldID; o["tableId"] = TableID; o["table"] = Table; o["folder"] = Folder;
            o["folderId"] = FolderID; o["folderParentId"] = FolderParentID; o["tablename"] = Tablename; o["fieldname"] = Fieldname;
            if (Type != null) o["type"] = Type;
            if (MaxLength > 0) o["maxLength"] = MaxLength;
            return o;
        }
    }

    class CtlFile
    {
        public string Name, Sha256, Origin, CachePath;
        public long Bytes;
        public bool FromCache;
        public JObject ToJson()
        {
            var o = new JObject();
            o["name"] = Name; o["bytes"] = Bytes; o["sha256"] = Sha256; o["origin"] = Origin;
            o["geprueft"] = FromCache ? "SHA-256 stimmt (aus dem Zwischenspeicher)" : "heruntergeladen, SHA-256 stimmt";
            return o;
        }
    }

    // ================= Installieren =================

    public JObject Install(JObject a, string requester)
    {
        bool dry = a["dryRun"] != null && a["dryRun"].Type == JTokenType.Boolean && (bool)a["dryRun"];
        string source, raw, baseLoc;
        JObject tpl = LoadTemplate(a, out source, out raw, out baseLoc);

        var problems = new List<string>();
        Validate(tpl, a, problems);
        if (problems.Count > 0) throw new Exception("Vorlage nicht installierbar:\n- " + string.Join("\n- ", problems.ToArray()));
        JArray fileList = tpl["files"] as JArray;
        bool hasFiles = fileList != null && fileList.Count > 0;
        // Datei-Tools (Rechte, File Repository) pruefen, bevor etwas heruntergeladen oder angelegt wird
        if (hasFiles && !dry) _ccw.CheckWrite(requester, true);

        string tid = (string)tpl["id"], tver = (string)tpl["version"];
        var hints = new List<string>();
        Dictionary<string, string> values = ResolveParams(tpl, a["parameters"] as JObject);

        // Globaler Account: die GUID ist in jeder Installation anders und in keiner fuer den MCP lesbaren Tabelle
        string ga = null;
        JToken req = tpl["requires"];
        JToken reqGa = req != null ? req["globalAccount"] : null;
        if (reqGa != null && reqGa.Type == JTokenType.String && ((string)reqGa).Length > 0)
        {
            ga = GuidBraces((string)a["globalAccountId"]);
            if (ga == null) throw new Exception("Die Vorlage braucht einen globalen ACMP-Account (" + (string)reqGa + "). Bitte globalAccountId (GUID des globalen Accounts aus der ACMP-Console) angeben.");
        }

        Dictionary<string, CfInfo> cfs = ResolveCustomFields(tpl, hints);

        JArray steps = (JArray)tpl["steps"].DeepClone();
        JArray vars = tpl["variables"] is JArray ? (JArray)tpl["variables"].DeepClone() : new JArray();
        Substitute(steps, values, ga, cfs, false);
        Substitute(vars, values, ga, cfs, false);
        var left = new List<string>();
        FindPlaceholders(steps, left); FindPlaceholders(vars, left);
        if (left.Count > 0) throw new Exception("Nicht ersetzte Platzhalter: " + string.Join(", ", left.ToArray()) + ". Nichts angelegt.");
        // Pruefsumme der aufgeloesten Schritte: ist _expectStepsHash gesetzt (nur serverseitig), wird vor dem Schreiben verglichen
        string stepsHash = Sha256Hex(steps.ToString(Formatting.None) + "|" + vars.ToString(Formatting.None));
        string expect = (string)a["_expectStepsHash"];
        if (!dry && !string.IsNullOrEmpty(expect) && expect != stepsHash)
            throw new Exception("Die aufgeloesten Schritte weichen von der freigegebenen Vorschau ab (z.B. individuelles Feld geaendert). Nichts angelegt - bitte erneut aufrufen.");

        // Kopf-JSON der individuellen Felder aus den Schritten sammeln (wie die Console es fuer das Skript ablegt)
        string hCf = CollectStored(steps, "StoredCustomFields");
        string hTb = CollectStored(steps, "StoredTables");
        string hFo = CollectStored(steps, "StoredFolders");

        List<string> notes;
        CcScript script = _ccw.BuildScript(steps, vars, out notes);
        script.StoredCustomFields = hCf; script.StoredTables = hTb; script.StoredFolders = hFo;
        notes.AddRange(hints);
        // Dateien schon beim dryRun holen und pruefen: die Vorschau der Freigabe zeigt so nur gepruefte Dateien
        List<CtlFile> files = hasFiles ? FetchFiles(fileList, baseLoc, notes) : new List<CtlFile>();
        CheckFileRefs(steps, files, notes);

        string name = a["name"] != null && ((string)a["name"] ?? "").Trim().Length > 0 ? ((string)a["name"]).Trim() : (string)tpl["name"];
        string folder = (string)a["folder"];
        if (string.IsNullOrEmpty(folder)) throw new Exception("folder fehlt (Client-Command-Ordner: GUID oder eindeutiger Name).");

        var info = new JObject();
        info["template"] = tid + "@" + tver;
        info["status"] = (string)tpl["status"];
        info["source"] = source;
        info["sha256"] = Sha256Hex(raw);
        if (values.Count > 0) { var pv = new JObject(); foreach (var kv in values) pv[kv.Key] = kv.Value; info["parameters"] = pv; }
        if (ga != null) info["globalAccountId"] = ga;
        if (cfs.Count > 0) { var cj = new JObject(); foreach (var kv in cfs) cj[kv.Key] = kv.Value.ToJson(); info["customFields"] = cj; }
        if (files.Count > 0) { var fj = new JArray(); foreach (var f in files) fj.Add(f.ToJson()); info["files"] = fj; }

        if (dry)
        {
            var o = new JObject();
            o["result"] = "Vorschau (dryRun) - nichts geschrieben" + (files.Count > 0 ? " (die Dateien liegen geprueft im Zwischenspeicher des MCP-Servers)" : "") +
                ". Ordner, Rechte und doppelte Namen prueft erst die echte Installation.";
            o["name"] = name; o["folder"] = folder; o["ctl"] = info;
            o["totalSteps"] = CountSteps(steps);
            var vj = new JArray();
            foreach (var v in script.Vars) { var x = new JObject(); x["name"] = v.Name; x["value"] = v.Value; vj.Add(x); }
            o["variables"] = vj;
            o["steps"] = steps;
            o["stepsHash"] = stepsHash;
            if (hCf != null) { var h = new JObject(); h["StoredCustomFields"] = hCf; h["StoredTables"] = hTb; h["StoredFolders"] = hFo; o["skriptKopf"] = h; }
            if (notes.Count > 0) o["hinweise"] = new JArray(notes.ToArray());
            return o;
        }

        string longDesc = "Aus ACMP CTL: " + tid + "@" + tver + " (" + source + ")\r\n" + ((string)tpl["summary"] ?? "");
        JObject res = _ccw.CreateDraft(name, folder, longDesc, script, requester, notes);
        res["ctl"] = info;
        if (files.Count > 0)
        {
            // Der Entwurf existiert jetzt; scheitert eine Datei, wird das gemeldet (loeschen kann der MCP Entwuerfe noch nicht)
            var attached = new JArray(); var failed = new JArray();
            foreach (var f in files)
            {
                try { _ccw.AddFileTrusted((string)res["scriptId"], f.CachePath, f.Name, requester, f.Sha256); attached.Add("private\\" + f.Name); }
                catch (Exception ex) { failed.Add(f.Name + ": " + ex.Message); }
            }
            res["dateien"] = attached;
            if (failed.Count > 0)
            {
                res["dateienFehler"] = failed;
                res["result"] = "Entwurf angelegt, aber " + failed.Count + " von " + files.Count + " Datei(en) fehlen (siehe dateienFehler). " +
                    "Nicht freigeben. Fehlende Dateien mit acmp_cc_add_file nachreichen oder den Entwurf in der Console loeschen.";
            }
        }
        LogInstall(res, info, requester);
        _log("CTL-Vorlage installiert: " + tid + "@" + tver + " -> " + (string)res["scriptId"] + " '" + name + "' von " + requester +
             (files.Count > 0 ? " (" + ((JArray)res["dateien"]).Count + "/" + files.Count + " Dateien)" : ""));
        return res;
    }

    // ================= Laden =================

    // baseLoc: Ordner bzw. https-Basis der Vorlage, gegen den files[].path aufgeloest wird (null = inline ohne Quelle)
    JObject LoadTemplate(JObject a, out string source, out string raw, out string baseLoc)
    {
        JToken inline = a["template"];
        if (inline != null && inline.Type != JTokenType.Null)
        {
            raw = inline.Type == JTokenType.String ? (string)inline : inline.ToString(Formatting.None);
            source = a["_source"] != null ? (string)a["_source"] + " (vom Server festgehalten)" : "inline";
            baseLoc = (string)a["_sourceBase"];
            return ParseTemplate(raw, "inline");
        }
        string id = ((string)a["id"] ?? "").Trim();
        if (id.Length == 0) throw new Exception("id (Vorlagen-ID aus der ACMP CTL) oder template fehlt.");
        int at = id.IndexOf('@');
        if (at > 0) { if (a["version"] == null) a["version"] = id.Substring(at + 1); id = id.Substring(0, at); }
        if (!IdRx.IsMatch(id)) throw new Exception("Ungueltige Vorlagen-ID '" + id + "' (nur a-z, 0-9 und Bindestriche).");
        var errs = new List<string>();
        foreach (string src in _sources)
        {
            try
            {
                if (src.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    string url = src + "/templates/" + id + "/template.json";
                    raw = Download(url); source = url; baseLoc = src + "/templates/" + id;
                    return ParseTemplate(raw, url);
                }
                if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) { errs.Add(src + ": nur https ist erlaubt"); continue; }
                string f = Path.Combine(Path.Combine(Path.Combine(src, "templates"), id), "template.json");
                if (!File.Exists(f)) { errs.Add(f + ": nicht vorhanden"); continue; }
                raw = File.ReadAllText(f, Encoding.UTF8); source = f; baseLoc = Path.GetDirectoryName(f);
                return ParseTemplate(raw, f);
            }
            catch (Exception ex) { errs.Add(src + ": " + ex.Message); }
        }
        throw new Exception("Vorlage '" + id + "' in keiner Quelle gefunden (ctl.sources):\n- " + string.Join("\n- ", errs.ToArray()));
    }

    static JObject ParseTemplate(string raw, string where)
    {
        try { return JObject.Parse(raw); }
        catch (Exception ex) { throw new Exception("template.json aus " + where + " ist kein gueltiges JSON: " + ex.Message); }
    }

    static string Download(string url)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.Timeout = 15000; req.UserAgent = "acmp-mcp-ctl";
        using (var resp = (HttpWebResponse)req.GetResponse())
        using (var s = resp.GetResponseStream())
        using (var r = new StreamReader(s, Encoding.UTF8))
        {
            string t = r.ReadToEnd();
            if (t.Length > 2000000) throw new Exception("Vorlage groesser als 2 MB.");
            return t;
        }
    }

    // ================= Suchen (acmp_lib_search, Erweiterung 25.10.2026) =================
    // Index der Vorlagen: bei https-Quellen der Index der CTL-Webseite (GitHub Pages baut ihn aus dem Repo, im Repo selbst liegt er nicht),
    // bei lokalen Quellen die template.json unter <quelle>\templates\<id>\. Erste Quelle mit einer id gewinnt (Reihenfolge wie beim Installieren).
    public const string DefaultIndexUrl = "https://herzog-gmbh.github.io/acmp-ctl/index.json";

    static bool Has(JToken t, string q)
    {
        if (t == null || t.Type == JTokenType.Null) return false;
        string s = t.Type == JTokenType.String ? (string)t : t.ToString(Formatting.None);
        return s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    static string TStr(JToken t) { return t != null && t.Type == JTokenType.String ? (string)t : null; }

    static JObject SearchHit(JObject t, string where, Dictionary<string, string> installed)
    {
        var o = new JObject();
        string id = TStr(t["id"]);
        o["id"] = id; o["version"] = t["version"]; o["status"] = t["status"]; o["name"] = t["name"];
        string sum = TStr(t["summary"]) ?? "";
        o["kurz"] = sum.Length > 300 ? sum.Substring(0, 300) + " ..." : sum;
        if (t["category"] != null) o["kategorie"] = t["category"];
        if (t["area"] != null) o["bereich"] = t["area"];
        if (t["tags"] is JArray) o["tags"] = t["tags"];
        var pars = new JArray();
        if (t["parameters"] is JArray) foreach (JToken p in (JArray)t["parameters"]) if (p is JObject && p["name"] != null) pars.Add((string)p["name"] + (p["required"] != null && p["required"].Type == JTokenType.Boolean && (bool)p["required"] ? " (Pflicht)" : ""));
        if (pars.Count > 0) o["parameter"] = pars;
        var need = new List<string>();
        JToken req = t["requires"];
        if (req != null && req["customFields"] is JArray && ((JArray)req["customFields"]).Count > 0) need.Add(((JArray)req["customFields"]).Count + " individuelle(s) Feld(er)");
        if (req != null && req["globalAccount"] != null && req["globalAccount"].Type == JTokenType.String && ((string)req["globalAccount"]).Length > 0) need.Add("globalen Account (globalAccountId)");
        if (t["files"] is JArray && ((JArray)t["files"]).Count > 0) need.Add(((JArray)t["files"]).Count + " Datei(en)");
        if (need.Count > 0) o["braucht"] = string.Join(", ", need.ToArray());
        if (t["stepCount"] != null) o["schritte"] = t["stepCount"];
        if (t["acmp"] != null && t["acmp"]["minVersion"] != null) o["abAcmp"] = t["acmp"]["minVersion"];
        string iv; if (id != null && installed.TryGetValue(id, out iv)) o["installiert"] = iv;
        o["quelle"] = where;
        return o;
    }

    public JObject Search(JObject a)
    {
        string q = a["query"] != null && a["query"].Type == JTokenType.String ? ((string)a["query"]).Trim() : "";
        string cat = a["category"] != null && a["category"].Type == JTokenType.String ? ((string)a["category"]).Trim() : "";
        int top = 30; if (a["top"] != null && a["top"].Type == JTokenType.Integer) top = Math.Max(1, Math.Min(200, (int)a["top"]));
        string[] words = q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        // zuletzt installierte Version je Vorlage (ctl-installs.json: template = "<id>@<version>")
        var installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(_logFile))
                foreach (JToken e in JArray.Parse(File.ReadAllText(_logFile)))
                {
                    string tv = (string)e["template"]; if (string.IsNullOrEmpty(tv)) continue;
                    int at = tv.LastIndexOf('@');
                    string tid = at > 0 ? tv.Substring(0, at) : tv, ver = at > 0 ? tv.Substring(at + 1) : "?";
                    JToken it = e["installed"];   // JArray.Parse macht aus dem ISO-Text ein Datum - selbst formatieren, nicht nach Kultur
                    string when = it == null || it.Type == JTokenType.Null ? "?" : it.Type == JTokenType.Date
                        ? ((DateTime)it).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) : (string)it;
                    installed[tid] = ver + " am " + when + " als \"" + ((string)e["name"] ?? "?") + "\"";
                }
        }
        catch { }
        var hits = new JArray(); var src = new JArray(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int total = 0;
        foreach (string s in _sources)
        {
            var list = new List<JObject>(); string where;
            try
            {
                if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    string url = s.TrimEnd('/') == DefaultSource ? DefaultIndexUrl : s.TrimEnd('/') + "/index.json";
                    JObject idx = JObject.Parse(Download(url));
                    if (idx["templates"] is JArray) foreach (JToken t in (JArray)idx["templates"]) if (t is JObject) list.Add((JObject)t);
                    where = url;
                    src.Add(url + ": " + list.Count + " Vorlagen" + (idx["generated"] != null ? ", Stand " + idx["generated"] : ""));
                }
                else
                {
                    string dir = Path.Combine(s, "templates");
                    where = dir;
                    if (!Directory.Exists(dir)) { src.Add(dir + ": nicht vorhanden"); continue; }
                    foreach (string d in Directory.GetDirectories(dir))
                    {
                        string f = Path.Combine(d, "template.json");
                        if (!File.Exists(f)) continue;
                        try { list.Add(JObject.Parse(File.ReadAllText(f))); } catch { }
                    }
                    src.Add(dir + ": " + list.Count + " Vorlagen");
                }
            }
            catch (Exception ex) { src.Add(s + ": nicht lesbar (" + ex.Message + ")"); continue; }
            foreach (JObject t in list)
            {
                string id = TStr(t["id"]);
                if (string.IsNullOrEmpty(id) || !seen.Add(id)) continue;
                total++;
                if (cat.Length > 0 && !string.Equals(TStr(t["category"]), cat, StringComparison.OrdinalIgnoreCase) && !string.Equals(TStr(t["area"]), cat, StringComparison.OrdinalIgnoreCase)) continue;
                bool ok = true;
                foreach (string w in words)
                    if (!(Has(t["id"], w) || Has(t["name"], w) || Has(t["summary"], w) || Has(t["tags"], w) || Has(t["applications"], w) || Has(t["category"], w))) { ok = false; break; }
                if (!ok) continue;
                if (hits.Count < top) hits.Add(SearchHit(t, where, installed));
            }
        }
        var res = new JObject();
        res["treffer"] = hits.Count; res["vorlagenGesamt"] = total; res["vorlagen"] = hits; res["quellen"] = src;
        res["hinweis"] = hits.Count == 0 ? "Nichts gefunden - kuerzeren Suchbegriff oder ohne query (alle Vorlagen) versuchen." :
            "Installieren: acmp_lib_install id=<id> folder=<Ordner> (erst dryRun=true). Beschreibung und Schritte: Webseite der CTL oder template.json der Quelle.";
        return res;
    }

    // ================= Pruefen =================

    static void Validate(JObject tpl, JObject a, List<string> p)
    {
        if ((string)tpl["schema"] != SchemaId) p.Add("schema ist nicht \"" + SchemaId + "\".");
        string id = (string)tpl["id"];
        if (string.IsNullOrEmpty(id) || !IdRx.IsMatch(id)) p.Add("id fehlt oder ist ungueltig.");
        string wantId = ((string)a["id"] ?? "").Trim();
        if (wantId.IndexOf('@') > 0) wantId = wantId.Substring(0, wantId.IndexOf('@'));
        if (wantId.Length > 0 && wantId != id) p.Add("Verlangt war die Vorlage '" + wantId + "', geladen wurde '" + id + "'.");
        string ver = (string)tpl["version"];
        if (string.IsNullOrEmpty(ver)) p.Add("version fehlt.");
        string want = a["version"] != null ? ((string)a["version"] ?? "").Trim() : "";
        if (want.Length > 0 && want != ver) p.Add("Verlangt war Version " + want + ", die Vorlage hat " + ver + ".");
        string st = (string)tpl["status"];
        if (st == "beispiel") p.Add("Status 'beispiel': Die Vorlage zeigt nur das Format und ist nicht installierbar.");
        else if (st != "entwurf" && st != "geprueft") p.Add("Unbekannter status '" + st + "'.");
        if (string.IsNullOrEmpty((string)tpl["name"])) p.Add("name fehlt.");
        JArray steps = tpl["steps"] as JArray;
        if (steps == null || steps.Count == 0) p.Add("steps fehlt oder ist leer.");
        else CheckSteps(steps, "steps", p);
        CheckFiles(tpl, p);
        // Individuelle Felder: jeder {{CF:..}}-Verweis braucht einen Eintrag in requires.customFields
        var names = new HashSet<string>(StringComparer.Ordinal);
        JArray cfl = tpl["requires"] != null ? tpl["requires"]["customFields"] as JArray : null;
        if (cfl != null) foreach (JToken c in cfl) if (c is JObject && c["name"] != null) names.Add((string)c["name"]);
        var used = new List<string>(); CollectStrings(steps, used);
        foreach (string s in used)
            foreach (Match m in CfRx.Matches(s))
            {
                if (!names.Contains(m.Groups[1].Value)) p.Add("{{CF:" + m.Groups[1].Value + ":...}} steht nicht in requires.customFields.");
                if (!CfProps.Contains(m.Groups[2].Value)) p.Add("Unbekannte Eigenschaft " + m.Groups[2].Value + " in {{CF:" + m.Groups[1].Value + ":...}}.");
            }
    }

    static void CheckSteps(JArray steps, string path, List<string> p)
    {
        for (int i = 0; i < steps.Count; i++)
        {
            string at = path + "[" + i + "]";
            JObject s = steps[i] as JObject;
            if (s == null) { p.Add(at + ": kein Objekt."); continue; }
            string cls = (string)s["class"];
            if (string.IsNullOrEmpty(cls) || !cls.StartsWith("T")) p.Add(at + ": class fehlt.");
            JObject f = s["fields"] as JObject;
            if (f == null && string.IsNullOrEmpty((string)s["xml"])) p.Add(at + " (" + cls + "): weder fields noch xml.");
            if (f != null)
                foreach (JProperty pr in f.Properties())
                    if (pr.Value.Type == JTokenType.String && ((string)pr.Value).Length > 0 && SecretKey.IsMatch(pr.Name))
                        p.Add(at + "." + pr.Name + ": Kennwortfeld ist gefuellt. Vorlagen duerfen keine Kennwoerter enthalten.");
            JArray ch = s["children"] as JArray;
            if (ch != null) CheckSteps(ch, at + ".children", p);
        }
    }

    // files: Dateiname ohne Pfad, eindeutig; SHA-256 Pflicht; genau eines von url (https) oder path (relativ im Vorlagen-Ordner)
    static void CheckFiles(JObject tpl, List<string> p)
    {
        JToken ft = tpl["files"];
        if (ft == null || ft.Type == JTokenType.Null) return;
        JArray files = ft as JArray;
        if (files == null) { p.Add("files ist keine Liste."); return; }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < files.Count; i++)
        {
            string at = "files[" + i + "]";
            JObject f = files[i] as JObject;
            if (f == null) { p.Add(at + ": kein Objekt."); continue; }
            string n = (string)f["name"];
            if (!ValidFileName(n)) { p.Add(at + ": Dateiname '" + n + "' ungueltig (nur ein Dateiname ohne Pfad)."); continue; }
            at += " " + n;
            if (!seen.Add(n)) p.Add(at + ": Dateiname doppelt.");
            string h = (string)f["sha256"];
            if (h == null || !Sha256Rx.IsMatch(h)) p.Add(at + ": sha256 fehlt oder ist ungueltig (64 Hex-Zeichen). Ohne Pruefsumme wird keine Datei angehaengt.");
            string url = (string)f["url"], rel = (string)f["path"];
            bool hasUrl = !string.IsNullOrEmpty(url), hasPath = !string.IsNullOrEmpty(rel);
            if (hasUrl == hasPath) p.Add(at + ": genau eines von url (Download beim Hersteller) oder path (Datei im Vorlagen-Ordner) angeben.");
            Uri u;
            if (hasUrl && (!Uri.TryCreate(url, UriKind.Absolute, out u) || u.Scheme != Uri.UriSchemeHttps)) p.Add(at + ": url muss eine https-Adresse sein.");
            if (hasPath && SplitRelPath(rel) == null) p.Add(at + ": path muss ein relativer Pfad im Vorlagen-Ordner sein (ohne .., ohne Laufwerk), z.B. files/skript.ps1.");
            if (f["size"] != null && f["size"].Type != JTokenType.Null && (f["size"].Type != JTokenType.Integer || (long)f["size"] <= 0)) p.Add(at + ": size muss eine positive Zahl sein.");
        }
    }

    // Wie AcmpCcWriter.CheckFileName: so legt die Console Dateien in "private" ab
    static bool ValidFileName(string n)
    {
        return !string.IsNullOrEmpty(n) && n.Length <= 200 && n.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && n != "." && n != ".." &&
               !n.EndsWith(".") && !n.EndsWith(" ") && !n.EndsWith(".mcp-upload", StringComparison.OrdinalIgnoreCase);
    }

    // "files/skript.ps1" -> ["files","skript.ps1"]; null = kein sicherer relativer Pfad
    static string[] SplitRelPath(string rel)
    {
        if (string.IsNullOrEmpty(rel) || rel.Length > 260) return null;
        string[] parts = rel.Replace('\\', '/').Split('/');
        foreach (string s in parts)
            if (s.Length == 0 || s == "." || s == ".." || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || s.EndsWith(".") || s.EndsWith(" ")) return null;
        return parts;
    }

    static Dictionary<string, string> ResolveParams(JObject tpl, JObject given)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var declared = new HashSet<string>(StringComparer.Ordinal);
        var missing = new List<string>();
        JArray ps = tpl["parameters"] as JArray;
        if (ps != null)
            foreach (JToken t in ps)
            {
                JObject pd = t as JObject; if (pd == null) continue;
                string n = (string)pd["name"]; if (string.IsNullOrEmpty(n)) continue;
                declared.Add(n);
                string v = given != null && given[n] != null && given[n].Type != JTokenType.Null ? given[n].ToString() : (string)pd["default"];
                bool required = pd["required"] != null && pd["required"].Type == JTokenType.Boolean && (bool)pd["required"];
                if (v == null || (required && v.Trim().Length == 0)) missing.Add(n + " (" + ((string)pd["description"] ?? "") + ")");
                else values[n] = v;
            }
        var unknown = new List<string>();
        if (given != null) foreach (JProperty pr in given.Properties()) if (!declared.Contains(pr.Name)) unknown.Add(pr.Name);
        if (unknown.Count > 0) throw new Exception("Unbekannte Parameter: " + string.Join(", ", unknown.ToArray()) + ". Die Vorlage kennt: " + (declared.Count > 0 ? string.Join(", ", new List<string>(declared).ToArray()) : "keine"));
        if (missing.Count > 0) throw new Exception("Fehlende Parameter:\n- " + string.Join("\n- ", missing.ToArray()));
        return values;
    }

    // ================= Individuelle Felder =================

    Dictionary<string, CfInfo> ResolveCustomFields(JObject tpl, List<string> hints)
    {
        var res = new Dictionary<string, CfInfo>(StringComparer.Ordinal);
        JArray cfl = tpl["requires"] != null ? tpl["requires"]["customFields"] as JArray : null;
        if (cfl == null || cfl.Count == 0) return res;
        // Welche Eigenschaften brauchen die Schritte je Feld?
        var used = new List<string>(); CollectStrings(tpl["steps"], used);
        var needs = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (string s in used)
            foreach (Match m in CfRx.Matches(s))
            {
                HashSet<string> hs;
                if (!needs.TryGetValue(m.Groups[1].Value, out hs)) { hs = new HashSet<string>(); needs[m.Groups[1].Value] = hs; }
                hs.Add(m.Groups[2].Value);
            }
        if (string.IsNullOrEmpty(_readCs)) throw new Exception("Die Vorlage braucht individuelle Felder, aber der DB-Lesezugang (db.connectionString) fehlt.");
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            foreach (JToken t in cfl)
            {
                JObject want = t as JObject; if (want == null) continue;
                string name = (string)want["name"]; if (string.IsNullOrEmpty(name)) continue;
                string wTable = (string)want["table"], wFolder = (string)want["folder"];
                var cands = new List<CfInfo>();
                var q = c.CreateCommand();
                q.CommandText = "SELECT CAST(CustomFieldId AS varchar(36)), CAST(TableId AS varchar(36)), InputMethod, MaxLength FROM VW_SYS_CustomFields WHERE Caption_de=@n OR Caption_en=@n";
                q.Parameters.AddWithValue("@n", name);
                using (var r = q.ExecuteReader())
                    while (r.Read())
                    {
                        var ci = new CfInfo();
                        ci.Name = name;
                        ci.FieldID = "{" + r.GetString(0).ToUpperInvariant() + "}";
                        ci.TableID = "{" + r.GetString(1).ToUpperInvariant() + "}";
                        int im = Convert.ToInt32(r.GetValue(2)); string tn;
                        ci.Type = InputMethods.TryGetValue(im, out tn) ? tn : "InputMethod " + im;
                        ci.MaxLength = Convert.ToInt32(r.GetValue(3));
                        cands.Add(ci);
                    }
                foreach (var ci in cands)
                {
                    // Tabelle: SYS_TableRelations (TableID -> TableDisplayName, FolderId); Ordner: SYS_Folders (FolderID -> Caption, ParentID)
                    string folderId;
                    ci.Table = TableCaption(c, ci.TableID, out folderId);
                    if (ci.Table == null) ci.Diag = "Tabelle " + ci.TableID + " nicht in SYS_TableRelations gefunden";
                    else if (folderId == null) ci.Diag = "Tabelle " + ci.TableID + " ('" + ci.Table + "') hat in SYS_TableRelations keine FolderId";
                    else
                    {
                        ci.FolderID = folderId;
                        string pp; ci.Folder = FolderCaption(c, folderId, out pp);
                        ci.FolderParentID = pp;
                        if (ci.Folder == null) ci.Diag = "Ordner " + folderId + " (FolderId der Tabelle '" + ci.Table + "') nicht in SYS_Folders gefunden";
                        else if (pp == null) ci.Diag = "Ordner " + folderId + " ('" + ci.Folder + "') hat in SYS_Folders keine ParentID";
                    }
                    string hex = ci.TableID.Trim('{', '}').Replace("-", "");
                    ci.Tablename = "CF_VAL_" + hex.Substring(0, 24);
                    ci.Fieldname = "CF_" + ci.FieldID.Trim('{', '}').Replace("-", "").Substring(0, 28);
                }
                var hits = cands.FindAll(h =>
                    (string.IsNullOrEmpty(wTable) || h.Table == null || string.Equals(h.Table, wTable, StringComparison.OrdinalIgnoreCase)) &&
                    (string.IsNullOrEmpty(wFolder) || h.Folder == null || string.Equals(h.Folder, wFolder, StringComparison.OrdinalIgnoreCase)));
                string where = (string.IsNullOrEmpty(wFolder) ? "" : wFolder + " > ") + (string.IsNullOrEmpty(wTable) ? "" : wTable);
                if (hits.Count == 0)
                {
                    var found = new List<string>();
                    foreach (var ci in cands) found.Add((ci.Folder ?? "?") + " > " + (ci.Table ?? "?") + " " + ci.FieldID);
                    throw new Exception("Individuelles Feld '" + name + "'" + (where.Length > 0 ? " (" + where + ")" : "") + " gibt es in dieser ACMP-Installation nicht. Bitte zuerst in der Console anlegen" +
                        (want["type"] != null ? ", Typ " + (string)want["type"] : "") + (want["maxLength"] != null ? ", Laenge " + want["maxLength"] : "") + "." +
                        (found.Count > 0 ? " Gleichnamige Felder an anderer Stelle: " + string.Join("; ", found.ToArray()) + "." : "") + " Nichts angelegt.");
                }
                if (hits.Count > 1)
                    throw new Exception("Individuelles Feld '" + name + "' ist nicht eindeutig (" + hits.Count + " Treffer). Die Vorlage sollte requires.customFields[].table/folder angeben. Nichts angelegt.");
                CfInfo hit = hits[0];
                HashSet<string> need;
                if (needs.TryGetValue(name, out need))
                    foreach (string pr in need)
                        if (hit.Get(pr) == null) throw new Exception("Fuer das Feld '" + name + "' fehlt " + pr + " (" + (hit.Diag ?? "Ursache unbekannt") + "). Nichts angelegt.");
                if (want["maxLength"] != null && want["maxLength"].Type == JTokenType.Integer && (int)want["maxLength"] != hit.MaxLength)
                    hints.Add("Feld '" + name + "': Laenge in dieser Installation " + hit.MaxLength + ", in der Vorlage " + (int)want["maxLength"] + ".");
                if (want["type"] != null && hit.Type != null && !string.Equals((string)want["type"], hit.Type, StringComparison.OrdinalIgnoreCase))
                    hints.Add("Feld '" + name + "': Typ in dieser Installation " + hit.Type + ", in der Vorlage " + (string)want["type"] + ".");
                res[name] = hit;
            }
        }
        return res;
    }

    // Tabelle eines individuellen Felds: SYS_TableRelations (IsCustomTable); null = nicht gefunden.
    // Die Tabelle steht NICHT in SYS_Folders (am 13.10.2026 am Feld TeamViewerID festgestellt).
    static string TableCaption(SqlConnection c, string id, out string folderId)
    {
        folderId = null;
        var q = c.CreateCommand();
        q.CommandText = "SELECT TOP 1 TableDisplayName, CAST(FolderId AS varchar(36)) FROM SYS_TableRelations WHERE TableID=@id ORDER BY CASE WHEN FolderId IS NULL THEN 1 ELSE 0 END";
        q.Parameters.AddWithValue("@id", new Guid(id.Trim('{', '}')));
        using (var r = q.ExecuteReader())
        {
            if (!r.Read()) return null;
            if (!r.IsDBNull(1)) folderId = "{" + r.GetString(1).ToUpperInvariant() + "}";
            return r.IsDBNull(0) ? "" : r.GetString(0);
        }
    }

    // Ordner der individuellen Felder (vermutlich SYS_Folders, noch zu bestaetigen); null = nicht gefunden
    static string FolderCaption(SqlConnection c, string id, out string parent)
    {
        parent = null;
        var q = c.CreateCommand();
        q.CommandText = "SELECT Caption, CAST(ParentID AS varchar(36)) FROM SYS_Folders WHERE FolderID=@id";
        q.Parameters.AddWithValue("@id", new Guid(id.Trim('{', '}')));
        using (var r = q.ExecuteReader())
        {
            if (!r.Read()) return null;
            if (!r.IsDBNull(1)) parent = "{" + r.GetString(1).ToUpperInvariant() + "}";
            return r.IsDBNull(0) ? "" : r.GetString(0);
        }
    }

    // ================= Platzhalter =================

    static void Substitute(JToken t, Dictionary<string, string> vals, string ga, Dictionary<string, CfInfo> cfs, bool xmlEscape)
    {
        JObject o = t as JObject;
        if (o != null)
        {
            foreach (JProperty p in new List<JProperty>(o.Properties()))
            {
                if (p.Value.Type == JTokenType.String) p.Value = new JValue(Replace((string)p.Value, vals, ga, cfs, p.Name == "xml"));
                else Substitute(p.Value, vals, ga, cfs, xmlEscape);
            }
            return;
        }
        JArray a = t as JArray;
        if (a != null)
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].Type == JTokenType.String) a[i] = new JValue(Replace((string)a[i], vals, ga, cfs, xmlEscape));
                else Substitute(a[i], vals, ga, cfs, xmlEscape);
            }
    }

    static string Replace(string s, Dictionary<string, string> vals, string ga, Dictionary<string, CfInfo> cfs, bool xmlEscape)
    {
        if (s.IndexOf("{{", StringComparison.Ordinal) < 0) return s;
        s = ParamRx.Replace(s, m =>
        {
            string n = m.Groups[1].Value, v;
            if (n == GlobalAccount) return ga ?? m.Value;
            if (!vals.TryGetValue(n, out v)) return m.Value;
            return xmlEscape ? v.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;") : v;
        });
        s = CfRx.Replace(s, m =>
        {
            CfInfo c;
            if (!cfs.TryGetValue(m.Groups[1].Value, out c)) return m.Value;
            return c.Get(m.Groups[2].Value) ?? m.Value;
        });
        return s;
    }

    static void FindPlaceholders(JToken t, List<string> outList)
    {
        var all = new List<string>(); CollectStrings(t, all);
        foreach (string s in all) foreach (Match m in AnyPh.Matches(s)) if (!outList.Contains(m.Value)) outList.Add(m.Value);
    }

    static void CollectStrings(JToken t, List<string> outList)
    {
        if (t == null) return;
        if (t.Type == JTokenType.String) { outList.Add((string)t); return; }
        foreach (JToken k in t.Children()) CollectStrings(k, outList);
    }

    // Vereinigt die Eintraege eines Stored*-JSON aller Schritte (Schluessel Id) zu {"<key>":[...]}; null = keine
    internal static string CollectStored(JArray steps, string key)
    {
        var list = new JArray(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectStoredRec(steps, key, list, seen);
        if (list.Count == 0) return null;
        var o = new JObject(); o[key] = list;
        return o.ToString(Formatting.None);
    }

    static void CollectStoredRec(JArray steps, string key, JArray list, HashSet<string> seen)
    {
        foreach (JToken t in steps)
        {
            JObject s = t as JObject; if (s == null) continue;
            JObject f = s["fields"] as JObject;
            if (f != null && f[key] != null && f[key].Type == JTokenType.String && ((string)f[key]).Trim().Length > 0)
            {
                JObject j;
                try { j = JObject.Parse((string)f[key]); } catch { throw new Exception("Feld " + key + " ist kein gueltiges JSON."); }
                JArray arr = j[key] as JArray;
                if (arr != null) foreach (JToken e in arr) { string id = e["Id"] != null ? (string)e["Id"] : e.ToString(); if (seen.Add(id)) list.Add(e.DeepClone()); }
            }
            JArray ch = s["children"] as JArray;
            if (ch != null) CollectStoredRec(ch, key, list, seen);
        }
    }

    // ================= Dateien =================

    // Holt jede Datei in den Zwischenspeicher <cacheDir>\<sha256> und prueft die Pruefsumme. Liegt sie dort schon
    // (z.B. seit dem dryRun), wird nur nachgerechnet. Weicht eine Datei ab, wird abgebrochen - vor dem Anlegen.
    List<CtlFile> FetchFiles(JArray fl, string baseLoc, List<string> notes)
    {
        Directory.CreateDirectory(_cacheDir);
        var list = new List<CtlFile>();
        foreach (JToken t in fl)
        {
            JObject f = (JObject)t;
            var cf = new CtlFile();
            cf.Name = (string)f["name"];
            cf.Sha256 = ((string)f["sha256"]).ToLowerInvariant();
            cf.CachePath = Path.Combine(_cacheDir, cf.Sha256);
            string url = (string)f["url"], rel = (string)f["path"], err = null;
            cf.Origin = !string.IsNullOrEmpty(url) ? url : ResolveRepoPath(baseLoc, rel, out err);
            if (File.Exists(cf.CachePath) && FileSha(cf.CachePath) == cf.Sha256)
            {
                cf.FromCache = true;
                if (cf.Origin == null) cf.Origin = "Zwischenspeicher (" + rel + ")";
            }
            else
            {
                if (cf.Origin == null) throw new Exception("Datei '" + cf.Name + "': " + err + " Nichts angelegt.");
                if (File.Exists(cf.CachePath)) File.Delete(cf.CachePath);   // beschaedigt oder fremd
                string part = cf.CachePath + "." + Guid.NewGuid().ToString("N") + ".part";
                try
                {
                    if (cf.Origin.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) DownloadFile(cf.Origin, part, _maxFileBytes);
                    else
                    {
                        var fi = new FileInfo(cf.Origin);
                        if (!fi.Exists) throw new Exception("Datei '" + cf.Name + "' nicht gefunden: " + cf.Origin + ". Nichts angelegt.");
                        if (fi.Length > _maxFileBytes) throw new Exception("Datei '" + cf.Name + "' ist groesser als " + (_maxFileBytes / 1048576) + " MB (ccWriter.maxFileMB). Nichts angelegt.");
                        File.Copy(cf.Origin, part);
                    }
                    string got = FileSha(part);
                    if (got != cf.Sha256)
                        throw new Exception("Datei '" + cf.Name + "' von " + cf.Origin + ": SHA-256 weicht ab (Vorlage " + cf.Sha256 + ", geladen " + got + "). " +
                            "Der Hersteller hat die Datei vermutlich ersetzt - die Vorlage muss aktualisiert werden. Nichts angelegt.");
                    try { File.Move(part, cf.CachePath); }
                    catch (IOException) { if (!File.Exists(cf.CachePath)) throw; }   // parallel schon abgelegt
                }
                finally { if (File.Exists(part)) File.Delete(part); }
            }
            cf.Bytes = new FileInfo(cf.CachePath).Length;
            if (cf.Bytes > _maxFileBytes) throw new Exception("Datei '" + cf.Name + "' ist groesser als " + (_maxFileBytes / 1048576) + " MB (ccWriter.maxFileMB). Nichts angelegt.");
            if (f["size"] != null && f["size"].Type == JTokenType.Integer && (long)f["size"] != cf.Bytes)
                notes.Add("Datei '" + cf.Name + "': Groesse laut Vorlage " + (long)f["size"] + " Byte, tatsaechlich " + cf.Bytes + " Byte (SHA-256 stimmt).");
            list.Add(cf);
        }
        return list;
    }

    // path einer Datei im Vorlagen-Ordner -> lokaler Pfad bzw. https-Adresse; null + err, wenn nicht aufloesbar.
    // Lokal nur innerhalb einer konfigurierten Quelle (ctl.sources), damit eine Anfrage keine anderen Server-Dateien anhaengen kann.
    string ResolveRepoPath(string baseLoc, string rel, out string err)
    {
        err = null;
        string[] parts = SplitRelPath(rel);
        if (parts == null) { err = "path '" + rel + "' ist ungueltig."; return null; }
        if (string.IsNullOrEmpty(baseLoc)) { err = "path '" + rel + "' geht nur bei Vorlagen aus einer Quelle (id), nicht bei einer inline uebergebenen Vorlage."; return null; }
        if (baseLoc.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var esc = new List<string>(); foreach (string s in parts) esc.Add(Uri.EscapeDataString(s));
            return baseLoc.TrimEnd('/') + "/" + string.Join("/", esc.ToArray());
        }
        if (baseLoc.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) { err = "nur https ist erlaubt."; return null; }
        string dir = Path.GetFullPath(baseLoc).TrimEnd('\\');
        bool inSource = false;
        foreach (string src in _sources)
            if (!src.StartsWith("http", StringComparison.OrdinalIgnoreCase) && dir.StartsWith(Path.GetFullPath(src).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) inSource = true;
        if (!inSource) { err = "Vorlagen-Ordner " + dir + " liegt in keiner Quelle aus ctl.sources."; return null; }
        string full = Path.GetFullPath(Path.Combine(dir, string.Join("\\", parts)));
        if (!full.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase)) { err = "path '" + rel + "' zeigt aus dem Vorlagen-Ordner heraus."; return null; }
        return full;
    }

    // Download nur ueber https, Weiterleitungen von Hand (keine Umleitung auf http), Groesse begrenzt
    static void DownloadFile(string url, string dest, long max)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        string cur = url;
        for (int hop = 0; hop < 6; hop++)
        {
            if (!cur.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) throw new Exception("Download " + url + ": Weiterleitung auf " + cur + " abgelehnt (nur https). Nichts angelegt.");
            var req = (HttpWebRequest)WebRequest.Create(cur);
            req.AllowAutoRedirect = false; req.Timeout = 30000; req.ReadWriteTimeout = 60000; req.UserAgent = "acmp-mcp-ctl";
            HttpWebResponse resp;
            try { resp = (HttpWebResponse)req.GetResponse(); }
            catch (WebException ex)
            {
                resp = ex.Response as HttpWebResponse;
                if (resp == null) throw new Exception("Download " + cur + ": " + ex.Message + " Nichts angelegt.");
            }
            using (resp)
            {
                int code = (int)resp.StatusCode;
                if (code >= 300 && code < 400)
                {
                    string loc = resp.Headers["Location"];
                    if (string.IsNullOrEmpty(loc)) throw new Exception("Download " + cur + ": HTTP " + code + " ohne Ziel. Nichts angelegt.");
                    cur = new Uri(new Uri(cur), loc).AbsoluteUri;
                    continue;
                }
                if (code != 200) throw new Exception("Download " + cur + ": HTTP " + code + " " + resp.StatusDescription + ". Nichts angelegt.");
                if (resp.ContentLength > max) throw new Exception("Download " + cur + ": " + (resp.ContentLength / 1048576) + " MB, erlaubt sind " + (max / 1048576) + " MB (ccWriter.maxFileMB). Nichts angelegt.");
                using (var s = resp.GetResponseStream())
                using (var o = File.Create(dest))
                {
                    var buf = new byte[81920]; long total = 0; int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0)
                    {
                        total += n;
                        if (total > max) throw new Exception("Download " + cur + ": groesser als " + (max / 1048576) + " MB (ccWriter.maxFileMB). Nichts angelegt.");
                        o.Write(buf, 0, n);
                    }
                }
                return;
            }
        }
        throw new Exception("Download " + url + ": zu viele Weiterleitungen. Nichts angelegt.");
    }

    // Abgleich Dateien <-> Schritte (nur Hinweise): private\<name> in TDownloadFile o.ae.; Variablen (%...%) werden nicht geprueft
    static void CheckFileRefs(JArray steps, List<CtlFile> files, List<string> notes)
    {
        var all = new List<string>(); CollectStrings(steps, all);
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string s in all) foreach (Match m in PrivateRef.Matches(s)) refs.Add((m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim());
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            names.Add(f.Name);
            bool used = false;
            foreach (string s in all) if (s.IndexOf("private\\" + f.Name, StringComparison.OrdinalIgnoreCase) >= 0) { used = true; break; }
            if (!used) notes.Add("Datei '" + f.Name + "' wird von keinem Schritt geladen (fehlt ein Schritt 'Datei herunterladen' mit private\\" + f.Name + "?).");
        }
        foreach (string r in refs)
            if (r.IndexOf('%') < 0 && !names.Contains(r)) notes.Add("Ein Schritt laedt private\\" + r + ", die Vorlage bringt diese Datei aber nicht mit.");
    }

    static string FileSha(string path)
    {
        using (var s = File.OpenRead(path)) using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
    }

    // ================= Hilfen =================

    void LogInstall(JObject res, JObject info, string requester)
    {
        lock (_lock)
        {
            JArray a = File.Exists(_logFile) ? JArray.Parse(File.ReadAllText(_logFile)) : new JArray();
            var e = new JObject();
            e["scriptId"] = res["scriptId"]; e["name"] = res["name"]; e["template"] = info["template"];
            e["sha256"] = info["sha256"]; e["source"] = info["source"]; e["requester"] = requester; e["installed"] = DateTime.Now.ToString("s");
            if (info["files"] != null) e["files"] = info["files"];
            if (res["dateienFehler"] != null) e["dateienFehler"] = res["dateienFehler"];
            a.Add(e);
            File.WriteAllText(_logFile, a.ToString(Formatting.Indented), new UTF8Encoding(false));
        }
    }

    static int CountSteps(JArray steps)
    {
        int n = 0;
        foreach (JToken t in steps) { n++; JArray ch = t["children"] as JArray; if (ch != null) n += CountSteps(ch); }
        return n;
    }

    static string Sha256Hex(string s)
    {
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(new UTF8Encoding(false).GetBytes(s ?? ""))).Replace("-", "").ToLowerInvariant();
    }

    static string GuidBraces(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        Guid g; if (!Guid.TryParse(s.Trim().Trim('{', '}'), out g)) return null;
        return "{" + g.ToString().ToUpperInvariant() + "}";
    }
}
