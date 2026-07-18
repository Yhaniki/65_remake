using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests.EditMode
{
    public class BuildTitleTests
    {
        [Test]
        public void OnExactTag_ShowsProductAndTag()
        {
            // HEAD sits exactly on v1.0.0 -> no "-dev-" suffix.
            Assert.AreEqual("dance v1.0.0", BuildTitle.Format("dance", "v1.0.0", "v1.0.0", "719a9"));
        }

        [Test]
        public void AfterTag_ShowsNearestTagDevHash()
        {
            // HEAD is past v1.0.0 (exact-match failed -> null) -> tag + dev + 5-char hash.
            Assert.AreEqual("dance v1.0.0-dev-719a9", BuildTitle.Format("dance", null, "v1.0.0", "719a9"));
        }

        [Test]
        public void NoTagsButHaveHash_ShowsDevHash()
        {
            Assert.AreEqual("dance dev-719a9", BuildTitle.Format("dance", null, null, "719a9"));
        }

        [Test]
        public void NoGitAtAll_FallsBackToProductOnly()
        {
            Assert.AreEqual("dance", BuildTitle.Format("dance", null, null, null));
        }

        [Test]
        public void TrimsWhitespaceAndTreatsEmptyAsMissing()
        {
            // git output often carries a trailing newline; empty exact-match must be treated as "not on a tag".
            Assert.AreEqual("dance v1.0.0-dev-719a9", BuildTitle.Format("dance", "", "v1.0.0\n", " 719a9 "));
        }

        [Test]
        public void EmptyProduct_ReturnsVersionOnly()
        {
            Assert.AreEqual("v1.0.0", BuildTitle.Format("", "v1.0.0", "v1.0.0", "719a9"));
        }
    }
}
