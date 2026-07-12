#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

// Headless Windows player build, invoked via:
//   Unity.exe -batchmode -nographics -quit -projectPath "<proj>" -executeMethod BuildScript.BuildWindows
// Output: <buildOut>/dance.exe  (default <repo>/Build/Windows, override with -buildOut <dir>).
// After a successful build the player icon is the project's AppIcon (from Icon3.ico) and the SDO game data is
// assembled into <buildOut>/DATA by tools/package_build.ps1, so the output runs without dev-machine paths.
public static class BuildScript
{
    private const string ExeName = "dance.exe";
    private const string IconAsset = "Assets/AppIcon.png";

    // Shaders the gameplay/effect/avatar/scene code resolves at RUNTIME via Shader.Find(). Because nothing in a built
    // scene references them, Unity strips them from the player → Shader.Find returns null → `new Material(null)` throws
    // (e.g. the 3D dancer + stage backdrop vanish). Force them into the build via Always Included Shaders.
    private static readonly string[] RequiredShaders =
    {
        // built-in
        "Sprites/Default",
        "Unlit/Texture",
        "Unlit/Color",
        "Unlit/Transparent",
        "Unlit/Transparent Cutout",
        "Legacy Shaders/Particles/Additive",
        "Particles/Standard Unlit",
        // project (Assets/**/*.shader)
        "Sdo/EftAlpha",          // kekkai disc + MW runes: 1× additive (no Legacy 2× clip for opaque-alpha textures)
        "Sdo/EftAdditiveLum",
        "Sdo/HpGlowClip",
        "Sdo/GlowClipRect",      // ShowTime energy-bar head glow (rect-clipped additive = official viewport crop)
        "Sdo/AdditiveRGB",       // ShowTime gauge RT composite (One-One add of the POWER EFT camera render)
        "Sdo/NoteCutout",        // 3D note highway cut-out sprites; stripped -> notes fall back to opaque (Note3dHighway/ScreenGameplay)
        "Sdo/UnlitSpotGlow",     // gameplay spotlight glow; stripped -> glow renders wrong (ScreenGameplay)
        "Sdo/PortraitOpaque",
        "Sdo/UnlitDoubleSided",
        "Sdo/UnlitAvatarAlpha",  // 眼鏡鏡片 + 翅膀(CHIBANG)/去背飾品 alpha-blend; stripped -> Shader.Find null -> 退回 UnlitDoubleSided(cutout,c.a=1
                                 // 強制不透明) -> 翅膀周圍閃爍貼圖/眼鏡鏡片「透明度沒做出來」(editor 不 strip 故只在打包版壞)
        "Sdo/UnlitInstanced",
        "Sdo/UnlitInstancedAlpha",   // alpha-blended mapobj props ("去背"); stripped -> alpha stage props go magenta
        "Sdo/UnlitInstancedAlphaCullBack", // mirrored separated alpha planes (SCN0011 DING): single-sided blend
        "Sdo/UnlitAdditiveOverlay",  // soft-alpha mapobj glow sprites (bulbs, lasers, sweep lights)
        "Sdo/UnlitOverlay",          // animated transparent overlays (FIFA crowd, texanim cut-outs)
        "Sdo/SceneVertexAlpha",      // soft-alpha SCENE.MSH materials (glass floors, feathered decals)
        "Sdo/SceneVertexCutout",     // base SCENE.MSH × baked vertex lighting (scene darkening/tint)
        "Sdo/UnlitInstancedCutout",  // solid/volumetric alpha mapobj props (SCN0006 carousel — no see-through)
    };

    public static void BuildWindows()
    {
        string outDir = ArgValue("-buildOut") ?? RepoPath("Build", "Windows");
        Directory.CreateDirectory(outDir);

        ApplyAppIcon();
        EnsureShadersIncluded();

        var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        if (scenes.Length == 0) scenes = new[] { "Assets/Scenes/SampleScene.unity" };

        var opts = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = Path.Combine(outDir, ExeName),
            target = BuildTarget.StandaloneWindows64,
            targetGroup = BuildTargetGroup.Standalone,
            options = BuildOptions.None,
        };

        Debug.Log($"[Build] scenes=[{string.Join(", ", scenes)}] -> {opts.locationPathName}");
        BuildReport report = BuildPipeline.BuildPlayer(opts);
        BuildSummary s = report.summary;
        Debug.Log($"[Build] result={s.result} errors={s.totalErrors} warnings={s.totalWarnings} " +
                  $"size={s.totalSize} time={s.totalTime} out={s.outputPath}");

