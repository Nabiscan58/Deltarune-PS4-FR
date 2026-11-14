// MakeTest.csx - made-up data files to test Port.csx without any game file (run by UndertaleModCli on any data file):
// writes <DRFR_TEST>/pc/... (data.win.bak = "PC-EN", data.win = "PC-FR") and <DRFR_TEST>/ps4/CUSA52463-patch0/...
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Util;

string root = Environment.GetEnvironmentVariable("DRFR_TEST");

UndertaleData Make(string variant, string chapter) {
    bool ps4 = variant == "ps4", fr = variant == "fr";
    var d = UndertaleData.CreateNew();
    d.SetGMS2Version(2, 2, 1);          // texture groups (TGIN) exist from GameMaker Studio 2.2.1
    d.GeneralInfo.Config = d.Strings.MakeString(ps4 ? "PS4" : "Default");
    d.GeneralInfo.DisplayName = d.Strings.MakeString(fr ? "DELTARUNE Chapitre " + chapter : ps4 ? "" : "DELTARUNE Chapter " + chapter);
    d.GeneralInfo.Info = ps4 ? UndertaleGeneralInfo.InfoFlags.Scale : UndertaleGeneralInfo.InfoFlags.Scale | UndertaleGeneralInfo.InfoFlags.ScreenKey;
    d.GeneralInfo.Timestamp = 1785378000UL + (ulong)(ps4 ? 0 : 60);
    if (ps4) d.Options.Scale = 3;

    foreach (string f in new[] { "show_debug_message", ps4 ? "extension_stubfunc_real" : "shader_replace_simple_raw", "gml_Script_convert_sakushi_flags" }) d.Functions.EnsureDefined(f, d.Strings);
    foreach (string vn in new[] { "x", "y", "drfr", "fps_target", "z", "q" }) {
        int id;
        var vs = d.Strings.MakeString(vn, out id);
        d.Variables.EnsureDefined(vs, id, UndertaleInstruction.InstanceType.Self, false, d);
    }
    Action<string, string> code = (name, src) => {
        var c = UndertaleCode.CreateEmptyEntry(d, name);
        c.Replace(Assembler.Assemble(src, d));
    };
    string say = "conv.s.v\ncall.i show_debug_message(argc=1)\npopz.v\n";
    code("gml_Script_scr_title", "push.s \"" + (ps4 ? "CUSA52463_00" : "PCTITLE") + "\"\n" + say + "push.s \"" + (fr ? "Bonjour à tous" : "Hello everyone") + "\"\n" + say);
    code("gml_Script_scr_shader", "pushi.e 1\nconv.i.v\ncall.i " + (ps4 ? "extension_stubfunc_real" : "shader_replace_simple_raw") + "(argc=1)\npopz.v\n");
    code("gml_Script_scr_touched",
         (fr ? "push.s \"ajout du patch FR\"\n" + say + "pushi.e 7\npop.v.i self.drfr\n" : "") +
         "push.s \"" + (fr ? "Â" : "A") + "\"\n" + say + "push.s \"" + (ps4 ? "PS4ID" : "PCID") + "\"\n" + say + "pushi.e 5\npop.v.i self.x\n" +
         "push.s \"" + (ps4 ? "Press (X)" : fr ? "Appuyez sur [Z]" : "Press [Z]") + "\"\n" + say +
         "pushi.e " + (ps4 ? "30" : "60") + "\npop.v.i self.fps_target\n");
    code("gml_Script_scr_same", "pushi.e 2\npop.v.i self.y\npush.s \"" + (fr ? "Pareil" : "Same") + "\"\n" + say);
    if (!ps4) code("gml_Script_pc_only_wrapper", "pushi.e 3\nconv.i.v\ncall.i shader_replace_simple_raw(argc=1)\npopz.v\n");
    if (fr) code("gml_Script_fr_extra", "push.s \"Français\"\n" + say + "pushi.e 4\nconv.i.v\ncall.i shader_replace_simple_raw(argc=1)\npopz.v\n");
    if (fr) {
        code("gml_Script_convert_sakushi_flags", "pushi.e 5\npop.v.i self.q\n");
        code("gml_Script_init_drfr", "call.i gml_Script_convert_sakushi_flags(argc=0)\npopz.v\n");
    }
    if (chapter == "2" && ps4) code("gml_Script_ps4_only", "pushi.e 9\npop.v.i self.z\n");
    if (chapter == "2") code("gml_Script_structural", ps4 ? "pushi.e 1\npop.v.i self.q\npushi.e 2\npop.v.i self.q\n" : "pushi.e 1\npop.v.i self.q\n");

    Action<string, bool> ext = (name, dll) => {
        var e = new UndertaleExtension { FolderName = d.Strings.MakeString(""), Name = d.Strings.MakeString(name), ClassName = d.Strings.MakeString("") };
        var f = new UndertaleExtensionFile { Filename = d.Strings.MakeString(name + (dll ? "_x64.dll" : ".gml")), CleanupScript = d.Strings.MakeString(""), InitScript = d.Strings.MakeString(""),
                                             Kind = dll ? UndertaleExtensionKind.Dll : UndertaleExtensionKind.GML };
        if (dll) f.Functions.Add(new UndertaleExtensionFunction { Name = d.Strings.MakeString("shader_replace_simple_raw"), ID = 1, Kind = 12, RetType = UndertaleExtensionVarType.Double,
                                                                 ExtName = d.Strings.MakeString("shader_replace_simple_raw") });
        e.Files.Add(f);
        d.Extensions.Add(e);
        d.FORM.EXTN.productIdData.Add(Enumerable.Repeat((byte)(name[0] + (ps4 ? 1 : 0)), 16).ToArray());   // GameMaker Studio 2 files have one per extension
    };
    if (!ps4) ext("shader_replace_simple", true);
    ext("Easing_and_Tweening", false);

    var sh = new UndertaleShader {
        Name = d.Strings.MakeString("shd_test"), Type = ps4 ? UndertaleShader.ShaderType.PSSL : UndertaleShader.ShaderType.HLSL11,
        GLSL_ES_Vertex = d.Strings.MakeString("void main() { }"), GLSL_ES_Fragment = d.Strings.MakeString("void main() { gl_FragColor = vec4(1.0); }"),
        GLSL_Vertex = d.Strings.MakeString(""), GLSL_Fragment = d.Strings.MakeString(""), HLSL9_Vertex = d.Strings.MakeString(""), HLSL9_Fragment = d.Strings.MakeString("") };
    Func<bool, byte, UndertaleShader.UndertaleRawShaderData> rawd = (on, b) => new UndertaleShader.UndertaleRawShaderData { IsNull = !on, Data = on ? Enumerable.Repeat(b, 64).ToArray() : null };
    sh.HLSL11_VertexData = rawd(!ps4, 0x11); sh.HLSL11_PixelData = rawd(!ps4, 0x12);
    sh.PSSL_VertexData = rawd(ps4, 0x41); sh.PSSL_PixelData = rawd(ps4, 0x42);
    sh.Cg_PSVita_VertexData = rawd(false, 0); sh.Cg_PSVita_PixelData = rawd(false, 0); sh.Cg_PS3_VertexData = rawd(false, 0); sh.Cg_PS3_PixelData = rawd(false, 0);
    sh.VertexShaderAttributes.Add(new UndertaleShader.VertexShaderAttribute { Name = d.Strings.MakeString("in_Position") });
    d.Shaders.Add(sh);

    // As the screen borders of the real game: a sprite whose image only the PS4 file has, in a texture group ("console")
    // the PC file does not have. On PC the sprite is there with nothing behind it.
    var tgin = new UndertaleChunkTGIN();
    var order = d.FORM.Chunks.ToList();           // TGIN goes before CODE, as in a real file (and before STRG: strings are written last)
    d.FORM.Chunks.Clear();
    foreach (var kv in order) { if (kv.Key == "CODE") d.FORM.Chunks["TGIN"] = tgin; d.FORM.Chunks[kv.Key] = kv.Value; }
    var tgDefault = new UndertaleTextureGroupInfo { Name = d.Strings.MakeString("default") };
    Func<string, UndertaleEmbeddedTexture, UndertaleSprite> sprite = (name, page) => {
        var sp = new UndertaleSprite { Name = d.Strings.MakeString(name), Width = 8, Height = 8 };
        var e = new UndertaleSprite.TextureEntry();
        if (page != null) {
            e.Texture = new UndertaleTexturePageItem { SourceX = 1, SourceY = 2, SourceWidth = 8, SourceHeight = 8, TargetWidth = 8, TargetHeight = 8, BoundingWidth = 8, BoundingHeight = 8, TexturePage = page };
            d.TexturePageItems.Add(e.Texture);
        }
        sp.Textures.Add(e);
        d.Sprites.Add(sp);
        return sp;
    };
    Func<UndertaleEmbeddedTexture> newPage = () => {
        var t = new UndertaleEmbeddedTexture { TextureWidth = 16, TextureHeight = 16 };
        t.TextureData.Image = new GMImage(16, 16).ConvertToPng();
        d.EmbeddedTextures.Add(t);
        return t;
    };
    var p0 = newPage();
    tgDefault.TexturePages.Add(new UndertaleResourceById<UndertaleEmbeddedTexture, UndertaleChunkTXTR>(p0));
    tgDefault.Sprites.Add(new UndertaleResourceById<UndertaleSprite, UndertaleChunkSPRT>(sprite("spr_kris", p0)));
    if (fr) tgDefault.Sprites.Add(new UndertaleResourceById<UndertaleSprite, UndertaleChunkSPRT>(sprite("spr_logo_fr", newPage())));
    if (ps4) {
        var tgConsole = new UndertaleTextureGroupInfo { Name = d.Strings.MakeString("console") };
        var p1 = newPage();
        tgConsole.TexturePages.Add(new UndertaleResourceById<UndertaleEmbeddedTexture, UndertaleChunkTXTR>(p1));
        tgConsole.Sprites.Add(new UndertaleResourceById<UndertaleSprite, UndertaleChunkSPRT>(sprite("spr_border", p1)));
        tgin.List.Add(tgConsole);
    }
    else sprite("spr_border", null);
    tgin.List.Add(tgDefault);
    return d;
}

