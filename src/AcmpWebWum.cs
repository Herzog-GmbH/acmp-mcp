// AcmpWebWum: Seite "Updates" der Weboberflaeche (/dashboard/updates, seit 23.10.2026). Teil von AcmpWeb (partial).
// Zeigt den Fehler-Stopp der Windows-Updates: Stand des Waechters, angehaltene Updates mit Entscheidung (weiterlaufen lassen, sofort
// freigeben), Updates im Testring mit dem Stand je Testrechner, Meldungen, kuerzlich freigegebene Updates mit Fehlschlaegen,
// Ring-Konfiguration und Test-Jobs. Daten: AcmpWumRings (DB, 60 s Zwischenstand), AcmpWumWatch (wum-waechter.json).
// Schreibaktionen (POST + csrf): /wum-ring (anhalten, weiterlaufen lassen, sofort freigeben - nur Bediener in wumWriter.allowedRequesters,
// gesperrt bei gezogener Notbremse, im Aktivitaetsprotokoll), /wum-pruefen (Waechter jetzt laufen lassen), /wum-testmail.
// net48 / C# 5 (csc v4.0.30319): keine string-Interpolation, keine expression-bodied member, kein ?.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

partial class AcmpWeb
{
    public const string UpdatesPath = "/dashboard/updates";
    public AcmpWumRings Wum;          // null = keine DB-Anbindung
    public AcmpWumWatch WumWatch;     // null = kein Waechter (nur in Tests)

    const string WumCss =
          ".bf.ok{background:#e7f6ec;color:#15803d}"
        + ".wt td{font-size:13.5px;vertical-align:top}.wt td small{display:block;line-height:1.45}.wt td.nm{min-width:260px}"
        + ".tl{list-style:none;margin:0;padding:0;display:grid;gap:2px;font-size:12.5px}.tl .x{color:#c81e1e;font-weight:700}.tl .y{color:#15803d;font-weight:700}.tl .z{color:#b45309;font-weight:700}.tl .w{color:#7a838c}"
        + "form.dec{display:flex;flex-wrap:wrap;gap:6px;align-items:center;margin:0 0 6px}form.dec input[type=text]{width:220px;padding:6px 9px;font-size:13px}"
        + "form.dec label{font-size:12px;color:#545e66;display:flex;gap:4px;align-items:center}"
        + ".rules{font-size:13.5px;color:#545e66;line-height:1.6;margin:0 0 14px}";

