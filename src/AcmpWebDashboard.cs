// AcmpWebDashboard: Startseite der Weboberflaeche (/dashboard) als Lagebild (seit 23.10.2026) und die Formulare fuer offene Punkte.
// Teil von AcmpWeb (partial). Daten: AcmpDashboard (ACMP-DB, Zwischenspeicher), AcmpOpenItems (offene-punkte.json), AcmpActivity (MCP-Leiste).
// Das Dashboard aendert nichts in ACMP. Einzige Schreibaktionen: offenen Punkt anlegen, erledigen, wieder oeffnen (POST + csrf,
// im Aktivitaetsprotokoll als "Webseite"); sie gehen auch bei gezogener Notbremse (die sperrt nur die MCP-Tools).
// net48 / C# 5 (csc v4.0.30319): keine string-Interpolation, keine expression-bodied member, kein ?.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

partial class AcmpWeb
{
    public AcmpDashboard Dashboard;     // null = keine DB-Anbindung: nur MCP-Leiste, offene Punkte, Aktivitaet
    public AcmpOpenItems OpenItems;     // null = keine Liste (nur in Tests)

    static readonly Regex OsYear = new Regex(@"Server (\d{4})");

    const string DashboardCss =
          ".mstrip{display:flex;flex-wrap:wrap;gap:8px;align-items:center;margin:0 0 18px}"
        + ".mpill{display:inline-flex;align-items:center;gap:6px;min-height:28px;padding:2px 11px;border-radius:999px;font-size:12.5px;font-weight:600;background:#f6f8fb;border:1px solid rgba(10,15,35,.1);color:#1c2430;text-decoration:none}"
        + ".mpill:hover{text-decoration:none;background:#eef2f7}.mpill b{font-weight:800;color:#0a0a0a}"
        + ".mpill.ok{background:#e7f6ec;border-color:transparent;color:#15803d}.mpill.stop{background:#fde8e8;border-color:#f3b4b4;color:#c81e1e}"
        + ".mstand{margin-left:auto;display:flex;gap:10px;align-items:center;font-size:12.5px;color:#7a838c;flex-wrap:wrap}"
        + ".btn-sm{padding:6px 12px;font-size:12.5px;border-radius:9px}"
        + ".now{border:1px solid rgba(10,15,35,.1);border-radius:12px;overflow:hidden;background:#fff;box-shadow:0 10px 30px rgba(10,15,35,.05)}"
        + ".nowrow{display:grid;grid-template-columns:70px 84px minmax(0,1fr);gap:12px;align-items:baseline;padding:10px 14px;border-top:1px solid rgba(10,15,35,.08);font-size:14px;color:#1c2430}"
        + ".nowrow:first-child{border-top:0}"
        + ".sev{display:inline-block;text-align:center;font-size:11px;font-weight:800;letter-spacing:.04em;text-transform:uppercase;padding:2px 6px;border-radius:6px;min-width:56px}"
        + ".sev.rot{background:#fde8e8;color:#c81e1e}.sev.gelb,.sev.tot{background:#fff1e6;color:#b45309}.sev.info{background:#f1f4f8;color:#545e66}"
        + ".area{font-size:12px;font-weight:700;color:#7a838c;text-transform:uppercase;letter-spacing:.05em}"
        + ".card .v small{font-size:15px;font-weight:700;color:#7a838c;letter-spacing:0}"
        + ".two{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,380px),1fr));gap:14px}"
        + ".panel{background:#fff;border:1px solid rgba(10,15,35,.1);border-radius:12px;padding:14px 16px;min-width:0;box-shadow:0 10px 30px rgba(10,15,35,.05)}"
        + ".panel h3{margin:0 0 10px;font-size:14px;font-weight:700;color:#0a0a0a}"
        + ".jl{list-style:none;margin:0;padding:0;display:grid;gap:8px}.jl li{display:grid;grid-template-columns:52px minmax(0,1fr);gap:10px;font-size:13.5px;line-height:1.45;color:#1c2430}"
        + ".jl .sev{min-width:0}"
        + ".bars{display:grid;gap:9px}.bar{display:grid;grid-template-columns:minmax(0,1fr) 100px;gap:10px;align-items:center;font-size:13px;color:#1c2430}"
        + ".bar .nm{white-space:nowrap;overflow:hidden;text-overflow:ellipsis;display:block}"
        + ".track{height:7px;border-radius:4px;background:#f6f8fb;border:1px solid rgba(10,15,35,.1);overflow:hidden;margin-top:3px}"
        + ".fill{height:100%;background:#dc2626}.fill.low{background:#d97706}"
        + ".bar .n{font-family:'JetBrains Mono',Consolas,monospace;font-size:12px;text-align:right;color:#7a838c;white-space:nowrap}"
        + ".bf{display:inline-block;padding:1px 8px;border-radius:999px;font-size:11px;font-weight:700;white-space:nowrap;margin:1px 2px 1px 0}"
        + ".bf.crit{background:#fde8e8;color:#c81e1e}.bf.warn{background:#fff1e6;color:#b45309}.bf.grey{background:#f1f4f8;color:#545e66}.bf.blue{background:#eaf1fb;color:#00459a}"
        + ".due{color:#c81e1e;font-weight:700}"
        + "form.inline{display:inline;margin:0}"
        + ".newform{display:grid;gap:12px;margin-top:12px;max-width:760px}.newform .row{display:flex;flex-wrap:wrap;gap:10px;align-items:center}"
        + ".newform textarea{width:100%;min-height:80px}.newform input[type=text]{width:100%}"
        + "td.num{font-variant-numeric:tabular-nums;white-space:nowrap}"
        + "@media(max-width:680px){.nowrow{grid-template-columns:62px minmax(0,1fr)}.nowrow .area{display:none}.mstand{margin-left:0}}";

