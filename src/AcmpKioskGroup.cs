// AcmpKioskGroup: einen Kiosk-Ordner aendern - umbenennen (DE/EN), Icon und Sichtbarkeitsfilter in EINEM Auftrag (acmp_kiosk_update_group).
// Grundlage: research\kiosk.md, "Ordner anlegen" und "Kiosk-Umbau 15.10.2026" (die Umbenennung in der Console kam dort nicht in der DB an).
// Der Ordnername steht in SYS_MultiLanguageItems (Id = Ordner-Id), das Icon in SYS_Kiosk_Groups.ImageId, der Filter in SYS_Kiosk_Filters (Id = Ordner-Id).
//  - Nur Ordner aus kioskWriter.allowedGroupIds und vom MCP angelegte Ordner. Beides haengt an der Ordner-Id, nicht am Namen.
//  - Ordnernamen bleiben eindeutig (wie bei acmp_kiosk_add_group).
//  - Vorher Sicherung (Art "group": Ordnerzeile, Namen, Filter); zurueck mit acmp_kiosk_restore.
//  - Bei der Ausfuehrung wird der Ordner mit dem Stand der Anfrage verglichen; weicht er ab, wird nichts geschrieben.
//  - Loeschen und Verschieben (ParentId) kann das Tool nicht. Loeschen nur in der Console (CASCADE auf alle Eintraege).
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using Newtonsoft.Json.Linq;

partial class AcmpKiosk
{
    static readonly string[][] GroupUpdatePerms = { new[] { "SYS_MultiLanguageItems", "UPDATE" }, new[] { "SYS_Kiosk_Groups", "UPDATE" } };

    // ================= Vorpruefung (sperrt nur dieses Tool) =================
    bool CheckGroupUpdatePerms(JArray checks, SqlConnection wc)
    {
        var miss = new List<string>();
        foreach (string[] p in GroupUpdatePerms) if (!HasPerm(wc, p[0], p[1])) miss.Add(p[0] + " " + p[1]);
        Add(checks, "Rechte Ordner", miss.Count == 0 ? "ok" : "warn", miss.Count == 0
            ? "UPDATE auf Ordner und Namen vorhanden (acmp_kiosk_update_group)"
            : "fehlen: " + string.Join(", ", miss.ToArray()) + " - acmp_kiosk_update_group gesperrt (grant-kiosk-writer.sql)");
        return miss.Count == 0;
    }

    public void CheckGroupWrite()
    {
        JObject pf = Preflight(false);
        if (pf == null || pf["groupTool"] == null || !(bool)pf["groupTool"])
            throw new Exception("acmp_kiosk_update_group gesperrt - Vorpruefung \"Rechte Ordner\" nicht bestanden (Details: acmp_kiosk_tree mit status=true). Nichts geschrieben.");
    }

    // ================= Hilfen (ohne DB, offline getestet in KioskTest.cs) =================
    // Mindestens eine Aenderung, filter und clearFilter nicht zusammen
    public static void CheckGroupArgs(JObject a)
    {
        if (Str(a, "group") == null) throw new Exception("group fehlt (Kiosk-Ordner: GUID oder Name, siehe acmp_kiosk_tree).");
        bool clear = a["clearFilter"] != null && a["clearFilter"].Type == JTokenType.Boolean && (bool)a["clearFilter"];
        if (!Has(a, "nameDe") && !Has(a, "nameEn") && Str(a, "image") == null && !Has(a, "filter") && !clear)
            throw new Exception("Nichts zu aendern angegeben - mindestens eines von nameDe, nameEn, image, filter oder clearFilter=true.");
        if (clear && Has(a, "filter")) throw new Exception("Entweder filter oder clearFilter=true, nicht beides.");
    }

