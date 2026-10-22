-- Rechte und Prozeduren fuer acmp_cf_create (individuelle Felder anlegen) und acmp_cc_set_parameters (Parameter eines
-- Client Commands setzen). Die Lesetools acmp_cf_list und acmp_cc_parameters brauchen nur db.connectionString.
-- Optional und standardmaessig aus. Direktes Schreiben in die ACMP-Datenbank ist von Aagon nicht vorgesehen:
-- vorher eine Sicherung der Datenbank anlegen.
--
-- So arbeitet das Tool (SQL-Mitschnitt ACMP 6.10.4): Beim Speichern in der Console fuehrt der ACMP-Server aus
--   CREATE TABLE CF_VAL_<24 Hex> (CLIENTID uniqueidentifier, PK) + GRANTs an die ACMP-Datenbankbenutzer + FK auf CLT_CLIENTS_TABLE
--   ON DELETE CASCADE, ALTER TABLE ... ADD CF_<28 Hex> BIGINT bzw. NVARCHAR(n), dazu Zeilen in SYS_TableRelations,
--   VW_SYS_DISPLAYFIELDS und SYS_CustomFields.
-- Der Schreib-Login des MCP bekommt KEINE DDL-Rechte. Die DDL laeuft ueber zwei Prozeduren im Besitz von dbo (EXECUTE AS OWNER), die
-- nur genau dieses Muster aus GUIDs bauen und vorher pruefen (nur individuelle, einfache Tabellen; Spalte/Tabelle darf es noch nicht geben).
-- Parameter: nur DML auf die drei Tabellen SYS_Jobs_Parameters_* (der ACMP-Server schreibt dort ebenso, keine DDL).
--
-- Beide Tools nutzen denselben SQL-Login wie der CC-Writer. Deshalb zuerst grant-cc-writer.sql ausfuehren
-- (legt Login und Datenbankbenutzer acmp_mcp_ccwriter an), dann dieses Skript als SQL-Admin, Abschnitt fuer Abschnitt.
-- Ggf. den Datenbanknamen [ACMP] anpassen. Wer nur die Parameter braucht, laesst Abschnitt 2 und den ersten Block von
-- Abschnitt 3 weg.
USE [ACMP];
GO

-- 1) VORHER ANSEHEN (nur lesen): Benutzer, Trigger auf den beteiligten Tabellen/Views.
--    Erwartet bei ACMP 6.10.4: die vier Benutzer (RLS_ACMPUser, AcmpUser_ReadOnly, AcmpUser_LowPrivilege als SQL_USER),
--    Trigger nur Trigger_Delete_SYS_TableRelations und die INSTEAD-OF-Trigger auf VW_SYS_DISPLAYFIELDS.
--    Fehlt einer der drei ACMP-Benutzer, passt das GRANT-Muster in mcp_cf_create_table nicht - dann acmp_cf_create nicht einrichten.
SELECT name, type_desc FROM sys.database_principals
WHERE name IN (N'acmp_mcp_ccwriter', N'RLS_ACMPUser', N'AcmpUser_ReadOnly', N'AcmpUser_LowPrivilege') ORDER BY name;
SELECT OBJECT_NAME(t.parent_id) AS objekt, t.name AS trigger_name, t.is_instead_of_trigger, t.is_disabled
FROM sys.triggers t
WHERE t.parent_id IN (OBJECT_ID(N'dbo.SYS_TableRelations'), OBJECT_ID(N'dbo.SYS_CustomFields'), OBJECT_ID(N'dbo.VW_SYS_DISPLAYFIELDS'),
                      OBJECT_ID(N'dbo.SYS_DISPLAYFIELDS'), OBJECT_ID(N'dbo.SYS_DisplayFields_Localization'), OBJECT_ID(N'dbo.SYS_DisplayFields_AvailableValues'),
                      OBJECT_ID(N'dbo.SYS_Jobs_Parameters_Declarations'), OBJECT_ID(N'dbo.SYS_Jobs_Parameters_Config'), OBJECT_ID(N'dbo.SYS_Jobs_Parameters_Config_Values'))
ORDER BY objekt, trigger_name;
GO

-- 2) PROZEDUREN ANLEGEN (Besitzer dbo, EXECUTE AS OWNER) - nur fuer acmp_cf_create. Erneut ausfuehren ersetzt sie.
IF OBJECT_ID(N'dbo.mcp_cf_create_table', N'P') IS NOT NULL DROP PROCEDURE dbo.mcp_cf_create_table;
GO
CREATE PROCEDURE dbo.mcp_cf_create_table
    @TableId uniqueidentifier
