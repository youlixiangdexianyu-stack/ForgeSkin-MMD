using ForgeVisual;

namespace ForgeCore.Tests;

/// <summary>
/// The one chain physics (Visual/Core/ChainPhysics.cs), core algorithm. A charm's rope (the shape ForgeSkin builds: a pinned hanging point,
/// six pieces, a heavier pendant on a rigid arm, gear boxes one of which holds the hanging point) on a moving and turning anchor and a body on
/// part of its path, at uneven frame times with a stall that puts it at rest, then held still, gives the locked node trajectory (checkpoints
/// within 1e-9 m: the same numbers on every platform's maths library). It stays stable: no node faster than the speed cap allows relative to
/// the anchor, a damped rope held still hangs straight again, and carried at a steady walk it hangs straight (drag is the chain's own).
/// Spring bones: a strand bound bent holds its shape under gravity with restore and sags without; held still at any facing it comes to rest
/// in the same shape, and strands pressed on body capsules come to rest; the angle limit holds; nodes stay out of a capsule; skirt strands linked side by side stay near their rest spacing; a teleport puts the chain at rest instead of flinging it, and a
/// knock-back faster than the body limit does not fling it either.
/// </summary>
internal static class ChainChecks
{
    internal static void Run(Action<bool, string> check)
    {
        const double arm = 0.012;
        var spec = ChainSpec.Charm();
        spec.Radius = 0.001;
        var rope = Charm(spec, 0.05, 6, arm);
        double[] gear = { 0, 0, -1, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0.3, 1.1, -0.2, 1 }; // turned a quarter about Y
        var frames = Frames();
        rope.AddBox(frames[0].M, gear, new[] { -0.15, -0.02, -0.02 }, new[] { 0.15, 0.03, 0.02 });
        rope.AddBox(frames[0].M, gear, new[] { 0.02, -0.12, -0.015 }, new[] { 0.06, -0.03, 0.015 });
        var nodes = new List<double[]>();
        foreach (var f in frames) { rope.Update(f.Time, f.M, f.Body); nodes.Add((double[])rope.Nodes.Clone()); }
        check(rope.Boxes == 2, "chain: both gear boxes taken");
        // Checkpoints: the pendant's centre (last node) at frames 60, 119, 200, 239 and the last.
        double[][] locked =
        {
            new[] { 0.47283970235980394, 0.9669087057975125, -0.31538490871751823 },
            new[] { 0.04879134796677367, 1.1082786803034332, -0.42621446838392585 },
            new[] { 0.5014365535821699, 1.0799861393189332, 0.023174676674019182 },
            new[] { 0.15788801151981086, 0.9717081853705346, -0.3253461487170894 },
            new[] { 0.15823305723599132, 0.9718490744231377, -0.32488113868935875 },
        };
        int[] at = { 60, 119, 200, 239, frames.Count - 1 };
        for (int k = 0; k < at.Length; k++)
        {
            var p = nodes[at[k]];
            double d = Math.Max(Math.Abs(p[^3] - locked[k][0]), Math.Max(Math.Abs(p[^2] - locked[k][1]), Math.Abs(p[^1] - locked[k][2])));
            check(d < 1e-9, $"chain: charm trajectory at frame {at[k]} (pendant {p[^3]:R}, {p[^2]:R}, {p[^1]:R})");
        }
        check(nodes.Take(240).Max(x => Math.Abs(x[^3] - x[0])) > 1e-2, "chain: the pendant swings off the straight hang while moving");
        double fastest = 0;
        for (int f = 1; f < frames.Count; f++)
        {
            double dt = frames[f].Time - frames[f - 1].Time;
            if (dt > ChainState.Gap) continue;
            for (int i = 3; i < nodes[f].Length; i += 3)
                fastest = Math.Max(fastest, Math.Sqrt(Enumerable.Range(0, 3).Sum(j => Math.Pow(nodes[f][i + j] - nodes[f][j] - nodes[f - 1][i + j] + nodes[f - 1][j], 2))) / dt);
        }
        check(fastest <= 3 * ChainState.MaxSpeed, $"chain: no node faster than the cap allows relative to the anchor ({fastest:F2} m/s)");

        // Held still without the boxes, damped: back to hanging straight down within a millimetre.
        var free = Charm(new ChainSpec { Damping = 0.9, Weight = 2.5, Stiffness = 0, Radius = 0.001, Collide = false }, 0.05, 12, arm);
        foreach (var f in frames) free.Update(f.Time, f.M, f.Body);
        var n = free.Nodes;
        check(Math.Abs(n[^3] - n[0]) < 1e-3 && Math.Abs(n[^1] - n[2]) < 1e-3 && Math.Abs(n[1] - n[^2] - 0.05 - arm) < 1e-3, "chain: a damped rope held still hangs straight again");

        // Walking (3.5 m/s reached over a second, then two seconds at it), anchor and body together: hangs straight again.
        var walker = Charm(new ChainSpec { Collide = false }, 0.05, 6, arm);
        for (int i = 0; i <= 180; i++)
        {
            double t = i / 60.0, z = t < 1 ? 3.5 * (t * t * t - t * t * t * t / 2) : 3.5 * (t - 0.5);
            walker.Update(t, Moved(0, 1.1, z), new[] { 0, 1.6, z - 0.4 });
        }
        var w = walker.Nodes;
        check(Math.Abs(w[^1] - w[2]) < 2e-3 && Math.Abs(w[^3] - w[0]) < 2e-3, $"chain: walking does not drag the charm ({w[^1] - w[2]:F4} m behind)");

        // A strand bound sticking out sideways and bending down (a lock of hair): with restore it holds near that shape held still for
        // two seconds; without, it sags.
        double Sag(double restore)
        {
            var hair = ChainSpec.LongHair();
            hair.Restore = restore;
            hair.StyleLeash = 0; // the spring alone: the leash would cap the sag and hide the difference
            hair.Collide = false;
            var shape = new ChainShape();
            int node = shape.AddRoot(0, 0, 0);
            for (int i = 0; i < 5; i++) node = shape.Add(node, 0.04, 1, -0.2 * i, 0);
            var chain = new ChainState(shape, hair);
            for (int i = 0; i <= 120; i++) chain.Update(i / 60.0, Moved(0, 1.6, 0), new[] { 0, 1, 0.0 });
            return 1.6 - chain.Nodes[^2];
        }
        double held = Sag(1), limp = Sag(0);
        check(held < 0.12 && limp > held + 0.05, $"chain: restore holds a bound shape ({held:F3} m drop) that sags without it ({limp:F3} m)");

        // The style leash: under a fast swing every node stays within 0.3 x (2 cm + its reach down the strand) of the bound shape;
        // without the leash the same strand strays further. A fringe (a short strand beside a long one) is held to the smaller
        // allowance its own reach gives it — laid on the brow, not thrown most of its length off it.
        double Stray(double leash, out double shortStray)
        {
            var hair = ChainSpec.LongHair();
            hair.Restore = 0;
            hair.StyleLeash = leash;
            hair.Collide = false;
            hair.SelfCollide = false;
            var shape = new ChainShape();
            int node = shape.AddRoot(0, 0, 0);
            for (int i = 0; i < 5; i++) node = shape.Add(node, 0.03, 1, 0, 0); // a fringe: 15 cm
            node = shape.AddRoot(0, 0, 0.2);
            for (int i = 0; i < 10; i++) node = shape.Add(node, 0.06, 1, 0, 0); // a lock: 60 cm
            var chain = new ChainState(shape, hair);
            double worst = 0, all = 0;
            for (int i = 0; i <= 150; i++)
            {
                double t = i / 60.0, x = 0.45 * Math.Sin(2 * Math.PI * 1.2 * t);
                chain.Update(t, Moved(x, 1.6, 0), new[] { 0, 1, 0.0 });
                var p = chain.Nodes;
                for (int k = 1; k <= 5; k++)
                    worst = Math.Max(worst, Math.Sqrt(Math.Pow(p[k * 3] - (x + 0.03 * k), 2) + Math.Pow(p[k * 3 + 1] - 1.6, 2) + Math.Pow(p[k * 3 + 2], 2)));
                for (int k = 7; k <= 15; k++)
                    all = Math.Max(all, Math.Sqrt(Math.Pow(p[k * 3] - (x + 0.06 * (k - 6)), 2) + Math.Pow(p[k * 3 + 1] - 1.6, 2) + Math.Pow(p[k * 3 + 2] - 0.2, 2)));
            }
            shortStray = worst;
            return Math.Max(worst, all);
        }
        double leashed = Stray(0.3, out double fringe), freeStray = Stray(0, out _);
        // The 3.5 cm over the allowance is the sub-step the chain trails the anchor by at this swing's speed (the leash clamps
        // against the sub-step pose, and the frame is drawn at the frame pose).
        check(fringe <= 0.3 * (0.02 + 0.15) + 0.035 && freeStray > leashed + 0.05,
            $"chain: the style leash holds the fringe near its shape ({fringe:F3} m of {0.3 * (0.02 + 0.15):F3}) while a free strand strays further ({freeStray:F3} m)");

        // A strand bound bent every which way, held still with the anchor turned to any facing: it comes to rest, in the same shape
        // (turned with it) as facing straight on.
        {
            double[] Settle(double yaw, out double moved)
            {
                var hair = ChainSpec.LongHair();
                hair.Collide = false;
                var shape = new ChainShape();
                int node = shape.AddRoot(0.05, 0, -0.1);
                for (int i = 0; i < 6; i++) node = shape.Add(node, 0.06, 0.4 - 0.15 * i, -1, -0.3 + 0.1 * i);
                var chain = new ChainState(shape, hair);
                double[] before = Array.Empty<double>();
                for (int i = 0; i <= 480; i++)
                {
                    chain.Update(i / 60.0, Turned(yaw, 0, 1.6, 0), new[] { 0, 1, 0.0 });
                    if (i == 420) before = (double[])chain.Nodes.Clone();
                }
                var p = chain.Nodes;
                moved = Largest(p, before);
                double c = Math.Cos(yaw), s = Math.Sin(yaw);
                var back = new double[p.Length]; // turned back to facing straight on
                for (int k = 0; k < p.Length; k += 3) { back[k] = c * p[k] - s * p[k + 2]; back[k + 1] = p[k + 1]; back[k + 2] = s * p[k] + c * p[k + 2]; }
                return back;
            }
            var straight = Settle(0, out double still);
            double off = 0;
            foreach (double yaw in new[] { Math.PI / 2, Math.PI, -Math.PI / 2, 0.7 })
            {
                off = Math.Max(off, Largest(Settle(yaw, out double moved), straight));
                still = Math.Max(still, moved);
            }
            check(still < 1e-5 && off < 1e-5, $"chain: held still at any facing it rests ({still * 1000:F3} mm in the last second) in the same shape ({off * 1000:F3} mm off)");
        }

        // Strands pressed on a shoulder and the head (light, holding their style), held still: they come to rest.
        {
            var hair = ChainSpec.LongHair();
            hair.Gravity = 0.05;
            var shape = new ChainShape();
            for (int k = 0; k < 3; k++)
            {
                int node = shape.AddRoot(-0.06 + 0.06 * k, 0, -0.1);
                for (int i = 0; i < 6; i++) node = shape.Add(node, 0.07, 0, -1, 0.1);
            }
            var chain = new ChainState(shape, hair) { Capsules = 2 };
            double[] before = Array.Empty<double>();
            for (int i = 0; i <= 480; i++)
            {
                chain.SetCapsule(0, -0.2, 1.3, -0.12, 0.2, 1.3, -0.12, 0.09);
                chain.SetCapsule(1, 0, 1.6, 0, 0, 1.6, 0, 0.1);
                chain.Update(i / 60.0, Moved(0, 1.6, 0), new[] { 0, 1, 0.0 });
                if (i == 420) before = (double[])chain.Nodes.Clone();
            }
            double moved = Largest(chain.Nodes, before);
            check(moved < 1e-3, $"chain: strands pressed on the body come to rest ({moved * 1000:F2} mm in the last second)");
        }

        // Angle limit: a stiff-free strand bound straight out sideways may bend at most 30 degrees a joint.
        {
            var s = ChainSpec.ShortHair();
            s.Restore = 0; s.Stiffness = 0; s.AngleLimit = 30; s.Collide = false;
            s.StyleLeash = 0; // this checks the bend limit alone; a leash would keep the tip from swinging far enough to bend
            var shape = new ChainShape();
            int node = shape.AddRoot(0, 0, 0);
            for (int i = 0; i < 4; i++) node = shape.Add(node, 0.05, 1, 0, 0);
            var chain = new ChainState(shape, s);
            for (int i = 0; i <= 120; i++) chain.Update(i / 60.0, Moved(0, 1.6, 0), new[] { 0, 1, 0.0 });
            var p = chain.Nodes;
            double worst = 0;
            for (int i = 1; i < 5; i++)
            {
                double ax = i == 1 ? 1 : p[(i - 1) * 3] - p[(i - 2) * 3], ay = i == 1 ? 0 : p[(i - 1) * 3 + 1] - p[(i - 2) * 3 + 1];
                double bx = p[i * 3] - p[(i - 1) * 3], by = p[i * 3 + 1] - p[(i - 1) * 3 + 1];
                worst = Math.Max(worst, Math.Acos(Math.Clamp((ax * bx + ay * by) / Math.Sqrt((ax * ax + ay * ay) * (bx * bx + by * by)), -1, 1)) * 180 / Math.PI);
            }
            check(worst < 31 && p[^2] < 1.6 - 0.05, $"chain: the angle limit holds ({worst:F1} degrees at most) while it still bends");
        }

        // A capsule (a thigh) under a hanging strand: no node ends inside it.
        {
            var s = ChainSpec.Skirt();
            s.Restore = 0;
            var shape = new ChainShape();
            int node = shape.AddRoot(0, 0, 0);
            for (int i = 0; i < 5; i++) node = shape.Add(node, 0.06, 0, -1, 0);
            var chain = new ChainState(shape, s) { Capsules = 1 };
            double inside = 0;
            for (int i = 0; i <= 120; i++)
            {
                chain.SetCapsule(0, 0.01, 1.4, -0.1, 0.01, 1.4, 0.1, 0.05);
                chain.Update(i / 60.0, Moved(0, 1.6, 0), new[] { 0, 1, 0.0 });
                if (i == 0) continue; // the first update puts it at rest in its bound shape, straight through the leg
                var p = chain.Nodes;
                for (int k = 1; k < 6; k++)
                {
                    double dx = p[k * 3] - 0.01, dy = p[k * 3 + 1] - 1.4;
                    if (Math.Abs(p[k * 3 + 2]) <= 0.1) inside = Math.Max(inside, 0.05 + s.Radius - Math.Sqrt(dx * dx + dy * dy));
                }
            }
            check(inside < 0.005, $"chain: nodes stay out of a capsule ({inside * 1000:F1} mm in at most)");
        }

        // A surface (a head as a sphere of points with outward normals, in a joint's own space, placed in the world by the shell's
        // pose). A strand swept through it is pushed around it and no node ends inside; a strand bound deep inside it (a skirt's
        // bones inside the legs) keeps the depth it was bound at instead of being shoved out.
        double[] SpherePose(double x, double y, double z)
        {
            var pose = new double[16];
            pose[0] = pose[5] = pose[10] = 1;
            pose[12] = x; pose[13] = y; pose[14] = z;
            return pose;
        }
        {
            var s = ChainSpec.Skirt();
            s.Gravity = 0.6;
            s.Restore = 0;
            s.StyleLeash = 0; // the surface alone: a surface that drives nothing sweeps the strand further than its leash allows
            var shape = new ChainShape();
            int node = shape.AddRoot(0.25, 0, 0);
            for (int i = 0; i < 5; i++) node = shape.Add(node, 0.06, 0, -1, 0);
            var chain = new ChainState(shape, s) { Shells = 1 };
            chain.SetShellData(0, new ChainShellData(Sphere(1600, 0.15)));
            var pose = SpherePose(0, 1.4, 0); // the sphere's middle 0.2 m under the anchor
            double inside = 0;
            for (int i = 0; i <= 420; i++)
            {
                // From frame 60 the anchor slides 0.5 m sideways, sweeping the strand through the sphere.
                double x = i < 60 ? 0.25 : 0.25 - 0.5 * Math.Min(1, (i - 60) / 300.0);
                chain.SetShellPose(0, pose);
                chain.Update(i / 60.0, Moved(x, 1.6, 0), new[] { x, 1, 0.0 });
                if (i < 60) continue;
                var p = chain.Nodes;
                for (int k = 1; k < 6; k++)
                {
                    double dx = p[k * 3] - 0, dy = p[k * 3 + 1] - 1.4, dz = p[k * 3 + 2];
                    inside = Math.Max(inside, 0.15 + s.Radius - Math.Sqrt(dx * dx + dy * dy + dz * dz));
                }
            }
            check(inside < 0.01, $"chain: a strand swept through a surface stays out of it ({inside * 1000:F1} mm in at most)");

            // A strand bound through a sphere's middle (a skirt's bones inside the body), held still: it keeps the shape it was
            // bound in — the surface does not shove it out.
            var hold = ChainSpec.Skirt();
            hold.Gravity = 0;
            hold.Restore = 1;
            var plain = new ChainState(shape, hold);
            var bound = new ChainState(shape, hold) { Shells = 1 };
            bound.SetShellData(0, new ChainShellData(Sphere(1600, 0.15)));
            var boundPose = SpherePose(0.25, 1.4, 0);
            for (int i = 0; i <= 240; i++)
            {
                bound.SetShellPose(0, boundPose);
                plain.Update(i / 60.0, Moved(0, 1.6, 0), new[] { 0, 1, 0.0 });
                bound.Update(i / 60.0, Moved(0, 1.6, 0), new[] { 0, 1, 0.0 });
            }
            double kept = Largest(plain.Nodes, bound.Nodes);
            check(kept < 0.02, $"chain: a strand bound inside a surface keeps its shape ({kept * 1000:F0} mm off the same strand without it)");

            // A strand bound above an open surface's rim (a skirt's bones in the pelvis, over the top of a thigh's surface): it is
            // not inside that surface, only past its edge, so when the thigh later comes at it from the side it is kept out like any
            // strand, not let in as deep as the rim's sideways normal said it was at the bind.
            var pastSpec = ChainSpec.Skirt();
            pastSpec.StyleLeash = 0; // the surface alone, as above
            var past = new ChainState(shape, pastSpec) { Shells = 1 };
            past.SetShellData(0, new ChainShellData(Tube(3000, 0.07, 0.7, 1.0)));
            double tubeIn = 0;
            for (int i = 0; i <= 480; i++)
            {
                double tx = i < 240 ? 0 : 0.25 * Math.Min(1, (i - 240) / 240.0);
                double ax = i < 60 ? 0 : 0.25 * Math.Min(1, (i - 60) / 60.0), ay = i < 120 ? 1.4 : 1.4 - 0.4 * Math.Min(1, (i - 120) / 60.0);
                past.SetShellPose(0, SpherePose(tx, 0, 0));
                past.Update(i / 60.0, Moved(ax - 0.25, ay, 0.03), new[] { ax - 0.25, ay - 0.4, 0.03 });
                if (i < 240) continue;
                var p = past.Nodes;
                for (int k = 1; k < 6; k++)
                    if (p[k * 3 + 1] > 0.72 && p[k * 3 + 1] < 0.98)
                        tubeIn = Math.Max(tubeIn, 0.07 - Math.Sqrt(Math.Pow(p[k * 3] - tx, 2) + Math.Pow(p[k * 3 + 2], 2)));
            }
            check(tubeIn < 0.01, $"chain: a strand bound past a surface's edge is kept out when it comes at it ({tubeIn * 1000:F1} mm in at most)");
        }
        {
            // Fabric: a cloth strand hung from the pelvis whose bones run down the middle of a thigh (as a skirt's do) with its fabric 2 cm
            // outside the skin. The thigh swings 4 cm into the fabric: the fabric is kept out by swinging the strand, and its pieces
            // keep their lengths (the bones that draw the cloth turn, they cannot stretch).
            var shape = new ChainShape();
            int last = shape.AddRoot(0, 0, 0);
            for (int k = 0; k < 5; k++)
            {
                last = shape.Add(last, 0.05, 0, -1, 0);
                shape.Fabric(last, 0.5, 0.09, 0, 0);
            }
            var cloth = new ChainState(shape, ChainSpec.Skirt()) { Shells = 1 };
            cloth.SetShellData(0, new ChainShellData(Tube(3000, 0.07, 0.6, 1.1)));
            double fabricIn = 0, stretch = 0;
            for (int i = 0; i <= 360; i++)
            {
                double tx = 0.04 * Math.Clamp((i - 60) / 120.0, 0, 1);
                cloth.SetShellPose(0, SpherePose(tx, 0, 0));
                cloth.Update(i / 60.0, Moved(0, 1.15, 0), new[] { 0, 0.75, 0.0 });
                if (i < 120) continue;
                var p = cloth.Nodes;
                for (int k = 1; k <= 5; k++)
                {
                    int a = (k - 1) * 3, b = k * 3;
                    double dx = p[b] - p[a], dy = p[b + 1] - p[a + 1], dz = p[b + 2] - p[a + 2], len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    stretch = Math.Max(stretch, Math.Abs(len - 0.05));
                    // The fabric where the solver keeps it: the offset turned the shortest way from straight down onto the piece
                    // (Rodrigues with k = (0, -1, 0) x v).
                    double vx = dx / len, vy = dy / len, vz = dz / len, c = -vy, kx = -vz, kz = vx, kr = kx * 0.09 / (1 + c);
                    double fx = (p[a] + p[b]) / 2 + 0.09 * c + kx * kr, fy = (p[a + 1] + p[b + 1]) / 2 + kz * 0.09, fz = (p[a + 2] + p[b + 2]) / 2 + kz * kr;
                    if (fy > 0.62 && fy < 1.08) fabricIn = Math.Max(fabricIn, 0.07 - Math.Sqrt(Math.Pow(fx - tx, 2) + fz * fz));
                }
            }
            check(fabricIn < 0.005 && stretch < 0.001, $"chain: fabric is kept out of a thigh that swings into it, by swinging the strand ({fabricIn * 1000:F1} mm in at most, pieces off length {stretch * 1000:F2} mm)");

            // The same strand against a thigh that drives it (ChainShellData.Drives): the thigh turns 50 degrees forward about the hip,
            // into the fabric. The strand's target turns with it, so the fabric stays out of the thigh and the strand's tip follows
            // where the thigh carries it; without the drive the same thigh swings through the fabric.
            (double In, double Off) Driven(bool drives)
            {
                var spec = ChainSpec.Skirt();
                spec.Gravity = 0.2;
                var strand = new ChainState(shape, spec) { Shells = 1 };
                strand.SetShellData(0, new ChainShellData(Tube(3000, 0.07, 0.6, 1.1)) { Drives = drives });
                double deepest = 0, off = 0;
                for (int i = 0; i <= 360; i++)
                {
                    double th = 50 * Math.PI / 180 * Math.Clamp((i - 60) / 60.0, 0, 1), c = Math.Cos(th), sn = Math.Sin(th);
                    strand.SetShellPose(0, new double[] { c, sn, 0, 0, -sn, c, 0, 0, 0, 0, 1, 0, 1.1 * sn, 1.1 - 1.1 * c, 0, 1 });
                    strand.Update(i / 60.0, Moved(0, 1.15, 0), new[] { 0, 0.75, 0.0 });
                    if (i < 120) continue;
                    var p = strand.Nodes;
                    for (int k = 1; k <= 5; k++)
                    {
                        int a = (k - 1) * 3, b = k * 3;
                        double dx = p[b] - p[a], dy = p[b + 1] - p[a + 1], dz = p[b + 2] - p[a + 2], len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                        double vx = dx / len, vy = dy / len, vz = dz / len, cc = -vy, kx = -vz, kz = vx, kr = kx * 0.09 / (1 + cc);
                        double fx = (p[a] + p[b]) / 2 + 0.09 * cc + kx * kr, fy = (p[a + 1] + p[b + 1]) / 2 + kz * 0.09, fz = (p[a + 2] + p[b + 2]) / 2 + kz * kr;
                        // Into the thigh's own space: turned back about the hip.
                        double rx = fx, ry = fy - 1.1, lx = c * rx + sn * ry, ly = -sn * rx + c * ry + 1.1;
                        if (ly > 0.62 && ly < 1.08) deepest = Math.Max(deepest, 0.07 - Math.Sqrt(lx * lx + fz * fz));
                    }
                    // The tip against its bound place carried by the thigh (it was bound at (0, -0.25) from the anchor at y 1.15).
                    double bx = 0, by = 0.9 - 1.1, tx = c * bx - sn * by, ty = sn * bx + c * by + 1.1;
                    off = Math.Max(off, Math.Sqrt(Math.Pow(p[15] - tx, 2) + Math.Pow(p[16] - ty, 2) + p[17] * p[17]));
                }
                return (deepest, off);
            }
            var driven = Driven(true);
            var loose = Driven(false);
            check(driven.In < 0.005 && driven.Off < 0.05 && loose.In > 0.02, $"chain: a strand driven by the thigh it hangs against follows it and stays out ({driven.In * 1000:F1} mm in, tip {driven.Off * 1000:F0} mm off the thigh's carry; undriven {loose.In * 1000:F0} mm in)");
        }
        {
            // The cloth between two bones: a skin point comes up between two nodes (a leg under a skirt) and the piece itself, not
            // only its nodes, is kept out of the surface. Without the cloth pass the same strand's middle sinks into the sphere; the
            // nodes never get near it, so only the piece's own test can catch this.
            ChainShape Long()
            {
                var shape = new ChainShape();
                int node = shape.AddRoot(0.25, 0, 0);
                node = shape.Add(node, 0.5, 0, -1, 0);
                shape.Add(node, 0.5, 0, -1, 0);
                return shape;
            }
            double MidPenetration(ChainSpec s)
            {
                var chain = new ChainState(Long(), s) { Shells = 1 };
                chain.SetShellData(0, new ChainShellData(Sphere(1600, 0.15)));
                var pose = SpherePose(0, 1.35, 0);
                double inside = 0;
                for (int i = 0; i <= 600; i++)
                {
                    double x = i < 60 ? 0.25 : 0.25 - 0.5 * Math.Min(1, (i - 60) / 500.0);
                    chain.SetShellPose(0, pose);
                    chain.Update(i / 60.0, Moved(x, 1.6, 0), new[] { x, 1, 0.0 });
                    if (i < 60) continue;
                    var p = chain.Nodes;
                    double mx = (p[0] + p[3]) * 0.5, my = (p[1] + p[4]) * 0.5, mz = (p[2] + p[5]) * 0.5;
                    inside = Math.Max(inside, 0.15 - Math.Sqrt(mx * mx + (my - 1.35) * (my - 1.35) + mz * mz));
                }
                return inside;
            }
            var cloth = ChainSpec.Skirt();
            cloth.Gravity = 0.4;
            cloth.Restore = 0;
            cloth.StyleLeash = 0; // the surface alone, as above
            var plain = ChainSpec.Skirt();
            plain.Gravity = 0.4;
            plain.Restore = 0;
            plain.StyleLeash = 0;
            plain.CollideCloth = false;
            double without = MidPenetration(plain), with = MidPenetration(cloth);
            check(without > 0.02, $"chain: a piece between two nodes sinks into a surface without the cloth pass ({without * 1000:F0} mm)");
            check(with < 0.01, $"chain: the cloth pass keeps the piece itself out ({with * 1000:F1} mm in at most)");
        }

        // A skirt: eight strands round a hip, linked side by side in a loop, swung by a turn of the hips; neighbours stay near their spacing.
        {
            var shape = new ChainShape();
            var strands = new List<int[]>();
            for (int k = 0; k < 8; k++)
            {
                double a = k * Math.PI / 4, cx = Math.Cos(a), cz = Math.Sin(a);
                var strand = new int[5];
                strand[0] = shape.AddRoot(cx * 0.15, 0, cz * 0.15);
                for (int j = 1; j < 5; j++) strand[j] = shape.Add(strand[j - 1], 0.08, cx * 0.3, -1, cz * 0.3);
                strands.Add(strand);
            }
            shape.LinkSide(strands, loop: true);
            var chain = new ChainState(shape, ChainSpec.Skirt());
            double worst = 0;
            for (int i = 0; i <= 120; i++)
            {
                double yaw = Math.Sin(i / 60.0 * 6) * 1.2;
                chain.Update(i / 60.0, Turned(yaw, 0, 1.0, 0), new[] { 0, 1, 0.0 });
                var p = chain.Nodes;
                for (int k = 0; k < 8; k++)
                {
                    int a = strands[k][4] * 3, b = strands[(k + 1) % 8][4] * 3;
                    double rest = 2 * Math.Sin(Math.PI / 8) * (0.15 + 4 * 0.08 * 0.3 / Math.Sqrt(1.09));
                    double d = Math.Sqrt(Math.Pow(p[b] - p[a], 2) + Math.Pow(p[b + 1] - p[a + 1], 2) + Math.Pow(p[b + 2] - p[a + 2], 2));
                    worst = Math.Max(worst, Math.Abs(d / rest - 1));
                }
            }
            check(worst < 0.35, $"chain: skirt neighbours stay near their spacing ({worst * 100:F0}% off at most)");
        }

        // A skirt dragged sideways: every piece stays inside its own radial plane's azimuth limit (no panel folds into the one beside
        // it); the same strands without the limit swing far out of plane, so the checks measure the limit and not the setup.
        {
            double Dragged(double azimuth)
            {
                var s = ChainSpec.Skirt();
                s.AzimuthLimit = azimuth;
                s.Restore = 0;
                var shape = new ChainShape();
                var strands = new List<int[]>();
                var restDir = new List<double[]>(); // per strand: the bound direction of every piece (same for all of them)
                var rootOff = new List<double[]>(); // per strand: the root's bound place from the anchor
                for (int k = 0; k < 4; k++)
                {
                    double a = k * Math.PI / 2, cx = Math.Cos(a), cz = Math.Sin(a);
                    var strand = new int[5];
                    strand[0] = shape.AddRoot(cx * 0.15, 0, cz * 0.15);
                    for (int j = 1; j < 5; j++) strand[j] = shape.Add(strand[j - 1], 0.08, cx * 0.35, -1, cz * 0.35);
                    strands.Add(strand);
                    double dl = Math.Sqrt(0.35 * 0.35 + 1);
                    restDir.Add(new[] { cx * 0.35 / dl, -1 / dl, cz * 0.35 / dl });
                    rootOff.Add(new[] { cx * 0.15, 0.0, cz * 0.15 });
                }
                shape.LinkSide(strands, loop: true);
                var chain = new ChainState(shape, s);
                double worst = 0;
                for (int i = 1; i <= 120; i++)
                {
                    double x = i / 60.0 * 2; // 2 m/s sideways
                    chain.Update(i / 60.0, Moved(x, 1.0, 0), new[] { x, 1.0, 0.0 });
                    var p = chain.Nodes;
                    for (int k = 0; k < 4; k++)
                    {
                        var d = restDir[k];
                        var strand = strands[k];
                        for (int j = 1; j < 5; j++)
                        {
                            int o = strand[j] * 3, a = strand[j - 1] * 3;
                            double ex = p[o] - p[a], ey = p[o + 1] - p[a + 1], ez = p[o + 2] - p[a + 2];
                            double len = Math.Sqrt(ex * ex + ey * ey + ez * ez);
                            if (len < 1e-9) continue;
                            ex /= len; ey /= len; ez /= len;
                            // The parent's bound place from the anchor: the radial reference, less its part along the bound direction.
                            var r0 = rootOff[k];
                            double px = r0[0] + d[0] * (j - 1) * 0.08, py = r0[1] + d[1] * (j - 1) * 0.08, pz = r0[2] + d[2] * (j - 1) * 0.08;
                            double qc = px * d[0] + py * d[1] + pz * d[2];
                            double fx = px - d[0] * qc, fy = py - d[1] * qc, fz = pz - d[2] * qc;
                            double fl = Math.Sqrt(fx * fx + fy * fy + fz * fz);
                            if (fl < 1e-9) continue;
                            fx /= fl; fy /= fl; fz /= fl;
                            // The plane's normal: bound direction x radial. The piece's angle from the plane is what the limit bounds.
                            double gx = d[1] * fz - d[2] * fy, gy = d[2] * fx - d[0] * fz, gz = d[0] * fy - d[1] * fx;
                            double gl = Math.Sqrt(gx * gx + gy * gy + gz * gz);
                            if (gl < 1e-9) continue;
                            double off = (ex * gx + ey * gy + ez * gz) / gl;
                            worst = Math.Max(worst, Math.Asin(Math.Min(1, Math.Abs(off))) * 180 / Math.PI);
                        }
                    }
                }
                return worst;
            }
            double limited = Dragged(18);
            double unconstrained = Dragged(0);
            check(limited < 19.5, $"chain: a skirt's piece stays in its own plane ({limited:F1} degrees out at most)");
            check(unconstrained > 15, $"chain: without the azimuth limit the same piece swings out of plane ({unconstrained:F1} degrees)");
        }

        // A teleport (3 m in one frame) puts the chain at rest; a knock-back at 30 m/s does not fling it past the speed cap.
        {
            var chain = Charm(new ChainSpec { Collide = false }, 0.05, 6, arm);
            for (int i = 0; i <= 30; i++) chain.Update(i / 60.0, Moved(0, 1.1, i / 60.0), new[] { 0, 1.6, i / 60.0 - 0.4 });
            chain.Update(31 / 60.0, Moved(3, 1.1, 0.5), new[] { 3, 1.6, 0.1 });
            var p = chain.Nodes;
            check(Math.Abs(p[^3] - 3) < 1e-9 && Math.Abs(p[^1] - 0.5) < 1e-9, "chain: a teleport puts it at rest where it landed");
            double fast = 0;
            double[] before = (double[])p.Clone();
            for (int i = 32; i <= 50; i++)
            {
                double x = 3 + (i - 31) * 0.5; // 30 m/s
                chain.Update(i / 60.0, Moved(x, 1.1, 0.5), new[] { x, 1.6, 0.1 });
                for (int k = 3; k < p.Length; k += 3)
                    fast = Math.Max(fast, Math.Abs(p[k] - p[0] - before[k] + before[0]) * 60);
                Array.Copy(p, before, p.Length);
            }
            check(fast <= 3 * ChainState.MaxSpeed, $"chain: a knock-back does not fling it ({fast:F2} m/s against the anchor)");
        }

        // A dance's fast step and spin carry the cloth; only a real teleport puts it back on its bind shape (whose bones are inside
        // the body, so a reset during a dance is the frame the cloth looks like it passes through the model).
        {
            var shape = new ChainShape();
            int node = shape.AddRoot(0.15, 0, 0);
            for (int i = 0; i < 5; i++) node = shape.Add(node, 0.07, 0, -1, 0);
            var cloth = new ChainState(shape, ChainSpec.Skirt());
            for (int i = 0; i <= 30; i++) cloth.Update(i / 60.0, Moved(0, 1.6, 0), new[] { 0, 1, 0.0 });
            cloth.Update(31 / 60.0, Moved(1.0, 1.6, 0), new[] { 1.0, 1, 0.0 });
            check(cloth.DebugRestJumps == 0, "chain: a fast step carries the cloth instead of resetting it");
            cloth.Update(32 / 60.0, Moved(4.0, 1.6, 0), new[] { 4.0, 1, 0.0 });
            check(cloth.DebugRestJumps == 1, "chain: a teleport still puts the cloth at rest");
            int jumps = cloth.DebugRestJumps;
            cloth.Update(33 / 60.0, Turned(Math.PI, 4.0, 1.6, 0), new[] { 4.0, 1, 0.0 });
            check(cloth.DebugRestJumps == jumps, "chain: a fast spin follows the anchor instead of resetting the cloth");
        }

        // The same skirt worn at the game's 1.509x: geometry, radius and gravity scale, and every fixed distance or speed of the
        // solver (jump, body limit, speed cap, the damping rate, the short-strand thresholds) must scale with them, so the
        // dimensionless motion is the editor's. A 0.4 m anchor jump (0.6 m at scale) pins the jump threshold; the 8 m/s body and
        // the 2.5 Hz swing pin the body limit and the speed cap.
        {
            const double worn = 1.509;
            ChainShape Ring(double k)
            {
                var shape = new ChainShape();
                var strands = new List<int[]>();
                for (int i = 0; i < 8; i++)
                {
                    double a = i * Math.PI / 4, cx = Math.Cos(a), cz = Math.Sin(a);
                    var strand = new int[7];
                    strand[0] = shape.AddRoot(cx * 0.15 * k, 0, cz * 0.15 * k);
                    for (int j = 1; j < 7; j++) strand[j] = shape.Add(strand[j - 1], 0.07 * k, cx * 0.3, -1, cz * 0.3);
                    strands.Add(strand);
                }
                shape.LinkSide(strands, loop: true);
                return shape;
            }
            var smallSpec = ChainSpec.Skirt();
            var wornSpec = ChainSpec.Skirt();
            wornSpec.Radius *= worn;
            wornSpec.Gravity *= worn;
            var small = new ChainState(Ring(1), smallSpec);
            var large = new ChainState(Ring(worn), wornSpec) { Scale = worn };
            double apart = 0;
            for (int i = 0; i <= 240; i++)
            {
                double t = i / 60.0;
                double jump = i == 60 ? 0.4 : 0;
                double x = 0.4 * Math.Sin(2 * Math.PI * 2.5 * t), z = 8 * t + jump;
                double yaw = 0.6 * Math.Sin(2 * Math.PI * 0.9 * t);
                small.Update(t, Turned(yaw, x, 1.6, z), new[] { x, 1.0, 8 * t });
                large.Update(t, Turned(yaw, x * worn, 1.6 * worn, z * worn), new[] { x * worn, 1.0 * worn, 8 * t * worn });
                var a = small.Nodes;
                var b = large.Nodes;
                for (int k = 0; k < a.Length; k += 3)
                    apart = Math.Max(apart, Math.Sqrt(Math.Pow(a[k] - b[k] / worn, 2) + Math.Pow(a[k + 1] - b[k + 1] / worn, 2) + Math.Pow(a[k + 2] - b[k + 2] / worn, 2)));
            }
            check(apart < 2e-3, $"chain: a 1.509x model moves like the editor ({apart * 1000:F2} mm apart)");

            // The same at the style's length thresholds: a short fringe and a long lock keep the same hold, bend limit and leash
            // allowance when the model is worn larger (they are chosen from reach in metres, so those metres scale).
            ChainShape Locks(double k)
            {
                var shape = new ChainShape();
                int n = shape.AddRoot(0, 0, 0);
                for (int i = 0; i < 5; i++) n = shape.Add(n, 0.03 * k, 1, -0.2 * i, 0);
                n = shape.AddRoot(0, 0, 0.5 * k);
                for (int i = 0; i < 10; i++) n = shape.Add(n, 0.06 * k, 1, 0.1 * i, 0);
                return shape;
            }
            var hairSmallSpec = ChainSpec.LongHair();
            var hairWornSpec = ChainSpec.LongHair();
            hairSmallSpec.Collide = hairWornSpec.Collide = false;
            hairSmallSpec.SelfCollide = hairWornSpec.SelfCollide = false;
            hairWornSpec.Radius *= worn;
            hairWornSpec.Gravity *= worn;
            var hairSmall = new ChainState(Locks(1), hairSmallSpec);
            var hairWorn = new ChainState(Locks(worn), hairWornSpec) { Scale = worn };
            double hairApart = 0;
            for (int i = 0; i <= 180; i++)
            {
                double t = i / 60.0, x = 0.5 * Math.Sin(2 * Math.PI * 2 * t), yaw = 0.5 * Math.Sin(2 * Math.PI * 1.1 * t);
                hairSmall.Update(t, Turned(yaw, x, 1.6, 0), new[] { x, 1.0, 0.0 });
                hairWorn.Update(t, Turned(yaw, x * worn, 1.6 * worn, 0), new[] { x * worn, 1.0 * worn, 0.0 });
                var a = hairSmall.Nodes;
                var b = hairWorn.Nodes;
                for (int k = 0; k < a.Length; k += 3)
                    hairApart = Math.Max(hairApart, Math.Sqrt(Math.Pow(a[k] - b[k] / worn, 2) + Math.Pow(a[k + 1] - b[k + 1] / worn, 2) + Math.Pow(a[k + 2] - b[k + 2] / worn, 2)));
            }
            check(hairApart < 2e-3, $"chain: a 1.509x hairstyle moves like the editor ({hairApart * 1000:F2} mm apart)");

            // The surface shell too: its points stay in the bone's local space while the pose carries the worn scale, exactly as
            // the game's shell does. A strand swept through the same sphere must follow the same dimensionless path.
            double[] SpherePoseAt(double x, double y, double z, double k)
            {
                var pose = new double[16];
                pose[0] = pose[5] = pose[10] = k;
                pose[12] = x * k; pose[13] = y * k; pose[14] = z * k;
                return pose;
            }
            ChainShape Strand(double k)
            {
                var shape = new ChainShape();
                int n = shape.AddRoot(0.25 * k, 0, 0);
                for (int i = 0; i < 5; i++) n = shape.Add(n, 0.06 * k, 0, -1, 0);
                return shape;
            }
            ChainSpec ShellSpec(bool scaled)
            {
                var c = ChainSpec.Skirt();
                c.Gravity = 0.6;
                c.Restore = 0;
                if (scaled) { c.Radius *= worn; c.Gravity *= worn; }
                return c;
            }
            var shellSmall = new ChainState(Strand(1), ShellSpec(false)) { Shells = 1 };
            var shellWorn = new ChainState(Strand(worn), ShellSpec(true)) { Scale = worn, Shells = 1 };
            shellSmall.SetShellData(0, new ChainShellData(Sphere(1600, 0.15)));
            shellWorn.SetShellData(0, new ChainShellData(Sphere(1600, 0.15)));
            var shellPoseSmall = SpherePoseAt(0, 1.4, 0, 1);
            var shellPoseWorn = SpherePoseAt(0, 1.4, 0, worn);
            double shellApart2 = 0;
            for (int i = 0; i <= 420; i++)
            {
                double x = i < 60 ? 0.25 : 0.25 - 0.5 * Math.Min(1, (i - 60) / 300.0);
                shellSmall.SetShellPose(0, shellPoseSmall);
                shellWorn.SetShellPose(0, shellPoseWorn);
                shellSmall.Update(i / 60.0, Moved(x, 1.6, 0), new[] { x, 1, 0.0 });
                shellWorn.Update(i / 60.0, Moved(x * worn, 1.6 * worn, 0), new[] { x * worn, 1 * worn, 0.0 });
                var a = shellSmall.Nodes;
                var b = shellWorn.Nodes;
                for (int k = 0; k < a.Length; k += 3)
                    shellApart2 = Math.Max(shellApart2, Math.Sqrt(Math.Pow(a[k] - b[k] / worn, 2) + Math.Pow(a[k + 1] - b[k + 1] / worn, 2) + Math.Pow(a[k + 2] - b[k + 2] / worn, 2)));
            }
            check(shellApart2 < 2e-3, $"chain: a 1.509x surface pushes like the editor ({shellApart2 * 1000:F2} mm apart)");
        }
    }

