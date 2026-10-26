// AcmpActivity: Aktivitaetsprotokoll und Notbremse des ACMP-MCP-Servers (seit 22.10.2026, ersetzt die Vier-Augen-Freigabe).
// - Jede Schreib-, Ausfuehr- und Loeschaktion ueber den MCP steht als eine JSON-Zeile in activity-JJJJ-MM.jsonl (Ordner der Config):
//   Zeit, Bediener, Tool, Art, Absicht (intent), Ziel, Details, Ergebnis, Dauer. Aufrufe mit dryRun=true schreiben nichts und fehlen hier.
// - Notbremse (notbremse.json): sperrt alle Schreib-, Ausfuehr- und Loesch-Tools sofort; Lesen und dryRun laufen weiter.
//   Ziehen und Loesen nur ueber die Webseite (Mensch im Browser), nie ueber das LLM. Ist die Datei unlesbar, gilt die Notbremse als gezogen.
// net48 / C# 5 (csc v4.0.30319): keine string-Interpolation, keine expression-bodied member, kein ?.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

enum RiskTier { Read, Write, Destructive }

class AcmpActivity
{
    readonly string _dir;
    readonly string _brakeFile;
    readonly Action<string> _log;
    readonly object _fileLock = new object();    // Anhaengen an die Monatsdatei
    readonly object _brakeLock = new object();   // Schreiben von notbremse.json
    volatile JObject _brake;   // null = Notbremse aus; jede Pruefung liest nur die Referenz (kein Warten auf das Protokoll)

    // Optionale Aufloeser GUID -> lesbarer Name (vom Server gesetzt) fuer die Spalte "Ziel"
    public Func<string, string> ResolveClientName;
    public Func<string, string> ResolveCommandName;

    static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
    static readonly string[] ReadPrefixes = new string[] {
        "get", "list", "enum", "find", "search", "query", "describe",
        "has", "is", "check", "count", "export", "download", "read", "load"
    };
    static readonly string[] DestructivePrefixes = new string[] {
        "delete", "remove", "uninstall", "reset", "wipe", "clear", "purge",
        "drop", "format", "shutdown", "reboot", "restart", "kill", "revoke", "erase"
    };
    // Argumente, die das Ziel einer Aktion benennen (Reihenfolge = Reihenfolge in der Spalte "Ziel")
    static readonly string[] TargetKeys = new string[] {
        "computerName", "client", "clientId", "clientIds", "container", "containerId", "containerIds", "command", "clientCommandId",
        "scriptId", "scriptIds", "script", "name", "folder", "targetFolder", "folderId", "group", "groupId", "item", "itemId", "items",
        "updateIds", "updateId", "updates", "ids", "id", "ticketId", "assetId", "contactId", "jobId", "jobIds", "table", "backup", "undo",
        "package", "packages", "clients"   // Managed Software (acmp_msw_*, 24.10.2026)
    };
    // Argumente, die zu gross oder zu heikel fuer das Protokoll sind: nur ihre Groesse wird vermerkt
    static readonly string[] BulkKeys = new string[] { "steps", "variables", "content", "template", "fields", "params", "parameters", "image", "data", "filter" };
    // Argumente, die den eigentlichen Befehl tragen: laenger aufheben
    static readonly string[] PayloadKeys = new string[] { "inner", "rawIcql" };
    static readonly Regex InnerClientRx = new Regex("<ClientId>([^<]+)</ClientId>", RegexOptions.IgnoreCase);
    static readonly Regex SecretKey = new Regex("pass|kennwort|secret|token|apikey|api_key", RegexOptions.IgnoreCase);
    static readonly Regex GuidRx = new Regex(@"^\{?[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}?$");

