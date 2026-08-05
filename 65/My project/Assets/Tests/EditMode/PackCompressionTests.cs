using System;
using System.IO;
using NUnit.Framework;
using Sdo.Osu;

namespace Sdo.Tests
{
    /// <summary>
    /// 傳檔用的逐檔壓縮(<see cref="PackCompression"/>)。
    ///
    /// 這一段是**client 與 server 共編的同一份**:送端照它壓、收端照它解,兩邊對「壓縮後的位元組」
    /// 只要有一點認知不同,症狀就是每個檔從錯的位移開始,而錯誤訊息只會說「內容與 sha256 不符」。
    /// 純檔案操作,不碰網路。
    /// </summary>
    public class PackCompressionTests
    {
        private string _tmp;

        [SetUp]
        public void SetUp()
        {
            _tmp = Path.Combine(Path.GetTempPath(), "sdo_zt_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tmp);
        }

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_tmp)) Directory.Delete(_tmp, true); } catch { }
        }

        private string Write(string name, byte[] data)
        {
            var p = Path.Combine(_tmp, name);
            File.WriteAllBytes(p, data);
            return p;
        }

        /// <summary>壓了再解要一個位元組不差 —— 這是整條傳輸鏈的地基。</summary>
        [Test]
        public void RoundTrip_ReproducesTheBytesExactly()
        {
            // 刻意混一段高度重複(壓得動)與一段亂數(壓不動)的資料。
            var data = new byte[300 * 1024];
            for (int i = 0; i < 200 * 1024; i++) data[i] = (byte)(i % 7);
            var rng = new Random(12345);
            for (int i = 200 * 1024; i < data.Length; i++) data[i] = (byte)rng.Next(256);

            var src = Write("src.bin", data);
            var z = Path.Combine(_tmp, "src.bin.z");
            var back = Path.Combine(_tmp, "back.bin");

            long clen = PackCompression.CompressFile(src, z);
            Assert.Greater(clen, 0, "壓縮應該成功");
            Assert.AreEqual(clen, new FileInfo(z).Length, "回傳的長度要等於壓縮檔的實際長度 —— 收端就是照它切的");

            Assert.IsTrue(PackCompression.DecompressFile(z, back));
            CollectionAssert.AreEqual(data, File.ReadAllBytes(back), "解出來要與原始位元組完全相同");
        }

        /// <summary>
        /// 未壓縮的貼圖正是這個功能存在的理由 —— 實測 .tga 壓到 5-11%。
        /// 這裡用一張「4096² RGBA、大片同色」的假貼圖,壓縮率至少要有個樣子。
        /// </summary>
        [Test]
        public void UncompressedTextureShrinksALot()
        {
            var data = new byte[4 * 1024 * 1024];
            for (int i = 0; i < data.Length; i += 4)
            {
                data[i] = 200; data[i + 1] = 180; data[i + 2] = 160; data[i + 3] = 255;
            }
            var src = Write("tex.tga", data);
            var z = Path.Combine(_tmp, "tex.z");

            long clen = PackCompression.CompressFile(src, z);
            Assert.Greater(clen, 0);
            Assert.Less(clen, data.Length / 10, "大片同色的未壓縮貼圖應該壓到一成以下");
        }

        /// <summary>
        /// 🔴 壓縮**不能改變任何東西的身分**。sha256 與 packId 一律算原始內容,
        /// 所以開了壓縮之後既有的包不用重算、線上已經流通的 packId 也不會失效。
        /// </summary>
        [Test]
        public void CompressedLength_DoesNotAffectThePackId()
        {
            var a = new PackFileEntry("miku.pmx", 100, new string('a', 64));
            var b = new PackFileEntry("miku.pmx", 100, new string('a', 64), 33);

            Assert.AreEqual(0, a.CompressedLength);
            Assert.AreEqual(33, b.CompressedLength);
            Assert.IsFalse(a.IsCompressed);
            Assert.IsTrue(b.IsCompressed);

            var one = new System.Collections.Generic.List<PackFileEntry> { a };
            var two = new System.Collections.Generic.List<PackFileEntry> { b };
            Assert.AreEqual(ModelPackId.Compute(one), ModelPackId.Compute(two),
                "clen 進了 packId 的話,壓縮率一變、每個既有的包就換一個 id");
            Assert.AreEqual(SongPackId.Compute(one), SongPackId.Compute(two));
        }

        /// <summary>線路長度:壓得動就是壓縮後的,壓不動(或還沒壓)就是原始長度。</summary>
        [Test]
        public void WireLength_IsWhatActuallyTravels()
        {
            Assert.AreEqual(100, new PackFileEntry("a.png", 100, "x").WireLength);
            Assert.AreEqual(33, new PackFileEntry("a.tga", 100, "x", 33).WireLength);
            // 空檔案一塊 chunk 都不會傳 —— 兩端都有對稱的跳過邏輯,所以線路長度是 0。
            Assert.AreEqual(0, new PackFileEntry("empty.ini", 0, "x").WireLength);
        }

        /// <summary>壞掉的壓縮流要**失敗**,而且不留下半個目的檔(半個檔會讓 sha256 指向錯的原因)。</summary>
        [Test]
        public void CorruptStream_FailsCleanly()
        {
            var z = Write("bad.z", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            var outPath = Path.Combine(_tmp, "out.bin");

            Assert.IsFalse(PackCompression.DecompressFile(z, outPath), "壞掉的壓縮流不該被當成成功");
            Assert.IsFalse(File.Exists(outPath), "失敗時不可以留下半個檔");
        }

        [Test]
        public void MissingInput_ReturnsFailure_NotAnException()
        {
            var missing = Path.Combine(_tmp, "nope.bin");
            Assert.AreEqual(-1, PackCompression.CompressFile(missing, Path.Combine(_tmp, "x.z")));
            Assert.IsFalse(PackCompression.DecompressFile(missing, Path.Combine(_tmp, "x.bin")));
        }
    }
}
