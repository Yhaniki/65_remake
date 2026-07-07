using Sdo.Ruleset;

namespace Sdo.Game
{
    /// <summary>Which head-emoji cut-in (UI/PLAYINGEXP sequence) to play.</summary>
    public enum EmojiKind { None, HH, SHSH, JRKL, KJ, HE, H, Y, JS, GTH }

    /// <summary>
    /// Pure, Unity-free decision logic for the dancer's head-emoji cut-ins. ScreenGameplay owns one instance, feeds it
    /// every judgment (with the resulting combo) and the visible HP-bar fraction, and pops whichever PNG sequence it
    /// returns (<see cref="EmojiKind.None"/> = show nothing). Kept Unity-free so the threshold / edge cases are
    /// unit-tested (see EmojiTriggersTests): combo milestones fire once each as a streak climbs and re-fire if the
    /// streak is rebuilt after a break; the consecutive-miss stages (H/Y/JS) don't re-fire within one run; GTH fires
    /// once when the CUMULATIVE miss count reaches 100 over the whole song. Low HP no longer shows an emoji — it only
    /// signals (<see cref="OnHp"/> returns true, with on/off hysteresis) so the caller can play the VOICE_0012 warning.
    /// </summary>
    public sealed class EmojiTriggers
    {
        public int ConsecMiss { get; private set; }     // current run of consecutive Bad/Miss (reset on Perfect/Cool)
        public int MissStage { get; private set; }      // highest miss emoji fired this run: 0 none, 1 H, 2 Y, 3 JS
        public int TotalMiss { get; private set; }      // cumulative Bad/Miss over the whole run (never resets)
        public bool GthArmed { get; private set; } = true;     // GTH (100 total misses) fires exactly once
        public bool LowHpArmed { get; private set; } = true;   // low-HP voice signal ready (re-armed once HP recovers)

        public const float LowHpOn = 0.30f;    // bar fraction strictly below which the low-HP voice signals
        public const float LowHpOff = 0.40f;   // bar fraction strictly above which the low-HP signal re-arms
        public const int GthTotalMiss = 100;   // GTH cut-in fires when the cumulative Bad/Miss count reaches this

        /// <summary>Feed one judgment plus the combo value AFTER it was applied. Returns the combo-milestone,
        /// cumulative-100-miss (GTH), or consecutive-miss emoji to show, or <see cref="EmojiKind.None"/>.</summary>
        public EmojiKind OnJudge(Judgment j, int comboAfter)
        {
            if (j == Judgment.Perfect || j == Judgment.Cool)
            {
                ConsecMiss = 0; MissStage = 0;
                switch (comboAfter)
                {
                    case 50: return EmojiKind.HH;
                    case 150: return EmojiKind.SHSH;
                    case 350: return EmojiKind.JRKL;
                    case 550: return EmojiKind.KJ;
                    case 800: return EmojiKind.HE;
                    default: return EmojiKind.None;
                }
            }

            // Bad / Miss: grow the cumulative total AND the consecutive run.
            TotalMiss++;
            ConsecMiss++;
            // GTH: 100 misses TOTAL over the song (not consecutive), fires once. Checked before the consec stages so the
            // judgment that reaches 100 shows GTH (the consec stages fire earlier at 10/30/50 and are one-shot per run).
            if (TotalMiss >= GthTotalMiss && GthArmed) { GthArmed = false; return EmojiKind.GTH; }
            // Consecutive-miss stages: fire each once (descending so a multi-step jump reports the highest).
            if (ConsecMiss >= 50 && MissStage < 3) { MissStage = 3; return EmojiKind.JS; }
            if (ConsecMiss >= 30 && MissStage < 2) { MissStage = 2; return EmojiKind.Y; }
            if (ConsecMiss >= 10 && MissStage < 1) { MissStage = 1; return EmojiKind.H; }
            return EmojiKind.None;
        }

        /// <summary>Feed the visible HP-bar fill fraction (0..1). Returns true ONCE when it first drops below 30% — the
        /// caller plays the low-HP warning voice VOICE_0012. Re-arms once the bar recovers above 40% (hysteresis
        /// prevents chattering around the threshold). No longer shows an emoji: the GTH cut-in moved to 100 total misses.</summary>
        public bool OnHp(float frac)
        {
            if (frac < LowHpOn && LowHpArmed) { LowHpArmed = false; return true; }
            if (frac > LowHpOff) LowHpArmed = true;
            return false;
        }
    }
}
