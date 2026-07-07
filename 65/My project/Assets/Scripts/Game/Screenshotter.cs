using System;
using System.Collections;
using System.IO;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Global in-game camera: press Print Screen to save the whole composited game view — 3D stage + every UI
    /// layer, exactly what's on screen — as a timestamped .jpg into the screensave/ folder beside the exe
    /// (<see cref="SdoExtracted.ScreensaveDir"/>). Self-boots and survives scene loads, so it works on every
    /// screen: lobby, room, song-select and gameplay.
    /// </summary>
    public sealed class Screenshotter : MonoBehaviour
    {
        private const int JpgQuality = 92;     // near-lossless; keeps text/HUD crisp while staying a normal .jpg size

        private bool _capturing;               // one shot at a time (Print held / spammed won't queue duplicates)
        private string _flashName;             // last-saved file name, flashed briefly on screen as confirmation
        private float _flashUntil;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            var go = new GameObject("Screenshotter");
            go.AddComponent<Screenshotter>();
            DontDestroyOnLoad(go);
        }

        private void Update()
        {
            if (!_capturing && Input.GetKeyDown(KeyCode.Print))
                StartCoroutine(Capture());
        }

        private IEnumerator Capture()
        {
            _capturing = true;
            // Grab only after the frame — including all UI overlays — has finished rendering, so the .jpg is a
            // faithful copy of the on-screen composite (the confirmation flash below is set post-capture, so it can
            // never leak into the shot).
            yield return new WaitForEndOfFrame();

            Texture2D tex = null, cropped = null;
            try
            {
                tex = ScreenCapture.CaptureScreenshotAsTexture();     // full window framebuffer: 3D + UI composited
                var dir = SdoExtracted.ScreensaveDir;
                Directory.CreateDirectory(dir);
                var name = ScreenshotNaming.UniqueFileName(DateTime.Now, n => File.Exists(Path.Combine(dir, n)));
                var path = Path.Combine(dir, name);
                File.WriteAllBytes(path, Encode(tex, ref cropped));   // crop off any pillar/letterbox bars first
                _flashName = name;
                _flashUntil = Time.unscaledTime + 1.6f;
                Debug.Log("[Screenshotter] saved " + path);
            }
            catch (Exception e) { Debug.LogWarning("[Screenshotter] capture failed: " + e); }
            finally
            {
                if (cropped != null) Destroy(cropped);
                if (tex != null) Destroy(tex);
                _capturing = false;
            }
        }

        /// <summary>Encode the captured frame, cropping off any pillar/letterbox bars so only the 4:3 game frame is
        /// saved (no-op when the frame already fills the screen). The cropped copy, if made, is returned via
        /// <paramref name="cropped"/> so the caller can destroy it.</summary>
        private static byte[] Encode(Texture2D full, ref Texture2D cropped)
        {
            var r = AspectController.ContentRect;   // bottom-left origin — matches CaptureScreenshotAsTexture's pixel origin
            if (!ScreenshotCrop.PixelRect(r.x, r.y, r.width, r.height, full.width, full.height,
                                          out int x, out int y, out int w, out int h))
                return full.EncodeToJPG(JpgQuality);

            cropped = new Texture2D(w, h, TextureFormat.RGB24, false);
            cropped.SetPixels(full.GetPixels(x, y, w, h));
            cropped.Apply(false);
            return cropped.EncodeToJPG(JpgQuality);
        }

        private void OnGUI()
        {
            if (string.IsNullOrEmpty(_flashName) || Time.unscaledTime > _flashUntil) return;

            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                alignment = TextAnchor.UpperRight,
                fontStyle = FontStyle.Bold,
            };
            style.normal.textColor = Color.white;
            var text = "📷  screensave/" + _flashName;
            var size = style.CalcSize(new GUIContent(text));

            // Anchor the toast to the top-right corner of the actual game frame (inside the pillar/letterbox bars),
            // in GUI space (top-left origin) — ContentRect is bottom-left origin, so the frame's top = height - top(vp).
            var vp = AspectController.ContentRect;
            float contentRight = (vp.x + vp.width) * Screen.width;
            float contentTop = (1f - (vp.y + vp.height)) * Screen.height;
            var rect = new Rect(contentRight - size.x - 14f, contentTop + 14f, size.x + 4f, size.y + 4f);

            var bg = new Rect(rect.x - 6f, rect.y - 3f, rect.width + 12f, rect.height + 6f);
            var prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(bg, Texture2D.whiteTexture);
            GUI.color = prev;
            GUI.Label(rect, text, style);
        }
    }
}
