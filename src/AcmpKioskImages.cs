// AcmpKioskImages: Bilder (Icons) fuer den ACMP-Kiosk - suchen, hochladen, einem Eintrag/Ordner zuweisen, entfernen.
// Grundlage: research\kiosk.md, Offen 8 "Bilder" (14.10.2026), gelesen an 26 Console-Uploads und den Aagon-Update-Skripten:
//  - ACMP legt ein Bild als unveraenderte Datei <imageRepositoryDir>\<erste 2 Zeichen der GUID>\{GUID} (ohne Endung) ab,
//    dazu eine Zeile in SYS_CustomImages (FileSize = aufgerundete KB, LastChanged ohne Millisekunden wie in den Aagon-Skripten).
//  - Der Agent holt alle Bilder, auf die Kiosk-Ordner/-Eintraege/-Aussehen verweisen (af_CustomImages_GetImageIdsForAgent),
//    und merkt sich das neueste LastChanged -> LastChanged wird erst bei der Ausfuehrung gesetzt (GETDATE() der Datenbank).
//  - Quelle nur aus kioskWriter.imageSourceRoots (UNC). Die Bytes werden in der Freigabe eingefroren (SHA-256) und genau so geschrieben.
//  - Erlaubt: PNG, JPEG, ICO, SVG. SVG ohne Skripte, Ereignis-Attribute, DTD und externe Verweise.
//  - Entfernen nur fuer vom MCP hochgeladene, nirgends benutzte Bilder; Zeile und Datei werden vorher gesichert.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Newtonsoft.Json.Linq;
using SD = System.Drawing;

// Prueft Bilddateien ohne System.Drawing: Typ an den ersten Bytes, Pixelmass aus dem Dateikopf, SVG auf aktive Inhalte.
static class KioskImage
{
    public const int MaxNameLength = 100, MaxDescriptionLength = 250, MaxPixels = 4096;
    public static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".ico", ".svg" };

    public class Info
    {
        public string Mime, Kind, Ext;
        public int Width, Height;
        public List<string> Notes = new List<string>();
    }

    // wie ACMP: Groesse in KB, aufgerundet (an 15 Console-Uploads bestaetigt, z.B. 262142 Byte -> 256, 304381 -> 298)
    public static int FileSizeKb(long bytes) { return (int)((bytes + 1023) / 1024); }

    // Ablageort wie ACMP: <root>\<erste 2 Zeichen der GUID>\{GUID}
    public static string RepoPath(string root, string imageId)
    {
        if (string.IsNullOrEmpty(root)) throw new Exception("kioskWriter.imageRepositoryDir fehlt (Ordner Images im File Repository des ACMP-Servers).");
        string g = imageId.Trim().Trim('{', '}').ToUpperInvariant();
        return Path.Combine(Path.Combine(root, g.Substring(0, 2)), "{" + g + "}");
    }

    public static string Sha256(byte[] data)
    {
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");
    }

    // Data-URI-Typ fuer die Vorschau im Browser (ICO als image/x-icon, das verstehen alle Browser)
    public static string BrowserMime(string mime) { return mime == "image/vnd.microsoft.icon" ? "image/x-icon" : mime; }

    public static string CleanText(string v, int max, string what, bool allowEmpty)
    {
        v = (v ?? "").Trim();
        if (v.Length == 0 && !allowEmpty) throw new Exception(what + " darf nicht leer sein.");
        if (v.Length > max) throw new Exception(what + " ist laenger als " + max + " Zeichen (Spalte in SYS_CustomImages).");
        foreach (char ch in v) if (char.IsControl(ch)) throw new Exception(what + " enthaelt Steuerzeichen.");
        return v;
    }

    public static Info Inspect(byte[] d, string fileName)
    {
        if (d == null || d.Length < 8) throw new Exception("Datei ist leer oder zu kurz fuer ein Bild.");
        Info i;
        if (d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47 && d[4] == 0x0D && d[5] == 0x0A && d[6] == 0x1A && d[7] == 0x0A) i = Png(d);
        else if (d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) i = Jpeg(d);
        else if (d[0] == 0 && d[1] == 0 && d[2] == 1 && d[3] == 0) i = Ico(d);
        else if (LooksLikeSvg(d)) i = Svg(d);
        else throw new Exception("Unbekanntes Bildformat - erlaubt sind PNG, JPEG, ICO und SVG.");
        string ext = (Path.GetExtension(fileName ?? "") ?? "").ToLowerInvariant();
        if (ext.Length > 0 && ext != "." + i.Ext && !(i.Ext == "jpg" && ext == ".jpeg"))
            throw new Exception("Dateiendung " + ext + " passt nicht zum Inhalt (" + i.Kind + ").");
        if (i.Width <= 0 || i.Height <= 0 || i.Width > MaxPixels || i.Height > MaxPixels)
            throw new Exception("Unplausibles Pixelmass " + i.Width + "x" + i.Height + " (erlaubt 1 bis " + MaxPixels + ").");
        return i;
    }

    static int BE16(byte[] d, int o) { return (d[o] << 8) | d[o + 1]; }
    static int BE32(byte[] d, int o) { return (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]; }
    static int LE16(byte[] d, int o) { return d[o] | (d[o + 1] << 8); }
    static int LE32(byte[] d, int o) { return d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24); }

    static Info Png(byte[] d)
    {
        if (d.Length < 24 || Encoding.ASCII.GetString(d, 12, 4) != "IHDR") throw new Exception("PNG ohne IHDR-Kopf.");
        var i = new Info(); i.Mime = "image/png"; i.Kind = "PNG"; i.Ext = "png";
        i.Width = BE32(d, 16); i.Height = BE32(d, 20);
        return i;
    }

    // Erstes SOF-Segment (Baseline, Progressive ...) traegt Hoehe und Breite
    static Info Jpeg(byte[] d)
    {
        int o = 2;
        while (o + 9 < d.Length)
        {
            if (d[o] != 0xFF) { o++; continue; }
            byte m = d[o + 1];
            if (m == 0xFF) { o++; continue; }
            if (m == 0x01 || (m >= 0xD0 && m <= 0xD8)) { o += 2; continue; }
            if (m == 0xD9 || m == 0xDA) break;
            int len = BE16(d, o + 2);
            if (len < 2) break;
            if (m >= 0xC0 && m <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC)
            {
                var i = new Info(); i.Mime = "image/jpeg"; i.Kind = "JPEG"; i.Ext = "jpg";
                i.Height = BE16(d, o + 5); i.Width = BE16(d, o + 7);
                return i;
            }
            o += 2 + len;
        }
        throw new Exception("JPEG ohne Bildkopf (SOF).");
    }

    // ICO: Mass wie ACMP - groesstes BMP-Bild im Verzeichnis; PNG-komprimierte Eintraege zaehlen nur, wenn es keine BMP-Eintraege gibt
    // (an echten Uploads bestaetigt: ein Icon mit BMP bis 192x192 und PNG 256x256 steht in SYS_CustomImages mit 192x192)
    static Info Ico(byte[] d)
    {
        int n = LE16(d, 4);
        if (n < 1 || n > 64 || d.Length < 6 + 16 * n) throw new Exception("ICO-Verzeichnis unvollstaendig.");
        int bw = 0, bh = 0, pw = 0, ph = 0;
        for (int k = 0; k < n; k++)
        {
            int e = 6 + 16 * k;
            int w = d[e] == 0 ? 256 : d[e], h = d[e + 1] == 0 ? 256 : d[e + 1];
            int size = LE32(d, e + 8), off = LE32(d, e + 12);
            if (off < 6 || size <= 0 || (long)off + size > d.Length) throw new Exception("ICO-Eintrag " + (k + 1) + " zeigt ausserhalb der Datei.");
            if (size >= 24 && d[off] == 0x89 && d[off + 1] == 0x50 && d[off + 2] == 0x4E && d[off + 3] == 0x47)
            {
                w = BE32(d, off + 16); h = BE32(d, off + 20);
                if ((long)w * h > (long)pw * ph) { pw = w; ph = h; }
            }
            else if ((long)w * h > (long)bw * bh) { bw = w; bh = h; }
        }
        var i = new Info(); i.Mime = "image/vnd.microsoft.icon"; i.Kind = "ICO"; i.Ext = "ico";
        if (bw > 0) { i.Width = bw; i.Height = bh; } else { i.Width = pw; i.Height = ph; }
        i.Notes.Add(n + " Bildgroesse(n) im Icon, gewertet " + i.Width + "x" + i.Height + (pw > 0 && bw > 0 && (long)pw * ph > (long)bw * bh ? " (wie ACMP ohne den PNG-Eintrag " + pw + "x" + ph + ")" : ""));
        return i;
    }

    static bool LooksLikeSvg(byte[] d)
    {
        string head = Encoding.UTF8.GetString(d, 0, Math.Min(d.Length, 2048)).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        return head.StartsWith("<?xml") || head.StartsWith("<svg") || head.StartsWith("<!--") || head.StartsWith("<!DOCTYPE svg", StringComparison.OrdinalIgnoreCase);
    }

    // Elemente, die Code ausfuehren, fremde Inhalte einbetten oder Verweise zur Laufzeit aendern koennen
    static readonly string[] SvgForbidden = { "script", "foreignobject", "iframe", "embed", "object", "audio", "video", "handler", "listener",
                                              "set", "animate", "animatemotion", "animatetransform", "animatecolor", "discard" };
    static readonly Regex CssUrl = new Regex(@"url\s*\(\s*['""]?\s*([^'""\)\s]*)", RegexOptions.IgnoreCase);
    static readonly Regex SafeData = new Regex(@"^data:image/(png|jpeg|gif);base64,", RegexOptions.IgnoreCase);
    static readonly Regex Length = new Regex(@"^\s*([0-9]*\.?[0-9]+)\s*(px)?\s*$");

    // DOCTYPE: nur die uebliche Form ohne eigenen Block (<!DOCTYPE svg PUBLIC "..." "...svg11.dtd">, so in den ACMP-Standard-Icons).
    // Ein eigener Block [ ... ] kann Entitaeten definieren -> abgelehnt. Die DTD selbst wird nie geladen (Ignore, kein Resolver).
    static readonly Regex Doctype = new Regex(@"<!DOCTYPE[^>]*", RegexOptions.IgnoreCase);

    static Info Svg(byte[] d)
    {
        Match dt = Doctype.Match(Encoding.UTF8.GetString(d));
        if (dt.Success && dt.Value.IndexOf('[') >= 0) throw new Exception("SVG mit eigener DTD/Entitaeten (DOCTYPE [...]) - nicht erlaubt.");
        var s = new XmlReaderSettings();
        s.DtdProcessing = DtdProcessing.Ignore; s.XmlResolver = null;
        s.IgnoreComments = true; s.IgnoreProcessingInstructions = true;
        string w = null, h = null, vb = null; bool root = true;
        try
        {
            using (var r = XmlReader.Create(new MemoryStream(d), s))
                while (r.Read())
                {
                    if (r.NodeType == XmlNodeType.Element)
                    {
                        string ln = r.LocalName.ToLowerInvariant();
                        if (root)
                        {
                            if (ln != "svg") throw new Exception("SVG: Wurzelelement ist <" + r.LocalName + ">, erwartet <svg>.");
                            w = r.GetAttribute("width"); h = r.GetAttribute("height"); vb = r.GetAttribute("viewBox");
                            root = false;
                        }
                        if (Array.IndexOf(SvgForbidden, ln) >= 0) throw new Exception("SVG enthaelt <" + r.LocalName + "> - nicht erlaubt.");
                        if (r.HasAttributes)
                        {
                            while (r.MoveToNextAttribute())
                            {
                                string an = r.LocalName.ToLowerInvariant(), av = r.Value ?? "";
                                if (an.StartsWith("on")) throw new Exception("SVG enthaelt das Ereignis-Attribut " + r.Name + " - nicht erlaubt.");
                                if (an == "href" && !SafeRef(av)) throw new Exception("SVG verweist auf \"" + Short(av) + "\" - erlaubt sind nur interne Verweise (#...) und eingebettete PNG/JPEG/GIF.");
                                CheckActive(av, r.Name);
                            }
                            r.MoveToElement();
                        }
                    }
                    else if (r.NodeType == XmlNodeType.Text || r.NodeType == XmlNodeType.CDATA) CheckActive(r.Value ?? "", "Text/Style");
                }
        }
        catch (XmlException ex) { throw new Exception("SVG ist kein gueltiges XML (oder nutzt Entitaeten): " + ex.Message); }
        if (root) throw new Exception("SVG ohne <svg>-Element.");
        double fw, fh;
        if (!Len(w, out fw) || !Len(h, out fh))
        {
            double[] v = ViewBox(vb);
            if (v == null) throw new Exception("SVG ohne Groesse: weder width/height in Pixeln noch viewBox.");
            fw = v[2]; fh = v[3];
        }
        var i = new Info(); i.Mime = "image/svg+xml"; i.Kind = "SVG"; i.Ext = "svg";
        i.Width = (int)Math.Round(fw); i.Height = (int)Math.Round(fh);
        i.Notes.Add("SVG geprueft: keine Skripte, Ereignis-Attribute, Entitaeten oder externen Verweise");
        return i;
    }

    static bool SafeRef(string v)
    {
        v = (v ?? "").Trim();
        return v.StartsWith("#") || SafeData.IsMatch(v);
    }

    static void CheckActive(string v, string where)
    {
        string l = v.ToLowerInvariant();
        foreach (string bad in new[] { "javascript:", "vbscript:", "@import", "expression(" })
            if (l.IndexOf(bad, StringComparison.Ordinal) >= 0) throw new Exception("SVG enthaelt \"" + bad + "\" (" + where + ") - nicht erlaubt.");
        foreach (Match m in CssUrl.Matches(v))
            if (!SafeRef(m.Groups[1].Value)) throw new Exception("SVG laedt \"" + Short(m.Groups[1].Value) + "\" per url() (" + where + ") - nur interne Verweise (#...) erlaubt.");
    }

    static string Short(string v) { v = v ?? ""; return v.Length > 60 ? v.Substring(0, 60) + "..." : v; }

    static bool Len(string v, out double n)
    {
        n = 0;
        if (string.IsNullOrEmpty(v)) return false;
        Match m = Length.Match(v);
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out n) && n > 0;
    }

    static double[] ViewBox(string vb)
    {
        if (string.IsNullOrEmpty(vb)) return null;
        string[] p = vb.Trim().Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (p.Length != 4) return null;
        var r = new double[4];
        for (int k = 0; k < 4; k++) if (!double.TryParse(p[k], NumberStyles.Float, CultureInfo.InvariantCulture, out r[k])) return null;
        return r[2] > 0 && r[3] > 0 ? r : null;
    }
}

