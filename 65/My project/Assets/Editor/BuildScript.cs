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
        "Sdo/UiScreenGain",      // 畫面亮度 overlay(BrightnessOverlay,乘法); stripped -> 只剩黑幕變暗、調亮完全失效
        "Sdo/NoteCutout",        // 3D note highway cut-out sprites; stripped -> notes fall back to opaque (Note3dHighway/ScreenGameplay)
        "Sdo/UnlitSpotGlow",     // gameplay spotlight glow; stripped -> glow renders wrong (ScreenGameplay)
        "Sdo/LensFlare",         // SCN0004 太陽鏡頭光斑(ONE/ONE 純加法); stripped -> Shader.Find null -> 退回 Unlit/Texture 變成不透明黑方塊
        "Sdo/PortraitOpaque",
        "Sdo/UnlitDoubleSided",

        "Sdo/UnlitAvatarAlpha",  // 眼鏡鏡片 + 翅膀(CHIBANG)/去背飾品 alpha-blend; stripped -> Shader.Find null -> 退回 UnlitDoubleSided(cutout,c.a=1
                                 // 強制不透明) -> 翅膀周圍閃爍貼圖/眼鏡鏡片「透明度沒做出來」(editor 不 strip 故只在打包版壞)
        "Sdo/UnlitAvatarSheer",  // 真紗質/蕾絲衣料(Flower Lace Dress)alpha-blend + 密度提升(_Density);stripped -> 退回 cutout -> 紗變實心黑袖
        "Sdo/AvatarDepthOnly",   // 半透明衣物/翅膀的只寫深度分身(AvatarTranslucentDepth);stripped -> Attach 印警告後退回舊行為
                                 // (半透明翅膀擋不住頭上名字牌)—— 不會壞畫面,但只在打包版才復發
        "Sdo/UnlitInstanced",
        "Sdo/UnlitInstancedAlpha",   // alpha-blended mapobj props ("去背"); stripped -> alpha stage props go magenta
        "Sdo/UnlitInstancedAlphaCullBack", // mirrored separated alpha planes (SCN0011 DING): single-sided blend
        "Sdo/UnlitAdditiveOverlay",  // soft-alpha mapobj glow sprites (bulbs, lasers, sweep lights)
        "Sdo/UnlitOverlay",          // animated transparent overlays (FIFA crowd, texanim cut-outs)
        "Sdo/SceneVertexAlpha",      // soft-alpha SCENE.MSH materials (glass floors, feathered decals)
        "Sdo/SceneVertexCutout",     // base SCENE.MSH × baked vertex lighting (scene darkening/tint)
        "Sdo/UnlitInstancedCutout",  // solid/volumetric alpha mapobj props (SCN0006 carousel — no see-through)
        "Sdo/SpritePremultiply",     // result YOU WIN/LOSE banner: premult-alpha so bilinear MAGNIFICATION has no 白邊
                                     // halo; stripped -> Shader.Find null -> banner falls back to straight-alpha (halo returns)
        "Sdo/MmdModel",              // MMD-avatar base+sphere+toon+outline; stripped -> Miku falls back to Unlit (no fx)

        // lilToon (Assets/lilToon, MIT) — MMD 身體的第二種著色後端 (config.ini [Mmd] mmdLilToon)。同樣只在執行期
        // 用 Shader.Find 取,而且是六支:不透明/裁切/透明 × 有無描邊,lilToon 把它們拆成各自的 shader。
        // 被剝掉 -> MmdAvatar 退回 Sdo/MmdModel(畫面還在,但開關等於沒作用)。名稱見 MmdLilToon 的常數。
        "lilToon",
        "Hidden/lilToonOutline",
        "Hidden/lilToonCutout",
        "Hidden/lilToonCutoutOutline",
        "Hidden/lilToonTransparent",
        "Hidden/lilToonTransparentOutline",
    };

    public static void BuildWindows()
    {
        string outDir = ArgValue("-buildOut") ?? RepoPath("Build", "Windows");
        Directory.CreateDirectory(outDir);

        ApplyAppIcon();

        // 🔴 加進 Always Included Shaders 之後**一定要還原**（跟下面的 productName 同一個道理）。
        //    留在 ProjectSettings/GraphicsSettings.asset 裡的話，**editor 也會一併載入 lilToon 的全部變體** ——
        //    MmdLilToonRenderTests 建 RenderTexture 時就把 GPU 打到 TDR（nvlddmkm 重置），
        //    之後每一個渲染測試連鎖假紅 + Unity 原生崩潰，而且看起來完全像是「自己改壞了」。
        //    2026-08-05 為此連丟三輪測試才定位。
        int originalShaderCount = CaptureAlwaysIncludedShaderCount();
        EnsureShadersIncluded();

        // Stamp the git version into the window title (PlayerSettings.productName). Captured so we can put it back
        // afterwards — leaving the versioned title in ProjectSettings.asset would dirty the tracked file on every build.
        string originalTitle = PlayerSettings.productName;
        ApplyWindowTitle(ComputeWindowTitle(originalTitle), originalTitle);
        try
        {
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

            // Restore before any exit: EditorApplication.Exit() does not run finally blocks, so put it back here.
            RestoreWindowTitle(originalTitle);
            RestoreAlwaysIncludedShaders(originalShaderCount);

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
        catch
        {
            RestoreWindowTitle(originalTitle);   // exception path (unlike Exit, this DOES run before the throw propagates)
            throw;
        }
    }

    // Build the versioned window title from git. SDO_BUILD_TITLE (env) overrides everything (CI / manual). Otherwise:
    // on a tag -> "<product> <tag>", after a tag -> "<product> <tag>-dev-<hash5>", no git -> the original product name.
    private static string ComputeWindowTitle(string fallback)
    {
        string env = System.Environment.GetEnvironmentVariable("SDO_BUILD_TITLE");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

        string exact   = Git("describe --tags --exact-match HEAD"); // null unless HEAD is exactly on a tag
        string nearest = Git("describe --tags --abbrev=0");         // closest ancestor tag (null if repo has no tags)
        string hash5   = Git("rev-parse --short=5 HEAD");           // 5-char commit hash

        string title = Sdo.Game.BuildTitle.Format(fallback, exact, nearest, hash5);
        return string.IsNullOrEmpty(title) ? fallback : title;
    }

    private static void ApplyWindowTitle(string title, string original)
    {
        if (string.IsNullOrEmpty(title) || title == original)
        {
            Debug.Log($"[Build] window title unchanged = \"{original}\" (git unavailable or no override)");
            return;
        }
        PlayerSettings.productName = title;   // in-memory value BuildPipeline.BuildPlayer bakes into the player
        Debug.Log($"[Build] window title (productName) = \"{title}\"  (was \"{original}\")");
    }

    private static void RestoreWindowTitle(string original)
    {
        if (PlayerSettings.productName == original) return;
        PlayerSettings.productName = original;
        AssetDatabase.SaveAssets();   // flush ProjectSettings.asset so the versioned title never lands in git
        Debug.Log($"[Build] restored window title (productName) = \"{original}\"");
    }

    // Run `git <args>` at the repo root; return trimmed stdout on exit 0, else null (no tags / not a repo / git missing).
    private static string Git(string args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = args,
                WorkingDirectory = RepoPath(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                string outp = p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();   // drain so git can't block on a full stderr pipe
                p.WaitForExit();
                return p.ExitCode == 0 ? outp.Trim() : null;
            }
        }
        catch { return null; }
    }

    /// <summary>Always Included Shaders 目前的長度。<see cref="EnsureShadersIncluded"/> **只會往後追加**，
    /// 所以還原＝截回這個長度就好。
    ///
    /// 🔴 不要改成「抓一份 Shader 參照的快照再寫回去」—— 那份清單裡可能有**懸空參照**
    /// （指向已刪除／來自套件的 shader，YAML 長成 <c>{fileID: 4800000, guid: …}</c> 但資產不在）。
    /// <c>objectReferenceValue as Shader</c> 對它們回 null，寫回去就變成 <c>{fileID: 0}</c>，
    /// 檔案於是「還原完還是髒的」。踩過一次：本專案就有兩筆這種懸空條目。
    /// 讀不到屬性 → -1，<see cref="RestoreAlwaysIncludedShaders"/> 會當成 no-op。</summary>
    private static int CaptureAlwaysIncludedShaderCount()
    {
        var so = new SerializedObject(GraphicsSettings.GetGraphicsSettings());
        var arr = so.FindProperty("m_AlwaysIncludedShaders");
        return arr == null ? -1 : arr.arraySize;
    }

    /// <summary>把 Always Included Shaders 截回 build 之前的長度。
    ///
    /// 為什麼要還原:那份清單留在工作樹裡會讓 **editor** 也一併載入 lilToon 的全部變體，
    /// EditMode 的渲染測試(MmdLilToonRenderTests)建 RenderTexture 時就把 GPU 打到 TDR，
    /// 整批假紅 + Unity 原生崩潰 —— 而且症狀看起來完全像是自己改壞了程式碼。
    /// build 需要它、測試受不了它，所以 build 完必須放回去。</summary>
    private static void RestoreAlwaysIncludedShaders(int originalCount)
    {
        if (originalCount < 0) return;

        var so = new SerializedObject(GraphicsSettings.GetGraphicsSettings());
        var arr = so.FindProperty("m_AlwaysIncludedShaders");
        if (arr == null || arr.arraySize <= originalCount) return;   // 沒被動過 → 不要白寫一次檔

        arr.arraySize = originalCount;
        so.ApplyModifiedProperties();
        AssetDatabase.SaveAssets();
        Debug.Log($"[Build] restored always-included shaders = {originalCount}");
    }

    // Append every RequiredShaders entry to GraphicsSettings' "Always Included Shaders" list (idempotent) so the
    // runtime Shader.Find() calls resolve in the built player. Persists to ProjectSettings/GraphicsSettings.asset —
    // 但 build 結束前會由 RestoreAlwaysIncludedShaders 放回原狀（見那裡的說明）。
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
        // SDO_SKIP_PACKAGE=1 → skip assembling DATA beside the exe (used by the dead-file probe build, which points
        // SdoExtracted.Root at an existing DATA tree via SDO_PROBE and never reads a packaged DATA).
        if (!string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("SDO_SKIP_PACKAGE")))
        { Debug.Log("[Build] SDO_SKIP_PACKAGE set — skipping DATA packaging."); return; }

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
