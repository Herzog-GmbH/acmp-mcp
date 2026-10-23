// AcmpWum: Windows-Updates in ACMP (CAWUM) ablehnen und die Ablehnung aufheben - acmp_wum_declined (lesen), acmp_wum_decline, acmp_wum_restore.
// Grundlage: Analyse der Datenbank vom 21.10.2026 (ACMP 6.10):
//  - Ablehnen in der Console = Prozedur dbo.ap_Wum_DeclineUpdates(@aItemIds dbo.at_IDList READONLY, @aComment nvarchar(max), @aNow float):
//    ein MERGE, das fehlende Update-Ids mit Kommentar und Zeit (OLE-Datum, Ortszeit) in Sys_Wum_DeclinedUpdates einfuegt. Mehr nicht.
//  - Das Flag Sys_Wum_Updates.IsDeclined setzt dbo.ap_Wum_Rebuild_UpdateFlags aus der View VW_Wum_Updates_Flags (idempotent: schreibt nur
//    Zeilen, die vom Soll abweichen). Das Tool ruft sie nach dem Schreiben auf, damit das Flag nicht bis zum naechsten Lauf des Servers fehlt.
//  - Fuer "Ablehnung aufheben" gibt es keine Prozedur (keine Datenbankprozedur nennt Sys_Wum_DeclinedUpdates ausser ap_Wum_DeclineUpdates
//    und Views). Die Console macht das vermutlich mit einem direkten DELETE auf Sys_Wum_DeclinedUpdates - NOCH NICHT BELEGT, deshalb:
//    acmp_wum_restore loescht genau die eine Zeile (nach Id, Kommentar und Zeit des Plans) und ruft danach ap_Wum_Rebuild_UpdateFlags;
//    erster Einsatz mit einem harmlosen, vom Bereinigungsjob abgelehnten Update und Kontrolle in der Console.
//  - Ein Bereinigungsjob lehnt nachts unbenutzte Updates mit dem Kommentar "Autodeclined by Cleanupjob for being unused." ab. Die Liste blendet
//    sie standardmaessig aus (includeAuto=true zeigt sie).
// Regeln: erst dryRun (Vorschau), nur einzelne Update-Ids (hoechstens MaxIds je Auftrag),
// Pflicht-Kommentar, Plan mit dem Stand der Updates (hat sich bis zum Schreiben etwas geaendert, wird nichts geschrieben),
// Sicherung der betroffenen Zeilen vor dem Schreiben (wum-backups\), eine Transaktion je Auftrag, Kontrolle danach.
// Abgeloeste (IsSuperseded), abgelaufene (IsExpired) und widerrufene (IsRevoked) Updates lehnt das Tool nicht ab (bringt nichts bzw. nicht noetig).
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

class AcmpWum
{
    public const int MaxIds = 10;
    public const int MaxListed = 200;
    public const int MinCommentLength = 8;
    public const int MaxCommentLength = 500;
    public const string AutoDeclinePrefix = "Autodeclined by Cleanupjob";
    public static readonly string[] TestedVersions = { "6.10" };
    public static readonly string[] KnownTriggers = { };   // auf Sys_Wum_DeclinedUpdates sind keine Trigger bekannt (Analyse 21.10.2026)

    public static readonly string IdsHelp =
        "Liste von Update-Ids (GUID), 1-" + MaxIds + ", z.B. [\"{A1B2C3D4-E5F6-4711-8899-AABBCCDDEEFF}\"]. Die Ids stehen in acmp_wum_declined (scope=open), " +
        "acmp_failed_updates (UpdateId) und in der Console. Mehrere Updates mit derselben KB-Nummer (z.B. Kumulatives Update und 26H2) sind getrennte Ids.";

    readonly string _readCs, _writeCs, _backupDir;
    readonly HashSet<string> _allowedReq = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    readonly Action<string> _log;
    readonly Func<string> _acmpVersion;
    readonly bool _allowUntested;
    readonly bool _rebuildFlags;
    JObject _preflight; DateTime _preflightAt;
    readonly object _pfLock = new object();
    static readonly object _wLock = new object();
    const int PreflightMaxAgeMinutes = 60;

    public bool ConfigOk { get; private set; }
    public string DisabledReason { get; private set; }
    public ICollection<string> AllowedRequesters { get { return _allowedReq; } }

    public AcmpWum(string readCs, JObject cfg, JObject ccCfg, string cfgDir, Action<string> log, Func<string> acmpVersion)
    {
        _readCs = readCs; _log = log; _acmpVersion = acmpVersion;
        string bd = cfg != null && cfg["backupDir"] != null ? (string)cfg["backupDir"] : "wum-backups";
        _backupDir = Path.IsPathRooted(bd) ? bd : Path.Combine(cfgDir, bd);
        _allowUntested = cfg != null && cfg["allowUntested"] != null && (bool)cfg["allowUntested"];
        _rebuildFlags = cfg == null || cfg["rebuildFlags"] == null || (bool)cfg["rebuildFlags"];
        if (cfg == null || cfg["enabled"] == null || !(bool)cfg["enabled"]) { DisabledReason = "wumWriter.enabled ist nicht true (nur Lesen und dryRun)"; return; }
        _writeCs = cfg["connectionString"] != null ? (string)cfg["connectionString"] : (ccCfg != null ? (string)ccCfg["connectionString"] : null);
        JArray ar = (cfg["allowedRequesters"] as JArray) ?? (ccCfg != null ? ccCfg["allowedRequesters"] as JArray : null);
        if (ar != null) foreach (JToken t in ar) _allowedReq.Add((string)t);
        if (string.IsNullOrEmpty(_readCs)) { DisabledReason = "db.connectionString fehlt"; return; }
        if (string.IsNullOrEmpty(_writeCs) || _writeCs.StartsWith("HIER_")) { DisabledReason = "kein Schreib-Login (wumWriter.connectionString oder ccWriter.connectionString)"; return; }
        if (_allowedReq.Count == 0) { DisabledReason = "keine erlaubten Antragsteller (wumWriter.allowedRequesters oder ccWriter.allowedRequesters)"; return; }
        ConfigOk = true;
    }

    // ================= Hilfen ohne DB (offline getestet in WumTest.cs) =================

    // "{GUID}" in Grossbuchstaben oder null
    public static string GuidBraces(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        Guid g; if (!Guid.TryParse(s.Trim().Trim('{', '}'), out g)) return null;
        return "{" + g.ToString().ToUpperInvariant() + "}";
    }

    static Guid G(string s) { return new Guid(s.Trim('{', '}')); }

    static bool IsStr(JToken t) { return t != null && t.Type == JTokenType.String && ((string)t).Trim().Length > 0; }

