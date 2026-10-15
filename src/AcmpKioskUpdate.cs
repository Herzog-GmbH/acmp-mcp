// AcmpKioskUpdate: mehrere Kiosk-Eintraege in EINEM Auftrag aendern - in einen anderen Ordner verschieben, umbenennen (DE/EN),
// Beschreibung, Icon und eigener Sichtbarkeitsfilter. Fuer Client Commands (ItemKind 4) und Managed Software (59).
// Grundlage: research\kiosk.md, Offen 7 (15.10.2026). Der Ordner eines Eintrags ist nur SYS_Kiosk_Items.GroupId (keine Reihenfolge-
// Spalte, keine ACL-Zeilen); Verschieben aendert genau diese Spalte. Eintrags-Id, Inhalt (Command/Paket) und Filterzeile bleiben.
//  - Quelle UND Ziel muessen beschreibbare Ordner sein (kioskWriter.allowedGroupIds oder vom MCP angelegt).
//  - Ein Auftrag = eine Freigabe, eine Transaktion, eine Sicherung (Art "items") aller beteiligten Eintraege vorher.
//  - Bei der Ausfuehrung wird jeder Eintrag mit dem Stand der Anfrage verglichen; weicht einer ab, wird nichts geschrieben.
//  - Verschieben aendert die Sichtbarkeit, wenn die Ordnerfilter verschieden sind - die Vorschau sagt das ausdruecklich.
//  - Wie beim Anlegen: derselbe Inhalt darf in einem Ordner nur einmal stehen.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

partial class AcmpKiosk
{
    public const int MaxUpdateChanges = 60;
    public const int MaxDescriptionLength = 8000;
    public static readonly string[] UpdateKeys = { "item", "group", "nameDe", "nameEn", "descriptionDe", "descriptionEn", "image", "filter", "clearFilter" };
    static readonly string[][] UpdatePerms = {
        new[] { "SYS_Kiosk_Items", "UPDATE" }, new[] { "SYS_MultiLanguageItems", "UPDATE" }, new[] { "SYS_MultiLanguageItems_Memo", "UPDATE" } };

    public const string UpdateHelp =
        "Liste der Aenderungen (1-60), je Kiosk-Eintrag ein Objekt: {\"item\":\"<GUID oder eindeutiger Name>\", \"group\":\"<Ziel-Ordner: GUID oder Name>\", " +
        "\"nameDe\":\"...\", \"nameEn\":\"...\", \"descriptionDe\":\"...\", \"descriptionEn\":\"...\", \"image\":\"<Icon: GUID oder Name>\", \"filter\":{...} oder \"clearFilter\":true}. " +
        "Pflicht ist nur item, dazu mindestens ein weiteres Feld. Namen hoechstens 50 Zeichen. nameEn/descriptionEn ohne Angabe: folgen dem deutschen Text, " +
        "wenn der englische bisher gleich oder leer war, sonst bleiben sie. descriptionDe \"\" leert die Beschreibung. Doppelte Namen (z.B. dasselbe Paket in " +
        "zwei Ordnern) per GUID aus acmp_kiosk_tree ansprechen. filter = eigener Sichtbarkeitsfilter des Eintrags wie bei acmp_kiosk_set_filter: ";

    // ================= Vorpruefung (sperrt nur dieses Tool) =================
    bool CheckUpdatePerms(JArray checks, SqlConnection wc)
    {
        var miss = new List<string>();
        foreach (string[] p in UpdatePerms) if (!HasPerm(wc, p[0], p[1])) miss.Add(p[0] + " " + p[1]);
        Add(checks, "Rechte Aendern", miss.Count == 0 ? "ok" : "warn", miss.Count == 0
            ? "UPDATE auf Eintraege, Namen und Beschreibungen vorhanden (acmp_kiosk_update_items)"
            : "fehlen: " + string.Join(", ", miss.ToArray()) + " - acmp_kiosk_update_items gesperrt (grant-kiosk-writer.sql)");
        return miss.Count == 0;
    }

    public void CheckUpdateWrite()
    {
        JObject pf = Preflight(false);
        if (pf == null || pf["updateTool"] == null || !(bool)pf["updateTool"])
            throw new Exception("acmp_kiosk_update_items gesperrt - Vorpruefung \"Rechte Aendern\" nicht bestanden (Details: acmp_kiosk_tree mit status=true). Nichts geschrieben.");
    }

