// AcmpRollout: Rollout-Stand eines Client Commands je Rechner fuer die Weboberflaeche (/dashboard/rollout, seit 23.10.2026).
// Nur lesen, ueber die Leseverbindung des Dashboards (db.connectionString). Quellen:
//   SYS_Jobs_Logs + SYS_Jobs_Logs_Items   Auftraege je Rechner, auch wartende (State 0 wartet, 1 zugewiesen, 2 laeuft, 3 fertig, 4 abgebrochen, 5 Timeout,
//                                         6 beim Agenten geplant - wartet auf die Startbedingung, z.B. Wartungsfenster; StateMsg leer)
//   CLT_SCRIPT_EXECRESULT                 Laeufe (EXECRESULT 0 = Erfolg; JobExecId = SYS_Jobs_Logs_Items.ExecId); XmlLogFile = Schritt-Protokoll (Raw-Deflate)
//   SYS_Jobs_Parameters_Declarations      Parameter des Commands (ihre Werte stehen im Protokoll unter "Logged Variables")
//   CLT_OPERATINGSYSTEM.LASTLOGGEDONUSER, VW_Inventory_ClientOnlineState (0 = online), CLT_Clients_LastConnection
// Ausgabe eines Laufs = Meldungen nach "ExitCode(n)" innerhalb eines Schritts; der Skripttext steht davor mit im Protokoll.
// Kennzeilen = Ausgabezeilen "SCHLUESSEL|..." (Konvention der MCP-Skripte, z.B. BUILD|25H2|26200.9457).
// Zusatz fuer MCP-Skripte mit Kopfzeile "MCPFIX|<Version>|<Rechner>|<Modus>|<Zeit>|...": Befund aus Modus (Pruefen/Fix), Exit-Code und
// ABBRUCH/FEHLER/FERTIG-Zeilen sowie Aenderungszeilen (KOPIERT, GEAENDERT, ...). Ohne MCPFIX zaehlt nur das Ergebnis aus ACMP.
// Zwischenspeicher 60 s je Command, Zeitraum und Filter.
// net48 / C# 5 (csc v4.0.30319): keine string-Interpolation, keine expression-bodied member, kein ?.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Newtonsoft.Json.Linq;

class RolloutRun
{
    public string ClientId, ExecId;
    public double RunDate;
    public int ExecResult, ScriptVersion;
    public byte[] Log;              // XmlLogFile, gepackt; null = kein Protokoll
}

class RolloutItem
{
    public string ClientId, ComputerName, ExecId, StateMsg, Creator, Condition;
    public int State, Result;
    public double Created, Started, LastActivity;
}

class RolloutClient
{
    public string ClientId, Name, User, LastContact;
    public int Online = 2;          // 0 online, 1 offline, 2 unbekannt
    public bool Retired;
}

class RolloutData
{
    public string CcId, Name;       // CcId "{GUID}" gross
    public int Version = -1, CcState = -1;
    public List<string> Params = new List<string>();
    public List<RolloutRun> Runs = new List<RolloutRun>();
    public List<RolloutItem> Items = new List<RolloutItem>();
    public Dictionary<string, RolloutClient> Clients = new Dictionary<string, RolloutClient>(StringComparer.OrdinalIgnoreCase);
    public bool RunsCut;
}

// Ein entpacktes Protokoll
class RolloutLog
{
    public Dictionary<string, string> Vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public List<string> Output = new List<string>();
    public int? ExitCode;
    public bool Ok;                 // Protokoll lesbar
    public bool Fallback;           // keine ExitCode-Marke gefunden: Kennzeilen aus allen Meldungen (letzter Treffer zaehlt)
    public string Error;
}

class AcmpRollout
{
    public const int MaxRuns = 3000, MaxOutputLines = 40, MaxHistory = 10, CacheSeconds = 60, DefaultDays = 14;
    const int SqlTimeout = 60;
    const int MaxLogBytes = 8 * 1024 * 1024;

    readonly string _cs;
    readonly Action<string> _log;
    readonly object _lock = new object();
    readonly Dictionary<string, KeyValuePair<DateTime, JObject>> _cache = new Dictionary<string, KeyValuePair<DateTime, JObject>>();
    public Func<string, DateTime, RolloutData> Loader;      // Standard: aus der DB; Tests setzen einen eigenen
    public Func<DateTime, JArray> ListLoader;

    public static readonly Regex GuidRe = new Regex(@"^\{?([0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})\}?$");
    static readonly Regex KeyRe = new Regex(@"^([A-Z][A-Z0-9_]{1,23})\|(.*)$");
    static readonly Regex VarRe = new Regex("^([A-Za-z0-9_]+) = \"(.*)\"$", RegexOptions.Singleline);
    static readonly Regex ExitRe = new Regex(@"^ExitCode\((-?\d+)\)$");
    static readonly Regex SecretRe = new Regex("(?i)(passw|kennw|pwd|secret|token|apikey|api_key|private)");
    static readonly string[] ChangeKeys = { "KOPIERT", "GEAENDERT", "GESETZT", "GELOESCHT", "ANGELEGT", "REPARIERT", "INSTALLIERT", "DEINSTALL", "ENTFERNT" };

    // Gruppen in Anzeige-Reihenfolge (zuerst, was Arbeit macht)
    public static readonly string[] Groups = { "fehler", "betroffen", "laeuft", "wartetOnline", "wartetOffline", "repariert", "ok" };

    public AcmpRollout(string readCs, Action<string> log)
    {
        _cs = readCs; _log = log != null ? log : delegate(string s) { };
        Loader = LoadFromDb;
        ListLoader = ListFromDb;
    }

