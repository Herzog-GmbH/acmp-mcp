// AcmpCcVersion: neue Version eines vorhandenen Client Commands als Entwurf anlegen (acmp_cc_new_version) - wie "Speichern"
// eines freigegebenen Commands in der ACMP-Console. Anlass: Fehler in vorhandenen, auch fremden und freigegebenen Commands beheben.
// Beobachtet mit ACMP 6.10.4 an mehreren Commands mit zwei bis neun Versionen (mit und ohne Dateien):
//  - Speichern eines freigegebenen Commands legt eine NEUE Zeile in SYS_SCRIPTS an: ScriptVersion = alte + 1, Predecessor = alte,
//    State 1, Creator/LastModifier = Bearbeiter, CreationDate = LastModifiedDate = jetzt, IsFileRepoVersionIncreased False.
//    Alle uebrigen Spalten wie die alte Version (PASSWORD, FOLDERID, CONDITIONS, Flags, FileRepoVersion, MaxUsedFileRepoVersion,
//    FileRepoSize - "7-Zip" v4 als Entwurf mit FileRepoSize 1941 wie v3). Variablen je Version in SYS_SCRIPTS_VARIABLES.
//    Rechte (SYS_AccessControl_*) haengen an der ScriptId, Dateien (SYS_SCRIPT_FILES) an der FileRepoVersion: beides bleibt.
//  - Die freigegebene Version laeuft weiter, bis die neue freigegeben wird (Console oder acmp_cc_release; dann alte Version -> State 8).
//  - Liegt schon ein Entwurf vor, oeffnet und speichert die Console diesen Entwurf (keine weitere Version).
// Regeln: nicht in Aagon-Standardordnern (StrucLevel 0), optional nur in ccWriter.versionScopeFolderIds; eigene MCP-Entwuerfe weiter mit
// acmp_cc_update_draft; einen fremden Entwurf nur mit replaceDraft=true ueberschreiben (Sicherung, zurueck mit acmp_cc_restore_draft);
// Schritt-IDs bleiben, '***' in Kennwortfeldern uebernimmt das gespeicherte Kennwort desselben Schritts; erst dryRun
// (Vorschau), Plan mit SHA-256 der Basis und des neuen Inhalts. Dateien aendert das Tool nicht.
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using Newtonsoft.Json.Linq;

partial class AcmpCcWriter
{
    const int MaxVersionLines = 400;       // Inhaltszeilen in der Vorschau
    const int MaxDiffLines = 3000;         // groessere Skripte: kein Zeilenvergleich, nur Hinweis

    // Eine Zeile aus SYS_SCRIPTS (eine Version)
    public class CcVersionRow
    {
        public int Version, State, Predecessor, FileRepoVersion;
        public long FileRepoSize;
        public string Name, FolderId, LongDescription, Creator, LastModifier;
        public double? Created, Modified;
        public byte[] Client, Console;
    }

    // ================= Hilfen ohne DB (offline getestet in CcVersionTest.cs) =================

    // Was passiert? "insert" = neue Version anlegen, "replace" = vorhandenen Entwurf ueberschreiben; sonst Exception mit Grund.
    public static string NewVersionMode(int topState, bool registered, bool draftByMcp, bool replaceDraft, string label, string draftInfo)
    {
        if (topState == 7) return "insert";
        if (topState == 1)
        {
            if (registered) throw new Exception(label + " ist ein vom MCP angelegter Entwurf - mit acmp_cc_update_draft aendern.");
            if (draftByMcp || replaceDraft) return "replace";
            throw new Exception(label + ": es gibt schon einen Entwurf " + draftInfo + ". Die Console wuerde diesen Entwurf oeffnen. " +
                "Ueberschreiben nur mit replaceDraft=true (vorher Sicherung, zurueck mit acmp_cc_restore_draft); sonst den Entwurf in der Console freigeben oder verwerfen.");
        }
        throw new Exception(label + ": neueste Version hat State " + topState + " (weder Entwurf noch freigegeben) - nichts geplant.");
    }

    // Ordner pruefen: kein Aagon-Standardordner, innerhalb von versionScopeFolderIds (leer = ueberall sonst)
    public static void CheckVersionFolder(Dictionary<string, CcFolder> all, string folderId, ICollection<string> scope, string label)
    {
        CcFolder f;
        if (folderId == null || !all.TryGetValue(folderId, out f)) throw new Exception(label + " liegt in einem unbekannten Ordner (" + folderId + ") - nichts geplant.");
        if (f.IsAagon && !f.IsRoot) throw new Exception(label + " liegt im Aagon-Standardordner \"" + f.Path + "\" - neue Versionen von Aagon-Commands nur in der ACMP-Console.");
        if (!InScope(all, f, scope)) throw new Exception(label + " liegt in \"" + f.Path + "\", ausserhalb von ccWriter.versionScopeFolderIds - nichts geplant.");
    }

    // Schritt-IDs vorab festlegen (fehlende, ungueltige oder doppelte neu), damit der Inhalt bei der Ausfuehrung genau so entsteht
    // wie in der Anfrage. Gibt eine Kopie zurueck.
    public static JArray NormalizeStepIds(JArray steps, Func<string> newId)
    {
        return NormalizeStepIds(steps, newId, null);
    }

    // keepOnly != null: nur diese IDs behalten (acmp_cc_update_draft: die des Entwurfs), alle anderen neu - keine IDs fremder Commands
    public static JArray NormalizeStepIds(JArray steps, Func<string> newId, ICollection<string> keepOnly)
    {
        if (steps == null || steps.Count == 0) throw new Exception("steps fehlt oder ist leer.");
        var copy = (JArray)steps.DeepClone();
        NormIds(copy, new HashSet<string>(StringComparer.OrdinalIgnoreCase), newId, keepOnly);
        return copy;
    }

