// AcmpWeb: Weboberflaeche des ACMP-MCP-Servers (gleicher Port wie der MCP, eigener Login, NICHT der MCP-Bearer-Key).
// Seiten: Uebersicht (/dashboard), Aktivitaet (/activity), Einstellungen mit Notbremse (/settings), Onboarding (/onboarding),
// Patchverlauf (/changelog), Ersteinrichtung (/setup). Seit 22.10.2026 ohne Vier-Augen-Freigabe: Schreibaktionen wirken sofort,
// stehen im Aktivitaetsprotokoll (AcmpActivity) und lassen sich mit der Notbremse sperren. Alte Freigabe-Links melden nicht mehr an.
// Anmeldung: Kurzname + Web-Token (web.users; Configs von vor dem 22.10.2026: approval.approvers) -> Sitzungs-Cookie
// (HttpOnly, SameSite=Strict, 12 Stunden, nur im Speicher - nach einem Neustart neu anmelden). Formulare mit Wirkung
// (Notbremse, Onboarding, Ersteinrichtung) nur per POST, mit dem Pruefwert der Sitzung (csrf).
// net48 / C# 5 (csc v4.0.30319): keine string-Interpolation, keine expression-bodied member, kein ?.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

class WebResult
{
    public int Code = 200;
    public string ContentType = "text/html; charset=utf-8";
    public string Body = "";
    public List<string> Headers = new List<string>();
}

class AcmpWeb
{
    public string CfgPath;
    public Func<JArray> GetAllTools;
    public Func<string, string> WriteTierOf;   // Tool -> "Write" / "Destructive", null = liest

    readonly AcmpActivity _act;
    readonly Action<string> _log;
    readonly object _cfgLock = new object();
    readonly object _sesLock = new object();
    readonly Dictionary<string, string> _users = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // Name -> Web-Token
    readonly Dictionary<string, WebSession> _sessions = new Dictionary<string, WebSession>(StringComparer.Ordinal);
    string _setupToken;   // != null => Ersteinrichtung offen (noch kein Bediener mit Web-Zugang)

    public string PublicUrl { get; private set; }
    public int UserCount { get { lock (_sesLock) { return _users.Count; } } }

    const string CookieName = "acmpmcp_sid";
    const int SessionHours = 12;

    class WebSession { public string Name; public string Csrf; public DateTime Expires; }

    // Letzte Zeile: alte Pfade der Freigabeseite (Lesezeichen, Mails), sie leiten auf die neuen Seiten um.
    static readonly string[] Paths = {
        "/dashboard", "/activity", "/settings", "/notbremse", "/onboarding", "/onboarding-create", "/changelog",
        "/login", "/logout", "/setup", "/setup-create",
        "/approvals", "/approval", "/approve", "/reject", "/history", "/settings-toggle", "/settings-bypass", "/settings-approval" };

    static readonly Regex OnbIdRe = new Regex("^[A-Za-z0-9_.-]{2,32}$");

    public static bool IsWebPath(string path) { return Array.IndexOf(Paths, path) >= 0; }

    public AcmpWeb(JObject cfg, string defaultUrl, AcmpActivity act, Action<string> log)
    {
        _act = act; _log = log;
        JObject web = cfg != null ? cfg["web"] as JObject : null;
        JObject old = cfg != null ? cfg["approval"] as JObject : null;   // Config von vor dem 22.10.2026
        string url = web != null && web["publicUrl"] != null ? (string)web["publicUrl"] : null;
        if (string.IsNullOrEmpty(url) && old != null && old["publicUrl"] != null) url = (string)old["publicUrl"];
        PublicUrl = (string.IsNullOrEmpty(url) ? defaultUrl : url).TrimEnd('/');
        AddUsers(web != null ? web["users"] as JArray : null);
        AddUsers(old != null ? old["approvers"] as JArray : null);
        if (_users.Count == 0)
        {
            _setupToken = RandomToken(24);
            _log("=== ERSTEINRICHTUNG NOETIG === Kein Bediener mit Web-Zugang. Oeffne " + PublicUrl + "/setup?token=" + _setupToken + " im Browser, um den ersten Administrator anzulegen.");
        }
    }

    void AddUsers(JArray arr)
    {
        if (arr == null) return;
        foreach (JToken t in arr)
        {
            JObject u = t as JObject; if (u == null) continue;
            string nm = u["name"] != null ? (string)u["name"] : null;
            string tk = u["token"] != null ? (string)u["token"] : null;
            if (!string.IsNullOrEmpty(nm) && !string.IsNullOrEmpty(tk) && !tk.StartsWith("HIER_") && !_users.ContainsKey(nm)) _users[nm] = tk;
        }
    }

    // ---------- Einstieg ----------
    public WebResult Handle(string method, string pathQuery, Dictionary<string, string> hdr, string body)
    {
        var res = new WebResult();
        res.Headers.Add("Cache-Control: no-store");
        res.Headers.Add("X-Frame-Options: DENY");
        res.Headers.Add("X-Content-Type-Options: nosniff");
        res.Headers.Add("Referrer-Policy: no-referrer");
        try { Route(method, pathQuery, hdr, body, res); }
        catch (Exception ex)
        {
            _log("Webseite: Fehler bei " + PathOnly(pathQuery) + ": " + ex.Message);
            res.Code = 500; res.Body = Page("<h1>Fehler</h1><p>" + Esc(ex.Message) + "</p><p><a href=\"/dashboard\">Zur Übersicht</a></p>");
        }
        return res;
    }

    void Route(string method, string pathQuery, Dictionary<string, string> hdr, string body, WebResult res)
    {
        string path = PathOnly(pathQuery);
        Dictionary<string, string> q = ParseQuery(pathQuery);
        Dictionary<string, string> f = method == "POST" ? ParsePairs(body) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Ersteinrichtung: ohne Login, nur mit dem Setup-Token aus server.log
        if (path == "/setup") { res.Body = SetupPage(Get(q, "token")); return; }
        if (path == "/setup-create")
        {
            if (method != "POST") { Redirect(res, "/setup"); return; }
            res.Body = SetupCreate(f, res); return;
        }

        if (path == "/login")
        {
            if (method == "POST")
            {
                string n = Get(f, "name"), t = Get(f, "token");
                if (Valid(n, t)) { StartSession(res, n); _log("Webseite: Anmeldung " + n); Redirect(res, SafeNext(Get(f, "next"))); }
                else { Slow(); res.Code = 403; res.Body = LoginPage("Kurzname oder Web-Token stimmt nicht.", Get(f, "next")); _log("Webseite: Anmeldung abgelehnt fuer '" + n + "'"); }
                return;
            }
            res.Body = LoginPage(null, Get(q, "next")); return;
        }

        // Alte Links mit ?approver=&token= (Lesezeichen, Freigabe-Mails) melden seit 23.10.2026 nicht mehr an: Ein Token in der Adresse landet in
        // Mails, Browserverlauf und Proxy-Logs. Der Token wird nicht geprueft, es kommt nur die Anmeldeseite.
        string la = Get(q, "approver"), lt = Get(q, "token");
        if (method == "GET" && !string.IsNullOrEmpty(la) && !string.IsNullOrEmpty(lt))
        {
            res.Code = 403; res.Body = LoginPage("Links mit Token gelten nicht mehr. Bitte mit Kurzname und Web-Token anmelden.", NewPath(path));
            _log("Webseite: alter Link mit Token abgewiesen (" + path + ")");
            return;
        }

        if (path == "/logout") { EndSession(hdr, res); Redirect(res, "/login"); return; }
        WebSession s = CurrentSession(hdr);
        if (s == null) { Redirect(res, "/login?next=" + Uri.EscapeDataString(NewPath(path))); return; }
        if (path != NewPath(path)) { Redirect(res, NewPath(path)); return; }
        if (method == "POST" && !FixedEquals(Get(f, "csrf"), s.Csrf))
        {
            res.Code = 403;
            res.Body = Notice(s, "Abgelehnt", "Das Formular gehört zu einer alten Sitzung. Bitte die Seite neu laden und noch einmal absenden.", "dashboard");
            return;
        }

        if (path == "/activity") { res.Body = ActivityPage(s, q); return; }
        if (path == "/settings") { res.Body = SettingsPage(s, null); return; }
        if (path == "/notbremse")
        {
            if (method != "POST") { Redirect(res, "/settings"); return; }
            res.Body = BrakeAction(s, f); return;
        }
        if (path == "/onboarding") { res.Body = OnboardingPage(s); return; }
        if (path == "/onboarding-create")
        {
            if (method != "POST") { Redirect(res, "/onboarding"); return; }
            res.Body = OnboardingCreate(s, f); return;
        }
        if (path == "/changelog") { res.Body = ChangelogPage(s); return; }
        res.Body = DashboardPage(s);
    }

