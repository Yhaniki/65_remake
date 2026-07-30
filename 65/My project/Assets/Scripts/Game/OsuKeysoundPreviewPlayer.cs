using System;
using System.Collections;
using System.Collections.Generic;
using Sdo.Osu;
using UnityEngine;

namespace Sdo.Game
{
    public readonly struct OsuKeysoundPreviewTrigger
    {
        public double TimeMs { get; }
        public string Filename { get; }
        public int Volume { get; }

        public OsuKeysoundPreviewTrigger(double timeMs, string filename, int volume)
        {
            TimeMs = timeMs;
            Filename = filename ?? "";
            Volume = Math.Max(0, Math.Min(100, volume));
        }
    }

    /// <summary>
    /// Song-select transport for osu! maps whose backing track is the reserved "virtual" filename.
    /// It automatically schedules storyboard samples and every playable note-head sample in one DSP timeline.
    /// </summary>
    public sealed class OsuKeysoundPreviewPlayer : IDisposable
    {
        public const double DefaultWindowMs = 20000.0;
        private const double PreviewTailMs = 1000.0;
        private const double ScheduleLeadSec = 0.05;
        private const double LookaheadMs = 500.0;
        private const double LateCutoffMs = 100.0;
        private const int MaxOccurrencesPerTick = 8192;
        private const int MaxVoices = 128;

        private readonly GameObject _host;
        private readonly string _chartPath;
        private readonly List<OsuKeysoundPreviewTrigger> _triggers;
        private readonly OsuKeysoundBank _bank = new OsuKeysoundBank();
        private readonly List<AudioSource> _voices = new List<AudioSource>();
        private readonly List<double> _busyUntil = new List<double>();
        private readonly List<long> _voiceCycles = new List<long>();
        private int _cursor;
        private long _cycle;
        private double _transportStartDsp;
        private bool _playing;
        private bool _disposed;

        public double WindowStartMs { get; }
        public double WindowEndMs { get; }
        public int ScheduleCount => _triggers.Count;
        public int ReferencedCount => _bank.ReferencedCount;
        public int LoadedCount => _bank.LoadedCount;
        public int MissingCount => _bank.MissingCount;
        public bool IsPlaying => _playing && !_disposed;

        public OsuKeysoundPreviewPlayer(GameObject host, OsuBeatmap map, string chartPath,
            double requestedStartMs, double requestedLengthMs)
        {
            _host = host;
            _chartPath = chartPath ?? "";
            ResolveWindow(map, requestedStartMs, requestedLengthMs,
                out double startMs, out double endMs);
            WindowStartMs = startMs;
            WindowEndMs = endMs;
            _triggers = BuildTriggers(map, startMs, endMs);
        }

        public IEnumerator Load()
        {
            var filenames = new List<string>(_triggers.Count);
            for (int i = 0; i < _triggers.Count; i++)
                filenames.Add(_triggers[i].Filename);
            return _bank.LoadFilenames(filenames, _chartPath);
        }

        public bool Play()
        {
            if (_disposed || _host == null || _triggers.Count == 0 || _bank.LoadedCount == 0)
                return false;
            Restart(AudioSettings.dspTime + ScheduleLeadSec);
            Tick();
            return true;
        }

        public void Tick()
        {
            if (!IsPlaying) return;
            ScheduleThrough(AudioSettings.dspTime);
        }

        public void Stop()
        {
            _playing = false;
            _cursor = 0;
            _cycle = 0;
            StopVoices();
        }

        public void Dispose()
        {
            if (_disposed) return;
            Stop();
            _disposed = true;
            _bank.Dispose();
            for (int i = 0; i < _voices.Count; i++)
            {
                var voice = _voices[i];
                if (voice == null) continue;
                if (Application.isPlaying) UnityEngine.Object.Destroy(voice);
                else UnityEngine.Object.DestroyImmediate(voice);
            }
            _voices.Clear();
            _busyUntil.Clear();
            _voiceCycles.Clear();
        }

