-- Rechte fuer Loesch-, Wartungsfenster-, Sicherheitsprofil- und Agentenplaner-Tools (Teil des Job-Writers, AcmpLueckenWrite.cs).
-- Grundlage: SQL-Mitschnitte der ACMP-Console 6.10.4. Jedes Tool sichert vorher/nachher alle betroffenen Zeilen (SELECT) und kann sie mit
-- acmp_job_restore zuruecklegen (INSERT bzw. UPDATE/DELETE) - deshalb SELECT/INSERT auch auf Tabellen, die nur ueber Kaskaden/Trigger geaendert werden.
--   acmp_cc_delete                  = DELETE SYS_Jobs_Parameters_Config/_Declarations, SYS_SCRIPTS, SYS_Kiosk_Items (Trigger/Kaskaden: Auswahllisten, Variablen, Kommentare,
--                                     SYS_ACLS, CLT_SCRIPT_ATTACHMENTS, SYS_Script_DistribFileRepos_ClearBeforeSync, SYS_SCRIPT_FILES). SYS_ACLS wird nur gesichert:
--                                     Einfuegen leitet der INSTEAD-OF-Trigger auf SYS_AccessControl_* um, deshalb kein INSERT-Recht noetig.
--   acmp_cc_delete_folder           = DELETE SYS_SCRIPT_STRUCTURE (INSTEAD-OF-Trigger), SYS_Folders_AclEntries_Users/_Groups
--   acmp_kiosk_delete_group         = DELETE SYS_Kiosk_Groups, SYS_Kiosk_Filters
--   acmp_unix_script_delete         = DELETE SYS_UnixClient_Scripts (Kaskade auf die Versionen)
--   acmp_maintenance_window_*       = INSERT/DELETE SYS_Jobs_Maintenance_Profiles, INSERT/UPDATE/DELETE SYS_Jobs_Maintenance_Profiles_Containers,
--                                     UPDATE CLT_CONTAINER_STRUCTURE(ContainerType)
--   acmp_container_security_profile = INSERT/DELETE SYS_Sec_BitLocker/Defender_ConfigurationProfiles_Containers, Vorlage/Job wie die MSW-Tools,
--                                     DELETE SYS_Containers_Templates, UPDATE CLT_CONTAINER_STRUCTURE(ContainerType)
--   acmp_agent_template_assign      = INSERT/DELETE SYS_Containers_Templates
-- Vorhanden (grant-jobs-writer.sql, grant-msw-writer.sql, CC-/Kiosk-/Unix-Writer): u.a. SELECT/INSERT SYS_Jobs, SYS_Templates INSERT/DELETE,
-- SYS_Containers_Templates SELECT/INSERT, SYS_Jobs_Logs INSERT, SYS_Templates_Jobs SELECT/INSERT/DELETE. GRANT ist wiederholbar.
-- Ohne diese Rechte bleiben nur diese Tools gesperrt (Lesen und dryRun gehen).
-- Optional und standardmaessig aus. Direktes Schreiben in die ACMP-Datenbank ist von Aagon nicht vorgesehen:
-- vorher eine Sicherung der Datenbank anlegen.
-- Voraussetzung: grant-cc-writer.sql, grant-jobs-writer.sql und grant-msw-writer.sql. Als SQL-Admin ausfuehren, Abschnitt fuer Abschnitt
-- oder alles, ggf. den Datenbanknamen [ACMP] anpassen. Danach: setup-extra-writers.ps1 -Features jobs
--
-- Bewusst NICHT vergeben: DELETE auf SYS_Jobs, UPDATE auf SYS_Jobs.JobAction, UPDATE auf andere Spalten von CLT_CONTAINER_STRUCTURE, ALTER/Schema-Rechte.
USE [ACMP];
GO

-- 0) Gibt es den Datenbankbenutzer? (erwartet: 1 Zeile, SQL_USER; sonst erst grant-cc-writer.sql)
SELECT name, type_desc FROM sys.database_principals WHERE name = N'acmp_mcp_ccwriter';
GO

