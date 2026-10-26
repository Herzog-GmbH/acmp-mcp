// AcmpFilterSql: Container-Filter (Console "Dyn. Filter", Filtermodus Standard-Filter) lesen, nachbauen und als SQL erzeugen - ohne DB, ohne JSON.
// Grundlage: SQL-Mitschnitt 23.10.2026 und die Filter einer Produktiv-DB.
// Die Console speichert je Container:
//  - CLT_CONTAINER_STRUCTURE: SQLFilter (= Expression), HasClientCondition 1/0, ClientCondition 0x, FilterKind 3 (alle Spalten, wie beim Container-Speichern)
//  - SYS_Queries_FilterContainer (Id = ContainerID): Expression, RootGroup (TQueryFilterGroup), FilterItems (TQueryFilterItemContainer)
//  Ein leerer Filter: HasClientCondition 0, SQLFilter/Expression '', RootGroup mit leerer Liste.
// Der ACMP-Server wertet nur SQLFilter aus: je Client beim Agentenkontakt und bei der Container-Neuberechnung
// (SELECT [ClientId] FROM ((<Teil nach "in (">)) T, danach MERGE in CLT_Container_Items).
// Der Generator bildet die Console nach fuer die belegten Formen: nur Kriterien in der obersten Gruppe (UND/ODER/AUSSER), dazu hoechstens EINE
// Untergruppe mit Kriterien (inline bei gleicher Tabelle, sonst exists/not exists). Mehrere Untergruppen unter ODER baut die Console voellig anders
// (je Gruppe ein eigener exists-Zweig, Mitschnitt 5) - das lehnt der Generator ab (Console benutzen).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Xml;

public class FilterField
{
    public string Id, Table, Field, CaptionDe, CaptionEn, TableId;
    public int Type;          // SYS_DISPLAYFIELDS.FIELDTYPE: 1 Text, 3 IP, 5 Text (Pfad), 6 Datum, 8 Ja/Nein, 0/10 Zahl
    public bool Virtual;      // Wert steht in der Fremdtabelle der Tabellen-Relation (z.B. CLT_SW_SETUP.NAME -> SYS_SW_SETUP.NAME)
    public string Label { get { return (CaptionDe ?? CaptionEn ?? Field) + " (" + Table + "." + Field + ")"; } }
}

public class FilterTable
{
    public string Id, Name, ForeignTable, ForeignKey, DisplayName;
    public bool Custom;       // Individuelle Felder (CF_VAL_...): die Console verknuepft immer ueber CLT_Clients
}

public class FilterCatalog
{
    public readonly Dictionary<string, FilterField> Fields = new Dictionary<string, FilterField>(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, FilterTable> Tables = new Dictionary<string, FilterTable>(StringComparer.OrdinalIgnoreCase);
    public FilterField Field(string id) { FilterField f; return id != null && Fields.TryGetValue(AcmpFilterSql.G(id), out f) ? f : null; }
    public FilterTable TableOf(FilterField f) { FilterTable t; return f != null && f.TableId != null && Tables.TryGetValue(AcmpFilterSql.G(f.TableId), out t) ? t : null; }
}

public class FilterItem
{
    public string FilterId, QueryId, FieldId, Action, Class, FilterType, Operator, Value, Ip1, Ip2;
    public List<string> Values = new List<string>();
    public bool IsNot, IsDynamic;
    public int DayRange;
    public string RawXml;     // Element wie gespeichert (fuer unveraenderte Kriterien)
}

public class FilterGroup
{
    public string Id;         // null = oberste Gruppe
    public string Join = "jkAnd", Action = "eaEdit";
    public readonly List<object> Children = new List<object>();   // FilterItem-Referenzen (string FilterId) oder FilterGroup
}

public class FilterModel
{
    public FilterGroup Root = new FilterGroup();
    public readonly Dictionary<string, FilterItem> Items = new Dictionary<string, FilterItem>(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> ItemOrder = new List<string>();  // Reihenfolge in FilterItems
}

public class FilterNotSupportedException : Exception { public FilterNotSupportedException(string m) : base(m) { } }

public static class AcmpFilterSql
{
    public const string NL = "\r\n";
    public static readonly string[] Joins = { "jkAnd", "jkOr", "jkExceptAnd", "jkExceptOr" };

    public static string G(string s)
    {
        if (s == null) return null;
        Guid g; return Guid.TryParse(s.Trim(), out g) ? "{" + g.ToString().ToUpperInvariant() + "}" : null;
    }

