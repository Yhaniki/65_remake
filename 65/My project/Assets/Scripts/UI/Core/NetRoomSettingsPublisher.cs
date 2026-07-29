using Sdo.Net;
using UnityEngine;

namespace Sdo.UI.Core
{
    /// <summary>
    /// 房主把**房間設定**(模式 / 隊形 / 旁觀人數 / 場景)同步給 server。
    ///
    /// 為什麼需要這個檔:這四項是在選歌畫面那三個下拉(以及選場景)改的,而那裡只寫進
    /// <see cref="GameSession"/> —— 在此之前**沒有任何一條路徑**把它們送上去
    /// (<c>OnlineRoomService.SetMode</c> 存在但沒有人呼叫)。結果是線上這間房的設定永遠是 server 的預設值:
    /// 房主明明選了「普通模式」,其他人的面板卻一直寫「自由模式」,而「只有普通模式才能組隊」
    /// 這條規則因此永遠不可能成立(server 眼中沒有一間房是普通模式)。
    ///
    /// 做法照 <see cref="NetSongPublisher.PublishIfRoomHasNone"/> 的形狀:
    /// **每一份房間快照都檢查一次**,而不是只在進房那一刻送一次 —— 進房時房間可能還沒建好
    /// (createRoom 要等 server 回 roomState),那一刻 <c>InRoom</c> 還是 false,送了也是靜默丟掉。
    /// 守門是「跟 server 手上的不一樣才送」(<see cref="NetRoomSettings.SameAs"/>),
    /// 所以送成功之後下一份快照就會停 —— 不會變成互相觸發的無窮迴圈。
    /// </summary>
    public static class NetRoomSettingsPublisher
    {
        /// <summary>房主:設定與 server 手上的不同就送一次。非房主 / 離線 / 還沒進房 → 什麼都不做。</summary>
        public static void SyncIfHost(AppContext ctx)
        {
            if (ctx == null || ctx.Net == null || ctx.Session == null) return;
            if (!ctx.Net.IsConnected || !ctx.Net.InRoom || !ctx.Net.IsHost) return;
            var snap = ctx.Net.Room;
            if (snap == null) return;                       // 快照還沒到 → 下一份再試

            var want = FromSession(ctx.Session);
            if (snap.Settings != null && snap.Settings.SameAs(want)) return;

            Debug.Log("[net] 同步房間設定給 server:模式=" + want.GameMode + " 隊形=" + want.Formation
                      + " 旁觀=" + want.LookerCount + " 場景=" + (want.SceneRandom ? "隨機" : want.SceneId.ToString()));
            ctx.Net.SetRoomSettings(want);
        }

        /// <summary>
        /// 把 session 面板上的值換成一份 <see cref="NetRoomSettings"/>。
        ///
        /// 這裡的夾值要與 <see cref="NetRoomSettings.Decode"/> / <c>ApplyPatch</c> **完全一致** ——
        /// 送出去的值被 server 夾成別的數字時,echo 回來就永遠不等於我們想要的,
        /// <see cref="SyncIfHost"/> 會每一份快照都再送一次(無窮迴圈)。
        /// </summary>
        public static NetRoomSettings FromSession(GameSession s)
        {
            return new NetRoomSettings
            {
                GameMode = Mathf.Clamp(s.GameMode, 0, 2),
                Formation = Mathf.Clamp(s.Formation, 0, 3),
                LookerCount = Mathf.Clamp(s.LookerCount, 0, NetLimits.MaxSpectators),
                SceneId = Mathf.Clamp(s.StageId, 0, NetLimits.MaxSceneId),
                SceneRandom = s.StageRandom,
            };
        }
    }
}
