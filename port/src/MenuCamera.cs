// Camera for a flat screen. The scenes put the player camera on the floor (a headset sets its
// height in VR), so it is lifted to a standing eye height everywhere. In menus the camera has
// one fixed view: the angle and zoom that frame the three menu panels (left, main, right) and
// the title bar, worked out once from the panel containers, so it does not move while you
// browse. Pinch / two-finger drag (TouchInput) still zoom and pan by hand. In gameplay it looks
// slightly down the note lanes.
using UnityEngine;

namespace BeatSaberPort
{
    public class MenuCamera : MonoBehaviour
    {
        public static float userZoom = 1f, userYaw, userPitch;

        public const float EyeHeight = 1.7f;         // metres above the play-area floor
        const float MinFov = 40f, MaxFov = 90f;      // vertical, degrees
        const float MarginDeg = 1.5f;                // around the panels, each side
        const float GameplayFov = 60f, GameplayPitch = 12f;   // degrees; pitch looks down

        Camera _cam;
        int _camId;
        Quaternion _origRot;
        bool _playerCam;
        float _nextScan;
        bool _haveTarget;
        float _tFov, _tYaw, _tPitch;                  // the fixed menu view
        int _ssId;
        float _aspect;


        public static void Create(GameObject root)
        {
            if (root.GetComponent<MenuCamera>() == null) root.AddComponent<MenuCamera>();
        }

        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam == null) return;
            if (cam.GetInstanceID() != _camId)
            {
                _cam = cam; _camId = cam.GetInstanceID();
                _origRot = cam.transform.localRotation;
                // the VR rig cameras (not e.g. the beatmap editor's own camera)
                _playerCam = cam.name == "MainCamera" || cam.name == "MenuMainCamera";
                _haveTarget = false;
            }
            if (_playerCam) _cam.transform.localPosition = new Vector3(0f, EyeHeight, 0f);
            if (TouchInput.InGameplay)
            {
                if (_playerCam)
                {
                    _cam.fieldOfView = GameplayFov;
                    _cam.transform.localRotation = _origRot * Quaternion.Euler(GameplayPitch, 0f, 0f);
                }
                return;
            }
            if (!_haveTarget || Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + 1f;
                Frame();
            }
            if (!_haveTarget) return;
            _cam.fieldOfView = Mathf.Clamp(_tFov / Mathf.Max(0.3f, userZoom), 20f, 100f);
            _cam.transform.localRotation = _origRot * Quaternion.Euler(-(_tPitch + userPitch), _tYaw + userYaw, 0f);
        }

        // The fixed menu view. Uses the screen containers, which never move or resize, rather
        // than what they currently show, so every menu gets the same view.
        void Frame()
        {
            var ss = FindObjectOfType<HMUI.ScreenSystem>();
            if (ss == null) { _haveTarget = false; return; }
            float aspect = Mathf.Max(0.5f, _cam.aspect);
            if (_haveTarget && ss.GetInstanceID() == _ssId && Mathf.Abs(aspect - _aspect) < 0.001f) return;
            var parent = _cam.transform.parent;
            Quaternion baseRot = (parent != null ? parent.rotation : Quaternion.identity) * _origRot;
            Quaternion inv = Quaternion.Inverse(baseRot);
            Vector3 eye = _cam.transform.position;
            float minY = 999f, maxY = -999f, minP = 999f, maxP = -999f;
            int n = 0;
            var corners = new Vector3[4];
            foreach (var s in new[] { ss.mainScreen, ss.leftScreen, ss.rightScreen, ss.topScreen })
            {
                var rt = s != null ? s.transform as RectTransform : null;
                if (rt == null) continue;
                rt.GetWorldCorners(corners);
                Accumulate(corners, inv, eye, ref minY, ref maxY, ref minP, ref maxP, ref n);
            }
            if (n == 0) { _haveTarget = false; return; }
            float halfW = (maxY - minY) * 0.5f + MarginDeg, halfH = (maxP - minP) * 0.5f + MarginDeg;
            float vFromW = 2f * Mathf.Atan(Mathf.Tan(Mathf.Min(halfW, 85f) * Mathf.Deg2Rad) / aspect) * Mathf.Rad2Deg;
            _tFov = Mathf.Clamp(Mathf.Max(2f * halfH, vFromW), MinFov, MaxFov);
            _tYaw = (minY + maxY) * 0.5f;
            _tPitch = (minP + maxP) * 0.5f;
            _haveTarget = true; _ssId = ss.GetInstanceID(); _aspect = aspect;
            Debug.Log("[BeatSaberPort] menu view: panels yaw " + minY.ToString("F0") + ".." + maxY.ToString("F0") +
                      " pitch " + minP.ToString("F0") + ".." + maxP.ToString("F0") + " -> fov " + _tFov.ToString("F1") +
                      " pitch " + _tPitch.ToString("F1") + " (aspect " + aspect.ToString("F2") + ")");
        }

        static void Accumulate(Vector3[] corners, Quaternion inv, Vector3 eye,
                               ref float minY, ref float maxY, ref float minP, ref float maxP, ref int n)
        {
            foreach (var c in corners)
            {
                var d = inv * (c - eye);
                if (d.z <= 0.05f) continue;
                float yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                float pitch = Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg;
                minY = Mathf.Min(minY, yaw); maxY = Mathf.Max(maxY, yaw);
                minP = Mathf.Min(minP, pitch); maxP = Mathf.Max(maxP, pitch);
                n++;
            }
        }
    }
}
