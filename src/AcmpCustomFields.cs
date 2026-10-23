// AcmpCustomFields: individuelle Felder (Custom Fields) lesen und anlegen - acmp_cf_list (lesen, auch Werte je Client), acmp_cf_create.
// Grundlage: SQL-Mitschnitt vom 22.10.2026 (ACMP 6.10). Die Console schreibt nicht selbst, der ACMP-Server legt an:
//  - neue Tabelle: CREATE TABLE CF_VAL_<24 Hex der TableID> ([CLIENTID] uniqueidentifier, PK) + GRANT an RLS_ACMPUser, AcmpUser_ReadOnly,
//    AcmpUser_LowPrivilege + FK auf CLT_CLIENTS_TABLE (ON DELETE CASCADE); Zeile in SYS_TableRelations (IntSupportedQueryBases 129, IsCustomTable 1)
//  - je Feld: ALTER TABLE ... ADD CF_<28 Hex der FieldID> BIGINT bzw. NVARCHAR(n); INSERT in die View VW_SYS_DISPLAYFIELDS (INSTEAD-OF-Trigger
//    verteilen auf SYS_DISPLAYFIELDS, _Localization, _AvailableValues); Zeile in SYS_CustomFields
// Die DDL fuehrt der Schreib-Login nicht selbst aus: zwei Prozeduren im Besitz von dbo mit EXECUTE AS OWNER (grant-cf-params.sql) bauen genau
// dieses Muster aus den GUIDs. Der Schreib-Login hat nur EXECUTE darauf und SELECT/INSERT auf die drei Metadaten-Objekte.
// Regeln: nur Client-Tabellen (Abfragebasis Clients, Ordner unter Client\Custom Fields), keine Mehrfachtabellen, nur Ganzzahl und Text (die
// Metadaten anderer Eingabemethoden sind nicht beobachtet), hoechstens MaxFields Felder je Auftrag, Namen in der Tabelle eindeutig, erst
// dryRun (Vorschau), Plan mit Ids, eine Transaktion, Sicherung des Plans, Kontrolle danach mit dem Lese-Login.
// Loeschen kann das Tool nicht (in der Console). Ob der ACMP-Server ein so angelegtes Feld ohne Neustart kennt (er haelt die Felder im
// Speicher und liest sie nach Aktionen der Console neu), zeigt der erste Lauf eines Commands mit "Wert auf Server speichern".
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Ein individuelles Feld mit Tabelle und Ordner (aus VW_SYS_CustomFields, VW_SYS_DISPLAYFIELDS, SYS_TableRelations, SYS_Folders)
class CfMeta
{
    public string FieldId, TableId, FolderId, FolderParentId;      // "{GUID}" in Grossbuchstaben
    public string CaptionDe, CaptionEn, TableCaption, FolderCaption, Description, SelectableValues, VariableName;
    public int InputMethod, MaxLength, FieldHeight, DecimalPlaces, FieldType = -1, Visibility = -1, FloatingPrecision = -1, QueryBases;
    public bool HasRange, IsMultiTable;
    public double RangeMin, RangeMax;
}

class AcmpCustomFields
{
    public const int MaxFields = 10;
    public const int MaxTextLength = 4000;
    public const int ClientQueryBases = 129;          // Abfragebasis der Client-Tabellen (SYS_TableRelations.IntSupportedQueryBases)
    public const int InputInteger = 13, InputText = 10;
    public static readonly string[] TestedVersions = { "6.10" };
    // Trigger, die auf SYS_TableRelations und SYS_CustomFields bekannt und geprueft sind (Ausgabe von grant-cf-params.sql Abschnitt 1, 22.10.2026):
    // nur ein AFTER-DELETE-Trigger auf SYS_TableRelations - das Tool fuegt nur ein. Auf SYS_CustomFields keiner.
    public static readonly string[] KnownTriggers = { "Trigger_Delete_SYS_TableRelations" };
    // Ziele der GRANTs in mcp_cf_create_table (beobachtet: Datenbankbenutzer vom Typ SQL_USER, keine Rollen)
    public static readonly string[] AcmpRoles = { "RLS_ACMPUser", "AcmpUser_ReadOnly", "AcmpUser_LowPrivilege" };
    public static readonly Dictionary<int, string> InputMethodNames = new Dictionary<int, string> {
        { 1, "imComboBoxFixed" }, { 3, "imDate" }, { 7, "imIpV4" }, { 9, "imMemo" }, { 10, "imText" }, { 13, "imInteger" } };

    public static readonly string FieldsHelp =
        "Liste neuer Felder, 1-" + MaxFields + ": [{\"name\":\"Scan Status\",\"nameEn\":\"Scan status\",\"type\":\"integer\",\"min\":0,\"max\":9," +
        "\"description\":\"...\"}, {\"name\":\"Scan Ergebnis\",\"type\":\"text\",\"maxLength\":500}]. type: integer (Ganzzahl, Spalte BIGINT) oder text " +
        "(Spalte NVARCHAR(maxLength), maxLength 1-" + MaxTextLength + ", Standard 250). nameEn ist optional (Standard = name), min/max nur bei integer und nur zusammen.";

    readonly string _readCs, _writeCs, _backupDir;
    readonly HashSet<string> _allowedReq = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    readonly Action<string> _log;
    readonly Func<string> _acmpVersion;
    readonly bool _allowUntested;
    JObject _preflight; DateTime _preflightAt;
    readonly object _pfLock = new object();
    static readonly object _wLock = new object();
    const int PreflightMaxAgeMinutes = 60;

    public bool ConfigOk { get; private set; }
    public string DisabledReason { get; private set; }
    public ICollection<string> AllowedRequesters { get { return _allowedReq; } }

    public AcmpCustomFields(string readCs, JObject cfg, JObject ccCfg, string cfgDir, Action<string> log, Func<string> acmpVersion)
    {
        _readCs = readCs; _log = log; _acmpVersion = acmpVersion;
        string bd = cfg != null && cfg["backupDir"] != null ? (string)cfg["backupDir"] : "cf-backups";
        _backupDir = Path.IsPathRooted(bd) ? bd : Path.Combine(cfgDir, bd);
        _allowUntested = cfg != null && cfg["allowUntested"] != null && (bool)cfg["allowUntested"];
        if (cfg == null || cfg["enabled"] == null || !(bool)cfg["enabled"]) { DisabledReason = "cfWriter.enabled ist nicht true (nur Lesen und dryRun)"; return; }
        _writeCs = cfg["connectionString"] != null ? (string)cfg["connectionString"] : (ccCfg != null ? (string)ccCfg["connectionString"] : null);
        JArray ar = (cfg["allowedRequesters"] as JArray) ?? (ccCfg != null ? ccCfg["allowedRequesters"] as JArray : null);
        if (ar != null) foreach (JToken t in ar) _allowedReq.Add((string)t);
        if (string.IsNullOrEmpty(_readCs)) { DisabledReason = "db.connectionString fehlt"; return; }
        if (string.IsNullOrEmpty(_writeCs) || _writeCs.StartsWith("HIER_")) { DisabledReason = "kein Schreib-Login (cfWriter.connectionString oder ccWriter.connectionString)"; return; }
        if (_allowedReq.Count == 0) { DisabledReason = "keine erlaubten Antragsteller (cfWriter.allowedRequesters oder ccWriter.allowedRequesters)"; return; }
        ConfigOk = true;
    }

    // ================= Hilfen ohne DB (offline getestet in CfParamTest.cs) =================

    // "{GUID}" in Grossbuchstaben oder null
    public static string GuidBraces(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        Guid g; if (!Guid.TryParse(s.Trim().Trim('{', '}'), out g)) return null;
        return "{" + g.ToString().ToUpperInvariant() + "}";
    }

    static Guid G(string s) { return new Guid(s.Trim('{', '}')); }
    static string Hex(string guid) { return G(guid).ToString("N").ToUpperInvariant(); }

    // Tabellen- und Spaltenname wie der ACMP-Server: CF_VAL_ + 24 bzw. CF_ + 28 Hex-Zeichen der GUID
    public static string TableName(string tableId) { return "CF_VAL_" + Hex(tableId).Substring(0, 24); }
    public static string ColumnName(string fieldId) { return "CF_" + Hex(fieldId).Substring(0, 28); }

    static readonly Regex TableNameRx = new Regex("^CF_VAL_[0-9A-F]{24}$"), ColumnNameRx = new Regex("^CF_[0-9A-F]{28}$");
    public static bool IsCfTableName(string s) { return s != null && TableNameRx.IsMatch(s); }
    public static bool IsCfColumnName(string s) { return s != null && ColumnNameRx.IsMatch(s); }

    // Variablenname wie der ACMP-Server: "Client_" + Tabellenname + "_" + englische Feldbezeichnung, jeweils nur Buchstaben und Ziffern
    // (z.B. Tabelle "Mein Test", Feld "Error Count" -> Client_MeinTest_ErrorCount; Sonderzeichen ausser Leerzeichen nicht beobachtet)
    public static string VarPart(string s)
    {
        var sb = new StringBuilder();
        foreach (char ch in s ?? "") if (char.IsLetterOrDigit(ch) && ch < 128) sb.Append(ch);
        return sb.ToString();
    }
    public static string VariableName(string tableCaption, string captionEn) { return "Client_" + VarPart(tableCaption) + "_" + VarPart(captionEn); }

    // SQL-Typ der Spalte je Eingabemethode (Mitschnitt: Ganzzahl BIGINT, Text NVARCHAR(Laenge))
    public static string SqlType(int inputMethod, int maxLength)
    {
        if (inputMethod == InputInteger) return "BIGINT";
        if (inputMethod == InputText)
        {
            if (maxLength < 1 || maxLength > MaxTextLength) throw new Exception("Textlaenge " + maxLength + " ausserhalb 1-" + MaxTextLength + ".");
            return "NVARCHAR(" + maxLength + ")";
        }
        throw new Exception("Eingabemethode " + inputMethod + " wird nicht unterstuetzt (nur Ganzzahl und Text).");
    }

