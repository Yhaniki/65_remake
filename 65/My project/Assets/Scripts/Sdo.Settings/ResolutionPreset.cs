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

    /// <summary>The window-size presets offered in Settings, plus validation/clamping. Pure logic.</summary>
    public static class ResolutionPreset
    {
        public static readonly ScreenSize[] Presets =
        {
            new ScreenSize(800, 600),
            new ScreenSize(1024, 768),
            new ScreenSize(1280, 720),
            new ScreenSize(1280, 800),
            new ScreenSize(1366, 768),
            new ScreenSize(1600, 900),
            new ScreenSize(1920, 1080),
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
            ScreenSize best = Presets[2]; // 1280x720 default
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
