using NUnit.Framework;
using Sdo.Osu;

namespace Sdo.Tests
{
    public class ExternalDifficultyPickerTests
    {
        // Assign returns [easyIdx, normalIdx, hardIdx]; hard = highest note count, filled downward, -1 = empty.

        [Test]
        public void None_All_Empty()
        {
            var s = ExternalDifficultyPicker.Assign(new int[0]);
            Assert.AreEqual(new[] { -1, -1, -1 }, s);
        }

        [Test]
        public void One_Chart_Goes_To_Hard_Only()
        {
            var s = ExternalDifficultyPicker.Assign(new[] { 800 });
            Assert.AreEqual(new[] { -1, -1, 0 }, s);   // easy/normal empty, hard = the only chart
        }

        [Test]
        public void Two_Charts_Fill_Hard_Then_Normal()
        {
            var s = ExternalDifficultyPicker.Assign(new[] { 500, 900 });
            Assert.AreEqual(new[] { -1, 0, 1 }, s);    // hard = idx1 (900), normal = idx0 (500), easy empty
        }

        [Test]
        public void Three_Charts_Ascending_To_Easy_Normal_Hard()
        {
            var s = ExternalDifficultyPicker.Assign(new[] { 50, 300, 100 });
            // desc: idx1(300), idx2(100), idx0(50) → hard=1, normal=2, easy=0
            Assert.AreEqual(new[] { 0, 2, 1 }, s);
        }

        [Test]
        public void Four_Charts_Keep_Top_Three_Only()
        {
            var s = ExternalDifficultyPicker.Assign(new[] { 10, 20, 30, 40 });
            // top-3 desc: idx3(40), idx2(30), idx1(20); idx0(10) dropped
            Assert.AreEqual(new[] { 1, 2, 3 }, s);
        }

        [Test]
        public void Ties_Break_By_Index()
        {
            var s = ExternalDifficultyPicker.Assign(new[] { 100, 100 });
            Assert.AreEqual(new[] { -1, 1, 0 }, s);    // equal → idx0 first (hard), idx1 (normal)
        }
    }
}
