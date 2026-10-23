// AcmpCcParams: Parameter eines Client Commands lesen und setzen (Console: Eigenschaften -> Client Command spezifisch -> Parameter) -
// acmp_cc_parameters (lesen), acmp_cc_set_parameters (setzen). Anlass: eine Ja/Nein-Auswahl als Parameter eines Pruef-Commands.
// Grundlage: SQL-Mitschnitt vom 22.10.2026 (ACMP 6.10). Der ACMP-Server schreibt je Parameter (Upsert, keine DDL):
//  - SYS_Jobs_Parameters_Declarations: Id, ContentId = ScriptId, Name = Projektvariable, Availability 0, Value = DefaultValue (sql_variant als
//    nvarchar), VersionStart, VersionEnd 2147483647, Option (Text 2, Combobox 2, Checkbox 1), Priority 1..n
//  - SYS_Jobs_Parameters_Config: Id = Id der Declaration, EditorType (0 Text, 1 Combobox, 2 Checkbox), Caption_en, Caption_de
//  - SYS_Jobs_Parameters_Config_Values (nur Combobox): Id, Value, ConfigId, IsDefault, Priority, Caption_en, Caption_de
//  Werte: Text = Standardtext, Checkbox = true/false, Combobox = "{GUID}" des Standard-Eintrags.
//  Id der Declaration (= Config-Id = ConfigId der Eintraege) bildet die Console selbst (TParameterDeclaration.RecalculateID, gefunden 22.10.2026,
//  200 von 200 DB-Zeilen bestaetigt): UUID v5, Namespace Aagon.Core.Uuid.Namespaces.NameSpace_Aagon_Default (AcmpCore.bpl), Name =
//  GROSS(Projektvariable) + "{SCRIPTID}" in UTF-8. Sie rechnet die Id beim Laden neu aus und sucht damit die Eintraege - ein Parameter mit
//  anderer Id (der MCP wuerfelte bis 22.10. v4) erscheint in Job-Dialog und Editor ohne Eintraege. Eintrags-Ids sind zufaellig (v4) wie in der Console.
//  Der Server liest die Parameter bei jeder Job-Auswertung frisch (kein Zwischenspeicher). Ein Parameterwert ueberschreibt beim Jobstart den Startwert
//  der Projektvariable - ein Schritt "Projektvariable setzen" im Skript ueberschreibt ihn danach wieder (die Vorschau warnt davor).
// Regeln: die Liste ersetzt alle Parameter des Commands (wie der Dialog der Console; Entfernen zeigt die Vorschau), nur schreibbare Projektvariablen
// der neuesten Version, je Variable ein Parameter, nicht in Aagon-Standardordnern (wie acmp_cc_new_version), erst dryRun (Vorschau),
// Plan mit dem Stand der Parameter (SHA-256), Sicherung vorher (cc-backups), eine Transaktion, Kontrolle in derselben Transaktion.
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

class CcParamEntry
{
    public string Id, Value, CaptionDe, CaptionEn;
    public bool IsDefault;
    public int Priority;
}

class CcParam
{
    public string Id, Name, Type, CaptionDe, CaptionEn, Value, DefaultValue;
    public int EditorType = -1, Option, Availability, VersionStart = 1, VersionEnd = int.MaxValue, Priority;
    public List<CcParamEntry> Entries = new List<CcParamEntry>();
}

partial class AcmpCcWriter
{
    public const int MaxParams = 20, MaxParamEntries = 50;
    public static readonly string[] ParamTables = { "SYS_Jobs_Parameters_Declarations", "SYS_Jobs_Parameters_Config", "SYS_Jobs_Parameters_Config_Values" };
    public static readonly string ParamsHelp =
        "Vollstaendige Liste der Parameter (ersetzt alle bisherigen, wie der Dialog 'Parameter' in der Console), hoechstens " + MaxParams + ": " +
        "[{\"variable\":\"VOLLSCAN\",\"type\":\"combobox\",\"caption\":\"Vollscan aller lokalen Festplatten\",\"captionEn\":\"Full scan of all local disks\"," +
        "\"entries\":[{\"value\":\"Ja\",\"caption\":\"Ja\",\"captionEn\":\"Yes\"},{\"value\":\"Nein\",\"caption\":\"Nein\",\"captionEn\":\"No\"}],\"default\":\"Ja\"}, " +
        "{\"variable\":\"NOTIZ\",\"type\":\"text\",\"caption\":\"Notiz\",\"default\":\"\"}, {\"variable\":\"SCHALTER\",\"type\":\"checkbox\",\"caption\":\"Schalter\",\"default\":true}]. " +
        "type: text, combobox oder checkbox; variable = schreibbare Projektvariable der neuesten Version (je Variable ein Parameter); caption Pflicht, captionEn optional.";

    JObject _paramPf; DateTime _paramPfAt;

    // ================= Hilfen ohne DB (offline getestet in CfParamTest.cs) =================

    static string PStr(JToken t) { return t != null && t.Type == JTokenType.String ? ((string)t).Trim() : ""; }
    static bool PNull(JToken t) { return t == null || t.Type == JTokenType.Null; }

    static readonly Guid ParamNamespace = new Guid("00B79CE3-4320-49B5-A569-23A7E8B05134");   // NameSpace_Aagon_Default

    // Belegt ist die Ableitung nur fuer Namen aus ASCII-Zeichen (Delphi-UpperCase aendert nur a-z)
    public static bool ParamIdKnown(string name)
    {
        foreach (char ch in name) if (ch < 0x20 || ch > 0x7E) return false;
        return name.Length > 0;
    }

    static string ParamIdFailText(string name)
    {
        return "Projektvariable \"" + name + "\" enthaelt Zeichen ausserhalb von ASCII - wie die Console daraus die Parameter-Id bildet, ist nicht belegt. Parameter in der ACMP-Console anlegen.";
    }

