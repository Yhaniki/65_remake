using System.Collections;
using NUnit.Framework;
using Sdo.Game;
using Sdo.UI.Screens;
using Sdo.UI.Util;
using UnityEngine;
using UnityEngine.TestTools;

namespace Sdo.Tests
{
    /// <summary>
    /// 使用者回報:「在 room 裡面,後面那個人的名牌出現在前面那個人的身體前面。」
    /// 原因是名字牌整層畫在 UI 上(疊在房間 RenderTexture 之上)—— 它根本沒參與房間相機的深度緩衝。
    /// 修法是每個人一張 world canvas 進房間相機(<see cref="RoomNamePlateAnchor"/>)。
    ///
    /// 這條測試跑的是**真的房間**:真的相機(4:3 釘死的投影 + 視窗形狀的 RT + 4× MSAA)、真的 cullingMask、
    /// 真的深度緩衝、真的 <see cref="OutlinedLabel"/>。EditMode 那兩條(RoomNamePlateAnchorTests /
    /// RoomNamePlateCanvasDepthTests)驗的是數學與 Unity 的性質,**驗不到接線** ——
    /// 例如 layer 14 沒進 cullingMask、或名字的子物件留在 layer 0,那兩條都會全綠而畫面上名字整個不見。
    ///
    /// 🔴 量像素一律用「同一幀內開/關名字牌的兩張圖相減」,不要用顏色比對:房間本身有大量洋紅燈光
    ///    與乳白色的家具,顏色比對抓到的雜訊比訊號還多(那個坑在頭上泡那次踩過)。
    ///    兩張圖必須在**同一幀**內拍(中間不 yield),否則角色的 idle 動作會讓整張圖都在動。
    /// </summary>
    public class RoomNamePlateDepthTest
    {
        private const int Layer = RoomScene3D.NamePlateLayer;

        private GameObject _sceneGo, _plateRoot, _blocker;
        private RoomNamePlateAnchor _anchor;
        private OutlinedLabel _label;

        [TearDown]
        public void TearDown()
        {
            if (_blocker != null)
            {
                var mr = _blocker.GetComponent<MeshRenderer>();
                if (mr != null && mr.sharedMaterial != null) Object.DestroyImmediate(mr.sharedMaterial);
                Object.DestroyImmediate(_blocker);
            }
            if (_plateRoot != null) Object.DestroyImmediate(_plateRoot);
            if (_sceneGo != null) Object.DestroyImmediate(_sceneGo);
        }

