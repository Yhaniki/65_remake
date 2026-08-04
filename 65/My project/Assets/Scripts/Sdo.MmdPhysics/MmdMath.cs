using System;

namespace Sdo.MmdPhysics
{
    /// <summary>
    /// Double-precision vector / quaternion / 3×3 matrix, deliberately NOT UnityEngine's.
    ///
    /// Two reasons. (1) PRECISION: the reference simulation this solver is verified against runs in float64, and a
    /// 4-second scenario is ~480 substeps × 10 solver iterations — single precision drifts enough over that to make
    /// "is the port correct?" unanswerable. (2) NO UNITY IN THE LOOP: with no UnityEngine types the whole solver
    /// compiles and runs under plain `dotnet run`, so a change can be checked against the ground truth in seconds
    /// instead of an editor import + player build. The game side converts at the boundary.
    ///
    /// Conventions match MMD/Bullet: right-handed maths on left-handed MMD data (which is what Bullet itself does —
    /// it never looks at handedness), quaternion (x,y,z,w), and MMD's euler composition R = Ry·Rx·Rz.
    /// </summary>
    public struct V3
    {
        public double X, Y, Z;
        public V3(double x, double y, double z) { X = x; Y = y; Z = z; }
        public static readonly V3 Zero = new V3(0, 0, 0);

        public static V3 operator +(V3 a, V3 b) => new V3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static V3 operator -(V3 a, V3 b) => new V3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static V3 operator -(V3 a) => new V3(-a.X, -a.Y, -a.Z);
        public static V3 operator *(V3 a, double s) => new V3(a.X * s, a.Y * s, a.Z * s);
        public static V3 operator *(double s, V3 a) => a * s;

        public double this[int i] => i == 0 ? X : (i == 1 ? Y : Z);

        public static double Dot(V3 a, V3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static V3 Cross(V3 a, V3 b)
            => new V3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        public double Length => Math.Sqrt(Dot(this, this));
        public V3 Normalized { get { double l = Length; return l > 1e-12 ? this * (1.0 / l) : Zero; } }
        public override string ToString() => $"({X:F5}, {Y:F5}, {Z:F5})";
    }

    /// <summary>Row-major 3×3. Columns are the local axes when it is a rotation.</summary>
    public struct M3
    {
        public double M00, M01, M02, M10, M11, M12, M20, M21, M22;

        public static readonly M3 Identity = new M3
        { M00 = 1, M01 = 0, M02 = 0, M10 = 0, M11 = 1, M12 = 0, M20 = 0, M21 = 0, M22 = 1 };

        public V3 Col(int c) => c == 0 ? new V3(M00, M10, M20)
                              : c == 1 ? new V3(M01, M11, M21)
                                       : new V3(M02, M12, M22);

        public static V3 operator *(M3 m, V3 v)
            => new V3(m.M00 * v.X + m.M01 * v.Y + m.M02 * v.Z,
                      m.M10 * v.X + m.M11 * v.Y + m.M12 * v.Z,
                      m.M20 * v.X + m.M21 * v.Y + m.M22 * v.Z);

        /// <summary>Mᵀ·v — for a rotation this is the inverse.</summary>
        public V3 TMul(V3 v)
            => new V3(M00 * v.X + M10 * v.Y + M20 * v.Z,
                      M01 * v.X + M11 * v.Y + M21 * v.Z,
                      M02 * v.X + M12 * v.Y + M22 * v.Z);

        public static M3 operator *(M3 a, M3 b) => new M3
        {
            M00 = a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20,
            M01 = a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21,
            M02 = a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22,
            M10 = a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20,
            M11 = a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21,
            M12 = a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22,
            M20 = a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20,
            M21 = a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21,
            M22 = a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22,
        };

        public M3 Transposed() => new M3
        {
            M00 = M00, M01 = M10, M02 = M20,
            M10 = M01, M11 = M11, M12 = M21,
            M20 = M02, M21 = M12, M22 = M22,
        };

        /// <summary>MMD/saba euler (radians): R = Ry · Rx · Rz.</summary>
        public static M3 FromMmdEuler(V3 e)
        {
            double cx = Math.Cos(e.X), sx = Math.Sin(e.X);
            double cy = Math.Cos(e.Y), sy = Math.Sin(e.Y);
            double cz = Math.Cos(e.Z), sz = Math.Sin(e.Z);
            var rx = new M3 { M00 = 1, M11 = cx, M12 = -sx, M21 = sx, M22 = cx };
            var ry = new M3 { M00 = cy, M02 = sy, M11 = 1, M20 = -sy, M22 = cy };
            var rz = new M3 { M00 = cz, M01 = -sz, M10 = sz, M11 = cz, M22 = 1 };
            return ry * rx * rz;
        }

