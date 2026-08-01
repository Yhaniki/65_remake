using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using Sdo.Game;
using Sdo.UI.Util;

namespace Sdo.Tests
{
    /// <summary>Guards the fix for 使用者回報「選歌畫面 模式/隊形/旁觀 的向上按鈕還是有白邊」 and its rollout to the rest of
    /// ROOMDLG + the room's 掉落方式 ▼. Same root cause and treatment as the 結算面板 (see <see cref="ResultPanelMatteTests"/>):
    /// MUSICSELDLG.PNG is a white-背景去背 atlas, so nearly every crop carries a ring of (255,255,255, α≈5~30) matte — the ▲
    /// (MusicSelDlg196.an, crop 868,4,25,24) has 46 such texels right around its rounded corners. Drawn through the old
    /// shared-atlas straight-alpha path and then MAGNIFIED from the 800×600 design to the real window, bilinear
    /// interpolates colour and coverage separately → the white RGB bleeds outward as a halo tracing the CROP rectangle,
    /// which is why the fringe reads square around a round-cornered button.
    /// <see cref="RoomDlgArt.An"/> now crops onto its own texture, clears the sub-48 white and premultiplies.</summary>
    public class SongSelectComboArrowMatteTests
    {
        private const string Arrow = "MusicSelDlg196.an";

        // Every ROOMDLG .an SongSelectScreen actually draws through the premult path (RoomDlgArt.An/AnFrames).
        // MusicSelDlg106 (唱片圓盤 = Mask graphic) and Scene*.an (縮圖被拉伸) deliberately stay on AnRaw — see AnRaw's doc.
        private static readonly string[] PremultArt =
        {
            Arrow, "lbl_notes.an", "new.an", "hot.an", "recommend.an", "classical.an",
            "MusicSelDlg95.an", "MusicSelDlg73.an",                                    // 列表列 normal / selected
            "MusicSelDlg96.an", "MusicSelDlg97.an", "MusicSelDlg98.an",                // ◀ 三態
            "MusicSelDlg99.an", "MusicSelDlg100.an", "MusicSelDlg101.an",              // ▶ 三態
            "MusicSelDlg70.an", "MusicSelDlg71.an", "MusicSelDlg72.an",                // 關閉鈕三態
            "MusicSelDlg64.an", "MusicSelDlg67.an",                                    // 確定 / 取消
            "MusicSelDlg124.an", "MusicSelDlg127.an",                                  // 加入/刪除收藏
            "MusicSelDlg15.an", "MusicSelDlg17.an",                                    // 難度鈕
            "MusicSelDlg18.an", "MusicSelDlg20.an", "MusicSelDlg21.an", "MusicSelDlg23.an",
            "MusicSelDlg2.an", "MusicSelDlg5.an", "MusicSelDlg6.an", "MusicSelDlg7.an", "MusicSelDlg8.an",  // 對話框 9-grid
            "ShopDlg16.an", "ShopDlg17.an", "ShopDlg18.an", "ShopDlg19.an",            // 綠色下拉列
        };

        // a texel that is (near-)transparent yet still bright white — what blooms into the 白邊 under magnification.
        private static bool IsWhiteMatte(Color32 c) => c.a < 48 && c.r > 170 && c.g > 170 && c.b > 170;

        private static string RoomDlgDir()
        {
            foreach (var d in new[] { RoomDlgArt.Dir, @"H:/65_remake_clean/DATA/UI/ROOMDLG" })
                if (!string.IsNullOrEmpty(d) && File.Exists(Path.Combine(d, Arrow))) return d;
            return null;
        }

        private static void RequireArt()
        {
            var dir = RoomDlgDir();
            if (dir == null) Assert.Ignore("ROOMDLG art not present in this environment.");
            RoomDlgArt.Dir = dir;   // also clears the sprite caches, so each test re-crops from disk
        }

        private static void RequireShader()
        {
            if (SdoExtracted.PremultUiMaterial == null)
                Assert.Ignore("Sdo/SpritePremultiply shader unavailable — RoomDlgArt.An falls back to the atlas path.");
        }

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

        // ---------------------------------------------------------------- the defect is real

        [Test]
        public void Arrow_RawAtlasPath_StillCarriesTheWhiteMatte()
        {
            // Control group. AnRaw is the pre-fix behaviour: the crop straight out of the shared atlas with no treatment
            // at all (LoadAn1 defaults bleed:false), so every matte texel is still sitting there. Without this the test
            // below could pass for the wrong reason (e.g. if the art itself were ever cleaned upstream).
            RequireArt();
            var old = RoomDlgArt.AnRaw(Arrow);
            Assert.IsNotNull(old, Arrow + " failed to load");
            Assert.Greater(Count(old, IsWhiteMatte), 0, "expected the baked white matte in the original crop");
        }

        // ---------------------------------------------------------------- the fix, across the whole rollout

