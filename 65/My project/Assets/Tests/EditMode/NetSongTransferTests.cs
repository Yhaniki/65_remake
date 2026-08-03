using System.Reflection;
using NUnit.Framework;
using Sdo.Game.Net;
using Sdo.Net;
using Sdo.Osu;
using Sdo.UI.Core;
using Sdo.UI.Screens;

namespace Sdo.Tests
{
    /// <summary>
    /// 外部歌上線時,譜面路徑要從「本機絕對路徑」翻成「相對歌曲資料夾」。
    ///
    /// 🔴 這是實機驗證抓到的真 bug:<c>GameSession.ExternalChartPath</c> 是絕對路徑,
    /// 原本直接塞進 <c>NetSongRef.ChartRelPath</c> → server 的 <c>SafeRelPath.IsSafe</c> 擋掉
    /// (它不收磁碟機代號)→ 整個 setSong 回 badState,而畫面上只是「選了歌但房間沒歌」。
    /// 當時所有單元測試都是綠的 —— 沒有一條測到「絕對路徑進 wire」。這幾條就是補那個洞。
    /// </summary>
    public class NetSongRefChartPathTests
    {
        [Test]
        public void An_Absolute_Chart_Path_Becomes_Relative_To_The_Song_Folder()
        {
            Assert.AreEqual("song.osu",
                NetSongPublisher.ToChartRelPath(@"H:\Songs\My Pack", @"H:\Songs\My Pack\song.osu"));
        }

        [Test]
        public void A_Chart_In_A_Subfolder_Keeps_The_Subfolder()
        {
            Assert.AreEqual("sub/song.osu",
                NetSongPublisher.ToChartRelPath(@"H:\Songs\My Pack", @"H:\Songs\My Pack\sub\song.osu"));
        }

        [Test]
        public void The_Result_Passes_The_Servers_Safety_Check()
        {
            // 這才是重點:server 會用這個函式擋。翻出來的東西一定要過得了。
            var rel = NetSongPublisher.ToChartRelPath(@"H:\Songs\My Pack", @"H:\Songs\My Pack\song.osu");
            Assert.IsTrue(SafeRelPath.IsSafe(rel), rel + " 過不了 SafeRelPath.IsSafe → server 會拒絕整個 setSong");

            // 反例:絕對路徑本來就過不了 —— 這正是那個 bug 的形狀。
            Assert.IsFalse(SafeRelPath.IsSafe(@"H:\Songs\My Pack\song.osu"), "絕對路徑不該被當成安全的相對路徑");
        }