void Save(UndertaleData d, string path) {
    Directory.CreateDirectory(Path.GetDirectoryName(path));
    using (var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite)) UndertaleIO.Write(fs, d, m => { });
    Console.WriteLine("wrote " + path);
}

foreach (string ch in new[] { "", "1", "2" }) {
    string pc = Path.Combine(root, "pc", ch == "" ? "" : "chapter" + ch + "_windows"), ps4 = Path.Combine(root, "ps4", "CUSA52463-patch0", "games", ch == "" ? "" : "chapter" + ch + "_ps4");
    Save(Make("en", ch), Path.Combine(pc, "data.win.bak"));
    Save(Make("fr", ch), Path.Combine(pc, "data.win"));
    Save(Make("ps4", ch), Path.Combine(ps4, "game.win"));
}
Directory.CreateDirectory(Path.Combine(root, "ps4", "CUSA52463-app0", "games", "chapter1_ps4", "lang"));
Directory.CreateDirectory(Path.Combine(root, "pc", "chapter1_windows", "lang"));
Directory.CreateDirectory(Path.Combine(root, "pc", "chapter2_windows", "vid"));
Directory.CreateDirectory(Path.Combine(root, "pc", "mus"));
Directory.CreateDirectory(Path.Combine(root, "ps4", "CUSA52463-app0", "games", "mus"));
File.WriteAllText(Path.Combine(root, "ps4", "CUSA52463-app0", "games", "chapter1_ps4", "lang", "lang_en.json"), "{\"a\":\"Hello\"}");
File.WriteAllText(Path.Combine(root, "pc", "chapter1_windows", "lang", "lang_en.json"), "{\"a\":\"Bonjour\"}");
File.WriteAllText(Path.Combine(root, "pc", "chapter1_windows", "lang", "lang_en.json.bak"), "{\"a\":\"Hello\"}");
File.WriteAllText(Path.Combine(root, "pc", "chapter2_windows", "vid", "intro_fr.mp4"), "video");
File.WriteAllText(Path.Combine(root, "pc", "mus", "same.ogg"), "ogg");
File.WriteAllText(Path.Combine(root, "pc", "mus", "new_fr.ogg"), "ogg");
File.WriteAllText(Path.Combine(root, "ps4", "CUSA52463-app0", "games", "mus", "same.ogg"), "ogg");
File.WriteAllText(Path.Combine(root, "pc", "DELTARUNE.exe"), "exe");
