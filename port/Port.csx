// Port.csx - French patch of DELTARUNE (PC) to the PS4 version. Run by UndertaleModCli (UndertaleModTool 0.9.x):
//   UndertaleModCli load <any data.win> -s Port.csx      with DRFR_PS4, DRFR_PC and DRFR_OUT set
//
// For the chapter select and for each chapter there are three data files:
//   PC-EN   <pc>/chapterN_windows/data.win.bak    the original PC file (kept by the French patch's installer)
//   PC-FR   <pc>/chapterN_windows/data.win        the same file with the French patch
//   PS4-EN  <ps4>/CUSA*-patch0/games/chapterN_ps4/game.win (or -app0)   the PS4 file of the same build
// PC-EN and PS4-EN are the same build of the game compiled for two targets: they differ only in what is specific to
// the platform (configuration name, a few constants in the code, extensions, compiled shaders). This script finds that
// difference and applies it to PC-FR, which gives the PS4 file in French. Nothing here is specific to one version of
// the game: everything is found by comparing, and everything found is written to the log.
//
// Output: <out>/<TITLEID>/games/... (the layout GoldHEN's AFR plugin expects under /data/GoldHEN/AFR) and
// <out>/deltarune-fr-ps4-journal.txt.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Util;

string ps4Root = Environment.GetEnvironmentVariable("DRFR_PS4");
string pcRoot = Environment.GetEnvironmentVariable("DRFR_PC");
string outRoot = Environment.GetEnvironmentVariable("DRFR_OUT");
bool recheck = Environment.GetEnvironmentVariable("DRFR_NO_RECHECK") != "1";
if (string.IsNullOrEmpty(ps4Root) || string.IsNullOrEmpty(pcRoot) || string.IsNullOrEmpty(outRoot))
    throw new Exception("DRFR_PS4, DRFR_PC and DRFR_OUT must be set");

try { Console.OutputEncoding = new UTF8Encoding(false); } catch (Exception) { }
Directory.CreateDirectory(outRoot);
var logW = new StreamWriter(Path.Combine(outRoot, "deltarune-fr-ps4-journal.txt"), false, new UTF8Encoding(true));
int problems = 0, warnings = 0;
void Log(string s) { Console.WriteLine(s); logW.WriteLine(s); logW.Flush(); }
void Problem(string s) { problems++; Log("  !! " + s); }
void Step(string s) { Console.WriteLine("@@step " + s); logW.WriteLine(); logW.WriteLine("==== " + s + " ===="); logW.Flush(); }
string Cut(string s, int n = 90) { if (s == null) return "<null>"; s = s.Replace("\r", "\\r").Replace("\n", "\\n"); return s.Length <= n ? s : s.Substring(0, n) + "..."; }
string N(long v) { return v.ToString("N0", System.Globalization.CultureInfo.InvariantCulture); }

// ---------------------------------------------------------------------------------------------------------------------
// What is taken from the PS4 file

class RefInfo { public char Kind; public string Str, Fn, VarName; public UndertaleInstruction.InstanceType VarInst; public int VarId; }
class PIns { public UndertaleInstruction Ins; public RefInfo Ref; }
class CodeDelta {
    public string Name;
    public bool Structural;
    public string[] PcLoose;                                     // PC-EN, every instruction, strings not compared
    public Dictionary<int, string> PcStr = new Dictionary<int, string>();   // PC-EN text of the string instructions that differ
    public List<int> Diff = new List<int>();                     // instructions that differ (same count on both sides)
    public Dictionary<int, PIns> Ps4 = new Dictionary<int, PIns>();
    public List<PIns> Ps4All;                                    // structural: the whole PS4 entry
    public Dictionary<string, uint> Ps4ChildOffsets = new Dictionary<string, uint>();
}
// Textures only the PS4 file has (the texture group for consoles: the screen borders). The PC file has the same sprites
// with no image behind them.
class PageDelta { public uint Scaled, GeneratedMips; public int W, H, IndexInGroup; public GMImage Image; }
class ItemDelta { public char Kind; public string Owner; public int Frame, Page; public ushort[] V; }
class GroupDelta {
    public string Name, Directory, Extension; public UndertaleTextureGroupInfo.TextureGroupLoadType LoadType; public int Index;
    public List<int> Pages = new List<int>(); public List<string> Sprites = new List<string>(), Fonts = new List<string>(), Tilesets = new List<string>();
}
class ShaderDelta { public string Name; public UndertaleShader.ShaderType Type; public int Version; public byte[][] Raw; public bool[] Null; public List<string> Attr; }
class Delta {
    public string Config;
    public UndertaleGeneralInfo.InfoFlags Info;
    public UndertaleOptions.OptionsFlags OptInfo;
    public int OptScale, OptShaderFlag, OptShaderVer;
    public uint[] OptVals;
    public List<KeyValuePair<string, string>> OptConstants = new List<KeyValuePair<string, string>>();
    public bool OptionsDiffer;
    public List<CodeDelta> Code = new List<CodeDelta>();
    public List<ShaderDelta> Shaders = new List<ShaderDelta>();
    public string DisplayName; public bool DisplayNameDiffers;
    public HashSet<string> Ps4Extensions = new HashSet<string>();
    public Dictionary<string, byte[]> Ps4ProductIds = new Dictionary<string, byte[]>();   // EXTN: 16 bytes for each extension
    public HashSet<string> PcOnlyRoots = new HashSet<string>();
    public Dictionary<string, int> PcCodeCount = new Dictionary<string, int>();
    public List<PageDelta> Pages = new List<PageDelta>();
    public List<ItemDelta> Items = new List<ItemDelta>();
    public List<GroupDelta> Groups = new List<GroupDelta>();
}

MethodInfo cloneM = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);
UndertaleInstruction CloneIns(UndertaleInstruction i) { return (UndertaleInstruction)cloneM.Invoke(i, null); }

UndertaleData Load(string path) {
    Log("  lecture de " + path + " (" + N(new FileInfo(path).Length) + " octets)");
    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        return UndertaleIO.Read(fs, (w, important) => { if (important) { warnings++; Log("     avertissement : " + w); } }, m => { });
}

void AppendVar(StringBuilder sb, UndertaleVariable v) {
    if (v == null) { sb.Append("<null>"); return; }
    sb.Append(v.Name?.Content).Append('@').Append((int)v.InstanceType);
}

/// One instruction as text, the same for the same instruction in two data files. full = false: the text of strings is
/// left out (the French patch translates strings in place). ren: the other file's name for a function.
string Sig(UndertaleInstruction i, bool full, Dictionary<string, string> ren) {
    var sb = new StringBuilder(40);
    sb.Append((int)i.Kind).Append(':');
    Func<UndertaleFunction, string> fn = f => {
        string n = f?.Name?.Content ?? "<null>";
        string r;
        return ren != null && ren.TryGetValue(n, out r) ? r : n;
    };
    switch (UndertaleInstruction.GetInstructionType(i.Kind)) {
        case UndertaleInstruction.InstructionType.GotoInstruction:
            sb.Append(i.JumpOffsetPopenvExitMagic ? "X" : i.JumpOffset.ToString());
            break;
        case UndertaleInstruction.InstructionType.PopInstruction:
            sb.Append((int)i.Type1).Append('.').Append((int)i.Type2).Append('.');
            if (i.Type1 == UndertaleInstruction.DataType.Int16) sb.Append(i.SwapExtra);
            else { sb.Append((short)i.TypeInst).Append('.').Append((int)i.ReferenceType).Append('.'); AppendVar(sb, i.ValueVariable); }
            break;
        case UndertaleInstruction.InstructionType.PushInstruction:
            sb.Append((int)i.Type1).Append('.').Append((int)i.Type2).Append('.');
            switch (i.Type1) {
                case UndertaleInstruction.DataType.Variable:
                    sb.Append((short)i.TypeInst).Append('.').Append((int)i.ReferenceType).Append('.');
                    AppendVar(sb, i.ValueVariable);
                    break;
                case UndertaleInstruction.DataType.Int32:
                    if (i.ValueFunction != null) sb.Append("F:").Append(fn(i.ValueFunction));
                    else if (i.ValueVariable != null) { sb.Append("V:"); AppendVar(sb, i.ValueVariable); }
                    else sb.Append(i.ValueInt);
                    break;
                case UndertaleInstruction.DataType.String:
                    sb.Append("S:");
                    if (full) sb.Append(i.ValueString?.Resource?.Content);
                    break;
                case UndertaleInstruction.DataType.Int16: sb.Append(i.ValueShort); break;
                case UndertaleInstruction.DataType.Double: sb.Append(BitConverter.DoubleToInt64Bits(i.ValueDouble)); break;
                case UndertaleInstruction.DataType.Int64: sb.Append(i.ValueLong); break;
                default: sb.Append(i.ValueShort).Append('/').Append(i.ValueLong); break;
            }
            break;
        case UndertaleInstruction.InstructionType.CallInstruction:
            sb.Append((int)i.Type1).Append('.').Append(i.ArgumentsCount).Append(".F:").Append(fn(i.ValueFunction));
            break;
        case UndertaleInstruction.InstructionType.BreakInstruction:
            sb.Append((int)i.Type1).Append('.').Append(i.ExtendedKind).Append('.');
            if (i.Type1 == UndertaleInstruction.DataType.Int32) {
                if (i.ValueFunction != null) sb.Append("F:").Append(fn(i.ValueFunction));
                else sb.Append(i.IntArgument);
            }
            break;
        default:
            sb.Append((int)i.Type1).Append('.').Append((int)i.Type2).Append('.').Append(i.ValueShort);
            break;
    }
    return sb.ToString();
}

