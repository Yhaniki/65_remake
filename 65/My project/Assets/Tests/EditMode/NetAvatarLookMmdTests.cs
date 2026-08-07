using NUnit.Framework;
using Sdo.Net;
using Sdo.Osu;

namespace Sdo.Tests
{
    /// <summary>
    /// 外觀裡的 MMD 模型欄位(<see cref="NetAvatarLook.MmdPack"/>)。
    ///
    /// 這是「別人看得到我的模型」的**唯一宣告通道** —— 房間快照每次都帶著整份 look 廣播,
    /// 而 <see cref="NetAvatarLook.Key"/> 又是遠端角色「要不要重建」的判斷依據。所以這裡有兩件事
    /// 一定要對:① 別人送來的 id 不可信,格式不對要當成「沒有 MMD」而不是照收;
    /// ② 顯示用的名字**不能**進比較鍵,否則改個資料夾名就會讓每個看到你的人重建一隻角色。
    /// </summary>
    public class NetAvatarLookMmdTests
    {
        private static string GoodPack() => ModelPackId.Compute(new[] { new PackFileEntry("miku.pmx", 10, new string('a', 64)) });

        /// <summary>JSON 字串 → 可以丟給 Decode 的節點。</summary>
        private static object Node(string json)
        {
            object node;
            Assert.IsTrue(NetJson.TryParse(json, out node), "測試自己的 JSON 就壞了:" + json);
            return node;
        }

        [Test]
        public void MmdPack_RoundTrips_ThroughTheWire()
        {
            var look = new NetAvatarLook { Gender = 0, BodyIndex = 2, Parts = new[] { "hair", "coat" },
                                           MmdPack = GoodPack(), MmdName = "IkaHatunemiku2025" };
            var back = NetAvatarLook.Decode(Node(look.Encode().Json()));

            Assert.AreEqual(look.MmdPack, back.MmdPack);
            Assert.AreEqual("IkaHatunemiku2025", back.MmdName);
            Assert.AreEqual(2, back.BodyIndex);
            CollectionAssert.AreEqual(look.Parts, back.Parts);
        }

        [Test]
        public void NoMmd_StaysEmpty_AndCostsNothingOnTheWire()
        {
            // 沒有用 MMD 的人(多數人)不該因為這個功能而在每份房間快照裡多帶兩個空欄位。
            var look = new NetAvatarLook { Gender = 1, Parts = new[] { "coat" } };
            string json = look.Encode().Json();
            StringAssert.DoesNotContain("mmd", json);

            var back = NetAvatarLook.Decode(Node(json));
            Assert.AreEqual("", back.MmdPack);
            Assert.AreEqual("", back.MmdName);
        }

        [Test]
        public void MalformedPackId_IsTreatedAsNoMmd_NotPassedThrough()
        {
            // 不驗的話,一個亂填的 id 會讓每個收到的人都去跟 server 要一份不存在的模型 —— 而且是
            // 每進一次房間問一次。壞外觀的正確結果是「他看起來是 SDO 角色」,不是「大家一直重試」。
            foreach (var bad in new[] { "not-a-pack", "sha256:xyz", "sha256:" + new string('a', 31), "sha1:" + new string('a', 32) })
            {
                var look = NetAvatarLook.Decode(Node("{\"gender\":0,\"mmd\":\"" + bad + "\",\"mmdName\":\"x\"}"));
                Assert.AreEqual("", look.MmdPack, "壞的 packId 被放行了:" + bad);
                Assert.AreEqual("", look.MmdName, "packId 不合法時名字也不該留著");
            }
        }

        // ---------------------------------------------------------------- 一包好幾個模型:是哪一個
        [Test]
        public void MmdFile_RoundTrips_SoTheOtherSideLoadsTheRightPmx()
        {
            // packId 是**整包**的指紋,而一包可以有好幾個 .pmx(角色 ＋ 武器 ＋ 影子)。
            // 少了這個欄位,收端只能反推 —— 實測反推挑到那把槍,別人畫面上是一支黑色的槍在跳舞。
            var look = new NetAvatarLook { MmdPack = GoodPack(), MmdFile = "nerissaravencroft.pmx" };
            var back = NetAvatarLook.Decode(Node(look.Encode().Json()));

            Assert.AreEqual("nerissaravencroft.pmx", back.MmdFile);
            Assert.AreEqual(MmdModelRef.Join(look.MmdPack, "nerissaravencroft.pmx"), back.MmdRef());
        }

