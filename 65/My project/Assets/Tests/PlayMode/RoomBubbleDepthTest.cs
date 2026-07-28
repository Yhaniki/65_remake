using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Sdo.Game;
using Sdo.UI.Util;

namespace Sdo.Tests
{
    /// <summary>
    /// 頭上聊天泡「搬進房間相機」的端到端像素驗證 —— 驗的是兩件用眼睛看不準、但使用者一定會發現的事:
    ///
    ///   ① **位置沒有跑掉**:泡的排版仍在 800×600 設計 px 裡算(鏈物理一行沒改),
    ///      所以「錨點右 dx、下 dy 設計 px」在算出來的畫面上必須還是同一點。差 3 px 就是「泡有點飄」。
    ///   ② **真的會被擋住**:在泡與相機之間放一片不透明的東西 → 泡的像素必須消失。
    ///      這是整個改動的**目的**;做壞了的症狀是「什麼都沒變」,而那看起來像沒改到程式。
    ///
    /// 用真的 <see cref="RoomScene3D"/>(真相機、真 RT、真 URP 設定)+ 真的
    /// <see cref="RoomBubbleWorldAnchor.Solve"/>,所以連「aspect 釘 4:3」「RT 是視窗形狀」這些
    /// 只有實跑才踩得到的東西都一起驗到。
    ///
    /// 🔴 量測一律用**同一幀內開/關標記塊的兩張圖相減**,不要用「找某個顏色的像素」——
    /// 踩過:房間本身有大量洋紅/粉色燈光,顏色比對抓到 3 倍於標記塊的雜訊像素,重心整個被拉走,
    /// 看起來像「泡位置錯了 224 px」,其實是量錯的。同一幀內不 yield → 除了標記塊沒有別的東西會變。
    /// </summary>
    public class RoomBubbleDepthTest
    {
        private const float MarkerW = 60f, MarkerH = 40f;   // 設計 px
        private const float DesignW = 800f, DesignH = 600f;

