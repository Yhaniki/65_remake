using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Sdo.Game;

namespace Sdo.UI.Util
{
    public static class RoomBubbleArt
    {
        public const float CanvasW = 171f;
        public const float CanvasH = 111f;

        // Real alpha bounds measured from the official TALK_1..TALK_11 PNGs (canvas 171×111). NOTE: styles 1..8 share
        // top=37/bottom=76 (只變寬), and ALL 11 styles share body CENTER-Y = 56.5 (9..11 grow symmetrically) and
        // center-x = 86 → the sprites are authored in a common canvas, so bubbles must be placed by a FIXED canvas
        // point (see RoomScreen.BubbleRootFromVisible), not per-sprite alpha bounds (which jump when the sprite swaps).
        private static readonly Rect[] TalkBounds =
        {
            new Rect(56, 37, 60, 39),
            new Rect(51, 37, 70, 39),
            new Rect(45, 37, 82, 39),
            new Rect(39, 37, 94, 39),
            new Rect(34, 37, 104, 39),
            new Rect(28, 37, 116, 39),
            new Rect(22, 37, 128, 39),
            new Rect(17, 37, 138, 39),
            new Rect(10, 32, 152, 49),
            new Rect(6, 26, 160, 61),
            new Rect(3, 21, 166, 71),
        };

        // ADDANI rest frame (13) alpha bounds; body top=37 same as TALK, extends to 82.
        private static readonly Rect AddAniBounds = new Rect(56, 37, 60, 45);

        /// <summary>所有 bubble sprite 共用畫布(171×111)的固定錨點：x=畫布中央、y=泡身垂直中心(TALK 各 style 都是 56.5)。
        /// 用它對齊(而非各 sprite 的 alpha bounds)才不會在打字圖/文字圖/不同 style 間跳位。</summary>
        public const float AnchorCanvasX = 85.5f;
        public const float AnchorCanvasY = 56.5f;

        private static string _dir;
        private static readonly Dictionary<string, Sprite[]> _framesCache = new Dictionary<string, Sprite[]>();
        private static readonly Dictionary<string, Sprite> _spriteCache = new Dictionary<string, Sprite>();

        public static string Dir
        {
            get { return _dir ?? (_dir = ResolveDir()); }
            set { _dir = value; _framesCache.Clear(); _spriteCache.Clear(); }
        }

        public static Sprite Base(int style = 1)
            => First(Mathf.Clamp(style, 1, 11).ToString());

        /// <summary>有下方指示棍的靜態 talk 框（TALK_N/00），打字變寬用這個；根目錄 TALK_N.PNG 沒棍。</summary>
        public static Sprite Talk(int style = 1)
        {
            var frames = TalkFrames(style);
            return frames != null && frames.Length > 0 ? frames[0] : Base(style);
        }

        public static Sprite Typing()
        {
            var frames = AddFrames();
            if (frames.Length > 10) return frames[10];
            return frames.Length > 0 ? frames[frames.Length - 1] : null;
        }

        public static Sprite[] EnterFrames(int style = 1)
        {
            int n = Mathf.Clamp(style, 1, 11);
            return Frames("N" + n, "ENTER_" + n);
        }

        public static Sprite[] TalkFrames(int style = 1)
        {
            int n = Mathf.Clamp(style, 1, 11);
            return Frames("T" + n, "TALK_" + n);
        }

        public static Sprite[] AddFrames()
            => Frames("ADDANI");

        public static Rect BubbleBounds(int style)
            => TalkBounds[Mathf.Clamp(style, 1, 11) - 1];

        public static Rect TypingBounds()
            => AddAniBounds;

        public static Rect TextRect(int style)
        {
            var b = BubbleBounds(style);
            const float padX = 10f;
            const float padTop = 9f;
            const float padBottom = 8f;
            return new Rect(
                b.x + padX,
                b.y + padTop,
                Mathf.Max(8f, b.width - padX * 2f),
                Mathf.Max(12f, b.height - padTop - padBottom));
        }

        public static Rect TypingTextRect()
            => new Rect(56, 46, 60, 18);

        private static Sprite First(string anName)
        {
            if (_spriteCache.TryGetValue(anName, out var sprite) && sprite != null) return sprite;
            var frames = Frames(anName);
            sprite = frames.Length > 0 ? frames[0] : null;
            _spriteCache[anName] = sprite;
            return sprite;
        }

        private static Sprite[] Frames(string anName)
            => Frames(anName, null);

        private static Sprite[] Frames(string anName, string fallbackAnName)
        {
            if (string.IsNullOrEmpty(anName)) return new Sprite[0];
            string key = string.IsNullOrEmpty(fallbackAnName) ? anName : anName + "|" + fallbackAnName;
            if (_framesCache.TryGetValue(key, out var frames) && frames != null) return frames;
            frames = LoadFrames(anName);
            if ((frames == null || frames.Length == 0) && !string.IsNullOrEmpty(fallbackAnName))
                frames = LoadFrames(fallbackAnName);
            _framesCache[key] = frames;
            return frames;
        }

        private static Sprite[] LoadFrames(string name)
        {
            var frameDir = Path.Combine(Dir, name);
            if (Directory.Exists(frameDir))
            {
                var files = Directory.GetFiles(frameDir, "*.PNG");
                System.Array.Sort(files, System.StringComparer.OrdinalIgnoreCase);
                var sprites = new List<Sprite>(files.Length);
                foreach (var file in files)
                {
                    var s = SdoExtracted.LoadImage(frameDir, Path.GetFileName(file), bleed: true);
                    if (s != null) sprites.Add(s);
                }
                return sprites.ToArray();
            }
            return SdoExtracted.LoadAn(Dir, name, bleed: true);
        }

        private static string ResolveDir()
        {
            try
            {
                var ordered = new List<string>();
                var assets = Path.GetDirectoryName(Path.GetDirectoryName(SdoExtracted.Root));
                if (assets != null && Directory.Exists(assets))
                    foreach (var d in Directory.GetDirectories(assets))
                        ordered.Add(Path.Combine(d, "DatasSDO", "UI", "BUBBLE2"));
                ordered.Add(Path.Combine(SdoExtracted.Root, "UI", "BUBBLE2"));
                return RoomDlgArt.PickDir(ordered, Directory.Exists);
            }
            catch
            {
                return Path.Combine(SdoExtracted.Root, "UI", "BUBBLE2");
            }
        }
    }
}
