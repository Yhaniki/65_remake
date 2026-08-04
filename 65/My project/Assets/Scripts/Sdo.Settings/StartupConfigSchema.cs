using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Sdo.Settings
{
    /// <summary>一列設定的呈現方式。</summary>
    public enum ConfigFieldKind
    {
        Toggle,   // 開/關
        Slider,   // 數值（Min..Max，Step>0 時吸附到整數格）
        Text,     // 自由文字（位址/路徑/清單）
        Choice,   // 固定幾個選項循環切換
    }

    /// <summary>
    /// 開場設定面板（<c>StartupConfigPanel</c>）裡的一列。值一律以**字串**進出（config.ini 本來就是文字），
    /// 所以整張表可以被單元測試逐列 round-trip 檢查，不需要 UI。
    /// </summary>
    public sealed class ConfigField
    {
        /// <summary>對應 config.ini 的 key（測試用它比對「有沒有漏掉哪個沒 UI 的設定」）。</summary>
        public string Key;
        /// <summary>分頁標題（＝<see cref="StartupConfigSchema.Categories"/> 其中之一）。</summary>
        public string Category;
        public string Label;
        /// <summary>面板底下那條說明列的內容（滑鼠移到該列時顯示）。</summary>
        public string Help;
        public ConfigFieldKind Kind;
        public float Min, Max;
        /// <summary>Slider 專用：>0 表示吸附到這個間距（1 = 只取整數）。</summary>
        public float Step;
        /// <summary>Slider 專用：數值後面那個單位（"ms"/"px"/"×"；空＝不加）。</summary>
        public string Unit;
        /// <summary>Slider 專用：值只能拖、不能打字，改用 <see cref="Format"/> 畫成文字（判定精度＝「精4」「JUSTICE」，
        /// 打字沒有意義）。其餘滑桿右邊都是可以直接輸入數字的欄位。</summary>
        public bool NoValueEntry;
        /// <summary>Slider 專用：<see cref="NoValueEntry"/> 時把數值畫成人看得懂的字。</summary>
        public Func<float, string> Format;
        /// <summary>Choice 專用：可選值（存進 config.ini 的原字串）。</summary>
        public string[] Choices;
        /// <summary>Choice 專用：對應 <see cref="Choices"/> 的顯示名稱（null = 直接顯示原字串）。</summary>
        public string[] ChoiceLabels;
        /// <summary>密碼/token：預設遮起來顯示。</summary>
        public bool Secret;

        public Func<string> Get;
        public Action<string> Set;

        /// <summary>目前值當成數字讀（Slider 用；讀不出來 → <see cref="Min"/>）。</summary>
        public float GetNumber()
            => float.TryParse(Get?.Invoke(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : Min;

        /// <summary>寫入數字（先夾範圍、再依 <see cref="Step"/> 吸附）。</summary>
        public void SetNumber(float v) => Set?.Invoke(NumberToText(SnapNumber(v)));

        /// <summary>夾範圍 + 吸附格點。純函式。</summary>
        public float SnapNumber(float v)
        {
            v = Mathf.Clamp(v, Min, Max);
            if (Step > 0f) v = Mathf.Round(v / Step) * Step;
            return v;
        }

        /// <summary>目前值當成開關讀（"1"/"true"/"on" 皆為開）。</summary>
        public bool GetBool() => StartupConfigSchema.ParseBool(Get?.Invoke());

        public void SetBool(bool on) => Set?.Invoke(on ? "1" : "0");

        /// <summary>Choice：目前值在 <see cref="Choices"/> 裡的索引（找不到 → 0）。</summary>
        public int GetChoiceIndex()
        {
            if (Choices == null || Choices.Length == 0) return 0;
            var cur = (Get?.Invoke() ?? "").Trim();
            for (int i = 0; i < Choices.Length; i++)
                if (string.Equals(Choices[i], cur, StringComparison.OrdinalIgnoreCase)) return i;
            return 0;
        }

        /// <summary>Choice：往後推 <paramref name="delta"/> 格（循環）。</summary>
        public void StepChoice(int delta)
        {
            if (Choices == null || Choices.Length == 0) return;
            int n = Choices.Length;
            int i = ((GetChoiceIndex() + delta) % n + n) % n;
            Set?.Invoke(Choices[i]);
        }

        /// <summary>Choice：目前值的顯示名稱。</summary>
        public string ChoiceText()
        {
            int i = GetChoiceIndex();
            if (ChoiceLabels != null && i < ChoiceLabels.Length) return ChoiceLabels[i];
            return Choices != null && i < Choices.Length ? Choices[i] : "";
        }

        /// <summary>Slider：目前值的顯示字串（不能打字的那種才用 <see cref="Format"/>）。</summary>
        public string NumberText()
        {
            float v = GetNumber();
            return NoValueEntry && Format != null ? Format(v) : NumberToText(v);
        }

        /// <summary>數值 → 欄位裡顯示的純數字字串（整數不帶小數點，其餘最多兩位）。純函式。</summary>
        public static string NumberToText(float v)
            => Mathf.Approximately(v, Mathf.Round(v))
                ? Mathf.RoundToInt(v).ToString(CultureInfo.InvariantCulture)
                : v.ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 開場（選性別）畫面那塊設定面板的內容表：**config.ini 裡「遊戲內沒有任何 UI 可以改」的設定全集**。
    ///
    /// 已經有 UI 的不放進來，避免同一個值有兩個入口：
    ///   * OPTION 對話框（<c>OptionDlgModal</c>）：音量三項、視窗大小/顯示模式/垂直同步/語言、遊戲頁七項、
    ///     完奏模式/歌曲變速/歌曲炸彈/面板透明度。
    ///   * 房間右側面板：<c>defaultSpeed / defaultNoteType / defaultTeam / defaultDropDirection / defaultGameMode</c>；
    ///     選歌畫面：<c>defaultScene</c>。
    ///   * <c>opt_cameraFixed</c> 不是給人調的（遊戲中 F2 切鏡頭時自己寫回）。
    /// StartupConfigSchemaTests 會拿 <see cref="RoomConfig.Serialize"/> 的 key 全集對這份表做覆蓋率檢查，
    /// 之後 config.ini 新增 key 卻忘了接 UI 就會紅。
    ///
    /// 每列都是字串進出 → 整張表不依賴 Unity UI，可單元測試。UI 在 <c>Sdo.UI.Screens.StartupConfigPanel</c>。
    /// </summary>
    public static class StartupConfigSchema
    {
        public const string CatNet = "連線";
        public const string CatPlay = "遊玩";
        public const string CatSong = "歌曲";
        public const string CatText = "顯示";

        /// <summary>分頁順序（＝面板上那排 tab 由左到右）。</summary>
        public static readonly string[] Categories = { CatNet, CatPlay, CatSong, CatText };

        private static List<ConfigField> _fields;

        public static IReadOnlyList<ConfigField> Fields => _fields ??= Build();

        /// <summary>某個分頁底下的列（順序＝<see cref="Build"/> 的宣告順序）。</summary>
        public static List<ConfigField> InCategory(string category)
        {
            var res = new List<ConfigField>();
            foreach (var f in Fields) if (f.Category == category) res.Add(f);
            return res;
        }

        /// <summary>依 config.ini 的 key 找一列（找不到 → null）。測試/除錯用。</summary>
        public static ConfigField ByKey(string key)
        {
            foreach (var f in Fields) if (string.Equals(f.Key, key, StringComparison.Ordinal)) return f;
            return null;
        }

        // ---- 「已經有別的 UI 可以改」的 key：不進這張表，但覆蓋率測試要知道它們是刻意排除的 ----
        /// <summary>OPTION 對話框 / 房間面板 / 選歌畫面已經有入口的 config.ini key。</summary>
        public static readonly string[] CoveredElsewhere =
        {
            // OPTION：音效頁
            "opt_bgm", "opt_music", "opt_sfx",
            // OPTION：進階頁（顯示）
            "opt_dispW", "opt_dispH", "opt_dispMode", "opt_vsync", "opt_lang",
            // OPTION：遊戲頁
            "opt_fullscreenFill", "opt_bloom", "opt_notesPanelLeft", "opt_effectCharacter", "opt_effectScene",
            "opt_cameraAuto", "opt_callCardInGame", "opt_panelOpacity",
            // OPTION：進階頁（玩法）
            "opt_playFullSong", "opt_songSpeed", "opt_songBombs",
            // 不是給人調的：遊戲中按 F2 切鏡頭時自己寫回
            "opt_cameraFixed",
            // 房間右側面板 / 選歌畫面
            "defaultSpeed", "defaultNoteType", "defaultTeam", "defaultDropDirection", "defaultGameMode", "defaultScene",
        };

        private static List<ConfigField> Build()
        {
            var f = new List<ConfigField>();

            // ---------------------------------------------------------------- 連線 [Net]
            f.Add(new ConfigField
            {
                Key = "serverAddress", Category = CatNet, Label = "伺服器位址", Kind = ConfigFieldKind.Text,
                Help = "IP 或主機名（例：192.168.1.10 / dance.example.com）。★留空＝純單機，填了按登入才會去連。",
                Get = () => RoomConfig.serverAddress ?? "", Set = v => RoomConfig.serverAddress = (v ?? "").Trim(),
            });
            f.Add(new ConfigField
            {
                Key = "serverPort", Category = CatNet, Label = "連線埠", Kind = ConfigFieldKind.Text,
                Help = "伺服器 port（1~65535，預設 27015）。要與 server 啟動時的 --port 一致。",
                Get = () => RoomConfig.serverPort.ToString(CultureInfo.InvariantCulture),
                Set = v => RoomConfig.serverPort = ParseInt(v, RoomConfig.serverPort),
            });
            f.Add(new ConfigField
            {
                Key = "serverPassword", Category = CatNet, Label = "進站密碼", Kind = ConfigFieldKind.Text, Secret = true,
                Help = "要與 server 的 --password 一致才連得上。留空＝連到沒設密碼的 server。",
                Get = () => RoomConfig.serverPassword ?? "", Set = v => RoomConfig.serverPassword = (v ?? "").Trim(),
            });
            f.Add(new ConfigField
            {
                Key = "serverToken", Category = CatNet, Label = "身分 token", Kind = ConfigFieldKind.Text, Secret = true,
                Help = "公網伺服器用：密碼是大家共用的門，token 是「伺服器認得的你」。留空＝不帶。",
                Get = () => RoomConfig.serverToken ?? "", Set = v => RoomConfig.serverToken = (v ?? "").Trim(),
            });
            f.Add(new ConfigField
            {
                Key = "serverTls", Category = CatNet, Label = "TLS 加密連線", Kind = ConfigFieldKind.Toggle,
                Help = "★開在公網一定要開：不開的話密碼、token、聊天內容全是明文。伺服器要有 --tls-cert。",
                Get = () => B(RoomConfig.serverTls), Set = v => RoomConfig.serverTls = ParseBool(v),
            });
            f.Add(new ConfigField
            {
                Key = "serverCertFingerprint", Category = CatNet, Label = "憑證指紋", Kind = ConfigFieldKind.Text,
                Help = "SHA-256（伺服器開機會印出來，冒號可留）。★自簽憑證一定要填，否則驗證必定失敗。留空＝走一般 CA 驗證。",
                Get = () => RoomConfig.serverCertFingerprint ?? "",
                Set = v => RoomConfig.serverCertFingerprint = (v ?? "").Trim(),
            });
            f.Add(new ConfigField
            {
                Key = "netAutoDownload", Category = CatNet, Label = "缺歌自動下載", Kind = ConfigFieldKind.Toggle,
                Help = "座位玩家缺歌時自動從伺服器下載（旁觀者一律不自動下載）。",
                Get = () => B(RoomConfig.netAutoDownload), Set = v => RoomConfig.netAutoDownload = ParseBool(v),
            });
            f.Add(new ConfigField
            {
                Key = "netMaxDownloadMb", Category = CatNet, Label = "下載上限 MB", Kind = ConfigFieldKind.Text,
                Help = "自動下載的單首歌大小上限（1~2048 MB）。超過就只顯示缺歌，避免在慢速網路上卡很久。",
                Get = () => RoomConfig.netMaxDownloadMb.ToString(CultureInfo.InvariantCulture),
                Set = v => RoomConfig.netMaxDownloadMb = ParseInt(v, RoomConfig.netMaxDownloadMb),
            });

            // ---------------------------------------------------------------- 遊玩 [Room]
            f.Add(new ConfigField
            {
                Key = "judgeLevel", Category = CatPlay, Label = "判定精度", Kind = ConfigFieldKind.Slider,
                Min = 1f, Max = 9f, Step = 1f, Format = JudgeLevelText, NoValueEntry = true,
                Help = "StepMania 的「精N」：數字越大越嚴格。精4＝Perfect ±45ms；精2 寬 1.33 倍、精8 只剩 0.33 倍。",
                Get = () => RoomConfig.judgeLevel.ToString(CultureInfo.InvariantCulture),
                Set = v => RoomConfig.judgeLevel = ParseInt(v, RoomConfig.judgeLevel),
            });
            f.Add(new ConfigField
            {
                Key = "globalOffsetMs", Category = CatPlay, Label = "判定 offset", Kind = ConfigFieldKind.Slider,
                Min = -300f, Max = 300f, Unit = "ms",
                Help = "正＝判定時間往後（整體打太早就往正的調）。機器的音訊延遲已自動補掉，這裡只留個人偏好；用編輯器 F2 打拍測試量。",
                Get = () => Num(RoomConfig.globalOffsetMs), Set = v => RoomConfig.globalOffsetMs = ParseFloat(v, RoomConfig.globalOffsetMs),
            });
            f.Add(new ConfigField
            {
                Key = "judgeOffsetY", Category = CatPlay, Label = "判定線位移", Kind = ConfigFieldKind.Slider,
                Min = -200f, Max = 200f, Unit = "px",
                Help = "只影響「看起來要打在哪」，不影響判定時間（那是判定 offset 的事）。0＝正中受擊線。",
                Get = () => Num(RoomConfig.judgeOffsetY), Set = v => RoomConfig.judgeOffsetY = ParseFloat(v, RoomConfig.judgeOffsetY),
            });
            f.Add(new ConfigField
            {
                Key = "scrollBaseBpm", Category = CatPlay, Label = "速度基準 BPM", Kind = ConfigFieldKind.Slider,
                Min = 30f, Max = 400f, Step = 1f,
                Help = "畫面速度 = 這個值 × 速度檔位 × 1.6 px/s。調大＝所有歌所有檔位一起變快（預設 130）。",
                Get = () => Num(RoomConfig.scrollBaseBpm), Set = v => RoomConfig.scrollBaseBpm = ParseFloat(v, RoomConfig.scrollBaseBpm),
            });
            f.Add(new ConfigField
            {
                Key = "speedSteps", Category = CatPlay, Label = "速度檔位表", Kind = ConfigFieldKind.Text,
                Help = "房間裡「速度」可以選的檔位清單，逗號分隔（預設 1,1.5,2,2.5,3,4,5,6,8）。",
                Get = () => FloatList(RoomConfig.speedSteps),
                Set = v => { var a = ParseFloatList(v); if (a.Length > 0) RoomConfig.speedSteps = a; },
            });
            f.Add(new ConfigField
            {
                Key = "rankBasedFormation", Category = CatPlay, Label = "依名次調整站位", Kind = ConfigFieldKind.Toggle,
                Help = "多人同場時：開（預設，官方行為）＝當下第一名會滑到中央前排（鏡頭錨定的那格）。關＝整場照房間座位順序站，不換位。",
                Get = () => B(RoomConfig.rankBasedFormation), Set = v => RoomConfig.rankBasedFormation = ParseBool(v),
            });
            f.Add(new ConfigField
            {
                Key = "opt_danceIgnoreMiss", Category = CatPlay, Label = "失誤不中斷舞蹈", Kind = ConfigFieldKind.Toggle,
                Help = "開＝跳舞完全不受 combo/miss/血量影響。關（預設）＝官方玩法，斷 combo 會停舞。",
                Get = () => B(Gameplay().danceIgnoreMiss), Set = v => Gameplay().danceIgnoreMiss = ParseBool(v),
            });

            // ---------------------------------------------------------------- 歌曲 [Room]
            f.Add(new ConfigField
            {
                Key = "LoadExternalSongs", Category = CatSong, Label = "載入外部歌曲", Kind = ConfigFieldKind.Toggle,
                Help = "osu / StepMania / Malody 歌曲的總開關。關掉＝開機不掃歌資料夾、沒有載入進度畫面，只剩官方歌。",
                Get = () => B(RoomConfig.loadExternalSongs), Set = v => RoomConfig.loadExternalSongs = ParseBool(v),
            });
            f.Add(new ConfigField
            {
                Key = "AdditionalSongFolders", Category = CatSong, Label = "額外歌曲資料夾", Kind = ConfigFieldKind.Text,
                Help = "分號分隔的絕對路徑（例：D:/test;E:/songs）。每個都當一個 Songs 根：第一層＝分類，第二層＝各首歌。",
                Get = () => StringList(RoomConfig.additionalSongFolders),
                Set = v => RoomConfig.additionalSongFolders = ParseStringList(v),
            });
            f.Add(new ConfigField
            {
                Key = "AddonFolder", Category = CatSong, Label = "外掛(ADDON)目錄", Kind = ConfigFieldKind.Text,
                Help = "留空＝DATA/ADDON。想把整包外掛（SONG/NOTESKIN/THEME/MODEL）放別顆硬碟就填絕對路徑。",
                Get = () => RoomConfig.addonFolder ?? "", Set = v => RoomConfig.addonFolder = (v ?? "").Trim(),
            });
            f.Add(new ConfigField
            {
                Key = "DifficultyCalc", Category = CatSong, Label = "難度計算方式", Kind = ConfigFieldKind.Choice,
                Choices = new[] { "minacalc", "osu" },
                ChoiceLabels = new[] { "MinaCalc (MSD)", "osu! 星數" },
                Help = "只影響要自己算難度的外部譜（.gn 一律保留原難度）。選了哪套，顯示數字/隨機難度範圍/簡單普通困難分槽就全照那套。",
                Get = () => RoomConfig.difficultyCalc ?? "minacalc",
                Set = v => RoomConfig.difficultyCalc = (v ?? "").Trim().ToLowerInvariant(),
            });
            f.Add(new ConfigField
            {
                Key = "SongUiAlpha", Category = CatSong, Label = "選歌面板透明度", Kind = ConfigFieldKind.Slider,
                Min = 0f, Max = 1f,
                Help = "選歌畫面「資料夾」那個浮動分類瀏覽面板的整體不透明度（0=全透明、1=不透明，預設 0.6）。",
                Get = () => Num(RoomConfig.songUiAlpha), Set = v => RoomConfig.songUiAlpha = ParseFloat(v, RoomConfig.songUiAlpha),
            });
            f.Add(new ConfigField
            {
                Key = "opt_collapseShortHolds", Category = CatSong, Label = "極短長條轉單鍵", Kind = ConfigFieldKind.Toggle,
                Help = "短於 83ms 的 long note 直接收成單顆 note（頭尾擠在同一個判定窗＝按不出來）。只對外部轉檔譜生效。",
                Get = () => B(Gameplay().collapseShortHolds), Set = v => Gameplay().collapseShortHolds = ParseBool(v),
            });

            // ---------------------------------------------------------------- 顯示 [Room]/[Option]
            f.Add(new ConfigField
            {
                Key = "comboTextScale", Category = CatText, Label = "COMBO 字大小", Kind = ConfigFieldKind.Slider,
                Min = 0.2f, Max = 3f, Unit = "×",
                Help = "COMBO 字樣＋連段數字的整體大小比例（1.0＝官方原尺寸）。純顯示，不影響判定與分數。",
                Get = () => Num(RoomConfig.comboTextScale), Set = v => RoomConfig.comboTextScale = ParseFloat(v, RoomConfig.comboTextScale),
            });
            f.Add(new ConfigField
            {
                Key = "comboTextAlpha", Category = CatText, Label = "COMBO 字不透明度", Kind = ConfigFieldKind.Slider,
                Min = 0f, Max = 1f,
                Help = "字就疊在音符板上，淡一點才不會擋住下落中的音符（預設 60%）。0＝完全看不見。",
                Get = () => Num(RoomConfig.comboTextAlpha), Set = v => RoomConfig.comboTextAlpha = ParseFloat(v, RoomConfig.comboTextAlpha),
            });
            f.Add(new ConfigField
            {
                Key = "comboTextPop", Category = CatText, Label = "COMBO 字彈跳", Kind = ConfigFieldKind.Slider,
                Min = 1f, Max = 4f, Unit = "×",
                Help = "打中時彈到最大那一瞬間的倍率（官方 2.0＝彈到兩倍再收回，1.0＝完全不彈跳）。",
                Get = () => Num(RoomConfig.comboTextPop), Set = v => RoomConfig.comboTextPop = ParseFloat(v, RoomConfig.comboTextPop),
            });
            f.Add(new ConfigField
            {
                Key = "judgeTextScale", Category = CatText, Label = "判定字大小", Kind = ConfigFieldKind.Slider,
                Min = 0.2f, Max = 3f, Unit = "×",
                Help = "PERFECT / COOL / BAD / MISS 判定字樣的整體大小比例（1.0＝官方原尺寸）。",
                Get = () => Num(RoomConfig.judgeTextScale), Set = v => RoomConfig.judgeTextScale = ParseFloat(v, RoomConfig.judgeTextScale),
            });
            f.Add(new ConfigField
            {
                Key = "judgeTextAlpha", Category = CatText, Label = "判定字不透明度", Kind = ConfigFieldKind.Slider,
                Min = 0f, Max = 1f,
                Help = "判定字不會淡出（官方是顯示完直接消失），這個值就是它顯示期間的亮度（預設 60%）。",
                Get = () => Num(RoomConfig.judgeTextAlpha), Set = v => RoomConfig.judgeTextAlpha = ParseFloat(v, RoomConfig.judgeTextAlpha),
            });
            f.Add(new ConfigField
            {
                Key = "judgeTextPop", Category = CatText, Label = "判定字彈跳", Kind = ConfigFieldKind.Slider,
                Min = 1f, Max = 4f, Unit = "×",
                Help = "同 COMBO 字彈跳，只是判定字收回的速度是官方寫死的（比較慢），這裡只調幅度。",
                Get = () => Num(RoomConfig.judgeTextPop), Set = v => RoomConfig.judgeTextPop = ParseFloat(v, RoomConfig.judgeTextPop),
            });
            f.Add(new ConfigField
            {
                Key = "opt_uiScale", Category = CatText, Label = "UI 縮放", Kind = ConfigFieldKind.Slider,
                Min = 0.5f, Max = 3f, Unit = "×",
                Help = "⚠ 目前遊戲還沒有任何地方讀這個值（畫面一律走 800×600 4:3 取景），改了不會有變化 —— 先留著對齊設定檔。",
                Get = () => Num(Display().uiScale), Set = v => Display().uiScale = ParseFloat(v, Display().uiScale),
            });

            return f;
        }

        // ---------------------------------------------------------------- 套用 / 存檔
        /// <summary>把面板上改好的值夾正並寫回 config.ini（<c>[Net]</c>/<c>[Room]</c> 直接寫、三個
        /// <c>opt_*</c> 走 <see cref="DisplaySettingsManager"/> 的工作副本，免得下次 OPTION 按保存又被舊值蓋回去）。</summary>
        public static void ApplyAndSave()
        {
            RoomConfig.Sanitize();
            DisplaySettingsManager.Save();   // Sanitize(Settings) → CaptureOptionFrom → RoomConfig.Save() + keymaps.ini
        }

        // ---------------------------------------------------------------- 純小工具（測試會直接叫）
        /// <summary>"1"/"true"/"on"/"yes"（不分大小寫）＝開；其餘（含空字串）＝關。純函式。</summary>
        public static bool ParseBool(string s)
        {
            s = (s ?? "").Trim();
            return s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase)
                            || s.Equals("on", StringComparison.OrdinalIgnoreCase)
                            || s.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        public static int ParseInt(string s, int fallback)
            => int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        public static float ParseFloat(string s, float fallback)
            => float.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        /// <summary>逗號分隔的浮點清單 → 陣列（壞的項目略過）。純函式。</summary>
        public static float[] ParseFloatList(string s)
        {
            var parts = (s ?? "").Split(',');
            var res = new List<float>();
            foreach (var p in parts)
            {
                var t = p.Trim();
                if (t.Length == 0) continue;
                if (float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) res.Add(v);
            }
            return res.ToArray();
        }

        /// <summary>分號（逗號亦可）分隔的路徑清單 → 陣列（空項目略過）。純函式。</summary>
        public static string[] ParseStringList(string s)
        {
            var parts = (s ?? "").Split(';', ',');
            var res = new List<string>();
            foreach (var p in parts)
            {
                var t = p.Trim();
                if (t.Length > 0) res.Add(t);
            }
            return res.ToArray();
        }

        public static string FloatList(float[] a)
        {
            if (a == null || a.Length == 0) return "";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < a.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(a[i].ToString("0.###", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        public static string StringList(string[] a) => a == null || a.Length == 0 ? "" : string.Join(";", a);

        /// <summary>判定精度 1~9 的顯示名稱（9＝JUSTICE）。純函式。</summary>
        public static string JudgeLevelText(float v)
        {
            int n = Mathf.Clamp(Mathf.RoundToInt(v), 1, 9);
            return n == 9 ? "JUSTICE" : "精" + n;
        }

        private static string B(bool v) => v ? "1" : "0";
        private static string Num(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        // 三個 opt_* 要寫進「執行期工作副本」而不是 RoomConfig 鏡像：存檔走
        // DisplaySettingsManager.Save() → CaptureOptionFrom(Settings)，直接改鏡像會被工作副本蓋掉。
        private static GameplaySettings Gameplay()
        {
            var s = DisplaySettingsManager.Settings;
            return s.gameplay ??= new GameplaySettings();
        }

        private static DisplaySettings Display()
        {
            var s = DisplaySettingsManager.Settings;
            return s.display ??= new DisplaySettings();
        }
    }
}