    static void NormIds(JArray steps, HashSet<string> used, Func<string> newId, ICollection<string> keepOnly)
    {
        foreach (JToken t in steps)
        {
            JObject st = t as JObject;
            if (st == null) throw new Exception("Schritt ist kein Objekt: " + t.ToString(Newtonsoft.Json.Formatting.None));
            string raw = st["xml"] != null && st["xml"].Type == JTokenType.String ? (string)st["xml"] : null;
            if (!string.IsNullOrEmpty(raw))
            {
                var doc = new XmlDocument(); doc.LoadXml(raw);
                var idEl = doc.DocumentElement["ID"];
                if (idEl != null)
                {
                    string g = GuidBraces(idEl.InnerText);
                    if (g == null || (keepOnly != null && !keepOnly.Contains(g)) || !used.Add(g)) { g = newId(); used.Add(g); idEl.InnerText = g; st["xml"] = doc.DocumentElement.OuterXml; }
                }
            }
            else
            {
                string g = GuidBraces(st["id"] != null && st["id"].Type == JTokenType.String ? (string)st["id"] : null);
                if (g == null || (keepOnly != null && !keepOnly.Contains(g)) || !used.Add(g)) { g = newId(); used.Add(g); }
                st["id"] = g;
            }
            JArray ch = st["children"] as JArray;
            if (ch != null) NormIds(ch, used, newId, keepOnly);
        }
    }

    // Gespeicherte Kennwoerter je Schritt-ID (nur Kennwortfelder mit Wert). Die erste Quelle gewinnt.
    public static Dictionary<string, KeyValuePair<string, Dictionary<string, string>>> CollectSecrets(params CcScript[] scripts)
    {
        var res = new Dictionary<string, KeyValuePair<string, Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
        foreach (CcScript s in scripts) if (s != null) CollectSecretNodes(s.Roots, res);
        return res;
    }

    static void CollectSecretNodes(List<CcNode> nodes, Dictionary<string, KeyValuePair<string, Dictionary<string, string>>> res)
    {
        foreach (CcNode n in nodes)
        {
            var doc = new XmlDocument(); doc.LoadXml(n.Xml);
            var idEl = doc.DocumentElement["ID"];
            string g = idEl != null ? GuidBraces(idEl.InnerText) : null;
            if (g != null && !res.ContainsKey(g))
            {
                var d = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (XmlNode e in doc.DocumentElement.ChildNodes)
                    if (e.NodeType == XmlNodeType.Element && !HasElementChild(e) && e.InnerText.Length > 0 && SecretField.IsMatch(e.Name)) d[e.Name] = e.InnerText;
                if (d.Count > 0) res[g] = new KeyValuePair<string, Dictionary<string, string>>(n.Class, d);
            }
            CollectSecretNodes(n.Children, res);
        }
    }

    // Console-Skript der neuen Version: leer bzw. die Schritte der Basis unveraendert, Variablenliste wie im Client-Skript
    // (die Console fuehrt sie in beiden Skripten). Schritte im Altformat werden nicht umgeschrieben.
    public static CcScript ConsoleFor(byte[] baseConsole, List<CcVar> vars, List<string> notes)
    {
        CcScript con = null;
        if (baseConsole != null && baseConsole.Length > 0)
        {
            try { con = CcFormat.Decode(baseConsole); }
            catch (Exception ex) { throw new Exception("Console-Skript der Basisversion nicht lesbar: " + ex.Message + " - nichts geplant."); }
        }
        if (con == null || con.Roots.Count == 0)
        {
            var e = new CcScript(); e.Kind = 0; e.Vars = vars;
            if (con != null && con.Format == 13) { e.ChangeLog = con.ChangeLog; e.Head1Raw = con.Head1Raw; e.Head2Raw = con.Head2Raw; e.Head6Raw = con.Head6Raw; }
            return e;
        }
        if (con.Format != 13 || con.TreeVersion != 3)
            throw new Exception("Console-Skript der Basisversion hat Schritte im Altformat " + con.Format + " - neue Version in der ACMP-Console anlegen.");
        con.Vars = vars;   // Art (Kind) wie gelesen: bei Console-Commands ist das Console-Skript das aktive (1)
        notes.Add("Console-Skript (" + CountSteps(con.Roots) + " Schritt(e)) unveraendert uebernommen, nur die Variablenliste angeglichen.");
        return con;
    }

    // Zeilen fuer den Vergleich: wie die Vorschau, aber ohne Schrittnummern (sonst verschiebt ein neuer Schritt alle folgenden)
    public List<string> DiffRender(CcScript s)
    {
        var lines = new List<string>();
        foreach (JToken t in PreviewScript(new List<string>(), s))
            lines.Add(Regex.Replace((string)t, @"^(\s*)\d+(\.\d+)*\. ", "$1* "));
        return lines;
    }