    public static string NewId() { return "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}"; }

    public static string JoinText(string j)
    {
        return j == "jkAnd" ? "UND" : j == "jkOr" ? "ODER" : j == "jkExceptAnd" ? "AUSSER (UND)" : j == "jkExceptOr" ? "AUSSER (ODER)" : j;
    }

    public static bool IsExcept(string j) { return j == "jkExceptAnd" || j == "jkExceptOr"; }
    static string Word(string j) { return j == "jkAnd" || j == "jkExceptAnd" ? "and" : "or"; }

    // ================= Lesen =================
    static string Kid(XmlElement e, string n) { XmlElement c = e[n]; return c == null ? null : c.InnerText; }

    // OuterXml schreibt leere Elemente als "<x />", SQL Server (Spalte xml) als "<x/>"; in Werten kann " />" nicht vorkommen (< ist &lt;)
    static string Outer(XmlElement e) { return e.OuterXml.Replace(" />", "/>"); }

    public static FilterModel Parse(string rootGroupXml, string filterItemsXml)
    {
        var m = new FilterModel();
        if (!string.IsNullOrEmpty(filterItemsXml))
        {
            var d = new XmlDocument(); d.PreserveWhitespace = true; d.LoadXml(filterItemsXml);
            XmlElement list = d.DocumentElement["TQueryFilterDataList"];
            if (list != null)
                foreach (XmlNode n in list.ChildNodes)
                {
                    XmlElement e = n as XmlElement; if (e == null) continue;
                    var it = new FilterItem();
                    it.Class = e.Name; it.RawXml = Outer(e);
                    it.FilterId = G(Kid(e, "FilterId")); it.QueryId = G(Kid(e, "QueryId")); it.FieldId = G(Kid(e, "FieldId"));
                    it.Action = Kid(e, "Action"); it.FilterType = Kid(e, "FilterType"); it.Operator = Kid(e, "Operator"); it.Value = Kid(e, "Value");
                    it.IsNot = Kid(e, "IsNot") == "true"; it.IsDynamic = Kid(e, "IsDynamic") == "true";
                    it.Ip1 = Kid(e, "IPString1"); it.Ip2 = Kid(e, "IPString2");
                    string dr = Kid(e, "DynamicDayRange"); int di; if (dr != null && int.TryParse(dr, NumberStyles.Integer, CultureInfo.InvariantCulture, out di)) it.DayRange = di;
                    XmlElement sv = e["SelectedValues"];
                    if (sv != null) foreach (XmlNode s in sv.ChildNodes) if (s is XmlElement) it.Values.Add(s.InnerText);
                    if (it.FilterId == null) throw new FilterNotSupportedException("Kriterium ohne FilterId");
                    m.Items[it.FilterId] = it; m.ItemOrder.Add(it.FilterId);
                }
        }
        if (!string.IsNullOrEmpty(rootGroupXml))
        {
            var d = new XmlDocument(); d.PreserveWhitespace = true; d.LoadXml(rootGroupXml);
            m.Root = ParseGroup(d.DocumentElement, true);
        }
        return m;
    }

    static FilterGroup ParseGroup(XmlElement e, bool root)
    {
        var g = new FilterGroup();
        g.Id = root ? null : G(Kid(e, "Id"));
        g.Join = Kid(e, "JoinKind") ?? "jkAnd"; g.Action = Kid(e, "Action") ?? "eaEdit";
        XmlElement list = e["TAcmpDataList"];
        if (list != null)
            foreach (XmlNode n in list.ChildNodes)
            {
                XmlElement c = n as XmlElement; if (c == null) continue;
                if (c.Name == "TQueryFilterItem") g.Children.Add(G(Kid(c, "Id")));
                else if (c.Name == "TQueryFilterGroup") g.Children.Add(ParseGroup(c, false));
            }
        return g;
    }

    public static List<FilterItem> DirectItems(FilterModel m, FilterGroup g)
    {
        var l = new List<FilterItem>();
        foreach (object o in g.Children)
        {
            string id = o as string; if (id == null) continue;
            FilterItem it; if (!m.Items.TryGetValue(id, out it)) throw new FilterNotSupportedException("Kriterium " + id + " fehlt in FilterItems");
            l.Add(it);
        }
        return l;
    }

