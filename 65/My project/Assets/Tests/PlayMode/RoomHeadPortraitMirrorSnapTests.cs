using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// 頭貼的「硬切」要跟著本體 —— 使用者回報:「room 裡面從旁觀出來,雖然人物的動作平滑關掉了,
    /// 可是上面大頭貼的平滑沒關掉。」
    ///
    /// 成因:按「進入 / 旁觀」換 slot 時本體是 <see cref="SdoAvatar.SnapNextClip"/> + SetClip(硬切,
    /// 見 <see cref="RoomScene3D.SetLocalSeat"/>),而上面那格頭貼是**鏡射**本體的 clip,鏡射路徑
    /// (<see cref="RoomHeadPortrait.MirrorBody"/>)每幀走的是會混 <see cref="SdoAvatar.BlendSec"/> 的 SetClip。
    /// 於是人物瞬間換好了,頭貼還在從看戲姿勢慢慢扭回站姿。
    ///
    /// 修法:SetLocalSeat 回報「這次是硬切」,呼叫端轉告 <see cref="RoomHeadPortrait.SnapMirrorPose"/>,
    /// 下一次鏡射就走 <see cref="SdoAvatar.PoseInitialClip"/>(硬切,而且不留一次性旗標)。
    ///
    /// 觀測點是 <see cref="SdoAvatar.ClipSwitchPending"/>(下一幀會不會混色)—— 所以這裡**不跨幀**:
    /// 一 yield 出去 LateUpdate 就把那個待處理的切換消耗掉了。
    ///
    /// Run: -runTests -testPlatform PlayMode -testFilter Sdo.Tests.RoomHeadPortraitMirrorSnapTests
    /// </summary>
    public class RoomHeadPortraitMirrorSnapTests
    {
        private const int SpectatorSlot = 6;   // 第一個旁觀席(RoomLayout.SeatCount)

        [SetUp]
        public void RequireData()
        {
            if (!System.IO.File.Exists(System.IO.Path.Combine(SdoExtracted.Root, "AVATAR", "FEMALE.HRC")))
                Assert.Ignore("SDO game data not available");
        }

        /// <summary>本體(房間裡那隻可走動的角色)+ 頭貼,兩邊都停在座位待機、而且已經接上。</summary>
        private static void BuildRig(out GameObject bodyGo, out GameObject headGo,
                                     out SdoAvatar body, out RoomHeadPortrait head,
                                     out MotLoader seated, out MotLoader watching)
        {
            LogAssert.ignoreFailingMessages = true;
            seated = SdoRoomAvatar.LoadMot(RoomScene3D.ResolveIdleMot(0, male: false, parts: null));
            watching = SdoRoomAvatar.LoadMot(RoomScene3D.ResolveIdleMot(SpectatorSlot, male: false, parts: null));
            Assert.IsNotNull(seated); Assert.IsNotNull(watching);
            Assert.AreNotSame(seated, watching, "座位待機與看戲姿勢必須是兩支 clip,否則這條測試什麼都沒測到");

            bodyGo = new GameObject("mirrorBodyProbe");
            body = SdoRoomAvatar.Build(bodyGo, 0, portraitOpaque: false, male: false, equippedParts: null);
            Assert.IsNotNull(body, "房間 avatar 應建得起來");
            body.PoseInitialClip(seated);          // 坐在座位上待機

            headGo = new GameObject("mirrorHeadProbe");
            head = headGo.AddComponent<RoomHeadPortrait>();
            Assert.IsTrue(head.Init(male: false), "頭貼 avatar 應建得起來");

            Assert.IsTrue(head.MirrorBody(body), "有本體就該鏡射得成功");   // 第一次接上(硬切)
            head.MirrorBody(body);                                          // 穩態:同一支 clip,不算換動作
            Assert.IsFalse(head.AvatarForTest.ClipSwitchPending, "接上之後不該留著待處理的切換");
        }

        private static void Destroy(GameObject a, GameObject b)
        {
            if (a != null) Object.DestroyImmediate(a);
            if (b != null) Object.DestroyImmediate(b);
        }

        [Test]
        public void Mirror_Follows_The_Body_HardCut_When_Told()
        {
            GameObject bodyGo = null, headGo = null;
            try
            {
                BuildRig(out bodyGo, out headGo, out var body, out var head, out _, out var watching);

                // 按「旁觀」→ SetLocalSeat 對本體做的事:硬切換到看戲姿勢。
                body.SnapNextClip();
                body.SetClip(watching);

                head.SnapMirrorPose();          // ← 呼叫端把「這次是硬切」轉告頭貼
                head.MirrorBody(body);

                Assert.AreSame(watching, head.AvatarForTest.CurrentClip, "頭貼該已經換到看戲姿勢");
                Assert.IsFalse(head.AvatarForTest.ClipSwitchPending,
                    "本體硬切了,頭貼卻留著待處理的切換 = 下一幀開始 0.5 秒混色(使用者:大頭貼的平滑沒關掉)");
                Assert.IsFalse(head.AvatarForTest.OneShotBlendPending,
                    "硬切用的一次性旗標不能留著 —— 會被下一次真的換動作(開始走路)吃掉");
            }
            finally { Destroy(bodyGo, headGo); }
        }

        [Test]
        public void Mirror_Without_The_Snap_Would_Crossfade()
        {
            // 反面對照:證明上面那條真的有在擋東西(而不是 ClipSwitchPending 永遠 false)。這也是修正前的行為。
            GameObject bodyGo = null, headGo = null;
            try
            {
                BuildRig(out bodyGo, out headGo, out var body, out var head, out _, out var watching);

                body.SnapNextClip();
                body.SetClip(watching);
                head.MirrorBody(body);          // 沒轉告 → 鏡射走 SetClip

                Assert.IsTrue(head.AvatarForTest.ClipSwitchPending,
                    "鏡射的 SetClip 就是會被當成換動作 —— 這條紅了代表混色判定改了,上面那條也就失效了");
            }
            finally { Destroy(bodyGo, headGo); }
        }

        [Test]
        public void Mirror_Still_Crossfades_Idle_To_Walk()
        {
            // 這個修正只針對「換 slot 的硬切」。走路 ↔ 待機仍然要平滑 —— 那是本體那邊也在混的。
            GameObject bodyGo = null, headGo = null;
            try
            {
                BuildRig(out bodyGo, out headGo, out var body, out var head, out _, out _);

                var walk = SdoRoomAvatar.LoadMot(SdoRoomAvatar.WalkMot);
                Assert.IsNotNull(walk);
                body.SetClip(walk);             // 開始走路(本體沒有 SnapNextClip → 混色)
                head.MirrorBody(body);

                Assert.IsTrue(head.AvatarForTest.ClipSwitchPending,
                    "走路是真的換動作,頭貼要跟著混 —— 全部改成硬切的話,idle↔walk 會變成一格跳過去");
            }
            finally { Destroy(bodyGo, headGo); }
        }
    }
}
