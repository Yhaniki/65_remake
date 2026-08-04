using System.Collections.Generic;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Spring-bone (VRM / DynamicBone-style) sway for an MMD model's physics bones — the hair / skirt / tie / breast
    /// chains that MMD ships as a full Bullet rigid-body + joint rig. This is the standard, performant approximation:
    /// each dynamic bone's TAIL is a verlet particle pulled toward its rigid-follow rest by <see cref="Stiffness"/>,
    /// carried by inertia (minus <see cref="Drag"/>), and sagged by <see cref="Gravity"/>; the bone then aims at the
    /// settled tail. Kinematic ancestors (posed by the retarget) drive each chain. Runs AFTER <see cref="MmdAvatar"/>
    /// (execution order 200 &gt; 100) so bone HEADS are already posed and we only swing the tails.
    ///
    /// Not simulated: inter-bone collision (hair may pass through the body) — a follow-up; sway is the priority.
    /// </summary>
    [DefaultExecutionOrder(200)]
    public sealed class MmdSpringBones : MonoBehaviour
    {
        public float Stiffness = 0.12f;   // pull the tail back to its rigid-follow rest each step (0..1) — low = gravity hangs it, high = follows the body / trails less
        public float Drag = 0.55f;         // velocity damping each step (0..1)
        public float Gravity = 0.05f;      // downward tail displacement per step, in WORLD units
        public float BaseGravity = 0.05f;  // avatar-scaled default (Gravity = BaseGravity × user multiplier)
        public float ColliderMul = 1f;     // body-collider radius multiplier (live-tunable)

        private Transform[] _bone;
        private Transform[] _colA, _colB;  // capsule endpoints (sphere when A==B): torso/hip/leg colliders
        private float[] _colR;             // base world radius per collider
        private int[] _order;              // physics bones, parent-first
        private Vector3[] _restDirLocal;   // rest tail direction in the bone's local frame (unit)
        private float[] _len;              // rest tail length (world)
        private Vector3[] _prevTail, _curTail;   // world verlet state (indexed by bone)
        private Vector3[] _lastHead;             // previous-frame head (teleport detection)
        private bool _ready;
        private bool _needsSeed = true;          // seed the verlet state on the first posed frame (avoid a bind-pose fling)
        private float _acc;                      // fixed-timestep accumulator
        private const float FixedDt = 1f / 60f;

        /// <summary>Build a spring-bone sim for <paramref name="pmx"/>'s physics bones under <paramref name="host"/>.
        /// Returns null (adds nothing) if the model has no physics bones.</summary>
        public static MmdSpringBones Attach(GameObject host, Transform[] bones, int[] parent, PmxLoader pmx, float unitScale, Transform mmdRoot)
        {
            if (pmx == null || pmx.PhysicsBones == null || pmx.PhysicsBones.Count == 0) return null;
            var sb = host.AddComponent<MmdSpringBones>();
            sb._bone = bones;
            sb.BaseGravity = Mathf.Max(unitScale, 1e-3f) * 0.05f;   // scale the default sag to the avatar size
            sb.Gravity = sb.BaseGravity;
            sb.Build(parent, pmx, unitScale);
            return sb.Ready ? sb : null;
        }

        public bool Ready => _ready;
        /// <summary>Live tune: gravity is BaseGravity × <paramref name="gravMul"/> (avatar-scale independent).</summary>
        public void SetTuning(float stiffness, float drag, float gravMul) { Stiffness = stiffness; Drag = drag; Gravity = BaseGravity * gravMul; }
        /// <summary>Body colliders that keep the hair/skirt tails out of the body: a capsule between a[c] and b[c]
        /// (a sphere when a[c]==b[c]) of radius r[c].</summary>
        public void SetColliders(Transform[] a, Transform[] b, float[] r) { _colA = a; _colB = b; _colR = r; }

        private void Build(int[] parent, PmxLoader pmx, float unitScale)
        {
            int bc = pmx.Bones.Count;
            var children = new List<int>[bc];
            for (int i = 0; i < bc; i++) { int p = parent[i]; if (p >= 0 && p < bc) (children[p] ?? (children[p] = new List<int>())).Add(i); }

            _restDirLocal = new Vector3[bc]; _len = new float[bc];
            var list = new List<int>();
            foreach (int i in pmx.PhysicsBones)
            {
                if (i < 0 || i >= bc || _bone[i] == null) continue;
                // tail = first child (its head); else continue the incoming direction from the parent.
                int child = (children[i] != null && children[i].Count > 0) ? children[i][0] : -1;
                Vector3 tailLocal;
                if (child >= 0) tailLocal = pmx.Bones[child].Position - pmx.Bones[i].Position;
                else if (parent[i] >= 0 && parent[i] < bc) tailLocal = pmx.Bones[i].Position - pmx.Bones[parent[i]].Position;
                else continue;
                if (tailLocal.sqrMagnitude < 1e-10f) continue;
                _restDirLocal[i] = tailLocal.normalized;
                _len[i] = tailLocal.magnitude * unitScale;
                list.Add(i);
            }
            // parent-first (by hierarchy depth) so a parent physics bone is solved before its child
            var depth = new int[bc];
            for (int i = 0; i < bc; i++) { int d = 0, p = parent[i], g = 0; while (p >= 0 && g++ < bc) { d++; p = parent[p]; } depth[i] = d; }
            list.Sort((a, b) => depth[a].CompareTo(depth[b]));
            _order = list.ToArray();

            _prevTail = new Vector3[bc]; _curTail = new Vector3[bc]; _lastHead = new Vector3[bc];
            _needsSeed = true;   // seeded on the first LateUpdate, when heads are POSED (not the bind pose) → no fling
            _ready = _order.Length > 0;
        }

        private Vector3 RestTail(int i)
        {
            Transform b = _bone[i];
            Quaternion prot = b.parent != null ? b.parent.rotation : Quaternion.identity;   // rest world rot (identity local)
            return b.position + (prot * _restDirLocal[i]) * _len[i];
        }

        private void LateUpdate()
        {
            if (!_ready) return;
            if (_needsSeed) { SeedRest(); _needsSeed = false; _acc = 0f; return; }   // heads are now posed → settle at the real pose

            _acc += Time.deltaTime;                       // fixed-timestep integration → frame-rate-independent sway
            int steps = 0;
            while (_acc >= FixedDt && steps++ < 4) { Integrate(); _acc -= FixedDt; }   // cap steps (no spiral of death)
            Apply();                                      // aim bones from the current tails every frame
        }

        private void OnEnable() { _needsSeed = true; _acc = 0f; }   // re-settle after a hide/show (no stale-state fling)

        private void SeedRest()
        {
            foreach (int i in _order) { Vector3 t = RestTail(i); _prevTail[i] = t; _curTail[i] = t; _lastHead[i] = _bone[i].position; }
        }

        // Advance the verlet tails one fixed step (does not touch bone rotations — that's Apply).
        private void Integrate()
        {
            Vector3 grav = Vector3.down * Gravity;
            float keep = 1f - Mathf.Clamp01(Drag), stiff = Mathf.Clamp01(Stiffness);
            for (int k = 0; k < _order.Length; k++)
            {
                int i = _order[k];
                Transform b = _bone[i];
                Vector3 head = b.position;
                // teleport guard: a big one-frame head jump (scene warp) re-settles this bone instead of flinging it.
                if ((head - _lastHead[i]).sqrMagnitude > _len[i] * _len[i] * 9f) { Vector3 r = RestTail(i); _prevTail[i] = r; _curTail[i] = r; }
                _lastHead[i] = head;
                Quaternion prot = b.parent != null ? b.parent.rotation : Quaternion.identity;
                Vector3 restDirWorld = prot * _restDirLocal[i];
                Vector3 restTail = head + restDirWorld * _len[i];
                Vector3 cur = _curTail[i], prev = _prevTail[i];
                Vector3 next = cur + (cur - prev) * keep + (restTail - cur) * stiff + grav;   // inertia + spring + gravity
                Vector3 dir = next - head;
                if (dir.sqrMagnitude < 1e-10f) { dir = restDirWorld; next = restTail; }
                next = head + dir.normalized * _len[i];                                        // keep bone length
                // body collision: push the tail out of any torso/hip/leg capsule it penetrates (the aim uses direction
                // only, so the resulting length change is harmless).
                if (_colA != null)
                    for (int c = 0; c < _colA.Length; c++)
                    {
                        if (_colA[c] == null || _colB[c] == null) continue;
                        Vector3 pa = _colA[c].position, ab = _colB[c].position - pa;
                        float abl2 = ab.sqrMagnitude;
                        Vector3 closest = abl2 < 1e-8f ? pa : pa + ab * Mathf.Clamp01(Vector3.Dot(next - pa, ab) / abl2);
                        Vector3 dd = next - closest; float r = _colR[c] * ColliderMul, m2 = dd.sqrMagnitude;
                        if (m2 < r * r && m2 > 1e-8f) next = closest + dd * (r / Mathf.Sqrt(m2));
                    }
                _prevTail[i] = cur; _curTail[i] = next;
            }
        }

        // Aim each bone from its head toward its settled tail (every frame, regardless of how many fixed steps ran).
        private void Apply()
        {
            for (int k = 0; k < _order.Length; k++)
            {
                int i = _order[k];
                Transform b = _bone[i];
                Quaternion prot = b.parent != null ? b.parent.rotation : Quaternion.identity;
                Vector3 restDirWorld = prot * _restDirLocal[i];
                Vector3 dir = _curTail[i] - b.position;
                if (dir.sqrMagnitude > 1e-10f) b.rotation = Quaternion.FromToRotation(restDirWorld, dir) * prot;
            }
        }
    }
}
