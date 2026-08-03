using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Game;
using UnityEngine;

namespace Sdo.Tests
{
    /// <summary>
    /// 名字牌與名字牌之間的前後 = **人站的位置**(使用者回報:「我站前面,可是遠端後面那個人的名牌擋在我前面」)。
    ///
    /// 名字牌全部畫在 UI 裡 ⇒ 誰蓋誰只由 sibling 順序決定,而順序由
    /// <see cref="RoomBubbleDrawOrder.ApplyFarToNear"/> 每幀重排(RoomScreen.SortNamePlateLayers 呼叫)。
    /// 這裡釘住的是「重排真的落在 sibling index 上」——
    /// 名次算對但用 SetSiblingIndex(rank) 套的話結果會是另一個排列,而且錯得很安靜。
    /// </summary>
    public class RoomNamePlateSortOrderTests
    {
        private readonly List<GameObject> _spawned = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _spawned.Count; i++) if (_spawned[i] != null) Object.DestroyImmediate(_spawned[i]);
            _spawned.Clear();
        }

        private RectTransform Layer(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            _spawned.Add(go);
            return (RectTransform)go.transform;
        }

        /// <summary>把每一層重排後的 sibling index 依輸入順序列出來。</summary>
        private static int[] SiblingIndices(List<RectTransform> layers)
        {
            var idx = new int[layers.Count];
            for (int i = 0; i < layers.Count; i++) idx[i] = layers[i].GetSiblingIndex();
            return idx;
        }

        [Test]
        public void The_Nearer_Persons_Nameplate_Draws_Last()
        {
            var root = Layer(null, "NamePlateLayer");
            // 建立順序刻意是「本機先、遠端後」——這正是修之前的 bug：本機那面在 BuildUI 就建好,
            // 所以無論誰站前面,後建的遠端名字牌都畫在它上面。
            var local = Layer(root, "Owner0");        // 我(站前面,深度 100)
            var remote = Layer(root, "Owner1234");    // 遠端(站後面,深度 300)

            var layers = new List<RectTransform> { local, remote };
            RoomBubbleDrawOrder.ApplyFarToNear(layers, new List<float> { 100f, 300f }, new List<int>());

            var idx = SiblingIndices(layers);
            Assert.Greater(idx[0], idx[1], "站在前面的自己的名字牌沒有蓋住後面那個人的(排序方向反了)");
        }

        [Test]
        public void Walking_Behind_Someone_Flips_Which_Nameplate_Is_On_Top()
        {
            var root = Layer(null, "NamePlateLayer");
            var local = Layer(root, "Owner0");
            var remote = Layer(root, "Owner1234");
            var layers = new List<RectTransform> { local, remote };
            var scratch = new List<int>();

            RoomBubbleDrawOrder.ApplyFarToNear(layers, new List<float> { 100f, 300f }, scratch);
            Assert.Greater(SiblingIndices(layers)[0], SiblingIndices(layers)[1]);

            // 我往後走到他後面 → 換他的名字牌蓋住我的。(每幀重排 ⇒ 上一幀留下的 sibling 順序不能有殘留影響)
            RoomBubbleDrawOrder.ApplyFarToNear(layers, new List<float> { 400f, 300f }, scratch);
            var idx = SiblingIndices(layers);
            Assert.Greater(idx[1], idx[0], "走到別人後面之後,我的名字牌沒有沉下去");
        }

        [Test]
        public void Every_Layer_Gets_A_Distinct_Sibling_Index_Far_To_Near()
        {
            var root = Layer(null, "NamePlateLayer");
            var layers = new List<RectTransform>();
            for (int i = 0; i < 5; i++) layers.Add(Layer(root, "Owner" + i));
            var depths = new List<float> { 500f, 120f, 300f, 900f, 200f };

            RoomBubbleDrawOrder.ApplyFarToNear(layers, depths, new List<int>());

            var idx = SiblingIndices(layers);
            var seen = new HashSet<int>(idx);
            Assert.AreEqual(idx.Length, seen.Count, "有兩層拿到同一個 sibling index");
            // 深度愈小(愈近)= sibling index 愈大(愈晚畫)。
            for (int a = 0; a < depths.Count; a++)
                for (int b = 0; b < depths.Count; b++)
                    if (depths[a] < depths[b])
                        Assert.Greater(idx[a], idx[b], "近的(" + depths[a] + ")沒有畫在遠的(" + depths[b] + ")之後");
        }

        [Test]
        public void A_Nameplate_With_No_Known_Depth_Sits_At_The_Back()
        {
            // 這一幀量不到深度(人剛進來/走到鏡頭後面)→ RoomScreen 填 float.MaxValue,
            // 它該排最遠而不是冒出來蓋住所有人。
            var root = Layer(null, "NamePlateLayer");
            var layers = new List<RectTransform> { Layer(root, "A"), Layer(root, "B"), Layer(root, "C") };
            RoomBubbleDrawOrder.ApplyFarToNear(layers, new List<float> { 100f, float.MaxValue, 300f }, new List<int>());

            var idx = SiblingIndices(layers);
            Assert.AreEqual(0, idx[1], "深度不明的名字牌沒有排在最後面");
            Assert.AreEqual(2, idx[0], "最近的名字牌沒有畫在最上面");
        }

        [Test]
        public void Mismatched_Or_Trivial_Inputs_Do_Nothing()
        {
            var root = Layer(null, "NamePlateLayer");
            var a = Layer(root, "A");
            var b = Layer(root, "B");
            var layers = new List<RectTransform> { a, b };

            // 長度對不上 → 什麼都不做(寧可維持上一幀的順序,也不要照錯的深度亂排)。
            RoomBubbleDrawOrder.ApplyFarToNear(layers, new List<float> { 100f }, new List<int>());
            Assert.AreEqual(0, a.GetSiblingIndex());
            Assert.AreEqual(1, b.GetSiblingIndex());

            // null 進來不該炸(每幀跑一次)。
            Assert.DoesNotThrow(() => RoomBubbleDrawOrder.ApplyFarToNear(null, new List<float>(), new List<int>()));
            Assert.DoesNotThrow(() => RoomBubbleDrawOrder.ApplyFarToNear(layers, null, new List<int>()));
            Assert.DoesNotThrow(() => RoomBubbleDrawOrder.ApplyFarToNear(layers, new List<float> { 1f, 2f }, null));
        }

        [Test]
        public void A_Destroyed_Layer_Does_Not_Break_The_Rest()
        {
            // 人剛離開房間、層已被 Destroy,但這一幀的 list 還握著它(Unity 的 null 是「假 null」)。
            var root = Layer(null, "NamePlateLayer");
            var gone = Layer(root, "Gone");
            var alive = Layer(root, "Alive");
            var layers = new List<RectTransform> { gone, alive };
            Object.DestroyImmediate(gone.gameObject);

            Assert.DoesNotThrow(() => RoomBubbleDrawOrder.ApplyFarToNear(layers, new List<float> { 100f, 300f }, new List<int>()));
            Assert.AreEqual(0, alive.GetSiblingIndex());
        }
    }
}