    // ---------- Aktionen ----------
    bool RouteWum(string path, string method, Dictionary<string, string> f, WebSession s, WebResult res)
    {
        if (path != "/wum-ring" && path != "/wum-pruefen" && path != "/wum-testmail") return false;
        if (method != "POST") { Redirect(res, UpdatesPath); return true; }
        if (Wum == null || !Wum.CanWebWrite(s.Name)) { res.Code = 403; res.Body = Notice(s, "Nicht erlaubt", "Wächter starten, Testmail und Update-Ringe ändern dürfen nur Bediener in wumWriter.allowedRequesters (bzw. ccWriter.allowedRequesters).", "updates"); return true; }
        if (path == "/wum-pruefen")
        {
            if (WumWatch == null || !WumWatch.Enabled) { res.Body = Notice(s, "Nicht möglich", "Der Update-Wächter ist aus" + (WumWatch != null ? " (" + WumWatch.DisabledReason + ")" : "") + ".", "updates"); return true; }
            try
            {
                JObject r = WumWatch.RunNow("Webseite " + s.Name);
                _act.Record(s.Name, "Update-Wächter", "Webseite", "Wächter jetzt laufen lassen", null, null, true, r.ToString(Formatting.None), 0);
                Redirect(res, UpdatesPath + "?geprueft=1");
            }
            catch (Exception ex) { res.Code = 409; res.Body = Notice(s, "Wächter nicht gelaufen", ex.Message, "updates"); }
            return true;
        }
        if (path == "/wum-testmail")
        {
            if (WumWatch == null) { Redirect(res, UpdatesPath); return true; }
            string r = WumWatch.TestMail(s.Name);
            _act.Record(s.Name, "Update-Wächter", "Webseite", "Testmail", null, null, r.Contains("gesendet"), r, 0);
            res.Body = Notice(s, "Testmail", r, "updates");
            return true;
        }
        // /wum-ring: anhalten, weiterlaufen lassen, sofort freigeben
        string action = Get(f, "action"), id = Get(f, "id"), comment = Get(f, "comment");
        if (_act.BrakeActive) { res.Code = 423; res.Body = Notice(s, "Notbremse gezogen", "Solange die Notbremse gezogen ist, ändert die Webseite keine Update-Ringe.", "updates"); return true; }
        string tool = action == "hold" ? "acmp_wum_ring_hold" : "acmp_wum_ring_release";
        var a = new JObject();
        a["updateId"] = id;
        a["comment"] = comment;
        if (action == "resume" || action == "now") a["mode"] = action;
        else if (action != "hold") { Redirect(res, UpdatesPath); return true; }
        if (action == "now" && Get(f, "force") == "1") a["force"] = true;
        string kind = action == "hold" ? "Write" : "Destructive";
        string intent = "Webseite: " + (action == "hold" ? "anhalten" : action == "resume" ? "weiterlaufen lassen" : "sofort freigeben") + " - " + AcmpOpenItems.Mask(comment ?? "");
        string target = "updateId=" + (id ?? "");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        JObject plan;
        try { plan = Wum.Plan(tool, a, s.Name); }
        catch (Exception ex)
        {
            _act.Record(s.Name, tool, kind, intent, target, null, false, ex.Message, sw.ElapsedMilliseconds);
            res.Code = 400; res.Body = Notice(s, "Nicht geändert", ex.Message, "updates");
            return true;
        }
        // Start-Eintrag vor dem Schreiben: nicht schreibbar -> nichts geschrieben
        string aid;
        try { aid = _act.Begin(s.Name, tool, kind, intent, target, null); }
        catch (Exception ex) { res.Code = 503; res.Body = Notice(s, "Nicht geändert", ex.Message, "updates"); return true; }
        try
        {
            var p = new JObject(); p["_plan"] = plan;
            JObject r = Wum.Execute(tool, p, s.Name, false);
            _act.End(aid, s.Name, tool, kind, intent, target, null, true, r.ToString(Formatting.None), sw.ElapsedMilliseconds);
            _log("Webseite: " + tool + " (" + action + ") durch " + s.Name + " an " + id);
            string bare = (AcmpWumRings.GuidBraces(id) ?? "").Trim('{', '}');
            bool kept = plan["items"] is JArray && ((JArray)plan["items"]).Count > 0 && ((JArray)plan["items"])[0]["takeOver"] != null;
            Redirect(res, UpdatesPath + "?ok=" + Uri.EscapeDataString(kept ? "keep" : action) + "#u-" + bare);
        }
        catch (Exception ex)
        {
            _act.End(aid, s.Name, tool, kind, intent, target, null, false, ex.Message, sw.ElapsedMilliseconds);
            res.Code = 400; res.Body = Notice(s, "Nicht geändert", ex.Message, "updates");
        }
        return true;
    }

