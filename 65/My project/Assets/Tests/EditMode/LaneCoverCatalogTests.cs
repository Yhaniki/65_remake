using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Sdo.Game;
using Sdo.Settings;

namespace Sdo.Tests
{
    /// <summary>
    /// 擋板圖清單（<see cref="LaneCoverCatalog"/>）—— 掃 <c>&lt;DATA&gt;/ADDON/LANE</c> 掃出哪些圖、名字從哪來、
    /// 設定檔存的 Id 怎麼對回檔案。filesystem 全部注入，所以整組不碰磁碟。
    /// </summary>
    public class LaneCoverCatalogTests
    {
        // 一棵假的檔案樹：root → 底下所有檔案（含子資料夾）。
        private static Func<string, IEnumerable<string>> Fs(Dictionary<string, string[]> tree)
            => root => tree.TryGetValue(root, out var files) ? files : Array.Empty<string>();

        private static Func<string, bool> Dirs(params string[] existing)
            => d => Array.IndexOf(existing, d) >= 0;

        private static Func<string, string> Texts(Dictionary<string, string> files)
            => p => files.TryGetValue(p, out var t) ? t : null;

        // ---------------------------------------------------------------- 純小工具

        [Test]
        public void IsImage_Accepts_The_Shipped_Formats_Only()
        {
            Assert.IsTrue(LaneCoverCatalog.IsImage("a/b.jpg"));
            Assert.IsTrue(LaneCoverCatalog.IsImage("a/b.JPEG"));
            Assert.IsTrue(LaneCoverCatalog.IsImage("a/b.png"));
            Assert.IsTrue(LaneCoverCatalog.IsImage("a/b.bmp"));
            Assert.IsFalse(LaneCoverCatalog.IsImage("a/names.tsv"));
            Assert.IsFalse(LaneCoverCatalog.IsImage("a/readme.txt"));
            Assert.IsFalse(LaneCoverCatalog.IsImage(null));
        }

        [Test]
        public void RelativeId_Normalises_Separators_And_Rejects_Outsiders()
        {
            Assert.AreEqual("1/img_customize_051.jpg",
                            LaneCoverCatalog.RelativeId(@"C:\d\LANE", @"C:\d\LANE\1\img_customize_051.jpg"));
            Assert.AreEqual("default.jpg", LaneCoverCatalog.RelativeId("C:/d/LANE/", "C:/d/LANE/default.jpg"));
            Assert.IsNull(LaneCoverCatalog.RelativeId(@"C:\d\LANE", @"C:\d\OTHER\x.jpg"));
            Assert.IsNull(LaneCoverCatalog.RelativeId(@"C:\d\LANE", @"C:\d\LANE"));   // root 自己不是項目
        }

        [Test]
        public void StemOf_Falls_Back_To_The_File_Name()
        {
            Assert.AreEqual("img_customize_051", LaneCoverCatalog.StemOf("1/img_customize_051.jpg"));
            Assert.AreEqual("default", LaneCoverCatalog.StemOf("default.jpg"));
            Assert.AreEqual("no_ext", LaneCoverCatalog.StemOf("a/b/no_ext"));
        }

        [Test]
        public void ParseNames_Reads_Tab_Separated_Rows_And_Skips_Comments()
        {
            var m = LaneCoverCatalog.ParseNames(
                "# 註解\n" +
                "default.jpg\tDefault\n" +
                "1/img_customize_051.jpg\tsigsig\r\n" +
                "壞掉的一行沒有tab\n" +
                "\n" +
                "3/img_customize_033.jpg\tInitiation\n");
            Assert.AreEqual(3, m.Count);
            Assert.AreEqual("sigsig", m["1/img_customize_051.jpg"]);
            Assert.AreEqual("Initiation", m["3/img_customize_033.jpg"]);
            Assert.AreEqual("Default", m["DEFAULT.JPG"]);   // 路徑不分大小寫
        }

        [Test]
        public void ParseNames_Handles_Empty_And_Null()
        {
            Assert.AreEqual(0, LaneCoverCatalog.ParseNames(null).Count);
            Assert.AreEqual(0, LaneCoverCatalog.ParseNames("").Count);
        }

        // ---------------------------------------------------------------- 掃描

        [Test]
        public void Discover_Lists_Images_With_Names_From_The_Sidecar()
        {
            const string root = "C:/DATA/ADDON/LANE";
            var entries = LaneCoverCatalog.Discover(
                new[] { root }, Dirs(root),
                Fs(new Dictionary<string, string[]>
                {
                    [root] = new[]
                    {
                        root + "/names.tsv",                    // 對照表本身不是項目
                        root + "/default.jpg",
                        root + "/1/img_customize_051.jpg",
                        root + "/readme.txt",                   // 非圖檔略過
                    },
                }),
                Texts(new Dictionary<string, string>
                {
                    [root + "/names.tsv"] = "default.jpg\tDefault\n1/img_customize_051.jpg\tsigsig\n",
                }));

            Assert.AreEqual(2, entries.Count);
            Assert.AreEqual(new[] { "Default", "sigsig" }, entries.Select(e => e.Display).ToArray());   // 依名字排序
            Assert.AreEqual("1/img_customize_051.jpg", entries[1].Id);
            Assert.AreEqual(root + "/1/img_customize_051.jpg", entries[1].Path);
        }

