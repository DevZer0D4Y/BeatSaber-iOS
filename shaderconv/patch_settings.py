#!/usr/bin/env python3
"""Patch globalgamemanagers for an Apple Metal, non-VR player:
BuildSettings.m_GraphicsAPIs -> [Metal], no VR devices, multi-pass stereo path.
--ios also restricts orientation to landscape.
usage: patch_settings.py <globalgamemanagers> [out] [--ios]"""
import sys, UnityPy

METAL = 16


def patch(path, out=None, ios=False):
    env = UnityPy.load(path)
    done = []
    for obj in env.objects:
        if obj.type.name == "BuildSettings":
            tt = obj.read_typetree()
            tt["m_GraphicsAPIs"] = [METAL]
            tt["enabledVRDevices"] = []
            obj.save_typetree(tt); done.append("BuildSettings")
        elif obj.type.name == "PlayerSettings":
            tt = obj.read_typetree()
            tt["m_StereoRenderingPath"] = 0
            if ios:   # landscape only, either way round
                tt["defaultScreenOrientation"] = 4          # AutoRotation
                tt["allowedAutorotateToPortrait"] = False
                tt["allowedAutorotateToPortraitUpsideDown"] = False
                tt["allowedAutorotateToLandscapeLeft"] = True
                tt["allowedAutorotateToLandscapeRight"] = True
                tt["useOSAutorotation"] = True
            obj.save_typetree(tt); done.append("PlayerSettings")
        elif obj.type.name == "AudioManager":
            # the PC build names Windows Mixed Reality's spatializer, which Apple players lack
            tt = obj.read_typetree()
            if tt.get("m_SpatializerPlugin") or tt.get("m_AmbisonicDecoderPlugin"):
                tt["m_SpatializerPlugin"] = ""
                tt["m_AmbisonicDecoderPlugin"] = ""
                obj.save_typetree(tt); done.append("AudioManager")
    f = next(iter(env.files.values()))
    # serialize before opening the output: UnityPy reads untouched objects lazily from the
    # source file, so truncating it first (in-place patch) writes them out empty
    data = f.save()
    with open(out or path, "wb") as fh:
        fh.write(data)
    return done


if __name__ == "__main__":
    args = [a for a in sys.argv[1:] if a != "--ios"]
    print(patch(args[0], args[1] if len(args) > 1 else None, ios="--ios" in sys.argv))