    // ForgeSkin's charm shape: the hanging point, `segments` pieces straight down, the pendant heavier on a rigid arm.
    static ChainState Charm(ChainSpec spec, double length, int segments, double arm)
    {
        var shape = new ChainShape();
        int node = shape.AddRoot(0, 0, 0);
        for (int i = 0; i < segments; i++) node = shape.Add(node, length / segments, 0, -1, 0);
        shape.Add(node, arm, 0, -1, 0, 4, 0.5, arm * 0.8, rigid: true);
        return new ChainState(shape, spec);
    }

    // A sphere of surface points with their outward normals (x, y, z, nx, ny, nz each), `n` of them, radius r around the origin.
    static double[] Sphere(int n, double r)
    {
        var points = new double[n * 6];
        double golden = Math.PI * (3 - Math.Sqrt(5));
        for (int i = 0; i < n; i++)
        {
            double y = 1 - 2 * (i + 0.5) / n, rad = Math.Sqrt(1 - y * y), a = golden * i;
            double x = Math.Cos(a) * rad, z = Math.Sin(a) * rad;
            int o = i * 6;
            points[o] = x * r; points[o + 1] = y * r; points[o + 2] = z * r;
            points[o + 3] = x; points[o + 4] = y; points[o + 5] = z;
        }
        return points;
    }