    // ================= Hilfen (ohne DB, offline getestet in KioskTest.cs) =================
    public static JArray ChangeList(JToken t)
    {
        if (t == null || t.Type == JTokenType.Null)
            throw new Exception("changes fehlt: Liste der Aenderungen, z.B. [{\"item\":\"<GUID>\",\"group\":\"Tools\",\"nameDe\":\"7-Zip 25\"}].");
        if (t.Type == JTokenType.String)
        {
            try { t = JToken.Parse((string)t); }
            catch (Exception ex) { throw new Exception("changes ist kein gueltiges JSON: " + ex.Message); }
        }
        JArray a = t as JArray;
        if (a == null && t is JObject) { a = new JArray(); a.Add(t); }
        if (a == null) throw new Exception("changes muss eine Liste von Objekten sein.");
        if (a.Count == 0) throw new Exception("changes ist leer.");
        if (a.Count > MaxUpdateChanges) throw new Exception("changes hat " + a.Count + " Eintraege - hoechstens " + MaxUpdateChanges + " je Auftrag, bitte aufteilen.");
        foreach (JToken e in a)
        {
            JObject ch = e as JObject;
            if (ch == null) throw new Exception("changes: jeder Eintrag muss ein Objekt sein, z.B. {\"item\":\"<GUID>\",\"group\":\"ELO\"}.");
            foreach (JProperty pr in ch.Properties())
                if (Array.IndexOf(UpdateKeys, pr.Name) < 0) throw new Exception("changes: unbekanntes Feld \"" + pr.Name + "\" (erlaubt: " + string.Join(", ", UpdateKeys) + ").");
            if (Str(ch, "item") == null) throw new Exception("changes: \"item\" fehlt (Kiosk-Eintrag: GUID oder Name).");
            if (ch.Count < 2) throw new Exception("changes: bei \"" + Str(ch, "item") + "\" steht nur item - mindestens ein Feld zum Aendern angeben.");
        }
        return a;
    }

    static bool Has(JObject o, string key) { return o[key] != null && o[key].Type != JTokenType.Null; }

    static string Str(JObject o, string key)
    {
        if (!Has(o, key)) return null;
        string v = ((string)o[key] ?? "").Trim();
        return v.Length == 0 ? null : v;
    }

    // Englischer Text ohne eigene Angabe: folgt dem deutschen, wenn er bisher gleich oder leer war, sonst bleibt er
    public static string FollowEn(string oldDe, string oldEn, string newDe)
    {
        oldDe = oldDe ?? ""; oldEn = oldEn ?? "";
        return oldEn.Length == 0 || oldEn == oldDe ? newDe : oldEn;
    }

    // Vorschauzeile fuer eine Namensaenderung (DE/EN): zeigt immer den bisherigen Wert der Sprache, die sich aendert
    // (vorher fehlte bei einer reinen EN-Aenderung der alte englische Name: "PDFsam -> PDFsam (EN ebenso)")
    public static string NameLine(string oDe, string oEn, string nDe, string nEn)
    {
        oDe = oDe ?? ""; oEn = oEn ?? ""; nDe = nDe ?? ""; nEn = nEn ?? "";
        bool de = nDe != oDe, en = nEn != oEn;
        if (de && en) return "Name: " + Quoted(oDe) + " -> " + Quoted(nDe) + (oEn == oDe && nEn == nDe ? " (EN ebenso)" : ", EN: " + Quoted(oEn) + " -> " + Quoted(nEn));
        if (de) return "Name: " + Quoted(oDe) + " -> " + Quoted(nDe) + " (EN bleibt " + Quoted(oEn) + ")";
        if (en) return "Name EN: " + Quoted(oEn) + " -> " + Quoted(nEn) + " (DE bleibt " + Quoted(oDe) + ")";
        return "Name: " + Quoted(oDe) + " (unveraendert)";
    }

    static string Quoted(string s) { return string.IsNullOrEmpty(s) ? "(leer)" : "\"" + s + "\""; }

    public static string Desc(JObject o, string key)
    {
        string v = (string)o[key] ?? "";
        v = Regex.Replace(v.TrimEnd(), "(?<!\r)\n", "\r\n");   // wie die Console: CR LF
        if (v.Length > MaxDescriptionLength) throw new Exception(key + " ist laenger als " + MaxDescriptionLength + " Zeichen.");
        return v;
    }

    // Stand eines Eintrags zum Vergleichen: Ordner, Icon, Namen, Beschreibung, eigener Filter (Ausdruck, null = keine Filterzeile)
    public static JObject StateOf(JObject snap)
    {
        var s = new JObject();
        JObject it = (JObject)snap["item"];
        JObject n = snap["names"] as JObject, m = snap["memo"] as JObject, f = snap["filter"] as JObject;
        s["groupId"] = it["GroupId"]; s["imageId"] = it["ImageId"];
        s["nameDe"] = n != null ? n["de"] : null; s["nameEn"] = n != null ? n["en"] : null;
        s["descriptionDe"] = m != null ? m["de"] : null; s["descriptionEn"] = m != null ? m["en"] : null;
        s["filter"] = f != null ? f["Expression"] : null;
        return s;
    }

