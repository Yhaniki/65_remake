using System.Collections.Generic;

namespace Sdo.Ruleset
{
    /// <summary>
    /// One recorded input frame: the song-relative time (ms) and the held-key bitmask (bit <c>i</c> = lane
    /// <c>i</c> held). Mirrors osu! <c>ManiaReplayFrame</c> (active-columns bitmask). Input + chart + ruleset
    /// deterministically reproduce judgments, score, the dancer's motion and the on-board hits — so a replay
    /// need not store per-frame skeleton poses (see docs/systems/replay-local.md).
    /// </summary>
    public readonly struct ReplayFrame
    {
        public readonly double TimeMs;
        public readonly int KeyMask;
        public ReplayFrame(double timeMs, int keyMask) { TimeMs = timeMs; KeyMask = keyMask; }
    }

    /// <summary>
    /// Replay identity + summary (osu <c>.rpl</c>-style header) so a viewer can validate the chart/ruleset
    /// before replaying and show a result line. See docs/systems/replay-local.md.
    /// </summary>
    public sealed class ReplayHeader
    {
        public string ChartHash;
        public string SongId;
        public string DifficultySlot;
        public string ScrollDirection;
        public int RulesetVersion;
        public long Score;
        public double Accuracy;          // 0..100
        public int MaxCombo;
        public int Perfect, Cool, Bad, Miss;
        public string PlayedAtUtc;
        public string PlayerName;
    }

    /// <summary>
    /// An in-memory replay: header + the input-frame stream. Frames are appended only when the held-key
    /// bitmask CHANGES (osu-style dedupe), so a full song is at most a few hundred frames. The SAME data drives
    /// (A) the result-screen background dance (hits/board hidden) and (B) later replay viewing (hits shown).
    /// Pure logic (no UnityEngine) — recorded by Sdo.Game, replayed by feeding <see cref="MaskAt"/> back into the
    /// ruleset, and (P1) serialised to <c>.rpl</c>.
    /// </summary>
    public sealed class Replay
    {
        public ReplayHeader Header = new ReplayHeader();
        public readonly List<ReplayFrame> Frames = new List<ReplayFrame>();
        private int _lastMask;
        private bool _any;

        /// <summary>Largest frame time recorded (ms), or 0 when empty — the replay's effective length.</summary>
        public double LengthMs => Frames.Count == 0 ? 0.0 : Frames[Frames.Count - 1].TimeMs;

        /// <summary>
        /// Record the held-key bitmask at <paramref name="timeMs"/>. No-op if the mask is unchanged since the last
        /// recorded frame (dedupe); if called twice at the same timestamp the latest mask replaces the earlier one.
        /// Returns true when a frame was actually appended/updated.
        /// </summary>
        public bool Record(double timeMs, int keyMask)
        {
            if (_any && keyMask == _lastMask) return false;
            if (Frames.Count > 0 && Frames[Frames.Count - 1].TimeMs == timeMs)
                Frames[Frames.Count - 1] = new ReplayFrame(timeMs, keyMask);
            else
                Frames.Add(new ReplayFrame(timeMs, keyMask));
            _lastMask = keyMask; _any = true;
            return true;
        }

        /// <summary>
        /// The held-key bitmask in effect at <paramref name="timeMs"/> — the most recent frame at or before it,
        /// or 0 before the first frame. Used to feed recorded input back into the ruleset on playback.
        /// </summary>
        public int MaskAt(double timeMs)
        {
            int mask = 0;
            var f = Frames;
            for (int i = 0; i < f.Count; i++)
            {
                if (f[i].TimeMs > timeMs) break;
                mask = f[i].KeyMask;
            }
            return mask;
        }

        public void Clear() { Frames.Clear(); _lastMask = 0; _any = false; }
    }
}
