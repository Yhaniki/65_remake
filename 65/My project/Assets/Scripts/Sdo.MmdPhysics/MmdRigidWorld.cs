using System;
using System.Collections.Generic;

namespace Sdo.MmdPhysics
{
    /// <summary>
    /// MMD's cloth physics, run as what it actually IS: Bullet rigid bodies joined by 6-DOF spring constraints.
    ///
    /// <b>Why this exists.</b> The shipped path converts the model's rigid bodies/joints into Magica Cloth (PBD)
    /// parameters. That conversion cannot be made correct, only close: PBD has no mass, no angular velocity and no
    /// forces — it moves particles to where they should be and reads the velocity back out — so an authored joint
    /// LIMIT (which only acts at its boundary) becomes a position projection that pushes every substep, and a chain
    /// that Bullet settles in 1.9 s never stops ringing. Measured on the twintails: tip speed 2 s into the rest
    /// scenario 1.23 m/s vs Bullet's 0.05. No parameter fixes that; the model is simply not the same one.
    ///
    /// So: don't convert. The authored numbers ARE Bullet's numbers — mass, linear/angular damping, per-axis joint
    /// limits and rotation springs — and this runs them directly. Nothing here is tuned or calibrated.
    ///
    /// The semantics follow three.js MMDPhysics / saba (what every MMD player does):
    /// 60 fps frames × 2 substeps of 1/120, gravity −9.8×10 model units, kinematic (mode 0) bodies teleported from
    /// their bone once per FRAME, damping as btRigidBody's exponential per-substep decay, joint solve as sequential
    /// impulses with Bullet's default ERP 0.2 and 10 iterations, warm-started at 0.85.
    ///
    /// Verified against <c>tools/mmd_cloth_validate/ref_*.json</c> — the pybullet reconstruction that was already
    /// being used as ground truth for the Magica conversion. Same inputs, same scenarios, so "is the port right?"
    /// is a numeric question with an answer, not a matter of taste.
    /// </summary>
    public sealed class MmdRigidWorld
    {
        public const double Fps = 60.0;
        public const int Substeps = 2;
        public const double Dt = 1.0 / (Fps * Substeps);
        public const double Gravity = -9.8 * 10.0;   // three.js MMDPhysics world gravity, in model units
        public const double Erp = 0.2;               // Bullet solverInfo.m_erp
        public const int SolverIters = 10;           // Bullet numSolverIterations
        public const double WarmStart = 0.85;        // Bullet SOLVER_USE_WARMSTARTING
        public const double MaxV = 1000.0, MaxW = 200.0;   // safety clamps (never reached in a healthy run)

        // ---- authored input (exactly the PMX physics section; see MmdPhysicsData) ----
        public sealed class Body
        {
            public string Name;
            public int Bone = -1;
            public int Shape;             // 0 sphere, 1 box, 2 capsule
            public V3 Size;               // sphere(r,_,_) box(hx,hy,hz) capsule(r, cylinder-length, _)
            public V3 Pos0;               // rigid-body centre, model space
            public V3 RotEuler;           // radians, MMD euler
            public double Mass, LinDamp, AngDamp;
            public int Mode;              // 0 kinematic (follows the bone), 1/2 dynamic
            public byte Group; public ushort Mask;
            public V3 BonePos;            // the attached bone's rest position (model space)
        }

        public sealed class Joint
        {
            public int A, B;
            public V3 Pos, RotEuler;
            public V3 PosLo, PosHi, RotLo, RotHi, RotSpring;
        }

        // ---- runtime state ----
        private readonly List<Body> _src = new List<Body>();
        private V3[] _pos, _vel, _avel;
        private Q[] _rot;
        private M3[] _rot0;               // authored orientation (also the kinematic bodies' rest frame)
        private V3[] _pos0;
        private bool[] _dynamic;
        private double[] _invMass;
        private V3[] _invInertiaLocal;    // diagonal, body-local
        private double[] _linDampF, _angDampF;   // per-substep exponential factors

