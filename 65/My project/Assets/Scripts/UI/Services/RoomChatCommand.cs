using System;
using System.Collections.Generic;
using Sdo.Localization;

namespace Sdo.UI.Services
{
    public static class RoomChatCommand
    {
        public const int MaxExpressionId = 168;
        public const int ExpressionsPerPage = 24;

        // Room-chat action motions. The official keyword handler (CN FUN_00953630 / TW FUN_0073aa50) only resolves the
        // typed text to an action id 0-7, then plays the motion via FUN_0090ee60(category, actionId, gender) — a
        // vector-of-vectors indexed by motion category. For the standard avatar the category is 0x18 (24); the 10-entry
        // costume table (CN DAT_00b8ea10 = [24,25,25,24,25,0,17,17,0,17], indexed by avatar +0x87f) only picks an
        // alternate set for special models. Motion category 24 is loaded by Motion_LoadRestTable_004a3900 from the
        // ~-delimited .data arrays (male @VA 0x585668, female @VA 0x585988) and has EXACTLY 8 entries — one per action
        // id — confirming the 1:1 mapping. See docs/reverse-engineering/ROOM_CHAT_ACTIONS.md and [[sdo-ingame-idle-mot]].
        private const string M = "MOTION/";
        public static readonly RoomChatAction[] RoomActions =
        {
            new RoomChatAction(
                "action_0", 0, "呵呵",
                M + "WREST0058.MOT", M + "MREST0070.MOT",
                "WOMAN_0", "MAN_0",
                new[] { "呵呵", "嘻嘻", "嘿嘿", ":)", "^_^", "橫橫", "横横", "西西", "襖襖", "袄袄", "henhen", "xixi", "aoao" },
                new[] { "快點", "快点", "hurry up", "趕緊的", "赶紧的", "快快快", "速度", "趕緊", "赶紧", "準備", "准备", "走", "開", "开", "KKK", "GO", "READY" }),
            new RoomChatAction(
                "action_1", 1, "哈哈",
                M + "WREST0059.MOT", M + "MREST0071.MOT",
                "WOMAN_1", "MAN_1",
                new[] { "哈哈", "挖哈哈", "嗬嗬", "呵呵", "挖卡卡", "挖哢哢", "活活" },
                new[] { "哈哈", "挖哈哈", "嗬嗬", "呵呵", "挖咔咔", "挖哢哢" }),
            new RoomChatAction(
                "action_2", 2, "麼麼",
                M + "WREST0073.MOT", M + "MREST0083.MOT",
                "WOMAN_2", "MAN_2",
                new[] { "麼麼", "么么" },
                new[] { "麼麼", "么么" }),
            new RoomChatAction(
                "action_3", 3, "生氣",
                M + "WREST0061.MOT", M + "MREST0073.MOT",
                "WOMAN_3", "MAN_3",
                new[] { "生氣", "生气", "怒了", "發火", "发火", "討厭", "讨厌", ":(", "死開", "死开", "去死", "滾", "滚" },
                new[] { "生氣", "生气", "怒了", "發火", "发火", "討厭", "讨厌", ":(", "欠扁" }),
            new RoomChatAction(
                "action_4", 4, "你好",
                M + "WREST0062.MOT", M + "MREST0074.MOT",
                "WOMAN_4", "MAN_4",
                new[] { "HI", "你好", "hello", "哈囉", "哈啰", "好" },
                new[] { "HI", "你好", "hello", "哈嘍", "哈喽", "嘿", "嗨" }),
            new RoomChatAction(
                "action_5", 5, "再見",
                M + "WREST0063.MOT", M + "MREST0075.MOT",
                "WOMAN_5", "MAN_5",
                new[] { "再見", "再见", "拜拜", "88", "bye", "北北", "BEI" },
                new[] { "昏", "ft", "暈", "晕", "服了", "orz", "倒", "OTL", "@ @" }),
            new RoomChatAction(
                "action_6", 6, "難過",
                M + "WREST0064.MOT", M + "MREST0076.MOT",
                "WOMAN_6", "MAN_6",
                new[] { "難過", "难过", "55", "傷心", "伤心", "嗚嗚", "呜呜", "死了", "-_-||", "|||", "55555", "T_T" },
                new[] { "再見", "再见", "拜拜", "88", "bye", "走了" }),
            new RoomChatAction(
                "action_7", 7, "打",
                M + "WREST0074.MOT", M + "MREST0084.MOT",
                null, null,
                new[] { "打", "扁人", "我打", "踢", "T", "T人", "飛", "飞", "拍磚", "拍砖", "打PP", "打pp", "kick", "874" },
                new[] { "打", "扁人", "我打", "踢", "T", "T人", "飛", "飞", "拍磚", "拍砖", "打PP", "打pp", "kick", "874" }),
        };

