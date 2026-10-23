// AcmpDb: read-only Zugriff auf die ACMP-SQL-DB fuer kuratierte Report-Tools.
// Nur parametrisierte SELECTs gegen unbedenkliche Tabellen (keine Secret-/Key-Tabellen).
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

class AcmpDb
{
    readonly string _cs;
    public AcmpDb(string connectionString) { _cs = connectionString; }
    public bool Enabled { get { return !string.IsNullOrEmpty(_cs); } }

    JArray Query(string sql, params SqlParameter[] pars)
    {
        var arr = new JArray();
        using (var c = new SqlConnection(_cs))
        {
            c.Open();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = sql; cmd.CommandTimeout = 30;
                if (pars != null && pars.Length > 0) cmd.Parameters.AddRange(pars);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        var o = new JObject();
                        for (int i = 0; i < r.FieldCount; i++)
                        {
                            object v = r.GetValue(i);
                            o[r.GetName(i)] = (v == null || v is DBNull) ? (JToken)JValue.CreateNull() : new JValue(v.ToString());
                        }
                        arr.Add(o);
                    }
                }
            }
        }
        return arr;
    }

    // Akzeptiert ClientId-GUID ODER Computername; gibt die ClientId-GUID als String zurueck (oder null).
    public string ResolveClient(string client)
    {
        if (string.IsNullOrEmpty(client)) return null;
        Guid g;
        if (Guid.TryParse(client.Trim('{', '}'), out g)) return g.ToString();
        var arr = Query("SELECT TOP 1 CLIENTID FROM CLT_CLIENTS_TABLE WHERE COMPUTERNAME=@n AND IsRetired=0",
                        new SqlParameter("@n", client));
        if (arr.Count > 0) return (string)arr[0]["CLIENTID"];
        return null;
    }

    // Reverse: ClientId-GUID -> Computername (oder null). Fuer lesbare Freigabe-Beschreibungen.
    public string ClientName(string guid)
    {
        if (string.IsNullOrEmpty(guid)) return null;
        Guid g;
        if (!Guid.TryParse(guid.Trim('{', '}'), out g)) return null;
        var arr = Query("SELECT TOP 1 COMPUTERNAME FROM CLT_CLIENTS_TABLE WHERE CLIENTID=@id", new SqlParameter("@id", g));
        if (arr.Count > 0) return (string)arr[0]["COMPUTERNAME"];
        return null;
    }

    // Reverse: ClientId-GUID -> letzter angemeldeter Benutzer (oder null).
    public string LastUser(string guid)
    {
        if (string.IsNullOrEmpty(guid)) return null;
        Guid g;
        if (!Guid.TryParse(guid.Trim('{', '}'), out g)) return null;
        var arr = Query("SELECT TOP 1 LASTLOGGEDONUSER FROM CLT_OPERATINGSYSTEM WHERE CLIENTID=@id", new SqlParameter("@id", g));
        if (arr.Count > 0 && arr[0]["LASTLOGGEDONUSER"] != null && arr[0]["LASTLOGGEDONUSER"].Type != JTokenType.Null)
        {
            string u = (string)arr[0]["LASTLOGGEDONUSER"];
            return string.IsNullOrEmpty(u) ? null : u;
        }
        return null;
    }

    public JArray OutdatedSoftware(string clientGuid)
    {
        if (clientGuid != null)
            return Query(
                "SELECT cat.Name AS Software, msw.VersionString AS Installiert, latest.DisplayVersion AS Verfuegbar " +
                "FROM CLT_SW_ManagedSoftware msw JOIN SYS_ManagedSoftware_Catalog cat ON cat.Id=msw.SoftwareId " +
                "OUTER APPLY (SELECT TOP 1 v.VersionId, v.DisplayVersion FROM SYS_ManagedSoftware_Versions v " +
                "  WHERE v.SoftwareId=msw.SoftwareId ORDER BY v.Version DESC, v.Revision DESC) latest " +
                "WHERE msw.ClientId=@cid AND msw.VersionId<>latest.VersionId ORDER BY cat.Name",
                new SqlParameter("@cid", new Guid(clientGuid)));
        return Query(
            "SELECT cat.Name AS Software, COUNT(*) AS VeralteteClients " +
            "FROM CLT_SW_ManagedSoftware msw JOIN SYS_ManagedSoftware_Catalog cat ON cat.Id=msw.SoftwareId " +
            "OUTER APPLY (SELECT TOP 1 v.VersionId FROM SYS_ManagedSoftware_Versions v " +
            "  WHERE v.SoftwareId=msw.SoftwareId ORDER BY v.Version DESC, v.Revision DESC) latest " +
            "WHERE msw.VersionId<>latest.VersionId GROUP BY cat.Name ORDER BY COUNT(*) DESC");
    }

    public JArray MissingUpdates(string clientGuid)
    {
        return Query(
            "SELECT loc.Name AS Update_Titel, u.Severity, u.KBId " +
            "FROM CLT_Wum_NotInstalledUpdates niu JOIN Sys_Wum_Updates u ON u.Id=niu.UpdateId " +
            "LEFT JOIN SYS_Wum_Updates_Localization loc ON loc.Id=niu.UpdateId " +
            "WHERE niu.ClientId=@cid AND u.IsSuperseded=0 AND u.IsDeclined=0 AND u.IsExpired=0 " +
            "ORDER BY u.Severity DESC, loc.Name",
            new SqlParameter("@cid", new Guid(clientGuid)));
    }

    public JArray SoftwareSearch(string name)
    {
        return Query(
            "SELECT TOP 500 cl.COMPUTERNAME AS Client, cl.COMPUTERDOMAIN AS Domaene, sw.NAME AS Software, sw.VERSION AS Version " +
            "FROM CLT_SW_SETUP css JOIN SYS_SW_SETUP sw ON sw.SWSETUPID=css.SWSETUPID " +
            "JOIN CLT_CLIENTS_TABLE cl ON cl.CLIENTID=css.CLIENTID " +
            "WHERE sw.NAME LIKE @n AND cl.IsRetired=0 ORDER BY sw.NAME, cl.COMPUTERNAME",
            new SqlParameter("@n", "%" + name + "%"));
    }

    // Generisch: fuehrt ein per JSON definiertes Report-Tool aus (parametrisiert).
    // def: { sql, args:[{name, required, kind(client|int|string), default?}] }; kind=client wird Computername->GUID aufgeloest.
    public JArray RunTool(JObject def, JObject args)
    {
        string sql = (string)def["sql"];
        var pars = new List<SqlParameter>();
        JArray argDefs = def["args"] as JArray;
        if (argDefs != null)
            foreach (JObject ad in argDefs)
            {
                string an = (string)ad["name"];
                string kind = (string)ad["kind"];
                bool req = ad["required"] != null && (bool)ad["required"];
                string def0 = ad["default"] != null ? (string)ad["default"] : null;
                JToken tok = args != null ? args[an] : null;
                bool has = tok != null && tok.Type != JTokenType.Null && !(tok.Type == JTokenType.String && string.IsNullOrEmpty((string)tok));
                if (kind == "client")
                {
                    object val = DBNull.Value;
                    if (has) { string g = ResolveClient((string)tok); if (g == null) throw new Exception("Client nicht gefunden: " + (string)tok); val = new Guid(g); }
                    else if (req) throw new Exception("Parameter '" + an + "' fehlt.");
                    pars.Add(new SqlParameter("@" + an, val));
                }
                else if (kind == "int")
                {
                    int iv;
                    if (has && int.TryParse(tok.ToString(), out iv)) pars.Add(new SqlParameter("@" + an, iv));
                    else if (def0 != null && int.TryParse(def0, out iv)) pars.Add(new SqlParameter("@" + an, iv));
                    else if (req) throw new Exception("Parameter '" + an + "' (Zahl) fehlt.");
                    // typisiert, sonst kommt NULL als nvarchar an und z.B. "-@days" scheitert ("nvarchar ist fuer den minus-Operator ungueltig")
                    else pars.Add(new SqlParameter("@" + an, System.Data.SqlDbType.Int) { Value = DBNull.Value });
                }
                else
                {
                    if (has) pars.Add(new SqlParameter("@" + an, (string)tok));
                    else if (def0 != null) pars.Add(new SqlParameter("@" + an, def0));
                    else if (req) throw new Exception("Parameter '" + an + "' fehlt.");
                    else pars.Add(new SqlParameter("@" + an, DBNull.Value));
                }
            }
        return Query(sql, pars.ToArray());
    }
}
