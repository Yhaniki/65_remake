namespace Sdo.Ruleset
{
    /// <summary>
    /// SDO single-player health, recovered from the decompiled exe (verified against raw bytes).
    /// See docs/reverse-engineering/SDO_HP_FORMULA.md.
    ///
    /// Model (FUN_004a6470 / FUN_0046ca60):
    ///   HP starts at 1000 (= max, player object offset 0x4).
    ///   Each judgment: HP = clamp(HP + delta[level][judge], -150, 1000).
    ///   Death floor is -150 (0x...ff6a); the bar can sit below "empty" (0) as a buffer.
    ///   Per-judgment deltas depend on a system level (DAT_00674f04+0x75 = 0/1/2).
    /// </summary>
    public sealed class HealthProcessor
    {
        public const double MaxHealth = 1000.0;
        public const double FloorHealth = -150.0; // -0x96; death floor

        // delta[level][judge]; judge order = Perfect, Cool, Bad, Miss
        // level 0: 0x980..0x98c = 6, 4, -10, -50
        // level 1:                 4, 2,  -7, -40
        // level 2:                 2, 1,  -5, -30
        private static readonly double[][] Deltas =
        {
            new[] {  6.0,  4.0, -10.0, -50.0 },
            new[] {  4.0,  2.0,  -7.0, -40.0 },
            new[] {  2.0,  1.0,  -5.0, -30.0 },
        };

        private readonly double[] _delta;
        private readonly bool _invincible;

        public double Health { get; private set; } = MaxHealth;

        /// <summary>0..1 for the visible HP bar (negative buffer shows empty).</summary>
        public double Normalized
        {
            get
            {
                double h = Health < 0.0 ? 0.0 : Health;
                return h / MaxHealth;
            }
        }

        /// <summary>True once HP hits the -150 death floor.</summary>
        public bool IsFailed => Health <= FloorHealth;

        /// <param name="level">System health level 0/1/2 (DAT_00674f04+0x75). Clamped.</param>
        /// <param name="invincible">player[0x9d] flag: HP changes disabled when set.</param>
        public HealthProcessor(int level = 0, bool invincible = false)
        {
            if (level < 0) level = 0;
            if (level > 2) level = 2;
            _delta = Deltas[level];
            _invincible = invincible;
        }

        public void Apply(Judgment j)
        {
            if (_invincible) return;

            Health += BaseDelta(j);
            if (Health > MaxHealth) Health = MaxHealth;
            if (Health < FloorHealth) Health = FloorHealth;
        }

        private double BaseDelta(Judgment j)
        {
            switch (j)
            {
                case Judgment.Perfect: return _delta[0];
                case Judgment.Cool: return _delta[1];
                case Judgment.Bad: return _delta[2];
                case Judgment.Miss: return _delta[3];
                default: return 0.0;
            }
        }
    }
}
