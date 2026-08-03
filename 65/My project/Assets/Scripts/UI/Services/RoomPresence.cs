using System.Collections.Generic;
using Sdo.Net;

namespace Sdo.UI.Services
{
    /// <summary>
    /// 「上一份快照到這一份之間,**誰來了、誰走了**」—— 左下訊息欄那兩行藍字廣播
    /// (「X 進入舞台遊戲」/「X 離開舞台遊戲」)唯一的依據。
    ///
    /// 🔴 在房間裡的人 = **座位 + 旁觀席**。只算座位的話,按一下「旁觀」鈕的人只是從 Seats 搬到
    ///    Spectators,卻會被判成「離開」再「進入」—— 玩家每切換一次旁觀,房裡每個人的訊息欄就多兩行
    ///    根本沒發生的進出(使用者回報的重複訊息)。人有沒有離開房間,看的是「兩張名單都找不到他」。
    ///
    /// 純函式、零 UnityEngine → 直接單元測試(見 RoomPresenceTests)。這很重要:誤判要開兩個 client
    /// 才看得出來,而且症狀(多幾行字)輕到很容易被當成正常。
    /// </summary>
    public static class RoomPresence
    {
        /// <summary>
        /// 這一份快照裡「人在房間」的所有遠端玩家:<c>userId → 顯示名</c>。
        /// 本機自己(<paramref name="me"/>)排除 —— 自己的進出不是廣播該講的事。
        /// </summary>
        public static void Collect(NetRoomSnapshot snap, int me, Dictionary<int, string> into)
        {
            if (into == null) return;
            into.Clear();
            if (snap == null) return;

            var seats = snap.Seats;
            if (seats != null)
                for (int i = 0; i < seats.Length; i++)
                {
                    var s = seats[i];
                    if (s == null || !s.IsTaken || s.UserId == me) continue;
                    into[s.UserId] = s.Name ?? "";
                }

            var specs = snap.Spectators;
            if (specs != null)
                for (int i = 0; i < specs.Length && i < NetLimits.MaxSpectators; i++)
                {
                    var sp = specs[i];
                    if (sp == null || sp.UserId == 0 || sp.UserId == me) continue;
                    into[sp.UserId] = sp.Name ?? "";
                }
        }

        /// <summary>
        /// 兩份名單的差異 → <paramref name="entered"/> / <paramref name="left"/> 各是要廣播的**顯示名**。
        /// 只在名單裡出現/消失才算數:同一個人從座位換到旁觀席(或反過來)兩邊都在,兩份清單都不會有他。
        /// 離開用的是**上一份**記到的名字 —— 人都不在快照裡了,那是唯一還查得到的名字。
        /// </summary>
        public static void Diff(Dictionary<int, string> before, Dictionary<int, string> now,
                               List<string> entered, List<string> left)
        {
            if (entered != null) entered.Clear();
            if (left != null) left.Clear();
            if (before == null || now == null) return;

            if (entered != null)
                foreach (var kv in now)
                    if (!before.ContainsKey(kv.Key)) entered.Add(kv.Value);

            if (left != null)
                foreach (var kv in before)
                    if (!now.ContainsKey(kv.Key)) left.Add(kv.Value);
        }
    }
}
