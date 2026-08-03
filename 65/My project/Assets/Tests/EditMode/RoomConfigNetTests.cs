using NUnit.Framework;
using Sdo.Settings;

namespace Sdo.Tests
{
    /// <summary>
    /// <c>config.ini</c> 的 <c>[Net]</c> 區 —— 多人連線設定。
    ///
    /// 最重要的一條是 <see cref="RoomConfig.OnlineEnabled"/>:它是**唯一**的離線/連線判斷點,
    /// 留空就完全不建連線層,單機體驗一字不動。這個欄位判斷錯的後果是「明明沒填伺服器
    /// 卻跑去連線然後卡在開機畫面」—— 對只想單機玩的人是災難。
    /// </summary>
    public class RoomConfigNetTests
    {
        // RoomConfig 是 static state,每個案例前重置成內建預設。
        [SetUp]
        public void Reset()
        {
            RoomConfig.serverAddress = "";
            RoomConfig.serverPort = 27015;
            RoomConfig.serverPassword = RoomConfig.DefaultServerPassword;
            RoomConfig.netAutoDownload = true;
            RoomConfig.netMaxDownloadMb = 200;
        }

        [TearDown]
        public void Cleanup() { Reset(); }

        // ---- ★ 總開關 ----

        [Test]
        public void OnlineEnabled_Is_False_By_Default()
        {
            // 預設就是單機 —— 加了連線功能不該改變任何現有玩家的體驗。
            Assert.IsFalse(RoomConfig.OnlineEnabled);
        }

        [Test]
        public void OnlineEnabled_Is_True_Once_An_Address_Is_Set()
        {
            RoomConfig.serverAddress = "192.168.1.10";
            Assert.IsTrue(RoomConfig.OnlineEnabled);
        }

        [Test]
        public void OnlineEnabled_Treats_Whitespace_As_Empty()
        {
            // 手改設定檔很容易留下空白行或空格。那些不該被當成「有填伺服器」。
            RoomConfig.serverAddress = "   ";
            Assert.IsFalse(RoomConfig.OnlineEnabled);

            RoomConfig.serverAddress = "\t";
            Assert.IsFalse(RoomConfig.OnlineEnabled);
        }

        // ---- 進站密碼 ----

        [Test]
        public void Default_Password_Comes_From_The_Shared_Constant()
        {
            // 「兩邊都不改就能連上」是設計意圖:client 與 server 的預設密碼指向**同一個**
            // 共用常數(Sdo.Net.NetLimits.DefaultServerPassword),所以漂移在結構上不可能發生。
            //
            // 這條測試釘住的是「有人把它改成硬編字串」那種退步 —— 那之後兩邊會各自漂移,
            // 而症狀(誰都連不進來)完全看不出根因。
            // (server 那一半在 dotnet test 驗:ServerOptions.DefaultPassword。)
            Assert.AreEqual(Sdo.Net.NetLimits.DefaultServerPassword, RoomConfig.DefaultServerPassword);
            Assert.IsNotEmpty(RoomConfig.DefaultServerPassword, "預設要有密碼,不是空密碼放行");
        }

        [Test]
        public void Fresh_Config_Ships_With_The_Default_Password()
        {
            // 新裝的玩家 config.ini 會寫出預設密碼 → 對上預設的 server 就能直接連。
            RoomConfig.serverPassword = RoomConfig.DefaultServerPassword;
            string ini = RoomConfig.Serialize();
            Assert.IsTrue(ini.Contains("serverPassword=" + RoomConfig.DefaultServerPassword),
                "Serialize 要寫出實際的密碼值,得到的是:" + ini);
        }

        [Test]
        public void Empty_Password_Is_Preserved_Not_Replaced_By_The_Default()
        {
            // 玩家刻意留空(要連沒設密碼的 server)時不能被「補回預設值」——
            // 那會讓「我就是要空密碼」變成做不到。
            RoomConfig.serverPassword = "";
            RoomConfig.Sanitize();
            Assert.AreEqual("", RoomConfig.serverPassword);

            RoomConfig.ParseInto("serverPassword=\n");
            RoomConfig.Sanitize();
            Assert.AreEqual("", RoomConfig.serverPassword, "檔案裡留空就是留空");
        }

