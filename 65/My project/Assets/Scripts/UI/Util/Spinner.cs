using UnityEngine;

namespace Sdo.UI.Util
{
    /// <summary>
    /// Z rotation for the ROOMDLG vinyl disk. Steady state reproduces the original <c>Circumgyrate</c>
    /// (360° per 1000ms, repeat −1; negative = clockwise). <see cref="Restart"/> makes the disk hold still for
    /// <see cref="PauseSec"/> then ease up to full speed over <see cref="RampSec"/> — the "stop, then spin up"
    /// feel the original plays when the selected song (cover) changes. Unscaled time, so it ignores timeScale.
    /// </summary>
    public sealed class Spinner : MonoBehaviour
    {
        public float DegPerSec = -360f;
        public float PauseSec = 1f;
        public float RampSec = 2f;

        private float _t = -1f;   // -1 = steady full speed; >=0 = seconds since Restart (pause + ramp window)

        /// <summary>Snap upright, then stop + accelerate back to full speed (call when the disk content changes).</summary>
        public void Restart()
        {
            _t = 0f;
            transform.localRotation = Quaternion.identity;   // always start the new cover straight, then spin up
        }

        private void Update()
        {
            float speed = DegPerSec;
            if (_t >= 0f)
            {
                _t += Time.unscaledDeltaTime;
                if (_t < PauseSec) speed = 0f;
                else if (_t < PauseSec + RampSec) speed = DegPerSec * ((_t - PauseSec) / RampSec);
                else _t = -1f;   // ramp done -> steady full speed
            }
            transform.Rotate(0f, 0f, speed * Time.unscaledDeltaTime);
        }
    }
}
