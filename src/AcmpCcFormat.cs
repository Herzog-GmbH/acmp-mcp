// AcmpCcFormat: Lesen und Schreiben des ACMP-Client-Command-Formats (SYS_SCRIPTS.CLIENTSCRIPT / CONSOLESCRIPT).
// Aufbau (entschluesselt 13.10.2026, ACMP 6.9.3):
//   raw Deflate( Kopf | Projektvariablen | 6 EncStrings (1, 2, 6 meist leer; 3-5 JSON) | Art | VirtualTreeView-Stream | Aenderungsprotokoll )
//   Block 1 ist nicht immer leer: Aagons „ACMP Unix Agent verteilen & Inventory" {1F5A4238-…} (Console-Skript) hat dort
//   Deflate("PYTHON\r\nSTDOUT\r\n") - eine Namensliste, vermutlich die beobachteten Variablen des Debuggers (gefunden 22.10.2026).
//   Strings = int32-Laenge + UTF-8. EncString = String "[Encoding:UTF8]" + int32-Laenge + Daten.
//   Baumknoten: NodeChunk(1){ BaseChunk(2){19 Byte + Kind-NodeChunks} UserChunk(4){Klasse, EncString(Deflate-XML)} }
//   Altformat 12 (Aagon-Vorlagen, z.B. „Send Message"): nach „Art" kein int32 0, VirtualTreeView-Stream-Version 2
//   mit 17-Byte-BaseChunk (States als Word statt int32). Wird nur gelesen; geschrieben wird immer Format 13 / VT 3.
// Bewusst ohne Newtonsoft, damit die Datei auch per Add-Type in PowerShell testbar ist. C# 5 (csc v4).
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

class CcVar
{
    public string Name;
    public string Value = "";
    public byte Origin;
    public bool ReadOnly;
}

class CcNode
{
    public string Class;
    public string Xml;                 // vollstaendiges Schritt-XML inkl. Deklaration
    public byte[] XmlDeflated;         // optional: fertig komprimiert (Byte-genaue Kopie); sonst wird Xml komprimiert
    public int States = -1;            // -1 = Standard (0x81 Blatt, 0xE1 aufgeklappter Elternknoten)
    // Anzeige-Felder des VirtualTreeView-Knotens (nur UI der Console, z.B. Zeilenhoehe je nach Bildschirm-DPI).
    // Beim Lesen uebernommen, damit ein Rundlauf bytegleich bleibt; neue Knoten: Standardwerte.
    public int NodeHeight = 22;
    public byte Align = 0x32, CheckState = 0, CheckType = 0;
    public int Reserved = 0;
    public List<CcNode> Children = new List<CcNode>();
}

class CcScript
{
    public int TotalSteps;             // nur beim Lesen; beim Schreiben berechnet
    public int Format = 13;            // nur beim Lesen: Formatkennung (12 = Altformat, 13 = aktuell)
    public int TreeVersion = 3;        // nur beim Lesen: VirtualTreeView-Stream-Version (2 oder 3)
    public int TreeOffset, ChangeLogOffset;   // nur beim Lesen: Beginn des Baums bzw. des Aenderungsprotokolls im entpackten Strom
    public int Kind = 1;              // 1 = Client-Skript, 0 = Console-Skript (bei Client Commands). Genauer wohl „aktives Skript": beim
                                      // Console-Command {1F5A4238-…} ist es umgekehrt (Client 0, Console 1). Beim Lesen uebernommen.
    public List<CcVar> Vars = new List<CcVar>();
    public List<CcNode> Roots = new List<CcNode>();
    public byte[] ChangeLog;           // Rohbytes nach dem Baum (int32 Anzahl + Eintraege); null = leeres Protokoll
    // Kopf-JSON fuer individuelle Felder, die das Skript benutzt (z.B. {"StoredCustomFields":[{...}]}); null = leerer Standardblock
    public string StoredCustomFields, StoredTables, StoredFolders;
    // nur beim Lesen: die Bloecke so komprimiert, wie ACMP sie abgelegt hat (Rundlauf-Pruefung); Encode nimmt sie, solange der Text gleich ist
    public byte[] StoredCustomFieldsRaw, StoredTablesRaw, StoredFoldersRaw;
    // Kopfbloecke 1, 2 und 6 (Bedeutung nicht gesichert, Block 1 = Namensliste, s.o.): beim Lesen roh uebernommen und unveraendert
    // zurueckgeschrieben, damit ein Rundlauf bytegleich bleibt; null = leer (neue Skripte)
    public byte[] Head1Raw, Head2Raw, Head6Raw;
    public List<string> Warnings = new List<string>();
}