    public static List<FilterGroup> SubGroups(FilterGroup g)
    {
        var l = new List<FilterGroup>();
        foreach (object o in g.Children) { FilterGroup s = o as FilterGroup; if (s != null) l.Add(s); }
        return l;
    }

    public static bool IsEmpty(FilterModel m) { return m.Root.Children.Count == 0; }

    // ================= Lesbar =================
    public static string OpText(FilterItem it)
    {
        switch (it.Operator ?? "")
        {
            case "qsoEqual": return "gleich";
            case "qsoNotEqual": return "nicht gleich";
            case "qsoLike": return "aehnlich";
            case "qsoNotLike": return "nicht aehnlich";
            case "qsoIN": return "in";
            case "qsoNotIN": return "nicht in";
            case "qnoGreater": return ">";
            case "qnoGreaterOrEqual": return ">=";
            case "qnoSmaller": return "<";
            case "qnoSmallerOrEqual": return "<=";
            case "qnoEqual": return "=";
            case "qnoNotEqual": return "<>";
            case "qioLike": return "aehnlich";
            case "qdoLastXDays": return "in den letzten";
        }
        if (it.Class == "TQueryBooleanFilterClass") return "ist";
        return it.Operator ?? "?";
    }

    public static string ValueText(FilterItem it)
    {
        if (it.Class == "TQueryBooleanFilterClass") return it.Value == "true" ? "ja" : "nein";
        if (it.Class == "TQueryIPFilterData") return "\"" + it.Ip1 + "\"" + (string.IsNullOrEmpty(it.Ip2) ? "" : " - \"" + it.Ip2 + "\"");
        if (it.Class == "TQueryDateFilterData") return it.Operator == "qdoLastXDays" ? it.DayRange + " Tagen" : "(Datum)";
        if (it.Operator == "qsoIN" || it.Operator == "qsoNotIN")
        {
            var q = new List<string>(); foreach (string v in it.Values) q.Add("\"" + v + "\"");
            return "(" + string.Join(", ", q.ToArray()) + ")";
        }
        return "\"" + it.Value + "\"";
    }

    public static string ItemText(FilterItem it, FilterCatalog cat)
    {
        FilterField f = cat != null ? cat.Field(it.FieldId) : null;
        return (f != null ? f.Label : "Feld " + it.FieldId) + " " + (it.IsNot ? "NICHT " : "") + OpText(it) + " " + ValueText(it);
    }

    public static List<string> Lines(FilterModel m, FilterCatalog cat)
    {
        var l = new List<string>();
        if (IsEmpty(m)) { l.Add("(kein Filter)"); return l; }
        AddLines(m, m.Root, cat, "", l);
        return l;
    }

    static void AddLines(FilterModel m, FilterGroup g, FilterCatalog cat, string ind, List<string> l)
    {
        l.Add(ind + JoinText(g.Join));
        foreach (object o in g.Children)
        {
            string id = o as string;
            if (id != null) { FilterItem it; l.Add(ind + "  " + (m.Items.TryGetValue(id, out it) ? ItemText(it, cat) : "(Kriterium " + id + " fehlt)")); }
            else AddLines(m, (FilterGroup)o, cat, ind + "  ", l);
        }
    }

    // ================= SQL wie die Console =================
    class Ctx
    {
        public FilterModel M; public FilterCatalog Cat;
        public readonly Dictionary<string, int> Alias = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public string Next(string table) { int n; Alias.TryGetValue(table, out n); n++; Alias[table] = n; return table + "_" + n; }
    }

    class Sel { public string Text, Alias; public List<string> Tables; }

    // Prueft die Form (null = unterstuetzt, sonst Grund) - ohne Felder aufzuloesen
    public static string ShapeProblem(FilterModel m)
    {
        if (IsEmpty(m)) return null;
        if (Array.IndexOf(Joins, m.Root.Join) < 0) return "unbekannte Verknuepfung " + m.Root.Join;
        List<FilterItem> items = DirectItems(m, m.Root);
        List<FilterGroup> subs = SubGroups(m.Root);
        if (items.Count == 0) return "oberste Gruppe ohne eigene Kriterien (nur Untergruppen)";
        if (subs.Count > 1) return subs.Count + " Untergruppen (der MCP baut hoechstens eine nach)";
        if (subs.Count == 1)
        {
            FilterGroup s = subs[0];
            if (Array.IndexOf(Joins, s.Join) < 0) return "unbekannte Verknuepfung " + s.Join;
            if (SubGroups(s).Count > 0) return "verschachtelte Untergruppen";
            if (DirectItems(m, s).Count == 0) return "leere Untergruppe";
        }
        return null;
    }

