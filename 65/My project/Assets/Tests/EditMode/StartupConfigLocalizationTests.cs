using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using Sdo.Localization;
using Sdo.Settings;

namespace Sdo.Tests
{
    /// <summary>
    /// 開場（選性別）畫面那塊設定面板的多語系。字全部走 <c>cfg.*</c>：表在
    /// <see cref="StartupConfigSchema"/>（<c>cfg.&lt;名字&gt;.label</c> / <c>.help</c>）、面板自己的字在
    /// <c>StartupConfigPanel</c>（<c>cfg.panel.*</c>），四份語言檔由 <c>tools/build_localization.py</c> 產。
    ///
    /// 為什麼要有這一條:漏一個 key 不會編譯失敗、不會拋例外 —— 只會在面板上出現一列
    /// <c>[cfg.mmd_toon.help]</c>,而且只有切到那個語言、又剛好把滑鼠移到那一列才看得到。
    /// <see cref="LocalizationFilesTests"/> 顧「四份語言檔的 key 集合一致」、
    /// <see cref="LocalizationKeyUsageTests"/> 顧「程式碼裡寫出來的 key 表裡有」;
    /// 這裡顧的是這張表**自己**:每一列都指到 key,而且沒有留下沒人用的孤兒 key。
    /// </summary>
    public class StartupConfigLocalizationTests
    {
        private static readonly string[] Langs = { "en", "zh-TW", "zh-Hans", "ja" };

        private static string Dir => Path.Combine(Application.streamingAssetsPath, "Localization");

        private static string ScriptsDir => Path.Combine(Application.dataPath, "Scripts");

        [Test]
        public void Every_Field_Points_At_A_Label_And_A_Help_Key()
        {
            foreach (var f in StartupConfigSchema.Fields)
            {
                Assert.IsNotEmpty(f.LabelKey, f.Key + " 沒有 LabelKey");
                Assert.IsNotEmpty(f.HelpKey, f.Key + " 沒有 HelpKey");
                StringAssert.StartsWith("cfg.", f.LabelKey, f.Key + " 的 LabelKey 不在 cfg.* 底下");
                StringAssert.StartsWith("cfg.", f.HelpKey, f.Key + " 的 HelpKey 不在 cfg.* 底下");
            }
        }

        [Test]
        public void Every_Key_The_Panel_Draws_Exists_In_All_Four_Languages()
        {
            var need = SchemaKeys();
            foreach (var lang in Langs)
            {
                var t = StringTable.Parse(File.ReadAllText(PathOf(lang), Encoding.UTF8));
                var missing = new List<string>();
                foreach (var k in need)
                    if (!t.TryGet(k, out var v) || string.IsNullOrEmpty(v)) missing.Add(k);
                Assert.IsEmpty(missing,
                    lang + ".json 少了設定面板的字（面板上會直接顯示 [key]）—— 補進 tools/build_localization.py 再重跑它：\n  "
                    + string.Join("\n  ", missing));
            }
        }

        [Test]
        public void Nothing_On_The_Panel_Falls_Back_To_A_Bracketed_Key()
        {
            // 上面那條是「檔案裡有沒有」，這條是真的走一遍 Label/Help/選項/按鈕 —— HelpArgs 帶錯數量、
            // key 拼錯之類的只有實際解一次才看得到。四種語言各跑一遍。
            var entry = LocalizationManager.Current;
            try
            {
                foreach (var lang in LanguageInfo.All)
                {
                    LocalizationManager.Init(lang);
                    foreach (var c in StartupConfigSchema.Categories)
                        AssertResolved(StartupConfigSchema.CategoryText(c), lang, c);
                    foreach (var f in StartupConfigSchema.Fields)
                    {
                        AssertResolved(f.Label, lang, f.Key + ".Label");
                        AssertResolved(f.Help, lang, f.Key + ".Help");
                        if (f.ChoiceLabelKeys != null)
                            for (int i = 0; i < f.Choices.Length; i++)
                                AssertResolved(f.ChoiceTextAt(i), lang, f.Key + ".Choice" + i);
                        if (f.ActionKeys != null)
                            foreach (var a in f.Actions) AssertResolved(a, lang, f.Key + ".Action");
                    }
                    // 判定精度那一列的值是自己組出來的（精1…精8 / JUSTICE），不是 Label/Help。
                    AssertResolved(StartupConfigSchema.JudgeLevelText(4f), lang, "judgeLevel value");
                    AssertResolved(StartupConfigSchema.JudgeLevelText(9f), lang, "judgeLevel JUSTICE");
                }
            }
            finally { LocalizationManager.Init(entry); }
        }