WITH EXECUTE AS OWNER
AS
BEGIN
    -- Neue Tabelle fuer individuelle Client-Felder, genau wie der ACMP-Server (Mitschnitt ACMP 6.10.4). Nur fuer den ACMP MCP (acmp_cf_create).
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @TableId IS NULL THROW 50101, N'mcp_cf_create_table: TableId fehlt.', 1;
    DECLARE @t sysname = N'CF_VAL_' + LEFT(REPLACE(UPPER(CONVERT(nvarchar(36), @TableId)), N'-', N''), 24);
    IF OBJECT_ID(N'dbo.' + @t) IS NOT NULL THROW 50102, N'mcp_cf_create_table: die Tabelle gibt es schon.', 1;
    IF EXISTS (SELECT 1 FROM dbo.SYS_TableRelations WHERE TableID = @TableId OR TABLENAME = @t)
        THROW 50103, N'mcp_cf_create_table: TableID bzw. Tabellenname ist in SYS_TableRelations schon vergeben.', 1;
    DECLARE @q nvarchar(300) = QUOTENAME(@t);
    DECLARE @sql nvarchar(max) =
        N'CREATE TABLE ' + @q + N' ([CLIENTID] uniqueidentifier, CONSTRAINT ' + QUOTENAME(N'PK_' + @t) + N' PRIMARY KEY ([CLIENTID]));' +
        N'GRANT SELECT, UPDATE, INSERT, DELETE ON ' + @q + N' TO RLS_ACMPUser;' +
        N'GRANT SELECT ON ' + @q + N' TO AcmpUser_ReadOnly;' +
        N'GRANT SELECT, UPDATE, INSERT, DELETE ON ' + @q + N' TO AcmpUser_LowPrivilege;' +
        N'ALTER TABLE ' + @q + N' ADD CONSTRAINT ' + QUOTENAME(N'FK_' + @t + N'_CLIENTID_CLT_CLIENTS_TABLE_CLIENTID') +
        N' FOREIGN KEY ([CLIENTID]) REFERENCES [CLT_CLIENTS_TABLE] ([CLIENTID]) ON DELETE CASCADE;';
    EXEC sys.sp_executesql @sql;
END
GO

IF OBJECT_ID(N'dbo.mcp_cf_add_column', N'P') IS NOT NULL DROP PROCEDURE dbo.mcp_cf_add_column;
GO
CREATE PROCEDURE dbo.mcp_cf_add_column
    @TableId uniqueidentifier,
    @FieldId uniqueidentifier,
    @InputMethod int,
    @MaxLength int
WITH EXECUTE AS OWNER
AS
BEGIN
    -- Neue Spalte in einer individuellen Client-Tabelle, Typ je Eingabemethode wie der ACMP-Server bzw. wie die vorhandenen CF_VAL-Tabellen:
    -- 13 Ganzzahl BIGINT, 10 Text / 9 Memo / 1 Auswahlliste NVARCHAR(MaxLength), 3 Datum FLOAT, 7 IPv4 CHAR(15). Nur fuer den ACMP MCP.
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @TableId IS NULL OR @FieldId IS NULL THROW 50111, N'mcp_cf_add_column: TableId oder FieldId fehlt.', 1;
    DECLARE @t sysname = N'CF_VAL_' + LEFT(REPLACE(UPPER(CONVERT(nvarchar(36), @TableId)), N'-', N''), 24);
    DECLARE @c sysname = N'CF_' + LEFT(REPLACE(UPPER(CONVERT(nvarchar(36), @FieldId)), N'-', N''), 28);
    IF NOT EXISTS (SELECT 1 FROM dbo.SYS_TableRelations WHERE TableID = @TableId AND TABLENAME = @t AND IsCustomTable = 1 AND ISNULL(IsMultiTable, 0) = 0)
        THROW 50112, N'mcp_cf_add_column: keine einfache individuelle Tabelle (SYS_TableRelations).', 1;
    IF OBJECT_ID(N'dbo.' + @t, N'U') IS NULL THROW 50113, N'mcp_cf_add_column: die Tabelle fehlt.', 1;
    IF COL_LENGTH(N'dbo.' + @t, @c) IS NOT NULL THROW 50114, N'mcp_cf_add_column: die Spalte gibt es schon.', 1;
    DECLARE @type nvarchar(40) =
        CASE WHEN @InputMethod = 13 THEN N'BIGINT'
             WHEN @InputMethod IN (1, 9, 10) AND @MaxLength BETWEEN 1 AND 4000 THEN N'NVARCHAR(' + CAST(@MaxLength AS nvarchar(10)) + N')'
             WHEN @InputMethod = 3 THEN N'FLOAT'
             WHEN @InputMethod = 7 THEN N'CHAR(15)'
        END;
    IF @type IS NULL THROW 50115, N'mcp_cf_add_column: Eingabemethode oder Laenge nicht erlaubt.', 1;
    DECLARE @sql nvarchar(max) = N'ALTER TABLE ' + QUOTENAME(@t) + N' ADD ' + QUOTENAME(@c) + N' ' + @type + N';';
    EXEC sys.sp_executesql @sql;
END
GO

-- 3) RECHTE VERGEBEN (bricht ab, wenn der Benutzer fehlt)
IF USER_ID(N'acmp_mcp_ccwriter') IS NULL
    THROW 50000, N'Datenbankbenutzer acmp_mcp_ccwriter fehlt - zuerst grant-cc-writer.sql ausfuehren. Nichts geaendert.', 1;
