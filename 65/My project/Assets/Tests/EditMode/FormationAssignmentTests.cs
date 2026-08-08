using NUnit.Framework;
using Sdo.Ruleset;

namespace Sdo.Tests
{
    /// <summary>
    /// 分數 → 隊形 slot 的指派。
    ///
    /// 🔴 為什麼這組測試重要:每一台 client 都各自算一次站位,結果**必須一樣**。
    /// 不一樣的話同一個人在不同人的畫面上站不同格 —— 而那不會有任何錯誤訊息,
    /// 只會有「他的角色在我這邊站中間、在他自己那邊站旁邊」這種沒人回報得清楚的怪事。
    /// 所以「同分怎麼辦」不能靠「剛好」,要有明文規則並且釘住。
    /// </summary>
    public class FormationAssignmentTests
    {
        [Test]
        public void With_No_Dancers_Nothing_Blows_Up()
        {
            CollectionAssert.IsEmpty(FormationAssignment.SlotForDancer(new long[0]));
            Assert.AreEqual(-1, FormationAssignment.TopScorer(new long[0]));
            CollectionAssert.IsEmpty(FormationAssignment.SlotForDancer(null));
        }

        [Test]
        public void A_Solo_Dancer_Takes_The_Leader_Slot()
        {
            CollectionAssert.AreEqual(new[] { 0 }, FormationAssignment.SlotForDancer(new long[] { 0 }));
        }

        [Test]
        public void Everyone_Starts_In_Seat_Order_When_The_Leader_Is_Already_Winning()
        {
            // 舞者 0 分數最高 → 不用換位,每個人都在自己的格子。
            var slots = FormationAssignment.SlotForDancer(new long[] { 900, 500, 100 });
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, slots);
        }

        [Test]
        public void The_Current_Leader_Slides_Into_Slot_Zero_And_Swaps_With_Whoever_Was_There()
        {
            // 舞者 2 是第一名 → 它去 slot 0,而原本在 slot 0 的舞者 0 去 slot 2(互換,不是整排推移)。
            var slots = FormationAssignment.SlotForDancer(new long[] { 300, 200, 900 });
            CollectionAssert.AreEqual(new[] { 2, 1, 0 }, slots);
        }

        [Test]
        public void Stable_Leader_Leaves_Everyone_Who_Is_Not_Contesting_First_Place_Alone()
        {
            // 🔴 這條釘住實際回報的 bug。三個人:舞者 0 分數墊底(而且他座位序第 0 位 → 開局站領隊格),
            //    舞者 1、2 在互爭第一名。每次第一名換手,**只有那兩位**能動;墊底的那位一格都不能挪。
            var slots = FormationAssignment.StableLeaderSlots(null, 1, 3);
            CollectionAssert.AreEqual(new[] { 1, 0, 2 }, slots, "舞者 1 進領隊格,把原本在那的舞者 0 換到 slot 1");

            slots = FormationAssignment.StableLeaderSlots(slots, 2, 3);
            CollectionAssert.AreEqual(new[] { 1, 2, 0 }, slots,
                "第一名換成舞者 2 → 它跟**當下站在領隊格的舞者 1** 對調;墊底的舞者 0 留在 slot 1");

            slots = FormationAssignment.StableLeaderSlots(slots, 1, 3);
            CollectionAssert.AreEqual(new[] { 1, 0, 2 }, slots, "換回來也一樣,舞者 0 還是 slot 1");

            // 無狀態的那條路才是壞的 —— 留著當對照,免得哪天有人把每幀路徑換回去。
            CollectionAssert.AreEqual(new[] { 1, 0, 2 }, FormationAssignment.SlotForDancer(new long[] { 0, 900, 300 }));
            CollectionAssert.AreEqual(new[] { 2, 1, 0 }, FormationAssignment.SlotForDancer(new long[] { 0, 300, 900 }),
                "無狀態版:第一名換手就把墊底的舞者 0 從 slot 1 甩到 slot 2");
        }

