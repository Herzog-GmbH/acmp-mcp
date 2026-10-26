# ACMP-MCP – Anwender-Cheat-Sheet

Sie reden in normaler Sprache mit Ihrer KI (Claude, Codex, Copilot, Gemini …); sie wählt die passenden ACMP-Tools. Diese Liste
zeigt typische Formulierungen pro Bereich. **Lesen** ist gefahrlos; **Schreiben/Ausführen/Löschen**
ändert echte Daten bzw. startet Aktionen auf Rechnern und wirkt sofort – die KI zeigt vorher eine Vorschau und wartet auf Ihr OK. Jede Aktion steht im Aktivitätsprotokoll, die Notbremse auf der Weboberfläche stoppt im Zweifel alles.

## Allgemein / Orientierung
- „Welche ACMP-Kommandos gibt es?" → kompletter Katalog (167)
- „Wie sieht der Request für *GetClientList* aus?" → `acmp_describe` (Versionen + Struktur)
- „Zeig mir die ACMP-Server-Infos." → Version, ServerId
- Tipp: „… und gib mir das als JSON." → Antworten kommen als JSON statt XML.

## 1) Container / Gruppen
- „Liste die Clients von Container X." (über Client-Abfrage/Query)
- „Lege einen neuen statischen Container ‚Rollout 2026' an." → **SaveContainers** (SCHREIBT)
- „Lösche den Container ‚Test-Gruppe'." → **DeleteContainers** (LÖSCHT)
- „Aktualisiere die AD-Computer im Container Y." → UpdateActiveDirectoryComputerInContainers
> Hinweis: Container-Lesen hat die API nicht direkt – Container findet man i.d.R. über Queries
> (`GetQueryList` / Abfrage) bzw. die Container-IDs aus der Console.

## 2) Jobs / Client Commands
- „Welche Client Commands / Scripts gibt es?" → **GetClientCommandList** (liefert Id + Name)
- „Führe das Client Command ‚7-Zip' auf dem Client PC-0815 aus." → **EnqueueClientCommand** (FÜHRT AUS)
  - Die KI holt sich die `ClientCommandId` (aus der Command-Liste) und die `ClientId` (aus der Client-Liste) selbst.
- „Welche Jobs hat Client PC-0815, und wie liefen sie?" → `acmp_get_jobs` (aus der DB, der Computername genügt)
- „Welche Daueraufträge sind auffällig?" → `acmp_jobs` mit `onlyNotable: true`
- „Welche Scanner laufen im Agentenplaner, und wann hat PC-0815 zuletzt Daten geliefert?" → `acmp_agent_tasks`, `acmp_scanner_status`
- „Warum ist der Client Command ‚…‘ auf PC-0815 fehlgeschlagen?" → `acmp_job_log` (jeder Schritt mit Ergebnis und Meldungen)
- „Welche Aufgaben des ACMP-Servers sind zuletzt gescheitert?" → `acmp_server_jobs` mit `onlyFailed: 1`

## 3) Inventar / Assets / Reports
- „Wer ist/war auf Client PC-0815 angemeldet?" → **GetOperatingSystem** (Feld **LastLoggedOnUser**) bzw. Tool `acmp_get_os_info` (braucht ClientId). *Nicht* GetMachine – das braucht eine separate MachineId.
- „Liste alle Clients." → **GetClientList** (Computername, Domäne, ClientId)
- „Zeig mir die Assets / das Inventar." → **GetAssetList**
- „Welche CPU/RAM/Festplatten hat Asset Z?" → GetCPU / GetMemory / GetHardDrives …
- „Welche Software ist auf … installiert?" → GetSoftware
- „Schick mir den Bericht ‚…‘ jetzt als PDF." → `acmp_send_report` (nur an Domains aus `reports.allowedMailDomains`); hängt die Mail, zeigt `acmp_mail_queue` den Grund
- „Ist das Oktober-Update überall drauf?" → `acmp_os_builds` (Build mit Revision je Client)
- „Wo läuft der Dienst ‚Spooler‘ noch auf Servern?" → `acmp_services_search`
- „Wo ist SMB1 noch aktiv?" → `acmp_windows_features`
- „Welche Laptops haben einen schwachen Akku?" → `acmp_hardware_health`
- „An welchen Rechnern war Frau Mustermann in den letzten Wochen angemeldet?" → `acmp_user_logons`
- „Setz bei PC-0815 das Feld ‚Scan Status‘ auf 2." → `acmp_cf_set_value` (erst als Vorschau)
- „Lösch die ausgemusterten Rechner ‚…‘ aus ACMP." → `acmp_delete_clients` (erst als Vorschau, nicht rückgängig)

