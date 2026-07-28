using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Game;
using UnityEngine;
using UnityEngine.TestTools;

namespace Sdo.Tests
{
    /// <summary>
    /// 房間頭貼相機 vs 飛行前傾 —— 使用者回報:「飛行的時候朝前飛,身體會大幅前傾,就會很貼近相機變得頭很大;
    /// camera 應該跟頭維持一定距離,但是要有一點 filter,身體小幅晃動的時候不應該一直跟著晃。」
    ///
    /// 這兩件事拿假資料測不出來,一定要餵**真的 MOT**(EditMode 的 HeadPortraitAimFilterTests 只驗濾波器的數學)。
    /// 實測(女生預設穿搭 + 008448 翅膀,模型單位;死區 = 0.3 個臉高 ≈ 3.14):
    ///     待機擺動 1.39 / 走路擺動 1.61 / 飛行待機擺動 0.41   ← 全在死區內 → 相機完全不動
    ///     FLY_NV 滑翔  headRel z[-11.10,-10.93]              ← 前傾把頭「固定」往相機挪 11 個單位(幾乎不擺動)
    /// 也就是說症狀不是抖動而是**持續偏移**:相機距離約 25,被吃掉 11 → 頭在框裡放大約 1.8 倍。所以濾波器要:
    /// 死區擋掉 1.6 以下的擺動,慢爬把 11 這種一直偏同一邊的位移整個吃掉(實測殘餘 0.17,相機自己只晃 ±0.29)。
    ///
    /// Run: -runTests -testPlatform PlayMode -testFilter Sdo.Tests.RoomHeadPortraitFlyFramingTest
    /// </summary>
    public class RoomHeadPortraitFlyFramingTest
    {
        private const string FlyingWingMesh = "AVATAR/008448_WOMAN_CHIBANG.MSH";   // 反編譯硬編的會飛翅膀(0x2100)
        private const string HeadBone = "Bip01_Head", RootBone = "Bip01";

        // 死區(模型單位)= 0.3 個臉高;臉高實測 10.45(女)。RoomHeadPortrait 用凍結當下量到的臉高當尺標。
        private const float FaceH = 10.45f;
        private static float DeadZone => HeadPortraitAimFilter.DefaultDeadZoneFaces * FaceH;

        private static string[] PartsWithWing()
        {
            var parts = new List<string>(SdoRoomAvatar.DefaultParts(male: false)) { FlyingWingMesh };
            return parts.ToArray();
        }

        private static Vector3 HeadRelRoot(SdoAvatar av, MotLoader clip, float frame)
        {
            av.SetClip(clip);
            av.PoseFrame(frame);
            return av.BoneModelPos(HeadBone) - av.BoneModelPos(RootBone);
        }

        /// <summary>整段 clip 裡「頭相對 root」離基準最遠多少(模型單位)。</summary>
        private static float MaxHeadOffset(SdoAvatar av, MotLoader clip, Vector3 baseRel)
        {
            float max = 0f;
            for (float f = 0f; f <= clip.MaxTime; f += 1f)
                max = Mathf.Max(max, (HeadRelRoot(av, clip, f) - baseRel).magnitude);
            return max;
        }

        /// <summary>把真的 clip 循環餵給真的濾波器,量穩態下的兩件事。</summary>
        private struct SimResult
        {
            public float Shortened;   // 相機沒補到、被吃掉的距離(模型單位)→ 頭在框裡放大多少
            public float Travel;      // 後 5 秒相機自己總共移動的距離 → 會不會「跟著晃」
            public float Excursion;   // 相機自己移動的最大幅度
            public override string ToString()
                => string.Format("短{0:F2} 走{1:F2} 幅{2:F2}", Shortened, Travel, Excursion);
        }

