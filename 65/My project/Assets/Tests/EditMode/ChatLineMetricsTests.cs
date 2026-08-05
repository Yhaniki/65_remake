using NUnit.Framework;
using Sdo.UI.Util;

namespace Sdo.Tests
{
    /// <summary>
    /// 訊息欄一則訊息的排版高度(<see cref="ChatLineMetrics.BlockHeight"/>)。
    /// 症狀:訊息長到折第二行時,排版仍然只留一行 → 第二行壓在下一則訊息上、捲到底也只捲得到第一行。
    /// </summary>
    public class ChatLineMetricsTests
    {
        // 房間:行距 16、13px 字實測單行字身高約 15.2。
        private const float RoomRow = 16f, RoomOne = 15.2f;
        // 大廳:行距 12.5、12.5px 字的單行字身高比行距還大一點(字身高 > 行距,靠行距壓密)。
        private const float LobbyRow = 12.5f, LobbyOne = 12.9f;

        [Test]
        public void Single_Row_Message_Keeps_The_Designed_Row_Height_Exactly()
        {
            // 不折行時量到的總高 == 單行高 → 高度必須**一格不差**還是原本的行距,
            // 否則大廳「視窗 110 剛好裝 8 行」那條算式會失效(見 LobbyScreen.ChatLineH 的註解)。
            Assert.AreEqual(RoomRow, ChatLineMetrics.BlockHeight(RoomOne, RoomOne, RoomRow), 1e-4f);
            Assert.AreEqual(LobbyRow, ChatLineMetrics.BlockHeight(LobbyOne, LobbyOne, LobbyRow), 1e-4f);
        }

        [Test]
        public void Wrapped_Message_Grows_By_The_Extra_Rows_It_Actually_Needs()
        {
            // 折成兩行:TMP 量到的總高 = 單行高 + 一個行進(這裡取 15)。
            float twoRows = RoomOne + 15f;
            float h = ChatLineMetrics.BlockHeight(twoRows, RoomOne, RoomRow);
            Assert.Greater(h, RoomRow, "折行的訊息必須比一行高,不然第二行會壓到下一則");
            Assert.AreEqual(RoomRow + 15f, h, 1e-4f);

            // 三行就再長一個行進 —— 是連續量出來的,不是「行數 × 行高」的階梯。
            Assert.AreEqual(RoomRow + 30f, ChatLineMetrics.BlockHeight(RoomOne + 30f, RoomOne, RoomRow), 1e-4f);
        }

        [Test]
        public void Height_Is_Monotonic_In_The_Measured_Text_Height()
        {
            float prev = 0f;
            for (float wrapped = RoomOne; wrapped < RoomOne + 60f; wrapped += 3f)
            {
                float h = ChatLineMetrics.BlockHeight(wrapped, RoomOne, RoomRow);
                Assert.GreaterOrEqual(h, prev, "字越多,佔的位置只能越大");
                prev = h;
            }
        }

        [Test]
        public void Never_Shorter_Than_One_Row()
        {
            // 空字串 / 量不到(TMP 還沒生成字形)一律退回一行,別讓 VerticalLayoutGroup 收成 0 高。
            Assert.AreEqual(RoomRow, ChatLineMetrics.BlockHeight(0f, RoomOne, RoomRow), 1e-4f);
            Assert.AreEqual(RoomRow, ChatLineMetrics.BlockHeight(RoomOne, 0f, RoomRow), 1e-4f);
            // 單行字身高比行距大很多時(大廳),仍然不准縮到行距以下。
            Assert.AreEqual(LobbyRow, ChatLineMetrics.BlockHeight(LobbyOne - 2f, LobbyOne, LobbyRow), 1e-4f);
        }
    }
}