    // ---------- Seite ----------
    string UpdatesPage(WebSession s, Dictionary<string, string> q)
    {
        var sb = new StringBuilder();
        sb.Append(BrakeBanner());
        sb.Append("<h1>Update-Ringe</h1>");
        sb.Append("<p class=\"rules\">Windows-Updates gehen vom <b>Testring</b> zum <b>Freigabering</b>. Der Wächter hält ein Update an, wenn es auf einem Testrechner mit einem echten Fehler scheitert (S1) ")
          .Append("oder kurz vor der Weitergabe noch auf keinem Testrechner installiert ist (S2). Vorübergehende Fehler – Office offen, Herunterfahren, Download – zählen nicht. ")
          .Append("Ein Stopp wegen „ungetestet“ löst sich von selbst, sobald ein Testrechner das Update hat; ein Stopp wegen Fehler entscheidet ein Mensch.</p>");
        string okp = Get(q, "ok");
        if (okp == "hold") sb.Append("<div class=\"callout ok\">Angehalten – das Update bleibt im Testring.</div>");
        else if (okp == "keep") sb.Append("<div class=\"callout ok\">Übernommen – das Update bleibt angehalten, auch wenn ein Testrechner es inzwischen hat.</div>");
        else if (okp == "resume") sb.Append("<div class=\"callout ok\">Läuft wieder automatisch weiter.</div>");
        else if (okp == "now") sb.Append("<div class=\"callout ok\">Freigegeben – der Freigabering bekommt das Update beim nächsten Lauf seines Jobs.</div>");
        if (Get(q, "geprueft") == "1") sb.Append("<div class=\"callout ok\">Der Wächter ist gerade gelaufen.</div>");
        if (Wum == null || !Wum.CanRead)
        {
            sb.Append("<div class=\"callout warn\">Keine Datenbank-Anbindung – der Stand kann nicht angezeigt werden.</div>");
            return PageNav(s, sb.ToString(), "updates");
        }
        bool canWrite = Wum.CanWebWrite(s.Name) && !_act.BrakeActive;
        WatchStrip(sb, s);

        AcmpWumRings.Evaluation ev;
        try
        {
            AcmpWumRings.Snapshot snap = Wum.Load(60);
            ev = AcmpWumRings.Evaluate(snap, Wum.Opt, Wum.ResumeOf, WumWatch != null ? WumWatch.HoldsByWatcher() : null);
        }
        catch (Exception ex) { sb.Append("<div class=\"callout warn\">Abfrage fehlgeschlagen: ").Append(Esc(ex.Message)).Append("</div>"); return PageNav(s, sb.ToString(), "updates"); }

        var stopped = new List<AcmpWumRings.Verdict>(); var active = new List<AcmpWumRings.Verdict>(); var quiet = new List<AcmpWumRings.Verdict>();
        foreach (AcmpWumRings.Verdict v in ev.Verdicts)
        {
            if (v.Status == "gestoppt") stopped.Add(v);
            else if (v.Status == "kein-testrechner" || v.Status == "ausgenommen" || v.Status == "erledigt") quiet.Add(v);
            else active.Add(v);
        }

        // Angehalten
        sb.Append("<h2>Angehalten <span class=\"muted\" style=\"text-transform:none;letter-spacing:0;font-weight:600\">").Append(stopped.Count).Append("</span></h2>");
        if (stopped.Count == 0) sb.Append("<div class=\"callout ok\">Kein Update angehalten.</div>");
        else
        {
            sb.Append("<div class=\"tablewrap\"><table class=\"wt\"><tr><th>Update</th><th>Ring</th><th>Grund</th><th>Testrechner</th><th>Entscheidung</th></tr>");
            foreach (AcmpWumRings.Verdict v in stopped)
            {
                string bare = v.It.Id.Trim('{', '}');
                JObject hi = WumWatch != null ? WumWatch.HoldInfo(bare.ToUpperInvariant()) : null;
                bool byWatcher = hi != null && (string)hi["by"] == AcmpWumWatch.Who;
                bool failing = v.Status == "fehler" || v.Hard > 0 || v.UpgradeS1 || (v.HardDefectOnly > 0 && v.Ok == 0);
                sb.Append("<tr id=\"u-").Append(Esc(bare)).Append("\">");
                UpdateCell(sb, v);
                sb.Append("<td class=\"num\">").Append(Esc(AcmpWumRings.RingText(v.It.Ring))).Append("<small>seit ").Append(Esc(v.It.LastChange.ToString("dd.MM. HH:mm", CultureInfo.InvariantCulture))).Append("</small></td>");
                sb.Append("<td>");
                if (hi != null)
                    sb.Append("<b>").Append(byWatcher ? "Wächter" : Esc((string)hi["by"])).Append("</b>, ").Append(Esc(WShort((string)hi["at"]))).Append("<small>").Append(Esc((string)hi["reason"] ?? "")).Append("</small>");
                else sb.Append("Übergang steht auf manuell<small>in der Console oder vor dem Wächter angehalten</small>");
                if (v.CanAutoResume) sb.Append("<small><span class=\"bf ok\">getestet</span> läuft beim nächsten Wächter-Lauf weiter – „Behalten“ verhindert das</small>");
                if (v.UpgradeS1) sb.Append("<small><span class=\"bf crit\">scheitert inzwischen</span> bleibt angehalten, ein Mensch entscheidet</small>");
                sb.Append("</td><td>").Append(TesterList(v)).Append("</td><td>");
                if (canWrite)
                {
                    DecisionForm(sb, s, bare, "resume", "Weiterlaufen lassen", "btn-ghost", false);
                    DecisionForm(sb, s, bare, "now", "Sofort freigeben", "btn-stop", failing);
                    if (byWatcher) DecisionForm(sb, s, bare, "hold", "Behalten", "btn-ghost", false);
                }
                else sb.Append(NoWrite(s));
                sb.Append("</td></tr>");
            }
            sb.Append("</table></div>");
        }

        // Im Testring
        sb.Append("<h2>Im Testring <span class=\"muted\" style=\"text-transform:none;letter-spacing:0;font-weight:600\">").Append(active.Count).Append("</span></h2>");
        if (active.Count == 0) sb.Append("<div class=\"callout\">Kein Update im Test, das ein Testrechner braucht.</div>");
        else
        {
            sb.Append("<div class=\"tablewrap\"><table class=\"wt\"><tr><th>Update</th><th>Weitergabe</th><th>Stand</th><th>Testrechner</th><th>Aktion</th></tr>");
            foreach (AcmpWumRings.Verdict v in active)
            {
                string bare = v.It.Id.Trim('{', '}');
                sb.Append("<tr id=\"u-").Append(Esc(bare)).Append("\">");
                UpdateCell(sb, v);
                sb.Append("<td class=\"num\">").Append(v.ReleaseAt.HasValue ? Esc(v.ReleaseAt.Value.ToString("dd.MM. HH:mm", CultureInfo.InvariantCulture)) : "–")
                  .Append("<small>").Append(Esc(AcmpWumRings.RingText(v.It.Ring))).Append(", ").Append(Esc(v.It.Config ?? "?")).Append("</small></td>");
                sb.Append("<td>").Append(Badge(v)).Append("<small>").Append(Esc(v.Text ?? "")).Append("</small></td>");
                sb.Append("<td>").Append(TesterList(v)).Append("</td><td>");
                if (canWrite)
                {
                    DecisionForm(sb, s, bare, "hold", "Anhalten", "btn-ghost", false);
                    if (v.Status == "getestet") DecisionForm(sb, s, bare, "now", "Sofort freigeben", "btn-stop", false);
                }
                else sb.Append(NoWrite(s));
                sb.Append("</td></tr>");
            }
            sb.Append("</table></div>");
        }
        if (quiet.Count > 0)
        {
            sb.Append("<details class=\"keep\" style=\"margin-top:10px\"><summary>").Append(quiet.Count).Append(" weitere Updates im Testring ohne Testrechner, ausgenommen oder erledigt</summary>");
            sb.Append("<div class=\"tablewrap\"><table class=\"wt\"><tr><th>Update</th><th>Weitergabe</th><th>Stand</th></tr>");
            foreach (AcmpWumRings.Verdict v in quiet)
            {
                sb.Append("<tr>"); UpdateCell(sb, v);
                sb.Append("<td class=\"num\">").Append(v.ReleaseAt.HasValue ? Esc(v.ReleaseAt.Value.ToString("dd.MM. HH:mm", CultureInfo.InvariantCulture)) : "–").Append("</td>");
                sb.Append("<td>").Append(Badge(v)).Append("<small>").Append(Esc(v.Text ?? "")).Append("</small></td></tr>");
            }
            sb.Append("</table></div></details>");
        }

        // Meldungen
        var events = WumWatch != null ? WumWatch.OpenEvents() : new List<JObject>();
        sb.Append("<h2>Meldungen <span class=\"muted\" style=\"text-transform:none;letter-spacing:0;font-weight:600\">").Append(events.Count > 0 ? events.Count + " offen" : (ev.Findings.Count > 0 ? ev.Findings.Count + " aktuell" : "keine")).Append("</span></h2>");
        if (events.Count > 0)
        {
            sb.Append("<div class=\"now\">");
            foreach (JObject e in events)
            {
                string sev = (string)e["severity"] ?? "info";
                sb.Append("<div class=\"nowrow\"><span class=\"sev ").Append(Esc(sev)).Append("\">").Append(Esc(RuleText((string)e["rule"]))).Append("</span><span class=\"area\">").Append(Esc(WShort((string)e["first"]))).Append("</span>")
                  .Append("<span><b>").Append(Esc((string)e["title"])).Append("</b>: ").Append(Esc((string)e["text"]));
                if (e["action"] != null) sb.Append(" <i>Wächter: ").Append(Esc((string)e["action"])).Append("</i>");
                if (e["baseline"] != null) sb.Append(" <span class=\"bf grey\">schon vor dem ersten Lauf bekannt</span>");
                if (e["openItem"] != null) sb.Append(" <a href=\"/dashboard#punkte\">offener Punkt ").Append(Esc((string)e["openItem"])).Append("</a>");
                sb.Append("</span></div>");
            }
            sb.Append("</div>");
        }
        else if (ev.Findings.Count > 0)
        {
            sb.Append("<div class=\"now\">");
            foreach (AcmpWumRings.Finding fd in ev.Findings)
                sb.Append("<div class=\"nowrow\"><span class=\"sev ").Append(Esc(fd.Severity)).Append("\">").Append(Esc(RuleText(fd.Rule))).Append("</span><span class=\"area\">aktuell</span><span><b>")
                  .Append(Esc(fd.Title)).Append("</b>: ").Append(Esc(fd.Text)).Append("</span></div>");
            sb.Append("</div>");
        }
        else sb.Append("<div class=\"callout ok\">Keine Meldungen.</div>");

        // Kuerzlich freigegeben
        var relBad = new List<AcmpWumRings.Verdict>();
        foreach (AcmpWumRings.Verdict v in ev.Released) if (v.Hard > 0) relBad.Add(v);
        sb.Append("<h2>Freigegeben in den letzten ").Append(Wum.Opt.PostReleaseDays).Append(" Tagen <span class=\"muted\" style=\"text-transform:none;letter-spacing:0;font-weight:600\">")
          .Append(ev.Released.Count).Append(", davon ").Append(relBad.Count).Append(" mit Fehlschlägen</span></h2>");
        if (relBad.Count > 0)
        {
            sb.Append("<div class=\"tablewrap\"><table class=\"wt\"><tr><th>Update</th><th>Freigegeben</th><th>Fehlschläge danach</th></tr>");
            foreach (AcmpWumRings.Verdict v in relBad)
            {
                sb.Append("<tr>"); UpdateCell(sb, v);
                sb.Append("<td class=\"num\">").Append(Esc(v.It.LastChange.ToString("dd.MM. HH:mm", CultureInfo.InvariantCulture))).Append("</td>");
                sb.Append("<td><span class=\"bf crit\">").Append(v.Hard).Append(" Rechner</span>").Append(v.Soft > 0 ? " <span class=\"bf grey\">" + v.Soft + " vorübergehend</span>" : "").Append("<small>").Append(Esc(v.Text)).Append("</small></td></tr>");
            }
            sb.Append("</table></div>");
        }

        // Ringe und Testrechner
        sb.Append("<h2>Ringe und Testrechner</h2><div class=\"two\"><div class=\"panel\"><h3>Ring-Konfiguration (Console: Test- und Freigabeprozess)</h3>");
        if (ev.Configs.Count == 0) sb.Append("<p class=\"muted\">nicht lesbar</p>");
        else
        {
            sb.Append("<table class=\"wt\"><tr><th>Name</th><th>Testring 1</th><th>Testring 2</th><th>Übergang</th></tr>");
            foreach (AcmpWumRings.RingConfig c in ev.Configs)
                sb.Append("<tr><td>").Append(Esc(c.Name)).Append("<small>Priorität ").Append(c.Priority).Append("</small></td><td class=\"num\">").Append(WDays(c.Days[1])).Append("</td><td class=\"num\">").Append(WDays(c.Days[2])).Append("</td><td>")
                  .Append(c.Auto[1] && c.Auto[2] ? "automatisch" : "manuell").Append("</td></tr>");
            sb.Append("</table>");
        }
        sb.Append("</div><div class=\"panel\"><h3>Test-Jobs (WUM-Jobs mit Ring &lt; Freigegeben)</h3>");
        JArray tj = AcmpWumRings.TestersJson(ev.Testers);
        if (tj.Count == 0) sb.Append("<p class=\"muted\">Kein WUM-Job mit Testring – dann testet nichts.</p>");
        else
        {
            sb.Append("<ul class=\"jl\">");
            foreach (JObject j in tj)
            {
                JArray r = (JArray)j["rechner"];
                bool on = (bool)j["aktiv"];
                sb.Append("<li><span class=\"sev ").Append(on && r.Count > 0 ? "info" : "gelb").Append("\">R").Append((int)j["ring"]).Append("</span><span><b>").Append(Esc((string)j["job"])).Append("</b> an <code>").Append(Esc((string)j["container"])).Append("</code>")
                  .Append("<small>").Append(Esc((string)j["startbedingung"])).Append(" · ").Append(r.Count == 0 ? "Container leer" : Esc(string.Join(", ", r.ToObject<string[]>()))).Append("</small></span></li>");
            }
            sb.Append("</ul>");
        }
        sb.Append("</div></div>");
        sb.Append("<script>(function(){function r(){var a=document.activeElement;var busy=a&&/^(INPUT|TEXTAREA|SELECT)$/.test(a.tagName);"
                + "if(busy||document.querySelector('details.keep[open]')){setTimeout(r,60000);return;}location.href='" + UpdatesPath + "';}setTimeout(r,300000);})();</script>");
        return PageNav(s, sb.ToString(), "updates");
    }

