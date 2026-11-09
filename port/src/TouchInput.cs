// Touch controls for the non-VR port.
//
// Gameplay: a finger on the left half of the screen drives the left saber, the right half
// the right saber. Each saber is held at a fixed hand point just below the view and only
// turns about it, like a swung saber: the blade points at the spot under the finger (where
// the finger's camera ray meets a sphere around the hand), so swiping swings the blade
// through the notes. A side with no finger lets its saber hang down out of the way.
// Menus: the touched side's controller sits at the camera and points along the camera ray
// through the finger, so the game's own laser pointer lands exactly on what you touch; touching
// holds the trigger, dragging vertically scrolls. Tapping the top-centre strip is the menu
// (pause) button. A mouse or trackpad pointer stands in for one finger.
using UnityEngine;
using UnityEngine.XR;

namespace BeatSaberPort
{
    public class TouchInput : MonoBehaviour
    {
        public static TouchInput Instance { get; private set; }

        // tuning
        const float HandDrop = 0.38f;         // the fixed hand (saber hilt) point, from the camera
        const float HandSide = 0.25f;
        const float HandForward = 0.45f;
        const float AimRadius = 0.85f;        // the blade passes under the finger this far from the hand (blade is 1 m)
        const float PauseStripHeight = 0.10f; // top fraction of the screen
        const float PauseStripWidth = 0.30f;  // centred width fraction
        const float ScrollPixelsPerUnit = 60f;
        const float TapMaxMove = 18f;         // pixels; more than this turns a menu touch into a scroll

        class Hand
        {
            public int fingerId = int.MinValue;
            public Vector2 pos, startPos, lastPos;
            public bool down, scrolling;
            public float scroll;
            public Vector3 worldPos;
            public Quaternion worldRot = Quaternion.identity;
        }

        readonly Hand _left = new Hand(), _right = new Hand();
        bool _menuButtonDown;
        bool _gameplay;
        float _nextModeCheck;
        Camera _cam;
        Transform _leftSpace, _rightSpace;   // parents of the active VRControllers
        bool _gActive, _gestureLatched;      // two-finger menu gesture (MenuCamera zoom/pan)
        float _gPrevDist;
        Vector2 _gPrevMid;

        // a saber scene (gameplay, also while paused) - MenuCamera leaves the camera alone there
        public static bool InGameplay { get { return Instance != null && Instance._saberScene; } }
        bool _saberScene;
        PauseMenuManager _pauseMenu;
        static System.Reflection.FieldInfo _pmContinue;

