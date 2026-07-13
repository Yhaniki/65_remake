using NUnit.Framework;
using Sdo.Osu;

namespace Sdo.Tests
{
    /// <summary>
    /// The CD disc built from a song's cover art. The point of the class is that the cover is CROPPED, not squashed —
    /// StepMania banners are ~4:1 and osu backgrounds 16:9, and stretching either into the square disc is exactly what
    /// this replaces — plus the disc geometry (rim, hub, transparent outside) that makes it look like the game's own
    /// ICONS discs.
    /// </summary>
    public class CdImageComposerTests
    {
        private const int Size = CdImageComposer.DefaultSize;   // 237
        private const int Mid = (Size - 1) / 2;                 // 118

        // A cover: black, with a white square of `marker` px centred in it.
        private static byte[] Cover(int w, int h, int marker)
        {
            var px = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++) px[i * 4 + 3] = 255;   // opaque black
            int x0 = (w - marker) / 2, y0 = (h - marker) / 2;
            for (int y = y0; y < y0 + marker; y++)
                for (int x = x0; x < x0 + marker; x++)
                {
                    int o = (y * w + x) * 4;
                    px[o] = px[o + 1] = px[o + 2] = 255;
                }
            return px;
        }

        private static byte A(byte[] img, int x, int y) => img[(y * Size + x) * 4 + 3];
        private static byte R(byte[] img, int x, int y) => img[(y * Size + x) * 4];
        private static byte G(byte[] img, int x, int y) => img[(y * Size + x) * 4 + 1];
        private static byte B(byte[] img, int x, int y) => img[(y * Size + x) * 4 + 2];

        // The white marker's width / height on the disc: bright pixels along the centre row / column. Measured only
        // near the centre — the disc's white RIM is bright too, and it would be counted as part of the marker.
        private const int Near = 40;   // < the hub's outer radius (32) + a margin; nowhere near the rim (103+)

        private static int RowRun(byte[] img)
        {
            int n = 0;
            for (int x = Mid - Near; x <= Mid + Near; x++) if (R(img, x, Mid) > 200 && A(img, x, Mid) > 200) n++;
            return n;
        }

        private static int ColRun(byte[] img)
        {
            int n = 0;
            for (int y = Mid - Near; y <= Mid + Near; y++) if (R(img, Mid, y) > 200 && A(img, Mid, y) > 200) n++;
            return n;
        }

        [Test]
        public void WideBanner_IsCroppedNotSquashed()
        {
            // 4:1 banner. Squashing it into the square disc would shrink the marker 4× horizontally and leave it
            // nearly full height; cropping keeps it SQUARE — scaled by 237/200, the same in both axes.
            // hubAlpha 0 takes the (semi-transparent) hub out of the measurement; the marker sits inside it.
            var disc = CdImageComposer.Compose(Cover(800, 200, 20), 800, 200, Size, hubAlpha: 0);

            int w = RowRun(disc), h = ColRun(disc);
            double expected = 20 * (Size / 200.0);   // cover-scaled: ~23.7px
            Assert.AreEqual(expected, w, 2.5, "marker width");
            Assert.AreEqual(expected, h, 2.5, "marker height");
            Assert.AreEqual(w, h, 2, "the marker came out non-square → the cover was stretched");
        }

        [Test]
        public void TallCover_IsCroppedNotSquashed()
        {
            var disc = CdImageComposer.Compose(Cover(200, 800, 20), 200, 800, Size, hubAlpha: 0);

            int w = RowRun(disc), h = ColRun(disc);
            Assert.AreEqual(20 * (Size / 200.0), w, 2.5, "marker width");
            Assert.AreEqual(w, h, 2, "the marker came out non-square → the cover was stretched");
        }

        [Test]
        public void SquareCover_KeepsItsScale()
        {
            var disc = CdImageComposer.Compose(Cover(474, 474, 40), 474, 474, Size, hubAlpha: 0);

            Assert.AreEqual(40 * (Size / 474.0), RowRun(disc), 2.0);   // exactly half-scaled, nothing cropped
        }

