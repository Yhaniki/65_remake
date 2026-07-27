using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using Sdo.Localization;

namespace Sdo.Tests
{
    /// <summary>
    /// 真的出貨的那四份語言檔(<c>StreamingAssets/Localization/*.json</c>)。
    ///
    /// 為什麼需要這個檔:漏翻一個 key 的症狀是畫面上出現 <c>[room.join_full]</c> ——
    /// 不會拋例外、不會有警告,而且只在剛好切到那個語言又剛好走到那條分支時才看得到。
    /// 加一組 key 就是「四個檔都要改」,少改一個沒有任何東西會擋下來,所以這裡把
    /// 「四份的 key 集合必須完全相同」變成一條測試。
    /// </summary>
    public class LocalizationFilesTests
    {
        /// <summary>出貨的語言檔。新增語言時要一起加進來。</summary>
        private static readonly string[] Langs = { "en", "zh-TW", "zh-Hans", "ja" };

        private static string Dir => Path.Combine(Application.streamingAssetsPath, "Localization");

        private static string PathOf(string lang) => Path.Combine(Dir, lang + ".json");

        private static string ReadText(string lang)
            => File.ReadAllText(PathOf(lang), Encoding.UTF8);

        [Test]
        public void All_Shipped_Language_Files_Parse()
        {
            foreach (var lang in Langs)
            {
                Assert.IsTrue(File.Exists(PathOf(lang)), "少了語言檔:" + PathOf(lang));
                var t = StringTable.Parse(ReadText(lang));
                Assert.IsNotNull(t, lang + ".json 解不出來(JSON 壞了?)");
                Assert.AreEqual(lang, t.LanguageCode, lang + ".json 的 language 欄位與檔名不一致");
                Assert.IsNotEmpty(t.Culture, lang + ".json 少了 culture");
            }
        }

        [Test]
        public void Every_Language_Has_Exactly_The_Same_Keys()
        {
            // 以英文當基準:它是 LocalizationManager 的 fallback,缺的 key 會退回這裡。
            var baseKeys = KeysOf("en");
            Assert.Greater(baseKeys.Count, 100, "前提:en.json 應該有上百個 key");

            foreach (var lang in Langs)
            {
                if (lang == "en") continue;
                var keys = KeysOf(lang);
                var missing = new List<string>();
                foreach (var k in baseKeys) if (!keys.Contains(k)) missing.Add(k);
                var extra = new List<string>();
                foreach (var k in keys) if (!baseKeys.Contains(k)) extra.Add(k);

                Assert.IsEmpty(missing, lang + ".json 少了這些 key(畫面會顯示成 [key]):" + string.Join(", ", missing));
                // 多出來的 key 不會壞畫面,但通常表示「改名時只改了一邊」——一樣要清掉。
                Assert.IsEmpty(extra, lang + ".json 有 en.json 沒有的 key(改名只改了一邊?):" + string.Join(", ", extra));
            }
        }

        [Test]
        public void No_Duplicate_Keys_And_No_Empty_Values()
        {
            foreach (var lang in Langs)
            {
                // 重複的 key 不會報錯,後面那筆就這樣默默蓋掉前面那筆 —— 只有翻譯「改了沒生效」的怪現象。
                var seen = new HashSet<string>();
                var dupes = new List<string>();
                foreach (var k in RawKeys(lang)) if (!seen.Add(k)) dupes.Add(k);
                Assert.IsEmpty(dupes, lang + ".json 有重複的 key:" + string.Join(", ", dupes));

                var t = StringTable.Parse(ReadText(lang));
                foreach (var k in seen)
                {
                    string v;
                    Assert.IsTrue(t.TryGet(k, out v), lang + " 讀不到 " + k);
                    Assert.IsNotEmpty(v, lang + ".json 的 " + k + " 是空字串(那在畫面上等於什麼都沒有)");
                }
            }
        }

        [Test]
        public void Join_Room_Flow_Has_All_Its_Strings()
        {
            // 加入房間那條流程的每一句話。這些是 JoinRoomModal 與 GenderSelectScreen.JoinErrorKey 會拿的 key ——
            // 少一個的症狀是對話框上出現 [room.join_full] 那種東西。
            string[] keys =
            {
                "room.join_title", "room.join_code", "room.join_confirm", "room.join_bad_code",
                "room.join_not_found", "room.join_full", "room.join_in_game", "room.join_failed",
                "room.create_failed", "common.cancel",
            };
            foreach (var lang in Langs)
            {
                var t = StringTable.Parse(ReadText(lang));
                foreach (var k in keys)
                {
                    string v;
                    Assert.IsTrue(t.TryGet(k, out v) && !string.IsNullOrEmpty(v), lang + ".json 少了 " + k);
                }
            }
        }

        // StringTable 只給查詢,沒有列 key 的 API → 直接掃檔案文字。也正好抓得到「重複的 key」
        // (StringTable 內部是 dictionary,重複的在它眼裡已經被合併掉了)。
        private static IEnumerable<string> RawKeys(string lang)
        {
            const string marker = "\"k\":";
            string text = ReadText(lang);
            int i = 0;
            while (true)
            {
                i = text.IndexOf(marker, i, System.StringComparison.Ordinal);
                if (i < 0) break;
                int q1 = text.IndexOf('"', i + marker.Length);
                if (q1 < 0) break;
                int q2 = text.IndexOf('"', q1 + 1);
                if (q2 < 0) break;
                yield return text.Substring(q1 + 1, q2 - q1 - 1);
                i = q2 + 1;
            }
        }

        private static HashSet<string> KeysOf(string lang)
        {
            var set = new HashSet<string>();
            foreach (var k in RawKeys(lang)) set.Add(k);
            return set;
        }
    }
}
