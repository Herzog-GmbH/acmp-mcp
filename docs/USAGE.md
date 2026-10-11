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

## Wenn etwas nicht klappt
- ACMP liefert sprechende Fehler, z.B. *„ClientCommandId: Value may not be empty"* – die KI erkennt
  daran fehlende Pflichtfelder und korrigiert sich. Sagen Sie ggf. „probier's nochmal, das Feld fehlt".
- Für seltene/komplexe Kommandos nutzt die KI `acmp_describe <Kommando>` und dann `acmp_call`.
- **Alle 167 Kommandos** sind über `acmp_call` erreichbar – auch die, für die es kein eigenes Tool gibt.
