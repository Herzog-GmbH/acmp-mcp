-- Eigener SQL-Login fuer den CC-Writer (Client Commands direkt in der ACMP-DB anlegen, aendern, freigeben, einsortieren).
-- Optional und standardmaessig aus. Direktes Schreiben in die ACMP-Datenbank ist von Aagon nicht vorgesehen:
-- vorher eine Sicherung der Datenbank anlegen und zuerst nur in einem Testordner arbeiten.
--
-- Auf eurer ACMP-SQL-Instanz als SQL-Admin ausfuehren (z.B. im SSMS), Abschnitt fuer Abschnitt oder alles.
-- Vorher anpassen: <starkes-Passwort> und ggf. den Datenbanknamen [ACMP]. Das Kennwort gehoert in euren Passwort-Manager;
-- setup-cc-writer.ps1 fragt es spaeter verdeckt ab.
--
-- Der Login bekommt nur genau die Rechte, die der Writer braucht. Loeschen in SYS_SCRIPTS darf er NICHT (Entwuerfe
-- loescht ihr in der ACMP-Konsole), Ordner loeschen und Rechte aendern auch nicht. Die Kompatibilitaetspruefung des
-- Servers (acmp_cc_writer_status) prueft diese Rechte und warnt, wenn der Login mehr darf (DELETE auf SYS_SCRIPTS,
-- db_owner, sysadmin).
--
-- Welcher Abschnitt wofuer:
--   3  Pflicht      Entwuerfe anlegen/aendern/zuruecksetzen, acmp_lib_install, acmp_cc_release (Freigeben), acmp_cc_move
--   4  optional    Dateien anhaengen (acmp_cc_add_file / acmp_cc_remove_file)
--   5  optional    Ordner anlegen (acmp_cc_add_folder)
--   6  optional    Ordner umbenennen und verschieben (acmp_cc_update_folder)
-- Der Kiosk-Writer nutzt denselben Login, seine Rechte stehen in grant-kiosk-writer.sql.

-- 0) Vorher ansehen: Trigger auf den Tabellen, in die geschrieben wird.
--    Der Server kennt die Trigger von ACMP 6.10 (Trg_SYS_SCRIPTS_Update, Trg_SYS_SCRIPTS_Delete,
--    TR_SYS_SCRIPT_FILES_DELETE, Trg_SYS_SCRIPT_STRUCTURE_Delete) und sperrt das Schreiben bei unbekannten Triggern.
USE [ACMP];
GO
SELECT OBJECT_NAME(t.parent_id) AS tabelle, t.name AS trigger_name, t.is_disabled
FROM sys.triggers t
WHERE t.parent_id IN (OBJECT_ID('dbo.SYS_SCRIPTS'), OBJECT_ID('dbo.SYS_SCRIPTS_VARIABLES'), OBJECT_ID('dbo.SYS_SCRIPT_FILES'),
                      OBJECT_ID('dbo.SYS_SCRIPT_STRUCTURE'), OBJECT_ID('dbo.SYS_Folders_AclEntries_Users'), OBJECT_ID('dbo.SYS_Folders_AclEntries_Groups'),
                      OBJECT_ID('dbo.SYS_AccessControl_AclEntries_Users'), OBJECT_ID('dbo.SYS_AccessControl_AclEntries_Groups'));
GO

-- 1) Login (SQL-Authentifizierung; der Dienst laeuft als SYSTEM, deshalb ein eigener Login statt des Maschinenkontos,
--    das schon fuer die read-only DB-Report-Tools genutzt wird)
USE [master];
GO
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'acmp_mcp_ccwriter')
    CREATE LOGIN [acmp_mcp_ccwriter] WITH PASSWORD = N'<starkes-Passwort>', CHECK_POLICY = ON, DEFAULT_DATABASE = [ACMP];
GO

-- 2) Benutzer in der ACMP-Datenbank, KEINE Rollenmitgliedschaft (auch nicht db_datareader/db_datawriter)
USE [ACMP];
GO
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'acmp_mcp_ccwriter')
    CREATE USER [acmp_mcp_ccwriter] FOR LOGIN [acmp_mcp_ccwriter];
GO

