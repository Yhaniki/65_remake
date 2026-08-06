using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using Sdo.Localization;
using Sdo.UI.Util;

namespace Sdo.Tests
{
    /// <summary>
    /// OPTION 對話框「進階」那頁的字面。這一頁是整個對話框唯一畫動態文字的地方,用的是
    /// <see cref="UIFont.DialogFace"/>。
    ///
    /// 為什麼需要一條專門的測試:bundled 的 `DFLiHei.ttc`(華康儷中黑)**會騙人** —— cmap 宣告 35,981 個碼位,
    /// 其中 11,706 個的字形是空的(有登記、沒筆畫)。因為 cmap 有登記,TMP 認定「這個字我有」→ fallback 永遠
    /// 不會啟動,直接畫出一片空白。症狀是「字不見了」而不是豆腐字 □,肉眼很難看出是字型問題
    /// (使用者回報:簡中的「显示模式」變成「示模式」、「语言」變成「言」、「简体中文」變成「体中文」)。
    ///
    /// 🔴 所以**不能用 <c>HasCharacter()</c> 驗**(既有的 <see cref="UIFontTests"/> 就是這樣驗的,它對空心字回
    ///    true,一路綠燈)。要問「畫不畫得出來」只能看 glyph metrics 有沒有寬高 —— <see cref="UIFont.CanRender"/>。
    /// </summary>
    public class OptionDialogFontTests
    {
        /// <summary>這一頁畫得出來的每一句話。與 <c>OptionDlgModal.BuildAdvanced</c> 對應 ——
        /// 那邊加一列就要在這裡補一個 key,否則新的字缺了沒人知道。</summary>
        private static readonly string[] AdvancedKeys =
        {
            "settings.play_full_song", "settings.song_speed", "settings.song_bomb",
            "settings.vsync", "settings.resolution", "settings.display_mode", "settings.language",
            "common.enabled", "common.disabled",
            "display.windowed", "display.fullscreen", "display.borderless",
        };

        /// <summary>語言下拉的四個選項是**各自語言的自稱**,寫死在 OptionDlgModal 裡(不進語言表)。</summary>
        private const string LanguageNames = "繁體中文简体中文English日本語";

        /// <summary>某個語言在**它自己當道**時,那格下拉會顯示的名字。
        /// 🔴 只驗這一個而不是四個都驗:<c>MiniSelect</c> 只畫「目前選中的那一個」,而按 ◀▶ 換到別的語言時
        ///    <c>Set()</c> 會先貼字再 notify → <c>SetLanguage</c> → <c>LanguageChanged</c> → 換字面,
        ///    三件事都在同一幀、canvas 重建之前跑完 —— 螢幕上不會有「舊字面配新語言名字」的那一瞬間。
        ///    (四個名字全部畫得出來這件事由 <see cref="Bundled_Source_Han_Sans_Covers_Every_Shipped_String"/> 顧。)</summary>
        private static string NativeName(Language l) => l switch
        {
            Language.TraditionalChinese => "繁體中文",
            Language.SimplifiedChinese => "简体中文",
            Language.Japanese => "日本語",
            _ => "English",
        };

        private static string Dir => Path.Combine(Application.streamingAssetsPath, "Localization");

        [Test]
        public void DialogFace_Can_Actually_Draw_The_Advanced_Page_In_Every_Language()
        {
            var entry = LocalizationManager.Current;
            try
            {
                foreach (var lang in LanguageInfo.All)
                {
                    LocalizationManager.Init(lang);
                    var face = UIFont.DialogFace;
                    Assert.IsNotNull(face, LanguageInfo.Code(lang) + "：DialogFace 是 null");

                    var text = new StringBuilder(NativeName(lang));
                    foreach (var k in AdvancedKeys) text.Append(LocalizationManager.Get(k));

                    Assert.IsTrue(UIFont.CanRender(face, text.ToString(), out var bad),
                        LanguageInfo.Code(lang) + "：字面 " + face.name + " 畫不出 '" + bad +
                        "'（U+" + ((int)bad).ToString("X4") + "）—— 畫面上那個字會是一片空白。");
                }
            }
            finally { LocalizationManager.Init(entry); }
        }

        [Test]
        public void Lihei_Declares_Simplified_Glyphs_That_Are_Actually_Hollow()
        {
            // 這條是「釘住真因」用的:它一旦變綠(儷中黑補上了那些字),DialogFace 的依語言硬切就可以拿掉。
            // 反過來,它也擋住「把 CanRender 簡化回 HasCharacter」——那樣寫的話下面第二個斷言會失敗。
            var lihei = UIFont.Lihei;
            if (lihei == null || lihei == UIFont.Cjk) Assert.Ignore("DFLiHei.ttc 沒載到（這台機器沒有那個資產）");

            const string Hollow = "显语简弹开关闭认频";   // 簡中;日文那批是 変弾楽戻観説読
            foreach (char c in Hollow)
            {
                Assert.IsTrue(lihei.HasCharacter(c, searchFallbacks: false, tryAddCharacter: true),
                    "'" + c + "' 已經不在 DFLiHei 的 cmap 裡了 —— 這條測試的前提變了，回去看 UIFont.DialogFace");
                Assert.IsFalse(UIFont.CanRender(lihei, c.ToString(), out _),
                    "'" + c + "' 在儷中黑裡現在畫得出來了 —— 若整批都是，DialogFace 就不必再依語言切");
            }
        }

        [Test]
        public void Bundled_Source_Han_Sans_Covers_Every_Shipped_String()
        {
            // 「有沒有一個不缺字的字體」的答案:專案裡 bundled 的思源黑體。四份語言檔的每一個字都要畫得出來
            // —— 它是所有字型的最後一道 fallback，它缺了就真的沒人接。
            var bundled = UIFont.Bundled;
            if (bundled == null) Assert.Ignore("SourceHanSansTC 沒載到（這台機器沒有那個資產）");

            foreach (var lang in new[] { "en", "zh-TW", "zh-Hans", "ja" })
            {
                var t = StringTable.Parse(File.ReadAllText(Path.Combine(Dir, lang + ".json"), Encoding.UTF8));
                var chars = new HashSet<char>();
                foreach (var k in AdvancedKeys)
                    if (t.TryGet(k, out var v) && v != null) foreach (var c in v) chars.Add(c);
                foreach (var c in LanguageNames) chars.Add(c);

                var sb = new StringBuilder();
                foreach (var c in chars) sb.Append(c);
                Assert.IsTrue(UIFont.CanRender(bundled, sb.ToString(), out var bad),
                    lang + "：思源黑體畫不出 '" + bad + "'（U+" + ((int)bad).ToString("X4") + "）");
            }
        }
    }
}
