// DeltaruneScan: reads a PS4 copy of DELTARUNE (the folders copied over FTP from /mnt/sandbox/pfsmnt) and a PC install
// with the French patch, and writes one text report: file trees, param.sfo, the structure of every GameMaker data file
// (game.win / data.win: chunks, version, code or no code, fonts, textures), the language files, and what differs
// between the PC and PS4 data of each chapter. The report holds names, sizes, hashes and a few short sample strings:
// no game data. It is the first step of porting the French patch to the PS4 version.
//
// Windows: double-click (two folder pickers). Command line: DeltaruneScan.exe <ps4 folder> <pc folder> [report.txt]
// C# 5 / .NET Framework 4.5: mcs -sdk:4.5 -target:winexe -r:System.Windows.Forms.dll -r:System.Drawing.dll
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("DeltaruneScan")]
[assembly: System.Reflection.AssemblyProduct("DeltaruneScan")]

class Chunk { public string Name; public long Off; public long Len; }

class Entry { public string Rel; public string Full; public long Size; }

/// <summary>One GameMaker data file (IFF "FORM"): its chunks, its string table, and the lists most chunks start with.</summary>
class GmFile : IDisposable {
    public readonly string Rel;
    public readonly long Size;
    public readonly FileStream F;
    public readonly BinaryReader B;
    public readonly List<Chunk> Chunks = new List<Chunk>();
    public readonly Dictionary<string, Chunk> By = new Dictionary<string, Chunk>();
    public long FormSize;
    public bool Truncated;
    byte[] strg;
    long strgOff;

    public GmFile(string path, string rel) {
        Rel = rel;
        F = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        B = new BinaryReader(F);
        Size = F.Length;
        F.Position = 4;
        FormSize = B.ReadUInt32();
        long pos = 8;
        while (pos + 8 <= Size) {
            F.Position = pos;
            byte[] n = B.ReadBytes(4);
            long len = B.ReadUInt32();
            if (!n.All(c => c >= 0x20 && c < 0x7f)) break;
            var ch = new Chunk { Name = Encoding.ASCII.GetString(n), Off = pos + 8, Len = len };
            if (pos + 8 + len > Size) { Truncated = true; ch.Len = Size - pos - 8; }
            Chunks.Add(ch);
            if (!By.ContainsKey(ch.Name)) By[ch.Name] = ch;
            pos += 8 + len;
        }
        if (FormSize + 8 != Size) Truncated = Truncated || FormSize + 8 > Size;
        Chunk s;
        if (By.TryGetValue("STRG", out s) && s.Len >= 4 && s.Len < (1L << 30)) {
            F.Position = s.Off;
            strg = B.ReadBytes((int)s.Len);
            strgOff = s.Off;
        }
    }

    public void Dispose() { B.Close(); }

    public long Len(string chunk) { Chunk c; return By.TryGetValue(chunk, out c) ? c.Len : -1; }

    public uint U32(long off) {
        if (off < 0 || off + 4 > Size) throw new IOException("read outside the file at 0x" + off.ToString("x"));
        F.Position = off;
        return B.ReadUInt32();
    }

    public ushort U16(long off) {
        if (off < 0 || off + 2 > Size) throw new IOException("read outside the file at 0x" + off.ToString("x"));
        F.Position = off;
        return B.ReadUInt16();
    }

    public byte[] Bytes(long off, int n) {
        if (off < 0 || off >= Size) return new byte[0];
        F.Position = off;
        return B.ReadBytes((int)Math.Min(n, Size - off));
    }

    /// <summary>A string by its pointer (the address of its first character; its length is the 4 bytes before).</summary>
    public string Str(long ptr) {
        if (ptr == 0) return null;
        if (strg != null && ptr - 4 >= strgOff && ptr < strgOff + strg.Length) {
            int i = (int)(ptr - strgOff);
            int len = BitConverter.ToInt32(strg, i - 4);
            if (len < 0 || (long)i + len > strg.Length) return "<bad string>";
            return Encoding.UTF8.GetString(strg, i, len);
        }
        if (ptr < 4 || ptr >= Size) return "<bad ptr 0x" + ptr.ToString("x") + ">";
        F.Position = ptr - 4;
        int l = B.ReadInt32();
        if (l < 0 || l > (1 << 20) || ptr + l > Size) return "<bad string>";
        return Encoding.UTF8.GetString(B.ReadBytes(l));
    }

    public int StringCount { get { return strg == null || strg.Length < 4 ? 0 : (int)BitConverter.ToUInt32(strg, 0); } }

    public IEnumerable<string> AllStrings() {
        if (strg == null || strg.Length < 4) yield break;
        uint count = BitConverter.ToUInt32(strg, 0);
        for (uint k = 0; k < count; k++) {
            long tp = 4 + 4L * k;
            if (tp + 4 > strg.Length) yield break;
            long i = BitConverter.ToUInt32(strg, (int)tp) - strgOff;
            if (i < 0 || i + 4 > strg.Length) continue;
            int len = BitConverter.ToInt32(strg, (int)i);
            if (len < 0 || i + 4 + len > strg.Length) continue;
            yield return Encoding.UTF8.GetString(strg, (int)i + 4, len);
        }
    }

