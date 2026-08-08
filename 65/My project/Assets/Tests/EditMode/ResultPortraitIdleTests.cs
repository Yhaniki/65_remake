using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// 結算左側那一排頭貼的待機 —— <see cref="ScreenGameplay.ResultRowRestMot"/>。
    ///
    /// 使用者要的是「結算的頭貼大家一起擺動」。這件事**修錯過一次**:上一版的規則是「身體是 MMD → 女版,
    /// 是 SDO → 照自己的性別」,只保證了 MMD 那幾格彼此一致;實際畫面是異質的(女角穿 MMD ＋ 男角沒穿)
    /// → 兩支不同的動作,使用者第二次回報「遠端那個 MMD 的擺動跟其他人沒同步」。
    ///
    /// 現在的規則是「整排同一個常數」,而且刻意做成**結構上**成立:那個常數沒有參數、也沒有分支可以走歪。
    /// 所以這裡釘的不是分支,是那個常數的性質 —— 它必須是舞台待機(64 幀迴圈),不能變成大廳待機
    /// (WREST0056=151 幀 / MREST0067=241 幀),否則整排的相位會跟舞台上的人整個對不上。
    ///
    /// 幀相位不必在這裡測:兩支舞台待機都是 MaxTime=63、相位 0、同一條 <see cref="SdoAvatar.LoopFrame"/>
    /// (對得上官方 hook 錄到的 "cursor spread=0.000 => IN LOCKSTEP")。
    /// </summary>
    public class ResultPortraitIdleTests
    {
        [Test]
        public void ResultRowIdle_IsAGameplayIdle_NotALobbyIdle()
        {
            // 必須是既有的**舞台**待機之一(不能憑空生一支,也不能退成大廳待機 —— 那是 151/241 幀,
            // 相位跟 64 幀的完全對不上)。目前釘在女版。
            Assert.AreEqual(ScreenGameplay.FemaleGameplayRestMot, ScreenGameplay.ResultRowRestMot,
                            "共用待機目前釘在女版舞台待機 —— 改了這個選擇就要一起改這條測試");
            Assert.AreNotEqual(SdoRoomAvatar.IdleMot, ScreenGameplay.ResultRowRestMot, "退成大廳待機了(女版)");
            Assert.AreNotEqual(SdoRoomAvatar.MaleIdleMot, ScreenGameplay.ResultRowRestMot, "退成大廳待機了(男版)");
        }

        [Test]
        public void TheTwoGameplayIdles_AreStillDistinct()
        {
            // 舞台待機本來就是一男一女兩支(結算列刻意只用其中一支;場上的舞者仍然各挑各的)。
            // 這兩個常數哪天被誰改成同一支的話,上面那條「釘在女版」就會失去意義而不自知。
            Assert.AreNotEqual(ScreenGameplay.MaleGameplayRestMot, ScreenGameplay.FemaleGameplayRestMot);
        }
    }
}
