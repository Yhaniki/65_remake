using NUnit.Framework;
using Sdo.Net;
using Sdo.Settings;
using Sdo.UI.Core;
using Sdo.UI.Services;

namespace Sdo.Tests
{
    /// <summary>
    /// ShowTime 模式的兩個回報:
    ///
    /// 1. **「ShowTime 的房間,把房主讓給別人之後變成自由模式」**
    ///    <c>NetRoomSettingsPublisher.SyncIfHost</c> 是拿**本機 session** 去跟 server 比對的,
    ///    而非房主的 session 從頭到尾都還是自己 config.ini 的預設值(通常 0=自由)——
    ///    房主一轉移,新房主下一份房間快照就把那份預設值推上去,整間房的模式/隊形/旁觀/場景全被蓋掉。
    ///    修法是**先收再推**:非房主每份快照都把房間設定收進自己的 session
    ///    (<c>AdoptIfNotHost</c> → <c>ApplyToSession</c>),升房主那一刻兩邊已經一致 → 不會送任何東西。
    ///
    /// 2. **「ShowTime 的房間,大廳看是普通模式」**
    ///    UI 的 <see cref="GameMode"/> 以前只有 Free/Normal,<c>NetRoomMapping</c> 把「非 0 一律當 Normal」——
    ///    房間裡讀的是 <c>Settings.GameMode</c> 所以顯示對,只有大廳房卡與「房間信息」框錯。
    ///
    /// 兩件事的共同前提:協定代號 0=自由 1=普通 2=ShowTime(見 <see cref="GameModeRules"/>)。
    /// </summary>
    public class ShowtimeRoomSettingsTests
    {
        private const int Free = GameModeRules.Free;
        private const int Normal = GameModeRules.Normal;
        private const int Showtime = GameModeRules.Showtime;

        private static NetRoomSettings ShowtimeRoom()
            => new NetRoomSettings { GameMode = Showtime, Formation = 2, LookerCount = 4, SceneId = 12, SceneRandom = false };

        /// <summary>剛連上線、還沒收過任何房間快照的一台 client:面板停在 config.ini 的預設值。</summary>
        private static GameSession FreshSession()
            => new GameSession { GameMode = Free, Formation = 0, LookerCount = 10, StageId = 9, StageFolder = "SCN0009", StageRandom = true };

        // ---- 1. 房主轉移 ----

        [Test]
        public void Non_Host_Adopts_The_Rooms_Settings_Into_Its_Session()
        {
            var s = FreshSession();
            NetRoomSettingsPublisher.ApplyToSession(s, ShowtimeRoom());

            Assert.AreEqual(Showtime, s.GameMode, "🔴 使用者回報的就是這個:進 ShowTime 房間的人,session 還停在自由模式");
            Assert.AreEqual(2, s.Formation);
            Assert.AreEqual(4, s.LookerCount);
            Assert.AreEqual(12, s.StageId);
            Assert.IsFalse(s.StageRandom);
        }

        [Test]
        public void Promoted_Host_Publishes_The_Same_Settings_It_Adopted()
        {
            // 這就是那個 bug 的完整劇本:B 以非房主身分進了一間 ShowTime 房,房主把房主讓給它。
            // 收過設定之後,升上來的 B 要送出**一模一樣**的一份 —— SameAs 為真 = SyncIfHost 一個字都不送。
            var room = ShowtimeRoom();
            var s = FreshSession();

            Assert.IsFalse(NetRoomSettingsPublisher.FromSession(s).SameAs(room),
                "沒收設定之前,新房主手上的是自己的預設值(這正是會把房間打回自由模式的那份)");

            NetRoomSettingsPublisher.ApplyToSession(s, room);

            Assert.IsTrue(NetRoomSettingsPublisher.FromSession(s).SameAs(room),
                "🔴 收→推必須是恆等的,否則升房主之後每一份快照都會再推一次(無窮迴圈)");
        }

