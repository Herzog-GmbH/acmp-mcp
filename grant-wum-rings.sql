-- Rechte fuer den Fehler-Stopp der Windows-Updates (Teil des WUM-Writers): acmp_wum_ring_hold, acmp_wum_ring_release und der Update-Waechter.
-- Ohne dieses Recht bleiben nur Anhalten und Weiterlaufen-Lassen gesperrt (Lesen und dryRun gehen).
-- Optional und standardmaessig aus. Direktes Schreiben in die ACMP-Datenbank ist von Aagon nicht vorgesehen:
-- vorher eine Sicherung der Datenbank anlegen.
-- Voraussetzung: grant-cc-writer.sql und grant-wum-writer.sql (die bringen SELECT/INSERT und UPDATE(TestRing, LastChangeDate) auf
-- SYS_TestRings und EXECUTE auf ap_Wum_Rebuild_UpdateFlags). NEU ist nur UPDATE auf die Spalte TransitionMode.
-- Als SQL-Admin ausfuehren, ggf. den Datenbanknamen [ACMP] anpassen. Danach: setup-extra-writers.ps1 -Features wum
--
-- SQL-Mitschnitt der ACMP-Console 6.10.4:
--   Anhalten/Weiterlaufen = UPDATE SYS_TestRings SET TransitionMode = 0/1 WHERE Id = ...
--   Sofort freigeben      = UPDATE SYS_TestRings SET TestRing = 255, TransitionMode = 1, LastChangeDate = jetzt ..., danach ap_Wum_Rebuild_UpdateFlags
--
-- Bewusst NICHT vergeben: DELETE auf SYS_TestRings, UPDATE auf ItemKind, ItemState, CollectionName.
USE [ACMP];
GO

-- 1) Vorher ansehen: Trigger auf SYS_TestRings (erwartet: keine Zeile - sonst nicht weitermachen)
SELECT t.name AS trigger_name, t.is_disabled, OBJECT_DEFINITION(t.object_id) AS definition
FROM sys.triggers t WHERE t.parent_id = OBJECT_ID(N'dbo.SYS_TestRings');
GO

-- 2) Recht vergeben (bricht ab, wenn der Benutzer fehlt)
IF USER_ID(N'acmp_mcp_ccwriter') IS NULL
    THROW 50000, N'Datenbankbenutzer acmp_mcp_ccwriter fehlt - zuerst grant-cc-writer.sql ausfuehren. Nichts geaendert.', 1;
GRANT UPDATE (TransitionMode) ON dbo.SYS_TestRings TO [acmp_mcp_ccwriter];
GO

-- 3) Kontrolle: erwartet alles 1, ausser den *_soll0-Spalten (0)
EXECUTE AS USER = N'acmp_mcp_ccwriter';
SELECT HAS_PERMS_BY_NAME(N'dbo.SYS_TestRings', N'OBJECT', N'SELECT')                              AS rings_select,
       HAS_PERMS_BY_NAME(N'dbo.SYS_TestRings', N'OBJECT', N'UPDATE', N'TransitionMode', N'COLUMN') AS rings_update_mode,
       HAS_PERMS_BY_NAME(N'dbo.SYS_TestRings', N'OBJECT', N'UPDATE', N'TestRing', N'COLUMN')       AS rings_update_ring,
       HAS_PERMS_BY_NAME(N'dbo.SYS_TestRings', N'OBJECT', N'UPDATE', N'LastChangeDate', N'COLUMN') AS rings_update_date,
       HAS_PERMS_BY_NAME(N'dbo.ap_Wum_Rebuild_UpdateFlags', N'OBJECT', N'EXECUTE')                 AS flags_execute,
       HAS_PERMS_BY_NAME(N'dbo.SYS_TestRings', N'OBJECT', N'UPDATE', N'ItemKind', N'COLUMN')       AS rings_update_kind_soll0,
       HAS_PERMS_BY_NAME(N'dbo.SYS_TestRings', N'OBJECT', N'DELETE')                               AS rings_delete_soll0;
REVERT;
GO

-- Danach: acmp_wum_rings mit writerStatus=true -> Vorpruefung "Rechte Anhalten" und "Rechte Freigeben" ok.
-- Rueckgaengig (nur bei Bedarf): REVOKE UPDATE (TransitionMode) ON dbo.SYS_TestRings FROM [acmp_mcp_ccwriter];
