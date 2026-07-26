using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>END-TO-END pixel guard for 使用者回報「成績結算的按鈕角落有白邊、沒去背」. The texel-level test
    /// (<see cref="ResultPanelMatteTests"/>) proves the white matte is gone from the crops; only a render can prove the
    /// SYMPTOM is gone, because it is the GPU sampler MAGNIFYING the 800×600 design that turns a faint rim into a haze.
    /// So this photographs the art through a magnifying orthographic camera on black:
    ///   • the 確定 / 保存錄像 buttons as <see cref="ResultScreen"/> actually builds them (plus the old path as a control),
    ///   • every glyph/banner, which must lose the halo WITHOUT going dark — the failure mode if a premultiplied texture
    ///     ever renders through a straight-alpha material.
    /// Runs in EditMode (Camera.Render works without play mode); skips when the game data isn't present.</summary>
    public class ResultPanelRenderTests
    {
        // design-space rects of the two buttons (ResultScreen.Build: Statis22 @595, Statis25 @694, both 97×54 at y=493)
        private static readonly Rect SaveRect = new Rect(595, 493, 97, 54);
        private static readonly Rect OkRect = new Rect(694, 493, 97, 54);
        private const int Zoom = 4;                    // design px → render px (the haze only shows under magnification)
        private const float CornerHazeLimit = 0.02f;   // mean corner luminance allowed over the black backdrop
        // measured: the old shared-atlas path lights the TOP corners to 0.113-0.118 (the matte is thickest along the top
        // edge of the crop, which is exactly where 使用者 saw it); the premultiplied crop leaves 0.0013-0.0042 (face AA).

        // Art whose rim is PURE WHITE — cleanMatte's target, so the haze has to drop hard (measured: −92% on %, −92% on
        // 100, −81% on score_num, −78% on Num3, −44% / −28% on the banners, whose glow is part white part coloured).
        private static readonly string[] WhiteRimArt = { "percent.an", "100.an", "score_num.an", "Num3.an",
                                                         "Statis28.an", "Statis30.an" };
        // Art whose rim is COLOURED (Num8 大數字, score_numS 小數字): the pure-white test in cleanMatte deliberately does
        // not match it, so these must come out unchanged — they go through the premult path only for consistency.
        private static readonly string[] ColouredRimArt = { "Num8.an", "score_numS.an" };

        private static bool DataPresent() => File.Exists(Path.Combine(SdoExtracted.ResultStatisDir, "Statis25.an"));

        // ---------------------------------------------------------------- buttons

        [Test]
        public void ResultScreenButtons_MagnifiedCorners_HaveNoWhiteFringe()
        {
            if (!DataPresent()) Assert.Ignore("結算 STATISTIC art not present in this environment.");

            GameObject hudGo = null, root = null; Texture2D shot = null;
            try
            {
                hudGo = new GameObject("HudCamProbe");
                var hud = hudGo.AddComponent<Camera>(); hud.enabled = false;

                var result = new ResultScreen();
                result.Build(hud);                          // the REAL panel — buttons included
                root = (GameObject)typeof(ResultScreen)
                    .GetField("_root", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(result);
                Assert.IsNotNull(root, "ResultScreen root missing");
                root.SetActive(true);                       // Build leaves the panel hidden

                shot = Photograph(ButtonView, "result-buttons.png");
                foreach (var (name, rect) in new[] { ("save", SaveRect), ("ok", OkRect) })
                    foreach (var (corner, lum) in CornerHaze(shot, rect))
                    {
                        TestContext.Out.WriteLine($"fixed {name} {corner}: {lum:F4}");
                        Assert.Less(lum, CornerHazeLimit, $"{name} {corner} corner glows ({lum:F4}) — white matte leaking");
                    }
            }
            finally { Cleanup(shot, root, hudGo); }
        }

        [Test]
        public void OldStraightAlphaButtons_MagnifiedCorners_ShowTheFringe()
        {
            // Control group: the same crops through the OLD path (shared bled atlas + straight alpha) must still light up
            // the corners under the same measurement — otherwise the test above would pass for the wrong reason.
            if (!DataPresent()) Assert.Ignore("結算 STATISTIC art not present in this environment.");

            GameObject root = null; Texture2D shot = null;
            try
            {
                string dir = SdoExtracted.ResultStatisDir;
                root = new GameObject("OldButtons");
                foreach (var (an, rect) in new[] { ("Statis22.an", SaveRect), ("Statis25.an", OkRect) })
                    NewSprite(root, an, SdoExtracted.LoadAn1(dir, an, bleed: true), rect.x, rect.y, null);

                shot = Photograph(ButtonView, "result-buttons-old.png");
                float worst = 0f;
                foreach (var rect in new[] { SaveRect, OkRect })
                    foreach (var (corner, lum) in CornerHaze(shot, rect))
                    { TestContext.Out.WriteLine($"old-path {corner}: {lum:F4}"); worst = Mathf.Max(worst, lum); }
                Assert.Greater(worst, CornerHazeLimit,
                    "the old straight-alpha path should still leak the white matte into the corners (control group)");
            }
            finally { Cleanup(shot, root, null); }
        }

        [Test]
        public void ResultScreenButtons_DrawTheirOwnArt_NotEachOthers()
        {
            // THE regression 使用者 caught in-game: with one SHARED premult material every SpriteRenderer on the panel
            // sampled the same _MainTex, so both buttons came out reading 保存錄像 (and the digits drew the YOU WIN
            // banner). A single-sprite render can never catch it — the buttons have to be photographed TOGETHER, and
            // each compared against what that art looks like on its own.
            if (!DataPresent()) Assert.Ignore("結算 STATISTIC art not present in this environment.");

            GameObject hudGo = null, root = null, refGo = null;
            Texture2D panel = null, okRef = null, saveRef = null;
            try
            {
                hudGo = new GameObject("HudCamProbe");
                var hud = hudGo.AddComponent<Camera>(); hud.enabled = false;
                var result = new ResultScreen();
                result.Build(hud);
                root = (GameObject)typeof(ResultScreen)
                    .GetField("_root", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(result);
                root.SetActive(true);
                panel = Photograph(ButtonView, "regress-panel.png");
                Object.DestroyImmediate(root); root = null;

                // Reference shots: the same two crops, ONE AT A TIME, each drawn the correct way.
                string dir = SdoExtracted.ResultStatisDir;
                okRef = ShootAlone(dir, "Statis25.an", OkRect, "regress-ok-alone.png", ref refGo);
                saveRef = ShootAlone(dir, "Statis22.an", SaveRect, "regress-save-alone.png", ref refGo);

                // Sanity: the two buttons genuinely look different, so the comparison below can tell them apart.
                Assert.Greater(MeanDiff(okRef, saveRef, OkRect), 0.02f,
                    "the two button crops should differ where the 確定 / 保存錄像 text sits");

                float okErr = MeanDiff(panel, okRef, OkRect);
                float saveErr = MeanDiff(panel, saveRef, SaveRect);
                float okWrong = MeanDiff(panel, saveRef, OkRect);   // panel's OK slot vs the SAVE art at that slot
                TestContext.Out.WriteLine($"ok vs own {okErr:F4} | save vs own {saveErr:F4} | ok vs wrong art {okWrong:F4}");

                Assert.Less(okErr, 0.01f, "確定 button does not match the 確定 art — it is drawing another sprite's texture");
                Assert.Less(saveErr, 0.01f, "保存錄像 button does not match its own art");
                Assert.Greater(okWrong, okErr * 2f, "the 確定 slot matches the WRONG art as well as its own — textures are bleeding");
            }
            finally
            {
                if (panel != null) Object.DestroyImmediate(panel);
                if (okRef != null) Object.DestroyImmediate(okRef);
                if (saveRef != null) Object.DestroyImmediate(saveRef);
                if (refGo != null) Object.DestroyImmediate(refGo);
                Cleanup(null, root, hudGo);
            }
        }

        [Test]
        public void FullPanel_EveryPremultSprite_MatchesItsOwnArtInPlace()
        {
            // The broadest form: build the panel WITH a populated row (rank badge, digits, hit-rate, grade — the very
            // elements 使用者 saw drawing GAME OVER and YOU WIN), photograph the whole 800×600 design, then re-photograph
            // each premultiplied sprite ALONE at the exact same spot and require the two to agree. Any texture bleeding
            // anywhere on the panel shows up here, not just on the two buttons.
            if (!DataPresent()) Assert.Ignore("結算 STATISTIC art not present in this environment.");

            var full = new Rect(0, 0, 800, 600);
            GameObject hudGo = null, root = null, lone = null;
            Texture2D panel = null;
            try
            {
                hudGo = new GameObject("HudCamProbe");
                var hud = hudGo.AddComponent<Camera>(); hud.enabled = false;
                var result = new ResultScreen();
                result.Build(hud);
                result.Show("Identic Conflict", "5", new[]
                {
                    new ResultScreen.Row { Rank = 1, Name = "飄漂o", Perfect = 111, Cool = 111, Bad = 1, Miss = 11,
                                           MaxCombo = 111, Accuracy = 98.76, Score = 111111, Grade = "A", IsLocal = true },
                }, localWon: true, expGained: 24, coinsGained: 0);
                root = (GameObject)typeof(ResultScreen)
                    .GetField("_root", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(result);
                root.SetActive(true);

                // The rank rows start off-screen at x = +800 and slide in over time; EditMode has no ticking clock, so
                // land them by hand — otherwise the very elements 使用者 saw corrupted (rank badge, digits, %, 成績字)
                // would sit outside the frame and never be compared.
                var rowRoots = (List<GameObject>)typeof(ResultScreen)
                    .GetField("_rowRoots", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(result);
                foreach (var rr in rowRoots)
                {
                    var lp = rr.transform.localPosition; lp.x = 0f; rr.transform.localPosition = lp;
                }
                result.PreviewBanner(win: true, atStart: false);   // park the YOU WIN banner at its final spot
                // Same for the EXP / G rolling totals: no clock in EditMode, so settle them by hand. They are drawn by
                // RollingDigits, which builds its own SpriteRenderers outside NewSR — the path that shipped un-paired.
                foreach (var f in new[] { "_expTotal", "_gTotal" })
                {
                    var rd = typeof(ResultScreen).GetField(f, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(result);
                    if (rd == null) continue;
                    rd.GetType().GetMethod("SetTarget").Invoke(rd, new object[] { 123456L, 0f });
                    rd.GetType().GetMethod("Tick").Invoke(rd, new object[] { 999f });
                }

                // Snapshot what every premultiplied renderer is showing and where, before we tear the panel down.
                var placed = new List<(string name, Sprite spr, Vector3 pos, int order)>();
                foreach (var sr in root.GetComponentsInChildren<SpriteRenderer>(true))
                    if (sr.sprite != null && SdoExtracted.IsPremultTexture(sr.sprite.texture) && sr.gameObject.activeInHierarchy)
                        placed.Add((sr.name, sr.sprite, sr.transform.position, sr.sortingOrder));
                Assert.Greater(placed.Count, 5, "expected the populated panel to carry many premultiplied sprites");

                panel = PhotographRect(full, "regress-full-panel.png");
                Object.DestroyImmediate(root); root = null;

                int compared = 0;
                foreach (var (name, spr, pos, order) in placed)
                {
                    // A design-space rect for this sprite, clipped to the panel; skip anything off-screen.
                    var size = spr.bounds.size;
                    var design = new Rect(SdoLayout.Width / 2f + pos.x - size.x / 2f,
                                          SdoLayout.Height / 2f - pos.y - size.y / 2f, size.x, size.y);
                    design = Rect.MinMaxRect(Mathf.Max(1, design.xMin), Mathf.Max(1, design.yMin),
                                             Mathf.Min(799, design.xMax), Mathf.Min(599, design.yMax));
                    if (design.width < 4f || design.height < 4f) continue;

                    lone = new GameObject("Lone");
                    var sr2 = new GameObject(name).AddComponent<SpriteRenderer>();
                    sr2.transform.SetParent(lone.transform, false);
                    sr2.sprite = spr; sr2.sortingOrder = order;
                    sr2.sharedMaterial = SdoExtracted.PremultSpriteMaterial(spr.texture);
                    sr2.transform.position = pos;
                    var alone = PhotographRect(full, null);
                    Object.DestroyImmediate(lone); lone = null;

                    // The panel has other art behind/around this sprite, so compare only where the sprite is OPAQUE.
                    float err = MaskedDiff(panel, alone, design, full);
                    Object.DestroyImmediate(alone);
                    compared++;
                    Assert.Less(err, 0.05f,
                        $"{name} on the full panel does not match the same sprite drawn alone (err {err:F4}) — " +
                        "its texture is bleeding from / into another renderer");
                }
                TestContext.Out.WriteLine($"full-panel sprites compared: {compared}");
                Assert.Greater(compared, 5, "expected to compare many sprites");
            }
            finally
            {
                if (panel != null) Object.DestroyImmediate(panel);
                if (lone != null) Object.DestroyImmediate(lone);
                Cleanup(null, root, hudGo);
            }
        }

        [Test]
        public void ManyPremultSprites_Together_EachKeepsItsOwnTexture()
        {
            // The general form of the same rule, with no ResultScreen involved: put several DIFFERENT premultiplied
            // sprites on screen at once through the supported pairing, and every one must still show its own art.
            if (!DataPresent()) Assert.Ignore("結算 STATISTIC art not present in this environment.");

            string dir = SdoExtracted.ResultStatisDir;
            var ans = new[] { "Statis25.an", "Statis22.an", "100.an", "percent.an" };
            var slots = new Rect[ans.Length];
            var sprites = new Sprite[ans.Length];
            float x = 300f;
            for (int i = 0; i < ans.Length; i++)
            {
                sprites[i] = SdoExtracted.LoadAnSoloPremultiplied(dir, ans[i], pad: 0, cleanMatte: true);
                Assert.IsNotNull(sprites[i], ans[i] + " failed to load");
                slots[i] = new Rect(x, 300f, sprites[i].rect.width, sprites[i].rect.height);
                x += sprites[i].rect.width + 4f;
            }
            var view = Rect.MinMaxRect(slots[0].xMin - 6, 300f - 6, slots[ans.Length - 1].xMax + 6, 300f + 60f);

            GameObject all = null, one = null;
            Texture2D together = null;
            var alone = new Texture2D[ans.Length];
            try
            {
                all = new GameObject("AllPremult");
                for (int i = 0; i < ans.Length; i++)
                    NewSprite(all, ans[i], sprites[i], slots[i].x, slots[i].y,
                              SdoExtracted.PremultSpriteMaterial(sprites[i].texture));
                together = Photograph(view, "regress-many-together.png");
                Object.DestroyImmediate(all); all = null;

                for (int i = 0; i < ans.Length; i++)
                {
                    one = new GameObject("OnePremult");
                    NewSprite(one, ans[i], sprites[i], slots[i].x, slots[i].y,
                              SdoExtracted.PremultSpriteMaterial(sprites[i].texture));
                    alone[i] = Photograph(view, null);
                    Object.DestroyImmediate(one); one = null;
                }

                for (int i = 0; i < ans.Length; i++)
                {
                    float err = MeanDiff(together, alone[i], slots[i], view);
                    TestContext.Out.WriteLine($"{ans[i]} together-vs-alone {err:F4}");
                    Assert.Less(err, 0.01f, $"{ans[i]} changed when other premultiplied sprites were on screen — " +
                                            "they are sharing one material's _MainTex");
                }
            }
            finally
            {
                if (together != null) Object.DestroyImmediate(together);
                foreach (var t in alone) if (t != null) Object.DestroyImmediate(t);
                if (all != null) Object.DestroyImmediate(all);
                if (one != null) Object.DestroyImmediate(one);
            }
        }

        // ---------------------------------------------------------------- glyphs & banners

        [Test]
        public void WhiteRimArt_LosesTheHalo_ButKeepsItsInk([ValueSource(nameof(WhiteRimArt))] string an)
        {
            var (haloOld, haloNew, bodyOld, bodyNew) = MeasureHalo(an);
            Assert.Less(haloNew, haloOld * 0.8f, $"{an}: the white halo must drop clearly");
            Assert.Greater(bodyNew, bodyOld * 0.85f,
                $"{an}: the artwork must not go dark — that means a premultiplied texture drew through a straight-alpha material");
        }

        [Test]
        public void ColouredRimArt_IsLeftAlone([ValueSource(nameof(ColouredRimArt))] string an)
        {
            // cleanMatte only clears PURE-WHITE sub-48 texels, so a coloured rim must survive: neither the halo nor the
            // ink may move much. This is what stops the matte cleaner from quietly eating coloured anti-aliasing.
            var (haloOld, haloNew, bodyOld, bodyNew) = MeasureHalo(an);
            Assert.Greater(haloNew, haloOld * 0.85f, $"{an}: a coloured rim must NOT be scrubbed");
            Assert.Greater(bodyNew, bodyOld * 0.85f, $"{an}: the artwork must not go dark");
        }

        /// <summary>Photograph <paramref name="an"/> through the old and the fixed path at the same spot and return the
        /// mean luminance of the faint surround (the haze) and of the artwork, measured over the SAME texels.</summary>
        private static (float haloOld, float haloNew, float bodyOld, float bodyNew) MeasureHalo(string an)
        {
            if (!DataPresent()) Assert.Ignore("結算 STATISTIC art not present in this environment.");

            string dir = SdoExtracted.ResultStatisDir;
            var oldSprite = SdoExtracted.LoadAn1(dir, an, bleed: true);
            var newSprite = SdoExtracted.LoadAnSoloPremultiplied(dir, an, pad: 0, cleanMatte: true);
            Assert.IsNotNull(oldSprite); Assert.IsNotNull(newSprite);

            var place = new Rect(400 - oldSprite.rect.width / 2f, 300 - oldSprite.rect.height / 2f,
                                 oldSprite.rect.width, oldSprite.rect.height);
            var view = Rect.MinMaxRect(place.xMin - 5, place.yMin - 5, place.xMax + 5, place.yMax + 5);

            GameObject a = null, b = null; Texture2D oldShot = null, newShot = null;
            try
            {
                a = new GameObject("GlyphOld");
                NewSprite(a, an, oldSprite, place.x, place.y, null);
                oldShot = Photograph(view, an + "-old.png");
                Object.DestroyImmediate(a); a = null;

                b = new GameObject("GlyphNew");
                Assert.IsNotNull(SdoExtracted.PremultUiMaterial, "Sdo/SpritePremultiply missing from the project");
                NewSprite(b, an, newSprite, place.x, place.y, SdoExtracted.PremultUiMaterial);   // what ResultScreen.NewSR does
                newShot = Photograph(view, an + "-new.png");

                var oldPx = oldShot.GetPixels();
                var newPx = newShot.GetPixels();
                Assert.AreEqual(oldPx.Length, newPx.Length);

                // Masks defined on the OLD shot so both images are measured over the same texels.
                float haloOld = 0f, haloNew = 0f; int haloN = 0;   // the faint surround — the haze being removed
                float bodyOld = 0f, bodyNew = 0f; int bodyN = 0;   // the artwork itself (core + its real AA edge)
                for (int i = 0; i < oldPx.Length; i++)
                {
                    float l = Lum(oldPx[i]);
                    if (l > 0.002f && l < 0.10f) { haloOld += l; haloNew += Lum(newPx[i]); haloN++; }
                    else if (l >= 0.10f) { bodyOld += l; bodyNew += Lum(newPx[i]); bodyN++; }
                }
                Assert.Greater(haloN, 0, "expected a measurable halo around the old art");
                Assert.Greater(bodyN, 0, "expected artwork pixels");
                haloOld /= haloN; haloNew /= haloN; bodyOld /= bodyN; bodyNew /= bodyN;
                TestContext.Out.WriteLine($"{an} halo {haloOld:F4} -> {haloNew:F4} | body {bodyOld:F4} -> {bodyNew:F4}");
                return (haloOld, haloNew, bodyOld, bodyNew);
            }
            finally
            {
                if (oldShot != null) Object.DestroyImmediate(oldShot);
                if (newShot != null) Object.DestroyImmediate(newShot);
                if (a != null) Object.DestroyImmediate(a);
                if (b != null) Object.DestroyImmediate(b);
            }
        }

        // ---------------------------------------------------------------- helpers

        // The button frame: both buttons plus an 8px margin of whatever sits behind them.
        private static Rect ButtonView => Rect.MinMaxRect(SaveRect.xMin - 8, SaveRect.yMin - 8, OkRect.xMax + 8, OkRect.yMax + 8);

        private static SpriteRenderer NewSprite(GameObject parent, string name, Sprite spr, float x, float y, Material mat)
        {
            var sr = new GameObject(name).AddComponent<SpriteRenderer>();
            sr.transform.SetParent(parent.transform, false);
            sr.sprite = spr; sr.sortingOrder = 140;
            if (mat != null) sr.sharedMaterial = mat;
            SdoLayout.PlaceTopLeft(sr, x, y, 0f);
            return sr;
        }

        /// <summary>Photograph one crop on its own, drawn the supported way, framed by <see cref="ButtonView"/>.</summary>
        private static Texture2D ShootAlone(string dir, string an, Rect place, string dump, ref GameObject holder)
        {
            if (holder != null) Object.DestroyImmediate(holder);
            holder = new GameObject("Alone");
            var spr = SdoExtracted.LoadAnSoloPremultiplied(dir, an, pad: 0, cleanMatte: true);
            Assert.IsNotNull(spr, an + " failed to load");
            NewSprite(holder, an, spr, place.x, place.y, SdoExtracted.PremultSpriteMaterial(spr.texture));
            var shot = Photograph(ButtonView, dump);
            Object.DestroyImmediate(holder); holder = null;
            return shot;
        }

        /// <summary>Photograph a design-space rect 1:1 (no zoom) — used for the whole 800×600 panel.</summary>
        private static Texture2D PhotographRect(Rect view, string dumpName)
        {
            int rw = Mathf.RoundToInt(view.width), rh = Mathf.RoundToInt(view.height);
            var rt = new RenderTexture(rw, rh, 16, RenderTextureFormat.ARGB32);
            var camGo = new GameObject("ResultPanelFullCam");
            try
            {
                var cam = camGo.AddComponent<Camera>();
                cam.orthographic = true;
                cam.orthographicSize = view.height / 2f;
                cam.transform.position = new Vector3(SdoLayout.WorldX(view.center.x), SdoLayout.WorldY(view.center.y), -100f);
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                cam.targetTexture = rt;
                cam.Render();

                var prev = RenderTexture.active; RenderTexture.active = rt;
                var shot = new Texture2D(rw, rh, TextureFormat.RGBA32, false);
                shot.ReadPixels(new Rect(0, 0, rw, rh), 0, 0); shot.Apply();
                RenderTexture.active = prev;

                var dump = System.Environment.GetEnvironmentVariable("SDO_SHOT_DIR");
                if (dumpName != null && !string.IsNullOrEmpty(dump) && Directory.Exists(dump))
                    File.WriteAllBytes(Path.Combine(dump, dumpName), shot.EncodeToPNG());
                return shot;
            }
            finally
            {
                RenderTexture.active = null;
                Object.DestroyImmediate(camGo);
                rt.Release(); Object.DestroyImmediate(rt);
            }
        }

        /// <summary>Difference between two shots over a rect, measured ONLY where the reference shot is lit — so panel
        /// art sitting behind/around the sprite does not count against it. Zoom is 1 here (PhotographRect).</summary>
        private static float MaskedDiff(Texture2D panel, Texture2D alone, Rect design, Rect view)
        {
            int x = Mathf.RoundToInt(design.xMin - view.xMin);
            int y = Mathf.RoundToInt(view.yMax - design.yMax);
            int w = Mathf.RoundToInt(design.width), h = Mathf.RoundToInt(design.height);
            var pa = panel.GetPixels(x, y, w, h);
            var pb = alone.GetPixels(x, y, w, h);
            float sum = 0f; int n = 0;
            for (int i = 0; i < pa.Length; i++)
            {
                if (Lum(pb[i]) < 0.05f) continue;              // reference is dark here → the sprite is not covering it
                sum += Mathf.Abs(pa[i].r - pb[i].r) + Mathf.Abs(pa[i].g - pb[i].g) + Mathf.Abs(pa[i].b - pb[i].b);
                n += 3;
            }
            return n == 0 ? 0f : sum / n;
        }

        /// <summary>Mean per-channel absolute difference between two shots over a design-space rect (frame = ButtonView).</summary>
        private static float MeanDiff(Texture2D a, Texture2D b, Rect design) => MeanDiff(a, b, design, ButtonView);

        private static float MeanDiff(Texture2D a, Texture2D b, Rect design, Rect view)
        {
            var p = ToPixels(view, design);
            var pa = a.GetPixels(p.x, p.y, p.width, p.height);
            var pb = b.GetPixels(p.x, p.y, p.width, p.height);
            float sum = 0f;
            for (int i = 0; i < pa.Length; i++)
                sum += Mathf.Abs(pa[i].r - pb[i].r) + Mathf.Abs(pa[i].g - pb[i].g) + Mathf.Abs(pa[i].b - pb[i].b);
            return sum / (pa.Length * 3f);
        }

        private static void Cleanup(Texture2D shot, GameObject root, GameObject hudGo)
        {
            if (shot != null) Object.DestroyImmediate(shot);
            if (root != null) Object.DestroyImmediate(root);
            if (hudGo != null) Object.DestroyImmediate(hudGo);
        }

        /// <summary>Render the current scene's sprites through a magnifying ortho camera framing <paramref name="view"/>
        /// (design space) on BLACK — the harshest backdrop for a white fringe. Set SDO_SHOT_DIR to also drop the PNG
        /// there (how the fix was eyeballed).</summary>
        private static Texture2D Photograph(Rect view, string dumpName)
        {
            int rw = Mathf.RoundToInt(view.width) * Zoom, rh = Mathf.RoundToInt(view.height) * Zoom;
            var rt = new RenderTexture(rw, rh, 16, RenderTextureFormat.ARGB32);
            var camGo = new GameObject("ResultPanelProbeCam");
            try
            {
                var cam = camGo.AddComponent<Camera>();
                cam.orthographic = true;
                cam.orthographicSize = view.height / 2f;    // 1 design px = Zoom render px
                cam.transform.position = new Vector3(SdoLayout.WorldX(view.center.x), SdoLayout.WorldY(view.center.y), -100f);
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                cam.targetTexture = rt;
                cam.Render();

                var prev = RenderTexture.active; RenderTexture.active = rt;
                var shot = new Texture2D(rw, rh, TextureFormat.RGBA32, false);
                shot.ReadPixels(new Rect(0, 0, rw, rh), 0, 0); shot.Apply();
                RenderTexture.active = prev;

                var dump = System.Environment.GetEnvironmentVariable("SDO_SHOT_DIR");
                if (dumpName != null && !string.IsNullOrEmpty(dump) && Directory.Exists(dump))
                    File.WriteAllBytes(Path.Combine(dump, dumpName), shot.EncodeToPNG());
                return shot;
            }
            finally
            {
                RenderTexture.active = null;
                Object.DestroyImmediate(camGo);
                rt.Release(); Object.DestroyImmediate(rt);
            }
        }

        /// <summary>Mean brightness of each rounded CORNER over the pure-black backdrop. The matte is thickest in the
        /// corners and the button's rounded face never reaches them (radius ≈ 8 design px &gt; the 4×4 block sampled), so
        /// anything lit there is leaked matte, not the button.</summary>
        private static (string corner, float lum)[] CornerHaze(Texture2D shot, Rect r)
        {
            const float S = 4f;    // design-px block sampled inside each corner (stays clear of the rounded face)
            var view = ButtonView;
            var corners = new[]
            {
                ("top-left",     new Rect(r.xMin,     r.yMin,     S, S)),
                ("top-right",    new Rect(r.xMax - S, r.yMin,     S, S)),
                ("bottom-left",  new Rect(r.xMin,     r.yMax - S, S, S)),
                ("bottom-right", new Rect(r.xMax - S, r.yMax - S, S, S)),
            };
            var outp = new (string, float)[corners.Length];
            for (int i = 0; i < corners.Length; i++)
                outp[i] = (corners[i].Item1, Lum(AvgDesign(shot, view, corners[i].Item2)));
            return outp;
        }

        private static float Lum(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

        // design-space rect → shot pixels (shot y is bottom-up, design y is top-down)
        private static RectInt ToPixels(Rect view, Rect design)
        {
            int x = Mathf.RoundToInt((design.xMin - view.xMin) * Zoom);
            int y = Mathf.RoundToInt((view.yMax - design.yMax) * Zoom);
            return new RectInt(x, y, Mathf.RoundToInt(design.width * Zoom), Mathf.RoundToInt(design.height * Zoom));
        }

        private static Color AvgDesign(Texture2D shot, Rect view, Rect design)
        {
            var p = ToPixels(view, design);
            var px = shot.GetPixels(p.x, p.y, p.width, p.height);
            Color sum = Color.black;
            foreach (var c in px) sum += c;
            return sum / Mathf.Max(1, px.Length);
        }
    }
}
