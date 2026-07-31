using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using Sdo.UI.Catalog;

namespace Sdo.Tests
{
    /// <summary>
    /// 「程式碼裡用到的每個 localization key,語言表裡都要有」。
    ///
    /// 為什麼要一條會自己去翻程式碼的測試:漏一個 key 不會編譯失敗、不會拋例外,只會在剛好走到
    /// 那條分支時把 <c>[room.teams_normal_mode_only]</c> 直接印在畫面上給玩家看。歷史上這種修法
    /// 全是「玩家先看到 → 回報 → 補一行」(見 commit「隨機難度 20/25 級以上顯示成 [songselect.rand_20up]」),
    /// 而每一次都是同一個原因:加程式的人沒有東西提醒他去改 build_localization.py。
    ///
    /// <see cref="LocalizationFilesTests"/> 顧的是另一半 ——「四份語言檔的 key 集合要一致」。
    /// 兩條合起來才是完整的:那邊擋「翻了一半」,這邊擋「根本沒翻」。
    ///
    /// 🔴 掃描範圍的取捨(**故意**不掃全部字串):只認「第一段前綴已經在表裡出現過」的字串,
    /// 例如 <c>room.*</c> / <c>songselect.*</c> / <c>neterr.*</c>。原因是專案裡到處都是
    /// <c>"label_all_2.an"</c>、<c>"config.ini"</c> 這種帶點的檔名,全掃會變成一片假紅 ——
    /// 而假紅測試會被關掉,關掉的測試等於沒有。代價是「開了全新前綴的第一個 key」漏得掉
    /// (例如第一次出現 <c>wardrobe.*</c> 時);那一種只能靠 review。實務上新 key 幾乎都掛在既有前綴下。
    /// </summary>
    public class LocalizationKeyUsageTests
    {
        private static string EnJsonPath =>
            Path.Combine(Application.streamingAssetsPath, "Localization", "en.json");

        private static string ScriptsDir => Path.Combine(Application.dataPath, "Scripts");

        // 帶點的字串裡有一大票是檔名。副檔名一律不當成 key。
        private static readonly Regex FileExt = new Regex(
            @"\.(dds|an|png|jpg|jpeg|json|gn|ogg|dat|txt|ini|tsv|csv|dll|osu|mot|hrc|dps|msh|mc|sm|xml|exe|log|bmp|tga|wav|mp3|ttf|otf|shader|cs|meta|header|scope|asset|prefab|unity|ps1|py|bat|cmd)$",
            RegexOptions.IgnoreCase);

        // key 樣式:全小寫 + 底線,至少一個點。$"stage.name.{i}" 那種內插不在這裡處理
        // (它由 Every_Stage_Name_Key_Is_Defined 直接問 StageCatalog 要真正的 key)。
        private static readonly Regex KeyLike = new Regex("\"([a-z][a-z0-9_]*(?:\\.[a-z0-9_]+)+)\"");

        [Test]
        public void Every_Key_Used_In_Code_Is_Defined()
        {
            var table = KeysOfEnglishTable();
            var prefixes = new HashSet<string>();
            foreach (var k in table)
            {
                int dot = k.IndexOf('.');
                if (dot > 0) prefixes.Add(k.Substring(0, dot));
            }
            Assert.Greater(prefixes.Count, 5, "前提:en.json 應該有好幾個前綴(common./room./songselect. …)");

            var missing = new List<string>();
            foreach (var file in Directory.GetFiles(ScriptsDir, "*.cs", SearchOption.AllDirectories))
            {
                var lines = File.ReadAllLines(file, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    // 註解行跳過:討論舊 key / 已改名 key 的說明文字不該讓測試變紅。
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("*", StringComparison.Ordinal))
                        continue;

                    foreach (Match m in KeyLike.Matches(line))
                    {
                        string k = m.Groups[1].Value;
                        if (FileExt.IsMatch(k)) continue;
                        int dot = k.IndexOf('.');
                        if (!prefixes.Contains(k.Substring(0, dot))) continue;   // 不是 loc key(見類別註解)
                        if (table.Contains(k)) continue;
                        missing.Add(k + "  ←  " + RelPath(file) + ":" + (i + 1));
                    }
                }
            }

            Assert.IsEmpty(missing,
                "這些 key 有人在用但 en.json 沒有(畫面會直接顯示 [key])。" +
                "補的位置是 tools/build_localization.py 的 STR,改完重跑那支腳本產生四份語言檔:\n  "
                + string.Join("\n  ", missing));
        }

        [Test]
        public void Every_Stage_Name_Key_Is_Defined()
        {
            // 場景名是 $"stage.name.{i}" 組出來的 —— 字串掃描看不到,所以直接問目錄要。
            // 漏一個不會顯示 [key](StageInfo.DisplayName 會退回中文名),但英日語系的人就會在
            // 一排英文場景名裡看到一個中文,那更難發現。
            var table = KeysOfEnglishTable();
            var missing = new List<string>();
            foreach (var s in StageCatalog.Stages)
            {
                if (string.IsNullOrEmpty(s.NameKey)) continue;
                if (!table.Contains(s.NameKey)) missing.Add(s.NameKey + " (" + s.Folder + " / " + s.NameZh + ")");
            }
            Assert.IsEmpty(missing, "這些場景沒有名字翻譯,非中文語系會退回中文名:\n  " + string.Join("\n  ", missing));
        }

        [Test]
        public void Every_Random_Range_Label_Is_Defined()
        {
            // 隨機難度那幾列的字。這串是資料表(SongListModel.RandRanges)裡的欄位,
            // 曾經真的漏過(「隨機難度 20/25 級以上顯示成 [songselect.rand_20up]」)。
            var table = KeysOfEnglishTable();
            var missing = new List<string>();
            foreach (var r in SongListModel.RandRanges)
                if (!string.IsNullOrEmpty(r.Key) && !table.Contains(r.Key)) missing.Add(r.Key);
            Assert.IsEmpty(missing, "隨機難度區間少了字:\n  " + string.Join("\n  ", missing));
        }

        private static string RelPath(string full)
        {
            var root = Application.dataPath.Replace('\\', '/');
            var p = full.Replace('\\', '/');
            return p.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? "Assets" + p.Substring(root.Length) : p;
        }

        // en.json 的 key 集合。用文字掃(與 LocalizationFilesTests 同一招)而不是 StringTable ——
        // StringTable 只給查詢,沒有列 key 的 API。
        private static HashSet<string> KeysOfEnglishTable()
        {
            Assert.IsTrue(File.Exists(EnJsonPath), "找不到 en.json:" + EnJsonPath);
            var set = new HashSet<string>();
            string text = File.ReadAllText(EnJsonPath, Encoding.UTF8);
            const string marker = "\"k\":";
            int i = 0;
            while (true)
            {
                i = text.IndexOf(marker, i, StringComparison.Ordinal);
                if (i < 0) break;
                int q1 = text.IndexOf('"', i + marker.Length);
                if (q1 < 0) break;
                int q2 = text.IndexOf('"', q1 + 1);
                if (q2 < 0) break;
                set.Add(text.Substring(q1 + 1, q2 - q1 - 1));
                i = q2 + 1;
            }
            Assert.Greater(set.Count, 100, "前提:en.json 應該有上百個 key");
            return set;
        }
    }
}
