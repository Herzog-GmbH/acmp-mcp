// AcmpKiosk: ACMP-Kiosk lesen und - mit kioskWriter-Section - Kiosk-Eintraege und Sichtbarkeitsfilter direkt in der ACMP-DB schreiben.
// Grundlage: research\kiosk.md (14.10.2026). Die Public API kennt keinen Kiosk; die Console schreibt direkt in SYS_Kiosk_*.
// Das Filterformat stammt aus einem Console-Lernfilter (alle 24 Kriterien) und ist per Rundlauf gegen echte Filter geprueft.
// Sicherheitsregeln wie beim CC-Writer:
//  - Schreiben nur mit dem Schreib-Login (kioskWriter.connectionString, sonst ccWriter.connectionString).
//  - Nur Antragsteller aus kioskWriter.allowedRequesters (sonst ccWriter.allowedRequesters), nur in Kiosk-Ordnern aus
//    kioskWriter.allowedGroupIds und in Ordnern, die der MCP selbst angelegt hat (acmp_kiosk_add_group, Protokoll *_groupadd_*).
//    Ordner loescht der MCP nie (DELETE auf SYS_Kiosk_Groups loescht per CASCADE alle Eintraege).
//  - Vorpruefung (ACMP-Version, Schema, Trigger, Rechte, Filter-Rundlauf an allen vorhandenen Filtern), sonst gesperrt.
//  - Nur bestaetigte Filterformen. Unbestaetigtes (UND mit mehreren Kriterien, verneinter lokaler Admin ...) wird abgelehnt.
//  - Vor jeder Aenderung Sicherung nach kioskWriter.backupDir; zurueck mit acmp_kiosk_restore.
//  - Freigabe: Der Plan (aufgeloeste SIDs, GUIDs, neue Ids) wird bei der Anfrage eingefroren und genau so geschrieben.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.DirectoryServices;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Newtonsoft.Json.Linq;
using Formatting = Newtonsoft.Json.Formatting;

// Ein Filterkriterium, wie es in SYS_Kiosk_Filters.Criterias steht
class KCrit
{
    public string Class, Id, KioskId, Value = "";
    public bool Negated;
    public Dictionary<string, string> Fields = new Dictionary<string, string>(StringComparer.Ordinal);
    public string Text;   // lesbare Beschreibung (nur Anzeige)

    public string Field(string n) { string v; return Fields.TryGetValue(n, out v) && v != null ? v : ""; }

    public JObject ToJson()
    {
        var o = new JObject();
        o["class"] = Class; o["id"] = Id; o["kioskId"] = KioskId; o["value"] = Value; o["negated"] = Negated;
        var f = new JObject(); foreach (var kv in Fields) f[kv.Key] = kv.Value; o["fields"] = f;
        if (Text != null) o["text"] = Text;
        return o;
    }

    public static KCrit FromJson(JObject o)
    {
        var c = new KCrit();
        c.Class = (string)o["class"]; c.Id = (string)o["id"]; c.KioskId = (string)o["kioskId"];
        c.Value = (string)o["value"] ?? ""; c.Negated = o["negated"] != null && (bool)o["negated"];
        c.Text = (string)o["text"];
        JObject f = o["fields"] as JObject;
        if (f != null) foreach (var p in f.Properties()) c.Fields[p.Name] = (string)p.Value;
        if (!KioskFilter.ClassFields.ContainsKey(c.Class ?? "")) throw new Exception("Unbekannte Kriterienklasse im Plan: " + c.Class);
        return c;
    }
}

// Ausdruck und XML der Console nachbilden (Vorlage: research\kiosk_filter_gen.py, Fixtures zeichengleich)
static class KioskFilter
{
    // Klassenfelder vor den Basisfeldern (Id, KioskId, Value, IsNegated, Action) - Reihenfolge laut Console
    public static readonly Dictionary<string, string[]> ClassFields = new Dictionary<string, string[]>(StringComparer.Ordinal) {
        { "TActiveDirectoryUserCriteria", new[] { "UserPrincipalName", "CommonName", "SearchParameter" } },
        { "TActiveDirectoryUserOUCriteria", new[] { "SearchParameter" } },
        { "TActiveDirectoryComputerOUCriteria", new[] { "SearchParameter" } },
        { "TActiveDirectoryUserPropertyCriteria", new[] { "PropertyName" } },
        { "TActiveDirectoryComputerPropertyCriteria", new[] { "PropertyName" } },
        { "TUserInActiveDirectoryGroupCriteria", new[] { "GroupName", "SearchParameter" } },
        { "TActiveDirectoryComputerGroupCriteria", new[] { "GroupName", "SearchParameter" } },
        { "TOperatingSystemCriteria", new[] { "CompareOperator", "MajorVersion", "MinorVersion", "BuildNo" } },
        { "TOsArchitectureCriteria", new[] { "Architecture" } },
        { "TIPv4RangeCriteria", new[] { "RangeStart", "RangeEnd" } },
        { "TLocalAdministratorCriteria", new string[0] }, { "TUserNameCriteria", new string[0] }, { "TOsTypeServerCriteria", new string[0] },
        { "TClientInDepartmentCriteria", new string[0] }, { "TClientInMaintenanceWindowCriteria", new string[0] }, { "TClientInCostCenterCriteria", new string[0] },
        { "TNetworkStatusCriteria", new string[0] }, { "TClientInLocationCriteria", new string[0] }, { "TMachineNameCriteria", new string[0] },
        { "TClientExplicitlyInContainerCriteria", new string[0] }, { "TClientInContainerCriteria", new string[0] }, { "TComputerDomainCriteria", new string[0] },
        { "TUserInLocalGroupCriteria", new string[0] }, { "TProductWithFreeLicenseCriteria", new string[0] },
    };
    static readonly string[] BaseFields = { "Id", "KioskId", "Value", "IsNegated", "Action" };

    // Standardterm Funktion(Variable, 'Wert'); Verneinung = 'not ' davor (bestaetigt)
    static readonly Dictionary<string, string[]> Simple = new Dictionary<string, string[]>(StringComparer.Ordinal) {
        { "TActiveDirectoryUserCriteria", new[] { "SameText", "UserSid" } },
        { "TUserInActiveDirectoryGroupCriteria", new[] { "ContainsText", "ActiveDirectoryGroupsOfUser" } },
        { "TActiveDirectoryComputerGroupCriteria", new[] { "ContainsText", "ActiveDirectoryGroupsOfComputer" } },
        { "TUserNameCriteria", new[] { "SameText", "UserName" } },
        { "TOsTypeServerCriteria", new[] { "SameText", "IsServerOs" } },
        { "TClientInDepartmentCriteria", new[] { "SameText", "DepartmentOfClient" } },
        { "TClientInCostCenterCriteria", new[] { "SameText", "CostCenterOfClient" } },
        { "TClientInLocationCriteria", new[] { "SameText", "LocationOfClient" } },
        { "TNetworkStatusCriteria", new[] { "SameText", "NetworkStatus" } },
        { "TMachineNameCriteria", new[] { "SameText", "MachineName" } },
        { "TClientExplicitlyInContainerCriteria", new[] { "ContainsText", "ExplicitContainerIdsOfClient" } },
        { "TClientInContainerCriteria", new[] { "ContainsText", "ContainerIdsOfClient" } },
        { "TComputerDomainCriteria", new[] { "MatchText", "DomainOfComputer" } },
        { "TUserInLocalGroupCriteria", new[] { "ContainsText", "LocalGroupsOfUser" } },
        { "TProductWithFreeLicenseCriteria", new[] { "ContainsText", "ProductsWithFreeLicense" } },
    };

    public static string Q(string v)
    {
        v = v ?? "";
        if (v.IndexOf('\'') >= 0) throw new NotSupportedException("Apostroph im Wert (" + v + ") - die Maskierung der Console ist noch nicht bestaetigt.");
        return "'" + v + "'";
    }

    public static string Term(KCrit c)
    {
        string t;
        string[] s;
        if (Simple.TryGetValue(c.Class, out s)) t = s[0] + "(" + s[1] + ", " + Q(c.Value) + ")";
        else if (c.Class == "TActiveDirectoryUserOUCriteria") t = "EndsText(" + Q(c.Value) + ", UserDN)";
        else if (c.Class == "TActiveDirectoryComputerOUCriteria") t = "EndsText(" + Q(c.Value) + ", ComputerDN)";
        else if (c.Class == "TActiveDirectoryUserPropertyCriteria") t = "KeyValueMatch(UserProperties, " + Q(c.Field("PropertyName") + "=" + c.Value) + ")";
        else if (c.Class == "TActiveDirectoryComputerPropertyCriteria") t = "KeyValueMatch(ComputerProperties, " + Q(c.Field("PropertyName") + "=" + c.Value) + ")";
        else if (c.Class == "TIPv4RangeCriteria") t = "IPv4RangeCheck(V4IPsOfComputer, " + Q(c.Field("RangeStart")) + ", " + Q(c.Field("RangeEnd")) + ")";
        else if (c.Class == "TOperatingSystemCriteria")
        {
            if (c.Negated) throw new NotSupportedException("verneintes Betriebssystem: Form noch nicht bestaetigt");
            return "(CompareVersion(OperatingSystem, '" + c.Field("MajorVersion") + "." + c.Field("MinorVersion") + "." + c.Field("BuildNo") + "') " + c.Field("CompareOperator") + " 0)";
        }
        else if (c.Class == "TOsArchitectureCriteria") return "(OSArchitecture " + (c.Negated ? "<>" : "=") + " " + Q(c.Value) + ")";  // Verneinung dreht den Operator (bestaetigt)
        else if (c.Class == "TLocalAdministratorCriteria")
        {
            if (c.Negated) throw new NotSupportedException("verneinter lokaler Administrator: Form noch nicht bestaetigt");
            return "(IsLocalAdmin = '1')";
        }
        else if (c.Class == "TClientInMaintenanceWindowCriteria")
        {
            if (c.Negated) throw new NotSupportedException("verneintes Wartungsfenster: Form noch nicht bestaetigt");
            return "(ClientInMaintenanceWindow = '1')";
        }
        else throw new NotSupportedException("unbekannte Kriterienklasse " + c.Class);
        return c.Negated ? "not " + t : t;
    }

    public static string Expression(string join, List<KCrit> cs)
    {
        if (cs.Count == 0) return "";
        string sep = join == "jkOr" ? " OR " : join == "jkAnd" ? " AND " : null;
        if (sep == null) throw new NotSupportedException("Verknuepfung " + join + " nicht unterstuetzt");
        var terms = new List<string>(); foreach (KCrit c in cs) terms.Add(Term(c));
        return "(" + string.Join(sep, terms.ToArray()) + ")";
    }

