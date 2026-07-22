using System;
using System.IO;
using System.Threading;
using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>商城捲動時「衣服才開始讀」的三個修法各有一組守門測試:
    ///   • <see cref="DdsLoader.Analyze"/> —— 把原本 4 次全圖 alpha 掃描 (HasAlpha / GetSceneAlphaMode /
    ///     HardTransparentFraction / TranslucentFraction) 併成一次。這裡逐一比對「併之後答案完全一樣」,因為衣服的
    ///     去背/紗質判定全靠這幾個數字,一但漂移就是渲染 bug (見 GarmentSheerAlphaTests)。
    ///   • <see cref="AvatarAssetCache"/> —— 檔案位元組快取 + 背景預讀 (冷讀一個檔 7-11ms,重讀 0.16ms)。
    ///   • <see cref="Sdo.UI.Screens.ShopScreen.MapRetainedSlots"/> —— 捲一列只重建捲進來的那幾格。</summary>
    public class AvatarAssetCacheTests
    {
        // 4×4 DXT3 (one block): 16 explicit 4-bit alpha nibbles (texel i = nibbles[i]) + an opaque colour payload.
        private static byte[] MakeDxt3(byte[] nibbles)
        {
            var block = new byte[16];
            for (int k = 0; k < 8; k++)
                block[k] = (byte)((nibbles[2 * k] & 0xF) | ((nibbles[2 * k + 1] & 0xF) << 4));
            var d = new byte[128 + 16];
            d[0] = (byte)'D'; d[1] = (byte)'D'; d[2] = (byte)'S'; d[3] = (byte)' ';
            BitConverter.GetBytes(4).CopyTo(d, 12);
            BitConverter.GetBytes(4).CopyTo(d, 16);
            System.Text.Encoding.ASCII.GetBytes("DXT3").CopyTo(d, 84);
            block.CopyTo(d, 128);
            return d;
        }

        private static byte[] Fill(byte nibble) { var n = new byte[16]; for (int i = 0; i < 16; i++) n[i] = nibble; return n; }

        private static void AssertSameAsSeparateCalls(byte[] dds, string what)
        {
            var st = DdsLoader.Analyze(dds);
            Assert.AreEqual(DdsLoader.HasAlpha(dds), st.HasAlpha, what + ": HasAlpha");
            Assert.AreEqual(DdsLoader.GetSceneAlphaMode(dds), st.Scene, what + ": Scene");
            Assert.AreEqual(DdsLoader.HardTransparentFraction(dds), st.HardTransp, 1e-6f, what + ": HardTransp");
            Assert.AreEqual(DdsLoader.TranslucentFraction(dds), st.Translucent, 1e-6f, what + ": Translucent");
            Assert.AreEqual(st.HasAlpha && DdsLoader.LooksLikeAdditiveGlow(dds), st.AdditiveGlow, what + ": AdditiveGlow");
        }

        [Test]
        public void Analyze_MatchesTheSeparateScans_ForEveryAlphaShape()
        {
            AssertSameAsSeparateCalls(MakeDxt3(Fill(15)), "solid opaque");
            AssertSameAsSeparateCalls(MakeDxt3(Fill(0)), "all holes");
            AssertSameAsSeparateCalls(MakeDxt3(Fill(8)), "all sheer midtones");
            var bimodal = new byte[16];
            for (int i = 0; i < 8; i++) bimodal[i] = 15;
            AssertSameAsSeparateCalls(MakeDxt3(bimodal), "solid body + hard holes");
            var oneHole = Fill(15); oneHole[0] = 0;
            AssertSameAsSeparateCalls(MakeDxt3(oneHole), "solid with a single punched hole");
            var lace = Fill(15); for (int i = 0; i < 5; i++) lace[i] = 7; lace[15] = 0;
            AssertSameAsSeparateCalls(MakeDxt3(lace), "lace mix");
        }

        [Test]
        public void Analyze_AlphaLessOrJunk_IsOpaqueDefaults()
        {
            var st = DdsLoader.Analyze(new byte[] { 1, 2, 3 });
            Assert.IsFalse(st.HasAlpha);
            Assert.AreEqual(DdsAlphaMode.Opaque, st.Scene);
            Assert.AreEqual(0f, st.HardTransp);
            Assert.AreEqual(0f, st.Translucent);
            Assert.IsFalse(st.AdditiveGlow);
        }

        // ---- byte cache -------------------------------------------------------------------------------------------

        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "sdo_assetcache_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            AvatarAssetCache.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            AvatarAssetCache.Clear();
            AvatarAssetCache.BytesCapacity = 160L << 20;
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string WriteFile(string name, int size)
        {
            var p = Path.Combine(_dir, name);
            File.WriteAllBytes(p, new byte[size]);
            return p;
        }

        [Test]
        public void Read_SecondTime_ComesFromRamNotDisk()
        {
            var p = WriteFile("a.msh", 1024);
            var first = AvatarAssetCache.Read(p);
            File.Delete(p);                                   // 檔案不在了,還讀得到 = 真的是 RAM 命中
            var second = AvatarAssetCache.Read(p);
            Assert.AreSame(first, second);
            Assert.IsTrue(AvatarAssetCache.IsCached(p));
        }

        [Test]
        public void Read_MissingFile_IsNullAndNotCached()
        {
            var p = Path.Combine(_dir, "nope.msh");
            Assert.IsNull(AvatarAssetCache.Read(p));
            Assert.IsFalse(AvatarAssetCache.IsCached(p));
        }

        [Test]
        public void Read_EvictsLeastRecentlyUsed_WhenOverCapacity()
        {
            AvatarAssetCache.BytesCapacity = 3000;
            var a = WriteFile("a", 1000); var b = WriteFile("b", 1000); var c = WriteFile("c", 1000);
            AvatarAssetCache.Read(a); AvatarAssetCache.Read(b); AvatarAssetCache.Read(c);
            AvatarAssetCache.Read(a);                          // a 變成最近使用 → 該被丟的是 b
            var d = WriteFile("d", 1000);
            AvatarAssetCache.Read(d);
            Assert.IsFalse(AvatarAssetCache.IsCached(b), "least-recently-used entry should be evicted");
            Assert.IsTrue(AvatarAssetCache.IsCached(a));
            Assert.IsTrue(AvatarAssetCache.IsCached(c));
            Assert.IsTrue(AvatarAssetCache.IsCached(d));
            AvatarAssetCache.Stats(out _, out _, out long used);
            Assert.LessOrEqual(used, AvatarAssetCache.BytesCapacity);
        }

        [Test]
        public void PrefetchFile_LoadsInTheBackground()
        {
            var p = WriteFile("ahead.dds", 2048);
            AvatarAssetCache.PrefetchFile(p);
            for (int i = 0; i < 200 && !AvatarAssetCache.IsCached(p); i++) Thread.Sleep(10);   // ≤2s
            Assert.IsTrue(AvatarAssetCache.IsCached(p), "prefetch worker should have pulled the file into RAM");
        }

        [Test]
        public void Trim_ShrinksToTheTarget_KeepingTheMostRecent()
        {
            var a = WriteFile("a", 1000); var b = WriteFile("b", 1000); var c = WriteFile("c", 1000);
            AvatarAssetCache.Read(a); AvatarAssetCache.Read(b); AvatarAssetCache.Read(c);
            AvatarAssetCache.Trim(1500);                       // 關商城 → 縮回小快取
            AvatarAssetCache.Stats(out _, out _, out long used);
            Assert.LessOrEqual(used, 1500);
            Assert.IsTrue(AvatarAssetCache.IsCached(c), "most-recently-read entry survives the trim");
            Assert.IsFalse(AvatarAssetCache.IsCached(a));
        }

        [Test]
        public void CancelPending_DropsTheQueue()
        {
            for (int i = 0; i < 50; i++) AvatarAssetCache.PrefetchFile(Path.Combine(_dir, "x" + i));
            AvatarAssetCache.CancelPending();
            Assert.AreEqual(0, AvatarAssetCache.PendingJobs);
        }
    }
}
