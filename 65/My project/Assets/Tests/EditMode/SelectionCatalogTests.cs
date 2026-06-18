using NUnit.Framework;
using Sdo.UI.Catalog;

namespace Sdo.Tests
{
    public class SelectionCatalogTests
    {
        [Test]
        public void Stages_Contains_Known_Entries()
        {
            Assert.Greater(StageCatalog.Stages.Count, 30);
            Assert.AreEqual("SCN0009", StageCatalog.Get(9).Folder);
            Assert.AreEqual(9, StageCatalog.Default.Id);
        }

        [Test]
        public void Stage_Unknown_Returns_Default()
            => Assert.IsNotNull(StageCatalog.Get(99999));

        [Test]
        public void NoteSkins_Available_NonEmpty_And_Default_Resolves()
        {
            Assert.GreaterOrEqual(NoteSkinCatalog.Available.Count, 1);
            Assert.IsNotNull(NoteSkinCatalog.Get(NoteSkinCatalog.DefaultId));
        }
    }
}
