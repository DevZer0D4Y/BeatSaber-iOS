#!/bin/bash
# Build the IL2CPP runtime source tree for the game-free app: Unity 2019.4.28's own libil2cpp
# with HybridCLR (MIT, github.com/focus-creative-games) merged in. HybridCLR's il2cpp changes
# are made against 2019.4.40; a three-way merge applies them to 2019.4.28 so that the runtime
# matches the il2cpp code generator used for the build.
#   usage: prepare.sh <out dir>      env: BS_UNITY / BS_WORK (Unity parts, see tools/fetch_unity.sh)
#   out:   <out>/tree (libil2cpp + external), <out>/hybridclr_unity (generator sources, templates)
set -euo pipefail
OUT=$(mkdir -p "$1" && cd "$1" && pwd)
HERE=$(cd "$(dirname "$0")" && pwd)
UNITY=${BS_UNITY:-${BS_WORK:-$HOME/BeatSaber-iOS-work}/unity}
IL=$UNITY/Unity/Unity.app/Contents/il2cpp
# HybridCLR v8.15.0, the last release supporting Unity 2019 (official version table)
HCLR_COMMIT=27fbcefd860c288d9495484a91fee64c909a0306
PLUS_COMMIT=fda18b91c1ee5ef1ae12153cf354a8c216b44946   # il2cpp_plus v2019-8.15.0
PLUS_BASE=dda74bf                                       # il2cpp_plus: unmodified 2019.4.40
TOOLS_TAG=v8.15.0                                       # hybridclr_unity

fetch() {   # repo dir commit
  [ -d "$OUT/$2/.git" ] || git clone -q --filter=blob:none "https://github.com/focus-creative-games/$1.git" "$OUT/$2"
  git -C "$OUT/$2" -c advice.detachedHead=false checkout -q "$3"
}
fetch hybridclr hybridclr "$HCLR_COMMIT"
fetch il2cpp_plus il2cpp_plus "$PLUS_COMMIT"
fetch hybridclr_unity hybridclr_unity "$TOOLS_TAG"

# three-way merge: base = 2019.4.40, ours = Unity 2019.4.28, theirs = 2019.4.40 + HybridCLR
M=$OUT/merge; rm -rf "$M"; mkdir -p "$M"; cd "$M"
git init -q; git config user.name build; git config user.email build@localhost
put() { rm -rf libil2cpp; "$@"; rm -rf libil2cpp/os/c-api/tests; git add -A; git commit -qm "$1" --allow-empty; }
(cd "$OUT/il2cpp_plus" && git archive "$PLUS_BASE" libil2cpp) | tar -x; rm -rf libil2cpp/os/c-api/tests
git add -A; git commit -qm v40; git branch base
git checkout -q -b hclr
rm -rf libil2cpp; (cd "$OUT/il2cpp_plus" && git archive HEAD libil2cpp) | tar -x; rm -rf libil2cpp/os/c-api/tests
git add -A; git commit -qm hclr
git checkout -q -b v28 base
rm -rf libil2cpp; cp -R "$IL/libil2cpp" libil2cpp; rm -rf libil2cpp/os/c-api/tests
git add -A; git commit -qm v28
git merge -q --no-edit hclr >/dev/null 2>&1 || true
conflicts=$(git diff --name-only --diff-filter=U | sort | tr '\n' ' ')
# Both conflicts are taken from HybridCLR's side: GenericMetadata.cpp (2019.4.40 rewrote type
# inflation, which HybridCLR builds on; internal, no ABI impact) and il2cpp-class-internals.h
# (extra pointer members in a MethodInfo union, same size).
[ "$conflicts" = "libil2cpp/il2cpp-class-internals.h libil2cpp/metadata/GenericMetadata.cpp " ] ||
  { echo "unexpected merge conflicts: $conflicts"; exit 1; }
git checkout -q --theirs libil2cpp/il2cpp-class-internals.h libil2cpp/metadata/GenericMetadata.cpp
git add -A; git commit -qm merged

