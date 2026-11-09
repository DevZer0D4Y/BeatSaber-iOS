// Entry point of the iOS port's own code. Called once from PCAppInit (patched in by bstool).
using UnityEngine;

namespace BeatSaberPort
{
    public static class PortBootstrap
    {
        static bool _done;
        public static GameObject Root { get; private set; }

        public static void Init()
        {
            if (_done) return;
            _done = true;
            Root = new GameObject("BeatSaberPort");
            UnityEngine.Object.DontDestroyOnLoad(Root);
            Debug.Log("[BeatSaberPort] bootstrap, platform " + Application.platform);
            TouchInput.Create(Root);
            MenuCamera.Create(Root);
            Root.AddComponent<PortDisplay>();
            RenderFixes.Init();
            Songs.Init();
            SongBrowser.Create(Root);
        }
    }
}