    static string Tok(JToken t) { return t == null || t.Type == JTokenType.Null ? null : (string)t; }

    static string KindText(int k) { return k == 4 ? "Client Command" : k == 59 ? "Managed Software" : "Art " + k; }

    static string Short(string s)
    {
        if (string.IsNullOrEmpty(s)) return "(leer)";
        string one = s.Replace("\r\n", " / ").Replace("\n", " / ");
        return "\"" + (one.Length > 80 ? one.Substring(0, 80) + " ..." : one) + "\"";
    }

    // Ordner mit Name und Filter (Klartext) fuer die Vorschau
    JObject GroupInfo(SqlConnection c, string gid, Dictionary<string, JObject> cache)
    {
        JObject g;
        if (gid == null) { g = new JObject(); g["id"] = null; g["name"] = "(kein Ordner)"; g["filter"] = null; g["filterText"] = null; return g; }
        if (cache.TryGetValue(gid, out g)) return g;
        g = new JObject(); g["id"] = gid; g["name"] = GroupName(c, gid) ?? "?";
        string root = null, expr = null, crit = null;
        var q = c.CreateCommand();
        q.CommandText = "SELECT CAST(RootGroup AS nvarchar(max)), Expression, CAST(Criterias AS nvarchar(max)) FROM SYS_Kiosk_Filters WHERE Id=@g";
        q.Parameters.AddWithValue("@g", G(gid));
        using (var r = q.ExecuteReader())
            if (r.Read()) { root = r.IsDBNull(0) ? "" : r.GetString(0); expr = r.IsDBNull(1) ? "" : r.GetString(1); crit = r.IsDBNull(2) ? "" : r.GetString(2); }
        string text = null;
        if (!string.IsNullOrEmpty(expr))
        {
            JObject fj = FilterJson(gid, root, expr, crit);
            JArray k = fj["kriterien"] as JArray;
            text = k != null && k.Count > 0 ? string.Join(" " + (string)fj["verknuepfung"] + " ", k.ToObject<string[]>()) : expr;
        }
        g["filter"] = string.IsNullOrEmpty(expr) ? null : expr; g["filterText"] = text;
        cache[gid] = g;
        return g;
    }

    static string VisText(JObject g)
    {
        return Tok(g["filter"]) == null ? "\"" + g["name"] + "\" (fuer alle sichtbar)" : "\"" + g["name"] + "\" (" + Tok(g["filterText"]) + ")";
    }

