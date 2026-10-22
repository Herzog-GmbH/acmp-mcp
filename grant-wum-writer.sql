-- Rechte fuer den WUM-Writer: Windows-Updates der ACMP-Update-Verwaltung (CAWUM) ablehnen und die Ablehnung aufheben
-- (acmp_wum_decline, acmp_wum_restore). Das Lesetool acmp_wum_declined braucht nur db.connectionString.
-- Optional und standardmaessig aus. Direktes Schreiben in die ACMP-Datenbank ist von Aagon nicht vorgesehen:
-- vorher eine Sicherung der Datenbank anlegen.
--
-- So arbeitet das Tool (Analyse ACMP 6.10): Ablehnen = Console-Prozedur dbo.ap_Wum_DeclineUpdates (MERGE nur in
-- Sys_Wum_DeclinedUpdates), danach dbo.ap_Wum_Rebuild_UpdateFlags (setzt Sys_Wum_Updates.IsDeclined). Prozeduren und Tabelle
-- gehoeren dbo (Besitzverkettung): Der Schreib-Login braucht keine Rechte auf Sys_Wum_Updates und kein INSERT auf
-- Sys_Wum_DeclinedUpdates. Aufheben = DELETE der einen Zeile in Sys_Wum_DeclinedUpdates, danach wieder die Flags.
--
-- Der WUM-Writer nutzt denselben SQL-Login wie der CC-Writer. Deshalb zuerst grant-cc-writer.sql ausfuehren
-- (legt Login und Datenbankbenutzer acmp_mcp_ccwriter an), dann dieses Skript als SQL-Admin, Abschnitt fuer Abschnitt
-- oder alles. Ggf. den Datenbanknamen [ACMP] anpassen.
--
-- Bewusst NICHT vergeben:
--   * INSERT/UPDATE auf Sys_Wum_DeclinedUpdates und UPDATE auf Sys_Wum_Updates (das Tool geht nur ueber die Prozeduren)
--   * DELETE auf andere Tabellen
USE [ACMP];
GO

-- 0) Gibt es den Datenbankbenutzer? (erwartet: 1 Zeile, SQL_USER; sonst erst grant-cc-writer.sql)
SELECT name, type_desc, create_date
FROM sys.database_principals
WHERE name = N'acmp_mcp_ccwriter';
GO

-- 1) Vorher ansehen: Trigger auf den beteiligten Tabellen. Erwartet: KEINE Zeile.
--    Der Server sperrt die Schreibtools bei unbekannten Triggern (Vorpruefung in acmp_wum_declined mit status=true).
SELECT OBJECT_NAME(t.parent_id) AS tabelle, t.name AS trigger_name, t.is_disabled
FROM sys.triggers t
WHERE t.parent_id IN (OBJECT_ID(N'dbo.Sys_Wum_DeclinedUpdates'), OBJECT_ID(N'dbo.Sys_Wum_Updates'));
GO

-- 2) Ablehnen (Pflicht fuer acmp_wum_decline; bricht ab, wenn der Benutzer fehlt)
IF USER_ID(N'acmp_mcp_ccwriter') IS NULL
    THROW 50000, N'Datenbankbenutzer acmp_mcp_ccwriter fehlt - zuerst grant-cc-writer.sql ausfuehren. Nichts geaendert.', 1;
-- Prozedur, Tabellentyp fuer die Id-Liste und Lesen der Ablehnungsliste (Kontrolle in der Transaktion)
GRANT EXECUTE ON OBJECT::dbo.ap_Wum_DeclineUpdates TO [acmp_mcp_ccwriter];
GRANT EXECUTE ON TYPE::dbo.at_IDList TO [acmp_mcp_ccwriter];
GRANT SELECT ON dbo.Sys_Wum_DeclinedUpdates TO [acmp_mcp_ccwriter];
-- Flags neu aufbauen (optional: ohne dieses Recht holt ACMP das Flag IsDeclined beim naechsten Lauf selbst nach)
GRANT EXECUTE ON OBJECT::dbo.ap_Wum_Rebuild_UpdateFlags TO [acmp_mcp_ccwriter];
GO

-- 3) Nur fuer acmp_wum_restore (Ablehnung aufheben): DELETE auf die Ablehnungsliste. Ohne dieses Recht bleibt nur dieses Tool gesperrt.
IF USER_ID(N'acmp_mcp_ccwriter') IS NULL
    THROW 50000, N'Datenbankbenutzer acmp_mcp_ccwriter fehlt - nichts geaendert.', 1;
GRANT DELETE ON dbo.Sys_Wum_DeclinedUpdates TO [acmp_mcp_ccwriter];
GO

-- 4) Kontrolle: wirksame Rechte des Logins. Erwartet: alles 1, ausser den Spalten *_soll0 (0).
--    Steht dort eine 1, ist der Login vermutlich in db_datawriter oder db_owner -> entfernen.
EXECUTE AS USER = N'acmp_mcp_ccwriter';
SELECT HAS_PERMS_BY_NAME(N'dbo.ap_Wum_DeclineUpdates', N'OBJECT', N'EXECUTE')      AS exec_decline,
       HAS_PERMS_BY_NAME(N'dbo.at_IDList', N'TYPE', N'EXECUTE')                      AS exec_type,
       HAS_PERMS_BY_NAME(N'dbo.ap_Wum_Rebuild_UpdateFlags', N'OBJECT', N'EXECUTE')   AS exec_rebuild,
       HAS_PERMS_BY_NAME(N'dbo.Sys_Wum_DeclinedUpdates', N'OBJECT', N'SELECT')       AS declined_select,
       HAS_PERMS_BY_NAME(N'dbo.Sys_Wum_DeclinedUpdates', N'OBJECT', N'DELETE')       AS declined_delete,
       HAS_PERMS_BY_NAME(N'dbo.Sys_Wum_DeclinedUpdates', N'OBJECT', N'INSERT')       AS declined_insert_soll0,
       HAS_PERMS_BY_NAME(N'dbo.Sys_Wum_DeclinedUpdates', N'OBJECT', N'UPDATE')       AS declined_update_soll0,
       HAS_PERMS_BY_NAME(N'dbo.Sys_Wum_Updates', N'OBJECT', N'UPDATE')               AS updates_update_soll0;
REVERT;
GO

-- Danach: setup-extra-writers.ps1 (als Admin), Pruefung im MCP-Client mit acmp_wum_declined status=true.
-- Rueckgaengig machen (nur bei Bedarf, Kommentarzeichen entfernen):
-- REVOKE EXECUTE ON OBJECT::dbo.ap_Wum_DeclineUpdates FROM [acmp_mcp_ccwriter];
-- REVOKE EXECUTE ON TYPE::dbo.at_IDList FROM [acmp_mcp_ccwriter];
-- REVOKE EXECUTE ON OBJECT::dbo.ap_Wum_Rebuild_UpdateFlags FROM [acmp_mcp_ccwriter];
-- REVOKE SELECT, DELETE ON dbo.Sys_Wum_DeclinedUpdates FROM [acmp_mcp_ccwriter];
