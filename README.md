# Beat Saber for iPhone and iPad (unofficial, non-VR)

An unofficial flat-screen port of the PC version of Beat Saber 1.29.1 to iOS and iPadOS, played
with touch instead of a VR headset. It is built on your own Mac from **your own copy of the game**.

**This repository contains no game files** — no assets, no game code, no music, no art — and no
Unity binaries. It only contains the tools and the port code that turn a copy you already own
into an app for your own device.

## Features

- Touch controls: the left half of the screen holds the left saber, the right half the right
  saber. Each saber is held below the screen and swings toward your finger, so a swipe swings the
  blade through the notes. Tap to use the menus; tap the top centre of the screen to pause.
- A fixed menu camera that shows every menu panel at once (pinch or drag with two fingers to
  zoom and pan).
- Custom songs, using the game's own built-in custom level support:
  - an in-game BeatSaver browser (**SONGS +** button): search, top rated, latest, curated and
    ranked maps, previews, one-tap download, and a list of installed songs;
  - maps in the newer v4 format are converted on install to a format 1.29.1 can play;
  - difficulties that need PC mods (Noodle Extensions, Mapping Extensions, Vivify) are skipped;
  - song `.zip` files copied into the app's folder with the Files app are installed automatically.
- Runs natively (IL2CPP, Metal); no emulation and no JIT.

## Which game version do I need?

**Only Beat Saber 1.29.1 for Windows, from Steam.**

- **The latest Steam version does not work.** Beat Saber 1.29.1 is the last version built on
  Unity 2019; later versions use a newer Unity with different engine files, shader formats and
  data layout, so this port cannot build them.
- **Older versions are not supported.** The port patches specific methods of the 1.29.1 game
  code. With any other version the patch step prints `NOT FOUND` for the patches that don't
  match and stops with "is this Beat Saber 1.29.1?".
- **Other stores are not supported:** the Meta/Oculus PC version and the Quest version are
  different builds.

Steam installs the newest version by default, but owners can download 1.29.1 with Steam's own
`download_depot` console command (see step 1 below). It only works for accounts that own the
game.

### Why is there no ready-made app to download?

The app contains the game's own code compiled for iOS, together with the game's data. Sharing
it would mean distributing Beat Games' work, so everyone builds the app themselves, on their own
Mac, from their own copy of the game. For the same reason, please never share the `.ipa` you
build.

### Can I build on Windows or Linux?

Not at the moment: building needs a Mac. The app is compiled and linked against Apple's iOS SDK
with Xcode's tools (`clang`, `ld`, `vtool`, `ibtool`, `actool`), and textures are encoded with
macOS's ImageIO. The other steps (game data conversion, the patcher, the code generator) could
run elsewhere, but the final build cannot yet. Any Apple Silicon or Intel Mac that runs a
current Xcode works. Signing and installing the finished `.ipa` can be done from any computer
with your usual sideloading tool.

## Requirements

