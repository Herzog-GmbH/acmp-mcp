// ACMP-MCP-Server (net48, kompiliert mit csc.exe)
// Stellt die ACMP Public API (SICS) als MCP-Server ueber HTTP bereit.
// - Verbindung via Aagons Client-DLLs (bewiesene Pipeline aus dem Spike)
// - MCP ueber Streamable HTTP (HttpListener, JSON-RPC 2.0), abgesichert per Bearer-API-Key
// - Tools: generisches acmp_call (alle Kommandos) + typisierte Komfort-Tools
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Security.Cryptography.X509Certificates;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

static class AcmpMcp
{
    const string SicsDir = @"C:\Program Files (x86)\Aagon\AESB SICS\SICS";
    [ThreadStatic] static AcmpConn _currentConn;          // pro Request/Job aktive Pro-User-Verbindung
    static AcmpConn _defaultConn;                          // Fallback (Operator/SICS)
    static AcmpConn CONN { get { return _currentConn != null ? _currentConn : _defaultConn; } }
    static readonly Dictionary<string, AcmpConn> ConnPool = new Dictionary<string, AcmpConn>(StringComparer.OrdinalIgnoreCase);
    static readonly object PoolLock = new object();
    static Dictionary<string, JObject> AcmpIdentities;    // identityId -> Secret-Eintrag (aus secrets.local.json)
    static readonly Dictionary<string, string> RequesterToIdentity = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    static string ConnHost, AcmpSrvName, AcmpSrvId;       // fuer Pro-User-Verbindungen
    static AcmpDb DB;
    static AcmpApproval APPROVAL;   // Vier-Augen-Freigabe (serverseitig erzwungen)
    static JArray DbToolDefs;  // nicht inline initialisieren (laeuft sonst im .cctor vor dem AssemblyResolve-Handler)
    static JArray ApiToolDefs; // datengetriebene API-Tools aus api-tools.json (Public-API-Kommandos ohne Recompile)
    static string ApiKey;
    static Dictionary<string, string> ApiKeyUsers;   // Bearer-Key -> Antragsteller-Name (Identitaet je Bediener)
    static string LogFile;
    static HashSet<string> NoApprovalTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, string> GatedToolTiers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "acmp_enqueue_client_command", "Write" }, { "acmp_run_powershell", "Destructive" },
        { "acmp_save_containers", "Write" },        { "acmp_delete_containers", "Destructive" },
        { "acmp_create_container", "Write" },       { "acmp_lend_asset", "Write" },
        { "acmp_return_asset", "Write" },           { "acmp_create_ticket", "Write" },
        { "acmp_update_ticket", "Write" },          { "acmp_close_ticket", "Write" },
        { "acmp_reopen_ticket", "Write" },          { "acmp_add_ticket_comment", "Write" },
        { "acmp_create_contact", "Write" },         { "acmp_update_contact", "Write" },
        { "acmp_create_company", "Write" },         { "acmp_update_company", "Write" },
        { "acmp_create_location", "Write" },        { "acmp_update_location", "Write" },
        { "acmp_create_department", "Write" },      { "acmp_update_department", "Write" },
        { "acmp_create_asset", "Write" },           { "acmp_update_asset", "Write" },
        { "acmp_call", "Write" },
    };
    static string TicketCreatedStateId = "{0E9A3C65-5EA3-42BE-80E9-B71A2C4F01C2}"; // Ticketstatus "Erstellt" fuer neue Tickets (Config: helpdesk.createdStateId)
    static string PsCommandId;            // ClientCommandId der Vorlagen-Aktion fuer acmp_run_powershell (aus Config)
    static string PsVariableName = "SCRIPT"; // Name der Skript-Variable in dieser Vorlage
    static string PublicUrl;                // erreichbare Basis-URL fuer Client-Callbacks (aus approval.publicUrl)
    // Offene Registry-Leseauftraege (acmp_read_registry): Token -> Auftrag. Der Client meldet das Ergebnis per HTTP an /regresult zurueck.
    static readonly Dictionary<string, RegRead> RegReads = new Dictionary<string, RegRead>(StringComparer.OrdinalIgnoreCase);
    class RegRead
    {
        public string Client; public string Path; public string Value;
        public DateTime Created;
        public bool Done; public bool Ok; public string Result; public string Error;
        public ManualResetEvent Signal = new ManualResetEvent(false);
    }

    static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object s, ResolveEventArgs e)
        {
            string name = new AssemblyName(e.Name).Name;
            string p = Path.Combine(SicsDir, name + ".dll");
            if (File.Exists(p)) { try { return Assembly.LoadFrom(p); } catch { return null; } }
            return null;
        };
        try { return Run(args); }
        catch (Exception ex) { Console.Error.WriteLine("FATAL: " + ex); return 99; }
    }

    static int Run(string[] args)
    {
        string cfgPath = args.Length > 0 ? args[0] : "config.local.json";
        if (!File.Exists(cfgPath)) { Console.Error.WriteLine("Config nicht gefunden: " + cfgPath); return 1; }
        JObject cfg = JObject.Parse(File.ReadAllText(cfgPath));
        // Basisverzeichnis = Ordner der Config. Relative Pfade (logFile, auditFile, pendingDir) werden dagegen aufgeloest -> Ordner portabel.
        string cfgDir = Path.GetDirectoryName(Path.GetFullPath(cfgPath));

        JObject sics = (JObject)cfg["sics"];
        string user = (string)sics["username"];
        string pass = (string)sics["password"];
        string host = (string)sics["host"]; if (string.IsNullOrEmpty(host)) host = "127.0.0.1";
        string authType = sics["authType"] != null ? (string)sics["authType"] : "sics";
        string domain = sics["domain"] != null ? (string)sics["domain"] : null;
        string acmpServerName = sics["acmpServerName"] != null ? (string)sics["acmpServerName"] : "ACMP";
        string acmpServerId = sics["acmpServerId"] != null ? (string)sics["acmpServerId"] : null;
        if (sics["virtualRouter"] != null && ((string)sics["virtualRouter"]).Length > 0) AcmpConn.DefaultVirtualRouter = (string)sics["virtualRouter"];
        if (sics["wssPort"] != null) AcmpConn.DefaultWssPort = (int)sics["wssPort"];
        JObject hdCfg = cfg["helpdesk"] as JObject;
        if (hdCfg != null && hdCfg["createdStateId"] != null && ((string)hdCfg["createdStateId"]).Length > 0) TicketCreatedStateId = (string)hdCfg["createdStateId"];

        JObject mcp = (JObject)cfg["mcp"];
        int port = mcp != null && mcp["httpPort"] != null ? (int)mcp["httpPort"] : 8765;
        string bind = mcp != null && mcp["bindAddress"] != null ? (string)mcp["bindAddress"] : "0.0.0.0";
        ApiKey = mcp != null && mcp["apiKey"] != null ? (string)mcp["apiKey"] : null;
        if (ApiKey != null && (ApiKey.Length == 0 || ApiKey.StartsWith("HIER_"))) ApiKey = null;

        // Bearer-Key -> Antragsteller-Identitaet. Legacy mcp.apiKey = Standard-Antragsteller (approval.requester);
        // weitere Bediener via mcp.users[{name, apiKey}]. So weiss der Server pro Aufruf, WER ihn ausloest.
        ApiKeyUsers = new Dictionary<string, string>();
        JObject apprForReq = cfg["approval"] as JObject;
        string defaultReq = (apprForReq != null && apprForReq["requester"] != null) ? (string)apprForReq["requester"] : null;
        if (ApiKey != null) ApiKeyUsers[ApiKey] = defaultReq;
        JArray mcpUsers = mcp != null ? mcp["users"] as JArray : null;
        if (mcpUsers != null)
            foreach (JObject u in mcpUsers)
            {
                string un = (string)u["name"]; string uk = (string)u["apiKey"];
                if (!string.IsNullOrEmpty(un) && !string.IsNullOrEmpty(uk) && !uk.StartsWith("HIER_")) ApiKeyUsers[uk] = un;
            }
        LogFile = mcp != null && mcp["logFile"] != null ? (string)mcp["logFile"] : null;
        if (LogFile != null && LogFile.Length == 0) LogFile = null;
        if (LogFile != null && !Path.IsPathRooted(LogFile)) LogFile = Path.Combine(cfgDir, LogFile);

        // Vorlagen-Aktion fuer das Komfort-Tool acmp_run_powershell (ad-hoc PowerShell auf einem Client)
        JObject psCfg = cfg["runPowershell"] as JObject;
        if (psCfg != null)
        {
            PsCommandId = psCfg["clientCommandId"] != null ? (string)psCfg["clientCommandId"] : null;
            if (PsCommandId != null && (PsCommandId.Length == 0 || PsCommandId.StartsWith("HIER_"))) PsCommandId = null;
            if (psCfg["variableName"] != null && ((string)psCfg["variableName"]).Length > 0) PsVariableName = (string)psCfg["variableName"];
        }

        Log("Verbinde mit ACMP SICS (" + host + ") als '" + user + "' ...");
        ConnHost = host; AcmpSrvName = acmpServerName;
        _defaultConn = new AcmpConn(SicsDir, host, user, pass, authType, domain, acmpServerName, acmpServerId);
        Log("Auth-Modus: " + authType + (authType.ToLowerInvariant() == "acmpdomain" ? " (Domain: " + domain + ")" : " (SICS-/AESB-User)"));
        for (int attempt = 1; ; attempt++)
        {
            try { CONN.Connect(); break; }
            catch (Exception ex)
            {
                Log("Verbindung fehlgeschlagen (Versuch " + attempt + "): " + ex.Message);
                if (attempt >= 24) { Console.Error.WriteLine("Gebe nach 24 Versuchen auf."); return 5; }
                Thread.Sleep(5000); // wartet z.B. beim Systemstart auf den SICS-Dienst
            }
        }
        Log("ACMP-Verbindung steht. ServerInfo: " + Truncate(CONN.Call("GetACMPServerInfo", 1, ""), 200));
        AcmpSrvId = _defaultConn.DiscoverAcmpServerId();
        if (string.IsNullOrEmpty(AcmpSrvId)) AcmpSrvId = acmpServerId;
        LoadAcmpIdentities(cfgDir, mcp, defaultReq);

        // Optionale read-only DB-Anbindung fuer Report-Tools
        string dbCs = (cfg["db"] != null && cfg["db"]["connectionString"] != null) ? (string)cfg["db"]["connectionString"] : null;
        if (dbCs != null && dbCs.StartsWith("HIER_")) dbCs = null;
        DB = new AcmpDb(dbCs);
        Log("DB-Report-Tools: " + (DB.Enabled ? "aktiv" : "aus (keine db.connectionString gesetzt)"));
        Log("acmp_run_powershell: " + (PsCommandId != null ? "aktiv (Vorlage " + PsCommandId + ", Variable " + PsVariableName + ")" : "aus (runPowershell.clientCommandId nicht gesetzt)"));
        DbToolDefs = new JArray();
        if (DB.Enabled)
        {
            string toolsFile = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(cfgPath)), "db-tools.json");
            if (File.Exists(toolsFile))
            {
                try { DbToolDefs = JArray.Parse(File.ReadAllText(toolsFile)); Log("DB-Report-Tools geladen aus db-tools.json: " + DbToolDefs.Count); }
                catch (Exception ex) { Log("db-tools.json Fehler: " + ex.Message); }
            }
        }

        // Datengetriebene API-Tools (api-tools.json): jedes Public-API-Kommando als Tool, ohne Recompile.
        ApiToolDefs = new JArray();
        string apiToolsFile = Path.Combine(cfgDir, "api-tools.json");
        if (File.Exists(apiToolsFile))
        {
            try
            {
                ApiToolDefs = JArray.Parse(File.ReadAllText(apiToolsFile));
                foreach (JObject d in ApiToolDefs)
                {
                    string tn = (string)d["name"]; string tt = d["tier"] != null ? (string)d["tier"] : null;
                    if (!string.IsNullOrEmpty(tn) && !string.IsNullOrEmpty(tt) && tt != "Read") GatedToolTiers[tn] = tt;
                }
                Log("API-Tools geladen aus api-tools.json: " + ApiToolDefs.Count);
            }
            catch (Exception ex) { Log("api-tools.json Fehler: " + ex.Message); }
        }

        // Vier-Augen-Freigabe initialisieren (schreibende/ausfuehrende Kommandos brauchen menschliche Zustimmung).
        JObject apprCfg = cfg["approval"] as JObject;
        JArray naTools = apprCfg != null ? apprCfg["noApprovalTools"] as JArray : null;
        if (naTools != null)
            foreach (JToken nt in naTools) { string nts = (string)nt; if (!string.IsNullOrEmpty(nts)) NoApprovalTools.Add(nts); }
        string pubUrl = (apprCfg != null && apprCfg["publicUrl"] != null && ((string)apprCfg["publicUrl"]).Length > 0)
            ? (string)apprCfg["publicUrl"] : ("http://" + Environment.MachineName + ":" + port);
        PublicUrl = pubUrl.TrimEnd('/'); // Basis-URL fuer den Registry-Rueckkanal (Client -> /regresult)
        APPROVAL = new AcmpApproval(apprCfg, cfgDir, pubUrl, delegate(JObject req)
        {
            // Ausfuehrung des freigegebenen Jobs als der urspruengliche Antragsteller (Pro-User-Identitaet).
            AcmpConn prev = _currentConn;
            _currentConn = ResolveConn(req["requester"] != null ? (string)req["requester"] : null);
            try
            {
                string mode = (string)req["mode"];
                if (mode == "publish") return CONN.Publish((string)req["raw"]);
                int v = req["version"] != null ? (int)req["version"] : 1;
                return CONN.Call((string)req["command"], v, (string)req["inner"]);
            }
            finally { _currentConn = prev; }
        }, new Action<string>(Log));
        APPROVAL.ResolveClientName = new Func<string, string>(ResolveClientName);
        APPROVAL.ResolveClientUser = new Func<string, string>(ResolveClientUser);
        APPROVAL.ResolveCommandName = new Func<string, string>(ResolveCommandName);
        APPROVAL.CfgPath = Path.GetFullPath(cfgPath);
        APPROVAL.NoApprovalTools = NoApprovalTools;
        APPROVAL.GatedToolTiers = GatedToolTiers;
        APPROVAL.BypassAll = apprCfg != null && apprCfg["bypassAll"] != null && (bool)apprCfg["bypassAll"];
        APPROVAL.GetAllTools = delegate() { return (JArray)ToolsList()["tools"]; };
        var toolCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        JObject tcCfg = apprCfg != null ? apprCfg["toolApprovalCounts"] as JObject : null;
        if (tcCfg != null)
            foreach (JProperty tp in tcCfg.Properties()) { int tv; if (int.TryParse(tp.Value.ToString(), out tv)) toolCounts[tp.Name] = tv; }
        foreach (KeyValuePair<string, int> tc in toolCounts) if (tc.Value == 0) NoApprovalTools.Add(tc.Key);
        APPROVAL.ToolApprovalCounts = toolCounts;
        if (APPROVAL.Enabled)
        {
            APPROVAL.LoadPending();
            APPROVAL.StartWorker();
            Log("Vier-Augen-Freigabe AKTIV (Schreib-/Ausfuehr-Kommandos brauchen Zustimmung; Freigabe-Seite: " + pubUrl + "/approvals).");
            Log("Freigeber-Benachrichtigung per E-Mail: " + (APPROVAL.NotifyEnabled ? "AKTIV" : "AUS (notify.smtpHost/from nicht gesetzt)"));
        }
        else Log("Vier-Augen-Freigabe AUS (keine approval-Section in der Config) - Schreib-/Ausfuehr-Kommandos laufen direkt.");

        System.Net.IPAddress ip;
        bool loopback = (bind == "127.0.0.1" || bind == "localhost" || bind == "::1");
        if (bind == "0.0.0.0" || bind == "*" || bind == "+") ip = System.Net.IPAddress.Any;
        else if (loopback) ip = System.Net.IPAddress.Loopback;
        else if (!System.Net.IPAddress.TryParse(bind, out ip)) { Console.Error.WriteLine("Ungueltige bindAddress: " + bind); return 1; }

        // SICHERHEITS-SCHRANKE: Netzwerk-Exposition nur mit mind. einem gesetzten API-Key.
        if (!loopback && ApiKeyUsers.Count == 0)
        {
            Console.Error.WriteLine("ABBRUCH: bindAddress '" + bind + "' ist netzwerk-erreichbar, aber kein API-Key gesetzt.");
            Console.Error.WriteLine("Setze mcp.apiKey (und/oder mcp.users) ODER binde an 127.0.0.1 (nur lokal).");
            return 4;
        }

        var listener = new System.Net.Sockets.TcpListener(ip, port);
        try { listener.Start(); }
        catch (Exception ex) { Console.Error.WriteLine("TcpListener.Start fehlgeschlagen: " + ex.Message); return 3; }
        Log("MCP-Server laeuft: http://" + bind + ":" + port + "/  (API-Keys: " + ApiKeyUsers.Count + (ApiKeyUsers.Count == 0 ? " - nur localhost" : " Bediener-Identitaeten") + ")");
        Log("Beenden mit Strg+C.");

        while (true)
        {
            System.Net.Sockets.TcpClient cli;
            try { cli = listener.AcceptTcpClient(); }
            catch { break; }
            ThreadPool.QueueUserWorkItem(delegate(object o) { try { HandleClient((System.Net.Sockets.TcpClient)o); } catch (Exception ex) { Log("Handler-Fehler: " + ex.Message); } }, cli);
        }
        return 0;
    }

    static void HandleClient(System.Net.Sockets.TcpClient cli)
    {
        cli.ReceiveTimeout = 60000; cli.SendTimeout = 60000;
        using (var ns = cli.GetStream())
        {
            // HTTP-Header bis \r\n\r\n lesen
            var memo = new MemoryStream();
            int state = 0; var one = new byte[1];
            while (state < 4)
            {
                int n = ns.Read(one, 0, 1);
                if (n <= 0) return;
                byte cur = one[0]; memo.WriteByte(cur);
                if ((state == 0 || state == 2) && cur == 13) state++;
                else if ((state == 1 || state == 3) && cur == 10) state++;
                else state = 0;
                if (memo.Length > 65536) return;
            }
            string[] lines = Encoding.ASCII.GetString(memo.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] rl = lines.Length > 0 ? lines[0].Split(' ') : new string[0];
            string method = rl.Length > 0 ? rl[0] : "";
            var hdr = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++) { int c = lines[i].IndexOf(':'); if (c > 0) hdr[lines[i].Substring(0, c).Trim()] = lines[i].Substring(c + 1).Trim(); }

            if (method == "OPTIONS") { WriteResp(ns, 204, null, null); return; }

            // Web-Freigabeseite (GET) - eigene Token-Auth, NICHT der MCP-Bearer-Key (Menschen im Browser).
            if (method == "GET")
            {
                string path = rl.Length > 1 ? rl[1] : "/";
                if (APPROVAL != null && APPROVAL.Enabled && APPROVAL.IsApprovalPath(path))
                {
                    int code; string ctype; string html;
                    APPROVAL.HandleHttp(path, out code, out ctype, out html);
                    WriteResp(ns, code, ctype, html);
                    return;
                }
            }

            // Rueckkanal fuer acmp_read_registry: der Client POSTet hierher das ausgelesene Ergebnis.
            // Eigene Auth ueber das Token IM Body (der Client kennt den MCP-Bearer-Key nicht).
            if (method == "POST")
            {
                string rpath = rl.Length > 1 ? rl[1] : "/";
                int q = rpath.IndexOf('?'); if (q >= 0) rpath = rpath.Substring(0, q);
                if (rpath == "/regresult")
                {
                    int clen2 = 0; string cl2; if (hdr.TryGetValue("Content-Length", out cl2)) int.TryParse(cl2, out clen2);
                    byte[] b2 = new byte[clen2]; int rd2 = 0;
                    while (rd2 < clen2) { int n = ns.Read(b2, rd2, clen2 - rd2); if (n <= 0) break; rd2 += n; }
                    HandleRegResult(Encoding.UTF8.GetString(b2, 0, rd2));
                    WriteResp(ns, 200, "application/json", "{\"ok\":true}");
                    return;
                }
            }

            string requester = null;
            if (ApiKeyUsers != null && ApiKeyUsers.Count > 0)
            {
                string auth; hdr.TryGetValue("Authorization", out auth);
                string tok = (auth != null && auth.StartsWith("Bearer ")) ? auth.Substring(7).Trim() : null;
                if (tok == null || !ApiKeyUsers.TryGetValue(tok, out requester)) { WriteResp(ns, 401, "application/json", "{\"error\":\"unauthorized\"}"); return; }
            }
            if (method != "POST") { WriteResp(ns, 405, "text/plain", "Method Not Allowed (use POST for MCP)"); return; }

            int clen = 0; string cl; if (hdr.TryGetValue("Content-Length", out cl)) int.TryParse(cl, out clen);
            byte[] body = new byte[clen]; int rd = 0;
            while (rd < clen) { int n = ns.Read(body, rd, clen - rd); if (n <= 0) break; rd += n; }
            string bodyStr = Encoding.UTF8.GetString(body, 0, rd);

            JToken parsed;
            try { parsed = JToken.Parse(bodyStr); }
            catch { WriteResp(ns, 200, "application/json", JsonRpcError(null, -32700, "Parse error").ToString(Formatting.None)); return; }

            JToken result;
            if (parsed is JArray)
            {
                var arr = new JArray();
                foreach (JToken item in (JArray)parsed) { var r = Dispatch(item as JObject, requester); if (r != null) arr.Add(r); }
                result = arr.Count > 0 ? (JToken)arr : null;
            }
            else result = Dispatch(parsed as JObject, requester);

            if (result == null) { WriteResp(ns, 202, null, null); return; }
            WriteResp(ns, 200, "application/json", result.ToString(Formatting.None));
        }
    }

    static void WriteResp(System.Net.Sockets.NetworkStream ns, int status, string ctype, string body)
    {
        byte[] bb = body != null ? Encoding.UTF8.GetBytes(body) : new byte[0];
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(status).Append(" ").Append(StatusText(status)).Append("\r\n");
        sb.Append("Access-Control-Allow-Origin: *\r\n");
        sb.Append("Access-Control-Allow-Headers: Authorization, Content-Type, Mcp-Session-Id\r\n");
        sb.Append("Access-Control-Allow-Methods: POST, GET, OPTIONS\r\n");
        if (ctype != null) sb.Append("Content-Type: ").Append(ctype).Append("\r\n");
        sb.Append("Content-Length: ").Append(bb.Length).Append("\r\n");
        sb.Append("Connection: close\r\n\r\n");
        byte[] hb = Encoding.ASCII.GetBytes(sb.ToString());
        ns.Write(hb, 0, hb.Length);
        if (bb.Length > 0) ns.Write(bb, 0, bb.Length);
        ns.Flush();
    }
    static string StatusText(int s) { if (s == 200) return "OK"; if (s == 202) return "Accepted"; if (s == 204) return "No Content"; if (s == 401) return "Unauthorized"; if (s == 405) return "Method Not Allowed"; return "Status"; }

    // Verarbeitet eine JSON-RPC-Nachricht; gibt Response-Objekt zurueck oder null (Notification)
    static JObject Dispatch(JObject msg, string requester)
    {
        if (msg == null) return JsonRpcError(null, -32600, "Invalid Request");
        JToken idTok = msg["id"];
        string method = (string)msg["method"];
        JObject prm = msg["params"] as JObject;

        if (method == null) return idTok != null ? JsonRpcError(idTok, -32600, "Invalid Request") : null;

        // Notifications (keine id) -> keine Antwort
        if (idTok == null)
        {
            return null; // notifications/initialized usw.
        }

        try
        {
            if (method == "initialize")
            {
                var caps = new JObject();
                caps["tools"] = new JObject();
                var info = new JObject(); info["name"] = "acmp-mcp"; info["version"] = "1.0.0";
                var r = new JObject();
                r["protocolVersion"] = prm != null && prm["protocolVersion"] != null ? prm["protocolVersion"] : "2025-06-18";
                r["capabilities"] = caps;
                r["serverInfo"] = info;
                r["instructions"] =
                    "ACMP Public API ueber MCP. Du hast fertige, benannte Tools fuer die haeufigen Aufgaben - NUTZE DIESE DIREKT.\n\n" +
                    "TOOL-AUSWAHL (wichtig):\n" +
                    "- Pruefe fuer jede Aufgabe ZUERST, ob ein passendes acmp_*-Tool existiert (siehe Aufgabe->Tool unten) und rufe es direkt auf. Rate nicht und suche keine Loesung ausserhalb dieser Tools.\n" +
                    "- Tool unklar? Einmal acmp_list_commands (Katalog) bzw. acmp_describe { command } - NICHT raten, NICHT auf Tools verzichten.\n" +
                    "- Nur wenn es KEIN spezifisches Tool gibt: generisch acmp_call { command, version, inner } ('inner' = XML-Felder in <TCommandRequest_Vx>, leer wenn parameterlos).\n" +
                    "- Antworten als JSON statt XML: jedem Tool 'format':'json' mitgeben.\n\n" +
                    "WICHTIG: ACMP liefert keine Schemas. Pflichtfelder verraten sich ueber Fehlermeldungen (z.B. ResultCode 35 'ClientCommandId: Value may not be empty'). Bei Unsicherheit erst mit minimalem Request testen und aus der Fehlermeldung lernen.\n" +
                    "SICHERHEIT (Vier-Augen): Lese-Kommandos (Get*/List* usw.) laufen sofort. Schreib-/Ausfuehr-/Loesch-Kommandos (Save*/Set*/Enqueue*/Delete*, acmp_run_powershell, ...) werden NICHT sofort ausgefuehrt - der Server legt eine Freigabe-Anfrage an und gibt eine Anfrage-Id zurueck. Erst nachdem genug verschiedene IT-Mitarbeiter ueber die Web-Freigabeseite (out-of-band) zugestimmt haben, fuehrt der Server den eingefrorenen Befehl aus. Gib bei solchen Tools immer ein aussagekraeftiges 'intent' (was/warum) mit. Den Stand danach mit acmp_approval_status { id } abfragen. Du kannst und musst die Freigabe NICHT selbst erteilen.\n" +
                    "FLOTTENWEITE ANALYSEN (KOSTEN / PFLICHT-RUECKFRAGE): Dieser Server liefert Daten pro Client - ein Aufruf = ein Client. Wenn du dieselbe Information ueber VIELE Clients zusammentragen willst (z.B. Schwachstellen/CVEs, Inventar, Defender-/BitLocker-Status ueber die GANZE Flotte; Iteration ueber alle Clients aus GetClientList; ein Sub-Agent oder Workflow pro Client; Bulk-Scan), ist das sehr rechen- und tokenintensiv (leicht Millionen Tokens und viele Minuten Laufzeit). DU MUSST IN DIESEM FALL ZUERST DEN USER INFORMIEREN UND EINE AUSDRUECKLICHE BESTAETIGUNG ABWARTEN, BEVOR du den Massenabruf oder einen flottenweiten Workflow/Fan-out startest. Nenne dem User dabei: ungefaehre Anzahl betroffener Clients, dass es viel Rechenleistung und Tokens kostet, und ob wirklich die ganze Flotte statt einer Stichprobe gemeint ist. Einzelne Clients oder kleine Stichproben (Daumenregel: bis ca. 25 Clients) darfst du ohne Rueckfrage abrufen. Starte NIEMALS ungefragt einen Workflow oder Sub-Agenten-Fan-out ueber die ganze Flotte - erst nach klarem 'ja' des Users.\n" +
                    "Typische Get-Tools laufen sofort; Schreib-/Loesch-Tools sind freigabepflichtig.\n" +
                    "AUFGABE -> TOOL (bevorzuge diese benannten Tools statt acmp_call):\n" +
                    "- Wer bin ich / als welcher ACMP-User laeuft das: acmp_whoami\n" +
                    "- Clients/Computer auflisten: acmp_get_clients ; ClientId zu einem Namen finden: acmp_get_client_id\n" +
                    "- Software/Hardware/Betriebssystem eines Clients: acmp_get_software / acmp_get_hardware / acmp_get_os_info\n" +
                    "- Tickets: Liste acmp_get_tickets ; einzelnes acmp_get_ticket ; anlegen acmp_create_ticket ; aendern acmp_update_ticket ; Kommentar acmp_add_ticket_comment ; schliessen/wieder oeffnen acmp_close_ticket/acmp_reopen_ticket ; per Query acmp_tickets_by_query\n" +
                    "- Assets: Liste acmp_get_asset_list ; anlegen acmp_create_asset ; aendern acmp_update_asset ; ausleihen/zurueck acmp_lend_asset/acmp_return_asset\n" +
                    "- Stammdaten lesen: acmp_get_contacts/_companies/_locations/_departments/_cost_centers/_contracts ; anlegen/aendern: acmp_create_*/acmp_update_* (contact/company/location/department)\n" +
                    "- Skript/Job auf einem Client: acmp_run_powershell bzw. acmp_enqueue_client_command\n" +
                    "- Container: acmp_create_container/acmp_save_containers/acmp_delete_containers\n" +
                    "- Lizenzen/Einstellungen: acmp_get_licenses / acmp_get_app_settings / acmp_get_master_data_settings / acmp_get_custom_field_list\n" +
                    "- Schwachstellen/Updates/Inventar (DB-Reports): acmp_outdated_software / acmp_missing_updates / acmp_software_search\n" +
                    "Tool-Name unbekannt: tools/list zeigt alle Tools, acmp_describe { command } zeigt Details. Es ist fast immer besser, ein vorhandenes Tool zu nutzen, als eine Aufgabe ohne Tool zu loesen.";
                return JsonRpcResult(idTok, r);
            }
            if (method == "ping") return JsonRpcResult(idTok, new JObject());
            if (method == "tools/list") return JsonRpcResult(idTok, ToolsList());
            if (method == "tools/call")
            {
                // Pro-User-Verbindung fuer die Dauer dieses (synchronen) Aufrufs aktiv setzen.
                AcmpConn prevConn = _currentConn;
                _currentConn = ResolveConn(requester);
                try { return JsonRpcResult(idTok, ToolsCall(prm, requester)); }
                finally { _currentConn = prevConn; }
            }
            return JsonRpcError(idTok, -32601, "Method not found: " + method);
        }
        catch (Exception ex)
        {
            return JsonRpcError(idTok, -32000, ex.Message);
        }
    }

    // ---------- Tools ----------
    static JObject Str(string type) { var o = new JObject(); o["type"] = type; return o; }
    static JObject Prop(string type, string desc) { var o = new JObject(); o["type"] = type; o["description"] = desc; return o; }

    static JObject Tool(string name, string desc, JObject props, string[] required)
    {
        var schema = new JObject();
        schema["type"] = "object";
        var p = props != null ? props : new JObject();
        if (p["format"] == null) p["format"] = Prop("string", "Antwortformat: 'xml' (Standard) oder 'json' (Antwort-XML wird zu JSON konvertiert).");
        schema["properties"] = p;
        if (required != null && required.Length > 0) { var ra = new JArray(); foreach (var s in required) ra.Add(s); schema["required"] = ra; }
        var t = new JObject();
        t["name"] = name; t["description"] = desc; t["inputSchema"] = schema;
        return t;
    }

    static JObject ToolsList()
    {
        var tools = new JArray();

        // generisch
        var callProps = new JObject();
        callProps["command"] = Prop("string", "Name des ACMP-Kommandos, z.B. GetClientList, SaveContainers, EnqueueClientCommand.");
        callProps["version"] = Prop("integer", "Kommando-Version (Standard 1). Siehe acmp_list_commands.");
        callProps["inner"] = Prop("string", "Innerer XML-Inhalt des Requests (innerhalb von <TCommandRequest_Vx>...). Leer lassen fuer parameterlose Kommandos.");
        callProps["rawIcql"] = Prop("string", "Optional: vollstaendiges <ICQL>...</ICQL> selbst angeben (ueberschreibt command/version/inner).");
        callProps["intent"] = Prop("string", "Bei Schreib-/Loeschkommandos PFLICHT: kurze Klartext-Beschreibung was/warum getan werden soll. Wird den menschlichen Freigebern gezeigt.");
        tools.Add(Tool("acmp_call",
            "Generisch: ruft JEDES ACMP-Public-API-Kommando auf und gibt die Roh-XML-Antwort zurueck. Voller Zugriff (lesen/schreiben/ausfuehren/loeschen). Baut <ICQL><ACMP><command version><Tcommand Request_Vx>inner...>.",
            callProps, new[] { "command" }));

        tools.Add(Tool("acmp_list_commands",
            "Listet alle verfuegbaren ACMP-Public-API-Kommandos mit ihren Versionen (Katalog).",
            new JObject(), null));

        tools.Add(Tool("acmp_server_info",
            "ACMP-Serverinformationen (Name, Version, ServerId, RoutingKeys).",
            new JObject(), null));

        tools.Add(Tool("acmp_whoami",
            "Zeigt, als welcher echter ACMP-User dieser Aufruf laeuft (Identitaet + GetOwnUserRights). Bestaetigt die Pro-User-Authentifizierung.",
            new JObject(), null));
        tools.Add(Tool("acmp_logout",
            "Trennt die zwischengespeicherte Pro-User-Verbindung des Aufrufers (z.B. nach Token-Wechsel). Naechster Aufruf meldet neu an.",
            new JObject(), null));

        // Lese-Tools (verifiziert)
        var verProp = new JObject(); verProp["version"] = Prop("integer", "Kommando-Version (Standard 1).");
        tools.Add(Tool("acmp_get_clients",
            "Liste der verwalteten Clients (ClientId, ComputerName, Domaene). Kommando GetClientList.",
            (JObject)verProp.DeepClone(), null));
        tools.Add(Tool("acmp_get_client_commands",
            "Liste der verfuegbaren Client Commands / Scripts (Id, Name, FolderPath, State). Diese Ids sind die ClientCommandId fuer Jobs. Kommando GetClientCommandList.",
            new JObject(), null));

        var jobsProps = new JObject(); jobsProps["clientId"] = Prop("string", "ClientId (GUID) des Clients, dessen Jobs abgefragt werden. Pflicht.");
        tools.Add(Tool("acmp_get_jobs",
            "Jobs/Ausfuehrungen eines Clients abfragen. Kommando GetJobs (benoetigt ClientId).",
            jobsProps, new[] { "clientId" }));

        // Schreib-/Ausfuehr-Tools
        var enqProps = new JObject();
        enqProps["clientCommandId"] = Prop("string", "Id (GUID) des auszufuehrenden Client Commands (aus acmp_get_client_commands). Pflicht.");
        enqProps["clientId"] = Prop("string", "Zielclient: ClientId-GUID (aus acmp_get_clients) ODER Computername (Namensaufloesung nur mit DB-Anbindung). Pflicht.");
        enqProps["variables"] = Prop("object", "Optional: Variablen fuer den Client Command als Objekt { Name: Wert, ... } (z.B. { \"SCRIPT\": \"...\" }).");
        enqProps["ignoreMaintenance"] = Prop("boolean", "Wartungsfenster ignorieren und sofort ausfuehren (Standard false). true verhindert 'Warte auf Wartung'.");
        enqProps["intent"] = Prop("string", "Kurze Klartext-Beschreibung was/warum ausgefuehrt werden soll (wird den Freigebern gezeigt).");
        tools.Add(Tool("acmp_enqueue_client_command",
            "(FUEHRT AUS - Freigabepflichtig) Stellt einen Client Command (Job) zur Ausfuehrung auf einem Client in die Warteschlange (EnqueueClientCommand v2). Loest KEINE sofortige Ausfuehrung aus, sondern eine Freigabe-Anfrage (Vier-Augen).",
            enqProps, new[] { "clientCommandId", "clientId" }));

        // Komfort: ad-hoc PowerShell auf einem Client (nur wenn eine Vorlagen-Aktion konfiguriert ist)
        if (PsCommandId != null)
        {
            var psProps = new JObject();
            psProps["script"] = Prop("string", "PowerShell-Code, der auf dem Zielclient als SYSTEM ausgefuehrt wird. Pflicht.");
            psProps["client"] = Prop("string", "Zielclient: ClientId-GUID ODER Computername (Namensaufloesung nur mit DB-Anbindung). Pflicht.");
            psProps["ignoreMaintenance"] = Prop("boolean", "Wartungsfenster ignorieren und sofort ausfuehren (Standard true).");
            psProps["intent"] = Prop("string", "Kurze Klartext-Beschreibung was das Skript tun soll (wird den Freigebern gezeigt).");
            tools.Add(Tool("acmp_run_powershell",
                "(FUEHRT AUS - Freigabepflichtig) PowerShell auf einem Client als SYSTEM. Loest KEINE sofortige Ausfuehrung aus, sondern eine Freigabe-Anfrage (Vier-Augen, 2 Personen). Reale Codeausfuehrung auf dem Zielrechner.",
                psProps, new[] { "script", "client" }));

            // Komfort: Registry-Wert eines Clients auslesen und das Ergebnis im Chat anzeigen (festes, nur-lesendes PowerShell + HTTP-Rueckkanal).
            var rrProps = new JObject();
            rrProps["client"] = Prop("string", "Zielclient: ClientId-GUID ODER Computername (Namensaufloesung nur mit DB-Anbindung). Pflicht. Client muss online sein.");
            rrProps["path"] = Prop("string", "Registry-Pfad, z.B. HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion. Hives: HKLM, HKCU, HKCR, HKU, HKCC (auch Langform HKEY_*). HKCU wird automatisch auf den angemeldeten Benutzer aufgeloest. Pflicht.");
            rrProps["value"] = Prop("string", "Optional: Name des auszulesenden Werts. Leer = ALLE Werte unter dem Schluessel.");
            rrProps["timeoutSec"] = Prop("number", "Optional: Sekunden Wartezeit auf die Client-Antwort (Standard 60, max 300). Danach gibt es ein Token zum spaeteren Nachfragen.");
            tools.Add(Tool("acmp_read_registry",
                "Liest einen Registry-Wert/-Schluessel von einem Client aus und zeigt das Ergebnis direkt im Chat. Schiebt dafuer ein FESTES, nur-lesendes PowerShell (Get-ItemProperty) auf den Client (EnqueueClientCommand, laeuft SOFORT - keine Freigabe) und wartet auf den HTTP-Rueckkanal. Es wird kein beliebiger Code ausgefuehrt; nur der angegebene Schluessel/Wert wird gelesen.",
                rrProps, new[] { "client", "path" }));

            var rrrProps = new JObject();
            rrrProps["token"] = Prop("string", "Das Token aus einem vorigen acmp_read_registry, dessen Ergebnis beim ersten Aufruf noch nicht zurueckkam. Pflicht.");
            tools.Add(Tool("acmp_read_registry_result",
                "Holt das Ergebnis eines zuvor gestarteten acmp_read_registry per Token (falls die Client-Antwort beim ersten Aufruf noch nicht da war).",
                rrrProps, new[] { "token" }));
        }

        var contProps = new JObject();
        contProps["containersXml"] = Prop("string", "XML-Inhalt der <Containers>...</Containers>-Liste (TContainer-Eintraege). Siehe ACMP-Doku zu SaveContainers.");
        contProps["intent"] = Prop("string", "Kurze Klartext-Beschreibung was geaendert werden soll (wird den Freigebern gezeigt).");
        tools.Add(Tool("acmp_save_containers",
            "(SCHREIBT) Legt Container an/aendert sie (SaveContainers). <Containers> mit TAcmpContainer_V1 (Id,Name,Path,ParentId). Path (z.B. \\Name\\) ist PFLICHT, sonst stilles Verwerfen. Einfacher: acmp_create_container.",
            contProps, new[] { "containersXml" }));

        var delContProps = new JObject();
        delContProps["containerIdsXml"] = Prop("string", "XML-Liste der zu loeschenden Container-IDs: <ContainerIds><string>{GUID}</string>...</ContainerIds>.");
        delContProps["intent"] = Prop("string", "Kurze Klartext-Beschreibung was/warum geloescht werden soll (wird den Freigebern gezeigt).");
        tools.Add(Tool("acmp_delete_containers",
            "(LOESCHT - Freigabepflichtig) Loescht Container (DeleteContainers). Loest KEINE sofortige Loeschung aus, sondern eine Freigabe-Anfrage (Vier-Augen, 2 Personen).",
            delContProps, null));
        var ccProps = new JObject();
        ccProps["name"]     = Prop("string", "Name des neuen Containers. Pflicht.");
        ccProps["parentId"] = Prop("string", "Eltern-Container-GUID. Optional - ohne Angabe wird ein Root-Container angelegt.");
        ccProps["intent"]   = Prop("string", "Begruendung fuer Freigabe-Seite. Empfohlen.");
        tools.Add(Tool("acmp_create_container",
            "(SCHREIBT - Freigabepflichtig) Legt einen statischen Container (Ordner) an. Kommando SaveContainers. Pfad/Ebene werden automatisch gesetzt. Dynamische Query-Container brauchen die Konsole.",
            ccProps, new[] { "name" }));
        var laProps = new JObject();
        laProps["assetId"]             = Prop("string", "Asset-GUID (aus acmp_get_asset_list). Pflicht.");
        laProps["contactId"]           = Prop("string", "Kontakt-GUID, an den ausgeliehen wird (aus acmp_get_contacts). Pflicht.");
        laProps["lentDate"]            = Prop("string", "Ausleihdatum ISO (z.B. 2026-06-19T08:00:00.000Z). Optional, Standard: jetzt.");
        laProps["estimatedReturnDate"] = Prop("string", "Geschaetztes Rueckgabedatum ISO. Optional, Standard: in 14 Tagen.");
        laProps["intent"]              = Prop("string", "Begruendung fuer Freigabe-Seite. Empfohlen.");
        tools.Add(Tool("acmp_lend_asset",
            "(SCHREIBT - Freigabepflichtig) Leiht ein Asset an einen Kontakt aus (LendAssets) - setzt 'Ist ausgeliehen' auf Ja.",
            laProps, new[] { "assetId", "contactId" }));

        var raProps = new JObject();
        raProps["assetId"]    = Prop("string", "Asset-GUID (aus acmp_get_asset_list). Pflicht.");
        raProps["returnDate"] = Prop("string", "Rueckgabedatum ISO. Optional, Standard: jetzt.");
        raProps["intent"]     = Prop("string", "Begruendung fuer Freigabe-Seite. Empfohlen.");
        tools.Add(Tool("acmp_return_asset",
            "(SCHREIBT - Freigabepflichtig) Gibt ein ausgeliehenes Asset zurueck (ReturnAssets) - setzt 'Ist ausgeliehen' auf Nein.",
            raProps, new[] { "assetId" }));

        // === Stammdaten-Schreibtools (SCHREIBEN - freigabepflichtig, Vier-Augen) ===
        // Gemeinsame optionale Felder: 'intent' (Begruendung fuer Freigabe-Seite). 'id' = Update (sonst Neuanlage).
        var ctcCreate = new JObject();
        ctcCreate["name"]                   = Prop("string",  "Vorname. Pflicht bei Neuanlage.");
        ctcCreate["familyName"]             = Prop("string",  "Nachname.");
        ctcCreate["email"]                  = Prop("string",  "E-Mail-Adresse.");
        ctcCreate["phone"]                  = Prop("string",  "Telefonnummer.");
        ctcCreate["phone2"]                 = Prop("string",  "Zweite Telefonnummer (z.B. Mobil).");
        ctcCreate["fax"]                    = Prop("string",  "Faxnummer.");
        ctcCreate["position"]               = Prop("string",  "Position/Funktion (z.B. Abteilungsleiter).");
        ctcCreate["companyId"]              = Prop("string",  "Firmen-GUID (aus acmp_get_companies).");
        ctcCreate["departmentId"]           = Prop("string",  "Abteilungs-GUID (aus acmp_get_departments).");
        ctcCreate["locationId"]             = Prop("string",  "Standort-GUID (aus acmp_get_locations).");
        ctcCreate["costCenterId"]           = Prop("string",  "Kostenstellen-GUID (aus acmp_get_cost_centers).");
        ctcCreate["address"]                = Prop("string",  "Strasse/Adresse.");
        ctcCreate["zipcode"]                = Prop("string",  "Postleitzahl.");
        ctcCreate["city"]                   = Prop("string",  "Ort.");
        ctcCreate["country"]                = Prop("string",  "Land.");
        ctcCreate["preferredContactMethod"] = Prop("string",  "Bevorzugter Kontaktweg: pcPhone, pcEmail, pcFax. Standard: pcEmail wenn E-Mail gesetzt, sonst pcPhone.");
        ctcCreate["comment"]                = Prop("string",  "Kommentar/Notiz (HTML erlaubt).");
        ctcCreate["intent"]                 = Prop("string",  "Begruendung fuer Freigabe-Seite. Empfohlen.");
        tools.Add(Tool("acmp_create_contact",
            "(SCHREIBT - Freigabepflichtig) Legt einen neuen Kontakt/eine Person an (SaveContacts). 'name' (Vorname) ist Pflicht.",
            (JObject)ctcCreate.DeepClone(), new[] { "name" }));

        var ctcUpdate = (JObject)ctcCreate.DeepClone();
        ctcUpdate["id"] = Prop("string", "Kontakt-GUID (aus acmp_get_contacts). Pflicht. Nur angegebene Felder werden geaendert, der Rest bleibt erhalten.");
        tools.Add(Tool("acmp_update_contact",
            "(SCHREIBT - Freigabepflichtig) Aendert einen bestehenden Kontakt (SaveContacts, Read-Modify-Write). Nur die angegebenen Felder werden ueberschrieben.",
            ctcUpdate, new[] { "id" }));

        var coCreate = new JObject();
        coCreate["name"]             = Prop("string",  "Firmenname. Pflicht bei Neuanlage.");
        coCreate["street"]           = Prop("string",  "Strasse.");
        coCreate["postalCode"]       = Prop("string",  "Postleitzahl.");
        coCreate["city"]             = Prop("string",  "Ort.");
        coCreate["country"]          = Prop("string",  "Land.");
        coCreate["description"]      = Prop("string",  "Beschreibung/Notiz.");
        coCreate["customerId"]       = Prop("string",  "Kundennummer.");
        coCreate["isManufacturer"]   = Prop("boolean", "Firma ist Hersteller. Standard false.");
        coCreate["isVendor"]         = Prop("boolean", "Firma ist Lieferant. Standard false.");
        coCreate["isServicePartner"] = Prop("boolean", "Firma ist Servicepartner. Standard false.");
        coCreate["intent"]           = Prop("string",  "Begruendung fuer Freigabe-Seite. Empfohlen.");
        tools.Add(Tool("acmp_create_company",
            "(SCHREIBT - Freigabepflichtig) Legt eine neue Firma an (SaveCompanies). 'name' ist Pflicht. Art ueber isManufacturer/isVendor/isServicePartner.",
            (JObject)coCreate.DeepClone(), new[] { "name" }));

        var coUpdate = (JObject)coCreate.DeepClone();
        coUpdate["id"] = Prop("string", "Firmen-GUID (aus acmp_get_companies). Pflicht. Nur angegebene Felder werden geaendert.");
        tools.Add(Tool("acmp_update_company",
            "(SCHREIBT - Freigabepflichtig) Aendert eine bestehende Firma (SaveCompanies, Read-Modify-Write).",
            coUpdate, new[] { "id" }));

        var locCreate = new JObject();
        locCreate["name"]        = Prop("string", "Standortname. Pflicht bei Neuanlage.");
        locCreate["parentId"]    = Prop("string", "Eltern-Standort-GUID fuer Hierarchie (aus acmp_get_locations). Optional.");
        locCreate["description"] = Prop("string", "Beschreibung.");
        locCreate["street"]      = Prop("string", "Strasse.");
        locCreate["city"]        = Prop("string", "Ort.");
        locCreate["postalCode"]  = Prop("string", "Postleitzahl.");
        locCreate["country"]     = Prop("string", "Land.");
        locCreate["intent"]      = Prop("string", "Begruendung fuer Freigabe-Seite. Empfohlen.");
        tools.Add(Tool("acmp_create_location",
            "(SCHREIBT - Freigabepflichtig) Legt einen neuen Standort an (SaveLocations). 'name' ist Pflicht.",
            (JObject)locCreate.DeepClone(), new[] { "name" }));

        var locUpdate = (JObject)locCreate.DeepClone();
        locUpdate["id"] = Prop("string", "Standort-GUID (aus acmp_get_locations). Pflicht. Nur angegebene Felder werden geaendert.");
        tools.Add(Tool("acmp_update_location",
            "(SCHREIBT - Freigabepflichtig) Aendert einen bestehenden Standort (SaveLocations, Read-Modify-Write).",
            locUpdate, new[] { "id" }));

        var depCreate = new JObject();
        depCreate["name"]         = Prop("string", "Abteilungsname. Pflicht bei Neuanlage.");
        depCreate["parentId"]     = Prop("string", "Eltern-Abteilungs-GUID fuer Hierarchie (aus acmp_get_departments). Optional.");
        depCreate["abbreviation"] = Prop("string", "Kuerzel/Abkuerzung.");
        depCreate["description"]  = Prop("string", "Beschreibung.");
        depCreate["intent"]       = Prop("string", "Begruendung fuer Freigabe-Seite. Empfohlen.");
        tools.Add(Tool("acmp_create_department",
            "(SCHREIBT - Freigabepflichtig) Legt eine neue Abteilung an (SaveDepartments). 'name' ist Pflicht.",
            (JObject)depCreate.DeepClone(), new[] { "name" }));

        var depUpdate = (JObject)depCreate.DeepClone();
        depUpdate["id"] = Prop("string", "Abteilungs-GUID (aus acmp_get_departments). Pflicht. Nur angegebene Felder werden geaendert.");
        tools.Add(Tool("acmp_update_department",
            "(SCHREIBT - Freigabepflichtig) Aendert eine bestehende Abteilung (SaveDepartments, Read-Modify-Write).",
            depUpdate, new[] { "id" }));

        tools.Add(Tool("acmp_get_asset_types",
            "Asset-Typen/-Kategorien (Id, Name, ParentId/Hierarchie). Kommando GetAssetTypeList. Nutze die Id als assetType bei acmp_create_asset. Hinweis: Neue Typen koennen NUR in der ACMP-Konsole angelegt werden.",
            new JObject(), null));

        var asCreate = new JObject();
        asCreate["name"]            = Prop("string", "Asset-Name/Bezeichnung. Pflicht.");
        asCreate["assetType"]       = Prop("string", "Asset-Typ: Name (z.B. 'Reisemonitore') ODER Typ-GUID (aus acmp_get_asset_types). Pflicht - jedes Asset braucht einen bestehenden Typ.");
        asCreate["inventoryNumber"] = Prop("string", "Inventarnummer.");
        asCreate["model"]           = Prop("string", "Modell (z.B. 'WorkForce ES-500WII').");
        asCreate["serialNumber"]    = Prop("string", "Seriennummer.");
        asCreate["description"]     = Prop("string", "Beschreibung/Notiz.");
        asCreate["manufacturerId"]  = Prop("string", "Hersteller: Firmenname oder GUID (aus acmp_get_companies). Optional.");
        asCreate["vendorId"]        = Prop("string", "Lieferant: Firmenname oder GUID. Optional.");
        asCreate["departmentId"]    = Prop("string", "Abteilung: Name (z.B. 'Buchhaltung') oder GUID. Optional.");
        asCreate["locationId"]      = Prop("string", "Standort: Name (z.B. 'Hauptsitz') oder GUID. Optional.");
        asCreate["costCenterId"]    = Prop("string", "Kostenstelle: Name oder GUID. Optional.");
        asCreate["assetStateId"]    = Prop("string", "Asset-Status-GUID (z.B. 'In Benutzung'). Optional - wird sonst automatisch aus einem bestehenden Asset uebernommen.");
        asCreate["intent"]          = Prop("string", "Begruendung fuer Freigabe-Seite. Empfohlen.");
        tools.Add(Tool("acmp_create_asset",
            "(SCHREIBT - Freigabepflichtig) Legt ein Asset an (SaveAssets). 'name' + 'assetType' (bestehender Typ) Pflicht. Neue Typen/Kategorien gehen NUR in der ACMP-Konsole.",
            (JObject)asCreate.DeepClone(), new[] { "name", "assetType" }));

        var asUpdate = new JObject();
        asUpdate["id"]              = Prop("string", "Asset-GUID (aus acmp_get_asset_list). Pflicht. Nur angegebene Felder werden geaendert.");
        asUpdate["name"]            = Prop("string", "Neuer Name. Optional.");
        asUpdate["assetType"]       = Prop("string", "Neuer Asset-Typ (Name oder GUID). Optional.");
        asUpdate["inventoryNumber"] = Prop("string", "Inventarnummer. Optional.");
        asUpdate["model"]           = Prop("string", "Modell. Optional.");
        asUpdate["serialNumber"]    = Prop("string", "Seriennummer. Optional.");
        asUpdate["description"]     = Prop("string", "Beschreibung. Optional.");
        asUpdate["manufacturerId"]  = Prop("string", "Hersteller: Firmenname oder GUID. Optional.");
        asUpdate["vendorId"]        = Prop("string", "Lieferant: Firmenname oder GUID. Optional.");
        asUpdate["departmentId"]    = Prop("string", "Abteilung: Name oder GUID. Optional.");
        asUpdate["locationId"]      = Prop("string", "Standort: Name oder GUID. Optional.");
        asUpdate["costCenterId"]    = Prop("string", "Kostenstelle: Name oder GUID. Optional.");
        asUpdate["intent"]          = Prop("string", "Begruendung fuer Freigabe-Seite. Empfohlen.");
        tools.Add(Tool("acmp_update_asset",
            "(SCHREIBT - Freigabepflichtig) Aendert ein bestehendes Asset (SaveAssets, Read-Modify-Write - andere Felder bleiben erhalten).",
            asUpdate, new[] { "id" }));

        var statProps = new JObject();
        statProps["id"] = Prop("string", "Anfrage-Id, die ein freigabepflichtiges Tool zurueckgegeben hat. Pflicht.");
        tools.Add(Tool("acmp_approval_status",
            "Status einer Freigabe-Anfrage abfragen (Zustimmungen, ob ausgefuehrt, Ergebnis). Read-only.",
            statProps, new[] { "id" }));

        tools.Add(Tool("acmp_get_asset_list",
            "Asset-Liste abfragen (Inventar). Kommando GetAssetList (v1-3).",
            (JObject)verProp.DeepClone(), null));

                tools.Add(Tool("acmp_get_tickets",
            "Helpdesk-Ticketliste abfragen. Kommando GetTicketList.",
            new JObject(), null));

        tools.Add(Tool("acmp_helpdesk_settings",
            "Helpdesk-Konfiguration lesen: Kategorien, Status, Prioritaeten, TextModule. Kommando GetHelpDeskSettings v2.",
            new JObject(), null));

        tools.Add(Tool("acmp_get_assignees",
            "Alle Helpdesk-Bearbeiter mit IDs abfragen. Kommando GetAssigneeList.",
            new JObject(), null));

        var gtProps = new JObject();
        gtProps["id"] = Prop("string", "Ticket-GUID oder IntTicketId (z.B. 12). Pflicht.");
        tools.Add(Tool("acmp_get_ticket",
            "Einzelnes Ticket mit Details. GUID oder IntTicketId. Kommando GetTicket v5.",
            gtProps, new[] { "id" }));

        var ctProps = new JObject();
        ctProps["title"]          = Prop("string",  "Titel des Tickets. Pflicht.");
        ctProps["description"]    = Prop("string",  "Problembeschreibung (HTML). Optional.");
        ctProps["requesterEmail"] = Prop("string",  "E-Mail des Melders. Optional.");
        ctProps["categoryId"]     = Prop("string",  "Kategorie-GUID aus acmp_helpdesk_settings. Optional.");
        ctProps["priority"]       = Prop("integer", "0=Trivial 1=Normal 2=Wichtig 3=Kritisch. Optional.");
        ctProps["assigneeId"]     = Prop("string",  "Bearbeiter-GUID aus acmp_get_assignees. Optional.");
        ctProps["intent"]         = Prop("string",  "Begruendung fuer Freigabe-Seite. Empfohlen.");
        tools.Add(Tool("acmp_create_ticket",
            "(SCHREIBT) Neues Helpdesk-Ticket. Titel pflicht, Rest optional. Status=Erzeugt. SaveTicket v4.",
            ctProps, new[] { "title" }));

        var utProps = new JObject();
        utProps["id"]         = Prop("string",  "Ticket-GUID oder IntTicketId. Pflicht.");
        utProps["assigneeId"] = Prop("string",  "Neuer Bearbeiter-GUID. Optional.");
        utProps["priority"]   = Prop("integer", "Neue Prioritaet 0-3. Optional.");
        utProps["intent"]     = Prop("string",  "Begruendung fuer Freigabe-Seite. Empfohlen.");
        tools.Add(Tool("acmp_update_ticket",
            "(SCHREIBT) Ticket: Bearbeiter/Prioritaet aendern. Min. 1 Feld. Status aendern via acmp_close_ticket/acmp_reopen_ticket. SaveTicket v4.",
            utProps, new[] { "id" }));

        var osProps = new JObject();

        osProps["clientId"] = Prop("string", "ClientId (GUID) des Clients. Pflicht.");
        osProps["version"] = Prop("integer", "Version 1 oder 2 (Standard 2).");
        tools.Add(Tool("acmp_get_os_info",
            "Betriebssystem-Inventar eines Clients - enthaelt u.a. LastLoggedOnUser (letzter angemeldeter Benutzer = 'wer ist/war angemeldet'), RegisteredUser, OS-Version, Sprache, Zeitzone. Kommando GetOperatingSystem (braucht ClientId).",
            osProps, new[] { "clientId" }));

        var swProps = new JObject();
        swProps["clientId"] = Prop("string", "ClientId (GUID) des Clients. Pflicht.");
        tools.Add(Tool("acmp_get_software",
            "Installierte Software eines Clients (Name, Version, Publisher, Major/Minor/PatchLevel, UninstallPath). Kommando GetSoftware (braucht ClientId).",
            swProps, new[] { "clientId" }));

        var descProps = new JObject();
        descProps["command"] = Prop("string", "Kommando-Name, dessen Struktur erklaert werden soll.");
        tools.Add(Tool("acmp_describe",
            "Erklaert ein Kommando: verfuegbare Versionen + ICQL-Request-Vorlage. Bei Get*-Kommandos wird die Antwortstruktur live ermittelt (sicher).",
            descProps, new[] { "command" }));

        // === Stammdaten- & Helpdesk-Lesetools (Read, laufen sofort) ===
        tools.Add(Tool("acmp_get_contacts",
            "Kontaktliste (Helpdesk-Melder/Personen): Id, Name, FullName, E-Mail. Kommando GetContactList.",
            new JObject(), null));
        tools.Add(Tool("acmp_get_companies",
            "Firmenliste (Hersteller/Lieferanten/Servicepartner) mit Id, Name, CompanyKinds. Kommando GetCompanyList.",
            new JObject(), null));
        tools.Add(Tool("acmp_get_locations",
            "Standortliste (Hierarchie, AssetCount, FullPath). Kommando GetLocationList.",
            new JObject(), null));
        tools.Add(Tool("acmp_get_departments",
            "Abteilungsliste (Hierarchie, AssetCount). Kommando GetDepartmentList.",
            new JObject(), null));
        tools.Add(Tool("acmp_get_cost_centers",
            "Kostenstellen-Liste. Kommando GetCostCenterList.",
            new JObject(), null));
        tools.Add(Tool("acmp_get_contracts",
            "Vertragsliste. Kommando GetContractList.",
            new JObject(), null));
        tools.Add(Tool("acmp_get_custom_fields",
            "Custom Fields (benutzerdefinierte Felder) mit Id, Caption DE/EN, Pfad. Kommando GetCustomFieldList.",
            new JObject(), null));
        tools.Add(Tool("acmp_get_queries",
            "Gespeicherte ACMP-Queries (Id, Name, Pfad). Ids als queryId fuer acmp_tickets_by_query nutzbar. Kommando GetQueryList.",
            new JObject(), null));
        tools.Add(Tool("acmp_get_ticket_templates",
            "Helpdesk-Ticketvorlagen (Id, Name). Kommando GetTicketTemplateList.",
            new JObject(), null));
        tools.Add(Tool("acmp_get_ticket_categories",
            "Helpdesk-Ticketkategorien (Id, Caption DE/EN, Hierarchie). Kommando GetTicketCategoryList.",
            new JObject(), null));
        tools.Add(Tool("acmp_get_ticket_resolutions",
            "Helpdesk-Loesungen/Resolutions (Id, Caption) - resolutionId fuer acmp_close_ticket. Kommando GetTicketResolutionList.",
            new JObject(), null));

        var tidProps = new JObject(); tidProps["id"] = Prop("string", "Ticket-GUID oder IntTicketId (z.B. 12). Pflicht.");
        tools.Add(Tool("acmp_get_ticket_comments",
            "Kommentare/Verlauf eines Tickets. Kommando GetTicketComments (TicketId).",
            (JObject)tidProps.DeepClone(), new[] { "id" }));
        tools.Add(Tool("acmp_get_ticket_worktime",
            "Erfasste Arbeitszeiten eines Tickets. Kommando GetTicketWorkingTimeList (TicketId).",
            (JObject)tidProps.DeepClone(), new[] { "id" }));
        tools.Add(Tool("acmp_get_ticket_costs",
            "Erfasste Kosten eines Tickets. Kommando GetTicketCostList (TicketId).",
            (JObject)tidProps.DeepClone(), new[] { "id" }));
        tools.Add(Tool("acmp_get_linked_tickets",
            "Mit einem Ticket verknuepfte Tickets. Kommando GetLinkedTicketsOfTicket (TicketId).",
            (JObject)tidProps.DeepClone(), new[] { "id" }));

        var byqProps = new JObject();
        byqProps["queryId"] = Prop("string", "Query-GUID aus acmp_get_queries. Pflicht.");
        byqProps["version"] = Prop("integer", "Version 1 oder 2 (Standard 2).");
        tools.Add(Tool("acmp_tickets_by_query",
            "Ticketliste anhand einer gespeicherten ACMP-Query (gefiltert). Kommando GetTicketListByQuery.",
            byqProps, new[] { "queryId" }));

        var hwProps = new JObject();
        hwProps["clientId"] = Prop("string", "ClientId (GUID) des Clients. Pflicht.");
        hwProps["component"] = Prop("string", "Komponente: cpu, memory, memoryslots, harddrives, logicaldrives, netcards, displays, monitors, bios, mainboard, printers, services, battery, devices, shares, cddvd. Pflicht.");
        tools.Add(Tool("acmp_get_hardware",
            "Hardware-/System-Inventar einer Komponente eines Clients (GetCPU/GetMemory/GetHardDrives/GetServices/...). Read-only (braucht ClientId + component).",
            hwProps, new[] { "clientId", "component" }));

        // === Helpdesk-Aktionen (SCHREIBEN - freigabepflichtig, Vier-Augen) ===
        var closeProps = new JObject();
        closeProps["id"]           = Prop("string", "Ticket-GUID oder IntTicketId. Pflicht.");
        closeProps["comment"]      = Prop("string", "Abschluss-Kommentar (PFLICHT - ACMP verlangt beim Schliessen einen Kommentar).");
        closeProps["resolutionId"] = Prop("string", "Loesungs-GUID aus acmp_get_ticket_resolutions. Optional (Standard: aktuelle Loesung des Tickets).");
        closeProps["categoryId"]   = Prop("string", "Kategorie-GUID. Optional - nur noetig, wenn das Ticket noch keine Kategorie hat.");
        closeProps["assigneeId"]   = Prop("string", "Bearbeiter-GUID. Optional - nur noetig, wenn das Ticket noch keinen Bearbeiter hat.");
        closeProps["intent"]       = Prop("string", "Begruendung fuer Freigabe-Seite. Empfohlen.");
        tools.Add(Tool("acmp_close_ticket",
            "(SCHREIBT - Freigabepflichtig) Schliesst ein Ticket. Kommando CloseTicket.",
            closeProps, new[] { "id", "comment" }));

        var reopenProps = new JObject();
        reopenProps["id"]     = Prop("string", "Ticket-GUID oder IntTicketId. Pflicht.");
        reopenProps["intent"] = Prop("string", "Begruendung fuer Freigabe-Seite. Empfohlen.");
        tools.Add(Tool("acmp_reopen_ticket",
            "(SCHREIBT - Freigabepflichtig) Oeffnet ein geschlossenes Ticket wieder. Kommando ReopenTicket.",
            reopenProps, new[] { "id" }));

        var comProps = new JObject();
        comProps["id"]       = Prop("string",  "Ticket-GUID oder IntTicketId. Pflicht.");
        comProps["comment"]  = Prop("string",  "Kommentartext (HTML erlaubt). Pflicht.");
        comProps["internal"] = Prop("boolean", "true = interner Kommentar (nicht fuer den Melder sichtbar). Standard false.");
        comProps["intent"]   = Prop("string",  "Begruendung fuer Freigabe-Seite. Empfohlen.");
        tools.Add(Tool("acmp_add_ticket_comment",
            "(SCHREIBT - Freigabepflichtig) Fuegt einem Ticket einen Kommentar hinzu. Kommando SaveTicketComment.",
            comProps, new[] { "id", "comment" }));
        // --- DB-gestuetzte Report-Tools (read-only, nativ aus ACMP) ---
        var outdatedProps = new JObject();
        outdatedProps["client"] = Prop("string", "Optional: Computername (z.B. PC-0815) ODER ClientId-GUID. Ohne Angabe: flottenweite Zusammenfassung (Software -> Anzahl veralteter Clients).");
        tools.Add(Tool("acmp_outdated_software",
            "Veraltete Managed-Software aus ACMPs eigenen Daten (installiert vs. verfuegbar). Mit 'client': Liste je Software; ohne 'client': flottenweite Zusammenfassung. Read-only DB.",
            outdatedProps, null));

        var missProps = new JObject();
        missProps["client"] = Prop("string", "Computername (z.B. PC-0815) ODER ClientId-GUID. Pflicht.");
        tools.Add(Tool("acmp_missing_updates",
            "Fehlende Windows-Updates eines Clients (Titel, Severity 0-4, KB). Read-only DB.",
            missProps, new[] { "client" }));

        var swSearchProps = new JObject();
        swSearchProps["name"] = Prop("string", "Software-Name oder Teilstring (z.B. 'Chrome', 'PuTTY'). Pflicht.");
        tools.Add(Tool("acmp_software_search",
            "Findet alle Clients, auf denen eine Software installiert ist, inkl. Version (Inventar). Read-only DB.",
            swSearchProps, new[] { "name" }));

        // --- Datengetriebene DB-Report-Tools aus db-tools.json ---
        foreach (JObject d in DbToolDefs)
        {
            var props = new JObject(); var req = new List<string>();
            JArray ad = d["args"] as JArray;
            if (ad != null)
                foreach (JObject arg in ad)
                {
                    string an = (string)arg["name"];
                    string kind = (string)arg["kind"];
                    props[an] = Prop(kind == "int" ? "integer" : "string", (string)arg["description"]);
                    if (arg["required"] != null && (bool)arg["required"]) req.Add(an);
                }
            tools.Add(Tool((string)d["name"], (string)d["description"] + " (read-only DB)", props, req.ToArray()));
        }

        // --- Datengetriebene API-Tools aus api-tools.json ---
        foreach (JObject d in ApiToolDefs)
        {
            var props = new JObject(); var req = new List<string>();
            JArray ad = d["args"] as JArray;
            if (ad != null)
                foreach (JObject arg in ad)
                {
                    string an = (string)arg["name"];
                    string kind = (string)arg["kind"];
                    props[an] = Prop(kind == "int" || kind == "integer" ? "integer" : "string", (string)arg["description"]);
                    if (arg["required"] != null && (bool)arg["required"]) req.Add(an);
                }
            string tierStr = d["tier"] != null ? (string)d["tier"] : "Read";
            if (tierStr != "Read") props["intent"] = Prop("string", "Bei Schreib-/Loeschkommandos: kurze Klartext-Begruendung fuer die Freigabe.");
            string desc = (string)d["description"];
            tools.Add(Tool((string)d["name"], desc + " [API-Tool: " + (string)d["command"] + " v" + (d["version"] != null ? d["version"].ToString() : "1") + ", " + tierStr + "]", props, req.ToArray()));
        }

        var r = new JObject(); r["tools"] = tools; return r;
    }

    // ---------- Pro-User-Identitaeten / Verbindungspool ----------
    static string IdentId(string domain, string user)
    {
        return (string.IsNullOrEmpty(domain) ? user : domain + "\\" + user).ToLowerInvariant();
    }

    static void LoadAcmpIdentities(string cfgDir, JObject mcp, string defaultRequester)
    {
        AcmpIdentities = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
        string secPath = Path.Combine(cfgDir, "secrets.local.json");
        if (File.Exists(secPath))
        {
            try
            {
                JObject sroot = JObject.Parse(File.ReadAllText(secPath));
                JObject su = sroot["acmpUsers"] as JObject;
                if (su != null) foreach (JProperty p in su.Properties()) AcmpIdentities[p.Name] = (JObject)p.Value;
            }
            catch (Exception ex) { Log("secrets.local.json Fehler: " + ex.Message); }
        }
        // requester -> identityId mappen (mcp.users[].acmpUser/acmpDomain, plus optionaler Default fuer den Legacy-Key)
        if (mcp != null)
        {
            if (mcp["defaultAcmpUser"] != null && !string.IsNullOrEmpty(defaultRequester) && ((string)mcp["defaultAcmpUser"]).Length > 0)
                RequesterToIdentity[defaultRequester] = IdentId(mcp["defaultAcmpDomain"] != null ? (string)mcp["defaultAcmpDomain"] : null, (string)mcp["defaultAcmpUser"]);
            JArray us = mcp["users"] as JArray;
            if (us != null)
                foreach (JObject u in us)
                {
                    string nm = (string)u["name"];
                    string au = u["acmpUser"] != null ? (string)u["acmpUser"] : null;
                    if (!string.IsNullOrEmpty(nm) && !string.IsNullOrEmpty(au))
                        RequesterToIdentity[nm] = IdentId(u["acmpDomain"] != null ? (string)u["acmpDomain"] : null, au);
                }
        }
        Log("ACMP-Identitaeten: " + AcmpIdentities.Count + " Secret(s), " + RequesterToIdentity.Count + " Requester-Mapping(s).");
    }

    static string ResolveIdentityId(string requester)
    {
        string id;
        if (!string.IsNullOrEmpty(requester) && RequesterToIdentity.TryGetValue(requester, out id)) return id;
        return null;
    }

    static AcmpConn ResolveConn(string requester)
    {
        string id = ResolveIdentityId(requester);
        if (id == null) return _defaultConn;
        return GetOrCreatePooled(id);
    }

    static AcmpConn GetOrCreatePooled(string id)
    {
        lock (PoolLock)
        {
            AcmpConn c;
            if (ConnPool.TryGetValue(id, out c) && c != null) return c;
            JObject e;
            if (AcmpIdentities == null || !AcmpIdentities.TryGetValue(id, out e)) return _defaultConn;
            try
            {
                string at = (string)e["authType"];
                string un = (string)e["username"];
                string dom = e["domain"] != null ? (string)e["domain"] : null;
                if (at == "acmptoken" || at == "acmpdomaintoken")
                {
                    c = new AcmpConn(SicsDir, ConnHost, un, null, at, dom, AcmpSrvName, AcmpSrvId);
                    c.SetToken(DpapiDecrypt((string)e["tokenDpapi"]));
                }
                else
                {
                    c = new AcmpConn(SicsDir, ConnHost, un, DpapiDecrypt((string)e["passwordDpapi"]), at, dom, AcmpSrvName, AcmpSrvId);
                }
                c.Connect();
                ConnPool[id] = c;
                Log("Pro-User-Verbindung aufgebaut: " + id + " (authType=" + at + ")");
                return c;
            }
            catch (Exception ex)
            {
                string inner = ex.InnerException != null ? " | Ursache: " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message : "";
                Log("Pro-User-Verbindung FEHLGESCHLAGEN fuer " + id + ": " + ex.Message + inner + " -> Fallback Operator");
                return _defaultConn;
            }
        }
    }

    static string DpapiDecrypt(string b64)
    {
        byte[] enc = Convert.FromBase64String(b64);
        byte[] plain = System.Security.Cryptography.ProtectedData.Unprotect(enc, null, System.Security.Cryptography.DataProtectionScope.LocalMachine);
        return Encoding.UTF8.GetString(plain);
    }

    static JObject ToolsCall(JObject prm, string requester)
    {
        if (prm == null) throw new Exception("params fehlt");
        string name = (string)prm["name"];
        JObject a = prm["arguments"] as JObject; if (a == null) a = new JObject();

        if (name == "acmp_approval_status")
        {
            string id = (string)a["id"]; if (string.IsNullOrEmpty(id)) throw new Exception("id fehlt");
            if (APPROVAL == null || !APPROVAL.Enabled) throw new Exception("Vier-Augen-Freigabe ist nicht aktiv.");
            return TextOut(APPROVAL.StatusText(id));
        }
        if (name == "acmp_call")
        {
            string raw = (string)a["rawIcql"];
            if (!string.IsNullOrEmpty(raw)) return GatePublish("acmp_call", raw, a, requester);
            string cmd = (string)a["command"]; if (string.IsNullOrEmpty(cmd)) throw new Exception("command fehlt");
            int ver = a["version"] != null ? (int)a["version"] : 1;
            string inner = a["inner"] != null ? (string)a["inner"] : "";
            RiskTier tier = APPROVAL != null ? APPROVAL.Classify(cmd) : RiskTier.Read;
            return GateCall("acmp_call", cmd, ver, inner, null, tier, a, requester);
        }
        if (name == "acmp_list_commands") return TextOut(ParseCatalog(CONN.Call("GetCommandVersions", 1, "<Commands/>")));
        if (name == "acmp_server_info") return Respond(CONN.Call("GetACMPServerInfo", 1, ""), a);
        if (name == "acmp_whoami")
        {
            string wid = ResolveIdentityId(requester);
            var sbw = new StringBuilder();
            sbw.Append("MCP-Antragsteller: ").Append(requester != null ? requester : "(anonym)").Append("\n");
            sbw.Append("ACMP-Identitaet: ").Append(wid != null ? wid : "(keine gemappt -> Operator/SICS-Fallback)").Append("\n");
            sbw.Append("Verbindung: ").Append(_currentConn != null && _currentConn != _defaultConn ? "Pro-User" : "Operator (Fallback)").Append("\n");
            try
            {
                string r = CONN.Call("GetOwnUserRights", 1, "<Version>1</Version>");
                var mc = System.Text.RegularExpressions.Regex.Match(r, "<ResultCode>(-?\\d+)</ResultCode>");
                int cnt = System.Text.RegularExpressions.Regex.Matches(r, "<(?:string|AcmpGuid)>").Count;
                sbw.Append("GetOwnUserRights: ResultCode=").Append(mc.Success ? mc.Groups[1].Value : "?").Append(", Rechte-Eintraege=").Append(cnt);
                sbw.Append(mc.Success && mc.Groups[1].Value == "0" ? "  => als echter ACMP-User authentifiziert" : "  => kein User-Login (Operator/Admin-Kontext)");
            }
            catch (Exception ex) { sbw.Append("GetOwnUserRights Fehler: ").Append(ex.Message); }
            return TextOut(sbw.ToString());
        }
        if (name == "acmp_logout")
        {
            string lid = ResolveIdentityId(requester);
            string msg;
            if (lid == null) msg = "Keine Pro-User-Identitaet gemappt - nichts zu trennen.";
            else lock (PoolLock)
            {
                AcmpConn c;
                if (ConnPool.TryGetValue(lid, out c) && c != null) { try { c.Close(); } catch { } ConnPool.Remove(lid); msg = "Verbindung fuer " + lid + " getrennt (naechster Aufruf meldet neu an)."; }
                else msg = "Keine offene Verbindung fuer " + lid + ".";
            }
            return TextOut(msg);
        }
        if (name == "acmp_get_clients") { int ver = a["version"] != null ? (int)a["version"] : 1; return Respond(CONN.Call("GetClientList", ver, ""), a); }
        if (name == "acmp_get_client_commands") return Respond(CONN.Call("GetClientCommandList", 1, ""), a);
        if (name == "acmp_get_asset_list") { int ver = a["version"] != null ? (int)a["version"] : 1; return Respond(CONN.Call("GetAssetList", ver, ""), a); }
                if (name == "acmp_get_tickets") return Respond(CONN.Call("GetTicketList", 1, ""), a);
        if (name == "acmp_helpdesk_settings") return Respond(CONN.Call("GetHelpDeskSettings", 2, ""), a);
        if (name == "acmp_get_assignees") return Respond(CONN.Call("GetAssigneeList", 1, ""), a);
        if (name == "acmp_get_ticket")
        {
            string tid = (string)a["id"]; if (string.IsNullOrEmpty(tid)) throw new Exception("id fehlt");
            string ticketGuid; Guid tguid;
            string tidClean = tid.Replace("{","").Replace("}","").Trim();
            if (Guid.TryParse(tidClean, out tguid))
            {
                ticketGuid = "{" + tguid.ToString().ToUpperInvariant() + "}";
            }
            else
            {
                string tlist = CONN.Call("GetTicketList", 1, "");
                ticketGuid = null;
                var tidMs = System.Text.RegularExpressions.Regex.Matches(tlist,
                    @"<TTicketInfo_V\d[^>]*>.*?<Id>([^<]+)</Id>.*?<IntTicketId>(\d+)</IntTicketId>",
                    System.Text.RegularExpressions.RegexOptions.Singleline);
                foreach (System.Text.RegularExpressions.Match im in tidMs)
                    if (im.Groups[2].Value == tid) { ticketGuid = im.Groups[1].Value; break; }
                if (ticketGuid == null) throw new Exception("Ticket IntTicketId " + tid + " nicht gefunden.");
            }
            return Respond(CONN.Call("GetTicket", 5, "<Id>" + ticketGuid + "</Id>"), a);
        }
        if (name == "acmp_create_ticket")
        {
            string ctTitle = (string)a["title"]; if (string.IsNullOrEmpty(ctTitle)) throw new Exception("title fehlt");
            string ctDesc  = (string)a["description"];
            string ctEmail = (string)a["requesterEmail"];
            string ctCat   = (string)a["categoryId"];
            int    ctPrio  = a["priority"] != null ? (int)a["priority"] : 1;
            string ctAsn   = (string)a["assigneeId"];
            string ctNewId = "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}";
            string createdStateId = TicketCreatedStateId;
            var ctSb = new StringBuilder();
            ctSb.Append("<Ticket xsi:type=\"TTicket_V4\">");
            ctSb.Append("<ID>").Append(ctNewId).Append("</ID>");
            ctSb.Append("<Title>").Append(Xml(ctTitle)).Append("</Title>");
            if (!string.IsNullOrEmpty(ctDesc))  ctSb.Append("<HtmlDescription>").Append(Xml(ctDesc)).Append("</HtmlDescription>");
            if (!string.IsNullOrEmpty(ctCat))   ctSb.Append("<CategoryId>").Append(GuidBraces(ctCat)).Append("</CategoryId>");
            ctSb.Append("<PriorityId>").Append(ctPrio).Append("</PriorityId>");
            ctSb.Append("<StateId>").Append(createdStateId).Append("</StateId>");
            ctSb.Append("<ResolutionId>NO_RESOLUTION</ResolutionId>");
            if (!string.IsNullOrEmpty(ctAsn))   ctSb.Append("<Assignee xsi:type=\"TAssigneeInfo_V1\"><Id>").Append(GuidBraces(ctAsn)).Append("</Id></Assignee>");
            if (!string.IsNullOrEmpty(ctEmail)) ctSb.Append("<Requester xsi:type=\"TContactInfo_V1\"><Email>").Append(Xml(ctEmail)).Append("</Email></Requester>");
            ctSb.Append("</Ticket>");
            return GateCall("acmp_create_ticket", "SaveTicket", 4, ctSb.ToString(), null, RiskTier.Write, a, requester);
        }
        if (name == "acmp_update_ticket")
        {
            string utId  = (string)a["id"]; if (string.IsNullOrEmpty(utId)) throw new Exception("id fehlt");
            string utAsn = (string)a["assigneeId"];
            int?   utPr  = a["priority"] != null ? (int?)((int)a["priority"]) : null;
            if (string.IsNullOrEmpty(utAsn) && !utPr.HasValue)
                throw new Exception("Mindestens assigneeId oder priority angeben. Status aendern via acmp_close_ticket/acmp_reopen_ticket.");
            Guid utGuid; string utGet;
            string utClean = utId.Replace("{","").Replace("}","").Trim();
            if (Guid.TryParse(utClean, out utGuid))
            {
                utGet = "<Id>{" + utGuid.ToString().ToUpperInvariant() + "}</Id>";
            }
            else
            {
                string utList = CONN.Call("GetTicketList", 1, "");
                string utg = null;
                var utMs = System.Text.RegularExpressions.Regex.Matches(utList,
                    @"<TTicketInfo_V\d[^>]*>.*?<Id>([^<]+)</Id>.*?<IntTicketId>(\d+)</IntTicketId>",
                    System.Text.RegularExpressions.RegexOptions.Singleline);
                foreach (System.Text.RegularExpressions.Match im in utMs)
                    if (im.Groups[2].Value == utId) { utg = im.Groups[1].Value; break; }
                if (utg == null) throw new Exception("Ticket IntTicketId " + utId + " nicht gefunden.");
                utGet = "<Id>" + utg + "</Id>";
            }
            string utEx = CONN.Call("GetTicket", 5, utGet);
            var utM2 = System.Text.RegularExpressions.Regex.Match(utEx,
                @"<Ticket[^>]*>.*?</Ticket>", System.Text.RegularExpressions.RegexOptions.Singleline);
            if (!utM2.Success) throw new Exception("Ticket-Element nicht in API-Antwort.");
            string utXml = utM2.Value; utXml = System.Text.RegularExpressions.Regex.Replace(utXml, @"<(CategoryId|ImpactId|TemplateId)(\s*/>|></(CategoryId|ImpactId|TemplateId)>)", "");
            if (utPr.HasValue)
                utXml = System.Text.RegularExpressions.Regex.Replace(utXml,
                    @"<PriorityId>[^<]*</PriorityId>", "<PriorityId>" + utPr.Value + "</PriorityId>");
            if (!string.IsNullOrEmpty(utAsn))
            {
                string utAX = "<Assignee xsi:type=\"TAssigneeInfo_V1\"><Id>" + GuidBraces(utAsn) + "</Id></Assignee>";
                if (System.Text.RegularExpressions.Regex.IsMatch(utXml,
                    @"<Assignee[^>]*>.*?</Assignee>", System.Text.RegularExpressions.RegexOptions.Singleline))
                    utXml = System.Text.RegularExpressions.Regex.Replace(utXml,
                        @"<Assignee[^>]*>.*?</Assignee>", utAX, System.Text.RegularExpressions.RegexOptions.Singleline);
                else
                    utXml = utXml.Replace("</Ticket>", utAX + "</Ticket>");
            }
            return GateCall("acmp_update_ticket", "SaveTicket", 4, utXml, null, RiskTier.Write, a, requester);
        }
        if (name == "acmp_get_os_info")
        {
            string cid = (string)a["clientId"]; if (string.IsNullOrEmpty(cid)) throw new Exception("clientId fehlt");
            int ver = a["version"] != null ? (int)a["version"] : 2;
            return Respond(CONN.Call("GetOperatingSystem", ver, "<ClientId>" + Xml(cid) + "</ClientId>"), a);
        }
        if (name == "acmp_get_software")
        {
            string cid = (string)a["clientId"]; if (string.IsNullOrEmpty(cid)) throw new Exception("clientId fehlt");
            return Respond(CONN.Call("GetSoftware", 1, "<ClientId>" + Xml(cid) + "</ClientId>"), a);
        }
        if (name == "acmp_get_jobs")
        {
            string cid = (string)a["clientId"]; if (string.IsNullOrEmpty(cid)) throw new Exception("clientId fehlt");
            return Respond(CONN.Call("GetJobs", 1, "<ClientId>" + Xml(cid) + "</ClientId>"), a);
        }
        if (name == "acmp_enqueue_client_command")
        {
            string ccid = (string)a["clientCommandId"]; string cid = (string)a["clientId"];
            if (string.IsNullOrEmpty(ccid)) throw new Exception("clientCommandId fehlt");
            if (string.IsNullOrEmpty(cid)) throw new Exception("clientId fehlt");
            bool ignore = BoolArg(a, "ignoreMaintenance", false);
            string inner = "<ClientCommandId>" + GuidBraces(ccid) + "</ClientCommandId>"
                         + "<ClientId>" + ClientRef(cid) + "</ClientId>"
                         + (ignore ? "<CanIgnoreMaintenanceTimes>true</CanIgnoreMaintenanceTimes>" : "")
                         + VarsXml(a["variables"] as JObject);
            return GateCall("acmp_enqueue_client_command", "EnqueueClientCommand", 2, inner, null, RiskTier.Write, a, requester);
        }
        if (name == "acmp_run_powershell")
        {
            if (string.IsNullOrEmpty(PsCommandId))
                throw new Exception("acmp_run_powershell ist nicht konfiguriert (runPowershell.clientCommandId in der Config).");
            string script = (string)a["script"]; if (string.IsNullOrEmpty(script)) throw new Exception("script fehlt");
            string client = (string)a["client"]; if (string.IsNullOrEmpty(client)) client = (string)a["clientId"];
            if (string.IsNullOrEmpty(client)) throw new Exception("client (ClientId-GUID oder Computername) fehlt");
            bool ignore = BoolArg(a, "ignoreMaintenance", true);
            string inner = "<ClientCommandId>" + GuidBraces(PsCommandId) + "</ClientCommandId>"
                         + "<ClientId>" + ClientRef(client) + "</ClientId>"
                         + (ignore ? "<CanIgnoreMaintenanceTimes>true</CanIgnoreMaintenanceTimes>" : "")
                         + "<Variables><TClientCommandVariable_V1><Name>" + Xml(PsVariableName) + "</Name><Value>" + Xml(WrapScriptOneLine(script)) + "</Value></TClientCommandVariable_V1></Variables>";
            // scanExtra bleibt das Klartext-Skript: Freigabe-Vorschau und Denylist-Scan sehen, was wirklich laeuft.
            return GateCall("acmp_run_powershell", "EnqueueClientCommand", 2, inner, script, RiskTier.Destructive, a, requester);
        }
        if (name == "acmp_save_containers")
        {
            string cx = (string)a["containersXml"]; if (string.IsNullOrEmpty(cx)) throw new Exception("containersXml fehlt");
            string inner = cx.IndexOf("<Containers", StringComparison.OrdinalIgnoreCase) >= 0 ? cx : "<Containers>" + cx + "</Containers>";
            return GateCall("acmp_save_containers", "SaveContainers", 1, inner, null, RiskTier.Write, a, requester);
        }
        if (name == "acmp_delete_containers")
        {
            string cx = (string)a["containerIdsXml"]; if (string.IsNullOrEmpty(cx)) throw new Exception("containerIdsXml fehlt");
            return GateCall("acmp_delete_containers", "DeleteContainers", 1, cx, null, RiskTier.Destructive, a, requester);
        }
        if (name == "acmp_create_container")
        {
            string ccName = (string)a["name"]; if (string.IsNullOrEmpty(ccName)) throw new Exception("name fehlt");
            string ccPar = (string)a["parentId"];
            string ccId = "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}";
            string inner = "<Containers><TAcmpContainer_V1 xsi:type=\"TAcmpContainer_V1\">"
                         + "<Id>" + ccId + "</Id><Name>" + Xml(ccName) + "</Name><Path>\\" + Xml(ccName) + "\\</Path>"
                         + (!string.IsNullOrEmpty(ccPar) ? "<ParentId>" + GuidBraces(ccPar) + "</ParentId>" : "")
                         + "</TAcmpContainer_V1></Containers>";
            return GateCall("acmp_create_container", "SaveContainers", 1, inner, null, RiskTier.Write, a, requester);
        }
        if (name == "acmp_lend_asset")
        {
            string laAsset = (string)a["assetId"]; if (string.IsNullOrEmpty(laAsset)) throw new Exception("assetId fehlt");
            string laContact = (string)a["contactId"]; if (string.IsNullOrEmpty(laContact)) throw new Exception("contactId fehlt");
            string laLent = (string)a["lentDate"]; if (string.IsNullOrEmpty(laLent)) laLent = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            string laRet = (string)a["estimatedReturnDate"]; if (string.IsNullOrEmpty(laRet)) laRet = DateTime.UtcNow.AddDays(14).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            string laRental = "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}";
            string inner = "<LendAssets><TLendAsset_V1 xsi:type=\"TLendAsset_V1\"><AssetId>" + GuidBraces(laAsset) + "</AssetId><RentalId>" + laRental + "</RentalId><LentToContactId>" + GuidBraces(laContact) + "</LentToContactId><LentDate>" + laLent + "</LentDate><EstimatedReturnDate>" + laRet + "</EstimatedReturnDate></TLendAsset_V1></LendAssets>";
            return GateCall("acmp_lend_asset", "LendAssets", 1, inner, null, RiskTier.Write, a, requester);
        }
        if (name == "acmp_return_asset")
        {
            string raAsset = (string)a["assetId"]; if (string.IsNullOrEmpty(raAsset)) throw new Exception("assetId fehlt");
            string raDate = (string)a["returnDate"]; if (string.IsNullOrEmpty(raDate)) raDate = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            string raRentalId = null;
            try {
                string raInfo = CONN.Call("GetAsset", 3, "<Id>" + GuidBraces(raAsset) + "</Id>");
                var raRm = System.Text.RegularExpressions.Regex.Match(raInfo, "<RentalId>([^<]+)</RentalId>");
                if (raRm.Success && raRm.Groups[1].Value.Trim().Length > 0) raRentalId = raRm.Groups[1].Value.Trim();
            } catch { }
            string raRentalXml = raRentalId != null ? "<RentalId>" + raRentalId + "</RentalId>" : "";
            string inner = "<ReturnAssets><TReturnAsset_V1 xsi:type=\"TReturnAsset_V1\"><AssetId>" + GuidBraces(raAsset) + "</AssetId>" + raRentalXml + "<LentReturnDate>" + raDate + "</LentReturnDate></TReturnAsset_V1></ReturnAssets>";
            return GateCall("acmp_return_asset", "ReturnAssets", 1, inner, null, RiskTier.Write, a, requester);
        }
        if (name == "acmp_get_asset_types") return Respond(CONN.Call("GetAssetTypeList", 1, ""), a);
        if (name == "acmp_create_asset")
        {
            if (string.IsNullOrEmpty((string)a["name"])) throw new Exception("name fehlt");
            string atype = (string)a["assetType"]; if (string.IsNullOrEmpty(atype)) throw new Exception("assetType fehlt");
            string typeId = ResolveAssetTypeId(atype);
            NormGuids(a, "assetStateId");
            ResolveRefArg(a, "departmentId", "GetDepartmentList");
            ResolveRefArg(a, "locationId", "GetLocationList");
            ResolveRefArg(a, "costCenterId", "GetCostCenterList");
            ResolveRefArg(a, "manufacturerId", "GetCompanyList");
            ResolveRefArg(a, "vendorId", "GetCompanyList");
            // SaveAssets = v1 -> erwartet TAsset_V1 (NICHT TAsset_V3); AssetStateId ist Pflicht -> bei Bedarf Default ermitteln.
            string stateId = (string)a["assetStateId"]; if (string.IsNullOrEmpty(stateId)) stateId = DefaultAssetStateId();
            string[][] map = {
                new[]{"name","Name"}, new[]{"inventoryNumber","InventoryNumber"}, new[]{"model","Model"},
                new[]{"serialNumber","SerialNumber"}, new[]{"description","Description"},
                new[]{"manufacturerId","ManufacturerId"}, new[]{"vendorId","VendorId"},
                new[]{"departmentId","DepartmentId"}, new[]{"locationId","LocationId"}, new[]{"costCenterId","CostCenterId"} };
            string fields = "<AssetTypeId>" + typeId + "</AssetTypeId><AutoGenerateInventoryNumber>false</AutoGenerateInventoryNumber>"
                          + FieldsXml(map, a, null) + "<ContactId/>"
                          + (!string.IsNullOrEmpty(stateId) ? "<AssetStateId>" + stateId + "</AssetStateId>" : "");
            string inner = StammInner("Assets", "TAsset_V1", "Id", NewGuid(), fields, null);
            return GateCall("acmp_create_asset", "SaveAssets", 1, inner, null, RiskTier.Write, a, requester);
        }
        if (name == "acmp_update_asset")
        {
            string aid = (string)a["id"]; if (string.IsNullOrEmpty(aid)) throw new Exception("id fehlt");
            string axId = GuidBraces(aid);
            // GetAsset v1 -> TAsset_V1, passend zu SaveAssets v1 (Read-Modify-Write auf demselben DTO).
            string cur = CONN.Call("GetAsset", 1, "<Id>" + axId + "</Id>");
            var am = System.Text.RegularExpressions.Regex.Match(cur, "<Asset\\b[\\s\\S]*?</Asset>");
            if (!am.Success) throw new Exception("Asset nicht gefunden: " + aid);
            // GetAsset liefert <Asset ...>; SaveAssets erwartet das Element als DTO-Typ <TAsset_V1> -> umbenennen (sonst No-Op).
            string ax = System.Text.RegularExpressions.Regex.Replace(am.Value, "^<Asset\\b", "<TAsset_V1");
            ax = System.Text.RegularExpressions.Regex.Replace(ax, "</Asset>$", "</TAsset_V1>");
            if (!string.IsNullOrEmpty((string)a["assetType"])) ax = ReplaceTag(ax, "AssetTypeId", ResolveAssetTypeId((string)a["assetType"]));
            ResolveRefArg(a, "departmentId", "GetDepartmentList");
            ResolveRefArg(a, "locationId", "GetLocationList");
            ResolveRefArg(a, "costCenterId", "GetCostCenterList");
            ResolveRefArg(a, "manufacturerId", "GetCompanyList");
            ResolveRefArg(a, "vendorId", "GetCompanyList");
            string[][] umap = {
                new[]{"name","Name"}, new[]{"inventoryNumber","InventoryNumber"}, new[]{"model","Model"},
                new[]{"serialNumber","SerialNumber"}, new[]{"description","Description"},
                new[]{"manufacturerId","ManufacturerId"}, new[]{"vendorId","VendorId"},
                new[]{"departmentId","DepartmentId"}, new[]{"locationId","LocationId"}, new[]{"costCenterId","CostCenterId"} };
            foreach (string[] f in umap)
            {
                JToken t = a[f[0]];
                if (t != null && t.Type != JTokenType.Null) ax = ReplaceTag(ax, f[1], t.ToString());
            }
            string inner = "<Assets>" + ax + "</Assets>";
            return GateCall("acmp_update_asset", "SaveAssets", 1, inner, null, RiskTier.Write, a, requester);
        }
        if (name == "acmp_create_contact" || name == "acmp_update_contact")
        {
            bool upd = name == "acmp_update_contact";
            NormGuids(a, "companyId", "departmentId", "locationId", "costCenterId");
            string cur = upd ? CONN.Call("GetContact", 2, "<ID>" + GuidBraces((string)a["id"]) + "</ID>") : null;
            if (!upd && string.IsNullOrEmpty((string)a["name"])) throw new Exception("name (Vorname) fehlt");
            if (!upd && a["preferredContactMethod"] == null)
                a["preferredContactMethod"] = !string.IsNullOrEmpty((string)a["email"]) ? "pcEmail" : "pcPhone";
            string[][] map = {
                new[]{"name","Name"}, new[]{"familyName","FamilyName"}, new[]{"phone","Phone"}, new[]{"phone2","Phone2"},
                new[]{"fax","Fax"}, new[]{"email","Email"}, new[]{"preferredContactMethod","PreferredContactMethod"},
                new[]{"departmentId","DepartmentId"}, new[]{"costCenterId","CostCenterId"}, new[]{"companyId","CompanyId"},
                new[]{"locationId","LocationId"}, new[]{"comment","HTMLComment"}, new[]{"address","Address"},
                new[]{"zipcode","Zipcode"}, new[]{"city","City"}, new[]{"country","Country"}, new[]{"position","Position"} };
            string inner = StammInner("Contacts", "TContact_V3", "ID", upd ? GuidBraces((string)a["id"]) : NewGuid(),
                "<Titles/>" + FieldsXml(map, a, cur), cur);
            return GateCall(name, "SaveContacts", 3, inner, null, RiskTier.Write, a, requester);
        }
        if (name == "acmp_create_company" || name == "acmp_update_company")
        {
            bool upd = name == "acmp_update_company";
            string cur = upd ? CONN.Call("GetCompany", 2, "<Id>" + GuidBraces((string)a["id"]) + "</Id>") : null;
            if (!upd && string.IsNullOrEmpty((string)a["name"])) throw new Exception("name fehlt");
            string[][] map = {
                new[]{"name","Name"}, new[]{"postalCode","PostalCode"}, new[]{"street","Street"}, new[]{"city","City"},
                new[]{"country","Country"}, new[]{"description","Description"}, new[]{"customerId","CustomerId"} };
            bool man = BoolField(a, "isManufacturer", cur, "IsManufacturer");
            bool ven = BoolField(a, "isVendor", cur, "IsVendor");
            bool srv = BoolField(a, "isServicePartner", cur, "IsServicePartner");
            var ks = new StringBuilder("<CompanyKinds>");
            if (ven) ks.Append("<option>ckVendor</option>");
            if (srv) ks.Append("<option>ckServicePartner</option>");
            if (man) ks.Append("<option>ckManufacturer</option>");
            ks.Append("</CompanyKinds>");
            string kinds = ks.ToString()
                + "<IsManufacturer>" + (man ? "true" : "false") + "</IsManufacturer>"
                + "<IsVendor>" + (ven ? "true" : "false") + "</IsVendor>"
                + "<IsServicePartner>" + (srv ? "true" : "false") + "</IsServicePartner>";
            string inner = StammInner("Companies", "TCompany_V2", "Id", upd ? GuidBraces((string)a["id"]) : NewGuid(),
                FieldsXml(map, a, cur) + kinds, cur);
            return GateCall(name, "SaveCompanies", 2, inner, null, RiskTier.Write, a, requester);
        }
        if (name == "acmp_create_location" || name == "acmp_update_location")
        {
            bool upd = name == "acmp_update_location";
            NormGuids(a, "parentId");
            string cur = upd ? CONN.Call("GetLocation", 2, "<Id>" + GuidBraces((string)a["id"]) + "</Id>") : null;
            if (!upd && string.IsNullOrEmpty((string)a["name"])) throw new Exception("name fehlt");
            string[][] map = {
                new[]{"parentId","ParentId"}, new[]{"name","Name"}, new[]{"description","Description"},
                new[]{"street","Street"}, new[]{"city","City"}, new[]{"postalCode","PostalCode"}, new[]{"country","Country"} };
            string lkind = upd ? ExtractTag(cur, "LocationKind") : "lkLocation";
            if (string.IsNullOrEmpty(lkind)) lkind = "lkLocation";
            string inner = StammInner("Locations", "TLocation_V2", "Id", upd ? GuidBraces((string)a["id"]) : NewGuid(),
                FieldsXml(map, a, cur) + "<LocationKind>" + lkind + "</LocationKind>", cur);
            return GateCall(name, "SaveLocations", 2, inner, null, RiskTier.Write, a, requester);
        }
        if (name == "acmp_create_department" || name == "acmp_update_department")
        {
            bool upd = name == "acmp_update_department";
            NormGuids(a, "parentId");
            string cur = upd ? CONN.Call("GetDepartment", 2, "<Id>" + GuidBraces((string)a["id"]) + "</Id>") : null;
            if (!upd && string.IsNullOrEmpty((string)a["name"])) throw new Exception("name fehlt");
            string[][] map = {
                new[]{"parentId","ParentId"}, new[]{"name","Name"}, new[]{"abbreviation","Abbreviation"}, new[]{"description","Description"} };
            string inner = StammInner("Departments", "TDepartment_V2", "Id", upd ? GuidBraces((string)a["id"]) : NewGuid(),
                FieldsXml(map, a, cur), cur);
            return GateCall(name, "SaveDepartments", 2, inner, null, RiskTier.Write, a, requester);
        }
        if (name == "acmp_describe")
        {
            string cmd = (string)a["command"]; if (string.IsNullOrEmpty(cmd)) throw new Exception("command fehlt");
            return TextOut(Describe(cmd));
        }
        if (name == "acmp_outdated_software")
        {
            RequireDb();
            string client = (string)a["client"];
            string cg = null;
            if (!string.IsNullOrEmpty(client)) { cg = DB.ResolveClient(client); if (cg == null) throw new Exception("Client nicht gefunden: " + client); }
            return JsonResult(DB.OutdatedSoftware(cg));
        }
        if (name == "acmp_missing_updates")
        {
            RequireDb();
            string client = (string)a["client"]; if (string.IsNullOrEmpty(client)) throw new Exception("client fehlt");
            string cg = DB.ResolveClient(client); if (cg == null) throw new Exception("Client nicht gefunden: " + client);
            return JsonResult(DB.MissingUpdates(cg));
        }
        if (name == "acmp_software_search")
        {
            RequireDb();
            string nm = (string)a["name"]; if (string.IsNullOrEmpty(nm)) throw new Exception("name fehlt");
            return JsonResult(DB.SoftwareSearch(nm));
        }
        if (name == "acmp_get_contacts") return Respond(CONN.Call("GetContactList", 1, ""), a);
        if (name == "acmp_get_companies") return Respond(CONN.Call("GetCompanyList", 1, ""), a);
        if (name == "acmp_get_locations") return Respond(CONN.Call("GetLocationList", 1, ""), a);
        if (name == "acmp_get_departments") return Respond(CONN.Call("GetDepartmentList", 1, ""), a);
        if (name == "acmp_get_cost_centers") return Respond(CONN.Call("GetCostCenterList", 1, ""), a);
        if (name == "acmp_get_contracts") return Respond(CONN.Call("GetContractList", 1, ""), a);
        if (name == "acmp_get_custom_fields") return Respond(CONN.Call("GetCustomFieldList", 1, ""), a);
        if (name == "acmp_get_queries") return Respond(CONN.Call("GetQueryList", 1, ""), a);
        if (name == "acmp_get_ticket_templates") return Respond(CONN.Call("GetTicketTemplateList", 1, ""), a);
        if (name == "acmp_get_ticket_categories") return Respond(CONN.Call("GetTicketCategoryList", 1, ""), a);
        if (name == "acmp_get_ticket_resolutions") return Respond(CONN.Call("GetTicketResolutionList", 1, ""), a);
        if (name == "acmp_get_ticket_comments") return Respond(CONN.Call("GetTicketComments", 1, "<TicketId>" + ResolveTicketGuid((string)a["id"]) + "</TicketId>"), a);
        if (name == "acmp_get_ticket_worktime") return Respond(CONN.Call("GetTicketWorkingTimeList", 1, "<TicketId>" + ResolveTicketGuid((string)a["id"]) + "</TicketId>"), a);
        if (name == "acmp_get_ticket_costs") return Respond(CONN.Call("GetTicketCostList", 1, "<TicketId>" + ResolveTicketGuid((string)a["id"]) + "</TicketId>"), a);
        if (name == "acmp_get_linked_tickets") return Respond(CONN.Call("GetLinkedTicketsOfTicket", 1, "<TicketId>" + ResolveTicketGuid((string)a["id"]) + "</TicketId>"), a);
        if (name == "acmp_tickets_by_query")
        {
            string qid = (string)a["queryId"]; if (string.IsNullOrEmpty(qid)) throw new Exception("queryId fehlt");
            int qver = a["version"] != null ? (int)a["version"] : 2;
            return Respond(CONN.Call("GetTicketListByQuery", qver, "<QueryId>" + GuidBraces(qid) + "</QueryId>"), a);
        }
        if (name == "acmp_get_hardware")
        {
            string hcid = (string)a["clientId"]; if (string.IsNullOrEmpty(hcid)) throw new Exception("clientId fehlt");
            string comp = (string)a["component"]; if (string.IsNullOrEmpty(comp)) throw new Exception("component fehlt");
            string hcmd;
            switch (comp.Trim().ToLowerInvariant())
            {
                case "cpu": hcmd = "GetCPU"; break;
                case "memory": hcmd = "GetMemory"; break;
                case "memoryslots": hcmd = "GetMemorySlots"; break;
                case "harddrives": hcmd = "GetHardDrives"; break;
                case "logicaldrives": hcmd = "GetLogicalDrives"; break;
                case "netcards": hcmd = "GetNetcards"; break;
                case "displays": hcmd = "GetDisplays"; break;
                case "monitors": hcmd = "GetMonitors"; break;
                case "bios": hcmd = "GetBiosData"; break;
                case "mainboard": hcmd = "GetMainboard"; break;
                case "printers": hcmd = "GetPrinters"; break;
                case "services": hcmd = "GetServices"; break;
                case "battery": hcmd = "GetBattery"; break;
                case "devices": hcmd = "GetDevices"; break;
                case "shares": hcmd = "GetShares"; break;
                case "cddvd": hcmd = "GetCdDvdDrives"; break;
                default: throw new Exception("Unbekannte component: " + comp);
            }
            return Respond(CONN.Call(hcmd, 1, "<ClientId>" + Xml(hcid) + "</ClientId>"), a);
        }
        if (name == "acmp_close_ticket")
        {
            string tg = ResolveTicketGuid((string)a["id"]);
            string ct = (string)a["comment"]; if (string.IsNullOrEmpty(ct)) throw new Exception("comment fehlt (ACMP verlangt beim Schliessen einen Kommentar).");
            string tx = CONN.Call("GetTicket", 5, "<Id>" + tg + "</Id>");
            string catP = (string)a["categoryId"]; string catXml;
            if (!string.IsNullOrEmpty(catP)) catXml = "<CategoryId>" + GuidBraces(catP) + "</CategoryId>";
            else { var mc = System.Text.RegularExpressions.Regex.Match(tx, "<CategoryId>([^<]+)</CategoryId>"); if (!mc.Success) throw new Exception("Ticket hat keine Kategorie - bitte categoryId angeben."); catXml = "<CategoryId>" + mc.Groups[1].Value + "</CategoryId>"; }
            string asnP = (string)a["assigneeId"]; string asnXml;
            if (!string.IsNullOrEmpty(asnP)) asnXml = "<Assignee xsi:type=\"TAssigneeInfo_V1\"><Id>" + GuidBraces(asnP) + "</Id><AssigneeIsGroup>false</AssigneeIsGroup></Assignee>";
            else { var ma = System.Text.RegularExpressions.Regex.Match(tx, "<Assignee\\b.*?</Assignee>", System.Text.RegularExpressions.RegexOptions.Singleline); if (!ma.Success || !System.Text.RegularExpressions.Regex.IsMatch(ma.Value, "<Id>\\{?[0-9A-Fa-f-]+\\}?</Id>")) throw new Exception("Ticket hat keinen Bearbeiter - bitte assigneeId angeben."); asnXml = ma.Value; }
            string resP = (string)a["resolutionId"]; string resVal;
            if (!string.IsNullOrEmpty(resP)) resVal = resP.Trim().Equals("NO_RESOLUTION", StringComparison.OrdinalIgnoreCase) ? "NO_RESOLUTION" : GuidBraces(resP);
            else { var mr = System.Text.RegularExpressions.Regex.Match(tx, "<ResolutionId>([^<]+)</ResolutionId>"); resVal = mr.Success ? mr.Groups[1].Value : "NO_RESOLUTION"; }
            string ccid = "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}";
            string inner = "<TicketId>" + tg + "</TicketId>"
                         + "<Comment xsi:type=\"TTicketComment_V2\"><ID>" + ccid + "</ID><TicketID>" + tg + "</TicketID><CommentText>" + Xml(ct) + "</CommentText><IsVisibleForWeb>true</IsVisibleForWeb></Comment>"
                         + catXml + asnXml + "<ResolutionId>" + resVal + "</ResolutionId>";
            return GateCall("acmp_close_ticket", "CloseTicket", 1, inner, null, RiskTier.Write, a, requester);
        }
        if (name == "acmp_reopen_ticket")
        {
            string tg = ResolveTicketGuid((string)a["id"]);
            return GateCall("acmp_reopen_ticket", "ReopenTicket", 1, "<TicketId>" + tg + "</TicketId>", null, RiskTier.Write, a, requester);
        }
        if (name == "acmp_add_ticket_comment")
        {
            string tg = ResolveTicketGuid((string)a["id"]);
            string ct = (string)a["comment"]; if (string.IsNullOrEmpty(ct)) throw new Exception("comment fehlt");
            bool intern = BoolArg(a, "internal", false);
            string ccid = "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}";
            string inner = "<Comment xsi:type=\"TTicketComment_V2\">"
                         + "<ID>" + ccid + "</ID>"
                         + "<TicketID>" + tg + "</TicketID>"
                         + "<CommentText>" + Xml(ct) + "</CommentText>"
                         + "<IsVisibleForWeb>" + (intern ? "false" : "true") + "</IsVisibleForWeb>"
                         + "</Comment>";
            return GateCall("acmp_add_ticket_comment", "SaveTicketComment", 2, inner, null, RiskTier.Write, a, requester);
        }
        if (name == "acmp_read_registry")
        {
            if (string.IsNullOrEmpty(PsCommandId)) throw new Exception("acmp_read_registry ist nicht konfiguriert (runPowershell.clientCommandId in der Config).");
            if (string.IsNullOrEmpty(PublicUrl)) throw new Exception("acmp_read_registry benoetigt eine erreichbare publicUrl (approval.publicUrl) fuer den Rueckkanal.");
            string client = (string)a["client"]; if (string.IsNullOrEmpty(client)) client = (string)a["clientId"];
            if (string.IsNullOrEmpty(client)) throw new Exception("client (ClientId-GUID oder Computername) fehlt");
            string path = (string)a["path"]; if (string.IsNullOrEmpty(path)) throw new Exception("path (Registry-Pfad) fehlt");
            string valName = (string)a["value"];
            int timeoutSec = a["timeoutSec"] != null ? (int)a["timeoutSec"] : 60;
            if (timeoutSec < 5) timeoutSec = 5; if (timeoutSec > 300) timeoutSec = 300;

            // Alte Auftraege aufraeumen (aelter als 30 min).
            lock (RegReads)
            {
                var stale = new List<string>();
                foreach (var kv in RegReads) if ((DateTime.UtcNow - kv.Value.Created).TotalMinutes > 30) stale.Add(kv.Key);
                foreach (var k in stale) RegReads.Remove(k);
            }

            string clientRef = ClientRef(client); // wirft frueh bei unbekanntem Client
            string token = Guid.NewGuid().ToString("N");
            var rr = new RegRead { Client = client, Path = path, Value = valName, Created = DateTime.UtcNow };
            lock (RegReads) { RegReads[token] = rr; }

            string script = BuildRegReadScript(token, path, valName);
            string inner = "<ClientCommandId>" + GuidBraces(PsCommandId) + "</ClientCommandId>"
                         + "<ClientId>" + clientRef + "</ClientId>"
                         + "<CanIgnoreMaintenanceTimes>true</CanIgnoreMaintenanceTimes>"
                         + "<Variables><TClientCommandVariable_V1><Name>" + Xml(PsVariableName) + "</Name><Value>" + Xml(script) + "</Value></TClientCommandVariable_V1></Variables>";
            string enq = CONN.Call("EnqueueClientCommand", 2, inner);
            var em = System.Text.RegularExpressions.Regex.Match(enq, "<ResultCode>(\\d+)</ResultCode>");
            if (em.Success && em.Groups[1].Value != "0")
            {
                lock (RegReads) { RegReads.Remove(token); }
                var er = Respond(enq, a); er["isError"] = true; return er;
            }

            bool got = rr.Signal.WaitOne(timeoutSec * 1000);
            if (got && rr.Done) return TextOut(FormatRegResult(rr));
            return TextOut("Registry-Leseauftrag an Client '" + client + "' wurde abgeschickt, aber innerhalb " + timeoutSec + "s kam noch keine Antwort.\n"
                + "Moegliche Gruende: Client offline, Job noch nicht abgeholt, oder kein Netzweg vom Client zum Server (" + PublicUrl + ").\n"
                + "Spaeter nachfragen mit acmp_read_registry_result token=\"" + token + "\".");
        }
        if (name == "acmp_read_registry_result")
        {
            string token = (string)a["token"]; if (string.IsNullOrEmpty(token)) throw new Exception("token fehlt");
            RegRead rr; lock (RegReads) { RegReads.TryGetValue(token, out rr); }
            if (rr == null) return TextOut("Unbekanntes oder abgelaufenes Token: " + token);
            if (!rr.Done) return TextOut("Noch keine Antwort vom Client fuer Token " + token + " (seit " + (int)(DateTime.UtcNow - rr.Created).TotalSeconds + "s). Bitte spaeter erneut versuchen.");
            return TextOut(FormatRegResult(rr));
        }
        foreach (JObject d in ApiToolDefs)
        {
            if ((string)d["name"] != name) continue;
            string command = (string)d["command"];
            int version = d["version"] != null ? (int)d["version"] : 1;
            string inner = BuildApiInner(d["args"] as JArray, a);
            RiskTier tier = d["tier"] != null ? ParseTier((string)d["tier"]) : (APPROVAL != null ? APPROVAL.Classify(command) : RiskTier.Read);
            return GateCall(name, command, version, inner, null, tier, a, requester);
        }
        foreach (JObject d in DbToolDefs)
        {
            if ((string)d["name"] == name) { RequireDb(); return JsonResult(DB.RunTool(d, a)); }
        }
        throw new Exception("Unbekanntes Tool: " + name);
    }

    static JObject TextOut(string text)
    {
        var c = new JObject(); c["type"] = "text"; c["text"] = text != null ? text : "";
        var arr = new JArray(); arr.Add(c);
        var r = new JObject(); r["content"] = arr; return r;
    }

    // Verarbeitet den Rueckkanal-POST des Clients (acmp_read_registry): { token, ok, value, error }.
    static void HandleRegResult(string bodyStr)
    {
        try
        {
            JObject o = JToken.Parse(bodyStr) as JObject;
            if (o == null) return;
            string token = (string)o["token"];
            if (string.IsNullOrEmpty(token)) return;
            RegRead rr; lock (RegReads) { RegReads.TryGetValue(token, out rr); }
            if (rr == null) { Log("regresult: unbekanntes Token " + token); return; }
            rr.Ok = o["ok"] != null && o["ok"].Type == JTokenType.Boolean && (bool)o["ok"];
            rr.Result = (o["value"] != null && o["value"].Type != JTokenType.Null) ? o["value"].ToString() : null;
            rr.Error = (string)o["error"];
            rr.Done = true;
            rr.Signal.Set();
            Log("regresult empfangen (Token " + token + ", ok=" + rr.Ok + ")");
        }
        catch (Exception ex) { Log("regresult Fehler: " + ex.Message); }
    }

    // Baut das FESTE, nur-lesende PowerShell, das den Registry-Wert ausliest und per HTTP an /regresult zurueckmeldet.
    // path/value sind reine Daten (als PowerShell-Here-Strings eingebettet) - es wird KEIN nutzergelieferter Code ausgefuehrt.
    static string BuildRegReadScript(string token, string path, string valName)
    {
        string p = (path ?? "").Replace("\r", "").Replace("\n", "");
        string v = (valName ?? "").Replace("\r", "").Replace("\n", "");
        string cb = PublicUrl + "/regresult";
        var sb = new StringBuilder();
        sb.Append("$ErrorActionPreference='Stop'\n");
        sb.Append("$token='").Append(token).Append("'\n");
        sb.Append("$cb='").Append(cb).Append("'\n");
        sb.Append("$rawPath=@'\n").Append(p).Append("\n'@\n");
        sb.Append("$valName=@'\n").Append(v).Append("\n'@\n");
        sb.Append("try{ Add-Content -LiteralPath \"$env:ProgramData\acmp-mcp-client.log\" -Value (\"[\"+(Get-Date -f 'yyyy-MM-dd HH:mm:ss')+\"] regread start token=\"+$token) }catch{}\n");
        sb.Append("function Send-Result($ok,$value,$err){\n");
        sb.Append("  $payload=@{token=$token;ok=$ok;value=$value;error=$err} | ConvertTo-Json -Depth 6 -Compress\n");
        sb.Append("  try{ Invoke-RestMethod -Uri $cb -Method Post -Body $payload -ContentType 'application/json' -TimeoutSec 30 }catch{ try{ Add-Content -LiteralPath \"$env:ProgramData\acmp-mcp-client.log\" -Value (\"[\"+(Get-Date -f 'yyyy-MM-dd HH:mm:ss')+\"] regresult POST fehlgeschlagen: \"+$_.Exception.Message) }catch{} }\n");
        sb.Append("}\n");
        sb.Append("try{\n");
        sb.Append("  $pp=$rawPath -replace '/','\\'\n");
        sb.Append("  $map=@{'HKLM'='HKEY_LOCAL_MACHINE';'HKCU'='HKEY_CURRENT_USER';'HKCR'='HKEY_CLASSES_ROOT';'HKU'='HKEY_USERS';'HKCC'='HKEY_CURRENT_CONFIG'}\n");
        sb.Append("  $parts=$pp -split '\\\\',2\n");
        sb.Append("  $hive=$parts[0].ToUpper(); if($map.ContainsKey($hive)){$hive=$map[$hive]}\n");
        sb.Append("  $sub=if($parts.Count -gt 1){$parts[1]}else{''}\n");
        // HKCU/Langform unter SYSTEM -> auf den angemeldeten Benutzer (HKEY_USERS\<SID>) aufloesen
        sb.Append("  if($hive -eq 'HKEY_CURRENT_USER'){\n");
        sb.Append("    $u=(Get-CimInstance Win32_ComputerSystem).UserName\n");
        sb.Append("    if($u){ $sid=(New-Object System.Security.Principal.NTAccount($u)).Translate([System.Security.Principal.SecurityIdentifier]).Value; $hive=\"HKEY_USERS\\$sid\" } else { throw 'Kein interaktiv angemeldeter Benutzer (HKCU nicht aufloesbar).' }\n");
        sb.Append("  }\n");
        sb.Append("  $full=\"Registry::$hive\"; if($sub){ $full=\"$full\\$sub\" }\n");
        sb.Append("  $item=Get-ItemProperty -Path $full\n");
        sb.Append("  if($valName){\n");
        sb.Append("    $val=$item.$valName\n");
        sb.Append("    if($null -eq $val){ Send-Result $true $null \"Wert '$valName' existiert nicht unter dem Schluessel.\" }\n");
        sb.Append("    else { $disp=if($val -is [array]){ ($val -join ', ') } else { \"$val\" }; Send-Result $true $disp $null }\n");
        sb.Append("  } else {\n");
        sb.Append("    $o=@{}\n");
        sb.Append("    foreach($prop in $item.PSObject.Properties){ if('PSPath','PSParentPath','PSChildName','PSDrive','PSProvider' -notcontains $prop.Name){ $vv=$prop.Value; $o[$prop.Name]=if($vv -is [array]){($vv -join ', ')}else{\"$vv\"} } }\n");
        sb.Append("    Send-Result $true ($o | ConvertTo-Json -Depth 4 -Compress) $null\n");
        sb.Append("  }\n");
        sb.Append("}catch{ Send-Result $false $null ($_.Exception.Message) }\n");
        // Einzeilig uebergeben: die ACMP-Variablenersetzung kann Zeilenumbrueche und
        // Anfuehrungszeichen zerstoeren, Base64 ueberlebt beides.
        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString()));
        return "$s=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + b64 + "'));Invoke-Expression $s";
    }

    // Skript einzeilig als Base64 verpacken: die ACMP-Variablenersetzung (%SCRIPT%) zerstoert
    // Zeilenumbrueche und kann Anfuehrungszeichen verfaelschen. Base64 + Invoke-Expression ueberlebt beides.
    // Einschraenkung: ein param()-Block am Skriptanfang funktioniert in Invoke-Expression nicht.
    static string WrapScriptOneLine(string script)
    {
        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script != null ? script : ""));
        return "$s=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + b64 + "'));Invoke-Expression $s";
    }

    // Formatiert ein Registry-Ergebnis fuer die Chat-Ausgabe.
    static string FormatRegResult(RegRead rr)
    {
        var sb = new StringBuilder();
        sb.Append("Registry-Auslesung\n");
        sb.Append("Client: ").Append(rr.Client).Append("\n");
        sb.Append("Pfad:   ").Append(rr.Path).Append("\n");
        if (!string.IsNullOrEmpty(rr.Value)) sb.Append("Wert:   ").Append(rr.Value).Append("\n");
        sb.Append("\n");
        if (rr.Ok)
        {
            string res = rr.Result;
            if (!string.IsNullOrEmpty(res) && res.TrimStart().StartsWith("{"))
            {
                try { res = JToken.Parse(res).ToString(Formatting.Indented); } catch { }
            }
            sb.Append("Ergebnis:\n").Append(string.IsNullOrEmpty(res) ? "(leer / Wert nicht vorhanden)" : res);
            if (!string.IsNullOrEmpty(rr.Error)) sb.Append("\n\nHinweis: ").Append(rr.Error);
        }
        else sb.Append("FEHLER auf dem Client: ").Append(string.IsNullOrEmpty(rr.Error) ? "unbekannt" : rr.Error);
        return sb.ToString();
    }

    static void RequireDb()
    {
        if (DB == null || !DB.Enabled)
            throw new Exception("DB-Report-Tools sind nicht konfiguriert (db.connectionString in der Config fehlt).");
    }

    static JObject JsonResult(JArray rows)
    {
        return TextOut("[" + rows.Count + " Zeilen]\n" + rows.ToString(Formatting.Indented));
    }

    // GUID -> Computername (ueber DB), fuer lesbare Freigabe-Beschreibungen.
    static string ResolveClientName(string guid)
    {
        try { if (DB != null && DB.Enabled) return DB.ClientName(guid); } catch { }
        return null;
    }

    // GUID -> letzter angemeldeter Benutzer (ueber DB).
    static string ResolveClientUser(string guid)
    {
        try { if (DB != null && DB.Enabled) return DB.LastUser(guid); } catch { }
        return null;
    }

    // GUID -> Client-Command-Name [Ordner] (ueber GetClientCommandList, gecacht; bei Miss max. alle 60s neu).
    static Dictionary<string, string> CmdNameCache;
    static DateTime CmdCacheTime;
    static readonly object _cmdCacheLock = new object();
    static string ResolveCommandName(string guid)
    {
        if (string.IsNullOrEmpty(guid)) return null;
        string key = guid.Trim().Trim('{', '}').ToUpperInvariant();
        lock (_cmdCacheLock)
        {
            bool stale = CmdNameCache == null;
            string val = null;
            if (CmdNameCache != null) CmdNameCache.TryGetValue(key, out val);
            if (val == null && (stale || (DateTime.UtcNow - CmdCacheTime).TotalSeconds > 60))
            {
                try
                {
                    var cache = new Dictionary<string, string>();
                    string xml = CONN.Call("GetClientCommandList", 1, "");
                    var ms = System.Text.RegularExpressions.Regex.Matches(xml, "<Id>\\{?([0-9A-Fa-f-]+)\\}?</Id><Name>([^<]*)</Name><FolderPath>([^<]*)</FolderPath>");
                    foreach (System.Text.RegularExpressions.Match m in ms)
                        cache[m.Groups[1].Value.ToUpperInvariant()] = m.Groups[2].Value + (m.Groups[3].Value.Length > 0 ? " [" + m.Groups[3].Value + "]" : "");
                    CmdNameCache = cache; CmdCacheTime = DateTime.UtcNow;
                    CmdNameCache.TryGetValue(key, out val);
                }
                catch { }
            }
            return val;
        }
    }

    // Vier-Augen-Tor: Read laeuft sofort, Write/Destructive werden eingefroren und brauchen Freigabe.
    // Baut Inner-XML aus den gesetzten Argumenten eines api-tools.json-Tools (arg.xml = Elementname, sonst arg.name).
    static string BuildApiInner(JArray args, JObject a)
    {
        if (args == null) return "";
        var sb = new StringBuilder();
        foreach (JObject arg in args)
        {
            string an = (string)arg["name"];
            JToken t = a != null ? a[an] : null;
            if (t == null || t.Type == JTokenType.Null) continue;
            string val = t.ToString();
            if (val.Length == 0) continue;
            string xmlName = arg["xml"] != null ? (string)arg["xml"] : an;
            string kind = (string)arg["kind"];
            string content = (kind == "int" || kind == "integer" || kind == "raw") ? val : Xml(val);
            sb.Append("<").Append(xmlName).Append(">").Append(content).Append("</").Append(xmlName).Append(">");
        }
        return sb.ToString();
    }

    static RiskTier ParseTier(string s)
    {
        if (string.Equals(s, "Destructive", StringComparison.OrdinalIgnoreCase)) return RiskTier.Destructive;
        if (string.Equals(s, "Write", StringComparison.OrdinalIgnoreCase)) return RiskTier.Write;
        return RiskTier.Read;
    }

    static JObject GateCall(string tool, string command, int version, string inner, string scanExtra, RiskTier tier, JObject a, string requester)
    {
        if (NoApprovalTools.Contains(tool))
            return Respond(CONN.Call(command, version, inner), a);
        if (APPROVAL == null || !APPROVAL.Enabled || tier == RiskTier.Read || APPROVAL.BypassAll)
            return Respond(CONN.Call(command, version, inner), a);
        string reason;
        if (APPROVAL.IsBlocked(command, (inner != null ? inner : "") + "\n" + (scanExtra != null ? scanExtra : ""), out reason))
            throw new Exception("Hart blockiert (Sicherheits-Denylist): " + reason);
        string intent = a != null && a["intent"] != null ? (string)a["intent"] : null;
        string id = APPROVAL.EnqueueCall(tool, command, version, inner, scanExtra, tier, intent, requester);
        return TextOut(APPROVAL.PendingMessage(id));
    }

    static JObject GatePublish(string tool, string raw, JObject a, string requester)
    {
        if (NoApprovalTools.Contains(tool) || APPROVAL == null || !APPROVAL.Enabled || APPROVAL.BypassAll)
            return Respond(CONN.Publish(raw), a);
        string reason;
        if (APPROVAL.IsBlocked("(rawIcql)", raw, out reason))
            throw new Exception("Hart blockiert (Sicherheits-Denylist): " + reason);
        string intent = a != null && a["intent"] != null ? (string)a["intent"] : null;
        string id = APPROVAL.EnqueuePublish(tool, raw, RiskTier.Destructive, intent, requester);
        return TextOut(APPROVAL.PendingMessage(id));
    }

    static JObject Respond(string xml, JObject a)
    {
        bool isErr = false;
        if (xml != null) { var m = System.Text.RegularExpressions.Regex.Match(xml, "<ResultCode>(\\d+)</ResultCode>"); if (m.Success && m.Groups[1].Value != "0") isErr = true; }
        string fmt = a != null && a["format"] != null ? (string)a["format"] : "xml";
        string text = (fmt != null && fmt.Equals("json", StringComparison.OrdinalIgnoreCase)) ? XmlToJson(xml) : xml;
        var r = TextOut(text);
        if (isErr) r["isError"] = true;
        return r;
    }

    static string XmlToJson(string xml)
    {
        if (xml == null) return "";
        try
        {
            string x = xml.Trim();
            if (x.StartsWith("<?")) { int i = x.IndexOf("?>"); if (i >= 0) x = x.Substring(i + 2).Trim(); }
            var doc = new System.Xml.XmlDocument(); doc.LoadXml(x);
            return JsonConvert.SerializeXmlNode(doc, Formatting.Indented, false);
        }
        catch { return xml; }
    }

    static string ParseCatalog(string xml)
    {
        try
        {
            var ms = System.Text.RegularExpressions.Regex.Matches(xml, "<CommandName>([^<]+)</CommandName><Versions>(.*?)</Versions>", System.Text.RegularExpressions.RegexOptions.Singleline);
            var list = new List<string>();
            foreach (System.Text.RegularExpressions.Match m in ms)
            {
                var vs = System.Text.RegularExpressions.Regex.Matches(m.Groups[2].Value, "<Value>([^<]+)</Value>");
                var vl = new List<string>(); foreach (System.Text.RegularExpressions.Match v in vs) vl.Add(v.Groups[1].Value);
                list.Add(m.Groups[1].Value + "  v[" + string.Join(",", vl.ToArray()) + "]");
            }
            list.Sort();
            return "ACMP Public API - " + list.Count + " Kommandos:\n" + string.Join("\n", list.ToArray());
        }
        catch { return xml; }
    }

    static string Describe(string cmd)
    {
        string cat = CONN.Call("GetCommandVersions", 1, "<Commands><string>" + Xml(cmd) + "</string></Commands>");
        var vm = System.Text.RegularExpressions.Regex.Match(cat, "<CommandName>" + System.Text.RegularExpressions.Regex.Escape(cmd) + "</CommandName><Versions>(.*?)</Versions>", System.Text.RegularExpressions.RegexOptions.Singleline);
        string versions = "(nicht im Katalog gefunden)"; int maxVer = 1;
        if (vm.Success)
        {
            var vs = System.Text.RegularExpressions.Regex.Matches(vm.Groups[1].Value, "<Value>([^<]+)</Value>");
            var vl = new List<string>();
            foreach (System.Text.RegularExpressions.Match v in vs) { vl.Add(v.Groups[1].Value); int iv; if (int.TryParse(v.Groups[1].Value, out iv) && iv > maxVer) maxVer = iv; }
            if (vl.Count > 0) versions = string.Join(",", vl.ToArray());
        }
        var sb = new StringBuilder();
        sb.Append("Kommando: ").Append(cmd).Append("\nVerfuegbare Versionen: ").Append(versions).Append("\n");
        sb.Append("acmp_call: { command:'").Append(cmd).Append("', version:").Append(maxVer).Append(", inner:'<FELDER>' }\n");
        sb.Append("Erzeugte ICQL: <ICQL><ACMP><").Append(cmd).Append(" version=\"").Append(maxVer).Append("\"><T").Append(cmd).Append("Request_V").Append(maxVer).Append(" xmlns:xsi=\"...\"> <FELDER> </T").Append(cmd).Append("Request_V").Append(maxVer).Append("></").Append(cmd).Append("></ACMP></ICQL>\n");
        if (cmd.StartsWith("Get", StringComparison.OrdinalIgnoreCase))
        {
            try { sb.Append("\nLive-Antwort auf leeren Request (gekuerzt, zeigt Feldstruktur):\n").Append(Truncate(CONN.Call(cmd, maxVer, ""), 1600)); }
            catch (Exception ex) { Exception inner = ex.InnerException != null ? ex.InnerException : ex; sb.Append("\nLeerer Request -> ").Append(inner.Message).Append("  (nennt ggf. Pflichtfelder)"); }
        }
        else sb.Append("\nHinweis: Schreib-/Ausfuehr-Kommando - nicht automatisch getestet. Pflichtfelder zeigt die Fehlermeldung bei minimalem acmp_call.");
        return sb.ToString();
    }

    static string Xml(string s) { if (s == null) return ""; return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;"); }

    // ---------- Stammdaten-Schreibhelfer ----------
    static string NewGuid() { return "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}"; }

    // Asset-Typ (Name ODER GUID) -> Typ-GUID. Loest Namen ueber GetAssetTypeList auf. Wirft bei Nichtfund.
    static string ResolveAssetTypeId(string nameOrId)
    {
        if (string.IsNullOrEmpty(nameOrId)) throw new Exception("assetType fehlt");
        string s = nameOrId.Trim();
        Guid g; if (Guid.TryParse(s.Trim('{', '}'), out g)) return "{" + g.ToString().ToUpperInvariant() + "}";
        string leaf = s; int bi = s.LastIndexOf('\\'); if (bi >= 0) leaf = s.Substring(bi + 1);
        string list = CONN.Call("GetAssetTypeList", 1, "");
        var ms = System.Text.RegularExpressions.Regex.Matches(list, "<TAssetType_V1[^>]*><Id>(\\{[^}]+\\})</Id><Name>([^<]*)</Name>");
        foreach (System.Text.RegularExpressions.Match m in ms)
            if (string.Equals(m.Groups[2].Value, leaf, StringComparison.OrdinalIgnoreCase)) return m.Groups[1].Value;
        throw new Exception("Asset-Typ nicht gefunden: '" + nameOrId + "'. Verfuegbare Typen via acmp_get_asset_types. Neue Typen/Kategorien koennen NUR in der ACMP-Konsole angelegt werden.");
    }

    // Loest einen Referenz-Namen (Abteilung/Standort/Firma/...) ueber ein Get*List-Kommando zur GUID auf. GUID wird durchgereicht.
    static string ResolveRefId(string nameOrId, string listCmd)
    {
        if (string.IsNullOrEmpty(nameOrId)) return "";
        string s = nameOrId.Trim();
        Guid g; if (Guid.TryParse(s.Trim('{', '}'), out g)) return "{" + g.ToString().ToUpperInvariant() + "}";
        string list = CONN.Call(listCmd, 1, "");
        // Jede GUID mit dem naechsten <Name> innerhalb desselben Eintrags paaren (bis zur naechsten <Id>).
        var ms = System.Text.RegularExpressions.Regex.Matches(list, "<Id>(\\{[^}]+\\})</Id>(?:(?!<Id>)[\\s\\S])*?<Name>([^<]*)</Name>");
        foreach (System.Text.RegularExpressions.Match m in ms)
            if (string.Equals(m.Groups[2].Value, s, StringComparison.OrdinalIgnoreCase)) return m.Groups[1].Value;
        throw new Exception("Nicht gefunden: '" + nameOrId + "' (" + listCmd + "). Per Liste die genaue Bezeichnung oder die GUID pruefen.");
    }

    // Loest ein Referenz-Argument in-place auf (Name -> GUID), falls gesetzt.
    static void ResolveRefArg(JObject a, string key, string listCmd)
    {
        JToken t = a != null ? a[key] : null;
        if (t == null || t.Type == JTokenType.Null) return;
        string s = t.ToString().Trim();
        if (s.Length > 0) a[key] = ResolveRefId(s, listCmd);
    }

    // Ermittelt eine gueltige AssetStateId (Pflichtfeld bei SaveAssets) aus einem bestehenden Asset. null wenn nicht ermittelbar.
    static string DefaultAssetStateId()
    {
        try
        {
            string list = CONN.Call("GetAssetList", 3, "");
            var m = System.Text.RegularExpressions.Regex.Match(list, "<Id>(\\{[^}]+\\})</Id>");
            if (m.Success)
            {
                string a1 = CONN.Call("GetAsset", 1, "<Id>" + m.Groups[1].Value + "</Id>");
                var sm = System.Text.RegularExpressions.Regex.Match(a1, "<AssetStateId>(\\{[^}]+\\})</AssetStateId>");
                if (sm.Success) return sm.Groups[1].Value;
            }
        }
        catch { }
        return null;
    }

    // Ersetzt den ersten Vorkommen-Block <Tag>..</Tag> bzw. <Tag/> durch einen neuen Wert (leer -> self-closing).
    static string ReplaceTag(string xml, string tag, string val)
    {
        string rep = string.IsNullOrEmpty(val) ? "<" + tag + "/>" : "<" + tag + ">" + Xml(val) + "</" + tag + ">";
        var m = System.Text.RegularExpressions.Regex.Match(xml, "<" + tag + ">[\\s\\S]*?</" + tag + ">|<" + tag + "\\s*/>");
        if (m.Success) return xml.Substring(0, m.Index) + rep + xml.Substring(m.Index + m.Length);
        return xml;
    }

    // Baut das Save-Inner: <Liste><TDto xsi:type="TDto"><IdTag>id</IdTag>{felder}</TDto></Liste>
    static string StammInner(string listTag, string dtoType, string idTag, string id, string fieldsXml, string curXml)
    {
        // ACMP Save*-Kommandos brauchen eine GESETZTE Id (auch bei Neuanlage; leere Id -> ResultCode 35 "Id may not be empty").
        // Fuer Neuanlagen wird daher eine frische GUID uebergeben; bei Update die bestehende.
        var sb = new StringBuilder();
        sb.Append("<").Append(listTag).Append("><").Append(dtoType).Append(" xsi:type=\"").Append(dtoType).Append("\">");
        sb.Append("<").Append(idTag).Append(">").Append(id).Append("</").Append(idTag).Append(">");
        sb.Append(fieldsXml);
        sb.Append("</").Append(dtoType).Append("></").Append(listTag).Append(">");
        return sb.ToString();
    }

    // Baut <Tag>Wert</Tag> je Feld. Wert = Arg (falls gesetzt), sonst aktueller Wert aus curXml (Update), sonst leer.
    // Leere Werte werden als self-closing <Tag/> ausgegeben (wie die API es selbst liefert -> kein leerer Enum/GUID-Fehler).
    static string FieldsXml(string[][] map, JObject a, string curXml)
    {
        var sb = new StringBuilder();
        foreach (string[] f in map)
        {
            string arg = f[0], tag = f[1], v = null;
            JToken t = a != null ? a[arg] : null;
            if (t != null && t.Type != JTokenType.Null) v = t.ToString();
            else if (curXml != null) v = ExtractTag(curXml, tag);
            if (string.IsNullOrEmpty(v)) sb.Append("<").Append(tag).Append("/>");
            else sb.Append("<").Append(tag).Append(">").Append(Xml(v)).Append("</").Append(tag).Append(">");
        }
        return sb.ToString();
    }

    // Liest den Textinhalt eines XML-Tags (erste Fundstelle). Entschluesselt XML-Entities. Self-closing/fehlend -> "".
    static string ExtractTag(string xml, string tag)
    {
        if (xml == null) return "";
        var m = System.Text.RegularExpressions.Regex.Match(xml, "<" + tag + ">(.*?)</" + tag + ">", System.Text.RegularExpressions.RegexOptions.Singleline);
        if (!m.Success) return "";
        return System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
    }

    // Bool aus Arg (falls gesetzt), sonst aus curXml (Update), sonst false.
    static bool BoolField(JObject a, string arg, string curXml, string tag)
    {
        JToken t = a != null ? a[arg] : null;
        if (t != null && t.Type != JTokenType.Null) return BoolArg(a, arg, false);
        if (curXml != null) return ExtractTag(curXml, tag).Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    // Normalisiert die genannten GUID-Argumente auf {XXXX...}-Format (falls gesetzt). Wirft bei ungueltiger GUID.
    static void NormGuids(JObject a, params string[] keys)
    {
        if (a == null) return;
        foreach (string k in keys)
        {
            JToken t = a[k];
            if (t != null && t.Type != JTokenType.Null)
            {
                string s = t.ToString().Trim();
                if (s.Length > 0) a[k] = GuidBraces(s);
            }
        }
    }

    // GUID -> registry-format {XXXXXXXX-....} (Grossschreibung, wie von der API geliefert). Wirft bei ungueltiger GUID.
    static string GuidBraces(string s)
    {
        Guid g;
        if (s == null || !Guid.TryParse(s.Trim().Trim('{', '}'), out g)) throw new Exception("Ungueltige GUID: " + s);
        return "{" + g.ToString().ToUpperInvariant() + "}";
    }

    // Ticket-Id (GUID oder IntTicketId) -> Ticket-GUID im {...}-Format. Wirft bei Nichtfund.
    static string ResolveTicketGuid(string tid)
    {
        if (string.IsNullOrEmpty(tid)) throw new Exception("id fehlt");
        Guid tg;
        string clean = tid.Replace("{", "").Replace("}", "").Trim();
        if (Guid.TryParse(clean, out tg)) return "{" + tg.ToString().ToUpperInvariant() + "}";
        string tlist = CONN.Call("GetTicketList", 1, "");
        var ms = System.Text.RegularExpressions.Regex.Matches(tlist,
            @"<TTicketInfo_V\d[^>]*>.*?<Id>([^<]+)</Id>.*?<IntTicketId>(\d+)</IntTicketId>",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        foreach (System.Text.RegularExpressions.Match im in ms)
            if (im.Groups[2].Value == tid) return im.Groups[1].Value;
        throw new Exception("Ticket IntTicketId " + tid + " nicht gefunden.");
    }
    // Zielclient-Referenz fuers ICQL: akzeptiert ClientId-GUID ODER Computername (Name -> GUID via DB, falls aktiv).
    static string ClientRef(string client)
    {
        if (string.IsNullOrEmpty(client)) throw new Exception("client fehlt");
        string c = client.Trim();
        Guid g;
        if (Guid.TryParse(c.Trim('{', '}'), out g)) return "{" + g.ToString().ToUpperInvariant() + "}";
        if (DB == null || !DB.Enabled)
            throw new Exception("Computername-Aufloesung benoetigt die DB-Anbindung; gib stattdessen die ClientId-GUID an.");
        string resolved = DB.ResolveClient(c);
        if (resolved == null) throw new Exception("Client nicht gefunden: " + client);
        return "{" + resolved.ToUpperInvariant() + "}";
    }

    // Liest ein Bool-Argument (akzeptiert echtes JSON-Bool oder String "true"/"false").
    static bool BoolArg(JObject a, string key, bool def)
    {
        JToken t = a != null ? a[key] : null;
        if (t == null || t.Type == JTokenType.Null) return def;
        if (t.Type == JTokenType.Boolean) return (bool)t;
        bool b; if (bool.TryParse(t.ToString(), out b)) return b;
        return def;
    }

    // Baut die <Variables>-Liste fuer EnqueueClientCommand v2 aus einem { Name: Wert }-Objekt.
    static string VarsXml(JObject vars)
    {
        if (vars == null || vars.Count == 0) return "";
        var sb = new StringBuilder("<Variables>");
        foreach (var kv in vars)
        {
            string val = (kv.Value != null && kv.Value.Type != JTokenType.Null) ? kv.Value.ToString() : "";
            sb.Append("<TClientCommandVariable_V1><Name>").Append(Xml(kv.Key))
              .Append("</Name><Value>").Append(Xml(val)).Append("</Value></TClientCommandVariable_V1>");
        }
        sb.Append("</Variables>");
        return sb.ToString();
    }

    // ---------- JSON-RPC Helfer ----------
    static JObject JsonRpcResult(JToken id, JToken result) { var o = new JObject(); o["jsonrpc"] = "2.0"; o["id"] = id; o["result"] = result; return o; }
    static JObject JsonRpcError(JToken id, int code, string message) { var e = new JObject(); e["code"] = code; e["message"] = message; var o = new JObject(); o["jsonrpc"] = "2.0"; o["id"] = id != null ? id : JValue.CreateNull(); o["error"] = e; return o; }

    static string Truncate(string s, int n) { if (s == null) return ""; return s.Length <= n ? s : s.Substring(0, n) + "..."; }
    static readonly object _logLock = new object();
    static void Log(string m)
    {
        string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + m;
        Console.WriteLine(line);
        if (LogFile != null) { try { lock (_logLock) { File.AppendAllText(LogFile, line + Environment.NewLine); } } catch { } }
    }
}