T=$OUT/tree; rm -rf "$T"; mkdir -p "$T/external"
cp -R "$M/libil2cpp" "$T/libil2cpp"
cp -R "$IL/external/zlib" "$IL/external/google" "$IL/external/xxHash" "$T/external/"
L=$T/libil2cpp
cp -R "$OUT/hybridclr/hybridclr" "$L/hybridclr"
printf '#pragma once\n//!!!{{UNITY_VERSION\n#define HYBRIDCLR_UNITY_VERSION 20190428\n#define HYBRIDCLR_UNITY_2019 1\n#define HYBRIDCLR_UNITY_2019_OR_NEW 1\n//!!!}}UNITY_VERSION\n' \
  > "$L/hybridclr/generated/UnityVersion.h"
# the method bridges are generated per build (tools/hclrgen) and compiled with the app
rm -f "$L/hybridclr/generated/MethodBridge.cpp"
mkdir -p "$L/hybridclr/port"; cp "$HERE/PortLoader.cpp" "$L/hybridclr/port/"
python3 - "$L" <<'PY'
import sys
L = sys.argv[1]
def sub(p, old, new):
    s = open(p).read(); assert old in s, (p, old); open(p, 'w').write(s.replace(old, new, 1))
# Unity compiles libil2cpp as merged files, which hides this missing include
sub(L + '/hybridclr/interpreter/InterpreterUtil.h',
    '#if HYBRIDCLR_UNITY_2023_OR_NEW\n#include "codegen/il2cpp-codegen.h"\n#else\n#include "codegen/il2cpp-codegen-il2cpp.h"\n#endif',
    '#include "codegen/il2cpp-codegen.h"')
# load the interpreted assemblies at the end of runtime startup
sub(L + '/vm/Runtime.cpp', '        hybridclr::Runtime::Initialize();\n        return true;',
    '        hybridclr::Runtime::Initialize();\n        hybridclr_port::LoadAssemblies();\n        return true;')
sub(L + '/vm/Runtime.cpp', '#include "hybridclr/Runtime.h"',
    '#include "hybridclr/Runtime.h"\nnamespace hybridclr_port { void LoadAssemblies(); }')
# the app may keep IL2CPP's own data (global-metadata.dat) apart from the game data folder
sub(L + '/utils/Runtime.cpp', '    void Runtime::SetDataDir(const char *path)\n    {\n        s_DataDir = path;',
    '    void Runtime::SetDataDir(const char *path)\n    {\n        if (hclr_port_il2cpp_data_dir) path = hclr_port_il2cpp_data_dir();\n        s_DataDir = path;')
sub(L + '/utils/Runtime.cpp', '    static std::string s_DataDirFallback;',
    '    static std::string s_DataDirFallback;\n    extern "C" const char* hclr_port_il2cpp_data_dir() __attribute__((weak));')
# accept .NET 1.x metadata (table schema 1.0, e.g. the game's BouncyCastle.Crypto): its rows are
# laid out as in 2.0, which only added the generic tables
sub(L + '/hybridclr/metadata/RawImageBase.cpp',
    'ptrTableHeader->majorVersion != 2 || ptrTableHeader->minorVersion != 0',
    '(ptrTableHeader->majorVersion != 2 && ptrTableHeader->majorVersion != 1) || ptrTableHeader->minorVersion > 1')
# say which type was missing (the exception alone may not be visible during startup)
sub(L + '/hybridclr/metadata/Image.cpp',
    '                return nullptr;\n            }\n            il2cpp::vm::Exception::Raise(il2cpp::vm::Exception::GetTypeLoadException(',
    '                return nullptr;\n            }\n            fprintf(stderr, "[HybridCLR] type not found: %s.%s in %s (%s)\\n", typeNamespaceStr, typeNameStr, assName, refAss ? "loaded" : "not loaded");\n            il2cpp::vm::Exception::Raise(il2cpp::vm::Exception::GetTypeLoadException(')
PY
echo "tree ready: $T"