        // joints, flattened
        private int[] _ja, _jb;
        private M3[] _fA, _fB;            // joint frame in A-local / B-local
        private V3[] _aA, _aB;            // anchor in A-local / B-local
        private V3[] _rlo, _rhi, _rspr, _plo, _phi;
        private bool[] _linLocked;
        private V3[] _lamAng, _lamLin;    // warm-start accumulators
        private int[][] _colors;          // edge colouring → Gauss-Seidel without shared bodies inside a colour

        public int BodyCount => _pos.Length;
        public int JointCount => _ja.Length;
        public int ColorCount => _colors.Length;

        public V3 PositionOf(int body) => _pos[body];
        public M3 OrientationOf(int body) => M3.FromQuat(_rot[body]);
        public bool IsDynamic(int body) => _dynamic[body];

        /// <summary>World position of the BONE a body carries (what the reference recordings store).</summary>
        public V3 BonePositionOf(int body)
            => _pos[body] + M3.FromQuat(_rot[body]) * _boneOffset[body];
        private V3[] _boneOffset;

        public MmdRigidWorld(IList<Body> bodies, IList<Joint> joints)
        {
            int n = bodies.Count;
            _src.AddRange(bodies);
            _pos = new V3[n]; _vel = new V3[n]; _avel = new V3[n]; _rot = new Q[n];
            _rot0 = new M3[n]; _pos0 = new V3[n]; _dynamic = new bool[n];
            _invMass = new double[n]; _invInertiaLocal = new V3[n];
            _linDampF = new double[n]; _angDampF = new double[n]; _boneOffset = new V3[n];

            for (int i = 0; i < n; i++)
            {
                var b = bodies[i];
                _rot0[i] = M3.FromMmdEuler(b.RotEuler);
                _pos0[i] = b.Pos0;
                _pos[i] = b.Pos0;
                _rot[i] = Q.FromMatrix(_rot0[i]);
                _dynamic[i] = b.Mode != 0;
                _boneOffset[i] = _rot0[i].TMul(b.BonePos - b.Pos0);   // bone in body-local (rest rotation is identity)

                if (_dynamic[i])
                {
                    double m = Math.Max(b.Mass, 1e-9);
                    _invMass[i] = 1.0 / m;
                    var I = LocalInertia(b.Shape, b.Size, m);
                    _invInertiaLocal[i] = new V3(1.0 / Math.Max(I.X, 1e-9), 1.0 / Math.Max(I.Y, 1e-9), 1.0 / Math.Max(I.Z, 1e-9));
                    // btRigidBody: v *= (1-damp)^dt per substep, damp clamped to [0,1] (authored 2.0 → 1.0, i.e. gone
                    // within a second — that clamp is Bullet's, and it is how the author says "settle fast").
                    _linDampF[i] = Math.Pow(1.0 - Clamp01(b.LinDamp), Dt);
                    _angDampF[i] = Math.Pow(1.0 - Clamp01(b.AngDamp), Dt);
                }
                else
                {
                    _invMass[i] = 0.0;
                    _invInertiaLocal[i] = V3.Zero;      // infinite inertia
                    _linDampF[i] = 1.0; _angDampF[i] = 1.0;   // kinematic velocity must survive
                }
            }
            BuildJoints(joints);
        }

        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        /// <summary>Bullet's own calculateLocalInertia. The capsule case really is the bounding box — btCapsuleShape
        /// inherits btBoxShape's formula — so a "more correct" capsule tensor would move us AWAY from MMD.</summary>
        public static V3 LocalInertia(int shape, V3 size, double mass)
        {
            if (shape == 0)
            {
                double r = Math.Max(size.X, 1e-4);
                double i = 0.4 * mass * r * r;
                return new V3(i, i, i);
            }
            double hx, hy, hz;
            if (shape == 1) { hx = Math.Max(size.X, 1e-4); hy = Math.Max(size.Y, 1e-4); hz = Math.Max(size.Z, 1e-4); }
            else
            {
                double r = Math.Max(size.X, 1e-4), h = Math.Max(size.Y, 1e-4);
                hx = r; hy = r + h * 0.5; hz = r;         // Y-aligned capsule → its bounding box
            }
            double lx = 2 * hx, ly = 2 * hy, lz = 2 * hz;
            return new V3(mass / 12.0 * (ly * ly + lz * lz),
                          mass / 12.0 * (lx * lx + lz * lz),
                          mass / 12.0 * (lx * lx + ly * ly));
        }

