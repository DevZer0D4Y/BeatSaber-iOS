#!/bin/bash
# Managed -> C++ for a game-free app: only the .NET class library and Unity's engine modules
# are compiled in (AOT). The game's own assemblies, the third-party libraries it ships and the
# port (BeatSaberPort.dll) stay IL and run in HybridCLR's interpreter, loaded at startup from
# the player's game folder.
#
# The game assemblies are still passed to UnityLinker so that it keeps every part of the class
# library and engine modules they use; none of them is converted to C++.
#   usage: il2cpp_hclr.sh <game Managed dir> <dir with BeatSaberPort.dll> <work dir>
#   out:   <work>/aot (stripped AOT assemblies, also HybridCLR's supplementary metadata),
#          <work>/hot_names.txt (in load order), <work>/Il2CppOutput, <work>/il2cpp_data,
#          <work>/MethodBridge.cpp (HybridCLR's interpreter <-> native call bridges)
#   env:   BS_UNITY, BS_WORK (as tools/il2cpp_ios.sh), PLATFORM (iOS|MacOSX, default iOS)
#          HCLR_SRC  output folder of tools/hybridclr/prepare.sh (required)
set -euo pipefail
GAME=$1; PORT=$2; W=$3
HCLR_SRC=${HCLR_SRC:?set HCLR_SRC to the tools/hybridclr/prepare.sh output folder}
HERE=$(cd "$(dirname "$0")" && pwd)
UNITY=${BS_UNITY:-${BS_WORK:-$HOME/BeatSaber-iOS-work}/unity}
U=$UNITY/Unity/Unity.app/Contents
PLATFORM=${PLATFORM:-iOS}
case "$PLATFORM" in
  iOS)    ENGINE="$UNITY/iOSSupport/Variations/il2cpp/Managed"; ARCH=ARM64; WL="$UNITY/iOSSupport/Whitelists" ;;
  # Mac IL2CPP player (for test builds): Unity's "Mac Build Support (IL2CPP)" in $UNITY/MacIL2CPP
  MacOSX) ENGINE="$UNITY/MacIL2CPP/Variations/macosx64_development_il2cpp/Data/Managed"; ARCH=x64; WL="$U/PlaybackEngines/MacStandaloneSupport/Whitelists" ;;
  *) echo "PLATFORM must be iOS or MacOSX"; exit 1 ;;
esac
MONO="$U/MonoBleedingEdge/bin/mono"
DEPLOY="$U/il2cpp/build/deploy/net471"
AOT_PROFILE="$U/MonoBleedingEdge/lib/mono/unityaot"

IN="$W/managed_in"; LINKED="$W/managed_linked"; AOT="$W/aot"; CPP="$W/Il2CppOutput"; DATA="$W/il2cpp_data"
rm -rf "$IN" "$LINKED" "$AOT" "$CPP" "$DATA"
mkdir -p "$IN" "$LINKED" "$AOT" "$CPP" "$DATA"

is_bcl() { case "$1" in mscorlib.dll|System.dll|System.*.dll|Mono.*.dll|Accessibility.dll|netstandard.dll|Microsoft.*.dll) return 0;; esac; return 1; }

# 1. inputs: game + third-party assemblies, the port, and the engine modules for this platform
for f in "$GAME"/*.dll; do b=$(basename "$f"); is_bcl "$b" || cp "$f" "$IN/"; done
cp "$PORT/BeatSaberPort.dll" "$IN/"
cp "$ENGINE/"UnityEngine*.dll "$IN/"
# engine modules this player doesn't have (the game's Windows build ships e.g. ClusterInput)
for f in "$IN"/UnityEngine.*Module.dll; do b=$(basename "$f"); [ -f "$ENGINE/$b" ] || rm -f "$f"; done
# hot = everything that is neither class library nor an engine module of this player
: > "$W/hot_names.txt"
for f in "$IN"/*.dll; do
  b=$(basename "$f")
  [ -f "$ENGINE/$b" ] || basename "$b" .dll >> "$W/hot_names.txt"
done

{
  echo '<linker>'
  while read -r n; do echo "  <assembly fullname=\"$n\" preserve=\"all\"/>"; done < "$W/hot_names.txt"
  echo '</linker>'
} > "$W/link.xml"

# 2. UnityLinker: strip class library + engine modules against everything that uses them
"$MONO" "$DEPLOY/UnityLinker.exe" \
  --search-directory="$IN,$AOT_PROFILE,$AOT_PROFILE/Facades" \
  --out="$LINKED" \
  --include-directory="$IN" \
  --include-link-xml="$W/link.xml" \
  --descriptor-directory="$WL" \
  --dotnetprofile=unityaot --dotnetruntime=il2cpp \
  --platform="$PLATFORM" --architecture="$ARCH" \
  --core-action=link --user-action=copy \
  --rule-set=Conservative --include-profile-search-dirs \
  --use-editor-options 2>&1 | tail -20

# 3. AOT set = linker output minus the interpreted assemblies
for f in "$LINKED"/*.dll; do
  n=$(basename "$f" .dll)
  grep -qx "$n" "$W/hot_names.txt" || cp "$f" "$AOT/"
done
echo "AOT assemblies: $(ls "$AOT" | wc -l), interpreted: $(wc -l < "$W/hot_names.txt")"

# 4. il2cpp on the AOT set only
"$MONO" "$DEPLOY/il2cpp.exe" --convert-to-cpp \
  --emit-null-checks --enable-array-bounds-check \
  --dotnetprofile=unityaot --platform="$PLATFORM" --architecture="$ARCH" --configuration=Release \
  --directory="$AOT" --generatedcppdir="$CPP" --data-folder="$DATA" \
  --symbols-folder="$W/il2cpp_symbols" 2>&1 | tail -30
# HybridCLR's runtime rewrites Il2CppType / Il2CppGenericInst records in place at startup
# (index -> handle). Unity 2019.4.28 emits them as const (read-only memory); HybridCLR's own
# modified 2019 code generator emits them without const. Do the same here.
sed -i '' -E 's/^(extern )?const (Il2CppType|Il2CppGenericInst) ([A-Za-z_0-9]+)( =|;)/\1\2 \3\4/' \
  "$CPP/Il2CppTypeDefinitions.c" "$CPP/Il2CppGenericInstDefinitions.c"
left=$(cat "$CPP"/*.c "$CPP"/*.cpp | { grep -c -E '^(extern )?const (Il2CppType|Il2CppGenericInst) [A-Za-z_0-9]+( =|;)' || true; })
[ "$left" = 0 ] || { echo "const type records left: $left"; exit 1; }
echo "C++ files: $(ls "$CPP" | wc -l)  size: $(du -sh "$CPP" | cut -f1)"

# 5. HybridCLR: load order of the interpreted assemblies, and the call bridges
GEN=$W/hclrgen
dotnet build "$HERE/hclrgen" -c Release -nologo -v q -p:HclrUnity="$HCLR_SRC/hybridclr_unity" -o "$GEN" >/dev/null
dotnet "$GEN/hclrgen.dll" order "$IN" "$W/hot_names.txt" BeatSaberPort
dotnet "$GEN/hclrgen.dll" "$AOT" "$IN" "$HCLR_SRC/hybridclr_unity/Data~/Templates/MethodBridge.cpp.tpl" "$W/MethodBridge.cpp" \
  "$(paste -sd, "$W/hot_names.txt")" | grep -v "^resolve " | tail -2
