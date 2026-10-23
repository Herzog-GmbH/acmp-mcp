// AcmpCcRelease: vom MCP angelegte Client-Command-Entwuerfe freigeben (acmp_cc_release) - wie "Freigeben" in der ACMP-Console.
// Beobachtet mit ACMP 6.10.4 an mehreren in der Console freigegebenen Entwuerfen (mit und ohne Dateien):
//  - Die Console aendert beim Freigeben NUR die Zeile der neuesten Version in SYS_SCRIPTS:
//      State 1 -> 7, IsFileRepoVersionIncreased True -> False,
//      FileRepoSize = 0 ohne Dateien, sonst max(1, floor(Summe der Dateigroessen / 1024)) ueber die Dateien der FileRepoVersion.
//  - Keine neue ScriptVersion; Blobs, Name, Ordner, Rechte, Variablen bleiben. LastModifier/LastModifiedDate bleiben ebenfalls
//    (geaendert werden sie nur durch ein Speichern in der Console VOR der Freigabe, nicht durch die Freigabe).
//    Keine Zeilen in anderen Tabellen.
//  - Gibt man eine NEUERE Version eines schon freigegebenen Commands frei, setzt die Console zusaetzlich die bisher freigegebene
//    Version auf State 8 (abgeloest), sonst bleibt sie unveraendert (beobachtet 15.10.2026 an einer Folgeversion
//    ohne Dateien). Seit 15.10.2026 nachgebaut: in derselben Transaktion je Version ein eigenes UPDATE (alte Version 7 -> 8,
//    dann neue 1 -> 7). Folgeversionen mit geaenderten Dateien (andere FileRepoVersion als die freigegebene Version oder
//    IsFileRepoVersionIncreased) sind nicht beobachtet und werden abgelehnt, ebenso Folgeversionen ohne freigegebene Vorversion.
// Regeln: eigene Entwuerfe (cc-drafts.json) und fremde Commands, deren neueste Version zuletzt der MCP gespeichert hat
// (LastModifier "MCP ...", also acmp_cc_new_version) und die nicht in einem Aagon-Standardordner bzw. ausserhalb von
// ccWriter.versionScopeFolderIds liegen; nur State 1; erst dryRun (Vorschau),
// Plan mit SHA-256 von Client-/Console-Skript und Dateien sowie den States aller Versionen (hat sich bis zum Schreiben
// etwas geaendert, wird nichts freigegeben), Sicherung vor dem Schreiben, eine Transaktion je Auftrag, ein UPDATE je
// Zeile (Update-Trigger). Platzhalter in Schritten (z.B. "HIER LIZENZSCHLUESSEL EINTRAGEN", "HIER_...") sperren die Freigabe.
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml;
using Newtonsoft.Json.Linq;

partial class AcmpCcWriter
{
    public const int MaxReleases = 50;
    const int MaxReleaseLines = 400;        // Inhaltszeilen je Command in der Vorschau (wie die Anzeige freigegebener Commands)
    public const string ReleaseHelp =
        "Liste von ScriptIds (oder eindeutigen Namen), 1-50, z.B. [\"{F7B095E6-...}\", \"Deinstall PDFsam (MCP)\"]: vom MCP angelegte Entwuerfe " +
        "oder neue Versionen vorhandener Commands aus acmp_cc_new_version. Fuer einen Command reicht scriptId.";

    // Platzhalter, die vor der Freigabe ersetzt sein muessen (so schreiben wir sie in MCP-Entwuerfe). Gross-/Kleinschreibung zaehlt,
    // damit normaler Text ("hier eintragen") nicht sperrt. Gesucht wird in Feldwerten und Variablen-Startwerten, nicht in Beschreibungen.
    static readonly Regex[] Placeholders = {
        new Regex(@"\bHIER\b[^\r\n]{0,60}?\bEINTRAGEN\b"),
        new Regex(@"\bHIER_[A-Z0-9_]+"),
        new Regex(@"\bPLATZHALTER\b") };
    static readonly string[] NoScanFields = { "Description", "_CommandComment", "ID" };

    // ================= Hilfen ohne DB (offline getestet in CcReleaseTest.cs) =================

    // Gleiche Formel wie UpdateRepoSize (belegt 13.10.2026 an 11 Werten): ohne Dateien 0, sonst Summe in KB abgerundet, mindestens 1
    public static long RepoSizeKB(long sum, int count) { return count == 0 ? 0 : Math.Max(1L, sum / 1024); }

    // Auftrag -> Liste der Commands (ScriptId oder Name): scriptIds=[...] (auch als JSON-Text) oder scriptId="..."
    public static List<string> ReleaseList(JObject a)
    {
        bool hasList = Has(a, "scriptIds"), hasOne = Has(a, "scriptId");
        if (hasList && hasOne) throw new Exception("Entweder scriptIds oder scriptId angeben, nicht beides.");
        var list = new List<string>();
        if (hasList)
        {
            JToken t = a["scriptIds"];
            if (t.Type == JTokenType.String)
            {
                string s = ((string)t).Trim();
                if (s.StartsWith("["))
                {
                    try { t = JToken.Parse(s); }
                    catch (Exception ex) { throw new Exception("scriptIds ist kein gueltiges JSON: " + ex.Message); }
                }
                else t = new JArray(s);
            }
            JArray arr = t as JArray;
            if (arr == null) throw new Exception("scriptIds muss eine Liste sein: " + ReleaseHelp);
            foreach (JToken x in arr)
            {
                string v = x.Type == JTokenType.String ? ((string)x).Trim() : "";
                if (v.Length == 0) throw new Exception("scriptIds: leere Angabe - je Eintrag eine ScriptId oder ein Name.");
                list.Add(v);
            }
        }
        else if (hasOne)
        {
            string v = Str(a["scriptId"]);
            if (v.Length == 0) throw new Exception("scriptId fehlt.");
            list.Add(v);
        }
        else throw new Exception("Was soll freigegeben werden? " + ReleaseHelp);
        if (list.Count == 0) throw new Exception("scriptIds ist leer.");
        if (list.Count > MaxReleases) throw new Exception(list.Count + " Commands - hoechstens " + MaxReleases + " je Auftrag, bitte aufteilen.");
        return list;
    }