-- individuelle Felder (acmp_cf_create): nur die Prozeduren und die Metadaten-Zeilen (Lesen fuer die Kontrolle in der Transaktion)
GRANT EXECUTE ON OBJECT::dbo.mcp_cf_create_table TO [acmp_mcp_ccwriter];
GRANT EXECUTE ON OBJECT::dbo.mcp_cf_add_column TO [acmp_mcp_ccwriter];
GRANT SELECT, INSERT ON dbo.SYS_TableRelations TO [acmp_mcp_ccwriter];
GRANT SELECT, INSERT ON dbo.SYS_CustomFields TO [acmp_mcp_ccwriter];
GRANT SELECT, INSERT ON dbo.VW_SYS_DISPLAYFIELDS TO [acmp_mcp_ccwriter];
-- Client-Command-Parameter (acmp_cc_set_parameters)
GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.SYS_Jobs_Parameters_Declarations TO [acmp_mcp_ccwriter];
GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.SYS_Jobs_Parameters_Config TO [acmp_mcp_ccwriter];
GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.SYS_Jobs_Parameters_Config_Values TO [acmp_mcp_ccwriter];
GO

-- 4) KONTROLLE als Schreib-Login. Erwartet: alles 1, ausser den Spalten *_soll0 (0).
--    Steht dort eine 1, ist der Login vermutlich in db_ddladmin, db_datawriter oder db_owner -> entfernen.
EXECUTE AS USER = N'acmp_mcp_ccwriter';
SELECT HAS_PERMS_BY_NAME(N'dbo.mcp_cf_create_table', N'OBJECT', N'EXECUTE')                  AS exec_create_table,
       HAS_PERMS_BY_NAME(N'dbo.mcp_cf_add_column', N'OBJECT', N'EXECUTE')                    AS exec_add_column,
       HAS_PERMS_BY_NAME(N'dbo.SYS_TableRelations', N'OBJECT', N'INSERT')                    AS tablerel_insert,
       HAS_PERMS_BY_NAME(N'dbo.SYS_CustomFields', N'OBJECT', N'INSERT')                      AS customfields_insert,
       HAS_PERMS_BY_NAME(N'dbo.VW_SYS_DISPLAYFIELDS', N'OBJECT', N'INSERT')                  AS displayfields_insert,
       HAS_PERMS_BY_NAME(N'dbo.SYS_Jobs_Parameters_Declarations', N'OBJECT', N'DELETE')      AS params_decl_delete,
       HAS_PERMS_BY_NAME(N'dbo.SYS_Jobs_Parameters_Config', N'OBJECT', N'UPDATE')            AS params_config_update,
       HAS_PERMS_BY_NAME(N'dbo.SYS_Jobs_Parameters_Config_Values', N'OBJECT', N'INSERT')     AS params_values_insert,
       HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'CREATE TABLE')                            AS create_table_soll0,
       HAS_PERMS_BY_NAME(N'dbo.SYS_DISPLAYFIELDS', N'OBJECT', N'INSERT')                     AS displayfields_tabelle_insert_soll0,
       HAS_PERMS_BY_NAME(N'dbo.CLT_CLIENTS_TABLE', N'OBJECT', N'ALTER')                      AS clients_alter_soll0;
REVERT;
GO

-- Danach: fuer acmp_cf_create setup-extra-writers.ps1 (als Admin, cfWriter); acmp_cc_set_parameters ist Teil des CC-Writers
-- und nach einem Neustart des Dienstes aktiv. Pruefung im MCP-Client mit acmp_cf_list status=true bzw. acmp_cc_parameters status=true.
-- Rueckgaengig machen (nur bei Bedarf, Kommentarzeichen entfernen):
-- REVOKE EXECUTE ON OBJECT::dbo.mcp_cf_create_table FROM [acmp_mcp_ccwriter];
-- REVOKE EXECUTE ON OBJECT::dbo.mcp_cf_add_column FROM [acmp_mcp_ccwriter];
-- REVOKE SELECT, INSERT ON dbo.SYS_TableRelations FROM [acmp_mcp_ccwriter];
-- REVOKE SELECT, INSERT ON dbo.SYS_CustomFields FROM [acmp_mcp_ccwriter];
-- REVOKE SELECT, INSERT ON dbo.VW_SYS_DISPLAYFIELDS FROM [acmp_mcp_ccwriter];
-- REVOKE SELECT, INSERT, UPDATE, DELETE ON dbo.SYS_Jobs_Parameters_Declarations FROM [acmp_mcp_ccwriter];
-- REVOKE SELECT, INSERT, UPDATE, DELETE ON dbo.SYS_Jobs_Parameters_Config FROM [acmp_mcp_ccwriter];
-- REVOKE SELECT, INSERT, UPDATE, DELETE ON dbo.SYS_Jobs_Parameters_Config_Values FROM [acmp_mcp_ccwriter];
-- DROP PROCEDURE dbo.mcp_cf_create_table; DROP PROCEDURE dbo.mcp_cf_add_column;