        /// <summary>相機看 +Z:頭往 −Z 挪就是朝相機衝過來,沒補到多少距離就短多少(頭的大小 ∝ 1/距離)。</summary>
        private static SimResult Sim(SdoAvatar av, MotLoader clip, Vector3 baseRel, float deadZone = -1f)
        {
            if (deadZone < 0f) deadZone = DeadZone;
            var follow = Vector3.zero;
            var r = new SimResult();
            float lo = float.MaxValue, hi = float.MinValue;
            for (int i = 0; i < 600; i++)   // 10 秒(clip 30fps,更新 60fps)
            {
                var raw = HeadRelRoot(av, clip, (i * 0.5f) % (clip.MaxTime + 1f)) - baseRel;
                var next = HeadPortraitAimFilter.Step(follow, raw, deadZone, HeadPortraitAimFilter.DefaultSmoothSec,
                                                      HeadPortraitAimFilter.DefaultCreepSec, 1f / 60f);
                if (i >= 300)   // 前 5 秒讓它追上去,只量穩態
                {
                    r.Travel += (next - follow).magnitude;
                    r.Shortened = Mathf.Max(r.Shortened, follow.z - raw.z);
                    lo = Mathf.Min(lo, next.z); hi = Mathf.Max(hi, next.z);
                }
                follow = next;
            }
            r.Excursion = hi - lo;
            return r;
        }

        private static MotLoader Mot(string rel)
        {
            var m = SdoRoomAvatar.LoadMot(rel);
            if (m == null || m.MaxTime <= 0f) Assert.Ignore("DATA 沒有 " + rel + " — 跳過真實資產驗證");
            return m;
        }

        [UnityTest]
        public IEnumerator RealClips_TheLeanIsFollowedAndTheSwayIsNot()
        {
            var go = new GameObject("FlyHeadAvatar");
            try
            {
                var av = SdoRoomAvatar.Build(go, 11, portraitOpaque: true, male: false,
                                             equippedParts: PartsWithWing(), bodyIndex: 0);
                Assert.IsNotNull(av, "avatar 建不起來(DATA 缺件?)");
                av.DanceEnabled = () => false;
                av.DanceTimeSec = () => -1f;
                for (int i = 0; i < 8; i++) yield return null;   // 等 CPU skinning

                var groundIdle = Mot(SdoRoomAvatar.IdleMot);
                var groundWalk = Mot(SdoRoomAvatar.WalkMot);
                var flyIdle = Mot(SpecialMotionItems.FlyIdleFemale);
                var flyGlide = Mot(SpecialMotionItems.FlyWalkFemale);

                // 取景基準 = 待機第 0 幀(RoomHeadPortrait.TryFreezeFraming 就是在這個姿勢凍結的;穿翅膀時是 flystay)。
                Vector3 groundBase = HeadRelRoot(av, groundIdle, 0f);
                Vector3 flyBase = HeadRelRoot(av, flyIdle, 0f);
                float idleSway = MaxHeadOffset(av, groundIdle, groundBase);
                float walkSway = MaxHeadOffset(av, groundWalk, groundBase);
                float flyIdleSway = MaxHeadOffset(av, flyIdle, flyBase);
                float glideLean = MaxHeadOffset(av, flyGlide, flyBase);

                var glide = Sim(av, flyGlide, flyBase);
                var idle = Sim(av, groundIdle, groundBase);
                var walk = Sim(av, groundWalk, groundBase);
                // 對照組:死區關掉 = 相機一路跟著頭跑 → 它的幅度就是「頭自己在 z 上晃了多少」。
                var idleRaw = Sim(av, groundIdle, groundBase, deadZone: 0f);
                var walkRaw = Sim(av, groundWalk, groundBase, deadZone: 0f);
                Debug.Log(string.Format(
                    "[head-aim] deadZone={0:F2} idleSway={1:F2} walkSway={2:F2} flyIdleSway={3:F2} glideLean={4:F2}"
                    + "\n           glide[{5}] idle[{6}] walk[{7}] | 對照 idle[{8}] walk[{9}]",
                    DeadZone, idleSway, walkSway, flyIdleSway, glideLean, glide, idle, walk, idleRaw, walkRaw));

                // 1) 回報的 bug 真的存在:滑翔把頭「持續」往相機挪掉一大截(遠大於死區),取景點凍結就守不住距離。
                Assert.Greater(glideLean, 2f * DeadZone,
                    "FLY 滑翔沒有把頭挪很遠 → 這個測試量錯東西了(換一支真的前傾的 clip)");

                // 2) 待機/走路的擺動小於死區 → 濾波器不會被它帶著跑。
                Assert.Less(idleSway, DeadZone, "待機擺動超出死區 → 頭貼會跟著晃(死區要放大)");
                Assert.Less(walkSway, DeadZone, "走路擺動超出死區 → 頭貼會跟著晃(死區要放大)");
                Assert.Less(flyIdleSway, DeadZone, "飛行待機擺動超出死區 → 頭貼會跟著晃");

                // 3) 濾波後:飛行的距離幾乎完全補回來(實測殘餘 0.17,相對 ~25 的相機距離 <1%)。
                Assert.Less(glide.Shortened, 1f, "滑翔時相機到頭的距離沒補回來 → 頭在框裡爆大(回報的症狀)");

                // 4) 但待機/走路時相機幾乎不動 —— 「小幅晃動不應該一直跟著晃」。兩個門檻:
                //    (a) 絕對值小到看不見:實測待機 0.28 / 走路 0.06 個模型單位,取景框高約 12.7 → 2% 以下;
                //    (b) 相對於「頭自己在 z 上晃了多少」(對照組)還要更小 → 擺動仍然是在框內演出,不是被相機抵銷掉。
                //        走路的對照組只有 0.09(WWALK 原地走,頭在 z 上根本不動)—— 沒東西可留就只驗絕對值。
                foreach (var p in new[] { new[] { idle.Excursion, idleRaw.Excursion },
                                          new[] { walk.Excursion, walkRaw.Excursion } })
                {
                    Assert.Less(p[0], 0.05f * FaceH, "相機自己晃得看得出來了");
                    if (p[1] > 0.3f)
                        Assert.Less(p[0], 0.6f * p[1], "相機把頭的擺動追掉了 —— 擺動應該留在框內演出");
                }
            }
            finally { Object.DestroyImmediate(go); }
        }

