# ACMP-MCP – Anwender-Cheat-Sheet

Sie reden in normaler Sprache mit Ihrer KI (Claude, Codex, Copilot, Gemini …); sie wählt die passenden ACMP-Tools. Diese Liste
zeigt typische Formulierungen pro Bereich. **Lesen** ist gefahrlos; **Schreiben/Ausführen/Löschen**
ändert echte Daten bzw. startet Aktionen auf Rechnern – die KI sollte vorher kurz nachfragen, und die Vier-Augen-Freigabe greift ohnehin.

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
- „Welche Jobs laufen gerade auf Client PC-0815?" → **GetJobs** (braucht die ClientId)

## 3) Inventar / Assets / Reports
- „Wer ist/war auf Client PC-0815 angemeldet?" → **GetOperatingSystem** (Feld **LastLoggedOnUser**) bzw. Tool `acmp_get_os_info` (braucht ClientId). *Nicht* GetMachine – das braucht eine separate MachineId.
- „Liste alle Clients." → **GetClientList** (Computername, Domäne, ClientId)
- „Zeig mir die Assets / das Inventar." → **GetAssetList**
- „Welche CPU/RAM/Festplatten hat Asset Z?" → GetCPU / GetMemory / GetHardDrives …
- „Welche Software ist auf … installiert?" → GetSoftware
- „Stoße den Report … an." → SendReports

## 4) Helpdesk / Tickets
- „Zeig mir die offenen Tickets." → **GetTicketList**
- „Details zu Ticket #1234." → **GetTicket**
- „Lege ein Ticket an: Titel …, Beschreibung …" → **SaveTicket** (SCHREIBT)
- „Füge Ticket #1234 einen Kommentar hinzu." → SaveTicketComment
- „Buche 30 Min Arbeitszeit auf Ticket #1234." → SaveTicketWorkingTime
- „Schließe Ticket #1234." → CloseTicket

## 5) Client Commands bauen und einsortieren (optional, CC-Writer)
- „Zeig mir die Schritte des Client Commands ‚7-Zip‘." → `acmp_cc_decode`
- „Bau mir einen Client Command, der den Druckerwarteschlangen-Dienst neu startet, als Entwurf im Ordner ‚MCP Entwürfe‘." → `acmp_cc_create_draft` (Freigabe-Anfrage mit allen Schritten)
- „Häng die Datei `\\server\software\setup.msi` an den Entwurf." → `acmp_cc_add_file`
- „Gib den Entwurf frei." → `acmp_cc_release` (immer mit Freigabe-Anfrage)
- „Im Command ‚Druckertreiber installieren‘ fehlt der Exit-Code-Check, bau das als neue Version ein." → `acmp_cc_decode`, dann `acmp_cc_new_version` (Vorschau mit den Änderungen), danach `acmp_cc_release` („Gib die neue Version frei.“)
- „Leg unter ‚Software‘ einen Ordner ‚Office‘ an und verschieb alle Office-Commands dorthin." → `acmp_cc_add_folder`, `acmp_cc_move`
- „Gib dem Command ‚Datenträger prüfen‘ einen Parameter ‚Vollscan‘ mit Ja/Nein, Standard Nein." → `acmp_cc_parameters`, dann `acmp_cc_set_parameters` (immer mit Freigabe-Anfrage)

## 6) Kiosk (optional, Kiosk-Writer)
- „Wie ist der Kiosk aufgebaut, und wer sieht was?" → `acmp_kiosk_tree` (Filter im Klartext)
- „Stell den Command ‚7-Zip installieren‘ in den Kiosk-Ordner ‚Allgemein‘, sichtbar nur für die AD-Gruppe ‚Buchhaltung‘." → `acmp_kiosk_add_item`
- „Zeig mir passende Icons für 7-Zip." → `acmp_kiosk_images` mit Vorschau, dann `acmp_kiosk_set_image`
- „Benenn den Kiosk-Ordner ‚Tools‘ in ‚Werkzeuge‘ um." → `acmp_kiosk_update_group`
- „Mach die letzte Kiosk-Änderung rückgängig." → `acmp_kiosk_restore`

## 7) Windows-Updates, Unix Scripts, individuelle Felder (optional)
- „Welche Updates haben wir abgelehnt, und warum?" → `acmp_wum_declined`
- „Welche offenen Updates scheitern am häufigsten?" → `acmp_wum_declined` mit `scope: open`
- „Lehn den fehlerhaften Treiber ‚…‘ ab, Kommentar: ‚bricht den Druck ab, Ticket 1234‘." → `acmp_wum_decline` (immer mit Freigabe-Anfrage), zurück mit `acmp_wum_restore`
- „Welche Unix Scripts gibt es, und welche Version ist freigegeben?" → `acmp_unix_scripts`
- „Leg ein Bash-Skript an, das den freien Platz unter /var meldet, erst in Testring 1." → `acmp_unix_script_create`, Freigabe danach mit `acmp_unix_script_set_ring` (`ring: 255`). Den Job startet ihr in der Console.
- „Was hat das Skript auf dem Server ‚linux-web-01‘ ausgegeben?" → `acmp_unix_results`
- „Leg ein individuelles Feld ‚Scan Status‘ (Ganzzahl 0 bis 9) für Clients an." → `acmp_cf_list`, dann `acmp_cf_create` (immer mit Freigabe-Anfrage)

## Wenn etwas nicht klappt
- ACMP liefert sprechende Fehler, z.B. *„ClientCommandId: Value may not be empty"* – die KI erkennt
  daran fehlende Pflichtfelder und korrigiert sich. Sagen Sie ggf. „probier's nochmal, das Feld fehlt".
- Für seltene/komplexe Kommandos nutzt die KI `acmp_describe <Kommando>` und dann `acmp_call`.
- **Alle 167 Kommandos** sind über `acmp_call` erreichbar – auch die, für die es kein eigenes Tool gibt.