- Beat Saber 1.29.1 on **Steam**, bought with your own account (see above).
- A Mac with Xcode (iOS SDK, `ibtool`, `actool`), CMake and Ninja, the .NET 10 SDK, Python 3,
  and Rosetta 2 on Apple Silicon (Unity's 2019 build tools are x86_64).
- About 25 GB of free disk space.
- A way to sideload apps (AltStore, SideStore, your own developer account, ...).

## Building

Set a work folder (anything outside this repository); the scripts use `~/BeatSaber-iOS-work`
when `BS_WORK` is not set.

```sh
export BS_WORK=~/BeatSaber-iOS-work
```

### 1. Your game files

Download the Windows build of Beat Saber **1.29.1** from your own Steam account. In the Steam
console (`steam://open/console`):

```
download_depot 620980 620981 886973241045584398
```

The manifest ID is the 1.29.1 one at the time of writing (check SteamDB if it fails). You need
the `Beat Saber_Data` folder from the download. Below, `GAME` is that folder:

```sh
GAME="/path/to/depot_620981/Beat Saber_Data"
```

### 2. Unity 2019.4.28f1 parts

`tools/fetch_unity.sh` downloads only the parts of the Unity 2019.4.28f1 editor and iOS build
support that the build needs, from Unity's servers. Unity's license terms apply to them.

```sh
tools/fetch_unity.sh
```

### 3. Helper tools

```sh
mkdir -p build
python3 -m venv build/venv && build/venv/bin/pip install UnityPy==1.25.4 lz4
xcrun swiftc -O tools/astc/astc_imageio.swift -o build/astc_imageio
git clone https://github.com/Unity-Technologies/HLSLcc build/HLSLcc
cmake -S build/HLSLcc -B build/HLSLcc/build -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build build/HLSLcc/build
clang++ -std=c++14 -O2 -Ibuild/HLSLcc/include -Ibuild/HLSLcc/src/internal_includes \
  shaderconv/hlslcc_metal.cpp -Lbuild/HLSLcc/build -lhlslcc \
  -Wl,-rpath,"$PWD/build/HLSLcc/build" -o build/hlslcc_metal
dotnet build tools/bstool -c Release
```

### 4. Convert the game data

Shaders are translated from Direct3D 11 to Metal, then textures are re-encoded as ASTC for iOS:

```sh
PY=build/venv/bin/python
$PY shaderconv/convert_data.py "$GAME" "$BS_WORK/data_metal" build/hlslcc_metal osx
$PY shaderconv/convert_data.py "$BS_WORK/data_metal" "$BS_WORK/data_ios" none ios
$PY shaderconv/patch_settings.py "$BS_WORK/data_ios/globalgamemanagers" --ios
sed -i '' -e 's/^vr-enabled=1/vr-enabled=0/' -e '/^vr-device-list=/d' "$BS_WORK/data_ios/boot.config"
```

### 5. Build the port and patch the game code

```sh
MANAGED="$GAME/Managed"
tools/build_port.sh "$MANAGED" "$BS_WORK/port"
tools/bstool/bin/Release/net10.0/bstool patch "$MANAGED" "$BS_WORK/patched" "$BS_WORK/port/BeatSaberPort.dll"
tools/il2cpp_ios.sh "$MANAGED" "$BS_WORK/patched" "$BS_WORK/ios"
```

### 6. Build the app

```sh
IOS_DATA="$BS_WORK/data_ios" tools/ios/build_ios.sh
```

The unsigned `BeatSaber.ipa` ends up in `$BS_WORK/ios/out`. Sign and install it with your
sideloading tool. Optional settings:

- `APP_ICON=/path/to/image.png`: an image of your choice for the home screen icon (cropped to a
  centred square). The repository does not ship one.
- `BUNDLE_ID=...`: the app identifier (default `com.devz.beatsaber`).
- `JOBS=4`: parallel compile jobs (default 2).

## Custom songs

Open **SONGS +** in the main menu to browse and download maps from
[BeatSaver](https://beatsaver.com), then find them under Solo › Custom Songs. You can also copy
song folders or `.zip` files into the app's folder with the Files app (On My iPhone › Beat Saber).
Maps are made by the community; check the license of what you download.

## Legal

- This is an unofficial fan project. It is not affiliated with, endorsed by or supported by
  Beat Games, Meta Platforms or Unity Technologies. Beat Saber is a trademark of Beat Games.
- No game files, game code, game assets or Unity binaries are included in or distributed with
  this repository. You must supply your own legally purchased copy of the game; the project does
  not help you obtain the game in any other way.
- You are responsible for making sure that your use of these tools complies with the game's
  EULA, Steam's terms, Unity's license terms and the laws of your country. Use it at your own
  risk.
- Do not share the `.ipa` you build: it contains the game's files. Do not post game files in
  issues or pull requests.
- The code in this repository is released under the MIT License (see `LICENSE`) and is provided
  "as is", without warranty of any kind.
