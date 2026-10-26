// AcmpOpenItems: gemeinsame Liste "Offene Punkte" der IT (seit 23.10.2026). Claude-Sitzungen pflegen sie ueber acmp_open_items (lesen)
// und acmp_open_item_set (anlegen, aendern, erledigen, wieder oeffnen); das Dashboard der Weboberflaeche zeigt sie, Menschen haken dort ab.
// Datei offene-punkte.json im Server-Ordner ({"version":1,"items":[...]}), vor jedem Schreiben Sicherung offene-punkte.json.bak,
// geschrieben ueber eine Temp-Datei. "von" ist immer der Bediener des API-Keys bzw. der Web-Sitzung, nie ein Argument.
// Texte: Werte hinter Kennwort/Token/Key/Secret werden maskiert (wie im Aktivitaetsprotokoll), Laengen begrenzt.
// net48 / C# 5 (csc v4.0.30319): keine string-Interpolation, keine expression-bodied member, kein ?.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

class AcmpOpenItems
{
    public const string FileName = "offene-punkte.json";
    public const int MaxItems = 1000;
    public static readonly string[] Areas = { "Flotte", "Server", "Updates", "Jobs", "Kiosk", "Client", "MCP", "Sonstiges" };
    public static readonly string[] Prios = { "hoch", "normal", "niedrig" };
    public const string AreasHelp = "Flotte, Server, Updates, Jobs, Kiosk, Client, MCP oder Sonstiges";

    readonly string _file;
    readonly Action<string> _log;
    readonly object _lock = new object();
    static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
    static readonly Regex SecretValue = new Regex(@"(?i)\b(pass(wor[dt])?|pwd|kennwort|token|api[-_ ]?key|secret|geheimnis)(\s*[:=]\s*|\s+ist\s+)(""[^""]*""|'[^']*'|\S+)");
    static readonly Regex DateRx = new Regex(@"^\d{4}-\d{2}-\d{2}$");
    static readonly Regex IdRx = new Regex("^[a-f0-9]{6,12}$");

    public AcmpOpenItems(string cfgDir, Action<string> log)
    {
        _file = Path.Combine(cfgDir, FileName);
        _log = log;
    }

    public string FilePath { get { return _file; } }

    // ---------- Lesen ----------
    JArray Load()
    {
        if (!File.Exists(_file)) return new JArray();
        JObject root;
        try { root = JObject.Parse(File.ReadAllText(_file, Encoding.UTF8)); }
        catch (Exception ex) { throw new Exception(FileName + " ist nicht lesbar (" + ex.Message + ") - nichts geaendert. Datei pruefen oder aus " + FileName + ".bak zuruecknehmen."); }
        JArray items = root["items"] as JArray;
        if (items == null) throw new Exception(FileName + " hat keine Liste 'items' - nichts geaendert.");
        return items;
    }

    public List<JObject> Items()
    {
        lock (_lock)
        {
            var l = new List<JObject>();
            foreach (JToken t in Load()) { JObject o = t as JObject; if (o != null) l.Add(o); }
            return l;
        }
    }

    // Offene Punkte fuer das Dashboard: hoch vor normal vor niedrig, faellige zuerst, dann aelteste zuerst
    public List<JObject> Open()
    {
        var l = new List<JObject>();
        foreach (JObject o in Items()) if (Str(o, "status") != "erledigt") l.Add(o);
        Sort(l);
        return l;
    }