    // Zeilenvergleich (LCS): "- alt", "+ neu", "  gleich" (context Zeilen um jede Aenderung), "  ..." zwischen Bloecken.
    // Leer = gleich. null = zu gross fuer den Vergleich.
    public static List<string> DiffLines(List<string> a, List<string> b, int context)
    {
        int n = a.Count, m = b.Count;
        if (n > MaxDiffLines || m > MaxDiffLines) return null;
        var L = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                L[i, j] = a[i] == b[j] ? L[i + 1, j + 1] + 1 : Math.Max(L[i + 1, j], L[i, j + 1]);
        var ops = new List<KeyValuePair<char, string>>();
        int x = 0, y = 0;
        while (x < n || y < m)
        {
            if (x < n && y < m && a[x] == b[y]) { ops.Add(new KeyValuePair<char, string>(' ', a[x])); x++; y++; }
            else if (y < m && (x >= n || L[x, y + 1] >= L[x + 1, y])) { ops.Add(new KeyValuePair<char, string>('+', b[y])); y++; }
            else { ops.Add(new KeyValuePair<char, string>('-', a[x])); x++; }
        }
        var show = new bool[ops.Count];
        bool any = false;
        for (int i = 0; i < ops.Count; i++)
            if (ops[i].Key != ' ')
            {
                any = true;
                for (int k = Math.Max(0, i - context); k <= Math.Min(ops.Count - 1, i + context); k++) show[k] = true;
            }
        var res = new List<string>();
        if (!any) return res;
        bool gap = false;
        for (int i = 0; i < ops.Count; i++)
        {
            if (!show[i]) { gap = true; continue; }
            if (gap && res.Count > 0) res.Add("  ...");
            gap = false;
            res.Add(ops[i].Key + " " + ops[i].Value);
        }
        return res;
    }

    // Unterschiede zwischen eingefrorenem Plan und jetzigem Stand (leer = unveraendert)
    public static List<string> VersionDiff(JObject p, List<CcVersionRow> rows)
    {
        var d = new List<string>();
        if (rows.Count == 0) { d.Add("Command nicht mehr vorhanden"); return d; }
        CcVersionRow top = rows[0];
        if (rows.Count != (int)p["versions"]) d.Add("jetzt " + rows.Count + " Versionen statt " + p["versions"]);
        if (top.Version != (int)p["baseVersion"]) d.Add("neueste Version " + p["baseVersion"] + " -> " + top.Version);
        if (top.State != (int)p["baseState"]) d.Add("State der neuesten Version jetzt " + top.State);
        if (!SameStr(Sha256Hex(top.Client), p["baseClientSha256"], true)) d.Add("Client-Skript der Basis geaendert");
        if (!SameStr(Sha256Hex(top.Console), p["baseConsoleSha256"], true)) d.Add("Console-Skript der Basis geaendert");
        if (!SameStr(top.FolderId, p["folderId"], true)) d.Add("Ordner geaendert");
        if (!SameStr(top.Name, p["name"], false)) d.Add("Name jetzt \"" + top.Name + "\"");
        if (!SameStr(top.LastModifier, p["baseLastModifier"], false)) d.Add("zuletzt geaendert von " + top.LastModifier);
        CcVersionRow rel = rows.Find(x => x.State == 7);
        if ((rel != null ? rel.Version : 0) != (int)p["releasedVersion"]) d.Add("freigegebene Version jetzt " + (rel != null ? "v" + rel.Version : "keine"));
        if (p["releasedCount"] != null)
        {
            int nrc = rows.FindAll(x => x.State == 7).Count;
            if (nrc != (int)p["releasedCount"]) d.Add("jetzt " + nrc + " freigegebene Versionen statt " + p["releasedCount"]);
        }
        return d;
    }

