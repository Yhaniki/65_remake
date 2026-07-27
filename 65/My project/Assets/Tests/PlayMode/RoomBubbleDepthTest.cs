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
