using NUnit.Framework;
using Sdo.UI.Util;
using UnityEngine;

namespace Sdo.Tests
{
    /// <summary>頭上對話泡的字色分性別：泡身美術(TALK/ENTER/ADDANI)男女共用一張白/淡青玻璃圖，
    /// 唯一帶顏色的是泡裡的字 —— 女=桃紅 #7C0138、男=藍 #3B5AA6 (RGB 59,90,166)。</summary>
    public class RoomBubbleTextColorTests
    {
        [Test]
        public void Male_IsTheBlue()
        {
            Color32 c = RoomBubbleArt.TextColor(true);
            Assert.AreEqual(59, c.r);
            Assert.AreEqual(90, c.g);
            Assert.AreEqual(166, c.b);
            Assert.AreEqual(255, c.a);
        }

        [Test]
        public void Female_KeepsThePink()
        {
            Color32 c = RoomBubbleArt.TextColor(false);
            Assert.AreEqual(0x7C, c.r);
            Assert.AreEqual(0x01, c.g);
            Assert.AreEqual(0x38, c.b);
            Assert.AreEqual(255, c.a);
        }

        [Test]
        public void TheTwoGendersNeverShareAColour()
        {
            Color32 m = RoomBubbleArt.TextColor(true), f = RoomBubbleArt.TextColor(false);
            Assert.IsFalse(m.r == f.r && m.g == f.g && m.b == f.b);
        }
    }
}
