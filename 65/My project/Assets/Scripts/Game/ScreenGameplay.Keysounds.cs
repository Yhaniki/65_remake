using System;
using System.Collections;
using System.Collections.Generic;
using Sdo.Osu;
using Sdo.Ruleset;
using UnityEngine;

namespace Sdo.Game
{
    public sealed partial class ScreenGameplay
    {
        private const int InitialOsuEventVoices = 24;
        // 純 keysound 譜(AudioFilename: virtual)沒有背景音樂檔:整首曲子就是幾百顆取樣疊出來的,起手就會
        // 用掉幾十個音源。先配好省得在密集段一顆一顆 AddComponent(實測峰值 170,見 KeysoundVoicePool)。
        private const int InitialVirtualOsuEventVoices = 64;
        private const int MaxOsuEventVoices = KeysoundVoicePool.MaxVoices;
        private const double OsuEventLookaheadMs = 500.0;
        private const double OsuEventLateCutoffMs = 100.0;

        private OsuKeysoundBank _osuKeysounds;
        private readonly List<AudioSource> _osuEventVoices = new List<AudioSource>();
        private readonly List<double> _osuEventStartsAt = new List<double>();
        private readonly List<double> _osuEventBusyUntil = new List<double>();
        private readonly List<int> _osuEventPausedSamples = new List<int>();
        // 每個音源目前掛著的發聲優先度。AudioSource.priority 是固定值、不會自己隨衰減下降,所以每幀重算
        // (UpdateOsuVoicePriorities);記在這裡是為了只在**跨桶**時才寫 setter(那是 native call)。
        private readonly List<int> _osuEventPriorities = new List<int>();
        private readonly List<int> _osuAutoNoteOwners = new List<int>();
        private readonly HashSet<int> _osuScheduledAutoNotes = new HashSet<int>();
        private int _osuEventIndex;
        private int _osuAutoNoteIndex;
        private double _lastOsuEventNowMs = double.NegativeInfinity;
        private bool _lastOsuAutoNoteScheduling;
        // The DSP schedule is only meaningful once _songStartDspTime has been anchored (OpeningSequence's
        // ScheduleMusic, or an editor seek). Before that it is still 0, which would map chart time straight onto
        // AudioSettings.dspTime — see TickOsuSampleEvents.
        private bool _osuTransportAnchored;
        private bool _resumeOsuSamplesOnNextEditorSeek;
        private bool _preserveOsuSamplesOnNextEditorSeek;
        private double _osuTimelineEndMs;

        private bool IsVirtualOsuTrack =>
            chartFormat == (int)SongFormat.Osu && _map != null &&
            OsuBeatmapParser.IsVirtualAudioFilename(_map.AudioFilename);

        private float OsuSamplePitch =>
            _map != null && _map.SamplesMatchPlaybackRate ? _timeScale : 1f;

        /// <summary>keysound 走哪一條音量軌。純 keysound 譜(AudioFilename: virtual)的取樣**就是音樂本身**,
        /// 要吃「音樂」滑桿 —— 走音效的話玩家把音效關小整首歌就消失了(選歌試聽的
        /// <see cref="OsuKeysoundPreviewPlayer"/> 一直都是走音樂)。一般 osu 圖的 keysound 是疊在音樂上的
        /// 打擊音,維持「音效」。</summary>
        private float OsuSampleMixGain => IsVirtualOsuTrack ? AudioMix.Music : AudioMix.Sfx;

        private bool ShouldScheduleOsuAutoNotes =>
            (editorMode && !beatTestMode) ||
            (!editorMode && (_showtime.Active || (autoPlay && forcedJudge != (int)Judgment.Miss)));

        private void BuildOsuKeysoundAudio()
        {
            if (chartFormat != (int)SongFormat.Osu || _map == null ||
                OsuKeysoundBank.ReferencedFilenames(_map).Count == 0) return;
            _osuKeysounds = new OsuKeysoundBank();
            int initial = IsVirtualOsuTrack ? InitialVirtualOsuEventVoices : InitialOsuEventVoices;
            for (int i = 0; i < initial; i++)
                AddOsuEventVoice();
        }

