using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Orients a scene quad to face the play camera every frame — the faithful remake of the SDO <c>BillboardSet</c>
    /// used for the SCN0022 坟墓 鬼火 (blue-flame) sprites. The official draws these as camera-facing billboards, NOT
    /// the fixed-orientation SHAN.MSH mesh (which looked foreshortened / "矮扁" from the stage camera's angle).
    ///
    /// This is a Y-axis (cylindrical) billboard: it rotates only about world-up so the flame stays UPRIGHT and always
    /// points up, while its face turns to the camera horizontally. The quad's own texture animation is driven
    /// separately by a shared <see cref="MapobjTexAnimator"/> (one counter → all flames in sync, like the original).
    /// Runs after the avatar/scene pose in LateUpdate (order 100), same as the other stage effects.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public sealed class SceneFlameBillboard : MonoBehaviour
    {
        private int _layer;
        private Camera _cam;
        // flipToMotion: mirror the sprite horizontally to LEAD WITH ITS HEAD in the direction of travel. A camera-facing
        // billboard can't turn a directional sprite (the ghost's head) toward its flight, so instead we flip it left/right
        // by the sign of its screen-horizontal velocity. The gui/gui2 texture's head faces LEFT, and the billboard renders
        // it mirrored (head-right) at +x — so +x when moving screen-right, −x when moving screen-left = always head-first.
        private bool _flipToMotion;
        private Vector3 _lastPos;
        private bool _hasLast;

        /// <param name="layer">the stage layer whose perspective camera the billboard should face</param>
        /// <param name="flipToMotion">mirror the sprite to face its flight direction (for directional sprites, e.g. ghosts)</param>
        public void Init(int layer, bool flipToMotion = false) { _layer = layer; _flipToMotion = flipToMotion; }

        private void LateUpdate()
        {
            if (_cam == null || !_cam.isActiveAndEnabled)
            {
                _cam = null;
                // Prefer the perspective camera that actually renders this layer (the stage cam that draws to the RT),
                // not merely the first perspective camera — same selection the EFT billboards use so both agree.
                foreach (var c in Camera.allCameras)
                    if (!c.orthographic && (c.cullingMask & (1 << _layer)) != 0) { _cam = c; break; }
                if (_cam == null)
                    foreach (var c in Camera.allCameras) if (!c.orthographic) { _cam = c; break; }
                if (_cam == null) return;
            }
            Vector3 pos = transform.position;
            Vector3 dir = _cam.transform.position - pos;                  // face the eye
            dir.y = 0f;                                                   // keep upright (flame/ghost points up)
            if (dir.sqrMagnitude > 1e-6f)
                transform.rotation = Quaternion.LookRotation(dir, Vector3.up);   // quad's +Z faces the camera

            if (_flipToMotion)
            {
                if (_hasLast)
                {
                    float screenX = Vector3.Dot(pos - _lastPos, _cam.transform.right);   // >0 = moving screen-right
                    if (Mathf.Abs(screenX) > 1e-3f)
                    {
                        var s = transform.localScale;
                        float m = Mathf.Abs(s.x);
                        s.x = screenX >= 0f ? m : -m;   // lead with the head
                        transform.localScale = s;
                    }
                }
                _lastPos = pos;
                _hasLast = true;
            }
        }
    }
}
