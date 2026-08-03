using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Game;
using Sdo.Net;
using Sdo.UI.Services;

namespace Sdo.Tests
{
    /// <summary>
    /// 「這一局站哪一種隊形」的解析(<see cref="RoundFormationChoice.Pick"/>)。
    ///
    /// 與 <see cref="RoundStageChoiceTests"/> 同一件事的另一半:結果只餵這一局的
    /// <c>ScreenGameplay.formationType</c>,房間設定(0..3,3=隨機)不能被它改掉。
    /// </summary>
    public class RoundFormationChoiceTests
    {
        private const int Random = NetResolvedRound.FormationTypeCount;   // 3 = 房間設定的「隨機」

        private sealed class FakeRng
        {
            private readonly Queue<int> _values;
            public int LastMin = -1, LastMaxExclusive = -1;
            public int Calls;
            public FakeRng(params int[] values) { _values = new Queue<int>(values); }
            public int Range(int min, int maxExclusive)
            {
                Calls++;
                LastMin = min; LastMaxExclusive = maxExclusive;
                return _values.Count > 0 ? _values.Dequeue() : min;
            }
        }

        [Test]
        public void A_Chosen_Formation_Is_Used_As_Is_And_Never_Rolls()
        {
            var rng = new FakeRng(2);
            Assert.AreEqual(0, RoundFormationChoice.Pick(0, rng.Range));
            Assert.AreEqual(1, RoundFormationChoice.Pick(1, rng.Range));
            Assert.AreEqual(2, RoundFormationChoice.Pick(2, rng.Range));
            Assert.AreEqual(0, rng.Calls, "指定隊形不該去抽");
        }

        [Test]
        public void Random_Rolls_Within_The_Three_Official_Tables()
        {
            var rng = new FakeRng(1);
            Assert.AreEqual(1, RoundFormationChoice.Pick(Random, rng.Range));
            Assert.AreEqual(0, rng.LastMin);
            Assert.AreEqual(Random, rng.LastMaxExclusive, "只有 0..2 三張官方個人隊形表");
        }

        [Test]
        public void Random_Rerolls_Every_Round()
        {
            var rng = new FakeRng(2, 0, 1);
            Assert.AreEqual(2, RoundFormationChoice.Pick(Random, rng.Range));
            Assert.AreEqual(0, RoundFormationChoice.Pick(Random, rng.Range));
            Assert.AreEqual(1, RoundFormationChoice.Pick(Random, rng.Range));
        }

        [Test]
        public void Out_Of_Range_Values_Are_Clamped()
        {
            var rng = new FakeRng(99);
            Assert.AreEqual(2, RoundFormationChoice.Pick(Random, rng.Range), "壞掉的 RNG 也不能送出越界的隊形");
            Assert.AreEqual(0, RoundFormationChoice.Pick(-4, null));
            // 4 以上在設定裡不存在;真的出現就當「隨機」,沒有 RNG 時退回夾進範圍的值(而不是丟例外)。
            Assert.AreEqual(2, RoundFormationChoice.Pick(9, null));
        }

        [Test]
        public void Protocol_And_Catalog_Agree_On_How_Many_Formations_Exist()
        {
            // 這個 Pick 的上界同時綁著協定(server 驗 formationType)與座標表 —— 兩邊漂移就會抽出畫不出來的隊形。
            Assert.AreEqual(FormationCatalog.TypeCount, NetResolvedRound.FormationTypeCount);
        }
    }
}