    // ---------- Routen: Aktualisieren und offene Punkte (nur POST mit csrf; Pruefung in Route) ----------
    bool RouteDashboard(string path, string method, Dictionary<string, string> f, WebSession s, WebResult res)
    {
        if (path == "/dashboard-refresh")
        {
            if (method == "POST" && Dashboard != null)
            {
                bool done = Dashboard.RefreshNow();
                _log("Webseite: Dashboard aktualisiert durch " + s.Name + (done ? "" : " (zu frueh, alter Stand bleibt)"));
                Redirect(res, done ? "/dashboard" : "/dashboard?zufrueh=1");
            }
            else Redirect(res, "/dashboard");
            return true;
        }
        if (path != "/punkt-neu" && path != "/punkt-erledigt" && path != "/punkt-oeffnen") return false;
        if (method != "POST" || OpenItems == null) { Redirect(res, "/dashboard"); return true; }
        var a = new JObject();
        string what;
        if (path == "/punkt-neu")
        {
            a["action"] = "anlegen"; what = "angelegt";
            foreach (string k in new[] { "titel", "details", "bereich", "prio", "faellig" }) { string v = Get(f, k); if (!string.IsNullOrEmpty(v) && v.Trim().Length > 0) a[k] = v; }
        }
        else
        {
            a["action"] = path == "/punkt-erledigt" ? "erledigen" : "oeffnen";
            what = path == "/punkt-erledigt" ? "erledigt" : "wieder geöffnet";
            a["id"] = Get(f, "id");
            string n = Get(f, "notiz"); if (!string.IsNullOrEmpty(n)) a["notiz"] = n;
        }
        try
        {
            JObject r = OpenItems.Set(a, s.Name, "Webseite");
            JObject p = r["punkt"] as JObject;
            string id = p != null ? (string)p["id"] : null, titel = p != null ? (string)p["titel"] : null;
            _act.Record(s.Name, "Offene Punkte", "Webseite", "Punkt " + what + ": " + titel, "id=" + id, null, true, null, 0);
            Redirect(res, "/dashboard#punkte");
        }
        catch (Exception ex)
        {
            res.Code = 400;
            res.Body = Notice(s, "Nicht gespeichert", ex.Message, "dashboard");
        }
        return true;
    }

    // ---------- Startseite ----------
    string DashboardPage(WebSession s, Dictionary<string, string> q)
    {
        var sb = new StringBuilder();
        sb.Append(BrakeBanner());
        JObject snap = null;
        string snapErr = null;
        if (Dashboard != null) { try { snap = Dashboard.Get(); } catch (Exception ex) { snapErr = ex.Message; } }
        McpStrip(sb, s, snap);
        sb.Append("<h1>Lagebild</h1>");
        if (Get(q, "zufrueh") == "1")
            sb.Append("<div class=\"callout warn\">Gerade erst aktualisiert – „Jetzt aktualisieren“ geht höchstens einmal pro Minute. Angezeigt wird der letzte Stand.</div>");
        if (Dashboard == null)
            sb.Append("<div class=\"callout warn\">Keine Datenbank-Anbindung (<code>db.connectionString</code> fehlt) – Flotte, Server und Jobs können nicht angezeigt werden.</div>");
        else if (snap == null)
            sb.Append("<div class=\"callout\">Die Daten werden gerade geholt" + (snapErr != null ? " (" + Esc(snapErr) + ")" : "") + ". Bitte die Seite in ein paar Sekunden neu laden.</div>");
        else
        {
            JObject fl = snap["flotte"] as JObject;
            sb.Append("<p class=\"muted\" style=\"margin-top:0\">");
            if (fl != null && fl["gesamt"] != null)
                sb.Append(Esc(AcmpDashboard.Num(AcmpDashboard.Lng(fl["gesamt"])) + " Clients, davon " + AcmpDashboard.Num(AcmpDashboard.Lng(fl["server"])) + " Server und " + AcmpDashboard.Num(AcmpDashboard.Lng(fl["unix"])) + " Linux · "));
            sb.Append("Daten älter als ").Append(Dashboard.Opt.CacheMinutes).Append(" Minuten werden beim nächsten Aufruf neu geholt</p>");
            NowSection(sb, snap);
            FleetSection(sb, fl);
            ServerSection(sb, snap["server"] as JArray);
            JobsSection(sb, snap["jobs"] as JObject);
        }
        OpenItemsSection(sb, s);
        sb.Append("<h2>Letzte MCP-Aktivität</h2>");
        sb.Append(ActivityTable(_act.Recent(400), 8, false));
        sb.Append("<p><a href=\"/activity\">Gesamtes Aktivitätsprotokoll &rarr;</a></p>");
        // Im offenen Tab alle 5 Minuten neu laden, nicht waehrend jemand tippt oder ein Formular offen hat
        sb.Append("<script>(function(){function r(){var a=document.activeElement;var busy=a&&/^(INPUT|TEXTAREA|SELECT)$/.test(a.tagName);"
                + "if(busy||document.querySelector('details.keep[open]')){setTimeout(r,60000);return;}location.href='/dashboard';}setTimeout(r,300000);})();</script>");
        return PageNav(s, sb.ToString(), "dashboard");
    }

