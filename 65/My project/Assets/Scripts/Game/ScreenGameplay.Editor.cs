using System;
using System.Collections;
using Sdo.Osu;
using Sdo.Ruleset;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// 譜面編輯器模式（<see cref="editorMode"/>）。整個遊玩畫面照常建起來 —— 音符板、受擊線、音符、note 皮、
    /// 捲動/變速數學全部沿用 —— 只是：
    ///   • 不載 3D 場景、不載舞者、不放特效 → 主相機本來就是 SolidColor 黑（SdoLayout.SetupCamera）＝純黑背景；
    ///   • 不判定、不扣血、不計分、不結算、沒有 READY/GO 開場；
    ///   • 時間可以自由 seek（暫停中也能拖），音樂跟著跳。
    ///
    /// 時間軸的四個錨點必須一起搬，少一個就音畫不同步（見 GameRate 的註解）：
    ///   _songStartDspTime（音訊排程用的 dsp 錨點）、_clockStart（譜面時鐘的 wall 基準）、
    ///   AudioSource 的播放位置、_clock（平滑器要重新 seed）。<see cref="EditorSeekMs"/> 就是把這四件事做完。
    ///
    /// UI／輸入在 <see cref="ChartEditorScreen"/>，這裡只提供最小的存取面。
    /// </summary>
    public sealed partial class ScreenGameplay
    {
        /// <summary>譜面編輯器模式：純黑背景（無場景/舞者）＋可自由 seek，不判定也不結算。</summary>
        public bool editorMode;

        // ---------- 打拍測試（校時）----------

        /// <summary>打拍測試：不讀 .gn、不放音樂，改用固定 BPM 的等距音符（節拍音＝assist tick）。用來校 offset。</summary>
        public bool beatTestMode;

        /// <summary>打拍測試的 BPM。</summary>
        public float beatTestBpm = 120f;

        /// <summary>每幾拍一顆音符（1 = 4 分音符）。</summary>
        public float beatTestBeatsPerNote = 1f;

        /// <summary>打拍測試的長度（秒）。5 分鐘的連續打點遠遠夠校時，再長只是白白多生幾千個音符物件。</summary>
        public const double BeatTestDurationSec = 300.0;

        /// <summary>每次判定到就回報：deltaMs = 打擊時間 − 音符時間（負 = 太早、正 = 太晚）；miss 時 delta 為 NaN。</summary>
        public Action<double, Judgment> EditorOnHit;

        /// <summary>目前的判定窗（畫誤差條要用）。</summary>
        public JudgmentWindows EditorWindows => _engine?.Windows;

        /// <summary>全域判定 offset（毫秒）。補這台機器的整條延遲；正 = 判定時間往後 → 適合整體打太早的人。</summary>
        public double EditorGlobalOffsetMs
        {
            get => _globalOffsetMs;
            set { _globalOffsetMs = (float)value; ApplyClockOffset(); }
        }

        /// <summary>
        /// 單首歌的 offset（毫秒）。補「這首譜跟音檔沒對齊」。<b>動的是音樂，音符/判定線不動</b>：
        /// 改完只重新錨定音樂（用同一個譜面時間 re-seek），所以畫面上的音符一格都不會跳，只有音樂前後挪。
        /// 正 = 音樂延後播放。
        /// </summary>
        public double EditorSongOffsetMs
        {
            get => songOffsetMs;
            set
            {
                songOffsetMs = (float)value;
                if (_started) EditorSeekMs(_nowMs);   // 譜面時間不變 → 音符不動；音樂被重新排到新的 count-in 上
            }
        }

        /// <summary>音樂目前播到第幾秒（clip 位置）。單首 offset 動的就是它 —— 測試用來確認「動的是音樂不是音符」。</summary>
        public double EditorClipSec => (_audio != null && _audio.clip != null) ? _audio.time : 0.0;

        /// <summary>
        /// 顯示縮放（＝下落速度）。StepMania 編輯器的 Ctrl+↑/↓ 就是改這個：速度越快，同一段畫面涵蓋的時間越短
        /// （＝「區域變窄」）。步進 0.5、底端特例 0.25（ScreenEdit.cpp 的 fNewScrollSpeed）。
        /// </summary>
        public void EditorZoom(int dir)
        {
            if (_map == null) return;
            float s = scrollSpeedMul;
            if (dir > 0)   // 變窄（速度變快）
            {
                if (s <= 0.25f + 1e-3f) s = 0.5f;
                else if (s < 8f) s = Mathf.Min(8f, s + 0.5f);
            }
            else           // 變寬（速度變慢）
            {
                if (s >= 1f) s -= 0.5f;
                else if (s >= 0.5f - 1e-3f) s = 0.25f;
            }
            scrollSpeedMul = Mathf.Clamp(s, 0.25f, 8f);
            BuildScroll();   // 速度是烘進 ManiaScroll 的 → 一定要重建，不然畫面不會變
        }

        public float EditorScrollSpeed => scrollSpeedMul;

        // 打拍測試的判定：只回報誤差，不進分數/血量/連段/結算（ApplyEvent 那一整套在這裡沒有意義）。
        private void EditorJudgeTick(double now)
        {
            var laneKeys = laneKeyOverride ?? DefaultLaneKeys;
            for (int lane = 0; lane < Keys; lane++)
            {
                bool down = false;
                foreach (var k in laneKeys[lane]) if (Input.GetKeyDown(k)) down = true;
                if (down) EditorPressLane(lane, now);
            }
            // 沒打到的音符走過判定窗 → 記一次 miss
            foreach (var n in _notes)
            {
                if (n.Done || n.HeadJudged) continue;
                if (!_engine.HasPassed(n.Note.StartTimeMs, now)) continue;
                n.HeadJudged = true; n.Done = true;
                EditorOnHit?.Invoke(double.NaN, Judgment.Miss);
            }
        }

        private void EditorPressLane(int lane, double now)
        {
            var n = NearestHittable(lane, now);
            if (n == null) return;                       // 空打（判定窗內沒有音符）→ 不計入統計，同 osu
            var j = _engine.JudgeHit(n.Note.StartTimeMs, now);
            if (j == null) return;
            n.HeadJudged = true; n.Done = true;          // 打拍測試只有 tap
            EditorOnHit?.Invoke(now - n.Note.StartTimeMs, j.Value);   // delta：負 = 太早、正 = 太晚（同 osu）
            TriggerClickFlash(lane);                                   // 打到有回饋
            if (_burstFrames != null && (j == Judgment.Perfect || j == Judgment.Cool)) SpawnBurst(lane, false);
        }

        /// <summary>編輯器可 seek 的尾端（毫秒）＝ 最後一顆音符 與 音樂長度 取大者。</summary>
        public double EditorEndMs { get; private set; }

        /// <summary>建置完成且時鐘已起跑（UI 可以開始畫了）。</summary>
        public bool EditorReady => editorMode && _sceneBootDone && _started;

        public double EditorNowMs => _nowMs;
        public bool EditorPaused => _paused;
        public double EditorRate => _musicRate;
        public OsuBeatmap EditorMap => _map;
        public AudioClip EditorClip => _audio != null ? _audio.clip : null;

        /// <summary>音檔載入「已經試過了」（成功或失敗都算）—— 沒有 .ogg 的歌不會有 clip，等它是等不到的。</summary>
        public bool EditorAudioReady => _audioReady;

        /// <summary>音樂比譜面第 0 拍晚多少秒進來（type-10 音樂起點的無聲數拍）。</summary>
        public double EditorMusicDelaySec => _musicStartDelaySec;

        /// <summary>音樂實際的 count-in（毫秒）＝ type-10 無聲數拍 ＋ 單首 offset。波形的第 0 格對應的譜面時間就是它。</summary>
        public double EditorMusicCountInMs => MusicCountInSec * 1000.0;

        // ---- 幾何：覆蓋層（格線/波形）用「跟音符同一套」的時間→Y，才不會有一格誤差 ----
        public float EditorYForTime(double ms) => YForTime(ms, _nowMs);
        public int EditorScrollSign => _scrollSign;
        public float EditorJudgeLineY => judgeLineY;
        public float EditorClipTopY => _clipTopY;
        public float EditorClipBottomY => _clipBottomY;
        public float EditorTrackLeftPx => PX(LaneLeftX[0]);
        public float EditorTrackRightPx => PX(LaneLeftX[Keys - 1] + 69f);

        // ---------- boot ----------

        // 編輯器的開場：不放 READY/GO、不排 lead-in，直接停在 0ms（暫停）等使用者按播放。
        // 由 LoadAndPlayAudio() 在音檔載完後接手（_audioReady 已為 true → BootRevealCo 會馬上掀開載入畫面）。
        private IEnumerator EditorOpeningCo()
        {
            while (!_bootRevealed) yield return null;

            _musicStartDelaySec = (useMusicStartOffset && _map != null) ? _map.MusicStartOffsetMs / 1000.0 : 0.0;
            _danceStartSec = 0.0;

            // 可 seek 的範圍：音樂通常比最後一顆音符長（尾奏），編譜時要能拖到歌尾。
            double clipMs = (_audio != null && _audio.clip != null) ? _audio.clip.length * 1000.0 : 0.0;
            EditorEndMs = Math.Max(_totalMs, clipMs + MusicCountInSec * 1000.0);

            _started = true;
            EditorSetPaused(true);
            EditorSeekMs(0.0);
        }

        // 編輯器不需要 HP 條/分數/名次/歌曲資訊列 —— 只留音符板、受擊線、音符。
        private void HideHudForEditor()
        {
            if (_hpSolidBack) _hpSolidBack.enabled = false;
            if (_hpBg) _hpBg.enabled = false;
            if (_hpTex) _hpTex.enabled = false;
            if (_hpBackFrame) _hpBackFrame.enabled = false;
            if (_hpGlow) _hpGlow.enabled = false;
            if (_missOverlay) _missOverlay.enabled = false;
            HideComboAndJudge();
            HideHudForPanel();   // 分數、名次/名單、歌曲資訊列、頭上名牌
        }

        // ---------- transport ----------

        /// <summary>暫停/繼續（音樂跟著停；暫停中 Time.timeScale = 0 → 譜面時鐘自然凍結）。</summary>
        public void EditorSetPaused(bool paused)
        {
            if (!editorMode || paused == _paused) return;
            if (paused)
            {
                _pauseChartSec = _nowMs / 1000.0;
                if (_audio != null) _audio.Pause();
                Time.timeScale = 0f;
                ResetScheduledTicks();
                _paused = true;
            }
            else
            {
                _paused = false;
                Time.timeScale = _timeScale;
                EditorSeekMs(_pauseChartSec * 1000.0);   // 從暫停的位置重新起播（音源是 Stop 過的，不能只 UnPause）
            }
        }

        /// <summary>跳到譜面時間（毫秒）。暫停中也可用（畫面會停在該處，按播放就從那裡開始）。</summary>
        public void EditorSeekMs(double chartMs)
        {
            if (!editorMode || !_started) return;
            chartMs = Math.Max(0.0, Math.Min(chartMs, Math.Max(0.0, EditorEndMs)));
            double chartSec = chartMs / 1000.0;

            // 四個錨點一起搬（少一個就對不上）：dsp 錨點、wall 基準、音源位置、平滑時鐘。
            double dspNow = AudioSettings.dspTime;
            _songStartDspTime = GameRate.AnchorForChartSeconds(dspNow, chartSec, _musicRate, MusicCountInSec);
            _clockStart = Time.timeAsDouble - chartSec;   // 暫停時 timeAsDouble 是凍結的 → 時鐘就停在 chartSec
            _pauseChartSec = chartSec;
            _clock.Reset();
            _nowMs = chartMs;

            if (_audio != null && _audio.clip != null)
            {
                _audio.Stop();
                double clipSec = chartSec - MusicCountInSec;   // 音樂晚 count-in 秒才進來（含單首 offset）
                if (clipSec < 0.0)
                {
                    // 還在無聲數拍裡：把音樂排在「譜面時間 = count-in」的那個 dsp 時刻（即錨點本身）。
                    _audio.timeSamples = 0;
                    if (!_paused) _audio.PlayScheduled(_songStartDspTime);
                }
                else if (clipSec < _audio.clip.length)
                {
                    _audio.time = (float)clipSec;
                    if (!_paused) _audio.Play();
                }
            }

            // 倒帶回去時音符要能重新出現：編輯器不判定，所以只有捲出畫面的 Done 需要清掉。
            foreach (var n in _notes) { n.Done = false; n.HeadJudged = false; n.BundledFail = false; }
            for (int i = 0; i < Keys; i++) _holding[i] = null;

            _tick.Rewind(chartMs);   // F7 打拍音：游標跟著跳，不補播過去的
            ResetScheduledTicks();
        }

        public void EditorSeekBy(double deltaMs) => EditorSeekMs(_nowMs + deltaMs);

        /// <summary>流速（音樂+音符一起變速）。暫停中改也安全：改完立刻用目前位置重新錨定。</summary>
        public void EditorSetRate(double rate)
        {
            if (!editorMode) return;
            double at = _nowMs;
            bool wasPaused = _paused;
            if (wasPaused) { _paused = false; Time.timeScale = 1f; }   // SetGameRate 會依 dsp 重算「現在的譜面時間」，暫停中那個值是錯的
            SetGameRate(rate);
            if (wasPaused) { _paused = true; Time.timeScale = 0f; }
            EditorSeekMs(at);                                          // 用剛才的位置重新錨定四件事
        }

        // 每幀（Update 在 editorMode 走這條，取代判定/扣血/結算）：跑到底就自己停下來。
        private void EditorTick(double now)
        {
            if (!_paused && EditorEndMs > 0.0 && now > EditorEndMs) EditorSetPaused(true);
        }
    }
}
