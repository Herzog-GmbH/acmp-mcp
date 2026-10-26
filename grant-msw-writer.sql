-- Rechte fuer die Managed-Software-Tools und das Loeschen einmaliger Jobs (Teil des Job-Writers):
--   acmp_msw_job_create   = Upsert SYS_Templates (neue Container-Vorlage bzw. UPDATE JobKinds), INSERT SYS_Jobs, SYS_Jobs_Logs, SYS_Templates_Jobs, SYS_Containers_Templates
--   acmp_msw_test_install = INSERT SYS_Templates (Einzelauftrags-Vorlage), SYS_Jobs, SYS_Templates_Jobs, CLT_Clients_Templates, SYS_Jobs_Logs, EXEC ap_Job_BulkAddJobLogItems
--   acmp_msw_subscribe    = EXEC ap_ManagedSoftware_Subscribe, Ruecknahme EXEC ap_ManagedSoftware_Unsubscribe (wie der ACMP-Server)
--   acmp_job_delete (einmalige Jobs) = DELETE SYS_Jobs_Logs_SubItems/_Items/SYS_Jobs_Logs je JobId (wie 'Loeschen' im Job-Monitor), DELETE SYS_Templates_Jobs
--                           (Trigger TG_Delete_SYS_Templates_Jobs loescht SYS_Jobs), DELETE SYS_Templates (leere Einzelauftrags-Vorlage, Kaskade CLT_Clients_Templates)
--   acmp_job_restore      = INSERT in dieselben Tabellen (geloeschten Einzelauftrag wiederherstellen), DELETE SYS_Templates (neu angelegte leere Vorlage)
-- Ohne diese Rechte bleiben nur diese Tools gesperrt (Lesen und dryRun gehen).
-- Optional und standardmaessig aus. Direktes Schreiben in die ACMP-Datenbank ist von Aagon nicht vorgesehen:
-- vorher eine Sicherung der Datenbank anlegen.
-- Voraussetzung: grant-cc-writer.sql und grant-jobs-writer.sql (SELECT/INSERT SYS_Jobs, UPDATE(Conditions, ConditionCheckSum),
-- SELECT SYS_Jobs_Logs + UPDATE(ConditionText, State), SELECT/INSERT/DELETE SYS_Templates_Jobs, SELECT SYS_Templates, SELECT CLT_CONTAINER_STRUCTURE).
-- Als SQL-Admin ausfuehren, Abschnitt fuer Abschnitt oder alles, ggf. den Datenbanknamen [ACMP] anpassen. Danach: setup-extra-writers.ps1 -Features jobs
--
-- Bewusst NICHT vergeben: DELETE auf SYS_Jobs, UPDATE auf SYS_Jobs.JobAction, UPDATE auf andere Spalten von SYS_Templates, Rechte auf SYS_ManagedSoftware_* ausser
-- SELECT auf SYS_ManagedSoftware_Subscriptions (die Prozeduren schreiben ueber Besitzerverkettung).
USE [ACMP];
GO

-- 0) Gibt es den Datenbankbenutzer? (erwartet: 1 Zeile, SQL_USER; sonst erst grant-cc-writer.sql)
SELECT name, type_desc FROM sys.database_principals WHERE name = N'acmp_mcp_ccwriter';
GO

-- 1) Vorher ansehen: Trigger auf den beteiligten Tabellen. Erwartet genau 2 Zeilen:
--    SYS_Templates / Trg_SYS_Templates_Delete (loescht SYS_Acls der Vorlage) und SYS_Templates_Jobs / TG_Delete_SYS_Templates_Jobs.
--    Andere Zeilen: nicht weitermachen.
SELECT OBJECT_NAME(t.parent_id) AS tabelle, t.name AS trigger_name, t.is_disabled
FROM sys.triggers t
WHERE t.parent_id IN (OBJECT_ID(N'dbo.SYS_Templates'), OBJECT_ID(N'dbo.SYS_Templates_Jobs'), OBJECT_ID(N'dbo.CLT_Clients_Templates'), OBJECT_ID(N'dbo.SYS_Containers_Templates'),
                      OBJECT_ID(N'dbo.SYS_Jobs_Logs'), OBJECT_ID(N'dbo.SYS_Jobs_Logs_Items'), OBJECT_ID(N'dbo.SYS_Jobs_Logs_SubItems'), OBJECT_ID(N'dbo.SYS_ManagedSoftware_Subscriptions'));
GO

-- 2) Rechte vergeben (bricht ab, wenn der Benutzer fehlt)
IF USER_ID(N'acmp_mcp_ccwriter') IS NULL
    THROW 50000, N'Datenbankbenutzer acmp_mcp_ccwriter fehlt - zuerst grant-cc-writer.sql ausfuehren. Nichts geaendert.', 1;
