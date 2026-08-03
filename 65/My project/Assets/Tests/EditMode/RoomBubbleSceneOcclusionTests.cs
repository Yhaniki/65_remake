using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Sdo.Game;
using Sdo.UI.Util;

namespace Sdo.Tests
{
    /// <summary>
    /// 頭上聊天泡的遮擋規則(使用者需求):**只被人擋,不被場景擋**。
    ///
    /// 機制:場景畫完之後,在同一台相機裡插兩片東西 ——
    ///   ① <c>Sdo/DepthReset</c>(sortingOrder 98)把整片深度推回無限遠 → 場景的深度不再存在;
    ///   ② 每個角色的隱形分身 <c>Sdo/DepthOnlyMask</c>(99)把人的剪影寫回深度;
    /// 然後泡(100+)照常做深度測試 → 只可能輸給人。
    ///
    /// 這裡驗四條性質(不需要真的房間,只需要這三層 + 一台相機):
    ///   ① 場景擋在泡前面 → 泡**一個像素都不能少**(這就是這次修的東西);
    ///   ② 人擋在泡前面   → 泡要被切掉(圖與 TMP 的字都要驗:字有自己的材質);
    ///   ③ 人站在泡後面   → 不能擋(分身寫的是真實深度,不是無條件覆蓋);
    ///   ④ 重置片與分身**都不能寫到顏色** —— 分身與本尊完全重疊,一旦寫色就是多一層重複疊畫,
    ///      而重置片會直接把整個房間洗成背景色。
    /// </summary>
    public class RoomBubbleSceneOcclusionTests
    {
        private const int SceneLayer = RoomScene3D.SceneLayer;
        private const int PeopleLayer = RoomScene3D.PeopleDepthLayer;
        private const int BubbleLayer = RoomScene3D.BubbleLayer;
        private const int W = 240, H = 240;

        private const float BubbleZ = 100f, FrontZ = 50f, BehindZ = 150f;

        private Camera _cam;
        private RenderTexture _rt;
        private GameObject _sceneBlocker, _person, _reset, _canvasGo;
        private RectTransform _canvas;
        private TextMeshProUGUI _label;

        [SetUp]
        public void SetUp()
        {
            _rt = new RenderTexture(W, H, 24);
            var camGo = new GameObject("roomCamProbe");
            _cam = camGo.AddComponent<Camera>();
            _cam.orthographic = false;
            _cam.fieldOfView = 45f;
            _cam.nearClipPlane = 5f; _cam.farClipPlane = 7500f;
            _cam.cullingMask = (1 << SceneLayer) | (1 << PeopleLayer) | (1 << BubbleLayer);
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = Color.black;
            _cam.targetTexture = _rt;
            _cam.transform.position = Vector3.zero;
            _cam.transform.rotation = Quaternion.identity;   // 看 +Z
            _cam.enabled = false;

            // 場景(紅):一片大到蓋滿畫面的不透明面片。
            _sceneBlocker = Quad(SceneLayer, Color.red, new Material(Shader.Find("Unlit/Color")), 0);
            // 人:隱形深度分身(與角色身上的分身走同一支 shader、同一個 sortingOrder)。
            var depthShader = Shader.Find(RoomPeopleDepthProxy.ShaderName);
            Assert.IsNotNull(depthShader, RoomPeopleDepthProxy.ShaderName + " 找不到");
            _person = Quad(PeopleLayer, Color.white, new Material(depthShader), RoomPeopleDepthProxy.ProxySortingOrder);
            // 深度重置片(掛在相機底下,production 就是這樣建的)。
            _reset = RoomPeopleDepthProxy.CreateDepthReset(_cam.transform, PeopleLayer);
            Assert.IsNotNull(_reset, RoomPeopleDepthProxy.ResetShaderName + " 找不到");

            _canvas = UIKit.CreateBubbleWorldCanvas("bubbleProbeCanvas", null, BubbleLayer, new Vector2(171f, 111f));
            _canvasGo = _canvas.gameObject;
            _canvas.pivot = new Vector2(0.5f, 0.5f);
            _canvas.GetComponent<Canvas>().sortingOrder = 100;   // = RoomScreen.BubbleSortingOrderBase
            var img = UIKit.AddImage(_canvas, "bg", new Color32(0, 255, 0, 255));
            UIKit.Stretch(img.rectTransform);
            _label = UIKit.AddText(_canvas, "txt", "口口口", 40f, Color.white, TextAlignmentOptions.Center);
            UIKit.Stretch(_label.rectTransform);
            _canvas.position = new Vector3(0f, 0f, BubbleZ);
            _canvas.rotation = Quaternion.identity;
            float s = 2f * BubbleZ / (111f * _cam.projectionMatrix.m11) * 0.8f;
            _canvas.localScale = new Vector3(s, s, s);
            SetLayerRecursive(_canvasGo, BubbleLayer);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in new[] { _sceneBlocker, _person, _canvasGo })
                if (go != null) Object.DestroyImmediate(go);
            if (_cam != null) Object.DestroyImmediate(_cam.gameObject);   // 重置片是它的子物件,一起走
            if (_rt != null) { _rt.Release(); Object.DestroyImmediate(_rt); }
        }

