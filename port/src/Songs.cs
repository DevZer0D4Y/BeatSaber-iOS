// Custom songs on the phone. The game has its own (normally hidden) custom level support:
// CustomLevelLoader reads <base>/CustomLevels/<song folder>/Info.dat, where <base> is
// CustomLevelPathHelper.baseProjectPath. bstool points that at GetBasePath() (the app's
// Documents folder on iOS, visible in the Files app) and turns the Custom Songs category on.
// This class installs song zips (from the in-game browser or dropped into Documents), checks
// they suit this game version, and tells an open song list to reload.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace BeatSaberPort
{
    public static class Songs
    {
        static string _base;

        // Called from the patched CustomLevelPathHelper static constructor (in place of
        // Application.dataPath, which is inside the read-only app bundle on iOS).
        public static string GetBasePath()
        {
            if (_base == null) _base = Application.persistentDataPath;
            return _base;
        }

        public static string LevelsDir { get { return Path.Combine(GetBasePath(), "CustomLevels"); } }
        public static string IncomingDir { get { return Path.Combine(GetBasePath(), ".incoming"); } }

        // characteristics this game version can play (the others come from mods)
        static readonly HashSet<string> Characteristics =
            new HashSet<string> { "Standard", "OneSaber", "NoArrows", "90Degree", "360Degree" };

        public class Result
        {
            public bool ok;
            public string folder, message;
        }

        // Main thread, at startup: remember the base path, then (in the background) copy the
        // game's two bundled sample levels into the song folder once and install any zips the
        // user dropped into Documents.
        public static void Init()
        {
            Debug.Log("[BeatSaberPort] custom songs folder " + LevelsDir);
            string bundled = Path.Combine(Application.dataPath, "CustomLevels");
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    string marker = Path.Combine(LevelsDir, ".samples");
                    if (Directory.Exists(bundled) && !File.Exists(marker))
                    {
                        Directory.CreateDirectory(LevelsDir);
                        foreach (var d in Directory.GetDirectories(bundled))
                        {
                            string dst = Path.Combine(LevelsDir, Path.GetFileName(d));
                            if (Directory.Exists(dst)) continue;
                            Directory.CreateDirectory(dst);
                            foreach (var f in Directory.GetFiles(d)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
                        }
                        File.WriteAllText(marker, "sample levels copied\n");
                    }
                    ImportDroppedZips();
                }
                catch (Exception e) { Debug.Log("[BeatSaberPort] songs init: " + e); }
            });
        }

        // Zips put into Documents or Documents/CustomLevels (Files app, AirDrop) get installed.
        public static int ImportDroppedZips()
        {
            int n = 0;
            foreach (var dir in new[] { GetBasePath(), LevelsDir })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var zip in Directory.GetFiles(dir, "*.zip"))
                {
                    var r = InstallZip(zip, Path.GetFileNameWithoutExtension(zip));
                    Debug.Log("[BeatSaberPort] import " + Path.GetFileName(zip) + ": " + (r.ok ? "ok " + r.folder : r.message));
                    if (r.ok) { File.Delete(zip); n++; continue; }
                    if (File.Exists(zip + ".failed")) File.Delete(zip + ".failed");
                    File.Move(zip, zip + ".failed");
                }
            }
            if (n > 0) pendingRefresh = true;
            return n;
        }

        public static volatile bool pendingRefresh;

        // "1f33 (Believer - NovaShaft)", the folder name BeatSaver's own tools use
        public static string FolderName(string key, string song, string mapper)
        {
            string s = key + " (" + song + " - " + mapper + ")";
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c.ToString(), "");
            s = Regex.Replace(s, "[\\\\/:*?\"<>|]", "").Trim();
            return s.Length > 80 ? s.Substring(0, 80).Trim() : s;
        }

        // Unzip a map into CustomLevels/<folder>. Safe to call from a worker thread.
        public static Result InstallZip(string zipPath, string folder)
        {
            string tmp = Path.Combine(IncomingDir, Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tmp);
                using (var fs = File.OpenRead(zipPath))
                using (var za = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    var info = za.Entries
                        .Where(e => e.Name.Equals("info.dat", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(e => e.FullName.Length).FirstOrDefault();
                    if (info == null) return Fail("not a song (no Info.dat in the zip)");
                    string prefix = info.FullName.Substring(0, info.FullName.Length - info.Name.Length);
                    foreach (var e in za.Entries)
                    {
                        if (string.IsNullOrEmpty(e.Name) || !e.FullName.StartsWith(prefix)) continue;
                        string rel = e.FullName.Substring(prefix.Length);
                        if (rel.Contains("/") || rel.Contains("\\")) continue;   // songs are flat; skip editor folders
                        string dst = Path.Combine(tmp, e == info ? "Info.dat" : rel);
                        using (var src = e.Open())
                        using (var o = File.Create(dst)) src.CopyTo(o);
                    }
                }
                string err = Check(tmp);
                if (err != null) return Fail(err);
                Directory.CreateDirectory(LevelsDir);
                string dest = Path.Combine(LevelsDir, folder);
                if (Directory.Exists(dest)) Directory.Delete(dest, true);
                Directory.Move(tmp, dest);
                return new Result { ok = true, folder = folder };
            }
            catch (Exception e)
            {
                return Fail("install failed: " + e.Message);
            }
            finally
            {
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            }
        }

        static Result Fail(string msg) { return new Result { ok = false, message = msg }; }

        // Make an unpacked song loadable by this game version, or say why it can't be.
        // Drops difficulty sets for mod-only characteristics and difficulties whose files are
        // missing or in a newer format; fixes the case of file names (iOS is case-sensitive).
        static string Check(string dir)
        {
            string infoPath = Path.Combine(dir, "Info.dat");
            JObject info;
            try { info = JObject.Parse(File.ReadAllText(infoPath).TrimStart('﻿')); }
            catch (Exception e) { return "broken Info.dat (" + e.Message + ")"; }
            if (MapConvert.IsV4Info(info))
            {
                try { info = MapConvert.ConvertV4(dir, info); }
                catch (Exception e) { return "couldn't convert this newer (v4) map: " + e.Message; }
            }
            bool changed = false;
            string song = (string)info["_songFilename"];
            if (string.IsNullOrEmpty(song) || !FixCase(dir, song, out song)) return "the song's audio file is missing";
            if (song != (string)info["_songFilename"]) { info["_songFilename"] = song; changed = true; }
            string cover = (string)info["_coverImageFilename"];
            if (!string.IsNullOrEmpty(cover) && FixCase(dir, cover, out string c2) && c2 != cover) { info["_coverImageFilename"] = c2; changed = true; }

            var sets = info["_difficultyBeatmapSets"] as JArray;
            if (sets == null) return "no difficulties";
            int playable = 0, modOnly = 0;
            foreach (var set in sets.ToList())
            {
                string ch = (string)set["_beatmapCharacteristicName"];
                var diffs = set["_difficultyBeatmaps"] as JArray;
                if (!Characteristics.Contains(ch ?? "") || diffs == null) { set.Remove(); changed = true; continue; }
                foreach (var d in diffs.ToList())
                {
                    if (NeedsMods(d)) { d.Remove(); changed = true; modOnly++; continue; }
                    string f = (string)d["_beatmapFilename"];
                    if (string.IsNullOrEmpty(f) || !FixCase(dir, f, out string f2) || IsV4Beatmap(Path.Combine(dir, f2)) ||
                        !MapConvert.FixVersionHeader(Path.Combine(dir, f2)))
                    { d.Remove(); changed = true; continue; }
                    if (f2 != f) { d["_beatmapFilename"] = f2; changed = true; }
                    playable++;
                }
                if (diffs.Count == 0) { set.Remove(); changed = true; }
            }
            if (playable == 0) return modOnly > 0 ? "every difficulty needs mods (Noodle/Mapping Extensions)" : "no difficulty this game version can play";
            if (changed) File.WriteAllText(infoPath, info.ToString());
            return null;
        }

        static readonly string[] ModRequirements = { "Noodle Extensions", "Mapping Extensions", "Vivify" };

        static bool NeedsMods(JToken diff)
        {
            var req = diff["_customData"]?["_requirements"] as JArray;
            if (req == null) return false;
            foreach (var r in req)
                if (r.Type == JTokenType.String && ModRequirements.Contains((string)r)) return true;
            return false;
        }

        static bool FixCase(string dir, string name, out string actual)
        {
            actual = name;
            if (File.Exists(Path.Combine(dir, name))) return true;
            foreach (var f in Directory.GetFiles(dir))
            {
                string n = Path.GetFileName(f);
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) { actual = n; return true; }
            }
            return false;
        }

        static bool IsV4Beatmap(string path)
        {
            var buf = new char[256];
            int n;
            using (var r = new StreamReader(path)) n = r.Read(buf, 0, buf.Length);
            var m = Regex.Match(new string(buf, 0, n), "\"_?version\"\\s*:\\s*\"(\\d+)");
            return m.Success && m.Groups[1].Value == "4";
        }

        // ------------------------------------------------------------------ library
        public class Local
        {
            public string folder, path, title, artist, mapper, cover;
        }

        public static List<Local> ListInstalled()
        {
            var list = new List<Local>();
            if (!Directory.Exists(LevelsDir)) return list;
            foreach (var d in Directory.GetDirectories(LevelsDir))
            {
                var l = new Local { folder = Path.GetFileName(d), path = d, title = Path.GetFileName(d) };
                try
                {
                    var info = JObject.Parse(File.ReadAllText(Path.Combine(d, "Info.dat")).TrimStart('﻿'));
                    l.title = (string)info["_songName"] ?? l.title;
                    l.artist = (string)info["_songAuthorName"];
                    l.mapper = (string)info["_levelAuthorName"];
                    string cover = (string)info["_coverImageFilename"];
                    if (!string.IsNullOrEmpty(cover)) l.cover = Path.Combine(d, cover);
                }
                catch { }
                list.Add(l);
            }
            list.Sort((a, b) => string.Compare(a.title, b.title, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        // BeatSaver keys of installed songs (folder names start with the key)
        public static HashSet<string> InstalledKeys()
        {
            var keys = new HashSet<string>();
            if (!Directory.Exists(LevelsDir)) return keys;
            foreach (var d in Directory.GetDirectories(LevelsDir))
            {
                var m = Regex.Match(Path.GetFileName(d), "^([0-9a-fA-F]+) \\(");
                if (m.Success) keys.Add(m.Groups[1].Value.ToLowerInvariant());
            }
            return keys;
        }

        public static void Delete(string path)
        {
            if (Directory.Exists(path) && Path.GetFullPath(path).StartsWith(Path.GetFullPath(LevelsDir)))
                Directory.Delete(path, true);
            pendingRefresh = true;
        }

        // Main thread: make an open song list re-read the song folder. If the level selection
        // isn't on screen nothing is needed, it reloads every time it opens.
        public static void RefreshGameList()
        {
            pendingRefresh = false;
            var nav = UnityEngine.Object.FindObjectOfType<LevelFilteringNavigationController>();
            if (nav == null) return;
            const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            try
            {
                typeof(LevelFilteringNavigationController).GetField("_customLevelPacks", F)?.SetValue(nav, null);
                typeof(LevelFilteringNavigationController).GetMethod("UpdateCustomSongs", F)?.Invoke(nav, null);
                Debug.Log("[BeatSaberPort] custom song list reloaded");
            }
            catch (Exception e) { Debug.Log("[BeatSaberPort] song list reload: " + e); }
        }
    }
}