// Vorschau fuer den Chat: jedes Bild als PNG (MCP-Inhaltstyp image), laengste Seite hoechstens Box Pixel,
// kleine Icons hoechstens 2-fach vergroessert. Typ und Mass prueft vorher KioskImage.Inspect (Groesse begrenzt der Aufrufer).
// SVG kann System.Drawing nicht zeichnen -> keine Vorschau (die Freigabe-Seite zeigt SVG im Browser).
static class KioskPreview
{
    public const int Box = 128, MaxUpscale = 2;

    public static byte[] Png(byte[] data, out string note)
    {
        note = null;
        if (data == null) { note = "Bilddatei nicht gefunden"; return null; }
        try
        {
            KioskImage.Info info = KioskImage.Inspect(data, null);
            if (info.Kind == "SVG") { note = "SVG - keine Vorschau im Chat (die Freigabe-Seite zeigt es)"; return null; }
            using (SD.Bitmap src = Decode(data, info.Mime))
            {
                double f = Math.Min((double)Box / src.Width, (double)Box / src.Height);
                if (f > MaxUpscale) f = MaxUpscale;
                int w = Math.Max(1, (int)Math.Round(src.Width * f)), h = Math.Max(1, (int)Math.Round(src.Height * f));
                using (var dst = new SD.Bitmap(w, h, SD.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var g = SD.Graphics.FromImage(dst))
                    using (var ia = new SD.Imaging.ImageAttributes())
                    {
                        g.Clear(SD.Color.Transparent);
                        g.InterpolationMode = SD.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = SD.Drawing2D.PixelOffsetMode.HighQuality;
                        g.SmoothingMode = SD.Drawing2D.SmoothingMode.HighQuality;
                        ia.SetWrapMode(SD.Drawing2D.WrapMode.TileFlipXY);   // keine hellen Raender beim Skalieren
                        g.DrawImage(src, new SD.Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, SD.GraphicsUnit.Pixel, ia);
                    }
                    using (var ms = new MemoryStream()) { dst.Save(ms, SD.Imaging.ImageFormat.Png); return ms.ToArray(); }
                }
            }
        }
        catch (Exception ex) { note = "keine Vorschau moeglich (" + ex.Message + ")"; return null; }
    }

    static SD.Bitmap Decode(byte[] data, string mime)
    {
        if (mime == "image/vnd.microsoft.icon") return Ico(data);
        using (var ms = new MemoryStream(data))
        using (SD.Image img = SD.Image.FromStream(ms, false, true))
            return new SD.Bitmap(img);
    }

    // ICO: groesstes Bild (bei gleicher Groesse das mit mehr Farbtiefe). PNG-Eintraege direkt, BMP-Eintraege als Ein-Bild-Icon.
    static SD.Bitmap Ico(byte[] d)
    {
        int n = d[4] | (d[5] << 8), best = -1; long bestArea = -1; int bestBpp = -1;
        for (int k = 0; k < n; k++)
        {
            int e = 6 + 16 * k;
            int w = d[e] == 0 ? 256 : d[e], h = d[e + 1] == 0 ? 256 : d[e + 1], bpp = d[e + 6] | (d[e + 7] << 8);
            if ((long)w * h > bestArea || ((long)w * h == bestArea && bpp > bestBpp)) { best = k; bestArea = (long)w * h; bestBpp = bpp; }
        }
        int be = 6 + 16 * best;
        int size = d[be + 8] | (d[be + 9] << 8) | (d[be + 10] << 16) | (d[be + 11] << 24);
        int off = d[be + 12] | (d[be + 13] << 8) | (d[be + 14] << 16) | (d[be + 15] << 24);
        if (d[off] == 0x89 && d[off + 1] == 0x50 && d[off + 2] == 0x4E && d[off + 3] == 0x47)
        {
            var png = new byte[size]; Buffer.BlockCopy(d, off, png, 0, size);
            return Decode(png, "image/png");
        }
        var one = new byte[22 + size];
        one[2] = 1; one[4] = 1;                                   // Kopf: Typ 1 (Icon), 1 Bild
        Buffer.BlockCopy(d, be, one, 6, 8);                       // Breite, Hoehe, Farben, Ebenen, Bit
        one[14] = (byte)size; one[15] = (byte)(size >> 8); one[16] = (byte)(size >> 16); one[17] = (byte)(size >> 24);
        one[18] = 22;                                             // Daten direkt hinter dem Verzeichnis
        Buffer.BlockCopy(d, off, one, 22, size);
        using (var ms = new MemoryStream(one))
        using (var ico = new SD.Icon(ms))
            return ico.ToBitmap();
    }
}

