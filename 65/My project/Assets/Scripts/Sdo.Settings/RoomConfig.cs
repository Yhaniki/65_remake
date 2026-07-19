using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Sdo.Settings
{
    /// <summary>
    /// 開房間右側面板的可選清單與預設值，存成 <c>config.ini</c>。全帳號共用 —— 放在 PROFILE 根
    /// （DATA/PROFILE/config.ini，與 settings.json / active.txt 同層，見 <see cref="ProfileManager"/>）；
    /// 舊的 per-user（DATA/PROFILE/&lt;id&gt;/）與更早的 exe 同層 config.ini 會一次性遷移進來。
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

        // 額外歌曲資料夾（osu / StepMania）：分號(;)分隔的絕對路徑（逗號仍相容），每個路徑都當成一個 Songs 根目錄（底下第一層=
        // 分類 group，再一層=各首歌的資料夾），語意同 StepMania 的 AdditionalSongFolders。預設的 <ADDON>/SONG 一律自動掃描，
        // 不需列在這（舊的 exe 同層 Songs/ 也仍相容）。
        public static string[] additionalSongFolders = new string[0];

        // 外掛(ADDON)根目錄覆蓋：預設空 = DATA/ADDON（即 Root/ADDON）。想把整包外掛（SONG/NOTESKIN/THEME/MODEL）放到別的
        // 資料夾（例如另一顆硬碟 D:/SdoAddon）就填一個絕對路徑；該資料夾底下就是 SONG 等子夾。見 SdoExtracted.AddonDir。
        public static string addonFolder = "";

        // ---- OPTION 對話框設定的鏡像（存進同一份共用 config.ini 的 [Option] 區）。使用者要求 OPTION 設定也落地
        //      config.ini（放 DATA/PROFILE/）。裝置層的 settings.json 仍是執行期讀取的工作副本；這裡是共用的
        //      覆蓋值：開機 Load() 後把有帶 [Option] 的值套回 GameSettings（ApplyOptionTo），Apply 時再抓回來存
        //      （CaptureOptionFrom + Save）。見 OptionDlgModal.Apply / SettingsBootstrap。----
        public static bool hasOption = false;   // 解析到的 config.ini 是否帶 [Option] 區（帶了才覆蓋 settings.json 的裝置層值）
        public static float optBgm = 0.5f, optMusic = 0.5f, optSfx = 0.5f;
        public static string optKeys = "A,S,W,D";
        public static string optKeysAux = "LeftArrow,DownArrow,UpArrow,RightArrow";
        public static int optDispW = 1024, optDispH = 768, optVsync = 1;
        public static string optDispMode = "Borderless";
        public static string optLang = "zh-TW";
        public static bool optFullscreenFill = false, optBloom = true, optNotesPanelLeft = true,
                           optEffectChar = true, optEffectScene = true, optCameraAuto = true, optCallCard = true,
                           optPlayFullSong = false, optSongSpeed = true, optCollapseShortHolds = true;
        public static float optPanelOpacity = 1.4f;

        public const string FileName = "config.ini";

        /// <summary>config.ini 的完整路徑：全帳號共用 —— <c>DATA/PROFILE/config.ini</c>（與 settings.json / active.txt 同層）。
        /// 房間預設、OPTION、外部歌曲資料夾都屬「本機」層級、不綁單一角色，故放 PROFILE 根而非 per-user 子資料夾。
        /// 需在 <see cref="ProfileManager.Boot"/> 之後讀（Root 那時才確定）。</summary>
        public static string FilePath => Path.Combine(ProfileManager.Root, FileName);

        /// <summary>前一版的 per-user 位置（<c>DATA/PROFILE/&lt;active id&gt;/config.ini</c>）。保留供一次性遷移；
        /// Boot() 前 ActiveDir 為空 → 回傳空字串（該來源略過）。</summary>
        public static string LegacyPerUserFilePath
        {
            get
            {
                var dir = ProfileManager.ActiveDir;
                return string.IsNullOrEmpty(dir) ? "" : Path.Combine(dir, FileName);
            }
        }

        /// <summary>更早的位置：執行檔同一層（Editor 下 = 專案根「My project/」）。保留供一次性遷移與 fallback。</summary>
        public static string LegacyFilePath
        {
            get
            {
                // 建置版 Application.dataPath = "<exe 同層>/<Product>_Data" → 其上一層就是 exe 所在資料夾。
                // Editor 下 dataPath = ".../My project/Assets" → 上一層 = "My project"（開發放這方便）。
                string dir;
                try { dir = Directory.GetParent(Application.dataPath).FullName; }
                catch { dir = Application.dataPath; }
                return Path.Combine(dir, FileName);
            }
        }

        /// <summary>讀 config.ini（全帳號共用，不存在就用內建預設並寫一份範本）。在 <see cref="ProfileManager.Boot"/>
        /// 之後呼叫。首次會把舊位置（前一版 per-user、更早的 exe 同層）的 config.ini 一次性遷移進來。</summary>
        public static void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    ParseInto(File.ReadAllText(FilePath));
                    Sanitize();
                }
                else
                {
                    // 首次 / 從舊版升級：把舊位置的 config.ini 讀進來，寫成共用的一份（不刪原檔）。
                    // 先找前一版的 per-user（DATA/PROFILE/<id>/），再找更早的 exe 同層檔。
                    string legacy = FirstExisting(LegacyPerUserFilePath, LegacyFilePath);
                    if (legacy != null)
                    {
                        ParseInto(File.ReadAllText(legacy));
                        Sanitize();
                        if (!hasOption) CaptureOptionFrom(DisplaySettingsManager.Settings);   // 舊檔無 [Option] → 補目前裝置層值
                    }
                    else
                    {
                        Sanitize();
                        CaptureOptionFrom(DisplaySettingsManager.Settings);   // 第一次：範本的 [Option] 反映目前 settings.json 值
                    }
                    Save();   // 落地一份共用的可編輯範本 DATA/PROFILE/config.ini
                }
                // config.ini 帶了 [Option] → 用共用值覆蓋 settings.json 的裝置層 GameSettings（見 SettingsBootstrap 隨後 ApplyDisplay）。
                if (hasOption) ApplyOptionTo(DisplaySettingsManager.Settings);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RoomConfig] load failed, using defaults: {e.Message}");
                Sanitize();
            }
        }

        // 回傳第一個存在的檔案路徑（空/null/不存在都略過）；供 Load 的一次性遷移挑舊來源。
        private static string FirstExisting(params string[] paths)
        {
            if (paths == null) return null;
            foreach (var p in paths)
                try { if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p; } catch { }
            return null;
        }

        /// <summary>把目前的值寫回 config.ini（附中文註解）。</summary>
        public static void Save()
        {
            try
            {
                File.WriteAllText(FilePath, Serialize(), new UTF8Encoding(false));
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
                    case "AdditionalSongFolders": additionalSongFolders = ParseStringList(val); break;
                    case "AddonFolder": addonFolder = NormalizeFolder(val); break;
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
                    case "opt_callCardInGame": optCallCard = ParseBool(val, optCallCard); break;
                    case "opt_playFullSong": optPlayFullSong = ParseBool(val, optPlayFullSong); break;
                    case "opt_songSpeed": optSongSpeed = ParseBool(val, optSongSpeed); break;
                    case "opt_collapseShortHolds": optCollapseShortHolds = ParseBool(val, optCollapseShortHolds); break;
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
            if (additionalSongFolders == null) additionalSongFolders = new string[0];
            if (addonFolder == null) addonFolder = "";
        }

        /// <summary>輸出帶註解的 INI 文字（純函式）。</summary>
        public static string Serialize()
        {
            var sb = new StringBuilder();
            sb.Append("# 開房間右側面板預設設定 — 放在 DATA/PROFILE/（全帳號共用），純文字可手改。\n");
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
            sb.Append("# 額外歌曲資料夾（osu/StepMania），仿 StepMania：分號分隔多個絕對路徑，例如 D:/test;E:/songs。\n");
            sb.Append("# 每個路徑都當成一個 Songs 根：底下第一層=分類(group)，再下一層=各首歌資料夾。\n");
            sb.Append("# 預設的 <ADDON>/SONG 一律自動掃描（舊的 exe 同層 Songs/ 仍相容），不需列在這。\n");
            sb.Append("AdditionalSongFolders=").Append(StringListToString(additionalSongFolders)).Append('\n');
            sb.Append("# 外掛(ADDON)根目錄：預設空=DATA/ADDON。想把整包外掛（SONG/NOTESKIN/THEME/MODEL）放別處就填絕對路徑，\n");
            sb.Append("# 例如 AddonFolder=D:/SdoAddon（該資料夾底下就是 SONG 等子夾）。\n");
            sb.Append("AddonFolder=").Append(addonFolder ?? "").Append('\n');

            // OPTION 對話框（畫面/音效/鍵盤/遊戲）的共用設定。改完在遊戲內 OPTION 按「保存」也會寫回這裡。
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
            sb.Append("opt_callCardInGame=").Append(B(optCallCard)).Append('\n');
            sb.Append("opt_playFullSong=").Append(B(optPlayFullSong)).Append('\n');
            sb.Append("opt_songSpeed=").Append(B(optSongSpeed)).Append('\n');
            sb.Append("# 無理短長條收成一般 note（短於 180BPM 16 分音符 ≈83ms 的 long note → note；1=開 預設 0=關）\n");
            sb.Append("opt_collapseShortHolds=").Append(B(optCollapseShortHolds)).Append('\n');
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
                optCallCard = g.callCardInGame; optPlayFullSong = g.playFullSong; optSongSpeed = g.songSpeed;
                optCollapseShortHolds = g.collapseShortHolds;
                optPanelOpacity = g.panelOpacity;
            }
            hasOption = true;
        }

        /// <summary>把 config.ini 的 OPTION 鏡像值套回 <see cref="GameSettings"/>（共用值覆蓋裝置層）。開機
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
            g.callCardInGame = optCallCard; g.playFullSong = optPlayFullSong; g.songSpeed = optSongSpeed;
            g.collapseShortHolds = optCollapseShortHolds;
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

        /// <summary>Split a path list into folders — StepMania-style, semicolon-separated (<c>;</c>); comma is still
        /// accepted for back-compat. Each entry is trimmed, backslashes normalised to '/', empties dropped, and leading
        /// slashes in front of a Windows drive letter stripped (a config like <c>////D:/Songs</c> resolves to
        /// <c>D:/Songs</c>). Pure/testable — mirrors <see cref="ParseFloatList"/> for the AdditionalSongFolders key.</summary>
        public static string[] ParseStringList(string s)
        {
            if (string.IsNullOrEmpty(s)) return new string[0];
            var parts = s.Split(';', ',');   // '; ' preferred (a folder name can carry a ','), ',' kept for old configs
            var list = new System.Collections.Generic.List<string>(parts.Length);
            foreach (var p in parts)
            {
                var t = NormalizeFolder(p);
                if (t.Length > 0) list.Add(t);
            }
            return list.ToArray();
        }

        /// <summary>Clean one folder entry: trim, backslashes→'/', and strip leading slashes sitting in front of a
        /// Windows drive letter (so a StepMania-style <c>////D:/Songs</c> becomes <c>D:/Songs</c>). A UNC path
        /// (<c>//server/share</c>) is left untouched — its second segment isn't a <c>X:</c> drive.</summary>
        private static string NormalizeFolder(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            var t = p.Trim().Replace('\\', '/');
            int i = 0;
            while (i < t.Length && t[i] == '/') i++;
            if (i > 0 && i + 1 < t.Length && char.IsLetter(t[i]) && t[i + 1] == ':') t = t.Substring(i);
            return t.Trim();
        }

        private static string StringListToString(string[] a)
        {
            if (a == null || a.Length == 0) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < a.Length; i++) { if (i > 0) sb.Append(';'); sb.Append(a[i] ?? ""); }
            return sb.ToString();
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
