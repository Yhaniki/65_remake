using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// The floor "yuanpan" ring under the dancer (SDO yuanpan.eft — a flat 14-segment ring band that spins).
    /// We render it as a soft glow disc + a ring of star sprites lying flat on the ground, slowly rotating and
    /// twinkling. It TRACKS the dancer: <see cref="Follow"/> is an anchor transform pinned to the pelvis bone
    /// (the avatar's root GameObject never moves — the dance translates the bones), and each frame the ring
    /// re-centres on that bone's X/Z while staying pinned to the floor at <see cref="FloorY"/>.
    /// </summary>
    public sealed class StarRing : MonoBehaviour
    {
        public SpriteRenderer[] Stars;
        public SpriteRenderer Glow;          // soft centre glow disc (optional)
        public float Radius = 26f;           // ring radius (flat-on-floor mode)
        public float Spin = 0.6f;            // orbit speed (rad/s); >0 = counter-clockwise viewed from above
        public float BaseScale = 1f;
        public Color Tint = Color.white;
        public Transform Follow;             // dancer pelvis anchor; ring re-centres on its X/Z each frame
        public float FloorY = 0.6f;

        public bool Billboard;               // legacy 2D mode: ellipse + face the perspective camera
        public float Rx = 26f, Ry = 26f;     // 2D ellipse radii
        private Camera _bcam;

        // LateUpdate so we read the pelvis anchor AFTER SdoAvatar has posed the skeleton this frame.
        private void LateUpdate()
        {
            if (Follow) { var p = Follow.position; transform.position = new Vector3(p.x, FloorY, p.z); }

            int n = Stars == null ? 0 : Stars.Length;
            for (int i = 0; i < n; i++)
            {
                var s = Stars[i]; if (!s) continue;
                float a = Time.time * Spin + i * Mathf.PI * 2f / n;

                if (Billboard)   // 2D: fake-perspective ellipse, stars face the camera
                {
                    float sin = Mathf.Sin(a);
                    s.transform.localPosition = new Vector3(Mathf.Cos(a) * Rx, sin * Ry, sin * 0.01f);
                    float depth = (sin + 1f) * 0.5f;
                    s.transform.localScale = Vector3.one * BaseScale * (0.55f + 0.45f * depth);
                    float tw2 = 0.55f + 0.45f * Mathf.Sin(Time.time * 5f + i * 1.7f);
                    s.color = new Color(Tint.r, Tint.g, Tint.b, tw2 * (0.4f + 0.6f * depth));
                    if (_bcam == null) foreach (var c in Camera.allCameras) if (!c.orthographic) { _bcam = c; break; }
                    if (_bcam) s.transform.rotation = Quaternion.LookRotation(s.transform.position - _bcam.transform.position);
                    continue;
                }

                // flat-on-floor: the root is tilted 90° about X, so local XY lies in the world XZ plane.
                s.transform.localPosition = new Vector3(Mathf.Cos(a) * Radius, Mathf.Sin(a) * Radius, 0f);
                s.transform.localRotation = Quaternion.identity;   // stars lie flat on the ground
                float tw = 0.6f + 0.4f * Mathf.Sin(Time.time * 4f + i * 1.7f);   // twinkle
                s.transform.localScale = Vector3.one * BaseScale * (0.8f + 0.3f * tw);
                s.color = new Color(Tint.r, Tint.g, Tint.b, tw);
            }
        }
    }
}
