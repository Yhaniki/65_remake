using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Maps DdrGamePlay.xml coordinates (800×600 design space, TOP-LEFT origin, y down)
    /// to Unity world space, pixel-perfect: 1 design px = 1 world unit.
    /// Set up an orthographic camera with size = Height/2 (=300) so the 800×600 frame
    /// fills a 4:3 view. Sprites loaded by <see cref="SdoExtracted"/> use pixelsPerUnit=1,
    /// so their native pixel size equals their world size.
    /// </summary>
    public static class SdoLayout
    {
        public const float Width = 800f;
        public const float Height = 600f;

        /// <summary>Configure the main camera for the 800×600 design frame.</summary>
        public static void SetupCamera(Camera cam)
        {
            cam.orthographic = true;
            cam.orthographicSize = Height / 2f;             // 300 -> view height = 600 units
            cam.transform.position = new Vector3(0, 0, -100f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
        }

        // pure mapping (top-left design px -> world)
        public static float WorldX(float px) => px - Width / 2f;     // 0..800 -> -400..400
        public static float WorldY(float px) => Height / 2f - px;    // 0..600 ->  300..-300 (flip)

        /// <summary>World point for a design-space (top-left origin) coordinate.</summary>
        public static Vector3 ToWorld(float pxX, float pxY, float z = 0f)
            => new Vector3(WorldX(pxX), WorldY(pxY), z);

        /// <summary>
        /// Place a native-size sprite so its TOP-LEFT sits at design (x,y).
        /// (DdrGamePlay.xml Label/background images are drawn from their top-left.)
        /// </summary>
        public static void PlaceTopLeft(SpriteRenderer sr, float x, float y, float z = 0f)
        {
            if (sr.sprite == null) { sr.transform.position = ToWorld(x, y, z); return; }
            var b = sr.sprite.bounds.size;                  // px (pixelsPerUnit=1)
            sr.transform.localScale = Vector3.one;
            sr.transform.position = new Vector3(WorldX(x) + b.x / 2f, WorldY(y) - b.y / 2f, z);
        }

        /// <summary>
        /// Place + stretch a sprite to exactly fill the design box (x,y,w,h).
        /// Used for ProgressBar backgrounds / fills that the engine scales to the bar rect.
        /// </summary>
        public static void PlaceBox(SpriteRenderer sr, float x, float y, float w, float h, float z = 0f)
        {
            sr.transform.position = new Vector3(WorldX(x) + w / 2f, WorldY(y) - h / 2f, z);
            if (sr.sprite == null) { sr.transform.localScale = new Vector3(w, h, 1f); return; }
            var b = sr.sprite.bounds.size;
            sr.transform.localScale = new Vector3(
                b.x > 1e-4f ? w / b.x : w, b.y > 1e-4f ? h / b.y : h, 1f);
        }

        /// <summary>
        /// Place a ProgressBar fill clipped to a fraction of its width (left-anchored),
        /// like the original forename .an drawn to (value-min)/(max-min) of the bar box.
        /// </summary>
        public static void PlaceBarFill(SpriteRenderer sr, float x, float y, float w, float h,
                                        float fraction, float z = 0f)
        {
            fraction = Mathf.Clamp01(fraction);
            float fw = w * fraction;
            sr.transform.position = new Vector3(WorldX(x) + fw / 2f, WorldY(y) - h / 2f, z);
            if (sr.sprite == null) { sr.transform.localScale = new Vector3(fw, h, 1f); return; }
            var b = sr.sprite.bounds.size;
            sr.transform.localScale = new Vector3(
                b.x > 1e-4f ? fw / b.x : fw, b.y > 1e-4f ? h / b.y : h, 1f);
        }
    }
}
