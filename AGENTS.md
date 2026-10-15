# Anleitung für KI-Agenten (Claude Code, Codex, GitHub Copilot, Gemini CLI, Cursor, …)

Ein Mensch hat dich gebeten, diesen ACMP-MCP-Server auf seinem ACMP-Server einzurichten. Diese Datei ist dein Ablauf. Arbeite die Schritte der Reihe nach ab und melde nach jedem Schritt kurz den Stand.

## Grundregeln

- **Geheimnisse fasst du nicht an.** Den SICS-Benutzer fragt `install.ps1` selbst ab. API-Keys und Freigabe-Tokens erzeugen der Assistent bzw. `add-user.ps1` und zeigen sie nur dem Menschen im Terminal. Bitte den Menschen nie, dir Passwörter oder Keys in den Chat zu schreiben, und gib keine aus `config.local.json` oder `secrets.local.json` wieder.
- **Admin-Schritte führt der Mensch aus.** Du läufst normalerweise ohne erhöhte Rechte. Erkläre deshalb den Befehl und lass ihn in einer PowerShell „Als Administrator ausführen“ laufen. Danach prüfst du das Ergebnis.
- **Konsolen-Einstellungen (ACMP/AESB) kannst du nicht setzen.** Erkläre sie anhand von `docs/ACMP-VORBEREITUNG.md`.
- **Nichts ins Netz öffnen, bevor ein Bediener existiert.** Der Server lauscht erst nur auf `127.0.0.1`. Der Assistent gibt ihn erst nach dem Anlegen des Administrators frei.
- Der Server kann Befehle **auf allen verwalteten Endgeräten** ausführen. Schalte die Vier-Augen-Freigabe nicht ab (`approval.enabled`, `approval.bypassAll`), auch nicht „zum Testen“.
- Den **CC-Writer** (Client Commands direkt in die ACMP-Datenbank schreiben) und den **Kiosk-Writer** richtest du nur ein, wenn der Mensch das ausdrücklich will. Weise ihn darauf hin, dass Aagon das nicht vorsieht und die Datenbank vorher gesichert werden muss. `approvalGate` und `allowUntested` (in `ccWriter` und `kioskWriter`) änderst du nicht.

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

Danach mit dem Menschen `docs/ACMP-VORBEREITUNG.md` Abschnitt „Pflicht“ durchgehen: Public API freischalten und SICS-Benutzer bereitstellen. Der Assistent fragt das auch selbst ab.

## Schritt 2 – Assistent starten (Mensch, als Administrator)
```powershell
.\install.ps1
```
Der Assistent ist interaktiv, deshalb führt ihn der **Mensch** in einer eigenen Admin-PowerShell aus, nicht du. Er erledigt alles weitere selbst:
- Er fragt den SICS-Benutzer ab (Passwort verdeckt) und prüft die Anmeldung an ACMP.
- Er legt den ersten Administrator/Freigeber an.
- Er verknüpft optional die eigene ACMP-Identität (`acmp-token-setup.exe`).
- Er richtet optionale Funktionen ein.
- Er gibt den Server im LAN frei.
- Am Ende zeigt er API-Key, Freigabe-Links und die Client-Konfiguration.

**Wichtig:** Diese Ausgabe enthält Geheimnisse, nämlich den API-Key und den Freigabe-Login. Bitte den Menschen **nicht**, sie dir zu zeigen. Frag nur nach dem Ergebnis: „Steht am Ende ‚Fertig‘? Gab es eine `[FEHLT]`-Zeile?“

Während der Assistent läuft, erklärst du bei Bedarf die einzelnen Schritte, etwa die Client-Command-Vorlage aus `docs/ACMP-VORBEREITUNG.md`.

Prüfen (darfst du, falls du Admin-Rechte hast; sonst den Menschen fragen):
```powershell
Get-ScheduledTask ACMP-MCP-Server | Select State
```

## Schritt 3 – Client verbinden und testen
Den Client richtet der Mensch auf **seinem** PC ein. Die Konfiguration hat ihm der Assistent am Ende angezeigt, Vorlagen für alle gängigen Clients stehen in `docs/CLIENTS.md`. Danach testet ihr mit `acmp_whoami` und `acmp_server_info`, beides reine Lesetools. Ein schreibendes Tool muss eine **Anfrage-Id** liefern, keine rohe ACMP-Antwort. Kommt eine rohe Antwort, ist die Freigabe aus. Melde das.

## Schritt 4 – Weitere Kolleg:innen
Der Mensch führt auf dem Server als Admin aus:
```powershell
cd C:\ProgramData\AcmpMcp
.\add-user.ps1
```
Alternativ geht es im Browser über `/onboarding`, mit Login als Freigeber.

## Ohne Rückfragen (nur wenn ausdrücklich gewünscht)
`.\install.ps1 -NonInteractive -SicsUser <Benutzer> -AdminName <kurzname> -AdminEmail <mail>` installiert ohne Rückfragen, braucht dafür aber Admin-Rechte. Das SICS-Passwort liest das Skript aus `$env:ACMP_SICS_PASSWORD`. **Diese Variable setzt der Mensch selbst** in seiner Sitzung, nicht du, und es läuft nie über den Chat. Die Schlussausgabe mit dem API-Key gibst du nicht wieder. Im Zweifel nimm den interaktiven Assistenten.

## Optionale Funktionen
Siehe `docs/ACMP-VORBEREITUNG.md` → „Optional“: `acmp_run_powershell` (Client-Command-Vorlage mit `%SCRIPT%`), DB-Report-Tools (`grant-db-readonly.sql`), CC-Writer (`grant-cc-writer.sql` + `setup-cc-writer.ps1`, standardmäßig aus), Kiosk-Writer (`grant-kiosk-writer.sql` + `setup-kiosk-writer.ps1`, standardmäßig aus), E-Mail-Benachrichtigung. Jede Option wird in `config.local.json` eingetragen. Das macht der Mensch als Admin, danach Dienst neu starten.

Nach dem Einrichten des CC-Writers rufst du `acmp_cc_writer_status` auf. Steht dort nicht „bestanden“, erklärst du die gesperrte Prüfung anhand der Tabelle in `docs/ACMP-VORBEREITUNG.md`. Das SQL-Kennwort fragt `setup-cc-writer.ps1` selbst verdeckt ab, es läuft nie über den Chat.

Der **Kiosk-Writer** setzt den CC-Writer voraus (gleicher SQL-Login): `grant-kiosk-writer.sql` (SQL-Admin), dann `setup-kiosk-writer.ps1` (Admin). Prüfen mit `acmp_kiosk_tree` und `status: true`.

**Bediener und Schreibrechte:** Der SQL-Login gehört dem Server. Wer die Schreib-Tools (CC-Writer, Kiosk-Writer, `acmp_lib_install`) beantragen darf, steht in `ccWriter.allowedRequesters`. Kommt ein Bediener über `/onboarding` dazu, sag dem Menschen, dass er ihn mit `.\setup-cc-writer.ps1 -AddRequester <kurzname>` freischalten muss, wenn die Person schreiben soll. `add-user.ps1` fragt das selbst ab.

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