        private AudioSource NewOsuSampleSource()
        {
            var source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 0f;
            source.pitch = OsuSamplePitch;
            source.priority = KeysoundVoicePool.PriorityFresh;
            return source;
        }

        private void AddOsuEventVoice()
        {
            _osuEventVoices.Add(NewOsuSampleSource());
            _osuEventStartsAt.Add(0.0);
            _osuEventBusyUntil.Add(0.0);
            _osuEventPausedSamples.Add(-1);
            _osuEventPriorities.Add(KeysoundVoicePool.PriorityFresh);
            _osuAutoNoteOwners.Add(-1);
        }

        private IEnumerator LoadOsuKeysoundsCo()
        {
            if (chartFormat != (int)SongFormat.Osu || _map == null || _osuKeysounds == null) yield break;
            yield return _osuKeysounds.Load(_map, chartPath);
            _osuEventIndex = 0;
            _osuAutoNoteIndex = 0;
            _lastOsuEventNowMs = double.NegativeInfinity;
            _lastOsuAutoNoteScheduling = ShouldScheduleOsuAutoNotes;
            RefreshOsuTimelineEnd();
            Debug.Log($"[keysound] loaded {_osuKeysounds.LoadedCount}/{_osuKeysounds.ReferencedCount} samples"
                    + (_osuKeysounds.MissingCount > 0 ? $" ({_osuKeysounds.MissingCount} missing/undecodable)" : ""));
        }

        private void RefreshOsuTimelineEnd(double remainingFromChartMs = double.NegativeInfinity)
        {
            _osuTimelineEndMs = 0.0;
            if (_map == null || _osuKeysounds == null) return;
            bool remainingOnly = !double.IsNegativeInfinity(remainingFromChartMs);
            double chartDurationScale = _map.SamplesMatchPlaybackRate ? 1.0 : _musicRate;
            foreach (var sample in _map.SampleEvents)
            {
                if (remainingOnly && sample.TimeMs < remainingFromChartMs) continue;
                if (_osuKeysounds.TryGet(sample.Filename, out var clip))
                    _osuTimelineEndMs = Math.Max(_osuTimelineEndMs,
                        sample.TimeMs + clip.length * 1000.0 * chartDurationScale);
            }
            foreach (var note in _map.HitObjects)
            {
                if (remainingOnly && note.StartTimeMs < remainingFromChartMs) continue;
                if (!note.IsBomb && !note.IsFake &&
                    _osuKeysounds.TryGet(note.CustomSampleFilename, out var clip))
                    _osuTimelineEndMs = Math.Max(_osuTimelineEndMs,
                        note.StartTimeMs + clip.length * 1000.0 * chartDurationScale);
            }

            if (!remainingOnly) return;
            double dspNow = AudioSettings.dspTime;
            for (int i = 0; i < _osuEventVoices.Count; i++)
            {
                int pausedPosition = _osuEventPausedSamples[i];
                bool pausedVoice = pausedPosition >= 0;
                if (!pausedVoice &&
                    (_osuEventStartsAt[i] > dspNow + 0.001 || _osuEventBusyUntil[i] <= dspNow))
                    continue;

                var voice = _osuEventVoices[i];
                var clip = voice != null ? voice.clip : null;
                if (clip == null || clip.samples <= 0 || clip.frequency <= 0) continue;
                int position = pausedVoice ? pausedPosition : voice.timeSamples;
                position = Math.Max(0, Math.Min(clip.samples, position));
                double remainingSourceSec = (clip.samples - position) / (double)clip.frequency;
                double remainingDspSec = remainingSourceSec / Math.Max(0.01f, OsuSamplePitch);
                _osuTimelineEndMs = Math.Max(_osuTimelineEndMs,
                    remainingFromChartMs + remainingDspSec * _musicRate * 1000.0);
            }
        }