    // Platzhalter in Feldwerten der Schritte und in Variablen-Startwerten. Meldung nennt Schritt-Nummer (wie die Vorschau), Klasse,
    // Feld und den gefundenen Platzhalter (in Kennwortfeldern nur das Feld).
    public static List<string> FindPlaceholders(CcScript s)
    {
        var res = new List<string>();
        foreach (CcVar v in s.Vars)
        {
            string m = PlaceholderMatch(v.Value);
            if (m != null) res.Add("Variable " + v.Name + " (Startwert): \"" + m + "\"");
        }
        ScanNodes(s.Roots, "", res);
        return res;
    }

    static void ScanNodes(List<CcNode> nodes, string prefix, List<string> res)
    {
        int i = 0;
        foreach (CcNode n in nodes)
        {
            string num = prefix + (++i);
            var doc = new XmlDocument();
            doc.LoadXml(n.Xml);
            string desc = "";
            foreach (XmlNode e in doc.DocumentElement.ChildNodes)
                if (e.NodeType == XmlNodeType.Element && e.Name == "Description") desc = e.InnerText;
            foreach (XmlNode e in doc.DocumentElement.ChildNodes)
            {
                if (e.NodeType != XmlNodeType.Element || Array.IndexOf(NoScanFields, e.Name) >= 0) continue;
                string m = PlaceholderMatch(e.InnerText);
                if (m == null) continue;
                res.Add("Schritt " + num + " \"" + desc + "\" [" + n.Class + "], Feld " + e.Name + ": " + (SecretField.IsMatch(e.Name) ? "Platzhalter im Kennwortfeld" : "\"" + m + "\""));
            }
            ScanNodes(n.Children, num + ".", res);
        }
    }

    static string PlaceholderMatch(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        foreach (Regex r in Placeholders)
        {
            Match m = r.Match(text);
            if (m.Success) return m.Value.Length > 60 ? m.Value.Substring(0, 57) + "..." : m.Value;
        }
        return null;
    }

    // "MCP <Bediener>" = zuletzt vom MCP gespeichert (acmp_cc_create_draft/update_draft/new_version)
    public static bool IsMcpModifier(string lastModifier) { return lastModifier != null && lastModifier.StartsWith("MCP ", StringComparison.Ordinal); }

    // States aller Versionen als Text "10:1,9:7,8:8" (neueste zuerst), fuer Plan, Vergleich und Kontrolle nach dem Schreiben
    public static List<KeyValuePair<int, int>> ParseStates(string text)
    {
        var l = new List<KeyValuePair<int, int>>();
        if (string.IsNullOrEmpty(text)) return l;
        foreach (string p in text.Split(','))
        {
            string[] kv = p.Split(':');
            l.Add(new KeyValuePair<int, int>(int.Parse(kv[0]), int.Parse(kv[1])));
        }
        return l;
    }

    public static string StatesText(List<KeyValuePair<int, int>> l)
    {
        var parts = new List<string>();
        foreach (var kv in l) parts.Add(kv.Key + ":" + kv.Value);
        return string.Join(",", parts.ToArray());
    }

    // Erwartete States nach der Freigabe: freigegebene Version 7, die bisher freigegebene 8 (0 = keine), alle anderen unveraendert
    public static string StatesAfterRelease(string before, int version, int releasedVersion)
    {
        var l = ParseStates(before);
        for (int i = 0; i < l.Count; i++)
        {
            if (l[i].Key == version) l[i] = new KeyValuePair<int, int>(version, 7);
            else if (releasedVersion > 0 && l[i].Key == releasedVersion) l[i] = new KeyValuePair<int, int>(releasedVersion, 8);
        }
        return StatesText(l);
    }

    // Ordnerregel fuer fremde Commands (wie acmp_cc_new_version): kein Aagon-Standardordner, innerhalb von versionScopeFolderIds
    // (leer = ueberall sonst). null = in Ordnung.
    public static string ReleaseFolderProblem(Dictionary<string, CcFolder> all, string folderId, ICollection<string> scope)
    {
        CcFolder f;
        if (folderId == null || !all.TryGetValue(folderId, out f)) return "liegt in einem unbekannten Ordner (" + folderId + ")";
        if (f.IsAagon && !f.IsRoot) return "liegt im Aagon-Standardordner \"" + f.Path + "\" - Aagon-Commands nur in der ACMP-Console freigeben";
        if (!InScope(all, f, scope)) return "liegt in \"" + f.Path + "\", ausserhalb von ccWriter.versionScopeFolderIds - in der ACMP-Console freigeben";
        return null;
    }

