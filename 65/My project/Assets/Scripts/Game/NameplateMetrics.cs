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

        /// <summary>Same calibration for the builtin LegacyRuntime (Arial) face used by the gameplay HUD's bottom
        /// row (歌名 / LV / 時間) — those were authored as fontSize 64 with characterSize = designPx × 0.2, so this
        /// keeps their on-screen size EXACTLY as before while the raster size follows the physical pixels.</summary>
        public const float PxToCharSizeLegacyAt64 = 0.2f;

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

        /// <summary>Supersample factor for the HUD raster (<see cref="RasterPxForEm"/>). 2 is the sweet spot:
        /// bilinear minification at exactly 2:1 IS a box filter (each output pixel averages the 2×2 it covers),
        /// so the glyph downsamples cleanly with no mipmap — while 1:1 would smear by half a pixel whenever the
        /// quad lands off the pixel grid (design y 585 × a fractional scale almost never lands on an integer).</summary>
        public const float HudTextSupersample = 2f;

        /// <summary>How tall the em box ACTUALLY stands on screen, in design px, for a label calibrated as
        /// (<paramref name="designPx"/>, <paramref name="pxToCharSizeAt64"/>). TextMesh's quad height =
        /// fontPx × characterSize × 0.1 world units (= design px, see <c>SdoLayout</c>); substituting
        /// <see cref="CharacterSizeFor"/> cancels fontPx out and leaves 0.1 × 64 × calibration × designPx.
        /// Legacy/Arial (0.2) → 1.28 × designPx; CJK (0.11) → 0.704 × designPx. The calibration constants were
        /// fitted to the INK height, so the em box the bitmap has to cover is bigger than the nominal designPx.</summary>
        public static float EmDesignPx(float designPx, float pxToCharSizeAt64)
            => 0.1f * CalibrationFontPx * pxToCharSizeAt64 * designPx;

        /// <summary>Raster size (px) that actually resolves the glyph: the em box's PHYSICAL pixel height, times
        /// <paramref name="supersample"/>. <see cref="FontPxFor"/> sizes off the nominal designPx instead, which is
        /// short by the <see cref="EmDesignPx"/> factor — for the HUD song title (legacy 0.2) that is 1.28×, so its
        /// bitmap was rasterized SMALLER than it is drawn and every glyph came out magnified/blurry. Clamped like
        /// <see cref="FontPxFor"/>.</summary>
        public static int RasterPxForEm(float designPx, float scaleY, float pxToCharSizeAt64,
                                        float supersample = HudTextSupersample, int min = 8, int max = 200)
            => Mathf.Clamp(
                Mathf.RoundToInt(EmDesignPx(designPx, pxToCharSizeAt64) * scaleY * Mathf.Max(0.01f, supersample)),
                min, max);

        /// <summary>TextMesh characterSize that keeps the on-screen height at <paramref name="designPx"/> for a
        /// bitmap rasterized at <paramref name="fontPx"/>: quad size scales with BOTH fontSize and characterSize,
        /// so the calibration (<paramref name="pxToCharSizeAt64"/>, per typeface) is rescaled by 64/fontSize.</summary>
        public static float CharacterSizeFor(float designPx, int fontPx, float pxToCharSizeAt64 = PxToCharSizeAt64)
            => designPx * pxToCharSizeAt64 * (CalibrationFontPx / (float)Mathf.Max(1, fontPx));
    }
}
