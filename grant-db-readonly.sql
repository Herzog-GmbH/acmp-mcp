-- Read-Only-DB-Zugriff fuer den ACMP-MCP-Dienst.
-- Der Dienst laeuft typischerweise als SYSTEM auf eurem ACMP-Server und authentifiziert sich
-- ueber das Maschinenkonto EURE-DOMAIN\EUERSERVERNAME$ (Windows-Auth, KEIN Passwort noetig).
-- Auf eurer ACMP-SQL-Instanz als SQL-Admin ausfuehren.

-- 1) Login fuer das Maschinenkonto (master)
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'EURE-DOMAIN\EUERSERVERNAME$')
    CREATE LOGIN [EURE-DOMAIN\EUERSERVERNAME$] FROM WINDOWS;

-- 2) Nur Leserecht auf die ACMP-Datenbank
USE [ACMP];
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'EURE-DOMAIN\EUERSERVERNAME$')
    CREATE USER [EURE-DOMAIN\EUERSERVERNAME$] FOR LOGIN [EURE-DOMAIN\EUERSERVERNAME$];
ALTER ROLE db_datareader ADD MEMBER [EURE-DOMAIN\EUERSERVERNAME$];

-- Entziehen jederzeit:  USE [ACMP]; DROP USER [EURE-DOMAIN\EUERSERVERNAME$];

-- ALTERNATIVE (falls Maschinenkonto unerwuenscht): dedizierter SQL-Login mit Passwort.
-- Dann in config.local.json: "Server=EURE-SQL-INSTANZ;Database=ACMP;User ID=acmp_mcp_ro;Password=...;TrustServerCertificate=True"
--   CREATE LOGIN [acmp_mcp_ro] WITH PASSWORD = N'<starkes-Passwort>', CHECK_POLICY = ON;
--   USE [ACMP]; CREATE USER [acmp_mcp_ro] FOR LOGIN [acmp_mcp_ro];
--   ALTER ROLE db_datareader ADD MEMBER [acmp_mcp_ro];
