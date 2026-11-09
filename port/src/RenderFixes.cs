// Rendering fixes for the flat-screen port.
//
// Dither noise: the game adds a blue-noise texture (BlueNoiseDithering, +-0.5/255 in linear
// space) to most surfaces. On the port the menu floor shows it as fine vertical streaks right
// from the boot (health warning) screen, because near black that tiny linear step is a big
// sRGB step and the noise comes out horizontally smeared. A neutral texture (0.5 = no offset)
// removes the streaks; nothing else changes.
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BeatSaberPort
{
    public static class RenderFixes
    {
        static Texture2D _neutralNoise;
        static System.Reflection.FieldInfo _noiseField;

        public static void Init()
        {
            SceneManager.sceneLoaded += (s, m) => NeutralDither();
            NeutralDither();
        }

        static void NeutralDither()
        {
            if (_neutralNoise == null)
            {
                _neutralNoise = new Texture2D(4, 4, TextureFormat.R8, false) { name = "NeutralNoise", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat };
                var px = new Color32[16];
                for (int i = 0; i < px.Length; i++) px[i] = new Color32(128, 128, 128, 255);
                _neutralNoise.SetPixels32(px);
                _neutralNoise.Apply(false, true);
                Object.DontDestroyOnLoad(_neutralNoise);
                _noiseField = typeof(BlueNoiseDithering).GetField("_noiseTexture",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            }
            if (_noiseField == null) return;
            foreach (var b in Resources.FindObjectsOfTypeAll<BlueNoiseDithering>())
                if (!ReferenceEquals(_noiseField.GetValue(b), _neutralNoise)) _noiseField.SetValue(b, _neutralNoise);
        }
    }
}
