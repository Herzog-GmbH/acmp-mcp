// AcmpDashboard: Lagebild fuer die Startseite der Weboberflaeche (/dashboard, seit 23.10.2026): Flotte, Server und Jobs aus der ACMP-DB.
// Nur lesen, ueber die Leseverbindung der Report-Tools (db.connectionString); dieselben Tabellen wie db-tools.json und acmp_jobs.
// Ein Durchgang (Schnappschuss) = ca. 15 Abfragen, gemessen 23.10.2026 zusammen ca. 2,5 s. Zwischenspeicher: neu geholt wird nur,
// wenn jemand die Seite oeffnet und der Stand aelter als dashboard.cacheMinutes ist (Hintergrund, immer nur ein Durchgang); ohne
// Besucher keine Last. Scheitert ein Abschnitt, steht der Fehler in "fehler" und nur dieser Abschnitt fehlt.
// Schwellen in config.local.json, Section "dashboard" (alle optional, Neustart noetig): cacheMinutes, offlineDays, minFreeGB, minFreePct,
// serverMinFreePct, critFreeGB, critFreePct, scanDays, jobErrorRatePct, jobErrorMin.
// net48 / C# 5 (csc v4.0.30319): keine string-Interpolation, keine expression-bodied member, kein ?.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Threading;
using Newtonsoft.Json.Linq;

class DashOptions
{
    public int CacheMinutes = 5, OfflineDays = 7, MinFreeGB = 15, MinFreePct = 10, ServerMinFreePct = 15, CritFreeGB = 5, CritFreePct = 3,
               ScanDays = 7, JobErrorRatePct = 20, JobErrorMin = 3;

    public static DashOptions From(JObject c)
    {
        var o = new DashOptions();
        if (c == null) return o;
        o.CacheMinutes = Get(c, "cacheMinutes", o.CacheMinutes, 1, 120);
        o.OfflineDays = Get(c, "offlineDays", o.OfflineDays, 1, 365);
        o.MinFreeGB = Get(c, "minFreeGB", o.MinFreeGB, 1, 10000);
        o.MinFreePct = Get(c, "minFreePct", o.MinFreePct, 1, 99);
        o.ServerMinFreePct = Get(c, "serverMinFreePct", o.ServerMinFreePct, 1, 99);
        o.CritFreeGB = Get(c, "critFreeGB", o.CritFreeGB, 0, 10000);
        o.CritFreePct = Get(c, "critFreePct", o.CritFreePct, 0, 99);
        o.ScanDays = Get(c, "scanDays", o.ScanDays, 1, 365);
        o.JobErrorRatePct = Get(c, "jobErrorRatePct", o.JobErrorRatePct, 1, 100);
        o.JobErrorMin = Get(c, "jobErrorMin", o.JobErrorMin, 1, 100000);
        return o;
    }

