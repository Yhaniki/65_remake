using NUnit.Framework;
using Sdo.Net;

namespace Sdo.Tests
{
    /// <summary>
    /// combo 里程碑事件的合法範圍與去重規則(client 與 server 編譯同一份 <see cref="ComboMilestoneRules"/>)。
    ///
    /// 為什麼這幾條值得釘住:這則事件直接驅動**別人畫面上**那隻舞者頭上的表情與腳下的 combo 爆發。
    /// 判太鬆 → 壞掉/偽造的封包能在任意時刻放特效;判太嚴 → 真的里程碑被吃掉,症狀是
    /// 「遠端的人 combo 特效有時候沒出來」(這正是 <see cref="AcceptsCombo_Allows_A_Smaller_Value"/> 那條的由來)。
    /// </summary>
    public class ComboMilestoneRulesTests
    {
        [Test]
        public void Valid_Milestones_Are_The_50_Boundaries_From_50_Up()
        {
            Assert.IsTrue(ComboMilestoneRules.IsValid(50));
            Assert.IsTrue(ComboMilestoneRules.IsValid(100));
            Assert.IsTrue(ComboMilestoneRules.IsValid(1500));

            // 第一個里程碑是 50 —— 0 與負數是壞封包,不是「還沒到」。
            Assert.IsFalse(ComboMilestoneRules.IsValid(0));
            Assert.IsFalse(ComboMilestoneRules.IsValid(-50));
            // 非邊界值:本機根本不會送,收到就是偽造的。
            Assert.IsFalse(ComboMilestoneRules.IsValid(75));
            Assert.IsFalse(ComboMilestoneRules.IsValid(101));
            // 防呆上界。
            Assert.IsFalse(ComboMilestoneRules.IsValid(ComboMilestoneRules.MaxCombo + 50));
        }

        [Test]
        public void AcceptsCombo_Rejects_The_Same_Value_Twice()
        {
            // 唯一要擋的是「同一則被重送」—— 放行的話同一個特效會在別人畫面上放兩次。
            Assert.IsFalse(ComboMilestoneRules.AcceptsCombo(100, 100));
        }

        [Test]
        public void AcceptsCombo_Allows_A_Smaller_Value()
        {
            // 🔴 這一條就是「遠端的人 combo 特效有時候沒出來」的修正。
            //
            // combo 會斷:打到 250 之後 Bad 一下歸零,再爬回 50 / 100 / 150 —— 每一個都是**真的**
            // 里程碑,本機那台也確實會各放一次特效(ScreenGameplay 的 _lastMilestone 只擋「與上一次
            // 相同的值」,不要求遞增)。server 若用「必須比上一則大」去重,這一整段在別人畫面上會
            // 完全消失,直到他重新超過斷掉前的最高點。
            Assert.IsTrue(ComboMilestoneRules.AcceptsCombo(250, 50));
            Assert.IsTrue(ComboMilestoneRules.AcceptsCombo(250, 100));
            Assert.IsTrue(ComboMilestoneRules.AcceptsCombo(50, 100));
        }

        [Test]
        public void MinInterval_Is_Far_Below_The_Fastest_Real_Milestone_Gap()
        {
            // 防洪門檻要寬到不可能誤殺:兩則里程碑之間一定隔著 50 次判定,而最密的譜面也才
            // ~50 判定/秒 → 最快也要一秒。門檻若逼近那個量級,就會開始吃掉真的里程碑。
            Assert.Less(ComboMilestoneRules.MinIntervalMs, 1000.0);

            Assert.IsFalse(ComboMilestoneRules.AcceptsAt(1000.0, 1000.0));
            Assert.IsFalse(ComboMilestoneRules.AcceptsAt(1000.0, 1000.0 + ComboMilestoneRules.MinIntervalMs - 1.0));
            Assert.IsTrue(ComboMilestoneRules.AcceptsAt(1000.0, 1000.0 + ComboMilestoneRules.MinIntervalMs));
            Assert.IsTrue(ComboMilestoneRules.AcceptsAt(1000.0, 2500.0));
        }
    }
}
