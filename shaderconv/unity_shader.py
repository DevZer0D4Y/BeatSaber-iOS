"""Unity 2019.4 shader sub-program parsing/writing and DXBC RDEF synthesis.

Sub-program blob layout (version 201806140):
  version, type, stats[4], keywords[], local_keywords[], code[] (aligned)
  sourceMap, channels[(source, target)], param groups, bindings
Param group: name, usedSize, params[(name, type, rows, cols, isMatrix, arraySize, offset)], structCount
Binding: name, kind (0 tex, 1 cbuffer, 2 buffer, 3 uav, 4 sampler), index, extra
         + textures only: (dim << 1) | multisampled
"""
import struct

METAL_HDR = struct.pack("<10I", 0xF00DCAFE, 0x28, 3, 0x34, 0, 0, 0, 0, 0, 5) + b"xlatMtlMain\0"


class R:
    def __init__(self, b, pos=0): self.b, self.p = b, pos
    def i32(self):
        v = struct.unpack_from("<i", self.b, self.p)[0]; self.p += 4; return v
    def u32(self):
        v = struct.unpack_from("<I", self.b, self.p)[0]; self.p += 4; return v
    def align(self): self.p = (self.p + 3) & ~3
    def raw(self):
        n = self.i32(); v = bytes(self.b[self.p:self.p + n]); self.p += n; return v
    def string(self):
        v = self.raw().decode("utf-8", "replace"); self.align(); return v


class W:
    def __init__(self): self.b = bytearray()
    def i32(self, v): self.b += struct.pack("<i", v)
    def u32(self, v): self.b += struct.pack("<I", v & 0xFFFFFFFF)
    def align(self):
        while len(self.b) % 4: self.b.append(0)
    def raw(self, data): self.i32(len(data)); self.b += data
    def string(self, s): self.raw(s.encode()); self.align()


def blob_entries(seg0):
    r = R(seg0)
    return [(r.u32(), r.u32(), r.u32()) for _ in range(r.i32())]


def parse_subprogram(b, off, length):
    r = R(b, off)
    sp = {"version": r.i32(), "type": r.i32(), "stats": [r.i32() for _ in range(4)]}
    sp["keywords"] = [r.string() for _ in range(r.i32())]
    sp["local_keywords"] = [r.string() for _ in range(r.i32())]
    sp["code"] = r.raw(); r.align()
    sp["source_map"] = r.i32()
    sp["channels"] = [(r.i32(), r.i32()) for _ in range(r.i32())]
    groups = []
    for _ in range(r.i32()):
        g = {"name": r.string(), "size": r.i32(), "params": []}
        for _ in range(r.i32()):
            g["params"].append({"name": r.string(), "type": r.i32(), "rows": r.i32(), "cols": r.i32(),
                                "matrix": r.i32(), "array": r.i32(), "offset": r.i32()})
        g["structs"] = []
        for _ in range(r.i32()):
            st = {"name": r.string(), "index": r.i32(), "array": r.i32(), "size": r.i32(), "members": []}
            for _ in range(r.i32()):
                st["members"].append({"name": r.string(), "type": r.i32(), "rows": r.i32(), "cols": r.i32(),
                                      "matrix": r.i32(), "array": r.i32(), "offset": r.i32()})
            g["structs"].append(st)
        groups.append(g)
    sp["groups"] = groups
    binds = []
    for _ in range(r.i32()):
        bd = {"name": r.string(), "kind": r.i32(), "index": r.i32(), "extra": r.i32()}
        if bd["kind"] == 0:
            bd["tex"] = r.i32()
        binds.append(bd)
    sp["bindings"] = binds
    if r.p != off + length:
        raise ValueError(f"sub-program parse ended at {r.p - off} of {length}")
    return sp


def write_subprogram(sp):
    w = W()
    w.i32(sp["version"]); w.i32(sp["type"])
    for s in sp["stats"]: w.i32(s)
    w.i32(len(sp["keywords"]))
    for k in sp["keywords"]: w.string(k)
    w.i32(len(sp["local_keywords"]))
    for k in sp["local_keywords"]: w.string(k)
    w.raw(sp["code"]); w.align()
    w.i32(sp["source_map"])
    w.i32(len(sp["channels"]))
    for a, b in sp["channels"]: w.i32(a); w.i32(b)
    w.i32(len(sp["groups"]))
    for g in sp["groups"]:
        w.string(g["name"]); w.i32(g["size"]); w.i32(len(g["params"]))
        for p in g["params"]:
            w.string(p["name"])
            for k in ("type", "rows", "cols", "matrix", "array", "offset"): w.i32(p[k])
        w.i32(len(g["structs"]))
        for st in g["structs"]:
            w.string(st["name"]); w.i32(st["index"]); w.i32(st["array"]); w.i32(st["size"])
            w.i32(len(st["members"]))
            for p in st["members"]:
                w.string(p["name"])
                for k in ("type", "rows", "cols", "matrix", "array", "offset"): w.i32(p[k])
    w.i32(len(sp["bindings"]))
    for bd in sp["bindings"]:
        w.string(bd["name"]); w.i32(bd["kind"]); w.i32(bd["index"]); w.i32(bd["extra"])
        if bd["kind"] == 0: w.i32(bd["tex"])
    return bytes(w.b)


