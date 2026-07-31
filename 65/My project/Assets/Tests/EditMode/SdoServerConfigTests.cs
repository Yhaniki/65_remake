using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Sdo.Osu;

namespace Sdo.Tests.EditMode
{
    /// <summary>
    /// serverconfig（歌單順序 + NEW/HOT/推薦/古典 標籤）的解析。格式與逆向依據見
    /// docs/reverse-engineering/SDO_SERVERCONFIG.md。
    /// </summary>
    public class SdoServerConfigTests
    {
        // ---- 造一份最小但版面完全正確的 serverconfig ----
        private static byte[] Build(params (int id, int newf, int hot, int rec, int hidden, int classical)[] rows)
        {
            var b = new List<byte>();
            void U32(long v) { b.Add((byte)(v & 0xFF)); b.Add((byte)((v >> 8) & 0xFF)); b.Add((byte)((v >> 16) & 0xFF)); b.Add((byte)((v >> 24) & 0xFF)); }

            U32(0);                                   // size 欄（客戶端不驗，解析也不看）
            b.AddRange(SdoServerConfig.Magic);        // "ServerConfig0073"
            U32(0x003333ad);                          // 版本/時戳
            for (int i = 0; i < 8; i++) U32(0);       // 8 張 u32 陣列，這裡都給 0 筆
            for (int i = 0; i < 40; i++) b.Add(0);    // 固定 5 組 × 4×u16 旗標
            U32(rows.Length);                         // 表 0：SDO 模式歌曲表
            foreach (var r in rows)
            {
                U32(r.id);
                b.Add((byte)r.newf); b.Add((byte)r.hot); b.Add((byte)r.rec);
                b.Add((byte)r.hidden); b.Add((byte)r.classical);
                b.Add(0xCC); b.Add(0xCC); b.Add(0xCC);   // 官方檔就是這三個填充值
            }
            U32(0); U32(0);                           // 表 1 / 表 2（AU / 第三模式）
            return b.ToArray();
        }

        [Test]
        public void Parse_ReadsIdOrderAndBadges()
        {
            var rows = SdoServerConfig.Parse(Build(
                (1, 0, 0, 0, 0, 0),
                (40, 0, 0, 1, 0, 0),
                (2818, 1, 0, 0, 0, 0),
                (5046, 0, 1, 0, 0, 0),
                (82, 0, 0, 0, 0, 1),
                (39, 0, 0, 0, 1, 0)));

            Assert.AreEqual(6, rows.Count);
            Assert.AreEqual(new[] { 1, 40, 2818, 5046, 82, 39 }, rows.ConvertAll(r => r.SongId).ToArray());
            Assert.AreEqual(new[] { 0, 1, 2, 3, 4, 5 }, rows.ConvertAll(r => r.Order).ToArray());
            Assert.AreEqual(SongBadge.None, rows[0].Badge);
            Assert.AreEqual(SongBadge.Recommend, rows[1].Badge);
            Assert.AreEqual(SongBadge.New, rows[2].Badge);
            Assert.AreEqual(SongBadge.Hot, rows[3].Badge);
            Assert.AreEqual(SongBadge.Classical, rows[4].Badge);
            Assert.IsTrue(rows[5].Hidden);
            Assert.IsFalse(rows[0].Hidden);
        }

        [Test]
        public void Parse_BadgePriorityIsNewHotRecommendClassical()
        {
            // 官方畫列時是 if/else if 串接，一列最多一個標籤 → 全部旗標都開時只會出 NEW。
            var all = SdoServerConfig.Parse(Build((7, 1, 1, 1, 0, 1)));
            Assert.AreEqual(SongBadge.New, all[0].Badge);

            Assert.AreEqual(SongBadge.Hot, SdoServerConfig.Parse(Build((7, 0, 1, 1, 0, 1)))[0].Badge);
            Assert.AreEqual(SongBadge.Recommend, SdoServerConfig.Parse(Build((7, 0, 0, 1, 0, 1)))[0].Badge);
            Assert.AreEqual(SongBadge.Classical, SdoServerConfig.Parse(Build((7, 0, 0, 0, 0, 1)))[0].Badge);
        }

        [Test]
        public void Parse_SkipsTheEightLeadingIdArrays()
        {
            // 那 8 張 u32 陣列不是空的時候，表 0 的位置會往後推 —— 版面走錯就會讀到垃圾。
            var b = new List<byte>();
            void U32(long v) { b.Add((byte)(v & 0xFF)); b.Add((byte)((v >> 8) & 0xFF)); b.Add((byte)((v >> 16) & 0xFF)); b.Add((byte)((v >> 24) & 0xFF)); }
            U32(0);
            b.AddRange(SdoServerConfig.Magic);
            U32(0);
            for (int i = 0; i < 8; i++) { U32(3); U32(11); U32(22); U32(33); }   // 每張 3 筆
            for (int i = 0; i < 40; i++) b.Add(0);
            U32(1); U32(1234); b.Add(0); b.Add(1); b.Add(0); b.Add(0); b.Add(0); b.Add(0xCC); b.Add(0xCC); b.Add(0xCC);
            U32(0); U32(0);

            var rows = SdoServerConfig.Parse(b.ToArray());
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual(1234, rows[0].SongId);
            Assert.AreEqual(SongBadge.Hot, rows[0].Badge);
        }

