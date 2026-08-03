using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Net.Server;

namespace Sdo.Tests
{
    /// <summary>
    /// 公網化的三道防線(M10):token 認證、來源限制、上傳配額。
    ///
    /// 這三個都是「**開在公網之前**必須成立」的東西,而它們共同的特徵是:
    /// 寫錯的時候**什麼事都不會發生** —— 沒有錯誤訊息、沒有崩潰,只是防線不存在。
    /// 所以每一條規則都要有一個測試明確地說「這個要被擋下來」。
    /// </summary>
    public class AuthTokensTests
    {
        [Test]
        public void With_No_Token_File_Auth_Is_Disabled_And_Everyone_Passes()
        {
            // LAN 用的人不該被迫先產生 token 才能玩 → 沒有 token 檔就完全回到 MVP 行為。
            var a = new AuthTokens();
            Assert.AreEqual(0, a.Load(null));
            Assert.IsFalse(a.Enabled);
            AuthIdentity id;
            Assert.IsTrue(a.TryAuth(null, out id), "停用時一律放行");
            Assert.IsFalse(id.HasPlayerId, "身分留空 = 沿用 client 自稱的");
        }

        [Test]
        public void A_Known_Token_Passes_And_An_Unknown_One_Does_Not()
        {
            var a = new AuthTokens();
            Assert.AreEqual(1, a.Load("abcdef0123456789abcdef"));
            Assert.IsTrue(a.Enabled);

            AuthIdentity id;
            Assert.IsTrue(a.TryAuth("abcdef0123456789abcdef", out id));
            Assert.IsFalse(a.TryAuth("something-else-entirely", out id), "不認得的 token 要被擋");
            Assert.IsFalse(a.TryAuth("", out id), "空 token 要被擋");
            Assert.IsFalse(a.TryAuth(null, out id));
        }

        [Test]
        public void A_Token_Can_Bind_The_Identity_So_The_Client_Cannot_Claim_It()
        {
            // 🔴 這是整個 M10 的重點:身分由 server 決定。
            // 少了這一步,token 只是「第二個密碼」—— 任何持有它的人還是能自稱是別人。
            var a = new AuthTokens();
            a.Load("0123456789abcdef0000 = 00000001, 小明, admin");

            AuthIdentity id;
            Assert.IsTrue(a.TryAuth("0123456789abcdef0000", out id));
            Assert.AreEqual("00000001", id.PlayerId);
            Assert.AreEqual("小明", id.Name);
            Assert.IsTrue(id.Admin);
        }

        [Test]
        public void Comments_Blank_Lines_And_Partial_Bindings_Parse()
        {
            var a = new AuthTokens();
            int n = a.Load("# 這是註解\n\n"
                           + "aaaaaaaaaaaaaaaaaaaa\n"                 // 只給 token
                           + "bbbbbbbbbbbbbbbbbbbb = 00000002\n"       // 只綁 playerId
                           + "   \n"
                           + "cccccccccccccccccccc = 00000003, 阿花\n");
            Assert.AreEqual(3, n);

            AuthIdentity id;
            a.TryAuth("aaaaaaaaaaaaaaaaaaaa", out id);
            Assert.IsFalse(id.HasPlayerId, "沒綁就是沒綁");
            a.TryAuth("bbbbbbbbbbbbbbbbbbbb", out id);
            Assert.AreEqual("00000002", id.PlayerId);
            Assert.IsFalse(id.HasName);
            a.TryAuth("cccccccccccccccccccc", out id);
            Assert.AreEqual("阿花", id.Name);
        }

        [Test]
        public void Short_Tokens_Are_Rejected_With_A_Reason_That_Does_Not_Leak_Them()
        {
            // 太短的 token 等於沒有(猜得出來)。而問題訊息會進日誌 → **不能包含 token 本身**
            // (日誌常被貼到 issue 或截圖分享,那正是密碼那邊已經處理過的同一個顧慮)。
            var a = new AuthTokens();
            var problems = new List<string>();
            Assert.AreEqual(0, a.Load("short\nalsoshort = 00000001", problems));
            Assert.AreEqual(2, problems.Count);
            foreach (var p in problems)
            {
                StringAssert.DoesNotContain("short", p, "問題訊息不可以洩漏 token 內容");
                StringAssert.Contains("太短", p);
            }
        }

        [Test]
        public void A_Duplicate_Token_Is_Ignored_Rather_Than_Silently_Overwriting()
        {
            // 靜默覆蓋的話「同一個 token 在檔案裡出現兩次、綁不同的人」會取後面那個 ——
            // 而編輯檔案的人以為是前面那個。寧可忽略並記一筆。
            var a = new AuthTokens();
            var problems = new List<string>();
            Assert.AreEqual(1, a.Load("dddddddddddddddddddd = 00000001\n"
                                      + "dddddddddddddddddddd = 00000009", problems));
            AuthIdentity id;
            a.TryAuth("dddddddddddddddddddd", out id);
            Assert.AreEqual("00000001", id.PlayerId, "保留第一筆");
            Assert.AreEqual(1, problems.Count);
        }
    }

    public class OriginPolicyTests
    {
        [Test]
        public void With_No_Allow_List_Anyone_Can_Connect()
        {
            var p = new OriginPolicy();
            Assert.IsTrue(p.Allows("203.0.113.7"), "沒設名單 = LAN 的預設行為,誰都可以連");
        }

