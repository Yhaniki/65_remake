using NUnit.Framework;
using Sdo.Settings;

namespace Sdo.Tests
{
    /// <summary>
    /// 單首歌的 offset（StepMania 的 song offset）。.gn 是唯讀又加密的，改不進去 → 存在旁邊的 song_offsets.ini。
    /// 這裡驗記憶體中的表與序列化（不碰檔案）。
    /// </summary>
    public class SongOffsetsTests
    {
        [SetUp]
        public void Reset() => SongOffsets.ResetForTests();

        [Test]
        public void Unset_IsZero()
        {
            Assert.AreEqual(0f, SongOffsets.Get("sdom1197k.gn"), 1e-6f);
            Assert.AreEqual(0f, SongOffsets.Get(null), 1e-6f);
        }

        [Test]
        public void Key_IsTheGnFileName_CaseInsensitive_AndPathStripped()
        {
            SongOffsets.SetInMemory("sdom1197K.gn", 25f);
            Assert.AreEqual(25f, SongOffsets.Get("SDOM1197k.GN"), 1e-6f);
            Assert.AreEqual(25f, SongOffsets.Get(@"H:\whatever\MUSIC\sdom1197k.gn"), 1e-6f);   // 傳整條路徑也要認得
        }

        [Test]
        public void Zero_RemovesTheEntry_SoTheFileStaysClean()
        {
            SongOffsets.SetInMemory("a.gn", 20f);
            SongOffsets.SetInMemory("a.gn", 0f);
            StringAssert.DoesNotContain("a.gn", SongOffsets.Serialize());
        }

        [Test]
        public void Value_IsClampedToSaneRange()
        {
            SongOffsets.SetInMemory("a.gn", 99999f);
            Assert.AreEqual(SongOffsets.MaxMs, SongOffsets.Get("a.gn"), 1e-6f);
            SongOffsets.SetInMemory("b.gn", -99999f);
            Assert.AreEqual(SongOffsets.MinMs, SongOffsets.Get("b.gn"), 1e-6f);
        }

        [Test]
        public void Serialize_WritesSortedKeyValues_WithInvariantDecimalPoint()
        {
            SongOffsets.SetInMemory("sdom0002k.gn", -1.5f);
            SongOffsets.SetInMemory("sdom0001k.gn", 20f);
            var ini = SongOffsets.Serialize();

            StringAssert.Contains("sdom0001k.gn=20", ini);
            StringAssert.Contains("sdom0002k.gn=-1.5", ini);   // 小數點一定是「.」（不吃系統地區設定）
            Assert.Less(ini.IndexOf("sdom0001k.gn"), ini.IndexOf("sdom0002k.gn"), "要排序，diff 才不會亂跳");
        }

        [Test]
        public void Steps_MatchStepMania()
        {
            Assert.AreEqual(0.02, SongOffsets.StepSec, 1e-9);       // F11/F12
            Assert.AreEqual(0.001, SongOffsets.FineStepSec, 1e-9);  // 按住 Alt
        }
    }
}