    // Pruefung eines Command-Stands (aus ReleaseSnapshot) fuer die Freigabe. blockers sperren, warnings sind Hinweise.
    // registered = eigener MCP-Entwurf (cc-drafts.json). Fremde Commands nur, wenn die neueste Version zuletzt der MCP gespeichert hat
    // (acmp_cc_new_version) und s["folderProblem"] fehlt (setzt der Aufrufer mit ReleaseFolderProblem).
    // Rueckgabe true = schon freigegeben (wird uebersprungen, kein Fehler).
    public static bool CheckRelease(JObject s, bool registered, List<string> placeholders, string decodeError, List<string> blockers, List<string> warnings)
    {
        int versions = (int)s["versions"], state = (int)s["state"], version = (int)s["version"];
        string lm = (string)s["lastModifier"];
        if (!registered)
        {
            if (!IsMcpModifier(lm))
            {
                blockers.Add("nicht vom MCP angelegt (fehlt in cc-drafts.json) und v" + version + " hat zuletzt " + (lm ?? "?") + " gespeichert - fremde Commands nur, " +
                             "wenn ihre neueste Version vom MCP stammt (acmp_cc_new_version); sonst in der ACMP-Console freigeben");
                return false;
            }
            if (s["folderProblem"] != null) { blockers.Add((string)s["folderProblem"]); return false; }
        }
        if (state == 7) return true;
        if (state != 1) { blockers.Add("ist kein Entwurf (State " + state + ")"); return false; }
        if (versions > 1) CheckOlderVersions(s, blockers, warnings);
        if (decodeError != null) blockers.Add("Client-Skript nicht lesbar: " + decodeError);
        if (placeholders != null && placeholders.Count > 0)
            blockers.Add("enthaelt Platzhalter, die vor der Freigabe ersetzt werden muessen (acmp_cc_update_draft oder Console): " + string.Join("; ", placeholders.ToArray()));
        if ((int)s["fileRepoVersion"] < 1) blockers.Add("FileRepoVersion " + s["fileRepoVersion"] + " unerwartet");
        JArray files = s["files"] as JArray;
        if (files != null)
            foreach (JObject f in files)
            {
                if (f["folder"] != null) continue;
                if (f["outside"] != null) blockers.Add("Datei \"" + f["path"] + "\": Pfad zeigt aus dem Ordner des Commands heraus");
                else if (f["missing"] != null) blockers.Add("Datei \"" + f["path"] + "\" fehlt im File Repository (" + f["disk"] + ")");
                else if (f["diskSha256"] != null && !string.Equals((string)f["diskSha256"], (string)f["sha256"], StringComparison.OrdinalIgnoreCase))
                    blockers.Add("Datei \"" + f["path"] + "\": SHA-256 im File Repository passt nicht zur DB (Clients wuerden den Download verwerfen)");
            }
        if (s["noRepository"] != null) blockers.Add("File Repository nicht gefunden (" + s["noRepository"] + ", ccWriter.fileRepositoryRoot)");
        JArray orphans = s["orphans"] as JArray;
        if (orphans != null && orphans.Count > 0)
            warnings.Add("im Repository-Ordner liegen Dateien ohne DB-Eintrag (werden nicht verteilt und nicht mitgezaehlt): " + string.Join(", ", orphans.ToObject<string[]>()));
        string ld = (string)s["longDescription"] ?? "";
        if (ld.IndexOf("vor der Freigabe", StringComparison.OrdinalIgnoreCase) >= 0)
            warnings.Add("Beschreibung verlangt etwas vor der Freigabe: \"" + OneLine(ld, 160) + "\"");
        return false;
    }

    // Folgeversion (v2+): genau eine aeltere Version freigegeben (State 7, wird abgeloest), alle anderen abgeloest (State 8), dieselben
    // Dateien wie die freigegebene Version (die Freigabe einer Folgeversion mit neuen Dateien ist nicht beobachtet).
    static void CheckOlderVersions(JObject s, List<string> blockers, List<string> warnings)
    {
        int version = (int)s["version"];
        var rel = new List<int>(); var drafts = new List<string>(); var odd = new List<string>();
        foreach (var kv in ParseStates((string)s["versionStates"]))
        {
            if (kv.Key == version) continue;
            if (kv.Value == 7) rel.Add(kv.Key);
            else if (kv.Value == 1) drafts.Add("v" + kv.Key);
            else if (kv.Value != 8) odd.Add("v" + kv.Key + " State " + kv.Value);
        }
        if (drafts.Count > 0) blockers.Add("weitere Entwuerfe neben v" + version + " (" + string.Join(", ", drafts.ToArray()) + ") - unerwartet, in der ACMP-Console pruefen");
        if (odd.Count > 0) blockers.Add("aeltere Versionen mit unbekanntem State (" + string.Join(", ", odd.ToArray()) + ") - in der ACMP-Console pruefen");
        if (rel.Count > 1) { blockers.Add("mehrere freigegebene Versionen (v" + string.Join(", v", rel.ConvertAll(x => x.ToString()).ToArray()) + ") - unerwartet, in der ACMP-Console pruefen"); return; }
        if (rel.Count == 0)
        {
            blockers.Add("v" + version + " hat keine freigegebene Vorversion (aeltere Versionen alle abgeloest) - so eine Freigabe ist nicht beobachtet; in der ACMP-Console freigeben");
            return;
        }
        int rv = s["releasedVersion"] != null ? (int)s["releasedVersion"] : 0;
        if (rv != rel[0]) { blockers.Add("freigegebene Version unklar (v" + rel[0] + " / v" + rv + ")"); return; }
        int frv = (int)s["fileRepoVersion"], rfrv = s["releasedFileRepoVersion"] != null ? (int)s["releasedFileRepoVersion"] : -1;
        bool inc = s["isFileRepoVersionIncreased"] != null && s["isFileRepoVersionIncreased"].Type == JTokenType.Boolean && (bool)s["isFileRepoVersionIncreased"];
        if (frv != rfrv || inc)
            blockers.Add("v" + version + " hat geaenderte Dateien (FileRepoVersion " + rfrv + " -> " + frv + (inc ? ", IsFileRepoVersionIncreased" : "") +
                         ") - die Freigabe einer Folgeversion mit neuen Dateien ist nicht beobachtet; in der ACMP-Console freigeben");
        else if (s["newFileRepoSize"] != null && s["releasedFileRepoSize"] != null && (long)s["newFileRepoSize"] != (long)s["releasedFileRepoSize"])
            warnings.Add("FileRepoSize der freigegebenen v" + rv + ": " + s["releasedFileRepoSize"] + " KB, jetzt berechnet " + s["newFileRepoSize"] +
                         " KB - die Dateien (gemeinsam mit v" + rv + ") haben sich seit deren Freigabe geaendert");
    }

