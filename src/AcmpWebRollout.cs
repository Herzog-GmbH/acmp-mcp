// AcmpWebRollout: Seite "Rollouts" der Weboberflaeche (/dashboard/rollout, seit 23.10.2026). Teil von AcmpWeb (partial).
// Ohne cc: Client Commands mit Auftraegen der letzten Tage. Mit cc=<GUID>: Stand je Rechner (Benutzer, online, Auftrag, letzter Lauf,
// Befund, Kennzeilen wie BUILD), gruppiert nach Ergebnis. Abfrage: cc, seit (JJJJ-MM-TT oder TT.MM.JJJJ), tage (Uebersicht),
// param (NAME:Wert, nur Laeufe mit diesem Parameterwert), zeilen (Kennzeilen als Spalten, mit Komma). Daten: AcmpRollout. Nur lesen.
// net48 / C# 5 (csc v4.0.30319): keine string-Interpolation, keine expression-bodied member, kein ?.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;

partial class AcmpWeb
{
    public const string RolloutPath = "/dashboard/rollout";
    public AcmpRollout Rollouts;   // null = beim ersten Aufruf aus der Leseverbindung des Dashboards anlegen (Tests setzen eine eigene)
    readonly object _rolloutLock = new object();

    const string RolloutCss =
          ".rform{display:flex;flex-wrap:wrap;gap:10px;align-items:flex-end;margin:8px 0 14px}"
        + ".rform label{font-size:12.5px;color:#545e66;font-weight:600;display:flex;flex-direction:column;gap:4px}"
        + ".rform input[type=text]{width:200px}.rform input[type=date]{width:165px}.rform select{max-width:280px}"
        + ".rhead{font-size:13.5px;color:#545e66;margin:-4px 0 6px}.rhead code{font-size:12px}"
        + ".rt td{font-size:13.5px}.rt td small{display:block;line-height:1.45}.rt td.rn{white-space:nowrap}.rt td.bef{min-width:220px}"
        + ".rt tr.main td{border-bottom:none}.rt tr.det td{padding:0 13px 9px}.rt tr.det:hover td{background:transparent}"
        + ".on{color:#15803d;font-weight:700}.off{color:#7a838c;font-weight:600}"
        + ".kz{font-family:'JetBrains Mono',Consolas,monospace;font-size:12px;white-space:nowrap}"
        + "details.runs summary{font-size:12px;font-weight:600}"
        + ".detgrid{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1.4fr);gap:12px;margin-top:6px}"
        + ".detgrid table{box-shadow:none}.detgrid td,.detgrid th{font-size:12.5px;padding:6px 9px}.detgrid pre{margin:0;max-height:340px;font-size:11.5px}"
        + "@media(max-width:680px){.detgrid{grid-template-columns:minmax(0,1fr)}}"
        + "a.chip{text-decoration:none}a.chip:hover{background:#eef2f7}a.chip.sel{background:#00459a;color:#fff;border-color:#00459a}";

    AcmpRollout GetRollouts()
    {
        lock (_rolloutLock)
        {
            if (Rollouts == null && Dashboard != null && !string.IsNullOrEmpty(Dashboard.ReadCs)) Rollouts = new AcmpRollout(Dashboard.ReadCs, _log);
            return Rollouts;
        }
    }

    string RolloutPage(WebSession s, Dictionary<string, string> q)
    {
        var sb = new StringBuilder();
        sb.Append(BrakeBanner());
        AcmpRollout ro = GetRollouts();
        if (ro == null)
        {
            sb.Append("<h1>Rollouts</h1><div class=\"callout warn\">Keine Datenbank-Anbindung (<code>db.connectionString</code> fehlt) – der Rollout-Stand kann nicht angezeigt werden.</div>");
            return PageNav(s, sb.ToString(), "rollout");
        }
        string cc = Get(q, "cc");
        if (!string.IsNullOrEmpty(cc) && cc.Trim().Length > 0) RolloutDetail(sb, ro, cc.Trim(), q);
        else RolloutList(sb, ro, q, null);
        // Im offenen Tab alle 5 Minuten neu laden, nicht waehrend jemand tippt oder Details offen hat
        sb.Append("<script>(function(){function r(){var a=document.activeElement;var busy=a&&/^(INPUT|TEXTAREA|SELECT)$/.test(a.tagName);"
                + "if(busy||document.querySelector('details[open]')){setTimeout(r,60000);return;}location.reload();}setTimeout(r,300000);})();</script>");
        return PageNav(s, sb.ToString(), "rollout");
    }