        /// <summary>btGeneric6DofConstraint::matrixToEulerXYZ — the decomposition its angle limits are expressed in
        /// (R = Rx·Ry·Rz). Gimbal-locked rows fall back the same way Bullet's does.</summary>
        public V3 ToEulerXyz()
        {
            // R[0,2] = sin(y)
            double s = M02;
            if (s < 1.0 - 1e-9)
            {
                if (s > -1.0 + 1e-9)
                    return new V3(Math.Atan2(-M12, M22), Math.Asin(s), Math.Atan2(-M01, M00));
                return new V3(-Math.Atan2(M10, M11), -Math.PI / 2.0, 0.0);
            }
            return new V3(Math.Atan2(M10, M11), Math.PI / 2.0, 0.0);
        }

        public static M3 FromQuat(Q q)
        {
            double x = q.X, y = q.Y, z = q.Z, w = q.W;
            double xx = x * x, yy = y * y, zz = z * z;
            return new M3
            {
                M00 = 1 - 2 * (yy + zz), M01 = 2 * (x * y - z * w), M02 = 2 * (x * z + y * w),
                M10 = 2 * (x * y + z * w), M11 = 1 - 2 * (xx + zz), M12 = 2 * (y * z - x * w),
                M20 = 2 * (x * z - y * w), M21 = 2 * (y * z + x * w), M22 = 1 - 2 * (xx + yy),
            };
        }
    }

    public struct Q
    {
        public double X, Y, Z, W;
        public Q(double x, double y, double z, double w) { X = x; Y = y; Z = z; W = w; }
        public static readonly Q Identity = new Q(0, 0, 0, 1);

        public static Q FromMatrix(M3 m)
        {
            double tr = m.M00 + m.M11 + m.M22;
            if (tr > 0)
            {
                double s = Math.Sqrt(tr + 1.0) * 2.0;
                return new Q((m.M21 - m.M12) / s, (m.M02 - m.M20) / s, (m.M10 - m.M01) / s, 0.25 * s).Normalized();
            }
            if (m.M00 > m.M11 && m.M00 > m.M22)
            {
                double s = Math.Sqrt(1.0 + m.M00 - m.M11 - m.M22) * 2.0;
                return new Q(0.25 * s, (m.M01 + m.M10) / s, (m.M02 + m.M20) / s, (m.M21 - m.M12) / s).Normalized();
            }
            if (m.M11 > m.M22)
            {
                double s = Math.Sqrt(1.0 + m.M11 - m.M00 - m.M22) * 2.0;
                return new Q((m.M01 + m.M10) / s, 0.25 * s, (m.M12 + m.M21) / s, (m.M02 - m.M20) / s).Normalized();
            }
            double t = Math.Sqrt(1.0 + m.M22 - m.M00 - m.M11) * 2.0;
            return new Q((m.M02 + m.M20) / t, (m.M12 + m.M21) / t, 0.25 * t, (m.M10 - m.M01) / t).Normalized();
        }

        public Q Normalized()
        {
            double l = Math.Sqrt(X * X + Y * Y + Z * Z + W * W);
            return l > 1e-12 ? new Q(X / l, Y / l, Z / l, W / l) : Identity;
        }

        /// <summary>Integrate a body's orientation by an angular velocity (Bullet does this same first-order step
        /// followed by a renormalise).</summary>
        public Q IntegrateAngular(V3 w, double dt)
        {
            var dq = new Q(w.X, w.Y, w.Z, 0.0);
            double hx = 0.5 * dt;
            var q = new Q(
                X + hx * (dq.W * X + dq.X * W + dq.Y * Z - dq.Z * Y),
                Y + hx * (dq.W * Y + dq.Y * W + dq.Z * X - dq.X * Z),
                Z + hx * (dq.W * Z + dq.Z * W + dq.X * Y - dq.Y * X),
                W + hx * (dq.W * W - dq.X * X - dq.Y * Y - dq.Z * Z));
            return q.Normalized();
        }
    }
}
