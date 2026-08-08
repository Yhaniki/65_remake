using NUnit.Framework;
using Sdo.Game;
using Sdo.UI.Screens;
using Sdo.UI.Util;
using UnityEngine;

namespace Sdo.Tests
{
    /// <summary>
    /// 房間頭上名字牌搬進 3D 之後的合約(使用者回報:「後面那個人的名牌出現在前面那個人的身體前面」)。
    ///
    /// 修法是每個人一張 world-space canvas 由房間相機畫 ⇒ 吃 GPU 深度測試。這裡釘住的是那張 canvas
    /// **沒有把畫面搞歪**:名字牌的螢幕位置與大小必須與它還畫在 UI 層時逐像素相同 —— 否則「修好遮擋」
    /// 會伴隨「名字飄離頭頂/遠的人名字變小」,而那兩件事看起來像不相干的新 bug。
    ///
    /// 深度測試本身是 Unity 的性質,由 <see cref="RoomNamePlateCanvasDepthTests"/> 釘。
    /// </summary>
    public class RoomNamePlateAnchorTests
    {
        private const int Layer = RoomScene3D.NamePlateLayer;

        private GameObject _root, _camGo;
        private Camera _cam;
        private RoomNamePlateAnchor _anchor;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("plateRoot");
            _camGo = new GameObject("roomCamProbe");
            _cam = _camGo.AddComponent<Camera>();
            _cam.orthographic = false;
            _cam.fieldOfView = 45f;                         // 照房間那台的參數(RoomScene3D.BuildCamera)
            _cam.nearClipPlane = 5f; _cam.farClipPlane = 7500f;
            _cam.aspect = RoomScene3D.ProjectionAspect;     // 房間每幀把 aspect 釘成 4:3
            _cam.enabled = false;
            _camGo.transform.position = new Vector3(0f, 60f, -200f);
            _camGo.transform.LookAt(new Vector3(0f, 50f, 0f), Vector3.up);

            _anchor = RoomNamePlateAnchor.Create(_root.transform, "RoomNamePlateOwner0", Layer);
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            if (_camGo != null) Object.DestroyImmediate(_camGo);
        }

        /// <summary>子物件照它在 UI 層時的絕對設計座標擺(左上錨、往下為負),回傳它自己。</summary>
        private RectTransform ChildAtDesign(float x, float y)
        {
            var rt = UIKit.NewRect(_anchor.Content, "probe");
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = Vector2.zero;
            rt.anchoredPosition = new Vector2(x, -y);
            return rt;
        }

        private static Vector2 Design(Vector3 vp) => new Vector2(vp.x * 800f, (1f - vp.y) * 600f);

        [Test]
        public void The_Canvas_Is_A_World_Canvas_On_The_Nameplate_Layer()
        {
            // 🔴 renderMode 被降級成 nested canvas(掛在別的 Canvas 底下)的話,名字牌會靜默回到 UI 層,
            //    遮擋整個消失而畫面看起來「幾乎正常」。
            Assert.AreEqual(RenderMode.WorldSpace, _anchor.Canvas.renderMode);
            Assert.IsNull(_anchor.Canvas.transform.parent.GetComponentInParent<Canvas>(),
                          "名字牌 canvas 被掛進了另一張 Canvas 底下 → Unity 會忽略 renderMode");
            Assert.AreEqual(RoomNamePlateAnchor.SortingBase, _anchor.Canvas.sortingOrder,
                            "起點必須 > 0,否則會與場景的透明批混在一起排");
            Assert.AreEqual(Layer, _anchor.Canvas.gameObject.layer);
            Assert.AreEqual(Layer, _anchor.Content.gameObject.layer);
            Assert.IsFalse(_anchor.Canvas.gameObject.activeSelf,
                           "建好就開著 → 角色還沒生出來的那幾幀,一張 800×600 的 canvas 會停在世界原點");
        }

        [Test]
        public void Late_Children_Get_Pulled_Onto_The_Layer()
        {
            // new GameObject 一律生在 layer 0(不繼承 parent)→ 名字/家族列建好之後一定要補這一下,
            // 漏掉的症狀是「那個人的名字整個不見」(房間相機沒拍到),不是位置跑掉。
            var late = ChildAtDesign(100f, 100f);
            Assert.AreEqual(0, late.gameObject.layer, "前提變了:UIKit.NewRect 現在會繼承 layer,這條測試要改寫");

            _anchor.RefreshLayer(Layer);
            Assert.AreEqual(Layer, late.gameObject.layer);
        }