        [UnityTest]
        public IEnumerator Bubble_Lands_On_The_Anchor_And_Is_Occluded_By_Geometry()
        {
            if (!HaveData()) { Assert.Ignore("no AVATAR/SCENE data root"); yield break; }

            var sceneGo = new GameObject("RoomScene3D_bubbleDepth");
            var scene = sceneGo.AddComponent<RoomScene3D>();
            scene.Build();
            for (int i = 0; i < 12; i++) yield return null;
            yield return new WaitForSecondsRealtime(0.5f);

            GameObject holder = null, blocker = null;
            try
            {
                var cam = scene.SceneCamera;
                var rt = scene.SceneTexture as RenderTexture;
                Assert.IsNotNull(cam, "房間相機應該建起來了");
                Assert.IsNotNull(rt, "房間 RT 應該建起來了");
                Assert.IsTrue(scene.TryChatBubbleViewport(out Vector2 vp), "拿不到肩膀錨點的 viewport");
                Assert.IsTrue(scene.TryChatBubbleAnchorWorld(out Vector3 anchorWorld), "拿不到肩膀錨點的世界座標");

                // ---- 照 RoomScreen 的做法擺一張 per-owner 的泡 canvas ----
                holder = new GameObject("BubbleWorldHolder") { layer = RoomScene3D.BubbleLayer };
                var canvas = UIKit.CreateBubbleWorldCanvas("BubbleCanvasTest", holder.transform,
                                                           RoomScene3D.BubbleLayer, new Vector2(DesignW, DesignH));
                Vector3 fwd = cam.transform.forward;
                float bias = scene.OwnerDepthExtent(0, fwd) + 2f;
                Assert.Greater(bias, 2f, "本機角色應該量得出厚度(否則 depthBias 是死的,自己的頭髮會切自己的泡)");
                var plane = RoomBubbleWorldAnchor.Solve(cam.transform.position, fwd, cam.projectionMatrix.m11,
                                                        cam.nearClipPlane, anchorWorld, bias, DesignH);
                Assert.IsTrue(plane.Valid);
                canvas.position = plane.Position;
                canvas.rotation = cam.transform.rotation;
                canvas.localScale = new Vector3(plane.Scale, plane.Scale, plane.Scale);
                canvas.GetComponent<Canvas>().sortingOrder = 100;

                // 標記塊:pivot/anchor (0,1) + anchoredPosition (0,0) → 左上角正好落在錨點的投影位置。
                var mark = UIKit.AddImage(canvas, "Mark", new Color32(255, 0, 255, 255));
                mark.rectTransform.anchorMin = mark.rectTransform.anchorMax = new Vector2(0f, 1f);
                mark.rectTransform.pivot = new Vector2(0f, 1f);
                mark.rectTransform.sizeDelta = new Vector2(MarkerW, MarkerH);
                mark.rectTransform.anchoredPosition = Vector2.zero;
                SetLayer(canvas.gameObject, RoomScene3D.BubbleLayer);
                yield return null;

                // ---- ① 位置 ----
                Vector2 got;
                int count;
                Measure(cam, rt, mark, out got, out count);
                Assert.Greater(count, 500, "標記塊幾乎沒畫出來(改變的像素只有 " + count + " 個)—— 泡沒被房間相機畫到");

                // 期望值:錨點的 viewport → RT 像素,再加上「標記塊中心相對錨點」的設計 px 位移。
                // (RT 是視窗形狀、相機 aspect 釘 4:3 → 兩軸各自換算,不能共用一個比例)
                float expX = vp.x * rt.width + (MarkerW * 0.5f) * (rt.width / DesignW);
                float expY = vp.y * rt.height - (MarkerH * 0.5f) * (rt.height / DesignH);
                Assert.AreEqual(expX, got.x, Mathf.Max(2f, rt.width * 0.006f),
                    "泡的水平位置與『錨點 + 設計 px 位移』不符 → 泡整體偏了(排版與畫的座標系沒對上)");
                Assert.AreEqual(expY, got.y, Mathf.Max(2f, rt.height * 0.006f),
                    "泡的垂直位置與『錨點 + 設計 px 位移』不符");

                // ---- ①b 任意鏈位置也要落在它的絕對設計座標上 ----
                // 這條抓的是「相對位移減錯基準」那一類 bug:泡的排版算出來的是**絕對**設計座標
                // (例如整條鏈往上飄了 60 px),寫進 world canvas 時要減 canvas 原點的投影點。
                // 減成那條鏈的 anchorRoot(它多帶了泡身位移 + 畫布中心)就會固定偏 (5.5, 46.5) px。
                var origin = RoomBubbleWorldAnchor.AnchorDesignPoint(vp, DesignW, DesignH);
                var absPos = new Vector2(origin.x - 120f, origin.y + 75f);   // 任選:左 120、上 75 設計 px
                mark.rectTransform.anchoredPosition = absPos - origin;       // = RoomScreen 的寫法
                yield return null;
                Measure(cam, rt, mark, out Vector2 got2, out int count2);
                Assert.Greater(count2, 500, "移到別的位置之後標記塊消失了");
                Assert.AreEqual((absPos.x + MarkerW * 0.5f) / DesignW * rt.width, got2.x,
                    Mathf.Max(2f, rt.width * 0.006f), "任意鏈位置的水平座標對不上絕對設計座標");
                Assert.AreEqual((1f + (absPos.y - MarkerH * 0.5f) / DesignH) * rt.height, got2.y,
                    Mathf.Max(2f, rt.height * 0.006f), "任意鏈位置的垂直座標對不上絕對設計座標");
                mark.rectTransform.anchoredPosition = Vector2.zero;
                yield return null;

                // ---- ② 遮擋 ----
                // 在泡與相機之間插一片不透明面片(蓋住標記塊的位置)→ 標記塊必須整片消失。
                blocker = GameObject.CreatePrimitive(PrimitiveType.Quad);
                blocker.layer = RoomScene3D.SceneLayer;
                blocker.GetComponent<MeshRenderer>().sharedMaterial =
                    new Material(Shader.Find("Unlit/Color")) { color = new Color32(20, 200, 20, 255) };
                Vector3 markCenterWorld = canvas.TransformPoint(new Vector3(MarkerW * 0.5f, -MarkerH * 0.5f, 0f));
                float dMark = Vector3.Dot(markCenterWorld - cam.transform.position, fwd);
                float dBlock = Mathf.Max(cam.nearClipPlane + 2f, dMark * 0.5f);   // 一半距離 = 明確在泡前面
                blocker.transform.position = cam.transform.position
                    + (markCenterWorld - cam.transform.position) * (dBlock / dMark);
                blocker.transform.rotation = cam.transform.rotation;
                blocker.transform.localScale = Vector3.one * (dBlock * 0.5f);     // 夠大,一定蓋住
                yield return null;

                Vector2 gotBlocked;
                int blocked;
                Measure(cam, rt, mark, out gotBlocked, out blocked);
                Assert.Less(blocked, count * 0.02f,
                    "泡沒有被前面的不透明面片擋住(開/關標記塊仍有 " + blocked + " 個像素在變,原本 " + count
                    + ")—— 深度測試沒生效,整個改動就沒有意義了");
            }
            finally
            {
                if (blocker != null) Object.DestroyImmediate(blocker);
                if (holder != null) Object.DestroyImmediate(holder);
                if (sceneGo != null) Object.DestroyImmediate(sceneGo);
            }
        }

