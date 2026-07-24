using UnityEngine;

namespace Sdo.UI.Core
{
    /// <summary>
    /// 除錯／測試專用功能的總開關：只在 Unity 編輯器裡開著（給開發/測試用），打包成 build（dance.exe）一律關閉。
    /// 目前守著：
    ///   1. 房間 F3 除錯鍵——切換本機「有家族 / 沒有家族」（見 RoomScreen.Update）。
    ///   2. 離線 MockChatService 的「模擬他人聊天」——bot 大廳閒聊、同族閒聊、家族/密語罐頭回覆
    ///      （見 AppContext.CreateMock 傳入的 simulateOthers）。
    /// 若哪天想在「開發 build」也保留（release build 才關）→ 把 Enabled 改成 Debug.isDebugBuild。
    /// </summary>
    public static class SdoDebugFeatures
    {
        public static bool Enabled => Application.isEditor;
    }
}
