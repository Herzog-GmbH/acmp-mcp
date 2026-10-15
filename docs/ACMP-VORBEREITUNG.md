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

### Client Commands direkt schreiben (CC-Writer)
Die Public API kann Client Commands starten, aber nicht anlegen. Der CC-Writer legt sie deshalb als **Entwurf** direkt in der ACMP-Datenbank an. Eigene Entwürfe gibt er auf Wunsch frei (`acmp_cc_release`), und er sortiert Commands in Ordner (`acmp_cc_move`, `acmp_cc_add_folder`, `acmp_cc_update_folder`). Lesen (`acmp_cc_decode`, `acmp_cc_catalog`, `acmp_cc_folders`) geht schon mit den DB-Report-Tools, dafür braucht es nichts weiter.

> **Achtung:** Direktes Schreiben in die ACMP-Datenbank ist von Aagon nicht vorgesehen und wird von Aagon nicht unterstützt. Sichert vorher die Datenbank und arbeitet zuerst nur in einem Testordner. Der Writer ist standardmäßig aus.

So ist er abgesichert:
- Er legt nur Entwürfe an und ändert nur Entwürfe, die er selbst angelegt hat. Vor jeder Änderung sichert er den alten Stand nach `cc-backups\`.
- Jede Schreibaktion läuft über die Freigabe-Seite. Die Vorschau zeigt Name, Ordner, Variablen und jeden Schritt mit seinen Feldern.
- Freigeben (`acmp_cc_release`) geht nur für eigene Entwürfe mit genau einer Version. Es läuft **immer** über die Freigabe-Seite, auch bei `approval.bypassAll`. Hat sich der Entwurf seit der Anfrage geändert oder steht noch ein Platzhalter wie „HIER … EINTRAGEN“ darin, gibt er nichts frei.
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
9. **Weitere Bediener freischalten:** Der SQL-Login gehört dem Server, nicht den Bedienern. Ob jemand die Schreib-Tools beantragen darf, steht in `ccWriter.allowedRequesters` und wird beim Start gelesen. `add-user.ps1` fragt danach. Für Bediener, die ihr über `/onboarding` anlegt: `.\setup-cc-writer.ps1 -AddRequester <kurzname>` (startet den Dienst neu). Die ACMP-Rechte der Person prüft ACMP bei diesen Tools nicht, weil sie an der Public API vorbeigehen. Wer schreiben darf, entscheidet ihr also hier, zustimmen muss trotzdem ein Freigeber.

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

#### Vorlagen aus der ACMP CTL
`acmp_lib_install` installiert Vorlagen aus der ACMP CTL (Community Template Library) als Entwurf, über den CC-Writer und dieselbe Freigabe. Mit `dryRun: true` zeigt es ohne Writer, was angelegt würde. Quellen stehen in `ctl.sources`. Standard ist erst der Ordner `ctl` im Installationsordner (`templates\<id>\template.json`), dann das Repo `Herzog-GmbH/acmp-ctl`. Das öffentliche Vorlagen-Repo folgt, bis dahin funktionieren eigene Vorlagen im Ordner `ctl`.

### Kiosk pflegen (Kiosk-Writer)
Auch den Kiosk kann die Public API nicht pflegen. Der Kiosk-Writer legt deshalb Einträge, Ordner, Sichtbarkeitsfilter und Icons direkt in der ACMP-Datenbank an. Lesen (`acmp_kiosk_tree`, `acmp_kiosk_images`) geht schon mit den DB-Report-Tools.

> **Achtung:** Wie beim CC-Writer ist das von Aagon nicht vorgesehen. Sichert vorher die Datenbank und fangt mit einem eigenen Test-Ordner im Kiosk an. Der Kiosk-Writer ist standardmäßig aus.

So ist er abgesichert:
- Er schreibt nur in Kiosk-Ordner aus `kioskWriter.allowedGroupIds` und in Ordner, die er selbst angelegt hat. Kiosk-Ordner löschen darf er nicht.
- Jede Änderung läuft über die Freigabe-Seite. Die Vorschau zeigt Name, Ordner, Filter im Klartext und das Icon. Vorher sichert er den alten Stand nach `kiosk-backups\`, `acmp_kiosk_restore` holt ihn zurück.
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

### E-Mail bei neuen Freigabe-Anfragen
Unter `approval.notify` einen SMTP-Relay eintragen. Ohne diesen Eintrag funktioniert alles andere genauso.
