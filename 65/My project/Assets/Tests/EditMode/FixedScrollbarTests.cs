using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Sdo.UI.Util;

namespace Sdo.Tests.EditMode
{
    /// <summary>
    /// 大廳三條捲軸(房間列表 / 聊天記錄 / 玩家名單)的行為。
    ///
    /// 🔴 這組測試是**代價換來的**:捲軸「滑鼠左鍵拉不動」被回報四輪,握把「被拉成貫穿整條軌道的長條」
    ///    又是實機截圖才發現的。兩件事在 editor 裡都看不出來(靜態截圖看不出拖不拖得動,
    ///    而 Scrollbar 的幾何要跑起來才成形),所以只能在這裡守。
    /// </summary>
    public sealed class FixedScrollbarTests
    {
        private const float HandleW = 14f, HandleH = 28f, TrackH = 300f;

        private GameObject _root;
        private RectTransform _canvasRt;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            var canvas = _root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;   // 螢幕座標 = canvas 座標,換算最單純
            _canvasRt = (RectTransform)_root.transform;
            _canvasRt.sizeDelta = new Vector2(800f, 600f);
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_root);

        private Scrollbar Make() =>
            FixedScrollbar.Create(_canvasRt, "Bar", null, 100f, 50f, HandleW, HandleH, TrackH);

        // ------------------------------------------------------------ 幾何

        /// <summary>
        /// 握把必須維持官方素材的 14×28。🔴 這條擋的是真的發生過的事:
        /// Scrollbar 只改 handleRect 的 anchorMin/Max、**不動 sizeDelta**,而 RectTransform 的實際大小是
        /// 「anchor 撐出的大小 + sizeDelta」—— 留著預設的 100×100,握把就變成一根貫穿軌道的長條。
        /// </summary>
        [Test]
        public void Handle_KeepsOfficialSize()
        {
            var sb = Make();
            var art = sb.handleRect.Find("Art") as RectTransform;
            Assert.That(art, Is.Not.Null, "握把的圖層不見了");
            Assert.That(sb.handleRect.rect.height, Is.EqualTo(HandleH).Within(0.5f), "握把高度被 Scrollbar 拉走了");
            Assert.That(art.rect.width, Is.EqualTo(HandleW).Within(0.5f), "握把寬度被命中區的 padding 撐胖了");
            Assert.That(art.rect.height, Is.EqualTo(HandleH).Within(0.5f));
        }

        /// <summary>命中區要比握把寬(14px 的軌道用滑鼠點不準),但握把的圖不能跟著變胖 —— 上一條測過寬度。</summary>
        [Test]
        public void Track_IsWiderThanHandle_ForEasierClicking()
        {
            var sb = Make();
            var rt = (RectTransform)sb.transform;
            Assert.That(rt.rect.width, Is.GreaterThan(HandleW), "命中區沒有加寬,實機上很難點中");
            Assert.That(rt.rect.height, Is.EqualTo(TrackH).Within(0.5f));
        }

        /// <summary>使用者要求握把**永遠顯示**,而且預設停在最上面(官方就是這樣)。</summary>
        [Test]
        public void StartsAtTop()
        {
            Assert.That(Make().value, Is.EqualTo(1f).Within(0.001f));
        }

        // ------------------------------------------------------------ 點軌道跳位

        /// <summary>把捲軸局部 y 換成螢幕座標,餵給事件系統(Overlay canvas 下相機是 null)。</summary>
        private static PointerEventData PointAt(Scrollbar sb, float localY)
        {
            var rt = (RectTransform)sb.transform;
            var screen = RectTransformUtility.WorldToScreenPoint(null, rt.TransformPoint(new Vector3(0f, localY, 0f)));
            return new PointerEventData(null) { position = screen, button = PointerEventData.InputButton.Left };
        }

        private static void ClickTrack(Scrollbar sb, float localY) =>
            sb.GetComponent<ScrollbarTrackJump>().OnPointerDown(PointAt(sb, localY));

        /// <summary>
        /// 點軌道 → 握把**跳到那個位置**(使用者明確要求的)。
        /// 🔴 Unity 內建的 Scrollbar 點軌道只會「分頁跳」(每幀 value ± size),所以這個行為得自己補;
        /// 這條測試就是在守那個補丁還在。
        /// </summary>
        [Test]
        public void ClickingTrack_JumpsToThatPosition()
        {
            var sb = Make();
            var r = ((RectTransform)sb.transform).rect;
            float half = r.height * sb.size * 0.5f;

            ClickTrack(sb, r.yMin + half);            // 軌道最下
            Assert.That(sb.value, Is.EqualTo(0f).Within(0.02f), "點軌道底部沒有跳到最下");

            ClickTrack(sb, r.center.y);               // 正中間
            Assert.That(sb.value, Is.EqualTo(0.5f).Within(0.02f), "點軌道中間沒有跳到中間");

            ClickTrack(sb, r.yMax - half);            // 軌道最上
            Assert.That(sb.value, Is.EqualTo(1f).Within(0.02f), "點軌道頂部沒有跳到最上");
        }

