using NUnit.Framework;
using Sdo.Net;

namespace Sdo.Tests
{
    /// <summary>
    /// 狀態 enum 的 wire 轉換與狀態機判定。
    ///
    /// 最重要的一條是 <see cref="NetState.IsClientSettable"/> —— 那是一道**安全邊界**:
    /// 沒有它,一個改過的 client 可以自稱 <c>playing</c> 來繞過整個載入同步機制。
    /// </summary>
    public class NetEnumsTests
    {
        // ---- wire round-trip ----

        [Test]
        public void All_PlayStates_Round_Trip()
        {
            foreach (PlayState s in System.Enum.GetValues(typeof(PlayState)))
            {
                PlayState back;
                Assert.IsTrue(NetState.TryParsePlayState(NetState.ToWire(s), out back), NetState.ToWire(s));
                Assert.AreEqual(s, back);
            }
        }

        [Test]
        public void All_RoomStatuses_Round_Trip()
        {
            foreach (RoomStatus s in System.Enum.GetValues(typeof(RoomStatus)))
            {
                RoomStatus back;
                Assert.IsTrue(NetState.TryParseRoomStatus(NetState.ToWire(s), out back), NetState.ToWire(s));
                Assert.AreEqual(s, back);
            }
        }

        [Test]
        public void All_Availabilities_Round_Trip()
        {
            foreach (Availability a in System.Enum.GetValues(typeof(Availability)))
            {
                Availability back;
                Assert.IsTrue(NetState.TryParseAvailability(NetState.ToWire(a), out back), NetState.ToWire(a));
                Assert.AreEqual(a, back);
            }
        }

        [Test]
        public void All_SeatStates_Round_Trip()
        {
            foreach (SeatState s in System.Enum.GetValues(typeof(SeatState)))
            {
                SeatState back;
                Assert.IsTrue(NetState.TryParseSeatState(NetState.ToWire(s), out back), NetState.ToWire(s));
                Assert.AreEqual(s, back);
            }
        }

        [Test]
        public void Wire_Names_Are_Stable()
        {
            // 這些字串是 wire format 的一部分 —— 改了就是破壞相容性(要同時 bump NetProto.Version)。
            // 釘住它們,讓「不小心改到」變成測試失敗而不是線上事故。
            Assert.AreEqual("idle", NetState.ToWire(PlayState.Idle));
            Assert.AreEqual("ready", NetState.ToWire(PlayState.Ready));
            Assert.AreEqual("waitingForLoad", NetState.ToWire(PlayState.WaitingForLoad));
            Assert.AreEqual("loaded", NetState.ToWire(PlayState.Loaded));
            Assert.AreEqual("readyForGameplay", NetState.ToWire(PlayState.ReadyForGameplay));
            Assert.AreEqual("playing", NetState.ToWire(PlayState.Playing));
            Assert.AreEqual("finished", NetState.ToWire(PlayState.Finished));
            Assert.AreEqual("results", NetState.ToWire(PlayState.Results));
            Assert.AreEqual("spectating", NetState.ToWire(PlayState.Spectating));

            Assert.AreEqual("missing", NetState.ToWire(Availability.Missing));
            Assert.AreEqual("have", NetState.ToWire(Availability.Have));
            Assert.AreEqual("taken", NetState.ToWire(SeatState.Taken));
        }

        [Test]
        public void Unknown_Wire_Values_Fail_And_Yield_A_Safe_Default()
        {
            // 對方送了我們不認得的狀態(可能是新版 client)。要回 false 讓呼叫端決定怎麼辦,
            // 而且 out 參數要是安全的預設值(不能是「更有權限」的狀態)。
            PlayState p;
            Assert.IsFalse(NetState.TryParsePlayState("teleporting", out p));
            Assert.AreEqual(PlayState.Idle, p, "未知狀態的預設值必須是最無害的 idle");

            Assert.IsFalse(NetState.TryParsePlayState("", out p));
            Assert.IsFalse(NetState.TryParsePlayState(null, out p));

            RoomStatus r;
            Assert.IsFalse(NetState.TryParseRoomStatus("exploding", out r));
            Assert.AreEqual(RoomStatus.Open, r);

            Availability a;
            Assert.IsFalse(NetState.TryParseAvailability("maybe", out a));
            Assert.AreEqual(Availability.Unknown, a);

            SeatState s;
            Assert.IsFalse(NetState.TryParseSeatState("quantum", out s));
            Assert.AreEqual(SeatState.Open, s);
        }

        [Test]
        public void Wire_Parsing_Is_Case_Sensitive()
        {
            // 刻意大小寫敏感:wire format 是我們自己兩邊都控制的,寬鬆解析只會遮蓋掉真正的 bug。
            PlayState p;
            Assert.IsFalse(NetState.TryParsePlayState("Idle", out p));
            Assert.IsFalse(NetState.TryParsePlayState("WAITINGFORLOAD", out p));
        }

