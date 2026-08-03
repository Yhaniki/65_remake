using System.Collections;
using NUnit.Framework;
using Sdo.Game;
using UnityEngine;
using UnityEngine.TestTools;

namespace Sdo.Tests
{
    /// <summary>
    /// 同場的遠端舞者必須跟本機舞者**在同一層**。
    ///
    /// 守的是使用者回報的症狀:「進遊戲後其他玩家變超小」。3D 舞台是 SceneCam 單獨渲染的
    /// (它的 cullingMask 只有 SceneLayer,主相機反過來把那一層剔掉),所以遠端舞者生出來後若沒被丟進那一層,
    /// 就改由**主正交相機**畫 —— 那台的座標系是 800×600 design px,模型單位直接當像素,
    /// 60 單位高的角色於是變成貼在畫面上的 60px 小人(而且不受場景遮擋)。
    ///
    /// 這條 EditMode 測不到:純粹是 layer 指派,但要看得出錯在哪就得真的把畫面、相機、舞者都開起來。
    /// </summary>
    public class RemoteDancerLayerTest
    {
        [UnityTearDown]
        public IEnumerator TearDown() => GameplayBoot.Teardown();

        [UnityTest]
        public IEnumerator RemoteDancer_RendersOnTheSameLayerAsTheLocalDancer()
        {
            ScreenGameplay game = null;
            yield return GameplayBoot.Boot(g => game = g, cfg =>
            {
                // 兩人同場,本機是第 0 位 —— 就是回報時的擺法(一台看另一台變小人)。
                // Parts 刻意留 null(= 對方的穿搭還沒傳到):那條路徑要退回預設整套,不能把 null 丟進
                // SdoAvatarBuilder.LoadParts —— 它的 foreach 會 NRE,而那條 NRE 會把整個 SpawnExtraDancers 打斷,
                // 後面的舞者連生都不會生。
                cfg.playerCount = 2;
                cfg.localDancerIndex = 0;
                cfg.netDancers = new[]
                {
                    new ScreenGameplay.DancerInfo { UserId = 1, Name = "本機", Male = false },
                    new ScreenGameplay.DancerInfo { UserId = 2, Name = "遠端", Male = false },
                };
            });

            // Boot 只等到固定鏡頭備妥;遠端舞者是同一段流程稍後才生的 → 再輪詢一下(別睡固定秒數)。
            float t0 = Time.realtimeSinceStartup;
            while (game.DancerRootForTest(1) == null && Time.realtimeSinceStartup - t0 < 60f)
                yield return null;

            var local = game.AvatarRootForTest;
            var remote = game.DancerRootForTest(1);
            Assert.IsNotNull(local, "本機舞者沒建起來");
            Assert.IsNotNull(remote, "遠端舞者沒建起來");

            // 🔴 要看**整棵**:蒙皮的 Renderer 掛在子物件(各部位)上,只把 root 設對的話畫出來的還是在 Default 層。
            var rends = remote.GetComponentsInChildren<Renderer>(true);
            Assert.Greater(rends.Length, 0, "遠端舞者一個 Renderer 都沒有 → 外觀根本沒載進來");
            foreach (var r in rends)
                Assert.AreEqual(local.gameObject.layer, r.gameObject.layer,
                    $"遠端舞者的 {r.name} 不在本機那一層 → 會被主正交相機當 design px 畫成小人");
        }
    }
}
