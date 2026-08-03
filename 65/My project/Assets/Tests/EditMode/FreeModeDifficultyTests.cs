using NUnit.Framework;
using Sdo.UI.Services;

namespace Sdo.Tests
{
    /// <summary>
    /// 自由模式「每個人自己挑難度」的規則。
    ///
    /// 這裡守的是三件會靜默出錯的事:
    ///   ① 誰看得到那格(房主看到的是「房主設置」,兩者疊在同一個位置);
    ///   ② ◄ ► 要跳過**這首歌沒有的譜**(外部歌常只有一兩張難度);
    ///   ③ 換歌之後,上次挑的難度要貼回還打得到的那個(否則開場會載到一張空譜)。
    /// </summary>
    public class FreeModeDifficultyTests
    {
        // ---- ① 誰看得到 ----

        [Test]
        public void Only_Non_Host_In_Free_Mode_Picks_Own_Difficulty()
        {
            Assert.IsTrue(FreeModeDifficulty.PlayerPicksOwn(0, isHost: false), "自由模式的非房主要看得到難度設置");
            Assert.IsFalse(FreeModeDifficulty.PlayerPicksOwn(0, isHost: true), "房主那格是「房主設置」(選歌)");
            Assert.IsFalse(FreeModeDifficulty.PlayerPicksOwn(1, isHost: false), "普通模式全場同一張譜");
            Assert.IsFalse(FreeModeDifficulty.PlayerPicksOwn(2, isHost: false), "ShowTime 模式同上");
        }

        // ---- ② ◄ ► ----

        [Test]
        public void Step_Moves_One_Slot_When_Everything_Is_Playable()
        {
            Assert.AreEqual(1, FreeModeDifficulty.Step(0, +1, null));
            Assert.AreEqual(2, FreeModeDifficulty.Step(1, +1, null));
            Assert.AreEqual(1, FreeModeDifficulty.Step(2, -1, null));
        }

        [Test]
        public void Step_Stops_At_The_Ends_Instead_Of_Wrapping()
        {
            // 箭頭鈕的語意是「往那個方向」,繞回去會讓人以為按錯了。
            Assert.AreEqual(2, FreeModeDifficulty.Step(2, +1, null));
            Assert.AreEqual(0, FreeModeDifficulty.Step(0, -1, null));
        }

        [Test]
        public void Step_Skips_Difficulties_That_Have_No_Chart()
        {
            var onlyEasyAndHard = new[] { true, false, true };
            Assert.AreEqual(2, FreeModeDifficulty.Step(0, +1, onlyEasyAndHard), "NORMAL 沒譜 → 直接跳到 HARD");
            Assert.AreEqual(0, FreeModeDifficulty.Step(2, -1, onlyEasyAndHard));
        }

        [Test]
        public void Step_Stays_Put_When_Nothing_Playable_That_Way()
        {
            var onlyEasy = new[] { true, false, false };
            Assert.AreEqual(0, FreeModeDifficulty.Step(0, +1, onlyEasy));
        }

        [Test]
        public void Step_Zero_Is_A_No_Op()
        {
            Assert.AreEqual(1, FreeModeDifficulty.Step(1, 0, null));
        }

        // ---- ③ 換歌後貼回 ----

        [Test]
        public void Snap_Keeps_A_Playable_Choice()
        {
            Assert.AreEqual(2, FreeModeDifficulty.Snap(2, new[] { true, true, true }));
        }

        [Test]
        public void Snap_Falls_Back_To_The_Nearest_Playable_Slot()
        {
            // 上一首選 HARD,新的那首只有 EASY → 貼到 EASY(而不是留在一張不存在的譜上)。
            Assert.AreEqual(0, FreeModeDifficulty.Snap(2, new[] { true, false, false }));
            Assert.AreEqual(2, FreeModeDifficulty.Snap(0, new[] { false, false, true }));
        }

        [Test]
        public void Snap_Prefers_The_Easier_Side_On_A_Tie()
        {
            // NORMAL 沒譜,兩邊距離一樣 → 取簡單的那個。自動把人推到更難的譜不是好的預設。
            Assert.AreEqual(0, FreeModeDifficulty.Snap(1, new[] { true, false, true }));
        }

        [Test]
        public void Snap_Without_Chart_Info_Just_Clamps()
        {
            // 缺歌 / 隨機難度 → 不知道有哪些譜,原樣夾回合法範圍即可(顯示用,開場前還會再貼一次)。
            Assert.AreEqual(2, FreeModeDifficulty.Snap(9, null));
            Assert.AreEqual(0, FreeModeDifficulty.Snap(-3, null));
            Assert.AreEqual(1, FreeModeDifficulty.Snap(1, new[] { false, false, false }));
        }

        // ---- 顯示 ----

        [Test]
        public void Names_Match_The_Official_Label()
        {
            // 官方 FMLvlChoose 的 labeltext 就是英文大寫。
            Assert.AreEqual("EASY", FreeModeDifficulty.Name(0));
            Assert.AreEqual("NORMAL", FreeModeDifficulty.Name(1));
            Assert.AreEqual("HARD", FreeModeDifficulty.Name(2));
            Assert.AreEqual("EASY", FreeModeDifficulty.Name(-1));
            Assert.AreEqual("HARD", FreeModeDifficulty.Name(7));
        }

        [Test]
        public void Slot_Count_Matches_The_Difficulty_Enum()
        {
            Assert.AreEqual(3, FreeModeDifficulty.SlotCount);
            Assert.AreEqual((int)Sdo.UI.Core.Difficulty.Hard, FreeModeDifficulty.SlotCount - 1);
        }
    }
}
