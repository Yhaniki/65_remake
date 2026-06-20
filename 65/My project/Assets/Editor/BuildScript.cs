#if UNITY_EDITOR
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

// Headless Windows player build, invoked via:
//   Unity.exe -batchmode -nographics -quit -projectPath "<proj>" -executeMethod BuildScript.BuildWindows
// Output: H:/65_remake/Build/Windows/SDO65.exe  (override with -buildOut <dir>).
public static class BuildScript
{
    public static void BuildWindows()
    {
        string outDir = ArgValue("-buildOut") ?? "H:/65_remake/Build/Windows";
        Directory.CreateDirectory(outDir);

        var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        if (scenes.Length == 0) scenes = new[] { "Assets/Scenes/SampleScene.unity" };

        var opts = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = Path.Combine(outDir, "SDO65.exe"),
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
        EditorApplication.Exit(0);
    }

    private static string ArgValue(string flag)
    {
        var a = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < a.Length - 1; i++) if (a[i] == flag) return a[i + 1];
        return null;
    }
}
#endif
