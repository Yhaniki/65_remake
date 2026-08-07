using NUnit.Framework;
using Sdo.Osu;

namespace Sdo.Tests
{
    /// <summary>
    /// 「他穿的是哪一個模型」的完整參照(<see cref="MmdModelRef"/>)＝ packId ＋ 包內是哪一個 .pmx。
    ///
    /// 這一段存在的唯一理由:**packId 是整個資料夾的指紋,而一個資料夾可以有好幾個 .pmx。**
    /// 少了包內路徑,收端只能反推,而反推挑錯不會有任何錯誤訊息 —— 畫面上是「那個人變成同一包裡的
    /// 另一個東西」(實測踩過:NerissaRavencroft/ 裡的角色本體、影子、一把槍 → 別人看到一支槍在跳舞)。
    /// </summary>
    public class MmdModelRefTests
    {
        private const string Pack = "sha256:0123456789abcdef0123456789abcdef";

        [Test]
        public void Join_KeepsBothHalves_AndSplitsBackExactly()
        {
            string r = MmdModelRef.Join(Pack, "nerissaravencroft.pmx");
            Assert.AreEqual(Pack, MmdModelRef.PackOf(r));
            Assert.AreEqual("nerissaravencroft.pmx", MmdModelRef.FileOf(r));
        }

        [Test]
        public void APackIdAlone_IsStillAValidRef()
        {
            // 舊 client 只送 packId(而且一包只有一個模型時本來就不必說)—— 這條路要原封不動地活著,
            // 收端拿到空的包內路徑就自己挑。
            Assert.AreEqual(Pack, MmdModelRef.Join(Pack, ""));
            Assert.AreEqual(Pack, MmdModelRef.PackOf(Pack));
            Assert.AreEqual("", MmdModelRef.FileOf(Pack));
        }

        [Test]
        public void NoPack_MeansNoRef_EvenWithAFile()
        {
            // 沒有 packId = 這個人沒穿模型。此時帶著一個檔名毫無意義,而且會讓收端以為他穿了。
            Assert.AreEqual("", MmdModelRef.Join("", "m.pmx"));
            Assert.AreEqual("", MmdModelRef.Join(null, "m.pmx"));
            Assert.AreEqual("", MmdModelRef.PackOf(""));
            Assert.AreEqual("", MmdModelRef.FileOf(null));
        }

        [Test]
        public void TheFileHalf_IsNormalised_SoBothMachinesAgree()
        {
            // 傳輸清單一律小寫 + '/'(SafeRelPath.Normalize),收端磁碟上落地的檔名也是那個形式。
            // 送出端如果照搬 Windows 的原始大小寫,對得起來就純屬運氣。
            Assert.AreEqual(Pack + "|pmx/model.pmx", MmdModelRef.Join(Pack, @"PMX\Model.PMX"));
        }

        [Test]
        public void ASeparatorCanNotAppearInsideEitherHalf()
        {
            // 挑 '|' 就是為了這個:packId 是 sha256:+hex,而 SafeRelPath 明確擋掉路徑裡的 '|'。
            Assert.IsFalse(MmdModelRef.IsSafeFile("evil|x.pmx"));
            Assert.IsFalse(Pack.Contains("|"));
        }

        // ---------------------------------------------------------------- 收端會拿它去 Combine 真實路徑
        [Test]
        public void IsSafeFile_RefusesPathTraversal()
        {
            foreach (var bad in new[]
            {
                "../../windows/system32/evil.pmx",
                "/abs/model.pmx",
                @"C:\models\x.pmx",
                "a/../../b.pmx",
                "model.pmx:stream.pmx",
            })
                Assert.IsFalse(MmdModelRef.IsSafeFile(bad), "危險路徑被放行:" + bad);
        }

        [Test]
        public void IsSafeFile_RefusesAnythingThatIsNotAPmx()
        {
            // 收端唯一會拿它做的事就是解析成模型。放行別的副檔名等於讓別人指定我要開哪個檔。
            Assert.IsFalse(MmdModelRef.IsSafeFile("model.png"));
            Assert.IsFalse(MmdModelRef.IsSafeFile("readme.txt"));
            Assert.IsFalse(MmdModelRef.IsSafeFile(""));
            Assert.IsFalse(MmdModelRef.IsSafeFile(null));
            Assert.IsTrue(MmdModelRef.IsSafeFile("pmx/la+darknesss.pmx"));
        }

        // ---------------------------------------------------------------- 送出端:算出包內路徑
        [Test]
        public void RelPathUnder_IsWhatTheOtherSideWillLookFor()
        {
            Assert.AreEqual("nerissaravencroft.pmx",
                MmdModelRef.RelPathUnder(@"C:\DATA\ADDON\MODEL\NerissaRavencroft",
                                         @"C:\DATA\ADDON\MODEL\NerissaRavencroft\NerissaRavencroft.pmx"));
            // .pmx 在包裡自己一層的包(ラプラス:PMX/*.pmx + sourceimages/*.tga)。
            Assert.AreEqual("pmx/la+darknesss.pmx",
                MmdModelRef.RelPathUnder(@"C:\M\laplus", @"C:\M\laplus\PMX\la+darknesss.pmx"));
        }

        [Test]
        public void RelPathUnder_ToleratesMixedSeparatorsAndTrailingSlash()
        {
            Assert.AreEqual("m.pmx", MmdModelRef.RelPathUnder(@"C:\M\pack\", "C:/M/pack/m.pmx"));
        }

        [Test]
        public void RelPathUnder_IsEmpty_WhenItIsNotUnderThePack()
        {
            // 空 = 「說不出來」→ 送出端就不宣告,收端自己挑。**絕不能**因為算錯而送出一個
            // 指向別處的路徑:那會讓對面永遠找不到而每次都退回猜測。
            Assert.AreEqual("", MmdModelRef.RelPathUnder(@"C:\M\pack", @"C:\M\other\m.pmx"));
            Assert.AreEqual("", MmdModelRef.RelPathUnder(@"C:\M\pack", @"C:\M\pack"));
            Assert.AreEqual("", MmdModelRef.RelPathUnder("", @"C:\M\pack\m.pmx"));
            Assert.AreEqual("", MmdModelRef.RelPathUnder(@"C:\M\pack", null));
            // 前綴長得像但不是同一層(pack vs pack2)—— 純字串比對最容易在這裡出錯。
            Assert.AreEqual("", MmdModelRef.RelPathUnder(@"C:\M\pack", @"C:\M\pack2\m.pmx"));
        }
    }
}