        private void BuildJoints(IList<Joint> joints)
        {
            var ja = new List<int>(); var jb = new List<int>();
            var fA = new List<M3>(); var fB = new List<M3>();
            var aA = new List<V3>(); var aB = new List<V3>();
            var rlo = new List<V3>(); var rhi = new List<V3>(); var rspr = new List<V3>();
            var plo = new List<V3>(); var phi = new List<V3>(); var locked = new List<bool>();

            foreach (var j in joints)
            {
                if (j.A < 0 || j.B < 0 || j.A >= _pos.Length || j.B >= _pos.Length) continue;
                if (!_dynamic[j.A] && !_dynamic[j.B]) continue;      // both kinematic: no-op

                var tj = M3.FromMmdEuler(j.RotEuler);
                var RA0 = _rot0[j.A]; var RB0 = _rot0[j.B];
                ja.Add(j.A); jb.Add(j.B);
                fA.Add(RA0.Transposed() * tj);
                fB.Add(RB0.Transposed() * tj);
                aA.Add(RA0.TMul(j.Pos - _pos0[j.A]));
                aB.Add(RB0.TMul(j.Pos - _pos0[j.B]));
                rlo.Add(j.RotLo); rhi.Add(j.RotHi);
                rspr.Add(new V3(Math.Abs(j.RotSpring.X), Math.Abs(j.RotSpring.Y), Math.Abs(j.RotSpring.Z)));
                plo.Add(j.PosLo); phi.Add(j.PosHi);
                locked.Add(Math.Abs(j.PosLo.X) < 1e-9 && Math.Abs(j.PosHi.X) < 1e-9 &&
                           Math.Abs(j.PosLo.Y) < 1e-9 && Math.Abs(j.PosHi.Y) < 1e-9 &&
                           Math.Abs(j.PosLo.Z) < 1e-9 && Math.Abs(j.PosHi.Z) < 1e-9);
            }

            _ja = ja.ToArray(); _jb = jb.ToArray();
            _fA = fA.ToArray(); _fB = fB.ToArray();
            _aA = aA.ToArray(); _aB = aB.ToArray();
            _rlo = rlo.ToArray(); _rhi = rhi.ToArray(); _rspr = rspr.ToArray();
            _plo = plo.ToArray(); _phi = phi.ToArray(); _linLocked = locked.ToArray();
            _lamAng = new V3[_ja.Length]; _lamLin = new V3[_ja.Length];

            // Greedy edge colouring over bodies: inside one colour no two joints share a body, so applying a colour
            // in one pass IS a sequential (Gauss-Seidel) sweep — same convergence class as Bullet's solver, which is
            // what stops the closed skirt lattice from ringing.
            var used = new Dictionary<int, HashSet<int>>();
            var colorOf = new int[_ja.Length];
            int maxColor = 0;
            for (int k = 0; k < _ja.Length; k++)
            {
                if (!used.TryGetValue(_ja[k], out var ca)) used[_ja[k]] = ca = new HashSet<int>();
                if (!used.TryGetValue(_jb[k], out var cb)) used[_jb[k]] = cb = new HashSet<int>();
                int c = 0;
                while (ca.Contains(c) || cb.Contains(c)) c++;
                colorOf[k] = c; ca.Add(c); cb.Add(c);
                if (c > maxColor) maxColor = c;
            }
            var buckets = new List<int>[maxColor + 1];
            for (int c = 0; c <= maxColor; c++) buckets[c] = new List<int>();
            for (int k = 0; k < _ja.Length; k++) buckets[colorOf[k]].Add(k);
            _colors = new int[maxColor + 1][];
            for (int c = 0; c <= maxColor; c++) _colors[c] = buckets[c].ToArray();
        }