        /// <summary>
        /// 擋在泡前面的是**真的紗質衣物**時也要遮住泡。
        ///
        /// 為什麼要單獨驗:紗質/蕾絲是 alpha-blend 材質,而 alpha-blend 的東西**預設不寫深度** ——
        /// 不寫深度就不會遮任何東西,泡會直接畫在裙子前面(明明人站在前面)。這件事完全取決於
        /// <see cref="SdoAvatarBuilder.ApplySheerMaterialState"/> 有沒有把 <c>_ZWriteMode</c> 開起來,
        /// 而那支函式的目的是別的(修「背面看得到前面」),隨時可能因為別的衣服問題被改回去。
        /// 所以這裡用衣服回歸套裡那件真的多層蕾絲裙(024976 金姬兰)當遮擋物,把這個相依性釘住。
        /// </summary>
        [UnityTest]
        public IEnumerator A_Real_Sheer_Garment_In_Front_Also_Occludes_The_Bubble()
        {
            if (!HaveData()) { Assert.Ignore("no AVATAR/SCENE data root"); yield break; }
            if (!HaveGarment(SheerGarment)) { Assert.Ignore("missing " + SheerGarment); yield break; }

            var sceneGo = new GameObject("RoomScene3D_sheerBlock");
            var scene = sceneGo.AddComponent<RoomScene3D>();
            scene.Build();
            for (int i = 0; i < 12; i++) yield return null;
            yield return new WaitForSecondsRealtime(0.5f);

            GameObject holder = null, blocker = null;
            try
            {
                var cam = scene.SceneCamera;
                var rt = scene.SceneTexture as RenderTexture;
                Assert.IsNotNull(cam); Assert.IsNotNull(rt);
                Assert.IsTrue(scene.TryChatBubbleAnchorWorld(out Vector3 anchorWorld));

                holder = new GameObject("BubbleWorldHolder2") { layer = RoomScene3D.BubbleLayer };
                var canvas = UIKit.CreateBubbleWorldCanvas("BubbleCanvasSheer", holder.transform,
                                                           RoomScene3D.BubbleLayer, new Vector2(DesignW, DesignH));
                Vector3 fwd = cam.transform.forward;
                var plane = RoomBubbleWorldAnchor.Solve(cam.transform.position, fwd, cam.projectionMatrix.m11,
                                                        cam.nearClipPlane, anchorWorld,
                                                        scene.OwnerDepthExtent(0, fwd) + 2f, DesignH);
                Assert.IsTrue(plane.Valid);
                canvas.position = plane.Position;
                canvas.rotation = cam.transform.rotation;
                canvas.localScale = new Vector3(plane.Scale, plane.Scale, plane.Scale);
                canvas.GetComponent<Canvas>().sortingOrder = 100;

                var mark = UIKit.AddImage(canvas, "Mark", new Color32(255, 0, 255, 255));
                mark.rectTransform.anchorMin = mark.rectTransform.anchorMax = new Vector2(0f, 1f);
                mark.rectTransform.pivot = new Vector2(0f, 1f);
                mark.rectTransform.sizeDelta = new Vector2(MarkerW, MarkerH);
                mark.rectTransform.anchoredPosition = Vector2.zero;
                SetLayer(canvas.gameObject, RoomScene3D.BubbleLayer);
                yield return null;

                Measure(cam, rt, mark, out Vector2 _, out int before);
                Assert.Greater(before, 500, "標記塊沒畫出來,後面的斷言就沒有意義");

                // 紗裙:走與房間角色完全同一條建置路徑(材質狀態才會一致)。
                blocker = new GameObject("sheerBlocker");
                var gav = SdoRoomAvatar.Build(blocker, RoomScene3D.SceneLayer, portraitOpaque: false, male: false,
                                              equippedParts: new[] { SheerGarment });
                Assert.IsNotNull(gav, "紗裙 avatar 建不起來");
                gav.enabled = false;   // 凍住姿勢:量測期間剪影不要變

                var b = MergedBounds(blocker);
                Assert.Greater(b.size.magnitude, 0.01f, "紗裙沒有任何 renderer bounds");

                // 擺到「相機 → 標記塊中心」那條線的一半距離,並放大到遠遠蓋過標記塊。
                Vector3 markCenter = canvas.TransformPoint(new Vector3(MarkerW * 0.5f, -MarkerH * 0.5f, 0f));
                float dMark = Vector3.Dot(markCenter - cam.transform.position, fwd);
                float dBlock = Mathf.Max(cam.nearClipPlane + 2f, dMark * 0.5f);
                float needWorld = MarkerH * (2f * dBlock / (DesignH * cam.projectionMatrix.m11)) * 8f;
                float scale = needWorld / Mathf.Max(0.01f, b.size.y);
                blocker.transform.localScale = Vector3.one * scale;
                blocker.transform.rotation = cam.transform.rotation;
                // 縮放/旋轉之後 bounds 會變 → 重新量,把 bounds 中心對到那條視線上。
                var b2 = MergedBounds(blocker);
                Vector3 want = cam.transform.position + (markCenter - cam.transform.position) * (dBlock / dMark);
                blocker.transform.position += want - b2.center;
                yield return null;

                Measure(cam, rt, mark, out Vector2 _, out int after);
                Assert.Less(after, before * 0.35f,
                    "真的紗質衣物擋在泡前面時沒有遮住泡(剩 " + after + " / 原 " + before + " 個像素)。"
                    + "alpha-blend 材質預設不寫深度 → 檢查 SdoAvatarBuilder.ApplySheerMaterialState 是否還在把 "
                    + "_ZWriteMode 設成 1;若那裡因為別的衣服問題必須關掉,泡就要改用另外一顆「只寫深度」的代理 renderer。");
            }
            finally
            {
                if (blocker != null) Object.DestroyImmediate(blocker);
                if (holder != null) Object.DestroyImmediate(holder);
                if (sceneGo != null) Object.DestroyImmediate(sceneGo);
            }
        }