    // o["x"] = null legt in Newtonsoft eine Eigenschaft mit JValue-Null an - "!= null" reicht dafuer nicht
    public static bool IsNull(JToken t) { return t == null || t.Type == JTokenType.Null; }

    // Auftrag -> Liste normalisierter Update-Ids: updateIds=[...] (auch als JSON-Text) oder updateId="..."
    public static List<string> IdList(JObject a)
    {
        bool hasList = a["updateIds"] != null && a["updateIds"].Type != JTokenType.Null, hasOne = IsStr(a["updateId"]);
        if (hasList && hasOne) throw new Exception("Entweder updateIds oder updateId angeben, nicht beides.");
        var raw = new List<string>();
        if (hasList)
        {
            JToken t = a["updateIds"];
            if (t.Type == JTokenType.String)
            {
                string s = ((string)t).Trim();
                if (s.StartsWith("["))
                {
                    try { t = JToken.Parse(s); }
                    catch (Exception ex) { throw new Exception("updateIds ist kein gueltiges JSON: " + ex.Message); }
                }
                else t = new JArray(s);
            }
            JArray arr = t as JArray;
            if (arr == null) throw new Exception("updateIds muss eine Liste sein: " + IdsHelp);
            foreach (JToken x in arr)
            {
                string v = x.Type == JTokenType.String ? ((string)x).Trim() : "";
                if (v.Length == 0) throw new Exception("updateIds: leere Angabe - je Eintrag eine Update-Id (GUID).");
                raw.Add(v);
            }
        }
        else if (hasOne) raw.Add(((string)a["updateId"]).Trim());
        else throw new Exception("Welches Update? " + IdsHelp);
        if (raw.Count == 0) throw new Exception("updateIds ist leer.");
        var ids = new List<string>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string v in raw)
        {
            string g = GuidBraces(v);
            if (g == null) throw new Exception("\"" + v + "\" ist keine Update-Id (GUID). Nur Ids, keine KB-Nummern oder Namen - " + IdsHelp);
            if (seen.Add(g)) ids.Add(g);
        }
        if (ids.Count > MaxIds) throw new Exception(ids.Count + " Updates - hoechstens " + MaxIds + " je Auftrag, bitte aufteilen.");
        return ids;
    }

    // Pflicht-Kommentar fuer decline: mindestens MinCommentLength Zeichen; gespeichert wird "MCP <Bediener>: <Text>"
    public static string DeclineComment(string comment, string requester)
    {
        string c = (comment ?? "").Trim();
        if (c.Length < MinCommentLength)
            throw new Exception("comment fehlt oder ist zu kurz (mindestens " + MinCommentLength + " Zeichen): Warum wird das Update abgelehnt? Der Text steht in der Console und im Protokoll.");
        if (c.Length > MaxCommentLength) throw new Exception("comment ist zu lang (hoechstens " + MaxCommentLength + " Zeichen).");
        return "MCP " + (requester ?? "?") + ": " + c;
    }

    public static bool IsAutoDecline(string comment)
    {
        return comment != null && comment.StartsWith(AutoDeclinePrefix, StringComparison.OrdinalIgnoreCase);
    }

    // OLE-Datum (Ortszeit, wie die Console es schreibt) -> Text; ungueltige Werte als Rohwert
    public static string OleText(double d)
    {
        try { return DateTime.FromOADate(d).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture); }
        catch { return d.ToString(CultureInfo.InvariantCulture); }
    }

    // Warnungen zu einem Update fuer Vorschau und dryRun (item aus Lookup)
    public static List<string> DeclineWarnings(JObject it)
    {
        var w = new List<string>();
        string cls = ((string)it["classification"] ?? "");
        int sev = it["severity"] != null && it["severity"].Type == JTokenType.Integer ? (int)it["severity"] : 0;
        bool security = cls.IndexOf("secur", StringComparison.OrdinalIgnoreCase) >= 0 || cls.IndexOf("critical", StringComparison.OrdinalIgnoreCase) >= 0 || sev >= 3;
        int req = (int)it["requiring"], inst = (int)it["installed"], failed = (int)it["failed"];
        if (security && req > 0)
            w.Add("ACHTUNG: Sicherheits-/kritisches Update (" + (cls.Length > 0 ? cls : "Severity " + sev) + ") - " + req + " Client(s) brauchen es und bekommen es nach der Ablehnung nicht mehr angeboten. Sie bleiben ungepatcht, bis die Ablehnung aufgehoben ist.");
        else if (req > 0)
            w.Add(req + " Client(s) brauchen das Update und bekommen es nach der Ablehnung nicht mehr angeboten.");
        if (inst > 0) w.Add(inst + " Client(s) haben es schon installiert - das bleibt, die Ablehnung deinstalliert nichts.");
        if (failed > 0) w.Add(failed + " Client(s) melden einen Fehlschlag.");
        JArray same = it["sameKb"] as JArray;
        if (same != null && same.Count > 0)
        {
            var l = new List<string>(); foreach (JToken t in same) l.Add((string)t);
            w.Add("Gleiche KB-Nummer, aber andere Updates (bleiben unberuehrt und werden weiter angeboten): " + string.Join("; ", l.ToArray()));
        }
        return w;
    }

    // Unterschiede zwischen dem eingefrorenen Plan-Eintrag und dem jetzigen Stand (leer = unveraendert)
    public static List<string> PlanDiff(string action, JObject planned, JObject now)
    {
        var d = new List<string>();
        if (now == null) { d.Add("Update gibt es nicht mehr"); return d; }
        bool pd = planned["declined"] != null && (bool)planned["declined"], nd = (bool)now["declined"];
        if (pd != nd) d.Add(nd ? "ist inzwischen abgelehnt" : "ist inzwischen nicht mehr abgelehnt");
        if (action == "restore")
        {
            if ((string)planned["comment"] != (string)now["comment"]) d.Add("Kommentar der Ablehnung geaendert");
            if (IsNull(planned["date"]) != IsNull(now["date"])) d.Add("Zeitpunkt der Ablehnung geaendert");
            else if (!IsNull(planned["date"]) && Math.Abs((double)planned["date"] - (double)now["date"]) > 1e-9) d.Add("Zeitpunkt der Ablehnung geaendert");
        }
        else
        {
            bool ps = planned["superseded"] != null && (bool)planned["superseded"];
            if (ps != (bool)now["superseded"]) d.Add("Abgeloest-Status geaendert");
            if ((bool)now["expired"]) d.Add("ist inzwischen abgelaufen");
        }
        return d;
    }

    // ================= Vorpruefung =================
    public void UsePreflightResult(JObject pf) { lock (_pfLock) { _preflight = pf; _preflightAt = DateTime.UtcNow; } }

    public JObject Preflight(bool refresh)
    {
        if (!ConfigOk) return null;
        lock (_pfLock)
        {
            if (!refresh && _preflight != null && (DateTime.UtcNow - _preflightAt).TotalMinutes < PreflightMaxAgeMinutes) return _preflight;
            string v = null;
            try { v = _acmpVersion != null ? _acmpVersion() : null; } catch { }
            _preflight = RunPreflight(v);
            _preflightAt = DateTime.UtcNow;
            _log("WUM-Writer Vorpruefung (ACMP " + (v ?? "?") + "): " + (string)_preflight["summary"]);
            return _preflight;
        }
    }

    static void Add(JArray checks, string check, string result, string detail)
    {
        var o = new JObject(); o["check"] = check; o["result"] = result; o["detail"] = detail; checks.Add(o);
    }

    static string Scalar(SqlConnection c, string sql)
    {
        var q = c.CreateCommand(); q.CommandText = sql;
        object o = q.ExecuteScalar();
        return o == null || o is DBNull ? null : Convert.ToString(o, CultureInfo.InvariantCulture);
    }

    static bool HasPerm(SqlConnection c, string obj, string cls, string perm)
    {
        var q = c.CreateCommand();
        q.CommandText = "SELECT HAS_PERMS_BY_NAME(@o, @c, @p)";
        q.Parameters.AddWithValue("@o", obj); q.Parameters.AddWithValue("@c", cls); q.Parameters.AddWithValue("@p", perm);
        object o = q.ExecuteScalar();
        return o != null && !(o is DBNull) && Convert.ToInt32(o) == 1;
    }

    JObject RunPreflight(string v)
    {
        var checks = new JArray();
        bool declineOk = false, restoreOk = false, rebuildOk = false;
        try
        {
            string mm = null;
            if (!string.IsNullOrEmpty(v)) { string[] p = v.Split('.'); if (p.Length >= 2) mm = p[0] + "." + p[1]; }
            if (mm != null && Array.IndexOf(TestedVersions, mm) >= 0) Add(checks, "ACMP-Version", "ok", v);
            else Add(checks, "ACMP-Version", _allowUntested ? "warn" : "block", (mm == null ? "nicht ermittelbar" : v + " ist nicht getestet") + " (getestet: " + string.Join(", ", TestedVersions) + ")" + (_allowUntested ? " - per wumWriter.allowUntested freigegeben" : ""));
            using (var rc = new SqlConnection(_readCs))
            using (var wc = new SqlConnection(_writeCs))
            {
                rc.Open();
                try { wc.Open(); }
                catch (Exception ex) { Add(checks, "Schreib-Login", "block", "Anmeldung fehlgeschlagen: " + ex.Message); return Result(checks, false, false, false); }
                string a = Scalar(rc, "SELECT @@SERVERNAME + '/' + DB_NAME()"), b = Scalar(wc, "SELECT @@SERVERNAME + '/' + DB_NAME()");
                bool same = string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
                Add(checks, "Datenbank", same ? "ok" : "block", a + (same ? " (Lese- und Schreib-Login gleich)" : " / Schreib-Login auf " + b));
                // Tabelle: genau Id uniqueidentifier, Comment nvarchar, Date float
                string cols = Scalar(rc, "SELECT STUFF((SELECT ',' + COLUMN_NAME + ':' + DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='Sys_Wum_DeclinedUpdates' ORDER BY ORDINAL_POSITION FOR XML PATH('')), 1, 1, '')");
                Add(checks, "Tabelle Sys_Wum_DeclinedUpdates", cols == "Id:uniqueidentifier,Comment:nvarchar,Date:float" ? "ok" : "block", "Spalten: " + (cols ?? "keine") + " (erwartet Id:uniqueidentifier,Comment:nvarchar,Date:float)");
                // Prozedur: Parameter @aItemIds, @aComment, @aNow
                // Metadaten der Prozeduren sieht nur, wer EXECUTE hat - deshalb mit dem Schreib-Login lesen
                string pars = Scalar(wc, "SELECT STUFF((SELECT ',' + p.name FROM sys.parameters p WHERE p.object_id = OBJECT_ID('dbo.ap_Wum_DeclineUpdates') ORDER BY p.parameter_id FOR XML PATH('')), 1, 1, '')");
                Add(checks, "Prozedur ap_Wum_DeclineUpdates", pars == "@aItemIds,@aComment,@aNow" ? "ok" : "warn",
                    "Parameter: " + (pars ?? "Prozedur fehlt oder fuer den Schreib-Login nicht sichtbar (EXECUTE fehlt?)") + " (erwartet @aItemIds,@aComment,@aNow)");
                bool decPars = pars == "@aItemIds,@aComment,@aNow";   // andere Parameter = andere ACMP-Version: Ablehnen gesperrt
                string rebuildExists = Scalar(wc, "SELECT CAST(COUNT(*) AS varchar(5)) FROM sys.procedures WHERE name = 'ap_Wum_Rebuild_UpdateFlags'");
                string rp = Scalar(wc, "SELECT CAST(COUNT(*) AS varchar(5)) FROM sys.parameters WHERE object_id = OBJECT_ID('dbo.ap_Wum_Rebuild_UpdateFlags')");
                Add(checks, "Prozedur ap_Wum_Rebuild_UpdateFlags", rebuildExists == "1" && rp == "0" ? "ok" : "warn",
                    rebuildExists == "1" ? (rp == "0" ? "ohne Parameter vorhanden" : "hat Parameter - Flags werden nicht neu aufgebaut") : "fehlt oder fuer den Schreib-Login nicht sichtbar - Flags werden nicht neu aufgebaut");
                // Trigger auf Sys_Wum_DeclinedUpdates
                var trg = new List<string>();
                var tq = wc.CreateCommand(); tq.CommandText = "SELECT t.name FROM sys.triggers t WHERE t.parent_id = OBJECT_ID('dbo.Sys_Wum_DeclinedUpdates')";
                using (var r = tq.ExecuteReader()) while (r.Read()) trg.Add(r.GetString(0));
                var unknown = new List<string>();
                foreach (string t in trg) if (Array.IndexOf(KnownTriggers, t) < 0) unknown.Add(t);
                Add(checks, "Trigger", unknown.Count == 0 ? "ok" : "block", unknown.Count == 0 ? "keine Trigger auf Sys_Wum_DeclinedUpdates (wie in der Analyse)" : "unbekannte Trigger: " + string.Join(", ", unknown.ToArray()) + " - erst pruefen");
                // Rechte
                bool exec = HasPerm(wc, "dbo.ap_Wum_DeclineUpdates", "OBJECT", "EXECUTE");
                bool typ = HasPerm(wc, "dbo.at_IDList", "TYPE", "EXECUTE");
                bool sel = HasPerm(wc, "dbo.Sys_Wum_DeclinedUpdates", "OBJECT", "SELECT"), del = HasPerm(wc, "dbo.Sys_Wum_DeclinedUpdates", "OBJECT", "DELETE");
                declineOk = exec && typ && sel && decPars;
                Add(checks, "Rechte Ablehnen", exec && typ && sel ? "ok" : "warn", declineOk ? "EXECUTE auf ap_Wum_DeclineUpdates und Typ at_IDList, SELECT auf Sys_Wum_DeclinedUpdates vorhanden"
                    : "fehlen: " + (exec ? "" : "EXECUTE ap_Wum_DeclineUpdates ") + (typ ? "" : "EXECUTE TYPE at_IDList ") + (sel ? "" : "SELECT Sys_Wum_DeclinedUpdates") + " - acmp_wum_decline gesperrt (grant-wum-writer.sql)");
                rebuildOk = HasPerm(wc, "dbo.ap_Wum_Rebuild_UpdateFlags", "OBJECT", "EXECUTE");
                Add(checks, "Rechte Flags", rebuildOk ? "ok" : "warn", rebuildOk ? "EXECUTE auf ap_Wum_Rebuild_UpdateFlags vorhanden" : "fehlt - IsDeclined wird nicht sofort gesetzt, ACMP holt es beim naechsten Lauf nach (grant-wum-writer.sql)");
                restoreOk = sel && del;
                Add(checks, "Rechte Aufheben", restoreOk ? "ok" : "warn", restoreOk ? "SELECT und DELETE auf Sys_Wum_DeclinedUpdates vorhanden"
                    : "fehlen: " + (sel ? "" : "SELECT ") + (del ? "" : "DELETE") + " auf Sys_Wum_DeclinedUpdates - acmp_wum_restore gesperrt (grant-wum-writer.sql)");
                var extra = new List<string>();
                if (HasPerm(wc, "dbo.Sys_Wum_DeclinedUpdates", "OBJECT", "INSERT")) extra.Add("INSERT auf Sys_Wum_DeclinedUpdates (das Tool braucht nur die Prozedur)");
                if (HasPerm(wc, "dbo.Sys_Wum_Updates", "OBJECT", "UPDATE")) extra.Add("UPDATE auf Sys_Wum_Updates");
                if (Scalar(wc, "SELECT CAST(IS_ROLEMEMBER('db_owner') AS varchar(5))") == "1") extra.Add("Mitglied von db_owner");
                if (extra.Count > 0) Add(checks, "Rechte (zu viele)", "warn", string.Join(", ", extra.ToArray()));
            }
        }
        catch (Exception ex) { Add(checks, "Pruefung", "block", "Abgebrochen: " + ex.Message); }
        return Result(checks, declineOk, restoreOk, rebuildOk);
    }

    static JObject Result(JArray checks, bool declineOk, bool restoreOk, bool rebuildOk)
    {
        var blocks = new List<string>(); int warns = 0;
        foreach (JObject c in checks)
        {
            if ((string)c["result"] == "block") blocks.Add((string)c["check"] + ": " + (string)c["detail"]);
            else if ((string)c["result"] == "warn") warns++;
        }
        var o = new JObject();
        bool ok = blocks.Count == 0;
        o["ok"] = ok;
        o["checkedLocal"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        o["summary"] = ok ? "bestanden" + (warns > 0 ? " (" + warns + " Hinweis(e))" : "") : "GESPERRT: " + string.Join(" | ", blocks.ToArray());
        o["declineTool"] = ok && declineOk;
        o["restoreTool"] = ok && restoreOk;
        o["rebuildFlags"] = rebuildOk;
        o["checks"] = checks;
        return o;
    }

    public JObject Status(bool refresh)
    {
        var o = new JObject();
        o["enabled"] = ConfigOk;
        if (!ConfigOk) { o["status"] = "aus"; o["reason"] = DisabledReason; return o; }
        JObject pf = Preflight(refresh);
        o["status"] = (bool)pf["ok"] ? "aktiv" : "gesperrt";
        o["allowedRequesters"] = new JArray(new List<string>(_allowedReq).ToArray());
        o["declineTool"] = pf["declineTool"]; o["restoreTool"] = pf["restoreTool"];
        o["flagsNeuAufbauen"] = _rebuildFlags && (bool)pf["rebuildFlags"];
        o["vorpruefung"] = pf;
        return o;
    }

    public void CheckWrite(string requester, string action)
    {
        if (!ConfigOk) throw new Exception("Update-Ablehnen ist ausgeschaltet: " + DisabledReason);
        if (requester == null || !_allowedReq.Contains(requester)) throw new Exception("Antragsteller '" + requester + "' darf Updates nicht ablehnen (wumWriter.allowedRequesters).");
        JObject pf = Preflight(false);
        if (pf == null || !(bool)pf["ok"]) throw new Exception("Update-Ablehnen gesperrt - Vorpruefung nicht bestanden: " + (pf != null ? (string)pf["summary"] : "keine Pruefung") + " (Details: acmp_wum_declined mit status=true). Nichts geschrieben.");
        string key = action == "restore" ? "restoreTool" : "declineTool";
        if (pf[key] == null || !(bool)pf[key])
            throw new Exception((action == "restore" ? "acmp_wum_restore" : "acmp_wum_decline") + " gesperrt - Rechte des Schreib-Logins fehlen oder die ACMP-Prozedur hat andere Parameter (Details: acmp_wum_declined mit status=true, grant-wum-writer.sql). Nichts geschrieben.");
    }

    // ================= Lesen =================
    // Stand eines Updates (null = unbekannt). Zaehler aus den Grids; declined = Zeile in Sys_Wum_DeclinedUpdates.
    JObject Lookup(SqlConnection c, string id)
    {
        var q = c.CreateCommand();
        q.CommandText =
            "SELECT TOP 1 CAST(u.Id AS varchar(36)), u.RevisionNumber, u.KBId, u.Severity, u.IsSuperseded, u.IsExpired, u.IsRevoked, u.IsDeclined, u.TestRing, " +
            "(SELECT TOP 1 l.Name FROM SYS_Wum_Updates_Localization l WHERE l.Id = u.Id) " +
            "FROM Sys_Wum_Updates u WHERE u.Id = @id ORDER BY u.RevisionNumber DESC";
        q.Parameters.AddWithValue("@id", G(id));
        var o = new JObject();
        using (var r = q.ExecuteReader())
        {
            if (!r.Read()) return null;
            o["id"] = "{" + r.GetString(0).ToUpperInvariant() + "}";
            o["revision"] = r.GetInt32(1);
            o["kb"] = r.IsDBNull(2) ? null : (JToken)r.GetInt32(2);
            o["severity"] = r.IsDBNull(3) ? null : (JToken)(int)r.GetInt16(3);
            o["superseded"] = r.GetBoolean(4); o["expired"] = r.GetBoolean(5); o["revoked"] = r.GetBoolean(6);
            o["flagDeclined"] = r.GetBoolean(7);
            o["testRing"] = (int)r.GetByte(8);
            o["name"] = r.IsDBNull(9) ? null : r.GetString(9);
        }
        var dq = c.CreateCommand();
        dq.CommandText = "SELECT Comment, [Date] FROM Sys_Wum_DeclinedUpdates WHERE Id = @id";
        dq.Parameters.AddWithValue("@id", G(id));
        using (var r = dq.ExecuteReader())
        {
            bool has = r.Read();
            o["declined"] = has;
            o["comment"] = has && !r.IsDBNull(0) ? r.GetString(0) : null;
            o["date"] = has ? (JToken)r.GetDouble(1) : null;
        }
        // Zaehler und Klassifizierung aus den Grids (offene oder abgelehnte Updates)
        var gq = c.CreateCommand();
        gq.CommandText =
            "SELECT TOP 1 Classification, ClientCountRequiring, ClientCountInstalled, ClientCountFailed FROM VW_Wum_UpdateGrid WHERE Id = @id " +
            "UNION ALL SELECT TOP 1 Classification, ClientCountRequiring, ClientCountInstalled, ClientCountFailed FROM VW_Wum_DeclinedUpdateGrid WHERE Id = @id";
        gq.Parameters.AddWithValue("@id", G(id));
        using (var r = gq.ExecuteReader())
        {
            if (r.Read())
            {
                o["classification"] = r.IsDBNull(0) ? null : r.GetString(0);
                o["requiring"] = r.GetInt32(1); o["installed"] = r.GetInt32(2); o["failed"] = r.GetInt32(3);
            }
            else { o["classification"] = null; o["requiring"] = 0; o["installed"] = 0; o["failed"] = 0; }
        }
        // andere, nicht abgelehnte und nicht abgeloeste Updates mit derselben KB-Nummer
        var sames = new JArray();
        if (!IsNull(o["kb"]))
        {
            var kq = c.CreateCommand();
            kq.CommandText =
                "SELECT TOP 5 (SELECT TOP 1 l.Name FROM SYS_Wum_Updates_Localization l WHERE l.Id = u.Id) FROM Sys_Wum_Updates u " +
                "WHERE u.KBId = @kb AND u.Id <> @id AND u.IsDeclined = 0 AND u.IsSuperseded = 0 AND u.IsExpired = 0 " +
                "AND u.RevisionNumber = (SELECT MAX(x.RevisionNumber) FROM Sys_Wum_Updates x WHERE x.Id = u.Id)";
            kq.Parameters.AddWithValue("@kb", (int)o["kb"]); kq.Parameters.AddWithValue("@id", G(id));
            using (var r = kq.ExecuteReader()) while (r.Read()) sames.Add(r.IsDBNull(0) ? "(ohne Namen)" : r.GetString(0));
        }
        o["sameKb"] = sames;
        return o;
    }

    static string ItemLabel(JObject it)
    {
        return "\"" + ((string)it["name"] ?? "(ohne Namen)") + "\" " + it["id"] + (!IsNull(it["kb"]) ? " (KB" + it["kb"] + ")" : "");
    }

    // acmp_wum_declined: abgelehnte Updates (scope=declined, Standard) oder noch offene Kandidaten (scope=open)
    public JObject ListUpdates(JObject a)
    {
        if (string.IsNullOrEmpty(_readCs)) throw new Exception("db.connectionString fehlt.");
        string scope = a["scope"] != null && a["scope"].Type == JTokenType.String ? ((string)a["scope"]).Trim().ToLowerInvariant() : "declined";
        if (scope != "declined" && scope != "open") throw new Exception("scope: declined (abgelehnt, Standard) oder open (offen, noch nicht abgelehnt).");
        string search = a["search"] != null && a["search"].Type == JTokenType.String ? ((string)a["search"]).Trim() : "";
        bool includeAuto = a["includeAuto"] != null && a["includeAuto"].Type == JTokenType.Boolean && (bool)a["includeAuto"];
        int top = 50;
        if (a["top"] != null && a["top"].Type != JTokenType.Null) { try { top = Math.Max(1, Math.Min(MaxListed, (int)a["top"])); } catch { throw new Exception("top muss eine Zahl sein."); } }
        string idArg = a["id"] != null && a["id"].Type == JTokenType.String ? GuidBraces((string)a["id"]) : null;
        if (a["id"] != null && a["id"].Type == JTokenType.String && idArg == null) throw new Exception("id ist keine GUID.");
        var rows = new JArray(); int total = 0, auto = 0;
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            var q = c.CreateCommand();
            var w = new StringBuilder(" WHERE 1=1");
            if (idArg != null) { w.Append(" AND g.Id = @id"); q.Parameters.AddWithValue("@id", G(idArg)); }
            if (search.Length > 0)
            {
                w.Append(" AND (g.Name LIKE @s ESCAPE '\\'");
                int kb; string digits = search.StartsWith("KB", StringComparison.OrdinalIgnoreCase) ? search.Substring(2) : search;
                if (int.TryParse(digits, out kb)) { w.Append(" OR g.KBId = @kb"); q.Parameters.AddWithValue("@kb", kb); }
                w.Append(")");
                q.Parameters.AddWithValue("@s", "%" + search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[") + "%");
            }
            if (scope == "declined")
            {
                string baseSql = "FROM VW_Wum_DeclinedUpdateGrid g" + w;
                q.CommandText = "SELECT COUNT(*), SUM(CASE WHEN g.Comment LIKE '" + AutoDeclinePrefix + "%' THEN 1 ELSE 0 END) " + baseSql;
                using (var r = q.ExecuteReader()) { r.Read(); total = r.GetInt32(0); auto = r.IsDBNull(1) ? 0 : r.GetInt32(1); }
                var q2 = c.CreateCommand();
                foreach (SqlParameter p in q.Parameters) q2.Parameters.AddWithValue(p.ParameterName, p.Value);
                q2.CommandText = "SELECT TOP (" + top + ") CAST(g.Id AS varchar(36)), g.Name, g.KBId, g.Severity, g.Classification, g.IsSuperseded, g.IsExpired, g.ClientCountRequiring, g.ClientCountInstalled, g.ClientCountFailed, g.Comment, g.[Date] " +
                    baseSql + (includeAuto ? "" : " AND (g.Comment IS NULL OR g.Comment NOT LIKE '" + AutoDeclinePrefix + "%')") + " ORDER BY g.[Date] DESC";
                using (var r = q2.ExecuteReader())
                    while (r.Read())
                    {
                        var o = new JObject();
                        o["id"] = "{" + r.GetString(0).ToUpperInvariant() + "}"; o["name"] = r.IsDBNull(1) ? null : r.GetString(1);
                        o["kb"] = r.IsDBNull(2) ? null : (JToken)r.GetInt32(2); o["classification"] = r.IsDBNull(4) ? null : r.GetString(4);
                        o["superseded"] = r.GetBoolean(5); o["expired"] = r.GetBoolean(6);
                        o["clientsRequiring"] = r.GetInt32(7); o["clientsInstalled"] = r.GetInt32(8); o["clientsFailed"] = r.GetInt32(9);
                        string cm = r.IsDBNull(10) ? null : r.GetString(10);
                        o["comment"] = cm; o["autoDeclined"] = IsAutoDecline(cm);
                        o["declinedOn"] = OleText(r.GetDouble(11));
                        rows.Add(o);
                    }
            }
            else
            {
                // offene Updates: nicht abgelehnt, nicht abgeloest, nicht abgelaufen - die mit den meisten betroffenen Clients zuerst
                string baseSql = "FROM VW_Wum_UpdateGrid g" + w + " AND g.IsSuperseded = 0 AND g.IsExpired = 0 AND NOT EXISTS (SELECT 1 FROM Sys_Wum_DeclinedUpdates d WHERE d.Id = g.Id)";
                q.CommandText = "SELECT COUNT(*) " + baseSql;
                using (var r = q.ExecuteReader()) { r.Read(); total = r.GetInt32(0); }
                var q2 = c.CreateCommand();
                foreach (SqlParameter p in q.Parameters) q2.Parameters.AddWithValue(p.ParameterName, p.Value);
                q2.CommandText = "SELECT TOP (" + top + ") CAST(g.Id AS varchar(36)), g.Name, g.KBId, g.Severity, g.Classification, g.ClientCountRequiring, g.ClientCountInstalled, g.ClientCountFailed " +
                    baseSql + " ORDER BY g.ClientCountFailed DESC, g.ClientCountRequiring DESC, g.Name";
                using (var r = q2.ExecuteReader())
                    while (r.Read())
                    {
                        var o = new JObject();
                        o["id"] = "{" + r.GetString(0).ToUpperInvariant() + "}"; o["name"] = r.IsDBNull(1) ? null : r.GetString(1);
                        o["kb"] = r.IsDBNull(2) ? null : (JToken)r.GetInt32(2); o["classification"] = r.IsDBNull(4) ? null : r.GetString(4);
                        o["clientsRequiring"] = r.GetInt32(5); o["clientsInstalled"] = r.GetInt32(6); o["clientsFailed"] = r.GetInt32(7);
                        rows.Add(o);
                    }
            }
        }
        var res = new JObject();
        res["scope"] = scope; res["gefunden"] = total; res["gezeigt"] = rows.Count;
        if (scope == "declined")
        {
            res["davonBereinigungsjob"] = auto;
            res["hinweis"] = includeAuto ? "Mit Bereinigungsjob-Eintraegen (includeAuto=true)." : "Ohne die " + auto + " vom Bereinigungsjob abgelehnten Updates (includeAuto=true zeigt sie). Sortiert nach Ablehnungszeit, neueste zuerst.";
        }
        else res["hinweis"] = "Offene Updates (nicht abgelehnt, nicht abgeloest, nicht abgelaufen), mit den meisten Fehlschlaegen zuerst. Ablehnen: acmp_wum_decline mit der Id.";
        res["updates"] = rows;
        return res;
    }

    // ================= Planen =================
    public JObject Plan(string tool, JObject a, string requester)
    {
        if (tool == "acmp_wum_decline") return PlanDecline(a, requester);
        if (tool == "acmp_wum_restore") return PlanRestore(a);
        throw new Exception("Unbekanntes WUM-Tool: " + tool);
    }

    JObject PlanDecline(JObject a, string requester)
    {
        List<string> ids = IdList(a);
        string comment = DeclineComment(a["comment"] != null && a["comment"].Type == JTokenType.String ? (string)a["comment"] : null, requester);
        var items = new JArray(); var lines = new List<string>(); var skipped = new List<string>();
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            int no = 0;
            foreach (string id in ids)
            {
                JObject it = Lookup(c, id);
                if (it == null) throw new Exception("Update " + id + " gibt es nicht (Sys_Wum_Updates) - nichts geplant.");
                if ((bool)it["declined"]) { skipped.Add(ItemLabel(it) + " ist schon abgelehnt" + (!string.IsNullOrEmpty((string)it["comment"]) ? " (\"" + OneLine((string)it["comment"], 80) + "\")" : "") + " - uebersprungen"); continue; }
                if ((bool)it["expired"] || (bool)it["revoked"]) throw new Exception(ItemLabel(it) + " ist " + ((bool)it["revoked"] ? "widerrufen" : "abgelaufen") + " - das Tool lehnt so etwas nicht ab (in der Console pruefen).");
                if ((bool)it["superseded"]) throw new Exception(ItemLabel(it) + " ist durch ein neueres Update abgeloest - eine Ablehnung bringt nichts. Das neuere Update ist der Kandidat (acmp_wum_declined scope=open search=KB-Nummer).");
                items.Add(it);
                lines.Add(++no + ". " + ItemLabel(it) + " - " + ((string)it["classification"] ?? "ohne Klassifizierung") + ", Testring " + it["testRing"]);
                lines.Add("   Betroffen: " + it["requiring"] + " Client(s) brauchen es, " + it["installed"] + " haben es installiert, " + it["failed"] + " mit Fehlschlag.");
                foreach (string w in DeclineWarnings(it)) lines.Add("   " + w);
            }
        }
        if (items.Count == 0) throw new Exception("Nichts zu tun: " + string.Join("; ", skipped.ToArray()));
        var plan = new JObject();
        plan["action"] = "decline"; plan["comment"] = comment; plan["items"] = items; plan["skipped"] = new JArray(skipped.ToArray());
        var pv = new JArray();
        pv.Add("Windows-Update(s) in ACMP ABLEHNEN (wie 'Ablehnen' in der Console, Windows Update Management): " + items.Count + " Update(s), eine Transaktion, Sicherung vorher.");
        pv.Add("Kommentar (steht in der Console): " + comment);
        foreach (string l in lines) pv.Add(l);
        foreach (string s in skipped) pv.Add("Uebersprungen: " + s);
        pv.Add("Wirkung: abgelehnte Updates werden Clients nicht mehr angeboten; bereits installierte bleiben. Zurueck mit acmp_wum_restore (Ablehnung aufheben).");
        plan["vorschau"] = pv;
        return plan;
    }

    JObject PlanRestore(JObject a)
    {
        List<string> ids = IdList(a);
        var items = new JArray(); var lines = new List<string>();
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            int no = 0;
            foreach (string id in ids)
            {
                JObject it = Lookup(c, id);
                if (it == null) throw new Exception("Update " + id + " gibt es nicht (Sys_Wum_Updates) - nichts geplant.");
                if (!(bool)it["declined"]) throw new Exception(ItemLabel(it) + " ist nicht abgelehnt - nichts aufzuheben.");
                items.Add(it);
                bool auto = IsAutoDecline((string)it["comment"]);
                lines.Add(++no + ". " + ItemLabel(it) + " - abgelehnt am " + OleText((double)it["date"]) + ", Kommentar: " + (!string.IsNullOrEmpty((string)it["comment"]) ?"\"" + OneLine((string)it["comment"], 120) + "\"" : "(ohne)"));
                lines.Add("   " + it["requiring"] + " Client(s) brauchen es, " + it["installed"] + " haben es installiert, " + it["failed"] + " mit Fehlschlag.");
                if (auto) lines.Add("   Hinweis: vom Bereinigungsjob abgelehnt (unbenutzt) - der Job kann es wieder ablehnen, wenn es weiter unbenutzt bleibt.");
                if ((bool)it["superseded"]) lines.Add("   Hinweis: durch ein neueres Update abgeloest - wird Clients auch nach dem Aufheben nicht angeboten.");
            }
        }
        var plan = new JObject();
        plan["action"] = "restore"; plan["items"] = items;
        var pv = new JArray();
        pv.Add("Ablehnung von " + items.Count + " Windows-Update(s) in ACMP AUFHEBEN: die Zeile in Sys_Wum_DeclinedUpdates wird geloescht (eine Transaktion, Sicherung vorher), danach werden die Update-Flags neu aufgebaut.");
        foreach (string l in lines) pv.Add(l);
        pv.Add("Hinweis: Das Aufheben ist noch nicht gegen die Console belegt (die Console macht es vermutlich ebenso) - nach dem ersten Einsatz in der Console pruefen, dass das Update nicht mehr unter 'Abgelehnt' steht.");
        pv.Add("Wirkung: Das Update wird den Clients wieder angeboten, soweit Collections und Testring es zulassen. Wieder ablehnen mit acmp_wum_decline.");
        plan["vorschau"] = pv;
        return plan;
    }

    static string OneLine(string s, int max)
    {
        string t = System.Text.RegularExpressions.Regex.Replace(s ?? "", @"\s+", " ").Trim();
        return t.Length > max ? t.Substring(0, max - 3) + "..." : t;
    }

    // ================= Schreiben =================
    // Fuehrt einen Plan aus (den der Server direkt vorher mit Plan() erstellt hat; ohne _plan wird nichts geschrieben)
    public JObject Execute(string tool, JObject a, string requester)
    {
        string action = tool == "acmp_wum_restore" ? "restore" : "decline";
        CheckWrite(requester, action);
        JObject p = a["_plan"] as JObject;
        if (p == null) throw new Exception(tool + " schreibt nur mit einem vom Server erstellten Plan - nichts geschrieben.");
        if ((string)p["action"] != action) throw new Exception("Plan passt nicht zum Tool (" + p["action"] + " / " + tool + ") - nichts geschrieben.");
        lock (_wLock)
        {
            return action == "decline" ? ExecDecline(p, requester) : ExecRestore(p, requester);
        }
    }

    string SaveBackup(JObject b, string kind)
    {
        Directory.CreateDirectory(_backupDir);
        string file = Path.Combine(_backupDir, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "_" + kind + ".json");
        File.WriteAllText(file, b.ToString(Formatting.Indented), new UTF8Encoding(false));
        if (new FileInfo(file).Length < 40) throw new Exception("Sicherung unvollstaendig - nichts geaendert.");
        return file;
    }

    // Flags neu aufbauen (Schreib-Login, nicht fatal) und mit dem Lese-Login pruefen, ob IsDeclined jetzt dem erwarteten Wert entspricht
    string RebuildAndVerify(SqlConnection wc, SqlConnection rc, List<string> ids, bool expectDeclined, out bool flagsOk)
    {
        string note = null;
        flagsOk = false;
        if (_rebuildFlags)
        {
            try
            {
                var q = wc.CreateCommand(); q.CommandType = CommandType.StoredProcedure; q.CommandText = "dbo.ap_Wum_Rebuild_UpdateFlags"; q.CommandTimeout = 120;
                q.ExecuteNonQuery();
            }
            catch (Exception ex) { note = "Flags nicht neu aufgebaut (" + ex.Message + ") - ACMP holt das beim naechsten Lauf nach."; }
        }
        else note = "Flags nicht neu aufgebaut (wumWriter.rebuildFlags=false) - ACMP holt das beim naechsten Lauf nach.";
        int ok = 0;
        foreach (string id in ids)
        {
            var q = rc.CreateCommand();
            q.CommandText = "SELECT COUNT(*) FROM Sys_Wum_Updates WHERE Id = @id AND IsDeclined = @d AND RevisionNumber = (SELECT MAX(RevisionNumber) FROM Sys_Wum_Updates WHERE Id = @id)";
            q.Parameters.AddWithValue("@id", G(id)); q.Parameters.AddWithValue("@d", expectDeclined ? 1 : 0);
            if (Convert.ToInt32(q.ExecuteScalar()) > 0) ok++;
        }
        flagsOk = ok == ids.Count;
        if (!flagsOk && note == null) note = "Das Flag IsDeclined steht noch nicht bei allen Updates auf " + (expectDeclined ? "1" : "0") + " (" + ok + "/" + ids.Count + ") - ACMP holt das beim naechsten Lauf nach.";
        return note;
    }

    // Stand der Updates mit dem Lese-Login pruefen (der Schreib-Login darf nur die Prozeduren und Sys_Wum_DeclinedUpdates)
    List<string> Revalidate(SqlConnection rc, string action, JArray items, JArray before)
    {
        var ids = new List<string>();
        foreach (JObject planned in items)
        {
            string id = (string)planned["id"];
            JObject now = Lookup(rc, id);
            List<string> d = PlanDiff(action, planned, now);
            if (d.Count > 0) throw new Exception("Update " + id + " (\"" + planned["name"] + "\") hat sich seit der Planung geaendert: " + string.Join("; ", d.ToArray()) + " - nichts geschrieben, bitte erneut aufrufen.");
            if (action == "decline" && (bool)now["revoked"]) throw new Exception("Update " + id + " ist inzwischen widerrufen - nichts geschrieben.");
            ids.Add(id); before.Add(now);
        }
        return ids;
    }

    JObject ExecDecline(JObject p, string requester)
    {
        var items = (JArray)p["items"];
        string comment = (string)p["comment"];
        var before = new JArray();
        List<string> ids;
        string backup;
        bool flagsOk; string note;
        using (var rc = new SqlConnection(_readCs))
        using (var wc = new SqlConnection(_writeCs))
        {
            rc.Open(); wc.Open();
            ids = Revalidate(rc, "decline", items, before);
            var b = new JObject(); b["kind"] = "decline"; b["grund"] = "vor acmp_wum_decline"; b["von"] = requester; b["zeit"] = DateTime.Now.ToString("s");
            b["comment"] = comment; b["updates"] = before;
            backup = SaveBackup(b, "decline");
            var dt = new DataTable(); dt.Columns.Add("Id", typeof(Guid));
            foreach (string id in ids) dt.Rows.Add(G(id));
            double now0 = DateTime.Now.ToOADate();
            using (SqlTransaction tx = wc.BeginTransaction())
            {
                var q = wc.CreateCommand(); q.Transaction = tx; q.CommandType = CommandType.StoredProcedure; q.CommandText = "dbo.ap_Wum_DeclineUpdates";
                SqlParameter tp = q.Parameters.AddWithValue("@aItemIds", dt); tp.SqlDbType = SqlDbType.Structured; tp.TypeName = "dbo.at_IDList";
                q.Parameters.AddWithValue("@aComment", comment);
                q.Parameters.AddWithValue("@aNow", now0);
                q.ExecuteNonQuery();
                // Kontrolle in derselben Transaktion: jede Id steht jetzt mit unserem Kommentar in der Tabelle
                foreach (string id in ids)
                {
                    var vq = wc.CreateCommand(); vq.Transaction = tx;
                    vq.CommandText = "SELECT COUNT(*) FROM Sys_Wum_DeclinedUpdates WHERE Id = @id AND Comment = @c";
                    vq.Parameters.AddWithValue("@id", G(id)); vq.Parameters.AddWithValue("@c", comment);
                    if (Convert.ToInt32(vq.ExecuteScalar()) != 1) throw new Exception("Update " + id + " steht nach der Prozedur nicht mit dem Kommentar in Sys_Wum_DeclinedUpdates - zurueckgerollt, nichts geschrieben.");
                }
                tx.Commit();
            }
            note = RebuildAndVerify(wc, rc, ids, true, out flagsOk);
        }
        var journal = new JObject(); journal["kind"] = "decline"; journal["von"] = requester; journal["zeit"] = DateTime.Now.ToString("s"); journal["plan"] = p;
        string jf = SaveBackup(journal, "declinelog");
        _log("acmp_wum_decline von " + requester + ": " + string.Join(", ", ids.ToArray()) + " (Flags " + (flagsOk ? "gesetzt" : "noch offen") + "), Sicherung " + Path.GetFileName(backup));
        var o = new JObject();
        o["ok"] = true; o["abgelehnt"] = new JArray(ids.ToArray()); o["flagIsDeclined"] = flagsOk;
        var names = new JArray(); foreach (JObject it in items) names.Add((string)it["name"]); o["namen"] = names;
        if (note != null) o["hinweis"] = note;
        o["sicherung"] = Path.GetFileName(backup); o["protokoll"] = Path.GetFileName(jf);
        o["zurueck"] = "acmp_wum_restore mit denselben updateIds hebt die Ablehnung wieder auf.";
        return o;
    }

    JObject ExecRestore(JObject p, string requester)
    {
        var items = (JArray)p["items"];
        var before = new JArray();
        List<string> ids;
        string backup;
        bool flagsOk; string note;
        using (var rc = new SqlConnection(_readCs))
        using (var wc = new SqlConnection(_writeCs))
        {
            rc.Open(); wc.Open();
            ids = Revalidate(rc, "restore", items, before);
            var b = new JObject(); b["kind"] = "restore"; b["grund"] = "vor acmp_wum_restore"; b["von"] = requester; b["zeit"] = DateTime.Now.ToString("s"); b["updates"] = before;
            backup = SaveBackup(b, "restore");
            using (SqlTransaction tx = wc.BeginTransaction())
            {
                foreach (JObject planned in items)
                {
                    var q = wc.CreateCommand(); q.Transaction = tx;
                    q.CommandText = "DELETE FROM Sys_Wum_DeclinedUpdates WHERE Id = @id AND ISNULL(Comment, N'') = @c AND [Date] = @d";
                    q.Parameters.AddWithValue("@id", G((string)planned["id"]));
                    q.Parameters.AddWithValue("@c", planned["comment"] == null || planned["comment"].Type == JTokenType.Null ? "" : (string)planned["comment"]);
                    q.Parameters.AddWithValue("@d", (double)planned["date"]);
                    if (q.ExecuteNonQuery() != 1) throw new Exception("Ablehnung von " + planned["id"] + " nicht eindeutig aufgehoben - zurueckgerollt, nichts geschrieben.");
                }
                tx.Commit();
            }
            note = RebuildAndVerify(wc, rc, ids, false, out flagsOk);
        }
        var journal = new JObject(); journal["kind"] = "restore"; journal["von"] = requester; journal["zeit"] = DateTime.Now.ToString("s"); journal["plan"] = p;
        string jf = SaveBackup(journal, "restorelog");
        _log("acmp_wum_restore von " + requester + ": " + string.Join(", ", ids.ToArray()) + " (Flags " + (flagsOk ? "zurueck" : "noch offen") + "), Sicherung " + Path.GetFileName(backup));
        var o = new JObject();
        o["ok"] = true; o["aufgehoben"] = new JArray(ids.ToArray()); o["flagIsDeclinedZurueck"] = flagsOk;
        if (note != null) o["hinweis"] = note;
        o["sicherung"] = Path.GetFileName(backup); o["protokoll"] = Path.GetFileName(jf);
        o["zurueck"] = "acmp_wum_decline mit denselben updateIds und dem Kommentar aus der Sicherung lehnt es wieder ab.";
        return o;
    }
}