        /// <summary>Place every kinematic body from its bone's current world transform, exactly once per FRAME (what
        /// three.js does), and give it the finite-difference velocity so the constraint solve sees a moving anchor
        /// the way Bullet's kinematic motion state does. <paramref name="boneWorld"/> maps a bone index to its
        /// world rotation+position; pass null for the rest pose.</summary>
        public void DriveKinematic(Func<int, (M3 rot, V3 pos)> boneWorld, double frameDt)
        {
            for (int i = 0; i < _pos.Length; i++)
            {
                if (_dynamic[i]) continue;
                var b = _src[i];
                M3 rw; V3 pw;
                if (boneWorld == null) { rw = _rot0[i]; pw = _pos0[i]; }
                else
                {
                    var (br, bp) = boneWorld(b.Bone);
                    rw = br * _rot0[i];
                    pw = bp + br * (_pos0[i] - b.BonePos);
                }
                if (frameDt > 0)
                {
                    _vel[i] = (pw - _pos[i]) * (1.0 / frameDt);
                    _avel[i] = AngularFrom(M3.FromQuat(_rot[i]), rw, frameDt);
                }
                else { _vel[i] = V3.Zero; _avel[i] = V3.Zero; }
                _pos[i] = pw; _rot[i] = Q.FromMatrix(rw);
            }
        }

        private static V3 AngularFrom(M3 from, M3 to, double dt)
        {
            var dR = to * from.Transposed();
            double tr = (dR.M00 + dR.M11 + dR.M22 - 1.0) * 0.5;
            double ang = Math.Acos(Math.Max(-1.0, Math.Min(1.0, tr)));
            if (ang < 1e-9) return V3.Zero;
            var axis = new V3(dR.M21 - dR.M12, dR.M02 - dR.M20, dR.M10 - dR.M01);
            double n = axis.Length;
            return n > 1e-12 ? axis * (ang / (n * dt)) : V3.Zero;
        }

        /// <summary>One 60 fps frame = <see cref="Substeps"/> solver substeps.</summary>
        public void StepFrame()
        {
            for (int s = 0; s < Substeps; s++) Substep();
        }