        private void PlayOsuHitSample(OsuHitObject note, Judgment judgment)
        {
            bool wasScheduled = ResolveScheduledAutoNote(
                note, judgment == Judgment.Miss && !editorMode);
            if (judgment == Judgment.Miss || wasScheduled ||
                _osuKeysounds == null ||
                !_osuKeysounds.TryGet(note.CustomSampleFilename, out var clip))
                return;

            int volume = note.CustomSampleVolume > 0
                ? note.CustomSampleVolume
                : (_map != null ? _map.SampleVolumeAt(note.StartTimeMs) : 100);
            PlayOsuSampleNow(clip, volume);
        }

        private bool ResolveScheduledAutoNote(OsuHitObject note, bool cancelIfFuture)
        {
            int noteIndex = -1;
            foreach (int candidate in _osuScheduledAutoNotes)
            {
                if (candidate < 0 || _map == null || candidate >= _map.HitObjects.Count ||
                    !_map.HitObjects[candidate].Equals(note))
                    continue;
                noteIndex = candidate;
                break;
            }
            if (noteIndex < 0) return false;

            _osuScheduledAutoNotes.Remove(noteIndex);
            double dspNow = AudioSettings.dspTime;
            for (int i = 0; i < _osuAutoNoteOwners.Count; i++)
            {
                if (_osuAutoNoteOwners[i] != noteIndex) continue;
                if (cancelIfFuture && _osuEventStartsAt[i] > dspNow + 0.001)
                    CancelOsuVoice(i, removeScheduledNote: false);
                else
                    _osuAutoNoteOwners[i] = -1;
            }
            return true;
        }

        private void PlayOsuSampleNow(AudioClip clip, int volume)
        {
            if (clip == null || clip.samples <= 0 || clip.frequency <= 0) return;
            double dspNow = AudioSettings.dspTime;
            int voiceIndex = PickOsuEventVoice(dspNow);
            if (voiceIndex < 0) return;
            StartOsuVoice(voiceIndex, clip, volume, dspNow, 0.0, -1, scheduled: false);
        }

        /// <summary>
        /// Schedule storyboard Sample events and automatic note samples in one chronological DSP queue. Duplicate
        /// event rows remain separate voices; note samples are marked so their later judgment frame cannot replay them.
        /// </summary>
        private void TickOsuSampleEvents(double nowMs)
        {
            if (_map == null || _osuKeysounds == null) return;
            // Gameplay flips _started before the READY/GO opening runs, but the music transport is only anchored
            // when GO finishes (ScheduleMusic -> OnOsuTransportStarted). Scheduling against the unset anchor makes
            // chart time equal AudioSettings.dspTime, so the map plays from wherever that clock happens to sit —
            // a keysound-only ("virtual") song then audibly plays its middle throughout the opening and is cut off
            // the instant the real transport starts.
            if (!_osuTransportAnchored) return;
            if (_ended)
            {
                ResetOsuSamplePlayback(double.MaxValue);
                _lastOsuEventNowMs = double.MaxValue;
                return;
            }
            if (_paused || Time.timeScale <= 0f)
                return;

            UpdateOsuVoicePriorities(AudioSettings.dspTime);

            bool scheduleAutoNotes = ShouldScheduleOsuAutoNotes;
            if (scheduleAutoNotes != _lastOsuAutoNoteScheduling)
                ChangeOsuAutoNoteScheduling(scheduleAutoNotes, nowMs);

            double schedulingNowMs = OsuDspChartMs();
            if (!double.IsNegativeInfinity(_lastOsuEventNowMs) &&
                (schedulingNowMs < _lastOsuEventNowMs - 1.0 ||
                 Math.Abs(schedulingNowMs - _lastOsuEventNowMs) > 1000.0))
                ResetOsuSamplePlayback(schedulingNowMs);
            _lastOsuEventNowMs = schedulingNowMs;
            _lastOsuAutoNoteScheduling = scheduleAutoNotes;

            bool persistentAuto = autoPlay && forcedJudge != (int)Judgment.Miss;
            double showtimeAutoEnd = _showtime.Active && !persistentAuto
                ? _showtime.UntilMs
                : double.PositiveInfinity;
            double horizon = schedulingNowMs + OsuEventLookaheadMs;
            while (true)
            {
                while (scheduleAutoNotes && _osuAutoNoteIndex < _map.HitObjects.Count)
                {
                    if (IsOsuAutoNoteEligible(_osuAutoNoteIndex))
                        break;
                    _osuAutoNoteIndex++;
                }

                double eventTime = _osuEventIndex < _map.SampleEvents.Count
                    ? _map.SampleEvents[_osuEventIndex].TimeMs
                    : double.PositiveInfinity;
                double noteTime = scheduleAutoNotes && _osuAutoNoteIndex < _map.HitObjects.Count
                    ? _map.HitObjects[_osuAutoNoteIndex].StartTimeMs
                    : double.PositiveInfinity;
                if (noteTime >= showtimeAutoEnd) noteTime = double.PositiveInfinity;
                if (Math.Min(eventTime, noteTime) > horizon) break;

                OsuScheduleResult result;
                if (eventTime <= noteTime)
                {
                    var sample = _map.SampleEvents[_osuEventIndex];
                    result = ScheduleOsuSample(sample.TimeMs, sample.Filename, sample.Volume, -1);
                    if (result == OsuScheduleResult.NoVoice) break;
                    _osuEventIndex++;
                }
                else
                {
                    var note = _map.HitObjects[_osuAutoNoteIndex];
                    int volume = note.CustomSampleVolume > 0
                        ? note.CustomSampleVolume
                        : _map.SampleVolumeAt(note.StartTimeMs);
                    result = ScheduleOsuSample(
                        note.StartTimeMs, note.CustomSampleFilename, volume,
                        _osuAutoNoteIndex);
                    if (result == OsuScheduleResult.NoVoice) break;
                    _osuAutoNoteIndex++;
                }
            }
        }

