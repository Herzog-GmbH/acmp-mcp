// AcmpCcPreflight: Kompatibilitaetspruefung, bevor der CC-Writer in eine ACMP-Datenbank schreiben darf.
// Der Writer kennt das Speicherformat einer bestimmten ACMP-Version (aus Beobachtung, nicht von Aagon dokumentiert).
// Diese Pruefung stellt fest, ob die Installation dazu passt: ACMP-Version, Tabellen und Spalten, Trigger,
// gleiche Datenbank fuer Lese- und Schreib-Login, Rechte des Schreib-Logins, Vorlage, Ordner, File Repository
// und ein Rundlauf an echten Commands (entpacken -> wieder packen -> bytegleich). Es wird nichts geschrieben.
// Ergebnis "block" sperrt das Schreiben; "warn" nur Hinweis. Version und Trigger lassen sich per
// ccWriter.allowUntested bewusst freigeben, Schema und Rundlauf nie.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using Newtonsoft.Json.Linq;

static class AcmpCcPreflight
{
    // Getestete ACMP-Versionen (Major.Minor), Formatanalyse und Tests mit 6.10.4.
    public static readonly string[] TestedVersions = { "6.10" };

    // Tabellen, in die der Writer schreibt: Spalten muessen EXAKT so aussehen (keine fehlenden, keine zusaetzlichen),
    // sonst passt das INSERT bzw. die Bedeutung der Zeile nicht mehr. Format Name:Typ:Laenge (Laenge -1 = max, leer = ohne).
    static readonly Dictionary<string, string[]> ExactTables = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) {
        { "SYS_SCRIPTS", new[] {
            "SCRIPTID:varchar:40", "ScriptVersion:int:", "Predecessor:int:", "FileRepoVersion:int:", "MaxUsedFileRepoVersion:int:",
            "DESCRIPTION:nvarchar:250", "LONGDESCRIPTION:nvarchar:-1", "PASSWORD:nvarchar:250", "FOLDERID:varchar:40",
            "CLIENTSCRIPT:varbinary:-1", "CONSOLESCRIPT:varbinary:-1", "CONDITIONS:varbinary:-1", "ISQUERYSHORTCUT:smallint:",
            "ISACTBARSHORTCUT:smallint:", "DOSTORECLIENTLOG:smallint:", "DOSHOWCONSOLELOG:smallint:", "HASCONSOLESCRIPT:smallint:",
            "ABOUTINFOS:varbinary:-1", "HASCLIENTSCRIPT:smallint:", "PRIORITY:int:", "VERSION:int:", "ICONID:varchar:40",
            "ALLOWINTERACTIVEUSE:smallint:", "COMMANDENABLED:smallint:", "HasConditions:bit:", "State:tinyint:", "Creator:nvarchar:100",
            "CreationDate:float:", "LastModifier:nvarchar:100", "LastModifiedDate:float:", "FileRepoSize:bigint:",
            "IsFileRepoVersionIncreased:bit:", "IsParallelCC:bit:", "ConsoleRelations:tinyint:", "ClientRelations:tinyint:", "LicensedCommands:tinyint:" } },
        { "SYS_SCRIPTS_VARIABLES", new[] { "ScriptId:varchar:40", "ScriptVersion:int:", "Name:nvarchar:200", "Origin:tinyint:", "ReadOnly:bit:" } },
        { "SYS_SCRIPT_FILES", new[] { "FileID:uniqueidentifier:", "ScriptID:varchar:40", "FileRepoVersion:int:", "Path:nvarchar:-1", "IsFolder:bit:", "PreviousFileID:uniqueidentifier:", "sha256:binary:32" } },
        { "SYS_AccessControl_AclEntries_Users", new[] { "ObjectID:uniqueidentifier:", "AccessID:uniqueidentifier:", "EditState:tinyint:", "VisibleState:tinyint:" } },
        { "SYS_AccessControl_AclEntries_Groups", new[] { "ObjectID:uniqueidentifier:", "AccessID:uniqueidentifier:", "EditState:tinyint:", "VisibleState:tinyint:" } },
    };

    // Tabellen/Views, die nur gelesen werden: diese Spalten muessen vorhanden sein (weitere sind erlaubt).
    static readonly Dictionary<string, string[]> ReadTables = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) {
        { "SYS_SCRIPT_STRUCTURE", new[] { "ScriptStructureID", "Description" } },
        { "SYS_Folders_AclEntries_Users", new[] { "ObjectID", "AccessID", "EditState", "VisibleState" } },
        { "SYS_Folders_AclEntries_Groups", new[] { "ObjectID", "AccessID", "EditState", "VisibleState" } },
        { "VW_SYS_CustomFields", new[] { "CustomFieldId", "TableId", "InputMethod", "MaxLength", "Caption_de", "Caption_en" } },
        { "SYS_TableRelations", new[] { "TableID", "TableDisplayName", "FolderId" } },
        { "SYS_Folders", new[] { "FolderID", "Caption", "ParentID" } },
    };

    // Bekannte Trigger auf den beschriebenen Tabellen (Tabelle.Trigger). Ein unbekannter Trigger koennte unsere Zeilen
    // veraendern oder verwerfen -> sperrt (ausser allowUntested). Stand ACMP 6.10.4 (erste Pruefung 14.10.2026).
    public static readonly string[] KnownTriggers = { "SYS_SCRIPTS.Trg_SYS_SCRIPTS_Update", "SYS_SCRIPTS.Trg_SYS_SCRIPTS_Delete", "SYS_SCRIPT_FILES.TR_SYS_SCRIPT_FILES_DELETE" };

    // Rechte, die der Schreib-Login braucht (Tabelle, Recht). Dateien: eigene Liste, fehlt sie, sind nur die Datei-Tools gesperrt.
    static readonly string[][] NeededPerms = {
        new[] { "SYS_SCRIPTS", "SELECT" }, new[] { "SYS_SCRIPTS", "INSERT" }, new[] { "SYS_SCRIPTS", "UPDATE" },
        new[] { "SYS_SCRIPTS_VARIABLES", "SELECT" }, new[] { "SYS_SCRIPTS_VARIABLES", "INSERT" }, new[] { "SYS_SCRIPTS_VARIABLES", "DELETE" },
        new[] { "SYS_SCRIPT_STRUCTURE", "SELECT" },
        new[] { "SYS_Folders_AclEntries_Users", "SELECT" }, new[] { "SYS_Folders_AclEntries_Groups", "SELECT" },
        new[] { "SYS_AccessControl_AclEntries_Users", "INSERT" }, new[] { "SYS_AccessControl_AclEntries_Groups", "INSERT" } };
    static readonly string[][] FilePerms = {
        new[] { "SYS_SCRIPT_FILES", "SELECT" }, new[] { "SYS_SCRIPT_FILES", "INSERT" }, new[] { "SYS_SCRIPT_FILES", "DELETE" } };

    // acmp_cc_add_folder (AcmpCcFolders.cs) schreibt den Ordnerbaum und die Ordner-Rechte. Eigene Pruefung "Ordner anlegen":
    // Abweichungen sperren nur dieses Tool, nicht den uebrigen CC-Writer. Verschieben (acmp_cc_move) aendert nur
    // SYS_SCRIPTS.FOLDERID und ist mit der Hauptpruefung abgedeckt (UPDATE-Recht, bekannte Trigger).
    static readonly Dictionary<string, string[]> FolderTables = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) {
        { "SYS_SCRIPT_STRUCTURE", new[] { "ScriptStructureID:varchar:40", "Description:nvarchar:200", "ParentID:varchar:40", "StrucLevel:int:", "IconId:varchar:40" } },
        { "SYS_Folders_AclEntries_Users", new[] { "ObjectID:uniqueidentifier:", "AccessID:uniqueidentifier:", "EditState:tinyint:", "VisibleState:tinyint:" } },
        { "SYS_Folders_AclEntries_Groups", new[] { "ObjectID:uniqueidentifier:", "AccessID:uniqueidentifier:", "EditState:tinyint:", "VisibleState:tinyint:" } },
    };
    // Trigger auf diesen Tabellen, die bekannt und fuers Anlegen harmlos sind. Trg_SYS_SCRIPT_STRUCTURE_Delete (INSTEAD OF DELETE)
    // loescht beim Loeschen eines Ordners die Commands darin und die direkten Unterordner - der MCP loescht keine Ordner.
    public static readonly string[] KnownFolderTriggers = { "SYS_SCRIPT_STRUCTURE.Trg_SYS_SCRIPT_STRUCTURE_Delete" };
    static readonly string[][] FolderPerms = {
        new[] { "SYS_SCRIPT_STRUCTURE", "SELECT" }, new[] { "SYS_SCRIPT_STRUCTURE", "INSERT" },
        new[] { "SYS_Folders_AclEntries_Users", "SELECT" }, new[] { "SYS_Folders_AclEntries_Users", "INSERT" },
        new[] { "SYS_Folders_AclEntries_Groups", "SELECT" }, new[] { "SYS_Folders_AclEntries_Groups", "INSERT" } };
    // acmp_cc_update_folder (umbenennen/verschieben) aendert nur diese beiden Spalten; UPDATE darf auf Spaltenebene vergeben sein
    // (grant-cc-writer.sql, Abschnitt 6). Eigene Pruefung "Ordner aendern", sperrt nur dieses Tool.
    static readonly string[][] FolderEditColumns = { new[] { "SYS_SCRIPT_STRUCTURE", "Description" }, new[] { "SYS_SCRIPT_STRUCTURE", "ParentID" } };
    // acmp_cc_release (AcmpCcRelease.cs) aendert je Command nur diese drei Spalten der Zeile in SYS_SCRIPTS und liest SYS_SCRIPT_FILES
    // (mit Sperre in der Transaktion). UPDATE darf auf Spaltenebene vergeben sein. Eigene Pruefung "Freigeben", sperrt nur dieses Tool;
    // Schema und Trigger von SYS_SCRIPTS deckt die Hauptpruefung ab.
    static readonly string[][] ReleaseColumns = { new[] { "SYS_SCRIPTS", "State" }, new[] { "SYS_SCRIPTS", "IsFileRepoVersionIncreased" }, new[] { "SYS_SCRIPTS", "FileRepoSize" } };

    public static JObject Run(string readCs, string writeCs, string acmpVersion, bool allowUntested, string templateId,
                              ICollection<string> allowedFolders, string fileRepoRoot, int sample)
    {
        var checks = new JArray();
        bool fileTools = true, folderTool = false, folderEditTool = false, releaseTool = false;
        try
        {
            CheckVersion(checks, acmpVersion, allowUntested);
            using (var rc = new SqlConnection(readCs))
            using (var wc = new SqlConnection(writeCs))
            {
                rc.Open();
                try { wc.Open(); }
                catch (Exception ex) { Add(checks, "Schreib-Login", "block", "Anmeldung fehlgeschlagen: " + ex.Message); return Result(checks, false, false, false, false, acmpVersion); }
                CheckSameDb(checks, rc, wc);
                CheckSchema(checks, rc);
                CheckTriggers(checks, wc, allowUntested);
                if (!CheckPerms(checks, wc)) fileTools = false;
                CheckTemplate(checks, wc, templateId);
                CheckFolders(checks, wc, allowedFolders);
                CheckRoundtrip(checks, rc, sample);
                CheckFolderTools(checks, rc, wc, allowUntested, out folderTool, out folderEditTool);
                CheckReleaseTool(checks, wc, out releaseTool);
            }
            if (string.IsNullOrEmpty(fileRepoRoot) || !Directory.Exists(fileRepoRoot))
            {
                Add(checks, "File Repository", "warn", "Ordner nicht gefunden: " + (fileRepoRoot ?? "(nicht gesetzt)") + " (ccWriter.fileRepositoryRoot; der MCP muss auf dem ACMP-Server laufen). Datei-Tools gesperrt.");
                fileTools = false;
            }
            else Add(checks, "File Repository", "ok", fileRepoRoot);
        }
        catch (Exception ex) { Add(checks, "Pruefung", "block", "Abgebrochen: " + ex.Message); }
        return Result(checks, fileTools, folderTool, folderEditTool, releaseTool, acmpVersion);
    }

    static JObject Result(JArray checks, bool fileTools, bool folderTool, bool folderEditTool, bool releaseTool, string acmpVersion)
    {
        var blocks = new List<string>(); int warns = 0;
        foreach (JObject c in checks)
        {
            if ((string)c["result"] == "block") blocks.Add((string)c["check"] + ": " + (string)c["detail"]);
            else if ((string)c["result"] == "warn") warns++;
        }
        var o = new JObject();
        o["ok"] = blocks.Count == 0;
        o["fileTools"] = blocks.Count == 0 && fileTools;
        o["folderTool"] = blocks.Count == 0 && folderTool;
        o["folderEditTool"] = blocks.Count == 0 && folderEditTool;
        o["releaseTool"] = blocks.Count == 0 && releaseTool;
        o["acmpVersion"] = acmpVersion;
        o["checkedLocal"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        o["summary"] = blocks.Count == 0 ? ("bestanden" + (warns > 0 ? " (" + warns + " Hinweis(e))" : "")) : "GESPERRT: " + string.Join(" | ", blocks.ToArray());
        o["checks"] = checks;
        return o;
    }

    static void Add(JArray checks, string name, string result, string detail)
    {
        var c = new JObject(); c["check"] = name; c["result"] = result; c["detail"] = detail; checks.Add(c);
    }

    static void CheckVersion(JArray checks, string v, bool allowUntested)
    {
        string mm = null;
        if (!string.IsNullOrEmpty(v)) { string[] p = v.Split('.'); if (p.Length >= 2) mm = p[0] + "." + p[1]; }
        if (mm != null && Array.IndexOf(TestedVersions, mm) >= 0) { Add(checks, "ACMP-Version", "ok", v + " (getestet: " + string.Join(", ", TestedVersions) + ")"); return; }
        string what = (mm == null ? "nicht ermittelbar" : v + " ist nicht getestet") + " (getestet: " + string.Join(", ", TestedVersions) + ")";
        if (allowUntested) Add(checks, "ACMP-Version", "warn", what + " - per ccWriter.allowUntested freigegeben");
        else Add(checks, "ACMP-Version", "block", what + ". Freischalten nur bewusst mit ccWriter.allowUntested = true.");
    }

    static void CheckSameDb(JArray checks, SqlConnection rc, SqlConnection wc)
    {
        string a = Scalar(rc, "SELECT @@SERVERNAME + '/' + DB_NAME()"), b = Scalar(wc, "SELECT @@SERVERNAME + '/' + DB_NAME()");
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) Add(checks, "Datenbank", "ok", a + " (Lese- und Schreib-Login gleich)");
        else Add(checks, "Datenbank", "block", "Lese-Login auf " + a + ", Schreib-Login auf " + b + " - muessen dieselbe ACMP-Datenbank sein.");
    }

    static string Scalar(SqlConnection c, string sql)
    {
        var q = c.CreateCommand(); q.CommandText = sql;
        object o = q.ExecuteScalar();
        return o == null || o is DBNull ? null : o.ToString();
    }

    static void CheckSchema(JArray checks, SqlConnection rc)
    {
        var have = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>(ExactTables.Keys); names.AddRange(ReadTables.Keys);
        var q = rc.CreateCommand();
        var ps = new List<string>();
        for (int i = 0; i < names.Count; i++) { ps.Add("@t" + i); q.Parameters.AddWithValue("@t" + i, names[i]); }
        q.CommandText = "SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS " +
                        "WHERE TABLE_SCHEMA='dbo' AND TABLE_NAME IN (" + string.Join(",", ps.ToArray()) + ")";
        using (var r = q.ExecuteReader())
            while (r.Read())
            {
                Dictionary<string, string> cols;
                if (!have.TryGetValue(r.GetString(0), out cols)) { cols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); have[r.GetString(0)] = cols; }
                cols[r.GetString(1)] = r.GetString(2) + ":" + (r.IsDBNull(3) ? "" : Convert.ToInt32(r.GetValue(3)).ToString());
            }
        foreach (var kv in ExactTables)
        {
            Dictionary<string, string> cols;
            if (!have.TryGetValue(kv.Key, out cols)) { Add(checks, "Schema " + kv.Key, "block", "Tabelle fehlt."); continue; }
            var diffs = new List<string>();
            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string spec in kv.Value)
            {
                string[] p = spec.Split(':'); expected.Add(p[0]);
                string got;
                if (!cols.TryGetValue(p[0], out got)) diffs.Add("fehlt " + p[0]);
                else if (!string.Equals(got, p[1] + ":" + p[2], StringComparison.OrdinalIgnoreCase)) diffs.Add(p[0] + " ist " + got + " statt " + p[1] + ":" + p[2]);
            }
            foreach (string c in cols.Keys) if (!expected.Contains(c)) diffs.Add("zusaetzliche Spalte " + c);
            if (diffs.Count == 0) Add(checks, "Schema " + kv.Key, "ok", kv.Value.Length + " Spalten wie erwartet");
            else Add(checks, "Schema " + kv.Key, "block", string.Join("; ", diffs.ToArray()));
        }
        foreach (var kv in ReadTables)
        {
            Dictionary<string, string> cols;
            if (!have.TryGetValue(kv.Key, out cols)) { Add(checks, "Schema " + kv.Key, "block", "Tabelle/View fehlt."); continue; }
            var miss = new List<string>();
            foreach (string c in kv.Value) if (!cols.ContainsKey(c)) miss.Add(c);
            if (miss.Count == 0) Add(checks, "Schema " + kv.Key, "ok", "benoetigte Spalten vorhanden");
            else Add(checks, "Schema " + kv.Key, "block", "fehlende Spalten: " + string.Join(", ", miss.ToArray()));
        }
    }

    static void CheckTriggers(JArray checks, SqlConnection wc, bool allowUntested)
    {
        var tables = new List<string>(ExactTables.Keys);
        var q = wc.CreateCommand();
        var ps = new List<string>();
        for (int i = 0; i < tables.Count; i++) { ps.Add("OBJECT_ID(@t" + i + ")"); q.Parameters.AddWithValue("@t" + i, "dbo." + tables[i]); }
        q.CommandText = "SELECT OBJECT_NAME(parent_id), name, CAST(is_disabled AS int) FROM sys.triggers WHERE parent_id IN (" + string.Join(",", ps.ToArray()) + ")";
        var found = new List<string>(); var unknown = new List<string>();
        using (var r = q.ExecuteReader())
            while (r.Read())
            {
                string t = r.GetString(0) + "." + r.GetString(1) + (r.GetInt32(2) != 0 ? " (deaktiviert)" : "");
                found.Add(t);
                if (Array.IndexOf(KnownTriggers, r.GetString(0) + "." + r.GetString(1)) < 0) unknown.Add(t);
            }
        if (found.Count == 0) { Add(checks, "Trigger", "warn", "Keine Trigger sichtbar (erwartet: " + string.Join(", ", KnownTriggers) + ") - fehlen dem Schreib-Login Metadaten-Rechte?"); return; }
        if (unknown.Count == 0) { Add(checks, "Trigger", "ok", string.Join(", ", found.ToArray())); return; }
        string d = "Unbekannte Trigger: " + string.Join(", ", unknown.ToArray()) + " (alle gefunden: " + string.Join(", ", found.ToArray()) + ")";
        if (allowUntested) Add(checks, "Trigger", "warn", d + " - per ccWriter.allowUntested freigegeben");
        else Add(checks, "Trigger", "block", d + ". Ein unbekannter Trigger kann angelegte Zeilen veraendern.");
    }

    // false = Datei-Rechte fehlen (nur Datei-Tools gesperrt)
    static bool CheckPerms(JArray checks, SqlConnection wc)
    {
        var miss = new List<string>(); var fmiss = new List<string>();
        foreach (string[] p in NeededPerms) if (!HasPerm(wc, p[0], p[1])) miss.Add(p[0] + " " + p[1]);
        foreach (string[] p in FilePerms) if (!HasPerm(wc, p[0], p[1])) fmiss.Add(p[0] + " " + p[1]);
        if (miss.Count > 0) Add(checks, "Rechte Schreib-Login", "block", "fehlen: " + string.Join(", ", miss.ToArray()));
        else Add(checks, "Rechte Schreib-Login", "ok", NeededPerms.Length + " benoetigte Rechte vorhanden");
        if (fmiss.Count > 0) Add(checks, "Rechte Dateien", "warn", "fehlen: " + string.Join(", ", fmiss.ToArray()) + " - Datei-Tools gesperrt");
        // Zu viele Rechte: nicht sperren, aber melden (Prinzip der geringsten Rechte)
        var extra = new List<string>();
        if (HasPerm(wc, "SYS_SCRIPTS", "DELETE")) extra.Add("DELETE auf SYS_SCRIPTS");
        if (Scalar(wc, "SELECT CAST(IS_ROLEMEMBER('db_owner') AS varchar(5))") == "1") extra.Add("Mitglied von db_owner");
        if (Scalar(wc, "SELECT CAST(IS_SRVROLEMEMBER('sysadmin') AS varchar(5))") == "1") extra.Add("sysadmin");
        if (extra.Count > 0) Add(checks, "Rechte (zu viele)", "warn", "Der Schreib-Login hat mehr Rechte als noetig: " + string.Join(", ", extra.ToArray()) + ". Empfohlen: nur die benoetigten Rechte.");
        return fmiss.Count == 0;
    }

    static bool HasPerm(SqlConnection c, string table, string perm)
    {
        var q = c.CreateCommand();
        q.CommandText = "SELECT HAS_PERMS_BY_NAME(@o, 'OBJECT', @p)";
        q.Parameters.AddWithValue("@o", "dbo." + table); q.Parameters.AddWithValue("@p", perm);
        object o = q.ExecuteScalar();
        return o != null && !(o is DBNull) && Convert.ToInt32(o) == 1;
    }

    static void CheckTemplate(JArray checks, SqlConnection wc, string templateId)
    {
        var q = wc.CreateCommand();
        q.CommandText = "SELECT TOP 1 CAST(HasConditions AS int), DESCRIPTION FROM SYS_SCRIPTS WHERE SCRIPTID=@t ORDER BY ScriptVersion DESC";
        q.Parameters.AddWithValue("@t", templateId ?? "");
        using (var r = q.ExecuteReader())
        {
            if (!r.Read()) { Add(checks, "Vorlage", "block", "ccWriter.templateScriptId " + templateId + " nicht gefunden."); return; }
            if (r.GetInt32(0) != 0) { Add(checks, "Vorlage", "block", "'" + (r.IsDBNull(1) ? "" : r.GetString(1)) + "' hat Bedingungen - als Vorlage nur einen Command ohne Bedingungen verwenden."); return; }
            Add(checks, "Vorlage", "ok", "'" + (r.IsDBNull(1) ? "" : r.GetString(1)) + "' " + templateId);
        }
    }

    static void CheckFolders(JArray checks, SqlConnection wc, ICollection<string> folders)
    {
        if (folders == null || folders.Count == 0) { Add(checks, "Ordner", "warn", "ccWriter.allowedFolderIds ist leer - Entwuerfe duerfen in jeden Ordner. Empfohlen: ein Testordner."); return; }
        var good = new List<string>(); var bad = new List<string>();
        foreach (string f in folders)
        {
            var q = wc.CreateCommand();
            q.CommandText = "SELECT s.Description, (SELECT COUNT(*) FROM SYS_Folders_AclEntries_Users u WHERE u.ObjectID=@g) + (SELECT COUNT(*) FROM SYS_Folders_AclEntries_Groups g WHERE g.ObjectID=@g) " +
                            "FROM SYS_SCRIPT_STRUCTURE s WHERE s.ScriptStructureID=@f";
            q.Parameters.AddWithValue("@f", f);
            Guid gid; q.Parameters.AddWithValue("@g", Guid.TryParse(f.Trim('{', '}'), out gid) ? gid : Guid.Empty);
            using (var r = q.ExecuteReader())
            {
                if (!r.Read()) { bad.Add(f + " nicht gefunden"); continue; }
                string nm = r.IsDBNull(0) ? "" : r.GetString(0); int acl = Convert.ToInt32(r.GetValue(1));
                if (acl == 0) bad.Add("'" + nm + "' ohne eigene Rechte-Eintraege (Entwurf waere in der Console unsichtbar)");
                else good.Add("'" + nm + "' (" + acl + " Rechte-Eintraege)");
            }
        }
        if (good.Count == 0) Add(checks, "Ordner", "block", "Kein erlaubter Ordner nutzbar: " + string.Join("; ", bad.ToArray()));
        else Add(checks, "Ordner", bad.Count > 0 ? "warn" : "ok", string.Join(", ", good.ToArray()) + (bad.Count > 0 ? " | nicht nutzbar: " + string.Join("; ", bad.ToArray()) : ""));
    }

    // "Ordner anlegen" (acmp_cc_add_folder) und "Ordner aendern" (acmp_cc_update_folder): Spalten exakt, keine unbekannten Trigger
    // (gilt fuer beide), dazu INSERT-Rechte bzw. UPDATE auf Description/ParentID. false = das jeweilige Tool gesperrt.
    static void CheckFolderTools(JArray checks, SqlConnection rc, SqlConnection wc, bool allowUntested, out bool addOk, out bool editOk)
    {
        var problems = new List<string>(); var notes = new List<string>();
        var q = rc.CreateCommand();
        var ps = new List<string>(); int i = 0;
        foreach (string t in FolderTables.Keys) { ps.Add("@t" + i); q.Parameters.AddWithValue("@t" + i, t); i++; }
        q.CommandText = "SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS " +
                        "WHERE TABLE_SCHEMA='dbo' AND TABLE_NAME IN (" + string.Join(",", ps.ToArray()) + ")";
        var have = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        using (var r = q.ExecuteReader())
            while (r.Read())
            {
                Dictionary<string, string> cols;
                if (!have.TryGetValue(r.GetString(0), out cols)) { cols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); have[r.GetString(0)] = cols; }
                cols[r.GetString(1)] = r.GetString(2) + ":" + (r.IsDBNull(3) ? "" : Convert.ToInt32(r.GetValue(3)).ToString());
            }
        foreach (var kv in FolderTables)
        {
            Dictionary<string, string> cols;
            if (!have.TryGetValue(kv.Key, out cols)) { problems.Add(kv.Key + " fehlt"); continue; }
            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string spec in kv.Value)
            {
                string[] p = spec.Split(':'); expected.Add(p[0]);
                string got;
                if (!cols.TryGetValue(p[0], out got)) problems.Add(kv.Key + ": fehlt " + p[0]);
                else if (!string.Equals(got, p[1] + ":" + p[2], StringComparison.OrdinalIgnoreCase)) problems.Add(kv.Key + "." + p[0] + " ist " + got + " statt " + p[1] + ":" + p[2]);
            }
            foreach (string c in cols.Keys) if (!expected.Contains(c)) problems.Add(kv.Key + ": zusaetzliche Spalte " + c);
        }
        var tq = wc.CreateCommand();
        var tps = new List<string>(); i = 0;
        foreach (string t in FolderTables.Keys) { tps.Add("OBJECT_ID(@t" + i + ")"); tq.Parameters.AddWithValue("@t" + i, "dbo." + t); i++; }
        tq.CommandText = "SELECT OBJECT_NAME(parent_id), name FROM sys.triggers WHERE parent_id IN (" + string.Join(",", tps.ToArray()) + ")";
        var unknown = new List<string>();
        using (var r = tq.ExecuteReader())
            while (r.Read())
            {
                string t = r.GetString(0) + "." + r.GetString(1);
                if (Array.IndexOf(KnownFolderTriggers, t) < 0) unknown.Add(t);
            }
        if (unknown.Count > 0)
        {
            if (allowUntested) notes.Add("unbekannte Trigger " + string.Join(", ", unknown.ToArray()) + " - per ccWriter.allowUntested zugelassen");
            else problems.Add("unbekannte Trigger: " + string.Join(", ", unknown.ToArray()) + " (Definition pruefen, dann in KnownFolderTriggers aufnehmen)");
        }
        var miss = new List<string>();
        foreach (string[] p in FolderPerms) if (!HasPerm(wc, p[0], p[1])) miss.Add(p[0] + " " + p[1]);
        var addProblems = new List<string>(problems);
        if (miss.Count > 0) addProblems.Add("Rechte fehlen: " + string.Join(", ", miss.ToArray()) + " (grant-cc-writer.sql, Abschnitt 5)");
        string note = notes.Count > 0 ? "; " + string.Join("; ", notes.ToArray()) : "";
        addOk = addProblems.Count == 0;
        if (addOk) Add(checks, "Ordner anlegen", "ok", "Ordnerbaum und Ordner-Rechte wie erwartet, keine unbekannten Trigger, INSERT-Rechte vorhanden (acmp_cc_add_folder)" + note);
        else Add(checks, "Ordner anlegen", "warn", string.Join("; ", addProblems.ToArray()) + " - acmp_cc_add_folder gesperrt");

        var emiss = new List<string>();
        if (!HasPerm(wc, "SYS_SCRIPT_STRUCTURE", "SELECT")) emiss.Add("SYS_SCRIPT_STRUCTURE SELECT");
        foreach (string[] p in FolderEditColumns) if (!HasColumnPerm(wc, p[0], p[1], "UPDATE")) emiss.Add(p[0] + "." + p[1] + " UPDATE");
        var editProblems = new List<string>(problems);
        if (emiss.Count > 0) editProblems.Add("Rechte fehlen: " + string.Join(", ", emiss.ToArray()) + " (grant-cc-writer.sql, Abschnitt 6)");
        editOk = editProblems.Count == 0;
        if (editOk) Add(checks, "Ordner aendern", "ok", "Ordnerbaum wie erwartet, keine unbekannten Trigger, UPDATE auf Description/ParentID vorhanden (acmp_cc_update_folder)" + note);
        else Add(checks, "Ordner aendern", "warn", string.Join("; ", editProblems.ToArray()) + " - acmp_cc_update_folder gesperrt");
    }

    // "Freigeben" (acmp_cc_release): UPDATE auf State/IsFileRepoVersionIncreased/FileRepoSize, SELECT auf SYS_SCRIPTS und SYS_SCRIPT_FILES.
    // false = nur acmp_cc_release gesperrt.
    static void CheckReleaseTool(JArray checks, SqlConnection wc, out bool ok)
    {
        var miss = new List<string>();
        if (!HasPerm(wc, "SYS_SCRIPTS", "SELECT")) miss.Add("SYS_SCRIPTS SELECT");
        if (!HasPerm(wc, "SYS_SCRIPT_FILES", "SELECT")) miss.Add("SYS_SCRIPT_FILES SELECT");
        foreach (string[] p in ReleaseColumns) if (!HasColumnPerm(wc, p[0], p[1], "UPDATE")) miss.Add(p[0] + "." + p[1] + " UPDATE");
        ok = miss.Count == 0;
        if (ok) Add(checks, "Freigeben", "ok", "UPDATE auf SYS_SCRIPTS.State/IsFileRepoVersionIncreased/FileRepoSize und SELECT auf SYS_SCRIPT_FILES vorhanden (acmp_cc_release, bei Folgeversionen auch alte Version State 7 -> 8)");
        else Add(checks, "Freigeben", "warn", "Rechte fehlen: " + string.Join(", ", miss.ToArray()) + " - acmp_cc_release gesperrt");
    }

    static bool HasColumnPerm(SqlConnection c, string table, string column, string perm)
    {
        var q = c.CreateCommand();
        q.CommandText = "SELECT HAS_PERMS_BY_NAME(@o, 'OBJECT', @p, @c, 'COLUMN')";
        q.Parameters.AddWithValue("@o", "dbo." + table); q.Parameters.AddWithValue("@p", perm); q.Parameters.AddWithValue("@c", column);
        object o = q.ExecuteScalar();
        return o != null && !(o is DBNull) && Convert.ToInt32(o) == 1;
    }

    // Feste Testfaelle des Rundlaufs: Commands, an denen er schon einmal gescheitert ist. Sie werden immer geprueft, auch wenn sie
    // nicht unter den neuesten sind. Aagon-Commands behalten ihre ID beim Import; fehlt einer in dieser Datenbank, wird er uebersprungen.
    public static readonly string[] FixedRoundtripIds = {
        "{1F5A4238-731B-44B6-84F0-3EFB8F2D3222}"   // ACMP Unix Agent verteilen & Inventory (Console-Skript): Kopfblock 1 belegt, 22.10.2026
    };

    class RtStats { public int Commands, Same, Old, Fixed; public List<string> Fails = new List<string>(); public HashSet<string> Seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); }

    // Rundlauf: die neuesten Commands und die festen Testfaelle entpacken, wieder packen und auf Bytegleichheit (entpackter Inhalt) pruefen.
    // Altformat (12 / VirtualTreeView 2) ist nur lesbar und wird nur gezaehlt.
    static void CheckRoundtrip(JArray checks, SqlConnection rc, int sample)
    {
        if (sample <= 0) sample = 50;
        const string latest = "SELECT SCRIPTID, DESCRIPTION, CLIENTSCRIPT, CONSOLESCRIPT, LastModifiedDate, ROW_NUMBER() OVER (PARTITION BY SCRIPTID ORDER BY ScriptVersion DESC) AS rn FROM SYS_SCRIPTS";
        var st = new RtStats();
        var q = rc.CreateCommand();
        q.CommandTimeout = 120;
        q.CommandText = "SELECT TOP (@n) SCRIPTID, DESCRIPTION, CLIENTSCRIPT, CONSOLESCRIPT FROM (" + latest + ") x WHERE rn = 1 AND CLIENTSCRIPT IS NOT NULL ORDER BY LastModifiedDate DESC";
        q.Parameters.AddWithValue("@n", sample);
        using (var r = q.ExecuteReader()) while (r.Read()) RoundtripRow(r, st, false);
        var f = rc.CreateCommand();
        f.CommandTimeout = 120;
        var ps = new List<string>();
        for (int i = 0; i < FixedRoundtripIds.Length; i++) { ps.Add("@f" + i); f.Parameters.AddWithValue("@f" + i, FixedRoundtripIds[i]); }
        f.CommandText = "SELECT SCRIPTID, DESCRIPTION, CLIENTSCRIPT, CONSOLESCRIPT FROM (" + latest + ") x WHERE rn = 1 AND SCRIPTID IN (" + string.Join(", ", ps.ToArray()) + ")";
        int fixedInDb = 0;
        if (ps.Count > 0)
            using (var r = f.ExecuteReader()) while (r.Read()) { fixedInDb++; if (!st.Seen.Contains(r.GetString(0))) RoundtripRow(r, st, true); }
        string d = st.Commands + " Commands: " + st.Same + " Skripte bytegleich, " + st.Old + " im Altformat (nur lesbar); feste Testfaelle " +
            fixedInDb + " von " + FixedRoundtripIds.Length + " vorhanden" + (st.Fixed > 0 ? " (" + st.Fixed + " zusaetzlich zu den neuesten)" : "");
        int same = st.Same; var fails = st.Fails;
        if (fails.Count > 0)
        {
            int show = Math.Min(fails.Count, 10);
            Add(checks, "Format-Rundlauf", "block", d + ", " + fails.Count + " ABWEICHUNG(EN): " + string.Join("; ", fails.GetRange(0, show).ToArray()) + (fails.Count > show ? " ..." : ""));
        }
        else if (same == 0) Add(checks, "Format-Rundlauf", "block", d + ". Kein Command im aktuellen Format zum Pruefen - einen Client Command in der Console anlegen und speichern, dann erneut pruefen (acmp_cc_writer_status refresh).");
        else Add(checks, "Format-Rundlauf", "ok", d);
    }

    static void RoundtripRow(SqlDataReader r, RtStats st, bool isFixed)
    {
        st.Commands++; st.Seen.Add(r.GetString(0));
        if (isFixed) st.Fixed++;
        string nm = r.IsDBNull(1) ? r.GetString(0) : r.GetString(1);
        for (int col = 2; col <= 3; col++)
        {
            if (r.IsDBNull(col)) continue;
            byte[] blob = (byte[])r.GetValue(col);
            string part = col == 2 ? "Client" : "Console";
            try
            {
                CcScript s = CcFormat.Decode(blob);
                if (s.Format != 13 || s.TreeVersion != 3) { st.Old++; continue; }
                byte[] a = CcFormat.Inflate(blob, 0, blob.Length);
                byte[] re = CcFormat.Encode(s);
                byte[] b = CcFormat.Inflate(re, 0, re.Length);
                int diff = FirstDiff(a, b);
                if (diff < 0) st.Same++;
                else st.Fails.Add("'" + nm + "' " + part + " ab Byte " + diff + " " + Region(s, diff) + " (" + a.Length + "/" + b.Length + ")");
            }
            catch (Exception ex) { st.Fails.Add("'" + nm + "' " + part + ": " + ex.Message); }
        }
    }

    // Wo liegt die erste Abweichung? Hilft beim Eingrenzen (Kopf = Kennung, Variablen, Kopfbloecke, Art)
    static string Region(CcScript s, int offset)
    {
        if (offset < s.TreeOffset) return "im Kopf";
        if (offset < s.ChangeLogOffset) return "im Schrittbaum";
        return "im Aenderungsprotokoll";
    }

    static int FirstDiff(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) if (a[i] != b[i]) return i;
        return a.Length == b.Length ? -1 : n;
    }
}
