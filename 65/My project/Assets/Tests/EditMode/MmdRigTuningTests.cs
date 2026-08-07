using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// 釘死「那四根旋鈕只調我自己的模型」這條界線。
    ///
    /// 回歸的 bug:剛在房間裡下載好別人的模型,上面那格頭貼裡他的頭髮被撐得膨起來 —— 身體是照他的
    /// physics.ini 建的沒錯,但建完馬上被本機 config.ini 的重力/剛性/碰撞半徑再蓋一次,那份跟著模型包
    /// 傳過來的檔案等於作廢(碰撞半徑實測差 1.5 倍)。
    ///
    /// 大小(mmdScale)是**唯一會跟著人走**的那一個,但走的是「他自己宣告的值」而不是我這台的旋鈕:
    /// 拿我的旋鈕去乘別人的模型只是把他變形(而且改倍率時只重建本機那幾隻,新建的遠端角色卻會吃到
    /// 當下的值 → 先載的人正常、後載的人變形);完全不傳的話,他在自己畫面上與在我畫面上是兩個大小。
    /// </summary>
    public class MmdRigTuningTests
    {
        // 本機的四根旋鈕都不是預設值 —— 這樣「有沒有被套用」才看得出來。
        private const float Size = 1.4f, Grav = 2.5f, Stiff = 0.5f, Col = 1.8f;

        /// <summary>他自己宣告的大小(＝他那台的 mmdScale,隨外觀廣播過來),刻意與本機的不同。</summary>
        private const float Declared = 0.8f;

        private static MmdRigTuning For(bool remote)
            => MmdTuningPolicy.For(remote, Size, Grav, Stiff, Col);

        private static MmdRigTuning ForRemoteDeclaring(float declared)
            => MmdTuningPolicy.For(true, Size, Grav, Stiff, Col, declared);

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
        public void Remote_Size_Ignores_MyKnob()
        {
            // 「這個模型看起來偏大/偏小」是**我對我那個模型**的修正,套到別人的模型上只是把他變形。
            Assert.AreNotEqual(Size, ForRemoteDeclaring(Declared).SizeMul, "本機的旋鈕漏進了別人的身體");
            // 他什麼都沒宣告(舊 client / 沒調過)→ 只做自動對齊身高。
            Assert.AreEqual(MmdTuningPolicy.NeutralSize, For(remote: true).SizeMul, 1e-6f);
        }

        [Test]
        public void Remote_Size_Follows_WhatHeDeclared()
        {
            // 🔴 這就是「他調的大小要跟著他」:少了它,同一個人在自己畫面上與在我畫面上是兩個尺寸,
            //    而且我這邊他頭上的名字會插進他頭裡(名字高度照畫出來的身高算,見 MmdHeadroom)。
            Assert.AreEqual(Declared, ForRemoteDeclaring(Declared).SizeMul, 1e-6f);
            Assert.AreEqual(2f, ForRemoteDeclaring(2f).SizeMul, 1e-6f);
        }

        [Test]
        public void Remote_Size_IsClamped_SoAHostileValueCantExplode()
        {
            // 別人送來的數字不可信:0/負數 = 「他沒說」→ 1×(夾成下限的話一個亂填的 0 會讓他縮成一個點);
            // 太大太小一律夾回可用範圍。
            Assert.AreEqual(1f, ForRemoteDeclaring(0f).SizeMul, 1e-6f);
            Assert.AreEqual(1f, ForRemoteDeclaring(-3f).SizeMul, 1e-6f);
            Assert.AreEqual(Sdo.Osu.MmdModelRef.MaxScale, ForRemoteDeclaring(999f).SizeMul, 1e-6f);
            Assert.AreEqual(Sdo.Osu.MmdModelRef.MinScale, ForRemoteDeclaring(0.01f).SizeMul, 1e-6f);
        }

        [Test]
        public void Remote_Cloth_StaysNeutral_EvenWhenHeDeclaresASize()
        {
            // 大小跟著人走,布料**不跟** —— 那是模型自己的東西(physics.ini 跟著模型包傳過來)。
            var t = ForRemoteDeclaring(Declared);
            Assert.AreEqual(MmdTuningPolicy.NeutralGravity, t.Gravity, 1e-6f);
            Assert.AreEqual(MmdTuningPolicy.NeutralStiffness, t.Stiffness, 1e-6f);
            Assert.AreEqual(MmdTuningPolicy.NeutralCollider, t.ColliderScale, 1e-6f);
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
        public void Neutral_Is_What_ARemoteWhoDeclaredNothing_Gets()
        {
            // 舊 client / 沒調過大小的人:整組都是中性值 —— 這條路要原封不動地活著。
            var n = MmdTuningPolicy.Neutral;
            var t = For(remote: true);
            Assert.AreEqual(n.SizeMul, t.SizeMul, 1e-6f);
            Assert.AreEqual(n.Gravity, t.Gravity, 1e-6f);
            Assert.AreEqual(n.Stiffness, t.Stiffness, 1e-6f);
            Assert.AreEqual(n.ColliderScale, t.ColliderScale, 1e-6f);
        }
    }
}
