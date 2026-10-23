# Änderungen der Claude-Desktop-Erweiterung

Gilt nur für die Erweiterung (`acmp-mcp-<version>.mcpb`), also die Verbindung von Claude Desktop zum ACMP-MCP-Server. Änderungen am Server selbst zeigt dessen Seite `/changelog`.

## 1.1.0 – 2026-10-22

- Beschreibung für Server-Version 2: keine Vier-Augen-Freigabe mehr, stattdessen Aktivitätsprotokoll und Notbremse. `acmp_approval_status` steht nicht mehr in der Tool-Liste. Die Brücke selbst ist unverändert.

## 1.0.0 – 2026-10-18

- Erste Fassung: `acmp-mcp-1.0.0.mcpb` aus dem GitHub-Release per Doppelklick in Claude Desktop installieren, Server-Adresse und API-Key in den Einstellungen der Erweiterung eintragen.
- Brücke `server/index.js` ohne Abhängigkeiten: leitet jede Nachricht per HTTP POST mit dem API-Key an den Server weiter, versteht JSON- und SSE-Antworten.
- Klare Meldungen im Chat, wenn Adresse oder Key fehlen, der Key abgelehnt wird (401) oder der Server nicht erreichbar ist.
- Optional: Stammzertifikat (PEM) für HTTPS über einen eigenen TLS-Proxy mit interner Zertifizierungsstelle.