    // Id des Parameters wie die Console: UUID v5 (RFC 4122, SHA-1) ueber GROSS(Name) + "{SCRIPTID}"
    public static string ParamId(string scriptId, string name)
    {
        if (!ParamIdKnown(name)) throw new Exception(ParamIdFailText(name));
        var up = new StringBuilder(name.Length);
        foreach (char ch in name) up.Append(ch >= 'a' && ch <= 'z' ? (char)(ch - 32) : ch);
        string text = up.ToString() + "{" + new Guid(scriptId.Trim('{', '}')).ToString().ToUpperInvariant() + "}";
        byte[] ns = ParamNamespace.ToByteArray(); GuidNetworkOrder(ns);
        byte[] data = new byte[16 + Encoding.UTF8.GetByteCount(text)];
        Buffer.BlockCopy(ns, 0, data, 0, 16);
        Encoding.UTF8.GetBytes(text, 0, text.Length, data, 16);
        byte[] h; using (var sha = SHA1.Create()) h = sha.ComputeHash(data);
        var u = new byte[16]; Buffer.BlockCopy(h, 0, u, 0, 16);
        u[6] = (byte)((u[6] & 0x0F) | 0x50); u[8] = (byte)((u[8] & 0x3F) | 0x80);
        GuidNetworkOrder(u);
        return "{" + new Guid(u).ToString().ToUpperInvariant() + "}";
    }

    // .NET-Guid-Bytes <-> Netzwerk-Reihenfolge (die ersten drei Felder umdrehen; in beide Richtungen gleich)
    static void GuidNetworkOrder(byte[] b)
    {
        Array.Reverse(b, 0, 4); Array.Reverse(b, 4, 2); Array.Reverse(b, 6, 2);
    }

    // Hat der Parameter die Id, unter der die Console ihn sucht? (Namen ausserhalb ASCII: unbekannt -> true, nicht anfassen)
    public static bool ParamIdOk(string scriptId, CcParam p)
    {
        return !ParamIdKnown(p.Name) || string.Equals(p.Id, ParamId(scriptId, p.Name), StringComparison.OrdinalIgnoreCase);
    }

    // Vorhandene Parameter nach Variable; gibt es zu einem Namen mehrere Zeilen, gewinnt die mit der Console-Id
    public static Dictionary<string, CcParam> ParamsByName(string scriptId, List<CcParam> ps)
    {
        var d = new Dictionary<string, CcParam>(StringComparer.OrdinalIgnoreCase);
        foreach (CcParam p in ps)
        {
            CcParam cur;
            if (!d.TryGetValue(p.Name, out cur) || (!ParamIdOk(scriptId, cur) && ParamIdOk(scriptId, p))) d[p.Name] = p;
        }
        return d;
    }

    // Auftrag -> vollstaendige Parameterliste. existing: vorhandene Parameter nach Variable (VersionStart, Availability bleiben; Eintrags-Ids nur,
    // wenn der vorhandene schon die Console-Id hat); writable: Projektvariablen der neuesten Version ohne ReadOnly; fromVersion: VersionStart neuer
    // Parameter. Id = Console-Id (ParamId); nur bei Namen ausserhalb ASCII bleibt die Id eines vorhandenen Parameters, neue werden abgelehnt.
    public static List<CcParam> NormalizeParams(JToken input, string scriptId, ICollection<string> writable, Dictionary<string, CcParam> existing, int fromVersion, Func<string> newId)
    {
        JToken t = input;
        if (t != null && t.Type == JTokenType.String && ((string)t).Trim().StartsWith("["))
        {
            try { t = JToken.Parse((string)t); } catch (Exception ex) { throw new Exception("parameters ist kein gueltiges JSON: " + ex.Message); }
        }
        JArray arr = t as JArray;
        if (arr == null) throw new Exception("parameters fehlt (leere Liste [] entfernt alle Parameter): " + ParamsHelp);
        if (arr.Count > MaxParams) throw new Exception(arr.Count + " Parameter - hoechstens " + MaxParams + ".");
        var list = new List<CcParam>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int no = 0;
        foreach (JToken x in arr)
        {
            JObject o = x as JObject;
            string label = "parameters[" + no + "]";
            if (o == null) throw new Exception(label + " ist kein Objekt: " + ParamsHelp);
            string v = PStr(o["variable"]).Trim('%');
            if (v.Length == 0) throw new Exception(label + ": variable fehlt (Projektvariable ohne %).");
            string realName = null;
            foreach (string w in writable) if (string.Equals(w, v, StringComparison.OrdinalIgnoreCase)) realName = w;
            if (realName == null) throw new Exception(label + ": \"" + v + "\" ist keine schreibbare Projektvariable der neuesten Version (vorhanden: " + string.Join(", ", new List<string>(writable).ToArray()) + ").");
            if (!used.Add(realName)) throw new Exception(label + ": fuer \"" + realName + "\" gibt es schon einen Parameter - je Variable nur einer.");
            string type = PStr(o["type"]).ToLowerInvariant();
            if (type == "auswahl" || type == "combo" || type == "select") type = "combobox";
            if (type == "bool" || type == "boolean" || type == "kontrollkaestchen") type = "checkbox";
            if (type == "string" || type == "textfeld") type = "text";
            if (type != "text" && type != "combobox" && type != "checkbox") throw new Exception(label + " \"" + realName + "\": type muss text, combobox oder checkbox sein.");
            string cap = PStr(o["caption"]);
            if (cap.Length == 0) throw new Exception(label + " \"" + realName + "\": caption fehlt (Beschriftung im Job-Dialog und Kiosk).");
            string capEn = PStr(o["captionEn"]); if (capEn.Length == 0) capEn = cap;
            if (cap.Length > 255 || capEn.Length > 255) throw new Exception(label + " \"" + realName + "\": caption/captionEn hoechstens 255 Zeichen.");
            var p = new CcParam();
            CcParam old; existing.TryGetValue(realName, out old);
            if (ParamIdKnown(realName)) p.Id = ParamId(scriptId, realName);
            else if (old != null) p.Id = old.Id;
            else throw new Exception(label + ": " + ParamIdFailText(realName));
            bool sameId = old != null && string.Equals(old.Id, p.Id, StringComparison.OrdinalIgnoreCase);
            p.Name = realName; p.Type = type; p.CaptionDe = cap; p.CaptionEn = capEn;
            p.Availability = old != null ? old.Availability : 0;
            p.VersionStart = old != null ? old.VersionStart : fromVersion; p.VersionEnd = int.MaxValue;
            p.Priority = ++no;
            if (type == "text")
            {
                p.EditorType = 0; p.Option = 2;
                if (o["entries"] != null) throw new Exception(label + " \"" + realName + "\": entries gibt es nur bei combobox.");
                string d = PNull(o["default"]) ? "" : o["default"].Type == JTokenType.String ? (string)o["default"] : o["default"].ToString(Newtonsoft.Json.Formatting.None);
                if (d.Length > 4000) throw new Exception(label + " \"" + realName + "\": default hoechstens 4000 Zeichen.");
                p.Value = p.DefaultValue = d;
            }
            else if (type == "checkbox")
            {
                p.EditorType = 2; p.Option = 1;
                if (o["entries"] != null) throw new Exception(label + " \"" + realName + "\": entries gibt es nur bei combobox.");
                bool b = false;
                JToken d = o["default"];
                if (!PNull(d))
                {
                    if (d.Type == JTokenType.Boolean) b = (bool)d;
                    else
                    {
                        string s = d.ToString().Trim().ToLowerInvariant();
                        if (s == "true" || s == "1" || s == "ja" || s == "yes") b = true;
                        else if (s == "false" || s == "0" || s == "nein" || s == "no" || s == "") b = false;
                        else throw new Exception(label + " \"" + realName + "\": default bei checkbox ist true oder false.");
                    }
                }
                p.Value = p.DefaultValue = b ? "true" : "false";
            }
            else
            {
                p.EditorType = 1; p.Option = 2;
                JArray en = o["entries"] as JArray;
                if (en == null || en.Count == 0) throw new Exception(label + " \"" + realName + "\": entries fehlt (Liste der Auswahlwerte).");
                if (en.Count > MaxParamEntries) throw new Exception(label + " \"" + realName + "\": hoechstens " + MaxParamEntries + " Eintraege.");
                var oldByValue = new Dictionary<string, CcParamEntry>(StringComparer.Ordinal);
                if (sameId && old.EditorType == 1) foreach (CcParamEntry oe in old.Entries) oldByValue[oe.Value] = oe;
                var values = new HashSet<string>(StringComparer.Ordinal);
                int ep = 0;
                foreach (JToken et in en)
                {
                    JObject eo = et as JObject;
                    string val = eo != null ? (eo["value"] != null && eo["value"].Type != JTokenType.Null ? eo["value"].ToString() : "") : et.ToString();
                    if (et.Type == JTokenType.String) val = (string)et;
                    if (val.Length == 0) throw new Exception(label + " \"" + realName + "\": Eintrag " + (ep + 1) + " ohne value.");
                    if (val.Length > 255) throw new Exception(label + " \"" + realName + "\": value \"" + val.Substring(0, 30) + "...\" laenger als 255 Zeichen.");
                    if (!values.Add(val)) throw new Exception(label + " \"" + realName + "\": value \"" + val + "\" doppelt.");
                    var e = new CcParamEntry();
                    CcParamEntry oe; oldByValue.TryGetValue(val, out oe);
                    e.Id = oe != null ? oe.Id : newId();
                    e.Value = val;
                    e.CaptionDe = eo != null && PStr(eo["caption"]).Length > 0 ? PStr(eo["caption"]) : val;
                    e.CaptionEn = eo != null && PStr(eo["captionEn"]).Length > 0 ? PStr(eo["captionEn"]) : e.CaptionDe;
                    if (e.CaptionDe.Length > 255 || e.CaptionEn.Length > 255) throw new Exception(label + " \"" + realName + "\": Beschriftung eines Eintrags hoechstens 255 Zeichen.");
                    e.Priority = ++ep;
                    p.Entries.Add(e);
                }
                string def = PNull(o["default"]) ? p.Entries[0].Value : o["default"].ToString();
                CcParamEntry de = p.Entries.Find(e => e.Value == def);
                if (de == null) throw new Exception(label + " \"" + realName + "\": default \"" + def + "\" ist keiner der Eintraege.");
                foreach (CcParamEntry e in p.Entries) e.IsDefault = e == de;
                p.Value = p.DefaultValue = de.Id;
            }
            list.Add(p);
        }
        return list;
    }