static class CcFormat
{
    const string Enc = "[Encoding:UTF8]";
    // Kopf nach int32 13 / int32 Schritte: EncString mit 6 Byte "3424" 02 00 + int32 0 – in allen Proben identisch.
    static readonly byte[] HeadConst = Hex("0F0000005B456E636F64696E673A555446385D0600000033343234020000000000");
    // Die drei JSON-Bloecke ({"StoredCustomFields":[]}, {"StoredTables":[]}, {"StoredFolders":[]}) exakt wie ACMP sie komprimiert.
    static readonly byte[] Json1 = Hex("AB560A2EC92F4A4D712E2D2EC9CF75CB4CCD492956B28A8EAD0500");
    static readonly byte[] Json2 = Hex("AB560A2EC92F4A4D09494CCA492D56B28A8EAD0500");
    static readonly byte[] Json3 = Hex("AB560A2EC92F4A4D71CBCF49492D2A56B28A8EAD0500");
    static readonly byte[] Magic = Hex("452056005400030020004620");   // WideChars ⁅ V T 3 ␠ ⁆ (VirtualTreeView, Stream-Version 3)
    static readonly byte[] MagicHead = Hex("452056005400");           // ⁅ V T  – danach Word Version, dann ␠ ⁆
    static readonly byte[] MagicTail = Hex("20004620");
    const int BaseBody = 19;                                          // VT 3; VT 2: 17 (States als Word)
    const string XmlDecl = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n";

    // ---------------- Schreiben ----------------