-- 1) Vorher ansehen: Trigger auf den beteiligten Tabellen. Erwartet genau diese 9 Zeilen (Reihenfolge egal, Stand 25.10.2026):
--    SYS_SCRIPTS / Trg_SYS_SCRIPTS_Delete, SYS_SCRIPTS / Trg_SYS_SCRIPTS_Update, SYS_SCRIPT_STRUCTURE / Trg_SYS_SCRIPT_STRUCTURE_Delete,
--    SYS_Templates / Trg_SYS_Templates_Delete, SYS_Templates_Jobs / TG_Delete_SYS_Templates_Jobs, CLT_CONTAINER_STRUCTURE / TG_CLT_CONTAINER_STRUCTURE_UpdateTenantNo,
--    SYS_Kiosk_Items / TR_SYS_Kiosk_Items_Delete, SYS_SCRIPT_FILES / TR_SYS_SCRIPT_FILES_DELETE, SYS_ACLS / TG_InsteadOfInsertUpdate_LegacyConversion.
--    Andere Zeilen: nicht weitermachen.
SELECT OBJECT_NAME(t.parent_id) AS tabelle, t.name AS trigger_name, t.is_disabled
FROM sys.triggers t
WHERE t.parent_id IN (OBJECT_ID(N'dbo.SYS_SCRIPTS'), OBJECT_ID(N'dbo.SYS_SCRIPTS_VARIABLES'), OBJECT_ID(N'dbo.SYS_Script_Comments'), OBJECT_ID(N'dbo.SYS_Jobs_Parameters_Declarations'), OBJECT_ID(N'dbo.SYS_Jobs_Parameters_Config_Values'),
                      OBJECT_ID(N'dbo.SYS_Jobs_Parameters_Config'), OBJECT_ID(N'dbo.SYS_Kiosk_Items'), OBJECT_ID(N'dbo.SYS_ACLS'), OBJECT_ID(N'dbo.CLT_SCRIPT_ATTACHMENTS'),
                      OBJECT_ID(N'dbo.SYS_Script_DistribFileRepos_ClearBeforeSync'), OBJECT_ID(N'dbo.SYS_SCRIPT_FILES'), OBJECT_ID(N'dbo.SYS_SCRIPT_STRUCTURE'),
                      OBJECT_ID(N'dbo.SYS_Folders_AclEntries_Users'), OBJECT_ID(N'dbo.SYS_Folders_AclEntries_Groups'), OBJECT_ID(N'dbo.SYS_Kiosk_Groups'), OBJECT_ID(N'dbo.SYS_Kiosk_Filters'),
                      OBJECT_ID(N'dbo.SYS_UnixClient_Scripts'), OBJECT_ID(N'dbo.SYS_UnixClient_Scripts_Versions'), OBJECT_ID(N'dbo.SYS_Jobs_Maintenance_Profiles'),
                      OBJECT_ID(N'dbo.SYS_Jobs_Maintenance_Profiles_Containers'), OBJECT_ID(N'dbo.CLT_Jobs_Maintenance_Profiles'), OBJECT_ID(N'dbo.CLT_CONTAINER_STRUCTURE'),
                      OBJECT_ID(N'dbo.SYS_Sec_BitLocker_ConfigurationProfiles_Containers'), OBJECT_ID(N'dbo.SYS_Sec_Defender_ConfigurationProfiles_Containers'),
                      OBJECT_ID(N'dbo.SYS_Templates'), OBJECT_ID(N'dbo.SYS_Jobs'), OBJECT_ID(N'dbo.SYS_Templates_Jobs'), OBJECT_ID(N'dbo.SYS_Containers_Templates'));
GO

-- 2) Rechte vergeben (bricht ab, wenn der Benutzer fehlt)
IF USER_ID(N'acmp_mcp_ccwriter') IS NULL
    THROW 50000, N'Datenbankbenutzer acmp_mcp_ccwriter fehlt - zuerst grant-cc-writer.sql ausfuehren. Nichts geaendert.', 1;