        [Test]
        public void AnOldClientThatOnlySendsThePackId_IsStillUnderstood()
        {
            // 舊 client 不送 mmdFile。那不是壞資料,是「他沒說」→ 收端自己從包裡挑。
            var look = NetAvatarLook.Decode(Node("{\"mmd\":\"" + GoodPack() + "\"}"));
            Assert.AreEqual("", look.MmdFile);
            Assert.AreEqual(look.MmdPack, look.MmdRef());
        }

        [Test]
        public void HostileMmdFile_IsDropped_NotUsedToBuildAPath()
        {
            // 收端會拿它去 Combine 一個真實資料夾 → 路徑穿越就是任意檔案讀取。
            // 丟掉的正確結果是「他沒說」(收端自己挑),不是把整個模型丟掉。
            foreach (var bad in new[] { "../../../windows/system32/x.pmx", "/abs.pmx", "a.png", "x.pmx:s.pmx" })
            {
                var look = NetAvatarLook.Decode(Node("{\"mmd\":\"" + GoodPack() + "\",\"mmdFile\":\"" + bad.Replace("\\", "\\\\") + "\"}"));
                Assert.AreEqual("", look.MmdFile, "危險的包內路徑被放行了:" + bad);
                Assert.AreEqual(look.MmdPack, look.MmdRef(), "packId 本身還是要活著 —— 他確實穿著這一包");
            }
        }

        [Test]
        public void SwitchingToAnotherModelInTheSameFolder_IsAVisibleChange()
        {
            // 🔴 同一包裡換一個模型 → packId **一個字都不會變**。這裡不比的話:
            //    ① setLook 判定「外觀沒變」不送出 → 換了模型別人完全不知道;
            //    ② 遠端角色的比較鍵一樣 → 就算送到了也不重建。
            string pack = GoodPack();
            var a = new NetAvatarLook { MmdPack = pack, MmdFile = "nerissaravencroft.pmx" };
            var b = new NetAvatarLook { MmdPack = pack, MmdFile = "shadow.pmx" };

            Assert.AreEqual(a.MmdPack, b.MmdPack, "測試前提:同一包 → 同一個 packId");
            Assert.AreNotEqual(a.Key(), b.Key());
            Assert.IsFalse(a.SameAs(b));
            Assert.AreEqual("", new NetAvatarLook { MmdPack = pack }.Clone().MmdFile);
            Assert.AreEqual("shadow.pmx", b.Clone().MmdFile, "漏了它 → 別人看到的是同一包裡的另一個模型");
            Assert.IsTrue(b.SameAs(b.Clone()));
        }

        // ---------------------------------------------------------------- 他把模型調到多大
        [Test]
        public void MmdScale_RoundTrips_SoHeIsTheSameSizeOnEveryScreen()
        {
            // 大小是**穿的人**對他自己那個模型的修正,收端推不出來。少了它:他在別人畫面上是另一個尺寸,
            // 而且頭上的名字牌高度是照畫出來的身高算的 → 別人那邊他的名字會插在他頭裡。
            var look = new NetAvatarLook { MmdPack = GoodPack(), MmdFile = "miku.pmx", MmdScale = 1.4f };
            var back = NetAvatarLook.Decode(Node(look.Encode().Json()));

            Assert.AreEqual(1.4f, back.MmdScale, 1e-3f);
            Assert.AreEqual(look.MmdRef(), back.MmdRef());
            Assert.AreEqual(1.4f, MmdModelRef.ScaleOf(back.MmdRef()), 1e-3f);
        }

        [Test]
        public void ResizingTheModel_IsAVisibleChange()
        {
            // 只改大小 → packId 與檔名一個字都不會變。這裡不比的話:
            //   ① setLook 判定「外觀沒變」不送出 → 我調了大小,別人完全不知道;
            //   ② 遠端角色的比較鍵一樣 → 就算送到了也不重建(而縮放是建構期決定的,見 MmdAvatar.Construct)。
            string pack = GoodPack();
            var a = new NetAvatarLook { MmdPack = pack, MmdFile = "miku.pmx", MmdScale = 1f };
            var b = new NetAvatarLook { MmdPack = pack, MmdFile = "miku.pmx", MmdScale = 1.4f };

            Assert.AreNotEqual(a.Key(), b.Key());
            Assert.IsFalse(a.SameAs(b));
            Assert.IsTrue(b.SameAs(b.Clone()));
            Assert.AreEqual(1.4f, b.Clone().MmdScale, 1e-6f, "漏了它 → 進遊戲之後他變回另一個大小");
        }