    static bool IsStr(JToken t) { return t != null && t.Type == JTokenType.String && ((string)t).Trim().Length > 0; }
    static string Str(JToken t) { return t != null && t.Type == JTokenType.String ? ((string)t).Trim() : ""; }
    public static bool IsNull(JToken t) { return t == null || t.Type == JTokenType.Null; }

    static double Num(JToken t, string what)
    {
        if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) return (double)t;
        double d;
        if (t.Type == JTokenType.String && double.TryParse(((string)t).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        throw new Exception(what + " ist keine Zahl.");
    }

    // Ein neues Feld aus dem Auftrag pruefen und mit allen Metadaten wie der ACMP-Server fuellen (Ids und Namen kommen spaeter dazu)
    public static JObject NormalizeField(JObject f, int no)
    {
        string label = "fields[" + no + "]";
        string name = Str(f["name"]);
        if (name.Length == 0) throw new Exception(label + ": name fehlt (deutsche Bezeichnung des Feldes).");
        if (name.Length > 200) throw new Exception(label + ": name ist laenger als 200 Zeichen.");
        string nameEn = IsStr(f["nameEn"]) ? Str(f["nameEn"]) : name;
        if (nameEn.Length > 200) throw new Exception(label + ": nameEn ist laenger als 200 Zeichen.");
        if (VarPart(nameEn).Length == 0) throw new Exception(label + ": nameEn braucht mindestens einen Buchstaben oder eine Ziffer (Variablenname).");
        string type = Str(f["type"]).ToLowerInvariant();
        if (type == "ganzzahl" || type == "int" || type == "number" || type == "zahl") type = "integer";
        if (type == "string") type = "text";
        if (type != "integer" && type != "text") throw new Exception(label + " \"" + name + "\": type muss integer (Ganzzahl) oder text sein - andere Eingabemethoden kann das Tool noch nicht anlegen (in der Console).");
        string desc = f["description"] != null && f["description"].Type == JTokenType.String ? ((string)f["description"]).Trim() : "";
        if (desc.Length > 2000) throw new Exception(label + ": description ist laenger als 2000 Zeichen.");
        var o = new JObject();
        o["name"] = name; o["nameEn"] = nameEn; o["type"] = type; o["description"] = desc;
        bool hasMin = !IsNull(f["min"]), hasMax = !IsNull(f["max"]);
        if (type == "integer")
        {
            if (!IsNull(f["maxLength"])) throw new Exception(label + " \"" + name + "\": maxLength gibt es nur bei text.");
            if (hasMin != hasMax) throw new Exception(label + " \"" + name + "\": min und max nur zusammen angeben (Bereich eingrenzen).");
            double min = hasMin ? Num(f["min"], label + ".min") : 0, max = hasMax ? Num(f["max"], label + ".max") : 0;
            if (hasMin && (min != Math.Floor(min) || max != Math.Floor(max))) throw new Exception(label + " \"" + name + "\": min/max muessen ganze Zahlen sein.");
            if (hasMin && min > max) throw new Exception(label + " \"" + name + "\": min ist groesser als max.");
            // Wie die Console bei "Zahl / Ganzzahl": DecimalPlaces 2 (Feld ausgegraut), Spalte BIGINT
            o["inputMethod"] = InputInteger; o["maxLength"] = 0; o["decimalPlaces"] = 2;
            o["hasRange"] = hasMin; o["rangeMin"] = min; o["rangeMax"] = max;
            o["fieldType"] = 0; o["floatingPrecision"] = 2;
        }
        else
        {
            if (hasMin || hasMax) throw new Exception(label + " \"" + name + "\": min/max gibt es nur bei integer.");
            int len = 250;
            if (!IsNull(f["maxLength"]))
            {
                double d = Num(f["maxLength"], label + ".maxLength");
                if (d != Math.Floor(d) || d < 1 || d > MaxTextLength) throw new Exception(label + " \"" + name + "\": maxLength muss eine ganze Zahl von 1 bis " + MaxTextLength + " sein.");
                len = (int)d;
            }
            o["inputMethod"] = InputText; o["maxLength"] = len; o["decimalPlaces"] = 0;
            o["hasRange"] = false; o["rangeMin"] = 0; o["rangeMax"] = 0;
            o["fieldType"] = 1; o["floatingPrecision"] = 52;
        }
        o["visibility"] = 3;
        o["sqlType"] = SqlType((int)o["inputMethod"], (int)o["maxLength"]);
        return o;
    }

    // Felder des Auftrags (fields als Liste oder JSON-Text)
    public static List<JObject> FieldList(JObject a)
    {
        JToken t = a["fields"];
        if (t != null && t.Type == JTokenType.String && ((string)t).Trim().StartsWith("["))
        {
            try { t = JToken.Parse((string)t); } catch (Exception ex) { throw new Exception("fields ist kein gueltiges JSON: " + ex.Message); }
        }
        JArray arr = t as JArray;
        if (arr == null || arr.Count == 0) throw new Exception("fields fehlt: " + FieldsHelp);
        if (arr.Count > MaxFields) throw new Exception(arr.Count + " Felder - hoechstens " + MaxFields + " je Auftrag.");
        var list = new List<JObject>();
        int no = 0;
        foreach (JToken x in arr)
        {
            JObject f = x as JObject;
            if (f == null) throw new Exception("fields[" + no + "] ist kein Objekt: " + FieldsHelp);
            list.Add(NormalizeField(f, no++));
        }
        return list;
    }

    // Doppelte Namen (deutsch, englisch, Variablenname) innerhalb der neuen Felder und gegen vorhandene Felder der Tabelle
    public static List<string> NameClashes(List<JObject> fields, ICollection<CfMeta> existing, string tableCaption)
    {
        var errs = new List<string>();
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (CfMeta m in existing)
        {
            if (!string.IsNullOrEmpty(m.CaptionDe)) seen["de:" + m.CaptionDe] = "vorhandenes Feld \"" + m.CaptionDe + "\"";
            if (!string.IsNullOrEmpty(m.CaptionEn)) seen["en:" + m.CaptionEn] = "vorhandenes Feld \"" + m.CaptionDe + "\" (EN \"" + m.CaptionEn + "\")";
            if (!string.IsNullOrEmpty(m.VariableName)) seen["var:" + m.VariableName] = "vorhandenes Feld \"" + m.CaptionDe + "\" (Variable " + m.VariableName + ")";
        }
        foreach (JObject f in fields)
        {
            string de = (string)f["name"], en = (string)f["nameEn"], v = VariableName(tableCaption, en);
            string hit;
            int before = errs.Count;
            if (seen.TryGetValue("de:" + de, out hit)) errs.Add("\"" + de + "\": Name gibt es schon in der Tabelle (" + hit + ").");
            else seen["de:" + de] = "neues Feld \"" + de + "\"";
            if (seen.TryGetValue("en:" + en, out hit)) { if (errs.Count == before) errs.Add("\"" + de + "\": englischer Name \"" + en + "\" ist schon vergeben (" + hit + ")."); }
            else seen["en:" + en] = "neues Feld \"" + de + "\"";
            if (seen.TryGetValue("var:" + v, out hit)) { if (errs.Count == before) errs.Add("\"" + de + "\": Variablenname " + v + " ist schon vergeben (" + hit + ") - anderen englischen Namen waehlen."); }
            else seen["var:" + v] = "neues Feld \"" + de + "\"";
        }
        return errs;
    }

    // ---- Schritt "Wert auf Server speichern" (TStoreValueOnACMPServer) aus einem Feld fuellen, wie die Console ----

    static string Fmt(double d) { return d.ToString("R", CultureInfo.InvariantCulture); }

    // Kopf-JSON-Eintraege (StoredCustomFields/StoredTables/StoredFolders) wie in Console-gespeicherten Commands (an "Install-ID" gesehen)
    public static string StoredCustomFieldsJson(CfMeta m)
    {
        string im; if (!InputMethodNames.TryGetValue(m.InputMethod, out im)) im = "imText";
        return "{\"StoredCustomFields\":[{\"Id\":" + JsonConvert.ToString(m.FieldId) + ",\"ParentId\":" + JsonConvert.ToString(m.TableId) +
               ",\"DisplayName_en\":" + JsonConvert.ToString(m.CaptionEn ?? m.CaptionDe ?? "") + ",\"DisplayName_de\":" + JsonConvert.ToString(m.CaptionDe ?? "") +
               ",\"IsMultiField\":false,\"InputMethod\":" + JsonConvert.ToString(im) + ",\"MaxLength\":" + m.MaxLength + ",\"FieldHeight\":" + m.FieldHeight +
               ",\"RangeMin\":" + Fmt(m.RangeMin) + ",\"RangeMax\":" + Fmt(m.RangeMax) + ",\"DecimalPlaces\":" + m.DecimalPlaces + ",\"HasRange\":" + (m.HasRange ? "true" : "false") + "}]}";
    }
    public static string StoredTablesJson(CfMeta m)
    {
        return "{\"StoredTables\":[{\"Id\":" + JsonConvert.ToString(m.TableId) + ",\"ParentId\":" + JsonConvert.ToString(m.FolderId) +
               ",\"DisplayName\":" + JsonConvert.ToString(m.TableCaption ?? "") + ",\"IsMultiTable\":" + (m.IsMultiTable ? "true" : "false") + "}]}";
    }
    public static string StoredFoldersJson(CfMeta m)
    {
        return "{\"StoredFolders\":[{\"Id\":" + JsonConvert.ToString(m.FolderId) + ",\"ParentId\":" + JsonConvert.ToString(m.FolderParentId) +
               ",\"DisplayName\":" + JsonConvert.ToString(m.FolderCaption ?? "") + "}]}";
    }

    // Felder eines TStoreValueOnACMPServer-Schritts in der Reihenfolge der Console (an einem vorhandenen Command gesehen).
    // FieldType = SYS_DISPLAYFIELDS.FIELDTYPE (Text 1 wie in allen vorhandenen Schritten; Ganzzahl 0 noch nicht gegen die Console belegt).
    public static JObject StoreStepFields(CfMeta m, string value, bool clearPrevious, JObject extra)
    {
        if (m.IsMultiTable) throw new Exception("Feld \"" + m.CaptionDe + "\" liegt in einer Mehrfachtabelle - das kann die Kurzform nicht (Schritt in der Console anlegen).");
        var f = new JObject();
        f["_DataVersion"] = "2";
        f["Tablename"] = TableName(m.TableId); f["Fieldname"] = ColumnName(m.FieldId);
        f["FieldDisplayName"] = m.CaptionDe ?? m.CaptionEn ?? "";
        f["FieldType"] = (m.FieldType >= 0 ? m.FieldType : (m.InputMethod == InputInteger ? 0 : 1)).ToString(CultureInfo.InvariantCulture);
        f["FieldID"] = m.FieldId;
        f["Value"] = value ?? "";
        f["TableType"] = "0"; f["TableDisplayName"] = m.TableCaption ?? ""; f["TableID"] = m.TableId;
        foreach (string k in new[] { "Tablenames", "Fieldnames", "FieldDisplayNames", "FieldTypes", "FieldIDs", "Values", "TableTypes", "TableDisplayNames", "TableIds", "FolderTypes", "FolderNames", "FolderGuids", "ClientID" })
            f[k] = "";
        f["ClearPreviousValues"] = clearPrevious ? "True" : "False";
        f["StoreMultiValue"] = "False";
        f["StoredCustomFields"] = StoredCustomFieldsJson(m);
        f["StoredTables"] = StoredTablesJson(m);
        f["StoredFolders"] = StoredFoldersJson(m);
        // gemeinsame Schritt-Felder (Ignore_error, Enabled, _CommandComment, ...) aus der Kurzform uebernehmen
        if (extra != null) foreach (JProperty p in extra.Properties()) if (f[p.Name] == null) f[p.Name] = p.Value.DeepClone();
        return f;
    }

    // Kurzform im Schrittbaum ersetzen: {"class":"TStoreValueOnACMPServer","fields":{"CustomField":"<Feldname oder GUID>","Table":"<optional>",
    // "Value":"%VAR%","ClearPreviousValues":"True"}} -> vollstaendige Felder. Schritte ohne CustomField bleiben unveraendert. Gibt eine Kopie zurueck.
    public static JArray ExpandStoreSteps(JArray steps, Func<string, string, CfMeta> resolve, List<string> notes)
    {
        if (steps == null) return null;
        var copy = (JArray)steps.DeepClone();
        ExpandRec(copy, resolve, notes);
        return copy;
    }

    static void ExpandRec(JArray steps, Func<string, string, CfMeta> resolve, List<string> notes)
    {
        foreach (JToken t in steps)
        {
            JObject st = t as JObject; if (st == null) continue;
            JObject f = st["fields"] as JObject;
            if ((string)st["class"] == "TStoreValueOnACMPServer" && f != null && f["CustomField"] != null)
            {
                string cf = Str(f["CustomField"]), tb = Str(f["Table"]);
                if (cf.Length == 0) throw new Exception("TStoreValueOnACMPServer: CustomField ist leer (Feldname oder GUID des individuellen Feldes).");
                CfMeta m = resolve(cf, tb.Length > 0 ? tb : null);
                var extra = new JObject();
                foreach (JProperty p in f.Properties())
                    if (p.Name != "CustomField" && p.Name != "Table" && p.Name != "Value" && p.Name != "ClearPreviousValues") extra[p.Name] = p.Value;
                string clear = Str(f["ClearPreviousValues"]);
                st["fields"] = StoreStepFields(m, f["Value"] != null ? (string)f["Value"] : "", clear.Length == 0 || clear.Equals("true", StringComparison.OrdinalIgnoreCase), extra);
                if (notes != null) notes.Add("Wert auf Server speichern: individuelles Feld \"" + m.FolderCaption + " > " + m.TableCaption + " > " + m.CaptionDe + "\" " +
                    m.FieldId + " (" + TableName(m.TableId) + "." + ColumnName(m.FieldId) + ", " + (InputMethodNames.ContainsKey(m.InputMethod) ? InputMethodNames[m.InputMethod] : "InputMethod " + m.InputMethod) + ") eingesetzt.");
            }
            JArray ch = st["children"] as JArray;
            if (ch != null) ExpandRec(ch, resolve, notes);
        }
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
            _log("Individuelle Felder (cfWriter) Vorpruefung (ACMP " + (v ?? "?") + "): " + (string)_preflight["summary"]);
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

    static List<string> Columns(SqlConnection c, string table)
    {
        var l = new List<string>();
        var q = c.CreateCommand(); q.CommandText = "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME=@t";
        q.Parameters.AddWithValue("@t", table);
        using (var r = q.ExecuteReader()) while (r.Read()) l.Add(r.GetString(0));
        return l;
    }

    static readonly string[] RelCols = { "TableID", "TableName", "TableDisplayName", "IntSupportedQueryBases", "DisplayInList", "IsMultiTable", "FOREIGN_TABLE", "FOREIGN_KEY", "FolderId", "Description", "IsCustomTable" };
    static readonly string[] CfCols = { "CustomFieldId", "InputMethod", "MaxLength", "FieldHeight", "DecimalPlaces", "RangeMin", "RangeMax", "HasRange", "Description", "SelectableValues" };
    static readonly string[] DispCols = { "AUTOFILLVALUES", "IntIsMultiFieldForBase", "FieldId", "TableID", "DisplayParentId", "IntSupportedQueryBases", "FIELDTYPE", "DisplayValues_EN", "DisplayValues_DE",
        "AVAILABLEVALUES", "TABLENAME", "FIELDNAME", "ChangeMode", "Visibility", "VIRTUAL", "FilterTableName", "FilterFieldName", "FilterJoinFieldName", "Caption_en", "Caption_de", "VariableName",
        "FloatingPrecision", "DisplayMaxIntAsInfinity", "AvailableValueOptions" };

    JObject RunPreflight(string v)
    {
        var checks = new JArray();
        bool createOk = false;
        try
        {
            string mm = null;
            if (!string.IsNullOrEmpty(v)) { string[] p = v.Split('.'); if (p.Length >= 2) mm = p[0] + "." + p[1]; }
            if (mm != null && Array.IndexOf(TestedVersions, mm) >= 0) Add(checks, "ACMP-Version", "ok", v);
            else Add(checks, "ACMP-Version", _allowUntested ? "warn" : "block", (mm == null ? "nicht ermittelbar" : v + " ist nicht getestet") + " (getestet: " + string.Join(", ", TestedVersions) + ")" + (_allowUntested ? " - per cfWriter.allowUntested freigegeben" : ""));
            using (var rc = new SqlConnection(_readCs))
            using (var wc = new SqlConnection(_writeCs))
            {
                rc.Open();
                try { wc.Open(); }
                catch (Exception ex) { Add(checks, "Schreib-Login", "block", "Anmeldung fehlgeschlagen: " + ex.Message); return Result(checks, false); }
                string a = Scalar(rc, "SELECT @@SERVERNAME + '/' + DB_NAME()"), b = Scalar(wc, "SELECT @@SERVERNAME + '/' + DB_NAME()");
                bool same = string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
                Add(checks, "Datenbank", same ? "ok" : "block", a + (same ? " (Lese- und Schreib-Login gleich)" : " / Schreib-Login auf " + b));
                // Spalten, die das Tool schreibt
                foreach (var t in new[] { new { n = "SYS_TableRelations", c = RelCols }, new { n = "SYS_CustomFields", c = CfCols }, new { n = "VW_SYS_DISPLAYFIELDS", c = DispCols } })
                {
                    var have = new HashSet<string>(Columns(rc, t.n), StringComparer.OrdinalIgnoreCase);
                    var miss = new List<string>(); foreach (string col in t.c) if (!have.Contains(col)) miss.Add(col);
                    Add(checks, "Schema " + t.n, miss.Count == 0 ? "ok" : "block", miss.Count == 0 ? t.c.Length + " benoetigte Spalten vorhanden" : "fehlen: " + string.Join(", ", miss.ToArray()));
                }
                // Prozeduren (Metadaten sieht nur, wer EXECUTE hat - deshalb mit dem Schreib-Login lesen)
                string p1 = Scalar(wc, "SELECT STUFF((SELECT ',' + p.name FROM sys.parameters p WHERE p.object_id = OBJECT_ID('dbo.mcp_cf_create_table') ORDER BY p.parameter_id FOR XML PATH('')), 1, 1, '')");
                string p2 = Scalar(wc, "SELECT STUFF((SELECT ',' + p.name FROM sys.parameters p WHERE p.object_id = OBJECT_ID('dbo.mcp_cf_add_column') ORDER BY p.parameter_id FOR XML PATH('')), 1, 1, '')");
                bool procs = p1 == "@TableId" && p2 == "@TableId,@FieldId,@InputMethod,@MaxLength";
                Add(checks, "Prozeduren", procs ? "ok" : "block", procs ? "dbo.mcp_cf_create_table(@TableId), dbo.mcp_cf_add_column(@TableId,@FieldId,@InputMethod,@MaxLength)"
                    : "fehlen oder andere Parameter (create_table: " + (p1 ?? "nicht sichtbar") + "; add_column: " + (p2 ?? "nicht sichtbar") + ") - grant-cf-params.sql ausfuehren");
                // Rechte
                var miss2 = new List<string>();
                if (!HasPerm(wc, "dbo.mcp_cf_create_table", "OBJECT", "EXECUTE")) miss2.Add("EXECUTE mcp_cf_create_table");
                if (!HasPerm(wc, "dbo.mcp_cf_add_column", "OBJECT", "EXECUTE")) miss2.Add("EXECUTE mcp_cf_add_column");
                foreach (string t in new[] { "dbo.SYS_TableRelations", "dbo.SYS_CustomFields", "dbo.VW_SYS_DISPLAYFIELDS" })
                    foreach (string pm in new[] { "SELECT", "INSERT" }) if (!HasPerm(wc, t, "OBJECT", pm)) miss2.Add(pm + " " + t.Substring(4));
                Add(checks, "Rechte", miss2.Count == 0 ? "ok" : "block", miss2.Count == 0 ? "EXECUTE auf beide Prozeduren, SELECT/INSERT auf SYS_TableRelations, SYS_CustomFields, VW_SYS_DISPLAYFIELDS" : "fehlen: " + string.Join(", ", miss2.ToArray()) + " (grant-cf-params.sql)");
                var extra = new List<string>();
                if (Scalar(wc, "SELECT CAST(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE TABLE') AS varchar(5))") == "1") extra.Add("CREATE TABLE (das Tool braucht nur die Prozeduren)");
                if (Scalar(wc, "SELECT CAST(IS_ROLEMEMBER('db_owner') AS varchar(5))") == "1") extra.Add("Mitglied von db_owner");
                if (Scalar(wc, "SELECT CAST(IS_ROLEMEMBER('db_ddladmin') AS varchar(5))") == "1") extra.Add("Mitglied von db_ddladmin");
                if (extra.Count > 0) Add(checks, "Rechte (zu viele)", "warn", string.Join(", ", extra.ToArray()));
                // Trigger: auf den Tabellen keine unbekannten; auf der View muss es den INSTEAD-OF-INSERT-Trigger geben (verteilt auf SYS_DISPLAYFIELDS & Co.)
                var unknown = new List<string>(); int instead = 0;
                var tq = rc.CreateCommand();
                tq.CommandText = "SELECT OBJECT_NAME(t.parent_id), t.name, t.is_instead_of_trigger, t.is_disabled FROM sys.triggers t " +
                    "WHERE t.parent_id IN (OBJECT_ID('dbo.SYS_TableRelations'), OBJECT_ID('dbo.SYS_CustomFields'), OBJECT_ID('dbo.VW_SYS_DISPLAYFIELDS'))";
                using (var r = tq.ExecuteReader())
                    while (r.Read())
                    {
                        string parent = r.GetString(0), name = r.GetString(1);
                        bool io = r.GetBoolean(2), dis = r.GetBoolean(3);
                        if (string.Equals(parent, "VW_SYS_DISPLAYFIELDS", StringComparison.OrdinalIgnoreCase)) { if (io && !dis) instead++; }
                        else if (Array.IndexOf(KnownTriggers, name) < 0) unknown.Add(parent + "." + name);
                    }
                Add(checks, "Trigger", unknown.Count == 0 && instead > 0 ? "ok" : "block",
                    (unknown.Count == 0 ? "keine unbekannten Trigger auf SYS_TableRelations/SYS_CustomFields" : "unbekannte Trigger: " + string.Join(", ", unknown.ToArray()) + " - erst pruefen") +
                    "; VW_SYS_DISPLAYFIELDS: " + instead + " aktive INSTEAD-OF-Trigger" + (instead == 0 ? " (erwartet mindestens einer) - ohne ihn landet nichts in SYS_DISPLAYFIELDS" : ""));
                // Ziele der GRANTs in mcp_cf_create_table (Benutzer oder Rollen, Gross/Klein egal). Lese- und Schreib-Login sehen fremde
                // Datenbankbenutzer meist nicht (Metadaten-Rechte) - deshalb nur Hinweis: fehlt einer, scheitert das GRANT in der Prozedur und
                // die Transaktion wird komplett zurueckgerollt. Am 22.10.2026 per grant-cf-params.sql Abschnitt 1 alle drei vorhanden (SQL_USER).
                var roles = new List<string>();
                foreach (string role in AcmpRoles) if (Scalar(rc, "SELECT CAST(COUNT(*) AS varchar(5)) FROM sys.database_principals WHERE UPPER(name) = UPPER('" + role + "')") != "1") roles.Add(role);
                Add(checks, "ACMP-Datenbankbenutzer", roles.Count == 0 ? "ok" : "warn", roles.Count == 0 ? string.Join(", ", AcmpRoles) + " vorhanden (Ziele der GRANTs fuer neue Tabellen)"
                    : "fuer den Lese-Login nicht sichtbar: " + string.Join(", ", roles.ToArray()) + " (Metadaten-Rechte). Fehlt einer wirklich, bricht mcp_cf_create_table ab und nichts wird geschrieben.");
                // Wurzel der Client-Felder
                string root = ClientRootId(rc);
                Add(checks, "Ordner Client\\Custom Fields", root != null ? "ok" : "block", root ?? "nicht gefunden (SYS_Folders, FolderType 39)");
                createOk = procs && miss2.Count == 0 && root != null;
            }
        }
        catch (Exception ex) { Add(checks, "Pruefung", "block", "Abgebrochen: " + ex.Message); }
        return Result(checks, createOk);
    }

    static JObject Result(JArray checks, bool createOk)
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
        o["createTool"] = ok && createOk;
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
        o["createTool"] = pf["createTool"];
        o["vorpruefung"] = pf;
        return o;
    }

    public void CheckWrite(string requester)
    {
        if (!ConfigOk) throw new Exception("Individuelle Felder anlegen ist ausgeschaltet: " + DisabledReason);
        if (requester == null || !_allowedReq.Contains(requester)) throw new Exception("Antragsteller '" + requester + "' darf keine individuellen Felder anlegen (cfWriter.allowedRequesters).");
        JObject pf = Preflight(false);
        if (pf == null || !(bool)pf["ok"]) throw new Exception("Individuelle Felder anlegen gesperrt - Vorpruefung nicht bestanden: " + (pf != null ? (string)pf["summary"] : "keine Pruefung") + " (Details: acmp_cf_list status=true). Nichts geschrieben.");
        if (pf["createTool"] == null || !(bool)pf["createTool"]) throw new Exception("acmp_cf_create gesperrt - Prozeduren oder Rechte fehlen (acmp_cf_list status=true, grant-cf-params.sql). Nichts geschrieben.");
    }

    // ================= Lesen =================

    class CfFolder { public string Id, Caption, ParentId; }
    class CfTable { public string Id, Name, Caption, FolderId, Description; public int QueryBases; public bool IsMulti; }

    static Dictionary<string, CfFolder> LoadFolders(SqlConnection c)
    {
        var d = new Dictionary<string, CfFolder>(StringComparer.OrdinalIgnoreCase);
        var q = c.CreateCommand();
        q.CommandText = "SELECT CAST(FolderID AS varchar(36)), Caption, CAST(ParentID AS varchar(36)) FROM SYS_Folders WHERE FolderType = 39";
        using (var r = q.ExecuteReader())
            while (r.Read())
            {
                var f = new CfFolder();
                f.Id = "{" + r.GetString(0).ToUpperInvariant() + "}"; f.Caption = r.IsDBNull(1) ? "" : r.GetString(1);
                f.ParentId = r.IsDBNull(2) ? null : "{" + r.GetString(2).ToUpperInvariant() + "}";
                d[f.Id] = f;
            }
        return d;
    }

    static string FolderPath(Dictionary<string, CfFolder> all, string id)
    {
        var parts = new List<string>(); int guard = 0;
        CfFolder f;
        while (id != null && all.TryGetValue(id, out f) && guard++ < 20) { parts.Insert(0, f.Caption); id = f.ParentId; }
        return string.Join("\\", parts.ToArray());
    }

    // "Client\Custom Fields": Ordner "Custom Fields" unter dem obersten Ordner "Client"
    static string ClientRootId(Dictionary<string, CfFolder> all)
    {
        foreach (CfFolder f in all.Values)
        {
            CfFolder p;
            if (f.Caption == "Custom Fields" && f.ParentId != null && all.TryGetValue(f.ParentId, out p) && p.Caption == "Client" && p.ParentId == null) return f.Id;
        }
        return null;
    }
    static string ClientRootId(SqlConnection c) { return ClientRootId(LoadFolders(c)); }

    static bool UnderClientRoot(Dictionary<string, CfFolder> all, string folderId)
    {
        string root = ClientRootId(all); int guard = 0;
        CfFolder f;
        while (folderId != null && guard++ < 20)
        {
            if (string.Equals(folderId, root, StringComparison.OrdinalIgnoreCase)) return true;
            if (!all.TryGetValue(folderId, out f)) return false;
            folderId = f.ParentId;
        }
        return false;
    }

    static Dictionary<string, CfTable> LoadTables(SqlConnection c, SqlTransaction tx)
    {
        var d = new Dictionary<string, CfTable>(StringComparer.OrdinalIgnoreCase);
        var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = "SELECT CAST(TableID AS varchar(36)), TABLENAME, TableDisplayName, IntSupportedQueryBases, IsMultiTable, CAST(FolderId AS varchar(36)), Description " +
                        "FROM SYS_TableRelations WHERE IsCustomTable = 1";
        using (var r = q.ExecuteReader())
            while (r.Read())
            {
                var t = new CfTable();
                t.Id = "{" + r.GetString(0).ToUpperInvariant() + "}"; t.Name = r.IsDBNull(1) ? "" : r.GetString(1); t.Caption = r.IsDBNull(2) ? "" : r.GetString(2);
                t.QueryBases = r.IsDBNull(3) ? 0 : Convert.ToInt32(r.GetValue(3)); t.IsMulti = !r.IsDBNull(4) && Convert.ToBoolean(r.GetValue(4));
                t.FolderId = r.IsDBNull(5) ? null : "{" + r.GetString(5).ToUpperInvariant() + "}"; t.Description = r.IsDBNull(6) ? null : r.GetString(6);
                d[t.Id] = t;
            }
        return d;
    }

    // Alle individuellen Felder mit Tabelle, Ordner und Anzeige-Metadaten (Lese-Login)
    static List<CfMeta> LoadFields(SqlConnection c)
    {
        Dictionary<string, CfFolder> folders = LoadFolders(c);
        Dictionary<string, CfTable> tables = LoadTables(c, null);
        var l = new List<CfMeta>();
        var q = c.CreateCommand();
        q.CommandText = "SELECT CAST(cf.CustomFieldId AS varchar(36)), CAST(cf.TableId AS varchar(36)), cf.InputMethod, cf.MaxLength, cf.FieldHeight, cf.DecimalPlaces, cf.HasRange, cf.RangeMin, cf.RangeMax, " +
                        "cf.SelectableValues, cf.Description, cf.Caption_de, cf.Caption_en, d.FIELDTYPE, d.Visibility, d.FloatingPrecision, d.VariableName " +
                        "FROM VW_SYS_CustomFields cf LEFT JOIN VW_SYS_DISPLAYFIELDS d ON d.FieldId = cf.CustomFieldId";
        using (var r = q.ExecuteReader())
            while (r.Read())
            {
                var m = new CfMeta();
                m.FieldId = "{" + r.GetString(0).ToUpperInvariant() + "}"; m.TableId = "{" + r.GetString(1).ToUpperInvariant() + "}";
                m.InputMethod = Convert.ToInt32(r.GetValue(2)); m.MaxLength = Convert.ToInt32(r.GetValue(3)); m.FieldHeight = Convert.ToInt32(r.GetValue(4));
                m.DecimalPlaces = Convert.ToInt32(r.GetValue(5)); m.HasRange = Convert.ToBoolean(r.GetValue(6));
                m.RangeMin = Convert.ToDouble(r.GetValue(7)); m.RangeMax = Convert.ToDouble(r.GetValue(8));
                m.SelectableValues = r.IsDBNull(9) ? null : r.GetString(9); m.Description = r.IsDBNull(10) ? null : r.GetString(10);
                m.CaptionDe = r.IsDBNull(11) ? null : r.GetString(11); m.CaptionEn = r.IsDBNull(12) ? null : r.GetString(12);
                m.FieldType = r.IsDBNull(13) ? -1 : Convert.ToInt32(r.GetValue(13)); m.Visibility = r.IsDBNull(14) ? -1 : Convert.ToInt32(r.GetValue(14));
                m.FloatingPrecision = r.IsDBNull(15) ? -1 : Convert.ToInt32(r.GetValue(15)); m.VariableName = r.IsDBNull(16) ? null : r.GetString(16);
                CfTable t;
                if (tables.TryGetValue(m.TableId, out t))
                {
                    m.TableCaption = t.Caption; m.IsMultiTable = t.IsMulti; m.QueryBases = t.QueryBases; m.FolderId = t.FolderId;
                    CfFolder f;
                    if (t.FolderId != null && folders.TryGetValue(t.FolderId, out f)) { m.FolderCaption = f.Caption; m.FolderParentId = f.ParentId; }
                }
                l.Add(m);
            }
        return l;
    }

    // Feld nach Name (deutsch oder englisch) oder GUID, optional eingegrenzt auf eine Tabelle (Name oder GUID)
    static CfMeta ResolveField(List<CfMeta> all, string field, string table)
    {
        string g = GuidBraces(field), tg = GuidBraces(table);
        var hits = all.FindAll(m =>
            (g != null ? string.Equals(m.FieldId, g, StringComparison.OrdinalIgnoreCase)
                       : string.Equals(m.CaptionDe, field, StringComparison.OrdinalIgnoreCase) || string.Equals(m.CaptionEn, field, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrEmpty(table) || (tg != null ? string.Equals(m.TableId, tg, StringComparison.OrdinalIgnoreCase) : string.Equals(m.TableCaption, table, StringComparison.OrdinalIgnoreCase))));
        if (hits.Count == 0) throw new Exception("Individuelles Feld \"" + field + "\"" + (string.IsNullOrEmpty(table) ? "" : " in Tabelle \"" + table + "\"") + " nicht gefunden (acmp_cf_list zeigt alle).");
        if (hits.Count > 1)
        {
            var l = new List<string>(); foreach (CfMeta m in hits) l.Add(m.TableCaption + " > " + m.CaptionDe + " " + m.FieldId);
            throw new Exception("Individuelles Feld \"" + field + "\" ist nicht eindeutig (" + string.Join("; ", l.ToArray()) + ") - Table angeben oder GUID nehmen.");
        }
        return hits[0];
    }

    // Fuer die Kurzform im CC-Writer: Feld ueber eine offene Lese-Verbindung aufloesen
    public static CfMeta ResolveWith(SqlConnection c, string field, string table) { return ResolveField(LoadFields(c), field, table); }

    static JObject FieldJson(CfMeta m, Dictionary<string, string> sqlTypes)
    {
        var o = new JObject();
        o["id"] = m.FieldId; o["name"] = m.CaptionDe; o["nameEn"] = m.CaptionEn;
        string im; o["type"] = InputMethodNames.TryGetValue(m.InputMethod, out im) ? im : "InputMethod " + m.InputMethod;
        if (m.MaxLength > 0) o["maxLength"] = m.MaxLength;
        if (m.HasRange) { o["min"] = m.RangeMin; o["max"] = m.RangeMax; }
        if (!string.IsNullOrEmpty(m.SelectableValues)) o["values"] = m.SelectableValues;
        if (!string.IsNullOrEmpty(m.Description)) o["description"] = m.Description;
        string col = ColumnName(m.FieldId), st;
        o["column"] = col;
        if (sqlTypes != null && sqlTypes.TryGetValue(TableName(m.TableId) + "." + col, out st)) o["sqlType"] = st;
        if (m.VariableName != null) o["variable"] = m.VariableName;
        return o;
    }

    static Dictionary<string, string> SqlTypes(SqlConnection c)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var q = c.CreateCommand();
        q.CommandText = "SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME LIKE 'CF[_]VAL[_]%'";
        using (var r = q.ExecuteReader())
            while (r.Read())
                d[r.GetString(0) + "." + r.GetString(1)] = r.GetString(2) + (r.IsDBNull(3) ? "" : "(" + (Convert.ToInt32(r.GetValue(3)) < 0 ? "max" : Convert.ToString(r.GetValue(3))) + ")");
        return d;
    }

    // acmp_cf_list: Baum Ordner > Tabelle > Felder; mit field (+ values=true) die Werte je Client
    public JObject List(JObject a)
    {
        if (string.IsNullOrEmpty(_readCs)) throw new Exception("db.connectionString fehlt.");
        string table = Str(a["table"]), field = Str(a["field"]);
        bool values = a["values"] != null && a["values"].Type == JTokenType.Boolean && (bool)a["values"];
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            List<CfMeta> all = LoadFields(c);
            Dictionary<string, string> types = SqlTypes(c);
            if (field.Length > 0)
            {
                CfMeta m = ResolveField(all, field, table.Length > 0 ? table : null);
                var o = new JObject();
                o["ordner"] = FolderPath(LoadFolders(c), m.FolderId);
                o["tabelle"] = m.TableCaption; o["tableId"] = m.TableId; o["tabellenname"] = TableName(m.TableId);
                o["feld"] = FieldJson(m, types);
                if (values) o["werte"] = Values(c, m, Str(a["client"]), a["top"]);
                o["hinweis"] = "Im Client Command: Schritt \"Wert auf Server speichern\" (TStoreValueOnACMPServer) mit fields {\"CustomField\":\"" + m.CaptionDe + "\",\"Table\":\"" + m.TableCaption + "\",\"Value\":\"%VARIABLE%\"} - acmp_cc_new_version/acmp_cc_create_draft fuellen den Rest.";
                return o;
            }
            Dictionary<string, CfFolder> folders = LoadFolders(c);
            Dictionary<string, CfTable> tables = LoadTables(c, null);
            string tg = GuidBraces(table);
            var byFolder = new SortedDictionary<string, JArray>(StringComparer.OrdinalIgnoreCase);
            int nt = 0, nf = 0;
            foreach (CfTable t in tables.Values)
            {
                if (table.Length > 0 && !(tg != null ? string.Equals(t.Id, tg, StringComparison.OrdinalIgnoreCase) : string.Equals(t.Caption, table, StringComparison.OrdinalIgnoreCase))) continue;
                var tj = new JObject();
                tj["tabelle"] = t.Caption; tj["tableId"] = t.Id; tj["tabellenname"] = t.Name;
                tj["basis"] = t.QueryBases == ClientQueryBases ? "Clients" : t.QueryBases == 1024 ? "Asset Management" : "Abfragebasis " + t.QueryBases;
                if (t.IsMulti) tj["mehrfachtabelle"] = true;
                if (!string.IsNullOrEmpty(t.Description)) tj["beschreibung"] = t.Description;
                var fa = new JArray();
                foreach (CfMeta m in all) if (string.Equals(m.TableId, t.Id, StringComparison.OrdinalIgnoreCase)) { fa.Add(FieldJson(m, types)); nf++; }
                tj["felder"] = fa;
                string path = t.FolderId != null ? FolderPath(folders, t.FolderId) : "(ohne Ordner)";
                JArray la; if (!byFolder.TryGetValue(path, out la)) { la = new JArray(); byFolder[path] = la; }
                la.Add(tj); nt++;
            }
            if (table.Length > 0 && nt == 0) throw new Exception("Tabelle \"" + table + "\" nicht gefunden (ohne table zeigt acmp_cf_list alle).");
            var res = new JObject();
            res["tabellen"] = nt; res["felder"] = nf;
            var od = new JObject(); foreach (var kv in byFolder) od[kv.Key] = kv.Value;
            res["ordner"] = od;
            res["hinweis"] = "Werte eines Feldes je Client: field=<Name> values=true. Neue Felder/Tabellen: acmp_cf_create (erst dryRun=true).";
            return res;
        }
    }