        private bool IsOsuAutoNoteEligible(int mapIndex)
        {
            if (_map == null || mapIndex < 0 || mapIndex >= _map.HitObjects.Count) return false;
            var candidate = _map.HitObjects[mapIndex];
            if (candidate.IsBomb || candidate.IsFake ||
                string.IsNullOrWhiteSpace(candidate.CustomSampleFilename) ||
                _osuScheduledAutoNotes.Contains(mapIndex))
                return false;
            if (mapIndex < _notesByMapIndex.Count)
            {
                var runtime = _notesByMapIndex[mapIndex];
                if (runtime != null && (runtime.HeadJudged || runtime.Done)) return false;
            }
            return true;
        }

        private OsuScheduleResult ScheduleOsuSample(
            double timeMs, string filename, int volume, int autoNoteIndex)
        {
            if (!_osuKeysounds.TryGet(filename, out var clip) ||
                clip.samples <= 0 || clip.frequency <= 0)
                return OsuScheduleResult.Consumed;

            double targetDsp = GameRate.DspFromChartSeconds(
                timeMs / 1000.0, _songStartDspTime, _musicRate, MusicCountInSec);
            double dspNow = AudioSettings.dspTime;
            double lateDspSec = Math.Max(0.0, dspNow - targetDsp);
            double lateChartMs = lateDspSec * 1000.0 * _musicRate;
            if (lateChartMs >= OsuEventLateCutoffMs) return OsuScheduleResult.Consumed;

            float pitch = OsuSamplePitch;
            double sourceOffsetSec = lateDspSec * pitch;
            if (sourceOffsetSec >= clip.length) return OsuScheduleResult.Consumed;

            int voiceIndex = PickOsuEventVoice(dspNow);
            if (voiceIndex < 0) return OsuScheduleResult.NoVoice;
            StartOsuVoice(voiceIndex, clip, volume, targetDsp, sourceOffsetSec, autoNoteIndex,
                scheduled: targetDsp > dspNow + 0.001);
            if (autoNoteIndex >= 0) _osuScheduledAutoNotes.Add(autoNoteIndex);
            return OsuScheduleResult.Scheduled;
        }

