using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Sdo.Game;
using Sdo.UI.Services;

namespace Sdo.UI.Util
{
    public static class RoomExpressionArt
    {
        private static string _dir;
        private static readonly Dictionary<int, Sprite[]> _smallFrames = new Dictionary<int, Sprite[]>();
        private static readonly Dictionary<int, Sprite[]> _largeFrames = new Dictionary<int, Sprite[]>();

        public static string Dir
        {
            get { return _dir ?? (_dir = ResolveDir()); }
            set { _dir = value; _smallFrames.Clear(); _largeFrames.Clear(); }
        }

        public static Sprite[] SmallFrames(int expressionId)
            => Frames(expressionId, large: false);

        public static Sprite[] LargeFrames(int expressionId)
            => Frames(expressionId, large: true);

        private static Sprite[] Frames(int expressionId, bool large)
        {
            if (!RoomChatCommand.IsValidExpression(expressionId)) return new Sprite[0];
            var cache = large ? _largeFrames : _smallFrames;
            if (cache.TryGetValue(expressionId, out var frames) && frames != null) return frames;

            string prefix = large ? "L" : "S";
            frames = SdoExtracted.LoadAn(Dir, prefix + "_Expression" + expressionId, bleed: true);
            if (frames.Length == 0)
                frames = SdoExtracted.LoadAn(Dir, prefix + "_EXPRESSION" + expressionId, bleed: true);

            cache[expressionId] = frames;
            return frames;
        }

        private static string ResolveDir()
        {
            try
            {
                var ordered = new List<string>();
                var assets = Path.GetDirectoryName(Path.GetDirectoryName(SdoExtracted.Root));
                if (assets != null && Directory.Exists(assets))
                    foreach (var d in Directory.GetDirectories(assets))
                        ordered.Add(Path.Combine(d, "DatasSDO", "UI", "EXPRESSIONS"));
                ordered.Add(Path.Combine(SdoExtracted.Root, "UI", "EXPRESSIONS"));
                return RoomDlgArt.PickDir(ordered, Directory.Exists);
            }
            catch
            {
                return Path.Combine(SdoExtracted.Root, "UI", "EXPRESSIONS");
            }
        }
    }
}
