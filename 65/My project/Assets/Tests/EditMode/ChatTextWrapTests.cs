using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// 遊戲畫面聊天列的折行(<see cref="ChatTextWrap"/>)。那條列是一列一顆 TextMesh、**沒有自動折行**,
    /// 長訊息以前直接畫到欄寬外面 —— 這裡是推進聊天框之前先把它切成幾列的算式。
    ///
    /// 量寬度用「每個字 10px」的假量尺,所以每一列裝得下的字數 = 寬 ÷ 10。
    /// </summary>
    public class ChatTextWrapTests
    {
        private static List<string> Wrap(string s, float first, float rest)
            => ChatTextWrap.Wrap(s, n => n * 10f, first, rest);

        [Test]
        public void Short_Text_Stays_One_Line()
        {
            var parts = Wrap("你好", 100f, 100f);
            Assert.AreEqual(1, parts.Count);
            Assert.AreEqual("你好", parts[0]);
        }

        [Test]
        public void Chinese_Fills_Each_Row_Before_Wrapping()
        {
            // 一列 5 個字(50px);12 個字 → 5 / 5 / 2
            var parts = Wrap("一二三四五六七八九十壹貳", 50f, 50f);
            Assert.AreEqual(3, parts.Count);
            Assert.AreEqual("一二三四五", parts[0]);
            Assert.AreEqual("六七八九十", parts[1]);
            Assert.AreEqual("壹貳", parts[2]);
        }

        [Test]
        public void Long_Digit_Run_Also_Fills_The_Row()
        {
            // 使用者回報的那一種:整串數字不能因為「它是一個單字」就整串跳到下一列。
            var parts = Wrap(new string('8', 12), 50f, 50f);
            Assert.AreEqual(3, parts.Count);
            Assert.AreEqual("88888", parts[0]);
            Assert.AreEqual("88888", parts[1]);
            Assert.AreEqual("88", parts[2]);
        }

        [Test]
        public void Break_Retreats_To_A_Space_So_Words_Are_Not_Cut()
        {
            // 一列 10 個字元。"hello worldly" → 硬切會切成 "hello worl"，退到空白變成 "hello"。
            var parts = Wrap("hello worldly", 100f, 100f);
            Assert.AreEqual("hello", parts[0]);
            Assert.AreEqual("worldly", parts[1]);
        }

        [Test]
        public void A_Word_Longer_Than_Half_The_Row_Is_Cut_Rather_Than_Leaving_The_Row_Empty()
        {
            // 回退超過半列就不回退 —— 不然一個超長單字會讓整列空著,又變回原本的毛病。
            var parts = Wrap("ab abcdefghijklmnop", 100f, 100f);
            Assert.AreEqual("ab abcdefg", parts[0], "退到 'ab' 會讓出 8/10 列 → 不退,照原切點硬切");
        }

        [Test]
        public void The_Space_At_A_Break_Is_Swallowed_So_The_Next_Row_Is_Not_Indented()
        {
            var parts = Wrap("aaaaaaaaaa bbbb", 100f, 100f);
            Assert.AreEqual("aaaaaaaaaa", parts[0]);
            Assert.AreEqual("bbbb", parts[1], "續列開頭不該留著折行處的空白");
        }

        [Test]
        public void First_Row_Can_Have_A_Smaller_Budget_Than_The_Rest()
        {
            // 有表情圖的那一列:名字與小圖先佔掉一段,第一列可用的寬因此比較窄。
            var parts = Wrap("一二三四五六七八", 20f, 50f);
            Assert.AreEqual("一二", parts[0]);
            Assert.AreEqual("三四五六七", parts[1]);
            Assert.AreEqual("八", parts[2]);
        }

        [Test]
        public void Always_Makes_Progress_Even_When_Nothing_Fits()
        {
            // 寬度荒謬地小也不能空轉:一列至少收一個字。
            var parts = Wrap("abc", 1f, 1f);
            Assert.AreEqual(3, parts.Count);
            Assert.AreEqual("a", parts[0]);
        }

        [Test]
        public void Empty_And_Null_Are_Safe()
        {
            Assert.AreEqual(1, Wrap("", 50f, 50f).Count);
            Assert.AreEqual("", ChatTextWrap.Wrap(null, n => n * 10f, 50f, 50f)[0]);
            Assert.AreEqual("abc", ChatTextWrap.Wrap("abc", null, 1f, 1f)[0], "沒有量尺就不折");
        }
    }
}