    // An open vertical tube round the Y axis between two heights (a thigh's surface: its top belongs to the hip), outward normals.
    static double[] Tube(int n, double r, double bottom, double top)
    {
        var points = new double[n * 6];
        int rings = (int)Math.Sqrt(n), round = n / rings;
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * (i % round) / round, y = bottom + (top - bottom) * Math.Min(1, (i / round) / (double)(rings - 1));
            int o = i * 6;
            points[o] = Math.Cos(a) * r; points[o + 1] = y; points[o + 2] = Math.Sin(a) * r;
            points[o + 3] = Math.Cos(a); points[o + 4] = 0; points[o + 5] = Math.Sin(a);
        }
        return points;
    }

    // The largest distance between the same node in two node arrays.
    static double Largest(double[] a, double[] b)
    {
        double worst = 0;
        for (int k = 0; k < a.Length; k += 3)
            worst = Math.Max(worst, Math.Sqrt(Math.Pow(a[k] - b[k], 2) + Math.Pow(a[k + 1] - b[k + 1], 2) + Math.Pow(a[k + 2] - b[k + 2], 2)));
        return worst;
    }

    static double[] Moved(double x, double y, double z) => new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, x, y, z, 1 };

    static double[] Turned(double yaw, double x, double y, double z)
    {
        double c = Math.Cos(yaw), s = Math.Sin(yaw);
        return new double[] { c, 0, -s, 0, 0, 1, 0, 0, s, 0, c, 0, x, y, z, 1 };
    }

    // The moving anchor: a Lissajous path turning (yaw and pitch, scaled 1.2) at uneven frame times with a 0.3 s stall at frame 120, the
    // body on part of its path; then held still and upright for 2.1 s.
    static List<(double Time, double[] M, double[] Body)> Frames()
    {
        var frames = new List<(double Time, double[] M, double[] Body)>();
        double time = 0;
        double[] dts = { 1 / 60.0, 1 / 144.0, 1 / 30.0, 0.011 };
        for (int i = 0; i < 240; i++)
        {
            time += i == 120 ? 0.3 : dts[i % dts.Length];
            double yaw = Math.Sin(time * 3.1) * 1.4, pitch = Math.Sin(time * 1.7) * 0.5, s = 1.2;
            double cy = Math.Cos(yaw), sy = Math.Sin(yaw), cp = Math.Cos(pitch), sp = Math.Sin(pitch);
            frames.Add((time, new[] { cy * s, 0, -sy * s, 0, sy * sp * s, cp * s, cy * sp * s, 0, sy * cp * s, -sp * s, cy * cp * s, 0,
                0.3 + Math.Sin(time * 2.3) * 0.25, 1.1 + Math.Sin(time * 4.1) * 0.08, -0.2 + Math.Cos(time * 1.9) * 0.3, 1 },
                new[] { 0.3 + Math.Sin(time * 2.3) * 0.25, 1.6, -0.5 + Math.Cos(time * 1.9) * 0.2 }));
        }
        var m = frames[^1].M;
        double[] still = { 1.2, 0, 0, 0, 0, 1.2, 0, 0, 0, 0, 1.2, 0, m[12], m[13], m[14], 1 };
        var body = frames[^1].Body;
        for (int i = 0; i < 120; i++) frames.Add((time += dts[i % dts.Length] * 1.2, still, body));
        return frames;
    }
}
