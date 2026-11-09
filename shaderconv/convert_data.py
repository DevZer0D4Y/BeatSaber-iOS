#!/usr/bin/env python3
"""Convert a Unity 2019.4 Windows player Data folder for an Apple Metal player.

For every Shader: translate the non-VR DX11 VS/PS variants to Metal (HLSLcc), add
them as platform 14 (blob + parsed-form records). Every serialized file is
re-targeted to the given BuildTarget. Other files are copied unchanged.

usage: convert_data.py <src Data> <dst Data> <hlslcc_metal|none> <target: osx|ios> [only-file]
For ios, BC/DXT textures are also transcoded to ASTC 4x4 (stored inline).
"""
import sys, os, json, struct, shutil, subprocess, collections, copy, lz4.block
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import UnityPy
from UnityPy.enums import ClassIDType
from unity_shader import (blob_entries, parse_subprogram, write_subprogram, dxbc_with_rdef,
                          to_metal, HLSLCC_FLAGS, METAL_TYPE)

STEREO = {"UNITY_SINGLE_PASS_STEREO", "STEREO_INSTANCING_ON", "STEREO_MULTIVIEW_ON"}
TARGETS = {"osx": 2, "ios": 9}
SEGMENT_LIMIT = 4 << 20          # uncompressed bytes per Metal blob segment
PLATFORM_METAL = 14
ASTC_QUALITY = float(os.environ.get("ASTC_QUALITY", "1.0"))
SKIP_EXT = (".resS", ".resource")


class Translator:
    def __init__(self, exe):
        self.exe, self._p = exe, None

    @property
    def p(self):
        if self._p is None:
            if self.exe == "none": raise RuntimeError("shader translation disabled")
            self._p = subprocess.Popen([self.exe], stdin=subprocess.PIPE, stdout=subprocess.PIPE)
        return self._p

    def run(self, dxbc):
        self.p.stdin.write(struct.pack("<II", HLSLCC_FLAGS, len(dxbc)) + dxbc); self.p.stdin.flush()
        n = struct.unpack("<I", self.p.stdout.read(4))[0]
        return json.loads(self.p.stdout.read(n))


def platform_segments(tt, plat):
    i = tt["platforms"].index(plat)
    offs, cls, dls = tt["offsets"][i], tt["compressedLengths"][i], tt["decompressedLengths"][i]
    if not isinstance(offs, list): offs, cls, dls = [offs], [cls], [dls]
    cb = bytes(tt["compressedBlob"])
    return [lz4.block.decompress(cb[a:a + b], uncompressed_size=c) for a, b, c in zip(offs, cls, dls)]


def pack_segments(programs):
    """Split serialized sub-programs into segments; segment 0 starts with the entry table."""
    table = []; segs = [bytearray()]; head = 4 + 12 * len(programs)
    pos = head
    for data in programs:
        if pos + len(data) > SEGMENT_LIMIT and pos > (head if len(segs) == 1 else 0):
            segs.append(bytearray()); pos = 0
        table.append((pos, len(data), len(segs) - 1))
        segs[-1] += data; pos += len(data)
    seg0 = bytearray(struct.pack("<i", len(programs)))
    for off, ln, sg in table: seg0 += struct.pack("<III", off, ln, sg)
    segs[0] = seg0 + segs[0]
    return [bytes(s) for s in segs]


class NameTable:
    def __init__(self, pass_):
        self.p = pass_
        self.map = {n: i for n, i in pass_["m_NameIndices"]}
        self.next = max(self.map.values(), default=-1) + 1

    def __call__(self, name):
        if name not in self.map:
            self.map[name] = self.next; self.p["m_NameIndices"].append((name, self.next)); self.next += 1
        return self.map[name]


def vec(p, nm):
    return {"m_NameIndex": nm(p["name"]), "m_Index": p["offset"], "m_ArraySize": p["array"],
            "m_Type": p["type"], "m_Dim": p["cols"]}