    // ================= Planen =================
    void PlanUpdate(SqlConnection c, JObject a, JObject p, List<string> lines)
    {
        JArray list = ChangeList(a["changes"]);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cache = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
        var planned = new JArray(); var body = new List<string>(); var skipped = new List<string>();
        int nMove = 0, nName = 0, nDesc = 0, nImg = 0, nFilter = 0;
        foreach (JObject ch in list)
        {
            string itemArg = Str(ch, "item");
            JObject it = ResolveItem(c, itemArg);
            if (it == null) throw new Exception("Kiosk-Eintrag \"" + itemArg + "\" nicht gefunden.");
            string iid = (string)it["id"];
            if (!seen.Add(iid)) throw new Exception("Eintrag \"" + it["name"] + "\" " + iid + " steht mehrfach in changes - je Eintrag nur ein Objekt.");
            JObject snap = ItemSnapshot(c, iid);
            JObject before = StateOf(snap);
            it["descriptionId"] = snap["item"]["DescriptionId"];
            string label = "\"" + it["name"] + "\" (" + KindText((int)it["kind"]) + ") " + iid;
            RequireAllowedGroup(Tok(before["groupId"]), "Eintrag " + label);
            JObject src = GroupInfo(c, Tok(before["groupId"]), cache), dst = src;
            var after = new JObject(); var sub = new List<string>();

            // Ordner
            string grpArg = Str(ch, "group");
            if (grpArg != null)
            {
                JObject g = ResolveGroup(c, grpArg);
                if (!string.Equals((string)g["id"], Tok(before["groupId"]), StringComparison.OrdinalIgnoreCase))
                {
                    RequireAllowedGroup((string)g["id"], "Ziel-Ordner \"" + g["name"] + "\"");
                    dst = GroupInfo(c, (string)g["id"], cache);
                    after["groupId"] = dst["id"];
                    sub.Add("Ordner: \"" + src["name"] + "\" -> \"" + dst["name"] + "\"");
                    nMove++;
                }
            }

            // Name
            bool hasDe = Has(ch, "nameDe"), hasEn = Has(ch, "nameEn");
            if (hasDe || hasEn)
            {
                string oDe = Tok(before["nameDe"]) ?? "", oEn = Tok(before["nameEn"]) ?? "";
                string nDe = hasDe ? Name(ch, "nameDe", null) : oDe;
                string nEn = hasEn ? Name(ch, "nameEn", null) : (hasDe ? FollowEn(oDe, oEn, nDe) : oEn);
                if (nDe != oDe || nEn != oEn)
                {
                    after["nameDe"] = nDe; after["nameEn"] = nEn; nName++;
                    sub.Add(NameLine(oDe, oEn, nDe, nEn));
                    if (nDe != oDe)
                    {
                        var q = c.CreateCommand();
                        q.CommandText = "SELECT COUNT(*) FROM SYS_Kiosk_Items i JOIN SYS_MultiLanguageItems m ON m.Id=i.Id WHERE m.Caption_de=@n AND i.Id<>@i";
                        q.Parameters.AddWithValue("@n", nDe); q.Parameters.AddWithValue("@i", G(iid));
                        int same = Convert.ToInt32(q.ExecuteScalar());
                        if (same > 0) sub.Add("Hinweis: " + same + " weitere(r) Eintrag/Eintraege heissen schon \"" + nDe + "\" - danach per GUID ansprechen.");
                    }
                }
            }

            // Beschreibung
            bool hasDd = Has(ch, "descriptionDe"), hasDen = Has(ch, "descriptionEn");
            if (hasDd || hasDen)
            {
                if (Tok(it["descriptionId"]) == null) throw new Exception(label + ": Eintrag hat keine Beschreibungs-Id (DescriptionId leer) - Beschreibung in der Console setzen.");
                string oD = Tok(before["descriptionDe"]) ?? "", oE = Tok(before["descriptionEn"]) ?? "";
                string nD = hasDd ? Desc(ch, "descriptionDe") : oD;
                string nE = hasDen ? Desc(ch, "descriptionEn") : (hasDd ? FollowEn(oD, oE, nD) : oE);
                if (nD != oD || nE != oE)
                {
                    after["descriptionDe"] = nD; after["descriptionEn"] = nE; nDesc++;
                    sub.Add("Beschreibung: " + Short(oD) + " -> " + Short(nD) + (nE != nD ? " (EN " + Short(nE) + ")" : ""));
                }
            }

            // Icon
            string imgArg = Str(ch, "image");
            if (imgArg != null)
            {
                string img = (string)ImageRow(c, imgArg)["Id"];
                string cur = Tok(before["imageId"]);
                if (!string.Equals(cur, img, StringComparison.OrdinalIgnoreCase))
                {
                    after["imageId"] = img; nImg++;
                    sub.Add("Icon: " + ImageLabel(c, cur) + " -> " + ImageLabel(c, img));
                }
            }

            // Eigener Filter
            bool clear = ch["clearFilter"] != null && ch["clearFilter"].Type == JTokenType.Boolean && (bool)ch["clearFilter"];
            bool hasF = Has(ch, "filter");
            if (clear && hasF) throw new Exception(label + ": entweder filter oder clearFilter=true, nicht beides.");
            string curF = Tok(before["filter"]);
            bool ownAfter = !string.IsNullOrEmpty(curF);
            if (hasF)
            {
                JObject f = BuildFilter(ch["filter"], iid, Lookup, NewGuid);
                if ((string)f["expression"] != curF)
                {
                    after["filter"] = f; nFilter++; ownAfter = true;
                    sub.Add("Eigener Filter: bisher " + (string.IsNullOrEmpty(curF) ? "keiner (wie Ordner)" : curF));
                    FilterLines(f, sub, "  neu: ");
                }
            }
            else if (clear && curF != null)
            {
                after["clearFilter"] = true; nFilter++; ownAfter = false;
                sub.Add("Eigener Filter ENTFERNEN (bisher " + (curF.Length == 0 ? "leer" : curF) + ") - danach sichtbar wie der Ordner.");
            }

            // Sichtbarkeit nach dem Verschieben
            if (after["groupId"] != null && Tok(src["filter"]) != Tok(dst["filter"]))
            {
                sub.Add("Sichtbarkeit Ordner: bisher " + VisText(src) + ", danach " + VisText(dst) + ".");
                if (!ownAfter) sub.Add(Tok(dst["filter"]) == null
                    ? "ACHTUNG: Der Eintrag hat keinen eigenen Filter - danach fuer ALLE Benutzer sichtbar."
                    : "Der Eintrag hat keinen eigenen Filter - danach sichtbar wie der neue Ordner.");
            }

            if (after.Count == 0) { skipped.Add(label); continue; }
            var pc = new JObject(); pc["item"] = it; pc["before"] = before; pc["after"] = after;
            planned.Add(pc);
            body.Add(planned.Count + ". " + label);
            foreach (string s in sub) body.Add("   " + s);
        }
        if (planned.Count == 0) throw new Exception("Nichts zu aendern - alle angegebenen Werte entsprechen schon dem jetzigen Stand.");
        CheckUpdateDuplicates(c, planned);
        p["action"] = "update_items"; p["batchId"] = NewGuid(); p["changes"] = planned;
        lines.Add(planned.Count + " Kiosk-Eintrag/-Eintraege aendern - eine Transaktion, vorher Sicherung aller beteiligten Eintraege (zurueck mit acmp_kiosk_restore).");
        lines.Add("Verschieben " + nMove + ", Name " + nName + ", Beschreibung " + nDesc + ", Icon " + nImg + ", Filter " + nFilter + ". Die verknuepften Client Commands und Pakete bleiben unveraendert.");
        lines.AddRange(body);
        foreach (string s in skipped) lines.Add("Unveraendert, uebersprungen: " + s);
    }