        // ---- 解析 ----

        [Test]
        public void ParseInto_Reads_All_Net_Keys()
        {
            RoomConfig.ParseInto(
                "[Net]\n" +
                "serverAddress=dance.example.com\n" +
                "serverPort=30000\n" +
                "serverPassword=hunter2\n" +
                "netAutoDownload=0\n" +
                "netMaxDownloadMb=50\n");

            Assert.AreEqual("dance.example.com", RoomConfig.serverAddress);
            Assert.AreEqual(30000, RoomConfig.serverPort);
            Assert.AreEqual("hunter2", RoomConfig.serverPassword);
            Assert.IsFalse(RoomConfig.netAutoDownload);
            Assert.AreEqual(50, RoomConfig.netMaxDownloadMb);
        }

        [Test]
        public void ParseInto_Accepts_The_Usual_Boolean_Spellings()
        {
            // ParseBool 認 1/true/yes/on 與 0/false/no/off —— 手改設定檔的人會用各種寫法。
            RoomConfig.ParseInto("netAutoDownload=false\n");
            Assert.IsFalse(RoomConfig.netAutoDownload);

            RoomConfig.ParseInto("netAutoDownload=yes\n");
            Assert.IsTrue(RoomConfig.netAutoDownload);

            RoomConfig.ParseInto("netAutoDownload=off\n");
            Assert.IsFalse(RoomConfig.netAutoDownload);
        }

        [Test]
        public void ParseInto_Keeps_The_Current_Value_On_Garbage()
        {
            RoomConfig.serverPort = 12345;
            RoomConfig.ParseInto("serverPort=not a number\n");
            Assert.AreEqual(12345, RoomConfig.serverPort, "解不出來要保留原值,不要變 0");
        }

        [Test]
        public void ParseInto_Ignores_The_Section_Header()
        {
            // ParseInto 完全忽略 [Section] 行 —— 區段標頭只是給人看的分組。
            // 這也表示 key 名必須全域唯一(不能有兩個區段用同一個 key 名)。
            RoomConfig.ParseInto("[Net]\n[Room]\n[Option]\nserverAddress=1.2.3.4\n");
            Assert.AreEqual("1.2.3.4", RoomConfig.serverAddress);
        }

        [Test]
        public void Net_Keys_Are_Case_Sensitive()
        {
            // ParseInto 的 switch 是大小寫敏感的。這條測試是刻意釘住現況:
            // 打錯大小寫會被靜默忽略(而不是報錯),所以 Serialize 寫出的拼法就是唯一正解。
            RoomConfig.ParseInto("ServerAddress=1.2.3.4\n");
            Assert.AreEqual("", RoomConfig.serverAddress, "大寫 S 不該被認得");
        }

        // ---- 夾值 ----

        [Test]
        public void Sanitize_Trims_The_Address()
        {
            // 🔴 尾端空白會讓 OnlineEnabled 誤判成「有填」,然後拿一個含空白的主機名去解析 ——
            // 錯誤訊息會很莫名(「找不到主機 '192.168.1.10 '」)。
            RoomConfig.serverAddress = "  192.168.1.10  ";
            RoomConfig.Sanitize();
            Assert.AreEqual("192.168.1.10", RoomConfig.serverAddress);
        }

        [Test]
        public void Sanitize_Trims_The_Password()
        {
            RoomConfig.serverPassword = "  secret  ";
            RoomConfig.Sanitize();
            Assert.AreEqual("secret", RoomConfig.serverPassword);
        }

        [Test]
        public void Sanitize_Clamps_The_Port_To_A_Valid_Range()
        {
            RoomConfig.serverPort = 0;
            RoomConfig.Sanitize();
            Assert.AreEqual(1, RoomConfig.serverPort);

            RoomConfig.serverPort = 99999;
            RoomConfig.Sanitize();
            Assert.AreEqual(65535, RoomConfig.serverPort);

            RoomConfig.serverPort = -5;
            RoomConfig.Sanitize();
            Assert.AreEqual(1, RoomConfig.serverPort);
        }