        [Test]
        public void RoomDlgArt_PremultCrop_HasNoWhiteMatteLeft([ValueSource(nameof(PremultArt))] string an)
        {
            RequireArt(); RequireShader();

            var s = RoomDlgArt.An(an);
            Assert.IsNotNull(s, an + ": premult crop returned null for a present .an");
            Assert.IsTrue(SdoExtracted.IsPremultTexture(s.texture), an + ": An() must hand back a premultiplied texture");
            Assert.AreEqual(0, Count(s, IsWhiteMatte), an + ": no (near-)transparent WHITE texel may survive");
        }

        [Test]
        public void RoomDlgArt_PremultCrop_KeepsNativeSizeAndEveryVisibleTexel([ValueSource(nameof(PremultArt))] string an)
        {
            // THE risk of rolling cleanMatte across a whole folder: some ROOMDLG art is almost entirely low-α white
            // (MusicSelDlg95 is 95% matte, MUSICTYPE/EMPTY are 100%). Clearing "the white" must only ever remove texels
            // that were already invisible — every texel at α ≥ 48 has to survive, or a sprite silently blanks out.
            // pad = 0 likewise keeps the crop's native size: callers position by sprite.rect, so padding would shift art.
            RequireArt(); RequireShader();

            var old = RoomDlgArt.AnRaw(an);
            var s = RoomDlgArt.An(an);
            Assert.IsNotNull(old); Assert.IsNotNull(s);
            Assert.AreEqual(old.rect.width, s.rect.width, an + ": crop width must stay native (pad 0)");
            Assert.AreEqual(old.rect.height, s.rect.height, an + ": crop height must stay native (pad 0)");
            Assert.AreEqual(1f, s.pixelsPerUnit, an + ": dialog art displays 1:1 in design space");
            Assert.AreEqual(Count(old, c => c.a >= 48), Count(s, c => c.a >= 48),
                an + ": cleanMatte must not remove a single visible texel — only the sub-48 white");
        }

        [Test]
        public void ModeNameFrames_AreAllPremultiplied()
        {
            // LABEL_SDO.an (13 mode-name slices) renders in the combo's value slot right next to the ▲, so it goes
            // through the same treatment — and every frame must survive (a dropped frame would blank a mode's name).
            RequireArt(); RequireShader();

            var raw = SdoExtracted.LoadAn(RoomDlgArt.Dir, "LABEL_SDO.an");
            var frames = RoomDlgArt.AnFrames("LABEL_SDO.an");
            Assert.AreEqual(raw.Length, frames.Length, "the premult path lost (or gained) a frame");
            Assert.GreaterOrEqual(frames.Length, 6, "song-select reads frames 0/1/5");
            for (int i = 0; i < frames.Length; i++)
            {
                Assert.IsTrue(SdoExtracted.IsPremultTexture(frames[i].texture), $"LABEL_SDO frame {i} is not premultiplied");
                Assert.AreEqual(0, Count(frames[i], IsWhiteMatte), $"LABEL_SDO frame {i} still carries white matte");
                Assert.AreEqual(raw[i].rect.width, frames[i].rect.width, $"LABEL_SDO frame {i}: width changed");
                Assert.AreEqual(raw[i].rect.height, frames[i].rect.height, $"LABEL_SDO frame {i}: height changed");
            }
        }

        // ---------------------------------------------------------------- the two deliberate exceptions

        [Test]
        public void MaskGraphicAndStretchedThumbs_StayOnTheRawPath()
        {
            // Sdo/SpritePremultiply has no Stencil block and no _ClipRect, so a premultiplied sprite is NOT clipped by a
            // UI Mask — the vinyl disc (MusicSelDlg106) IS the Mask's showMaskGraphic. And the scene thumbnails are
            // stretched into a 205×90 frame, which the ApplySprite call needed to attach a premult material would undo.
            // Both must therefore come back straight-alpha, on the shared atlas.
            RequireArt(); RequireShader();

            foreach (var an in new[] { "MusicSelDlg106.an", "Scene1.an" })
            {
                var s = RoomDlgArt.AnRaw(an);
                if (s == null) continue;                       // Scene1 may be absent in a trimmed pack
                Assert.IsFalse(SdoExtracted.IsPremultTexture(s.texture),
                    an + " must stay straight-alpha (Mask graphic / stretched thumbnail)");
            }
        }

        [Test]
        public void Crops_AreCached_AndTheTwoPathsDoNotShareAnEntry()
        {
            RequireArt();
            Assert.AreSame(RoomDlgArt.An(Arrow), RoomDlgArt.An(Arrow), "repeat requests must reuse the cached sprite");
            Assert.AreSame(RoomDlgArt.AnRaw(Arrow), RoomDlgArt.AnRaw(Arrow), "raw crops must be cached too");
            RequireShader();
            Assert.AreNotSame(RoomDlgArt.An(Arrow), RoomDlgArt.AnRaw(Arrow),
                "An() and AnRaw() must not collide in the cache — one is premultiplied, the other is not");
        }

        // ---------------------------------------------------------------- the material actually gets attached