-- Job-Vorlagen: Container-Vorlage anlegen bzw. Art ergaenzen, Einzelauftrags-Vorlage anlegen/aufraeumen
GRANT INSERT, DELETE ON dbo.SYS_Templates TO [acmp_mcp_ccwriter];
GRANT UPDATE ON dbo.SYS_Templates (JobKinds) TO [acmp_mcp_ccwriter];
GRANT SELECT, INSERT ON dbo.SYS_Containers_Templates TO [acmp_mcp_ccwriter];
GRANT SELECT, INSERT ON dbo.CLT_Clients_Templates TO [acmp_mcp_ccwriter];
-- Job-Monitor: anlegen, loeschen (Einzelauftraege), wiederherstellen
GRANT INSERT, DELETE ON dbo.SYS_Jobs_Logs TO [acmp_mcp_ccwriter];
GRANT SELECT, INSERT, DELETE ON dbo.SYS_Jobs_Logs_Items TO [acmp_mcp_ccwriter];
GRANT SELECT, INSERT, DELETE ON dbo.SYS_Jobs_Logs_SubItems TO [acmp_mcp_ccwriter];
GRANT EXECUTE ON dbo.ap_Job_BulkAddJobLogItems TO [acmp_mcp_ccwriter];
-- Abo
GRANT SELECT ON dbo.SYS_ManagedSoftware_Subscriptions TO [acmp_mcp_ccwriter];
GRANT EXECUTE ON dbo.ap_ManagedSoftware_Subscribe TO [acmp_mcp_ccwriter];
GRANT EXECUTE ON dbo.ap_ManagedSoftware_Unsubscribe TO [acmp_mcp_ccwriter];
GO

-- 3) Kontrolle: wirksame Rechte des Logins. Erwartet: alles 1, ausser den *_soll0-Spalten (0).
EXECUTE AS USER = N'acmp_mcp_ccwriter';
SELECT HAS_PERMS_BY_NAME(N'dbo.SYS_Templates', N'OBJECT', N'INSERT')                               AS tpl_insert,
       HAS_PERMS_BY_NAME(N'dbo.SYS_Templates', N'OBJECT', N'DELETE')                               AS tpl_delete,
       HAS_PERMS_BY_NAME(N'dbo.SYS_Templates', N'OBJECT', N'UPDATE', N'JobKinds', N'COLUMN')        AS tpl_upd_kinds,
       HAS_PERMS_BY_NAME(N'dbo.SYS_Containers_Templates', N'OBJECT', N'INSERT')                    AS ct_insert,
       HAS_PERMS_BY_NAME(N'dbo.CLT_Clients_Templates', N'OBJECT', N'INSERT')                       AS clt_insert,
       HAS_PERMS_BY_NAME(N'dbo.SYS_Jobs_Logs', N'OBJECT', N'INSERT')                               AS logs_insert,
       HAS_PERMS_BY_NAME(N'dbo.SYS_Jobs_Logs', N'OBJECT', N'DELETE')                               AS logs_delete,
       HAS_PERMS_BY_NAME(N'dbo.SYS_Jobs_Logs_Items', N'OBJECT', N'DELETE')                         AS items_delete,
       HAS_PERMS_BY_NAME(N'dbo.SYS_Jobs_Logs_SubItems', N'OBJECT', N'DELETE')                      AS subitems_delete,
       HAS_PERMS_BY_NAME(N'dbo.ap_Job_BulkAddJobLogItems', N'OBJECT', N'EXECUTE')                  AS bulk_execute,
       HAS_PERMS_BY_NAME(N'dbo.ap_ManagedSoftware_Subscribe', N'OBJECT', N'EXECUTE')               AS subscribe_execute,
       HAS_PERMS_BY_NAME(N'dbo.ap_ManagedSoftware_Unsubscribe', N'OBJECT', N'EXECUTE')             AS unsubscribe_execute,
       HAS_PERMS_BY_NAME(N'dbo.SYS_Jobs', N'OBJECT', N'DELETE')                                    AS jobs_delete_soll0,
       HAS_PERMS_BY_NAME(N'dbo.SYS_Jobs', N'OBJECT', N'UPDATE', N'JobAction', N'COLUMN')            AS jobs_upd_jobaction_soll0,
       HAS_PERMS_BY_NAME(N'dbo.SYS_Templates', N'OBJECT', N'UPDATE', N'Name', N'COLUMN')            AS tpl_upd_name_soll0;
REVERT;
GO

-- Danach: acmp_jobs mit status=true (bzw. Dienst-Neustart) -> Vorpruefung "Rechte MSW-Dauerauftrag/-Testauftrag/-Abo" und "Rechte Einzelauftraege loeschen/wiederherstellen" ok.
-- Rueckgaengig machen (nur bei Bedarf, Kommentarzeichen entfernen):
-- REVOKE INSERT, DELETE ON dbo.SYS_Templates FROM [acmp_mcp_ccwriter];
-- REVOKE UPDATE ON dbo.SYS_Templates (JobKinds) FROM [acmp_mcp_ccwriter];
-- REVOKE SELECT, INSERT ON dbo.SYS_Containers_Templates FROM [acmp_mcp_ccwriter];
-- REVOKE SELECT, INSERT ON dbo.CLT_Clients_Templates FROM [acmp_mcp_ccwriter];
-- REVOKE INSERT, DELETE ON dbo.SYS_Jobs_Logs FROM [acmp_mcp_ccwriter];
-- REVOKE SELECT, INSERT, DELETE ON dbo.SYS_Jobs_Logs_Items FROM [acmp_mcp_ccwriter];
-- REVOKE SELECT, INSERT, DELETE ON dbo.SYS_Jobs_Logs_SubItems FROM [acmp_mcp_ccwriter];
-- REVOKE EXECUTE ON dbo.ap_Job_BulkAddJobLogItems FROM [acmp_mcp_ccwriter];
-- REVOKE SELECT ON dbo.SYS_ManagedSoftware_Subscriptions FROM [acmp_mcp_ccwriter];
-- REVOKE EXECUTE ON dbo.ap_ManagedSoftware_Subscribe FROM [acmp_mcp_ccwriter];
-- REVOKE EXECUTE ON dbo.ap_ManagedSoftware_Unsubscribe FROM [acmp_mcp_ccwriter];
