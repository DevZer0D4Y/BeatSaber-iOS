// Frame rate and render resolution for phones. Unity 2019 on iOS runs at 30 fps unless a
// target is set, and native resolution on a 3x display is far more than needed.
// The size comes from the panel itself: Screen.currentResolution only echoes the last
// SetResolution, and the PC game sets its 1280x720 desktop window size at startup, which
// iOS would stretch over a 19.5:9 screen.
using UnityEngine;

namespace BeatSaberPort
{
    public class PortDisplay : MonoBehaviour
    {
        public static int targetFps = 60;
        public static float renderScale = 0.75f;
        float _next, _nextFps;
        int _w, _h, _frames;
        float _worst, _fpsStart;

        void Update()
        {
            _frames++;
            _worst = Mathf.Max(_worst, Time.unscaledDeltaTime);
            if (Time.unscaledTime >= _nextFps)
            {
                float span = Time.unscaledTime - _fpsStart;
                if (_fpsStart > 0f && span > 0f)
                    Debug.Log("[BeatSaberPort] fps " + (_frames / span).ToString("F1") + " worst frame " + (_worst * 1000f).ToString("F0") +
                              " ms at " + Screen.width + "x" + Screen.height);
                _fpsStart = Time.unscaledTime; _nextFps = _fpsStart + 10f; _frames = 0; _worst = 0f;
            }
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;
            if (Application.targetFrameRate != targetFps) Application.targetFrameRate = targetFps;   // the game may reset it
            if (Application.platform != RuntimePlatform.IPhonePlayer) return;
            if (_w == 0)
            {
                int nw = Display.main.systemWidth, nh = Display.main.systemHeight;
                if (nw <= 0 || nh <= 0) { var r = Screen.currentResolution; nw = r.width; nh = r.height; }
                if (nw <= 0 || nh <= 0) return;
                int lw = Mathf.Max(nw, nh), lh = Mathf.Min(nw, nh);   // landscape-only app
                _w = Mathf.RoundToInt(lw * renderScale); _h = Mathf.RoundToInt(lh * renderScale);
                Debug.Log("[BeatSaberPort] render resolution " + _w + "x" + _h + " (panel " + lw + "x" + lh + ")");
            }
            // re-assert: anything that calls Screen.SetResolution would distort the picture
            if (Screen.width != _w || Screen.height != _h) Screen.SetResolution(_w, _h, true);
        }
    }
}