    // Vergleichbarer Text eines Parametersatzes (Plan einfrieren, Kontrolle nach dem Schreiben)
    public static string ParamKey(List<CcParam> ps)
    {
        var sorted = new List<CcParam>(ps);
        sorted.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
        var sb = new StringBuilder();
        foreach (CcParam p in sorted)
        {
            sb.Append(p.Id.ToUpperInvariant()).Append('|').Append(p.Name).Append('|').Append(p.Availability).Append('|').Append(p.Value).Append('|').Append(p.DefaultValue).Append('|')
              .Append(p.VersionStart).Append('|').Append(p.VersionEnd).Append('|').Append(p.Option).Append('|').Append(p.Priority).Append('|')
              .Append(p.EditorType).Append('|').Append(p.CaptionEn).Append('|').Append(p.CaptionDe).Append('\n');
            var es = new List<CcParamEntry>(p.Entries);
            es.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
            foreach (CcParamEntry e in es)
                sb.Append("  ").Append(e.Id.ToUpperInvariant()).Append('|').Append(e.Value).Append('|').Append(e.IsDefault ? 1 : 0).Append('|').Append(e.Priority).Append('|').Append(e.CaptionEn).Append('|').Append(e.CaptionDe).Append('\n');
        }
        return sb.ToString();
    }

