-- Rechte fuer den Kiosk-Writer (Kiosk-Eintraege, -Ordner, Sichtbarkeitsfilter und Icons direkt in der ACMP-DB pflegen).
-- Optional und standardmaessig aus. Direktes Schreiben in die ACMP-Datenbank ist von Aagon nicht vorgesehen:
-- vorher eine Sicherung der Datenbank anlegen und zuerst nur in einem Test-Kioskordner arbeiten.
--
-- Der Kiosk-Writer nutzt denselben SQL-Login wie der CC-Writer. Deshalb zuerst grant-cc-writer.sql ausfuehren
-- (legt Login und Datenbankbenutzer acmp_mcp_ccwriter an), dann dieses Skript als SQL-Admin, Abschnitt fuer Abschnitt
-- oder alles. Ggf. den Datenbanknamen [ACMP] anpassen.
--
-- Bewusst NICHT vergeben:
--   * DELETE auf SYS_Kiosk_Groups  (loescht per CASCADE alle Eintraege eines Ordners; Ordner loeschen bleibt in der Konsole)
--   * DELETE auf SYS_MultiLanguageItems(_Memo)  (die Tabellen teilen sich mehrere ACMP-Module. Beim Entfernen eines Eintrags
--     raeumt der ACMP-Trigger TR_SYS_Kiosk_Items_Delete Name, Beschreibung und Filter selbst ab - per Besitzverkettung,
--     ohne eigenes DELETE-Recht des Logins. Der Server sichert die Zeilen vorher.)
--   * irgendetwas auf SYS_Kiosk_Appearance (das Aussehen des Kiosk bleibt Sache der Konsole)
USE [ACMP];
GO

-- 0) Gibt es den Datenbankbenutzer? (erwartet: 1 Zeile, SQL_USER; sonst erst grant-cc-writer.sql)
SELECT name, type_desc, create_date
FROM sys.database_principals
WHERE name = N'acmp_mcp_ccwriter';
GO

-- 1) Vorher ansehen: Trigger auf den Kiosk-Tabellen. Der Server kennt von ACMP 6.10 nur TR_SYS_Kiosk_Items_Delete
--    und sperrt das Schreiben bei unbekannten Triggern.
SELECT OBJECT_NAME(t.parent_id) AS tabelle, t.name AS trigger_name, t.is_disabled
FROM sys.triggers t
WHERE t.parent_id IN (OBJECT_ID(N'dbo.SYS_Kiosk_Items'), OBJECT_ID(N'dbo.SYS_Kiosk_Groups'), OBJECT_ID(N'dbo.SYS_Kiosk_Filters'),
                      OBJECT_ID(N'dbo.SYS_Kiosk_Filters_ActiveDirectoryUserPropertyNames'),
                      OBJECT_ID(N'dbo.SYS_Kiosk_Filters_ActiveDirectoryComputerPropertyNames'),
                      OBJECT_ID(N'dbo.SYS_MultiLanguageItems'), OBJECT_ID(N'dbo.SYS_MultiLanguageItems_Memo'),
                      OBJECT_ID(N'dbo.SYS_CustomImages'));
GO

-- 2) Vorher ansehen: Fremdschluessel (bestimmen die Reihenfolge beim Einfuegen und Entfernen)
SELECT fk.name AS fk_name, OBJECT_NAME(fk.parent_object_id) AS tabelle, OBJECT_NAME(fk.referenced_object_id) AS verweist_auf,
       fk.delete_referential_action_desc AS bei_loeschen
FROM sys.foreign_keys fk
WHERE OBJECT_NAME(fk.parent_object_id) LIKE N'SYS[_]Kiosk%' OR OBJECT_NAME(fk.referenced_object_id) LIKE N'SYS[_]Kiosk%'
   OR OBJECT_NAME(fk.parent_object_id) LIKE N'SYS[_]MultiLanguageItems%' OR OBJECT_NAME(fk.referenced_object_id) LIKE N'SYS[_]MultiLanguageItems%';
GO

-- 3) Kiosk-Eintraege, Filter, Ordner, Namen (Pflicht fuer den Kiosk-Writer)
IF USER_ID(N'acmp_mcp_ccwriter') IS NULL
    THROW 50000, N'Datenbankbenutzer acmp_mcp_ccwriter fehlt - zuerst grant-cc-writer.sql ausfuehren. Nichts geaendert.', 1;
BEGIN TRANSACTION;
    -- Eintraege: anlegen, aendern (Name/Icon/Ordner/Command), entfernen
    GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.SYS_Kiosk_Items TO [acmp_mcp_ccwriter];
    -- Filter: setzen, ersetzen, beim Entfernen eines Eintrags mit entfernen
    GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.SYS_Kiosk_Filters TO [acmp_mcp_ccwriter];
    GRANT SELECT, INSERT, DELETE         ON dbo.SYS_Kiosk_Filters_ActiveDirectoryUserPropertyNames TO [acmp_mcp_ccwriter];
    GRANT SELECT, INSERT, DELETE         ON dbo.SYS_Kiosk_Filters_ActiveDirectoryComputerPropertyNames TO [acmp_mcp_ccwriter];
    -- Ordner: anlegen und aendern (Name, Icon, Filter), nicht loeschen
    GRANT SELECT, INSERT, UPDATE         ON dbo.SYS_Kiosk_Groups TO [acmp_mcp_ccwriter];
    -- Namen und Beschreibungen (DE/EN): anlegen und aendern, nicht loeschen
    GRANT SELECT, INSERT, UPDATE         ON dbo.SYS_MultiLanguageItems TO [acmp_mcp_ccwriter];
    GRANT SELECT, INSERT, UPDATE         ON dbo.SYS_MultiLanguageItems_Memo TO [acmp_mcp_ccwriter];
    -- Nur lesen: Icons und Views, die der Kiosk auswertet
    GRANT SELECT ON dbo.SYS_CustomImages TO [acmp_mcp_ccwriter];
    GRANT SELECT ON dbo.VW_SYS_Kiosk_Items TO [acmp_mcp_ccwriter];
    GRANT SELECT ON dbo.VW_SYS_Kiosk_AppItems_ClientCommand TO [acmp_mcp_ccwriter];
    GRANT SELECT ON dbo.VW_SYS_Kiosk_AppGroups TO [acmp_mcp_ccwriter];