        [Test]
        public void Plain_UnwrapsNxObfuscation()
        {
            var plain = Build((40, 1, 0, 0, 0, 0));
            foreach (var seed in SdoServerConfig.NxSeeds)
            {
                var wrapped = SdoServerConfig.Deobfuscate(plain, seed);       // 對稱：同一式即加密
                CollectionAssert.AreNotEqual(plain, wrapped);
                CollectionAssert.AreEqual(plain, SdoServerConfig.Plain(wrapped));
                var rows = SdoServerConfig.Parse(wrapped);
                Assert.AreEqual(1, rows.Count);
                Assert.AreEqual(SongBadge.New, rows[0].Badge);
            }
        }

        [Test]
        public void KeyAt_MatchesTheRealConfig2Header()
        {
            // [NX]Patch 3.0 的 patch Datas\config2 前 8 bytes（實檔），明碼是 size=0x9914 + "Serv"。
            byte[] cipher = { 0x4F, 0xCA, 0x4B, 0x43, 0x28, 0x16, 0x19, 0x15 };
            byte[] expect = { 0x14, 0x99, 0x00, 0x00, (byte)'S', (byte)'e', (byte)'r', (byte)'v' };
            for (int i = 0; i < cipher.Length; i++)
                Assert.AreEqual(expect[i], (byte)(cipher[i] ^ SdoServerConfig.KeyAt(i, 0xC3)), "byte " + i);
        }

        [Test]
        public void Parse_GarbageAndTruncationReturnEmpty()
        {
            Assert.AreEqual(0, SdoServerConfig.Parse(null).Count);
            Assert.AreEqual(0, SdoServerConfig.Parse(new byte[0]).Count);
            Assert.AreEqual(0, SdoServerConfig.Parse(new byte[] { 1, 2, 3, 4, 5 }).Count);

            var good = Build((40, 1, 0, 0, 0, 0));
            for (int cut = 1; cut < 24; cut++)
            {
                var chopped = new byte[good.Length - cut];
                System.Array.Copy(good, chopped, chopped.Length);
                Assert.DoesNotThrow(() => SdoServerConfig.Parse(chopped));   // 截斷只能回空表，不能炸
            }
        }

        [Test]
        public void SongIdOf_StripsTheTenThousands()
        {
            Assert.AreEqual(40, SdoServerConfig.SongIdOf(10040));
            Assert.AreEqual(2818, SdoServerConfig.SongIdOf(12818));
            Assert.AreEqual(40, SdoServerConfig.SongIdOf(40));      // 已經是 4 位數就原樣
            Assert.AreEqual(0, SdoServerConfig.SongIdOf(0));
            Assert.AreEqual(0, SdoServerConfig.SongIdOf(-5));
        }

        [Test]
        public void ConfigCandidates_LooksInThePackAndItsSiblings()
        {
            var dir = Path.Combine(Path.Combine("C:", "packs", "NX30"), "patch music");
            var got = SdoServerConfig.ConfigCandidates(dir);

            Assert.Contains(Path.Combine(dir, "ServerConfigND.dat"), got);
            var patchDatas = Path.Combine(Path.Combine("C:", "packs", "NX30"), "patch Datas");
            Assert.Contains(Path.Combine(patchDatas, "config2"), got);          // [NX]Patch 實際擺法
            Assert.Contains(Path.Combine(Path.Combine("C:", "packs", "NX30"), "serverconfig.dat"), got);

            // 歌資料夾本身優先於隔壁資料夾
            Assert.Less(got.IndexOf(Path.Combine(dir, "config2")), got.IndexOf(Path.Combine(patchDatas, "config2")));
            Assert.AreEqual(0, SdoServerConfig.ConfigCandidates(null).Count);
            Assert.AreEqual(0, SdoServerConfig.ConfigCandidates("").Count);
        }

        [Test]
        public void ById_KeepsTheFirstRowForARepeatedId()
        {
            var map = SdoServerConfig.ById(SdoServerConfig.Parse(Build(
                (40, 1, 0, 0, 0, 0),
                (40, 0, 1, 0, 0, 0))));
            Assert.AreEqual(1, map.Count);
            Assert.AreEqual(SongBadge.New, map[40].Badge);
            Assert.AreEqual(0, map[40].Order);
        }
    }
}