-- Client Commands loeschen (und zuruecklegen)
GRANT SELECT, INSERT, DELETE ON dbo.SYS_SCRIPTS TO [acmp_mcp_ccwriter];
GRANT SELECT, INSERT ON dbo.SYS_SCRIPTS_VARIABLES TO [acmp_mcp_ccwriter];
GRANT SELECT, INSERT ON dbo.SYS_Script_Comments TO [acmp_mcp_ccwriter];
GRANT SELECT, INSERT, DELETE ON dbo.SYS_Jobs_Parameters_Declarations TO [acmp_mcp_ccwriter];
GRANT SELECT, INSERT, DELETE ON dbo.SYS_Jobs_Parameters_Config TO [acmp_mcp_ccwriter];
GRANT SELECT, INSERT ON dbo.SYS_Jobs_Parameters_Config_Values TO [acmp_mcp_ccwriter];
GRANT SELECT ON dbo.SYS_Jobs_Parameters TO [acmp_mcp_ccwriter];
GRANT SELECT ON dbo.SYS_Jobs_Logs_Items TO [acmp_mcp_ccwriter];
GRANT SELECT, INSERT, DELETE ON dbo.SYS_Kiosk_Items TO [acmp_mcp_ccwriter];
GRANT SELECT ON dbo.SYS_ACLS TO [acmp_mcp_ccwriter];
GRANT SELECT, INSERT ON dbo.CLT_SCRIPT_ATTACHMENTS TO [acmp_mcp_ccwriter];
GRANT SELECT, INSERT ON dbo.SYS_Script_DistribFileRepos_ClearBeforeSync TO [acmp_mcp_ccwriter];
GRANT SELECT, INSERT ON dbo.SYS_SCRIPT_FILES TO [acmp_mcp_ccwriter];
-- Client-Command-Ordner loeschen
GRANT SELECT, INSERT, DELETE ON dbo.SYS_SCRIPT_STRUCTURE TO [acmp_mcp_ccwriter];
GRANT SELECT, INSERT, DELETE ON dbo.SYS_Folders_AclEntries_Users TO [acmp_mcp_ccwriter];
GRANT SELECT, INSERT, DELETE ON dbo.SYS_Folders_AclEntries_Groups TO [acmp_mcp_ccwriter];
-- Kiosk-Ordner loeschen
GRANT SELECT, INSERT, DELETE ON dbo.SYS_Kiosk_Groups TO [acmp_mcp_ccwriter];
GRANT SELECT, INSERT, DELETE ON dbo.SYS_Kiosk_Filters TO [acmp_mcp_ccwriter];
-- Unix Scripts loeschen
GRANT SELECT, INSERT, DELETE ON dbo.SYS_UnixClient_Scripts TO [acmp_mcp_ccwriter];
GRANT SELECT, INSERT ON dbo.SYS_UnixClient_Scripts_Versions TO [acmp_mcp_ccwriter];
-- Wartungsfenster
GRANT SELECT, INSERT, DELETE ON dbo.SYS_Jobs_Maintenance_Profiles TO [acmp_mcp_ccwriter];
GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.SYS_Jobs_Maintenance_Profiles_Containers TO [acmp_mcp_ccwriter];
GRANT SELECT, INSERT ON dbo.CLT_Jobs_Maintenance_Profiles TO [acmp_mcp_ccwriter];
GRANT SELECT ON dbo.CLT_CONTAINER_STRUCTURE TO [acmp_mcp_ccwriter];
GRANT UPDATE ON dbo.CLT_CONTAINER_STRUCTURE (ContainerType) TO [acmp_mcp_ccwriter];
-- BitLocker-/Defender-Profile an Containern
GRANT SELECT, INSERT, DELETE ON dbo.SYS_Sec_BitLocker_ConfigurationProfiles_Containers TO [acmp_mcp_ccwriter];
GRANT SELECT, INSERT, DELETE ON dbo.SYS_Sec_Defender_ConfigurationProfiles_Containers TO [acmp_mcp_ccwriter];
GRANT SELECT ON dbo.SYS_Sec_BitLocker_ConfigurationProfiles TO [acmp_mcp_ccwriter];
GRANT SELECT ON dbo.SYS_Sec_Defender_ConfigurationProfiles TO [acmp_mcp_ccwriter];
GRANT SELECT, INSERT, DELETE ON dbo.SYS_Templates TO [acmp_mcp_ccwriter];
GRANT SELECT, INSERT ON dbo.SYS_Jobs TO [acmp_mcp_ccwriter];
GRANT SELECT, INSERT ON dbo.SYS_Jobs_Logs TO [acmp_mcp_ccwriter];
GRANT UPDATE ON dbo.SYS_Jobs_Logs (State) TO [acmp_mcp_ccwriter];
GRANT SELECT, INSERT, DELETE ON dbo.SYS_Templates_Jobs TO [acmp_mcp_ccwriter];
-- Agentenplaner-Vorlage an Containern (auch Profile-Vorlagen)
GRANT SELECT, INSERT, DELETE ON dbo.SYS_Containers_Templates TO [acmp_mcp_ccwriter];
-- Sperren der Console lesen (Objekt in Bearbeitung)
GRANT SELECT ON dbo.SYS_ObjectLocks TO [acmp_mcp_ccwriter];
GO

