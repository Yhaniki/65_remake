using NUnit.Framework;
using Sdo.UI.Util;

namespace Sdo.Tests
{
    // OutlinedLabel.StripColorTags：聊天描邊的邊層要把面層的色碼拿掉、其餘標記留著，
    // 這樣綠/青/白的 rich 行都描出「純黑」邊，而不是把彩色字位移一份變成糊影。
    public class OutlinedLabelTests
    {
        [Test]
        public void Strips_Color_Open_And_Close()
            => Assert.AreEqual("你沒有家族",
                OutlinedLabel.StripColorTags("<color=#3CE63C>你沒有家族</color>"));

        [Test]
        public void Strips_Hash_Shorthand()
            => Assert.AreEqual("hi", OutlinedLabel.StripColorTags("<#ff0000>hi</color>"));

        [Test]
        public void Strips_Hex_With_Alpha()
            => Assert.AreEqual("x", OutlinedLabel.StripColorTags("<#ff000080>x</color>"));

        [Test]
        public void Keeps_Noparse_So_Guild_Tag_Stays_Literal()
            => Assert.AreEqual("<noparse><家族></noparse>月光",
                OutlinedLabel.StripColorTags("<color=#3CE63C><noparse><家族></noparse>月光</color>"));

        [Test]
        public void Keeps_Link_Tag_For_The_Name()
            => Assert.AreEqual("<link=\"w|月光\">月光</link>: 嗨",
                OutlinedLabel.StripColorTags("<link=\"w|月光\">月光</link>: 嗨"));

        [Test]
        public void Leaves_Escaped_Angle_Brackets_Alone()   // EscapeTmp 產出的 &lt;/&gt; 是內容不是色碼
            => Assert.AreEqual("a &lt;b&gt; c", OutlinedLabel.StripColorTags("a &lt;b&gt; c"));

        [Test]
        public void Null_And_Empty_Are_Safe()
        {
            Assert.AreEqual("", OutlinedLabel.StripColorTags(null));
            Assert.AreEqual("", OutlinedLabel.StripColorTags(""));
        }

        [Test]
        public void Plain_Text_Is_Unchanged()
            => Assert.AreEqual("晚上開團囉", OutlinedLabel.StripColorTags("晚上開團囉"));
    }
}