partial class AcmpKiosk
{
    string _imageDir, _imageFolderCfg;
    readonly List<string> _imageSourceRoots = new List<string>();
    long _maxImageBytes;
    public string ImageSourceRootsText { get { return string.Join(", ", _imageSourceRoots.ToArray()); } }

    static readonly string[] ImageColumns = { "Id:uniqueidentifier:", "FolderId:uniqueidentifier:", "Name:nvarchar:100", "Description:nvarchar:250",
        "MimeType:varchar:127", "Width:int:", "Height:int:", "FileSize:int:", "LastChanged:datetime:" };
    static readonly string[][] ImagePerms = {
        new[] { "SYS_CustomImages", "SELECT" }, new[] { "SYS_CustomImages", "INSERT" }, new[] { "SYS_CustomImages", "DELETE" },
        new[] { "SYS_Kiosk_Items", "UPDATE" }, new[] { "SYS_Kiosk_Groups", "SELECT" }, new[] { "SYS_Kiosk_Groups", "UPDATE" } };

    void InitImages(JObject cfg, JObject ccCfg)
    {
        // Kein Standardpfad: das File Repository liegt in jeder Installation woanders (<Repository>\Images).
        // Ohne Angabe sind nur die Bild-Tools gesperrt.
        _imageDir = cfg != null && cfg["imageRepositoryDir"] != null && ((string)cfg["imageRepositoryDir"]).Trim().Length > 0
            ? ((string)cfg["imageRepositoryDir"]).Trim().TrimEnd('\\') : null;
        _imageFolderCfg = cfg != null && cfg["imageFolderId"] != null ? GuidBraces((string)cfg["imageFolderId"]) : null;
        int kb = cfg != null && cfg["maxImageKB"] != null ? (int)cfg["maxImageKB"] : 512;
        _maxImageBytes = (long)Math.Max(1, kb) * 1024;
        JArray roots = (cfg != null ? cfg["imageSourceRoots"] as JArray : null) ?? (ccCfg != null ? ccCfg["fileSourceRoots"] as JArray : null);
        if (roots != null)
            foreach (JToken t in roots)
            {
                string r = ((string)t ?? "").Trim().TrimEnd('\\');
                if (r.Length > 0) _imageSourceRoots.Add(r);
            }
    }

    static bool IsImageAction(string act) { return act == "add_image" || act == "set_image" || act == "remove_image"; }

    // ================= Vorpruefung (nur Hinweise: sperrt bei Problemen nur die Bild-Tools, nicht den Kiosk-Writer) =================
    bool CheckImages(JArray checks, SqlConnection rc, SqlConnection wc)
    {
        bool ok = true;
        // Schema
        var cols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var q = rc.CreateCommand();
        q.CommandText = "SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA='dbo' AND TABLE_NAME='SYS_CustomImages'";
        using (var r = q.ExecuteReader()) while (r.Read()) cols[r.GetString(0)] = r.GetString(1) + ":" + (r.IsDBNull(2) ? "" : Convert.ToInt32(r.GetValue(2)).ToString());
        var diffs = new List<string>(); var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string spec in ImageColumns)
        {
            string[] p = spec.Split(':'); expected.Add(p[0]); string got;
            if (!cols.TryGetValue(p[0], out got)) diffs.Add("fehlt " + p[0]);
            else if (!string.Equals(got, p[1] + ":" + p[2], StringComparison.OrdinalIgnoreCase)) diffs.Add(p[0] + " ist " + got + " statt " + p[1] + ":" + p[2]);
        }
        foreach (string c in cols.Keys) if (!expected.Contains(c)) diffs.Add("zusaetzliche Spalte " + c);
        if (diffs.Count > 0) ok = false;
        Add(checks, "Bilder: Schema SYS_CustomImages", diffs.Count == 0 ? "ok" : "warn", diffs.Count == 0 ? ImageColumns.Length + " Spalten wie erwartet" : string.Join("; ", diffs.ToArray()));

        // Trigger: auf SYS_CustomImages und SYS_Kiosk_Groups ist keiner bekannt; auf SYS_Kiosk_Items nur der Loesch-Trigger (AFTER DELETE)
        q = wc.CreateCommand();
        q.CommandText = "SELECT OBJECT_NAME(parent_id) + '.' + name FROM sys.triggers WHERE parent_id IN (OBJECT_ID('dbo.SYS_CustomImages'), OBJECT_ID('dbo.SYS_Kiosk_Groups'))";
        var trig = new List<string>();
        using (var r = q.ExecuteReader()) while (r.Read()) trig.Add(r.GetString(0));
        if (trig.Count > 0) ok = false;
        Add(checks, "Bilder: Trigger", trig.Count == 0 ? "ok" : "warn", trig.Count == 0 ? "keine auf SYS_CustomImages und SYS_Kiosk_Groups" : "unbekannt: " + string.Join(", ", trig.ToArray()));

        // Rechte des Schreib-Logins
        var miss = new List<string>();
        foreach (string[] p in ImagePerms) if (!HasPerm(wc, p[0], p[1])) miss.Add(p[0] + " " + p[1]);
        if (miss.Count > 0) ok = false;
        Add(checks, "Bilder: Rechte", miss.Count == 0 ? "ok" : "warn", miss.Count == 0 ? ImagePerms.Length + " benoetigte Rechte vorhanden" : "fehlen: " + string.Join(", ", miss.ToArray()) + " (grant-kiosk-writer.sql, Abschnitt 4)");

        // Bildordner in SYS_Folders
        try { JObject f = ResolveImageFolder(rc); Add(checks, "Bilder: Ordner", "ok", "\"" + f["name"] + "\" " + f["id"]); }
        catch (Exception ex) { ok = false; Add(checks, "Bilder: Ordner", "warn", ex.Message); }

        // Ablage: die 5 neuesten Bilder muessen dort liegen (beweist, dass es die aktive Ablage ist)
        if (_imageDir == null) { ok = false; Add(checks, "Bilder: Ablage", "warn", "kioskWriter.imageRepositoryDir fehlt (Ordner Images im File Repository des ACMP-Servers)"); }
        else if (!Directory.Exists(_imageDir)) { ok = false; Add(checks, "Bilder: Ablage", "warn", "Ordner nicht gefunden: " + _imageDir + " (kioskWriter.imageRepositoryDir)"); }
        else
        {
            q = rc.CreateCommand();
            q.CommandText = "SELECT TOP 5 CAST(Id AS varchar(36)) FROM SYS_CustomImages ORDER BY LastChanged DESC";
            int seen = 0; var gone = new List<string>();
            using (var r = q.ExecuteReader())
                while (r.Read()) { seen++; string id = "{" + r.GetString(0).ToUpperInvariant() + "}"; if (!File.Exists(KioskImage.RepoPath(_imageDir, id))) gone.Add(id); }
            if (gone.Count > 0) ok = false;
            Add(checks, "Bilder: Ablage", gone.Count == 0 ? "ok" : "warn", gone.Count == 0 ? _imageDir + " (die " + seen + " neuesten Bilder liegen dort)" : _imageDir + ": Dateien fehlen fuer " + string.Join(", ", gone.ToArray()) + " - falsche Ablage?");
        }

