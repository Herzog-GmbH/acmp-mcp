// Bootstrap/Verifikation fuer die Pro-User-Authentifizierung.
// Einmal interaktiv ausfuehren (oder ACMP_USER/ACMP_PASS als Env setzen):
//   .\acmp-token-setup.exe
// Verbindet als ECHTER ACMP-User, beweist die Attribution (GetOwnUserRights),
// mintet einen Token und legt ihn DPAPI-verschluesselt (LocalMachine) in secrets.local.json ab.
using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

class AcmpTokenSetup
{
    const string SicsDir = @"C:\Program Files (x86)\Aagon\AESB SICS\SICS";

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
        catch (Exception ex) { Console.Error.WriteLine("FATAL: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message)); return 99; }
    }

    static int Run(string[] args)
    {
        string cfgPath = "config.local.json";
        JObject cfg = JObject.Parse(File.ReadAllText(cfgPath));
        JObject sics = (JObject)cfg["sics"];
        string host = (string)sics["host"]; if (string.IsNullOrEmpty(host)) host = "127.0.0.1";
        string opUser = (string)sics["username"];
        string opPass = (string)sics["password"];
        if (sics["virtualRouter"] != null && ((string)sics["virtualRouter"]).Length > 0) AcmpConn.DefaultVirtualRouter = (string)sics["virtualRouter"];
        if (sics["wssPort"] != null) AcmpConn.DefaultWssPort = (int)sics["wssPort"];

        // 1) Als Operator verbinden, um ACMP-ServerId + Servernamen zu entdecken.
        Console.WriteLine("[1/4] Entdecke ACMP-Server (verbinde als SICS '" + opUser + "') ...");
        var disc = new AcmpConn(SicsDir, host, opUser, opPass, "sics", null, "ACMP", null);
        disc.Connect();
        string acmpServerId = disc.DiscoverAcmpServerId();
        string info = disc.Call("GetACMPServerInfo", 1, "");
        string acmpServerName = ExtractFirst(info, new[] { "ServerName", "Name", "DisplayName", "ComputerName" });
        if (string.IsNullOrEmpty(acmpServerName)) acmpServerName = "ACMP";
        disc.Close();
        Console.WriteLine("      acmpServerName=" + acmpServerName + "  acmpServerId=" + acmpServerId);

        // 2) Domain-User-Credentials erfragen .
        string user = args.Length > 0 ? args[0] : Environment.GetEnvironmentVariable("ACMP_USER");
        if (string.IsNullOrEmpty(user)) { Console.Write("Domain-Benutzername (z.B. mmustermann): "); user = Console.ReadLine(); }
        string domain = args.Length > 1 ? args[1] : Environment.GetEnvironmentVariable("ACMP_DOMAIN");
        if (domain == null) { Console.Write("Domain (Enter = lokaler ACMP-User; sonst z.B. firma.local): "); domain = Console.ReadLine(); }
        if (domain != null) domain = domain.Trim();
        string pass = Environment.GetEnvironmentVariable("ACMP_PASS");
        if (string.IsNullOrEmpty(pass)) { Console.Write("Passwort: "); pass = ReadMasked(); }
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass)) { Console.Error.WriteLine("Benutzer/Passwort fehlt."); return 1; }