        [Test]
        public void Stable_Leader_Holds_Still_When_The_Leader_Is_Unchanged_Or_Unknown()
        {
            var slots = FormationAssignment.StableLeaderSlots(null, 2, 3);
            CollectionAssert.AreEqual(new[] { 2, 1, 0 }, slots);

            CollectionAssert.AreEqual(slots, FormationAssignment.StableLeaderSlots(slots, 2, 3), "同一個第一名 → 完全不動");
            CollectionAssert.AreEqual(slots, FormationAssignment.StableLeaderSlots(slots, -1, 3), "沒有第一名 → 維持上一幀");
            CollectionAssert.AreEqual(slots, FormationAssignment.StableLeaderSlots(slots, 9, 3), "越界 → 維持上一幀");
        }

        [Test]
        public void Stable_Leader_Recovers_From_A_Broken_Previous_Assignment()
        {
            // 人數變了 / 兩個人疊在同一格 → 從座位序重來,不能讓壞掉的排列一直傳下去。
            CollectionAssert.AreEqual(new[] { 1, 0, 2 }, FormationAssignment.StableLeaderSlots(new[] { 0, 1 }, 1, 3),
                "長度不符 → 回座位序再交換");
            CollectionAssert.AreEqual(new[] { 1, 0, 2 }, FormationAssignment.StableLeaderSlots(new[] { 1, 1, 2 }, 1, 3),
                "重複的格子 → 回座位序再交換");

            var slots = FormationAssignment.StableLeaderSlots(null, 1, 4);
            var seen = new bool[4];
            foreach (var s in slots) { Assert.IsFalse(seen[s], "slot " + s + " 被指派了兩次"); seen[s] = true; }
        }

        [Test]
        public void Every_Slot_Is_Used_Exactly_Once()
        {
            // 這是「指派」而不是「排序」:六個人一定剛好占滿六格,不能有人重複或漏格
            // (重複 = 兩個人站同一點疊在一起;漏格 = 隊形看起來缺一個人)。
            var slots = FormationAssignment.SlotForDancer(new long[] { 10, 90, 20, 80, 30, 70 });
            var seen = new bool[slots.Length];
            foreach (var s in slots)
            {
                Assert.GreaterOrEqual(s, 0);
                Assert.Less(s, slots.Length);
                Assert.IsFalse(seen[s], "slot " + s + " 被指派了兩次");
                seen[s] = true;
            }
        }

        [Test]
        public void A_Tie_Goes_To_The_Lower_Dancer_Index()
        {
            // 🔴 決定性的規則。兩台各挑一個人當第一名的話,那個人的站位在兩台上就不一樣。
            Assert.AreEqual(0, FormationAssignment.TopScorer(new long[] { 500, 500, 500 }));
            Assert.AreEqual(1, FormationAssignment.TopScorer(new long[] { 100, 500, 500 }));

            var slots = FormationAssignment.SlotForDancer(new long[] { 100, 500, 500 });
            CollectionAssert.AreEqual(new[] { 1, 0, 2 }, slots, "同分時由座位序在前的那位(舞者 1)進領隊格");
        }

        [Test]
        public void Leader_Only_Changes_After_A_Challenger_Leads_By_300()
        {
            Assert.AreEqual(0, FormationAssignment.SelectLeader(new long[] { 1000, 1299 }, 0),
                "只領先 299 分時,目前 leader 要留在中央,避免兩人來回換位");
            Assert.AreEqual(1, FormationAssignment.SelectLeader(new long[] { 1000, 1300 }, 0),
                "領先達 300 分才確認換 leader");
            Assert.AreEqual(1, FormationAssignment.SelectLeader(new long[] { 1599, 1300 }, 1),
                "換位後反向也要領先 300 分,不能剛追近就立刻換回");
            Assert.AreEqual(0, FormationAssignment.SelectLeader(new long[] { 1600, 1300 }, 1));
        }