    // Derselbe Inhalt (Command bzw. Paket) darf in einem Ordner nur einmal stehen - wie beim Anlegen.
    // Eintraege, die im selben Auftrag den Ordner verlassen, zaehlen nicht.
    void CheckUpdateDuplicates(SqlConnection c, JArray planned)
    {
        var finalGroup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (JObject pc in planned) finalGroup[(string)pc["item"]["id"]] = Tok(pc["after"]["groupId"]) ?? Tok(pc["before"]["groupId"]);
        var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (JObject pc in planned)
        {
            string gid = Tok(pc["after"]["groupId"]), content = Tok(pc["item"]["contentId"]);
            if (gid == null || content == null) continue;
            int kind = (int)pc["item"]["kind"];
            string key = gid + "|" + kind + "|" + content, other;
            if (targets.TryGetValue(key, out other))
                throw new Exception("\"" + pc["item"]["name"] + "\" und \"" + other + "\" zeigen auf denselben Inhalt und sollen beide in denselben Ordner - nur einen davon verschieben.");
            targets[key] = (string)pc["item"]["name"];
            var q = c.CreateCommand();
            q.CommandText = "SELECT CAST(i.Id AS varchar(36)), m.Caption_de FROM SYS_Kiosk_Items i LEFT JOIN SYS_MultiLanguageItems m ON m.Id=i.Id WHERE i.GroupId=@g AND i.ItemKind=@k AND i.ContentId=@c";
            q.Parameters.AddWithValue("@g", G(gid)); q.Parameters.AddWithValue("@k", (byte)kind); q.Parameters.AddWithValue("@c", G(content));
            using (var r = q.ExecuteReader())
                while (r.Read())
                {
                    string oid = "{" + r.GetString(0).ToUpperInvariant() + "}", fin;
                    if (string.Equals(oid, (string)pc["item"]["id"], StringComparison.OrdinalIgnoreCase)) continue;
                    if (finalGroup.TryGetValue(oid, out fin) && !string.Equals(fin, gid, StringComparison.OrdinalIgnoreCase)) continue;
                    throw new Exception("\"" + pc["item"]["name"] + "\": Im Ziel-Ordner steht derselbe Inhalt schon (\"" + (r.IsDBNull(1) ? "" : r.GetString(1)) + "\" " + oid +
                        ") - den doppelten Eintrag vorher entfernen (acmp_kiosk_remove_item) oder diesen nicht verschieben.");
                }
        }
    }