        // 3) Verbinden (createToken=true). Lokaler ACMP-User -> "acmp", Domain-User -> "acmpdomain".
        bool isDomain = !string.IsNullOrEmpty(domain);
        string connAuth = isDomain ? "acmpdomain" : "acmp";
        Console.WriteLine("\n[2/4] Verbinde als " + (isDomain ? "Domain-User '" + domain + "\\" + user + "'" : "lokaler ACMP-User '" + user + "'") + " (mint Token) ...");
        var conn = new AcmpConn(SicsDir, host, user, pass, connAuth, domain, acmpServerName, acmpServerId);
        conn.SetCreateToken(true);
        try { conn.Connect(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("LOGIN FEHLGESCHLAGEN: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message));
            Console.Error.WriteLine("Pruefe: richtiger Domain-Benutzer/Passwort? Ist der Domain-User als ACMP-Bediener berechtigt? Public-API-Zugriff in den SICS-Einstellungen erlaubt?");
            return 2;
        }
        Console.WriteLine("      verbunden. SessionId=" + conn.CurrentSessionId);
        Console.WriteLine("      AuthProps: " + conn.DumpAuthProps());

        // 4) Attribution beweisen: eigene Rechte abrufen.
        Console.WriteLine("\n[3/4] Pruefe Identitaet via GetOwnUserRights ...");
        string rights = conn.Call("GetOwnUserRights", 1, "<Version>1</Version>");
        string rc = ExtractFirst(rights, new[] { "ResultCode" });
        int rightCount = System.Text.RegularExpressions.Regex.Matches(rights, "<(?:string|AcmpGuid)>").Count;
        Console.WriteLine("      ResultCode=" + rc + "  Rechte-Eintraege=" + rightCount
            + (rc == "0" ? "  => Identitaet bestaetigt (kein SICS-Admin-Bypass)" : "  => PRUEFEN: erwartet 0"));

        string token = conn.LastToken;
        conn.Close();

        // 5) Secret speichern: Token bevorzugt, sonst DPAPI-Passwort (z.B. Domain-User, deren Token leer bleibt).
        Console.WriteLine("\n[4/4] Speichere Secret ...");
        if (!string.IsNullOrEmpty(token))
        {
            SaveSecret(user, domain, isDomain ? "acmpdomaintoken" : "acmptoken", "tokenDpapi", token);
            Console.WriteLine("      OK: TOKEN fuer '" + Id(domain, user) + "' DPAPI-verschluesselt (LocalMachine) gespeichert. Kein Passwort persistent.");
        }
        else
        {
            Console.WriteLine("      Kein Token aus dem Login (bei Domain-Usern erwartet) -> speichere stattdessen das Passwort DPAPI-verschluesselt.");
            SaveSecret(user, domain, isDomain ? "acmpdomain" : "acmp", "passwordDpapi", pass);
            Console.WriteLine("      OK: PASSWORT fuer '" + Id(domain, user) + "' DPAPI-verschluesselt (LocalMachine) gespeichert (nicht im Repo).");
        }
        Console.WriteLine("\nFertig. Naechster Schritt: mcp.users in config.local.json -> acmpUser='" + user + "'"
            + (isDomain ? ", acmpDomain='" + domain + "'" : "") + " verknuepfen (Identitaet: " + Id(domain, user) + ").");
        return 0;
    }

    static string Id(string domain, string user)
    {
        return (string.IsNullOrEmpty(domain) ? user : domain + "\\" + user).ToLowerInvariant();
    }

    static void SaveSecret(string user, string domain, string authType, string secretField, string secretValue)
    {
        string path = "secrets.local.json";
        JObject root = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject();
        JObject users = root["acmpUsers"] as JObject;
        if (users == null) { users = new JObject(); root["acmpUsers"] = users; }
        byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(secretValue), null, DataProtectionScope.LocalMachine);
        var entry = new JObject();
        entry["username"] = user;
        if (!string.IsNullOrEmpty(domain)) entry["domain"] = domain;
        entry["authType"] = authType;
        entry[secretField] = Convert.ToBase64String(enc);
        users[Id(domain, user)] = entry;
        File.WriteAllText(path, root.ToString());
    }

    static string ExtractFirst(string xml, string[] tags)
    {
        if (xml == null) return null;
        foreach (var t in tags)
        {
            var m = System.Text.RegularExpressions.Regex.Match(xml, "<" + t + ">([^<]+)</" + t + ">");
            if (m.Success && m.Groups[1].Value.Trim().Length > 0) return m.Groups[1].Value.Trim();
        }
        return null;
    }

    static string ReadMasked()
    {
        var sb = new StringBuilder();
        ConsoleKeyInfo k;
        while ((k = Console.ReadKey(true)).Key != ConsoleKey.Enter)
        {
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Remove(sb.Length - 1, 1); }
            else if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }
}