    void WatchStrip(StringBuilder sb, WebSession s)
    {
        sb.Append("<div class=\"mstrip\">");
        if (WumWatch == null || !WumWatch.Enabled)
            sb.Append("<span class=\"mpill stop\">Wächter aus").Append(WumWatch != null && WumWatch.DisabledReason != null ? " – " + Esc(WumWatch.DisabledReason) : "").Append("</span>");
        else
        {
            JObject st = WumWatch.Status();
            bool still = (bool)st["still"], failed = st["fehler"] != null && st["fehler"].Type == JTokenType.Boolean && (bool)st["fehler"];
            sb.Append("<span class=\"mpill ").Append(still || failed ? "stop" : "ok").Append("\">Wächter ").Append(still ? "still – kein erfolgreicher Lauf seit über zwei Intervallen" : failed ? "– letzter Lauf gescheitert" : "läuft").Append("</span>");
            if (st["nurMelden"] != null) sb.Append("<span class=\"mpill stop\">").Append(Esc((string)st["nurMelden"])).Append("</span>");
            sb.Append("<span class=\"mpill\">Modus <b>").Append(Esc((string)st["modus"])).Append("</b> · alle ").Append((int)st["intervallMinuten"]).Append(" min</span>");
            sb.Append("<span class=\"mpill\">Letzter Lauf <b>").Append(Esc(WShort((string)st["letzterLauf"]))).Append("</b>").Append(st["letzteDauerMs"] != null && st["letzteDauerMs"].Type != JTokenType.Null ? " (" + Esc(((long)st["letzteDauerMs"] / 1000.0).ToString("0.0", CultureInfo.GetCultureInfo("de-DE"))) + " s)" : "").Append("</span>");
            if (st["letzterFehler"] != null && st["letzterFehler"].Type != JTokenType.Null) sb.Append("<span class=\"mpill stop\">Fehler: ").Append(Esc((string)st["letzterFehler"])).Append("</span>");
            sb.Append("<span class=\"mpill\">Mail ").Append(Esc(WumWatch.MailOn ? "an " + string.Join(", ", WumWatch.MailTo.ToArray()) : "aus")).Append("</span>");
            if (Wum.CanWebWrite(s.Name))
            {
                sb.Append("<span class=\"mstand\"><form class=\"inline\" method=\"post\" action=\"/wum-pruefen\">").Append(CsrfField(s)).Append("<button class=\"btn btn-ghost btn-sm\" type=\"submit\">Jetzt prüfen</button></form>");
                if (WumWatch.MailOn) sb.Append("<form class=\"inline\" method=\"post\" action=\"/wum-testmail\">").Append(CsrfField(s)).Append("<button class=\"btn btn-ghost btn-sm\" type=\"submit\">Testmail</button></form>");
                sb.Append("</span>");
            }
        }
        sb.Append("</div>");
    }