        /// <summary>
        /// 名字牌與頭上泡的兩條關係(使用者要求):
        ///   ① 名字**也要**被站在前面的人擋住(與泡同一個平面 → 同樣吃深度)。
        ///   ② 泡永遠畫在名字**之上** —— 否則自己說話時會被自己的名字擋住。
        ///
        /// 為什麼要測:兩者都在同一張 canvas、而 UI 材質不寫深度 → 它們之間的前後**只由兄弟順序決定**。
        /// 那是一個「看起來沒關係」的隱性相依:哪天有人在名字後面才 SetParent 進去(或把 SetAsFirstSibling 拿掉),
        /// 泡就會被名字壓住,而症狀只在「名字與泡剛好重疊」時出現 —— 很容易被當成偶發。
        /// </summary>
        [UnityTest]
        public IEnumerator Name_Is_Occluded_Like_The_Bubble_And_The_Bubble_Draws_Over_The_Name()
        {
            if (!HaveData()) { Assert.Ignore("no AVATAR/SCENE data root"); yield break; }

            var sceneGo = new GameObject("RoomScene3D_nameDepth");
            var scene = sceneGo.AddComponent<RoomScene3D>();
            scene.Build();
            for (int i = 0; i < 12; i++) yield return null;
            yield return new WaitForSecondsRealtime(0.5f);

            GameObject holder = null, blocker = null;
            try
            {
                var cam = scene.SceneCamera;
                var rt = scene.SceneTexture as RenderTexture;
                Assert.IsNotNull(cam); Assert.IsNotNull(rt);
                Assert.IsTrue(scene.TryChatBubbleAnchorWorld(out Vector3 anchorWorld));

                holder = new GameObject("BubbleWorldHolder3") { layer = RoomScene3D.BubbleLayer };
                var canvas = UIKit.CreateBubbleWorldCanvas("BubbleCanvasName", holder.transform,
                                                           RoomScene3D.BubbleLayer, new Vector2(DesignW, DesignH));
                Vector3 fwd = cam.transform.forward;
                var plane = RoomBubbleWorldAnchor.Solve(cam.transform.position, fwd, cam.projectionMatrix.m11,
                                                        cam.nearClipPlane, anchorWorld,
                                                        scene.OwnerDepthExtent(0, fwd) + 2f, DesignH);
                Assert.IsTrue(plane.Valid);
                canvas.position = plane.Position;
                canvas.rotation = cam.transform.rotation;
                canvas.localScale = new Vector3(plane.Scale, plane.Scale, plane.Scale);
                canvas.GetComponent<Canvas>().sortingOrder = 100;

                // 名字(洋紅)先建 → 兄弟順序在前;泡(青)後建 → 畫在名字之上。兩者故意**重疊**。
                var name = AddMarker(canvas, "NameMarker", new Color32(255, 0, 255, 255), Vector2.zero);
                var bubble = AddMarker(canvas, "BubbleMarker", new Color32(0, 255, 255, 255), new Vector2(20f, -10f));
                name.rectTransform.SetAsFirstSibling();   // = ParentNameIntoOwnerCanvas 做的事
                SetLayer(canvas.gameObject, RoomScene3D.BubbleLayer);
                yield return null;

                // ---- ② 重疊處由泡贏 ----
                // 🔴 量法一律用 Measure(同一幀開/關那一塊再相減)—— 它量的正是「這一塊對最終畫面貢獻了幾個像素」,
                //    也就是**沒被別人蓋掉的面積**。用顏色數像素會被房間本身的洋紅燈光污染(這個檔頭就寫著這個教訓,
                //    我第一版還是踩了:名字量到 16389 px,而一塊標記其實只有 ~3.5k)。
                //
                // 兩塊各 60×40、泡偏移 (20,-10) → 重疊剛好是各自面積的一半。
                //   泡在上 ⇒ 泡看得到全部、名字只剩一半 ⇒ nameVisible ≈ bubbleVisible / 2
                //   名字在上 ⇒ 反過來。0.75 當門檻,兩種情形分得很開。
                Measure(cam, rt, bubble, out Vector2 _, out int bubbleVisible);
                Measure(cam, rt, name, out Vector2 _, out int nameVisible);
                Assert.Greater(bubbleVisible, 200, "泡的標記沒畫出來");
                Assert.Greater(nameVisible, 100, "名字的標記沒畫出來(整塊被蓋住?)");
                Assert.Less(nameVisible, bubbleVisible * 0.75f,
                    "名字可見 " + nameVisible + " px、泡可見 " + bubbleVisible + " px —— 泡沒有蓋在名字上面,"
                    + "自己說話時泡會被自己的名字擋住(檢查 ParentNameIntoOwnerCanvas 的 SetAsFirstSibling)");

                // ---- ① 名字也要被前面的東西擋住 ----
                Vector3 nameCenter = canvas.TransformPoint(new Vector3(MarkerW * 0.5f, -MarkerH * 0.5f, 0f));
                float dMark = Vector3.Dot(nameCenter - cam.transform.position, fwd);
                float dBlock = Mathf.Max(cam.nearClipPlane + 2f, dMark * 0.5f);
                blocker = GameObject.CreatePrimitive(PrimitiveType.Quad);
                blocker.layer = RoomScene3D.SceneLayer;
                blocker.GetComponent<MeshRenderer>().sharedMaterial =
                    new Material(Shader.Find("Unlit/Color")) { color = new Color32(20, 200, 20, 255) };
                blocker.transform.position = cam.transform.position
                    + (nameCenter - cam.transform.position) * (dBlock / dMark);
                blocker.transform.rotation = cam.transform.rotation;
                blocker.transform.localScale = Vector3.one * (dBlock * 0.5f);
                yield return null;

                Measure(cam, rt, name, out Vector2 _, out int nameAfter);
                Assert.Less(nameAfter, 60,
                    "名字沒有被前面的不透明面片擋住(剩 " + nameAfter + " 個像素)—— 名字沒有跟泡一樣進到房間相機裡");
            }
            finally
            {
                if (blocker != null) Object.DestroyImmediate(blocker);
                if (holder != null) Object.DestroyImmediate(holder);
                if (sceneGo != null) Object.DestroyImmediate(sceneGo);
            }
        }

