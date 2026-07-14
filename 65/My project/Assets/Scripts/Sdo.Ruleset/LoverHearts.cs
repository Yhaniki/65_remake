using System.Collections.Generic;

namespace Sdo.Ruleset
{
    /// <summary>
    /// 情侶模式 (LOVER / Couple) heart collection — a faithful port of the CN online client
    /// event handler. See docs/reverse-engineering/SDO_COUPLE_MODE.md §2.
    ///
    /// Model (event id 12 = 0xc in FUN_0072fce0, sdo.bin.c:464933-464951):
    ///   The couple screen (mode byte +0x62 ∈ {0x02,0x0c}) consumes a replay/network event
    ///   carrying a byte <c>param</c> and:
    ///     param &lt; 0xe0 : slot = (param&amp;1)+(param&gt;&gt;1)*2   (== param);
    ///                     if (count[slot] &lt; 0x14) count[slot]++    // clamp at 20, no fail
    ///     param &gt;= 0xf0: set row-A "wink"/celebration flag (no count change)
    ///     0xe0..0xef  : set row-B "wink"/celebration flag (no count change)
    ///   There is NO win/lose condition tied to hearts; 20 is purely a display clamp.
    ///
    /// The SEND side (which Perfect/Cool judgement or combo milestone emits id 0xc) lives in the
    /// server and is NOT in the client binary, so in the offline remake the caller decides WHEN to
    /// grant a heart (e.g. one per Perfect) — that policy is a remake decision, not recovered code.
    /// This class only owns the authoritative "apply / clamp / wink" state so it stays pure &amp; testable.
    /// </summary>
    public sealed class LoverHearts
    {
        /// <summary>Per-dancer hard clamp (0x14). sdo.bin.c:464941.</summary>
        public const int MaxPerDancer = 20;

        /// <summary>param &gt;= this ⇒ row-A celebration flag (no count change). sdo.bin.c:464947.</summary>
        public const int WinkRowAParam = 0xF0;
        /// <summary>0xE0..0xEF ⇒ row-B celebration flag (no count change). sdo.bin.c:464951.</summary>
        public const int WinkRowBParam = 0xE0;

        private readonly Dictionary<int, int> _counts = new Dictionary<int, int>();

        /// <summary>Row-A "all filled" celebration/wink flag (set by param &gt;= 0xf0).</summary>
        public bool WinkRowA { get; private set; }
        /// <summary>Row-B "all filled" celebration/wink flag (set by param 0xe0..0xef).</summary>
        public bool WinkRowB { get; private set; }

        /// <summary>Current heart count for a dancer slot (0 if never touched).</summary>
        public int Count(int slot) => _counts.TryGetValue(slot, out var c) ? c : 0;

        /// <summary>Sum of all dancers' hearts.</summary>
        public int Total
        {
            get { int t = 0; foreach (var kv in _counts) t += kv.Value; return t; }
        }

        /// <summary>True once a slot has reached the 20 clamp.</summary>
        public bool IsFull(int slot) => Count(slot) >= MaxPerDancer;

        /// <summary>
        /// Grant one heart to a dancer slot, clamped at <see cref="MaxPerDancer"/> (no fail on overflow).
        /// This is the core of case 0xc (sdo.bin.c:464939-464942) and the entry point the offline
        /// remake calls directly (e.g. on a Perfect judgement) since it has no network param.
        /// </summary>
        public void AddHeart(int slot)
        {
            int c = Count(slot);
            if (c < MaxPerDancer) _counts[slot] = c + 1;
        }

        /// <summary>
        /// Faithful port of the id-12 (0xc) event apply. <paramref name="param"/> is the raw event byte.
        /// Useful if real recorded events are ever replayed; offline code normally calls
        /// <see cref="AddHeart"/> directly instead.
        /// </summary>
        public void ApplyEvent(int param)
        {
            if (param >= WinkRowAParam) WinkRowA = true;          // >= 0xf0
            else if (param >= WinkRowBParam) WinkRowB = true;     // 0xe0..0xef
            else AddHeart(SlotFromParam(param));                  // < 0xe0
        }

        /// <summary>
        /// Official slot addressing <c>(param&amp;1)+(param&gt;&gt;1)*2</c> from
        /// <c>obj+0x1994+((param&amp;1)+(param&gt;&gt;1)*2)*4</c> (sdo.bin.c:464939). The expression is the
        /// identity (== param); kept explicit to mirror the decompiled math.
        /// </summary>
        public static int SlotFromParam(int param) => (param & 1) + (param >> 1) * 2;

        /// <summary>Clear the celebration/wink flags (e.g. after playing the wink animation).</summary>
        public void ClearWinks() { WinkRowA = false; WinkRowB = false; }

        /// <summary>Reset all hearts and flags (new song / new round).</summary>
        public void Reset() { _counts.Clear(); WinkRowA = false; WinkRowB = false; }
    }
}
