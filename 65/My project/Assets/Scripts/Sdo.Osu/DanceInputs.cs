using System.Collections.Generic;

namespace Sdo.Osu
{
    /// <summary>One chart's note window: when its first note lands and when its last one ends (ms).</summary>
    public readonly struct ChartWindow
    {
        public double FirstMs { get; }
        public double LastMs { get; }

        public ChartWindow(double firstMs, double lastMs)
        {
            FirstMs = firstMs;
            LastMs = lastMs;
        }
    }

    /// <summary>
    /// The two inputs a generated choreography is planned from — deliberately taken from the SONG, never from the
    /// difficulty the player happened to pick.
    ///
    /// <see cref="RandomDps"/> is deterministic in its seed, but the seed is only half of it: the plan also depends on
    /// how long the dance is and on the beat grid. Feeding it the loaded chart's own span and BPM made a song's dance
    /// depend on which difficulty was played first — easy and hard rarely start and end on the same note, so the same
    /// seed produced two differently-truncated dances. One song must have ONE dance, so:
    ///
    ///   • length — the UNION of every difficulty's note window: earliest first note → latest last note
    ///     (<see cref="UnionSeconds"/>). Whichever difficulty is played, that chart's own window sits inside this one,
    ///     so the dancer never runs out of choreography before the last note. The dance still STARTS on the played
    ///     difficulty's first note (ScreenGameplay._danceStartSec) — only its length is shared.
    ///   • tempo — the catalog's per-song BPM (what song-select shows). Charts in one mapset normally carry identical
    ///     timing, but nothing enforces it (Malody .mc files each carry their own), and a differing BPM would re-cut
    ///     every row.
    ///
    /// The windows must be measured off the charts AS WRITTEN (see <c>Sdo.Game.ExternalChartIO</c>): gameplay's
    /// own post-steps — lead-in, short-hold collapse, bomb removal — move the last note and hang off player settings,
    /// which must not reach the dance.
    ///
    /// Either input missing (no chart could be measured, a song whose BPM never got resolved) falls back to the loaded
    /// chart — a dance that is still deterministic per difficulty, which is strictly better than none.
    /// </summary>
    public struct DanceInputs
    {
        /// <summary>Beat grid when nothing else is known (RandomDps' own default).</summary>
        public const double FallbackBpm = 120.0;

        /// <summary>Seconds of dance to plan.</summary>
        public double Seconds;

        /// <summary>Beats per minute the rows are laid out on.</summary>
        public double Bpm;

        /// <summary>True when BOTH inputs came from the song (⇒ every difficulty plans the same dance); false when
        /// either had to fall back to the loaded chart.</summary>
        public bool PerSong;

        /// <summary>
        /// Earliest first note → latest last note across a song's difficulties, in seconds; 0 when there is nothing to
        /// measure. Windows that end before they start (a chart parsed to nothing sensible) are ignored, so one broken
        /// difficulty can't stretch or collapse the dance.
        /// </summary>
        public static double UnionSeconds(IReadOnlyList<ChartWindow> charts)
        {
            if (charts == null) return 0.0;
            double first = 0.0, last = 0.0;
            bool any = false;
            for (int i = 0; i < charts.Count; i++)
            {
                var w = charts[i];
                if (w.LastMs <= w.FirstMs) continue;
                if (!any) { first = w.FirstMs; last = w.LastMs; any = true; continue; }
                if (w.FirstMs < first) first = w.FirstMs;
                if (w.LastMs > last) last = w.LastMs;
            }
            return any ? (last - first) / 1000.0 : 0.0;
        }

        /// <param name="unionSeconds">The song's dance window (<see cref="UnionSeconds"/>); &lt;= 0 = nothing measurable.</param>
        /// <param name="songBpm">The catalog's per-song BPM; &lt;= 0 = unknown.</param>
        /// <param name="mapSpanSeconds">Fallback: the loaded chart's own first→last note span.</param>
        /// <param name="mapBpm">Fallback: the loaded chart's own BPM.</param>
        public static DanceInputs For(double unionSeconds, double songBpm, double mapSpanSeconds, double mapBpm)
        {
            bool haveSpan = unionSeconds > 0.0;
            bool haveBpm = songBpm > 0.0;
            return new DanceInputs
            {
                Seconds = haveSpan ? unionSeconds : (mapSpanSeconds > 0.0 ? mapSpanSeconds : 0.0),
                Bpm = haveBpm ? songBpm : (mapBpm > 0.0 ? mapBpm : FallbackBpm),
                PerSong = haveSpan && haveBpm,
            };
        }
    }
}
