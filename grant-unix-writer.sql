-- Rechte fuer den Unix-Script-Writer (ACMP 6.10, Desktop Automation -> Unix Scripts): acmp_unix_script_create,
-- acmp_unix_script_new_version, acmp_unix_script_set_ring. Die Lesetools acmp_unix_scripts und acmp_unix_results
-- brauchen nur db.connectionString.
-- Optional und standardmaessig aus. Direktes Schreiben in die ACMP-Datenbank ist von Aagon nicht vorgesehen:
-- vorher eine Sicherung der Datenbank anlegen. Unix Scripts laufen auf den Clients als root.
--
-- So arbeitet das Tool (Beobachtung der Console 6.10.4):
--   Skript      = Zeile in SYS_UnixClient_Scripts + Rechte wie der Ordner (SYS_AccessControl_AclEntries_*)
--   Version     = Zeile in SYS_UnixClient_Scripts_Versions + Zeile in SYS_TestRings (Id = Versions-Id, ItemKind 68, Ring 1)
--   Freigeben   = UPDATE SYS_TestRings SET TestRing = 255, LastChangeDate = jetzt (Console: "Verteilungsring aendern" -> Freigegeben)
-- Einen Job starten kann der MCP nicht (die Public API hat keinen Befehl dafuer) - das bleibt in der Console.
--
-- Der Unix-Script-Writer nutzt denselben SQL-Login wie der CC-Writer. Deshalb zuerst grant-cc-writer.sql ausfuehren
-- (legt Login und Datenbankbenutzer acmp_mcp_ccwriter an und vergibt INSERT auf SYS_AccessControl_AclEntries_* und
-- SELECT auf SYS_Folders_AclEntries_*), dann dieses Skript als SQL-Admin, Abschnitt fuer Abschnitt oder alles.
-- Ggf. den Datenbanknamen [ACMP] anpassen.
--
-- Bewusst NICHT vergeben:
--   * UPDATE auf SYS_UnixClient_Scripts / _Versions (das Tool aendert keine vorhandenen Skripte oder Versionen, es legt nur neue an)
--   * DELETE auf alle drei Tabellen, UPDATE auf andere Spalten von SYS_TestRings (dort stehen auch die Ringe der Windows-Updates)
USE [ACMP];
GO

-- 0) Gibt es den Datenbankbenutzer? (erwartet: 1 Zeile, SQL_USER; sonst erst grant-cc-writer.sql)
SELECT name, type_desc, create_date
FROM sys.database_principals
WHERE name = N'acmp_mcp_ccwriter';
GO

-- 1) Vorher ansehen. Der Server sperrt die Schreibtools bei unbekannten Triggern (Vorpruefung in acmp_unix_scripts mit status=true).
-- 1a) Trigger auf den beteiligten Tabellen (ACMP 6.10.4: keine)
SELECT OBJECT_NAME(t.parent_id) AS tabelle, t.name AS trigger_name, t.is_disabled, t.is_instead_of_trigger
FROM sys.triggers t
WHERE t.parent_id IN (OBJECT_ID(N'dbo.SYS_UnixClient_Scripts'), OBJECT_ID(N'dbo.SYS_UnixClient_Scripts_Versions'), OBJECT_ID(N'dbo.SYS_TestRings'), OBJECT_ID(N'dbo.SYS_TestRings_DeclineInfo'));
-- 1b) Fremdschluessel von/auf diese Tabellen
SELECT fk.name AS fk_name, OBJECT_NAME(fk.parent_object_id) AS von_tabelle, OBJECT_NAME(fk.referenced_object_id) AS auf_tabelle,
       fk.delete_referential_action_desc AS beim_loeschen
FROM sys.foreign_keys fk
WHERE fk.parent_object_id IN (OBJECT_ID(N'dbo.SYS_UnixClient_Scripts'), OBJECT_ID(N'dbo.SYS_UnixClient_Scripts_Versions'), OBJECT_ID(N'dbo.SYS_TestRings'))
   OR fk.referenced_object_id IN (OBJECT_ID(N'dbo.SYS_UnixClient_Scripts'), OBJECT_ID(N'dbo.SYS_UnixClient_Scripts_Versions'), OBJECT_ID(N'dbo.SYS_TestRings'));
GO

