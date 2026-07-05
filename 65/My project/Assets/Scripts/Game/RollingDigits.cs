using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// A fixed row of digit SpriteRenderers that count up to a target with the shared <see cref="RollingNumber"/>
    /// roll + per-digit pop. Reusable wherever a number animates the way the in-game score does — here it drives the
    /// result-screen EXP / G幣 totals. Digits are placed on the 800×600 design grid (top-left origin); the run is
    /// right-aligned to <c>rightX</c> (so a short value hugs the baked "G" / label) or left-aligned from it.
    /// Leading zeros are hidden (a value of 0 still shows a single "0"). Driven by an external <see cref="Tick"/>.
    /// </summary>
    public sealed class RollingDigits
    {
        private readonly SpriteRenderer[] _slots;     // [0] = rightmost digit
        private readonly Sprite[] _digits;            // 0..9
        private readonly float _rightX, _y, _z, _pitch;
        private readonly bool _rightAlign;

        private long _from, _target, _shown = long.MinValue;
        private float _animAt = -999f;
        private bool _visible = true;

        /// <summary>Hide/show the whole run. When hidden, all slots are disabled and <see cref="Tick"/> is a no-op
        /// (so it can't re-enable them) — used to drop the ShowTime score/bonus off the result panel.</summary>
        public void SetVisible(bool on)
        {
            _visible = on;
            if (!on) for (int i = 0; i < _slots.Length; i++) if (_slots[i]) _slots[i].enabled = false;
        }

        /// <param name="maxDigits">slot count (max value width).</param>
        /// <param name="pitch">design-px advance between digits (≈ digit width).</param>
        /// <param name="rightAlign">true → digits end at rightX (right-aligned); false → start at rightX.</param>
        public RollingDigits(Transform parent, Sprite[] digits, int maxDigits, int order,
                             float rightX, float y, float pitch, bool rightAlign = true, float z = -3f)
        {
            _digits = digits; _rightX = rightX; _y = y; _z = z; _pitch = pitch; _rightAlign = rightAlign;
            _slots = new SpriteRenderer[Mathf.Max(1, maxDigits)];
            for (int i = 0; i < _slots.Length; i++)
            {
                var sr = new GameObject("rd" + i).AddComponent<SpriteRenderer>();
                sr.transform.SetParent(parent, false);
                sr.sortingOrder = order; sr.enabled = false;
                _slots[i] = sr;
            }
        }

        /// <summary>Begin a roll from the currently-shown value to <paramref name="v"/>.</summary>
        public void SetTarget(long v, float now)
        {
            _from = _shown == long.MinValue ? 0 : _shown;
            _target = v; _animAt = now; _shown = _from;
        }

        /// <summary>Advance the roll and redraw. Safe to call every frame.</summary>
        public void Tick(float now)
        {
            if (!_visible) return;
            if (_digits == null || _digits.Length < 10) return;
            _shown = RollingNumber.ValueAt(_from, _target, _animAt, now);
            string s = (_shown < 0 ? 0 : _shown).ToString();
            float pop = RollingNumber.Bounce(now - _animAt);   // whole-number pop (all visible digits together)

            int n = s.Length;
            for (int slot = 0; slot < _slots.Length; slot++)
            {
                var sr = _slots[slot];
                if (slot >= n) { sr.enabled = false; continue; }
                // right-aligned: slot 0 = RIGHTMOST char, grows leftwards. left-aligned: slot 0 = LEFTMOST char,
                // grows rightwards. (The previous code reversed the digits in left-align mode → "30" drew as "03".)
                int ci = _rightAlign ? (n - 1 - slot) : slot;
                int d = s[ci] - '0';
                sr.sprite = _digits[d]; sr.enabled = true;
                float w = _digits[d].bounds.size.x, h = _digits[d].bounds.size.y;
                float topLeftX = _rightAlign ? _rightX - (slot + 1) * _pitch : _rightX + slot * _pitch;
                sr.transform.localScale = Vector3.one * pop;   // position by CENTRE so the pop scales in place
                sr.transform.position = new Vector3(SdoLayout.WorldX(topLeftX) + w / 2f, SdoLayout.WorldY(_y) - h / 2f, _z);
            }
        }
    }
}
