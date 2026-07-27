using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Sdo.UI.Util;

namespace Sdo.Tests
{
    /// <summary>
    /// 頭上聊天泡能被前面的人擋住,靠的是一個 Unity 行為假設:**world-space canvas 被透視相機 render 時
    /// 會做深度測試**(UI 材質的 ZTest 不是 Always)。這裡把那個假設釘住 —— 尤其是 **TMP 的字**,
    /// 因為字是整個泡最容易出問題的部分:
    ///
    /// • TMP 不是普通 Image,它自己有一套材質;
    /// • 字型是**執行期**從 OS SimSun 建的,碰到 SimSun 沒有的字會**動態長出 sub-mesh 子物件**,
    ///   那些子物件有自己的材質與自己的 layer。
    ///
    /// 所以「圖會被擋住」不代表「字也會被擋住」,而症狀會是:只有打到罕見字的那則訊息,
    /// 一部分字穿透牆壁/人體 —— 幾乎不可能從畫面猜到原因。
    ///
    /// 這裡不依賴任何專案程式碼的行為,純粹驗 Unity/URP 的性質。<see cref="RoomBubbleDepthTest"/>(PlayMode)
    /// 才是驗真的房間相機 + 真的定位數學。
    /// </summary>
    public class RoomBubbleCanvasDepthTests
    {
        private const int Layer = 14;   // = RoomScene3D.BubbleLayer(這裡刻意不引用 Sdo.Game,測的是 Unity 性質)
        private const int W = 200, H = 200;

        private Camera _cam;
        private RenderTexture _rt;
        private GameObject _cube, _canvasGo;
        private TextMeshProUGUI _label;
        private RectTransform _canvas;

        [SetUp]
        public void SetUp()
        {
            _rt = new RenderTexture(W, H, 24);
            var camGo = new GameObject("bubbleDepthProbeCam");
            _cam = camGo.AddComponent<Camera>();
            _cam.orthographic = false;
            _cam.fieldOfView = 45f;                       // 照房間那台的參數
            _cam.nearClipPlane = 5f; _cam.farClipPlane = 7500f;
            _cam.cullingMask = 1 << Layer;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = Color.black;
            _cam.targetTexture = _rt;
            _cam.transform.position = Vector3.zero;
            _cam.transform.rotation = Quaternion.identity;   // 看 +Z
            _cam.enabled = false;

            _cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _cube.layer = Layer;
            _cube.transform.localScale = new Vector3(20f, 20f, 1f);
            _cube.GetComponent<MeshRenderer>().sharedMaterial =
                new Material(Shader.Find("Unlit/Color")) { color = Color.red };

            _canvas = UIKit.CreateBubbleWorldCanvas("bubbleDepthProbeCanvas", null, Layer, new Vector2(171f, 111f));
            _canvasGo = _canvas.gameObject;
            _canvas.pivot = new Vector2(0.5f, 0.5f);   // 這個測試要泡在畫面正中央,方便與 cube 重疊
            var img = UIKit.AddImage(_canvas, "bg", new Color32(0, 255, 0, 255));
            UIKit.Stretch(img.rectTransform);
            _label = UIKit.AddText(_canvas, "txt", "口口口", 40f, Color.white, TextAlignmentOptions.Center);
            UIKit.Stretch(_label.rectTransform);

            _canvas.position = new Vector3(0f, 0f, 100f);
            _canvas.rotation = Quaternion.identity;
            float s = 2f * 100f / (111f * _cam.projectionMatrix.m11) * 0.8f;
            _canvas.localScale = new Vector3(s, s, s);
            SetLayerRecursive(_canvasGo, Layer);
        }

        [TearDown]
        public void TearDown()
        {
            if (_cube != null) Object.DestroyImmediate(_cube);
            if (_canvasGo != null) Object.DestroyImmediate(_canvasGo);
            if (_cam != null) Object.DestroyImmediate(_cam.gameObject);
            if (_rt != null) { _rt.Release(); Object.DestroyImmediate(_rt); }
        }

        [Test]
        public void Geometry_In_Front_Occludes_Both_The_Bubble_Art_And_Its_Text()
        {
            // A:遮擋物在泡**後面** → 泡完整看得到,遮擋物一個像素都不該露出來。
            _cube.transform.position = new Vector3(0f, 0f, 150f);
            var a = Shoot();
            int aArt = CountGreen(a), aText = CountWhite(a), aCube = CountRed(a);
            Assert.Greater(aArt, 5000, "泡的底圖沒畫出來");
            Assert.Greater(aText, 100, "泡裡的字沒畫出來(字是 TMP,材質與圖不同 → 要單獨驗)");
            Assert.AreEqual(0, aCube, "遮擋物在泡後面,不該有任何像素露出來");

            // B:遮擋物在泡**前面** → 圖與字都必須被切掉一部分。
            _cube.transform.position = new Vector3(0f, 0f, 50f);
            var b = Shoot();
            int bArt = CountGreen(b), bText = CountWhite(b), bCube = CountRed(b);
            Assert.Greater(bCube, 3000, "遮擋物自己要畫出來");
            Assert.Less(bArt, aArt * 0.95f, "泡的底圖沒有被前面的東西切掉 —— world canvas 沒吃到深度測試");
            Assert.Less(bText, aText * 0.95f,
                "泡裡的**字**沒有被切掉(圖有、字沒有)—— TMP 的材質沒吃到 canvas 的 ZTest 設定。"
                + "這種狀況下泡看起來會被擋住,但字會穿透人體。");
        }

        [Test]
        public void Tmp_Fallback_Submeshes_Inherit_The_Bubble_Layer()
        {
            // 字型是執行期 OS SimSun;`𠀋`(CJK ext-B)不在 SimSun 裡 → TMP 會為後備字型長出 sub-mesh 子物件。
            // 那些子物件**必須**落在泡的 layer 上,否則它們會從房間 RT 消失、或被前端 UI 相機畫在錯的位置。
            _label.text = "口𠀋口";
            _label.ForceMeshUpdate();
            Assert.Greater(_label.transform.childCount, 0,
                "預期 `𠀋` 會逼出 TMP 後備字型的 sub-mesh;沒有的話這條測試就沒在驗東西了"
                + "(字型鏈換過?換一個 SimSun 沒有的字)");
            AssertLayerRecursive(_canvasGo.transform);
        }

        private static void AssertLayerRecursive(Transform t)
        {
            Assert.AreEqual(Layer, t.gameObject.layer, "後代物件 " + t.name + " 不在泡的 layer 上");
            for (int i = 0; i < t.childCount; i++) AssertLayerRecursive(t.GetChild(i));
        }

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