    // Schmale Leiste mit den MCP-Kennzahlen der frueheren Uebersicht und dem Datenstand
    void McpStrip(StringBuilder sb, WebSession s, JObject snap)
    {
        List<JObject> recent = _act.Recent(3000);
        string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string weekStart = DateTime.Now.AddDays(-6).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        int nToday = 0, errToday = 0, nWeek = 0, errWeek = 0;
        foreach (JObject e in recent)
        {
            if ((string)e["kind"] == "Webseite") continue;
            string z = e["zeit"] != null ? (string)e["zeit"] : "";
            bool ok = (e["ok"] != null && (bool)e["ok"]) || (e["pending"] != null && (bool)e["pending"]);
            if (string.CompareOrdinal(z, weekStart) >= 0) { nWeek++; if (!ok) errWeek++; }
            if (z.StartsWith(today)) { nToday++; if (!ok) errToday++; }
        }
        int toolCount = 0, writeTools = 0;
        JArray all = GetAllTools != null ? GetAllTools() : null;
        if (all != null) foreach (JToken t in all) { toolCount++; if (WriteTierOf != null && WriteTierOf((string)t["name"]) != null) writeTools++; }
        bool brake = _act.BrakeActive;

        sb.Append("<div class=\"mstrip\">");
        sb.Append("<a class=\"mpill ").Append(brake ? "stop" : "ok").Append("\" href=\"/settings\">").Append(brake ? "Notbremse GEZOGEN" : "Notbremse aus").Append("</a>");
        sb.Append("<a class=\"mpill\" href=\"/activity\">Schreibaktionen heute <b>").Append(nToday).Append("</b> · ").Append(errToday > 0 ? errToday + " mit Fehler" : "ohne Fehler").Append("</a>");
        sb.Append("<a class=\"mpill\" href=\"/activity\">7 Tage <b>").Append(nWeek).Append("</b>").Append(errWeek > 0 ? " · " + errWeek + " mit Fehler" : "").Append("</a>");
        sb.Append("<a class=\"mpill\" href=\"/settings\">Tools <b>").Append(toolCount).Append("</b> · ").Append(writeTools).Append(" schreiben</a>");
        sb.Append(WumPill());
        JArray cl = LoadChangelog();
        if (cl.Count > 0)
        {
            JObject last = cl[0] as JObject;
            if (last != null)
                sb.Append("<a class=\"mpill\" href=\"/changelog\">Letztes Update <b>").Append(Esc(DateShort(last["date"] != null ? (string)last["date"] : ""))).Append("</b> ")
                  .Append(Esc(Cut(last["title"] != null ? (string)last["title"] : "", 48))).Append("</a>");
        }
        if (Dashboard != null)
        {
            sb.Append("<span class=\"mstand\">");
            if (snap != null && snap["stand"] != null)
            {
                sb.Append("Datenstand ").Append(Esc(TimeShort((string)snap["stand"])));
                if (snap["dauerMs"] != null) sb.Append(" <small>(").Append(Esc((AcmpDashboard.Dbl(snap["dauerMs"]) / 1000.0).ToString("0.0", CultureInfo.GetCultureInfo("de-DE")))).Append(" s)</small>");
            }
            if (Dashboard.Building) sb.Append(" · wird aktualisiert …");
            sb.Append("<form class=\"inline\" method=\"post\" action=\"/dashboard-refresh\">").Append(CsrfField(s))
              .Append("<button class=\"btn btn-ghost btn-sm\" type=\"submit\">Jetzt aktualisieren</button></form></span>");
        }
        sb.Append("</div>");
    }

