#!/bin/bash
# Turn your own copy of Beat Saber 1.29.1 (Steam, Windows build) into the "GameData" folder the
# app needs. Copy the result to the iPhone/iPad with the Files app (On My iPhone > Beat Saber),
# or from a computer (Finder / Apple Devices > the device > Files > Beat Saber).
#
# Nothing here is uploaded anywhere; everything stays on your computer and your device.
#   usage: import_game.sh "<.../Beat Saber_Data>" <out dir>
#   out:   <out>/GameData/Data          converted game data
#          <out>/GameData/Data/Managed  the game's assemblies, patched for the port
#   needs: the helper tools from the README (build/venv, build/hlslcc_metal, build/astc_imageio,
#          tools/bstool built), the .NET SDK
set -euo pipefail
GAME=$(cd "$1" && pwd); OUT=$(mkdir -p "$2" && cd "$2" && pwd)
ROOT=$(cd "$(dirname "$0")/.." && pwd)
B=$ROOT/build
PY=$B/venv/bin/python
for f in "$PY" "$B/hlslcc_metal" "$B/astc_imageio" "$ROOT/tools/bstool/bin/Release/net10.0/bstool"; do
  [ -e "$f" ] || { echo "missing $f: build the helper tools first (README, step 3)"; exit 1; }
done
[ -f "$GAME/globalgamemanagers" ] && [ -f "$GAME/Managed/Main.dll" ] ||
  { echo "$GAME is not a Beat Saber_Data folder"; exit 1; }

D=$OUT/GameData/Data
TMP=$OUT/.import_tmp
rm -rf "$D" "$TMP"; mkdir -p "$TMP"

echo "1/4 shaders -> Metal"
"$PY" -I "$ROOT/shaderconv/convert_data.py" "$GAME" "$TMP/metal" "$B/hlslcc_metal" osx > "$OUT/import_shaders.log"
echo "2/4 textures -> ASTC (takes a while)"
ASTC_ENCODER=$B/astc_imageio "$PY" -I "$ROOT/shaderconv/convert_data.py" "$TMP/metal" "$D" none ios > "$OUT/import_textures.log"
rm -rf "$TMP/metal"
"$PY" -I "$ROOT/shaderconv/patch_settings.py" "$D/globalgamemanagers" --ios
sed -i '' -e 's/^vr-enabled=1/vr-enabled=0/' -e '/^vr-device-list=/d' "$D/boot.config"
# iOS player layout: StreamingAssets is "Raw"; PC-only content goes
[ -d "$D/StreamingAssets" ] && { rm -rf "$D/Raw"; mv "$D/StreamingAssets" "$D/Raw"; }
rm -rf "$D/Plugins" "$D/MonoBleedingEdge" "$D/UnitySubsystems" "$D/Resources/unity default resources" "$D/Managed"

echo "3/4 port + patched game assemblies"
"$ROOT/tools/build_port.sh" "$GAME/Managed" "$TMP/port" > /dev/null
"$ROOT/tools/bstool/bin/Release/net10.0/bstool" patch "$GAME/Managed" "$TMP/patched" "$TMP/port/BeatSaberPort.dll" > "$OUT/import_patch.log"
mkdir -p "$D/Managed"
for f in "$GAME/Managed"/*.dll; do
  b=$(basename "$f")
  case "$b" in   # the class library and Unity's engine modules are part of the app
    mscorlib.dll|netstandard.dll|System.dll|System.*.dll|Mono.*.dll|Microsoft.*.dll|Accessibility.dll|UnityEngine.dll|UnityEngine.*Module.dll) continue ;;
  esac
  cp "$f" "$D/Managed/"
done
cp "$TMP/patched"/*.dll "$D/Managed/"
rm -f "$D/Managed/BeatSaberPort.dll"   # ships with the app

echo "4/4 cleanup"
rm -rf "$TMP"
find "$OUT/GameData" -name '._*' -delete
echo "done: $OUT/GameData ($(du -sh "$OUT/GameData" | cut -f1)). Copy the GameData folder into the app's folder on your device."
