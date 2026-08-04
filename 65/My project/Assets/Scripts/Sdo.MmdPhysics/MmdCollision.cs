using System;
using System.Collections.Generic;

namespace Sdo.MmdPhysics
{
    /// <summary>
    /// Contact generation for the MMD rigid bodies: sphere / box / capsule against each other, filtered by the
    /// author's own collision groups.
    ///
    /// This matters more than it looks. The authored bodies START interpenetrating — hair roots are modelled inside
    /// the head, skirt panels inside the hips — so the very first frame of a real MMD run pushes them out, and that
    /// push is 6× a frame of free fall. Without it a chain settles INSIDE the body and every downstream number is
    /// wrong: measured against the ground truth, the twintail root was off by 0.52 units with collisions missing and
    /// 0.016 with them accounted for.
    ///
    /// Shapes follow the PMX/Bullet definitions: sphere = size.x radius, box = size as HALF extents, capsule =
    /// size.x radius with size.y the CYLINDER length (sphere centre to sphere centre), Y-aligned in body space.
    /// A capsule is treated as its segment, a sphere as its centre, a box as an oriented box — so every pair reduces
    /// to "closest points between a segment/point and a box/segment/point", which is exact for everything except
    /// box-vs-box (approximated by the closer box's centre; MMD models have no dynamic box that touches another box).
    /// </summary>
    public static class MmdCollision
    {
        public struct Contact
        {
            public int A, B;          // body indices; the normal points from B towards A
            public V3 PointA, PointB; // witness points on each surface
            public V3 Normal;
            public double Depth;      // >0 = overlapping by this much
        }

        /// <summary>Two bodies collide iff each has the OTHER's group bit set in its own enable mask — the same test
        /// Bullet runs, and the reason the skirt is allowed to ignore that huge hip capsule.</summary>
        public static bool Filter(byte groupA, ushort maskA, byte groupB, ushort maskB)
            => ((maskA >> groupB) & 1) == 1 && ((maskB >> groupA) & 1) == 1;

        /// <summary>Half the diagonal of a shape's bounding sphere — used for the broad-phase reject.</summary>
        public static double BoundingRadius(int shape, V3 size)
        {
            if (shape == 0) return Math.Max(size.X, 1e-4);
            if (shape == 1) return Math.Sqrt(size.X * size.X + size.Y * size.Y + size.Z * size.Z);
            return Math.Max(size.X, 1e-4) + Math.Max(size.Y, 0) * 0.5;   // capsule: radius + half the cylinder
        }

        /// <summary>The capsule's segment endpoints in world space (both equal to the centre for a sphere/box).</summary>
        public static void Segment(int shape, V3 size, V3 pos, M3 rot, out V3 p0, out V3 p1)
        {
            if (shape != 2) { p0 = pos; p1 = pos; return; }
            var axis = rot.Col(1) * (Math.Max(size.Y, 0) * 0.5);   // Y-aligned in body space
            p0 = pos - axis; p1 = pos + axis;
        }

        /// <summary>Closest point to <paramref name="p"/> on the oriented box (centre <paramref name="c"/>, half
        /// extents <paramref name="h"/>), and whether p was inside it.</summary>
        public static V3 ClosestOnBox(V3 p, V3 c, M3 rot, V3 h, out bool inside)
        {
            var d = p - c;
            inside = true;
            var local = new V3(V3.Dot(d, rot.Col(0)), V3.Dot(d, rot.Col(1)), V3.Dot(d, rot.Col(2)));
            var clamped = V3.Zero;
            for (int i = 0; i < 3; i++)
            {
                double hi = i == 0 ? h.X : i == 1 ? h.Y : h.Z;
                hi = Math.Max(hi, 1e-4);
                double v = local[i];
                if (v > hi) { v = hi; inside = false; }
                else if (v < -hi) { v = -hi; inside = false; }
                clamped = Set(clamped, i, v);
            }
            return c + rot.Col(0) * clamped.X + rot.Col(1) * clamped.Y + rot.Col(2) * clamped.Z;
        }

        private static V3 Set(V3 v, int i, double val)
            => i == 0 ? new V3(val, v.Y, v.Z) : i == 1 ? new V3(v.X, val, v.Z) : new V3(v.X, v.Y, val);

