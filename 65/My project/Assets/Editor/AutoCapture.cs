using UnityEditor;
using UnityEngine;

namespace Sdo.EditorTools
{
    /// <summary>
    /// Auto-saves an 800×600 render of the gameplay to H:/65_remake/play-capture.png a few seconds
    /// after you press Play in the Editor. Lets the layout be reviewed without manual screenshots
    /// (and works while the Editor is open, unlike the headless batch capture which needs the
    /// project closed). Menu: Tools/Capture Gameplay Now also works any time in Play mode.
    /// </summary>
    [InitializeOnLoad]
    public static class AutoCapture
    {
        private const string OutPath = "H:/65_remake/play-capture.png";
        private const int W = 800, H = 600;
        private static double _captureAt = -1;

        static AutoCapture()
        {
            EditorApplication.playModeStateChanged += s =>
            {
                if (s == PlayModeStateChange.EnteredPlayMode)
                    _captureAt = EditorApplication.timeSinceStartup + 6.0; // past 1.5s lead-in + notes falling
            };
            EditorApplication.update += Tick;
        }

        private static void Tick()
        {
            if (_captureAt > 0 && EditorApplication.timeSinceStartup >= _captureAt)
            {
                _captureAt = -1;
                Capture();
            }
        }

        [MenuItem("Tools/Capture Gameplay Now")]
        private static void Capture()
        {
            var cam = Camera.main;
            if (cam == null) { Debug.LogWarning("[AutoCapture] no Camera.main"); return; }
            var rt = new RenderTexture(W, H, 24);
            var prevT = cam.targetTexture; var prevA = RenderTexture.active;
            cam.targetTexture = rt; cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0); tex.Apply();
            System.IO.File.WriteAllBytes(OutPath, tex.EncodeToPNG());
            cam.targetTexture = prevT; RenderTexture.active = prevA;
            Object.DestroyImmediate(tex); rt.Release(); Object.DestroyImmediate(rt);
            Debug.Log("[AutoCapture] saved " + OutPath);
        }
    }
}