        // Quellen
        if (_imageSourceRoots.Count == 0) { ok = false; Add(checks, "Bilder: Quellen", "warn", "kioskWriter.imageSourceRoots ist leer (UNC-Ordner, aus dem Bilder gelesen werden duerfen)."); }
        else
        {
            var unreachable = new List<string>();
            foreach (string r in _imageSourceRoots) { try { if (!Directory.Exists(r)) unreachable.Add(r); } catch { unreachable.Add(r); } }
            Add(checks, "Bilder: Quellen", unreachable.Count == 0 ? "ok" : "warn", string.Join(", ", _imageSourceRoots.ToArray()) + (unreachable.Count == 0 ? "" : " - fuer den Dienst nicht erreichbar: " + string.Join(", ", unreachable.ToArray())));
        }
        Add(checks, "Bilder", ok ? "ok" : "warn", ok ? "Bild-Tools aktiv" : "Bild-Tools gesperrt (siehe Pruefungen \"Bilder: ...\"); der Kiosk-Writer selbst bleibt aktiv.");
        return ok;
    }

    public void CheckImageWrite()
    {
        JObject pf = Preflight(false);
        if (pf == null || pf["imageTools"] == null || !(bool)pf["imageTools"])
            throw new Exception("Bild-Tools gesperrt - Vorpruefung \"Bilder\" nicht bestanden (Details: acmp_kiosk_tree mit status=true). Nichts geschrieben.");
    }

    // ================= Lesen =================
    JObject ResolveImageFolder(SqlConnection c)
    {
        var q = c.CreateCommand();
        if (_imageFolderCfg != null)
        {
            q.CommandText = "SELECT CAST(FolderID AS varchar(36)), Caption FROM SYS_Folders WHERE FolderID=@f AND FolderType=60";
            q.Parameters.AddWithValue("@f", G(_imageFolderCfg));
        }
        else q.CommandText = "SELECT TOP 2 CAST(FolderID AS varchar(36)), Caption FROM SYS_Folders WHERE Caption='Pictures' AND FolderType=60";
        var hits = new List<JObject>();
        using (var r = q.ExecuteReader())
            while (r.Read()) { var o = new JObject(); o["id"] = "{" + r.GetString(0).ToUpperInvariant() + "}"; o["name"] = r.GetString(1); hits.Add(o); }
        if (hits.Count == 0) throw new Exception(_imageFolderCfg != null ? "Bildordner " + _imageFolderCfg + " (kioskWriter.imageFolderId) nicht gefunden." : "Bildordner \"Pictures\" (SYS_Folders, FolderType 60) nicht gefunden - kioskWriter.imageFolderId setzen.");
        if (hits.Count > 1) throw new Exception("Bildordner \"Pictures\" ist nicht eindeutig - kioskWriter.imageFolderId setzen.");
        return hits[0];
    }

    // Eine Zeile aus SYS_CustomImages per GUID oder eindeutigem Namen
    JObject ImageRow(SqlConnection c, string s)
    {
        if (string.IsNullOrEmpty(s)) throw new Exception("image fehlt (Bild: GUID oder Name, siehe acmp_kiosk_images).");
        string id = GuidBraces(s);
        var q = c.CreateCommand();
        q.CommandText = "SELECT TOP 3 CAST(ci.Id AS varchar(36)), CAST(ci.FolderId AS varchar(36)), ci.Name, ci.Description, ci.MimeType, ci.Width, ci.Height, ci.FileSize, " +
                        "CONVERT(varchar(19), ci.LastChanged, 120), f.Caption FROM SYS_CustomImages ci LEFT JOIN SYS_Folders f ON f.FolderID=ci.FolderId WHERE " + (id != null ? "ci.Id=@g" : "ci.Name=@n");
        if (id != null) q.Parameters.AddWithValue("@g", G(id)); else q.Parameters.AddWithValue("@n", s.Trim());
        var hits = new List<JObject>();
        using (var r = q.ExecuteReader())
            while (r.Read())
            {
                var o = new JObject();
                o["Id"] = "{" + r.GetString(0).ToUpperInvariant() + "}";
                o["FolderId"] = r.IsDBNull(1) ? null : "{" + r.GetString(1).ToUpperInvariant() + "}";
                o["Name"] = r.GetString(2); o["Description"] = r.IsDBNull(3) ? null : r.GetString(3);
                o["MimeType"] = r.GetString(4);
                o["Width"] = r.IsDBNull(5) ? null : (JToken)r.GetInt32(5); o["Height"] = r.IsDBNull(6) ? null : (JToken)r.GetInt32(6);
                o["FileSize"] = r.GetInt32(7); o["LastChanged"] = r.GetString(8);
                o["folderName"] = r.IsDBNull(9) ? null : r.GetString(9);
                hits.Add(o);
            }
        if (hits.Count == 0) throw new Exception("Bild \"" + s + "\" nicht in SYS_CustomImages gefunden.");
        if (hits.Count > 1) throw new Exception("Bild \"" + s + "\" ist nicht eindeutig - bitte GUID angeben.");
        return hits[0];
    }

    static string MimeShort(string mime)
    {
        switch (mime) { case "image/png": return "PNG"; case "image/jpeg": return "JPEG"; case "image/vnd.microsoft.icon": return "ICO"; case "image/svg+xml": return "SVG"; }
        return mime;
    }

    string ImageLabel(SqlConnection c, string id)
    {
        if (string.IsNullOrEmpty(id)) return "(kein Icon)";
        try { JObject r = ImageRow(c, id); return "\"" + r["Name"] + "\" " + id + " (" + MimeShort((string)r["MimeType"]) + " " + r["Width"] + "x" + r["Height"] + ")"; }
        catch { return id + " (nicht in SYS_CustomImages)"; }
    }

    // Wer benutzt das Bild? (Kiosk-Eintraege, -Ordner, Container, Kiosk-Aussehen) - mit dem Lese-Login
    List<string> ImageUses(SqlConnection c, string id)
    {
        var q = c.CreateCommand();
        q.CommandText =
            "SELECT 'Kiosk-Eintrag', ISNULL(m.Caption_de, CAST(i.Id AS varchar(36))) FROM SYS_Kiosk_Items i LEFT JOIN SYS_MultiLanguageItems m ON m.Id=i.Id WHERE i.ImageId=@g " +
            "UNION ALL SELECT 'Kiosk-Ordner', ISNULL(m.Caption_de, CAST(g.Id AS varchar(36))) FROM SYS_Kiosk_Groups g LEFT JOIN SYS_MultiLanguageItems m ON m.Id=g.Id WHERE g.ImageId=@g " +
            "UNION ALL SELECT 'Container', ISNULL(s.Name, '') FROM CLT_CONTAINER_STRUCTURE s WHERE s.ImageId=@g " +
            "UNION ALL SELECT 'Kiosk-Aussehen', ISNULL(a.KioskWindowName, '') FROM SYS_Kiosk_Appearance a WHERE a.KioskWindowIconId=@g OR a.CompanyLogoId=@g";
        q.Parameters.AddWithValue("@g", G(id));
        var l = new List<string>();
        using (var r = q.ExecuteReader()) while (r.Read()) l.Add(r.GetString(0) + " \"" + (r.IsDBNull(1) ? "" : r.GetString(1)) + "\"");
        return l;
    }

    bool UploadedByMcp(string id)
    {
        if (!Directory.Exists(_backupDir)) return false;
        return Directory.GetFiles(_backupDir, "*_imageadd_" + id.Trim('{', '}').ToUpperInvariant() + ".json").Length > 0;
    }

    string ImageIdByName(SqlConnection c, string name)
    {
        var q = c.CreateCommand();
        q.CommandText = "SELECT TOP 1 CAST(Id AS varchar(36)) FROM SYS_CustomImages WHERE Name=@n";
        q.Parameters.AddWithValue("@n", name);
        object o = q.ExecuteScalar();
        return o == null || o is DBNull ? null : "{" + ((string)o).ToUpperInvariant() + "}";
    }

    bool ImageExists(SqlConnection c, string id)
    {
        var q = c.CreateCommand();
        q.CommandText = "SELECT COUNT(*) FROM SYS_CustomImages WHERE Id=@g";
        q.Parameters.AddWithValue("@g", G(id));
        return Convert.ToInt32(q.ExecuteScalar()) > 0;
    }

    // Gleiche Bytes schon vorhanden? Vorfilter ueber FileSize, dann SHA-256 der Dateien in der Ablage.
    JObject SameBytes(SqlConnection c, byte[] data, string sha)
    {
        if (!Directory.Exists(_imageDir)) return null;
        var q = c.CreateCommand();
        q.CommandText = "SELECT CAST(Id AS varchar(36)), Name FROM SYS_CustomImages WHERE FileSize=@s";
        q.Parameters.AddWithValue("@s", KioskImage.FileSizeKb(data.Length));
        var cand = new List<string[]>();
        using (var r = q.ExecuteReader()) while (r.Read()) cand.Add(new[] { "{" + r.GetString(0).ToUpperInvariant() + "}", r.GetString(1) });
        foreach (string[] k in cand)
        {
            string f = KioskImage.RepoPath(_imageDir, k[0]);
            try
            {
                var fi = new FileInfo(f);
                if (!fi.Exists || fi.Length != data.Length) continue;
                if (KioskImage.Sha256(File.ReadAllBytes(f)) == sha) { var o = new JObject(); o["id"] = k[0]; o["name"] = k[1]; return o; }
            }
            catch { }
        }
        return null;
    }

    public const int MaxPreviews = 6, MaxSourceMatches = 2000;

    // acmp_kiosk_images: Bilder suchen (Name, Ordner, GUID), mit Verwendung, Herkunft (MCP) und Dateipruefung;
    // where=files|both auch Dateien in den Bildquellen; preview=N zeigt die ersten N Treffer als Bild (Auswahl im Chat)
    public JObject Images(JObject a)
    {
        string search = a["search"] != null && a["search"].Type != JTokenType.Null ? ((string)a["search"] ?? "").Trim() : "";
        string where = a["where"] != null && a["where"].Type != JTokenType.Null ? ((string)a["where"] ?? "").Trim().ToLowerInvariant() : "";
        if (where.Length == 0) where = "acmp";
        if (where != "acmp" && where != "files" && where != "both") throw new Exception("where: acmp (Standard, Bilder in ACMP), files (Dateien in den Bildquellen) oder both.");
        int preview = 0;
        if (a["preview"] != null && a["preview"].Type == JTokenType.Integer) preview = Math.Max(0, Math.Min(MaxPreviews, (int)a["preview"]));
        else if (a["preview"] != null && a["preview"].Type == JTokenType.Boolean && (bool)a["preview"]) preview = 3;
        JObject res = where == "files" ? new JObject() : AcmpImages(a, search);
        var picks = new List<JObject>();   // Auswahl-Kandidaten in Anzeige-Reihenfolge: erst ACMP-Bilder, dann Dateien
        if (res["bilder"] != null) foreach (JObject o in (JArray)res["bilder"]) picks.Add(o);
        if (where != "acmp")
        {
            int total; string note;
            JArray files;
            using (var c = new SqlConnection(_readCs)) { c.Open(); files = SourceFiles(c, search, TopArg(a), out total, out note); }
            res["dateienTreffer"] = total;
            if (note != null) res["dateienHinweis"] = note;
            res["dateien"] = files;
            foreach (JObject o in files) picks.Add(o);
        }
        if (preview > 0)
        {
            var sel = new JArray(); var pv = new JArray();
            foreach (JObject o in picks)
            {
                if (sel.Count >= preview) break;
                if (o["hinweis"] != null && o["typ"] == null) continue;   // unlesbare Datei
                int nr = sel.Count + 1;
                var s = new JObject(); s["nr"] = nr; s["name"] = o["name"];
                bool isFile = o["pfad"] != null;
                if (isFile)
                {
                    s["art"] = "Datei (noch nicht in ACMP)"; s["pfad"] = o["pfad"];
                    s["verwenden"] = o["schonInAcmp"] != null ? "schon in ACMP als " + o["schonInAcmp"] + " - das vorhandene Bild verwenden"
                                                             : "erst hochladen: acmp_kiosk_add_image source=\"" + o["pfad"] + "\"";
                }
                else { s["art"] = "Bild in ACMP"; s["id"] = o["id"]; s["verwenden"] = "image=\"" + o["id"] + "\" (acmp_kiosk_set_image / add_item / add_group)"; }
                s["typ"] = o["typ"]; s["pixel"] = o["pixel"];
                byte[] data = isFile ? ReadSmall((string)o["pfad"]) : RepoBytes((string)o["id"]);
                JObject p = AddPreview(pv, "Bild " + nr + ": \"" + o["name"] + "\" (" + o["typ"] + " " + o["pixel"] + ")", data);
                s["vorschau"] = p["data"] != null ? "Bild " + nr + " unten" : (JToken)p["hinweis"];
                sel.Add(s);
            }
            res["auswahl"] = sel;
            res["_previews"] = pv;
        }
        return res;
    }

    static int TopArg(JObject a) { return a["top"] != null && a["top"].Type == JTokenType.Integer ? Math.Max(1, Math.Min(200, (int)a["top"])) : 50; }

    JObject AcmpImages(JObject a, string search)
    {
        string folder = a["folder"] != null && a["folder"].Type != JTokenType.Null ? ((string)a["folder"] ?? "").Trim() : "";
        string one = a["image"] != null && a["image"].Type != JTokenType.Null ? ((string)a["image"] ?? "").Trim() : "";
        bool unused = a["unused"] != null && a["unused"].Type == JTokenType.Boolean && (bool)a["unused"];
        int top = TopArg(a);
        var list = new JArray();
        using (var c = new SqlConnection(_readCs))
        {
            c.Open();
            var useItems = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string sql in new[] {
                "SELECT CAST(i.ImageId AS varchar(36)), 'Eintrag \"' + ISNULL(m.Caption_de, '?') + '\"' FROM SYS_Kiosk_Items i LEFT JOIN SYS_MultiLanguageItems m ON m.Id=i.Id WHERE i.ImageId IS NOT NULL",
                "SELECT CAST(g.ImageId AS varchar(36)), 'Ordner \"' + ISNULL(m.Caption_de, '?') + '\"' FROM SYS_Kiosk_Groups g LEFT JOIN SYS_MultiLanguageItems m ON m.Id=g.Id WHERE g.ImageId IS NOT NULL",
                "SELECT CAST(s.ImageId AS varchar(36)), 'Container \"' + ISNULL(s.Name, '?') + '\"' FROM CLT_CONTAINER_STRUCTURE s WHERE s.ImageId IS NOT NULL",
                "SELECT CAST(KioskWindowIconId AS varchar(36)), 'Kiosk-Fenstersymbol' FROM SYS_Kiosk_Appearance WHERE KioskWindowIconId IS NOT NULL",
                "SELECT CAST(CompanyLogoId AS varchar(36)), 'Kiosk-Firmenlogo' FROM SYS_Kiosk_Appearance WHERE CompanyLogoId IS NOT NULL" })
            {
                var uq = c.CreateCommand(); uq.CommandText = sql;
                using (var r = uq.ExecuteReader())
                    while (r.Read())
                    {
                        string k = "{" + r.GetString(0).ToUpperInvariant() + "}"; List<string> l;
                        if (!useItems.TryGetValue(k, out l)) { l = new List<string>(); useItems[k] = l; }
                        l.Add(r.GetString(1));
                    }
            }
            var q = c.CreateCommand();
            var where = new List<string>();
            if (one.Length > 0)
            {
                string g = GuidBraces(one);
                if (g != null) { where.Add("ci.Id=@id"); q.Parameters.AddWithValue("@id", G(g)); }
                else { where.Add("ci.Name=@nm"); q.Parameters.AddWithValue("@nm", one); }
            }
            if (search.Length > 0) { where.Add("ci.Name LIKE @s"); q.Parameters.AddWithValue("@s", "%" + search + "%"); }
            if (folder.Length > 0)
            {
                string g = GuidBraces(folder);
                if (g != null) { where.Add("ci.FolderId=@f"); q.Parameters.AddWithValue("@f", G(g)); }
                else { where.Add("f.Caption=@fn"); q.Parameters.AddWithValue("@fn", folder); }
            }
            q.CommandText = "SELECT CAST(ci.Id AS varchar(36)), ci.Name, ci.Description, ci.MimeType, ci.Width, ci.Height, ci.FileSize, CONVERT(varchar(19), ci.LastChanged, 120), f.Caption " +
                            "FROM SYS_CustomImages ci LEFT JOIN SYS_Folders f ON f.FolderID=ci.FolderId" + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where.ToArray()) : "") +
                            " ORDER BY ci.LastChanged DESC";
            int total = 0;
            using (var r = q.ExecuteReader())
                while (r.Read())
                {
                    string id = "{" + r.GetString(0).ToUpperInvariant() + "}";
                    List<string> uses; useItems.TryGetValue(id, out uses);
                    if (unused && uses != null) continue;
                    total++;
                    if (list.Count >= top) continue;
                    var o = new JObject();
                    o["id"] = id; o["name"] = r.GetString(1);
                    if (!r.IsDBNull(2) && r.GetString(2).Length > 0) o["beschreibung"] = r.GetString(2);
                    o["ordner"] = r.IsDBNull(8) ? null : r.GetString(8);
                    o["typ"] = MimeShort(r.GetString(3));
                    o["pixel"] = (r.IsDBNull(4) ? "?" : r.GetInt32(4).ToString()) + "x" + (r.IsDBNull(5) ? "?" : r.GetInt32(5).ToString());
                    o["kb"] = r.GetInt32(6); o["geaendert"] = r.GetString(7);
                    o["benutztVon"] = uses != null ? new JArray(uses.ToArray()) : new JArray();
                    if (UploadedByMcp(id)) o["vomMcp"] = true;
                    if (Directory.Exists(_imageDir) && !File.Exists(KioskImage.RepoPath(_imageDir, id))) o["datei"] = "fehlt in " + _imageDir;
                    list.Add(o);
                }
            var res = new JObject();
            res["treffer"] = total;
            if (total > list.Count) res["hinweis"] = "Nur die neuesten " + list.Count + " gezeigt (top, max. 200) - mit search/folder eingrenzen.";
            res["bilder"] = list;
            res["verwenden"] = "Als Icon: acmp_kiosk_add_item image=<id> oder acmp_kiosk_set_image target=<Eintrag/Ordner> image=<id>. Neues Bild: acmp_kiosk_add_image source=<UNC-Pfad>. Auswahl mit Vorschau: preview=3.";
            return res;
        }
    }

    // Bilddateien unter kioskWriter.imageSourceRoots (Kandidaten fuer acmp_kiosk_add_image), nach Dateiname sortiert
    JArray SourceFiles(SqlConnection c, string search, int top, out int total, out string note)
    {
        var arr = new JArray(); total = 0; note = null;
        if (_imageSourceRoots.Count == 0) { note = "kioskWriter.imageSourceRoots ist leer - keine Bildquellen."; return arr; }
        var hits = new List<string>();
        foreach (string root in _imageSourceRoots)
        {
            try
            {
                foreach (string f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    if (Array.IndexOf(KioskImage.Extensions, (Path.GetExtension(f) ?? "").ToLowerInvariant()) < 0) continue;
                    if (search.Length > 0 && Path.GetFileName(f).IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (hits.Count >= MaxSourceMatches) { note = "Mehr als " + MaxSourceMatches + " Bilddateien - mit search eingrenzen."; break; }
                    hits.Add(f);
                }
            }
            catch (Exception ex) { note = (note != null ? note + " | " : "") + root + ": " + ex.Message; }
        }
        hits.Sort(delegate(string x, string y) { return string.Compare(Path.GetFileName(x), Path.GetFileName(y), StringComparison.OrdinalIgnoreCase); });
        total = hits.Count;
        foreach (string f in hits)
        {
            if (arr.Count >= top) break;
            var o = new JObject(); o["name"] = Path.GetFileNameWithoutExtension(f); o["pfad"] = f;
            try
            {
                var fi = new FileInfo(f);
                o["kb"] = KioskImage.FileSizeKb(fi.Length);
                if (fi.Length > _maxImageBytes) o["hinweis"] = "zu gross fuer den Upload (max. " + (_maxImageBytes / 1024) + " KB, kioskWriter.maxImageKB)";
                byte[] d = ReadSmall(f);
                if (d != null)
                {
                    KioskImage.Info info = KioskImage.Inspect(d, fi.Name);
                    o["typ"] = info.Kind; o["pixel"] = info.Width + "x" + info.Height;
                    JObject same = SameBytes(c, d, KioskImage.Sha256(d));
                    if (same != null) o["schonInAcmp"] = "\"" + same["name"] + "\" " + same["id"];
                }
            }
            catch (Exception ex) { o["hinweis"] = ex.Message; }
            arr.Add(o);
        }
        return arr;
    }

    // Datei lesen, aber nur bis maxImageKB (sonst null)
    byte[] ReadSmall(string path)
    {
        try { var fi = new FileInfo(path); return fi.Exists && fi.Length <= _maxImageBytes ? File.ReadAllBytes(path) : null; }
        catch { return null; }
    }

    byte[] RepoBytes(string imageId) { return string.IsNullOrEmpty(imageId) || _imageDir == null ? null : ReadSmall(KioskImage.RepoPath(_imageDir, imageId)); }

    // Vorschau-Eintrag {label, data (PNG base64) | hinweis}; der Server macht daraus Text- und Bild-Bloecke
    static JObject AddPreview(JArray arr, string label, byte[] data)
    {
        string note;
        byte[] png = KioskPreview.Png(data, out note);
        var o = new JObject(); o["label"] = label;
        if (png != null) o["data"] = Convert.ToBase64String(png); else o["hinweis"] = note;
        arr.Add(o);
        return o;
    }

    void AddRepoPreview(JArray arr, SqlConnection c, string label, string imageId)
    {
        if (string.IsNullOrEmpty(imageId)) { var o = new JObject(); o["label"] = label; o["hinweis"] = "kein Icon"; arr.Add(o); return; }
        AddPreview(arr, label + ": " + ImageLabel(c, imageId), RepoBytes(imageId));
    }

    // Vorschaubilder fuer dryRun und Freigabe-Anfrage im Chat (das Icon, um das es geht; bei set_image bisher und neu)
    public JArray ChatPreviews(JObject plan)
    {
        var arr = new JArray();
        try
        {
            string act = (string)plan["action"];
            if (act == "add_image") { AddPreview(arr, "Neues Bild \"" + plan["name"] + "\"", Convert.FromBase64String((string)plan["_data"])); return arr; }
            if (act == "restore" && (string)plan["kind"] == "image")
            {
                JObject b = LoadBackup((string)plan["backup"]);
                AddPreview(arr, "Wird wiederhergestellt: \"" + b["row"]["Name"] + "\"", Convert.FromBase64String((string)b["data"]));
                return arr;
            }
            using (var c = new SqlConnection(_readCs))
            {
                c.Open();
                if (act == "set_image") { AddRepoPreview(arr, c, "Bisher", (string)plan["previousImageId"]); AddRepoPreview(arr, c, "Neu", (string)plan["imageId"]); }
                else if (act == "add_item" || act == "add_group") AddRepoPreview(arr, c, "Icon", (string)plan["imageId"]);
                else if (act == "remove_image") AddRepoPreview(arr, c, "Wird entfernt", (string)plan["image"]["Id"]);
            }
        }
        catch (Exception ex) { var o = new JObject(); o["label"] = "Vorschau"; o["hinweis"] = "nicht moeglich: " + ex.Message; arr.Add(o); }
        return arr;
    }

    // Vorschaubild fuer die Freigabe-Seite (Daten-URI): beim Hochladen aus dem Plan, sonst aus der Ablage bzw. Sicherung
    public JObject PreviewImage(JObject plan)
    {
        try
        {
            string act = (string)plan["action"], mime = null, data = null, name = null;
            if (act == "add_image") { mime = (string)plan["mime"]; data = (string)plan["_data"]; name = (string)plan["name"]; }
            else if (act == "set_image" || act == "remove_image" || act == "add_item" || act == "add_group")
            {
                string id = act == "remove_image" ? (string)plan["image"]["Id"] : (string)plan["imageId"];
                using (var c = new SqlConnection(_readCs)) { c.Open(); JObject r = ImageRow(c, id); mime = (string)r["MimeType"]; name = (string)r["Name"]; }
                string f = KioskImage.RepoPath(_imageDir, id);
                var fi = new FileInfo(f);
                if (!fi.Exists || fi.Length > _maxImageBytes) return null;
                data = Convert.ToBase64String(File.ReadAllBytes(f));
            }
            else if (act == "restore" && (string)plan["kind"] == "image")
            {
                JObject b = LoadBackup((string)plan["backup"]);
                mime = (string)b["row"]["MimeType"]; data = (string)b["data"]; name = (string)b["row"]["Name"];
            }
            if (mime == null || string.IsNullOrEmpty(data)) return null;
            var o = new JObject(); o["mime"] = KioskImage.BrowserMime(mime); o["data"] = data; o["name"] = name;
            o["label"] = act == "set_image" ? "Neues Icon" : act == "remove_image" ? "Wird entfernt" : act == "restore" ? "Wird wiederhergestellt" : act == "add_image" ? "Neues Bild" : "Icon";
            return o;
        }
        catch { return null; }
    }

    // ================= Planen =================
    string CheckImageSource(string source)
    {
        if (string.IsNullOrEmpty(source)) throw new Exception(@"source fehlt (UNC-Pfad der Bilddatei, z.B. " + (_imageSourceRoots.Count > 0 ? _imageSourceRoots[0] : @"\\server\freigabe") + @"\Icon.png).");
        if (_imageSourceRoots.Count == 0) throw new Exception("kioskWriter.imageSourceRoots ist leer - erlaubte UNC-Ordner fuer Bilder in config.local.json eintragen.");
        string full;
        try { full = Path.GetFullPath(source.Trim()); }
        catch (Exception ex) { throw new Exception("Ungueltiger Pfad '" + source + "': " + ex.Message); }
        bool inside = false;
        foreach (string root in _imageSourceRoots) if (full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) inside = true;
        if (!inside) throw new Exception("Bildquelle muss unterhalb von " + string.Join(" oder ", _imageSourceRoots.ToArray()) + " liegen (kioskWriter.imageSourceRoots).");
        if (Array.IndexOf(KioskImage.Extensions, (Path.GetExtension(full) ?? "").ToLowerInvariant()) < 0)
            throw new Exception("Nur Bilddateien " + string.Join(", ", KioskImage.Extensions) + " erlaubt.");
        return full;
    }

    void PlanImage(SqlConnection c, string tool, JObject a, JObject p, List<string> lines)
    {
        if (tool == "acmp_kiosk_add_image")
        {
            string src = CheckImageSource((string)a["source"]);
            var fi = new FileInfo(src);
            if (!fi.Exists) throw new Exception("Bilddatei nicht gefunden oder fuer den Dienst (Computerkonto des ACMP-Servers) nicht lesbar: " + src);
            if (fi.Length > _maxImageBytes) throw new Exception("Bild ist " + KioskImage.FileSizeKb(fi.Length) + " KB gross, erlaubt sind " + (_maxImageBytes / 1024) + " KB (kioskWriter.maxImageKB).");
            byte[] data = File.ReadAllBytes(src);
            KioskImage.Info info = KioskImage.Inspect(data, fi.Name);
            string sha = KioskImage.Sha256(data);
            bool hasName = a["name"] != null && a["name"].Type != JTokenType.Null;
            string name = KioskImage.CleanText(hasName ? (string)a["name"] : Path.GetFileNameWithoutExtension(fi.Name), KioskImage.MaxNameLength, "name", false);
            string desc = a["description"] != null && a["description"].Type != JTokenType.Null ? KioskImage.CleanText((string)a["description"], KioskImage.MaxDescriptionLength, "description", true) : "";
            JObject folder = ResolveImageFolder(c);
            string other = ImageIdByName(c, name);
            if (other != null) throw new Exception("Ein Bild \"" + name + "\" gibt es schon (" + other + ") - anderen Namen (name) waehlen oder das vorhandene verwenden.");
            JObject same = SameBytes(c, data, sha);
            if (same != null) throw new Exception("Genau dieses Bild gibt es schon als \"" + same["name"] + "\" " + same["id"] + " - das vorhandene verwenden.");
            string id = NewGuid();
            p["action"] = "add_image"; p["imageId"] = id; p["name"] = name; p["description"] = desc.Length > 0 ? desc : null;
            p["folder"] = folder; p["mime"] = info.Mime; p["kind"] = info.Kind; p["width"] = info.Width; p["height"] = info.Height;
            p["bytes"] = data.Length; p["fileSizeKb"] = KioskImage.FileSizeKb(data.Length); p["sha256"] = sha;
            p["source"] = src; p["file"] = KioskImage.RepoPath(_imageDir, id);
            p["_data"] = Convert.ToBase64String(data);
            lines.Add("Bild \"" + name + "\" in den ACMP-Bildordner \"" + folder["name"] + "\" hochladen.");
            lines.Add("Quelle: " + src + " (" + data.Length + " Byte, " + info.Kind + " " + info.Width + "x" + info.Height + ")");
            foreach (string n in info.Notes) lines.Add("Geprueft: " + n);
            lines.Add("SHA-256: " + sha + " (eingefroren, genau diese Bytes werden geschrieben)");
            lines.Add("Neue Bild-Id: " + id + ", Datei " + p["file"] + ", Zeile in SYS_CustomImages (" + p["fileSizeKb"] + " KB).");
            if (desc.Length > 0) lines.Add("Beschreibung: " + desc);
            lines.Add("Danach als Icon nutzbar: acmp_kiosk_add_item image=\"" + id + "\" oder acmp_kiosk_set_image.");
        }
        else if (tool == "acmp_kiosk_set_image")
        {
            string target = (string)a["target"];
            if (string.IsNullOrEmpty(target)) throw new Exception("target fehlt (Kiosk-Eintrag oder -Ordner: GUID oder Name).");
            JObject it = ResolveItem(c, target);
            var t = new JObject();
            if (it != null) { t["id"] = it["id"]; t["kind"] = "item"; t["name"] = it["name"]; t["groupId"] = it["groupId"]; }
            else { JObject g = ResolveGroup(c, target); t["id"] = g["id"]; t["kind"] = "group"; t["name"] = g["name"]; t["groupId"] = g["id"]; }
            bool isItem = (string)t["kind"] == "item";
            RequireAllowedGroup((string)t["groupId"], (isItem ? "Eintrag" : "Kiosk-Ordner") + " \"" + t["name"] + "\"");
            if (a["image"] == null || a["image"].Type == JTokenType.Null || ((string)a["image"] ?? "").Trim().Length == 0) throw new Exception("image fehlt (Bild: GUID oder Name, siehe acmp_kiosk_images).");
            string img = (string)ImageRow(c, (string)a["image"])["Id"];
            string cur = CurrentImage(c, (string)t["id"], isItem);
            if (string.Equals(cur, img, StringComparison.OrdinalIgnoreCase)) throw new Exception("\"" + t["name"] + "\" hat dieses Icon schon.");
            p["action"] = "set_image"; p["target"] = t; p["previousImageId"] = cur; p["imageId"] = img;
            lines.Add("Icon " + (isItem ? "des Kiosk-Eintrags" : "des Kiosk-Ordners") + " \"" + t["name"] + "\" " + t["id"] + " aendern.");
            lines.Add("Bisher: " + ImageLabel(c, cur));
            lines.Add("Neu: " + ImageLabel(c, img));
            if (cur != null) lines.Add("Zurueck: acmp_kiosk_set_image target=\"" + t["id"] + "\" image=\"" + cur + "\".");
        }
        else if (tool == "acmp_kiosk_remove_image")
        {
            JObject im = ImageRow(c, (string)a["image"]);
            string id = (string)im["Id"];
            if (!UploadedByMcp(id)) throw new Exception("Entfernen nur fuer Bilder, die der MCP hochgeladen hat (Protokoll *_imageadd_" + id.Trim('{', '}') + ".json in kiosk-backups). Andere Bilder in der Console loeschen.");
            List<string> uses = ImageUses(c, id);
            if (uses.Count > 0) throw new Exception("Bild \"" + im["Name"] + "\" wird noch benutzt: " + string.Join(", ", uses.ToArray()) + " - erst dort ein anderes Icon setzen (acmp_kiosk_set_image).");
            p["action"] = "remove_image"; p["image"] = im;
            lines.Add("Bild \"" + im["Name"] + "\" " + id + " (" + MimeShort((string)im["MimeType"]) + " " + im["Width"] + "x" + im["Height"] + ") ENTFERNEN: Zeile in SYS_CustomImages und Datei " + KioskImage.RepoPath(_imageDir, id) + ".");
            lines.Add("Nicht benutzt: kein Kiosk-Eintrag, -Ordner, Container und kein Kiosk-Aussehen verweist darauf.");
            lines.Add("Vorher Sicherung (Zeile und Datei) nach kiosk-backups; zurueck mit acmp_kiosk_restore.");
        }
        else throw new Exception("Unbekanntes Bild-Tool: " + tool);
    }

    void PlanRestoreImage(SqlConnection c, JObject b, JObject p, List<string> lines)
    {
        JObject row = (JObject)b["row"];
        string id = (string)row["Id"];
        if (ImageExists(c, id)) throw new Exception("Bild " + id + " existiert noch - Wiederherstellen nur fuer entfernte Bilder.");
        string other = ImageIdByName(c, (string)row["Name"]);
        if (other != null) throw new Exception("Ein anderes Bild heisst inzwischen \"" + row["Name"] + "\" (" + other + ") - nichts wiederhergestellt.");
        if (b["data"] == null || b["data"].Type == JTokenType.Null) throw new Exception("Sicherung enthaelt keine Bilddaten.");
        p["imageId"] = id;
        lines.Add("Entferntes Bild \"" + row["Name"] + "\" " + id + " wiederherstellen (Zeile und Datei aus " + p["backup"] + ").");
        lines.Add("SHA-256: " + b["sha256"]);
    }

    string CurrentImage(SqlConnection c, string id, bool item)
    {
        var q = c.CreateCommand();
        q.CommandText = "SELECT CAST(ImageId AS varchar(36)) FROM " + (item ? "SYS_Kiosk_Items" : "SYS_Kiosk_Groups") + " WHERE Id=@g";
        q.Parameters.AddWithValue("@g", G(id));
        object o = q.ExecuteScalar();
        return o == null || o is DBNull ? null : "{" + ((string)o).ToUpperInvariant() + "}";
    }

    // ================= Schreiben =================
    JObject ExecAddImage(JObject p, string requester)
    {
        string id = (string)p["imageId"], sha = (string)p["sha256"];
        byte[] data = Convert.FromBase64String((string)p["_data"]);
        if (KioskImage.Sha256(data) != sha) throw new Exception("Bilddaten passen nicht zum SHA-256 der Freigabe - nichts geschrieben.");
        KioskImage.Info info = KioskImage.Inspect(data, null);
        if (info.Mime != (string)p["mime"] || info.Width != (int)p["width"] || info.Height != (int)p["height"]) throw new Exception("Bildpruefung weicht vom Plan ab - nichts geschrieben.");
        if (!Directory.Exists(_imageDir)) throw new Exception("Bildablage " + _imageDir + " nicht gefunden - nichts geschrieben.");
        string file = KioskImage.RepoPath(_imageDir, id);
        if (!string.Equals(file, (string)p["file"], StringComparison.OrdinalIgnoreCase)) throw new Exception("Zielpfad weicht vom Plan ab - nichts geschrieben.");
        using (var rc = new SqlConnection(_readCs))
        {
            rc.Open();
            if (ImageExists(rc, id)) throw new Exception("Bild " + id + " gibt es schon - nichts geschrieben.");
            string other = ImageIdByName(rc, (string)p["name"]);
            if (other != null) throw new Exception("Ein Bild \"" + p["name"] + "\" gibt es inzwischen (" + other + ") - nichts geschrieben, bitte neu beantragen.");
            if (!string.Equals((string)ResolveImageFolder(rc)["id"], (string)p["folder"]["id"], StringComparison.OrdinalIgnoreCase)) throw new Exception("Bildordner hat sich geaendert - nichts geschrieben.");
        }
        if (File.Exists(file)) throw new Exception("Datei " + file + " gibt es schon - nichts geschrieben.");
        var jp = (JObject)p.DeepClone(); jp.Remove("_data");
        string journal = WriteJournal("imageadd", id, jp, requester);   // zugleich Kennzeichen "vom MCP hochgeladen"
        Directory.CreateDirectory(Path.GetDirectoryName(file));
        string tmp = file + ".mcp-upload";
        File.WriteAllBytes(tmp, data);
        if (KioskImage.Sha256(File.ReadAllBytes(tmp)) != sha) { File.Delete(tmp); throw new Exception("Datei nach dem Schreiben nicht bytegleich - nichts geschrieben."); }
        File.Move(tmp, file);
        try
        {
            using (var c = new SqlConnection(_writeCs))
            {
                c.Open();
                var q = c.CreateCommand();
                q.CommandText = "INSERT INTO SYS_CustomImages (Id, FolderId, Name, Description, MimeType, Width, Height, FileSize, LastChanged) " +
                                "VALUES (@id, @f, @n, @d, @m, @w, @h, @s, DATEADD(ms, -DATEPART(ms, GETDATE()), GETDATE()))";
                q.Parameters.AddWithValue("@id", G(id)); q.Parameters.AddWithValue("@f", G((string)p["folder"]["id"]));
                q.Parameters.AddWithValue("@n", (string)p["name"]);
                q.Parameters.AddWithValue("@d", p["description"] != null && p["description"].Type != JTokenType.Null ? (object)(string)p["description"] : DBNull.Value);
                q.Parameters.AddWithValue("@m", (string)p["mime"]); q.Parameters.AddWithValue("@w", info.Width); q.Parameters.AddWithValue("@h", info.Height);
                q.Parameters.AddWithValue("@s", KioskImage.FileSizeKb(data.Length));
                if (q.ExecuteNonQuery() != 1) throw new Exception("Zeile nicht eingefuegt.");
            }
        }
        catch
        {
            try { File.Delete(file); } catch { }
            throw;
        }
        _log("acmp_kiosk_add_image von " + requester + ": " + id + " \"" + p["name"] + "\" (" + info.Kind + " " + info.Width + "x" + info.Height + ", " + data.Length + " Byte)");
        var o = new JObject();
        o["ok"] = true; o["imageId"] = id; o["name"] = p["name"]; o["datei"] = file; o["protokoll"] = Path.GetFileName(journal);
        o["hinweis"] = "Als Icon nutzen: acmp_kiosk_add_item image=\"" + id + "\" oder acmp_kiosk_set_image target=<Eintrag/Ordner> image=\"" + id + "\". " +
                       "Clients laden das Bild beim naechsten Aktualisieren des Kiosk, sobald es an einem Eintrag oder Ordner haengt. Entfernen (solange unbenutzt): acmp_kiosk_remove_image.";
        return o;
    }

    JObject ExecSetImage(JObject p, string requester)
    {
        JObject t = (JObject)p["target"];
        string tid = (string)t["id"], img = (string)p["imageId"];
        bool isItem = (string)t["kind"] == "item";
        string prev = p["previousImageId"] != null && p["previousImageId"].Type != JTokenType.Null ? (string)p["previousImageId"] : null;
        using (var c = new SqlConnection(_writeCs))
        {
            c.Open();
            if (isItem)
            {
                JObject it = ResolveItem(c, tid);
                if (it == null) throw new Exception("Eintrag " + tid + " gibt es nicht mehr - nichts geschrieben.");
                RequireAllowedGroup((string)it["groupId"], "Eintrag \"" + it["name"] + "\"");
            }
            else RequireAllowedGroup(tid, "Kiosk-Ordner \"" + t["name"] + "\"");
            if (!ImageExists(c, img)) throw new Exception("Bild " + img + " gibt es nicht mehr - nichts geschrieben.");
            using (SqlTransaction tx = c.BeginTransaction())
            {
                var q = Cmd(c, tx, "UPDATE " + (isItem ? "SYS_Kiosk_Items" : "SYS_Kiosk_Groups") + " SET ImageId=@new WHERE Id=@id AND " + (prev != null ? "ImageId=@old" : "ImageId IS NULL"));
                q.Parameters.AddWithValue("@new", G(img)); q.Parameters.AddWithValue("@id", G(tid));
                if (prev != null) q.Parameters.AddWithValue("@old", G(prev));
                if (q.ExecuteNonQuery() != 1) throw new Exception("Das Icon von \"" + t["name"] + "\" wurde seit der Anfrage geaendert - nichts geschrieben, bitte neu beantragen.");
                tx.Commit();
            }
        }
        string journal = WriteJournal("imageset", tid, p, requester);
        _log("acmp_kiosk_set_image von " + requester + ": " + tid + " \"" + t["name"] + "\" " + (prev ?? "(keins)") + " -> " + img);
        var o = new JObject();
        o["ok"] = true; o["ziel"] = t["name"]; o["id"] = tid; o["icon"] = img; o["vorher"] = prev; o["protokoll"] = Path.GetFileName(journal);
        o["hinweis"] = "Im Kiosk nach dem Aktualisieren sichtbar." + (prev != null ? " Zurueck: acmp_kiosk_set_image target=\"" + tid + "\" image=\"" + prev + "\"." : "");
        return o;
    }

    JObject ExecRemoveImage(JObject p, string requester)
    {
        string id = (string)p["image"]["Id"];
        string file = KioskImage.RepoPath(_imageDir, id);
        JObject row; byte[] data = null;
        using (var rc = new SqlConnection(_readCs))
        {
            rc.Open();
            if (!ImageExists(rc, id)) throw new Exception("Bild " + id + " gibt es nicht mehr - nichts geschrieben.");
            row = ImageRow(rc, id);
            if (!UploadedByMcp(id)) throw new Exception("Bild " + id + " wurde nicht vom MCP hochgeladen - nichts geschrieben.");
            List<string> uses = ImageUses(rc, id);
            if (uses.Count > 0) throw new Exception("Bild wird inzwischen benutzt: " + string.Join(", ", uses.ToArray()) + " - nichts geschrieben.");
        }
        if (File.Exists(file)) data = File.ReadAllBytes(file);
        var b = new JObject(); b["kind"] = "image"; b["grund"] = "vor acmp_kiosk_remove_image"; b["von"] = requester; b["zeit"] = DateTime.Now.ToString("s");
        row.Remove("folderName");
        b["row"] = row; b["data"] = data != null ? Convert.ToBase64String(data) : null; b["sha256"] = data != null ? KioskImage.Sha256(data) : null; b["file"] = file;
        string backup = SaveBackup(b, id, "image");
        using (var c = new SqlConnection(_writeCs))
        {
            c.Open();
            using (SqlTransaction tx = c.BeginTransaction())
            {
                var chk = Cmd(c, tx, "SELECT (SELECT COUNT(*) FROM SYS_Kiosk_Items WHERE ImageId=@g) + (SELECT COUNT(*) FROM SYS_Kiosk_Groups WHERE ImageId=@g)");
                chk.Parameters.AddWithValue("@g", G(id));
                if (Convert.ToInt32(chk.ExecuteScalar()) > 0) throw new Exception("Bild wird inzwischen im Kiosk benutzt - nichts geschrieben.");
                var q = Cmd(c, tx, "DELETE FROM SYS_CustomImages WHERE Id=@g");
                q.Parameters.AddWithValue("@g", G(id));
                if (q.ExecuteNonQuery() != 1) throw new Exception("Bild nicht eindeutig geloescht - zurueckgerollt.");
                tx.Commit();
            }
        }
        string moved = null;
        if (data != null)
        {
            moved = Path.Combine(_backupDir, Path.GetFileNameWithoutExtension(backup) + ".bin");
            try { File.Move(file, moved); } catch (Exception ex) { _log("acmp_kiosk_remove_image: Datei " + file + " nicht verschoben: " + ex.Message); moved = null; }
        }
        _log("acmp_kiosk_remove_image von " + requester + ": " + id + " \"" + row["Name"] + "\"");
        var o = new JObject();
        o["ok"] = true; o["entfernt"] = row["Name"]; o["id"] = id; o["sicherung"] = Path.GetFileName(backup);
        if (moved != null) o["datei"] = "verschoben nach " + moved; else if (data == null) o["datei"] = "lag nicht in der Ablage";
        o["hinweis"] = "Zurueck mit acmp_kiosk_restore backup=\"" + Path.GetFileName(backup) + "\".";
        return o;
    }

    JObject ExecRestoreImage(JObject b, string backupName, string requester)
    {
        JObject row = (JObject)b["row"];
        string id = (string)row["Id"];
        byte[] data = Convert.FromBase64String((string)b["data"]);
        if (KioskImage.Sha256(data) != (string)b["sha256"]) throw new Exception("Bilddaten der Sicherung passen nicht zum SHA-256 - nichts geschrieben.");
        string file = KioskImage.RepoPath(_imageDir, id);
        string folderId;
        using (var rc = new SqlConnection(_readCs))
        {
            rc.Open();
            if (ImageExists(rc, id)) throw new Exception("Bild " + id + " existiert schon - nichts geschrieben.");
            string other = ImageIdByName(rc, (string)row["Name"]);
            if (other != null) throw new Exception("Ein anderes Bild heisst inzwischen \"" + row["Name"] + "\" - nichts geschrieben.");
            folderId = row["FolderId"] != null && row["FolderId"].Type != JTokenType.Null ? (string)row["FolderId"] : null;
            if (folderId != null)
            {
                var fq = rc.CreateCommand(); fq.CommandText = "SELECT COUNT(*) FROM SYS_Folders WHERE FolderID=@f"; fq.Parameters.AddWithValue("@f", G(folderId));
                if (Convert.ToInt32(fq.ExecuteScalar()) == 0) folderId = null;   // Ordner weg -> wie ON DELETE SET NULL
            }
        }
        bool wrote = false;
        if (File.Exists(file)) { if (KioskImage.Sha256(File.ReadAllBytes(file)) != (string)b["sha256"]) throw new Exception("Datei " + file + " liegt schon da und ist eine andere - nichts geschrieben."); }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllBytes(file + ".mcp-upload", data); File.Move(file + ".mcp-upload", file); wrote = true;
        }
        try
        {
            using (var c = new SqlConnection(_writeCs))
            {
                c.Open();
                var q = c.CreateCommand();
                q.CommandText = "INSERT INTO SYS_CustomImages (Id, FolderId, Name, Description, MimeType, Width, Height, FileSize, LastChanged) " +
                                "VALUES (@id, @f, @n, @d, @m, @w, @h, @s, DATEADD(ms, -DATEPART(ms, GETDATE()), GETDATE()))";
                q.Parameters.AddWithValue("@id", G(id)); q.Parameters.AddWithValue("@f", NG(folderId));
                q.Parameters.AddWithValue("@n", (string)row["Name"]);
                q.Parameters.AddWithValue("@d", row["Description"] != null && row["Description"].Type != JTokenType.Null ? (object)(string)row["Description"] : DBNull.Value);
                q.Parameters.AddWithValue("@m", (string)row["MimeType"]);
                q.Parameters.AddWithValue("@w", row["Width"] != null && row["Width"].Type != JTokenType.Null ? (object)(int)row["Width"] : DBNull.Value);
                q.Parameters.AddWithValue("@h", row["Height"] != null && row["Height"].Type != JTokenType.Null ? (object)(int)row["Height"] : DBNull.Value);
                q.Parameters.AddWithValue("@s", KioskImage.FileSizeKb(data.Length));
                q.ExecuteNonQuery();
            }
        }
        catch
        {
            if (wrote) try { File.Delete(file); } catch { }
            throw;
        }
        WriteJournal("imageadd", id, row, requester);   // wieder als vom MCP hochgeladen kennzeichnen
        _log("acmp_kiosk_restore (Bild) von " + requester + ": " + id + " \"" + row["Name"] + "\" aus " + backupName);
        var o = new JObject(); o["ok"] = true; o["wiederhergestellt"] = backupName; o["imageId"] = id; o["name"] = row["Name"];
        return o;
    }
}
