# Anleitung für KI-Agenten (Claude Code, Codex, …)

Ein Mensch hat dich gebeten, diesen ACMP-MCP-Server auf seinem ACMP-Server einzurichten. Diese Datei ist dein Ablauf. Arbeite die Schritte der Reihe nach ab und melde nach jedem Schritt kurz den Stand.

## Grundregeln

- **Geheimnisse fasst du nicht an.** Den SICS-Benutzer fragt `install.ps1` selbst ab. API-Keys und Freigabe-Tokens erzeugt der Server auf seiner Weboberfläche. Bitte den Menschen nie, dir Passwörter oder Keys in den Chat zu schreiben, und gib keine aus `config.local.json` oder `secrets.local.json` wieder.
- **Admin-Schritte führt der Mensch aus.** Du läufst normalerweise ohne erhöhte Rechte. Erkläre deshalb den Befehl und lass ihn in einer PowerShell „Als Administrator ausführen“ laufen. Danach prüfst du das Ergebnis.
- **Konsolen-Einstellungen (ACMP/AESB) kannst du nicht setzen.** Erkläre sie anhand von `docs/ACMP-VORBEREITUNG.md`.
- **Nichts ins Netz öffnen, bevor ein Bediener existiert.** Der Server lauscht erst nur auf `127.0.0.1`. LAN-Zugriff gibt es erst nach Schritt 5.
- Der Server kann Befehle **auf allen verwalteten Endgeräten** ausführen. Schalte die Vier-Augen-Freigabe nicht ab (`approval.enabled`, `approval.bypassAll`), auch nicht „zum Testen“.

## Schritt 0 – Wo bist du?
Der Server muss **auf dem ACMP-Server** laufen, denn er nutzt die dort installierten Aagon-DLLs.
- Prüfe, ob `C:\Program Files (x86)\Aagon\AESB SICS\SICS\Aagon.Sis.Sics.Clients.dll` existiert.
- Fehlt die Datei, bist du auf dem falschen Rechner. Sag das dem Menschen und hör auf. Eine Installation auf einem Arbeitsplatz-PC funktioniert nicht.

## Schritt 1 – Voraussetzungen prüfen (ohne Admin, verändert nichts)
```powershell
.\install.ps1 -CheckOnly
```
Jede `[FEHLT]`-Zeile erklärt, was fehlt. Typische Ursachen:
- Port 3950 zu: AESB-Dienste laufen nicht → `Get-Service | ? Name -match 'SICS|AESB'`
- Port 8765 belegt: anderes Programm → `-HttpPort` wählen

Danach mit dem Menschen `docs/ACMP-VORBEREITUNG.md` Abschnitt „Pflicht“ durchgehen: Public API freischalten und SICS-Benutzer bereitstellen.

## Schritt 2 – Installieren (Mensch, als Administrator)
```powershell
.\install.ps1
```
Das Skript fragt den SICS-Benutzernamen und das Passwort ab, baut, legt `C:\ProgramData\AcmpMcp\config.local.json` an, richtet die geplante Aufgabe `ACMP-MCP-Server` (SYSTEM, Autostart) ein und zeigt am Ende einen **Setup-Link**.

Prüfen (darfst du):
```powershell
Get-ScheduledTask ACMP-MCP-Server | Select State
Get-Content C:\ProgramData\AcmpMcp\server.log -Tail 30
```
`server.log` liegt in einem Ordner nur für Administratoren. Kannst du ihn nicht lesen, bitte den Menschen, die letzten Zeilen einzufügen. Vorher soll er die Zeile mit `setup?token=` entfernen.

## Schritt 3 – Ersteinrichtung (Mensch, im Browser auf dem Server)
Den Setup-Link aus Schritt 2 (`http://127.0.0.1:8765/setup?token=…`) öffnen und den ersten Administrator/Freigeber anlegen. Die Seite zeigt danach die fertigen Befehle für Claude Code bzw. Desktop an, inklusive persönlichem API-Key. Der Mensch kopiert sie selbst.

Danach Dienst neu starten (Mensch, Admin):
```powershell
Stop-ScheduledTask ACMP-MCP-Server; Start-ScheduledTask ACMP-MCP-Server
```

## Schritt 4 – Eigene ACMP-Identität (empfohlen, Mensch)
Damit ACMP die Rechte der Person prüft, die das Tool aufruft, statt alles als SICS-Admin auszuführen:
```powershell
cd C:\ProgramData\AcmpMcp
.\acmp-token-setup.exe
```
Das Programm fragt den ACMP-Benutzer und das Passwort ab. Gespeichert wird nur ein DPAPI-verschlüsselter Token. Danach Dienst neu starten.

## Schritt 5 – Im LAN freigeben (optional, Mensch, Admin)
```powershell
cd C:\ProgramData\AcmpMcp
.\set-network-admin.ps1 -BindAddress <LAN-IP des ACMP-Servers> -AllowedSubnets "<Admin-Subnetz>/24"
```
Empfiehl, die erlaubten Quell-Subnetze auf die Admin-Arbeitsplätze zu beschränken. Weise darauf hin, dass der Server nur **HTTP** spricht. Über Netzgrenzen hinweg gehört ein TLS-Proxy oder VPN davor.

## Schritt 6 – Client verbinden und testen
Den Befehl von der Setup- bzw. Onboarding-Seite führt der Mensch auf **seinem** PC aus, zum Beispiel:
```
claude mcp add --transport http acmp http://<HOST>:8765/ --header "Authorization: Bearer <KEY>" --scope user
```
Danach testest du mit `acmp_whoami` und `acmp_server_info`, beide sind reine Lesetools. Ein schreibendes Tool muss eine **Anfrage-Id** liefern und keine rohe ACMP-Antwort. Kommt eine rohe Antwort, ist die Freigabe aus. Melde das.

Weitere Kolleg:innen legt ein Freigeber unter `http://<HOST>:8765/onboarding` an.

## Optionale Funktionen
Siehe `docs/ACMP-VORBEREITUNG.md` → „Optional“: `acmp_run_powershell` (Client-Command-Vorlage mit `%SCRIPT%`), DB-Report-Tools (`grant-db-readonly.sql`), E-Mail-Benachrichtigung. Jede Option wird in `config.local.json` eingetragen. Das macht der Mensch als Admin, danach Dienst neu starten.

## Aktualisieren
```powershell
git pull
.\install.ps1          # als Administrator; config.local.json bleibt erhalten
```

## Fehlersuche
| Symptom (`server.log`) | Ursache / Abhilfe |
|---|---|
| `Wss connection failed to open` in Schleife | SICS nimmt keine Verbindungen an **oder** die Anmeldung wurde abgelehnt. Erst SICS-Benutzer/Passwort prüfen, dann `Get-Service \| ? Name -match 'SICS\|AESB' \| Restart-Service -Force` (der Start dauert über eine Minute) |
| `Pro-User-Verbindung FEHLGESCHLAGEN … Fallback Operator` | Token des Bedieners ungültig → `acmp-token-setup.exe` erneut ausführen, Dienst neu starten |
| `ABBRUCH: bindAddress … kein API-Key` | LAN-Bindung ohne Bediener → erst `/setup` abschließen |
| `ACMP-Target/Routing-Key in VR '…' nicht gefunden` | Virtual Router weicht ab → `sics.virtualRouter` setzen |
| Client-Job „Erfolg“, aber keine Wirkung | Siehe Hinweise zu `%SCRIPT%` in `docs/ACMP-VORBEREITUNG.md` |
