-- Rechte fuer acmp_container_set_restriction (Teil des Job-Writers): Client-Beschraenkung eines Containers setzen
-- ("Nur Clients der Betriebssystemfamilie erlauben" / "Alle Clients erlauben"). Ohne diese Rechte bleibt nur dieses Tool gesperrt.
-- Optional und standardmaessig aus. Direktes Schreiben in die ACMP-Datenbank ist von Aagon nicht vorgesehen:
-- vorher eine Sicherung der Datenbank anlegen.
-- Der Job-Writer nutzt denselben SQL-Login wie der CC-Writer. Deshalb zuerst grant-cc-writer.sql ausfuehren
-- (legt Login und Datenbankbenutzer acmp_mcp_ccwriter an), dann dieses Skript als SQL-Admin, Abschnitt fuer Abschnitt
-- oder alles. Ggf. den Datenbanknamen [ACMP] anpassen. Danach: setup-extra-writers.ps1 -Features jobs
--
-- SQL-Mitschnitt der ACMP-Console 6.10: die Console aendert in CLT_CONTAINER_STRUCTURE nur die Spalte ClientRestriction.
--   Der Trigger TG_CLT_CONTAINER_STRUCTURE_UpdateTenantNo setzt danach TenantNo nach (Trigger und Tabelle gehoeren dbo ->
--   Besitzerverkettung, kein UPDATE auf TenantNo noetig).
-- acmp_job_set_condition braucht KEINE neuen Rechte (dieselben wie acmp_job_set_enabled, grant-jobs-writer.sql).
--
-- Bewusst NICHT vergeben: UPDATE auf andere Spalten von CLT_CONTAINER_STRUCTURE (Name, Path, SQLFilter, OsFamily ...), INSERT, DELETE.
USE [ACMP];
GO

-- 0) Gibt es den Datenbankbenutzer? (erwartet: 1 Zeile, SQL_USER; sonst erst grant-cc-writer.sql)
SELECT name, type_desc, create_date FROM sys.database_principals WHERE name = N'acmp_mcp_ccwriter';
GO

-- 1) Vorher ansehen: Trigger auf CLT_CONTAINER_STRUCTURE. Erwartet: genau 1 Zeile, TG_CLT_CONTAINER_STRUCTURE_UpdateTenantNo.
SELECT t.name AS trigger_name, t.is_disabled
FROM sys.triggers t
WHERE t.parent_id = OBJECT_ID(N'dbo.CLT_CONTAINER_STRUCTURE');
GO

-- 2) Rechte vergeben (bricht ab, wenn der Benutzer fehlt)
IF USER_ID(N'acmp_mcp_ccwriter') IS NULL
    THROW 50000, N'Datenbankbenutzer acmp_mcp_ccwriter fehlt - zuerst grant-cc-writer.sql ausfuehren. Nichts geaendert.', 1;
GRANT SELECT ON dbo.CLT_CONTAINER_STRUCTURE TO [acmp_mcp_ccwriter];
GRANT UPDATE ON dbo.CLT_CONTAINER_STRUCTURE (ClientRestriction) TO [acmp_mcp_ccwriter];
GO

-- 3) Kontrolle: wirksame Rechte des Logins. Erwartet: die ersten zwei 1, die *_soll0-Spalten 0.
EXECUTE AS USER = N'acmp_mcp_ccwriter';
SELECT HAS_PERMS_BY_NAME(N'dbo.CLT_CONTAINER_STRUCTURE', N'OBJECT', N'SELECT')                                  AS cont_select,
       HAS_PERMS_BY_NAME(N'dbo.CLT_CONTAINER_STRUCTURE', N'OBJECT', N'UPDATE', N'ClientRestriction', N'COLUMN')  AS cont_upd_restriction,
       HAS_PERMS_BY_NAME(N'dbo.CLT_CONTAINER_STRUCTURE', N'OBJECT', N'UPDATE', N'OsFamily', N'COLUMN')           AS cont_upd_osfamily_soll0,
       HAS_PERMS_BY_NAME(N'dbo.CLT_CONTAINER_STRUCTURE', N'OBJECT', N'UPDATE', N'SQLFilter', N'COLUMN')          AS cont_upd_sqlfilter_soll0,
       HAS_PERMS_BY_NAME(N'dbo.CLT_CONTAINER_STRUCTURE', N'OBJECT', N'DELETE')                                  AS cont_delete_soll0;
REVERT;
GO

-- Danach: acmp_jobs mit status=true (bzw. Dienst-Neustart) -> Vorpruefung "Rechte Client-Beschraenkung" ok.
-- Rueckgaengig machen (nur bei Bedarf, Kommentarzeichen entfernen):
-- REVOKE UPDATE ON dbo.CLT_CONTAINER_STRUCTURE (ClientRestriction) FROM [acmp_mcp_ccwriter];
-- REVOKE SELECT ON dbo.CLT_CONTAINER_STRUCTURE FROM [acmp_mcp_ccwriter];
