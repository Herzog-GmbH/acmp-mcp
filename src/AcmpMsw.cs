// AcmpMsw.cs - Aagon Managed Software (MSW) im ACMP-MCP (24.10.2026).
// Lesen:     acmp_msw_packages (Abos/Katalog: Versionen je Ring, Installationen, Updates, Jobs), acmp_msw_package (ein Paket im Detail:
//            Versionen, Verteilung in der Flotte, Clients mit Update, Jobs, Parameter), acmp_msw_client (MSW-Stand eines Clients).
// Schreiben (partial AcmpJobs, gleiche Machart wie die Job-Tools: dryRun, Sicherung jobs-backups\, Aktivitaetsprotokoll und Notbremse im Server,
// Ruecknahme ueber acmp_job_restore bzw. acmp_job_delete):
//  - acmp_msw_job_create:   Dauerauftrag (JobKind 59) an einem Container wie der ACMP-Server beim Speichern in der Console.
//  - acmp_msw_test_install: einmaliger Auftrag an 1-5 Clients wie "Managed Software zuweisen" (Einzelauftrag, IsManualJob 1).
//  - acmp_msw_subscribe:    Paket abonnieren wie der ACMP-Server (EXEC ap_ManagedSoftware_Subscribe), Ruecknahme = abbestellen.
//  - acmp_job_delete fuer einmalige Jobs (Job-Monitor "Loeschen", Aagon.JobManagement.DeleteJobLogs) und sofortiges Aufraeumen von Job und Vorlage
//    wie ap_Job_ClearOrphanTemplates - nur fuer genau diesen Job.
// Belege (ohne eigenen Console-Mitschnitt):
//  - Mitschnitte 1 und 3 vom 22.10.2026: Anlegen eines Container-Jobs = Upsert SYS_Templates "Job template for container: {Id}",
//    INSERT SYS_Jobs, Upsert SYS_Jobs_Logs (State 1, CreatorName, ConditionText, TestRing), Upsert SYS_Templates_Jobs, INSERT SYS_Containers_Templates.
//  - JobAction TManagedSoftwareJobAction aus drei Sicherungen geloeschter MSW-Jobs (Firefox 32/64, 7-Zip).
//  - Server-Module (ACMP Server\CommandModules): Aagon.JobManagement.Commands.dll (DeleteJobLogs, CreateManualJobs mit JobNames/ClientIds/StartCondition/
//    JobActions/TestRing/OsFamily + Pull-Request, ap_Job_BulkAddJobLogItems, ap_Job_ClearOrphanTemplates), Aagon.ManagedSoftware.Commands.dll
//    (Subscribe: Lizenzpruefung Evaluation, EXEC ap_ManagedSoftware_Subscribe @aCatalogIds), Aagon.JobServices.AgentPush.dll.
//  - Enums der Console (Acmp.Data.bpl, AcmpCore.bpl): TSoftwareAction (saInstallOrUpdate, saUpdateOnly, saReinstall, saUninstall), TLogStorageOption,
//    TDeploymentOption (0 kein Up-/Downgrade, 1 Upgrade moeglich, 2 Downgrade moeglich), TEditorType (etText, etCombobox, etCheckbox),
//    TParameterOption, TParameterAvailability, TJobExecState, TJobResult.
//  - DB: Einzelauftraege der Public API (Vorlage ohne Namen, IsReferenceCounted 1, CLT_Clients_Templates, Items State 0), af_Jobs_CalcClientTemplates /
//    af_Jobs_CalcJobsOfTemplates (so holen Agents Einzelauftraege: Vorlage am Client + offener Eintrag in SYS_Jobs_Logs_Items),
//    af_ManagedSoftware_GetHighestReleasedVersion (Version je Ring: SYS_TestRings.TestRing >= Ring), Ring-Konfiguration ManagedSoftwareConfig.
// Grenze: Der MCP schreibt direkt in die DB; den Pull-Request des ACMP-Servers an die Agents (sofortige Zustellung) kann er nicht ausloesen.
// Ein Testauftrag wird beim naechsten Abruf der Job-Liste durch den Agent geholt (Agentenstart, regelmaessige Job-Abfrage) - belegt an Einzelauftraegen,
// deren Push scheiterte ("Could not connect") und die spaeter liefen.
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using Newtonsoft.Json.Linq;

partial class AcmpJobs
{
    public const int MswKind = 59;
    public const int RingAll = 255;
    public const int MaxTestClients = 5;
    public const int MaxSubscribe = 5;
    public const int MswDefaultTop = 100;
    public const int MswMaxTop = 500;
    public const string DefaultLogOption = "soStoreScriptLogOnError";
    // Vom Server gesetzt (GuardProtectedTargets): wirft, wenn ein Ziel-Client geschuetzt ist (ACMP-/MCP-Server selbst)
    public static Action<string> GuardTargets;

    public static bool IsMswReadTool(string n) { return n == "acmp_msw_packages" || n == "acmp_msw_package" || n == "acmp_msw_client"; }
    public static bool IsMswWriteTool(string n) { return n == "acmp_msw_job_create" || n == "acmp_msw_test_install" || n == "acmp_msw_subscribe"; }

    // ================= Hilfen ohne DB (offline getestet in MswTest.cs) =================

    static string NormKey(string s)
    {
        return (s ?? "").Trim().ToLowerInvariant().Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss")
                        .Replace("-", "").Replace("_", "").Replace(" ", "");
    }

    // Aktion -> TSoftwareAction der Console. Deinstallieren bietet der MCP bewusst nicht an.
    public static string SoftwareActionOf(string s, bool allowReinstall)
    {
        if (s == null) return null;
        switch (NormKey(s))
        {
            case "install": case "installieren": case "installorupdate": case "installierenoderaktualisieren": case "sainstallorupdate": return "saInstallOrUpdate";
            case "update": case "aktualisieren": case "updateonly": case "nuraktualisieren": case "saupdateonly": return "saUpdateOnly";
            case "reinstall": case "neuinstallieren": case "neuinstallation": case "sareinstall": return allowReinstall ? "saReinstall" : null;
        }
        return null;
    }

    public static string SoftwareActionText(string sa)
    {
        switch (sa)
        {
            case "saInstallOrUpdate": return "installieren oder aktualisieren (installiert auch auf Clients ohne das Paket)";
            case "saUpdateOnly": return "nur aktualisieren (nur wo das Paket schon installiert ist)";
            case "saReinstall": return "neu installieren";
            case "saUninstall": return "deinstallieren";
        }
        return sa ?? "?";
    }

    public static readonly string[] LogOptions = { "soNeverStoreScriptLog", "soStoreScriptLogOnError", "soAlwaysStoreScriptLog" };

    public static string LogOptionOf(string s, string dflt)
    {
        if (s == null || s.Trim().Length == 0) return Array.IndexOf(LogOptions, dflt) >= 0 ? dflt : DefaultLogOption;
        foreach (string o in LogOptions) if (string.Equals(o, s.Trim(), StringComparison.OrdinalIgnoreCase)) return o;
        switch (NormKey(s))
        {
            case "onerror": case "fehler": case "beifehler": return "soStoreScriptLogOnError";
            case "always": case "immer": return "soAlwaysStoreScriptLog";
            case "never": case "nie": return "soNeverStoreScriptLog";
        }
        throw new Exception("log '" + s + "' unbekannt (erlaubt: onError, always, never).");
    }

    // JobKinds der Job-Vorlage (binary(16)): Bit (Art mod 8) im Byte (Art div 8), z.B. 4 -> 0x10..., 58 -> Byte 7 = 0x04, 59 -> Byte 7 = 0x08 (DB 24.10.2026)
    public static byte[] KindsMask(byte[] existing, int kind)
    {
        if (kind < 0 || kind >= 128) throw new Exception("JobKind " + kind + " ausserhalb der Maske.");
        var b = new byte[16];
        if (existing != null) Array.Copy(existing, b, Math.Min(16, existing.Length));
        b[kind / 8] |= (byte)(1 << (kind % 8));
        return b;
    }

    public static string HexOf(byte[] b)
    {
        if (b == null) return null;
        var sb = new StringBuilder("0x");
        foreach (byte x in b) sb.Append(x.ToString("X2"));
        return sb.ToString();
    }