    void UpdateCell(StringBuilder sb, AcmpWumRings.Verdict v)
    {
        sb.Append("<td class=\"nm\"><b>").Append(Esc(AcmpWumRings.Short(v.It.Name))).Append("</b><small class=\"muted\">")
          .Append(Esc((v.It.Classification ?? "ohne Klassifizierung") + (v.It.Kb.HasValue ? " · KB" + v.It.Kb.Value : "") + " · Flotte: " + v.It.Requiring + " brauchen, " + v.It.Installed + " installiert" + (v.It.Failed > 0 ? ", " + v.It.Failed + " fehlgeschlagen" : "")))
          .Append("</small><small class=\"kz muted\">").Append(Esc(v.It.Id)).Append("</small></td>");
    }

    static string Badge(AcmpWumRings.Verdict v)
    {
        switch (v.Status)
        {
            case "fehler": return "<span class=\"bf crit\">Fehler</span>";
            case "ungetestet": return "<span class=\"bf warn\">ungetestet</span>";
            case "gestoppt": return "<span class=\"bf crit\">angehalten</span>";
            case "im-test": return "<span class=\"bf blue\">im Test</span>";
            case "getestet": return "<span class=\"bf ok\">getestet</span>";
            case "kein-testrechner": return "<span class=\"bf grey\">kein Testrechner</span>";
            case "ausgenommen": return "<span class=\"bf grey\">ausgenommen</span>";
            case "erledigt": return "<span class=\"bf grey\">erledigt</span>";
            default: return "<span class=\"bf grey\">" + Esc(v.Status) + "</span>";
        }
    }