-- 2) Rechte vergeben (bricht ab, wenn der Benutzer fehlt)
IF USER_ID(N'acmp_mcp_ccwriter') IS NULL
    THROW 50000, N'Datenbankbenutzer acmp_mcp_ccwriter fehlt - zuerst grant-cc-writer.sql ausfuehren. Nichts geaendert.', 1;
GRANT SELECT, INSERT ON dbo.SYS_UnixClient_Scripts TO [acmp_mcp_ccwriter];
GRANT SELECT, INSERT ON dbo.SYS_UnixClient_Scripts_Versions TO [acmp_mcp_ccwriter];
GRANT SELECT, INSERT ON dbo.SYS_TestRings TO [acmp_mcp_ccwriter];
GRANT UPDATE (TestRing, LastChangeDate) ON dbo.SYS_TestRings TO [acmp_mcp_ccwriter];
GO

-- 3) Kontrolle: wirksame Rechte des Logins. Erwartet: alles 1, ausser den Spalten *_soll0 (0).
--    Steht dort eine 1, ist der Login vermutlich in db_datawriter oder db_owner -> entfernen.
EXECUTE AS USER = N'acmp_mcp_ccwriter';
SELECT HAS_PERMS_BY_NAME(N'dbo.SYS_UnixClient_Scripts', N'OBJECT', N'SELECT')                        AS scripts_select,
       HAS_PERMS_BY_NAME(N'dbo.SYS_UnixClient_Scripts', N'OBJECT', N'INSERT')                        AS scripts_insert,
       HAS_PERMS_BY_NAME(N'dbo.SYS_UnixClient_Scripts_Versions', N'OBJECT', N'SELECT')               AS versions_select,
       HAS_PERMS_BY_NAME(N'dbo.SYS_UnixClient_Scripts_Versions', N'OBJECT', N'INSERT')               AS versions_insert,
       HAS_PERMS_BY_NAME(N'dbo.SYS_TestRings', N'OBJECT', N'SELECT')                                 AS rings_select,
       HAS_PERMS_BY_NAME(N'dbo.SYS_TestRings', N'OBJECT', N'INSERT')                                 AS rings_insert,
       HAS_PERMS_BY_NAME(N'dbo.SYS_TestRings', N'OBJECT', N'UPDATE', N'TestRing', N'COLUMN')         AS rings_update_ring,
       HAS_PERMS_BY_NAME(N'dbo.SYS_TestRings', N'OBJECT', N'UPDATE', N'LastChangeDate', N'COLUMN')   AS rings_update_date,
       HAS_PERMS_BY_NAME(N'dbo.SYS_AccessControl_AclEntries_Users', N'OBJECT', N'INSERT')            AS acl_users_insert,
       HAS_PERMS_BY_NAME(N'dbo.SYS_AccessControl_AclEntries_Groups', N'OBJECT', N'INSERT')           AS acl_groups_insert,
       HAS_PERMS_BY_NAME(N'dbo.SYS_Folders_AclEntries_Users', N'OBJECT', N'SELECT')                  AS folderacl_users_select,
       HAS_PERMS_BY_NAME(N'dbo.SYS_Folders_AclEntries_Groups', N'OBJECT', N'SELECT')                 AS folderacl_groups_select,
       HAS_PERMS_BY_NAME(N'dbo.SYS_TestRings', N'OBJECT', N'UPDATE', N'ItemKind', N'COLUMN')         AS rings_update_kind_soll0,
       HAS_PERMS_BY_NAME(N'dbo.SYS_UnixClient_Scripts_Versions', N'OBJECT', N'UPDATE')               AS versions_update_soll0,
       HAS_PERMS_BY_NAME(N'dbo.SYS_TestRings', N'OBJECT', N'DELETE')                                 AS rings_delete_soll0;
REVERT;
GO

-- Danach: setup-extra-writers.ps1 (als Admin), Pruefung im MCP-Client mit acmp_unix_scripts status=true.
-- Rueckgaengig machen (nur bei Bedarf, Kommentarzeichen entfernen):
-- REVOKE SELECT, INSERT ON dbo.SYS_UnixClient_Scripts FROM [acmp_mcp_ccwriter];
-- REVOKE SELECT, INSERT ON dbo.SYS_UnixClient_Scripts_Versions FROM [acmp_mcp_ccwriter];
-- REVOKE SELECT, INSERT ON dbo.SYS_TestRings FROM [acmp_mcp_ccwriter];
-- REVOKE UPDATE (TestRing, LastChangeDate) ON dbo.SYS_TestRings FROM [acmp_mcp_ccwriter];