        [Test]
        public void NoScaling_CostsNothingOnTheWire_AndOldClientsStillWork()
        {
            // 沒調過大小的人(絕大多數)不該因為這個功能而在每份房間快照裡多帶一個欄位。
            var look = new NetAvatarLook { MmdPack = GoodPack(), MmdFile = "miku.pmx" };
            StringAssert.DoesNotContain("mmdScale", look.Encode().Json());

            // 舊 client 不送這個欄位 = 「他沒調過」→ 1×,而不是縮成 0。
            var old = NetAvatarLook.Decode(Node("{\"mmd\":\"" + GoodPack() + "\",\"mmdFile\":\"miku.pmx\"}"));
            Assert.AreEqual(1f, old.MmdScale, 1e-6f);
            Assert.IsTrue(old.SameAs(look));
        }

        [Test]
        public void HostileMmdScale_IsClamped_NotObeyed()
        {
            foreach (var kv in new[] { new[] { "0", "1" }, new[] { "-5", "1" }, new[] { "9999", "3" }, new[] { "0.0001", "0.3" } })
            {
                var look = NetAvatarLook.Decode(Node("{\"mmd\":\"" + GoodPack() + "\",\"mmdScale\":" + kv[0] + "}"));
                Assert.AreEqual(float.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture), look.MmdScale, 1e-3f,
                                "沒夾住的大小:" + kv[0]);
            }
        }

        [Test]
        public void HostileMmdName_IsClipped()
        {
            // 名字是別人送來的字串而且會被畫在 UI 上。
            string huge = new string('x', 500);
            var look = NetAvatarLook.Decode(Node("{\"mmd\":\"" + GoodPack() + "\",\"mmdName\":\"" + huge + "\"}"));
            Assert.LessOrEqual(look.MmdName.Length, NetAvatarLook.MaxMmdNameLength);
        }

        // ---------------------------------------------------------------- 比較鍵 / SameAs
        [Test]
        public void ChangingTheModel_ForcesARemoteRebuild()
        {
            var a = new NetAvatarLook { Parts = new[] { "coat" }, MmdPack = GoodPack() };
            var b = new NetAvatarLook { Parts = new[] { "coat" } };                       // 同一套衣服,脫掉 MMD
            Assert.AreNotEqual(a.Key(), b.Key());
            Assert.IsFalse(a.SameAs(b));
            Assert.IsFalse(b.SameAs(a));
        }

        [Test]
        public void RenamingTheModelFolder_DoesNotRebuildAnything()
        {
            // 名字純粹顯示用。它變了畫面上什麼都不會不同,卻會白白重建一隻角色(要讀十幾個部件檔)。
            var a = new NetAvatarLook { Parts = new[] { "coat" }, MmdPack = GoodPack(), MmdName = "Miku" };
            var b = new NetAvatarLook { Parts = new[] { "coat" }, MmdPack = GoodPack(), MmdName = "初音未來" };
            Assert.AreEqual(a.Key(), b.Key());
            Assert.IsTrue(a.SameAs(b));
        }

        [Test]
        public void TwoDifferentModels_AreDifferentLooks()
        {
            var one = ModelPackId.Compute(new[] { new PackFileEntry("a.pmx", 10, new string('a', 64)) });
            var two = ModelPackId.Compute(new[] { new PackFileEntry("b.pmx", 10, new string('b', 64)) });
            Assert.AreNotEqual(one, two);

            var a = new NetAvatarLook { MmdPack = one };
            var b = new NetAvatarLook { MmdPack = two };
            Assert.AreNotEqual(a.Key(), b.Key());
            Assert.IsFalse(a.SameAs(b));
        }

