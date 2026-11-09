#!/bin/bash
# Compile the port's own C# (port/src) against the game's assemblies -> BeatSaberPort.dll
# usage: build_port.sh <Managed dir to reference> <out dir>
set -euo pipefail
REF=$1; OUT=$2
# the C# compiler of the newest installed .NET SDK (override with CSC=...)
if [ -z "${CSC:-}" ]; then
  CSC=$(dotnet --list-sdks | tail -1 | sed -E 's/^([^ ]+) \[(.*)\]$/\2\/\1\/Roslyn\/bincore\/csc.dll/')
fi
[ -f "$CSC" ] || { echo "C# compiler not found (install the .NET SDK, or set CSC)"; exit 1; }
SRC=$(cd "$(dirname "$0")/../port/src" && pwd)
mkdir -p "$OUT"
refs=()
for f in "$REF"/*.dll; do
  case "$(basename "$f")" in BeatSaberPort.dll) continue;; esac
  refs+=("-r:$f")
done
dotnet "$CSC" -nologo -noconfig -nostdlib -target:library -langversion:7.3 -optimize+ -deterministic \
  -nowarn:1701,1702 -out:"$OUT/BeatSaberPort.dll" "${refs[@]}" "$SRC"/*.cs
echo "built $OUT/BeatSaberPort.dll"