        [Test]
        public void A_Child_Placed_At_The_Anchors_Design_Point_Lands_Exactly_On_The_Anchor()
        {
            // 這是整個搬遷的核心性質:內容層的回推位移必須剛好抵銷錨點的設計座標。
            // 差一個負號的症狀是名字牌上下鏡射地飄到頭的另一邊(而且只有鏡頭移動時才明顯)。
            Vector3 anchorWorld = new Vector3(30f, 95f, 40f);
            Vector3 vp = _cam.WorldToViewportPoint(anchorWorld);
            Assert.IsTrue(_anchor.Place(_cam, anchorWorld, vp), "錨點在鏡頭前面,平面卻解不出來");

            var probe = ChildAtDesign(Design(vp).x, Design(vp).y);
            Assert.That(Vector3.Distance(probe.position, anchorWorld), Is.LessThan(0.01f),
                        "子物件沒有落在錨點上 —— 內容層的位移算錯(符號/座標系)");
        }

        [Test]
        public void The_Nameplate_Keeps_Its_Screen_Position_And_Size_At_Any_Distance()
        {
            // 走遠/走近都不可以讓名字變大變小或飄離頭頂 —— 它在 UI 層時是固定設計 px 的。
            foreach (var anchorWorld in new[] { new Vector3(-40f, 95f, -60f), new Vector3(25f, 95f, 300f) })
            {
                Vector3 vp = _cam.WorldToViewportPoint(anchorWorld);
                Assert.IsTrue(_anchor.Place(_cam, anchorWorld, vp));

                var d = Design(vp);
                var atHead = ChildAtDesign(d.x, d.y);
                var oneLineUp = ChildAtDesign(d.x, d.y - 15f);   // 家族列疊在名字上方一行(RoomFamilyRow.LinePitch)

                Vector3 vpHead = _cam.WorldToViewportPoint(atHead.position);
                Vector3 vpUp = _cam.WorldToViewportPoint(oneLineUp.position);
                Assert.That(vpHead.x, Is.EqualTo(vp.x).Within(0.001f), "名字的水平位置跑掉了 @ " + anchorWorld);
                Assert.That(vpHead.y, Is.EqualTo(vp.y).Within(0.001f), "名字的垂直位置跑掉了 @ " + anchorWorld);
                // 15 設計 px = 600 設計高的 1/40 → 不論遠近都佔畫面高度的 2.5%(這就是「大小不隨距離變」)。
                Assert.That(vpUp.y - vpHead.y, Is.EqualTo(15f / 600f).Within(0.001f),
                            "行距隨距離縮放了 —— 名字牌不再是固定螢幕大小 @ " + anchorWorld);

                Object.DestroyImmediate(atHead.gameObject);
                Object.DestroyImmediate(oneLineUp.gameObject);
            }
        }

        [Test]
        public void The_Plate_Faces_The_Camera()
        {
            // 名字牌是一張平面 —— 沒有正對相機的話,鏡頭一轉字就被壓成一條線。
            Vector3 anchorWorld = new Vector3(0f, 95f, 0f);
            Assert.IsTrue(_anchor.Place(_cam, anchorWorld, _cam.WorldToViewportPoint(anchorWorld)));
            Assert.That(Vector3.Dot(_anchor.Canvas.transform.forward, _cam.transform.forward),
                        Is.GreaterThan(0.999f), "名字牌沒有正對相機");
        }

        [Test]
        public void An_Anchor_Behind_The_Camera_Turns_The_Whole_Plate_Off()
        {
            // 解不出平面時如果照擺,Scale 會變成負的 → 名字以鏡像的一團亂圖出現在鏡頭後面。
            Vector3 behind = _cam.transform.position - _cam.transform.forward * 50f;
            Assert.IsFalse(_anchor.Place(_cam, behind, new Vector2(0.5f, 0.5f)));
            Assert.IsFalse(_anchor.Canvas.gameObject.activeSelf, "解不出平面卻還把 canvas 開著");

            // 走回鏡頭前面 → 自己開回來(不能要求呼叫端記得)。
            Vector3 front = _cam.transform.position + _cam.transform.forward * 200f;
            Assert.IsTrue(_anchor.Place(_cam, front, _cam.WorldToViewportPoint(front)));
            Assert.IsTrue(_anchor.Canvas.gameObject.activeSelf);
        }

        [Test]
        public void No_Camera_Is_Survivable()
        {
            Assert.DoesNotThrow(() => _anchor.Place(null, Vector3.zero, Vector2.zero));
        }
    }
}
