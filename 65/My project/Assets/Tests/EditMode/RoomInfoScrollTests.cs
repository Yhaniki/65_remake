using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Sdo.UI.Screens;
using Sdo.UI.Services;
using Sdo.UI.Util;

namespace Sdo.Tests
{
    /// <summary>
    /// 「房間信息」名單的捲動 —— 那個框只有 <b>4 列</b>,房間卻坐得下 <b>6 個</b>。
    ///
    /// 🔴 以前那根握把是**畫上去的一張圖**(捲不動),而列是空的所以看不出來;名字補上去之後
    ///    滿房會有兩個人永遠看不到。這組測試釘住的就是「握把要真的能捲、而且捲的是整列」。
    ///
    /// 不依賴素材:缺圖時 <c>UIKit.AddSprite</c> / <c>FixedScrollbar</c> 都退成透明,版面照建。
    /// </summary>
    public class RoomInfoScrollTests
    {
        private GameObject _canvasGo;
        private RoomInfoModal _modal;
        private RectTransform _root;

        [SetUp]
        public void SetUp()
        {
            _canvasGo = new GameObject("RoomInfoTestCanvas", typeof(RectTransform), typeof(Canvas));
            var canvasRt = (RectTransform)_canvasGo.transform;
            canvasRt.sizeDelta = new Vector2(800f, 600f);

            _modal = new GameObject("RoomInfo").AddComponent<RoomInfoModal>();
            _modal.transform.SetParent(canvasRt, false);
            _modal.Build(canvasRt);
            _root = canvasRt;
        }

        [TearDown]
        public void TearDown()
        {
            if (_canvasGo != null) Object.DestroyImmediate(_canvasGo);
        }

        // ---- helpers ----

        /// <summary>坐滿 <paramref name="taken"/> 個人的房間(名字 A/B/C…、等級 11,12,13…)。</summary>
        private static RoomInfo Room(int taken, int capacity = 6)
        {
            var r = new RoomInfo { Id = 1, Capacity = capacity, HostName = "A" };
            for (int i = 0; i < capacity; i++)
                r.Seats.Add(new SeatInfo
                {
                    Player = i < taken
                        ? new PlayerProfile(i.ToString(), ((char)('A' + i)).ToString(), 11 + i)
                        : null,
                });
            return r;
        }

        private string Row(string suffix, int i)
        {
            foreach (var t in _root.GetComponentsInChildren<TextMeshProUGUI>(true))
                if (t.gameObject.name == "row" + i + "_" + suffix) return t.text;
            Assert.Fail("找不到 row" + i + "_" + suffix);
            return null;
        }

        private string Name(int i) => Row("name", i);
        private string Level(int i) => Row("level", i);

        /// <summary>轉一格滾輪。正值 = 往上(與 UGUI 的 scrollDelta.y 同向)。</summary>
        private void Wheel(float dy)
        {
            var wheel = _root.GetComponentInChildren<WheelScroll>(true);
            Assert.IsNotNull(wheel, "名單上要鋪一張收滾輪的板子");
            wheel.Scrolled(dy);
        }

