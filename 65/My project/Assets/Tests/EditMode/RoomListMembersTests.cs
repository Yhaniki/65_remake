using NUnit.Framework;
using Sdo.Net;
using Sdo.UI.Services;

namespace Sdo.Tests
{
    /// <summary>
    /// 房間列表帶回來的「房裡有誰」——「房間信息」對話框(大廳右鍵房卡)那 4 列的資料來源。
    ///
    /// 🔴 為什麼名字要跟著**房間列表**走:那個框是在大廳開的,人還沒進房 → 收不到 roomSnapshot。
    ///    以前列表只送 count,mapping 就補一堆空名字的佔位座位 → 那 4 列永遠空白
    ///    (而空白看起來像排版壞掉,完全指不到「封包沒帶」)。
    /// </summary>
    public class RoomListMembersTests
    {
        /// <summary>把 JSON 字串解析成 <c>Decode</c> 吃的節點 —— 走的是實機同一條路(MiniJson)。</summary>
        private static NetRoomListEntry Decode(string json)
        {
            object node;
            Assert.IsTrue(NetJson.TryParse(json, out node), "測試資料本身要是合法 JSON");
            return NetRoomListEntry.Decode(node);
        }

        private const string TwoMembers =
            "{\"code\":47884,\"seq\":3,\"name\":\"須彌芥子的舞蹈室\",\"hostName\":\"須彌芥子\"," +
            "\"status\":\"open\",\"count\":2,\"capacity\":6,\"mode\":0," +
            "\"genders\":[0,1]," +
            "\"members\":[{\"name\":\"須彌芥子\",\"level\":11,\"gender\":0}," +
            "{\"name\":\"飄漂o\",\"level\":7,\"gender\":1}]}";

        [Test]
        public void Members_Reach_The_Seats_With_Their_Names_And_Levels()
        {
            var room = NetRoomMapping.ToRoomInfo(Decode(TwoMembers));

            Assert.AreEqual(6, room.Seats.Count, "座位數是房間容量,不是人數");
            Assert.AreEqual(2, room.Count);

            Assert.AreEqual("須彌芥子", room.Seats[0].Player.DisplayName);
            Assert.AreEqual(11, room.Seats[0].Player.Level, "🔴 使用者回報的就是這個:11 等被寫成 1");
            Assert.AreEqual("飄漂o", room.Seats[1].Player.DisplayName);
            Assert.AreEqual(7, room.Seats[1].Player.Level);

            Assert.IsNull(room.Seats[2].Player, "空位還是空位");
        }

        [Test]
        public void Old_Server_Without_Members_Leaves_The_Names_Blank_Instead_Of_Faking_Them()
        {
            // 舊版 server 只送 count —— 人數要正確(房卡的 2/6 靠它),但名字/等級留白:
            // 湊一個假等級比留白更難發現是錯的。
            var room = NetRoomMapping.ToRoomInfo(Decode(
                "{\"code\":1,\"count\":2,\"capacity\":6,\"status\":\"open\"}"));

            Assert.AreEqual(2, room.Count);
            Assert.AreEqual("", room.Seats[0].Player.DisplayName);
            Assert.AreEqual(0, room.Seats[0].Player.Level, "0 = 不知道,顯示端要留白");
        }

        [Test]
        public void Genders_Still_Come_Through_For_The_Lobby_Hearts()
        {
            // 房卡那排愛心讀的是 SeatGenders —— 加了 members 之後那條路不能斷。
            var room = NetRoomMapping.ToRoomInfo(Decode(TwoMembers));
            Assert.IsNotNull(room.SeatGenders);
            Assert.AreEqual(2, room.SeatGenders.Length);
            Assert.AreEqual(0, room.SeatGenders[0]);
            Assert.AreEqual(1, room.SeatGenders[1]);
        }

        [Test]
        public void Genders_Are_Derived_From_Members_When_The_Old_Field_Is_Missing()
        {
            // server 之後若不再送那個舊欄位,愛心也不能整排退回粉紅。
            var e = Decode("{\"code\":1,\"count\":2,\"capacity\":6,\"status\":\"open\"," +
                           "\"members\":[{\"name\":\"A\",\"level\":1,\"gender\":1}," +
                           "{\"name\":\"B\",\"level\":2,\"gender\":0}]}");
            Assert.IsNotNull(e.Genders);
            Assert.AreEqual(1, e.Genders[0]);
            Assert.AreEqual(0, e.Genders[1]);
        }
    }
}
