// AcmpConn: thread-sichere Verbindung zur ACMP Public API (SICS) ueber Aagons Client-DLLs.
// Kapselt die im Spike bewiesene Pipeline: WSS, CA-Pinning, Auth, ACMP-Routing-Key, ICQL.
// Auth-Modi: "sics" (AESB-User, Standard), "acmpdomain" (obsolet), "acmp" (ACMP-User+Passwort, optional Token minten),
//            "acmptoken" (ACMP-User per gespeichertem Token, ohne Passwort).
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Threading;
using System.Security.Cryptography.X509Certificates;

class AcmpConn
{
    readonly string _sicsDir, _host, _user, _pass;
    readonly string _authType, _domain, _acmpServerName, _acmpServerId;
    readonly object _lock = new object();
    readonly HashSet<string> _allow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    string _token;        // fuer authType "acmptoken": Reconnect ohne Passwort
    bool _createToken;    // fuer authType "acmp": Token beim Connect minten lassen
    string _lastToken;    // Token aus dem letzten Open() (OpenClientResult.AuthenticationProtocolProperties)

    public string LastToken { get { return _lastToken; } }
    public string AuthUser { get { return _user; } }
    public void SetToken(string token) { _token = token; }
    public void SetCreateToken(bool v) { _createToken = v; }
    public string CurrentSessionId { get { lock (_lock) { try { return _client != null ? _client.SessionId : null; } catch { return null; } } } }

    Aagon.Sis.Sics.Clients.SimpleClients.SicsClient _client;
    Aagon.Logging.ILogger _logger;
    public static string DefaultVirtualRouter = "VCMN";   // per Config sics.virtualRouter ueberschreibbar
    public static int DefaultWssPort = 3950;              // per Config sics.wssPort ueberschreibbar
    string _vr = DefaultVirtualRouter;
    string _acmpRoutingKey;