        private void Substep()
        {
            int n = _pos.Length;
            // 1) btRigidBody damping, then gravity (Bullet applies gravity as a force before solving)
            for (int i = 0; i < n; i++)
            {
                if (!_dynamic[i]) continue;
                _vel[i] = _vel[i] * _linDampF[i];
                _avel[i] = _avel[i] * _angDampF[i];
                _vel[i] = _vel[i] + new V3(0, Gravity * Dt, 0);
            }

            // 2) per-body world inverse inertia + the joint frames/axes for this substep
            var R = new M3[n];
            var invIW = new M3[n];
            for (int i = 0; i < n; i++)
            {
                R[i] = M3.FromQuat(_rot[i]);
                invIW[i] = _dynamic[i] ? RotateDiag(R[i], _invInertiaLocal[i]) : default;
            }

            int nj = _ja.Length;
            var axes = new V3[nj * 3];
            var ieff = new double[nj * 3];
            var bias = new double[nj * 3];
            var active = new bool[nj * 3];
            var sprImp = new double[nj * 3];
            var rAw = new V3[nj]; var rBw = new V3[nj];
            var linAxis = new V3[nj * 3];
            var lbias = new double[nj * 3];
            var lact = new bool[nj * 3];
            var meff = new double[nj * 3];

            for (int k = 0; k < nj; k++)
            {
                int a = _ja[k], b = _jb[k];
                var RA = R[a] * _fA[k];
                var RB = R[b] * _fB[k];
                var rel = RA.Transposed() * RB;
                var theta = rel.ToEulerXyz();

                // btGeneric6DofConstraint::calculateAngleInfo axes
                var axis0B = RB.Col(0);
                var axis2A = RA.Col(2);
                var ax1 = V3.Cross(axis2A, axis0B).Normalized;
                var ax0 = V3.Cross(ax1, axis2A).Normalized;
                var ax2 = V3.Cross(axis0B, ax1).Normalized;
                axes[k * 3 + 0] = ax0; axes[k * 3 + 1] = ax1; axes[k * 3 + 2] = ax2;

                for (int c = 0; c < 3; c++)
                {
                    var ax = axes[k * 3 + c];
                    double ia = _dynamic[a] ? V3.Dot(ax, invIW[a] * ax) : 0.0;
                    double ib = _dynamic[b] ? V3.Dot(ax, invIW[b] * ax) : 0.0;
                    double sum = ia + ib;
                    ieff[k * 3 + c] = sum > 1e-12 ? 1.0 / sum : 0.0;

                    double th = theta[c], lo = _rlo[k][c], hi = _rhi[k][c];
                    double err = th - Math.Max(lo, Math.Min(hi, th));
                    bool act = Math.Abs(err) > 1e-12;
                    active[k * 3 + c] = act;
                    bias[k * 3 + c] = act ? -Erp * err / Dt : 0.0;
                    // rotation spring inside the allowed range: a torque impulse, applied once (it is a force,
                    // not a velocity target — that distinction is the whole difference from a PBD projection)
                    sprImp[k * 3 + c] = (!act && _rspr[k][c] > 0) ? -_rspr[k][c] * th * Dt : 0.0;
                }

                rAw[k] = R[a] * _aA[k];
                rBw[k] = R[b] * _aB[k];
                var d = RA.TMul((_pos[b] + rBw[k]) - (_pos[a] + rAw[k]));
                for (int c = 0; c < 3; c++)
                {
                    var nAx = RA.Col(c);
                    linAxis[k * 3 + c] = nAx;
                    double lo = _linLocked[k] ? 0.0 : _plo[k][c];
                    double hi = _linLocked[k] ? 0.0 : _phi[k][c];
                    double lerr = d[c] - Math.Max(lo, Math.Min(hi, d[c]));
                    bool la = Math.Abs(lerr) > 1e-12;
                    lact[k * 3 + c] = la;
                    lbias[k * 3 + c] = la ? -Erp * lerr / Dt : 0.0;

                    var cA = V3.Cross(rAw[k], nAx);
                    var cB = V3.Cross(rBw[k], nAx);
                    double kA = _dynamic[a] ? V3.Dot(cA, invIW[a] * cA) : 0.0;
                    double kB = _dynamic[b] ? V3.Dot(cB, invIW[b] * cB) : 0.0;
                    double inv = _invMass[a] + _invMass[b] + kA + kB;
                    meff[k * 3 + c] = 1.0 / Math.Max(inv, 1e-12);
                }
            }

            var dv = new V3[n]; var dw = new V3[n];

            // springs first (single application), then Bullet's warm start
            for (int k = 0; k < nj; k++)
            {
                int a = _ja[k], b = _jb[k];
                var tau = V3.Zero;
                for (int c = 0; c < 3; c++) if (sprImp[k * 3 + c] != 0) tau = tau + axes[k * 3 + c] * sprImp[k * 3 + c];
                if (tau.X != 0 || tau.Y != 0 || tau.Z != 0) ApplyTorque(dw, invIW, a, b, tau);

                var lamA = V3.Zero; var lamL = V3.Zero;
                for (int c = 0; c < 3; c++)
                {
                    double wa = active[k * 3 + c] ? WarmStart * _lamAng[k][c] : 0.0;
                    double wl = lact[k * 3 + c] ? WarmStart * _lamLin[k][c] : 0.0;
                    lamA = Set(lamA, c, wa); lamL = Set(lamL, c, wl);
                }
                _lamAng[k] = lamA; _lamLin[k] = lamL;

                var tau0 = V3.Zero;
                for (int c = 0; c < 3; c++) tau0 = tau0 + axes[k * 3 + c] * lamA[c];
                ApplyTorque(dw, invIW, a, b, tau0);

                var j0 = V3.Zero;
                for (int c = 0; c < 3; c++) j0 = j0 + linAxis[k * 3 + c] * lamL[c];
                ApplyImpulse(dv, dw, invIW, a, b, rAw[k], rBw[k], j0);
            }

            // 3) Gauss-Seidel sweeps, colour by colour
            for (int it = 0; it < SolverIters; it++)
            {
                foreach (var colour in _colors)
                {
                    foreach (int k in colour)
                    {
                        int a = _ja[k], b = _jb[k];
                        var wrelBase = (_avel[b] + dw[b]) - (_avel[a] + dw[a]);
                        var lam = V3.Zero;
                        for (int c = 0; c < 3; c++)
                        {
                            if (!active[k * 3 + c]) continue;
                            double wrel = V3.Dot(axes[k * 3 + c], wrelBase);
                            lam = Set(lam, c, ieff[k * 3 + c] * (bias[k * 3 + c] - wrel));
                        }
                        if (lam.X != 0 || lam.Y != 0 || lam.Z != 0)
                        {
                            _lamAng[k] = _lamAng[k] + lam;
                            var tau = V3.Zero;
                            for (int c = 0; c < 3; c++) tau = tau + axes[k * 3 + c] * lam[c];
                            ApplyTorque(dw, invIW, a, b, tau);
                        }

                        var vA = (_vel[a] + dv[a]) + V3.Cross(_avel[a] + dw[a], rAw[k]);
                        var vB = (_vel[b] + dv[b]) + V3.Cross(_avel[b] + dw[b], rBw[k]);
                        var vrelBase = vB - vA;
                        var lamL = V3.Zero;
                        for (int c = 0; c < 3; c++)
                        {
                            if (!lact[k * 3 + c]) continue;
                            double vrel = V3.Dot(linAxis[k * 3 + c], vrelBase);
                            lamL = Set(lamL, c, meff[k * 3 + c] * (lbias[k * 3 + c] - vrel));
                        }
                        if (lamL.X != 0 || lamL.Y != 0 || lamL.Z != 0)
                        {
                            _lamLin[k] = _lamLin[k] + lamL;
                            var J = V3.Zero;
                            for (int c = 0; c < 3; c++) J = J + linAxis[k * 3 + c] * lamL[c];
                            ApplyImpulse(dv, dw, invIW, a, b, rAw[k], rBw[k], J);
                        }
                    }
                }
            }

            // 4) integrate
            for (int i = 0; i < n; i++)
            {
                if (!_dynamic[i]) continue;
                _vel[i] = ClampV(_vel[i] + dv[i], MaxV);
                _avel[i] = ClampV(_avel[i] + dw[i], MaxW);
                _pos[i] = _pos[i] + _vel[i] * Dt;
                _rot[i] = _rot[i].IntegrateAngular(_avel[i], Dt);
            }
        }