    void NowSection(StringBuilder sb, JObject snap)
    {
        List<JObject> list = AcmpDashboard.Findings(snap, Dashboard.Opt, 10);
        sb.Append("<h2>Jetzt wichtig</h2>");
        if (list.Count == 0) { sb.Append("<div class=\"callout ok\">Keine Auffälligkeiten nach den eingestellten Schwellen.</div>"); return; }
        sb.Append("<div class=\"now\">");
        foreach (JObject e in list)
        {
            string st = (string)e["stufe"];
            sb.Append("<div class=\"nowrow\"><span class=\"sev ").Append(Esc(st)).Append("\">").Append(Esc(st)).Append("</span><span class=\"area\">").Append(Esc((string)e["bereich"])).Append("</span>")
              .Append("<span><b>").Append(Esc((string)e["titel"])).Append("</b>: ").Append(Esc((string)e["text"])).Append("</span></div>");
        }
        sb.Append("</div>");
    }

    void FleetSection(StringBuilder sb, JObject fl)
    {
        if (fl == null) return;
        sb.Append("<h2>Flotte</h2><div class=\"cards\">");
        if (fl["gesamt"] != null)
        {
            long ges = L(fl["gesamt"]), nie = L(fl["nieVerbunden"]);
            sb.Append(DCard(null, "Online jetzt", N(L(fl["online"])), "von " + N(ges - nie),
                nie > 0 ? "dazu " + N(nie) + " nie verbunden (alte Scan-Einträge, Netzwerk-Boot-Platzhalter)" : "alle Clients waren schon einmal verbunden"));
            long lo = L(fl["langeOffline"]);
            sb.Append(DCard(lo > 0 ? "warnc" : "ok", "Länger offline", N(lo), null,
                "kein Kontakt seit mehr als " + Dashboard.Opt.OfflineDays + " Tagen" + (lo > 0 ? ": " + AcmpDashboard.Names(StrList(fl["langeOfflineNamen"]), 3) : "")));
        }
        JObject pl = fl["platz"] as JObject;
        if (pl != null)
        {
            long cl = L(pl["clients"]), crit = L(pl["kritisch"]);
            sb.Append(DCard(crit > 0 ? "alert" : cl > 0 ? "warnc" : "ok", "Wenig Platz", N(cl), null,
                "Clients mit " + N(L(pl["laufwerke"])) + " Laufwerken unter " + Dashboard.Opt.MinFreeGB + " GB oder " + Dashboard.Opt.MinFreePct + " %" + (crit > 0 ? ", davon " + N(crit) + " unter " + Dashboard.Opt.CritFreeGB + " GB" : "")));
        }
        JObject bl = fl["bitlocker"] as JObject;
        if (bl != null)
        {
            long off = L(bl["cOffen"]);
            sb.Append(DCard(off > 0 ? "alert" : "ok", "BitLocker C:", N(off), "offen", "von " + N(L(bl["mitDaten"])) + " Clients mit BitLocker-Daten ungeschützt"));
        }
        JObject av = fl["virenschutz"] as JObject;
        if (av != null)
        {
            long ohne = L(av["arbeitsplaetzeOhne"]);
            var act = new List<string>(); var idle = new List<string>();
            if (av["produkte"] is JArray)
                foreach (JObject p in (JArray)av["produkte"])
                {
                    if (L(p["aktiv"]) > 0) act.Add((string)p["name"] + " " + N(L(p["aktiv"])));
                    else idle.Add((string)p["name"] + " " + N(L(p["clients"])));
                }
            string sub = "Arbeitsplätze ohne aktiven Schutz" + (ohne > 0 ? ": " + AcmpDashboard.Names(StrList(av["ohneNamen"]), 3) : "");
            if (act.Count > 0) sub += " · aktiv: " + string.Join(", ", act.ToArray());
            if (idle.Count > 0) sub += " · inaktiv gemeldet: " + string.Join(", ", idle.ToArray());
            sb.Append(DCard(ohne > 0 ? "alert" : "ok", "Virenschutz", N(ohne), null, sub));
        }
        JObject up = fl["updates"] as JObject;
        if (up != null)
        {
            long k = L(up["kritischClients"]), fc = L(up["fehlgeschlagenClients"]);
            sb.Append(DCard(k > 0 ? "warnc" : "ok", "Kritische Updates", N(k), null, "Clients mit fehlendem kritischen Update · " + N(L(up["fehlendClients"])) + " mit irgendeinem"));
            sb.Append(DCard(fc > 0 ? "warnc" : "ok", "Updates fehlgeschlagen", N(fc), null, "Clients mit " + N(L(up["fehlgeschlagen"])) + " fehlgeschlagenen Installationen in 7 Tagen"));
        }
        JObject sc = fl["scan"] as JObject;
        if (sc != null)
        {
            long c = L(sc["clients"]), fr = L(sc["frisch"]);
            string sub = "in den letzten " + Dashboard.Opt.ScanDays + " Tagen gescannt" + (sc["neuester"] != null && sc["neuester"].Type != JTokenType.Null ? ", neuester Scan " + TimeShort((string)sc["neuester"]) : "");
            JObject top = sc["topCve"] as JObject;
            if (top != null) sub += " · häufigste Lücke ab CVSS 9: " + (string)top["cve"] + " (" + Cut((string)top["titel"], 40) + ") auf " + N(L(top["clients"])) + " Clients";
            sb.Append(DCard(fr < c ? "warnc" : "ok", "Schwachstellen-Scan", N(fr), "von " + N(c), sub));
        }
        sb.Append("</div>");
        if (fl["agenten"] is JArray && ((JArray)fl["agenten"]).Count > 0)
        {
            var parts = new List<string>();
            foreach (JObject a in (JArray)fl["agenten"]) parts.Add((string)a["version"] + ": " + N(L(a["anzahl"])));
            sb.Append("<p class=\"muted\" style=\"font-size:13px\">ACMP-Agent: ").Append(Esc(string.Join(" · ", parts.ToArray()))).Append("</p>");
        }
    }

