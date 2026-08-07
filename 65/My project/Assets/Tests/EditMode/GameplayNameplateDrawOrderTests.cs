using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Game;
using UnityEngine;

namespace Sdo.Tests
{
    /// <summary>
    /// 遊戲中兩面頭上名字牌重疊時,**站在前面那個人的黑邊要蓋得住站在後面那個人的字面**。
    ///
    /// 修之前全場共用同一組畫序(字面 2、黑邊 1)。sortingOrder 是跨物件的全域分層,所以實際畫的順序是
    /// 「所有人的黑邊 → 所有人的字面」:後面那個人的白字整片畫在前面那個人的黑邊上,描邊等於不存在,
    /// 兩串名字糊成一團(使用者回報,截圖可見)。名牌是 ZWrite Off 的 alpha-blend,深度緩衝救不了
    /// 名牌對名牌 —— 只由畫序決定。所以這件事要有測試釘著。
    /// </summary>
    public class GameplayNameplateDrawOrderTests
    {
        private readonly List<GameObject> _spawned = new List<GameObject>();
        private const int Layer = 4;

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _spawned.Count; i++) if (_spawned[i] != null) Object.DestroyImmediate(_spawned[i]);
            _spawned.Clear();
        }

        // ---- 純畫序算式 ----

        [Test]
        public void A_Nearer_Persons_Edge_Outranks_A_Farther_Persons_Face()
        {
            // rank 0 = 最遠、rank 1 = 比較近。這一行就是這次的 bug:
            // 近的人的**黑邊**必須大於遠的人的**字面**,否則描邊在重疊處被抹掉。
            Assert.Greater(NameplateDrawOrder.EdgeOrder(1), NameplateDrawOrder.FaceOrder(0),
                "站在前面的人的黑邊沒有蓋過站在後面的人的字面 —— 描邊會在重疊處消失");
            Assert.Greater(NameplateDrawOrder.FaceOrder(1), NameplateDrawOrder.EdgeOrder(1),
                "字面沒有壓在自己的黑邊上");
        }

        [Test]
        public void Every_Nameplate_Stays_Above_The_Transparent_Scene_Batch()
        {
            // 場景的透明物(噴水池的水、光柱)都在 sortingOrder 0;名牌**連黑邊**都要在它之上,
            // 否則人站在噴水池前面、名牌卻被後面的水蓋掉(見 HeadMarker.WorldNameOrder)。
            for (int rank = 0; rank < 8; rank++)
                Assert.Greater(NameplateDrawOrder.EdgeOrder(rank), 0,
                    "名次 " + rank + " 的黑邊排到透明場景物之下了");
        }

        [Test]
        public void Face_Orders_Follow_The_Standing_Depths()
        {
            var orders = new List<int>();
            var scratch = new List<int>();
            var depths = new List<float> { 500f, 120f, 300f, float.MaxValue };   // 最後一個:這一幀量不到深度

            NameplateDrawOrder.FaceOrders(depths, orders, scratch);

            Assert.AreEqual(depths.Count, orders.Count);
            for (int a = 0; a < depths.Count; a++)
                for (int b = 0; b < depths.Count; b++)
                    if (depths[a] < depths[b])
                        Assert.Greater(orders[a], orders[b],
                            "近的(" + depths[a] + ")沒有排在遠的(" + depths[b] + ")之上");
            // 深度不明的那面排最遠 → 拿最小的一段,而且仍是「最遠那位」的基準值。
            Assert.AreEqual(NameplateDrawOrder.FirstFaceOrder, orders[3]);
        }

        [Test]
        public void Trivial_Inputs_Do_Nothing()
        {
            var orders = new List<int> { 99 };
            Assert.DoesNotThrow(() => NameplateDrawOrder.FaceOrders(null, orders, new List<int>()));
            Assert.AreEqual(0, orders.Count, "深度是 null 時應該清空輸出而不是留著上一幀的值");
            Assert.DoesNotThrow(() => NameplateDrawOrder.FaceOrders(new List<float> { 1f }, null, null));
        }

        // ---- 真的套到 renderer 上 ----

        private Camera MakeCam()
        {
            var go = new GameObject("NameplateOrderCam");
            _spawned.Add(go);
            var cam = go.AddComponent<Camera>();
            cam.enabled = false;
            cam.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);   // 看向 +z
            return cam;
        }

        private HeadMarker MakeMarker(Camera cam, string label, Vector3 head)
        {
            var go = new GameObject("Marker_" + label);
            _spawned.Add(go);
            var hm = go.AddComponent<HeadMarker>();
            hm.AnchorGetter = () => head;
            hm.CamGetter = () => cam;
            hm.Init(null, label, depthTestedWorld: true, worldLayer: Layer);
            _spawned.Add(hm.NameRootForTest);   // Label3D 自己一個 root,不掛在 marker 底下
            return hm;
        }

        private static void OrderRange(HeadMarker marker, out int min, out int max)
        {
            min = int.MaxValue; max = int.MinValue;
            var renderers = marker.NameRootForTest.GetComponentsInChildren<MeshRenderer>();
            Assert.Greater(renderers.Length, 1, "名牌應該有字面 + 黑邊多個 renderer");
            foreach (var r in renderers)
            {
                if (r.sortingOrder < min) min = r.sortingOrder;
                if (r.sortingOrder > max) max = r.sortingOrder;
            }
        }

        [Test]
        public void The_Nearer_Nameplate_Draws_Entirely_Above_The_Farther_One()
        {
            var cam = MakeCam();
            var near = MakeMarker(cam, "NEAR", new Vector3(0f, 0f, 100f));
            var far = MakeMarker(cam, "FAR", new Vector3(10f, 0f, 300f));

            HeadMarker.ApplyWorldDrawOrder();

            OrderRange(near, out int nearMin, out int nearMax);
            OrderRange(far, out _, out int farMax);
            Assert.Greater(nearMin, farMax,
                "站在前面那面名牌的黑邊沒有整個蓋過後面那面的字面 —— 重疊處的描邊會被抹掉");
            Assert.Greater(nearMax, nearMin, "字面和黑邊被塞進同一層了(誰在上面會抖動)");
        }

        [Test]
        public void Walking_Behind_Someone_Flips_Which_Nameplate_Is_On_Top()
        {
            var cam = MakeCam();
            var local = MakeMarker(cam, "ME", new Vector3(0f, 0f, 100f));
            var other = MakeMarker(cam, "OTHER", new Vector3(10f, 0f, 300f));

            HeadMarker.ApplyWorldDrawOrder();
            OrderRange(local, out int localMin, out _);
            OrderRange(other, out _, out int otherMax);
            Assert.Greater(localMin, otherMax);

            // 我往後走到他後面 → 換他整面蓋住我。
            local.AnchorGetter = () => new Vector3(0f, 0f, 400f);
            HeadMarker.ApplyWorldDrawOrder();

            OrderRange(local, out _, out int localMax2);
            OrderRange(other, out int otherMin2, out _);
            Assert.Greater(otherMin2, localMax2, "走到別人後面之後,我的名牌沒有沉下去");
        }

        [Test]
        public void A_Nameplate_With_No_Camera_Sits_At_The_Back()
        {
            var cam = MakeCam();
            var known = MakeMarker(cam, "KNOWN", new Vector3(0f, 0f, 300f));
            var unknown = MakeMarker(cam, "UNKNOWN", new Vector3(0f, 0f, 50f));
            unknown.CamGetter = () => null;   // 相機還沒好(人剛進來)→ 不該冒出來蓋住所有人

            HeadMarker.ApplyWorldDrawOrder();

            OrderRange(known, out int knownMin, out _);
            OrderRange(unknown, out _, out int unknownMax);
            Assert.Greater(knownMin, unknownMax, "深度不明的名牌沒有排在最後面");
        }

        [Test]
        public void A_Hud_Mode_Marker_Keeps_Its_Note_Board_Relative_Order()
        {
            // 2D fallback(沒有場景相機)那條路的畫序是相對音符板挑的負值,不歸這套排名管。
            var cam = MakeCam();
            var go = new GameObject("HudMarker");
            _spawned.Add(go);
            var hud = go.AddComponent<HeadMarker>();
            hud.AnchorGetter = () => new Vector3(0f, 0f, 10f);
            hud.CamGetter = () => cam;
            hud.Init(null, "HUD");                     // depthTestedWorld: false
            _spawned.Add(hud.NameRootForTest);
            MakeMarker(cam, "WORLD", new Vector3(0f, 0f, 100f));

            HeadMarker.ApplyWorldDrawOrder();

            OrderRange(hud, out _, out int hudMax);
            Assert.Less(hudMax, 0, "HUD 模式的名牌被世界排名改掉了畫序");
        }
    }
}
