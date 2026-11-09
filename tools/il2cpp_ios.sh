#!/bin/bash
# Managed → C++ for iOS with Unity 2019.4.28f1's own UnityLinker + il2cpp.
# Game assemblies come from the Windows build (Mono IL), UnityEngine modules from the
# iOS player, the class library from the unityaot profile.
# usage: il2cpp_ios.sh <Windows Managed dir> <patched overlay dir> <work dir>
set -euo pipefail
WIN=$1; OVL=$2; W=$3
UNITY=${BS_UNITY:-${BS_WORK:-$HOME/BeatSaber-iOS-work}/unity}   # from tools/fetch_unity.sh
U=$UNITY/Unity/Unity.app/Contents
IOS=$UNITY/iOSSupport
MONO="$U/MonoBleedingEdge/bin/mono"
DEPLOY="$U/il2cpp/build/deploy/net471"
AOT="$U/MonoBleedingEdge/lib/mono/unityaot"

IN="$W/managed_in"; LINKED="$W/managed_linked"; CPP="$W/Il2CppOutput"; DATA="$W/il2cpp_data"
rm -rf "$IN" "$LINKED" "$CPP" "$DATA"   # start clean: some file systems keep stale tails on overwritten files
mkdir -p "$IN" "$LINKED" "$CPP" "$DATA"

# 1. inputs: game/third-party assemblies only (BCL comes from unityaot, engine from iOS)
for f in "$WIN"/*.dll; do
  b=$(basename "$f")
  case "$b" in
    mscorlib.dll|System.dll|System.*.dll|Mono.*.dll|Accessibility.dll|netstandard.dll|Microsoft.*.dll) ;;
    *) cp "$f" "$IN/" ;;
  esac
done
cp "$OVL"/*.dll "$IN/"   # patched game assemblies + BeatSaberPort.dll
# iOS engine modules override the Windows ones; package assemblies (UnityEngine.UI, ...) stay
cp "$IOS/Variations/il2cpp/Managed/"UnityEngine*.dll "$IN/"
rm -f "$IN"/UnityEngine.Cluster*Module.dll   # Windows-only engine modules
GAME=$(cd "$IN" && ls *.dll | grep -v '^UnityEngine' | sed "s|^|$IN/|" | paste -sd, -)

# keep every game assembly whole: Zenject and the game rely on reflection
{
  echo '<linker>'
  for f in "$IN"/*.dll; do b=$(basename "$f" .dll); case "$b" in UnityEngine|UnityEngine.*Module) ;; *) echo "  <assembly fullname=\"$b\" preserve=\"all\"/>";; esac; done
  echo '</linker>'
} > "$W/link.xml"

# 2. UnityLinker: strip the class library and engine modules, copy the game as-is
"$MONO" "$DEPLOY/UnityLinker.exe" \
  --search-directory="$IN,$AOT,$AOT/Facades" \
  --out="$LINKED" \
  --include-directory="$IN" \
  --include-link-xml="$W/link.xml" \
  --descriptor-directory="$IOS/Whitelists" \
  --dotnetprofile=unityaot --dotnetruntime=il2cpp \
  --platform=iOS --architecture=ARM64 \
  --core-action=link --user-action=copy \
  --rule-set=Conservative --include-profile-search-dirs \
  --use-editor-options 2>&1 | tail -20

# 3. il2cpp: C++ + metadata
"$MONO" "$DEPLOY/il2cpp.exe" --convert-to-cpp \
  --emit-null-checks --enable-array-bounds-check \
  --dotnetprofile=unityaot --platform=iOS --architecture=ARM64 --configuration=Release \
  --directory="$LINKED" --generatedcppdir="$CPP" --data-folder="$DATA" \
  --symbols-folder="$W/il2cpp_symbols" 2>&1 | tail -30
echo "C++ files: $(ls "$CPP" | wc -l)  size: $(du -sh "$CPP" | cut -f1)"