        [Test]
        public void No_Orphan_Cfg_Keys_In_The_Tables()
        {
            // 改名/砍掉一列設定時很容易只改 C# 那邊，翻譯就這樣留著沒人用。四份都要一起清掉，
            // 所以只要 en.json 有一個沒人引用的 cfg.* 就算數。
            var code = new StringBuilder();
            foreach (var file in Directory.GetFiles(ScriptsDir, "*.cs", SearchOption.AllDirectories))
                code.Append(File.ReadAllText(file, Encoding.UTF8));
            string all = code.ToString();

            var orphans = new List<string>();
            foreach (var k in KeysOf("en"))
            {
                if (!k.StartsWith("cfg.")) continue;
                // 分頁 key 是 const（"cfg.cat.net" 這個字串本身就寫在 StartupConfigSchema 裡），一樣找得到。
                if (!all.Contains("\"" + k + "\"")) orphans.Add(k);
            }
            Assert.IsEmpty(orphans,
                "這些 cfg.* 沒有任何程式碼引用（改名之後忘了刪？）：" + string.Join(", ", orphans));
        }

        private static void AssertResolved(string text, Language lang, string what)
        {
            Assert.IsNotEmpty(text, LanguageInfo.Code(lang) + "：" + what + " 是空的");
            Assert.IsFalse(Regex.IsMatch(text, @"^\[cfg\."),
                LanguageInfo.Code(lang) + "：" + what + " 解不出來，畫面上會是 " + text);
        }

        /// <summary>這張表**自己**指名的每一個 key（分頁 + 每列的標籤/說明/選項/按鈕）。
        /// 只在執行期組出來的那幾個（判定精度的值、MMD 模型「找不到」、存檔結果訊息）不在這裡 ——
        /// 它們寫在程式碼裡，由 <see cref="LocalizationKeyUsageTests"/> 掃字串抓。</summary>
        private static List<string> SchemaKeys()
        {
            var keys = new List<string>(StartupConfigSchema.Categories);
            foreach (var f in StartupConfigSchema.Fields)
            {
                keys.Add(f.LabelKey);
                keys.Add(f.HelpKey);
                if (f.ChoiceLabelKeys != null) keys.AddRange(f.ChoiceLabelKeys);
                if (f.ActionKeys != null) keys.AddRange(f.ActionKeys);
            }
            return keys;
        }

        private static string PathOf(string lang) => Path.Combine(Dir, lang + ".json");

        // StringTable 只給查詢，沒有列 key 的 API → 直接掃檔案文字（同 LocalizationFilesTests）。
        private static HashSet<string> KeysOf(string lang)
        {
            var set = new HashSet<string>();
            const string marker = "\"k\":";
            string text = File.ReadAllText(PathOf(lang), Encoding.UTF8);
            int i = 0;
            while (true)
            {
                i = text.IndexOf(marker, i, System.StringComparison.Ordinal);
                if (i < 0) break;
                int q1 = text.IndexOf('"', i + marker.Length);
                if (q1 < 0) break;
                int q2 = text.IndexOf('"', q1 + 1);
                if (q2 < 0) break;
                set.Add(text.Substring(q1 + 1, q2 - q1 - 1));
                i = q2 + 1;
            }
            return set;
        }
    }
}