        [Test]
        public void An_Exact_Address_And_A_Prefix_Range_Both_Work()
        {
            var p = new OriginPolicy();
            p.SetAllowList("127.0.0.1, 192.168.0.");
            Assert.IsTrue(p.Allows("127.0.0.1"));
            Assert.IsTrue(p.Allows("192.168.0.42"), "以 . 結尾 = 前綴網段");
            Assert.IsFalse(p.Allows("192.168.1.42"), "隔壁網段不算");
            Assert.IsFalse(p.Allows("203.0.113.7"));
            Assert.IsFalse(p.Allows(""), "拿不到位址時要拒絕,不是放行");
            Assert.IsFalse(p.Allows(null));
        }

        [Test]
        public void The_Per_Ip_Connection_Cap_Leaves_Room_For_Both_Connections_Of_One_Client()
        {
            // 一份 client 正常用 2 條(control + file)。上限設得太低會讓正常玩家的傳檔連不上,
            // 而那看起來像「傳檔壞了」。
            var p = new OriginPolicy();
            Assert.GreaterOrEqual(OriginPolicy.DefaultMaxPerIp, 4, "預設上限至少要容得下一份 client 的兩條 + 餘裕");
            Assert.IsTrue(p.AllowsAnother(0));
            Assert.IsTrue(p.AllowsAnother(OriginPolicy.DefaultMaxPerIp - 1));
            Assert.IsFalse(p.AllowsAnother(OriginPolicy.DefaultMaxPerIp), "滿了就不能再加");

            p.MaxPerIp = 0;
            Assert.IsTrue(p.AllowsAnother(9999), "<=0 = 不限");
        }

        [Test]
        public void Ipv6_Endpoints_Parse_Without_Mangling_The_Address()
        {
            // 🔴 直接找最後一個冒號會把 IPv6 位址切壞 → 每條 IPv6 連線都被算成不同的 IP,
            //    per-IP 上限完全失效(而且不會有任何錯誤)。
            Assert.AreEqual("::1", OriginPolicy.IpOf("[::1]:52345"));
            Assert.AreEqual("2001:db8::1", OriginPolicy.IpOf("[2001:db8::1]:27015"));
            Assert.AreEqual("192.168.0.5", OriginPolicy.IpOf("192.168.0.5:27015"));
            Assert.AreEqual("192.168.0.5", OriginPolicy.IpOf("192.168.0.5"));
            Assert.AreEqual("", OriginPolicy.IpOf(null));
        }
    }

    public class UploadQuotaTests
    {
        private const long Hour = 3600L * 1000L;
        private const long T0 = 1_700_000_000_000L;

        [Test]
        public void Disabled_By_Default_So_Lan_Behaviour_Is_Unchanged()
        {
            var q = new UploadQuota();
            Assert.IsFalse(q.Enabled);
            Assert.IsTrue(q.Allows(1, long.MaxValue / 2, T0));
            Assert.AreEqual(long.MaxValue, q.Remaining(1, T0));
        }

        [Test]
        public void Uploads_Accumulate_Until_The_Hourly_Cap_Is_Reached()
        {
            // 擋的是「一個人反覆上傳不同的歌」:每一包都合法,加起來能把磁碟吃光。
            var q = new UploadQuota { BytesPerHour = 1000 };
            Assert.IsTrue(q.Allows(1, 600, T0));
            q.Add(1, 600, T0);
            Assert.AreEqual(600, q.Used(1, T0));
            Assert.IsTrue(q.Allows(1, 400, T0), "剛好用完額度是允許的");
            Assert.IsFalse(q.Allows(1, 401, T0), "超過一個位元組就不行");
            q.Add(1, 400, T0);
            Assert.AreEqual(0, q.Remaining(1, T0));
            Assert.IsFalse(q.Allows(1, 1, T0));
        }

        [Test]
        public void The_Budget_Resets_In_The_Next_Hour()
        {
            var q = new UploadQuota { BytesPerHour = 1000 };
            q.Add(1, 1000, T0);
            Assert.IsFalse(q.Allows(1, 1, T0));
            Assert.IsTrue(q.Allows(1, 1000, T0 + Hour), "下一個小時額度回滿");
            Assert.AreEqual(0, q.Used(1, T0 + Hour));
        }

        [Test]
        public void Each_User_Has_Their_Own_Budget()
        {
            var q = new UploadQuota { BytesPerHour = 1000 };
            q.Add(1, 1000, T0);
            Assert.IsFalse(q.Allows(1, 1, T0));
            Assert.IsTrue(q.Allows(2, 1000, T0), "別人的額度不受影響");
        }

        [Test]
        public void Forgetting_A_User_Frees_The_Bookkeeping()
        {
            // 不清的話這張表會跟著 server 的執行時間一直長(每個連過的 userId 一筆)。
            var q = new UploadQuota { BytesPerHour = 1000 };
            q.Add(7, 500, T0);
            q.Forget(7);
            Assert.AreEqual(0, q.Used(7, T0));
        }

        [Test]
        public void The_Default_Cap_Allows_Several_Real_Songs_Per_Hour()
        {
            // 一首歌上限 200 MB(NetLimits.DefaultMaxBlobBytes)。額度要容得下正常使用,
            // 不然「換幾首歌就傳不了」會被當成傳檔壞了。
            Assert.GreaterOrEqual(UploadQuota.DefaultBytesPerHour, 5 * Sdo.Net.NetLimits.DefaultMaxBlobBytes,
                "預設額度至少要容得下一小時傳五首滿額的歌");
        }
    }
}