    public static string Generate(FilterModel m, FilterCatalog cat)
    {
        if (IsEmpty(m)) return "";
        string why = ShapeProblem(m);
        if (why != null) throw new FilterNotSupportedException(why);
        var ctx = new Ctx(); ctx.M = m; ctx.Cat = cat;
        List<FilterGroup> subs = SubGroups(m.Root);
        FilterGroup sub = subs.Count == 1 ? subs[0] : null;
        // CLT_Clients-Nummern: jede Gruppe belegt eine (oberste 1, Untergruppe 2), die AUSSER-Huelle der obersten Gruppe die naechste
        Sel s = Select(ctx, m.Root, sub, 1);
        if (!IsExcept(m.Root.Join)) return "[CLT_Clients].[ClientId] in (" + s.Text + NL + ")";
        string w = "CLT_Clients_" + (sub != null ? 3 : 2);
        return "[CLT_Clients].[ClientId] in (select [" + w + "].[ClientId] from CLT_Clients as [" + w + "] with (READUNCOMMITTED)" + NL +
               "where not exists(" + NL + s.Text + NL +
               "and (" + NL + "([" + w + "].[ClientId] = [" + s.Alias + "].[ClientId])" + NL + ")" + NL + ")" + NL + ")";
    }

    static List<string> TablesOf(Ctx ctx, List<FilterItem> items)
    {
        var l = new List<string>();
        foreach (FilterItem it in items)
        {
            FilterField f = FieldOf(ctx, it);
            bool seen = false; foreach (string t in l) if (string.Equals(t, f.Table, StringComparison.OrdinalIgnoreCase)) seen = true;
            if (!seen) l.Add(f.Table);
        }
        return l;
    }

    static FilterField FieldOf(Ctx ctx, FilterItem it)
    {
        FilterField f = ctx.Cat.Field(it.FieldId);
        if (f == null) throw new FilterNotSupportedException("Feld " + it.FieldId + " nicht im Feldkatalog (SYS_DISPLAYFIELDS)");
        if (!Ident(f.Table) || !Ident(f.Field)) throw new FilterNotSupportedException("Feld " + f.Label + ": Tabellen-/Feldname mit Sonderzeichen");
        if (f.Virtual)
        {
            FilterTable t = ctx.Cat.TableOf(f);
            if (t == null || string.IsNullOrEmpty(t.ForeignTable) || string.IsNullOrEmpty(t.ForeignKey))
                throw new FilterNotSupportedException("virtuelles Feld " + f.Label + " ohne Tabellen-Relation");
            if (!Ident(t.ForeignTable) || !Ident(t.ForeignKey)) throw new FilterNotSupportedException("virtuelles Feld " + f.Label + ": Relation mit Sonderzeichen");
        }
        return f;
    }

