using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// 結算左側那幾格頭貼要用哪一支待機 —— <see cref="ScreenGameplay.ResultPortraitRestMot"/>。
    ///
    /// 病灶(使用者回報「MMD 模型結算時兩個頭貼的動作不同步」):兩格的**幀號一直都是對齊的**
    /// (WREST0072 與 MREST0082 都是 MaxTime=63、相位都是 0、同一條 SdoAvatar.LoopFrame,
    /// 對得上官方 hook 錄到的 "cursor spread=0.000 => IN LOCKSTEP"),差的是**動作內容**:
    /// 一格男角放男版待機、一格女角放女版待機。穿 SDO 身體時那是一男一女、本來就該不一樣;
    /// 換成 MMD 後兩格都是同一個模型,就變成「同一個人卻在做兩套動作」。
    ///
    /// 所以規則是:身體是 MMD → 大家同一支;還是 SDO 身體 → 照自己的性別。
    /// 這裡刻意不寫死 .MOT 檔名(檔名只有 ScreenGameplay 那三個常數一份,見那裡的註解),
    /// 只釘住彼此的關係。
    /// </summary>
    public class ResultPortraitIdleTests
    {
        [Test]
        public void MmdBody_MaleAndFemale_ShareTheSameIdle()
        {
            // 同一個 MMD 模型,兩格必須完全同一支 —— 這就是「不同步」的修正點
            Assert.AreEqual(ScreenGameplay.ResultPortraitRestMot(true, male: false),
                            ScreenGameplay.ResultPortraitRestMot(true, male: true));
        }

        [Test]
        public void SdoBody_KeepsEachGendersOwnIdle()
        {
            // SDO 身體是一男一女,各自的待機才是對的(官方行為,不要一起改掉)
            Assert.AreNotEqual(ScreenGameplay.ResultPortraitRestMot(false, male: false),
                               ScreenGameplay.ResultPortraitRestMot(false, male: true));
        }

        [Test]
        public void MmdIdle_IsOneOfTheGameplayIdles_NotSomethingNew()
        {
            // 共用的那一支必須是既有的**舞台**待機之一(不能變成大廳待機 WREST0056,
            // 也不能憑空生一支);目前挑的是女版。
            string mmd = ScreenGameplay.ResultPortraitRestMot(true, male: false);
            Assert.AreEqual(ScreenGameplay.ResultPortraitRestMot(false, male: false), mmd,
                            "共用待機目前釘在女版舞台待機 —— 改了這個選擇就要一起改這條測試");
        }

        [Test]
        public void SwitchingMmdOn_ChangesOnlyTheMaleRow()
        {
            // 女角那格開不開 MMD 都是同一支(不該無謂地換 clip → 不該有多餘的硬切)
            Assert.AreEqual(ScreenGameplay.ResultPortraitRestMot(false, male: false),
                            ScreenGameplay.ResultPortraitRestMot(true, male: false));
            // 男角那格才會被改過去
            Assert.AreNotEqual(ScreenGameplay.ResultPortraitRestMot(false, male: true),
                               ScreenGameplay.ResultPortraitRestMot(true, male: true));
        }
    }
}