        [Test]
        public void Sanitize_Clamps_The_Download_Cap()
        {
            RoomConfig.netMaxDownloadMb = 0;
            RoomConfig.Sanitize();
            Assert.AreEqual(1, RoomConfig.netMaxDownloadMb);

            RoomConfig.netMaxDownloadMb = 999999;
            RoomConfig.Sanitize();
            Assert.AreEqual(2048, RoomConfig.netMaxDownloadMb);
        }

        [Test]
        public void Sanitize_Handles_Null_Strings()
        {
            // 反射/序列化路徑可能塞進 null。Sanitize 之後應該是空字串而不是 null,
            // 否則 OnlineEnabled 之外的地方(例如組連線字串)會 NRE。
            RoomConfig.serverAddress = null;
            RoomConfig.serverPassword = null;
            RoomConfig.Sanitize();
            Assert.AreEqual("", RoomConfig.serverAddress);
            Assert.AreEqual("", RoomConfig.serverPassword);
            Assert.IsFalse(RoomConfig.OnlineEnabled);
        }

        // ---- 序列化 round-trip ----

        [Test]
        public void Serialize_Then_ParseInto_Round_Trips()
        {
            RoomConfig.serverAddress = "10.0.0.5";
            RoomConfig.serverPort = 28000;
            RoomConfig.serverPassword = "pw";
            RoomConfig.netAutoDownload = false;
            RoomConfig.netMaxDownloadMb = 77;

            string ini = RoomConfig.Serialize();

            Reset();   // 打回預設
            RoomConfig.ParseInto(ini);

            Assert.AreEqual("10.0.0.5", RoomConfig.serverAddress);
            Assert.AreEqual(28000, RoomConfig.serverPort);
            Assert.AreEqual("pw", RoomConfig.serverPassword);
            Assert.IsFalse(RoomConfig.netAutoDownload);
            Assert.AreEqual(77, RoomConfig.netMaxDownloadMb);
        }

        [Test]
        public void Serialize_Writes_The_Net_Section_Header()
        {
            string ini = RoomConfig.Serialize();
            Assert.IsTrue(ini.Contains("[Net]"), "要有區段標頭讓人在設定檔裡找得到");
            Assert.IsTrue(ini.Contains("serverAddress="));
            Assert.IsTrue(ini.Contains("serverPort="));
            Assert.IsTrue(ini.Contains("serverPassword="));
            Assert.IsTrue(ini.Contains("netAutoDownload="));
            Assert.IsTrue(ini.Contains("netMaxDownloadMb="));
        }

        [Test]
        public void Serialize_Writes_An_Empty_Address_Without_Breaking_The_Format()
        {
            // 預設狀態(單機)寫出來的檔案要能被讀回來,而且讀回來還是單機。
            string ini = RoomConfig.Serialize();
            Assert.IsTrue(ini.Contains("serverAddress=\n"), "空位址要寫成空值而不是省略整行");

            RoomConfig.serverAddress = "somewhere";
            RoomConfig.ParseInto(ini);
            Assert.AreEqual("", RoomConfig.serverAddress);
            Assert.IsFalse(RoomConfig.OnlineEnabled);
        }

        // ---- 舊檔升級 ----

        [Test]
        public void Old_Config_Without_Net_Keys_Is_Detected_As_Missing()
        {
            // 沒有這條的話,舊玩家的 config.ini 永遠不會出現 [Net] 區,
            // 他們就沒有一個「可以手改的位置」去填伺服器位址。
            string old =
                "[Profile]\nactiveId=00000000\n" +
                "[Room]\ndefaultSpeed=2.5\n" +
                "[Option]\nopt_bgm=0.5\n";
            Assert.IsFalse(old.Contains("serverAddress"), "前提:這份舊內容確實沒有 [Net] 的 key");
            Assert.IsTrue(RoomConfig.IsMissingCurrentKey(old), "Load 應該判定要補寫");
        }

        [Test]
        public void Freshly_Serialized_Config_Is_Not_Missing_Anything()
        {
            Assert.IsFalse(RoomConfig.IsMissingCurrentKey(RoomConfig.Serialize()));
        }
    }
}