        // Decompiled expression popup load order from sdo.bin / standalone:
        // two normal pages, 24 cells per page, read horizontally.
        public static readonly int[] MenuExpressionIds =
        {
            2, 3, 4, 13, 6, 15, 21, 5, 23, 17, 26, 25,
            11, 12, 9, 8, 1, 7, 22, 24, 14, 16, 18, 19,
            10, 20, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36,
            37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48,
        };

        private static readonly string[] SimplifiedCommands =
        {
            "/开始", "/GO", "/问候", "/是", "/否", "/?", "/!", "/歌唱", "/媚眼", "/嘘", "/心", "/小可怜",
            "/愤怒", "/流泪", "/昏倒", "/打嗝", "/打哈欠", "/催促", "/嘿嘿", "/破碎的心", "/送花", "/无聊", "/有主意", "/晕",
            "/倒计时", "/点点点", "/沮丧", "/加油", "/耍帅", "/羡慕", "/再来一局", "/抱一抱", "/累", "/爱你", "/单挑", "/烦",
            "/寒", "/换歌", "/换场景", "/无奈", "/节日快乐", "/夸奖", "/路过", "/烧香", "/撞墙", "/摊煎饼", "/委屈", "/我吐",
        };

        private static readonly string[] TraditionalCommands =
        {
            "/開始", "/GO", "/問候", "/是", "/否", "/?", "/!", "/歌唱", "/媚眼", "/噓", "/心", "/小可憐",
            "/憤怒", "/流淚", "/昏倒", "/打嗝", "/打哈欠", "/催促", "/嘿嘿", "/破碎的心", "/送花", "/無聊", "/有主意", "/暈",
            "/倒計時", "/點點點", "/沮喪", "/加油", "/耍帥", "/羨慕", "/再來一局", "/抱一抱", "/累", "/愛你", "/單挑", "/煩",
            "/寒", "/換歌", "/換場景", "/無奈", "/節日快樂", "/誇獎", "/路過", "/燒香", "/撞牆", "/攤煎餅", "/委屈", "/我吐",
        };

        private static readonly string[] EnglishCommands =
        {
            "/start", "/GO", "/greet", "/yes", "/no", "/?", "/!", "/sing", "/wink", "/shh", "/heart", "/pity",
            "/angry", "/cry", "/faint", "/hiccup", "/yawn", "/hurry", "/hehe", "/brokenheart", "/flower", "/bored", "/idea", "/dizzy",
            "/countdown", "/ellipsis", "/sad", "/cheer", "/cool", "/envy", "/again", "/hug", "/tired", "/love", "/duel", "/annoyed",
            "/cold", "/song", "/scene", "/helpless", "/holiday", "/praise", "/passing", "/incense", "/wall", "/pancake", "/wronged", "/vomit",
        };

        private static readonly string[] JapaneseCommands =
        {
            "/開始", "/GO", "/挨拶", "/はい", "/いいえ", "/?", "/!", "/歌う", "/ウインク", "/シー", "/ハート", "/かわいそう",
            "/怒る", "/泣く", "/倒れる", "/しゃっくり", "/あくび", "/急いで", "/へへ", "/失恋", "/花", "/退屈", "/ひらめき", "/めまい",
            "/カウント", "/点々", "/落ち込み", "/応援", "/クール", "/羨ましい", "/もう一局", "/ハグ", "/疲れた", "/愛してる", "/勝負", "/面倒",
            "/寒い", "/曲変更", "/場面変更", "/仕方ない", "/祝日", "/褒める", "/通過", "/焼香", "/壁", "/パンケーキ", "/委屈", "/吐く",
        };