def write_blob(subprograms):
    """Pack serialized sub-programs into one segment with the entry table."""
    head = 4 + 12 * len(subprograms)
    out = bytearray(struct.pack("<i", len(subprograms)))
    body = bytearray(); pos = head
    for data in subprograms:
        out += struct.pack("<III", pos, len(data), 0)
        body += data; pos += len(data)
    return bytes(out + body)


# ---------------------------------------------------------------- DXBC / RDEF
def dxbc_split(code):
    """Unity D3D11 code = 6-byte prefix + DXBC container."""
    i = code.find(b"DXBC")
    if i < 0:
        raise ValueError("no DXBC")
    return code[:i], code[i:]


def dxbc_chunks(d):
    n = struct.unpack_from("<I", d, 28)[0]
    out = []
    for j in range(n):
        o = struct.unpack_from("<I", d, 32 + 4 * j)[0]
        cc, sz = d[o:o + 4], struct.unpack_from("<I", d, o + 4)[0]
        out.append((cc, d[o + 8:o + 8 + sz]))
    return out


def dxbc_build(chunks):
    head = 32 + 4 * len(chunks)
    offs, body, pos = [], bytearray(), head
    for cc, data in chunks:
        offs.append(pos)
        body += cc + struct.pack("<I", len(data)) + data
        pos += 8 + len(data)
    hdr = b"DXBC" + b"\0" * 16 + struct.pack("<III", 1, pos, len(chunks))
    return hdr + b"".join(struct.pack("<I", o) for o in offs) + bytes(body)


def shader_version(shdr):
    tok = struct.unpack_from("<I", shdr, 0)[0]
    return (tok >> 4) & 0xF, tok & 0xF, (tok >> 16) & 0xFFFF   # major, minor, program type


UNITY_DIM_TO_SRV = {2: 4, 3: 8, 4: 9, 5: 5, 6: 10}   # Unity TextureDimension -> D3D_SRV_DIMENSION
PARAM_TO_SVT = {0: 3, 1: 2, 2: 1, 3: 3, 4: 2, 5: 19}  # Unity ShaderParamType -> D3D_SHADER_VARIABLE_TYPE


def _var_size(p):
    elem_regs = p["rows"] if p["matrix"] else 1
    elem = (elem_regs - 1) * 16 + p["cols"] * 4 if p["matrix"] else p["cols"] * 4
    if p["array"] > 1:
        return (p["array"] - 1) * 16 * elem_regs + elem
    return elem


