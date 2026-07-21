using System.Collections.Generic;
using System.IO;
using Sdo.Osu;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// The song-select disc of an external (user <c>Songs/</c>) song.
    ///
    /// The disc is COMPOSED ONCE, ever: <see cref="CdImageComposer"/> cuts the song's cover art into the CD
    /// (cover-cropped, never stretched — a wide StepMania banner is matched to the disc's height and its sides are cut
    /// off — then rimmed and hubbed like the official ICONS discs), the PNG is written into the SONG'S OWN FOLDER, and
    /// its name is recorded in that folder's <see cref="SongSidecar"/> (<c>sdo.header</c>). Every later run just reads
    /// the sidecar during the scan and loads the file — see <see cref="ExternalSongScanner"/>.
    ///
    /// It runs on SELECTION rather than during the boot scan because composing means fully decoding the cover (song
    /// folders routinely carry a 1920×1080 background): a 1500-song library would spend minutes at the progress bar
    /// building discs the player never looks at. One song costs a few tens of ms, once.
    ///
    /// Anything unexpected (unreadable cover, read-only folder) falls back to the raw cover image, i.e. exactly what
    /// the disc showed before this existed.
    /// </summary>
    public static class ExternalCdImage
    {
        // Per-song sprite cache, keyed by the entry's gn. A null IS cached: a song whose cover can't be composed must
        // not retry the decode every time the selection moves back onto it.
        private static readonly Dictionary<string, Sprite> _cache = new Dictionary<string, Sprite>();

        /// <summary>The disc sprite for an external song: the recorded CD image, else one composed (and recorded) now,
        /// else the raw cover, else null (the caller shows the NONE disc).</summary>
        public static Sprite Get(SongCatalog.Entry e)
        {
            if (e == null || !e.external) return null;
            string key = string.IsNullOrEmpty(e.gn) ? e.imagePath : e.gn;
            if (string.IsNullOrEmpty(key)) return null;
            if (_cache.TryGetValue(key, out var cached)) return cached;

            Sprite sprite = LoadSprite(e.cdPath);                 // recorded by an earlier run
            if (sprite == null && !string.IsNullOrEmpty(e.imagePath))
            {
                e.cdPath = Compose(e);                            // first selection of this song → build it
                sprite = LoadSprite(e.cdPath) ?? LoadSprite(e.imagePath);   // last resort: the raw cover, as before
            }

            _cache[key] = sprite;
            return sprite;
        }

        /// <summary>Drop the sprite cache (a re-scan may have replaced the discs on disk).</summary>
        public static void Clear() => _cache.Clear();

        private static Sprite LoadSprite(string absPath)
        {
            if (string.IsNullOrEmpty(absPath)) return null;
            try
            {
                if (!File.Exists(absPath)) return null;
                if (absPath.EndsWith(".dds", System.StringComparison.OrdinalIgnoreCase)) return LoadDdsSprite(absPath);
                return SdoExtracted.LoadImage(Path.GetDirectoryName(absPath), Path.GetFileName(absPath));
            }
            catch { return null; }
        }

        /// <summary>
        /// A .dds disc, straight from an SDO pack's UI/MUSIC/ICONS. The shared image loader only knows PNG/JPG/BMP, and
        /// <see cref="DdsLoader"/> can't be reused here: every one of its decoders ends in
        /// <c>Apply(false, makeNoLongerReadable: true)</c>, so the pixels can't be read back — and they MUST be, because
        /// DDS stores the TOP row first while a Unity texture's row 0 is the BOTTOM. A mesh cancels that out with its
        /// D3D-era UVs; a full-rect sprite has nothing to cancel it with, so the disc would come out upside down.
        ///
        /// Only the 32-bit UNCOMPRESSED layout is decoded — which is what SDO song icons actually are (all 134 .dds in
        /// the [NX] pack are A8R8G8B8 237×237). Anything else returns null: run tools/nx/nx_to_gn.py over the pack and
        /// it converts every icon to PNG up front (smaller, and no decoding at all at runtime).
        /// </summary>
        private static Sprite LoadDdsSprite(string absPath)
        {
            var d = File.ReadAllBytes(absPath);
            if (d.Length < 128 || d[0] != 'D' || d[1] != 'D' || d[2] != 'S' || d[3] != ' ') return null;
            int h = System.BitConverter.ToInt32(d, 12), w = System.BitConverter.ToInt32(d, 16);
            uint pf = System.BitConverter.ToUInt32(d, 80);
            int bits = System.BitConverter.ToInt32(d, 88);
            if (w <= 0 || h <= 0 || w > 4096 || h > 4096) return null;
            if ((pf & 0x4u) != 0 || (pf & 0x40u) == 0 || bits != 32 || 128 + w * h * 4 > d.Length)
            {
                SdoLog.MissingAsset("cd-dds", absPath, "compressed/unsupported DDS — run tools/nx/nx_to_gn.py");
                return null;
            }
            uint rm = System.BitConverter.ToUInt32(d, 92), gm = System.BitConverter.ToUInt32(d, 96);
            uint bm = System.BitConverter.ToUInt32(d, 100), am = System.BitConverter.ToUInt32(d, 104);
            int rs = MaskShift(rm), gs = MaskShift(gm), bs = MaskShift(bm), as_ = MaskShift(am);

            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                int src = 128 + y * w * 4;
                int dst = (h - 1 - y) * w;          // DDS row 0 = top → Unity row 0 = bottom
                for (int x = 0; x < w; x++, src += 4)
                {
                    uint v = (uint)(d[src] | (d[src + 1] << 8) | (d[src + 2] << 16) | (d[src + 3] << 24));
                    px[dst + x] = new Color32((byte)((v & rm) >> rs), (byte)((v & gm) >> gs), (byte)((v & bm) >> bs),
                                              am != 0 ? (byte)((v & am) >> as_) : (byte)255);
                }
            }
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            tex.SetPixels32(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 1f, 0, SpriteMeshType.FullRect);
        }

        private static int MaskShift(uint mask)
        {
            if (mask == 0) return 0;
            int s = 0;
            while ((mask & 1) == 0) { mask >>= 1; s++; }
            return s;
        }

        // Compose the disc from the song's cover art and persist it (PNG + sidecar entry). Returns the disc's absolute
        // path, or "" when it couldn't be built/written — the caller then keeps using the raw cover.
        private static string Compose(SongCatalog.Entry e)
        {
            if (string.IsNullOrEmpty(e.folderPath)) return "";

            Texture2D src = null;
            Texture2D disc = null;
            bool ownSrc = false;
            try
            {
                src = Decode(e.imagePath, out ownSrc);
                if (src == null) { SdoLog.MissingAsset("cd-cover", e.imagePath, "undecodable"); return ""; }

                var rgba = ToRgba(src);
                var composed = CdImageComposer.Compose(rgba, src.width, src.height);
                if (composed == null) return "";

                int size = CdImageComposer.DefaultSize;
                disc = new Texture2D(size, size, TextureFormat.RGBA32, false);
                disc.LoadRawTextureData(composed);   // same bottom-up row order the pixels came in as
                disc.Apply();
                var png = disc.EncodeToPNG();
                if (png == null || png.Length == 0) return "";

                string file = SongSidecar.CdFileName(e.songKey);
                string path = Path.Combine(e.folderPath, file);
                File.WriteAllBytes(path, png);
                WriteSidecar(e.folderPath, e.songKey, file);
                return path;
            }
            catch (System.Exception ex)
            {
                SdoLog.Note("cd", "compose failed for " + e.imagePath + ": " + ex.Message);
                return "";
            }
            finally
            {
                if (ownSrc) Destroy(src);   // the .bmp path hands back a shared-cache texture — not ours to free
                Destroy(disc);
            }
        }

        // Record the disc's filename for THIS song, leaving the other songs of a multi-song folder (and any #MOT /
        // #CAMERA lines already written) untouched.
        private static void WriteSidecar(string folder, string songKey, string cdFile)
        {
            string path = Path.Combine(folder, SongSidecar.FileName);
            string text = "";
            try { if (File.Exists(path)) text = File.ReadAllText(path); }
            catch { text = ""; }
            File.WriteAllText(path, SongSidecar.SetCdImage(text, songKey, cdFile));
        }

        // Decode a cover to a readable RGBA texture. Ours is uncached and destroyed right after use: covers are
        // full-size backgrounds (8 MB as RGBA32) and browsing a library would otherwise pin one per song visited.
        // <paramref name="owned"/> is false for the shared-cache texture the .bmp path returns.
        private static Texture2D Decode(string absPath, out bool owned)
        {
            owned = false;
            byte[] bytes;
            try { if (!File.Exists(absPath)) return null; bytes = File.ReadAllBytes(absPath); }
            catch { return null; }

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (tex.LoadImage(bytes)) { owned = true; return tex; }   // PNG / JPG
            Destroy(tex);

            // Anything Unity won't decode (a .bmp cover) goes through the shared loader, which knows those formats.
            var sprite = SdoExtracted.LoadImage(Path.GetDirectoryName(absPath), Path.GetFileName(absPath));
            return sprite != null ? sprite.texture : null;
        }

        private static byte[] ToRgba(Texture2D tex)
        {
            var px = tex.GetPixels32();
            var rgba = new byte[px.Length * 4];
            for (int i = 0; i < px.Length; i++)
            {
                int o = i * 4;
                rgba[o] = px[i].r; rgba[o + 1] = px[i].g; rgba[o + 2] = px[i].b; rgba[o + 3] = px[i].a;
            }
            return rgba;
        }

        private static void Destroy(Texture2D tex)
        {
            if (tex == null) return;
            if (Application.isPlaying) Object.Destroy(tex);
            else Object.DestroyImmediate(tex);
        }
    }
}
