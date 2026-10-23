// AcmpCcFolders: Client-Command-Ordner lesen (acmp_cc_folders), Client Commands in andere Ordner verschieben (acmp_cc_move),
// neue Ordner anlegen (acmp_cc_add_folder, auch mehrere in einem Auftrag) und Ordner umbenennen/verschieben
// (acmp_cc_update_folder). Grundlage: Console-Beobachtung 15.10.2026 (ACMP 6.10.4, Test-CC
// "ZZ Verschiebetest (MCP)", Ordner "ZZ Ordnertest"/"ZZ Unterordner"):
//  - Verschieben ("Client Command verschieben", Strg+X -> Ordner waehlen) aendert NUR SYS_SCRIPTS.FOLDERID. Der Update-Trigger
//    traegt den Ordner in alle Versionen ein. LastModifiedDate/LastModifier/State bleiben, die Rechte des Commands
//    (SYS_AccessControl_AclEntries_*) bleiben unveraendert - sie kommen NICHT vom neuen Ordner.
//  - Ordner anlegen schreibt eine Zeile in SYS_SCRIPT_STRUCTURE ({GUID}, Description, ParentID, StrucLevel NULL, IconId NULL =
//    Standardordner) und die Rechte in SYS_Folders_AclEntries_Users/_Groups: Vorschlag der Console = Rechte des Elternordners
//    plus der angemeldete Console-Benutzer. Der MCP uebernimmt die Rechte des Elternordners (ohne Zusatz-Benutzer).
//  - Ordner bearbeiten ("Ordner -> Bearbeiten": Name, Zielordner) aendert SYS_SCRIPT_STRUCTURE.Description bzw. ParentID.
//    Die Console liest den Baum ueber VW_Script_Structure_FolderServant (reines Abbild von SYS_SCRIPT_STRUCTURE); CC-Ordner
//    stehen nicht in SYS_Folders und nicht in SYS_MultiLanguageItems. Der MCP aendert nur Description/ParentID, die Rechte
//    des Ordners bleiben (wie beim Verschieben von Commands). Oberster Ordner (StrucLevel 1) und Aagon-Standardordner
//    (StrucLevel 0) bleiben unangetastet.
// Sicherheitsregeln wie beim CC-Writer: Antragsteller aus ccWriter.allowedRequesters, Kompatibilitaetspruefung bestanden,
// erst dryRun (Vorschau), Plan direkt vor dem Schreiben, Sicherung vor jedem Verschieben bzw. Aendern von Ordnern (zurueck mit undo=...).
// Optional ccWriter.moveScopeFolderIds: nur innerhalb dieser Ordner (samt Unterordnern) verschieben, anlegen und aendern.
// Loeschen von Ordnern bleibt Sache der Console.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using Formatting = Newtonsoft.Json.Formatting;

class CcFolder
{
    public string Id, Name, ParentId, Path;
    public int Depth;
    public int? Level;      // SYS_SCRIPT_STRUCTURE.StrucLevel: 1 = oberster Ordner, 0 = Aagon-Standardordner, NULL = selbst angelegt
    public bool IsNew;      // im selben Auftrag geplant (acmp_cc_add_folder), noch nicht in der DB
    public bool IsRoot { get { return string.IsNullOrEmpty(ParentId); } }
    public bool IsAagon { get { return Level.HasValue; } }
}

// Eine geplante Aenderung von acmp_cc_update_folder (Endstand nach dem ganzen Auftrag)
class CcFolderChange
{
    public CcFolder Folder;
    public string NewName, NewParentId, OldPath, NewPath;
    public bool Renamed, Moved;
}

partial class AcmpCcWriter
{
    public const int MaxMoves = 100;
    public const int MaxNewFolders = 50, MaxFolderChanges = 50;
    public const int MaxFolderName = 200;   // SYS_SCRIPT_STRUCTURE.Description nvarchar(200)
    const int MaxListed = 15;               // laengere Ergebnislisten kuerzen (die Antwort soll im Chat lesbar bleiben)
    static readonly string[] RootAliases = { "Available commands", "Verfügbare Client Commands", "Verfuegbare Client Commands" };
    static readonly string[] MoveKeys = { "command", "folder" };
    static readonly string[] AddFolderKeys = { "name", "parent" };
    static readonly string[] FolderChangeKeys = { "folder", "name", "parent" };

    public const string MoveHelp =
        "Liste [{\"command\":\"<Name oder ScriptId>\",\"folder\":\"<Zielordner>\"}, ...] (1-100). Kurzform fuer ein gemeinsames Ziel: " +
        "commands=[...] und folder=\"...\". Ordner als GUID, eindeutiger Name oder Pfad wie in acmp_cc_folders (z.B. \"Software\\Installieren\" " +
        "oder \"Available commands\\Software\"); der oberste Ordner heisst \"Available commands\".";
    public const string AddFolderHelp =
        "Liste [{\"name\":\"<neuer Ordner>\",\"parent\":\"<Elternordner>\"}, ...] (1-50), angelegt in dieser Reihenfolge. parent: GUID, Name oder " +
        "Pfad wie in acmp_cc_folders, auch ein Ordner, der weiter oben im selben Auftrag steht (z.B. \"Software\\Neu\"); ohne parent der " +
        "oberste Ordner. Fuer einen Ordner reicht name (+ parent).";
    public const string FolderChangeHelp =
        "Liste [{\"folder\":\"<Ordner>\",\"name\":\"<neuer Name>\",\"parent\":\"<neuer Elternordner>\"}, ...] (1-50; je Eintrag name und/oder " +
        "parent). Ordner als GUID, eindeutiger Name oder Pfad wie in acmp_cc_folders (Stand vor dem Auftrag); parent \"Available commands\" = " +
        "oberste Ebene. Fuer einen Ordner reicht folder + name/parent.";

    // ================= Hilfen ohne DB (offline getestet in CcFolderTest.cs) =================

    // Ordnerbaum aus Zeilen {Id, Name, ParentId[, StrucLevel]}: Pfad wie in acmp_get_client_commands ("Available commands\Software\...")
    public static Dictionary<string, CcFolder> BuildFolders(List<string[]> rows)
    {
        var all = new Dictionary<string, CcFolder>(StringComparer.OrdinalIgnoreCase);
        foreach (string[] r in rows)
        {
            var f = new CcFolder(); f.Id = r[0]; f.Name = r[1] ?? ""; f.ParentId = string.IsNullOrEmpty(r[2]) ? null : r[2];
            int lv; if (r.Length > 3 && r[3] != null && int.TryParse(r[3], out lv)) f.Level = lv;
            all[f.Id] = f;
        }
        foreach (CcFolder f in all.Values)
        {
            var parts = new List<string>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CcFolder cur = f;
            while (cur != null && seen.Add(cur.Id))
            {
                parts.Insert(0, cur.Name);
                CcFolder p = null;
                if (cur.ParentId != null && !all.TryGetValue(cur.ParentId, out p)) { parts.Insert(0, "?"); break; }
                cur = p;
            }
            f.Path = string.Join("\\", parts.ToArray());
            f.Depth = parts.Count - 1;
        }
        return all;
    }

    // Oberster Ordner (ohne ParentID). Gibt es mehrere, zaehlt der mit dem bekannten Namen ("Available commands").
    public static CcFolder RootOf(Dictionary<string, CcFolder> all)
    {
        var roots = new List<CcFolder>();
        foreach (CcFolder f in all.Values) if (f.IsRoot) roots.Add(f);
        if (roots.Count == 1) return roots[0];
        if (roots.Count == 0) throw new Exception("Oberster Client-Command-Ordner (ohne ParentID) nicht gefunden.");
        CcFolder hit = null;
        foreach (CcFolder f in roots)
            foreach (string a in RootAliases)
                if (string.Equals(f.Name, a, StringComparison.OrdinalIgnoreCase)) { if (hit != null && hit != f) throw new Exception("Mehrere oberste Client-Command-Ordner mit bekanntem Namen - unerwarteter Aufbau, nichts geaendert."); hit = f; }
        if (hit == null) throw new Exception("Mehrere oberste Client-Command-Ordner, keiner heisst \"Available commands\" - unerwarteter Aufbau, nichts geaendert.");
        return hit;
    }