        private static readonly Dictionary<string, int> ExpressionAliases = BuildExpressionAliases();
        private static readonly Dictionary<string, RoomChatAction> FemaleRoomActionAliases = BuildRoomActionAliases(false);
        private static readonly Dictionary<string, RoomChatAction> MaleRoomActionAliases = BuildRoomActionAliases(true);
        private static readonly Dictionary<string, RoomChatAction> RoomActionById = BuildRoomActionById();

        public static int TotalExpressionPages
            => (MenuExpressionIds.Length + ExpressionsPerPage - 1) / ExpressionsPerPage;

        public static int ExpressionAtMenuSlot(int page, int slot)
        {
            int index = page * ExpressionsPerPage + slot;
            return index >= 0 && index < MenuExpressionIds.Length ? MenuExpressionIds[index] : 0;
        }

        public static bool TryParseExpression(string text, out int expressionId)
            => TryParseExpression(text, out expressionId, out _);

        /// <summary>
        /// Parse 表情指令。不一定要在開頭：掃第一個能認的 `/…`，前面任意字 + 指令後尾隨字合併成 trailing（trim，可空）。
        /// 例：`/無聊`、`/無聊 嗨`、`哈囉 /無聊`、`前綴/開始 衝`。
        /// </summary>
        public static bool TryParseExpression(string text, out int expressionId, out string trailingText)
        {
            if (TryParseExpression(text, out expressionId, out var leading, out trailingText))
            {
                trailingText = CombineExpressionText(leading, trailingText);
                return true;
            }
            trailingText = "";
            return false;
        }

