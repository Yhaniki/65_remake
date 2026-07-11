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
    }
}
