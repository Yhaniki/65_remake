using UnityEngine;

namespace Sdo.UI.Util
{
    /// <summary>Shared colors for the front-end (SDO-ish neon purple/pink). Placeholder palette for v1.</summary>
    public static class UITheme
    {
        public static readonly Color Bg = new Color(0.09f, 0.06f, 0.13f, 1f);
        public static readonly Color Panel = new Color(0.16f, 0.11f, 0.22f, 0.96f);
        public static readonly Color PanelAlt = new Color(0.22f, 0.15f, 0.30f, 1f);
        public static readonly Color Header = new Color(0.27f, 0.13f, 0.34f, 1f);

        public static readonly Color Primary = new Color(0.93f, 0.27f, 0.55f, 1f);
        public static readonly Color Secondary = new Color(0.32f, 0.27f, 0.42f, 1f);
        public static readonly Color Danger = new Color(0.80f, 0.28f, 0.30f, 1f);
        public static readonly Color Disabled = new Color(0.35f, 0.33f, 0.40f, 1f);
        public static readonly Color Accent = new Color(0.40f, 0.72f, 1f, 1f);

        public static readonly Color Text = new Color(0.96f, 0.95f, 0.98f, 1f);
        public static readonly Color TextDim = new Color(0.72f, 0.68f, 0.80f, 1f);
        public static readonly Color OnPrimary = Color.white;

        public static readonly Color Row = new Color(1f, 1f, 1f, 0.04f);
        public static readonly Color RowAlt = new Color(1f, 1f, 1f, 0.08f);
        public static readonly Color RowSelected = new Color(0.93f, 0.27f, 0.55f, 0.35f);

        public static readonly Color Ready = new Color(0.36f, 0.85f, 0.46f, 1f);
        public static readonly Color Warn = new Color(0.96f, 0.76f, 0.26f, 1f);
        public static readonly Color HostGold = new Color(1f, 0.82f, 0.30f, 1f);
    }
}
