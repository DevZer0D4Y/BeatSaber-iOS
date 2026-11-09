#!/usr/bin/env python3
"""List a target's source files (path relative to the project dir) + per-file COMPILER_FLAGS,
and its Resources / Frameworks phases, from an Xcode project.pbxproj (Unity trampoline)."""
import sys, re, os

pbx, target = sys.argv[1], sys.argv[2]
s = open(pbx).read()
objs = {}
for m in re.finditer(r'\n\t\t([0-9A-F]{24}) (?:/\* .*? \*/ )?= \{(.*?)\n?\t*\};', s, re.S):
    objs[m.group(1)] = m.group(2)

def field(body, name):
    m = re.search(r'\b' + name + r' = ("(?:[^"\\]|\\.)*"|[^;]+);', body)
    if not m: return None
    v = re.sub(r'\s*/\*.*?\*/', '', m.group(1)).strip()
    return v[1:-1].replace('\\"', '"') if v.startswith('"') else v

def children(body):
    m = re.search(r'children = \((.*?)\);', body, re.S)
    return re.findall(r'\b([0-9A-F]{24})\b', m.group(1)) if m else []

# parent map for groups -> build full paths
parent = {}
for oid, body in objs.items():
    if 'isa = PBXGroup' in body or 'isa = PBXVariantGroup' in body:
        for c in children(body): parent[c] = oid

def full_path(oid):
    parts = []
    cur = oid
    while cur in objs:
        b = objs[cur]
        tree = field(b, 'sourceTree')
        p = field(b, 'path')
        if p: parts.append(p)
        if tree and tree not in ('"<group>"', '<group>'): break
        cur = parent.get(cur)
    return '/'.join(reversed(parts))

tid = None
for oid, body in objs.items():
    if 'isa = PBXNativeTarget' in body and field(body, 'name') in (target, '"%s"' % target):
        tid = oid
phases = re.findall(r'\b([0-9A-F]{24})\b', re.search(r'buildPhases = \((.*?)\);', objs[tid], re.S).group(1))
for ph in phases:
    body = objs[ph]
    kind = re.search(r'isa = (\w+)', body).group(1)
    files = re.search(r'files = \((.*?)\);', body, re.S)
    for bf in re.findall(r'\b([0-9A-F]{24})\b', files.group(1) if files else ''):
        b = objs[bf]
        ref = field(b, 'fileRef')
        flags = ''
        m = re.search(r'COMPILER_FLAGS = ("(?:[^"\\]|\\.)*"|[^;]+);', b)
        if m: flags = m.group(1).strip('"')
        print(f"{kind}\t{full_path(ref)}\t{flags}")