    // Tabellen- und Feldnamen kommen aus dem Katalog der DB und landen ohne Maskierung im SQL - nur Buchstaben, Ziffern, Unterstrich
    static bool Ident(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length > 128) return false;
        foreach (char ch in s) if (!(ch == '_' || (ch >= '0' && ch <= '9') || (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z'))) return false;
        return true;
    }

    static bool IsCustom(Ctx ctx, FilterItem it) { FilterTable t = ctx.Cat.TableOf(FieldOf(ctx, it)); return t != null && t.Custom; }

    static Sel Select(Ctx ctx, FilterGroup g, FilterGroup sub, int clt)
    {
        List<FilterItem> items = DirectItems(ctx.M, g);
        List<string> tables = TablesOf(ctx, items);
        bool custom = false; foreach (FilterItem it in items) if (IsCustom(ctx, it)) custom = true;
        bool direct = tables.Count == 1 && !custom;
        var alias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        string a;
        if (direct)
        {
            a = ctx.Next(tables[0]); alias[tables[0]] = a;
            sb.Append("select [" + a + "].[ClientId] from " + tables[0] + " as [" + a + "] with (READUNCOMMITTED)" + NL);
            sb.Append(VirtualJoins(ctx, items, tables[0], a));
        }
        else
        {
            a = "CLT_Clients_" + clt;
            sb.Append("select [" + a + "].[ClientId] from CLT_Clients as [" + a + "] with (READUNCOMMITTED)" + NL);
            foreach (string t in tables)
            {
                string ta = ctx.Next(t); alias[t] = ta;
                sb.Append("left join [" + t + "] as [" + ta + "] with (READUNCOMMITTED) on [" + ta + "].[ClientId] = [" + a + "].[ClientId]" + NL);
                sb.Append(VirtualJoins(ctx, items, t, ta));
            }
        }
        sb.Append("where (" + NL + CondLine(ctx, items, alias, g.Join) + NL);
        if (sub != null)
        {
            List<FilterItem> sitems = DirectItems(ctx.M, sub);
            List<string> stables = TablesOf(ctx, sitems);
            bool subset = true; foreach (string t in stables) if (!alias.ContainsKey(t)) subset = false;
            bool scustom = false; foreach (FilterItem it in sitems) if (IsCustom(ctx, it)) scustom = true;
            sb.Append(Word(g.Join) + "  (" + NL);
            if (subset && !scustom)
            {
                // gleiche Tabelle wie die obersten Kriterien: die Console schreibt die Untergruppe direkt hinein (belegt: UND in einer Tabelle)
                if (IsExcept(sub.Join) || !direct) throw new FilterNotSupportedException("Untergruppe " + JoinText(sub.Join) + " in derselben Tabelle wie die oberste Gruppe");
                sb.Append("(" + NL + CondLine(ctx, sitems, alias, sub.Join) + NL + ")");
            }
            else
            {
                bool overlap = false; foreach (string t in stables) if (alias.ContainsKey(t)) overlap = true;
                if (overlap) throw new FilterNotSupportedException("Untergruppe teilt Tabellen mit der obersten Gruppe");
                Sel s = Select(ctx, sub, null, 2);
                sb.Append((IsExcept(sub.Join) ? "not exists (" : "exists (") + NL + s.Text + NL +
                          "and (" + NL + "([" + a + "].[ClientId] = [" + s.Alias + "].[ClientId])" + NL + ")" + NL + ")");
            }
            sb.Append(NL + ")" + NL);
        }
        sb.Append(")");
        var r = new Sel(); r.Text = sb.ToString(); r.Alias = a; r.Tables = tables;
        return r;
    }

    static string VirtualJoins(Ctx ctx, List<FilterItem> items, string table, string alias)
    {
        foreach (FilterItem it in items)
        {
            FilterField f = FieldOf(ctx, it);
            if (!f.Virtual || !string.Equals(f.Table, table, StringComparison.OrdinalIgnoreCase)) continue;
            FilterTable t = ctx.Cat.TableOf(f);
            return "left join [" + t.ForeignTable + "] with (READUNCOMMITTED) on [" + t.ForeignTable + "].[" + t.ForeignKey + "] = [" + alias + "].[" + t.ForeignKey + "]" + NL;
        }
        return "";
    }

    static string CondLine(Ctx ctx, List<FilterItem> items, Dictionary<string, string> alias, string join)
    {
        var parts = new List<string>(); int dates = 0;
        foreach (FilterItem it in items)
        {
            FilterField f = FieldOf(ctx, it);
            string col = f.Virtual ? "[" + ctx.Cat.TableOf(f).ForeignTable + "].[" + f.Field + "]" : "[" + alias[f.Table] + "].[" + f.Field + "]";
            if (it.Class == "TQueryDateFilterData" && ++dates > 1) throw new FilterNotSupportedException("mehr als ein Datumskriterium");
            parts.Add("(" + Cond(it, col) + ")");
        }
        return string.Join(" " + Word(join) + " ", parts.ToArray()) + " ";
    }

    public static string Lit(string v) { return "N'" + (v ?? "").Replace("'", "''") + "'"; }

    static string Cond(FilterItem it, string col)
    {
        if (it.IsNot) throw new FilterNotSupportedException("Kriterium mit NICHT (IsNot)");
        if (it.IsDynamic) throw new FilterNotSupportedException("dynamisches Kriterium (IsDynamic)");
        switch (it.Class)
        {
            case "TQueryStringFilterData":
                switch (it.Operator)
                {
                    case "qsoLike": return col + " like " + Lit(it.Value);
                    case "qsoNotLike": return col + " not like " + Lit(it.Value);
                    case "qsoEqual": return col + " = " + Lit(it.Value);
                    case "qsoNotEqual": return col + " <> " + Lit(it.Value);
                    case "qsoIN":
                    case "qsoNotIN":
                        if (it.Values.Count == 0) throw new FilterNotSupportedException("'in' ohne Werte");
                        var q = new List<string>(); foreach (string v in it.Values) q.Add(Lit(v));
                        return col + (it.Operator == "qsoIN" ? " in (" : " not in (") + string.Join(", ", q.ToArray()) + ")";
                }
                break;
            case "TQueryBooleanFilterClass":
                if (it.Value == "true") return col + " = 1";
                if (it.Value == "false") return col + " <> 1";
                break;
            case "TQueryNumberFilterData":
                {
                    double d;
                    if (!double.TryParse(it.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) break;
                    string n = d.ToString("F2", CultureInfo.InvariantCulture);
                    if (it.Operator == "qnoGreater") return col + " > " + n;
                    if (it.Operator == "qnoSmallerOrEqual") return col + " <= " + n;
                    break;
                }
            case "TQueryIPFilterData":
                if (it.Operator == "qioLike") return col + " like '" + IpPattern(it.Ip1) + "'";
                break;
            case "TQueryDateFilterData":
                if (it.Operator == "qdoLastXDays") return "(cast(" + col + " as int) between :Now_1 - " + it.DayRange.ToString(CultureInfo.InvariantCulture) + " and :Now_2)";
                break;
        }
        throw new FilterNotSupportedException("Operator " + (it.Operator ?? "?") + " bei " + it.Class + " (nicht belegt)");
    }

    // "10.0.0.%" -> "010000000%" (je Zahl dreistellig, % bleibt)
    public static string IpPattern(string ip)
    {
        if (string.IsNullOrEmpty(ip)) throw new FilterNotSupportedException("IP-Kriterium ohne Wert");
        var sb = new StringBuilder();
        foreach (string p in ip.Split('.'))
        {
            int n;
            if (p == "%") sb.Append("%");
            else if (p.Length > 0 && p.Length <= 3 && int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out n) && n <= 255) sb.Append(n.ToString("000", CultureInfo.InvariantCulture));
            else throw new FilterNotSupportedException("IP-Muster \"" + ip + "\" (erlaubt: Zahlen 0-255 und %, z.B. 10.0.0.%)");
        }
        return sb.ToString();
    }

    // Teil nach "[CLT_Clients].[ClientId] in (" bis zur letzten Klammer - so wertet der ACMP-Server bei der Neuberechnung aus
    public static string Inner(string sql)
    {
        const string head = "[CLT_Clients].[ClientId] in (";
        if (string.IsNullOrEmpty(sql) || !sql.StartsWith(head) || !sql.EndsWith(")")) return null;
        return sql.Substring(head.Length, sql.Length - head.Length - 1);
    }

    // ================= XML wie die Console =================
    static string X(string s) { return SecurityElementEscape(s ?? ""); }
    static string SecurityElementEscape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) { if (c == '&') sb.Append("&amp;"); else if (c == '<') sb.Append("&lt;"); else if (c == '>') sb.Append("&gt;"); else sb.Append(c); }
        return sb.ToString();
    }