    // Stand eines Ordners zum Vergleichen: Icon, Namen, Filter (Ausdruck, null = keine Filterzeile)
    public static JObject GroupStateOf(JObject snap)
    {
        var s = new JObject();
        JObject g = (JObject)snap["group"];
        JObject n = snap["names"] as JObject, f = snap["filter"] as JObject;
        s["imageId"] = g["ImageId"];
        s["nameDe"] = n != null ? n["de"] : null; s["nameEn"] = n != null ? n["en"] : null;
        s["filter"] = f != null ? f["Expression"] : null;
        return s;
    }

    // ================= Lesen =================
    // Ordnerzeile, Namen und Filter (Inhalt einer Sicherung der Art "group")
    JObject GroupSnapshot(SqlConnection c, string id)
    {
        var b = new JObject();
        var q = c.CreateCommand();
        q.CommandText = "SELECT CAST(Id AS varchar(36)), CAST(ParentId AS varchar(36)), CAST(ImageId AS varchar(36)), CAST(TenantId AS varchar(36)), TenantNo FROM SYS_Kiosk_Groups WHERE Id=@g";
        q.Parameters.AddWithValue("@g", G(id));
        using (var r = q.ExecuteReader())
        {
            if (!r.Read()) throw new Exception("Kiosk-Ordner " + id + " nicht gefunden.");
            var g = new JObject();
            Func<int, JToken> gs = delegate(int i) { return r.IsDBNull(i) ? null : (JToken)("{" + r.GetString(i).ToUpperInvariant() + "}"); };
            g["Id"] = gs(0); g["ParentId"] = gs(1); g["ImageId"] = gs(2); g["TenantId"] = gs(3); g["TenantNo"] = r.IsDBNull(4) ? null : (JToken)r.GetInt32(4);
            b["group"] = g;
        }
        b["names"] = ReadText(c, "SYS_MultiLanguageItems", id);
        ReadFilterRows(c, id, b);
        return b;
    }

    // Anderer Ordner mit diesem deutschen Namen (null = keiner)
    static string OtherGroupWithName(SqlConnection c, string name, string exceptId)
    {
        var q = c.CreateCommand();
        q.CommandText = "SELECT TOP 1 CAST(g.Id AS varchar(36)) FROM SYS_Kiosk_Groups g JOIN SYS_MultiLanguageItems m ON m.Id=g.Id WHERE m.Caption_de=@n AND g.Id<>@g";
        q.Parameters.AddWithValue("@n", name); q.Parameters.AddWithValue("@g", G(exceptId));
        object o = q.ExecuteScalar();
        return o == null || o is DBNull ? null : "{" + ((string)o).ToUpperInvariant() + "}";
    }

    // Eintraege im Ordner: alle / mit eigenem (nicht leerem) Filter
    static void CountGroupItems(SqlConnection c, string gid, out int all, out int own)
    {
        var q = c.CreateCommand();
        q.CommandText = "SELECT COUNT(*), SUM(CASE WHEN ISNULL(f.Expression, '') <> '' THEN 1 ELSE 0 END) FROM SYS_Kiosk_Items i LEFT JOIN SYS_Kiosk_Filters f ON f.Id=i.Id WHERE i.GroupId=@g";
        q.Parameters.AddWithValue("@g", G(gid));
        using (var r = q.ExecuteReader())
        {
            r.Read();
            all = r.GetInt32(0); own = r.IsDBNull(1) ? 0 : r.GetInt32(1);
        }
    }