def mat(p, nm):
    return {"m_NameIndex": nm(p["name"]), "m_Index": p["offset"], "m_ArraySize": p["array"],
            "m_Type": p["type"], "m_RowCount": p["rows"]}


def metal_record(dx, mt, blob_index, nm):
    rec = copy.deepcopy(dx)
    rec["m_BlobIndex"] = blob_index
    rec["m_GpuProgramType"] = mt["type"]
    rec["m_Channels"] = {"m_Channels": [{"source": s, "target": t} for s, t in mt["channels"]],
                         "m_SourceMap": mt["source_map"]}
    g0 = mt["groups"][0]
    rec["m_VectorParams"] = [vec(p, nm) for p in g0["params"] if not p["matrix"]]
    rec["m_MatrixParams"] = [mat(p, nm) for p in g0["params"] if p["matrix"]]
    cbs = []
    for g in mt["groups"][1:]:
        cbs.append({"m_NameIndex": nm(g["name"]),
                    "m_MatrixParams": [mat(p, nm) for p in g["params"] if p["matrix"]],
                    "m_VectorParams": [vec(p, nm) for p in g["params"] if not p["matrix"]],
                    "m_StructParams": [{"m_NameIndex": nm(st["name"]), "m_Index": st["index"],
                                        "m_ArraySize": st["array"], "m_StructSize": st["size"],
                                        "m_VectorMembers": [vec(p, nm) for p in st["members"] if not p["matrix"]],
                                        "m_MatrixMembers": [mat(p, nm) for p in st["members"] if p["matrix"]]}
                                       for st in g["structs"]],
                    "m_Size": g["size"]})
    rec["m_ConstantBuffers"] = cbs
    rec["m_TextureParams"] = []; rec["m_ConstantBufferBindings"] = []
    rec["m_BufferParams"] = []; rec["m_UAVParams"] = []; rec["m_Samplers"] = []
    for b in mt["bindings"]:
        if b["kind"] == 0:
            rec["m_TextureParams"].append({"m_NameIndex": nm(b["name"]), "m_Index": b["index"],
                                           "m_SamplerIndex": b["extra"], "m_MultiSampled": bool(b["tex"] & 1),
                                           "m_Dim": b["tex"] >> 1})
        elif b["kind"] == 1:
            rec["m_ConstantBufferBindings"].append({"m_NameIndex": nm(b["name"]), "m_Index": b["index"]})
        elif b["kind"] == 2:
            rec["m_BufferParams"].append({"m_NameIndex": nm(b["name"]), "m_Index": b["index"], "m_ArraySize": 0})
        elif b["kind"] == 3:
            rec["m_UAVParams"].append({"m_NameIndex": nm(b["name"]), "m_Index": b["index"],
                                       "m_OriginalIndex": b["extra"]})
        elif b["kind"] == 4:
            rec["m_Samplers"].append({"sampler": b["extra"], "bindPoint": b["index"]})
    return rec


