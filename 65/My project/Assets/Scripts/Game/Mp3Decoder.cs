using System;
using NLayer;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>Decoded interleaved-float PCM (thread-safe POCO — no Unity types, so decoding can run off the main thread).</summary>
    public sealed class Mp3Pcm
    {
        public float[] Samples;   // interleaved [-1,1]
        public int Channels;
        public int SampleRate;
    }

    /// <summary>
    /// Runtime .mp3 → AudioClip decoder using the bundled NLayer managed decoder (Assets/Plugins/NLayer). Unity's
    /// UnityWebRequestMultimedia can only decode ogg/wav on desktop/editor (mp3 only on mobile), but external
    /// osu/StepMania song audio is usually mp3 — this makes it play everywhere. Decoding is CPU-only (no Unity API),
    /// so <see cref="Decode"/> is safe on a background thread; <see cref="ToClip"/> (AudioClip.Create) must run on the
    /// main thread.
    /// </summary>
    public static class Mp3Decoder
    {
        public static bool IsMp3(string path)
            => !string.IsNullOrEmpty(path) && path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);

        /// <summary>Decode an .mp3 file to interleaved PCM. Returns null on failure. Background-thread safe.</summary>
        public static Mp3Pcm Decode(string path)
        {
            try
            {
                using (var mp3 = new MpegFile(path))
                {
                    int ch = mp3.Channels > 0 ? mp3.Channels : 2;
                    int sr = mp3.SampleRate > 0 ? mp3.SampleRate : 44100;
                    var data = new float[1 << 20];
                    int len = 0;
                    var buf = new float[16384];
                    int n;
                    while ((n = mp3.ReadSamples(buf, 0, buf.Length)) > 0)
                    {
                        if (len + n > data.Length) Array.Resize(ref data, Math.Max(data.Length * 2, len + n));
                        for (int i = 0; i < n; i++)
                        {
                            float s = buf[i];
                            data[len++] = s > 1f ? 1f : (s < -1f ? -1f : s);   // NLayer can overshoot ±1 slightly → clamp
                        }
                    }
                    if (len == 0) return null;
                    if (len != data.Length) Array.Resize(ref data, len);
                    return new Mp3Pcm { Samples = data, Channels = ch, SampleRate = sr };
                }
            }
            catch { return null; }   // caller logs; keep this Unity-free so it can run on a worker thread
        }

        /// <summary>
        /// Decode ONLY a window of an mp3 (for song-select previews) instead of the whole song — NLayer seeks
        /// accurately in ~15 ms, so this is ~10× faster than a full decode (≈120 ms vs ≈1.4 s) and starts the preview
        /// almost immediately. <paramref name="startSec"/> &lt; 0 (or past the end) → centre the window. Background-thread safe.
        /// </summary>
        public static Mp3Pcm DecodeWindow(string path, float startSec, float lenSec)
        {
            try
            {
                using (var mp3 = new MpegFile(path))
                {
                    int ch = mp3.Channels > 0 ? mp3.Channels : 2;
                    int sr = mp3.SampleRate > 0 ? mp3.SampleRate : 44100;
                    double dur = mp3.Duration.TotalSeconds;
                    float win = lenSec > 0f ? lenSec : 20f;
                    if (dur > 0 && win > dur) win = (float)dur;
                    float start = startSec;
                    if (start < 0f || (dur > 0 && start >= dur)) start = (float)(dur * 0.4);   // osu default preview = 40% of length
                    if (dur > 0) start = (float)Math.Min(start, Math.Max(0.0, dur - win));
                    if (start < 0f) start = 0f;
                    if (start > 0f) mp3.Time = TimeSpan.FromSeconds(start);

                    int need = (int)(win * sr) * ch;
                    if (need <= 0) return null;
                    var data = new float[need];
                    int len = 0;
                    var buf = new float[16384];
                    int n;
                    while (len < need && (n = mp3.ReadSamples(buf, 0, Math.Min(buf.Length, need - len))) > 0)
                        for (int i = 0; i < n; i++)
                        {
                            float s = buf[i];
                            data[len++] = s > 1f ? 1f : (s < -1f ? -1f : s);
                        }
                    if (len == 0) return null;
                    if (len != data.Length) Array.Resize(ref data, len);
                    return new Mp3Pcm { Samples = data, Channels = ch, SampleRate = sr };
                }
            }
            catch { return null; }
        }

        /// <summary>Build an AudioClip from decoded PCM (MAIN THREAD only). Null on empty/invalid input.</summary>
        public static AudioClip ToClip(Mp3Pcm pcm, string name)
        {
            if (pcm == null || pcm.Samples == null || pcm.Samples.Length == 0 || pcm.Channels <= 0) return null;
            int perChannel = pcm.Samples.Length / pcm.Channels;
            if (perChannel <= 0) return null;
            var clip = AudioClip.Create(name, perChannel, pcm.Channels, pcm.SampleRate, false);
            clip.SetData(pcm.Samples, 0);
            return clip;
        }
    }

    /// <summary>
    /// STREAMING mp3 preview clip — plays as fast as ogg. Instead of decoding the whole window up front (~100-470 ms),
    /// it creates a streaming AudioClip whose PCMReaderCallback pulls samples from NLayer on demand on the audio thread,
    /// looping a [startSec, startSec+lenSec] window seamlessly. Opening + seeking is ~20-30 ms so playback starts almost
    /// immediately; NLayer decodes ~130× faster than realtime so the callback never underruns. Create on the MAIN thread
    /// (AudioClip.Create), then assign <see cref="Clip"/> to an AudioSource and Play. Dispose (main thread) when done.
    /// </summary>
    public sealed class Mp3StreamClip : IDisposable
    {
        private NLayer.MpegFile _mp3;
        private readonly int _channels;
        private readonly int _sampleRate;
        private readonly double _startSec;
        private readonly int _windowFrames;   // per-channel samples in the loop window
        private int _framesRead;              // per-channel frames since window start
        private readonly object _lock = new object();

        public AudioClip Clip { get; private set; }

        /// <summary>Open <paramref name="path"/> and build a looping streaming clip of a [start, start+len] window.
        /// startSec &lt; 0 (or past the end) → centre the window. Null on failure. MAIN THREAD.</summary>
        public static Mp3StreamClip Create(string path, float startSec, float lenSec, string name)
        {
            NLayer.MpegFile mp3 = null;
            try
            {
                mp3 = new NLayer.MpegFile(path);
                int ch = mp3.Channels > 0 ? mp3.Channels : 2;
                int sr = mp3.SampleRate > 0 ? mp3.SampleRate : 44100;
                double dur = mp3.Duration.TotalSeconds;
                float win = lenSec > 0f ? lenSec : 20f;
                if (dur > 0 && win > dur) win = (float)dur;
                float start = startSec;
                if (start < 0f || (dur > 0 && start >= dur)) start = (float)(dur * 0.4);   // osu default preview = 40% of length
                if (dur > 0) start = (float)Math.Min(start, Math.Max(0.0, dur - win));
                if (start < 0f) start = 0f;
                int winFrames = Math.Max(1, (int)(win * sr));

                var self = new Mp3StreamClip(mp3, ch, sr, start, winFrames);
                self.SeekStart();
                // Big clip length + our own internal loop (OnRead) so Unity never wraps the position (avoids a resync
                // seam at the loop). Streaming clips don't allocate the sample buffer, so a long length is free.
                int clipLen = Math.Max(winFrames, sr * 600);
                self.Clip = AudioClip.Create(name, clipLen, ch, sr, true, self.OnRead, self.OnSetPos);
                return self;
            }
            catch { try { mp3?.Dispose(); } catch { } return null; }
        }

        private Mp3StreamClip(NLayer.MpegFile mp3, int ch, int sr, double start, int winFrames)
        { _mp3 = mp3; _channels = ch; _sampleRate = sr; _startSec = start; _windowFrames = winFrames; }

        // Audio thread: fill data with the next interleaved samples, looping the window seamlessly.
        private void OnRead(float[] data)
        {
            lock (_lock)
            {
                if (_mp3 == null) { Array.Clear(data, 0, data.Length); return; }
                int need = data.Length, off = 0, guard = 0;
                while (off < need && guard++ < 256)
                {
                    if (_framesRead >= _windowFrames) SeekStart();
                    int framesLeft = Math.Max(1, _windowFrames - _framesRead);
                    int want = Math.Min(need - off, framesLeft * _channels);
                    int n = _mp3.ReadSamples(data, off, want);
                    if (n <= 0) { SeekStart(); continue; }
                    for (int k = off; k < off + n; k++) { float s = data[k]; if (s > 1f) data[k] = 1f; else if (s < -1f) data[k] = -1f; }
                    off += n; _framesRead += n / _channels;
                }
                if (off < need) Array.Clear(data, off, need - off);
            }
        }

        // Audio thread: Unity seeks (rare with our big length + internal loop) → re-point the decoder.
        private void OnSetPos(int position)
        {
            lock (_lock)
            {
                int p = position % _windowFrames; if (p < 0) p += _windowFrames;
                _framesRead = p;
                SeekTo(p);
            }
        }

        private void SeekStart() { _framesRead = 0; SeekTo(0); }
        private void SeekTo(int frame)
        {
            if (_mp3 == null) return;
            try { _mp3.Time = TimeSpan.FromSeconds(_startSec + frame / (double)_sampleRate); } catch { }
        }

        public void Dispose()
        {
            lock (_lock) { try { _mp3?.Dispose(); } catch { } _mp3 = null; }
            if (Clip != null) { UnityEngine.Object.Destroy(Clip); Clip = null; }
        }
    }
}
