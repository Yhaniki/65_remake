using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Sdo.Game
{
    /// <summary>
    /// The four cloth groups a model's physics bones fall into. Kept here (not in <see cref="MmdMagicaCloth"/>) so the
    /// profile file and the converter agree on the section names.
    /// </summary>
    public enum MmdClothPartId { Bang = 0, Hair = 1, Skirt = 2, Tie = 3 }

    /// <summary>How a cloth part's particles are connected — normally derived from the model (box-shaped rigid bodies
    /// ⇒ a panel sheet, e.g. a skirt; otherwise independent strands).</summary>
    public enum MmdClothConnection { Auto = 0, Line = 1, Mesh = 2 }

    /// <summary>
    /// The physics knobs for ONE cloth part, as they end up on the Magica Cloth component. <see cref="MmdMagicaCloth"/>
    /// fills this in by CONVERTING the model's own rigid bodies/joints; a <c>physics.ini</c> in the model folder then
    /// overrides whatever it names (see <see cref="MmdClothProfile"/>).
    ///
    /// Everything here is SCALE-INDEPENDENT on purpose. Gravity, particle radius and the skirt leash are real distances
    /// that depend on how tall the model was scaled to match the SDO dancer (the same model is built at a different
    /// scale for a different dancer), so the file stores MULTIPLIERS on the converted value, never the world-space
    /// number — a profile written for one dancer stays correct on the next.
    /// </summary>
    public struct MmdClothPart
    {
        public float GravityMul;              // × the converted gravity (9.8 m/s² at this model's scale)
        public float GravityFalloff;          // 0 = hangs straight down · 1 = holds its rest shape against gravity
        public bool UseAngleRestoration;      // the shape-holding force
        public float AngleStiffness;          // 0..1 restoration strength at the chain ROOT
        public float AngleStiffTipScale;      // × stiffness at the TIP (root-weighted chains: <1 = free-er tip = whip)
        public bool RootWeighted;             // apply that root→tip curve at all
        public float AngleLimitDeg;           // hard guard: how far a joint may bend from its authored pose
        public float AngleLimitStiffness;     // how hard that guard pulls back (Bullet ERP analog ≈0.2 = soft)
        public float DampingRoot, DampingTip; // air resistance along the chain (per Magica substep)
        public float RadiusMul;               // × the converted particle thickness
        public float RadiusRootScale;         // × radius near the body (thin, so it can't puff over the hip collider)
        public float WorldInertia;            // how much of the body's motion the cloth inherits (1 = all, like MMD)
        public float DepthInertia;            // root rides with the body, tip lags behind (the whip)
        public float InertiaSmoothing;        // low-pass on that motion transfer
        public MmdClothConnection Connection; // Auto = decide from the model (box bodies ⇒ Mesh)
        public float MaxDistanceMul;          // skirt leash: how far the hem may stray from its animated drape (× chain length)
        public float MaxDistanceRootScale;    // that leash near the waist
        public float Friction;                // cloth↔body friction
        public float ParticleSpeedLimitMps;   // anti-explosion cap, in METERS/s (scaled at build)
    }

    /// <summary>
    /// A saved physics tuning for one MMD model: <c>physics.ini</c> sitting in the model's own folder next to the .pmx.
    ///
    /// <b>Have the file → it wins. No file → convert from the PMX as before.</b> An override is per-KEY, not per-file:
    /// <see cref="MmdMagicaCloth"/> always converts the model's rigid bodies/joints first, then this overwrites only the
    /// keys the file actually lists. So a two-line file that just softens the skirt keeps every other converted value —
    /// and a model you never tuned behaves exactly as it did before this file existed.
    ///
    /// Format is the project's usual ini (like config.ini): a <c>[global]</c> section plus one section per cloth part
    /// (<c>[bang] [hair] [skirt] [tie]</c>), <c>key = value</c>, <c>#</c> or <c>;</c> comments. Unknown keys and unknown
    /// sections are ignored (so a newer file can't break an older build). Numbers are parsed culture-invariantly.
    ///
    /// <see cref="Save"/> writes back what the game is CURRENTLY running (converted values + whatever the debug panel's
    /// gravity/stiffness/collider knobs were dialled to), with a comment on every line — that is the "調到滿意就存檔"
    /// button. Hand-written comments are not preserved across a save.
    /// </summary>
    public sealed class MmdClothProfile
    {
        /// <summary>The file the game looks for inside a model's folder.</summary>
        public const string FileName = "physics.ini";

        // section (lower-case) → key (lower-case) → raw value, exactly as the file listed them
        private readonly Dictionary<string, Dictionary<string, string>> _sections =
            new Dictionary<string, Dictionary<string, string>>();

        /// <summary>Where this profile was read from (null when it is the built-in "nothing overridden" one).</summary>
        public string Path { get; private set; }

        /// <summary>True when the model folder actually had a physics.ini (i.e. this profile overrides something).</summary>
        public bool FromFile => Path != null;

        public static string PathFor(string modelDir) => System.IO.Path.Combine(modelDir ?? "", FileName);

        /// <summary>Load the profile from <paramref name="modelDir"/>, or null when the model has no physics.ini (→ the
        /// caller just uses the converted values). Unreadable/garbled files are treated as "no profile" — a broken tuning
        /// file must never stop the model from being displayed.</summary>
        public static MmdClothProfile Load(string modelDir)
        {
            string path = PathFor(modelDir);
            try
            {
                if (string.IsNullOrEmpty(modelDir) || !File.Exists(path)) return null;
                var p = Parse(File.ReadAllText(path));
                p.Path = path;
                return p;
            }
            catch { return null; }
        }

        /// <summary>Parse ini text (the unit-testable core; no filesystem).</summary>
        public static MmdClothProfile Parse(string text)
        {
            var p = new MmdClothProfile();
            if (string.IsNullOrEmpty(text)) return p;
            string section = "global";
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                if (line[0] == '[')
                {
                    int end = line.IndexOf(']');
                    if (end > 1) section = line.Substring(1, end - 1).Trim().ToLowerInvariant();
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string val = line.Substring(eq + 1).Trim();
                int hash = val.IndexOf('#');   // allow a trailing comment on a value line
                if (hash >= 0) val = val.Substring(0, hash).Trim();
                if (key.Length == 0) continue;
                if (!p._sections.TryGetValue(section, out var kv)) p._sections[section] = kv = new Dictionary<string, string>();
                kv[key] = val;
            }
            return p;
        }

        // ---- typed reads (return the fallback when the file doesn't mention the key) ----

        private string Raw(string section, string key)
        {
            if (_sections.TryGetValue(section, out var kv) && kv.TryGetValue(key, out var v) && v.Length > 0) return v;
            return null;
        }

        public float Get(string section, string key, float fallback)
        {
            string v = Raw(section, key);
            return v != null && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : fallback;
        }

        public bool Get(string section, string key, bool fallback)
        {
            string v = Raw(section, key);
            if (v == null) return fallback;
            v = v.ToLowerInvariant();
            if (v == "1" || v == "true" || v == "on" || v == "yes") return true;
            if (v == "0" || v == "false" || v == "off" || v == "no") return false;
            return fallback;
        }

        /// <summary>Does the file have anything to say about this part? (Used only for the "which parts are overridden"
        /// line in the debug panel / log.)</summary>
        public bool Has(MmdClothPartId part) => _sections.ContainsKey(SectionOf(part));

        public static string SectionOf(MmdClothPartId part) => part.ToString().ToLowerInvariant();

        /// <summary>The global knobs (fallbacks are the converter's own defaults).</summary>
        public float SimulationFrequency(float fallback) => Get("global", "simulationfrequency", fallback);
        public float ColliderRadiusMul(float fallback) => Get("global", "colliderradiusmul", fallback);

        /// <summary>Overwrite in <paramref name="p"/> ONLY the keys this file lists — everything else keeps the value the
        /// converter derived from the model. This is what makes "no file = pure conversion" and "two-line file = tweak
        /// one thing" both work.</summary>
        public MmdClothPart Apply(MmdClothPartId part, MmdClothPart p)
        {
            string s = SectionOf(part);
            p.GravityMul            = Get(s, "gravitymul", p.GravityMul);
            p.GravityFalloff        = Get(s, "gravityfalloff", p.GravityFalloff);
            p.UseAngleRestoration   = Get(s, "useanglerestoration", p.UseAngleRestoration);
            p.AngleStiffness        = Get(s, "anglestiffness", p.AngleStiffness);
            p.AngleStiffTipScale    = Get(s, "anglestifftipscale", p.AngleStiffTipScale);
            p.RootWeighted          = Get(s, "rootweighted", p.RootWeighted);
            p.AngleLimitDeg         = Get(s, "anglelimitdeg", p.AngleLimitDeg);
            p.AngleLimitStiffness   = Get(s, "anglelimitstiffness", p.AngleLimitStiffness);
            p.DampingRoot           = Get(s, "dampingroot", p.DampingRoot);
            p.DampingTip            = Get(s, "dampingtip", p.DampingTip);
            p.RadiusMul             = Get(s, "radiusmul", p.RadiusMul);
            p.RadiusRootScale       = Get(s, "radiusrootscale", p.RadiusRootScale);
            p.WorldInertia          = Get(s, "worldinertia", p.WorldInertia);
            p.DepthInertia          = Get(s, "depthinertia", p.DepthInertia);
            p.InertiaSmoothing      = Get(s, "inertiasmoothing", p.InertiaSmoothing);
            p.MaxDistanceMul        = Get(s, "maxdistancemul", p.MaxDistanceMul);
            p.MaxDistanceRootScale  = Get(s, "maxdistancerootscale", p.MaxDistanceRootScale);
            p.Friction              = Get(s, "friction", p.Friction);
            p.ParticleSpeedLimitMps = Get(s, "particlespeedlimitmps", p.ParticleSpeedLimitMps);

            string conn = Raw(s, "connection");
            if (conn != null)
            {
                switch (conn.ToLowerInvariant())
                {
                    case "line": p.Connection = MmdClothConnection.Line; break;
                    case "mesh": p.Connection = MmdClothConnection.Mesh; break;
                    case "auto": p.Connection = MmdClothConnection.Auto; break;
                }
            }
            return p;
        }

        // ---- writing ----

        /// <summary>Serialize a full tuning (the ini text <see cref="Save"/> writes). Every value the game is running is
        /// written out — including the ones that were converted rather than tuned — so the file is a complete, editable
        /// snapshot rather than a diff you can't read.</summary>
        public static string Serialize(float simulationFrequency, float colliderRadiusMul,
                                       IEnumerable<KeyValuePair<MmdClothPartId, MmdClothPart>> parts)
        {
            var sb = new StringBuilder(2048);
            sb.Append("# MMD 物理設定 (").Append(FileName).Append(")\n");
            sb.Append("# 這個檔在,遊戲就照它跑;刪掉它,就回去直接從 .pmx 的剛體/關節轉換。\n");
            sb.Append("# 只寫你想改的那幾行也可以 —— 沒列到的 key 一律用轉換出來的值。\n");
            sb.Append("# 所有數值都與模型縮放無關(用「倍率」而非世界座標長度),換一個身高的舞者也還原得出同樣手感。\n\n");

            sb.Append("[global]\n");
            Line(sb, "simulationFrequency", simulationFrequency, "解算頻率 Hz — 越高越不會被快速的腿穿過去");
            Line(sb, "colliderRadiusMul", colliderRadiusMul, "身體碰撞體半徑倍率(面板的 碰-/碰+)");
            sb.Append('\n');

            foreach (var kv in parts)
            {
                var p = kv.Value;
                sb.Append('[').Append(SectionOf(kv.Key)).Append("]\n");
                Line(sb, "gravityMul", p.GravityMul, "重力倍率(面板的 重-/重+)");
                Line(sb, "gravityFalloff", p.GravityFalloff, "0=自然下垂 1=靠回原姿勢(瀏海這種被釘住的接近 1)");
                Line(sb, "useAngleRestoration", p.UseAngleRestoration, "保持形狀的回復力");
                Line(sb, "angleStiffness", p.AngleStiffness, "回復力強度 0..1(面板的 硬-/硬+ 乘在這上面)");
                Line(sb, "angleStiffTipScale", p.AngleStiffTipScale, "髮尾相對髮根的強度(<1 髮尾較自由=甩得開)");
                Line(sb, "rootWeighted", p.RootWeighted, "是否套用上面那條 根→尾 曲線");
                Line(sb, "angleLimitDeg", p.AngleLimitDeg, "硬性角度上限(只擋極端甩動)");
                Line(sb, "angleLimitStiffness", p.AngleLimitStiffness, "上限拉回的硬度(0.2≈Bullet 的軟回彈;1=硬鎖)");
                Line(sb, "dampingRoot", p.DampingRoot, "空氣阻力(髮根)");
                Line(sb, "dampingTip", p.DampingTip, "空氣阻力(髮尾)");
                Line(sb, "radiusMul", p.RadiusMul, "粒子粗細倍率");
                Line(sb, "radiusRootScale", p.RadiusRootScale, "貼近身體那端的粗細比例");
                Line(sb, "worldInertia", p.WorldInertia, "身體動作帶動布料的比例(1=全部,MMD 就是 1)");
                Line(sb, "depthInertia", p.DepthInertia, "髮根跟著身體、髮尾落後(甩尾)");
                Line(sb, "inertiaSmoothing", p.InertiaSmoothing, "動作傳遞的平滑(太大轉身會慢半拍)");
                Line(sb, "connection", p.Connection == MmdClothConnection.Mesh ? "mesh" : p.Connection == MmdClothConnection.Line ? "line" : "auto",
                     "line=各自獨立的髮束 mesh=一片裙子 auto=看模型剛體形狀決定");
                Line(sb, "maxDistanceMul", p.MaxDistanceMul, "裙擺可以離開原本垂墜位置多遠(×裙長);太小會完全不動");
                Line(sb, "maxDistanceRootScale", p.MaxDistanceRootScale, "同上,腰部那端");
                Line(sb, "friction", p.Friction, "布料與身體的摩擦");
                Line(sb, "particleSpeedLimitMps", p.ParticleSpeedLimitMps, "速度上限(公尺/秒),防爆用");
                sb.Append('\n');
            }
            return sb.ToString();
        }

        private static void Line(StringBuilder sb, string key, float v, string comment)
            => sb.Append(key).Append(" = ").Append(v.ToString("0.####", CultureInfo.InvariantCulture)).Append("        # ").Append(comment).Append('\n');

        private static void Line(StringBuilder sb, string key, bool v, string comment)
            => sb.Append(key).Append(" = ").Append(v ? "true" : "false").Append("        # ").Append(comment).Append('\n');

        private static void Line(StringBuilder sb, string key, string v, string comment)
            => sb.Append(key).Append(" = ").Append(v).Append("        # ").Append(comment).Append('\n');

        /// <summary>Write the tuning into the model's folder. Returns the path, or null if it couldn't be written
        /// (read-only install dir…) — the caller reports that; nothing else depends on it.</summary>
        public static string Save(string modelDir, float simulationFrequency, float colliderRadiusMul,
                                  IEnumerable<KeyValuePair<MmdClothPartId, MmdClothPart>> parts)
        {
            string path = PathFor(modelDir);
            try
            {
                File.WriteAllText(path, Serialize(simulationFrequency, colliderRadiusMul, parts), new UTF8Encoding(false));
                return path;
            }
            catch { return null; }
        }

        /// <summary>Delete the model's physics.ini → the next build converts from the .pmx again. True if a file was
        /// actually removed.</summary>
        public static bool Delete(string modelDir)
        {
            try
            {
                string path = PathFor(modelDir);
                if (!File.Exists(path)) return false;
                File.Delete(path);
                return true;
            }
            catch { return false; }
        }
    }
}
