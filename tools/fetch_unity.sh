#!/bin/bash
# Stream-extract only the needed parts of Unity 2019.4.28f1 (Editor + iOS Support)
# without storing the full .pkg files. Payload offsets come from tools/xarls.py.
set -euo pipefail
OUT=${1:-${BS_UNITY:-${BS_WORK:-$HOME/BeatSaber-iOS-work}/unity}}
U=https://download.unity3d.com/download_unity/1381962e9d08
mkdir -p "$OUT"
cd "$OUT"

# iOS Support: Payload at 96098, length 1004603262
if [ ! -f .ios.done ]; then
  curl -sS --retry 5 -r 96098-$((96098+1004603262-1)) \
    "$U/MacEditorTargetInstaller/UnitySetup-iOS-Support-for-Editor-2019.4.28f1.pkg" \
  | gunzip | bsdtar -xf - \
      --exclude '*-dev.a' --exclude '*-dev-*' --exclude '*debug*' --exclude '*amd64*' \
      --exclude '*arm32*' --exclude '*x64_managed*' --exclude '*Development*'
  touch .ios.done
  echo "ios done $(date)"
fi

# Editor: Payload at 2057019, length 2147297691
if [ ! -f .editor.done ]; then
  curl -sS --retry 5 -r 2057019-$((2057019+2147297691-1)) \
    "$U/MacEditorInstaller/Unity-2019.4.28f1.pkg" \
  | gunzip | bsdtar -xf - \
      --include '*Contents/il2cpp/*' --include '*Contents/MonoBleedingEdge/*' \
      --include '*Contents/PlaybackEngines/MacStandaloneSupport/*' \
      --include '*Contents/Managed/*' --include '*Contents/CGIncludes/*' \
      --exclude '*MacStandaloneSupport/Variations/*il2cpp*'
  touch .editor.done
  echo "editor done $(date)"
fi