## 4) Helpdesk / Tickets
- „Zeig mir die offenen Tickets." → **GetTicketList**
- „Details zu Ticket #1234." → **GetTicket**
- „Lege ein Ticket an: Titel …, Beschreibung …" → **SaveTicket** (SCHREIBT)
- „Füge Ticket #1234 einen Kommentar hinzu." → SaveTicketComment
- „Buche 30 Min Arbeitszeit auf Ticket #1234." → SaveTicketWorkingTime
- „Schließe Ticket #1234." → CloseTicket

## 5) Client Commands bauen und einsortieren (optional, CC-Writer)
- „Zeig mir die Schritte des Client Commands ‚7-Zip‘." → `acmp_cc_decode`
- „Bau mir einen Client Command, der den Druckerwarteschlangen-Dienst neu startet, als Entwurf im Ordner ‚MCP Entwürfe‘." → `acmp_cc_create_draft` (erst als Vorschau mit allen Schritten)
- „Häng die Datei `\\server\software\setup.msi` an den Entwurf." → `acmp_cc_add_file`
- „Gib den Entwurf frei." → `acmp_cc_release` (erst als Vorschau)
- „Im Command ‚Druckertreiber installieren‘ fehlt der Exit-Code-Check, bau das als neue Version ein." → `acmp_cc_decode`, dann `acmp_cc_new_version` (Vorschau mit den Änderungen), danach `acmp_cc_release` („Gib die neue Version frei.“)
- „Leg unter ‚Software‘ einen Ordner ‚Office‘ an und verschieb alle Office-Commands dorthin." → `acmp_cc_add_folder`, `acmp_cc_move`
- „Gib dem Command ‚Datenträger prüfen‘ einen Parameter ‚Vollscan‘ mit Ja/Nein, Standard Nein." → `acmp_cc_parameters`, dann `acmp_cc_set_parameters` (erst als Vorschau)

## 6) Kiosk (optional, Kiosk-Writer)
- „Wie ist der Kiosk aufgebaut, und wer sieht was?" → `acmp_kiosk_tree` (Filter im Klartext)
- „Stell den Command ‚7-Zip installieren‘ in den Kiosk-Ordner ‚Allgemein‘, sichtbar nur für die AD-Gruppe ‚Buchhaltung‘." → `acmp_kiosk_add_item`
- „Zeig mir passende Icons für 7-Zip." → `acmp_kiosk_images` mit Vorschau, dann `acmp_kiosk_set_image`
- „Benenn den Kiosk-Ordner ‚Tools‘ in ‚Werkzeuge‘ um." → `acmp_kiosk_update_group`
- „Mach die letzte Kiosk-Änderung rückgängig." → `acmp_kiosk_restore`