        [Test]
        public void TheSdoOutfit_IsStillCarried_WhileWearingAModel()
        {
            // 這是「還沒下載完就先顯示他的穿搭」能成立的前提:穿搭一直都在 look 裡,
            // 不會因為他改用 MMD 就不送了。
            var look = new NetAvatarLook { Gender = 1, BodyIndex = 3, Parts = new[] { "hair", "coat", "shoes" },
                                           MmdPack = GoodPack() };
            var back = NetAvatarLook.Decode(Node(look.Encode().Json()));
            Assert.AreEqual(1, back.Gender);
            Assert.AreEqual(3, back.BodyIndex);
            CollectionAssert.AreEqual(new[] { "hair", "coat", "shoes" }, back.Parts);
        }

        /// <summary>
        /// 🔴 <b>複製 look 一定要走 <see cref="NetAvatarLook.Clone"/>。</b>
        ///
        /// 手捲的複製漏掉一個欄位不會有任何編譯錯誤或警告 —— 那個值就是靜靜地不見了,
        /// 而症狀離現場很遠:<c>NetMatchPlayerSnapshot.Capture</c> 漏了 MmdPack,結果是
        /// <b>房間裡每個人的 MMD 都好好的,一進遊戲全部變回 SDO 穿搭</b>(那份快照就是
        /// matchStarting 送出去的參賽者名單)。log 上唯一的線索是「registered dancer」那一行不見了。
        /// </summary>
        [Test]
        public void Clone_CarriesEveryField_IncludingTheModel()
        {
            var src = new NetAvatarLook
            {
                Gender = 1,
                BodyIndex = 4,
                Parts = new[] { "hair", "coat" },
                MmdPack = GoodPack(),
                MmdFile = "pmx/miku.pmx",
                MmdName = "TestMiku",
                MmdScale = 1.4f,
            };

            var copy = src.Clone();
            Assert.AreEqual(src.Gender, copy.Gender);
            Assert.AreEqual(src.BodyIndex, copy.BodyIndex);
            CollectionAssert.AreEqual(src.Parts, copy.Parts);
            Assert.AreEqual(src.MmdPack, copy.MmdPack, "漏了它 → 進遊戲之後每個人的模型都變回 SDO");
            Assert.AreEqual(src.MmdFile, copy.MmdFile, "漏了它 → 進遊戲之後變成同一包裡的另一個模型");
            Assert.AreEqual(src.MmdScale, copy.MmdScale, 1e-6f, "漏了它 → 進遊戲之後他變回另一個大小");
            Assert.AreEqual(src.MmdName, copy.MmdName);
            Assert.IsTrue(src.SameAs(copy), "複製出來的必須與原件相等");

            // 深拷貝:改副本的穿搭不可以動到原件。
            copy.Parts[0] = "changed";
            Assert.AreEqual("hair", src.Parts[0]);
        }

        /// <summary>
        /// 上一條的**端對端**版本:走 server 真正用的那條路(座位 → 參賽者快照),
        /// 模型的宣告要活著到參賽者名單裡。
        /// </summary>
        [Test]
        public void MatchSnapshot_KeepsTheModelDeclaration()
        {
            string pack = GoodPack();
            var seat = new NetSeat
            {
                UserId = 7,
                Name = "穿模型的",
                Look = new NetAvatarLook { Gender = 0, BodyIndex = 1, MmdPack = pack, MmdFile = "miku.pmx",
                                           MmdName = "TestMiku", MmdScale = 1.4f },
            };

            var snap = Sdo.Net.Server.NetMatchPlayerSnapshot.Capture(seat, 2);
            Assert.AreEqual(7, snap.UserId);
            Assert.AreEqual(2, snap.Seat);
            Assert.IsNotNull(snap.Look);
            Assert.AreEqual(pack, snap.Look.MmdPack,
                "參賽者名單掉了 packId → 進遊戲之後別人的 MMD 全部變回 SDO 穿搭");
            Assert.AreEqual("miku.pmx", snap.Look.MmdFile,
                "參賽者名單掉了包內路徑 → 進遊戲之後別人變成同一包裡的另一個模型");
            Assert.AreEqual(1.4f, snap.Look.MmdScale, 1e-6f,
                "參賽者名單掉了大小 → 房間裡好好的,一進遊戲他就變回另一個尺寸(名字也會插進他頭裡)");
            Assert.AreEqual("TestMiku", snap.Look.MmdName);
        }
    }
}
