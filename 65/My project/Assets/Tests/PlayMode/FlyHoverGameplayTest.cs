using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Game;
using UnityEngine;
using UnityEngine.TestTools;

namespace Sdo.Tests
{
    /// <summary>
    /// 舞台(遊玩畫面)飛行懸浮的實機回歸 —— 這條只有真的把畫面跑起來才守得住,EditMode 測不到。
    ///
    /// 守的是使用者回報的症狀在舞台上的版本:「穿會飛的翅膀,靜止的時候高度沒有提升」。舊版 UpdateFlyHover 有兩個
    /// 會讓人沉下去的閘門 ——
    ///   (a) 抬升量去量「flystay 比 dance ready pose 高多少」,而 flystay 相對站姿只有 +3.4(女)/+1.2(男)
    ///       (見 FlyHoverRealDataTests),Δ 常常算成 0 → 整段靜默失效;
    ///   (b) `_avatar.IsRestPose ? 0 : Δ` —— 待機與勝負定格被判為 rest 而抬 0,跳舞才抬 → 一沉一浮。
    /// 現在懸浮只看「有沒有穿飛行翅膀」,所以整場(待機/跳舞/定格)都是同一個高度。這個測試就是量那件事:
    /// 待機時已經浮著,而且過了幾秒(歌跑起來、進 DPS 舞蹈)高度一模一樣。
    ///
    /// 註:官方舞台其實不浮(y+=10 只掛在房間 state,見 ScreenGameplay.UpdateFlyHover 的註解),這是 remake 依使用者
    /// 要求刻意偏離的一條 —— 所以更需要測試釘住,否則將來對照反編譯的人會以為是 bug 而拿掉。
    /// </summary>
    public class FlyHoverGameplayTest
    {
        // 008448 = 反編譯硬編的 5 個會飛的翅膀之一(0x2100),女版 mesh 隨 clean\DATA 出貨。
        private const string FlyingWingMesh = "AVATAR/008448_WOMAN_CHIBANG.MSH";

        [UnityTearDown]
        public IEnumerator TearDown() => GameplayBoot.Teardown();

        private static string[] PartsWithWing()
        {
            var parts = new List<string>(SdoRoomAvatar.DefaultParts(male: false)) { FlyingWingMesh };
            return parts.ToArray();
        }

        [UnityTest]
        public IEnumerator FlyingWing_HoversTheSameWhileIdleAndWhileDancing()
        {
            ScreenGameplay game = null;
            yield return GameplayBoot.Boot(g => game = g, cfg => cfg.avatarParts = PartsWithWing());
            Assert.IsNotNull(game.AvatarRootForTest, "舞者沒建起來");

            // 待機(歌還沒開始跳):就該已經浮著 —— 這正是回報的「靜止時高度沒有提升」。
            Assert.AreEqual(SpecialMotionItems.FlyHoverY, game.FlyLiftForTest, 0.01f,
                "飛行角色待機時沒有浮起來");
            float idleY = game.AvatarRootForTest.position.y;

            yield return new WaitForSecondsRealtime(8f);   // 讓時鐘跑起來進入 DPS 舞蹈

            Assert.AreEqual(SpecialMotionItems.FlyHoverY, game.FlyLiftForTest, 0.01f,
                "跳舞時懸浮被改掉了(舊版的 IsRestPose 閘門就是在這裡讓人一沉一浮)");
            Assert.AreEqual(idleY, game.AvatarRootForTest.position.y, 0.01f,
                "待機與跳舞的 root 高度必須一致");
        }

        [UnityTest]
        public IEnumerator WithoutFlyingWing_TheDancerStaysOnTheFloor()
        {
            ScreenGameplay game = null;
            yield return GameplayBoot.Boot(g => game = g);   // 預設穿搭沒有翅膀
            Assert.IsNotNull(game.AvatarRootForTest, "舞者沒建起來");
            float y0 = game.AvatarRootForTest.position.y;

            yield return new WaitForSecondsRealtime(8f);

            Assert.AreEqual(0f, game.FlyLiftForTest, 0.001f, "沒穿翅膀不該有任何懸浮");
            Assert.AreEqual(y0, game.AvatarRootForTest.position.y, 0.01f, "沒穿翅膀時 root 高度不該被動到");
        }
    }
}
