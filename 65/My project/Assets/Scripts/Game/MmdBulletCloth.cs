using System.Collections.Generic;
using UnityEngine;
using Sdo.MmdPhysics;

namespace Sdo.Game
{
    /// <summary>
    /// Runs the model's hair/skirt/tie with <see cref="MmdRigidWorld"/> — MMD's OWN Bullet rigid-body physics, fed
    /// the authored numbers directly — as an alternative to converting them into Magica Cloth parameters
    /// (<see cref="MmdMagicaCloth"/>). Which one a body uses is <c>config.ini [Mmd] mmdPhysicsEngine</c>.
    ///
    /// <b>Nothing here is tuned.</b> Mass, linear/angular damping, per-axis joint limits, rotation springs, collision
    /// shapes and the author's collision groups all go in as written. That is the entire point: the Magica path can
    /// only approximate them (PBD has no mass, no angular velocity, no forces), and every knob that approximation
    /// needs — restoration stiffness, depth inertia, damping remapping, angle slack — has no counterpart in the .pmx
    /// and had to be calibrated by hand.
    ///
    /// <b>Space.</b> The solver works in the model's own units, exactly like MMD: gravity is −9.8×10 model units and
    /// the numbers in the file mean what they meant to the author, regardless of how tall this dancer scaled the
    /// model. Only the boundary converts: the driven (kinematic) bones are read into model space each frame, and the
    /// simulated bones are written back out to world space.
    ///
    /// <b>Timing.</b> MMD runs 60 fps frames of 2 substeps; the game's frame rate is whatever it is, so time is
    /// accumulated and stepped in fixed 1/60 frames (capped, so a hitch cannot make the cloth explode or eat the
    /// frame budget catching up).
    /// </summary>
    public sealed class MmdBulletCloth
    {
        private MmdRigidWorld _world;
        private Transform _root;                 // the rig root: model space ⇄ world
        private Transform[] _bone;
        private int[] _bodyBone;                 // body → bone index (-1 = none)
        private bool[] _bodyDynamic;
        private int[] _writeOrder;               // dynamic bodies, parents before children
        private M3[] _restInv;                   // inverse authored body rotation: bone rot = bodyRot · restInv
        private float _accum;
        private const float FrameDt = 1f / (float)MmdRigidWorld.Fps;
        private const int MaxFramesPerTick = 3;  // a hitch must not turn into a long catch-up

        public bool Any => _world != null;
        public int BodyCount => _world?.BodyCount ?? 0;
        public int JointCount => _world?.JointCount ?? 0;
        public int ContactCount => _world?.ContactCount ?? 0;
        public bool Enabled = true;

        /// <summary>Build from the parsed model. Returns null when it has no physics section (nothing to simulate).</summary>
        public static MmdBulletCloth Setup(Transform root, Transform[] bone, PmxLoader pmx)
        {
            if (pmx?.RigidBodies == null || pmx.RigidBodies.Count == 0) return null;
            var m = new MmdBulletCloth();
            try { m.Build(root, bone, pmx); }
            catch (System.Exception e)
            {
                Debug.LogWarning("[mmd] bullet cloth setup failed: " + e.Message + "\n" + e.StackTrace);
                return null;
            }
            return m.Any ? m : null;
        }

