using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// 歌名 35 字／歌手 20 字的顯示上限（選歌清單 + 遊戲中 HUD 共用 <see cref="SongTextLimits"/>）。
    /// </summary>
    public class SongTextLimitsTests
    {
        [Test]
        public void Limits_Are_35_Title_And_20_Artist()
        {
            Assert.AreEqual(35, SongTextLimits.Title);
            Assert.AreEqual(20, SongTextLimits.Artist);
        }

        [Test]
        public void Short_Text_Is_Untouched()
        {
            Assert.AreEqual("Vortex", SongTextLimits.ClampTitle("Vortex"));
            Assert.AreEqual("紫羅蘭", SongTextLimits.ClampTitle("紫羅蘭"));
            Assert.AreEqual("SOUL EXPLOSION", SongTextLimits.ClampArtist("SOUL EXPLOSION"));
        }

        [Test]
        public void Text_Exactly_At_The_Limit_Survives_Whole()
        {
            string t35 = new string('a', 35), a20 = new string('b', 20);
            Assert.AreEqual(t35, SongTextLimits.ClampTitle(t35));
            Assert.AreEqual(a20, SongTextLimits.ClampArtist(a20));
        }

        [Test]
        public void One_Char_Over_Loses_Exactly_That_Char()
        {
            Assert.AreEqual(new string('a', 35), SongTextLimits.ClampTitle(new string('a', 36)));
            Assert.AreEqual(new string('b', 20), SongTextLimits.ClampArtist(new string('b', 21)));
        }

        [Test]
        public void The_Overflowing_Real_Title_Is_Cut_To_35()
        {
            // 實際畫出框外的那首（外部歌）：55 字 → 只留前 35 字，尾巴直接砍掉、不加省略號
            const string full = "Shiroi Yuki no Princess wa (The Snow White Princess is)";
            string cut = SongTextLimits.ClampTitle(full);
            Assert.AreEqual("Shiroi Yuki no Princess wa (The Sno", cut);
            Assert.AreEqual(35, cut.Length);
            StringAssert.StartsWith(cut, full);   // 前綴一致（只砍尾巴，不改內容）
        }

        [Test]
        public void Cjk_Counts_As_One_Char_Each()
        {
            // 「字」就是字元：CJK 一個算一個（不按顯示寬度加權）
            string s = new string('中', 40);
            Assert.AreEqual(35, SongTextLimits.ClampTitle(s).Length);
            Assert.AreEqual(20, SongTextLimits.ClampArtist(s).Length);
        }

        [Test]
        public void Null_And_Empty_Come_Back_As_Empty_String()
        {
            Assert.AreEqual("", SongTextLimits.ClampTitle(null));
            Assert.AreEqual("", SongTextLimits.ClampArtist(null));
            Assert.AreEqual("", SongTextLimits.ClampTitle(""));
        }

        [Test]
        public void Surrogate_Pair_Is_Never_Split_In_Half()
        {
            // 邊界正好落在 surrogate pair 中間 → 整個 pair 一起丟掉（切一半會變破字／tofu）
            string s = new string('a', 19) + "\U0001F3B5" + "tail";   // 19 + (2 個 char 的音符 emoji) + …
            string cut = SongTextLimits.Clamp(s, 20);
            Assert.AreEqual(new string('a', 19), cut);
            Assert.IsFalse(char.IsHighSurrogate(cut[cut.Length - 1]));

            // 邊界落在 pair 後面 → pair 完整保留
            Assert.AreEqual(new string('a', 19) + "\U0001F3B5", SongTextLimits.Clamp(s, 21));
        }

        [Test]
        public void Zero_Or_Negative_Limit_Yields_Empty()
        {
            Assert.AreEqual("", SongTextLimits.Clamp("abc", 0));
            Assert.AreEqual("", SongTextLimits.Clamp("abc", -5));
        }
    }
}
