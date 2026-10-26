// AcmpContainerFilter: Container-Filter lesen (acmp_container_filter) und setzen (acmp_container_set_filter), Ruecknahme ueber acmp_job_restore.
// Grundlage: SQL-Mitschnitt 23.10.2026:
// Beim Speichern im Reiter "Dyn. Filter" schreibt der ACMP-Server fuer die Console
//  - UPDATE CLT_CONTAINER_STRUCTURE (alle Spalten; geaendert SQLFilter, HasClientCondition 1/0, ClientCondition 0x)
//  - Upsert SYS_Queries_FilterContainer (Id = ContainerID): Expression (= SQLFilter), RootGroup, FilterItems
//  - wie bei jedem Container-Speichern den Testring in SYS_Jobs/SYS_Jobs_Logs (unveraendert, der MCP laesst das weg)
// Die Mitglieder rechnet die Console nicht neu: der ACMP-Server wertet SQLFilter je Client beim Agentenkontakt und bei der Container-Neuberechnung
// aus (im Mitschnitt 7 Minuten nach dem Speichern: RecalcCache, MERGE in CLT_Container_Items; nur IsDynamic-Mitglieder fallen weg).
// Das SQL baut AcmpFilterSql wie die Console (an allen Filtern einer Produktiv-DB und mehreren Mitschnitt-Staenden zeichengleich). Der MCP schreibt nur, wenn
// er auch den jetzigen Filter des Containers zeichengleich nachbauen kann, und fuehrt den neuen Filter vorher lesend aus (Vorschau der Mitglieder).
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

partial class AcmpJobs
{
    public const int MaxFilterCriteria = 20, MaxFilterValues = 50, MaxFilterValueLen = 400, MaxPreviewNames = 60;
    public static readonly int[] TextFieldTypes = { 1, 5 }, NumberFieldTypes = { 0, 10 };
    public const int BoolFieldType = 8, IpFieldType = 3;
    static FilterCatalog _fcat; static DateTime _fcatAt; static readonly object _fcatLock = new object();

    public static readonly string FilterHelp =
        "filter = {\"join\":\"and|or|exceptAnd|exceptOr\", \"criteria\":[{\"field\":\"Computer Name\", \"op\":\"notLike\", \"value\":\"PC-%\"}, ...], " +
        "\"group\":{\"join\":\"and\", \"criteria\":[...]}} - field = Anzeigename (deutsch/englisch), Tabelle.Feld oder FieldId; op: like, notLike, equal, notEqual, in, notIn (Text, " +
        "mit values:[...] bei in/notIn), isTrue/isFalse (Ja/Nein), greater, smallerOrEqual (Zahl), like (IP wie 10.0.0.%). id = vorhandenes Kriterium (aus acmp_container_filter) behalten. " +
        "Hoechstens eine Untergruppe. Am einfachsten: acmp_container_filter lesen, 'spec' aendern und als filter uebergeben.";

    // ================= Feldkatalog =================
    FilterCatalog Catalog(SqlConnection c)
    {
        lock (_fcatLock)
        {
            if (_fcat != null && (DateTime.UtcNow - _fcatAt).TotalMinutes < 30) return _fcat;
            var cat = new FilterCatalog();
            var q = Cmd(c, "SELECT CAST(d.FieldID AS varchar(36)), d.TABLENAME, d.FIELDNAME, d.FIELDTYPE, ISNULL(d.VIRTUAL, 0), CAST(d.TableID AS varchar(36)), l.Caption_de, l.Caption_en " +
                           "FROM SYS_DISPLAYFIELDS d LEFT JOIN SYS_DisplayFields_Localization l ON l.FieldId = d.FieldID WHERE d.IntSupportedQueryBases & 1 = 1");
            using (var r = q.ExecuteReader())
                while (r.Read())
                {
                    var f = new FilterField();
                    f.Id = AcmpFilterSql.G(Str(r, 0)); f.Table = Str(r, 1); f.Field = Str(r, 2); f.Type = Int(r, 3); f.Virtual = Int(r, 4) == 1;
                    f.TableId = AcmpFilterSql.G(Str(r, 5)); f.CaptionDe = Str(r, 6); f.CaptionEn = Str(r, 7);
                    if (f.Id != null && f.Table != null && f.Field != null) cat.Fields[f.Id] = f;
                }
            var tq = Cmd(c, "SELECT CAST(TableID AS varchar(36)), TABLENAME, FOREIGN_TABLE, FOREIGN_KEY, ISNULL(IsCustomTable, 0), TableDisplayName FROM SYS_TableRelations");
            using (var r = tq.ExecuteReader())
                while (r.Read())
                {
                    var t = new FilterTable();
                    t.Id = AcmpFilterSql.G(Str(r, 0)); t.Name = Str(r, 1); t.ForeignTable = Str(r, 2); t.ForeignKey = Str(r, 3); t.Custom = Int(r, 4) == 1; t.DisplayName = Str(r, 5);
                    if (t.Id != null) cat.Tables[t.Id] = t;
                }
            _fcat = cat; _fcatAt = DateTime.UtcNow;
            return cat;
        }
    }

    static bool In(int[] a, int v) { return Array.IndexOf(a, v) >= 0; }

    static string TypeText(int t)
    {
        if (In(TextFieldTypes, t)) return "Text";
        if (t == BoolFieldType) return "Ja/Nein";
        if (In(NumberFieldTypes, t)) return "Zahl";
        if (t == IpFieldType) return "IP";
        return "Typ " + t + " (nur Console)";
    }

    static string OpsFor(int t)
    {
        if (In(TextFieldTypes, t)) return "like, notLike, equal, notEqual, in, notIn";
        if (t == BoolFieldType) return "isTrue, isFalse";
        if (In(NumberFieldTypes, t)) return "greater, smallerOrEqual";
        if (t == IpFieldType) return "like";
        return "-";
    }