        private void StartOsuVoice(int voiceIndex, AudioClip clip, int volume,
            double targetDsp, double sourceOffsetSec, int autoNoteIndex, bool scheduled)
        {
            var voice = _osuEventVoices[voiceIndex];
            voice.Stop();
            voice.clip = clip;
            voice.pitch = OsuSamplePitch;
            voice.volume = OsuSampleMixGain * Mathf.Clamp01(volume / 100f);
            voice.priority = KeysoundVoicePool.PriorityFresh;   // 剛起音 = 最高;UpdateOsuVoicePriorities 之後往下降
            _osuEventPriorities[voiceIndex] = KeysoundVoicePool.PriorityFresh;
            voice.timeSamples = Math.Min(clip.samples - 1,
                Math.Max(0, (int)Math.Round(sourceOffsetSec * clip.frequency)));

            double playDsp = scheduled ? targetDsp : AudioSettings.dspTime;
            if (scheduled) voice.PlayScheduled(targetDsp);
            else voice.Play();

            _osuEventStartsAt[voiceIndex] = playDsp;
            _osuEventBusyUntil[voiceIndex] =
                playDsp + Math.Max(0.0, clip.length - sourceOffsetSec) /
                Math.Max(0.01f, voice.pitch);
            _osuEventPausedSamples[voiceIndex] = -1;
            _osuAutoNoteOwners[voiceIndex] = autoNoteIndex;
        }

        private int PickOsuEventVoice(double dspNow)
        {
            int free = KeysoundVoicePool.FindFree(_osuEventBusyUntil, dspNow);
            if (free >= 0)
            {
                // The sound may already be in the output buffer while its note has not reached the visible judgment
                // line. Detach the voice for reuse but retain the scheduled-note marker until that judgment occurs.
                _osuAutoNoteOwners[free] = -1;
                return free;
            }
            if (_osuEventVoices.Count < MaxOsuEventVoices)
            {
                AddOsuEventVoice();
                return _osuEventVoices.Count - 1;
            }

            // 池滿了就偷最舊的那顆(voice stealing,見 KeysoundVoicePool)。舊版在這裡回 -1,排程迴圈 break
            // 且游標不前進 —— 等有空位時後面那些取樣早就過了 OsuEventLateCutoffMs 被整批丟掉,純 keysound 譜
            // 聽起來就是「玩到後面整段沒音樂」。被偷的音已經響過了,所以只解除擁有關係、不撤 _osuScheduledAutoNotes
            // 標記(同上面 free 的路徑),否則那顆音符的判定幀會再播一次。
            int steal = KeysoundVoicePool.FindStealable(_osuEventStartsAt, _osuEventPausedSamples, dspNow);
            if (steal < 0) return -1;
            _osuAutoNoteOwners[steal] = -1;
            return steal;
        }

        /// <summary>
        /// 每幀依「這顆音已經響了多久」重掛發聲優先度:AudioSource.priority 是固定值,不會自己隨衰減下降。
        /// 發聲數(Real Voices)真的吃滿時,先被虛擬化(靜音)的才會是快聽不見的鋼琴尾巴,而不是剛起音的音
        /// 或 F7 打拍音。只在跨桶時才寫 setter —— 那是 native call,幾百個音源每幀無條件寫太浪費。
        /// </summary>
        private void UpdateOsuVoicePriorities(double dspNow)
        {
            for (int i = 0; i < _osuEventVoices.Count; i++)
            {
                if (_osuEventBusyUntil[i] <= dspNow) continue;
                int want = KeysoundVoicePool.PriorityForAge(dspNow - _osuEventStartsAt[i]);
                if (_osuEventPriorities[i] == want) continue;
                _osuEventPriorities[i] = want;
                var voice = _osuEventVoices[i];
                if (voice != null) voice.priority = want;
            }
        }