    void ServerSection(StringBuilder sb, JArray srv)
    {
        if (srv == null) return;
        var rows = new List<KeyValuePair<int, JObject>>();
        int online = 0;
        foreach (JObject s in srv)
        {
            if (L(s["online"]) == 0 && s["online"] != null && s["online"].Type != JTokenType.Null) online++;
            rows.Add(new KeyValuePair<int, JObject>(ServerRank(s), s));
        }
        var idx = new Dictionary<JObject, int>(); for (int i = 0; i < rows.Count; i++) idx[rows[i].Value] = i;
        rows.Sort(delegate(KeyValuePair<int, JObject> x, KeyValuePair<int, JObject> y) { int d = x.Key.CompareTo(y.Key); return d != 0 ? d : idx[x.Value].CompareTo(idx[y.Value]); });
        int withFinding = 0; foreach (var kv in rows) if (kv.Key < 9) withFinding++;
        sb.Append("<h2>Server <span class=\"muted\" style=\"text-transform:none;letter-spacing:0;font-weight:600\">")
          .Append(srv.Count).Append(" · ").Append(online == srv.Count ? "alle online" : online + " online").Append(" · ").Append(withFinding).Append(" mit Befund</span></h2>");
        if (withFinding == 0) sb.Append("<div class=\"callout ok\">Kein Server mit Befund.</div>");
        else
        {
            sb.Append(ServerTableHead());
            int shown = 0;
            foreach (var kv in rows) { if (kv.Key >= 9 || shown >= 14) break; sb.Append(ServerRow(kv.Value)); shown++; }
            sb.Append("</table></div>");
            if (withFinding > shown) sb.Append("<p class=\"muted\" style=\"font-size:13px\">").Append(withFinding - shown).Append(" weitere mit Befund in der vollständigen Liste.</p>");
        }
        sb.Append("<details><summary>Alle ").Append(srv.Count).Append(" Server</summary>").Append(ServerTableHead());
        foreach (var kv in rows) sb.Append(ServerRow(kv.Value));
        sb.Append("</table></div></details>");
    }

    // 0 = Platz kritisch / offline, 1 = kritische Updates, 2 = Platz knapp, 3 = Update-Fehler, 4 = fehlende Updates, 9 = ohne Befund
    int ServerRank(JObject s)
    {
        if (s["online"] != null && s["online"].Type != JTokenType.Null && L(s["online"]) != 0) return 0;
        JArray d = s["laufwerke"] as JArray;
        if (d != null && d.Count > 0)
        {
            JObject w = (JObject)d[0];
            if (AcmpDashboard.DriveCritical(AcmpDashboard.Dbl(w["freiGB"]), AcmpDashboard.Dbl(w["freiPct"]), Dashboard.Opt)) return 0;
        }
        if (L(s["kritisch"]) > 0) return 1;
        if (d != null && d.Count > 0) return 2;
        if (L(s["fehlgeschlagen7"]) > 0) return 3;
        if (L(s["fehlend"]) > 0) return 4;
        return 9;
    }

    static string ServerTableHead()
    {
        return "<div class=\"tablewrap\"><table><tr><th>Server</th><th>System</th><th>Letzter Kontakt</th><th>Knappstes Laufwerk</th><th>Fehlende Updates</th><th>Befund</th></tr>";
    }