    // Unterschiede zwischen dem eingefrorenen Plan-Eintrag und dem jetzigen Stand (leer = unveraendert).
    // Plaene der ersten Fassung (ohne "versions"/"releasedVersion"/"versionStates") galten nur fuer Commands mit einer Version.
    public static List<string> ReleaseDiff(JObject planned, JObject now)
    {
        var d = new List<string>();
        int pv = planned["versions"] != null ? (int)planned["versions"] : 1;
        if ((int)now["versions"] != pv) d.Add("jetzt " + now["versions"] + " Versionen (geplant " + pv + ")");
        if ((int)now["version"] != (int)planned["version"]) d.Add("Version " + planned["version"] + " -> " + now["version"]);
        int prv = planned["releasedVersion"] != null ? (int)planned["releasedVersion"] : 0, nrv = now["releasedVersion"] != null ? (int)now["releasedVersion"] : 0;
        if (prv != nrv) d.Add("freigegebene Version jetzt " + (nrv > 0 ? "v" + nrv : "keine") + " (geplant " + (prv > 0 ? "v" + prv : "keine") + ")");
        if (planned["versionStates"] != null && (string)planned["versionStates"] != (string)now["versionStates"]) d.Add("States der Versionen " + planned["versionStates"] + " -> " + now["versionStates"]);
        if (planned["lastModifier"] != null && !SameText(now["lastModifier"], planned["lastModifier"])) d.Add("zuletzt gespeichert von " + now["lastModifier"]);
        if ((int)now["state"] != 1) d.Add("State jetzt " + now["state"]);
        if (!SameText(now["clientSha256"], planned["clientSha256"])) d.Add("Client-Skript geaendert");
        if (!SameText(now["consoleSha256"], planned["consoleSha256"])) d.Add("Console-Skript geaendert");
        if ((string)now["name"] != (string)planned["name"]) d.Add("Name jetzt \"" + now["name"] + "\"");
        if (!SameText(now["folderId"], planned["folderId"])) d.Add("Ordner geaendert");
        if ((int)now["fileRepoVersion"] != (int)planned["fileRepoVersion"]) d.Add("FileRepoVersion " + planned["fileRepoVersion"] + " -> " + now["fileRepoVersion"]);
        if (FileKey(now["files"] as JArray) != FileKey(planned["files"] as JArray)) d.Add("angehaengte Dateien geaendert");
        return d;
    }

    static bool SameText(JToken a, JToken b)
    {
        return string.Equals(a != null && a.Type != JTokenType.Null ? (string)a : "", b != null && b.Type != JTokenType.Null ? (string)b : "", StringComparison.OrdinalIgnoreCase);
    }

    // Vergleichbarer Text der Dateiliste: Pfad|SHA-256 (DB), sortiert
    static string FileKey(JArray files)
    {
        var l = new List<string>();
        if (files != null)
            foreach (JObject f in files) l.Add(((string)f["path"] ?? "").ToLowerInvariant() + "|" + (f["folder"] != null ? "folder" : ((string)f["sha256"] ?? "").ToUpperInvariant()));
        l.Sort(StringComparer.Ordinal);
        return string.Join("\n", l.ToArray());
    }

    static string OneLine(string s, int max)
    {
        string t = Regex.Replace(s ?? "", @"\s+", " ").Trim();
        return t.Length > max ? t.Substring(0, max - 3) + "..." : t;
    }