        [Test]
        public void Output_IsTheOfficialDiscCanvas()
        {
            var disc = CdImageComposer.Compose(Cover(64, 64, 8), 64, 64);
            Assert.AreEqual(Size * Size * 4, disc.Length);   // 237×237 RGBA, like ICONS/<id>.PNG
        }

        [Test]
        public void OutsideTheDisc_IsTransparent()
        {
            var disc = CdImageComposer.Compose(Cover(400, 400, 40), 400, 400, Size);

            Assert.AreEqual(0, A(disc, 0, 0), "corner");
            Assert.AreEqual(0, A(disc, Size - 1, 0), "corner");
            Assert.AreEqual(0, A(disc, Mid, 0), "above the rim");     // r=118 > rim
            Assert.AreEqual(0, A(disc, Size - 1, Mid), "right of the rim");
        }

        [Test]
        public void TheRim_IsAWhiteRing()
        {
            var disc = CdImageComposer.Compose(Cover(400, 400, 40), 400, 400, Size);

            int r = (int)CdImageComposer.DiscRadiusRef;   // 106 — the middle of the white rim
            foreach (var p in new[] { (Mid + r, Mid), (Mid - r, Mid), (Mid, Mid + r), (Mid, Mid - r) })
            {
                Assert.AreEqual(255, A(disc, p.Item1, p.Item2), "rim must be opaque");
                Assert.Greater(R(disc, p.Item1, p.Item2), 240, "rim must be white");
                Assert.Greater(G(disc, p.Item1, p.Item2), 240);
                Assert.Greater(B(disc, p.Item1, p.Item2), 240);
            }
        }

        [Test]
        public void TheHub_ShowsTheCoverThroughItsLightBands()
        {
            // A red cover under the default (≈80% opaque) hub: the centre must be neither the raw cover nor the flat
            // hub colour — that blend is what the CD generator tool produces and what the disc is supposed to look like.
            var red = new byte[64 * 64 * 4];
            for (int i = 0; i < 64 * 64; i++) { red[i * 4] = 255; red[i * 4 + 3] = 255; }

            var disc = CdImageComposer.Compose(red, 64, 64, Size);

            Assert.AreEqual(255, A(disc, Mid, Mid), "the spindle hole is filled, not punched out");
            Assert.Greater(R(disc, Mid, Mid), 210, "hub light band (216) tinted by the red cover");
            Assert.Less(R(disc, Mid, Mid), 255, "the raw cover is showing through unblended");
            Assert.Greater(G(disc, Mid, Mid), 100, "the hub's own colour is missing — cover drawn on top of the hub?");

            // The dark hub ring (r=16..18) is opaque: the cover must not tint it.
            int x = Mid + 17;
            Assert.AreEqual(133, R(disc, x, Mid), 6);
            Assert.AreEqual(148, G(disc, x, Mid), 6);
            Assert.AreEqual(181, B(disc, x, Mid), 6);
        }

        [Test]
        public void CoverAlpha_IsCarriedThrough()
        {
            // A fully transparent cover leaves the disc's cover area transparent (the rim/hub still draw).
            var clear = new byte[64 * 64 * 4];

            var disc = CdImageComposer.Compose(clear, 64, 64, Size, hubAlpha: 0);

            Assert.AreEqual(0, A(disc, Mid + 60, Mid), "transparent cover became opaque");
            Assert.AreEqual(255, A(disc, Mid + (int)CdImageComposer.DiscRadiusRef, Mid), "the rim must still be there");
        }

        [Test]
        public void BadInput_YieldsNothing()
        {
            Assert.IsNull(CdImageComposer.Compose(null, 10, 10));
            Assert.IsNull(CdImageComposer.Compose(new byte[4], 0, 0));
            Assert.IsNull(CdImageComposer.Compose(new byte[4], 10, 10), "buffer smaller than the declared size");
        }
    }
}