    // authType: "sics" (SICS-/AESB-User, Standard), "acmpdomain" (AD-/Domain-User, obsolet),
    //           "acmp" (lokaler ACMP-User + Passwort), "acmptoken" (lokaler ACMP-User + Token)
    public AcmpConn(string sicsDir, string host, string user, string pass,
                    string authType, string domain, string acmpServerName, string acmpServerId)
    {
        _sicsDir = sicsDir; _host = host; _user = user; _pass = pass;
        _authType = (authType != null ? authType : "sics").ToLowerInvariant();
        _domain = domain; _acmpServerName = acmpServerName; _acmpServerId = acmpServerId;
        _logger = new Aagon.Logging.NullLogger();

        // Bekannte AESB-SICS-CA pinnen (Thumbprint aus lokaler CA-Datei)
        string caFile = @"C:\ProgramData\Aagon\AESB\Microservices Supervisor\Certificates\127.0.0.1_3950.crt";
        if (File.Exists(caFile)) { try { _allow.Add(new X509Certificate2(caFile).Thumbprint); } catch { } }

        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        ServicePointManager.ServerCertificateValidationCallback = delegate(object snd, X509Certificate cert, X509Chain chain, SslPolicyErrors errors)
        {
            if (errors == SslPolicyErrors.None) return true;
            try
            {
                if (cert != null)
                {
                    var c = new X509Certificate2(cert);
                    if (_allow.Contains(c.Thumbprint)) return true;
                    if (c.Issuer != null && c.Issuer.IndexOf("AESB SICS", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
                if (chain != null)
                    foreach (var el in chain.ChainElements)
                        if (_allow.Contains(el.Certificate.Thumbprint) ||
                            (el.Certificate.Subject != null && el.Certificate.Subject.IndexOf("AESB SICS CA", StringComparison.OrdinalIgnoreCase) >= 0))
                            return true;
            }
            catch { }
            return false;
        };
    }

    public void Connect()
    {
        lock (_lock) { ConnectLocked(); }
    }

    void ConnectLocked()
    {
        try { if (_client != null) _client.Close(); }
        catch { }

        string endpoint = "wss://" + _host + ":" + DefaultWssPort;
        string cbVr = "MCP_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        string cbName = "MCP_CBT_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        string cbKey = "MCP_CBK_" + Guid.NewGuid().ToString("N").Substring(0, 8); // bewusst != cbName

        _client = Aagon.Sis.Sics.Clients.SimpleClients.SicsClientFactory.GetRpcClient(
            endpoint, true, false, cbVr, cbName, new string[] { cbKey }, _logger, null, null);

        _client.SetTrustCertificateCallback(delegate(X509Certificate cert)
        {
            try
            {
                var c = new X509Certificate2(cert);
                return _allow.Contains(c.Thumbprint)
                    || (c.Issuer != null && c.Issuer.IndexOf("AESB SICS", StringComparison.OrdinalIgnoreCase) >= 0)
                    || (c.Subject != null && c.Subject.IndexOf("AESB SICS", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            catch { return false; }
        });

        Aagon.Sis.Sics.Clients.ICredentials creds;
        string srvName = !string.IsNullOrEmpty(_acmpServerName) ? _acmpServerName : "ACMP";
        string srvId = _acmpServerId != null ? _acmpServerId : "";
        if (_authType == "acmpdomain")
        {
            // AD-/Domain-User + Passwort. createToken=true -> Token wird beim Open geminted.
            // Hinweis: AcmpDomainCredentials ist von Aagon [Obsolete], in 6.9.3 aber funktional.
#pragma warning disable 0618
            creds = new Aagon.Sis.Sics.Clients.Credentials.AcmpDomainCredentials(
                _user, _pass, srvName, _domain, "AesbShell", _createToken, srvId);
#pragma warning restore 0618
        }
        else if (_authType == "acmpdomaintoken")
        {
            // AD-/Domain-User per gespeichertem Token (kein Passwort). [Obsolete], aber funktional.
#pragma warning disable 0618
            creds = new Aagon.Sis.Sics.Clients.Credentials.AcmpDomainTokenCredentials(
                _user, _token, srvName, _domain, "AesbShell", srvId);
#pragma warning restore 0618
        }
        else if (_authType == "acmp")
        {
            // Lokaler ACMP-User + Passwort. createToken=true -> Token wird beim Open geminted.
            creds = new Aagon.Sis.Sics.Clients.Credentials.AcmpCredentials2(
                _user, _pass, srvName, "AesbShell", _createToken, srvId);
        }
        else if (_authType == "acmptoken")
        {
            // Lokaler ACMP-User per gespeichertem Token (kein Passwort).
            creds = new Aagon.Sis.Sics.Clients.Credentials.AcmpTokenCredentials(
                _user, _token, srvName, "AesbShell", srvId);
        }
        else
        {
            creds = new Aagon.Sis.Sics.Clients.Credentials.SicsCredentials2(_user, _pass, "AesbShell", false);
        }

        var openRes = _client.Open(creds, CancellationToken.None);
        _lastToken = ExtractToken(openRes);
        _lastAuthPropsDump = DumpProps(openRes);

        _acmpRoutingKey = DiscoverAcmpRoutingKey();
        if (_acmpRoutingKey == null) throw new Exception("ACMP-Target/Routing-Key in VR '" + _vr + "' nicht gefunden.");
    }

    // Token nach createToken-Login aus OpenClientResult.AuthenticationProtocolProperties ziehen (Schluessel enthaelt "token").
    static string ExtractToken(object openResult)
    {
        try
        {
            if (openResult == null) return null;
            var p = openResult.GetType().GetProperty("AuthenticationProtocolProperties");
            var dict = p != null ? p.GetValue(openResult, null) as System.Collections.IDictionary : null;
            if (dict == null) return null;
            foreach (System.Collections.DictionaryEntry e in dict)
            {
                string k = e.Key != null ? e.Key.ToString() : "";
                if (k.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0 && e.Value != null && e.Value.ToString().Length > 0)
                    return e.Value.ToString();
            }
        }
        catch { }
        return null;
    }

    // Liefert alle AuthenticationProtocolProperties des letzten Open() als "k=v"-Liste (Debug/Setup).
    public string DumpAuthProps() { return _lastAuthPropsDump != null ? _lastAuthPropsDump : ""; }
    string _lastAuthPropsDump;

    static string DumpProps(object openResult)
    {
        try
        {
            if (openResult == null) return "(null)";
            var p = openResult.GetType().GetProperty("AuthenticationProtocolProperties");
            var dict = p != null ? p.GetValue(openResult, null) as System.Collections.IDictionary : null;
            if (dict == null) return "(keine AuthenticationProtocolProperties)";
            var sb = new System.Text.StringBuilder();
            foreach (System.Collections.DictionaryEntry e in dict)
            {
                string k = e.Key != null ? e.Key.ToString() : "";
                string v = e.Value != null ? e.Value.ToString() : "";
                // Token-Werte gekuerzt anzeigen (nicht im Klartext loggen)
                if (k.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0 && v.Length > 8) v = v.Substring(0, 4) + "…(" + v.Length + ")";
                sb.Append(k).Append("=").Append(v).Append("  ");
            }
            return sb.ToString();
        }
        catch (Exception ex) { return "(DumpProps-Fehler: " + ex.Message + ")"; }
    }

    string DiscoverAcmpRoutingKey()
    {
        var lt = _client.ListTargets(_vr);
        string[] targets = GetResult(lt);
        foreach (var tgt in targets)
        {
            var rkMsg = _client.ListTargetRoutingKeys(tgt);
            foreach (var k in GetResult(rkMsg))
            {
                // Voll qualifiziert ist z.B. "VCMN:SICS.Aagon.Components.ACMPServer.<id>" -> "VCMN:"-Prefix entfernen
                int idx = k.IndexOf(':');
                string key = idx >= 0 ? k.Substring(idx + 1) : k;
                if (key.IndexOf("ACMPServer", StringComparison.OrdinalIgnoreCase) >= 0) return key;
            }
        }
        return null;
    }

    // ACMP-Server-Id aus dem Routing-Key (…ACMPServer.<guid>) ableiten. null wenn nicht verbunden.
    public string DiscoverAcmpServerId()
    {
        lock (_lock)
        {
            if (_acmpRoutingKey == null) return null;
            int i = _acmpRoutingKey.LastIndexOf('.');
            return i >= 0 ? _acmpRoutingKey.Substring(i + 1) : null;
        }
    }

    static string[] GetResult(object ack)
    {
        if (ack == null) return new string[0];
        var p = ack.GetType().GetProperty("Result");
        if (p == null) return new string[0];
        var v = p.GetValue(ack, null) as string[];
        return v != null ? v : new string[0];
    }

    // Baut <ICQL><ACMP><cmd version><Tcmd Request_Vx xmlns:xsi=...>innerBody</...>></cmd></ACMP></ICQL>
    public string Call(string command, int version, string innerBody)
    {
        string icql =
            "<ICQL><ACMP><" + command + " version=\"" + version + "\">" +
            "<T" + command + "Request_V" + version + " xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
            (innerBody != null ? innerBody : "") +
            "</T" + command + "Request_V" + version + "></" + command + "></ACMP></ICQL>";
        return Publish(icql);
    }

    public string Publish(string icql)
    {
        lock (_lock)
        {
            try { return PublishLocked(icql); }
            catch (Exception)
            {
                // Einmal reconnect + retry
                try { ConnectLocked(); } catch { throw; }
                return PublishLocked(icql);
            }
        }
    }

    string PublishLocked(string icql)
    {
        return Aagon.Sis.Sics.Icql.SicsClientExtensions.PublishIcqlAndAwaitAnswer(
            _client, _logger, icql, _vr, _acmpRoutingKey, (Guid?)null, (string[])null, 30000, CancellationToken.None);
    }

    public void Close() { lock (_lock) { try { if (_client != null) _client.Close(); } catch { } } }
}