    // Erledigte der letzten n Tage, neueste zuerst
    public List<JObject> RecentlyDone(int days)
    {
        string since = DateTime.Now.AddDays(-days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var l = new List<JObject>();
        foreach (JObject o in Items()) if (Str(o, "status") == "erledigt" && string.CompareOrdinal(Str(o, "erledigtAm") ?? "", since) >= 0) l.Add(o);
        l.Sort(delegate(JObject x, JObject y) { return string.CompareOrdinal(Str(y, "erledigtAm") ?? "", Str(x, "erledigtAm") ?? ""); });
        return l;
    }

    public static void Sort(List<JObject> l)
    {
        var idx = new Dictionary<JObject, int>(); for (int i = 0; i < l.Count; i++) idx[l[i]] = i;
        l.Sort(delegate(JObject x, JObject y)
        {
            int d = Array.IndexOf(Prios, Str(x, "prio") ?? "normal").CompareTo(Array.IndexOf(Prios, Str(y, "prio") ?? "normal")); if (d != 0) return d;
            string fx = Str(x, "faellig"), fy = Str(y, "faellig");
            if (fx != null || fy != null) { if (fx == null) return 1; if (fy == null) return -1; d = string.CompareOrdinal(fx, fy); if (d != 0) return d; }
            d = string.CompareOrdinal(Str(x, "seit") ?? "", Str(y, "seit") ?? ""); if (d != 0) return d;
            return idx[x].CompareTo(idx[y]);
        });
    }

    // acmp_open_items
    public JObject List(JObject a)
    {
        string status = Arg(a, "status") ?? "offen";
        if (status != "offen" && status != "erledigt" && status != "alle") throw new Exception("status: offen (Standard), erledigt oder alle.");
        string area = Arg(a, "bereich"), von = Arg(a, "von"), q = Arg(a, "suche");
        int top = 100;
        if (a != null && a["top"] != null && a["top"].Type != JTokenType.Null) { try { top = Math.Max(1, Math.Min(MaxItems, (int)a["top"])); } catch { throw new Exception("top muss eine Zahl sein."); } }
        var all = Items();
        var shown = new List<JObject>();
        int open = 0;
        foreach (JObject o in all)
        {
            bool done = Str(o, "status") == "erledigt";
            if (!done) open++;
            if (status == "offen" && done) continue;
            if (status == "erledigt" && !done) continue;
            if (area != null && !string.Equals(Str(o, "bereich"), area, StringComparison.OrdinalIgnoreCase)) continue;
            if (von != null && !string.Equals(Str(o, "von"), von, StringComparison.OrdinalIgnoreCase)) continue;
            if (q != null && (Str(o, "titel") ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 && (Str(o, "details") ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
            shown.Add(o);
        }
        if (status == "erledigt") shown.Sort(delegate(JObject x, JObject y) { return string.CompareOrdinal(Str(y, "erledigtAm") ?? "", Str(x, "erledigtAm") ?? ""); });
        else Sort(shown);
        var res = new JObject();
        res["offen"] = open;
        res["gesamt"] = all.Count;
        res["gefunden"] = shown.Count;
        var arr = new JArray(); foreach (JObject o in shown) { if (arr.Count >= top) break; arr.Add(o.DeepClone()); }
        res["punkte"] = arr;
        if (arr.Count < shown.Count) res["gekuerzt"] = "top=" + top + " - mit top (max. " + MaxItems + ") oder Filtern mehr zeigen.";
        res["hinweis"] = "Anlegen, aendern, erledigen: acmp_open_item_set. Die Liste erscheint im Dashboard der Webseite (" + FileName + " im Server-Ordner).";
        return res;
    }

    // ---------- Schreiben ----------
    // acmp_open_item_set und die Formulare der Webseite. action: anlegen | aendern | erledigen | oeffnen. via: "MCP" oder "Webseite".
    public JObject Set(JObject a, string who, string via)
    {
        if (string.IsNullOrEmpty(who)) throw new Exception("Unbekannter Bediener - nichts geaendert.");
        string action = (Arg(a, "action") ?? "").ToLowerInvariant();
        if (action == "öffnen" || action == "wieder_oeffnen" || action == "wiederoeffnen") action = "oeffnen";
        if (action != "anlegen" && action != "aendern" && action != "erledigen" && action != "oeffnen")
            throw new Exception("action: anlegen, aendern, erledigen oder oeffnen.");
        bool dry = a != null && a["dryRun"] != null && a["dryRun"].Type == JTokenType.Boolean && (bool)a["dryRun"];
        string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        lock (_lock)
        {
            JArray items = Load();
            JObject item, before = null;
            if (action == "anlegen")
            {
                if (Arg(a, "id") != null) throw new Exception("Beim Anlegen keine id angeben - der Server vergibt sie.");
                if (items.Count >= MaxItems) throw new Exception("Die Liste hat schon " + MaxItems + " Eintraege - erst aufraeumen (Datei " + FileName + ").");
                item = new JObject();
                item["id"] = NewId(items);
                string t = Text(a, "titel", 3, 200, true);
                item["titel"] = t;
                SetOptional(item, a, true);
                if (item["bereich"] == null) item["bereich"] = "Sonstiges";
                if (item["prio"] == null) item["prio"] = "normal";
                item["status"] = "offen";
                item["von"] = who;
                item["via"] = via;
                item["seit"] = now;
            }
            else
            {
                string id = (Arg(a, "id") ?? "").ToLowerInvariant();
                if (!IdRx.IsMatch(id)) throw new Exception("id fehlt oder ist ungueltig (steht in acmp_open_items).");
                item = null;
                foreach (JToken tk in items) if (tk is JObject && string.Equals(Str((JObject)tk, "id"), id, StringComparison.OrdinalIgnoreCase)) item = (JObject)tk;
                if (item == null) throw new Exception("Kein offener Punkt mit der id " + id + ".");
                before = (JObject)item.DeepClone();
                bool done = Str(item, "status") == "erledigt";
                if (action == "aendern")
                {
                    if (Arg(a, "titel") != null) item["titel"] = Text(a, "titel", 3, 200, true);
                    int n = SetOptional(item, a, false);
                    if (n == 0 && Arg(a, "titel") == null) throw new Exception("Nichts zu aendern: titel, details, bereich, prio, faellig, sitzung oder link angeben.");
                }
                else if (action == "erledigen")
                {
                    if (done) throw new Exception("Punkt " + id + " ist schon erledigt (" + Str(item, "erledigtAm") + " von " + Str(item, "erledigtVon") + ").");
                    item["status"] = "erledigt";
                    item["erledigtAm"] = now;
                    item["erledigtVon"] = who;
                    string note = Text(a, "notiz", 0, 500, false);
                    if (note != null) item["erledigtNotiz"] = note; else item.Remove("erledigtNotiz");
                }
                else
                {
                    if (!done) throw new Exception("Punkt " + id + " ist noch offen.");
                    item["status"] = "offen";
                    item.Remove("erledigtAm"); item.Remove("erledigtVon"); item.Remove("erledigtNotiz");
                }
                item["geaendert"] = now;
                item["geaendertVon"] = who;
            }

            var res = new JObject();
            res["action"] = action;
            if (dry)
            {
                res["dryRun"] = true;
                res["ergebnis"] = "Vorschau - nichts geschrieben. Ohne dryRun ausfuehren, um zu speichern.";
                if (before != null) res["vorher"] = before;
                res["nachher"] = item.DeepClone();
                return res;
            }
            if (action == "anlegen") items.Add(item);
            Save(items);
            _log("Offene Punkte: " + action + " " + Str(item, "id") + " durch " + who + " (" + via + "): " + Str(item, "titel"));
            res["ergebnis"] = action == "anlegen" ? "angelegt" : action == "erledigen" ? "erledigt" : action == "oeffnen" ? "wieder offen" : "geaendert";
            if (before != null) res["vorher"] = before;
            res["punkt"] = item.DeepClone();
            res["sicherung"] = FileName + ".bak";
            return res;
        }
    }

    // Optionale Felder setzen; beim Aendern entfernt ein leerer String das Feld (nicht bei bereich/prio). Rueckgabe: Zahl der gesetzten Felder.
    int SetOptional(JObject item, JObject a, bool create)
    {
        int n = 0;
        if (Has(a, "details")) { n++; string v = Text(a, "details", 0, 2000, false); if (v == null) item.Remove("details"); else item["details"] = v; }
        if (Has(a, "bereich"))
        {
            n++;
            string b = Arg(a, "bereich");
            string hit = null; foreach (string x in Areas) if (string.Equals(x, b, StringComparison.OrdinalIgnoreCase)) hit = x;
            if (hit == null) throw new Exception("bereich: " + AreasHelp + ".");
            item["bereich"] = hit;
        }
        if (Has(a, "prio"))
        {
            n++;
            string p = (Arg(a, "prio") ?? "").ToLowerInvariant();
            if (Array.IndexOf(Prios, p) < 0) throw new Exception("prio: hoch, normal oder niedrig.");
            item["prio"] = p;
        }
        if (Has(a, "faellig"))
        {
            n++;
            string f = Arg(a, "faellig");
            DateTime d;
            if (f == null) item.Remove("faellig");
            else if (!DateRx.IsMatch(f) || !DateTime.TryParseExact(f, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) throw new Exception("faellig: Datum als JJJJ-MM-TT, z.B. 2026-10-30.");
            else item["faellig"] = f;
        }
        if (Has(a, "sitzung")) { n++; string v = Text(a, "sitzung", 0, 120, false); if (v == null) item.Remove("sitzung"); else item["sitzung"] = v; }
        if (Has(a, "link"))
        {
            n++;
            string v = Text(a, "link", 0, 300, false);
            if (v == null) item.Remove("link");
            else
            {
                if (v.IndexOf("javascript:", StringComparison.OrdinalIgnoreCase) >= 0 || v.IndexOf("data:", StringComparison.OrdinalIgnoreCase) == 0)
                    throw new Exception("link: nur Web-Adressen (https://...) oder Pfade, z.B. eine Wiki-Notiz.");
                item["link"] = v;
            }
        }
        return n;
    }

    void Save(JArray items)
    {
        var root = new JObject();
        root["version"] = 1;
        root["items"] = items;
        string text = root.ToString(Formatting.Indented);
        string tmp = _file + ".tmp";
        File.WriteAllText(tmp, text, Utf8);
        JObject.Parse(File.ReadAllText(tmp, Encoding.UTF8));   // Kontrolle, bevor die echte Datei ersetzt wird
        if (File.Exists(_file)) File.Replace(tmp, _file, _file + ".bak");
        else File.Move(tmp, _file);
    }

    static string NewId(JArray items)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JToken t in items) if (t is JObject) { string s = Str((JObject)t, "id"); if (s != null) used.Add(s); }
        byte[] b = new byte[4];
        using (var rng = new RNGCryptoServiceProvider())
            for (int i = 0; i < 100; i++)
            {
                rng.GetBytes(b);
                string id = BitConverter.ToString(b, 0, 3).Replace("-", "").ToLowerInvariant();
                if (!used.Contains(id)) return id;
            }
        throw new Exception("Keine freie id gefunden.");
    }

    // ---------- Hilfen ----------
    // Werte hinter Kennwort/Token/Key/Secret maskieren ("Kennwort: abc" -> "Kennwort: ***")
    public static string Mask(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return SecretValue.Replace(s, delegate(Match m) { return m.Groups[1].Value + m.Groups[3].Value + "***"; });
    }

    static string Text(JObject a, string key, int min, int max, bool required)
    {
        string v = Arg(a, key);
        if (v == null)
        {
            if (required) throw new Exception(key + " fehlt.");
            return null;
        }
        v = v.Replace("\r\n", "\n").Replace('\r', '\n');
        var sb = new StringBuilder();
        foreach (char c in v) if (c == '\n' || c == '\t' || !char.IsControl(c)) sb.Append(c);
        v = Mask(sb.ToString().Trim());
        if (v.Length < min) throw new Exception(key + ": mindestens " + min + " Zeichen.");
        if (v.Length > max) throw new Exception(key + ": hoechstens " + max + " Zeichen.");
        return v.Length == 0 ? null : v;
    }

    static bool Has(JObject a, string k) { return a != null && a[k] != null && a[k].Type != JTokenType.Null; }

    static string Arg(JObject a, string k)
    {
        if (a == null || a[k] == null || a[k].Type == JTokenType.Null) return null;
        string s = a[k].Type == JTokenType.String ? (string)a[k] : a[k].ToString();
        s = s.Trim();
        return s.Length == 0 ? null : s;
    }

    public static string Str(JObject o, string k)
    {
        if (o == null || o[k] == null || o[k].Type == JTokenType.Null) return null;
        return o[k].Type == JTokenType.String ? (string)o[k] : o[k].ToString();
    }
}
