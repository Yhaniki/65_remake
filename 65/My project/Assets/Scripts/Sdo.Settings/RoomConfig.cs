using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Sdo.Settings
{
    /// <summary>
    /// 開房間右側面板的可選清單與預設值 + OPTION 設定，存成 <c>config.ini</c>。**全域一份**，放在存檔層
    /// <c>DATA/PROFILE/</c>（與 active.txt / settings.json / favorites.json 同層）—— 設定仍不跟著使用者跑
    /// （換帳號不會換設定），只是把檔案位置搬進 PROFILE 資料夾。開機時會把舊位置的 config.ini 一次性搬進來後移除：
    /// 舊 per-user（<c>DATA/PROFILE/&lt;id&gt;/</c>）優先，其次舊全域（執行檔同層），見 <see cref="Load"/>。
    /// 純文字、好手改：第一次跑會自動寫一份附註解的範本；之後讀檔覆蓋預設。解析/夾值是純函式可單元測試
    /// （<see cref="ParseInto"/> / <see cref="Sanitize"/> 不碰檔案）。
    /// </summary>
    public static class RoomConfig
    {
        // ---- 當下生效的值（欄位＝INI 的 key）----
        public static float[] speedSteps = { 1.0f, 1.5f, 2.0f, 2.5f, 3.0f, 4.0f, 5.0f, 6.0f, 8.0f };
        public static float defaultSpeed = 2.5f;     // 預設速度（會對齊到 speedSteps 最近檔位）。玩家在房間選了會寫回這裡
        public static int defaultNoteType = -1;      // note 種類(hit-effect)：-1=隨機(預設)；>=0=指定第幾種。玩家在房間選了會寫回這裡
        public static int defaultTeam = 3;           // 組隊：0=A,1=B,2=C,3=自由
        public static int defaultDropDirection = 0;  // 掉落方式：0=向上,1=向下,2=傾斜
        public static int defaultGameMode = 0;       // 模式：0=自由模式,1=普通模式,2=ShowTime模式
        public static int defaultScene = -1;         // 場景：-1=隨機(預設)；0..30=指定場景 id(見 StageCatalog)。玩家在選歌選了會寫回這裡
        // 判定精度：沿用 StepMania 的「精N」（1~8，9=JUSTICE）。以精4 為基準窗（Perfect 45 / Cool 90 / Bad 135 /
        // Miss 180 ms）乘上該精度的係數；精2 = ×1.33。手改這個 key 就能整組調鬆緊，見 JudgmentWindows.FromStepManiaJudge。
        public static int judgeLevel = 2;

        // 全域判定 offset（毫秒）：加在譜面時鐘上（GameplayClock.OffsetMs）。正 = 判定時間往後（適合整體打太早的人）。
        //
        // **機器的音訊延遲不歸它管，已經自動補掉了**（ScreenGameplay：DSP 混音緩衝算得出來、驅動延遲是實測寫死的
        // DriverLatencyMs、打拍音檔的前導靜音在排程時提早）。所以這裡預設 0，留給「我就是想打早/晚一點」的個人偏好，
        // 以及「別台機器的驅動延遲跟我的不一樣」的微調。
        //
        // 要調就用編輯器的「打拍測試」(F2) 量 —— **一定要用聽節拍器那個測法**。看著 note 打是拿時鐘畫出來的東西去對
        // 時鐘（自我參照），只量得到輸入延遲，永遠量不到音訊延遲。
        public static float globalOffsetMs = 0f;

        // 判定線的視覺偏移（設計 px）：完美時機的音符會落在受擊線 + 這個位移的地方（0 = 正中受擊線）。
        // 只影響「看起來要打在哪」，不影響判定時間（那是 globalOffsetMs 的事）。同樣用打拍測試調。
        public static float judgeOffsetY = 0f;

        // ---- OPTION 對話框設定的鏡像（存進同一份全域 config.ini 的 [Option] 區）。settings.json 仍是執行期讀取的
        //      工作副本；這裡是「可手改的落地檔」：開機 Load() 後把有帶 [Option] 的值套回 GameSettings（ApplyOptionTo），
        //      OPTION 按保存時再抓回來寫檔（CaptureOptionFrom + Save）。見 OptionDlgModal.Apply / SettingsBootstrap。----
        public static bool hasOption = false;   // 解析到的 config.ini 是否帶 [Option] 區（帶了才覆蓋 settings.json 的裝置層值）
        public static float optBgm = 0.5f, optMusic = 0.5f, optSfx = 0.5f;
        public static string optKeys = "A,S,W,D";
        public static string optKeysAux = "LeftArrow,DownArrow,UpArrow,RightArrow";
        public static int optDispW = 1024, optDispH = 768, optVsync = 1;
        public static string optDispMode = "Borderless";
        public static string optLang = "zh-TW";
        public static bool optFullscreenFill = false, optBloom = true, optNotesPanelLeft = true,
                           optEffectChar = true, optEffectScene = true, optCameraAuto = true, optCallCard = true,
                           optPlayFullSong = false, optSongSpeed = true;
        public static int optCameraFixed = 0;   // 固定視角用哪一台（0..5）；遊戲中 F2 切鏡頭會寫回
        public static float optPanelOpacity = 1.4f;

        public const string FileName = "config.ini";

        /// <summary>config.ini 的完整路徑：**全域一份**，放在存檔層 <c>DATA/PROFILE/</c>（＝<see cref="ProfileManager.Root"/>，
        /// 與 active.txt / settings.json 同層）。不隨 active user 改變 —— 設定不跟著使用者，只是位置在 PROFILE 資料夾。</summary>
        public static string FilePath
        {
            get
            {
                // 存檔根（含 SDO_DATA_ROOT / data_root.txt 覆寫）由 ProfileManager.Root（= SdoDataRoot.ProfileDir）決定，
                // 跟 settings.json / active.txt 同一層。理論上一定解析得到；萬一為空（極端測試情境）才退回舊的執行檔同層。
                var profileRoot = ProfileManager.Root;
                if (!string.IsNullOrEmpty(profileRoot)) return Path.Combine(profileRoot, FileName);
                return LegacyExePath;
            }
        }

        /// <summary>舊版位置：執行檔同一層（建置版＝exe 資料夾；Editor 下＝專案根「My project/」）。只用於開機時把
        /// 舊 config.ini 一次性搬進 <see cref="FilePath"/>（PROFILE），不再是實際讀寫位置。</summary>
        public static string LegacyExePath
        {
            get
            {
                // 建置版 Application.dataPath = "<exe 同層>/<Product>_Data" → 其上一層就是 exe 所在資料夾。
                // Editor 下 dataPath = ".../My project/Assets" → 上一層 = "My project"。
                string dir;
                try { dir = Directory.GetParent(Application.dataPath).FullName; }
                catch { dir = Application.dataPath; }
                return Path.Combine(dir, FileName);
            }
        }

        /// <summary>讀 config.ini（全域，放在 DATA/PROFILE/；不存在就用內建預設並寫一份範本）。開機呼叫一次即可
        /// （<see cref="SettingsBootstrap"/>）；換 active user **不需要**重讀。新位置若還沒有，會把舊位置的 config.ini
        /// 一次性搬進來後移除：舊 per-user（DATA/PROFILE/&lt;id&gt;/）優先，其次舊全域（執行檔同層）。</summary>
        public static void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    // 新位置（DATA/PROFILE/config.ini）已就緒 → 正常讀。
                    ParseInto(File.ReadAllText(FilePath));
                    Sanitize();
                }
                else
                {
                    // 新位置還沒有 → 找舊檔一次性搬進來：舊 per-user（DATA/PROFILE/<id>/config.ini）才是玩家實際在用的那份，
                    // 優先；沒有再看舊全域（執行檔同層）。搬完寫進新位置並把舊檔刪掉，之後每次開機都只走上面那條。
                    string legacy = FindProfileConfig() ?? FindLegacyExeConfig();
                    if (legacy != null)
                    {
                        ParseInto(File.ReadAllText(legacy));
                        Sanitize();
                        if (!hasOption) CaptureOptionFrom(DisplaySettingsManager.Settings);   // 舊檔無 [Option] → 補目前值
                        Save();                 // 落地到新位置 DATA/PROFILE/config.ini
                        DeleteLegacyConfigs();  // 清掉舊 per-user + 執行檔同層的舊檔（內容已寫進新位置）
                        Debug.Log($"[RoomConfig] moved legacy config.ini -> {FilePath}");
                    }
                    else
                    {
                        Sanitize();
                        CaptureOptionFrom(DisplaySettingsManager.Settings);   // 第一次：範本的 [Option] 反映目前 settings.json 值
                        Save();   // 第一次：在 DATA/PROFILE 留一份可編輯的範本
                    }
                }

                // config.ini 帶了 [Option] → 以檔案值覆蓋 settings.json 的 GameSettings（見 SettingsBootstrap 隨後 ApplyDisplay）。
                if (hasOption) ApplyOptionTo(DisplaySettingsManager.Settings);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RoomConfig] load failed, using defaults: {e.Message}");
                Sanitize();
            }
        }

        /// <summary>找一份殘留的舊 per-user config.ini：優先 active user，其次 PROFILE 下第一個找到的（只看 &lt;id&gt; 子資料夾，
        /// 不會誤抓 PROFILE 根的新全域檔）。沒有則 null。</summary>
        private static string FindProfileConfig()
        {
            try
            {
                var active = ProfileManager.ActiveDir;
                if (!string.IsNullOrEmpty(active))
                {
                    var p = Path.Combine(active, FileName);
                    if (File.Exists(p)) return p;
                }
                var root = ProfileManager.Root;
                if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
                    foreach (var dir in Directory.GetDirectories(root))   // 只列子資料夾 → PROFILE 根的 config.ini 不在其中
                    {
                        var p = Path.Combine(dir, FileName);
                        if (File.Exists(p)) return p;
                    }
            }
            catch { /* 找不到就算了，用預設 */ }
            return null;
        }

        /// <summary>找舊全域位置（執行檔同層）的 config.ini。沒有、或它其實就是新位置（極端 fallback 情形）則 null。</summary>
        private static string FindLegacyExeConfig()
        {
            try
            {
                var p = LegacyExePath;
                if (File.Exists(p) && !SamePath(p, FilePath)) return p;
            }
            catch { }
            return null;
        }

        /// <summary>移除舊位置的 config.ini（只在內容已寫進新位置後呼叫）：PROFILE/&lt;id&gt;/config.ini（per-user）+ 執行檔同層的舊全域檔。</summary>
        private static void DeleteLegacyConfigs()
        {
            try
            {
                var root = ProfileManager.Root;
                if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
                    foreach (var dir in Directory.GetDirectories(root))
                    {
                        var p = Path.Combine(dir, FileName);
                        if (File.Exists(p)) { File.Delete(p); Debug.Log($"[RoomConfig] removed per-user {p}"); }
                    }
            }
            catch (Exception e) { Debug.LogWarning($"[RoomConfig] per-user cleanup failed: {e.Message}"); }

            try
            {
                var exe = LegacyExePath;
                if (File.Exists(exe) && !SamePath(exe, FilePath)) { File.Delete(exe); Debug.Log($"[RoomConfig] removed legacy {exe}"); }
            }
            catch (Exception e) { Debug.LogWarning($"[RoomConfig] legacy cleanup failed: {e.Message}"); }
        }

        /// <summary>兩個路徑是否指同一個檔（大小寫不敏感、正規化後比較）；任一失敗保守回 false。</summary>
        private static bool SamePath(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        /// <summary>把目前的值寫回 config.ini（附中文註解）。寫在 DATA/PROFILE/ 下（開機時該資料夾已由 ProfileManager.Boot
        /// 建好，這裡再保險確保一次，供 OPTION 保存等較晚的呼叫）。</summary>
        public static void Save()
        {
            try
            {
                var path = FilePath;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, Serialize(), new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Debug.LogError($"[RoomConfig] save failed: {e.Message}");
            }
        }

        /// <summary>把一份 INI 文字解析進靜態欄位（純函式：不碰檔案）。未出現的 key 保留原值。</summary>
        public static void ParseInto(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';' || line[0] == '[') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();
                if (key.StartsWith("opt_")) hasOption = true;   // 檔案帶 [Option] → 開機時覆蓋 settings.json 的裝置層值
                switch (key)
                {
                    case "speedSteps": speedSteps = ParseFloatList(val); break;
                    case "defaultSpeed": defaultSpeed = ParseFloat(val, defaultSpeed); break;
                    case "defaultNoteType": defaultNoteType = ParseInt(val, defaultNoteType); break;
                    case "defaultTeam": defaultTeam = ParseInt(val, defaultTeam); break;
                    case "defaultDropDirection": defaultDropDirection = ParseInt(val, defaultDropDirection); break;
                    case "defaultGameMode": defaultGameMode = ParseInt(val, defaultGameMode); break;
                    case "defaultScene": defaultScene = ParseInt(val, defaultScene); break;
                    case "judgeLevel": judgeLevel = ParseInt(val, judgeLevel); break;
                    case "globalOffsetMs": globalOffsetMs = ParseFloat(val, globalOffsetMs); break;
                    case "judgeOffsetY": judgeOffsetY = ParseFloat(val, judgeOffsetY); break;
                    // ---- OPTION 對話框設定 ----
                    case "opt_bgm": optBgm = ParseFloat(val, optBgm); break;
                    case "opt_music": optMusic = ParseFloat(val, optMusic); break;
                    case "opt_sfx": optSfx = ParseFloat(val, optSfx); break;
                    case "opt_keys": optKeys = val; break;
                    case "opt_keysAux": optKeysAux = val; break;
                    case "opt_dispW": optDispW = ParseInt(val, optDispW); break;
                    case "opt_dispH": optDispH = ParseInt(val, optDispH); break;
                    case "opt_dispMode": optDispMode = val; break;
                    case "opt_vsync": optVsync = ParseInt(val, optVsync); break;
                    case "opt_lang": optLang = val; break;
                    case "opt_fullscreenFill": optFullscreenFill = ParseBool(val, optFullscreenFill); break;
                    case "opt_bloom": optBloom = ParseBool(val, optBloom); break;
                    case "opt_notesPanelLeft": optNotesPanelLeft = ParseBool(val, optNotesPanelLeft); break;
                    case "opt_effectCharacter": optEffectChar = ParseBool(val, optEffectChar); break;
                    case "opt_effectScene": optEffectScene = ParseBool(val, optEffectScene); break;
                    case "opt_cameraAuto": optCameraAuto = ParseBool(val, optCameraAuto); break;
                    case "opt_cameraFixed": optCameraFixed = ParseInt(val, optCameraFixed); break;
                    case "opt_callCardInGame": optCallCard = ParseBool(val, optCallCard); break;
                    case "opt_playFullSong": optPlayFullSong = ParseBool(val, optPlayFullSong); break;
                    case "opt_songSpeed": optSongSpeed = ParseBool(val, optSongSpeed); break;
                    case "opt_panelOpacity": optPanelOpacity = ParseFloat(val, optPanelOpacity); break;
                }
            }
        }

        /// <summary>夾正非法值（空/壞的 speedSteps 回退內建；其餘夾範圍）。純函式。</summary>
        public static void Sanitize()
        {
            if (speedSteps == null || speedSteps.Length == 0)
                speedSteps = new[] { 1.0f, 1.5f, 2.0f, 2.5f, 3.0f, 4.0f, 5.0f, 6.0f, 8.0f };
            if (defaultSpeed <= 0f) defaultSpeed = 2.5f;
            if (defaultNoteType < -1) defaultNoteType = -1;
            defaultTeam = Mathf.Clamp(defaultTeam, 0, 3);
            defaultDropDirection = Mathf.Clamp(defaultDropDirection, 0, 2);
            defaultGameMode = Mathf.Clamp(defaultGameMode, 0, 2);
            if (defaultScene < -1 || defaultScene > 30) defaultScene = -1;   // 只允許 -1(隨機) 或 0..30(可選場景 id)
            judgeLevel = Mathf.Clamp(judgeLevel, 1, 9);                      // 精1~精8、9=JUSTICE
            globalOffsetMs = Mathf.Clamp(globalOffsetMs, -300f, 300f);       // 再大就不是延遲、是打錯拍了
            judgeOffsetY = Mathf.Clamp(judgeOffsetY, -200f, 200f);           // 設計 px（畫面高 600）
        }

        /// <summary>輸出帶註解的 INI 文字（純函式）。</summary>
        public static string Serialize()
        {
            var sb = new StringBuilder();
            sb.Append("# 開房間右側面板預設設定 — 放在存檔資料夾 DATA/PROFILE/（與 settings.json 同層），純文字可手改。\n");
            sb.Append("# 改完存檔，下次開遊戲生效。\n");
            sb.Append("[Room]\n");
            sb.Append("# 速度可選清單（逗號分隔，要加/減檔位直接改）\n");
            sb.Append("speedSteps=").Append(FloatListToString(speedSteps)).Append('\n');
            sb.Append("# 預設速度（會對齊到上面最接近的檔位）。玩家在房間選了會寫回這裡\n");
            sb.Append("defaultSpeed=").Append(defaultSpeed.ToString("0.0##", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("# 預設 note 種類(hit-effect)：-1=隨機，>=0=指定第幾種\n");
            sb.Append("defaultNoteType=").Append(defaultNoteType).Append('\n');
            sb.Append("# 預設組隊：0=A 1=B 2=C 3=自由\n");
            sb.Append("defaultTeam=").Append(defaultTeam).Append('\n');
            sb.Append("# 預設掉落方式：0=向上 1=向下 2=傾斜\n");
            sb.Append("defaultDropDirection=").Append(defaultDropDirection).Append('\n');
            sb.Append("# 預設模式：0=自由模式 1=普通模式 2=ShowTime模式\n");
            sb.Append("defaultGameMode=").Append(defaultGameMode).Append('\n');
            sb.Append("# 預設場景：-1=隨機，0..30=指定場景 id（步行街=0 … 卡通公路=30）。玩家在選歌選了會寫回這裡\n");
            sb.Append("defaultScene=").Append(defaultScene).Append('\n');
            sb.Append("# 判定精度（StepMania 的「精N」）：1~8，9=JUSTICE。數字越大越嚴格。\n");
            sb.Append("# 以精4 為基準窗（Perfect ±45 / Cool ±90 / Bad ±135 / Miss ±180 ms）乘該精度係數：\n");
            sb.Append("#   精1=1.50 精2=1.33 精3=1.16 精4=1.00 精5=0.84 精6=0.66 精7=0.50 精8=0.33 JUSTICE=0.20\n");
            sb.Append("#   例：精2 → Perfect ±59.9 / Cool ±119.7 / Bad ±179.6 / Miss ±239.4 ms\n");
            sb.Append("judgeLevel=").Append(judgeLevel).Append('\n');
            sb.Append("# 全域判定 offset（毫秒）：正 = 判定時間往後（整體打太早就調正的）。預設 0。\n");
            sb.Append("# 機器的音訊延遲**已經自動補掉了**（DSP 緩衝、驅動延遲、打拍音的前導靜音）→ 這裡只留給個人偏好/跨機微調。\n");
            sb.Append("# 要調就用譜面編輯器的「打拍測試」(F2)，**聽節拍器打**（看著 note 打量不到音訊延遲），它會給建議值。\n");
            sb.Append("globalOffsetMs=").Append(globalOffsetMs.ToString("0.##", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("# 判定線視覺偏移（設計 px，畫面高 600）：完美時機的音符會落在受擊線 + 這個位移處。0 = 正中受擊線。\n");
            sb.Append("judgeOffsetY=").Append(judgeOffsetY.ToString("0.##", CultureInfo.InvariantCulture)).Append('\n');

            // OPTION 對話框（畫面/音效/鍵盤/遊戲）的 per-user 設定。改完在遊戲內 OPTION 按「保存」也會寫回這裡。
            sb.Append('\n').Append("[Option]\n");
            sb.Append("# 音量 0.0~1.0（背景音樂 / 遊戲音樂 / 遊戲音效）\n");
            sb.Append("opt_bgm=").Append(optBgm.ToString("0.0##", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("opt_music=").Append(optMusic.ToString("0.0##", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("opt_sfx=").Append(optSfx.ToString("0.0##", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("# 4 鍵鍵位（左,下,上,右）主鍵位 / 輔助鍵位（KeyCode 名稱，逗號分隔；空=該格不綁）\n");
            sb.Append("opt_keys=").Append(optKeys ?? "").Append('\n');
            sb.Append("opt_keysAux=").Append(optKeysAux ?? "").Append('\n');
            sb.Append("# 視窗大小 / 顯示模式（Windowed|Fullscreen|Borderless）/ 垂直同步（0|1）/ 語言\n");
            sb.Append("opt_dispW=").Append(optDispW).Append('\n');
            sb.Append("opt_dispH=").Append(optDispH).Append('\n');
            sb.Append("opt_dispMode=").Append(optDispMode ?? "Borderless").Append('\n');
            sb.Append("opt_vsync=").Append(optVsync).Append('\n');
            sb.Append("opt_lang=").Append(optLang ?? "zh-TW").Append('\n');
            sb.Append("# 遊戲頁（1=開 0=關）：全屏填滿 / 泛光 / notes面板靠左 / 人物特效 / 場景特效 / 自動導播 / 呼叫卡 / 完奏模式 / 歌曲變速\n");
            sb.Append("opt_fullscreenFill=").Append(B(optFullscreenFill)).Append('\n');
            sb.Append("opt_bloom=").Append(B(optBloom)).Append('\n');
            sb.Append("opt_notesPanelLeft=").Append(B(optNotesPanelLeft)).Append('\n');
            sb.Append("opt_effectCharacter=").Append(B(optEffectChar)).Append('\n');
            sb.Append("opt_effectScene=").Append(B(optEffectScene)).Append('\n');
            sb.Append("opt_cameraAuto=").Append(B(optCameraAuto)).Append('\n');
            sb.Append("# 固定視角用哪一台（0~5，＝遊戲中 F2 循環的 6 台固定鏡頭；F2 切了會寫回這裡）\n");
            sb.Append("opt_cameraFixed=").Append(optCameraFixed).Append('\n');
            sb.Append("opt_callCardInGame=").Append(B(optCallCard)).Append('\n');
            sb.Append("opt_playFullSong=").Append(B(optPlayFullSong)).Append('\n');
            sb.Append("opt_songSpeed=").Append(B(optSongSpeed)).Append('\n');
            sb.Append("# 面板透明度 0.0~1.6\n");
            sb.Append("opt_panelOpacity=").Append(optPanelOpacity.ToString("0.0##", CultureInfo.InvariantCulture)).Append('\n');
            return sb.ToString();
        }

        private static string B(bool v) => v ? "1" : "0";
        private static bool ParseBool(string s, bool fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            s = s.Trim().ToLowerInvariant();
            if (s == "1" || s == "true" || s == "yes" || s == "on") return true;
            if (s == "0" || s == "false" || s == "no" || s == "off") return false;
            return fallback;
        }

        /// <summary>把目前 <see cref="GameSettings"/>（settings.json 工作副本）的 OPTION 值抓進 RoomConfig 鏡像欄位，
        /// 供 <see cref="Save"/> 寫進 config.ini。OptionDlgModal 按「保存」時呼叫。純函式（不碰檔案）。</summary>
        public static void CaptureOptionFrom(GameSettings s)
        {
            if (s == null) return;
            if (s.audio != null) { optBgm = s.audio.bgm; optMusic = s.audio.gameMusic; optSfx = s.audio.sfx; }
            if (s.keys != null)
            {
                optKeys = JoinKeys(s.keys.lane4);
                optKeysAux = JoinKeys(s.keys.lane4aux);
            }
            if (s.display != null)
            {
                optDispW = s.display.width; optDispH = s.display.height;
                optDispMode = s.display.displayMode; optVsync = s.display.vsync ? 1 : 0;
            }
            optLang = s.language;
            if (s.gameplay != null)
            {
                var g = s.gameplay;
                optFullscreenFill = g.fullscreenFill; optBloom = g.bloom; optNotesPanelLeft = g.notesPanelLeft;
                optEffectChar = g.effectCharacter; optEffectScene = g.effectScene; optCameraAuto = g.cameraAuto;
                optCameraFixed = g.cameraFixed;
                optCallCard = g.callCardInGame; optPlayFullSong = g.playFullSong; optSongSpeed = g.songSpeed;
                optPanelOpacity = g.panelOpacity;
            }
            hasOption = true;
        }

        /// <summary>把 config.ini 的 OPTION 鏡像值套回 <see cref="GameSettings"/>（每帳號覆蓋裝置層）。開機/切帳號
        /// Load() 後呼叫（見 <see cref="Load"/>）。純函式（不碰檔案）。</summary>
        public static void ApplyOptionTo(GameSettings s)
        {
            if (s == null) return;
            if (s.audio == null) s.audio = new VolumeSettings();
            s.audio.bgm = optBgm; s.audio.gameMusic = optMusic; s.audio.sfx = optSfx;
            if (s.keys == null) s.keys = new KeyBindSettings();
            s.keys.lane4 = KeyBindSettings.SanitizeNames(SplitKeys(optKeys), KeyBindSettings.DefaultPrimary);
            s.keys.lane4aux = KeyBindSettings.SanitizeNames(SplitKeys(optKeysAux), KeyBindSettings.DefaultAux);
            if (s.display == null) s.display = new DisplaySettings();
            s.display.width = optDispW; s.display.height = optDispH;
            s.display.displayMode = optDispMode; s.display.vsync = optVsync != 0;
            if (!string.IsNullOrEmpty(optLang)) s.language = optLang;
            if (s.gameplay == null) s.gameplay = new GameplaySettings();
            var g = s.gameplay;
            g.fullscreenFill = optFullscreenFill; g.bloom = optBloom; g.notesPanelLeft = optNotesPanelLeft;
            g.effectCharacter = optEffectChar; g.effectScene = optEffectScene; g.cameraAuto = optCameraAuto;
            g.cameraFixed = optCameraFixed;
            g.callCardInGame = optCallCard; g.playFullSong = optPlayFullSong; g.songSpeed = optSongSpeed;
            g.panelOpacity = optPanelOpacity;
        }

        private static string JoinKeys(string[] a)
        {
            if (a == null) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < a.Length; i++) { if (i > 0) sb.Append(','); sb.Append(a[i] ?? ""); }
            return sb.ToString();
        }

        // 逗號切 4 格，保留空格（""=刻意清空，SanitizeNames 會原樣保留）。
        private static string[] SplitKeys(string s)
        {
            var parts = (s ?? "").Split(',');
            var res = new string[4];
            for (int i = 0; i < 4; i++) res[i] = i < parts.Length ? parts[i].Trim() : "";
            return res;
        }

        // ---- small parse helpers ----
        private static float[] ParseFloatList(string s)
        {
            var parts = s.Split(',');
            var list = new System.Collections.Generic.List<float>(parts.Length);
            foreach (var p in parts)
            {
                var t = p.Trim();
                if (t.Length == 0) continue;
                if (float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) list.Add(f);
            }
            return list.ToArray();
        }

        private static string FloatListToString(float[] a)
        {
            if (a == null || a.Length == 0) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < a.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(a[i].ToString("0.0##", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static float ParseFloat(string s, float fallback)
            => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : fallback;

        private static int ParseInt(string s, int fallback)
            => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;
    }
}