## 7) Windows-Updates, Unix Scripts, individuelle Felder, Jobs (optional)
- „Welche Updates haben wir abgelehnt, und warum?" → `acmp_wum_declined`
- „Welche offenen Updates scheitern am häufigsten?" → `acmp_wum_declined` mit `scope: open`
- „Lehn den fehlerhaften Treiber ‚…‘ ab, Kommentar: ‚bricht den Druck ab, Ticket 1234‘." → `acmp_wum_decline` (erst als Vorschau), zurück mit `acmp_wum_restore`
- „Welche Unix Scripts gibt es, und welche Version ist freigegeben?" → `acmp_unix_scripts`
- „Leg ein Bash-Skript an, das den freien Platz unter /var meldet, erst in Testring 1." → `acmp_unix_script_create`, Freigabe danach mit `acmp_unix_script_set_ring` (`ring: 255`). Den Job startet ihr in der Console.
- „Was hat das Skript auf dem Server ‚linux-web-01‘ ausgegeben?" → `acmp_unix_results`
- „Leg ein individuelles Feld ‚Scan Status‘ (Ganzzahl 0 bis 9) für Clients an." → `acmp_cf_list`, dann `acmp_cf_create` (erst als Vorschau)
- „Schalte den Job ‚…‘ ab, er läuft stündlich ohne ‚einmalig‘." → `acmp_job_set_enabled` mit `enabled: false` (erst als Vorschau), zurück mit `acmp_job_restore`
- „Der Container ‚…‘ soll nur noch einmal je Client laufen." → `acmp_job_set_condition` mit `condition: once` (erst als Vorschau, gilt für alle Jobs des Containers)
- „Im Windows-Container ‚…‘ sind Linux-Rechner, nur noch Windows zulassen." → `acmp_container_set_restriction` mit `onlyOsFamily: true` (erst als Vorschau)
- „Welche Rechner landen im Container ‚…‘ und warum?" → `acmp_container_filter` (mit `members: true` die Namen)
- „Nimm den Rechner ‚PC-0815‘ aus dem Container ‚…‘." → `acmp_container_set_filter` mit `excludeComputers: ["PC-0815"]` (erst als Vorschau)
- „Im Update-Container ‚…‘ sollen keine Server mehr sein." → `acmp_container_filter` lesen, in `spec` ein Kriterium `{"field":"Betriebssystem Name","op":"notLike","value":"%Server%"}` ergänzen, mit `acmp_container_set_filter` (erst als Vorschau)
- „Häng den Command ‚Inventar nachziehen‘ als Dauerauftrag an den Container ‚…‘, täglich." → `acmp_job_create`, dann `acmp_job_set_condition`
- „Leg ein Wartungsfenster werktags 19 bis 22 Uhr an und gib es dem Container ‚…‘." → `acmp_maintenance_window_create`, `acmp_maintenance_window_assign`
- „Lösch den alten Command ‚…‘ und den leeren Ordner ‚Alt‘." → `acmp_cc_delete`, `acmp_cc_delete_folder` (erst als Vorschau, zurück mit `acmp_job_restore`)

## 8) Managed Software (optional, Job-Writer)
- „Welche abonnierten Pakete haben ein freigegebenes Update, das noch keiner verteilt?" → `acmp_msw_packages`
- „Wie ist 7-Zip in der Flotte verteilt, und welche Version ist im Testring?" → `acmp_msw_package`
- „Was ist an Managed Software auf PC-0815, und was lief zuletzt?" → `acmp_msw_client`
- „Installier 7-Zip testweise auf PC-0815." → `acmp_msw_test_install` (erst als Vorschau)
- „Leg für 7-Zip einen Dauerauftrag ‚nur aktualisieren‘ am Container ‚…‘ an." → `acmp_msw_job_create` (erst als Vorschau)

## 9) Windows-Updates im Testring (optional)
- „Wie steht es um die Updates im Testring?" → `acmp_wum_rings` bzw. Weboberfläche `/dashboard/updates`
- „Welche Rechner brauchen KB5012345 noch, wo ist es gescheitert?" → `acmp_wum_update_clients`
- „Warum bekommt PC-0815 keine Updates angeboten?" → `acmp_wu_diagnosis` (WSUS-Richtlinien, Pausen)
- „Halt das Update ‚…‘ an, es bricht den Druck ab." → `acmp_wum_ring_hold` (erst als Vorschau)
- „Gib das Sicherheitsupdate ‚…‘ sofort frei, die Testrechner haben es." → `acmp_wum_ring_release` mit `mode: now` (erst als Vorschau)