    string ServerRow(JObject s)
    {
        var sb = new StringBuilder();
        string os = (string)s["os"] ?? "";
        Match m = OsYear.Match(os);
        bool off = s["online"] != null && s["online"].Type != JTokenType.Null && L(s["online"]) != 0;
        JArray d = s["laufwerke"] as JArray;
        string drive = "ok"; bool dCrit = false, dLow = false;
        if (d != null && d.Count > 0)
        {
            JObject w = (JObject)d[0];
            double gb = AcmpDashboard.Dbl(w["freiGB"]), pct = AcmpDashboard.Dbl(w["freiPct"]);
            drive = (string)w["lw"] + " " + AcmpDashboard.Gb(gb) + " · " + AcmpDashboard.Pct(pct);
            if (d.Count > 1) drive += " (+" + (d.Count - 1) + ")";
            dCrit = AcmpDashboard.DriveCritical(gb, pct, Dashboard.Opt); dLow = !dCrit;
        }
        long miss = L(s["fehlend"]), crit = L(s["kritisch"]), fail = L(s["fehlgeschlagen7"]);
        sb.Append("<tr><td><b>").Append(Esc((string)s["name"])).Append("</b></td><td>").Append(Esc(m.Success ? "Server " + m.Groups[1].Value : Cut(os, 30))).Append("</td>");
        sb.Append("<td class=\"num\">").Append(Esc(s["letzterKontakt"] != null && s["letzterKontakt"].Type != JTokenType.Null ? TimeShort((string)s["letzterKontakt"]) : "nie")).Append("</td>");
        sb.Append("<td class=\"num\">").Append(Esc(drive)).Append("</td>");
        sb.Append("<td class=\"num\">").Append(miss > 0 ? Esc(N(miss) + (crit > 0 ? " · " + N(crit) + " kritisch" : "")) : "–").Append("</td><td>");
        if (off) sb.Append("<span class=\"bf crit\">offline</span>");
        if (dCrit) sb.Append("<span class=\"bf crit\">Platz</span>"); else if (dLow) sb.Append("<span class=\"bf warn\">Platz</span>");
        if (crit > 0) sb.Append("<span class=\"bf warn\">Updates</span>"); else if (miss > 0) sb.Append("<span class=\"bf grey\">Updates</span>");
        if (fail > 0) sb.Append("<span class=\"bf warn\">").Append(fail).Append(" Update-Fehler</span>");
        if (!off && d != null && d.Count == 0 && miss == 0 && fail == 0) sb.Append("<span class=\"bf blue\">ok</span>");
        sb.Append("</td></tr>");
        return sb.ToString();
    }

    void JobsSection(StringBuilder sb, JObject j)
    {
        if (j == null) return;
        long runs = L(j["laeufe7"]), errs = L(j["fehler7"]);
        sb.Append("<h2>Jobs <span class=\"muted\" style=\"text-transform:none;letter-spacing:0;font-weight:600\">");
        if (j["gesamt"] != null) sb.Append(Esc(N(L(j["auffaellig"])) + " von " + N(L(j["gesamt"])) + " Daueraufträgen auffällig · "));
        sb.Append(Esc(N(errs) + " von " + N(runs) + " Läufen mit Fehler" + (runs > 0 ? " (" + AcmpDashboard.Pct(100.0 * errs / runs) + ")" : "") + ", 7 Tage")).Append("</span></h2>");
        sb.Append("<div class=\"two\"><div class=\"panel\"><h3>Auffällige Daueraufträge</h3>");
        JArray liste = j["liste"] as JArray;
        if (liste == null || liste.Count == 0) sb.Append("<p class=\"muted\">Keine.</p>");
        else
        {
            // Jobs, die nie laufen, in einer Zeile; darunter die uebrigen auffaelligen (rot, gelb), damit Fehlerquoten sichtbar bleiben
            var dead = new List<string>();
            foreach (JObject e in liste) if ((string)e["stufe"] == "tot") dead.Add((string)e["name"]);
            sb.Append("<ul class=\"jl\">");
            if (dead.Count > 0)
                sb.Append("<li><span class=\"sev tot\">tot</span><span><b>").Append(dead.Count).Append(dead.Count == 1 ? " Dauerauftrag läuft" : " Daueraufträge laufen").Append(" nie</b> (leerer Container oder abgeschaltet): ")
                  .Append(Esc(AcmpDashboard.Names(AcmpJobs.Grouped(dead), 4))).Append("</span></li>");
            int n = 0;
            foreach (JObject e in liste)
            {
                string st = (string)e["stufe"];
                if (st == "tot") continue;
                if (n++ >= 6) break;
                sb.Append("<li><span class=\"sev ").Append(Esc(st)).Append("\">").Append(Esc(st)).Append("</span><span><b>").Append(Esc((string)e["name"])).Append("</b>: ")
                  .Append(Esc(Cut((string)e["hinweis"], 150))).Append("</span></li>");
            }
            sb.Append("</ul>");
            sb.Append("<p class=\"muted\" style=\"font-size:12.5px;margin:10px 0 0\">").Append(Esc("rot " + L(j["rot"]) + " · tot " + L(j["tot"]) + " · gelb " + L(j["gelb"])))
              .Append(" · alle mit <code>acmp_jobs onlyNotable=true</code>").Append("</p>");
        }
        sb.Append("</div><div class=\"panel\"><h3>Meiste Fehler in 7 Tagen</h3>");
        JArray top = j["meisteFehler"] as JArray;
        if (top == null || top.Count == 0) sb.Append("<p class=\"muted\">Keine Jobs mit mindestens ").Append(Dashboard.Opt.JobErrorMin).Append(" Fehlern.</p>");
        else
        {
            sb.Append("<div class=\"bars\">");
            int n = 0;
            foreach (JObject e in top)
            {
                if (n++ >= 5) break;
                long r = L(e["laeufe"]), x = L(e["fehler"]);
                double rate = r > 0 ? Math.Min(100.0, 100.0 * x / r) : 0;
                sb.Append("<div class=\"bar\"><div><span class=\"nm\" title=\"").Append(Esc((string)e["name"])).Append("\">").Append(Esc((string)e["name"])).Append("</span>")
                  .Append("<div class=\"track\"><div class=\"fill").Append(rate < 50 ? " low" : "").Append("\" style=\"width:").Append(rate.ToString("0.#", CultureInfo.InvariantCulture)).Append("%\"></div></div></div>")
                  .Append("<span class=\"n\">").Append(Esc(N(x) + " / " + N(r))).Append("</span></div>");
            }
            sb.Append("</div><p class=\"muted\" style=\"font-size:12.5px;margin:10px 0 0\">Balken = Anteil fehlerhafter Läufe.</p>");
        }
        sb.Append("</div></div>");
    }