def build_rdef(sp, major, minor, ptype):
    """Synthesize an RDEF chunk (only the fields HLSLcc reads) from Unity metadata.
    Layout: header | string pool | resources | cbuffers | variables | types | struct members."""
    sm5 = major >= 5
    cbufs = [g for g in sp["groups"] if g["name"]]
    cb_bind = {b["name"]: b["index"] for b in sp["bindings"] if b["kind"] == 1}
    resources = []   # (name, type, ret, dim, samples, bind, count, flags)
    samplers = {}
    for b in sp["bindings"]:
        if b["kind"] == 0:
            dim = b["tex"] >> 1; ms = b["tex"] & 1
            srv = 6 if ms else UNITY_DIM_TO_SRV.get(dim, 4)
            resources.append((b["name"], 2, 5, srv, 0 if ms else 0xFFFFFFFF, b["index"], 1, 0xC))
            if 0 <= b["extra"] < 16 and b["extra"] not in samplers:
                samplers[b["extra"]] = "sampler" + b["name"]
        elif b["kind"] == 4:
            samplers.setdefault(b["index"], b["name"])
        elif b["kind"] == 2:
            resources.append((b["name"], 5, 6, 1, 0, b["index"], 1, 0))
        elif b["kind"] == 3:
            resources.append((b["name"], 4, 5, 1, 0, b["index"], 1, 0))
    used_names = set()
    for idx, name in sorted(samplers.items()):
        if not name or name in ("sampler", "sampler_") or name in used_names:
            name = f"sampler_s{idx}"
        used_names.add(name)
        resources.append((name, 3, 0, 0, 0, idx, 1, 0))
    for g in cbufs:
        resources.append((g["name"], 0, 0, 0, 0, cb_bind.get(g["name"], 0), 1, 0))

    # variables per cbuffer: plain params, then structs
    cb_vars = []
    for g in cbufs:
        vs = [("param", p) for p in g["params"]] + [("struct", st) for st in g.get("structs", [])]
        vs.sort(key=lambda v: v[1]["offset"] if v[0] == "param" else v[1]["index"])
        cb_vars.append(vs)

    pool = bytearray(); str_off = {}
    def s(name):
        if name not in str_off:
            str_off[name] = 28 + len(pool); pool.extend(name.encode() + b"\0")
        return str_off[name]
    for r_ in resources: s(r_[0])
    for g, vs in zip(cbufs, cb_vars):
        s(g["name"])
        for kind, v in vs:
            s(v["name"])
            if kind == "struct":
                for m in v["members"]: s(m["name"])
    s("Unity")
    while len(pool) % 4: pool.append(0)

    res_size = (10 if (major == 5 and minor >= 1) or major > 5 else 8) * 4
    var_size = (10 if sm5 else 6) * 4
    res_off = 28 + len(pool)
    cb_off = res_off + res_size * len(resources)
    var_off = cb_off + 24 * len(cbufs)
    nvars = sum(len(vs) for vs in cb_vars)
    type_off = var_off + var_size * nvars
    # type descs: one per variable, plus one per struct member
    nmembers = sum(len(v["members"]) for vs in cb_vars for k, v in vs if k == "struct")
    mem_off = type_off + 16 * (nvars + nmembers)

    hdr = struct.pack("<7I", len(cbufs), cb_off, len(resources), res_off,
                      ((0xFFFE if ptype == 1 else 0xFFFF) << 16) | (major << 8) | minor, 0, s("Unity"))
    res_b = bytearray()
    for (name, t, ret, dim, samples, bind, count, flags) in resources:
        res_b += struct.pack("<8I", s(name), t, ret, dim, samples, bind, count, flags)
        if res_size == 40: res_b += struct.pack("<2I", 0, 0)
    cb_b = bytearray(); var_b = bytearray(); type_b = bytearray(); mem_b = bytearray()
    vi = 0; ti = nvars  # member type descs come after the variable type descs

    def ptype_desc(p):
        cls = 3 if p["matrix"] else (1 if p["cols"] > 1 else 0)
        return struct.pack("<6HI", cls, PARAM_TO_SVT.get(p["type"], 3), p["rows"], p["cols"], p["array"], 0, 0)

    member_types = bytearray()
    for g, vs in zip(cbufs, cb_vars):
        cb_b += struct.pack("<6I", s(g["name"]), len(vs), var_off + vi * var_size, g["size"], 0, 0)
        for kind, v in vs:
            if kind == "param":
                var_b += struct.pack("<6I", s(v["name"]), v["offset"], _var_size(v), 2, type_off + vi * 16, 0)
                type_b += ptype_desc(v)
            else:
                size = v["size"] * max(v["array"], 1)
                var_b += struct.pack("<6I", s(v["name"]), v["index"], size, 2, type_off + vi * 16, 0)
                type_b += struct.pack("<6HI", 5, 0, 1, v["size"] // 4, v["array"], len(v["members"]),
                                      mem_off + len(mem_b))
                for m in v["members"]:
                    mem_b += struct.pack("<3I", s(m["name"]), type_off + ti * 16, m["offset"])
                    member_types += ptype_desc(m); ti += 1
            if sm5: var_b += struct.pack("<4I", 0xFFFFFFFF, 0, 0xFFFFFFFF, 0)
            vi += 1
    out = hdr + pool + res_b + cb_b + var_b + type_b + member_types
    assert len(out) == mem_off, (len(out), mem_off)
    return bytes(out + mem_b)


def dxbc_with_rdef(code, sp):
    prefix, d = dxbc_split(code)
    chunks = [c for c in dxbc_chunks(d) if c[0] != b"RDEF"]
    shdr = next(data for cc, data in chunks if cc in (b"SHDR", b"SHEX"))
    major, minor, ptype = shader_version(shdr)
    return dxbc_build([(b"RDEF", build_rdef(sp, major, minor, ptype))] + chunks)


# ---------------------------------------------------------- Metal metadata
HLSLCC_FLAGS = 0x20000   # HLSLCC_FLAG_TRANSLATE_MATRICES, like Unity's Metal output
SVT_TO_PARAM = {3: 0, 2: 1, 1: 1, 19: 5}   # bool is declared as int by HLSLcc
CHANNEL_SEMANTIC = {0: "POSITION0", 1: "NORMAL0", 2: "TANGENT0", 3: "COLOR0"}
CHANNEL_SEMANTIC.update({4 + i: f"TEXCOORD{i}" for i in range(8)})
METAL_TYPE = {15: 23, 16: 23, 17: 24, 18: 24}   # DX11 VS/PS SM4/5 -> MetalVS/MetalFS


