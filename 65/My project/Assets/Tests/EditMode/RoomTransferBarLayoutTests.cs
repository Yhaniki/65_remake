using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// 頭貼下緣那條上傳/下載跑條的橫向版位。
    ///
    /// 為什麼值得一條測試:名牌(AvatarName)跟頭貼格(AvatarView)在官方版面裡**不同心** ——
    /// 名牌 108 寬、左緣比頭貼格再往左 11px,中心差 5px。跑條是「跟名牌一起看」的元件,
    /// 之前拿 HeadSlotX/HeadSlotW 去擺,整條就往右偏 5px,看起來歪一邊沒置中。
    /// 這條測試把「跑條與名牌同心」釘住,免得日後又順手改回頭貼格的座標。
    /// </summary>
    public class RoomTransferBarLayoutTests
    {
        private static float PlateCenter(int slot)
            => RoomLayout.NamePlateX[slot] + RoomLayout.NamePlateW * 0.5f;

        [Test]
        public void Bar_Is_Centred_On_The_Name_Plate_In_Every_Seat()
        {
            for (int i = 0; i < RoomLayout.SeatCount; i++)
            {
                float x = RoomLayout.TransferBarX(i, RoomLayout.NamePlateW);
                Assert.AreEqual(PlateCenter(i), x + RoomLayout.NamePlateW * 0.5f, 0.01f,
                                "slot " + i + " 的跑條中心要跟名牌中心一致");
            }
        }

        [Test]
        public void A_Narrower_Bar_Stays_Centred()
        {
            // 寬度改窄(例如日後想讓條比名牌短)也不能變成靠左/靠右對齊。
            const float narrow = 60f;
            for (int i = 0; i < RoomLayout.SeatCount; i++)
            {
                float x = RoomLayout.TransferBarX(i, narrow);
                Assert.AreEqual(PlateCenter(i), x + narrow * 0.5f, 0.01f);
            }
        }

        [Test]
        public void Full_Width_Bar_Sits_Exactly_On_The_Plate()
        {
            for (int i = 0; i < RoomLayout.SeatCount; i++)
                Assert.AreEqual(RoomLayout.NamePlateX[i],
                                RoomLayout.TransferBarX(i, RoomLayout.NamePlateW), 0.01f);
        }

        [Test]
        public void Head_Slot_And_Name_Plate_Really_Are_Off_Centre()
        {
            // 這是上面那條規則的前提:兩者若同心,整個 TransferBarX 就沒有存在必要。
            // 官方版面每格差 4~7px(座標是逐字抄 XML,本來就不是等距) —— 記錄下來,
            // 免得有人「順手把 NamePlateX 對齊 HeadSlotX」。
            for (int i = 0; i < RoomLayout.SeatCount; i++)
            {
                float headCenter = RoomLayout.HeadSlotX[i] + RoomLayout.HeadSlotW * 0.5f;
                float d = headCenter - PlateCenter(i);
                Assert.That(d, Is.InRange(3f, 8f), "slot " + i + ":頭貼格中心在名牌中心右邊數 px");
            }
        }
    }
}