def convert_shader(tt, tr, stats):
    if 4 not in tt["platforms"] or PLATFORM_METAL in tt["platforms"]:
        stats["shader_untouched"] += 1; return False
    segs = platform_segments(tt, 4)
    entries = blob_entries(segs[0])
    cache = {}           # dx blob index -> (metal blob index) | None
    programs = []

    def get_metal(idx):
        if idx in cache: return cache[idx]
        o, ln, sg = entries[idx]
        sp = parse_subprogram(segs[sg], o, ln)
        out = None
        if not (STEREO & set(sp["keywords"] + sp["local_keywords"])) and sp["type"] in METAL_TYPE:
            try:
                res = tr.run(dxbc_with_rdef(sp["code"], sp))
                if res["ok"] and res["msl"]:
                    mt = to_metal(sp, res)
                    programs.append(write_subprogram(mt))
                    out = (len(programs) - 1, mt)
                    stats["converted"] += 1
                else:
                    stats["hlslcc_fail"] += 1
            except Exception as e:
                stats["error: " + str(e)[:60]] += 1
        else:
            stats["skipped"] += 1
        cache[idx] = out
        return out

    pf = tt["m_ParsedForm"]
    for ss in pf["m_SubShaders"]:
        for pass_ in ss["m_Passes"]:
            nm = NameTable(pass_)
            for prog in ("progVertex", "progFragment"):
                subs = pass_[prog]["m_SubPrograms"]
                new = []
                for rec in subs:
                    new.append(rec)
                    if rec["m_GpuProgramType"] in METAL_TYPE:
                        got = get_metal(rec["m_BlobIndex"])
                        if got:
                            new.append(metal_record(rec, got[1], got[0], nm))
                pass_[prog]["m_SubPrograms"] = new
    if not programs:
        stats["shader_no_metal"] += 1; return False
    msegs = pack_segments(programs)
    blob = bytearray(bytes(tt["compressedBlob"]))
    offs, clens, dlens = [], [], []
    for sgm in msegs:
        c = lz4.block.compress(sgm, store_size=False)
        offs.append(len(blob)); clens.append(len(c)); dlens.append(len(sgm)); blob += c
    tt["platforms"].append(PLATFORM_METAL)
    tt["offsets"].append(offs); tt["compressedLengths"].append(clens); tt["decompressedLengths"].append(dlens)
    tt["compressedBlob"] = bytes(blob)
    stats["shader_converted"] += 1
    return True


# ---------------------------------------------------------------- textures (iOS)
ASTC_4x4 = 48            # TextureFormat.ASTC_4x4 (ASTC_RGB_4x4 in older enums; carries alpha)
BC_FORMATS = {10: ("bc1", 8), 12: ("bc3", 16), 25: ("bc7", 16), 26: ("bc4", 8), 27: ("bc5", 16),
              28: ("crunch_dxt1", 0), 29: ("crunch_dxt5", 0)}


def _bgra_to_rgba(b):
    o = bytearray(b); o[0::4], o[2::4] = b[2::4], b[0::4]; return bytes(o)


def _read_texture_data(tt, file_dir, assets_file=None):
    sd = tt.get("m_StreamData") or {}
    if sd.get("size"):
        p = os.path.join(file_dir, sd["path"].split("/")[-1])
        if os.path.exists(p):
            with open(p, "rb") as fh:
                fh.seek(sd["offset"]); return fh.read(sd["size"])
        if assets_file is not None:   # e.g. archive:/CAB-x/CAB-x.resS inside an asset bundle
            from UnityPy.helpers.ResourceReader import get_resource_data
            return bytes(get_resource_data(sd["path"], assets_file, sd["offset"], sd["size"]))
        raise FileNotFoundError(p)
    return bytes(tt["image data"])


def _decode_level(kind, bb, data, w, h):
    import texture2ddecoder as t2d
    dec = {"bc1": t2d.decode_bc1, "bc3": t2d.decode_bc3, "bc7": t2d.decode_bc7,
           "bc4": t2d.decode_bc4, "bc5": t2d.decode_bc5}[kind]
    return _bgra_to_rgba(dec(data, w, h))


