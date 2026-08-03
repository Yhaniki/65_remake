using System;
using Sdo.Localization;
using Sdo.Settings;
using Sdo.UI.Core;
using Sdo.UI.Services;

namespace Sdo.UI.Util
{
    /// <summary>選單項目 → 顯示文字。與房間座位選單共用同一組 key,兩邊的字一定一樣。</summary>
    public static class PlayerMenuLabels
    {
        public static string Of(PlayerAction a)
        {
            switch (a)
            {
                case PlayerAction.PlayerInfo: return LocalizationManager.Get("room.slot_player_info");
                case PlayerAction.Whisper: return LocalizationManager.Get("room.slot_whisper");
                case PlayerAction.AddFriend: return LocalizationManager.Get("room.slot_add_friend");
                case PlayerAction.RemoveFriend: return LocalizationManager.Get("room.slot_remove_friend");
                case PlayerAction.Block: return LocalizationManager.Get("room.slot_block");
                default: return LocalizationManager.Get("room.slot_unblock");
            }
        }
    }

    /// <summary>
    /// 選單項目按下去實際做的事 —— 大廳玩家名單與「房間信息」的參與者列表**共用這一份**。
    ///
    /// 為什麼要有這層:兩個畫面的選單長得一樣、按下去也該做一樣的事,但它們是兩個互不相干的類別。
    /// 各寫一份的結果必然是「其中一邊忘了 <c>ProfileManager.Save()</c>」這種只有玩家重開遊戲才發現的 bug。
    ///
    /// 🔴 這裡**不彈任何 Toast**(使用者明講:大廳一律安靜)。加好友/封鎖成功與否的回饋是看得見的畫面變化 ——
    ///    名單重畫、以及下次右鍵同一個人時選單本身會翻成「刪除好友 / 移出黑名單」。
    /// 🔴 清單改完**一定要存檔**:<see cref="FriendList"/> / <see cref="BlockList"/> 刻意不自己存
    ///    (它們常常跟別的 profile 變更一起發生),漏了這一步關掉遊戲就沒了。
    /// </summary>
    public static class PlayerMenuActions
    {
        /// <summary>
        /// 跑一個選單項目。回 true = **本機清單被改動了**(好友/黑名單)——呼叫端據此重畫名單;
        /// 開視窗、開始密語那兩項不算改動,回 false。
        /// </summary>
        /// <param name="who">對象的顯示名字(好友/黑名單以它為鍵,見 <see cref="FriendList"/>)。</param>
        /// <param name="profile">對象的座位資料;null → 用名字現組一份給玩家資訊視窗。</param>
        /// <param name="gender">0=女 1=男(玩家資訊視窗要畫他的 3D 角色)。</param>
        /// <param name="userId">server 這次連線給的編號;0 = 不知道(視窗會退回用名字查)。</param>
        /// <param name="isSelf">這是我自己 → 「玩家信息」開的是自己那份(本機 profile,不必問 server)。</param>
        public static bool Run(PlayerAction action, string who, PlayerProfile profile, int gender, int userId,
                               bool isSelf)
        {
            string name = (who ?? "").Trim();
            if (name.Length == 0) return false;
            var me = ProfileManager.Active;

            switch (action)
            {
                case PlayerAction.PlayerInfo:
                    if (isSelf) { Nav.OpenSelfInfo?.Invoke(); return false; }
                    // 名單只有名字/等級時也要開得起來:視窗自己會拿 userId(或名字)去跟 server 要名片。
                    Nav.OpenPlayerInfo?.Invoke(profile ?? new PlayerProfile("", name, 0), gender, userId);
                    return false;

                case PlayerAction.Whisper:
                    Nav.WhisperTo?.Invoke(name);
                    return false;

                case PlayerAction.AddFriend:
                    return Persist(FriendList.Add(me, name, PlayerIdOf(profile), NowIso()));

                case PlayerAction.RemoveFriend:
                    return Persist(FriendList.Remove(me, name));

                case PlayerAction.Block:
                    return Persist(BlockList.Add(me, name, PlayerIdOf(profile), NowIso()));

                default:
                    return Persist(BlockList.Remove(me, name));
            }
        }

        private static bool Persist(bool changed)
        {
            if (changed) ProfileManager.Save();
            return changed;
        }

        /// <summary>對方本機存檔的編號(備查用;好友/黑名單認的是名字)。拿不到就存空字串。</summary>
        private static string PlayerIdOf(PlayerProfile p) => p != null ? (p.Id ?? "") : "";

        private static string NowIso() => DateTime.UtcNow.ToString("o");
    }
}