    public AcmpActivity(string cfgDir, Action<string> log)
    {
        _dir = cfgDir; _log = log;
        _brakeFile = Path.Combine(cfgDir, "notbremse.json");
        try
        {
            if (File.Exists(_brakeFile))
            {
                JObject b = JObject.Parse(File.ReadAllText(_brakeFile, Encoding.UTF8));
                if (b["active"] != null && b["active"].Type == JTokenType.Boolean && (bool)b["active"]) _brake = b;
            }
        }
        catch (Exception ex)
        {
            _brake = new JObject();
            _brake["active"] = true; _brake["by"] = "Server"; _brake["utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            _brake["reason"] = "notbremse.json nicht lesbar (" + ex.Message + ")";
            _log("Notbremse: notbremse.json nicht lesbar (" + ex.Message + ") - gilt als GEZOGEN, bis sie auf der Webseite geloest wird.");
        }
    }

    // ---------- Art eines ACMP-Kommandos am Namen (Get*/List* lesen, Delete*/Remove* loeschen, sonst schreiben) ----------
    public static RiskTier CommandTier(string command)
    {
        if (string.IsNullOrEmpty(command)) return RiskTier.Write;
        string c = command.Trim().ToLowerInvariant();
        foreach (string p in DestructivePrefixes) if (c.StartsWith(p, StringComparison.Ordinal)) return RiskTier.Destructive;
        foreach (string p in ReadPrefixes) if (c.StartsWith(p, StringComparison.Ordinal)) return RiskTier.Read;
        return RiskTier.Write;
    }

    // ---------- Notbremse ----------
    public bool BrakeActive { get { return _brake != null; } }

    public JObject BrakeInfo() { JObject b = _brake; return b != null ? (JObject)b.DeepClone() : null; }

    public string BrakeMessage(string publicUrl)
    {
        JObject b = BrakeInfo();
        if (b == null) return "";
        return "NOTBREMSE GEZOGEN seit " + LocalTime((string)b["utc"]) + " von " + (string)b["by"] +
            (b["reason"] != null && ((string)b["reason"]).Length > 0 ? " (" + (string)b["reason"] + ")" : "") +
            ". Schreib-, Ausfuehr- und Loesch-Tools sind gesperrt - es wurde NICHTS ausgefuehrt. Lesen und dryRun gehen weiter. " +
            "Loesen kann nur ein Mensch auf der Webseite " + publicUrl + "/settings (nicht ueber das LLM).";
    }

    // Zieht bzw. loest die Notbremse. Schlaegt das Speichern fehl, bleibt der alte Zustand und die Ausnahme geht an die Webseite.
    public void SetBrake(bool on, string by, string reason)
    {
        var b = new JObject();
        b["active"] = on;
        b["by"] = by;
        b["utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        b["reason"] = reason != null ? reason : "";
        lock (_brakeLock)
        {
            File.WriteAllText(_brakeFile, b.ToString(Formatting.Indented), Utf8);
            _brake = on ? b : null;
        }
        _log("Notbremse " + (on ? "GEZOGEN" : "geloest") + " von " + by + (string.IsNullOrEmpty(reason) ? "" : ": " + reason));
        Record(by, on ? "Notbremse gezogen" : "Notbremse geloest", "Webseite", reason, null, null, true, null, 0);
    }

    // ---------- Protokoll schreiben ----------
    // Abgeschlossener Eintrag (abgewiesene Aufrufe, Aktionen auf der Webseite). false = konnte nicht geschrieben werden.
    public bool Record(string user, string tool, string kind, string intent, string target, string details, bool ok, string result, long ms)
    {
        JObject e = Entry(user, tool, kind, intent, target, details);
        e["ok"] = ok;
        if (!string.IsNullOrEmpty(result)) e["result"] = Truncate(result, 1500);
        e["ms"] = ms;
        bool written = Append(e);
        _log("AKTIVITAET " + e["user"] + " " + tool + " " + (ok ? "ok" : "FEHLER") + (string.IsNullOrEmpty(target) ? "" : " | " + Truncate(target, 160)));
        return written;
    }

    // Vor einer Schreibaktion: Eintrag "laeuft". Laesst er sich nicht schreiben, wird die Aktion nicht ausgefuehrt (Ausnahme).
    // Gibt die Id fuer End() zurueck; ein Eintrag ohne Abschluss (Absturz, Neustart) bleibt im Protokoll als "offen" sichtbar.
    public string Begin(string user, string tool, string kind, string intent, string target, string details)
    {
        JObject e = Entry(user, tool, kind, intent, target, details);
        string id = Guid.NewGuid().ToString("N");
        e["id"] = id; e["phase"] = "start";
        if (!Append(e)) throw new Exception("Aktivitaetsprotokoll laesst sich nicht schreiben (Platte voll, Datei gesperrt?) - aus Sicherheitsgruenden wurde NICHTS ausgefuehrt. Bitte server.log pruefen.");
        return id;
    }

    // Nach der Schreibaktion: vollstaendiger Eintrag mit Ergebnis (Fehler beim Schreiben nur ins server.log - die Aktion ist schon gelaufen).
    public void End(string id, string user, string tool, string kind, string intent, string target, string details, bool ok, string result, long ms)
    {
        JObject e = Entry(user, tool, kind, intent, target, details);
        e["id"] = id; e["phase"] = "end";
        e["ok"] = ok;
        if (!string.IsNullOrEmpty(result)) e["result"] = Truncate(result, 1500);
        e["ms"] = ms;
        Append(e);
        _log("AKTIVITAET " + e["user"] + " " + tool + " " + (ok ? "ok" : "FEHLER") + (string.IsNullOrEmpty(target) ? "" : " | " + Truncate(target, 160)));
    }

    static JObject Entry(string user, string tool, string kind, string intent, string target, string details)
    {
        var e = new JObject();
        e["utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        e["zeit"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        e["user"] = user != null ? user : "(unbekannt)";
        e["tool"] = tool;
        e["kind"] = kind;
        if (!string.IsNullOrEmpty(intent)) e["intent"] = Truncate(intent, 1000);
        if (!string.IsNullOrEmpty(target)) e["target"] = target;
        if (!string.IsNullOrEmpty(details)) e["details"] = details;
        return e;
    }

    bool Append(JObject e)
    {
        try { lock (_fileLock) { File.AppendAllText(FileFor(DateTime.Now), e.ToString(Formatting.None) + "\n", Utf8); } return true; }
        catch (Exception ex) { _log("Aktivitaetsprotokoll: Schreiben fehlgeschlagen: " + ex.Message); return false; }
    }

    string FileFor(DateTime d) { return Path.Combine(_dir, "activity-" + d.ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".jsonl"); }

    // ---------- Protokoll lesen (neueste zuerst, ueber die Monatsdateien hinweg) ----------
    // Je Aktion zaehlt der Abschluss-Eintrag; ein Start ohne Abschluss erscheint als offen (pending = true).
    // Gelesen wird ohne Sperre (ein gerade halb geschriebener Eintrag faellt beim Parsen heraus), Schreib-Tools warten also nie auf die Webseite.
    public List<JObject> Recent(int max)
    {
        var res = new List<JObject>();
        var ended = new HashSet<string>(StringComparer.Ordinal);
        string[] files;
        try { files = Directory.GetFiles(_dir, "activity-*.jsonl"); } catch { return res; }
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        for (int i = files.Length - 1; i >= 0 && res.Count < max; i--)
        {
            string[] lines;
            try
            {
                using (var fs = new FileStream(files[i], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var rd = new StreamReader(fs, Encoding.UTF8))
                    lines = rd.ReadToEnd().Split('\n');
            }
            catch { continue; }
            for (int j = lines.Length - 1; j >= 0 && res.Count < max; j--)
            {
                if (lines[j].Trim().Length == 0) continue;
                JObject e;
                try { e = JObject.Parse(lines[j]); } catch { continue; }
                string id = e["id"] != null ? (string)e["id"] : null;
                string phase = e["phase"] != null ? (string)e["phase"] : null;
                if (phase == "end" && id != null) ended.Add(id);
                if (phase == "start")
                {
                    if (id != null && ended.Contains(id)) continue;
                    e["pending"] = true; e["ok"] = false;
                    e["result"] = "Kein Ergebnis protokolliert - laeuft noch oder wurde abgebrochen (Neustart, Absturz). Im Zweifel in ACMP pruefen.";
                }
                res.Add(e);
            }
        }
        return res;
    }

    // ---------- Ziel und Details einer Aktion aus den Argumenten ----------
    public void Describe(string tool, JObject a, out string target, out string details)
    {
        target = null; details = null;
        if (a == null) return;
        var parts = new List<string>();
        foreach (string k in TargetKeys)
        {
            JToken t = a[k];
            if (t == null || t.Type == JTokenType.Null) continue;
            string v = TargetValue(k, t);
            if (!string.IsNullOrEmpty(v)) parts.Add(k + "=" + v);
            if (parts.Count >= 5) break;
        }
        if (tool == "acmp_call" && a["command"] == null && a["rawIcql"] != null) parts.Add("rawIcql");
        if (a["inner"] != null && a["inner"].Type == JTokenType.String)
        {
            Match cm = InnerClientRx.Match((string)a["inner"]);
            if (cm.Success) parts.Add("clientId=" + ResolveName("clientId", cm.Groups[1].Value));
        }
        if (parts.Count > 0) target = Truncate(string.Join(", ", parts.ToArray()), 300);

        var d = new JObject();
        foreach (JProperty p in a.Properties())
        {
            if (p.Name == "intent" || p.Name.StartsWith("_")) continue;
            if (SecretKey.IsMatch(p.Name)) { d[p.Name] = "***"; continue; }
            if (Array.IndexOf(BulkKeys, p.Name) >= 0) { d[p.Name] = Size(p.Value); continue; }
            if (p.Name == "script" && tool == "acmp_run_powershell") { d[p.Name] = Truncate(MaskText(p.Value.ToString()), 4000); continue; }
            if (Array.IndexOf(PayloadKeys, p.Name) >= 0) { d[p.Name] = Truncate(MaskText(p.Value.ToString()), 2000); continue; }
            d[p.Name] = Shorten(MaskDeep(p.Value.DeepClone()));
        }
        if (d.Count > 0) details = Truncate(d.ToString(Formatting.None), 6000);
    }

    // Kennwoerter nicht ins Protokoll: Werte heikler XML-Elemente (<Password>..</Password>) und PowerShell-Parameter (-Password 'x').
    // Faengt nicht jede Schreibweise ab - Kennwoerter gehoeren ohnehin nicht in Skripte (1Password, gespeicherte Konten in ACMP).
    static readonly Regex SecretXml = new Regex(@"(?<o><(?<n>[A-Za-z_:]*(?:pass|kennwort|secret|token|apikey)[A-Za-z_:]*)(?:\s[^>]*)?>)[^<]*(?<c></\k<n>>)", RegexOptions.IgnoreCase);
    static readonly Regex SecretArg = new Regex(@"(?<a>-[A-Za-z]*(?:pass|kennwort|secret|token|apikey)[A-Za-z]*\s+)(?:""[^""]*""|'[^']*'|[^\s;|)]+)", RegexOptions.IgnoreCase);
    public static string MaskText(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        s = SecretXml.Replace(s, "${o}***${c}");
        return SecretArg.Replace(s, "${a}***");
    }

    // Verschachtelte Argumente: Felder mit heiklem Namen maskieren, Texte wie oben (t ist eine Kopie, nie das echte Argument)
    static JToken MaskDeep(JToken t)
    {
        JObject o = t as JObject;
        if (o != null)
        {
            foreach (JProperty p in new List<JProperty>(o.Properties())) { if (SecretKey.IsMatch(p.Name)) p.Value = "***"; else p.Value = MaskDeep(p.Value); }
            return o;
        }
        JArray arr = t as JArray;
        if (arr != null) { for (int i = 0; i < arr.Count; i++) arr[i] = MaskDeep(arr[i]); return arr; }
        if (t != null && t.Type == JTokenType.String) return MaskText((string)t);
        return t;
    }

    string TargetValue(string key, JToken t)
    {
        if (t.Type == JTokenType.Array)
        {
            var arr = (JArray)t;
            if (arr.Count == 0) return null;
            var names = new List<string>();
            for (int i = 0; i < arr.Count && i < 3; i++) names.Add(ResolveName(key, arr[i].Type == JTokenType.String ? (string)arr[i] : Truncate(arr[i].ToString(Formatting.None), 40)));
            return (arr.Count > 3 ? arr.Count + " (" + string.Join("; ", names.ToArray()) + "; ...)" : string.Join("; ", names.ToArray()));
        }
        if (t.Type == JTokenType.Object) return Truncate(MaskDeep(t.DeepClone()).ToString(Formatting.None), 60);
        return MaskText(ResolveName(key, t.ToString()));
    }

    string ResolveName(string key, string v)
    {
        if (v == null) return null;
        v = v.Trim();
        if (GuidRx.IsMatch(v))
        {
            string nm = null;
            try
            {
                if ((key == "clientCommandId" || key.StartsWith("script") || key == "command") && ResolveCommandName != null) nm = ResolveCommandName(v);
                else if (key.StartsWith("client") && ResolveClientName != null) nm = ResolveClientName(v);
            }
            catch { }
            if (!string.IsNullOrEmpty(nm)) return nm + " (" + v + ")";
        }
        return Truncate(v, 80);
    }

    static JToken Shorten(JToken t)
    {
        if (t.Type == JTokenType.String) return Truncate((string)t, 300);
        if (t.Type == JTokenType.Array || t.Type == JTokenType.Object)
        {
            string s = t.ToString(Formatting.None);
            return s.Length <= 300 ? t : (JToken)Truncate(s, 300);
        }
        return t;
    }

    static string Size(JToken t)
    {
        if (t == null || t.Type == JTokenType.Null) return "-";
        if (t.Type == JTokenType.Array) return "(" + ((JArray)t).Count + " Eintraege)";
        if (t.Type == JTokenType.Object) return "(" + ((JObject)t).Count + " Felder)";
        return "(" + t.ToString().Length + " Zeichen)";
    }

    public static string LocalTime(string utc)
    {
        DateTime d;
        if (utc != null && DateTime.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out d))
            return d.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
        return utc != null ? utc : "?";
    }

    static string Truncate(string s, int n) { if (s == null) return null; return s.Length <= n ? s : s.Substring(0, n) + "..."; }
}