    static string NewPath(string p)
    {
        if (p == "/approvals" || p == "/approval" || p == "/approve" || p == "/reject" || p == "/history") return "/activity";
        if (p == "/settings-toggle" || p == "/settings-bypass" || p == "/settings-approval") return "/settings";
        if (p == "/login" || p == "/logout" || p == "/setup" || p == "/setup-create") return "/dashboard";
        return p;
    }

    // Nur bekannte Seiten als Ziel nach dem Login (keine Weiterleitung auf fremde Adressen)
    static string SafeNext(string n)
    {
        if (string.IsNullOrEmpty(n)) return "/dashboard";
        string p = PathOnly(n);
        return IsWebPath(p) ? NewPath(p) : "/dashboard";
    }

    // ---------- Anmeldung und Sitzungen ----------
    bool Valid(string name, string token)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(token)) return false;
        string t;
        lock (_sesLock) { if (!_users.TryGetValue(name.Trim(), out t)) return false; }
        return FixedEquals(t, token.Trim());
    }

    // Nach einer falschen Anmeldung kurz warten (bremst Rateversuche; die Tokens selbst sind 192 Bit zufaellig)
    static void Slow() { System.Threading.Thread.Sleep(1500); }

    static bool FixedEquals(string a, string b)
    {
        if (a == null || b == null) return false;
        byte[] x = Encoding.UTF8.GetBytes(a), y = Encoding.UTF8.GetBytes(b);
        int d = x.Length ^ y.Length;
        for (int i = 0; i < x.Length && i < y.Length; i++) d |= x[i] ^ y[i];
        return d == 0;
    }

    void StartSession(WebResult res, string name)
    {
        var s = new WebSession();
        lock (_sesLock)
        {
            foreach (string k in _users.Keys) if (string.Equals(k, name.Trim(), StringComparison.OrdinalIgnoreCase)) s.Name = k;
            if (s.Name == null) s.Name = name.Trim();
            s.Csrf = RandomToken(18);
            s.Expires = DateTime.UtcNow.AddHours(SessionHours);
            var dead = new List<string>();
            foreach (KeyValuePair<string, WebSession> kv in _sessions) if (kv.Value.Expires < DateTime.UtcNow) dead.Add(kv.Key);
            foreach (string k in dead) _sessions.Remove(k);
            string sid = RandomToken(32);
            _sessions[sid] = s;
            res.Headers.Add("Set-Cookie: " + CookieName + "=" + sid + "; Path=/; HttpOnly; SameSite=Strict; Max-Age=" + (SessionHours * 3600));
        }
    }

    WebSession CurrentSession(Dictionary<string, string> hdr)
    {
        string sid = CookieValue(hdr);
        if (sid == null) return null;
        lock (_sesLock)
        {
            WebSession s;
            if (!_sessions.TryGetValue(sid, out s)) return null;
            if (s.Expires < DateTime.UtcNow || !_users.ContainsKey(s.Name)) { _sessions.Remove(sid); return null; }
            return s;
        }
    }

    void EndSession(Dictionary<string, string> hdr, WebResult res)
    {
        string sid = CookieValue(hdr);
        if (sid != null) lock (_sesLock) { _sessions.Remove(sid); }
        res.Headers.Add("Set-Cookie: " + CookieName + "=; Path=/; HttpOnly; SameSite=Strict; Max-Age=0");
    }

    static string CookieValue(Dictionary<string, string> hdr)
    {
        string c;
        if (hdr == null || !hdr.TryGetValue("Cookie", out c) || c == null) return null;
        foreach (string part in c.Split(';'))
        {
            string p = part.Trim();
            if (p.StartsWith(CookieName + "=")) { string v = p.Substring(CookieName.Length + 1); return v.Length > 0 ? v : null; }
        }
        return null;
    }

    static void Redirect(WebResult res, string url)
    {
        res.Code = 303;
        res.Headers.Add("Location: " + url);
        res.Body = "<!doctype html><html><head><meta charset=\"utf-8\"></head><body><a href=\"" + Esc(url) + "\">Weiter</a></body></html>";
    }

    // ---------- Seitenrahmen ----------
    string Render(string header, string inner)
    {
        return "<!doctype html><html lang=\"de\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
             + "<title>ACMP MCP</title>"
             + "<link rel=\"preconnect\" href=\"https://fonts.googleapis.com\"><link rel=\"preconnect\" href=\"https://fonts.gstatic.com\" crossorigin>"
             + "<link href=\"https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700;800&family=JetBrains+Mono:wght@400;600&display=swap\" rel=\"stylesheet\">"
             + "<style>"
             + "*{box-sizing:border-box}"
             + "body{font-family:'Inter',-apple-system,system-ui,'Segoe UI',Arial,sans-serif;color:#0a0a0a;margin:0;min-height:100vh;font-size:16px;line-height:1.65;-webkit-font-smoothing:antialiased;background:#ffffff;background-image:radial-gradient(1100px 480px at 85% -12%,rgba(0,69,154,.10),transparent 60%),radial-gradient(800px 420px at 8% 4%,rgba(0,102,204,.07),transparent 60%)}"
             + ".wrap{position:relative;z-index:1;max-width:1180px;margin:0 auto;padding:0 20px 80px}"
             + ".brand{font-size:12px;letter-spacing:.14em;color:#00459a;text-transform:uppercase;font-weight:600;margin:30px 0 16px}"
             + "h1{font-size:28px;font-weight:700;letter-spacing:-.01em;color:#0a0a0a;margin:0 0 10px}"
             + "h2{font-size:13px;font-weight:700;letter-spacing:.08em;text-transform:uppercase;color:#00459a;margin:28px 0 10px}"
             + "p{line-height:1.65;color:#1c2430}b{color:#0a0a0a;font-weight:600}small,.muted{color:#7a838c}"
             + "a{color:#00459a;text-decoration:none;font-weight:500}a:hover{text-decoration:underline}"
             + "code{font-family:'JetBrains Mono',Consolas,monospace;font-size:.88em;background:#f1f4f8;border-radius:5px;padding:1px 5px}"
             + ".nav{position:sticky;top:0;z-index:20;display:flex;align-items:center;gap:4px;background:rgba(255,255,255,.86);backdrop-filter:blur(12px);-webkit-backdrop-filter:blur(12px);border-bottom:1px solid rgba(10,15,35,.1);padding:0 20px;margin:0 -20px 26px;min-height:62px;flex-wrap:wrap}"
             + ".nav .logo{font-weight:800;color:#00459a;letter-spacing:-.01em;font-size:16px;margin-right:18px;white-space:nowrap}.nav .logo span{color:#0a0a0a}"
             + ".navlink{padding:8px 13px;border-radius:9px;color:#545e66;font-weight:600;font-size:14px;display:inline-flex;align-items:center;gap:7px;white-space:nowrap}"
             + ".navlink:hover{background:#eef2f7;text-decoration:none;color:#0a0a0a}.navlink.active{background:#00459a;color:#fff}"
             + ".nav .spacer{flex:1}.nav .who{font-size:13px;color:#7a838c;margin-right:4px}.nav .who b{color:#0a0a0a}"
             + ".brakebadge{display:inline-flex;align-items:center;height:24px;padding:0 10px;border-radius:12px;background:#dc2626;color:#fff;font-size:12px;font-weight:800;letter-spacing:.04em}"
             + ".cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(190px,1fr));gap:16px;margin:18px 0 10px}"
             + ".card{background:#fff;border:1px solid rgba(10,15,35,.1);border-radius:14px;padding:18px 20px;box-shadow:0 10px 30px rgba(10,15,35,.05);text-decoration:none;display:block;transition:transform .15s,box-shadow .15s}"
             + "a.card:hover{transform:translateY(-2px);box-shadow:0 14px 36px rgba(10,15,35,.1);text-decoration:none}"
             + ".card .k{font-size:12px;letter-spacing:.06em;text-transform:uppercase;color:#7a838c;font-weight:700;margin:0 0 8px}"
             + ".card .v{font-size:30px;font-weight:800;color:#0a0a0a;letter-spacing:-.02em;line-height:1.05}"
             + ".card.compact .v{font-size:20px;letter-spacing:-.01em;white-space:nowrap}"
             + ".card .s{font-size:13px;color:#7a838c;margin-top:5px}.card.alert{border-color:#f3b4b4;background:#fff7f7}.card.alert .v{color:#dc2626}.card.ok .v{color:#15803d}.card.warnc .v{color:#b45309}"
             + ".banner{border-radius:12px;padding:14px 18px;margin:0 0 20px;font-weight:600;border:1px solid}.banner.brake{background:#fde8e8;border-color:#f3b4b4;color:#9b1c1c}"
             + ".callout{border-radius:10px;padding:12px 16px;background:#eef6ff;border:1px solid #cfe0f5;margin:12px 0}.callout.ok{background:#ecfdf3;border-color:#bbf7d0}.callout.warn{background:#fff8ec;border-color:#f5d6a3}"
             + ".tablewrap{overflow-x:auto;border-radius:12px}"
             + "table{border-collapse:separate;border-spacing:0;width:100%;background:#fff;border:1px solid rgba(10,15,35,.10);border-radius:12px;overflow:hidden;box-shadow:0 10px 30px rgba(10,15,35,.05)}"
             + "td,th{border-bottom:1px solid rgba(10,15,35,.08);padding:10px 13px;text-align:left;font-size:14px;vertical-align:top}"
             + "tr:last-child td{border-bottom:none}"
             + "th{background:#f6f8fb;color:#545e66;font-weight:700;letter-spacing:.04em;text-transform:uppercase;font-size:12px;white-space:nowrap}"
             + "tr:hover td{background:#f9fafc}"
             + "td.nowrap{white-space:nowrap}"
             + "pre{background:#f6f8fb;border:1px solid rgba(10,15,35,.10);border-left:3px solid #00459a;padding:11px 13px;border-radius:8px;overflow:auto;font-size:12.5px;color:#1c2430;white-space:pre-wrap;word-break:break-word;font-family:'JetBrains Mono',Consolas,'Courier New',monospace;line-height:1.5;margin:8px 0 0}"
             + "details summary{cursor:pointer;color:#00459a;font-weight:600;font-size:13px}"
             + ".pill{display:inline-block;padding:1px 9px;border-radius:999px;font-size:11.5px;font-weight:700;white-space:nowrap}"
             + ".pill-ok{background:#e7f6ec;color:#15803d}.pill-err{background:#fde8e8;color:#c81e1e}.pill-write{background:#eaf1fb;color:#00459a}.pill-crit{background:#fff1e6;color:#b45309}.pill-web{background:#f1f4f8;color:#545e66}"
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
             + "ol.steps{padding-left:20px}ol.steps li{margin:7px 0;font-size:14px;line-height:1.6;color:#1c2430}"
             + "input,select,textarea{background:#fff;border:1px solid rgba(10,15,35,.22);color:#0a0a0a;padding:9px 12px;border-radius:8px;font-family:inherit;font-size:15px;max-width:100%}"
             + "input[type=text],input[type=password],input:not([type]){width:320px}input[type=checkbox]{width:auto}"
             + "input:focus,select:focus,textarea:focus{outline:none;border-color:#00459a;box-shadow:0 0 0 3px rgba(0,69,154,.15)}"
             + ".filters{display:flex;gap:10px;flex-wrap:wrap;align-items:center;margin:8px 0 16px}.filters input{width:220px}"
             + ".actions{display:flex;gap:14px;flex-wrap:wrap;margin:22px 0;align-items:center}"
             + ".btn{display:inline-flex;align-items:center;justify-content:center;gap:8px;padding:11px 22px;border:1px solid transparent;cursor:pointer;border-radius:10px;text-decoration:none;font-family:inherit;font-weight:600;font-size:.95rem;color:#fff;transition:transform .15s,box-shadow .15s,background .15s}"
             + ".btn-primary{background:#00459a;box-shadow:0 4px 16px rgba(0,69,154,.25)}.btn-primary:hover{background:#003a82;text-decoration:none}"
             + ".btn-ok{background:#16a34a;box-shadow:0 4px 16px rgba(22,163,74,.28)}.btn-ok:hover{background:#15803d;text-decoration:none}"
             + ".btn-stop{background:#dc2626;box-shadow:0 4px 16px rgba(220,38,38,.26)}.btn-stop:hover{background:#b91c1c;text-decoration:none}"
             + ".btn-ghost{background:#f6f8fb;color:#0a0a0a;border:1px solid rgba(10,15,35,.15);box-shadow:none}.btn-ghost:hover{background:#eef2f7;text-decoration:none}"
             + ".foot{margin-top:54px;padding-top:18px;border-top:1px solid rgba(10,15,35,.08);color:#9aa3ab;font-size:12.5px;display:flex;gap:14px;flex-wrap:wrap;align-items:center}.foot b{color:#7a838c}"
             + "@media(max-width:680px){.nav .who{display:none}.navlink{padding:8px 10px}input[type=text],input[type=password],input:not([type]){width:100%}}"
             + "</style></head><body><div class=\"wrap\">" + header + inner + Footer() + "</div></body></html>";
    }

    string Page(string inner) { return Render("<div class=\"brand\">ACMP &middot; MCP-Server</div>", inner); }

    string PageNav(WebSession s, string inner, string active) { return Render(NavBar(s, active), inner); }

    string NavBar(WebSession s, string active)
    {
        var sb = new StringBuilder();
        sb.Append("<div class=\"nav\">");
        sb.Append("<a class=\"logo\" href=\"/dashboard\" style=\"text-decoration:none\">ACMP <span>MCP</span></a>");
        sb.Append(NavLink("/dashboard", "Übersicht", "dashboard", active));
        sb.Append(NavLink("/activity", "Aktivität", "activity", active));
        sb.Append(NavLink("/settings", "Einstellungen", "settings", active));
        sb.Append(NavLink("/onboarding", "Onboarding", "onboarding", active));
        sb.Append(NavLink("/changelog", "Patchverlauf", "changelog", active));
        sb.Append("<div class=\"spacer\"></div>");
        if (_act.BrakeActive) sb.Append("<a href=\"/settings\" class=\"brakebadge\" style=\"text-decoration:none\">NOTBREMSE</a>");
        sb.Append("<div class=\"who\">Angemeldet: <b>").Append(Esc(s.Name)).Append("</b></div>");
        sb.Append("<a class=\"navlink\" href=\"/logout\">Abmelden &rsaquo;</a>");
        sb.Append("</div>");
        return sb.ToString();
    }

    static string NavLink(string href, string label, string key, string active)
    {
        return "<a class=\"" + (key == active ? "navlink active" : "navlink") + "\" href=\"" + href + "\">" + Esc(label) + "</a>";
    }

    string Footer()
    {
        return "<div class=\"foot\"><b>ACMP MCP</b> &middot; Weboberfläche<span style=\"flex:1\"></span>"
             + "<span>Schreibaktionen über den MCP wirken sofort und stehen im Aktivitätsprotokoll. Die Notbremse sperrt sie alle.</span></div>";
    }

    string Notice(WebSession s, string title, string msg, string active)
    {
        return PageNav(s, "<h1>" + Esc(title) + "</h1><p>" + Esc(msg) + "</p><p><a href=\"/dashboard\">&larr; Zur Übersicht</a></p>", active);
    }

    static string Card(string href, string cls, string k, string v, string s)
    {
        string c = string.IsNullOrEmpty(cls) ? "card" : "card " + cls;
        string head = href != null ? "<a class=\"" + c + "\" href=\"" + href + "\">" : "<div class=\"" + c + "\">";
        return head + "<div class=\"k\">" + Esc(k) + "</div><div class=\"v\">" + Esc(v) + "</div><div class=\"s\">" + Esc(s) + "</div>" + (href != null ? "</a>" : "</div>");
    }

    string CsrfField(WebSession s) { return "<input type=\"hidden\" name=\"csrf\" value=\"" + Esc(s.Csrf) + "\">"; }

    string BrakeBanner()
    {
        JObject b = _act.BrakeInfo();
        if (b == null) return "";
        return "<div class=\"banner brake\">Notbremse gezogen seit " + Esc(AcmpActivity.LocalTime((string)b["utc"])) + " von " + Esc((string)b["by"])
             + (b["reason"] != null && ((string)b["reason"]).Length > 0 ? " – „" + Esc((string)b["reason"]) + "“" : "")
             + ". Alle Schreib-, Ausführ- und Lösch-Tools des MCP sind gesperrt. <a href=\"/settings\">Lösen &rarr;</a></div>";
    }

    // ---------- Login ----------
    string LoginPage(string error, string next)
    {
        var sb = new StringBuilder();
        sb.Append("<h1>Anmelden</h1>");
        if (_setupToken != null)
            sb.Append("<div class=\"callout warn\">Noch kein Bediener mit Web-Zugang – den Link zur Ersteinrichtung schreibt der Server beim Start in <code>server.log</code> (Zeile <code>ERSTEINRICHTUNG NOETIG</code>).</div>");
        if (error != null) sb.Append("<div class=\"callout warn\">").Append(Esc(error)).Append("</div>");
        sb.Append("<p>Mit Kurzname und Web-Token anmelden (früher „Freigabe-Token“).</p>");
        sb.Append("<form method=\"post\" action=\"/login\">");
        if (!string.IsNullOrEmpty(next)) sb.Append("<input type=\"hidden\" name=\"next\" value=\"").Append(Esc(next)).Append("\">");
        sb.Append("<p>Kurzname:<br><input type=\"text\" name=\"name\" autocomplete=\"username\" required></p>");
        sb.Append("<p>Web-Token:<br><input type=\"password\" name=\"token\" autocomplete=\"current-password\" required></p>");
        sb.Append("<p class=\"actions\"><button class=\"btn btn-primary\" type=\"submit\">Anmelden</button></p></form>");
        return Page(sb.ToString());
    }

    // ---------- Uebersicht ----------
    string DashboardPage(WebSession s)
    {
        List<JObject> recent = _act.Recent(3000);
        string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string weekStart = DateTime.Now.AddDays(-6).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        int nToday = 0, errToday = 0, nWeek = 0, errWeek = 0;
        var usersToday = new List<string>();
        foreach (JObject e in recent)
        {
            if ((string)e["kind"] == "Webseite") continue;
            string z = e["zeit"] != null ? (string)e["zeit"] : "";
            bool ok = (e["ok"] != null && (bool)e["ok"]) || (e["pending"] != null && (bool)e["pending"]);
            if (string.CompareOrdinal(z, weekStart) >= 0) { nWeek++; if (!ok) errWeek++; }
            if (z.StartsWith(today))
            {
                nToday++; if (!ok) errToday++;
                string u = (string)e["user"]; if (u != null && !usersToday.Contains(u)) usersToday.Add(u);
            }
        }
        int toolCount = 0, writeTools = 0;
        JArray all = GetAllTools != null ? GetAllTools() : null;
        if (all != null) foreach (JToken t in all) { toolCount++; if (WriteTierOf != null && WriteTierOf((string)t["name"]) != null) writeTools++; }
        JObject brake = _act.BrakeInfo();

        var sb = new StringBuilder();
        sb.Append(BrakeBanner());
        sb.Append("<h1>Übersicht</h1><p>Willkommen, <b>").Append(Esc(s.Name)).Append("</b>. Was über den ACMP-MCP-Server geändert wurde, auf einen Blick.</p>");
        sb.Append("<div class=\"cards\">");
        sb.Append(Card("/settings", brake != null ? "alert" : "ok", "Notbremse", brake != null ? "GEZOGEN" : "aus",
            brake != null ? "Schreib-Tools gesperrt" : "Schreib-Tools frei"));
        sb.Append(Card("/activity", null, "Schreibaktionen heute", nToday.ToString(), errToday > 0 ? errToday + " mit Fehler" : "ohne Fehler"));
        sb.Append(Card("/activity", errWeek > 0 ? "warnc" : null, "Letzte 7 Tage", nWeek.ToString(), errWeek > 0 ? errWeek + " mit Fehler" : "ohne Fehler"));
        sb.Append(Card("/activity", "compact", "Bediener heute", usersToday.Count > 0 ? usersToday.Count.ToString() : "–", usersToday.Count > 0 ? string.Join(", ", usersToday.ToArray()) : "noch niemand"));
        sb.Append(Card("/settings", null, "Tools", toolCount.ToString(), writeTools + " davon schreiben"));
        JArray cl = LoadChangelog();
        if (cl.Count > 0)
        {
            JObject last = cl[0] as JObject;
            sb.Append(Card("/changelog", "compact", "Letztes Update", last != null && last["date"] != null ? (string)last["date"] : "–", last != null && last["title"] != null ? (string)last["title"] : ""));
        }
        sb.Append("</div>");

        sb.Append("<h2>Letzte Aktivität</h2>");
        sb.Append(ActivityTable(recent, 12, false));
        sb.Append("<p><a href=\"/activity\">Gesamtes Aktivitätsprotokoll &rarr;</a></p>");
        return PageNav(s, sb.ToString(), "dashboard");
    }

    // ---------- Aktivitaet ----------
    string ActivityPage(WebSession s, Dictionary<string, string> q)
    {
        string fUser = Get(q, "user"), fTool = Get(q, "tool"), fN = Get(q, "n");
        bool fErr = Get(q, "errors") == "1";
        int n = 200; int pn; if (int.TryParse(fN, out pn) && pn > 0) n = Math.Min(pn, 5000);
        List<JObject> all = _act.Recent(5000);
        var users = new List<string>();
        foreach (JObject e in all) { string u = (string)e["user"]; if (u != null && !users.Contains(u)) users.Add(u); }
        users.Sort(StringComparer.OrdinalIgnoreCase);
        var shown = new List<JObject>();
        foreach (JObject e in all)
        {
            if (!string.IsNullOrEmpty(fUser) && !string.Equals((string)e["user"], fUser, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(fTool) && ((string)e["tool"] ?? "").IndexOf(fTool, StringComparison.OrdinalIgnoreCase) < 0
                && ((string)e["target"] ?? "").IndexOf(fTool, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (fErr && e["ok"] != null && (bool)e["ok"]) continue;
            shown.Add(e);
            if (shown.Count >= n) break;
        }

        var sb = new StringBuilder();
        sb.Append(BrakeBanner());
        sb.Append("<h1>Aktivität</h1><p>Jede Schreib-, Ausführ- und Löschaktion über den MCP, neueste zuerst (Probeläufe mit <code>dryRun</code> fehlen, sie ändern nichts). Gespeichert in <code>activity-JJJJ-MM.jsonl</code> im Server-Ordner.</p>");
        sb.Append("<form method=\"get\" action=\"/activity\" class=\"filters\">");
        sb.Append("<select name=\"user\"><option value=\"\">Alle Bediener</option>");
        foreach (string u in users) sb.Append("<option").Append(string.Equals(u, fUser, StringComparison.OrdinalIgnoreCase) ? " selected" : "").Append(">").Append(Esc(u)).Append("</option>");
        sb.Append("</select>");
        sb.Append("<input type=\"text\" name=\"tool\" placeholder=\"Tool oder Ziel enthält …\" value=\"").Append(Esc(fTool)).Append("\">");
        sb.Append("<label><input type=\"checkbox\" name=\"errors\" value=\"1\"").Append(fErr ? " checked" : "").Append("> nur Fehler</label>");
        sb.Append("<select name=\"n\">");
        foreach (int c in new int[] { 50, 200, 1000, 5000 }) sb.Append("<option").Append(c == n ? " selected" : "").Append(">").Append(c).Append("</option>");
        sb.Append("</select><button class=\"btn btn-ghost\" type=\"submit\">Filtern</button></form>");
        sb.Append("<p class=\"muted\">").Append(shown.Count).Append(" Einträge</p>");
        sb.Append(ActivityTable(shown, n, true));
        return PageNav(s, sb.ToString(), "activity");
    }

    static string ActivityTable(List<JObject> list, int max, bool withDetails)
    {
        var sb = new StringBuilder();
        sb.Append("<div class=\"tablewrap\"><table><tr><th>Zeit</th><th>Bediener</th><th>Tool</th><th>Ziel</th><th>Absicht</th><th>Ergebnis</th></tr>");
        int shown = 0;
        foreach (JObject e in list)
        {
            if (shown >= max) break;
            shown++;
            bool ok = e["ok"] != null && (bool)e["ok"];
            bool pending = e["pending"] != null && (bool)e["pending"];
            string kind = e["kind"] != null ? (string)e["kind"] : "";
            string kcls = kind == "Destructive" ? "pill pill-crit" : (kind == "Webseite" ? "pill pill-web" : "pill pill-write");
            string klbl = kind == "Destructive" ? "kritisch" : (kind == "Webseite" ? "Webseite" : "schreibt");
            sb.Append("<tr><td class=\"nowrap\">").Append(Esc(ZeitKurz((string)e["zeit"]))).Append("</td>");
            sb.Append("<td>").Append(Esc((string)e["user"])).Append("</td>");
            sb.Append("<td><b>").Append(Esc((string)e["tool"])).Append("</b><br><span class=\"").Append(kcls).Append("\">").Append(klbl).Append("</span></td>");
            sb.Append("<td>").Append(Esc((string)e["target"])).Append("</td>");
            sb.Append("<td>").Append(Esc((string)e["intent"])).Append("</td>");
            sb.Append("<td><span class=\"pill ").Append(pending ? "pill-crit\">offen" : (ok ? "pill-ok\">ok" : "pill-err\">Fehler")).Append("</span>");
            if (e["ms"] != null && (long)e["ms"] > 0) sb.Append(" <small>").Append(((long)e["ms"]).ToString(CultureInfo.InvariantCulture)).Append(" ms</small>");
            string res = (string)e["result"], det = (string)e["details"];
            if (withDetails && (!string.IsNullOrEmpty(res) || !string.IsNullOrEmpty(det)))
            {
                sb.Append("<details><summary>Details</summary>");
                if (!string.IsNullOrEmpty(det)) sb.Append("<pre>").Append(Esc(PrettyJson(det))).Append("</pre>");
                if (!string.IsNullOrEmpty(res)) sb.Append("<pre>").Append(Esc(res)).Append("</pre>");
                sb.Append("</details>");
            }
            else if (!ok && !string.IsNullOrEmpty(res)) sb.Append("<br><small>").Append(Esc(res.Length > 160 ? res.Substring(0, 160) + "…" : res)).Append("</small>");
            sb.Append("</td></tr>");
        }
        if (shown == 0) sb.Append("<tr><td colspan=\"6\">Noch keine Einträge.</td></tr>");
        sb.Append("</table></div>");
        return sb.ToString();
    }

    static string ZeitKurz(string z)
    {
        DateTime d;
        if (z != null && DateTime.TryParseExact(z, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
            return d.ToString("dd.MM. HH:mm:ss", CultureInfo.InvariantCulture);
        return z != null ? z : "";
    }

    static string PrettyJson(string s)
    {
        try { return JToken.Parse(s).ToString(Formatting.Indented); } catch { return s; }
    }

    // ---------- Einstellungen und Notbremse ----------
    string SettingsPage(WebSession s, string message)
    {
        JObject b = _act.BrakeInfo();
        var sb = new StringBuilder();
        sb.Append("<h1>Einstellungen</h1>");
        if (message != null) sb.Append("<div class=\"callout ok\">").Append(Esc(message)).Append("</div>");

        sb.Append("<h2>Notbremse</h2>");
        if (b != null)
        {
            sb.Append(BrakeBanner());
            sb.Append("<p>Solange die Notbremse gezogen ist, lehnt der Server jede Schreib-, Ausführ- und Löschaktion des MCP ab (Lesen und <code>dryRun</code> gehen weiter). Lösen kann nur ein Mensch hier auf der Seite.</p>");
            sb.Append("<form method=\"post\" action=\"/notbremse\">").Append(CsrfField(s)).Append("<input type=\"hidden\" name=\"on\" value=\"0\">");
            sb.Append("<p>Bemerkung (optional):<br><input type=\"text\" name=\"reason\" maxlength=\"300\" style=\"width:520px\"></p>");
            sb.Append("<p class=\"actions\"><button class=\"btn btn-ok\" type=\"submit\">Notbremse lösen</button></p></form>");
        }
        else
        {
            sb.Append("<p>Die Notbremse sperrt sofort alle Schreib-, Ausführ- und Lösch-Tools des MCP für alle Bediener – zum Beispiel, wenn eine KI-Sitzung etwas Unerwartetes tut. Lesen und <code>dryRun</code> gehen weiter. Gezogen und gelöst wird sie nur hier, nie über das LLM.</p>");
            sb.Append("<form method=\"post\" action=\"/notbremse\">").Append(CsrfField(s)).Append("<input type=\"hidden\" name=\"on\" value=\"1\">");
            sb.Append("<p>Grund (erscheint im Protokoll und in der Meldung an die KI):<br><input type=\"text\" name=\"reason\" maxlength=\"300\" style=\"width:520px\"></p>");
            sb.Append("<p class=\"actions\"><button class=\"btn btn-stop\" type=\"submit\">Notbremse ziehen</button></p></form>");
        }

        sb.Append("<h2>Bediener mit Web-Zugang</h2><p>");
        var names = new List<string>();
        lock (_sesLock) { names.AddRange(_users.Keys); }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (string nm in names) sb.Append("<span class=\"chip\">").Append(Esc(nm)).Append("</span> ");
        sb.Append("</p><p class=\"muted\">Neue Bediener über <a href=\"/onboarding\">Onboarding</a>. Wer die optionalen Schreib-Tools (Client Commands, Kiosk, …) nutzen darf, steht in <code>ccWriter.allowedRequesters</code>.</p>");

        JArray all = GetAllTools != null ? GetAllTools() : null;
        var rows = new List<JObject>();
        if (all != null) foreach (JToken t in all) { JObject to = t as JObject; if (to != null && WriteTierOf != null && WriteTierOf((string)to["name"]) != null) rows.Add(to); }
        rows.Sort(delegate(JObject x, JObject y) { return string.Compare((string)x["name"], (string)y["name"], StringComparison.OrdinalIgnoreCase); });
        sb.Append("<h2>Tools, die etwas ändern (").Append(rows.Count).Append(")</h2>");
        sb.Append("<p>Diese Tools wirken sofort, stehen im Aktivitätsprotokoll und sind bei gezogener Notbremse gesperrt. Mit <code>dryRun=true</code> zeigen die meisten vorher eine Vorschau. <code>acmp_call</code> zählt nur bei schreibenden Kommandos dazu.</p>");
        sb.Append("<div class=\"tablewrap\"><table><tr><th>Tool</th><th>Art</th><th>Beschreibung</th></tr>");
        foreach (JObject t in rows)
        {
            string tier = WriteTierOf((string)t["name"]);
            string desc = t["description"] != null ? (string)t["description"] : "";
            if (desc.Length > 150) desc = desc.Substring(0, 147) + "...";
            sb.Append("<tr><td><b>").Append(Esc((string)t["name"])).Append("</b></td><td><span class=\"").Append(tier == "Destructive" ? "pill pill-crit\">kritisch" : "pill pill-write\">schreibt").Append("</span></td><td><small>").Append(Esc(desc)).Append("</small></td></tr>");
        }
        sb.Append("</table></div>");
        return PageNav(s, sb.ToString(), "settings");
    }

    string BrakeAction(WebSession s, Dictionary<string, string> f)
    {
        bool on = Get(f, "on") == "1";
        string reason = Get(f, "reason");
        if (reason != null) { reason = reason.Trim(); if (reason.Length > 300) reason = reason.Substring(0, 300); }
        try { _act.SetBrake(on, s.Name, reason); }
        catch (Exception ex) { return SettingsPage(s, "Konnte die Notbremse nicht speichern (notbremse.json): " + ex.Message + " – nichts geändert."); }
        return SettingsPage(s, on ? "Notbremse gezogen. Alle Schreib-Tools des MCP sind ab sofort gesperrt." : "Notbremse gelöst. Die Schreib-Tools laufen wieder.");
    }

    // ---------- Patchverlauf (changelog.json im Server-Ordner, neueste zuerst) ----------
    JArray LoadChangelog()
    {
        try
        {
            if (CfgPath == null) return new JArray();
            string p = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(CfgPath)), "changelog.json");
            if (!File.Exists(p)) return new JArray();
            return JArray.Parse(File.ReadAllText(p, Encoding.UTF8));
        }
        catch { return new JArray(); }
    }

    string ChangelogPage(WebSession s)
    {
        JArray cl = LoadChangelog();
        var sb = new StringBuilder();
        sb.Append("<h1>Patchverlauf</h1><p>Änderungen am ACMP-MCP-Server – neue Tools, Features und Fixes. Gepflegt in <code>changelog.json</code>.</p>");
        if (cl.Count == 0)
        {
            sb.Append("<p>Noch keine Einträge. Lege <code>changelog.json</code> im Server-Ordner an (Liste von Objekten mit <code>date</code>, <code>title</code>, optional <code>tags</code>, <code>items</code>, <code>tools</code>).</p>");
            return PageNav(s, sb.ToString(), "changelog");
        }
        sb.Append("<div class=\"timeline\">");
        foreach (JToken tok in cl)
        {
            JObject e = tok as JObject; if (e == null) continue;
            sb.Append("<div class=\"tl-item\"><div class=\"tl-dot\"></div><div class=\"tl-body\">");
            sb.Append("<div class=\"tl-head\"><span class=\"tl-date\">").Append(Esc(e["date"] != null ? (string)e["date"] : "")).Append("</span><span class=\"tl-title\">").Append(Esc(e["title"] != null ? (string)e["title"] : "(ohne Titel)")).Append("</span>");
            JArray tags = e["tags"] as JArray;
            if (tags != null) foreach (JToken tg in tags)
            {
                string tn = (string)tg;
                string tcls = tn != null && tn.Equals("Fix", StringComparison.OrdinalIgnoreCase) ? "tag fix" : (tn != null && tn.Equals("UI", StringComparison.OrdinalIgnoreCase) ? "tag ui" : "tag");
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
        return PageNav(s, sb.ToString(), "changelog");
    }

    // ---------- Onboarding: neue Bediener (MCP-API-Key, ACMP-Benutzer, optional Web-Zugang) ----------
    JObject ReadCfgSafe()
    {
        try { lock (_cfgLock) { return JObject.Parse(File.ReadAllText(CfgPath, Encoding.UTF8)); } }
        catch (Exception ex) { _log("Onboarding: config.local.json nicht lesbar: " + ex.Message); return new JObject(); }
    }

    static string DefaultRequester(JObject cfg)
    {
        JObject mcp = cfg["mcp"] as JObject;
        if (mcp != null && mcp["defaultRequester"] != null && ((string)mcp["defaultRequester"]).Length > 0) return (string)mcp["defaultRequester"];
        JObject old = cfg["approval"] as JObject;
        return old != null && old["requester"] != null ? (string)old["requester"] : null;
    }

    string OnboardingPage(WebSession s)
    {
        JObject cfg = ReadCfgSafe();
        JObject mcp = cfg["mcp"] as JObject;
        var sb = new StringBuilder();
        sb.Append("<h1>Onboarding</h1><p>Neuen IT-Mitarbeiter für den ACMP-MCP-Server einrichten: eigener MCP-API-Key, Verknüpfung mit seinem ACMP-Benutzer, optional Zugang zu dieser Weboberfläche.</p>");

        sb.Append("<h2>Bestehende Bediener</h2><div class=\"tablewrap\"><table><tr><th>Name</th><th>ACMP-User</th><th>Web-Zugang</th></tr>");
        string defaultReq = DefaultRequester(cfg);
        string defaultAcmpUser = mcp != null && mcp["defaultAcmpUser"] != null ? (string)mcp["defaultAcmpUser"] : null;
        if (!string.IsNullOrEmpty(defaultReq))
            sb.Append("<tr><td>").Append(Esc(defaultReq)).Append(" <small>(Standard-Key)</small></td><td>").Append(Esc(string.IsNullOrEmpty(defaultAcmpUser) ? "-" : defaultAcmpUser)).Append("</td><td>").Append(HasWeb(defaultReq) ? "ja" : "-").Append("</td></tr>");
        JArray usersArr = mcp != null ? mcp["users"] as JArray : null;
        if (usersArr != null)
            foreach (JToken ut in usersArr)
            {
                JObject u = ut as JObject; if (u == null) continue;
                string nm = u["name"] != null ? (string)u["name"] : "";
                string au = u["acmpUser"] != null ? (string)u["acmpUser"] : null;
                sb.Append("<tr><td>").Append(Esc(nm)).Append("</td><td>").Append(Esc(string.IsNullOrEmpty(au) ? "- (noch offen)" : au)).Append("</td><td>").Append(HasWeb(nm) ? "ja" : "-").Append("</td></tr>");
            }
        sb.Append("</table></div>");

        sb.Append("<h2>Neuen Mitarbeiter anlegen</h2>");
        sb.Append("<p class=\"muted\">Voraussetzung: In ACMP gibt es schon einen lokalen ACMP-User für die Person (in der ACMP-Console angelegt) – der MCP-Server legt keine ACMP-User an, nur die Verknüpfung.</p>");
        sb.Append("<form method=\"post\" action=\"/onboarding-create\">").Append(CsrfField(s));
        sb.Append("<p>Kurzname (für Protokoll und Logs, z.B. <code>mmustermann</code>):<br><input type=\"text\" name=\"name\" pattern=\"[A-Za-z0-9_.-]{2,32}\" required></p>");
        sb.Append("<p>Lokaler ACMP-Username (in ACMP schon angelegt, z.B. <code>admin_mm</code>):<br><input type=\"text\" name=\"acmpUser\" pattern=\"[A-Za-z0-9_.-]{2,32}\" required></p>");
        sb.Append("<p><label><input type=\"checkbox\" name=\"web\" value=\"1\" checked> Zugang zu dieser Weboberfläche (Übersicht, Aktivität, Notbremse, Onboarding)</label></p>");
        sb.Append("<p class=\"actions\"><button class=\"btn btn-primary\" type=\"submit\">Anlegen</button></p>");
        sb.Append("</form>");
        return PageNav(s, sb.ToString(), "onboarding");
    }

    bool HasWeb(string name) { lock (_sesLock) { return name != null && _users.ContainsKey(name); } }

    string OnboardingCreate(WebSession s, Dictionary<string, string> f)
    {
        string name = Get(f, "name"); name = name != null ? name.Trim() : null;
        string acmpUser = Get(f, "acmpUser"); acmpUser = acmpUser != null ? acmpUser.Trim() : null;
        bool web = Get(f, "web") == "1";
        if (string.IsNullOrEmpty(name) || !OnbIdRe.IsMatch(name))
            return Notice(s, "Fehler", "Ungültiger oder fehlender Kurzname (nur Buchstaben, Zahlen und ._-, 2 bis 32 Zeichen).", "onboarding");
        if (string.IsNullOrEmpty(acmpUser) || !OnbIdRe.IsMatch(acmpUser))
            return Notice(s, "Fehler", "Ungültiger oder fehlender ACMP-Username.", "onboarding");

        string apiKey = RandomToken(32);
        string webToken = web ? RandomToken(24) : null;
        string err;
        if (!SaveUser(name, acmpUser, apiKey, webToken, out err))
            return Notice(s, "Fehler", "Konnte config.local.json nicht schreiben: " + err, "onboarding");
        _log("Onboarding: neuer Bediener '" + name + "' angelegt (acmpUser=" + acmpUser + ", Web-Zugang=" + web + ") durch " + s.Name);
        _act.Record(s.Name, "Onboarding", "Webseite", "Neuer Bediener " + name, "name=" + name + ", acmpUser=" + acmpUser + (web ? ", Web-Zugang" : ""), null, true, null, 0);

        var sb = new StringBuilder();
        sb.Append("<h1>Mitarbeiter angelegt: ").Append(Esc(name)).Append("</h1>");
        sb.Append("<div class=\"callout ok\"><b>Wichtig:</b> Diese Werte sind nur JETZT sichtbar. Sicher an ").Append(Esc(name)).Append(" weitergeben (nicht per unverschlüsseltem Chat oder Mail).</div>");
        sb.Append("<h2>1. MCP-Zugang für sein Claude/Codex</h2>");
        sb.Append("<p>Sein persönlicher API-Key:</p><p><input readonly style=\"width:100%;font-family:monospace\" value=\"").Append(Esc(apiKey)).Append("\" onclick=\"this.select()\"></p>");
        AppendClientSnippets(sb, apiKey);
        sb.Append("<h2>2. ACMP-Identität verknüpfen (macht die Person selbst)</h2>");
        sb.Append("<p>Per RDP auf den ACMP-Server verbinden, dort einmalig:</p>");
        sb.Append("<pre>cd ").Append(Esc(InstallDir())).Append("\n.\\acmp-token-setup.exe</pre>");
        sb.Append("<p>Eingaben: <b>Domain-Benutzername</b> = <code>").Append(Esc(acmpUser)).Append("</code>, <b>Domain</b> leer lassen (Enter), <b>Passwort</b> = sein ACMP-Passwort. Gespeichert wird nur ein Token.</p>");
        int step = 3;
        if (web)
        {
            sb.Append("<h2>").Append(step++).Append(". Web-Zugang</h2>");
            sb.Append("<p>Anmeldung unter <code>").Append(Esc(PublicUrl)).Append("/login</code> mit Kurzname <code>").Append(Esc(name)).Append("</code> und diesem Web-Token (gilt sofort):</p>");
            sb.Append("<p><input readonly style=\"width:100%;font-family:monospace\" value=\"").Append(Esc(webToken)).Append("\" onclick=\"this.select()\"></p>");
        }
        sb.Append("<h2>").Append(step).Append(". Aktivieren</h2>");
        sb.Append("<p>Der neue API-Key und die ACMP-Verknüpfung wirken erst nach einem Neustart des Server-Tasks (Admin, auf dem ACMP-Server):</p>");
        sb.Append("<pre>schtasks /End /TN \"ACMP-MCP-Server\"\nschtasks /Run /TN \"ACMP-MCP-Server\"</pre>");
        sb.Append("<p>Soll die Person die optionalen Schreib-Tools nutzen (Client Commands, Kiosk, Windows-Updates, …), muss ihr Kurzname zusätzlich in <code>ccWriter.allowedRequesters</code> stehen.</p>");
        sb.Append("<p class=\"actions\"><a class=\"btn btn-ghost\" href=\"/onboarding\">&larr; Zurück zum Onboarding</a></p>");
        return PageNav(s, sb.ToString(), "onboarding");
    }

    // Schreibt den Bediener nach mcp.users und (mit Web-Zugang) nach web.users; der Web-Zugang gilt sofort.
    bool SaveUser(string name, string acmpUser, string apiKey, string webToken, out string err)
    {
        err = null;
        if (CfgPath == null || !File.Exists(CfgPath)) { err = "Config-Pfad fehlt"; return false; }
        try
        {
            lock (_cfgLock)
            {
                var cfg = JObject.Parse(File.ReadAllText(CfgPath, Encoding.UTF8));
                var mcp = cfg["mcp"] as JObject;
                if (mcp == null) { mcp = new JObject(); cfg["mcp"] = mcp; }
                var users = mcp["users"] as JArray;
                if (users == null) { users = new JArray(); mcp["users"] = users; }
                if (string.Equals(DefaultRequester(cfg), name, StringComparison.OrdinalIgnoreCase)) { err = "Name kollidiert mit dem Standard-Bediener."; return false; }
                foreach (JToken ut in users)
                    if (ut is JObject && string.Equals(ut["name"] != null ? (string)ut["name"] : null, name, StringComparison.OrdinalIgnoreCase)) { err = "Name existiert bereits."; return false; }
                if (webToken != null && HasWeb(name)) { err = "Für diesen Namen gibt es schon einen Web-Zugang."; return false; }
                if (!string.IsNullOrEmpty(acmpUser))
                {
                    string dau = mcp["defaultAcmpUser"] != null ? (string)mcp["defaultAcmpUser"] : null;
                    bool taken = string.Equals(dau, acmpUser, StringComparison.OrdinalIgnoreCase);
                    foreach (JToken ut in users) if (ut is JObject && string.Equals(ut["acmpUser"] != null ? (string)ut["acmpUser"] : null, acmpUser, StringComparison.OrdinalIgnoreCase)) taken = true;
                    if (taken) { err = "Der ACMP-Benutzer '" + acmpUser + "' gehört schon einem anderen Bediener - jede Person braucht ihren eigenen ACMP-Benutzer."; return false; }
                }
                var nu = new JObject();
                nu["name"] = name;
                nu["apiKey"] = apiKey;
                nu["acmpUser"] = string.IsNullOrEmpty(acmpUser) ? null : acmpUser;
                users.Add(nu);
                if (webToken != null) AddWebUser(cfg, name, webToken);
                string tmp = CfgPath + ".tmp-web";
                File.WriteAllText(tmp, cfg.ToString(Formatting.Indented), new UTF8Encoding(false));
                JObject.Parse(File.ReadAllText(tmp, Encoding.UTF8));   // Kontrolle, bevor die echte Config ersetzt wird
                File.Replace(tmp, CfgPath, CfgPath + ".bak-web");
            }
            if (webToken != null) lock (_sesLock) { _users[name] = webToken; }
            return true;
        }
        catch (Exception ex) { err = ex.Message; _log("Onboarding: Speichern fehlgeschlagen: " + ex.Message); return false; }
    }

    static void AddWebUser(JObject cfg, string name, string token)
    {
        var web = cfg["web"] as JObject;
        if (web == null) { web = new JObject(); cfg["web"] = web; }
        var arr = web["users"] as JArray;
        if (arr == null) { arr = new JArray(); web["users"] = arr; }
        var u = new JObject(); u["name"] = name; u["token"] = token;
        arr.Add(u);
    }

    // ---------- Ersteinrichtung: ersten Administrator ohne vorhandenen Login anlegen ----------
    string SetupPage(string tok)
    {
        if (_setupToken == null)
            return Page("<h1>Ersteinrichtung nicht verfügbar</h1><p>Es gibt schon einen Bediener mit Web-Zugang. Weitere Bediener über <a href=\"/onboarding\">Onboarding</a> anlegen (Anmeldung nötig).</p>");
        if (!FixedEquals(tok, _setupToken))
        {
            Slow();
            return Page("<h1>Ersteinrichtung</h1><p>Ungültiger oder fehlender Setup-Token. Den Link schreibt der Server beim Start in <code>server.log</code> (Zeile <code>ERSTEINRICHTUNG NOETIG</code>).</p>");
        }
        var sb = new StringBuilder();
        sb.Append("<h1>Ersteinrichtung</h1><p>Noch kein Bediener mit Web-Zugang – lege den ersten Administrator für diesen ACMP-MCP-Server an. Diese Seite schaltet sich danach ab.</p>");
        sb.Append("<form method=\"post\" action=\"/setup-create\">");
        sb.Append("<input type=\"hidden\" name=\"token\" value=\"").Append(Esc(tok)).Append("\">");
        sb.Append("<p>Kurzname (für Protokoll, Logs und Anmeldung, z.B. <code>admin</code>):<br><input type=\"text\" name=\"name\" pattern=\"[A-Za-z0-9_.-]{2,32}\" required></p>");
        sb.Append("<p>Lokaler ACMP-Username (optional – gibt es noch keinen ACMP-User, leer lassen und später über Onboarding nachtragen):<br><input type=\"text\" name=\"acmpUser\" pattern=\"[A-Za-z0-9_.-]{0,32}\"></p>");
        sb.Append("<p class=\"actions\"><button class=\"btn btn-primary\" type=\"submit\">Ersten Administrator anlegen</button></p>");
        sb.Append("</form>");
        return Page(sb.ToString());
    }

    string SetupCreate(Dictionary<string, string> f, WebResult res)
    {
        string tok = Get(f, "token");
        if (_setupToken == null || !FixedEquals(tok, _setupToken)) return SetupPage(tok);
        string name = Get(f, "name"); name = name != null ? name.Trim() : null;
        string acmpUser = Get(f, "acmpUser"); acmpUser = acmpUser != null ? acmpUser.Trim() : null;
        string back = "<p><a href=\"/setup?token=" + Uri.EscapeDataString(tok) + "\">&larr; Zurück</a></p>";
        if (string.IsNullOrEmpty(name) || !OnbIdRe.IsMatch(name))
            return Page("<h1>Fehler</h1><p>Ungültiger oder fehlender Kurzname (nur Buchstaben, Zahlen und ._-, 2 bis 32 Zeichen).</p>" + back);
        if (!string.IsNullOrEmpty(acmpUser) && !OnbIdRe.IsMatch(acmpUser))
            return Page("<h1>Fehler</h1><p>Ungültiger ACMP-Username.</p>" + back);

        string apiKey = RandomToken(32);
        string webToken = RandomToken(24);
        string err;
        if (!SaveUser(name, acmpUser, apiKey, webToken, out err))
            return Page("<h1>Fehler</h1><p>Konnte config.local.json nicht schreiben: " + Esc(err) + "</p>" + back);
        _setupToken = null;   // Ersteinrichtung fuer diesen laufenden Prozess schliessen
        _log("Ersteinrichtung: erster Administrator '" + name + "' angelegt (acmpUser=" + (string.IsNullOrEmpty(acmpUser) ? "-" : acmpUser) + ").");
        StartSession(res, name);

        var sb = new StringBuilder();
        sb.Append("<h1>Willkommen, ").Append(Esc(name)).Append("!</h1>");
        sb.Append("<div class=\"callout ok\"><b>Wichtig:</b> Diese Werte sind nur JETZT sichtbar – sicher aufbewahren.</div>");
        sb.Append("<h2>Dein MCP-API-Key</h2><p><input readonly style=\"width:100%;font-family:monospace\" value=\"").Append(Esc(apiKey)).Append("\" onclick=\"this.select()\"></p>");
        AppendClientSnippets(sb, apiKey);
        sb.Append("<h2>Dein Web-Token</h2><p>Anmeldung unter <code>").Append(Esc(PublicUrl)).Append("/login</code> mit Kurzname <code>").Append(Esc(name)).Append("</code> und:</p>");
        sb.Append("<p><input readonly style=\"width:100%;font-family:monospace\" value=\"").Append(Esc(webToken)).Append("\" onclick=\"this.select()\"></p>");
        if (string.IsNullOrEmpty(acmpUser))
            sb.Append("<h2>ACMP-Identität verknüpfen</h2><p>Noch kein ACMP-User verknüpft. Sobald einer existiert, über Onboarding nachtragen.</p>");
        else
        {
            sb.Append("<h2>ACMP-Identität verknüpfen (machst du selbst)</h2>");
            sb.Append("<p>Per RDP auf den ACMP-Server verbinden, dort einmalig:</p><pre>cd ").Append(Esc(InstallDir())).Append("\n.\\acmp-token-setup.exe</pre>");
            sb.Append("<p>Eingaben: <b>Domain-Benutzername</b> = <code>").Append(Esc(acmpUser)).Append("</code>, <b>Domain</b> leer lassen (Enter), <b>Passwort</b> = dein ACMP-Passwort. Gespeichert wird nur ein Token.</p>");
        }
        sb.Append("<h2>Aktivieren</h2><p>Server-Task einmal neu starten (Admin, auf dem ACMP-Server), damit der API-Key greift:</p>");
        sb.Append("<pre>schtasks /End /TN \"ACMP-MCP-Server\"\nschtasks /Run /TN \"ACMP-MCP-Server\"</pre>");
        sb.Append("<p class=\"actions\"><a class=\"btn btn-primary\" href=\"/dashboard\">Weiter zur Übersicht</a></p>");
        return Page(sb.ToString());
    }

    // Ordner der laufenden EXE (= Installationsordner) fuer Copy-Paste-Anleitungen.
    static string InstallDir() { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'); }

    // Copy-Paste-Konfiguration fuer MCP-Clients (Ersteinrichtung und Onboarding).
    void AppendClientSnippets(StringBuilder sb, string key)
    {
        string url = Esc(PublicUrl) + "/", k = Esc(key);
        sb.Append("<p><b>Jeder MCP-Client</b> (Streamable HTTP): URL <code>").Append(url).Append("</code>, Header <code>Authorization: Bearer ").Append(k).Append("</code>. Vorlagen für Codex, VS Code/Copilot, Cursor, Gemini u. a.: <code>docs/CLIENTS.md</code> im Repo.</p>");
        sb.Append("<p><b>Claude Code (CLI):</b></p><pre>claude mcp add --transport http acmp ").Append(url).Append(" --header \"Authorization: Bearer ").Append(k).Append("\" --scope user</pre>");
        AppendClaudeDesktopSnippet(sb, url, k);
    }

    // Claude Desktop: zuerst die Erweiterung (.mcpb aus den Releases), mcp-remote nur noch als Weg ohne Erweiterung.
    // url und k kommen bereits maskiert (Esc).
    static void AppendClaudeDesktopSnippet(StringBuilder sb, string url, string k)
    {
        sb.Append("<p><b>Claude Desktop</b>, am einfachsten als Erweiterung:</p><ol class=\"steps\">");
        sb.Append("<li><code>acmp-mcp-&lt;version&gt;.mcpb</code> aus den <a href=\"https://github.com/Herzog-GmbH/acmp-mcp/releases\" target=\"_blank\" rel=\"noopener\">GitHub-Releases</a> laden.</li>");
        sb.Append("<li>Doppelklick auf die Datei (oder in Claude Desktop unter <b>Einstellungen → Erweiterungen</b> hineinziehen), dann „Installieren“.</li>");
        sb.Append("<li>In den Einstellungen der Erweiterung eintragen: <b>Server-Adresse</b> <code>").Append(url).Append("</code> und <b>API-Key</b> <code>").Append(k).Append("</code>. Das Feld <b>Stammzertifikat</b> bleibt leer (nur für HTTPS über einen eigenen TLS-Proxy).</li>");
        sb.Append("<li>Den Schalter <b>Aktiviert</b> einschalten.</li></ol>");
        sb.Append("<p><b>Ohne Erweiterung</b> (auch für andere Clients nur mit stdio): in <code>claude_desktop_config.json</code> über die Brücke mcp-remote, braucht Node.js:</p><pre>{\n  \"mcpServers\": {\n    \"acmp\": {\n      \"command\": \"npx\",\n      \"args\": [\"-y\", \"mcp-remote\", \"").Append(url).Append("\", \"--header\", \"Authorization: Bearer ").Append(k).Append("\", \"--allow-http\"]\n    }\n  }\n}</pre>");
    }

    // ---------- Hilfen ----------
    static string Get(Dictionary<string, string> d, string k) { string v; return d != null && d.TryGetValue(k, out v) ? v : null; }

    public static string PathOnly(string pathQuery)
    {
        if (pathQuery == null) return "/";
        int q = pathQuery.IndexOf('?');
        return q >= 0 ? pathQuery.Substring(0, q) : pathQuery;
    }

    static Dictionary<string, string> ParseQuery(string pathQuery)
    {
        if (pathQuery == null) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int q = pathQuery.IndexOf('?');
        return ParsePairs(q < 0 ? null : pathQuery.Substring(q + 1));
    }

    // a=1&b=2 (Query-String oder application/x-www-form-urlencoded)
    static Dictionary<string, string> ParsePairs(string s)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(s)) return d;
        foreach (string p in s.Split('&'))
        {
            if (p.Length == 0) continue;
            int eq = p.IndexOf('=');
            string k = eq >= 0 ? p.Substring(0, eq) : p;
            string v = eq >= 0 ? p.Substring(eq + 1) : "";
            try { d[Uri.UnescapeDataString(k.Replace('+', ' '))] = Uri.UnescapeDataString(v.Replace('+', ' ')); } catch { d[k] = v; }
        }
        return d;
    }

    static string Esc(string s)
    {
        if (s == null) return "";
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }

    static string RandomToken(int numBytes)
    {
        byte[] b = new byte[numBytes];
        using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(b);
        return Convert.ToBase64String(b).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
