#!/bin/bash
# Build the iOS app from Unity 2019.4.28f1's iOS trampoline + IL2CPP output, without Xcode
# projects: one arm64 executable (UnityFramework code linked in), unsigned (sign it with your sideloading tool).
#
# usage: build_ios.sh <phase...>   phases: compile link bundle ipa   (default: all)
# env:   BS_WORK    work folder (default ~/BeatSaber-iOS-work): ios/ holds the il2cpp output
#        BS_UNITY   Unity 2019.4.28f1 parts from tools/fetch_unity.sh (default $BS_WORK/unity)
#        ONLY=tramp compile only the trampoline + port glue
#        IOS_DATA   converted Data folder to put in the bundle (bundle phase)
#        APP_ICON   image for the home screen icon (bundle phase; cropped to a centred square)
#        BUNDLE_ID  app identifier (default com.devz.beatsaber)
#        JOBS       parallel compile jobs (default 2)
set -euo pipefail

W=${BS_WORK:-$HOME/BeatSaber-iOS-work}
U=${BS_UNITY:-$W/unity}
TR=$U/iOSSupport/Trampoline
IL2CPP_INC=$U/iOSSupport/il2cpp/libil2cpp/include
BDWGC_INC=$U/Unity/Unity.app/Contents/il2cpp/external/bdwgc/include
IL=$W/ios/Il2CppOutput
IL_DATA=$W/ios/il2cpp_data
SRC=$(cd "$(dirname "$0")/../.." && pwd)            # repository root
NATIVE=$SRC/port/native
OBJ=${OBJ:-$W/obj}
OUT=${OUT:-$W/ios/out}
APP=$OUT/Payload/BeatSaber.app
JOBS=${JOBS:-2}
MIN_IOS=12.0
LINK_SDK=18.0                                         # SDK version written into the executable (see link)
BUNDLE_ID=${BUNDLE_ID:-com.devz.beatsaber}

SDK=$(xcrun --sdk iphoneos --show-sdk-path)
CC="xcrun --sdk iphoneos clang"
CXX="xcrun --sdk iphoneos clang++"
ARCH="-target arm64-apple-ios$MIN_IOS -isysroot $SDK"
DEFS="-DINIT_SCRIPTING_BACKEND=1 -DRUNTIME_IL2CPP=1 -DNET_4_0 -DNDEBUG"
# -fno-strict-aliasing: IL2CPP 2019.4 output type-puns (delegate Invoke reads `this` through a
# Delegate_t** cast); current clang drops that spill under strict aliasing and Invoke reads garbage.
COMMON="$ARCH -Os -fno-strict-overflow -fno-strict-aliasing -fvisibility=hidden -w $DEFS"
IL_CXX="$COMMON -std=c++11 -fexceptions -I$IL -I$IL2CPP_INC -I$BDWGC_INC"
IL_C="$COMMON -std=gnu99 -I$IL -I$IL2CPP_INC -I$BDWGC_INC"
# Xcode header maps make every project header reachable by bare name: add each Classes subdir
TR_DIRS=$(find "$TR/Classes" -type d | sed 's/^/-I/' | tr '\n' ' ')
TR_INC="$TR_DIRS -I$TR -I$TR/Libraries -I$IL2CPP_INC -I$BDWGC_INC -include $TR/Classes/Prefix.pch"
TR_OBJC="$COMMON -fobjc-arc -fexceptions $TR_INC"