    static int Get(JObject c, string k, int def, int min, int max)
    {
        int v;
        if (c[k] == null || c[k].Type == JTokenType.Null || !int.TryParse(c[k].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return def;
        return Math.Max(min, Math.Min(max, v));
    }
}

class AcmpDashboard
{
    public readonly DashOptions Opt;
    public Func<JObject> Builder;          // Standard: aus der DB; Tests setzen einen eigenen Builder
    readonly string _cs;
    readonly AcmpJobs _jobs;
    readonly Action<string> _log;
    readonly object _lock = new object();
    volatile JObject _snap;
    DateTime _snapAt = DateTime.MinValue;  // UTC
    DateTime _lastStart = DateTime.MinValue;
    bool _building;
    public const int MinManualSeconds = 60;   // "Jetzt aktualisieren" hoechstens einmal pro Minute
    const int SqlTimeout = 30;

    public AcmpDashboard(string readCs, AcmpJobs jobs, JObject cfg, Action<string> log)
    {
        _cs = readCs; _jobs = jobs; _log = log != null ? log : delegate(string s) { };
        Opt = DashOptions.From(cfg);
        Builder = BuildFromDb;
    }

    public string ReadCs { get { return _cs; } }   // Leseverbindung, auch fuer die Rollout-Seite (AcmpRollout)
    public bool Building { get { lock (_lock) { return _building; } } }
    public DateTime StandUtc { get { lock (_lock) { return _snapAt; } } }

    // Fuer die Seite: aktueller Stand. Beim allerersten Aufruf wird gewartet; ist der Stand zu alt, holt ein Hintergrund-Durchgang den neuen.
    public JObject Get()
    {
        JObject s = _snap;
        if (s == null) { Refresh(true); return _snap; }
        bool stale;
        lock (_lock) { stale = !_building && (DateTime.UtcNow - _snapAt).TotalMinutes >= Opt.CacheMinutes; }
        if (stale) StartBackground();
        return s;
    }

    // "Jetzt aktualisieren": synchron, hoechstens einmal pro Minute. false = zu frueh (alter Stand bleibt).
    public bool RefreshNow()
    {
        lock (_lock) { if ((DateTime.UtcNow - _lastStart).TotalSeconds < MinManualSeconds) return false; }
        Refresh(true);
        return true;
    }

    void StartBackground()
    {
        lock (_lock) { if (_building) return; _building = true; _lastStart = DateTime.UtcNow; }
        ThreadPool.QueueUserWorkItem(delegate(object o) { RunBuild(); });
    }

    // wait=true: laeuft schon ein Durchgang, auf ihn warten (hoechstens 90 s), sonst selbst einen starten
    void Refresh(bool wait)
    {
        bool mine = false;
        lock (_lock)
        {
            if (_building)
            {
                if (!wait) return;
                DateTime until = DateTime.UtcNow.AddSeconds(90);
                while (_building && DateTime.UtcNow < until) Monitor.Wait(_lock, 1000);
                if (_snap != null) return;
            }
            if (!_building) { _building = true; _lastStart = DateTime.UtcNow; mine = true; }
        }
        if (mine) RunBuild();
    }

    void RunBuild()
    {
        JObject s;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { s = Builder(); }
        catch (Exception ex)
        {
            s = new JObject();
            var f = new JObject(); f["verbindung"] = ex.Message; s["fehler"] = f;
            _log("Dashboard: Durchgang fehlgeschlagen: " + ex.Message);
        }
        if (s["stand"] == null) s["stand"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        s["dauerMs"] = sw.ElapsedMilliseconds;
        lock (_lock)
        {
            _snap = s; _snapAt = DateTime.UtcNow; _building = false;
            Monitor.PulseAll(_lock);
        }
    }

    // ================= Schnappschuss aus der DB =================
    JObject BuildFromDb()
    {
        if (string.IsNullOrEmpty(_cs)) throw new Exception("db.connectionString fehlt.");
        var snap = new JObject();
        var err = new JObject();
        snap["stand"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var fl = new JObject();
        snap["flotte"] = fl;
        using (var c = new SqlConnection(_cs))
        {
            c.Open();
            Section(err, "Flotte", delegate()
            {
                JObject r = One(c,
                    "SELECT COUNT(*) AS gesamt, SUM(CASE WHEN o.OSSTRING LIKE '%Server%' THEN 1 ELSE 0 END) AS server, " +
                    "SUM(CASE WHEN u.ClientId IS NOT NULL THEN 1 ELSE 0 END) AS unix, " +
                    "SUM(CASE WHEN v.OnlineState = 0 THEN 1 ELSE 0 END) AS online, SUM(CASE WHEN v.OnlineState = 1 THEN 1 ELSE 0 END) AS offline, " +
                    "SUM(CASE WHEN v.OnlineState IS NULL OR v.OnlineState NOT IN (0, 1) THEN 1 ELSE 0 END) AS unbekannt, " +
                    "SUM(CASE WHEN lc.TimeStamp IS NULL THEN 1 ELSE 0 END) AS nieVerbunden, " +
                    "SUM(CASE WHEN lc.TimeStamp IS NOT NULL AND lc.TimeStamp < DATEADD(DAY, -@d, GETDATE()) THEN 1 ELSE 0 END) AS langeOffline " +
                    "FROM CLT_CLIENTS_TABLE c LEFT JOIN CLT_OPERATINGSYSTEM o ON o.CLIENTID = c.CLIENTID " +
                    "LEFT JOIN (SELECT DISTINCT ClientId FROM CLT_UnixClients) u ON u.ClientId = c.CLIENTID " +
                    "LEFT JOIN VW_Inventory_ClientOnlineState v ON v.ClientId = c.CLIENTID " +
                    "LEFT JOIN CLT_Clients_LastConnection lc ON lc.ClientId = c.CLIENTID WHERE c.IsRetired = 0", "@d", Opt.OfflineDays);
                foreach (JProperty p in r.Properties()) fl[p.Name] = p.Value;
                fl["langeOfflineNamen"] = Col(Rows(c,
                    "SELECT TOP 6 c.COMPUTERNAME + ' (' + CAST(DATEDIFF(DAY, lc.TimeStamp, GETDATE()) AS varchar(10)) + ' Tage)' AS n FROM CLT_CLIENTS_TABLE c " +
                    "JOIN CLT_Clients_LastConnection lc ON lc.ClientId = c.CLIENTID WHERE c.IsRetired = 0 AND lc.TimeStamp < DATEADD(DAY, -@d, GETDATE()) ORDER BY lc.TimeStamp",
                    "@d", Opt.OfflineDays), "n");
                fl["agenten"] = Rows(c, "SELECT TOP 4 COALESCE(InstalledACMPVersion, '(leer)') AS version, COUNT(*) AS anzahl FROM CLT_CLIENTS_TABLE WHERE IsRetired = 0 GROUP BY InstalledACMPVersion ORDER BY COUNT(*) DESC");
            });
            Section(err, "Plattenplatz", delegate()
            {
                fl["platz"] = One(c,
                    "SELECT COUNT(DISTINCT ld.CLIENTID) AS clients, COUNT(*) AS laufwerke, SUM(CASE WHEN ld.FREESPACE < CAST(@crit AS bigint) * 1048576 THEN 1 ELSE 0 END) AS kritisch " +
                    "FROM CLT_HDW_LDRIVES ld JOIN CLT_CLIENTS_TABLE c ON c.CLIENTID = ld.CLIENTID WHERE c.IsRetired = 0 AND ld.MediaType = 'Local Disk' AND ld.CAPACITY > 0 " +
                    "AND (ld.FREESPACE < CAST(@gb AS bigint) * 1048576 OR 100.0 * ld.FREESPACE / ld.CAPACITY < @pct)",
                    "@crit", Opt.CritFreeGB, "@gb", Opt.MinFreeGB, "@pct", Opt.MinFreePct);
            });
            Section(err, "BitLocker", delegate()
            {
                // VolumeType in der ACMP-DB: 1 = Betriebssystem (C:), 2 = Datenlaufwerk (geprueft 23.10.2026)
                fl["bitlocker"] = One(c,
                    "SELECT COUNT(DISTINCT di.ClientId) AS mitDaten, COUNT(DISTINCT CASE WHEN (di.VolumeType = 1 OR di.DriveLetter LIKE 'C%') AND di.IsProtected = 0 THEN di.ClientId END) AS cOffen " +
                    "FROM CLT_Sec_BitLocker_DriveInventory di JOIN CLT_CLIENTS_TABLE c ON c.CLIENTID = di.ClientId WHERE c.IsRetired = 0");
            });
            Section(err, "Virenschutz", delegate()
            {
                var av = new JObject();
                av["produkte"] = Rows(c,
                    "SELECT TOP 6 a.DisplayName AS name, COUNT(DISTINCT a.ClientID) AS clients, COUNT(DISTINCT CASE WHEN a.OnAccessScanningEnabled = 1 THEN a.ClientID END) AS aktiv " +
                    "FROM CLT_SECCENTER_ANTIVIRUS a JOIN CLT_CLIENTS_TABLE c ON c.CLIENTID = a.ClientID WHERE c.IsRetired = 0 GROUP BY a.DisplayName ORDER BY COUNT(DISTINCT a.ClientID) DESC");
                JArray ohne = Col(Rows(c,
                    "SELECT c.COMPUTERNAME AS n FROM CLT_CLIENTS_TABLE c JOIN CLT_OPERATINGSYSTEM o ON o.CLIENTID = c.CLIENTID " +
                    "JOIN CLT_Clients_LastConnection lc ON lc.ClientId = c.CLIENTID WHERE c.IsRetired = 0 AND o.OSSTRING LIKE '%Windows%' AND o.OSSTRING NOT LIKE '%Server%' " +
                    "AND NOT EXISTS (SELECT 1 FROM CLT_SECCENTER_ANTIVIRUS a WHERE a.ClientID = c.CLIENTID AND a.OnAccessScanningEnabled = 1) ORDER BY c.COMPUTERNAME"), "n");
                av["arbeitsplaetzeOhne"] = ohne.Count;
                av["ohneNamen"] = ohne;
                fl["virenschutz"] = av;
            });
            Section(err, "Updates", delegate()
            {
                JObject u = One(c,
                    "SELECT COUNT(DISTINCT niu.ClientId) AS fehlendClients, COUNT(DISTINCT CASE WHEN u.Severity = 4 THEN niu.ClientId END) AS kritischClients " +
                    "FROM CLT_Wum_NotInstalledUpdates niu JOIN Sys_Wum_Updates u ON u.Id = niu.UpdateId JOIN CLT_CLIENTS_TABLE c ON c.CLIENTID = niu.ClientId " +
                    "WHERE c.IsRetired = 0 AND u.IsSuperseded = 0 AND u.IsDeclined = 0 AND u.IsExpired = 0");
                JObject f = One(c,
                    "SELECT COUNT(DISTINCT f.ClientId) AS clients, COUNT(*) AS fehler FROM CLT_Wum_FailedUpdates f JOIN CLT_CLIENTS_TABLE c ON c.CLIENTID = f.ClientId " +
                    "WHERE c.IsRetired = 0 AND f.ReportDate >= DATEADD(DAY, -7, GETDATE())");
                u["fehlgeschlagenClients"] = f["clients"]; u["fehlgeschlagen"] = f["fehler"];
                fl["updates"] = u;
            });
            Section(err, "Schwachstellen", delegate()
            {
                JObject sc = One(c,
                    "SELECT COUNT(*) AS clients, SUM(CASE WHEN a.LastDataArrival > 2 AND DATEADD(day, -2, CAST(a.LastDataArrival AS datetime)) >= DATEADD(DAY, -@d, GETDATE()) THEN 1 ELSE 0 END) AS frisch, " +
                    "MAX(CASE WHEN a.LastDataArrival > 2 THEN DATEADD(day, -2, CAST(a.LastDataArrival AS datetime)) END) AS neuester " +
                    "FROM CLT_SCANNER_DATA_ARRIVAL a JOIN SYS_SCANNER_DATA_ARRIVAL n ON n.SCANNERID = a.SCANNERID JOIN CLT_CLIENTS_TABLE c ON c.CLIENTID = a.CLIENTID " +
                    "WHERE c.IsRetired = 0 AND n.NAME = 'Vulnerabilities'", "@d", Opt.ScanDays);
                JArray top = Rows(c,
                    "SELECT TOP 1 o.CVEId AS cve, MAX(vs.Score) AS score, COUNT(DISTINCT r.ClientId) AS clients, MAX(LEFT(o.Title, 120)) AS titel " +
                    "FROM CLT_Sec_OvalDefinitions_Results r JOIN SYS_Sec_OvalDefinitions o ON o.OID = r.OID JOIN SYS_Sec_VulnerabilityScoreDefinitions vs ON vs.CVEId = o.CVEId " +
                    "JOIN CLT_CLIENTS_TABLE c ON c.CLIENTID = r.ClientId WHERE c.IsRetired = 0 GROUP BY o.CVEId HAVING MAX(vs.Score) >= 9 " +
                    "ORDER BY COUNT(DISTINCT r.ClientId) DESC, MAX(vs.Score) DESC");
                if (top.Count > 0) sc["topCve"] = top[0];
                fl["scan"] = sc;
            });
            Section(err, "Server", delegate() { snap["server"] = Servers(c); });
            Section(err, "Jobs", delegate() { snap["jobs"] = Jobs(c); });
        }
        if (err.Count > 0) snap["fehler"] = err;
        return snap;
    }

    JArray Servers(SqlConnection c)
    {
        // Die Zeilen bleiben in der Liste aus Rows: ein JObject mit Eltern wuerde beim Einfuegen in eine andere JArray kopiert,
        // die spaeteren Zuordnungen (Laufwerke, Updates) kaemen dann nicht an.
        JArray list = Rows(c,
            "SELECT CAST(c.CLIENTID AS varchar(36)) AS id, c.COMPUTERNAME AS name, o.OSSTRING AS os, v.OnlineState AS online, lc.TimeStamp AS letzterKontakt " +
            "FROM CLT_CLIENTS_TABLE c JOIN CLT_OPERATINGSYSTEM o ON o.CLIENTID = c.CLIENTID LEFT JOIN VW_Inventory_ClientOnlineState v ON v.ClientId = c.CLIENTID " +
            "LEFT JOIN CLT_Clients_LastConnection lc ON lc.ClientId = c.CLIENTID WHERE c.IsRetired = 0 AND o.OSSTRING LIKE '%Server%' ORDER BY c.COMPUTERNAME");
        var byId = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
        foreach (JObject r in list)
        {
            r["laufwerke"] = new JArray(); r["fehlend"] = 0; r["kritisch"] = 0; r["fehlgeschlagen7"] = 0;
            byId[(string)r["id"]] = r;
        }
        foreach (JObject r in Rows(c,
            "SELECT CAST(ld.CLIENTID AS varchar(36)) AS id, ld.DRIVENAME AS lw, CAST(ld.FREESPACE / 1048576.0 AS decimal(10,1)) AS freiGB, " +
            "CAST(100.0 * ld.FREESPACE / ld.CAPACITY AS decimal(5,1)) AS freiPct FROM CLT_HDW_LDRIVES ld JOIN CLT_CLIENTS_TABLE c ON c.CLIENTID = ld.CLIENTID " +
            "JOIN CLT_OPERATINGSYSTEM o ON o.CLIENTID = c.CLIENTID WHERE c.IsRetired = 0 AND o.OSSTRING LIKE '%Server%' AND ld.MediaType = 'Local Disk' AND ld.CAPACITY > 0 " +
            "AND 100.0 * ld.FREESPACE / ld.CAPACITY < @pct ORDER BY 100.0 * ld.FREESPACE / ld.CAPACITY",
            "@pct", Opt.ServerMinFreePct))
        {
            JObject s; if (!byId.TryGetValue((string)r["id"], out s)) continue;
            ((JArray)s["laufwerke"]).Add(new JObject(new JProperty("lw", r["lw"]), new JProperty("freiGB", r["freiGB"]), new JProperty("freiPct", r["freiPct"])));
        }
        foreach (JObject r in Rows(c,
            "SELECT CAST(niu.ClientId AS varchar(36)) AS id, COUNT(DISTINCT niu.UpdateId) AS fehlend, COUNT(DISTINCT CASE WHEN u.Severity = 4 THEN niu.UpdateId END) AS kritisch " +
            "FROM CLT_Wum_NotInstalledUpdates niu JOIN Sys_Wum_Updates u ON u.Id = niu.UpdateId JOIN CLT_CLIENTS_TABLE c ON c.CLIENTID = niu.ClientId " +
            "JOIN CLT_OPERATINGSYSTEM o ON o.CLIENTID = c.CLIENTID WHERE c.IsRetired = 0 AND o.OSSTRING LIKE '%Server%' AND u.IsSuperseded = 0 AND u.IsDeclined = 0 AND u.IsExpired = 0 " +
            "GROUP BY niu.ClientId"))
        {
            JObject s; if (!byId.TryGetValue((string)r["id"], out s)) continue;
            s["fehlend"] = r["fehlend"]; s["kritisch"] = r["kritisch"];
        }
        foreach (JObject r in Rows(c,
            "SELECT CAST(f.ClientId AS varchar(36)) AS id, COUNT(*) AS fehler FROM CLT_Wum_FailedUpdates f JOIN CLT_CLIENTS_TABLE c ON c.CLIENTID = f.ClientId " +
            "JOIN CLT_OPERATINGSYSTEM o ON o.CLIENTID = c.CLIENTID WHERE c.IsRetired = 0 AND o.OSSTRING LIKE '%Server%' AND f.ReportDate >= DATEADD(DAY, -7, GETDATE()) GROUP BY f.ClientId"))
        {
            JObject s; if (!byId.TryGetValue((string)r["id"], out s)) continue;
            s["fehlgeschlagen7"] = r["fehler"];
        }
        foreach (JObject s in list) s.Remove("id");
        return list;
    }

    JObject Jobs(SqlConnection c)
    {
        var o = new JObject();
        if (_jobs != null)
        {
            var a = new JObject(); a["onlyNotable"] = true; a["top"] = AcmpJobs.MaxListed;
            JObject jl = _jobs.List(a);
            o["gesamt"] = jl["gefunden"]; o["auffaellig"] = jl["auffaellig"];
            var liste = new JArray();
            int rot = 0, tot = 0, gelb = 0;
            JArray jobs = jl["jobs"] as JArray;
            if (jobs != null)
                foreach (JObject j in jobs)
                {
                    JArray h = j["hinweise"] as JArray;
                    string first = h != null && h.Count > 0 ? (string)h[0] : "";
                    int rank = AcmpJobs.Rank(first);
                    string stufe = rank == 0 ? "rot" : rank == 1 ? "tot" : rank == 2 ? "gelb" : "info";
                    if (rank == 0) rot++; else if (rank == 1) tot++; else if (rank == 2) gelb++;
                    var e = new JObject();
                    e["name"] = j["name"]; e["ziel"] = j["ziel"]; e["stufe"] = stufe;
                    int colon = first.IndexOf(':');
                    e["hinweis"] = colon > 0 && colon < 6 ? first.Substring(colon + 1).Trim() : first;
                    e["containerLeer"] = first.Contains("Container ist leer");
                    e["abgeschaltet"] = first.Contains("Startbedingung abgeschaltet");
                    liste.Add(e);
                }
            o["rot"] = rot; o["tot"] = tot; o["gelb"] = gelb;
            o["liste"] = liste;
        }
        const string Runs = "SUM(CASE WHEN i.State BETWEEN 2 AND 5 THEN 1 ELSE 0 END)";
        const string Errs = "SUM(CASE WHEN i.State BETWEEN 2 AND 5 AND (i.Result IN (2, 3) OR i.State IN (4, 5)) THEN 1 ELSE 0 END)";
        JObject t = One(c, "DECLARE @now float = CAST(GETDATE() AS float) + 2; SELECT " + Runs + " AS laeufe7, " + Errs + " AS fehler7 FROM SYS_Jobs_Logs_Items i WHERE i.LastActivity >= @now - 7");
        o["laeufe7"] = t["laeufe7"]; o["fehler7"] = t["fehler7"];
        o["meisteFehler"] = Rows(c,
            "DECLARE @now float = CAST(GETDATE() AS float) + 2; SELECT TOP 6 ISNULL(j.Name, N'(Job gelöscht)') AS name, " + Runs + " AS laeufe, " + Errs + " AS fehler " +
            "FROM SYS_Jobs_Logs_Items i LEFT JOIN SYS_Jobs j ON j.Id = i.JobId WHERE i.LastActivity >= @now - 7 GROUP BY ISNULL(j.Name, N'(Job gelöscht)') " +
            "HAVING " + Errs + " >= @min ORDER BY " + Errs + " DESC", "@min", Opt.JobErrorMin);
        return o;
    }

    // ================= Regeln fuer "Jetzt wichtig" (ohne DB, offline getestet) =================
    // Je Befund: stufe (rot/gelb/info), bereich, titel (fett), text. Rot zuerst, hoechstens max.
    public static List<JObject> Findings(JObject snap, DashOptions t, int max)
    {
        var rot = new List<JObject>(); var gelb = new List<JObject>(); var info = new List<JObject>();
        if (snap == null) return rot;
        JObject fl = snap["flotte"] as JObject;

        // Server-Platz
        JArray srv = snap["server"] as JArray;
        if (srv != null)
        {
            var low = new List<string>(); int crit = 0;
            foreach (JObject s in srv)
                foreach (JObject d in (JArray)s["laufwerke"])
                {
                    double gb = Dbl(d["freiGB"]), pct = Dbl(d["freiPct"]);
                    string lw = (string)d["lw"];
                    if (DriveCritical(gb, pct, t))
                    {
                        if (crit++ < 3) rot.Add(F("rot", "Server", (string)s["name"], "Laufwerk " + lw + " nur noch " + Gb(gb) + " frei (" + Pct(pct) + ")."));
                    }
                    else low.Add((string)s["name"] + " " + lw + " " + Pct(pct) + " frei");
                }
            if (crit > 3) rot.Add(F("rot", "Server", (crit - 3) + " weitere Server-Laufwerke", "fast voll (unter " + t.CritFreePct + " % oder unter " + t.CritFreeGB + " GB bei weniger als " + t.ServerMinFreePct + " %)."));
            if (low.Count > 0) gelb.Add(F("gelb", "Server", low.Count == 1 ? "1 Server-Laufwerk" : low.Count + " Server-Laufwerke", "unter " + t.ServerMinFreePct + " % frei: " + Names(low, 3) + "."));
        }

        // Jobs mit vielen Fehlern
        JObject jobs = snap["jobs"] as JObject;
        if (jobs != null && jobs["meisteFehler"] is JArray)
        {
            int nRot = 0; var gelbNamen = new List<string>();
            foreach (JObject j in (JArray)jobs["meisteFehler"])
            {
                long runs = Lng(j["laeufe"]), errs = Lng(j["fehler"]);
                if (runs <= 0 || errs < t.JobErrorMin) continue;
                double rate = 100.0 * errs / runs;
                if (rate < t.JobErrorRatePct) continue;
                if (rate >= 80 && errs >= 10 && nRot < 3) { nRot++; rot.Add(F("rot", "Jobs", (string)j["name"], Num(errs) + " von " + Num(runs) + " Läufen in 7 Tagen mit Fehler.")); }
                else gelbNamen.Add((string)j["name"] + " " + Pct(rate));
            }
            if (gelbNamen.Count > 0) gelb.Add(F("gelb", "Jobs", "Fehlerquote ab " + t.JobErrorRatePct + " %", Names(gelbNamen, 3) + "."));
        }

        if (fl != null)
        {
            JObject av = fl["virenschutz"] as JObject;
            if (av != null && Lng(av["arbeitsplaetzeOhne"]) > 0)
                rot.Add(F("rot", "Flotte", Num(Lng(av["arbeitsplaetzeOhne"])) + " Arbeitsplätze", "ohne aktiven Virenschutz: " + Names(Strs(av["ohneNamen"]), 5) + "."));
            JObject up = fl["updates"] as JObject;
            if (up != null && Lng(up["kritischClients"]) > 0)
            {
                var names = new List<string>();
                if (srv != null) foreach (JObject s in srv) if (Lng(s["kritisch"]) > 0) names.Add((string)s["name"]);
                gelb.Add(F("gelb", "Updates", Num(Lng(up["kritischClients"])) + " Clients", "mit fehlendem kritischen Update" + (names.Count > 0 ? ", darunter die Server " + Names(names, 5) : "") + "."));
            }
            JObject bl = fl["bitlocker"] as JObject;
            if (bl != null && Lng(bl["cOffen"]) > 0)
                gelb.Add(F("gelb", "Flotte", Num(Lng(bl["cOffen"])) + " von " + Num(Lng(bl["mitDaten"])) + " Clients", "mit BitLocker-Daten: Laufwerk C: ist nicht geschützt."));
        }

        if (jobs != null && Lng(jobs["tot"]) > 0)
        {
            int leer = 0, aus = 0;
            if (jobs["liste"] is JArray) foreach (JObject j in (JArray)jobs["liste"]) { if (j["containerLeer"] != null && (bool)j["containerLeer"]) leer++; if (j["abgeschaltet"] != null && (bool)j["abgeschaltet"]) aus++; }
            var parts = new List<string>();
            if (leer > 0) parts.Add(leer + (leer == 1 ? " hängt" : " hängen") + " an leeren Containern");
            if (aus > 0) parts.Add(aus + (aus == 1 ? " ist" : " sind") + " abgeschaltet");
            gelb.Add(F("gelb", "Jobs", Num(Lng(jobs["tot"])) + " Daueraufträge laufen nie", parts.Count > 0 ? string.Join(", ", parts.ToArray()) + "." : "siehe acmp_jobs onlyNotable=true."));
        }

        if (fl != null)
        {
            JObject sc = fl["scan"] as JObject;
            if (sc != null && Lng(sc["clients"]) > 0 && Lng(sc["frisch"]) < Lng(sc["clients"]))
                info.Add(F("info", "Scan", Num(Lng(sc["clients"]) - Lng(sc["frisch"])) + " von " + Num(Lng(sc["clients"])) + " Clients", "ohne Schwachstellen-Scan seit " + t.ScanDays + " Tagen."));
            JObject up = fl["updates"] as JObject;
            if (up != null && Lng(up["fehlgeschlagenClients"]) > 0)
                info.Add(F("info", "Updates", Num(Lng(up["fehlgeschlagenClients"])) + " Clients", "mit " + Num(Lng(up["fehlgeschlagen"])) + " fehlgeschlagenen Update-Installationen in 7 Tagen."));
            if (Lng(fl["langeOffline"]) > 0)
                info.Add(F("info", "Flotte", Num(Lng(fl["langeOffline"])) + " Clients", "seit mehr als " + t.OfflineDays + " Tagen ohne Kontakt: " + Names(Strs(fl["langeOfflineNamen"]), 4) + "."));
            if (Lng(fl["nieVerbunden"]) > 0)
                info.Add(F("info", "Flotte", Num(Lng(fl["nieVerbunden"])) + " Clients", "waren nie mit dem ACMP-Server verbunden (z.B. alte Scan-Einträge oder Netzwerk-Boot-Platzhalter)."));
        }

        JObject fe = snap["fehler"] as JObject;
        if (fe != null) foreach (JProperty p in fe.Properties()) gelb.Insert(0, F("gelb", "Daten", "Abschnitt " + p.Name, "nicht verfügbar: " + (string)p.Value));

        var all = new List<JObject>(); all.AddRange(rot); all.AddRange(gelb); all.AddRange(info);
        if (all.Count > max) all = all.GetRange(0, max);
        return all;
    }

    // Server-Laufwerk fast voll: unter critFreePct, oder unter critFreeGB und zugleich unter serverMinFreePct
    // (ein kleines Laufwerk mit 1,7 GB und 33 % frei ist kein Notfall)
    public static bool DriveCritical(double gb, double pct, DashOptions t)
    {
        return pct < t.CritFreePct || (gb < t.CritFreeGB && pct < t.ServerMinFreePct);
    }

    static JObject F(string stufe, string bereich, string titel, string text)
    {
        var o = new JObject(); o["stufe"] = stufe; o["bereich"] = bereich; o["titel"] = titel; o["text"] = text; return o;
    }

    // ================= Hilfen =================
    // Zahl aus dem Schnappschuss; JValue.ToString() wuerde die Ortskultur nehmen ("136,6"), deshalb den Wert direkt umwandeln
    public static double Dbl(JToken t)
    {
        double d;
        if (t == null || t.Type == JTokenType.Null) return 0;
        if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) return Convert.ToDouble(((JValue)t).Value, CultureInfo.InvariantCulture);
        string s = t.Type == JTokenType.String ? (string)t : t.ToString();
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : 0;
    }
    public static long Lng(JToken t) { return (long)Math.Round(Dbl(t)); }
    public static string Num(long n) { return n.ToString("#,0", CultureInfo.GetCultureInfo("de-DE")); }
    public static string Gb(double gb) { return gb.ToString(gb < 10 ? "0.0" : "#,0", CultureInfo.GetCultureInfo("de-DE")) + " GB"; }
    public static string Pct(double p) { return p.ToString("0.#", CultureInfo.GetCultureInfo("de-DE")) + " %"; }
    static List<string> Strs(JToken t) { var l = new List<string>(); if (t is JArray) foreach (JToken x in (JArray)t) l.Add((string)x); return l; }
    public static string Names(List<string> l, int max)
    {
        if (l.Count <= max) return string.Join(", ", l.ToArray());
        return string.Join(", ", l.GetRange(0, max).ToArray()) + " und " + (l.Count - max) + " weitere";
    }

    void Section(JObject err, string name, Action body)
    {
        try { body(); }
        catch (Exception ex) { err[name] = ex.Message; _log("Dashboard: Abschnitt " + name + " fehlgeschlagen: " + ex.Message); }
    }

    static JArray Col(JArray rows, string col)
    {
        var a = new JArray(); foreach (JObject r in rows) a.Add(r[col]); return a;
    }

    static JObject One(SqlConnection c, string sql, params object[] kv)
    {
        JArray r = Rows(c, sql, kv);
        return r.Count > 0 ? (JObject)r[0] : new JObject();
    }

    // Abfrage mit Parametern als Paare ("@name", Wert); Zahlen bleiben Zahlen, Datum als "JJJJ-MM-TT HH:mm:ss"
    static JArray Rows(SqlConnection c, string sql, params object[] kv)
    {
        var arr = new JArray();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = sql; cmd.CommandTimeout = SqlTimeout;
            for (int i = 0; i + 1 < kv.Length; i += 2) cmd.Parameters.AddWithValue((string)kv[i], kv[i + 1]);
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var o = new JObject();
                    for (int i = 0; i < r.FieldCount; i++)
                    {
                        object v = r.GetValue(i);
                        JToken tok;
                        if (v == null || v is DBNull) tok = JValue.CreateNull();
                        else if (v is DateTime) tok = ((DateTime)v).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                        else if (v is int || v is long || v is short || v is byte) tok = Convert.ToInt64(v, CultureInfo.InvariantCulture);
                        else if (v is float) tok = (double)(decimal)(float)v;   // real-Spalten: 9.1 statt 9.1000003814697266
                        else if (v is decimal || v is double) tok = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                        else if (v is bool) tok = (bool)v;
                        else tok = Convert.ToString(v, CultureInfo.InvariantCulture);
                        o[r.GetName(i)] = tok;
                    }
                    arr.Add(o);
                }
        }
        return arr;
    }
}
