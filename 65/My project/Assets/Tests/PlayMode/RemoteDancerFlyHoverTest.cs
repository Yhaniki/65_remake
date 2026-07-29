using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Game;
using UnityEngine;
using UnityEngine.TestTools;

namespace Sdo.Tests
{
    /// <summary>
    /// 同場的**遠端**舞者也要吃飛行翅膀的懸浮與 flystay 待機 —— 而且是看**他自己的**穿搭,不是本機的。
    ///
    /// 本機那條路徑由 <see cref="FlyHoverGameplayTest"/> 守著(UpdateFlyHover);遠端是完全另一條程式碼
    /// (SpawnExtraDancers 擺位時加 HoverY),漏掉的話同場會出現「我浮著、別人踩在地上」。
    /// 反過來,遠端若沿用本機那份 RestMot,本機一穿翅膀就會讓全場的人待機都變飛行姿勢。
    ///
    /// 🔴 量高度一定要指定**地面** clip 當基準(<c>FeetYAt(frame, clip)</c>)。無參數的 FeetYAt 取樣的是
    /// 當下活著的 clip,而 flystay 是收腳的浮空姿勢 —— 用它量出來的「腳底」會把 clip 自身的抬升折進去
    /// (實測女版 +1.57),看起來就像懸浮多了一截。見 SdoAvatar.FeetYAt(float, MotLoader) 的註解。
    /// </summary>
    public class RemoteDancerFlyHoverTest
    {
        // 008448 = 反編譯硬編的 5 個會飛的翅膀之一(0x2100),女版 mesh 隨 clean\DATA 出貨(同 FlyHoverGameplayTest)。
        private const string FlyingWingMesh = "AVATAR/008448_WOMAN_CHIBANG.MSH";

        [UnityTearDown]
        public IEnumerator TearDown() => GameplayBoot.Teardown();

        private static string[] NoWing() => SdoRoomAvatar.DefaultParts(male: false);

        private static string[] WithWing()
        {
            var parts = new List<string>(SdoRoomAvatar.DefaultParts(male: false)) { FlyingWingMesh };
            return parts.ToArray();
        }

        private static SdoAvatar AvatarOn(Transform root)
        {
            var av = root != null ? root.GetComponent<SdoAvatar>() : null;
            Assert.IsNotNull(av, "舞者的 root 上沒有 SdoAvatar");
            return av;
        }

        /// <summary>某位舞者的腳底世界高度,用 <paramref name="ground"/>(地面 clip)當共同基準量 —— 地板 = 0。</summary>
        private static float FeetWorldY(Transform root, MotLoader ground)
            => root.position.y + AvatarOn(root).FeetYAt(0f, ground);

        /// <summary>三人同場(本機 + 兩位遠端),等到兩位遠端都生出來。</summary>
        private static IEnumerator BootThree(System.Action<ScreenGameplay> onReady,
                                             string[] localParts, string[] remote1, string[] remote2)
        {
            ScreenGameplay game = null;
            yield return GameplayBoot.Boot(g => game = g, cfg =>
            {
                cfg.playerCount = 3;
                cfg.localDancerIndex = 0;
                cfg.avatarParts = localParts;
                cfg.netDancers = new[]
                {
                    new ScreenGameplay.DancerInfo { UserId = 1, Name = "本機",  Male = false, Parts = localParts },
                    new ScreenGameplay.DancerInfo { UserId = 2, Name = "遠端A", Male = false, Parts = remote1 },
                    new ScreenGameplay.DancerInfo { UserId = 3, Name = "遠端B", Male = false, Parts = remote2 },
                };
            });

            float t0 = Time.realtimeSinceStartup;
            while (game.DancerRootForTest(2) == null && Time.realtimeSinceStartup - t0 < 60f)
                yield return null;
            Assert.IsNotNull(game.DancerRootForTest(1), "遠端 A 沒建起來");
            Assert.IsNotNull(game.DancerRootForTest(2), "遠端 B 沒建起來");
            onReady(game);
        }

        [UnityTest]
        public IEnumerator RemoteDancer_WithFlyingWing_HoversExactlyHoverYAboveOneWithout()
        {
            ScreenGameplay game = null;
            // 本機不穿;遠端 A 穿翅膀、遠端 B 不穿 —— 同一場裡直接對照,排除場地/基準的變因。
            yield return BootThree(g => game = g, localParts: NoWing(), remote1: WithWing(), remote2: NoWing());

            var wing = game.DancerRootForTest(1);
            var plain = game.DancerRootForTest(2);
            var ground = AvatarOn(plain).RestMot;   // 沒穿翅膀那位的待機 = 地面 idle → 兩位共用的量測基準
            Assert.IsNotNull(ground, "地面待機 clip 載不到,沒有基準可量");

            Assert.AreEqual(SpecialMotionItems.FlyHoverY,
                FeetWorldY(wing, ground) - FeetWorldY(plain, ground), 0.01f,
                "穿飛行翅膀的遠端舞者沒有浮起來(本機有 UpdateFlyHover,遠端是另一條路徑)");
        }

        [UnityTest]
        public IEnumerator RemoteDancer_WithoutWing_StaysOnTheFloorAndKeepsItsOwnIdle()
        {
            ScreenGameplay game = null;
            // 本機穿翅膀,兩位遠端都不穿 —— 守的是反方向:懸浮與待機都不該跟著本機的穿搭走。
            yield return BootThree(g => game = g, localParts: WithWing(), remote1: NoWing(), remote2: NoWing());

            var remote = game.DancerRootForTest(1);
            var ground = AvatarOn(remote).RestMot;
            Assert.IsNotNull(ground, "地面待機 clip 載不到,沒有基準可量");

            // 本機浮著、遠端貼地 → 差距正好是 HoverY。遠端若沿用本機的 flying 狀態,差距會變成 0。
            Assert.AreEqual(SpecialMotionItems.FlyHoverY,
                FeetWorldY(game.AvatarRootForTest, ground) - FeetWorldY(remote, ground), 0.01f,
                "沒穿翅膀的遠端舞者被抬起來了 —— 懸浮不該看本機的穿搭");
            // 兩位遠端都不穿 → 高度必須一致(順便釘住「每一位都各自算」而不是只算第一位)。
            Assert.AreEqual(FeetWorldY(remote, ground), FeetWorldY(game.DancerRootForTest(2), ground), 0.01f,
                "兩位同樣沒穿翅膀的遠端舞者高度不一致");

            // 待機 clip 也要各看各的:沿用本機那份(_sharedRestMot,此刻是 flystay)會讓沒穿翅膀的人擺飛行姿勢。
            Assert.AreNotSame(AvatarOn(game.AvatarRootForTest).RestMot, AvatarOn(remote).RestMot,
                "遠端舞者沿用了本機的 flystay 待機 —— 沒穿翅膀的人會擺出飛行姿勢");
        }
    }
}