    /// <summary>The pointer list a chunk starts with (count, then that many file offsets); empty when it is not one.</summary>
    public List<long> PtrList(string chunk, int skipWords = 0) {
        var r = new List<long>();
        Chunk c;
        if (!By.TryGetValue(chunk, out c) || c.Len < 4 + 4 * skipWords) return r;
        long off = c.Off + 4 * skipWords;
        long count = U32(off);
        if (count * 4 + 4 + 4 * skipWords > c.Len) return r;
        F.Position = off + 4;
        byte[] raw = B.ReadBytes((int)count * 4);
        for (int i = 0; i + 4 <= raw.Length; i += 4) r.Add(BitConverter.ToUInt32(raw, i));
        return r;
    }

    public List<string> Names(string chunk, int nameWord = 0, int skipWords = 0) {
        var r = new List<string>();
        foreach (long p in PtrList(chunk, skipWords)) {
            if (p == 0 || p + 4 * nameWord + 4 > Size) continue;
            r.Add(Str(U32(p + 4 * nameWord)) ?? "<null>");
        }
        return r;
    }
}

static class Scanner {
    const string Version = "1";
    static TextWriter W;
    static Action<string> Status = s => { };
    public static int Ps4Data, PcData;

    static readonly Regex French = new Regex("[éèêëàâçùûîïôœÉÈÊÀÇ]");
    static readonly Regex Japanese = new Regex("[\u3040-\u30ff\u4e00-\u9fff]");
    static readonly Regex Interesting = new Regex(
        @"lang_|\.json|\.ini\b|translat|french|fran[cç]ais|patch|discord|https?://|^v\d+\.\d+|^\d+\.\d+\.\d+$", RegexOptions.IgnoreCase);
    static readonly Regex Platform = new Regex(@"ps4|ps5|console|switch|os_type|steam", RegexOptions.IgnoreCase);
    static readonly Regex ChapterNo = new Regex(@"chapter\s*_?(\d)", RegexOptions.IgnoreCase);

    static string N(long v) { return v.ToString("N0", System.Globalization.CultureInfo.InvariantCulture); }

    static string Cut(string s, int n = 110) {
        if (s == null) return "<null>";
        s = s.Replace("\r", "\\r").Replace("\n", "\\n");
        return s.Length <= n ? s : s.Substring(0, n) + "...";
    }

    static void Try(string what, Action a) {
        try { a(); }
        catch (Exception e) { W.WriteLine("  !! " + what + ": " + e.GetType().Name + ": " + e.Message); }
    }

