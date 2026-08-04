using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>遊戲中聊天文字的自動隱藏:預設不顯示 → 有人講話就全出來 → 10 秒沒人說話直接消失(不淡出)。</summary>
    public class GameplayChatFadeTests
    {
        [Test]
        public void Hidden_Before_Anyone_Has_Spoken()
        {
            // 「預設是字不顯示狀態」—— 一句話都還沒有(lastActivity ≤ 0)就是 0。
            Assert.AreEqual(0f, GameplayChatFade.TextAlpha(false, -1.0, 120.0));
            Assert.AreEqual(0f, GameplayChatFade.TextAlpha(false, 0.0, 120.0));
        }

        [Test]
        public void Fully_Visible_Right_After_A_Message()
        {
            Assert.AreEqual(1f, GameplayChatFade.TextAlpha(false, 100.0, 100.0));
            Assert.AreEqual(1f, GameplayChatFade.TextAlpha(false, 100.0, 105.0));
            Assert.AreEqual(1f, GameplayChatFade.TextAlpha(false, 100.0, 110.0));   // 剛好第 10 秒仍全亮
        }

        [Test]
        public void Disappears_Instantly_After_Ten_Idle_Seconds()
        {
            const double t0 = 100.0;
            // 不淡出:過了第 10 秒的下一刻就是 0,中間不存在半透明。
            Assert.AreEqual(0f, GameplayChatFade.TextAlpha(false, t0, t0 + GameplayChatFade.IdleHideSec + 0.001));
            Assert.AreEqual(0f, GameplayChatFade.TextAlpha(false, t0, t0 + GameplayChatFade.IdleHideSec + 0.2));
            Assert.AreEqual(0f, GameplayChatFade.TextAlpha(false, t0, t0 + 60.0));
        }

        [Test]
        public void Typing_Keeps_The_Text_Up_No_Matter_How_Long_It_Has_Been_Quiet()
        {
            Assert.AreEqual(1f, GameplayChatFade.TextAlpha(true, -1.0, 500.0));    // 連一句話都沒有過也照顯示
            Assert.AreEqual(1f, GameplayChatFade.TextAlpha(true, 100.0, 500.0));   // 早就過了 10 秒
        }
    }
}