        public static List<OsuKeysoundPreviewTrigger> BuildTriggers(
            OsuBeatmap map, double startMs, double endMs)
        {
            var result = new List<OsuKeysoundPreviewTrigger>();
            if (map == null || endMs <= startMs) return result;

            for (int i = 0; i < map.SampleEvents.Count; i++)
            {
                var sample = map.SampleEvents[i];
                if (sample.TimeMs < startMs || sample.TimeMs >= endMs ||
                    string.IsNullOrWhiteSpace(sample.Filename))
                    continue;
                result.Add(new OsuKeysoundPreviewTrigger(
                    sample.TimeMs, sample.Filename, sample.Volume));
            }

            for (int i = 0; i < map.HitObjects.Count; i++)
            {
                var note = map.HitObjects[i];
                if (note.StartTimeMs < startMs || note.StartTimeMs >= endMs ||
                    note.IsBomb || note.IsFake ||
                    string.IsNullOrWhiteSpace(note.CustomSampleFilename))
                    continue;
                int volume = note.CustomSampleVolume > 0
                    ? note.CustomSampleVolume
                    : map.SampleVolumeAt(note.StartTimeMs);
                result.Add(new OsuKeysoundPreviewTrigger(
                    note.StartTimeMs, note.CustomSampleFilename, volume));
            }

            result.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
            return result;
        }

        public static void ResolveWindow(OsuBeatmap map,
            double requestedStartMs, double requestedLengthMs,
            out double startMs, out double endMs)
        {
            double timelineEndMs = LastTriggerTimeMs(map);
            if (timelineEndMs > 0.0) timelineEndMs += PreviewTailMs;
            else timelineEndMs = PreviewTailMs;

            double lengthMs = requestedLengthMs > 0.0
                ? requestedLengthMs
                : DefaultWindowMs;
            lengthMs = Math.Max(1.0, lengthMs);

            double normalizedStartMs = requestedStartMs > 0.0
                ? requestedStartMs
                : -1.0;
            startMs = SongPreviewWindow.ResolveStart(
                normalizedStartMs, timelineEndMs, lengthMs,
                SongPreviewWindow.OsuAutomaticStartRatio);
            endMs = startMs + lengthMs;
        }

        public static double OccurrenceDspTime(double anchorDsp,
            double windowStartMs, double windowEndMs,
            double triggerTimeMs, long cycle)
        {
            double loopSec = (windowEndMs - windowStartMs) / 1000.0;
            if (loopSec <= 0.0) throw new ArgumentOutOfRangeException(nameof(windowEndMs));
            if (cycle < 0) throw new ArgumentOutOfRangeException(nameof(cycle));
            return anchorDsp + cycle * loopSec +
                (triggerTimeMs - windowStartMs) / 1000.0;
        }

        public static double CycleEndDspTime(double anchorDsp,
            double windowStartMs, double windowEndMs, long cycle)
        {
            double loopSec = (windowEndMs - windowStartMs) / 1000.0;
            if (loopSec <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(windowEndMs));
            if (cycle < 0)
                throw new ArgumentOutOfRangeException(nameof(cycle));
            return anchorDsp + (cycle + 1) * loopSec;
        }

        private static double LastTriggerTimeMs(OsuBeatmap map)
        {
            if (map == null) return 0.0;
            double last = 0.0;
            for (int i = 0; i < map.SampleEvents.Count; i++)
            {
                var sample = map.SampleEvents[i];
                if (!string.IsNullOrWhiteSpace(sample.Filename))
                    last = Math.Max(last, sample.TimeMs);
            }
            for (int i = 0; i < map.HitObjects.Count; i++)
            {
                var note = map.HitObjects[i];
                if (!note.IsBomb && !note.IsFake &&
                    !string.IsNullOrWhiteSpace(note.CustomSampleFilename))
                    last = Math.Max(last, note.StartTimeMs);
            }
            return last;
        }

        private void Restart(double startDsp)
        {
            StopVoices();
            _cursor = 0;
            _cycle = 0;
            _transportStartDsp = startDsp;
            _playing = true;
        }