    static string Sha256Text(string s)
    {
        using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(new UTF8Encoding(false).GetBytes(s))).Replace("-", "");
    }

    // Schritte "Projektvariable setzen", die eine Parameter-Variable fest setzen (ueberschreiben den Parameterwert beim Lauf)
    public static List<string> ParamOverrideWarnings(CcScript s, ICollection<string> vars)
    {
        var w = new List<string>();
        if (s == null || vars.Count == 0) return w;
        var set = new HashSet<string>(vars, StringComparer.OrdinalIgnoreCase);
        int no = 0;
        WalkOverrides(s.Roots, set, w, 0, ref no);
        return w;
    }

    static void WalkOverrides(List<CcNode> nodes, HashSet<string> vars, List<string> w, int depth, ref int no)
    {
        foreach (CcNode n in nodes)
        {
            no++;
            if (n.Class == "TSetProjectVar" || n.Class == "TSetProjectVarFromRegistry" || n.Class == "TSetProjectVarFromInifile" || n.Class == "TSetProjectVarFromEnvString")
            {
                var doc = new XmlDocument(); doc.LoadXml(n.Xml);
                XmlElement vn = doc.DocumentElement["Variable_name"], en = doc.DocumentElement["Enabled"], de = doc.DocumentElement["Description"], ct = doc.DocumentElement["Content"];
                string v = vn != null ? vn.InnerText.Trim().Trim('%') : "";
                bool enabled = en == null || !string.Equals(en.InnerText, "False", StringComparison.OrdinalIgnoreCase);
                if (enabled && vars.Contains(v))
                    w.Add("ACHTUNG: Schritt " + no + " \"" + (de != null ? de.InnerText : n.Class) + "\" (" + n.Class + (depth > 0 ? ", verschachtelt" : "") + ") setzt " + v +
                          (ct != null ? " fest auf \"" + ct.InnerText + "\"" : "") + " - der Parameterwert wird dort ueberschrieben. Schritt entfernen (Startwert der Projektvariable genuegt) oder bedingt machen.");
            }
            if (n.Children.Count > 0) WalkOverrides(n.Children, vars, w, depth + 1, ref no);
        }
    }

    static JObject ParamJson(CcParam p, bool ids)
    {
        var o = new JObject();
        if (ids) o["id"] = p.Id;
        o["variable"] = p.Name;
        o["type"] = p.EditorType == 0 ? "text" : p.EditorType == 1 ? "combobox" : p.EditorType == 2 ? "checkbox" : "EditorType " + p.EditorType;
        o["caption"] = p.CaptionDe; if (p.CaptionEn != p.CaptionDe) o["captionEn"] = p.CaptionEn;
        if (p.EditorType == 1)
        {
            var ea = new JArray();
            foreach (CcParamEntry e in p.Entries)
            {
                var eo = new JObject(); eo["value"] = e.Value; if (e.CaptionDe != e.Value) eo["caption"] = e.CaptionDe; if (e.CaptionEn != e.CaptionDe) eo["captionEn"] = e.CaptionEn;
                if (ids) eo["id"] = e.Id;
                ea.Add(eo);
            }
            o["entries"] = ea;
            CcParamEntry de = p.Entries.Find(e => string.Equals(e.Id, p.DefaultValue, StringComparison.OrdinalIgnoreCase));
            o["default"] = de != null ? de.Value : p.DefaultValue;
        }
        else if (p.EditorType == 2) o["default"] = string.Equals(p.DefaultValue, "true", StringComparison.OrdinalIgnoreCase) || p.DefaultValue == "1";
        else o["default"] = p.DefaultValue;
        o["versions"] = p.VersionStart + (p.VersionEnd == int.MaxValue ? "+" : "-" + p.VersionEnd);
        if (p.Availability != 0) o["availability"] = p.Availability;
        return o;
    }

    static string ParamLine(CcParam p)
    {
        string t = p.EditorType == 0 ? "Text" : p.EditorType == 1 ? "Auswahl" : "Kontrollkaestchen";
        string d;
        if (p.EditorType == 1)
        {
            var vals = new List<string>(); string def = null;
            foreach (CcParamEntry e in p.Entries) { vals.Add(e.Value + (e.CaptionDe != e.Value ? " (\"" + e.CaptionDe + "\")" : "")); if (e.IsDefault) def = e.Value; }
            d = "Werte " + string.Join(", ", vals.ToArray()) + "; Standard " + (def ?? "?");
        }
        else d = "Standard \"" + p.DefaultValue + "\"";
        return p.Name + " - " + t + " \"" + p.CaptionDe + "\"" + (p.CaptionEn != p.CaptionDe ? " (EN \"" + p.CaptionEn + "\")" : "") + "; " + d + "; ab Version " + p.VersionStart;
    }

    // ================= DB =================

    static List<CcParam> LoadParams(SqlConnection c, SqlTransaction tx, string scriptId)
    {
        Guid g = new Guid(scriptId.Trim('{', '}'));
        var list = new List<CcParam>();
        var byId = new Dictionary<string, CcParam>(StringComparer.OrdinalIgnoreCase);
        var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = "SELECT CAST(d.Id AS varchar(36)), d.Name, d.Availability, CAST(d.Value AS nvarchar(4000)), CAST(d.DefaultValue AS nvarchar(4000)), d.VersionStart, d.VersionEnd, d.[Option], d.Priority, " +
                        "c.EditorType, c.Caption_En, c.Caption_De FROM SYS_Jobs_Parameters_Declarations d" + (tx != null ? " WITH (UPDLOCK, HOLDLOCK)" : "") +
                        " LEFT JOIN SYS_Jobs_Parameters_Config c ON c.Id = d.Id WHERE d.ContentId = @id ORDER BY d.Priority, d.Name";
        q.Parameters.AddWithValue("@id", g);
        using (var r = q.ExecuteReader())
            while (r.Read())
            {
                var p = new CcParam();
                p.Id = "{" + r.GetString(0).ToUpperInvariant() + "}"; p.Name = r.GetString(1); p.Availability = Convert.ToInt32(r.GetValue(2));
                p.Value = r.IsDBNull(3) ? null : r.GetString(3); p.DefaultValue = r.IsDBNull(4) ? null : r.GetString(4);
                p.VersionStart = Convert.ToInt32(r.GetValue(5)); p.VersionEnd = Convert.ToInt32(r.GetValue(6)); p.Option = Convert.ToInt32(r.GetValue(7)); p.Priority = Convert.ToInt32(r.GetValue(8));
                p.EditorType = r.IsDBNull(9) ? -1 : Convert.ToInt32(r.GetValue(9)); p.CaptionEn = r.IsDBNull(10) ? null : r.GetString(10); p.CaptionDe = r.IsDBNull(11) ? null : r.GetString(11);
                p.Type = p.EditorType == 0 ? "text" : p.EditorType == 1 ? "combobox" : p.EditorType == 2 ? "checkbox" : "?";
                list.Add(p); byId[p.Id] = p;
            }
        if (list.Count == 0) return list;
        var vq = c.CreateCommand(); vq.Transaction = tx;
        var names = new List<string>(); int i = 0;
        foreach (CcParam p in list) { string n = "@c" + i++; names.Add(n); vq.Parameters.AddWithValue(n, new Guid(p.Id.Trim('{', '}'))); }
        vq.CommandText = "SELECT CAST(Id AS varchar(36)), CAST(ConfigId AS varchar(36)), Value, IsDefault, Priority, Caption_En, Caption_De FROM SYS_Jobs_Parameters_Config_Values" +
                         " WHERE ConfigId IN (" + string.Join(",", names.ToArray()) + ") ORDER BY Priority";
        using (var r = vq.ExecuteReader())
            while (r.Read())
            {
                CcParam p;
                if (!byId.TryGetValue("{" + r.GetString(1).ToUpperInvariant() + "}", out p)) continue;
                var e = new CcParamEntry();
                e.Id = "{" + r.GetString(0).ToUpperInvariant() + "}"; e.Value = r.IsDBNull(2) ? "" : r.GetString(2); e.IsDefault = !r.IsDBNull(3) && r.GetBoolean(3);
                e.Priority = Convert.ToInt32(r.GetValue(4)); e.CaptionEn = r.IsDBNull(5) ? null : r.GetString(5); e.CaptionDe = r.IsDBNull(6) ? null : r.GetString(6);
                p.Entries.Add(e);
            }
        return list;
    }

    static List<string> WritableVars(SqlConnection c, string id, int ver)
    {
        var l = new List<string>();
        foreach (CcVar v in LoadVarRows(c, null, id, ver)) if (!v.ReadOnly) l.Add(v.Name);
        return l;
    }

    // ================= Vorpruefung (zusaetzlich zur Kompatibilitaetspruefung des CC-Writers) =================

    static readonly Dictionary<string, string[]> ParamCols = new Dictionary<string, string[]> {
        { "SYS_Jobs_Parameters_Declarations", new[] { "Id", "Name", "Value", "DefaultValue", "VersionStart", "VersionEnd", "Availability", "Option", "ContentId", "Priority" } },
        { "SYS_Jobs_Parameters_Config", new[] { "Id", "EditorType", "Caption_En", "Caption_De" } },
        { "SYS_Jobs_Parameters_Config_Values", new[] { "Id", "Caption_En", "Caption_De", "IsDefault", "ConfigId", "Value", "Priority" } } };

    public JObject ParamPreflight(bool refresh)
    {
        if (!ConfigOk) return null;
        lock (_pfLock)
        {
            if (!refresh && _paramPf != null && (DateTime.UtcNow - _paramPfAt).TotalMinutes < PreflightMaxAgeMinutes) return _paramPf;
            var checks = new JArray(); bool ok = true;
            Action<string, string, string> add = (ck, rs, det) => { var co = new JObject(); co["check"] = ck; co["result"] = rs; co["detail"] = det; checks.Add(co); if (rs == "block") ok = false; };
            try
            {
                using (var rc = new SqlConnection(_readCs))
                using (var wc = new SqlConnection(_writeCs))
                {
                    rc.Open(); wc.Open();
                    foreach (var kv in ParamCols)
                    {
                        var have = new List<string>();
                        var q = rc.CreateCommand(); q.CommandText = "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME=@t ORDER BY ORDINAL_POSITION"; q.Parameters.AddWithValue("@t", kv.Key);
                        using (var r = q.ExecuteReader()) while (r.Read()) have.Add(r.GetString(0));
                        var miss = new List<string>(); foreach (string col in kv.Value) if (!have.Exists(h => string.Equals(h, col, StringComparison.OrdinalIgnoreCase))) miss.Add(col);
                        bool exact = miss.Count == 0 && have.Count == kv.Value.Length;
                        add("Schema " + kv.Key, exact ? "ok" : "block", exact ? have.Count + " Spalten wie erwartet" : "Spalten: " + string.Join(",", have.ToArray()) + (miss.Count > 0 ? " - fehlen: " + string.Join(",", miss.ToArray()) : " - unerwartete Spalten"));
                    }
                    var missPerm = new List<string>();
                    foreach (string t in ParamTables)
                        foreach (string pm in new[] { "SELECT", "INSERT", "UPDATE", "DELETE" })
                        {
                            var q = wc.CreateCommand(); q.CommandText = "SELECT HAS_PERMS_BY_NAME(@o, 'OBJECT', @p)"; q.Parameters.AddWithValue("@o", "dbo." + t); q.Parameters.AddWithValue("@p", pm);
                            object o = q.ExecuteScalar();
                            if (o == null || o is DBNull || Convert.ToInt32(o) != 1) missPerm.Add(pm + " " + t);
                        }
                    add("Rechte Parameter", missPerm.Count == 0 ? "ok" : "block", missPerm.Count == 0 ? "SELECT/INSERT/UPDATE/DELETE auf die drei Parameter-Tabellen" : "fehlen: " + string.Join(", ", missPerm.ToArray()) + " (grant-cf-params.sql)");
                    var trg = new List<string>();
                    var tq = rc.CreateCommand();
                    tq.CommandText = "SELECT OBJECT_NAME(parent_id) + '.' + name FROM sys.triggers WHERE parent_id IN (OBJECT_ID('dbo.SYS_Jobs_Parameters_Declarations'), OBJECT_ID('dbo.SYS_Jobs_Parameters_Config'), OBJECT_ID('dbo.SYS_Jobs_Parameters_Config_Values'))";
                    using (var r = tq.ExecuteReader()) while (r.Read()) trg.Add(r.GetString(0));
                    add("Trigger Parameter", trg.Count == 0 ? "ok" : "block", trg.Count == 0 ? "keine Trigger auf den Parameter-Tabellen" : "unbekannte Trigger: " + string.Join(", ", trg.ToArray()) + " - erst pruefen");
                }
            }
            catch (Exception ex) { add("Pruefung Parameter", "block", "Abgebrochen: " + ex.Message); }
            var res = new JObject();
            res["ok"] = ok; res["checkedLocal"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"); res["checks"] = checks;
            var bl = new List<string>(); foreach (JObject ck in checks) if ((string)ck["result"] == "block") bl.Add((string)ck["check"] + ": " + (string)ck["detail"]);
            res["summary"] = ok ? "bestanden" : "GESPERRT: " + string.Join(" | ", bl.ToArray());
            _paramPf = res; _paramPfAt = DateTime.UtcNow;
            _log("CC-Parameter Vorpruefung: " + (string)res["summary"]);
            return res;
        }
    }

    public void CheckParamWrite(string requester)
    {
        CheckWrite(requester, false);
        JObject pf = ParamPreflight(false);
        if (pf == null || !(bool)pf["ok"]) throw new Exception("acmp_cc_set_parameters gesperrt - Vorpruefung nicht bestanden: " + (pf != null ? (string)pf["summary"] : "keine Pruefung") + " (Details: acmp_cc_parameters status=true). Nichts geschrieben.");
    }

    // ================= Lesen =================

    public JObject Parameters(JObject a)
    {
        if (string.IsNullOrEmpty(_readCs)) throw new Exception("DB-Lesezugang (db.connectionString) fehlt.");
        string arg = PStr(a["scriptId"]);
        if (arg.Length == 0) throw new Exception("scriptId fehlt (GUID oder eindeutiger Name des Client Commands).");
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            JObject cmd = ResolveCommand(c, arg);
            string id = (string)cmd["scriptId"];
            List<CcVersionRow> rows = LoadVersions(c, null, id);
            CcVersionRow top = rows[0];
            List<CcParam> ps = LoadParams(c, null, id);
            var o = new JObject();
            o["scriptId"] = id; o["name"] = top.Name; o["neuesteVersion"] = top.Version; o["state"] = top.State == 1 ? "Entwurf" : top.State == 7 ? "freigegeben" : "State " + top.State;
            var pa = new JArray(); foreach (CcParam p in ps) pa.Add(ParamJson(p, a["ids"] != null && a["ids"].Type == JTokenType.Boolean && (bool)a["ids"]));
            o["parameter"] = pa;
            var vars = new JArray(); var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase); foreach (CcParam p in ps) used.Add(p.Name);
            foreach (CcVar v in LoadVarRows(c, null, id, top.Version))
            {
                var vj = new JObject(); vj["name"] = v.Name; if (v.ReadOnly) vj["readOnly"] = true; if (used.Contains(v.Name)) vj["parameter"] = true;
                vars.Add(vj);
            }
            o["projektvariablen"] = vars;
            var warn = new List<string>();
            foreach (CcParam p in ps)
                if (!ParamIdOk(id, p))
                    warn.Add("Parameter " + p.Name + " hat die Id " + p.Id + ", die Console sucht ihn aber unter " + ParamId(id, p.Name) +
                             " und zeigt ihn ohne Eintraege (z.B. vom MCP vor der Reparatur am 22.10.2026 angelegt). Reparatur: acmp_cc_set_parameters mit derselben Liste.");
            try { warn.AddRange(ParamOverrideWarnings(top.Client != null ? CcFormat.Decode(top.Client) : null, used)); }
            catch { }
            if (warn.Count > 0) o["warnungen"] = new JArray(warn.ToArray());
            o["hinweis"] = "Setzen: acmp_cc_set_parameters mit der vollstaendigen Liste (erst dryRun=true). Der Parameterwert ersetzt beim Jobstart den Startwert der Projektvariable.";
            if (a["status"] != null && a["status"].Type == JTokenType.Boolean && (bool)a["status"]) o["vorpruefung"] = ConfigOk ? ParamPreflight(true) : (JToken)("CC-Writer aus: " + DisabledReason);
            return o;
        }
    }

    // ================= Planen =================

    public JObject PlanSetParameters(JObject a)
    {
        if (string.IsNullOrEmpty(_readCs)) throw new Exception("DB-Lesezugang (db.connectionString) fehlt.");
        string arg = PStr(a["scriptId"]);
        if (arg.Length == 0) throw new Exception("scriptId fehlt (GUID oder eindeutiger Name des Client Commands).");
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            JObject cmd = ResolveCommand(c, arg);
            string id = (string)cmd["scriptId"];
            List<CcVersionRow> rows = LoadVersions(c, null, id);
            CcVersionRow top = rows[0];
            string label = "\"" + top.Name + "\" " + id;
            Dictionary<string, CcFolder> folders = LoadFolders(c);
            CheckVersionFolder(folders, top.FolderId, _versionScope, label);
            // Standard wie die Console: erste Version des Commands (ein Parameter, dessen Variable erst eine spaetere Version hat, bekam VersionStart 1)
            int fromVersion = rows[rows.Count - 1].Version;
            if (!PNull(a["fromVersion"]))
            {
                try { fromVersion = (int)a["fromVersion"]; } catch { throw new Exception("fromVersion muss eine Zahl sein."); }
                if (fromVersion < 1 || fromVersion > top.Version) throw new Exception("fromVersion " + fromVersion + " ausserhalb 1-" + top.Version + ".");
            }
            List<CcParam> before = LoadParams(c, null, id);
            Dictionary<string, CcParam> existing = ParamsByName(id, before);
            List<string> writable = WritableVars(c, id, top.Version);
            List<CcParam> after = NormalizeParams(a["parameters"], id, writable, existing, fromVersion, NewGuid);
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase); foreach (CcParam p in after) keep.Add(p.Id);
            var removed = before.FindAll(p => !keep.Contains(p.Id));
            string beforeKey = ParamKey(before), afterKey = ParamKey(after);
            if (beforeKey == afterKey) throw new Exception(label + ": Parameter sind schon genau so - nichts zu tun.");
            CcFolder f; folders.TryGetValue(top.FolderId, out f);

            var p0 = new JObject();
            p0["action"] = "setparams"; p0["batchId"] = NewGuid(); p0["scriptId"] = id; p0["name"] = top.Name;
            p0["folderPath"] = f != null ? f.Path : top.FolderId; p0["topVersion"] = top.Version;
            p0["beforeSha256"] = Sha256Text(beforeKey); p0["afterSha256"] = Sha256Text(afterKey);
            p0["params"] = JArray.FromObject(after);
            p0["removedIds"] = new JArray(removed.ConvertAll(x => x.Id).ToArray());

            var pv = new List<string>();
            pv.Add("PARAMETER von " + label + " setzen (wie Eigenschaften -> Client Command spezifisch -> Parameter in der Console), Ordner \"" + p0["folderPath"] + "\", neueste Version v" + top.Version + ".");
            pv.Add("Die Liste ersetzt alle bisherigen Parameter (" + before.Count + " bisher, " + after.Count + " danach). Wirkt sofort fuer neue Jobs, Kiosk und Job Collections - keine neue Version des Commands noetig.");
            var reKeyed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CcParam p in after)
            {
                CcParam old; existing.TryGetValue(p.Name, out old);
                bool newKey = old != null && !string.Equals(old.Id, p.Id, StringComparison.OrdinalIgnoreCase);
                if (newKey) reKeyed.Add(old.Id);
                string mark = old == null ? "NEU      " : newKey ? "NEUE ID  " : ParamKey(new List<CcParam> { old }) == ParamKey(new List<CcParam> { p }) ? "gleich   " : "GEAENDERT";
                pv.Add(mark + " " + ParamLine(p));
                if (newKey) pv.Add("          bisher unter " + old.Id + " - die Console findet ihn nur unter " + p.Id + " (ohne Eintraege angezeigt); alte Zeilen werden entfernt. Bisher: " + ParamLine(old));
                else if (old != null && mark.StartsWith("GEAENDERT")) pv.Add("          bisher: " + ParamLine(old));
            }
            foreach (CcParam p in removed)
                if (!reKeyed.Contains(p.Id))
                    pv.Add("ENTFERNT  " + ParamLine(p) + (after.Exists(x => string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase))
                        ? " - zweite Zeile zur selben Variable (Id " + p.Id + "), die Console zeigt nur eine."
                        : " - Achtung: laut Aagon koennen Jobs alter Versionen, die den Parameter erwarten, dann fehlschlagen."));
            try
            {
                var names = new List<string>(); foreach (CcParam p in after) names.Add(p.Name);
                foreach (string w in ParamOverrideWarnings(top.Client != null ? CcFormat.Decode(top.Client) : null, names)) pv.Add(w);
            }
            catch (Exception ex) { pv.Add("Hinweis: Skript v" + top.Version + " nicht lesbar, keine Pruefung auf 'Projektvariable setzen' (" + ex.Message + ")."); }
            CcVersionRow released = rows.Find(x => x.State == 7);
            if (released != null && fromVersion > released.Version && after.Exists(x => !existing.ContainsKey(x.Name)))
                pv.Add("ACHTUNG: Auf Clients laeuft die freigegebene v" + released.Version + ", neue Parameter gelten erst ab v" + fromVersion + " - fuer Jobs mit v" + released.Version + " nicht (fromVersion weglassen = ab der ersten Version, wie die Console).");
            pv.Add("Ablauf: eine Transaktion (SYS_Jobs_Parameters_Declarations, _Config, _Config_Values), Sicherung vorher (cc-backups). Bei der Ausfuehrung wird der Stand der Parameter (SHA-256) verglichen.");
            p0["vorschau"] = new JArray(pv.ToArray());
            return p0;
        }
    }

    // ================= Ausfuehren (nur mit dem Plan, den der Server direkt vorher erstellt hat) =================

    public JObject RunSetParameters(JObject a, string requester)
    {
        CheckParamWrite(requester);
        JObject p = a["_plan"] as JObject;
        if (p == null || (string)p["action"] != "setparams") throw new Exception("acmp_cc_set_parameters laeuft nur mit einem vom Server erstellten Plan - nichts geschrieben.");
        string id = (string)p["scriptId"], label = "\"" + p["name"] + "\" " + id;
        List<CcParam> after = p["params"].ToObject<List<CcParam>>();
        var removedIds = new List<string>(); foreach (JToken t in (JArray)p["removedIds"]) removedIds.Add((string)t);
        if (Sha256Text(ParamKey(after)) != (string)p["afterSha256"]) throw new Exception("Plan unvollstaendig (Pruefsumme) - nichts geschrieben.");
        foreach (CcParam x in after)   // Anfragen von vor der Reparatur (22.10.2026) trugen zufaellige Ids
            if (!ParamIdOk(id, x)) throw new Exception(label + ": Parameter " + x.Name + " hat im Plan nicht die Console-Id (Plan von vor der Reparatur?) - nichts geschrieben, bitte erneut aufrufen.");
        string backup;
        Guid sid = new Guid(id.Trim('{', '}'));
        lock (_lock)
        using (var c = new SqlConnection(_writeCs))
        {
            c.Open();
            using (var rc = new SqlConnection(_readCs))
            {
                rc.Open();
                List<CcVersionRow> rows = LoadVersions(rc, null, id);
                if (rows.Count == 0) throw new Exception(label + " gibt es nicht mehr - nichts geschrieben.");
                CheckVersionFolder(LoadFolders(rc), rows[0].FolderId, _versionScope, label);
                List<string> writable = WritableVars(rc, id, rows[0].Version);
                foreach (CcParam x in after)
                    if (!writable.Exists(w => string.Equals(w, x.Name, StringComparison.OrdinalIgnoreCase)))
                        throw new Exception(label + ": Projektvariable " + x.Name + " ist in der neuesten Version v" + rows[0].Version + " nicht (mehr) schreibbar vorhanden - nichts geschrieben, bitte erneut aufrufen.");
            }
            List<CcParam> now = LoadParams(c, null, id);
            if (Sha256Text(ParamKey(now)) != (string)p["beforeSha256"]) throw new Exception(label + ": Parameter wurden seit der Planung geaendert - nichts geschrieben, bitte erneut aufrufen.");
            var b = new JObject();
            b["kind"] = "params"; b["batchId"] = p["batchId"]; b["zeit"] = DateTime.Now.ToString("s"); b["von"] = requester; b["scriptId"] = id; b["name"] = p["name"];
            b["grund"] = "vor acmp_cc_set_parameters (Stand vorher; zurueck: acmp_cc_set_parameters mit dieser Liste)";
            b["vorher"] = JArray.FromObject(now);
            var back = new JArray(); foreach (CcParam x in now) back.Add(ParamJson(x, false));
            b["vorherAlsAuftrag"] = back;
            backup = WriteBackup(b, "params", (string)p["batchId"]);

            using (var tx = c.BeginTransaction())
            {
                List<CcParam> locked = LoadParams(c, tx, id);
                if (Sha256Text(ParamKey(locked)) != (string)p["beforeSha256"]) throw new Exception(label + ": Parameter wurden gerade geaendert - zurueckgerollt, nichts geschrieben.");
                foreach (string rid in removedIds)
                {
                    Guid g = new Guid(rid.Trim('{', '}'));
                    foreach (string sql in new[] { "DELETE FROM SYS_Jobs_Parameters_Config_Values WHERE ConfigId=@id", "DELETE FROM SYS_Jobs_Parameters_Config WHERE Id=@id", "DELETE FROM SYS_Jobs_Parameters_Declarations WHERE Id=@id AND ContentId=@sid" })
                    {
                        var d = c.CreateCommand(); d.Transaction = tx; d.CommandText = sql; d.Parameters.AddWithValue("@id", g); d.Parameters.AddWithValue("@sid", sid);
                        d.ExecuteNonQuery();
                    }
                }
                foreach (CcParam x in after)
                {
                    Guid pid = new Guid(x.Id.Trim('{', '}'));
                    var u = c.CreateCommand(); u.Transaction = tx;
                    u.CommandText = "UPDATE SYS_Jobs_Parameters_Declarations SET ContentId=@sid, Name=@n, Availability=@av, Value=@v, DefaultValue=@dv, VersionStart=@vs, VersionEnd=@ve, [Option]=@op, Priority=@pr WHERE Id=@id; " +
                                    "IF @@ROWCOUNT = 0 INSERT INTO SYS_Jobs_Parameters_Declarations (Id, ContentId, Name, Availability, Value, DefaultValue, VersionStart, VersionEnd, [Option], Priority) VALUES (@id, @sid, @n, @av, @v, @dv, @vs, @ve, @op, @pr);";
                    u.Parameters.AddWithValue("@id", pid); u.Parameters.AddWithValue("@sid", sid); u.Parameters.Add("@n", SqlDbType.NVarChar, 200).Value = x.Name;
                    u.Parameters.AddWithValue("@av", x.Availability);
                    u.Parameters.Add("@v", SqlDbType.NVarChar, 4000).Value = (object)x.Value ?? DBNull.Value; u.Parameters.Add("@dv", SqlDbType.NVarChar, 4000).Value = (object)x.DefaultValue ?? DBNull.Value;
                    u.Parameters.AddWithValue("@vs", x.VersionStart); u.Parameters.AddWithValue("@ve", x.VersionEnd); u.Parameters.AddWithValue("@op", x.Option); u.Parameters.AddWithValue("@pr", x.Priority);
                    u.ExecuteNonQuery();
                    var cu = c.CreateCommand(); cu.Transaction = tx;
                    cu.CommandText = "UPDATE SYS_Jobs_Parameters_Config SET EditorType=@et, Caption_En=@en, Caption_De=@de WHERE Id=@id; " +
                                     "IF @@ROWCOUNT = 0 INSERT INTO SYS_Jobs_Parameters_Config (Id, EditorType, Caption_En, Caption_De) VALUES (@id, @et, @en, @de);";
                    cu.Parameters.AddWithValue("@id", pid); cu.Parameters.Add("@et", SqlDbType.TinyInt).Value = (byte)x.EditorType;
                    cu.Parameters.AddWithValue("@en", x.CaptionEn); cu.Parameters.AddWithValue("@de", x.CaptionDe);
                    cu.ExecuteNonQuery();
                    // Eintraege: nicht mehr vorhandene loeschen, uebrige aktualisieren bzw. anlegen
                    var dv = c.CreateCommand(); dv.Transaction = tx;
                    var keepNames = new List<string>(); int k = 0;
                    foreach (CcParamEntry e in x.Entries) { string n = "@k" + k++; keepNames.Add(n); dv.Parameters.AddWithValue(n, new Guid(e.Id.Trim('{', '}'))); }
                    dv.CommandText = "DELETE FROM SYS_Jobs_Parameters_Config_Values WHERE ConfigId=@id" + (keepNames.Count > 0 ? " AND Id NOT IN (" + string.Join(",", keepNames.ToArray()) + ")" : "");
                    dv.Parameters.AddWithValue("@id", pid);
                    dv.ExecuteNonQuery();
                    foreach (CcParamEntry e in x.Entries)
                    {
                        var ev = c.CreateCommand(); ev.Transaction = tx;
                        ev.CommandText = "UPDATE SYS_Jobs_Parameters_Config_Values SET Value=@v, IsDefault=@d, Priority=@pr, Caption_En=@en, Caption_De=@de WHERE Id=@eid AND ConfigId=@id; " +
                                         "IF @@ROWCOUNT = 0 INSERT INTO SYS_Jobs_Parameters_Config_Values (Id, Value, ConfigId, IsDefault, Priority, Caption_En, Caption_De) VALUES (@eid, @v, @id, @d, @pr, @en, @de);";
                        ev.Parameters.AddWithValue("@eid", new Guid(e.Id.Trim('{', '}'))); ev.Parameters.AddWithValue("@id", pid);
                        ev.Parameters.AddWithValue("@v", e.Value); ev.Parameters.AddWithValue("@d", e.IsDefault); ev.Parameters.AddWithValue("@pr", e.Priority);
                        ev.Parameters.AddWithValue("@en", e.CaptionEn); ev.Parameters.AddWithValue("@de", e.CaptionDe);
                        ev.ExecuteNonQuery();
                    }
                }
                // Kontrolle in derselben Transaktion: Stand = Plan
                List<CcParam> check = LoadParams(c, tx, id);
                if (Sha256Text(ParamKey(check)) != (string)p["afterSha256"]) throw new Exception(label + ": Parameter nach dem Schreiben nicht wie geplant - zurueckgerollt, nichts geschrieben.");
                tx.Commit();
            }
        }
        string bn = System.IO.Path.GetFileName(backup);
        _log("acmp_cc_set_parameters von " + requester + ": " + label + " " + after.Count + " Parameter (" + removedIds.Count + " entfernt), Sicherung " + bn);
        var o = new JObject();
        o["ok"] = true; o["scriptId"] = id; o["name"] = p["name"];
        var pa = new JArray(); foreach (CcParam x in after) pa.Add(ParamJson(x, false));
        o["parameter"] = pa; o["entfernt"] = removedIds.Count;
        o["sicherung"] = bn;
        o["hinweis"] = "Wirkt sofort fuer neue Jobs (Console: Job anlegen zeigt die Parameter, Kiosk ebenso). Zurueck: acmp_cc_set_parameters mit 'vorherAlsAuftrag' aus der Sicherung.";
        return o;
    }
}
