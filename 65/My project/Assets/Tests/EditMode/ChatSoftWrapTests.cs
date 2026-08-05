using NUnit.Framework;
using Sdo.UI.Util;

namespace Sdo.Tests
{
    /// <summary>
    /// 長串英數的可折點(<see cref="ChatSoftWrap"/>)。
    /// 症狀:全部打數字時,那串數字對 TMP 是「一個單字」→ 擠不進這一排的剩餘寬度就整串跳到下一排,
    /// 名字那排空著半截(使用者截圖:「飄漂o2:」後面整排空白,8888… 全跑到下一排)。
    /// </summary>
    public class ChatSoftWrapTests
    {
        private const char Z = ChatSoftWrap.Zwsp;

        [Test]
        public void Long_Digit_Run_Gets_A_Break_Point_Between_Every_Character()
        {
            string outp = ChatSoftWrap.Apply(new string('8', 14));
            Assert.AreEqual(14 + 13, outp.Length);   // 14 個字元 + 13 個可折點
            Assert.AreEqual("8" + Z + "8", outp.Substring(0, 3));
            Assert.IsFalse(outp.EndsWith(Z.ToString()), "尾巴不要多掛一個可折點");
            // 去掉零寬空格之後必須和原文一模一樣 —— 顯示內容一個字都不能變。
            Assert.AreEqual(new string('8', 14), outp.Replace(Z.ToString(), ""));
        }

        [Test]
        public void Short_Words_Are_Left_Alone()
        {
            // 正常英文句子不該被從單字中間切開;整串沒東西要改時連字串都不重配。
            const string s = "hello there ok 123";
            Assert.AreSame(s, ChatSoftWrap.Apply(s));
            Assert.AreSame("測試測試測試測試測試測試測試測試", ChatSoftWrap.Apply("測試測試測試測試測試測試測試測試"));
        }

        [Test]
        public void Chinese_Is_Untouched_Because_TMP_Already_Breaks_Per_Character()
        {
            const string s = "測試測試測試測試測試測試測試測試測試測試測試測試";
            Assert.AreEqual(s, ChatSoftWrap.Apply(s));
        }

        [Test]
        public void Rich_Text_Tags_Are_Never_Split()
        {
            // 標籤裡的 link id / 色碼被塞進零寬空格 → 點名字密語會失效、顏色會變亂碼。
            string outp = ChatSoftWrap.Apply("<color=#72c1fe><link=\"w|SomebodyWithALongName\">SomebodyWithALongName</link>: "
                                             + new string('8', 20) + "</color>");
            Assert.IsTrue(outp.Contains("<link=\"w|SomebodyWithALongName\">"), "標籤內容必須原封不動");
            Assert.IsTrue(outp.Contains("<color=#72c1fe>"));
            Assert.IsTrue(outp.Contains("</color>"));
            Assert.IsTrue(outp.Contains("8" + Z + "8"), "標籤外的長數字串照樣要給可折點");
        }

        [Test]
        public void Escaped_Entities_Stay_In_One_Piece()
        {
            // 使用者打的 < > & 會被跳脫成 &lt; &gt; &amp;;可折點插在實體中間會把它拆成亂碼。
            string outp = ChatSoftWrap.Apply("&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;");
            Assert.IsFalse(outp.Contains("&" + Z), "實體開頭之後不准斷");
            Assert.IsFalse(outp.Contains(Z + ";"), "實體結尾之前不准斷");
            Assert.IsTrue(outp.Contains(";" + Z + "&"), "實體與實體之間才是可折點");
            Assert.AreEqual("&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;", outp.Replace(Z.ToString(), ""));
        }

        [Test]
        public void Mixed_Message_Only_Breaks_The_Long_Run()
        {
            string outp = ChatSoftWrap.Apply("飄漂o2: " + new string('8', 30));
            Assert.IsTrue(outp.StartsWith("飄漂o2: 8" + Z), "名字與短字不動,長數字串才拆");
            Assert.IsFalse(outp.Contains("漂" + Z));
        }

        [Test]
        public void Caret_Index_Is_Shifted_By_The_Break_Points_In_Front_Of_It()
        {
            // 頭上打字泡的游標是拿「顯示字元索引」去問 characterInfo 要座標的,而零寬空格自己也佔一格
            // → 不換算的話,打愈長游標偏得愈多。
            int mapped;
            ChatSoftWrap.Apply(new string('8', 20), ChatSoftWrap.DefaultMinRun, 0, out mapped);
            Assert.AreEqual(0, mapped, "游標在最前面 → 前面沒有可折點");

            ChatSoftWrap.Apply(new string('8', 20), ChatSoftWrap.DefaultMinRun, 5, out mapped);
            Assert.AreEqual(10, mapped, "前 5 個字之間插了 5 個可折點");

            ChatSoftWrap.Apply(new string('8', 20), ChatSoftWrap.DefaultMinRun, 20, out mapped);
            Assert.AreEqual(39, mapped, "游標在最尾端 → 前面 19 個可折點全算進去");
        }

        [Test]
        public void Caret_Index_Is_Untouched_When_Nothing_Was_Inserted()
        {
            int mapped;
            ChatSoftWrap.Apply("短短的一句話", ChatSoftWrap.DefaultMinRun, 3, out mapped);
            Assert.AreEqual(3, mapped);
            ChatSoftWrap.Apply("", ChatSoftWrap.DefaultMinRun, 0, out mapped);
            Assert.AreEqual(0, mapped);
        }

        [Test]
        public void Empty_And_Null_Are_Safe()
        {
            Assert.AreEqual("", ChatSoftWrap.Apply(null));
            Assert.AreEqual("", ChatSoftWrap.Apply(""));
        }

        [Test]
        public void Unclosed_Tag_Does_Not_Lose_Text()
        {
            // 沒收尾的 '<'(使用者亂打)不能讓後面整段消失。
            const string s = "abc <color=#fff";
            Assert.AreEqual(s, ChatSoftWrap.Apply(s));
        }
    }
}
