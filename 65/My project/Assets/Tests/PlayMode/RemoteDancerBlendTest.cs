using System.Collections;
using NUnit.Framework;
using Sdo.Game;
using UnityEngine;
using UnityEngine.TestTools;

namespace Sdo.Tests
{
    /// <summary>
    /// 同場的遠端舞者換動作時,混色長度要與本機**一樣**。
    ///
    /// 守的是使用者回報的症狀:「遠端的人物跳舞的時候切換 mot 動作,中間都會卡一下」。
    /// SdoAvatar 自己的 <c>BlendSec</c> 預設是 1.0 秒 —— 那是寫給房間 idle↔walk 的長度;
    /// 本機舞者在 TryLoadAvatar 被改成官方的 0.5 秒(DanceBlendSec,反編譯出來的 500ms),
    /// 但 SpawnExtraDancers 漏了同一行 → 遠端每個 DPS slice 邊界都混整整 1 秒。
    /// DPS 一個 row 常常只有 1~2 秒,於是混色蓋掉大半段 slice,下一個邊界又從混到一半的姿勢重新起混,
    /// 動作幅度被壓扁、看起來就是切動作時卡一下。
    ///
    /// 這條 EditMode 測不到:BlendSec 是生舞者那條流程寫進去的,要真的把場景與舞者開起來才看得到。
    /// </summary>
    public class RemoteDancerBlendTest
    {
        [UnityTearDown]
        public IEnumerator TearDown() => GameplayBoot.Teardown();

        [UnityTest]
        public IEnumerator RemoteDancer_CrossfadesForTheSameTimeAsTheLocalDancer()
        {
            ScreenGameplay game = null;
            yield return GameplayBoot.Boot(g => game = g, cfg =>
            {
                cfg.playerCount = 3;
                cfg.localDancerIndex = 0;
                cfg.netDancers = new[]
                {
                    new ScreenGameplay.DancerInfo { UserId = 1, Name = "本機",  Male = false },
                    new ScreenGameplay.DancerInfo { UserId = 2, Name = "遠端A", Male = false },
                    new ScreenGameplay.DancerInfo { UserId = 3, Name = "遠端B", Male = false },
                };
            });

            // Boot 只等到固定鏡頭備妥;遠端舞者是同一段流程稍後才生的 → 再輪詢一下(別睡固定秒數)。
            float t0 = Time.realtimeSinceStartup;
            while (game.DancerRootForTest(2) == null && Time.realtimeSinceStartup - t0 < 60f)
                yield return null;

            var local = game.AvatarRootForTest != null ? game.AvatarRootForTest.GetComponent<SdoAvatar>() : null;
            Assert.IsNotNull(local, "本機舞者沒建起來");

            // 兩位都查:漏設的話是整條生成迴圈都漏,但只驗第一位會讓「只設了第一位」這種改法悄悄過關。
            for (int i = 1; i <= 2; i++)
            {
                var root = game.DancerRootForTest(i);
                Assert.IsNotNull(root, $"遠端舞者 {i} 沒建起來");
                var av = root.GetComponent<SdoAvatar>();
                Assert.IsNotNull(av, $"遠端舞者 {i} 的 root 上沒有 SdoAvatar");
                Assert.AreEqual(local.BlendSec, av.BlendSec, 1e-4f,
                    $"遠端舞者 {i} 的換動作混色比本機長 —— 每個 DPS slice 邊界都會拖一段,看起來就是「切動作卡一下」");
            }
        }
    }
}