    JObject Values(SqlConnection c, CfMeta m, string client, JToken topTok)
    {
        string tn = TableName(m.TableId), cn = ColumnName(m.FieldId);
        if (!IsCfTableName(tn) || !IsCfColumnName(cn)) throw new Exception("Ungueltiger Tabellen-/Spaltenname.");
        if (m.IsMultiTable) throw new Exception("Werte von Mehrfachtabellen zeigt das Tool noch nicht.");
        int top = 50;
        if (!IsNull(topTok)) { try { top = Math.Max(1, Math.Min(500, (int)topTok)); } catch { throw new Exception("top muss eine Zahl sein."); } }
        var o = new JObject();
        // Verteilung der Werte (z.B. wie viele Clients Status 2 haben)
        var sq = c.CreateCommand();
        sq.CommandText = "SELECT TOP 20 CAST(v.[" + cn + "] AS nvarchar(400)), COUNT(*) FROM [" + tn + "] v JOIN CLT_CLIENTS_TABLE c ON c.CLIENTID = v.CLIENTID " +
                         "WHERE v.[" + cn + "] IS NOT NULL GROUP BY v.[" + cn + "] ORDER BY COUNT(*) DESC";
        var dist = new JArray(); int total = 0;
        using (var r = sq.ExecuteReader())
            while (r.Read()) { var d = new JObject(); d["wert"] = r.IsDBNull(0) ? null : r.GetString(0); d["clients"] = r.GetInt32(1); total += r.GetInt32(1); dist.Add(d); }
        o["clientsMitWert"] = total; o["verteilung"] = dist;
        var q = c.CreateCommand();
        q.CommandText = "SELECT TOP (" + top + ") c.COMPUTERNAME, CAST(v.[" + cn + "] AS nvarchar(4000)) FROM [" + tn + "] v JOIN CLT_CLIENTS_TABLE c ON c.CLIENTID = v.CLIENTID " +
                        "WHERE v.[" + cn + "] IS NOT NULL" + (client.Length > 0 ? " AND c.COMPUTERNAME LIKE @c" : "") + " ORDER BY c.COMPUTERNAME";
        if (client.Length > 0) q.Parameters.AddWithValue("@c", client.Replace("*", "%"));
        var rows = new JArray();
        using (var r = q.ExecuteReader())
            while (r.Read()) { var x = new JObject(); x["client"] = r.IsDBNull(0) ? null : r.GetString(0); x["wert"] = r.IsDBNull(1) ? null : r.GetString(1); rows.Add(x); }
        o["clients"] = rows;
        return o;
    }

