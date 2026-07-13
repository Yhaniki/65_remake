using NUnit.Framework;
using Sdo.Game;
using UnityEngine;

namespace Sdo.Tests
{
    /// <summary>
    /// 房間大頭貼取景。核心不變量：**頭的大小/位置只由臉決定，換什麼髮型都一樣**（臉 mesh 每套裝扮都一樣）。
    /// 頭髮唯一能做的事是「頂到框」時把鏡頭往上挪，頭不會因此縮小。
    ///
    /// 座標取自實際執行期 renderer.bounds（log.txt 的 [headframe]，avatarScale 1.05、parkSpot 上）：
    ///   臉 900007_WOMAN_FACE  y 53.22 .. 63.67（臉高 10.45）
    ///   預設髮 900017_WOMAN_HAIR  髮頂 64.36（貼著頭皮）
    ///   037916_WOMAN_HAIR        髮頂 69.23（蓬 5.6，比顱頂高半顆臉）→ 舊算法相機遠 1.44 倍、頭縮小
    /// 注意：MSH 的 raw 頂點量不得（900017 是 scaled bone-local 綁定），只有執行期 bounds 是真的。
    /// </summary>
    public class RoomHeadPortraitFramingTests
    {
        private const float FrameDist = 1.9f, Zoom = 1f, AimUp = 0.25f, Fov = 45f;   // 男生那組（女生現在共用）
        private const float FaceTop = 63.67f, FaceBottom = 53.22f;
        private const float DefaultHairTop = 64.36f, PoofyHairTop = 69.23f;

        private static Bounds Face()
        {
            var b = new Bounds();
            b.SetMinMax(new Vector3(-4.82f, FaceBottom, -4.52f), new Vector3(4.82f, FaceTop, 5.34f));
            return b;
        }

        private static void Frame(bool hasHair, float hairTop, out Vector3 target, out float dist)
            => RoomHeadPortrait.ComputeFraming(Face(), hasHair, hairTop, FrameDist, Zoom, AimUp, Fov, out target, out dist);

        [Test]
        public void HeadSize_IsIdenticalForEveryHairstyle()   // ← 回報的 bug：蓬髮把相機推遠、頭縮小
        {
            Frame(true, DefaultHairTop, out _, out float bald);
            Frame(true, PoofyHairTop, out _, out float poofy);
            Frame(false, 0f, out _, out float noHair);

            Assert.AreEqual(bald, poofy, 1e-3f);    // 037916 蓬髮：相機距離跟預設髮完全相同 → 頭一樣大
            Assert.AreEqual(bald, noHair, 1e-3f);   // 連光頭都一樣
        }

        [Test]
        public void Calibration_ReproducesTheMaleDefaultFraming()
        {
            // 使用者說「男生預設頭貼的大小和位置剛好」，女生要比照。那個框 = 下巴 → 髮頂 71.13
            // （臉 58.05..68.81，臉高 10.76 → dist 13.08×1.9 = 24.85、aimY 61.32）。BoxTopPadFaces 就是照它訂的，
            // 且改成用臉高表示 → 髮型再也影響不到它。
            var maleFace = new Bounds();
            maleFace.SetMinMax(new Vector3(-4.85f, 58.05f, -4.5f), new Vector3(4.85f, 68.81f, 5.3f));
            RoomHeadPortrait.ComputeFraming(maleFace, false, 0f, FrameDist, Zoom, AimUp, Fov, out var target, out float dist);

            Assert.AreEqual(24.85f, dist, 0.05f);
            Assert.AreEqual(61.32f, target.y, 0.05f);
        }

        [Test]
        public void HairIsIgnoredByDefault_FrameNeverMoves()   // 預設 fitHairTop=false：大小＋位置都不看頭髮
        {
            var go = new GameObject("t");
            try { Assert.IsFalse(go.AddComponent<RoomHeadPortrait>().fitHairTop); }
            finally { Object.DestroyImmediate(go); }

            Frame(false, 0f, out var faceOnly, out float faceDist);      // hasHair=false ＝ 預設走的路徑
            Frame(true, PoofyHairTop, out var ignored, out float dist);  // (fitHairTop=true 時才會傳 hasHair=true)
            Assert.AreEqual(faceDist, dist, 1e-3f);
            Assert.AreNotEqual(faceOnly.y, ignored.y);                    // ← 開啟選項才會挪鏡頭
        }

        [Test]
        public void PoofyHair_WhenFittingIsEnabled_RaisesTheAim_NeverZoomsOut()
        {
            Frame(true, DefaultHairTop, out var baseTarget, out float baseDist);
            Frame(true, PoofyHairTop, out var target, out float dist);

            Assert.AreEqual(baseDist, dist, 1e-3f);              // 不退遠
            Assert.Greater(target.y, baseTarget.y);              // 改成把鏡頭往上挪
            float half = dist * Mathf.Tan(Fov * 0.5f * Mathf.Deg2Rad);
            Assert.LessOrEqual(PoofyHairTop, target.y + half);   // 髮頂進得了框（不會被切）
            Assert.GreaterOrEqual(FaceBottom, target.y - half);  // 下巴也還在框內
        }

        [Test]
        public void DefaultHair_LeavesTheAimWhereTheFacePutsIt()
        {
            Frame(true, DefaultHairTop, out var target, out _);
            Frame(false, 0f, out var faceOnly, out _);
            Assert.AreEqual(faceOnly.y, target.y, 0.2f);   // 貼頭皮的髮撐不到框 → 構圖＝純臉的構圖
        }

        [Test]
        public void UnfittableHair_KeepsTheBaselineFraming_NeverFlingsTheCameraAway()
        {
            Frame(false, 0f, out var baseTarget, out float baseDist);   // 純臉的基準構圖
            foreach (float top in new[] { 80f, 9999f })                 // 誇張高髮髻 / 離群頂點、飄帶
            {
                Frame(true, top, out var target, out float dist);
                Assert.AreEqual(baseDist, dist, 1e-3f);                 // 塞不下 → 也絕不退遠(頭不縮小)
                Assert.AreEqual(baseTarget.y, target.y, 1e-3f);         // 也不亂挪鏡頭(頭髮被切掉就算了)
            }
        }

        [Test]
        public void Aim_IsHorizontallyCentredOnTheFace()
        {
            Frame(true, PoofyHairTop, out var target, out _);
            Assert.AreEqual(Face().center.x, target.x, 1e-3f);   // 髮往兩側/後方掃 → 不可歪掉取景中心
            Assert.AreEqual(Face().center.z, target.z, 1e-3f);
        }
    }
}