    // null und "" gelten als gleich (JSON-Rundlauf des Plans)
    static bool SameStr(string now, JToken planned, bool ignoreCase)
    {
        string b = planned != null && planned.Type != JTokenType.Null ? (string)planned : null;
        return string.Equals(now ?? "", b ?? "", ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    internal static JArray VarsToJson(List<CcVar> vars)
    {
        var a = new JArray();
        foreach (CcVar v in vars)
        {
            var o = new JObject(); o["name"] = v.Name; o["value"] = v.Value ?? "";
            if (v.ReadOnly) o["readOnly"] = true;
            if (v.Origin != 0) o["origin"] = (int)v.Origin;
            a.Add(o);
        }
        return a;
    }

    static string OaText(double? d) { return d.HasValue ? DateTime.FromOADate(d.Value).ToString("dd.MM.yyyy HH:mm") : "?"; }

    // ================= Individuelle Felder in Schritten (seit 22.10.2026) =================

    // Kurzform {"class":"TStoreValueOnACMPServer","fields":{"CustomField":"<Name oder GUID>","Table":"<optional>","Value":"%VAR%"}} vorhanden?
    static bool HasCfShorthand(JArray steps)
    {
        if (steps == null) return false;
        foreach (JToken t in steps)
        {
            JObject st = t as JObject; if (st == null) continue;
            JObject f = st["fields"] as JObject;
            if ((string)st["class"] == "TStoreValueOnACMPServer" && f != null && f["CustomField"] != null) return true;
            if (HasCfShorthand(st["children"] as JArray)) return true;
        }
        return false;
    }

    // Kurzform mit den Metadaten aus der DB fuellen (Tabellen-/Spaltenname, FieldType, Kopf-JSON wie die Console); sonst unveraendert
    internal static JArray ExpandCustomFieldSteps(SqlConnection c, JArray steps, List<string> notes)
    {
        if (!HasCfShorthand(steps)) return steps;
        return AcmpCustomFields.ExpandStoreSteps(steps, (f, t) => AcmpCustomFields.ResolveWith(c, f, t), notes);
    }

    public JArray ExpandCustomFieldSteps(JArray steps, List<string> notes)
    {
        if (!HasCfShorthand(steps)) return steps;
        if (string.IsNullOrEmpty(_readCs)) throw new Exception("DB-Lesezugang (db.connectionString) fehlt - CustomField-Kurzform nicht aufloesbar.");
        using (var c = new SqlConnection(_readCs)) { c.Open(); return ExpandCustomFieldSteps(c, steps, notes); }
    }

    // Kopf-JSON (StoredCustomFields/-Tables/-Folders) wie die Console aus den Schritten "Wert auf Server speichern". Liefern die Schritte nichts,
    // bleibt der Kopf (Basis bzw. leer); bei gleichem Inhalt bleibt der Text der Basis, damit der Rundlauf bytegleich bleibt.
    internal static void ApplyStoredHeader(CcScript s, JArray steps)
    {
        if (steps == null) return;
        s.StoredCustomFields = MergeHeader(s.StoredCustomFields, AcmpCtl.CollectStored(steps, "StoredCustomFields"));
        s.StoredTables = MergeHeader(s.StoredTables, AcmpCtl.CollectStored(steps, "StoredTables"));
        s.StoredFolders = MergeHeader(s.StoredFolders, AcmpCtl.CollectStored(steps, "StoredFolders"));
    }

    static string MergeHeader(string current, string fromSteps)
    {
        if (fromSteps == null) return current;
        if (current != null) { try { if (JToken.DeepEquals(JToken.Parse(current), JToken.Parse(fromSteps))) return current; } catch { } }
        return fromSteps;
    }

    // ================= DB =================

    // Alle Versionen, neueste zuerst. tx != null: in der Schreib-Transaktion mit Sperre lesen.
    static List<CcVersionRow> LoadVersions(SqlConnection c, SqlTransaction tx, string id)
    {
        var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = "SELECT ScriptVersion, CAST(State AS int), Predecessor, DESCRIPTION, FOLDERID, LONGDESCRIPTION, Creator, LastModifier, CreationDate, LastModifiedDate, " +
            "CLIENTSCRIPT, CONSOLESCRIPT, FileRepoVersion, FileRepoSize FROM SYS_SCRIPTS" + (tx != null ? " WITH (UPDLOCK, HOLDLOCK)" : "") +
            " WHERE SCRIPTID=@id ORDER BY ScriptVersion DESC";
        q.Parameters.AddWithValue("@id", id);
        var rows = new List<CcVersionRow>();
        using (var r = q.ExecuteReader())
            while (r.Read())
            {
                var v = new CcVersionRow();
                v.Version = r.GetInt32(0); v.State = r.GetInt32(1);
                v.Predecessor = r.IsDBNull(2) ? 0 : Convert.ToInt32(r.GetValue(2));
                v.Name = r.IsDBNull(3) ? "" : r.GetString(3);
                v.FolderId = r.IsDBNull(4) ? null : r.GetString(4);
                v.LongDescription = r.IsDBNull(5) ? "" : r.GetString(5);
                v.Creator = r.IsDBNull(6) ? null : r.GetString(6);
                v.LastModifier = r.IsDBNull(7) ? null : r.GetString(7);
                v.Created = r.IsDBNull(8) ? (double?)null : Convert.ToDouble(r.GetValue(8));
                v.Modified = r.IsDBNull(9) ? (double?)null : Convert.ToDouble(r.GetValue(9));
                v.Client = r.IsDBNull(10) ? null : (byte[])r.GetValue(10);
                v.Console = r.IsDBNull(11) ? null : (byte[])r.GetValue(11);
                v.FileRepoVersion = r.IsDBNull(12) ? 0 : Convert.ToInt32(r.GetValue(12));
                v.FileRepoSize = r.IsDBNull(13) ? 0 : Convert.ToInt64(r.GetValue(13));
                rows.Add(v);
            }
        return rows;
    }

    static List<CcVar> LoadVarRows(SqlConnection c, SqlTransaction tx, string id, int ver)
    {
        var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = "SELECT Name, CAST(Origin AS int), CAST(ReadOnly AS bit) FROM SYS_SCRIPTS_VARIABLES WHERE ScriptId=@id AND ScriptVersion=@v ORDER BY Name";
        q.Parameters.AddWithValue("@id", id); q.Parameters.AddWithValue("@v", ver);
        var l = new List<CcVar>();
        using (var r = q.ExecuteReader()) while (r.Read()) { var v = new CcVar(); v.Name = r.GetString(0); v.Origin = (byte)r.GetInt32(1); v.ReadOnly = r.GetBoolean(2); l.Add(v); }
        return l;
    }

    static int FileCount(SqlConnection c, string id, int repoVer)
    {
        var q = c.CreateCommand();
        q.CommandText = "SELECT COUNT(*) FROM SYS_SCRIPT_FILES WHERE ScriptID=@id AND FileRepoVersion=@rv AND IsFolder=0";
        q.Parameters.AddWithValue("@id", id); q.Parameters.AddWithValue("@rv", repoVer);
        return Convert.ToInt32(q.ExecuteScalar());
    }

    // Neuen Inhalt bauen (beim Planen und bei der Ausfuehrung gleich): Schritte mit festen IDs, Kennwoerter aus der Basis,
    // Kopf (Aenderungsprotokoll, Kopfbloecke) aus dem Client-Skript der Basis, Console-Skript aus der Basis.
    internal CcScript BuildVersion(JArray normSteps, JArray vars, CcVersionRow top, CcVersionRow released, out byte[] cb, out byte[] ob, out List<string> notes, out int secretsTaken)
    {
        CcScript baseClient = null, relClient = null;
        try { if (top.Client != null) baseClient = CcFormat.Decode(top.Client); }
        catch (Exception ex) { throw new Exception("Client-Skript von v" + top.Version + " nicht lesbar: " + ex.Message + " - nichts geplant."); }
        if (released != null && released != top && released.Client != null) { try { relClient = CcFormat.Decode(released.Client); } catch { } }
        var ctx = new CcBuildCtx();
        ctx.KeepIds = true;
        ctx.Secrets = CollectSecrets(baseClient, relClient);
        ctx.SecretSource = "v" + top.Version + (relClient != null ? " bzw. v" + released.Version : "");
        CcScript client = BuildScript(normSteps, vars, out notes, ctx);
        client.Kind = 1;
        if (baseClient != null && baseClient.Format == 13 && baseClient.TreeVersion == 3)
        {
            client.ChangeLog = baseClient.ChangeLog;
            client.StoredCustomFields = baseClient.StoredCustomFields; client.StoredCustomFieldsRaw = baseClient.StoredCustomFieldsRaw;
            client.StoredTables = baseClient.StoredTables; client.StoredTablesRaw = baseClient.StoredTablesRaw;
            client.StoredFolders = baseClient.StoredFolders; client.StoredFoldersRaw = baseClient.StoredFoldersRaw;
            client.Head1Raw = baseClient.Head1Raw; client.Head2Raw = baseClient.Head2Raw; client.Head6Raw = baseClient.Head6Raw;
        }
        else if (baseClient != null)
            notes.Add("Client-Skript der Basis im Altformat " + baseClient.Format + " - neue Version im aktuellen Format 13, Aenderungsprotokoll beginnt neu.");
        ApplyStoredHeader(client, normSteps);
        cb = CcFormat.Encode(client);
        CcScript console = ConsoleFor(top.Console, client.Vars, notes);
        ob = CcFormat.Encode(console);
        CcFormat.Decode(cb); CcFormat.Decode(ob);   // Selbsttest
        secretsTaken = ctx.SecretsTaken;
        return client;
    }

    // ================= Planen (dryRun und Ausfuehrung) =================

    public JObject PlanNewVersion(JObject a, bool withContent)
    {
        if (string.IsNullOrEmpty(_readCs)) throw new Exception("DB-Lesezugang (db.connectionString) fehlt.");
        string arg = Str(a["scriptId"]);
        if (arg.Length == 0) throw new Exception("scriptId fehlt (GUID oder eindeutiger Name des Client Commands).");
        JArray steps = a["steps"] as JArray;
        if (steps == null && a["steps"] != null && a["steps"].Type == JTokenType.String && ((string)a["steps"]).Trim().StartsWith("["))
            steps = JArray.Parse((string)a["steps"]);
        bool replaceDraft = a["replaceDraft"] != null && a["replaceDraft"].Type == JTokenType.Boolean && (bool)a["replaceDraft"];
        string longDesc = a["longDescription"] != null && a["longDescription"].Type == JTokenType.String ? (string)a["longDescription"] : null;
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            // Kurzform "Wert auf Server speichern" mit CustomField aufloesen (eingefroren im Plan), dann Schritt-IDs festlegen
            var cfNotes = new List<string>();
            JArray normSteps = NormalizeStepIds(ExpandCustomFieldSteps(c, steps, cfNotes), NewGuid);
            JObject cmd = ResolveCommand(c, arg);
            string id = (string)cmd["scriptId"];
            List<CcVersionRow> rows = LoadVersions(c, null, id);
            if (rows.Count == 0) throw new Exception("Client Command " + id + " nicht gefunden.");
            CcVersionRow top = rows[0], released = rows.Find(x => x.State == 7);
            string label = "\"" + top.Name + "\" " + id;
            Dictionary<string, CcFolder> folders = LoadFolders(c);
            CheckVersionFolder(folders, top.FolderId, _versionScope, label);
            bool draftByMcp = top.State == 1 && top.LastModifier != null && top.LastModifier.StartsWith("MCP ", StringComparison.Ordinal);

            // Inhalt eines vorhandenen Entwurfs mit der freigegebenen Version vergleichen (fuer Meldung und Vorschau)
            string draftInfo = "", draftSame = null;
            if (top.State == 1)
            {
                draftInfo = "v" + top.Version + " (angelegt " + OaText(top.Created) + " von " + (top.Creator ?? "?") + ", zuletzt " + OaText(top.Modified) + " von " + (top.LastModifier ?? "?") + ")";
                if (released != null)
                    try
                    {
                        List<string> dl = DiffLines(DiffRender(CcFormat.Decode(released.Client)), DiffRender(CcFormat.Decode(top.Client)), 0);
                        draftSame = dl != null && dl.Count == 0 ? "inhaltsgleich mit der freigegebenen v" + released.Version : "weicht von der freigegebenen v" + released.Version + " ab";
                        draftInfo += ", " + draftSame;
                    }
                    catch { }
            }
            string mode = NewVersionMode(top.State, IsRegistered(id), draftByMcp, replaceDraft, label, draftInfo);
            JArray vars = a["variables"] as JArray;
            bool varsFromBase = vars == null;
            if (varsFromBase)
            {
                try { vars = top.Client != null ? VarsToJson(CcFormat.Decode(top.Client).Vars) : new JArray(); }
                catch (Exception ex) { throw new Exception("Client-Skript von v" + top.Version + " nicht lesbar: " + ex.Message); }
            }

            byte[] cb, ob; List<string> notes; int secrets;
            CcScript client = BuildVersion(normSteps, vars, top, released, out cb, out ob, out notes, out secrets);
            notes.InsertRange(0, cfNotes);
            int newVer = mode == "insert" ? top.Version + 1 : top.Version;
            CcFolder f; folders.TryGetValue(top.FolderId, out f);
            int files = FileCount(c, id, top.FileRepoVersion);

            var p = new JObject();
            p["action"] = "newversion"; p["batchId"] = NewGuid(); p["mode"] = mode;
            p["scriptId"] = id; p["name"] = top.Name; p["folderId"] = top.FolderId; p["folderPath"] = f != null ? f.Path : top.FolderId;
            p["versions"] = rows.Count; p["baseVersion"] = top.Version; p["baseState"] = top.State;
            p["baseClientSha256"] = Sha256Hex(top.Client); p["baseConsoleSha256"] = Sha256Hex(top.Console);
            p["baseLastModifier"] = top.LastModifier;
            p["releasedVersion"] = released != null ? released.Version : 0;
            // Mehrere Versionen mit State 7 (Altlast aus der Console, z.B. v1 und v2): als freigegeben gilt
            // die neueste davon; ihre Zahl bleibt beim Anlegen gleich und wird nach dem Schreiben so geprueft
            List<CcVersionRow> relAll = rows.FindAll(x => x.State == 7);
            p["releasedCount"] = relAll.Count;
            p["newVersion"] = newVer;
            p["newClientSha256"] = Sha256Hex(cb); p["newConsoleSha256"] = Sha256Hex(ob);
            p["totalSteps"] = CountSteps(client.Roots); p["secretsTaken"] = secrets;
            p["steps"] = normSteps; p["variables"] = vars;
            if (longDesc != null) p["longDescription"] = longDesc;
            if (notes.Count > 0) p["hinweise"] = new JArray(notes.ToArray());

            // Vorschau
            var head = new List<string>();
            string relText = released != null ? "die freigegebene v" + released.Version : "keine freigegebene Version";
            if (mode == "insert")
                head.Add("NEUE VERSION v" + newVer + " von " + label + " als ENTWURF anlegen (State 1, Predecessor v" + top.Version + ") - wie Speichern in der ACMP-Console.");
            else
                head.Add("Vorhandenen ENTWURF " + draftInfo + " von " + label + " UEBERSCHREIBEN - vorher Sicherung, zurueck mit acmp_cc_restore_draft.");
            head.Add("Ordner \"" + p["folderPath"] + "\". Auf Clients laeuft weiter " + relText + ", bis v" + newVer + " freigegeben wird (acmp_cc_release oder ACMP-Console).");
            if (relAll.Count > 1)
                head.Add("Hinweis: " + relAll.Count + " Versionen stehen auf freigegeben (State 7: " + string.Join(", ", relAll.ConvertAll(x => "v" + x.Version).ToArray()) +
                         ", Altlast aus der Console). Als freigegeben gilt hier v" + released.Version + ". Beim Anlegen bleiben alle so; acmp_cc_release loest beim Freigeben von v" + newVer +
                         " alle aelteren freigegebenen Versionen ab (State 8).");
            head.Add("Uebernommen aus v" + top.Version + ": Name, Ordner, Rechte, Bedingungen, Kennwort-Hash des Commands, angehaengte Dateien (FileRepoVersion " + top.FileRepoVersion + ", " + files + " Datei(en)), Aenderungsprotokoll" +
                     (varsFromBase ? ", Variablen" : "") + (longDesc == null ? ", Beschreibung" : "") + ".");
            if (longDesc != null) head.Add("Neue Beschreibung: " + OneLine(longDesc, 300));
            if (secrets > 0) head.Add(secrets + " gespeicherte(s) Kennwort(e) aus der Basis uebernommen (gleiche Schritt-ID; Werte nicht angezeigt).");
            foreach (string n in notes) if (!n.StartsWith("Schritt \"")) head.Add("Hinweis: " + n);
            CcVersionRow cmp = released ?? top;
            List<string> diff = null;
            try { diff = DiffLines(DiffRender(CcFormat.Decode(cmp.Client)), DiffRender(client), 2); } catch { }
            if (diff == null) head.Add("Aenderungen: Skript zu gross fuer den Zeilenvergleich - Inhalt unten pruefen.");
            else if (diff.Count == 0) head.Add("Aenderungen gegenueber v" + cmp.Version + ": KEINE (Schritte und Variablen gleich).");
            else
            {
                head.Add("Aenderungen gegenueber v" + cmp.Version + " (- alt, + neu, Kennwoerter maskiert):");
                int shown = 0;
                foreach (string l in diff)
                {
                    if (shown++ >= MaxVersionLines) { head.Add("   ... (" + (diff.Count - MaxVersionLines) + " weitere Zeilen)"); break; }
                    head.Add("   " + l);
                }
            }
            JArray content = PreviewScript(new List<string>(), client);
            if (withContent)
            {
                head.Add("Inhalt v" + newVer + " (Client-Skript, SHA-256 " + ((string)p["newClientSha256"]).Substring(0, 16) + "...):");
                int shown = 0;
                foreach (JToken t in content)
                {
                    if (shown++ >= MaxVersionLines) { head.Add("   ... (" + (content.Count - MaxVersionLines) + " weitere Zeilen)"); break; }
                    head.Add("   " + (string)t);
                }
            }
            else head.Add("Inhalt: " + content.Count + " Zeilen - mit details=true.");
            head.Add("Bei der Ausfuehrung wird verglichen: Versionen, State, SHA-256 von Client- und Console-Skript der Basis, Name, Ordner, letzter Bearbeiter und der neu gebaute Inhalt. Weicht etwas ab, wird NICHTS geschrieben.");
            p["vorschau"] = new JArray(head.ToArray());
            return p;
        }
    }

    // ================= Ausfuehren (nur mit dem Plan, den der Server direkt vorher erstellt hat) =================

    public JObject RunNewVersion(JObject a, string requester)
    {
        CheckWrite(requester, false);
        JObject p = a["_plan"] as JObject;
        if (p == null || (string)p["action"] != "newversion")
            throw new Exception("acmp_cc_new_version laeuft nur mit einem vom Server erstellten Plan - nichts geschrieben.");
        string id = (string)p["scriptId"], mode = (string)p["mode"], label = "\"" + p["name"] + "\" " + id;
        int baseVer = (int)p["baseVersion"], newVer = (int)p["newVersion"];
        if (mode != "insert" && mode != "replace") throw new Exception("Plan ohne gueltigen Modus - nichts geschrieben.");
        string backup; int varCount;
        lock (_lock)
        using (var c = new SqlConnection(_writeCs))
        {
            c.Open();
            // 1) Jetzigen Stand gegen den Plan pruefen, Inhalt neu bauen und mit der Anfrage vergleichen
            List<CcVersionRow> rows = LoadVersions(c, null, id);
            List<string> diff = VersionDiff(p, rows);
            if (diff.Count > 0) throw new Exception("Nichts geschrieben - " + label + " wurde seit der Planung geaendert (" + string.Join(", ", diff.ToArray()) + "). Bitte erneut aufrufen.");
            CheckVersionFolder(LoadFolders(c), rows[0].FolderId, _versionScope, label);
            CcVersionRow top = rows[0], released = rows.Find(x => x.State == 7);
            byte[] cb, ob; List<string> notes; int secrets;
            CcScript client = BuildVersion((JArray)p["steps"], (JArray)p["variables"], top, released, out cb, out ob, out notes, out secrets);
            if (Sha256Hex(cb) != (string)p["newClientSha256"] || Sha256Hex(ob) != (string)p["newConsoleSha256"])
                throw new Exception("Nichts geschrieben - der neu gebaute Inhalt weicht vom Plan ab (SHA-256). Bitte erneut aufrufen.");
            varCount = client.Vars.Count;

            // 2) Sicherung: beim Ueberschreiben der ganze Entwurf (zurueck mit acmp_cc_restore_draft), sonst nur der Ausgangsstand
            var b = new JObject();
            b["kind"] = mode == "replace" ? "newversion-draft" : "newversion-insert";
            b["batchId"] = p["batchId"]; b["zeit"] = DateTime.Now.ToString("s"); b["von"] = requester;
            b["scriptId"] = id; b["version"] = top.Version; b["neueVersion"] = newVer;
            b["grund"] = mode == "replace" ? "vor acmp_cc_new_version (Entwurf v" + top.Version + " ueberschrieben)" : "vor acmp_cc_new_version (v" + newVer + " neu angelegt, Basis v" + top.Version + ")";
            b["State"] = top.State; b["Creator"] = top.Creator;
            b["DESCRIPTION"] = top.Name; b["LONGDESCRIPTION"] = top.LongDescription; b["FOLDERID"] = top.FolderId;
            b["LastModifier"] = top.LastModifier;
            b["LastModifiedDate"] = top.Modified.HasValue ? (JToken)top.Modified.Value : null;
            b["baseClientSha256"] = p["baseClientSha256"]; b["newClientSha256"] = p["newClientSha256"];
            if (mode == "replace")
            {
                b["CLIENTSCRIPT"] = top.Client != null ? Convert.ToBase64String(top.Client) : null;
                b["CONSOLESCRIPT"] = top.Console != null ? Convert.ToBase64String(top.Console) : null;
                var va = new JArray();
                foreach (CcVar v in LoadVarRows(c, null, id, top.Version)) { var vj = new JObject(); vj["Name"] = v.Name; vj["Origin"] = (int)v.Origin; vj["ReadOnly"] = v.ReadOnly; va.Add(vj); }
                b["variables"] = va;
            }
            backup = WriteBackup(b, "newversion", (string)p["batchId"]);

            // 3) Eine Transaktion: mit Sperre erneut lesen und vergleichen, schreiben, kontrollieren
            string who = "MCP " + requester; double now = DateTime.Now.ToOADate();
            string longDesc = p["longDescription"] != null && p["longDescription"].Type == JTokenType.String ? (string)p["longDescription"] : null;
            using (var tx = c.BeginTransaction())
            {
                List<CcVersionRow> locked = LoadVersions(c, tx, id);
                List<string> d2 = VersionDiff(p, locked);
                if (d2.Count > 0) throw new Exception(label + " wurde gerade geaendert (" + string.Join(", ", d2.ToArray()) + ") - zurueckgerollt, nichts geschrieben.");
                var w = c.CreateCommand(); w.Transaction = tx;
                if (mode == "insert")
                {
                    // Neue Zeile wie die Console: alle Spalten aus der Basis, neu nur Version, Vorgaenger, Inhalt, State, Bearbeiter, Zeiten
                    w.CommandText =
                        "INSERT INTO SYS_SCRIPTS (SCRIPTID, ScriptVersion, Predecessor, FileRepoVersion, MaxUsedFileRepoVersion, DESCRIPTION, LONGDESCRIPTION, PASSWORD, FOLDERID, " +
                        "CLIENTSCRIPT, CONSOLESCRIPT, CONDITIONS, ISQUERYSHORTCUT, ISACTBARSHORTCUT, DOSTORECLIENTLOG, DOSHOWCONSOLELOG, HASCONSOLESCRIPT, ABOUTINFOS, HASCLIENTSCRIPT, " +
                        "PRIORITY, VERSION, ICONID, ALLOWINTERACTIVEUSE, COMMANDENABLED, HasConditions, State, Creator, CreationDate, LastModifier, LastModifiedDate, FileRepoSize, " +
                        "IsFileRepoVersionIncreased, IsParallelCC, ConsoleRelations, ClientRelations, LicensedCommands) " +
                        "SELECT b.SCRIPTID, @nv, b.ScriptVersion, b.FileRepoVersion, b.MaxUsedFileRepoVersion, b.DESCRIPTION, " + (longDesc != null ? "@ld" : "b.LONGDESCRIPTION") + ", b.PASSWORD, b.FOLDERID, " +
                        "@cs, @os, b.CONDITIONS, b.ISQUERYSHORTCUT, b.ISACTBARSHORTCUT, b.DOSTORECLIENTLOG, b.DOSHOWCONSOLELOG, b.HASCONSOLESCRIPT, b.ABOUTINFOS, b.HASCLIENTSCRIPT, " +
                        "b.PRIORITY, b.VERSION, b.ICONID, b.ALLOWINTERACTIVEUSE, b.COMMANDENABLED, b.HasConditions, 1, @who, @now, @who, @now, b.FileRepoSize, " +
                        "0, b.IsParallelCC, b.ConsoleRelations, b.ClientRelations, b.LicensedCommands " +
                        "FROM SYS_SCRIPTS b WHERE b.SCRIPTID=@id AND b.ScriptVersion=@bv AND b.State=7";
                    w.Parameters.AddWithValue("@nv", newVer);
                    w.Parameters.AddWithValue("@bv", baseVer);
                }
                else
                {
                    w.CommandText = "UPDATE SYS_SCRIPTS SET CLIENTSCRIPT=@cs, CONSOLESCRIPT=@os, LastModifier=@who, LastModifiedDate=@now" + (longDesc != null ? ", LONGDESCRIPTION=@ld" : "") +
                        " WHERE SCRIPTID=@id AND ScriptVersion=@bv AND State=1";
                    w.Parameters.AddWithValue("@bv", baseVer);
                }
                w.Parameters.AddWithValue("@id", id);
                w.Parameters.Add("@cs", SqlDbType.VarBinary, -1).Value = cb;
                w.Parameters.Add("@os", SqlDbType.VarBinary, -1).Value = ob;
                w.Parameters.AddWithValue("@who", who);
                w.Parameters.AddWithValue("@now", now);
                if (longDesc != null) w.Parameters.AddWithValue("@ld", longDesc);
                if (w.ExecuteNonQuery() != 1) throw new Exception(label + ": " + (mode == "insert" ? "neue Version nicht angelegt" : "Entwurf nicht eindeutig getroffen") + " - zurueckgerollt, nichts geschrieben.");
                var del = c.CreateCommand(); del.Transaction = tx;
                del.CommandText = "DELETE FROM SYS_SCRIPTS_VARIABLES WHERE ScriptId=@id AND ScriptVersion=@v";
                del.Parameters.AddWithValue("@id", id); del.Parameters.AddWithValue("@v", newVer);
                del.ExecuteNonQuery();
                InsertVars(c, tx, id, newVer, client.Vars);

                // Kontrolle in derselben Transaktion: Anzahl Versionen, genau ein Entwurf = die geschriebene Version, freigegebene Versionen unberuehrt
                // (so viele mit State 7 wie bei der Planung; Plaene ohne releasedCount: hoechstens eine)
                var chk = c.CreateCommand(); chk.Transaction = tx;
                chk.CommandText = "SELECT COUNT(*), MAX(ScriptVersion), SUM(CASE WHEN CAST(State AS int)=1 THEN 1 ELSE 0 END), " +
                    "SUM(CASE WHEN CAST(State AS int)=1 AND ScriptVersion=@nv THEN 1 ELSE 0 END), SUM(CASE WHEN CAST(State AS int)=7 THEN 1 ELSE 0 END), " +
                    "(SELECT COUNT(*) FROM SYS_SCRIPTS_VARIABLES WHERE ScriptId=@id AND ScriptVersion=@nv) FROM SYS_SCRIPTS WHERE SCRIPTID=@id";
                chk.Parameters.AddWithValue("@id", id); chk.Parameters.AddWithValue("@nv", newVer);
                int expectRows = (int)p["versions"] + (mode == "insert" ? 1 : 0),
                    expectReleased = p["releasedCount"] != null ? (int)p["releasedCount"] : ((int)p["releasedVersion"] > 0 ? 1 : 0);
                using (var r = chk.ExecuteReader())
                {
                    r.Read();
                    if (r.GetInt32(0) != expectRows || r.GetInt32(1) != newVer || r.GetInt32(2) != 1 || r.GetInt32(3) != 1 || r.GetInt32(4) != expectReleased || r.GetInt32(5) != varCount)
                        throw new Exception(label + ": nach dem Schreiben nicht wie geplant (Versionen/State/Variablen) - zurueckgerollt, nichts geschrieben.");
                }
                tx.Commit();
            }
        }
        string bn = Path.GetFileName(backup);
        _log("acmp_cc_new_version von " + requester + ": " + label + " " + (mode == "insert" ? "v" + newVer + " angelegt" : "Entwurf v" + newVer + " ueberschrieben") + ", Sicherung " + bn);
        var o = new JObject();
        o["sicherung"] = bn;
        o["ok"] = true; o["scriptId"] = id; o["name"] = p["name"]; o["version"] = newVer; o["modus"] = mode == "insert" ? "neue Version angelegt" : "Entwurf ueberschrieben";
        o["hinweis"] = "v" + newVer + " ist ein ENTWURF (State 1). Auf Clients laeuft weiter die freigegebene Version. Freigeben mit acmp_cc_release (erst dryRun=true) oder in der ACMP-Console; " +
                       "dabei wird die alte Version abgeloest (State 8)." +
                       (mode == "replace" ? " Zurueck zum vorherigen Entwurf: acmp_cc_restore_draft backup=\"" + bn + "\"." : "");
        return o;
    }
}
