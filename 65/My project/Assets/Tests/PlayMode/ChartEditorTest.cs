using System.Collections;
using System.IO;
using NUnit.Framework;
using Sdo.Game;
using UnityEngine;
using UnityEngine.TestTools;

namespace Sdo.Tests
{
    /// <summary>
    /// 譜面編輯器的端對端驗證：真的把 <see cref="ChartEditorScreen"/> 開起來（它會重用 ScreenGameplay 的 editorMode），
    /// 檢查「純黑背景（沒有場景/舞者）＋音符讀進來＋可自由 seek＋波形真的從 PCM 解出來」，最後存一張 PNG 供人眼複核。
    /// Run: -runTests -testPlatform PlayMode -testFilter Sdo.Tests.ChartEditorTest
    /// </summary>
    public class ChartEditorTest
    {
        private const int W = 800, H = 600;

        [UnityTest]
        public IEnumerator ChartEditor_BlackBackground_Notes_Seek_And_Waveform()
        {
            // 測試場景裡前端會自己開起來（RuntimeInitializeOnLoadMethod），而且它前三幀會 KillStrayGameplay
            // 把任何 ScreenGameplay 殺掉 → 先把它請走（實機是靠 SDO_EDITOR 讓前端直接不啟動）。
            KillByName("FrontendApp");
            KillByName("FrontendCanvas");
            yield return null;

            PlayerPrefs.DeleteKey(ChartEditorScreen.PrefLastGn);   // 沒有「上次那首」→ 走預設：編號最大的那首
            var ed = new GameObject("ChartEditor_test").AddComponent<ChartEditorScreen>();
            Assert.IsNotNull(ed);

            ScreenGameplay game = null;
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 60f)
            {
                game = Object.FindAnyObjectByType<ScreenGameplay>();
                if (game != null && game.EditorReady) break;
                yield return null;
            }
            Assert.IsNotNull(game, "編輯器沒有建出 ScreenGameplay");
            Assert.IsTrue(game.EditorReady, "編輯器 60 秒內沒有備妥（譜面/音樂載不進來？）");

            // (1) 純黑背景：不載 3D 場景、不載舞者，連場景背景 quad 都不該存在
            Assert.IsNull(GameObject.Find("StageScene"), "編輯器不該載入 3D 場景");
            Assert.IsNull(GameObject.Find("Avatar3D"), "編輯器不該載入舞者");
            Assert.IsNull(GameObject.Find("SceneBackdrop"), "編輯器不該有場景背景 quad");
            Assert.AreEqual(Color.black, Camera.main.backgroundColor, "背景不是純黑");

            // (2) 譜面讀進來了，而且預設開的是「編號最大」的那首（＝最後匯入的新歌，且檔案真的在 DATA 樹裡）
            Assert.Greater(game.EditorMap.TotalNotes, 0, "沒有讀到任何音符");
            Assert.AreEqual(ExpectedNewestGn(), Path.GetFileName(game.gnPath).ToLowerInvariant(),
                "預設沒有開到編號最大的那首");

            // (3) 開場停在 0ms（不放 READY/GO，等使用者按播放）
            Assert.IsTrue(game.EditorPaused, "編輯器開場應該是暫停的");
            Assert.AreEqual(0.0, game.EditorNowMs, 2.0, "編輯器開場應該停在 0ms");

            // (4) seek：暫停中也要能跳，而且跳到某顆音符的時間時，那顆音符要正好落在受擊線上
            //     （時間錨點/捲動方向/變速任何一個接錯，這條就會歪掉）
            var note = game.EditorMap.HitObjects[game.EditorMap.HitObjects.Count / 2];
            game.EditorSeekMs(note.StartTimeMs);
            yield return null;                       // 讓 Update 跑一幀（時鐘 + ScrollNotes）
            Assert.AreEqual(note.StartTimeMs, game.EditorNowMs, 30.0, "seek 之後的譜面時間不對");
            Assert.AreEqual(game.EditorJudgeLineY, game.EditorYForTime(note.StartTimeMs), 12.0,
                "seek 到某顆音符的時間，該音符卻不在受擊線上");

            // (5) 波形：真的從 AudioClip 的 PCM 解出來（不是全靜音）
            if (game.EditorClip != null)
            {
                var overlay = Object.FindAnyObjectByType<ChartEditorOverlay>();
                Assert.IsNotNull(overlay, "沒有建出波形/格線覆蓋層");
                t0 = Time.realtimeSinceStartup;
                while (overlay.Peaks == null && Time.realtimeSinceStartup - t0 < 90f) yield return null;
                Assert.IsNotNull(overlay.Peaks, "波形沒有解出來");
                Assert.Greater(overlay.Peaks.Count, 100, "波形格數過少");
                float maxPeak = 0f, maxRms = 0f, minRms = 1f;
                foreach (var p in overlay.Peaks.Peak) if (p > maxPeak) maxPeak = p;
                foreach (var r in overlay.Peaks.Rms) { if (r > maxRms) maxRms = r; if (r < minRms) minRms = r; }
                Assert.Greater(maxPeak, 0.5f, "波形全是靜音 → GetData 沒讀到 PCM");
                // RMS 必須真的有起伏：全曲一路貼在最大值 = 畫出來會是一根實心柱（不是波形）
                Assert.Less(minRms, 0.5f * maxRms, "RMS 沒有起伏 —— 波形會變成一根實心柱");
                Assert.AreEqual(game.EditorMusicDelaySec * 1000.0, overlay.PeaksOffsetMs, 1.0,
                    "波形的時間原點沒有對到音樂起點（type-10 無聲數拍）");
            }

            yield return null;
            Capture("H:/65_remake/chart-editor-capture.png");
        }

        // 獨立重算一次「編號最大、有譜、檔案真的存在」的那首（不呼叫產品程式的同一支函式，才驗得出東西）
        private static string ExpectedNewestGn()
        {
            SongCatalog.Entry best = null;
            foreach (var e in SongCatalog.All)
            {
                if (!(e.HasChart(0) || e.HasChart(1) || e.HasChart(2))) continue;
                if (best != null && e.fileId <= best.fileId) continue;
                var p = SongPaths.Gn(e.gn);
                if (!string.IsNullOrEmpty(p) && File.Exists(p)) best = e;
            }
            Assert.IsNotNull(best, "MUSIC 資料夾裡一首有譜的歌都找不到");
            return best.gn.ToLowerInvariant();
        }

        private static void KillByName(string name)
        {
            var go = GameObject.Find(name);
            if (go != null) Object.DestroyImmediate(go);
        }

        // 主相機（正交 800×600）的離屏render：黑底 + 音符板 + 音符 + 波形/格線。IMGUI 的工具列不會進 RT，剛好。
        private static void Capture(string path)
        {
            var main = Camera.main;
            if (main == null) return;
            var rt = new RenderTexture(W, H, 24);
            main.targetTexture = rt; main.Render(); main.targetTexture = null;
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0); tex.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.Destroy(tex); Object.Destroy(rt);
            Debug.Log("[chart-editor] capture -> " + path);
        }
    }
}