-- 3) Pflicht: Entwuerfe, Freigeben, Verschieben
--    acmp_cc_release aendert nur State, IsFileRepoVersionIncreased und FileRepoSize, acmp_cc_move nur FOLDERID -
--    beides deckt UPDATE auf SYS_SCRIPTS ab, ein eigenes Recht braucht es dafuer nicht.
GRANT SELECT, INSERT, UPDATE ON dbo.SYS_SCRIPTS                         TO [acmp_mcp_ccwriter];  -- kein DELETE
GRANT SELECT, INSERT, DELETE ON dbo.SYS_SCRIPTS_VARIABLES               TO [acmp_mcp_ccwriter];  -- Projektvariablen des Entwurfs neu schreiben
GRANT SELECT                 ON dbo.SYS_SCRIPT_STRUCTURE                TO [acmp_mcp_ccwriter];  -- Ordnerbaum (Ordner aufloesen)
GRANT SELECT                 ON dbo.SYS_Folders_AclEntries_Users        TO [acmp_mcp_ccwriter];  -- Rechte des Ordners lesen ...
GRANT SELECT                 ON dbo.SYS_Folders_AclEntries_Groups       TO [acmp_mcp_ccwriter];
GRANT INSERT                 ON dbo.SYS_AccessControl_AclEntries_Users  TO [acmp_mcp_ccwriter];  -- ... und auf den Entwurf kopieren
GRANT INSERT                 ON dbo.SYS_AccessControl_AclEntries_Groups TO [acmp_mcp_ccwriter];  --     (sonst ist er in der Konsole unsichtbar)
GRANT SELECT                 ON dbo.SYS_SCRIPT_FILES                    TO [acmp_mcp_ccwriter];  -- acmp_cc_release prueft die Dateien des Entwurfs
GO

-- 4) Optional: angehaengte Dateien (acmp_cc_add_file / acmp_cc_remove_file)
GRANT SELECT, INSERT, DELETE ON dbo.SYS_SCRIPT_FILES                    TO [acmp_mcp_ccwriter];
GO

-- 5) Optional: Ordner anlegen (acmp_cc_add_folder). Der neue Ordner bekommt die Rechte seines Elternordners.
--    Bewusst NICHT: UPDATE/DELETE auf SYS_Folders_AclEntries_* (Rechte aendern bleibt in der Konsole).
GRANT SELECT, INSERT ON dbo.SYS_SCRIPT_STRUCTURE          TO [acmp_mcp_ccwriter];
GRANT SELECT, INSERT ON dbo.SYS_Folders_AclEntries_Users  TO [acmp_mcp_ccwriter];
GRANT SELECT, INSERT ON dbo.SYS_Folders_AclEntries_Groups TO [acmp_mcp_ccwriter];
GO

-- 6) Optional: Ordner umbenennen und verschieben (acmp_cc_update_folder) - nur diese zwei Spalten, wie "Ordner bearbeiten"
--    in der Konsole. Bewusst NICHT: DELETE auf SYS_SCRIPT_STRUCTURE (Ordner loeschen bleibt in der Konsole).
GRANT UPDATE (Description, ParentID) ON dbo.SYS_SCRIPT_STRUCTURE TO [acmp_mcp_ccwriter];
GO

-- 7) Kontrolle: wirksame Rechte des Logins. Erwartet (mit allen Abschnitten):
--    SYS_SCRIPTS 1/1/1/0, SYS_SCRIPTS_VARIABLES 1/1/0/1, SYS_SCRIPT_FILES 1/1/0/1, SYS_SCRIPT_STRUCTURE select 1, insert 1,
--    delete 0 (UPDATE gilt nur fuer Spalten: zweite Abfrage, Description und ParentID 1, alle anderen 0),
--    SYS_Folders_AclEntries_* 1/1/0/0, SYS_AccessControl_AclEntries_* 0/1/0/0.
--    Steht unerwartet eine 1 bei UPDATE/DELETE, ist der Login vermutlich in db_datawriter oder db_owner -> entfernen.
EXECUTE AS USER = N'acmp_mcp_ccwriter';
SELECT t.tabelle,
       HAS_PERMS_BY_NAME(N'dbo.' + t.tabelle, N'OBJECT', N'SELECT') AS [select],
       HAS_PERMS_BY_NAME(N'dbo.' + t.tabelle, N'OBJECT', N'INSERT') AS [insert],
       HAS_PERMS_BY_NAME(N'dbo.' + t.tabelle, N'OBJECT', N'UPDATE') AS [update],
       HAS_PERMS_BY_NAME(N'dbo.' + t.tabelle, N'OBJECT', N'DELETE') AS [delete]
FROM (VALUES (N'SYS_SCRIPTS'), (N'SYS_SCRIPTS_VARIABLES'), (N'SYS_SCRIPT_FILES'), (N'SYS_SCRIPT_STRUCTURE'),
             (N'SYS_Folders_AclEntries_Users'), (N'SYS_Folders_AclEntries_Groups'),
             (N'SYS_AccessControl_AclEntries_Users'), (N'SYS_AccessControl_AclEntries_Groups')) AS t(tabelle);
SELECT c.spalte,
       HAS_PERMS_BY_NAME(N'dbo.SYS_SCRIPT_STRUCTURE', N'OBJECT', N'UPDATE', c.spalte, N'COLUMN') AS [update]
FROM (VALUES (N'Description'), (N'ParentID'), (N'ScriptStructureID'), (N'StrucLevel'), (N'IconId')) AS c(spalte);
REVERT;
GO

-- Danach: Dienst neu starten oder acmp_cc_writer_status mit refresh=true aufrufen.
-- Entziehen jederzeit:  USE [ACMP]; DROP USER [acmp_mcp_ccwriter];  USE [master]; DROP LOGIN [acmp_mcp_ccwriter];