    void OpenItemsSection(StringBuilder sb, WebSession s)
    {
        if (OpenItems == null) return;
        List<JObject> open, done;
        try { open = OpenItems.Open(); done = OpenItems.RecentlyDone(14); }
        catch (Exception ex) { sb.Append("<h2 id=\"punkte\">Offene Punkte</h2><div class=\"callout warn\">").Append(Esc(ex.Message)).Append("</div>"); return; }
        string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        sb.Append("<h2 id=\"punkte\">Offene Punkte <span class=\"muted\" style=\"text-transform:none;letter-spacing:0;font-weight:600\">").Append(open.Count).Append(" offen</span></h2>");
        if (open.Count == 0) sb.Append("<p class=\"muted\">Keine offenen Punkte. Claude legt sie mit <code>acmp_open_item_set</code> an, oder hier unten per Formular.</p>");
        else
        {
            sb.Append("<div class=\"tablewrap\"><table><tr><th>Prio</th><th>Punkt</th><th>Bereich</th><th>Von</th><th>Seit</th><th></th></tr>");
            foreach (JObject p in open)
            {
                string prio = AcmpOpenItems.Str(p, "prio") ?? "normal";
                string due = AcmpOpenItems.Str(p, "faellig");
                sb.Append("<tr><td><span class=\"bf ").Append(prio == "hoch" ? "crit" : prio == "niedrig" ? "grey" : "blue").Append("\">").Append(Esc(prio)).Append("</span></td>");
                sb.Append("<td><b>").Append(Esc(AcmpOpenItems.Str(p, "titel"))).Append("</b>");
                string det = AcmpOpenItems.Str(p, "details");
                if (!string.IsNullOrEmpty(det)) sb.Append("<br><small>").Append(Esc(det).Replace("\n", "<br>")).Append("</small>");
                string link = AcmpOpenItems.Str(p, "link");
                if (!string.IsNullOrEmpty(link))
                {
                    if (link.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || link.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                        sb.Append("<br><small><a href=\"").Append(Esc(link)).Append("\" target=\"_blank\" rel=\"noopener noreferrer\">").Append(Esc(Cut(link, 80))).Append("</a></small>");
                    else if (link.StartsWith("/") && !link.StartsWith("//") && IsWebPath(PathOnly(link)))   // Seite dieser Weboberflaeche, z.B. ein Rollout-Stand
                        sb.Append("<br><small><a href=\"").Append(Esc(link)).Append("\">").Append(Esc(Cut(link, 80))).Append("</a></small>");
                    else sb.Append("<br><small><code>").Append(Esc(link)).Append("</code></small>");
                }
                sb.Append("</td><td>").Append(Esc(AcmpOpenItems.Str(p, "bereich"))).Append("</td>");
                sb.Append("<td>").Append(Esc(AcmpOpenItems.Str(p, "von")));
                string ses = AcmpOpenItems.Str(p, "sitzung");
                if (!string.IsNullOrEmpty(ses)) sb.Append("<br><small>").Append(Esc(ses)).Append("</small>");
                sb.Append("</td><td class=\"num\">").Append(Esc(DateShort(AcmpOpenItems.Str(p, "seit"))));
                if (due != null) sb.Append("<br><small").Append(string.CompareOrdinal(due, today) < 0 ? " class=\"due\"" : "").Append(">fällig ").Append(Esc(DateShort(due))).Append("</small>");
                sb.Append("</td><td><form class=\"inline\" method=\"post\" action=\"/punkt-erledigt\">").Append(CsrfField(s))
                  .Append("<input type=\"hidden\" name=\"id\" value=\"").Append(Esc(AcmpOpenItems.Str(p, "id"))).Append("\">")
                  .Append("<button class=\"btn btn-ok btn-sm\" type=\"submit\">Erledigt</button></form></td></tr>");
            }
            sb.Append("</table></div>");
        }

        sb.Append("<details class=\"keep\"><summary>Neuen Punkt anlegen</summary><form class=\"newform\" method=\"post\" action=\"/punkt-neu\">").Append(CsrfField(s));
        sb.Append("<label for=\"op-titel\">Titel<br><input type=\"text\" id=\"op-titel\" name=\"titel\" minlength=\"3\" maxlength=\"200\" required></label>");
        sb.Append("<div class=\"row\"><label for=\"op-bereich\">Bereich <select id=\"op-bereich\" name=\"bereich\">");
        foreach (string a in AcmpOpenItems.Areas) sb.Append("<option").Append(a == "Sonstiges" ? " selected" : "").Append(">").Append(Esc(a)).Append("</option>");
        sb.Append("</select></label><label for=\"op-prio\">Prio <select id=\"op-prio\" name=\"prio\">");
        foreach (string pr in AcmpOpenItems.Prios) sb.Append("<option").Append(pr == "normal" ? " selected" : "").Append(">").Append(pr).Append("</option>");
        sb.Append("</select></label><label for=\"op-faellig\">fällig <input type=\"date\" id=\"op-faellig\" name=\"faellig\"></label></div>");
        sb.Append("<label for=\"op-details\">Details (optional, keine Kennwörter)<br><textarea id=\"op-details\" name=\"details\" maxlength=\"2000\"></textarea></label>");
        sb.Append("<div><button class=\"btn btn-primary btn-sm\" type=\"submit\">Anlegen</button></div></form></details>");

        if (done.Count > 0)
        {
            sb.Append("<details class=\"keep\"><summary>Erledigt in den letzten 14 Tagen (").Append(done.Count).Append(")</summary>");
            sb.Append("<div class=\"tablewrap\" style=\"margin-top:10px\"><table><tr><th>Punkt</th><th>Erledigt</th><th></th></tr>");
            foreach (JObject p in done)
            {
                sb.Append("<tr><td><b>").Append(Esc(AcmpOpenItems.Str(p, "titel"))).Append("</b>");
                string note = AcmpOpenItems.Str(p, "erledigtNotiz");
                if (!string.IsNullOrEmpty(note)) sb.Append("<br><small>").Append(Esc(note)).Append("</small>");
                sb.Append("</td><td class=\"num\">").Append(Esc(TimeShort(AcmpOpenItems.Str(p, "erledigtAm") + ":00"))).Append("<br><small>").Append(Esc(AcmpOpenItems.Str(p, "erledigtVon"))).Append("</small></td>");
                sb.Append("<td><form class=\"inline\" method=\"post\" action=\"/punkt-oeffnen\">").Append(CsrfField(s))
                  .Append("<input type=\"hidden\" name=\"id\" value=\"").Append(Esc(AcmpOpenItems.Str(p, "id"))).Append("\">")
                  .Append("<button class=\"btn btn-ghost btn-sm\" type=\"submit\">Wieder öffnen</button></form></td></tr>");
            }
            sb.Append("</table></div></details>");
        }
    }

    // ---------- Hilfen ----------
    static string DCard(string cls, string k, string v, string small, string sub)
    {
        return "<div class=\"" + (string.IsNullOrEmpty(cls) ? "card" : "card " + cls) + "\"><div class=\"k\">" + Esc(k) + "</div><div class=\"v\">" + Esc(v)
             + (small != null ? " <small>" + Esc(small) + "</small>" : "") + "</div><div class=\"s\">" + Esc(sub) + "</div></div>";
    }

    static long L(JToken t) { return AcmpDashboard.Lng(t); }
    static string N(long n) { return AcmpDashboard.Num(n); }
    static string Cut(string s, int n) { if (s == null) return ""; return s.Length > n ? s.Substring(0, n - 1) + "…" : s; }
    static List<string> StrList(JToken t) { var l = new List<string>(); if (t is JArray) foreach (JToken x in (JArray)t) l.Add((string)x); return l; }

    // "2026-10-23" -> "23.10."
    static string DateShort(string d)
    {
        DateTime x;
        if (d != null && d.Length >= 10 && DateTime.TryParseExact(d.Substring(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out x))
            return x.ToString("dd.MM.", CultureInfo.InvariantCulture);
        return d ?? "";
    }

    // "2026-10-23 06:00:12" -> "06:00" (heute) bzw. "22.10. 06:00"
    static string TimeShort(string z)
    {
        DateTime x;
        if (z != null && z.Length >= 16 && DateTime.TryParseExact(z.Substring(0, 16), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out x))
            return x.Date == DateTime.Today ? x.ToString("HH:mm", CultureInfo.InvariantCulture) : x.ToString("dd.MM. HH:mm", CultureInfo.InvariantCulture);
        return z ?? "";
    }
}
