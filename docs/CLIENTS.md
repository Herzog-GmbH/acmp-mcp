# MCP-Clients anbinden

Der Server spricht MCP über **Streamable HTTP** (JSON-RPC 2.0 per `POST`). Jeder Client braucht zwei Angaben:

| | |
|---|---|
| URL | `http://<HOST>:8765/` |
| Header | `Authorization: Bearer <KEY>` |

`<HOST>` ist euer ACMP-Server. `<KEY>` ist der **persönliche** API-Key von der Setup- bzw. Onboarding-Seite: Jede Person bekommt einen eigenen Key, damit die Freigabe weiß, wer was angefordert hat. Den Key behandelt ihr wie ein Admin-Passwort. Schreibt ihn nicht in Dateien, die in Git landen.

> Konfigurationsformate ändern sich bei den Clients gelegentlich. Funktioniert eine Vorlage nicht, in der Doku des Clients unter „MCP“ / „remote server“ / „HTTP“ nachsehen. Die zwei Angaben oben bleiben immer gleich.

## Claude Code
```
claude mcp add --transport http acmp http://<HOST>:8765/ --header "Authorization: Bearer <KEY>" --scope user
```

## Claude Desktop

### Als Erweiterung (empfohlen)

1. `acmp-mcp-<version>.mcpb` aus den [Releases](https://github.com/Herzog-GmbH/acmp-mcp/releases) laden.
2. Doppelklick auf die Datei, oder in Claude Desktop unter **Einstellungen → Erweiterungen** hineinziehen, dann „Installieren“.
3. In den Einstellungen der Erweiterung **Server-Adresse** (`http://<HOST>:8765/`) und **API-Key** eintragen. Den Key legt Claude Desktop im Schlüsselbund des Betriebssystems ab.

Die Erweiterung ist nur eine kleine Brücke ohne Abhängigkeiten und nutzt das Node.js, das Claude Desktop mitbringt. Der Server selbst läuft weiter auf dem ACMP-Server. Fehlen Adresse oder Key, ist der Key falsch oder der Server nicht erreichbar, steht das als Meldung im Chat. Wer HTTPS über einen eigenen TLS-Proxy mit interner Zertifizierungsstelle nutzt, hinterlegt dort zusätzlich das Stammzertifikat (PEM).

Selbst bauen: `powershell -NoProfile -ExecutionPolicy Bypass -File mcpb\build-mcpb.ps1` (Windows PowerShell 5.1, legt `dist\acmp-mcp-<version>.mcpb` an). Testen ohne Claude Desktop: `mcpb\test-mcpb.py`, Aufruf steht im Kopf der Datei. Änderungen: [`mcpb/CHANGELOG.md`](../mcpb/CHANGELOG.md).

### Ohne Erweiterung

`claude_desktop_config.json`, über die Brücke `mcp-remote` (braucht Node.js). `--allow-http` ist nötig, weil der Server kein TLS spricht:
```json
{
  "mcpServers": {
    "acmp": {
      "command": "npx",
      "args": ["-y", "mcp-remote", "http://<HOST>:8765/", "--header", "Authorization: Bearer <KEY>", "--allow-http"]
    }
  }
}
```

## OpenAI Codex CLI
`~/.codex/config.toml`. Der Key kommt aus einer Umgebungsvariable und nicht aus der Datei:
```toml
[mcp_servers.acmp]
url = "http://<HOST>:8765/"
bearer_token_env_var = "ACMP_MCP_KEY"
```
Dann `ACMP_MCP_KEY` als Benutzer-Umgebungsvariable setzen. Ältere Codex-Versionen ohne HTTP-Unterstützung binden den Server wie Claude Desktop über `mcp-remote` an:
```toml
[mcp_servers.acmp]
command = "npx"
args = ["-y", "mcp-remote", "http://<HOST>:8765/", "--header", "Authorization: Bearer <KEY>", "--allow-http"]
```

## VS Code / GitHub Copilot (Agent-Modus)
`.vscode/mcp.json` im Arbeitsordner oder in der Benutzerkonfiguration. VS Code fragt den Key beim ersten Start ab und speichert ihn sicher:
```json
{
  "inputs": [
    { "type": "promptString", "id": "acmp-key", "description": "ACMP-MCP API-Key", "password": true }
  ],
  "servers": {
    "acmp": {
      "type": "http",
      "url": "http://<HOST>:8765/",
      "headers": { "Authorization": "Bearer ${input:acmp-key}" }
    }
  }
}
```

## Cursor
`~/.cursor/mcp.json`:
```json
{
  "mcpServers": {
    "acmp": {
      "url": "http://<HOST>:8765/",
      "headers": { "Authorization": "Bearer <KEY>" }
    }
  }
}
```

## Gemini CLI
`~/.gemini/settings.json`:
```json
{
  "mcpServers": {
    "acmp": {
      "httpUrl": "http://<HOST>:8765/",
      "headers": { "Authorization": "Bearer <KEY>" }
    }
  }
}
```

## Andere Clients
- **Mit HTTP-Unterstützung** (Streamable HTTP + eigene Header): URL und Header von oben eintragen.
- **Nur lokale stdio-Server:** Ihr bindet über `mcp-remote` an, mit demselben Aufruf wie bei Claude Desktop (`npx -y mcp-remote <URL> --header "Authorization: Bearer <KEY>" --allow-http`).
- **Nur HTTPS:** Manche Clients, darunter Web-Oberflächen in der Cloud, verbinden sich nur mit HTTPS-Adressen aus dem Internet. Dafür bräuchte der Server einen TLS-Proxy und eine Freigabe nach außen. Davon raten wir ab, denn der Server kann Befehle auf allen Endgeräten auslösen. Nutzt besser einen lokal laufenden Client.

## Test
Nach dem Verbinden das Tool `acmp_whoami` aufrufen lassen. Es zeigt, als wer ihr angemeldet seid und ob eine eigene ACMP-Identität verknüpft ist.
