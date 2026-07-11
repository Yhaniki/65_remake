using System;
using NUnit.Framework;
using Sdo.Ruleset;

namespace Sdo.Tests
{
    /// <summary>
    /// Locks GameplayClock's audio-sync filter: it must advance smoothly on the wall clock while re-locking
    /// onto the true audio position, correct slow drift invisibly, snap on a stall, and never rewind under slew.
    /// See GameplayClock.cs for the design.
    /// </summary>
    public class GameplayClockTests
    {
        private const double Frame = 1.0 / 60.0;   // 16.6ms, a typical frame

        // With no audio truth the clock is exactly the wall clock (the old behaviour, so nothing that never
        // gets an audio reading — observe-burst mode, headless — changes).
        [Test]
        public void WallOnly_EqualsWallClock()
        {
            var c = new GameplayClock();
            c.Tick(0.0, null);
            Assert.AreEqual(0.0, c.CurrentMs, 1e-9);
            c.Tick(0.016, null);
            Assert.AreEqual(16.0, c.CurrentMs, 1e-9);
            c.Tick(0.032, null);
            Assert.AreEqual(32.0, c.CurrentMs, 1e-9);
        }

        [Test]
        public void Offset_ShiftsCurrentMs_ButNotEstimate()
        {
            var c = new GameplayClock { OffsetMs = 20.0 };
            c.Tick(0.0, null);
            Assert.AreEqual(20.0, c.CurrentMs, 1e-9);
            Assert.AreEqual(0.0, c.EstimatedMs, 1e-9);
            c.Tick(0.1, null);
            Assert.AreEqual(120.0, c.CurrentMs, 1e-9);   // 100ms wall + 20ms offset
            Assert.AreEqual(100.0, c.EstimatedMs, 1e-9);
        }

        // Back-compat: SetAudioSeconds is a wall-only tick.
        [Test]
        public void SetAudioSeconds_IsWallOnlyTick()
        {
            var c = new GameplayClock();
            c.SetAudioSeconds(0.0);
            c.SetAudioSeconds(0.5);
            Assert.AreEqual(500.0, c.CurrentMs, 1e-9);
        }

        // A gap larger than the snap threshold (a stall recovery / seek) snaps straight onto the audio.
        [Test]
        public void LargeGap_SnapsOntoAudio()
        {
            var c = new GameplayClock();   // SnapThreshold 50ms
            c.Tick(0.0, 0.0);
            c.Tick(Frame, Frame);          // aligned
            c.Tick(2 * Frame, 0.2);        // audio jumps to 200ms while the wall says ~33ms → snap
            Assert.AreEqual(200.0, c.EstimatedMs, 1e-9);
            Assert.AreEqual(200.0, c.CurrentMs, 1e-9);
        }

        // The wall clock runs ~0.6% fast; over ~10s it drifts 60ms ahead of the audio, but the clock stays
        // locked to the AUDIO truth (within a couple ms) and never runs backward.
        [Test]
        public void SlowDrift_TracksAudio_AndStaysMonotonic()
        {
            var c = new GameplayClock();
            double wall = 0.0, audio = 0.0;
            c.Tick(wall, audio);
            double prev = c.CurrentMs;

            for (int i = 0; i < 600; i++)   // ~10s at 60fps
            {
                wall += Frame;
                audio += Frame * 0.994;     // audio 0.6% slower → wall drifts ahead
                c.Tick(wall, audio);
                Assert.GreaterOrEqual(c.CurrentMs + 1e-9, prev, "clock must never rewind under slew");
                prev = c.CurrentMs;
            }

            double audioMs = audio * 1000.0;
            double wallMs = wall * 1000.0;
            Assert.Less(Math.Abs(c.EstimatedMs - audioMs), 3.0, "clock should track the audio truth");
            Assert.Greater(Math.Abs(wallMs - audioMs), 10.0, "sanity: the wall clock really did drift away");
        }

        // The audio reading is steppy (updates once per audio buffer, constant in between). The output must stay
        // smooth (monotonic, no per-buffer jitter) and hug the true position rather than the stale sample.
        [Test]
        public void SteppyAudioReading_StaysSmoothAndCloseToTruth()
        {
            var c = new GameplayClock();
            double wall = 0.0;
            c.Tick(0.0, 0.0);
            double prev = c.CurrentMs;
            double reported = 0.0;          // last value the audio buffer exposed (staircase)

            for (int i = 1; i <= 300; i++)
            {
                wall = i * Frame;
                if (i % 3 == 0) reported = wall;   // audio == wall in truth, but only revealed every 3rd frame
                c.Tick(wall, reported);
                Assert.GreaterOrEqual(c.CurrentMs + 1e-9, prev, "steppy input must not make the output jitter backward");
                prev = c.CurrentMs;
                Assert.Less(Math.Abs(c.CurrentMs - wall * 1000.0), 60.0, "output must hug the true position, not the stale sample");
            }
        }

        // A backward slew request (audio behind the estimate but within the snap threshold) slows the clock but
        // never reverses it.
        [Test]
        public void AudioBehindEstimate_SlowsButNeverRewinds()
        {
            var c = new GameplayClock();
            c.Tick(0.0, 0.0);
            double prev = c.CurrentMs;
            double wall = 0.0;
            for (int i = 0; i < 120; i++)
            {
                wall += Frame;
                double audio = wall - 0.030;   // audio persistently 30ms behind (< 50ms snap) → constant back-pressure
                c.Tick(wall, audio);
                Assert.GreaterOrEqual(c.CurrentMs + 1e-9, prev, "must slow, not rewind");
                prev = c.CurrentMs;
            }
        }

        [Test]
        public void Reset_ReseedsEstimate()
        {
            var c = new GameplayClock();
            c.Tick(0.0, 0.0);
            c.Tick(1.0, 1.0);
            Assert.AreEqual(1000.0, c.CurrentMs, 1e-9);
            c.Reset();
            c.Tick(5.0, 5.0);                 // fresh song: seeds at 5000, not 1000 + delta
            Assert.AreEqual(5000.0, c.CurrentMs, 1e-9);
        }
    }
}