    static string GroupXml(FilterGroup g, bool root)
    {
        var sb = new StringBuilder();
        sb.Append("<TQueryFilterGroup>");
        sb.Append(root ? "<BaseTableName>CLT_Clients</BaseTableName><IdFieldName>ClientId</IdFieldName><Id/>" : "<BaseTableName/><IdFieldName/><Id>" + g.Id + "</Id>");
        sb.Append("<JoinKind>" + g.Join + "</JoinKind>");
        if (g.Children.Count == 0) sb.Append("<TAcmpDataList/>");
        else
        {
            sb.Append("<TAcmpDataList>");
            foreach (object o in g.Children)
            {
                FilterGroup s = o as FilterGroup;
                if (s != null) sb.Append(GroupXml(s, false));
                else { string id = (string)o; sb.Append("<TQueryFilterItem><Id>" + id + "</Id><Action>" + (RefAction(g, id) ?? "eaEdit") + "</Action></TQueryFilterItem>"); }
            }
            sb.Append("</TAcmpDataList>");
        }
        sb.Append("<Action>" + (!root && g.Action == "eaAdd" ? "eaAdd" : "eaEdit") + "</Action></TQueryFilterGroup>");   // neue Untergruppe eaAdd, sonst eaEdit (Console 6.10)
        return sb.ToString();
    }

