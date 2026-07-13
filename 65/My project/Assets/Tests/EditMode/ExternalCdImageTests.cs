using System.IO;
using NUnit.Framework;
using Sdo.Game;
using Sdo.Osu;
using UnityEngine;

namespace Sdo.Tests
{
    /// <summary>
    /// The disc pipeline end to end (the part the pure tests can't reach): compose the disc from a real cover file,
    /// write it into the song's folder, record it in the sidecar — and then never build it again.
    /// </summary>
    public class ExternalCdImageTests
    {
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "sdo_cd_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_dir);
            ExternalCdImage.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            ExternalCdImage.Clear();
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* temp dir */ }
        }

        // A real image file on disk — a wide banner, the case the disc must crop rather than squash.
        private string Cover(string name, int w = 512, int h = 160)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var px = new Color32[w * h];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(200, 40, 40, 255);
            tex.SetPixels32(px);
            tex.Apply();
            var path = Path.Combine(_dir, name);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            return path;
        }

        private SongCatalog.Entry Entry(string gn, string songKey, string cover)
            => new SongCatalog.Entry
            {
                gn = gn, external = true, fileId = -1000,
                folderPath = _dir, songKey = songKey, imagePath = cover,
            };

        private string Sidecar() => File.ReadAllText(Path.Combine(_dir, SongSidecar.FileName));

        [Test]
        public void FirstSelection_WritesTheDiscAndRecordsIt()
        {
            var e = Entry("ext_1k.gn", "", Cover("banner.png"));

            var sprite = ExternalCdImage.Get(e);

            Assert.IsNotNull(sprite, "no disc came back");
            Assert.AreEqual(CdImageComposer.DefaultSize, (int)sprite.rect.width, "not the official disc canvas");
            Assert.AreEqual(CdImageComposer.DefaultSize, (int)sprite.rect.height);

            var disc = Path.Combine(_dir, "cd.png");
            FileAssert.Exists(disc);
            Assert.AreEqual(disc, e.cdPath, "the entry must point at the disc it just built");

            var recorded = SongSidecar.Find(SongSidecar.Parse(Sidecar()), "");
            Assert.AreEqual("cd.png", recorded.CdImage);
        }

        [Test]
        public void ARecordedDisc_IsReadBackAndNeverRebuilt()
        {
            var cover = Cover("banner.png");
            ExternalCdImage.Get(Entry("ext_1k.gn", "", cover));   // first run builds it
            ExternalCdImage.Clear();

            // Next launch: the scan hands the entry the recorded disc. Delete the cover it was built from — a rebuild
            // would now be impossible, so a disc coming back proves it was read, not composed.
            File.Delete(cover);
            var e = Entry("ext_1k.gn", "", "");
            e.cdPath = Path.Combine(_dir, "cd.png");

            var sprite = ExternalCdImage.Get(e);

            Assert.IsNotNull(sprite);
            Assert.AreEqual(CdImageComposer.DefaultSize, (int)sprite.rect.width);
        }

        [Test]
        public void EachSongOfAMultiSongFolder_GetsItsOwnDisc()
        {
            var a = Entry("ext_ak.gn", "audio:a.mp3", Cover("a_bg.png"));
            var b = Entry("ext_bk.gn", "audio:b.mp3", Cover("b_bg.png"));

            ExternalCdImage.Get(a);
            ExternalCdImage.Get(b);

            Assert.AreNotEqual(a.cdPath, b.cdPath, "two songs in one folder overwrote each other's disc");
            FileAssert.Exists(a.cdPath);
            FileAssert.Exists(b.cdPath);

            // One sidecar, one block per song — writing B's disc must not have eaten A's.
            var entries = SongSidecar.Parse(Sidecar());
            Assert.AreEqual(2, entries.Count);
            Assert.AreEqual(Path.GetFileName(a.cdPath), SongSidecar.Find(entries, "audio:a.mp3").CdImage);
            Assert.AreEqual(Path.GetFileName(b.cdPath), SongSidecar.Find(entries, "audio:b.mp3").CdImage);
        }

        [Test]
        public void TheDiscTheScannerFindsNextTime_IsTheOneWeJustWrote()
        {
            var e = Entry("ext_1k.gn", "", Cover("banner.png"));
            ExternalCdImage.Get(e);

            // What the next boot does: read the folder's sidecar back through the scanner's own path.
            var recorded = SongSidecar.Find(SongSidecar.Parse(Sidecar()), "");
            var onDisk = Path.Combine(_dir, recorded.CdImage);

            FileAssert.Exists(onDisk);
            Assert.AreEqual(e.cdPath, onDisk);
        }

        [Test]
        public void ASongWithNoCover_GetsNoDiscAndNoSidecar()
        {
            var e = Entry("ext_1k.gn", "", "");   // an osu folder that ships no image at all

            Assert.IsNull(ExternalCdImage.Get(e), "the NONE placeholder disc is the caller's job");
            Assert.IsFalse(File.Exists(Path.Combine(_dir, SongSidecar.FileName)), "nothing to record");
        }
    }
}
