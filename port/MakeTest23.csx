// MakeTest23.csx - a second, small test set in the format of the real game (GameMaker 2023.6: functions inside global
// scripts), built with the GML compiler: the PC original, the PC file with a "French patch" and the PS4 file of the
// chapter selection and of chapter 1. It reproduces how the French patch starts itself only where the PC game starts.
//   UndertaleModCli load <any data.win> -s MakeTest23.csx      with DRFR_TEST set to the output folder
using System;
using System.IO;
using System.Linq;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Compiler;

string root = Environment.GetEnvironmentVariable("DRFR_TEST");

UndertaleData Make(bool ps4, bool fr, string chapter) {
    var d = UndertaleData.CreateNew();
    d.SetGMS2Version(2, 3);
    d.GeneralInfo.Config = d.Strings.MakeString(ps4 ? "PS4" : "Default");
    d.GeneralInfo.DisplayName = d.Strings.MakeString(ps4 ? "" : "DELTARUNE" + (chapter == "" ? "" : " Chap" + (fr ? "itre " : "ter ") + chapter));
    d.GeneralInfo.Timestamp = 1785378143UL + (ulong)(ps4 ? 100 : 0);
    var obj = new UndertaleGameObject { Name = d.Strings.MakeString("obj_initializer2") };
    d.GameObjects.Add(obj);
    foreach (string f in new[] { "method", "@@This@@", "@@Other@@", "@@Global@@", "@@NullObject@@", "@@NewGMLArray@@", "@@NewGMLObject@@", "@@CopyStatic@@", "@@SetStatic@@", "ds_map_create" })
        d.Functions.EnsureDefined(f, d.Strings);
    var g = new CodeImportGroup(d);
    g.QueueReplace("gml_GlobalScript_scr_gamestart", "function scr_gamestart()\n{\n    global.started = 1;\n}\n");
    g.QueueReplace("gml_GlobalScript_snd_play", "function snd_play(arg0)\n{\n    " + (fr ? "arg0 = scr_drfr_get_soundloc(arg0);\n    " : "") + "return arg0;\n}\n");
    g.QueueReplace("gml_GlobalScript_scr_title", "function scr_title()\n{\n    return \"" + (ps4 ? "CUSA52463_00" : "") + "\" + \"" + (fr ? "Bonjour" : "Hello") + "\";\n}\n");
    if (fr) {
        g.QueueReplace("gml_GlobalScript_scr_drfr_init_localization", "function scr_drfr_init_localization()\n{\n    global.drfr_prefered_audio = \"fr\";\n}\n\nfunction scr_drfr_get_soundloc(arg0)\n{\n    if (global.drfr_prefered_audio == \"en\")\n    {\n        return arg0;\n    }\n    return arg0 + 1;\n}\n");
        g.QueueReplace("gml_GlobalScript_init_drfr", "function init_drfr()\n{\n    global.drfr = 1;\n    scr_drfr_init_localization();\n}\n");
    }
    g.QueueReplace("gml_Object_obj_initializer2_Create_0",
        "global.is_console = os_type == os_ps4;\nif (global.is_console)\n{\n    global.loading = 1;\n}\nelse\n{\n    scr_gamestart();\n" + (fr ? "    scr_drfr_init_localization();\n    init_drfr();\n" : "") + "    global.ready = 1;\n}\n");
    g.QueueReplace("gml_Object_obj_initializer2_Other_72", "if (global.is_console)\n{\n    global.savedata = ds_map_create();\n    scr_gamestart();\n    global.ready = 1;\n    snd_play(3);\n}\n");
    g.Import();
    return d;
}

void Save(UndertaleData d, string path) {
    Directory.CreateDirectory(Path.GetDirectoryName(path));
    using (var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite)) UndertaleIO.Write(fs, d, m => { });
    Console.WriteLine("wrote " + path);
}

foreach (string ch in new[] { "", "1" }) {
    string pc = Path.Combine(root, "pc", ch == "" ? "" : "chapter" + ch + "_windows");
    Save(Make(false, false, ch), Path.Combine(pc, "data.win.bak"));
    Save(Make(false, true, ch), Path.Combine(pc, "data.win"));
    Save(Make(true, false, ch), Path.Combine(root, "ps4", "CUSA52463-app0", "games", ch == "" ? "" : "chapter" + ch + "_ps4", "game.win"));
}
File.WriteAllText(Path.Combine(root, "pc", "DELTARUNE.exe"), "MZ");