    static bool IsRootAlias(string s, CcFolder root)
    {
        if (string.Equals(s, root.Name, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (string a in RootAliases) if (string.Equals(s, a, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // Ordner aus GUID, eindeutigem Namen oder Pfad (mit oder ohne fuehrenden obersten Ordner)
    public static CcFolder FindFolder(Dictionary<string, CcFolder> all, string arg)
    {
        if (string.IsNullOrEmpty(arg) || arg.Trim().Length == 0) throw new Exception("Ordner fehlt (GUID, Name oder Pfad aus acmp_cc_folders).");
        string s = arg.Trim();
        CcFolder root = RootOf(all);
        string g = GuidBraces(s);
        if (g != null)
        {
            CcFolder f;
            if (all.TryGetValue(g, out f)) return f;
            throw new Exception("Ordner " + g + " nicht gefunden.");
        }
        string[] parts = s.Trim('\\', '/').Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++) parts[i] = parts[i].Trim();
        if (parts.Length == 0 || (parts.Length == 1 && IsRootAlias(parts[0], root))) return root;
        if (parts.Length == 1)
        {
            var hits = new List<CcFolder>();
            foreach (CcFolder f in all.Values) if (!f.IsRoot && string.Equals(f.Name, parts[0], StringComparison.OrdinalIgnoreCase)) hits.Add(f);
            if (hits.Count == 1) return hits[0];
            if (hits.Count == 0) throw new Exception("Ordner \"" + parts[0] + "\" nicht gefunden (acmp_cc_folders zeigt alle).");
            var ps = new List<string>(); foreach (CcFolder h in hits) ps.Add(h.Path + " " + h.Id);
            ps.Sort(StringComparer.OrdinalIgnoreCase);
            throw new Exception("Ordnername \"" + parts[0] + "\" ist nicht eindeutig - Pfad oder GUID angeben: " + string.Join("; ", ps.ToArray()));
        }
        int start = IsRootAlias(parts[0], root) ? 1 : 0;
        CcFolder cur = root;
        for (int i = start; i < parts.Length; i++)
        {
            CcFolder next = null;
            foreach (CcFolder f in all.Values)
                if (string.Equals(f.ParentId, cur.Id, StringComparison.OrdinalIgnoreCase) && string.Equals(f.Name, parts[i], StringComparison.OrdinalIgnoreCase))
                {
                    if (next != null) throw new Exception("Pfad \"" + s + "\": unter \"" + cur.Path + "\" gibt es \"" + parts[i] + "\" mehrfach - GUID angeben.");
                    next = f;
                }
            if (next == null) throw new Exception("Pfad \"" + s + "\": \"" + parts[i] + "\" gibt es unter \"" + cur.Path + "\" nicht.");
            cur = next;
        }
        return cur;
    }

    // Ist f gleich einem der Ordner in scope oder liegt darunter? (leerer scope = alles erlaubt)
    public static bool InScope(Dictionary<string, CcFolder> all, CcFolder f, ICollection<string> scope)
    {
        if (scope == null || scope.Count == 0) return true;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (CcFolder cur = f; cur != null && seen.Add(cur.Id); )
        {
            if (scope.Contains(cur.Id)) return true;
            CcFolder p = null;
            if (cur.ParentId == null || !all.TryGetValue(cur.ParentId, out p)) break;
            cur = p;
        }
        return false;
    }

    public static string CheckFolderName(string name)
    {
        string n = (name ?? "").Trim();
        if (n.Length == 0) throw new Exception("name fehlt (Name des neuen Ordners).");
        if (n.Length > MaxFolderName) throw new Exception("Ordnername ist " + n.Length + " Zeichen lang, erlaubt sind " + MaxFolderName + ".");
        if (n.IndexOf('\\') >= 0 || n.IndexOf('/') >= 0) throw new Exception("Ordnername darf kein \\ oder / enthalten (Trennzeichen fuer Pfade). Unterordner: parent angeben.");
        foreach (char ch in n) if (char.IsControl(ch)) throw new Exception("Ordnername enthaelt Steuerzeichen.");
        foreach (string a in RootAliases) if (string.Equals(n, a, StringComparison.OrdinalIgnoreCase)) throw new Exception("\"" + n + "\" ist der Name des obersten Ordners - bitte anders nennen.");
        return n;
    }

    // Auftrag -> Liste {command, folder}: moves=[{command,folder}] oder commands=[...] + folder
    public static List<string[]> MoveList(JObject a)
    {
        JToken mv = a["moves"];
        if (mv != null && mv.Type == JTokenType.String)
        {
            try { mv = JToken.Parse((string)mv); }
            catch (Exception ex) { throw new Exception("moves ist kein gueltiges JSON: " + ex.Message); }
        }
        var list = new List<string[]>();
        bool hasMoves = mv != null && mv.Type != JTokenType.Null;
        bool hasCommands = a["commands"] != null && a["commands"].Type != JTokenType.Null;
        if (hasMoves && hasCommands) throw new Exception("Entweder moves oder commands+folder angeben, nicht beides.");
        if (hasMoves)
        {
            JArray arr = mv as JArray;
            if (arr == null && mv is JObject) { arr = new JArray(); arr.Add(mv); }
            if (arr == null) throw new Exception("moves muss eine Liste sein: " + MoveHelp);
            foreach (JToken t in arr)
            {
                JObject o = t as JObject;
                if (o == null) throw new Exception("moves: jeder Eintrag muss ein Objekt {\"command\":...,\"folder\":...} sein.");
                foreach (JProperty p in o.Properties())
                    if (Array.IndexOf(MoveKeys, p.Name) < 0) throw new Exception("moves: unbekanntes Feld \"" + p.Name + "\" (erlaubt: command, folder).");
                string c = o["command"] != null && o["command"].Type == JTokenType.String ? ((string)o["command"]).Trim() : "";
                string f = o["folder"] != null && o["folder"].Type == JTokenType.String ? ((string)o["folder"]).Trim() : "";
                if (c.Length == 0) throw new Exception("moves: \"command\" fehlt (Name oder ScriptId).");
                if (f.Length == 0) throw new Exception("moves: bei \"" + c + "\" fehlt \"folder\" (Zielordner).");
                list.Add(new[] { c, f });
            }
        }
        else if (hasCommands)
        {
            JToken ct = a["commands"];
            if (ct.Type == JTokenType.String) { string s = (string)ct; ct = s.TrimStart().StartsWith("[") ? JToken.Parse(s) : new JArray(s); }
            JArray arr = ct as JArray;
            if (arr == null) throw new Exception("commands muss eine Liste von Namen oder ScriptIds sein.");
            string f = a["folder"] != null && a["folder"].Type == JTokenType.String ? ((string)a["folder"]).Trim() : "";
            if (f.Length == 0) throw new Exception("folder fehlt (Zielordner fuer alle commands).");
            foreach (JToken t in arr)
            {
                string c = t.Type == JTokenType.String ? ((string)t).Trim() : "";
                if (c.Length == 0) throw new Exception("commands: leere Angabe - je Eintrag Name oder ScriptId.");
                list.Add(new[] { c, f });
            }
        }
        else throw new Exception("Was soll verschoben werden? " + MoveHelp);
        if (list.Count == 0) throw new Exception("Liste ist leer.");
        if (list.Count > MaxMoves) throw new Exception(list.Count + " Commands - hoechstens " + MaxMoves + " je Auftrag, bitte aufteilen.");
        return list;
    }

    static string Str(JToken t) { return t != null && t.Type == JTokenType.String ? ((string)t).Trim() : ""; }
    static bool Has(JObject a, string key) { return a[key] != null && a[key].Type != JTokenType.Null; }

    // Liste aus JSON-Liste, JSON-Text oder einzelnem Objekt
    static JArray ListArg(JToken t, string what, string help)
    {
        if (t != null && t.Type == JTokenType.String)
        {
            try { t = JToken.Parse((string)t); }
            catch (Exception ex) { throw new Exception(what + " ist kein gueltiges JSON: " + ex.Message); }
        }
        if (t is JObject) { var one = new JArray(); one.Add(t); return one; }
        JArray arr = t as JArray;
        if (arr == null) throw new Exception(what + " muss eine Liste sein: " + help);
        return arr;
    }

    // Lange Ergebnislisten kuerzen: die ersten MaxListed Eintraege, dann ein Hinweis mit der Restzahl
    public static JArray ShortList(List<string> items, string moreText)
    {
        var a = new JArray();
        for (int i = 0; i < items.Count && i < MaxListed; i++) a.Add(items[i]);
        if (items.Count > MaxListed) a.Add("... und " + (items.Count - MaxListed) + " weitere" + moreText);
        return a;
    }

    // acmp_cc_add_folder: folders=[{name,parent}] oder name (+ parent) -> Liste {name (geprueft), parent ("" = oberster Ordner)}
    public static List<string[]> AddFolderList(JObject a)
    {
        bool hasList = Has(a, "folders"), hasName = Has(a, "name");
        if (hasList && (hasName || Has(a, "parent"))) throw new Exception("Entweder folders oder name (+ parent) angeben, nicht beides.");
        var list = new List<string[]>();
        if (hasList)
        {
            int i = 0;
            foreach (JToken t in ListArg(a["folders"], "folders", AddFolderHelp))
            {
                i++;
                JObject o = t as JObject;
                if (o == null) throw new Exception("folders: jeder Eintrag muss ein Objekt {\"name\":...,\"parent\":...} sein.");
                foreach (JProperty p in o.Properties())
                    if (Array.IndexOf(AddFolderKeys, p.Name) < 0) throw new Exception("folders: unbekanntes Feld \"" + p.Name + "\" (erlaubt: name, parent).");
                string n;
                try { n = CheckFolderName(o["name"] != null && o["name"].Type == JTokenType.String ? (string)o["name"] : null); }
                catch (Exception ex) { throw new Exception("folders, Eintrag " + i + ": " + ex.Message); }
                list.Add(new[] { n, Str(o["parent"]) });
            }
        }
        else if (hasName)
        {
            if (a["name"].Type != JTokenType.String) throw new Exception("name muss Text sein.");
            list.Add(new[] { CheckFolderName((string)a["name"]), Str(a["parent"]) });
        }
        else throw new Exception("Was soll angelegt werden? " + AddFolderHelp);
        if (list.Count == 0) throw new Exception("folders ist leer.");
        if (list.Count > MaxNewFolders) throw new Exception(list.Count + " Ordner - hoechstens " + MaxNewFolders + " je Auftrag, bitte aufteilen.");
        return list;
    }

    // Neue Ordner der Reihe nach einplanen. Elternordner = vorhandener Ordner oder einer, der weiter oben im selben Auftrag steht.
    // Liefert die neuen Ordner (IsNew, Pfad wie spaeter in der Console); all bleibt unveraendert.
    public static List<CcFolder> PlanNewFolders(Dictionary<string, CcFolder> all, List<string[]> list, ICollection<string> scope, Func<string> newId)
    {
        var work = new Dictionary<string, CcFolder>(all, StringComparer.OrdinalIgnoreCase);
        var res = new List<CcFolder>();
        for (int i = 0; i < list.Count; i++)
        {
            string name = list[i][0], parentArg = list[i][1] ?? "";
            string label = list.Count > 1 ? "Eintrag " + (i + 1) + " \"" + name + "\": " : "";
            CcFolder parent;
            try { parent = parentArg.Length == 0 ? RootOf(work) : FindFolder(work, parentArg); }
            catch (Exception ex)
            {
                string hint = "";
                for (int k = i + 1; k < list.Count; k++)
                    if (LastSegmentEquals(parentArg, list[k][0])) { hint = " (\"" + list[k][0] + "\" steht weiter unten im Auftrag - Elternordner vor ihren Unterordnern auffuehren)"; break; }
                throw new Exception(label + ex.Message + hint);
            }
            if (!InScope(work, parent, scope)) throw new Exception(label + "Elternordner \"" + parent.Path + "\" liegt ausserhalb von ccWriter.moveScopeFolderIds.");
            foreach (CcFolder f in work.Values)
                if (string.Equals(f.ParentId, parent.Id, StringComparison.OrdinalIgnoreCase) && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                    throw new Exception(label + "Unter \"" + parent.Path + "\" gibt es " + (f.IsNew ? "im selben Auftrag schon" : "schon") + " einen Ordner \"" + f.Name + "\"" + (f.IsNew ? "" : " (" + f.Id + ")") + ".");
            var nf = new CcFolder();
            nf.Id = newId(); nf.Name = name; nf.ParentId = parent.Id; nf.Path = parent.Path + "\\" + name; nf.Depth = parent.Depth + 1; nf.IsNew = true;
            work[nf.Id] = nf;
            res.Add(nf);
        }
        return res;
    }

    static bool LastSegmentEquals(string pathArg, string name)
    {
        string[] parts = (pathArg ?? "").Trim().Trim('\\', '/').Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 && string.Equals(parts[parts.Length - 1].Trim(), name, StringComparison.OrdinalIgnoreCase);
    }

    // acmp_cc_update_folder: changes=[{folder,name,parent}] oder folder + name/parent -> Liste {folder, neuer Name|null, neuer Elternordner|null}
    public static List<string[]> FolderChangeList(JObject a)
    {
        bool hasList = Has(a, "changes"), hasOne = Has(a, "folder") || Has(a, "name") || Has(a, "parent");
        if (hasList && hasOne) throw new Exception("Entweder changes oder folder + name/parent angeben, nicht beides.");
        var list = new List<string[]>();
        if (hasList)
        {
            int i = 0;
            foreach (JToken t in ListArg(a["changes"], "changes", FolderChangeHelp))
            {
                i++;
                JObject o = t as JObject;
                if (o == null) throw new Exception("changes: jeder Eintrag muss ein Objekt {\"folder\":...,\"name\":...,\"parent\":...} sein.");
                foreach (JProperty p in o.Properties())
                    if (Array.IndexOf(FolderChangeKeys, p.Name) < 0) throw new Exception("changes: unbekanntes Feld \"" + p.Name + "\" (erlaubt: folder, name, parent).");
                list.Add(OneFolderChange(o, "changes, Eintrag " + i + ": "));
            }
        }
        else if (hasOne) list.Add(OneFolderChange(a, ""));
        else throw new Exception("Welcher Ordner soll geaendert werden? " + FolderChangeHelp);
        if (list.Count == 0) throw new Exception("changes ist leer.");
        if (list.Count > MaxFolderChanges) throw new Exception(list.Count + " Ordner - hoechstens " + MaxFolderChanges + " je Auftrag, bitte aufteilen.");
        return list;
    }

    static string[] OneFolderChange(JObject o, string label)
    {
        string f = Str(o["folder"]);
        if (f.Length == 0) throw new Exception(label + "\"folder\" fehlt (Ordner als GUID, Name oder Pfad aus acmp_cc_folders).");
        string n = null;
        if (Has(o, "name"))
        {
            if (o["name"].Type != JTokenType.String) throw new Exception(label + "name muss Text sein.");
            try { n = CheckFolderName((string)o["name"]); }
            catch (Exception ex) { throw new Exception(label + ex.Message); }
        }
        string par = Str(o["parent"]);
        if (n == null && par.Length == 0) throw new Exception(label + "bei \"" + f + "\" fehlt name (neuer Name) oder parent (neuer Elternordner).");
        return new[] { f, n, par.Length > 0 ? par : null };
    }

    // Umbenennen/Verschieben pruefen. Ordner werden im Stand vor dem Auftrag gesucht; geprueft wird der Endstand nach allen
    // Aenderungen: kein Zyklus, keine gleichnamigen Geschwister, oberster Ordner und Aagon-Ordner unveraendert, Bereich.
    // Eintraege ohne Wirkung landen in skipped (falls angegeben).
    public static List<CcFolderChange> PlanFolderChanges(Dictionary<string, CcFolder> all, List<string[]> list, ICollection<string> scope, List<string> skipped)
    {
        CcFolder root = RootOf(all);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var name = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);     // Endstand: Id -> Name
        var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // Endstand: Id -> ParentId
        foreach (CcFolder f in all.Values) { name[f.Id] = f.Name; parent[f.Id] = f.ParentId; }
        var res = new List<CcFolderChange>();
        foreach (string[] m in list)
        {
            CcFolder f = FindFolder(all, m[0]);
            string label = "\"" + f.Path + "\": ";
            if (!seen.Add(f.Id)) throw new Exception(label + "steht mehrfach im Auftrag - je Ordner ein Eintrag (name und parent zusammen).");
            if (f.Id == root.Id || f.IsRoot) throw new Exception(label + "der oberste Ordner wird nicht geaendert.");
            if (f.IsAagon) throw new Exception(label + "Aagon-Standardordner (StrucLevel " + f.Level + ") - wird nicht umbenannt oder verschoben.");
            string newName = m[1] ?? f.Name;
            CcFolder np = m[2] != null ? FindFolder(all, m[2]) : null;
            if (np != null && np.Id == f.Id) throw new Exception(label + "kann nicht in sich selbst verschoben werden.");
            string newParent = np != null ? np.Id : f.ParentId;
            bool renamed = !string.Equals(newName, f.Name, StringComparison.Ordinal);
            bool moved = !string.Equals(newParent, f.ParentId, StringComparison.OrdinalIgnoreCase);
            if (!renamed && !moved) { if (skipped != null) skipped.Add(label + "Name und Elternordner sind schon so"); continue; }
            if (scope != null && scope.Count > 0)
            {
                if (!InScope(all, f, scope)) throw new Exception(label + "liegt ausserhalb von ccWriter.moveScopeFolderIds.");
                if (moved && scope.Contains(f.Id)) throw new Exception(label + "ist selbst in ccWriter.moveScopeFolderIds eingetragen - nicht verschieben.");
                if (moved && !InScope(all, np, scope)) throw new Exception(label + "neuer Elternordner \"" + np.Path + "\" liegt ausserhalb von ccWriter.moveScopeFolderIds.");
            }
            name[f.Id] = newName; parent[f.Id] = newParent;
            var ch = new CcFolderChange();
            ch.Folder = f; ch.NewName = newName; ch.NewParentId = newParent; ch.OldPath = f.Path; ch.Renamed = renamed; ch.Moved = moved;
            res.Add(ch);
        }
        // Kein Ordner darf (auch ueber mehrere Aenderungen hinweg) unter sich selbst oder einem eigenen Unterordner landen
        foreach (CcFolderChange ch in res)
        {
            var up = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (string cur = ch.NewParentId; !string.IsNullOrEmpty(cur) && up.Add(cur); )
            {
                if (string.Equals(cur, ch.Folder.Id, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("\"" + ch.OldPath + "\" wuerde unter sich selbst bzw. einem eigenen Unterordner liegen (Zyklus) - nichts geplant.");
                string next; if (!parent.TryGetValue(cur, out next)) break;
                cur = next;
            }
        }
        var rows = new List<string[]>();
        foreach (CcFolder f in all.Values) rows.Add(new[] { f.Id, name[f.Id], parent[f.Id] ?? "", f.Level.HasValue ? f.Level.Value.ToString() : null });
        Dictionary<string, CcFolder> after = BuildFolders(rows);
        // Keine zwei gleichnamigen Ordner unter demselben Elternordner, an denen eine Aenderung beteiligt ist
        foreach (CcFolderChange ch in res)
        {
            foreach (CcFolder x in after.Values)
                if (!string.Equals(x.Id, ch.Folder.Id, StringComparison.OrdinalIgnoreCase) && string.Equals(x.ParentId, ch.NewParentId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.Name, ch.NewName, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("\"" + ch.OldPath + "\": unter \"" + after[ch.NewParentId].Path + "\" gaebe es danach zweimal \"" + ch.NewName + "\" - nichts geplant.");
            ch.NewPath = after[ch.Folder.Id].Path;
        }
        return res;
    }

    // undo: Sicherung von acmp_cc_update_folder -> Liste {Ordner-GUID, alter Name, alter Elternordner}; nur wenn der Stand noch der nach dem Auftrag ist
    public static List<string[]> FolderUndoList(JObject backup, Dictionary<string, CcFolder> all)
    {
        var list = new List<string[]>();
        foreach (JObject ch in (JArray)backup["changes"])
        {
            string id = (string)ch["folderId"];
            CcFolder f;
            if (!all.TryGetValue(id, out f)) throw new Exception("Ordner \"" + ch["newPath"] + "\" " + id + " gibt es nicht mehr - zurueck abgebrochen, nichts geaendert.");
            if (!string.Equals(f.Name, (string)ch["newName"], StringComparison.Ordinal) || !string.Equals(f.ParentId, (string)ch["newParentId"], StringComparison.OrdinalIgnoreCase))
                throw new Exception("\"" + ch["newPath"] + "\" wurde seit dem Auftrag erneut geaendert (jetzt \"" + f.Path + "\") - zurueck abgebrochen, nichts geaendert.");
            if (!all.ContainsKey((string)ch["oldParentId"])) throw new Exception("Der alte Elternordner von \"" + ch["oldPath"] + "\" existiert nicht mehr - zurueck abgebrochen, nichts geaendert.");
            list.Add(new[] { id, (string)ch["oldName"], (string)ch["oldParentId"] });
        }
        if (list.Count == 0) throw new Exception("Sicherung enthaelt keine Aenderungen.");
        return list;
    }

    static string StateText(int st) { return st == 1 ? "Entwurf" : st == 7 ? "freigegeben" : st == 8 ? "abgeloest" : "State " + st; }

    // ================= Lesen =================

    static Dictionary<string, CcFolder> LoadFolders(SqlConnection c)
    {
        var rows = new List<string[]>();
        var q = c.CreateCommand();
        q.CommandText = "SELECT ScriptStructureID, Description, ParentID, CAST(StrucLevel AS varchar(11)) FROM SYS_SCRIPT_STRUCTURE";
        using (var r = q.ExecuteReader())
            while (r.Read()) rows.Add(new[] { r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3) });
        return BuildFolders(rows);
    }

    // Neueste Version je Command: ScriptId -> {name, folderId, state, version, released}
    static Dictionary<string, JObject> LatestCommands(SqlConnection c, string onlyId)
    {
        var q = c.CreateCommand();
        q.CommandText = "SELECT s.SCRIPTID, s.DESCRIPTION, s.FOLDERID, CAST(s.State AS int), s.ScriptVersion, " +
            "(SELECT COUNT(*) FROM SYS_SCRIPTS x WHERE x.SCRIPTID=s.SCRIPTID AND x.State=7) FROM SYS_SCRIPTS s " +
            "WHERE s.ScriptVersion=(SELECT MAX(m.ScriptVersion) FROM SYS_SCRIPTS m WHERE m.SCRIPTID=s.SCRIPTID)" + (onlyId != null ? " AND s.SCRIPTID=@id" : "");
        if (onlyId != null) q.Parameters.AddWithValue("@id", onlyId);
        var res = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
        using (var r = q.ExecuteReader())
            while (r.Read())
            {
                var o = new JObject();
                o["scriptId"] = r.GetString(0); o["name"] = r.IsDBNull(1) ? "" : r.GetString(1);
                o["folderId"] = r.IsDBNull(2) ? null : r.GetString(2); o["state"] = r.GetInt32(3); o["version"] = r.GetInt32(4);
                o["released"] = Convert.ToInt32(r.GetValue(5)) > 0;
                res[r.GetString(0)] = o;
            }
        return res;
    }

    static JObject ResolveCommand(SqlConnection c, string arg)
    {
        string id = GuidBraces(arg);
        if (id == null)
        {
            var q = c.CreateCommand();
            q.CommandText = "SELECT DISTINCT SCRIPTID FROM SYS_SCRIPTS WHERE DESCRIPTION=@n";
            q.Parameters.AddWithValue("@n", arg.Trim());
            var ids = new List<string>();
            using (var r = q.ExecuteReader()) while (r.Read()) ids.Add(r.GetString(0));
            if (ids.Count == 0) throw new Exception("Client Command \"" + arg + "\" nicht gefunden.");
            if (ids.Count > 1) throw new Exception("Client-Command-Name \"" + arg + "\" ist nicht eindeutig (" + string.Join(", ", ids.ToArray()) + ") - ScriptId angeben.");
            id = ids[0];
        }
        Dictionary<string, JObject> one = LatestCommands(c, id);
        JObject cmd;
        if (!one.TryGetValue(id, out cmd)) throw new Exception("Client Command " + id + " nicht gefunden.");
        return cmd;
    }

    static int AclCount(SqlConnection c, SqlTransaction tx, string table, string objectId)
    {
        var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = "SELECT COUNT(*) FROM " + table + " WHERE ObjectID=@o";
        Guid g; q.Parameters.AddWithValue("@o", Guid.TryParse(objectId.Trim('{', '}'), out g) ? g : Guid.Empty);
        return Convert.ToInt32(q.ExecuteScalar());
    }

    // Rechte als vergleichbarer Text "U:<AccessID>:<Edit>:<Visible>|G:..."
    static string AclKey(SqlConnection c, string usersTable, string groupsTable, string objectId)
    {
        var parts = new List<string>();
        Guid g; Guid.TryParse(objectId.Trim('{', '}'), out g);
        foreach (string[] t in new[] { new[] { "U", usersTable }, new[] { "G", groupsTable } })
        {
            var q = c.CreateCommand();
            q.CommandText = "SELECT CAST(AccessID AS varchar(36)), CAST(EditState AS int), CAST(VisibleState AS int) FROM " + t[1] + " WHERE ObjectID=@o";
            q.Parameters.AddWithValue("@o", g);
            using (var r = q.ExecuteReader()) while (r.Read()) parts.Add(t[0] + ":" + r.GetString(0).ToUpperInvariant() + ":" + r.GetInt32(1) + ":" + r.GetInt32(2));
        }
        parts.Sort(StringComparer.Ordinal);
        return string.Join("|", parts.ToArray());
    }

    // acmp_cc_folders: Ordnerbaum mit Anzahl Commands, optional die Commands selbst
    public JObject Folders(string folder, bool withCommands)
    {
        if (string.IsNullOrEmpty(_readCs)) throw new Exception("DB-Lesezugang (db.connectionString) fehlt.");
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            Dictionary<string, CcFolder> all = LoadFolders(c);
            CcFolder top = string.IsNullOrEmpty(folder) ? RootOf(all) : FindFolder(all, folder);
            Dictionary<string, JObject> cmds = LatestCommands(c, null);
            var byFolder = new Dictionary<string, List<JObject>>(StringComparer.OrdinalIgnoreCase);
            foreach (JObject cmd in cmds.Values)
            {
                string fid = (string)cmd["folderId"] ?? "";
                List<JObject> l; if (!byFolder.TryGetValue(fid, out l)) { l = new List<JObject>(); byFolder[fid] = l; }
                l.Add(cmd);
            }
            var aclU = AclCounts(c, "SYS_Folders_AclEntries_Users"); var aclG = AclCounts(c, "SYS_Folders_AclEntries_Groups");
            var mine = RegisteredFolders();
            var list = new List<CcFolder>();
            foreach (CcFolder f in all.Values) if (f.Id == top.Id || IsBelow(all, f, top.Id)) list.Add(f);
            list.Sort(delegate(CcFolder x, CcFolder y) { return string.Compare(x.Path, y.Path, StringComparison.OrdinalIgnoreCase); });
            var arr = new JArray(); int total = 0; var warn = new List<string>();
            foreach (CcFolder f in list)
            {
                var o = new JObject();
                o["path"] = f.Path; o["id"] = f.Id;
                List<JObject> l; byFolder.TryGetValue(f.Id, out l);
                int n = l != null ? l.Count : 0; total += n;
                o["commands"] = n;
                int sub = 0; foreach (CcFolder x in all.Values) if (string.Equals(x.ParentId, f.Id, StringComparison.OrdinalIgnoreCase)) sub++;
                if (sub > 0) o["unterordner"] = sub;
                int u, gr; aclU.TryGetValue(f.Id, out u); aclG.TryGetValue(f.Id, out gr);
                o["rechte"] = u + " Benutzer, " + gr + " Gruppen";
                if (u + gr == 0) warn.Add("\"" + f.Path + "\" hat keine eigenen Rechte-Eintraege (fuer Commands darin nicht verwenden).");
                if (mine.Contains(f.Id)) o["vomMcp"] = true;
                if (f.IsAagon && !f.IsRoot) o["aagon"] = true;   // StrucLevel 0: nicht umbenennen/verschieben
                if (withCommands && l != null)
                {
                    l.Sort(delegate(JObject x, JObject y) { return string.Compare((string)x["name"], (string)y["name"], StringComparison.OrdinalIgnoreCase); });
                    var ca = new JArray();
                    foreach (JObject cmd in l)
                    {
                        int st = (int)cmd["state"];
                        ca.Add(cmd["name"] + " | " + cmd["scriptId"] + " | v" + cmd["version"] + " " + StateText(st) + (st != 7 && (bool)cmd["released"] ? " (freigegebene Version vorhanden)" : ""));
                    }
                    o["liste"] = ca;
                }
                arr.Add(o);
            }
            int orphan = 0;
            foreach (var kv in byFolder) if (!all.ContainsKey(kv.Key)) orphan += kv.Value.Count;
            var res = new JObject();
            res["ordner"] = arr.Count; res["commands"] = total;
            if (top.IsRoot && orphan > 0) warn.Add(orphan + " Command(s) mit unbekanntem Ordner (FOLDERID ohne Zeile in SYS_SCRIPT_STRUCTURE).");
            if (warn.Count > 0) res["hinweise"] = new JArray(warn.ToArray());
            res["baum"] = arr;
            res["hinweis"] = "Commands verschieben: acmp_cc_move ; neue Ordner: acmp_cc_add_folder ; Ordner umbenennen/verschieben: acmp_cc_update_folder (nicht bei aagon=true und dem obersten Ordner) ; alles erst mit dryRun=true pruefen. Gezaehlt wird die neueste Version je Command (auch Entwuerfe).";
            return res;
        }
    }

    static bool IsBelow(Dictionary<string, CcFolder> all, CcFolder f, string ancestorId)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (CcFolder cur = f; cur != null && cur.ParentId != null && seen.Add(cur.Id); )
        {
            if (string.Equals(cur.ParentId, ancestorId, StringComparison.OrdinalIgnoreCase)) return true;
            CcFolder p; if (!all.TryGetValue(cur.ParentId, out p)) return false;
            cur = p;
        }
        return false;
    }

    static Dictionary<string, int> AclCounts(SqlConnection c, string table)
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var q = c.CreateCommand();
        q.CommandText = "SELECT CAST(ObjectID AS varchar(36)), COUNT(*) FROM " + table + " GROUP BY ObjectID";
        using (var r = q.ExecuteReader()) while (r.Read()) d["{" + r.GetString(0).ToUpperInvariant() + "}"] = Convert.ToInt32(r.GetValue(1));
        return d;
    }

    // ================= Planen (dryRun und Ausfuehrung) =================

    public JObject PlanFolderOp(string tool, JObject a)
    {
        if (string.IsNullOrEmpty(_readCs)) throw new Exception("DB-Lesezugang (db.connectionString) fehlt.");
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            if (tool == "acmp_cc_add_folder") return PlanAddFolder(c, a);
            if (tool == "acmp_cc_update_folder") return PlanUpdateFolder(c, a);
            string undo = a["undo"] != null && a["undo"].Type == JTokenType.String ? ((string)a["undo"]).Trim() : null;
            if (!string.IsNullOrEmpty(undo))
            {
                if (a["moves"] != null || a["commands"] != null) throw new Exception("undo nicht zusammen mit moves/commands angeben.");
                return PlanUndo(c, undo);
            }
            return PlanMove(c, MoveList(a), null);
        }
    }

    JObject PlanMove(SqlConnection c, List<string[]> list, string undoOf)
    {
        Dictionary<string, CcFolder> all = LoadFolders(c);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var moves = new JArray(); var lines = new List<string>(); var skipped = new List<string>();
        var targetNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // Ziel|Name -> ScriptId (Doppelte im Auftrag)
        foreach (string[] m in list)
        {
            JObject cmd = ResolveCommand(c, m[0]);
            string id = (string)cmd["scriptId"], name = (string)cmd["name"];
            if (!seen.Add(id)) throw new Exception("\"" + name + "\" " + id + " steht mehrfach im Auftrag - je Command nur einmal.");
            CcFolder dst = FindFolder(all, m[1]);
            CcFolder src; all.TryGetValue((string)cmd["folderId"] ?? "", out src);
            string label = "\"" + name + "\" (" + StateText((int)cmd["state"]) + ", v" + cmd["version"] + ") " + id;
            if (src != null && src.Id == dst.Id) { skipped.Add(label + " liegt schon in \"" + dst.Path + "\""); continue; }
            if (_moveScope.Count > 0 && (src == null || !InScope(all, src, _moveScope)))
                throw new Exception(label + ": Quellordner \"" + (src != null ? src.Path : (string)cmd["folderId"]) + "\" liegt ausserhalb von ccWriter.moveScopeFolderIds.");
            if (!InScope(all, dst, _moveScope)) throw new Exception(label + ": Zielordner \"" + dst.Path + "\" liegt ausserhalb von ccWriter.moveScopeFolderIds.");
            if (AclCount(c, null, "SYS_Folders_AclEntries_Users", dst.Id) + AclCount(c, null, "SYS_Folders_AclEntries_Groups", dst.Id) == 0)
                throw new Exception("Zielordner \"" + dst.Path + "\" hat keine eigenen Rechte-Eintraege - in der Console waere er fuer niemanden sichtbar. Anderen Ordner waehlen.");
            string key = dst.Id + "|" + name.ToLowerInvariant(), other;
            if (targetNames.TryGetValue(key, out other)) throw new Exception("Zwei Commands namens \"" + name + "\" sollen nach \"" + dst.Path + "\" - in einem Ordner nur einmal.");
            targetNames[key] = id;
            var dq = c.CreateCommand();
            dq.CommandText = "SELECT COUNT(DISTINCT SCRIPTID) FROM SYS_SCRIPTS WHERE FOLDERID=@f AND DESCRIPTION=@n AND SCRIPTID<>@id";
            dq.Parameters.AddWithValue("@f", dst.Id); dq.Parameters.AddWithValue("@n", name); dq.Parameters.AddWithValue("@id", id);
            if (Convert.ToInt32(dq.ExecuteScalar()) > 0) throw new Exception("In \"" + dst.Path + "\" gibt es schon einen Command \"" + name + "\" - vorher umbenennen oder anderen Ordner waehlen.");
            var mo = new JObject();
            mo["scriptId"] = id; mo["name"] = name; mo["version"] = cmd["version"]; mo["state"] = cmd["state"];
            mo["from"] = (string)cmd["folderId"]; mo["fromPath"] = src != null ? src.Path : "(unbekannter Ordner " + cmd["folderId"] + ")";
            mo["to"] = dst.Id; mo["toPath"] = dst.Path;
            moves.Add(mo);
            lines.Add(moves.Count + ". " + label);
            lines.Add("   \"" + mo["fromPath"] + "\" -> \"" + dst.Path + "\"");
            if (AclKey(c, "SYS_AccessControl_AclEntries_Users", "SYS_AccessControl_AclEntries_Groups", id) != AclKey(c, "SYS_Folders_AclEntries_Users", "SYS_Folders_AclEntries_Groups", dst.Id))
                lines.Add("   Hinweis: Die Rechte des Commands bleiben, wie beim Verschieben in der Console; sie weichen von den Rechten des Zielordners ab.");
        }
        if (moves.Count == 0) throw new Exception("Nichts zu verschieben - " + (skipped.Count > 0 ? string.Join("; ", skipped.ToArray()) : "Liste leer") + ".");
        var p = new JObject();
        p["action"] = "move"; p["batchId"] = NewGuid(); p["moves"] = moves;
        if (undoOf != null) p["undoOf"] = undoOf;
        var head = new List<string>();
        head.Add(undoOf != null
            ? moves.Count + " Client Command(s) auf den Stand vor " + undoOf + " zurueck verschieben."
            : moves.Count + " Client Command(s) verschieben - eine Transaktion, vorher Sicherung (zurueck mit acmp_cc_move undo=\"<Sicherung>\").");
        head.Add("Geaendert wird nur der Ordner (SYS_SCRIPTS.FOLDERID, alle Versionen) - wie \"Client Command verschieben\" in der Console. Inhalt, Freigabe, Rechte, Kiosk-Eintraege und Jobs bleiben.");
        head.AddRange(lines);
        foreach (string s in skipped) head.Add("Uebersprungen: " + s);
        head.Add("Danach in der Console Aktualisieren (F5). Commands, die gerade in einer Console geoeffnet sind, vorher schliessen.");
        p["vorschau"] = new JArray(head.ToArray());
        return p;
    }

    JObject PlanUndo(SqlConnection c, string backupName)
    {
        JObject b = ReadBackup(backupName, "move", "acmp_cc_move");
        Dictionary<string, CcFolder> all = LoadFolders(c);
        var list = new List<string[]>();
        foreach (JObject m in (JArray)b["moves"])
        {
            string id = (string)m["scriptId"];
            Dictionary<string, JObject> one = LatestCommands(c, id);
            JObject cmd;
            if (!one.TryGetValue(id, out cmd)) throw new Exception("\"" + m["name"] + "\" " + id + " gibt es nicht mehr - zurueck nur fuer vorhandene Commands.");
            if (!string.Equals((string)cmd["folderId"], (string)m["to"], StringComparison.OrdinalIgnoreCase))
                throw new Exception("\"" + m["name"] + "\" liegt nicht mehr in \"" + m["toPath"] + "\" (inzwischen erneut verschoben) - zurueck abgebrochen, nichts geaendert.");
            if (!all.ContainsKey((string)m["from"])) throw new Exception("Der alte Ordner \"" + m["fromPath"] + "\" von \"" + m["name"] + "\" existiert nicht mehr.");
            list.Add(new[] { id, (string)m["from"] });
        }
        return PlanMove(c, list, backupName);
    }

    // Sicherung von acmp_cc_move (kind "move", Liste moves) bzw. acmp_cc_update_folder (kind "folders", Liste changes)
    JObject ReadBackup(string backupName, string kind, string tool)
    {
        if (backupName.IndexOfAny(new[] { '/', '\\' }) >= 0 || !backupName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            throw new Exception("undo = Dateiname der Sicherung aus der Antwort von " + tool + " (ohne Pfad).");
        string file = Path.Combine(_backupDir, backupName);
        if (!File.Exists(file)) throw new Exception("Sicherung nicht gefunden: " + backupName);
        JObject b = JObject.Parse(File.ReadAllText(file));
        if ((string)b["kind"] != kind || !(b[kind == "move" ? "moves" : "changes"] is JArray)) throw new Exception(backupName + " ist keine Sicherung von " + tool + ".");
        return b;
    }

    // Sicherung schreiben (vor jeder Aenderung); Name: <Zeit>_<kind>_<Anfang der batchId>.json
    string WriteBackup(JObject b, string kind, string batchId)
    {
        Directory.CreateDirectory(_backupDir);
        string file = Path.Combine(_backupDir, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "_" + kind + "_" + batchId.Trim('{', '}').Substring(0, 8) + ".json");
        File.WriteAllText(file, b.ToString(Formatting.Indented), new UTF8Encoding(false));
        if (new FileInfo(file).Length < 50) throw new Exception("Sicherung unvollstaendig - nichts geaendert.");
        return file;
    }

    JObject PlanAddFolder(SqlConnection c, JObject a)
    {
        List<string[]> list = AddFolderList(a);
        Dictionary<string, CcFolder> all = LoadFolders(c);
        List<CcFolder> plan = PlanNewFolders(all, list, _moveScope, NewGuid);
        var planned = new Dictionary<string, CcFolder>(StringComparer.OrdinalIgnoreCase);
        foreach (CcFolder f in plan) planned[f.Id] = f;
        var arr = new JArray(); var lines = new List<string>();
        foreach (CcFolder f in plan)
        {
            // Rechte = Elternordner; entsteht der im selben Auftrag, sind es die seines vorhandenen Vorfahren (gleiche Eintraege)
            string srcId = f.ParentId; CcFolder pn;
            while (planned.TryGetValue(srcId, out pn)) srcId = pn.ParentId;
            CcFolder src = all[srcId];
            int u = AclCount(c, null, "SYS_Folders_AclEntries_Users", src.Id), g = AclCount(c, null, "SYS_Folders_AclEntries_Groups", src.Id);
            if (u + g == 0) throw new Exception("Elternordner \"" + src.Path + "\" hat keine eigenen Rechte-Eintraege - \"" + f.Path + "\" waere unsichtbar. Nichts geplant.");
            bool parentNew = planned.ContainsKey(f.ParentId);
            var o = new JObject();
            o["folderId"] = f.Id; o["name"] = f.Name; o["parentId"] = f.ParentId;
            o["parentPath"] = parentNew ? planned[f.ParentId].Path : all[f.ParentId].Path; o["path"] = f.Path;
            if (parentNew) o["parentNew"] = true;
            arr.Add(o);
            lines.Add((plan.Count > 1 ? arr.Count + ". " : "") + "\"" + f.Path + "\" (Id " + f.Id + ") - Rechte wie \"" + src.Path + "\" (" + u + " Benutzer, " + g + " Gruppen)" +
                (parentNew ? ", Elternordner entsteht im selben Auftrag" : ""));
        }
        var p = new JObject();
        p["action"] = "add_folder"; p["folders"] = arr;
        var head = new List<string>();
        head.Add(plan.Count == 1 ? "Neuen Client-Command-Ordner anlegen (Standard-Ordnersymbol):"
            : plan.Count + " neue Client-Command-Ordner anlegen (Standard-Ordnersymbol) - eine Transaktion, in dieser Reihenfolge:");
        head.AddRange(lines);
        head.Add("Rechte je Ordner wie der Elternordner. Die Console nimmt beim Anlegen zusaetzlich den angemeldeten Benutzer auf; das passiert hier nicht.");
        head.Add("Umbenennen/Verschieben: acmp_cc_update_folder ; Loeschen in der ACMP-Console. Danach in der Console Aktualisieren (F5).");
        p["vorschau"] = new JArray(head.ToArray());
        return p;
    }

    JObject PlanUpdateFolder(SqlConnection c, JObject a)
    {
        Dictionary<string, CcFolder> all = LoadFolders(c);
        string undo = Str(a["undo"]);
        List<string[]> list;
        if (undo.Length > 0)
        {
            if (Has(a, "changes") || Has(a, "folder") || Has(a, "name") || Has(a, "parent")) throw new Exception("undo nicht zusammen mit changes/folder/name/parent angeben.");
            list = FolderUndoList(ReadBackup(undo, "folders", "acmp_cc_update_folder"), all);
        }
        else list = FolderChangeList(a);
        var skipped = new List<string>();
        List<CcFolderChange> changes = PlanFolderChanges(all, list, _moveScope, skipped);
        if (changes.Count == 0) throw new Exception("Nichts zu aendern - " + string.Join("; ", skipped.ToArray()) + ".");
        Dictionary<string, JObject> cmds = LatestCommands(c, null);
        var arr = new JArray(); var lines = new List<string>();
        foreach (CcFolderChange ch in changes)
        {
            CcFolder f = ch.Folder;
            if (ch.Moved && AclCount(c, null, "SYS_Folders_AclEntries_Users", ch.NewParentId) + AclCount(c, null, "SYS_Folders_AclEntries_Groups", ch.NewParentId) == 0)
                throw new Exception("Neuer Elternordner \"" + all[ch.NewParentId].Path + "\" hat keine eigenen Rechte-Eintraege - \"" + f.Path + "\" waere darin unsichtbar. Nichts geplant.");
            // Was zieht mit? (in der DB aendert sich dafuer nichts, nur der Pfad in der Console)
            int subs = 0, cc = 0;
            foreach (CcFolder x in all.Values) if (IsBelow(all, x, f.Id)) subs++;
            foreach (JObject cmd in cmds.Values)
            {
                CcFolder cf;
                if (all.TryGetValue((string)cmd["folderId"] ?? "", out cf) && (cf.Id == f.Id || IsBelow(all, cf, f.Id))) cc++;
            }
            var o = new JObject();
            o["folderId"] = f.Id; o["oldName"] = f.Name; o["oldParentId"] = f.ParentId; o["oldPath"] = ch.OldPath;
            o["newName"] = ch.NewName; o["newParentId"] = ch.NewParentId; o["newPath"] = ch.NewPath;
            arr.Add(o);
            lines.Add(arr.Count + ". \"" + ch.OldPath + "\" -> \"" + ch.NewPath + "\" (" + (ch.Renamed && ch.Moved ? "umbenennen und verschieben" : ch.Renamed ? "umbenennen" : "verschieben") + ", Id " + f.Id + ")");
            if (subs + cc > 0) lines.Add("   zieht mit: " + subs + " Unterordner, " + cc + " Command(s) - in der DB aendert sich dafuer nichts");
            if (ch.Moved && AclKey(c, "SYS_Folders_AclEntries_Users", "SYS_Folders_AclEntries_Groups", f.Id) != AclKey(c, "SYS_Folders_AclEntries_Users", "SYS_Folders_AclEntries_Groups", ch.NewParentId))
                lines.Add("   Hinweis: Die Rechte des Ordners bleiben; sie weichen von denen des neuen Elternordners ab.");
        }
        var p = new JObject();
        p["action"] = "update_folder"; p["batchId"] = NewGuid(); p["changes"] = arr;
        if (undo.Length > 0) p["undoOf"] = undo;
        var head = new List<string>();
        head.Add(undo.Length > 0
            ? changes.Count + " Client-Command-Ordner auf den Stand vor " + undo + " zuruecksetzen."
            : changes.Count + " Client-Command-Ordner umbenennen/verschieben - eine Transaktion, vorher Sicherung (zurueck mit acmp_cc_update_folder undo=\"<Sicherung>\").");
        head.Add("Geaendert wird nur Name bzw. Elternordner (SYS_SCRIPT_STRUCTURE.Description/ParentID) - wie \"Ordner bearbeiten\" in der Console. Rechte, Icon, Unterordner und Commands bleiben.");
        head.AddRange(lines);
        foreach (string s in skipped) head.Add("Uebersprungen: " + s);
        head.Add("Danach in der Console Aktualisieren (F5). Ordner, die gerade in einer Console bearbeitet werden, vorher schliessen.");
        p["vorschau"] = new JArray(head.ToArray());
        return p;
    }

    // ================= Ausfuehren =================

    public static bool IsFolderWriteTool(string tool)
    {
        return tool == "acmp_cc_move" || tool == "acmp_cc_add_folder" || tool == "acmp_cc_update_folder";
    }

    public void CheckFolderWrite(string tool, string requester)
    {
        CheckWrite(requester, false);
        JObject pf = Preflight(false);
        if (tool == "acmp_cc_add_folder" && (pf["folderTool"] == null || !(bool)pf["folderTool"]))
            throw new Exception("acmp_cc_add_folder gesperrt - Vorpruefung \"Ordner anlegen\" nicht bestanden (Details: acmp_cc_writer_status). Nichts geschrieben.");
        if (tool == "acmp_cc_update_folder" && (pf["folderEditTool"] == null || !(bool)pf["folderEditTool"]))
            throw new Exception("acmp_cc_update_folder gesperrt - Vorpruefung \"Ordner aendern\" nicht bestanden (Details: acmp_cc_writer_status). Nichts geschrieben.");
    }

    // Plant und fuehrt sofort aus (ein _plan kommt nur vom Server, Aufruf-Argumente mit _ verwirft der Server).
    public JObject RunFolderOp(string tool, JObject a, string requester)
    {
        CheckFolderWrite(tool, requester);
        JObject p = a["_plan"] as JObject ?? PlanFolderOp(tool, a);
        if ((string)p["action"] == "add_folder") return ExecAddFolder(p, requester);
        if ((string)p["action"] == "move") return ExecMove(p, requester);
        if ((string)p["action"] == "update_folder") return ExecUpdateFolder(p, requester);
        throw new Exception("Unbekannter Plan: " + p["action"]);
    }

    JObject ExecMove(JObject p, string requester)
    {
        JArray moves = (JArray)p["moves"];
        string backup;
        lock (_lock)
        using (var c = new SqlConnection(_writeCs))
        {
            c.Open();
            Dictionary<string, CcFolder> all = LoadFolders(c);
            foreach (JObject m in moves)
            {
                string id = (string)m["scriptId"], name = (string)m["name"];
                Dictionary<string, JObject> one = LatestCommands(c, id);
                JObject cmd;
                if (!one.TryGetValue(id, out cmd)) throw new Exception("\"" + name + "\" " + id + " gibt es nicht mehr - nichts geaendert.");
                if (!string.Equals((string)cmd["folderId"], (string)m["from"], StringComparison.OrdinalIgnoreCase) || (int)cmd["version"] != (int)m["version"])
                    throw new Exception("\"" + name + "\" wurde seit der Planung verschoben oder neu gespeichert - nichts geaendert, bitte erneut aufrufen.");
                CcFolder dst;
                if (!all.TryGetValue((string)m["to"], out dst)) throw new Exception("Zielordner \"" + m["toPath"] + "\" gibt es nicht mehr - nichts geaendert.");
                if (!InScope(all, dst, _moveScope)) throw new Exception("Zielordner \"" + dst.Path + "\" liegt ausserhalb von ccWriter.moveScopeFolderIds - nichts geaendert.");
                var dq = c.CreateCommand();
                dq.CommandText = "SELECT COUNT(DISTINCT SCRIPTID) FROM SYS_SCRIPTS WHERE FOLDERID=@f AND DESCRIPTION=@n AND SCRIPTID<>@id";
                dq.Parameters.AddWithValue("@f", dst.Id); dq.Parameters.AddWithValue("@n", name); dq.Parameters.AddWithValue("@id", id);
                if (Convert.ToInt32(dq.ExecuteScalar()) > 0) throw new Exception("In \"" + dst.Path + "\" gibt es inzwischen einen Command \"" + name + "\" - nichts geaendert.");
            }
            var b = new JObject();
            b["kind"] = "move"; b["batchId"] = p["batchId"]; b["zeit"] = DateTime.Now.ToString("s"); b["von"] = requester;
            b["grund"] = p["undoOf"] != null ? "vor acmp_cc_move undo=" + p["undoOf"] : "vor acmp_cc_move";
            var bm = new JArray();
            foreach (JObject m in moves)
            {
                var e = new JObject();
                foreach (string k in new[] { "scriptId", "name", "from", "fromPath", "to", "toPath" }) e[k] = m[k];
                bm.Add(e);
            }
            b["moves"] = bm;
            backup = WriteBackup(b, "move", (string)p["batchId"]);
            using (var tx = c.BeginTransaction())
            {
                foreach (JObject m in moves)
                {
                    string id = (string)m["scriptId"], to = (string)m["to"];
                    // Eine ScriptId je Statement (der Update-Trigger bricht sonst ab); er traegt den Ordner in die uebrigen Versionen ein.
                    var up = c.CreateCommand(); up.Transaction = tx;
                    up.CommandText = "UPDATE SYS_SCRIPTS SET FOLDERID=@to WHERE SCRIPTID=@id AND ScriptVersion=@v AND FOLDERID=@from";
                    up.Parameters.AddWithValue("@to", to); up.Parameters.AddWithValue("@id", id);
                    up.Parameters.AddWithValue("@v", (int)m["version"]); up.Parameters.AddWithValue("@from", (string)m["from"]);
                    if (up.ExecuteNonQuery() != 1) throw new Exception("\"" + m["name"] + "\": Ordner nicht eindeutig geaendert - zurueckgerollt, nichts geaendert.");
                    var chk = c.CreateCommand(); chk.Transaction = tx;
                    chk.CommandText = "SELECT COUNT(*) FROM SYS_SCRIPTS WHERE SCRIPTID=@id AND (FOLDERID IS NULL OR FOLDERID<>@to)";
                    chk.Parameters.AddWithValue("@id", id); chk.Parameters.AddWithValue("@to", to);
                    if (Convert.ToInt32(chk.ExecuteScalar()) > 0)
                    {
                        // Trigger hat die aelteren Versionen nicht nachgezogen: selbst nachziehen (weiterhin nur diese ScriptId)
                        var all2 = c.CreateCommand(); all2.Transaction = tx;
                        all2.CommandText = "UPDATE SYS_SCRIPTS SET FOLDERID=@to WHERE SCRIPTID=@id";
                        all2.Parameters.AddWithValue("@to", to); all2.Parameters.AddWithValue("@id", id);
                        all2.ExecuteNonQuery();
                        if (Convert.ToInt32(chk.ExecuteScalar()) > 0) throw new Exception("\"" + m["name"] + "\": nicht alle Versionen im neuen Ordner - zurueckgerollt, nichts geaendert.");
                    }
                }
                tx.Commit();
            }
        }
        _log("acmp_cc_move von " + requester + ": " + moves.Count + " Command(s), Sicherung " + Path.GetFileName(backup));
        return MoveResult(moves, Path.GetFileName(backup));
    }

    // Ergebnis von acmp_cc_move: Sicherung zuerst (die Antwort soll im Chat lesbar bleiben), je Zielordner die Anzahl,
    // dann die Liste - bei vielen Eintraegen gekuerzt, vollstaendig steht sie in der Sicherung.
    public static JObject MoveResult(JArray moves, string backupName)
    {
        var o = new JObject();
        o["sicherung"] = backupName;
        o["ok"] = true; o["anzahl"] = moves.Count;
        o["hinweis"] = "In der Console Aktualisieren (F5). Zurueck: acmp_cc_move undo=\"" + backupName + "\".";
        var perTarget = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var items = new List<string>();
        foreach (JObject m in moves)
        {
            string to = (string)m["toPath"]; int n;
            perTarget.TryGetValue(to, out n); perTarget[to] = n + 1;
            items.Add(m["name"] + ": " + m["fromPath"] + " -> " + to);
        }
        var pt = new JObject();
        foreach (var kv in perTarget) pt[kv.Key] = kv.Value;
        o["jeZielordner"] = pt;
        o["verschoben"] = ShortList(items, " (vollstaendige Liste in der Sicherung " + backupName + ")");
        return o;
    }

    JObject ExecAddFolder(JObject p, string requester)
    {
        JArray fl = p["folders"] as JArray;
        if (fl == null)   // Plan aus einer Anfrage von vor der Mehrfach-Anlage: ein Ordner auf oberster Ebene des Plans
        {
            var one = new JObject();
            foreach (string k in new[] { "folderId", "name", "parentId", "parentPath", "path" }) one[k] = p[k];
            fl = new JArray(one);
        }
        var done = new List<string>(); int aclTotal = 0, aclFirst = 0;
        lock (_lock)
        using (var c = new SqlConnection(_writeCs))
        {
            c.Open();
            Dictionary<string, CcFolder> all = LoadFolders(c);
            // Erst alles pruefen (neue Ordner des Auftrags dabei mitzaehlen), dann in einer Transaktion anlegen
            foreach (JObject f in fl)
            {
                string id = (string)f["folderId"], name = (string)f["name"], parentId = (string)f["parentId"];
                CcFolder parent;
                if (!all.TryGetValue(parentId, out parent)) throw new Exception("Elternordner \"" + f["parentPath"] + "\" gibt es nicht mehr - nichts angelegt.");
                if (all.ContainsKey(id)) throw new Exception("Ordner " + id + " gibt es schon (Anfrage schon ausgefuehrt?) - nichts angelegt.");
                if (!InScope(all, parent, _moveScope)) throw new Exception("Elternordner \"" + parent.Path + "\" liegt ausserhalb von ccWriter.moveScopeFolderIds - nichts angelegt.");
                foreach (CcFolder x in all.Values)
                    if (string.Equals(x.ParentId, parentId, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                        throw new Exception("Unter \"" + parent.Path + "\" gibt es inzwischen einen Ordner \"" + x.Name + "\" - nichts angelegt.");
                var nf = new CcFolder(); nf.Id = id; nf.Name = name; nf.ParentId = parentId; nf.Path = parent.Path + "\\" + name; nf.IsNew = true;
                all[id] = nf;
            }
            using (var tx = c.BeginTransaction())
            {
                foreach (JObject f in fl)
                {
                    string id = (string)f["folderId"], name = (string)f["name"], parentId = (string)f["parentId"];
                    Guid gid = Guid.Parse(id.Trim('{', '}')), gpar = Guid.Parse(parentId.Trim('{', '}'));
                    var ins = c.CreateCommand(); ins.Transaction = tx;
                    ins.CommandText = "INSERT INTO SYS_SCRIPT_STRUCTURE (ScriptStructureID, Description, ParentID, StrucLevel, IconId) VALUES (@id, @n, @p, NULL, NULL)";
                    ins.Parameters.AddWithValue("@id", id); ins.Parameters.AddWithValue("@n", name); ins.Parameters.AddWithValue("@p", parentId);
                    if (ins.ExecuteNonQuery() != 1) throw new Exception("\"" + f["path"] + "\": INSERT hat keinen Ordner angelegt - zurueckgerollt, nichts angelegt.");
                    // Rechte vom Elternordner; ist der im selben Auftrag entstanden, stehen seine Rechte schon in dieser Transaktion
                    int acl = 0;
                    foreach (string kind in new[] { "Users", "Groups" })
                    {
                        var cp = c.CreateCommand(); cp.Transaction = tx;
                        cp.CommandText = "INSERT INTO SYS_Folders_AclEntries_" + kind + " (ObjectID, AccessID, EditState, VisibleState) " +
                            "SELECT @g, f.AccessID, f.EditState, f.VisibleState FROM SYS_Folders_AclEntries_" + kind + " f WHERE f.ObjectID=@p";
                        cp.Parameters.AddWithValue("@g", gid); cp.Parameters.AddWithValue("@p", gpar);
                        acl += cp.ExecuteNonQuery();
                    }
                    if (acl == 0) throw new Exception("\"" + f["path"] + "\": Elternordner hat keine Rechte-Eintraege - zurueckgerollt, nichts angelegt.");
                    if (done.Count == 0) aclFirst = acl;
                    aclTotal += acl;
                    done.Add(f["path"] + " | " + id);
                }
                tx.Commit();
            }
        }
        foreach (JObject f in fl) RegisterFolder((string)f["folderId"], (string)f["name"], (string)f["path"], requester);
        _log("CC-Ordner angelegt von " + requester + ": " + fl.Count + " (" + aclTotal + " Rechte-Eintraege): " + string.Join("; ", done.ToArray()));
        var o = new JObject();
        o["ok"] = true;
        if (fl.Count == 1)
        {
            string id = (string)fl[0]["folderId"];
            o["result"] = "Ordner angelegt."; o["folderId"] = id; o["path"] = fl[0]["path"]; o["rechteEintraege"] = aclFirst;
            o["hinweis"] = "In der Console Aktualisieren (F5). Verschieben hinein: acmp_cc_move folder=\"" + id + "\". Umbenennen/verschieben: acmp_cc_update_folder ; Loeschen in der Console.";
        }
        else
        {
            o["result"] = fl.Count + " Ordner angelegt (" + aclTotal + " Rechte-Eintraege, je Ordner wie der Elternordner).";
            o["hinweis"] = "In der Console Aktualisieren (F5). GUIDs und Baum: acmp_cc_folders. Umbenennen/verschieben: acmp_cc_update_folder ; Loeschen in der Console.";
            o["ordner"] = ShortList(done, " (acmp_cc_folders zeigt alle)");
        }
        return o;
    }

    JObject ExecUpdateFolder(JObject p, string requester)
    {
        JArray changes = (JArray)p["changes"];
        string backup;
        lock (_lock)
        using (var c = new SqlConnection(_writeCs))
        {
            c.Open();
            Dictionary<string, CcFolder> all = LoadFolders(c);
            var list = new List<string[]>();
            foreach (JObject ch in changes)
            {
                string id = (string)ch["folderId"];
                CcFolder f;
                if (!all.TryGetValue(id, out f)) throw new Exception("Ordner \"" + ch["oldPath"] + "\" gibt es nicht mehr - nichts geaendert.");
                if (!string.Equals(f.Name, (string)ch["oldName"], StringComparison.Ordinal) || !string.Equals(f.ParentId, (string)ch["oldParentId"], StringComparison.OrdinalIgnoreCase))
                    throw new Exception("\"" + ch["oldPath"] + "\" wurde seit der Planung geaendert (jetzt \"" + f.Path + "\") - nichts geaendert, bitte erneut aufrufen.");
                if (!all.ContainsKey((string)ch["newParentId"])) throw new Exception("Der neue Elternordner von \"" + ch["oldPath"] + "\" (\"" + ch["newPath"] + "\") gibt es nicht mehr - nichts geaendert.");
                list.Add(new[] { id, (string)ch["newName"], (string)ch["newParentId"] });
            }
            // Dieselben Pruefungen wie bei der Anfrage gegen den jetzigen Stand: Zyklen, gleichnamige Geschwister, Aagon-Ordner, Bereich
            List<CcFolderChange> now = PlanFolderChanges(all, list, _moveScope, null);
            if (now.Count != changes.Count) throw new Exception("Der Ordnerbaum hat sich seit der Planung geaendert - nichts geaendert, bitte erneut aufrufen.");
            var b = new JObject();
            b["kind"] = "folders"; b["batchId"] = p["batchId"]; b["zeit"] = DateTime.Now.ToString("s"); b["von"] = requester;
            b["grund"] = p["undoOf"] != null ? "vor acmp_cc_update_folder undo=" + p["undoOf"] : "vor acmp_cc_update_folder";
            b["changes"] = changes.DeepClone();
            backup = WriteBackup(b, "folders", (string)p["batchId"]);
            using (var tx = c.BeginTransaction())
            {
                foreach (JObject ch in changes)
                {
                    var up = c.CreateCommand(); up.Transaction = tx;
                    up.CommandText = "UPDATE SYS_SCRIPT_STRUCTURE SET Description=@n, ParentID=@p " +
                        "WHERE ScriptStructureID=@id AND ISNULL(Description,'')=@on AND ISNULL(ParentID,'')=@op AND StrucLevel IS NULL";
                    up.Parameters.AddWithValue("@n", (string)ch["newName"]); up.Parameters.AddWithValue("@p", (string)ch["newParentId"]);
                    up.Parameters.AddWithValue("@id", (string)ch["folderId"]);
                    up.Parameters.AddWithValue("@on", (string)ch["oldName"] ?? ""); up.Parameters.AddWithValue("@op", (string)ch["oldParentId"] ?? "");
                    if (up.ExecuteNonQuery() != 1) throw new Exception("\"" + ch["oldPath"] + "\": Ordner nicht eindeutig geaendert - zurueckgerollt, nichts geaendert.");
                }
                // Kontrolle in derselben Transaktion: steht genau der geplante Endstand da? (unbekannter Trigger, gleichzeitige Aenderung)
                foreach (JObject ch in changes)
                {
                    var q = c.CreateCommand(); q.Transaction = tx;
                    q.CommandText = "SELECT Description, ParentID FROM SYS_SCRIPT_STRUCTURE WHERE ScriptStructureID=@id";
                    q.Parameters.AddWithValue("@id", (string)ch["folderId"]);
                    string dn = null, dp = null; int n = 0;
                    using (var r = q.ExecuteReader()) while (r.Read()) { n++; dn = r.IsDBNull(0) ? null : r.GetString(0); dp = r.IsDBNull(1) ? null : r.GetString(1); }
                    if (n != 1 || !string.Equals(dn, (string)ch["newName"], StringComparison.Ordinal) || !string.Equals(dp, (string)ch["newParentId"], StringComparison.OrdinalIgnoreCase))
                        throw new Exception("\"" + ch["oldPath"] + "\": nach dem Aendern nicht wie geplant - zurueckgerollt, nichts geaendert.");
                }
                tx.Commit();
            }
        }
        string bn = Path.GetFileName(backup);
        var items = new List<string>();
        foreach (JObject ch in changes) items.Add(ch["oldPath"] + " -> " + ch["newPath"]);
        _log("acmp_cc_update_folder von " + requester + ": " + changes.Count + " Ordner, Sicherung " + bn + ": " + string.Join("; ", items.ToArray()));
        var o = new JObject();
        o["sicherung"] = bn;
        o["ok"] = true; o["anzahl"] = changes.Count;
        o["hinweis"] = "In der Console Aktualisieren (F5). Zurueck: acmp_cc_update_folder undo=\"" + bn + "\".";
        o["geaendert"] = ShortList(items, " (vollstaendige Liste in der Sicherung " + bn + ")");
        return o;
    }

    // ---------- Register vom MCP angelegter Ordner (nur Nachweis, keine Sonderrechte) ----------
    string FolderRegistryFile { get { return Path.Combine(Path.GetDirectoryName(_registryFile), "cc-folders.json"); } }

    HashSet<string> RegisteredFolders()
    {
        var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try { if (File.Exists(FolderRegistryFile)) foreach (JObject e in JArray.Parse(File.ReadAllText(FolderRegistryFile))) s.Add((string)e["folderId"]); }
        catch { }
        return s;
    }

    void RegisterFolder(string id, string name, string path, string requester)
    {
        lock (_lock)
        {
            JArray a = File.Exists(FolderRegistryFile) ? JArray.Parse(File.ReadAllText(FolderRegistryFile)) : new JArray();
            var e = new JObject(); e["folderId"] = id; e["name"] = name; e["path"] = path; e["requester"] = requester; e["created"] = DateTime.Now.ToString("s");
            a.Add(e);
            File.WriteAllText(FolderRegistryFile, a.ToString(Formatting.Indented), new UTF8Encoding(false));
        }
    }
}