    // ================= Planen =================
    void PlanUpdateGroup(SqlConnection c, JObject a, JObject p, List<string> lines)
    {
        CheckGroupArgs(a);
        JObject g = ResolveGroup(c, Str(a, "group"));
        string gid = (string)g["id"], label = "\"" + g["name"] + "\" " + gid;
        RequireAllowedGroup(gid, "Kiosk-Ordner " + label);
        JObject before = GroupStateOf(GroupSnapshot(c, gid));
        var after = new JObject(); var sub = new List<string>();
        int nAll, nOwn; CountGroupItems(c, gid, out nAll, out nOwn);

        // Name
        bool hasDe = Has(a, "nameDe"), hasEn = Has(a, "nameEn");
        if (hasDe || hasEn)
        {
            string oDe = Tok(before["nameDe"]) ?? "", oEn = Tok(before["nameEn"]) ?? "";
            string nDe = hasDe ? Name(a, "nameDe", null) : oDe;
            string nEn = hasEn ? Name(a, "nameEn", null) : (hasDe ? FollowEn(oDe, oEn, nDe) : oEn);
            if (nDe != oDe || nEn != oEn)
            {
                if (nDe != oDe)
                {
                    string other = OtherGroupWithName(c, nDe, gid);
                    if (other != null) throw new Exception("Einen Kiosk-Ordner \"" + nDe + "\" gibt es schon (" + other + ").");
                }
                after["nameDe"] = nDe; after["nameEn"] = nEn;
                sub.Add(NameLine(oDe, oEn, nDe, nEn));
                if (nDe != oDe)
                {
                    var iq = c.CreateCommand();
                    iq.CommandText = "SELECT COUNT(*) FROM SYS_Kiosk_Items i JOIN SYS_MultiLanguageItems m ON m.Id=i.Id WHERE m.Caption_de=@n";
                    iq.Parameters.AddWithValue("@n", nDe);
                    if (Convert.ToInt32(iq.ExecuteScalar()) > 0) sub.Add("Hinweis: Ein Kiosk-Eintrag heisst ebenfalls \"" + nDe + "\" - bei target= fuer den Ordner die GUID angeben.");
                }
            }
        }

        // Icon
        string imgArg = Str(a, "image");
        if (imgArg != null)
        {
            string img = (string)ImageRow(c, imgArg)["Id"];
            string cur = Tok(before["imageId"]);
            if (!string.Equals(cur, img, StringComparison.OrdinalIgnoreCase))
            {
                after["imageId"] = img;
                sub.Add("Icon: " + ImageLabel(c, cur) + " -> " + ImageLabel(c, img));
            }
        }

        // Filter des Ordners
        bool clear = a["clearFilter"] != null && a["clearFilter"].Type == JTokenType.Boolean && (bool)a["clearFilter"];
        string curF = Tok(before["filter"]);
        string curText = Tok(GroupInfo(c, gid, new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase))["filterText"]) ?? curF;
        if (Has(a, "filter"))
        {
            JObject f = BuildFilter(a["filter"], gid, Lookup, NewGuid);
            if ((string)f["expression"] != curF)
            {
                after["filter"] = f;
                sub.Add("Sichtbarkeit Ordner: bisher " + (string.IsNullOrEmpty(curF) ? "fuer alle (kein Filter)" : curText));
                FilterLines(f, sub, "  neu: ");
            }
        }
        else if (clear && curF != null)
        {
            after["clearFilter"] = true;
            sub.Add(curF.Length == 0
                ? "Leere Filterzeile des Ordners entfernen - Sichtbarkeit bleibt: fuer alle."
                : "Filter des Ordners ENTFERNEN (bisher " + curText + ") - ACHTUNG: Ordner danach fuer ALLE Benutzer sichtbar.");
        }
        if (after["filter"] != null || (after["clearFilter"] != null && !string.IsNullOrEmpty(curF)))
            sub.Add("Im Ordner stehen " + nAll + " Eintraege, " + (nAll - nOwn) + " davon ohne eigenen Filter - diese sind danach sichtbar wie der Ordner.");

