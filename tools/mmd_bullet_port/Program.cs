using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Sdo.MmdPhysics;

// Runs MmdRigidWorld on the same model + scenario as the pybullet ground truth and prints the per-frame
// error against ref_<scenario>.json. No Unity, no build — `dotnet run` and read the numbers.
//
//   dotnet run --project tools/mmd_bullet_port -- [scenario] [seconds]
//
// The reference records BONE positions (body position + its bone offset), so that is what is compared.

static class Program
{
    static int Main(string[] args)
    {
        string scenario = args.Length > 0 ? args[0] : "rest";
        double seconds = args.Length > 1 ? double.Parse(args[1], CultureInfo.InvariantCulture) : 4.0;
        string here = AppContext.BaseDirectory;
        string toolDir = FindUp(here, "mmd_bullet_port") ?? Directory.GetCurrentDirectory();
        string repoTools = Path.GetFullPath(Path.Combine(toolDir, ".."));

        string physPath = Path.Combine(toolDir, "ika_physics.json");
        string refPath = Path.Combine(repoTools, "mmd_cloth_validate", $"ref_{scenario}.json");
        if (!File.Exists(physPath)) { Console.Error.WriteLine($"missing {physPath} — run export_physics.py first"); return 2; }
        if (!File.Exists(refPath)) { Console.Error.WriteLine($"missing {refPath}"); return 2; }

        var phys = JsonDocument.Parse(File.ReadAllText(physPath)).RootElement;
        var world = BuildWorld(phys, out var chains);
        var reference = JsonDocument.Parse(File.ReadAllText(refPath)).RootElement;
        double upm = reference.GetProperty("unitsPerMeter").GetDouble();

        Console.WriteLine($"scenario={scenario}  bodies={world.BodyCount}  joints={world.JointCount}  colours={world.ColorCount}");
        Console.WriteLine($"comparing BONE positions against {Path.GetFileName(refPath)}  (unitsPerMeter={upm:F2})\n");

        var refChains = reference.GetProperty("chains");
        int frames = (int)Math.Round(seconds * MmdRigidWorld.Fps);

        // rest pose only for now: the kinematic bodies never move, which is what the "rest" scenario is
        world.DriveKinematic(null, 0.0);

        var report = new List<(int frame, double maxErr, double meanErr)>();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int f = 1; f <= frames; f++)
        {
            world.StepFrame();
            double worst = 0, sum = 0; int cnt = 0;
            foreach (var (name, idx) in chains)
            {
                if (!refChains.TryGetProperty(name, out var rc)) continue;
                var rframes = rc.GetProperty("frames");
                if (f >= rframes.GetArrayLength()) continue;
                var rf = rframes[f];
                for (int b = 0; b < idx.Length && b < rf.GetArrayLength(); b++)
                {
                    var got = world.BonePositionOf(idx[b]);
                    var want = rf[b];
                    double dx = got.X - want[0].GetDouble();
                    double dy = got.Y - want[1].GetDouble();
                    double dz = got.Z - want[2].GetDouble();
                    double e = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (e > worst) worst = e;
                    sum += e; cnt++;
                }
            }
            if (cnt > 0) report.Add((f, worst, sum / cnt));
            if (f == frames)
            {
                Console.WriteLine("per-chain error at the last frame (model units):");
                foreach (var (name, idx) in chains)
                {
                    if (!refChains.TryGetProperty(name, out var rc)) continue;
                    var rframes = rc.GetProperty("frames");
                    if (f >= rframes.GetArrayLength()) continue;
                    var rf = rframes[f];
                    double cw = 0, cs = 0; int cc = 0; double rootErr = 0, tipErr = 0;
                    for (int b = 0; b < idx.Length && b < rf.GetArrayLength(); b++)
                    {
                        var got = world.BonePositionOf(idx[b]);
                        var want = rf[b];
                        double e = Math.Sqrt(Math.Pow(got.X - want[0].GetDouble(), 2)
                                           + Math.Pow(got.Y - want[1].GetDouble(), 2)
                                           + Math.Pow(got.Z - want[2].GetDouble(), 2));
                        if (b == 0) rootErr = e;
                        tipErr = e;
                        if (e > cw) cw = e;
                        cs += e; cc++;
                    }
                    // is the chain the right LENGTH? (segment sum + straight root->tip span, ours vs the reference)
                    double gotSeg = 0, refSeg = 0;
                    for (int b = 1; b < idx.Length && b < rf.GetArrayLength(); b++)
                    {
                        var g0 = world.BonePositionOf(idx[b - 1]); var g1 = world.BonePositionOf(idx[b]);
                        gotSeg += Math.Sqrt(Math.Pow(g1.X - g0.X, 2) + Math.Pow(g1.Y - g0.Y, 2) + Math.Pow(g1.Z - g0.Z, 2));
                        refSeg += Math.Sqrt(Math.Pow(rf[b][0].GetDouble() - rf[b - 1][0].GetDouble(), 2)
                                          + Math.Pow(rf[b][1].GetDouble() - rf[b - 1][1].GetDouble(), 2)
                                          + Math.Pow(rf[b][2].GetDouble() - rf[b - 1][2].GetDouble(), 2));
                    }
                    var gr = world.BonePositionOf(idx[0]); var gt = world.BonePositionOf(idx[Math.Min(idx.Length, rf.GetArrayLength()) - 1]);
                    int lastI = Math.Min(idx.Length, rf.GetArrayLength()) - 1;
                    double gotSpan = Math.Sqrt(Math.Pow(gt.X - gr.X, 2) + Math.Pow(gt.Y - gr.Y, 2) + Math.Pow(gt.Z - gr.Z, 2));
                    double refSpan = Math.Sqrt(Math.Pow(rf[lastI][0].GetDouble() - rf[0][0].GetDouble(), 2)
                                             + Math.Pow(rf[lastI][1].GetDouble() - rf[0][1].GetDouble(), 2)
                                             + Math.Pow(rf[lastI][2].GetDouble() - rf[0][2].GetDouble(), 2));
                    Console.WriteLine($"   {name,-16} n={cc,3}  root {rootErr,7:F4}  tip {tipErr,7:F4}  mean {cs / Math.Max(cc, 1),7:F4}" +
                                      $"   segsum {gotSeg,7:F3}/{refSeg,7:F3}   span {gotSpan,7:F3}/{refSpan,7:F3}");
                }
                Console.WriteLine();
            }
        }
        watch.Stop();