    public static byte[] BytesOfHex(string h)
    {
        if (string.IsNullOrEmpty(h)) return null;
        if (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) h = h.Substring(2);
        var b = new byte[h.Length / 2];
        for (int i = 0; i < b.Length; i++) b[i] = byte.Parse(h.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return b;
    }

    public static string RingText(int r)
    {
        if (r < 0) return "noch nicht heruntergeladen (kein Ring)";
        if (r == 0) return "nicht freigegeben";
        if (r >= RingAll) return "freigegeben (alle)";
        return "Testring " + r;
    }

    public static string ItemStateText(int s)
    {
        switch (s) { case 0: return "nicht heruntergeladen"; case 1: return "wird synchronisiert"; case 2: return "synchronisiert"; }
        return s < 0 ? "-" : "Zustand " + s;
    }

    public static string DeploymentText(int o)
    {
        switch (o) { case 0: return "kein Update (aktuell oder nicht aktualisierbar)"; case 1: return "Update moeglich"; case 2: return "neuer als die Freigabe"; }
        return "unbekannt (" + o + ")";
    }

    public static string ExecStateText(int s)
    {
        switch (s)
        {
            case 0: return "wartet auf Zustellung"; case 1: return "zugestellt"; case 2: return "laeuft"; case 3: return "fertig";
            case 4: return "abgebrochen"; case 5: return "verschoben"; case 6: return "wartet auf Wartungsfenster";
        }
        return "Zustand " + s;
    }

    public static string ResultText(int r)
    {
        switch (r) { case 0: return "-"; case 1: return "Erfolg"; case 2: return "fehlgeschlagen"; case 3: return "uebersprungen"; case 4: return "keine Lizenz"; }
        return "Ergebnis " + r;
    }

    // ---------- Parameter der Pakete (SYS_Jobs_Parameters_Declarations/_Config/_Config_Values) ----------
    public class MswValue { public string Id, Value, CaptionEn, CaptionDe; public bool IsDefault; public int Priority; }

    public class MswParam
    {
        public string Id, Name, Value, DefaultValue, CaptionEn, CaptionDe;
        public int EditorType = -1, VersionStart = 1, VersionEnd = int.MaxValue, Availability, Option, Priority;
        public List<MswValue> Values = new List<MswValue>();
    }

    public static readonly string[] EditorTypes = { "etText", "etCombobox", "etCheckbox" };
    public static readonly string[] ParamOptions = { "poInvisibleForEndUser", "poReadOnlyForEndUser", "poEditableByEndUser" };
    public static readonly string[] ParamAvailabilities = { "paInstallAndUpdate", "paInstallOnly" };

    static string EnumName(string[] names, int v) { return v >= 0 && v < names.Length ? names[v] : null; }

    public static bool? StrictBool(string s)
    {
        switch (NormKey(s))
        {
            case "1": case "true": case "ja": case "yes": case "wahr": case "an": return true;
            case "0": case "false": case "nein": case "no": case "falsch": case "aus": return false;
        }
        return null;
    }

    static string VariantText(object v)
    {
        if (v == null || v is DBNull) return "";
        if (v is bool) return (bool)v ? "1" : "0";
        return Convert.ToString(v, CultureInfo.InvariantCulture);
    }

    // Wert wie die Console ihn in die JobAction schreibt: Kontrollkaestchen "True"/"False", sonst der Text (Auswahl = "{GUID}" des Eintrags)
    public static string ParamValueText(object v, int editorType)
    {
        string s = VariantText(v);
        if (editorType == 2) { bool? b = StrictBool(s); return b.HasValue && b.Value ? "True" : "False"; }
        return s;
    }

    // Standardwert wie in der JobAction der Console: Kontrollkaestchen "true"/"false" (klein), sonst der Text
    public static string ParamDefaultText(object v, int editorType)
    {
        string s = VariantText(v);
        if (editorType == 2) { bool? b = StrictBool(s); return b.HasValue && b.Value ? "true" : "false"; }
        return s;
    }

    // Eingabe fuer einen Parameter pruefen und in den gespeicherten Wert umsetzen
    public static string ParamInput(MswParam p, JToken t)
    {
        if (t == null || t.Type == JTokenType.Null) throw new Exception("Parameter " + p.Name + ": Wert fehlt.");
        string s = t.Type == JTokenType.Boolean ? ((bool)t ? "true" : "false") : (t is JValue ? Convert.ToString(((JValue)t).Value, CultureInfo.InvariantCulture) : null);
        if (s == null) throw new Exception("Parameter " + p.Name + ": Wert muss Text, Zahl oder true/false sein.");
        if (p.EditorType == 2)
        {
            bool? b = StrictBool(s);
            if (!b.HasValue) throw new Exception("Parameter " + p.Name + " (" + p.CaptionDe + ") ist ein Kontrollkaestchen: true oder false, nicht '" + s + "'.");
            return b.Value ? "True" : "False";
        }
        if (p.EditorType == 1)
        {
            string g = GuidBraces(s);
            var names = new List<string>();
            foreach (MswValue v in p.Values)
            {
                if ((g != null && string.Equals(GuidBraces(v.Value) ?? v.Id, g, StringComparison.OrdinalIgnoreCase)) || string.Equals(v.Value, s.Trim(), StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(v.CaptionDe, s.Trim(), StringComparison.OrdinalIgnoreCase) || string.Equals(v.CaptionEn, s.Trim(), StringComparison.OrdinalIgnoreCase))
                    return v.Value;
                names.Add(v.CaptionDe + " (" + v.CaptionEn + ")");
            }
            throw new Exception("Parameter " + p.Name + " (" + p.CaptionDe + "): '" + s + "' gibt es nicht. Erlaubt: " + string.Join(", ", names.ToArray()) + ".");
        }
        if (s.Length > 4000) throw new Exception("Parameter " + p.Name + ": Text zu lang (hoechstens 4000 Zeichen).");
        return s;
    }

    // Anzeigeversion zu einer Paketversion (DisplayVersionStart der Console: die kleinste vorhandene Version ab VersionStart, bei VersionEnd die groesste bis dahin)
    public static string DisplayVersionFrom(SortedDictionary<int, string> versions, int number, bool end)
    {
        if (versions == null || versions.Count == 0) return "";
        if (end && number >= int.MaxValue) return "";
        string hit = "";
        foreach (KeyValuePair<int, string> kv in versions)
        {
            if (!end && kv.Key >= number) return kv.Value ?? "";
            if (end && kv.Key <= number) hit = kv.Value ?? "";
        }
        return hit;
    }

    static XmlElement AddEl(XmlElement parent, string name, string text)
    {
        XmlElement e = parent.OwnerDocument.CreateElement(name);
        if (!string.IsNullOrEmpty(text)) e.InnerText = text;
        parent.AppendChild(e);
        return e;
    }

    // Ein Parameter der JobAction wie die Console ihn beim Anlegen schreibt (OwnerId = Paket, TEditorConfig mit Auswahlwerten; Sicherungen vom 23.10.2026)
    public static XmlElement ParamElement(XmlDocument d, MswParam p, string contentId, string value, SortedDictionary<int, string> versions)
    {
        string avail = EnumName(ParamAvailabilities, p.Availability), opt = EnumName(ParamOptions, p.Option), et = EnumName(EditorTypes, p.EditorType);
        if (avail == null || opt == null || et == null)
            throw new Exception("Parameter " + p.Name + ": unbekannte Einstellung (Availability " + p.Availability + ", Option " + p.Option + ", EditorType " + p.EditorType + ") - bitte in der Console anlegen.");
        XmlElement e = d.CreateElement("TParameter");
        AddEl(e, "OwnerId", contentId);
        AddEl(e, "DeclarationContentId", contentId);
        AddEl(e, "VariableName", p.Name);
        AddEl(e, "Availability", avail);
        AddEl(e, "Value", value);
        AddEl(e, "DefaultValue", p.DefaultValue);
        XmlElement tc = AddEl(AddEl(e, "EditorConfig", null), "TEditorConfig", null);
        XmlElement list = AddEl(tc, "TAvailableValueItemList", null);
        foreach (MswValue v in p.Values)
        {
            XmlElement it = AddEl(list, "TAvailableValueItem", null);
            AddEl(it, "DBID", v.Id);       // Zeile in SYS_Jobs_Parameters_Config_Values
            AddEl(it, "Id", v.Value);      // gespeicherter Wert, z.B. "{...}" oder "sk" (Firefox-Sprachen)
            AddEl(it, "IsDefault", v.IsDefault ? "true" : "false");
            AddEl(it, "Priority", v.Priority.ToString(CultureInfo.InvariantCulture));
            AddEl(it, "Caption_en", v.CaptionEn);
            AddEl(it, "Caption_de", v.CaptionDe);
            AddEl(it, "Action", "eaEdit");
        }
        AddEl(tc, "EditorType", et);
        AddEl(tc, "Id", p.Id);
        AddEl(tc, "Caption_en", p.CaptionEn);
        AddEl(tc, "Caption_de", p.CaptionDe);
        AddEl(tc, "Action", "eaEdit");
        AddEl(e, "VersionStart", p.VersionStart.ToString(CultureInfo.InvariantCulture));
        AddEl(e, "VersionEnd", p.VersionEnd.ToString(CultureInfo.InvariantCulture));
        AddEl(e, "Option", opt);
        AddEl(e, "Id", p.Id);
        AddEl(e, "DisplayVersionStart", DisplayVersionFrom(versions, p.VersionStart, false));
        AddEl(e, "DisplayVersionEnd", DisplayVersionFrom(versions, p.VersionEnd, true));
        AddEl(e, "Priority", p.Priority.ToString(CultureInfo.InvariantCulture));
        AddEl(e, "Action", "eaEdit");
        return e;
    }

    // JobAction eines MSW-Jobs (Reihenfolge der Elemente wie in den Sicherungen der Console-Jobs vom 23.10.2026)
    public static string MswJobActionXml(string contentId, string name, string softwareAction, string logOption, int testRing,
                                         IList<MswParam> pars, IDictionary<string, string> values, SortedDictionary<int, string> versions)
    {
        if (Array.IndexOf(new[] { "saInstallOrUpdate", "saUpdateOnly", "saReinstall" }, softwareAction) < 0) throw new Exception("SoftwareAction '" + softwareAction + "' nicht erlaubt.");
        if (Array.IndexOf(LogOptions, logOption) < 0) throw new Exception("LogStorageOption '" + logOption + "' unbekannt.");
        var d = new XmlDocument();
        XmlElement r = d.CreateElement("TManagedSoftwareJobAction");
        d.AppendChild(r);
        AddEl(r, "SoftwareAction", softwareAction);
        AddEl(r, "LogStorageOption", logOption);
        XmlElement pl = AddEl(r, "TParameterList", null);
        if (pars != null)
            foreach (MswParam p in pars)
            {
                string v;
                if (values == null || !values.TryGetValue(p.Name, out v)) v = p.Value;
                pl.AppendChild(ParamElement(d, p, contentId, v, versions));
            }
        AddEl(r, "ChildExecId", null);
        AddEl(r, "MessageCode", "-1");
        AddEl(r, "ErrorCode", "0");
        AddEl(r, "ParentJobId", null);
        AddEl(r, "ParentJobExecId", null);
        AddEl(r, "Name", name);
        AddEl(r, "ContentId", contentId);
        AddEl(r, "JobExecId", null);
        AddEl(r, "CanRunParallel", "false");
        AddEl(r, "TestRing", testRing.ToString(CultureInfo.InvariantCulture));
        AddEl(r, "IsAgentInRollout", "false");
        AddEl(r, "JobExecMode", "emAlways");
        AddEl(r, "Action", "eaEdit");
        return d.OuterXml;
    }

    // Startbedingung eines Einzelauftrags wie die Public API (EnqueueClientCommand) sie anlegt: "Sobald wie moeglich", einmalig, Huelle eaAdd
    public static string ManualConditionXml(string id, bool ignoreMw)
    {
        return "<TStartConditionFacade><TStartConditionList><TStartConditionOnNow>" + WakeUpXml + CondCommon(id, "coNow", true) + "</TStartConditionOnNow></TStartConditionList>" +
               "<CanIgnoreMaintenanceTimes>" + (ignoreMw ? "true" : "false") + "</CanIgnoreMaintenanceTimes><ConditionText>" + ConditionTextFor("once", 1, ignoreMw) + "</ConditionText>" +
               FacadeTailXml + "<Action>eaAdd</Action></TStartConditionFacade>";
    }

    // Liste fuer die Prozeduren (ap_ManagedSoftware_Subscribe, ap_Job_BulkAddJobLogItems): <Ids><string>{...}</string>...</Ids>
    public static string IdsXml(IEnumerable<string> ids)
    {
        var sb = new StringBuilder("<Ids>");
        foreach (string s in ids)
        {
            string g = GuidBraces(s);
            if (g == null) throw new Exception("'" + s + "' ist keine GUID.");
            sb.Append("<string>").Append(g).Append("</string>");
        }
        return sb.Append("</Ids>").ToString();
    }

    // Einmaliger Job: darf der MCP ihn loeschen (Job-Monitor)? null = ja
    public static readonly int[] ManualDeletableKinds = { 4, 13, 58, 59 };

    public static string RefuseManualDelete(int kind, int origin, int templateLinks, bool refCounted, bool namedTemplate, int containerLinks)
    {
        if (Array.IndexOf(ManualDeletableKinds, kind) < 0)
            return "ist ein einmaliger Job der Art " + KindName(kind) + " - per MCP nur Client Command, Konfigurationsprofil, WUM-Sammlung und Managed Software, sonst im Job-Monitor der Console";
        if (origin == 1) return "stammt von einem Container (Origin 1) - das ist kein Einzelauftrag";
        if (templateLinks != 1) return "haengt an " + templateLinks + " Job-Vorlagen (erwartet genau eine Einzelauftrags-Vorlage) - bitte im Job-Monitor der Console loeschen";
        if (!refCounted || namedTemplate || containerLinks > 0)
            return "gehoert zu einer Job-Sammlung oder benannten Vorlage (z.B. eine Job-Sammlung fuer neue Rechner) - kein Einzelauftrag, bitte in der Console aendern";
        return null;
    }

    // ================= Argumente =================

    static string StrArg(JObject a, string name)
    {
        JToken t = a[name];
        if (t == null || t.Type == JTokenType.Null) return null;
        if (t.Type == JTokenType.String) { string s = ((string)t).Trim(); return s.Length == 0 ? null : s; }
        if (t is JValue) return Convert.ToString(((JValue)t).Value, CultureInfo.InvariantCulture);
        throw new Exception(name + " muss ein Text sein.");
    }

    // Liste aus Array, JSON-Text oder kommagetrenntem Text
    static List<string> ListArg(JObject a, string name)
    {
        var l = new List<string>();
        JToken t = a[name];
        if (t == null || t.Type == JTokenType.Null) return l;
        if (t.Type == JTokenType.String)
        {
            string s = ((string)t).Trim();
            if (s.StartsWith("[")) { try { t = JToken.Parse(s); } catch (Exception ex) { throw new Exception(name + " ist kein gueltiges JSON: " + ex.Message); } }
            else { foreach (string p in s.Split(',', ';')) if (p.Trim().Length > 0) l.Add(p.Trim()); return l; }
        }
        JArray arr = t as JArray;
        if (arr == null) throw new Exception(name + " muss eine Liste sein.");
        foreach (JToken x in arr) { string v = x.Type == JTokenType.String ? ((string)x).Trim() : (x is JValue ? Convert.ToString(((JValue)x).Value, CultureInfo.InvariantCulture) : ""); if (v.Length > 0) l.Add(v); }
        return l;
    }

    static string NewId() { return "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}"; }

    static Guid G(string s) { return new Guid(s.Trim('{', '}')); }

    // ================= Lesen: Pakete =================

    class MswVersion
    {
        public int Version, Revision, Ring = -1, ItemState = -1, Transition = -1;
        public string Display, VersionId, RevisionId;
        public double ReleaseDate;
        public bool Declined;
    }

    class MswPkg
    {
        public string Id, Name, Manufacturer;
        public bool Subscribed;
        public int State, License;
        public List<MswVersion> Versions = new List<MswVersion>();   // absteigend nach Version, Revision
        public List<MswParam> Params = new List<MswParam>();
        public MswVersion Latest { get { return Versions.Count > 0 ? Versions[0] : null; } }
        // hoechste Version, die fuer den Ring freigegeben ist (wie af_ManagedSoftware_GetHighestReleasedVersion: TestRing >= Ring, nicht abgelehnt)
        public MswVersion ReleasedFor(int ring)
        {
            foreach (MswVersion v in Versions) if (!v.Declined && v.Ring >= ring && v.Ring > 0) return v;
            return null;
        }
        public SortedDictionary<int, string> VersionMap()
        {
            var m = new SortedDictionary<int, string>();
            foreach (MswVersion v in Versions) if (!m.ContainsKey(v.Version)) m[v.Version] = v.Display;   // absteigend sortiert: erste = hoechste Revision
            return m;
        }
    }

    // Paket per GUID, genauem Namen oder eindeutigem Teilnamen -> Id
    string ResolvePkg(SqlConnection c, string arg)
    {
        if (string.IsNullOrEmpty(arg)) throw new Exception("package fehlt: Paket-Id (GUID aus acmp_msw_packages) oder Name wie 'Google Chrome 64-Bit'.");
        string g = GuidBraces(arg);
        if (g != null)
        {
            var q = Cmd(c, "SELECT COUNT(*) FROM SYS_ManagedSoftware_Catalog WHERE Id = @id");
            q.Parameters.AddWithValue("@id", G(g));
            if (Convert.ToInt32(q.ExecuteScalar()) != 1) throw new Exception("Paket " + g + " gibt es nicht im Katalog (SYS_ManagedSoftware_Catalog).");
            return g;
        }
        var hits = new List<string[]>();
        var e = Cmd(c, "SELECT CAST(c.Id AS varchar(36)), c.Name, CASE WHEN s.Id IS NULL THEN 0 ELSE 1 END FROM SYS_ManagedSoftware_Catalog c LEFT JOIN SYS_ManagedSoftware_Subscriptions s ON s.Id = c.Id WHERE c.Name = @n");
        e.Parameters.AddWithValue("@n", arg.Trim());
        using (var r = e.ExecuteReader()) while (r.Read()) hits.Add(new[] { Gid(r, 0), Str(r, 1), Int(r, 2).ToString() });
        if (hits.Count == 1) return hits[0][0];
        if (hits.Count == 0)
        {
            var l = Cmd(c, "SELECT TOP 20 CAST(c.Id AS varchar(36)), c.Name, CASE WHEN s.Id IS NULL THEN 0 ELSE 1 END FROM SYS_ManagedSoftware_Catalog c LEFT JOIN SYS_ManagedSoftware_Subscriptions s ON s.Id = c.Id " +
                "WHERE c.Name LIKE @n ORDER BY CASE WHEN s.Id IS NULL THEN 1 ELSE 0 END, c.Name");
            l.Parameters.AddWithValue("@n", "%" + arg.Trim().Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%");
            using (var r = l.ExecuteReader()) while (r.Read()) hits.Add(new[] { Gid(r, 0), Str(r, 1), Int(r, 2).ToString() });
            if (hits.Count == 1) return hits[0][0];
        }
        if (hits.Count == 0) throw new Exception("Kein Paket '" + arg + "' im Katalog - acmp_msw_packages catalog=true name=... zeigt den Katalog.");
        var names = new List<string>();
        foreach (string[] h in hits) names.Add(h[1] + (h[2] == "1" ? " (abonniert)" : "") + " " + h[0]);
        throw new Exception("'" + arg + "' ist nicht eindeutig: " + string.Join("; ", names.ToArray()) + " - bitte genauen Namen oder Id angeben.");
    }

    MswPkg LoadPkg(SqlConnection c, string id, bool withParams)
    {
        var p = new MswPkg();
        var q = Cmd(c, "SELECT CAST(c.Id AS varchar(36)), c.Name, m.Name, CASE WHEN s.Id IS NULL THEN 0 ELSE 1 END, c.SoftwareState, c.LicenseKind FROM SYS_ManagedSoftware_Catalog c " +
            "LEFT JOIN SYS_ManagedSoftware_Manufacturers m ON m.Id = c.ManufacturerId LEFT JOIN SYS_ManagedSoftware_Subscriptions s ON s.Id = c.Id WHERE c.Id = @id");
        q.Parameters.AddWithValue("@id", G(id));
        using (var r = q.ExecuteReader())
        {
            if (!r.Read()) throw new Exception("Paket " + id + " gibt es nicht.");
            p.Id = Gid(r, 0); p.Name = Str(r, 1) ?? ""; p.Manufacturer = Str(r, 2); p.Subscribed = Int(r, 3) == 1; p.State = Int(r, 4); p.License = Int(r, 5);
        }
        var v = Cmd(c, "SELECT v.Version, v.Revision, v.DisplayVersion, CAST(v.VersionId AS varchar(36)), CAST(v.RevisionId AS varchar(36)), r.TestRing, r.ItemState, r.TransitionMode, ri.ReleaseDate, " +
            "CASE WHEN d.Id IS NULL THEN 0 ELSE 1 END FROM SYS_ManagedSoftware_Versions v LEFT JOIN SYS_TestRings r ON r.Id = v.RevisionId " +
            "LEFT JOIN SYS_ManagedSoftware_Versions_ReleaseInfo ri ON ri.Id = v.VersionId LEFT JOIN SYS_TestRings_DeclineInfo d ON d.Id = v.RevisionId " +
            "WHERE v.SoftwareId = @id ORDER BY v.Version DESC, v.Revision DESC");
        v.Parameters.AddWithValue("@id", G(id));
        using (var r = v.ExecuteReader())
            while (r.Read())
            {
                var x = new MswVersion();
                x.Version = Int(r, 0); x.Revision = Int(r, 1); x.Display = Str(r, 2) ?? ""; x.VersionId = Gid(r, 3); x.RevisionId = Gid(r, 4);
                x.Ring = Int(r, 5); x.ItemState = Int(r, 6); x.Transition = Int(r, 7); x.ReleaseDate = Dbl(r, 8); x.Declined = Int(r, 9) == 1;
                p.Versions.Add(x);
            }
        if (!withParams) return p;
        var byId = new Dictionary<string, MswParam>(StringComparer.OrdinalIgnoreCase);
        var dq = Cmd(c, "SELECT CAST(d.Id AS varchar(36)), d.Name, d.Value, d.DefaultValue, d.VersionStart, d.VersionEnd, d.Availability, d.[Option], d.Priority, c.EditorType, c.Caption_En, c.Caption_De " +
            "FROM SYS_Jobs_Parameters_Declarations d LEFT JOIN SYS_Jobs_Parameters_Config c ON c.Id = d.Id WHERE d.ContentId = @id ORDER BY d.Priority, d.Name");
        dq.Parameters.AddWithValue("@id", G(id));
        using (var r = dq.ExecuteReader())
            while (r.Read())
            {
                var pa = new MswParam();
                pa.Id = Gid(r, 0); pa.Name = Str(r, 1) ?? ""; pa.EditorType = Int(r, 9);
                pa.Value = ParamValueText(r.IsDBNull(2) ? null : r.GetValue(2), pa.EditorType);
                pa.DefaultValue = ParamDefaultText(r.IsDBNull(3) ? null : r.GetValue(3), pa.EditorType);
                pa.VersionStart = Int(r, 4); pa.VersionEnd = Int(r, 5); pa.Availability = Int(r, 6); pa.Option = Int(r, 7); pa.Priority = Int(r, 8);
                pa.CaptionEn = Str(r, 10) ?? ""; pa.CaptionDe = Str(r, 11) ?? "";
                p.Params.Add(pa); byId[pa.Id] = pa;
            }
        if (p.Params.Count > 0)
        {
            // Auswahlwerte in der Reihenfolge der Tabelle (ORDER BY Id wie die Console sie lud - an den Sicherungen von 7-Zip geprueft)
            var vq = Cmd(c, "SELECT CAST(v.Id AS varchar(36)), CAST(v.ConfigId AS varchar(36)), v.Value, v.Caption_En, v.Caption_De, v.IsDefault, v.Priority FROM SYS_Jobs_Parameters_Config_Values v " +
                "WHERE v.ConfigId IN (SELECT d.Id FROM SYS_Jobs_Parameters_Declarations d WHERE d.ContentId = @id) ORDER BY v.Id");
            vq.Parameters.AddWithValue("@id", G(id));
            using (var r = vq.ExecuteReader())
                while (r.Read())
                {
                    MswParam pa;
                    if (!byId.TryGetValue(Gid(r, 1), out pa)) continue;
                    var mv = new MswValue();
                    mv.Id = Gid(r, 0); mv.Value = Str(r, 2) ?? mv.Id; mv.CaptionEn = Str(r, 3) ?? ""; mv.CaptionDe = Str(r, 4) ?? ""; mv.IsDefault = Int(r, 5) == 1; mv.Priority = Int(r, 6);
                    pa.Values.Add(mv);
                }
        }
        return p;
    }

    static JObject VersionObj(MswVersion v)
    {
        if (v == null) return null;
        var o = new JObject();
        o["version"] = v.Display; o["nr"] = v.Version + "." + v.Revision; o["ring"] = RingText(v.Ring);
        if (v.ItemState >= 0) o["download"] = ItemStateText(v.ItemState);
        if (v.ReleaseDate > 0) o["veroeffentlicht"] = OleText(v.ReleaseDate);
        if (v.Declined) o["abgelehnt"] = true;
        if (v.Ring >= 0 && v.Ring < RingAll && v.Transition == 0) o["uebergang"] = "angehalten (manuell)";
        return o;
    }

    static string StateText(int s) { return s == 1 ? "abgekuendigt (EoL bei Aagon)" : s == 0 ? "aktiv" : "Zustand " + s; }

    public JObject MswRead(string tool, JObject a)
    {
        if (string.IsNullOrEmpty(_readCs)) throw new Exception("db.connectionString fehlt.");
        if (tool == "acmp_msw_packages") return MswPackages(a);
        if (tool == "acmp_msw_package") return MswPackage(a);
        if (tool == "acmp_msw_client") return MswClient(a);
        throw new Exception("Unbekanntes MSW-Tool: " + tool);
    }

    // Gemeinsame Abfrage fuer Paketlisten: Versionen je Ring, Installationen, Updates, Jobs, Kiosk
    const string PkgSql =
        "SELECT CAST(c.Id AS varchar(36)), c.Name, m.Name, c.LicenseKind, c.SoftwareState, CASE WHEN s.Id IS NULL THEN 0 ELSE 1 END, " +
        " la.DisplayVersion, r255.DisplayVersion, r255.Version, r1.DisplayVersion, r2.DisplayVersion, " +
        " (SELECT COUNT(*) FROM SYS_ManagedSoftware_Versions v JOIN SYS_TestRings r ON r.Id = v.RevisionId WHERE v.SoftwareId = c.Id AND r.TestRing < 255 AND v.Version > ISNULL(r255.Version, -1)), " +
        " ISNULL(inst.n, 0), ISNULL(inst.upd, 0), ISNULL(inst.newer, 0), " +
        " (SELECT COUNT(*) FROM SYS_Jobs j WHERE j.ContentId = c.Id AND j.JobKind = 59 AND j.IsManualJob = 0), " +
        " (SELECT COUNT(*) FROM SYS_Jobs j WHERE j.ContentId = c.Id AND j.JobKind = 59 AND j.IsManualJob = 0 AND j.Conditions.exist('//Enabled[text()=\"true\"]') = 1), " +
        " ISNULL(k.n, 0), " +
        " (SELECT COUNT(DISTINCT i.ClientId) FROM SYS_Jobs j JOIN SYS_Jobs_Logs_Items i ON i.JobId = j.Id WHERE j.ContentId = c.Id AND j.JobKind = 59 AND j.IsManualJob = 1 AND i.State IN (0, 1, 2, 5, 6)), " +
        " (SELECT COUNT(*) FROM SYS_ManagedSoftware_Versions v JOIN SYS_TestRings r ON r.Id = v.RevisionId WHERE v.SoftwareId = c.Id) " +
        "FROM SYS_ManagedSoftware_Catalog c " +
        "LEFT JOIN SYS_ManagedSoftware_Manufacturers m ON m.Id = c.ManufacturerId " +
        "LEFT JOIN SYS_ManagedSoftware_Subscriptions s ON s.Id = c.Id " +
        "LEFT JOIN (SELECT ContentId, COUNT(*) AS n FROM VW_SYS_Kiosk_AppItems_ManagedSoftware GROUP BY ContentId) k ON k.ContentId = c.Id " +
        "OUTER APPLY (SELECT TOP 1 v.DisplayVersion FROM SYS_ManagedSoftware_Versions v WHERE v.SoftwareId = c.Id ORDER BY v.Version DESC, v.Revision DESC) la " +
        "OUTER APPLY (SELECT TOP 1 v.DisplayVersion, v.Version, v.VersionId FROM SYS_ManagedSoftware_Versions v JOIN SYS_TestRings r ON r.Id = v.RevisionId " +
        "  WHERE v.SoftwareId = c.Id AND r.TestRing >= 255 AND NOT EXISTS (SELECT 1 FROM SYS_TestRings_DeclineInfo d WHERE d.Id = v.RevisionId) ORDER BY v.Version DESC, v.Revision DESC) r255 " +
        "OUTER APPLY (SELECT TOP 1 v.DisplayVersion FROM SYS_ManagedSoftware_Versions v JOIN SYS_TestRings r ON r.Id = v.RevisionId " +
        "  WHERE v.SoftwareId = c.Id AND r.TestRing >= 1 AND NOT EXISTS (SELECT 1 FROM SYS_TestRings_DeclineInfo d WHERE d.Id = v.RevisionId) ORDER BY v.Version DESC, v.Revision DESC) r1 " +
        "OUTER APPLY (SELECT TOP 1 v.DisplayVersion FROM SYS_ManagedSoftware_Versions v JOIN SYS_TestRings r ON r.Id = v.RevisionId " +
        "  WHERE v.SoftwareId = c.Id AND r.TestRing >= 2 AND NOT EXISTS (SELECT 1 FROM SYS_TestRings_DeclineInfo d WHERE d.Id = v.RevisionId) ORDER BY v.Version DESC, v.Revision DESC) r2 " +
        "OUTER APPLY (SELECT COUNT(*) AS n, SUM(z.upd) AS upd, SUM(z.newer) AS newer FROM (SELECT " +
        // VersionId NULL = installierte Version keiner Katalogversion zugeordnet; ACMP meldet trotzdem "Update moeglich" (DeploymentOption 1).
        // Vorher fielen diese Clients heraus - jetzt wie acmp_msw_package.
        "  CASE WHEN x.DeploymentOption = 1 AND r255.VersionId IS NOT NULL AND (x.VersionId IS NULL OR x.VersionId <> r255.VersionId) THEN 1 ELSE 0 END AS upd, " +
        "  CASE WHEN x.DeploymentOption = 2 THEN 1 ELSE 0 END AS newer " +
        "  FROM CLT_SW_ManagedSoftware x JOIN CLT_CLIENTS_TABLE cl ON cl.CLIENTID = x.ClientId AND cl.IsRetired = 0 WHERE x.SoftwareId = c.Id) z) inst ";

    JObject PkgRow(SqlDataReader r, out int upd, out int jobs, out bool sub)
    {
        var o = new JObject();
        o["id"] = Gid(r, 0); o["name"] = Str(r, 1); o["hersteller"] = Str(r, 2);
        sub = Int(r, 5) == 1;
        o["abonniert"] = sub;
        int state = Int(r, 4);
        o["zustand"] = StateText(state);
        o["lizenzArt"] = Int(r, 3);
        string latest = Str(r, 6), rel = Str(r, 7), r1 = Str(r, 9), r2 = Str(r, 10);
        o["neuesteVersion"] = latest;
        o["freigegeben"] = rel;
        if (r1 != null && r1 != rel) o["testring1"] = r1;
        if (r2 != null && r2 != rel && r2 != r1) o["testring2"] = r2;
        int waiting = Int(r, 11);
        if (waiting > 0) o["neuerNichtFreigegeben"] = waiting;
        int inst = Int(r, 12); upd = Int(r, 13); int newer = Int(r, 14);
        o["installiert"] = inst; o["updateMoeglich"] = upd;
        if (newer > 0) o["neuerAlsFreigabe"] = newer;
        jobs = Int(r, 15); int active = Int(r, 16);
        o["dauerauftraege"] = jobs;
        if (jobs > 0) o["davonAktiv"] = active;
        int kiosk = Int(r, 17); if (kiosk > 0) o["kiosk"] = kiosk;
        int pend = Int(r, 18); if (pend > 0) o["einzelauftraegeOffen"] = pend;
        int nv = Int(r, 19);
        var h = new List<string>();
        if (sub && rel == null) h.Add(nv == 0 ? "noch keine Version heruntergeladen (neu abonniert? naechster MSW-Abgleich des ACMP-Servers)" : "keine freigegebene Version - ein Job installiert nichts");
        if (state == 1) h.Add("von Aagon abgekuendigt - keine neuen Versionen mehr, Nachfolgepaket pruefen");
        if (sub && jobs == 0 && upd > 0) h.Add("kein Dauerauftrag, " + upd + " Client(s) mit freigegebenem Update");
        else if (sub && jobs > 0 && active == 0 && upd > 0) h.Add("Dauerauftraege abgeschaltet, " + upd + " Client(s) mit freigegebenem Update");
        if (!sub && inst > 0) h.Add("nicht abonniert, aber auf " + inst + " Client(s) erkannt");
        if (h.Count > 0) o["hinweise"] = new JArray(h.ToArray());
        return o;
    }

    JObject MswPackages(JObject a)
    {
        bool all = Flag(a, "catalog"), noJob = Flag(a, "onlyWithoutJob"), onlyUpd = Flag(a, "onlyUpdates");
        string name = StrArg(a, "name");
        int top = MswDefaultTop;
        if (a["top"] != null && a["top"].Type != JTokenType.Null) top = Math.Max(1, Math.Min(MswMaxTop, IntArg(a, "top", MswDefaultTop)));
        var list = new List<JObject>(); var keys = new List<long>();
        int subs = 0, subsNoJob = 0, updNoJob = 0;
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            var q = Cmd(c, PkgSql + "WHERE (@all = 1 OR s.Id IS NOT NULL) AND (@name IS NULL OR c.Name LIKE @name OR m.Name LIKE @name)");
            q.Parameters.AddWithValue("@all", all ? 1 : 0);
            q.Parameters.AddWithValue("@name", name == null ? (object)DBNull.Value : "%" + name.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%");
            using (var r = q.ExecuteReader())
                while (r.Read())
                {
                    int upd, jobs; bool sub;
                    JObject o = PkgRow(r, out upd, out jobs, out sub);
                    if (sub) { subs++; if (jobs == 0) { subsNoJob++; if (upd > 0) updNoJob++; } }
                    if (noJob && jobs > 0) continue;
                    if (onlyUpd && upd == 0) continue;
                    list.Add(o);
                    // Sortierung: abonniert zuerst, dann ohne Job mit vielen Updates, dann Name
                    keys.Add((sub ? 0L : 1L) * 100000000L + (jobs == 0 ? 0L : 1L) * 10000000L + (9999999L - Math.Min(upd, 9999999)));
                }
        }
        var idx = new List<int>(); for (int i = 0; i < list.Count; i++) idx.Add(i);
        idx.Sort(delegate(int x, int y) { int k = keys[x].CompareTo(keys[y]); return k != 0 ? k : string.Compare((string)list[x]["name"], (string)list[y]["name"], StringComparison.OrdinalIgnoreCase); });
        var arr = new JArray();
        for (int i = 0; i < idx.Count && i < top; i++) arr.Add(list[idx[i]]);
        var res = new JObject();
        res["gefunden"] = list.Count;
        if (list.Count > top) res["gezeigt"] = top;
        res["abonniert"] = subs; res["abonniertOhneDauerauftrag"] = subsNoJob; res["davonMitUpdates"] = updNoJob;
        res["legende"] = "freigegeben = hoechste Version im Freigabering (255), testring1/2 nur wenn abweichend; updateMoeglich = Clients, deren Version nicht die freigegebene ist und die ACMP als aktualisierbar meldet (CLT_SW_ManagedSoftware.DeploymentOption 1); " +
                         "neuerAlsFreigabe = Clients mit neuerer Version (Selbstaktualisierer wie Teams/Edge). Details je Paket: acmp_msw_package package=<id>. Dauerauftrag anlegen: acmp_msw_job_create.";
        res["pakete"] = arr;
        return res;
    }

    JObject MswPackage(JObject a)
    {
        string arg = StrArg(a, "package");
        int topV = Math.Max(1, Math.Min(50, IntArg(a, "versions", 8)));
        bool names = a["clients"] == null || a["clients"].Type == JTokenType.Null || BoolArg(a, "clients", true);
        var res = new JObject();
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            string id = ResolvePkg(c, arg);
            MswPkg p = LoadPkg(c, id, true);
            var hq = Cmd(c, PkgSql + "WHERE c.Id = @id");
            hq.Parameters.AddWithValue("@id", G(id));
            using (var r = hq.ExecuteReader()) { if (r.Read()) { int u, j; bool s; res["paket"] = PkgRow(r, out u, out j, out s); } }
            MswVersion rel = p.ReleasedFor(RingAll);
            var va = new JArray();
            for (int i = 0; i < p.Versions.Count && i < topV; i++) va.Add(VersionObj(p.Versions[i]));
            res["versionen"] = va;
            if (p.Versions.Count > topV) res["weitereVersionen"] = p.Versions.Count - topV;

            // Verteilung in der Flotte (CLT_SW_ManagedSoftware, ohne ausgemusterte Clients)
            var groups = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase); var order = new List<string>();
            var upd = new List<string>(); var newer = new List<string>();
            var dq = Cmd(c, "SELECT cl.COMPUTERNAME, x.VersionString, x.DeploymentOption, CAST(x.VersionId AS varchar(36)) FROM CLT_SW_ManagedSoftware x " +
                "JOIN CLT_CLIENTS_TABLE cl ON cl.CLIENTID = x.ClientId AND cl.IsRetired = 0 WHERE x.SoftwareId = @id ORDER BY cl.COMPUTERNAME");
            dq.Parameters.AddWithValue("@id", G(id));
            int total = 0;
            using (var r = dq.ExecuteReader())
                while (r.Read())
                {
                    total++;
                    string cn = Str(r, 0) ?? "?", vs = Str(r, 1) ?? "(leer)", vid = Gid(r, 3); int opt = Int(r, 2);
                    JObject g;
                    if (!groups.TryGetValue(vs, out g))
                    {
                        g = new JObject(); g["version"] = vs; g["clients"] = 0; g["namen"] = new JArray();
                        groups[vs] = g; order.Add(vs);
                    }
                    g["clients"] = (int)g["clients"] + 1;
                    if (rel != null && vid != null && string.Equals(vid, rel.VersionId, StringComparison.OrdinalIgnoreCase)) g["istFreigegeben"] = true;
                    if (names && ((JArray)g["namen"]).Count < 40) ((JArray)g["namen"]).Add(cn);
                    if (opt == 1 && rel != null && !string.Equals(vid, rel.VersionId, StringComparison.OrdinalIgnoreCase)) upd.Add(cn);
                    if (opt == 2) newer.Add(cn);
                }
            order.Sort(delegate(string x, string y) { int k = ((int)groups[y]["clients"]).CompareTo((int)groups[x]["clients"]); return k != 0 ? k : string.CompareOrdinal(x, y); });
            var dist = new JArray();
            foreach (string k in order) { JObject g = groups[k]; if (!names) g.Remove("namen"); dist.Add(g); }
            res["installiert"] = total;
            res["verteilung"] = dist;
            res["updateMoeglich"] = upd.Count;
            if (upd.Count > 0 && names) res["clientsMitUpdate"] = new JArray(upd.ToArray());
            if (newer.Count > 0) res["neuerAlsFreigabe"] = new JArray(newer.ToArray());

            // Jobs mit dem Paket: Dauerauftraege, Einzelauftraege mit offenen Eintraegen, Job-Sammlungen
            var jobs = new JArray(); var manual = new JArray(); var coll = new JArray();
            var jq = Cmd(c, "SELECT CAST(j.Id AS varchar(36)), j.Name, j.IsManualJob, j.Origin, s.Path, CAST(j.Conditions AS nvarchar(max)), " +
                "(SELECT COUNT(*) FROM CLT_CONTAINER_ITEMS ci JOIN CLT_CLIENTS_TABLE cl ON cl.CLIENTID = ci.LinkID WHERE ci.CONTAINERID = j.ContainerId AND cl.IsRetired = 0), " +
                "(SELECT COUNT(*) FROM SYS_Jobs_Logs_Items i WHERE i.JobId = j.Id AND i.State IN (0, 1, 2, 5, 6)), " +
                "(SELECT COUNT(*) FROM SYS_Jobs_Logs_Items i WHERE i.JobId = j.Id AND i.LastActivity >= CAST(GETDATE() AS float) + 2 - 30), " +
                "(SELECT COUNT(*) FROM SYS_Jobs_Logs_Items i WHERE i.JobId = j.Id AND i.LastActivity >= CAST(GETDATE() AS float) + 2 - 30 AND i.Result = 2), " +
                "(SELECT TOP 1 t.Name FROM SYS_Templates_Jobs tj JOIN SYS_Templates t ON t.Id = tj.TemplateId WHERE tj.JobId = j.Id), " +
                "(SELECT TOP 1 CAST(t.IsReferenceCounted AS int) FROM SYS_Templates_Jobs tj JOIN SYS_Templates t ON t.Id = tj.TemplateId WHERE tj.JobId = j.Id), j.CreationDate, " +
                "(SELECT TOP 1 l.CreatorName FROM SYS_Jobs_Logs l WHERE l.JobId = j.Id) " +
                "FROM SYS_Jobs j LEFT JOIN CLT_CONTAINER_STRUCTURE s ON s.ContainerID = j.ContainerId WHERE j.JobKind = 59 AND j.ContentId = @id ORDER BY j.IsManualJob, s.Path, j.CreationDate DESC");
            jq.Parameters.AddWithValue("@id", G(id));
            using (var r = jq.ExecuteReader())
                while (r.Read())
                {
                    var o = new JObject();
                    o["jobId"] = Gid(r, 0); o["name"] = Str(r, 1);
                    bool isManual = Int(r, 2) == 1; int origin = Int(r, 3);
                    string tname = Str(r, 10); int rc = Int(r, 11);
                    if (!isManual)
                    {
                        CondInfo ci = ParseConditions(Str(r, 5));
                        o["container"] = Str(r, 4); o["mitglieder"] = Int(r, 6); o["startbedingung"] = ci.Text; o["aktiv"] = ci.Active;
                        o["laeufe30"] = Int(r, 8); o["fehler30"] = Int(r, 9);
                        jobs.Add(o);
                    }
                    else if (rc == 1 && string.IsNullOrEmpty(tname))
                    {
                        int open = Int(r, 7);
                        if (open == 0) continue;
                        o["offeneEintraege"] = open; o["angelegt"] = OleText(Dbl(r, 12)); o["von"] = Str(r, 13);
                        manual.Add(o);
                    }
                    else { o["sammlung"] = tname ?? "(ohne Vorlage)"; coll.Add(o); }
                }
            res["dauerauftraege"] = jobs;
            if (manual.Count > 0) res["einzelauftraegeOffen"] = manual;
            if (coll.Count > 0) res["inJobSammlungen"] = coll;

            // Parameter (fuer acmp_msw_job_create parameters={...})
            var pa = new JArray();
            foreach (MswParam x in p.Params)
            {
                var o = new JObject();
                o["name"] = x.Name; o["text"] = x.CaptionDe; o["typ"] = x.EditorType == 2 ? "ja/nein" : x.EditorType == 1 ? "Auswahl" : "Text";
                o["wert"] = x.EditorType == 1 ? CaptionOf(x, x.Value) : x.Value;
                if (x.DefaultValue != (x.EditorType == 2 ? x.Value.ToLowerInvariant() : x.Value)) o["standardAagon"] = x.EditorType == 1 ? CaptionOf(x, x.DefaultValue) : x.DefaultValue;
                if (x.EditorType == 1) { var vals = new JArray(); foreach (MswValue v in x.Values) vals.Add(v.CaptionDe); o["auswahl"] = vals; }
                if (x.VersionStart > 1) o["abVersion"] = DisplayVersionFrom(p.VersionMap(), x.VersionStart, false);
                pa.Add(o);
            }
            res["parameter"] = pa;
            res["hinweisParameter"] = "wert = Vorbelegung des Pakets (Console: Managed Software, Parameter); ein neuer Job uebernimmt sie, abweichend per acmp_msw_job_create parameters={\"Name\": Wert}.";
        }
        return res;
    }

    static string CaptionOf(MswParam p, string value)
    {
        foreach (MswValue v in p.Values) if (string.Equals(v.Value, value, StringComparison.OrdinalIgnoreCase)) return v.CaptionDe + " " + v.Value;
        return value;
    }

    JObject MswClient(JObject a)
    {
        string arg = StrArg(a, "client") ?? StrArg(a, "clientId") ?? StrArg(a, "computerName");
        if (arg == null) throw new Exception("client fehlt (Computername oder ClientId).");
        bool all = Flag(a, "all");
        var res = new JObject();
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            string[] cl = FindClient(c, arg);
            if (cl == null) throw new Exception("Client nicht gefunden: " + arg);
            res["client"] = cl[1]; res["clientId"] = cl[0];
            var iq = Cmd(c, "SELECT IsManaged, LastUpdate, CASE WHEN EXISTS (SELECT 1 FROM CLT_UnixClients u WHERE u.ClientId = c.CLIENTID) THEN 1 ELSE 0 END FROM CLT_CLIENTS_TABLE c WHERE c.CLIENTID = @cl");
            iq.Parameters.AddWithValue("@cl", G(cl[0]));
            using (var r = iq.ExecuteReader()) if (r.Read()) { res["verwaltet"] = Int(r, 0) == 1; res["letzterKontakt"] = OleText(Dbl(r, 1)); if (Int(r, 2) == 1) res["unix"] = true; }
            var pk = new JArray(); int nUpd = 0, nNew = 0, nCur = 0;
            var q = Cmd(c, "SELECT c.Name, CAST(x.SoftwareId AS varchar(36)), x.VersionString, x.DeploymentOption, CAST(x.VersionId AS varchar(36)), CASE WHEN s.Id IS NULL THEN 0 ELSE 1 END, c.SoftwareState, " +
                "r255.DisplayVersion, CAST(r255.VersionId AS varchar(36)), (SELECT COUNT(*) FROM SYS_Jobs j JOIN CLT_CONTAINER_ITEMS ci ON ci.CONTAINERID = j.ContainerId AND ci.LinkID = x.ClientId " +
                "  WHERE j.JobKind = 59 AND j.IsManualJob = 0 AND j.ContentId = x.SoftwareId) " +
                "FROM CLT_SW_ManagedSoftware x JOIN SYS_ManagedSoftware_Catalog c ON c.Id = x.SoftwareId LEFT JOIN SYS_ManagedSoftware_Subscriptions s ON s.Id = c.Id " +
                "OUTER APPLY (SELECT TOP 1 v.DisplayVersion, v.VersionId FROM SYS_ManagedSoftware_Versions v JOIN SYS_TestRings r ON r.Id = v.RevisionId WHERE v.SoftwareId = x.SoftwareId AND r.TestRing >= 255 " +
                "  AND NOT EXISTS (SELECT 1 FROM SYS_TestRings_DeclineInfo d WHERE d.Id = v.RevisionId) ORDER BY v.Version DESC, v.Revision DESC) r255 " +
                "WHERE x.ClientId = @cl ORDER BY c.Name");
            q.Parameters.AddWithValue("@cl", G(cl[0]));
            using (var r = q.ExecuteReader())
                while (r.Read())
                {
                    int opt = Int(r, 3); string vid = Gid(r, 4), relId = Gid(r, 8);
                    string st = opt == 2 ? "neuer als die Freigabe" : (opt == 1 && relId != null && !string.Equals(vid, relId, StringComparison.OrdinalIgnoreCase)) ? "Update moeglich"
                              : (relId != null && string.Equals(vid, relId, StringComparison.OrdinalIgnoreCase)) ? "aktuell" : DeploymentText(opt);
                    if (st == "Update moeglich") nUpd++; else if (st == "neuer als die Freigabe") nNew++; else if (st == "aktuell") nCur++;
                    if (!all && st == "aktuell") continue;
                    var o = new JObject();
                    o["paket"] = Str(r, 0); o["paketId"] = Gid(r, 1); o["installiert"] = Str(r, 2); o["freigegeben"] = Str(r, 7); o["stand"] = st;
                    if (Int(r, 5) == 0) o["abonniert"] = false;
                    if (Int(r, 6) == 1) o["abgekuendigt"] = true;
                    o["dauerauftraegeFuerDiesenClient"] = Int(r, 9);
                    pk.Add(o);
                }
            res["aktuell"] = nCur; res["updateMoeglich"] = nUpd; res["neuerAlsFreigabe"] = nNew;
            res["pakete"] = pk;
            if (!all && nCur > 0) res["hinweis"] = nCur + " aktuelle Pakete ausgeblendet (all=true zeigt alle).";
            // MSW-Dauerauftraege an Containern des Clients
            var jobs = new JArray();
            var jq = Cmd(c, "SELECT CAST(j.Id AS varchar(36)), j.Name, s.Path, CAST(j.Conditions AS nvarchar(max)) FROM SYS_Jobs j JOIN CLT_CONTAINER_ITEMS ci ON ci.CONTAINERID = j.ContainerId AND ci.LinkID = @cl " +
                "LEFT JOIN CLT_CONTAINER_STRUCTURE s ON s.ContainerID = j.ContainerId WHERE j.JobKind = 59 AND j.IsManualJob = 0 ORDER BY j.Name");
            jq.Parameters.AddWithValue("@cl", G(cl[0]));
            using (var r = jq.ExecuteReader())
                while (r.Read()) { var o = new JObject(); o["jobId"] = Gid(r, 0); o["name"] = Str(r, 1); o["container"] = Str(r, 2); o["startbedingung"] = ParseConditions(Str(r, 3)).Text; jobs.Add(o); }
            res["dauerauftraege"] = jobs;
            // letzte MSW-Laeufe auf dem Client (Job-Monitor), mit Meldung des Pakets
            var runs = new JArray();
            var rq = Cmd(c, "SELECT TOP 25 l.JobName, CAST(l.JobId AS varchar(36)), l.Origin, i.State, i.Result, i.LastActivity, LEFT(i.StateMsg, 300), " +
                "(SELECT TOP 1 sb.MessageCode FROM SYS_Jobs_Logs_SubItems sb WHERE sb.ParentExecId = i.ExecId ORDER BY sb.StartDate DESC), " +
                "(SELECT TOP 1 LEFT(COALESCE(NULLIF(sb.NativeErrorMessage, ''), sb.NativeMessage), 300) FROM SYS_Jobs_Logs_SubItems sb WHERE sb.ParentExecId = i.ExecId ORDER BY sb.StartDate DESC) " +
                "FROM SYS_Jobs_Logs_Items i JOIN SYS_Jobs_Logs l ON l.JobId = i.JobId WHERE i.ClientId = @cl AND l.JobKind = 59 ORDER BY i.LastActivity DESC");
            rq.Parameters.AddWithValue("@cl", G(cl[0]));
            using (var r = rq.ExecuteReader())
                while (r.Read())
                {
                    var o = new JObject();
                    o["job"] = Str(r, 0); o["jobId"] = Gid(r, 1); o["art"] = Int(r, 2) == 1 ? "Dauerauftrag" : "Einzelauftrag";
                    o["status"] = ExecStateText(Int(r, 3)); o["ergebnis"] = ResultText(Int(r, 4)); o["zeit"] = OleText(Dbl(r, 5));
                    string sm = Str(r, 6); if (!string.IsNullOrEmpty(sm)) o["meldung"] = sm;
                    if (!r.IsDBNull(7)) o["meldungsCode"] = Int(r, 7);
                    string nm = Str(r, 8); if (!string.IsNullOrEmpty(nm)) o["paketMeldung"] = nm;
                    runs.Add(o);
                }
            res["letzteLaeufe"] = runs;
        }
        return res;
    }