        [Test]
        public void ComboBoxArrowAndValue_RenderWithThePremultMaterial()
        {
            // The material pairing only happens inside UIKit.ApplySprite; assigning Image.sprite directly (what
            // SdoComboBox used to do) leaves a premultiplied texture drawing through the default straight-alpha
            // material — which brings the halo straight back AND washes the art out.
            RequireArt(); RequireShader();

            GameObject canvasGo = null;
            try
            {
                canvasGo = new GameObject("ComboProbe", typeof(RectTransform), typeof(Canvas));
                var root = (RectTransform)canvasGo.transform;

                var arrow = RoomDlgArt.An(Arrow);
                var modes = RoomDlgArt.AnFrames("LABEL_SDO.an");
                var value = modes.Length > 0 ? modes[0] : arrow;
                SdoComboBox.Create(root, "probeCombo", 289, 488, 258, 22, 522, arrow, null, null,
                    new[] { "a", "b" }, new[] { value, value }, 0, Color.white, Color.white, _ => { });

                var img = FindChild(canvasGo, "probeCombo_arr");
                Assert.IsNotNull(img, "the ▲ arrow image was not built");
                Assert.AreSame(arrow, img.sprite, "the arrow must keep the premult sprite");
                Assert.AreEqual("Sdo/SpritePremultiply", img.material.shader.name,
                    "the ▲ must render with the premultiplied-alpha material");
                Assert.AreEqual(new Vector2(arrow.rect.width, arrow.rect.height), img.rectTransform.sizeDelta,
                    "the arrow must stay at its native crop size — otherwise all three buttons shift");

                var val = FindChild(canvasGo, "probeCombo_val");
                Assert.IsNotNull(val, "the collapsed value image was not built");
                Assert.AreEqual("Sdo/SpritePremultiply", val.material.shader.name,
                    "the 模式名 value slice must render with the premultiplied-alpha material too");
            }
            finally { if (canvasGo != null) Object.DestroyImmediate(canvasGo); }
        }

        [Test]
        public void RoomDropdownArrow_IsPremultiplied_AndItsListRowsAreNot()
        {
            // 房間「掉落方式」的 ▼ (ShopDlg13, ROOM folder) showed the same square fringe: AnSolo's AlphaBleed only
            // rewrites RGB at α ≤ 8 and DeMatteWhite un-composites white over white, so neither touches its α≈5~30 matte.
            // Its green list rows (LabUnCheck/LabCheck) measured clean, so they stay on AnSolo — they are a DIFFERENT
            // Image with its own material, so mixing the two paths in one combo box is safe.
            var dir = RoomUiArt.Dir;
            if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "ShopDlg13.an")))
                Assert.Ignore("ROOM art not present in this environment.");
            RequireShader();

            var arrow = RoomUiArt.AnPremult("ShopDlg13");
            Assert.IsNotNull(arrow, "ShopDlg13 failed to load");
            Assert.IsTrue(SdoExtracted.IsPremultTexture(arrow.texture), "the room ▼ must be premultiplied");
            Assert.AreEqual(0, Count(arrow, IsWhiteMatte), "the room ▼ still carries white matte");

            var solo = RoomUiArt.AnSolo("ShopDlg13");
            Assert.AreEqual(solo.rect.width, arrow.rect.width, "▼ width must stay native (pad 0) — otherwise it shifts");
            Assert.AreEqual(solo.rect.height, arrow.rect.height, "▼ height must stay native (pad 0)");
            Assert.AreEqual(Count(solo, c => c.a >= 48), Count(arrow, c => c.a >= 48),
                "cleanMatte must not remove a visible texel from the ▼");

            foreach (var row in new[] { "LabUnCheck", "LabCheck" })
            {
                var s = RoomUiArt.AnSolo(row);
                if (s == null) continue;
                Assert.IsFalse(SdoExtracted.IsPremultTexture(s.texture), row + " should stay on the solo path");
            }
        }

        // ---------------------------------------------------------------- folder-wide sanity

        [Test]
        public void NoRoomDlgSprite_IsBlankedByCleanMatte()
        {
            // Folder sweep behind the per-file test above: run EVERY ROOMDLG .an through the premult path and assert
            // none of them loses visible content. This is the guard against a future .an (or a re-extracted data pack)
            // whose art is mostly low-α white being silently cleared to nothing.
            RequireArt(); RequireShader();

            var dir = RoomDlgArt.Dir;
            var blanked = new List<string>();
            foreach (var path in Directory.GetFiles(dir, "*.an"))
            {
                string an = Path.GetFileName(path);
                var raw = RoomDlgArt.AnRaw(an);
                if (raw == null) continue;
                var s = RoomDlgArt.An(an);
                if (s == null || !SdoExtracted.IsPremultTexture(s.texture)) continue;   // fell back — nothing to check
                if (Count(raw, c => c.a >= 48) != Count(s, c => c.a >= 48)) blanked.Add(an);
            }
            Assert.IsEmpty(blanked, "cleanMatte removed visible texels from: " + string.Join(", ", blanked));
        }

        private static Image FindChild(GameObject root, string name)
        {
            foreach (var img in root.GetComponentsInChildren<Image>(true))
                if (img.gameObject.name == name) return img;
            return null;
        }
    }
}
