-- Rechte fuer acmp_container_set_filter (Teil des Job-Writers): dynamischen Filter eines Containers setzen wie die Console.
-- Ohne diese Rechte bleibt nur dieses Tool gesperrt (die Vorschau mit dryRun geht).
-- Optional und standardmaessig aus. Direktes Schreiben in die ACMP-Datenbank ist von Aagon nicht vorgesehen:
-- vorher eine Sicherung der Datenbank anlegen.
-- Der Job-Writer nutzt denselben SQL-Login wie der CC-Writer. Deshalb zuerst grant-cc-writer.sql und grant-jobs-writer2.sql ausfuehren,
-- dann dieses Skript als SQL-Admin, Abschnitt fuer Abschnitt oder alles. Ggf. den Datenbanknamen [ACMP] anpassen.
-- Danach: setup-extra-writers.ps1 -Features jobs
--
-- SQL-Mitschnitt der ACMP-Console 6.10: beim Speichern im Reiter "Dyn. Filter" aendert die Console in CLT_CONTAINER_STRUCTURE nur SQLFilter,
-- HasClientCondition und ClientCondition und schreibt die Definition per Upsert in SYS_Queries_FilterContainer (Id = ContainerID).
-- Der Trigger TG_CLT_CONTAINER_STRUCTURE_UpdateTenantNo setzt danach TenantNo nach (Besitzerverkettung, kein UPDATE auf TenantNo noetig).
-- DELETE auf SYS_Queries_FilterContainer braucht nur acmp_job_restore, wenn ein Container vorher gar keine Filterzeile hatte.
--
-- Bewusst NICHT vergeben: UPDATE auf andere Spalten von CLT_CONTAINER_STRUCTURE (Name, Path, OsFamily, FilterKind ...), INSERT/DELETE auf
-- CLT_CONTAINER_STRUCTURE, Rechte auf CLT_CONTAINER_ITEMS (die Mitglieder rechnet der ACMP-Server neu).
USE [ACMP];
GO

-- 0) Gibt es den Datenbankbenutzer? (erwartet: 1 Zeile, SQL_USER; sonst erst grant-cc-writer.sql)
SELECT name, type_desc, create_date FROM sys.database_principals WHERE name = N'acmp_mcp_ccwriter';
GO

-- 1) Vorher ansehen: Trigger. Erwartet: genau 1 Zeile, CLT_CONTAINER_STRUCTURE / TG_CLT_CONTAINER_STRUCTURE_UpdateTenantNo
--    (auf SYS_Queries_FilterContainer keiner). Andere Zeilen: nicht weitermachen.
SELECT OBJECT_NAME(t.parent_id) AS tabelle, t.name AS trigger_name, t.is_disabled
FROM sys.triggers t
WHERE t.parent_id IN (OBJECT_ID(N'dbo.CLT_CONTAINER_STRUCTURE'), OBJECT_ID(N'dbo.SYS_Queries_FilterContainer'));
GO

-- 2) Rechte vergeben (bricht ab, wenn der Benutzer fehlt). SELECT auf CLT_CONTAINER_STRUCTURE kommt auch aus grant-jobs-writer2.sql.
IF USER_ID(N'acmp_mcp_ccwriter') IS NULL
    THROW 50000, N'Datenbankbenutzer acmp_mcp_ccwriter fehlt - zuerst grant-cc-writer.sql ausfuehren. Nichts geaendert.', 1;
GRANT SELECT ON dbo.CLT_CONTAINER_STRUCTURE TO [acmp_mcp_ccwriter];
GRANT UPDATE ON dbo.CLT_CONTAINER_STRUCTURE (SQLFilter, HasClientCondition, ClientCondition) TO [acmp_mcp_ccwriter];
GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.SYS_Queries_FilterContainer TO [acmp_mcp_ccwriter];
GO

-- 3) Kontrolle: wirksame Rechte des Logins. Erwartet: die ersten sechs 1, die *_soll0-Spalten 0.
EXECUTE AS USER = N'acmp_mcp_ccwriter';
SELECT HAS_PERMS_BY_NAME(N'dbo.CLT_CONTAINER_STRUCTURE', N'OBJECT', N'UPDATE', N'SQLFilter', N'COLUMN')           AS cont_upd_sqlfilter,
       HAS_PERMS_BY_NAME(N'dbo.CLT_CONTAINER_STRUCTURE', N'OBJECT', N'UPDATE', N'HasClientCondition', N'COLUMN')  AS cont_upd_hascond,
       HAS_PERMS_BY_NAME(N'dbo.CLT_CONTAINER_STRUCTURE', N'OBJECT', N'UPDATE', N'ClientCondition', N'COLUMN')     AS cont_upd_clientcond,
       HAS_PERMS_BY_NAME(N'dbo.SYS_Queries_FilterContainer', N'OBJECT', N'SELECT')                                AS fc_select,
       HAS_PERMS_BY_NAME(N'dbo.SYS_Queries_FilterContainer', N'OBJECT', N'INSERT')                                AS fc_insert,
       HAS_PERMS_BY_NAME(N'dbo.SYS_Queries_FilterContainer', N'OBJECT', N'UPDATE')                                AS fc_update,
       HAS_PERMS_BY_NAME(N'dbo.CLT_CONTAINER_STRUCTURE', N'OBJECT', N'UPDATE', N'Path', N'COLUMN')                AS cont_upd_path_soll0,
       HAS_PERMS_BY_NAME(N'dbo.CLT_CONTAINER_STRUCTURE', N'OBJECT', N'UPDATE', N'FilterKind', N'COLUMN')          AS cont_upd_filterkind_soll0,
       HAS_PERMS_BY_NAME(N'dbo.CLT_CONTAINER_STRUCTURE', N'OBJECT', N'DELETE')                                    AS cont_delete_soll0,
       HAS_PERMS_BY_NAME(N'dbo.CLT_CONTAINER_ITEMS', N'OBJECT', N'INSERT')                                        AS items_insert_soll0;
REVERT;
GO

-- Danach: acmp_jobs mit status=true (bzw. Dienst-Neustart) -> Vorpruefung "Rechte Container-Filter" ok.
-- Rueckgaengig machen (nur bei Bedarf, Kommentarzeichen entfernen):
-- REVOKE UPDATE ON dbo.CLT_CONTAINER_STRUCTURE (SQLFilter, HasClientCondition, ClientCondition) FROM [acmp_mcp_ccwriter];
-- REVOKE SELECT, INSERT, UPDATE, DELETE ON dbo.SYS_Queries_FilterContainer FROM [acmp_mcp_ccwriter];