        [Test]
        public void Discover_Without_A_Sidecar_Uses_File_Names()
        {
            const string root = "C:/LANE";
            var entries = LaneCoverCatalog.Discover(
                new[] { root }, Dirs(root),
                Fs(new Dictionary<string, string[]> { [root] = new[] { root + "/5/31.jpg" } }),
                _ => null);
            Assert.AreEqual(1, entries.Count);
            Assert.AreEqual("31", entries[0].Display);
            Assert.AreEqual("5/31.jpg", entries[0].Id);
        }

        [Test]
        public void Discover_Earlier_Root_Wins_The_Same_Id()
        {
            // 開發樹的圖要蓋掉打包進去的同名圖（跟 MMD 模型同一條規則）。
            const string a = "C:/A/LANE", b = "C:/B/LANE";
            var entries = LaneCoverCatalog.Discover(
                new[] { a, b }, Dirs(a, b),
                Fs(new Dictionary<string, string[]>
                {
                    [a] = new[] { a + "/default.jpg" },
                    [b] = new[] { b + "/default.jpg", b + "/only_here.png" },
                }),
                _ => null);
            Assert.AreEqual(2, entries.Count);
            Assert.AreEqual(a + "/default.jpg", entries.First(e => e.Id == "default.jpg").Path);
        }

        [Test]
        public void Discover_Skips_Missing_Roots_And_Survives_Nulls()
        {
            Assert.AreEqual(0, LaneCoverCatalog.Discover(null, _ => true, _ => null, _ => null).Count);
            Assert.AreEqual(0, LaneCoverCatalog.Discover(new[] { "C:/nope", null, "" },
                                                         Dirs(), _ => null, _ => null).Count);
        }

        // ---------------------------------------------------------------- 選項 / 對照

        [Test]
        public void OptionsOf_Puts_None_First()
        {
            var entries = Two();
            var opts = LaneCoverCatalog.OptionsOf(entries);
            Assert.AreEqual(3, opts.Length);
            Assert.AreEqual(RoomConfig.laneCoverNone, opts[0]);
            Assert.AreEqual("a.jpg", opts[1]);
            Assert.AreEqual("b/c.png", opts[2]);
            Assert.AreEqual(1, LaneCoverCatalog.OptionsOf(null).Length);   // 一張都沒裝 → 只剩「不使用」
        }

        [Test]
        public void DisplayOf_Covers_None_Known_And_Missing()
        {
            var entries = Two();
            Assert.AreEqual(RoomConfig.laneCoverNone, LaneCoverCatalog.DisplayOf(entries, RoomConfig.laneCoverNone));
            Assert.AreEqual(RoomConfig.laneCoverNone, LaneCoverCatalog.DisplayOf(entries, ""));
            Assert.AreEqual(RoomConfig.laneCoverNone, LaneCoverCatalog.DisplayOf(entries, null));
            Assert.AreEqual("Alpha", LaneCoverCatalog.DisplayOf(entries, "a.jpg"));
            Assert.AreEqual("gone", LaneCoverCatalog.DisplayOf(entries, "x/gone.jpg"));   // 圖被刪了 → 退回檔名
        }

        [Test]
        public void PathOf_Resolves_Known_Ids_Only()
        {
            var entries = Two();
            Assert.AreEqual("C:/L/a.jpg", LaneCoverCatalog.PathOf(entries, "a.jpg"));
            Assert.AreEqual("C:/L/a.jpg", LaneCoverCatalog.PathOf(entries, "A.JPG"));   // 不分大小寫
            Assert.IsNull(LaneCoverCatalog.PathOf(entries, RoomConfig.laneCoverNone));
            Assert.IsNull(LaneCoverCatalog.PathOf(entries, ""));
            Assert.IsNull(LaneCoverCatalog.PathOf(entries, "x/gone.jpg"));
            Assert.IsNull(LaneCoverCatalog.PathOf(null, "a.jpg"));
        }

        private static List<LaneCoverCatalog.Entry> Two() => new List<LaneCoverCatalog.Entry>
        {
            new LaneCoverCatalog.Entry("a.jpg", "Alpha", "C:/L/a.jpg"),
            new LaneCoverCatalog.Entry("b/c.png", "Beta", "C:/L/b/c.png"),
        };
    }
}
