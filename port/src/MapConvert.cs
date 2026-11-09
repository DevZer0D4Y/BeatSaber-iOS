// Map format fixes at install time, so 1.29.1's own loader can read a song:
//  - v4 maps (Beat Saber 1.34+: Info.dat "version":"4.x", beatmaps split into objects + data
//    tables, lights in a separate lightshow file) are rewritten as a v2 Info.dat with v3 beatmaps.
//    Gameplay objects, bpm changes, basic light events and boosts carry over; v4 light event
//    boxes, NJS events and colour schemes don't.
//  - BeatmapSaveDataHelpers.GetVersion only looks for "version":"x" / "_version":"x" written
//    without spaces in the first 50 characters; files that don't match get re-serialised.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BeatSaberPort
{
    public static class MapConvert
    {
        static readonly Dictionary<string, int> Rank = new Dictionary<string, int>
        { { "Easy", 1 }, { "Normal", 3 }, { "Hard", 5 }, { "Expert", 7 }, { "ExpertPlus", 9 } };

        public static bool IsV4Info(JObject info)
        {
            return info["_version"] == null && info["version"] != null && ((string)info["version"]).StartsWith("4");
        }

        // v4 Info.dat (+ beatmaps) -> v2 Info.dat + v3 beatmaps in the same folder. Returns the new Info.
        public static JObject ConvertV4(string dir, JObject v4)
        {
            var song = v4["song"] ?? new JObject();
            var audio = v4["audio"] ?? new JObject();
            float bpm = F(audio, "bpm", 120f);
            var envs = v4["environmentNames"] as JArray;
            string env = envs != null && envs.Count > 0 ? (string)envs[0] : "DefaultEnvironment";
            JArray bpmEvents = BpmEvents(dir, (string)audio["audioDataFilename"], bpm);

            var mappers = new List<string>();
            var sets = new JArray();
            var byChar = new Dictionary<string, JArray>();
            foreach (var d in (v4["difficultyBeatmaps"] as JArray) ?? new JArray())
            {
                string ch = (string)d["characteristic"] ?? "Standard";
                string diff = (string)d["difficulty"] ?? "Expert";
                string mapFile = (string)d["beatmapDataFilename"];
                if (string.IsNullOrEmpty(mapFile) || !File.Exists(Path.Combine(dir, mapFile))) continue;
                var am = d["beatmapAuthors"]?["mappers"] as JArray;
                if (am != null) foreach (var a in am) if (!mappers.Contains((string)a)) mappers.Add((string)a);

                string outName = diff + ch + ".v3.dat";
                var map = JObject.Parse(File.ReadAllText(Path.Combine(dir, mapFile)).TrimStart('﻿'));
                JObject v3;
                string mv = (string)map["version"] ?? (string)map["_version"] ?? "";
                if (mv.StartsWith("4"))
                {
                    string lsFile = (string)d["lightshowDataFilename"];
                    JObject ls = null;
                    if (!string.IsNullOrEmpty(lsFile) && File.Exists(Path.Combine(dir, lsFile)))
                        ls = JObject.Parse(File.ReadAllText(Path.Combine(dir, lsFile)).TrimStart('﻿'));
                    v3 = BeatmapV4ToV3(map, ls, bpmEvents);
                }
                else v3 = map;   // a v4 Info can point at v2/v3 beatmaps
                File.WriteAllText(Path.Combine(dir, outName), Compact(v3, mv.StartsWith("2") ? "_version" : "version"));

                JArray list;
                if (!byChar.TryGetValue(ch, out list))
                {
                    list = new JArray();
                    byChar[ch] = list;
                    sets.Add(new JObject { ["_beatmapCharacteristicName"] = ch, ["_difficultyBeatmaps"] = list });
                }
                int rank;
                list.Add(new JObject
                {
                    ["_difficulty"] = diff,
                    ["_difficultyRank"] = Rank.TryGetValue(diff, out rank) ? rank : 7,
                    ["_beatmapFilename"] = outName,
                    ["_noteJumpMovementSpeed"] = F(d, "noteJumpMovementSpeed", 0f),
                    ["_noteJumpStartBeatOffset"] = F(d, "noteJumpStartBeatOffset", 0f)
                });
            }

            var info = new JObject
            {
                ["_version"] = "2.1.0",
                ["_songName"] = (string)song["title"] ?? "",
                ["_songSubName"] = (string)song["subTitle"] ?? "",
                ["_songAuthorName"] = (string)song["author"] ?? "",
                ["_levelAuthorName"] = string.Join(", ", mappers),
                ["_beatsPerMinute"] = bpm,
                ["_songTimeOffset"] = 0f,
                ["_shuffle"] = 0f,
                ["_shufflePeriod"] = 0.5f,
                ["_previewStartTime"] = F(audio, "previewStartTime", 12f),
                ["_previewDuration"] = F(audio, "previewDuration", 10f),
                ["_songFilename"] = (string)audio["songFilename"] ?? "song.egg",
                ["_coverImageFilename"] = (string)v4["coverImageFilename"] ?? "",
                ["_environmentName"] = env,
                ["_allDirectionsEnvironmentName"] = "GlassDesertEnvironment",
                ["_difficultyBeatmapSets"] = sets
            };
            File.Move(Path.Combine(dir, "Info.dat"), Path.Combine(dir, "Info.v4.dat"));
            File.WriteAllText(Path.Combine(dir, "Info.dat"), info.ToString(Formatting.Indented));
            return info;
        }

        // AudioData.dat bpmData regions (sample range <-> beat range) -> v3 bpmEvents
        static JArray BpmEvents(string dir, string audioDataFile, float bpm)
        {
            var res = new JArray();
            try
            {
                if (string.IsNullOrEmpty(audioDataFile) || !File.Exists(Path.Combine(dir, audioDataFile))) return res;
                var ad = JObject.Parse(File.ReadAllText(Path.Combine(dir, audioDataFile)).TrimStart('﻿'));
                float freq = F(ad, "songFrequency", 44100f);
                var regions = ad["bpmData"] as JArray;
                if (regions == null || regions.Count < 2) return res;   // one region = the Info bpm
                foreach (var r in regions)
                {
                    float si = F(r, "si", 0f), ei = F(r, "ei", 0f), sb = F(r, "sb", 0f), eb = F(r, "eb", 0f);
                    if (ei <= si || eb <= sb) continue;
                    float m = (eb - sb) / ((ei - si) / freq) * 60f;
                    res.Add(new JObject { ["b"] = sb, ["m"] = m });
                }
            }
            catch { res.Clear(); }
            return res;
        }

        static JObject BeatmapV4ToV3(JObject m, JObject ls, JArray bpmEvents)
        {
            var notesData = Arr(m, "colorNotesData");
            var colorNotes = new JArray();
            foreach (var n in Arr(m, "colorNotes"))
            {
                var nd = At(notesData, I(n, "i"));
                colorNotes.Add(new JObject { ["b"] = F(n, "b", 0f), ["x"] = I(nd, "x"), ["y"] = I(nd, "y"),
                                             ["a"] = I(nd, "a"), ["c"] = I(nd, "c"), ["d"] = I(nd, "d") });
            }
            var bombData = Arr(m, "bombNotesData");
            var bombs = new JArray();
            foreach (var n in Arr(m, "bombNotes"))
            {
                var nd = At(bombData, I(n, "i"));
                bombs.Add(new JObject { ["b"] = F(n, "b", 0f), ["x"] = I(nd, "x"), ["y"] = I(nd, "y") });
            }
            var obstData = Arr(m, "obstaclesData");
            var obstacles = new JArray();
            foreach (var o in Arr(m, "obstacles"))
            {
                var od = At(obstData, I(o, "i"));
                obstacles.Add(new JObject { ["b"] = F(o, "b", 0f), ["x"] = I(od, "x"), ["y"] = I(od, "y"),
                                            ["d"] = F(od, "d", 0f), ["w"] = I(od, "w"), ["h"] = I(od, "h") });
            }
            var arcData = Arr(m, "arcsData");
            var sliders = new JArray();
            foreach (var a in Arr(m, "arcs"))
            {
                var h = At(notesData, I(a, "hi"));
                var t = At(notesData, I(a, "ti"));
                var ad = At(arcData, I(a, "ai"));
                sliders.Add(new JObject
                {
                    ["b"] = F(a, "hb", 0f), ["c"] = I(h, "c"), ["x"] = I(h, "x"), ["y"] = I(h, "y"), ["d"] = I(h, "d"),
                    ["mu"] = F(ad, "m", 0f), ["tb"] = F(a, "tb", 0f), ["tx"] = I(t, "x"), ["ty"] = I(t, "y"),
                    ["tc"] = I(t, "d"), ["tmu"] = F(ad, "tm", 0f), ["m"] = I(ad, "a")
                });
            }
            var chainData = Arr(m, "chainsData");
            var bursts = new JArray();
            foreach (var c in Arr(m, "chains"))
            {
                var h = At(notesData, I(c, "i"));
                var cd = At(chainData, I(c, "ci"));
                bursts.Add(new JObject
                {
                    ["b"] = F(c, "hb", 0f), ["c"] = I(h, "c"), ["x"] = I(h, "x"), ["y"] = I(h, "y"), ["d"] = I(h, "d"),
                    ["tb"] = F(c, "tb", 0f), ["tx"] = I(cd, "tx"), ["ty"] = I(cd, "ty"), ["sc"] = I(cd, "c"), ["s"] = F(cd, "s", 1f)
                });
            }
            var rotData = Arr(m, "spawnRotationsData");
            var rotations = new JArray();
            foreach (var r in Arr(m, "spawnRotations"))
            {
                var rd = At(rotData, I(r, "i"));
                rotations.Add(new JObject { ["b"] = F(r, "b", 0f), ["e"] = I(rd, "t"), ["r"] = F(rd, "r", 0f) });
            }
            var basic = new JArray();
            var boosts = new JArray();
            if (ls != null)
            {
                var bd = Arr(ls, "basicEventsData");
                foreach (var e in Arr(ls, "basicEvents"))
                {
                    var d = At(bd, I(e, "i"));
                    basic.Add(new JObject { ["b"] = F(e, "b", 0f), ["et"] = I(d, "t"), ["i"] = I(d, "i"), ["f"] = F(d, "f", 1f) });
                }
                var cbd = Arr(ls, "colorBoostEventsData");
                foreach (var e in Arr(ls, "colorBoostEvents"))
                {
                    var d = At(cbd, I(e, "i"));
                    boosts.Add(new JObject { ["b"] = F(e, "b", 0f), ["o"] = I(d, "b") != 0 });
                }
            }
            return new JObject
            {
                ["version"] = "3.2.0",
                ["bpmEvents"] = bpmEvents,
                ["rotationEvents"] = rotations,
                ["colorNotes"] = colorNotes,
                ["bombNotes"] = bombs,
                ["obstacles"] = obstacles,
                ["sliders"] = sliders,
                ["burstSliders"] = bursts,
                ["waypoints"] = new JArray(),
                ["basicBeatmapEvents"] = basic,
                ["colorBoostBeatmapEvents"] = boosts,
                ["lightColorEventBoxGroups"] = new JArray(),
                ["lightRotationEventBoxGroups"] = new JArray(),
                ["lightTranslationEventBoxGroups"] = new JArray(),
                ["basicEventTypesWithKeywords"] = new JObject { ["d"] = new JArray() },
                ["useNormalEventsAsCompatibleEvents"] = true
            };
        }

        // ------------------------------------------------------------------ version header
        static readonly Regex Header = new Regex("^\\s*\\{\\s*\"_?version\":\"[0-9]");

        // Make sure the game's version sniffing finds the version; returns false if the file
        // isn't JSON at all.
        public static bool FixVersionHeader(string path)
        {
            string text = File.ReadAllText(path).TrimStart('﻿');
            if (text.Length >= 50 && Header.IsMatch(text.Substring(0, Math.Min(text.Length, 50)))) return true;
            JObject o;
            try { o = JObject.Parse(text); } catch { return false; }
            string v = (string)o["version"] ?? (string)o["_version"];
            string key = v != null && !v.StartsWith("2") ? "version" : "_version";
            if (v == null) v = "2.0.0";   // very old v2 files have no version
            o.Remove("version"); o.Remove("_version");
            var outObj = new JObject { [key] = v };
            foreach (var p in o.Properties()) outObj.Add(p.Name, p.Value);
            string s = outObj.ToString(Formatting.None);
            if (s.Length < 50) s = s.Substring(0, s.Length - 1) + ",\"_pad\":\"" + new string(' ', 50) + "\"}";
            File.WriteAllText(path, s);
            return true;
        }

        static string Compact(JObject o, string versionKey)
        {
            string v = (string)o["version"] ?? (string)o["_version"] ?? "3.2.0";
            o.Remove("version"); o.Remove("_version");
            var outObj = new JObject { [versionKey] = v };
            foreach (var p in o.Properties()) outObj.Add(p.Name, p.Value);
            return outObj.ToString(Formatting.None);
        }

        // ------------------------------------------------------------------ helpers (v4 omits defaults)
        static JArray Arr(JObject o, string k) { return o[k] as JArray ?? new JArray(); }
        static JToken At(JArray a, int i) { return i >= 0 && i < a.Count ? a[i] : new JObject(); }
        static int I(JToken t, string k)
        {
            var v = t?[k];
            if (v == null || v.Type == JTokenType.Null) return 0;
            if (v.Type == JTokenType.Boolean) return (bool)v ? 1 : 0;
            return (int)Math.Round((double)v);
        }
        static float F(JToken t, string k, float def)
        {
            var v = t?[k];
            return v == null || v.Type == JTokenType.Null ? def : (float)v;
        }
    }
}