        [Test]
        public void Retained_Leader_Still_Occupies_Leader_Slot_While_A_Challenger_Is_Close()
        {
            var scores = new long[] { 1000, 1299, 500 };
            int leader = FormationAssignment.SelectLeader(scores, 0);

            CollectionAssert.AreEqual(new[] { 0, 1, 2 },
                FormationAssignment.SlotForDancer(scores, leader),
                "防抖留下原 leader 後,slot 指派也必須沿用它,不能又用原始最高分把人換掉");
        }

        [Test]
        public void Server_Authoritative_Leader_Overrides_Local_Hysteresis_And_Invalid_Authority_Falls_Back()
        {
            var scores = new long[] { 5000, 1000, 10 };

            Assert.AreEqual(2, FormationAssignment.ResolveLeader(scores, 0, 2),
                "a mapped server leader is authoritative even when local scores currently favour another dancer");
            Assert.AreEqual(0, FormationAssignment.ResolveLeader(scores, 0, -1));
            Assert.AreEqual(0, FormationAssignment.ResolveLeader(scores, 0, 99));
        }

        [Test]
        public void Everyone_On_Zero_Keeps_Seat_Order()
        {
            // 開場那一刻大家都是 0 分 —— 這時就該是「照座位序」,而不是隨便挑一個人站中間。
            var slots = FormationAssignment.SlotForDancer(new long[] { 0, 0, 0, 0, 0, 0 });
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4, 5 }, slots);
        }

        // ---------------------------------------------------------------- 依名次調整站位（三種模式）
        [Test]
        public void Rank_Based_Formation_Off_Keeps_Everyone_In_Seat_Order()
        {
            // config.ini rankBasedFormation=off：分數再怎麼變都不換位（leader 模式下舞者 2 會被搬到 slot 0）。
            var scores = new long[] { 300, 200, 900 };
            CollectionAssert.AreEqual(new[] { 0, 1, 2 },
                FormationAssignment.SlotForDancer(scores, leader: 2, mode: FormationRankMode.Off));
            CollectionAssert.AreEqual(new[] { 2, 1, 0 },
                FormationAssignment.SlotForDancer(scores, leader: 2, mode: FormationRankMode.Leader));
            CollectionAssert.AreEqual(new[] { 1, 2, 0 },
                FormationAssignment.SlotForDancer(scores, leader: 2, mode: FormationRankMode.Full),
                "完整名次：900 → slot 0、300 → slot 1、200 → slot 2");
        }

        [Test]
        public void Full_Mode_Puts_The_Kth_Place_In_Slot_K()
        {
            CollectionAssert.AreEqual(new[] { 2, 0, 1 }, FormationAssignment.RankSlots(new long[] { 100, 5000, 4000 }));
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, FormationAssignment.RankSlots(new long[] { 900, 500, 100 }));
            CollectionAssert.AreEqual(new[] { 0 }, FormationAssignment.RankSlots(new long[] { 0 }));
            CollectionAssert.IsEmpty(FormationAssignment.RankSlots(null));
        }

        [Test]
        public void Full_Mode_Leaves_The_Last_Place_Alone_While_The_Top_Two_Trade_Places()
        {
            // 🔴 這條就是「完整名次」存在的理由（回歸測試）。
            // 座位序：舞者 0 是分數很低的那位（他的 base slot 恰好是領隊格），舞者 1、2 在爭第一名。
            //
            // 官方（leader）模式：那次互換的對象**恆是占著領隊格的舞者 0**，所以第 1/2 名每換一次手，
            //   墊底的舞者 0 就被從一格甩到另一格 —— 玩家看到的是「三個人都在動」。
            var lowFirst = new long[] { 100, 5000, 4000 };
            var lowSecond = new long[] { 100, 5000, 6000 };
            Assert.AreEqual(1, FormationAssignment.SlotForDancer(lowFirst, 1, FormationRankMode.Leader)[0]);
            Assert.AreEqual(2, FormationAssignment.SlotForDancer(lowSecond, 2, FormationRankMode.Leader)[0],
                            "官方模式：墊底那位跟著第一名換手一起被搬（＝這次要修掉的現象）");

            // 完整名次：他從頭到尾就是第 3 名 → 從頭到尾站 slot 2。
            var a = FormationAssignment.SlotForDancer(lowFirst, 1, FormationRankMode.Full);
            var b = FormationAssignment.SlotForDancer(lowSecond, 2, FormationRankMode.Full);
            Assert.AreEqual(2, a[0]);
            Assert.AreEqual(2, b[0], "第 3 名的格子不能因為前兩名換手而改變");
            CollectionAssert.AreEqual(new[] { 2, 0, 1 }, a);
            CollectionAssert.AreEqual(new[] { 2, 1, 0 }, b, "只有真的換了名次的那兩位換格子");
        }

        [Test]
        public void Full_Mode_Breaks_Ties_By_Seat_Order_And_Fills_Every_Slot_Once()
        {
            // 同分要決定性（理由同 leader 模式：兩台各排一種的話，同一個人在不同畫面上站不同格）。
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, FormationAssignment.RankSlots(new long[] { 500, 500, 500 }));
            CollectionAssert.AreEqual(new[] { 1, 0, 2 }, FormationAssignment.RankSlots(new long[] { 500, 900, 500 }));

            var slots = FormationAssignment.RankSlots(new long[] { 10, 90, 20, 80, 30, 70 });
            var seen = new bool[slots.Length];
            foreach (var s in slots)
            {
                Assert.GreaterOrEqual(s, 0);
                Assert.Less(s, slots.Length);
                Assert.IsFalse(seen[s], "slot " + s + " 被指派了兩次");
                seen[s] = true;
            }
        }

        // ---------------------------------------------------------------- 完整名次的防抖（每幀路徑）
        [Test]
        public void Stable_Rank_Only_Swaps_Neighbours_That_Clear_The_Threshold()
        {
            // 完整名次讓**每一組相鄰名次**都有得抖（官方模式只有領隊格那一格），所以防抖要擴到全排。
            var seat = new[] { 0, 1, 2 };
            CollectionAssert.AreEqual(new[] { 0, 1, 2 },
                FormationAssignment.StableRankSlots(new long[] { 1000, 1299, 0 }, seat, -1),
                "只領先 299 分 → 不換，免得每個判定都換一次");
            CollectionAssert.AreEqual(new[] { 1, 0, 2 },
                FormationAssignment.StableRankSlots(new long[] { 1000, 1300, 0 }, seat, -1),
                "領先達 300 分才換");

            // 換過去之後，反向換回也要再跨過同一條門檻（不然剛追近就立刻換回，一樣在抖）。
            var after = new[] { 1, 0, 2 };
            CollectionAssert.AreEqual(after,
                FormationAssignment.StableRankSlots(new long[] { 1250, 1300, 0 }, after, -1));
            CollectionAssert.AreEqual(new[] { 0, 1, 2 },
                FormationAssignment.StableRankSlots(new long[] { 1600, 1300, 0 }, after, -1));
        }

        [Test]
        public void Stable_Rank_Converges_To_The_Plain_Rank_Order()
        {
            // 一幀只做相鄰交換 → 分數差很開時要幾幀才排好。站位本來就是 SlideStep 慢慢滑的，看不出來，
            // 但一定要收斂到跟 RankSlots 同一個答案（否則「第 k 名站第 k 格」只是說說而已）。
            var scores = new long[] { 100, 5000, 4000, 90, 4500, 20 };
            int[] slots = null;
            for (int frame = 0; frame < 12; frame++)
                slots = FormationAssignment.StableRankSlots(scores, slots, -1);
            CollectionAssert.AreEqual(FormationAssignment.RankSlots(scores), slots);
        }

        [Test]
        public void Stable_Rank_Pins_The_Authoritative_Leader_To_Slot_Zero()
        {
            // 線上的第一名是 server 權威的（ResolveLeader）。中段名次各台可能因分數的時間落差而不同，
            // 但中央前排那格＝導播鏡頭的錨點，每台必須是同一個人。
            var scores = new long[] { 100, 5000, 4000 };
            var slots = FormationAssignment.StableRankSlots(scores, new[] { 0, 1, 2 }, leader: 2);
            Assert.AreEqual(0, slots[2], "server 說舞者 2 是第一名 → 他就在 slot 0");
            CollectionAssert.AreEqual(new[] { 2, 1, 0 }, slots, "其餘保持相對順序，往後推一格");

            // leader 不合法（舊 server、名單正在換）→ 照本機分數排，不能整排歪掉。
            CollectionAssert.AreEqual(FormationAssignment.StableRankSlots(scores, new[] { 0, 1, 2 }, -1),
                                      FormationAssignment.StableRankSlots(scores, new[] { 0, 1, 2 }, 99));
        }

        [Test]
        public void Stable_Rank_Recovers_From_A_Broken_Previous_State()
        {
            // 上一幀的狀態不是排列（人數變了、兩個人指到同一格）→ 從座位序重新開始。
            // 壞狀態不能被沿用，否則兩個人會疊在同一點上。
            var scores = new long[] { 1000, 2000, 3000 };
            var expected = FormationAssignment.StableRankSlots(scores, new[] { 0, 1, 2 }, -1);

            CollectionAssert.AreEqual(expected, FormationAssignment.StableRankSlots(scores, null, -1));
            CollectionAssert.AreEqual(expected, FormationAssignment.StableRankSlots(scores, new[] { 0, 1 }, -1),
                                      "長度不符（有人離開了）");
            CollectionAssert.AreEqual(expected, FormationAssignment.StableRankSlots(scores, new[] { 0, 0, 1 }, -1),
                                      "重複的格子");
            CollectionAssert.AreEqual(expected, FormationAssignment.StableRankSlots(scores, new[] { 0, 1, 9 }, -1),
                                      "越界的格子");
            CollectionAssert.IsEmpty(FormationAssignment.StableRankSlots(new long[0], null, 0));
        }

        [Test]
        public void Stable_Rank_Always_Returns_A_Permutation()
        {
            // 相鄰交換 + 把 leader 往前搬，兩個動作都必須保持「剛好占滿每一格」。
            var scores = new long[] { 10, 4000, 20, 3999, 8000, 30 };
            int[] slots = null;
            for (int frame = 0; frame < 8; frame++)
            {
                slots = FormationAssignment.StableRankSlots(scores, slots, frame % 6);
                var seen = new bool[slots.Length];
                foreach (var s in slots)
                {
                    Assert.IsFalse(seen[s], "slot " + s + " 被指派了兩次（第 " + frame + " 幀）");
                    seen[s] = true;
                }
            }
        }

        // ---------------------------------------------------------------- config.ini 的值
        [Test]
        public void Mode_Parses_The_Config_Values_Including_The_Old_Booleans()
        {
            Assert.AreEqual(FormationRankMode.Off, FormationAssignment.ParseMode("off"));
            Assert.AreEqual(FormationRankMode.Leader, FormationAssignment.ParseMode("leader"));
            Assert.AreEqual(FormationRankMode.Full, FormationAssignment.ParseMode("full"));

            // 這個鍵前幾版是布林開關 —— 玩家手上的 config.ini 還是 0/1。
            Assert.AreEqual(FormationRankMode.Off, FormationAssignment.ParseMode("0"));
            Assert.AreEqual(FormationRankMode.Leader, FormationAssignment.ParseMode("1"));
            Assert.AreEqual(FormationRankMode.Full, FormationAssignment.ParseMode("2"));
            Assert.AreEqual(FormationRankMode.Off, FormationAssignment.ParseMode(" FALSE "));

            // 認不出來 → 預設（官方行為），不是默默變成別的模式。
            Assert.AreEqual(FormationRankMode.Leader, FormationAssignment.ParseMode("banana"));
            Assert.AreEqual(FormationRankMode.Leader, FormationAssignment.ParseMode(null));
            Assert.AreEqual(FormationRankMode.Full, FormationAssignment.ParseMode("", FormationRankMode.Full));

            Assert.AreEqual("off", FormationAssignment.ModeKey(FormationRankMode.Off));
            Assert.AreEqual("leader", FormationAssignment.ModeKey(FormationRankMode.Leader));
            Assert.AreEqual("full", FormationAssignment.ModeKey(FormationRankMode.Full));
        }

        [Test]
        public void Mode_Values_Agree_With_RoomConfig()
        {
            // 🔴 Sdo.Settings 不參照 Sdo.Ruleset，所以 canonical 字串與別名清單兩邊各寫一份。
            //    對不上的話：面板存進 config.ini 的值 ScreenGameplay 讀不出來 → 默默退回官方模式。
            Assert.AreEqual(Sdo.Settings.RoomConfig.rankFormationOff, FormationAssignment.ModeKeyOff);
            Assert.AreEqual(Sdo.Settings.RoomConfig.rankFormationLeader, FormationAssignment.ModeKeyLeader);
            Assert.AreEqual(Sdo.Settings.RoomConfig.rankFormationFull, FormationAssignment.ModeKeyFull);

            foreach (var raw in new[] { "off", "leader", "full", "0", "1", "2", "false", "true", "no", "yes",
                                        "seat", "rank", " FULL ", "banana", "", null })
                Assert.AreEqual(FormationAssignment.ParseMode(Sdo.Settings.RoomConfig.NormalizeRankFormation(raw)),
                                FormationAssignment.ParseMode(raw),
                                "RoomConfig 收出來的值與 ParseMode 對 '" + (raw ?? "<null>") + "' 的解讀不一致");
        }

        [Test]
        public void Seat_Order_Slots_Are_The_Identity_Assignment()
        {
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 }, FormationAssignment.SeatOrderSlots(4));
            CollectionAssert.IsEmpty(FormationAssignment.SeatOrderSlots(0));
            CollectionAssert.IsEmpty(FormationAssignment.SeatOrderSlots(-3));   // 防呆：負數不炸
        }

        [Test]
        public void Rank_Based_Formation_Off_Still_Fills_Every_Slot_Exactly_Once()
        {
            // 關掉之後一樣是「指派」：六個人剛好占滿六格（重複＝疊在一起、漏格＝隊形缺人）。
            var slots = FormationAssignment.SlotForDancer(new long[] { 10, 90, 20, 80, 30, 70 }, 1,
                                                         FormationRankMode.Off);
            var seen = new bool[slots.Length];
            foreach (var s in slots)
            {
                Assert.IsFalse(seen[s], "slot " + s + " 被指派了兩次");
                seen[s] = true;
            }
            Assert.AreEqual(6, slots.Length);
        }

        [Test]
        public void The_Slide_Converges_Towards_The_Target_Without_Overshooting()
        {
            // 官方是每幀 cur*0.9 + target*0.1(固定比例,不是時間相關)。
            float cur = 0f;
            for (int i = 0; i < 200; i++) cur = FormationAssignment.SlideStep(cur, 100f);
            Assert.AreEqual(100f, cur, 0.5f, "應該收斂到目標");

            // 單步不能越過目標(不然會來回抖)
            float one = FormationAssignment.SlideStep(0f, 100f);
            Assert.Greater(one, 0f);
            Assert.Less(one, 100f);
            Assert.AreEqual(10f, one, 0.001f);
        }

        [Test]
        public void The_Slide_Also_Works_Backwards()
        {
            // 被擠掉的人要往回走 —— 同一個算式,方向相反。
            float cur = 100f;
            for (int i = 0; i < 200; i++) cur = FormationAssignment.SlideStep(cur, 0f);
            Assert.AreEqual(0f, cur, 0.5f);
        }
    }
}
