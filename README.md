# ACMP-MCP-Server

Ein [MCP](https://modelcontextprotocol.io/)-Server für die **ACMP Public API** ([Aagon Client Management Platform](https://www.aagon.com/)). Er stellt ACMP als Werkzeuge für jeden MCP-fähigen KI-Client bereit (Claude, ChatGPT/Codex, GitHub Copilot, Gemini, Cursor …): Clients, Container, Jobs/Client Commands, Tickets, Assets, Inventar und Stammdaten. Eingebaut sind eine **Vier-Augen-Freigabe** für schreibende, ausführende und löschende Aktionen und eine Weboberfläche zur Verwaltung.

Er läuft als eigenständige Windows-EXE direkt auf eurem ACMP-Server: kein IIS, kein .NET-SDK, keine Cloud.

> *English summary: MCP server for Aagon ACMP's Public API, with a built-in four-eyes approval gate for write/execute/delete actions. Runs as a single Windows EXE on the ACMP server and works with any MCP client. Docs are in German because ACMP's user base is mostly DACH; issues in English are welcome.*

## Am einfachsten: von eurer KI einrichten lassen

Auf dem **ACMP-Server** das Repo klonen oder als ZIP entpacken. Dort einen KI-Agenten starten, der Befehle ausführen kann – egal welchen: Claude Code, OpenAI Codex CLI, GitHub Copilot (Agent-Modus in VS Code), Gemini CLI, Cursor o. Ä. Dann Folgendes eingeben:

```text
Richte diesen ACMP-MCP-Server auf diesem Rechner ein. Folge AGENTS.md Schritt für Schritt,
erkläre mir jeden Admin-Schritt, bevor ich ihn ausführe, und frag mich nie nach Passwörtern
oder API-Keys im Chat.
```

[`AGENTS.md`](AGENTS.md) enthält den kompletten Ablauf für den Agenten: Prüfung, Installation, Ersteinrichtung, LAN-Freigabe, Client-Anbindung und Fehlersuche. Die meisten Agenten lesen `AGENTS.md` von selbst. Für Claude Code (`CLAUDE.md`), Gemini CLI (`GEMINI.md`) und GitHub Copilot (`.github/copilot-instructions.md`) liegen Verweise darauf bei.

Ohne Agent geht es genauso, siehe nächster Abschnitt.

## Von Hand einrichten

**Voraussetzungen:** ACMP mit AESB/SICS (getestet mit ACMP 6.9), Windows Server mit .NET Framework 4.8, Admin-Rechte auf dem ACMP-Server. In der ACMP-Konsole muss die Public API freigeschaltet sein (→ [`docs/ACMP-VORBEREITUNG.md`](docs/ACMP-VORBEREITUNG.md)).

```powershell
# 1. Prüfen (verändert nichts)
.\install.ps1 -CheckOnly

# 2. Installieren (PowerShell als Administrator) - fragt den SICS-Benutzer ab,
#    installiert nach C:\ProgramData\AcmpMcp, Autostart als geplante Aufgabe
.\install.ps1
```

3. **Ersteinrichtung:** Am Ende zeigt das Skript einen Link `http://127.0.0.1:8765/setup?token=…`. Öffnet ihn auf dem Server im Browser und legt den ersten Administrator/Freigeber an. Der Server erzeugt API-Key und Freigabe-Token und zeigt die fertigen Befehle für Claude an. Für andere MCP-Clients siehe [`docs/CLIENTS.md`](docs/CLIENTS.md). Danach den Dienst einmal neu starten.
4. **Eigene ACMP-Identität** (empfohlen): `C:\ProgramData\AcmpMcp\acmp-token-setup.exe`. Dann prüft ACMP die Rechte des jeweiligen Bedieners, statt alles als SICS-Admin auszuführen.
5. **Im LAN freigeben:** `.\set-network-admin.ps1 -BindAddress <LAN-IP> -AllowedSubnets "<Admin-Netz>/24"`
6. **Weitere Kolleg:innen** legt ihr über `http://<HOST>:8765/onboarding` an, ohne JSON zu bearbeiten.

Aktualisieren: `git pull`, dann erneut `.\install.ps1`. Die Konfiguration bleibt erhalten. Entfernen: `C:\ProgramData\AcmpMcp\uninstall-admin.ps1`.

## Client anbinden

Jeder MCP-Client, der **Streamable HTTP** mit eigenem Header kann, verbindet sich direkt:

- **URL:** `http://<HOST>:8765/`
- **Header:** `Authorization: Bearer <KEY>` (persönlicher API-Key von der Setup- bzw. Onboarding-Seite)

Fertige Konfigurationen für Claude Code, Claude Desktop, Codex CLI, VS Code/GitHub Copilot, Cursor und Gemini CLI stehen in [`docs/CLIENTS.md`](docs/CLIENTS.md). Clients, die nur lokale (stdio) Server kennen, binden ihr über die Brücke `mcp-remote` an.

## Funktionsweise

- Der Server verbindet sich über Aagons eigene Client-DLLs (`Aagon.Sis.Sics.*`, sind im ACMP-Setup schon vorhanden) mit SICS/AESB (`wss://127.0.0.1:3950`). Protokoll, Verschlüsselung und Anmeldung sind dadurch dieselben wie in Aagons eigenen Werkzeugen.
- Er sendet ICQL-Kommandos an ACMP. Den Routing-Key ermittelt er selbst, den Virtual Router liest er aus der Config (Standard `VCMN`).
- Die Kommandos stehen als MCP-Tools über JSON-RPC 2.0/HTTP bereit (eigener `TcpListener`, keine URL-ACL nötig).
- **Pro-User:** Jeder Bediener hat einen eigenen API-Key und optional eine eigene ACMP-Identität. ACMP prüft dann dessen echte Rechte (`GetOwnUserRights`).
- **Vier-Augen-Freigabe:** Schreib-, Ausführ- und Löschaufrufe laufen nicht sofort, sondern werden als Antrag eingefroren. Erst wenn 1 Person (Write) bzw. 2 verschiedene Personen (Destructive) auf der Weboberfläche zustimmen, startet der eigentliche ACMP-Aufruf. Die Zustimmung läuft also außerhalb des LLM.

## Tools (Auswahl)

Der Katalog wächst. Die aktuelle Liste liefert `tools/list`, alle 167 nativen ACMP-Kommandos zeigt `acmp_list_commands`.

| Kategorie | Beispiele | Freigabe |
|---|---|---|
| Lesen | `acmp_get_clients`, `acmp_get_jobs`, `acmp_get_tickets`, `acmp_call` (generisch) | keine |
| DB-Reports (optional) | `acmp_outdated_software`, `acmp_missing_updates`, `acmp_vulnerabilities`, `acmp_license_compliance` | keine (read-only) |
| Ausführen | `acmp_enqueue_client_command`, `acmp_run_powershell` (optional) | Vier-Augen |
| Stammdaten | `acmp_create_contact`/`_update_`, `_company`, `_location`, `_department`, `_asset` | Vier-Augen |
| Container/Assets | `acmp_save_containers`, `acmp_delete_containers`, `acmp_lend_asset` | Vier-Augen (Löschen: 2 Personen) |
| Helpdesk | `acmp_create_ticket`, `acmp_update_ticket`, `acmp_close_ticket` | einstellbar |

Jedes Tool akzeptiert optional `format: "json"`. Beispiel-Prompts stehen in [`docs/USAGE.md`](docs/USAGE.md). Neue native Kommandos ergänzt ihr deklarativ in `api-tools.json`, neue SQL-Reports in `db-tools.json`. Beides braucht kein Neu-Kompilieren.

## Weboberfläche

`/dashboard`, `/approvals` (offene Freigaben), `/history`, `/settings` (Freigabe-Schwellwerte pro Tool), `/changelog`, `/onboarding`. `/setup` ist nur aktiv, solange es keinen Freigeber gibt. Die Anmeldung läuft über ein eigenes Token pro Freigeber und ist **unabhängig** vom ACMP- oder MCP-Key: Ein abgeflossener API-Key gibt also keine Freigaberechte.

## Konfiguration

`config.example.json` ist kommentiert (`_hinweis`-Felder). `install.ps1` erzeugt daraus `config.local.json`. Wichtige Schlüssel:

| Schlüssel | Bedeutung |
|---|---|
| `sics.username` / `password` | SICS-/AESB-Basisverbindung |
| `sics.virtualRouter` / `wssPort` | Standard `VCMN` / `3950` |
| `mcp.bindAddress` / `httpPort` | Standard `127.0.0.1` / `8765`. LAN-Zugriff über `set-network-admin.ps1` |
| `approval.*` | Vier-Augen-Freigabe, Schwellwerte, SMTP-Benachrichtigung |
| `runPowershell.clientCommandId` | optional, aktiviert `acmp_run_powershell` / `acmp_read_registry` |
| `db.connectionString` | optional, aktiviert die DB-Report-Tools |
| `helpdesk.createdStateId` | optional, Ticketstatus für neue Tickets |

## Sicherheit

- Der Server gewährt vollen ACMP-Zugriff bis hin zur **Befehlsausführung auf allen Endgeräten**. Behandelt jeden API-Key wie ein Admin-Passwort.
- **Kein TLS eingebaut.** Der Server lauscht standardmäßig nur auf `127.0.0.1` und verweigert LAN-Betrieb ohne API-Key. Im LAN beschränkt ihr per Firewall die Quell-Subnetze. Über Netzgrenzen hinweg gehört ein TLS-Proxy oder VPN davor.
- `install.ps1` beschränkt den Installationsordner auf Administratoren und SYSTEM, denn dort liegen Zugangsdaten.
- Bediener-Passwörter werden nie gespeichert: `acmp-token-setup.exe` speichert nur einen DPAPI-verschlüsselten Token (`LocalMachine`).
- `/regresult` (Rückkanal für `acmp_read_registry`) ist bewusst ohne Key erreichbar, weil der Client den Key nicht kennt. Er nimmt nur Ergebnisse zu offenen, nicht ratbaren Tokens an.
- Sicherheitslücken bitte **nicht** als öffentliches Issue melden, sondern wie in [`SECURITY.md`](SECURITY.md) beschrieben.

## Mitmachen

Issues und Pull Requests sind willkommen. Bitte in Issues **keine** Logs mit Hostnamen, IPs, Benutzernamen, Keys oder Tokens posten, sondern vorher schwärzen.

## Lizenz

[MIT](LICENSE). Kein offizielles Produkt der Aagon GmbH. ACMP und AESB sind Marken der Aagon GmbH.
