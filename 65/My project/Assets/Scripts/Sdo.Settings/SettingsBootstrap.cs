using Sdo.Settings.Vfs;
using UnityEngine;

namespace Sdo.Settings
{
    /// <summary>Loads + applies the persisted display settings before the first scene renders.</summary>
    public static class SettingsBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Boot()
        {
            // 開機第一行就把實際解析到的 data root 印出來（含 SDO_DATA_ROOT / data_root.txt 覆寫的結果）——
            // 「資產/存檔到底讀哪一棵樹」以前只能靠猜。
            Debug.Log($"[DataRoot] {SdoDataRoot.Root}  (profiles: {SdoDataRoot.ProfileDir})");

            // 🔴 VFS 一定要在**主執行緒**先掛好：SdoVfs 的延遲初始化會碰 SdoDataRoot.Root，而那裡面讀
            //    Application.dataPath —— 只能在主執行緒取。AvatarAssetCache 會在背景執行緒預讀資產，
            //    要是讓它先觸發初始化，root 會解析成錯的值**而且被快取起來**，整個 session 都讀錯樹。
            SdoVfs.Initialise();
            Debug.Log("[vfs] " + string.Join(" | ", SdoVfs.LayerNames()));

            // 順序有相依：config.ini 是設定總表（[Room] + [Option]），先讀它；profile.json 的一次性搬遷要撿 config.ini
            // 舊 [Profile] 區的值，所以緊接在後；keymaps.ini 缺檔時要靠 config.ini 的舊 opt_keys 種一份；GameSettings
            // 工作副本由前兩者組成；ProfileManager 的 active 使用者讀的是 profile.json 的 activeId。
            RoomConfig.Load();               // DATA/PROFILE/config.ini（並一次性併入舊 settings.json / 舊位置 config.ini）
            ProfileDefaults.Load();          // DATA/PROFILE/profile.json（登入的角色 + 家族/等級預設；併入舊 [Profile] / active.txt）
            KeyMap.Load();                   // DATA/PROFILE/keymaps.ini（4 鍵鍵位 + 遊玩功能鍵）
            DisplaySettingsManager.Load();   // [Option] + 鍵位 → 執行期 GameSettings
            ProfileManager.Boot();           // 解析/建立 DATA/PROFILE（active 使用者+資料夾）
            DisplaySettingsManager.ApplyDisplay();
        }
    }
}
