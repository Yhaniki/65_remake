using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// One animated channel of an EFT emitter (17 per emitter, record = 0x13 words at word[(ch+9)*0x13]).
    /// Keyframe pairs: time[k]=word[1+2k]/480 (0..1 of life), value[k]=1−word[2+2k]/462 (decoded curve).
    /// min=word[0x11], max=word[0x12]. Sampler = piecewise-linear (Anim_InterpKeyframeCurve_004bcd30).
    /// </summary>
    public sealed class EftChannel
    {
        public int Count; public float Min, Max;
        public float[] Time, Val;

        public float Decoded(float t)
        {
            if (Count < 2) return 0f;                          // count<=1 → 0 (engine FUN_004bcd30)
            if (t <= Time[0]) return Val[0];
            for (int k = 1; k < Count; k++)
                if (t <= Time[k]) { float f = (t - Time[k - 1]) / Mathf.Max(1e-6f, Time[k] - Time[k - 1]); return Mathf.Lerp(Val[k - 1], Val[k], f); }
            return Val[Count - 1];
        }
        // 0.5-pivot remap → size multiplier (channels 0, 0xe, 0xf, 0x10). Neutral 1.0 at v=0.5.
        public float Scale(float t)
        {
            float v = Decoded(t), half = (Max - Min) * 0.5f;
            if (v < 0.5f) { float d = (0.5f - v) * half + 1f; return d != 0f ? 1f / d : v; }
            return (v - 0.5f) * half + 1f;
        }
        public float Ranged(float t) => (Max - Min) * Decoded(t);            // colour byte 0..255 (ch1-7)
        public float RangedMin(float t) => (Max - Min) * Decoded(t) + Min;   // degrees (ch8-0xd)
    }

    /// <summary>One EFT emitter template (0x232 words). Only the fields the simulation needs are parsed.</summary>
    public sealed class EftEmitter
    {
        public int Flags, Emit, Life0, SpawnDelay, StartDelay, Blend;
        public int PosSpreadX, PosSpreadZ;
        public Vector3 Vel, Pos, BaseSize;
        public Vector3 InitRot, RotJit;   // template rotation [0x1a-1c] (rad→deg) + jitter [0x1f5-7] (deg); update accumulates channels onto this
        public int Seg; public float Rx, Ry, Rh;
        public bool HasTex; public int TexIdx;
        public float SpeedMul, GravBase, GravAccel;
        public int TrigType, Slot, NumTrig;   // TrigType word[0x36] (as a child); NumTrig word[3] = #child triggers this emitter fires
        public float AtTime;                  // word[0x3b] ([0xec]): PARENT age (ticks) at which this child sub-emits (trigType 2)
        public readonly List<EftEmitter> Children = new List<EftEmitter>();   // DFS sub-tree, parsed via NumTrig
        public readonly EftChannel[] Ch = new EftChannel[17];

        public bool IsRing => (Flags & 8) != 0;        // bit3 → ring geometry
        public bool Orient => (Flags & 0x10000) != 0;  // 0x10000 → camera-billboard
    }

    /// <summary>
    /// Parses a 3DEFT (.EFT) particle-effect file into emitter templates + the root/control block, faithfully to
    /// the decompiled loader (Effect_LoadEftFile_004bbb60). File layout: ver(4) + script(0x8000) + emitter array
    /// @0x8004 (32 × 0x8c8) + root block @0x19904. A slot is live iff word[1](flags)≠0.
    /// </summary>
    public sealed class EftFile
    {
        public readonly List<EftEmitter> Emitters = new List<EftEmitter>();
        public int RootCount; public int[] RootSlots, RootDelays, ChildSlots;

        const int TEMPLATE = 0x8004, STRIDE = 0x8c8, ROOT = 0x19904, SLOTS = 32;

        public static EftFile Load(byte[] d)
        {
            var f = new EftFile();
            int I(int b) => BitConverter.ToInt32(d, b);
            float F(int b) => BitConverter.ToSingle(d, b);

            for (int slot = 0; slot < SLOTS; slot++)
            {
                int eb = TEMPLATE + slot * STRIDE;
                if (eb + STRIDE > d.Length) break;
                int W(int w) => I(eb + w * 4);
                float Wf(int w) => F(eb + w * 4);
                int flags = W(1);
                if (flags == 0) continue;

                var em = new EftEmitter
                {
                    Flags = flags,
                    Blend = W(2),
                    Emit = W(0xc),
                    Life0 = W(0xe),
                    SpawnDelay = W(0xf),
                    StartDelay = W(0x10),
                    PosSpreadX = W(0x11),
                    PosSpreadZ = W(0x12),
                    Vel = new Vector3(Wf(0x14), Wf(0x15), Wf(0x16)),
                    Pos = new Vector3(Wf(0x17), Wf(0x18), Wf(0x19)),
                    BaseSize = new Vector3(Wf(0x20), Wf(0x21), Wf(0x22)),
                    InitRot = new Vector3(Wf(0x1a), Wf(0x1b), Wf(0x1c)) * Mathf.Rad2Deg,
                    RotJit = new Vector3(Wf(0x1f5), Wf(0x1f6), Wf(0x1f7)),
                    Seg = W(0x21f), Rx = Wf(0x220), Ry = Wf(0x221), Rh = Wf(0x223),
                    HasTex = W(0x45) != 0,
                    SpeedMul = Wf(0x1f4),
                    GravBase = Wf(0x21b), GravAccel = Wf(0x21c),
                };
                if (em.Life0 < 0) em.Life0 = 1 - em.Life0;        // neg-life → 1−v
                if (em.SpeedMul == 0f) em.SpeedMul = 1f;          // global speed mult; default 1
                em.Slot = slot;
                em.TrigType = W(0x36);
                em.NumTrig = W(3);
                em.AtTime = Wf(0x3b);
                int texSel = W(0x46);
                em.TexIdx = W(0x47 + texSel);

                for (int ch = 0; ch < 17; ch++)
                {
                    int cb = eb + (ch + 9) * 0x13 * 4;
                    int cnt = I(cb);
                    var c = new EftChannel { Count = cnt, Min = F(cb + 0x11 * 4), Max = F(cb + 0x12 * 4) };
                    if (cnt > 0)
                    {
                        c.Time = new float[cnt]; c.Val = new float[cnt];
                        for (int k = 0; k < cnt; k++)
                        {
                            c.Time[k] = F(cb + (1 + 2 * k) * 4) / 480f;
                            c.Val[k] = 1f - F(cb + (2 + 2 * k) * 4) / 462f;
                        }
                    }
                    em.Ch[ch] = c;
                }
                f.Emitters.Add(em);
            }

            f.RootCount = I(ROOT);
            int rc = Mathf.Clamp(f.RootCount, 0, 32);
            f.RootSlots = new int[rc]; f.RootDelays = new int[rc];
            for (int k = 0; k < rc; k++) { f.RootSlots[k] = I(ROOT + (1 + k) * 4); f.RootDelays[k] = I(ROOT + (0x21 + k) * 4); }
            int cc = Mathf.Clamp(I(ROOT + 0x41 * 4), 0, 32);   // child count word[0x41]; child slot indices at word[0x62+m]
            f.ChildSlots = new int[cc];
            for (int m = 0; m < cc; m++) f.ChildSlots[m] = I(ROOT + (0x62 + m) * 4);

            // Build the sub-emit TREE: ChildSlots is the DFS flattening of the whole tree; each emitter consumes
            // NumTrig children (then those consume theirs, depth-first). Verified vs the live game (Frida): for
            // 100COMBO root0(naga00,numTrig2)→[ring1,ring2]; for 400COMBO each ring(numTrig1)→a billboard.
            var bySlot = new Dictionary<int, EftEmitter>();
            foreach (var em in f.Emitters) bySlot[em.Slot] = em;
            int idx = 0;
            void Build(EftEmitter parent)
            {
                for (int i = 0; i < parent.NumTrig && idx < f.ChildSlots.Length; i++)
                {
                    if (bySlot.TryGetValue(f.ChildSlots[idx++], out var child)) { parent.Children.Add(child); Build(child); }
                }
            }
            foreach (int rs in f.RootSlots) if (bySlot.TryGetValue(rs, out var rem)) Build(rem);
            return f;
        }
    }
}
