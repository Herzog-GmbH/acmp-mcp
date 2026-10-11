# ACMP vorbereiten

Diese Schritte passieren in der **ACMP-Konsole** bzw. **AESB-Konsole**. Kein Skript kann sie euch abnehmen. Ein KI-Agent kann sie nur erklären, klicken müsst ihr selbst.

## Pflicht

### 1. Public API freischalten
ACMP-Konsole → **System → Einstellungen → ACMP Server → SICS-Verbindung**
- **„Public API – Zugriff gewähren“** aktivieren.
- Notieren: **Virtual Router** (meist `VCMN`). Weicht er ab, in `config.local.json` unter `sics.virtualRouter` eintragen.

### 2. SICS-/AESB-Benutzer
Der MCP-Server braucht einen SICS-Benutzer mit Passwort für die Grundverbindung. Das kann der bestehende Benutzer aus der SICS-Verbindung sein oder ein eigener, den ihr in der AESB-Konsole anlegt.

> Ein SICS-Benutzer gilt in ACMP als Admin **ohne** Rechteprüfung. Deshalb bekommt jeder Bediener über `/onboarding` und `acmp-token-setup.exe` zusätzlich eine eigene ACMP-Identität. Dann prüft ACMP die echten Rechte dieser Person.

### 3. Ports
Der MCP-Server läuft **auf dem ACMP-Server selbst** und spricht SICS lokal über `wss://127.0.0.1:3950` an. Nach außen braucht er nur seinen eigenen HTTP-Port (Standard `8765`).

## Optional

### Ad-hoc-PowerShell (`acmp_run_powershell`, `acmp_read_registry`)
Ihr braucht ein Client Command als **Vorlage**:
1. Neues Client Command anlegen, z. B. „MCP PowerShell“.
2. Einen Schritt **„PowerShell-Script ausführen“** hinzufügen. Der Skriptinhalt ist genau: `%SCRIPT%`
   - Nur Prozentzeichen verwenden: `[SCRIPT]` oder `<%SCRIPT%>` werden **nicht** ersetzt.
   - **Exit-Code-** und **Ausgabe-Variable** NICHT auf `SCRIPT` setzen. Nehmt eigene Namen, z. B. `SCRIPT_EXITCODE` und `SCRIPT_OUTPUT`, sonst ist `%SCRIPT%` beim Ausführen leer.
3. Das Command freigeben. Seine GUID tragt ihr in `config.local.json` unter `runPowershell.clientCommandId` ein (`variableName` bleibt `SCRIPT`).

Gut zu wissen:
- Der Server übergibt jedes Skript als eine einzige Base64-Zeile, denn die ACMP-Variablenersetzung zerstört Zeilenumbrüche. Ein `param()`-Block am Skriptanfang funktioniert deshalb nicht.
- Der Job-Status „Erfolg“ beweist **nicht**, dass das Skript lief, denn das Command wertet den Exit-Code nicht aus. Prüft im Zweifel die Wirkung. Das clientseitige Log liegt in `C:\ProgramData\acmp-mcp-client.log`.
- `acmp_read_registry` meldet das Ergebnis per HTTP an `approval.publicUrl` + `/regresult` zurück. Diese Adresse muss **von den Clients aus** erreichbar sein (siehe `set-network-admin.ps1`).

### DB-Report-Tools (Schwachstellen, Inventar, Lizenzen …)
Für diese Tools braucht der Server Lesezugriff auf die ACMP-SQL-Datenbank:
- `grant-db-readonly.sql` anpassen und als SQL-Admin ausführen. Es gibt dem Maschinenkonto des ACMP-Servers `db_datareader`.
- In `config.local.json` den Abschnitt `db.connectionString` setzen.

### E-Mail bei neuen Freigabe-Anfragen
Unter `approval.notify` einen SMTP-Relay eintragen. Ohne diesen Eintrag funktioniert alles andere genauso.
