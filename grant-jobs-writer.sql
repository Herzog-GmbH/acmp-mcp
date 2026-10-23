-- Rechte fuer den Job-Writer: Dauerauftraege abschalten, einschalten, loeschen und zuruecknehmen (acmp_job_set_enabled,
-- acmp_job_delete, acmp_job_restore) und ihre Startbedingung setzen (acmp_job_set_condition, braucht keine weiteren Rechte).
-- Die Lesetools acmp_jobs und acmp_get_jobs brauchen nur db.connectionString.
-- Optional und standardmaessig aus. Direktes Schreiben in die ACMP-Datenbank ist von Aagon nicht vorgesehen:
-- vorher eine Sicherung der Datenbank anlegen.
-- Der Job-Writer nutzt denselben SQL-Login wie der CC-Writer. Deshalb zuerst grant-cc-writer.sql ausfuehren
-- (legt Login und Datenbankbenutzer acmp_mcp_ccwriter an), dann dieses Skript als SQL-Admin, Abschnitt fuer Abschnitt
-- oder alles. Ggf. den Datenbanknamen [ACMP] anpassen. Danach: setup-extra-writers.ps1 -Features jobs
--
-- So arbeitet das Tool (SQL-Mitschnitt der ACMP-Console 6.10):
--   Abschalten/Einschalten = UPDATE SYS_Jobs (Conditions, ConditionCheckSum) + UPDATE SYS_Jobs_Logs (ConditionText)
--   Loeschen = DELETE SYS_Templates_Jobs; der ACMP-Trigger TG_Delete_SYS_Templates_Jobs loescht den Job aus SYS_Jobs und setzt
--   SYS_Jobs_Logs.State (Trigger und Tabellen gehoeren dbo -> Besitzerverkettung, kein DELETE auf SYS_Jobs noetig)
--   Zuruecknehmen = UPDATE wie oben bzw. INSERT SYS_Jobs + SYS_Templates_Jobs, UPDATE SYS_Jobs_Logs (State, ConditionText)
--
-- Bewusst NICHT vergeben: DELETE auf SYS_Jobs, UPDATE auf andere Spalten von SYS_Jobs (JobAction, ContainerId, Name ...),
-- Rechte auf SYS_Templates ausser SELECT, SYS_Containers_Templates.
USE [ACMP];
GO

-- 0) Gibt es den Datenbankbenutzer? (erwartet: 1 Zeile, SQL_USER; sonst erst grant-cc-writer.sql)
SELECT name, type_desc, create_date FROM sys.database_principals WHERE name = N'acmp_mcp_ccwriter';
GO

-- 1) Vorher ansehen: Trigger auf den beteiligten Tabellen. Erwartet: genau 1 Zeile, SYS_Templates_Jobs / TG_Delete_SYS_Templates_Jobs.
--    Der Server sperrt die Schreibtools bei unbekannten Triggern (Vorpruefung in acmp_jobs mit status=true).
SELECT OBJECT_NAME(t.parent_id) AS tabelle, t.name AS trigger_name, t.is_disabled
FROM sys.triggers t
WHERE t.parent_id IN (OBJECT_ID(N'dbo.SYS_Jobs'), OBJECT_ID(N'dbo.SYS_Templates_Jobs'), OBJECT_ID(N'dbo.SYS_Jobs_Logs'));
GO

-- 2) Rechte vergeben (bricht ab, wenn der Benutzer fehlt)
IF USER_ID(N'acmp_mcp_ccwriter') IS NULL
    THROW 50000, N'Datenbankbenutzer acmp_mcp_ccwriter fehlt - zuerst grant-cc-writer.sql ausfuehren. Nichts geaendert.', 1;