    static string Esc(string s) { return (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;"); }
    static string El(string n, string t) { return string.IsNullOrEmpty(t) ? "<" + n + "/>" : "<" + n + ">" + Esc(t) + "</" + n + ">"; }

    static string SearchParam(string baseDn)
    {
        return "<SearchParameter><TActiveDirectorySearchParameter>" + El("BaseDN", baseDn) +
               "<GlobalAccountId/><RunOnGlobalCatalog>true</RunOnGlobalCatalog>" +
               "<SearchAccountType>ADSearchAccountType_DefaultAccount</SearchAccountType><Action>eaNone</Action>" +
               "</TActiveDirectorySearchParameter></SearchParameter>";
    }

    public static string CriteriaXml(List<KCrit> cs)
    {
        var sb = new StringBuilder();
        foreach (KCrit c in cs)
        {
            sb.Append('<').Append(c.Class).Append('>');
            foreach (string f in ClassFields[c.Class])
                sb.Append(f == "SearchParameter" ? SearchParam(c.Field("BaseDN")) : El(f, c.Field(f)));
            sb.Append(El("Id", c.Id)).Append(El("KioskId", c.KioskId)).Append(El("Value", c.Value));
            sb.Append("<IsNegated>").Append(c.Negated ? "true" : "false").Append("</IsNegated><Action>eaAdd</Action></").Append(c.Class).Append('>');
        }
        string list = sb.Length > 0 ? "<TFilterCriteriaList>" + sb + "</TFilterCriteriaList>" : "<TFilterCriteriaList/>";
        return "<TKioskFilterItemContainer>" + list + "<Action>eaEdit</Action></TKioskFilterItemContainer>";
    }

    public static string RootGroupXml(string join, List<KCrit> cs)
    {
        var sb = new StringBuilder();
        foreach (KCrit c in cs) sb.Append("<TKioskFilterItem><Id>").Append(c.Id).Append("</Id><Action>eaAdd</Action></TKioskFilterItem>");
        string list = sb.Length > 0 ? "<TAcmpDataList>" + sb + "</TAcmpDataList>" : "<TAcmpDataList/>";
        return "<TKioskFilterGroup><Id/><JoinKind>" + join + "</JoinKind>" + list + "<Action>eaEdit</Action></TKioskFilterGroup>";
    }

    static string Child(XmlNode n, string path) { XmlNode x = n.SelectSingleNode(path); return x != null ? x.InnerText : ""; }

    // Gespeicherten Filter in das Modell zerlegen. Unbekanntes (Klasse, Feld, verschachtelte Gruppe) -> NotSupportedException.
    public static void Parse(string rootXml, string critXml, out string join, out List<KCrit> cs)
    {
        var rg = new XmlDocument(); rg.LoadXml(rootXml);
        join = Child(rg.DocumentElement, "JoinKind");
        var order = new List<string>();
        XmlNode list = rg.DocumentElement.SelectSingleNode("TAcmpDataList");
        if (list != null)
            foreach (XmlNode n in list.ChildNodes)
            {
                if (n.NodeType != XmlNodeType.Element) continue;
                if (n.Name != "TKioskFilterItem") throw new NotSupportedException("verschachtelte Filtergruppe (" + n.Name + ")");
                order.Add(Child(n, "Id"));
            }
        var byId = new Dictionary<string, KCrit>(StringComparer.OrdinalIgnoreCase);
        var cd = new XmlDocument(); cd.LoadXml(critXml);
        XmlNode cl = cd.DocumentElement.SelectSingleNode("TFilterCriteriaList");
        if (cl != null)
            foreach (XmlNode e in cl.ChildNodes)
            {
                if (e.NodeType != XmlNodeType.Element) continue;
                string[] cf;
                if (!ClassFields.TryGetValue(e.Name, out cf)) throw new NotSupportedException("unbekannte Kriterienklasse " + e.Name);
                foreach (XmlNode ch in e.ChildNodes)
                    if (ch.NodeType == XmlNodeType.Element && Array.IndexOf(cf, ch.Name) < 0 && Array.IndexOf(BaseFields, ch.Name) < 0)
                        throw new NotSupportedException(e.Name + " hat unbekanntes Feld " + ch.Name);
                var c = new KCrit(); c.Class = e.Name;
                foreach (string f in cf)
                    if (f == "SearchParameter") c.Fields["BaseDN"] = Child(e, "SearchParameter/TActiveDirectorySearchParameter/BaseDN");
                    else c.Fields[f] = Child(e, f);
                c.Id = Child(e, "Id"); c.KioskId = Child(e, "KioskId"); c.Value = Child(e, "Value");
                c.Negated = Child(e, "IsNegated") == "true";
                byId[c.Id] = c;
            }
        if (order.Count != byId.Count) throw new NotSupportedException("RootGroup und Criterias passen nicht zusammen");
        cs = new List<KCrit>();
        foreach (string id in order)
        {
            KCrit c;
            if (!byId.TryGetValue(id, out c)) throw new NotSupportedException("Kriterium " + id + " fehlt in Criterias");
            cs.Add(c);
        }
    }

    // Lesbarer Text eines Kriteriums. nameOf loest GUIDs (Container, Stammdaten) in Namen auf, darf null liefern.
    public static string Describe(KCrit c, Func<string, string> nameOf)
    {
        Func<string, string> nm = delegate(string g) { string n = nameOf != null ? nameOf(g) : null; return n != null ? "\"" + n + "\"" : g; };
        string t;
        switch (c.Class)
        {
            case "TActiveDirectoryUserCriteria": t = "AD-Benutzer " + c.Field("CommonName") + " (" + c.Field("UserPrincipalName") + ")"; break;
            case "TActiveDirectoryUserOUCriteria": t = "Benutzer in " + c.Value; break;
            case "TActiveDirectoryComputerOUCriteria": t = "Computer in " + c.Value; break;
            case "TActiveDirectoryUserPropertyCriteria": t = "AD-Benutzereigenschaft " + c.Field("PropertyName") + " = \"" + c.Value + "\""; break;
            case "TActiveDirectoryComputerPropertyCriteria": t = "AD-Computereigenschaft " + c.Field("PropertyName") + " = \"" + c.Value + "\""; break;
            case "TUserInActiveDirectoryGroupCriteria": t = "Benutzer in AD-Gruppe " + c.Field("GroupName"); break;
            case "TActiveDirectoryComputerGroupCriteria": t = "Computer in AD-Gruppe " + c.Field("GroupName"); break;
            case "TLocalAdministratorCriteria": t = "Benutzer ist lokaler Administrator"; break;
            case "TUserNameCriteria": t = "Benutzername = " + c.Value; break;
            case "TOperatingSystemCriteria": t = "Betriebssystem " + c.Field("CompareOperator") + " " + c.Field("MajorVersion") + "." + c.Field("MinorVersion") + "." + c.Field("BuildNo") + (c.Value.Length > 0 ? " (" + c.Value + ")" : ""); break;
            case "TOsTypeServerCriteria": t = "Server-Betriebssystem"; break;
            case "TOsArchitectureCriteria": t = "Architektur = " + c.Value; break;
            case "TClientInDepartmentCriteria": t = "Client in Abteilung " + nm(c.Value); break;
            case "TClientInCostCenterCriteria": t = "Client in Kostenstelle " + nm(c.Value); break;
            case "TClientInLocationCriteria": t = "Client am Standort " + nm(c.Value); break;
            case "TClientInMaintenanceWindowCriteria": t = "Client im Wartungsfenster"; break;
            case "TNetworkStatusCriteria": t = "Netzwerkstatus " + c.Value; break;
            case "TMachineNameCriteria": t = "Computername = " + c.Value; break;
            case "TClientExplicitlyInContainerCriteria": t = "Client direkt in Container " + nm(c.Value); break;
            case "TClientInContainerCriteria": t = "Client in Container " + nm(c.Value) + " (mit Unterstruktur)"; break;
            case "TComputerDomainCriteria": t = "Domaene/Arbeitsgruppe passt zu \"" + c.Value + "\""; break;
            case "TIPv4RangeCriteria": t = "IPv4 von " + c.Field("RangeStart") + " bis " + c.Field("RangeEnd"); break;
            case "TUserInLocalGroupCriteria": t = "Benutzer in lokaler Gruppe " + c.Value; break;
            case "TProductWithFreeLicenseCriteria": t = "freie Lizenz fuer Produkt " + c.Value; break;
            default: t = c.Class + " " + c.Value; break;
        }
        return (c.Negated ? "NICHT " : "") + t;
    }

    public static string JoinText(string join) { return join == "jkOr" ? "ODER" : join == "jkAnd" ? "UND" : join; }
}

// Aufloesung von Namen (AD, Container, Stammdaten). Eigene Schnittstelle, damit der Offline-Test ohne AD/DB laeuft.
interface IKioskLookup
{
    JObject Group(string s);                    // {dn, sid}
    JObject User(string s);                     // {dn, sid, upn, cn}
    string Dn(string dn);                       // vorhandener DN (kanonisch), sonst Exception
    JObject Container(string s);                // {id, name}
    JObject MasterData(string kind, string s);  // kind: Departments | Locations | CostCenters -> {id, name}
}

class AdDbLookup : IKioskLookup
{
    readonly string _readCs;
    public AdDbLookup(string readCs) { _readCs = readCs; }

    static string LdapEsc(string s)
    {
        var sb = new StringBuilder();
        foreach (char ch in s)
            if (ch == '\\') sb.Append("\\5c"); else if (ch == '*') sb.Append("\\2a"); else if (ch == '(') sb.Append("\\28");
            else if (ch == ')') sb.Append("\\29"); else if (ch == '\0') sb.Append("\\00"); else sb.Append(ch);
        return sb.ToString();
    }

    static string Nc()
    {
        using (var r = new DirectoryEntry("LDAP://RootDSE")) return (string)r.Properties["defaultNamingContext"].Value;
    }

    // Genau ein Treffer, sonst Fehler mit Hinweis
    static JObject One(string filter, string what)
    {
        using (var root = new DirectoryEntry("LDAP://" + Nc()))
        using (var ds = new DirectorySearcher(root, filter, new[] { "distinguishedName", "objectSid", "userPrincipalName", "cn" }))
        {
            ds.SizeLimit = 5;
            using (SearchResultCollection res = ds.FindAll())
            {
                if (res.Count == 0) throw new Exception(what + " nicht im AD gefunden.");
                if (res.Count > 1)
                {
                    var l = new List<string>(); foreach (SearchResult r in res) l.Add((string)r.Properties["distinguishedName"][0]);
                    throw new Exception(what + " ist nicht eindeutig: " + string.Join(" | ", l.ToArray()) + " - bitte DN angeben.");
                }
                SearchResult x = res[0];
                var o = new JObject();
                o["dn"] = (string)x.Properties["distinguishedName"][0];
                if (x.Properties["objectSid"].Count > 0) o["sid"] = new SecurityIdentifier((byte[])x.Properties["objectSid"][0], 0).Value;
                o["upn"] = x.Properties["userPrincipalName"].Count > 0 ? (string)x.Properties["userPrincipalName"][0] : "";
                o["cn"] = x.Properties["cn"].Count > 0 ? (string)x.Properties["cn"][0] : "";
                return o;
            }
        }
    }

    static string IdFilter(string s)
    {
        s = s.Trim();
        if (s.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)) return "(objectSid=" + LdapEsc(s) + ")";
        if (s.Contains("=") && s.Contains(",")) return "(distinguishedName=" + LdapEsc(s) + ")";
        return null;
    }

    public JObject Group(string s)
    {
        string f = IdFilter(s) ?? "(|(sAMAccountName=" + LdapEsc(s.Trim()) + ")(cn=" + LdapEsc(s.Trim()) + "))";
        JObject o = One("(&(objectCategory=group)" + f + ")", "AD-Gruppe \"" + s + "\"");
        if (o["sid"] == null) throw new Exception("AD-Gruppe \"" + s + "\" hat keine SID.");
        return o;
    }

    public JObject User(string s)
    {
        string f = IdFilter(s) ?? (s.Contains("@") ? "(userPrincipalName=" + LdapEsc(s.Trim()) + ")" : "(sAMAccountName=" + LdapEsc(s.Trim()) + ")");
        JObject o = One("(&(objectCategory=person)(objectClass=user)" + f + ")", "AD-Benutzer \"" + s + "\"");
        if (o["sid"] == null) throw new Exception("AD-Benutzer \"" + s + "\" hat keine SID.");
        return o;
    }

    public string Dn(string dn)
    {
        if (string.IsNullOrEmpty(dn) || !dn.Contains("=")) throw new Exception("\"" + dn + "\" ist kein DN (z.B. OU=IT,OU=Users,DC=example,DC=local).");
        return (string)One("(distinguishedName=" + LdapEsc(dn.Trim()) + ")", "AD-Objekt \"" + dn + "\"")["dn"];
    }

    JObject ByIdOrName(string idCol, string table, string s, string what)
    {
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            var q = c.CreateCommand();
            Guid g;
            bool isId = Guid.TryParse((s ?? "").Trim().Trim('{', '}'), out g);
            q.CommandText = "SELECT TOP 6 CAST(" + idCol + " AS varchar(36)), Name FROM " + table + " WHERE " + (isId ? idCol + "=@g" : "Name=@n");
            if (isId) q.Parameters.AddWithValue("@g", g); else q.Parameters.AddWithValue("@n", (s ?? "").Trim());
            var hits = new List<string[]>();
            using (var r = q.ExecuteReader()) while (r.Read()) hits.Add(new[] { r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1) });
            if (hits.Count == 0) throw new Exception(what + " \"" + s + "\" nicht gefunden.");
            if (hits.Count > 1) throw new Exception(what + " \"" + s + "\" ist nicht eindeutig (" + hits.Count + " Treffer) - bitte GUID angeben.");
            var o = new JObject(); o["id"] = "{" + hits[0][0].ToUpperInvariant() + "}"; o["name"] = hits[0][1];
            return o;
        }
    }

    public JObject Container(string s) { return ByIdOrName("ContainerID", "CLT_CONTAINER_STRUCTURE", s, "Container"); }

    public JObject MasterData(string kind, string s)
    {
        if (kind != "Departments" && kind != "Locations" && kind != "CostCenters") throw new Exception("Stammdatenart " + kind + " unbekannt.");
        return ByIdOrName("Id", "SYS_MasterData_" + kind, s, kind == "Departments" ? "Abteilung" : kind == "Locations" ? "Standort" : "Kostenstelle");
    }
}

partial class AcmpKiosk   // Bilder: AcmpKioskImages.cs, Aendern/Verschieben: AcmpKioskUpdate.cs
{
    readonly string _readCs, _writeCs, _backupDir;
    readonly HashSet<string> _allowedReq = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _allowedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    readonly Action<string> _log;
    readonly Func<string> _acmpVersion;
    readonly bool _allowUntested;
    JObject _preflight; DateTime _preflightAt;
    readonly object _pfLock = new object();
    static readonly object _wLock = new object();
    const int PreflightMaxAgeMinutes = 60;

    public IKioskLookup Lookup;
    public bool ConfigOk { get; private set; }
    public string DisabledReason { get; private set; }
    public bool ApprovalGate { get; private set; }
    public ICollection<string> AllowedGroups { get { return _allowedGroups; } }
    public ICollection<string> AllowedRequesters { get { return _allowedReq; } }

    public const string DefaultImageId = "{C6D18635-6BAE-443B-90CF-B8DA71DE19E7}";   // "Default icon for Kiosk software Client Command"
    public const string DefaultFolderImageId = "{ED40EC22-D366-41E5-A996-4B20E50BF973}";   // "Folder" - "Default icon for Kiosk folder"
    public const int MaxNameLength = 50;                                             // Console kuerzt Namen auf 50 Zeichen
    public static readonly string[] TestedVersions = { "6.10" };
    public static readonly string[] KnownTriggers = { "SYS_Kiosk_Items.TR_SYS_Kiosk_Items_Delete" };
    // UND mit mehreren Kriterien: Trenner ' AND ' vermutet, aber noch nicht per Console-Lernfilter bestaetigt
    public static bool AndConfirmed = false;

