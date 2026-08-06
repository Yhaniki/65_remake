using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// 釘死「那四根旋鈕只調我自己的模型」這條界線。
    ///
    /// 回歸的 bug:剛在房間裡下載好別人的模型,上面那格頭貼裡他的頭髮被撐得膨起來 —— 身體是照他的
    /// physics.ini 建的沒錯,但建完馬上被本機 config.ini 的重力/剛性/碰撞半徑再蓋一次,那份跟著模型包
    /// 傳過來的檔案等於作廢(碰撞半徑實測差 1.5 倍)。大小(mmdScale)同一件事,而且更隱蔽:改倍率時
    /// 只重建本機那幾隻,但**新建**的遠端角色會吃到當下的值 → 先載的人正常、後載的人變形。
    /// </summary>
    public class MmdRigTuningTests
    {
        // 本機的四根旋鈕都不是預設值 —— 這樣「有沒有被套用」才看得出來。
        private const float Size = 1.4f, Grav = 2.5f, Stiff = 0.5f, Col = 1.8f;

        private static MmdRigTuning For(bool remote)
            => MmdTuningPolicy.For(remote, Size, Grav, Stiff, Col);

        [Test]
        public void Local_Eats_Every_Knob_From_ConfigIni()
        {
            var t = For(remote: false);
            Assert.AreEqual(Size, t.SizeMul, 1e-6f);
            Assert.AreEqual(Grav, t.Gravity, 1e-6f);
            Assert.AreEqual(Stiff, t.Stiffness, 1e-6f);
            Assert.AreEqual(Col, t.ColliderScale, 1e-6f);
        }

        [Test]
        public void Remote_Cloth_Stays_On_The_Models_Own_Physics()
        {
            // 🔴 這就是回歸的那一條:別人的模型自帶 physics.ini(跟著模型包傳過來),
            //    本機的旋鈕再蓋上去就等於把它作廢 → 頭髮被撐開。
            var t = For(remote: true);
            Assert.AreEqual(MmdTuningPolicy.NeutralGravity, t.Gravity, 1e-6f);
            Assert.AreEqual(MmdTuningPolicy.NeutralStiffness, t.Stiffness, 1e-6f);
            Assert.AreEqual(MmdTuningPolicy.NeutralCollider, t.ColliderScale, 1e-6f);
        }

        [Test]
        public void Remote_Size_Is_Just_The_Height_Match()
        {
            // 「這個模型看起來偏大/偏小」是我對**我那個**模型的修正,別人的模型只會被我調變形。
            Assert.AreEqual(MmdTuningPolicy.NeutralSize, For(remote: true).SizeMul, 1e-6f);
        }

        [Test]
        public void Neutral_Stiffness_Is_The_Panel_Default()
        {
            // MmdAvatar.TunePhysics 把面板刻度換算成 Magica 的倍率時,拿的就是這個基準(stiffMul 剛好 1×);
            // 對不上的話「中性」其實會偷偷調硬或調軟別人的頭髮。
            Assert.AreEqual(0.12f, MmdTuningPolicy.NeutralStiffness, 1e-6f);
            Assert.AreEqual(1f, MmdTuningPolicy.NeutralGravity, 1e-6f);
            Assert.AreEqual(1f, MmdTuningPolicy.NeutralCollider, 1e-6f);
            Assert.AreEqual(1f, MmdTuningPolicy.NeutralSize, 1e-6f);
        }

        [Test]
        public void Neutral_Is_What_Remote_Gets()
        {
            var n = MmdTuningPolicy.Neutral;
            var t = For(remote: true);
            Assert.AreEqual(n.SizeMul, t.SizeMul, 1e-6f);
            Assert.AreEqual(n.Gravity, t.Gravity, 1e-6f);
            Assert.AreEqual(n.Stiffness, t.Stiffness, 1e-6f);
            Assert.AreEqual(n.ColliderScale, t.ColliderScale, 1e-6f);
        }
    }
}