        private void Build(Transform root, Transform[] bone, PmxLoader pmx)
        {
            _root = root; _bone = bone;
            int n = pmx.RigidBodies.Count;
            var bodies = new List<MmdRigidWorld.Body>(n);
            _bodyBone = new int[n]; _bodyDynamic = new bool[n]; _restInv = new M3[n];

            for (int i = 0; i < n; i++)
            {
                var rb = pmx.RigidBodies[i];
                var bonePos = (rb.Bone >= 0 && rb.Bone < pmx.Bones.Count) ? pmx.Bones[rb.Bone].Position : Vector3.zero;
                bodies.Add(new MmdRigidWorld.Body
                {
                    Name = rb.Name,
                    Bone = rb.Bone,
                    BonePos = V(bonePos),
                    Group = rb.Group,
                    Mask = rb.Mask,
                    Shape = rb.Shape,
                    Size = V(rb.Size),
                    Pos0 = V(rb.Position),
                    RotEuler = V(rb.Rotation),
                    Mass = rb.Mass,
                    LinDamp = rb.LinearDamp,
                    AngDamp = rb.AngularDamp,
                    Mode = rb.Mode,
                });
                _bodyBone[i] = rb.Bone;
                _bodyDynamic[i] = rb.Mode != 0;
                _restInv[i] = M3.FromMmdEuler(V(rb.Rotation)).Transposed();
            }

            var joints = new List<MmdRigidWorld.Joint>(pmx.Joints6Dof.Count);
            foreach (var j in pmx.Joints6Dof)
                joints.Add(new MmdRigidWorld.Joint
                {
                    A = j.BodyA, B = j.BodyB,
                    Pos = V(j.Position), RotEuler = V(j.Rotation),
                    PosLo = V(j.PosLower), PosHi = V(j.PosUpper),
                    RotLo = V(j.RotLower), RotHi = V(j.RotUpper),
                    RotSpring = V(j.RotSpring),
                });

            _world = new MmdRigidWorld(bodies, joints);

            // write simulated bones out parents-first: a child's world pose is only meaningful once its parent is set
            var order = new List<int>();
            for (int i = 0; i < n; i++)
                if (_bodyDynamic[i] && _bodyBone[i] >= 0 && _bodyBone[i] < bone.Length && bone[_bodyBone[i]] != null)
                    order.Add(i);
            order.Sort((a, b) => _bodyBone[a].CompareTo(_bodyBone[b]));   // PMX guarantees parents come first
            _writeOrder = order.ToArray();

            SdoLog.Note("mmd", $"  bullet cloth: {n} bodies ({order.Count} simulated), {_world.JointCount} joints, " +
                               $"{_world.ColorCount} solver colours — authored values, no conversion");
        }

        /// <summary>Advance the simulation and write the result onto the bones. Call after the retarget has posed the
        /// driven bones for this frame (the cloth hangs off them).</summary>
        public void Tick(float deltaTime)
        {
            if (_world == null || !Enabled) return;

            // driven bones → model space, once per frame (three.js updates kinematic bodies per frame too)
            var w2m = _root.worldToLocalMatrix;
            var rootInv = Quaternion.Inverse(_root.rotation);
            _world.DriveKinematic(b =>
            {
                if (b < 0 || b >= _bone.Length || _bone[b] == null) return (M3.Identity, V3.Zero);
                var t = _bone[b];
                return (Mat(rootInv * t.rotation), V(w2m.MultiplyPoint3x4(t.position)));
            }, FrameDt);

            _accum += Mathf.Min(deltaTime, FrameDt * MaxFramesPerTick);
            int steps = 0;
            while (_accum >= FrameDt && steps < MaxFramesPerTick) { _world.StepFrame(); _accum -= FrameDt; steps++; }
            if (steps == 0) return;

            var m2w = _root.localToWorldMatrix;
            var rootRot = _root.rotation;
            for (int k = 0; k < _writeOrder.Length; k++)
            {
                int i = _writeOrder[k];
                var t = _bone[_bodyBone[i]];
                if (t == null) continue;
                // the authored body carries the bone at a fixed offset and a fixed relative rotation, so the bone's
                // pose falls straight out of the body's
                var boneRot = _world.OrientationOf(i) * _restInv[i];
                t.SetPositionAndRotation(m2w.MultiplyPoint3x4(Vec(_world.BonePositionOf(i))),
                                         rootRot * Quat(boneRot));
            }
        }

        /// <summary>Put every simulated bone back where the animation says it is (used when the cloth is switched off
        /// or the body is hidden, so it does not resume from a stale pose).</summary>
        public void Reset() { _accum = 0f; _world?.DriveKinematic(null, 0.0); }

        // ---- conversions (Unity float ⇄ solver double) ----
        private static V3 V(Vector3 v) => new V3(v.x, v.y, v.z);
        private static Vector3 Vec(V3 v) => new Vector3((float)v.X, (float)v.Y, (float)v.Z);

        private static M3 Mat(Quaternion q)
        {
            var m = Matrix4x4.Rotate(q);
            return new M3
            {
                M00 = m.m00, M01 = m.m01, M02 = m.m02,
                M10 = m.m10, M11 = m.m11, M12 = m.m12,
                M20 = m.m20, M21 = m.m21, M22 = m.m22,
            };
        }

        private static Quaternion Quat(M3 m)
        {
            var q = Q.FromMatrix(m);
            return new Quaternion((float)q.X, (float)q.Y, (float)q.Z, (float)q.W);
        }
    }
}
