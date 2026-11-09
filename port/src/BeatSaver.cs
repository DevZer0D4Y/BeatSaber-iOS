// BeatSaver (beatsaver.com) API: the community map site. Only the public read-only endpoints
// are used: search/browse and the map zip / cover / preview CDN links they return.
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace BeatSaberPort
{
    [Serializable] public class BsPage { public BsMap[] docs; }
    [Serializable] public class BsUser { public string name; }
    [Serializable] public class BsStats { public int upvotes, downvotes; public float score; }
    [Serializable] public class BsMeta
    {
        public float bpm;
        public int duration;
        public string songName, songSubName, songAuthorName, levelAuthorName;
    }
    [Serializable] public class BsDiff
    {
        public string characteristic, difficulty;
        public float nps, njs;
        public int notes;
        public bool chroma, me, ne, cinema;
    }
    [Serializable] public class BsVersion
    {
        public string hash, key, state, createdAt, downloadURL, coverURL, previewURL;
        public BsDiff[] diffs;
    }
    [Serializable] public class BsMap
    {
        public string id, name;
        public BsUser uploader, curator;
        public BsMeta metadata;
        public BsStats stats;
        public bool ranked, blRanked, automapper;
        public BsVersion[] versions;

        [NonSerialized] BsVersion _latest;
        public BsVersion Latest
        {
            get
            {
                if (_latest == null && versions != null)
                    foreach (var v in versions)
                        if (_latest == null || string.CompareOrdinal(v.createdAt, _latest.createdAt) > 0) _latest = v;
                return _latest;
            }
        }

        // Noodle / Mapping Extensions difficulties don't play right without those mods
        // (the installer drops them); Chroma is only lighting and plays fine.
        public int ModDiffs
        {
            get
            {
                var v = Latest;
                int n = 0;
                if (v != null && v.diffs != null) foreach (var d in v.diffs) if (d.ne || d.me) n++;
                return n;
            }
        }
        public bool AllNeedMods { get { var v = Latest; return v != null && v.diffs != null && v.diffs.Length > 0 && ModDiffs == v.diffs.Length; } }

        // difficulty letters for the list, e.g. "E N H X X+"
        public string DiffSummary
        {
            get
            {
                var v = Latest;
                if (v == null || v.diffs == null) return "";
                bool e = false, n = false, h = false, x = false, xp = false, other = false;
                foreach (var d in v.diffs)
                {
                    if (d.characteristic != "Standard") { other = true; continue; }
                    switch (d.difficulty)
                    {
                        case "Easy": e = true; break;
                        case "Normal": n = true; break;
                        case "Hard": h = true; break;
                        case "Expert": x = true; break;
                        case "ExpertPlus": xp = true; break;
                    }
                }
                string s = (e ? "E " : "") + (n ? "N " : "") + (h ? "H " : "") + (x ? "X " : "") + (xp ? "X+ " : "");
                return (s + (other ? "+modes" : "")).Trim();
            }
        }
    }

    // Built from Newtonsoft's JObject by hand: Unity's JsonUtility leaves these empty because
    // BeatSaberPort.dll isn't one of the player's registered script assemblies.
    public static class BsJson
    {
        static string S(JToken t, string k) { var v = t?[k]; return v == null || v.Type == JTokenType.Null ? null : (string)v; }
        static float F(JToken t, string k) { var v = t?[k]; return v == null || v.Type == JTokenType.Null ? 0f : (float)v; }
        static int I(JToken t, string k) { var v = t?[k]; return v == null || v.Type == JTokenType.Null ? 0 : (int)(double)v; }
        static bool B(JToken t, string k) { var v = t?[k]; return v != null && v.Type == JTokenType.Boolean && (bool)v; }

        public static BsMap Map(JToken j)
        {
            if (j == null || j.Type != JTokenType.Object) return null;
            var m = new BsMap
            {
                id = S(j, "id"), name = S(j, "name"),
                uploader = new BsUser { name = S(j["uploader"], "name") },
                curator = new BsUser { name = S(j["curator"], "name") },
                metadata = new BsMeta(), stats = new BsStats(),
                ranked = B(j, "ranked"), blRanked = B(j, "blRanked"), automapper = B(j, "automapper")
            };
            var md = j["metadata"];
            m.metadata.bpm = F(md, "bpm"); m.metadata.duration = I(md, "duration");
            m.metadata.songName = S(md, "songName") ?? m.name ?? "";
            m.metadata.songSubName = S(md, "songSubName") ?? "";
            m.metadata.songAuthorName = S(md, "songAuthorName") ?? "";
            m.metadata.levelAuthorName = S(md, "levelAuthorName") ?? "";
            var st = j["stats"];
            m.stats.upvotes = I(st, "upvotes"); m.stats.downvotes = I(st, "downvotes"); m.stats.score = F(st, "score");
            var vs = j["versions"] as JArray;
            var list = new List<BsVersion>();
            if (vs != null)
                foreach (var v in vs)
                {
                    var bv = new BsVersion
                    {
                        hash = S(v, "hash"), key = S(v, "key"), state = S(v, "state"), createdAt = S(v, "createdAt") ?? "",
                        downloadURL = S(v, "downloadURL"), coverURL = S(v, "coverURL"), previewURL = S(v, "previewURL")
                    };
                    var ds = v["diffs"] as JArray;
                    var dl = new List<BsDiff>();
                    if (ds != null)
                        foreach (var d in ds)
                            dl.Add(new BsDiff
                            {
                                characteristic = S(d, "characteristic"), difficulty = S(d, "difficulty"),
                                nps = F(d, "nps"), njs = F(d, "njs"), notes = I(d, "notes"),
                                chroma = B(d, "chroma"), me = B(d, "me"), ne = B(d, "ne"), cinema = B(d, "cinema")
                            });
                    bv.diffs = dl.ToArray();
                    if (!string.IsNullOrEmpty(bv.hash) && !string.IsNullOrEmpty(bv.downloadURL)) list.Add(bv);
                }
            m.versions = list.ToArray();
            return string.IsNullOrEmpty(m.id) ? null : m;
        }

        public static List<BsMap> Page(string json)
        {
            var res = new List<BsMap>();
            var docs = JObject.Parse(json)["docs"] as JArray;
            if (docs != null) foreach (var d in docs) { var m = Map(d); if (m != null) res.Add(m); }
            return res;
        }
    }

    public enum BsTab { Search, Top, Latest, Curated, Ranked, Mine }

    public static class BeatSaver
    {
        public const string Api = "https://api.beatsaver.com";
        public const string UserAgent = "BeatSaberPort-iOS/1.0";

        public static string ListUrl(BsTab tab, string query, int page, bool hideModMaps)
        {
            string u = Api + "/search/text/" + page + "?";
            switch (tab)
            {
                case BsTab.Search: u += "q=" + Uri.EscapeDataString(query ?? "") + "&sortOrder=Relevance"; break;
                case BsTab.Top: u += "sortOrder=Rating"; break;
                case BsTab.Latest: u += "sortOrder=Latest"; break;
                case BsTab.Curated: u += "sortOrder=Curated&curated=true"; break;
                case BsTab.Ranked: u += "sortOrder=Latest&leaderboard=Ranked"; break;
            }
            if (hideModMaps) u += "&vivify=false&me=false";   // the server's noodle=false doesn't filter; done client-side
            return u;
        }

        // "!bsr 1f33", "bsr 1f33" or a bare key with a digit in it -> "1f33"
        public static string KeyFromQuery(string q)
        {
            if (string.IsNullOrEmpty(q)) return null;
            var m = Regex.Match(q.Trim(), "^!?bsr\\s+([0-9a-fA-F]{1,6})$");
            if (m.Success) return m.Groups[1].Value.ToLowerInvariant();
            m = Regex.Match(q.Trim(), "^([0-9a-fA-F]{2,6})$");
            if (m.Success && Regex.IsMatch(q, "[0-9]")) return m.Groups[1].Value.ToLowerInvariant();
            return null;
        }

        public static string MapUrl(string key) { return Api + "/maps/id/" + key; }
    }
}