        private void ScheduleThrough(double dspNow)
        {
            double loopSec = (WindowEndMs - WindowStartMs) / 1000.0;
            if (loopSec <= 0.0 || _triggers.Count == 0) return;
            long physicalCycle = dspNow <= _transportStartDsp
                ? 0
                : (long)Math.Floor((dspNow - _transportStartDsp) / loopSec);
            StopExpiredCycleVoices(physicalCycle);

            // After a long frame stall, discard whole loops whose occurrences are already too late.
            // Keep at most the immediately preceding loop so a trigger near its end can still resume.
            double expiredBoundary = dspNow - LateCutoffMs / 1000.0;
            if (expiredBoundary > _transportStartDsp)
            {
                long liveCycle = (long)Math.Floor(
                    (expiredBoundary - _transportStartDsp) / loopSec);
                if (liveCycle > _cycle)
                {
                    _cycle = liveCycle;
                    _cursor = 0;
                }
            }

            double horizonDsp = dspNow + LookaheadMs / 1000.0;

            // Keep one continuous DSP anchor and queue the next loop ahead of time, but cap every voice at
            // its own cycle boundary. A long keysound from the previous preview must never bleed into the next loop.
            for (int processed = 0; processed < MaxOccurrencesPerTick; processed++)
            {
                var trigger = _triggers[_cursor];
                long occurrenceCycle = _cycle;
                double targetDsp = OccurrenceDspTime(
                    _transportStartDsp, WindowStartMs, WindowEndMs,
                    trigger.TimeMs, occurrenceCycle);
                if (targetDsp > horizonDsp) break;
                double cycleEndDsp = CycleEndDspTime(
                    _transportStartDsp, WindowStartMs, WindowEndMs, occurrenceCycle);
                if (dspNow >= cycleEndDsp)
                {
                    AdvanceCursor();
                    continue;
                }

                if (!_bank.TryGet(trigger.Filename, out var clip) ||
                    clip == null || clip.samples <= 0 || clip.frequency <= 0)
                {
                    AdvanceCursor();
                    continue;
                }

                double lateSec = Math.Max(0.0, dspNow - targetDsp);
                if (lateSec * 1000.0 >= LateCutoffMs || lateSec >= clip.length)
                {
                    AdvanceCursor();
                    continue;
                }

                int voiceIndex = PickVoice(dspNow);
                if (voiceIndex < 0) break;
                StartVoice(voiceIndex, clip, trigger.Volume,
                    targetDsp, cycleEndDsp, occurrenceCycle, lateSec, dspNow);
                AdvanceCursor();
            }
        }

        private void AdvanceCursor()
        {
            _cursor++;
            if (_cursor < _triggers.Count) return;
            _cursor = 0;
            _cycle++;
        }

        private int PickVoice(double dspNow)
        {
            for (int i = 0; i < _voices.Count; i++)
                if (_busyUntil[i] <= dspNow)
                    return i;
            if (_voices.Count >= MaxVoices || _host == null) return -1;

            var source = _host.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 0f;
            _voices.Add(source);
            _busyUntil.Add(0.0);
            _voiceCycles.Add(-1);
            return _voices.Count - 1;
        }

        private void StartVoice(int index, AudioClip clip, int volume,
            double targetDsp, double cycleEndDsp, long cycle,
            double sourceOffsetSec, double dspNow)
        {
            var voice = _voices[index];
            voice.Stop();
            voice.clip = clip;
            voice.pitch = 1f;
            voice.volume = AudioMix.Music * Mathf.Clamp01(volume / 100f);
            voice.timeSamples = Math.Min(clip.samples - 1,
                Math.Max(0, (int)Math.Round(sourceOffsetSec * clip.frequency)));

            bool scheduled = targetDsp > dspNow + 0.001;
            double playDsp = scheduled ? targetDsp : dspNow;
            if (scheduled) voice.PlayScheduled(targetDsp);
            else voice.Play();
            double naturalEndDsp =
                playDsp + Math.Max(0.0, clip.length - sourceOffsetSec);
            if (naturalEndDsp > cycleEndDsp) voice.SetScheduledEndTime(cycleEndDsp);
            _busyUntil[index] = Math.Min(naturalEndDsp, cycleEndDsp);
            _voiceCycles[index] = cycle;
        }

        private void StopExpiredCycleVoices(long physicalCycle)
        {
            if (physicalCycle <= 0) return;
            for (int i = 0; i < _voices.Count; i++)
            {
                long voiceCycle = _voiceCycles[i];
                if (voiceCycle < 0 || voiceCycle >= physicalCycle) continue;
                var voice = _voices[i];
                if (voice != null)
                {
                    voice.Stop();
                    voice.clip = null;
                }
                _busyUntil[i] = 0.0;
                _voiceCycles[i] = -1;
            }
        }

        private void StopVoices()
        {
            for (int i = 0; i < _voices.Count; i++)
            {
                if (_voices[i] != null)
                {
                    _voices[i].Stop();
                    _voices[i].clip = null;
                }
                _busyUntil[i] = 0.0;
                _voiceCycles[i] = -1;
            }
        }
    }
}
