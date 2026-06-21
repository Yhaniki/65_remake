#if UNITY_EDITOR
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

// Headless Windows player build, invoked via:
//   Unity.exe -batchmode -nographics -quit -projectPath "<proj>" -executeMethod BuildScript.BuildWindows
// Output: <buildOut>/dance.exe  (default <repo>/Build/Windows, override with -buildOut <dir>).
// After a successful build the player icon is the project's AppIcon (from Icon3.ico) and the SDO game data is
// assembled into <buildOut>/DATA by tools/package_build.ps1, so the output runs without dev-machine paths.
public static class BuildScript
{
    private const string ExeName = "dance.exe";
    private const string IconAsset = "Assets/AppIcon.png";

    public static void BuildWindows()
    {
        string outDir = ArgValue("-buildOut") ?? RepoPath("Build", "Windows");
        Directory.CreateDirectory(outDir);

        ApplyAppIcon();

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
