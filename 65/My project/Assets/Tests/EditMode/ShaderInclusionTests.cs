using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace Sdo.Tests
{
    /// <summary>
    /// Regression guard for the build-time shader-stripping trap: any project shader (Sdo/*) that the game resolves
    /// ONLY at runtime via <c>Shader.Find</c> — never referenced by a material baked into a built scene/asset — is
    /// stripped from the standalone player. <c>Shader.Find</c> then returns null and the caller silently falls back to
    /// a different (usually opaque) shader, so the effect looks correct in the Editor (shaders aren't stripped there)
    /// but breaks in the packaged build. That is exactly what happened to <c>Sdo/UnlitAvatarAlpha</c>: missing from
    /// BuildScript.RequiredShaders → 翅膀(CHIBANG)/眼鏡 fell back to the opaque-cutout UnlitDoubleSided → 翅膀周圍閃爍
    /// 貼圖「透明度沒做出來」in the build only.
    ///
    /// This test scans the game source for every <c>Shader.Find("Sdo/…")</c> literal and asserts each one is listed in
    /// BuildScript's RequiredShaders array (the "Always Included Shaders" seed). Pure text scan, so it needs no
    /// reference to the Editor assembly.
    /// </summary>
    public class ShaderInclusionTests
    {
        private static readonly Regex FindLiteral = new Regex("Shader\\.Find\\(\\s*\"(Sdo/[A-Za-z0-9_]+)\"", RegexOptions.Compiled);
        private static readonly Regex SdoLiteral = new Regex("\"(Sdo/[A-Za-z0-9_]+)\"", RegexOptions.Compiled);

        // Application.dataPath = <project>/Assets in an EditMode run.
        private static string Assets => Application.dataPath;

        [Test]
        public void EveryRuntimeResolvedSdoShaderIsAlwaysIncludedInTheBuild()
        {
            string buildScript = Path.Combine(Assets, "Editor", "BuildScript.cs");
            Assert.IsTrue(File.Exists(buildScript), "BuildScript.cs not found at " + buildScript);

            // The only "Sdo/…" quoted literals in BuildScript.cs are the RequiredShaders entries (comments use bare
            // names, not quoted Sdo/ literals), so this collects the always-included set.
            var required = new HashSet<string>();
            foreach (Match m in SdoLiteral.Matches(File.ReadAllText(buildScript)))
                required.Add(m.Groups[1].Value);
            Assert.Greater(required.Count, 0, "No Sdo/* shaders parsed from RequiredShaders — parser or file layout changed.");

            string scripts = Path.Combine(Assets, "Scripts");
            Assert.IsTrue(Directory.Exists(scripts), "Scripts folder not found at " + scripts);

            var missing = new SortedDictionary<string, string>();   // shader -> first file that references it
            foreach (var file in Directory.GetFiles(scripts, "*.cs", SearchOption.AllDirectories))
            {
                foreach (Match m in FindLiteral.Matches(File.ReadAllText(file)))
                {
                    string shader = m.Groups[1].Value;
                    if (!required.Contains(shader) && !missing.ContainsKey(shader))
                        missing[shader] = file.Substring(Assets.Length).Replace('\\', '/');
                }
            }

            if (missing.Count > 0)
            {
                var lines = new List<string>();
                foreach (var kv in missing) lines.Add($"  \"{kv.Key}\"  (referenced in Assets{kv.Value})");
                Assert.Fail("These Sdo/* shaders are resolved via Shader.Find() but are NOT in BuildScript.RequiredShaders, "
                    + "so they will be stripped from the player build and fall back to the wrong shader at runtime. "
                    + "Add each to RequiredShaders:\n" + string.Join("\n", lines));
            }
        }
    }
}