    // ================= Schreiben: gemeinsame Bausteine =================

    string LatestServerId(SqlConnection c)
    {
        var q = Cmd(c, "SELECT TOP 1 CAST(ServerId AS varchar(36)) FROM SYS_Jobs WHERE ServerId IS NOT NULL ORDER BY CreationDate DESC");
        object o = q.ExecuteScalar();
        return o == null || o is DBNull ? null : GuidBraces((string)o);
    }

    string ConfigLogOption(SqlConnection c)
    {
        try
        {
            var q = Cmd(c, "SELECT TOP 1 CAST(Config AS xml).value('(/TAcmpConfig/ManagedSoftwareConfig/TManagedSoftwareConfig/DefaultLogStorageOption)[1]', 'nvarchar(60)') FROM SYS_Config");
            object o = q.ExecuteScalar();
            string s = o == null || o is DBNull ? null : (string)o;
            return Array.IndexOf(LogOptions, s) >= 0 ? s : DefaultLogOption;
        }
        catch { return DefaultLogOption; }
    }

    // Parameter-Werte: Vorbelegung des Pakets, abweichend aus parameters={Name: Wert}
    static Dictionary<string, string> ParamValues(MswPkg p, JObject a, List<string> changed)
    {
        var vals = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (MswParam x in p.Params) vals[x.Name] = x.Value;
        JToken t = a["parameters"];
        if (t != null && t.Type == JTokenType.String && ((string)t).Trim().StartsWith("{")) t = JToken.Parse((string)t);
        if (t == null || t.Type == JTokenType.Null) return vals;
        JObject po = t as JObject;
        if (po == null) throw new Exception("parameters muss ein Objekt sein, z.B. {\"DesktopShortcut1\": false}.");
        foreach (JProperty pr in po.Properties())
        {
            MswParam hit = null;
            foreach (MswParam x in p.Params) if (string.Equals(x.Name, pr.Name, StringComparison.OrdinalIgnoreCase)) { hit = x; break; }
            if (hit == null)
            {
                var n = new List<string>(); foreach (MswParam x in p.Params) n.Add(x.Name);
                throw new Exception("Parameter '" + pr.Name + "' gibt es bei " + p.Name + " nicht. Vorhanden: " + (n.Count > 0 ? string.Join(", ", n.ToArray()) : "keine") + ".");
            }
            string v = ParamInput(hit, pr.Value);
            if (v != vals[hit.Name] && changed != null) changed.Add(hit.Name + " = " + (hit.EditorType == 1 ? CaptionOf(hit, v) : v) + " (Paket: " + (hit.EditorType == 1 ? CaptionOf(hit, vals[hit.Name]) : vals[hit.Name]) + ")");
            vals[hit.Name] = v;
        }
        return vals;
    }

