using Sdo.Game;
using Sdo.UI.Util;
using UnityEngine;

namespace Sdo.UI.Screens
{
    /// <summary>
    /// 房間裡**一個人**頭上那組名字牌(名字 + 家族列 + 徽章)住的地方。
    ///
    /// 以前這是一張貼在 UI 上的透明層,於是整組名字疊在房間 RenderTexture 之上 ——
    /// **任何人的名字都畫在所有 3D 內容前面**,站在後面那個人的名字牌就浮在站在前面那個人的身體上
    /// (使用者回報)。UI 層永遠救不了這件事:那一層根本沒有參與房間相機的深度緩衝。
    ///
    /// 所以每個人的名字牌改成一張 <b>world-space canvas</b>,由房間相機一起 render:
    ///
    ///   ① <see cref="Root"/> = canvas 本體。每幀貼到「那個人的頭部錨點」那張正對相機的平面上
    ///      (<see cref="RoomBubbleWorldAnchor.Solve"/>),縮放隨距離補償 ⇒ 螢幕位置與大小
    ///      **與畫在 UI 上時逐像素相同**,唯一的差別就是它現在會被前面的東西擋住。
    ///   ② <see cref="Content"/> = 中間夾的內容層,位移 = <see cref="RoomBubbleWorldAnchor.ContentOffset"/>。
    ///      它存在的唯一理由是「讓子物件繼續用 800×600 的絕對設計座標」——
    ///      RoomScreen.PlaceFollow / RoomFamilyRow.Place 一行都不用改。
    ///
    /// 遮擋規則(使用者定案):**人和場景都會擋**。所以這裡不需要泡當年那套深度重置片 + 隱形分身 ——
    /// 就是老老實實吃房間相機的深度緩衝。半透明的衣物(alpha-blend 不寫深度)因此擋不住名字,這是對的。
    ///
    /// 🔴 canvas **不可以**掛在任何 Canvas 底下(會被降級成 nested canvas,renderMode 被忽略 →
    ///    名字牌靜默回到 UI 層、遮擋消失)。所以 RoomScreen 把它們掛在一個場景根物件底下,不是掛在 Root 上。
    /// </summary>
    public sealed class RoomNamePlateAnchor
    {
        /// <summary>設計框(與 UI 同一組 800×600 絕對座標)。</summary>
        public const float DesignW = 800f, DesignH = 600f;

        /// <summary>
        /// 名字牌 canvas 的 sortingOrder 起點。**必須 &gt; 0** ——
        /// 房間場景(牆/家具/角色/玻璃)全在 0,給 0 的話名字牌會跟場景的透明批混在一起排,
        /// 窗玻璃那種 ZWrite Off 的東西就會蓋在名字上(見 <see cref="RoomBubbleDrawOrder.ApplyFarToNearSorting"/>)。
        /// </summary>
        public const int SortingBase = 1;

        private readonly Canvas _canvas;
        private readonly RectTransform _root;
        private readonly RectTransform _content;

        private RoomNamePlateAnchor(Canvas canvas, RectTransform root, RectTransform content)
        {
            _canvas = canvas;
            _root = root;
            _content = content;
        }

        /// <summary>畫序用(每幀依站位重排)。</summary>
        public Canvas Canvas => _canvas;

        /// <summary>名字/家族列掛這裡。座標仍是 800×600 的絕對設計座標。</summary>
        public RectTransform Content => _content;

        /// <summary>建一組。<paramref name="parent"/> 不可以是 Canvas 的後代(見類別註解)。</summary>
        public static RoomNamePlateAnchor Create(Transform parent, string name, int layer)
        {
            var root = UIKit.CreateDepthTestedWorldCanvas(name, parent, layer, new Vector2(DesignW, DesignH));
            var canvas = root.GetComponent<Canvas>();
            canvas.sortingOrder = SortingBase;

            var content = UIKit.NewRect(root, "Content");
            content.anchorMin = content.anchorMax = new Vector2(0f, 1f);
            content.pivot = new Vector2(0f, 1f);
            content.sizeDelta = new Vector2(DesignW, DesignH);
            UIKit.SetLayerRecursive(root.gameObject, layer);
            // 建好時是關著的:整組要等第一次 <see cref="Place"/> 才知道該貼在哪個深度。開著的話,
            // 「人剛進房、角色還沒生出來」那幾幀會有一張 800×600 的 canvas 停在世界原點(scale 1)——
            // 那是一片橫在房間中央的巨大名字。
            root.gameObject.SetActive(false);
            return new RoomNamePlateAnchor(canvas, root, content);
        }

        /// <summary>
        /// 這一組剛加了子物件(名字牌/家族列)→ 把整棵樹搬回名字牌的 layer。
        /// 🔴 漏叫的症狀是「那個人的名字整個不見」(子物件生在 layer 0,房間相機看不到),
        /// 而不是「位置不對」—— 見 <see cref="UIKit.SetLayerRecursive"/>。
        /// </summary>
        public void RefreshLayer(int layer) => UIKit.SetLayerRecursive(_root.gameObject, layer);

        /// <summary>
        /// 每幀把整組貼到那個人頭上。<paramref name="anchorWorld"/> 是頭部錨點的世界座標,
        /// <paramref name="vp"/> 是同一個點的 viewport(兩者必須來自同一個錨點,不然位置與深度會對不上)。
        /// 回 false = 這一幀不該畫(在相機後面/太近),呼叫端已經把子物件關掉,這裡順手把整張 canvas 也關掉。
        /// </summary>
        public bool Place(Camera cam, Vector3 anchorWorld, Vector2 vp)
        {
            if (cam == null || _root == null) return false;
            var plane = RoomBubbleWorldAnchor.Solve(cam.transform.position, cam.transform.forward,
                                                    cam.projectionMatrix.m11, cam.nearClipPlane,
                                                    anchorWorld, 0f, DesignH);
            SetActive(plane.Valid);
            if (!plane.Valid) return false;

            _root.SetPositionAndRotation(plane.Position, cam.transform.rotation);
            _root.localScale = Vector3.one * plane.Scale;
            _content.anchoredPosition = RoomBubbleWorldAnchor.ContentOffset(vp, DesignW, DesignH);
            return true;
        }

        public void SetActive(bool on)
        {
            if (_root != null && _root.gameObject.activeSelf != on) _root.gameObject.SetActive(on);
        }

        public void Destroy()
        {
            if (_root != null) Object.Destroy(_root.gameObject);
        }
    }
}