phases=("$@"); [ ${#phases[@]} -eq 0 ] && phases=(compile link bundle ipa)
has() { local p; for p in "${phases[@]}"; do [ "$p" = "$1" ] && return 0; done; return 1; }

# compile one source -> object, skipping up-to-date objects
compile_one() {   # kind src obj
  local kind=$1 src=$2 obj=$3
  [ -f "$obj" ] && [ "$obj" -nt "$src" ] && return 0
  mkdir -p "$(dirname "$obj")"
  case $kind in
    ilcpp) $CXX $IL_CXX -c "$src" -o "$obj.tmp" ;;
    ilc)   $CC  $IL_C   -c "$src" -o "$obj.tmp" ;;
    objcxx) $CXX $TR_OBJC -std=c++11 -x objective-c++ -c "$src" -o "$obj.tmp" ;;
    objc)  $CC  $TR_OBJC -x objective-c -c "$src" -o "$obj.tmp" ;;
    cxx)   $CXX $COMMON -std=c++11 -fexceptions $TR_INC -c "$src" -o "$obj.tmp" ;;
  esac && mv "$obj.tmp" "$obj" || { echo "FAILED: $src" >&2; rm -f "$obj.tmp"; return 1; }
}
export -f compile_one
export CC CXX IL_CXX IL_C TR_OBJC TR_INC COMMON

