using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Props the original moves by rewriting their world POSITION every tick — as opposed to a .mot (baked animation)
    /// or a UV scroll (texture only). Keyed like the other scene catalogs: scene folder + mapobj mesh base name.
    ///
    /// There is exactly one in the whole game, SCN0010 花車's street-front HOUSE. It is listed here rather than
    /// hard-coded in ScreenGameplay so the constants sit next to their decompiled provenance, and so a second one
    /// (if any scene turns out to have it) is a data row rather than another special case.
    /// </summary>
    public static class SceneMapobjPositionScrollCatalog
    {
        public sealed class Spec
        {
            public readonly string Folder, ObjectKey;
            public readonly Vector3 Axis;      // unit direction the props travel along
            public readonly float Step;        // signed units per tick
            public readonly float TickMs;
            public readonly float WrapAt;      // reaching this (inclusive) snaps to WrapTo
            public readonly float WrapTo;
            public readonly float[] Start;     // per-instance initial coordinate on Axis (length must match the placements)
            public Spec(string folder, string objectKey, Vector3 axis, float step, float tickMs,
                        float wrapAt, float wrapTo, float[] start)
            { Folder = folder; ObjectKey = objectKey; Axis = axis; Step = step; TickMs = tickMs; WrapAt = wrapAt; WrapTo = wrapTo; Start = start; }

            /// <summary>Signed units per second — the figure to sanity-check against the original.</summary>
            public float PerSecond => Step / (TickMs * 0.001f);
            /// <summary>Seconds for one prop to travel the whole span and wrap.</summary>
            public float LapSeconds => Mathf.Abs(WrapTo - WrapAt) / Mathf.Abs(PerSecond);
        }

        private static readonly Spec[] Specs =
        {
            // SCN0010 花車 街景 (HOUSE ×2). StageScene_UpdateScrollPair_004b40e0 — 我自己反組譯 0x4b40e0 逐行核過,
            // 因為 Ghidra 把物件寫入的 this 掉了:
            //   計時器 (esi+0xac, 0x1e=30, 1)             → 每 30 ms 一 tick
            //   A = [0x678638] (.bss 初值 0)   A += [0x589060] (= −1.0)
            //   B = [0x58905c] (.data 初值 2168.0)  B += −1.0
            //   兩者各自 fcomp [0x558770] (= −2168.0),`test ah,0x41 / jp` 展開後是 **<= 就重設**,重設值
            //   0x45078000 = +2168.0
            //   objects[0]: +0x28 = A、+0x2c = 0、+0x30 = 0、+0x6c = 1
            //   objects[1]: +0x28 = B、+0x2c = 0、+0x30 = 0、+0x6c = 1   ← y/z 每 tick 都被寫 0
            // 兩個累加器起點差 2168 = 4336 單位跨度的一半,所以永遠一棟進場、一棟出場,不會有空隙。
            // −1.0 / 30 ms = −33.333 單位/秒;單棟跑完 4336 單位 = 130.08 秒。
            // SceneMapobjCatalog 的 HOUSE placement 正是 (0,0,0) 與 (2168,0,0),與這兩個起點一致。
            new Spec("SCN0010", "HOUSE", Vector3.right, -1f, 30f, -2168f, 2168f, new[] { 0f, 2168f }),
        };

        public static Spec Find(string folder, string objectKey)
        {
            if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(objectKey)) return null;
            foreach (var s in Specs)
                if (string.Equals(s.Folder, folder, System.StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(s.ObjectKey, objectKey, System.StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }
    }
}