    static readonly Dictionary<string, string[]> ExactTables = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) {
        { "SYS_Kiosk_Items", new[] { "Id:uniqueidentifier:", "DescriptionId:uniqueidentifier:", "ImageId:uniqueidentifier:", "GroupId:uniqueidentifier:", "ItemKind:tinyint:", "ContentId:uniqueidentifier:" } },
        { "SYS_Kiosk_Filters", new[] { "Id:uniqueidentifier:", "RootGroup:xml:-1", "Expression:nvarchar:-1", "Criterias:xml:-1" } },
        { "SYS_Kiosk_Filters_ActiveDirectoryUserPropertyNames", new[] { "Id:uniqueidentifier:", "KioskId:uniqueidentifier:", "PropertyName:nvarchar:200" } },
        { "SYS_Kiosk_Filters_ActiveDirectoryComputerPropertyNames", new[] { "Id:uniqueidentifier:", "KioskId:uniqueidentifier:", "PropertyName:nvarchar:200" } },
        { "SYS_MultiLanguageItems", new[] { "Id:uniqueidentifier:", "Caption_de:nvarchar:500", "Caption_en:nvarchar:500" } },
        { "SYS_MultiLanguageItems_Memo", new[] { "Id:uniqueidentifier:", "Caption_de:nvarchar:-1", "Caption_en:nvarchar:-1" } },
        // Ordner anlegen (acmp_kiosk_add_group): Mandant bleibt NULL wie bei allen Console-Ordnern
        { "SYS_Kiosk_Groups", new[] { "Id:uniqueidentifier:", "ParentId:uniqueidentifier:", "ImageId:uniqueidentifier:", "TenantId:uniqueidentifier:", "TenantNo:int:" } },
    };
    static readonly Dictionary<string, string[]> ReadTables = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) {
        { "SYS_CustomImages", new[] { "Id", "Name" } },
        { "CLT_CONTAINER_STRUCTURE", new[] { "ContainerID", "Name" } },
        { "SYS_MasterData_Departments", new[] { "Id", "Name" } },
        { "SYS_MasterData_Locations", new[] { "Id", "Name" } },
        { "SYS_MasterData_CostCenters", new[] { "Id", "Name" } },
        { "SYS_SCRIPTS", new[] { "SCRIPTID", "ScriptVersion", "DESCRIPTION", "LONGDESCRIPTION", "State" } },
        { "VW_SYS_Kiosk_Items", new[] { "Id", "ContentName" } },
    };
    static readonly string[][] NeededPerms = {
        new[] { "SYS_Kiosk_Items", "SELECT" }, new[] { "SYS_Kiosk_Items", "INSERT" }, new[] { "SYS_Kiosk_Items", "DELETE" },
        new[] { "SYS_Kiosk_Filters", "SELECT" }, new[] { "SYS_Kiosk_Filters", "INSERT" }, new[] { "SYS_Kiosk_Filters", "DELETE" },
        new[] { "SYS_Kiosk_Filters_ActiveDirectoryUserPropertyNames", "SELECT" }, new[] { "SYS_Kiosk_Filters_ActiveDirectoryUserPropertyNames", "INSERT" },
        new[] { "SYS_Kiosk_Filters_ActiveDirectoryComputerPropertyNames", "SELECT" }, new[] { "SYS_Kiosk_Filters_ActiveDirectoryComputerPropertyNames", "INSERT" },
        new[] { "SYS_MultiLanguageItems", "SELECT" }, new[] { "SYS_MultiLanguageItems", "INSERT" },
        new[] { "SYS_MultiLanguageItems_Memo", "SELECT" }, new[] { "SYS_MultiLanguageItems_Memo", "INSERT" },
        new[] { "SYS_Kiosk_Groups", "SELECT" }, new[] { "SYS_Kiosk_Groups", "INSERT" } };

    public AcmpKiosk(string readCs, JObject cfg, JObject ccCfg, string cfgDir, Action<string> log, Func<string> acmpVersion)
    {
        _readCs = readCs; _log = log; _acmpVersion = acmpVersion;
        Lookup = new AdDbLookup(readCs);
        string bd = cfg != null && cfg["backupDir"] != null ? (string)cfg["backupDir"] : "kiosk-backups";
        _backupDir = Path.IsPathRooted(bd) ? bd : Path.Combine(cfgDir, bd);
        JToken gate = cfg != null && cfg["approvalGate"] != null ? cfg["approvalGate"] : (ccCfg != null ? ccCfg["approvalGate"] : null);
        ApprovalGate = gate == null || (bool)gate;
        _allowUntested = cfg != null && cfg["allowUntested"] != null && (bool)cfg["allowUntested"];
        InitImages(cfg, ccCfg);
        if (cfg == null || cfg["enabled"] == null || !(bool)cfg["enabled"]) { DisabledReason = "kioskWriter.enabled ist nicht true (nur Lesen und dryRun)"; return; }
        _writeCs = cfg["connectionString"] != null ? (string)cfg["connectionString"] : (ccCfg != null ? (string)ccCfg["connectionString"] : null);
        JArray ar = (cfg["allowedRequesters"] as JArray) ?? (ccCfg != null ? ccCfg["allowedRequesters"] as JArray : null);
        if (ar != null) foreach (JToken t in ar) _allowedReq.Add((string)t);
        JArray ag = cfg["allowedGroupIds"] as JArray;
        if (ag != null) foreach (JToken t in ag) { string g = GuidBraces((string)t); if (g != null) _allowedGroups.Add(g); }
        if (string.IsNullOrEmpty(_readCs)) { DisabledReason = "db.connectionString fehlt"; return; }
        if (string.IsNullOrEmpty(_writeCs) || _writeCs.StartsWith("HIER_")) { DisabledReason = "kein Schreib-Login (kioskWriter.connectionString oder ccWriter.connectionString)"; return; }
        if (_allowedReq.Count == 0) { DisabledReason = "keine erlaubten Antragsteller (kioskWriter.allowedRequesters oder ccWriter.allowedRequesters)"; return; }
        if (_allowedGroups.Count == 0) { DisabledReason = "kioskWriter.allowedGroupIds ist leer"; return; }
        ConfigOk = true;
    }

    // ================= Vorpruefung =================
    public void UsePreflightResult(JObject pf) { lock (_pfLock) { _preflight = pf; _preflightAt = DateTime.UtcNow; } }

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
            _log("Kiosk-Writer Vorpruefung (ACMP " + (v ?? "?") + "): " + (string)_preflight["summary"]);
            return _preflight;
        }
    }

    JObject RunPreflight(string v)
    {
        var checks = new JArray();
        bool imgOk = false, updOk = false, grpOk = false;
        try
        {
            string mm = null;
            if (!string.IsNullOrEmpty(v)) { string[] p = v.Split('.'); if (p.Length >= 2) mm = p[0] + "." + p[1]; }
            if (mm != null && Array.IndexOf(TestedVersions, mm) >= 0) Add(checks, "ACMP-Version", "ok", v);
            else Add(checks, "ACMP-Version", _allowUntested ? "warn" : "block", (mm == null ? "nicht ermittelbar" : v + " ist nicht getestet") + " (getestet: " + string.Join(", ", TestedVersions) + ")" + (_allowUntested ? " - per kioskWriter.allowUntested freigegeben" : ""));
            using (var rc = new SqlConnection(_readCs))
            using (var wc = new SqlConnection(_writeCs))
            {
                rc.Open();
                try { wc.Open(); }
                catch (Exception ex) { Add(checks, "Schreib-Login", "block", "Anmeldung fehlgeschlagen: " + ex.Message); return Result(checks); }
                string a = Scalar(rc, "SELECT @@SERVERNAME + '/' + DB_NAME()"), b = Scalar(wc, "SELECT @@SERVERNAME + '/' + DB_NAME()");
                Add(checks, "Datenbank", string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ? "ok" : "block", a + (string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ? " (Lese- und Schreib-Login gleich)" : " / Schreib-Login auf " + b));
                CheckSchema(checks, rc);
                CheckTriggers(checks, wc);
                CheckPerms(checks, wc);
                updOk = CheckUpdatePerms(checks, wc);
                grpOk = CheckGroupUpdatePerms(checks, wc);
                CheckGroups(checks, rc);
                CheckRoundtrip(checks, rc);
                try { imgOk = CheckImages(checks, rc, wc); }
                catch (Exception ex) { Add(checks, "Bilder", "warn", "Pruefung abgebrochen: " + ex.Message + " - Bild-Tools gesperrt."); }
            }
        }
        catch (Exception ex) { Add(checks, "Pruefung", "block", "Abgebrochen: " + ex.Message); }
        JObject res = Result(checks);
        res["imageTools"] = imgOk && (bool)res["ok"];
        res["updateTool"] = updOk && (bool)res["ok"];
        res["groupTool"] = grpOk && (bool)res["ok"];
        return res;
    }

    static JObject Result(JArray checks)
    {
        var blocks = new List<string>(); int warns = 0;
        foreach (JObject c in checks)
        {
            if ((string)c["result"] == "block") blocks.Add((string)c["check"] + ": " + (string)c["detail"]);
            else if ((string)c["result"] == "warn") warns++;
        }
        var o = new JObject();
        o["ok"] = blocks.Count == 0;
        o["checkedLocal"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        o["summary"] = blocks.Count == 0 ? "bestanden" + (warns > 0 ? " (" + warns + " Hinweis(e))" : "") : "GESPERRT: " + string.Join(" | ", blocks.ToArray());
        o["checks"] = checks;
        return o;
    }

    static void Add(JArray checks, string name, string result, string detail)
    {
        var c = new JObject(); c["check"] = name; c["result"] = result; c["detail"] = detail; checks.Add(c);
    }

    static string Scalar(SqlConnection c, string sql)
    {
        var q = c.CreateCommand(); q.CommandText = sql;
        object o = q.ExecuteScalar();
        return o == null || o is DBNull ? null : o.ToString();
    }

    void CheckSchema(JArray checks, SqlConnection rc)
    {
        var have = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>(ExactTables.Keys); names.AddRange(ReadTables.Keys);
        var q = rc.CreateCommand(); var ps = new List<string>();
        for (int i = 0; i < names.Count; i++) { ps.Add("@t" + i); q.Parameters.AddWithValue("@t" + i, names[i]); }
        q.CommandText = "SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA='dbo' AND TABLE_NAME IN (" + string.Join(",", ps.ToArray()) + ")";
        using (var r = q.ExecuteReader())
            while (r.Read())
            {
                Dictionary<string, string> cols;
                if (!have.TryGetValue(r.GetString(0), out cols)) { cols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); have[r.GetString(0)] = cols; }
                cols[r.GetString(1)] = r.GetString(2) + ":" + (r.IsDBNull(3) ? "" : Convert.ToInt32(r.GetValue(3)).ToString());
            }
        foreach (var kv in ExactTables)
        {
            Dictionary<string, string> cols;
            if (!have.TryGetValue(kv.Key, out cols)) { Add(checks, "Schema " + kv.Key, "block", "Tabelle fehlt."); continue; }
            var diffs = new List<string>(); var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string spec in kv.Value)
            {
                string[] p = spec.Split(':'); expected.Add(p[0]);
                string got;
                if (!cols.TryGetValue(p[0], out got)) diffs.Add("fehlt " + p[0]);
                else if (!string.Equals(got, p[1] + ":" + p[2], StringComparison.OrdinalIgnoreCase)) diffs.Add(p[0] + " ist " + got + " statt " + p[1] + ":" + p[2]);
            }
            foreach (string c in cols.Keys) if (!expected.Contains(c)) diffs.Add("zusaetzliche Spalte " + c);
            Add(checks, "Schema " + kv.Key, diffs.Count == 0 ? "ok" : "block", diffs.Count == 0 ? kv.Value.Length + " Spalten wie erwartet" : string.Join("; ", diffs.ToArray()));
        }
        foreach (var kv in ReadTables)
        {
            Dictionary<string, string> cols;
            if (!have.TryGetValue(kv.Key, out cols)) { Add(checks, "Schema " + kv.Key, "block", "Tabelle/View fehlt."); continue; }
            var miss = new List<string>(); foreach (string c in kv.Value) if (!cols.ContainsKey(c)) miss.Add(c);
            Add(checks, "Schema " + kv.Key, miss.Count == 0 ? "ok" : "block", miss.Count == 0 ? "benoetigte Spalten vorhanden" : "fehlende Spalten: " + string.Join(", ", miss.ToArray()));
        }
    }

    void CheckTriggers(JArray checks, SqlConnection wc)
    {
        var tables = new List<string>(ExactTables.Keys);
        var q = wc.CreateCommand(); var ps = new List<string>();
        for (int i = 0; i < tables.Count; i++) { ps.Add("OBJECT_ID(@t" + i + ")"); q.Parameters.AddWithValue("@t" + i, "dbo." + tables[i]); }
        q.CommandText = "SELECT OBJECT_NAME(parent_id), name, CAST(is_disabled AS int) FROM sys.triggers WHERE parent_id IN (" + string.Join(",", ps.ToArray()) + ")";
        var found = new List<string>(); var unknown = new List<string>(); bool knownSeen = false;
        using (var r = q.ExecuteReader())
            while (r.Read())
            {
                string key = r.GetString(0) + "." + r.GetString(1);
                found.Add(key + (r.GetInt32(2) != 0 ? " (deaktiviert)" : ""));
                if (Array.IndexOf(KnownTriggers, key) < 0) unknown.Add(key); else knownSeen = true;
            }
        if (unknown.Count > 0)
            Add(checks, "Trigger", _allowUntested ? "warn" : "block", "Unbekannte Trigger: " + string.Join(", ", unknown.ToArray()) + ". Ein unbekannter Trigger kann geschriebene Zeilen veraendern.");
        else if (!knownSeen)
            Add(checks, "Trigger", "warn", "TR_SYS_Kiosk_Items_Delete nicht sichtbar (fehlen Metadaten-Rechte?). Ohne ihn raeumt das Entfernen eines Eintrags Namen und Filter nicht ab.");
        else Add(checks, "Trigger", "ok", string.Join(", ", found.ToArray()));
    }

    void CheckPerms(JArray checks, SqlConnection wc)
    {
        var miss = new List<string>();
        foreach (string[] p in NeededPerms) if (!HasPerm(wc, p[0], p[1])) miss.Add(p[0] + " " + p[1]);
        Add(checks, "Rechte Schreib-Login", miss.Count == 0 ? "ok" : "block", miss.Count == 0 ? NeededPerms.Length + " benoetigte Rechte vorhanden" : "fehlen: " + string.Join(", ", miss.ToArray()) + " (grant-kiosk-writer.sql)");
        var extra = new List<string>();
        if (HasPerm(wc, "SYS_Kiosk_Groups", "DELETE")) extra.Add("DELETE auf SYS_Kiosk_Groups (loescht per CASCADE alle Eintraege eines Ordners)");
        if (HasPerm(wc, "SYS_MultiLanguageItems", "DELETE")) extra.Add("DELETE auf SYS_MultiLanguageItems");
        if (Scalar(wc, "SELECT CAST(IS_ROLEMEMBER('db_owner') AS varchar(5))") == "1") extra.Add("Mitglied von db_owner");
        if (extra.Count > 0) Add(checks, "Rechte (zu viele)", "warn", string.Join(", ", extra.ToArray()));
    }

    static bool HasPerm(SqlConnection c, string table, string perm)
    {
        var q = c.CreateCommand();
        q.CommandText = "SELECT HAS_PERMS_BY_NAME(@o, 'OBJECT', @p)";
        q.Parameters.AddWithValue("@o", "dbo." + table); q.Parameters.AddWithValue("@p", perm);
        object o = q.ExecuteScalar();
        return o != null && !(o is DBNull) && Convert.ToInt32(o) == 1;
    }

    void CheckGroups(JArray checks, SqlConnection rc)
    {
        var miss = new List<string>(); var ok = new List<string>();
        foreach (string g in _allowedGroups)
        {
            string n = GroupName(rc, g);
            if (n == null) miss.Add(g); else ok.Add("\"" + n + "\" " + g);
        }
        Add(checks, "Erlaubte Kiosk-Ordner", miss.Count == 0 ? "ok" : "block", miss.Count == 0 ? string.Join(", ", ok.ToArray()) : "nicht gefunden: " + string.Join(", ", miss.ToArray()));
    }

    // Rundlauf: jeden vorhandenen Filter zerlegen und neu erzeugen. Ausdruck anders -> sperren (Generator falsch);
    // nur XML anders oder Form nicht unterstuetzt -> Hinweis (diese Variante schreibt der MCP nicht).
    void CheckRoundtrip(JArray checks, SqlConnection rc)
    {
        var q = rc.CreateCommand();
        q.CommandText = "SELECT CAST(Id AS varchar(36)), CAST(RootGroup AS nvarchar(max)), Expression, CAST(Criterias AS nvarchar(max)) FROM SYS_Kiosk_Filters";
        int ok = 0; var bad = new List<string>(); var xmlDiff = new List<string>(); var unsupported = new List<string>();
        using (var r = q.ExecuteReader())
            while (r.Read())
            {
                string id = r.GetString(0);
                string root = r.IsDBNull(1) ? "" : r.GetString(1), expr = r.IsDBNull(2) ? "" : r.GetString(2), crit = r.IsDBNull(3) ? "" : r.GetString(3);
                try
                {
                    string join; List<KCrit> cs;
                    KioskFilter.Parse(root, crit, out join, out cs);
                    if (KioskFilter.Expression(join, cs) != expr) { bad.Add(id); continue; }
                    if (KioskFilter.CriteriaXml(cs) != crit || KioskFilter.RootGroupXml(join, cs) != root) { xmlDiff.Add(id); continue; }
                    ok++;
                }
                catch (NotSupportedException ex) { unsupported.Add(id + " (" + ex.Message + ")"); }
                catch (Exception ex) { bad.Add(id + " (" + ex.Message + ")"); }
            }
        if (bad.Count > 0) Add(checks, "Filter-Rundlauf", "block", "Ausdruck weicht ab bei " + string.Join(", ", bad.ToArray()) + " - der Generator bildet diese Filter nicht wie die Console nach.");
        else Add(checks, "Filter-Rundlauf", "ok", ok + " Filter zeichengleich nachgebildet");
        if (xmlDiff.Count > 0) Add(checks, "Filter-Rundlauf XML", "warn", "Ausdruck gleich, XML-Variante anders (z.B. Suchbasis): " + string.Join(", ", xmlDiff.ToArray()));
        if (unsupported.Count > 0) Add(checks, "Filter-Rundlauf Varianten", "warn", "nicht unterstuetzte Formen (schreibt der MCP nicht): " + string.Join("; ", unsupported.ToArray()));
    }

    // ================= Lesen =================
    static string GroupName(SqlConnection c, string groupId)
    {
        var q = c.CreateCommand();
        q.CommandText = "SELECT g.Id, m.Caption_de FROM SYS_Kiosk_Groups g LEFT JOIN SYS_MultiLanguageItems m ON m.Id=g.Id WHERE g.Id=@g";
        q.Parameters.AddWithValue("@g", new Guid(groupId.Trim('{', '}')));
        using (var r = q.ExecuteReader()) return r.Read() ? (r.IsDBNull(1) ? "" : r.GetString(1)) : null;
    }

    Dictionary<string, string> _names;   // GUID -> Name (Container, Stammdaten) fuer lesbare Filter
    string NameOf(string g)
    {
        if (_names == null)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var c = new SqlConnection(_readCs))
            {
                c.Open();
                foreach (string sql in new[] {
                    "SELECT CAST(ContainerID AS varchar(36)), Name FROM CLT_CONTAINER_STRUCTURE",
                    "SELECT CAST(Id AS varchar(36)), Name FROM SYS_MasterData_Departments",
                    "SELECT CAST(Id AS varchar(36)), Name FROM SYS_MasterData_Locations",
                    "SELECT CAST(Id AS varchar(36)), Name FROM SYS_MasterData_CostCenters" })
                {
                    var q = c.CreateCommand(); q.CommandText = sql;
                    using (var r = q.ExecuteReader()) while (r.Read()) if (!r.IsDBNull(1)) d["{" + r.GetString(0).ToUpperInvariant() + "}"] = r.GetString(1);
                }
            }
            _names = d;
        }
        string n; return g != null && _names.TryGetValue(g.Trim(), out n) ? n : null;
    }

    JObject FilterJson(string id, string root, string expr, string crit)
    {
        var o = new JObject();
        try
        {
            string join; List<KCrit> cs;
            KioskFilter.Parse(root, crit, out join, out cs);
            if (cs.Count == 0) { o["leer"] = true; return o; }
            o["verknuepfung"] = KioskFilter.JoinText(join);
            var arr = new JArray(); foreach (KCrit c in cs) arr.Add(KioskFilter.Describe(c, NameOf)); o["kriterien"] = arr;
        }
        catch (Exception ex) { o["hinweis"] = "Filter nicht lesbar: " + ex.Message; }
        o["ausdruck"] = expr;
        return o;
    }

    // Kiosk-Baum: Ordner, Eintraege, Namen, verknuepfter Command (freigegebene Version), Filter im Klartext, Warnungen
    public JObject Tree(string groupArg, bool details)
    {
        _names = null;
        var groups = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        var filters = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
        var warnings = new JArray();
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            var q = c.CreateCommand();
            q.CommandText = "SELECT CAST(Id AS varchar(36)), CAST(RootGroup AS nvarchar(max)), Expression, CAST(Criterias AS nvarchar(max)) FROM SYS_Kiosk_Filters";
            var rawFilters = new List<string[]>();
            using (var r = q.ExecuteReader()) while (r.Read()) rawFilters.Add(new[] { "{" + r.GetString(0).ToUpperInvariant() + "}", r.IsDBNull(1) ? "" : r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2), r.IsDBNull(3) ? "" : r.GetString(3) });
            foreach (string[] f in rawFilters) filters[f[0]] = FilterJson(f[0], f[1], f[2], f[3]);

            q = c.CreateCommand();
            q.CommandText = "SELECT CAST(g.Id AS varchar(36)), CAST(g.ParentId AS varchar(36)), m.Caption_de, m.Caption_en, CAST(g.ImageId AS varchar(36)), ci.Name " +
                "FROM SYS_Kiosk_Groups g LEFT JOIN SYS_MultiLanguageItems m ON m.Id=g.Id LEFT JOIN SYS_CustomImages ci ON ci.Id=g.ImageId ORDER BY m.Caption_de";
            using (var r = q.ExecuteReader())
                while (r.Read())
                {
                    string id = "{" + r.GetString(0).ToUpperInvariant() + "}";
                    var g = new JObject();
                    g["id"] = id; g["name"] = r.IsDBNull(2) ? "" : r.GetString(2);
                    if (!r.IsDBNull(3) && r.GetString(3) != g["name"].ToString()) g["name_en"] = r.GetString(3);
                    if (!r.IsDBNull(1)) g["parentId"] = "{" + r.GetString(1).ToUpperInvariant() + "}";
                    if (details && !r.IsDBNull(4)) g["icon"] = (r.IsDBNull(5) ? "?" : "\"" + r.GetString(5) + "\"") + " {" + r.GetString(4).ToUpperInvariant() + "}";
                    JObject gf; bool hasF = filters.TryGetValue(id, out gf) && gf["leer"] == null;
                    g["sichtbarkeit"] = hasF ? (JToken)gf : "alle";
                    g["mcpDarfSchreiben"] = IsAllowedGroup(id);
                    if (CreatedByMcp(id)) g["vomMcpAngelegt"] = true;
                    g["eintraege"] = new JArray();
                    groups[id] = g; order.Add(id);
                }

            // freigegebene CC-Versionen der verknuepften Commands
            var released = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            q = c.CreateCommand();
            q.CommandText = "SELECT SCRIPTID, MAX(ScriptVersion) FROM SYS_SCRIPTS WHERE State=7 AND SCRIPTID IN (SELECT '{' + CAST(ContentId AS varchar(36)) + '}' FROM SYS_Kiosk_Items WHERE ItemKind=4) GROUP BY SCRIPTID";
            using (var r = q.ExecuteReader()) while (r.Read()) released[r.GetString(0)] = r.GetInt32(1);

            q = c.CreateCommand();
            q.CommandText = "SELECT CAST(i.Id AS varchar(36)), CAST(i.GroupId AS varchar(36)), CAST(i.ItemKind AS int), CAST(i.ContentId AS varchar(36)), m.Caption_de, m.Caption_en, " +
                "(SELECT TOP 1 v.ContentName FROM VW_SYS_Kiosk_Items v WHERE v.Id=i.Id), LEFT(mm.Caption_de, 300), CAST(i.ImageId AS varchar(36)), ci.Name " +
                "FROM SYS_Kiosk_Items i LEFT JOIN SYS_MultiLanguageItems m ON m.Id=i.Id LEFT JOIN SYS_MultiLanguageItems_Memo mm ON mm.Id=i.DescriptionId " +
                "LEFT JOIN SYS_CustomImages ci ON ci.Id=i.ImageId";
            var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var r = q.ExecuteReader())
                while (r.Read())
                {
                    string id = "{" + r.GetString(0).ToUpperInvariant() + "}"; owned.Add(id);
                    string gid = r.IsDBNull(1) ? null : "{" + r.GetString(1).ToUpperInvariant() + "}";
                    int kind = r.GetInt32(2);
                    string content = r.IsDBNull(3) ? null : "{" + r.GetString(3).ToUpperInvariant() + "}";
                    var it = new JObject();
                    it["id"] = id;
                    it["name"] = r.IsDBNull(4) ? "" : r.GetString(4);
                    if (!r.IsDBNull(5) && r.GetString(5) != it["name"].ToString()) it["name_en"] = r.GetString(5);
                    it["art"] = kind == 4 ? "Client Command" : kind == 59 ? "Managed Software" : "Art " + kind;
                    var ct = new JObject(); ct["id"] = content; ct["name"] = r.IsDBNull(6) ? "" : r.GetString(6);
                    if (kind == 4)
                    {
                        int rv;
                        if (content != null && released.TryGetValue(content, out rv)) ct["freigegebeneVersion"] = rv;
                        else { ct["freigegebeneVersion"] = null; warnings.Add("\"" + it["name"] + "\" zeigt auf einen Client Command ohne freigegebene Version (" + ct["name"] + ")."); }
                    }
                    it["inhalt"] = ct;
                    JObject f; bool hasF = filters.TryGetValue(id, out f) && f["leer"] == null;
                    it["sichtbarkeit"] = hasF ? (JToken)f : "wie Ordner";
                    if (details)
                    {
                        if (!r.IsDBNull(7) && r.GetString(7).Length > 0) it["beschreibung"] = r.GetString(7);
                        it["imageId"] = r.IsDBNull(8) ? null : "{" + r.GetString(8).ToUpperInvariant() + "}";
                        if (!r.IsDBNull(9)) it["icon"] = r.GetString(9);
                    }
                    JObject g;
                    if (gid != null && groups.TryGetValue(gid, out g)) ((JArray)g["eintraege"]).Add(it);
                    else warnings.Add("Eintrag \"" + it["name"] + "\" " + id + " haengt an keinem vorhandenen Ordner.");
                }
            foreach (string fid in filters.Keys)
                if (!owned.Contains(fid) && !groups.ContainsKey(fid)) warnings.Add("Filterzeile " + fid + " gehoert zu keinem Eintrag oder Ordner.");
        }
        if (!details)   // Ausdruck nur auf Wunsch (lang)
            foreach (JObject g in groups.Values)
            {
                JObject gf = g["sichtbarkeit"] as JObject; if (gf != null) gf.Remove("ausdruck");
                foreach (JObject it in (JArray)g["eintraege"]) { JObject f = it["sichtbarkeit"] as JObject; if (f != null) f.Remove("ausdruck"); }
            }
        var outGroups = new JArray();
        foreach (string id in order)
        {
            JObject g = groups[id];
            if (!string.IsNullOrEmpty(groupArg) && !string.Equals(id, GuidBraces(groupArg) ?? "", StringComparison.OrdinalIgnoreCase) && !string.Equals((string)g["name"], groupArg.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            outGroups.Add(g);
        }
        var o = new JObject();
        o["ordner"] = outGroups;
        if (warnings.Count > 0) o["warnungen"] = warnings;
        o["schreiber"] = WriterInfo(false);
        return o;
    }

    public JObject WriterInfo(bool refresh)
    {
        var o = new JObject();
        if (!ConfigOk) { o["status"] = "aus"; o["grund"] = DisabledReason; return o; }
        JObject pf = Preflight(refresh);
        o["status"] = pf != null && (bool)pf["ok"] ? "aktiv" : "gesperrt";
        o["vorpruefung"] = pf != null ? pf["summary"] : null;
        o["erlaubteOrdner"] = new JArray(new List<string>(_allowedGroups).ToArray());
        List<string> own = McpCreatedGroups();
        if (own.Count > 0) o["vomMcpAngelegteOrdner"] = new JArray(own.ToArray());
        o["freigabeGate"] = ApprovalGate;
        if (refresh) o["pruefungen"] = pf != null ? pf["checks"] : null;
        return o;
    }

    // ================= Planen (Aufloesen, Pruefen, Vorschau) =================
    public const string FilterHelp =
        "Sichtbarkeitsfilter als JSON: {\"join\":\"or\",\"criteria\":[{\"type\":\"userGroup\",\"group\":\"Buchhaltung\"},{\"type\":\"userOu\",\"ou\":\"OU=IT,OU=Users,DC=example,DC=local\"}]}. " +
        "join: or (Standard) | and (UND nur mit EINEM Kriterium, mehrere noch nicht bestaetigt). Jedes Kriterium kann \"not\":true haben. Typen: " +
        "userGroup {group: Name|DN|SID} Benutzer in AD-Gruppe; computerGroup {group}; user {user: sAMAccountName|UPN|DN} bestimmter AD-Benutzer; " +
        "userOu {ou: DN} Benutzer unterhalb OU/Container; computerOu {ou}; userName {value}; computerName {value} (exakt); " +
        "container {container: Name|GUID, includeSub: true|false} Client in ACMP-Container; department|location|costCenter {value: Name|GUID}; " +
        "domain {value: Muster}; ipRange {from, to}; serverOs {}; networkStatus {value: ntMobile}; localAdmin {} (nicht verneinbar); " +
        "maintenanceWindow {} (nicht verneinbar); localGroup {value}; userProperty|computerProperty {property, value}; " +
        "operatingSystem {version: \"10.0.22621\", op: \"=\"|\"<>\"|\"<\"|\">\"|\"<=\"|\">=\", label} (nicht verneinbar). " +
        "AD-Gruppen, Benutzer, OUs, Container und Stammdaten werden aufgeloest und geprueft (SID/GUID); Werte duerfen keinen Apostroph enthalten.";

    static string NewGuid() { return "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}"; }

    static string Req(JObject o, string key, string type)
    {
        string v = o[key] != null && o[key].Type != JTokenType.Null ? o[key].ToString().Trim() : "";
        if (v.Length == 0) throw new Exception("Kriterium " + type + ": '" + key + "' fehlt oder ist leer.");
        foreach (char ch in v) if (char.IsControl(ch)) throw new Exception("Kriterium " + type + ": '" + key + "' enthaelt Steuerzeichen.");
        KioskFilter.Q(v);   // Apostroph -> abgelehnt
        return v;
    }

    static readonly Regex Ipv4 = new Regex(@"^(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(\.(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3}$");
    static readonly Regex AttrName = new Regex(@"^[A-Za-z][A-Za-z0-9-]*$");
    static readonly Regex Version3 = new Regex(@"^(\d{1,5})\.(\d{1,5})\.(\d{1,6})$");

    // Filter-JSON -> Kriterien mit aufgeloesten Werten. newId erzeugt die Kriterium-Ids (Test: feste Ids).
    public static JObject BuildFilter(JToken spec, string kioskId, IKioskLookup lk, Func<string> newId)
    {
        JObject s = spec as JObject;
        if (s == null) throw new Exception("filter muss ein JSON-Objekt sein. " + FilterHelp);
        JArray arr = s["criteria"] as JArray;
        if (arr == null || arr.Count == 0) throw new Exception("filter.criteria fehlt oder ist leer (zum Entfernen des Filters acmp_kiosk_set_filter mit clear=true).");
        string j = s["join"] != null ? ((string)s["join"] ?? "").Trim().ToLowerInvariant() : "or";
        string join = j == "or" || j == "oder" ? "jkOr" : j == "and" || j == "und" ? "jkAnd" : null;
        if (join == null) throw new Exception("filter.join muss 'or' oder 'and' sein.");
        if (join == "jkAnd" && arr.Count > 1 && !AndConfirmed)
            throw new Exception("UND mit mehreren Kriterien ist noch nicht bestaetigt (Form des Ausdrucks unbekannt). Bitte 'or' verwenden oder erst einen UND-Lernfilter in der Console auswerten.");
        var cs = new List<KCrit>();
        foreach (JToken t in arr)
        {
            JObject o = t as JObject; if (o == null) throw new Exception("Jedes Kriterium muss ein JSON-Objekt sein.");
            string type = ((string)o["type"] ?? "").Trim();
            bool neg = o["not"] != null && o["not"].Type == JTokenType.Boolean && (bool)o["not"];
            var c = new KCrit(); c.Id = newId(); c.KioskId = kioskId; c.Negated = neg;
            JObject x;
            switch (type)
            {
                case "userOu": case "computerOu":
                    c.Class = type == "userOu" ? "TActiveDirectoryUserOUCriteria" : "TActiveDirectoryComputerOUCriteria";
                    c.Value = lk.Dn(Req(o, "ou", type)); c.Fields["BaseDN"] = ""; break;
                case "userGroup": case "computerGroup":
                    c.Class = type == "userGroup" ? "TUserInActiveDirectoryGroupCriteria" : "TActiveDirectoryComputerGroupCriteria";
                    x = lk.Group(Req(o, "group", type)); c.Fields["GroupName"] = (string)x["dn"]; c.Fields["BaseDN"] = ""; c.Value = (string)x["sid"]; break;
                case "user":
                    c.Class = "TActiveDirectoryUserCriteria";
                    x = lk.User(Req(o, "user", type)); c.Fields["UserPrincipalName"] = (string)x["upn"]; c.Fields["CommonName"] = (string)x["cn"]; c.Fields["BaseDN"] = ""; c.Value = (string)x["sid"]; break;
                case "userName": c.Class = "TUserNameCriteria"; c.Value = Req(o, "value", type); break;
                case "computerName": c.Class = "TMachineNameCriteria"; c.Value = Req(o, "value", type); break;
                case "container":
                    x = lk.Container(Req(o, "container", type));
                    c.Class = o["includeSub"] != null && o["includeSub"].Type == JTokenType.Boolean && (bool)o["includeSub"] ? "TClientInContainerCriteria" : "TClientExplicitlyInContainerCriteria";
                    c.Value = (string)x["id"]; c.Text = (string)x["name"]; break;
                case "department": case "location": case "costCenter":
                    x = lk.MasterData(type == "department" ? "Departments" : type == "location" ? "Locations" : "CostCenters", Req(o, "value", type));
                    c.Class = type == "department" ? "TClientInDepartmentCriteria" : type == "location" ? "TClientInLocationCriteria" : "TClientInCostCenterCriteria";
                    c.Value = (string)x["id"]; c.Text = (string)x["name"]; break;
                case "domain": c.Class = "TComputerDomainCriteria"; c.Value = Req(o, "value", type); break;
                case "ipRange":
                    c.Class = "TIPv4RangeCriteria";
                    c.Fields["RangeStart"] = Req(o, "from", type); c.Fields["RangeEnd"] = Req(o, "to", type);
                    if (!Ipv4.IsMatch(c.Fields["RangeStart"]) || !Ipv4.IsMatch(c.Fields["RangeEnd"])) throw new Exception("ipRange: from/to muessen IPv4-Adressen sein.");
                    break;
                case "serverOs": c.Class = "TOsTypeServerCriteria"; c.Value = "1"; break;
                case "networkStatus":
                    c.Class = "TNetworkStatusCriteria"; c.Value = Req(o, "value", type);
                    if (c.Value != "ntMobile") throw new Exception("networkStatus: bisher nur 'ntMobile' bestaetigt (fuer LAN: ntMobile mit not:true).");
                    break;
                case "localAdmin": c.Class = "TLocalAdministratorCriteria"; break;
                case "maintenanceWindow": c.Class = "TClientInMaintenanceWindowCriteria"; break;
                case "localGroup": c.Class = "TUserInLocalGroupCriteria"; c.Value = Req(o, "value", type); break;
                case "userProperty": case "computerProperty":
                    c.Class = type == "userProperty" ? "TActiveDirectoryUserPropertyCriteria" : "TActiveDirectoryComputerPropertyCriteria";
                    c.Fields["PropertyName"] = Req(o, "property", type); c.Value = Req(o, "value", type);
                    if (!AttrName.IsMatch(c.Fields["PropertyName"])) throw new Exception(type + ": property muss ein AD-Attributname sein (Buchstaben, Ziffern, Bindestrich).");
                    break;
                case "operatingSystem":
                    c.Class = "TOperatingSystemCriteria";
                    Match m = Version3.Match(Req(o, "version", type));
                    if (!m.Success) throw new Exception("operatingSystem: version im Format Major.Minor.Build, z.B. 10.0.22621.");
                    string op = o["op"] != null ? ((string)o["op"] ?? "").Trim() : "=";
                    if (Array.IndexOf(new[] { "=", "<>", "<", ">", "<=", ">=" }, op) < 0) throw new Exception("operatingSystem: op muss =, <>, <, >, <= oder >= sein.");
                    c.Fields["CompareOperator"] = op; c.Fields["MajorVersion"] = m.Groups[1].Value; c.Fields["MinorVersion"] = m.Groups[2].Value; c.Fields["BuildNo"] = m.Groups[3].Value;
                    c.Value = o["label"] != null ? Req(o, "label", type) : "Windows " + m.Value;
                    break;
                default:
                    throw new Exception("Unbekannter oder (noch) nicht unterstuetzter Kriterientyp '" + type + "'. " + FilterHelp);
            }
            if (c.Text == null) c.Text = KioskFilter.Describe(c, null);
            else c.Text = KioskFilter.Describe(c, delegate(string g) { return g == c.Value ? c.Text : null; });
            KioskFilter.Term(c);   // nicht unterstuetzte Formen (Verneinung ...) hier schon ablehnen
            cs.Add(c);
        }
        var r = new JObject();
        r["join"] = join;
        var ca = new JArray(); foreach (KCrit c in cs) ca.Add(c.ToJson()); r["criteria"] = ca;
        r["expression"] = KioskFilter.Expression(join, cs);
        return r;
    }

    static List<KCrit> Crits(JObject filterPlan)
    {
        var l = new List<KCrit>();
        foreach (JObject o in (JArray)filterPlan["criteria"]) l.Add(KCrit.FromJson(o));
        return l;
    }

    static void FilterLines(JObject f, List<string> lines, string indent)
    {
        if (f == null) { lines.Add(indent + "Sichtbarkeit: wie Ordner (kein eigener Filter)"); return; }
        var cs = Crits(f);
        lines.Add(indent + (cs.Count > 1 ? "Sichtbar, wenn eines der folgenden zutrifft (" + KioskFilter.JoinText((string)f["join"]) + "):" : "Sichtbar, wenn:"));
        foreach (KCrit c in cs) lines.Add(indent + "  - " + c.Text);
        lines.Add(indent + "Ausdruck: " + (string)f["expression"]);
    }

    JObject ResolveGroup(SqlConnection c, string s)
    {
        if (string.IsNullOrEmpty(s)) throw new Exception("group fehlt (Kiosk-Ordner: GUID oder Name, siehe acmp_kiosk_tree).");
        var q = c.CreateCommand();
        string id = GuidBraces(s);
        q.CommandText = "SELECT TOP 3 CAST(g.Id AS varchar(36)), m.Caption_de FROM SYS_Kiosk_Groups g LEFT JOIN SYS_MultiLanguageItems m ON m.Id=g.Id WHERE " + (id != null ? "g.Id=@g" : "m.Caption_de=@n");
        if (id != null) q.Parameters.AddWithValue("@g", new Guid(id.Trim('{', '}'))); else q.Parameters.AddWithValue("@n", s.Trim());
        var hits = new List<string[]>();
        using (var r = q.ExecuteReader()) while (r.Read()) hits.Add(new[] { "{" + r.GetString(0).ToUpperInvariant() + "}", r.IsDBNull(1) ? "" : r.GetString(1) });
        if (hits.Count == 0) throw new Exception("Kiosk-Ordner \"" + s + "\" nicht gefunden.");
        if (hits.Count > 1) throw new Exception("Kiosk-Ordner \"" + s + "\" ist nicht eindeutig - bitte GUID angeben.");
        var o = new JObject(); o["id"] = hits[0][0]; o["name"] = hits[0][1]; return o;
    }

    static string GroupIdByName(SqlConnection c, string name)
    {
        var q = c.CreateCommand();
        q.CommandText = "SELECT TOP 1 CAST(g.Id AS varchar(36)) FROM SYS_Kiosk_Groups g JOIN SYS_MultiLanguageItems m ON m.Id=g.Id WHERE m.Caption_de=@n";
        q.Parameters.AddWithValue("@n", name);
        object o = q.ExecuteScalar();
        return o == null || o is DBNull ? null : "{" + ((string)o).ToUpperInvariant() + "}";
    }

    // Eintrag per GUID oder eindeutigem deutschem Namen
    JObject ResolveItem(SqlConnection c, string s)
    {
        if (string.IsNullOrEmpty(s)) throw new Exception("Eintrag fehlt (GUID oder Name, siehe acmp_kiosk_tree).");
        var q = c.CreateCommand();
        string id = GuidBraces(s);
        q.CommandText = "SELECT TOP 3 CAST(i.Id AS varchar(36)), m.Caption_de, CAST(i.GroupId AS varchar(36)), CAST(i.ContentId AS varchar(36)), CAST(i.ItemKind AS int) " +
            "FROM SYS_Kiosk_Items i LEFT JOIN SYS_MultiLanguageItems m ON m.Id=i.Id WHERE " + (id != null ? "i.Id=@g" : "m.Caption_de=@n");
        if (id != null) q.Parameters.AddWithValue("@g", new Guid(id.Trim('{', '}'))); else q.Parameters.AddWithValue("@n", s.Trim());
        var hits = new List<JObject>();
        using (var r = q.ExecuteReader())
            while (r.Read())
            {
                var o = new JObject();
                o["id"] = "{" + r.GetString(0).ToUpperInvariant() + "}"; o["name"] = r.IsDBNull(1) ? "" : r.GetString(1);
                o["groupId"] = r.IsDBNull(2) ? null : "{" + r.GetString(2).ToUpperInvariant() + "}";
                o["contentId"] = r.IsDBNull(3) ? null : "{" + r.GetString(3).ToUpperInvariant() + "}";
                o["kind"] = r.GetInt32(4);
                hits.Add(o);
            }
        if (hits.Count == 0) return null;
        if (hits.Count > 1) throw new Exception("Eintrag \"" + s + "\" ist nicht eindeutig - bitte GUID angeben.");
        return hits[0];
    }

    string CurrentExpression(SqlConnection c, string id)
    {
        var q = c.CreateCommand();
        q.CommandText = "SELECT Expression FROM SYS_Kiosk_Filters WHERE Id=@g";
        q.Parameters.AddWithValue("@g", new Guid(id.Trim('{', '}')));
        object o = q.ExecuteScalar();
        return o == null ? null : o is DBNull ? "" : (string)o;
    }

    void RequireAllowedGroup(string groupId, string what)
    {
        if (!ConfigOk) return;   // dryRun ohne Writer: nur Hinweis in der Vorschau
        if (!IsAllowedGroup(groupId))
            throw new Exception(what + " liegt nicht in einem freigegebenen Kiosk-Ordner (kioskWriter.allowedGroupIds: " + string.Join(", ", new List<string>(_allowedGroups).ToArray()) + ", dazu vom MCP angelegte Ordner).");
    }

    // Beschreibbar: Ordner aus kioskWriter.allowedGroupIds und Ordner, die der MCP selbst angelegt hat (Protokoll *_groupadd_<GUID>.json)
    bool IsAllowedGroup(string groupId)
    {
        return groupId != null && (_allowedGroups.Contains(groupId) || CreatedByMcp(groupId));
    }

    bool CreatedByMcp(string groupId)
    {
        if (!Directory.Exists(_backupDir)) return false;
        return Directory.GetFiles(_backupDir, "*_groupadd_" + groupId.Trim('{', '}').ToUpperInvariant() + ".json").Length > 0;
    }

    List<string> McpCreatedGroups()
    {
        var l = new List<string>();
        if (!Directory.Exists(_backupDir)) return l;
        foreach (string f in Directory.GetFiles(_backupDir, "*_groupadd_*.json"))
        {
            string n = Path.GetFileNameWithoutExtension(f);
            string g = GuidBraces(n.Substring(n.LastIndexOf('_') + 1));
            if (g != null && !l.Contains(g)) l.Add(g);
        }
        return l;
    }

    // Plan fuer ein Schreib-Tool: alles aufloesen und pruefen, nichts schreiben. "vorschau" = Zeilen fuer Freigabe/dryRun.
    public JObject Plan(string tool, JObject a)
    {
        if (string.IsNullOrEmpty(_readCs)) throw new Exception("DB-Lesezugang (db.connectionString) fehlt.");
        var p = new JObject(); var lines = new List<string>();
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            if (tool == "acmp_kiosk_add_item")
            {
                JObject g = ResolveGroup(c, (string)a["group"]);
                RequireAllowedGroup((string)g["id"], "Kiosk-Ordner \"" + g["name"] + "\"");
                JObject cc = ResolveCommand(c, (string)a["command"]);
                var dq = c.CreateCommand();
                dq.CommandText = "SELECT COUNT(*) FROM SYS_Kiosk_Items WHERE GroupId=@g AND ContentId=@c";
                dq.Parameters.AddWithValue("@g", new Guid(((string)g["id"]).Trim('{', '}'))); dq.Parameters.AddWithValue("@c", new Guid(((string)cc["id"]).Trim('{', '}')));
                if (Convert.ToInt32(dq.ExecuteScalar()) > 0) throw new Exception("\"" + cc["name"] + "\" steht schon im Kiosk-Ordner \"" + g["name"] + "\".");
                string img = ResolveImage(c, (string)a["image"]);
                string itemId = NewGuid();
                string nde = Name(a, "nameDe", (string)cc["name"]), nen = Name(a, "nameEn", nde);
                p["action"] = "add_item"; p["itemId"] = itemId; p["group"] = g; p["command"] = cc; p["imageId"] = img;
                p["nameDe"] = nde; p["nameEn"] = nen;
                p["descriptionDe"] = a["descriptionDe"] != null ? (string)a["descriptionDe"] : (string)cc["longDescription"];
                p["descriptionEn"] = a["descriptionEn"] != null ? (string)a["descriptionEn"] : (string)p["descriptionDe"];
                ((JObject)p["command"]).Remove("longDescription");
                p["filter"] = a["filter"] != null && a["filter"].Type != JTokenType.Null ? BuildFilter(a["filter"], itemId, Lookup, NewGuid) : null;
                lines.Add("Kiosk-Eintrag \"" + nde + "\"" + (nen != nde ? " (EN \"" + nen + "\")" : "") + " im Kiosk-Ordner \"" + g["name"] + "\" anlegen.");
                lines.Add("Client Command: \"" + cc["name"] + "\" " + cc["id"] + ", freigegebene Version " + cc["version"] + ".");
                lines.Add("Neue Eintrags-Id: " + itemId + ", Icon " + img + (img == DefaultImageId ? " (Standard-Icon Client Command)" : "") + ".");
                string desc = (string)p["descriptionDe"] ?? "";
                lines.Add("Beschreibung: " + (desc.Length == 0 ? "(leer)" : desc.Length > 200 ? desc.Substring(0, 200) + " ..." : desc.Trim()));
                FilterLines(p["filter"] as JObject, lines, "");
            }
            else if (tool == "acmp_kiosk_add_group")
            {
                string nde = Name(a, "nameDe", null);
                if (nde.Length == 0) throw new Exception("nameDe fehlt (Name des neuen Kiosk-Ordners).");
                string nen = Name(a, "nameEn", nde);
                string other = GroupIdByName(c, nde);
                if (other != null) throw new Exception("Einen Kiosk-Ordner \"" + nde + "\" gibt es schon (" + other + ").");
                string img = ResolveImage(c, a["image"] != null && a["image"].Type != JTokenType.Null && ((string)a["image"] ?? "").Trim().Length > 0 ? (string)a["image"] : DefaultFolderImageId);
                string groupId = NewGuid();
                p["action"] = "add_group"; p["groupId"] = groupId; p["nameDe"] = nde; p["nameEn"] = nen; p["imageId"] = img;
                p["filter"] = a["filter"] != null && a["filter"].Type != JTokenType.Null ? BuildFilter(a["filter"], groupId, Lookup, NewGuid) : null;
                lines.Add("Neuen Kiosk-Ordner \"" + nde + "\"" + (nen != nde ? " (EN \"" + nen + "\")" : "") + " auf oberster Ebene anlegen.");
                lines.Add("Neue Ordner-Id: " + groupId + ", Icon " + ImageLabel(c, img) + (img == DefaultFolderImageId ? " (Standard-Ordnersymbol)" : "") + ".");
                if (p["filter"] is JObject) FilterLines((JObject)p["filter"], lines, "");
                else lines.Add("Sichtbarkeit: ALLE Benutzer (kein Filter) - mit filter einschraenken, z.B. auf eine AD-Gruppe.");
                var iq = c.CreateCommand();
                iq.CommandText = "SELECT COUNT(*) FROM SYS_Kiosk_Items i JOIN SYS_MultiLanguageItems m ON m.Id=i.Id WHERE m.Caption_de=@n";
                iq.Parameters.AddWithValue("@n", nde);
                if (Convert.ToInt32(iq.ExecuteScalar()) > 0) lines.Add("Hinweis: Ein Kiosk-Eintrag heisst ebenfalls \"" + nde + "\" - bei target= fuer den Ordner die GUID angeben.");
                lines.Add("Danach darf der MCP in diesem Ordner schreiben (vom MCP angelegt). Loeschen nur in der ACMP-Console.");
            }
            else if (tool == "acmp_kiosk_set_filter")
            {
                string target = (string)a["target"];
                JObject it = ResolveItem(c, target);
                JObject t = new JObject();
                if (it != null) { t["id"] = it["id"]; t["kind"] = "item"; t["name"] = it["name"]; t["groupId"] = it["groupId"]; }
                else
                {
                    JObject g = ResolveGroup(c, target);
                    t["id"] = g["id"]; t["kind"] = "group"; t["name"] = g["name"]; t["groupId"] = g["id"];
                }
                RequireAllowedGroup((string)t["groupId"], (t["kind"].ToString() == "item" ? "Eintrag" : "Kiosk-Ordner") + " \"" + t["name"] + "\"");
                bool clear = a["clear"] != null && a["clear"].Type == JTokenType.Boolean && (bool)a["clear"];
                bool hasFilter = a["filter"] != null && a["filter"].Type != JTokenType.Null;
                if (clear == hasFilter) throw new Exception("Entweder filter angeben (neuer Filter) oder clear=true (Filter entfernen).");
                p["action"] = "set_filter"; p["target"] = t;
                string prev = CurrentExpression(c, (string)t["id"]);
                p["previousExpression"] = prev;   // null = keine Filterzeile; bei der Ausfuehrung verglichen
                p["filter"] = hasFilter ? BuildFilter(a["filter"], (string)t["id"], Lookup, NewGuid) : null;
                lines.Add("Sichtbarkeitsfilter " + (hasFilter ? "setzen" : "ENTFERNEN") + " fuer " + (t["kind"].ToString() == "item" ? "Kiosk-Eintrag" : "Kiosk-Ordner") + " \"" + t["name"] + "\" " + t["id"] + ".");
                lines.Add("Bisher: " + (string.IsNullOrEmpty(prev) ? "kein Filter (" + (t["kind"].ToString() == "item" ? "wie Ordner" : "fuer alle sichtbar") + ")" : prev));
                if (hasFilter) FilterLines((JObject)p["filter"], lines, "Neu: ");
                else lines.Add("Neu: kein Filter - " + (t["kind"].ToString() == "item" ? "sichtbar wie der Ordner" : "Ordner fuer ALLE sichtbar") + ".");
            }
            else if (tool == "acmp_kiosk_remove_item")
            {
                JObject it = ResolveItem(c, (string)a["item"]);
                if (it == null) throw new Exception("Kiosk-Eintrag \"" + a["item"] + "\" nicht gefunden.");
                RequireAllowedGroup((string)it["groupId"], "Eintrag \"" + it["name"] + "\"");
                p["action"] = "remove_item"; p["item"] = it;
                lines.Add("Kiosk-Eintrag \"" + it["name"] + "\" " + it["id"] + " ENTFERNEN (Client Command " + it["contentId"] + " bleibt unveraendert).");
                lines.Add("Vorher Sicherung nach kiosk-backups (Eintrag, Namen, Beschreibung, Filter); zurueck mit acmp_kiosk_restore.");
                lines.Add("Name, Beschreibung und Filter raeumt der ACMP-Trigger TR_SYS_Kiosk_Items_Delete mit ab.");
            }
            else if (tool == "acmp_kiosk_restore")
            {
                JObject b = LoadBackup((string)a["backup"]);
                p["action"] = "restore"; p["backup"] = Path.GetFileName((string)a["backup"]); p["kind"] = b["kind"];
                if ((string)b["kind"] == "item")
                {
                    JObject item = (JObject)b["item"];
                    string iid = (string)item["Id"];
                    if (ResolveItem(c, iid) != null) throw new Exception("Eintrag " + iid + " existiert noch - Wiederherstellen nur fuer entfernte Eintraege.");
                    RequireAllowedGroup((string)item["GroupId"], "Eintrag \"" + (b["names"] != null ? (string)b["names"]["de"] : iid) + "\"");
                    if (GroupName(c, (string)item["GroupId"]) == null) throw new Exception("Kiosk-Ordner " + item["GroupId"] + " gibt es nicht mehr.");
                    lines.Add("Entfernten Kiosk-Eintrag \"" + (b["names"] != null ? (string)b["names"]["de"] : "") + "\" " + iid + " wiederherstellen (aus " + p["backup"] + ").");
                }
                else if ((string)b["kind"] == "filter")
                {
                    string tid = (string)b["targetId"];
                    JObject it = ResolveItem(c, tid);
                    string gid = it != null ? (string)it["groupId"] : (GroupName(c, tid) != null ? tid : null);
                    if (gid == null) throw new Exception("Ziel " + tid + " gibt es nicht mehr.");
                    RequireAllowedGroup(gid, "Ziel " + tid);
                    p["previousExpression"] = CurrentExpression(c, tid);
                    lines.Add("Filter von " + tid + " auf den gesicherten Stand zuruecksetzen (aus " + p["backup"] + ").");
                    lines.Add("Bisher: " + (string.IsNullOrEmpty((string)p["previousExpression"]) ? "kein Filter" : (string)p["previousExpression"]));
                    lines.Add("Gesichert: " + (b["filter"] != null && b["filter"].Type != JTokenType.Null ? (string)b["filter"]["Expression"] : "kein Filter"));
                }
                else if ((string)b["kind"] == "image") PlanRestoreImage(c, b, p, lines);
                else if ((string)b["kind"] == "items") PlanRestoreItems(c, b, p, lines);
                else if ((string)b["kind"] == "group") PlanRestoreGroup(c, b, p, lines);
                else throw new Exception("Unbekannte Sicherungsart " + b["kind"]);
            }
            else if (tool == "acmp_kiosk_update_items") PlanUpdate(c, a, p, lines);
            else if (tool == "acmp_kiosk_update_group") PlanUpdateGroup(c, a, p, lines);
            else if (tool == "acmp_kiosk_add_image" || tool == "acmp_kiosk_set_image" || tool == "acmp_kiosk_remove_image") PlanImage(c, tool, a, p, lines);
            else throw new Exception("Unbekanntes Kiosk-Tool: " + tool);
        }
        if (!ConfigOk) lines.Add("Hinweis: Kiosk-Writer ist aus (" + DisabledReason + ") - nur Vorschau.");
        p["vorschau"] = new JArray(lines.ToArray());
        return p;
    }

    static string Name(JObject a, string key, string dflt)
    {
        string v = a[key] != null && a[key].Type != JTokenType.Null ? ((string)a[key] ?? "").Trim() : null;
        if (v == null) { v = (dflt ?? "").Trim(); return v.Length > MaxNameLength ? v.Substring(0, MaxNameLength) : v; }
        if (v.Length == 0) throw new Exception(key + " darf nicht leer sein.");
        if (v.Length > MaxNameLength) throw new Exception(key + " ist laenger als " + MaxNameLength + " Zeichen (Grenze der Console).");
        return v;
    }

    JObject ResolveCommand(SqlConnection c, string s)
    {
        if (string.IsNullOrEmpty(s)) throw new Exception("command fehlt (Client Command: Name oder ScriptId-GUID).");
        string id = GuidBraces(s);
        if (id == null)
        {
            var nq = c.CreateCommand();
            nq.CommandText = "SELECT DISTINCT TOP 3 SCRIPTID FROM SYS_SCRIPTS WHERE DESCRIPTION=@n";
            nq.Parameters.AddWithValue("@n", s.Trim());
            var ids = new List<string>();
            using (var r = nq.ExecuteReader()) while (r.Read()) ids.Add(r.GetString(0));
            if (ids.Count == 0) throw new Exception("Client Command \"" + s + "\" nicht gefunden.");
            if (ids.Count > 1) throw new Exception("Client Command \"" + s + "\" ist nicht eindeutig - bitte ScriptId angeben.");
            id = GuidBraces(ids[0]);
        }
        var q = c.CreateCommand();
        q.CommandText = "SELECT TOP 1 ScriptVersion, DESCRIPTION, LONGDESCRIPTION FROM SYS_SCRIPTS WHERE SCRIPTID=@id AND State=7 ORDER BY ScriptVersion DESC";
        q.Parameters.AddWithValue("@id", id);
        using (var r = q.ExecuteReader())
        {
            if (!r.Read()) throw new Exception("Client Command " + s + " hat keine freigegebene Version (State 7) - erst in der Console freigeben.");
            var o = new JObject();
            o["id"] = id; o["version"] = r.GetInt32(0); o["name"] = r.IsDBNull(1) ? "" : r.GetString(1);
            o["longDescription"] = r.IsDBNull(2) ? "" : r.GetString(2);
            return o;
        }
    }

    string ResolveImage(SqlConnection c, string s)
    {
        string id = string.IsNullOrEmpty(s) ? DefaultImageId : GuidBraces(s);
        var q = c.CreateCommand();
        q.CommandText = "SELECT TOP 3 CAST(Id AS varchar(36)) FROM SYS_CustomImages WHERE " + (id != null ? "Id=@g" : "Name=@n");
        if (id != null) q.Parameters.AddWithValue("@g", new Guid(id.Trim('{', '}'))); else q.Parameters.AddWithValue("@n", s.Trim());
        var hits = new List<string>();
        using (var r = q.ExecuteReader()) while (r.Read()) hits.Add("{" + r.GetString(0).ToUpperInvariant() + "}");
        if (hits.Count == 0) throw new Exception("Icon \"" + (s ?? DefaultImageId) + "\" nicht in SYS_CustomImages gefunden.");
        if (hits.Count > 1) throw new Exception("Icon \"" + s + "\" ist nicht eindeutig - bitte GUID angeben.");
        return hits[0];
    }

    // ================= Schreiben =================
    public void CheckWrite(string requester)
    {
        if (!ConfigOk) throw new Exception("Kiosk-Schreiben ist ausgeschaltet: " + DisabledReason);
        if (requester == null || !_allowedReq.Contains(requester)) throw new Exception("Antragsteller '" + requester + "' darf den Kiosk nicht aendern (kioskWriter.allowedRequesters).");
        JObject pf = Preflight(false);
        if (pf == null || !(bool)pf["ok"]) throw new Exception("Kiosk-Schreiben gesperrt - Vorpruefung nicht bestanden: " + (pf != null ? (string)pf["summary"] : "keine Pruefung") + " (Details: acmp_kiosk_tree mit status=true). Nichts geschrieben.");
    }

    // Fuehrt einen Plan aus (eingefroren aus der Freigabe oder frisch geplant). Prueft vorher erneut, ob sich nichts geaendert hat.
    public JObject Execute(string tool, JObject a, string requester)
    {
        CheckWrite(requester);
        JObject p = a["_plan"] as JObject;
        if (p == null) p = Plan(tool, a);
        string act = (string)p["action"];
        if (IsImageAction(act) || (act == "restore" && (string)p["kind"] == "image")) CheckImageWrite();
        if (act == "update_items" || (act == "restore" && (string)p["kind"] == "items")) CheckUpdateWrite();
        if (act == "update_group" || (act == "restore" && (string)p["kind"] == "group")) CheckGroupWrite();
        lock (_wLock)
        {
            if (act == "add_item") return ExecAdd(p, requester);
            if (act == "update_items") return ExecUpdate(p, requester);
            if (act == "update_group") return ExecUpdateGroup(p, requester);
            if (act == "add_group") return ExecAddGroup(p, requester);
            if (act == "set_filter") return ExecSetFilter(p, requester);
            if (act == "remove_item") return ExecRemove(p, requester);
            if (act == "restore") return ExecRestore(p, requester);
            if (act == "add_image") return ExecAddImage(p, requester);
            if (act == "set_image") return ExecSetImage(p, requester);
            if (act == "remove_image") return ExecRemoveImage(p, requester);
            throw new Exception("Unbekannte Plan-Aktion " + act);
        }
    }

    static Guid G(string s) { return new Guid(s.Trim('{', '}')); }

    SqlCommand Cmd(SqlConnection c, SqlTransaction tx, string sql)
    {
        var q = c.CreateCommand(); q.Transaction = tx; q.CommandText = sql; return q;
    }

    void InsertFilter(SqlConnection c, SqlTransaction tx, string kioskId, JObject f)
    {
        List<KCrit> cs = Crits(f);
        string join = (string)f["join"];
        string expr = KioskFilter.Expression(join, cs);
        if (expr != (string)f["expression"]) throw new Exception("Ausdruck weicht vom freigegebenen Plan ab - nichts geschrieben.");
        foreach (KCrit k in cs) if (!string.Equals(k.KioskId, kioskId, StringComparison.OrdinalIgnoreCase)) throw new Exception("Kriterium gehoert nicht zu " + kioskId);
        var q = Cmd(c, tx, "INSERT INTO SYS_Kiosk_Filters (Id, RootGroup, Expression, Criterias) VALUES (@id, @rg, @ex, @cr)");
        q.Parameters.AddWithValue("@id", G(kioskId)); q.Parameters.AddWithValue("@rg", KioskFilter.RootGroupXml(join, cs));
        q.Parameters.AddWithValue("@ex", expr); q.Parameters.AddWithValue("@cr", KioskFilter.CriteriaXml(cs));
        q.ExecuteNonQuery();
        foreach (KCrit k in cs)
        {
            string table = k.Class == "TActiveDirectoryUserPropertyCriteria" ? "SYS_Kiosk_Filters_ActiveDirectoryUserPropertyNames"
                         : k.Class == "TActiveDirectoryComputerPropertyCriteria" ? "SYS_Kiosk_Filters_ActiveDirectoryComputerPropertyNames" : null;
            if (table == null) continue;
            var pq = Cmd(c, tx, "INSERT INTO " + table + " (Id, KioskId, PropertyName) VALUES (@id, @k, @p)");
            pq.Parameters.AddWithValue("@id", G(k.Id)); pq.Parameters.AddWithValue("@k", G(kioskId)); pq.Parameters.AddWithValue("@p", k.Field("PropertyName"));
            pq.ExecuteNonQuery();
        }
    }

    JObject ExecAdd(JObject p, string requester)
    {
        string itemId = (string)p["itemId"], groupId = (string)p["group"]["id"], ccId = (string)p["command"]["id"];
        RequireAllowedGroup(groupId, "Kiosk-Ordner \"" + p["group"]["name"] + "\"");
        using (var c = new SqlConnection(_writeCs))
        {
            c.Open();
            using (var rc = new SqlConnection(_readCs))
            {
                rc.Open();
                JObject cc = ResolveCommand(rc, ccId);   // weiterhin freigegeben?
                if (GroupName(rc, groupId) == null) throw new Exception("Kiosk-Ordner " + groupId + " gibt es nicht mehr.");
                var dq = rc.CreateCommand();
                dq.CommandText = "SELECT (SELECT COUNT(*) FROM SYS_Kiosk_Items WHERE Id=@i) + (SELECT COUNT(*) FROM SYS_Kiosk_Items WHERE GroupId=@g AND ContentId=@c)";
                dq.Parameters.AddWithValue("@i", G(itemId)); dq.Parameters.AddWithValue("@g", G(groupId)); dq.Parameters.AddWithValue("@c", G(ccId));
                if (Convert.ToInt32(dq.ExecuteScalar()) > 0) throw new Exception("Eintrag existiert bereits oder \"" + cc["name"] + "\" steht schon im Ordner - nichts geschrieben.");
            }
            using (SqlTransaction tx = c.BeginTransaction())
            {
                var q = Cmd(c, tx, "INSERT INTO SYS_Kiosk_Items (Id, DescriptionId, ImageId, GroupId, ItemKind, ContentId) VALUES (@id, @id, @img, @g, 4, @c)");
                q.Parameters.AddWithValue("@id", G(itemId)); q.Parameters.AddWithValue("@img", G((string)p["imageId"]));
                q.Parameters.AddWithValue("@g", G(groupId)); q.Parameters.AddWithValue("@c", G(ccId));
                q.ExecuteNonQuery();
                q = Cmd(c, tx, "INSERT INTO SYS_MultiLanguageItems (Id, Caption_de, Caption_en) VALUES (@id, @de, @en)");
                q.Parameters.AddWithValue("@id", G(itemId)); q.Parameters.AddWithValue("@de", (string)p["nameDe"]); q.Parameters.AddWithValue("@en", (string)p["nameEn"]);
                q.ExecuteNonQuery();
                q = Cmd(c, tx, "INSERT INTO SYS_MultiLanguageItems_Memo (Id, Caption_de, Caption_en) VALUES (@id, @de, @en)");
                q.Parameters.AddWithValue("@id", G(itemId)); q.Parameters.AddWithValue("@de", (string)p["descriptionDe"] ?? ""); q.Parameters.AddWithValue("@en", (string)p["descriptionEn"] ?? "");
                q.ExecuteNonQuery();
                if (p["filter"] is JObject) InsertFilter(c, tx, itemId, (JObject)p["filter"]);
                tx.Commit();
            }
        }
        string journal = WriteJournal("add", itemId, p, requester);
        _log("acmp_kiosk_add_item von " + requester + ": " + itemId + " \"" + p["nameDe"] + "\" in " + groupId);
        var o = new JObject();
        o["ok"] = true; o["itemId"] = itemId; o["name"] = p["nameDe"]; o["ordner"] = p["group"]["name"];
        o["filter"] = p["filter"] is JObject ? p["filter"]["expression"] : (JToken)"(keiner - wie Ordner)";
        o["protokoll"] = Path.GetFileName(journal);
        o["hinweis"] = "Im Kiosk des Clients erscheint der Eintrag beim naechsten Aktualisieren. Entfernen mit acmp_kiosk_remove_item.";
        return o;
    }

    JObject ExecAddGroup(JObject p, string requester)
    {
        string gid = (string)p["groupId"], nde = (string)p["nameDe"], img = (string)p["imageId"];
        using (var c = new SqlConnection(_writeCs))
        {
            c.Open();
            using (var rc = new SqlConnection(_readCs))
            {
                rc.Open();
                if (GroupName(rc, gid) != null) throw new Exception("Kiosk-Ordner " + gid + " gibt es schon - nichts geschrieben.");
                string other = GroupIdByName(rc, nde);
                if (other != null) throw new Exception("Einen Kiosk-Ordner \"" + nde + "\" gibt es inzwischen (" + other + ") - nichts geschrieben, bitte neu beantragen.");
                if (!ImageExists(rc, img)) throw new Exception("Icon " + img + " gibt es nicht mehr - nichts geschrieben.");
            }
            using (SqlTransaction tx = c.BeginTransaction())
            {
                var q = Cmd(c, tx, "INSERT INTO SYS_Kiosk_Groups (Id, ParentId, ImageId) VALUES (@id, NULL, @img)");
                q.Parameters.AddWithValue("@id", G(gid)); q.Parameters.AddWithValue("@img", G(img));
                q.ExecuteNonQuery();
                q = Cmd(c, tx, "INSERT INTO SYS_MultiLanguageItems (Id, Caption_de, Caption_en) VALUES (@id, @de, @en)");
                q.Parameters.AddWithValue("@id", G(gid)); q.Parameters.AddWithValue("@de", nde); q.Parameters.AddWithValue("@en", (string)p["nameEn"]);
                q.ExecuteNonQuery();
                if (p["filter"] is JObject) InsertFilter(c, tx, gid, (JObject)p["filter"]);
                tx.Commit();
            }
        }
        string journal = WriteJournal("groupadd", gid, p, requester);   // Kennzeichen "vom MCP angelegt" -> Ordner beschreibbar
        _log("acmp_kiosk_add_group von " + requester + ": " + gid + " \"" + nde + "\"");
        var o = new JObject();
        o["ok"] = true; o["ordnerId"] = gid; o["name"] = nde;
        o["filter"] = p["filter"] is JObject ? p["filter"]["expression"] : (JToken)"(keiner - fuer alle sichtbar)";
        o["protokoll"] = Path.GetFileName(journal);
        o["hinweis"] = "Erscheint im Kiosk beim naechsten Aktualisieren. Eintraege: acmp_kiosk_add_item group=\"" + gid + "\". Loeschen nur in der ACMP-Console.";
        return o;
    }

    JObject ExecSetFilter(JObject p, string requester)
    {
        string tid = (string)p["target"]["id"];
        RequireAllowedGroup((string)p["target"]["groupId"], "Ziel \"" + p["target"]["name"] + "\"");
        string backup;
        using (var c = new SqlConnection(_writeCs))
        {
            c.Open();
            string now = CurrentExpression(c, tid);
            string prev = p["previousExpression"] != null && p["previousExpression"].Type != JTokenType.Null ? (string)p["previousExpression"] : null;
            if (now != prev) throw new Exception("Der Filter von \"" + p["target"]["name"] + "\" wurde seit der Anfrage geaendert (jetzt: " + (now ?? "keiner") + ") - nichts geschrieben, bitte neu beantragen.");
            backup = BackupFilter(c, tid, "vor acmp_kiosk_set_filter", requester);
            using (SqlTransaction tx = c.BeginTransaction())
            {
                var q = Cmd(c, tx, "DELETE FROM SYS_Kiosk_Filters WHERE Id=@id");   // PropertyNames per CASCADE
                q.Parameters.AddWithValue("@id", G(tid)); q.ExecuteNonQuery();
                if (p["filter"] is JObject) InsertFilter(c, tx, tid, (JObject)p["filter"]);
                tx.Commit();
            }
        }
        _log("acmp_kiosk_set_filter von " + requester + ": " + tid + " -> " + (p["filter"] is JObject ? (string)p["filter"]["expression"] : "(entfernt)"));
        var o = new JObject();
        o["ok"] = true; o["ziel"] = p["target"]["name"]; o["id"] = tid;
        o["filter"] = p["filter"] is JObject ? p["filter"]["expression"] : (JToken)"(entfernt)";
        o["sicherung"] = Path.GetFileName(backup);
        o["hinweis"] = "Zurueck mit acmp_kiosk_restore backup=\"" + Path.GetFileName(backup) + "\".";
        return o;
    }

    JObject ExecRemove(JObject p, string requester)
    {
        string iid = (string)p["item"]["id"];
        string backup;
        using (var c = new SqlConnection(_writeCs))
        {
            c.Open();
            JObject it = ResolveItem(c, iid);
            if (it == null) throw new Exception("Eintrag " + iid + " gibt es nicht mehr - nichts geschrieben.");
            RequireAllowedGroup((string)it["groupId"], "Eintrag \"" + it["name"] + "\"");
            backup = BackupItem(c, iid, "vor acmp_kiosk_remove_item", requester);
            using (SqlTransaction tx = c.BeginTransaction())
            {
                var q = Cmd(c, tx, "DELETE FROM SYS_Kiosk_Items WHERE Id=@id");   // Trigger raeumt Namen, Beschreibung, Filter ab
                q.Parameters.AddWithValue("@id", G(iid));
                if (q.ExecuteNonQuery() != 1) throw new Exception("Eintrag nicht eindeutig geloescht - zurueckgerollt.");
                tx.Commit();
            }
        }
        _log("acmp_kiosk_remove_item von " + requester + ": " + iid + " \"" + p["item"]["name"] + "\"");
        var o = new JObject();
        o["ok"] = true; o["entfernt"] = p["item"]["name"]; o["id"] = iid; o["sicherung"] = Path.GetFileName(backup);
        o["hinweis"] = "Zurueck mit acmp_kiosk_restore backup=\"" + Path.GetFileName(backup) + "\".";
        return o;
    }

    JObject ExecRestore(JObject p, string requester)
    {
        JObject b = LoadBackup((string)p["backup"]);
        if ((string)b["kind"] == "image") return ExecRestoreImage(b, (string)p["backup"], requester);
        if ((string)b["kind"] == "items") return ExecRestoreItems(b, p, requester);
        if ((string)b["kind"] == "group") return ExecRestoreGroup(b, p, requester);
        string again;
        using (var c = new SqlConnection(_writeCs))
        {
            c.Open();
            if ((string)b["kind"] == "item")
            {
                JObject item = (JObject)b["item"];
                string iid = (string)item["Id"];
                if (ResolveItem(c, iid) != null) throw new Exception("Eintrag " + iid + " existiert schon - nichts geschrieben.");
                RequireAllowedGroup((string)item["GroupId"], "Eintrag " + iid);
                again = null;
                using (SqlTransaction tx = c.BeginTransaction())
                {
                    var q = Cmd(c, tx, "INSERT INTO SYS_Kiosk_Items (Id, DescriptionId, ImageId, GroupId, ItemKind, ContentId) VALUES (@id, @d, @img, @g, @k, @c)");
                    q.Parameters.AddWithValue("@id", G(iid)); q.Parameters.AddWithValue("@d", NG((string)item["DescriptionId"]));
                    q.Parameters.AddWithValue("@img", NG((string)item["ImageId"])); q.Parameters.AddWithValue("@g", NG((string)item["GroupId"]));
                    q.Parameters.AddWithValue("@k", (byte)(int)item["ItemKind"]); q.Parameters.AddWithValue("@c", NG((string)item["ContentId"]));
                    q.ExecuteNonQuery();
                    if (b["names"] is JObject) InsertText(c, tx, "SYS_MultiLanguageItems", iid, (JObject)b["names"]);
                    if (b["memo"] is JObject && item["DescriptionId"] != null && item["DescriptionId"].Type != JTokenType.Null) InsertText(c, tx, "SYS_MultiLanguageItems_Memo", (string)item["DescriptionId"], (JObject)b["memo"]);
                    RestoreFilterRows(c, tx, b);
                    tx.Commit();
                }
            }
            else
            {
                string tid = (string)b["targetId"];
                string now = CurrentExpression(c, tid);
                string prev = p["previousExpression"] != null && p["previousExpression"].Type != JTokenType.Null ? (string)p["previousExpression"] : null;
                if (now != prev) throw new Exception("Filter wurde seit der Anfrage geaendert - nichts geschrieben.");
                again = BackupFilter(c, tid, "vor acmp_kiosk_restore", requester);
                using (SqlTransaction tx = c.BeginTransaction())
                {
                    var q = Cmd(c, tx, "DELETE FROM SYS_Kiosk_Filters WHERE Id=@id");
                    q.Parameters.AddWithValue("@id", G(tid)); q.ExecuteNonQuery();
                    RestoreFilterRows(c, tx, b);
                    tx.Commit();
                }
            }
        }
        _log("acmp_kiosk_restore von " + requester + ": " + p["backup"]);
        var o = new JObject(); o["ok"] = true; o["wiederhergestellt"] = p["backup"];
        if (again != null) o["sicherungVorher"] = Path.GetFileName(again);
        return o;
    }

    static object NG(string s) { return string.IsNullOrEmpty(s) ? (object)DBNull.Value : G(s); }

    void InsertText(SqlConnection c, SqlTransaction tx, string table, string id, JObject t)
    {
        var q = Cmd(c, tx, "INSERT INTO " + table + " (Id, Caption_de, Caption_en) VALUES (@id, @de, @en)");
        q.Parameters.AddWithValue("@id", G(id));
        q.Parameters.AddWithValue("@de", t["de"] != null && t["de"].Type != JTokenType.Null ? (object)(string)t["de"] : DBNull.Value);
        q.Parameters.AddWithValue("@en", t["en"] != null && t["en"].Type != JTokenType.Null ? (object)(string)t["en"] : DBNull.Value);
        q.ExecuteNonQuery();
    }

    void RestoreFilterRows(SqlConnection c, SqlTransaction tx, JObject b)
    {
        JObject f = b["filter"] as JObject;
        if (f == null) return;
        var q = Cmd(c, tx, "INSERT INTO SYS_Kiosk_Filters (Id, RootGroup, Expression, Criterias) VALUES (@id, @rg, @ex, @cr)");
        q.Parameters.AddWithValue("@id", G((string)f["Id"]));
        q.Parameters.AddWithValue("@rg", (object)(string)f["RootGroup"] ?? DBNull.Value); q.Parameters.AddWithValue("@ex", (object)(string)f["Expression"] ?? DBNull.Value);
        q.Parameters.AddWithValue("@cr", (object)(string)f["Criterias"] ?? DBNull.Value);
        q.ExecuteNonQuery();
        foreach (string kind in new[] { "User", "Computer" })
        {
            JArray rows = b[kind.ToLowerInvariant() + "Props"] as JArray; if (rows == null) continue;
            foreach (JObject r in rows)
            {
                var pq = Cmd(c, tx, "INSERT INTO SYS_Kiosk_Filters_ActiveDirectory" + kind + "PropertyNames (Id, KioskId, PropertyName) VALUES (@id, @k, @p)");
                pq.Parameters.AddWithValue("@id", G((string)r["Id"])); pq.Parameters.AddWithValue("@k", G((string)r["KioskId"]));
                pq.Parameters.AddWithValue("@p", (object)(string)r["PropertyName"] ?? DBNull.Value);
                pq.ExecuteNonQuery();
            }
        }
    }

    // ================= Sicherungen =================
    JObject ReadFilterRows(SqlConnection c, string id, JObject b)
    {
        var q = c.CreateCommand();
        q.CommandText = "SELECT CAST(Id AS varchar(36)), CAST(RootGroup AS nvarchar(max)), Expression, CAST(Criterias AS nvarchar(max)) FROM SYS_Kiosk_Filters WHERE Id=@g";
        q.Parameters.AddWithValue("@g", G(id));
        using (var r = q.ExecuteReader())
        {
            if (r.Read())
            {
                var f = new JObject();
                f["Id"] = "{" + r.GetString(0).ToUpperInvariant() + "}";
                f["RootGroup"] = r.IsDBNull(1) ? null : r.GetString(1); f["Expression"] = r.IsDBNull(2) ? null : r.GetString(2); f["Criterias"] = r.IsDBNull(3) ? null : r.GetString(3);
                b["filter"] = f;
            }
            else b["filter"] = null;
        }
        foreach (string kind in new[] { "User", "Computer" })
        {
            var pq = c.CreateCommand();
            pq.CommandText = "SELECT CAST(Id AS varchar(36)), CAST(KioskId AS varchar(36)), PropertyName FROM SYS_Kiosk_Filters_ActiveDirectory" + kind + "PropertyNames WHERE KioskId=@g";
            pq.Parameters.AddWithValue("@g", G(id));
            var arr = new JArray();
            using (var r = pq.ExecuteReader())
                while (r.Read()) { var o = new JObject(); o["Id"] = "{" + r.GetString(0).ToUpperInvariant() + "}"; o["KioskId"] = "{" + r.GetString(1).ToUpperInvariant() + "}"; o["PropertyName"] = r.IsDBNull(2) ? null : r.GetString(2); arr.Add(o); }
            b[kind.ToLowerInvariant() + "Props"] = arr;
        }
        return b;
    }

    string BackupFilter(SqlConnection c, string id, string reason, string requester)
    {
        var b = new JObject(); b["kind"] = "filter"; b["targetId"] = id; b["grund"] = reason; b["von"] = requester; b["zeit"] = DateTime.Now.ToString("s");
        ReadFilterRows(c, id, b);
        return SaveBackup(b, id, "filter");
    }

    string BackupItem(SqlConnection c, string id, string reason, string requester)
    {
        var b = new JObject(); b["kind"] = "item"; b["grund"] = reason; b["von"] = requester; b["zeit"] = DateTime.Now.ToString("s");
        foreach (JProperty pr in ItemSnapshot(c, id).Properties()) b[pr.Name] = pr.Value;
        return SaveBackup(b, id, "item");
    }

    // Zeile, Namen, Beschreibung und Filter eines Eintrags (Inhalt einer Sicherung, auch je Eintrag in Sicherungen der Art "items")
    JObject ItemSnapshot(SqlConnection c, string id)
    {
        var b = new JObject();
        var q = c.CreateCommand();
        q.CommandText = "SELECT CAST(Id AS varchar(36)), CAST(DescriptionId AS varchar(36)), CAST(ImageId AS varchar(36)), CAST(GroupId AS varchar(36)), CAST(ItemKind AS int), CAST(ContentId AS varchar(36)) FROM SYS_Kiosk_Items WHERE Id=@g";
        q.Parameters.AddWithValue("@g", G(id));
        string descId = null;
        using (var r = q.ExecuteReader())
        {
            if (!r.Read()) throw new Exception("Eintrag " + id + " nicht gefunden.");
            var it = new JObject();
            Func<int, JToken> gs = delegate(int i) { return r.IsDBNull(i) ? null : (JToken)("{" + r.GetString(i).ToUpperInvariant() + "}"); };
            it["Id"] = gs(0); it["DescriptionId"] = gs(1); it["ImageId"] = gs(2); it["GroupId"] = gs(3); it["ItemKind"] = r.GetInt32(4); it["ContentId"] = gs(5);
            descId = (string)it["DescriptionId"];
            b["item"] = it;
        }
        b["names"] = ReadText(c, "SYS_MultiLanguageItems", id);
        b["memo"] = descId != null ? ReadText(c, "SYS_MultiLanguageItems_Memo", descId) : null;
        ReadFilterRows(c, id, b);
        return b;
    }

    static JToken ReadText(SqlConnection c, string table, string id)
    {
        var q = c.CreateCommand();
        q.CommandText = "SELECT Caption_de, Caption_en FROM " + table + " WHERE Id=@g";
        q.Parameters.AddWithValue("@g", G(id));
        using (var r = q.ExecuteReader())
        {
            if (!r.Read()) return null;
            var o = new JObject(); o["de"] = r.IsDBNull(0) ? null : r.GetString(0); o["en"] = r.IsDBNull(1) ? null : r.GetString(1); return o;
        }
    }

    string SaveBackup(JObject b, string id, string kind)
    {
        Directory.CreateDirectory(_backupDir);
        string file = Path.Combine(_backupDir, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "_" + kind + "_" + id.Trim('{', '}') + ".json");
        File.WriteAllText(file, b.ToString(Formatting.Indented), new UTF8Encoding(false));
        if (new FileInfo(file).Length < 40) throw new Exception("Sicherung unvollstaendig - nichts geaendert.");
        return file;
    }

    string WriteJournal(string kind, string id, JObject plan, string requester)
    {
        var b = new JObject(); b["kind"] = kind; b["von"] = requester; b["zeit"] = DateTime.Now.ToString("s"); b["plan"] = plan;
        return SaveBackup(b, id, kind);
    }

    JObject LoadBackup(string name)
    {
        if (string.IsNullOrEmpty(name)) throw new Exception("backup fehlt (Dateiname aus der Antwort von acmp_kiosk_set_filter/remove_item/remove_image/update_items/update_group).");
        string fn = Path.GetFileName(name.Trim());
        if (fn != name.Trim() || !fn.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) throw new Exception("backup: nur der Dateiname (ohne Pfad) einer Sicherung.");
        string path = Path.Combine(_backupDir, fn);
        if (!File.Exists(path)) throw new Exception("Sicherung " + fn + " nicht gefunden.");
        JObject b = JObject.Parse(File.ReadAllText(path));
        string k = (string)b["kind"];
        if (k != "item" && k != "filter" && k != "image" && k != "items" && k != "group") throw new Exception(fn + " ist keine Sicherung eines Eintrags, Filters, Bildes, Ordners oder einer Sammelaenderung (Art " + k + ").");
        return b;
    }

    static string GuidBraces(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        Guid g; if (!Guid.TryParse(s.Trim().Trim('{', '}'), out g)) return null;
        return "{" + g.ToString().ToUpperInvariant() + "}";
    }
}