if has compile; then
  # objects are reused by timestamp only: start over when the compile flags change
  mkdir -p "$OBJ"
  flags="$IL_CXX|$IL_C|$TR_OBJC"
  if [ "$(cat "$OBJ/flags.txt" 2>/dev/null)" != "$flags" ]; then
    echo "compile flags changed: rebuilding every object"
    rm -rf "$OBJ/il2cpp" "$OBJ/tramp" "$OBJ/native" "$OBJ/gen"
    printf '%s' "$flags" > "$OBJ/flags.txt"
  fi
  list=$OBJ/compile_list.txt; : > "$list"
  # IL2CPP output (largest first so the long files start early); ONLY=tramp skips it
  [ "${ONLY:-}" = tramp ] || ls -S "$IL"/*.cpp "$IL"/*.c | while IFS= read -r f; do   # names may contain spaces
    b=$(basename "$f"); k=ilcpp; [ "${b##*.}" = c ] && k=ilc
    echo "$k|$f|$OBJ/il2cpp/${b// /_}.o" >> "$list"
  done
  # trampoline: UnityFramework target sources
  python3 "$SRC/tools/ios/pbx_sources.py" "$TR/Unity-iPhone.xcodeproj/project.pbxproj" UnityFramework |
  awk -F'\t' '$1=="PBXSourcesBuildPhase"{print $2}' | while read -r rel; do
    case "$rel" in
      Libraries/RegisterMonoModules.cpp) continue ;;   # replaced by port/native/UnityClassRegistration.cpp
    esac
    case "${rel##*.}" in mm) k=objcxx;; m) k=objc;; *) k=cxx;; esac
    echo "$k|$TR/$rel|$OBJ/tramp/${rel//\//_}.o" >> "$list"
  done
  # the port's native glue + single-binary main
  for f in "$NATIVE"/*.cpp "$NATIVE"/*.mm; do
    case "${f##*.}" in mm) k=objcxx;; *) k=cxx;; esac
    echo "$k|$f|$OBJ/native/$(basename "$f").o" >> "$list"
  done
  # engine internal calls the managed code uses (the editor normally generates this)
  mkdir -p "$OBJ/gen"
  python3 "$SRC/tools/ios/gen_icalls.py" "$IL" "$TR/Libraries/libiPhone-lib-il2cpp.a" "$OBJ/gen/InternalCalls.cpp.new"
  cmp -s "$OBJ/gen/InternalCalls.cpp.new" "$OBJ/gen/InternalCalls.cpp" && rm "$OBJ/gen/InternalCalls.cpp.new" ||
    mv "$OBJ/gen/InternalCalls.cpp.new" "$OBJ/gen/InternalCalls.cpp"
  echo "cxx|$OBJ/gen/InternalCalls.cpp|$OBJ/gen/InternalCalls.o" >> "$list"
  # link takes every object in $OBJ: drop those whose source is gone (il2cpp's file set
  # changes with what the linker keeps, e.g. System.Core1-4.cpp)
  cut -d'|' -f3 "$list" | sort > "$OBJ/expected.txt"
  find "$OBJ" -name '*.o' | sort | comm -23 - "$OBJ/expected.txt" |
  while IFS= read -r o; do
    case "$o" in "$OBJ"/il2cpp/*) [ "${ONLY:-}" = tramp ] && continue ;; esac
    echo "stale object: $o"; rm -f "$o"
  done
  echo "compiling $(wc -l < "$list") files with $JOBS jobs"
  tr '\n' '\0' < "$list" | nice -n 19 xargs -0 -n1 -P "$JOBS" bash -c 'IFS="|" read -r k s o <<< "$0"; compile_one "$k" "$s" "$o"' || { echo "compile failed"; exit 1; }
  echo "compile done: $(find "$OBJ" -name '*.o' | wc -l) objects, $(du -sh "$OBJ" | cut -f1)"
fi

if has link; then
  mkdir -p "$APP"
  find "$OBJ" -name '*.o' > "$OBJ/link_list.txt"
  nice -n 19 $CXX $ARCH -o "$APP/BeatSaber" -filelist "$OBJ/link_list.txt" \
    "$TR/Libraries/libiPhone-lib-il2cpp.a" "$TR/Libraries/libil2cpp-net40.a" \
    -dead_strip -Wl,-no_deduplicate \
    -framework Foundation -framework UIKit -framework QuartzCore -framework CoreGraphics \
    -framework Metal -framework AudioToolbox -framework AVFoundation -framework AVKit \
    -framework CFNetwork -framework CoreMedia -framework CoreText -framework CoreVideo \
    -framework MediaToolbox -framework Security -framework SystemConfiguration \
    -framework GameController -framework CoreLocation -framework StoreKit \
    -weak_framework CoreMotion -weak_framework ReplayKit -weak_framework OpenGLES -weak_framework OpenAL \
    -Wl,-rpath,/usr/lib/swift -Wl,-rpath,@executable_path/Frameworks \
    -liconv -lz -lc++ 2>&1 | grep -v "^ld: warning: .*was built for newer\|object file.*was built for" | head -60
  [ -f "$APP/BeatSaber" ] || { echo "link failed"; exit 1; }
  # iOS 27 refuses to launch apps linked against its SDK that lack the UIScene life cycle,
  # which Unity 2019.4's trampoline predates: record an older SDK so UIKit keeps the legacy path
  xcrun vtool -set-build-version ios $MIN_IOS $LINK_SDK -replace -output "$APP/BeatSaber.tmp" "$APP/BeatSaber"
  mv "$APP/BeatSaber.tmp" "$APP/BeatSaber"
  ls -la "$APP/BeatSaber"
fi

if has bundle; then
  : "${IOS_DATA:?set IOS_DATA to the converted iOS Data folder}"
  mkdir -p "$APP"
  cp "$SRC/tools/ios/Info.plist" "$APP/Info.plist"
  plutil -replace CFBundleIdentifier -string "$BUNDLE_ID" "$APP/Info.plist"
  # launch screen: Unity's SplashScreen asserts unless UILaunchStoryboardName names a nib
  xcrun ibtool --errors --compile "$APP/LaunchScreen.nib" "$SRC/tools/ios/LaunchScreen.xib" \
    --minimum-deployment-target $MIN_IOS --target-device iphone --target-device ipad > /dev/null
  [ -e "$APP/LaunchScreen.nib" ] || { echo "launch screen compile failed"; exit 1; }
  # home screen icon: an asset catalog compiled by actool from APP_ICON (any image size)
  if [ -n "${APP_ICON:-}" ]; then
    T=$(mktemp -d); SET=$T/Assets.xcassets/AppIcon.appiconset; mkdir -p "$SET"
    printf '{"info":{"version":1,"author":"xcode"}}' > "$T/Assets.xcassets/Contents.json"
    w=$(sips -g pixelWidth "$APP_ICON" | awk '/pixelWidth/{print $2}')
    h=$(sips -g pixelHeight "$APP_ICON" | awk '/pixelHeight/{print $2}')
    s=$(( w < h ? w : h ))
    sips -c "$s" "$s" -s format png "$APP_ICON" --out "$T/square.png" > /dev/null
    imgs=""
    for e in "iphone 20 2" "iphone 20 3" "iphone 29 2" "iphone 29 3" "iphone 40 2" "iphone 40 3" "iphone 60 2" "iphone 60 3" \
             "ipad 20 1" "ipad 20 2" "ipad 29 1" "ipad 29 2" "ipad 40 1" "ipad 40 2" "ipad 76 1" "ipad 76 2" "ipad 83.5 2" "ios-marketing 1024 1"; do
      set -- $e
      px=$(python3 -c "print(round($2*$3))"); f="icon-$1-$2@$3x.png"
      sips -z "$px" "$px" "$T/square.png" --out "$SET/$f" > /dev/null
      imgs="$imgs${imgs:+,}{\"idiom\":\"$1\",\"size\":\"$2x$2\",\"scale\":\"$3x\",\"filename\":\"$f\"}"
    done
    printf '{"images":[%s],"info":{"version":1,"author":"xcode"}}' "$imgs" > "$SET/Contents.json"
    xcrun actool --compile "$APP" --platform iphoneos --minimum-deployment-target $MIN_IOS \
      --app-icon AppIcon --output-partial-info-plist "$T/icon.plist" \
      --target-device iphone --target-device ipad "$T/Assets.xcassets" > /dev/null
    /usr/libexec/PlistBuddy -c "Merge $T/icon.plist" "$APP/Info.plist" > /dev/null
    rm -rf "$T"
    [ -f "$APP/Assets.car" ] || { echo "icon compile failed"; exit 1; }
  fi
  # Data: move (not copy) the converted data in; disk space is tight
  if [ "$IOS_DATA" != "$APP/Data" ]; then rm -rf "$APP/Data"; mv "$IOS_DATA" "$APP/Data"; fi
  D=$APP/Data
  cp "$TR/Data/unity default resources" "$D/unity default resources"
  mkdir -p "$D/Managed"
  rm -rf "$D/Managed/"*.dll "$D/Managed/Metadata" "$D/Managed/Resources" "$D/Managed/etc"
  cp -R "$IL_DATA/Metadata" "$IL_DATA/Resources" "$IL_DATA/etc" "$D/Managed/"
  [ -d "$D/StreamingAssets" ] && { rm -rf "$D/Raw"; mv "$D/StreamingAssets" "$D/Raw"; }
  # PC-only content: native plugins, Mono, XR subsystem manifests, the Windows default resources
  rm -rf "$D/Plugins" "$D/MonoBleedingEdge" "$D/UnitySubsystems" "$D/Resources/unity default resources"
  # the executable may have been linked into another OUT; bring it along
  EXE=${EXE:-$W/ios/out/Payload/BeatSaber.app/BeatSaber}
  if [ "$EXE" != "$APP/BeatSaber" ] && ! cmp -s "$EXE" "$APP/BeatSaber"; then cp "$EXE" "$APP/BeatSaber"; fi
  find "$APP" -name '._*' -delete
  # files copied from some file systems (e.g. exFAT) come out owner-only (rwx------); the app runs as another user than
  # the bundle's owner, so unreadable metadata = "Unable to find type" for every class
  find "$APP" -type d -exec chmod 755 {} +
  find "$APP" -type f -exec chmod 644 {} +
  chmod 755 "$APP/BeatSaber"
  echo "bundle: $(du -sh "$APP" | cut -f1)"
fi

if has ipa; then
  rm -f "$OUT/BeatSaber.ipa"
  (cd "$OUT" && nice -n 19 zip -qry -1 BeatSaber.ipa Payload)
  ls -la "$OUT/BeatSaber.ipa"
fi
