using System.IO;
using NUnit.Framework;
using Sdo.Game;
using UnityEngine;

namespace Sdo.Tests
{
    /// <summary>
    /// REAL-DATA guard for the 飛行翅膀 hover. 使用者回報:「穿戴會飛的翅膀,飛起來靜止(idle)的時候高度沒有提升,只有
    /// 移動的時候有提升」。兩個根因,這個檔案釘住的是第二個:
    ///   1. 懸浮被 gate 在「有沒有在移動」上。官方兩處都只看飛行旗標 —— Player_UpdateTransform_004ab4a0 028:2614
    ///      (每幀,完全沒有移動條件) 與 Player_StepMovement 028:2852 → <see cref="SpecialMotionItems.HoverY"/>
    ///      (純邏輯,測在 SpecialMotionItemsTests)。
    ///   2. 「flystay 這個 clip 自己會浮」的錯誤假設 —— 這裡用真的 HRC/MOT 走 production FK
    ///      (<see cref="HrcLoader"/> → <see cref="MotLoader"/> → <see cref="SdoAvatar"/>.Pose) 量出來:FLYSTAY 相對
    ///      地面站姿只把腳抬高個位數單位,遠不足以看成離地,所以那個常駐 +10 不能省。
    ///
    /// 同時守住第三件事:地板校正 (<c>RoomScene3D.GroundFeetY</c>) 一定要拿地面站姿量。飛行 clip 的腳是收起來的,
    /// 拿它量到的「腳底」再減掉,等於把浮空整個抵消 —— 這就是為什麼原本靜止時完全貼地。
    /// </summary>
    public class FlyHoverRealDataTests
    {
        /// <summary>One gender's clip set: skeleton + 地面站姿 idle + 飛行 idle(flystay) + 飛行移動(glide)。</summary>
        public struct Row
        {
            public string Label, Hrc, GroundIdle, FlyIdle, FlyGlide;
            public override string ToString() => Label;
        }

        private static readonly Row[] Rows =
        {
            new Row { Label = "female", Hrc = AvatarOutfit.FemaleHrc, GroundIdle = SdoRoomAvatar.IdleMot,
                      FlyIdle = SpecialMotionItems.FlyIdleFemale, FlyGlide = SpecialMotionItems.FlyWalkFemale },
            new Row { Label = "male",   Hrc = AvatarOutfit.MaleHrc,   GroundIdle = SdoRoomAvatar.MaleIdleMot,
                      FlyIdle = SpecialMotionItems.FlyIdleMale,   FlyGlide = SpecialMotionItems.FlyWalkMale },
        };

        private static System.Collections.Generic.IEnumerable<Row> AllRows() => Rows;

        // The foot chain — its lowest bone is what "how high off the floor is this pose" means. (FeetYAt uses the
        // lowest skinned VERTEX; bones need no mesh, and the two move together.)
        private static readonly string[] FootBones =
            { "Bip01_L_Foot", "Bip01_R_Foot", "Bip01_L_Toe0", "Bip01_R_Toe0" };

        private static HrcLoader LoadHrc(string rel)
        {
            var path = SdoAvatarBuilder.ResolveAvatarFile(rel);
            return string.IsNullOrEmpty(path) || !File.Exists(path) ? null : HrcLoader.Load(File.ReadAllBytes(path));
        }

        /// <summary>Lowest foot-bone Y (model space) of <paramref name="clip"/>'s frame 0, through the production FK.</summary>
        private static float FootY(SdoAvatar av, MotLoader clip)
        {
            av.SetClip(clip);
            av.PoseFrame(0f);
            float y = float.PositiveInfinity;
            foreach (var b in FootBones)
                if (av.BoneIndex(b) >= 0) y = Mathf.Min(y, av.BoneModelPos(b).y);
            Assert.IsFalse(float.IsInfinity(y), "no foot bone in the skeleton");
            return y;
        }

        [Test, TestCaseSource(nameof(AllRows))]
        public void FlystayClip_DoesNotLiftBodyByItself_SoTheConstantHoverIsRequired(Row row)
        {
            var hrc = LoadHrc(row.Hrc);
            var ground = SdoRoomAvatar.LoadMot(row.GroundIdle);
            var flyIdle = SdoRoomAvatar.LoadMot(row.FlyIdle);
            var flyGlide = SdoRoomAvatar.LoadMot(row.FlyGlide);
            if (hrc == null || ground == null || flyIdle == null || flyGlide == null)
                Assert.Ignore("avatar/motion data not found — real-data rows need the game data (data_root.txt)");

            var go = new GameObject("flyhover_probe");
            try
            {
                var av = go.AddComponent<SdoAvatar>();
                av.Setup(hrc, ground);
                float groundY = FootY(av, ground);
                float idleLift = FootY(av, flyIdle) - groundY;    // flystay 相對站姿抬多少
                float glideLift = FootY(av, flyGlide) - groundY;   // fly(移動) 相對站姿抬多少

                // flystay 幾乎沒抬 (實測 female ≈ 3.4、male ≈ 1.2,對照身高 ~50) → 靜止時的離地感全靠常駐 +10。
                // 若哪天有人「反正 flystay 自己會浮」而把 HoverY 拿掉,這條會紅。
                Assert.Less(idleLift, SpecialMotionItems.FlyHoverY * 0.5f,
                    $"{row.Label}: flystay 抬了 {idleLift:F2},若它真能撐起浮空就得重新檢討常駐 hover 的必要性");
                Assert.GreaterOrEqual(idleLift, -1f, $"{row.Label}: flystay 不該比站姿還低 ({idleLift:F2})");
                // 移動用的 glide 才是真的把腳收起來的 clip — 這就是「只有移動時看起來有提升」的另一半原因。
                Assert.Greater(glideLift, idleLift + 5f,
                    $"{row.Label}: glide 抬 {glideLift:F2} 應明顯高於 flystay 的 {idleLift:F2}");
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
