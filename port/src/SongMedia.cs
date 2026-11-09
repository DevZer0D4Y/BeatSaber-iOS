// Loading a custom song's audio and cover from its folder.
//
// The game turns a file path into a URL with FileHelpers.GetEscapedURLForFilePath, which is
// "file:///" + UnityWebRequest.EscapeURL(path). That suits "C:\..." on Windows, but for a
// Unix path it gives "file:///%2Fvar%2Fmobile%2F...", with every '/' escaped and spaces turned
// into '+'. bstool sends that method to FileUrl below, and MediaAsyncLoader.
// LoadAudioClipFromFilePathAsync (which turns any failure into a silent null, and so into
// "error loading level") to LoadAudioClip, which logs what went wrong and tries other ways.
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace BeatSaberPort
{
    public static class SongMedia
    {
        // "/var/mobile/A B/(x).ogg" -> "file:///var/mobile/A%20B/%28x%29.ogg"
        public static string FileUrl(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            path = path.Replace('\\', '/');
            var sb = new StringBuilder("file://", path.Length + 32);
            if (!path.StartsWith("/")) sb.Append('/');
            var parts = path.Split('/');
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) sb.Append('/');
                // keep a Windows drive ("C:") as it is
                if (i == 0 && parts[i].Length == 2 && parts[i][1] == ':') sb.Append(parts[i]);
                else sb.Append(Uri.EscapeDataString(parts[i]));
            }
            return sb.ToString();
        }

        // From the file's first bytes; the extension says little (BeatSaver songs are often .egg).
        public static AudioType SniffAudioType(string path)
        {
            try
            {
                var b = new byte[12];
                int n;
                using (var f = File.OpenRead(path)) n = f.Read(b, 0, b.Length);
                if (n >= 4 && b[0] == 'O' && b[1] == 'g' && b[2] == 'g' && b[3] == 'S') return AudioType.OGGVORBIS;
                if (n >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'A' && b[10] == 'V' && b[11] == 'E') return AudioType.WAV;
                if (n >= 3 && b[0] == 'I' && b[1] == 'D' && b[2] == '3') return AudioType.MPEG;
                if (n >= 2 && b[0] == 0xFF && (b[1] & 0xE0) == 0xE0) return AudioType.MPEG;
            }
            catch (Exception) { }
            return string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase) ? AudioType.WAV : AudioType.OGGVORBIS;
        }

        public static async Task<AudioClip> LoadAudioClip(string filePath)
        {
            string name = Path.GetFileName(Path.GetDirectoryName(filePath)) + "/" + Path.GetFileName(filePath);
            if (!File.Exists(filePath))
            {
                Debug.Log("[BeatSaberPort] song audio " + name + ": file not found (" + filePath + ")");
                return null;
            }
            var type = SniffAudioType(filePath);
            string err;
            var clip = await TryLoad(FileUrl(filePath), type, name, "url");
            if (clip != null) return clip;

            // Last try: a copy under a plain name, in case the path itself is the problem.
            try
            {
                string tmp = Path.Combine(Application.temporaryCachePath,
                    "song" + ((uint)filePath.GetHashCode()).ToString("x8") + Path.GetExtension(filePath).ToLowerInvariant());
                await Task.Run(() => File.Copy(filePath, tmp, true));
                clip = await TryLoad("file://" + tmp, type, name, "copy");
                if (clip != null) return clip;
                err = "every way failed";
            }
            catch (Exception e) { err = "copy: " + e.Message; }
            Debug.Log("[BeatSaberPort] song audio " + name + ": " + err);
            return null;
        }

        static async Task<AudioClip> TryLoad(string url, AudioType type, string name, string how)
        {
            float t0 = Time.realtimeSinceStartup;
            using (var www = UnityWebRequestMultimedia.GetAudioClip(url, type))
            {
                ((DownloadHandlerAudioClip)www.downloadHandler).streamAudio = true;
                var op = www.SendWebRequest();
                while (!op.isDone) await Task.Delay(50);
                if (www.isNetworkError || www.isHttpError)
                {
                    Debug.Log("[BeatSaberPort] song audio " + name + " (" + how + " " + type + "): " + www.error + " [" + url + "]");
                    return null;
                }
                AudioClip clip = null;
                string why = null;
                try { clip = DownloadHandlerAudioClip.GetContent(www); }
                catch (Exception e) { why = e.Message; }
                if (clip == null || clip.loadState == AudioDataLoadState.Failed)
                {
                    Debug.Log("[BeatSaberPort] song audio " + name + " (" + how + " " + type + "): can't decode, " +
                              (why ?? (clip == null ? "no clip" : "load state Failed")) + ", " + www.downloadedBytes + " bytes");
                    return null;
                }
                Debug.Log("[BeatSaberPort] song audio " + name + " (" + how + " " + type + "): ok " + clip.length.ToString("0.0") + " s, " +
                          clip.frequency + " Hz, " + clip.channels + " ch, " + clip.loadState + ", " +
                          ((Time.realtimeSinceStartup - t0) * 1000f).ToString("0") + " ms");
                return clip;
            }
        }
    }
}
