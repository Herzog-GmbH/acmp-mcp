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
- `acmp_read_registry` meldet das Ergebnis per HTTP an `web.publicUrl` + `/regresult` zurück. Diese Adresse muss **von den Clients aus** erreichbar sein (siehe `set-network-admin.ps1`).
- Auf dem ACMP-/MCP-Server selbst startet der Server keine Jobs, auch nicht über `acmp_enqueue_client_command` oder `acmp_call`: Ein Skript als SYSTEM könnte dort `config.local.json` lesen. Geprüft wird jede ClientId im Auftrag über den Computernamen in der Datenbank. Weitere Rechner tragt ihr unter `runPowershell.protectedClients` ein (Computernamen). **Ohne DB-Anbindung** geht das nicht: Dann tragt ihr die ClientId des ACMP-Servers unter `runPowershell.protectedClientIds` ein, sonst lehnt der Server Jobs auf Clients ab.

### DB-Report-Tools (Schwachstellen, Inventar, Lizenzen …)
Für diese Tools braucht der Server Lesezugriff auf die ACMP-SQL-Datenbank:
- `grant-db-readonly.sql` anpassen und als SQL-Admin ausführen. Es gibt dem Maschinenkonto des ACMP-Servers `db_datareader`.
- In `config.local.json` den Abschnitt `db.connectionString` setzen.

### Client Commands direkt schreiben (CC-Writer)
Die Public API kann Client Commands starten, aber nicht anlegen. Der CC-Writer legt sie deshalb als **Entwurf** direkt in der ACMP-Datenbank an. Eigene Entwürfe gibt er auf Wunsch frei (`acmp_cc_release`), und er sortiert Commands in Ordner (`acmp_cc_move`, `acmp_cc_add_folder`, `acmp_cc_update_folder`). Lesen (`acmp_cc_decode`, `acmp_cc_catalog`, `acmp_cc_folders`) geht schon mit den DB-Report-Tools, dafür braucht es nichts weiter.

> **Achtung:** Direktes Schreiben in die ACMP-Datenbank ist von Aagon nicht vorgesehen und wird von Aagon nicht unterstützt. Sichert vorher die Datenbank und arbeitet zuerst nur in einem Testordner. Der Writer ist standardmäßig aus.

