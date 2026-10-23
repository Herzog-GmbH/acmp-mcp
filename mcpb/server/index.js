#!/usr/bin/env node
// Bruecke fuer Claude Desktop (MCPB): stdio <-> ACMP-MCP-Server (Streamable HTTP).
//
// Der eigentliche Server laeuft auf dem ACMP-Server (install.ps1). Diese Datei nimmt die
// JSON-RPC-Nachrichten von Claude Desktop zeilenweise auf stdin entgegen, schickt jede per
// HTTP POST mit dem persoenlichen API-Key an den Server und schreibt die Antwort auf stdout.
// Keine Abhaengigkeiten, nur Node-Bordmittel.
//
// Einstellungen kommen als Umgebungsvariablen (manifest.json -> user_config):
//   ACMP_MCP_URL      Adresse des Servers, z. B. http://acmp.example.local:8765/
//   ACMP_MCP_KEY      persoenlicher API-Key
//   ACMP_MCP_CA_FILE  optional: Stammzertifikat (PEM) fuer HTTPS ueber einen eigenen TLS-Proxy
"use strict";

const fs = require("fs");
const http = require("http");
const https = require("https");
const readline = require("readline");

const VERSION = "1.1.0";
const TIMEOUT_MS = 300000;

// Ein leeres oder nie gesetztes Feld kommt je nach Client als "" oder als unersetzter
// Platzhalter "${user_config....}" an; beides gilt als nicht gesetzt.
function wert(name) {
  let v = process.env[name];
  if (v == null) return null;
  v = String(v).trim();
  if (!v || v.startsWith("${") || v === "undefined" || v === "null") return null;
  return v;
}

function log(text) {
  process.stderr.write("[acmp-mcp] " + text + "\n");
}

// ---------- Einstellungen pruefen ----------
let ziel = null;
let konfigFehler = null;
const key = wert("ACMP_MCP_KEY");
let ca = null;