        /// <summary>
        /// 同上，但把「指令前的字」與「指令後的字」分開回傳，保留使用者輸入時 emoji 的位置：
        /// 顯示時應排成 leadingText 〔emoji〕 trailingText。
        /// 例：`有人一起跳嗎 /GO` → leading="有人一起跳嗎", trailing=""；`/GO 衝` → leading="", trailing="衝"；
        /// `說話 /無聊 嗨` → leading="說話", trailing="嗨"。
        /// </summary>
        public static bool TryParseExpression(string text, out int expressionId, out string leadingText, out string trailingText)
        {
            expressionId = 0;
            leadingText = "";
            trailingText = "";
            if (string.IsNullOrWhiteSpace(text)) return false;

            string s = text.Trim();
            // 有 `/` 就從該處往後掃；前面文字算顯示字（不必 `/` 在行首）。
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '/') continue;
                string leading = i > 0 ? s.Substring(0, i).Trim() : "";
                if (!TryParseExpressionFromSlash(s.Substring(i), out expressionId, out var afterCmd))
                    continue;
                leadingText = leading;
                trailingText = afterCmd != null ? afterCmd.Trim() : "";
                return true;
            }
            return false;
        }

        /// <summary>s 必須以 `/` 開頭；只解析這一段指令。</summary>
        private static bool TryParseExpressionFromSlash(string s, out int expressionId, out string trailingText)
        {
            expressionId = 0;
            trailingText = "";
            if (string.IsNullOrEmpty(s) || s[0] != '/') return false;

            string key = s.Substring(1).Trim();
            if (key.Length == 0) return false;

            key = StripExpressionPrefix(key);
            if (key.Length == 0) return false;

            if (TryParseLeadingExpressionNumber(key, out var n, out var rest) && IsValidExpression(n))
            {
                expressionId = n;
                trailingText = rest;
                return true;
            }

            if (TryParseCompactNumber(key, "e", out n, out rest) ||
                TryParseCompactNumber(key, "exp", out n, out rest) ||
                TryParseCompactNumber(key, "emoji", out n, out rest))
            {
                if (IsValidExpression(n))
                {
                    expressionId = n;
                    trailingText = rest;
                    return true;
                }
                return false;
            }

            if (TryMatchExpressionAlias(key, out n, out rest) && IsValidExpression(n))
            {
                expressionId = n;
                trailingText = rest;
                return true;
            }

            return false;
        }

        private static string CombineExpressionText(string leading, string trailing)
        {
            leading = leading != null ? leading.Trim() : "";
            trailing = trailing != null ? trailing.Trim() : "";
            if (leading.Length == 0) return trailing;
            if (trailing.Length == 0) return leading;
            return leading + " " + trailing;
        }

        // 密語語法：以半形中括號包住對象名開頭 → `[名字] 訊息`（只認半形 []）。
        // 點聊天列的人名會把 `[名字] ` 塞進輸入框（見 RoomScreen.InsertWhisperTarget），也可手打。
        // 回傳 target=對象名（去空白）、body=中括號後的內容（去空白，可空—代表只選了對象還沒打字）。
        public static bool TryParseWhisper(string text, out string target, out string body)
        {
            target = "";
            body = "";
            if (string.IsNullOrWhiteSpace(text)) return false;

            string s = text.TrimStart();
            if (s.Length < 2 || s[0] != '[') return false;
            int close = s.IndexOf(']', 1);
            if (close <= 1) return false;   // 沒有結尾括號，或括號內是空的 → 不是密語
            string name = s.Substring(1, close - 1).Trim();
            if (name.Length == 0) return false;
            target = name;
            body = s.Substring(close + 1).Trim();
            return true;
        }

        // 家族頻道的指令字（送出時剝掉，取後面的內容當家族訊息）。CJT 詞可緊接內容（無空白），拉丁詞需字界。
        private static readonly string[] GuildCommandWords = { "家族", "公會", "公会", "guild", "family", "ギルド" };

        // 家族頻道選單自動填入的指令前綴（本地化，例：「/家族 」）。見 RoomScreen.SetChatChannel。
        public static string GuildCommandPrefix => "/" + LocalizationManager.Get("room.guild_command") + " ";

        // 家族頻道綠字行的前綴標籤（本地化，例：「<家族>」）。見 RoomScreen.AddRoomChatGuildLine。
        public static string GuildTag => LocalizationManager.Get("room.guild_tag");

        // 送出時的實際頻道：頭上泡打字（點空曠處起、或送出後 armed 續打）一律走「當前頻道一般說話」——即使左下
        // 頻道選單停在家族/好友，氣泡打字都彈頭上藍泡、不被劫走成家族綠字或密語。家族/好友/回覆的專屬訊息只在
        // 「輸入框回顯」模式（頻道選單自動填「/家族 」前綴、或直接點左下輸入框）送。見 RoomScreen.SendRoomChat。
        public static ChatChannel ResolveSendChannel(bool bubbleTyping, ChatChannel selectedChannel)
            => bubbleTyping ? ChatChannel.Current : selectedChannel;

        // 文字是否以家族指令前綴（/家族、/公會、/guild…）開頭。用來在「當前」綜合台辨識「明打的家族訊息」：
        // 有前綴 → 送家族綠字；沒前綴 → 一般說話（見 RoomScreen.SendRoomChat）。
        public static bool HasGuildCommand(string text) => TryStripGuildCommand(text, out _);

        // 有家族指令前綴 → out body=剝掉前綴+空白後的內容（可空，代表只打了「/家族 」還沒內容），回傳 true；
        // 沒前綴 → body="" 回傳 false。前綴辨識規則：CJK 詞可緊接內容（無空白），拉丁詞需字界。
        public static bool TryStripGuildCommand(string text, out string body)
        {
            body = "";
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = text.TrimStart();
            if (s.Length < 1 || s[0] != '/') return false;
            string rest = s.Substring(1);
            foreach (var w in GuildCommandWords)
            {
                if (!rest.StartsWith(w, StringComparison.OrdinalIgnoreCase)) continue;
                string after = rest.Substring(w.Length);
                // 拉丁字母指令需字界（後接空白或結尾），避免把 "/guildhall" 咬壞；CJK 詞可直接接字。
                bool ascii = w[0] < 128;
                if (ascii && after.Length > 0 && !char.IsWhiteSpace(after[0])) continue;
                body = after.Trim();
                return true;
            }
            return false;
        }

        // 剝掉開頭的 `/家族`（或其他家族指令字）+ 後面空白，取內容。沒有指令前綴就原字（去頭尾空白）回傳。
        public static string StripGuildCommand(string text)
            => TryStripGuildCommand(text, out var body) ? body : (text != null ? text.Trim() : "");

        public static bool TryParseRoomAction(string text, out RoomChatAction action)
            => TryParseRoomAction(text, false, out action);

        public static bool TryParseRoomAction(string text, bool male, out RoomChatAction action)
        {
            action = null;
            if (string.IsNullOrWhiteSpace(text)) return false;

            string key = text.Trim();
            if (key.Length == 0) return false;

            return (male ? MaleRoomActionAliases : FemaleRoomActionAliases).TryGetValue(key, out action);
        }

        public static bool TryGetRoomAction(string id, out RoomChatAction action)
        {
            action = null;
            return !string.IsNullOrWhiteSpace(id) && RoomActionById.TryGetValue(id.Trim(), out action);
        }

        public static bool IsValidExpression(int expressionId)
            => expressionId >= 1 && expressionId <= MaxExpressionId;

        public static string ExpressionDisplayText(int expressionId)
            => ExpressionDisplayText(expressionId, LocalizationManager.Current);

        public static string ExpressionDisplayText(int expressionId, Language language)
        {
            if (!IsValidExpression(expressionId)) return "";
            int index = Array.IndexOf(MenuExpressionIds, expressionId);
            var commands = CommandsFor(language);
            return index >= 0 && index < commands.Length ? commands[index] : "/exp " + expressionId;
        }

        private static Dictionary<string, int> BuildExpressionAliases()
        {
            var aliases = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            AddCommandAliases(aliases, SimplifiedCommands);
            AddCommandAliases(aliases, TraditionalCommands);
            AddCommandAliases(aliases, EnglishCommands);
            AddCommandAliases(aliases, JapaneseCommands);

            aliases["hi"] = 4;
            aliases["hello"] = 4;
            aliases["ok"] = 13;
            aliases["y"] = 13;
            aliases["n"] = 6;
            aliases["sh"] = 17;
            aliases["ssh"] = 17;
            aliases["噓"] = 17;
            aliases["嘘"] = 17;
            return aliases;
        }

        private static Dictionary<string, RoomChatAction> BuildRoomActionAliases(bool male)
        {
            var aliases = new Dictionary<string, RoomChatAction>(StringComparer.OrdinalIgnoreCase);
            foreach (var action in RoomActions)
            {
                if (action == null) continue;
                var triggers = male ? action.MaleTriggers : action.FemaleTriggers;
                foreach (var trigger in triggers)
                    AddRoomActionAlias(aliases, trigger, action);
            }
            return aliases;
        }

        private static void AddRoomActionAlias(Dictionary<string, RoomChatAction> aliases, string trigger, RoomChatAction action)
        {
            if (string.IsNullOrWhiteSpace(trigger) || action == null) return;
            string key = trigger.Trim();
            if (!aliases.ContainsKey(key)) aliases.Add(key, action);
        }

        private static Dictionary<string, RoomChatAction> BuildRoomActionById()
        {
            var actions = new Dictionary<string, RoomChatAction>(StringComparer.OrdinalIgnoreCase);
            foreach (var action in RoomActions)
                if (action != null && !string.IsNullOrWhiteSpace(action.Id)) actions[action.Id] = action;
            return actions;
        }

        private static void AddCommandAliases(Dictionary<string, int> aliases, string[] commands)
        {
            int n = Math.Min(MenuExpressionIds.Length, commands.Length);
            for (int i = 0; i < n; i++)
            {
                string command = commands[i];
                if (string.IsNullOrEmpty(command)) continue;
                if (command.StartsWith("/", StringComparison.Ordinal))
                    aliases[command.Substring(1)] = MenuExpressionIds[i];
            }
        }

        private static bool TryParseLeadingExpressionNumber(string key, out int number, out string trailing)
        {
            number = 0;
            trailing = "";
            if (string.IsNullOrEmpty(key) || key[0] < '0' || key[0] > '9') return false;

            int i = 0;
            while (i < key.Length && key[i] >= '0' && key[i] <= '9') i++;
            if (!int.TryParse(key.Substring(0, i), out number)) return false;

            if (i >= key.Length)
            {
                trailing = "";
                return true;
            }

            // `/12 hello`：數字後面要空白才算尾隨，避免把 `/168` 咬壞。
            if (!char.IsWhiteSpace(key[i])) return false;
            trailing = key.Substring(i).Trim();
            return true;
        }

        private static bool TryMatchExpressionAlias(string key, out int expressionId, out string trailing)
        {
            expressionId = 0;
            trailing = "";
            if (string.IsNullOrEmpty(key)) return false;

            // 完整相等：純表情指令。
            if (ExpressionAliases.TryGetValue(key, out expressionId))
            {
                trailing = "";
                return true;
            }

            // `/無聊 任意文字`：第一個空白切開，前後都可是中文／英文／數字／符號。
            int ws = -1;
            for (int i = 0; i < key.Length; i++)
            {
                if (char.IsWhiteSpace(key[i]))
                {
                    ws = i;
                    break;
                }
            }
            if (ws <= 0) return false;

            string head = key.Substring(0, ws);
            if (!ExpressionAliases.TryGetValue(head, out expressionId)) return false;
            trailing = key.Substring(ws).Trim();
            return true;
        }

        private static string[] CommandsFor(Language language)
        {
            switch (language)
            {
                case Language.English: return EnglishCommands;
                case Language.SimplifiedChinese: return SimplifiedCommands;
                case Language.Japanese: return JapaneseCommands;
                default: return TraditionalCommands;
            }
        }

        private static string StripExpressionPrefix(string key)
        {
            string[] prefixes = { "exp", "emoji", "emote", "expression", "表情", "表情符号", "表情符號" };
            foreach (var p in prefixes)
            {
                if (key.Equals(p, StringComparison.OrdinalIgnoreCase)) return "";
                if (key.StartsWith(p + " ", StringComparison.OrdinalIgnoreCase))
                    return key.Substring(p.Length + 1).Trim();
                if (key.StartsWith(p + ":", StringComparison.OrdinalIgnoreCase))
                    return key.Substring(p.Length + 1).Trim();
            }
            return key;
        }

        private static bool TryParseCompactNumber(string key, string prefix, out int number)
            => TryParseCompactNumber(key, prefix, out number, out _);

        private static bool TryParseCompactNumber(string key, string prefix, out int number, out string trailing)
        {
            number = 0;
            trailing = "";
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            string rest = key.Substring(prefix.Length);
            if (rest.StartsWith(":", StringComparison.Ordinal)) rest = rest.Substring(1);
            rest = rest.Trim();
            if (rest.Length == 0) return false;
            return TryParseLeadingExpressionNumber(rest, out number, out trailing);
        }
    }

    public sealed class RoomChatAction
    {
        public readonly string Id;
        public readonly int OfficialActionByte;
        public readonly string DisplayText;
        public readonly string FemaleMotion;
        public readonly string MaleMotion;
        public readonly string FemaleSound;
        public readonly string MaleSound;
        public readonly string[] FemaleTriggers;
        public readonly string[] MaleTriggers;
        public readonly string[] Triggers;

        public RoomChatAction(string id, int officialActionByte, string displayText,
            string femaleMotion, string maleMotion, string femaleSound, string maleSound,
            string[] femaleTriggers, string[] maleTriggers)
        {
            Id = id;
            OfficialActionByte = officialActionByte;
            DisplayText = displayText;
            FemaleMotion = femaleMotion;
            MaleMotion = maleMotion;
            FemaleSound = femaleSound;
            MaleSound = maleSound;
            FemaleTriggers = femaleTriggers ?? new string[0];
            MaleTriggers = maleTriggers ?? new string[0];
            Triggers = MergeTriggers(FemaleTriggers, MaleTriggers);
        }

        /// <summary>The gender-correct motion (MOTION/*.MOT rel path) — male plays the MREST clip, female the WREST clip.</summary>
        public string MotionFor(bool male) => male ? MaleMotion : FemaleMotion;

        /// <summary>The gender-correct SE stem (official FUN_009534a0 switch: MAN_n / WOMAN_n). Null when no sound case.</summary>
        public string SoundFor(bool male) => male ? MaleSound : FemaleSound;

        private static string[] MergeTriggers(string[] femaleTriggers, string[] maleTriggers)
        {
            var merged = new List<string>();
            AddTriggers(merged, femaleTriggers);
            AddTriggers(merged, maleTriggers);
            return merged.ToArray();
        }

        private static void AddTriggers(List<string> merged, string[] triggers)
        {
            if (triggers == null) return;
            foreach (var trigger in triggers)
            {
                if (string.IsNullOrWhiteSpace(trigger)) continue;
                bool exists = false;
                for (int i = 0; i < merged.Count; i++)
                {
                    if (string.Equals(merged[i], trigger, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
                if (!exists) merged.Add(trigger);
            }
        }
    }
}
