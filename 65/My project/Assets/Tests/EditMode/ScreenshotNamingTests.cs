using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    public class ScreenshotNamingTests
    {
        [Test]
        public void FileName_Is_yyyyMMdd_HHmmss_Jpg()
        {
            var name = ScreenshotNaming.FileName(new DateTime(2026, 4, 9, 12, 7, 11));
            Assert.AreEqual("20260409_120711.jpg", name);   // matches the reference format
        }

        [Test]
        public void FileName_Zero_Pads_All_Fields()
        {
            var name = ScreenshotNaming.FileName(new DateTime(2026, 1, 2, 3, 4, 5));
            Assert.AreEqual("20260102_030405.jpg", name);
        }

        [Test]
        public void UniqueFileName_Returns_Base_When_Free()
        {
            var when = new DateTime(2026, 4, 9, 12, 7, 11);
            var name = ScreenshotNaming.UniqueFileName(when, _ => false);
            Assert.AreEqual("20260409_120711.jpg", name);
        }

        [Test]
        public void UniqueFileName_Suffixes_On_Same_Second_Collision()
        {
            var when = new DateTime(2026, 4, 9, 12, 7, 11);
            var taken = new HashSet<string> { "20260409_120711.jpg", "20260409_120711_2.jpg" };
            var name = ScreenshotNaming.UniqueFileName(when, taken.Contains);
            Assert.AreEqual("20260409_120711_3.jpg", name);
        }

        [Test]
        public void UniqueFileName_Null_Predicate_Returns_Base()
        {
            var when = new DateTime(2026, 4, 9, 12, 7, 11);
            Assert.AreEqual("20260409_120711.jpg", ScreenshotNaming.UniqueFileName(when, null));
        }
    }
}