        bool PauseMenuVisible()
        {
            if (_pauseMenu == null) return false;
            if (_pmContinue == null)
                _pmContinue = typeof(PauseMenuManager).GetField("_continueButton",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var b = _pmContinue != null ? _pmContinue.GetValue(_pauseMenu) as UnityEngine.UI.Button : null;
            return b != null && b.gameObject.activeInHierarchy;
        }

        public static void Create(GameObject root)
        {
            if (Instance == null) Instance = root.AddComponent<TouchInput>();
        }

        void Update()
        {
            if (Time.unscaledTime >= _nextModeCheck)
            {
                _nextModeCheck = Time.unscaledTime + 0.5f;
                _saberScene = FindObjectOfType<Saber>() != null;
                _pauseMenu = _saberScene ? FindObjectOfType<PauseMenuManager>() : null;
                if (_cam == null || !_cam.isActiveAndEnabled) _cam = Camera.main;
                _leftSpace = _rightSpace = null;
                foreach (var c in FindObjectsOfType<VRController>())
                {
                    if (!c.isActiveAndEnabled) continue;
                    if (c.node == XRNode.LeftHand && _leftSpace == null) _leftSpace = c.transform.parent;
                    if (c.node == XRNode.RightHand && _rightSpace == null) _rightSpace = c.transform.parent;
                }
            }
            // sabers cut in gameplay; the pause menu uses pointers like the main menu
            _gameplay = _saberScene && !PauseMenuVisible();
            ReadPointers();
            if (_cam != null)
            {
                Place(_left, XRNode.LeftHand);
                Place(_right, XRNode.RightHand);
            }
        }

        void LateUpdate() { _menuButtonDown = false; }

        // ------------------------------------------------------------------ input
        void ReadPointers()
        {
            bool seenL = false, seenR = false;
            int n = Input.touchCount;
            if (SongBrowser.IsOpen)
            {
                // the song browser owns the screen; don't let its touches leak into the menus after it closes
                _gActive = false; _gestureLatched = n > 0;
                Release(_left); Release(_right);
                return;
            }
            if (!_gameplay && n >= 2)
            {
                // menus: two fingers = pinch zoom / drag pan, never a click
                Vector2 a = Input.GetTouch(0).position, b = Input.GetTouch(1).position;
                float dist = Vector2.Distance(a, b);
                Vector2 mid = (a + b) * 0.5f;
                if (_gActive && _gPrevDist > 1f)
                {
                    MenuCamera.userZoom = Mathf.Clamp(MenuCamera.userZoom * dist / _gPrevDist, 0.5f, 4f);
                    float degPerPx = (_cam != null ? _cam.fieldOfView : 60f) / Mathf.Max(1, Screen.height);
                    MenuCamera.userYaw -= (mid.x - _gPrevMid.x) * degPerPx;
                    MenuCamera.userPitch -= (mid.y - _gPrevMid.y) * degPerPx;
                }
                _gActive = true; _gestureLatched = true; _gPrevDist = dist; _gPrevMid = mid;
                Release(_left); Release(_right);
                return;
            }
            _gActive = false;
            if (_gestureLatched)
            {
                if (n == 0) _gestureLatched = false;   // wait until every finger is up
                Release(_left); Release(_right);
                return;
            }
            for (int i = 0; i < n; i++)
            {
                var t = Input.GetTouch(i);
                Feed(t.fingerId, t.position, t.phase == TouchPhase.Began,
                     t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled, ref seenL, ref seenR);
            }
            if (n == 0 && (Input.GetMouseButton(0) || Input.GetMouseButtonUp(0)))
                Feed(-1, Input.mousePosition, Input.GetMouseButtonDown(0), Input.GetMouseButtonUp(0), ref seenL, ref seenR);
            if (!seenL) Release(_left);
            if (!seenR) Release(_right);
        }

        void Feed(int id, Vector2 p, bool began, bool ended, ref bool seenL, ref bool seenR)
        {
            if (began && SongBrowser.Blocks(p)) return;           // the "SONGS +" button
            if (began && IsPauseStrip(p)) { _menuButtonDown = true; return; }
            Hand h = id == _left.fingerId ? _left : id == _right.fingerId ? _right : null;
            if (h == null)
            {
                if (!began && id >= 0) return;                // a finger we never claimed
                h = p.x < Screen.width * 0.5f ? _left : _right;
                if (h.down && h.fingerId != id) return;       // side already owned by another finger
                h.fingerId = id; h.down = true; h.scrolling = false;
                h.startPos = h.lastPos = h.pos = p; h.scroll = 0f;
            }
            if (h == _left) seenL = true; else seenR = true;
            h.lastPos = h.pos; h.pos = p;
            if (!_gameplay)
            {
                float dy = p.y - h.startPos.y;
                if (!h.scrolling && Mathf.Abs(dy) > TapMaxMove) h.scrolling = true;
                h.scroll = h.scrolling ? Mathf.Clamp((p.y - h.lastPos.y) / ScrollPixelsPerUnit * 6f, -1f, 1f) : 0f;
            }
            if (ended) Release(h);
        }

        static void Release(Hand h)
        {
            h.down = false; h.scrolling = false; h.scroll = 0f; h.fingerId = int.MinValue;
        }

        static bool IsPauseStrip(Vector2 p)
        {
            return p.y > Screen.height * (1f - PauseStripHeight) &&
                   Mathf.Abs(p.x - Screen.width * 0.5f) < Screen.width * PauseStripWidth * 0.5f;
        }

        // ------------------------------------------------------------------ poses
        void Place(Hand h, XRNode node)
        {
            var ct = _cam.transform;
            float side = node == XRNode.LeftHand ? -1f : 1f;
            if (!_gameplay)
            {
                // menu: ray from the camera through the finger (or straight down when idle)
                var ray = h.down ? _cam.ScreenPointToRay(h.pos) : new Ray(ct.position, -ct.up);
                h.worldPos = ct.position + ct.right * (0.02f * side);
                h.worldRot = Quaternion.LookRotation(ray.direction, ct.up);
                return;
            }
            Vector3 hand = ct.position - ct.up * HandDrop + ct.right * (HandSide * side) + ct.forward * HandForward;
            h.worldPos = hand;
            if (!h.down)
            {
                // resting: hanging down and out to the side, below the view
                h.worldRot = Quaternion.LookRotation(-ct.up * 0.7f + ct.forward * 0.5f + ct.right * (0.5f * side), ct.forward);
                return;
            }
            // where the finger's ray leaves the sphere of radius AimRadius around the hand (the
            // camera is inside it, so there is always exactly one such point ahead)
            var r = _cam.ScreenPointToRay(h.pos);
            Vector3 m = r.origin - hand;
            float b = Vector3.Dot(m, r.direction);
            float t = -b + Mathf.Sqrt(Mathf.Max(0f, b * b - (m.sqrMagnitude - AimRadius * AimRadius)));
            Vector3 dir = (r.origin + r.direction * t - hand).normalized;
            h.worldRot = Quaternion.LookRotation(dir, Mathf.Abs(Vector3.Dot(dir, ct.up)) < 0.98f ? ct.up : -ct.forward);
        }

        // Called from the patched DevicelessVRHelper.GetNodePose (tracking-space pose).
        public static bool GetNodePose(XRNode node, int idx, out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero; rot = Quaternion.identity;
            var me = Instance;
            if (me == null || me._cam == null) return true;
            if (node != XRNode.LeftHand && node != XRNode.RightHand) return true;
            var h = node == XRNode.LeftHand ? me._left : me._right;
            var space = node == XRNode.LeftHand ? me._leftSpace : me._rightSpace;
            if (space == null) space = me._cam.transform.parent;
            if (space != null)
            {
                pos = space.InverseTransformPoint(h.worldPos);
                rot = Quaternion.Inverse(space.rotation) * h.worldRot;
            }
            else { pos = h.worldPos; rot = h.worldRot; }
            return true;
        }

        // Called from the patched VRControllersInputManager.
        public static float TriggerValue(XRNode node)
        {
            var me = Instance;
            if (me == null || me._gameplay) return 0f;
            var h = node == XRNode.LeftHand ? me._left : me._right;
            return h.down && !h.scrolling ? 1f : 0f;
        }

        public static float VerticalAxisValue(XRNode node)
        {
            var me = Instance;
            if (me == null || me._gameplay) return 0f;
            var h = node == XRNode.LeftHand ? me._left : me._right;
            return h.scroll;
        }

        public static bool MenuButtonDown()
        {
            var me = Instance;
            return me != null && me._menuButtonDown;
        }

        public static bool MenuButton() { return MenuButtonDown(); }
    }
}
