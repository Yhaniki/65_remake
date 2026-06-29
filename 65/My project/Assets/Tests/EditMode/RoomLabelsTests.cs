using NUnit.Framework;
using Sdo.Localization;
using Sdo.UI.Services;

namespace Sdo.Tests
{
    /// <summary>Localized room-header texts (server / channel / room name), incl. the custom-vs-default branch.</summary>
    public class RoomLabelsTests
    {
        private static StringTable Tw() => StringTable.Parse(
            "{\"language\":\"zh-TW\",\"name\":\"繁中\",\"culture\":\"zh-TW\",\"entries\":[" +
            "{\"k\":\"room.server_name\",\"v\":\"自由練習場{0}\"}," +
            "{\"k\":\"room.channel\",\"v\":\"頻道{0}\"}," +
            "{\"k\":\"room.default_name\",\"v\":\"{0}的舞蹈室\"}]}");

        private static StringTable En() => StringTable.Parse(
            "{\"language\":\"en\",\"name\":\"English\",\"culture\":\"en-US\",\"entries\":[" +
            "{\"k\":\"room.server_name\",\"v\":\"Free Practice {0}\"}," +
            "{\"k\":\"room.channel\",\"v\":\"Channel {0}\"}," +
            "{\"k\":\"room.default_name\",\"v\":\"{0}'s Dance Room\"}]}");

        [SetUp]
        public void Reset() => LocalizationManager.LoadFromTables(Language.TraditionalChinese, Tw(), En());

        [Test]
        public void ServerName_And_Channel_Interpolate_Number()
        {
            Assert.AreEqual("自由練習場1", RoomLabels.ServerName(1));
            Assert.AreEqual("頻道1", RoomLabels.Channel(1));
        }

        [Test]
        public void DisplayName_Uses_Default_When_No_Custom_Name()
        {
            Assert.AreEqual("玩家001的舞蹈室", RoomLabels.DisplayName(null, "玩家001"));
            Assert.AreEqual("玩家001的舞蹈室", RoomLabels.DisplayName("", "玩家001"));
            Assert.AreEqual("玩家001的舞蹈室", RoomLabels.DisplayName("   ", "玩家001"));
        }

        [Test]
        public void DisplayName_Uses_Custom_Name_When_Set()
        {
            Assert.AreEqual("★熱舞坊匠★的舞蹈室", RoomLabels.DisplayName("★熱舞坊匠★的舞蹈室", "玩家001"));
        }

        [Test]
        public void Texts_Follow_The_Active_Language()
        {
            LocalizationManager.LoadFromTables(Language.English, En(), En());
            Assert.AreEqual("Free Practice 1", RoomLabels.ServerName(1));
            Assert.AreEqual("Channel 1", RoomLabels.Channel(1));
            Assert.AreEqual("Neo's Dance Room", RoomLabels.DisplayName(null, "Neo"));
        }
    }
}
