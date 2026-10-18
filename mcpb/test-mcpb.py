"""Startet die gebaute Erweiterung so, wie Claude Desktop sie startet, und ruft Werkzeuge auf.

    python -I mcpb\\test-mcpb.py dist\\acmp-mcp-<version>.mcpb [aufrufe.json | --liste] -- server_url=<url> [ca_file=<pfad>]

Den API-Key liest das Skript aus der Umgebungsvariable ACMP_MCP_TEST_KEY, damit er weder in der
Befehlszeile noch im Verlauf der Shell steht. Ohne Key wird api_key nicht gesetzt (Test der Meldung).

Statt der .mcpb geht auch ein entpackter Ordner, z. B. die von Claude Desktop installierte Kopie unter
%APPDATA%\\Claude\\Claude Extensions\\<id>. Das Skript setzt die Einstellungen (user_config) wie der
MCPB-Standard ein (${__dirname}, ${user_config.x}; nicht gesetzte Felder bleiben als Platzhalter stehen)
und startet command/args/env aus manifest.json.

Ohne aufrufe.json: initialize, tools/list und acmp_whoami. Mit --liste: nur initialize und tools/list.
aufrufe.json: Liste von [name, argumente]. Nur lesende Werkzeuge verwenden.
"""
import json, os, re, shutil, subprocess, sys, tempfile, zipfile

sys.stdout.reconfigure(encoding="utf-8")
LIMIT = int(os.environ.get("MCP_TEST_LIMIT", "600"))
argv = sys.argv[1:]
konfig_args = argv[argv.index("--") + 1:] if "--" in argv else []
argv = argv[:argv.index("--")] if "--" in argv else argv
if not argv:
    sys.exit(__doc__)
paket = argv[0]
if len(argv) > 1 and argv[1] == "--liste":
    calls = []
elif len(argv) > 1:
    calls = json.load(open(argv[1], encoding="utf-8"))
else:
    calls = [["acmp_whoami", {}]]

if os.path.isdir(paket):
    ordner, aufraeumen = os.path.abspath(paket), False
else:
    ordner, aufraeumen = tempfile.mkdtemp(prefix="acmp-mcpb-"), True
    with zipfile.ZipFile(paket) as z:
        z.extractall(ordner)
manifest = json.load(open(os.path.join(ordner, "manifest.json"), encoding="utf-8"))

schema = manifest.get("user_config", {})
werte = {k: v["default"] for k, v in schema.items() if "default" in v}
for a in konfig_args:
    k, _, v = a.partition("=")
    if k not in schema:
        sys.exit("unbekannte Einstellung: " + k + " (bekannt: " + ", ".join(schema) + ")")
    if k == "api_key":
        sys.exit("api_key bitte ueber ACMP_MCP_TEST_KEY uebergeben, nicht in der Befehlszeile")
    werte[k] = (v.lower() == "true") if schema[k]["type"] == "boolean" else v
if os.environ.get("ACMP_MCP_TEST_KEY"):
    werte["api_key"] = os.environ["ACMP_MCP_TEST_KEY"]

var = {"__dirname": ordner, "pathSeparator": os.sep, "/": os.sep, "HOME": os.path.expanduser("~")}
for k, v in werte.items():
    var["user_config." + k] = ("true" if v else "false") if isinstance(v, bool) else str(v)

def ersetze(s):
    return re.sub(r"\$\{([^}]+)\}", lambda m: var.get(m.group(1), m.group(0)), s)

cfg = dict(manifest["server"]["mcp_config"])
cfg.update(cfg.get("platform_overrides", {}).get(sys.platform, {}))
befehl = [ersetze(cfg["command"])] + [ersetze(a) for a in cfg.get("args", [])]
umgebung = dict(os.environ)
umgebung.pop("ACMP_MCP_TEST_KEY", None)
umgebung.update({k: ersetze(v) for k, v in cfg.get("env", {}).items()})
print("Paket:", manifest["name"], manifest["version"], "entpackt nach", ordner)
print("Start:", " ".join(befehl))
print("Einstellungen:", ", ".join(k + ("=***" if schema.get(k, {}).get("sensitive") else "=" + str(v)) for k, v in werte.items()) or "keine")

p = subprocess.Popen(befehl, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                     text=True, encoding="utf-8", env=umgebung, cwd=ordner)

def rpc(i, method, params=None):
    msg = {"jsonrpc": "2.0", "id": i, "method": method}
    if params is not None:
        msg["params"] = params
    p.stdin.write(json.dumps(msg) + "\n")
    p.stdin.flush()
    line = p.stdout.readline()
    if not line:
        raise RuntimeError("keine Antwort, Prozess beendet")
    return json.loads(line)

def notify(method):
    p.stdin.write(json.dumps({"jsonrpc": "2.0", "method": method}) + "\n")
    p.stdin.flush()

fehler = 0
try:
    r = rpc(1, "initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                              "clientInfo": {"name": "test-mcpb", "version": "1"}})
    if "error" in r:
        print("initialize FEHLER:", r["error"]["message"])
        fehler += 1
    else:
        info = r["result"]["serverInfo"]
        print("initialize ok:", info.get("name"), info.get("version"), "Protokoll", r["result"].get("protocolVersion"))
        notify("notifications/initialized")
        r = rpc(2, "tools/list")
        if "error" in r:
            print("tools/list FEHLER:", r["error"]["message"])
            fehler += 1
        else:
            namen = [t["name"] for t in r["result"]["tools"]]
            print("tools/list ok:", len(namen), "Werkzeuge")
            fehlen = [t["name"] for t in manifest.get("tools", []) if t["name"] not in namen]
            if fehlen:
                print("  im Manifest, aber nicht auf dem Server:", ", ".join(fehlen))
        for n, (name, args) in enumerate(calls, start=3):
            r = rpc(n, "tools/call", {"name": name, "arguments": args})
            if "error" in r:
                print(name, "FEHLER:", r["error"]["message"])
                fehler += 1
                continue
            text = "".join(c.get("text", "") for c in r["result"].get("content", []) if c.get("type") == "text")
            print(name, "ok" if not r["result"].get("isError") else "isError", "->", text[:LIMIT].replace("\n", " "))
finally:
    p.stdin.close()
    try:
        p.wait(timeout=20)
    except subprocess.TimeoutExpired:
        p.kill()
    err = p.stderr.read().strip()
    if err:
        print("stderr:", err[:LIMIT])
    if aufraeumen:
        shutil.rmtree(ordner, ignore_errors=True)
sys.exit(1 if fehler else 0)
