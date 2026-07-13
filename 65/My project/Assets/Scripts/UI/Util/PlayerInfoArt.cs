using System.IO;
using UnityEngine;
using Sdo.Game;

namespace Sdo.UI.Util
{
    /// <summary>
    /// 官方「個人資訊」對話框 (PlayerInformationDlg) 的素材。全部走官方 .an 圖集裁切描述，
    /// 底圖 BaseBoard.png / BaseBoard2.png / Effort.png 一個像素都不改（中文是烘在圖上的，我們只疊動態數值）。
    ///
    /// 只做**女版粉色皮**：官方依自己角色的性別換整套皮 (BaseBoard vs BaseBoard_man)，但重製版其餘 UI
    /// (ROOM / ROOMDLG / SHOP / MYHOUSEDLG…) 一律用女版，這裡跟著一致。
    ///
    /// 這整包素材**離線版沒有** (sdox_offline/Extracted/UI 裡沒有 PLAYERINFORMATIONDLG)，只有線上版
    /// 閉撰敃氪/DatasSDO 有 → 必須讓 data root 吃得到它（tools/link_data_root.ps1）。
    /// 用到哪些檔請見 docs/USED_ASSETS_PLAYERINFO.md。
    /// </summary>
    public static class PlayerInfoArt
    {
        private static string _dir;

        /// <summary>PLAYERINFORMATIONDLG 素材夾（lazy）。可設定以利測試。</summary>
        public static string Dir
        {
            get { return _dir ?? (_dir = Path.Combine(SdoExtracted.Root, "UI", "PLAYERINFORMATIONDLG")); }
            set { _dir = value; }
        }

        /// <summary>素材在不在（沒接 data root 時整個面板應該安靜地不開，而不是畫出一堆破圖）。</summary>
        public static bool Available => File.Exists(Path.Combine(Dir, "PLAYERINFORMATIONDLG0.AN"))
                                     && File.Exists(Path.Combine(Dir, "BASEBOARD.PNG"));

        private static Sprite An(string name) => SdoExtracted.LoadAn1(Dir, name, bleed: true);

        // ---- 外框 / 關閉 / 確定 ----
        /// <summary>粉色外框（含烘死的「个人信息 PERSONAL INFORMATION」標題）。XML: Label @ (93,56)。</summary>
        public static Sprite Frame => An("PlayerInformationDlg0");
        public static Sprite CloseN => An("PlayerInformationDlg14");
        public static Sprite CloseH => An("PlayerInformationDlg15");
        public static Sprite CloseP => An("PlayerInformationDlg16");
        public static Sprite ConfirmN => An("PlayerInformationDlg29");
        public static Sprite ConfirmH => An("PlayerInformationDlg30");
        public static Sprite ConfirmP => An("PlayerInformationDlg31");

        // ---- 分頁條 ----
        // 每個分頁的 .an 都是一條 350×39 的圖，只有「自己那一格」有畫，其餘透明 → 全部疊起來才是完整的分頁列。
        // normal = 暗（未選），pushed = 亮 + 底下那條橘線。XML: 全部 @ (336,108)。
        public static Sprite Tab0N => An("PlayerInformationDlg4");    // 基本信息
        public static Sprite Tab0P => An("PlayerInformationDlg6");
        public static Sprite Tab1N => An("PlayerInformationDlg7");    // 技术统计 ← 戰績數據
        public static Sprite Tab1P => An("PlayerInformationDlg9");

        // ---- 分頁內容底圖（都 @ (336,147)）----
        public static Sprite Board0 => An("PlayerInformationDlg34");  // 基本信息：天使等级/TP/经验/魅力/幸运/知名度/家族/年龄…
        public static Sprite Board1 => An("PlayerInformationDlg43");  // 技术统计：超舞战绩/劲舞战绩/目前排名 + 成就/统计明细

        // ---- 技术统计 分頁 ----
        /// <summary>六列（胜率/命中率/Perfect率/Cool率/Bad率/Miss率）的底板，含烘死的列名與空的進度條槽。@ (350,245)。</summary>
        public static Sprite SkillBg => An("SkillBg");
        public static Sprite EffortBtnN => An("EffortBtn1");   // 成就（未選）
        public static Sprite EffortBtnP => An("EffortBtn2");
        public static Sprite SkillBtnN => An("SkillBtn1");     // 统计明细（未選）
        public static Sprite SkillBtnP => An("SkillBtn2");

        /// <summary>進度條的填充（232×19）。官方 6 條共用同一張。</summary>
        public static Sprite BarFill => An("PlayerInformationDlg65");
    }
}
