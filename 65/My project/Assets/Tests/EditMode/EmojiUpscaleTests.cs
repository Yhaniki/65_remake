using System.IO;
using NUnit.Framework;
using UnityEngine;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// Guards 使用者要求「這批圖片的解析度都太低有辦法提高3倍嗎 並且要確保遊戲裡面顯示的大小 還是一樣」
    /// (UI/PLAYINGEXP 表情 cut-in). The frames were authored 64×64 and are now shipped as a 192×192 hq3x upscale
    /// (tools/upscale_playingexp.py). The ONLY thing keeping them the same size on screen is
    /// <see cref="SdoExtracted.LoadImageAtDesignWidth"/> setting pixelsPerUnit = tex.width / 64 — a plain
    /// <see cref="SdoExtracted.LoadImage"/> (ppu 1) would pop the cut-in out 3× too big.
    /// </summary>
    public class EmojiUpscaleTests
    {
        /// <summary>The size ScreenGameplay.LoadEmojiSeq pins the cut-ins to (the original art's width).</summary>
        private const int DesignPx = 64;

        private static string _tmp;

        [SetUp]
        public void SetUp() => _tmp = Path.Combine(Path.GetTempPath(), "sdo_emoji_upscale_" + Path.GetRandomFileName());

        [TearDown]
        public void TearDown() { try { if (Directory.Exists(_tmp)) Directory.Delete(_tmp, true); } catch { } }

        /// <summary>Write a solid square PNG of the given size into the temp folder and return its file name.</summary>
        private string WritePng(int size)
        {
            Directory.CreateDirectory(_tmp);
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var px = new Color32[size * size];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(255, 0, 0, 255);
            tex.SetPixels32(px); tex.Apply();
            var name = size + ".png";
            File.WriteAllBytes(Path.Combine(_tmp, name), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            return name;
        }

        // A sprite's world size is rect.size / pixelsPerUnit — that (not the texel count) is what the player sees.
        private static float WorldWidth(Sprite s) => s.rect.width / s.pixelsPerUnit;

        [Test]
        public void DesignWidth_PinsWorldSize_RegardlessOfFileResolution()
        {
            var small = SdoExtracted.LoadImageAtDesignWidth(_tmp, WritePng(DesignPx), DesignPx);
            var big = SdoExtracted.LoadImageAtDesignWidth(_tmp, WritePng(DesignPx * 3), DesignPx);
            Assert.IsNotNull(small); Assert.IsNotNull(big);

            Assert.AreEqual(DesignPx, small.texture.width, "control: the 64px file is 64 texels");
            Assert.AreEqual(DesignPx * 3, big.texture.width, "the upscaled file is 3× the texels");
            Assert.AreEqual(1f, small.pixelsPerUnit, 1e-4f, "a native-size file stays at ppu 1");
            Assert.AreEqual(3f, big.pixelsPerUnit, 1e-4f, "a 3× file must report ppu 3 to cancel the extra texels");
            Assert.AreEqual(WorldWidth(small), WorldWidth(big), 1e-3f, "同樣的顯示大小: 3× art must not change world size");
            Assert.AreEqual(DesignPx, WorldWidth(big), 1e-3f, "world size stays the 64px design size");
        }

        [Test]
        public void PlainLoadImage_WouldTripleTheSize_WhyDesignWidthIsRequired()
        {
            // Pins the regression this whole test file exists for: the old call site (ppu 1) scales with the file.
            var big = SdoExtracted.LoadImage(_tmp, WritePng(DesignPx * 3));
            Assert.AreEqual(DesignPx * 3, WorldWidth(big), 1e-3f, "LoadImage ties world size to the texel count");
        }

        [Test]
        public void Mip_Requested_YieldsMippedTrilinearTexture_SameWorldSize()
        {
            var name = WritePng(DesignPx * 3);
            var plain = SdoExtracted.LoadImageAtDesignWidth(_tmp, name, DesignPx);
            var mipped = SdoExtracted.LoadImageAtDesignWidth(_tmp, name, DesignPx, mip: true);

            Assert.AreEqual(1, plain.texture.mipmapCount, "control: the shared texture has no mip chain");
            Assert.Greater(mipped.texture.mipmapCount, 1, "an upscaled sprite is minified on screen — it needs mips");
            Assert.AreEqual(FilterMode.Trilinear, mipped.texture.filterMode, "must blend across mips, not pop");
            Assert.AreEqual(WorldWidth(plain), WorldWidth(mipped), 1e-3f, "mip must not change the display size");

            // cached: asking twice hands back the SAME texture (gameplay re-loads its art every song start)
            var again = SdoExtracted.LoadImageAtDesignWidth(_tmp, name, DesignPx, mip: true);
            Assert.AreSame(mipped.texture, again.texture, "mipmapped copies must be cached, not rebuilt per load");
        }

        // ── real shipped art ────────────────────────────────────────────────────────────────────────────────────────
        private static string ExpDir()
        {
            foreach (var d in new[] { Path.Combine(SdoExtracted.Root, "UI", "PLAYINGEXP"), @"H:/65_remake_clean/DATA/UI/PLAYINGEXP" })
                if (!string.IsNullOrEmpty(d) && File.Exists(Path.Combine(d, "HH000.PNG"))) return d;
            return null;
        }

        // 🔴 這九段(共 81 張)就是全部的表情 cut-in —— 與 ScreenGameplay.LoadEmojiArt 同一份清單。
        //
        // 為什麼不能直接 Directory.GetFiles(dir, "*.PNG"):UI/PLAYINGEXP 裡不是只有表情。原始的
        // Extracted 樹另外躺著 COMBO*/MISS*/PLAYING* 那 18 張 90×90 —— 那是連段/Miss 的提示圖,
        // 另一個系列、本來就不是 64 的倍數,不歸 hq3x 那次升解析度管。剪枝過的乾淨包把它們拿掉了,
        // 所以 data_root.txt 指著乾淨包時掃整個資料夾剛好會過;一旦 data root 落回主 repo 的原始
        // Extracted 樹(worktree 沒有 data_root.txt 時就是這樣),同一條測試就會拿提示圖來紅。
        private static readonly string[] SeqPrefixes = { "HH", "SHSH", "JRKL", "KJ", "HE", "H", "Y", "JS", "GTH" };
        private static readonly int[] SeqCounts = { 7, 16, 8, 14, 8, 10, 4, 6, 8 };

        [Test]
        public void ShippedFrames_AreSquareMultiplesOfTheDesignSize()
        {
            var dir = ExpDir();
            if (dir == null) Assert.Ignore("UI/PLAYINGEXP art not present in this environment.");

            int checkedFrames = 0;
            for (int s = 0; s < SeqPrefixes.Length; s++)
                for (int i = 0; i < SeqCounts[s]; i++)
                {
                    var name = $"{SeqPrefixes[s]}{i:D3}.PNG";
                    var f = Path.Combine(dir, name);
                    // 缺幀不在這條測試的守備範圍(LoadEmojiSeq 本來就跳過載不到的張數)——
                    // 這裡只管「有出貨的那些張,解析度對不對」。
                    if (!File.Exists(f)) continue;

                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    Assert.IsTrue(tex.LoadImage(File.ReadAllBytes(f)), "decodable PNG: " + f);
                    int w = tex.width, h = tex.height;
                    Object.DestroyImmediate(tex);
                    checkedFrames++;
                    Assert.AreEqual(w, h, "frames are square: " + name);
                    // A non-integer factor would still display right (ppu is a float) but means someone resampled the set
                    // with a different pipeline — worth failing loudly rather than shipping a mixed-resolution sequence.
                    Assert.AreEqual(0, w % DesignPx, $"{name} is {w}px — not a whole multiple of the {DesignPx}px design size");
                }

            // 一張都沒對到 = 目錄長得對但檔名全變了 → 這條測試等於沒跑,要讓它紅而不是靜靜地綠。
            Assert.Greater(checkedFrames, 0, "找到了 PLAYINGEXP 卻一張表情框都沒對到 —— 檔名規則變了?");
        }

        [Test]
        public void ShippedFrames_DisplayAtTheDesignSize()
        {
            var dir = ExpDir();
            if (dir == null) Assert.Ignore("UI/PLAYINGEXP art not present in this environment.");

            foreach (var n in new[] { "HH000.PNG", "SHSH000.PNG", "KJ000.PNG", "GTH000.PNG" })
            {
                var s = SdoExtracted.LoadImageAtDesignWidth(dir, n, DesignPx, bleed: true, mip: true);
                Assert.IsNotNull(s, n);
                Assert.AreEqual(DesignPx, WorldWidth(s), 1e-3f, n + " must display at the 64px design size");
            }
        }
    }
}