-- Schalten
GRANT SELECT ON dbo.SYS_Jobs TO [acmp_mcp_ccwriter];
GRANT UPDATE ON dbo.SYS_Jobs (Conditions, ConditionCheckSum) TO [acmp_mcp_ccwriter];
GRANT SELECT ON dbo.SYS_Jobs_Logs TO [acmp_mcp_ccwriter];
GRANT UPDATE ON dbo.SYS_Jobs_Logs (ConditionText, State) TO [acmp_mcp_ccwriter];
-- Loeschen (ueber die Verknuepfung, der ACMP-Trigger erledigt den Rest)
GRANT SELECT, DELETE ON dbo.SYS_Templates_Jobs TO [acmp_mcp_ccwriter];
-- Zuruecknehmen eines geloeschten Jobs
GRANT INSERT ON dbo.SYS_Jobs TO [acmp_mcp_ccwriter];
GRANT INSERT ON dbo.SYS_Templates_Jobs TO [acmp_mcp_ccwriter];
GRANT SELECT ON dbo.SYS_Templates TO [acmp_mcp_ccwriter];
GO

-- 3) Kontrolle: wirksame Rechte des Logins. Erwartet: alles 1, ausser den *_soll0-Spalten (0).
EXECUTE AS USER = N'acmp_mcp_ccwriter';
SELECT HAS_PERMS_BY_NAME(N'dbo.SYS_Jobs', N'OBJECT', N'SELECT')                                   AS jobs_select,
       HAS_PERMS_BY_NAME(N'dbo.SYS_Jobs', N'OBJECT', N'UPDATE', N'Conditions', N'COLUMN')          AS jobs_upd_conditions,
       HAS_PERMS_BY_NAME(N'dbo.SYS_Jobs', N'OBJECT', N'UPDATE', N'ConditionCheckSum', N'COLUMN')   AS jobs_upd_checksum,
       HAS_PERMS_BY_NAME(N'dbo.SYS_Jobs', N'OBJECT', N'INSERT')                                   AS jobs_insert,
       HAS_PERMS_BY_NAME(N'dbo.SYS_Jobs_Logs', N'OBJECT', N'UPDATE', N'ConditionText', N'COLUMN')  AS logs_upd_text,
       HAS_PERMS_BY_NAME(N'dbo.SYS_Jobs_Logs', N'OBJECT', N'UPDATE', N'State', N'COLUMN')          AS logs_upd_state,
       HAS_PERMS_BY_NAME(N'dbo.SYS_Templates_Jobs', N'OBJECT', N'DELETE')                         AS tj_delete,
       HAS_PERMS_BY_NAME(N'dbo.SYS_Templates_Jobs', N'OBJECT', N'INSERT')                         AS tj_insert,
       HAS_PERMS_BY_NAME(N'dbo.SYS_Templates', N'OBJECT', N'SELECT')                              AS tpl_select,
       HAS_PERMS_BY_NAME(N'dbo.SYS_Jobs', N'OBJECT', N'DELETE')                                   AS jobs_delete_soll0,
       HAS_PERMS_BY_NAME(N'dbo.SYS_Jobs', N'OBJECT', N'UPDATE', N'JobAction', N'COLUMN')           AS jobs_upd_jobaction_soll0,
       HAS_PERMS_BY_NAME(N'dbo.SYS_Templates', N'OBJECT', N'DELETE')                              AS tpl_delete_soll0;
REVERT;
GO

-- Danach: acmp_jobs mit status=true (bzw. Dienst-Neustart) -> Vorpruefung "Rechte Schalten/Loeschen/Zuruecknehmen" ok.
-- Rueckgaengig machen (nur bei Bedarf, Kommentarzeichen entfernen):
-- REVOKE SELECT, INSERT ON dbo.SYS_Jobs FROM [acmp_mcp_ccwriter];
-- REVOKE UPDATE ON dbo.SYS_Jobs (Conditions, ConditionCheckSum) FROM [acmp_mcp_ccwriter];
-- REVOKE SELECT ON dbo.SYS_Jobs_Logs FROM [acmp_mcp_ccwriter];
-- REVOKE UPDATE ON dbo.SYS_Jobs_Logs (ConditionText, State) FROM [acmp_mcp_ccwriter];
-- REVOKE SELECT, DELETE, INSERT ON dbo.SYS_Templates_Jobs FROM [acmp_mcp_ccwriter];
-- REVOKE SELECT ON dbo.SYS_Templates FROM [acmp_mcp_ccwriter];