    static string Md5(string path) {
        using (var m = MD5.Create())
        using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            return BitConverter.ToString(m.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
    }

    static List<Entry> Walk(string root) {
        var r = new List<Entry>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0) {
            string d = stack.Pop();
            try {
                foreach (string f in Directory.GetFiles(d)) {
                    long size = 0;
                    try { size = new FileInfo(f).Length; } catch (Exception) { }
                    r.Add(new Entry { Full = f, Rel = f.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/'), Size = size });
                }
                foreach (string s in Directory.GetDirectories(d)) stack.Push(s);
            }
            catch (Exception e) { W.WriteLine("  !! cannot read " + d + ": " + e.Message); }
        }
        r.Sort((a, b) => string.CompareOrdinal(a.Rel.ToLowerInvariant(), b.Rel.ToLowerInvariant()));
        return r;
    }

    static readonly HashSet<string> KeepExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        ".win", ".unx", ".ios", ".droid", ".json", ".ini", ".dat", ".sfo", ".bin", ".prx", ".sprx", ".txt", ".csv", ".exe",
        ".dll", ".elf", ".self", ".trp", ".xdelta", ".bak", ".orig", ".old", ".zip", ".7z"
    };

    static void Tree(List<Entry> all) {
        W.WriteLine("  " + N(all.Count) + " files, " + N(all.Sum(e => e.Size)) + " bytes");
        foreach (var dir in all.GroupBy(e => e.Rel.Contains("/") ? e.Rel.Substring(0, e.Rel.LastIndexOf('/')) : "")
                               .OrderBy(g => g.Key.ToLowerInvariant(), StringComparer.Ordinal)) {
            var files = dir.ToList();
            W.WriteLine("  [" + (dir.Key == "" ? "." : dir.Key) + "/]  " + files.Count + " files, " + N(files.Sum(e => e.Size)) + " bytes");
            if (files.Count <= 40) {
                foreach (var e in files) W.WriteLine("      " + Path.GetFileName(e.Rel) + "  " + N(e.Size));
                continue;
            }
            foreach (var ext in files.GroupBy(e => Path.GetExtension(e.Rel).ToLowerInvariant()).OrderByDescending(g => g.Count())) {
                var l = ext.ToList();
                W.WriteLine("      *" + (ext.Key == "" ? "(no extension)" : ext.Key) + ": " + l.Count + " files, " + N(l.Sum(e => e.Size)) +
                            " bytes, e.g. " + string.Join(", ", l.Take(6).Select(e => Path.GetFileName(e.Rel))));
                if (KeepExt.Contains(ext.Key)) foreach (var e in l.Take(80)) W.WriteLine("        " + Path.GetFileName(e.Rel) + "  " + N(e.Size));
            }
        }
    }

    static void Sfo(Entry e) {
        W.WriteLine("  " + e.Rel + "  (" + N(e.Size) + " bytes, md5 " + Md5(e.Full) + ")");
        byte[] d = File.ReadAllBytes(e.Full);
        if (d.Length < 20 || d[0] != 0 || d[1] != (byte)'P' || d[2] != (byte)'S' || d[3] != (byte)'F') { W.WriteLine("    not a param.sfo"); return; }
        int keys = BitConverter.ToInt32(d, 8), data = BitConverter.ToInt32(d, 12), count = BitConverter.ToInt32(d, 16);
        for (int i = 0; i < count; i++) {
            int o = 20 + 16 * i;
            if (o + 16 > d.Length) break;
            int ko = keys + BitConverter.ToUInt16(d, o), fmt = BitConverter.ToUInt16(d, o + 2);
            int len = BitConverter.ToInt32(d, o + 4), max = BitConverter.ToInt32(d, o + 8), dat = data + BitConverter.ToInt32(d, o + 12);
            int ke = ko;
            while (ke < d.Length && d[ke] != 0) ke++;
            string key = Encoding.ASCII.GetString(d, ko, ke - ko), val;
            if (dat + len > d.Length) val = "<outside the file>";
            else if (fmt == 0x0404) val = "0x" + BitConverter.ToUInt32(d, dat).ToString("x8");
            else val = "\"" + Encoding.UTF8.GetString(d, dat, Math.Max(0, len)).TrimEnd('\0') + "\"";
            W.WriteLine("    " + key + " = " + val + "   (fmt 0x" + fmt.ToString("x4") + ", " + len + "/" + max + ")");
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // One data file

    static string TexMagic(byte[] h, out string dims) {
        dims = "";
        if (h.Length < 12) return "short";
        if (h[0] == 0x89 && h[1] == 'P' && h[2] == 'N' && h[3] == 'G') {
            if (h.Length >= 24) dims = ((h[16] << 24) | (h[17] << 16) | (h[18] << 8) | h[19]) + "x" + ((h[20] << 24) | (h[21] << 16) | (h[22] << 8) | h[23]);
            return "PNG";
        }
        string m = Encoding.ASCII.GetString(h, 0, 4);
        if (m == "fioq" || m == "2zoq") { dims = BitConverter.ToUInt16(h, 4) + "x" + BitConverter.ToUInt16(h, 6); return m == "fioq" ? "QOI" : "BZ2+QOI"; }
        if (m == "GNF ") return "GNF";
        if (m == "DDS ") return "DDS";
        if (h[0] == 'B' && h[1] == 'Z' && h[2] == 'h') return "BZ2";
        return "?" + BitConverter.ToString(h, 0, 8);
    }

    static void Textures(GmFile g) {
        var tx = g.PtrList("TXTR");
        W.WriteLine("  TXTR: " + tx.Count + " texture pages");
        if (tx.Count == 0) return;
        long esize = tx.Count >= 2 ? tx[1] - tx[0] : 0x1c;
        if (esize < 8 || esize > 0x40) esize = 0x1c;
        int words = (int)(esize / 4);
        W.WriteLine("    entry size " + esize + " bytes (" + words + " words; the last one is the data pointer)");
        var tally = new Dictionary<string, int>();
        var lines = new List<string>();
        for (int i = 0; i < tx.Count; i++) {
            var v = new uint[words];
            for (int k = 0; k < words; k++) v[k] = g.U32(tx[i] + 4 * k);
            long data = v[words - 1];
            string dims, fmt;
            if (data == 0) { fmt = "external (no data in this file)"; dims = ""; }
            else fmt = TexMagic(g.Bytes(data, 24), out dims);
            int t;
            tally[fmt] = tally.TryGetValue(fmt, out t) ? t + 1 : 1;
            if (i < 3 || lines.Count < 120)
                lines.Add("    #" + i + "  " + fmt + " " + dims + (i < 3 ? "   words: " + string.Join(" ", v.Select(x => x.ToString("x"))) : ""));
        }
        W.WriteLine("    formats: " + string.Join(", ", tally.Select(kv => kv.Key + " x" + kv.Value)));
        foreach (string l in lines) W.WriteLine(l);
    }

    static void Fonts(GmFile g) {
        var fonts = g.PtrList("FONT");
        W.WriteLine("  FONT: " + fonts.Count + " fonts");
        foreach (long p in fonts) {
            if (p == 0) continue;
            string name = g.Str(g.U32(p)), disp = g.Str(g.U32(p + 4));
            long listAt = 0, n = 0;
            long limit = Math.Min(p + 0x80, g.Size - 8);
            for (long q = p + 8; q <= limit; q += 4) {  // the glyph list: a count, then pointers, the glyphs right after them
                long c = g.U32(q);
                if (c < 1 || c > 0x20000) continue;
                if (g.U32(q + 4) == q + 4 + 4 * c) { listAt = q; n = c; break; }
            }
            if (listAt == 0) {
                W.WriteLine("    " + name + " | " + disp + " | glyph list not found, words: " +
                            string.Join(" ", Enumerable.Range(0, 12).Select(k => g.U32(p + 4 * k).ToString("x"))));
                continue;
            }
            byte[] raw = g.Bytes(listAt + 4, (int)n * 4);
            int min = int.MaxValue, max = 0, latin = 0, jp = 0;
            bool eacute = false, oe = false;
            for (int k = 0; k + 4 <= raw.Length; k += 4) {
                int ch = g.U16(BitConverter.ToUInt32(raw, k));
                if (ch < min) min = ch;
                if (ch > max) max = ch;
                if (ch >= 0xC0 && ch <= 0xFF) latin++;
                if (ch >= 0x3000) jp++;
                if (ch == 0xE9) eacute = true;
                if (ch == 0x153) oe = true;
            }
            W.WriteLine("    " + name + " | " + disp + " | " + n + " glyphs, U+" + min.ToString("X4") + "-U+" + max.ToString("X4") +
                        ", accented Latin-1: " + latin + (eacute ? " (é yes)" : " (é NO)") + (oe ? ", œ yes" : "") + (jp > 0 ? ", CJK: " + jp : "") +
                        ", header +0x" + (listAt - p).ToString("x"));
        }
    }

    static Dictionary<string, uint> CodeLengths(GmFile g) {
        var r = new Dictionary<string, uint>();
        foreach (long p in g.PtrList("CODE")) {
            if (p == 0 || p + 8 > g.Size) continue;
            string name = g.Str(g.U32(p)) ?? "<null>";
            if (!r.ContainsKey(name)) r[name] = g.U32(p + 4);
        }
        return r;
    }

    static Dictionary<string, string> SpriteDims(GmFile g) {
        var r = new Dictionary<string, string>();
        foreach (long p in g.PtrList("SPRT")) {
            if (p == 0 || p + 12 > g.Size) continue;
            string name = g.Str(g.U32(p)) ?? "<null>";
            if (!r.ContainsKey(name)) r[name] = g.U32(p + 4) + "x" + g.U32(p + 8);
        }
        return r;
    }

    static List<string> Functions(GmFile g) {
        var r = new List<string>();
        Chunk c;
        if (!g.By.TryGetValue("FUNC", out c) || c.Len < 4) return r;
        long count = g.U32(c.Off);
        if (count * 12 + 4 > c.Len) return r;
        for (long k = 0; k < count; k++) r.Add(g.Str(g.U32(c.Off + 4 + 12 * k)) ?? "<null>");
        return r;
    }

    static string Key(GmFile g) {
        string disp = "", name = "";
        try { Chunk c = g.By["GEN8"]; disp = g.Str(g.U32(c.Off + 100)) ?? ""; name = g.Str(g.U32(c.Off + 40)) ?? ""; } catch (Exception) { }
        Match m = ChapterNo.Match(disp);
        if (!m.Success) m = ChapterNo.Match(name);
        if (!m.Success) m = ChapterNo.Match(g.Rel);
        if (m.Success) return "chapter " + m.Groups[1].Value;
        return (disp != "" ? disp : name).ToLowerInvariant();
    }

    static void Describe(GmFile g, string md5) {
        W.WriteLine();
        W.WriteLine("### " + g.Rel);
        W.WriteLine("  size " + N(g.Size) + "  md5 " + md5 + "  FORM says " + N(g.FormSize + 8) + (g.Truncated ? "   !! TRUNCATED FILE (incomplete copy?)" : ""));
        W.WriteLine("  chunks: " + string.Join(" ", g.Chunks.Select(c => c.Name + ":" + N(c.Len))));
        if (!g.By.ContainsKey("GEN8")) { W.WriteLine("  (no GEN8: an audio group or another kind of data file)"); return; }
        Try("GEN8", () => {
            long o = g.By["GEN8"].Off;
            byte[] h = g.Bytes(o, 4);
            W.WriteLine("  GEN8: bytecode " + h[1] + ", debugger disabled " + h[0] + ", name \"" + g.Str(g.U32(o + 40)) + "\", display name \"" +
                        g.Str(g.U32(o + 100)) + "\", file name \"" + g.Str(g.U32(o + 4)) + "\", config \"" + g.Str(g.U32(o + 8)) + "\"");
            W.WriteLine("        version " + g.U32(o + 44) + "." + g.U32(o + 48) + "." + g.U32(o + 52) + "." + g.U32(o + 56) + ", game id " + g.U32(o + 20) +
                        ", window " + g.U32(o + 60) + "x" + g.U32(o + 64) + ", info flags 0x" + g.U32(o + 68).ToString("x") +
                        ", steam app " + (int)g.U32(o + 120) + ", last object " + g.U32(o + 12) + ", last tile " + g.U32(o + 16));
            ulong ts = g.U32(o + 92) | ((ulong)g.U32(o + 96) << 32);
            W.WriteLine("        timestamp " + ts + " = " + new DateTime(1970, 1, 1).AddSeconds(Math.Min(ts, 4102444800UL)).ToString("yyyy-MM-dd HH:mm:ss") + " UTC" +
                        ", active targets 0x" + (g.U32(o + 104) | ((ulong)g.U32(o + 108) << 32)).ToString("x") +
                        ", function classes 0x" + (g.U32(o + 112) | ((ulong)g.U32(o + 116) << 32)).ToString("x") + ", rooms " + g.U32(o + 128));
        });
        long code = g.Len("CODE");
        W.WriteLine("  CODE: " + (code < 0 ? "no chunk" : code == 0 ? "EMPTY -> compiled game (YYC): the code is inside the executable"
                                                                    : N(code) + " bytes -> bytecode (VM): the code is in this file"));
        Try("lists", () => {
            var parts = new List<string>();
            foreach (string c in new[] { "SPRT", "BGND", "SOND", "OBJT", "ROOM", "SCPT", "CODE", "SHDR", "PATH", "TMLN", "TPAG", "AUDO", "FONT", "TXTR" })
                if (g.By.ContainsKey(c)) parts.Add(c + " " + g.PtrList(c).Count);
            W.WriteLine("  counts: " + string.Join(", ", parts) + ", FUNC " + Functions(g).Count + ", STRG " + g.StringCount);
        });
        Try("AGRP", () => { var a = g.Names("AGRP"); if (a.Count > 0) W.WriteLine("  audio groups: " + string.Join(", ", a)); });
        Try("TGIN", () => { var a = g.Names("TGIN", 0, 1); if (a.Count > 0) W.WriteLine("  texture groups: " + string.Join(", ", a.Take(60))); });
        Try("EXTN", () => {
            foreach (long p in g.PtrList("EXTN"))
                if (p != 0) W.WriteLine("  extension: \"" + g.Str(g.U32(p)) + "\" / \"" + g.Str(g.U32(p + 4)) + "\" / \"" + g.Str(g.U32(p + 8)) + "\"");
        });
        Try("LANG", () => {
            Chunk c;
            if (g.By.TryGetValue("LANG", out c) && c.Len >= 12)
                W.WriteLine("  LANG chunk: " + g.U32(c.Off) + " " + g.U32(c.Off + 4) + " " + g.U32(c.Off + 8));
        });
        Try("TXTR", () => Textures(g));
        Try("FONT", () => Fonts(g));
        Try("STRG", () => {
            int fr = 0, jp = 0;
            var hits = new List<string>();
            var plat = new List<string>();
            var frs = new List<string>();
            var seen = new HashSet<string>();
            foreach (string s in g.AllStrings()) {
                if (French.IsMatch(s)) { fr++; if (frs.Count < 8 && s.Length > 12) frs.Add(s); }
                if (Japanese.IsMatch(s)) jp++;
                if (hits.Count < 220 && s.Length < 200 && Interesting.IsMatch(s) && seen.Add(s)) hits.Add(s);
                else if (plat.Count < 140 && s.Length < 80 && Platform.IsMatch(s) && seen.Add(s)) plat.Add(s);
            }
            W.WriteLine("  STRG: " + N(g.StringCount) + " strings, " + N(fr) + " with French accents, " + N(jp) + " with Japanese");
            foreach (string s in frs) W.WriteLine("    fr> " + Cut(s));
            W.WriteLine("    strings worth a look (" + hits.Count + "):");
            foreach (string s in hits) W.WriteLine("      " + Cut(s));
            W.WriteLine("    platform names (" + plat.Count + "): " + string.Join(" ", plat));
        });
        Try("FUNC", () => {
            var f = Functions(g).Where(s => Regex.IsMatch(s, @"psn|steam|switch|ps4|ps5|font_add|sprite_add|sprite_replace|json|ini_|file_|buffer_load|external|os_get|window_set|display_|texture|audio_create|video")).ToList();
            W.WriteLine("  functions worth a look (" + f.Count + "): " + string.Join(" ", f.Take(220)));
        });
    }

    // ---------------------------------------------------------------------------------------------------------------
    // PC against PS4

    static void DiffNames(string label, ICollection<string> a, ICollection<string> b, int show = 40) {
        var sa = new HashSet<string>(a);
        var sb = new HashSet<string>(b);
        var onlyA = sa.Where(x => !sb.Contains(x)).ToList();
        var onlyB = sb.Where(x => !sa.Contains(x)).ToList();
        W.WriteLine("  " + label + ": PC " + N(sa.Count) + ", PS4 " + N(sb.Count) + ", only PC " + N(onlyA.Count) + ", only PS4 " + N(onlyB.Count));
        if (onlyA.Count > 0) W.WriteLine("      only PC:  " + string.Join(" | ", onlyA.Take(show).Select(s => Cut(s, 70))));
        if (onlyB.Count > 0) W.WriteLine("      only PS4: " + string.Join(" | ", onlyB.Take(show).Select(s => Cut(s, 70))));
    }

    static void Pair(GmFile pc, GmFile ps4) {
        W.WriteLine();
        W.WriteLine("### PC " + pc.Rel + "   <->   PS4 " + ps4.Rel);
        W.WriteLine("  chunks only PC: " + string.Join(" ", pc.Chunks.Select(c => c.Name).Where(n => !ps4.By.ContainsKey(n))) +
                    "   only PS4: " + string.Join(" ", ps4.Chunks.Select(c => c.Name).Where(n => !pc.By.ContainsKey(n))));
        foreach (string c in new[] { "SPRT", "BGND", "FONT", "SOND", "OBJT", "ROOM", "SCPT", "SHDR", "PATH", "TMLN" }) {
            string cc = c;
            Try(cc, () => DiffNames(cc, pc.Names(cc), ps4.Names(cc)));
        }
        Try("sprite sizes", () => {
            var a = SpriteDims(pc);
            var b = SpriteDims(ps4);
            var diff = a.Where(kv => b.ContainsKey(kv.Key) && b[kv.Key] != kv.Value).ToList();
            W.WriteLine("  sprites in both with another size: " + N(diff.Count));
            foreach (var kv in diff.Take(80)) W.WriteLine("      " + kv.Key + "  PC " + kv.Value + "  PS4 " + b[kv.Key]);
        });
        Try("TGIN", () => DiffNames("texture groups", pc.Names("TGIN", 0, 1), ps4.Names("TGIN", 0, 1)));
        Try("FUNC", () => DiffNames("functions", Functions(pc), Functions(ps4), 300));
        Try("CODE", () => {
            var a = CodeLengths(pc);
            var b = CodeLengths(ps4);
            DiffNames("code entries", a.Keys, b.Keys, 60);
            var diff = a.Where(kv => b.ContainsKey(kv.Key) && b[kv.Key] != kv.Value).ToList();
            W.WriteLine("  code entries in both: " + N(a.Keys.Count(k => b.ContainsKey(k))) + ", with a different length: " + N(diff.Count));
            foreach (var kv in diff.Take(60)) W.WriteLine("      " + kv.Key + "  PC " + kv.Value + "  PS4 " + b[kv.Key]);
        });
        Try("STRG", () => {
            var a = new HashSet<string>(pc.AllStrings());
            var b = new HashSet<string>(ps4.AllStrings());
            var onlyA = a.Where(x => !b.Contains(x)).ToList();
            var onlyB = b.Where(x => !a.Contains(x)).ToList();
            W.WriteLine("  strings: PC " + N(a.Count) + " distinct, PS4 " + N(b.Count) + ", only PC " + N(onlyA.Count) + ", only PS4 " + N(onlyB.Count));
            foreach (string s in onlyA.Where(x => x.Length > 3).Take(25)) W.WriteLine("      only PC>  " + Cut(s, 100));
            foreach (string s in onlyB.Where(x => x.Length > 3).Take(25)) W.WriteLine("      only PS4> " + Cut(s, 100));
            var plainA = onlyA.Where(x => !French.IsMatch(x) && !x.Contains(" ") && x.Length > 3).ToList();
            var plainB = onlyB.Where(x => !x.Contains(" ") && x.Length > 3).ToList();
            W.WriteLine("      one-word strings only PC (" + plainA.Count + "): " + string.Join(" ", plainA.Take(150).Select(s => Cut(s, 50))));
            W.WriteLine("      one-word strings only PS4 (" + plainB.Count + "): " + string.Join(" ", plainB.Take(150).Select(s => Cut(s, 50))));
        });
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Language files: flat JSON objects of "key": "text"

    static Dictionary<string, string> FlatJson(string text) {
        var r = new Dictionary<string, string>();
        int i = 0, n = text.Length;
        Func<string> str = () => {
            var sb = new StringBuilder();
            i++;
            while (i < n && text[i] != '"') {
                if (text[i] == '\\' && i + 1 < n) {
                    char c = text[++i];
                    if (c == 'n') sb.Append('\n');
                    else if (c == 't') sb.Append('\t');
                    else if (c == 'r') sb.Append('\r');
                    else if (c == 'u' && i + 4 < n) { sb.Append((char)Convert.ToInt32(text.Substring(i + 1, 4), 16)); i += 4; }
                    else sb.Append(c);
                }
                else sb.Append(text[i]);
                i++;
            }
            i++;
            return sb.ToString();
        };
        while (i < n) {
            if (text[i] != '"') { i++; continue; }
            string key = str();
            while (i < n && char.IsWhiteSpace(text[i])) i++;
            if (i >= n || text[i] != ':') continue;
            i++;
            while (i < n && char.IsWhiteSpace(text[i])) i++;
            if (i < n && text[i] == '"') r[key] = str();
            else {
                int s = i;
                while (i < n && text[i] != ',' && text[i] != '}' && text[i] != '{' && text[i] != '[') i++;
                r[key] = text.Substring(s, i - s).Trim();
            }
        }
        return r;
    }

    static Dictionary<string, string> LangFile(Entry e, string side) {
        W.WriteLine();
        W.WriteLine("### " + side + " " + e.Rel + "  (" + N(e.Size) + " bytes, md5 " + Md5(e.Full) + ")");
        if (e.Size > (200 << 20)) { W.WriteLine("  too big to read"); return null; }
        var d = FlatJson(File.ReadAllText(e.Full, Encoding.UTF8));
        int fr = d.Values.Count(v => French.IsMatch(v)), jp = d.Values.Count(v => Japanese.IsMatch(v));
        W.WriteLine("  " + N(d.Count) + " keys, " + N(fr) + " texts with French accents, " + N(jp) + " with Japanese" +
                    (d.ContainsKey("date") ? ", date \"" + Cut(d["date"], 40) + "\"" : ""));
        foreach (var kv in d.Where(kv => kv.Value.Length > 15).Take(4)) W.WriteLine("    " + Cut(kv.Key, 60) + " = " + Cut(kv.Value, 90));
        return d;
    }

    static string LangKey(Entry e) {
        Match m = ChapterNo.Match(e.Rel);
        return (m.Success ? "chapter " + m.Groups[1].Value : "root") + "/" + Path.GetFileName(e.Rel).ToLowerInvariant();
    }

    // ---------------------------------------------------------------------------------------------------------------

    static bool IsForm(Entry e) {
        if (e.Size < 16) return false;
        try {
            using (var f = new FileStream(e.Full, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                var h = new byte[4];
                return f.Read(h, 0, 4) == 4 && h[0] == 'F' && h[1] == 'O' && h[2] == 'R' && h[3] == 'M';
            }
        }
        catch (Exception) { return false; }
    }

    static List<GmFile> Side(string label, string root, List<Entry> all, out Dictionary<string, Dictionary<string, string>> lang) {
        var games = new List<GmFile>();
        lang = new Dictionary<string, Dictionary<string, string>>();
        W.WriteLine();
        W.WriteLine("==================== " + label + ": FILES ====================");
        W.WriteLine("  root: " + root);
        Try("tree", () => Tree(all));

        W.WriteLine();
        W.WriteLine("==================== " + label + ": SMALL FILES ====================");
        foreach (var e in all.Where(x => Path.GetFileName(x.Rel).ToLowerInvariant() == "param.sfo")) { var ee = e; Try(e.Rel, () => Sfo(ee)); }
        foreach (var e in all.Where(x => x.Size < (1 << 16) && Regex.IsMatch(Path.GetExtension(x.Rel), @"^\.(ini|txt|cfg|xml|yml|gp4)$", RegexOptions.IgnoreCase)).Take(40)) {
            var ee = e;
            Try(e.Rel, () => {
                W.WriteLine("  --- " + ee.Rel + " (" + N(ee.Size) + " bytes)");
                foreach (string l in File.ReadAllLines(ee.Full).Take(60)) W.WriteLine("      " + Cut(l, 160));
            });
        }
        foreach (var e in all.Where(x => Regex.IsMatch(Path.GetFileName(x.Rel), @"^(eboot\.bin|.*\.exe|.*\.prx|.*\.sprx)$", RegexOptions.IgnoreCase)).Take(40)) {
            var ee = e;
            Try(e.Rel, () => {
                using (var f = new FileStream(ee.Full, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                    var h = new byte[8];
                    int n = f.Read(h, 0, 8);
                    W.WriteLine("  " + ee.Rel + "  " + N(ee.Size) + " bytes, starts with " + BitConverter.ToString(h, 0, n));
                }
            });
        }

        W.WriteLine();
        W.WriteLine("==================== " + label + ": GAMEMAKER DATA FILES ====================");
        foreach (var e in all.Where(IsForm)) {
            var ee = e;
            Status(label + " : " + e.Rel);
            Try(e.Rel, () => {
                var g = new GmFile(ee.Full, ee.Rel);
                bool game = g.By.ContainsKey("GEN8");
                Describe(g, Md5(ee.Full));
                if (game) games.Add(g); else g.Dispose();
            });
        }
        if (games.Count == 0) W.WriteLine("  !! NO GAME DATA FILE FOUND UNDER THIS FOLDER");

        W.WriteLine();
        W.WriteLine("==================== " + label + ": LANGUAGE FILES ====================");
        foreach (var e in all.Where(x => x.Rel.ToLowerInvariant().EndsWith(".json"))) {
            var ee = e;
            Status(label + " : " + e.Rel);
            var l = lang;
            Try(e.Rel, () => { var d = LangFile(ee, label); if (d != null) l[LangKey(ee)] = d; });
        }
        return games;
    }

    public static void Run(string ps4, string pc, string report, Action<string> status) {
        Status = status ?? (s => { });
        Ps4Data = PcData = 0;
        using (W = new StreamWriter(report, false, new UTF8Encoding(true))) {
            W.WriteLine("DeltaruneScan " + Version + "   " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            W.WriteLine("PS4: " + ps4);
            W.WriteLine("PC:  " + pc);
            Status("Lecture des dossiers...");
            var a = Walk(ps4);
            var b = Walk(pc);
            W.WriteLine("PS4 top level: " + string.Join(", ", a.Select(e => e.Rel.Split('/')[0]).Distinct().Take(40)));
            W.WriteLine("PC top level:  " + string.Join(", ", b.Select(e => e.Rel.Split('/')[0]).Distinct().Take(60)));
            Dictionary<string, Dictionary<string, string>> la, lb;
            var ga = Side("PS4", ps4, a, out la);
            var gb = Side("PC", pc, b, out lb);
            Ps4Data = ga.Count;
            PcData = gb.Count;

            W.WriteLine();
            W.WriteLine("==================== PC AGAINST PS4: DATA FILES ====================");
            var used = new HashSet<GmFile>();
            foreach (var p in gb) {
                string k = Key(p);
                var match = ga.Where(x => !used.Contains(x) && Key(x) == k).ToList();
                W.WriteLine();
                W.WriteLine("PC " + p.Rel + " is \"" + k + "\": " + (match.Count == 0 ? "no PS4 file with that name" : match.Count + " PS4 file(s)"));
                foreach (var m in match) {
                    var pp = p; var mm = m;
                    used.Add(m);
                    Status("Comparaison : " + k);
                    Try("pair", () => Pair(pp, mm));
                }
            }
            foreach (var m in ga.Where(x => !used.Contains(x))) W.WriteLine("PS4 " + m.Rel + " is \"" + Key(m) + "\": no PC file with that name");

            W.WriteLine();
            W.WriteLine("==================== PC AGAINST PS4: LANGUAGE FILES ====================");
            foreach (var kv in lb) {
                Dictionary<string, string> other;
                if (!la.TryGetValue(kv.Key, out other)) { W.WriteLine("  " + kv.Key + ": PC only"); continue; }
                int onlyPc = kv.Value.Keys.Count(x => !other.ContainsKey(x)), onlyPs4 = other.Keys.Count(x => !kv.Value.ContainsKey(x));
                int diff = kv.Value.Count(x => other.ContainsKey(x.Key) && other[x.Key] != x.Value);
                W.WriteLine("  " + kv.Key + ": PC " + N(kv.Value.Count) + " keys, PS4 " + N(other.Count) + ", only PC " + N(onlyPc) + ", only PS4 " + N(onlyPs4) +
                            ", same key with another text " + N(diff));
                foreach (var x in kv.Value.Where(x => other.ContainsKey(x.Key) && other[x.Key] != x.Value).Take(3))
                    W.WriteLine("      " + Cut(x.Key, 50) + ": PC \"" + Cut(x.Value, 60) + "\"  PS4 \"" + Cut(other[x.Key], 60) + "\"");
                foreach (string x in other.Keys.Where(x => !kv.Value.ContainsKey(x)).Take(5)) W.WriteLine("      only PS4: " + Cut(x, 70) + " = " + Cut(other[x], 60));
                foreach (string x in kv.Value.Keys.Where(x => !other.ContainsKey(x)).Take(5)) W.WriteLine("      only PC:  " + Cut(x, 70) + " = " + Cut(kv.Value[x], 60));
            }
            foreach (string k in la.Keys.Where(x => !lb.ContainsKey(x))) W.WriteLine("  " + k + ": PS4 only");
            foreach (var g in ga.Concat(gb)) g.Dispose();
            W.WriteLine();
            W.WriteLine("END OF REPORT");
        }
    }
}

class ScanForm : Form {
    readonly Label label;
    readonly BackgroundWorker worker = new BackgroundWorker { WorkerReportsProgress = true };
    public Exception Error;

    public ScanForm(string ps4, string pc, string report) {
        Text = "DeltaruneScan";
        ClientSize = new Size(560, 110);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(new Label { Text = "Analyse en cours (quelques minutes, le temps de lire les gros fichiers)...", Location = new Point(14, 14), Size = new Size(532, 20) });
        Controls.Add(new ProgressBar { Style = ProgressBarStyle.Marquee, Location = new Point(14, 40), Size = new Size(532, 20) });
        label = new Label { Location = new Point(14, 70), Size = new Size(532, 30), AutoEllipsis = true };
        Controls.Add(label);
        worker.DoWork += (s, e) => Scanner.Run(ps4, pc, report, t => worker.ReportProgress(0, t));
        worker.ProgressChanged += (s, e) => label.Text = (string)e.UserState;
        worker.RunWorkerCompleted += (s, e) => { Error = e.Error; Close(); };
        Shown += (s, e) => worker.RunWorkerAsync();
        FormClosing += (s, e) => { if (worker.IsBusy) e.Cancel = true; };
    }
}

static class Program {
    static string Pick(string text) {
        using (var d = new FolderBrowserDialog { Description = text, ShowNewFolderButton = false })
            return d.ShowDialog() == DialogResult.OK ? d.SelectedPath : null;
    }

    [STAThread]
    static int Main(string[] args) {
        if (args.Length >= 2) {
            string outp = args.Length >= 3 ? args[2] : "deltarune-rapport.txt";
            Scanner.Run(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]), outp, null);
            return 0;
        }
        Application.EnableVisualStyles();
        if (MessageBox.Show(
                "Cet outil lit tes fichiers DELTARUNE PS4 (copiés par FTP) et ton jeu PC patché en français, puis écrit un rapport texte " +
                "(deltarune-rapport.txt), utile pour diagnostiquer un problème.\n\nIl ne modifie rien et n'envoie rien sur Internet.\n\n" +
                "Tu vas choisir deux dossiers :\n  1. celui où tu as copié les fichiers PS4 (il contient CUSAxxxxx-app0),\n" +
                "  2. celui du jeu PC en français (il contient DELTARUNE.exe).",
                "DeltaruneScan", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return 1;
        string ps4 = Pick("1/2 - Le dossier où tu as copié les fichiers PS4 (celui qui contient CUSAxxxxx-app0)");
        if (ps4 == null) return 1;
        string pc = Pick("2/2 - Le dossier du jeu PC patché en français (celui qui contient DELTARUNE.exe)");
        if (pc == null) return 1;
        string dir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
        string report = Path.Combine(dir, "deltarune-rapport.txt");
        try { File.WriteAllText(report, ""); }
        catch (Exception) { report = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "deltarune-rapport.txt"); }
        var form = new ScanForm(ps4, pc, report);
        Application.Run(form);
        if (form.Error != null) {
            MessageBox.Show("L'analyse s'est arrêtée sur une erreur :\n\n" + form.Error + "\n\nJoins ce message si tu signales le problème.",
                            "DeltaruneScan", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }
        string warn = "";
        if (Scanner.Ps4Data == 0) warn += "\n\nATTENTION : aucun fichier de données du jeu trouvé dans le dossier PS4. Vérifie que la copie FTP est complète.";
        if (Scanner.PcData == 0) warn += "\n\nATTENTION : aucun fichier de données du jeu trouvé dans le dossier PC. Vérifie que c'est bien le dossier de DELTARUNE.exe.";
        MessageBox.Show("Rapport écrit :\n" + report + "\n\nFichiers de données trouvés : " + Scanner.Ps4Data + " côté PS4, " + Scanner.PcData +
                        " côté PC." + warn + "", "DeltaruneScan", MessageBoxButtons.OK,
                        warn == "" ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        try { Process.Start("explorer.exe", "/select,\"" + report + "\""); } catch (Exception) { }
        return 0;
    }
}
