// AcmpUnix: Unix Scripts (ACMP 6.10, Desktop Automation -> Unix Scripts) lesen und schreiben.
//   acmp_unix_scripts (lesen: Skripte, Versionen, Ring, Inhalt), acmp_unix_results (lesen: Job-Ergebnisse je Client mit StdOut/StdErr),
//   acmp_unix_script_create, acmp_unix_script_new_version, acmp_unix_script_set_ring (schreiben, immer ueber die Vier-Augen-Freigabe).
// Grundlage: Beobachtung der Console 6.10.4 am 22.10.2026 (Rechte: grant-unix-writer.sql):
//  - Skript   = SYS_UnixClient_Scripts (Id, Name, FolderId, Description, Interpreter 0 Bash / 1 Zsh, StoreLog), Rechte wie der Ordner
//               (SYS_Folders FolderType 68, SYS_Folders_AclEntries_* -> SYS_AccessControl_AclEntries_*, ObjectID = Skript-Id).
//  - Version  = SYS_UnixClient_Scripts_Versions (Id, ScriptId, VersionNo, Content mit CRLF) + SYS_TestRings (Id = Versions-Id, ItemKind 68,
//               TestRing 1, TransitionMode 1, ItemState 0, CollectionName "Default", LastChangeDate = OLE-Datum Ortszeit). „Version hinzufuegen"
//               in der Console legt die neue Version in Testring 1 an.
//  - Freigabe = „Verteilungsring aendern" -> Freigegeben: nur TestRing 1 -> 255 und LastChangeDate der Version, sonst nichts (auch nicht an
//               aelteren Versionen). Ringe der Console: Kein Ring, Testring 1, Testring 2, Freigegeben (255). „Kein Ring" ist nicht beobachtet
//               und wird nicht geschrieben.
//  - Job starten: nur in der Console (Container oder Abfrage -> „Unix-Jobs ausfuehren"). Der ACMP-Server legt dafuer kurz eine SYS_Jobs-Zeile
//               an (JobKind 68, TUnixScriptJobAction) und loescht sie nach dem Lauf; die Job Collection kennt keine Unix Scripts, die Public API
//               hat keinen Befehl dafuer. Ergebnisse: SYS_Jobs_Logs(_Items) + CLT_UnixClient_Scripts_Logs (StdOut/StdErr).
//  - Keine Trigger auf den drei Tabellen; Fremdschluessel loeschen kaskadierend (Ordner -> Skripte -> Versionen, Ring -> DeclineInfo).
//    Standardwerte der Tabellen weichen von dem ab, was die Console schreibt (TransitionMode 0, TestRing 255, ItemKind -1) - das Tool
//    setzt deshalb jede Spalte ausdruecklich.
// Regeln: nur ueber die Vier-Augen-Freigabe (auch bei bypassAll, AlwaysGatedTools); Plan mit Inhalt (SHA-256) und Stand eingefroren - hat
// sich bis zur Zustimmung etwas geaendert, wird nichts geschrieben; eine Transaktion je Auftrag; Protokoll/Sicherung in unix-backups\.
// Der MCP aendert keine vorhandenen Versionen (Inhalt) und loescht nichts - Aendern = neue Version, Zurueck = Ring wieder setzen.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

class AcmpUnix
{
    public const int ItemKind = 68;           // SYS_TestRings.ItemKind der Unix-Script-Versionen = SYS_Folders.FolderType der Unix-Script-Ordner
    public const int RingReleased = 255;
    public static readonly int[] WritableRings = { 1, 2, RingReleased };
    public const int MaxNameLength = 100;      // SYS_UnixClient_Scripts.Name nvarchar(100)
    public const int MaxContentChars = 500000;
    public const int MaxDescriptionChars = 4000;
    public static readonly string[] TestedVersions = { "6.10" };
    public static readonly string[] KnownTriggers = { };   // keine Trigger auf den drei Tabellen (grant-unix-writer.sql, 22.10.2026)

    // Erwartete Spalten (Reihenfolge wie in der Datenbank, 22.10.2026)
    public const string ColsScripts = "Id:uniqueidentifier,Name:nvarchar,FolderId:uniqueidentifier,Description:nvarchar,Interpreter:tinyint,StoreLog:tinyint";
    public const string ColsVersions = "Id:uniqueidentifier,ScriptId:uniqueidentifier,VersionNo:int,Content:nvarchar";
    public const string ColsRings = "Id:uniqueidentifier,TestRing:int,ItemKind:int,LastChangeDate:float,TransitionMode:tinyint,ItemState:tinyint,CollectionName:nvarchar";

    readonly string _readCs, _writeCs, _backupDir;
    readonly HashSet<string> _allowedReq = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _allowedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    readonly Action<string> _log;
    readonly Func<string> _acmpVersion;
    readonly bool _allowUntested;
    JObject _preflight; DateTime _preflightAt;
    readonly object _pfLock = new object();
    static readonly object _wLock = new object();
    const int PreflightMaxAgeMinutes = 60;

    public bool ConfigOk { get; private set; }
    public string DisabledReason { get; private set; }
    public bool ApprovalGate { get; private set; }

    public static bool IsWriteTool(string name)
    {
        return name == "acmp_unix_script_create" || name == "acmp_unix_script_new_version" || name == "acmp_unix_script_set_ring";
    }

    public AcmpUnix(string readCs, JObject cfg, JObject ccCfg, string cfgDir, Action<string> log, Func<string> acmpVersion)
    {
        _readCs = readCs; _log = log; _acmpVersion = acmpVersion;
        string bd = cfg != null && cfg["backupDir"] != null ? (string)cfg["backupDir"] : "unix-backups";
        _backupDir = Path.IsPathRooted(bd) ? bd : Path.Combine(cfgDir, bd);
        JToken gate = cfg != null && cfg["approvalGate"] != null ? cfg["approvalGate"] : (ccCfg != null ? ccCfg["approvalGate"] : null);
        ApprovalGate = gate == null || (bool)gate;
        _allowUntested = cfg != null && cfg["allowUntested"] != null && (bool)cfg["allowUntested"];
        JArray af = cfg != null ? cfg["allowedFolderIds"] as JArray : null;
        if (af != null) foreach (JToken t in af) { string g = GuidBraces((string)t); if (g != null) _allowedFolders.Add(g); }
        if (cfg == null || cfg["enabled"] == null || !(bool)cfg["enabled"]) { DisabledReason = "unixWriter.enabled ist nicht true (nur Lesen und dryRun)"; return; }
        _writeCs = cfg["connectionString"] != null ? (string)cfg["connectionString"] : (ccCfg != null ? (string)ccCfg["connectionString"] : null);
        JArray ar = (cfg["allowedRequesters"] as JArray) ?? (ccCfg != null ? ccCfg["allowedRequesters"] as JArray : null);
        if (ar != null) foreach (JToken t in ar) _allowedReq.Add((string)t);
        if (string.IsNullOrEmpty(_readCs)) { DisabledReason = "db.connectionString fehlt"; return; }
        if (string.IsNullOrEmpty(_writeCs) || _writeCs.StartsWith("HIER_")) { DisabledReason = "kein Schreib-Login (unixWriter.connectionString oder ccWriter.connectionString)"; return; }
        if (_allowedReq.Count == 0) { DisabledReason = "keine erlaubten Antragsteller (unixWriter.allowedRequesters oder ccWriter.allowedRequesters)"; return; }
        ConfigOk = true;
    }

    // ================= Hilfen ohne DB (offline getestet in UnixTest.cs) =================

    public static string GuidBraces(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        Guid g; if (!Guid.TryParse(s.Trim().Trim('{', '}'), out g)) return null;
        return "{" + g.ToString().ToUpperInvariant() + "}";
    }

    static Guid G(string s) { return new Guid(s.Trim('{', '}')); }
    static bool IsStr(JToken t) { return t != null && t.Type == JTokenType.String && ((string)t).Trim().Length > 0; }
    static bool IsText(string s) { return s != null && s.Trim().Length > 0; }
    static bool Flag(JObject a, string k) { return a[k] != null && a[k].Type == JTokenType.Boolean && (bool)a[k]; }

    // Inhalt so, wie die Console ihn speichert: Zeilenenden CRLF (der Agent kommt damit zurecht, 21./22.10.2026 belegt), ohne BOM.
    public static string NormalizeContent(string s)
    {
        if (s == null || s.Trim().Length == 0) throw new Exception("content fehlt oder ist leer.");
        if (s.Length > 0 && s[0] == '﻿') s = s.Substring(1);
        if (s.IndexOf('\0') >= 0) throw new Exception("content enthaelt ein Nullzeichen - kein Text.");
        string t = s.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
        if (t.Length > MaxContentChars) throw new Exception("content ist zu lang (" + t.Length + " Zeichen, hoechstens " + MaxContentChars + ").");
        return t;
    }

    // Zum Lesen: LF statt CRLF
    public static string DisplayContent(string s) { return s == null ? null : s.Replace("\r\n", "\n"); }

    public static string Sha256(string s)
    {
        using (var h = SHA256.Create())
        {
            byte[] b = h.ComputeHash(new UTF8Encoding(false).GetBytes(s ?? ""));
            var sb = new StringBuilder(); foreach (byte x in b) sb.Append(x.ToString("X2")); return sb.ToString();
        }
    }