    // ================= Schreiben =================
    JObject ExecUpdate(JObject p, string requester)
    {
        JArray changes = (JArray)p["changes"];
        string batch = (string)p["batchId"], backup;
        using (var c = new SqlConnection(_writeCs))
        {
            c.Open();
            var snaps = new JArray();
            foreach (JObject pc in changes)
            {
                string iid = (string)pc["item"]["id"], name = (string)pc["item"]["name"];
                if (ResolveItem(c, iid) == null) throw new Exception("Eintrag \"" + name + "\" " + iid + " gibt es nicht mehr - nichts geschrieben.");
                JObject snap = ItemSnapshot(c, iid);
                if (!JToken.DeepEquals(StateOf(snap), pc["before"]))
                    throw new Exception("Eintrag \"" + name + "\" wurde seit der Anfrage geaendert (Ordner, Name, Beschreibung, Icon oder Filter) - nichts geschrieben, bitte neu beantragen.");
                RequireAllowedGroup(Tok(pc["before"]["groupId"]), "Eintrag \"" + name + "\"");
                JObject after = (JObject)pc["after"];
                string gid = Tok(after["groupId"]), img = Tok(after["imageId"]);
                if (gid != null)
                {
                    RequireAllowedGroup(gid, "Ziel-Ordner " + gid);
                    if (GroupName(c, gid) == null) throw new Exception("Ziel-Ordner " + gid + " gibt es nicht mehr - nichts geschrieben.");
                }
                if (img != null && !ImageExists(c, img)) throw new Exception("Icon " + img + " gibt es nicht mehr - nichts geschrieben.");
                snaps.Add(snap);
            }
            CheckUpdateDuplicates(c, changes);
            var b = new JObject(); b["kind"] = "items"; b["grund"] = "vor acmp_kiosk_update_items"; b["von"] = requester; b["zeit"] = DateTime.Now.ToString("s");
            b["items"] = snaps;
            backup = SaveBackup(b, batch, "items");
            using (SqlTransaction tx = c.BeginTransaction())
            {
                foreach (JObject pc in changes) ApplyChange(c, tx, pc);
                tx.Commit();
            }
        }
        string journal = WriteJournal("itemsupdate", batch, p, requester);
        var res = new JArray();
        foreach (JObject pc in changes)
        {
            JObject after = (JObject)pc["after"];
            var r = new JObject(); r["id"] = pc["item"]["id"]; r["name"] = Tok(after["nameDe"]) ?? (string)pc["item"]["name"];
            var what = new List<string>();
            if (after["groupId"] != null) what.Add("Ordner");
            if (after["nameDe"] != null) what.Add("Name");
            if (after["descriptionDe"] != null) what.Add("Beschreibung");
            if (after["imageId"] != null) what.Add("Icon");
            if (after["filter"] != null || after["clearFilter"] != null) what.Add("Filter");
            r["geaendert"] = string.Join(", ", what.ToArray());
            res.Add(r);
        }
        _log("acmp_kiosk_update_items von " + requester + ": " + changes.Count + " Eintraege, Sicherung " + Path.GetFileName(backup));
        var o = new JObject();
        o["ok"] = true; o["anzahl"] = changes.Count; o["eintraege"] = res;
        o["sicherung"] = Path.GetFileName(backup); o["protokoll"] = Path.GetFileName(journal);
        o["hinweis"] = "Im Kiosk nach dem Aktualisieren sichtbar. Zurueck mit acmp_kiosk_restore backup=\"" + Path.GetFileName(backup) + "\".";
        return o;
    }

    void ApplyChange(SqlConnection c, SqlTransaction tx, JObject pc)
    {
        string iid = (string)pc["item"]["id"], name = (string)pc["item"]["name"];
        JObject before = (JObject)pc["before"], after = (JObject)pc["after"];
        if (after["groupId"] != null)
        {
            var q = Cmd(c, tx, "UPDATE SYS_Kiosk_Items SET GroupId=@new WHERE Id=@id AND GroupId=@old");
            q.Parameters.AddWithValue("@new", G((string)after["groupId"])); q.Parameters.AddWithValue("@id", G(iid)); q.Parameters.AddWithValue("@old", G((string)before["groupId"]));
            if (q.ExecuteNonQuery() != 1) throw new Exception("\"" + name + "\": Ordner nicht eindeutig geaendert - zurueckgerollt, nichts geschrieben.");
        }
        if (after["imageId"] != null)
        {
            string old = Tok(before["imageId"]);
            var q = Cmd(c, tx, "UPDATE SYS_Kiosk_Items SET ImageId=@new WHERE Id=@id AND " + (old != null ? "ImageId=@old" : "ImageId IS NULL"));
            q.Parameters.AddWithValue("@new", G((string)after["imageId"])); q.Parameters.AddWithValue("@id", G(iid));
            if (old != null) q.Parameters.AddWithValue("@old", G(old));
            if (q.ExecuteNonQuery() != 1) throw new Exception("\"" + name + "\": Icon nicht eindeutig geaendert - zurueckgerollt, nichts geschrieben.");
        }
        if (after["nameDe"] != null) UpsertText(c, tx, "SYS_MultiLanguageItems", iid, (string)after["nameDe"], (string)after["nameEn"]);
        if (after["descriptionDe"] != null) UpsertText(c, tx, "SYS_MultiLanguageItems_Memo", (string)pc["item"]["descriptionId"], (string)after["descriptionDe"], (string)after["descriptionEn"]);
        if (after["filter"] is JObject || after["clearFilter"] != null)
        {
            var q = Cmd(c, tx, "DELETE FROM SYS_Kiosk_Filters WHERE Id=@id");   // PropertyNames per CASCADE
            q.Parameters.AddWithValue("@id", G(iid)); q.ExecuteNonQuery();
            if (after["filter"] is JObject) InsertFilter(c, tx, iid, (JObject)after["filter"]);
        }
    }