So ist er abgesichert:
- Er legt nur Entwürfe an und ändert nur Entwürfe, die er selbst angelegt hat. Vor jeder Änderung sichert er den alten Stand nach `cc-backups\`.
- Jede Schreibaktion wirkt sofort und steht im Aktivitätsprotokoll. Die Vorschau (`dryRun`) zeigt vorher Name, Ordner, Variablen und jeden Schritt mit seinen Feldern.
- Freigeben (`acmp_cc_release`) geht für eigene Entwürfe und für neue Versionen, die zuletzt der MCP gespeichert hat (`acmp_cc_new_version`), nicht in Aagon-Standardordnern. Bei einer Folgeversion löst er wie die Konsole die bisher freigegebene Version ab (State 8). Folgeversionen mit geänderten Dateien gibt er nicht frei. Hat sich der Entwurf zwischen Plan und Schreiben geändert oder steht noch ein Platzhalter wie „HIER … EINTRAGEN“ darin, gibt er nichts frei.
- Vorhandene, auch freigegebene oder fremde Commands ändert er nur als **neue Version** (`acmp_cc_new_version`), wie „Speichern“ in der Konsole: Die freigegebene Version läuft weiter, bis ihr die neue freigebt (`acmp_cc_release` oder Konsole). Nie in Aagon-Standardordnern, optional nur in `ccWriter.versionScopeFolderIds`. Schritt-IDs bleiben, `***` in einem Kennwortfeld übernimmt das gespeicherte Kennwort desselben Schritts. Liegt schon ein Entwurf eines anderen Bearbeiters vor, überschreibt er ihn nur mit `replaceDraft: true` (mit Sicherung). Die Vorschau (`dryRun`) zeigt die Änderungen gegenüber der freigegebenen Version.
- Verschieben (`acmp_cc_move`) darf er auch fremde Commands, aber nur deren Ordner ändern (wie in der Konsole). Mit `ccWriter.moveScopeFolderIds` begrenzt ihr die Ordner-Tools auf bestimmte Teilbäume.
- Er schreibt mit einem eigenen SQL-Login, der nur die nötigen Rechte hat und in `SYS_SCRIPTS` nichts löschen darf.
- Vor dem Schreiben läuft eine **Kompatibilitätsprüfung**: ACMP-Version, Tabellen und Spalten, Trigger, Rechte des Logins, Vorlage, Ordner, File Repository und ein Rundlauf an euren 50 neuesten Commands (auspacken, wieder einpacken, muss bytegleich sein). Fällt eine Prüfung durch, schreibt er nichts. Nach einem ACMP-Update kann das passieren, bis die neue Version getestet ist.

Einrichten:
1. **DB-Report-Tools** einrichten (siehe oben). Der Writer braucht den Lesezugang für Prüfung und Decoder.
2. **Datenbank sichern.**
3. **Testordner** in der Konsole unter Client Commands anlegen, z. B. „MCP Entwürfe“, und ihm **eigene Rechte** geben. Der Writer kopiert die Rechte des Ordners auf jeden Entwurf. Ohne eigene Rechte-Einträge wäre der Entwurf in der Konsole unsichtbar.
4. **Vorlage** in diesem Ordner anlegen: einen leeren Client Command **ohne Bedingungen**, z. B. „MCP Vorlage“. Der Writer übernimmt von ihm Einstellungen und Bedingungen. Die Vorlage nicht löschen, sonst sperrt die Prüfung das Schreiben.
5. **SQL-Login** anlegen: In `grant-cc-writer.sql` das Kennwort ersetzen und das Skript als SQL-Admin ausführen. Das Kennwort gehört in euren Passwort-Manager. Abschnitt 3 ist Pflicht (Entwürfe, Freigeben, Verschieben). Abschnitt 4 (Dateien), 5 (Ordner anlegen) und 6 (Ordner umbenennen/verschieben) sind optional. Ohne sie bleiben nur die jeweiligen Tools gesperrt.
6. **Optional, für angehängte Dateien:** eine oder mehrere UNC-Freigaben mit Installationsdateien, auf die das Computerkonto des ACMP-Servers lesen darf. Dazu der Pfad des File Repository auf dem ACMP-Server, Unterordner `ClientCommands\Projects`. Darin legt ACMP je Command einen Ordner mit dessen GUID an.
7. **Einrichten** als Administrator: `.\setup-cc-writer.ps1` im Installationsordner, oder `install.ps1` → Schritt „Optionale Funktionen“. Das Skript fragt Vorlage, Testordner, Bediener und das Kennwort (verdeckt) ab. Es prüft Anmeldung, Rechte, Vorlage und Ordner, bevor es `config.local.json` ändert.
8. **Prüfen:** Im MCP-Client `acmp_cc_writer_status` aufrufen. Dort muss „bestanden“ stehen. `ordnerTools` und `freigabeTool` zeigen, ob die optionalen Tools frei sind.
9. **Weitere Bediener freischalten:** Der SQL-Login gehört dem Server, nicht den Bedienern. Ob jemand die Schreib-Tools nutzen darf, steht in `ccWriter.allowedRequesters` und wird beim Start gelesen. `add-user.ps1` fragt danach. Für Bediener, die ihr über `/onboarding` anlegt: `.\setup-cc-writer.ps1 -AddRequester <kurzname>` (startet den Dienst neu). Die ACMP-Rechte der Person prüft ACMP bei diesen Tools nicht, weil sie an der Public API vorbeigehen. Wer schreiben darf, entscheidet ihr also hier.

Die GUIDs von Vorlage und Ordner liefert euch die KI, sobald die DB-Report-Tools laufen: `acmp_db_script_rows` mit `table: SYS_SCRIPTS`, `column: DESCRIPTION`, `value: MCP Vorlage`. `SCRIPTID` ist die Vorlage, `FOLDERID` der Ordner.

Ausschalten: `.\setup-cc-writer.ps1 -Disable`. Entwürfe löscht ihr in der Konsole.

Fällt die Prüfung durch, nennt `acmp_cc_writer_status` den Grund:

| Prüfung | Abhilfe |
|---|---|
| ACMP-Version nicht getestet | erst klären, ob sich das Speicherformat geändert hat (Issue im Repo). `ccWriter.allowUntested: true` nur bewusst setzen |
| unbekannter Trigger | Trigger mit `grant-cc-writer.sql` Schritt 0 ansehen. Freigabe wie oben nur bewusst |
| Schema weicht ab | nicht übersteuerbar: Die Tabellen haben sich geändert, der Writer bleibt aus |
| Rechte fehlen | `grant-cc-writer.sql` ausführen (Abschnitt steht in der Meldung) |
| „Ordner anlegen“ / „Ordner ändern“ / „Freigeben“ gesperrt | nur dieses Tool ist aus: Abschnitt 5, 6 bzw. 3 von `grant-cc-writer.sql` nachholen, dann `acmp_cc_writer_status` mit `refresh: true` |
| Antragsteller nicht erlaubt | `.\setup-cc-writer.ps1 -AddRequester <kurzname>` |
| Vorlage hat Bedingungen / fehlt | andere Vorlage ohne Bedingungen eintragen |
| Ordner ohne eigene Rechte | dem Ordner in der Konsole eigene Rechte geben |
| File Repository nicht gefunden | nur die Datei-Tools sind gesperrt. `ccWriter.fileRepositoryRoot` korrigieren |

#### Parameter eines Client Commands
`acmp_cc_parameters` zeigt die Parameter eines Commands (Konsole: Eigenschaften → Client Command spezifisch → Parameter), `acmp_cc_set_parameters` setzt sie wie der Dialog der Konsole: Text, Auswahl oder Kontrollkästchen je Projektvariable, mit Beschriftung DE/EN und Standardwert. Die Liste ersetzt alle bisherigen Parameter, sie wirken sofort für neue Jobs und im Kiosk, ohne neue Version des Commands. Setzt ein Schritt „Projektvariable setzen“ dieselbe Variable, warnt die Vorschau, denn er überschreibt den Parameterwert wieder. Das Tool gehört zum CC-Writer, zeigt mit `dryRun` vorher eine Vorschau und braucht zusätzlich `grant-cf-params.sql` Abschnitt 3 (nur die drei Tabellen `SYS_Jobs_Parameters_*`). Prüfen mit `acmp_cc_parameters` und `status: true`.

#### Vorlagen aus der ACMP CTL
`acmp_lib_install` installiert Vorlagen aus der ACMP CTL (Community Template Library) als Entwurf, über den CC-Writer. Mit `dryRun: true` zeigt es ohne Writer, was angelegt würde. Quellen stehen in `ctl.sources`. Standard ist erst der Ordner `ctl` im Installationsordner (`templates\<id>\template.json`), dann das Repo [`Herzog-GmbH/acmp-ctl`](https://github.com/Herzog-GmbH/acmp-ctl). Alle Vorlagen samt Schritten und Skripten könnt ihr vorher auf https://herzog-gmbh.github.io/acmp-ctl/ lesen. Eigene Vorlagen legt ihr im Ordner `ctl` ab.

### Kiosk pflegen (Kiosk-Writer)
Auch den Kiosk kann die Public API nicht pflegen. Der Kiosk-Writer legt deshalb Einträge, Ordner, Sichtbarkeitsfilter und Icons direkt in der ACMP-Datenbank an. Lesen (`acmp_kiosk_tree`, `acmp_kiosk_images`) geht schon mit den DB-Report-Tools.

> **Achtung:** Wie beim CC-Writer ist das von Aagon nicht vorgesehen. Sichert vorher die Datenbank und fangt mit einem eigenen Test-Ordner im Kiosk an. Der Kiosk-Writer ist standardmäßig aus.

So ist er abgesichert:
- Er schreibt nur in Kiosk-Ordner aus `kioskWriter.allowedGroupIds` und in Ordner, die er selbst angelegt hat. Kiosk-Ordner löschen darf er nicht.
- Jede Änderung wirkt sofort und steht im Aktivitätsprotokoll. Die Vorschau (`dryRun`) zeigt vorher Name, Ordner, Filter im Klartext und das Icon. Vorher sichert er den alten Stand nach `kiosk-backups\`, `acmp_kiosk_restore` holt ihn zurück.
- Eigene Icons entfernt er nur, wenn er sie selbst hochgeladen hat und sie nirgends benutzt werden.
- Vor dem Schreiben prüft er ACMP-Version, Tabellen, Trigger und Rechte und baut eure vorhandenen Sichtbarkeitsfilter zur Probe nach. Weicht einer ab, schreibt er nichts. Filterformen, die er nicht kennt, meldet er als Hinweis und legt sie selbst nicht an.

Einrichten:
1. **CC-Writer** einrichten (siehe oben). Der Kiosk-Writer nutzt denselben SQL-Login und dieselbe Bediener-Liste (`ccWriter.allowedRequesters`). Wer also schon Client Commands schreiben darf, darf auch den Kiosk pflegen. Eine eigene Liste geht über `kioskWriter.allowedRequesters`.
2. **Datenbank sichern.**
3. **Rechte:** `grant-kiosk-writer.sql` als SQL-Admin ausführen. Abschnitt 3 ist Pflicht, Abschnitt 4 (Icons hochladen und entfernen) optional.
4. **Test-Ordner** im Kiosk in der Konsole anlegen.
5. **Optional, für Icons:** den Ordner `Images` im File Repository des ACMP-Servers (darin liegen die Bilder als `<2 Zeichen>\{GUID}`) und eine oder mehrere UNC-Freigaben mit Bilddateien, auf die das Computerkonto des ACMP-Servers lesen darf.
6. **Einrichten** als Administrator: `.\setup-kiosk-writer.ps1` im Installationsordner, oder `install.ps1` → Schritt „Optionale Funktionen“. Das Skript zeigt die vorhandenen Kiosk-Ordner zur Auswahl und prüft die Rechte, bevor es `config.local.json` ändert.
7. **Prüfen:** Im MCP-Client `acmp_kiosk_tree` mit `status: true` aufrufen. Die Vorprüfung muss bestanden sein.

Sichtbarkeitsfilter nach AD-Gruppe, OU oder Benutzer löst der Server im Active Directory auf. Er läuft als SYSTEM und fragt das AD mit dem Computerkonto des ACMP-Servers ab, dafür braucht es nichts weiter.

Ausschalten: `.\setup-kiosk-writer.ps1 -Disable`.

### Weitere Schreib-Tools (Windows-Updates, Unix Scripts, individuelle Felder, Jobs)
Vier weitere Funktionen schreiben direkt in die ACMP-Datenbank, weil die Public API sie nicht kennt. Lesen geht bei allen schon mit den DB-Report-Tools: `acmp_wum_declined`, `acmp_wum_rings`, `acmp_unix_scripts`, `acmp_unix_results`, `acmp_cf_list`, `acmp_jobs`, `acmp_get_jobs`, `acmp_job_log`, `acmp_container_filter`, `acmp_msw_packages`, `acmp_msw_package`, `acmp_msw_client`, `acmp_maintenance_windows`, `acmp_security_profiles`.

| Funktion | Tools | Rechte | Config |
|---|---|---|---|
| Windows-Updates ablehnen | `acmp_wum_decline`, `acmp_wum_restore` | `grant-wum-writer.sql` | `wumWriter` |
| Fehler-Stopp im Testring | `acmp_wum_ring_hold`, `acmp_wum_ring_release`, Update-Wächter | zusätzlich `grant-wum-rings.sql` | `wumWriter`, `wumWatch` |
| Unix Scripts schreiben (ab ACMP 6.10) | `acmp_unix_script_create`, `_new_version`, `_set_ring` | `grant-unix-writer.sql` | `unixWriter` |
| Individuelle Felder anlegen | `acmp_cf_create` | `grant-cf-params.sql` | `cfWriter` |
| Jobs schalten, löschen, Startbedingung, Client-Beschränkung | `acmp_job_set_enabled`, `_set_condition`, `_delete`, `_restore`, `acmp_container_set_restriction` | `grant-jobs-writer.sql`, für die Client-Beschränkung zusätzlich `grant-jobs-writer2.sql` | `jobWriter` |
| Container-Filter | `acmp_container_set_filter` | zusätzlich `grant-container-filter.sql` | `jobWriter` |
| Managed Software, Client Command als Dauerauftrag | `acmp_msw_subscribe`, `acmp_msw_job_create`, `acmp_msw_test_install`, `acmp_job_create`, `acmp_job_delete` für einmalige Jobs | zusätzlich `grant-msw-writer.sql` | `jobWriter` |
| Löschen, Wartungsfenster, Sicherheitsprofile, Agentenplaner | `acmp_cc_delete`, `acmp_cc_delete_folder`, `acmp_kiosk_delete_group`, `acmp_unix_script_delete`, `acmp_maintenance_window_create`, `_assign`, `_delete`, `acmp_container_security_profile`, `acmp_agent_template_assign` | zusätzlich `grant-luecken-writer.sql` | `jobWriter` |

Die zusätzlichen Rechte sind optional: Fehlen sie, bleiben nur die Tools dieser Zeile gesperrt, die Vorschau mit `dryRun` geht trotzdem.

> **Achtung:** Wie beim CC-Writer ist das von Aagon nicht vorgesehen. Sichert vorher die Datenbank. Alle vier sind standardmäßig aus.

Für alle vier gilt:
- Sie nutzen den SQL-Login und die Bediener-Liste (`ccWriter.allowedRequesters`) des CC-Writers, solange im eigenen Abschnitt nichts anderes steht.
- Jede Schreibaktion zeigt mit `dryRun` vorher eine Vorschau, wirkt dann sofort und steht im Aktivitätsprotokoll. Der Plan hält den Stand fest; hat sich bis zum Schreiben etwas geändert, schreibt der Server nichts.
- Vor dem Schreiben prüft der Server ACMP-Version, Tabellen, Trigger und Rechte. Getestet ist ACMP 6.10. Bei Abweichungen sperrt die Vorprüfung, `allowUntested: true` nur bewusst setzen.
- Vorher wird gesichert bzw. protokolliert (`wum-backups\`, `unix-backups\`, `cf-backups\`, `jobs-backups\`).

**Windows-Updates ablehnen.** Der Server lehnt Updates über die ACMP-eigene Prozedur `ap_Wum_DeclineUpdates` ab, wie „Ablehnen“ in der Konsole, und baut danach die Flags neu auf. Abgelehnte Updates bekommen die Clients nicht mehr angeboten, bereits installierte bleiben. Höchstens 10 Updates je Auftrag, nur per Update-Id, mit Pflicht-Kommentar. Abgelöste, abgelaufene und widerrufene Updates lehnt er nicht ab. `acmp_wum_restore` hebt eine Ablehnung wieder auf. Die nächtlichen Ablehnungen des ACMP-Bereinigungsjobs („Autodeclined by Cleanupjob …“) blendet `acmp_wum_declined` standardmäßig aus (`includeAuto: true` zeigt sie).

**Fehler-Stopp im Testring.** `acmp_wum_ring_hold` schaltet für ein Update den automatischen Übergang in den Freigabering aus, wie „Automatischen Übergangsmodus deaktivieren“ in der Konsole (`SYS_TestRings.TransitionMode = 0`). `acmp_wum_ring_release` schaltet ihn wieder ein oder gibt das Update sofort frei (Ring 255, danach Flags neu). Dafür braucht der Login nur zusätzlich `UPDATE` auf die Spalte `TransitionMode` (`grant-wum-rings.sql`). Der **Update-Wächter** (`wumWatch`, standardmäßig aus) wendet die Regeln aus [`USAGE.md`](USAGE.md#fehler-stopp-der-windows-updates) im Server regelmäßig an: Startet mit `mode: report` (nur melden, auf `/dashboard/updates` und als offene Punkte), erst nach ein paar Tagen Beobachtung `mode: hold`. Für die Sammel-Mail tragt ihr unter `wumWatch.mail` einen SMTP-Server ein, der ohne Anmeldung annimmt (z. B. euer interner Relay), Absender und Empfänger. Testrechner sind die Mitglieder von Containern mit einem WUM-Job, dessen Verteilungsring unter 255 liegt.

**Unix Scripts.** Der Server legt Skripte und Versionen nur neu an, wie „Unix Script hinzufügen“ bzw. „Version hinzufügen“ in der Konsole. Neue Versionen landen in Testring 1, freigegeben wird mit `acmp_unix_script_set_ring` und `ring: 255`. Vorhandene Versionen ändert oder löscht er nicht. Mit `unixWriter.allowedFolderIds` begrenzt ihr das Schreiben auf bestimmte Unix-Script-Ordner. Die Skripte laufen auf den Clients **als root**.
- Einen Unix-Job **starten** kann der MCP nicht, ACMP hat dafür weder API noch Job Collection. Das macht ihr in der Konsole: Container oder Abfrage → „Unix-Jobs ausführen“. Danach liest `acmp_unix_results` die Ausgabe je Client.
- Die Ausgabe landet nur in der Datenbank, wenn das Skript sie speichert („Log speichern“). Neue Skripte legt der MCP standardmäßig mit „immer“ an.
- Ohne freie Unix-Script-Lizenz liefert ACMP das Skript nicht aus. Der Job steht trotzdem auf „Erfolg“, `acmp_unix_results` weist dann auf die Lizenz hin.

**Individuelle Felder.** `acmp_cf_create` legt Felder für Clients an wie die Konsole: optional eine neue Tabelle, je Feld Ganzzahl oder Text, höchstens 10 Felder je Auftrag. Andere Eingabemethoden, Mehrfachtabellen und Löschen bleiben Sache der Konsole. Der Schreib-Login bekommt dafür **keine** DDL-Rechte: Tabelle und Spalte legen die zwei Prozeduren `mcp_cf_create_table` und `mcp_cf_add_column` aus `grant-cf-params.sql` an (`EXECUTE AS OWNER`), die nur genau das Muster des ACMP-Servers bauen. Sie vergeben auf neue Tabellen die Rechte an die ACMP-Datenbankbenutzer `RLS_ACMPUser`, `AcmpUser_ReadOnly` und `AcmpUser_LowPrivilege`. Fehlt einer davon, passt das Muster nicht zu eurer Installation, und ihr richtet das Tool besser nicht ein. Ob der ACMP-Server ein neu angelegtes Feld ohne Neustart kennt, zeigt der erste Lauf eines Commands mit „Wert auf Server speichern“ (`acmp_cf_list` mit `values: true`).

**Jobs.** `acmp_jobs` listet die Daueraufträge (Jobs an Containern) mit Ziel-Container, Startbedingung im Klartext, Läufen und Fehlern der letzten 7 und 30 Tage und Hinweisen: abgeschaltet, Client Command gelöscht, Container leer, stündlich oder bei jedem Start ohne „einmalig“, dieselbe Aufgabe an mehreren Containern, Linux-Clients im Windows-Container. Die Job-Aktion bleibt ausgeblendet, weil sie Kennwörter enthalten kann. Der Job-Writer arbeitet wie die Konsole:
- `acmp_job_set_enabled` schaltet Daueraufträge ab und wieder ein. Einschalten stellt die alte Startbedingung exakt wieder her.
- `acmp_job_delete` löscht die Verknüpfung zur Job-Vorlage, den Job selbst löscht der ACMP-Trigger `TG_Delete_SYS_Templates_Jobs`. Job-Historie und Job-Vorlage bleiben.
- `acmp_job_set_condition` setzt die Startbedingung eines Containers: einmalig, beim Agentenstart, täglich bzw. alle n Tage, stündlich bzw. alle n Stunden oder im Wartungsfenster, optional ohne Rücksicht auf Wartungsfenster. Die Konsole führt die Startbedingung je Container, deshalb gilt sie für alle seine Daueraufträge, und abgeschaltete Jobs werden damit wieder aktiv. Wochentage, Uhrzeiten und Netzwerk-Bedingungen bleiben Sache der Konsole. **Vorsicht:** `condition: once` (sofort einmalig) startet sofort auf allen Mitgliedern, die online sind. An der Prüfsumme der Startbedingung erkennen die Agents die Änderung: Kennt der MCP den Wert der Konsole für genau diese Bedingung, übernimmt er ihn, sonst setzt er einen eigenen. Vorschau und Ergebnis nennen die Quelle.
- `acmp_container_set_restriction` setzt nur die Client-Beschränkung eines Containers (nur Clients seiner Betriebssystemfamilie oder alle). Die Vorschau nennt die Mitglieder, die dann herausfallen; wirksam wird das bei der nächsten Container-Neuberechnung des ACMP-Servers.
- `acmp_job_restore` nimmt jede dieser Änderungen aus der Sicherung zurück, solange seitdem nichts geändert wurde.

Höchstens 10 Jobs je Auftrag. `acmp_job_set_enabled` schaltet auch die Scanner einer Agentenplaner-Vorlage (`acmp_agent_tasks`), `acmp_job_delete` löscht auch einmalige Jobs wie „Löschen“ im Job-Monitor (mit `grant-msw-writer.sql`).

**Container-Filter.** `acmp_container_set_filter` schreibt wie die Konsole beim Speichern im Reiter „Dyn. Filter“ das SQL in `CLT_CONTAINER_STRUCTURE.SQLFilter` und die Definition in `SYS_Queries_FilterContainer`. Das SQL baut der Server so, wie es die Konsole baut (an echten Filtern zeichengleich geprüft). Er ändert nur Filter, die er genauso nachbauen kann: Kriterien der obersten Gruppe (UND, ODER, AUSSER) und höchstens eine Untergruppe; Text-, Ja/Nein-, Zahl- und IP-Kriterien. Vorher führt er den neuen Filter lesend aus und zeigt, welche Rechner dazukommen oder wegfallen. Die Mitglieder rechnet der ACMP-Server bei der nächsten Container-Neuberechnung neu (bzw. je Rechner beim Agentenkontakt); die Client-Beschränkung des Containers gilt dabei weiter.

**Managed Software und neue Daueraufträge.** `acmp_msw_job_create` und `acmp_job_create` legen einen Dauerauftrag an einem Container an wie die Konsole beim Speichern: Job-Vorlage des Containers, Job, Job-Monitor-Eintrag und Verknüpfungen in einer Transaktion. `acmp_job_create` nimmt nur Client Commands ohne Parameter, weil nicht belegt ist, wie die Konsole Parameterwerte im Job ablegt. `acmp_msw_test_install` gibt ein Paket einmalig an 1–5 Clients wie „Managed Software zuweisen“, `acmp_msw_subscribe` abonniert über die ACMP-eigene Prozedur `ap_ManagedSoftware_Subscribe` (die Lizenzprüfung der Konsole für Evaluationspakete läuft dabei nicht). Den sofortigen Anstoß an die Agents kann der MCP nicht auslösen: Ein Agent holt einen neuen Auftrag bei seiner nächsten Job-Abfrage (Agentenstart, regelmäßige Abfrage).

**Löschen, Wartungsfenster, Sicherheitsprofile, Agentenplaner.** Diese Tools bilden je einen SQL-Mitschnitt der Konsole 6.10 nach. Vor dem Schreiben sichern sie alle betroffenen Zeilen (auch die, die ACMP-Trigger und Kaskaden ändern), nach dem Schreiben noch einmal; `acmp_job_restore` legt den alten Stand zurück, solange niemand die Zeilen inzwischen geändert hat. Abgelehnt werden: Client Commands mit Daueraufträgen (erst `acmp_job_delete`), in Aagon-Standardordnern oder gerade in der Konsole geöffnet, die Vorlagen des MCP selbst (`runPowershell.clientCommandId`, `ccWriter.templateScriptId`), nicht leere Ordner, Wartungsprofile, die noch einem Container zugeordnet sind. Ein BitLocker- oder Defender-Profil an einem Container setzen die Agents auf **allen** Mitgliedern um, Ziel und Profil also vorher genau prüfen.

In der Konsole bleiben: die Betriebssystemfamilie eines Containers, Agentenplaner-Aufgaben selbst anlegen, Berichte, Jobs mit mehreren Vorlagen löschen, Unix-Jobs starten.

Einrichten:
1. **CC-Writer** einrichten (siehe oben).
2. **Datenbank sichern.**
3. **Rechte:** je Funktion das SQL als SQL-Admin ausführen: `grant-wum-writer.sql`, `grant-unix-writer.sql`, `grant-cf-params.sql` bzw. `grant-jobs-writer.sql`, dazu nach Bedarf die zusätzlichen aus der Tabelle oben (`grant-jobs-writer2.sql`, `grant-wum-rings.sql`, `grant-container-filter.sql`, `grant-msw-writer.sql`, `grant-luecken-writer.sql` – in dieser Reihenfolge). Abschnitt 1 zeigt jeweils vorher Trigger und Benutzer, die Kontrolle am Ende die wirksamen Rechte. Zeigt Abschnitt 1 andere Trigger als erwartet, nicht weitermachen.
4. **Einrichten** als Administrator: `.\setup-extra-writers.ps1` im Installationsordner, oder `install.ps1` → Schritt „Optionale Funktionen“. Das Skript fragt je Funktion nach, prüft die Rechte des Schreib-Logins und schaltet nur ein, was vollständig ist. Mit `-Features wum,unix,cf,jobs` ohne Rückfrage, Unix-Ordner mit `-UnixFolderIds`.
5. **Prüfen:** im MCP-Client `acmp_wum_declined`, `acmp_unix_scripts`, `acmp_cf_list` bzw. `acmp_jobs` jeweils mit `status: true`. Die Vorprüfung muss bestanden sein.

Ausschalten: `.\setup-extra-writers.ps1 -Disable` (alle) oder mit `-Features` nur einzelne.

### Lagebild, offene Punkte, Berichte per Mail
- **Lagebild** (`/dashboard`, `/dashboard/rollout`): braucht nur `db.connectionString`. Die Schwellen stehen optional im Abschnitt `dashboard` (siehe `config.example.json`), ohne ihn gelten die Standardwerte. Abgefragt wird nur, wenn jemand die Seite öffnet, mit Zwischenspeicher (`cacheMinutes`).
- **Offene Punkte** (`acmp_open_items`, `acmp_open_item_set`, Formular im Dashboard): braucht keine Einrichtung. Die Liste liegt als `offene-punkte.json` im Installationsordner.
- **Berichte per Mail** (`acmp_send_report`): Der ACMP-Server erzeugt einen Bericht der Konsole und verschickt ihn über seine eigenen Mail-Einstellungen. Erlaubt sind nur Empfänger aus `reports.allowedMailDomains`; ohne Eintrag bleibt das Tool aus. Hängt eine Mail, zeigt `acmp_mail_queue` den Grund.

### Aktivitätsprotokoll und Notbremse
Braucht keine Einrichtung. Das Protokoll liegt als `activity-JJJJ-MM.jsonl` im Installationsordner, die Notbremse als `notbremse.json`; beides seht ihr auf der Weboberfläche unter `/activity` und `/settings`. Die E-Mail-Benachrichtigung aus Version 1 (`approval.notify`) gibt es nicht mehr.
