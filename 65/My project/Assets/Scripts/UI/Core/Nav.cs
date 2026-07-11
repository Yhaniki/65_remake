using System;

namespace Sdo.UI.Core
{
    /// <summary>Hooks set by FrontendApp so screens can open overlays without referencing it directly.</summary>
    public static class Nav
    {
        public static Action OpenSettings;
        public static Action OpenNoteSkinPicker;
        public static Action OpenShop;             // Room -> 商城 (avatar shop): browse / buy / try-on clothing
        public static Action StartGame;   // host pressed Start -> hand off to gameplay (ScreenGameplay) with the session selection
        public static Action PlayRoomEntrance;   // 進房間轉場漸亮時觸發：房間 UI 從四邊滑入（見 RoomScreen.PlayEntrance）
    }
}