        [Test]
        public void Scene_Geometry_In_Front_Does_Not_Occlude_The_Bubble()
        {
            Place(_sceneBlocker, BehindZ);
            Place(_person, BehindZ);
            var baseline = Shoot();
            int art0 = CountGreen(baseline), text0 = CountWhite(baseline);
            Assert.Greater(art0, 5000, "泡的底圖沒畫出來(後面的斷言就沒意義了)");
            Assert.Greater(text0, 100, "泡裡的字沒畫出來");

            // 場景搬到泡**前面** —— 泡一個像素都不該少。
            Place(_sceneBlocker, FrontZ);
            var px = Shoot();
            Assert.Greater(CountRed(px), 1000, "場景面片自己要畫出來(不然這條測試沒在驗東西)");
            Assert.GreaterOrEqual(CountGreen(px), art0,
                "場景擋在泡前面時泡的底圖被切掉了 —— 深度重置片沒生效(檢查 sortingOrder 98 有沒有排在場景之後)");
            Assert.GreaterOrEqual(CountWhite(px), text0, "場景擋在泡前面時泡裡的**字**被切掉了");
        }

        [Test]
        public void A_Person_In_Front_Occludes_The_Bubble_Art_And_Its_Text()
        {
            Place(_sceneBlocker, BehindZ);
            Place(_person, BehindZ);
            var baseline = Shoot();
            int art0 = CountGreen(baseline), text0 = CountWhite(baseline);
            Assert.Greater(art0, 5000);
            Assert.Greater(text0, 100);

            Place(_person, FrontZ);
            var px = Shoot();
            Assert.Less(CountGreen(px), art0 * 0.05f,
                "人擋在泡前面時泡沒有被切掉 —— 分身沒寫進深度,擋人功能整個失效");
            Assert.Less(CountWhite(px), text0 * 0.05f,
                "人擋在泡前面時泡裡的**字**沒有被切掉(圖有、字沒有)—— TMP 的材質沒吃到深度測試");
        }

        [Test]
        public void A_Person_Behind_The_Bubble_Does_Not_Occlude_It()
        {
            Place(_sceneBlocker, BehindZ);
            Place(_person, BehindZ);
            var baseline = Shoot();
            int art0 = CountGreen(baseline);
            Assert.Greater(art0, 5000);

            Place(_person, BehindZ + 40f);
            Assert.GreaterOrEqual(CountGreen(Shoot()), art0,
                "站在泡**後面**的人把泡擋住了 —— 分身寫的深度不是真實距離(變成無條件覆蓋)");
        }

        [Test]
        public void The_Depth_Reset_And_The_Proxy_Never_Paint_Anything()
        {
            // 泡關掉:畫面上就只剩「場景 + 看不見的兩片」,顏色必須與完全沒有那兩片時一模一樣。
            _canvasGo.SetActive(false);
            Place(_sceneBlocker, BehindZ);
            Place(_person, FrontZ);
            int red = CountRed(Shoot());

            _person.SetActive(false);
            _reset.SetActive(false);
            int redPlain = CountRed(Shoot());

            Assert.Greater(redPlain, 5000, "場景面片沒畫出來");
            Assert.AreEqual(redPlain, red,
                "深度重置片或深度分身把場景蓋掉了 —— 它們有寫到顏色(檢查兩支 shader 的 ColorMask 0)");
        }

        private static GameObject Quad(int layer, Color color, Material mat, int sortingOrder)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.layer = layer;
            go.transform.localScale = new Vector3(400f, 400f, 1f);
            mat.color = color;
            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.sortingOrder = sortingOrder;
            return go;
        }

        private static void Place(GameObject go, float z) => go.transform.position = new Vector3(0f, 0f, z);

        private static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            for (int i = 0; i < go.transform.childCount; i++) SetLayerRecursive(go.transform.GetChild(i).gameObject, layer);
        }

        private Color32[] Shoot()
        {
            Canvas.ForceUpdateCanvases();
            _cam.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = _rt;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            var px = tex.GetPixels32();
            Object.DestroyImmediate(tex);
            return px;
        }

        private static int CountGreen(Color32[] px)
        {
            int n = 0;
            foreach (var p in px) if (p.g > 180 && p.r < 90 && p.b < 90) n++;
            return n;
        }

        private static int CountRed(Color32[] px)
        {
            int n = 0;
            foreach (var p in px) if (p.r > 180 && p.g < 90 && p.b < 90) n++;
            return n;
        }

        private static int CountWhite(Color32[] px)
        {
            int n = 0;
            foreach (var p in px) if (p.r > 200 && p.g > 200 && p.b > 200) n++;
            return n;
        }
    }
}
