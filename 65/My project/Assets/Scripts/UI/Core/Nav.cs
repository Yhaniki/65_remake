using System;

namespace Sdo.UI.Core
{
    /// <summary>Hooks set by FrontendApp so screens can open overlays without referencing it directly.</summary>
    public static class Nav
    {
        public static Action OpenSettings;
        public static Action OpenNoteSkinPicker;
        public static Action OpenShop;             // Room -> 商城 (avatar shop): browse / buy / try-on clothing
        public static Action OpenWardrobe;         // Room 衣服鈕 -> 儲物櫃 (INVENTORY): 已擁有衣物 / 換穿 (WardrobeScreen)
        public static Action RefreshRoomAvatar;    // 換穿後：叫房間重建本機 3D avatar，讓穿搭立即反映 (RoomScreen 綁)
        public static Action RefreshGenderPreview; // 商城(modal)關閉後：叫男女選擇畫面用最新穿搭/性別刷新 3D 預覽 (GenderSelectScreen 綁)
        public static Action StartGame;   // host pressed Start -> hand off to gameplay (ScreenGameplay) with the session selection
        public static Action PlayRoomEntrance;   // 進房間轉場漸亮時觸發：房間 UI 從四邊滑入（見 RoomScreen.PlayEntrance）

        // 座位右鍵「玩家信息」→ 玩家資訊視窗（PlayerInfoModal）。
        // 走 Nav 而不是讓 RoomScreen 直接抓 FrontendApp.Instance.XxxModal：那個 modal 與畫面是分開演進的，
        // 一旦 RoomScreen 直接依賴具體欄位，modal 還沒接上時整個房間就編不過（這正是 Nav 存在的理由）。
        // 沒接上時 ?.Invoke() 什麼也不做，不是 NRE。
        // 別人的資料。參數:(對方的座位資料, 對方性別 0=女 1=男, 對方的 server userId)。
        // 🔴 userId 是**線上查名片的鍵**(命中率/穿搭那些數字要跟 server 要,見 NetProto.PlayerCardQuery)——
        //    PlayerProfile.Id 是對方 client 自報的存檔編號,不是 server 認得的東西。0 = 離線或查不到。
        //    離線那條路靠的是**名字**(server 的快照表以名字為鍵),modal 自己會用 DisplayName 去查。
        public static Action<Sdo.UI.Services.PlayerProfile, int, int> OpenPlayerInfo;
        public static Action OpenSelfInfo;                                          // 自己的資料（modal 自己讀本機 profile）

        // 大廳「創建舞台」→ 官方的創建遊戲房間對話框（RoomCreateModal）。
        // 回呼收到 (房名, 密碼, 模式)；使用者按取消就不會回呼。
        public static Action<Action<string, string, Sdo.UI.Services.GameMode>> OpenRoomCreate;

        // 大廳房卡右鍵 → 官方的房間信息對話框（RoomInfoModal）。第二個參數是按「進入」時要做的事。
        public static Action<Sdo.UI.Services.RoomInfo, Action> OpenRoomInfo;
    }
}