        // ---- 🔴 安全邊界 ----

        [Test]
        public void Server_Reserved_States_Are_Not_Client_Settable()
        {
            // 這三個代表「server 已經把這個人納入某個階段」,只有 server 有權宣告。
            // 沒有這道檢查,改過的 client 可以自稱 playing 直接繞過載入同步 ——
            // 它會在別人還在載入時就開始跑譜面。
            Assert.IsFalse(NetState.IsClientSettable(PlayState.WaitingForLoad));
            Assert.IsFalse(NetState.IsClientSettable(PlayState.Playing));
            Assert.IsFalse(NetState.IsClientSettable(PlayState.Results));
        }

        [Test]
        public void Client_Settable_States_Are_Exactly_The_Expected_Six()
        {
            Assert.IsTrue(NetState.IsClientSettable(PlayState.Idle));
            Assert.IsTrue(NetState.IsClientSettable(PlayState.Ready));
            Assert.IsTrue(NetState.IsClientSettable(PlayState.Loaded));
            Assert.IsTrue(NetState.IsClientSettable(PlayState.ReadyForGameplay));
            Assert.IsTrue(NetState.IsClientSettable(PlayState.Finished));
            Assert.IsTrue(NetState.IsClientSettable(PlayState.Spectating));

            // 數量也釘住 —— 將來有人新增狀態時，會被迫回來想「這個該不該讓 client 設」。
            int settable = 0;
            foreach (PlayState s in System.Enum.GetValues(typeof(PlayState)))
                if (NetState.IsClientSettable(s)) settable++;
            Assert.AreEqual(6, settable);
        }

        // ---- 狀態機判定 ----

        [Test]
        public void CanStartGameplay_Is_Loaded_Or_ReadyForGameplay()
        {
            // 照抄 osu 的 MultiplayerRoomUser.CanStartGameplay()。
            // 房間從 waitingForLoad 推進到 playing 的條件是「沒人還在 waitingForLoad」,
            // 這個 helper 決定的是「哪些人要被一起帶進 playing」。
            Assert.IsTrue(NetState.CanStartGameplay(PlayState.Loaded));
            Assert.IsTrue(NetState.CanStartGameplay(PlayState.ReadyForGameplay));

            Assert.IsFalse(NetState.CanStartGameplay(PlayState.WaitingForLoad), "還在載入的不能開場");
            Assert.IsFalse(NetState.CanStartGameplay(PlayState.Idle));
            Assert.IsFalse(NetState.CanStartGameplay(PlayState.Ready));
            Assert.IsFalse(NetState.CanStartGameplay(PlayState.Playing));
            Assert.IsFalse(NetState.CanStartGameplay(PlayState.Spectating));
        }

        [Test]
        public void IsInMatch_Covers_The_Whole_Participating_Span()
        {
            // 從被納入載入開始,到打完為止。用來判斷「這個人是本場的參與者嗎」。
            Assert.IsTrue(NetState.IsInMatch(PlayState.WaitingForLoad));
            Assert.IsTrue(NetState.IsInMatch(PlayState.Loaded));
            Assert.IsTrue(NetState.IsInMatch(PlayState.ReadyForGameplay));
            Assert.IsTrue(NetState.IsInMatch(PlayState.Playing));
            Assert.IsTrue(NetState.IsInMatch(PlayState.Finished));

            Assert.IsFalse(NetState.IsInMatch(PlayState.Idle), "沒準備的人留在房間,不算參與者");
            Assert.IsFalse(NetState.IsInMatch(PlayState.Ready), "按了準備但還沒開場 —— 還不算在場中");
            Assert.IsFalse(NetState.IsInMatch(PlayState.Results), "已經在看結算了");
            Assert.IsFalse(NetState.IsInMatch(PlayState.Spectating), "旁觀者不是參與者");
        }

        // ---- 隊伍 ----

        [Test]
        public void Team_Values_Are_Zero_To_Three()
        {
            // 與遊戲內 GameSession.Team 同一套編碼(0=A, 1=B, 2=C, 3=自由)。
            Assert.IsTrue(NetState.IsValidTeam(0));
            Assert.IsTrue(NetState.IsValidTeam(3));
            Assert.IsFalse(NetState.IsValidTeam(-1));
            Assert.IsFalse(NetState.IsValidTeam(4));
        }

        [Test]
        public void ClampTeam_Falls_Back_To_Free()
        {
            // 收到範圍外的值時退到「自由」—— 那是最無害的選擇(不會意外把人塞進某一隊)。
            Assert.AreEqual(TeamTag.A, NetState.ClampTeam(0));
            Assert.AreEqual(TeamTag.C, NetState.ClampTeam(2));
            Assert.AreEqual(TeamTag.Free, NetState.ClampTeam(3));
            Assert.AreEqual(TeamTag.Free, NetState.ClampTeam(99));
            Assert.AreEqual(TeamTag.Free, NetState.ClampTeam(-5));
        }
    }
}
