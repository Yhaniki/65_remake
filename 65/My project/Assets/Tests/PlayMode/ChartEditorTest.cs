using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Sdo.Game;
using Sdo.Osu;
using UnityEngine;
using UnityEngine.SceneManagement;
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

        // PlayMode 的測試共用同一個場景：一個測試留下來的 ScreenGameplay（以及它生出來的一堆根物件）會被
        // 下一個測試的 FindAnyObjectByType 撈到，驗到的就不是自己建的那個了。每個測試前後照差集清乾淨。
        private HashSet<GameObject> _preRoots;

        [SetUp]
        public void SnapshotRoots()
            => _preRoots = new HashSet<GameObject>(SceneManager.GetActiveScene().GetRootGameObjects());

        [TearDown]
        public void DestroySpawnedRoots()
        {
            foreach (var go in SceneManager.GetActiveScene().GetRootGameObjects())
                if (!_preRoots.Contains(go)) Object.DestroyImmediate(go);
            Time.timeScale = 1f;   // 編輯器暫停時把 timeScale 歸零過
        }

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

            // (4b) 單首 offset（F11/F12）：**動的是音樂，音符不動**。
            //      譜面時間不變（音符/判定線一格都不跳），但音樂的播放位置要往回退 = 音樂延後播出來。
            if (game.EditorClip != null)
            {
                // ★先強制暫停★：EditorClipSec 播放中回報 _audio.time(會隨真實時間漂移),暫停中才回報 seek 當下算好的
                // 確定值。歌單刪減後預設開的歌換人,新歌在此點恰好還在播 → offset 的 −100ms 被播放漂移稀釋成 −54ms。
                game.EditorSetPaused(true);
                yield return null;
                // 再 seek 到「音檔正中央」：中位音符對某些短音檔會落在音檔尾端外,換成音檔一半的位置保證落在 clip 內部。
                //   clipSec = chartSec − MusicCountIn(見 EditorSeekMs) → chartMs = clipTargetSec×1000 + MusicCountInMs。
                double clipTargetSec = game.EditorClip.length * 0.5;
                game.EditorSeekMs(clipTargetSec * 1000.0 + game.EditorMusicCountInMs);
                yield return null;

                // ★EditorSongOffsetMs 是「絕對設定」不是相加★:先歸零建立基準,再設 100 —— 這樣「變化量」一定是 +100ms。
                // 不能只 =100 就假設變化 100ms:歌本身在 song_table.csv 帶了自己的 offsetMs(這首 ≈46ms),setter 把它整個
                // 換掉 → 變化量 = 100 − 46 = 54ms(歌單刪減後預設開的歌換人才踩到,舊歌 offsetMs 剛好是 0)。
                game.EditorSongOffsetMs = 0.0;
                double chartBefore = game.EditorNowMs;
                double clipBefore = game.EditorClipSec;

                game.EditorSongOffsetMs = 100.0;   // 音樂延後 100ms（相對基準 0 → 正好 +100ms）
                double clipAfter = game.EditorClipSec;

                Assert.AreEqual(chartBefore, game.EditorNowMs, 3.0,
                    "單首 offset 不該移動譜面時鐘 —— 音符/判定線要待在原地");
                // 測「音樂往回退的量」而非絕對位置：offset +100ms → clip 位置正好少 100ms（差值才是真正的不變量，
                // 不受這首歌音檔長短/seek 到哪裡影響）。
                Assert.AreEqual(0.100, clipBefore - clipAfter, 0.010,
                    "單首 offset +100ms → 音樂要往回退 100ms（＝之後才播到現在這個位置）");
                Assert.AreEqual(game.EditorMusicDelaySec * 1000.0 + 100.0, game.EditorMusicCountInMs, 1.0,
                    "波形的時間原點要跟著音樂走（不然波形會跟音符一起動）");

                game.EditorSongOffsetMs = 0.0;     // 還原，後面的波形檢查才不受影響
                yield return null;
            }

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
                // 波形第 0 格 = 音樂起點（type-10 無聲數拍）**再往早補解碼暖機**：Unity 的 Vorbis 解碼在 clip 開頭
                // 留了一段暖機樣本，不補的話波形瞬態整條晚到、看起來音符比波形早。純顯示修正，見 WaveformDecoderDelayMs。
                Assert.AreEqual(game.EditorMusicDelaySec * 1000.0 - ScreenGameplay.WaveformDecoderDelayMs,
                    overlay.PeaksOffsetMs, 1.0, "波形的時間原點沒有對到音樂起點（type-10 無聲數拍 − 解碼暖機）");
            }

            yield return null;
            Capture("H:/65_remake/chart-editor-capture.png");
        }

        /// <summary>
        /// 打拍測試（校時）：不讀 .gn、不放音樂，只有固定 BPM 的等距音符。這裡釘住兩件事——
        /// (1) 合成譜真的長在 R 軌、間距 = 一拍；(2) global offset 的**方向**：offset 加大 → 譜面時鐘往前 →
        /// 同一下打擊的 delta 變大（判得比較晚）。方向弄反的話，照著建議值調只會越調越糟。
        /// </summary>
        [UnityTest]
        public IEnumerator BeatTest_SynthChart_AndGlobalOffsetShiftsClockForward()
        {
            KillByName("FrontendApp");
            KillByName("FrontendCanvas");
            yield return null;

            var game = new GameObject("ScreenGameplay_beattest").AddComponent<ScreenGameplay>();
            game.editorMode = true;
            game.beatTestMode = true;
            game.beatTestBpm = 120f;          // 一拍 500ms
            game.assistTick = true;
            game.effectCharacter = false;
            game.effectScene = false;

            float t0 = Time.realtimeSinceStartup;
            while (!game.EditorReady && Time.realtimeSinceStartup - t0 < 30f) yield return null;
            Assert.IsTrue(game.EditorReady, "打拍測試 30 秒內沒有備妥");

            // (1) 合成譜：全部在 R 軌、每拍一顆、沒有音樂
            var map = game.EditorMap;
            Assert.Greater(map.TotalNotes, 100);
            Assert.IsNull(game.EditorClip, "打拍測試不該載入任何音樂");
            for (int i = 1; i < 20; i++)
            {
                Assert.AreEqual(BeatTestChart.RightLane, map.HitObjects[i].Lane);
                Assert.AreEqual(500, map.HitObjects[i].StartTimeMs - map.HitObjects[i - 1].StartTimeMs);
            }
            Assert.IsNotNull(game.EditorWindows, "誤差條需要判定窗");

            // (2) global offset 的方向
            game.EditorSeekMs(10000);
            yield return null;
            double before = game.EditorNowMs;

            game.EditorGlobalOffsetMs = 40.0;
            yield return null;
            double after = game.EditorNowMs;

            Assert.AreEqual(40.0, after - before, 2.0,
                "offset +40 → 譜面時鐘要往前 40ms（同一下打擊的 delta 變大 = 判得比較晚）");

            // (3) 單首 offset 動的是「音樂」不是「音符」→ 它不該碰譜面時鐘（打拍測試沒有音樂，所以什麼都不該變）
            game.EditorSongOffsetMs = -10.0;
            yield return null;
            Assert.AreEqual(after, game.EditorNowMs, 2.0,
                "單首 offset 不該移動譜面時鐘（音符/判定線不動，動的是音樂）");
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