        if (after.Count == 0) throw new Exception("Nichts zu aendern - alle angegebenen Werte entsprechen schon dem jetzigen Stand von " + label + ".");
        p["action"] = "update_group"; p["group"] = g; p["before"] = before; p["after"] = after;
        lines.Add("Kiosk-Ordner " + label + " aendern - eine Transaktion, vorher Sicherung des Ordners (zurueck mit acmp_kiosk_restore).");
        foreach (string s in sub) lines.Add("   " + s);
        lines.Add("Die " + nAll + " Eintraege im Ordner bleiben unveraendert. Die Schreibfreigabe des MCP fuer den Ordner haengt an der Id und bleibt.");
        if (after["nameDe"] != null) lines.Add("Danach den Ordner per GUID oder neuem Namen ansprechen (group=\"" + gid + "\"); der alte Name findet ihn nicht mehr.");
    }

    // ================= Schreiben =================
    JObject ExecUpdateGroup(JObject p, string requester)
    {
        string gid = (string)p["group"]["id"], name = (string)p["group"]["name"], backup;
        JObject before = (JObject)p["before"], after = (JObject)p["after"];
        RequireAllowedGroup(gid, "Kiosk-Ordner \"" + name + "\"");
        using (var c = new SqlConnection(_writeCs))
        {
            c.Open();
            if (GroupName(c, gid) == null) throw new Exception("Kiosk-Ordner \"" + name + "\" " + gid + " gibt es nicht mehr - nichts geschrieben.");
            JObject snap = GroupSnapshot(c, gid);
            if (!JToken.DeepEquals(GroupStateOf(snap), before))
                throw new Exception("Kiosk-Ordner \"" + name + "\" wurde seit der Anfrage geaendert (Name, Icon oder Filter) - nichts geschrieben, bitte neu beantragen.");
            string nde = Tok(after["nameDe"]), img = Tok(after["imageId"]);
            if (nde != null && nde != Tok(before["nameDe"]))
            {
                string other = OtherGroupWithName(c, nde, gid);
                if (other != null) throw new Exception("Einen Kiosk-Ordner \"" + nde + "\" gibt es inzwischen (" + other + ") - nichts geschrieben, bitte neu beantragen.");
            }
            if (img != null && !ImageExists(c, img)) throw new Exception("Icon " + img + " gibt es nicht mehr - nichts geschrieben.");
            var b = new JObject(); b["kind"] = "group"; b["grund"] = "vor acmp_kiosk_update_group"; b["von"] = requester; b["zeit"] = DateTime.Now.ToString("s");
            foreach (JProperty pr in snap.Properties()) b[pr.Name] = pr.Value;
            backup = SaveBackup(b, gid, "group");
            using (SqlTransaction tx = c.BeginTransaction())
            {
                if (img != null)
                {
                    string old = Tok(before["imageId"]);
                    var q = Cmd(c, tx, "UPDATE SYS_Kiosk_Groups SET ImageId=@new WHERE Id=@id AND " + (old != null ? "ImageId=@old" : "ImageId IS NULL"));
                    q.Parameters.AddWithValue("@new", G(img)); q.Parameters.AddWithValue("@id", G(gid));
                    if (old != null) q.Parameters.AddWithValue("@old", G(old));
                    if (q.ExecuteNonQuery() != 1) throw new Exception("Icon des Ordners nicht eindeutig geaendert - zurueckgerollt, nichts geschrieben.");
                }
                if (nde != null) UpsertText(c, tx, "SYS_MultiLanguageItems", gid, nde, Tok(after["nameEn"]));
                if (after["filter"] is JObject || after["clearFilter"] != null)
                {
                    var q = Cmd(c, tx, "DELETE FROM SYS_Kiosk_Filters WHERE Id=@id");   // PropertyNames per CASCADE
                    q.Parameters.AddWithValue("@id", G(gid)); q.ExecuteNonQuery();
                    if (after["filter"] is JObject) InsertFilter(c, tx, gid, (JObject)after["filter"]);
                }
                tx.Commit();
            }
        }
        string journal = WriteJournal("groupupdate", gid, p, requester);
        var what = new List<string>();
        if (after["nameDe"] != null) what.Add("Name");
        if (after["imageId"] != null) what.Add("Icon");
        if (after["filter"] != null || after["clearFilter"] != null) what.Add("Filter");
        _log("acmp_kiosk_update_group von " + requester + ": " + gid + " (" + string.Join(", ", what.ToArray()) + "), Sicherung " + Path.GetFileName(backup));
        var o = new JObject();
        o["ok"] = true; o["ordnerId"] = gid; o["name"] = Tok(after["nameDe"]) ?? name;
        if (after["nameDe"] != null) { o["nameEn"] = after["nameEn"]; o["vorher"] = name; }
        o["geaendert"] = string.Join(", ", what.ToArray());
        o["sicherung"] = Path.GetFileName(backup); o["protokoll"] = Path.GetFileName(journal);
        o["hinweis"] = "Im Kiosk nach dem Aktualisieren sichtbar. Zurueck mit acmp_kiosk_restore backup=\"" + Path.GetFileName(backup) + "\".";
        return o;
    }

    // ================= Zurueck (Sicherung der Art "group") =================
    void PlanRestoreGroup(SqlConnection c, JObject b, JObject p, List<string> lines)
    {
        JObject bg = b["group"] as JObject;
        if (bg == null || Tok(bg["Id"]) == null) throw new Exception(p["backup"] + " enthaelt keinen Ordner.");
        string gid = (string)bg["Id"];
        JObject was = GroupStateOf(b);
        if (GroupName(c, gid) == null) throw new Exception("Kiosk-Ordner " + gid + " (\"" + Tok(was["nameDe"]) + "\") gibt es nicht mehr - zurueck nur fuer vorhandene Ordner (neu anlegen: acmp_kiosk_add_group).");
        JObject now = GroupStateOf(GroupSnapshot(c, gid));
        string label = "\"" + Tok(now["nameDe"]) + "\" " + gid;
        RequireAllowedGroup(gid, "Kiosk-Ordner " + label);
        bool names = b["names"] is JObject && (Tok(now["nameDe"]) != Tok(was["nameDe"]) || Tok(now["nameEn"]) != Tok(was["nameEn"]));
        bool icon = Tok(now["imageId"]) != Tok(was["imageId"]), filter = Tok(now["filter"]) != Tok(was["filter"]);
        if (!names && !icon && !filter) throw new Exception("Kiosk-Ordner " + label + " ist schon auf dem Stand von " + p["backup"] + " - nichts zu tun.");
        if (names && Tok(was["nameDe"]) != null && Tok(was["nameDe"]) != Tok(now["nameDe"]))
        {
            string other = OtherGroupWithName(c, Tok(was["nameDe"]), gid);
            if (other != null) throw new Exception("Den gesicherten Namen \"" + Tok(was["nameDe"]) + "\" traegt inzwischen ein anderer Kiosk-Ordner (" + other + ").");
        }
        if (icon && Tok(was["imageId"]) != null && !ImageExists(c, Tok(was["imageId"]))) throw new Exception("Das gesicherte Icon " + was["imageId"] + " gibt es nicht mehr.");
        p["groupId"] = gid; p["now"] = now;
        lines.Add("Kiosk-Ordner " + label + " auf den Stand der Sicherung " + p["backup"] + " zuruecksetzen (" + b["zeit"] + ", " + b["grund"] + "); vorher wird der jetzige Stand gesichert.");
        if (names) lines.Add("   " + NameLine(Tok(now["nameDe"]), Tok(now["nameEn"]), Tok(was["nameDe"]), Tok(was["nameEn"])));
        if (icon) lines.Add("   Icon: " + ImageLabel(c, Tok(now["imageId"])) + " -> " + ImageLabel(c, Tok(was["imageId"])));
        if (filter) lines.Add("   Filter des Ordners: " + (Tok(now["filter"]) ?? "keiner") + " -> " + (Tok(was["filter"]) ?? "keiner") +
            (string.IsNullOrEmpty(Tok(was["filter"])) ? " - ACHTUNG: Ordner danach fuer ALLE Benutzer sichtbar." : ""));
        lines.Add("Die Eintraege im Ordner bleiben unveraendert.");
    }

    JObject ExecRestoreGroup(JObject b, JObject p, string requester)
    {
        string gid = (string)p["groupId"], again;
        JObject was = GroupStateOf(b), now = (JObject)p["now"];
        if (!string.Equals(gid, Tok(b["group"]["Id"]), StringComparison.OrdinalIgnoreCase)) throw new Exception("Ordner der Anfrage passt nicht zur Sicherung - nichts geschrieben.");
        RequireAllowedGroup(gid, "Kiosk-Ordner " + gid);
        using (var c = new SqlConnection(_writeCs))
        {
            c.Open();
            if (GroupName(c, gid) == null) throw new Exception("Kiosk-Ordner " + gid + " gibt es nicht mehr - nichts geschrieben.");
            JObject sn = GroupSnapshot(c, gid);
            if (!JToken.DeepEquals(GroupStateOf(sn), now)) throw new Exception("Kiosk-Ordner " + gid + " wurde seit der Anfrage geaendert - nichts geschrieben, bitte neu beantragen.");
            bool names = b["names"] is JObject && (Tok(now["nameDe"]) != Tok(was["nameDe"]) || Tok(now["nameEn"]) != Tok(was["nameEn"]));
            if (names && Tok(was["nameDe"]) != null && Tok(was["nameDe"]) != Tok(now["nameDe"]) && OtherGroupWithName(c, Tok(was["nameDe"]), gid) != null)
                throw new Exception("Den gesicherten Namen \"" + Tok(was["nameDe"]) + "\" traegt inzwischen ein anderer Kiosk-Ordner - nichts geschrieben.");
            var nb = new JObject(); nb["kind"] = "group"; nb["grund"] = "vor acmp_kiosk_restore " + p["backup"]; nb["von"] = requester; nb["zeit"] = DateTime.Now.ToString("s");
            foreach (JProperty pr in sn.Properties()) nb[pr.Name] = pr.Value;
            again = SaveBackup(nb, gid, "group");
            using (SqlTransaction tx = c.BeginTransaction())
            {
                if (names) UpsertText(c, tx, "SYS_MultiLanguageItems", gid, Tok(was["nameDe"]), Tok(was["nameEn"]));
                if (Tok(now["imageId"]) != Tok(was["imageId"]))
                {
                    var q = Cmd(c, tx, "UPDATE SYS_Kiosk_Groups SET ImageId=@img WHERE Id=@id");
                    q.Parameters.AddWithValue("@img", NG(Tok(was["imageId"]))); q.Parameters.AddWithValue("@id", G(gid));
                    if (q.ExecuteNonQuery() != 1) throw new Exception("Icon des Ordners nicht eindeutig zurueckgesetzt - zurueckgerollt, nichts geschrieben.");
                }
                if (Tok(now["filter"]) != Tok(was["filter"]))
                {
                    var dq = Cmd(c, tx, "DELETE FROM SYS_Kiosk_Filters WHERE Id=@id");   // PropertyNames per CASCADE
                    dq.Parameters.AddWithValue("@id", G(gid)); dq.ExecuteNonQuery();
                    RestoreFilterRows(c, tx, b);
                }
                tx.Commit();
            }
        }
        _log("acmp_kiosk_restore von " + requester + ": " + p["backup"] + " (Ordner " + gid + ")");
        var o = new JObject(); o["ok"] = true; o["wiederhergestellt"] = p["backup"]; o["ordnerId"] = gid; o["name"] = was["nameDe"];
        o["sicherungVorher"] = Path.GetFileName(again);
        o["hinweis"] = "Im Kiosk nach dem Aktualisieren sichtbar. Diese Ruecknahme selbst laesst sich mit acmp_kiosk_restore backup=\"" + Path.GetFileName(again) + "\" wieder umkehren.";
        return o;
    }
}
