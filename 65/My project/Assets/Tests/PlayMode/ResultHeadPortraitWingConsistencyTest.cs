using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// 線上結算的**遠端**頭貼:穿飛行翅膀的人,取景不可以跟別人不一樣。
    ///
    /// 使用者回報「線上模式遠端結算,如果有翅膀的人,頭貼相機位置高度會不一樣」。病灶不是翅膀的幾何(那件事
    /// 早就修過,見 HeadPortraitWingFramingTest),而是**姿勢**:穿飛行翅膀的人,RoomHeadPortrait 的鏡射 idle
    /// 會換成 flystay(浮空前傾),而取景是拿「idle 第 0 幀」量臉框算出來的 —— 姿勢不同 → 臉框不同 →
    /// 相機高度/距離就跟沒穿翅膀的人不一樣。結算列因此改成 boneFraming + groundClipsOnly(與本機那一列同一條路)。
    ///
    /// Run: -runTests -testPlatform PlayMode -testFilter Sdo.Tests.ResultHeadPortraitWingConsistencyTest
    /// </summary>
    public class ResultHeadPortraitWingConsistencyTest
    {
        private const string FlyingWingMesh = "AVATAR/008448_WOMAN_CHIBANG.MSH";   // 反編譯硬編的會飛翅膀(0x2100)

        private static string[] PartsWithWing()
        {
            var parts = new List<string>(SdoRoomAvatar.DefaultParts(male: false)) { FlyingWingMesh };
            return parts.ToArray();
        }

        private static void RequireWing()
        {
            string p = SdoAvatarBuilder.ResolveAvatarFile(FlyingWingMesh);
            if (string.IsNullOrEmpty(p) || !File.Exists(p))
                Assert.Ignore("DATA 沒有 " + FlyingWingMesh + " — 跳過真實資產驗證");
            Assert.IsTrue(SpecialMotionItems.WearsFlyingWing(PartsWithWing()), "這件翅膀不算會飛的 → 換一件來測");
        }

        /// <summary>結算列用的待機 clip = **舞台**待機(本機那一列 BuildIdleHeadAvatar 用的同一支)。
        /// 不是大廳待機(WREST0056)—— 那會讓同一排頭像裡只有本機那格動作不一樣。</summary>
        private const string StageRestFemale = "MOTION/WREST0072.MOT";

        /// <summary>照 ScreenGameplay.AttachResultHeadPortraits 的設定建一個結算用頭貼。</summary>
        private static RoomHeadPortrait BuildResultPortrait(GameObject host, string[] parts)
        {
            var hp = host.AddComponent<RoomHeadPortrait>();
            hp.layer = 11;
            hp.parkSpot = new Vector3(5200f, 0f, 5200f);
            hp.fov = 45f;
            hp.pitchDeg = 2.3f;
            hp.yaw = 30f;
            hp.avatarScale = 1.05f;
            hp.zoom = 1f;
            hp.boneFraming = true;                 // ← 修正:只對頭骨取景
            hp.boneAimOffset = new Vector3(-2f, HeadBoneFraming.AimUpModel, 0f);
            hp.boneDistModel = HeadBoneFraming.DistModel;
            hp.groundClipsOnly = true;             // ← 修正:不演飛行 idle
            hp.idleMotOverride = StageRestFemale;  // ← 修正:與本機那一列同一支舞台待機
            hp.fitHairTop = false;
            hp.rtWidth = 192; hp.rtHeight = 216;
            Assert.IsTrue(hp.Init(male: false, avatarParts: parts), "頭貼建不起來(DATA 缺件?)");
            return hp;
        }

        private static Transform CamOf(GameObject host)
        {
            var cam = host.transform.Find("RoomHeadPortraitCam");
            Assert.IsNotNull(cam, "找不到頭貼相機");
            return cam;
        }

        [UnityTest]
        public IEnumerator ResultPortrait_WingedAndBare_ShareTheSameCamera()
        {
            RequireWing();
            var bare = new GameObject("resultHeadBare");
            var winged = new GameObject("resultHeadWinged");
            try
            {
                BuildResultPortrait(bare, SdoRoomAvatar.DefaultParts(male: false));
                BuildResultPortrait(winged, PartsWithWing());
                for (int i = 0; i < 12; i++) yield return null;   // 等蒙皮 + 幾輪 LateUpdate

                Vector3 a = CamOf(bare).position, b = CamOf(winged).position;
                Debug.Log(string.Format("[result-head] bare={0} winged={1} Δ={2:F4}", a, b, (a - b).magnitude));
                // 同一副骨架 + 同一支地面 idle + 只對頭骨取景 → 兩台相機必須在同一個地方(高度尤其是回報的症狀)。
                Assert.AreEqual(a.y, b.y, 1e-3f, "穿翅膀的人頭貼相機高度不一樣(使用者回報的症狀)");
                Assert.AreEqual(a.x, b.x, 1e-3f);
                Assert.AreEqual(a.z, b.z, 1e-3f);
            }
            finally { Object.DestroyImmediate(bare); Object.DestroyImmediate(winged); }
        }

        /// <summary>
        /// 結算列的頭貼取景 = 「**舞台**待機第 0 幀的頭骨」+ 那組固定的距離/瞄準偏移 —— 與本機那一列
        /// (ScreenGameplay.BuildIdleHeadAvatar + UpdateHeadPortraitCam)完全同一條算式。
        /// 這條擋的是「只改一邊」:遠端那幾格若退回大廳待機(WREST0056),量到的頭骨位置就不一樣了。
        /// </summary>
        [UnityTest]
        public IEnumerator ResultPortrait_FramesTheStageIdlePose_LikeTheLocalRow()
        {
            var parts = SdoRoomAvatar.DefaultParts(male: false);
            var reference = new GameObject("referenceAvatar");
            var host = new GameObject("resultHeadRef");
            try
            {
                // 參考值:同一副骨架擺在舞台待機第 0 幀,拿頭骨算出「本機那一列」會用的相機位置。
                var av = SdoRoomAvatar.Build(reference, 11, portraitOpaque: true, male: false,
                                             equippedParts: parts, bodyIndex: 0);
                Assert.IsNotNull(av, "參考 avatar 建不起來(DATA 缺件?)");
                var stageIdle = SdoRoomAvatar.LoadMot(StageRestFemale);
                if (stageIdle == null) Assert.Ignore("DATA 沒有 " + StageRestFemale + " — 跳過");
                av.DanceEnabled = () => false; av.DanceTimeSec = () => -1f;
                for (int i = 0; i < 8; i++) yield return null;
                av.SetClip(stageIdle); av.PoseFrame(0f);
                reference.transform.position = new Vector3(5200f, 0f, 5200f);
                reference.transform.localScale = Vector3.one * 1.05f;
                reference.transform.localRotation = Quaternion.Euler(0f, 30f, 0f);
                Vector3 headWorld = reference.transform.TransformPoint(av.BoneModelPos("Bip01_Head"));
                HeadBoneFraming.Compute(headWorld, 1.05f, 1f, new Vector3(-2f, HeadBoneFraming.AimUpModel, 0f),
                                        HeadBoneFraming.DistModel, out var target, out float dist);
                Vector3 dir = Quaternion.Euler(2.3f, 0f, 0f) * Vector3.forward;
                Vector3 expected = target - dir * dist;

                BuildResultPortrait(host, parts);
                for (int i = 0; i < 12; i++) yield return null;
                Vector3 actual = CamOf(host).position;
                Debug.Log(string.Format("[result-head] 舞台待機參考={0} 實際={1} Δ={2:F4}",
                                        expected, actual, (expected - actual).magnitude));
                Assert.AreEqual(0f, (expected - actual).magnitude, 0.01f,
                    "結算頭貼的取景不是照舞台待機的頭骨算的(idleMotOverride 掉了?)");
            }
            finally { Object.DestroyImmediate(reference); Object.DestroyImmediate(host); }
        }

        /// <summary>
        /// 病灶存證:同樣兩個人,改用**房間**那組設定(量臉框 + 允許飛行 clip),相機高度就是會不一樣。
        /// 這條測試會隨資產而動,所以只驗「有明顯差」這件事本身 —— 它是上面那條修正的理由。
        /// </summary>
        [UnityTest]
        public IEnumerator RoomStyleFraming_MovesTheCameraForWingWearers()
        {
            RequireWing();
            var bare = new GameObject("roomHeadBare");
            var winged = new GameObject("roomHeadWinged");
            try
            {
                foreach (var (go, parts) in new[] { (bare, SdoRoomAvatar.DefaultParts(male: false)),
                                                    (winged, PartsWithWing()) })
                {
                    var hp = go.AddComponent<RoomHeadPortrait>();
                    hp.layer = 11;
                    hp.parkSpot = new Vector3(5200f, 0f, 5200f);
                    hp.pitchDeg = 2.3f; hp.yaw = 30f; hp.avatarScale = 1.05f;
                    hp.headFrameDist = 1.9f; hp.headAimUp = 0.25f; hp.fitHairTop = false;
                    Assert.IsTrue(hp.Init(male: false, avatarParts: parts), "頭貼建不起來(DATA 缺件?)");
                }
                for (int i = 0; i < 12; i++) yield return null;

                Vector3 a = CamOf(bare).position, b = CamOf(winged).position;
                Debug.Log(string.Format("[result-head] (舊路徑) bare={0} winged={1} Δy={2:F3}", a, b, a.y - b.y));
                Assert.Greater(Mathf.Abs(a.y - b.y), 0.05f,
                    "舊路徑下穿翅膀的人相機高度竟然一樣 → 病灶的診斷要重看(flystay 姿勢應該把臉框挪掉)");
            }
            finally { Object.DestroyImmediate(bare); Object.DestroyImmediate(winged); }
        }
    }
}
