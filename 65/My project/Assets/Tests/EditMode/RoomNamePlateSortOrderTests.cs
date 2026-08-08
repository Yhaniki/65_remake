using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Game;
using UnityEngine;

namespace Sdo.Tests
{
    /// <summary>
    /// 名字牌與名字牌之間的前後 = **人站的位置**(使用者回報:「我站前面,可是遠端後面那個人的名牌擋在我前面」)。
    ///
    /// 名字牌現在一個人一張 world canvas(由房間相機畫,才吃得到深度遮擋),所以「誰蓋誰」不再由
    /// sibling 順序決定 —— 獨立的 root canvas 之間只看 <c>sortingOrder</c>。重排在
    /// <see cref="RoomBubbleDrawOrder.ApplyFarToNearSorting"/>(RoomScreen.SortNamePlateLayers 每幀呼叫)。
    ///
    /// 這裡釘住三件事:方向(近的人 order 大)、起點必須 &gt; 0(場景在 0,給 0 會被玻璃那類透明物蓋掉),
    /// 以及「量不到深度的排最後面」。
    /// </summary>
    public class RoomNamePlateSortOrderTests
    {
        private const int Base = 1;   // = RoomNamePlateAnchor.SortingBase

        private readonly List<GameObject> _spawned = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _spawned.Count; i++) if (_spawned[i] != null) Object.DestroyImmediate(_spawned[i]);
            _spawned.Clear();
        }

        private Canvas NewCanvas(string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Canvas));
            _spawned.Add(go);
            var c = go.GetComponent<Canvas>();
            c.renderMode = RenderMode.WorldSpace;
            return c;
        }

        private static int[] Orders(List<Canvas> canvases)
        {
            var o = new int[canvases.Count];
            for (int i = 0; i < canvases.Count; i++) o[i] = canvases[i].sortingOrder;
            return o;
        }

        [Test]
        public void The_Nearer_Persons_Nameplate_Draws_Last()
        {
            // 建立順序刻意是「本機先、遠端後」——修之前的 bug:本機那面在 BuildUI 就建好,
            // 所以無論誰站前面,後建的遠端名字牌都畫在它上面。
            var local = NewCanvas("Owner0");        // 我(站前面,深度 100)
            var remote = NewCanvas("Owner1234");    // 遠端(站後面,深度 300)
            var canvases = new List<Canvas> { local, remote };

            RoomBubbleDrawOrder.ApplyFarToNearSorting(canvases, new List<float> { 100f, 300f }, new List<int>(), Base);

            var o = Orders(canvases);
            Assert.Greater(o[0], o[1], "站在前面的自己的名字牌沒有蓋住後面那個人的(排序方向反了)");
        }

        [Test]
        public void Every_Canvas_Sits_Above_The_Room_Scene()
        {
            // 🔴 場景(牆/家具/角色/玻璃)全在 sortingOrder 0,而 sortingOrder 比 renderQueue 優先。
            // 起點給 0 的話名字牌會與場景的透明批混在一起排 → 窗玻璃那種 ZWrite Off 的東西會蓋在名字上。
            var canvases = new List<Canvas> { NewCanvas("A"), NewCanvas("B"), NewCanvas("C") };
            RoomBubbleDrawOrder.ApplyFarToNearSorting(canvases, new List<float> { 300f, 100f, 200f }, new List<int>(), Base);

            foreach (var o in Orders(canvases)) Assert.Greater(o, 0, "名字牌排在場景的透明批之前 → 會被玻璃/光柱蓋掉");
        }

        [Test]
        public void A_Lone_Nameplate_Still_Gets_The_Base_Order()
        {
            // 房裡只有自己(離線就是這樣)。「只有一個就不用排」的 early-out 會讓它停在 canvas 預設的 0 →
            // 整張名字牌被場景的透明批蓋掉,而且只有站在噴水池/玻璃前面時才看得出來。
            var only = NewCanvas("Owner0");
            RoomBubbleDrawOrder.ApplyFarToNearSorting(new List<Canvas> { only }, new List<float> { 120f },
                                                      new List<int>(), Base);
            Assert.AreEqual(Base, only.sortingOrder);
        }

        [Test]
        public void Walking_Behind_Someone_Flips_Which_Nameplate_Is_On_Top()
        {
            var local = NewCanvas("Owner0");
            var remote = NewCanvas("Owner1234");
            var canvases = new List<Canvas> { local, remote };
            var scratch = new List<int>();

            RoomBubbleDrawOrder.ApplyFarToNearSorting(canvases, new List<float> { 100f, 300f }, scratch, Base);
            Assert.Greater(Orders(canvases)[0], Orders(canvases)[1]);

            // 我往後走到他後面 → 換他的名字牌蓋住我的。(每幀重排 ⇒ 上一幀的 order 不能有殘留影響)
            RoomBubbleDrawOrder.ApplyFarToNearSorting(canvases, new List<float> { 400f, 300f }, scratch, Base);
            var o = Orders(canvases);
            Assert.Greater(o[1], o[0], "走到別人後面之後,我的名字牌沒有沉下去");
        }

        [Test]
        public void Every_Canvas_Gets_A_Distinct_Order_Far_To_Near()
        {
            var canvases = new List<Canvas>();
            for (int i = 0; i < 5; i++) canvases.Add(NewCanvas("Owner" + i));
            var depths = new List<float> { 500f, 120f, 300f, 900f, 200f };

            RoomBubbleDrawOrder.ApplyFarToNearSorting(canvases, depths, new List<int>(), Base);

            var o = Orders(canvases);
            var seen = new HashSet<int>(o);
            Assert.AreEqual(o.Length, seen.Count, "有兩張 canvas 拿到同一個 sortingOrder");
            for (int a = 0; a < depths.Count; a++)
                for (int b = 0; b < depths.Count; b++)
                    if (depths[a] < depths[b])
                        Assert.Greater(o[a], o[b], "近的(" + depths[a] + ")沒有畫在遠的(" + depths[b] + ")之後");
        }

        [Test]
        public void A_Nameplate_With_No_Known_Depth_Sits_At_The_Back()
        {
            // 這一幀量不到深度(人剛進來/走到鏡頭後面)→ RoomScreen 填 float.MaxValue,
            // 它該排最遠而不是冒出來蓋住所有人。
            var canvases = new List<Canvas> { NewCanvas("A"), NewCanvas("B"), NewCanvas("C") };
            RoomBubbleDrawOrder.ApplyFarToNearSorting(canvases, new List<float> { 100f, float.MaxValue, 300f },
                                                      new List<int>(), Base);

            var o = Orders(canvases);
            Assert.AreEqual(Base, o[1], "深度不明的名字牌沒有排在最後面");
            Assert.AreEqual(Base + 2, o[0], "最近的名字牌沒有畫在最上面");
        }

        [Test]
        public void Mismatched_Or_Trivial_Inputs_Do_Nothing()
        {
            var a = NewCanvas("A");
            var b = NewCanvas("B");
            var canvases = new List<Canvas> { a, b };

            // 長度對不上 → 什麼都不做(寧可維持上一幀的順序,也不要照錯的深度亂排)。
            RoomBubbleDrawOrder.ApplyFarToNearSorting(canvases, new List<float> { 100f }, new List<int>(), Base);
            Assert.AreEqual(0, a.sortingOrder);
            Assert.AreEqual(0, b.sortingOrder);

            // null 進來不該炸(每幀跑一次)。
            Assert.DoesNotThrow(() => RoomBubbleDrawOrder.ApplyFarToNearSorting(null, new List<float>(), new List<int>(), Base));
            Assert.DoesNotThrow(() => RoomBubbleDrawOrder.ApplyFarToNearSorting(canvases, null, new List<int>(), Base));
            Assert.DoesNotThrow(() => RoomBubbleDrawOrder.ApplyFarToNearSorting(canvases, new List<float> { 1f, 2f }, null, Base));
        }

        [Test]
        public void A_Destroyed_Canvas_Does_Not_Break_The_Rest()
        {
            // 人剛離開房間、canvas 已被 Destroy,但這一幀的 list 還握著它(Unity 的 null 是「假 null」)。
            var gone = NewCanvas("Gone");
            var alive = NewCanvas("Alive");
            var canvases = new List<Canvas> { gone, alive };
            Object.DestroyImmediate(gone.gameObject);

            Assert.DoesNotThrow(() => RoomBubbleDrawOrder.ApplyFarToNearSorting(canvases, new List<float> { 100f, 300f },
                                                                                new List<int>(), Base));
            Assert.AreEqual(Base, alive.sortingOrder, "活著的那張沒有拿到名次(被死掉的那張中斷了)");
        }
    }
}
