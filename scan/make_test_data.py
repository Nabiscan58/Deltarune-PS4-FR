#!/usr/bin/env python3
"""Synthetic GameMaker data files (same layout as the real ones, tiny and made up) to test DeltaruneScan without any
game file: make_test_data.py <out dir> writes <out>/ps4 and <out>/pc."""
import json
import os
import struct
import sys


def u32(v):
    return struct.pack("<I", v & 0xFFFFFFFF)


class Build:
    """Two passes: the first with dummy string pointers to learn every size, the second with the real ones."""

    def __init__(self, spec):
        self.spec = spec

    def data(self):
        self.strings = []
        self.strpos = {}
        self.pass1 = True
        self.layout()
        self.pass1 = False
        return self.layout()

    def s(self, text):
        if text not in self.strpos and self.pass1:
            self.strings.append(text)
            self.strpos[text] = 0
        return self.strpos[text]

    def ptrlist(self, base, items, prefix=b""):
        """count, pointers, then the items one after another (each a function of its own offset)."""
        out = bytearray(prefix + u32(len(items)) + b"\0" * 4 * len(items))
        for i, f in enumerate(items):
            off = base + len(out)
            struct.pack_into("<I", out, len(prefix) + 4 + 4 * i, off)
            out += f(off)
        return bytes(out)

    def layout(self):
        sp = self.spec
        chunks = []
        pos = 8

        def add(name, maker):
            nonlocal pos
            body = maker(pos + 8)
            body += b"\0" * (-len(body) % 16)
            chunks.append((name, body))
            pos += 8 + len(body)

        def gen8(base):
            b = bytes([1, 17, 0, 0]) + u32(self.s(sp["file"])) + u32(self.s("Default")) + u32(100000) + u32(10000000) + u32(1234)
            b += b"\0" * 16 + u32(self.s(sp["name"])) + u32(2) + u32(0) + u32(0) + u32(0) + u32(640) + u32(480) + u32(0x1234)
            b += u32(0) + b"\0" * 16 + struct.pack("<Q", 1750000000) + u32(self.s(sp["display"])) + struct.pack("<QQ", 0, 0x10)
            b += struct.pack("<i", sp.get("steam", 0)) + u32(0) + u32(2) + u32(0) + u32(1)
            return b

        def named(names, extra=b"\0" * 12):
            return lambda base: self.ptrlist(base, [lambda off, n=n: u32(self.s(n)) + extra for n in names])

        def fonts(base):
            def font(name, chars):
                def f(off):
                    head = u32(self.s(name)) + u32(self.s("Font " + name)) + u32(12) + u32(0) + u32(0) + struct.pack("<HBB", 32, 0, 1)
                    head += u32(255) + u32(0) + struct.pack("<ff", 1.0, 1.0) + u32(0)
                    return head + self.ptrlist(off + len(head), [lambda o, c=c: struct.pack("<HHHHHhh", c, 0, 0, 8, 8, 8, 0) + struct.pack("<H", 0) for c in chars])
                return f
            return self.ptrlist(base, [font(n, c) for n, c in sp["fonts"]])

        def code(base):
            if not sp["code"]:
                return b""
            return self.ptrlist(base, [lambda off, n=n, l=l: u32(self.s(n)) + u32(l) + struct.pack("<HH", 1, 0) + u32(0) + u32(0) for n, l in sp["code"]])

        def func(base):
            if not sp["code"]:
                return b""
            return u32(len(sp["funcs"])) + b"".join(u32(self.s(n)) + u32(1) + u32(0) for n in sp["funcs"]) + u32(0)

        def strg(base):
            out = bytearray(u32(len(self.strings)) + b"\0" * 4 * len(self.strings))
            for i, t in enumerate(self.strings):
                raw = t.encode("utf-8")
                struct.pack_into("<I", out, 4 + 4 * i, base + len(out))
                self.strpos[t] = base + len(out) + 4
                out += u32(len(raw)) + raw + b"\0"
                out += b"\0" * (-len(out) % 4)
            return bytes(out)

        def txtr(base):
            blobs = sp["textures"]
            n = len(blobs)
            esize = 28
            out = bytearray(u32(n) + b"".join(u32(base + 4 + 4 * n + esize * i) for i in range(n)))
            start = len(out) + esize * n
            offs, cur = [], start
            for b in blobs:
                cur += -cur % 128
                offs.append(cur)
                cur += len(b)
            for i, b in enumerate(blobs):
                out += u32(0) + u32(0) + u32(len(b)) + u32(2048) + u32(2048) + u32(i) + u32(base + offs[i] if b else 0)
            for i, b in enumerate(blobs):
                out += b"\0" * (offs[i] - len(out)) + b
            return bytes(out)

        add("GEN8", gen8)
        add("OPTN", lambda base: b"\0" * 32)
        add("EXTN", lambda base: self.ptrlist(base, [lambda off, n=n: u32(self.s("")) + u32(self.s(n)) + u32(self.s("")) + u32(0) for n in sp["ext"]]))
        add("SOND", named(sp["sounds"]))
        add("AGRP", named(["audiogroup_default"], b""))
        add("SPRT", named(sp["sprites"]))
        add("BGND", named(["bg_a"]))
        add("SCPT", named(["scr_a", "scr_b"], u32(0)))
        add("FONT", fonts)
        add("OBJT", named(sp["objects"]))
        add("ROOM", named(["room_a", "room_b"]))
        add("TPAG", lambda base: self.ptrlist(base, [lambda off: b"\0" * 22] * 5))
        add("CODE", code)
        add("FUNC", func)
        add("TGIN", lambda base: self.ptrlist(base, [lambda off, n=n: u32(self.s(n)) + b"\0" * 16 for n in ["Default", "tg_fonts"]], u32(1)))
        for t in sp["strings"]:
            self.s(t)
        add("STRG", strg)
        add("TXTR", txtr)
        add("AUDO", lambda base: self.ptrlist(base, [lambda off: u32(4) + b"RIFF"] * 2))
        body = b"".join(n.encode() + u32(len(b)) + b for n, b in chunks)
        return b"FORM" + u32(len(body)) + body


