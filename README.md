# Beat Saber for iPhone and iPad (unofficial, non-VR)

An unofficial flat-screen port of the PC version of Beat Saber 1.29.1 to iOS and iPadOS, played
with touch instead of a VR headset. It runs **your own copy of the game**.

**The app contains no game files.** No game code, no music, no art, no levels: you add those
yourself, from the copy of Beat Saber you own on Steam. This repository, and the app published
from it, only contain the port's own code, the tools to convert your copy, and the open-source
and runtime parts listed under [Credits](#credits).

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
  - songs added with the Files app, AirDrop or a computer (as `.zip` files or song folders) are
    checked and installed automatically, even while the game is running.
- Unity's engine runs natively (Metal); the game's own code, loaded from your files, runs in the
  [HybridCLR](https://github.com/focus-creative-games/hybridclr) interpreter. No JIT, no
  emulation.

## How it works

The app is Unity 2019.4's iOS player with the game's code left out. When it starts, it loads the
game's code (the `.dll` files of your copy, patched for touch and iOS) and the game's data from
the `GameData` folder you put on your device, and runs them. Without that folder the app only
shows how to add it.

## Which game version do I need?

**Only Beat Saber 1.29.1 for Windows, from Steam.**

- **The latest Steam version does not work.** Beat Saber 1.29.1 is the last version built on
  Unity 2019; later versions use a newer Unity with different engine files, shader formats and
  data layout.
- **Older versions are not supported.** The port patches specific methods of the 1.29.1 game
  code. With any other version the import stops with "is this Beat Saber 1.29.1?".
- **Other stores are not supported:** the Meta/Oculus PC version and the Quest version are
  different builds.

Steam installs the newest version by default, but owners can download 1.29.1 with Steam's own
`download_depot` console command. In the Steam console (`steam://open/console`):

```
download_depot 620980 620981 886973241045584398
```

The manifest ID is the 1.29.1 one at the time of writing (check SteamDB if it fails). It only
works for accounts that own the game. You need the `Beat Saber_Data` folder from the download.

## Installing

### 1. The app

Download `BeatSaber.ipa` from [Releases](../../releases) and install it with your sideloading
tool (AltStore, SideStore, your own developer account, ...). You can also build it yourself, see
[Building the app](#building-the-app).

### 2. Your game files

Converting your copy needs a Mac (Apple Silicon or Intel) with the Xcode command line tools,
Python 3, CMake, Ninja and the .NET 10 SDK, and about 6 GB of free space. Once, set up the helper
tools:

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

Then convert your copy (this takes a while, mostly for the textures):

```sh
tools/import_game.sh "/path/to/depot_620981/Beat Saber_Data" ~/BeatSaberImport
```

It converts the shaders to Metal and the textures to ASTC, adapts the player settings for iOS,
and patches the game's assemblies for touch input, custom songs and iOS. Nothing is uploaded
anywhere.

### 3. Copy them to your device

Copy the resulting `GameData` folder (about 1.7 GB) into the app's folder:

- **From a Mac:** Finder › your iPhone/iPad › Files › Beat Saber, and drop `GameData` on it.
- **From Windows:** the Apple Devices app (or iTunes) › your device › Files › Beat Saber.
- **On the device:** Files › On My iPhone › Beat Saber › GameData (for example via AirDrop or a
  cloud drive).

Open the app. If something is missing, it tells you which files.

### Can I convert on Windows or Linux?

Not at the moment: the texture encoder uses macOS's ImageIO. The other steps (shader translation,
the patcher) could run elsewhere.

## Building the app

Only needed if you don't use the release. Building also needs a Mac with Xcode, Rosetta 2 on
Apple Silicon (Unity's 2019 build tools are x86_64), about 15 GB of free space, and your copy of
the game: the linker uses the game's assemblies to decide which parts of the .NET class library
and Unity's engine to keep. None of the game's code is compiled into the app.

```sh
export BS_WORK=~/BeatSaber-iOS-work
GAME="/path/to/depot_620981/Beat Saber_Data"

tools/fetch_unity.sh                          # Unity 2019.4.28f1 parts, from Unity's servers
tools/hybridclr/prepare.sh "$BS_WORK/hclr"    # IL2CPP runtime + HybridCLR (see the script)
dotnet build tools/bstool -c Release

tools/build_port.sh "$GAME/Managed" "$BS_WORK/port"
tools/bstool/bin/Release/net10.0/bstool patch "$GAME/Managed" "$BS_WORK/patched" "$BS_WORK/port/BeatSaberPort.dll"
mkdir -p "$BS_WORK/managed" && cp "$GAME/Managed"/*.dll "$BS_WORK/patched"/*.dll "$BS_WORK/managed/" && rm "$BS_WORK/managed/BeatSaberPort.dll"
HCLR_SRC="$BS_WORK/hclr" tools/il2cpp_hclr.sh "$BS_WORK/managed" "$BS_WORK/port" "$BS_WORK/il2cpp"
TARGET=ios BDWGC_INC="$BS_WORK/unity/Unity/Unity.app/Contents/il2cpp/external/bdwgc/include" \
  tools/ios/build_libil2cpp.sh "$BS_WORK/hclr/tree" "$BS_WORK/libil2cpp-hclr.a"
HCLR="$BS_WORK/il2cpp" HCLR_SRC="$BS_WORK/hclr" HCLR_LIB="$BS_WORK/libil2cpp-hclr.a" \
  PORT_DLL="$BS_WORK/port/BeatSaberPort.dll" tools/ios/build_ios.sh
```

The unsigned `BeatSaber.ipa` ends up in `$BS_WORK/ios/out`. Optional settings:

- `APP_ICON=/path/to/image.png`: an image of your choice for the home screen icon (cropped to a
  centred square). The repository does not ship one.
- `BUNDLE_ID=...`: the app identifier (default `com.devz.beatsaber`).
- `JOBS=4`: parallel compile jobs (default 2).

## Custom songs

Open **SONGS +** in the main menu to browse and download maps from
[BeatSaver](https://beatsaver.com), then find them under Solo › Custom Songs.

You can also add songs yourself, in the same Beat Saber folder as `GameData`:

- **`.zip` files** (as downloaded from BeatSaver): put them in the `Beat Saber` folder or in
  `Beat Saber/CustomLevels`. They are installed and the zip is deleted. A zip that can't be used
  is renamed to `.zip.failed`.
- **Song folders** (already unzipped, with `Info.dat`, the audio, the cover and the difficulty
  files): put them in `Beat Saber/CustomLevels`.

New songs are picked up when you start the game, when you come back to it, when you open
**SONGS +**, and every few seconds while you are in the menus. Each song is checked once the
same way as a download: maps in the newer v4 format are converted, file-name case is fixed and
difficulties that need PC mods are removed. A song folder that can't be used is left exactly as
it is.

Maps are made by the community; check the license of what you download.

## Credits

- [HybridCLR](https://github.com/focus-creative-games/hybridclr) and
  [il2cpp_plus](https://github.com/focus-creative-games/il2cpp_plus) (MIT), Code Philosophy: the
  IL interpreter that runs the game's code. Version 8.15.0, the last release supporting
  Unity 2019, merged into Unity 2019.4.28's IL2CPP runtime by `tools/hybridclr/prepare.sh`.
- [HLSLcc](https://github.com/Unity-Technologies/HLSLcc) (Unity), shader translation.
- [UnityPy](https://github.com/K0lb3/UnityPy) (MIT), reading and writing Unity data files.
- [Mono.Cecil](https://github.com/jbevain/cecil) (MIT), patching assemblies.
- The released app includes runtime parts of Unity 2019.4.28f1 (the iOS player engine, the
  IL2CPP runtime and Unity's build of the Mono class library), downloaded from Unity's servers
  at build time, under Unity's terms.

## Legal

- This is an unofficial fan project. It is not affiliated with, endorsed by or supported by
  Beat Games, Meta Platforms or Unity Technologies. Beat Saber is a trademark of Beat Games.
- No game files, game code, game assets or game art are included in this repository or in the
  released app. You must supply your own legally purchased copy of the game; the project does
  not help you obtain the game in any other way.
- You are responsible for making sure that your use of these tools complies with the game's
  EULA, Steam's terms, Unity's license terms and the laws of your country. Use it at your own
  risk.
- Never share your `GameData` folder: it contains the game's files. Do not post game files in
  issues or pull requests.
- The code in this repository is released under the MIT License (see `LICENSE`) and is provided
  "as is", without warranty of any kind.
