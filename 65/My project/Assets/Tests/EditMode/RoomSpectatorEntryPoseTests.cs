using System.IO;
using NUnit.Framework;
using UnityEngine;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// 「一進畫面就是最終姿勢」—— 剛生出來的房間角色不准混色。
    ///
    /// 症狀(使用者回報):進房間時,原本在旁觀的人會**從立正慢慢彎下去坐好**。
    /// 成因:<see cref="SdoRoomAvatar.Build"/> 內部一律掛站立 idle,旁觀者的 cat-0x21 看戲姿勢是
    /// 建好之後才套上去的;以前那一步走 <see cref="SdoAvatar.SetClip"/> → 下一次 LateUpdate 把它當成
    /// 「換了動作」→ 從站立姿勢混 <see cref="SdoAvatar.BlendSec"/>(0.5 秒)過去。
    /// 混色本身是對的(走路↔待機要平滑),錯的是**出生那一下**也混。
    ///
    /// 守的就是這件事:生成路徑要用 <see cref="SdoAvatar.PoseInitialClip"/>(硬切),
    /// 而 <see cref="SdoAvatar.ClipSwitchPending"/> 是「下一幀會不會混色」的觀測點。
    /// </summary>
    public class RoomSpectatorEntryPoseTests
    {
        private const int SpectatorSlot = 6;   // 第一個旁觀席(RoomLayout.SeatCount)

        private static bool HaveData()
        {
            var probe = SdoAvatarBuilder.ResolveAvatarFile("AVATAR/900007_WOMAN_FACE.MSH");
            return !string.IsNullOrEmpty(probe) && File.Exists(probe);
        }

        [Test]
        public void Spectator_Idle_Is_A_Different_Clip_From_The_Standing_Idle()
        {
            // 前提條件:兩者若是同一支 clip,下面兩條測試就什麼都沒測到(不會有切換,自然不會混色)。
            if (!HaveData()) Assert.Ignore("AVATAR data root not found — needs the game data (data_root.txt)");
            var seated = SdoRoomAvatar.LoadMot(RoomScene3D.ResolveIdleMot(0, male: false, parts: null));
            var watching = SdoRoomAvatar.LoadMot(RoomScene3D.ResolveIdleMot(SpectatorSlot, male: false, parts: null));
            Assert.IsNotNull(watching, "旁觀席的看戲姿勢(cat-0x21)應載得到");
            Assert.AreNotSame(seated, watching, "旁觀者的看戲姿勢必須是另一支 clip —— 一樣的話畫面上分不出誰在旁觀");
        }

        [Test]
        public void Spawning_A_Spectator_Arms_The_Watching_Pose_Without_A_Crossfade()
        {
            if (!HaveData()) Assert.Ignore("AVATAR data root not found");
            GameObject go = null;
            try
            {
                go = new GameObject("spectatorSpawnProbe");
                var av = SdoRoomAvatar.Build(go, 0, portraitOpaque: false, male: false, equippedParts: null);
                Assert.IsNotNull(av, "房間 avatar 應建得起來");

                var watching = SdoRoomAvatar.LoadMot(RoomScene3D.ResolveIdleMot(SpectatorSlot, male: false, parts: null));
                Assert.IsNotNull(watching);

                av.RestMot = watching;
                av.PoseInitialClip(watching);

                Assert.AreSame(watching, av.CurrentClip, "掛上去的就該是看戲姿勢");
                Assert.IsFalse(av.ClipSwitchPending,
                    "剛生出來的旁觀者不能留下待處理的動作切換 —— 那就是下一幀開始的 0.5 秒混色(立正慢慢彎下去坐好)");
            }
            finally { if (go != null) Object.DestroyImmediate(go); }
        }

        [Test]
        public void SetClip_On_A_Fresh_Avatar_Would_Crossfade()
        {
            // 反面對照:證明上面那條真的有在擋東西(而不是 ClipSwitchPending 永遠都是 false)。
            // 這也是修正前生成路徑的行為。
            if (!HaveData()) Assert.Ignore("AVATAR data root not found");
            GameObject go = null;
            try
            {
                go = new GameObject("spectatorSpawnProbeNaive");
                var av = SdoRoomAvatar.Build(go, 0, portraitOpaque: false, male: false, equippedParts: null);
                Assert.IsNotNull(av);

                var watching = SdoRoomAvatar.LoadMot(RoomScene3D.ResolveIdleMot(SpectatorSlot, male: false, parts: null));
                av.SetClip(watching);

                Assert.IsTrue(av.ClipSwitchPending,
                    "SetClip 就是會被當成換動作 —— 這條要是紅了,代表混色的判定改了,上面那條測試也就失效了");
            }
            finally { if (go != null) Object.DestroyImmediate(go); }
        }

        [Test]
        public void PoseInitialClip_Clears_A_Pending_One_Shot_Blend_Override()
        {
            // PoseInitialClip 是硬切 → 之前排好的一次性混色旗標(SnapNextClip / BlendNextClip)必須被清掉,
            // 否則它會留到下一次真的換動作(= 開始走路)才被消耗:走路那一下沒混色 / 混了奇怪的長度。
            if (!HaveData()) Assert.Ignore("AVATAR data root not found");
            GameObject go = null;
            try
            {
                go = new GameObject("blendFlagProbe");
                var av = SdoRoomAvatar.Build(go, 0, portraitOpaque: false, male: false, equippedParts: null);
                Assert.IsNotNull(av);

                var watching = SdoRoomAvatar.LoadMot(RoomScene3D.ResolveIdleMot(SpectatorSlot, male: false, parts: null));
                var walk = SdoRoomAvatar.LoadMot(SdoRoomAvatar.WalkMot);
                Assert.IsNotNull(walk);

                av.SnapNextClip();              // 有人先排了一次硬切
                av.PoseInitialClip(watching);   // 但生成這一步自己就硬切了 → 旗標該被清掉
                Assert.IsFalse(av.OneShotBlendPending,
                    "一次性的硬切旗標留著的話,會被下一次真的換動作(開始走路)吃掉 → 走路那一下沒有混色");

                av.SetClip(walk);               // 接著真的換動作(開始走路)
                Assert.IsTrue(av.ClipSwitchPending, "走路是真的換動作,必須被判定為切換(才會混色)");
            }
            finally { if (go != null) Object.DestroyImmediate(go); }
        }
    }
}