        if (s.result != BuildResult.Succeeded)
        {
            foreach (var step in report.steps)
                foreach (var msg in step.messages)
                    if (msg.type == LogType.Error || msg.type == LogType.Exception)
                        Debug.LogError($"[Build] {step.name}: {msg.content}");
            EditorApplication.Exit(1);
        }

        PackageData(outDir);
        EditorApplication.Exit(0);
    }

    // Append every RequiredShaders entry to GraphicsSettings' "Always Included Shaders" list (idempotent) so the
    // runtime Shader.Find() calls resolve in the built player. Persists to ProjectSettings/GraphicsSettings.asset.
    private static void EnsureShadersIncluded()
    {
        var so = new SerializedObject(GraphicsSettings.GetGraphicsSettings());
        var arr = so.FindProperty("m_AlwaysIncludedShaders");
        if (arr == null) { Debug.LogWarning("[Build] m_AlwaysIncludedShaders not found — skipping shader inclusion."); return; }

        var present = new HashSet<Shader>();
        for (int i = 0; i < arr.arraySize; i++)
            if (arr.GetArrayElementAtIndex(i).objectReferenceValue is Shader s && s != null) present.Add(s);

        bool changed = false;
        foreach (var name in RequiredShaders)
        {
            var sh = Shader.Find(name);
            if (sh == null) { Debug.LogWarning($"[Build] shader not found (cannot include): {name}"); continue; }
            if (present.Contains(sh)) continue;
            arr.arraySize++;
            arr.GetArrayElementAtIndex(arr.arraySize - 1).objectReferenceValue = sh;
            present.Add(sh);
            changed = true;
            Debug.Log($"[Build] +always-included shader: {name}");
        }
        if (changed) { so.ApplyModifiedProperties(); AssetDatabase.SaveAssets(); }
        Debug.Log($"[Build] always-included shaders total = {arr.arraySize}");
    }

    // Set the standalone player icon from Assets/AppIcon.png (converted from Icon3.ico). The texture must be
    // readable/uncompressed so Unity can resize it into every required icon slot; we fill all slots with it.
    private static void ApplyAppIcon()
    {
        var importer = AssetImporter.GetAtPath(IconAsset) as TextureImporter;
        if (importer != null)
        {
            importer.isReadable = true;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();
        }
        var icon = AssetDatabase.LoadAssetAtPath<Texture2D>(IconAsset);
        if (icon == null) { Debug.LogWarning($"[Build] icon '{IconAsset}' not found — using Unity default icon."); return; }

        var nbt = NamedBuildTarget.Standalone;
        int[] sizes = PlayerSettings.GetIconSizes(nbt, IconKind.Application);
        var icons = new Texture2D[sizes.Length > 0 ? sizes.Length : 1];
        for (int i = 0; i < icons.Length; i++) icons[i] = icon;
        PlayerSettings.SetIcons(nbt, icons, IconKind.Application);
        Debug.Log($"[Build] applied app icon '{IconAsset}' to {icons.Length} standalone slot(s).");
    }

    // Assemble the DATA folder beside the exe by invoking the packaging script. Paths derive from the repo root
    // (no hardcoded drive letters); failures are logged but do not fail the build (data can be packaged manually).
    private static void PackageData(string outDir)
    {
        string script = RepoPath("tools", "package_build.ps1");
        if (!File.Exists(script)) { Debug.LogWarning($"[Build] packaging script missing: {script}"); return; }

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -BuildDir \"{outDir}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        try
        {
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (!string.IsNullOrWhiteSpace(stdout)) Debug.Log("[Build][package]\n" + stdout);
                if (!string.IsNullOrWhiteSpace(stderr)) Debug.LogWarning("[Build][package:err]\n" + stderr);
                Debug.Log($"[Build] packaging exit={p.ExitCode}");
            }
        }
        catch (System.Exception e) { Debug.LogWarning($"[Build] packaging failed: {e.Message}"); }
    }

    // Repo root = three levels up from Application.dataPath (<repo>/65/My project/Assets).
    private static string RepoPath(params string[] parts)
    {
        string repo = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", ".."));
        return parts.Length == 0 ? repo : Path.Combine(repo, Path.Combine(parts));
    }

    private static string ArgValue(string flag)
    {
        var a = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < a.Length - 1; i++) if (a[i] == flag) return a[i + 1];
        return null;
    }
}
#endif