(function pruefen() {
  const roh = wert("ACMP_MCP_URL");
  if (!roh) { konfigFehler = "Keine Server-Adresse eingetragen. In Claude Desktop unter Einstellungen > Erweiterungen > ACMP-MCP-Server die Adresse eintragen, z. B. http://acmp.example.local:8765/"; return; }
  try { ziel = new URL(/^https?:\/\//i.test(roh) ? roh : "http://" + roh); }
  catch (e) { konfigFehler = "Server-Adresse ungueltig: " + roh; return; }
  if (ziel.protocol !== "http:" && ziel.protocol !== "https:") { konfigFehler = "Server-Adresse muss mit http:// oder https:// beginnen: " + roh; ziel = null; return; }
  if (!key) { konfigFehler = "Kein API-Key eingetragen. Den persoenlichen Key gibt es am Ende von install.ps1, ueber add-user.ps1 oder die Seite /onboarding des Servers."; return; }
  const caDatei = wert("ACMP_MCP_CA_FILE");
  if (caDatei) {
    try { ca = fs.readFileSync(caDatei); }
    catch (e) { konfigFehler = "Stammzertifikat aus den Einstellungen nicht lesbar: " + caDatei + " (" + e.message + ")"; }
  }
})();

if (konfigFehler) log("Einstellungen unvollstaendig: " + konfigFehler);
else log("Bruecke " + VERSION + " bereit, Server " + ziel.origin + ziel.pathname);

// ---------- HTTP ----------
let sitzung = null;          // Mcp-Session-Id, falls der Server eine vergibt
let protokoll = null;        // ausgehandelte Protokollversion nach initialize

function senden(text) {
  return new Promise((resolve, reject) => {
    const body = Buffer.from(text, "utf8");
    const headers = {
      "Content-Type": "application/json",
      "Accept": "application/json, text/event-stream",
      "Content-Length": body.length,
      "Authorization": "Bearer " + key,
      "User-Agent": "acmp-mcp-mcpb/" + VERSION,
    };
    if (sitzung) headers["Mcp-Session-Id"] = sitzung;
    if (protokoll) headers["MCP-Protocol-Version"] = protokoll;
    const mod = ziel.protocol === "https:" ? https : http;
    const req = mod.request({
      hostname: ziel.hostname,
      port: ziel.port || (ziel.protocol === "https:" ? 443 : 80),
      path: (ziel.pathname || "/") + ziel.search,
      method: "POST",
      headers,
      ca: ca || undefined,
      timeout: TIMEOUT_MS,
    }, (res) => {
      const teile = [];
      res.on("data", (c) => teile.push(c));
      res.on("end", () => resolve({ status: res.statusCode, headers: res.headers, text: Buffer.concat(teile).toString("utf8") }));
      res.on("error", reject);
    });
    req.on("timeout", () => req.destroy(new Error("keine Antwort nach " + (TIMEOUT_MS / 1000) + " s")));
    req.on("error", reject);
    req.end(body);
  });
}

// Gegenprobe ohne Body. Der Server lehnt einen falschen Key ab, bevor er den Body gelesen hat, und
// schliesst die Verbindung; dann kommt beim POST oft nur ECONNRESET an statt der 401. Ein GET auf
// denselben Pfad hat keinen Body: falscher Key -> 401, richtiger Key -> 405 (MCP nur per POST).
function keyPruefen() {
  return new Promise((resolve) => {
    const mod = ziel.protocol === "https:" ? https : http;
    const req = mod.request({
      hostname: ziel.hostname,
      port: ziel.port || (ziel.protocol === "https:" ? 443 : 80),
      path: (ziel.pathname || "/") + ziel.search,
      method: "GET",
      headers: { "Authorization": "Bearer " + key, "User-Agent": "acmp-mcp-mcpb/" + VERSION },
      ca: ca || undefined,
      timeout: 15000,
    }, (res) => { res.resume(); resolve(res.statusCode); });
    req.on("timeout", () => req.destroy(new Error("timeout")));
    req.on("error", () => resolve(null));
    req.end();
  });
}

// Antwort als SSE (text/event-stream): jede data-Zeile ist eine JSON-RPC-Nachricht.
function sseNachrichten(text) {
  const raus = [];
  for (const block of text.split(/\r?\n\r?\n/)) {
    const daten = block.split(/\r?\n/).filter((z) => z.startsWith("data:")).map((z) => z.slice(5).replace(/^ /, "")).join("\n");
    if (daten.trim()) raus.push(daten);
  }
  return raus;
}

// ---------- stdout ----------
function schreiben(obj) {
  process.stdout.write(JSON.stringify(obj) + "\n");
}

function fehler(ids, code, text) {
  for (const id of ids) schreiben({ jsonrpc: "2.0", id, error: { code, message: text } });
}

// ---------- eine Nachricht von Claude Desktop ----------
async function weiterleiten(zeile) {
  let msg;
  try { msg = JSON.parse(zeile); }
  catch (e) { log("unlesbare Zeile von Claude Desktop verworfen"); return; }

  const liste = Array.isArray(msg) ? msg : [msg];
  const ids = liste.filter((m) => m && m.id !== undefined && m.id !== null && m.method).map((m) => m.id);

  if (konfigFehler) { fehler(ids, -32002, "ACMP-MCP-Erweiterung: " + konfigFehler); return; }

  let antwort;
  try { antwort = await senden(zeile); }
  catch (e) {
    let erneut = null;
    if (e.code === "ECONNRESET" || e.code === "EPIPE") {
      const status = await keyPruefen();
      if (status === 401) {
        const text = "API-Key vom ACMP-MCP-Server abgelehnt (401). Key in den Einstellungen der Erweiterung pruefen.";
        log(text);
        fehler(ids, -32001, text);
        return;
      }
      // Key stimmt: Verbindung nur kurz gestoert, einmal neu senden.
      if (status === 405) {
        try { erneut = await senden(zeile); } catch (e2) { e = e2; }
      }
    }
    if (!erneut) {
      const text = "ACMP-MCP-Server unter " + ziel.origin + " nicht erreichbar: " + e.message + ". Laeuft der Dienst, und ist dieser PC in der Firewall-Regel des Servers freigegeben?";
      log(text);
      fehler(ids, -32003, text);
      return;
    }
    antwort = erneut;
  }

  const sid = antwort.headers["mcp-session-id"];
  if (sid) sitzung = String(sid);

  if (antwort.status === 202 || antwort.status === 204) return;
  if (antwort.status === 401) {
    const text = "API-Key vom ACMP-MCP-Server abgelehnt (401). Key in den Einstellungen der Erweiterung pruefen.";
    log(text);
    fehler(ids, -32001, text);
    return;
  }
  if (antwort.status < 200 || antwort.status >= 300) {
    const text = "ACMP-MCP-Server antwortet mit HTTP " + antwort.status + ": " + antwort.text.slice(0, 300);
    log(text);
    fehler(ids, -32004, text);
    return;
  }

  const typ = String(antwort.headers["content-type"] || "");
  const nachrichten = typ.includes("text/event-stream") ? sseNachrichten(antwort.text) : [antwort.text];
  for (const n of nachrichten) {
    if (!n.trim()) continue;
    let obj;
    try { obj = JSON.parse(n); }
    catch (e) { log("unlesbare Antwort vom Server: " + n.slice(0, 200)); fehler(ids, -32005, "Unlesbare Antwort vom ACMP-MCP-Server"); return; }
    for (const m of Array.isArray(obj) ? obj : [obj]) {
      if (m && m.result && m.result.protocolVersion && liste.some((x) => x && x.method === "initialize" && x.id === m.id)) {
        protokoll = String(m.result.protocolVersion);
      }
    }
    schreiben(obj);
  }
}

// ---------- Hauptschleife ----------
const offen = new Set();
const rl = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
rl.on("line", (zeile) => {
  if (!zeile.trim()) return;
  const p = weiterleiten(zeile).catch((e) => log("Fehler: " + (e && e.stack || e)));
  offen.add(p);
  p.finally(() => offen.delete(p));
});
rl.on("close", async () => {
  await Promise.allSettled([...offen]);
  process.exit(0);
});
