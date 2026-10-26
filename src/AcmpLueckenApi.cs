// AcmpLueckenApi.cs - Erweiterung vom 25.10.2026: Public-API-Befehle, die es schon gibt,
// mit Pruefung, Vorschau (dryRun) und Protokoll statt rohem acmp_call (partial AcmpMcp):
//  - acmp_delete_clients: DeleteClients v1 (<ClientIds><string>{GUID}</string>...</ClientIds>, Probe mit unbekannter Id: OK).
//  - acmp_cf_set_value:   SaveSingleCustomFieldValues v1 (ObjectId, QueryBase 0 = Client, FieldValues/TCustomFieldValue_V1 FieldId+Value);
//                         Lesen des alten Werts mit GetSingleCustomFieldValueLists v1 (Items/TCustomFieldValueContainerRequestItem_V1 ItemId+QueryBase).
//  - acmp_send_report:    SendReports v1 (ReportJobs/TReportJob_V1: ReportID, ExportFormat als Zahl efNone=0 ... efCSV=11, MailToRecipients,
//                         MailCCRecipients, MailBCCRecipients, MailSubject, MailBody).
// Feldnamen aus Aagon.PublicAPI.Commands.dll (ACMP Server\CommandModules, Delphi-RTTI), Enums als Zahl: Proben am 25.10.2026
// ("QueryBase/ExportFormat: Value is no valid enum value" bei Namen, mit Zahl angenommen).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

static partial class AcmpMcp
{
    public const int MaxDeleteClients = 25;
    public const int RecentContactDays = 30;
    // Berichte nur an interne Adressen: erlaubte Domains aus config.local.json (reports.allowedMailDomains, vom Server gesetzt);
    // andere Domains lehnt das Tool ab, ohne Eintrag ist es aus
    static string[] ReportMailDomains = new string[0];
    static string ReportDomainsText() { return ReportMailDomains.Length > 0 ? string.Join(", ", ReportMailDomains) : "reports.allowedMailDomains (nicht gesetzt - Tool aus)"; }
    static readonly string[] ExportFormats = { "none", "html", "pdf", "xlsx", "rtf", "xml", "pptx", "docx", "ods", "odt", "txt", "csv" };

    static bool IsLueckenApiTool(string n) { return n == "acmp_delete_clients" || n == "acmp_cf_set_value" || n == "acmp_send_report"; }

    static bool DryRunArg(JObject a) { return a != null && a["dryRun"] != null && a["dryRun"].Type == JTokenType.Boolean && (bool)a["dryRun"]; }

    static string LStr(JObject a, string n)
    {
        JToken t = a != null ? a[n] : null;
        if (t == null || t.Type == JTokenType.Null) return null;
        string s = t.Type == JTokenType.String ? (string)t : t.ToString(Formatting.None);
        s = s.Trim(); return s.Length == 0 ? null : s;
    }

    static List<string> LList(JObject a, string n)
    {
        var l = new List<string>();
        JToken t = a != null ? a[n] : null;
        if (t == null || t.Type == JTokenType.Null) return l;
        if (t.Type == JTokenType.String)
        {
            string s = ((string)t).Trim();
            if (s.StartsWith("[")) { try { t = JToken.Parse(s); } catch (Exception ex) { throw new Exception(n + " ist kein gueltiges JSON: " + ex.Message); } }
            else { foreach (string p in s.Split(',', ';')) if (p.Trim().Length > 0) l.Add(p.Trim()); return l; }
        }
        var arr = t as JArray; if (arr == null) throw new Exception(n + " muss eine Liste sein.");
        foreach (JToken x in arr) { string v = x.Type == JTokenType.String ? ((string)x).Trim() : x.ToString(); if (v.Length > 0) l.Add(v); }
        return l;
    }