        [Test]
        public void Adopting_Keeps_The_Scene_Folder_In_Step_With_The_Id()
        {
            // 只寫 StageId 的話房間縮圖對了、真的載進去的還是舊資料夾。
            var s = FreshSession();
            NetRoomSettingsPublisher.ApplyToSession(s, new NetRoomSettings { SceneId = 0, SceneRandom = false });

            Assert.AreEqual(0, s.StageId);
            Assert.AreEqual(Sdo.UI.Catalog.StageCatalog.Get(0).Folder, s.StageFolder);
            Assert.AreNotEqual("SCN0009", s.StageFolder, "資料夾要跟著 id 換");
        }

        [Test]
        public void Adopting_A_Scene_We_Do_Not_Have_Falls_Back_Without_Looping()
        {
            // 34 是場景表裡的空號(見 StageCatalog:34/36 沒有資料夾)。退回預設場景是對的,
            // 但 StageId 要跟著退 —— 不然送回去的 sceneId 永遠不等於 server 手上的 → 每份快照都再推一次。
            var s = FreshSession();
            NetRoomSettingsPublisher.ApplyToSession(s,
                new NetRoomSettings { GameMode = Showtime, SceneId = 34, SceneRandom = false });

            Assert.AreEqual(Sdo.UI.Catalog.StageCatalog.DefaultId, s.StageId, "認不得的場景 → 退回預設場景");
            Assert.AreEqual(Sdo.UI.Catalog.StageCatalog.Default.Folder, s.StageFolder, "id 與資料夾要一致");

            var back = NetRoomSettingsPublisher.FromSession(s);
            Assert.AreEqual(Showtime, back.GameMode, "認不得的場景不該連模式一起丟掉");
            Assert.AreEqual(s.StageId, back.SceneId, "推回去的就是我們真的載得動的那個場景");
        }

        [Test]
        public void Adopting_Clamps_Broken_Values_The_Same_Way_The_Wire_Does()
        {
            var s = FreshSession();
            NetRoomSettingsPublisher.ApplyToSession(s,
                new NetRoomSettings { GameMode = 9, Formation = -3, LookerCount = 999, SceneId = 7, SceneRandom = true });

            var back = NetRoomSettingsPublisher.FromSession(s);
            Assert.AreEqual(Showtime, back.GameMode);
            Assert.AreEqual(0, back.Formation);
            Assert.AreEqual(NetLimits.MaxSpectators, back.LookerCount);
        }

        // ---- 1b. 自己開新房 → 房間設定回到自己的預設 ----

        [Test]
        public void Creating_Your_Own_Room_Goes_Back_To_The_Default_Free_Mode()
        {
            // 收設定只該在**那間房**裡有效:在別人的 ShowTime 房待過之後回大廳開自己的房,
            // 不該莫名其妙也變成 ShowTime(使用者指定:自己開新房固定回到預設的自由模式)。
            int mode = RoomConfig.defaultGameMode, scene = RoomConfig.defaultScene;
            try
            {
                RoomConfig.defaultGameMode = Free;   // 出廠值
                RoomConfig.defaultScene = -1;        // 出廠值 = 隨機場景

                var s = FreshSession();
                NetRoomSettingsPublisher.ApplyToSession(s, ShowtimeRoom());
                Assert.AreEqual(Showtime, s.GameMode, "前提:確實收過別人房間的設定");

                s.ResetRoomSettingsToDefaults();

                Assert.AreEqual(Free, s.GameMode, "🔴 自己開新房固定回到預設的自由模式");
                Assert.AreEqual(GameSession.DefaultFormation, s.Formation);
                Assert.AreEqual(GameSession.DefaultLookerCount, s.LookerCount);
                Assert.IsTrue(s.StageRandom, "場景也一起回到預設(隨機)");
            }
            finally { RoomConfig.defaultGameMode = mode; RoomConfig.defaultScene = scene; }
        }

        [Test]
        public void Reset_Honours_A_Configured_Default_Mode_And_Scene()
        {
            // 「預設」的定義就是 config.ini 那兩個 key —— 不是寫死 0/隨機。
            int mode = RoomConfig.defaultGameMode, scene = RoomConfig.defaultScene;
            try
            {
                RoomConfig.defaultGameMode = Normal;
                RoomConfig.defaultScene = 3;

                var s = FreshSession();
                NetRoomSettingsPublisher.ApplyToSession(s, ShowtimeRoom());
                s.ResetRoomSettingsToDefaults();

                Assert.AreEqual(Normal, s.GameMode);
                Assert.AreEqual(3, s.StageId);
                Assert.AreEqual(Sdo.UI.Catalog.StageCatalog.Get(3).Folder, s.StageFolder);
                Assert.IsFalse(s.StageRandom);
            }
            finally { RoomConfig.defaultGameMode = mode; RoomConfig.defaultScene = scene; }
        }