    void UpsertText(SqlConnection c, SqlTransaction tx, string table, string id, string de, string en)
    {
        var q = Cmd(c, tx, "UPDATE " + table + " SET Caption_de=@de, Caption_en=@en WHERE Id=@id");
        q.Parameters.AddWithValue("@id", G(id));
        q.Parameters.AddWithValue("@de", (object)de ?? DBNull.Value); q.Parameters.AddWithValue("@en", (object)en ?? DBNull.Value);
        int n = q.ExecuteNonQuery();
        if (n == 0) { var t = new JObject(); t["de"] = de; t["en"] = en; InsertText(c, tx, table, id, t); }
        else if (n != 1) throw new Exception(table + ": mehr als eine Zeile fuer " + id + " - zurueckgerollt, nichts geschrieben.");
    }

    // ================= Zurueck (Sicherung der Art "items") =================
    void PlanRestoreItems(SqlConnection c, JObject b, JObject p, List<string> lines)
    {
        JArray items = b["items"] as JArray;
        if (items == null || items.Count == 0) throw new Exception(p["backup"] + " enthaelt keine Eintraege.");
        var cache = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
        var todo = new JArray(); var body = new List<string>();
        foreach (JObject snap in items)
        {
            string iid = (string)snap["item"]["Id"];
            JObject was = StateOf(snap);
            JObject it = ResolveItem(c, iid);
            if (it == null) throw new Exception("Eintrag " + iid + " (\"" + Tok(was["nameDe"]) + "\") gibt es nicht mehr - zurueck nur fuer vorhandene Eintraege (einen entfernten mit seiner eigenen Sicherung zurueckholen).");
            JObject now = StateOf(ItemSnapshot(c, iid));
            if (JToken.DeepEquals(now, was)) continue;
            RequireAllowedGroup(Tok(now["groupId"]), "Eintrag \"" + it["name"] + "\"");
            RequireAllowedGroup(Tok(was["groupId"]), "Gesicherter Ordner von \"" + it["name"] + "\"");
            if (GroupName(c, Tok(was["groupId"])) == null) throw new Exception("Der gesicherte Ordner " + was["groupId"] + " von \"" + it["name"] + "\" existiert nicht mehr.");
            if (Tok(was["imageId"]) != null && !ImageExists(c, Tok(was["imageId"]))) throw new Exception("Das gesicherte Icon " + was["imageId"] + " von \"" + it["name"] + "\" existiert nicht mehr.");
            var e = new JObject(); e["id"] = iid; e["name"] = it["name"]; e["now"] = now;
            todo.Add(e);
            body.Add(todo.Count + ". \"" + it["name"] + "\" " + iid);
            if (Tok(now["groupId"]) != Tok(was["groupId"])) body.Add("   Ordner: \"" + GroupInfo(c, Tok(now["groupId"]), cache)["name"] + "\" -> \"" + GroupInfo(c, Tok(was["groupId"]), cache)["name"] + "\"");
            if (Tok(now["nameDe"]) != Tok(was["nameDe"]) || Tok(now["nameEn"]) != Tok(was["nameEn"])) body.Add("   " + NameLine(Tok(now["nameDe"]), Tok(now["nameEn"]), Tok(was["nameDe"]), Tok(was["nameEn"])));
            if (Tok(now["descriptionDe"]) != Tok(was["descriptionDe"]) || Tok(now["descriptionEn"]) != Tok(was["descriptionEn"])) body.Add("   Beschreibung: " + Short(Tok(now["descriptionDe"])) + " -> " + Short(Tok(was["descriptionDe"])));
            if (Tok(now["imageId"]) != Tok(was["imageId"])) body.Add("   Icon: " + ImageLabel(c, Tok(now["imageId"])) + " -> " + ImageLabel(c, Tok(was["imageId"])));
            if (Tok(now["filter"]) != Tok(was["filter"])) body.Add("   Eigener Filter: " + (Tok(now["filter"]) ?? "keiner") + " -> " + (Tok(was["filter"]) ?? "keiner"));
        }
        if (todo.Count == 0) throw new Exception("Alle Eintraege aus " + p["backup"] + " sind schon auf dem gesicherten Stand - nichts zu tun.");
        p["items"] = todo;
        lines.Add(todo.Count + " Kiosk-Eintrag/-Eintraege auf den Stand der Sicherung " + p["backup"] + " zuruecksetzen (" + b["zeit"] + ", " + b["grund"] + "); vorher wird der jetzige Stand gesichert.");
        lines.AddRange(body);
    }