        [UnityTest]
        public IEnumerator The_Room_Camera_Draws_The_Nameplate_And_Anything_In_Front_Cuts_It()
        {
            _sceneGo = new GameObject("RoomScene3D_nameplate");
            var scene = _sceneGo.AddComponent<RoomScene3D>();
            scene.Build();
            for (int i = 0; i < 12; i++) yield return null;
            yield return new WaitForSecondsRealtime(0.5f);   // 讓角色擺好姿勢、相機定位

            if (!scene.Ready || scene.SceneCamera == null || scene.SceneTexture == null)
                Assert.Ignore("房間建不起來(缺 DATA)—— 這條測試需要真實資產");

            var cam = scene.SceneCamera;
            var rt = scene.SceneTexture as RenderTexture;
            Assert.IsNotNull(rt, "房間的 SceneTexture 不是 RenderTexture");

            Vector3 anchorWorld;
            Vector2 vp;
            if (!scene.TryHeadAnchorWorld(out anchorWorld) || !scene.TryHeadViewport(out vp))
                Assert.Ignore("量不到本機角色的頭 —— 這條測試需要真實資產");

            // 真的名字牌:一張 world canvas + 真的 OutlinedLabel,擺法與 RoomScreen 一模一樣。
            _plateRoot = new GameObject("RoomNamePlates3D_test");
            _anchor = RoomNamePlateAnchor.Create(_plateRoot.transform, "RoomNamePlateOwnerTest", Layer);
            _label = OutlinedLabel.Create(_anchor.Content, "TestName", 0, 0, 160, 20, 14,
                                          TextStyles.FaceCream, Color.black, 1.4f, true);
            _label.SetText("DEPTHTEST");
            _anchor.RefreshLayer(Layer);   // 🔴 子物件生在 layer 0 —— 漏了這行名字整個不見
            yield return null;
            yield return null;

            // ---- ① 名字牌真的被房間相機畫出來(= layer 14 在 cullingMask 裡、子物件也在那層)----
            PlaceLikeRoomScreen(cam, scene, out anchorWorld, out vp);
            var onA = Render(cam, rt);
            _anchor.SetActive(false);
            var offA = Render(cam, rt);      // 同一幀 → 差異只可能是名字牌
            _anchor.SetActive(true);

            int visible = CountDiff(onA, offA);
            Assert.Greater(visible, 150,
                "名字牌沒有出現在房間畫面裡 —— layer " + Layer + " 不在房間相機的 cullingMask,"
                + "或名字的子物件還留在 layer 0");

            // ---- ② 位置沒跑掉:名字仍在頭的正上方(UI 層時就是這樣)----
            Vector2 centroid = DiffCentroidDesign(onA, offA, rt.width, rt.height);
            float expectedX = vp.x * 800f;
            float expectedY = (1f - vp.y) * 600f - 8f - 10f;   // PlaceFollow topOffset −8,holder 高 20 → 中心再上 10
            Assert.That(centroid.x, Is.EqualTo(expectedX).Within(30f),
                        "名字牌的水平位置跑掉了(內容層的回推位移算錯)");
            Assert.That(centroid.y, Is.EqualTo(expectedY).Within(30f),
                        "名字牌的垂直位置跑掉了 —— 符號寫反的話它會鏡射到頭的另一邊");

            // ---- ③ 前面有東西就切掉它(= 使用者要的那件事)----
            // 一片不透明的板子擋在相機與名字牌之間,放在**場景那層**:等同「站在他前面的另一個人」。
            _blocker = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _blocker.layer = RoomScene3D.SceneLayer;
            Vector3 toPlate = anchorWorld - cam.transform.position;
            _blocker.transform.position = cam.transform.position + toPlate * 0.5f;
            _blocker.transform.rotation = cam.transform.rotation;
            _blocker.transform.localScale = Vector3.one * Mathf.Max(40f, toPlate.magnitude);
            _blocker.GetComponent<MeshRenderer>().sharedMaterial =
                new Material(Shader.Find("Unlit/Color")) { color = new Color32(20, 100, 20, 255) };
            yield return null;

            PlaceLikeRoomScreen(cam, scene, out anchorWorld, out vp);
            var onB = Render(cam, rt);
            _anchor.SetActive(false);
            var offB = Render(cam, rt);
            _anchor.SetActive(true);

            int throughWall = CountDiff(onB, offB);
            Assert.Less(throughWall, visible * 0.05f,
                "名字牌穿透了擋在它前面的不透明物體(" + throughWall + " / " + visible + " px)——"
                + "它沒有吃到房間相機的深度緩衝,後面那個人的名牌還是會浮在前面那個人的身體上");
        }

        /// <summary>照 RoomScreen 每幀做的那兩步:子物件擺絕對設計座標 → 整組貼到頭部深度平面。</summary>
        private void PlaceLikeRoomScreen(Camera cam, RoomScene3D scene, out Vector3 anchorWorld, out Vector2 vp)
        {
            Assert.IsTrue(scene.TryHeadAnchorWorld(out anchorWorld));
            Assert.IsTrue(scene.TryHeadViewport(out vp));
            var rect = _label.Rect;
            rect.anchoredPosition = new Vector2(vp.x * 800f - rect.sizeDelta.x * 0.5f,
                                                -((1f - vp.y) * 600f - 8f));
            Assert.IsTrue(_anchor.Place(cam, anchorWorld, vp), "解不出名字牌的平面");
            Canvas.ForceUpdateCanvases();
        }

        private static Color32[] Render(Camera cam, RenderTexture rt)
        {
            cam.Render();
            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0f, 0f, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = previous;
            var pixels = tex.GetPixels32();
            Object.DestroyImmediate(tex);
            return pixels;
        }

        // 兩張圖差多少個像素。門檻 24 是為了濾掉 MSAA 解析的 ±1 抖動,同時留得住黑邊那圈暗像素。
        private static bool Differs(Color32 a, Color32 b)
            => Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) > 24;

        private static int CountDiff(Color32[] a, Color32[] b)
        {
            int n = 0;
            for (int i = 0; i < a.Length && i < b.Length; i++) if (Differs(a[i], b[i])) n++;
            return n;
        }

        /// <summary>差異像素的重心,換算回 800×600 設計座標(y 自上緣往下為正)。</summary>
        private static Vector2 DiffCentroidDesign(Color32[] a, Color32[] b, int w, int h)
        {
            double sx = 0, sy = 0; int n = 0;
            for (int i = 0; i < a.Length && i < b.Length; i++)
            {
                if (!Differs(a[i], b[i])) continue;
                sx += i % w; sy += i / w; n++;
            }
            if (n == 0) return new Vector2(float.NaN, float.NaN);
            float cx = (float)(sx / n), cy = (float)(sy / n);          // ReadPixels 的 y 是**自下往上**
            return new Vector2(cx / w * 800f, (1f - cy / h) * 600f);
        }
    }
}