        private Scrollbar Bar()
        {
            var bar = (Scrollbar)typeof(RoomInfoModal)
                .GetField("_bar", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(_modal);
            Assert.IsNotNull(bar, "那根握把要是真的 Scrollbar,不是一張畫上去的圖");
            return bar;
        }

        // ---- 捲動 ----

        [Test]
        public void Six_People_Start_At_The_Top_And_Show_The_First_Four()
        {
            _modal.Open(Room(6), null);
            Assert.AreEqual("A", Name(0));
            Assert.AreEqual("D", Name(3));
            Assert.AreEqual("11", Level(0), "左格是等級,不是座位編號");
            Assert.AreEqual(1f, Bar().value, 1e-4f, "BottomToTop:1 = 最上");
        }

        [Test]
        public void Wheeling_Down_Moves_The_List_One_Row_At_A_Time_And_Stops_At_The_End()
        {
            _modal.Open(Room(6), null);

            Wheel(-1f);
            Assert.AreEqual("B", Name(0));
            Assert.AreEqual("E", Name(3));

            Wheel(-1f);
            Assert.AreEqual("C", Name(0));
            Assert.AreEqual("F", Name(3), "最後一個人要看得到 —— 這就是加捲軸的理由");
            Assert.AreEqual(0f, Bar().value, 1e-4f, "捲到底 = 握把在最下面");

            // 到底了就停住,不能捲出一片空白。
            Wheel(-1f);
            Assert.AreEqual("C", Name(0));
            Assert.AreEqual("F", Name(3));
        }

        [Test]
        public void Wheeling_Back_Up_Returns_To_The_Top()
        {
            _modal.Open(Room(6), null);
            Wheel(-1f); Wheel(-1f);
            Wheel(1f); Wheel(1f); Wheel(1f);   // 多轉一格也不能捲過頭
            Assert.AreEqual("A", Name(0));
            Assert.AreEqual(1f, Bar().value, 1e-4f);
        }

        [Test]
        public void Dragging_The_Handle_Scrolls_The_List()
        {
            // 拖握把 == 改 Scrollbar.value(這是 UGUI 內建的部分,我們接的是 onValueChanged)。
            _modal.Open(Room(6), null);
            Bar().value = 0f;                  // 拖到底
            Assert.AreEqual("C", Name(0));
            Assert.AreEqual("F", Name(3));
        }

        // ---- 人數塞得下的時候 ----

        [Test]
        public void Three_People_Fill_Three_Rows_And_Leave_The_Fourth_Blank()
        {
            _modal.Open(Room(3), null);
            Assert.AreEqual("C", Name(2));
            Assert.AreEqual("", Name(3));
            Assert.AreEqual("", Level(3), "沒人的列連等級都不能有字");
        }

        [Test]
        public void Nothing_To_Scroll_Means_The_Handle_Snaps_Back_To_The_Top()
        {
            // 🔴 沒得捲卻被拖動時要彈回去:放著不管的話握把會停在半路,看起來像「捲了但列表沒跟上」。
            _modal.Open(Room(3), null);
            Bar().value = 0f;
            Assert.AreEqual("A", Name(0), "沒得捲,列表不能動");
            Assert.AreEqual(1f, Bar().value, 1e-4f, "握把要彈回最上面");
        }

        [Test]
        public void Reopening_On_Another_Room_Starts_From_The_Top_Again()
        {
            // 上一間房捲到哪與這間無關 —— 不歸零的話開一間 2 個人的房會看到一片空白。
            _modal.Open(Room(6), null);
            Wheel(-1f); Wheel(-1f);
            _modal.Open(Room(2), null);
            Assert.AreEqual("A", Name(0));
            Assert.AreEqual("B", Name(1));
            Assert.AreEqual("", Name(2));
        }

        // ---- 空位不佔列 ----

        [Test]
        public void An_Empty_Seat_In_The_Middle_Does_Not_Leave_A_Gap()
        {
            // 這是一份「房裡有誰」的名單,不是座位圖:座位 1 空著,坐在座位 2 的人要遞補到第二列。
            var r = Room(3);
            r.Seats[1].Player = null;
            _modal.Open(r, null);
            Assert.AreEqual("A", Name(0));
            Assert.AreEqual("C", Name(1), "空位不佔列");
            Assert.AreEqual("", Name(2));
        }

        // ---- 等級 0 = 不知道 ----

        [Test]
        public void Unknown_Level_Is_Blank_Not_Zero()
        {
            // 舊版 server 的 roomList 不送 members → 等級 0。寫「0」會讓人以為那是真的等級。
            var r = Room(1);
            r.Seats[0].Player = new PlayerProfile("0", "飄漂o", 0);
            _modal.Open(r, null);
            Assert.AreEqual("飄漂o", Name(0));
            Assert.AreEqual("", Level(0));
        }
    }
}