    // Action der Referenz in der Gruppe: neue Kriterien eaAdd, vorhandene eaEdit (wie die Console)
    [ThreadStatic] static HashSet<string> _newRefs;
    static string RefAction(FilterGroup g, string id) { return _newRefs != null && _newRefs.Contains(id) ? "eaAdd" : "eaEdit"; }

    public static string RootGroupXml(FilterModel m, ICollection<string> newIds)
    {
        _newRefs = new HashSet<string>(newIds ?? new string[0], StringComparer.OrdinalIgnoreCase);
        try { return GroupXml(m.Root, true); } finally { _newRefs = null; }
    }

    public static string FilterItemsXml(FilterModel m, IEnumerable<string> ids)
    {
        var sb = new StringBuilder();
        var parts = new List<string>();
        foreach (string id in ids) parts.Add(ItemXml(m.Items[id]));
        sb.Append("<TQueryFilterItemContainer>");
        sb.Append(parts.Count == 0 ? "<TQueryFilterDataList/>" : "<TQueryFilterDataList>" + string.Join("", parts.ToArray()) + "</TQueryFilterDataList>");
        sb.Append("<Action>eaEdit</Action></TQueryFilterItemContainer>");
        return sb.ToString();
    }

    // Unveraenderte Kriterien: gespeichertes Element, nur Action ersetzt; neue/geaenderte: neu aufgebaut
    public static string ItemXml(FilterItem it)
    {
        if (it.RawXml != null)
        {
            var d = new XmlDocument(); d.PreserveWhitespace = true; d.LoadXml(it.RawXml);
            XmlElement a = d.DocumentElement["Action"];
            if (a != null) a.InnerText = it.Action ?? "eaNone";
            return Outer(d.DocumentElement);
        }
        var sb = new StringBuilder();
        sb.Append("<" + it.Class + "><QueryId>" + it.QueryId + "</QueryId><FilterId>" + it.FilterId + "</FilterId><FieldId>" + it.FieldId + "</FieldId>");
        sb.Append("<Action>" + it.Action + "</Action><IsNot>false</IsNot><IsDynamic>false</IsDynamic><FilterType>" + it.FilterType + "</FilterType>");
        if (it.Values.Count == 0) sb.Append("<SelectedValues/>");
        else { sb.Append("<SelectedValues>"); foreach (string v in it.Values) sb.Append("<string>" + X(v) + "</string>"); sb.Append("</SelectedValues>"); }
        switch (it.Class)
        {
            case "TQueryStringFilterData":
                sb.Append("<Operator>" + it.Operator + "</Operator>" + (string.IsNullOrEmpty(it.Value) ? "<Value/>" : "<Value>" + X(it.Value) + "</Value>")); break;
            case "TQueryBooleanFilterClass":
                sb.Append("<Value>" + it.Value + "</Value>"); break;
            case "TQueryNumberFilterData":
                sb.Append("<Operator>" + it.Operator + "</Operator><Value>" + X(it.Value) + "</Value><BetweenLeft>0</BetweenLeft><BetweenRight>0</BetweenRight>"); break;
            case "TQueryIPFilterData":
                sb.Append("<Operator>" + it.Operator + "</Operator><IPString1>" + X(it.Ip1) + "</IPString1><IPString2/>"); break;
            default: throw new FilterNotSupportedException("Kriterium " + it.Class + " kann der MCP nicht neu anlegen");
        }
        sb.Append("</" + it.Class + ">");
        return sb.ToString();
    }

    // Kriterium inhaltlich gleich? (Feld, Klasse, Operator, Werte, NICHT)
    public static bool SameContent(FilterItem a, FilterItem b)
    {
        if (a == null || b == null) return false;
        if (!string.Equals(a.FieldId, b.FieldId, StringComparison.OrdinalIgnoreCase) || a.Class != b.Class || (a.Operator ?? "") != (b.Operator ?? "") || a.IsNot != b.IsNot) return false;
        if (a.Class == "TQueryIPFilterData") return (a.Ip1 ?? "") == (b.Ip1 ?? "");
        if (a.Class == "TQueryDateFilterData") return a.DayRange == b.DayRange;
        if ((a.Value ?? "") != (b.Value ?? "")) return false;
        if (a.Values.Count != b.Values.Count) return false;
        for (int i = 0; i < a.Values.Count; i++) if (a.Values[i] != b.Values[i]) return false;
        return true;
    }
}
