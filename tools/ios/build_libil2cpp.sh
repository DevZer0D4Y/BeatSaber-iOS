#!/bin/bash
# Compile the IL2CPP runtime with HybridCLR (an IL interpreter) into a static library that
# replaces Unity's prebuilt libil2cpp. The source tree is Unity 2019.4.28's libil2cpp with
# HybridCLR's il2cpp_plus changes merged in, plus HybridCLR itself in libil2cpp/hybridclr
# (see tools/hybridclr/prepare.sh). external/ needs Unity's zlib, google (sparsehash) and xxHash.
#   usage: build_libil2cpp.sh <tree with libil2cpp/ and external/zlib> <out.a>
#   env:   OBJ   object folder (default <out dir>/libil2cpp_obj), JOBS (default 2)
#          TARGET ios (default) | mac (x86_64, for the Mac IL2CPP test player)
set -euo pipefail
TREE=$(cd "$1" && pwd); OUTLIB=$2
OBJ=${OBJ:-$(dirname "$OUTLIB")/libil2cpp_obj}
JOBS=${JOBS:-2}
TARGET=${TARGET:-ios}
case "$TARGET" in
  ios) SDKN=iphoneos; TRIPLE=arm64-apple-ios12.0 ;;
  mac) SDKN=macosx;   TRIPLE=x86_64-apple-macos10.13 ;;
  *) echo "TARGET must be ios or mac"; exit 1 ;;
esac
SDK=$(xcrun --sdk $SDKN --show-sdk-path)
BDWGC_INC=${BDWGC_INC:?set BDWGC_INC to Unity il2cpp/external/bdwgc/include}

COMMON="-target $TRIPLE -isysroot $SDK -Os -fno-strict-overflow -fno-strict-aliasing -w \
 -DRUNTIME_IL2CPP=1 -DNET_4_0 -DNDEBUG -I$TREE/libil2cpp -I$TREE -I$BDWGC_INC"
# -include il2cpp-config.h: some headers test config macros (e.g. IL2CPP_SUPPORT_THREADS in
# os/ThreadLocalValue.h) before anything includes the config. Unity's merged-file build hides
# that; compiled file by file, HybridCLR's thread-local interpreter state ended up unconstructed.
export CXXFLAGS="$COMMON -std=c++14 -fexceptions -include il2cpp-config.h"
# -Dfdopen=fdopen: zlib's zutil.h otherwise turns fdopen into a macro on Apple SDKs
export CFLAGS="$COMMON -std=gnu99 -Dfdopen=fdopen"

mkdir -p "$OBJ"
flags="$CXXFLAGS|$CFLAGS"
if [ "$(cat "$OBJ/flags.txt" 2>/dev/null)" != "$flags" ]; then
  find "$OBJ" -name '*.o' -delete; printf '%s' "$flags" > "$OBJ/flags.txt"
fi

list=$OBJ/list.txt; : > "$list"
( cd "$TREE" && find libil2cpp external/zlib \( -name '*.cpp' -o -name '*.c' -o -name '*.mm' \) \
    -not -path '*/os/c-api/tests/*' -not -path '*/os/Win32/*' -not -path '*/os/WinRT/*' \
    -not -path 'libil2cpp/debugger/*' -not -path 'libil2cpp/hybridclr/generated/MethodBridge*' ) |
while IFS= read -r rel; do
  echo "$rel|$OBJ/$(echo "${rel%.*}" | tr '/ ' '__').o" >> "$list"
done

compile_one() {
  local rel=$1 obj=$2 src="$TREE/$1"
  [ -f "$obj" ] && [ "$obj" -nt "$src" ] && return 0
  case "$rel" in
    *.c)  xcrun --sdk $SDKN clang   $CFLAGS -c "$src" -o "$obj.tmp" ;;
    *.mm) xcrun --sdk $SDKN clang++ $CXXFLAGS -x objective-c++ -c "$src" -o "$obj.tmp" ;;
    *)    xcrun --sdk $SDKN clang++ $CXXFLAGS -c "$src" -o "$obj.tmp" ;;
  esac && mv "$obj.tmp" "$obj" || { echo "FAILED: $rel" >&2; rm -f "$obj.tmp"; return 1; }
}
export -f compile_one; export TREE SDKN

echo "libil2cpp: compiling $(wc -l < "$list") files with $JOBS jobs"
tr '\n' '\0' < "$list" | nice -n 19 xargs -0 -n1 -P "$JOBS" bash -c 'IFS="|" read -r r o <<< "$0"; compile_one "$r" "$o"' ||
  { echo "libil2cpp compile failed"; exit 1; }
cut -d'|' -f2 "$list" > "$OBJ/objs.txt"
rm -f "$OUTLIB"
xcrun libtool -static -no_warning_for_no_symbols -o "$OUTLIB" -filelist "$OBJ/objs.txt"
ls -la "$OUTLIB"
