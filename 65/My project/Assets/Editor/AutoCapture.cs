using UnityEditor;
using UnityEngine;
using System.Collections.Generic;

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
            if (!EditorApplication.isPlaying) { _captureAt = -1; return; }
            if (_captureAt > 0 && EditorApplication.timeSinceStartup >= _captureAt)
            {
                Capture();
                // Keep refreshing so play-capture.png is always the latest on-screen frame: the 6s lead-in usually
                // lands on the song-select menu while you navigate, so re-capture every few seconds — whatever you're
                // looking at when you check the file (e.g. mid-gameplay in stage 14) is what gets saved.
                _captureAt = EditorApplication.timeSinceStartup + 3.0;
            }
        }

        [MenuItem("Tools/Capture Gameplay Now")]
        private static void Capture()
        {
            // Prefer rendering the 3D stage camera ("SceneCam") directly to a texture — that's the camera that draws
            // the dancer / scene / mapobj props (corals, crowd…). It's reliable from an editor-update callback, unlike
            // ScreenCapture of the composited game view (which grabbed blank/white frames mid-gameplay). Fall back to
            // a full-screen grab (e.g. on menus, where there is no SceneCam).
            Camera cam = null;
#pragma warning disable 0618
            foreach (var c in Object.FindObjectsOfType<Camera>()) if (c.name == "SceneCam") { cam = c; break; }
#pragma warning restore 0618
            if (cam != null)
            {
                const int w = 960, h = 720;   // 4:3 stage view
                var rt = new RenderTexture(w, h, 24);
                var prevT = cam.targetTexture; var prevA = RenderTexture.active;
                // In Pillarbox mode (視窗化) the on-screen camera's rect is a centred sub-rect and its aspect is
                // reset to the window's letterbox shape. Rendering that straight into a fixed 4:3 RT would crop the
                // left/right of the frame. Force the FULL 4:3 frame into the RT for the capture, then restore.
                var prevRect = cam.rect; var prevAspect = cam.aspect;
                cam.rect = new Rect(0f, 0f, 1f, 1f); cam.aspect = (float)w / h;
                cam.targetTexture = rt; cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0); tex.Apply();
                System.IO.File.WriteAllBytes(OutPath, tex.EncodeToPNG());
                cam.targetTexture = prevT; cam.rect = prevRect; cam.aspect = prevAspect;
                RenderTexture.active = prevA;
                Object.DestroyImmediate(tex); rt.Release(); Object.DestroyImmediate(rt);
                Debug.Log("[AutoCapture] saved (SceneCam) " + OutPath);
                return;
            }
            if (CaptureActiveCameras()) return;
            Debug.LogWarning("[AutoCapture] capture failed: no active camera");
        }

        private static bool CaptureActiveCameras()
        {
            var cams = new List<Camera>();
#pragma warning disable 0618
            foreach (var c in Object.FindObjectsOfType<Camera>())
#pragma warning restore 0618
            {
                if (c == null || !c.enabled || !c.gameObject.activeInHierarchy) continue;
                if (c.targetTexture != null) continue;
                cams.Add(c);
            }
            if (cams.Count == 0) return false;
            cams.Sort((a, b) => a.depth.CompareTo(b.depth));

            const int w = 800, h = 600;
            var rt = new RenderTexture(w, h, 24);
            var prevA = RenderTexture.active;
            var prevTargets = new RenderTexture[cams.Count];
            // Save the on-screen rect/aspect so the Pillarbox (視窗化) sub-rect doesn't crop the capture:
            // each camera renders the FULL 4:3 frame into the 4:3 RT, then is restored (see SceneCam path above).
            var prevRects = new Rect[cams.Count];
            var prevAspects = new float[cams.Count];
            RenderTexture.active = rt;
            GL.Clear(true, true, Color.black);
            for (int i = 0; i < cams.Count; i++)
            {
                prevTargets[i] = cams[i].targetTexture;
                prevRects[i] = cams[i].rect;
                prevAspects[i] = cams[i].aspect;
                cams[i].rect = new Rect(0f, 0f, 1f, 1f);
                cams[i].aspect = (float)w / h;
                cams[i].targetTexture = rt;
                cams[i].Render();
            }

            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            System.IO.File.WriteAllBytes(OutPath, tex.EncodeToPNG());

            for (int i = 0; i < cams.Count; i++)
                if (cams[i] != null) { cams[i].targetTexture = prevTargets[i]; cams[i].rect = prevRects[i]; cams[i].aspect = prevAspects[i]; }
            RenderTexture.active = prevA;
            Object.DestroyImmediate(tex);
            rt.Release();
            Object.DestroyImmediate(rt);
            Debug.Log("[AutoCapture] saved (cameras) " + OutPath);
            return true;
        }
    }
}