COMMIT TRANSACTION;
GO

-- 4) Optional: Icons hochladen und entfernen (acmp_kiosk_add_image / acmp_kiosk_remove_image).
--    acmp_kiosk_set_image braucht nur UPDATE auf Items/Groups aus Abschnitt 3.
--    DELETE nutzt der Server nur fuer Bilder, die er selbst hochgeladen hat und die nirgends benutzt werden. Achtung: Die
--    Fremdschluessel auf SYS_CustomImages sind ON DELETE SET NULL - ein geloeschtes, noch benutztes Bild wuerde dort das Icon
--    leeren. Deshalb prueft der Server das vorher.
IF USER_ID(N'acmp_mcp_ccwriter') IS NULL
    THROW 50000, N'Datenbankbenutzer acmp_mcp_ccwriter fehlt - nichts geaendert.', 1;
GRANT SELECT, INSERT, DELETE ON dbo.SYS_CustomImages TO [acmp_mcp_ccwriter];
GO

-- 5) Kontrolle: wirksame Rechte des Logins. Erwartet:
--    SYS_Kiosk_Items und SYS_Kiosk_Filters 1/1/1/1, *_PropertyNames 1/1/0/1, SYS_Kiosk_Groups 1/1/1/0,
--    SYS_MultiLanguageItems(_Memo) 1/1/1/0, SYS_CustomImages 1/1/0/1 (ohne Abschnitt 4: 1/0/0/0), SYS_Kiosk_Appearance 0/0/0/0,
--    SYS_SCRIPTS DELETE 0. Steht unerwartet eine 1 bei INSERT/UPDATE/DELETE, ist der Login vermutlich in db_datawriter
--    oder db_owner -> entfernen.
EXECUTE AS USER = N'acmp_mcp_ccwriter';
SELECT t.tabelle,
       HAS_PERMS_BY_NAME(N'dbo.' + t.tabelle, N'OBJECT', N'SELECT') AS [select],
       HAS_PERMS_BY_NAME(N'dbo.' + t.tabelle, N'OBJECT', N'INSERT') AS [insert],
       HAS_PERMS_BY_NAME(N'dbo.' + t.tabelle, N'OBJECT', N'UPDATE') AS [update],
       HAS_PERMS_BY_NAME(N'dbo.' + t.tabelle, N'OBJECT', N'DELETE') AS [delete]
FROM (VALUES (N'SYS_Kiosk_Items'), (N'SYS_Kiosk_Filters'),
             (N'SYS_Kiosk_Filters_ActiveDirectoryUserPropertyNames'), (N'SYS_Kiosk_Filters_ActiveDirectoryComputerPropertyNames'),
             (N'SYS_Kiosk_Groups'), (N'SYS_MultiLanguageItems'), (N'SYS_MultiLanguageItems_Memo'),
             (N'SYS_CustomImages'), (N'SYS_Kiosk_Appearance'), (N'SYS_SCRIPTS')) AS t(tabelle);
REVERT;
GO

-- Danach: Dienst neu starten; Pruefung im MCP-Client mit acmp_kiosk_tree status=true.
-- Rueckgaengig machen (nur bei Bedarf, Kommentarzeichen entfernen):
-- REVOKE SELECT, INSERT, UPDATE, DELETE ON dbo.SYS_Kiosk_Items FROM [acmp_mcp_ccwriter];
-- REVOKE SELECT, INSERT, UPDATE, DELETE ON dbo.SYS_Kiosk_Filters FROM [acmp_mcp_ccwriter];
-- REVOKE SELECT, INSERT, DELETE ON dbo.SYS_Kiosk_Filters_ActiveDirectoryUserPropertyNames FROM [acmp_mcp_ccwriter];
-- REVOKE SELECT, INSERT, DELETE ON dbo.SYS_Kiosk_Filters_ActiveDirectoryComputerPropertyNames FROM [acmp_mcp_ccwriter];
-- REVOKE SELECT, INSERT, UPDATE ON dbo.SYS_Kiosk_Groups FROM [acmp_mcp_ccwriter];
-- REVOKE SELECT, INSERT, UPDATE ON dbo.SYS_MultiLanguageItems FROM [acmp_mcp_ccwriter];
-- REVOKE SELECT, INSERT, UPDATE ON dbo.SYS_MultiLanguageItems_Memo FROM [acmp_mcp_ccwriter];
-- REVOKE SELECT, INSERT, DELETE ON dbo.SYS_CustomImages FROM [acmp_mcp_ccwriter];
-- REVOKE SELECT ON dbo.VW_SYS_Kiosk_Items FROM [acmp_mcp_ccwriter];
-- REVOKE SELECT ON dbo.VW_SYS_Kiosk_AppItems_ClientCommand FROM [acmp_mcp_ccwriter];
-- REVOKE SELECT ON dbo.VW_SYS_Kiosk_AppGroups FROM [acmp_mcp_ccwriter];