    static string TesterList(AcmpWumRings.Verdict v)
    {
        if (v.Testers.Count == 0) return "<span class=\"muted\">–</span>";
        var sb = new StringBuilder("<ul class=\"tl\">");
        foreach (AcmpWumRings.TesterResult t in v.Testers)
        {
            string cls = t.Kind == "ok" ? "y" : t.Kind == "fehler" || t.Kind == "defekt" ? "x" : t.Kind == "voruebergehend" || t.Kind == "unklar" ? "z" : "w";
            string label = t.Kind == "ok" ? "installiert" : t.Kind == "fehler" ? "Fehler" : t.Kind == "defekt" ? "Fehler (Rechnerproblem)" : t.Kind == "voruebergehend" ? "vorübergehend"
                         : t.Kind == "unklar" ? "Fehler (unklar)" : t.Kind == "akzeptiert" ? "Fehler (in Kauf genommen)" : "offen";
            sb.Append("<li>").Append(Esc(t.Name)).Append(": <span class=\"").Append(cls).Append("\">").Append(label).Append("</span>");
            if (t.Error != null) sb.Append(" <span class=\"muted\">").Append(Esc(t.Error.Length > 90 ? t.Error.Substring(0, 89) + "…" : t.Error)).Append("</span>");
            sb.Append("</li>");
        }
        return sb.Append("</ul>").ToString();
    }