    // Vorschau-Kopf eines Commands (eine Zeile + Dateien)
    public static List<string> ReleaseHeadLines(int no, JObject it)
    {
        var l = new List<string>();
        int rv = it["releasedVersion"] != null ? (int)it["releasedVersion"] : 0;
        l.Add(no + ". \"" + it["name"] + "\" " + it["scriptId"] + " - v" + it["version"] + (rv > 0 ? " (loest die freigegebene v" + rv + " ab: v" + rv + " State 7 -> 8)" : "") +
              ", Ordner \"" + it["folderPath"] + "\", " + it["totalSteps"] + " Schritt(e)");
        if (it["foreign"] != null)
            l.Add("   Fremder Command (nicht in cc-drafts.json): v" + it["version"] + " angelegt von " + ((string)it["creator"] ?? "?") + ", zuletzt gespeichert von " + it["lastModifier"] + " (acmp_cc_new_version)");
        JArray files = it["files"] as JArray;
        var fl = new List<string>();
        if (files != null) foreach (JObject f in files) if (f["folder"] == null) fl.Add(f["path"] + " (" + ((long)f["bytes"]).ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("de-DE")) + " Byte)");
        long before = (long)it["fileRepoSizeBefore"], after = (long)it["fileRepoSize"];
        l.Add("   Dateien: " + (fl.Count == 0 ? "keine" : string.Join(", ", fl.ToArray()) + ", SHA-256 im File Repository = DB") +
              " | FileRepoSize " + (before == after ? after + " KB (unveraendert)" : before + " -> " + after + " KB"));
        return l;
    }

    // Vorschau einer Folgeversion: Aenderungen gegenueber der bisher freigegebenen Version (wie acmp_cc_new_version, Kennwoerter maskiert)
    public List<string> ReleaseDiffLines(byte[] releasedClient, CcScript script, int releasedVersion)
    {
        var l = new List<string>();
        CcScript rel;
        try { rel = CcFormat.Decode(releasedClient); }
        catch (Exception ex) { l.Add("   Aenderungen gegenueber v" + releasedVersion + ": nicht vergleichbar (Client-Skript von v" + releasedVersion + " nicht lesbar: " + ex.Message + ") - Inhalt pruefen."); return l; }
        List<string> diff = DiffLines(DiffRender(rel), DiffRender(script), 2);
        if (diff == null) l.Add("   Aenderungen gegenueber v" + releasedVersion + ": Skript zu gross fuer den Zeilenvergleich - Inhalt pruefen (details=true).");
        else if (diff.Count == 0) l.Add("   Aenderungen gegenueber der freigegebenen v" + releasedVersion + ": KEINE (Schritte und Variablen gleich).");
        else
        {
            l.Add("   Aenderungen gegenueber der freigegebenen v" + releasedVersion + " (- alt, + neu, Kennwoerter maskiert):");
            int shown = 0;
            foreach (string d in diff)
            {
                if (shown++ >= MaxReleaseLines) { l.Add("      ... (" + (diff.Count - MaxReleaseLines) + " weitere Zeilen)"); break; }
                l.Add("      " + d);
            }
        }
        return l;
    }

    // ================= Planen (dryRun und Ausfuehrung) =================

    // withContent: Inhalt jedes Commands in die Vorschau (Ausfuehrung immer; dryRun nur mit details=true, sonst kompakt fuer den Chat)
    public JObject PlanRelease(JObject a, bool withContent)
    {
        if (string.IsNullOrEmpty(_readCs)) throw new Exception("DB-Lesezugang (db.connectionString) fehlt.");
        List<string> list = ReleaseList(a);
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            Dictionary<string, CcFolder> folders = LoadFolders(c);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var items = new JArray(); var skipped = new List<string>(); var problems = new List<string>();
            var body = new List<string>(); var allWarn = new List<string>();
            foreach (string arg in list)
            {
                JObject cmd;
                try { cmd = ResolveCommand(c, arg); }
                catch (Exception ex) { problems.Add(ex.Message); continue; }
                string id = (string)cmd["scriptId"];
                if (!seen.Add(id)) { problems.Add("\"" + cmd["name"] + "\" " + id + " steht mehrfach im Auftrag."); continue; }
                byte[] blob, relBlob;
                JObject s = ReleaseSnapshot(c, null, id, true, out blob, out relBlob);
                string label = "\"" + s["name"] + "\" " + id;
                CcScript script = null; string decodeError = null;
                try { if (blob != null) script = CcFormat.Decode(blob); else decodeError = "kein Client-Skript"; }
                catch (Exception ex) { decodeError = ex.Message; }
                bool registered = IsRegistered(id);
                string fp = registered ? null : ReleaseFolderProblem(folders, (string)s["folderId"], _versionScope);
                if (fp != null) s["folderProblem"] = fp;
                var blockers = new List<string>(); var warnings = new List<string>();
                bool released = CheckRelease(s, registered, script != null ? FindPlaceholders(script) : null, decodeError, blockers, warnings);
                if (released) { skipped.Add(label + " ist schon freigegeben"); continue; }
                if (blockers.Count > 0) { problems.Add(label + ": " + string.Join("; ", blockers.ToArray())); continue; }
                CcFolder f; folders.TryGetValue((string)s["folderId"] ?? "", out f);
                var it = new JObject();
                foreach (string k in new[] { "scriptId", "name", "version", "folderId", "clientSha256", "consoleSha256", "fileRepoVersion",
                                             "versions", "releasedVersion", "versionStates", "creator", "lastModifier" }) it[k] = s[k];
                if (!registered) it["foreign"] = true;
                it["folderPath"] = f != null ? f.Path : "(unbekannter Ordner " + s["folderId"] + ")";
                it["totalSteps"] = script.TotalSteps;
                var pf = new JArray();
                foreach (JObject x in (JArray)s["files"])
                {
                    var y = new JObject(); y["path"] = x["path"];
                    if (x["folder"] != null) y["folder"] = true; else { y["sha256"] = x["sha256"]; y["bytes"] = x["bytes"]; }
                    pf.Add(y);
                }
                it["files"] = pf;
                it["fileRepoSize"] = s["newFileRepoSize"];
                it["fileRepoSizeBefore"] = s["fileRepoSize"];
                items.Add(it);
                body.AddRange(ReleaseHeadLines(items.Count, it));
                foreach (string w in warnings) { body.Add("   Hinweis: " + w); allWarn.Add(label + ": " + w); }
                int rv = (int)s["releasedVersion"];
                if (rv > 0) body.AddRange(ReleaseDiffLines(relBlob, script, rv));
                JArray content = PreviewScript(new List<string>(), script);
                if (!withContent) { body.Add("   Inhalt: " + content.Count + " Zeilen (Client-Skript, SHA-256 " + ((string)s["clientSha256"]).Substring(0, 16) + "...) - mit details=true"); continue; }
                body.Add("   Inhalt (Client-Skript, SHA-256 " + ((string)s["clientSha256"]).Substring(0, 16) + "...):");
                int shown = 0;
                foreach (JToken t in content)
                {
                    if (shown++ >= MaxReleaseLines) { body.Add("      ... (" + (content.Count - MaxReleaseLines) + " weitere Zeilen, vollstaendig mit acmp_cc_decode)"); break; }
                    body.Add("      " + (string)t);
                }
            }
            if (problems.Count > 0)
                throw new Exception("Nichts geplant - " + problems.Count + " Command(s) koennen nicht freigegeben werden: " + string.Join(" | ", problems.ToArray()) +
                    (items.Count > 0 ? " (die uebrigen " + items.Count + " waeren freigebbar; ohne die genannten erneut aufrufen)" : ""));
            if (items.Count == 0) throw new Exception("Nichts freizugeben - " + string.Join("; ", skipped.ToArray()) + ".");
            var p = new JObject();
            p["action"] = "release"; p["batchId"] = NewGuid(); p["items"] = items;
            if (skipped.Count > 0) p["uebersprungen"] = new JArray(skipped.ToArray());
            if (allWarn.Count > 0) p["hinweise"] = new JArray(allWarn.ToArray());
            int follow = 0;
            foreach (JObject x in items) if ((int)x["releasedVersion"] > 0) follow++;
            var head = new List<string>();
            head.Add(items.Count + " Client Command(s) FREIGEBEN - wie \"Freigeben\" in der ACMP-Console. Eine Transaktion, vorher Sicherung.");
            head.Add("Danach sind sie sofort per Kiosk, Job und acmp_enqueue_client_command auf Clients ausfuehrbar. Bitte den Inhalt unten pruefen.");
            head.Add("Geaendert wird je Command nur SYS_SCRIPTS: neueste Version State 1 -> 7 (freigegeben), IsFileRepoVersionIncreased -> False, FileRepoSize (KB der angehaengten Dateien)" +
                     (follow > 0 ? "; bei " + follow + " Folgeversion(en) zusaetzlich die bisher freigegebene Version State 7 -> 8 (abgeloest, laeuft danach nicht mehr)" : "") +
                     ". Inhalt, Name, Ordner, Rechte, Variablen bleiben.");
            head.AddRange(body);
            foreach (string s in skipped) head.Add("Uebersprungen: " + s);
            head.Add("Bei der Ausfuehrung wird verglichen: Versionen und ihre States, SHA-256 von Client- und Console-Skript, Name, Ordner, letzter Bearbeiter, Dateien (DB und File Repository). Weicht etwas ab, wird NICHTS freigegeben.");
            head.Add("Zuruecknehmen kann das Tool eine Freigabe nicht; die alten Werte stehen in der Sicherung. Danach in der Console Aktualisieren (F5).");
            p["vorschau"] = new JArray(head.ToArray());
            return p;
        }
    }

    // Stand eines Commands fuer die Freigabe. withDisk: Dateien im File Repository lesen (Groesse, SHA-256), FileRepoSize berechnen,
    // Dateien ohne DB-Eintrag melden. tx != null: in der Freigabe-Transaktion mit Sperre lesen (kein Plattenzugriff).
    // Ausgewertet wird die neueste Version; dazu die States aller Versionen und die bisher freigegebene Version (State 7 unter der
    // neuesten: releasedVersion, deren FileRepoVersion/FileRepoSize und Client-Skript fuer den Vergleich).
    JObject ReleaseSnapshot(SqlConnection c, SqlTransaction tx, string id, bool withDisk, out byte[] clientBlob, out byte[] releasedBlob)
    {
        clientBlob = null; releasedBlob = null;
        var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = "SELECT ScriptVersion, CAST(State AS int), DESCRIPTION, FOLDERID, LONGDESCRIPTION, CLIENTSCRIPT, CONSOLESCRIPT, FileRepoVersion, FileRepoSize, " +
            "CAST(IsFileRepoVersionIncreased AS int), LastModifier, LastModifiedDate, Creator FROM SYS_SCRIPTS" + (tx != null ? " WITH (UPDLOCK, HOLDLOCK)" : "") +
            " WHERE SCRIPTID=@id ORDER BY ScriptVersion DESC";
        q.Parameters.AddWithValue("@id", id);
        var s = new JObject(); int rows = 0, rv = 0;
        var states = new List<KeyValuePair<int, int>>();
        using (var r = q.ExecuteReader())
            while (r.Read())
            {
                int ver = r.GetInt32(0), st = r.GetInt32(1);
                states.Add(new KeyValuePair<int, int>(ver, st));
                if (rows++ > 0)
                {
                    if (st == 7 && rv == 0)
                    {
                        rv = ver;
                        if (!r.IsDBNull(5)) releasedBlob = (byte[])r.GetValue(5);
                        s["releasedFileRepoVersion"] = r.IsDBNull(7) ? 0 : Convert.ToInt32(r.GetValue(7));
                        s["releasedFileRepoSize"] = r.IsDBNull(8) ? 0L : Convert.ToInt64(r.GetValue(8));
                    }
                    continue;
                }
                s["scriptId"] = id;
                s["version"] = r.GetInt32(0);
                s["state"] = r.GetInt32(1);
                s["name"] = r.IsDBNull(2) ? "" : r.GetString(2);
                s["folderId"] = r.IsDBNull(3) ? null : r.GetString(3);
                s["longDescription"] = r.IsDBNull(4) ? "" : r.GetString(4);
                if (!r.IsDBNull(5)) clientBlob = (byte[])r.GetValue(5);
                s["clientSha256"] = Sha256Hex(clientBlob);
                s["consoleSha256"] = Sha256Hex(r.IsDBNull(6) ? null : (byte[])r.GetValue(6));
                s["fileRepoVersion"] = r.IsDBNull(7) ? 0 : Convert.ToInt32(r.GetValue(7));
                s["fileRepoSize"] = r.IsDBNull(8) ? 0L : Convert.ToInt64(r.GetValue(8));
                s["isFileRepoVersionIncreased"] = r.IsDBNull(9) ? (JToken)null : r.GetInt32(9) != 0;
                s["lastModifier"] = r.IsDBNull(10) ? null : r.GetString(10);
                s["lastModifiedDate"] = r.IsDBNull(11) ? null : (JToken)Convert.ToDouble(r.GetValue(11));
                s["creator"] = r.IsDBNull(12) ? null : r.GetString(12);
            }
        if (rows == 0) throw new Exception("Client Command " + id + " nicht gefunden.");
        s["versions"] = rows;
        s["versionStates"] = StatesText(states);
        s["releasedVersion"] = rv;
        var fq = c.CreateCommand(); fq.Transaction = tx;
        fq.CommandText = "SELECT Path, CAST(IsFolder AS int), sha256 FROM SYS_SCRIPT_FILES" + (tx != null ? " WITH (HOLDLOCK)" : "") +
            " WHERE ScriptID=@id AND FileRepoVersion=@rv ORDER BY Path";
        fq.Parameters.AddWithValue("@id", id); fq.Parameters.AddWithValue("@rv", (int)s["fileRepoVersion"]);
        var files = new JArray();
        using (var r = fq.ExecuteReader())
            while (r.Read())
            {
                var f = new JObject(); f["path"] = r.IsDBNull(0) ? "" : r.GetString(0);
                if (!r.IsDBNull(1) && r.GetInt32(1) != 0) f["folder"] = true;
                else f["sha256"] = r.IsDBNull(2) ? "" : BitConverter.ToString((byte[])r.GetValue(2)).Replace("-", "");
                files.Add(f);
            }
        s["files"] = files;
        if (!withDisk) return s;
        string dir = Path.Combine(_fileRepoRoot, id, s["fileRepoVersion"].ToString());
        long sum = 0; int count = 0;
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool anyFile = false;
        foreach (JObject f in files) if (f["folder"] == null) anyFile = true;
        if (anyFile && !Directory.Exists(_fileRepoRoot)) s["noRepository"] = _fileRepoRoot;
        else
            foreach (JObject f in files)
            {
                if (f["folder"] != null) continue;
                string full = Path.GetFullPath(Path.Combine(dir, (string)f["path"]));
                if (!full.StartsWith(Path.GetFullPath(dir) + "\\", StringComparison.OrdinalIgnoreCase)) { f["outside"] = true; continue; }
                listed.Add(full);
                f["disk"] = full;
                var fi = new FileInfo(full);
                if (!fi.Exists) { f["missing"] = true; continue; }
                f["bytes"] = fi.Length;
                using (var st = File.OpenRead(full)) using (var sha = SHA256.Create()) f["diskSha256"] = BitConverter.ToString(sha.ComputeHash(st)).Replace("-", "");
                sum += fi.Length; count++;
            }
        s["newFileRepoSize"] = RepoSizeKB(sum, count);
        var orphans = new JArray();
        if (Directory.Exists(dir))
            foreach (var fi in new DirectoryInfo(dir).GetFiles("*", SearchOption.AllDirectories))
                if (!listed.Contains(fi.FullName) && !fi.Name.EndsWith(".mcp-upload", StringComparison.OrdinalIgnoreCase)) orphans.Add(fi.FullName.Substring(dir.Length).TrimStart('\\'));
        if (orphans.Count > 0) s["orphans"] = orphans;
        return s;
    }

    static string Sha256Hex(byte[] b)
    {
        if (b == null) return "";
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(b)).Replace("-", "");
    }

    // ================= Ausfuehren (nur mit dem Plan, den der Server direkt vorher erstellt hat) =================

    public void CheckReleaseWrite(string requester)
    {
        CheckWrite(requester, false);
        JObject pf = Preflight(false);
        if (pf["releaseTool"] == null || !(bool)pf["releaseTool"])
            throw new Exception("acmp_cc_release gesperrt - Vorpruefung \"Freigeben\" nicht bestanden (Details: acmp_cc_writer_status). Nichts freigegeben.");
    }

    public JObject RunRelease(JObject a, string requester)
    {
        CheckReleaseWrite(requester);
        JObject p = a["_plan"] as JObject;
        if (p == null || (string)p["action"] != "release")
            throw new Exception("acmp_cc_release laeuft nur mit einem vom Server erstellten Plan - nichts freigegeben.");
        return ExecRelease(p, requester);
    }

    JObject ExecRelease(JObject p, string requester)
    {
        JArray items = (JArray)p["items"];
        string backup;
        lock (_lock)
        using (var c = new SqlConnection(_writeCs))
        {
            c.Open();
            // 1) Jetzigen Stand gegen den Plan pruefen, inkl. Dateien im File Repository (ohne Sperre, Platte lesen kann dauern)
            var problems = new List<string>(); var before = new JArray();
            Dictionary<string, CcFolder> folders = LoadFolders(c);
            foreach (JObject it in items)
            {
                string id = (string)it["scriptId"], label = "\"" + it["name"] + "\" " + id;
                byte[] blob, relBlob;
                JObject now = ReleaseSnapshot(c, null, id, true, out blob, out relBlob);
                List<string> diff = ReleaseDiff(it, now);
                if (diff.Count > 0) { problems.Add(label + " wurde seit der Planung geaendert (" + string.Join(", ", diff.ToArray()) + ")"); continue; }
                CcScript script = null; string decodeError = null;
                try { script = CcFormat.Decode(blob); } catch (Exception ex) { decodeError = ex.Message; }
                bool registered = IsRegistered(id);
                string fp = registered ? null : ReleaseFolderProblem(folders, (string)now["folderId"], _versionScope);
                if (fp != null) now["folderProblem"] = fp;
                var blockers = new List<string>(); var warnings = new List<string>();
                CheckRelease(now, registered, script != null ? FindPlaceholders(script) : null, decodeError, blockers, warnings);
                if (blockers.Count > 0) { problems.Add(label + ": " + string.Join("; ", blockers.ToArray())); continue; }
                if ((long)now["newFileRepoSize"] != (long)it["fileRepoSize"]) { problems.Add(label + ": Dateigroessen im File Repository haben sich geaendert"); continue; }
                before.Add(now);
            }
            if (problems.Count > 0) throw new Exception("Nichts freigegeben - " + string.Join(" | ", problems.ToArray()) + ". Bitte erneut aufrufen.");
            // 2) Sicherung der Werte, die sich aendern (plus Kennung des Inhalts)
            var b = new JObject();
            b["kind"] = "release"; b["batchId"] = p["batchId"]; b["zeit"] = DateTime.Now.ToString("s"); b["von"] = requester;
            b["grund"] = "vor acmp_cc_release (State 1 -> 7, IsFileRepoVersionIncreased -> False, FileRepoSize; bei Folgeversionen bisher freigegebene Version State 7 -> 8)";
            var bc = new JArray();
            foreach (JObject now in before)
            {
                var e = new JObject();
                foreach (string k in new[] { "scriptId", "name", "version", "state", "isFileRepoVersionIncreased", "fileRepoSize", "lastModifier", "lastModifiedDate",
                                             "folderId", "clientSha256", "consoleSha256", "fileRepoVersion", "creator", "versions", "versionStates", "releasedVersion" }) e[k] = now[k];
                bc.Add(e);
            }
            b["commands"] = bc;
            backup = WriteBackup(b, "release", (string)p["batchId"]);
            // 3) Freigeben in einer Transaktion; je Command erst mit Sperre erneut lesen und vergleichen, dann ein UPDATE je Zeile
            //    (Update-Trigger): bisher freigegebene Version 7 -> 8, danach neueste Version 1 -> 7. Zu keinem Zeitpunkt zwei freigegebene.
            using (var tx = c.BeginTransaction())
            {
                foreach (JObject it in items)
                {
                    string id = (string)it["scriptId"], label = "\"" + it["name"] + "\"";
                    byte[] blob, relBlob;
                    JObject locked = ReleaseSnapshot(c, tx, id, false, out blob, out relBlob);
                    List<string> diff = ReleaseDiff(it, locked);
                    if (diff.Count > 0) throw new Exception(label + " wurde gerade geaendert (" + string.Join(", ", diff.ToArray()) + ") - zurueckgerollt, nichts freigegeben.");
                    long kb = (long)it["fileRepoSize"];
                    int v = (int)it["version"], rv = it["releasedVersion"] != null ? (int)it["releasedVersion"] : 0;
                    if (rv > 0)
                    {
                        var old = c.CreateCommand(); old.Transaction = tx;
                        old.CommandText = "UPDATE SYS_SCRIPTS SET State=8 WHERE SCRIPTID=@id AND ScriptVersion=@rv AND State=7";
                        old.Parameters.AddWithValue("@id", id); old.Parameters.AddWithValue("@rv", rv);
                        if (old.ExecuteNonQuery() != 1) throw new Exception(label + ": v" + rv + " nicht eindeutig abgeloest - zurueckgerollt, nichts freigegeben.");
                    }
                    var up = c.CreateCommand(); up.Transaction = tx;
                    up.CommandText = "UPDATE SYS_SCRIPTS SET State=7, IsFileRepoVersionIncreased=0, FileRepoSize=@kb WHERE SCRIPTID=@id AND ScriptVersion=@v AND State=1";
                    up.Parameters.Add("@kb", SqlDbType.BigInt).Value = kb;
                    up.Parameters.AddWithValue("@id", id); up.Parameters.AddWithValue("@v", v);
                    if (up.ExecuteNonQuery() != 1) throw new Exception(label + ": nicht eindeutig freigegeben - zurueckgerollt, nichts freigegeben.");
                    // Kontrolle in derselben Transaktion: alle Versionen mit den erwarteten States (neueste 7, bisher freigegebene 8, Rest
                    // unveraendert), neueste wie geplant (unbekannter Trigger, gleichzeitige Aenderung)
                    string expect = StatesAfterRelease((string)locked["versionStates"], v, rv);
                    var chk = c.CreateCommand(); chk.Transaction = tx;
                    chk.CommandText = "SELECT ScriptVersion, CAST(State AS int), FileRepoSize, CAST(IsFileRepoVersionIncreased AS int) FROM SYS_SCRIPTS WHERE SCRIPTID=@id ORDER BY ScriptVersion DESC";
                    chk.Parameters.AddWithValue("@id", id);
                    var got = new List<KeyValuePair<int, int>>(); bool topOk = false;
                    using (var r = chk.ExecuteReader())
                        while (r.Read())
                        {
                            got.Add(new KeyValuePair<int, int>(r.GetInt32(0), r.GetInt32(1)));
                            if (r.GetInt32(0) == v) topOk = r.GetInt32(1) == 7 && !r.IsDBNull(2) && Convert.ToInt64(r.GetValue(2)) == kb && !r.IsDBNull(3) && r.GetInt32(3) == 0;
                        }
                    if (!topOk || StatesText(got) != expect)
                        throw new Exception(label + ": nach dem Freigeben nicht wie geplant (States " + StatesText(got) + ", erwartet " + expect + ") - zurueckgerollt, nichts freigegeben.");
                }
                tx.Commit();
            }
        }
        string bn = Path.GetFileName(backup);
        var names = new List<string>();
        int follow = 0;
        foreach (JObject it in items)
        {
            int rv = it["releasedVersion"] != null ? (int)it["releasedVersion"] : 0;
            if (rv > 0) follow++;
            names.Add(it["name"] + " " + it["scriptId"] + " v" + it["version"] + (rv > 0 ? " (v" + rv + " abgeloest)" : ""));
        }
        _log("acmp_cc_release von " + requester + ": " + items.Count + " Command(s) freigegeben" + (follow > 0 ? " (davon " + follow + " Folgeversion(en), alte Version State 8)" : "") +
             ", Sicherung " + bn + ": " + string.Join("; ", names.ToArray()));
        var o = new JObject();
        o["sicherung"] = bn;
        o["ok"] = true; o["anzahl"] = items.Count;
        if (follow > 0) o["folgeversionen"] = follow;
        o["hinweis"] = "Freigegeben (State 7)" + (follow > 0 ? "; bei Folgeversionen ist die bisher freigegebene Version abgeloest (State 8)" : "") +
                       ". In der Console Aktualisieren (F5). Jetzt per Kiosk/Job ausfuehrbar; MCP-Commands danach in ihren Fachordner verschieben (acmp_cc_move). " +
                       "Zuruecknehmen kann das Tool die Freigabe nicht - die alten Werte stehen in der Sicherung.";
        o["freigegeben"] = ShortList(names, " (vollstaendige Liste in der Sicherung " + bn + ")");
        return o;
    }
}
