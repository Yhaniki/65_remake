using NUnit.Framework;
using UnityEngine;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// 頭上泡搬進房間相機之後的定位數學。
    ///
    /// 這裡守的是一句話:**泡只該多一個「會被前面的人擋住」的性質,其他什麼都不准變。**
    /// 螢幕位置與大小都不能動 —— 而那兩件事完全由 <see cref="RoomBubbleWorldAnchor.Solve"/> 的
    /// Position/Scale 決定,眼睛很難分辨「差了 3%」,所以用數學把它釘死。
    /// </summary>
    public class RoomBubbleWorldAnchorTests
    {
        // 房間相機的實際參數(RoomScene3D.BuildCamera):fov 45、near 5、aspect 釘 4:3。
        private const float Fov = 45f, Near = 5f, DesignH = 600f;
        private static float M11 => 1f / Mathf.Tan(Fov * 0.5f * Mathf.Deg2Rad);

        private static Camera MakeCam()
        {
            var go = new GameObject("bubbleAnchorTestCam");
            var cam = go.AddComponent<Camera>();
            cam.orthographic = false;
            cam.fieldOfView = Fov;
            cam.nearClipPlane = Near;
            cam.farClipPlane = 7500f;
            cam.aspect = 4f / 3f;
            cam.transform.position = new Vector3(10f, 120f, -300f);
            cam.transform.rotation = Quaternion.Euler(8f, 15f, 0f);
            cam.enabled = false;
            return cam;
        }

        [Test]
        public void Scale_Makes_600_Design_Px_Fill_The_View_Height()
        {
            // 「泡不隨距離變大變小」就是這條:在距離 d 處,600 設計 px 必須剛好等於相機看得到的世界高度。
            // 錯了的話遠處的人的泡會變小(看起來像 3D 物件),而使用者要的只是「被擋住」。
            foreach (float d in new[] { 50f, 200f, 800f, 3000f })
            {
                var p = RoomBubbleWorldAnchor.Solve(Vector3.zero, Vector3.forward, M11, Near,
                                                    new Vector3(0f, 0f, d), 0f, DesignH);
                Assert.IsTrue(p.Valid, "d=" + d);
                float viewWorldHeight = 2f * d * Mathf.Tan(Fov * 0.5f * Mathf.Deg2Rad);
                Assert.AreEqual(viewWorldHeight, p.Scale * DesignH, viewWorldHeight * 1e-4f,
                    "d=" + d + ":600 設計 px 應剛好填滿該距離的視野高度");
            }
        }

        [Test]
        public void Position_Projects_To_Exactly_The_Anchor_Point()
        {
            // 「螢幕位置不變」就是這條:把平面往相機拉近(depthBias)只准改深度,**不准改投影點**。
            // 差一點點就是泡整體偏移幾像素,而且會隨人走動而變化 —— 眼睛只會覺得「泡有點飄」,查不到原因。
            var cam = MakeCam();
            try
            {
                var anchor = new Vector3(-40f, 150f, 260f);
                Vector3 vpAnchor = cam.WorldToViewportPoint(anchor);
                foreach (float bias in new[] { 0f, 5f, 25f, 60f })
                {
                    var p = RoomBubbleWorldAnchor.Solve(cam.transform.position, cam.transform.forward,
                                                        cam.projectionMatrix.m11, cam.nearClipPlane,
                                                        anchor, bias, DesignH);
                    Assert.IsTrue(p.Valid, "bias=" + bias);
                    Vector3 vpPlane = cam.WorldToViewportPoint(p.Position);
                    Assert.AreEqual(vpAnchor.x, vpPlane.x, 1e-4f, "bias=" + bias + ":投影 x 不該動");
                    Assert.AreEqual(vpAnchor.y, vpPlane.y, 1e-4f, "bias=" + bias + ":投影 y 不該動");
                    Assert.Less(vpPlane.z, vpAnchor.z + 1e-3f, "bias 應該把平面往相機拉近(深度變小)");
                }
            }
            finally { Object.DestroyImmediate(cam.gameObject); }
        }

        [Test]
        public void Camera_Matrix_M11_Matches_The_Fov_Formula()
        {
            // Solve 吃的是 projectionMatrix.m11 而不是 fieldOfView(房間相機每幀釘 aspect,矩陣才是真相)。
            // 這條確認兩者在正常情況下一致 —— 哪天 Unity 改了矩陣慣例,這裡會先紅。
            var cam = MakeCam();
            try { Assert.AreEqual(M11, cam.projectionMatrix.m11, 1e-4f); }
            finally { Object.DestroyImmediate(cam.gameObject); }
        }

        [Test]
        public void Anchor_Behind_Or_Too_Close_To_The_Camera_Is_Invalid()
        {
            // 🔴 d ≤ 0 會讓 Scale 變負 → 泡以鏡像形式出現在相機後面(一團亂圖)。
            foreach (var anchor in new[]
                     {
                         new Vector3(0f, 0f, -100f),   // 相機正後方
                         new Vector3(0f, 0f, 0f),      // 相機原點
                         new Vector3(0f, 0f, Near),    // 貼在近裁面
                         new Vector3(0f, 0f, Near + 0.5f),
                     })
                Assert.IsFalse(RoomBubbleWorldAnchor.Solve(Vector3.zero, Vector3.forward, M11, Near,
                                                           anchor, 0f, DesignH).Valid, "anchor=" + anchor);
        }

        [Test]
        public void A_Huge_Bias_Is_Clamped_Instead_Of_Crossing_The_Camera()
        {
            // bias 由 renderer bounds 算出來,理論上不會大於距離;但錯一次的後果是負縮放,所以夾住。
            var p = RoomBubbleWorldAnchor.Solve(Vector3.zero, Vector3.forward, M11, Near,
                                                new Vector3(0f, 0f, 30f), 10000f, DesignH);
            Assert.IsTrue(p.Valid, "夾住之後仍然要畫得出來");
            Assert.Greater(p.Scale, 0f, "縮放必須是正的");
            Assert.GreaterOrEqual(p.Position.z, Near, "不可以被推到近裁面前面");
        }

        [Test]
        public void Garbage_Inputs_Do_Not_Produce_A_Bubble()
        {
            // NaN 進了 Transform 會污染整棵 hierarchy(泡的字會整片消失),寧可這一幀不畫。
            Assert.IsFalse(RoomBubbleWorldAnchor.Solve(Vector3.zero, Vector3.forward, M11, Near,
                                                       new Vector3(float.NaN, 0f, 100f), 0f, DesignH).Valid);
            Assert.IsFalse(RoomBubbleWorldAnchor.Solve(Vector3.zero, Vector3.forward, 0f, Near,
                                                       new Vector3(0f, 0f, 100f), 0f, DesignH).Valid,
                "m11=0(正交相機)沒有這個換算 → 不畫");
            Assert.IsFalse(RoomBubbleWorldAnchor.Solve(Vector3.zero, Vector3.forward, M11, Near,
                                                       new Vector3(0f, 0f, 100f), 0f, 0f).Valid);
        }

        [Test]
        public void The_Plane_Is_Fronto_Parallel_So_A_Design_Px_Offset_Stays_A_Design_Px_Offset()
        {
            // 泡的排版仍在 800×600 設計 px 裡算(鏈物理一行都沒改),所以「偏移 N 設計 px」必須在螢幕上
            // 也剛好是 N 設計 px —— 這條把「平面正對相機 + Scale 正確」兩件事一起驗完。
            var cam = MakeCam();
            try
            {
                var anchor = new Vector3(-40f, 150f, 260f);
                var p = RoomBubbleWorldAnchor.Solve(cam.transform.position, cam.transform.forward,
                                                    cam.projectionMatrix.m11, cam.nearClipPlane, anchor, 0f, DesignH);
                Assert.IsTrue(p.Valid);

                // 在平面上往「相機的右、上」各推 100 設計 px
                Vector3 right = cam.transform.right, up = cam.transform.up;
                Vector3 moved = p.Position + (right * 100f + up * 100f) * p.Scale;

                Vector3 a = cam.WorldToViewportPoint(p.Position);
                Vector3 b = cam.WorldToViewportPoint(moved);
                // 垂直:600 設計 px = 1.0 viewport;水平:800 設計 px = 1.0 viewport(aspect 釘 4:3)
                Assert.AreEqual(100f / 600f, b.y - a.y, 1e-3f, "100 設計 px 垂直位移");
                Assert.AreEqual(100f / 800f, b.x - a.x, 1e-3f, "100 設計 px 水平位移");
            }
            finally { Object.DestroyImmediate(cam.gameObject); }
        }
    }
}