    // DB-Abfrage ueber AcmpDb.RunTool (parametrisiert, Lese-Login des MCP)
    static JArray LQuery(string sql, params object[] nameKindValue)
    {
        RequireDb();
        var def = new JObject(); def["sql"] = sql; var ad = new JArray(); var args = new JObject();
        for (int i = 0; i + 2 < nameKindValue.Length; i += 3)
        {
            var o = new JObject(); o["name"] = (string)nameKindValue[i]; o["kind"] = (string)nameKindValue[i + 1]; ad.Add(o);
            object v = nameKindValue[i + 2]; args[(string)nameKindValue[i]] = v == null ? JValue.CreateNull() : new JValue(v);
        }
        def["args"] = ad;
        return DB.RunTool(def, args);
    }

    static string Js(JToken t) { return t == null || t.Type == JTokenType.Null ? null : (string)t; }
    static double Dbl(JToken t)
    {
        if (t == null || t.Type == JTokenType.Null) return 0;
        if (t.Type == JTokenType.Float || t.Type == JTokenType.Integer) return (double)t;
        double d; return double.TryParse((string)t, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : 0;
    }

    // ResultCode und ResultMessage getrennt lesen (ResultMessage fehlt bei manchen Antworten; Review 25.10.2026)
    static void ApiResult(string xml, JObject res)
    {
        var c = Regex.Match(xml ?? "", "<ResultCode>(\\d+)</ResultCode>");
        var m = Regex.Match(xml ?? "", "<ResultMessage>([^<]*)</ResultMessage>");
        res["resultCode"] = c.Success ? c.Groups[1].Value : "?";
        res["resultMessage"] = m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value) : (c.Success ? "" : Truncate(xml ?? "", 2000));
    }

    static JObject LueckenApi(string name, JObject a, string requester)
    {
        // dryRun nur als echtes true/false: "true" als Text wuerde sonst als Ausfuehren gelesen (Review 25.10.2026)
        if (a != null && a["dryRun"] != null && a["dryRun"].Type != JTokenType.Boolean && a["dryRun"].Type != JTokenType.Null)
            throw new Exception("dryRun muss true oder false sein (nicht als Text) - nichts ausgefuehrt.");
        if (name == "acmp_delete_clients") return DeleteClientsTool(a, requester);
        if (name == "acmp_cf_set_value") return CfSetValueTool(a, requester);
        if (name == "acmp_send_report") return SendReportTool(a, requester);
        throw new Exception("Unbekanntes Tool: " + name);
    }

