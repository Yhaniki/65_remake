using NUnit.Framework;
using Sdo.Net;
using Sdo.UI.Services;

namespace Sdo.Tests
{
    /// <summary>
    /// 規則:**旁觀的人不能用氣泡,只能用左下打字框**(<see cref="RoomBubblePolicy"/>)。
    /// 兩邊都要擋:自己旁觀時打字不進頭上泡、別人旁觀時他的話也不彈泡。
    /// </summary>
    public class RoomBubblePolicyTests
    {
        private const int Me = 11, Other = 22, Ghost = 33;

        private static NetRoomSnapshot RoomWith(params int[] spectatorIds)
        {
            var snap = new NetRoomSnapshot();
            snap.Spectators = new NetSpectator[spectatorIds.Length];
            for (int i = 0; i < spectatorIds.Length; i++)
                snap.Spectators[i] = new NetSpectator { UserId = spectatorIds[i], Name = "spec" + i };
            return snap;
        }

        private static ChatMessage Remote(int senderUserId)
            => new ChatMessage { SenderUserId = senderUserId, Text = "hi", Scope = ChatScope.Room };

        private static ChatMessage Mine()
            => new ChatMessage { Local = true, SenderUserId = 0, Text = "hi", Scope = ChatScope.Room };

        [Test]
        public void Spectator_List_Decides_Who_Is_Spectating()
        {
            var snap = RoomWith(Other);
            Assert.IsTrue(RoomBubblePolicy.IsSpectator(snap, Other));
            Assert.IsFalse(RoomBubblePolicy.IsSpectator(snap, Me), "座位上的人不是旁觀者");
            Assert.IsFalse(RoomBubblePolicy.IsSpectator(snap, Ghost), "不在房裡的人不是旁觀者");
        }

        [Test]
        public void Offline_Has_No_Spectators()
        {
            Assert.IsFalse(RoomBubblePolicy.IsSpectator(null, Other), "離線沒有快照 → 不是旁觀者");
            Assert.IsFalse(RoomBubblePolicy.IsSpectator(RoomWith(Other), 0), "userId 0 不是任何人");
        }

        /// <summary>泡與關鍵字動作是同一道門:旁觀者的話兩樣都不給,只留左下訊息欄那行字。</summary>
        [Test]
        public void Remote_Spectator_Gets_Neither_Bubble_Nor_Action()
        {
            var snap = RoomWith(Other);
            bool spectating = RoomBubblePolicy.SpeakerIsSpectator(Remote(Other), false, snap);
            Assert.IsTrue(spectating);
            Assert.IsFalse(RoomBubblePolicy.CanEmoteInRoom(spectating), "旁觀者的話只進左下訊息欄");
        }

        [Test]
        public void Remote_Seated_Player_Still_Gets_A_Bubble()
        {
            var snap = RoomWith(Other);
            bool spectating = RoomBubblePolicy.SpeakerIsSpectator(Remote(Me), false, snap);
            Assert.IsFalse(spectating);
            Assert.IsTrue(RoomBubblePolicy.CanEmoteInRoom(spectating));
        }

        /// <summary>自己旁觀時送出的話,也不該讓自己的角色做關鍵字動作。</summary>
        [Test]
        public void Local_Spectator_Gets_Neither_Bubble_Nor_Action()
        {
            Assert.IsFalse(RoomBubblePolicy.CanEmoteInRoom(
                RoomBubblePolicy.SpeakerIsSpectator(Mine(), true, RoomWith(Me))));
        }

        /// <summary>
        /// 🔴 本機訊息的 SenderUserId 是 0(見 ChatMessage 的註解)—— 只查快照永遠會判成「不是旁觀者」,
        /// 自己旁觀時的發言就會照樣彈泡。所以本機那條一定要看 localSpectating。
        /// </summary>
        [Test]
        public void Local_Message_Uses_Local_Spectating_Not_The_Snapshot()
        {
            var snap = RoomWith(Me);   // 自己在旁觀名單裡,但本機訊息不帶 userId
            Assert.IsTrue(RoomBubblePolicy.SpeakerIsSpectator(Mine(), true, snap));
            Assert.IsFalse(RoomBubblePolicy.SpeakerIsSpectator(Mine(), false, snap), "坐回座位 → 自己的話又能彈泡");
        }

        [Test]
        public void Local_Spectator_Types_In_The_Chat_Input_Only()
        {
            Assert.IsFalse(RoomBubblePolicy.CanTypeInBubble(true), "旁觀中 → 打字走左下輸入框");
            Assert.IsTrue(RoomBubblePolicy.CanTypeInBubble(false), "座位上 → 照舊彈頭上打字泡");
        }

        [Test]
        public void Null_Message_Is_Not_A_Spectator()
        {
            Assert.IsFalse(RoomBubblePolicy.SpeakerIsSpectator(null, true, RoomWith(Me)));
        }
    }
}