        [UnityTest]
        public IEnumerator WhileGliding_TheCameraKeepsItsDistanceToTheHead()
        {
            var host = new GameObject("RoomHeadPortraitHost");
            try
            {
                var hp = host.AddComponent<RoomHeadPortrait>();
                bool walking = false;
                hp.WalkingProvider = () => walking;
                hp.FacingProvider = () => 0f;
                Assert.IsTrue(hp.Init(male: false, avatarParts: PartsWithWing()), "頭貼建不起來(DATA 缺件?)");

                for (int i = 0; i < 10; i++) yield return null;   // 等蒙皮 + 取景凍結

                var cam = host.transform.Find("RoomHeadPortraitCam");
                var avatar = host.GetComponentInChildren<SdoAvatar>();
                Assert.IsNotNull(cam, "找不到頭貼相機");
                Assert.IsNotNull(avatar, "找不到頭貼 avatar");

                float HeadDist()
                {
                    Vector3 head = avatar.transform.TransformPoint(avatar.BoneModelPos(HeadBone));
                    return Vector3.Distance(cam.position, head);
                }

                float restDist = HeadDist();
                Assert.Greater(restDist, 1f, "距離量出來是 0 → 取景還沒凍結");

                walking = true;                                        // → FLY_NV 前傾滑翔
                yield return new WaitForSecondsRealtime(6f);           // 快追 0.2s + 慢爬 1.5s,給足穩定時間

                float minDist = float.PositiveInfinity, maxDist = 0f;
                float until = Time.realtimeSinceStartup + 1f;
                while (Time.realtimeSinceStartup < until)
                {
                    yield return null;
                    float d = HeadDist();
                    minDist = Mathf.Min(minDist, d);
                    maxDist = Mathf.Max(maxDist, d);
                }
                Debug.Log(string.Format("[head-aim] restDist={0:F2} glide min={1:F2} max={2:F2}(頭放大 {3:F2}倍)",
                                        restDist, minDist, maxDist, restDist / minDist));

                // 頭在框裡的大小 ∝ 1/距離。修之前是 25→14(放大 1.8 倍),現在距離要守在 5% 以內。
                Assert.Greater(minDist, restDist * 0.95f,
                    "滑翔時相機到頭的距離被吃掉 → 頭在框裡爆大(這就是回報的症狀)");
                Assert.Less(maxDist, restDist * 1.05f, "滑翔時相機反而退太遠");
            }
            finally { Object.DestroyImmediate(host); }
        }
    }
}