    // ================= Einstieg fuer die Seite =================
    // cc = GUID mit oder ohne Klammern; param = "NAME:Wert" (nur Laeufe mit diesem Parameterwert), zeilen = Kennzeilen-Spalten (leer = automatisch)
    public JObject Get(string cc, DateTime since, string param, string zeilen)
    {
        Match m = GuidRe.Match(cc ?? "");
        if (!m.Success) throw new Exception("cc: keine gültige Command-Id (GUID).");
        string id = "{" + m.Groups[1].Value.ToUpperInvariant() + "}";
        string key = id + "|" + since.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "|" + (param ?? "") + "|" + (zeilen ?? "");
        lock (_lock)
        {
            KeyValuePair<DateTime, JObject> hit;
            if (_cache.TryGetValue(key, out hit) && (DateTime.UtcNow - hit.Key).TotalSeconds < CacheSeconds) return hit.Value;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        RolloutData d = Loader(id, since);
        JObject r = Evaluate(d, since, param, zeilen);
        r["dauerMs"] = sw.ElapsedMilliseconds;
        lock (_lock)
        {
            if (_cache.Count > 40) _cache.Clear();
            _cache[key] = new KeyValuePair<DateTime, JObject>(DateTime.UtcNow, r);
        }
        return r;
    }

    // Commands mit Auftraegen seit 'since' (fuer die Uebersicht)
    public JArray List(DateTime since)
    {
        string key = "liste|" + since.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        lock (_lock)
        {
            KeyValuePair<DateTime, JObject> hit;
            if (_cache.TryGetValue(key, out hit) && (DateTime.UtcNow - hit.Key).TotalSeconds < CacheSeconds) return (JArray)hit.Value["liste"];
        }
        JArray a = ListLoader(since);
        var o = new JObject(); o["liste"] = a;
        lock (_lock) { _cache[key] = new KeyValuePair<DateTime, JObject>(DateTime.UtcNow, o); }
        return a;
    }

    // ================= DB =================
    JArray ListFromDb(DateTime since)
    {
        if (string.IsNullOrEmpty(_cs)) throw new Exception("db.connectionString fehlt.");
        var arr = new JArray();
        using (var c = new SqlConnection(_cs))
        {
            c.Open();
            var q = c.CreateCommand(); q.CommandTimeout = SqlTimeout;
            // JobKind 4 = Client Command. Auftraege, die seitdem angelegt wurden oder seitdem liefen (auch Dauerauftraege).
            q.CommandText =
                "SELECT CAST(l.ContentId AS varchar(36)) AS id, MAX(l.JobName) AS name, COUNT(DISTINCT i.ClientId) AS rechner, COUNT(*) AS auftraege, " +
                "SUM(CASE WHEN i.State IN (0, 6) THEN 1 ELSE 0 END) AS wartet, SUM(CASE WHEN i.State IN (1, 2) THEN 1 ELSE 0 END) AS laeuft, " +
                "SUM(CASE WHEN i.State = 3 AND i.Result = 1 THEN 1 ELSE 0 END) AS ok, " +
                "SUM(CASE WHEN (i.State = 3 AND i.Result <> 1) OR i.State IN (4, 5) THEN 1 ELSE 0 END) AS fehler, " +
                "MIN(l.CreationDate) AS erster, MAX(CASE WHEN i.LastActivity > l.CreationDate THEN i.LastActivity ELSE l.CreationDate END) AS zuletzt, " +
                "MAX(l.Origin) AS origin FROM SYS_Jobs_Logs l JOIN SYS_Jobs_Logs_Items i ON i.JobId = l.JobId " +
                "WHERE l.JobKind = 4 AND l.ContentId IS NOT NULL AND (l.CreationDate >= @seit OR i.LastActivity >= @seit) " +
                "GROUP BY l.ContentId ORDER BY MAX(CASE WHEN i.LastActivity > l.CreationDate THEN i.LastActivity ELSE l.CreationDate END) DESC";
            q.Parameters.AddWithValue("@seit", since.ToOADate());
            using (var r = q.ExecuteReader())
                while (r.Read())
                {
                    var o = new JObject();
                    o["id"] = "{" + r.GetString(0).ToUpperInvariant() + "}";
                    o["name"] = r.IsDBNull(1) ? "" : r.GetString(1);
                    o["rechner"] = Convert.ToInt64(r.GetValue(2)); o["auftraege"] = Convert.ToInt64(r.GetValue(3));
                    o["wartet"] = Convert.ToInt64(r.GetValue(4)); o["laeuft"] = Convert.ToInt64(r.GetValue(5));
                    o["ok"] = Convert.ToInt64(r.GetValue(6)); o["fehler"] = Convert.ToInt64(r.GetValue(7));
                    o["erster"] = Oa(r.GetValue(8)); o["zuletzt"] = Oa(r.GetValue(9));
                    arr.Add(o);
                }
        }
        return arr;
    }

    RolloutData LoadFromDb(string id, DateTime since)
    {
        if (string.IsNullOrEmpty(_cs)) throw new Exception("db.connectionString fehlt.");
        var d = new RolloutData(); d.CcId = id;
        Guid g = new Guid(id.Trim('{', '}'));
        double seit = since.ToOADate();
        using (var c = new SqlConnection(_cs))
        {
            c.Open();
            var q = Cmd(c, "SELECT TOP 1 DESCRIPTION, ScriptVersion, State FROM SYS_SCRIPTS WHERE SCRIPTID = @sid ORDER BY ScriptVersion DESC", "@sid", id);
            using (var r = q.ExecuteReader())
                if (r.Read()) { d.Name = r.IsDBNull(0) ? null : r.GetString(0); d.Version = Convert.ToInt32(r.GetValue(1)); d.CcState = Convert.ToInt32(r.GetValue(2)); }

            q = Cmd(c, "SELECT Name FROM SYS_Jobs_Parameters_Declarations WHERE ContentId = @g ORDER BY Priority, Name", "@g", g);
            using (var r = q.ExecuteReader())
                while (r.Read()) if (!r.IsDBNull(0) && !d.Params.Contains(r.GetString(0))) d.Params.Add(r.GetString(0));

            q = Cmd(c,
                "SELECT CAST(i.ClientId AS varchar(36)), i.ComputerName, CAST(i.ExecId AS varchar(36)), i.State, i.Result, l.CreationDate, i.StartedDate, i.LastActivity, " +
                "LEFT(i.StateMsg, 400), l.CreatorName, l.JobName, LEFT(l.ConditionText, 200) FROM SYS_Jobs_Logs l JOIN SYS_Jobs_Logs_Items i ON i.JobId = l.JobId " +
                "WHERE l.ContentId = @g AND (l.CreationDate >= @seit OR i.LastActivity >= @seit)", "@g", g, "@seit", seit);
            using (var r = q.ExecuteReader())
                while (r.Read())
                {
                    var it = new RolloutItem();
                    it.ClientId = r.IsDBNull(0) ? null : r.GetString(0).ToUpperInvariant();
                    it.ComputerName = r.IsDBNull(1) ? null : r.GetString(1);
                    it.ExecId = r.IsDBNull(2) ? null : r.GetString(2).ToUpperInvariant();
                    it.State = r.IsDBNull(3) ? -1 : Convert.ToInt32(r.GetValue(3));
                    it.Result = r.IsDBNull(4) ? 0 : Convert.ToInt32(r.GetValue(4));
                    it.Created = Dbl(r.GetValue(5)); it.Started = Dbl(r.GetValue(6)); it.LastActivity = Dbl(r.GetValue(7));
                    it.StateMsg = r.IsDBNull(8) ? null : r.GetString(8);
                    it.Creator = r.IsDBNull(9) ? null : r.GetString(9);
                    if (d.Name == null && !r.IsDBNull(10)) d.Name = r.GetString(10);
                    it.Condition = r.IsDBNull(11) ? null : r.GetString(11);
                    if (it.ClientId != null) d.Items.Add(it);
                }

            q = Cmd(c,
                "SELECT TOP (" + (MaxRuns + 1) + ") CAST(r.CLIENTID AS varchar(36)), r.RUNDATE, r.EXECRESULT, r.ScriptVersion, CAST(r.JobExecId AS varchar(36)), " +
                "CASE WHEN r.HasXmlLog = 0 THEN NULL ELSE r.XmlLogFile END FROM CLT_SCRIPT_EXECRESULT r WHERE r.SCRIPTID = @sid AND r.RUNDATE >= @seit ORDER BY r.RUNDATE DESC",
                "@sid", id, "@seit", seit);
            using (var r = q.ExecuteReader())
                while (r.Read())
                {
                    if (d.Runs.Count >= MaxRuns) { d.RunsCut = true; break; }
                    var run = new RolloutRun();
                    run.ClientId = r.IsDBNull(0) ? null : r.GetString(0).ToUpperInvariant();
                    run.RunDate = Dbl(r.GetValue(1));
                    run.ExecResult = r.IsDBNull(2) ? 0 : Convert.ToInt32(r.GetValue(2));
                    run.ScriptVersion = r.IsDBNull(3) ? 0 : Convert.ToInt32(r.GetValue(3));
                    run.ExecId = r.IsDBNull(4) ? null : r.GetString(4).ToUpperInvariant();
                    run.Log = r.IsDBNull(5) ? null : (byte[])r.GetValue(5);
                    if (run.ClientId != null) d.Runs.Add(run);
                }

            q = Cmd(c,
                "SELECT CAST(c.CLIENTID AS varchar(36)), c.COMPUTERNAME, o.LASTLOGGEDONUSER, v.OnlineState, lc.TimeStamp, c.IsRetired FROM CLT_CLIENTS_TABLE c " +
                "LEFT JOIN CLT_OPERATINGSYSTEM o ON o.CLIENTID = c.CLIENTID LEFT JOIN VW_Inventory_ClientOnlineState v ON v.ClientId = c.CLIENTID " +
                "LEFT JOIN CLT_Clients_LastConnection lc ON lc.ClientId = c.CLIENTID " +
                "WHERE c.CLIENTID IN (SELECT i.ClientId FROM SYS_Jobs_Logs l JOIN SYS_Jobs_Logs_Items i ON i.JobId = l.JobId WHERE l.ContentId = @g AND (l.CreationDate >= @seit OR i.LastActivity >= @seit)) " +
                "OR c.CLIENTID IN (SELECT r.CLIENTID FROM CLT_SCRIPT_EXECRESULT r WHERE r.SCRIPTID = @sid AND r.RUNDATE >= @seit)", "@g", g, "@sid", id, "@seit", seit);
            using (var r = q.ExecuteReader())
                while (r.Read())
                {
                    var cl = new RolloutClient();
                    cl.ClientId = r.GetString(0).ToUpperInvariant();
                    cl.Name = r.IsDBNull(1) ? null : r.GetString(1);
                    cl.User = r.IsDBNull(2) ? null : r.GetString(2);
                    cl.Online = r.IsDBNull(3) ? 2 : Convert.ToInt32(r.GetValue(3));
                    cl.LastContact = r.IsDBNull(4) ? null : Convert.ToDateTime(r.GetValue(4)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    cl.Retired = !r.IsDBNull(5) && Convert.ToBoolean(r.GetValue(5));
                    d.Clients[cl.ClientId] = cl;
                }
        }
        return d;
    }

    static SqlCommand Cmd(SqlConnection c, string sql, params object[] kv)
    {
        var q = c.CreateCommand(); q.CommandText = sql; q.CommandTimeout = SqlTimeout;
        for (int i = 0; i + 1 < kv.Length; i += 2) q.Parameters.AddWithValue((string)kv[i], kv[i + 1]);
        return q;
    }

    // ================= Auswertung (ohne DB, offline getestet) =================
    public static JObject Evaluate(RolloutData d, DateTime since, string param, string zeilen)
    {
        var res = new JObject();
        res["cc"] = d.CcId; res["name"] = d.Name; res["version"] = d.Version; res["ccState"] = d.CcState;
        res["seit"] = since.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        res["stand"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        res["parameter"] = new JArray(d.Params.ToArray());
        res["laeufeGesamt"] = d.Runs.Count; res["auftraegeGesamt"] = d.Items.Count; res["abgeschnitten"] = d.RunsCut;

        // Filter "NAME:Wert"
        string fName = null, fVal = null;
        if (!string.IsNullOrEmpty(param))
        {
            int p = param.IndexOf(':');
            if (p > 0) { fName = param.Substring(0, p).Trim(); fVal = param.Substring(p + 1).Trim(); }
            if (string.IsNullOrEmpty(fName)) fName = null;
            else { var fo = new JObject(); fo["name"] = fName; fo["wert"] = fVal; res["filter"] = fo; }
        }

        // Alle Protokolle einmal entpacken; Parameterwerte (fuer Filter und Auswahl), MCPFIX-Kopf
        var logs = new Dictionary<RolloutRun, RolloutLog>();
        var paramCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        bool mcpfix = false;
        foreach (RolloutRun run in d.Runs)
        {
            RolloutLog lg = Unpack(run.Log);
            logs[run] = lg;
            if (Fields(lg, "MCPFIX") != null) mcpfix = true;
            foreach (KeyValuePair<string, string> pv in RunParams(d, lg))
            {
                if (SecretRe.IsMatch(pv.Key)) continue;   // Kennwort-Parameter nicht zur Auswahl anbieten
                string k = pv.Key + ":" + pv.Value; int n;
                paramCount[k] = paramCount.TryGetValue(k, out n) ? n + 1 : 1;
            }
        }
        res["mcpfix"] = mcpfix;
        var pw = new JArray();
        var pkeys = new List<string>(paramCount.Keys); pkeys.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (string k in pkeys)
        {
            int c = k.IndexOf(':');
            var o = new JObject(); o["name"] = k.Substring(0, c); o["wert"] = k.Substring(c + 1); o["laeufe"] = paramCount[k]; pw.Add(o);
        }
        res["paramWerte"] = pw;

        // Laeufe je Rechner (nach Filter); Auftraege, deren Lauf herausgefiltert ist, fallen mit weg
        var runByExec = new Dictionary<string, RolloutRun>(StringComparer.OrdinalIgnoreCase);
        foreach (RolloutRun run in d.Runs) if (run.ExecId != null && !runByExec.ContainsKey(run.ExecId)) runByExec[run.ExecId] = run;
        var runsBy = new Dictionary<string, List<RolloutRun>>(StringComparer.OrdinalIgnoreCase);
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // Rechner mit herausgefilterten Laeufen
        int filteredOut = 0;
        foreach (RolloutRun run in d.Runs)
        {
            if (fName != null && !MatchParam(d, logs[run], fName, fVal)) { filteredOut++; excluded.Add(run.ClientId); continue; }
            List<RolloutRun> l;
            if (!runsBy.TryGetValue(run.ClientId, out l)) { l = new List<RolloutRun>(); runsBy[run.ClientId] = l; }
            l.Add(run);
        }
        res["herausgefiltert"] = filteredOut;
        var itemsBy = new Dictionary<string, List<RolloutItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (RolloutItem it in d.Items)
        {
            RolloutRun own;
            if (it.ExecId != null && runByExec.TryGetValue(it.ExecId, out own) && fName != null && !MatchParam(d, logs[own], fName, fVal)) continue;
            List<RolloutItem> l;
            if (!itemsBy.TryGetValue(it.ClientId, out l)) { l = new List<RolloutItem>(); itemsBy[it.ClientId] = l; }
            l.Add(it);
        }

        var ids = new List<string>(runsBy.Keys);
        foreach (string k in itemsBy.Keys) if (!runsBy.ContainsKey(k)) ids.Add(k);
        int clientsOut = 0;
        foreach (string k in excluded) if (!ids.Contains(k)) clientsOut++;
        res["rechnerHerausgefiltert"] = clientsOut;

        // Kennzeilen der jeweils letzten Laeufe: welche Schluessel gibt es, wie oft, einzeilig?
        var keyRuns = new Dictionary<string, int>(StringComparer.Ordinal);
        var keyMulti = new HashSet<string>(StringComparer.Ordinal);
        var keyLen = new Dictionary<string, int>(StringComparer.Ordinal);    // Summe der Wertlaengen (kurze Werte taugen als Spalte)
        var keyOrder = new List<string>();
        int latestWithLog = 0;
        foreach (string cid in ids)
        {
            List<RolloutRun> l; if (!runsBy.TryGetValue(cid, out l)) continue;
            RolloutLog lg = logs[l[0]];
            if (!lg.Ok) continue;
            latestWithLog++;
            foreach (KeyValuePair<string, List<string>> kv in KeyLines(lg))
            {
                int n; keyRuns[kv.Key] = keyRuns.TryGetValue(kv.Key, out n) ? n + 1 : 1;
                keyLen[kv.Key] = (keyLen.TryGetValue(kv.Key, out n) ? n : 0) + kv.Value[0].Length;
                if (kv.Value.Count > 1) keyMulti.Add(kv.Key);
                if (!keyOrder.Contains(kv.Key)) keyOrder.Add(kv.Key);
            }
        }
        var kz = new JArray();
        foreach (string k in keyOrder) { var o = new JObject(); o["key"] = k; o["laeufe"] = keyRuns[k]; o["mehrfach"] = keyMulti.Contains(k); kz.Add(o); }
        res["kennzeilen"] = kz;
        var cols = new List<string>();
        if (!string.IsNullOrEmpty(zeilen))
        {
            foreach (string z in zeilen.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string u = z.Trim().ToUpperInvariant();
                if (KeyRe.IsMatch(u + "|") && !cols.Contains(u) && cols.Count < 6) cols.Add(u);
            }
        }
        else
        {
            // automatisch: einzeilige, kurze Schluessel (im Mittel bis 40 Zeichen) in mindestens der Haelfte der letzten Laeufe, ohne Kopf/Abschluss, hoechstens 3
            foreach (string k in keyOrder)
                if (cols.Count < 3 && k != "MCPFIX" && k != "FERTIG" && !keyMulti.Contains(k) && keyRuns[k] * 2 >= Math.Max(1, latestWithLog) && keyLen[k] <= 40 * keyRuns[k]) cols.Add(k);
        }
        res["spalten"] = new JArray(cols.ToArray());

        // Je Rechner: letztes Ereignis = juengster Lauf oder juengerer Auftrag ohne Lauf
        var rows = new JArray();
        var counts = new Dictionary<string, int>();
        foreach (string g in Groups) counts[g] = 0;
        foreach (string cid in ids)
        {
            List<RolloutRun> rl; runsBy.TryGetValue(cid, out rl);
            List<RolloutItem> il; itemsBy.TryGetValue(cid, out il);
            RolloutRun last = rl != null && rl.Count > 0 ? rl[0] : null;   // Laeufe kommen absteigend nach Zeit
            RolloutItem lastItem = null;      // juengster Auftrag
            RolloutItem runItem = null;       // Auftrag des letzten Laufs
            RolloutItem pending = null;       // juengster Auftrag ohne eigenen Lauf (wartet, laeuft, abgebrochen, fertig ohne Protokoll)
            int open = 0;                     // Auftraege ohne Lauf, die noch warten oder laufen
            if (il != null)
                foreach (RolloutItem it in il)
                {
                    if (lastItem == null || it.Created > lastItem.Created) lastItem = it;
                    if (last != null && it.ExecId != null && it.ExecId == last.ExecId) runItem = it;
                    if (it.ExecId != null && runByExec.ContainsKey(it.ExecId)) continue;
                    if (it.State == 0 || it.State == 1 || it.State == 2) open++;
                    if (pending == null || it.Created > pending.Created) pending = it;
                }
            // Ein Auftrag ohne Lauf zaehlt nur, wenn er nach dem letzten Lauf angelegt wurde; sonst entscheidet der Lauf
            bool pendingWins = pending != null && (last == null || pending.Created > last.RunDate);

            RolloutClient cl; d.Clients.TryGetValue(cid, out cl);
            var row = new JObject();
            row["name"] = cl != null && cl.Name != null ? cl.Name : (lastItem != null && lastItem.ComputerName != null ? lastItem.ComputerName : cid);
            row["benutzer"] = cl != null ? cl.User : null;
            row["online"] = cl != null ? cl.Online : 2;
            row["letzterKontakt"] = cl != null ? cl.LastContact : null;
            if (cl != null && cl.Retired) row["ausgemustert"] = true;
            row["laeufe"] = rl != null ? rl.Count : 0;
            row["auftraege"] = il != null ? il.Count : 0;
            if (!pendingWins && open > 0) row["offen"] = open;

            string group, befund, text;
            RolloutItem shown = pendingWins ? pending : (runItem != null ? runItem : lastItem);
            if (shown != null) row["auftrag"] = ItemJson(shown);
            if (pendingWins)
            {
                group = PendingGroup(pending, cl, out befund, out text);
            }
            else if (last != null)
            {
                RolloutLog lg = logs[last];
                befund = Befund(lg, last.ExecResult, out text);
                // Hat ein frueherer Lauf im Zeitraum repariert und der letzte findet alles in Ordnung, zaehlt die Reparatur
                if (befund == "ok")
                    foreach (RolloutRun h in rl)
                    {
                        string ht;
                        if (h == last || Befund(logs[h], h.ExecResult, out ht) != "repariert") continue;
                        befund = "repariert"; text = "repariert " + ShortTime(h.RunDate) + ", danach in Ordnung";
                        break;
                    }
                group = befund == "fehler" || befund == "abbruch" ? "fehler" : befund == "betroffen" ? "betroffen" : befund == "repariert" ? "repariert" : befund == "ok" || befund == "erfolg" ? "ok" : "fehler";
            }
            else { group = "fehler"; befund = "unbekannt"; text = "kein Lauf und kein Auftrag"; }
            row["gruppe"] = group; row["befund"] = befund; row["befundText"] = text;
            counts[group] = counts[group] + 1;

            if (last != null)
            {
                RolloutLog lg = logs[last];
                row["lauf"] = RunJson(d, last, lg);
                var zj = new JObject();
                Dictionary<string, List<string>> kl = KeyLines(lg);
                foreach (string k in cols) { List<string> v; if (kl.TryGetValue(k, out v)) zj[k] = Mask(k, string.Join("; ", v.ToArray())); }
                row["zeilen"] = zj;
                var outp = new JArray();
                foreach (string line in lg.Output) { if (outp.Count >= MaxOutputLines) break; outp.Add(MaskLine(Cut(line, 300))); }
                row["ausgabe"] = outp;
                if (lg.Output.Count > MaxOutputLines) row["ausgabeMehr"] = lg.Output.Count - MaxOutputLines;
                if (!lg.Ok && lg.Error != null) row["protokollFehler"] = lg.Error;
                var hist = new JArray();
                foreach (RolloutRun h in rl)
                {
                    if (hist.Count >= MaxHistory) break;
                    string ht; string hb = Befund(logs[h], h.ExecResult, out ht);
                    var ho = RunJson(d, h, logs[h]); ho["befund"] = hb; ho["befundText"] = ht; hist.Add(ho);
                }
                row["verlauf"] = hist;
            }
            rows.Add(row);
        }
        var sorted = new List<JObject>(); foreach (JObject r in rows) sorted.Add(r);
        sorted.Sort(delegate(JObject a, JObject b)
        {
            int x = Array.IndexOf(Groups, (string)a["gruppe"]).CompareTo(Array.IndexOf(Groups, (string)b["gruppe"]));
            return x != 0 ? x : string.Compare((string)a["name"], (string)b["name"], StringComparison.OrdinalIgnoreCase);
        });
        res["rechner"] = new JArray(sorted.ToArray());
        var cj = new JObject(); foreach (string g in Groups) cj[g] = counts[g];
        res["gruppen"] = cj;
        res["rechnerGesamt"] = sorted.Count;
        return res;
    }

    static string PendingGroup(RolloutItem it, RolloutClient cl, out string befund, out string text)
    {
        if (it.State == 4 || it.State == 5)
        {
            befund = "abbruch"; text = (it.State == 4 ? "Auftrag abgebrochen" : "Auftrag mit Timeout") + (string.IsNullOrEmpty(it.StateMsg) ? "" : ": " + Cut(it.StateMsg, 160));
            return "fehler";
        }
        if (it.State == 3)
        {
            // fertig, aber kein Protokoll in CLT_SCRIPT_EXECRESULT (z.B. Command speichert kein Client-Protokoll): Ergebnis aus dem Job-Monitor
            if (it.Result == 1) { befund = "erfolg"; text = "laut Job-Monitor erfolgreich (kein Protokoll)"; return "ok"; }
            befund = "fehler"; text = "laut Job-Monitor " + (it.Result == 2 ? "Warnung" : "Fehler") + (string.IsNullOrEmpty(it.StateMsg) ? "" : ": " + Cut(it.StateMsg, 160));
            return "fehler";
        }
        if (it.State == 1 || it.State == 2) { befund = "laeuft"; text = it.State == 1 ? "an den Agenten übergeben" : "läuft"; return "laeuft"; }
        befund = "wartet";
        bool on = cl != null && cl.Online == 0;
        text = it.State == 6 ? "beim Agenten geplant, wartet auf die Startbedingung" + (string.IsNullOrEmpty(it.Condition) ? "" : " (" + Cut(it.Condition, 80) + ")")
             : on ? "eingereiht, Rechner online" : "eingereiht, Rechner offline";
        return on ? "wartetOnline" : "wartetOffline";
    }

    static JObject ItemJson(RolloutItem it)
    {
        var o = new JObject();
        o["eingereiht"] = OaText(it.Created);
        o["von"] = it.Creator;
        o["zustand"] = StateText(it.State);
        if (it.State == 3) o["ergebnis"] = it.Result == 1 ? "Erfolg" : it.Result == 2 ? "Warnung" : it.Result == 3 ? "Fehler" : "?";
        if (it.LastActivity > 0) o["letzteAktivitaet"] = OaText(it.LastActivity);
        if (!string.IsNullOrEmpty(it.StateMsg)) o["meldung"] = Cut(it.StateMsg, 300);
        if (!string.IsNullOrEmpty(it.Condition)) o["bedingung"] = Cut(it.Condition, 120);
        return o;
    }

    static JObject RunJson(RolloutData d, RolloutRun run, RolloutLog lg)
    {
        var o = new JObject();
        o["zeit"] = OaText(run.RunDate);
        o["code"] = run.ExecResult;
        if (lg.ExitCode.HasValue) o["exit"] = lg.ExitCode.Value;
        o["version"] = run.ScriptVersion;
        var pj = new JObject();
        foreach (KeyValuePair<string, string> pv in RunParams(d, lg)) pj[pv.Key] = Mask(pv.Key, pv.Value);
        o["param"] = pj;
        List<string> mf = Fields(lg, "MCPFIX");
        if (mf != null && mf.Count >= 3) { o["mcpfixVersion"] = mf[0]; o["modus"] = mf[2]; }
        return o;
    }

    // Parameterwerte eines Laufs: deklarierte Parameter aus "Logged Variables"; ohne Deklaration und mit MCPFIX-Kopf der Modus aus dem Kopf
    static List<KeyValuePair<string, string>> RunParams(RolloutData d, RolloutLog lg)
    {
        var l = new List<KeyValuePair<string, string>>();
        foreach (string p in d.Params) { string v; if (lg.Vars.TryGetValue(p, out v)) l.Add(new KeyValuePair<string, string>(p, v)); }
        if (l.Count == 0)
        {
            List<string> mf = Fields(lg, "MCPFIX");
            if (mf != null && mf.Count >= 3 && mf[2].Length > 0) l.Add(new KeyValuePair<string, string>("MODUS", mf[2]));
        }
        return l;
    }

    static bool MatchParam(RolloutData d, RolloutLog lg, string name, string val)
    {
        foreach (KeyValuePair<string, string> pv in RunParams(d, lg))
            if (string.Equals(pv.Key, name, StringComparison.OrdinalIgnoreCase) && string.Equals(pv.Value, val ?? "", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // Befund eines Laufs: ok | repariert | betroffen | fehler | abbruch | erfolg (ohne MCPFIX). text = kurze Begruendung.
    public static string Befund(RolloutLog lg, int execResult, out string text)
    {
        List<string> mf = Fields(lg, "MCPFIX");
        int code = lg.ExitCode.HasValue ? lg.ExitCode.Value : execResult;
        if (mf == null)
        {
            if (execResult == 0) { text = lg.ExitCode.HasValue && lg.ExitCode.Value != 0 ? "Erfolg laut ACMP (Exit " + lg.ExitCode.Value + ")" : "Erfolg"; return "erfolg"; }
            text = "Fehler laut ACMP (Code " + execResult + (lg.ExitCode.HasValue ? ", Exit " + lg.ExitCode.Value : "") + ")";
            return "fehler";
        }
        Dictionary<string, List<string>> kl = KeyLines(lg);
        List<string> v;
        if (kl.TryGetValue("ABBRUCH", out v)) { text = "Abbruch: " + Cut(v[0], 160); return "abbruch"; }
        if (kl.TryGetValue("FEHLER", out v)) { text = "Fehler: " + Cut(v[0], 160); return "fehler"; }
        int nf = 0;
        if (kl.TryGetValue("FERTIG", out v))
        {
            Match m = Regex.Match(v[v.Count - 1], @"Fehler=(\d+)");
            if (m.Success) nf = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        string modus = mf.Count >= 3 ? mf[2] : "";
        bool pruefen = modus.Equals("Pruefen", StringComparison.OrdinalIgnoreCase) || modus.Equals("Prüfen", StringComparison.OrdinalIgnoreCase);
        if (nf > 0) { text = nf + (nf == 1 ? " Fehler" : " Fehler") + " (FERTIG), Exit " + code; return "fehler"; }
        if (pruefen)
        {
            if (code == 0) { text = "geprüft: in Ordnung"; return "ok"; }
            if (code == 1) { text = "geprüft: betroffen, Fix nötig"; return "betroffen"; }
            text = "Prüfung mit Exit " + code; return "fehler";
        }
        if (code != 0) { text = "Exit " + code + (execResult != 0 ? " (Code " + execResult + ")" : ""); return "fehler"; }
        var changes = new List<string>();
        foreach (string k in ChangeKeys) if (kl.TryGetValue(k, out v)) changes.Add(v.Count + "× " + k);
        if (changes.Count > 0) { text = (modus.Length > 0 ? modus + ": " : "") + "repariert (" + string.Join(", ", changes.ToArray()) + ")"; return "repariert"; }
        text = (modus.Length > 0 ? modus + ": " : "") + "war schon in Ordnung, nichts geändert";
        return "ok";
    }

    // ================= Protokoll =================
    public static RolloutLog Unpack(byte[] data)
    {
        var lg = new RolloutLog();
        if (data == null || data.Length == 0) { lg.Error = "kein Protokoll gespeichert"; return lg; }
        string xml = Inflate(data);
        if (xml == null) { lg.Error = "Protokoll nicht entpackbar"; return lg; }
        try { ParseXml(xml, lg); lg.Ok = true; }
        catch (Exception ex)
        {
            // abgeschnittenes oder fehlerhaftes XML: Meldungen per Muster, Kennzeilen letzter Treffer
            lg.Vars.Clear(); lg.Output.Clear(); lg.ExitCode = null;
            ParseFlat(xml, lg);
            lg.Ok = lg.Output.Count > 0;
            lg.Error = "Protokoll nicht vollständig lesbar (" + Cut(ex.Message, 80) + ")";
        }
        return lg;
    }

    // Raw-Deflate, bei Bedarf mit 2-Byte-zlib-Kopf; Text UTF-8 oder UTF-16 (BOM)
    static string Inflate(byte[] data)
    {
        foreach (int off in new[] { 0, 2 })
        {
            if (data.Length <= off) continue;
            try
            {
                byte[] raw;
                using (var ms = new MemoryStream(data, off, data.Length - off))
                using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
                using (var o = new MemoryStream())
                {
                    var buf = new byte[65536]; int n;
                    while ((n = ds.Read(buf, 0, buf.Length)) > 0) { o.Write(buf, 0, n); if (o.Length > MaxLogBytes) throw new Exception("zu gross"); }
                    raw = o.ToArray();
                }
                if (raw.Length == 0) continue;
                if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE) return Encoding.Unicode.GetString(raw, 2, raw.Length - 2);
                if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF) return Encoding.UTF8.GetString(raw, 3, raw.Length - 3);
                if (raw.Length >= 2 && raw[1] == 0) return Encoding.Unicode.GetString(raw);
                return Encoding.UTF8.GetString(raw);
            }
            catch { }
        }
        return null;
    }

    // Aufbau (ACMP 6.10.4): <LE> Wurzel (Tiefe 0) -> <LE> je Schritt (Tiefe 1; der erste traegt "Logged Variables") -> <M>-Meldungen darunter.
    // In einem Skript-Schritt: Skripttext Zeile fuer Zeile, "PowerShell found at ...", "ExitCode(n)", danach die Ausgabe.
    static void ParseXml(string xml, RolloutLog lg)
    {
        var st = new XmlReaderSettings();
        st.DtdProcessing = DtdProcessing.Prohibit; st.XmlResolver = null; st.IgnoreWhitespace = true;
        var all = new List<string>();
        bool anyExit = false;
        using (var r = XmlReader.Create(new StringReader(xml), st))
        {
            bool inVars = false, afterExit = false, firstMsg = false;
            while (!r.EOF)
            {
                if (r.NodeType == XmlNodeType.Element && r.Name == "LE" && r.Depth == 1)
                {
                    inVars = false; afterExit = false; firstMsg = true;
                    r.Read(); continue;
                }
                if (r.NodeType == XmlNodeType.Element && r.Name == "M")
                {
                    int depth = r.Depth;
                    string t = r.ReadElementContentAsString();
                    if (depth < 2) continue;
                    if (firstMsg && depth == 2 && t == "Logged Variables") { inVars = true; firstMsg = false; continue; }
                    firstMsg = false;
                    if (inVars) { Match vm = VarRe.Match(t); if (vm.Success) lg.Vars[vm.Groups[1].Value] = vm.Groups[2].Value; continue; }
                    Match em = ExitRe.Match(t.Trim());
                    if (em.Success) { lg.ExitCode = int.Parse(em.Groups[1].Value, CultureInfo.InvariantCulture); afterExit = true; anyExit = true; continue; }
                    foreach (string line in t.Replace("\r\n", "\n").Split('\n'))
                    {
                        if (line.Length == 0) continue;
                        if (afterExit) lg.Output.Add(line);
                        all.Add(line);
                    }
                    continue;
                }
                r.Read();
            }
        }
        if (!anyExit)
        {
            // Ohne ExitCode-Marke (andere Schritt-Arten): nur Kennzeilen aus allen Meldungen; Skriptzeilen beginnen mit Anfuehrungszeichen und passen nicht
            lg.Fallback = true;
            foreach (string line in all) if (KeyRe.IsMatch(line)) lg.Output.Add(line);
        }
    }

    static readonly Regex FlatM = new Regex("<M>(.*?)</M>", RegexOptions.Singleline);
    static void ParseFlat(string xml, RolloutLog lg)
    {
        lg.Fallback = true;
        bool afterExit = false;
        foreach (Match m in FlatM.Matches(xml))
        {
            string t = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
            Match vm = VarRe.Match(t);
            if (vm.Success && !afterExit) { lg.Vars[vm.Groups[1].Value] = vm.Groups[2].Value; continue; }
            Match em = ExitRe.Match(t.Trim());
            if (em.Success) { lg.ExitCode = int.Parse(em.Groups[1].Value, CultureInfo.InvariantCulture); afterExit = true; continue; }
            if (KeyRe.IsMatch(t)) lg.Output.Add(t);
        }
    }

    // Kennzeilen "SCHLUESSEL|Rest" der Ausgabe, je Schluessel alle Werte in Reihenfolge (ohne Fallback: nur die Ausgabe)
    public static Dictionary<string, List<string>> KeyLines(RolloutLog lg)
    {
        var d = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string line in lg.Output)
        {
            Match m = KeyRe.Match(line);
            if (!m.Success) continue;
            List<string> l;
            if (!d.TryGetValue(m.Groups[1].Value, out l)) { l = new List<string>(); d[m.Groups[1].Value] = l; }
            l.Add(m.Groups[2].Value);
        }
        if (lg.Fallback)
        {
            // Ohne Marke kann der Skripttext Treffer liefern: nur den letzten Wert behalten (wie die Auswerteskripte)
            var keys = new List<string>(d.Keys);
            foreach (string k in keys) if (k == "MCPFIX" && d[k].Count > 1) d[k] = new List<string> { d[k][d[k].Count - 1] };
        }
        return d;
    }

    // Felder einer einzeiligen Kennzeile (letzter Treffer), z.B. MCPFIX -> [Version, Rechner, Modus, Zeit, ...]
    static List<string> Fields(RolloutLog lg, string key)
    {
        string hit = null;
        foreach (string line in lg.Output) if (line.StartsWith(key + "|", StringComparison.Ordinal)) hit = line;
        if (hit == null) return null;
        return new List<string>(hit.Substring(key.Length + 1).Split('|'));
    }

    // ================= Hilfen =================
    static string Mask(string key, string v) { return SecretRe.IsMatch(key ?? "") ? "***" : v; }
    static string MaskLine(string line)
    {
        if (!SecretRe.IsMatch(line)) return line;
        int p = line.IndexOf('|');
        return p > 0 && p < 25 ? line.Substring(0, p + 1) + "*** (ausgeblendet)" : "*** (Zeile ausgeblendet)";
    }
    static string StateText(int s)
    {
        switch (s)
        {
            case 0: return "wartet"; case 1: return "zugewiesen"; case 2: return "läuft"; case 3: return "fertig"; case 4: return "abgebrochen"; case 5: return "Timeout";
            case 6: return "geplant"; default: return "Status " + s;
        }
    }
    public static string Cut(string s, int n) { if (s == null) return ""; s = s.Replace("\r", " ").Replace("\n", " "); return s.Length > n ? s.Substring(0, n - 1) + "…" : s; }
    static double Dbl(object v) { return v == null || v is DBNull ? 0 : Convert.ToDouble(v, CultureInfo.InvariantCulture); }
    static JToken Oa(object v) { double d = Dbl(v); return d > 0 ? (JToken)OaText(d) : JValue.CreateNull(); }
    static string ShortTime(double d)
    {
        try { return DateTime.FromOADate(d).ToString("dd.MM. HH:mm", CultureInfo.InvariantCulture); } catch { return "?"; }
    }
    // OLE-Datum (Ortszeit) -> "JJJJ-MM-TT HH:mm:ss"
    public static string OaText(double d)
    {
        if (d <= 0) return null;
        try { return DateTime.FromOADate(d).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture); } catch { return null; }
    }
}