    // ---------- Uebersicht ----------
    void RolloutList(StringBuilder sb, AcmpRollout ro, Dictionary<string, string> q, string error)
    {
        int days = AcmpRollout.DefaultDays, d;
        if (int.TryParse(Get(q, "tage"), NumberStyles.Integer, CultureInfo.InvariantCulture, out d)) days = Math.Max(1, Math.Min(90, d));
        DateTime since = DateTime.Today.AddDays(-days);
        sb.Append("<h1>Rollouts</h1>");
        if (error != null) sb.Append("<div class=\"callout warn\">").Append(Esc(error)).Append("</div>");
        sb.Append("<p class=\"muted\" style=\"margin-top:0\">Client Commands mit Aufträgen seit ").Append(Esc(since.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)))
          .Append(" – ein Klick zeigt den Stand je Rechner: wer wartet, wer fertig ist, wo es hakt. Nur gelesen aus der ACMP-Datenbank.</p>");
        sb.Append("<form class=\"rform\" method=\"get\" action=\"").Append(RolloutPath).Append("\"><label for=\"ro-tage\">Zeitraum<select id=\"ro-tage\" name=\"tage\">");
        foreach (int t in new[] { 3, 7, 14, 30, 90 }) sb.Append("<option value=\"").Append(t).Append("\"").Append(t == days ? " selected" : "").Append(">").Append(t).Append(" Tage</option>");
        sb.Append("</select></label><button class=\"btn btn-ghost btn-sm\" type=\"submit\">Anzeigen</button></form>");
        JArray list;
        try { list = ro.List(since); }
        catch (Exception ex) { sb.Append("<div class=\"callout warn\">Abfrage fehlgeschlagen: ").Append(Esc(ex.Message)).Append("</div>"); return; }
        if (list.Count == 0) { sb.Append("<div class=\"callout\">Keine Client Commands mit Aufträgen in diesem Zeitraum.</div>"); return; }
        string seit = since.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        sb.Append("<div class=\"tablewrap\"><table class=\"rt\"><tr><th>Client Command</th><th>Rechner</th><th>Erfolg</th><th>Fehler</th><th>Wartet</th><th>Läuft</th><th>Zuletzt</th></tr>");
        foreach (JObject e in list)
        {
            string id = ((string)e["id"]).Trim('{', '}');
            string href = RolloutPath + "?cc=" + Uri.EscapeDataString(id) + "&seit=" + seit;
            long err = L(e["fehler"]), wait = L(e["wartet"]), run = L(e["laeuft"]);
            sb.Append("<tr><td><a href=\"").Append(Esc(href)).Append("\"><b>").Append(Esc((string)e["name"])).Append("</b></a><small class=\"kz muted\">").Append(Esc(id)).Append("</small></td>");
            sb.Append("<td class=\"num\">").Append(N(L(e["rechner"]))).Append("<small>").Append(N(L(e["auftraege"]))).Append(L(e["auftraege"]) == 1 ? " Auftrag" : " Aufträge").Append("</small></td>");
            sb.Append("<td class=\"num\">").Append(N(L(e["ok"]))).Append("</td>");
            sb.Append("<td class=\"num\">").Append(err > 0 ? "<span class=\"bf crit\">" + N(err) + "</span>" : "–").Append("</td>");
            sb.Append("<td class=\"num\">").Append(wait > 0 ? "<span class=\"bf warn\">" + N(wait) + "</span>" : "–").Append("</td>");
            sb.Append("<td class=\"num\">").Append(run > 0 ? "<span class=\"bf blue\">" + N(run) + "</span>" : "–").Append("</td>");
            sb.Append("<td class=\"num\">").Append(Esc(TimeShort((string)e["zuletzt"]))).Append("</td></tr>");
        }
        sb.Append("</table></div>");
        sb.Append("<p class=\"muted\" style=\"font-size:12.5px\">Zahlen = Aufträge im Zeitraum laut Job-Monitor: ein Rechner kann mehrere haben, Daueraufträge laufen wiederholt, ")
          .Append("„wartet“ schließt beim Agenten geplante Läufe ein (Startbedingung, z. B. Wartungsfenster), und ein Prüflauf mit Befund zählt dort als Fehler. Den Stand je Rechner zeigt die Detailseite.</p>");
    }

    // ---------- Stand eines Commands ----------
    void RolloutDetail(StringBuilder sb, AcmpRollout ro, string cc, Dictionary<string, string> q)
    {
        if (!AcmpRollout.GuidRe.IsMatch(cc)) { RolloutList(sb, ro, q, "Ungültige Command-Id: " + AcmpRollout.Cut(cc, 60)); return; }
        string seitWarn = null;
        DateTime since = ParseSince(Get(q, "seit"), out seitWarn);
        string param = Get(q, "param"), zeilen = Get(q, "zeilen");
        if (param != null && param.Trim().Length == 0) param = null;
        if (zeilen != null && zeilen.Trim().Length == 0) zeilen = null;
        JObject r;
        try { r = ro.Get(cc, since, param, zeilen); }
        catch (Exception ex)
        {
            sb.Append("<h1>Rollout</h1><div class=\"callout warn\">Abfrage fehlgeschlagen: ").Append(Esc(ex.Message)).Append("</div><p><a href=\"").Append(RolloutPath).Append("\">&larr; Alle Rollouts</a></p>");
            return;
        }
        string id = ((string)r["cc"]).Trim('{', '}');
        bool mcpfix = r["mcpfix"] != null && (bool)r["mcpfix"];
        string seit = since.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        sb.Append("<p style=\"margin:0 0 6px\"><a href=\"").Append(RolloutPath).Append("\">&larr; Alle Rollouts</a></p>");
        sb.Append("<h1>").Append(Esc(r["name"] != null && r["name"].Type != JTokenType.Null ? (string)r["name"] : "Client Command " + id)).Append("</h1>");
        sb.Append("<div class=\"rhead\"><code>").Append(Esc(id)).Append("</code>");
        long ver = L(r["version"]);
        if (ver >= 0) sb.Append(" · Version ").Append(ver).Append(L(r["ccState"]) == 7 ? " (freigegeben)" : L(r["ccState"]) == 1 ? " (Entwurf)" : "");
        sb.Append(" · seit ").Append(Esc(since.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)));
        sb.Append(" · ").Append(N(L(r["rechnerGesamt"]))).Append(" Rechner, ").Append(N(L(r["laeufeGesamt"]))).Append(" Läufe, ").Append(N(L(r["auftraegeGesamt"]))).Append(" Aufträge");
        sb.Append(" · Datenstand ").Append(Esc(TimeShort((string)r["stand"])));
        if (r["dauerMs"] != null) sb.Append(" <small style=\"display:inline\">(").Append(Esc((AcmpDashboard.Dbl(r["dauerMs"]) / 1000.0).ToString("0.0", CultureInfo.GetCultureInfo("de-DE")))).Append(" s)</small>");
        sb.Append("</div>");
        if (seitWarn != null) sb.Append("<div class=\"callout warn\">").Append(Esc(seitWarn)).Append("</div>");
        if (r["abgeschnitten"] != null && (bool)r["abgeschnitten"])
            sb.Append("<div class=\"callout warn\">Mehr als ").Append(N(AcmpRollout.MaxRuns)).Append(" Läufe seit diesem Datum – nur die neuesten ausgewertet. Ein späteres „seit“ wählen.</div>");

        // Filterformular (GET)
        sb.Append("<form class=\"rform\" method=\"get\" action=\"").Append(RolloutPath).Append("\"><input type=\"hidden\" name=\"cc\" value=\"").Append(Esc(id)).Append("\">");
        sb.Append("<label for=\"ro-seit\">seit<input type=\"date\" id=\"ro-seit\" name=\"seit\" value=\"").Append(seit).Append("\"></label>");
        JArray pw = r["paramWerte"] as JArray;
        if (pw != null && pw.Count > 0)
        {
            sb.Append("<label for=\"ro-param\">Läufe<select id=\"ro-param\" name=\"param\"><option value=\"\">alle</option>");
            foreach (JObject p in pw)
            {
                string v = (string)p["name"] + ":" + (string)p["wert"];
                bool sel = param != null && string.Equals(param.Trim(), v, StringComparison.OrdinalIgnoreCase);
                sb.Append("<option value=\"").Append(Esc(v)).Append("\"").Append(sel ? " selected" : "").Append(">nur ").Append(Esc((string)p["name"] + " = " + (string)p["wert"]))
                  .Append(" (").Append(N(L(p["laeufe"]))).Append(")</option>");
            }
            sb.Append("</select></label>");
        }
        else if (param != null) sb.Append("<input type=\"hidden\" name=\"param\" value=\"").Append(Esc(param)).Append("\">");
        sb.Append("<label for=\"ro-zeilen\">Kennzeilen als Spalten<input type=\"text\" id=\"ro-zeilen\" name=\"zeilen\" value=\"").Append(Esc(zeilen ?? "")).Append("\" placeholder=\"automatisch, z.B. BUILD,VORHER\" maxlength=\"120\"></label>");
        sb.Append("<button class=\"btn btn-ghost btn-sm\" type=\"submit\">Anzeigen</button></form>");

        JArray kz = r["kennzeilen"] as JArray;
        var cols = new List<string>();
        if (r["spalten"] is JArray) foreach (JToken t in (JArray)r["spalten"]) cols.Add((string)t);
        if (kz != null && kz.Count > 0)
        {
            sb.Append("<div class=\"chips\" style=\"margin:-4px 0 12px\"><span class=\"muted\" style=\"font-size:12.5px\">Kennzeilen in der Ausgabe:</span>");
            foreach (JObject k in kz)
            {
                string key = (string)k["key"];
                string href = RolloutLink(id, seit, param, key);
                sb.Append("<a class=\"chip").Append(cols.Contains(key) ? " sel" : "").Append("\" href=\"").Append(Esc(href)).Append("\" title=\"in ").Append(L(k["laeufe"])).Append(" letzten Läufen")
                  .Append(k["mehrfach"] != null && (bool)k["mehrfach"] ? ", mehrere je Lauf" : "").Append("\">").Append(Esc(key)).Append("</a>");
            }
            sb.Append("</div>");
        }
        JObject fo = r["filter"] as JObject;
        if (fo != null)
        {
            sb.Append("<div class=\"callout\">Nur Läufe mit <b>").Append(Esc((string)fo["name"] + " = " + (string)fo["wert"])).Append("</b>");
            if (L(r["herausgefiltert"]) > 0) sb.Append(" – ").Append(N(L(r["herausgefiltert"]))).Append(" andere Läufe ausgeblendet").Append(L(r["rechnerHerausgefiltert"]) > 0 ? ", " + N(L(r["rechnerHerausgefiltert"])) + " Rechner nur mit solchen" : "");
            sb.Append(". Wartende Aufträge zählen immer mit: ihr Parameterwert steht erst im Protokoll des Laufs.</div>");
        }
        if (mcpfix)
            sb.Append("<p class=\"muted\" style=\"font-size:12.5px;margin:0 0 6px\">MCP-Skript (Kopfzeile <code>MCPFIX</code>): Befund aus Modus, Exit-Code und den Zeilen ABBRUCH, FEHLER, FERTIG und KOPIERT. Prüfen: Exit 0 in Ordnung, 1 betroffen. Fix: Exit 0 mit Änderung = repariert.</p>");

        // Kacheln je Gruppe
        JObject gc = r["gruppen"] as JObject;
        sb.Append("<div class=\"cards\">");
        foreach (string g in AcmpRollout.Groups)
        {
            long n = gc != null ? L(gc[g]) : 0;
            if (n == 0 && g != "fehler" && g != "ok") continue;
            sb.Append(Card(n > 0 ? "#g-" + g : null, n == 0 ? "" : GroupClass(g), GroupLabel(g, mcpfix), N(n), GroupHint(g, mcpfix)));
        }
        sb.Append("</div>");

        JArray rows = r["rechner"] as JArray;
        if (rows == null || rows.Count == 0)
        {
            sb.Append("<div class=\"callout\">Keine Aufträge und keine Läufe seit ").Append(Esc(since.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture))).Append(".</div>");
            return;
        }
        foreach (string g in AcmpRollout.Groups)
        {
            var part = new List<JObject>();
            foreach (JObject row in rows) if ((string)row["gruppe"] == g) part.Add(row);
            if (part.Count == 0) continue;
            sb.Append("<h2 id=\"g-").Append(g).Append("\">").Append(Esc(GroupLabel(g, mcpfix))).Append(" <span class=\"muted\" style=\"text-transform:none;letter-spacing:0;font-weight:600\">").Append(part.Count).Append("</span></h2>");
            sb.Append("<div class=\"tablewrap\"><table class=\"rt\"><tr><th>Rechner</th><th>Benutzer</th><th>Online</th><th>Auftrag</th><th>Letzter Lauf</th><th>Befund</th>");
            foreach (string c in cols) sb.Append("<th>").Append(Esc(c)).Append("</th>");
            sb.Append("</tr>");
            foreach (JObject row in part) sb.Append(RolloutRow(row, g, cols));
            sb.Append("</table></div>");
        }
        sb.Append("<p class=\"muted\" style=\"font-size:12.5px;margin-top:18px\">Link für offene Punkte: <code>").Append(Esc(RolloutLink(id, seit, param, zeilen))).Append("</code> · ")
          .Append("Benutzer = zuletzt angemeldet laut ACMP. Ein Auftrag ohne Lauf zählt nur, wenn er nach dem letzten Lauf angelegt wurde.</p>");
    }

    // Eine Zeile je Rechner; Verlauf und Ausgabe darunter in einer aufklappbaren Zeile ueber die ganze Breite
    string RolloutRow(JObject row, string g, List<string> cols)
    {
        var sb = new StringBuilder();
        JArray hist = row["verlauf"] as JArray;
        JArray outp = row["ausgabe"] as JArray;
        bool det = (hist != null && hist.Count > 0) || (outp != null && outp.Count > 0);
        sb.Append(det ? "<tr class=\"main\">" : "<tr>").Append("<td class=\"rn\"><b>").Append(Esc((string)row["name"])).Append("</b>");
        if (row["ausgemustert"] != null) sb.Append("<small>ausgemustert</small>");
        sb.Append("</td><td>").Append(Esc(row["benutzer"] != null && row["benutzer"].Type != JTokenType.Null ? (string)row["benutzer"] : "")).Append("</td>");
        long on = L(row["online"]);
        string lc = row["letzterKontakt"] != null && row["letzterKontakt"].Type != JTokenType.Null ? (string)row["letzterKontakt"] : null;
        sb.Append("<td class=\"nowrap\">").Append(on == 0 ? "<span class=\"on\">online</span>" : on == 1 ? "<span class=\"off\">offline</span>" : "<span class=\"off\">unbekannt</span>");
        if (on != 0 && lc != null) sb.Append("<small>zuletzt ").Append(Esc(TimeShort(lc))).Append("</small>");
        sb.Append("</td>");

        JObject a = row["auftrag"] as JObject;
        sb.Append("<td class=\"nowrap\">");
        if (a != null)
        {
            sb.Append(Esc((string)a["zustand"])).Append(a["ergebnis"] != null ? " · " + Esc((string)a["ergebnis"]) : "");
            sb.Append("<small>eingereiht ").Append(Esc(TimeShort((string)a["eingereiht"]))).Append(a["von"] != null && a["von"].Type != JTokenType.Null ? " von " + Esc((string)a["von"]) : "").Append("</small>");
            if (a["bedingung"] != null && (g == "wartetOnline" || g == "wartetOffline" || g == "laeuft"))
                sb.Append("<small title=\"").Append(Esc((string)a["bedingung"])).Append("\">").Append(Esc(AcmpRollout.Cut((string)a["bedingung"], 50))).Append("</small>");
        }
        else sb.Append("–");
        if (row["offen"] != null) sb.Append("<small>dazu ").Append(L(row["offen"])).Append(L(row["offen"]) == 1 ? " älterer Auftrag offen" : " ältere Aufträge offen").Append("</small>");
        sb.Append("</td>");

        JObject lauf = row["lauf"] as JObject;
        sb.Append("<td class=\"nowrap\">");
        if (lauf != null)
        {
            sb.Append(Esc(TimeShort((string)lauf["zeit"])));
            sb.Append("<small>").Append(Esc(RunSummary(lauf))).Append("</small>");
        }
        else sb.Append("<span class=\"muted\">noch keiner</span>");
        sb.Append("</td>");

        sb.Append("<td class=\"bef\"><span class=\"bf ").Append(BefundClass(g)).Append("\">").Append(Esc(ShortBefund(g, (string)row["befund"]))).Append("</span><small>").Append(Esc((string)row["befundText"])).Append("</small>");
        if (a != null && a["meldung"] != null && g != "ok" && g != "repariert")
        {
            string msg = (string)a["meldung"];
            sb.Append("<small title=\"").Append(Esc(msg)).Append("\">").Append(Esc(AcmpRollout.Cut(msg, 70))).Append("</small>");
        }
        if (row["protokollFehler"] != null) sb.Append("<small class=\"muted\">").Append(Esc((string)row["protokollFehler"])).Append("</small>");
        sb.Append("</td>");

        JObject z = row["zeilen"] as JObject;
        foreach (string c in cols)
        {
            string v = z != null && z[c] != null ? ((string)z[c]).Replace("|", " · ") : "";
            sb.Append("<td class=\"kz\" title=\"").Append(Esc(v)).Append("\">").Append(Esc(AcmpRollout.Cut(v, 60))).Append("</td>");
        }
        sb.Append("</tr>");
        if (!det) return sb.ToString();

        long nRuns = L(row["laeufe"]);
        sb.Append("<tr class=\"det\"><td colspan=\"").Append(6 + cols.Count).Append("\"><details class=\"runs\"><summary>Verlauf und Ausgabe · ")
          .Append(nRuns).Append(nRuns == 1 ? " Lauf" : " Läufe").Append("</summary><div class=\"detgrid\"><div>");
        if (hist != null && hist.Count > 0)
        {
            sb.Append("<table><tr><th>Zeit</th><th>Lauf</th><th>Befund</th></tr>");
            foreach (JObject h in hist)
                sb.Append("<tr><td class=\"nowrap\">").Append(Esc(TimeShort((string)h["zeit"]))).Append("</td><td class=\"nowrap\">").Append(Esc(RunSummary(h)))
                  .Append("</td><td>").Append(Esc((string)h["befundText"])).Append("</td></tr>");
            sb.Append("</table>");
            if (nRuns > hist.Count) sb.Append("<small class=\"muted\">").Append(nRuns - hist.Count).Append(" ältere nicht gezeigt</small>");
        }
        sb.Append("</div><div>");
        if (outp != null && outp.Count > 0)
        {
            var lines = new List<string>(); foreach (JToken t in outp) lines.Add((string)t);
            sb.Append("<pre>").Append(Esc(string.Join("\n", lines.ToArray())));
            if (row["ausgabeMehr"] != null) sb.Append("\n… ").Append(L(row["ausgabeMehr"])).Append(" weitere Zeilen");
            sb.Append("</pre>");
        }
        else sb.Append("<small class=\"muted\">keine Ausgabe im Protokoll des letzten Laufs</small>");
        sb.Append("</div></div></details></td></tr>");
        return sb.ToString();
    }

    // "MODUS=Fix · Exit 0" bzw. "Code 1001 · Exit 1"
    static string RunSummary(JObject lauf)
    {
        var parts = new List<string>();
        JObject p = lauf["param"] as JObject;
        if (p != null) foreach (JProperty x in p.Properties()) parts.Add(x.Name + "=" + (string)x.Value);
        if (lauf["exit"] != null) parts.Add("Exit " + L(lauf["exit"]));
        if (L(lauf["code"]) != 0 || lauf["exit"] == null) parts.Add("Code " + L(lauf["code"]));
        if (lauf["version"] != null && L(lauf["version"]) > 0) parts.Add("v" + L(lauf["version"]));
        return string.Join(" · ", parts.ToArray());
    }

    static string RolloutLink(string id, string seit, string param, string zeilen)
    {
        string s = RolloutPath + "?cc=" + Uri.EscapeDataString(id) + "&seit=" + seit;
        if (!string.IsNullOrEmpty(param)) s += "&param=" + Uri.EscapeDataString(param);
        if (!string.IsNullOrEmpty(zeilen)) s += "&zeilen=" + Uri.EscapeDataString(zeilen);
        return s;
    }

    // "2026-10-22", "22.10.2026", "22.10." -> Datum; leer = heute minus 14 Tage; hoechstens ein Jahr zurueck, nicht in der Zukunft
    static DateTime ParseSince(string v, out string warn)
    {
        warn = null;
        DateTime def = DateTime.Today.AddDays(-AcmpRollout.DefaultDays), x;
        if (string.IsNullOrEmpty(v) || v.Trim().Length == 0) return def;
        v = v.Trim();
        if (v.Length == 6 && v.EndsWith(".")) v += DateTime.Today.Year.ToString(CultureInfo.InvariantCulture);
        if (!DateTime.TryParseExact(v, new[] { "yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out x))
        {
            warn = "„seit“ nicht lesbar (" + AcmpRollout.Cut(v, 20) + ") – gezeigt wird ab " + def.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) + ".";
            return def;
        }
        if (x > DateTime.Today) x = DateTime.Today;
        if (x < DateTime.Today.AddDays(-366)) { x = DateTime.Today.AddDays(-366); warn = "„seit“ höchstens ein Jahr zurück."; }
        return x;
    }

    static string GroupLabel(string g, bool mcpfix)
    {
        switch (g)
        {
            case "fehler": return "Fehler oder Abbruch";
            case "betroffen": return "Betroffen, nur geprüft";
            case "laeuft": return "Läuft gerade";
            case "wartetOnline": return "Wartet – Rechner online";
            case "wartetOffline": return "Wartet – Rechner offline";
            case "repariert": return "Repariert";
            default: return mcpfix ? "In Ordnung" : "Erfolgreich";
        }
    }

    static string GroupHint(string g, bool mcpfix)
    {
        switch (g)
        {
            case "fehler": return "letzter Lauf mit Fehler, Abbruch oder Timeout";
            case "betroffen": return "Prüflauf meldet betroffen – Fix einreihen";
            case "laeuft": return "an den Agenten übergeben oder läuft";
            case "wartetOnline": return "eingereiht, Rechner online – sollte bald laufen";
            case "wartetOffline": return "eingereiht, läuft beim nächsten Kontakt";
            case "repariert": return "letzter Fix-Lauf hat etwas geändert";
            default: return mcpfix ? "letzter Lauf: alles in Ordnung" : "letzter Lauf erfolgreich";
        }
    }

    static string GroupClass(string g)
    {
        if (g == "fehler") return "alert";
        if (g == "betroffen" || g == "wartetOnline") return "warnc";
        if (g == "ok" || g == "repariert") return "ok";
        return "";
    }

    static string BefundClass(string g)
    {
        if (g == "fehler") return "crit";
        if (g == "betroffen" || g == "wartetOnline") return "warn";
        if (g == "ok" || g == "repariert" || g == "laeuft") return "blue";
        return "grey";
    }

    static string ShortBefund(string g, string befund)
    {
        switch (befund)
        {
            case "abbruch": return "Abbruch";
            case "fehler": return "Fehler";
            case "betroffen": return "betroffen";
            case "repariert": return "repariert";
            case "ok": return "in Ordnung";
            case "erfolg": return "Erfolg";
            case "laeuft": return "läuft";
            case "wartet": return g == "wartetOnline" ? "wartet (online)" : "wartet (offline)";
            default: return befund ?? "?";
        }
    }
}
