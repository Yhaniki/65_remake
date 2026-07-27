using System.IO;
using NUnit.Framework;
using UnityEngine;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// 動作 clip 的「物件同一性」—— 一條看不出來卻會被使用者看到的正確性規則。
    ///
    /// <see cref="SdoAvatar"/> 判斷「動作換了嗎」是比**物件參照**(MaybeStartBlend:<c>_mot == _lastMot</c>),
    /// 因為換動作要混色 0.5 秒。所以同一段 .MOT 只要被 parse 成兩個實例,它就會誤判成換了動作:
    /// 從當下顯示的姿勢混回迴圈 —— 而換裝重建 avatar 時「當下顯示的姿勢」正好是 FeetYAt 量腳留下的第 0 幀,
    /// 於是整個人往第 0 幀跳一下再慢慢混回來 = 使用者回報的「換衣服的時候動作會抖一下」。
    ///
    /// 換裝路徑天生會踩到:<see cref="SdoRoomAvatar.Build"/> 內部載一份 idle 當 RestMot,
    /// 房間的 ApplyOutfitMotion 又照穿搭載一份同樣的 idle 當 _idleMot —— 兩次 <see cref="SdoRoomAvatar.LoadMot"/>。
    /// 只要 LoadMot 回同一個實例就沒事,所以這裡守的就是那件事。
    /// </summary>
    public class AvatarClipIdentityTests
    {
        private static bool HaveData()
        {
            var probe = SdoAvatarBuilder.ResolveAvatarFile("AVATAR/900007_WOMAN_FACE.MSH");
            return !string.IsNullOrEmpty(probe) && File.Exists(probe);
        }

        [Test]
        public void LoadMot_Returns_The_Same_Instance_For_The_Same_Clip()
        {
            if (!HaveData()) Assert.Ignore("AVATAR data root not found — needs the game data (data_root.txt)");
            var a = SdoRoomAvatar.LoadMot(SdoRoomAvatar.IdleMot);
            var b = SdoRoomAvatar.LoadMot(SdoRoomAvatar.IdleMot);
            Assert.IsNotNull(a, "待機動作應載得到");
            Assert.AreSame(a, b, "同一個路徑必須回同一個 MotLoader —— 兩個實例會被 SdoAvatar 當成換了動作(見類別註解)");
        }

        [Test]
        public void LoadMot_Returns_Different_Instances_For_Different_Clips()
        {
            if (!HaveData()) Assert.Ignore("AVATAR data root not found");
            var idle = SdoRoomAvatar.LoadMot(SdoRoomAvatar.IdleMot);
            var walk = SdoRoomAvatar.LoadMot(SdoRoomAvatar.WalkMot);
            Assert.IsNotNull(walk);
            Assert.AreNotSame(idle, walk, "待機與走路是不同 clip → 必須是不同物件,否則 idle↔walk 的混色不會發生");
        }

        [Test]
        public void Rebuilt_Room_Avatar_Sees_Its_Own_Idle_As_The_Same_Clip()
        {
            // 這條是上面那條規則的**行為**版本:重演換裝重建的兩步(Build 載 RestMot → 依穿搭再載一次 idle 並 SetClip),
            // 然後用 IsRestPose(它就是在比 _mot == RestMot)確認 SdoAvatar 認得這是同一段動作。
            if (!HaveData()) Assert.Ignore("AVATAR data root not found");
            GameObject go = null;
            try
            {
                go = new GameObject("clipIdentityProbe");
                var av = SdoRoomAvatar.Build(go, 0, portraitOpaque: false, male: false, equippedParts: null);
                Assert.IsNotNull(av, "房間 avatar 應建得起來");
                Assert.IsNotNull(av.RestMot, "Build 應該把待機動作掛成 RestMot");

                av.SetClip(SdoRoomAvatar.LoadMot(SdoRoomAvatar.IdleMot));   // = ApplyOutfitMotion 那次載入
                Assert.IsTrue(av.IsRestPose,
                    "換裝重建後套回同一段待機動作 → 必須被當成同一個 clip;不然會多混一次 0.5 秒 crossfade(換衣服抖一下)");
            }
            finally { if (go != null) Object.DestroyImmediate(go); }
        }

        [Test]
        public void AutoLoopFrame_Stays_Inside_The_Clip()
        {
            // PrimeBlendFrom / 重建路徑都用 AutoLoopFrame 把顯示姿勢對回「畫面上原本那一格」。
            // 它必須落在 clip 範圍內 —— 越界的話 Pose 會夾在最後一幀,重建當下就定格。
            if (!HaveData()) Assert.Ignore("AVATAR data root not found");
            GameObject go = null;
            try
            {
                go = new GameObject("loopFrameProbe");
                var av = SdoRoomAvatar.Build(go, 0, portraitOpaque: false, male: false, equippedParts: null);
                Assert.IsNotNull(av);
                float f = av.AutoLoopFrame;
                Assert.GreaterOrEqual(f, 0f);
                Assert.Less(f, av.RestMot.MaxTime + 1f, "自動迴圈的幀不能超出 clip 長度");
            }
            finally { if (go != null) Object.DestroyImmediate(go); }
        }
    }
}
