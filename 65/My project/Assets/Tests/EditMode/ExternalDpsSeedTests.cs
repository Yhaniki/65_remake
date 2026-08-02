using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// 生成舞蹈的 RNG seed（<see cref="ExternalDps.SeedFor"/>）＝**三張難度譜的內容**，其他一概不看。
    ///
    /// 這條測試存在的原因是一個實機 bug:房主選了一首外部歌，缺歌的人自動下載補齊，兩個人**同一場同一首歌**
    /// 卻跳完全不同的舞。原因是 seed 吃「資料夾名」——
    ///   • .dps 不隨檔案傳(SongPackFilter 排掉 dance*.dps，收端自己重生)；
    ///   • 收端的資料夾叫 <c>ADDON/SONG/connect/&lt;歌名 - 作者 [packId 前8碼]&gt;/</c>，
    ///     持有原檔那邊叫什麼根本沒在協定裡傳;
    ///   → 兩邊 seed 不同 → xorshift 第一抽就分岔 → 整支舞不一樣。
    ///
    /// 譜面是傳檔時**唯一逐位元組驗過 SHA-256** 的東西(SongPackId.NeedsContentHash)，也正好是舞蹈長度/BPM 的
    /// 來源(DanceInputs)——「會改變舞蹈的東西全在 seed 裡，不會改變舞蹈的東西全不在」。
    /// </summary>
    public class ExternalDpsSeedTests
    {
        private const string Pack = "sha256:d4b93b4b6196374e423b1142abd96a1c";

        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "sdo_dpsseed_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        }

        /// <summary>在 <paramref name="folder"/>（相對 _root，自動建）底下寫一張譜，回傳絕對路徑。</summary>
        private string Chart(string folder, string name, string body)
        {
            string dir = Path.Combine(_root, folder);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name);
            File.WriteAllText(path, body);
            return path;
        }

        private static uint Seed(IReadOnlyList<string> paths, IReadOnlyList<int> indices,
                                 string folderPath, string songKey = "", string packId = "")
            => ExternalDps.SeedFor(paths, indices, folderPath, songKey, packId);

        private static readonly int[] Osu = { 0, 0, 0 };   // osu：三個難度是三個檔，index 都 0

        // ---- 傳檔的兩端 ----

        [Test]
        public void The_Two_Sides_Of_A_Transfer_Seed_The_Same_Dance()
        {
            // 房主手上的原始資料夾 vs 缺歌的人下載後落地的資料夾:資料夾名、檔名、packId 全都不同,
            // 但三張譜是逐位元組相同的那三張(傳檔會驗 SHA-256)。
            const string easy = "// easy\n1,2,3", normal = "// normal\n4,5,6", hard = "// hard\n7,8,9";
            var owner = new[]
            {
                Chart("1234567 Artist - Song", "Song [EASY].osu", easy),
                Chart("1234567 Artist - Song", "Song [NORMAL].osu", normal),
                Chart("1234567 Artist - Song", "Song [HARD].osu", hard),
            };
            var receiver = new[]
            {
                Chart("Song - Artist [d4b93b4b]", "Song [EASY].osu", easy),
                Chart("Song - Artist [d4b93b4b]", "Song [NORMAL].osu", normal),
                Chart("Song - Artist [d4b93b4b]", "Song [HARD].osu", hard),
            };

            Assert.AreEqual(Seed(owner, Osu, Path.Combine(_root, "1234567 Artist - Song"), "", Pack),
                            Seed(receiver, Osu, Path.Combine(_root, "Song - Artist [d4b93b4b]"), "", "sha256:0badf00d"),
                            "同樣三張譜 → 同一支舞,資料夾名/packId 都不該有發言權");
        }

        [Test]
        public void Renaming_The_Chart_Files_Changes_Nothing()
        {
            var a = new[] { Chart("a", "x.osu", "chart") };
            var b = new[] { Chart("b", "完全不同的檔名.osu", "chart") };
            Assert.AreEqual(Seed(a, Osu, Path.Combine(_root, "a")), Seed(b, Osu, Path.Combine(_root, "b")));
        }

        [Test]
        public void Slot_Order_Does_Not_Matter()
        {
            // 🔴 哪張譜排進簡單/普通/困難是**每台自己的設定**(RoomConfig.difficultyCalc:minacalc / osu),
            // 兩個人手上同樣三張譜、槽的順序卻可能不同 —— 指紋因此是「集合」而不是「序列」。
            string x = Chart("s", "x.osu", "AAA"), y = Chart("s", "y.osu", "BBB"), z = Chart("s", "z.osu", "CCC");
            string folder = Path.Combine(_root, "s");
            Assert.AreEqual(Seed(new[] { x, y, z }, Osu, folder), Seed(new[] { z, x, y }, Osu, folder));
        }

        [Test]
        public void A_Multi_Difficulty_Single_File_Splits_On_Its_Index()
        {
            // .sm / .gn：三個難度在同一個檔裡，靠 index 分。整首歌的身分要包含「用了哪些 index」。
            string sm = Chart("s", "song.sm", "#NOTES...");
            string folder = Path.Combine(_root, "s");
            var three = new[] { sm, sm, sm };

            Assert.AreNotEqual(Seed(three, new[] { 0, 1, 2 }, folder), Seed(three, new[] { 0, 1, 3 }, folder),
                               "換了一個難度區塊 = 換了一首歌的內容");
            Assert.AreEqual(Seed(three, new[] { 0, 1, 2 }, folder), Seed(three, new[] { 2, 1, 0 }, folder),
                            "順序仍然不算");
            Assert.AreEqual(Seed(new[] { sm, sm }, new[] { 1, 1 }, folder), Seed(new[] { sm }, new[] { 1 }, folder),
                            "同檔同 index 重複出現只算一次");
        }

        [Test]
        public void Editing_A_Chart_Is_A_Different_Song()
        {
            var before = new[] { Chart("s", "x.osu", "notes v1") };
            string folder = Path.Combine(_root, "s");
            uint a = Seed(before, Osu, folder);
            File.WriteAllText(before[0], "notes v2");
            Assert.AreNotEqual(a, Seed(before, Osu, folder), "譜改了就是另一支舞 —— 這是對的");
        }

        [Test]
        public void Adding_A_Difficulty_Is_A_Different_Song()
        {
            string e = Chart("s", "e.osu", "E"), n = Chart("s", "n.osu", "N");
            string folder = Path.Combine(_root, "s");
            Assert.AreNotEqual(Seed(new[] { e }, Osu, folder), Seed(new[] { e, n }, Osu, folder));
        }

        [Test]
        public void Two_Different_Songs_Do_Not_Share_A_Dance()
        {
            Assert.AreNotEqual(Seed(new[] { Chart("s", "a.osu", "AAA") }, Osu, Path.Combine(_root, "s")),
                               Seed(new[] { Chart("s", "b.osu", "BBB") }, Osu, Path.Combine(_root, "s")));
        }

        // ---- 一張譜都讀不到時的兩層退路 ----

        [Test]
        public void Unreadable_Charts_Fall_Back_To_The_PackId()
        {
            var missing = new[] { Path.Combine(_root, "nope.osu"), "", null };
            Assert.AreEqual(Seed(missing, Osu, @"D:\osu!\Songs\my song", "", Pack),
                            Seed(missing, Osu, @"C:\connect\my song - artist [d4b93b4b]", "", Pack),
                            "沒有譜可讀 → 退回 packId，那仍然是跨電腦一致的");
            Assert.AreNotEqual(Seed(missing, Osu, @"D:\a", "", Pack),
                               Seed(missing, Osu, @"D:\a", "", "sha256:0000000000000000000000000000ffff"));
        }

        [Test]
        public void Without_Charts_Or_PackId_The_Folder_Name_Is_The_Last_Resort()
        {
            uint moved = Seed(null, null, @"E:\backup\Songs\my song");
            Assert.AreEqual(Seed(null, null, @"D:\osu!\Songs\my song"), moved,
                            "退到最後一層時只看葉名：整個歌庫搬碟不該重編舞");
            Assert.AreEqual(moved, Seed(null, null, @"D:\osu!\Songs\MY SONG\"), "葉名不分大小寫，結尾的分隔符也不算");
            Assert.AreNotEqual(moved, Seed(null, null, @"D:\osu!\Songs\another song"));
        }

        [Test]
        public void A_Multi_Song_Folder_Splits_On_The_SongKey_Only_In_The_Fallbacks()
        {
            // 退路要靠 songKey 分開同資料夾的多首歌;走譜面內容時不必 —— 不同的歌本來就是不同的譜。
            Assert.AreNotEqual(Seed(null, null, @"D:\s", "audio:a.mp3", Pack), Seed(null, null, @"D:\s", "audio:b.mp3", Pack));
            Assert.AreNotEqual(Seed(null, null, @"D:\s", "audio:a.mp3"), Seed(null, null, @"D:\s", "audio:b.mp3"));

            string a = Chart("s", "a.osu", "AAA");
            string folder = Path.Combine(_root, "s");
            Assert.AreEqual(Seed(new[] { a }, Osu, folder, "audio:a.mp3"), Seed(new[] { a }, Osu, folder, "audio:b.mp3"),
                            "同一張譜配不同音檔 = 同樣的長度、同樣的 BPM → 同一支舞才對");
        }
    }
}