def _downscale(rgba, w, h):
    from PIL import Image
    im = Image.frombytes("RGBA", (w, h), rgba)
    nw, nh = max(1, w // 2), max(1, h // 2)
    return im.resize((nw, nh), Image.BOX).tobytes(), nw, nh


def convert_texture(tt, file_dir, enc, stats, quality, assets_file=None):
    fmt = tt.get("m_TextureFormat")
    if fmt not in BC_FORMATS: return False
    kind, bb = BC_FORMATS[fmt]
    w, h, mips = tt["m_Width"], tt["m_Height"], max(1, tt.get("m_MipCount", 1))
    images = max(1, tt.get("m_ImageCount", 1))
    data = _read_texture_data(tt, file_dir, assets_file)
    out = bytearray(); per_image = None
    if kind.startswith("crunch"):
        import texture2ddecoder as t2d
        if images != 1: raise RuntimeError("crunched multi-image texture")
        raw = t2d.unpack_unity_crunch(data)
        lvl = _decode_level("bc1" if kind == "crunch_dxt1" else "bc3", 0, raw[: ((w + 3) // 4) * ((h + 3) // 4) * (8 if kind == "crunch_dxt1" else 16)], w, h)
        cw, ch = w, h
        for m in range(mips):
            out += enc.encode(lvl, cw, ch, 4, quality)
            if m + 1 < mips: lvl, cw, ch = _downscale(lvl, cw, ch)
        per_image = len(out)
    else:
        complete = tt["m_CompleteImageSize"]
        if len(data) < complete * images:
            raise RuntimeError(f"texture data {len(data)} < {complete}*{images}")
        for i in range(images):
            pos = i * complete; start = len(out)
            for m in range(mips):
                cw, ch = max(1, w >> m), max(1, h >> m)
                n = ((cw + 3) // 4) * ((ch + 3) // 4) * bb
                out += enc.encode(_decode_level(kind, bb, data[pos:pos + n], cw, ch), cw, ch, 4, quality)
                pos += n
            per_image = len(out) - start
    tt["m_TextureFormat"] = ASTC_4x4
    tt["m_CompleteImageSize"] = per_image
    tt["image data"] = bytes(out)
    tt["m_StreamData"] = {"offset": 0, "size": 0, "path": ""}
    stats[f"tex_{kind}->astc"] += 1
    return True


def main():
    src, dst, exe, target = sys.argv[1:5]
    only = sys.argv[5] if len(sys.argv) > 5 else None
    tp = TARGETS[target]
    tr = Translator(exe)
    enc = None
    if target == "ios":
        from astc_tool import AstcEncoder
        enc = AstcEncoder()
    stats = collections.Counter()
    for root, dirs, names in os.walk(src):
        rel = os.path.relpath(root, src)
        os.makedirs(os.path.join(dst, rel), exist_ok=True)
        for n in sorted(names):
            if only and n != only: continue
            s_path, d_path = os.path.join(root, n), os.path.join(dst, rel, n)
            if n.endswith(SKIP_EXT) or n.endswith((".txt", ".json", ".xml", ".info", ".config", ".dll")):
                shutil.copyfile(s_path, d_path); continue
            try:
                env = UnityPy.load(s_path)
                if not env.files: raise ValueError("not a unity file")
            except Exception:
                shutil.copyfile(s_path, d_path); continue
            changed = False
            for obj in env.objects:
                if obj.type == ClassIDType.Shader:
                    tt = obj.read_typetree()
                    if convert_shader(tt, tr, stats):
                        obj.save_typetree(tt); changed = True
                elif enc and obj.type in (ClassIDType.Texture2D, ClassIDType.Cubemap):
                    tt = obj.read_typetree()
                    try:
                        if convert_texture(tt, root, enc, stats, ASTC_QUALITY, obj.assets_file):
                            obj.save_typetree(tt); changed = True
                    except Exception as e:
                        stats["tex_error"] += 1
                        print(f"  texture {tt.get('m_Name')}: {e}", flush=True)
            for f in env.files.values():
                for sf in ([f] if hasattr(f, "target_platform") else getattr(f, "files", {}).values()):
                    if hasattr(sf, "_m_target_platform"):
                        sf._m_target_platform = tp; changed = True   # the field save() writes
            f = next(iter(env.files.values()))
            data = f.save() if changed else open(s_path, "rb").read()
            with open(d_path, "wb") as out:
                out.write(data)
            stats["files_written"] += 1
            print(f"{os.path.join(rel, n)}: {dict(stats)}", flush=True)
    print(json.dumps(stats, indent=1))


if __name__ == "__main__":
    main()