        [Test]
        public void Forward_And_Back_Slashes_Both_Work()
        {
            Assert.AreEqual("song.osu",
                NetSongPublisher.ToChartRelPath("H:/Songs/My Pack", @"H:\Songs\My Pack\song.osu"));
            Assert.AreEqual("song.osu",
                NetSongPublisher.ToChartRelPath(@"H:\Songs\My Pack\", "H:/Songs/My Pack/song.osu"));
        }

        [Test]
        public void A_Chart_Outside_The_Folder_Falls_Back_To_The_File_Name()
        {
            // 切不出相對路徑時退回檔名 —— 那仍是合法且能用的相對路徑。
            // 回空字串會讓 server 拒絕整個 setSong(房間就沒歌了),那比退回檔名糟得多。
            var rel = NetSongPublisher.ToChartRelPath(@"H:\Other", @"H:\Songs\My Pack\song.osu");
            Assert.AreEqual("song.osu", rel);
            Assert.IsTrue(SafeRelPath.IsSafe(rel));
        }

        [Test]
        public void No_Chart_Path_Stays_Empty()
        {
            Assert.AreEqual("", NetSongPublisher.ToChartRelPath(@"H:\Songs\My Pack", null));
            Assert.AreEqual("", NetSongPublisher.ToChartRelPath(null, ""));
        }
    }

    /// <summary>
    /// 下載來的歌要放在哪個資料夾 —— 唯一的純函式,所以唯一能單元測試的部分。
    /// (真正的傳輸有 server 那邊的端到端測試守著:BlobTransferTests。)
    ///
    /// 為什麼這條值得測:這個字串會**直接變成檔案系統上的資料夾名**。裡面有非法字元的話,
    /// 症狀是「下載完成但歌沒出現」——因為 CreateDirectory 丟了例外、而那被當成一般的傳輸失敗。
    /// 歌名裡有 <c>:</c> 或 <c>?</c> 的 osu 圖非常常見。
    /// </summary>
    public class NetSongTransferTests
    {
        private const string Pack = "sha256:0123456789abcdef0123456789abcdef";
        private const string PackB = "sha256:ffffffffffffffffffffffffffffffff";

        [SetUp]
        public void SetUp()
        {
            NetSongTransfer.Reset();
            SetStatic("_wired", null);
        }

        [TearDown]
        public void TearDown()
        {
            NetSongTransfer.Reset();
            SetStatic("_wired", null);
        }

        [Test]
        public void The_Folder_Name_Is_Title_Artist_And_A_Pack_Tag()
        {
            Assert.AreEqual("夜に駆ける - YOASOBI [01234567]",
                NetSongFetcher.ConnectFolderName("夜に駆ける", "YOASOBI", Pack));
        }

        [Test]
        public void Characters_Windows_Forbids_Become_Underscores()
        {
            // \ / : * ? " < > | —— 這幾個在 Windows 上是非法的,而歌名裡出現冒號與問號很常見。
            var name = NetSongFetcher.ConnectFolderName("A:B/C*D?E\"F<G>H|I\\J", "art", Pack);
            foreach (var c in "\\/:*?\"<>|")
                Assert.IsFalse(name.IndexOf(c) >= 0, "資料夾名還留著非法字元 " + c + ":" + name);
            StringAssert.StartsWith("A_B_C_D_E_F_G_H_I_J", name);
        }

        [Test]
        public void A_Trailing_Space_Or_Dot_Is_Trimmed()
        {
            // 🔴 Windows 會**靜默**去掉結尾的空白與句點:建出來的資料夾名與你要求的不一樣,
            //    之後拿原字串去比對就永遠對不上。
            var name = NetSongFetcher.ConnectFolderName("結尾有點...", "", Pack);
            StringAssert.StartsWith("結尾有點 [", name);

            var name2 = NetSongFetcher.ConnectFolderName("結尾有空白   ", "", Pack);
            StringAssert.StartsWith("結尾有空白 [", name2);
        }

        [Test]
        public void The_Pack_Tag_Makes_Two_Same_Named_Songs_Land_In_Different_Folders()
        {
            // 一律加上 packId 前 8 碼 → 撞名問題直接消失(同名但內容不同的歌是不同的資料夾),
            // 而且看資料夾就知道它來自哪一份包。
            var a = NetSongFetcher.ConnectFolderName("同名歌", "同一個人", Pack);
            var b = NetSongFetcher.ConnectFolderName("同名歌", "同一個人", SongPackId.Prefix + "ffffffffffffffffffffffffffffffff");
            Assert.AreNotEqual(a, b);
        }

        [Test]
        public void An_Empty_Title_Still_Produces_A_Usable_Name()
        {
            var name = NetSongFetcher.ConnectFolderName("", "", Pack);
            Assert.AreEqual("song [01234567]", name);
        }

        [Test]
        public void A_Missing_Pack_Id_Does_Not_Produce_A_Broken_Name()
        {
            Assert.AreEqual("歌 [unknown]", NetSongFetcher.ConnectFolderName("歌", "", null));
            Assert.AreEqual("歌 [unknown]", NetSongFetcher.ConnectFolderName("歌", "", "短"));
        }

        [Test]
        public void A_Very_Long_Title_Is_Truncated()
        {
            // Windows 的路徑長度上限是真的會踩到的(ADDON/SONG/connect/<這個名字>/<檔名>)。
            var name = NetSongFetcher.ConnectFolderName(new string('長', 300), new string('人', 300), Pack);
            Assert.LessOrEqual(name.Length, 60 + 11, "名字要截短,不然整條路徑會超過 Windows 的上限");
            StringAssert.EndsWith("[01234567]", name);
        }

        [Test]
        public void RoomSongChangeInvalidatesOldTransferBeforeReportingNewAvailability()
        {
            var method = typeof(RoomScreen).GetMethod(
                "RunSongAvailabilitySync", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(method);

            string order = "";
            string seenPack = null;
            System.Action<string> onRoomSong = pack =>
            {
                order += "song>";
                seenPack = pack;
            };
            System.Action reportAvailability = () => order += "availability";

            method.Invoke(null, new object[] { PackB, onRoomSong, reportAvailability });

            Assert.AreEqual(PackB, seenPack);
            Assert.AreEqual("song>availability", order);
        }

        [Test]
        public void ChangingRoomPackDisposesTheOldFetcherAndInvalidatesItsImportGeneration()
        {
            NetSongTransfer.OnRoomSong(Pack);
            var fx = new NetSongFetcher();
            SetFetcher(fx, "_packId", Pack);
            SetFetcher(fx, "<State>k__BackingField", NetTransferState.Downloading);
            SetFetcher(fx, "_link", new NetConnection());
            Invoke("ActivateTransfer", fx, "song-a");
            int generation = Static<int>("_transferGeneration");

            Assert.IsTrue(fx.IsBusy);
            Assert.AreEqual("song-a", Static<string>("_transferSongKey"));
            Assert.IsTrue((bool)Invoke("IsCurrentTransfer", fx, generation, Pack));

            NetSongTransfer.OnRoomSong(PackB);

            Assert.IsNull(Static<NetSongFetcher>("_fx"));
            Assert.IsNull(Fetcher<object>(fx, "_link"), "OnRoomSong must Dispose the old fetcher's connection");
            Assert.IsFalse(Static<bool>("_importing"));
            Assert.IsNull(Static<string>("_transferSongKey"));
            Assert.AreNotEqual(generation, Static<int>("_transferGeneration"));
            Assert.IsFalse((bool)Invoke("IsCurrentTransfer", fx, generation, Pack),
                "stale A import completion must not be current after selecting B");
        }

        [Test]
        public void EachTransferCapturesItsOwnSongKey()
        {
            NetSongTransfer.OnRoomSong(Pack);
            var a = new NetSongFetcher();
            SetFetcher(a, "_packId", Pack);
            Invoke("ActivateTransfer", a, "key-a");
            int aGeneration = Static<int>("_transferGeneration");

            NetSongTransfer.OnRoomSong(PackB);
            var b = new NetSongFetcher();
            SetFetcher(b, "_packId", PackB);
            Invoke("ActivateTransfer", b, "key-b");
            int bGeneration = Static<int>("_transferGeneration");

            Assert.AreEqual("key-b", Static<string>("_transferSongKey"));
            Assert.IsFalse((bool)Invoke("IsCurrentTransfer", a, aGeneration, Pack));
            Assert.IsTrue((bool)Invoke("IsCurrentTransfer", b, bGeneration, PackB));
        }

        [Test]
        public void BlobInfoOnlyCompletesTheMatchingCurrentPackQuery()
        {
            NetSongTransfer.OnRoomSong(PackB);
            SetStatic("_queryPending", true);
            SetStatic("_queriedPack", PackB);
            SetStatic("_serverHasPack", false);

            Invoke("OnBlobInfo", Pack, true);
            Assert.IsTrue(Static<bool>("_queryPending"));
            Assert.IsFalse(Static<bool>("_serverHasPack"));

            Invoke("OnBlobInfo", PackB, true);
            Assert.IsFalse(Static<bool>("_queryPending"));
            Assert.IsNull(Static<string>("_queriedPack"));
            Assert.IsTrue(Static<bool>("_serverHasPack"));
        }

        [Test]
        public void BlobAvailableForOldPackCannotUnlockTheNewPack()
        {
            NetSongTransfer.OnRoomSong(PackB);
            SetStatic("_handledPack", PackB);
            SetStatic("_queryPending", true);
            SetStatic("_queriedPack", PackB);
            SetStatic("_serverHasPack", false);

            Invoke("OnBlobAvailable", Pack);
            Assert.IsFalse(Static<bool>("_serverHasPack"));
            Assert.IsTrue(Static<bool>("_queryPending"));
            Assert.AreEqual(PackB, Static<string>("_handledPack"));

            Invoke("OnBlobAvailable", PackB);
            Assert.IsTrue(Static<bool>("_serverHasPack"));
            Assert.IsFalse(Static<bool>("_queryPending"));
            Assert.IsNull(Static<string>("_queriedPack"));
            Assert.IsNull(Static<string>("_handledPack"));
        }

        /// <summary>
        /// 🔴 回歸(實機:房裡兩個人缺歌,只有一個下載到,另一個從頭到尾掛著 NO MAP)。
        ///
        /// 「等 blobQuery 的回覆」以前是一個**無條件**的鎖:回覆只要沒被收下(訊息掉了,或被
        /// <c>OnBlobInfo</c> 的 _roomPack 守衛丟掉),那台就永久停在缺歌 —— 不再重問,而房主上傳完的
        /// blobAvailable 廣播也叫不醒它(同一道守衛)。按不了準備、房主也開不了場,log 上一行都沒有。
        /// </summary>
        [Test]
        public void AQueryWhoseReplyNeverArrivesIsRetriedInsteadOfLockingForever()
        {
            SetStatic("_queryPending", true);
            SetStatic("_lastQueryAt", 100f);

            Assert.IsTrue(QueryStillPending(101f), "才過 1 秒 → 回覆還可能在路上,不該重問(會撞 server 的限流)");
            Assert.IsTrue(QueryStillPending(107f));
            Assert.IsFalse(QueryStillPending(108f), "等超過上限 → 這一問當成掉了,必須能重問");
            Assert.IsFalse(QueryStillPending(9999f));

            // 沒有在等回覆的時候永遠是 false —— 不然 MaybeStart 會被一個不存在的查詢擋住。
            SetStatic("_queryPending", false);
            Assert.IsFalse(QueryStillPending(100.5f));
        }

        /// <summary>
        /// 🔴 回歸(同一次實機事故的另一半):<c>_roomPack</c> 這個 latch 有兩個呼叫端 ——
        /// <c>RoomScreen.SyncNetSongAvailability</c>(只在房間畫面收到快照時)與 <c>NetSongTransfer.Tick</c>
        /// (每幀無條件)。兩邊算出不同的字串就會每幀互相覆蓋 → 每幀都被當成換歌 → 進度條一直閃掉重來。
        /// 所以 key 只能有一份算法。
        /// </summary>
        [Test]
        public void TheRoomPackLatchKeyHasOneDefinitionForBothCallers()
        {
            Assert.IsNull(NetSongTransfer.RoomPackKeyOf(null), "房間沒歌 → null");

            // 官方歌沒有 packId(大家的 DATA/MUSIC 是同一份,不走傳檔)。重點是**兩個呼叫端拿到同一個值**,
            // 不論那個值是 "" 還是 null。
            var official = new NetSongRef { Official = true, Gn = "M0001", PackId = "" };
            Assert.AreEqual("", NetSongTransfer.RoomPackKeyOf(official));

            var external = new NetSongRef { Official = false, PackId = Pack };
            Assert.AreEqual(Pack, NetSongTransfer.RoomPackKeyOf(external));
        }

        private static bool QueryStillPending(float now)
            => (bool)Invoke("QueryReplyStillPending", now);

        /// <summary>
        /// 🔴 回歸:房主以前是「一選外部歌就無條件上傳」,一個人在房裡試歌也會把好幾 MB 推上去
        /// (實機 log:「開始收上傳:10/10 個檔、2104 KB」,房裡只有房主)。沒有人要的東西
        /// server 還得存、續命、跑 janitor —— 純粹是白花的流量與磁碟。
        /// </summary>
        [Test]
        public void UploadOnlyStartsWhenSomeoneElseActuallyMissesTheSong()
        {
            Assert.IsFalse(AnyoneMissing(RoomOf(1), 1), "一個人在房裡試歌不該觸發上傳");

            // 換歌會把全房的 avail 打回 unknown(R9)。把 unknown 當成缺歌的話,
            // 等於每次換歌都無條件上傳 —— 這個判斷就白寫了。
            Assert.IsFalse(AnyoneMissing(RoomOf(1, Seat(2, Availability.Unknown)), 1), "還沒回報 → 等他算完");
            Assert.IsFalse(AnyoneMissing(RoomOf(1, Seat(2, Availability.Have)), 1));

            // 已經在拿了 = server 手上一定有這個包,再傳一次沒有意義。
            Assert.IsFalse(AnyoneMissing(RoomOf(1, Seat(2, Availability.Downloading)), 1));
            Assert.IsFalse(AnyoneMissing(RoomOf(1, Seat(2, Availability.Importing)), 1));

            Assert.IsTrue(AnyoneMissing(RoomOf(1, Seat(2, Availability.Missing)), 1), "有人真的缺 → 這時才傳");
            Assert.IsTrue(AnyoneMissing(RoomOf(1, Seat(2, Availability.Have), Seat(3, Availability.Missing)), 1),
                          "有人有、有人缺 → 還是要傳");
        }

        [Test]
        public void MyOwnSeatNeverCountsAsSomeoneMissingTheSong()
        {
            // 房主自己不算 —— 不然「自己缺歌」會變成「傳給自己」,而房主的歌本來就在本機。
            var room = RoomOf(1);
            room.Seats[0] = Seat(1, Availability.Missing);
            Assert.IsFalse(AnyoneMissing(room, 1));
        }

        [Test]
        public void SpectatorsDoNotTriggerAnUpload()
        {
            // 旁觀者不自動下載(需求 10)→ 為他們上傳沒有意義。他們不在 Seats 裡,所以天然不算,
            // 但這條測試要釘住「將來有人把旁觀者也掃進去」這件事。
            var room = RoomOf(1);
            room.Spectators = new[] { new NetSpectator { UserId = 9, Name = "看戲的" } };
            Assert.IsFalse(AnyoneMissing(room, 1));
        }

        private static bool AnyoneMissing(NetRoomSnapshot snap, int meUserId)
            => (bool)Invoke("AnyoneMissingSong", snap, meUserId);

        /// <summary>房主坐 0 號位(當然有自己選的歌),其餘的人依序往後坐。</summary>
        private static NetRoomSnapshot RoomOf(int hostUserId, params NetSeat[] others)
        {
            var snap = new NetRoomSnapshot { HostUserId = hostUserId };
            snap.Seats[0] = Seat(hostUserId, Availability.Have);
            for (int i = 0; i < others.Length; i++) snap.Seats[i + 1] = others[i];
            return snap;
        }

        private static NetSeat Seat(int userId, Availability avail)
            => new NetSeat { State = SeatState.Taken, UserId = userId, Avail = avail };

        private static object Invoke(string name, params object[] args)
        {
            var method = typeof(NetSongTransfer).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(method, name);
            return method.Invoke(null, args);
        }

        private static T Static<T>(string name)
        {
            var field = typeof(NetSongTransfer).GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(field, name);
            return (T)field.GetValue(null);
        }

        private static void SetStatic(string name, object value)
        {
            var field = typeof(NetSongTransfer).GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(field, name);
            field.SetValue(null, value);
        }

        private static T Fetcher<T>(NetSongFetcher fetcher, string name)
        {
            var field = typeof(NetSongFetcher).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, name);
            return (T)field.GetValue(fetcher);
        }

        private static void SetFetcher(NetSongFetcher fetcher, string name, object value)
        {
            var field = typeof(NetSongFetcher).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, name);
            field.SetValue(fetcher, value);
        }
    }
}