    public static byte[] Encode(CcScript s)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        int total = 0; foreach (var n in s.Roots) total += Count(n);
        w.Write(13);
        w.Write(total);
        w.Write(HeadConst);
        w.Write(s.Vars.Count);
        foreach (var v in s.Vars)
        {
            w.Write(v.Origin);
            WriteStr(w, v.Name);
            WriteStr(w, v.Value ?? "");
            w.Write((byte)(v.ReadOnly ? 1 : 0));
        }
        WriteEnc(w, s.Head1Raw ?? new byte[0]);
        WriteEnc(w, s.Head2Raw ?? new byte[0]);
        WriteEnc(w, JsonBlock(s.StoredCustomFields, s.StoredCustomFieldsRaw, Json1));
        WriteEnc(w, JsonBlock(s.StoredTables, s.StoredTablesRaw, Json2));
        WriteEnc(w, JsonBlock(s.StoredFolders, s.StoredFoldersRaw, Json3));
        WriteEnc(w, s.Head6Raw ?? new byte[0]);
        w.Write(s.Kind);
        w.Write(0);
        w.Write(Magic);
        w.Write(s.Roots.Count);
        foreach (var n in s.Roots) WriteNode(w, n);
        if (s.ChangeLog != null) w.Write(s.ChangeLog); else w.Write(0);
        w.Flush();
        return Deflate(ms.ToArray());
    }

    // Kopf-JSON: null = Standardblock; gelesene Rohbytes unveraendert zurueckschreiben, solange der Text gleich geblieben ist
    static byte[] JsonBlock(string text, byte[] raw, byte[] standard)
    {
        if (text == null) return standard;
        if (raw != null && Utf8(Inflate(raw, 0, raw.Length)) == text) return raw;
        return Deflate(new UTF8Encoding(false).GetBytes(text));
    }

    static int Count(CcNode n) { int c = 1; foreach (var k in n.Children) c += Count(k); return c; }

    static void WriteNode(BinaryWriter w, CcNode n)
    {
        byte[] user = UserChunk(n);
        var kids = new MemoryStream();
        var kw = new BinaryWriter(kids);
        foreach (var k in n.Children) WriteNode(kw, k);
        kw.Flush();
        byte[] kb = kids.ToArray();
        int baseSize = BaseBody + kb.Length;
        int nodeSize = 8 + baseSize + 8 + user.Length;
        w.Write(1); w.Write(nodeSize);
        w.Write(2); w.Write(baseSize);
        w.Write(n.Children.Count);                 // ChildCount
        w.Write(n.NodeHeight);                     // NodeHeight (Standard 22)
        int st = n.States >= 0 ? n.States : (n.Children.Count > 0 ? 0xE1 : 0x81);
        w.Write(st);                               // States
        w.Write(n.Align);                          // Align (Standard 0x32)
        w.Write(n.CheckState); w.Write(n.CheckType);
        w.Write(n.Reserved);                       // Reserved
        w.Write(kb);
        w.Write(4); w.Write(user.Length);
        w.Write(user);
    }

    static byte[] UserChunk(CcNode n)
    {
        if (string.IsNullOrEmpty(n.Class)) throw new Exception("Schritt ohne Klasse.");
        byte[] x = n.XmlDeflated;
        if (x == null)
        {
            if (string.IsNullOrEmpty(n.Xml)) throw new Exception("Schritt " + n.Class + " ohne XML.");
            string xml = n.Xml.StartsWith("<?xml") ? n.Xml : XmlDecl + n.Xml;
            x = Deflate(new UTF8Encoding(false).GetBytes(xml));
        }
        var ms = new MemoryStream(); var w = new BinaryWriter(ms);
        WriteStr(w, n.Class);
        WriteEnc(w, x);
        w.Flush();
        return ms.ToArray();
    }

    static void WriteStr(BinaryWriter w, string s)
    {
        byte[] b = new UTF8Encoding(false).GetBytes(s ?? "");
        w.Write(b.Length); w.Write(b);
    }

    static void WriteEnc(BinaryWriter w, byte[] data)
    {
        WriteStr(w, Enc);
        w.Write(data.Length); w.Write(data);
    }

    // ---------------- Lesen ----------------

    public static CcScript Decode(byte[] blob)
    {
        byte[] raw = Inflate(blob, 0, blob.Length);
        var s = new CcScript();
        int p = 0;
        int fmt = I32(raw, ref p);
        s.Format = fmt;
        if (fmt != 13 && fmt != 12) s.Warnings.Add("Formatkennung " + fmt + " statt 13");
        s.TotalSteps = I32(raw, ref p);
        if (!Same(raw, p, HeadConst)) s.Warnings.Add("Kopf weicht von den bekannten Proben ab");
        p += HeadConst.Length;
        int nv = I32(raw, ref p);
        for (int i = 0; i < nv; i++)
        {
            var v = new CcVar();
            v.Origin = raw[p++];
            v.Name = Str(raw, ref p);
            v.Value = Str(raw, ref p);
            v.ReadOnly = raw[p++] != 0;
            s.Vars.Add(v);
        }
        byte[] e;
        e = EncData(raw, ref p); if (e.Length != 0) s.Head1Raw = e;
        e = EncData(raw, ref p); if (e.Length != 0) s.Head2Raw = e;
        e = EncData(raw, ref p); if (!Same(e, 0, Json1) || e.Length != Json1.Length) { s.StoredCustomFields = Utf8(Inflate(e, 0, e.Length)); s.StoredCustomFieldsRaw = e; s.Warnings.Add("StoredCustomFields: " + s.StoredCustomFields); }
        e = EncData(raw, ref p); if (!Same(e, 0, Json2) || e.Length != Json2.Length) { s.StoredTables = Utf8(Inflate(e, 0, e.Length)); s.StoredTablesRaw = e; s.Warnings.Add("StoredTables: " + s.StoredTables); }
        e = EncData(raw, ref p); if (!Same(e, 0, Json3) || e.Length != Json3.Length) { s.StoredFolders = Utf8(Inflate(e, 0, e.Length)); s.StoredFoldersRaw = e; s.Warnings.Add("StoredFolders: " + s.StoredFolders); }
        e = EncData(raw, ref p); if (e.Length != 0) s.Head6Raw = e;
        s.Kind = I32(raw, ref p);
        if (fmt >= 13) { int z = I32(raw, ref p); if (z != 0) s.Warnings.Add("Feld nach Art = " + z); }
        s.TreeOffset = p;
        if (!Same(raw, p, MagicHead) || !Same(raw, p + 8, MagicTail))
            throw new Exception("VirtualTreeView-Kennung nicht an erwarteter Stelle (Offset " + p + ", Formatkennung " + fmt + ").");
        s.TreeVersion = BitConverter.ToUInt16(raw, p + 6);
        if (s.TreeVersion != 2 && s.TreeVersion != 3) throw new Exception("Unbekannte VirtualTreeView-Version " + s.TreeVersion + ".");
        p += Magic.Length;
        int baseBody = s.TreeVersion >= 3 ? BaseBody : 17;
        int roots = I32(raw, ref p);
        for (int i = 0; i < roots; i++) s.Roots.Add(ReadNode(raw, ref p, baseBody));
        s.ChangeLogOffset = p;
        s.ChangeLog = new byte[raw.Length - p];
        Buffer.BlockCopy(raw, p, s.ChangeLog, 0, s.ChangeLog.Length);
        int total = 0; foreach (var n in s.Roots) total += Count(n);
        if (total != s.TotalSteps) s.Warnings.Add("Schrittzahl im Kopf " + s.TotalSteps + ", im Baum " + total);
        return s;
    }

    static CcNode ReadNode(byte[] raw, ref int p, int baseBody)
    {
        if (I32(raw, ref p) != 1) throw new Exception("NodeChunk erwartet (Offset " + (p - 4) + ").");
        int size = I32(raw, ref p);
        int end = p + size;
        var n = new CcNode();
        while (p < end)
        {
            int ct = I32(raw, ref p), cs = I32(raw, ref p);
            int cend = p + cs;
            if (ct == 2)
            {
                int childCount = BitConverter.ToInt32(raw, p);
                n.States = baseBody >= 19 ? BitConverter.ToInt32(raw, p + 8) : BitConverter.ToUInt16(raw, p + 8);
                if (baseBody >= 19)
                {
                    n.NodeHeight = BitConverter.ToInt32(raw, p + 4);
                    n.Align = raw[p + 12]; n.CheckState = raw[p + 13]; n.CheckType = raw[p + 14];
                    n.Reserved = BitConverter.ToInt32(raw, p + 15);
                }
                p += baseBody;
                while (p < cend) n.Children.Add(ReadNode(raw, ref p, baseBody));
                if (n.Children.Count != childCount) throw new Exception("ChildCount " + childCount + " passt nicht zu " + n.Children.Count + " Kindern.");
            }
            else if (ct == 4)
            {
                n.Class = Str(raw, ref p);
                n.XmlDeflated = EncData(raw, ref p);
                n.Xml = Utf8(Inflate(n.XmlDeflated, 0, n.XmlDeflated.Length));
            }
            p = cend;
        }
        return n;
    }

    // ---------------- Hilfen ----------------

    public static string StepXml(string body) { return XmlDecl + body; }

    // Kopfblock lesbar (nur Anzeige): entpackter Text, Zeilen mit ", " verbunden; null = leer
    public static string HeadText(byte[] raw)
    {
        if (raw == null) return null;
        try { return string.Join(", ", Utf8(Inflate(raw, 0, raw.Length)).Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)); }
        catch { return "(" + raw.Length + " Byte, nicht entpackbar)"; }
    }

    public static byte[] Deflate(byte[] data)
    {
        var o = new MemoryStream();
        using (var d = new DeflateStream(o, CompressionMode.Compress, true)) d.Write(data, 0, data.Length);
        return o.ToArray();
    }

    public static byte[] Inflate(byte[] buf, int off, int len)
    {
        var o = new MemoryStream();
        using (var d = new DeflateStream(new MemoryStream(buf, off, len), CompressionMode.Decompress)) d.CopyTo(o);
        return o.ToArray();
    }

    public static byte[] Hex(string h)
    {
        var b = new byte[h.Length / 2];
        for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(h.Substring(i * 2, 2), 16);
        return b;
    }

    static int I32(byte[] b, ref int p) { int v = BitConverter.ToInt32(b, p); p += 4; return v; }
    static string Str(byte[] b, ref int p) { int n = I32(b, ref p); string s = Encoding.UTF8.GetString(b, p, n); p += n; return s; }
    static byte[] EncData(byte[] b, ref int p)
    {
        string tag = Str(b, ref p);
        if (tag != Enc) throw new Exception("EncString erwartet, gefunden '" + tag + "' (Offset " + p + ").");
        int n = I32(b, ref p);
        var d = new byte[n]; Buffer.BlockCopy(b, p, d, 0, n); p += n;
        return d;
    }
    static bool Same(byte[] b, int p, byte[] pat)
    {
        if (p + pat.Length > b.Length) return false;
        for (int i = 0; i < pat.Length; i++) if (b[p + i] != pat[i]) return false;
        return true;
    }
    static string Utf8(byte[] b) { return Encoding.UTF8.GetString(b); }
}