    // Feld aus Angabe: FieldId, Tabelle.Feld oder Anzeigename (deutsch/englisch), jeweils ohne Gross/Klein
    static FilterField ResolveField(FilterCatalog cat, string s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0) throw new Exception("field fehlt. " + FilterHelp);
        string g = AcmpFilterSql.G(s);
        if (g != null) { FilterField f = cat.Field(g); if (f == null) throw new Exception("Feld " + g + " gibt es nicht (oder es ist fuer Client-Filter nicht freigegeben)."); return f; }
        var hits = new List<FilterField>();
        foreach (FilterField f in cat.Fields.Values)
            if (string.Equals(f.Table + "." + f.Field, s, StringComparison.OrdinalIgnoreCase)) hits.Add(f);
        if (hits.Count == 0)
            foreach (FilterField f in cat.Fields.Values)
                if (string.Equals(f.CaptionDe, s, StringComparison.OrdinalIgnoreCase) || string.Equals(f.CaptionEn, s, StringComparison.OrdinalIgnoreCase)) hits.Add(f);
        if (hits.Count == 1) return hits[0];
        if (hits.Count == 0) throw new Exception("Feld \"" + s + "\" nicht gefunden - acmp_container_filter mit fields=\"" + s + "\" zeigt passende Felder.");
        var l = new List<string>(); foreach (FilterField f in hits) { if (l.Count < 10) l.Add(f.Label + " " + f.Id); }
        throw new Exception("Feld \"" + s + "\" ist nicht eindeutig (" + hits.Count + "): " + string.Join("; ", l.ToArray()) + " - bitte Tabelle.Feld oder FieldId angeben.");
    }

    // ================= Zustand eines Containers =================
    // hasCond, sql, cc (Base64 von ClientCondition, null = NULL), kind (FilterKind), fcExists, expr, rg, fi
    static JObject ReadFilterState(SqlConnection c, SqlTransaction tx, string cid, bool lockRows)
    {
        string hint = lockRows ? " WITH (UPDLOCK, HOLDLOCK)" : "";
        var q = c.CreateCommand(); q.Transaction = tx; q.CommandTimeout = SqlTimeout;
        q.CommandText = "SELECT s.HasClientCondition, s.SQLFilter, s.ClientCondition, s.FilterKind, s.Path, s.Name, s.OsFamily, s.ClientRestriction FROM CLT_CONTAINER_STRUCTURE s" + hint + " WHERE s.ContainerID = @c";
        q.Parameters.AddWithValue("@c", new Guid(cid.Trim('{', '}')));
        var o = new JObject();
        using (var r = q.ExecuteReader())
        {
            if (!r.Read()) return null;
            o["hasCond"] = Int(r, 0); o["sql"] = Nv(Str(r, 1));
            o["cc"] = r.IsDBNull(2) ? JValue.CreateNull() : (JToken)Convert.ToBase64String((byte[])r.GetValue(2));
            o["kind"] = Int(r, 3); o["path"] = Str(r, 4); o["name"] = Str(r, 5); o["osFamily"] = Int(r, 6); o["restriction"] = Int(r, 7);
        }
        var f = c.CreateCommand(); f.Transaction = tx; f.CommandTimeout = SqlTimeout;
        f.CommandText = "SELECT Expression, CAST(RootGroup AS nvarchar(max)), CAST(FilterItems AS nvarchar(max)) FROM SYS_Queries_FilterContainer" + hint + " WHERE Id = @c";
        f.Parameters.AddWithValue("@c", new Guid(cid.Trim('{', '}')));
        o["fcExists"] = false;
        using (var r = f.ExecuteReader())
            if (r.Read()) { o["fcExists"] = true; o["expr"] = Nv(Str(r, 0)); o["rg"] = Nv(Str(r, 1)); o["fi"] = Nv(Str(r, 2)); }
        return o;
    }

    // NULL der DB als echtes JSON-null (ein JValue((string)null) hat Typ String und wird nach dem Weg durch die Sicherungsdatei zu Null)
    public static JToken Nv(string s) { return s == null ? JValue.CreateNull() : (JToken)s; }

    static string StateVal(JToken t)
    {
        if (t == null || t.Type == JTokenType.Null) return null;
        JValue v = t as JValue;
        if (v != null && v.Value == null) return null;
        return t.ToString();
    }

    // Gleicher Filter-Stand? (frisch gelesen und aus der Sicherungsdatei geparst vergleichbar; NULL und '' sind verschieden)
    public static bool SameState(JObject a, JObject b)
    {
        foreach (string k in new[] { "hasCond", "sql", "cc", "kind", "fcExists", "expr", "rg", "fi" })
            if (StateVal(a[k]) != StateVal(b[k])) return false;
        return true;
    }

    static FilterModel ModelOf(JObject st)
    {
        return (bool)st["fcExists"] ? AcmpFilterSql.Parse((string)st["rg"], (string)st["fi"]) : new FilterModel();
    }

    // Kann der MCP den gespeicherten Filter zeichengleich nachbauen? null = ja (oder kein Filter), sonst Grund
    static string RebuildProblem(JObject st, FilterModel m, FilterCatalog cat)
    {
        int kind = (int)st["kind"];
        if ((int)st["hasCond"] < 0) return "HasClientCondition ist NULL";
        if (kind != 3) return "Filtermodus " + (kind == 2 ? "'Erweiterter SQL-Filter'" : kind == 1 ? "'Baumfilter (veraltet)'" : kind.ToString()) + " (der MCP kennt nur den Standard-Filter)";
        string sql = (string)st["sql"] ?? "";
        bool has = (int)st["hasCond"] == 1;
        if (!(bool)st["fcExists"]) return has || sql.Length > 0 ? "Filter ohne Zeile in SYS_Queries_FilterContainer" : null;
        if (((string)st["expr"] ?? "") != sql) return "SQLFilter und Expression weichen voneinander ab";
        if (AcmpFilterSql.IsEmpty(m)) return has || sql.Length > 0 ? "leere Filter-Definition, aber SQL gesetzt" : null;
        if (!has) return "Filter-Definition vorhanden, aber HasClientCondition = 0";
        string gen;
        try { gen = AcmpFilterSql.Generate(m, cat); }
        catch (FilterNotSupportedException ex) { return "Form wird nicht nachgebaut: " + ex.Message; }
        if (gen != sql) return "der nachgebaute SQL-Text weicht vom gespeicherten ab (unbekannte Form)";
        return null;
    }

    // ================= Lesen: acmp_container_filter =================
    public JObject ContainerFilter(JObject a)
    {
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            FilterCatalog cat = Catalog(c);
            if (IsStr(a["fields"]) && !IsStr(a["containerId"]) && !IsStr(a["container"]) && !IsStr(a["jobId"])) return FieldSearch(cat, (string)a["fields"]);
            string cid = ResolveContainer(c, a, true);
            JObject st = ReadFilterState(c, null, cid, false);
            if (st == null) throw new Exception("Container " + cid + " gibt es nicht.");
            FilterModel m = ModelOf(st);
            var o = new JObject();
            o["container"] = st["path"]; o["containerId"] = cid;
            o["filtermodus"] = (int)st["kind"] == 3 ? "Standard-Filter" : (int)st["kind"] == 2 ? "Erweiterter SQL-Filter" : "FilterKind " + st["kind"];
            o["betriebssystemfamilie"] = OsFamilyText((int)st["osFamily"]); o["clientBeschraenkung"] = RestrictionText((int)st["restriction"]);
            o["hatFilter"] = (int)st["hasCond"] == 1;
            o["filter"] = new JArray(AcmpFilterSql.Lines(m, cat).ToArray());
            if (!AcmpFilterSql.IsEmpty(m)) o["spec"] = Spec(m, cat);
            string why = RebuildProblem(st, m, cat);
            o["aenderbar"] = why == null;
            if (why != null) o["grund"] = why + " - Aenderungen bitte in der Console.";
            int members = 0, dyn = 0;
            var mq = Cmd(c, "SELECT ci.IsDynamic FROM CLT_CONTAINER_ITEMS ci JOIN CLT_CLIENTS_TABLE cl ON cl.CLIENTID = ci.LinkID WHERE ci.CONTAINERID = @c AND cl.IsRetired = 0");
            mq.Parameters.AddWithValue("@c", new Guid(cid.Trim('{', '}')));
            using (var r = mq.ExecuteReader()) while (r.Read()) { members++; if (Int(r, 0) == 1) dyn++; }
            o["mitglieder"] = members + " (davon " + dyn + " ueber den Filter, " + (members - dyn) + " statisch)";
            string sql = (string)st["sql"];
            if ((int)st["hasCond"] == 1 && !string.IsNullOrEmpty(sql))
            {
                try
                {
                    List<string[]> hit = EvalFilter(c, sql);
                    o["filterTrifftJetzt"] = hit.Count;
                    if ((int)st["restriction"] == 1)
                    {
                        // Die Neuberechnung des Servers nimmt nur Clients der Betriebssystemfamilie auf (belegt 23.10.2026: Linux-Clients, die der Filter traf, wurden keine Mitglieder)
                        int fam = (int)st["osFamily"], fit = 0;
                        foreach (string[] h in hit) if ((h[2] == "1") == (fam == 1)) fit++;
                        o["nachClientBeschraenkung"] = fit;
                    }
                    if (BoolArg(a, "members", false)) o["treffer"] = NameArray(hit, MaxPreviewNames * 5);
                }
                catch (Exception ex) { o["filterTrifftJetzt"] = "nicht auswertbar: " + ex.Message; }
            }
            if (BoolArg(a, "members", false)) o["mitgliederListe"] = NameArray(Members(c, cid), MaxPreviewNames * 5);
            if (BoolArg(a, "sql", false)) o["sql"] = sql;
            if (IsStr(a["fields"])) o["felder"] = FieldSearch(cat, (string)a["fields"])["felder"];
            o["hinweis"] = "Aendern mit acmp_container_set_filter (filter = spec, geaendert). Der ACMP-Server uebernimmt die Mitglieder bei der naechsten Container-Neuberechnung bzw. je Client beim Agentenkontakt.";
            return o;
        }
    }

    static JObject FieldSearch(FilterCatalog cat, string s)
    {
        s = s.Trim();
        var l = new List<FilterField>();
        foreach (FilterField f in cat.Fields.Values)
        {
            string hay = (f.CaptionDe ?? "") + "|" + (f.CaptionEn ?? "") + "|" + f.Table + "." + f.Field;
            if (hay.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) l.Add(f);
        }
        l.Sort((x, y) => string.Compare(x.Label, y.Label, StringComparison.OrdinalIgnoreCase));
        var arr = new JArray();
        foreach (FilterField f in l)
        {
            if (arr.Count >= 40) break;
            var o = new JObject();
            o["field"] = f.CaptionDe ?? f.CaptionEn ?? f.Field; o["tabelleFeld"] = f.Table + "." + f.Field; o["fieldId"] = f.Id;
            o["typ"] = TypeText(f.Type); o["operatoren"] = OpsFor(f.Type);
            arr.Add(o);
        }
        var r = new JObject(); r["suche"] = s; r["treffer"] = l.Count; r["felder"] = arr;
        if (l.Count > 40) r["hinweis"] = "Nur die ersten 40 - genauer suchen.";
        return r;
    }

    static JObject ItemSpec(FilterItem it, FilterCatalog cat)
    {
        var o = new JObject();
        FilterField f = cat.Field(it.FieldId);
        o["id"] = it.FilterId.Trim('{', '}').Substring(0, 8);
        o["field"] = f != null ? (f.CaptionDe ?? f.CaptionEn ?? f.Field) : it.FieldId;
        if (f != null) o["tabelleFeld"] = f.Table + "." + f.Field;
        o["op"] = OpName(it);
        if (it.Class == "TQueryIPFilterData") o["value"] = it.Ip1;
        else if (it.Class == "TQueryDateFilterData") o["value"] = it.Operator == "qdoLastXDays" ? "letzte " + it.DayRange + " Tage" : "(Datum)";
        else if (it.Operator == "qsoIN" || it.Operator == "qsoNotIN") o["values"] = new JArray(it.Values.ToArray());
        else if (it.Class != "TQueryBooleanFilterClass") o["value"] = it.Value;
        return o;
    }

    // Angabe beschreibt das vorhandene Kriterium unveraendert (gleicher op, gleiche Werte wie in 'spec') - dann bleibt es roh, auch bei Typen,
    // die der MCP nicht neu anlegt (Datum)
    static bool SpecUnchanged(JObject cr, FilterItem old, FilterCatalog cat)
    {
        JObject s = ItemSpec(old, cat);
        if (OpKey((string)s["op"]) != OpKey(IsStr(cr["op"]) ? (string)cr["op"] : null)) return false;
        if (!JToken.DeepEquals(s["value"] ?? JValue.CreateNull(), cr["value"] ?? JValue.CreateNull()) && !(s["value"] == null && IsNullTok(cr["value"]))) return false;
        if (!JToken.DeepEquals(s["values"] ?? JValue.CreateNull(), cr["values"] ?? JValue.CreateNull()) && !(s["values"] == null && IsNullTok(cr["values"]))) return false;
        string fs = IsStr(cr["field"]) ? (string)cr["field"] : (IsStr(cr["fieldId"]) ? (string)cr["fieldId"] : null);
        if (fs == null) return false;
        try { return string.Equals(ResolveField(cat, fs).Id, old.FieldId, StringComparison.OrdinalIgnoreCase); } catch { return string.Equals(fs, (string)s["field"], StringComparison.OrdinalIgnoreCase); }
    }

    static string OpName(FilterItem it)
    {
        switch (it.Operator ?? "")
        {
            case "qsoLike": case "qioLike": return "like";
            case "qsoNotLike": return "notLike";
            case "qsoEqual": return "equal";
            case "qsoNotEqual": return "notEqual";
            case "qsoIN": return "in";
            case "qsoNotIN": return "notIn";
            case "qnoGreater": return "greater";
            case "qnoSmallerOrEqual": return "smallerOrEqual";
        }
        if (it.Class == "TQueryBooleanFilterClass") return it.Value == "true" ? "isTrue" : "isFalse";
        return it.Operator ?? "?";
    }

    static JObject Spec(FilterModel m, FilterCatalog cat)
    {
        JObject s = SpecGroup(m, m.Root, cat);
        List<FilterGroup> subs = AcmpFilterSql.SubGroups(m.Root);
        if (subs.Count == 1) s["group"] = SpecGroup(m, subs[0], cat);
        else if (subs.Count > 1) s["groups"] = subs.Count + " Untergruppen (nur in der Console aenderbar)";
        return s;
    }

    static JObject SpecGroup(FilterModel m, FilterGroup g, FilterCatalog cat)
    {
        var s = new JObject();
        s["join"] = g.Join == "jkAnd" ? "and" : g.Join == "jkOr" ? "or" : g.Join == "jkExceptAnd" ? "exceptAnd" : g.Join == "jkExceptOr" ? "exceptOr" : g.Join;
        var arr = new JArray();
        foreach (FilterItem it in AcmpFilterSql.DirectItems(m, g)) arr.Add(ItemSpec(it, cat));
        s["criteria"] = arr;
        return s;
    }

    // ================= Auswerten (nur lesend) =================
    // Treffer eines Filters: ClientId + Computername, ohne ausgemusterte Clients (wie die Neuberechnung des Servers: CLT_Clients_Retired)
    List<string[]> EvalFilter(SqlConnection c, string sql)
    {
        // Datumskriterien: der Server setzt :Now_n zur Laufzeit (heute als Delphi-Datum)
        string s = System.Text.RegularExpressions.Regex.Replace(sql, @":Now_\d+", "CAST(FLOOR(CAST(GETDATE() AS float)) + 2 AS int)");
        var q = Cmd(c, "SELECT CAST(CLT_Clients.ClientId AS varchar(36)), t.COMPUTERNAME, CASE WHEN EXISTS (SELECT 1 FROM CLT_UnixClients u WHERE u.ClientId = CLT_Clients.ClientId) THEN 1 ELSE 0 END " +
                       "FROM CLT_Clients WITH (READUNCOMMITTED) JOIN CLT_CLIENTS_TABLE t ON t.CLIENTID = CLT_Clients.ClientId " +
                       "WHERE t.IsRetired = 0 AND NOT EXISTS (SELECT 1 FROM CLT_Clients_Retired x WHERE x.ClientId = CLT_Clients.ClientId) AND " + s);
        var l = new List<string[]>();
        using (var r = q.ExecuteReader()) while (r.Read()) l.Add(new[] { Gid(r, 0), Str(r, 1) ?? "?", Int(r, 2).ToString(CultureInfo.InvariantCulture) });
        return l;
    }

    // Mitglieder: ClientId, Computername, IsDynamic
    List<string[]> Members(SqlConnection c, string cid)
    {
        var q = Cmd(c, "SELECT CAST(ci.LinkID AS varchar(36)), cl.COMPUTERNAME, ci.IsDynamic FROM CLT_CONTAINER_ITEMS ci JOIN CLT_CLIENTS_TABLE cl ON cl.CLIENTID = ci.LinkID " +
                       "WHERE ci.CONTAINERID = @c AND cl.IsRetired = 0 ORDER BY cl.COMPUTERNAME");
        q.Parameters.AddWithValue("@c", new Guid(cid.Trim('{', '}')));
        var l = new List<string[]>();
        using (var r = q.ExecuteReader()) while (r.Read()) l.Add(new[] { Gid(r, 0), Str(r, 1) ?? "?", Int(r, 2).ToString(CultureInfo.InvariantCulture) });
        return l;
    }

    static JArray NameArray(List<string[]> l, int max)
    {
        var n = new List<string>(); foreach (string[] x in l) n.Add(x[1]);
        n.Sort(StringComparer.OrdinalIgnoreCase);
        var a = new JArray(); foreach (string s in n) { if (a.Count >= max) { a.Add("... (" + (n.Count - max) + " weitere)"); break; } a.Add(s); }
        return a;
    }

    static string NameList(List<string> n)
    {
        n.Sort(StringComparer.OrdinalIgnoreCase);
        if (n.Count <= MaxPreviewNames) return string.Join(", ", n.ToArray());
        return string.Join(", ", n.GetRange(0, MaxPreviewNames).ToArray()) + " ... (" + (n.Count - MaxPreviewNames) + " weitere)";
    }

    // ================= Neuer Filter aus der Angabe =================
    static string JoinOf(JToken t, string where)
    {
        string s = IsStr(t) ? ((string)t).Trim().ToLowerInvariant().Replace(" ", "").Replace("(", "").Replace(")", "").Replace("ß", "ss") : "and";
        switch (s)
        {
            case "and": case "und": case "jkand": return "jkAnd";
            case "or": case "oder": case "jkor": return "jkOr";
            case "exceptand": case "ausserund": case "jkexceptand": return "jkExceptAnd";
            case "exceptor": case "ausseroder": case "jkexceptor": return "jkExceptOr";
        }
        throw new Exception(where + ": join \"" + t + "\" unbekannt (and, or, exceptAnd, exceptOr).");
    }

    static string OpKey(string s)
    {
        s = (s ?? "").Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("ä", "ae");
        switch (s)
        {
            case "like": case "aehnlich": return "like";
            case "notlike": case "nichtaehnlich": return "notlike";
            case "equal": case "eq": case "=": case "gleich": return "equal";
            case "notequal": case "ne": case "<>": case "!=": case "nichtgleich": return "notequal";
            case "in": return "in";
            case "notin": case "nichtin": return "notin";
            case "istrue": case "true": case "ja": return "istrue";
            case "isfalse": case "false": case "nein": return "isfalse";
            case "greater": case "gt": case ">": return "greater";
            case "smallerorequal": case "le": case "<=": return "smallerorequal";
        }
        return s;
    }

    static string CheckValue(string v, string where)
    {
        if (v == null) throw new Exception(where + ": value fehlt.");
        if (v.Trim().Length == 0) throw new Exception(where + ": leerer Wert.");
        if (v.Length > MaxFilterValueLen) throw new Exception(where + ": Wert laenger als " + MaxFilterValueLen + " Zeichen.");
        if (v.IndexOf('\'') >= 0) throw new Exception(where + ": Hochkomma (') im Wert - wie die Console es maskiert, ist nicht belegt; bitte in der Console.");
        if (v.IndexOf(":Now_", StringComparison.OrdinalIgnoreCase) >= 0) throw new Exception(where + ": \":Now_\" im Wert ist ein Platzhalter des ACMP-Servers - nicht erlaubt.");
        foreach (char ch in v) if (char.IsControl(ch)) throw new Exception(where + ": Steuerzeichen im Wert.");
        try { System.Xml.XmlConvert.VerifyXmlChars(v); } catch (System.Xml.XmlException) { throw new Exception(where + ": Zeichen, die in XML nicht erlaubt sind."); }
        return v;
    }

    // Ein Kriterium der Angabe -> FilterItem (ohne Ids)
    static FilterItem BuildItem(JObject cr, FilterCatalog cat, string where)
    {
        FilterField f = ResolveField(cat, IsStr(cr["field"]) ? (string)cr["field"] : (IsStr(cr["fieldId"]) ? (string)cr["fieldId"] : null));
        string op = OpKey(IsStr(cr["op"]) ? (string)cr["op"] : null);
        var it = new FilterItem(); it.FieldId = f.Id;
        where += " (" + f.Label + ")";
        if (In(TextFieldTypes, f.Type))
        {
            it.Class = "TQueryStringFilterData"; it.FilterType = "qfString";
            if (op == "in" || op == "notin")
            {
                JArray vals = cr["values"] as JArray;
                if (vals == null && IsStr(cr["value"])) vals = new JArray((string)cr["value"]);
                if (vals == null || vals.Count == 0) throw new Exception(where + ": op " + op + " braucht values:[...].");
                if (vals.Count > MaxFilterValues) throw new Exception(where + ": hoechstens " + MaxFilterValues + " Werte.");
                foreach (JToken v in vals) it.Values.Add(CheckValue(v.Type == JTokenType.String ? (string)v : null, where));
                it.Operator = op == "in" ? "qsoIN" : "qsoNotIN"; it.Value = "";
            }
            else
            {
                it.Operator = op == "like" ? "qsoLike" : op == "notlike" ? "qsoNotLike" : op == "equal" ? "qsoEqual" : op == "notequal" ? "qsoNotEqual" : null;
                if (it.Operator == null) throw new Exception(where + ": op \"" + cr["op"] + "\" passt nicht zu einem Textfeld (" + OpsFor(f.Type) + ").");
                it.Value = CheckValue(IsStr(cr["value"]) ? (string)cr["value"] : (cr["value"] != null && cr["value"].Type != JTokenType.Null ? cr["value"].ToString() : null), where);
            }
        }
        else if (f.Type == BoolFieldType)
        {
            it.Class = "TQueryBooleanFilterClass"; it.FilterType = "qfBoolean";
            if (op == "equal" && cr["value"] != null) op = BoolArg(cr, "value", true) ? "istrue" : "isfalse";
            if (op != "istrue" && op != "isfalse") throw new Exception(where + ": Ja/Nein-Feld braucht op isTrue oder isFalse.");
            it.Value = op == "istrue" ? "true" : "false";
        }
        else if (In(NumberFieldTypes, f.Type))
        {
            it.Class = "TQueryNumberFilterData"; it.FilterType = "qfNumber";
            it.Operator = op == "greater" ? "qnoGreater" : op == "smallerorequal" ? "qnoSmallerOrEqual" : null;
            if (it.Operator == null) throw new Exception(where + ": bei Zahlen sind nur greater und smallerOrEqual belegt.");
            int n;
            string v = cr["value"] != null ? cr["value"].ToString().Trim() : "";
            if (!int.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n)) throw new Exception(where + ": Wert muss eine ganze Zahl sein.");
            it.Value = n.ToString(CultureInfo.InvariantCulture);
        }
        else if (f.Type == IpFieldType)
        {
            it.Class = "TQueryIPFilterData"; it.FilterType = "qfIP";
            if (op != "like") throw new Exception(where + ": bei IP-Feldern ist nur like belegt (z.B. 10.0.0.%).");
            it.Operator = "qioLike"; it.Ip1 = CheckValue(IsStr(cr["value"]) ? ((string)cr["value"]).Trim() : null, where);
            try { AcmpFilterSql.IpPattern(it.Ip1); } catch (FilterNotSupportedException ex) { throw new Exception(where + ": " + ex.Message); }
        }
        else throw new Exception(where + ": Feldtyp " + TypeText(f.Type) + " kann der MCP nicht als neues Kriterium anlegen - bitte in der Console.");
        return it;
    }

    // Angabe -> Modell; vorhandene Kriterien (gleicher Inhalt bzw. id) behalten ihre Ids
    static FilterModel BuildModel(JObject spec, FilterModel cur, FilterCatalog cat, string cid, List<string> newIds, List<string> lines)
    {
        var m = new FilterModel();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        m.Root.Join = JoinOf(spec["join"], "filter");
        int total = 0;
        FillGroup(m, m.Root, spec, cur, cat, cid, used, newIds, lines, "filter", ref total);
        JObject gs = spec["group"] as JObject;
        if (spec["groups"] != null) throw new Exception("Mehrere Untergruppen kann der MCP nicht anlegen (die Console baut dafuer ein anderes SQL) - bitte in der Console.");
        if (gs != null)
        {
            var sub = new FilterGroup();
            List<FilterGroup> old = AcmpFilterSql.SubGroups(cur.Root);
            if (old.Count == 1) { sub.Id = old[0].Id; sub.Action = "eaEdit"; } else { sub.Id = AcmpFilterSql.NewId(); sub.Action = "eaAdd"; }
            sub.Join = JoinOf(gs["join"], "group");
            FillGroup(m, sub, gs, cur, cat, cid, used, newIds, lines, "group", ref total);
            if (AcmpFilterSql.DirectItems(m, sub).Count == 0) throw new Exception("group ohne criteria.");
            m.Root.Children.Add(sub);
        }
        if (AcmpFilterSql.DirectItems(m, m.Root).Count == 0) throw new Exception("filter.criteria ist leer (zum Entfernen des Filters clear=true). " + FilterHelp);
        if (total > MaxFilterCriteria) throw new Exception("Hoechstens " + MaxFilterCriteria + " Kriterien.");
        return m;
    }

    static void FillGroup(FilterModel m, FilterGroup g, JObject spec, FilterModel cur, FilterCatalog cat, string cid, HashSet<string> used, List<string> newIds, List<string> lines, string where, ref int total)
    {
        JArray crit = spec["criteria"] as JArray;
        if (crit == null) throw new Exception(where + ".criteria fehlt. " + FilterHelp);
        int i = 0;
        foreach (JToken t in crit)
        {
            JObject cr = t as JObject;
            string w = where + ".criteria[" + i++ + "]";
            if (cr == null) throw new Exception(w + " ist kein Objekt. " + FilterHelp);
            total++;
            FilterItem old = null;
            if (IsStr(cr["id"]))
            {
                string p = ((string)cr["id"]).Trim().Trim('{', '}').ToUpperInvariant();
                if (p.Length < 8) throw new Exception(w + ": id \"" + cr["id"] + "\" zu kurz (mindestens 8 Zeichen aus acmp_container_filter).");
                foreach (string id in cur.ItemOrder) if (id.Trim('{').StartsWith(p, StringComparison.OrdinalIgnoreCase) && !used.Contains(id)) { old = cur.Items[id]; break; }
                if (old == null) throw new Exception(w + ": Kriterium id " + cr["id"] + " gibt es im Filter nicht (oder doppelt angegeben).");
                if (SpecUnchanged(cr, old, cat))
                {
                    used.Add(old.FilterId);
                    var keep0 = Clone(old); keep0.Action = "eaNone";
                    lines.Add("   = " + AcmpFilterSql.ItemText(keep0, cat));
                    m.Items[keep0.FilterId] = keep0; m.ItemOrder.Add(keep0.FilterId); g.Children.Add(keep0.FilterId);
                    continue;
                }
            }
            FilterItem it = BuildItem(cr, cat, w);
            if (old == null)
                foreach (string id in cur.ItemOrder) if (!used.Contains(id) && AcmpFilterSql.SameContent(cur.Items[id], it)) { old = cur.Items[id]; break; }
            if (old != null)
            {
                used.Add(old.FilterId);
                if (AcmpFilterSql.SameContent(old, it)) { var keep = Clone(old); keep.Action = "eaNone"; it = keep; lines.Add("   = " + AcmpFilterSql.ItemText(it, cat)); }
                else { it.FilterId = old.FilterId; it.QueryId = old.QueryId; it.Action = "eaEdit"; lines.Add("   ~ " + AcmpFilterSql.ItemText(old, cat) + "  ->  " + AcmpFilterSql.ItemText(it, cat)); }
            }
            else
            {
                it.FilterId = AcmpFilterSql.NewId(); it.QueryId = cid; it.Action = "eaAdd"; newIds.Add(it.FilterId);
                lines.Add("   + " + AcmpFilterSql.ItemText(it, cat));
            }
            if (m.Items.ContainsKey(it.FilterId)) throw new Exception(w + ": Kriterium doppelt.");
            m.Items[it.FilterId] = it; m.ItemOrder.Add(it.FilterId); g.Children.Add(it.FilterId);
        }
    }

    static bool Referenced(FilterGroup g, string id)
    {
        foreach (object o in g.Children)
        {
            string s = o as string;
            if (s != null ? string.Equals(s, id, StringComparison.OrdinalIgnoreCase) : Referenced((FilterGroup)o, id)) return true;
        }
        return false;
    }

    static FilterItem Clone(FilterItem o)
    {
        var c = new FilterItem();
        c.FilterId = o.FilterId; c.QueryId = o.QueryId; c.FieldId = o.FieldId; c.Action = o.Action; c.Class = o.Class; c.FilterType = o.FilterType; c.Operator = o.Operator;
        c.Value = o.Value; c.Ip1 = o.Ip1; c.Ip2 = o.Ip2; c.Values.AddRange(o.Values); c.IsNot = o.IsNot; c.IsDynamic = o.IsDynamic; c.DayRange = o.DayRange; c.RawXml = o.RawXml;
        return c;
    }

    public const string ComputerNameFieldId = "{84666CA3-E04B-432E-800B-6F7FAF6E1A1A}";   // Computer Name (VW_CLT_Clients_Table.COMPUTERNAME)

    // excludeComputers: jetziger Filter + je Rechner ein Kriterium auf den Computernamen. Bei AUSSER (ODER) "Computer Name gleich X" (faellt unter die Ausnahme),
    // bei UND "Computer Name nicht gleich X". Bei ODER bzw. AUSSER (UND) ginge es nur mit einer Untergruppe - dann bitte filter angeben.
    JObject ExcludeSpec(SqlConnection c, JObject a, FilterModel cur, FilterCatalog cat)
    {
        JToken t = a["excludeComputers"];
        JArray names = t as JArray;
        if (names == null && t != null && t.Type == JTokenType.String) names = new JArray(((string)t).Trim());
        if (names == null || names.Count == 0) throw new Exception("excludeComputers: Liste von Computernamen, z.B. [\"PC-0815\"].");
        if (names.Count > 10) throw new Exception("excludeComputers: hoechstens 10 Rechner je Auftrag.");
        if (AcmpFilterSql.IsEmpty(cur)) throw new Exception("excludeComputers braucht einen vorhandenen Filter - der Container hat keinen. Bitte filter angeben.");
        string j = cur.Root.Join;
        if (j != "jkExceptOr" && j != "jkAnd")
            throw new Exception("excludeComputers geht bei " + AcmpFilterSql.JoinText(j) + " nicht ohne Untergruppe - bitte den ganzen Filter mit filter angeben (acmp_container_filter zeigt 'spec').");
        if (cat.Field(ComputerNameFieldId) == null) throw new Exception("Feld Computer Name (" + ComputerNameFieldId + ") fehlt im Feldkatalog.");
        JObject spec = Spec(cur, cat);
        JArray crit = (JArray)spec["criteria"];
        foreach (JToken n in names)
        {
            string name = n.Type == JTokenType.String ? ((string)n).Trim() : "";
            if (name.Length == 0) throw new Exception("excludeComputers: leerer Name.");
            var q = Cmd(c, "SELECT COUNT(*) FROM CLT_CLIENTS_TABLE WHERE COMPUTERNAME = @n AND IsRetired = 0");
            q.Parameters.AddWithValue("@n", name);
            if (Convert.ToInt32(q.ExecuteScalar()) == 0) throw new Exception("excludeComputers: Rechner \"" + name + "\" gibt es in ACMP nicht (oder er ist ausgemustert).");
            var cr = new JObject();
            cr["field"] = ComputerNameFieldId; cr["op"] = j == "jkExceptOr" ? "equal" : "notEqual"; cr["value"] = name;
            crit.Add(cr);
        }
        return spec;
    }

    static JObject SpecArg(JObject a)
    {
        JToken t = a["filter"];
        if (t != null && t.Type == JTokenType.String)
        {
            try { t = JToken.Parse((string)t); } catch (Exception ex) { throw new Exception("filter ist kein gueltiges JSON: " + ex.Message); }
        }
        JObject o = t as JObject;
        if (o == null) throw new Exception("filter fehlt. " + FilterHelp);
        return o;
    }

    // ================= Planen: acmp_container_set_filter =================
    JObject PlanFilter(JObject a)
    {
        bool clear = BoolArg(a, "clear", false);
        bool hasFilter = a["filter"] != null && a["filter"].Type != JTokenType.Null, hasEx = a["excludeComputers"] != null && a["excludeComputers"].Type != JTokenType.Null;
        if ((clear ? 1 : 0) + (hasFilter ? 1 : 0) + (hasEx ? 1 : 0) != 1) throw new Exception("Genau eines angeben: filter (ganzer neuer Filter), excludeComputers (Rechner aus dem jetzigen Filter ausschliessen) oder clear=true.");
        var plan = new JObject(); var pv = new JArray();
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            FilterCatalog cat = Catalog(c);
            string cid = ResolveContainer(c, a, false);
            JObject st = ReadFilterState(c, null, cid, false);
            if (st == null) throw new Exception("Container " + cid + " gibt es nicht - nichts geplant.");
            FilterModel cur = ModelOf(st);
            string why = RebuildProblem(st, cur, cat);
            if (why != null) throw new Exception("Container " + st["path"] + ": " + why + ". Der MCP aendert nur Filter, die er zeichengleich nachbauen kann - bitte in der Console. Nichts geplant.");
            var lines = new List<string>(); var newIds = new List<string>();
            JObject after = new JObject();
            FilterModel nm;
            if (clear)
            {
                if (AcmpFilterSql.IsEmpty(cur)) throw new Exception("Nichts zu tun: " + st["path"] + " hat keinen Filter.");
                nm = new FilterModel(); nm.Root.Join = cur.Root.Join;
                after["hasCond"] = 0; after["sql"] = "";
            }
            else
            {
                nm = BuildModel(hasEx ? ExcludeSpec(c, a, cur, cat) : SpecArg(a), cur, cat, cid, newIds, lines);
                foreach (string id in cur.ItemOrder)
                    if (!nm.Items.ContainsKey(id) && Referenced(cur.Root, id)) lines.Add("   - " + AcmpFilterSql.ItemText(cur.Items[id], cat));
                string sql;
                try { sql = AcmpFilterSql.Generate(nm, cat); }
                catch (FilterNotSupportedException ex) { throw new Exception("Diese Filterform baut der MCP nicht nach (" + ex.Message + ") - bitte in der Console. Nichts geplant."); }
                after["hasCond"] = 1; after["sql"] = sql;
            }
            after["cc"] = ""; after["kind"] = 3; after["fcExists"] = true; after["expr"] = after["sql"];
            after["rg"] = AcmpFilterSql.RootGroupXml(nm, newIds);
            after["fi"] = AcmpFilterSql.FilterItemsXml(nm, nm.ItemOrder);
            if ((string)after["sql"] == ((string)st["sql"] ?? "") && (bool)st["fcExists"] && (string)after["rg"] == (string)st["rg"] && (string)after["fi"] == (string)st["fi"])
                throw new Exception("Nichts zu tun: der Filter von " + st["path"] + " ist schon so.");
            bool sameSql = (string)after["sql"] == ((string)st["sql"] ?? "");
            bool itemsChanged = false; foreach (string l in lines) if (l.StartsWith("   +") || l.StartsWith("   ~") || l.StartsWith("   -")) itemsChanged = true;
            if (sameSql && !itemsChanged && !clear) throw new Exception("Nichts zu tun: der Filter von " + st["path"] + " ist inhaltlich schon so (gleiche Kriterien, gleiches SQL).");
            // Vorschau der Mitglieder: neuer Filter lesend ausgefuehrt (gleiche Auswertung wie die Neuberechnung des Servers)
            List<string[]> hits = new List<string[]>();
            if (!clear)
            {
                try { hits = EvalFilter(c, (string)after["sql"]); }
                catch (Exception ex) { throw new Exception("Der neue Filter laesst sich nicht ausfuehren (" + ex.Message + ") - nichts geplant."); }
            }
            List<string[]> mem = Members(c, cid);
            var hitIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase); foreach (string[] h in hits) hitIds.Add(h[0]);
            var memIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase); foreach (string[] x in mem) memIds.Add(x[0]);
            var add = new List<string>(); var drop = new List<string>(); var odd = new List<string>();
            int fam = (int)st["osFamily"]; bool restr = (int)st["restriction"] == 1;
            foreach (string[] h in hits)
            {
                bool unix = h[2] == "1";
                if (restr && (fam == 0 ? unix : !unix)) { odd.Add(h[1]); continue; }
                if (!memIds.Contains(h[0])) add.Add(h[1]);
            }
            foreach (string[] x in mem) if (x[2] == "1" && !hitIds.Contains(x[0])) drop.Add(x[1]);
            int stat = 0; foreach (string[] x in mem) if (x[2] != "1") stat++;
            var jobs = new List<string>();
            var jq = Cmd(c, "SELECT Name FROM SYS_Jobs WHERE ContainerId = @c AND IsManualJob = 0 ORDER BY Name");
            jq.Parameters.AddWithValue("@c", new Guid(cid.Trim('{', '}')));
            using (var r = jq.ExecuteReader()) while (r.Read()) jobs.Add(Str(r, 0) ?? "?");

            plan["action"] = "filter"; plan["containerId"] = cid; plan["path"] = st["path"];
            plan["before"] = st; plan["after"] = after;
            plan["treffer"] = hits.Count; plan["neuDazu"] = add.Count; plan["fallenWeg"] = drop.Count; plan["dauerauftraege"] = jobs.Count;
            plan["filterNeu"] = new JArray(AcmpFilterSql.Lines(nm, cat).ToArray());
            plan["sqlNeu"] = after["sql"];
            pv.Add("Filter von " + st["path"] + " (" + mem.Count + " Mitglieder, davon " + stat + " statisch) " + (clear ? "ENTFERNEN" : "SETZEN") +
                   " wie die Console (SQLFilter + SYS_Queries_FilterContainer, eine Transaktion, Sicherung vorher).");
            pv.Add("Bisher: " + string.Join(" | ", AcmpFilterSql.Lines(cur, cat).ToArray()));
            pv.Add("Neu: " + string.Join(" | ", AcmpFilterSql.Lines(nm, cat).ToArray()));
            foreach (string l in lines) pv.Add(l);
            if (sameSql) pv.Add("Das SQL bleibt gleich (nur die Darstellung der Kriterien aendert sich).");
            if (!string.IsNullOrEmpty((string)st["cc"])) pv.Add("Hinweis: alter Baumfilter-Inhalt in ClientCondition wird wie in der Console geleert (Sicherung enthaelt ihn).");
            if (!clear) pv.Add("Der neue Filter trifft jetzt " + hits.Count + " Client(s)" + (restr && odd.Count > 0 ? ", davon " + odd.Count + " nicht in der Betriebssystemfamilie " + OsFamilyText(fam) + " (Client-Beschraenkung, nicht aufgenommen): " + NameList(odd) : "") + ".");
            int dynNow = 0; foreach (string[] x in mem) if (x[2] == "1") dynNow++;
            if (!clear && hits.Count == 0) pv.Add("ACHTUNG: Der neue Filter trifft KEINEN Client" + (dynNow > 0 ? " - alle " + dynNow + " Filter-Mitglieder fallen weg." : "."));
            else if (dynNow > 0 && drop.Count == dynNow) pv.Add("ACHTUNG: Alle " + dynNow + " bisherigen Filter-Mitglieder fallen weg.");
            plan["warnung"] = (!clear && hits.Count == 0) || (dynNow > 0 && drop.Count == dynNow);
            pv.Add(add.Count == 0 ? "Neu dazu: keine." : "Neu dazu (" + add.Count + "): " + NameList(add));
            pv.Add(drop.Count == 0 ? "Fallen weg: keine." : "Fallen weg (" + drop.Count + "): " + NameList(drop));
            if (stat > 0) pv.Add(stat + " statische Mitglieder bleiben (die Neuberechnung entfernt nur Filter-Mitglieder).");
            if (jobs.Count > 0) pv.Add("ACHTUNG: " + jobs.Count + " Dauerauftrag/-auftraege am Container (" + string.Join(", ", jobs.ToArray()) + ") - neue Mitglieder bekommen sie nach der Neuberechnung" + (drop.Count > 0 ? ", weggefallene nicht mehr" : "") + ".");
            pv.Add("Wirkung: Der ACMP-Server uebernimmt die Mitglieder bei der naechsten Container-Neuberechnung (im Mitschnitt nach etwa 7 Minuten) bzw. je Client beim naechsten Agentenkontakt. Zurueck mit acmp_job_restore.");
        }
        plan["vorschau"] = pv;
        return plan;
    }

    // ================= Ausfuehren =================
    // Filter tauschen, wenn der Stand noch dem erwarteten entspricht (beide Zeilen gesperrt); danach Kontrolle
    static void SwapFilter(SqlConnection wc, SqlTransaction tx, string cid, JObject expect, JObject now, string label)
    {
        JObject cur = ReadFilterState(wc, tx, cid, true);
        if (cur == null) throw new Exception(label + " gibt es nicht mehr - zurueckgerollt, nichts geschrieben.");
        if (!SameState(cur, expect)) throw new Exception(label + ": Filter wurde seit der Planung geaendert - zurueckgerollt, nichts geschrieben. Bitte neu aufrufen.");
        Guid g = new Guid(cid.Trim('{', '}'));
        var uq = TxCmd(wc, tx, "UPDATE CLT_CONTAINER_STRUCTURE SET SQLFilter = @s, HasClientCondition = @h, ClientCondition = @cc WHERE ContainerID = @id");
        uq.Parameters.AddWithValue("@s", (string)now["sql"] ?? "");
        uq.Parameters.AddWithValue("@h", (byte)(int)now["hasCond"]);
        var ccp = new SqlParameter("@cc", SqlDbType.VarBinary, -1);
        ccp.Value = IsNullTok(now["cc"]) ? (object)DBNull.Value : Convert.FromBase64String((string)now["cc"]);
        uq.Parameters.Add(ccp); uq.Parameters.AddWithValue("@id", g);
        uq.ExecuteNonQuery();   // Zeilenzahl enthaelt die des Triggers TG_CLT_CONTAINER_STRUCTURE_UpdateTenantNo - Kontrolle unten
        if ((bool)now["fcExists"])
        {
            var fq = TxCmd(wc, tx, (bool)cur["fcExists"]
                ? "UPDATE SYS_Queries_FilterContainer SET Expression = @e, RootGroup = @rg, FilterItems = @fi WHERE Id = @id"
                : "INSERT INTO SYS_Queries_FilterContainer (Id, Expression, RootGroup, FilterItems) VALUES (@id, @e, @rg, @fi)");
            fq.Parameters.AddWithValue("@e", (object)(string)now["expr"] ?? DBNull.Value);
            fq.Parameters.Add(XmlPar("@rg", (string)now["rg"])); fq.Parameters.Add(XmlPar("@fi", (string)now["fi"]));
            fq.Parameters.AddWithValue("@id", g);
            if (fq.ExecuteNonQuery() != 1) throw new Exception(label + ": Filterzeile nicht eindeutig geschrieben - zurueckgerollt.");
        }
        else if ((bool)cur["fcExists"])
        {
            var dq = TxCmd(wc, tx, "DELETE FROM SYS_Queries_FilterContainer WHERE Id = @id");
            dq.Parameters.AddWithValue("@id", g);
            if (dq.ExecuteNonQuery() != 1) throw new Exception(label + ": Filterzeile nicht eindeutig entfernt - zurueckgerollt.");
        }
        JObject check = ReadFilterState(wc, tx, cid, false);
        if (!SameState(check, now)) throw new Exception(label + ": Kontrolle nach dem Schreiben fehlgeschlagen (SQL Server hat Werte anders gespeichert) - zurueckgerollt.");
    }

    JObject ExecFilter(JObject plan, string requester)
    {
        string cid = (string)plan["containerId"], label = "Container " + plan["path"];
        var b = new JObject();
        b["kind"] = "filter"; b["tool"] = "acmp_container_set_filter"; b["von"] = requester; b["zeit"] = DateTime.Now.ToString("s");
        b["containerId"] = cid; b["path"] = plan["path"]; b["before"] = plan["before"]; b["after"] = plan["after"];
        b["filterNeu"] = plan["filterNeu"];
        string bf = Path.GetFileName(SaveBackup(b, "filter", cid));
        using (var wc = new SqlConnection(_writeCs))
        {
            wc.Open();
            using (SqlTransaction tx = wc.BeginTransaction()) { SwapFilter(wc, tx, cid, (JObject)plan["before"], (JObject)plan["after"], label); tx.Commit(); }
        }
        _log("acmp_container_set_filter von " + requester + ": " + plan["path"] + " -> " + string.Join(" | ", ((JArray)plan["filterNeu"]).ToObject<string[]>()) + ", Sicherung " + bf);
        var res = new JObject();
        res["ok"] = true; res["aktion"] = (int)((JObject)plan["after"])["hasCond"] == 1 ? "Filter gesetzt" : "Filter entfernt"; res["container"] = plan["path"];
        res["filter"] = plan["filterNeu"]; res["trifftJetzt"] = plan["treffer"]; res["neuDazu"] = plan["neuDazu"]; res["fallenWeg"] = plan["fallenWeg"];
        if (plan["warnung"] != null && (bool)plan["warnung"]) res["warnung"] = "Der neue Filter trifft keinen Client bzw. alle bisherigen Filter-Mitglieder fallen weg.";
        res["sicherung"] = bf;
        res["zurueck"] = "acmp_job_restore mit backup=" + bf;
        res["hinweis"] = "Der ACMP-Server uebernimmt die Mitglieder bei der naechsten Container-Neuberechnung bzw. je Client beim Agentenkontakt. Pruefen: acmp_container_filter members=true (mitglieder/filterTrifftJetzt).";
        return res;
    }

    // Ruecknahme aus einer _filter_-Sicherung planen (aufgerufen von PlanRestore)
    JObject PlanRestoreFilter(JObject b, string name)
    {
        string cid = (string)b["containerId"];
        var plan = new JObject(); var pv = new JArray();
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            JObject st = ReadFilterState(c, null, cid, false);
            if (st == null) throw new Exception("Container " + cid + " gibt es nicht mehr - die Sicherung " + name + " laesst sich nicht zuruecknehmen.");
            if (!SameState(st, (JObject)b["after"])) throw new Exception(st["path"] + ": Filter wurde seit der Sicherung geaendert - nichts geplant. In der Console pruefen.");
            FilterCatalog cat = Catalog(c);
            JObject before = (JObject)b["before"];
            plan["action"] = "restore_filter"; plan["containerId"] = cid; plan["path"] = st["path"]; plan["current"] = st; plan["target"] = before;
            pv.Add("Filter von " + st["path"] + " ZURUECK auf den Stand vor " + b["zeit"] + " (Sicherung " + name + "):");
            pv.Add("   jetzt: " + string.Join(" | ", AcmpFilterSql.Lines(ModelOf(st), cat).ToArray()));
            pv.Add("   danach: " + string.Join(" | ", AcmpFilterSql.Lines(ModelOf(before), cat).ToArray()));
            pv.Add("Die Mitglieder uebernimmt der ACMP-Server wieder bei der naechsten Container-Neuberechnung.");
        }
        plan["vorschau"] = pv;
        return plan;
    }
}