        [Test]
        public void Reset_Leaves_The_Personal_Preferences_Alone()
        {
            // 速度/note 皮/組隊/掉落方向是個人偏好,不是房間設定(官方就是分開的,見 NetRoomSettings 的 doc)。
            var s = FreshSession();
            s.Speed = 6f; s.NoteType = 4; s.Team = 1; s.DropDirection = 2;

            s.ResetRoomSettingsToDefaults();

            Assert.AreEqual(6f, s.Speed);
            Assert.AreEqual(4, s.NoteType);
            Assert.AreEqual(1, s.Team);
            Assert.AreEqual(2, s.DropDirection);
        }

        // ---- 2. 大廳房卡 / 房間信息框的模式字 ----

        [Test]
        public void Protocol_Mode_Maps_To_The_Matching_Ui_Mode()
        {
            Assert.AreEqual(GameMode.Free, NetRoomMapping.ToUiMode(Free));
            Assert.AreEqual(GameMode.Normal, NetRoomMapping.ToUiMode(Normal));
            Assert.AreEqual(GameMode.ShowTime, NetRoomMapping.ToUiMode(Showtime),
                "🔴 使用者回報的就是這個:ShowTime 以前被歸進 Normal → 大廳寫「普通模式」");
        }

        [Test]
        public void Ui_Mode_Values_Match_The_Protocol_Codes()
        {
            // OnlineRoomService.SetMode 直接 cast —— 值對不上就會把 ShowTime 送成別的模式。
            Assert.AreEqual(Free, (int)GameMode.Free);
            Assert.AreEqual(Normal, (int)GameMode.Normal);
            Assert.AreEqual(Showtime, (int)GameMode.ShowTime);
        }

        [Test]
        public void Broken_Mode_Values_Still_Produce_A_Real_Ui_Mode()
        {
            Assert.AreEqual(GameMode.Free, NetRoomMapping.ToUiMode(-1));
            Assert.AreEqual(GameMode.ShowTime, NetRoomMapping.ToUiMode(99));
        }

        [Test]
        public void Room_Snapshot_Carries_Showtime_To_The_Room_Info()
        {
            var snap = new NetRoomSnapshot { Settings = ShowtimeRoom() };
            Assert.AreEqual(GameMode.ShowTime, NetRoomMapping.ToRoomInfo(snap).Mode);
        }

        [Test]
        public void Room_List_Row_Carries_Showtime_To_The_Lobby_Card()
        {
            object node;
            Assert.IsTrue(NetJson.TryParse(
                "{\"code\":47884,\"seq\":3,\"name\":\"\",\"hostName\":\"須彌芥子\"," +
                "\"status\":\"open\",\"count\":1,\"capacity\":6,\"mode\":2}", out node));

            var row = NetRoomMapping.ToRoomInfo(NetRoomListEntry.Decode(node));
            Assert.AreEqual(GameMode.ShowTime, row.Mode, "🔴 大廳房卡那格的資料來源");
        }

        [Test]
        public void Each_Mode_Has_Its_Own_Label_Key()
        {
            Assert.AreEqual("songselect.mode_free", RoomLabels.ModeKey(GameMode.Free));
            Assert.AreEqual("songselect.mode_normal", RoomLabels.ModeKey(GameMode.Normal));
            Assert.AreEqual("songselect.mode_showtime", RoomLabels.ModeKey(GameMode.ShowTime),
                "大廳/房間信息/選歌下拉共用同一組 key —— 同一件事不該有三種講法");

            // 房間右側面板拿到的是協定代號(int),走同一組 key 的 overload。
            Assert.AreEqual("songselect.mode_free", RoomLabels.ModeKey(Free));
            Assert.AreEqual("songselect.mode_normal", RoomLabels.ModeKey(Normal));
            Assert.AreEqual("songselect.mode_showtime", RoomLabels.ModeKey(Showtime));
        }
    }
}
