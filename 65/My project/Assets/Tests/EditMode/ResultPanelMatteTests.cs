using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>Guards the fix for 使用者回報「成績結算的按鈕角落有白邊、沒去背」. Two causes, one treatment:
    ///   • the 確定 / 保存錄像 buttons and the YOU WIN / LOSE banners carry a real ISOLATED matte — low-alpha pure white
    ///     sitting 2px and more clear of the artwork (21 texels per button, 363 / 281 per banner) — and they are cropped
    ///     from the SHARED BALANCE.png with the other button states packed right next to them;
    ///   • the small glyphs (%, the all-combo 100, every digit strip, 成績字, 名次牌) have no isolated matte, but the art
    ///     bakes a low-alpha whitish rim tight around the strokes. It reads fine at the official 1:1 and blooms into a
    ///     white haze once the 800×600 design is magnified to the window.
    /// The old path (LoadAn1/LoadImage + AlphaBleed) rewrites RGB only where a ≤ 8 and never touches alpha, so both
    /// survived. <see cref="SdoExtracted.LoadAnSoloPremultiplied"/> &amp; friends with cleanMatte crop onto their own
    /// texture, clear the sub-48 white and premultiply. Integration test; skipped when the game data isn't present.</summary>
    public class ResultPanelMatteTests
    {
        // Every .an the 結算 panel loads through ResultScreen's LoadPanelSprite / LoadPanelSprites.
        private static readonly string[] SingleFrame = { "Statis25.an", "Statis22.an", "Statis28.an", "Statis30.an",
                                                         "percent.an", "100.an", "dot.an" };
        private static readonly string[] Strips = { "Num8.an", "Num3.an", "score_num.an", "score_numS.an" };
        private static readonly string[] Images = { "02/A0.PNG", "02/A1.PNG", "02/A2.PNG", "02/B2.PNG", "02/C2.PNG",
                                                    "02/D2.PNG", "02/F0.PNG", "RANK/7.PNG", "rank/1.PNG", "rank/8.PNG" };

        private static string StatisticDir()
        {
            foreach (var d in new[] { SdoExtracted.ResultStatisDir, @"H:/65_remake_clean/DATA/UI/STATIS/STATISTIC" })
                if (!string.IsNullOrEmpty(d) && File.Exists(Path.Combine(d, "Statis25.an"))) return d;
            return null;
        }

        // a texel that is (near-)transparent yet still bright white — what blooms into the 白邊 under magnification.
        private static bool IsWhiteMatte(Color32 c) => c.a < 48 && c.r > 170 && c.g > 170 && c.b > 170;

        private static int Count(Sprite s, System.Func<Color32, bool> match)
        {
            var tex = s.texture;
            var px = tex.GetPixels32();
            var r = s.rect;
            int n = 0;
            for (int y = (int)r.y; y < (int)r.yMax; y++)
                for (int x = (int)r.x; x < (int)r.xMax; x++)
                    if (match(px[y * tex.width + x])) n++;
            return n;
        }

        [Test]
        public void PanelArt_PremultCrop_HasNoWhiteMatteLeft([ValueSource(nameof(SingleFrame))] string an)
        {
            var dir = StatisticDir();
            if (dir == null) Assert.Ignore("結算 STATISTIC art not present in this environment.");

            var s = SdoExtracted.LoadAnSoloPremultiplied(dir, an, pad: 0, cleanMatte: true);
            Assert.IsNotNull(s, "premult crop returned null for a present .an");
            Assert.AreEqual(0, Count(s, IsWhiteMatte), "no (near-)transparent WHITE texel may survive");
        }

        [Test]
        public void DigitStrips_PremultFrames_HaveNoWhiteMatteLeft([ValueSource(nameof(Strips))] string an)
        {
            var dir = StatisticDir();
            if (dir == null) Assert.Ignore("結算 STATISTIC art not present in this environment.");

            // ten digits — score_numS.an officially lists 0.png an eleventh time, so accept the extra frame.
            var frames = SdoExtracted.LoadAnPremultiplied(dir, an, pad: 0, cleanMatte: true);
            Assert.GreaterOrEqual(frames.Length, 10, an + " should yield ten digits");
            for (int i = 0; i < frames.Length; i++)
                Assert.AreEqual(0, Count(frames[i], IsWhiteMatte), $"{an} digit {i} still carries white matte");
        }

        [Test]
        public void BareImages_PremultCrop_HaveNoWhiteMatteLeft([ValueSource(nameof(Images))] string image)
        {
            var dir = StatisticDir();
            if (dir == null) Assert.Ignore("結算 STATISTIC art not present in this environment.");

            var s = SdoExtracted.LoadImagePremultiplied(dir, image, pad: 0, cleanMatte: true);
            Assert.IsNotNull(s, image + " failed to load");
            Assert.AreEqual(0, Count(s, IsWhiteMatte), image + " still carries white matte");
        }

        [Test]
        public void PanelArt_OldPath_StillCarriesTheWhiteMatte(
            [Values("Statis25.an", "Statis22.an", "Statis28.an", "percent.an", "100.an")] string an)
        {
            // The regression this guards is real: the bled path leaves the whitish rim in place (alpha untouched, and
            // AlphaBleed only rewrites RGB at a ≤ 8, so everything from a = 9 up stays bright).
            var dir = StatisticDir();
            if (dir == null) Assert.Ignore("結算 STATISTIC art not present in this environment.");

            var old = SdoExtracted.LoadAn1(dir, an, bleed: true);
            Assert.IsNotNull(old);
            Assert.Greater(Count(old, IsWhiteMatte), 0, "expected the baked white rim in the original art");
        }

        [Test]
        public void PanelArt_PremultCrop_KeepsNativeSizeAndEveryVisibleTexel([ValueSource(nameof(SingleFrame))] string an)
        {
            // pad = 0 keeps the sprite exactly the size of the crop: the panel aligns these by their TOP-LEFT, so any
            // padding would shift them off the official spot. And cleanMatte must ONLY eat the sub-48 white: every texel
            // at a ≥ 48 (the artwork and its real AA edge) has to survive untouched.
            var dir = StatisticDir();
            if (dir == null) Assert.Ignore("結算 STATISTIC art not present in this environment.");

            var old = SdoExtracted.LoadAn1(dir, an, bleed: true);
            var s = SdoExtracted.LoadAnSoloPremultiplied(dir, an, pad: 0, cleanMatte: true);
            Assert.IsNotNull(old); Assert.IsNotNull(s);
            Assert.AreEqual(old.rect.width, s.rect.width, "crop width must stay native (pad 0)");
            Assert.AreEqual(old.rect.height, s.rect.height, "crop height must stay native (pad 0)");
            Assert.AreEqual(1f, s.pixelsPerUnit, "panel art displays 1:1 in design space");
            Assert.AreEqual(Count(old, c => c.a >= 48), Count(s, c => c.a >= 48),
                "cleanMatte must not remove a single visible texel — only the sub-48 white");
        }

        [Test]
        public void PremultStrips_KeepEveryFrame_LikeTheOldPath([ValueSource(nameof(Strips))] string an)
        {
            // The premult path must never silently DROP a frame. The old SpriteFromFrame falls back to the whole
            // texture when an .an declares a crop bigger than its PNG; if the premult crop just returned null instead,
            // a strip could come back 9 long — and every caller bails on digits.Length < 10, so the number would
            // vanish — or come back 10 long with every digit shifted by one, drawing wrong figures silently.
            var dir = StatisticDir();
            if (dir == null) Assert.Ignore("結算 STATISTIC art not present in this environment.");

            var old = SdoExtracted.LoadAn(dir, an, bleed: true);
            var premult = SdoExtracted.LoadAnPremultiplied(dir, an, pad: 0, cleanMatte: true);
            Assert.AreEqual(old.Length, premult.Length, an + ": the premult path lost (or gained) a frame");
            for (int i = 0; i < premult.Length; i++)
            {
                Assert.AreEqual(old[i].rect.width, premult[i].rect.width, $"{an} frame {i}: width changed");
                Assert.AreEqual(old[i].rect.height, premult[i].rect.height, $"{an} frame {i}: height changed");
            }
        }

        [Test]
        public void PremultCrops_AreCached_SoReenteringTheScreenDoesNotLeak()
        {
            // ResultScreen.Build runs once PER SONG and pulls ~55 crops. Every crop used to mint a fresh RGBA32 texture
            // that the static premult registries then held alive forever, so each round leaked a batch that
            // Resources.UnloadUnusedAssets could never reclaim. The same request must now hand back the same sprite.
            var dir = StatisticDir();
            if (dir == null) Assert.Ignore("結算 STATISTIC art not present in this environment.");

            var a1 = SdoExtracted.LoadAnSoloPremultiplied(dir, "Statis25.an", pad: 0, cleanMatte: true);
            var a2 = SdoExtracted.LoadAnSoloPremultiplied(dir, "Statis25.an", pad: 0, cleanMatte: true);
            Assert.AreSame(a1, a2, "repeat crop requests must reuse the cached sprite");
            Assert.AreSame(a1.texture, a2.texture, "…and therefore the same texture");

            var i1 = SdoExtracted.LoadImagePremultiplied(dir, "RANK/7.PNG", pad: 0, cleanMatte: true);
            var i2 = SdoExtracted.LoadImagePremultiplied(dir, "RANK/7.PNG", pad: 0, cleanMatte: true);
            Assert.AreSame(i1, i2, "bare-image crops must be cached too");

            // A DIFFERENT request (cleanMatte off) must still get its own crop, not the cleaned one.
            Assert.AreNotSame(a1, SdoExtracted.LoadAnSoloPremultiplied(dir, "Statis25.an", pad: 0, cleanMatte: false),
                "the cache key must include cleanMatte");
        }

        [Test]
        public void EveryPanelSprite_IsOnThePremultPath()
        {
            // Whole-panel sweep: anything ResultScreen loads must come back premultiplied, so a graphic added later
            // cannot quietly fall back to the straight-alpha path and bring the halo with it. The background tiles are
            // the deliberate exception — they are fully opaque 256px slabs with no matte and no edge to bloom.
            var dir = StatisticDir();
            if (dir == null) Assert.Ignore("結算 STATISTIC art not present in this environment.");

            GameObject hudGo = null, root = null;
            try
            {
                hudGo = new GameObject("HudCamProbe");
                var hud = hudGo.AddComponent<Camera>(); hud.enabled = false;
                var result = new ResultScreen();
                result.Build(hud);
                root = (GameObject)Field("_root").GetValue(result);

                foreach (var f in new[] { "_percent", "_dot", "_allCombo", "_overSprite" })
                {
                    var spr = (Sprite)Field(f).GetValue(result);
                    Assert.IsNotNull(spr, f + " not loaded");
                    Assert.IsTrue(SdoExtracted.IsPremultTexture(spr.texture), f + " is not premultiplied");
                }
                foreach (var f in new[] { "_num8", "_num3", "_scoreNum", "_scoreNumS" })
                {
                    var strip = (Sprite[])Field(f).GetValue(result);
                    Assert.IsNotNull(strip); Assert.GreaterOrEqual(strip.Length, 10, f + " should hold ten digits");
                    for (int i = 0; i < strip.Length; i++)
                        Assert.IsTrue(SdoExtracted.IsPremultTexture(strip[i].texture), $"{f}[{i}] is not premultiplied");
                }
                var grades = (Dictionary<string, Sprite>)Field("_gradeSprites").GetValue(result);
                Assert.Greater(grades.Count, 0);
                foreach (var kv in grades)
                    Assert.IsTrue(SdoExtracted.IsPremultTexture(kv.Value.texture), "grade " + kv.Key + " is not premultiplied");

                foreach (var name in new[] { "OkBtn", "SaveBtn", "BannerWinImg", "BannerLoseImg" })
                {
                    var sr = FindChild(root, name);
                    Assert.IsNotNull(sr, name + " not built");
                    Assert.IsTrue(SdoExtracted.IsPremultTexture(sr.sprite.texture), name + " is not premultiplied");
                    Assert.AreEqual("Sdo/SpritePremultiply", sr.sharedMaterial.shader.name,
                        name + " must render with the premultiplied-alpha material");
                }
            }
            finally
            {
                if (root != null) Object.DestroyImmediate(root);
                if (hudGo != null) Object.DestroyImmediate(hudGo);
            }
        }

        [Test]
        public void PremultSpriteRenderers_EachBindTheirOwnTexture()
        {
            // THE regression that shipped in f4245f7 and 使用者 caught immediately: NewSR handed every premultiplied
            // SpriteRenderer the SAME shared material. One material INSTANCE may only serve renderers drawing the SAME
            // texture — share it across textures and they all sample whichever was written last, so the whole panel drew
            // one image (數字位置畫出 YOU WIN 旗、確定鈕變成保存錄像、排名欄畫出 GAME OVER).
            // Structural guard: no two renderers whose sprites differ may share a material, and every material must be
            // bound to exactly the texture its own sprite uses. Build() alone only makes 4 premult renderers, so this
            // populates a row too — the rank badge, digits, %, 成績字 and the rolling totals are all Show()-time.
            var dir = StatisticDir();
            if (dir == null) Assert.Ignore("結算 STATISTIC art not present in this environment.");

            GameObject hudGo = null, root = null;
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
                    new ResultScreen.Row { Rank = 2, Name = "P2", Perfect = 90, Cool = 80, Bad = 7, Miss = 6,
                                           MaxCombo = 70, Accuracy = 100.0, Score = 90210, Grade = "B", FullCombo = true },
                }, localWon: true, expGained: 24, coinsGained: 7);
                // The EXP / G totals only arm once the rows have finished sliding in, and EditMode has no ticking clock,
                // so drive the two RollingDigits directly — otherwise their slots keep a null sprite and the sweep below
                // skips them entirely, which is exactly how the "totals drawn with the default material" defect hid.
                foreach (var f in new[] { "_expTotal", "_gTotal" })
                {
                    var rd = Field(f).GetValue(result);
                    Assert.IsNotNull(rd, f + " not built");
                    var t = rd.GetType();
                    t.GetMethod("SetTarget").Invoke(rd, new object[] { 123456L, 0f });
                    t.GetMethod("Tick").Invoke(rd, new object[] { 999f });   // long past the roll → settles on the target
                }
                root = (GameObject)Field("_root").GetValue(result);

                var byMaterial = new Dictionary<Material, Texture>();
                int checkedRenderers = 0;
                foreach (var sr in root.GetComponentsInChildren<SpriteRenderer>(true))
                {
                    if (sr.sprite == null || !SdoExtracted.IsPremultTexture(sr.sprite.texture)) continue;
                    checkedRenderers++;
                    var mat = sr.sharedMaterial;
                    Assert.IsNotNull(mat, sr.name + " has no material");
                    Assert.AreEqual(sr.sprite.texture, mat.mainTexture,
                        sr.name + ": its material must be bound to its OWN texture, not another sprite's");
                    if (byMaterial.TryGetValue(mat, out var already))
                        Assert.AreEqual(already, sr.sprite.texture,
                            sr.name + " shares a material with a renderer using a DIFFERENT texture — they will both " +
                            "draw the same image");
                    else byMaterial[mat] = sr.sprite.texture;
                }
                // Floor well above what Build() alone yields (4), so the guard can never quietly shrink back to
                // "only the buttons and banners" and miss a Show()-time regression like the RollingDigits one.
                Assert.Greater(checkedRenderers, 20,
                    $"only {checkedRenderers} premultiplied renderers were checked — the populated row is missing");
                Assert.AreNotSame(SdoExtracted.PremultUiMaterial, null);
                foreach (var mat in byMaterial.Keys)
                    Assert.AreNotSame(SdoExtracted.PremultUiMaterial, mat,
                        "SpriteRenderers must NOT use the shared UGUI material — that is exactly the bug");
            }
            finally
            {
                if (root != null) Object.DestroyImmediate(root);
                if (hudGo != null) Object.DestroyImmediate(hudGo);
            }
        }

        private static FieldInfo Field(string name) =>
            typeof(ResultScreen).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);

        private static SpriteRenderer FindChild(GameObject root, string name)
        {
            foreach (var sr in root.GetComponentsInChildren<SpriteRenderer>(true))
                if (sr.gameObject.name == name) return sr;
            return null;
        }
    }
}