def _metal_size_align(t, rows, cols, matrix, arr):
    half = t in (53, 54, 55)            # min16 types (not produced from our RDEF, kept for safety)
    comp = 2 if half else 4
    if matrix:
        elem = 4 * comp; count = cols * max(arr, 1)   # float4 hlslcc_mtx..[cols * elements]
        return elem * count, elem
    n = cols
    vsize = {1: comp, 2: 2 * comp, 3: 4 * comp, 4: 4 * comp}[n]
    return vsize * max(arr, 1), vsize


def metal_groups(res):
    """Recompute cbuffer layouts with Metal alignment rules from HLSLcc's declaration
    order (what Unity's reflection layer does). Struct members arrive first, named
    'S[].member' or 'S.member', then the struct itself as a void-typed constant."""
    groups = [{"name": "", "size": 0, "params": [], "structs": []}]
    by_cb = {}
    for cb, name, off, t, rows, cols, matrix, arr, used in res["consts"]:
        by_cb.setdefault(cb, []).append((name, t, rows, cols, matrix, arr))
    for cb, size, count in res["cbuffers"]:
        cur = 0; params = []; structs = []; pending = {}

        def close(sname, arr):
            nonlocal cur
            members = pending.pop(sname)
            mcur = 0; mlist = []
            for mname, t, rows, cols, matrix, marr in members:
                sz, al = _metal_size_align(t, rows, cols, matrix, marr)
                mcur = (mcur + al - 1) // al * al
                mlist.append({"name": mname, "type": SVT_TO_PARAM.get(t, 0), "rows": rows, "cols": cols,
                              "matrix": int(matrix), "array": marr, "offset": mcur})
                mcur += sz
            ssize = (mcur + 15) // 16 * 16
            cur = (cur + 15) // 16 * 16
            structs.append({"name": sname, "index": cur, "array": arr, "size": ssize, "members": mlist})
            cur += ssize * max(arr, 1)

        for name, t, rows, cols, matrix, arr in by_cb.get(cb, []):
            if "." in name:
                sname, mname = name.split(".", 1)
                sname = sname.replace("[]", "")
                pending.setdefault(sname, []).append((mname, t, rows, cols, matrix, arr))
                continue
            if t == 0 and name in pending:        # array-of-struct top-level var
                close(name, arr); continue
            for sname in list(pending): close(sname, 1)
            sz, al = _metal_size_align(t, rows, cols, matrix, arr)
            cur = (cur + al - 1) // al * al
            params.append({"name": name, "type": SVT_TO_PARAM.get(t, 0), "rows": rows, "cols": cols,
                           "matrix": int(matrix), "array": arr, "offset": cur})
            cur += sz
        for sname in list(pending): close(sname, 1)
        groups.append({"name": cb, "size": cur, "params": params, "structs": structs})
    return groups


def to_metal(sp, res):
    """Build a Metal sub-program dict from a DX11 one and the HLSLcc result."""
    attr = {name: idx for name, idx in res["inputs"]}
    channels = []
    for src, _dst in sp["channels"]:
        sem = CHANNEL_SEMANTIC.get(src)
        if sem not in attr:
            raise ValueError(f"no Metal attribute for channel {src} ({sem}); inputs {res['inputs']}")
        channels.append((src, 13 + attr[sem]))
    dx_tex = {b["name"]: b for b in sp["bindings"] if b["kind"] == 0}
    binds = []
    for name, idx, sampler, ms, dim, uav in res["textures"]:
        if uav:
            binds.append({"name": name, "kind": 3, "index": idx, "extra": idx}); continue
        src = dx_tex.get(name)
        tex = src["tex"] if src else ((dim_to_unity(dim) << 1) | ms)
        binds.append({"name": name, "kind": 0, "index": idx, "extra": sampler, "tex": tex})
    for name, idx, uav in res["buffers"]:
        binds.append({"name": name, "kind": 3 if uav else 2, "index": idx, "extra": 0})
    for name, idx in res["cbbind"]:
        binds.append({"name": name, "kind": 1, "index": idx, "extra": 0})
    return {"version": sp["version"], "type": METAL_TYPE[sp["type"]], "stats": [0, 0, 0, 0],
            "keywords": sp["keywords"], "local_keywords": sp["local_keywords"],
            "code": METAL_HDR + res["msl"].encode(), "source_map": sp["source_map"],
            "channels": channels, "groups": metal_groups(res), "bindings": binds}


def dim_to_unity(td):
    # HLSLCC_TEX_DIMENSION -> Unity TextureDimension
    return {2: 2, 3: 3, 4: 4, 5: 2, 6: 5, 7: 6}.get(td, 2)
