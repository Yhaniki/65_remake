using Sdo.Net;
using Sdo.UI.Catalog;
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
        /// **非房主**:把 server 手上的房間設定收回自己的 <see cref="GameSession"/>。
        ///
        /// 🔴 這是「把房主讓給別人之後,房間變回自由模式」的修法。
        /// <see cref="SyncIfHost"/> 是拿**本機 session** 去跟 server 比對的,而非房主的 session 從頭到尾
        /// 都還是自己 config.ini 的預設值(<c>RoomConfig.defaultGameMode</c>,通常 0=自由)——
        /// 房主一轉移,新房主下一份快照就把自己那份預設值推上去,整間房的模式/隊形/旁觀人數/場景
        /// 全部被打回他的預設。房間設定是**房間的**,不是誰的個人偏好。
        ///
        /// 收回來之後,升房主那一刻 session 與 server 已經一致 → <see cref="SyncIfHost"/> 不會送任何東西。
        /// 附帶修好另一件事:非房主開選歌對話框時,底下那三個下拉終於顯示這間房真正的設定。
        /// </summary>
        public static void AdoptIfNotHost(AppContext ctx)
        {
            if (ctx == null || ctx.Net == null || ctx.Session == null) return;
            if (!ctx.Net.IsConnected || !ctx.Net.InRoom || ctx.Net.IsHost) return;
            var snap = ctx.Net.Room;
            if (snap == null || snap.Settings == null) return;
            ApplyToSession(ctx.Session, snap.Settings);
        }

        /// <summary>
        /// 房間設定 → session 面板值。<see cref="FromSession"/> 的反向,而且必須是它的**左反元**:
        /// <c>FromSession(套用後的 session)</c> 要與傳進來的設定 <see cref="NetRoomSettings.SameAs"/>,
        /// 否則升房主之後每一份快照都會再推一次(無窮迴圈)。見 TeamModeGateTests 的往返測試。
        /// </summary>
        public static void ApplyToSession(GameSession s, NetRoomSettings settings)
        {
            if (s == null || settings == null) return;

            s.GameMode = Mathf.Clamp(settings.GameMode, 0, 2);
            s.Formation = Mathf.Clamp(settings.Formation, 0, 3);
            s.LookerCount = Mathf.Clamp(settings.LookerCount, 0, NetLimits.MaxSpectators);
            s.StageRandom = settings.SceneRandom;

            // 場景要連 Folder 一起換 —— 只寫 StageId 的話房間縮圖對了、真的載進去的還是舊資料夾。
            // 目錄裡沒有這個 id(對方版本比較新)→ StageCatalog.Get 退回預設場景,而且 StageId 也跟著退,
            // 這樣 FromSession 送回去的仍然是一個我們真的載得動的場景。
            var st = StageCatalog.Get(settings.SceneId);
            s.StageId = st.Id;
            s.StageFolder = st.Folder;
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
