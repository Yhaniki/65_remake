using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Pure math shared by the fake-outline nameplates — the gameplay head name (<see cref="Label3D"/>,
    /// legacy TextMesh) and the room float name (<c>Sdo.UI.Util.OutlinedLabel</c>, TMP). Both draw the
    /// outline as N shifted glyph copies behind the face; these helpers pick the copy directions, size the
    /// legacy-TextMesh raster to the PHYSICAL on-screen pixel height (so the bitmap draws ≈1:1 instead of
    /// bilinear-resampled → ragged at fullscreen), and compensate the 4:3→screen Stretch so the ring looks
    /// uniform even though the frame is magnified more horizontally than vertically. Pure statics —
    /// unit-tested in NameplateMetricsTests.
    /// </summary>
    public static class NameplateMetrics
    {
        /// <summary>Dynamic-font raster size the TextMesh calibration was measured at (see <see cref="CharacterSizeFor"/>).</summary>
        public const int CalibrationFontPx = 64;

        /// <summary>design px → TextMesh characterSize at fontSize 64: the dynamic CJK font renders ≈9 px per
        /// unit of characterSize, so px × 0.11 ≈ px tall on screen (calibrated against the offscreen capture).</summary>
        public const float PxToCharSizeAt64 = 0.11f;

        /// <summary>N evenly-spaced offsets of length <paramref name="radius"/>, starting at +X so the four
        /// cardinals are exact whenever N is a multiple of 4. 16 directions close the scalloped notches an
        /// 8-copy ring shows once fullscreen magnifies the offsets to 2–3 physical px.</summary>
        public static Vector2[] Ring(float radius, int directions)
        {
            var dirs = new Vector2[Mathf.Max(1, directions)];
            for (int i = 0; i < dirs.Length; i++)
            {
                float a = i * 2f * Mathf.PI / dirs.Length;
                dirs[i] = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius;
            }
            return dirs;
        }

        /// <summary>Physical screen px per design px, vertically: how the 600-px-tall design frame maps onto
        /// the on-screen content rect (<c>AspectController.ContentRect</c> — full screen in Stretch, the 4:3
        /// sub-rect in Pillarbox).</summary>
        public static float ScaleY(float screenH, Rect contentRect, float designH = 600f)
            => Mathf.Max(0.01f, screenH * contentRect.height / designH);

        /// <summary>Horizontal magnification relative to vertical: 1 in Pillarbox/4:3 windows; 4:3 stretched
        /// onto 16:9 = 1.33…. Divide an offset's x by this so the outline ring LOOKS uniform after the
        /// non-uniform stretch (otherwise it renders visibly thicker left/right than above/below).</summary>
        public static float AnisotropyX(float screenW, float screenH, Rect contentRect,
            float designW = 800f, float designH = 600f)
        {
            float sy = screenH * contentRect.height / designH;
            if (sy <= 1e-4f) return 1f;
            float sx = screenW * contentRect.width / designW;
            return sx <= 1e-4f ? 1f : sx / sy;
        }

        /// <summary>Pre-compensate a design-px offset for the horizontal stretch: x shrinks by the anisotropy,
        /// y passes through.</summary>
        public static Vector2 Compensate(Vector2 offset, float anisotropyX)
            => anisotropyX <= 1e-4f ? offset : new Vector2(offset.x / anisotropyX, offset.y);

        /// <summary>Dynamic-font raster size (px) for text that should stand <paramref name="designPx"/> tall on
        /// screen: the PHYSICAL pixel height, so the glyph bitmap is drawn ≈1:1 instead of resampled. Clamped —
        /// tiny sizes rasterize to unhinted mush, huge sizes bloat the dynamic font atlas.</summary>
        public static int FontPxFor(float designPx, float scaleY, int min = 8, int max = 200)
            => Mathf.Clamp(Mathf.RoundToInt(designPx * scaleY), min, max);

        /// <summary>TextMesh characterSize that keeps the on-screen height at <paramref name="designPx"/> for a
        /// bitmap rasterized at <paramref name="fontPx"/>: quad size scales with BOTH fontSize and characterSize,
        /// so the 0.11-at-64 calibration is rescaled by 64/fontSize.</summary>
        public static float CharacterSizeFor(float designPx, int fontPx)
            => designPx * PxToCharSizeAt64 * (CalibrationFontPx / (float)Mathf.Max(1, fontPx));
    }
}