        private void ChangeOsuAutoNoteScheduling(bool enabled, double nowMs)
        {
            if (enabled)
            {
                _osuAutoNoteIndex = FirstHitObjectAfter(nowMs - OsuEventLateCutoffMs);
            }
            else
            {
                double dspNow = AudioSettings.dspTime;
                for (int i = 0; i < _osuAutoNoteOwners.Count; i++)
                    if (_osuAutoNoteOwners[i] >= 0 && _osuEventStartsAt[i] > dspNow + 0.001)
                        CancelOsuVoice(i, removeScheduledNote: true);
            }
            _lastOsuAutoNoteScheduling = enabled;
        }

        private void OnOsuPlaybackPaused(double chartMs)
        {
            if (IsVirtualOsuTrack && !_osuTransportAnchored) return;
            double dspNow = AudioSettings.dspTime;
            for (int i = 0; i < _osuEventVoices.Count; i++)
            {
                if (_osuEventBusyUntil[i] <= dspNow)
                {
                    _osuAutoNoteOwners[i] = -1;
                    continue;
                }
                if (_osuEventStartsAt[i] > dspNow + 0.001)
                {
                    CancelOsuVoice(i, removeScheduledNote: true);
                    continue;
                }

                var voice = _osuEventVoices[i];
                int position = voice.clip != null ? voice.timeSamples : 0;
                voice.Pause();
                _osuEventPausedSamples[i] = Math.Max(0, position);
            }
            double schedulingNowMs = OsuDspChartMs();
            ResetOsuScheduleCursors(schedulingNowMs);
            _lastOsuEventNowMs = schedulingNowMs;
        }

        private void OnOsuPlaybackResumed(double chartMs, double resumeDsp)
        {
            if (IsVirtualOsuTrack && !_osuTransportAnchored) return;
            for (int i = 0; i < _osuEventVoices.Count; i++)
            {
                int position = _osuEventPausedSamples[i];
                if (position < 0) continue;
                var voice = _osuEventVoices[i];
                var clip = voice.clip;
                if (clip == null || clip.samples <= 0 || clip.frequency <= 0 || position >= clip.samples)
                {
                    CancelOsuVoice(i, removeScheduledNote: false);
                    continue;
                }

                voice.Stop();
                voice.pitch = OsuSamplePitch;
                voice.timeSamples = Math.Min(clip.samples - 1, position);
                voice.PlayScheduled(resumeDsp);
                _osuEventStartsAt[i] = resumeDsp;
                _osuEventBusyUntil[i] = resumeDsp +
                    (clip.samples - position) / (double)clip.frequency /
                    Math.Max(0.01f, voice.pitch);
                _osuEventPausedSamples[i] = -1;
            }
            _lastOsuEventNowMs = OsuDspChartMs();
        }

        private void OnOsuPlaybackRateChanged(double chartMs)
        {
            if (_preserveOsuSamplesOnNextEditorSeek && editorMode && _paused)
                chartMs -= _globalOffsetMs;
            if (IsVirtualOsuTrack && !_osuTransportAnchored)
            {
                RefreshOsuTimelineEnd();
                return;
            }

            double dspNow = AudioSettings.dspTime;
            for (int i = 0; i < _osuEventVoices.Count; i++)
            {
                if (_osuEventPausedSamples[i] >= 0)
                {
                    _osuEventVoices[i].pitch = OsuSamplePitch;
                    continue;
                }
                if (_osuEventStartsAt[i] > dspNow + 0.001)
                {
                    CancelOsuVoice(i, removeScheduledNote: true);
                    continue;
                }
                if (_osuEventBusyUntil[i] <= dspNow) continue;

                var voice = _osuEventVoices[i];
                voice.pitch = OsuSamplePitch;
                if (voice.clip != null && voice.clip.frequency > 0)
                    _osuEventBusyUntil[i] = dspNow +
                        Math.Max(0, voice.clip.samples - voice.timeSamples) /
                        (double)voice.clip.frequency / Math.Max(0.01f, voice.pitch);
            }
            RefreshOsuTimelineEnd(chartMs);
            if (editorMode) RefreshEditorTimelineEnd();
            if (!_preserveOsuSamplesOnNextEditorSeek)
                ResetOsuScheduleCursors(chartMs);
            _lastOsuEventNowMs = OsuDspChartMs();
        }