    void DecisionForm(StringBuilder sb, WebSession s, string bareId, string action, string label, string btnClass, bool needForce)
    {
        sb.Append("<form class=\"dec\" method=\"post\" action=\"/wum-ring\">").Append(CsrfField(s))
          .Append("<input type=\"hidden\" name=\"id\" value=\"").Append(Esc(bareId)).Append("\"><input type=\"hidden\" name=\"action\" value=\"").Append(Esc(action)).Append("\">")
          .Append("<input type=\"text\" name=\"comment\" minlength=\"8\" maxlength=\"500\" required placeholder=\"Warum? (mind. 8 Zeichen)\" aria-label=\"Kommentar\">");
        if (needForce) sb.Append("<label><input type=\"checkbox\" name=\"force\" value=\"1\" required> trotz Fehler</label>");
        sb.Append("<button class=\"btn ").Append(btnClass).Append(" btn-sm\" type=\"submit\">").Append(Esc(label)).Append("</button></form>");
    }

    string NoWrite(WebSession s)
    {
        if (_act.BrakeActive) return "<span class=\"muted\">Notbremse gezogen</span>";
        return "<span class=\"muted\">Nur Bediener mit Schreibrecht (wumWriter) – oder per MCP</span>";
    }

    static string RuleText(string rule)
    {
        switch (rule)
        {
            case "S1": return "Fehler";
            case "S2": return "ungetestet";
            case "nach": return "Freigabe";
            case "defekt": return "Rechner";
            case "haengt": return "hängt";
            default: return rule ?? "";
        }
    }