    // ================= acmp_delete_clients =================
    static JObject DeleteClientsTool(JObject a, string requester)
    {
        List<string> wanted = LList(a, "clients");
        if (wanted.Count == 0) { string one = LStr(a, "client"); if (one != null) wanted.Add(one); }
        if (wanted.Count == 0) throw new Exception("clients fehlt: Computernamen oder ClientIds, z.B. [\"[NETWORK BOOT] 00-11-22\"].");
        if (wanted.Count > MaxDeleteClients) throw new Exception(wanted.Count + " Clients - hoechstens " + MaxDeleteClients + " je Auftrag.");
        bool force = a["force"] != null && a["force"].Type == JTokenType.Boolean && (bool)a["force"];
        var rows = new JArray(); var pv = new JArray(); var refuse = new List<string>(); var ids = new List<string>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const string sql =
            "SELECT CAST(c.CLIENTID AS varchar(36)) AS ClientId, c.COMPUTERNAME AS Rechner, c.COMPUTERDOMAIN AS Domaene, c.IsRetired AS Ausgemustert, " +
            "c.MANAGEMENTAPPISSERVICE AS Art, c.ISMANAGED AS Verwaltet, o.OSSTRING AS Betriebssystem, c.InstalledACMPVersion AS Agent, " +
            "CONVERT(varchar(16), lc.TimeStamp, 120) AS LetzterKontakt, DATEDIFF(day, lc.TimeStamp, GETDATE()) AS TageSeitKontakt, os.OnlineState AS Online, " +
            "CONVERT(varchar(16), CAST(c.LASTUPDATE - 2 AS datetime), 120) AS Inventarisiert, " +
            "(SELECT COUNT(*) FROM CLT_CONTAINER_ITEMS ci WHERE ci.LinkID = c.CLIENTID AND ISNULL(ci.IsDynamic, 0) = 0) AS StatischeContainer " +
            "FROM CLT_CLIENTS_TABLE c LEFT JOIN CLT_OPERATINGSYSTEM o ON o.CLIENTID = c.CLIENTID LEFT JOIN CLT_Clients_LastConnection lc ON lc.ClientId = c.CLIENTID " +
            "LEFT JOIN VW_Inventory_ClientOnlineState os ON os.ClientId = c.CLIENTID WHERE c.COMPUTERNAME = @n OR CAST(c.CLIENTID AS varchar(36)) = @g";
        foreach (string w in wanted)
        {
            string g = AcmpJobs.GuidBraces(w);
            JArray hit = LQuery(sql, "n", "string", g == null ? w : "-", "g", "string", g == null ? "-" : g.Trim('{', '}'));
            if (hit.Count == 0) { refuse.Add(w + ": nicht gefunden"); continue; }
            if (hit.Count > 1) { refuse.Add(w + ": " + hit.Count + " Clients mit diesem Namen - bitte die ClientId angeben (" + string.Join(", ", ToStrings(hit, "ClientId")) + ")"); continue; }
            JObject r = (JObject)hit[0];
            string id = AcmpJobs.GuidBraces(Js(r["ClientId"]));
            if (!seen.Add(id)) continue;
            try { GuardProtectedTargets(id); } catch (Exception ex) { refuse.Add(Js(r["Rechner"]) + ": " + ex.Message.Split('(')[0].Trim()); continue; }
            int tage; bool recent = int.TryParse(Js(r["TageSeitKontakt"]), out tage) && tage < RecentContactDays;
            bool online = Js(r["Online"]) == "0";
            string line = Js(r["Rechner"]) + " " + id + ": " + (Js(r["Betriebssystem"]) ?? "ohne Betriebssystem") + ", Art " + Js(r["Art"]) + (Js(r["Ausgemustert"]) == "True" || Js(r["Ausgemustert"]) == "1" ? ", ausgemustert" : "") +
                          ", letzter Kontakt " + (Js(r["LetzterKontakt"]) ?? "nie") + ", " + (online ? "ONLINE" : "offline") + ", statisch in " + Js(r["StatischeContainer"]) + " Container(n)";
            if (online && !force) { refuse.Add(line + " - ist online, nicht geloescht (force=true, wenn wirklich gewollt)"); continue; }
            if (recent && !force) { refuse.Add(line + " - hatte vor " + tage + " Tagen noch Kontakt, nicht geloescht (force=true, wenn wirklich gewollt)"); continue; }
            pv.Add("LOESCHEN: " + line + (online || recent ? " [force]" : ""));
            rows.Add(r); ids.Add(id);
        }
        var res = new JObject();
        res["vorschau"] = pv;
        if (refuse.Count > 0) res["abgelehnt"] = new JArray(refuse.ToArray());
        res["hinweis"] = "Loeschen entfernt den Client samt Inventar, Job-Historie und Zuordnungen aus ACMP (DeleteClients der Public API) - nicht rueckgaengig zu machen. " +
                         "Ein Client mit Agent meldet sich danach neu an (neue ClientId).";
        if (DryRunArg(a) || ids.Count == 0)
        {
            res["dryRun"] = DryRunArg(a);
            if (ids.Count == 0) res["ergebnis"] = "nichts zu loeschen";
            return TextOut(res.ToString(Formatting.Indented));
        }
        // Sicherung der Stammdaten (zur Nachvollziehbarkeit; wiederherstellen kann der MCP einen Client nicht)
        string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "client-deletes");
        Directory.CreateDirectory(dir);
        string bf = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "_delete_" + ids.Count + ".json");
        var b = new JObject(); b["von"] = requester; b["zeit"] = DateTime.Now.ToString("s"); b["intent"] = LStr(a, "intent"); b["clients"] = rows;
        File.WriteAllText(bf, b.ToString(Formatting.Indented), new UTF8Encoding(false));
        var sb = new StringBuilder("<ClientIds>");
        foreach (string id in ids) sb.Append("<string>").Append(id).Append("</string>");
        sb.Append("</ClientIds>");
        string xml = CONN.Call("DeleteClients", 1, sb.ToString());
        ApiResult(xml, res);
        // Kontrolle in der DB
        var still = new List<string>();
        foreach (JObject r in rows)
        {
            JArray x = LQuery("SELECT COUNT(*) AS n FROM CLT_CLIENTS_TABLE WHERE CAST(CLIENTID AS varchar(36)) = @g", "g", "string", AcmpJobs.GuidBraces(Js(r["ClientId"])).Trim('{', '}'));
            if (x.Count > 0 && Js(x[0]["n"]) != "0") still.Add(Js(r["Rechner"]));
        }
        res["geloescht"] = ids.Count - still.Count;
        if (still.Count > 0) res["nochVorhanden"] = new JArray(still.ToArray());
        res["sicherung"] = Path.GetFileName(bf);
        Log("acmp_delete_clients von " + requester + ": " + ids.Count + " Client(s), ResultCode " + res["resultCode"] + ", noch vorhanden " + still.Count + ", Sicherung " + Path.GetFileName(bf));
        var r2 = TextOut(res.ToString(Formatting.Indented));
        if ((string)res["resultCode"] != "0") r2["isError"] = true;
        return r2;
    }

    static string[] ToStrings(JArray rows, string col)
    {
        var l = new List<string>(); foreach (JToken t in rows) l.Add(Js(t[col]) ?? "?"); return l.ToArray();
    }

    // ================= acmp_cf_set_value =================
    static string ReadCfValue(string clientId, string fieldId, out bool found)
    {
        found = false;
        string xml = CONN.Call("GetSingleCustomFieldValueLists", 1, "<Items><TCustomFieldValueContainerRequestItem_V1><ItemId>" + clientId + "</ItemId><QueryBase>0</QueryBase></TCustomFieldValueContainerRequestItem_V1></Items>");
        var rc = Regex.Match(xml ?? "", "<ResultCode>(\\d+)</ResultCode>");
        if (rc.Success && rc.Groups[1].Value != "0") throw new Exception("Feldwerte nicht lesbar (GetSingleCustomFieldValueLists ResultCode " + rc.Groups[1].Value + ").");
        foreach (Match v in Regex.Matches(xml ?? "", "<TCustomFieldValue_V1[^>]*>(.*?)</TCustomFieldValue_V1>", RegexOptions.Singleline))
        {
            var f = Regex.Match(v.Groups[1].Value, "<FieldId>([^<]*)</FieldId>");
            if (!f.Success || !string.Equals(AcmpJobs.GuidBraces(f.Groups[1].Value), fieldId, StringComparison.OrdinalIgnoreCase)) continue;
            found = true;
            var val = Regex.Match(v.Groups[1].Value, "<Value>(.*?)</Value>", RegexOptions.Singleline);
            return val.Success ? WebUtility.HtmlDecode(val.Groups[1].Value) : null;
        }
        return null;
    }

    // Wert passend zur Eingabeart des Felds pruefen und normieren (VW_SYS_CustomFields.InputMethod: 1 Liste, 7 IPv4, 13 Ganzzahl; Review 25.10.2026).
    // Leer = Wert entfernen, immer erlaubt. Ganzzahl: die Public API lehnt leer ab (ResultCode 35, auch ohne <Value> oder mit xsi:nil) und speichert 0 als leer (NULL) -
    // leer wird deshalb als 0 gesendet, 0 ist immer erlaubt; sonst gilt der Bereich des Felds (HasRange/RangeMin/RangeMax), den die API nicht prueft (Praxistest 25.10.2026).
    public static string CheckCfValue(string inputMethod, string selectable, string name, string value, bool hasRange = false, double rangeMin = 0, double rangeMax = 0)
    {
        int im; int.TryParse(inputMethod, NumberStyles.Integer, CultureInfo.InvariantCulture, out im);
        if (string.IsNullOrEmpty(value)) return im == 13 ? "0" : value ?? "";
        string v = value.Trim();
        if (im == 13)
        {
            long n;
            if (!long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n)) throw new Exception("Feld '" + name + "' ist eine Ganzzahl - '" + value + "' ist keine.");
            if (n != 0 && hasRange && (n < rangeMin || n > rangeMax))
                throw new Exception("Feld '" + name + "' erlaubt " + rangeMin.ToString(CultureInfo.InvariantCulture) + " bis " + rangeMax.ToString(CultureInfo.InvariantCulture) +
                                    " - " + n.ToString(CultureInfo.InvariantCulture) + " liegt ausserhalb (0 = leer ist immer erlaubt).");
            return n.ToString(CultureInfo.InvariantCulture);
        }
        if (im == 1)
        {
            var opts = new List<string>();
            foreach (string s in (selectable ?? "").Split(',')) { string x = s.Trim().Trim('"').Trim(); if (x.Length > 0) opts.Add(x); }
            if (opts.Count == 0) return value;   // Liste ohne hinterlegte Werte: nicht pruefbar
            foreach (string o in opts) if (string.Equals(o, v, StringComparison.OrdinalIgnoreCase)) return o;
            throw new Exception("Feld '" + name + "' ist eine Auswahlliste - erlaubt: " + string.Join(", ", opts.ToArray()) + ".");
        }
        if (im == 7)
        {
            IPAddress ip;
            if (v.Split('.').Length != 4 || !IPAddress.TryParse(v, out ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                throw new Exception("Feld '" + name + "' erwartet eine IPv4-Adresse - '" + value + "' ist keine.");
            return ip.ToString();
        }
        return value;
    }

    static JObject CfSetValueTool(JObject a, string requester)
    {
        string client = LStr(a, "client"), field = LStr(a, "field");
        if (client == null) throw new Exception("client fehlt (Computername oder ClientId).");
        if (field == null) throw new Exception("field fehlt (Name des individuellen Felds wie in der Console bzw. acmp_cf_list, oder Feld-Id).");
        JToken vt = a["value"];
        string value = vt == null || vt.Type == JTokenType.Null ? "" : (vt.Type == JTokenType.String ? (string)vt : vt.ToString(Formatting.None));
        string cid = ClientRef(client);
        string fg = AcmpJobs.GuidBraces(field);
        JArray f = LQuery(
            "SELECT CAST(f.CustomFieldId AS varchar(36)) AS Id, f.Caption_de AS Name, f.Caption_en AS NameEn, f.InputMethod, f.MaxLength, f.QueryBases, " +
            "ISNULL(t.TableDisplayName, '') AS Tabelle, f.SelectableValues, CAST(ISNULL(f.HasRange, 0) AS int) AS HasRange, f.RangeMin, f.RangeMax " +
            "FROM VW_SYS_CustomFields f LEFT JOIN SYS_TableRelations t ON t.TableID = f.TableId " +
            "WHERE CAST(f.CustomFieldId AS varchar(36)) = @g OR f.Caption_de = @n OR f.Caption_en = @n",
            "g", "string", fg == null ? "-" : fg.Trim('{', '}'), "n", "string", fg == null ? field : "-");
        if (f.Count == 0) throw new Exception("Feld '" + field + "' nicht gefunden (acmp_cf_list zeigt die Felder).");
        if (f.Count > 1) throw new Exception("Feld '" + field + "' ist nicht eindeutig - bitte die Feld-Id angeben: " + string.Join("; ", ToStrings(f, "Id")) + ".");
        JObject fd = (JObject)f[0];
        string fid = AcmpJobs.GuidBraces(Js(fd["Id"]));
        value = CheckCfValue(Js(fd["InputMethod"]), Js(fd["SelectableValues"]), Js(fd["Name"]), value, Js(fd["HasRange"]) == "1", Dbl(fd["RangeMin"]), Dbl(fd["RangeMax"]));
        bool isInt = Js(fd["InputMethod"]) == "13";
        int maxLen; if (int.TryParse(Js(fd["MaxLength"]), out maxLen) && maxLen > 0 && value.Length > maxLen)
            throw new Exception("Wert ist " + value.Length + " Zeichen lang, das Feld '" + fd["Name"] + "' erlaubt " + maxLen + ".");
        bool found; string old = ReadCfValue(cid, fid, out found);
        if (!found) throw new Exception("Das Feld '" + fd["Name"] + "' gehoert nicht zu den Feldern dieses Clients (QueryBase Client) - nichts geaendert.");
        string who = DB != null && DB.Enabled ? (DB.ClientName(cid.Trim('{', '}')) ?? cid) : cid;
        var res = new JObject();
        res["client"] = who; res["feld"] = Js(fd["Name"]) + (Js(fd["Tabelle"]) != "" ? " (Tabelle " + Js(fd["Tabelle"]) + ")" : ""); res["feldId"] = fid;
        res["bisher"] = old == null ? JValue.CreateNull() : (JToken)old; res["neu"] = value;
        if (isInt && (value == "0" || old == "0")) res["hinweis"] = "Ganzzahlfeld: 0 und leer sind in ACMP dasselbe (die Public API speichert 0 als leer und liest leer als 0).";
        if (string.Equals(old ?? "", value, StringComparison.Ordinal)) { res["ergebnis"] = "Wert ist schon so gesetzt - nichts geaendert."; return TextOut(res.ToString(Formatting.Indented)); }
        if (DryRunArg(a)) { res["dryRun"] = true; res["vorschau"] = "Feld '" + fd["Name"] + "' bei " + who + ": '" + (old ?? "") + "' -> '" + value + "'"; return TextOut(res.ToString(Formatting.Indented)); }
        string inner = "<ObjectId>" + cid + "</ObjectId><QueryBase>0</QueryBase><FieldValues><TCustomFieldValue_V1><FieldId>" + fid + "</FieldId><Value>" + Xml(value) + "</Value></TCustomFieldValue_V1></FieldValues>";
        string xml = CONN.Call("SaveSingleCustomFieldValues", 1, inner);
        ApiResult(xml, res);
        var fe = Regex.Match(xml ?? "", "<FirstErrorMsg>([^<]+)</FirstErrorMsg>"); if (fe.Success) res["fehler"] = fe.Groups[1].Value;
        bool f2; string after = ReadCfValue(cid, fid, out f2);
        res["jetzt"] = after == null ? JValue.CreateNull() : (JToken)after;
        bool ok = (string)res["resultCode"] == "0" && string.Equals(after ?? "", value, StringComparison.Ordinal);
        res["ok"] = ok;
        res["zurueck"] = "acmp_cf_set_value client=\"" + who + "\" field=\"" + fid + "\" value=" + JsonConvert.ToString(old ?? "");
        Log("acmp_cf_set_value von " + requester + ": " + who + " / " + fd["Name"] + " '" + Truncate(old ?? "", 80) + "' -> '" + Truncate(value, 80) + "', ok=" + ok);
        var r2 = TextOut(res.ToString(Formatting.Indented));
        if (!ok) r2["isError"] = true;
        return r2;
    }

    // ================= acmp_send_report =================
    // Genau eine Adresse mit genau einem @ in einer erlaubten Domain, ohne Trenn- und Steuerzeichen (Review 25.10.2026)
    public static bool InternalMail(string m)
    {
        if (string.IsNullOrEmpty(m)) return false;
        int at = m.IndexOf('@');
        if (at <= 0 || at != m.LastIndexOf('@') || at == m.Length - 1) return false;
        if (m.IndexOfAny(new[] { ';', ',', ' ', '<', '>', '%', '"', '\'', '\r', '\n', '\t', '(', ')', '\\' }) >= 0) return false;
        return Array.IndexOf(ReportMailDomains, m.Substring(at + 1).ToLowerInvariant()) >= 0;
    }

    static JObject SendReportTool(JObject a, string requester)
    {
        if (ReportMailDomains.Length == 0) throw new Exception("acmp_send_report ist aus: in config.local.json fehlt reports.allowedMailDomains (erlaubte Mail-Domains, z.B. [\"example.com\"]), danach Server neu starten.");
        string report = LStr(a, "report");
        if (report == null) throw new Exception("report fehlt (Name des Berichts wie in der Console oder Report-Id).");
        List<string> to = LList(a, "to"), cc = LList(a, "cc");
        if (to.Count == 0) throw new Exception("to fehlt: Empfaenger, z.B. [\"it@example.com\"].");
        var bad = new List<string>();
        foreach (string m in to.Concat(cc))
            if (!InternalMail(m)) bad.Add(m);
        if (bad.Count > 0) throw new Exception("Nur Empfaenger in " + ReportDomainsText() + " erlaubt (je eine Adresse): " + string.Join(", ", bad.ToArray()) + ".");
        // Berichtsformat in reportFormat; 'format' nur noch abwaertskompatibel, denn 'json' ist dort das allgemeine Antwortformat aller Tools (Praxistest 25.10.2026)
        string fa = LStr(a, "format");
        string fmt = (LStr(a, "reportFormat") ?? (fa != null && !fa.Equals("json", StringComparison.OrdinalIgnoreCase) ? fa : null) ?? "pdf").ToLowerInvariant().TrimStart('.');
        int fmtNo = Array.IndexOf(ExportFormats, fmt);
        if (fmtNo <= 0) throw new Exception("reportFormat '" + fmt + "' unbekannt. Erlaubt: " + string.Join(", ", ExportFormats, 1, ExportFormats.Length - 1) + ".");
        string rg = AcmpJobs.GuidBraces(report);
        JArray hit = LQuery("SELECT CAST(ReportId AS varchar(36)) AS Id, Caption AS Name, ISNULL(Description, '') AS Beschreibung FROM SYS_RE_Reports " +
                            "WHERE CAST(ReportId AS varchar(36)) = @g OR Caption = @n", "g", "string", rg == null ? "-" : rg.Trim('{', '}'), "n", "string", rg == null ? report : "-");
        if (hit.Count == 0) throw new Exception("Bericht '" + report + "' nicht gefunden (Name genau wie in der Console unter Berichte).");
        if (hit.Count > 1) throw new Exception("Bericht '" + report + "' ist nicht eindeutig - bitte die Id angeben: " + string.Join(", ", ToStrings(hit, "Id")) + ".");
        string rid = AcmpJobs.GuidBraces(Js(hit[0]["Id"])), rname = Js(hit[0]["Name"]);
        string subject = Regex.Replace(LStr(a, "subject") ?? ("ACMP-Bericht " + rname), "[\\r\\n\\t]+", " ").Trim();   // keine Kopfzeilen einschleusen
        string body = LStr(a, "body") ?? ("Bericht \"" + rname + "\" aus ACMP, angefordert ueber den ACMP-MCP von " + requester + ".");
        var res = new JObject();
        res["bericht"] = rname; res["reportId"] = rid; res["format"] = fmt; res["an"] = string.Join("; ", to.ToArray()); if (cc.Count > 0) res["cc"] = string.Join("; ", cc.ToArray());
        res["betreff"] = subject;
        if (DryRunArg(a)) { res["dryRun"] = true; res["vorschau"] = "ACMP erzeugt den Bericht '" + rname + "' als " + fmt.ToUpperInvariant() + " und mailt ihn an " + res["an"] + "."; return TextOut(res.ToString(Formatting.Indented)); }
        string inner = "<ReportJobs><TReportJob_V1><ReportID>" + rid + "</ReportID><ExportFormat>" + fmtNo.ToString(CultureInfo.InvariantCulture) + "</ExportFormat>" +
                       "<MailToRecipients>" + Xml(string.Join(";", to.ToArray())) + "</MailToRecipients>" +
                       (cc.Count > 0 ? "<MailCCRecipients>" + Xml(string.Join(";", cc.ToArray())) + "</MailCCRecipients>" : "") +
                       "<MailSubject>" + Xml(subject) + "</MailSubject><MailBody>" + Xml(body) + "</MailBody></TReportJob_V1></ReportJobs>";
        string xml = CONN.Call("SendReports", 1, inner);
        ApiResult(xml, res);
        res["hinweis"] = "Zustellung pruefen: acmp_mail_queue (haengt die Mail dort, nennt sie den Fehler).";
        Log("acmp_send_report von " + requester + ": '" + rname + "' als " + fmt + " an " + res["an"] + ", ResultCode " + res["resultCode"]);
        var r2 = TextOut(res.ToString(Formatting.Indented));
        if ((string)res["resultCode"] != "0") r2["isError"] = true;
        return r2;
    }
}
