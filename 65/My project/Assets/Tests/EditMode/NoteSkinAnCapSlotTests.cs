using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// 守住「每一套 2D noteskin 的長條**尾端一定有封口(尾帽)**」。
    ///
    /// 官方 <c>NOTEIMAGE.AN</c> 是 16 個一組(4 軌 × 4 幀,軌序 left/down/up/right),第 4 組(frame 48..63)
    /// = 長條**尾端**的封口。有些 skin 在「靠判定線那端」那一組(第 3 組)放 <c>*HoldHeadActive*</c>(note 頭)
    /// 當填充,意思是那一端不畫封口 —— 那是官方常態(5/8/9/10/PET 都這樣)。但**尾端**那一組放 note 頭就不是
    /// 常態了:整條長條會變成硬切的斷面。
    ///
    /// 🔴 NOTEIMAGE_8 的原檔正是這樣寫錯的:第 3、4 組都被複製成 note 頭,四張
    /// <c>{rightleft,updown}_LONG_BOTTOM(_D).PNG</c> 俱全卻沒有任何 .an 指到 → 使用者回報「NOTEIMAGE_8 的
    /// 尾帽向上向下都不見了」。修正檔放在 <c>art\upscaled\NOTEIMAGE\NOTEIMAGE_8\</c>(與 ROOM93.AN 的
    /// hover 修正同一棵樹),由 build_clean_data.ps1 / package_build.ps1 疊到 DATA 上。
    /// 這條測試就是釘住那份修正:少疊了 overlay、或哪天又被原檔蓋回去,這裡會紅。
    ///
    /// 讀真實資料,沒資料就 Ignore(比照 <see cref="NoteSkinNativeSizeTests"/>)。
    /// </summary>
    public class NoteSkinAnCapSlotTests
    {
        // NOTEIMAGE.AN 的組別:0 = note 頭, 1 = 長條身體, 2 = 靠判定線端的封口, 3 = 尾端的封口。
        // 與 ScreenGameplay.CapSlotHead / CapSlotTail 同一份定義。
        const int CapSlotTail = 3;
        const int PerGroup = 16;   // 4 軌 × 4 幀

        static readonly string[] Skins =
        {
            "NOTEIMAGE_5", "NOTEIMAGE_6", "NOTEIMAGE_8", "NOTEIMAGE_9",
            "NOTEIMAGE_10", "NOTEIMAGE_11", "NOTEIMAGE_PET", "NOTEIMAGE_SHOWTIME",
        };

        static string NoteImageDir()
        {
            foreach (var d in new[] { Path.Combine(SdoExtracted.Root, "NOTEIMAGE"), @"H:/65_remake_clean/DATA/NOTEIMAGE" })
                if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) return d;
            return null;
        }

        /// <summary>.an 依序引用的檔名(只取名字,忽略後面的 crop 括號)—— 與 SdoExtracted.AnFrameNames 同一條規則。</summary>
        static List<string> AnFrameNames(string path)
        {
            var names = new List<string>();
            foreach (var raw in File.ReadAllText(path).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                int paren = line.IndexOf('(');
                names.Add(paren >= 0 && line.EndsWith(")") ? line.Substring(0, paren).Trim() : line);
            }
            return names;
        }

        static bool FileExistsCI(string dir, string name)
        {
            foreach (var f in Directory.GetFiles(dir))
                if (string.Equals(Path.GetFileName(f), name, System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ── 尾端那一組必須放真的尾帽圖,而且那張圖真的在 skin 資料夾裡 ────────────────────────────────────
        [Test]
        public void EverySkinDrawsATailCap()
        {
            var root = NoteImageDir();
            if (root == null) Assert.Ignore("NOTEIMAGE art not present in this environment.");

            int checkedSkins = 0;
            foreach (var skin in Skins)
            {
                var dir = Path.Combine(root, skin);
                if (!Directory.Exists(dir)) continue;
                foreach (var an in new[] { "NOTEIMAGE.AN", "NOTEIMAGE_MOVEDOWN.AN" })
                {
                    var path = Path.Combine(dir, an);
                    if (!File.Exists(path)) continue;
                    checkedSkins++;

                    var names = AnFrameNames(path);
                    int lo = CapSlotTail * PerGroup;
                    Assert.GreaterOrEqual(names.Count, lo + PerGroup,
                        $"{skin}/{an} 只有 {names.Count} 格 —— 讀不到尾端封口那一組(需要至少 {lo + PerGroup} 格)");

                    for (int i = lo; i < lo + PerGroup; i++)
                    {
                        var n = names[i] ?? "";
                        Assert.IsTrue(n.IndexOf("holdheadactive", System.StringComparison.OrdinalIgnoreCase) < 0,
                            $"{skin}/{an} frame {i}(長條尾端的封口)填的是 note 頭 \"{n}\" —— 照這份 .an 走,尾帽整個不會畫," +
                            "長條尾端變成硬切的斷面。NOTEIMAGE_8 的原檔就是這樣寫錯的,修正檔在 art\\upscaled\\NOTEIMAGE\\");
                        Assert.IsTrue(n.IndexOf("_long_bottom", System.StringComparison.OrdinalIgnoreCase) >= 0
                                   || n.IndexOf("_long_head", System.StringComparison.OrdinalIgnoreCase) >= 0,
                            $"{skin}/{an} frame {i} 指到 \"{n}\",不是尾帽圖(*_long_bottom / *_long_head)");
                        Assert.IsTrue(FileExistsCI(dir, n), $"{skin}/{an} frame {i} 指到不存在的檔 \"{n}\"");
                    }
                }
            }
            Assert.Greater(checkedSkins, 0, "一份 NOTEIMAGE.AN 都沒找到 — 資料樹不完整");
        }

        // ── NOTEIMAGE_8 的修正檔本身:兩支 .an 的尾端組要指到「同一頂帽子的兩個朝向」──────────────────────
        [Test]
        public void NoteImage8TailCapUsesBothOrientations()
        {
            var root = NoteImageDir();
            if (root == null) Assert.Ignore("NOTEIMAGE art not present in this environment.");
            var dir = Path.Combine(root, "NOTEIMAGE_8");
            if (!Directory.Exists(dir)) Assert.Ignore("NOTEIMAGE_8 not present.");

            // 軌序 left/down/up/right → 左右軌用 rightleft_*、上下軌用 updown_*(與 NOTEIMAGE_5 完全相同的排法)。
            string[] fam = { "rightleft", "updown", "updown", "rightleft" };
            foreach (var pair in new[] { new[] { "NOTEIMAGE.AN", "" }, new[] { "NOTEIMAGE_MOVEDOWN.AN", "_d" } })
            {
                var path = Path.Combine(dir, pair[0]);
                Assert.IsTrue(File.Exists(path), $"NOTEIMAGE_8/{pair[0]} 不存在");
                var names = AnFrameNames(path);
                for (int lane = 0; lane < 4; lane++)
                {
                    string want = fam[lane] + "_long_bottom" + pair[1] + ".png";
                    for (int f = 0; f < 4; f++)
                    {
                        int i = CapSlotTail * PerGroup + lane * 4 + f;
                        Assert.AreEqual(want.ToLowerInvariant(), (names[i] ?? "").ToLowerInvariant(),
                            $"NOTEIMAGE_8/{pair[0]} frame {i}(第 {lane} 軌尾帽)");
                    }
                }
            }
            // 兩個朝向的圖都得在,否則 ScreenGameplay 只拿得到一張、得靠翻轉湊另一頭。
            // (NOTEIMAGE_8 的 updown 那對是**對調存放**的 —— 誰是上緣封口由圖的內容重心判,不看檔名。)
            foreach (var f in new[] { "rightleft_long_bottom.png", "rightleft_long_bottom_d.png",
                                      "updown_long_bottom.png", "updown_long_bottom_d.png" })
                Assert.IsTrue(FileExistsCI(dir, f), $"NOTEIMAGE_8 少了尾帽圖 {f}");
        }
    }
}