        private void ApplyTorque(V3[] dw, M3[] invIW, int a, int b, V3 tau)
        {
            if (_dynamic[b]) dw[b] = dw[b] + invIW[b] * tau;
            if (_dynamic[a]) dw[a] = dw[a] - invIW[a] * tau;
        }

        private void ApplyImpulse(V3[] dv, V3[] dw, M3[] invIW, int a, int b, V3 rA, V3 rB, V3 J)
        {
            if (_dynamic[b])
            {
                dv[b] = dv[b] + J * _invMass[b];
                dw[b] = dw[b] + invIW[b] * V3.Cross(rB, J);
            }
            if (_dynamic[a])
            {
                dv[a] = dv[a] - J * _invMass[a];
                dw[a] = dw[a] - invIW[a] * V3.Cross(rA, J);
            }
        }

        private static V3 Set(V3 v, int i, double val)
            => i == 0 ? new V3(val, v.Y, v.Z) : i == 1 ? new V3(v.X, val, v.Z) : new V3(v.X, v.Y, val);

        private static V3 ClampV(V3 v, double max)
            => new V3(Math.Max(-max, Math.Min(max, v.X)),
                      Math.Max(-max, Math.Min(max, v.Y)),
                      Math.Max(-max, Math.Min(max, v.Z)));

        /// <summary>R · diag(d) · Rᵀ — the world-space inverse inertia tensor.</summary>
        private static M3 RotateDiag(M3 R, V3 d)
        {
            var m = new M3
            {
                M00 = R.M00 * d.X, M01 = R.M01 * d.Y, M02 = R.M02 * d.Z,
                M10 = R.M10 * d.X, M11 = R.M11 * d.Y, M12 = R.M12 * d.Z,
                M20 = R.M20 * d.X, M21 = R.M21 * d.Y, M22 = R.M22 * d.Z,
            };
            return m * R.Transposed();
        }
    }
}