-- 3) Kontrolle: wirksame Rechte des Logins. Erwartet: alles 1, ausser den *_soll0-Spalten (0).
EXECUTE AS USER = N'acmp_mcp_ccwriter';
SELECT HAS_PERMS_BY_NAME(N'dbo.SYS_SCRIPTS', N'OBJECT', N'DELETE')                                        AS scripts_delete,
       HAS_PERMS_BY_NAME(N'dbo.SYS_Jobs_Parameters_Config', N'OBJECT', N'DELETE')                         AS paramcfg_delete,
       HAS_PERMS_BY_NAME(N'dbo.SYS_Kiosk_Items', N'OBJECT', N'DELETE')                                    AS kioskitems_delete,
       HAS_PERMS_BY_NAME(N'dbo.SYS_Jobs_Parameters_Config_Values', N'OBJECT', N'INSERT')                  AS paramvalues_insert,
       HAS_PERMS_BY_NAME(N'dbo.SYS_SCRIPT_STRUCTURE', N'OBJECT', N'DELETE')                               AS folders_delete,
       HAS_PERMS_BY_NAME(N'dbo.SYS_Kiosk_Groups', N'OBJECT', N'DELETE')                                   AS kioskgroups_delete,
       HAS_PERMS_BY_NAME(N'dbo.SYS_UnixClient_Scripts', N'OBJECT', N'DELETE')                             AS unix_delete,
       HAS_PERMS_BY_NAME(N'dbo.SYS_Jobs_Maintenance_Profiles_Containers', N'OBJECT', N'UPDATE')           AS mwcont_update,
       HAS_PERMS_BY_NAME(N'dbo.CLT_CONTAINER_STRUCTURE', N'OBJECT', N'UPDATE', N'ContainerType', N'COLUMN') AS cont_upd_type,
       HAS_PERMS_BY_NAME(N'dbo.SYS_Sec_BitLocker_ConfigurationProfiles_Containers', N'OBJECT', N'INSERT') AS bl_insert,
       HAS_PERMS_BY_NAME(N'dbo.SYS_Containers_Templates', N'OBJECT', N'DELETE')                           AS ct_delete,
       HAS_PERMS_BY_NAME(N'dbo.SYS_ObjectLocks', N'OBJECT', N'SELECT')                                    AS locks_select,
       HAS_PERMS_BY_NAME(N'dbo.SYS_Jobs', N'OBJECT', N'DELETE')                                           AS jobs_delete_soll0,
       HAS_PERMS_BY_NAME(N'dbo.SYS_Jobs', N'OBJECT', N'UPDATE', N'JobAction', N'COLUMN')                  AS jobs_upd_jobaction_soll0,
       HAS_PERMS_BY_NAME(N'dbo.CLT_CONTAINER_STRUCTURE', N'OBJECT', N'UPDATE', N'Path', N'COLUMN')         AS cont_upd_path_soll0;
REVERT;
GO

-- Danach: acmp_jobs mit status=true (bzw. Dienst-Neustart) -> Vorpruefung "Rechte Loesch-/Profil-Tools" ok.
-- Rueckgaengig machen (nur bei Bedarf, Kommentarzeichen entfernen; Rechte, die schon vorher bestanden, NICHT entziehen):
-- REVOKE DELETE ON dbo.SYS_SCRIPTS FROM [acmp_mcp_ccwriter];
-- REVOKE DELETE ON dbo.SYS_Jobs_Parameters_Declarations FROM [acmp_mcp_ccwriter];
-- REVOKE DELETE ON dbo.SYS_Jobs_Parameters_Config FROM [acmp_mcp_ccwriter];
-- REVOKE DELETE ON dbo.SYS_Kiosk_Items FROM [acmp_mcp_ccwriter];
-- REVOKE INSERT ON dbo.SYS_Jobs_Parameters_Config_Values FROM [acmp_mcp_ccwriter];
-- REVOKE DELETE ON dbo.SYS_SCRIPT_STRUCTURE FROM [acmp_mcp_ccwriter];
-- REVOKE DELETE ON dbo.SYS_Folders_AclEntries_Users FROM [acmp_mcp_ccwriter];
-- REVOKE DELETE ON dbo.SYS_Folders_AclEntries_Groups FROM [acmp_mcp_ccwriter];
-- REVOKE DELETE ON dbo.SYS_Kiosk_Groups FROM [acmp_mcp_ccwriter];
-- REVOKE DELETE ON dbo.SYS_Kiosk_Filters FROM [acmp_mcp_ccwriter];
-- REVOKE DELETE ON dbo.SYS_UnixClient_Scripts FROM [acmp_mcp_ccwriter];
-- REVOKE INSERT, DELETE ON dbo.SYS_Jobs_Maintenance_Profiles FROM [acmp_mcp_ccwriter];
-- REVOKE INSERT, UPDATE, DELETE ON dbo.SYS_Jobs_Maintenance_Profiles_Containers FROM [acmp_mcp_ccwriter];
-- REVOKE INSERT ON dbo.CLT_Jobs_Maintenance_Profiles FROM [acmp_mcp_ccwriter];
-- REVOKE UPDATE ON dbo.CLT_CONTAINER_STRUCTURE (ContainerType) FROM [acmp_mcp_ccwriter];
-- REVOKE INSERT, DELETE ON dbo.SYS_Sec_BitLocker_ConfigurationProfiles_Containers FROM [acmp_mcp_ccwriter];
-- REVOKE INSERT, DELETE ON dbo.SYS_Sec_Defender_ConfigurationProfiles_Containers FROM [acmp_mcp_ccwriter];
-- REVOKE DELETE ON dbo.SYS_Containers_Templates FROM [acmp_mcp_ccwriter];