    JObject ExecRestoreItems(JObject b, JObject p, string requester)
    {
        var saved = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
        foreach (JObject snap in (JArray)b["items"]) saved[(string)snap["item"]["Id"]] = snap;
        JArray todo = (JArray)p["items"];
        string again;
        using (var c = new SqlConnection(_writeCs))
        {
            c.Open();
            var snapsNow = new JArray();
            foreach (JObject e in todo)
            {
                string iid = (string)e["id"];
                JObject was;
                if (!saved.TryGetValue(iid, out was)) throw new Exception("Eintrag " + iid + " steht nicht in der Sicherung - nichts geschrieben.");
                if (ResolveItem(c, iid) == null) throw new Exception("Eintrag \"" + e["name"] + "\" gibt es nicht mehr - nichts geschrieben.");
                JObject sn = ItemSnapshot(c, iid);
                if (!JToken.DeepEquals(StateOf(sn), e["now"])) throw new Exception("Eintrag \"" + e["name"] + "\" wurde seit der Anfrage geaendert - nichts geschrieben, bitte neu beantragen.");
                string gid = Tok(was["item"]["GroupId"]);
                RequireAllowedGroup(Tok(e["now"]["groupId"]), "Eintrag \"" + e["name"] + "\"");
                RequireAllowedGroup(gid, "Gesicherter Ordner von \"" + e["name"] + "\"");
                if (GroupName(c, gid) == null) throw new Exception("Der gesicherte Ordner " + gid + " existiert nicht mehr - nichts geschrieben.");
                snapsNow.Add(sn);
            }
            var nb = new JObject(); nb["kind"] = "items"; nb["grund"] = "vor acmp_kiosk_restore " + p["backup"]; nb["von"] = requester; nb["zeit"] = DateTime.Now.ToString("s");
            nb["items"] = snapsNow;
            again = SaveBackup(nb, NewGuid(), "items");
            using (SqlTransaction tx = c.BeginTransaction())
            {
                foreach (JObject e in todo)
                {
                    string iid = (string)e["id"];
                    JObject was = saved[iid], wi = (JObject)was["item"];
                    var q = Cmd(c, tx, "UPDATE SYS_Kiosk_Items SET GroupId=@g, ImageId=@img WHERE Id=@id");
                    q.Parameters.AddWithValue("@g", NG(Tok(wi["GroupId"]))); q.Parameters.AddWithValue("@img", NG(Tok(wi["ImageId"]))); q.Parameters.AddWithValue("@id", G(iid));
                    if (q.ExecuteNonQuery() != 1) throw new Exception("\"" + e["name"] + "\" nicht eindeutig zurueckgesetzt - zurueckgerollt, nichts geschrieben.");
                    JObject n = was["names"] as JObject, m = was["memo"] as JObject;
                    if (n != null) UpsertText(c, tx, "SYS_MultiLanguageItems", iid, Tok(n["de"]), Tok(n["en"]));
                    if (m != null && Tok(wi["DescriptionId"]) != null) UpsertText(c, tx, "SYS_MultiLanguageItems_Memo", Tok(wi["DescriptionId"]), Tok(m["de"]), Tok(m["en"]));
                    if (Tok(e["now"]["filter"]) != Tok(StateOf(was)["filter"]))
                    {
                        var dq = Cmd(c, tx, "DELETE FROM SYS_Kiosk_Filters WHERE Id=@id");   // PropertyNames per CASCADE
                        dq.Parameters.AddWithValue("@id", G(iid)); dq.ExecuteNonQuery();
                        RestoreFilterRows(c, tx, was);
                    }
                }
                tx.Commit();
            }
        }
        _log("acmp_kiosk_restore von " + requester + ": " + p["backup"] + " (" + todo.Count + " Eintraege)");
        var o = new JObject(); o["ok"] = true; o["wiederhergestellt"] = p["backup"]; o["anzahl"] = todo.Count;
        o["sicherungVorher"] = Path.GetFileName(again);
        o["hinweis"] = "Im Kiosk nach dem Aktualisieren sichtbar. Diese Ruecknahme selbst laesst sich mit acmp_kiosk_restore backup=\"" + Path.GetFileName(again) + "\" wieder umkehren.";
        return o;
    }
}
