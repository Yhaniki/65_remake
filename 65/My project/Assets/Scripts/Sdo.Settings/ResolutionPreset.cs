namespace Sdo.Settings
{
    public readonly struct ScreenSize
    {
        public readonly int Width;
        public readonly int Height;
        public ScreenSize(int w, int h) { Width = w; Height = h; }
        public override string ToString() => $"{Width} × {Height}";
        public long Area => (long)Width * Height;
    }

    /// <summary>The window-size presets offered in Settings, plus validation/clamping. Pure logic.
    /// ALL entries are 4:3 to match the fixed 800×600 game frame: these presets only take effect in **Windowed**
    /// mode (fullscreen/borderless use the native desktop resolution — see <see cref="DisplaySettingsManager.ApplyDisplay"/>),
    /// and a 4:3 window fits the content exactly with no pillarbox bars or stretch distortion.</summary>
    public static class ResolutionPreset
    {
        public static readonly ScreenSize[] Presets =
        {
            new ScreenSize(800, 600),     // SVGA  (native design frame)
            new ScreenSize(1024, 768),    // XGA   (default windowed size)
            new ScreenSize(1152, 864),    // XGA+
            new ScreenSize(1280, 960),    // SXGA−
            new ScreenSize(1400, 1050),   // SXGA+
            new ScreenSize(1600, 1200),   // UXGA
        };

        public const int MinDim = 640;
        public const int MaxDim = 7680;

        public static bool IsValid(int w, int h)
            => w >= MinDim && h >= MinDim && w <= MaxDim && h <= MaxDim;

        /// <summary>Return (w,h) if valid; otherwise snap to the nearest preset by area distance.</summary>
        public static ScreenSize Clamp(int w, int h)
        {
            if (IsValid(w, h)) return new ScreenSize(w, h);
            long target = (long)w * h;
            ScreenSize best = Presets[1]; // 1024×768 seed (always overwritten by the nearest-area preset below)
            long bestDist = long.MaxValue;
            foreach (var p in Presets)
            {
                long dist = System.Math.Abs(p.Area - target);
                if (dist < bestDist) { bestDist = dist; best = p; }
            }
            return best;
        }

        /// <summary>Index of the preset matching (w,h) exactly, or -1 (custom).</summary>
        public static int IndexOf(int w, int h)
        {
            for (int i = 0; i < Presets.Length; i++)
                if (Presets[i].Width == w && Presets[i].Height == h) return i;
            return -1;
        }
    }
}