def png(w, h):
    return b"\x89PNG\r\n\x1a\n" + struct.pack(">I", 13) + b"IHDR" + struct.pack(">II", w, h) + b"\x08\x06\0\0\0" + b"\0" * 40


def qoi(w, h):
    return b"2zoq" + struct.pack("<HH", w, h) + u32(100) + b"BZh9" + b"\0" * 40


def sfo(entries):
    keys, data, index = b"", b"", b""
    for k, v in entries:
        if isinstance(v, int):
            raw, fmt, ln, mx = u32(v), 0x0404, 4, 4
        else:
            raw = v.encode() + b"\0"
            fmt, ln, mx = 0x0204, len(raw), len(raw) + (-len(raw) % 4)
            raw += b"\0" * (mx - ln)
        index += struct.pack("<HHIII", len(keys), fmt, ln, mx, len(data))
        keys += k.encode() + b"\0"
        data += raw
    keys += b"\0" * (-len(keys) % 4)
    kt = 20 + len(index)
    return b"\0PSF" + u32(0x101) + u32(kt) + u32(kt + len(keys)) + u32(len(entries)) + index + keys + data


def write(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(data)


def main(out):
    ascii_chars = list(range(32, 127))
    en = dict(
        file="DELTARUNE", name="DELTARUNE", display="DELTARUNE Chapter 1", ext=["PS4Trophies"], sounds=["snd_a", "snd_b"],
        sprites=["spr_kris", "spr_logo", "spr_ps4_buttons"], objects=["obj_a", "obj_b"],
        fonts=[("fnt_main", ascii_chars), ("fnt_ja_main", ascii_chars + list(range(0x3041, 0x3090)))],
        code=[("gml_Object_obj_a_Create_0", 120), ("gml_Object_obj_b_Step_0", 400), ("gml_Script_scr_84_lang_load", 64), ("gml_Script_scr_ps4_only", 8)],
        funcs=["show_debug_message", "psn_init_np_libs", "json_decode", "file_text_open_read", "os_get_language"],
        strings=["* Hello there, this is a test line.", "v1.40", "lang_en.json", "lang/", "is_console", "os_ps4", "* Another line of dialogue here."],
        textures=[png(2048, 2048), qoi(1024, 512)])
    fr = dict(en, ext=["Steamworks"], steam=1671210, sprites=["spr_kris", "spr_logo", "spr_logo_fr"],
              fonts=[("fnt_main", ascii_chars + [0xE9, 0xE8, 0xE0, 0xE7, 0x153]), ("fnt_ja_main", ascii_chars + list(range(0x3041, 0x3090)))],
              code=[("gml_Object_obj_a_Create_0", 120), ("gml_Object_obj_b_Step_0", 440), ("gml_Script_scr_84_lang_load", 64), ("gml_Script_scr_fr_extra", 30)],
              funcs=["show_debug_message", "steam_initialised", "json_decode", "file_text_open_read", "font_add_sprite_ext"],
              strings=["* Bonjour, ceci est une ligne de test très accentuée.", "v1.40", "lang_fr.json", "lang/", "is_console", "os_ps4",
                       "* Une autre réplique de dialogue ici.", "https://deltarune-fr.com"],
              textures=[png(2048, 2048), qoi(1024, 512), png(512, 512)])
    select_ps4 = dict(en, display="DELTARUNE", fonts=[("fnt_main", ascii_chars)], code=[], funcs=[], textures=[png(64, 64), b""])
    select_pc = dict(fr, display="DELTARUNE")

    ps4 = os.path.join(out, "ps4", "CUSA52463-app0")
    write(os.path.join(ps4, "games", "game.win"), Build(select_ps4).data())
    write(os.path.join(ps4, "games", "chapter1", "game.win"), Build(en).data())
    write(os.path.join(ps4, "games", "chapter2", "game.win"), Build(dict(en, display="DELTARUNE Chapter 2")).data()[:-900])  # a cut copy
    write(os.path.join(ps4, "games", "chapter1", "audiogroup1.dat"), b"FORM" + u32(16) + b"AUDO" + u32(8) + u32(0) + u32(0))
    write(os.path.join(ps4, "eboot.bin"), b"\x4f\x15\x3d\x1d" + b"\0" * 60)
    write(os.path.join(ps4, "sce_sys", "param.sfo"), sfo([("APP_VER", "01.05"), ("CATEGORY", "gd"), ("CONTENT_ID", "UP3893-CUSA52463_00-DELTARUNE0000000"),
                                                        ("SYSTEM_VER", 0x09008001), ("TITLE", "DELTARUNE"), ("TITLE_ID", "CUSA52463"), ("VERSION", "01.00")]))
    lang_en = {"date": "20250804", "obj_a_slash_Create_0_gml_1_0": "* Hello there, this is a test line.", "only_ps4_key": "Press \\[OPTIONS] to continue"}
    lang_ja = {"date": "20250804", "obj_a_slash_Create_0_gml_1_0": "* こんにちは、これはテストです。"}
    write(os.path.join(ps4, "games", "chapter1", "lang", "lang_en.json"), json.dumps(lang_en, ensure_ascii=False, indent=1).encode())
    write(os.path.join(ps4, "games", "chapter1", "lang", "lang_ja.json"), json.dumps(lang_ja, ensure_ascii=False, indent=1).encode())
    for i in range(55):
        write(os.path.join(ps4, "games", "chapter1", "mus", "song%02d.ogg" % i), b"OggS" + bytes(100))

    pc = os.path.join(out, "pc")
    write(os.path.join(pc, "DELTARUNE.exe"), b"MZ" + b"\0" * 62)
    write(os.path.join(pc, "data.win"), Build(select_pc).data())
    write(os.path.join(pc, "chapter1_windows", "data.win"), Build(fr).data())
    write(os.path.join(pc, "chapter1_windows", "options.ini"), b"[Windows]\r\nSleepMargin=10\r\n")
    lang_fr = {"date": "20250804", "obj_a_slash_Create_0_gml_1_0": "* Bonjour, ceci est une ligne de test très accentuée.", "extra_fr": "Traduit par l'équipe"}
    write(os.path.join(pc, "chapter1_windows", "lang", "lang_en.json"), json.dumps(lang_fr, ensure_ascii=False, indent=1).encode())
    write(os.path.join(pc, "chapter1_windows", "lang", "lang_ja.json"), json.dumps(lang_ja, ensure_ascii=False, indent=1).encode())


if __name__ == "__main__":
    main(sys.argv[1])