        /// <summary>Closest points between two segments (degenerate ends are fine — a sphere is a zero-length one).
        /// Standard clamped-parameter solution.</summary>
        public static void ClosestSegments(V3 a0, V3 a1, V3 b0, V3 b1, out V3 ca, out V3 cb)
        {
            var da = a1 - a0; var db = b1 - b0; var r = a0 - b0;
            double A = V3.Dot(da, da), E = V3.Dot(db, db), F = V3.Dot(db, r);
            double s, t;
            if (A <= 1e-12 && E <= 1e-12) { ca = a0; cb = b0; return; }
            if (A <= 1e-12) { s = 0; t = Clamp01(F / Math.Max(E, 1e-12)); }
            else
            {
                double C = V3.Dot(da, r);
                if (E <= 1e-12) { t = 0; s = Clamp01(-C / A); }
                else
                {
                    double B = V3.Dot(da, db);
                    double denom = A * E - B * B;
                    s = denom > 1e-12 ? Clamp01((B * F - C * E) / denom) : 0.0;
                    t = (B * s + F) / E;
                    if (t < 0) { t = 0; s = Clamp01(-C / A); }
                    else if (t > 1) { t = 1; s = Clamp01((B - C) / A); }
                }
            }
            ca = a0 + da * s; cb = b0 + db * t;
        }

        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        /// <summary>One pair → a contact, or false when they are apart. <paramref name="radA"/>/<paramref name="radB"/>
        /// are the shapes' "thickness" (sphere/capsule radius; 0 for a box).</summary>
        public static bool Collide(int shapeA, V3 sizeA, V3 posA, M3 rotA,
                                   int shapeB, V3 sizeB, V3 posB, M3 rotB,
                                   out V3 pa, out V3 pb, out V3 normal, out double depth)
        {
            double radA = shapeA == 1 ? 0.0 : Math.Max(sizeA.X, 1e-4);
            double radB = shapeB == 1 ? 0.0 : Math.Max(sizeB.X, 1e-4);
            V3 ca, cb;

            if (shapeA == 1 && shapeB == 1)
            {
                // box-box: witness on each box towards the other's centre (no MMD model needs better than this)
                ca = ClosestOnBox(posB, posA, rotA, sizeA, out _);
                cb = ClosestOnBox(posA, posB, rotB, sizeB, out _);
            }
            else if (shapeA == 1)
            {
                Segment(shapeB, sizeB, posB, rotB, out var b0, out var b1);
                // sample the box against both ends and the middle, keep the deepest
                cb = ClosestPointOnSegmentToBox(b0, b1, posA, rotA, sizeA);
                ca = ClosestOnBox(cb, posA, rotA, sizeA, out _);
            }
            else if (shapeB == 1)
            {
                Segment(shapeA, sizeA, posA, rotA, out var a0, out var a1);
                ca = ClosestPointOnSegmentToBox(a0, a1, posB, rotB, sizeB);
                cb = ClosestOnBox(ca, posB, rotB, sizeB, out _);
            }
            else
            {
                Segment(shapeA, sizeA, posA, rotA, out var a0, out var a1);
                Segment(shapeB, sizeB, posB, rotB, out var b0, out var b1);
                ClosestSegments(a0, a1, b0, b1, out ca, out cb);
            }

            var delta = ca - cb;
            double dist = delta.Length;
            double want = radA + radB;
            if (dist > want && dist > 1e-9)
            {
                pa = default; pb = default; normal = default; depth = 0;
                return false;
            }
            // normal points B → A; degenerate (exactly coincident) falls back to "up", which only happens for bodies
            // authored at the same point and any direction is as good as another
            normal = dist > 1e-9 ? delta * (1.0 / dist) : new V3(0, 1, 0);
            depth = want - dist;
            pa = ca - normal * radA;
            pb = cb + normal * radB;
            return true;
        }

        private static V3 ClosestPointOnSegmentToBox(V3 s0, V3 s1, V3 c, M3 rot, V3 h)
        {
            // cheap but adequate: try both ends and the midpoint, keep whichever is closest to the box
            V3 best = s0; double bestD = double.MaxValue;
            for (int i = 0; i <= 2; i++)
            {
                var p = s0 + (s1 - s0) * (i * 0.5);
                var q = ClosestOnBox(p, c, rot, h, out _);
                double d = (p - q).Length;
                if (d < bestD) { bestD = d; best = p; }
            }
            return best;
        }
    }
}