        private static Image AddMarker(RectTransform canvas, string name, Color32 col, Vector2 at)
        {
            var img = UIKit.AddImage(canvas, name, col);
            img.rectTransform.anchorMin = img.rectTransform.anchorMax = new Vector2(0f, 1f);
            img.rectTransform.pivot = new Vector2(0f, 1f);
            img.rectTransform.sizeDelta = new Vector2(MarkerW, MarkerH);
            img.rectTransform.anchoredPosition = at;
            return img;
        }

        /// <summary>衣服回歸套裡那件真的多層蕾絲裙([[sdo-garment-regression-suite]] 的 024976 金姬兰)。</summary>
        private const string SheerGarment = "AVATAR/024976_WOMAN_ONE.MSH";

        private static Bounds MergedBounds(GameObject root)
        {
            var rends = root.GetComponentsInChildren<Renderer>();
            bool any = false;
            var b = new Bounds();
            foreach (var r in rends)
            {
                if (r == null) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            return any ? b : new Bounds();
        }

        private static bool HaveGarment(string rel)
        {
            var p = SdoAvatarBuilder.ResolveAvatarFile(rel);
            return !string.IsNullOrEmpty(p) && File.Exists(p);
        }

        private static bool HaveData()
        {
            var probe = SdoAvatarBuilder.ResolveAvatarFile("AVATAR/900007_WOMAN_FACE.MSH");
            return !string.IsNullOrEmpty(probe) && File.Exists(probe);
        }

        private static void SetLayer(GameObject go, int layer)
        {
            go.layer = layer;
            for (int i = 0; i < go.transform.childCount; i++) SetLayer(go.transform.GetChild(i).gameObject, layer);
        }

        /// <summary>
        /// 標記塊真正畫在畫面上的重心與面積 —— **同一幀內**關掉它拍一張、開起來再拍一張,取差集。
        /// 同一幀不 yield ⇒ 場景動畫/角色姿勢都沒推進,所以差集裡只會有標記塊。
        /// 重心用 RT 像素座標(原點左下,與 viewport 同向)。
        /// </summary>
        private static void Measure(Camera cam, RenderTexture rt, Graphic mark, out Vector2 centroid, out int count)
        {
            mark.enabled = false;
            var off = Shoot(cam, rt);
            mark.enabled = true;
            var on = Shoot(cam, rt);

            int w = rt.width, h = rt.height;
            double sx = 0, sy = 0;
            count = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    int d = Mathf.Abs(on[i].r - off[i].r) + Mathf.Abs(on[i].g - off[i].g) + Mathf.Abs(on[i].b - off[i].b);
                    if (d <= 40) continue;
                    sx += x + 0.5; sy += y + 0.5; count++;
                }
            centroid = count > 0 ? new Vector2((float)(sx / count), (float)(sy / count)) : Vector2.zero;
        }

        private static Color32[] Shoot(Camera cam, RenderTexture rt)
        {
            Canvas.ForceUpdateCanvases();
            cam.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            var px = tex.GetPixels32();
            Object.DestroyImmediate(tex);
            return px;
        }
    }
}