        Console.WriteLine($"{"frame",6} {"t(s)",7} {"max err",12} {"mean err",12}   (model units; 1 unit ≈ {100.0 / upm:F1} cm)");
        foreach (var r in report.Where(r => r.frame % 15 == 0 || r.frame <= 3))
            Console.WriteLine($"{r.frame,6} {r.frame / MmdRigidWorld.Fps,7:F2} {r.maxErr,12:F5} {r.meanErr,12:F5}");
        var last = report[^1];
        Console.WriteLine($"\nfinal: max {last.maxErr:F5} u ({last.maxErr / upm * 100:F2} cm), mean {last.meanErr:F5} u");
        Console.WriteLine($"sim: {frames} frames in {watch.ElapsedMilliseconds} ms " +
                          $"({watch.Elapsed.TotalMilliseconds / frames:F2} ms/frame, {world.BodyCount} bodies)");
        return 0;
    }

    static string FindUp(string start, string name)
    {
        var d = new DirectoryInfo(start);
        while (d != null) { if (d.Name == name) return d.FullName; d = d.Parent; }
        return null;
    }

    static MmdRigidWorld BuildWorld(JsonElement phys, out List<(string, int[])> chains)
    {
        var bodies = new List<MmdRigidWorld.Body>();
        foreach (var b in phys.GetProperty("bodies").EnumerateArray())
            bodies.Add(new MmdRigidWorld.Body
            {
                Name = b.GetProperty("name").GetString(),
                Bone = b.GetProperty("bone").GetInt32(),
                BonePos = Vec(b.GetProperty("bonePos")),
                Group = (byte)b.GetProperty("group").GetInt32(),
                Mask = (ushort)b.GetProperty("mask").GetInt32(),
                Shape = b.GetProperty("shape").GetInt32(),
                Size = Vec(b.GetProperty("size")),
                Pos0 = Vec(b.GetProperty("pos")),
                RotEuler = Vec(b.GetProperty("rot")),
                Mass = b.GetProperty("mass").GetDouble(),
                LinDamp = b.GetProperty("linDamp").GetDouble(),
                AngDamp = b.GetProperty("angDamp").GetDouble(),
                Mode = b.GetProperty("mode").GetInt32(),
            });

        var joints = new List<MmdRigidWorld.Joint>();
        foreach (var j in phys.GetProperty("joints").EnumerateArray())
            joints.Add(new MmdRigidWorld.Joint
            {
                A = j.GetProperty("a").GetInt32(),
                B = j.GetProperty("b").GetInt32(),
                Pos = Vec(j.GetProperty("pos")),
                RotEuler = Vec(j.GetProperty("rot")),
                PosLo = Vec(j.GetProperty("posLo")),
                PosHi = Vec(j.GetProperty("posHi")),
                RotLo = Vec(j.GetProperty("rotLo")),
                RotHi = Vec(j.GetProperty("rotHi")),
                RotSpring = Vec(j.GetProperty("rotSpring")),
            });

        chains = new List<(string, int[])>();
        foreach (var c in phys.GetProperty("chains").EnumerateObject())
            chains.Add((c.Name, c.Value.EnumerateArray().Select(v => v.GetInt32()).ToArray()));

        return new MmdRigidWorld(bodies, joints);
    }

    static V3 Vec(JsonElement e) => new V3(e[0].GetDouble(), e[1].GetDouble(), e[2].GetDouble());
}