    // ================= Planen =================
    public JObject Plan(JObject a, string requester)
    {
        if (string.IsNullOrEmpty(_readCs)) throw new Exception("db.connectionString fehlt.");
        string table = Str(a["table"]), newTable = Str(a["newTable"]);
        if ((table.Length > 0) == (newTable.Length > 0)) throw new Exception("Entweder table (vorhandene Tabelle: Name oder GUID) oder newTable (Name einer neuen Tabelle) angeben.");
        List<JObject> fields = FieldList(a);
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            Dictionary<string, CfFolder> folders = LoadFolders(c);
            Dictionary<string, CfTable> tables = LoadTables(c, null);
            List<CfMeta> all = LoadFields(c);
            var p = new JObject();
            p["action"] = "cfcreate"; p["batchId"] = "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}";
            var tj = new JObject();
            CfTable t = null;
            string folderId, tableCaption;
            if (newTable.Length > 0)
            {
                if (newTable.Length > 100) throw new Exception("newTable ist laenger als 100 Zeichen.");
                if (VarPart(newTable).Length == 0) throw new Exception("newTable braucht mindestens einen Buchstaben oder eine Ziffer.");
                string folder = Str(a["folder"]); if (folder.Length == 0) folder = "Client Info";
                folderId = ResolveFolder(folders, folder);
                foreach (CfTable x in tables.Values)
                    if (string.Equals(x.FolderId, folderId, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Caption, newTable, StringComparison.OrdinalIgnoreCase))
                        throw new Exception("Im Ordner \"" + FolderPath(folders, folderId) + "\" gibt es schon eine Tabelle \"" + newTable + "\" " + x.Id + " - table=\"" + newTable + "\" nimmt die vorhandene.");
                string tid = "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}";
                tableCaption = newTable;
                tj["new"] = true; tj["id"] = tid; tj["name"] = TableName(tid); tj["caption"] = newTable;
                string td = a["tableDescription"] != null && a["tableDescription"].Type == JTokenType.String ? ((string)a["tableDescription"]).Trim() : "";
                if (td.Length > 2000) throw new Exception("tableDescription ist laenger als 2000 Zeichen.");
                tj["description"] = td;
            }
            else
            {
                t = ResolveTable(tables, table);
                if (t.QueryBases != ClientQueryBases) throw new Exception("Tabelle \"" + t.Caption + "\" gehoert nicht zu den Clients (Abfragebasis " + t.QueryBases + ") - das Tool legt nur Client-Felder an.");
                if (t.IsMulti) throw new Exception("Tabelle \"" + t.Caption + "\" ist eine Mehrfachtabelle - das kann das Tool nicht (in der Console).");
                if (!IsCfTableName(t.Name) || !string.Equals(t.Name, TableName(t.Id), StringComparison.Ordinal)) throw new Exception("Tabelle \"" + t.Caption + "\" hat einen unerwarteten Namen (" + t.Name + ") - nichts geplant.");
                folderId = t.FolderId; tableCaption = t.Caption;
                tj["new"] = false; tj["id"] = t.Id; tj["name"] = t.Name; tj["caption"] = t.Caption;
            }
            if (folderId == null || !UnderClientRoot(folders, folderId)) throw new Exception("Ordner " + (folderId ?? "?") + " liegt nicht unter Client\\Custom Fields - das Tool legt nur Client-Felder an.");
            tj["folderId"] = folderId; tj["folderPath"] = FolderPath(folders, folderId);
            p["table"] = tj;
            // vorhandene Felder der Tabelle (Namen pruefen, Stand einfrieren)
            var existing = all.FindAll(m => string.Equals(m.TableId, (string)tj["id"], StringComparison.OrdinalIgnoreCase));
            List<string> clashes = NameClashes(fields, existing, tableCaption);
            var usedVars = new HashSet<string>(StringComparer.OrdinalIgnoreCase); foreach (CfMeta m in all) if (m.VariableName != null) usedVars.Add(m.VariableName);
            foreach (JObject f in fields) { string v = VariableName(tableCaption, (string)f["nameEn"]); if (usedVars.Contains(v) && !clashes.Exists(x => x.Contains(v))) clashes.Add("\"" + f["name"] + "\": Variablenname " + v + " gibt es schon (anderes Feld) - anderen englischen Namen waehlen."); }
            if (clashes.Count > 0) throw new Exception("Nichts geplant:\n- " + string.Join("\n- ", clashes.ToArray()));
            var ex = new List<string>(); foreach (CfMeta m in existing) ex.Add(m.FieldId); ex.Sort(StringComparer.OrdinalIgnoreCase);
            p["existingFieldIds"] = new JArray(ex.ToArray());
            var fa = new JArray();
            foreach (JObject f in fields)
            {
                string fid = "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}";
                f["id"] = fid; f["column"] = ColumnName(fid); f["variableName"] = VariableName(tableCaption, (string)f["nameEn"]);
                fa.Add(f);
            }
            p["fields"] = fa;

            // Vorschau
            var pv = new JArray();
            pv.Add((bool)tj["new"] ? "NEUE TABELLE \"" + tableCaption + "\" im Ordner \"" + tj["folderPath"] + "\" anlegen (" + tj["name"] + ", Abfragebasis Clients) und " + fa.Count + " Feld(er):"
                                   : fa.Count + " neue(s) Feld(er) in der Tabelle \"" + tableCaption + "\" (" + tj["name"] + ", Ordner \"" + tj["folderPath"] + "\", bisher " + existing.Count + " Feld(er)):");
            int no = 0;
            foreach (JObject f in fa)
                pv.Add(++no + ". \"" + f["name"] + "\"" + ((string)f["nameEn"] != (string)f["name"] ? " (EN \"" + f["nameEn"] + "\")" : "") + " - " +
                       ((string)f["type"] == "integer" ? "Ganzzahl" + ((bool)f["hasRange"] ? ", Bereich " + Fmt((double)f["rangeMin"]) + " bis " + Fmt((double)f["rangeMax"]) : "") : "Text, Laenge " + f["maxLength"]) +
                       "; Spalte " + f["column"] + " " + f["sqlType"] + ", Variable " + f["variableName"] + (((string)f["description"]).Length > 0 ? "; Beschreibung: " + OneLine((string)f["description"], 120) : ""));
            if ((bool)tj["new"] && ((string)tj["description"]).Length > 0) pv.Add("Beschreibung der Tabelle: " + OneLine((string)tj["description"], 200));
            // Muster aus vorhandenen Client-Feldern gleicher Eingabemethode (Anzeige-Metadaten wie der ACMP-Server)
            foreach (int im in new[] { InputInteger, InputText })
            {
                JObject sample = null; foreach (JObject f in fa) if ((int)f["inputMethod"] == im) { sample = f; break; }
                if (sample == null) continue;
                CfMeta refm = all.Find(m => m.InputMethod == im && m.QueryBases == ClientQueryBases && m.FieldType >= 0);
                if (refm == null) { pv.Add("Hinweis: kein vorhandenes Client-Feld vom Typ " + InputMethodNames[im] + " zum Vergleich der Anzeige-Metadaten."); continue; }
                bool same = refm.FieldType == (int)sample["fieldType"] && refm.Visibility == (int)sample["visibility"] && refm.FloatingPrecision == (int)sample["floatingPrecision"];
                pv.Add((same ? "Anzeige-Metadaten wie das vorhandene Feld \"" : "ACHTUNG: Anzeige-Metadaten weichen ab vom vorhandenen Feld \"") + refm.CaptionDe + "\" (" + InputMethodNames[im] + "): FIELDTYPE " +
                       sample["fieldType"] + "/" + refm.FieldType + ", Visibility " + sample["visibility"] + "/" + refm.Visibility + ", FloatingPrecision " + sample["floatingPrecision"] + "/" + refm.FloatingPrecision + " (neu/vorhanden).");
            }
            pv.Add("Ablauf wie der ACMP-Server beim Speichern in der Console (Mitschnitt 22.10.2026), eine Transaktion: " + ((bool)tj["new"] ? "dbo.mcp_cf_create_table (CREATE TABLE mit PK, GRANTs, FK auf CLT_CLIENTS_TABLE), Zeile in SYS_TableRelations; " : "") +
                   "je Feld dbo.mcp_cf_add_column (ALTER TABLE ADD), Zeile in VW_SYS_DISPLAYFIELDS (Trigger verteilen auf SYS_DISPLAYFIELDS & Co.) und in SYS_CustomFields. Vorher Sicherung des Plans (cf-backups).");
            pv.Add("Danach: in der Console unter den individuellen Feldern pruefen. Ob der ACMP-Server neue Felder fuer \"Wert auf Server speichern\" ohne Neustart kennt, ist noch nicht belegt - erster Lauf zeigt es (acmp_cf_list field=... values=true).");
            pv.Add("Zurueck: Loeschen kann das Tool nicht - in der Console (Feld/Tabelle loeschen).");
            p["vorschau"] = pv;
            return p;
        }
    }

    static string ResolveFolder(Dictionary<string, CfFolder> all, string arg)
    {
        string g = GuidBraces(arg);
        if (g != null) { if (!all.ContainsKey(g)) throw new Exception("Ordner " + g + " gibt es nicht (SYS_Folders, FolderType 39)."); return g; }
        var hits = new List<string>();
        foreach (CfFolder f in all.Values)
            if (UnderClientRoot(all, f.Id) && (string.Equals(f.Caption, arg, StringComparison.OrdinalIgnoreCase) || string.Equals(FolderPath(all, f.Id), arg, StringComparison.OrdinalIgnoreCase))) hits.Add(f.Id);
        if (hits.Count == 0) throw new Exception("Ordner \"" + arg + "\" unter Client\\Custom Fields nicht gefunden (z.B. \"Client Info\").");
        if (hits.Count > 1) throw new Exception("Ordner \"" + arg + "\" ist nicht eindeutig - Pfad oder GUID angeben.");
        return hits[0];
    }

    static CfTable ResolveTable(Dictionary<string, CfTable> all, string arg)
    {
        string g = GuidBraces(arg);
        CfTable t;
        if (g != null) { if (!all.TryGetValue(g, out t)) throw new Exception("Tabelle " + g + " ist keine individuelle Tabelle (SYS_TableRelations)."); return t; }
        var hits = new List<CfTable>();
        foreach (CfTable x in all.Values) if (string.Equals(x.Caption, arg, StringComparison.OrdinalIgnoreCase)) hits.Add(x);
        if (hits.Count == 0) throw new Exception("Tabelle \"" + arg + "\" nicht gefunden (acmp_cf_list zeigt alle; neue Tabelle mit newTable).");
        if (hits.Count > 1) throw new Exception("Tabellenname \"" + arg + "\" ist nicht eindeutig - GUID angeben.");
        return hits[0];
    }

    static string OneLine(string s, int max)
    {
        string t = Regex.Replace(s ?? "", @"\s+", " ").Trim();
        return t.Length > max ? t.Substring(0, max - 3) + "..." : t;
    }

    // ================= Schreiben (nur der eingefrorene Plan aus der Freigabe) =================
    public JObject Execute(JObject a, string requester)
    {
        CheckWrite(requester);
        JObject p = a["_plan"] as JObject;
        if (p == null || (string)p["action"] != "cfcreate") throw new Exception("acmp_cf_create schreibt nur mit einem vom Server erstellten Plan - nichts geschrieben.");
        lock (_wLock) { return Exec(p, requester); }
    }

    // Stand gegen den Plan pruefen (Lese-Login): Tabelle, vorhandene Felder, neue Ids und Namen
    void Revalidate(SqlConnection rc, JObject p)
    {
        JObject tj = (JObject)p["table"];
        string tid = (string)tj["id"];
        Dictionary<string, CfTable> tables = LoadTables(rc, null);
        Dictionary<string, CfFolder> folders = LoadFolders(rc);
        if (!folders.ContainsKey((string)tj["folderId"])) throw new Exception("Ordner \"" + tj["folderPath"] + "\" gibt es nicht mehr - nichts geschrieben.");
        var cq = rc.CreateCommand(); cq.CommandText = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME=@t"; cq.Parameters.AddWithValue("@t", (string)tj["name"]);
        bool physical = Convert.ToInt32(cq.ExecuteScalar()) > 0;
        if ((bool)tj["new"])
        {
            if (tables.ContainsKey(tid) || physical) throw new Exception("Tabelle " + tj["name"] + " gibt es inzwischen - nichts geschrieben, bitte erneut aufrufen.");
            foreach (CfTable x in tables.Values)
                if (string.Equals(x.FolderId, (string)tj["folderId"], StringComparison.OrdinalIgnoreCase) && string.Equals(x.Caption, (string)tj["caption"], StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Im Ordner gibt es inzwischen eine Tabelle \"" + tj["caption"] + "\" - nichts geschrieben.");
        }
        else
        {
            CfTable t;
            if (!tables.TryGetValue(tid, out t) || !physical) throw new Exception("Tabelle \"" + tj["caption"] + "\" gibt es nicht mehr - nichts geschrieben.");
            if (t.IsMulti || t.QueryBases != ClientQueryBases) throw new Exception("Tabelle \"" + tj["caption"] + "\" wurde geaendert - nichts geschrieben.");
        }
        List<CfMeta> all = LoadFields(rc);
        var now = new List<string>(); foreach (CfMeta m in all) if (string.Equals(m.TableId, tid, StringComparison.OrdinalIgnoreCase)) now.Add(m.FieldId);
        now.Sort(StringComparer.OrdinalIgnoreCase);
        var planned = new List<string>(); foreach (JToken x in (JArray)p["existingFieldIds"]) planned.Add((string)x);
        if (string.Join("|", now.ToArray()) != string.Join("|", planned.ToArray())) throw new Exception("Die Felder der Tabelle \"" + tj["caption"] + "\" haben sich seit der Planung geaendert - nichts geschrieben, bitte erneut aufrufen.");
        foreach (JObject f in (JArray)p["fields"])
            if (all.Exists(m => string.Equals(m.FieldId, (string)f["id"], StringComparison.OrdinalIgnoreCase))) throw new Exception("Feld-Id " + f["id"] + " ist schon vergeben - nichts geschrieben.");
    }

    string SaveBackup(JObject b, string kind)
    {
        Directory.CreateDirectory(_backupDir);
        string file = Path.Combine(_backupDir, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "_" + kind + ".json");
        File.WriteAllText(file, b.ToString(Formatting.Indented), new UTF8Encoding(false));
        if (new FileInfo(file).Length < 40) throw new Exception("Sicherung unvollstaendig - nichts geaendert.");
        return file;
    }

    static object Db(string s) { return string.IsNullOrEmpty(s) ? (object)DBNull.Value : s; }

    JObject Exec(JObject p, string requester)
    {
        JObject tj = (JObject)p["table"];
        JArray fa = (JArray)p["fields"];
        string tid = (string)tj["id"], tname = (string)tj["name"], caption = (string)tj["caption"];
        bool isNew = (bool)tj["new"];
        if (!IsCfTableName(tname) || tname != TableName(tid)) throw new Exception("Plan mit ungueltigem Tabellennamen - nichts geschrieben.");
        string backup;
        using (var rc = new SqlConnection(_readCs))
        using (var wc = new SqlConnection(_writeCs))
        {
            rc.Open(); wc.Open();
            Revalidate(rc, p);
            var b = new JObject(); b["kind"] = "cfcreate"; b["grund"] = "vor acmp_cf_create (Plan; Loeschen in der Console)"; b["von"] = requester; b["zeit"] = DateTime.Now.ToString("s"); b["plan"] = p;
            backup = SaveBackup(b, "cfcreate");
            using (SqlTransaction tx = wc.BeginTransaction())
            {
                if (isNew)
                {
                    var q = wc.CreateCommand(); q.Transaction = tx; q.CommandType = CommandType.StoredProcedure; q.CommandText = "dbo.mcp_cf_create_table";
                    q.Parameters.AddWithValue("@TableId", G(tid));
                    q.ExecuteNonQuery();
                    var ins = wc.CreateCommand(); ins.Transaction = tx;
                    ins.CommandText = "INSERT INTO SYS_TableRelations (TableID, TableName, TableDisplayName, IntSupportedQueryBases, DisplayInList, IsMultiTable, FOREIGN_TABLE, FOREIGN_KEY, FolderId, Description, IsCustomTable) " +
                                      "VALUES (@id, @tn, @cap, @qb, 1, 0, NULL, NULL, @fo, @de, 1)";
                    ins.Parameters.AddWithValue("@id", G(tid)); ins.Parameters.AddWithValue("@tn", tname); ins.Parameters.AddWithValue("@cap", caption);
                    ins.Parameters.AddWithValue("@qb", ClientQueryBases); ins.Parameters.AddWithValue("@fo", G((string)tj["folderId"])); ins.Parameters.AddWithValue("@de", Db((string)tj["description"]));
                    if (ins.ExecuteNonQuery() != 1) throw new Exception("SYS_TableRelations: Zeile nicht angelegt - zurueckgerollt, nichts geschrieben.");
                }
                foreach (JObject f in fa)
                {
                    string fid = (string)f["id"], col = (string)f["column"];
                    if (!IsCfColumnName(col) || col != ColumnName(fid)) throw new Exception("Plan mit ungueltigem Spaltennamen - zurueckgerollt, nichts geschrieben.");
                    int im = (int)f["inputMethod"];
                    var q = wc.CreateCommand(); q.Transaction = tx; q.CommandType = CommandType.StoredProcedure; q.CommandText = "dbo.mcp_cf_add_column";
                    q.Parameters.AddWithValue("@TableId", G(tid)); q.Parameters.AddWithValue("@FieldId", G(fid));
                    q.Parameters.AddWithValue("@InputMethod", im); q.Parameters.AddWithValue("@MaxLength", (int)f["maxLength"]);
                    q.ExecuteNonQuery();
                    // Anzeige-Metadaten wie der ACMP-Server (INSTEAD-OF-Trigger der View verteilen auf SYS_DISPLAYFIELDS, _Localization, _AvailableValues)
                    var d = wc.CreateCommand(); d.Transaction = tx;
                    d.CommandText = "INSERT INTO VW_SYS_DISPLAYFIELDS (AUTOFILLVALUES, IntIsMultiFieldForBase, FieldId, TableID, DisplayParentId, IntSupportedQueryBases, FIELDTYPE, DisplayValues_EN, DisplayValues_DE, " +
                        "AVAILABLEVALUES, TABLENAME, FIELDNAME, ChangeMode, Visibility, VIRTUAL, FilterTableName, FilterFieldName, FilterJoinFieldName, Caption_en, Caption_de, VariableName, FloatingPrecision, " +
                        "DisplayMaxIntAsInfinity, AvailableValueOptions) VALUES (0, 0, @fid, @tid, NULL, @qb, @ft, N'', N'', N'', @tn, @fn, 0, @vis, 0, N'', N'', N'', @en, @de, @var, @fp, 0, 0)";
                    d.Parameters.AddWithValue("@fid", fid); d.Parameters.AddWithValue("@tid", tid); d.Parameters.Add("@qb", SqlDbType.BigInt).Value = (long)ClientQueryBases;
                    d.Parameters.AddWithValue("@ft", (int)f["fieldType"]); d.Parameters.AddWithValue("@tn", tname); d.Parameters.AddWithValue("@fn", col);
                    d.Parameters.Add("@vis", SqlDbType.BigInt).Value = (long)(int)f["visibility"];
                    d.Parameters.AddWithValue("@en", (string)f["nameEn"]); d.Parameters.AddWithValue("@de", (string)f["name"]); d.Parameters.AddWithValue("@var", (string)f["variableName"]);
                    d.Parameters.AddWithValue("@fp", (int)f["floatingPrecision"]);
                    d.ExecuteNonQuery();
                    var cfq = wc.CreateCommand(); cfq.Transaction = tx;
                    cfq.CommandText = "INSERT INTO SYS_CustomFields (CustomFieldId, InputMethod, MaxLength, FieldHeight, DecimalPlaces, HasRange, RangeMin, RangeMax, SelectableValues, Description) " +
                                      "VALUES (@id, @im, @ml, 0, @dp, @hr, @rmin, @rmax, NULL, @de)";
                    cfq.Parameters.AddWithValue("@id", G(fid)); cfq.Parameters.Add("@im", SqlDbType.TinyInt).Value = (byte)im;
                    cfq.Parameters.Add("@ml", SqlDbType.SmallInt).Value = (short)(int)f["maxLength"]; cfq.Parameters.Add("@dp", SqlDbType.TinyInt).Value = (byte)(int)f["decimalPlaces"];
                    cfq.Parameters.AddWithValue("@hr", (bool)f["hasRange"]); cfq.Parameters.AddWithValue("@rmin", (double)f["rangeMin"]); cfq.Parameters.AddWithValue("@rmax", (double)f["rangeMax"]);
                    cfq.Parameters.AddWithValue("@de", Db((string)f["description"]));
                    if (cfq.ExecuteNonQuery() != 1) throw new Exception("SYS_CustomFields: \"" + f["name"] + "\" nicht angelegt - zurueckgerollt, nichts geschrieben.");
                }
                // Kontrolle in derselben Transaktion (Schreib-Login: SELECT auf die drei Metadaten-Objekte)
                foreach (JObject f in fa)
                {
                    var k = wc.CreateCommand(); k.Transaction = tx;
                    k.CommandText = "SELECT (SELECT COUNT(*) FROM SYS_CustomFields WHERE CustomFieldId=@id), (SELECT COUNT(*) FROM VW_SYS_DISPLAYFIELDS WHERE FieldId=@id AND TABLENAME=@tn AND FIELDNAME=@fn)";
                    k.Parameters.AddWithValue("@id", G((string)f["id"])); k.Parameters.AddWithValue("@tn", tname); k.Parameters.AddWithValue("@fn", (string)f["column"]);
                    using (var r = k.ExecuteReader())
                    {
                        r.Read();
                        if (r.GetInt32(0) != 1 || r.GetInt32(1) != 1) throw new Exception("\"" + f["name"] + "\": Metadaten nach dem Schreiben nicht vollstaendig (SYS_CustomFields " + r.GetInt32(0) + ", VW_SYS_DISPLAYFIELDS " + r.GetInt32(1) + ") - zurueckgerollt, nichts geschrieben.");
                    }
                }
                if (isNew)
                {
                    var k = wc.CreateCommand(); k.Transaction = tx;
                    k.CommandText = "SELECT COUNT(*) FROM SYS_TableRelations WHERE TableID=@id AND IsCustomTable=1"; k.Parameters.AddWithValue("@id", G(tid));
                    if (Convert.ToInt32(k.ExecuteScalar()) != 1) throw new Exception("SYS_TableRelations nach dem Schreiben nicht wie geplant - zurueckgerollt, nichts geschrieben.");
                }
                tx.Commit();
            }
            // Kontrolle nach dem Schreiben mit dem Lese-Login: Spalten und Felder sichtbar
            var types = SqlTypes(rc);
            List<CfMeta> all = LoadFields(rc);
            var problems = new List<string>();
            foreach (JObject f in fa)
            {
                string st; string key = tname + "." + (string)f["column"], want = (string)f["sqlType"];
                if (!types.TryGetValue(key, out st)) problems.Add("Spalte " + key + " fehlt");
                else if (!string.Equals(st, want, StringComparison.OrdinalIgnoreCase)) problems.Add("Spalte " + key + " hat Typ " + st + " statt " + want);
                if (!all.Exists(m => string.Equals(m.FieldId, (string)f["id"], StringComparison.OrdinalIgnoreCase) && m.CaptionDe == (string)f["name"])) problems.Add("Feld \"" + f["name"] + "\" nicht in VW_SYS_CustomFields");
            }
            var o = new JObject();
            o["ok"] = problems.Count == 0;
            o["tabelle"] = caption; o["tableId"] = tid; o["tabellenname"] = tname; o["neueTabelle"] = isNew; o["ordner"] = tj["folderPath"];
            var res = new JArray();
            foreach (JObject f in fa) { var x = new JObject(); x["name"] = f["name"]; x["id"] = f["id"]; x["spalte"] = f["column"]; x["typ"] = f["sqlType"]; x["variable"] = f["variableName"]; res.Add(x); }
            o["felder"] = res;
            if (problems.Count > 0) o["probleme"] = new JArray(problems.ToArray());
            o["sicherung"] = Path.GetFileName(backup);
            o["hinweis"] = "In der Console: individuelle Felder einmal oeffnen bzw. aktualisieren. Im Client Command: Schritt \"Wert auf Server speichern\" mit fields {\"CustomField\":\"<Name>\",\"Table\":\"" + caption + "\",\"Value\":\"%VAR%\"}. " +
                           "Ob der ACMP-Server das Feld ohne Neustart kennt, zeigt der erste Lauf (acmp_cf_list field=<Name> values=true).";
            _log("acmp_cf_create von " + requester + ": " + (isNew ? "neue Tabelle \"" + caption + "\" " + tname + ", " : "Tabelle \"" + caption + "\", ") + fa.Count + " Feld(er)" + (problems.Count > 0 ? " - PROBLEME: " + string.Join("; ", problems.ToArray()) : "") + ", Sicherung " + Path.GetFileName(backup));
            return o;
        }
    }
}
