using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Server.Logging;

namespace Sdo.Tests
{
    /// <summary>
    /// log 檔的命名與「總量超過 100 MB 就從舊的開始刪」的決策。
    ///
    /// 為什麼這一塊值得逐條測試:它會**刪磁碟上的檔案**,而且錯的方向不對稱 ——
    /// 少刪只是佔空間,多刪(或刪錯檔)會把正在查的那天的證據弄不見,
    /// 而症狀是「log 裡什麼都沒有」,和「那件事根本沒發生」長得一模一樣。
    /// </summary>
    public class LogRetentionTests
    {
        private const long Mb = 1024L * 1024L;

        private static LogFileInfo F(string name, long bytes) => new LogFileInfo { Name = name, Bytes = bytes };

        private static LogFileInfo Day(int month, int day, long bytes, int seq = 1)
            => F(LogRetention.FileName(new DateTime(2026, month, day), seq), bytes);

        [Test]
        public void The_First_File_Of_A_Day_Has_No_Sequence_Number()
        {
            // 絕大多數日子只有一個檔,檔名就該是乾淨的日期 —— 那是人要用眼睛找的東西。
            Assert.AreEqual("sdo-server-2026-07-31.log", LogRetention.FileName(new DateTime(2026, 7, 31), 1));
            Assert.AreEqual("sdo-server-2026-07-31.2.log", LogRetention.FileName(new DateTime(2026, 7, 31), 2));
        }

        [Test]
        public void A_Name_Round_Trips_Back_To_Its_Day_And_Sequence()
        {
            DateTime day; int seq;
            Assert.IsTrue(LogRetention.TryParseName("sdo-server-2026-07-31.log", out day, out seq));
            Assert.AreEqual(new DateTime(2026, 7, 31), day);
            Assert.AreEqual(1, seq, "沒有序號 = 第 1 段");

            Assert.IsTrue(LogRetention.TryParseName("sdo-server-2026-07-31.12.log", out day, out seq));
            Assert.AreEqual(12, seq);
        }

        [Test]
        public void Foreign_File_Names_Are_Not_Recognised()
        {
            DateTime day; int seq;
            Assert.IsFalse(LogRetention.TryParseName("server.log", out day, out seq));
            Assert.IsFalse(LogRetention.TryParseName("sdo-server-2026-13-31.log", out day, out seq), "13 月");
            Assert.IsFalse(LogRetention.TryParseName("sdo-server-2026-07-31.log.bak", out day, out seq));
            Assert.IsFalse(LogRetention.TryParseName("sdo-server-2026-07-31.1.log", out day, out seq),
                           ".1 不是我們會產的名字(第 1 段不帶序號),認了會讓同一段有兩個名字");
        }

        [Test]
        public void Files_We_Did_Not_Write_Are_Never_Deleted()
        {
            // 🔴 --log-dir 可以被指到任何目錄(包括有別的東西在裡面的)。認不得的檔名一律不碰,
            //    否則這個功能就成了「幫使用者刪掉他自己的檔案」。
            var plan = LogRetention.Plan(new[]
            {
                F("important.txt", 500 * Mb),
                F("server.log", 500 * Mb),
                Day(7, 31, 1 * Mb),
            }, 10 * Mb, null);

            CollectionAssert.IsEmpty(plan);
        }

        [Test]
        public void Nothing_Is_Deleted_While_Under_The_Cap()
        {
            var plan = LogRetention.Plan(new[] { Day(7, 29, 30 * Mb), Day(7, 30, 30 * Mb), Day(7, 31, 30 * Mb) },
                                         LogRetention.DefaultCapBytes, null);
            CollectionAssert.IsEmpty(plan, "90 MB < 100 MB,什麼都不該動");
        }

        [Test]
        public void Over_The_Cap_The_Oldest_Days_Go_First()
        {
            var plan = LogRetention.Plan(new[]
            {
                Day(7, 28, 40 * Mb),
                Day(7, 29, 40 * Mb),
                Day(7, 30, 40 * Mb),
                Day(7, 31, 40 * Mb),
            }, LogRetention.DefaultCapBytes, null);

            // 100 MB 裝得下最新的兩天(80 MB),第三天就滿了 → 它與更舊的一起走。
            CollectionAssert.AreEquivalent(
                new[] { "sdo-server-2026-07-29.log", "sdo-server-2026-07-28.log" }, plan);
        }

        [Test]
        public void Once_The_Cap_Is_Hit_Every_Older_File_Goes_Even_If_It_Is_Small()
        {
            // 跳過大檔、留下更舊的小檔的話,保留下來的日期會有洞 ——
            // 而查問題時「這天沒有 log」與「這天 server 沒開」看起來一樣。
            var plan = LogRetention.Plan(new[]
            {
                Day(7, 31, 9 * Mb),
                Day(7, 30, 9 * Mb),
                Day(7, 29, 1 * Mb),     // 小,但比那個撐爆上限的還舊
            }, 10 * Mb, null);

            CollectionAssert.AreEqual(
                new[] { "sdo-server-2026-07-30.log", "sdo-server-2026-07-29.log" }, plan);
        }

        [Test]
        public void The_File_Being_Written_Right_Now_Is_Never_Deleted()
        {
            // 🔴 今天的檔一個人就超過上限時,刪掉它 = 把今天的 log 整個丟掉(而且是在正在寫的時候)。
            string today = LogRetention.FileName(new DateTime(2026, 7, 31), 1);
            var plan = LogRetention.Plan(new[] { F(today, 200 * Mb), Day(7, 30, 1 * Mb) },
                                         LogRetention.DefaultCapBytes, today);

            CollectionAssert.AreEqual(new[] { "sdo-server-2026-07-30.log" }, plan);
            CollectionAssert.DoesNotContain(plan, today);
        }

        [Test]
        public void Segments_Of_The_Same_Day_Are_Ordered_By_Number_Not_By_Name()
        {
            // 字串排序會把 .10 排在 .2 前面 → 刪掉的是「最新的那一段」,正好相反。
            var plan = LogRetention.Plan(new[]
            {
                Day(7, 31, 6 * Mb, 10),
                Day(7, 31, 6 * Mb, 2),
                Day(7, 31, 6 * Mb, 3),
            }, 12 * Mb, null);

            CollectionAssert.AreEqual(new[] { "sdo-server-2026-07-31.2.log" }, plan,
                                      "第 10 段與第 3 段是較新的兩段,該走的是第 2 段");
        }

        [Test]
        public void A_Cap_Of_Zero_Means_Unlimited_Not_Delete_Everything()
        {
            // Plan 只負責「超過就刪」;--log-mb 0 的意思是不寫檔(在上層擋掉),
            // 絕不能在這裡被解讀成「上限 0 → 全部刪光」。
            var plan = LogRetention.Plan(new[] { Day(7, 30, 500 * Mb), Day(7, 31, 500 * Mb) }, 0, null);
            CollectionAssert.IsEmpty(plan);
        }

        [Test]
        public void No_Files_At_All_Is_Fine()
        {
            CollectionAssert.IsEmpty(LogRetention.Plan(new List<LogFileInfo>(), LogRetention.DefaultCapBytes, null));
            CollectionAssert.IsEmpty(LogRetention.Plan(null, LogRetention.DefaultCapBytes, null));
        }
    }
}