PIns Detach(UndertaleInstruction i) {
    var p = new PIns { Ins = CloneIns(i) };
    if (i.ValueString != null) p.Ref = new RefInfo { Kind = 'S', Str = i.ValueString.Resource?.Content };
    else if (i.ValueFunction != null) p.Ref = new RefInfo { Kind = 'F', Fn = i.ValueFunction.Name?.Content };
    else if (i.ValueVariable != null)
        p.Ref = new RefInfo { Kind = 'V', VarName = i.ValueVariable.Name?.Content, VarInst = i.ValueVariable.InstanceType, VarId = i.ValueVariable.VarID };
    if (p.Ref != null) p.Ins.ValueString = null;   // the copy keeps nothing of the PS4 file's objects
    return p;
}

string Describe(UndertaleInstruction i) { return Sig(i, true, null); }

// The code that deals with the screen borders of the console versions, as text: every line that names a border, with
// the lines around it. Written for the PS4 original and for the French file, to compare them.
string unitLabel = "";
void DumpBorders(UndertaleData data, string suffix) {
    try {
        string dir = Path.Combine(outRoot, "code-fr");
        Directory.CreateDirectory(dir);
        var names = new HashSet<string> { "screen_border_id", "screen_border_active", "disable_border", "selected_border", "border_options", "screen_border_alpha", "screen_border_state" };
        var ctx = new GlobalDecompileContext(data);
        int entries = 0;
        using (var w = new StreamWriter(Path.Combine(dir, unitLabel.Replace(' ', '-') + "-bordures-" + suffix + ".txt"), false, new UTF8Encoding(true))) {
            foreach (var c in data.Code) {
                if (c?.Name?.Content == null || c.ParentEntry != null) continue;
                bool hit = c.Name.Content.IndexOf("border", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!hit)
                    foreach (var i in c.Instructions) {
                        string v = i.ValueVariable?.Name?.Content ?? i.ValueFunction?.Name?.Content;
                        if (v != null && (names.Contains(v) || v.IndexOf("border", StringComparison.OrdinalIgnoreCase) >= 0)) { hit = true; break; }
                    }
                if (!hit) continue;
                string text;
                try { text = new Underanalyzer.Decompiler.DecompileContext(ctx, c, data.ToolInfo.DecompilerSettings).DecompileToString(); }
                catch (Exception e) { text = "// decompilation failed: " + e.Message; }
                var lines = text.Replace("\r", "").Split('\n');
                bool whole = c.Name.Content.IndexOf("border", StringComparison.OrdinalIgnoreCase) >= 0 && lines.Length <= 500;
                w.WriteLine("//////// " + c.Name.Content + "  (" + lines.Length + " lignes)");
                int last = -1;
                for (int k = 0; k < lines.Length; k++) {
                    if (!whole && lines[k].IndexOf("border", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    int from = whole ? k : Math.Max(last + 1, k - 5), to = whole ? k : Math.Min(lines.Length - 1, k + 5);
                    if (!whole && from > last + 1) w.WriteLine("    ...");
                    for (int j = from; j <= to; j++) w.WriteLine((j + 1).ToString().PadLeft(5) + ": " + lines[j]);
                    last = to;
                    k = to;
                }
                w.WriteLine();
                entries++;
            }
        }
        Log("  code-fr : bordures, " + entries + " morceaux de code (" + suffix + ")");
    }
    catch (Exception e) { Log("  code-fr : bordures non écrites (" + e.Message + ")"); }
}

Delta Compare(UndertaleData pc, UndertaleData ps4) {
    var d = new Delta();

    // GEN8 and OPTN
    var g = pc.GeneralInfo; var h = ps4.GeneralInfo;
    d.Config = h.Config?.Content;
    d.Info = h.Info;
    d.DisplayName = h.DisplayName?.Content;
    d.DisplayNameDiffers = g.DisplayName?.Content != h.DisplayName?.Content;
    Log("  GEN8 : config PC \"" + g.Config?.Content + "\" PS4 \"" + h.Config?.Content + "\" ; info PC " + g.Info + " | PS4 " + h.Info);
    if (g.BytecodeVersion != h.BytecodeVersion) Problem("version du bytecode différente : PC " + g.BytecodeVersion + ", PS4 " + h.BytecodeVersion);
    Action<string, object, object> field = (n, a, b) => { if (!Equals(a, b)) Log("  GEN8." + n + " : PC " + a + " | PS4 " + b); };
    field("FileName", g.FileName?.Content, h.FileName?.Content); field("Name", g.Name?.Content, h.Name?.Content);
    field("DisplayName", g.DisplayName?.Content, h.DisplayName?.Content); field("GameID", g.GameID, h.GameID);
    field("LastObj", g.LastObj, h.LastObj); field("LastTile", g.LastTile, h.LastTile);
    field("Window", g.DefaultWindowWidth + "x" + g.DefaultWindowHeight, h.DefaultWindowWidth + "x" + h.DefaultWindowHeight);
    field("ActiveTargets", g.ActiveTargets, h.ActiveTargets); field("FunctionClassifications", g.FunctionClassifications, h.FunctionClassifications);
    field("SteamAppID", g.SteamAppID, h.SteamAppID); field("DebuggerPort", g.DebuggerPort, h.DebuggerPort);
    field("GMS2FPS", g.GMS2FPS, h.GMS2FPS); field("GMS2AllowStatistics", g.GMS2AllowStatistics, h.GMS2AllowStatistics);
    field("Timestamp", g.Timestamp, h.Timestamp); field("Rooms", g.RoomOrder.Count, h.RoomOrder.Count);
    Log("  version GameMaker détectée : PC " + pc.GeneralInfo.Major + "." + pc.GeneralInfo.Minor + "." + pc.GeneralInfo.Release + "." + pc.GeneralInfo.Build +
        " | PS4 " + ps4.GeneralInfo.Major + "." + ps4.GeneralInfo.Minor + "." + ps4.GeneralInfo.Release + "." + ps4.GeneralInfo.Build);

    var o = pc.Options; var q = ps4.Options;
    Func<UndertaleOptions, uint[]> vals = x => new[] { x.WindowColor, x.ColorDepth, x.Resolution, x.Frequency, x.VertexSync, x.Priority, x.LoadAlpha };
    Func<UndertaleOptions, string> cons = x => string.Join(" ; ", x.Constants.Select(c => c.Name?.Content + "=" + c.Value?.Content));
    d.OptInfo = q.Info; d.OptScale = q.Scale; d.OptShaderFlag = q.ShaderExtensionFlag; d.OptShaderVer = q.ShaderExtensionVersion; d.OptVals = vals(q);
    foreach (var c in q.Constants) d.OptConstants.Add(new KeyValuePair<string, string>(c.Name?.Content, c.Value?.Content));
    d.OptionsDiffer = o.Info != q.Info || o.Scale != q.Scale || o.ShaderExtensionFlag != q.ShaderExtensionFlag || o.ShaderExtensionVersion != q.ShaderExtensionVersion ||
                      !vals(o).SequenceEqual(vals(q)) || cons(o) != cons(q);
    if (d.OptionsDiffer) {
        Log("  OPTN : PC " + o.Info + " scale " + o.Scale + " [" + string.Join(",", vals(o)) + "] {" + cons(o) + "}");
        Log("         PS4 " + q.Info + " scale " + q.Scale + " [" + string.Join(",", vals(q)) + "] {" + cons(q) + "}");
    }
    else Log("  OPTN : identique");

    // Extensions
    foreach (var e in ps4.Extensions) d.Ps4Extensions.Add(e.Name?.Content ?? "");
    var ps4Ids = ps4.FORM.EXTN?.productIdData;
    if (ps4Ids != null && ps4Ids.Count == ps4.Extensions.Count)
        for (int k = 0; k < ps4Ids.Count; k++) d.Ps4ProductIds[ps4.Extensions[k].Name?.Content ?? ""] = (byte[])ps4Ids[k].Clone();
    var pcIds = pc.FORM.EXTN?.productIdData;
    Log("  extensions : identifiants (16 octets chacun) PC " + (pcIds?.Count ?? 0) + " | PS4 " + (ps4Ids?.Count ?? 0));
    var pcExt = pc.Extensions.Select(e => e.Name?.Content ?? "").ToList();
    Log("  extensions : PC [" + string.Join(", ", pcExt) + "] | PS4 [" + string.Join(", ", d.Ps4Extensions) + "]");
    foreach (string n in d.Ps4Extensions.Where(n => !pcExt.Contains(n))) Problem("extension présente seulement sur PS4 : " + n);
    foreach (var e in ps4.Extensions) {
        var p = pc.Extensions.FirstOrDefault(x => x.Name?.Content == e.Name?.Content);
        if (p == null) continue;
        string a = string.Join(" ", p.Files.Select(f => f.Filename?.Content + "(" + f.Kind + "," + f.Functions.Count + ")"));
        string b = string.Join(" ", e.Files.Select(f => f.Filename?.Content + "(" + f.Kind + "," + f.Functions.Count + ")"));
        if (a != b) Log("  extension " + e.Name?.Content + " : fichiers PC " + a + " | PS4 " + b);
    }

    // Shaders
    Func<UndertaleShader, UndertaleShader.UndertaleRawShaderData[]> raw = s => new[] {
        s.HLSL11_VertexData, s.HLSL11_PixelData, s.PSSL_VertexData, s.PSSL_PixelData, s.Cg_PSVita_VertexData, s.Cg_PSVita_PixelData, s.Cg_PS3_VertexData, s.Cg_PS3_PixelData };
    Func<UndertaleShader, string> present = s => string.Join("", raw(s).Select(r => r == null || r.IsNull ? "-" : "X"));
    int shSame = 0;
    foreach (var s in ps4.Shaders) {
        if (s == null) continue;
        var p = pc.Shaders.FirstOrDefault(x => x?.Name?.Content == s.Name?.Content);
        if (p == null) { Problem("shader présent seulement sur PS4 : " + s.Name?.Content); continue; }
        if (p.GLSL_ES_Vertex?.Content != s.GLSL_ES_Vertex?.Content || p.GLSL_ES_Fragment?.Content != s.GLSL_ES_Fragment?.Content)
            Log("  shader " + s.Name?.Content + " : source GLSL ES différente entre PC et PS4");
        else shSame++;
        d.Shaders.Add(new ShaderDelta {
            Name = s.Name?.Content, Type = s.Type, Version = s.Version,
            Raw = raw(s).Select(r => r == null || r.IsNull || r.Data == null ? null : (byte[])r.Data.Clone()).ToArray(),
            Null = raw(s).Select(r => r == null || r.IsNull).ToArray(),
            Attr = s.VertexShaderAttributes.Select(a => a.Name?.Content).ToList() });
    }
    if (ps4.Shaders.Count > 0) {
        var s0 = ps4.Shaders.First(x => x != null);
        var p0 = pc.Shaders.FirstOrDefault(x => x?.Name?.Content == s0.Name?.Content);
        Log("  shaders : " + ps4.Shaders.Count + " sur PS4, " + pc.Shaders.Count + " sur PC, " + shSame + " avec la même source ; données compilées " +
            "(HLSL11 v/p, PSSL v/p, Vita v/p, PS3 v/p) : PC " + (p0 == null ? "?" : present(p0) + " type " + p0.Type) + " | PS4 " + present(s0) + " type " + s0.Type);
    }
    foreach (var p in pc.Shaders.Where(x => x != null && !ps4.Shaders.Any(y => y?.Name?.Content == x.Name?.Content)))
        Problem("shader présent seulement sur PC : " + p.Name?.Content);

    // Code
    var pcCode = new Dictionary<string, UndertaleCode>();
    foreach (var c in pc.Code) if (c?.Name?.Content != null && !pcCode.ContainsKey(c.Name.Content)) pcCode[c.Name.Content] = c;
    foreach (var kv in pcCode) if (kv.Value.ParentEntry == null) d.PcCodeCount[kv.Key] = kv.Value.Instructions.Count;
    var ps4Code = new Dictionary<string, UndertaleCode>();
    foreach (var c in ps4.Code) if (c?.Name?.Content != null && !ps4Code.ContainsKey(c.Name.Content)) ps4Code[c.Name.Content] = c;
    var ren = new Dictionary<string, string>();       // PS4 name -> PC name
    foreach (var c in ps4.Code) {
        if (c?.Name?.Content == null || pcCode.ContainsKey(c.Name.Content) || c.ParentEntry == null) continue;
        UndertaleCode pp;
        if (!pcCode.TryGetValue(c.ParentEntry.Name.Content, out pp)) continue;
        var m = pp.ChildEntries.FirstOrDefault(x => x.Offset == c.Offset && !ps4Code.ContainsKey(x.Name.Content));
        if (m != null) { ren[c.Name.Content] = m.Name.Content; Log("  même fonction sous deux noms : PS4 " + c.Name.Content + " = PC " + m.Name.Content); }
    }
    foreach (var c in ps4.Code)
        if (c?.Name?.Content != null && !pcCode.ContainsKey(c.Name.Content) && !ren.ContainsKey(c.Name.Content))
            Problem("code présent seulement sur PS4 : " + c.Name.Content + (c.ParentEntry != null ? " (dans " + c.ParentEntry.Name.Content + ")" : ""));
    var renBack = new HashSet<string>(ren.Values);
    foreach (var c in pc.Code) {
        if (c?.Name?.Content == null || ps4Code.ContainsKey(c.Name.Content) || renBack.Contains(c.Name.Content)) continue;
        Log("  code présent seulement sur PC : " + c.Name.Content + (c.ParentEntry != null ? " (dans " + c.ParentEntry.Name.Content + ")" : " (" + c.Instructions.Count + " instructions)"));
        if (c.ParentEntry == null) d.PcOnlyRoots.Add(c.Name.Content);
    }

    long total = 0; int shown = 0;
    foreach (var b in ps4.Code) {
        if (b?.Name?.Content == null || b.ParentEntry != null) continue;
        UndertaleCode a;
        if (!pcCode.TryGetValue(b.Name.Content, out a)) continue;
        var A = a.Instructions; var B = b.Instructions;
        total += B.Count;
        var cd = new CodeDelta { Name = b.Name.Content, Structural = A.Count != B.Count };
        if (!cd.Structural) {
            for (int i = 0; i < A.Count; i++) {
                if (Sig(A[i], true, null) == Sig(B[i], true, ren)) continue;
                cd.Diff.Add(i);
                if (A[i].Kind != B[i].Kind || A[i].CalculateInstructionSize() != B[i].CalculateInstructionSize()) cd.Structural = true;
            }
            if (cd.Diff.Count == 0) continue;
        }
        cd.PcLoose = A.Select(i => Sig(i, false, null)).ToArray();
        if (cd.Structural) {
            cd.Ps4All = B.Select(Detach).ToList();
            foreach (var ch in b.ChildEntries) {
                string r;
                cd.Ps4ChildOffsets[ren.TryGetValue(ch.Name.Content, out r) ? r : ch.Name.Content] = ch.Offset;
            }
            Log("  code " + cd.Name + " : structure différente (PC " + A.Count + " instructions, PS4 " + B.Count + ")");
        }
        else {
            foreach (int i in cd.Diff) {
                cd.Ps4[i] = Detach(B[i]);
                if (A[i].ValueString != null) cd.PcStr[i] = A[i].ValueString.Resource?.Content;
                if (shown++ < 400) Log("  code " + cd.Name + " [" + i + "] : PC " + Cut(Describe(A[i])) + " | PS4 " + Cut(Describe(B[i])));
            }
        }
        d.Code.Add(cd);
    }
    Log("  code : " + N(total) + " instructions comparées, " + d.Code.Count + " entrées propres à la PS4 (" + d.Code.Sum(c => c.Diff.Count) + " instructions, " +
        d.Code.Count(c => c.Structural) + " de structure différente)");

    // Things that are expected to be the same: say so when they are not
    Action<string, IEnumerable<string>, IEnumerable<string>> names = (what, a, b) => {
        var sa = new HashSet<string>(a.Where(x => x != null)); var sb2 = new HashSet<string>(b.Where(x => x != null));
        var oa = sa.Where(x => !sb2.Contains(x)).ToList(); var ob = sb2.Where(x => !sa.Contains(x)).ToList();
        if (oa.Count + ob.Count > 0)
            Log("  " + what + " : seulement PC [" + string.Join(", ", oa.Take(30)) + "] seulement PS4 [" + string.Join(", ", ob.Take(30)) + "]");
    };
    names("sprites", pc.Sprites.Select(x => x?.Name?.Content), ps4.Sprites.Select(x => x?.Name?.Content));
    names("sons", pc.Sounds.Select(x => x?.Name?.Content), ps4.Sounds.Select(x => x?.Name?.Content));
    names("objets", pc.GameObjects.Select(x => x?.Name?.Content), ps4.GameObjects.Select(x => x?.Name?.Content));
    names("salles", pc.Rooms.Select(x => x?.Name?.Content), ps4.Rooms.Select(x => x?.Name?.Content));
    names("polices", pc.Fonts.Select(x => x?.Name?.Content), ps4.Fonts.Select(x => x?.Name?.Content));
    names("fonds", pc.Backgrounds.Select(x => x?.Name?.Content), ps4.Backgrounds.Select(x => x?.Name?.Content));
    names("scripts", pc.Scripts.Select(x => x?.Name?.Content), ps4.Scripts.Select(x => { string r; return x?.Name?.Content != null && ren.TryGetValue(x.Name.Content, out r) ? r : x?.Name?.Content; }));
    names("groupes de textures", pc.TextureGroupInfo?.Select(x => x?.Name?.Content) ?? new string[0], ps4.TextureGroupInfo?.Select(x => x?.Name?.Content) ?? new string[0]);
    names("groupes audio", pc.AudioGroups.Select(x => x?.Name?.Content), ps4.AudioGroups.Select(x => x?.Name?.Content));
    // Images only the PS4 file has: kept, with their texture pages and their texture group
    var pageIndex = new Dictionary<UndertaleEmbeddedTexture, int>();
    Func<UndertaleEmbeddedTexture, int> page = t => {
        int k;
        if (pageIndex.TryGetValue(t, out k)) return k;
        k = d.Pages.Count;
        pageIndex[t] = k;
        if (t.TextureExternal) Problem("page de textures PS4 stockée hors du fichier : non reprise");
        d.Pages.Add(new PageDelta { Scaled = t.Scaled, GeneratedMips = t.GeneratedMips, W = t.TextureWidth, H = t.TextureHeight, IndexInGroup = t.IndexInGroup,
                                    Image = t.TextureExternal ? null : t.TextureData?.Image });
        return k;
    };
    Action<char, string, int, UndertaleTexturePageItem> keep = (kind, owner, frame, it) => {
        if (it.TexturePage == null) { Problem("image PS4 sans page de textures : " + owner); return; }
        d.Items.Add(new ItemDelta { Kind = kind, Owner = owner, Frame = frame, Page = page(it.TexturePage),
            V = new ushort[] { it.SourceX, it.SourceY, it.SourceWidth, it.SourceHeight, it.TargetX, it.TargetY, it.TargetWidth, it.TargetHeight, it.BoundingWidth, it.BoundingHeight } });
    };
    var pcSprites = new Dictionary<string, UndertaleSprite>();
    foreach (var x in pc.Sprites) if (x?.Name?.Content != null && !pcSprites.ContainsKey(x.Name.Content)) pcSprites[x.Name.Content] = x;
    int frames = 0, framesDiff = 0, pcOnlyImages = 0;
    var ps4OnlyOwners = new List<string>();
    foreach (var s in ps4.Sprites) {
        if (s?.Name?.Content == null) continue;
        UndertaleSprite p;
        if (!pcSprites.TryGetValue(s.Name.Content, out p)) continue;
        frames += s.Textures.Count;
        if (p.Width != s.Width || p.Height != s.Height || p.Textures.Count != s.Textures.Count) {
            if (framesDiff++ < 30) Log("  sprite " + s.Name.Content + " : PC " + p.Width + "x" + p.Height + " " + p.Textures.Count + " images | PS4 " + s.Width + "x" + s.Height + " " + s.Textures.Count + " images");
        }
        bool any = false;
        for (int k = 0; k < s.Textures.Count; k++) {
            bool here = s.Textures[k].Texture != null, there = k < p.Textures.Count && p.Textures[k].Texture != null;
            if (here && !there) { keep('S', s.Name.Content, k, s.Textures[k].Texture); any = true; }
            else if (!here && there) pcOnlyImages++;
        }
        if (any) ps4OnlyOwners.Add(s.Name.Content);
    }
    foreach (var b in ps4.Backgrounds) {
        if (b?.Name?.Content == null || b.Texture == null) continue;
        var p = pc.Backgrounds.FirstOrDefault(x => x?.Name?.Content == b.Name.Content);
        if (p != null && p.Texture == null) { keep('B', b.Name.Content, 0, b.Texture); ps4OnlyOwners.Add(b.Name.Content); }
    }
    foreach (var f in ps4.Fonts) {
        if (f?.Name?.Content == null || f.Texture == null) continue;
        var p = pc.Fonts.FirstOrDefault(x => x?.Name?.Content == f.Name.Content);
        if (p != null && p.Texture == null) { keep('F', f.Name.Content, 0, f.Texture); ps4OnlyOwners.Add(f.Name.Content); }
    }
    if (ps4.TextureGroupInfo != null) {
        for (int gi = 0; gi < ps4.TextureGroupInfo.Count; gi++) {
            var tg = ps4.TextureGroupInfo[gi];
            if (tg == null || (pc.TextureGroupInfo != null && pc.TextureGroupInfo.Any(x => x?.Name?.Content == tg.Name?.Content))) continue;
            var gd = new GroupDelta { Name = tg.Name?.Content, Directory = tg.Directory?.Content, Extension = tg.Extension?.Content, LoadType = tg.LoadType, Index = gi };
            foreach (var r in tg.TexturePages) if (r?.Resource != null) gd.Pages.Add(page(r.Resource));
            foreach (var r in tg.Sprites) if (r?.Resource?.Name?.Content != null) gd.Sprites.Add(r.Resource.Name.Content);
            foreach (var r in tg.Fonts) if (r?.Resource?.Name?.Content != null) gd.Fonts.Add(r.Resource.Name.Content);
            foreach (var r in tg.Tilesets) if (r?.Resource?.Name?.Content != null) gd.Tilesets.Add(r.Resource.Name.Content);
            d.Groups.Add(gd);
            Log("  groupe de textures seulement sur PS4 : \"" + gd.Name + "\" (" + gd.Pages.Count + " pages, " + gd.Sprites.Count + " sprites [" + string.Join(", ", gd.Sprites.Take(40)) + "], " +
                gd.Fonts.Count + " polices, " + gd.Tilesets.Count + " jeux de tuiles, chargement " + gd.LoadType + ")");
        }
    }
    Log("  sprites : " + N(frames) + " images comparées, " + framesDiff + " sprites de taille différente ; images seulement sur PS4 : " + d.Items.Count + " dans " + d.Pages.Count +
        " pages [" + string.Join(", ", ps4OnlyOwners.Take(40)) + "] ; images seulement sur PC : " + pcOnlyImages + " ; pages de textures PC " + pc.EmbeddedTextures.Count +
        " | PS4 " + ps4.EmbeddedTextures.Count + " ; sons intégrés PC " + pc.EmbeddedAudio.Count + " | PS4 " + ps4.EmbeddedAudio.Count);
    return d;
}

// ---------------------------------------------------------------------------------------------------------------------
// Applying it to the French file

int Locate(string[] a, string[] c, int i) {
    foreach (int k in new[] { 2, 4, 8, 16, 32, 64 }) {
        int lo = Math.Max(0, i - k), hi = Math.Min(a.Length - 1, i + k), len = hi - lo + 1, found = -1, count = 0;
        for (int s = 0; s + len <= c.Length && count < 2; s++) {
            bool ok = true;
            for (int t = 0; t < len; t++) if (c[s + t] != a[lo + t]) { ok = false; break; }
            if (ok) { count++; found = s + (i - lo); }
        }
        if (count == 1) return found;
        if (count == 0) return -1;
    }
    return -1;
}

void Apply(Delta d, UndertaleData fr, string label) {
    // GEN8, OPTN
    Log("  GEN8 : config \"" + fr.GeneralInfo.Config?.Content + "\" -> \"" + d.Config + "\" ; info " + fr.GeneralInfo.Info + " -> " + d.Info);
    fr.GeneralInfo.Config = fr.Strings.MakeString(d.Config);
    fr.GeneralInfo.Info = d.Info;
    if (d.DisplayNameDiffers && d.DisplayName != null) {
        Log("  GEN8 : nom affiché \"" + fr.GeneralInfo.DisplayName?.Content + "\" -> \"" + d.DisplayName + "\" (celui de la PS4)");
        fr.GeneralInfo.DisplayName = fr.Strings.MakeString(d.DisplayName);
    }
    if (d.OptionsDiffer) {
        var o = fr.Options;
        o.Info = d.OptInfo; o.Scale = d.OptScale; o.ShaderExtensionFlag = d.OptShaderFlag; o.ShaderExtensionVersion = d.OptShaderVer;
        o.WindowColor = d.OptVals[0]; o.ColorDepth = d.OptVals[1]; o.Resolution = d.OptVals[2]; o.Frequency = d.OptVals[3]; o.VertexSync = d.OptVals[4]; o.Priority = d.OptVals[5]; o.LoadAlpha = d.OptVals[6];
        o.Constants.Clear();
        foreach (var kv in d.OptConstants) o.Constants.Add(new UndertaleOptions.Constant { Name = fr.Strings.MakeString(kv.Key), Value = fr.Strings.MakeString(kv.Value) });
        Log("  OPTN : options de la PS4 appliquées");
    }

    // Code
    var frCode = new Dictionary<string, UndertaleCode>();
    foreach (var c in fr.Code) if (c?.Name?.Content != null && !frCode.ContainsKey(c.Name.Content)) frCode[c.Name.Content] = c;
    var funcs = new Dictionary<string, UndertaleFunction>();
    foreach (var f in fr.Functions) if (f?.Name?.Content != null && !funcs.ContainsKey(f.Name.Content)) funcs[f.Name.Content] = f;
    var vars = new Dictionary<string, UndertaleVariable>();
    foreach (var v in fr.Variables) {
        if (v?.Name?.Content == null) continue;
        string k3 = v.Name.Content + "@" + (int)v.InstanceType + "#" + v.VarID, k2 = v.Name.Content + "@" + (int)v.InstanceType;
        if (!vars.ContainsKey(k3)) vars[k3] = v;
        if (!vars.ContainsKey(k2)) vars[k2] = v;
    }
    Func<string, UndertaleFunction> func = name => {
        UndertaleFunction f;
        if (!funcs.TryGetValue(name, out f)) { f = fr.Functions.EnsureDefined(name, fr.Strings); funcs[name] = f; Log("  fonction ajoutée : " + name); }
        return f;
    };
    Func<PIns, UndertaleInstruction, UndertaleInstruction> rehome = (p, hint) => {
        var c = CloneIns(p.Ins);
        if (p.Ref == null) return c;
        if (p.Ref.Kind == 'S') {
            int id;
            var s = fr.Strings.MakeString(p.Ref.Str, out id);
            c.ValueString = new UndertaleResourceById<UndertaleString, UndertaleChunkSTRG>() { Resource = s, CachedId = id };
        }
        else if (p.Ref.Kind == 'F') c.ValueFunction = func(p.Ref.Fn);
        else {
            UndertaleVariable v = hint?.ValueVariable;
            if (v == null || v.Name?.Content != p.Ref.VarName || v.InstanceType != p.Ref.VarInst) {
                if (!vars.TryGetValue(p.Ref.VarName + "@" + (int)p.Ref.VarInst + "#" + p.Ref.VarId, out v) && !vars.TryGetValue(p.Ref.VarName + "@" + (int)p.Ref.VarInst, out v)) {
                    Problem(label + " : variable de la PS4 introuvable dans le fichier français : " + p.Ref.VarName + " (" + p.Ref.VarInst + ")");
                    v = hint?.ValueVariable;
                }
            }
            c.ValueVariable = v;
        }
        return c;
    };

    int patched = 0, touched = 0, kept = 0;
    foreach (var cd in d.Code) {
        UndertaleCode c;
        if (!frCode.TryGetValue(cd.Name, out c)) { Problem(label + " : code absent du fichier français : " + cd.Name); continue; }
        var C = c.Instructions;
        string[] loose = C.Select(i => Sig(i, false, null)).ToArray();
        bool untouched = loose.SequenceEqual(cd.PcLoose);
        if (!untouched) { touched++; Log("  " + cd.Name + " : aussi modifié par le patch FR (" + cd.PcLoose.Length + " -> " + loose.Length + " instructions)"); }
        if (cd.Structural) {
            if (!untouched) { Problem(label + " : " + cd.Name + " diffère en structure sur PS4 et le patch FR l'a modifié : laissé tel quel"); continue; }
            var list = new List<UndertaleInstruction>();
            for (int k = 0; k < cd.Ps4All.Count; k++) list.Add(rehome(cd.Ps4All[k], null));
            c.Replace(list);
            foreach (var ch in c.ChildEntries) {
                uint off;
                if (cd.Ps4ChildOffsets.TryGetValue(ch.Name.Content, out off)) ch.Offset = off;
                else Problem(label + " : fonction interne sans équivalent PS4 : " + ch.Name.Content);
            }
            patched += cd.Ps4All.Count;
            continue;
        }
        foreach (int i in cd.Diff) {
            int j = untouched ? i : Locate(cd.PcLoose, loose, i);
            if (j < 0) { Problem(label + " : " + cd.Name + " [" + i + "] : endroit introuvable dans le code modifié par le patch FR"); continue; }
            var p = cd.Ps4[i];
            string pcStr;
            if (p.Ref != null && p.Ref.Kind == 'S' && cd.PcStr.TryGetValue(i, out pcStr) && C[j].ValueString?.Resource?.Content != pcStr) {
                kept++;
                Log("  " + cd.Name + " [" + j + "] : texte traduit gardé : \"" + Cut(C[j].ValueString?.Resource?.Content, 60) + "\" (PC \"" + Cut(pcStr, 40) + "\", PS4 \"" + Cut(p.Ref.Str, 40) + "\")");
                continue;
            }
            C[j] = rehome(p, C[j]);
            patched++;
        }
    }
    Log("  code : " + patched + " instructions mises à la valeur PS4 dans " + d.Code.Count + " entrées (" + touched + " aussi modifiées par le patch FR, " + kept + " textes traduits gardés)");

    // Extensions the PS4 build does not have: their functions become the stub the PS4 compiler uses, the extension goes
    var gone = fr.Extensions.Where(e => !d.Ps4Extensions.Contains(e.Name?.Content ?? "")).ToList();
    if (gone.Count > 0) {
        var stubbed = new HashSet<string>();
        foreach (var e in gone) foreach (var f in e.Files) foreach (var fn in f.Functions) if (fn?.Name?.Content != null) stubbed.Add(fn.Name.Content);
        int n = 0;
        UndertaleFunction stub = null;
        foreach (var c in fr.Code) {
            if (c == null || c.ParentEntry != null) continue;
            foreach (var i in c.Instructions) {
                if (i.ValueFunction?.Name?.Content == null || !stubbed.Contains(i.ValueFunction.Name.Content)) continue;
                if (stub == null) stub = func("extension_stubfunc_real");
                i.ValueFunction = stub;
                n++;
            }
        }
        // The chunk also holds 16 bytes for each extension, in the same order: those of the removed ones go with them
        var ids = fr.FORM.EXTN?.productIdData;
        if (ids != null && ids.Count == fr.Extensions.Count) {
            for (int k = fr.Extensions.Count - 1; k >= 0; k--) if (gone.Contains(fr.Extensions[k])) ids.RemoveAt(k);
        }
        else if (ids != null && ids.Count > 0) Problem(label + " : " + ids.Count + " identifiants d'extension pour " + fr.Extensions.Count + " extensions");
        foreach (var e in gone) fr.Extensions.Remove(e);
        int removed = 0;
        for (int k = fr.Functions.Count - 1; k >= 0; k--)
            if (fr.Functions[k]?.Name?.Content != null && stubbed.Contains(fr.Functions[k].Name.Content)) { fr.Functions.RemoveAt(k); removed++; }
        Log("  extensions retirées (absentes sur PS4) : " + string.Join(", ", gone.Select(e => e.Name?.Content)) + " ; " + stubbed.Count + " fonctions, " + n +
            " appels remplacés par extension_stubfunc_real, " + removed + " retirées de la table");
    }

    // Code of the French patch that cannot run on a console as it is. On a console the save files are loaded a moment
    // after the game starts (global.savedata), and init_drfr converts flags of the save files right at the start:
    // "global variable 'savedata' not set before reading it". The conversion is skipped at that point.
    {
        var skip = new HashSet<string>((Environment.GetEnvironmentVariable("DRFR_SKIP") ?? "gml_Script_convert_sakushi_flags").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
        UndertaleFunction stub = null;
        var where = new List<string>();
        var elsewhere = new List<string>();
        foreach (var c in fr.Code) {
            if (c == null || c.ParentEntry != null) continue;
            foreach (var i in c.Instructions) {
                if (i.Kind != UndertaleInstruction.Opcode.Call || i.ValueFunction?.Name?.Content == null || !skip.Contains(i.ValueFunction.Name.Content)) continue;
                if (c.Name.Content.IndexOf("init_drfr", StringComparison.OrdinalIgnoreCase) < 0) { elsewhere.Add(c.Name.Content); continue; }
                if (stub == null) stub = func("extension_stubfunc_real");
                where.Add(i.ValueFunction.Name.Content + " dans " + c.Name.Content);
                i.ValueFunction = stub;
            }
        }
        if (where.Count > 0 || elsewhere.Count > 0)
            Log("  patch FR, appels faits avant le chargement des sauvegardes de la console : " + where.Count + " neutralisés [" + string.Join(", ", where) + "] ; laissés ailleurs [" +
                string.Join(", ", elsewhere.Distinct()) + "]");
    }

    // The French patch starts itself (scr_drfr_init_localization, init_drfr) right after scr_gamestart, but only where
    // the PC game starts. A console starts elsewhere, once its save data is loaded, and the patch is not started there:
    // "global variable 'drfr_prefered_audio' not set". Every other place that calls scr_gamestart now calls a small new
    // function doing the three calls in the PC order.
    var consoleStarts = new HashSet<string>();
    if (fr.Functions.ByName("gml_Script_scr_gamestart") != null && fr.Functions.ByName("gml_Script_init_drfr") != null && fr.Functions.ByName("gml_Script_scr_drfr_init_localization") != null) {
        var calls = new List<Tuple<UndertaleCode, UndertaleInstruction>>();
        foreach (var c in fr.Code) {
            if (c == null || c.ParentEntry != null || c.Name.Content.IndexOf("drfr", StringComparison.OrdinalIgnoreCase) >= 0) continue;
            var cs = c.Instructions.Where(i => i.Kind == UndertaleInstruction.Opcode.Call && i.ValueFunction?.Name?.Content == "gml_Script_scr_gamestart").ToList();
            if (cs.Count == 0) continue;
            if (c.Instructions.Any(i => i.Kind == UndertaleInstruction.Opcode.Call && (i.ValueFunction?.Name?.Content == "gml_Script_init_drfr" || i.ValueFunction?.Name?.Content == "gml_Script_scr_drfr_init_localization"))) {
                Log("  démarrage du patch FR déjà présent dans " + c.Name.Content);
                continue;
            }
            foreach (var i in cs) {
                if (i.ArgumentsCount != 0) { Problem(label + " : scr_gamestart appelé avec " + i.ArgumentsCount + " arguments dans " + c.Name.Content + " : laissé"); continue; }
                calls.Add(Tuple.Create(c, i));
            }
        }
        if (calls.Count == 0) Problem(label + " : aucun autre endroit n'appelle scr_gamestart : le patch FR ne sera pas démarré sur console");
        else {
            var group = new UndertaleModLib.Compiler.CodeImportGroup(fr);
            group.QueueReplace("gml_GlobalScript_drfr_ps4_gamestart", "function drfr_ps4_gamestart()\n{\n    scr_gamestart();\n    scr_drfr_init_localization();\n    init_drfr();\n}\n");
            var res = group.Import(false);
            var wrapper = fr.Functions.ByName("gml_Script_drfr_ps4_gamestart");
            if (!res.Successful || wrapper == null) Problem(label + " : la fonction de démarrage n'a pas pu être ajoutée : " + res.PrintAllErrors(false));
            else {
                foreach (var t in calls) { t.Item2.ValueFunction = wrapper; consoleStarts.Add(t.Item1.Name.Content); }
                Log("  patch FR démarré aussi au démarrage console : " + calls.Count + " appel(s) à scr_gamestart remplacés par drfr_ps4_gamestart dans [" + string.Join(", ", consoleStarts) + "]");
            }
        }
    }

    {
        var ids = fr.FORM.EXTN?.productIdData;
        int changed = 0;
        if (ids != null && ids.Count == fr.Extensions.Count)
            for (int k = 0; k < ids.Count; k++) {
                byte[] v;
                if (d.Ps4ProductIds.TryGetValue(fr.Extensions[k].Name?.Content ?? "", out v) && !v.SequenceEqual(ids[k])) { ids[k] = v; changed++; }
            }
        if (fr.Extensions.Count > 0)
            Log("  extensions : " + fr.Extensions.Count + " [" + string.Join(", ", fr.Extensions.Select(e => e.Name?.Content)) + "], " + (ids?.Count ?? 0) + " identifiants (" + changed + " mis à la valeur PS4)");
    }

    // Shaders: the PS4 build's compiled data
    int sh = 0;
    foreach (var sd in d.Shaders) {
        var s = fr.Shaders.FirstOrDefault(x => x?.Name?.Content == sd.Name);
        if (s == null) { Problem(label + " : shader absent du fichier français : " + sd.Name); continue; }
        s.Type = sd.Type; s.Version = sd.Version;
        var raw = new[] { s.HLSL11_VertexData, s.HLSL11_PixelData, s.PSSL_VertexData, s.PSSL_PixelData, s.Cg_PSVita_VertexData, s.Cg_PSVita_PixelData, s.Cg_PS3_VertexData, s.Cg_PS3_PixelData };
        for (int k = 0; k < raw.Length; k++) {
            if (raw[k] == null) { if (!sd.Null[k]) Problem(label + " : shader " + sd.Name + " : emplacement " + k + " absent"); continue; }
            raw[k].IsNull = sd.Null[k];
            raw[k].Data = sd.Null[k] ? null : sd.Raw[k];
        }
        if (!s.VertexShaderAttributes.Select(a => a.Name?.Content).SequenceEqual(sd.Attr)) {
            s.VertexShaderAttributes.Clear();
            foreach (string a in sd.Attr) s.VertexShaderAttributes.Add(new UndertaleShader.VertexShaderAttribute { Name = fr.Strings.MakeString(a) });
        }
        sh++;
    }
    if (d.Shaders.Count > 0) Log("  shaders : données compilées PS4 mises dans " + sh + " shaders");
    foreach (var s in fr.Shaders.Where(x => x != null && !d.Shaders.Any(y => y.Name == x.Name?.Content)))
        Problem(label + " : shader ajouté par le patch FR, sans version PS4 : " + s.Name?.Content);

    // The images only the PS4 has (screen borders): their pages, their places on the pages, their texture group
    if (d.Pages.Count > 0) {
        var newPages = new List<UndertaleEmbeddedTexture>();
        foreach (var pd in d.Pages) {
            var t = new UndertaleEmbeddedTexture { Scaled = pd.Scaled, GeneratedMips = pd.GeneratedMips, TextureWidth = pd.W, TextureHeight = pd.H, IndexInGroup = pd.IndexInGroup };
            t.TextureData.Image = pd.Image;
            t.Name = new UndertaleString("Texture " + fr.EmbeddedTextures.Count);
            fr.EmbeddedTextures.Add(t);
            newPages.Add(t);
        }
        var frSprites = new Dictionary<string, UndertaleSprite>();
        foreach (var x in fr.Sprites) if (x?.Name?.Content != null && !frSprites.ContainsKey(x.Name.Content)) frSprites[x.Name.Content] = x;
        int set = 0, already = 0;
        foreach (var it in d.Items) {
            UndertaleSprite sp = null; UndertaleBackground bg = null; UndertaleFont fo = null;
            UndertaleTexturePageItem cur = null;
            if (it.Kind == 'S') {
                if (!frSprites.TryGetValue(it.Owner, out sp)) { Problem(label + " : sprite de la PS4 absent du fichier français : " + it.Owner); continue; }
                while (sp.Textures.Count <= it.Frame) sp.Textures.Add(new UndertaleSprite.TextureEntry());
                cur = sp.Textures[it.Frame].Texture;
            }
            else if (it.Kind == 'B') {
                bg = fr.Backgrounds.FirstOrDefault(x => x?.Name?.Content == it.Owner);
                if (bg == null) { Problem(label + " : fond de la PS4 absent du fichier français : " + it.Owner); continue; }
                cur = bg.Texture;
            }
            else {
                fo = fr.Fonts.FirstOrDefault(x => x?.Name?.Content == it.Owner);
                if (fo == null) { Problem(label + " : police de la PS4 absente du fichier français : " + it.Owner); continue; }
                cur = fo.Texture;
            }
            if (cur != null) { already++; continue; }
            var ni = new UndertaleTexturePageItem {
                SourceX = it.V[0], SourceY = it.V[1], SourceWidth = it.V[2], SourceHeight = it.V[3], TargetX = it.V[4], TargetY = it.V[5], TargetWidth = it.V[6], TargetHeight = it.V[7],
                BoundingWidth = it.V[8], BoundingHeight = it.V[9], TexturePage = newPages[it.Page] };
            ni.Name = new UndertaleString("PageItem " + fr.TexturePageItems.Count);
            fr.TexturePageItems.Add(ni);
            if (sp != null) sp.Textures[it.Frame].Texture = ni; else if (bg != null) bg.Texture = ni; else fo.Texture = ni;
            set++;
        }
        int groups = 0;
        if (fr.TextureGroupInfo != null) {
            foreach (var gd in d.Groups) {
                if (fr.TextureGroupInfo.Any(x => x?.Name?.Content == gd.Name)) continue;
                var tg = new UndertaleTextureGroupInfo { Name = fr.Strings.MakeString(gd.Name), LoadType = gd.LoadType };
                if (gd.Directory != null) tg.Directory = fr.Strings.MakeString(gd.Directory);
                if (gd.Extension != null) tg.Extension = fr.Strings.MakeString(gd.Extension);
                foreach (int k in gd.Pages) { tg.TexturePages.Add(new UndertaleResourceById<UndertaleEmbeddedTexture, UndertaleChunkTXTR>(newPages[k])); newPages[k].TextureInfo = tg; }
                foreach (string n in gd.Sprites) { UndertaleSprite x; if (frSprites.TryGetValue(n, out x)) tg.Sprites.Add(new UndertaleResourceById<UndertaleSprite, UndertaleChunkSPRT>(x)); }
                foreach (string n in gd.Fonts) { var x = fr.Fonts.FirstOrDefault(y => y?.Name?.Content == n); if (x != null) tg.Fonts.Add(new UndertaleResourceById<UndertaleFont, UndertaleChunkFONT>(x)); }
                foreach (string n in gd.Tilesets) { var x = fr.Backgrounds.FirstOrDefault(y => y?.Name?.Content == n); if (x != null) tg.Tilesets.Add(new UndertaleResourceById<UndertaleBackground, UndertaleChunkBGND>(x)); }
                fr.TextureGroupInfo.Insert(Math.Min(gd.Index, fr.TextureGroupInfo.Count), tg);
                groups++;
            }
        }
        Log("  images propres à la PS4 : " + set + " images remises dans " + newPages.Count + " pages de textures ajoutées (" + already + " déjà présentes), " + groups + " groupe(s) de textures ajouté(s)");
    }

    // The code the French patch added or reshaped, and the game's save code, as text, to diagnose a problem (code-fr\)
    try {
        string dir = Path.Combine(outRoot, "code-fr");
        Directory.CreateDirectory(dir);
        var always = new Regex("ossafe_ini_open|ossafe_ini_close|ossafe_savedata|ossafe_init|ossafe_file|CHAPTER_SELECT_Create_0|obj_initializer.*_Create_0|GlobalScript_scr_init$|obj_initializer2_|obj_init_console|drfr|sakushi", RegexOptions.IgnoreCase);
        var ctx = new GlobalDecompileContext(fr);
        var settings = fr.ToolInfo.DecompilerSettings;
        int dumped = 0, failed = 0; long size = 0;
        using (var w = new StreamWriter(Path.Combine(dir, label.Replace(' ', '-') + ".txt"), false, new UTF8Encoding(true))) {
            foreach (var c in fr.Code) {
                if (c?.Name?.Content == null || c.ParentEntry != null) continue;
                int pcCount;
                bool added = !d.PcCodeCount.TryGetValue(c.Name.Content, out pcCount), reshaped = !added && pcCount != c.Instructions.Count;
                bool named = always.IsMatch(c.Name.Content) || consoleStarts.Contains(c.Name.Content);
                if (!named && !added && !reshaped) continue;
                if (!named && size > 700000) continue;
                string text;
                try { text = new Underanalyzer.Decompiler.DecompileContext(ctx, c, settings).DecompileToString(); }
                catch (Exception e) { text = "// decompilation failed: " + e.Message; failed++; }
                var lines = text.Replace("\r", "").Split('\n');
                w.WriteLine("//////// " + c.Name.Content + "  (" + (added ? "ajouté par le patch FR" : reshaped ? "modifié par le patch FR : " + pcCount + " -> " + c.Instructions.Count + " instructions" : "inchangé") + ")");
                foreach (string l in lines.Take(named ? 600 : 250)) w.WriteLine(l);
                if (lines.Length > (named ? 600 : 250)) w.WriteLine("// ... " + (lines.Length - (named ? 600 : 250)) + " lignes de plus");
                w.WriteLine();
                size += Math.Min(text.Length, 20000);
                dumped++;
            }
        }
        Log("  code-fr : " + dumped + " morceaux de code écrits en texte (" + failed + " illisibles)");
    }
    catch (Exception e) { Log("  code-fr : non écrit (" + e.Message + ")"); }

    DumpBorders(fr, "fr");

    int nullTex = fr.Sprites.Where(s => s != null).Sum(s => s.Textures.Count(t => t.Texture == null));
    if (nullTex > 0) Log("  sprites : " + nullTex + " images sans texture dans le fichier français");
    var fmts = fr.EmbeddedTextures.Where(t => t != null).GroupBy(t => t.TextureData?.Image?.Format.ToString() ?? (t.TextureExternal ? "externe" : "?")).Select(g2 => g2.Key + " x" + g2.Count());
    Log("  textures : " + string.Join(", ", fmts) + " ; sons intégrés " + fr.EmbeddedAudio.Count + " ; chaînes " + N(fr.Strings.Count) + " ; code " + N(fr.Code.Count));
}

// ---------------------------------------------------------------------------------------------------------------------

string Sub(string root, string suffix) {
    foreach (string dir in Directory.GetDirectories(root)) if (Path.GetFileName(dir).EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return dir;
    return null;
}

string app0 = Sub(ps4Root, "-app0"), patch0 = Sub(ps4Root, "-patch0");
if (app0 == null && File.Exists(Path.Combine(ps4Root, "games", "game.win"))) app0 = ps4Root;
if (app0 == null) throw new Exception("pas de dossier ...-app0 dans " + ps4Root);
string titleId = Path.GetFileName(app0).Split('-')[0];
if (!Regex.IsMatch(titleId, "^[A-Z]{4}[0-9]{5}$")) titleId = "CUSA52463";
string outGames = Path.Combine(outRoot, titleId, "games");
Log("Port.csx 0.6 - " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
Log("PS4 : " + app0 + (patch0 != null ? " + " + patch0 : " (pas de mise à jour)"));
Log("PC  : " + pcRoot);
Log("Sortie : " + outGames);

Func<string, string> ps4File = rel => {
    if (patch0 != null && File.Exists(Path.Combine(patch0, rel))) return Path.Combine(patch0, rel);
    return File.Exists(Path.Combine(app0, rel)) ? Path.Combine(app0, rel) : null;
};

var units = new List<Tuple<string, string, string>>();   // label, PC folder, PS4 folder under games
units.Add(Tuple.Create("sélection des chapitres", "", ""));
for (int n = 1; n <= 9; n++)
    if (Directory.Exists(Path.Combine(pcRoot, "chapter" + n + "_windows"))) units.Add(Tuple.Create("chapitre " + n, "chapter" + n + "_windows", "chapter" + n + "_ps4"));

int done = 0;
var written = new List<string>();
foreach (var u in units) {
    Step(u.Item1);
    string pcFr = Path.Combine(pcRoot, u.Item2, "data.win"), pcEn = pcFr + ".bak";
    string ps4 = ps4File(Path.Combine("games", u.Item3, "game.win"));
    if (!File.Exists(pcFr)) { Problem(u.Item1 + " : pas de " + pcFr); continue; }
    if (!File.Exists(pcEn)) { Problem(u.Item1 + " : pas de data.win.bak (le fichier d'origine gardé par l'installateur du patch FR) : chapitre laissé en anglais"); continue; }
    if (ps4 == null) { Problem(u.Item1 + " : pas de game.win côté PS4 : chapitre ignoré"); continue; }
    try {
        Delta d;
        {
            var a = Load(pcEn);
            var b = Load(ps4);
            if (Math.Abs((long)a.GeneralInfo.Timestamp - (long)b.GeneralInfo.Timestamp) > 6 * 3600)
                Problem(u.Item1 + " : les fichiers PC et PS4 n'ont pas été construits le même jour (PC " + a.GeneralInfo.Timestamp + ", PS4 " + b.GeneralInfo.Timestamp +
                        ") : ce ne sont sans doute pas les mêmes versions du jeu");
            Log(" -- PC d'origine comparé à la PS4");
            unitLabel = u.Item1;
            d = Compare(a, b);
            DumpBorders(b, "ps4");
            a.Dispose(); b.Dispose();
        }
        GC.Collect();
        var fr = Load(pcFr);
        Log(" -- application au fichier français");
        Apply(d, fr, u.Item1);
        d = null;
        string dst = Path.Combine(outGames, u.Item3, "game.win");
        Directory.CreateDirectory(Path.GetDirectoryName(dst));
        using (var fs = new FileStream(dst, FileMode.Create, FileAccess.ReadWrite))
            UndertaleIO.Write(fs, fr, m => { });
        fr.Dispose(); fr = null;
        GC.Collect();
        Log("  écrit : " + dst + " (" + N(new FileInfo(dst).Length) + " octets)");
        written.Add(dst);
        if (recheck) {
            int before = warnings;
            var r = Load(dst);
            if (warnings > before) Problem(u.Item1 + " : " + (warnings - before) + " avertissement(s) à la relecture du fichier écrit");
            Log("  relu sans erreur : config \"" + r.GeneralInfo.Config?.Content + "\", " + N(r.Code.Count) + " codes, " + N(r.Strings.Count) + " chaînes, " + r.Shaders.Count + " shaders, " +
                r.Extensions.Count + " extensions");
            r.Dispose(); r = null;
            GC.Collect();
        }
        done++;
    }
    catch (Exception e) {
        Problem(u.Item1 + " : " + e.GetType().Name + " : " + e.Message);
        Log(e.ToString());
    }
}

// The other files of the French patch: those it replaced (a .bak next to them) and those it added
Step("autres fichiers");
int copied = 0;
Func<string, string> toPs4 = rel => {
    string r = rel.Replace('\\', '/');
    var m = Regex.Match(r, @"^chapter(\d)_windows/(.*)$", RegexOptions.IgnoreCase);
    return m.Success ? "chapter" + m.Groups[1].Value + "_ps4/" + m.Groups[2].Value : r;
};
foreach (string f in Directory.GetFiles(pcRoot, "*", SearchOption.AllDirectories)) {
    string rel = f.Substring(pcRoot.Length).TrimStart('\\', '/'), name = Path.GetFileName(f), ext = Path.GetExtension(f).ToLowerInvariant();
    if (name.Equals("data.win", StringComparison.OrdinalIgnoreCase) || ext == ".bak" || ext == ".exe" || ext == ".dll" || name.Equals("options.ini", StringComparison.OrdinalIgnoreCase)) continue;
    if (rel.Contains("deltarune-fr-ps4") || ext == ".txt" || ext == ".log" || ext == ".xdelta" || ext == ".zip") continue;
    string p4 = toPs4(rel);
    string there = ps4File("games/" + p4);
    bool replaced = File.Exists(f + ".bak");
    if (!replaced && there != null) continue;                       // an original file that the PS4 has as well
    if (replaced && there == null) Log("  remplacé par le patch FR mais absent côté PS4 : " + rel);
    string dst = Path.Combine(outGames, p4.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(dst));
    File.Copy(f, dst, true);
    copied++;
    Log("  " + (replaced ? "remplacé par le patch FR" : "ajouté par le patch FR") + " : " + rel + " -> games/" + p4 + " (" + N(new FileInfo(f).Length) + " octets)");
}
Log("  " + copied + " fichiers copiés");

Step("fin");
Log(done + " fichiers de données sur " + units.Count + " ; " + problems + " point(s) à regarder (lignes marquées !!)");
Log("Dossier à envoyer sur la PS4 : " + Path.Combine(outRoot, titleId) + "  ->  /data/GoldHEN/AFR/" + titleId);
File.WriteAllText(Path.Combine(outRoot, "plugins.ini-a-ajouter.txt"),
    "; Lignes à ajouter dans /data/GoldHEN/plugins.ini sur la PS4 (crée le fichier s'il n'existe pas) :\r\n[" + titleId + "]\r\n/data/GoldHEN/plugins/afr.prx\r\n");
logW.Close();
Console.WriteLine("@@done " + done + " " + units.Count + " " + problems);
