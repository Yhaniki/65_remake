using NUnit.Framework;
using Sdo.Game;
using Sdo.Settings;

namespace Sdo.Tests
{
    /// <summary>
    /// MMD 顯示是**兩個互相獨立的功能**，這個檔案就是在釘死那條界線：
    ///   ① 我要用 MMD 模型（＝設定裡選了一個模型；「(不使用)」＝不用，沒有第二個總開關）
    ///   ② 我要看到別人的 MMD 模型（<c>mmdShowOthers</c>）
    ///
    /// 回歸的那個 bug：同房沒穿 MMD 的人（packId 空）走到了「沒 packId ⇒ 用設定裡選的那個」這條回退路徑，
    /// 於是**我自己的模型被貼到了每一個人身上**。畫面上看起來像「全場都是初音」，很難聯想到根因。
    /// </summary>
    public class MmdTwoFeaturesTests
    {
        // ------------------------------------------------------------------ ① 我自己
        [Test]
        public void Local_Uses_The_Selected_Model_And_Nothing_Else_Is_Needed()
        {
            // 選了模型＝要用它。不需要再開一個開關。
            Assert.AreEqual(MmdSource.LocalModel,
                MmdDisplayPolicy.SourceFor(remote: false, declaredPack: "", localModelSelected: true, showOthers: false));
        }

        [Test]
        public void Local_Without_A_Selected_Model_Stays_On_The_Sdo_Body()
        {
            Assert.AreEqual(MmdSource.Sdo,
                MmdDisplayPolicy.SourceFor(remote: false, declaredPack: "", localModelSelected: false, showOthers: true));
        }

        [Test]
        public void Seeing_Other_Peoples_Models_Does_Not_Put_One_On_Me()
        {
            // 「看別人的」開著、我自己沒選模型 → 我還是 SDO 原角色。兩個功能不能互相牽動。
            Assert.AreEqual(MmdSource.Sdo,
                MmdDisplayPolicy.SourceFor(remote: false, declaredPack: "", localModelSelected: false, showOthers: true));
        }

        // ------------------------------------------------------------------ ② 別人
        [Test]
        public void Remote_Without_A_Declared_Pack_Never_Borrows_My_Model()
        {
            // 🔴 這就是回歸的那一條：他沒穿 MMD → 他就是他的 SDO 穿搭，
            //    不管我自己選了什麼模型、也不管我有沒有開「看別人的」。
            Assert.AreEqual(MmdSource.Sdo,
                MmdDisplayPolicy.SourceFor(remote: true, declaredPack: "", localModelSelected: true, showOthers: true));
            Assert.AreEqual(MmdSource.Sdo,
                MmdDisplayPolicy.SourceFor(remote: true, declaredPack: null, localModelSelected: true, showOthers: true));
        }

        [Test]
        public void Remote_With_A_Declared_Pack_Uses_His_Own_Model()
        {
            Assert.AreEqual(MmdSource.RemoteModel,
                MmdDisplayPolicy.SourceFor(remote: true, declaredPack: "sha256:abc", localModelSelected: false, showOthers: true));
        }

        [Test]
        public void Remote_Is_Hidden_When_I_Turned_Off_Seeing_Other_Peoples_Models()
        {
            Assert.AreEqual(MmdSource.Sdo,
                MmdDisplayPolicy.SourceFor(remote: true, declaredPack: "sha256:abc", localModelSelected: true, showOthers: false));
        }

        [Test]
        public void I_Can_See_Other_Peoples_Models_While_Staying_Sdo_Myself()
        {
            // 反過來也要成立：我維持 SDO 原角色，但看得到別人的 MMD。
            Assert.AreEqual(MmdSource.RemoteModel,
                MmdDisplayPolicy.SourceFor(remote: true, declaredPack: "sha256:abc", localModelSelected: false, showOthers: true));
        }

        // ------------------------------------------------------------------ 設定：淘汰 mmdEnabled
        [Test]
        public void Legacy_MmdEnabled_Off_Migrates_To_The_None_Choice()
        {
            // 舊檔關著 MMD → 升級後不能突然變成 MMD（最嚇人的一種「改版自己動了我的設定」）。
            RoomConfig.hasMmdEnabledKey = true;
            RoomConfig.legacyMmdEnabled = false;
            RoomConfig.mmdModel = "IkaHatunemiku2025";
            Assert.IsTrue(RoomConfig.MigrateLegacyMmdEnabled(), "搬遷過就要重寫一次檔案，把舊鍵清掉");
            Assert.IsTrue(RoomConfig.IsMmdNone(RoomConfig.mmdModel));
        }

        [Test]
        public void Legacy_MmdEnabled_On_Keeps_The_Chosen_Model()
        {
            RoomConfig.hasMmdEnabledKey = true;
            RoomConfig.legacyMmdEnabled = true;
            RoomConfig.mmdModel = "IkaHatunemiku2025";
            Assert.IsTrue(RoomConfig.MigrateLegacyMmdEnabled());
            Assert.AreEqual("IkaHatunemiku2025", RoomConfig.mmdModel);
        }

        [Test]
        public void Legacy_MmdEnabled_On_With_No_Model_Falls_Back_To_The_First_Installed()
        {
            // mmdEnabled=1 但 mmdModel 還是新版預設的「(不使用)」→ 兩者矛盾，以舊的總開關為準（空＝掃到的第一個）。
            RoomConfig.hasMmdEnabledKey = true;
            RoomConfig.legacyMmdEnabled = true;
            RoomConfig.mmdModel = RoomConfig.mmdModelNone;
            Assert.IsTrue(RoomConfig.MigrateLegacyMmdEnabled());
            Assert.AreEqual("", RoomConfig.mmdModel);
        }

        [Test]
        public void A_Config_Without_The_Legacy_Key_Is_Left_Alone()
        {
            RoomConfig.hasMmdEnabledKey = false;
            RoomConfig.mmdModel = "IkaHatunemiku2025";
            Assert.IsFalse(RoomConfig.MigrateLegacyMmdEnabled(), "沒有舊鍵就沒有要搬的東西，也不該害檔案重寫");
            Assert.AreEqual("IkaHatunemiku2025", RoomConfig.mmdModel);
        }

        [Test]
        public void The_None_Choice_Is_Recognised_Regardless_Of_Padding()
        {
            Assert.IsTrue(RoomConfig.IsMmdNone(RoomConfig.mmdModelNone));
            Assert.IsTrue(RoomConfig.IsMmdNone("  " + RoomConfig.mmdModelNone + " "), "手改設定檔很容易留下空白");
            Assert.IsFalse(RoomConfig.IsMmdNone(""), "留空＝掃到的第一個模型，不是「不使用」");
            Assert.IsFalse(RoomConfig.IsMmdNone("IkaHatunemiku2025"));
        }

        [SetUp]
        public void Reset()
        {
            RoomConfig.hasMmdEnabledKey = false;
            RoomConfig.legacyMmdEnabled = false;
            RoomConfig.mmdModel = RoomConfig.mmdModelNone;
            RoomConfig.mmdShowOthers = true;
        }

        [TearDown]
        public void Restore() => Reset();
    }
}