    static string WDays(double d) { return d == 0 ? "sofort" : d.ToString("0.#", CultureInfo.GetCultureInfo("de-DE")) + (d == 1 ? " Tag" : " Tage"); }

    static string WShort(string isoLocal)
    {
        DateTime d;
        if (isoLocal != null && DateTime.TryParse(isoLocal, CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return d.ToString("dd.MM. HH:mm", CultureInfo.InvariantCulture);
        return isoLocal ?? "–";
    }

    // Pille fuer das Dashboard (McpStrip)
    string WumPill()
    {
        if (WumWatch == null) return "";
        if (!WumWatch.Enabled) return "<a class=\"mpill\" href=\"" + UpdatesPath + "\">Update-Wächter aus</a>";
        JObject st = WumWatch.Status();
        if ((bool)st["still"]) return "<a class=\"mpill stop\" href=\"" + UpdatesPath + "\">Update-Wächter still</a>";
        if (st["fehler"] != null && st["fehler"].Type == JTokenType.Boolean && (bool)st["fehler"]) return "<a class=\"mpill stop\" href=\"" + UpdatesPath + "\">Update-Wächter: Fehler</a>";
        int held = st["angehaltenVomWaechter"] != null ? (int)st["angehaltenVomWaechter"] : 0;
        int open = st["offeneMeldungen"] != null ? (int)st["offeneMeldungen"] : 0;
        if (held > 0 || open > 0)
            return "<a class=\"mpill stop\" href=\"" + UpdatesPath + "\">Updates <b>" + held + "</b> angehalten" + (open > 0 ? " · " + open + " Meldung" + (open == 1 ? "" : "en") : "") + "</a>";
        return "<a class=\"mpill ok\" href=\"" + UpdatesPath + "\">Update-Wächter ok</a>";
    }
}
