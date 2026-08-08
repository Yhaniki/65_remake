using System.IO;
using System.Linq;
using NUnit.Framework;
using Sdo.Game;
using Sdo.Osu;

namespace Sdo.Tests
{
    /// <summary>
    /// 歌曲資料夾自帶的**客製編舞**:sidecar 的 <c>#DPS</c> 指到使用者自己放進去的 .dps 時,那一支永遠照跳。
    ///
    /// 這條測試存在的原因是一個實機 bug:玩家把自己編的 <c>12951.DPS</c>(連同它點名的客製 WDANCE*.MOT)放進歌曲
    /// 資料夾、在 sdoinfo.dat 手寫 <c>#DPS:12951.DPS;</c>,遊戲卻無視它,自己生了 <c>dance.dps</c> 還把那一行覆寫掉。
    /// 兇手是 <c>#DPSVER</c>:手寫當然不會帶版本號 → 讀成 0 → 被當成「舊版產生器造的舞」→ 汰換重生成。
    /// 版本號的意思是「**我這一版產生器**造的」,對別人手寫的檔案沒有發言權。
    ///
    /// 判準是檔名(<see cref="SongSidecar.IsGeneratedDpsName"/>):dance.dps / dance_&lt;…&gt;.dps 是我們生的,
    /// 其他都是使用者的。
    /// </summary>
    public class ExternalDpsCustomDpsTests
    {
        private string _folder;

        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(Path.GetTempPath(), "sdo_customdps_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_folder);
        }

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); } catch { }
        }

        // 夠長(> MinDanceSeconds)的一張譜:短到 EnsureFor 提早收工的話就測不到重生成那條分支了。
        private static OsuBeatmap Map()
        {
            var m = new OsuBeatmap { Bpm = 120.0 };
            m.HitObjects.Add(new OsuHitObject(0, 0));
            m.HitObjects.Add(new OsuHitObject(1, 30000));
            return m;
        }

        private string Dps(string name)
        {
            string path = Path.Combine(_folder, name);
            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });   // 內容不重要:EnsureFor 只看「在不在」
            return path;
        }

        private void Sidecar(string text) => File.WriteAllText(Path.Combine(_folder, SongSidecar.FileName), text);

        private string SidecarText() => File.ReadAllText(Path.Combine(_folder, SongSidecar.FileName));

        private string Ensure()
            => ExternalDps.EnsureFor(_folder, "", "", Map(), 120.0, (int)SongFormat.Osu, 0L, null, null);

        // ---- 使用者自帶的 .dps ----

        [Test]
        public void A_Hand_Written_Dps_Is_Used_Even_Without_A_DPSVER()
        {
            // 實機那份 sdoinfo.dat 就長這樣:手寫一行 #DPS,沒有 #DPSVER。
            string custom = Dps("12951.DPS");
            Sidecar("#VERSION:1;\n#SONG:;\n#DPS:12951.DPS;\n");

            Assert.AreEqual(custom, Ensure(), "手寫指名的客製編舞就是要跳這一支");
        }

        [Test]
        public void A_Hand_Written_Dps_Survives_An_Old_DPSVER()
        {
            // 版本號就算寫了、而且是舊的,也管不到不是我們造的檔案。
            string custom = Dps("mydance.dps");
            Sidecar("#SONG:;\n#DPS:mydance.dps;\n#DPSVER:1;\n");

            Assert.AreEqual(custom, Ensure());
        }

        [Test]
        public void Using_A_Hand_Written_Dps_Does_Not_Rewrite_The_Sidecar()
        {
            // 覆寫那一行 = 使用者下次打開檔案發現自己的設定不見了(bug 當時就是這樣)。
            Dps("12951.DPS");
            const string text = "#SONG:;\n#DPS:12951.DPS;\n#DPSOFFSETMS:-3000;\n";
            Sidecar(text);

            Ensure();

            Assert.AreEqual(text, SidecarText(), "自帶編舞不重生成,也就不該動 sidecar");
            Assert.IsFalse(File.Exists(Path.Combine(_folder, "dance.dps")), "不該偷生一支");
        }

        [Test]
        public void A_Hand_Written_Dps_That_Is_Not_There_Falls_Back_To_A_Generated_One()
        {
            // 打錯字 → 沒有舞可跳。退回生成(或退回 "" = fallback clip),但**絕不**回一個不存在的路徑。
            Sidecar("#SONG:;\n#DPS:typo.dps;\n");

            string got = Ensure();
            Assert.AreNotEqual(Path.Combine(_folder, "typo.dps"), got);
            if (got.Length > 0) Assert.IsTrue(File.Exists(got), "回傳的路徑一定要真的有檔案");
        }

        // ---- 我們自己生的 .dps:版本汰換照舊 ----

        [Test]
        public void Our_Own_Dance_Is_Reused_At_The_Current_Generator_Version()
        {
            string generated = Dps("dance.dps");
            Sidecar(SongSidecar.SetDps("", "", "dance.dps"));

            Assert.AreEqual(generated, Ensure());
        }

        [Test]
        public void Our_Own_Dance_Is_Rebuilt_When_The_Generator_Moved_On()
        {
            // 產生器修好了,已經跳過的歌也要吃到 —— 這條行為不能被上面的「尊重自帶」改掉。
            // 重建是**就地覆寫同一個檔名**(dance.dps 是 DpsFileName 給的名字),所以看的是內容和版本戳,不是路徑。
            string stale = Dps("dance.dps");
            byte[] before = File.ReadAllBytes(stale);
            Sidecar(SongSidecar.SetDps("", "", "dance.dps", SongSidecar.DpsGenerator - 1));

            string got = Ensure();

            // 資料樹沒有編舞素材(DPSINDEX.TXT)時什麼都生不出來 → "" = 退回 fallback clip,一樣沒有沿用舊舞。
            if (got.Length == 0) return;

            Assert.AreEqual(stale, got, "重建就地覆寫同一個檔名");
            CollectionAssert.AreNotEqual(before, File.ReadAllBytes(got), "檔案內容要換成新產生器造的");
            var entry = SongSidecar.Find(SongSidecar.Parse(SidecarText()), "");
            Assert.AreEqual(SongSidecar.DpsGenerator, entry.DpsVersion, "版本戳要跟著更新,否則每次開歌都重生成一次");
        }

        // ---- 檔名判準 ----

        [Test]
        public void Only_Our_Naming_Scheme_Counts_As_Generated()
        {
            Assert.IsTrue(SongSidecar.IsGeneratedDpsName("dance.dps"));
            Assert.IsTrue(SongSidecar.IsGeneratedDpsName("DANCE.DPS"), "檔名不分大小寫");
            Assert.IsTrue(SongSidecar.IsGeneratedDpsName(SongSidecar.DpsFileName("audio:song.mp3")),
                          "多首歌資料夾的 dance_<slug>_<hash>.dps 也是我們生的");
            Assert.IsTrue(SongSidecar.IsGeneratedDpsName(SongSidecar.DpsFileName("恋.mp3")), "CJK key 走 hash-only 命名");

            Assert.IsFalse(SongSidecar.IsGeneratedDpsName("12951.DPS"));
            Assert.IsFalse(SongSidecar.IsGeneratedDpsName("mydance.dps"));
            Assert.IsFalse(SongSidecar.IsGeneratedDpsName(""));
            Assert.IsFalse(SongSidecar.IsGeneratedDpsName(null));
        }

        [Test]
        public void The_Transfer_Filter_Shares_The_Same_Verdict()
        {
            // 🔴 兩份判定分家的話:傳檔會把收端自己會重生的 dance.dps 傳過去,或反過來把使用者的客製舞當成
            // 生成物濾掉。SongPackFilter 走的就是 SongSidecar 這一份。
            Assert.IsTrue(SongPackFilter.IsGenerated("dance.dps"));
            Assert.IsTrue(SongPackFilter.IsGenerated(SongSidecar.DpsFileName("audio:song.mp3")));
            Assert.IsFalse(SongPackFilter.IsGenerated("12951.DPS"), "客製編舞是使用者的檔案,不是生成物");
        }

        // ---- 缺歌傳檔:客製編舞要跟著過去 ----

        [Test]
        public void The_Custom_Dance_Its_Clips_And_The_Sidecar_Are_All_Transferable()
        {
            // 🔴 「不是生成物」還不夠 —— .dps/.mot 也得在**白名單**裡,否則它們是 UnknownType,
            // 連上傳清單都進不去;而 sidecar 是指到它們的唯一指標,一起漏掉的話收端拿到檔案也沒人指它。
            // 實機症狀:同一場裡房主跳作者編的舞,下載到這首歌的人跳的是亂數生成的那一支。
            Assert.IsTrue(SongPackFilter.IsTransferable("12951.dps", 4096), "客製編舞要傳");
            Assert.IsTrue(SongPackFilter.IsTransferable("WDANCE0272.MOT", 40960), "它點名的動作片段也要傳");
            Assert.IsTrue(SongPackFilter.IsTransferable(SongSidecar.FileName, 512), "指標(sidecar)也要傳");
            Assert.IsFalse(SongPackFilter.IsTransferable("dance.dps", 4096), "生成的那一支仍然由收端自己重生");
        }

        [Test]
        public void A_Custom_Dance_Survives_A_Round_Trip_Through_The_Transfer_Filter()
        {
            // 端到端:把「過得了過濾器」的檔案複製到另一個資料夾(= 收端下載後的樣子,檔名依
            // SafeRelPath.Normalize 全部轉小寫),那邊的 EnsureFor 必須挑到同一支客製編舞。
            Dps("12951.DPS");
            File.WriteAllBytes(Path.Combine(_folder, "WDANCE0272.MOT"), new byte[] { 9, 9, 9, 9 });
            File.WriteAllText(Path.Combine(_folder, "song.osu"), "osu file format v14\n");
            Sidecar("#VERSION:1;\n#SONG:;\n#DPS:12951.DPS;\n#MOT:WDANCE0272.MOT;\n#DPSOFFSETMS:-3000;\n");

            string dest = Path.Combine(Path.GetTempPath(), "sdo_customdps_rx_" + Path.GetRandomFileName());
            Directory.CreateDirectory(dest);
            try
            {
                foreach (var abs in Directory.GetFiles(_folder))
                {
                    string rel = SafeRelPath.Normalize(Path.GetFileName(abs));
                    if (!SongPackFilter.IsTransferable(rel, new FileInfo(abs).Length)) continue;
                    File.Copy(abs, Path.Combine(dest, rel));
                }

                CollectionAssert.Contains(Directory.GetFiles(dest).Select(Path.GetFileName).ToArray(), "12951.dps");
                CollectionAssert.Contains(Directory.GetFiles(dest).Select(Path.GetFileName).ToArray(), "wdance0272.mot");
                CollectionAssert.Contains(Directory.GetFiles(dest).Select(Path.GetFileName).ToArray(), SongSidecar.FileName);

                string got = ExternalDps.EnsureFor(dest, "", "", Map(), 120.0, (int)SongFormat.Osu, 0L, null, null);
                Assert.AreEqual("12951.DPS", Path.GetFileName(got), "收端要跳作者編的那一支,不是自己生的");
                Assert.IsTrue(File.Exists(got));
                Assert.IsFalse(File.Exists(Path.Combine(dest, "dance.dps")), "不該在收端偷生一支");

                // 手調過的舞蹈 offset 也要跟著過去 —— 不然兩邊的舞跟音樂對不在同一個點上。
                var entry = SongSidecar.Find(SongSidecar.Parse(File.ReadAllText(Path.Combine(dest, SongSidecar.FileName))), "");
                Assert.AreEqual(-3000f, entry.DpsOffsetMs);
            }
            finally
            {
                try { Directory.Delete(dest, true); } catch { }
            }
        }
    }
}
