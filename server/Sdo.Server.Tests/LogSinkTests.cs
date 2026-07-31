using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using Sdo.Server.Logging;

namespace Sdo.Tests
{
    /// <summary>
    /// log 落地的實際行為(真的碰檔案,用臨時目錄)。
    ///
    /// 這裡守的三件事都只有「時間真的過去」才看得到,不注入時鐘的話就只能上線後靠人工觀察 ——
    /// 而沒有人會在正式機上等到隔天午夜去確認換檔有沒有成功:
    ///   • 跨日換一個新檔,舊的留著
    ///   • 同一天寫爆一段就切下一段(否則 -v 開著時會長出一個大到刪不得的檔)
    ///   • 總量超過上限,從最舊的刪
    /// </summary>
    public class LogSinkTests
    {
        private string _dir;
        private DateTime _now;

        private const long Mb = 1024L * 1024L;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "sdo_log_" + Path.GetRandomFileName());
            _now = new DateTime(2026, 7, 31, 14, 3, 12);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        }

        private LogSink NewSink(long capBytes = 100 * Mb) => new LogSink(_dir, capBytes, () => _now);

        private string[] LogNames()
        {
            var names = new System.Collections.Generic.List<string>();
            foreach (var p in Directory.GetFiles(_dir)) names.Add(Path.GetFileName(p));
            names.Sort(StringComparer.Ordinal);
            return names.ToArray();
        }

        private string Read(string name) => File.ReadAllText(Path.Combine(_dir, name), Encoding.UTF8);

        private static void Write(LogSink sink, string line) { sink.Write(line); }

        [Test]
        public void A_Line_Lands_In_Todays_File()
        {
            using (var sink = NewSink())
                Write(sink, "2026-07-31 14:03:12 [sdo-server] 監聽 0.0.0.0:27015");

            CollectionAssert.AreEqual(new[] { "sdo-server-2026-07-31.log" }, LogNames());
            StringAssert.Contains("監聽 0.0.0.0:27015", Read("sdo-server-2026-07-31.log"));
        }

        [Test]
        public void The_Directory_Is_Created_If_Missing()
        {
            // 部署時 <data>/logs 不會事先存在(bootstrap 只建 data 與 blobs)。
            Assert.IsFalse(Directory.Exists(_dir));
            using (var sink = NewSink()) Write(sink, "hello");
            Assert.IsTrue(Directory.Exists(_dir));
        }

        [Test]
        public void Midnight_Starts_A_New_File_And_Keeps_The_Old_One()
        {
            using (var sink = NewSink())
            {
                Write(sink, "昨天的事");
                _now = _now.AddDays(1);          // 過午夜
                Write(sink, "今天的事");
            }

            CollectionAssert.AreEqual(new[] { "sdo-server-2026-07-31.log", "sdo-server-2026-08-01.log" }, LogNames());
            StringAssert.Contains("昨天的事", Read("sdo-server-2026-07-31.log"));
            StringAssert.Contains("今天的事", Read("sdo-server-2026-08-01.log"));
            StringAssert.DoesNotContain("今天的事", Read("sdo-server-2026-07-31.log"));
        }

        [Test]
        public void Restarting_The_Server_On_The_Same_Day_Appends_Instead_Of_Overwriting()
        {
            // 🔴 開機把當天的檔重開成空的話,「重啟前發生了什麼」就永遠沒了 ——
            //    而重啟的原因十之八九正是要查的那件事。
            using (var sink = NewSink()) Write(sink, "第一次啟動");
            using (var sink = NewSink()) Write(sink, "第二次啟動");

            string body = Read("sdo-server-2026-07-31.log");
            StringAssert.Contains("第一次啟動", body);
            StringAssert.Contains("第二次啟動", body);
        }

        [Test]
        public void A_Day_That_Fills_A_Segment_Rolls_To_The_Next_One()
        {
            // -v 開著時一天可以噴掉遠不只上限 —— 那時「一天一個檔」會變成一個超過上限、
            // 卻又因為是正在寫的檔而刪不得的東西。切段讓回收的粒度小於上限。
            string fat = new string('x', 8192);
            using (var sink = NewSink())
                for (int i = 0; i < (LogRetention.SegmentBytes / 8192) + 2; i++) Write(sink, fat);

            CollectionAssert.Contains(LogNames(), "sdo-server-2026-07-31.log");
            CollectionAssert.Contains(LogNames(), "sdo-server-2026-07-31.2.log");
        }

        [Test]
        public void Going_Over_The_Cap_Deletes_The_Oldest_Days()
        {
            // 上限 3 MB、每天 2 MB:寫到第三天就裝不下,最舊的那天要被刪掉。
            Directory.CreateDirectory(_dir);
            WriteFakeDay(new DateTime(2026, 7, 29), 2 * Mb);
            WriteFakeDay(new DateTime(2026, 7, 30), 2 * Mb);

            using (var sink = NewSink(3 * Mb)) Write(sink, "今天開工");

            CollectionAssert.DoesNotContain(LogNames(), "sdo-server-2026-07-29.log", "最舊的先走");
            CollectionAssert.Contains(LogNames(), "sdo-server-2026-07-31.log", "今天的當然留著");
        }

        [Test]
        public void The_Sweep_Says_What_It_Deleted()
        {
            Directory.CreateDirectory(_dir);
            WriteFakeDay(new DateTime(2026, 7, 29), 2 * Mb);

            using (var sink = NewSink(1 * Mb))
            {
                Write(sink, "今天開工");
                StringAssert.Contains("刪掉", sink.TakeNotice() ?? "",
                                      "刪了東西一定要留一行紀錄 —— 不然 log 會無聲地少掉一段時間");
                Assert.IsNull(sink.TakeNotice(), "取走之後就沒了,不會每行重覆講");
            }
        }

        [Test]
        public void Foreign_Files_In_The_Log_Directory_Survive()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, "important.txt"), new string('y', (int)(2 * Mb)));

            using (var sink = NewSink(1 * Mb)) Write(sink, "今天開工");

            CollectionAssert.Contains(LogNames(), "important.txt", "不是我們寫的檔,一根寒毛都不能碰");
        }

        private void WriteFakeDay(DateTime day, long bytes)
        {
            File.WriteAllText(Path.Combine(_dir, LogRetention.FileName(day, 1)), new string('z', (int)bytes));
        }
    }
}
