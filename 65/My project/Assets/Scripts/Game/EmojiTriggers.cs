using Sdo.Ruleset;

namespace Sdo.Game
{
    /// <summary>Which head-emoji cut-in (UI/PLAYINGEXP sequence) to play.</summary>
    public enum EmojiKind { None, HH, SHSH, JRKL, KJ, HE, H, Y, JS, GTH }

    /// <summary>
    /// Pure, Unity-free decision logic for the dancer's head-emoji cut-ins. Step1Game owns one instance, feeds it
    /// every judgment (with the resulting combo) and the visible HP-bar fraction, and pops whichever PNG sequence it
    /// returns (<see cref="EmojiKind.None"/> = show nothing). Kept Unity-free so the threshold / hysteresis edge cases
    /// are unit-tested (see EmojiTriggersTests): combo milestones fire once each as a streak climbs and re-fire if the
    /// streak is rebuilt after a break; the consecutive-miss stages don't re-fire within one run; the low-HP trigger
    /// has on/off hysteresis so it can't chatter at the threshold.
    /// </summary>
    public sealed class EmojiTriggers
    {
        public int ConsecMiss { get; private set; }     // current run of consecutive Bad/Miss (reset on Perfect/Cool)
        public int MissStage { get; private set; }      // highest miss emoji fired this run: 0 none, 1 H, 2 Y, 3 JS
        public bool LowHpArmed { get; private set; } = true;   // GTH ready to fire (re-armed once HP recovers)

        public const float LowHpOn = 0.30f;    // bar fraction strictly below which GTH fires
        public const float LowHpOff = 0.40f;   // bar fraction strictly above which GTH re-arms

        /// <summary>Feed one judgment plus the combo value AFTER it was applied. Returns the combo-milestone or
        /// consecutive-miss emoji to show, or <see cref="EmojiKind.None"/>.</summary>
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

            // Bad / Miss: grow the run; fire each stage once (descending so a multi-step jump reports the highest).
            ConsecMiss++;
            if (ConsecMiss >= 50 && MissStage < 3) { MissStage = 3; return EmojiKind.JS; }
            if (ConsecMiss >= 30 && MissStage < 2) { MissStage = 2; return EmojiKind.Y; }
            if (ConsecMiss >= 10 && MissStage < 1) { MissStage = 1; return EmojiKind.H; }
            return EmojiKind.None;
        }

        /// <summary>Feed the visible HP-bar fill fraction (0..1). Returns GTH once when it drops below 30%; the trigger
        /// re-arms once the bar recovers above 40% (hysteresis prevents chattering around the threshold).</summary>
        public EmojiKind OnHp(float frac)
        {
            if (frac < LowHpOn && LowHpArmed) { LowHpArmed = false; return EmojiKind.GTH; }
            if (frac > LowHpOff) LowHpArmed = true;
            return EmojiKind.None;
        }
    }
}
