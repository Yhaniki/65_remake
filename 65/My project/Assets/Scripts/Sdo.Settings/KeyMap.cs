using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Sdo.Settings
{
    /// <summary>遊玩中可自訂的功能鍵。<see cref="KeyMap"/> 的 [Hotkeys] 區每個 enum 值一行（key 名見
    /// <see cref="KeyMap.HotkeyIds"/>），順序＝enum 順序。加新功能鍵：這裡加一個值，
    /// <see cref="KeyMap.HotkeyIds"/>／<see cref="KeyMap.HotkeyDefaults"/>／<see cref="KeyMap.HotkeyComments"/>
    /// 各補一格（三張表等長，有單元測試守著）。</summary>
    public enum Hotkey
    {
        Camera = 0,     // 切換鏡頭：自動導播 ↔ 6 台固定鏡頭（原 F2）
        SpeedUp,        // note 加速一檔（原 F5）
        SpeedDown,      // note 減速一檔（原 F6）
        AssistTick,     // 打拍音開關（原 F7）
        AutoPlay,       // Auto 自動打擊開關（原 F8）
        Showtime,       // ShowTime 模式釋放氣條（原 Space）
        Quit,           // 中離：不結算直接退出（原 Escape）
    }

    /// <summary>
    /// 鍵位設定，存成 <c>keymaps.ini</c>，跟 config.ini 同層（存檔層 <c>DATA/PROFILE/</c>）。**全域一份**，不跟著使用者。
    /// 兩區：<c>[Lane4]</c>＝4 鍵打擊鍵位（主/輔，遊戲內 OPTION 鍵盤頁改完會寫回這裡），<c>[Hotkeys]</c>＝遊玩中的
    /// 功能鍵（原本寫死的 F2/F5/F6/F7/F8/Space/Esc，現在都能自訂位置）。
    ///
    /// 從 config.ini 拆出來的：舊檔的 <c>[Option] opt_keys / opt_keysAux</c> 在第一次開機時會被搬進這裡
    /// （<see cref="Load"/> 缺檔 → 用 <see cref="RoomConfig.optKeys"/> 種一份），之後 config.ini 不再寫鍵位。
    ///
    /// 解析/夾值/輸出都是純函式（<see cref="ParseInto"/> / <see cref="Sanitize"/> / <see cref="Serialize"/> 不碰檔案）。
    /// </summary>
    public static class KeyMap
    {
        public const string FileName = "keymaps.ini";

        // ---- 表：id（ini 的 key 名）/ 預設鍵 / 註解。三張表與 Hotkey enum 等長且同序。
        //      **必須宣告在下面那組可變欄位之前** —— C# 靜態欄位初始化依宣告順序跑，反過來的話
        //      hotkeys/_resolved 會讀到還沒初始化的 null 表，整個型別在第一次被碰到就 TypeInitializationException。----
        /// <summary>[Hotkeys] 區的 key 名，索引＝(int)<see cref="Hotkey"/>。</summary>
        public static readonly string[] HotkeyIds =
        {
            "camera", "speedUp", "speedDown", "assistTick", "autoPlay", "showtime", "quit",
        };

        /// <summary>各功能鍵的預設鍵（＝重製前寫死的那顆）。</summary>
        public static readonly KeyCode[] HotkeyDefaults =
        {
            KeyCode.F2, KeyCode.F5, KeyCode.F6, KeyCode.F7, KeyCode.F8, KeyCode.Space, KeyCode.Escape,
        };

        private static readonly string[] HotkeyComments =
        {
            "# 切換鏡頭：自動導播 ↔ 6 台固定鏡頭（切了會寫回 config.ini 的 opt_cameraFixed）",
            "# note 加速一檔 / 減速一檔（依 config.ini 的 speedSteps 檔位步進）",
            null,   // speedDown 併在 speedUp 的註解裡
            "# 打拍音：每顆音符響一聲 click，方便對拍",
            "# Auto：自動打擊所有音符",
            "# ShowTime 模式：氣條集滿後釋放",
            "# 中離：遊玩中直接退出（不結算）",
        };

        private static readonly string[] HotkeyDefaultNames = NamesOf(HotkeyDefaults);

        // ---- 當下生效的值（初始化會讀上面那三張表，故一定要排在它們後面）----
        /// <summary>4 鍵主鍵位（順序：左,下,上,右）。KeyCode 名稱；""＝該格刻意不綁。</summary>
        public static string[] lane4 = (string[])KeyBindSettings.DefaultPrimaryNames.Clone();
        /// <summary>4 鍵輔助鍵位（順序同上）。</summary>
        public static string[] lane4aux = (string[])KeyBindSettings.DefaultAuxNames.Clone();
        /// <summary>功能鍵名稱，索引＝(int)<see cref="Hotkey"/>。""／無效名＝不綁（<see cref="KeyCode.None"/>）。</summary>
        public static string[] hotkeys = (string[])HotkeyDefaultNames.Clone();

        // 每幀要問的東西不能每次 Enum.TryParse → Sanitize() 時解析一次快取起來。
        private static KeyCode[] _resolved = ResolveAll(HotkeyDefaultNames);

        public static int Count => HotkeyIds.Length;

        // ---------------- 查詢（呼叫端用的） ----------------

        /// <summary>該功能綁的鍵；沒綁 → <see cref="KeyCode.None"/>。純查表（已預先解析）。</summary>
        public static KeyCode Key(Hotkey h)
        {
            int i = (int)h;
            return (i >= 0 && i < _resolved.Length) ? _resolved[i] : KeyCode.None;
        }

        /// <summary>該功能鍵這一幀是否剛被按下。沒綁鍵（KeyCode.None）永遠 false。</summary>
        public static bool Down(Hotkey h)
        {
            var k = Key(h);
            return k != KeyCode.None && Input.GetKeyDown(k);
        }

        /// <summary>該功能鍵是否被按著。沒綁鍵永遠 false。</summary>
        public static bool Held(Hotkey h)
        {
            var k = Key(h);
            return k != KeyCode.None && Input.GetKey(k);
        }

        // ---------------- 檔案 IO ----------------

        /// <summary>keymaps.ini 的完整路徑：存檔層 DATA/PROFILE/（與 config.ini 同層）。</summary>
        public static string FilePath
        {
            get
            {
                var root = ProfileManager.Root;
                return string.IsNullOrEmpty(root) ? FileName : Path.Combine(root, FileName);
            }
        }

        /// <summary>讀 keymaps.ini。缺檔 → 從 config.ini 舊的 <c>opt_keys/opt_keysAux</c> 一次性搬進來（沒有就用預設），
        /// 並落地一份可手改的範本。**必須在 <see cref="RoomConfig.Load"/> 之後呼叫**（要拿它解析出來的舊鍵位）。</summary>
        public static void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    ParseInto(File.ReadAllText(FilePath, Encoding.UTF8));
                    Sanitize();
                }
                else
                {
                    // 舊版把 4 鍵放在 config.ini 的 [Option]（RoomConfig 仍會解析它，只是不再寫出）→ 搬過來。
                    lane4 = SplitKeys(RoomConfig.optKeys);
                    lane4aux = SplitKeys(RoomConfig.optKeysAux);
                    Sanitize();
                    Save();
                    Debug.Log($"[KeyMap] created {FilePath}");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[KeyMap] load failed, using defaults: {e.Message}");
                lane4 = (string[])KeyBindSettings.DefaultPrimaryNames.Clone();
                lane4aux = (string[])KeyBindSettings.DefaultAuxNames.Clone();
                hotkeys = (string[])HotkeyDefaultNames.Clone();
                Sanitize();
            }
        }

        /// <summary>把目前的值寫回 keymaps.ini（附中文註解）。</summary>
        public static void Save()
        {
            try
            {
                var path = FilePath;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, Serialize(), new UTF8Encoding(false));
            }
            catch (Exception e) { Debug.LogError($"[KeyMap] save failed: {e.Message}"); }
        }

        // ---------------- 純函式（可單元測試） ----------------

        /// <summary>把一份 INI 文字解析進靜態欄位（不碰檔案）。未出現的 key 保留原值。</summary>
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

                if (key == "primary") { lane4 = SplitKeys(val); continue; }
                if (key == "aux") { lane4aux = SplitKeys(val); continue; }
                for (int i = 0; i < HotkeyIds.Length; i++)
                    if (string.Equals(key, HotkeyIds[i], StringComparison.OrdinalIgnoreCase)) { SetName(i, val); break; }
            }
        }

        /// <summary>夾正非法值（陣列補齊/無效名回預設；""＝刻意不綁，原樣保留）並重建 KeyCode 快取。</summary>
        public static void Sanitize()
        {
            lane4 = KeyBindSettings.SanitizeNames(lane4, KeyBindSettings.DefaultPrimary);
            lane4aux = KeyBindSettings.SanitizeNames(lane4aux, KeyBindSettings.DefaultAux);

            var res = new string[HotkeyIds.Length];
            for (int i = 0; i < res.Length; i++)
            {
                var v = (hotkeys != null && i < hotkeys.Length) ? hotkeys[i] : null;
                // ""（刻意停用該功能）原樣保留；null/缺欄位/無效名 → 回該功能預設鍵。
                if (v != null && v.Length == 0) res[i] = "";
                else res[i] = (!string.IsNullOrEmpty(v) && Enum.TryParse<KeyCode>(v, out _)) ? v : HotkeyDefaults[i].ToString();
            }
            hotkeys = res;
            _resolved = ResolveAll(hotkeys);
        }

        /// <summary>輸出帶註解的 INI 文字（純函式）。</summary>
        public static string Serialize()
        {
            var sb = new StringBuilder();
            sb.Append("# 鍵位設定 — 放在存檔資料夾 DATA/PROFILE/（與 config.ini 同層），純文字可手改。\n");
            sb.Append("# 值＝Unity KeyCode 名稱：A…Z / F1…F15 / Alpha0…Alpha9 / Keypad0…Keypad9 /\n");
            sb.Append("#   LeftArrow RightArrow UpArrow DownArrow / Space Escape Return Tab / LeftShift LeftControl /\n");
            sb.Append("#   Comma Period Slash Semicolon Quote LeftBracket RightBracket / Home End PageUp PageDown …\n");
            sb.Append("# 留空（key= 後面不寫）＝該格/該功能不綁鍵。改完存檔，下次開遊戲生效。\n");

            sb.Append('\n').Append("[Lane4]\n");
            sb.Append("# 4 鍵打擊鍵位，順序＝左,下,上,右。主鍵位 / 輔助鍵位兩排都會判定。\n");
            sb.Append("# 遊戲內 OPTION →「鍵盤」頁也能改，按保存會寫回這裡。\n");
            sb.Append("primary=").Append(JoinKeys(lane4)).Append('\n');
            sb.Append("aux=").Append(JoinKeys(lane4aux)).Append('\n');

            sb.Append('\n').Append("[Hotkeys]\n");
            sb.Append("# 遊玩中的功能鍵。跟上面的打擊鍵位別綁同一顆，否則打歌時會誤觸。\n");
            for (int i = 0; i < HotkeyIds.Length; i++)
            {
                var c = i < HotkeyComments.Length ? HotkeyComments[i] : null;
                if (!string.IsNullOrEmpty(c)) sb.Append(c).Append('\n');
                sb.Append(HotkeyIds[i]).Append('=').Append(NameAt(i)).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>把 4 鍵鍵位抓進 <see cref="lane4"/>／<see cref="lane4aux"/>（OPTION 鍵盤頁按保存時用）。不碰檔案。</summary>
        public static void CaptureFrom(KeyBindSettings k)
        {
            if (k == null) return;
            lane4 = KeyBindSettings.SanitizeNames(k.lane4, KeyBindSettings.DefaultPrimary);
            lane4aux = KeyBindSettings.SanitizeNames(k.lane4aux, KeyBindSettings.DefaultAux);
            Sanitize();
        }

        /// <summary>把 4 鍵鍵位套回 <see cref="GameSettings"/> 的工作副本（開機/讀檔後用）。不碰檔案。</summary>
        public static void ApplyTo(KeyBindSettings k)
        {
            if (k == null) return;
            k.lane4 = KeyBindSettings.SanitizeNames(lane4, KeyBindSettings.DefaultPrimary);
            k.lane4aux = KeyBindSettings.SanitizeNames(lane4aux, KeyBindSettings.DefaultAux);
        }

        /// <summary>把某個功能鍵改綁到指定鍵（null/None → 清空不綁）。純函式，改完 <see cref="Save"/> 才落地。</summary>
        public static void Bind(Hotkey h, KeyCode k)
        {
            SetName((int)h, k == KeyCode.None ? "" : k.ToString());
            Sanitize();
        }

        /// <summary>目前所有「打擊鍵位 vs 功能鍵」的衝突（同一顆鍵綁在兩邊）。回傳衝突的鍵，供 UI/測試檢查。純函式。</summary>
        public static List<KeyCode> LaneHotkeyConflicts()
        {
            var lanes = new HashSet<KeyCode>();
            foreach (var n in lane4) AddKey(lanes, n);
            foreach (var n in lane4aux) AddKey(lanes, n);
            var res = new List<KeyCode>();
            foreach (var k in _resolved)
                if (k != KeyCode.None && lanes.Contains(k) && !res.Contains(k)) res.Add(k);
            return res;
        }

        // ---------------- helpers ----------------

        private static void AddKey(HashSet<KeyCode> set, string name)
        {
            if (!string.IsNullOrEmpty(name) && Enum.TryParse<KeyCode>(name, out var k) && k != KeyCode.None) set.Add(k);
        }

        private static void SetName(int i, string name)
        {
            if (i < 0 || i >= HotkeyIds.Length) return;
            if (hotkeys == null || hotkeys.Length != HotkeyIds.Length)
            {
                var grown = new string[HotkeyIds.Length];
                for (int j = 0; j < grown.Length; j++)
                    grown[j] = (hotkeys != null && j < hotkeys.Length) ? hotkeys[j] : HotkeyDefaultNames[j];
                hotkeys = grown;
            }
            hotkeys[i] = name ?? "";
        }

        private static string NameAt(int i) => (hotkeys != null && i < hotkeys.Length) ? (hotkeys[i] ?? "") : "";

        private static KeyCode[] ResolveAll(string[] names)
        {
            var res = new KeyCode[HotkeyIds.Length];
            for (int i = 0; i < res.Length; i++)
            {
                var v = (names != null && i < names.Length) ? names[i] : null;
                res[i] = (!string.IsNullOrEmpty(v) && Enum.TryParse<KeyCode>(v, out var k)) ? k : KeyCode.None;
            }
            return res;
        }

        private static string[] NamesOf(KeyCode[] keys)
        {
            var res = new string[keys.Length];
            for (int i = 0; i < keys.Length; i++) res[i] = keys[i].ToString();
            return res;
        }

        private static string JoinKeys(string[] a)
        {
            if (a == null) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < a.Length; i++) { if (i > 0) sb.Append(','); sb.Append(a[i] ?? ""); }
            return sb.ToString();
        }

        // 逗號切 4 格（""＝刻意清空，SanitizeNames 會原樣保留）。
        private static string[] SplitKeys(string s)
        {
            var parts = (s ?? "").Split(',');
            var res = new string[4];
            for (int i = 0; i < 4; i++) res[i] = i < parts.Length ? parts[i].Trim() : "";
            return res;
        }
    }
}