    // Hinweise zum Inhalt (Vorschau): Shebang nicht in Zeile 1, fuehrende Leerzeichen, Interpreter.
    // Shebang = erste Zeile mit "#!" vor dem ersten Befehl (davor nur leere Zeilen oder Kommentare; ein "#!" weiter unten, z.B. in einem
    // Here-Document, zaehlt nicht). Beispiel (22.10.2026): Zeile 1 " # Kommentar", Zeile 2 mit 24 Leerzeichen vor "#!/bin/bash".
    public static List<string> ContentNotes(string content, int interpreter)
    {
        var n = new List<string>();
        string[] lines = (content ?? "").Replace("\r\n", "\n").Split('\n');
        int at = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            string t = lines[i].TrimStart();
            if (t.StartsWith("#!")) { at = i; break; }
            if (t.Length > 0 && !t.StartsWith("#")) break;
        }
        if (at < 0) return n;
        string line = lines[at], shebang = line.TrimStart();
        int lead = line.Length - shebang.Length;
        if (at > 0 || lead > 0)
            n.Add((at > 0 ? "Die Shebang-Zeile steht erst in Zeile " + (at + 1) + (lead > 0 ? " und hat " + lead + " Leerzeichen vor \"#!\"" : "") : "Die erste Zeile hat " + lead + " Leerzeichen vor \"#!\"") +
                  " - sie wirkt nur ganz vorne in Zeile 1 (der Agent startet den Interpreter trotzdem ausdruecklich).");
        bool zsh = shebang.IndexOf("zsh", StringComparison.OrdinalIgnoreCase) >= 0, bash = shebang.IndexOf("bash", StringComparison.OrdinalIgnoreCase) >= 0;
        if (interpreter == 0 && zsh) n.Add("Shebang nennt zsh, Interpreter ist Bash.");
        if (interpreter == 1 && bash) n.Add("Shebang nennt bash, Interpreter ist Zsh.");
        return n;
    }

    public static int ParseInterpreter(JToken t)
    {
        if (t == null || t.Type == JTokenType.Null) return 0;
        string s = ((string)t).Trim().ToLowerInvariant();
        if (s == "bash" || s == "0" || s.Length == 0) return 0;
        if (s == "zsh" || s == "1") return 1;
        throw new Exception("interpreter '" + (string)t + "' unbekannt - bash oder zsh.");
    }

    public static string InterpreterText(int i) { return i == 0 ? "Bash" : i == 1 ? "Zsh" : "Interpreter " + i; }

    // StoreLog: 2 = immer („Log immer speichern"), 1 = Standard der Tabelle und der Aagon-Beispiele (vermutlich nur bei Fehler,
    // nicht in der Console belegt), 0 = nie (API slNever)
    public static int ParseStoreLog(JToken t)
    {
        if (t == null || t.Type == JTokenType.Null) return 2;
        string s = ((string)t).Trim().ToLowerInvariant();
        if (s == "always" || s == "immer" || s == "2") return 2;
        if (s == "error" || s == "onerror" || s == "fehler" || s == "beifehler" || s == "1") return 1;
        if (s == "never" || s == "nie" || s == "0") return 0;
        throw new Exception("storeLog '" + (string)t + "' unbekannt - always (immer), error (vermutlich nur bei Fehler) oder never (nie).");
    }

    public static string StoreLogText(int v) { return v == 2 ? "immer" : v == 1 ? "1 (Standard, vermutlich nur bei Fehler)" : v == 0 ? "nie" : "Wert " + v; }

    public static int ParseRing(JToken t)
    {
        if (t == null || t.Type == JTokenType.Null) throw new Exception("ring fehlt: 1 (Testring 1), 2 (Testring 2) oder 255 / freigegeben.");
        string s = ((string)t).Trim().ToLowerInvariant().Replace(" ", "");
        int r;
        if (s == "freigegeben" || s == "released" || s == "freigabering") r = RingReleased;
        else if (s.StartsWith("testring") && int.TryParse(s.Substring(8), out r)) { }
        else if (!int.TryParse(s, out r)) throw new Exception("ring '" + (string)t + "' unbekannt - 1, 2 oder 255 (freigegeben).");
        if (Array.IndexOf(WritableRings, r) < 0) throw new Exception("ring " + r + " ist nicht erlaubt - 1 (Testring 1), 2 (Testring 2) oder 255 (Freigegeben). „Kein Ring\" setzt der MCP nicht (nicht beobachtet).");
        return r;
    }

    public static string RingText(int r) { return r == RingReleased ? "Freigegeben (255)" : r == 0 ? "Kein Ring (0)" : "Testring " + r; }

    public static string OleText(double d)
    {
        if (d <= 0) return "-";
        try { return DateTime.FromOADate(d).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture); }
        catch { return d.ToString(CultureInfo.InvariantCulture); }
    }

    public static string JobState(int s) { return s == 0 ? "Wartend" : s == 1 ? "Zugewiesen" : s == 2 ? "Laeuft" : s == 3 ? "Abgeschlossen" : s == 4 ? "Abgebrochen" : s == 5 ? "Timeout/Fehler" : "Status " + s; }
    public static string JobResult(int r) { return r == 0 ? "Offen" : r == 1 ? "Erfolg" : r == 2 ? "Warnung/Fehlgeschlagen" : r == 3 ? "Fehler" : "Ergebnis " + r; }

    // Text fuer einen Lauf ohne Ausgabe in CLT_UnixClient_Scripts_Logs. storeLog = Log speichern des Skripts (-1 = Skript nicht mehr da);
    // der Console-Push nimmt die Einstellung des Skripts (JobAction StoreLog jslDefinedByScript, 22.10.2026).
    // Belegt 22.10.2026: ohne Unix-Script-Lizenz liefert ACMP das Skript nicht aus (GetUnixScriptForExecution ResultCode 32 „Maximum number of
    // licensed elements reached"), der Agent meldet „Empty script content received" und den Job trotzdem als Erfolg.
    public static string NoOutputText(int state, int result, int storeLog)
    {
        if (state < 3) return "noch keine (Job laeuft noch)";
        if (storeLog == 2 && result == 1) return "keine, obwohl Log speichern = immer: Skript lief vermutlich nicht - Unix-Script-Lizenz erschoepft? (Agent-Log /var/log/aagon/ACMPUnixAgent/*.sil: ResultCode 32, „Empty script content received\")";
        if (storeLog == 2) return "keine, obwohl Log speichern = immer (Agent hat nichts gemeldet - Agent-Log /var/log/aagon/ACMPUnixAgent/*.sil pruefen)";
        if (storeLog == 1) return "keine gespeichert (Log speichern = " + StoreLogText(1) + ", oder Agent hat nichts gemeldet)";
        if (storeLog == 0) return "keine gespeichert (Log speichern = nie)";
        return "keine gespeichert (Log-Einstellung unbekannt - Skript geloescht?; oder Agent hat nichts gemeldet)";
    }

    // Zeilenvergleich fuer die Vorschau einer neuen Version: entfernte (-) und neue (+) Zeilen, Reihenfolge wie im Text (einfacher LCS);
    // je geaenderter Stelle erst die entfernten, dann die neuen Zeilen (bei Gleichstand wird zuerst entfernt)
    public static List<string> LineDiff(string oldText, string newText, int max)
    {
        string[] a = (oldText ?? "").Split(new[] { "\r\n" }, StringSplitOptions.None), b = (newText ?? "").Split(new[] { "\r\n" }, StringSplitOptions.None);
        var res = new List<string>();
        if (a.Length * (long)b.Length > 4000000L) { res.Add("(zu gross fuer einen Zeilenvergleich: " + a.Length + " -> " + b.Length + " Zeilen)"); return res; }
        var L = new int[a.Length + 1, b.Length + 1];
        for (int i = a.Length - 1; i >= 0; i--)
            for (int j = b.Length - 1; j >= 0; j--)
                L[i, j] = a[i] == b[j] ? L[i + 1, j + 1] + 1 : Math.Max(L[i + 1, j], L[i, j + 1]);
        int x = 0, y = 0, total = 0;
        while (x < a.Length || y < b.Length)
        {
            if (x < a.Length && y < b.Length && a[x] == b[y]) { x++; y++; continue; }
            if (x < a.Length && (y >= b.Length || L[x + 1, y] >= L[x, y + 1])) { total++; if (res.Count < max) res.Add("- " + (x + 1) + ": " + Short(a[x], 160)); x++; }
            else { total++; if (res.Count < max) res.Add("+ " + (y + 1) + ": " + Short(b[y], 160)); y++; }
        }
        if (total > res.Count) res.Add("... insgesamt " + total + " geaenderte Zeilen");
        return res;
    }

    public static string Short(string s, int max)
    {
        string t = (s ?? "").Replace("\t", "    ");
        return t.Length > max ? t.Substring(0, max - 3) + "..." : t;
    }

    static string FirstLine(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        int i = s.IndexOfAny(new[] { '\r', '\n' });
        return Short(i >= 0 ? s.Substring(0, i) : s, 200);
    }

    // ================= Vorpruefung =================
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
            _log("Unix-Script-Writer Vorpruefung (ACMP " + (v ?? "?") + "): " + (string)_preflight["summary"]);
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

    static bool HasPerm(SqlConnection c, string obj, string perm, string column)
    {
        var q = c.CreateCommand();
        q.CommandText = column == null ? "SELECT HAS_PERMS_BY_NAME(@o, 'OBJECT', @p)" : "SELECT HAS_PERMS_BY_NAME(@o, 'OBJECT', @p, @c, 'COLUMN')";
        q.Parameters.AddWithValue("@o", obj); q.Parameters.AddWithValue("@p", perm);
        if (column != null) q.Parameters.AddWithValue("@c", column);
        object o = q.ExecuteScalar();
        return o != null && !(o is DBNull) && Convert.ToInt32(o) == 1;
    }

    static string Cols(SqlConnection c, string table)
    {
        return Scalar(c, "SELECT STUFF((SELECT ',' + COLUMN_NAME + ':' + DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='" + table + "' ORDER BY ORDINAL_POSITION FOR XML PATH('')), 1, 1, '')");
    }

    JObject RunPreflight(string v)
    {
        var checks = new JArray();
        bool createOk = false, versionOk = false, ringOk = false;
        try
        {
            string mm = null;
            if (!string.IsNullOrEmpty(v)) { string[] p = v.Split('.'); if (p.Length >= 2) mm = p[0] + "." + p[1]; }
            if (mm != null && Array.IndexOf(TestedVersions, mm) >= 0) Add(checks, "ACMP-Version", "ok", v);
            else Add(checks, "ACMP-Version", _allowUntested ? "warn" : "block", (mm == null ? "nicht ermittelbar" : v + " ist nicht getestet") + " (getestet: " + string.Join(", ", TestedVersions) + ")" + (_allowUntested ? " - per unixWriter.allowUntested freigegeben" : ""));
            using (var rc = new SqlConnection(_readCs))
            using (var wc = new SqlConnection(_writeCs))
            {
                rc.Open();
                try { wc.Open(); }
                catch (Exception ex) { Add(checks, "Schreib-Login", "block", "Anmeldung fehlgeschlagen: " + ex.Message); return Result(checks, false, false, false); }
                string a = Scalar(rc, "SELECT @@SERVERNAME + '/' + DB_NAME()"), b = Scalar(wc, "SELECT @@SERVERNAME + '/' + DB_NAME()");
                bool same = string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
                Add(checks, "Datenbank", same ? "ok" : "block", a + (same ? " (Lese- und Schreib-Login gleich)" : " / Schreib-Login auf " + b));
                foreach (string[] t in new[] { new[] { "SYS_UnixClient_Scripts", ColsScripts }, new[] { "SYS_UnixClient_Scripts_Versions", ColsVersions }, new[] { "SYS_TestRings", ColsRings } })
                {
                    string cols = Cols(rc, t[0]);
                    Add(checks, "Tabelle " + t[0], cols == t[1] ? "ok" : "block", cols == t[1] ? "Spalten wie erwartet" : "Spalten: " + (cols ?? "keine") + " (erwartet " + t[1] + ")");
                }
                var trg = new List<string>();
                var tq = wc.CreateCommand();
                tq.CommandText = "SELECT OBJECT_NAME(t.parent_id) + '.' + t.name FROM sys.triggers t WHERE t.parent_id IN (OBJECT_ID('dbo.SYS_UnixClient_Scripts'), OBJECT_ID('dbo.SYS_UnixClient_Scripts_Versions'), OBJECT_ID('dbo.SYS_TestRings'))";
                using (var r = tq.ExecuteReader()) while (r.Read()) trg.Add(r.GetString(0));
                var unknown = new List<string>();
                foreach (string t in trg) if (Array.IndexOf(KnownTriggers, t) < 0) unknown.Add(t);
                Add(checks, "Trigger", unknown.Count == 0 ? "ok" : (_allowUntested ? "warn" : "block"),
                    unknown.Count == 0 ? "keine Trigger auf den Unix-Script-Tabellen und SYS_TestRings (wie am 22.10.2026)" : "unbekannte Trigger: " + string.Join(", ", unknown.ToArray()) + " - erst pruefen");
                // Jede vorhandene Version hat eine Ring-Zeile mit ItemKind 68 (sonst stimmt das Bild vom Datenmodell nicht)
                string missing = Scalar(rc, "SELECT CAST(COUNT(*) AS varchar(10)) FROM SYS_UnixClient_Scripts_Versions v LEFT JOIN SYS_TestRings r ON r.Id = v.Id AND r.ItemKind = " + ItemKind + " WHERE r.Id IS NULL");
                string folders = Scalar(rc, "SELECT CAST(COUNT(*) AS varchar(10)) FROM SYS_Folders WHERE FolderType = " + ItemKind);
                Add(checks, "Datenmodell", missing == "0" ? "ok" : "block", missing == "0" ? "alle Versionen mit Ring-Zeile (ItemKind " + ItemKind + "), " + folders + " Unix-Script-Ordner" : missing + " Version(en) ohne Ring-Zeile mit ItemKind " + ItemKind + " - Datenmodell weicht ab");
                bool sSel = HasPerm(wc, "dbo.SYS_UnixClient_Scripts", "SELECT", null), sIns = HasPerm(wc, "dbo.SYS_UnixClient_Scripts", "INSERT", null);
                bool vSel = HasPerm(wc, "dbo.SYS_UnixClient_Scripts_Versions", "SELECT", null), vIns = HasPerm(wc, "dbo.SYS_UnixClient_Scripts_Versions", "INSERT", null);
                bool rSel = HasPerm(wc, "dbo.SYS_TestRings", "SELECT", null), rIns = HasPerm(wc, "dbo.SYS_TestRings", "INSERT", null);
                bool rUpd = HasPerm(wc, "dbo.SYS_TestRings", "UPDATE", "TestRing") && HasPerm(wc, "dbo.SYS_TestRings", "UPDATE", "LastChangeDate");
                bool aclU = HasPerm(wc, "dbo.SYS_AccessControl_AclEntries_Users", "INSERT", null), aclG = HasPerm(wc, "dbo.SYS_AccessControl_AclEntries_Groups", "INSERT", null);
                bool fU = HasPerm(wc, "dbo.SYS_Folders_AclEntries_Users", "SELECT", null), fG = HasPerm(wc, "dbo.SYS_Folders_AclEntries_Groups", "SELECT", null);
                versionOk = sSel && vSel && vIns && rSel && rIns;
                createOk = versionOk && sIns && aclU && aclG && fU && fG;
                ringOk = vSel && rSel && rUpd;
                var miss = new List<string>();
                if (!sSel) miss.Add("SELECT Scripts"); if (!sIns) miss.Add("INSERT Scripts"); if (!vSel) miss.Add("SELECT Versions"); if (!vIns) miss.Add("INSERT Versions");
                if (!rSel) miss.Add("SELECT TestRings"); if (!rIns) miss.Add("INSERT TestRings"); if (!rUpd) miss.Add("UPDATE TestRings(TestRing, LastChangeDate)");
                if (!aclU || !aclG) miss.Add("INSERT SYS_AccessControl_AclEntries_*"); if (!fU || !fG) miss.Add("SELECT SYS_Folders_AclEntries_*");
                Add(checks, "Rechte", miss.Count == 0 ? "ok" : "warn", miss.Count == 0 ? "alle Rechte des Schreib-Logins vorhanden (grant-unix-writer.sql)" : "fehlen: " + string.Join(", ", miss.ToArray()) + " - betroffene Tools gesperrt (grant-unix-writer.sql)");
                var extra = new List<string>();
                if (HasPerm(wc, "dbo.SYS_UnixClient_Scripts_Versions", "UPDATE", null)) extra.Add("UPDATE auf SYS_UnixClient_Scripts_Versions");
                if (HasPerm(wc, "dbo.SYS_TestRings", "DELETE", null)) extra.Add("DELETE auf SYS_TestRings");
                if (HasPerm(wc, "dbo.SYS_TestRings", "UPDATE", "ItemKind")) extra.Add("UPDATE auf SYS_TestRings.ItemKind");
                if (Scalar(wc, "SELECT CAST(IS_ROLEMEMBER('db_owner') AS varchar(5))") == "1") extra.Add("Mitglied von db_owner");
                if (extra.Count > 0) Add(checks, "Rechte (zu viele)", "warn", string.Join(", ", extra.ToArray()));
            }
        }
        catch (Exception ex) { Add(checks, "Pruefung", "block", "Abgebrochen: " + ex.Message); }
        return Result(checks, createOk, versionOk, ringOk);
    }

    static JObject Result(JArray checks, bool createOk, bool versionOk, bool ringOk)
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
        o["createTool"] = ok && createOk; o["versionTool"] = ok && versionOk; o["ringTool"] = ok && ringOk;
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
        o["approvalGate"] = ApprovalGate;
        o["allowedRequesters"] = new JArray(new List<string>(_allowedReq).ToArray());
        if (_allowedFolders.Count > 0) o["allowedFolderIds"] = new JArray(new List<string>(_allowedFolders).ToArray());
        o["createTool"] = pf["createTool"]; o["versionTool"] = pf["versionTool"]; o["ringTool"] = pf["ringTool"];
        o["jobStart"] = "nicht moeglich: Unix-Jobs startet nur die Console (Container oder Abfrage -> Unix-Jobs ausfuehren); Ergebnis danach mit acmp_unix_results";
        o["vorpruefung"] = pf;
        return o;
    }

    public void CheckWrite(string requester, string tool)
    {
        if (!ConfigOk) throw new Exception("Unix-Script-Schreiben ist ausgeschaltet: " + DisabledReason);
        if (!ApprovalGate) throw new Exception(tool + " laeuft nur ueber die Vier-Augen-Freigabe (unixWriter.approvalGate bzw. ccWriter.approvalGate ist aus) - nichts geschrieben.");
        if (requester == null || !_allowedReq.Contains(requester)) throw new Exception("Antragsteller '" + requester + "' darf keine Unix Scripts schreiben (unixWriter.allowedRequesters bzw. ccWriter.allowedRequesters).");
        JObject pf = Preflight(false);
        if (pf == null || !(bool)pf["ok"]) throw new Exception("Unix-Script-Schreiben gesperrt - Vorpruefung nicht bestanden: " + (pf != null ? (string)pf["summary"] : "keine Pruefung") + " (Details: acmp_unix_scripts mit status=true). Nichts geschrieben.");
        string key = tool == "acmp_unix_script_create" ? "createTool" : tool == "acmp_unix_script_new_version" ? "versionTool" : "ringTool";
        if (pf[key] == null || !(bool)pf[key]) throw new Exception(tool + " gesperrt - Rechte des Schreib-Logins fehlen (Details: acmp_unix_scripts mit status=true, grant-unix-writer.sql). Nichts geschrieben.");
    }

    // ================= Lesen =================

    class ScriptInfo
    {
        public string Id, Name, FolderId, Description; public int Interpreter, StoreLog;
        public List<VersionInfo> Versions = new List<VersionInfo>();
    }
    class VersionInfo
    {
        public string Id; public int No; public int Ring = -1; public double Changed; public int TransitionMode, ItemState; public string Collection, Declined; public int Chars;
    }

    // Unix-Script-Ordner: Id -> (Name, ParentId)
    static Dictionary<string, string[]> LoadFolders(SqlConnection c)
    {
        var d = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var q = c.CreateCommand();
        q.CommandText = "SELECT CAST(FolderID AS varchar(36)), Caption, CAST(ParentID AS varchar(36)) FROM SYS_Folders WHERE FolderType = " + ItemKind;
        using (var r = q.ExecuteReader())
            while (r.Read()) d["{" + r.GetString(0).ToUpperInvariant() + "}"] = new[] { r.GetString(1), r.IsDBNull(2) ? null : "{" + r.GetString(2).ToUpperInvariant() + "}" };
        return d;
    }

    static string FolderPath(Dictionary<string, string[]> f, string id)
    {
        var parts = new List<string>(); int guard = 0;
        while (id != null && f.ContainsKey(id) && guard++ < 20) { parts.Insert(0, f[id][0]); id = f[id][1]; }
        return parts.Count > 0 ? string.Join("\\", parts.ToArray()) : "(Ordner " + id + ")";
    }

    // Ordner aus GUID, Pfad ("Examples\Linux") oder eindeutigem Namen
    static string ResolveFolder(Dictionary<string, string[]> f, string spec)
    {
        if (!IsText(spec)) throw new Exception("folder fehlt: Unix-Script-Ordner als GUID, Pfad (z.B. \"Examples\\Linux\") oder eindeutiger Name.");
        string g = GuidBraces(spec);
        if (g != null) { if (!f.ContainsKey(g)) throw new Exception("Unix-Script-Ordner " + g + " gibt es nicht (SYS_Folders, FolderType " + ItemKind + ")."); return g; }
        var hits = new List<string>(); string s = spec.Trim().Trim('\\');
        foreach (var kv in f) if (string.Equals(FolderPath(f, kv.Key), s, StringComparison.OrdinalIgnoreCase)) hits.Add(kv.Key);
        if (hits.Count == 0) foreach (var kv in f) if (string.Equals(kv.Value[0], s, StringComparison.OrdinalIgnoreCase)) hits.Add(kv.Key);
        if (hits.Count == 1) return hits[0];
        var all = new List<string>(); foreach (var kv in f) all.Add(FolderPath(f, kv.Key));
        all.Sort(StringComparer.OrdinalIgnoreCase);
        throw new Exception(hits.Count == 0 ? "Unix-Script-Ordner '" + spec + "' nicht gefunden. Vorhanden: " + string.Join(", ", all.ToArray())
                                            : "Ordnername '" + spec + "' ist nicht eindeutig - Pfad oder GUID angeben: " + string.Join(", ", all.ToArray()));
    }

    static List<ScriptInfo> LoadScripts(SqlConnection c, string onlyId)
    {
        var q = c.CreateCommand();
        q.CommandText =
            "SELECT CAST(s.Id AS varchar(36)), s.Name, CAST(s.FolderId AS varchar(36)), s.Description, s.Interpreter, s.StoreLog, " +
            "CAST(v.Id AS varchar(36)), v.VersionNo, r.TestRing, r.LastChangeDate, r.TransitionMode, r.ItemState, r.CollectionName, d.Comment, LEN(v.Content) " +
            "FROM SYS_UnixClient_Scripts s LEFT JOIN SYS_UnixClient_Scripts_Versions v ON v.ScriptId = s.Id " +
            "LEFT JOIN SYS_TestRings r ON r.Id = v.Id AND r.ItemKind = " + ItemKind + " LEFT JOIN SYS_TestRings_DeclineInfo d ON d.Id = v.Id " +
            (onlyId != null ? "WHERE s.Id = @id " : "") + "ORDER BY s.Name, s.Id, v.VersionNo";
        if (onlyId != null) q.Parameters.AddWithValue("@id", G(onlyId));
        var list = new List<ScriptInfo>(); ScriptInfo cur = null;
        using (var r = q.ExecuteReader())
            while (r.Read())
            {
                string id = "{" + r.GetString(0).ToUpperInvariant() + "}";
                if (cur == null || cur.Id != id)
                {
                    cur = new ScriptInfo(); cur.Id = id; cur.Name = r.GetString(1); cur.FolderId = "{" + r.GetString(2).ToUpperInvariant() + "}";
                    cur.Description = r.IsDBNull(3) ? "" : r.GetString(3); cur.Interpreter = r.GetByte(4); cur.StoreLog = r.GetByte(5);
                    list.Add(cur);
                }
                if (r.IsDBNull(6)) continue;
                var v = new VersionInfo(); v.Id = "{" + r.GetString(6).ToUpperInvariant() + "}"; v.No = r.GetInt32(7);
                if (!r.IsDBNull(8)) { v.Ring = r.GetInt32(8); v.Changed = r.GetDouble(9); v.TransitionMode = r.GetByte(10); v.ItemState = r.GetByte(11); v.Collection = r.IsDBNull(12) ? null : r.GetString(12); }
                v.Declined = r.IsDBNull(13) ? null : r.GetString(13);
                v.Chars = r.IsDBNull(14) ? 0 : Convert.ToInt32(r.GetValue(14));
                cur.Versions.Add(v);
            }
        return list;
    }

    // Skript aus GUID oder Name (eindeutig, sonst Liste der Treffer)
    static ScriptInfo FindScript(SqlConnection c, string spec)
    {
        if (!IsText(spec)) throw new Exception("script fehlt: Name oder Id (GUID) des Unix Scripts (acmp_unix_scripts listet sie).");
        string g = GuidBraces(spec);
        List<ScriptInfo> all = LoadScripts(c, g);
        if (g != null) { if (all.Count == 0) throw new Exception("Unix Script " + g + " gibt es nicht."); return all[0]; }
        var hits = all.FindAll(s => string.Equals(s.Name, spec.Trim(), StringComparison.OrdinalIgnoreCase));
        if (hits.Count == 1) return hits[0];
        if (hits.Count > 1) { var ids = new List<string>(); foreach (var h in hits) ids.Add(h.Id); throw new Exception("Name '" + spec + "' ist mehrdeutig - Id angeben: " + string.Join(", ", ids.ToArray())); }
        throw new Exception("Unix Script '" + spec + "' nicht gefunden (acmp_unix_scripts listet alle).");
    }

    static VersionInfo Newest(ScriptInfo s) { return s.Versions.Count > 0 ? s.Versions[s.Versions.Count - 1] : null; }

    static VersionInfo NewestReleased(ScriptInfo s)
    {
        for (int i = s.Versions.Count - 1; i >= 0; i--) if (s.Versions[i].Ring == RingReleased) return s.Versions[i];
        return null;
    }

    static string ReadContent(SqlConnection c, string versionId)
    {
        var q = c.CreateCommand(); q.CommandText = "SELECT Content FROM SYS_UnixClient_Scripts_Versions WHERE Id = @v";
        q.Parameters.AddWithValue("@v", G(versionId));
        object o = q.ExecuteScalar();
        return o == null || o is DBNull ? null : (string)o;
    }

    static JObject VersionJson(VersionInfo v)
    {
        var o = new JObject();
        o["version"] = v.No; o["id"] = v.Id;
        o["ring"] = v.Ring >= 0 ? (JToken)RingText(v.Ring) : "keine Ring-Zeile";
        if (v.Changed > 0) o["ringGeaendert"] = OleText(v.Changed);
        if (v.Collection != null && v.Collection != "Default") o["testprozess"] = v.Collection;
        if (v.ItemState != 0) o["itemState"] = v.ItemState;
        if (v.Declined != null) o["verweigert"] = v.Declined;
        o["zeichen"] = v.Chars;
        return o;
    }

    JObject ScriptJson(ScriptInfo s, Dictionary<string, string[]> f, bool versions)
    {
        var o = new JObject();
        o["name"] = s.Name; o["id"] = s.Id; o["ordner"] = FolderPath(f, s.FolderId);
        o["interpreter"] = InterpreterText(s.Interpreter); o["logSpeichern"] = StoreLogText(s.StoreLog);
        if (s.Description.Length > 0) o["beschreibung"] = s.Description;
        VersionInfo rel = NewestReleased(s), nw = Newest(s);
        o["freigegeben"] = rel != null ? (JToken)("v" + rel.No) : "keine Version freigegeben";
        if (nw != null && (rel == null || nw.No != rel.No)) o["neueste"] = "v" + nw.No + " (" + (nw.Ring >= 0 ? RingText(nw.Ring) : "ohne Ring") + ")";
        if (versions) { var va = new JArray(); foreach (var v in s.Versions) va.Add(VersionJson(v)); o["versionen"] = va; }
        else o["versionen"] = s.Versions.Count;
        return o;
    }

    // acmp_unix_scripts: Liste (ohne script) oder ein Skript mit Versionen und Inhalt
    public JObject Scripts(JObject a)
    {
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            var f = LoadFolders(c);
            if (IsStr(a["script"]))
            {
                ScriptInfo s = FindScript(c, (string)a["script"]);
                JObject o = ScriptJson(s, f, true);
                VersionInfo v = null;
                if (a["version"] != null && a["version"].Type != JTokenType.Null)
                {
                    int no = (int)a["version"];
                    v = s.Versions.Find(x => x.No == no);
                    if (v == null) throw new Exception("Version " + no + " von '" + s.Name + "' gibt es nicht.");
                }
                else v = Newest(s);
                bool content = a["content"] == null || a["content"].Type != JTokenType.Boolean || (bool)a["content"];
                if (v != null && content)
                {
                    string raw = ReadContent(c, v.Id) ?? "";
                    o["inhaltVersion"] = v.No;
                    o["inhaltSha256"] = Sha256(raw);
                    var notes = ContentNotes(raw, s.Interpreter);
                    if (notes.Count > 0) o["inhaltHinweise"] = new JArray(notes.ToArray());
                    o["inhalt"] = DisplayContent(raw);
                }
                o["hinweis"] = "Auf Clients laeuft die neueste freigegebene Version (Ring 255). Gestartet wird in der Console (Container oder Abfrage -> Unix-Jobs ausfuehren); Ergebnis mit acmp_unix_results.";
                return o;
            }
            string folder = IsStr(a["folder"]) ? ResolveFolder(f, (string)a["folder"]) : null;
            string search = IsStr(a["search"]) ? ((string)a["search"]).Trim() : null;
            var arr = new JArray();
            foreach (var s in LoadScripts(c, null))
            {
                if (folder != null && s.FolderId != folder) continue;
                if (search != null && s.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                arr.Add(ScriptJson(s, f, false));
            }
            var res = new JObject();
            res["anzahl"] = arr.Count;
            res["skripte"] = arr;
            var fl = new List<string>(); foreach (var kv in f) fl.Add(FolderPath(f, kv.Key));
            fl.Sort(StringComparer.OrdinalIgnoreCase);
            res["ordner"] = new JArray(fl.ToArray());
            return res;
        }
    }

    // acmp_unix_results: Laeufe von Unix Scripts je Client (Job-Monitor) mit Ausgabe aus CLT_UnixClient_Scripts_Logs
    public JObject Results(JObject a)
    {
        int days = a["days"] != null && a["days"].Type == JTokenType.Integer ? Math.Max(1, Math.Min(90, (int)a["days"])) : 7;
        int top = a["top"] != null && a["top"].Type == JTokenType.Integer ? Math.Max(1, Math.Min(100, (int)a["top"])) : 20;
        int maxChars = a["maxChars"] != null && a["maxChars"].Type == JTokenType.Integer ? Math.Max(200, Math.Min(100000, (int)a["maxChars"])) : 20000;
        bool output = a["output"] == null || a["output"].Type != JTokenType.Boolean || (bool)a["output"];
        string client = IsStr(a["client"]) ? ((string)a["client"]).Trim() : null;
        string clientId = GuidBraces(client);
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            string scriptId = null, scriptName = null;
            if (IsStr(a["script"])) { ScriptInfo s = FindScript(c, (string)a["script"]); scriptId = s.Id; scriptName = s.Name; }
            var q = c.CreateCommand();
            q.CommandTimeout = 60;
            // Zuordnung Ausgabe <-> Job: JobExecId der Ausgabe = ExecId des Client-Eintrags (sonst Unter-Eintrag oder JobId); "zuordnung" zeigt, was passte
            q.CommandText =
                "SELECT TOP (@n) l.JobName, l.CreatorName, l.CreationDate, CAST(l.ContentId AS varchar(36)), l.TestRing, CAST(i.ExecId AS varchar(36)), " +
                "CAST(i.ClientId AS varchar(36)), i.ComputerName, i.State, i.Result, i.LastActivity, i.StateMsg, u.StdOut, u.StdErr, " +
                "CASE WHEN u.JobExecId IS NULL THEN NULL WHEN u.JobExecId = i.ExecId THEN 'ExecId' WHEN u.JobExecId = l.JobId THEN 'JobId' ELSE 'SubItem' END, sc.StoreLog " +
                "FROM SYS_Jobs_Logs l JOIN SYS_Jobs_Logs_Items i ON i.JobId = l.JobId LEFT JOIN SYS_UnixClient_Scripts sc ON sc.Id = l.ContentId " +
                "OUTER APPLY (SELECT TOP 1 x.JobExecId, x.StdOut, x.StdErr FROM CLT_UnixClient_Scripts_Logs x WHERE x.ClientId = i.ClientId AND (x.JobExecId = i.ExecId OR x.JobExecId = l.JobId " +
                "  OR x.JobExecId IN (SELECT s.ChildExecId FROM SYS_Jobs_Logs_SubItems s WHERE s.ParentExecId = i.ExecId))) u " +
                "WHERE (l.JobKind = " + ItemKind + " OR l.ContentId IN (SELECT Id FROM SYS_UnixClient_Scripts)) AND i.LastActivity >= @since " +
                (clientId != null ? "AND i.ClientId = @cid " : client != null ? "AND i.ComputerName = @cname " : "") +
                (scriptId != null ? "AND (l.ContentId = @sid OR (l.ContentId IS NULL AND l.JobName = @sname)) " : "") +
                "ORDER BY i.LastActivity DESC";
            q.Parameters.AddWithValue("@n", top);
            q.Parameters.AddWithValue("@since", DateTime.Now.AddDays(-days).ToOADate());
            if (clientId != null) q.Parameters.AddWithValue("@cid", G(clientId));
            else if (client != null) q.Parameters.AddWithValue("@cname", client);
            if (scriptId != null) { q.Parameters.AddWithValue("@sid", G(scriptId)); q.Parameters.AddWithValue("@sname", scriptName); }
            var runs = new JArray(); int withOut = 0;
            using (var r = q.ExecuteReader())
                while (r.Read())
                {
                    var o = new JObject();
                    o["client"] = r.IsDBNull(7) ? (r.IsDBNull(6) ? "?" : r.GetString(6)) : r.GetString(7);
                    o["skript"] = r.IsDBNull(0) ? "" : r.GetString(0);
                    o["status"] = JobState(r.GetByte(8));
                    o["ergebnis"] = JobResult(r.GetInt32(9));
                    o["letzteAktivitaet"] = r.IsDBNull(10) ? "-" : OleText(r.GetDouble(10));
                    o["gestartetVon"] = r.IsDBNull(1) ? "" : r.GetString(1);
                    o["ring"] = (int)r.GetByte(4);
                    if (!r.IsDBNull(11) && r.GetString(11).Length > 0) o["meldung"] = Short(r.GetString(11), 500);
                    if (!r.IsDBNull(14))
                    {
                        withOut++;
                        string so = r.IsDBNull(12) ? "" : r.GetString(12), se = r.IsDBNull(13) ? "" : r.GetString(13);
                        o["kurz"] = FirstLine(so);
                        if (output)
                        {
                            o["stdout"] = so.Length > maxChars ? so.Substring(0, maxChars) + "\n... (" + so.Length + " Zeichen, gekuerzt - maxChars erhoehen)" : so;
                            if (se.Length > 0) o["stderr"] = se.Length > maxChars ? se.Substring(0, maxChars) + "\n... (gekuerzt)" : se;
                        }
                        o["zuordnung"] = r.GetString(14);
                    }
                    else o["ausgabe"] = NoOutputText(r.GetByte(8), r.GetInt32(9), r.IsDBNull(15) ? -1 : r.GetByte(15));
                    o["execId"] = r.IsDBNull(5) ? null : "{" + r.GetString(5).ToUpperInvariant() + "}";
                    runs.Add(o);
                }
            var res = new JObject();
            res["zeitraumTage"] = days;
            res["laeufe"] = runs.Count;
            res["mitAusgabe"] = withOut;
            res["ergebnisse"] = runs;
            // Rueckfall: gibt es fuer den Client Ausgaben, aber keine zugeordneten Laeufe, die Ausgaben trotzdem zeigen (Zuordnung unbekannt)
            if (withOut == 0 && (clientId != null || client != null))
            {
                var uq = c.CreateCommand();
                uq.CommandText = "SELECT TOP 10 CAST(x.JobExecId AS varchar(36)), x.StdOut, x.StdErr FROM CLT_UnixClient_Scripts_Logs x " +
                    (clientId != null ? "WHERE x.ClientId = @cid" : "WHERE x.ClientId IN (SELECT i.ClientId FROM SYS_Jobs_Logs_Items i WHERE i.ComputerName = @cname)");
                if (clientId != null) uq.Parameters.AddWithValue("@cid", G(clientId)); else uq.Parameters.AddWithValue("@cname", client);
                var lone = new JArray();
                using (var r = uq.ExecuteReader())
                    while (r.Read())
                    {
                        var o = new JObject(); o["jobExecId"] = "{" + r.GetString(0).ToUpperInvariant() + "}";
                        string so = r.IsDBNull(1) ? "" : r.GetString(1);
                        o["kurz"] = FirstLine(so);
                        if (output) o["stdout"] = so.Length > maxChars ? so.Substring(0, maxChars) + "\n... (gekuerzt)" : so;
                        lone.Add(o);
                    }
                if (lone.Count > 0) { res["ausgabenOhneJobZuordnung"] = lone; res["hinweis"] = "Ausgaben vorhanden, aber keinem Job-Lauf zugeordnet - bitte melden (Zuordnung JobExecId pruefen)."; }
            }
            res["start"] = "Unix-Jobs startet nur die Console (Container oder Abfrage -> Unix-Jobs ausfuehren).";
            return res;
        }
    }

    // ================= Planen =================
    public JObject Plan(string tool, JObject a, string requester)
    {
        JObject p;
        if (tool == "acmp_unix_script_create") p = PlanCreate(a, requester);
        else if (tool == "acmp_unix_script_new_version") p = PlanNewVersion(a, requester);
        else if (tool == "acmp_unix_script_set_ring") p = PlanSetRing(a);
        else throw new Exception("Unbekanntes Unix-Tool: " + tool);
        if (Flag(a, "dryRun")) MarkNewIds(p);
        return p;
    }

    public const string NewIdText = "(neu, wird beim Antrag vergeben)";

    // dryRun: Die Ids neuer Zeilen vergibt erst der Antrag (dort eingefroren und so geschrieben) - die Zufalls-Ids des dryRun nicht zeigen
    // (beobachtet 22.10.2026: dryRun und Antrag vergaben verschiedene Ids). Vorhandene Ids (Skript bei new_version, Version bei set_ring) bleiben.
    public static void MarkNewIds(JObject plan)
    {
        string act = (string)plan["action"];
        if (act == "create") { plan["scriptId"] = NewIdText; plan["versionId"] = NewIdText; }
        else if (act == "new_version") plan["versionId"] = NewIdText;
    }

    static void AddContentPreview(JArray pv, string content, int lines)
    {
        string[] ls = content.Split(new[] { "\r\n" }, StringSplitOptions.None);
        pv.Add("Inhalt (" + ls.Length + " Zeilen, " + content.Length + " Zeichen, SHA-256 " + Sha256(content).Substring(0, 16) + "...):");
        for (int i = 0; i < ls.Length && i < lines; i++) pv.Add("   " + (i + 1).ToString().PadLeft(3) + "  " + Short(ls[i], 160));
        if (ls.Length > lines) pv.Add("   ... " + (ls.Length - lines) + " weitere Zeilen (vollstaendig in der Anfrage)");
    }

    JObject PlanCreate(JObject a, string requester)
    {
        string name = IsStr(a["name"]) ? ((string)a["name"]).Trim() : null;
        if (name == null) throw new Exception("name fehlt (Name des Unix Scripts, hoechstens " + MaxNameLength + " Zeichen).");
        if (name.Length > MaxNameLength) throw new Exception("name ist zu lang (" + name.Length + " Zeichen, hoechstens " + MaxNameLength + ").");
        int interp = ParseInterpreter(a["interpreter"]);
        int storeLog = ParseStoreLog(a["storeLog"]);
        int ring = a["ring"] == null || a["ring"].Type == JTokenType.Null ? 1 : ParseRing(a["ring"]);
        string desc = IsStr(a["description"]) ? ((string)a["description"]).Trim() : "";
        if (desc.Length > MaxDescriptionChars) throw new Exception("description ist zu lang (hoechstens " + MaxDescriptionChars + " Zeichen).");
        string content = NormalizeContent(a["content"] != null && a["content"].Type == JTokenType.String ? (string)a["content"] : null);
        string folderId, folderPath; int acl;
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            var f = LoadFolders(c);
            folderId = ResolveFolder(f, (string)a["folder"]);
            folderPath = FolderPath(f, folderId);
            if (_allowedFolders.Count > 0 && !_allowedFolders.Contains(folderId)) throw new Exception("Ordner '" + folderPath + "' ist nicht in unixWriter.allowedFolderIds - nichts geplant.");
            if (NameTaken(c, null, name, folderId)) throw new Exception("Im Ordner '" + folderPath + "' gibt es schon ein Unix Script '" + name + "' - fuer Aenderungen acmp_unix_script_new_version.");
            acl = FolderAclCount(c, null, folderId);
            if (acl == 0) throw new Exception("Ordner '" + folderPath + "' hat keine eigenen Rechte-Eintraege - das Skript waere in der Console unsichtbar. Nichts geplant.");
        }
        var plan = new JObject();
        plan["action"] = "create";
        plan["scriptId"] = "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}";
        plan["versionId"] = "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}";
        plan["name"] = name; plan["folderId"] = folderId; plan["folderPath"] = folderPath;
        plan["interpreter"] = interp; plan["storeLog"] = storeLog; plan["description"] = desc; plan["ring"] = ring;
        plan["content"] = content; plan["sha256"] = Sha256(content); plan["requester"] = requester;
        var pv = new JArray();
        pv.Add("Neues Unix Script anlegen (wie „Unix Script hinzufuegen\" in der Console): \"" + name + "\" in \"" + folderPath + "\", Version 1 in " + RingText(ring) + ".");
        pv.Add("Interpreter " + InterpreterText(interp) + ", Log speichern: " + StoreLogText(storeLog) + (desc.Length > 0 ? ", Beschreibung: \"" + Short(desc, 120) + "\"" : "") + ". Rechte wie der Ordner (" + acl + " Eintraege).");
        foreach (string n in ContentNotes(content, interp)) pv.Add("Hinweis: " + n);
        AddContentPreview(pv, content, 40);
        pv.Add(ring == RingReleased ? "Wirkung: sofort freigegeben - laeuft beim naechsten Unix-Job aus der Console auf allen Ziel-Clients."
                                    : "Wirkung: noch nicht freigegeben. Freigeben mit acmp_unix_script_set_ring ring=255 oder in der Console (Verteilungsring aendern). Gestartet wird in der Console.");
        plan["vorschau"] = pv;
        return plan;
    }

    JObject PlanNewVersion(JObject a, string requester)
    {
        string content = NormalizeContent(a["content"] != null && a["content"].Type == JTokenType.String ? (string)a["content"] : null);
        int ring = a["ring"] == null || a["ring"].Type == JTokenType.Null ? 1 : ParseRing(a["ring"]);
        ScriptInfo s; string baseContent; VersionInfo nw; string folderPath;
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            s = FindScript(c, (string)a["script"]);
            var f = LoadFolders(c);
            folderPath = FolderPath(f, s.FolderId);
            if (_allowedFolders.Count > 0 && !_allowedFolders.Contains(s.FolderId)) throw new Exception("Ordner '" + folderPath + "' ist nicht in unixWriter.allowedFolderIds - nichts geplant.");
            nw = Newest(s);
            baseContent = nw != null ? (ReadContent(c, nw.Id) ?? "") : "";
        }
        if (nw != null && Sha256(baseContent) == Sha256(content)) throw new Exception("Inhalt ist gleich der neuesten Version v" + nw.No + " - keine neue Version noetig.");
        int no = nw != null ? nw.No + 1 : 1;
        var plan = new JObject();
        plan["action"] = "new_version";
        plan["scriptId"] = s.Id; plan["name"] = s.Name; plan["folderPath"] = folderPath;
        plan["baseVersion"] = nw != null ? nw.No : 0; plan["baseSha256"] = Sha256(baseContent);
        plan["versionNo"] = no; plan["versionId"] = "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}";
        plan["collection"] = nw != null && nw.Collection != null ? nw.Collection : "Default";
        plan["ring"] = ring; plan["content"] = content; plan["sha256"] = Sha256(content); plan["requester"] = requester;
        VersionInfo rel = NewestReleased(s);
        var pv = new JArray();
        pv.Add("Neue Version v" + no + " von Unix Script \"" + s.Name + "\" (" + folderPath + ") anlegen (wie „Version hinzufuegen\" in der Console), in " + RingText(ring) + ".");
        pv.Add("Bisher: " + (nw != null ? "neueste v" + nw.No + " (" + RingText(nw.Ring) + ")" : "keine Version") + ", freigegeben: " + (rel != null ? "v" + rel.No : "keine") + ". Vorhandene Versionen bleiben unveraendert.");
        pv.Add("Aenderungen gegenueber v" + (nw != null ? nw.No : 0) + ":");
        foreach (string l in LineDiff(baseContent, content, 40)) pv.Add("   " + l);
        foreach (string n in ContentNotes(content, s.Interpreter)) pv.Add("Hinweis: " + n);
        pv.Add(ring == RingReleased ? "Wirkung: v" + no + " ist sofort freigegeben und laeuft beim naechsten Unix-Job aus der Console."
                                    : "Wirkung: auf Clients laeuft weiter " + (rel != null ? "v" + rel.No : "nichts") + ", bis v" + no + " freigegeben ist (acmp_unix_script_set_ring ring=255 oder Console).");
        plan["vorschau"] = pv;
        return plan;
    }

    JObject PlanSetRing(JObject a)
    {
        int ring = ParseRing(a["ring"]);
        ScriptInfo s; string folderPath;
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            s = FindScript(c, (string)a["script"]);
            folderPath = FolderPath(LoadFolders(c), s.FolderId);
        }
        if (_allowedFolders.Count > 0 && !_allowedFolders.Contains(s.FolderId)) throw new Exception("Ordner '" + folderPath + "' ist nicht in unixWriter.allowedFolderIds - nichts geplant.");
        VersionInfo v;
        if (a["version"] != null && a["version"].Type != JTokenType.Null)
        {
            int no = (int)a["version"];
            v = s.Versions.Find(x => x.No == no);
            if (v == null) throw new Exception("Version " + no + " von '" + s.Name + "' gibt es nicht.");
        }
        else { v = Newest(s); if (v == null) throw new Exception("'" + s.Name + "' hat keine Version."); }
        if (v.Ring < 0) throw new Exception("v" + v.No + " hat keine Ring-Zeile (ItemKind " + ItemKind + ") - in der Console pruefen, nichts geplant.");
        if (v.Ring == ring) throw new Exception("v" + v.No + " steht schon in " + RingText(ring) + " - nichts zu tun.");
        if (v.Declined != null) throw new Exception("v" + v.No + " ist verweigert (\"" + Short(v.Declined, 80) + "\") - in der Console pruefen, nichts geplant.");
        VersionInfo rel = NewestReleased(s);
        var plan = new JObject();
        plan["action"] = "set_ring";
        plan["scriptId"] = s.Id; plan["name"] = s.Name; plan["versionId"] = v.Id; plan["versionNo"] = v.No;
        plan["oldRing"] = v.Ring; plan["oldChanged"] = v.Changed; plan["ring"] = ring;
        var pv = new JArray();
        pv.Add("Verteilungsring aendern (wie in der Console): Unix Script \"" + s.Name + "\" (" + folderPath + ") v" + v.No + ": " + RingText(v.Ring) + " -> " + RingText(ring) + ".");
        if (ring == RingReleased)
            pv.Add("Wirkung: v" + v.No + " ist freigegeben" + (rel != null && rel.No > v.No ? " - aber v" + rel.No + " ist neuer und auch freigegeben, die laeuft weiter." : " und laeuft beim naechsten Unix-Job aus der Console" + (rel != null ? " statt v" + rel.No : "") + "."));
        else if (v.Ring == RingReleased)
        {
            VersionInfo prev = null;
            foreach (var x in s.Versions) if (x.No != v.No && x.Ring == RingReleased && (prev == null || x.No > prev.No)) prev = x;
            pv.Add("Wirkung: v" + v.No + " ist nicht mehr freigegeben; " + (prev != null ? "auf Clients laeuft dann v" + prev.No + "." : "es gibt dann keine freigegebene Version mehr."));
        }
        pv.Add("Zurueck: acmp_unix_script_set_ring mit ring=" + v.Ring + ".");
        plan["vorschau"] = pv;
        return plan;
    }

    static bool NameTaken(SqlConnection c, SqlTransaction tx, string name, string folderId)
    {
        var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = "SELECT COUNT(*) FROM SYS_UnixClient_Scripts WHERE Name = @n AND FolderId = @f";
        q.Parameters.AddWithValue("@n", name); q.Parameters.AddWithValue("@f", G(folderId));
        return Convert.ToInt32(q.ExecuteScalar()) > 0;
    }

    static int FolderAclCount(SqlConnection c, SqlTransaction tx, string folderId)
    {
        var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = "SELECT (SELECT COUNT(*) FROM SYS_Folders_AclEntries_Users WHERE ObjectID = @f) + (SELECT COUNT(*) FROM SYS_Folders_AclEntries_Groups WHERE ObjectID = @f)";
        q.Parameters.AddWithValue("@f", G(folderId));
        return Convert.ToInt32(q.ExecuteScalar());
    }

    // ================= Schreiben =================
    public JObject Execute(string tool, JObject a, string requester)
    {
        CheckWrite(requester, tool);
        JObject p = a["_plan"] as JObject;
        if (p == null) throw new Exception(tool + " schreibt nur mit dem eingefrorenen Plan aus der Vier-Augen-Freigabe - nichts geschrieben.");
        string want = tool == "acmp_unix_script_create" ? "create" : tool == "acmp_unix_script_new_version" ? "new_version" : "set_ring";
        if ((string)p["action"] != want) throw new Exception("Plan passt nicht zum Tool (" + p["action"] + " / " + tool + ") - nichts geschrieben.");
        if (p["content"] != null && Sha256((string)p["content"]) != (string)p["sha256"]) throw new Exception("Inhalt des Plans passt nicht zur Pruefsumme - nichts geschrieben.");
        lock (_wLock)
        {
            if (want == "create") return ExecCreate(p, requester);
            if (want == "new_version") return ExecNewVersion(p, requester);
            return ExecSetRing(p, requester);
        }
    }

    string SaveJournal(JObject b, string kind)
    {
        Directory.CreateDirectory(_backupDir);
        string file = Path.Combine(_backupDir, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "_" + kind + ".json");
        File.WriteAllText(file, b.ToString(Formatting.Indented), new UTF8Encoding(false));
        if (new FileInfo(file).Length < 40) throw new Exception("Protokoll unvollstaendig - nichts geaendert.");
        return file;
    }

    static void InsertVersion(SqlConnection c, SqlTransaction tx, string versionId, string scriptId, int no, string content, int ring, string collection, double now)
    {
        var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = "INSERT INTO SYS_UnixClient_Scripts_Versions (Id, ScriptId, VersionNo, Content) VALUES (@v, @s, @no, @c)";
        q.Parameters.AddWithValue("@v", G(versionId)); q.Parameters.AddWithValue("@s", G(scriptId)); q.Parameters.AddWithValue("@no", no);
        q.Parameters.Add("@c", System.Data.SqlDbType.NVarChar, -1).Value = content;
        if (q.ExecuteNonQuery() != 1) throw new Exception("INSERT der Version hat keine Zeile angelegt.");
        var r = c.CreateCommand(); r.Transaction = tx;
        r.CommandText = "INSERT INTO SYS_TestRings (Id, TestRing, ItemKind, LastChangeDate, TransitionMode, ItemState, CollectionName) VALUES (@v, @ring, @kind, @now, 1, 0, @coll)";
        r.Parameters.AddWithValue("@v", G(versionId)); r.Parameters.AddWithValue("@ring", ring); r.Parameters.AddWithValue("@kind", ItemKind);
        r.Parameters.AddWithValue("@now", now); r.Parameters.AddWithValue("@coll", collection ?? "Default");
        if (r.ExecuteNonQuery() != 1) throw new Exception("INSERT der Ring-Zeile hat keine Zeile angelegt.");
    }

    JObject ExecCreate(JObject p, string requester)
    {
        string sid = (string)p["scriptId"], vid = (string)p["versionId"], name = (string)p["name"], folder = (string)p["folderId"];
        int ring = (int)p["ring"]; int acl;
        string jf;
        using (var wc = new SqlConnection(_writeCs))
        {
            wc.Open();
            using (SqlTransaction tx = wc.BeginTransaction())
            {
                if (NameTaken(wc, tx, name, folder)) throw new Exception("Im Ordner gibt es inzwischen ein Unix Script '" + name + "' - nichts geschrieben, bitte neu beantragen.");
                var chk = wc.CreateCommand(); chk.Transaction = tx;
                chk.CommandText = "SELECT COUNT(*) FROM SYS_UnixClient_Scripts WHERE Id = @s"; chk.Parameters.AddWithValue("@s", G(sid));
                if (Convert.ToInt32(chk.ExecuteScalar()) > 0) throw new Exception("Skript " + sid + " gibt es schon (Plan schon ausgefuehrt?) - nichts geschrieben.");
                var q = wc.CreateCommand(); q.Transaction = tx;
                q.CommandText = "INSERT INTO SYS_UnixClient_Scripts (Id, Name, FolderId, Description, Interpreter, StoreLog) VALUES (@s, @n, @f, @d, @i, @l)";
                q.Parameters.AddWithValue("@s", G(sid)); q.Parameters.AddWithValue("@n", name); q.Parameters.AddWithValue("@f", G(folder));
                q.Parameters.Add("@d", System.Data.SqlDbType.NVarChar, -1).Value = (string)p["description"] ?? "";
                q.Parameters.AddWithValue("@i", (byte)(int)p["interpreter"]); q.Parameters.AddWithValue("@l", (byte)(int)p["storeLog"]);
                if (q.ExecuteNonQuery() != 1) throw new Exception("INSERT des Skripts hat keine Zeile angelegt.");
                InsertVersion(wc, tx, vid, sid, 1, (string)p["content"], ring, "Default", DateTime.Now.ToOADate());
                var au = wc.CreateCommand(); au.Transaction = tx;
                au.CommandText = "INSERT INTO SYS_AccessControl_AclEntries_Users (ObjectID, AccessID, EditState, VisibleState) SELECT @s, AccessID, EditState, VisibleState FROM SYS_Folders_AclEntries_Users WHERE ObjectID = @f";
                au.Parameters.AddWithValue("@s", G(sid)); au.Parameters.AddWithValue("@f", G(folder));
                acl = au.ExecuteNonQuery();
                var ag = wc.CreateCommand(); ag.Transaction = tx;
                ag.CommandText = "INSERT INTO SYS_AccessControl_AclEntries_Groups (ObjectID, AccessID, EditState, VisibleState) SELECT @s, AccessID, EditState, VisibleState FROM SYS_Folders_AclEntries_Groups WHERE ObjectID = @f";
                ag.Parameters.AddWithValue("@s", G(sid)); ag.Parameters.AddWithValue("@f", G(folder));
                acl += ag.ExecuteNonQuery();
                if (acl == 0) throw new Exception("Ordner hat keine Rechte-Eintraege - das Skript waere unsichtbar. Zurueckgerollt, nichts geschrieben.");
                tx.Commit();
            }
        }
        var j = new JObject(); j["kind"] = "create"; j["von"] = requester; j["zeit"] = DateTime.Now.ToString("s"); j["plan"] = p;
        jf = SaveJournal(j, "create");
        string check = Verify(sid, vid, (string)p["sha256"], ring);
        _log("acmp_unix_script_create von " + requester + ": '" + name + "' " + sid + " v1 " + vid + " (" + RingText(ring) + "), Kontrolle: " + check);
        var o = new JObject();
        o["ok"] = check == "ok"; o["scriptId"] = sid; o["versionId"] = vid; o["name"] = name; o["ordner"] = (string)p["folderPath"];
        o["ring"] = RingText(ring); o["rechte"] = acl; o["kontrolle"] = check; o["protokoll"] = Path.GetFileName(jf);
        o["naechsterSchritt"] = ring == RingReleased ? "In der Console starten (Container oder Abfrage -> Unix-Jobs ausfuehren), dann acmp_unix_results." : "Testen bzw. freigeben: acmp_unix_script_set_ring ring=255 oder Console; starten in der Console.";
        return o;
    }

    JObject ExecNewVersion(JObject p, string requester)
    {
        string sid = (string)p["scriptId"], vid = (string)p["versionId"];
        int no = (int)p["versionNo"], baseNo = (int)p["baseVersion"], ring = (int)p["ring"];
        string jf;
        using (var wc = new SqlConnection(_writeCs))
        {
            wc.Open();
            using (SqlTransaction tx = wc.BeginTransaction())
            {
                var q = wc.CreateCommand(); q.Transaction = tx;
                q.CommandText = "SELECT ISNULL(MAX(VersionNo), 0) FROM SYS_UnixClient_Scripts_Versions WITH (UPDLOCK, HOLDLOCK) WHERE ScriptId = @s";
                q.Parameters.AddWithValue("@s", G(sid));
                int max = Convert.ToInt32(q.ExecuteScalar());
                if (max != baseNo) throw new Exception("Seit der Anfrage gibt es eine neuere Version (v" + max + " statt v" + baseNo + ") - nichts geschrieben, bitte neu beantragen.");
                if (baseNo > 0)
                {
                    var bq = wc.CreateCommand(); bq.Transaction = tx;
                    bq.CommandText = "SELECT Content FROM SYS_UnixClient_Scripts_Versions WHERE ScriptId = @s AND VersionNo = @no";
                    bq.Parameters.AddWithValue("@s", G(sid)); bq.Parameters.AddWithValue("@no", baseNo);
                    object bc = bq.ExecuteScalar();
                    if (Sha256(bc == null || bc is DBNull ? "" : (string)bc) != (string)p["baseSha256"]) throw new Exception("v" + baseNo + " wurde seit der Anfrage geaendert (Version bearbeiten in der Console?) - nichts geschrieben, bitte neu beantragen.");
                }
                InsertVersion(wc, tx, vid, sid, no, (string)p["content"], ring, (string)p["collection"], DateTime.Now.ToOADate());
                tx.Commit();
            }
        }
        var j = new JObject(); j["kind"] = "new_version"; j["von"] = requester; j["zeit"] = DateTime.Now.ToString("s"); j["plan"] = p;
        jf = SaveJournal(j, "version");
        string check = Verify(sid, vid, (string)p["sha256"], ring);
        _log("acmp_unix_script_new_version von " + requester + ": '" + p["name"] + "' v" + no + " " + vid + " (" + RingText(ring) + "), Kontrolle: " + check);
        var o = new JObject();
        o["ok"] = check == "ok"; o["scriptId"] = sid; o["name"] = (string)p["name"]; o["version"] = no; o["versionId"] = vid;
        o["ring"] = RingText(ring); o["kontrolle"] = check; o["protokoll"] = Path.GetFileName(jf);
        o["naechsterSchritt"] = ring == RingReleased ? "In der Console starten, dann acmp_unix_results." : "Freigeben: acmp_unix_script_set_ring script=\"" + p["name"] + "\" version=" + no + " ring=255 (oder Console).";
        return o;
    }

    JObject ExecSetRing(JObject p, string requester)
    {
        string vid = (string)p["versionId"];
        int oldRing = (int)p["oldRing"], ring = (int)p["ring"];
        double oldChanged = (double)p["oldChanged"];
        string jf;
        using (var wc = new SqlConnection(_writeCs))
        {
            wc.Open();
            using (SqlTransaction tx = wc.BeginTransaction())
            {
                var q = wc.CreateCommand(); q.Transaction = tx;
                q.CommandText = "SELECT TestRing, LastChangeDate FROM SYS_TestRings WITH (UPDLOCK) WHERE Id = @v AND ItemKind = " + ItemKind;
                q.Parameters.AddWithValue("@v", G(vid));
                int curRing; double curChanged;
                using (var r = q.ExecuteReader())
                {
                    if (!r.Read()) throw new Exception("Ring-Zeile von v" + p["versionNo"] + " fehlt - nichts geschrieben.");
                    curRing = r.GetInt32(0); curChanged = r.GetDouble(1);
                }
                if (curRing != oldRing || Math.Abs(curChanged - oldChanged) > 1e-9)
                    throw new Exception("Ring von v" + p["versionNo"] + " wurde seit der Anfrage geaendert (jetzt " + RingText(curRing) + ") - nichts geschrieben, bitte neu beantragen.");
                var j = new JObject(); j["kind"] = "set_ring"; j["von"] = requester; j["zeit"] = DateTime.Now.ToString("s"); j["vorher"] = new JObject(new JProperty("testRing", curRing), new JProperty("lastChangeDate", curChanged)); j["plan"] = p;
                jf = SaveJournal(j, "ring");
                var u = wc.CreateCommand(); u.Transaction = tx;
                u.CommandText = "UPDATE SYS_TestRings SET TestRing = @ring, LastChangeDate = @now WHERE Id = @v AND ItemKind = " + ItemKind + " AND TestRing = @old";
                u.Parameters.AddWithValue("@ring", ring); u.Parameters.AddWithValue("@now", DateTime.Now.ToOADate());
                u.Parameters.AddWithValue("@v", G(vid)); u.Parameters.AddWithValue("@old", oldRing);
                if (u.ExecuteNonQuery() != 1) throw new Exception("UPDATE hat nicht genau eine Zeile geaendert - zurueckgerollt, nichts geschrieben.");
                tx.Commit();
            }
        }
        string check;
        using (var rc = new SqlConnection(_readCs))
        {
            rc.Open();
            var q = rc.CreateCommand(); q.CommandText = "SELECT TestRing FROM SYS_TestRings WHERE Id = @v AND ItemKind = " + ItemKind;
            q.Parameters.AddWithValue("@v", G(vid));
            object o0 = q.ExecuteScalar();
            check = o0 != null && !(o0 is DBNull) && Convert.ToInt32(o0) == ring ? "ok" : "Ring steht nicht auf " + ring;
        }
        _log("acmp_unix_script_set_ring von " + requester + ": '" + p["name"] + "' v" + p["versionNo"] + " " + RingText(oldRing) + " -> " + RingText(ring) + ", Kontrolle: " + check);
        var o = new JObject();
        o["ok"] = check == "ok"; o["name"] = (string)p["name"]; o["version"] = (int)p["versionNo"];
        o["vorher"] = RingText(oldRing); o["jetzt"] = RingText(ring); o["kontrolle"] = check; o["protokoll"] = Path.GetFileName(jf);
        o["zurueck"] = "acmp_unix_script_set_ring script=\"" + p["name"] + "\" version=" + p["versionNo"] + " ring=" + oldRing;
        return o;
    }

    // Kontrolle mit dem Lese-Login: Version da, Inhalt gleich, Ring wie geplant
    string Verify(string sid, string vid, string sha, int ring)
    {
        using (var rc = new SqlConnection(_readCs))
        {
            rc.Open();
            var q = rc.CreateCommand();
            q.CommandText = "SELECT v.Content, r.TestRing, r.ItemKind FROM SYS_UnixClient_Scripts_Versions v LEFT JOIN SYS_TestRings r ON r.Id = v.Id WHERE v.Id = @v AND v.ScriptId = @s";
            q.Parameters.AddWithValue("@v", G(vid)); q.Parameters.AddWithValue("@s", G(sid));
            using (var r = q.ExecuteReader())
            {
                if (!r.Read()) return "Version nicht gefunden";
                if (Sha256(r.GetString(0)) != sha) return "Inhalt weicht ab";
                if (r.IsDBNull(1)) return "Ring-Zeile fehlt";
                if (r.GetInt32(1) != ring || r.GetInt32(2) != ItemKind) return "Ring " + r.GetInt32(1) + "/ItemKind " + r.GetInt32(2) + " statt " + ring + "/" + ItemKind;
                return "ok";
            }
        }
    }
}