        /// <summary>點在握把身上是要拖它,不能瞬移(不然按下去的瞬間握把自己置中,手感很怪)。</summary>
        [Test]
        public void ClickingHandle_DoesNotJump()
        {
            var sb = Make();
            sb.value = 1f;                            // 握把在最上
            var r = ((RectTransform)sb.transform).rect;
            ClickTrack(sb, r.yMax - r.height * sb.size * 0.5f);
            Assert.That(sb.value, Is.EqualTo(1f).Within(0.001f));
        }

        /// <summary>右鍵/中鍵不該讓捲軸亂跳 —— 右鍵在大廳是叫房間選單的。</summary>
        [Test]
        public void RightClick_Ignored()
        {
            var sb = Make();
            var r = ((RectTransform)sb.transform).rect;
            var e = PointAt(sb, r.yMin + r.height * sb.size * 0.5f);
            e.button = PointerEventData.InputButton.Right;
            sb.GetComponent<ScrollbarTrackJump>().OnPointerDown(e);
            Assert.That(sb.value, Is.EqualTo(1f).Within(0.001f));
        }

        // ------------------------------------------------------------ 與 ScrollRect 同步

        private ScrollRect MakeScroll(float contentH, float viewportH)
        {
            var srGo = new GameObject("Scroll", typeof(RectTransform), typeof(ScrollRect));
            srGo.transform.SetParent(_canvasRt, false);
            var sr = srGo.GetComponent<ScrollRect>();

            var vp = new GameObject("Viewport", typeof(RectTransform)).GetComponent<RectTransform>();
            vp.SetParent(srGo.transform, false);
            vp.anchorMin = vp.anchorMax = new Vector2(0f, 1f);
            vp.pivot = new Vector2(0f, 1f);
            vp.sizeDelta = new Vector2(200f, viewportH);

            var content = new GameObject("Content", typeof(RectTransform)).GetComponent<RectTransform>();
            content.SetParent(vp, false);
            content.anchorMin = content.anchorMax = new Vector2(0f, 1f);
            content.pivot = new Vector2(0f, 1f);
            content.sizeDelta = new Vector2(200f, contentH);

            sr.viewport = vp; sr.content = content; sr.horizontal = false;
            return sr;
        }

        /// <summary>拉捲軸 → 內容跟著捲。</summary>
        [Test]
        public void Bind_BarDrivesScroll()
        {
            var sb = Make();
            var sr = MakeScroll(600f, 100f);
            FixedScrollbar.Bind(sb, sr);

            sb.value = 0f;
            Assert.That(sr.verticalNormalizedPosition, Is.EqualTo(0f).Within(0.02f), "捲軸拉到底,內容沒跟著到底");
            sb.value = 1f;
            Assert.That(sr.verticalNormalizedPosition, Is.EqualTo(1f).Within(0.02f));
        }

        /// <summary>
        /// 滾輪捲內容 → 握把跟著動。
        ///
        /// 🔴 這裡要**手動呼叫 Sync**,不能只設 verticalNormalizedPosition 就期待握把自己動:
        ///    ScrollRect 的 onValueChanged 是在它的 LateUpdate 裡發的,EditMode 測試沒有那個迴圈。
        ///    Bind 把 Sync 掛上 onValueChanged 這件事只有實機跑得到,這條守的是換算本身。
        /// </summary>
        [Test]
        public void Bind_ScrollDrivesBar()
        {
            var sb = Make();
            var sr = MakeScroll(600f, 100f);
            FixedScrollbar.Bind(sb, sr);

            sr.verticalNormalizedPosition = 0.25f;
            FixedScrollbar.Sync(sb, sr);
            Assert.That(sb.value, Is.EqualTo(0.25f).Within(0.02f), "內容捲了,握把沒跟上");
        }

        /// <summary>
        /// 內容比視窗短的時候握把要停在最上面 —— 這種情況 Unity 回的 verticalNormalizedPosition
        /// 算不出比例(通常固定回 1 或 0),照字面用會讓握把停在莫名其妙的位置。
        /// 握把本身仍要顯示(使用者要求),所以只調位置、不隱藏。
        /// </summary>
        [Test]
        public void Sync_ShortContent_ParksAtTop()
        {
            var sb = Make();
            var sr = MakeScroll(50f, 100f);
            sb.value = 0f;
            FixedScrollbar.Sync(sb, sr);
            Assert.That(sb.value, Is.EqualTo(1f).Within(0.001f));
            Assert.That(sb.handleRect.gameObject.activeInHierarchy, Is.True, "握把要永遠看得到");
        }
    }
}