### Fehler-Stopp der Windows-Updates
Windows-Updates gehen in ACMP vom **Testring** (Ring 1/2) nach einer festen Verweildauer automatisch an den **Freigabering** (255). Der Fehler-Stopp schaltet diesen Übergang für ein Update ab, wenn die Tests dagegen sprechen. **Testrechner** sind die Mitglieder von Containern mit einem eingeschalteten WUM-Job, dessen Ring unter 255 liegt und dessen Sammlung das Update enthält.

| Regel | Wann | Folge |
|---|---|---|
| S1 Fehler | Ein gesunder Testrechner meldet das Update seit Eintritt in den Ring als fehlgeschlagen, mit einem festen (nicht vorübergehenden) Fehler, und es ist dort nicht installiert. Ebenso: derselbe Fehler oder ein Fehler ohne Text auf zwei Testrechnern, oder Fehler nur auf Rechnern mit Rechnerdefekt, ohne dass ein Testrechner es geschafft hat | anhalten, ein Mensch entscheidet |
| S2 ungetestet | Weniger als `hoursBeforeDue` Stunden (Standard 24) bis zur Weitergabe, mindestens ein Testrechner braucht das Update, keiner hat es installiert | anhalten; läuft automatisch weiter, sobald ein Testrechner es ohne festen Fehler hat |
| hängt | vorübergehender Fehler auf einem Testrechner seit `stuckDays` Tagen | nur Hinweis auf der Seite |
| Rechnerdefekt | ein Rechner meldet in `defectDays` Tagen mindestens `defectMinUpdates` verschiedene fehlgeschlagene Updates | Meldung, kein Stopp; sein Fehlschlag allein hält kein Update an |
| nach Freigabe | binnen `postReleaseDays` nach der Freigabe scheitert ein Update auf mindestens `postReleaseMinClients` Rechnern mit demselben Fehler | Meldung, Ablehnen erwägen |

**Vorübergehend** und damit kein Grund zum Anhalten sind z. B. Office offen, Herunterfahren während der Installation (`0x8024001E`), Download- und Netzfehler, abgebrochene Läufe (Liste in `wumWatch.transientPatterns` erweiterbar).

Lässt ein Mensch ein angehaltenes Update weiterlaufen (`acmp_wum_ring_release`, Weboberfläche oder Console), nimmt er die bekannten Fehlschläge in Kauf: Derselbe Fehler auf demselben Rechner hält das Update danach nicht wieder an, ein anderer Rechner oder ein anderer Fehler schon.

Der Wächter (`wumWatch`) prüft das im Server alle `intervalMinutes` (Standard 60). Mit `mode: report` meldet er nur, mit `mode: hold` hält er auch an. Jede neue Meldung wird ein offener Punkt, auf Wunsch kommt eine Sammel-Mail. Fällt der Wächter aus, läuft ACMP wie ohne ihn weiter (die Updates gehen nach Plan an den Freigabering). Bei gezogener Notbremse hält er nichts an und meldet nur.

## Wenn etwas nicht klappt
- ACMP liefert sprechende Fehler, z.B. *„ClientCommandId: Value may not be empty"* – die KI erkennt
  daran fehlende Pflichtfelder und korrigiert sich. Sagen Sie ggf. „probier's nochmal, das Feld fehlt".
- Für seltene/komplexe Kommandos nutzt die KI `acmp_describe <Kommando>` und dann `acmp_call`.
- **Alle 167 Kommandos** sind über `acmp_call` erreichbar – auch die, für die es kein eigenes Tool gibt.
