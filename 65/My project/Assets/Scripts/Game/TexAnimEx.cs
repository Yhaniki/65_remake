using System.Collections.Generic;

namespace Sdo.Game
{
    /// <summary>
    /// The model-embedded texture-animation convention ("_TexAnimEx"). Faithful port of TexAnimEx_parse
    /// (render/008_render_00418510.c): a mapobj submesh whose MSH material name is
    ///   _TexAnimEx(NAME)A_B[...].dds
    /// is NOT textured by that (placeholder) file. Instead the engine:
    ///   • reads "&lt;NAME&gt;.an" from the prop's own folder — a tiny CRLF text list of the real DDS frame files
    ///     (the SCN0016 city buildings each ship a 2-frame "&lt;name&gt;an.dds / &lt;name&gt;liang.dds" unlit/lit blink);
    ///   • uses A (the first integer after ')') as the frame interval in milliseconds.
    /// This is independent of <see cref="SceneMapobjTexAnimCatalog"/> (the scene-driven frame-swap): _TexAnimEx is
    /// driven entirely by the material name + .an file, so it works for any scene without a hand-authored entry.
    /// Pure parsing (no I/O) so it can be unit-tested; the caller does the file read + ResolveDds.
    /// </summary>
    public static class TexAnimEx
    {
        /// <summary>The decoded "_TexAnimEx(NAME)A_..." material name: which .an to read + the frame interval (ms).</summary>
        public struct Spec { public string Name; public float IntervalMs; }

        /// <summary>
        /// Parse a "_TexAnimEx(NAME)A_B...dds" material name. Returns true + the NAME and interval A (ms) on match;
        /// false for any ordinary material name. Case-insensitive on the "_texanimex" tag (the MSH stores it both
        /// "_texanimex(...)" and "_TEXANIMEX(...)").
        /// </summary>
        public static bool TryParse(string materialName, out Spec spec)
        {
            spec = default;
            if (string.IsNullOrEmpty(materialName)) return false;
            string lower = materialName.ToLowerInvariant();
            int tag = lower.IndexOf("_texanimex(");
            if (tag < 0) return false;
            int open = materialName.IndexOf('(', tag);
            int close = open >= 0 ? materialName.IndexOf(')', open + 1) : -1;
            if (open < 0 || close < 0 || close <= open + 1) return false;
            string name = materialName.Substring(open + 1, close - open - 1).Trim();
            if (name.Length == 0) return false;
            // first run of digits after ')' = interval A (ms)
            int i = close + 1; int start = i;
            while (i < materialName.Length && materialName[i] >= '0' && materialName[i] <= '9') i++;
            float interval = 0f;
            if (i > start && int.TryParse(materialName.Substring(start, i - start), out int a)) interval = a;
            spec = new Spec { Name = name, IntervalMs = interval };
            return true;
        }

        /// <summary>
        /// Parse the CRLF/whitespace-delimited contents of a "&lt;NAME&gt;.an" file into its ordered frame image names.
        /// Frames may be .dds (SCN0016 buildings) OR .tga (avatar 翅膀/wings — e.g. 花雨飞翼 023921 lists
        /// "023921_woman_chibang1_.tga\r\n..2_.tga\r\n..3_.tga"), also .bmp/.png. The caller resolves each name to a
        /// texture (extension-agnostic), so keep any recognised image extension here — dropping .tga was exactly why
        /// the wings fell back to the flat beige colour.
        /// </summary>
        public static string[] ParseAn(string anText)
        {
            var frames = new List<string>();
            if (string.IsNullOrEmpty(anText)) return frames.ToArray();
            foreach (var raw in anText.Split('\r', '\n', ' ', '\t'))
            {
                string t = raw.Trim();
                if (t.Length == 0) continue;
                string lo = t.ToLowerInvariant();
                if (lo.EndsWith(".dds") || lo.EndsWith(".tga") || lo.EndsWith(".bmp") || lo.EndsWith(".png"))
                    frames.Add(t);
            }
            return frames.ToArray();
        }
    }
}
