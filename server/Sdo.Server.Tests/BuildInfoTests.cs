using NUnit.Framework;
using Sdo.Server;

namespace Sdo.Server.Tests
{
    /// <summary>
    /// 啟動 banner 的版本字串。會出錯的地方全在「git 沒給我們想要的東西」那一側:
    /// 不在 tag 上、根本沒有 .git、或 git 把錯誤訊息寫到 stderr 而被一起收進來。
    /// </summary>
    public class BuildInfoTests
    {
        [Test]
        public void ComposesTheSameShapeAsTheClientWindowTitle()
        {
            // client 標題是「dance v1.5.0-dev-d41da」,server 這半段要長得一樣才好比對。
            Assert.AreEqual("v1.5.0-dev-d41da", BuildInfo.Compose(null, "v1.5.0", "d41da"));
        }

        [Test]
        public void ExactTagWinsOverNearestTag()
        {
            Assert.AreEqual("v1.5.0", BuildInfo.Compose("v1.5.0", "v1.5.0", "d41da"));
        }

        [Test]
        public void NoGitAtAllIsUnknownRatherThanBlank()
        {
            // 空白版本會讓 banner 印成「sdo-server   (protocol v1)」——看起來像壞了,而且什麼都沒說。
            Assert.AreEqual("unknown", BuildInfo.Compose(null, null, null));
            Assert.AreEqual("unknown", BuildInfo.Compose("", "  ", ""));
        }

        [Test]
        public void GitErrorTextNeverLeaksIntoTheVersion()
        {
            // 不在 tag 上時 git 會往 stderr 寫這種東西;MSBuild 的 ConsoleToMSBuild 會一起收進來。
            Assert.AreEqual("dev-d41da",
                BuildInfo.Compose("fatal: no tag exactly matches '9fceb02'", null, "d41da"));
            Assert.IsNull(BuildInfo.Sane("fatal: not a git repository"));
            Assert.IsNull(BuildInfo.Sane("warning: something with spaces"));
        }

        [Test]
        public void SaneKeepsRealTagsAndHashes()
        {
            Assert.AreEqual("v1.5.0", BuildInfo.Sane("  v1.5.0\n"));
            Assert.AreEqual("d41da", BuildInfo.Sane("d41da"));
            Assert.IsNull(BuildInfo.Sane(new string('x', 65)), "長度異常的東西不是版本");
        }

        [Test]
        public void RealAssemblyReportsAVersion()
        {
            // 這個測試專案由同一次 build 產生 → 至少不該是空的。
            Assert.IsNotEmpty(BuildInfo.Version);
            Assert.IsTrue(BuildInfo.Banner.StartsWith("sdo-server "), BuildInfo.Banner);
        }
    }

    /// <summary>
    /// client/server 版本比對。重點是**什麼時候不該喊** —— 每次連線都跳一句沒有意義的警告,
    /// 下次真的版本不一致時就沒人會看了。
    /// </summary>
    public class BuildVersionMatchTests
    {
        [Test]
        public void SameCommitDespiteDifferentProductNames()
        {
            Assert.IsTrue(BuildVersionMatch.Same("dance v1.5.0-dev-d41da", "v1.5.0-dev-d41da"));
            Assert.IsTrue(BuildVersionMatch.Same("dance v1.5.0-dev-d41da", "sdo-server v1.5.0-dev-d41da"));
        }

        [Test]
        public void DifferentCommitIsReported()
        {
            Assert.IsFalse(BuildVersionMatch.Same("dance v1.5.0-dev-d41da", "sdo-server v1.5.0-dev-50359"));
            Assert.IsFalse(BuildVersionMatch.Same("dance v1.4.0", "sdo-server v1.5.0"));
        }

        [Test]
        public void UnknownVersionsDoNotWarn()
        {
            // Unity Editor 裡 productName 只是「dance」;tarball 建的 server 是「unknown」。
            // 這兩種情況警告不了任何事,只會每次連線洗一行。
            Assert.IsTrue(BuildVersionMatch.Same("dance", "sdo-server v1.5.0-dev-d41da"));
            Assert.IsTrue(BuildVersionMatch.Same("dance v1.5.0-dev-d41da", "sdo-server unknown"));
            Assert.IsTrue(BuildVersionMatch.Same("", "sdo-server v1.5.0-dev-d41da"));
            Assert.IsTrue(BuildVersionMatch.Same(null, null));
        }

        [Test]
        public void CaseDoesNotMatter()
        {
            Assert.IsTrue(BuildVersionMatch.Same("dance V1.5.0-DEV-D41DA", "sdo-server v1.5.0-dev-d41da"));
        }
    }
}
