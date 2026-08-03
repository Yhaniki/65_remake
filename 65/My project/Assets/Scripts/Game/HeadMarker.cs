using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// The locally-controlled dancer's nameplate: an animated downward arrow (UI/ARROW, the rainbow
    /// .AN cycle) above the player's name. It PROJECTS a point a fixed WORLD distance above the head
    /// bone through the gameplay camera. In 3D gameplay the arrow + name are camera-facing world geometry
    /// rendered by SceneCam, so foreground dancers depth-occlude a rear name pixel by pixel. The legacy 2D
    /// fallback still draws at the projected HUD point. Both paths keep a constant on-screen size.
    ///
    /// sortingOrder is set BELOW the note board (which is −30) so the board occludes the marker where
    /// they overlap — yet still above the opaque RenderTexture backdrop, so it stays over the dancer.
    /// Runs at DefaultExecutionOrder(100) — AFTER the skeleton has posed and the head anchor moved.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public sealed class HeadMarker : MonoBehaviour
    {
        public System.Func<Vector3> AnchorGetter;   // head-bone world position (in the scene camera's space)
        public System.Func<Camera> CamGetter;        // the gameplay camera used to PROJECT the head to screen

        public float upWorld = 20f;       // screen-up offset, in WORLD-unit-equivalents (scales with depth)
        public float nameFontPx = 22f;    // name size (design px, constant on-screen)
        public float arrowDesignW = 20f;  // arrow width (design px)
        public float arrowGapPx = 3f;     // gap between the name top and the arrow bottom (design px)
        public float frameMs = 200f;      // arrow animation: per-frame hold
        public float depthBiasWorld = 0f; // optional camera-ward bias; zero keeps the name at its owner's true depth

        // draw order: behind the note board (−30) but above the opaque scene backdrop.
        private const int NameOrder = -31, ArrowOrder = -33, Zdepth = 10;

        private SpriteRenderer _arrow;
        private Label3D _name;
        private Sprite[] _arrowFrames;
        private float _arrowStart;
        private bool _depthTestedWorld;

        public bool DepthTestedWorld => _depthTestedWorld;
        public GameObject NameRootForTest => _name != null ? _name.root : null;

        /// <summary>Build the arrow + name. <paramref name="arrowFrames"/> is the ARROW.AN colour cycle;
        /// null/empty hides it. World mode places every renderer on <paramref name="worldLayer"/>.</summary>
        public void Init(Sprite[] arrowFrames, string playerName, bool depthTestedWorld = false, int worldLayer = 0)
        {
            _arrowFrames = arrowFrames;
            _arrowStart = Time.time;
            _depthTestedWorld = depthTestedWorld;
            int layer = depthTestedWorld ? worldLayer : 0;
            gameObject.layer = layer;

            var ag = new GameObject("Arrow");
            ag.layer = layer;
            ag.transform.SetParent(transform, false);
            _arrow = ag.AddComponent<SpriteRenderer>();   // Sprites/Default = alpha blend
            if (arrowFrames != null && arrowFrames.Length > 0) _arrow.sprite = arrowFrames[0];
            _arrow.sortingOrder = ArrowOrder;
            if (depthTestedWorld) _arrow.sharedMaterial = TextStyles.DepthSpriteMaterial();

            _name = TextStyles.NewLabel("Name", TextStyles.Style.HeadName, NameOrder, nameFontPx,
                                        TextAnchor.MiddleCenter, layer, depthTested: depthTestedWorld);
            _name.Text = playerName ?? string.Empty;
        }

        public void SetName(string playerName) { if (_name != null) _name.Text = playerName ?? string.Empty; }

        /// <summary>
        /// Promote a marker created before SceneCam existed into the scene-depth path. This is intentionally
        /// idempotent: gameplay may know the camera track before the scene camera itself has finished loading.
        /// </summary>
        public void EnableDepthTestedWorld(int worldLayer)
        {
            _depthTestedWorld = true;
            gameObject.layer = worldLayer;
            if (_arrow != null)
            {
                _arrow.gameObject.layer = worldLayer;
                _arrow.sharedMaterial = TextStyles.DepthSpriteMaterial();
            }
            if (_name != null) _name.EnableDepthTestedWorld(worldLayer);
        }

        /// <summary>
        /// 組隊局:把名字染成那一隊的顏色(<see cref="TeamColors"/>);沒組隊就維持官方原本的乳白。
        ///
        /// **只改字面、黑邊不動** —— 那圈黑邊是這個名字在任何背景上都讀得到的唯一原因,
        /// 跟著換色只會讓深色隊(藍)的名字糊進舞台。
        /// </summary>
        public void SetTeamColor(int team)
        {
            if (_name == null) return;
            Color face;
            if (!TeamColors.TryFor(team, out face)) return;   // 沒組隊 → 保持 HeadName 樣式原本的乳白
            _name.SetColors(face, Color.black);
        }

        /// <summary>Hide the arrow + name and STOP tracking (the name label is a separate root object that LateUpdate
        /// re-shows every frame, so disabling this component alone wouldn't hide it). Used when the result panel opens.</summary>
        public void Hide()
        {
            if (_arrow != null) _arrow.enabled = false;
            if (_name != null) _name.SetActive(false);
            enabled = false;   // stop LateUpdate from re-enabling them
        }

        private void OnDestroy()
        {
            // Label3D deliberately owns a separate scene root (it is not parented under this component), so normal
            // GameObject hierarchy destruction cannot clean it. Hide first to avoid a one-frame orphan in play mode.
            if (_name == null || _name.root == null) return;
            _name.SetActive(false);
            if (Application.isPlaying) Destroy(_name.root);
            else DestroyImmediate(_name.root);
            _name = null;
        }

        /// <summary>Place a constant-design-size billboard on the same view ray as <paramref name="anchorWorld"/>.
        /// Perspective gameplay reuses the room bubble's tested projection math; the orthographic branch keeps the
        /// old avatar-debug path usable. The returned plane is rendered by the scene camera and therefore shares its
        /// depth buffer with every dancer.</summary>
        public static RoomBubbleWorldAnchor.BubblePlane SolveWorldPlane(Camera cam, Vector3 anchorWorld, float depthBias)
        {
            if (cam == null) return default;
            depthBias = Mathf.Max(0f, depthBias);
            if (!cam.orthographic)
                return RoomBubbleWorldAnchor.Solve(cam.transform.position, cam.transform.forward,
                                                    cam.projectionMatrix.m11, cam.nearClipPlane,
                                                    anchorWorld, depthBias, SdoLayout.Height);

            var plane = default(RoomBubbleWorldAnchor.BubblePlane);
            float depth = Vector3.Dot(anchorWorld - cam.transform.position, cam.transform.forward);
            float minDepth = cam.nearClipPlane + 1f;
            if (!(depth > minDepth) || !(cam.orthographicSize > 0f)) return plane;
            float appliedBias = Mathf.Min(depthBias, depth - minDepth);
            plane.Valid = true;
            plane.Position = anchorWorld - cam.transform.forward * appliedBias;
            plane.Scale = 2f * cam.orthographicSize / SdoLayout.Height;
            return plane;
        }

        private void LateUpdate()
        {
            Camera cam = CamGetter != null ? CamGetter() : null;
            if (cam == null || AnchorGetter == null) return;

            Vector3 headW = AnchorGetter();
            if (_depthTestedWorld)
            {
                PlaceDepthTestedWorld(cam, headW);
                return;
            }
            Vector3 vp = cam.WorldToViewportPoint(headW);
            bool visible = vp.z > 0f && vp.x > -0.1f && vp.x < 1.1f;   // in front of the camera and roughly on-screen
            if (_arrow != null) _arrow.enabled = visible && _arrow.sprite != null;
            if (_name != null) _name.SetActive(visible);
            if (!visible) return;

            // px-per-world at the head's depth, measured along the camera's RIGHT axis — which is ALWAYS
            // perpendicular to the view, so it never foreshortens. (Offsetting along world-up instead collapsed
            // to ~0 in a top-down shot, dropping the name onto the head.) We then push the name UP in screen
            // space by upWorld×this, giving a consistent gap above the head at any camera angle/zoom.
            Vector3 vpRight = cam.WorldToViewportPoint(headW + cam.transform.right);
            float pxPerWorld = Mathf.Abs(vpRight.x - vp.x) * SdoLayout.Width;

            float hx = vp.x * SdoLayout.Width;
            float nameY = (1f - vp.y) * SdoLayout.Height - upWorld * pxPerWorld;   // up on screen

            if (_name != null)
            {
                _name.PxSize = nameFontPx;
                _name.Position = SdoLayout.ToWorld(hx, nameY, -Zdepth);
            }

            if (_arrow != null && _arrowFrames != null && _arrowFrames.Length > 0)
            {
                int f = (int)((Time.time - _arrowStart) * 1000f / Mathf.Max(1f, frameMs)) % _arrowFrames.Length;
                _arrow.sprite = _arrowFrames[f];
                var b = _arrow.sprite.bounds.size;
                float s = b.x > 1e-4f ? arrowDesignW / b.x : 1f;   // fit to arrowDesignW, keep aspect
                _arrow.transform.localScale = new Vector3(s, s, 1f);
                float arrowY = nameY - nameFontPx * 0.5f - arrowGapPx - b.y * s * 0.5f;
                _arrow.transform.position = SdoLayout.ToWorld(hx, arrowY, -Zdepth);
            }
        }

        private void PlaceDepthTestedWorld(Camera cam, Vector3 headWorld)
        {
            // Camera-up is perpendicular to the view ray, so this is exactly the old screen-up offset expressed
            // in world units. It stays stable in tilted/top-down director shots without using global Vector3.up.
            Vector3 anchorWorld = headWorld + cam.transform.up * upWorld;
            Vector3 vp = cam.WorldToViewportPoint(anchorWorld);
            var plane = SolveWorldPlane(cam, anchorWorld, depthBiasWorld);
            bool visible = plane.Valid && vp.z > 0f && vp.x > -0.1f && vp.x < 1.1f;

            if (_arrowFrames != null && _arrowFrames.Length > 0)
            {
                int frame = (int)((Time.time - _arrowStart) * 1000f / Mathf.Max(1f, frameMs)) % _arrowFrames.Length;
                _arrow.sprite = _arrowFrames[frame];
            }
            if (_arrow != null) _arrow.enabled = visible && _arrow.sprite != null;
            if (_name != null) _name.SetActive(visible);
            if (!visible) return;

            if (_name != null)
            {
                _name.PxSize = nameFontPx;
                var t = _name.root.transform;
                t.SetPositionAndRotation(plane.Position, cam.transform.rotation);
                t.localScale = Vector3.one * plane.Scale;
            }

            if (_arrow != null && _arrow.sprite != null)
            {
                Vector2 size = _arrow.sprite.bounds.size;
                float spriteScale = size.x > 1e-4f ? arrowDesignW / size.x : 1f;
                float arrowAboveName = nameFontPx * 0.5f + arrowGapPx + size.y * spriteScale * 0.5f;
                _arrow.transform.SetPositionAndRotation(
                    plane.Position + cam.transform.up * (arrowAboveName * plane.Scale),
                    cam.transform.rotation);
                float worldScale = spriteScale * plane.Scale;
                _arrow.transform.localScale = new Vector3(worldScale, worldScale, 1f);
            }
        }
    }
}