    // ================= acmp_msw_job_create =================

    JObject PlanMswJob(JObject a)
    {
        string sa = SoftwareActionOf(StrArg(a, "action"), false);
        if (sa == null) throw new Exception("action fehlt oder ist unbekannt: install (installieren oder aktualisieren - installiert auch auf Mitgliedern ohne das Paket) oder update (nur aktualisieren, wo installiert).");
        string condArg = StrArg(a, "condition");
        string kind = condArg != null ? ConditionKind(condArg) : null;
        if (condArg != null && kind == null) throw new Exception("condition '" + condArg + "' unbekannt. Erlaubt: once, startup, daily, hourly, maintenance (wie acmp_job_set_condition).");
        int every = IntArg(a, "every", 1);
        bool ignore = BoolArg(a, "ignoreMaintenanceWindows", false);
        bool hasEnabled = a["enabled"] != null && a["enabled"].Type != JTokenType.Null;
        bool enabled = BoolArg(a, "enabled", true);
        var plan = new JObject(); var pv = new JArray(); var warn = new List<string>(); var changed = new List<string>();
        MswPkg p; JObject cont;
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            p = LoadPkg(c, ResolvePkg(c, StrArg(a, "package")), true);
            if (!p.Subscribed) throw new Exception(p.Name + " ist nicht abonniert - erst acmp_msw_subscribe (bzw. in der Console abonnieren), danach laedt der ACMP-Server die Versionen.");
            if (p.State == 1) warn.Add(p.Name + " ist von Aagon abgekuendigt (EoL) - es kommen keine neuen Versionen mehr; Nachfolgepaket pruefen.");
            string cid = ResolveContainer(c, a, false);
            cont = LookupContainer(c, cid);
            if (cont == null) throw new Exception("Container " + cid + " gibt es nicht - nichts geplant.");
            if ((int)cont["osFamily"] != 0) throw new Exception("Container " + cont["path"] + " ist kein Windows-Container (" + OsFamilyText((int)cont["osFamily"]) + ") - Managed Software gibt es nur fuer Windows.");

            // vorhandene Dauerauftraege des Containers
            var ex = new List<JObject>();
            var eq = Cmd(c, "SELECT CAST(j.Id AS varchar(36)), j.Name, j.JobKind, CAST(j.ContentId AS varchar(36)), CAST(j.Conditions AS nvarchar(max)), ISNULL(j.ConditionCheckSum, ''), " +
                "(SELECT TOP 1 l.TestRing FROM SYS_Jobs_Logs l WHERE l.JobId = j.Id), (SELECT TOP 1 CAST(tj.TemplateId AS varchar(36)) FROM SYS_Templates_Jobs tj WHERE tj.JobId = j.Id), " +
                "(SELECT COUNT(*) FROM SYS_Templates_Jobs tj WHERE tj.JobId = j.Id) FROM SYS_Jobs j WHERE j.ContainerId = @c AND j.IsManualJob = 0 ORDER BY j.CreationDate");
            eq.Parameters.AddWithValue("@c", G(cid));
            using (var r = eq.ExecuteReader())
                while (r.Read())
                {
                    var o = new JObject();
                    o["id"] = Gid(r, 0); o["name"] = Str(r, 1); o["kind"] = Int(r, 2); o["contentId"] = Gid(r, 3); o["conditions"] = Str(r, 4); o["checksum"] = Str(r, 5);
                    o["ring"] = r.IsDBNull(6) ? -1 : Int(r, 6); o["templateId"] = Gid(r, 7); o["links"] = Int(r, 8);
                    ex.Add(o);
                }
            foreach (JObject o in ex)
                if ((int)o["kind"] == MswKind && string.Equals((string)o["contentId"], p.Id, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Am Container " + cont["path"] + " gibt es schon einen Dauerauftrag fuer " + p.Name + " (" + o["id"] + ") - nichts geplant. Aendern: acmp_job_set_condition / acmp_job_set_enabled.");
            if (ex.Count + 1 > MaxContainerJobs) throw new Exception("Container " + cont["path"] + " hat schon " + ex.Count + " Dauerauftraege (hoechstens " + MaxContainerJobs + ") - bitte in der Console.");

            // Verteilungsring: wie die vorhandenen Jobs des Containers (die Console setzt ihn je Container), sonst Freigabering
            int ring = RingAll; var rings = new HashSet<int>();
            foreach (JObject o in ex) if ((int)o["ring"] >= 0) rings.Add((int)o["ring"]);
            if (rings.Count > 1) throw new Exception("Die Jobs von " + cont["path"] + " haben verschiedene Verteilungsringe - bitte in der Console klaeren.");
            foreach (int x in rings) ring = x;
            MswVersion rel = p.ReleasedFor(ring);
            if (rel == null) throw new Exception(p.Name + " hat keine Version, die fuer " + RingText(ring) + " freigegeben ist - der Job wuerde nichts installieren. Stand: acmp_msw_package.");

            // Startbedingung: die Console fuehrt sie je Container
            string condXml, cs, csSrc, condId = null;
            if (ex.Count > 0)
            {
                var keys = new HashSet<string>();
                foreach (JObject o in ex) keys.Add(ConditionKey((string)o["conditions"]) ?? "?");
                if (keys.Count > 1) throw new Exception("Die Dauerauftraege von " + cont["path"] + " haben verschiedene Startbedingungen - bitte zuerst angleichen (acmp_job_set_condition).");
                condXml = (string)ex[0]["conditions"]; cs = (string)ex[0]["checksum"];
                csSrc = "wie der vorhandene Job \"" + ex[0]["name"] + "\" des Containers";
                CondInfo have = ParseConditions(condXml);
                if (kind != null)
                {
                    CheckCondition(kind, every, ignore);
                    string haveText = have.Readable ? ParseConditionsConsole(condXml) : "?";
                    if (!have.Active || !SameCondition(have, haveText, kind, every, ignore))   // AcmpLuecken.cs: auch aeltere Schreibweise beim Agentenstart
                        throw new Exception("Container " + cont["path"] + " hat schon die Startbedingung '" + have.Text + "' [Console: " + haveText + "]; die Console fuehrt sie je Container. " +
                                            "Ohne condition aufrufen (der neue Job uebernimmt sie) oder danach acmp_job_set_condition (gilt dann fuer alle Jobs des Containers).");
                }
                if (hasEnabled && enabled != have.Active)
                    throw new Exception("Die Startbedingung von " + cont["path"] + " ist " + (have.Active ? "an" : "aus") + " und gilt je Container - enabled weglassen oder danach acmp_job_set_enabled.");
                if (!have.Active) warn.Add("Die Startbedingung des Containers ist AUS ('" + have.Text + "') - der neue Job laeuft erst, wenn sie eingeschaltet wird (acmp_job_set_enabled bzw. acmp_job_set_condition).");
            }
            else
            {
                if (kind == null) throw new Exception("condition fehlt (der Container hat noch keine Dauerauftraege): once (sobald wie moeglich, einmalig je Client), startup, daily, hourly (mit every) oder maintenance (in jedem Wartungsfenster), optional ignoreMaintenanceWindows.");
                CheckCondition(kind, every, ignore);
                condId = NewId();
                condXml = ConditionXml(null, kind, every, ignore, condId);
                if (!enabled) condXml = DisableXml(condXml);
                cs = ChecksumFor(condXml, LoadChecksumRows(c), OwnChecksums(), out csSrc);
            }
            string condText = ParseConditionsConsole(condXml);
            CondInfo ci = ParseConditions(condXml);

            // Job-Vorlage des Containers
            string tplId = null; byte[] kindsOld = null; int tplCount = 0;
            var tq = Cmd(c, "SELECT CAST(t.Id AS varchar(36)), t.JobKinds, t.OsFamily FROM SYS_Containers_Templates ct JOIN SYS_Templates t ON t.Id = ct.TemplateId " +
                "WHERE ct.ContainerId = @c AND t.Name LIKE '" + ContainerTemplatePrefix + "%' AND ISNULL(t.IsReferenceCounted, 0) = 0 AND t.FolderId IS NULL");
            tq.Parameters.AddWithValue("@c", G(cid));
            using (var r = tq.ExecuteReader())
                while (r.Read()) { tplCount++; tplId = Gid(r, 0); kindsOld = r.IsDBNull(1) ? null : (byte[])r.GetValue(1); if (Int(r, 2) != 0) throw new Exception("Die Job-Vorlage von " + cont["path"] + " ist nicht fuer Windows - bitte in der Console."); }
            if (tplCount > 1) throw new Exception("Container " + cont["path"] + " hat " + tplCount + " Job-Vorlagen - bitte in der Console.");
            foreach (JObject o in ex)
                if ((int)o["links"] != 1 || tplId == null || !string.Equals((string)o["templateId"], tplId, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Job \"" + o["name"] + "\" haengt nicht (nur) an der Job-Vorlage des Containers - bitte in der Console.");
            bool tplNew = tplId == null;
            if (tplNew) tplId = NewId();
            byte[] kindsNew = KindsMask(kindsOld, MswKind);

            // Parameter, Protokoll, JobAction
            Dictionary<string, string> vals = ParamValues(p, a, changed);
            string log = LogOptionOf(StrArg(a, "log"), ConfigLogOption(c));
            string jobId = NewId();
            string jobAction = MswJobActionXml(p.Id, p.Name, sa, log, ring, p.Params, vals, p.VersionMap());

            // Wirkung auf die Mitglieder
            int mem = 0, cur = 0, updN = 0, newerN = 0, missing = 0, other = 0; var missingNames = new List<string>(); var updNames = new List<string>();
            var mq = Cmd(c, "SELECT cl.COMPUTERNAME, x.VersionString, x.DeploymentOption, CAST(x.VersionId AS varchar(36)), " +
                "CASE WHEN EXISTS (SELECT 1 FROM CLT_UnixClients u WHERE u.ClientId = cl.CLIENTID) OR cl.COMPUTERNAME LIKE '[[]NETWORK BOOT]%' THEN 1 ELSE 0 END, CAST(cl.CLIENTID AS varchar(36)) " +
                "FROM CLT_CONTAINER_ITEMS ci JOIN CLT_CLIENTS_TABLE cl ON cl.CLIENTID = ci.LinkID AND cl.IsRetired = 0 " +
                "LEFT JOIN CLT_SW_ManagedSoftware x ON x.ClientId = cl.CLIENTID AND x.SoftwareId = @sw WHERE ci.CONTAINERID = @c ORDER BY cl.COMPUTERNAME");
            mq.Parameters.AddWithValue("@sw", G(p.Id)); mq.Parameters.AddWithValue("@c", G(cid));
            var memberIds = new List<string>();
            using (var r = mq.ExecuteReader())
                while (r.Read())
                {
                    mem++; memberIds.Add(Str(r, 5));
                    if (Int(r, 4) == 1) { other++; continue; }
                    if (r.IsDBNull(1) && r.IsDBNull(3)) { missing++; if (missingNames.Count < 30) missingNames.Add(Str(r, 0)); continue; }
                    string vid = Gid(r, 3); int opt = Int(r, 2);
                    if (string.Equals(vid, rel.VersionId, StringComparison.OrdinalIgnoreCase)) cur++;
                    else if (opt == 2) newerN++;
                    else { updN++; if (updNames.Count < 30) updNames.Add(Str(r, 0) + " " + Str(r, 1)); }
                }
            // gleiches Paket schon an anderen Containern (ueberlappende Mitglieder)
            var others = new List<string>();
            var oq = Cmd(c, "SELECT s.Path, (SELECT COUNT(*) FROM CLT_CONTAINER_ITEMS a1 JOIN CLT_CONTAINER_ITEMS b1 ON b1.LinkID = a1.LinkID AND b1.CONTAINERID = @c WHERE a1.CONTAINERID = j.ContainerId), " +
                "CAST(j.Conditions AS nvarchar(max)) FROM SYS_Jobs j LEFT JOIN CLT_CONTAINER_STRUCTURE s ON s.ContainerID = j.ContainerId WHERE j.JobKind = 59 AND j.IsManualJob = 0 AND j.ContentId = @sw");
            oq.Parameters.AddWithValue("@sw", G(p.Id)); oq.Parameters.AddWithValue("@c", G(cid));
            using (var r = oq.ExecuteReader()) while (r.Read()) others.Add((Str(r, 0) ?? "?") + " (" + Int(r, 1) + " gemeinsame Mitglieder, " + ParseConditions(Str(r, 2)).Text + ")");
            // geschuetzte Clients (ACMP-/MCP-Server) unter den Mitgliedern: deutlicher Hinweis (Dauerauftraege an Server-Containern gibt es bewusst)
            if (GuardTargets != null && memberIds.Count > 0)
            {
                try { GuardTargets(string.Join(" ", memberIds.ToArray())); }
                catch (Exception gx) { warn.Add("ACHTUNG: Unter den Mitgliedern ist ein geschuetzter Client (ACMP-/MCP-Server) - der Job installiert auch dort: " + gx.Message.Split('(')[0].Trim()); }
            }
            string srv = LatestServerId(c);

            plan["action"] = "mswjob";
            plan["jobId"] = jobId; plan["name"] = p.Name; plan["contentId"] = p.Id; plan["containerId"] = cont["id"]; plan["path"] = cont["path"];
            plan["softwareAction"] = sa; plan["logOption"] = log; plan["testRing"] = ring; plan["releasedVersion"] = rel.Display;
            plan["templateId"] = tplId; plan["templateNew"] = tplNew; plan["templateName"] = ContainerTemplatePrefix + ": " + cont["id"];
            plan["kindsOld"] = HexOf(kindsOld); plan["kindsNew"] = HexOf(kindsNew);
            plan["templateJobId"] = NewId(); plan["containerTemplateId"] = NewId();
            plan["conditionsXml"] = condXml; plan["checksum"] = cs; plan["checksumQuelle"] = csSrc; plan["conditionText"] = condText;
            plan["jobActionXml"] = jobAction; plan["serverId"] = srv;
            var exIds = new JArray(); foreach (JObject o in ex) exIds.Add(o["id"]);
            plan["existingJobIds"] = exIds;

            pv.Add("MSW-DAUERAUFTRAG ANLEGEN wie die Console: " + p.Name + " am Container " + cont["path"] + " (" + mem + " Mitglieder), Aktion: " + SoftwareActionText(sa) + ".");
            pv.Add("   Version: " + rel.Display + " (" + RingText(ring) + ")" + (p.Latest != null && p.Latest.Display != rel.Display ? "; neueste verfuegbare " + p.Latest.Display + " (" + RingText(p.Latest.Ring) + ")" : "") + ".");
            pv.Add("   Startbedingung: " + ci.Text + " [Console: " + condText + "], Pruefsumme " + Cs8(cs) + " " + csSrc + ".");
            pv.Add("   Job-Vorlage: " + (tplNew ? "neu ('" + plan["templateName"] + "', mit dem Container verknuepft)" : "vorhandene des Containers" + (HexOf(kindsOld) != HexOf(kindsNew) ? ", Art Managed Software wird ergaenzt" : "")) +
                   (ex.Count > 0 ? "; dort schon: " + ex.Count + " Job(s)" : "") + ".");
            pv.Add("   Skript-Protokoll: " + log + "; Parameter: " + p.Params.Count + (changed.Count > 0 ? ", abweichend: " + string.Join("; ", changed.ToArray()) : " wie im Paket vorbelegt") + ".");
            pv.Add("Wirkung (Inventar): " + cur + " schon aktuell, " + updN + " mit aelterer Version" + (updNames.Count > 0 ? " (" + string.Join(", ", updNames.ToArray()) + (updN > updNames.Count ? ", ..." : "") + ")" : "") +
                   ", " + newerN + " neuer als die Freigabe, " + missing + " ohne das Paket" + (other > 0 ? ", " + other + " Nicht-Windows/[NETWORK BOOT]" : "") + ".");
            if (sa == "saInstallOrUpdate" && missing > 0)
                warn.Add("ACHTUNG: Aktion install installiert " + p.Name + " NEU auf " + missing + " Mitglied(er) ohne das Paket: " + string.Join(", ", missingNames.ToArray()) + (missing > missingNames.Count ? ", ..." : "") + ". Nur aktualisieren: action=update.");
            if (sa == "saUpdateOnly" && updN == 0) warn.Add("Kein Mitglied hat eine aeltere Version - der Job hat derzeit nichts zu tun.");
            if (mem == 0) warn.Add("Der Container hat keine Mitglieder - der Job laeuft erst, wenn Clients dazukommen.");
            if (others.Count > 0) warn.Add("Dasselbe Paket hat schon Dauerauftraege an: " + string.Join("; ", others.ToArray()) + ".");
            if (srv == null) warn.Add("ServerId nicht ermittelbar - der Job wird ohne ServerId angelegt.");
        }
        foreach (string w in warn) pv.Add("Hinweis: " + w);
        pv.Add("Ablauf: Sicherung, dann eine Transaktion (Job-Vorlage, SYS_Jobs, Job-Monitor-Eintrag, Verknuepfung). Die Agents holen den Job mit ihrer naechsten Job-Liste. " +
               "Zurueck: acmp_job_restore mit der Sicherung (loescht den neuen Job und eine neu angelegte, leere Vorlage) oder acmp_job_delete.");
        plan["vorschau"] = pv;
        return plan;
    }

    JObject ExecMswJob(JObject plan, string requester)
    {
        string id = (string)plan["jobId"], cid = (string)plan["containerId"], tpl = (string)plan["templateId"];
        bool tplNew = (bool)plan["templateNew"];
        string label = "MSW-Job \"" + plan["name"] + "\" an " + plan["path"];
        var b = new JObject();
        b["kind"] = "mswjob"; b["tool"] = "acmp_msw_job_create"; b["von"] = requester; b["zeit"] = DateTime.Now.ToString("s"); b["jobId"] = id; b["name"] = plan["name"];
        b["target"] = plan["path"]; b["containerId"] = cid; b["contentId"] = plan["contentId"]; b["templateId"] = tpl; b["templateNew"] = tplNew;
        b["templateJobId"] = plan["templateJobId"]; b["containerTemplateId"] = plan["containerTemplateId"]; b["kindsOld"] = plan["kindsOld"]; b["kindsNew"] = plan["kindsNew"];
        var after = new JObject(); after["checksum"] = plan["checksum"]; after["checksumQuelle"] = plan["checksumQuelle"]; after["xml"] = plan["conditionsXml"]; after["logText"] = plan["conditionText"];
        b["after"] = after; b["jobAction"] = plan["jobActionXml"]; b["testRing"] = plan["testRing"]; b["softwareAction"] = plan["softwareAction"];
        string bf = Path.GetFileName(SaveBackup(b, "mswjob", id));
        double now = DateTime.Now.ToOADate();
        using (var wc = new SqlConnection(_writeCs))
        {
            wc.Open();
            using (SqlTransaction tx = wc.BeginTransaction())
            {
                var k = TxCmd(wc, tx, "SELECT COUNT(*) FROM CLT_CONTAINER_STRUCTURE WHERE ContainerID = @c");
                k.Parameters.AddWithValue("@c", G(cid));
                if (Convert.ToInt32(k.ExecuteScalar()) != 1) throw new Exception(label + ": Container gibt es nicht mehr - zurueckgerollt.");
                // Stand pruefen: gleiche Dauerauftraege wie geplant, keiner mit diesem Paket
                var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase); bool dup = false;
                var jq = TxCmd(wc, tx, "SELECT CAST(Id AS varchar(36)), CAST(ContentId AS varchar(36)), JobKind FROM SYS_Jobs WITH (UPDLOCK, HOLDLOCK) WHERE ContainerId = @c AND IsManualJob = 0");
                jq.Parameters.AddWithValue("@c", G(cid));
                using (var r = jq.ExecuteReader()) while (r.Read()) { have.Add(Gid(r, 0)); if (Int(r, 2) == MswKind && string.Equals(Gid(r, 1), (string)plan["contentId"], StringComparison.OrdinalIgnoreCase)) dup = true; }
                if (dup) throw new Exception(label + ": inzwischen gibt es dort schon einen Job fuer das Paket - zurueckgerollt, nichts angelegt.");
                var want = new HashSet<string>(StringComparer.OrdinalIgnoreCase); foreach (JToken t in (JArray)plan["existingJobIds"]) want.Add((string)t);
                if (!have.SetEquals(want)) throw new Exception(label + ": die Dauerauftraege des Containers haben sich seit der Planung geaendert - zurueckgerollt, bitte neu aufrufen.");
                // Job-Vorlage
                var tq = TxCmd(wc, tx, "SELECT CAST(t.Id AS varchar(36)), t.JobKinds FROM SYS_Containers_Templates ct JOIN SYS_Templates t WITH (UPDLOCK) ON t.Id = ct.TemplateId " +
                    "WHERE ct.ContainerId = @c AND t.Name LIKE '" + ContainerTemplatePrefix + "%' AND ISNULL(t.IsReferenceCounted, 0) = 0 AND t.FolderId IS NULL");
                tq.Parameters.AddWithValue("@c", G(cid));
                string curTpl = null; byte[] curKinds = null; int n = 0;
                using (var r = tq.ExecuteReader()) while (r.Read()) { n++; curTpl = Gid(r, 0); curKinds = r.IsDBNull(1) ? null : (byte[])r.GetValue(1); }
                if (tplNew ? n != 0 : (n != 1 || !string.Equals(curTpl, tpl, StringComparison.OrdinalIgnoreCase)))
                    throw new Exception(label + ": die Job-Vorlage des Containers hat sich seit der Planung geaendert - zurueckgerollt, bitte neu aufrufen.");
                byte[] kinds = BytesOfHex((string)plan["kindsNew"]);
                if (tplNew)
                {
                    var iq = TxCmd(wc, tx, "INSERT INTO SYS_Templates (Id, Name, Description, IsSystemTemplate, IsClientSpecific, IsReferenceCounted, FolderId, JobKinds, OsFamily) VALUES (@t, @n, N'', 0, 0, 0, NULL, @k, 0)");
                    iq.Parameters.AddWithValue("@t", G(tpl)); iq.Parameters.AddWithValue("@n", (string)plan["templateName"]);
                    iq.Parameters.Add(new SqlParameter("@k", SqlDbType.Binary, 16) { Value = kinds });
                    iq.ExecuteNonQuery();
                }
                else if (HexOf(curKinds) != HexOf(kinds))
                {
                    if (HexOf(curKinds) != (string)plan["kindsOld"]) throw new Exception(label + ": JobKinds der Vorlage geaendert - zurueckgerollt.");
                    var uq = TxCmd(wc, tx, "UPDATE SYS_Templates SET JobKinds = @k WHERE Id = @t");
                    uq.Parameters.Add(new SqlParameter("@k", SqlDbType.Binary, 16) { Value = kinds }); uq.Parameters.AddWithValue("@t", G(tpl));
                    if (uq.ExecuteNonQuery() != 1) throw new Exception(label + ": Job-Vorlage nicht eindeutig - zurueckgerollt.");
                }
                // Job (Spalten wie der ACMP-Server laut Mitschnitt 22.10.2026)
                var jq2 = TxCmd(wc, tx, "INSERT INTO SYS_Jobs (Id, ContainerId, Conditions, ConditionCheckSum, JobAction, ContentId, IsManualJob, CanRunParallel, Name, Description, Priority, Origin, JobKind, ServerId, CreationDate) " +
                    "VALUES (@id, @c, @cond, @cs, @ja, @ct, 0, 0, @n, N'', 0, 1, 59, @srv, @cd)");
                jq2.Parameters.AddWithValue("@id", G(id)); jq2.Parameters.AddWithValue("@c", G(cid));
                jq2.Parameters.Add(XmlPar("@cond", (string)plan["conditionsXml"])); jq2.Parameters.AddWithValue("@cs", (string)plan["checksum"]);
                jq2.Parameters.Add(XmlPar("@ja", (string)plan["jobActionXml"])); jq2.Parameters.AddWithValue("@ct", G((string)plan["contentId"]));
                jq2.Parameters.AddWithValue("@n", (string)plan["name"]);
                jq2.Parameters.AddWithValue("@srv", string.IsNullOrEmpty((string)plan["serverId"]) ? (object)DBNull.Value : G((string)plan["serverId"]));
                jq2.Parameters.AddWithValue("@cd", now);
                jq2.ExecuteNonQuery();
                // Job-Monitor-Eintrag
                var lq = TxCmd(wc, tx, "INSERT INTO SYS_Jobs_Logs (JobId, JobName, CreatorName, CreationDate, ContentId, ContainerId, State, ConditionText, TestRing, JobKind, Origin) " +
                    "VALUES (@id, @n, @who, @cd, @ct, @c, 1, @txt, @ring, 59, 1)");
                lq.Parameters.AddWithValue("@id", G(id)); lq.Parameters.AddWithValue("@n", (string)plan["name"]); lq.Parameters.AddWithValue("@who", "MCP " + requester);
                lq.Parameters.AddWithValue("@cd", now); lq.Parameters.AddWithValue("@ct", G((string)plan["contentId"])); lq.Parameters.AddWithValue("@c", G(cid));
                lq.Parameters.AddWithValue("@txt", (string)plan["conditionText"] ?? ""); lq.Parameters.AddWithValue("@ring", (byte)(int)plan["testRing"]);
                lq.ExecuteNonQuery();
                // Verknuepfungen
                var vq = TxCmd(wc, tx, "INSERT INTO SYS_Templates_Jobs (Id, TemplateId, JobId, JobKind) VALUES (@tj, @t, @id, 59)");
                vq.Parameters.AddWithValue("@tj", G((string)plan["templateJobId"])); vq.Parameters.AddWithValue("@t", G(tpl)); vq.Parameters.AddWithValue("@id", G(id));
                vq.ExecuteNonQuery();
                if (tplNew)
                {
                    var cq = TxCmd(wc, tx, "INSERT INTO SYS_Containers_Templates (Id, TemplateId, ContainerId) VALUES (@x, @t, @c)");
                    cq.Parameters.AddWithValue("@x", G((string)plan["containerTemplateId"])); cq.Parameters.AddWithValue("@t", G(tpl)); cq.Parameters.AddWithValue("@c", G(cid));
                    cq.ExecuteNonQuery();
                }
                var ck = TxCmd(wc, tx, "SELECT COUNT(*) FROM SYS_Jobs j JOIN SYS_Templates_Jobs tj ON tj.JobId = j.Id JOIN SYS_Containers_Templates ct ON ct.TemplateId = tj.TemplateId " +
                    "WHERE j.Id = @id AND ct.ContainerId = @c AND j.ContainerId = @c");
                ck.Parameters.AddWithValue("@id", G(id)); ck.Parameters.AddWithValue("@c", G(cid));
                if (Convert.ToInt32(ck.ExecuteScalar()) != 1) throw new Exception(label + ": Kontrolle nach dem Anlegen fehlgeschlagen - zurueckgerollt.");
                tx.Commit();
            }
        }
        _log("acmp_msw_job_create von " + requester + ": " + label + " (" + plan["softwareAction"] + ", " + plan["conditionText"] + "), Job " + id + ", Sicherung " + bf);
        var res = new JObject();
        res["ok"] = true; res["aktion"] = "MSW-Dauerauftrag angelegt"; res["jobId"] = id; res["paket"] = plan["name"]; res["container"] = plan["path"];
        res["softwareAction"] = SoftwareActionText((string)plan["softwareAction"]); res["startbedingung"] = ParseConditions((string)plan["conditionsXml"]).Text;
        res["pruefsumme"] = Cs8((string)plan["checksum"]) + " " + plan["checksumQuelle"]; res["sicherung"] = bf;
        res["zurueck"] = "acmp_job_restore backup=" + bf + " (loescht den Job" + (tplNew ? " und die neu angelegte Vorlage" : "") + ") oder acmp_job_delete jobId=" + id + ".";
        res["hinweis"] = "Die Agents holen den Job mit ihrer naechsten Job-Liste. Pruefen: acmp_jobs name=\"" + plan["name"] + "\", acmp_msw_package package=" + plan["contentId"] + " (laeufe30/fehler30) und in der Console (Container, Reiter Jobs).";
        return res;
    }

    // ================= acmp_msw_test_install =================

    JObject PlanMswTest(JObject a)
    {
        string sa = SoftwareActionOf(StrArg(a, "action") ?? "install", true);
        if (sa == null) throw new Exception("action unbekannt: install (Standard), update oder reinstall.");
        bool ignore = BoolArg(a, "ignoreMaintenanceWindows", true);
        List<string> args = ListArg(a, "clients");
        if (args.Count == 0) { string one = StrArg(a, "client"); if (one != null) args.Add(one); }
        if (args.Count == 0) throw new Exception("clients fehlt: 1-" + MaxTestClients + " Computernamen oder ClientIds, z.B. [\"PC-0815\"].");
        if (args.Count > MaxTestClients) throw new Exception(args.Count + " Clients - ein Testauftrag geht an hoechstens " + MaxTestClients + ". Fuer mehr einen Dauerauftrag an einem Container (acmp_msw_job_create).");
        var plan = new JObject(); var pv = new JArray(); var warn = new List<string>(); var changed = new List<string>();
        var cls = new JArray(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        MswPkg p; MswVersion rel;
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            p = LoadPkg(c, ResolvePkg(c, StrArg(a, "package")), true);
            if (!p.Subscribed) throw new Exception(p.Name + " ist nicht abonniert - erst acmp_msw_subscribe.");
            rel = p.ReleasedFor(RingAll);
            if (rel == null) throw new Exception(p.Name + " hat keine freigegebene Version - nichts zu installieren. Stand: acmp_msw_package.");
            if (p.State == 1) warn.Add(p.Name + " ist von Aagon abgekuendigt (EoL).");
            foreach (string s in args)
            {
                string g = GuidBraces(s);
                var q = Cmd(c, "SELECT CAST(cl.CLIENTID AS varchar(36)), cl.COMPUTERNAME, cl.IsRetired, cl.IsManaged, cl.LastUpdate, " +
                    "CASE WHEN EXISTS (SELECT 1 FROM CLT_UnixClients u WHERE u.ClientId = cl.CLIENTID) OR cl.COMPUTERNAME LIKE '[[]NETWORK BOOT]%' THEN 1 ELSE 0 END, " +
                    "x.VersionString, x.DeploymentOption, CAST(x.VersionId AS varchar(36)), " +
                    "(SELECT COUNT(*) FROM SYS_Jobs_Logs_Items i JOIN SYS_Jobs j ON j.Id = i.JobId WHERE i.ClientId = cl.CLIENTID AND j.JobKind = 59 AND j.ContentId = @sw AND i.State IN (0, 1, 2, 5, 6)) " +
                    "FROM CLT_CLIENTS_TABLE cl LEFT JOIN CLT_SW_ManagedSoftware x ON x.ClientId = cl.CLIENTID AND x.SoftwareId = @sw " +
                    "WHERE " + (g != null ? "cl.CLIENTID = @id" : "cl.COMPUTERNAME = @n AND cl.IsRetired = 0"));
                q.Parameters.AddWithValue("@sw", G(p.Id));
                if (g != null) q.Parameters.AddWithValue("@id", G(g)); else q.Parameters.AddWithValue("@n", s.Trim());
                var o = new JObject(); int rows = 0;
                using (var r = q.ExecuteReader())
                    while (r.Read())
                    {
                        rows++;
                        o["clientId"] = Gid(r, 0); o["name"] = Str(r, 1); o["retired"] = Int(r, 2) == 1; o["managed"] = Int(r, 3) == 1; o["lastContact"] = OleText(Dbl(r, 4));
                        o["notWindows"] = Int(r, 5) == 1; o["installed"] = Str(r, 6); o["option"] = r.IsDBNull(7) ? -1 : Int(r, 7); o["versionId"] = Gid(r, 8); o["pending"] = Int(r, 9);
                    }
                if (rows == 0) throw new Exception("Client '" + s + "' nicht gefunden (Computername eines aktiven Clients oder ClientId).");
                if (rows > 1) throw new Exception("Computername '" + s + "' ist nicht eindeutig - bitte ClientId angeben.");
                string nm = (string)o["name"];
                if (!seen.Add((string)o["clientId"])) continue;
                if ((bool)o["retired"]) throw new Exception(nm + " ist ausgemustert - nichts geplant.");
                if (!(bool)o["managed"]) throw new Exception(nm + " ist kein verwalteter Client (kein Agent) - nichts geplant.");
                if ((bool)o["notWindows"]) throw new Exception(nm + " ist kein Windows-Client - Managed Software gibt es nur fuer Windows.");
                if ((int)o["pending"] > 0) throw new Exception(nm + " hat schon einen offenen Auftrag fuer " + p.Name + " (Job-Monitor) - erst abwarten oder mit acmp_job_delete entfernen.");
                if (GuardTargets != null) GuardTargets((string)o["clientId"]);
                string inst = IsNullTok(o["installed"]) ? null : (string)o["installed"];
                string st = inst == null ? "nicht installiert" : string.Equals((string)o["versionId"], rel.VersionId, StringComparison.OrdinalIgnoreCase) ? "aktuell (" + inst + ")"
                          : (int)o["option"] == 2 ? "neuer als die Freigabe (" + inst + ")" : "aeltere Version " + inst;
                o["stand"] = st;
                if (sa == "saUpdateOnly" && inst == null) warn.Add(nm + ": " + p.Name + " ist laut Inventar nicht installiert - 'update' hat dort nichts zu tun.");
                if (sa == "saInstallOrUpdate" && st.StartsWith("aktuell")) warn.Add(nm + ": laut Inventar schon aktuell - der Job meldet vermutlich 'nichts zu tun' (fuer einen echten Test reinstall oder einen Client mit aelterer Version).");
                o["execId"] = NewId(); o["clientTemplateId"] = NewId();
                cls.Add(o);
            }
            Dictionary<string, string> vals = ParamValues(p, a, changed);
            string log = LogOptionOf(StrArg(a, "log"), ConfigLogOption(c));
            string jobId = NewId(), condId = NewId();
            plan["action"] = "mswtest";
            plan["jobId"] = jobId; plan["name"] = p.Name; plan["contentId"] = p.Id; plan["softwareAction"] = sa; plan["logOption"] = log; plan["testRing"] = RingAll;
            plan["releasedVersion"] = rel.Display; plan["templateId"] = NewId(); plan["templateJobId"] = NewId();
            plan["kinds"] = HexOf(KindsMask(null, MswKind));
            plan["conditionsXml"] = ManualConditionXml(condId, ignore); plan["conditionText"] = ConditionTextFor("once", 1, ignore);
            plan["jobActionXml"] = MswJobActionXml(p.Id, p.Name, sa, log, RingAll, p.Params, vals, p.VersionMap());
            plan["serverId"] = LatestServerId(c);
            plan["clients"] = cls;
        }
        var names = new List<string>(); foreach (JObject o in cls) names.Add(o["name"] + " (" + o["stand"] + ", letzter Kontakt " + o["lastContact"] + ")");
        pv.Add("MSW-TESTAUFTRAG (einmalig, wie 'Managed Software zuweisen' in der Console): " + p.Name + " " + rel.Display + " - " + SoftwareActionText(sa) + " - an " + cls.Count + " Client(s): " + string.Join("; ", names.ToArray()) + ".");
        pv.Add("   Startbedingung: sobald wie moeglich, einmalig" + (ignore ? ", ignoriert Wartungsfenster" : ", nur im Wartungsfenster") + " [Console: " + plan["conditionText"] + "]; Skript-Protokoll " + plan["logOption"] +
               (changed.Count > 0 ? "; Parameter abweichend: " + string.Join("; ", changed.ToArray()) : "") + ".");
        foreach (string w in warn) pv.Add("Hinweis: " + w);
        pv.Add("Zustellung: Der MCP schreibt den Auftrag wie der ACMP-Server in die DB (Einzelauftrags-Vorlage, SYS_Jobs, Vorlage am Client, Job-Monitor), kann aber keinen Push an den Agent ausloesen. " +
               "Der Agent holt ihn bei seiner naechsten Job-Abfrage (Agentenstart bzw. regelmaessig). AUF DEM CLIENT WIRD DANN INSTALLIERT.");
        pv.Add("Stand: acmp_msw_client client=<Name> bzw. acmp_job_results. Zurueck, solange er nicht lief: acmp_job_delete jobId=" + plan["jobId"] + " (oder acmp_job_restore mit der Sicherung).");
        plan["vorschau"] = pv;
        return plan;
    }

    JObject ExecMswTest(JObject plan, string requester)
    {
        string id = (string)plan["jobId"], tpl = (string)plan["templateId"];
        var cls = (JArray)plan["clients"];
        if (GuardTargets != null) foreach (JObject o in cls) GuardTargets((string)o["clientId"]);
        string label = "MSW-Testauftrag \"" + plan["name"] + "\"";
        var b = new JObject();
        b["kind"] = "mswtest"; b["tool"] = "acmp_msw_test_install"; b["von"] = requester; b["zeit"] = DateTime.Now.ToString("s"); b["jobId"] = id; b["name"] = plan["name"];
        var tn = new List<string>(); foreach (JObject o in cls) tn.Add((string)o["name"]);
        b["target"] = string.Join(", ", tn.ToArray()); b["templateId"] = tpl; b["templateJobId"] = plan["templateJobId"]; b["contentId"] = plan["contentId"];
        b["clients"] = cls; b["jobAction"] = plan["jobActionXml"]; b["conditions"] = plan["conditionsXml"];
        string bf = Path.GetFileName(SaveBackup(b, "mswtest", id));
        double now = DateTime.Now.ToOADate();
        using (var wc = new SqlConnection(_writeCs))
        {
            wc.Open();
            using (SqlTransaction tx = wc.BeginTransaction())
            {
                // offene Auftraege fuer dasselbe Paket auf den Clients (seit der Planung)?
                foreach (JObject o in cls)
                {
                    var pq = TxCmd(wc, tx, "SELECT COUNT(*) FROM SYS_Jobs_Logs_Items i JOIN SYS_Jobs j ON j.Id = i.JobId WHERE i.ClientId = @cl AND j.JobKind = 59 AND j.ContentId = @sw AND i.State IN (0, 1, 2, 5, 6)");
                    pq.Parameters.AddWithValue("@cl", G((string)o["clientId"])); pq.Parameters.AddWithValue("@sw", G((string)plan["contentId"]));
                    if (Convert.ToInt32(pq.ExecuteScalar()) > 0) throw new Exception(label + ": " + o["name"] + " hat inzwischen einen offenen Auftrag fuer das Paket - zurueckgerollt, nichts angelegt.");
                }
                var t1 = TxCmd(wc, tx, "INSERT INTO SYS_Templates (Id, Name, Description, IsSystemTemplate, IsClientSpecific, IsReferenceCounted, FolderId, JobKinds, OsFamily) VALUES (@t, N'', N'', 0, 0, 1, NULL, @k, 0)");
                t1.Parameters.AddWithValue("@t", G(tpl)); t1.Parameters.Add(new SqlParameter("@k", SqlDbType.Binary, 16) { Value = BytesOfHex((string)plan["kinds"]) });
                t1.ExecuteNonQuery();
                var j1 = TxCmd(wc, tx, "INSERT INTO SYS_Jobs (Id, ContainerId, Conditions, ConditionCheckSum, JobAction, ContentId, IsManualJob, CanRunParallel, Name, Description, Priority, Origin, JobKind, ServerId, CreationDate) " +
                    "VALUES (@id, NULL, @cond, '', @ja, @ct, 1, 0, @n, N'', 0, 0, 59, @srv, @cd)");
                j1.Parameters.AddWithValue("@id", G(id)); j1.Parameters.Add(XmlPar("@cond", (string)plan["conditionsXml"])); j1.Parameters.Add(XmlPar("@ja", (string)plan["jobActionXml"]));
                j1.Parameters.AddWithValue("@ct", G((string)plan["contentId"])); j1.Parameters.AddWithValue("@n", (string)plan["name"]);
                j1.Parameters.AddWithValue("@srv", string.IsNullOrEmpty((string)plan["serverId"]) ? (object)DBNull.Value : G((string)plan["serverId"])); j1.Parameters.AddWithValue("@cd", now);
                j1.ExecuteNonQuery();
                var v1 = TxCmd(wc, tx, "INSERT INTO SYS_Templates_Jobs (Id, TemplateId, JobId, JobKind) VALUES (@tj, @t, @id, 59)");
                v1.Parameters.AddWithValue("@tj", G((string)plan["templateJobId"])); v1.Parameters.AddWithValue("@t", G(tpl)); v1.Parameters.AddWithValue("@id", G(id));
                v1.ExecuteNonQuery();
                var l1 = TxCmd(wc, tx, "INSERT INTO SYS_Jobs_Logs (JobId, JobName, CreatorName, CreationDate, ContentId, ContainerId, State, ConditionText, TestRing, JobKind, Origin) " +
                    "VALUES (@id, @n, @who, @cd, @ct, NULL, 1, @txt, @ring, 59, 0)");
                l1.Parameters.AddWithValue("@id", G(id)); l1.Parameters.AddWithValue("@n", (string)plan["name"]); l1.Parameters.AddWithValue("@who", "MCP " + requester);
                l1.Parameters.AddWithValue("@cd", now); l1.Parameters.AddWithValue("@ct", G((string)plan["contentId"]));
                l1.Parameters.AddWithValue("@txt", (string)plan["conditionText"]); l1.Parameters.AddWithValue("@ring", (byte)(int)plan["testRing"]);
                l1.ExecuteNonQuery();
                var execIds = new List<string>(); var clientIds = new List<string>();
                foreach (JObject o in cls)
                {
                    var c1 = TxCmd(wc, tx, "INSERT INTO CLT_Clients_Templates (Id, TemplateId, ClientId, IsPersistent, CreationDate) VALUES (@x, @t, @cl, 0, @dt)");
                    c1.Parameters.AddWithValue("@x", G((string)o["clientTemplateId"])); c1.Parameters.AddWithValue("@t", G(tpl)); c1.Parameters.AddWithValue("@cl", G((string)o["clientId"]));
                    c1.Parameters.AddWithValue("@dt", DateTime.Now);
                    c1.ExecuteNonQuery();
                    execIds.Add((string)o["execId"]); clientIds.Add((string)o["clientId"]);
                }
                // Eintraege im Job-Monitor wie der ACMP-Server (State 0 = wartet auf Zustellung)
                var bq = TxCmd(wc, tx, "EXEC ap_Job_BulkAddJobLogItems @aExecIds = @e, @aJobId = @id, @aClientIds = @c, @aLastActivity = @now, @aTestRing = @ring, @aState = 0");
                bq.Parameters.Add(XmlPar("@e", IdsXml(execIds))); bq.Parameters.AddWithValue("@id", G(id)); bq.Parameters.Add(XmlPar("@c", IdsXml(clientIds)));
                bq.Parameters.AddWithValue("@now", now); bq.Parameters.AddWithValue("@ring", (byte)(int)plan["testRing"]);
                bq.ExecuteNonQuery();
                var ck = TxCmd(wc, tx, "SELECT COUNT(*) FROM SYS_Jobs_Logs_Items WHERE JobId = @id AND State = 0");
                ck.Parameters.AddWithValue("@id", G(id));
                if (Convert.ToInt32(ck.ExecuteScalar()) != cls.Count) throw new Exception(label + ": Kontrolle der Job-Monitor-Eintraege fehlgeschlagen - zurueckgerollt.");
                var ck2 = TxCmd(wc, tx, "SELECT COUNT(*) FROM CLT_Clients_Templates WHERE TemplateId = @t");
                ck2.Parameters.AddWithValue("@t", G(tpl));
                if (Convert.ToInt32(ck2.ExecuteScalar()) != cls.Count) throw new Exception(label + ": Kontrolle der Client-Vorlagen fehlgeschlagen - zurueckgerollt.");
                tx.Commit();
            }
        }
        _log("acmp_msw_test_install von " + requester + ": " + label + " (" + plan["softwareAction"] + ") an " + b["target"] + ", Job " + id + ", Sicherung " + bf);
        var res = new JObject();
        res["ok"] = true; res["aktion"] = "MSW-Testauftrag angelegt"; res["jobId"] = id; res["paket"] = plan["name"] + " " + plan["releasedVersion"]; res["clients"] = b["target"];
        res["softwareAction"] = SoftwareActionText((string)plan["softwareAction"]); res["sicherung"] = bf;
        res["hinweis"] = "Der Agent holt den Auftrag bei seiner naechsten Job-Abfrage (kein Push durch den MCP). Stand: acmp_msw_client client=<Name> (letzteLaeufe) bzw. acmp_job_results.";
        res["zurueck"] = "Solange er nicht lief: acmp_job_delete jobId=" + id + " (oder acmp_job_restore backup=" + bf + ").";
        return res;
    }

    // ================= acmp_msw_subscribe =================

    JObject PlanSubscribe(JObject a)
    {
        List<string> args = ListArg(a, "packages");
        if (args.Count == 0) { string one = StrArg(a, "package"); if (one != null) args.Add(one); }
        if (args.Count == 0) throw new Exception("packages fehlt: 1-" + MaxSubscribe + " Paket-Ids oder Namen aus acmp_msw_packages catalog=true.");
        if (args.Count > MaxSubscribe) throw new Exception("hoechstens " + MaxSubscribe + " Pakete je Auftrag.");
        var items = new JArray(); var pv = new JArray(); var skipped = new List<string>();
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            var lic = Cmd(c, "SELECT COUNT(*) FROM SYS_ManagedSoftware_Subscriptions s JOIN SYS_ManagedSoftware_Catalog c ON c.Id = s.Id WHERE c.LicenseKind = 0");
            int licensedSubs = Convert.ToInt32(lic.ExecuteScalar());
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string s in args)
            {
                MswPkg p = LoadPkg(c, ResolvePkg(c, s), false);
                if (!seen.Add(p.Id)) continue;
                if (p.Subscribed) { skipped.Add(p.Name + " ist schon abonniert"); continue; }
                if (p.State == 1) throw new Exception(p.Name + " ist von Aagon abgekuendigt (EoL) - nicht abonnieren, Nachfolgepaket waehlen (acmp_msw_packages catalog=true name=...).");
                if (p.License == 0 && licensedSubs == 0)
                    throw new Exception(p.Name + " ist kein Evaluationspaket; ob die Managed-Software-Lizenz es erlaubt, prueft nur die Console - bitte dort abonnieren.");
                var it = new JObject();
                it["id"] = p.Id; it["name"] = p.Name; it["hersteller"] = p.Manufacturer; it["versionen"] = p.Versions.Count;
                if (p.Latest != null) it["neuesteVersion"] = p.Latest.Display;
                var iq = Cmd(c, "SELECT COUNT(*) FROM CLT_SW_ManagedSoftware x JOIN CLT_CLIENTS_TABLE cl ON cl.CLIENTID = x.ClientId AND cl.IsRetired = 0 WHERE x.SoftwareId = @id");
                iq.Parameters.AddWithValue("@id", G(p.Id));
                it["erkanntAuf"] = Convert.ToInt32(iq.ExecuteScalar());
                items.Add(it);
            }
        }
        if (items.Count == 0) throw new Exception("Nichts zu tun: " + string.Join("; ", skipped.ToArray()) + ".");
        var plan = new JObject();
        plan["action"] = "subscribe"; plan["items"] = items; plan["skipped"] = new JArray(skipped.ToArray());
        pv.Add("ABONNIEREN wie der ACMP-Server (EXEC ap_ManagedSoftware_Subscribe): " + items.Count + " Paket(e):");
        foreach (JObject it in items) pv.Add("   " + it["name"] + " (" + it["hersteller"] + "), neueste Version " + (IsNullTok(it["neuesteVersion"]) ? "?" : (string)it["neuesteVersion"]) + ", laut Inventar auf " + it["erkanntAuf"] + " Client(s).");
        foreach (string s in skipped) pv.Add("Uebersprungen: " + s);
        pv.Add("Wirkung: nur die Zeile in SYS_ManagedSoftware_Subscriptions. Herunterladen, Testring und Freigabe (Ring-Konfiguration 'Default', automatisch) erledigt der ACMP-Server beim naechsten MSW-Abgleich; " +
               "solange zeigt acmp_msw_package 'noch keine Version geladen'. Die Lizenzpruefung der Console (Evaluationspakete) laeuft dabei nicht - nur Pakete abonnieren, fuer die eine Lizenz besteht.");
        pv.Add("Zurueck: acmp_job_restore mit der Sicherung (abbestellen wie die Console: ap_ManagedSoftware_Unsubscribe), solange kein Job und kein Kiosk-Eintrag das Paket nutzt.");
        plan["vorschau"] = pv;
        return plan;
    }

    JObject ExecSubscribe(JObject plan, string requester)
    {
        var ids = new List<string>(); var names = new List<string>();
        foreach (JObject it in (JArray)plan["items"]) { ids.Add((string)it["id"]); names.Add((string)it["name"]); }
        var b = new JObject();
        b["kind"] = "subscribe"; b["tool"] = "acmp_msw_subscribe"; b["von"] = requester; b["zeit"] = DateTime.Now.ToString("s"); b["jobId"] = ids[0];
        b["name"] = string.Join(", ", names.ToArray()); b["target"] = b["name"]; b["packageIds"] = new JArray(ids.ToArray());
        string bf = Path.GetFileName(SaveBackup(b, "subscribe", ids[0]));
        using (var wc = new SqlConnection(_writeCs))
        {
            wc.Open();
            using (SqlTransaction tx = wc.BeginTransaction())
            {
                foreach (string id in ids)
                {
                    var q = TxCmd(wc, tx, "SELECT COUNT(*) FROM SYS_ManagedSoftware_Subscriptions WITH (UPDLOCK, HOLDLOCK) WHERE Id = @id");
                    q.Parameters.AddWithValue("@id", G(id));
                    if (Convert.ToInt32(q.ExecuteScalar()) != 0) throw new Exception("Paket " + id + " ist inzwischen abonniert - zurueckgerollt.");
                }
                var e = TxCmd(wc, tx, "EXEC ap_ManagedSoftware_Subscribe @aCatalogIds = @x");
                e.Parameters.Add(XmlPar("@x", IdsXml(ids)));
                e.ExecuteNonQuery();
                foreach (string id in ids)
                {
                    var q = TxCmd(wc, tx, "SELECT COUNT(*) FROM SYS_ManagedSoftware_Subscriptions WHERE Id = @id");
                    q.Parameters.AddWithValue("@id", G(id));
                    if (Convert.ToInt32(q.ExecuteScalar()) != 1) throw new Exception("Kontrolle: Paket " + id + " nicht abonniert - zurueckgerollt.");
                }
                tx.Commit();
            }
        }
        _log("acmp_msw_subscribe von " + requester + ": " + b["name"] + ", Sicherung " + bf);
        var res = new JObject();
        res["ok"] = true; res["aktion"] = "abonniert"; res["pakete"] = new JArray(names.ToArray()); res["sicherung"] = bf;
        res["hinweis"] = "Der ACMP-Server laedt die Versionen beim naechsten MSW-Abgleich; danach zeigt acmp_msw_package Versionen und Freigabe. Erst dann einen Job anlegen (acmp_msw_job_create).";
        res["zurueck"] = "acmp_job_restore backup=" + bf + " (abbestellen).";
        return res;
    }

    // ================= acmp_job_delete fuer einmalige Jobs =================

    // Plan-Eintrag fuer einen einmaligen Job (Job-Monitor "Loeschen"); j aus LookupJob
    JObject PlanManualDeleteItem(SqlConnection c, JObject j, List<string> lines, int no, bool force)
    {
        string id = (string)j["id"];
        int origin = -1;
        var oq = Cmd(c, "SELECT Origin FROM SYS_Jobs WHERE Id = @id");
        oq.Parameters.AddWithValue("@id", G(id));
        object ov = oq.ExecuteScalar(); if (ov != null && !(ov is DBNull)) origin = Convert.ToInt32(ov);
        string tj = null, tpl = null, tname = null; bool rc = false, named = false; int cont = 0, jobsInTpl = 0, clientLinks = 0, links = 0;
        var tq = Cmd(c, "SELECT CAST(tj.Id AS varchar(36)), CAST(t.Id AS varchar(36)), t.Name, ISNULL(CAST(t.IsReferenceCounted AS int), 0), CASE WHEN t.FolderId IS NULL THEN 0 ELSE 1 END, " +
            "(SELECT COUNT(*) FROM SYS_Containers_Templates ct WHERE ct.TemplateId = t.Id), (SELECT COUNT(*) FROM SYS_Templates_Jobs x WHERE x.TemplateId = t.Id), " +
            "(SELECT COUNT(*) FROM CLT_Clients_Templates ct WHERE ct.TemplateId = t.Id) FROM SYS_Templates_Jobs tj JOIN SYS_Templates t ON t.Id = tj.TemplateId WHERE tj.JobId = @id");
        tq.Parameters.AddWithValue("@id", G(id));
        using (var r = tq.ExecuteReader())
            while (r.Read())
            {
                links++;
                tj = Gid(r, 0); tpl = Gid(r, 1); tname = Str(r, 2); rc = Int(r, 3) == 1; named = !string.IsNullOrEmpty(tname) || Int(r, 4) == 1;
                cont = Int(r, 5); jobsInTpl = Int(r, 6); clientLinks = Int(r, 7);
            }
        string why = RefuseManualDelete((int)j["kind"], origin, links, rc, named, cont);
        if (why != null) throw new Exception(JobLabel(j) + " " + why + ". Nichts geplant.");
        var states = new List<string>(); int items = 0, running = 0; double last = 0;
        var iq = Cmd(c, "SELECT i.ComputerName, i.State, i.Result, i.LastActivity, LEFT(ISNULL(i.StateMsg, ''), 120) FROM SYS_Jobs_Logs_Items i WHERE i.JobId = @id ORDER BY i.ComputerName");
        iq.Parameters.AddWithValue("@id", G(id));
        double now = DateTime.Now.ToOADate();
        using (var r = iq.ExecuteReader())
            while (r.Read())
            {
                items++;
                double la = Dbl(r, 3); if (la > last) last = la;
                int st = Int(r, 1);
                if (st == 2 && now - la < 2.0 / 24) running++;
                if (states.Count < 12) states.Add((Str(r, 0) ?? "?") + ": " + ExecStateText(st) + (Int(r, 2) > 0 ? "/" + ResultText(Int(r, 2)) : "") + " seit " + OleText(la) + (Str(r, 4).Length > 0 ? " (" + Str(r, 4) + ")" : ""));
            }
        int subs = 0, logs = 0; string creator = null; double created = 0;
        var sq = Cmd(c, "SELECT (SELECT COUNT(*) FROM SYS_Jobs_Logs_SubItems WHERE JobId = @id), (SELECT COUNT(*) FROM SYS_Jobs_Logs WHERE JobId = @id), " +
            "(SELECT TOP 1 CreatorName FROM SYS_Jobs_Logs WHERE JobId = @id), (SELECT TOP 1 CreationDate FROM SYS_Jobs_Logs WHERE JobId = @id)");
        sq.Parameters.AddWithValue("@id", G(id));
        using (var r = sq.ExecuteReader()) if (r.Read()) { subs = Int(r, 0); logs = Int(r, 1); creator = Str(r, 2); created = Dbl(r, 3); }
        var it = new JObject();
        it["jobId"] = id; it["name"] = j["name"]; it["kind"] = j["kind"]; it["manual"] = true; it["target"] = items + " Client(s)";
        it["templateJobId"] = tj; it["templateId"] = tpl; it["templateRemoved"] = jobsInTpl == 1; it["clientLinks"] = clientLinks;
        it["items"] = items; it["lastActivity"] = last; it["subItems"] = subs; it["logs"] = logs; it["checksum"] = j["checksum"];
        lines.Add(no + ". " + JobLabel(j) + " - EINZELAUFTRAG" + (creator != null ? " von " + creator : "") + (created > 0 ? " vom " + OleText(created) : "") + ", " + items + " Eintrag/Eintraege im Job-Monitor:");
        foreach (string s in states) lines.Add("   " + s);
        if (items > states.Count) lines.Add("   ...");
        lines.Add("   Geloescht werden die Job-Monitor-Eintraege (wie 'Loeschen' im Job-Monitor), dann Job und " + (jobsInTpl == 1 ? "Einzelauftrags-Vorlage samt " + clientLinks + " Client-Verknuepfung(en)"
                  : "Verknuepfung (die Vorlage nutzt noch " + (jobsInTpl - 1) + " weitere(n) Job(s); sie wird mitgeloescht, sobald sie leer ist, z.B. wenn der andere Job im selben Auftrag steht)") +
                  " - wie die Bereinigung des ACMP-Servers (ap_Job_ClearOrphanTemplates), nur fuer diesen Job.");
        if (running > 0 && !force)
            throw new Exception(JobLabel(j) + ": " + running + " Eintrag/Eintraege 'laeuft' mit Aktivitaet in den letzten 2 Stunden - der Job laeuft vermutlich gerade. Abwarten oder bewusst mit force=true loeschen. Nichts geplant.");
        if (running > 0) lines.Add("   ACHTUNG (force): " + running + " Eintrag/Eintraege 'laeuft' mit Aktivitaet in den letzten 2 Stunden - der Job laeuft vielleicht gerade; sein Ergebnis landet dann nicht mehr im Job-Monitor.");
        return it;
    }

    // Generische Zeilen fuer Sicherung und Wiederherstellung (Spalten und Typen laut DB 24.10.2026)
    static readonly Dictionary<string, string[]> RowSpecs = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        { "SYS_Templates", new[] { "Id:g", "Name:s", "IsSystemTemplate:b", "IsClientSpecific:b", "IsReferenceCounted:b", "FolderId:g", "Description:s", "JobKinds:x", "OsFamily:t" } },
        { "CLT_Clients_Templates", new[] { "Id:g", "TemplateId:g", "ClientId:g", "IsPersistent:b", "CreationDate:d" } },
        { "SYS_Jobs_Logs", new[] { "JobId:g", "JobName:s", "Origin:t", "CreatorName:s", "CreationDate:f", "ContainerId:g", "JobKind:t", "ContentId:g", "State:t", "ConditionText:s", "TestRing:t" } },
        { "SYS_Jobs_Logs_Items", new[] { "ExecId:g", "JobId:g", "ClientId:g", "State:t", "StartedDate:f", "LastActivity:f", "Result:i", "ComputerName:s", "StateMsg:s", "TenantId:g", "TenantNo:i", "TestRing:t" } },
        { "SYS_Jobs_Logs_SubItems", new[] { "ChildExecId:g", "ParentExecId:g", "JobId:g", "StartDate:f", "EndDate:f", "NativeMessage:s", "MessageCode:i", "ErrorCode:i", "NativeErrorMessage:s",
                                            "Result:i", "LastUpdate:d", "JobKind:t", "TestRing:t", "ContentId:g", "IconCode:i" } },
    };

    static string ColList(string table)
    {
        var l = new List<string>(); foreach (string s in RowSpecs[table]) l.Add("[" + s.Split(':')[0] + "]");
        return string.Join(", ", l.ToArray());
    }

    JArray ReadRows(SqlConnection c, string table, string where, string id)
    {
        var res = new JArray();
        var q = Cmd(c, "SELECT " + ColList(table) + " FROM " + table + " WHERE " + where);
        q.Parameters.AddWithValue("@id", G(id));
        string[] spec = RowSpecs[table];
        using (var r = q.ExecuteReader())
            while (r.Read())
            {
                var o = new JObject();
                for (int i = 0; i < spec.Length; i++)
                {
                    string[] p = spec[i].Split(':');
                    if (r.IsDBNull(i)) { o[p[0]] = JValue.CreateNull(); continue; }
                    object v = r.GetValue(i);
                    switch (p[1])
                    {
                        case "g": o[p[0]] = GuidBraces(Convert.ToString(v, CultureInfo.InvariantCulture)); break;
                        case "b": o[p[0]] = Convert.ToBoolean(v); break;
                        case "t": case "i": o[p[0]] = Convert.ToInt32(v, CultureInfo.InvariantCulture); break;
                        case "f": o[p[0]] = Convert.ToDouble(v, CultureInfo.InvariantCulture); break;
                        case "d": o[p[0]] = ((DateTime)v).ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture); break;
                        case "x": o[p[0]] = HexOf((byte[])v); break;
                        default: o[p[0]] = Convert.ToString(v, CultureInfo.InvariantCulture); break;
                    }
                }
                res.Add(o);
            }
        return res;
    }

    static void InsertRow(SqlConnection wc, SqlTransaction tx, string table, JObject row)
    {
        string[] spec = RowSpecs[table];
        var cols = new List<string>(); var pars = new List<string>();
        var q = TxCmd(wc, tx, "");
        for (int i = 0; i < spec.Length; i++)
        {
            string[] p = spec[i].Split(':');
            cols.Add("[" + p[0] + "]"); pars.Add("@p" + i);
            JToken t = row[p[0]];
            bool nul = t == null || t.Type == JTokenType.Null;
            SqlParameter sp;
            switch (p[1])
            {
                case "g": sp = new SqlParameter("@p" + i, SqlDbType.UniqueIdentifier); sp.Value = nul ? (object)DBNull.Value : G((string)t); break;
                case "b": sp = new SqlParameter("@p" + i, SqlDbType.Bit); sp.Value = nul ? (object)DBNull.Value : (bool)t; break;
                case "t": sp = new SqlParameter("@p" + i, SqlDbType.TinyInt); sp.Value = nul ? (object)DBNull.Value : (byte)(int)t; break;
                case "i": sp = new SqlParameter("@p" + i, SqlDbType.Int); sp.Value = nul ? (object)DBNull.Value : (int)t; break;
                case "f": sp = new SqlParameter("@p" + i, SqlDbType.Float); sp.Value = nul ? (object)DBNull.Value : (double)t; break;
                case "d": sp = new SqlParameter("@p" + i, SqlDbType.DateTime); sp.Value = nul ? (object)DBNull.Value : t.Type == JTokenType.Date ? (DateTime)t : DateTime.Parse((string)t, CultureInfo.InvariantCulture); break;
                case "x": sp = new SqlParameter("@p" + i, SqlDbType.Binary, 16); sp.Value = nul ? (object)DBNull.Value : BytesOfHex((string)t); break;
                default: sp = new SqlParameter("@p" + i, SqlDbType.NVarChar, -1); sp.Value = nul ? (object)DBNull.Value : (string)t; break;
            }
            q.Parameters.Add(sp);
        }
        q.CommandText = "INSERT INTO " + table + " (" + string.Join(", ", cols.ToArray()) + ") VALUES (" + string.Join(", ", pars.ToArray()) + ")";
        q.ExecuteNonQuery();
    }

    JObject ExecManualDeleteItem(SqlConnection rc, SqlConnection wc, JObject it, string requester)
    {
        string id = (string)it["jobId"], tpl = (string)it["templateId"], label = "Einzelauftrag \"" + it["name"] + "\" " + id;
        JObject row = FullJobRow(rc, id);
        if (row == null) throw new Exception(label + " gibt es nicht mehr - nichts geloescht.");
        var b = new JObject();
        b["kind"] = "delete"; b["manual"] = true; b["tool"] = "acmp_job_delete"; b["von"] = requester; b["zeit"] = DateTime.Now.ToString("s"); b["jobId"] = id; b["name"] = it["name"]; b["target"] = it["target"];
        b["row"] = row; b["templateJobId"] = it["templateJobId"]; b["templateId"] = tpl; b["templateJobKind"] = it["kind"];
        JArray trow = ReadRows(rc, "SYS_Templates", "Id = @id", tpl);
        b["template"] = trow.Count == 1 ? trow[0] : null;
        b["clientLinks"] = ReadRows(rc, "CLT_Clients_Templates", "TemplateId = @id", tpl);
        b["logs"] = ReadRows(rc, "SYS_Jobs_Logs", "JobId = @id", id);
        b["items"] = ReadRows(rc, "SYS_Jobs_Logs_Items", "JobId = @id", id);
        b["subItems"] = ReadRows(rc, "SYS_Jobs_Logs_SubItems", "JobId = @id", id);
        string bf = Path.GetFileName(SaveBackup(b, "delete", id));
        bool tplDeleted;
        using (SqlTransaction tx = wc.BeginTransaction())
        {
            var lk = TxCmd(wc, tx, "SELECT ISNULL(ConditionCheckSum, '') FROM SYS_Jobs WITH (UPDLOCK, ROWLOCK) WHERE Id = @id");
            lk.Parameters.AddWithValue("@id", G(id));
            object cur = lk.ExecuteScalar();
            if (cur == null) throw new Exception(label + " gibt es nicht mehr - zurueckgerollt.");
            var fq = TxCmd(wc, tx, "SELECT COUNT(*), ISNULL(MAX(LastActivity), 0) FROM SYS_Jobs_Logs_Items WITH (UPDLOCK, HOLDLOCK) WHERE JobId = @id");
            fq.Parameters.AddWithValue("@id", G(id));
            using (var r = fq.ExecuteReader())
            {
                r.Read();
                if (Convert.ToInt32(r.GetValue(0)) != (int)it["items"] || Math.Abs(Convert.ToDouble(r.GetValue(1)) - (double)it["lastActivity"]) > 1e-9)
                    throw new Exception(label + ": der Job-Monitor hat sich seit der Planung geaendert (Job laeuft?) - zurueckgerollt, nichts geloescht.");
            }
            // 1) wie 'Loeschen' im Job-Monitor (Aagon.JobManagement.DeleteJobLogs, Variante je JobId)
            var d1 = TxCmd(wc, tx, "DELETE FROM SYS_Jobs_Logs_SubItems WHERE JobId = @id; DELETE FROM SYS_Jobs_Logs_Items WHERE JobId = @id; " +
                "DELETE FROM SYS_Jobs_Logs WHERE ((Origin <> 1) OR JobId NOT IN (SELECT Id FROM SYS_Jobs)) AND JobId = @id;");
            d1.Parameters.AddWithValue("@id", G(id));
            d1.ExecuteNonQuery();
            // 2) Job und Vorlage wie ap_Job_ClearOrphanTemplates, nur fuer diesen Job (Trigger TG_Delete_SYS_Templates_Jobs loescht SYS_Jobs)
            var d2 = TxCmd(wc, tx, "DELETE FROM SYS_Templates_Jobs WHERE Id = @tj AND JobId = @id");
            d2.Parameters.AddWithValue("@tj", G((string)it["templateJobId"])); d2.Parameters.AddWithValue("@id", G(id));
            if (d2.ExecuteNonQuery() != 1) throw new Exception(label + ": Verknuepfung mit der Vorlage nicht eindeutig - zurueckgerollt.");
            var d3 = TxCmd(wc, tx, "DELETE FROM SYS_Templates WHERE Id = @t AND IsReferenceCounted = 1 AND ISNULL(Name, '') = '' AND FolderId IS NULL " +
                "AND NOT EXISTS (SELECT 1 FROM SYS_Templates_Jobs x WHERE x.TemplateId = @t) AND NOT EXISTS (SELECT 1 FROM SYS_Containers_Templates ct WHERE ct.TemplateId = @t)");
            d3.Parameters.AddWithValue("@t", G(tpl));
            d3.ExecuteNonQuery();
            var k1 = TxCmd(wc, tx, "SELECT (SELECT COUNT(*) FROM SYS_Jobs WHERE Id = @id) + (SELECT COUNT(*) FROM SYS_Jobs_Logs WHERE JobId = @id) + (SELECT COUNT(*) FROM SYS_Jobs_Logs_Items WHERE JobId = @id), " +
                "(SELECT COUNT(*) FROM SYS_Templates WHERE Id = @t)");
            k1.Parameters.AddWithValue("@id", G(id)); k1.Parameters.AddWithValue("@t", G(tpl));
            using (var r = k1.ExecuteReader())
            {
                r.Read();
                if (Convert.ToInt32(r.GetValue(0)) != 0) throw new Exception(label + ": Kontrolle nach dem Loeschen fehlgeschlagen (Job oder Eintraege noch da) - zurueckgerollt.");
                tplDeleted = Convert.ToInt32(r.GetValue(1)) == 0;
            }
            tx.Commit();
        }
        _log("acmp_job_delete (Einzelauftrag) von " + requester + ": " + label + (tplDeleted ? " samt Vorlage" : "") + ", Sicherung " + bf);
        var o = new JObject(); o["jobId"] = id; o["name"] = it["name"]; o["art"] = "Einzelauftrag"; o["eintraege"] = it["items"]; o["vorlageGeloescht"] = tplDeleted; o["sicherung"] = bf;
        return o;
    }

    // ================= Zuruecknehmen =================

    JObject PlanRestoreMsw(JObject b, string name, bool force)
    {
        string kind = (string)b["kind"], id = (string)b["jobId"];
        var plan = new JObject(); plan["backup"] = name; plan["jobId"] = id; plan["kind"] = kind;
        var pv = new JArray();
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            if (kind == "mswjob" || kind == "ccjob")   // ccjob = Client-Command-Dauerauftrag aus acmp_job_create (AcmpLuecken.cs), gleicher Aufbau
            {
                JObject now = LookupJob(c, id);
                if (now == null) throw new Exception("Job " + id + " gibt es nicht mehr - nichts zurueckzunehmen.");
                if ((string)now["checksum"] != (string)b["after"]["checksum"] || !string.Equals((string)now["containerId"], (string)b["containerId"], StringComparison.OrdinalIgnoreCase))
                    throw new Exception(JobLabel(now) + " wurde seit dem Anlegen geaendert - bitte acmp_job_delete verwenden.");
                if ((int)now["templateLinks"] != 1 || !string.Equals((string)now["templateJobId"], (string)b["templateJobId"], StringComparison.OrdinalIgnoreCase))
                    throw new Exception(JobLabel(now) + ": Verknuepfung mit der Job-Vorlage geaendert - bitte in der Console.");
                plan["action"] = "restore_mswjob"; plan["templateJobId"] = b["templateJobId"]; plan["templateId"] = b["templateId"]; plan["templateNew"] = b["templateNew"]; plan["checksum"] = now["checksum"];
                pv.Add("Angelegten " + (kind == "ccjob" ? "Client-Command-Job" : "MSW-Job") + " WIEDER LOESCHEN: " + JobLabel(now) + " an " + TargetText(now) + " (Sicherung " + name + " vom " + b["zeit"] + ")" +
                       ((bool)b["templateNew"] ? "; die damals neu angelegte Job-Vorlage wird mitgeloescht, wenn sie dann leer ist." : "") + " Der Job-Monitor-Eintrag bleibt (Status fertig), wie beim Loeschen in der Console.");
            }
            else if (kind == "mswtest")
            {
                JObject now = LookupJob(c, id);
                if (now == null) throw new Exception("Testauftrag " + id + " gibt es nicht mehr - nichts zurueckzunehmen.");
                var lines = new List<string>();
                JObject it = PlanManualDeleteItem(c, now, lines, 1, force);
                plan["action"] = "restore_mswtest"; plan["item"] = it;
                pv.Add("Testauftrag ZURUECKNEHMEN (= Einzelauftrag loeschen wie im Job-Monitor), Sicherung " + name + ":");
                foreach (string l in lines) pv.Add(l);
            }
            else if (kind == "subscribe")
            {
                var ids = new List<string>();
                foreach (JToken t in (JArray)b["packageIds"])
                {
                    string pid = (string)t;
                    MswPkg p = LoadPkg(c, pid, false);
                    if (!p.Subscribed) throw new Exception(p.Name + " ist schon abbestellt - nichts zurueckzunehmen.");
                    var q = Cmd(c, "SELECT (SELECT COUNT(*) FROM SYS_Jobs WHERE ContentId = @id AND JobKind = 59), (SELECT COUNT(*) FROM VW_SYS_Kiosk_AppItems_ManagedSoftware WHERE ContentId = @id)");
                    q.Parameters.AddWithValue("@id", G(pid));
                    using (var r = q.ExecuteReader())
                    {
                        r.Read();
                        if (Int(r, 0) > 0 || Int(r, 1) > 0) throw new Exception(p.Name + " wird inzwischen genutzt (" + Int(r, 0) + " Job(s), " + Int(r, 1) + " Kiosk-Eintrag/-Eintraege) - nicht abbestellen, bitte in der Console entscheiden.");
                    }
                    ids.Add(pid);
                    pv.Add("ABBESTELLEN wie die Console (ap_ManagedSoftware_Unsubscribe): " + p.Name + " - entfernt das Abo samt Verteilungsring-Eintraegen und Skripten der Versionen; die Paketdateien loescht der ACMP-Server.");
                }
                plan["action"] = "restore_subscribe"; plan["packageIds"] = new JArray(ids.ToArray());
            }
            else if (kind == "delete" && b["manual"] != null && (bool)b["manual"])
            {
                if (LookupJob(c, id) != null) throw new Exception("Job " + id + " gibt es wieder - nichts zurueckzunehmen.");
                JObject row = (JObject)b["row"];
                var tq = Cmd(c, "SELECT COUNT(*) FROM SYS_Templates WHERE Id = @t");
                tq.Parameters.AddWithValue("@t", G((string)b["templateId"]));
                bool tplThere = Convert.ToInt32(tq.ExecuteScalar()) == 1;
                if (!tplThere && IsNullTok(b["template"])) throw new Exception("Die Vorlage des Einzelauftrags ist weg und steht nicht in der Sicherung - nicht wiederherstellbar.");
                var lq = Cmd(c, "SELECT COUNT(*) FROM SYS_Jobs_Logs WHERE JobId = @id");
                lq.Parameters.AddWithValue("@id", G(id));
                bool logThere = Convert.ToInt32(lq.ExecuteScalar()) > 0;
                plan["action"] = "restore_manual"; plan["templateThere"] = tplThere; plan["logThere"] = logThere;
                pv.Add("Geloeschten EINZELAUFTRAG WIEDER ANLEGEN mit derselben Id: \"" + row["Name"] + "\" " + id + " (Sicherung " + name + ", geloescht am " + b["zeit"] + "): " +
                       (tplThere ? "Vorlage vorhanden" : "Vorlage neu") + ", " + ((JArray)b["clientLinks"]).Count + " Client-Verknuepfung(en), " + ((JArray)b["items"]).Count + " Job-Monitor-Eintrag/Eintraege" +
                       (logThere ? " (Kopf ist noch da)" : "") + ". Paket-Protokolle (SYS_ManagedSoftware_Logs) kommen nicht zurueck.");
                pv.Add("ACHTUNG: Offene Eintraege (wartet auf Zustellung) werden damit wieder zugestellt.");
            }
            else throw new Exception("Unbekannte Art der Sicherung: " + kind);
        }
        plan["vorschau"] = pv;
        return plan;
    }

    JObject ExecRestoreMsw(JObject plan, string requester)
    {
        string action = (string)plan["action"], id = (string)plan["jobId"];
        var res = new JObject(); res["ok"] = true; res["sicherung"] = plan["backup"];
        if (action == "restore_mswtest")
        {
            using (var rc = new SqlConnection(_readCs))
            using (var wc = new SqlConnection(_writeCs))
            {
                rc.Open(); wc.Open();
                JObject o = ExecManualDeleteItem(rc, wc, (JObject)plan["item"], requester);
                res["aktion"] = "Testauftrag geloescht"; res["jobId"] = id; res["neueSicherung"] = o["sicherung"];
            }
            return res;
        }
        using (var wc = new SqlConnection(_writeCs))
        {
            wc.Open();
            if (action == "restore_mswjob")
            {
                using (SqlTransaction tx = wc.BeginTransaction())
                {
                    var k = TxCmd(wc, tx, "SELECT ISNULL(ConditionCheckSum, '') FROM SYS_Jobs WITH (UPDLOCK, ROWLOCK) WHERE Id = @id");
                    k.Parameters.AddWithValue("@id", G(id));
                    object cur = k.ExecuteScalar();
                    if (cur == null || (string)cur != (string)plan["checksum"]) throw new Exception("Job " + id + " wurde seit der Planung geaendert - zurueckgerollt.");
                    var d = TxCmd(wc, tx, "DELETE FROM SYS_Templates_Jobs WHERE Id = @tj AND JobId = @id");
                    d.Parameters.AddWithValue("@tj", G((string)plan["templateJobId"])); d.Parameters.AddWithValue("@id", G(id));
                    if (d.ExecuteNonQuery() != 1) throw new Exception("Verknuepfung nicht eindeutig - zurueckgerollt.");
                    var v = TxCmd(wc, tx, "SELECT COUNT(*) FROM SYS_Jobs WHERE Id = @id");
                    v.Parameters.AddWithValue("@id", G(id));
                    if (Convert.ToInt32(v.ExecuteScalar()) != 0) throw new Exception("Der ACMP-Trigger hat den Job nicht geloescht - zurueckgerollt.");
                    if ((bool)plan["templateNew"])
                    {
                        var t = TxCmd(wc, tx, "DELETE FROM SYS_Templates WHERE Id = @t AND NOT EXISTS (SELECT 1 FROM SYS_Templates_Jobs x WHERE x.TemplateId = @t)");
                        t.Parameters.AddWithValue("@t", G((string)plan["templateId"]));
                        t.ExecuteNonQuery();   // Zeilenzahl enthaelt Kaskade und Trigger - Ergebnis unten nachzaehlen
                        var tc = TxCmd(wc, tx, "SELECT COUNT(*) FROM SYS_Templates WHERE Id = @t");
                        tc.Parameters.AddWithValue("@t", G((string)plan["templateId"]));
                        res["vorlageGeloescht"] = Convert.ToInt32(tc.ExecuteScalar()) == 0;
                    }
                    tx.Commit();
                }
                res["aktion"] = "angelegten " + ((string)plan["kind"] == "ccjob" ? "Client-Command-Job" : "MSW-Job") + " geloescht"; res["jobId"] = id;
            }
            else if (action == "restore_subscribe")
            {
                var ids = new List<string>(); foreach (JToken t in (JArray)plan["packageIds"]) ids.Add((string)t);
                using (SqlTransaction tx = wc.BeginTransaction())
                {
                    var e = TxCmd(wc, tx, "EXEC ap_ManagedSoftware_Unsubscribe @aCatalogIds = @x");
                    e.Parameters.Add(XmlPar("@x", IdsXml(ids)));
                    e.ExecuteNonQuery();
                    foreach (string pid in ids)
                    {
                        var q = TxCmd(wc, tx, "SELECT COUNT(*) FROM SYS_ManagedSoftware_Subscriptions WHERE Id = @id");
                        q.Parameters.AddWithValue("@id", G(pid));
                        if (Convert.ToInt32(q.ExecuteScalar()) != 0) throw new Exception("Kontrolle: Paket " + pid + " noch abonniert - zurueckgerollt.");
                    }
                    tx.Commit();
                }
                res["aktion"] = "abbestellt"; res["pakete"] = plan["packageIds"];
            }
            else if (action == "restore_manual")
            {
                // Datumswerte als Text lassen (sonst wandelt Json.NET die ISO-Zeiten um und die Millisekunden gehen verloren)
                JObject b = Newtonsoft.Json.JsonConvert.DeserializeObject<JObject>(File.ReadAllText(Path.Combine(_backupDir, (string)plan["backup"])),
                    new Newtonsoft.Json.JsonSerializerSettings { DateParseHandling = Newtonsoft.Json.DateParseHandling.None });
                JObject row = (JObject)b["row"];
                using (SqlTransaction tx = wc.BeginTransaction())
                {
                    var ex = TxCmd(wc, tx, "SELECT COUNT(*) FROM SYS_Jobs WHERE Id = @id");
                    ex.Parameters.AddWithValue("@id", G(id));
                    if (Convert.ToInt32(ex.ExecuteScalar()) != 0) throw new Exception("Job " + id + " gibt es inzwischen wieder - zurueckgerollt.");
                    string tpl = (string)b["templateId"];
                    var tq = TxCmd(wc, tx, "SELECT COUNT(*) FROM SYS_Templates WHERE Id = @t");
                    tq.Parameters.AddWithValue("@t", G(tpl));
                    if (Convert.ToInt32(tq.ExecuteScalar()) == 0) InsertRow(wc, tx, "SYS_Templates", (JObject)b["template"]);
                    var iq = TxCmd(wc, tx, "INSERT INTO SYS_Jobs (Id, Name, Description, Priority, Conditions, ConditionCheckSum, JobAction, CanRunParallel, ContentId, JobKind, ContainerId, Origin, IsManualJob, ServerId, CreationDate, FolderId) " +
                        "VALUES (@id, @n, @d, @p, @c, @cs, @ja, @crp, @ct, @k, NULL, @o, 1, @srv, @cd, @f)");
                    iq.Parameters.AddWithValue("@id", G(id));
                    iq.Parameters.AddWithValue("@n", (object)(string)row["Name"] ?? DBNull.Value);
                    iq.Parameters.AddWithValue("@d", (object)(string)row["Description"] ?? DBNull.Value);
                    iq.Parameters.AddWithValue("@p", (int)row["Priority"]);
                    iq.Parameters.Add(XmlPar("@c", (string)row["Conditions"]));
                    iq.Parameters.AddWithValue("@cs", (object)(string)row["ConditionCheckSum"] ?? DBNull.Value);
                    iq.Parameters.Add(XmlPar("@ja", (string)row["JobAction"]));
                    iq.Parameters.AddWithValue("@crp", (bool)row["CanRunParallel"]);
                    iq.Parameters.AddWithValue("@ct", G((string)row["ContentId"]));
                    iq.Parameters.AddWithValue("@k", (byte)(int)row["JobKind"]);
                    iq.Parameters.AddWithValue("@o", (byte)(int)row["Origin"]);
                    iq.Parameters.AddWithValue("@srv", IsNullTok(row["ServerId"]) ? (object)DBNull.Value : G((string)row["ServerId"]));
                    iq.Parameters.AddWithValue("@cd", IsNullTok(row["CreationDate"]) ? (object)DBNull.Value : (double)row["CreationDate"]);
                    iq.Parameters.AddWithValue("@f", IsNullTok(row["FolderId"]) ? (object)DBNull.Value : G((string)row["FolderId"]));
                    iq.ExecuteNonQuery();
                    var lq = TxCmd(wc, tx, "INSERT INTO SYS_Templates_Jobs (Id, TemplateId, JobId, JobKind) VALUES (@tj, @t, @id, @k)");
                    lq.Parameters.AddWithValue("@tj", G((string)b["templateJobId"])); lq.Parameters.AddWithValue("@t", G(tpl)); lq.Parameters.AddWithValue("@id", G(id));
                    lq.Parameters.AddWithValue("@k", (byte)(int)b["templateJobKind"]);
                    lq.ExecuteNonQuery();
                    foreach (JObject cl in (JArray)b["clientLinks"])
                    {
                        var cq = TxCmd(wc, tx, "SELECT COUNT(*) FROM CLT_Clients_Templates WHERE Id = @x");
                        cq.Parameters.AddWithValue("@x", G((string)cl["Id"]));
                        if (Convert.ToInt32(cq.ExecuteScalar()) == 0) InsertRow(wc, tx, "CLT_Clients_Templates", cl);
                    }
                    var hq = TxCmd(wc, tx, "SELECT COUNT(*) FROM SYS_Jobs_Logs WHERE JobId = @id");
                    hq.Parameters.AddWithValue("@id", G(id));
                    if (Convert.ToInt32(hq.ExecuteScalar()) == 0) foreach (JObject l in (JArray)b["logs"]) InsertRow(wc, tx, "SYS_Jobs_Logs", l);
                    foreach (JObject l in (JArray)b["items"])
                    {
                        var xq = TxCmd(wc, tx, "SELECT COUNT(*) FROM SYS_Jobs_Logs_Items WHERE ExecId = @x");
                        xq.Parameters.AddWithValue("@x", G((string)l["ExecId"]));
                        if (Convert.ToInt32(xq.ExecuteScalar()) == 0) InsertRow(wc, tx, "SYS_Jobs_Logs_Items", l);
                    }
                    foreach (JObject l in (JArray)b["subItems"])
                    {
                        var xq = TxCmd(wc, tx, "SELECT COUNT(*) FROM SYS_Jobs_Logs_SubItems WHERE ChildExecId = @x");
                        xq.Parameters.AddWithValue("@x", G((string)l["ChildExecId"]));
                        if (Convert.ToInt32(xq.ExecuteScalar()) == 0) InsertRow(wc, tx, "SYS_Jobs_Logs_SubItems", l);
                    }
                    var vq = TxCmd(wc, tx, "SELECT COUNT(*) FROM SYS_Templates_Jobs WHERE JobId = @id");
                    vq.Parameters.AddWithValue("@id", G(id));
                    if (Convert.ToInt32(vq.ExecuteScalar()) != 1) throw new Exception("Kontrolle nach dem Anlegen fehlgeschlagen - zurueckgerollt.");
                    tx.Commit();
                }
                res["aktion"] = "Einzelauftrag wieder angelegt"; res["jobId"] = id;
            }
            else throw new Exception("Unbekannte Ruecknahme: " + action);
        }
        _log("acmp_job_restore (" + action + ") von " + requester + ": " + id + " aus " + plan["backup"]);
        return res;
    }

    // ================= Vorpruefung fuer die MSW-Tools und Einzelauftraege =================
    static readonly string[] MswKnownTriggers = { "Trg_SYS_Templates_Delete", "TG_Delete_SYS_Templates_Jobs" };

    // f: 0 Dauerauftrag anlegen, 1 Testauftrag, 2 Einzelauftraege loeschen, 3 Einzelauftraege wiederherstellen, 4 abonnieren/abbestellen
    internal void MswPreflight(SqlConnection rc, SqlConnection wc, JArray checks, bool[] f)
    {
        var colErr = new List<string>();
        foreach (KeyValuePair<string, string[]> kv in RowSpecs)
        {
            string have = Scalar(rc, "SELECT STUFF((SELECT ',' + COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = '" + kv.Key + "' ORDER BY COLUMN_NAME FOR XML PATH('')), 1, 1, '')") ?? "";
            var want = new List<string>(); foreach (string s in kv.Value) want.Add(s.Split(':')[0]);
            want.Sort(StringComparer.Ordinal);
            var haveL = new List<string>(have.Split(',')); haveL.Sort(StringComparer.Ordinal);
            if (string.Join(",", haveL.ToArray()) != string.Join(",", want.ToArray())) colErr.Add(kv.Key + " (" + have + ")");
        }
        string ct = Scalar(rc, "SELECT STUFF((SELECT ',' + COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'SYS_Containers_Templates' ORDER BY COLUMN_NAME FOR XML PATH('')), 1, 1, '')");
        if (ct != "ContainerId,Id,TemplateId") colErr.Add("SYS_Containers_Templates (" + ct + ")");
        bool cols = colErr.Count == 0;
        Add(checks, "Tabellen Managed Software", cols ? "ok" : "warn", cols ? "SYS_Templates, CLT_Clients_Templates, SYS_Containers_Templates, SYS_Jobs_Logs(_Items/_SubItems) wie am 24.10.2026"
            : "Spalten abweichend: " + string.Join("; ", colErr.ToArray()) + " - MSW-Schreib-Tools und Einzelauftraege loeschen gesperrt");
        // Parameter der Prozeduren mit dem Schreib-Login lesen (Metadaten sieht nur, wer EXECUTE oder VIEW DEFINITION hat)
        string bulk = Scalar(wc, "SELECT STUFF((SELECT ',' + name FROM sys.parameters WHERE object_id = OBJECT_ID('dbo.ap_Job_BulkAddJobLogItems') ORDER BY parameter_id FOR XML PATH('')), 1, 1, '')");
        bool bulkOk = bulk == "@aExecIds,@aJobId,@aClientIds,@aLastActivity,@aTestRing,@aState";
        string sub = Scalar(wc, "SELECT STUFF((SELECT ',' + name FROM sys.parameters WHERE object_id IN (OBJECT_ID('dbo.ap_ManagedSoftware_Subscribe'), OBJECT_ID('dbo.ap_ManagedSoftware_Unsubscribe')) ORDER BY object_id, parameter_id FOR XML PATH('')), 1, 1, '')");
        bool subOk = sub == "@aCatalogIds,@aCatalogIds";
        Add(checks, "Prozeduren Managed Software", bulkOk && subOk ? "ok" : "warn", "ap_Job_BulkAddJobLogItems(" + (bulk ?? "fehlt") + "), ap_ManagedSoftware_Subscribe/_Unsubscribe(" + (sub ?? "fehlt") + ")" +
            (bulkOk ? "" : " - Testauftrag gesperrt") + (subOk ? "" : " - Abonnieren gesperrt"));
        var trg = new List<string>(); var unknown = new List<string>();
        var tq = rc.CreateCommand();
        tq.CommandText = "SELECT OBJECT_NAME(t.parent_id) + '.' + t.name FROM sys.triggers t WHERE t.is_disabled = 0 AND t.parent_id IN (OBJECT_ID('dbo.SYS_Templates'), OBJECT_ID('dbo.CLT_Clients_Templates'), " +
            "OBJECT_ID('dbo.SYS_Containers_Templates'), OBJECT_ID('dbo.SYS_Jobs_Logs'), OBJECT_ID('dbo.SYS_Jobs_Logs_Items'), OBJECT_ID('dbo.SYS_Jobs_Logs_SubItems'), OBJECT_ID('dbo.SYS_ManagedSoftware_Subscriptions'), OBJECT_ID('dbo.SYS_Templates_Jobs'))";
        using (var r = tq.ExecuteReader()) while (r.Read()) trg.Add(r.GetString(0));
        foreach (string t in trg) if (Array.IndexOf(MswKnownTriggers, t.Substring(t.IndexOf('.') + 1)) < 0) unknown.Add(t);
        Add(checks, "Trigger Managed Software", unknown.Count == 0 ? "ok" : "warn", unknown.Count == 0 ? "nur bekannte: " + string.Join(", ", trg.ToArray())
            : "unbekannte Trigger: " + string.Join(", ", unknown.ToArray()) + " - MSW-Schreib-Tools gesperrt, erst pruefen");
        bool base0 = cols && unknown.Count == 0;
        Func<string, string, bool> P = delegate(string o, string perm) { return HasPerm(wc, "dbo." + o, perm, null); };
        bool tplIns = P("SYS_Templates", "INSERT"), tplSel = P("SYS_Templates", "SELECT"), tplKinds = HasPerm(wc, "dbo.SYS_Templates", "UPDATE", "JobKinds"), tplDel = P("SYS_Templates", "DELETE");
        bool ctSel = P("SYS_Containers_Templates", "SELECT"), ctIns = P("SYS_Containers_Templates", "INSERT");
        bool jobIns = P("SYS_Jobs", "INSERT"), jobSel = P("SYS_Jobs", "SELECT");
        bool tjIns = P("SYS_Templates_Jobs", "INSERT"), tjSel = P("SYS_Templates_Jobs", "SELECT"), tjDel = P("SYS_Templates_Jobs", "DELETE");
        bool logIns = P("SYS_Jobs_Logs", "INSERT"), logSel = P("SYS_Jobs_Logs", "SELECT"), logDel = P("SYS_Jobs_Logs", "DELETE");
        bool itSel = P("SYS_Jobs_Logs_Items", "SELECT"), itIns = P("SYS_Jobs_Logs_Items", "INSERT"), itDel = P("SYS_Jobs_Logs_Items", "DELETE");
        bool siSel = P("SYS_Jobs_Logs_SubItems", "SELECT"), siIns = P("SYS_Jobs_Logs_SubItems", "INSERT"), siDel = P("SYS_Jobs_Logs_SubItems", "DELETE");
        bool clSel = P("CLT_Clients_Templates", "SELECT"), clIns = P("CLT_Clients_Templates", "INSERT");
        bool bulkEx = P("ap_Job_BulkAddJobLogItems", "EXECUTE"), subEx = P("ap_ManagedSoftware_Subscribe", "EXECUTE"), unsubEx = P("ap_ManagedSoftware_Unsubscribe", "EXECUTE");
        bool subsSel = P("SYS_ManagedSoftware_Subscriptions", "SELECT"), contSel = P("CLT_CONTAINER_STRUCTURE", "SELECT");
        f[0] = base0 && tplIns && tplSel && tplKinds && ctSel && ctIns && jobIns && jobSel && tjIns && tjSel && logIns && contSel;
        f[1] = base0 && bulkOk && tplIns && jobIns && jobSel && tjIns && clIns && clSel && logIns && bulkEx && itSel;
        f[2] = base0 && jobSel && tjSel && tjDel && tplDel && tplSel && ctSel && logSel && logDel && itSel && itDel && siSel && siDel;
        f[3] = base0 && tplIns && jobIns && tjIns && clIns && clSel && logIns && logSel && itIns && itSel && siIns && siSel;
        f[4] = subOk && subEx && unsubEx && subsSel;
        Add(checks, "Rechte MSW-Dauerauftrag", f[0] ? "ok" : "warn", f[0] ? "INSERT/UPDATE(JobKinds) auf SYS_Templates, SELECT/INSERT auf SYS_Containers_Templates, INSERT auf SYS_Jobs_Logs vorhanden" : "fehlen - acmp_msw_job_create gesperrt (grant-msw-writer.sql)");
        Add(checks, "Rechte MSW-Testauftrag", f[1] ? "ok" : "warn", f[1] ? "INSERT auf CLT_Clients_Templates, EXECUTE auf ap_Job_BulkAddJobLogItems vorhanden" : "fehlen - acmp_msw_test_install gesperrt (grant-msw-writer.sql)");
        Add(checks, "Rechte Einzelauftraege loeschen", f[2] ? "ok" : "warn", f[2] ? "DELETE auf SYS_Jobs_Logs(_Items/_SubItems) und SYS_Templates vorhanden" : "fehlen - acmp_job_delete fuer einmalige Jobs gesperrt (grant-msw-writer.sql)");
        Add(checks, "Rechte Einzelauftraege wiederherstellen", f[3] ? "ok" : "warn", f[3] ? "INSERT auf SYS_Jobs_Logs(_Items/_SubItems) vorhanden" : "fehlen - acmp_job_restore fuer geloeschte Einzelauftraege gesperrt (grant-msw-writer.sql)");
        Add(checks, "Rechte MSW-Abo", f[4] ? "ok" : "warn", f[4] ? "EXECUTE auf ap_ManagedSoftware_Subscribe/_Unsubscribe vorhanden" : "fehlen - acmp_msw_subscribe gesperrt (grant-msw-writer.sql)");
    }
}