        private void OnOsuPlaybackSeek(double chartMs)
        {
            _osuTransportAnchored = true;
            ResetOsuSamplePlayback(chartMs);
            _lastOsuEventNowMs = OsuDspChartMs();
        }

        private void OnOsuPlaybackReanchored(double chartMs)
        {
            _osuTransportAnchored = true;
            double dspNow = AudioSettings.dspTime;
            for (int i = 0; i < _osuEventVoices.Count; i++)
                if (_osuEventPausedSamples[i] < 0 && _osuEventStartsAt[i] > dspNow + 0.001)
                    CancelOsuVoice(i, removeScheduledNote: true);
            _lastOsuEventNowMs = OsuDspChartMs();
        }

        private void OnOsuTransportStarted()
        {
            _osuTransportAnchored = true;
            double chartMs = GameRate.ChartSecondsFromDsp(
                AudioSettings.dspTime, _songStartDspTime, _musicRate, MusicCountInSec) * 1000.0;
            ResetOsuSamplePlayback(chartMs);
            _lastOsuEventNowMs = OsuDspChartMs();
        }

        private void ResetOsuSamplePlayback(double skipThroughMs)
        {
            for (int i = 0; i < _osuEventVoices.Count; i++)
                CancelOsuVoice(i, removeScheduledNote: false);
            _osuScheduledAutoNotes.Clear();
            ResetOsuScheduleCursors(skipThroughMs);
            _lastOsuAutoNoteScheduling = ShouldScheduleOsuAutoNotes;
        }

        private void ResetOsuScheduleCursors(double skipThroughMs)
        {
            int lo = 0, hi = _map != null ? _map.SampleEvents.Count : 0;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (_map.SampleEvents[mid].TimeMs <= skipThroughMs) lo = mid + 1;
                else hi = mid;
            }
            _osuEventIndex = lo;
            _osuAutoNoteIndex = FirstHitObjectAfter(skipThroughMs);
        }

        private int FirstHitObjectAfter(double timeMs)
        {
            int lo = 0, hi = _map != null ? _map.HitObjects.Count : 0;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (_map.HitObjects[mid].StartTimeMs <= timeMs) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        private void CancelOsuVoice(int index, bool removeScheduledNote)
        {
            int owner = _osuAutoNoteOwners[index];
            if (removeScheduledNote && owner >= 0) _osuScheduledAutoNotes.Remove(owner);
            _osuAutoNoteOwners[index] = -1;
            if (_osuEventVoices[index] != null) _osuEventVoices[index].Stop();
            _osuEventStartsAt[index] = 0.0;
            _osuEventBusyUntil[index] = 0.0;
            _osuEventPausedSamples[index] = -1;
        }

        private double OsuDspChartMs() =>
            GameRate.ChartSecondsFromDsp(
                AudioSettings.dspTime, _songStartDspTime, _musicRate, MusicCountInSec) * 1000.0;

        private double? VirtualOsuChartSeconds()
        {
            if (!IsVirtualOsuTrack || !_osuTransportAnchored || _paused || _ended) return null;
            double dsp = AudioSettings.dspTime;
            double chartStartDsp = GameRate.DspFromChartSeconds(
                0.0, _songStartDspTime, _musicRate, MusicCountInSec);
            if (dsp < chartStartDsp) return null;
            return GameRate.ChartSecondsFromDsp(
                dsp, _songStartDspTime, _musicRate, MusicCountInSec);
        }

        private void DisposeOsuKeysounds()
        {
            ResetOsuSamplePlayback(double.MaxValue);
            _osuKeysounds?.Dispose();
            _osuKeysounds = null;
            _osuTransportAnchored = false;
            _osuTimelineEndMs = 0.0;
        }

        private enum OsuScheduleResult
        {
            Consumed,
            Scheduled,
            NoVoice
        }
    }
}
