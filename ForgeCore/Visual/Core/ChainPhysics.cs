using System;
using System.Collections.Generic;

namespace ForgeVisual;

/// <summary>What an author sets for a chain (a charm's rope, a hair strand, a skirt, a tail or ribbon), plus the solver's sub-step and
/// passes. The few author values stay in plain ranges; the presets are starting points the editor offers.</summary>
public sealed class ChainSpec
{
    /// <summary>0: limp; 1: a stiff wire that keeps going the way the piece above it points.</summary>
    public double Stiffness { get; set; } = 0.25;
    /// <summary>0: none; 1: springs straight back to the shape it was bound in (hair keeps its style). A hanging rope has none.</summary>
    public double Restore { get; set; }
    /// <summary>0: off; otherwise the share of its own reach a node may stray from the bound shape (hair's leash). A fringe is
    /// short, so a small value holds it on the brow; a ponytail's tip is far down its strand and may swing the same share of a
    /// metre. Bounds the swing hard: the springs alone let a fast dance throw short strands most of their length away, which
    /// reads as hair lifting off the head.</summary>
    public double StyleLeash { get; set; }
    /// <summary>0: swings long; 1: settles at once.</summary>
    public double Damping { get; set; } = 0.3;
    /// <summary>The chain's mass, 0.1 to 3: heavier swings longer, lighter settles sooner.</summary>
    public double Weight { get; set; } = 1;
    /// <summary>Times real gravity.</summary>
    public double Gravity { get; set; } = 1;
    /// <summary>0 to 1: the share of the body's own move the chain does not ride along with, so it feels that much of the body's
    /// speeding up and slowing down. The anchor's move against the body (a turn, a flick, a step's bob) always reaches it whole.</summary>
    public double Inertia { get; set; } = 0.15;
    /// <summary>Metres: how far a node is kept out of the colliders.</summary>
    public double Radius { get; set; } = 0.001;
    /// <summary>Degrees a joint may bend away from its bound bend; 0: no limit.</summary>
    public double AngleLimit { get; set; }
    /// <summary>Degrees a joint may swing sideways out of its own radial plane (the bound direction and the way the body spreads
    /// from the anchor); 0: no limit. A skirt keeps its panels from folding into their neighbours with a small value; hair leaves it 0.</summary>
    public double AzimuthLimit { get; set; }
    /// <summary>Kept out of the colliders (boxes and capsules).</summary>
    public bool Collide { get; set; } = true;
    /// <summary>The cloth along the pieces — down each strand and across the links between strands — is kept out of the surface
    /// shells too, each piece tested at its middle: bones are centimetres apart, and a leg that comes up between two of them pushes
    /// the panel instead of passing through it where no node is.</summary>
    public bool CollideCloth { get; set; }
    /// <summary>Nodes of different strands of one set are kept apart, at their bound distance (or their radii when the strands lie
    /// further apart than that): hair strands slide along each other instead of crossing and knotting.</summary>
    public bool SelfCollide { get; set; }
    /// <summary>The fixed sub-step in seconds; every frame runs as many as fit, whatever the frame rate.</summary>
    public double Step { get; set; } = 1.0 / 240;
    /// <summary>Per sub-step: iterations of (length passes, side links, colliders).</summary>
    public int Iterations { get; set; } = 6;
    public int LengthPasses { get; set; } = 4;

    /// <summary>A short charm chain: ForgeSkin's charm defaults (fine sub-step, many passes: a few nodes, cheap).</summary>
    public static ChainSpec Charm() => new();
    /// <summary>Long hair or a ponytail: holds its style loosely, swings wide, bends at most 50 degrees a joint. (A finer sub-step
    /// was tried and taken back: every collision budget is per sub-step, so halving the step doubles the correction rate the hair
    /// can take per frame, and a calm walk measured twice as jittery.)</summary>
    public static ChainSpec LongHair() => new() { Stiffness = 0.35, Restore = 0.3, Damping = 0.4, Inertia = 0.3, Radius = 0.015, AngleLimit = 50, StyleLeash = 0.3, SelfCollide = true, Step = 1.0 / 120, Iterations = 2, LengthPasses = 1 };
    /// <summary>Short hair: springs back fast, bends little, and stays close to the head (a fringe is half its length off the brow
    /// after one fast move otherwise).</summary>
    public static ChainSpec ShortHair() => new() { Stiffness = 0.6, Restore = 0.7, Damping = 0.6, Inertia = 0.2, Radius = 0.012, AngleLimit = 30, StyleLeash = 0.18, SelfCollide = true, Step = 1.0 / 120, Iterations = 2, LengthPasses = 1 };
    /// <summary>A skirt: strands linked side by side (<see cref="ChainShape.LinkSide"/>), each held to its own radial plane
    /// (<see cref="AzimuthLimit"/>) so panels do not fold into each other, loosely back toward its shape, the cloth between the
    /// bones (<see cref="CollideCloth"/>) kept off the legs. The standoff is a centimetre wider than a bare bone needs: the fabric
    /// lies a couple of centimetres out from the bones, and at the old 2 cm a thigh came through the cloth where no node was.</summary>
    public static ChainSpec Skirt() => new() { Stiffness = 0.15, Restore = 0.25, Damping = 0.35, Inertia = 0.2, Radius = 0.03, AngleLimit = 70, StyleLeash = 0.3, AzimuthLimit = 18, CollideCloth = true, SelfCollide = true, Step = 1.0 / 240, Iterations = 3, LengthPasses = 2 };
    /// <summary>A tail or ribbon: hangs loosely but settles like cloth (the old 0.2/0.2 rang for seconds after every step).</summary>
    public static ChainSpec Tail() => new() { Stiffness = 0.45, Restore = 0.1, Damping = 0.6, Inertia = 0.4, Radius = 0.01, StyleLeash = 0.4, SelfCollide = true, Step = 1.0 / 120, Iterations = 2, LengthPasses = 1 };
}

/// <summary>The nodes of one chain set in the anchor's frame, built once: roots pinned to the anchor, every other node hanging from a
/// parent added before it, and side links between strands (a skirt's hem). Setup only.</summary>
public sealed class ChainShape
{
    internal readonly List<int> Parent = new();
    internal readonly List<double> Length = new(), Mass = new(), Bend = new(), Radius = new();
    internal readonly List<double> Dir = new(); // per node: a root's offset from the anchor, else the unit direction from its parent at rest
    internal readonly List<bool> Rigid = new();
    internal readonly List<int> Side = new(); // node pairs
    internal readonly List<int> ClothNode = new(); // per fabric point: the node whose piece (from its parent) carries it
    internal readonly List<double> Cloth = new(); // per fabric point: t, x, y, z

    public int Count => Parent.Count;

    /// <summary>A root, pinned at (x, y, z) in the anchor's frame.</summary>
    public int AddRoot(double x, double y, double z) => Push(-1, 0, x, y, z, 0, 0, 0, false);

    /// <summary>A node `length` from `parent` along (dx, dy, dz) in the anchor's frame at rest. `mass` against an ordinary node, `bend` scales
    /// the bend spring on it, `radius` below 0 takes the spec's. A rigid node is held at exactly its length (a pendant's arm); any
    /// other is held at most its length.</summary>
    public int Add(int parent, double length, double dx, double dy, double dz, double mass = 1, double bend = 1, double radius = -1, bool rigid = false)
    {
        if (parent < 0 || parent >= Count) throw new ArgumentOutOfRangeException(nameof(parent));
        double l = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (!(l > 0) || !(length > 0) || !(mass > 0)) throw new ArgumentException("a node needs a length, a direction and a mass");
        return Push(parent, length, dx / l, dy / l, dz / l, mass, bend, radius, rigid);
    }

    /// <summary>A point of the fabric skinned to the piece ending at `node`, at rest: `t` of the way down the piece, then (x, y, z) off
    /// it in the anchor's frame, world metres; a piece may carry several (across a panel's width, along its length). A chain with fabric
    /// keeps that fabric out of the body surfaces instead of its bones: a cloth rig's bones run inside the legs on purpose, and only the
    /// cloth they carry has to stay outside.</summary>
    public void Fabric(int node, double t, double x, double y, double z)
    {
        if (node < 0 || node >= Count || Parent[node] < 0) throw new ArgumentOutOfRangeException(nameof(node));
        if (!double.IsFinite(t) || !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)) throw new ArgumentException("fabric is four numbers");
        ClothNode.Add(node);
        Cloth.Add(t); Cloth.Add(x); Cloth.Add(y); Cloth.Add(z);
    }

    /// <summary>Links two nodes side by side at their rest distance (half as stiff as a strand's own pieces).</summary>
    public void Link(int a, int b)
    {
        if (a == b || a < 0 || b < 0 || a >= Count || b >= Count) throw new ArgumentOutOfRangeException(nameof(b));
        Side.Add(a); Side.Add(b);
    }

    /// <summary>Links strands side by side, node to node at the same depth below their roots (a skirt's hem), each to the next, and
    /// the last to the first when `loop`. A strand is its node indices from the root down.</summary>
    public void LinkSide(IReadOnlyList<int[]> strands, bool loop)
    {
        for (int k = 0; k + 1 < strands.Count || (loop && strands.Count > 2 && k < strands.Count); k++)
        {
            int[] a = strands[k], b = strands[(k + 1) % strands.Count];
            for (int j = 1; j < Math.Min(a.Length, b.Length); j++) Link(a[j], b[j]);
        }
    }

    int Push(int parent, double length, double x, double y, double z, double mass, double bend, double radius, bool rigid)
    {
        Parent.Add(parent); Length.Add(length); Mass.Add(mass); Bend.Add(bend); Radius.Add(radius); Rigid.Add(rigid);
        Dir.Add(x); Dir.Add(y); Dir.Add(z);
        return Count - 1;
    }
}

/// <summary>
/// One chain set moving: the one chain physics for charm ropes and spring bones (hair, skirts, tails), with no game reference so the
/// game, the skin editor's backend and the tests run the same code.
/// <para>Verlet integration in world space at a fixed sub-step under real gravity; each sub-step takes the anchor where it was at that
/// sub-step's own time in the frame (pose interpolated), so the motion does not depend on the frame rate; a frame of more than
/// <see cref="MaxSteps"/> sub-steps only follows the rest rigidly. The chain rides along with (1 - Inertia) of the body's move, so it
/// feels only the rest of the body's speeding up and slowing down; a body faster than <see cref="BodyLimit"/> takes its excess whole
/// (a fall or a knock-back does not fling it). Drag acts on the nodes' move against the anchor, growing with that speed; weight lessens
/// drag and springs. Per sub-step: the bend once (a damped spring toward the straight continuation of the piece above, the first piece
/// toward its bound direction; the restore spring toward the bound shape carried by the anchor; both pulls equal and opposite between a
/// node and the one above, shared by their weights, so a chain held still against a collider settles; the angle limit), then per iteration the
/// length passes, the side links and the colliders (boxes in the anchor's frame, capsules in the world interpolated per sub-step), last
/// a sweep down every strand so no piece is longer than its length. A frame after a gap, or whose anchor or body jumps a teleport's
/// distance, puts the chain at rest in its bound shape; a fast step or spin is followed rigidly instead.</para>
/// <para>Data is flat arrays indexed by node (positions, previous positions, weights, lengths), parents before children, so a sweep is
/// one loop; nothing allocates after construction.</para>
/// Matrices are 16 numbers, column-major (element [col * 4 + row]); a pose is position xyz and quaternion xyzw.
/// </summary>
public sealed class ChainState
{
    public const int MaxSteps = 24; // per update; the chain only follows the rest of a longer frame rigidly
    public const double Gap = 0.25; // s between updates after which the chain is put at rest
    public const double Jump = 0.5; // m the anchor or the body moved in one update; a jump this far is the unit of a teleport
    public const double G = 9.81;
    public const double BodyLimit = 10; // m/s of the body's move beyond which the excess is carried whole
    public const double SwingDrag = 0.5; // drag per (m/s against the anchor / reach), at Weight 1
    public const double MinPiece = 0.005; // m: callers make no piece shorter than this
    public const double MaxSpeed = 5; // m/s, a node's speed relative to the anchor
    // Bend spring: BendRate x Stiffness² rad/s at Weight 1; per sub-step pull and damping capped for stability. Restore likewise.
    public const double BendRate = 120, BendMax = 0.25, BendDampMax = 0.5, RestoreRate = 90, RestoreMax = 0.5;
    public const double DampSlow = 0.04, DampFast = 1; // drag as a share of critical damping at Damping 0 and 1, Weight 1
    public const double SideStiffness = 0.5; // side links against a strand's own pieces
    public const double DriveReach = 0.2; // m at scale 1: how far from a driving surface at the bind a node still follows it

    readonly int _n, _iterations, _passes;
    readonly double _step, _bend, _bendDamp, _restore, _cosLimit, _sinLimit, _carry;
    // Rates that depend on the worn size: the game binds the model at 1.5–1.8× the editor's, and these must follow a rescale too.
    double _gravity, _keep, _swingDrag;
    readonly double _mass, _zeta, _bendLimit;
    readonly bool _collide, _collideCloth, _self;
    // World metres per authored metre: a character is worn scaled (the game's body is 1.5–1.8 times the editor's model), so the
    // collision distances that are fixed inside the solver — the standoff from a surface, the dead band, the correction budgets,
    // the contact band — are multiplied by it, as the caller already multiplies the spec's own radius and gravity. Without this the
    // cloth is relatively thinner in the game than in the editor, and hair strands and skirt panels pass into the body and each
    // other exactly as they do not in the preview. The node radii themselves are NOT scaled here: they already arrive in world metres.
    double _scale = 1, _appliedScale = double.NaN;
    double _margin, _standoff, _fabGap, _dead, _budget, _segBudget, _fabBudget, _softCap, _bandFloor, _floorSlack;
    double _reach; // the longest way from a root down, world metres
    readonly double _bendShare; // the bend and restore pull per iteration; applied in every iteration, the same total as before
    readonly int[] _parent, _side, _rootOf;
    readonly double[] _strandReach; // per root: the longest reach of the strand it starts
    // Per piece, its two nodes (down a strand and across the side links); per cross-strand node pair, the distance they may not
    // come closer than (their bound distance, or their radii when the strands lie further apart than that), the side they were
    // bound on (the anti-swap constraint) and their bound distance.
    int[] _segA = Array.Empty<int>(), _segB = Array.Empty<int>();
    int[] _selfA = Array.Empty<int>(), _selfB = Array.Empty<int>();
    double[] _selfFloor = Array.Empty<double>(), _selfRest = Array.Empty<double>(), _selfDir = Array.Empty<double>(), _selfDirSub = Array.Empty<double>();
    readonly bool[] _rigid, _straight;
    readonly double[] _len, _dir, _w, _bendK, _bendD, _radius, _sideLen, _restoreK;
    // Per node, the bend limit: short strands are held closer to their own direction than long ones (a fringe that may swing 50
    // degrees a joint lifts whole centimetres off the brow; the same limit on a long wave reads as hair moving).
    readonly double[] _cosLimitK, _sinLimitK;
    readonly bool _hasLimit;
    // The style leash (see ChainSpec.StyleLeash): the per-node allowance is _leash x (a little + the node's reach down its strand).
    readonly double _leash;
    readonly double[] _reachTo;
    readonly double[] _p, _q, _rm;
    readonly double[] _restPos; // every node's bound place in the anchor's frame (the radial plane of the azimuth limit)
    readonly double _azimuth;   // radians the piece may swing sideways out of its own radial plane; 0: no limit
    double[] _box = Array.Empty<double>(); // per box: centre 3, axes 3x3, half sizes 3, in the anchor's frame; grown by AddBox
    double[] _world = Array.Empty<double>(); // the same in world space, per sub-step
    int _boxes, _capsules;
    double[] _cap = Array.Empty<double>(), _capFrom = Array.Empty<double>(), _capSub = Array.Empty<double>(); // a, b, radius; grown by Capsules
    int _shells;
    ChainShellData[] _shellData = Array.Empty<ChainShellData>();
    // Per shell, the shell bone's pose: position 3, rotation 4, scale 3; grown by Shells.
    double[] _shPose = Array.Empty<double>(), _shFrom = Array.Empty<double>(), _shSub = Array.Empty<double>();
    // Per shell and node: the nearest point last found (-1: none) and where the node was then, so a node that barely moved
    // (hair and cloth do, sub-step to sub-step) is not searched for again.
    int[] _shNear = Array.Empty<int>();
    double[] _shNearAt = Array.Empty<double>();
    // Per shell and node, measured once when the chain is bound: the nearest point then and how deep the node already was in the
    // surface (NaN: it was clear of it). Cloth rigs put their bones inside the body on purpose (a skirt's bones run down inside the
    // legs, the fabric is held out by the weights); the bound depth is what the node may keep, so binding never shoves the cloth out.
    int[] _shRestNear = Array.Empty<int>();
    double[] _shFloor = Array.Empty<double>();
    double[] _shPush = Array.Empty<double>(); // per node, what the shells have moved it this sub-step (a budget, not a sum)
    bool _shMeasured;
    // Fabric (ChainShape.Fabric): per fabric point its node and t with the offset in the anchor's frame; per node whether it carries
    // fabric on either side (then the surfaces push its fabric, not the node); per shell and fabric point the bound floor, the
    // point nearest it then, and the last point it was found clear of (which way is out when it is deep).
    readonly int _nf;
    readonly int[] _fabNode;
    readonly double[] _fab;
    readonly bool[] _clothed;
    readonly bool _hasFabric;
    // Per node, a bound on its fabric for a quick miss: every point lies within _fabSpan x the piece's length + _fabOff of the piece's
    // middle (-1: no fabric); _fabFar marks the pieces a shell is too far from this sub-step.
    readonly double[] _fabSpan, _fabOff;
    readonly bool[] _fabFar;
    int[] _fabRestNear = Array.Empty<int>(), _fabNear = Array.Empty<int>(), _fabHint = Array.Empty<int>();
    double[] _fabFloor = Array.Empty<double>();
    // The driven target (see Targets): per (driving shell, node) pair, measured once at the bind, the shell, the node, its weight and
    // the bound direction of the node's piece in the shell bone's frame (a unit direction: a rescale leaves it); per node the target
    // this sub-step (world) and its piece's blended direction.
    int[] _drvShell = Array.Empty<int>(), _drvNode = Array.Empty<int>();
    double[] _drvW = Array.Empty<double>(), _drvAt = Array.Empty<double>();
    readonly double[] _target, _targetDir;
    readonly double[] _cur = new double[7], _from = new double[7], _sub = new double[7], _last = new double[7], _t = new double[16];
    readonly double[] _body = new double[12]; // the body: at the frame's start, now, this sub-step, the last one
    double _time, _acc, _f;
    bool _started;

    public ChainState(ChainShape shape, ChainSpec spec)
    {
        _n = shape.Count;
        if (_n < 2 || shape.Parent[0] != -1) throw new ArgumentException("a chain needs a root first and a node below it");
        _step = spec.Step;
        _iterations = Math.Max(spec.Iterations, 1);
        _passes = Math.Max(spec.LengthPasses, 1);
        _bendShare = 1.0 / _iterations;
        double mass = spec.Weight, damping = spec.Damping, stiffness = spec.Stiffness;
        _parent = shape.Parent.ToArray();
        _len = shape.Length.ToArray();
        _dir = shape.Dir.ToArray();
        _rigid = shape.Rigid.ToArray();
        _side = shape.Side.ToArray();
        _fab = shape.Cloth.ToArray();
        _fabNode = shape.ClothNode.ToArray();
        _nf = _fabNode.Length;
        _hasFabric = _nf > 0;
        _clothed = new bool[_n];
        foreach (int i in _fabNode) _clothed[i] = _clothed[_parent[i]] = true;
        _fabSpan = new double[_n];
        _fabOff = new double[_n];
        _fabFar = new bool[_n];
        Array.Fill(_fabOff, -1);
        for (int k = 0; k < _nf; k++)
        {
            int i = _fabNode[k], f = k * 4;
            _fabSpan[i] = Math.Max(_fabSpan[i], Math.Abs(_fab[f] - 0.5));
            _fabOff[i] = Math.Max(_fabOff[i], Math.Sqrt(_fab[f + 1] * _fab[f + 1] + _fab[f + 2] * _fab[f + 2] + _fab[f + 3] * _fab[f + 3]));
        }
        _w = new double[_n];
        _bendK = new double[_n];
        _bendD = new double[_n];
        _radius = new double[_n];
        _restoreK = new double[_n];
        _straight = new bool[_n];
        _short = new bool[_n];
        _p = new double[_n * 3];
        _q = new double[_n * 3];
        _rm = new double[_n * 3];
        _shPush = new double[_n * 3];
        _target = new double[_n * 3];
        _targetDir = new double[_n * 3];
        // Reach: the longest way from a root down, for the swing's own pace and the style leash's allowance.
        _leash = Math.Max(0, spec.StyleLeash);
        var reachTo = _reachTo = new double[_n];
        double reach = 0;
        for (int i = 0; i < _n; i++)
        {
            int a = _parent[i];
            if (a >= i) throw new ArgumentException("a parent comes before its node");
            if (a < 0) continue;
            reachTo[i] = reachTo[a] + _len[i];
            reach = Math.Max(reach, reachTo[i]);
            // The bend's target is the piece above's direction itself when the node was bound straight on from it.
            _straight[i] = _parent[a] >= 0 && _dir[i * 3] == _dir[a * 3] && _dir[i * 3 + 1] == _dir[a * 3 + 1] && _dir[i * 3 + 2] == _dir[a * 3 + 2];
        }
        _reach = Math.Max(reach, 1e-6);
        _mass = mass;
        _rootOf = new int[_n];
        _strandReach = new double[_n];
        for (int i = 0; i < _n; i++) _rootOf[i] = _parent[i] < 0 ? i : _rootOf[_parent[i]];
        for (int i = 0; i < _n; i++) _strandReach[_rootOf[i]] = Math.Max(_strandReach[_rootOf[i]], reachTo[i]);
        // Which nodes belong to a short strand (a fringe against a long wave): the telemetry reports their deviation on its own.
        {
            double longest = 0;
            for (int i = 0; i < _n; i++) longest = Math.Max(longest, _strandReach[_rootOf[i]]);
            if (longest > 0)
                for (int i = 0; i < _n; i++) _short[i] = _strandReach[_rootOf[i]] < 0.35 * longest;
        }
        _gravity = G * spec.Gravity * _step * _step;
        // Drag on a node's move against the anchor: a share of critical damping for the chain's own swing (sqrt(g / reach)), plus a
        // part growing with that speed (SwingDrag per reach), so a large swing loses more; both less on a heavier chain. The
        // natural rate uses the worn scale, so a 1.5x model keeps the editor's motion; the author's gravity multiplier stays out of
        // the damping, as it always has.
        _zeta = Math.Min(DampSlow * Math.Pow(DampFast / DampSlow, damping) / mass, 1);
        _keep = Math.Exp(-2 * _zeta * Math.Sqrt(G * _scale / _reach) * _step);
        _swingDrag = SwingDrag / (_reach * mass);
        // Bending: a spring of BendRate x stiffness² rad/s, damped toward critical as the stiffness nears 1.
        double bend = BendRate * stiffness * stiffness * _step;
        _bend = Math.Min(bend * bend / mass, BendMax);
        _bendDamp = Math.Min(2 * bend * stiffness * stiffness / mass, BendDampMax);
        double restore = RestoreRate * spec.Restore * _step;
        _restore = Math.Min(restore * restore / mass, RestoreMax);
        double limit = spec.AngleLimit > 0 && spec.AngleLimit < 180 ? spec.AngleLimit * Math.PI / 180 : 0;
        _bendLimit = limit;
        _cosLimit = limit > 0 ? Math.Cos(limit) : -2;
        _sinLimit = Math.Sin(limit);
        _hasLimit = limit > 0;
        _cosLimitK = new double[_n];
        _sinLimitK = new double[_n];
        _azimuth = spec.AzimuthLimit > 0 && spec.AzimuthLimit < 180 ? spec.AzimuthLimit * Math.PI / 180 : 0;
        _carry = 1 - spec.Inertia;
        _collide = spec.Collide;
        _collideCloth = spec.CollideCloth;
        _self = spec.SelfCollide;
        _margin = spec.Radius * 2;
        ApplyStyle();
        for (int i = 0; i < _n; i++)
        {
            if (_parent[i] < 0) continue;
            _w[i] = 1 / shape.Mass[i];
            _bendK[i] = _bend * shape.Bend[i];
            _bendD[i] = _bendDamp * shape.Bend[i];
            _radius[i] = shape.Radius[i] < 0 ? spec.Radius : shape.Radius[i];
        }
        // Side links at their rest distance; every node's bound place, kept for the azimuth limit's radial plane.
        _sideLen = new double[_side.Length / 2];
        _restPos = new double[_n * 3];
        for (int i = 0; i < _n; i++)
        {
            int o = i * 3, a = _parent[i] * 3;
            for (int c = 0; c < 3; c++) _restPos[o + c] = a < 0 ? _dir[o + c] : _restPos[a + c] + _dir[o + c] * _len[i];
        }
        for (int k = 0; k < _sideLen.Length; k++)
        {
            int a = _side[k * 2] * 3, b = _side[k * 2 + 1] * 3;
            double x = _restPos[b] - _restPos[a], y = _restPos[b + 1] - _restPos[a + 1], z = _restPos[b + 2] - _restPos[a + 2];
            _sideLen[k] = Math.Sqrt(x * x + y * y + z * z);
        }
        // The pieces the cloth collision tests at their middles: down each strand, and the links across strands.
        if (_collideCloth)
        {
            var segA = new List<int>(_n + _side.Length / 2);
            var segB = new List<int>(_n + _side.Length / 2);
            for (int i = 0; i < _n; i++) if (_parent[i] >= 0) { segA.Add(_parent[i]); segB.Add(i); }
            for (int k = 0; k < _side.Length / 2; k++) { segA.Add(_side[k * 2]); segB.Add(_side[k * 2 + 1]); }
            _segA = segA.ToArray();
            _segB = segB.ToArray();
        }
        // Nodes of neighbouring strands, kept apart where the bind has them close.
        if (_self) BuildSelfPairs();
    }

    /// <summary>Debug telemetry: the largest surface push a node took in the last sub-step, world metres.</summary>
    public double DebugPushMax { get; private set; }
    /// <summary>Debug telemetry: the mean and smallest depth the nodes were bound at inside the surface shells (negative inside),
    /// and how many were bound outside. A first wear and a re-wear must show the same numbers; a difference says which one is wrong.</summary>
    public double DebugBoundDepthMean { get; private set; }
    public double DebugBoundDepthMin { get; private set; }
    public int DebugBoundOutside { get; private set; }
    public int DebugBoundInside { get; private set; }
    /// <summary>Debug telemetry: cross-strand node pairs still closer than their floor by more than 5 mm at the last pass.</summary>
    public int DebugTangled { get; private set; }
    /// <summary>Debug telemetry: how far the furthest node is from the bound style carried by the anchor, and the same for the
    /// short strands only (the fringe, which is what reads as floating hair). World metres.</summary>
    public double DebugDev { get; private set; }
    public double DebugShortDev { get; private set; }
    /// <summary>Debug telemetry: how many times the chain has been laid back on its style because the anchor jumped (a respawn, a
    /// dance's warp) — a chain reset over and over is a chain that never keeps its swing.</summary>
    public int DebugRestJumps { get; private set; }
    /// <summary>Whether a node belongs to a short strand (its whole strand is under a third of the set's longest).</summary>
    readonly bool[] _short;

    /// <summary>Refits the chain to a model worn at another size: every length, radius, side link and the bound shape scale by
    /// `factor`. The caller scales the nodes themselves about the anchor (<see cref="ScaleAbout"/>), so the motion is kept — a chain
    /// reset for a size change pops the hair. A chain left at the old size is the hair that floats off the head.</summary>
    public void Rescale(double factor)
    {
        if (!(factor > 1e-3) || !double.IsFinite(factor) || Math.Abs(factor - 1) < 1e-4) return;
        for (int i = 0; i < _n; i++) _len[i] *= factor;
        for (int i = 0; i < _restPos.Length; i++) _restPos[i] *= factor;
        for (int i = 0; i < _radius.Length; i++) _radius[i] *= factor;
        for (int i = 0; i < _sideLen.Length; i++) _sideLen[i] *= factor;
        for (int i = 0; i < _reachTo.Length; i++) _reachTo[i] *= factor;
        for (int i = 0; i < _strandReach.Length; i++) _strandReach[i] *= factor;
        _reach *= factor;
        _margin *= factor;
        // Gravity was scaled by the size the chain was bound at; the new size scales it again, keeping the same dimensionless
        // swing. The rates derived from it and from reach are recomputed by ApplyScale.
        _gravity *= factor;
        // The shell floors are world depths measured at the old size: they scale with everything else, or the cloth is held at the
        // old clearance. (The measured nearest points are in the shell bones' own space and do not.)
        for (int i = 0; i < _shFloor.Length; i++)
            if (!double.IsNaN(_shFloor[i])) _shFloor[i] *= factor;
        for (int i = 0; i < _fabFloor.Length; i++)
            if (!double.IsNaN(_fabFloor[i])) _fabFloor[i] *= factor;
        for (int k = 0; k < _nf; k++)
            for (int c = 1; c < 4; c++) _fab[k * 4 + c] *= factor;
        for (int i = 0; i < _n; i++)
            if (_fabOff[i] > 0) _fabOff[i] *= factor;
        _scale *= factor;
        _appliedScale = double.NaN;
        if (_self) BuildSelfPairs();
    }

    /// <summary>Scales every node's place, and its previous place, about a point (the anchor): the chain follows a model whose
    /// drawn size changed without being reset, so it keeps swinging where it was.</summary>
    public void ScaleAbout(double x, double y, double z, double factor)
    {
        if (!(factor > 0) || !double.IsFinite(factor) || Math.Abs(factor - 1) < 1e-6) return;
        for (int i = 0; i < _n; i++)
        {
            int o = i * 3;
            _p[o] = x + (_p[o] - x) * factor;
            _p[o + 1] = y + (_p[o + 1] - y) * factor;
            _p[o + 2] = z + (_p[o + 2] - z) * factor;
            _q[o] = x + (_q[o] - x) * factor;
            _q[o + 1] = y + (_q[o + 1] - y) * factor;
            _q[o + 2] = z + (_q[o + 2] - z) * factor;
        }
    }

    /// <summary>World metres per authored metre, set before the first <see cref="Rest"/> or <see cref="Update"/>: the character
    /// wearing the model is scaled, and every collision distance must scale with it or the cloth is thinner the bigger it is worn.</summary>
    public double Scale
    {
        get => _scale;
        set { if (value > 1e-6 && double.IsFinite(value)) _scale = value; }
    }

    // Every distance that is authored in metres and compared against world positions, multiplied by the worn scale. The radii feed
    // capsule, box and surface collisions and the self-collision floors; the rest distances of the chain are world already (the bones
    // were bound where they stand), so only the radius part of a floor scales. Rebuilt when Scale changes, measured at first use.
    void ApplyScale()
    {
        if (_appliedScale == _scale) return;
        _appliedScale = _scale;
        _standoff = 0.004 * _scale;
        // The fabric points are a few samples of a panel: the cloth between them sags past them, so they keep a centimetre.
        _fabGap = 0.01 * _scale;
        _dead = 0.003 * _scale;
        // Cloth's correction per sub-step is kept a per-frame rate across sub-step sizes: a finer sub-step must not multiply how fast
        // the surface can move the cloth, or the cloth over-corrects and rings.
        double rate = _collideCloth ? _step * 120 : 1;
        _budget = 0.02 * rate * _scale;
        _segBudget = 0.016 * rate * _scale;
        // The fabric's push is a light backstop: the driven target carries the cloth with the legs, and a heavier push only feeds
        // the contact a buzz.
        _fabBudget = 0.008 * rate * _scale;
        _softCap = 0.02 * rate * _scale;
        _bandFloor = 0.03 * _scale;
        _floorSlack = 0.008 * _scale;
        // The swing's pace and drag come from reach and the worn scale; both are world values, so the dimensionless motion stays
        // the editor's at any worn size. Recompute them after a Rescale too.
        _swingDrag = SwingDrag / (_reach * _mass);
        _keep = Math.Exp(-2 * _zeta * Math.Sqrt(G * _scale / _reach) * _step);
        ApplyStyle();
        if (_self) BuildSelfPairs();
    }

    // The length-shaped style values: a short strand's restore and bend limit are chosen from its reach in metres, so at another
    // worn size the thresholds must grow with it or a 1.5x model reads every strand as shorter than it is.
    void ApplyStyle()
    {
        for (int i = 0; i < _n; i++)
        {
            double hold = _collideCloth ? 1 : Math.Clamp(0.2 * _scale / Math.Max(_reachTo[i], 0.05 * _scale), 0.5, 8);
            _restoreK[i] = _restore * hold;
        }
        if (_hasLimit)
            for (int i = 0; i < _n; i++)
            {
                double share = Math.Clamp(_strandReach[_rootOf[i]] / (0.35 * _scale), 0.35, 1);
                double l = _bendLimit * share;
                _cosLimitK[i] = Math.Cos(l);
                _sinLimitK[i] = Math.Sin(l);
            }
    }

    // Nodes of neighbouring strands, kept apart where the bind has them close: within a hand's width (at the worn scale) are the
    // pairs that can meet. Each pair also keeps the side it was bound on, so a node cannot braid through its neighbour.
    void BuildSelfPairs()
    {
        var strand = new int[_n];
        for (int i = 0; i < _n; i++) strand[i] = _parent[i] < 0 ? i : strand[_parent[i]];
        var pa = new List<int>();
        var pb = new List<int>();
        var pf = new List<double>();
        var pr = new List<double>();
        var pd = new List<double>();
        double range = 0.15 * _scale;
        for (int i = 0; i < _n; i++)
        {
            if (_parent[i] < 0) continue;
            int io = i * 3;
            for (int j = i + 1; j < _n; j++)
            {
                if (_parent[j] < 0 || strand[j] == strand[i]) continue;
                int jo = j * 3;
                double dx = _restPos[jo] - _restPos[io], dy = _restPos[jo + 1] - _restPos[io + 1], dz = _restPos[jo + 2] - _restPos[io + 2];
                double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (d >= range || d < 1e-9) continue;
                double floor = Math.Min(d - 0.002 * _scale, _radius[i] + _radius[j]);
                if (floor <= 0.001 * _scale) continue;
                pa.Add(i); pb.Add(j); pf.Add(floor); pr.Add(d);
                pd.Add(dx / d); pd.Add(dy / d); pd.Add(dz / d);
            }
        }
        _selfA = pa.ToArray();
        _selfB = pb.ToArray();
        _selfFloor = pf.ToArray();
        _selfRest = pr.ToArray();
        _selfDir = pd.ToArray();
        _selfDirSub = new double[pd.Count];
    }

    /// <summary>World positions of the nodes, three per node, in the shape's order.</summary>
    public double[] Nodes => _p;
    public int Count => _n;
    public int Boxes => _boxes;
    /// <summary>The anchor's pose (position xyz, quaternion xyzw) at the last update.</summary>
    public double[] Anchor => _cur;

    // ---- colliders ----

    /// <summary>A box of a rigid body that moves with the anchor: min and max in the body's frame `body`, taken into the anchor's frame
    /// (`anchor`, both at the same moment). A box that holds the anchor is cut back to it on its nearest face, so the chain starts
    /// outside; one cut to nothing is left out.</summary>
    public void AddBox(double[] anchor, double[] body, double[] min, double[] max)
    {
        ApplyScale();
        int o = _boxes * 15;
        if (_box.Length < o + 15) { Array.Resize(ref _box, o + 15); Array.Resize(ref _world, o + 15); }
        Pose(anchor, _sub);
        double cx = (min[0] + max[0]) / 2, cy = (min[1] + max[1]) / 2, cz = (min[2] + max[2]) / 2;
        // Centre: body -> world -> anchor's frame (rotation inverse, no scale).
        double wx = body[0] * cx + body[4] * cy + body[8] * cz + body[12] - _sub[0];
        double wy = body[1] * cx + body[5] * cy + body[9] * cz + body[13] - _sub[1];
        double wz = body[2] * cx + body[6] * cy + body[10] * cz + body[14] - _sub[2];
        Rotate(-_sub[3], -_sub[4], -_sub[5], _sub[6], wx, wy, wz, _box, o);
        for (int j = 0; j < 3; j++)
        {
            double ax = body[j * 4], ay = body[j * 4 + 1], az = body[j * 4 + 2];
            double len = Math.Sqrt(ax * ax + ay * ay + az * az);
            _box[o + 12 + j] = (max[j] - min[j]) / 2 * len;
            if (len > 0) { ax /= len; ay /= len; az /= len; }
            Rotate(-_sub[3], -_sub[4], -_sub[5], _sub[6], ax, ay, az, _box, o + 3 + j * 3);
        }
        // The anchor (the frame's origin) in box coordinates.
        int nearest = -1;
        double nearestGap = double.MaxValue;
        for (int j = 0; j < 3; j++)
        {
            double l = -(_box[o] * _box[o + 3 + j * 3] + _box[o + 1] * _box[o + 4 + j * 3] + _box[o + 2] * _box[o + 5 + j * 3]);
            double gap = _box[o + 12 + j] - Math.Abs(l);
            if (gap <= 0) { _boxes++; return; } // outside
            if (gap < nearestGap) { nearestGap = gap; nearest = j; }
        }
        {
            int j = nearest;
            double l = -(_box[o] * _box[o + 3 + j * 3] + _box[o + 1] * _box[o + 4 + j * 3] + _box[o + 2] * _box[o + 5 + j * 3]);
            double h = _box[o + 12 + j];
            // Keep the side away from the anchor: [-h, l - margin] when it is on the + side, [l + margin, h] else.
            double lo = l >= 0 ? -h : l + _margin, hi = l >= 0 ? l - _margin : h;
            if (hi <= lo) return;
            double shift = (lo + hi) / 2;
            _box[o] += _box[o + 3 + j * 3] * shift;
            _box[o + 1] += _box[o + 4 + j * 3] * shift;
            _box[o + 2] += _box[o + 5 + j * 3] * shift;
            _box[o + 12 + j] = (hi - lo) / 2;
        }
        _boxes++;
    }

    /// <summary>How many capsules the chain is kept out of; set each with <see cref="SetCapsule"/> before every update. Setting a larger
    /// number than before grows the arrays once.</summary>
    public int Capsules
    {
        get => _capsules;
        set
        {
            _capsules = Math.Max(value, 0);
            if (_cap.Length >= _capsules * 7) return;
            Array.Resize(ref _cap, _capsules * 7);
            Array.Resize(ref _capFrom, _capsules * 7);
            Array.Resize(ref _capSub, _capsules * 7);
        }
    }

    /// <summary>Capsule `i` now, in the world: the segment from a to b and its radius (a sphere when a is b). Each sub-step takes it
    /// where it was at that sub-step's time, from the last update's place to this one.</summary>
    public void SetCapsule(int i, double ax, double ay, double az, double bx, double by, double bz, double radius)
    {
        int o = i * 7;
        _cap[o] = ax; _cap[o + 1] = ay; _cap[o + 2] = az; _cap[o + 3] = bx; _cap[o + 4] = by; _cap[o + 5] = bz; _cap[o + 6] = radius;
    }

    /// <summary>How many surface shells (a body joint's skin as points with outward normals) the chain is kept out of; each set with
    /// <see cref="SetShellData"/> once and <see cref="SetShellPose"/> before every update. Setting a larger number than before grows
    /// the arrays once.</summary>
    public int Shells
    {
        get => _shells;
        set
        {
            int was = _shells;
            _shells = Math.Max(value, 0);
            if (_shPose.Length >= _shells * 10 && _shells <= was) return;
            Array.Resize(ref _shPose, _shells * 10);
            Array.Resize(ref _shFrom, _shells * 10);
            Array.Resize(ref _shSub, _shells * 10);
            Array.Resize(ref _shellData, _shells);
            for (int i = was; i < _shells; i++) { _shPose[i * 10 + 6] = 1; _shFrom[i * 10 + 6] = 1; }
            int cells = _shells * _n;
            if (_shNear.Length < cells)
            {
                int wasCells = _shNear.Length;
                Array.Resize(ref _shNear, cells);
                Array.Resize(ref _shNearAt, cells * 3);
                Array.Resize(ref _shRestNear, cells);
                Array.Resize(ref _shFloor, cells);
                for (int i = wasCells; i < cells; i++) { _shNear[i] = -1; _shRestNear[i] = -1; _shFloor[i] = double.NaN; }
                for (int i = wasCells * 3; i < cells * 3; i++) _shNearAt[i] = double.NaN;
            }
            int fabCells = _shells * _nf;
            if (_fabNear.Length < fabCells)
            {
                int wasCells = _fabNear.Length;
                Array.Resize(ref _fabRestNear, fabCells);
                Array.Resize(ref _fabNear, fabCells);
                Array.Resize(ref _fabFloor, fabCells);
                Array.Resize(ref _fabHint, fabCells);
                for (int i = wasCells; i < fabCells; i++) { _fabRestNear[i] = _fabNear[i] = _fabHint[i] = -1; _fabFloor[i] = double.NaN; }
            }
        }
    }

    public void SetShellData(int i, ChainShellData data) { if (i >= 0 && i < _shells) _shellData[i] = data; }

    /// <summary>Measures every node's bound depth against the shells as they are posed now (the bind pose), once; a caller that
    /// binds at rest calls this right after setting the poses, so the first animated frame is not taken for the bind.</summary>
    public void BindShells() { if (_shells > 0) MeasureShells(); }

    /// <summary>Shell `i`'s bone now: the world matrix of the bone the shell's points are held in. Each sub-step takes the shell
    /// where it was at that sub-step's time, from the last update's place to this one.</summary>
    public void SetShellPose(int i, double[] m)
    {
        int o = i * 10;
        double sx = Math.Sqrt(m[0] * m[0] + m[1] * m[1] + m[2] * m[2]);
        double sy = Math.Sqrt(m[4] * m[4] + m[5] * m[5] + m[6] * m[6]);
        double sz = Math.Sqrt(m[8] * m[8] + m[9] * m[9] + m[10] * m[10]);
        if (!(sx > 1e-9 && sy > 1e-9 && sz > 1e-9))
        {
            // A gone (or degenerate) bone: scale 0, which keeps the shell out of the solve.
            _shPose[o] = _shPose[o + 1] = _shPose[o + 2] = 0;
            _shPose[o + 3] = _shPose[o + 4] = _shPose[o + 5] = 0; _shPose[o + 6] = 1;
            _shPose[o + 7] = _shPose[o + 8] = _shPose[o + 9] = 0;
            return;
        }
        Pose(m, _shPose, o);
        _shPose[o + 7] = sx; _shPose[o + 8] = sy; _shPose[o + 9] = sz;
    }

    // ---- simulation ----

    /// <summary>At rest: the bound shape carried by the anchor.</summary>
    public void Rest(double[] anchor)
    {
        ApplyScale();
        Pose(anchor, _cur);
        for (int i = 0; i < _n; i++)
        {
            int o = i * 3, a = _parent[i] * 3;
            if (a < 0)
            {
                Rotate(_cur[3], _cur[4], _cur[5], _cur[6], _dir[o], _dir[o + 1], _dir[o + 2], _t, 0);
                _p[o] = _q[o] = _cur[0] + _t[0];
                _p[o + 1] = _q[o + 1] = _cur[1] + _t[1];
                _p[o + 2] = _q[o + 2] = _cur[2] + _t[2];
                continue;
            }
            Rotate(_cur[3], _cur[4], _cur[5], _cur[6], _dir[o], _dir[o + 1], _dir[o + 2], _t, 0);
            _p[o] = _q[o] = _p[a] + _t[0] * _len[i];
            _p[o + 1] = _q[o + 1] = _p[a + 1] + _t[1] * _len[i];
            _p[o + 2] = _q[o + 2] = _p[a + 2] + _t[2] * _len[i];
        }
        Array.Copy(_cur, _from, 7);
        Array.Copy(_cur, _last, 7);
        Array.Copy(_cap, _capFrom, _capsules * 7);
        Array.Copy(_shPose, _shFrom, _shells * 10);
        if (_shells > 0 && !_shMeasured) MeasureShells();
        Array.Fill(_body, double.NaN); // not known until the next update
        _acc = 0;
        _started = true;
    }

    /// <summary>Advances to `time` (seconds) with the anchor at `anchor` and the body (a charm's player camera, a character's root) at
    /// `body` (xyz). Allocates nothing.</summary>
    public void Update(double time, double[] anchor, double[] body)
    {
        ApplyScale();
        double[] b = _body;
        Pose(anchor, _cur);
        double dt = time - _time;
        double jx = _cur[0] - _from[0], jy = _cur[1] - _from[1], jz = _cur[2] - _from[2];
        double bx = body[0] - b[0], by = body[1] - b[1], bz = body[2] - b[2];
        // A jump is a world distance: the same pose change on a model worn 1.5x larger is 1.5x the metres, not a teleport.
        // Only a real teleport resets; a dance's fast step or a spin is followed rigidly. Resetting cloth into its bind shape
        // (whose bones sit inside the body) is exactly the frame the cloth appears to pass through the model.
        double jump = Jump * 4 * _scale;
        if (!_started || dt > Gap || dt < 0 || !(jx * jx + jy * jy + jz * jz <= jump * jump) || !(bx * bx + by * by + bz * bz <= jump * jump))
        {
            if (_started) DebugRestJumps++;
            _time = time;
            Rest(anchor);
            for (int i = 0; i < 12; i++) b[i] = body[i % 3];
            return;
        }
        b[3] = body[0]; b[4] = body[1]; b[5] = body[2];
        _time = time;
        _acc += dt;
        // A frame time a rounding error short of a whole number of sub-steps (1/60 s is 4 x 1/240 s, but 3.9999999 in doubles) runs
        // them all: flooring it left a sub-step over every few frames, so frames ran 3, 5, 4 sub-steps by turns, and the leftover
        // carried the chain rigidly and pushed it out of the surfaces once more outside the solve.
        int steps = (int)Math.Floor(_acc / _step + 1e-6);
        _acc = Math.Max(0, _acc - steps * _step);
        // Each sub-step takes the anchor where it was at that sub-step's own time in this frame (the clock runs _acc behind), so it
        // moves at its real speed whatever the frame times. Past MaxSteps, the frame's start is only followed.
        int skip = Math.Max(0, steps - MaxSteps);
        for (int k = Math.Max(skip, 1); k <= steps; k++)
        {
            double f = 1 - (_acc + (steps - k) * _step) / dt;
            _f = f;
            _sub[0] = _from[0] + (_cur[0] - _from[0]) * f;
            _sub[1] = _from[1] + (_cur[1] - _from[1]) * f;
            _sub[2] = _from[2] + (_cur[2] - _from[2]) * f;
            Slerp(_from, _cur, f, _sub);
            for (int i = 0; i < 3; i++) b[6 + i] = b[i] + (b[3 + i] - b[i]) * f;
            if (k == skip) Follow();
            else Substep();
            Array.Copy(_sub, _last, 7);
            b[9] = b[6]; b[10] = b[7]; b[11] = b[8];
        }
        // A leftover shorter than a sub-step: the chain is still at the last sub-step's time while the body is drawn at this frame's.
        // Carry it the rest of the way rigidly with the anchor and answer the body at the frame's own pose, or the cloth is drawn a
        // sub-step behind the leg that moved into it (very visible at a high frame rate with a fine sub-step).
        if ((steps == 0 || _acc > 1e-9) && _collide && _collideCloth)
        {
            _f = 1;
            Array.Copy(_cur, _sub, 7);
            for (int i = 0; i < _shells * 10; i++) _shSub[i] = _shPose[i];
            for (int i = 0; i < _capsules * 7; i++) _capSub[i] = _cap[i];
            b[6] = b[3]; b[7] = b[4]; b[8] = b[5];
            Follow();
            Array.Clear(_shPush, 0, _n * 3);
            for (int c = 0; c < _capsules; c++) PushOutOfCapsule(c * 7);
            for (int s = 0; s < _shells; s++) CollideShell(s * 10);
            if (_hasFabric) SweepLengths(0); // fabric pushes swing the drawn bones, they do not stretch them
            Inelastic();
            Array.Copy(_cur, _last, 7);
        }
        // Debug telemetry: how far the nodes are from their driven target at the last sub-step (what a floating fringe is).
        double dev = 0, shortDev = 0;
        for (int i = 0; i < _n; i++)
        {
            int o = i * 3;
            double dx = _p[o] - _target[o], dy = _p[o + 1] - _target[o + 1], dz = _p[o + 2] - _target[o + 2];
            double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (d > dev) dev = d;
            if (_short[i] && d > shortDev) shortDev = d;
        }
        DebugDev = dev;
        DebugShortDev = shortDev;
        Array.Copy(_cur, _from, 7);
        Array.Copy(_cap, _capFrom, _capsules * 7);
        Array.Copy(_shPose, _shFrom, _shells * 10);
        b[0] = b[3]; b[1] = b[4]; b[2] = b[5];
    }

    // A root's place at the sub-step's anchor pose into _t[0..2].
    void RootAt(int o)
    {
        Rotate(_sub[3], _sub[4], _sub[5], _sub[6], _dir[o], _dir[o + 1], _dir[o + 2], _t, 0);
        _t[0] += _sub[0]; _t[1] += _sub[1]; _t[2] += _sub[2];
    }

    // Moves every node rigidly with the anchor's move from _last to _sub: rotation _sub * inverse(_last) about the anchor, then its move.
    void Follow()
    {
        double ax = _last[3], ay = _last[4], az = _last[5], aw = _last[6];
        double bx = _sub[3], by = _sub[4], bz = _sub[5], bw = _sub[6];
        double dx = bw * -ax + bx * aw + by * -az - bz * -ay;
        double dy = bw * -ay - bx * -az + by * aw + bz * -ax;
        double dz = bw * -az + bx * -ay - by * -ax + bz * aw;
        double dw = bw * aw - bx * -ax - by * -ay - bz * -az;
        for (int i = 0; i < _n; i++)
        {
            int o = i * 3;
            if (_parent[i] < 0)
            {
                RootAt(o);
                _p[o] = _q[o] = _t[0]; _p[o + 1] = _q[o + 1] = _t[1]; _p[o + 2] = _q[o + 2] = _t[2];
                continue;
            }
            Carried(_p, o, dx, dy, dz, dw);
            Carried(_q, o, dx, dy, dz, dw);
        }
    }

    // One sub-step from the anchor's pose _last to _sub.
    void Substep()
    {
        // The chain rides along with (1 - Inertia) of the body's move, the excess over BodyLimit whole.
        double[] bd = _body;
        double ex0 = bd[6] - bd[9], ey0 = bd[7] - bd[10], ez0 = bd[8] - bd[11];
        double carry = _carry, moved = Math.Sqrt(ex0 * ex0 + ey0 * ey0 + ez0 * ez0);
        double bodyLimit = BodyLimit * _scale, speedLimit = MaxSpeed * _scale;
        if (moved > bodyLimit * _step) carry += (1 - carry) * (1 - bodyLimit * _step / moved);
        double cx = ex0 * carry, cy = ey0 * carry, cz = ez0 * carry;
        // The anchor's move this sub-step in that riding frame. Verlet there: a node's move against the anchor's loses drag (the
        // chain's own friction, so walking at a steady speed drags nothing) and is capped at MaxSpeed.
        double mx = _sub[0] - _last[0] - cx, my = _sub[1] - _last[1] - cy, mz = _sub[2] - _last[2] - cz;
        double cap = speedLimit * _step;
        for (int i = 0; i < _n; i++)
        {
            int o = i * 3;
            if (_parent[i] < 0)
            {
                // Pinned: its own move this sub-step (in the riding frame) is what the first pieces below it bend against.
                RootAt(o);
                _rm[o] = _t[0] - _p[o] - cx; _rm[o + 1] = _t[1] - _p[o + 1] - cy; _rm[o + 2] = _t[2] - _p[o + 2] - cz;
                _p[o] = _q[o] = _t[0]; _p[o + 1] = _q[o + 1] = _t[1]; _p[o + 2] = _q[o + 2] = _t[2];
                continue;
            }
            double rx = _p[o] - _q[o] - mx, ry = _p[o + 1] - _q[o + 1] - my, rz = _p[o + 2] - _q[o + 2] - mz;
            double r = Math.Sqrt(rx * rx + ry * ry + rz * rz);
            double keep = _keep * Math.Exp(-_swingDrag * r);
            rx *= keep; ry *= keep; rz *= keep; r *= keep;
            if (r > cap)
            {
                double k = cap / r;
                rx *= k; ry *= k; rz *= k;
            }
            _p[o] += cx; _p[o + 1] += cy; _p[o + 2] += cz;
            _q[o] = _p[o]; _q[o + 1] = _p[o + 1]; _q[o + 2] = _p[o + 2];
            _p[o] += mx + rx;
            _p[o + 1] += my + ry - _gravity;
            _p[o + 2] += mz + rz;
        }
        for (int b = 0; b < _boxes; b++)
        {
            int o = b * 15;
            Rotate(_sub[3], _sub[4], _sub[5], _sub[6], _box[o], _box[o + 1], _box[o + 2], _world, o);
            _world[o] += _sub[0]; _world[o + 1] += _sub[1]; _world[o + 2] += _sub[2];
            for (int j = 0; j < 3; j++)
                Rotate(_sub[3], _sub[4], _sub[5], _sub[6], _box[o + 3 + j * 3], _box[o + 4 + j * 3], _box[o + 5 + j * 3], _world, o + 3 + j * 3);
            _world[o + 12] = _box[o + 12]; _world[o + 13] = _box[o + 13]; _world[o + 14] = _box[o + 14];
        }
        for (int c = 0; c < _capsules * 7; c++) _capSub[c] = _capFrom[c] + (_cap[c] - _capFrom[c]) * _f;
        for (int s = 0; s < _shells; s++)
        {
            int o = s * 10;
            double f = _f;
            _shSub[o] = _shFrom[o] + (_shPose[o] - _shFrom[o]) * f;
            _shSub[o + 1] = _shFrom[o + 1] + (_shPose[o + 1] - _shFrom[o + 1]) * f;
            _shSub[o + 2] = _shFrom[o + 2] + (_shPose[o + 2] - _shFrom[o + 2]) * f;
            double ax = _shFrom[o + 3], ay = _shFrom[o + 4], az = _shFrom[o + 5], aw = _shFrom[o + 6];
            double bx = _shPose[o + 3], by = _shPose[o + 4], bz = _shPose[o + 5], bw = _shPose[o + 6];
            double d = ax * bx + ay * by + az * bz + aw * bw;
            if (d < 0) { d = -d; bx = -bx; by = -by; bz = -bz; bw = -bw; }
            double ka, kb;
            if (d > 0.9995) { ka = 1 - f; kb = f; }
            else
            {
                double th = Math.Acos(d), sn = Math.Sin(th);
                ka = Math.Sin((1 - f) * th) / sn; kb = Math.Sin(f * th) / sn;
            }
            double qx = ax * ka + bx * kb, qy = ay * ka + by * kb, qz = az * ka + bz * kb, qw = aw * ka + bw * kb;
            double ql = Math.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
            if (ql < 1e-9) { qx = qy = qz = 0; qw = ql = 1; }
            _shSub[o + 3] = qx / ql; _shSub[o + 4] = qy / ql; _shSub[o + 5] = qz / ql; _shSub[o + 6] = qw / ql;
            _shSub[o + 7] = _shFrom[o + 7] + (_shPose[o + 7] - _shFrom[o + 7]) * f;
            _shSub[o + 8] = _shFrom[o + 8] + (_shPose[o + 8] - _shFrom[o + 8]) * f;
            _shSub[o + 9] = _shFrom[o + 9] + (_shPose[o + 9] - _shFrom[o + 9]) * f;
        }
        Targets();
        // The bound side directions turned with the anchor, for the anti-swap constraint.
        if (_self && _selfDir.Length > 0)
            for (int k = 0; k < _selfDir.Length / 3; k++)
                Rotate(_sub[3], _sub[4], _sub[5], _sub[6], _selfDir[k * 3], _selfDir[k * 3 + 1], _selfDir[k * 3 + 2], _selfDirSub, k * 3);
        Array.Clear(_shPush, 0, _n * 3);
        for (int it = 0; it < _iterations; it++)
        {
            // The style springs inside the loop, not once before it: the self and cloth constraints would otherwise undo the style
            // between the springs' one pull and the frame that renders, which is the hair that loses its shape at speed.
            if (_bend > 0 || _restore > 0 || _cosLimit > -2) Bend(mx, my, mz);
            for (int pass = 0; pass < _passes; pass++)
                for (int i = 0; i < _n; i++)
                {
                    if (_parent[i] < 0) continue;
                    Hold(_parent[i] * 3, i * 3, _len[i], _w[_parent[i]], _w[i], 1);
                }
            for (int k = 0; k < _sideLen.Length; k++)
                HoldSide(_side[k * 2] * 3, _side[k * 2 + 1] * 3, _sideLen[k]);
            if (_self) PushSelvesApart();
            if (_collide)
            {
                for (int b = 0; b < _boxes; b++) PushOutOfBox(b * 15);
                for (int c = 0; c < _capsules; c++) PushOutOfCapsule(c * 7);
                for (int s = 0; s < _shells; s++) CollideShell(s * 10);
            }
        }
        // The body's capsules push first, then the length sweep has the last word, down from the roots: every piece exactly its
        // length. (The other order left pieces stretched for the frame that renders them, which reads as torn cloth.)
        if (_collide)
        {
            for (int c = 0; c < _capsules; c++) PushOutOfCapsule(c * 7);
            for (int s = 0; s < _shells; s++) CollideShell(s * 10);
        }
        if (_self) PushSelvesApart();
        // Debug telemetry: the largest surface push any node took this sub-step.
        {
            double pushMax = 0;
            for (int i = 0; i < _n * 3; i++) pushMax = Math.Max(pushMax, Math.Abs(_shPush[i]));
            DebugPushMax = pushMax;
        }
        if (_collideCloth) HoldLengths(3);
        else
            for (int i = 0; i < _n; i++)
            {
                if (_parent[i] < 0) continue;
                int o = i * 3, a = _parent[i] * 3;
                double rest = _len[i];
                double ex = _p[o] - _p[a], ey = _p[o + 1] - _p[a + 1], ez = _p[o + 2] - _p[a + 2];
                double len = Math.Sqrt(ex * ex + ey * ey + ez * ez);
                if (len < 1e-9) continue;
                double k = rest / len;
                _p[o] = _p[a] + ex * k;
                _p[o + 1] = _p[a + 1] + ey * k;
                _p[o + 2] = _p[a + 2] + ez * k;
            }
        // The cloth between the bones whole in the frame that renders: the length sweep and the side links alternate once more,
        // so a panel is neither torn (links stretched) nor crumpled (pieces short) when it is drawn. The azimuth and angle limits
        // below keep every piece's length, so the pieces are still exact at the end.
        for (int pass = 0; pass < 2 && _sideLen.Length > 0; pass++)
        {
            for (int k = 0; k < _sideLen.Length; k++)
                HoldSide(_side[k * 2] * 3, _side[k * 2 + 1] * 3, _sideLen[k]);
            if (_collideCloth) HoldLengths(2);
            else
                for (int i = 0; i < _n; i++)
                {
                    if (_parent[i] < 0) continue;
                    int o = i * 3, a = _parent[i] * 3;
                    double rest = _len[i];
                    double ex = _p[o] - _p[a], ey = _p[o + 1] - _p[a + 1], ez = _p[o + 2] - _p[a + 2];
                    double len = Math.Sqrt(ex * ex + ey * ey + ez * ez);
                    if (len < 1e-9) continue;
                    double k = rest / len;
                    _p[o] = _p[a] + ex * k;
                    _p[o + 1] = _p[a + 1] + ey * k;
                    _p[o + 2] = _p[a + 2] + ez * k;
                }
        }
        // The style leash, once the lengths are exact: a node may swing only so far from where the bound shape carries it. Lengths
        // are then re-held (the leash moves a node off its parent's segment) before the limits below, which keep them. Cloth shares
        // that correction between both ends, as everywhere else in its solve: a sweep down from the roots handed every millimetre
        // the leash took off a panel's upper nodes on to its hem, which kept the hem of a resting skirt swinging for good.
        if (_leash > 0)
        {
            HoldStyle();
            if (_collideCloth) HoldLengths(2);
            else SweepLengths(0);
        }
        // The azimuth limit has the last word: the side links of the iteration would otherwise drag a panel sideways again after
        // the Bend pass, and the piece rendered this sub-step would leave its plane.
        if (_azimuth > 0)
            for (int i = 0; i < _n; i++)
                if (_parent[i] >= 0) LimitAzimuth(_parent[i] * 3, i * 3);
        // So does the angle limit: the surface and the side links can fold a piece past it after the Bend pass, and a panel folded
        // far past its limit reads as a crumple in the cloth.
        if (_cosLimit > -2)
            for (int i = 0; i < _n; i++)
            {
                int a = _parent[i];
                if (a < 0) continue;
                int g = _parent[a];
                double tx, ty, tz;
                if (g < 0)
                {
                    TargetDir(a * 3, i * 3);
                    tx = _t[0]; ty = _t[1]; tz = _t[2];
                }
                else
                {
                    tx = _p[a * 3] - _p[g * 3]; ty = _p[a * 3 + 1] - _p[g * 3 + 1]; tz = _p[a * 3 + 2] - _p[g * 3 + 2];
                    double len = Math.Sqrt(tx * tx + ty * ty + tz * tz);
                    if (len < 1e-9) continue;
                    tx /= len; ty /= len; tz /= len;
                }
                Limit(a * 3, i * 3, tx, ty, tz);
            }
        // And the leash after the limits too: a limit turns a piece about its parent and can carry its node past what the leash
        // allowed before them, and the shape drawn this sub-step is what the player sees. The lengths and the shape are settled
        // against each other twice; the shape has the last word (the few tenths of a millimetre it leaves a piece stretched are
        // invisible next to a strand standing off the head).
        if (_leash > 0)
        {
            for (int pass = 0; pass < 2; pass++) { HoldStyle(); SweepLengths(0); }
            HoldStyle();
        }
        Inelastic();
    }

    // Cloth's pieces at their bound length with both ends moving (a root stays pinned). A one-sided sweep drags a node the leg pushed
    // clear back toward its parent, which can pull the panel into the leg; sharing the correction bends the panel around it instead.
    void HoldLengths(int passes)
    {
        for (int pass = 0; pass < passes; pass++)
            for (int i = 0; i < _n; i++)
            {
                int a = _parent[i];
                if (a < 0) continue;
                Hold(a * 3, i * 3, _len[i], _w[a], _w[i], 1);
            }
    }

    // Every piece to exactly its bound length from its parent (down the strands top down: a root is pinned).
    void SweepLengths(double tolerance)
    {
        for (int i = 0; i < _n; i++)
        {
            if (_parent[i] < 0) continue;
            int o = i * 3, a = _parent[i] * 3;
            double rest = _len[i];
            double ex = _p[o] - _p[a], ey = _p[o + 1] - _p[a + 1], ez = _p[o + 2] - _p[a + 2];
            double len = Math.Sqrt(ex * ex + ey * ey + ez * ez);
            if (len < 1e-9 || Math.Abs(len - rest) < tolerance) continue;
            double k = rest / len;
            _p[o] = _p[a] + ex * k;
            _p[o + 1] = _p[a + 1] + ey * k;
            _p[o + 2] = _p[a + 2] + ez * k;
        }
    }

    // Every node's target this sub-step: the bound shape laid down each strand from its pinned root, every piece along its bound
    // direction turned by the anchor, blended toward the same direction turned by each body bone that drives it (a thigh under a
    // skirt) by the weights measured at the bind — a cloth rig's helper bones, done once for every kind. Directions blend, not places:
    // a piece is a few centimetres long and its two nodes have different weights, so blended places would turn it far more than the
    // thigh turned (the leash and the limits then fight over it every sub-step: a buzz). Laid at the pieces' own lengths, the target
    // is a shape the length sweep agrees with.
    void Targets()
    {
        for (int i = 0; i < _n; i++)
        {
            int o = i * 3;
            Rotate(_sub[3], _sub[4], _sub[5], _sub[6], _dir[o], _dir[o + 1], _dir[o + 2], _targetDir, o);
        }
        for (int k = 0; k < _drvNode.Length; k++)
        {
            int s = _drvShell[k] * 10, o = _drvNode[k] * 3, at = k * 3;
            if (!(_shSub[s + 7] > 1e-9 && _shSub[s + 8] > 1e-9 && _shSub[s + 9] > 1e-9)) continue; // a gone bone: its share stays with the anchor
            Rotate(_sub[3], _sub[4], _sub[5], _sub[6], _dir[o], _dir[o + 1], _dir[o + 2], _t, 3);
            Rotate(_shSub[s + 3], _shSub[s + 4], _shSub[s + 5], _shSub[s + 6], _drvAt[at], _drvAt[at + 1], _drvAt[at + 2], _t, 0);
            double w = _drvW[k];
            _targetDir[o] += (_t[0] - _t[3]) * w;
            _targetDir[o + 1] += (_t[1] - _t[4]) * w;
            _targetDir[o + 2] += (_t[2] - _t[5]) * w;
        }
        for (int i = 0; i < _n; i++)
        {
            int o = i * 3, a = _parent[i] * 3;
            if (a < 0)
            {
                _target[o] = _sub[0] + _targetDir[o]; _target[o + 1] = _sub[1] + _targetDir[o + 1]; _target[o + 2] = _sub[2] + _targetDir[o + 2];
                continue;
            }
            double x = _targetDir[o], y = _targetDir[o + 1], z = _targetDir[o + 2];
            double l = Math.Sqrt(x * x + y * y + z * z);
            if (l < 1e-9) { Rotate(_sub[3], _sub[4], _sub[5], _sub[6], _dir[o], _dir[o + 1], _dir[o + 2], _t, 0); x = _t[0]; y = _t[1]; z = _t[2]; l = 1; }
            double k = _len[i] / l;
            _target[o] = _target[a] + x * k;
            _target[o + 1] = _target[a + 1] + y * k;
            _target[o + 2] = _target[a + 2] + z * k;
        }
    }

    // The unit direction from node a's target to node o's into _t[0..2] (the bound direction carried by the anchor when they meet).
    void TargetDir(int a, int o)
    {
        double x = _target[o] - _target[a], y = _target[o + 1] - _target[a + 1], z = _target[o + 2] - _target[a + 2];
        double l = Math.Sqrt(x * x + y * y + z * z);
        if (l < 1e-9) { Rotate(_sub[3], _sub[4], _sub[5], _sub[6], _dir[o], _dir[o + 1], _dir[o + 2], _t, 0); return; }
        _t[0] = x / l; _t[1] = y / l; _t[2] = z / l;
    }

    // Keeps every node inside a small ball around its target this sub-step (Targets: the bound shape carried by the anchor and the
    // body bones that drive it) — the max distance of a cloth solver. The allowance grows with the node's reach down its strand: a
    // fringe's tip may move a few centimetres, a ponytail's or a hem's more. This is what stops a fast dance throwing short strands off
    // the head, and a stride leaving the skirt where the thigh now is.
    void HoldStyle()
    {
        for (int i = 0; i < _n; i++)
        {
            if (_parent[i] < 0) continue;
            int o = i * 3;
            double dx = _p[o] - _target[o], dy = _p[o + 1] - _target[o + 1], dz = _p[o + 2] - _target[o + 2];
            double d2 = dx * dx + dy * dy + dz * dz;
            double limit = _leash * (0.02 * _scale + _reachTo[i]);
            if (d2 <= limit * limit) continue;
            double k = limit / Math.Sqrt(d2);
            _p[o] = _target[o] + dx * k;
            _p[o + 1] = _target[o + 1] + dy * k;
            _p[o + 2] = _target[o + 2] + dz * k;
        }
    }

    // The bend once per sub-step, top down: a damped spring toward the straight continuation of the piece above (the first piece toward
    // its bound direction carried by the anchor, a piece bound at an angle toward that angle turned with the piece above), the restore
    // spring toward the bound shape carried by the anchor (both shared between the node and the one above), then the angle limit
    // around the bend's target.
    void Bend(double mx, double my, double mz)
    {
        for (int i = 0; i < _n; i++)
        {
            int a = _parent[i];
            if (a < 0) continue;
            int o = i * 3, g = _parent[a];
            a *= 3;
            double tx, ty, tz;
            if (g < 0)
            {
                TargetDir(a, o);
                tx = _t[0]; ty = _t[1]; tz = _t[2];
            }
            else
            {
                g *= 3;
                tx = _p[a] - _p[g]; ty = _p[a + 1] - _p[g + 1]; tz = _p[a + 2] - _p[g + 2];
                double len = Math.Sqrt(tx * tx + ty * ty + tz * tz);
                if (len < 1e-9) continue;
                tx /= len; ty /= len; tz /= len;
                if (!_straight[i]) TurnedLike(a, o, ref tx, ref ty, ref tz);
            }
            double rest = _len[i];
            double k = _bendK[i] * _bendShare, d = _bendD[i] * _bendShare;
            if (k > 0 || d > 0)
            {
                // The node's move this sub-step against the node above's (below a root, the root's move with gravity's).
                double ux = g < 0 ? _rm[a] : _p[a] - _q[a], uy = g < 0 ? _rm[a + 1] - _gravity : _p[a + 1] - _q[a + 1], uz = g < 0 ? _rm[a + 2] : _p[a + 2] - _q[a + 2];
                Share(a, o, (_p[a] + tx * rest - _p[o]) * k - (_p[o] - _q[o] - ux) * d,
                    (_p[a + 1] + ty * rest - _p[o + 1]) * k - (_p[o + 1] - _q[o + 1] - uy) * d,
                    (_p[a + 2] + tz * rest - _p[o + 2]) * k - (_p[o + 2] - _q[o + 2] - uz) * d);
            }
            if (_restore > 0 && g >= 0)
            {
                double share = _restoreK[i] * _bendShare;
                TargetDir(a, o);
                Share(a, o, (_p[a] + _t[0] * rest - _p[o]) * share, (_p[a + 1] + _t[1] * rest - _p[o + 1]) * share, (_p[a + 2] + _t[2] * rest - _p[o + 2]) * share);
            }
            if (_cosLimit > -2) Limit(a, o, tx, ty, tz);
            if (_azimuth > 0) LimitAzimuth(a, o);
        }
    }

    // Moves node o by (x, y, z) against node a, shared by their weights: o its share, a the rest the other way (a root none). The pull
    // between them is equal and opposite, as a joint's spring is; pulling the child alone kept feeding a swing against the colliders.
    void Share(int a, int o, double x, double y, double z)
    {
        double wo = _w[o / 3], wa = _w[a / 3], so = wo / (wo + wa), sa = wa / (wo + wa);
        _p[o] += x * so; _p[o + 1] += y * so; _p[o + 2] += z * so;
        _p[a] -= x * sa; _p[a + 1] -= y * sa; _p[a + 2] -= z * sa;
    }

    // The bound direction of node o turned by the shortest turn that takes its parent's bound direction onto the parent's current
    // one (t, unit, world) into t. Both bound directions are in the anchor's frame: taken into the world first.
    void TurnedLike(int a, int o, ref double tx, ref double ty, ref double tz)
    {
        Rotate(_sub[3], _sub[4], _sub[5], _sub[6], _dir[a], _dir[a + 1], _dir[a + 2], _t, 3);
        Rotate(_sub[3], _sub[4], _sub[5], _sub[6], _dir[o], _dir[o + 1], _dir[o + 2], _t, 6);
        double rx = _t[3], ry = _t[4], rz = _t[5], vx = _t[6], vy = _t[7], vz = _t[8];
        double c = rx * tx + ry * ty + rz * tz;
        if (c < -0.999999) return; // folded back: aim along the parent
        double kx = ry * tz - rz * ty, ky = rz * tx - rx * tz, kz = rx * ty - ry * tx;
        double kv = (kx * vx + ky * vy + kz * vz) / (1 + c);
        tx = vx * c + (ky * vz - kz * vy) + kx * kv;
        ty = vy * c + (kz * vx - kx * vz) + ky * kv;
        tz = vz * c + (kx * vy - ky * vx) + kz * kv;
    }

    // Keeps the piece a -> o close to its own radial plane: the plane that holds the piece's bound direction (in the anchor's frame)
    // and the way its parent lies from the anchor. Measured as the angle between the piece and that plane, so it bounds folding
    // sideways (either way) while flaring outward or inward stays free. This is what stops a skirt panel from crossing the one
    // beside it; a cone limit cannot, and a soft side link only resists it.
    void LimitAzimuth(int a, int o)
    {
        double ex = _p[o] - _p[a], ey = _p[o + 1] - _p[a + 1], ez = _p[o + 2] - _p[a + 2];
        double len = Math.Sqrt(ex * ex + ey * ey + ez * ez);
        if (len < 1e-9) return;
        double dx = ex / len, dy = ey / len, dz = ez / len;
        double rx = _dir[o], ry = _dir[o + 1], rz = _dir[o + 2];
        // The radial reference: the parent's bound place from the anchor, less its part along the bound direction.
        double qx = _restPos[a], qy = _restPos[a + 1], qz = _restPos[a + 2];
        double qc = qx * rx + qy * ry + qz * rz;
        double fx = qx - rx * qc, fy = qy - ry * qc, fz = qz - rz * qc;
        double fl = Math.Sqrt(fx * fx + fy * fy + fz * fz);
        if (fl < 1e-6) return;
        // The plane's normal: bound direction x radial, in the anchor's frame; into the world with the anchor, then turned the
        // shortest way from the bound direction there onto the piece's target direction, so the plane goes with the thigh that
        // drives the panel instead of holding it back where the anchor alone would have it.
        double nx = ry * fz - rz * fy, ny = rz * fx - rx * fz, nz = rx * fy - ry * fx;
        double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (nl < 1e-9) return;
        Rotate(_sub[3], _sub[4], _sub[5], _sub[6], nx / nl, ny / nl, nz / nl, _t, 3);
        Rotate(_sub[3], _sub[4], _sub[5], _sub[6], rx, ry, rz, _t, 6);
        TargetDir(a, o);
        Arc(_t[6], _t[7], _t[8], _t[0], _t[1], _t[2], _t, 3);
        nx = _t[3]; ny = _t[4]; nz = _t[5];
        double s = dx * nx + dy * ny + dz * nz; // sine of the angle from the plane
        double lim = Math.Sin(_azimuth);
        if (Math.Abs(s) <= lim) return;
        // Keep the part along the plane, take the out-of-plane part down to the limit.
        double px = dx - nx * s, py = dy - ny * s, pz = dz - nz * s;
        double pl = Math.Sqrt(px * px + py * py + pz * pz);
        if (pl < 1e-6) return; // pointing along the plane's normal: no side to choose
        double k = Math.Cos(_azimuth) / pl;
        _p[o] = _p[a] + (px * k + nx * (s > 0 ? lim : -lim)) * len;
        _p[o + 1] = _p[a + 1] + (py * k + ny * (s > 0 ? lim : -lim)) * len;
        _p[o + 2] = _p[a + 2] + (pz * k + nz * (s > 0 ? lim : -lim)) * len;
    }

    // The vector r turned by the shortest turn that takes unit u onto unit v, into o[at..at+2] (r as it is when u is v's opposite).
    static void Arc(double ux, double uy, double uz, double vx, double vy, double vz, double[] o, int at)
    {
        double rx = o[at], ry = o[at + 1], rz = o[at + 2];
        double c = ux * vx + uy * vy + uz * vz;
        if (c < -0.999999) return;
        double kx = uy * vz - uz * vy, ky = uz * vx - ux * vz, kz = ux * vy - uy * vx;
        double kr = (kx * rx + ky * ry + kz * rz) / (1 + c);
        o[at] = rx * c + (ky * rz - kz * ry) + kx * kr;
        o[at + 1] = ry * c + (kz * rx - kx * rz) + ky * kr;
        o[at + 2] = rz * c + (kx * ry - ky * rx) + kz * kr;
    }

    // Keeps the piece a -> o within its angle limit around the unit direction t, at its current length.
    void Limit(int a, int o, double tx, double ty, double tz)
    {
        int i = o / 3;
        double cosLimit = _cosLimitK[i], sinLimit = _sinLimitK[i];
        double ex = _p[o] - _p[a], ey = _p[o + 1] - _p[a + 1], ez = _p[o + 2] - _p[a + 2];
        double len = Math.Sqrt(ex * ex + ey * ey + ez * ez);
        if (len < 1e-9) return;
        double c = (ex * tx + ey * ty + ez * tz) / len;
        if (c >= cosLimit) return;
        // Perpendicular part of the piece, unit; on the cone's edge in that plane.
        double px = ex / len - tx * c, py = ey / len - ty * c, pz = ez / len - tz * c;
        double pl = Math.Sqrt(px * px + py * py + pz * pz);
        if (pl < 1e-9) { px = ty; py = -tx; pz = 0; pl = Math.Sqrt(px * px + py * py); if (pl < 1e-9) { px = 1; pl = 1; } }
        px /= pl; py /= pl; pz /= pl;
        _p[o] = _p[a] + (tx * cosLimit + px * sinLimit) * len;
        _p[o + 1] = _p[a + 1] + (ty * cosLimit + py * sinLimit) * len;
        _p[o + 2] = _p[a + 2] + (tz * cosLimit + pz * sinLimit) * len;
    }

    // Moves nodes a and b (offsets) toward `rest` apart by their weights, `stiffness` of the way.
    void Hold(int a, int b, double rest, double wa, double wb, double stiffness)
    {
        double ex = _p[b] - _p[a], ey = _p[b + 1] - _p[a + 1], ez = _p[b + 2] - _p[a + 2];
        double len = Math.Sqrt(ex * ex + ey * ey + ez * ez);
        double sum = wa + wb;
        if (len < 1e-9 || sum <= 0) return;
        double diff = (len - rest) / (len * sum);
        if (stiffness != 1) diff *= stiffness;
        _p[a] += ex * diff * wa; _p[a + 1] += ey * diff * wa; _p[a + 2] += ez * diff * wa;
        _p[b] -= ex * diff * wb; _p[b + 1] -= ey * diff * wb; _p[b + 2] -= ez * diff * wb;
    }

    // A side link is cloth: it may not stretch apart (a torn panel) and it holds its width against being squeezed in (a leg must push
    // the whole panel, not sink between two bones), but it gives a few per cent when pinched so the ring does not buckle into a fold.
    void HoldSide(int a, int b, double rest)
    {
        double ex = _p[b] - _p[a], ey = _p[b + 1] - _p[a + 1], ez = _p[b + 2] - _p[a + 2];
        double len = Math.Sqrt(ex * ex + ey * ey + ez * ez);
        double wa = _w[a / 3], wb = _w[b / 3], sum = wa + wb;
        if (len < 1e-9 || sum <= 0) return;
        double pull = len - rest;
        if (pull < 0)
        {
            if (pull > -rest * 0.05) return; // 5 % slack: enough to absorb a pinch without the panel folding
            pull = (pull + rest * 0.05) * 0.9;
        }
        double diff = pull / (len * sum);
        _p[a] += ex * diff * wa; _p[a + 1] += ey * diff * wa; _p[a + 2] += ez * diff * wa;
        _p[b] -= ex * diff * wb; _p[b + 1] -= ey * diff * wb; _p[b + 2] -= ez * diff * wb;
    }

    void PushOutOfBox(int o)
    {
        for (int i = 0; i < _n; i++)
        {
            if (_parent[i] < 0) continue;
            int v = i * 3;
            double r = _radius[i];
            double rx = _p[v] - _world[o], ry = _p[v + 1] - _world[o + 1], rz = _p[v + 2] - _world[o + 2];
            int axis = -1;
            double best = double.MaxValue, push = 0;
            for (int j = 0; j < 3; j++)
            {
                double l = rx * _world[o + 3 + j * 3] + ry * _world[o + 4 + j * 3] + rz * _world[o + 5 + j * 3];
                double e = _world[o + 12 + j] + r;
                double gap = e - Math.Abs(l);
                if (gap <= 0) { axis = -1; break; }
                if (gap < best) { best = gap; axis = j; push = (l >= 0 ? e : -e) - l; }
            }
            if (axis < 0) continue;
            _p[v] += _world[o + 3 + axis * 3] * push;
            _p[v + 1] += _world[o + 4 + axis * 3] * push;
            _p[v + 2] += _world[o + 5 + axis * 3] * push;
        }
    }

    // Out of the capsule at this sub-step: along the line from the nearest point on its axis.
    void PushOutOfCapsule(int o)
    {
        double ax = _capSub[o], ay = _capSub[o + 1], az = _capSub[o + 2];
        double sx = _capSub[o + 3] - ax, sy = _capSub[o + 4] - ay, sz = _capSub[o + 5] - az;
        double ss = sx * sx + sy * sy + sz * sz;
        for (int i = 0; i < _n; i++)
        {
            if (_parent[i] < 0) continue;
            int v = i * 3;
            double px = _p[v] - ax, py = _p[v + 1] - ay, pz = _p[v + 2] - az;
            double t = ss > 1e-18 ? Math.Clamp((px * sx + py * sy + pz * sz) / ss, 0, 1) : 0;
            double dx = px - sx * t, dy = py - sy * t, dz = pz - sz * t;
            double d2 = dx * dx + dy * dy + dz * dz, r = _capSub[o + 6] + _radius[i];
            if (d2 >= r * r || d2 < 1e-18) continue;
            double d = Math.Sqrt(d2), k = (r - d) / d;
            _p[v] += dx * k; _p[v + 1] += dy * k; _p[v + 2] += dz * k;
        }
    }

    // Once, at the first rest: how near the surface each node already is, and the point it is nearest then. A node bound clear of
    // the surface keeps the chain's standoff from it; a node bound inside it (a cloth rig's bones run inside the body, the fabric
    // is held out by the weights) keeps that depth — bounding must not shove the cloth out of the body it was made in.
    void MeasureShells()
    {
        if (_shMeasured) return;
        _shMeasured = true;
        for (int s = 0; s < _shells; s++)
        {
            ChainShellData data = _shellData[s];
            if (data == null || data.Count == 0) continue;
            int o = s * 10;
            double sx = _shPose[o + 7], sy = _shPose[o + 8], sz = _shPose[o + 9];
            if (!(sx > 1e-9 && sy > 1e-9 && sz > 1e-9)) continue;
            double unit = (sx + sy + sz) / 3;
            for (int i = 0; i < _n; i++)
            {
                if (_parent[i] < 0) continue;
                int v = i * 3, slot = s * _n + i;
                double depth = BoundDepth(data, o, _p[v], _p[v + 1], _p[v + 2], unit, out int p);
                if (p < 0) continue;
                _shRestNear[slot] = p;
                // Bound outside the surface: the node may gain a little clearance over the bind (up to the standoff), so a raised radius
                // works where it matters and a bone bound close to the body does not fight its way out of the cloth's own style — that
                // fight is a buzz. Bound inside it — a cloth rig whose bones run inside the body, the fabric held out by the weights:
                // exactly that depth, so binding never shoves the cloth out.
                _shFloor[slot] = depth >= 0 ? Math.Min(Clearance(i), depth + _floorSlack) : depth;
            }
            // The fabric is the model's own surface: held off the skin by _fabGap, or at the depth it was made at.
            for (int k = 0; k < _nf; k++)
            {
                if (!FabricAt(k, _cur)) continue;
                double depth = BoundDepth(data, o, _t[6], _t[7], _t[8], unit, out int p);
                if (p < 0) continue;
                _fabRestNear[s * _nf + k] = p;
                _fabFloor[s * _nf + k] = depth >= 0 ? Math.Min(_fabGap, depth + _floorSlack) : depth;
            }
        }
        MeasureDrivers();
        int nIn = 0, nOut = 0;
        double min = 0, sum = 0;
        int used = 0;
        for (int s = 0; s < _shells; s++)
            for (int i = 0; i < _n; i++)
            {
                double f = _shFloor[s * _n + i];
                if (double.IsNaN(f)) continue;
                used++;
                if (f >= 0) nOut++; else nIn++;
                if (f < min) min = f;
                sum += f;
            }
        DebugBoundInside = nIn;
        DebugBoundOutside = nOut;
        DebugBoundDepthMin = used > 0 ? min : 0;
        DebugBoundDepthMean = used > 0 ? sum / used : 0;
    }

    // Once, with MeasureShells at the bind: every node's weight toward each driving shell (ChainShellData.Drives) and the bound direction
    // of its piece in that shell bone's frame. The weight falls off smoothly with how far the node — or, for a node that carries fabric, its
    // nearest fabric point — lay from that surface (DriveReach at the worn scale; inside it counts as on it); a node's weights are
    // scaled down to sum to at most 1, the anchor keeping the rest.
    void MeasureDrivers()
    {
        double reach = DriveReach * _scale;
        var shell = new List<int>();
        var node = new List<int>();
        var weight = new List<double>();
        var at = new List<double>();
        var sum = new double[_n];
        for (int s = 0; s < _shells; s++)
        {
            ChainShellData data = _shellData[s];
            if (data == null || data.Count == 0 || !data.Drives) continue;
            int o = s * 10;
            double sx = _shPose[o + 7], sy = _shPose[o + 8], sz = _shPose[o + 9];
            if (!(sx > 1e-9 && sy > 1e-9 && sz > 1e-9)) continue;
            double unit = (sx + sy + sz) / 3;
            for (int i = 0; i < _n; i++)
            {
                if (_parent[i] < 0) continue;
                int v = i * 3;
                double d = double.MaxValue;
                if (_clothed[i])
                {
                    for (int k = 0; k < _nf; k++)
                        if ((_fabNode[k] == i || _parent[_fabNode[k]] == i) && FabricAt(k, _cur))
                            d = Math.Min(d, SurfaceDistance(data, o, _t[6], _t[7], _t[8], unit));
                }
                else d = SurfaceDistance(data, o, _p[v], _p[v + 1], _p[v + 2], unit);
                double f = 1 - d / reach;
                if (!(f > 0)) continue;
                shell.Add(s); node.Add(i);
                weight.Add(f * f * (3 - 2 * f));
                sum[i] += weight[^1];
                // The piece's bound direction (from the anchor's frame) in the shell bone's frame.
                Rotate(_cur[3], _cur[4], _cur[5], _cur[6], _dir[v], _dir[v + 1], _dir[v + 2], _t, 3);
                Rotate(-_shPose[o + 3], -_shPose[o + 4], -_shPose[o + 5], _shPose[o + 6], _t[3], _t[4], _t[5], _t, 0);
                at.Add(_t[0]); at.Add(_t[1]); at.Add(_t[2]);
            }
        }
        for (int k = 0; k < weight.Count; k++)
            if (sum[node[k]] > 1) weight[k] /= sum[node[k]];
        _drvShell = shell.ToArray();
        _drvNode = node.ToArray();
        _drvW = weight.ToArray();
        _drvAt = at.ToArray();
    }

    // How far the world point lies from shell `o`'s surface at its bind pose, world metres: 0 inside, else to the nearest point.
    double SurfaceDistance(ChainShellData data, int o, double x, double y, double z, double unit)
    {
        double depth = BoundDepth(data, o, x, y, z, unit, out int p);
        if (p < 0) return double.MaxValue;
        if (depth <= 0) return 0;
        Rotate(-_shPose[o + 3], -_shPose[o + 4], -_shPose[o + 5], _shPose[o + 6], x - _shPose[o], y - _shPose[o + 1], z - _shPose[o + 2], _t, 0);
        int q = p * 6;
        double ex = _t[0] / _shPose[o + 7] - data.Points[q], ey = _t[1] / _shPose[o + 8] - data.Points[q + 1], ez = _t[2] / _shPose[o + 9] - data.Points[q + 2];
        return Math.Sqrt(ex * ex + ey * ey + ez * ez) * unit;
    }

    // How deep the world point (x, y, z) lies against shell `o` at its bind pose (_shPose), world metres, negative inside, and the
    // nearest point then (-1: none). Behind the nearest point only if mostly along its normal: a point past an open surface's edge (a
    // skirt bone in the pelvis, over the rim of a thigh's surface) is behind the rim's sideways normal without being inside that
    // surface, and taking that as its bound depth let the thigh pass through the skirt by as much. It is outside, as far as it is
    // from the surface.
    double BoundDepth(ChainShellData data, int o, double x, double y, double z, double unit, out int p)
    {
        Rotate(-_shPose[o + 3], -_shPose[o + 4], -_shPose[o + 5], _shPose[o + 6], x - _shPose[o], y - _shPose[o + 1], z - _shPose[o + 2], _t, 0);
        double lx = _t[0] / _shPose[o + 7], ly = _t[1] / _shPose[o + 8], lz = _t[2] / _shPose[o + 9];
        p = data.Nearest(lx, ly, lz, double.MaxValue, -1); // the whole cloud: this runs once
        if (p < 0) return 0;
        int q = p * 6;
        double ox = lx - data.Points[q], oy = ly - data.Points[q + 1], oz = lz - data.Points[q + 2];
        double inside = ox * data.Points[q + 3] + oy * data.Points[q + 4] + oz * data.Points[q + 5];
        double off = ox * ox + oy * oy + oz * oz;
        if (inside < 0 && inside * inside < 0.5 * off) inside = Math.Sqrt(off);
        return inside * unit;
    }

    // Where fabric point `k` lies now, into _t[6..8] (world): its bound offset turned with the anchor pose `pose`, then the shortest
    // way from its piece's bound direction onto that piece's direction now. False for a piece of no length.
    bool FabricAt(int k, double[] pose)
    {
        int i = _fabNode[k], o = i * 3, a = _parent[i] * 3, f = k * 4;
        double dx = _p[o] - _p[a], dy = _p[o + 1] - _p[a + 1], dz = _p[o + 2] - _p[a + 2];
        double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (len < 1e-9) return false;
        double vx = dx / len, vy = dy / len, vz = dz / len;
        Rotate(pose[3], pose[4], pose[5], pose[6], _dir[o], _dir[o + 1], _dir[o + 2], _t, 9);
        double ux = _t[9], uy = _t[10], uz = _t[11];
        Rotate(pose[3], pose[4], pose[5], pose[6], _fab[f + 1], _fab[f + 2], _fab[f + 3], _t, 12);
        double rx = _t[12], ry = _t[13], rz = _t[14];
        double c = ux * vx + uy * vy + uz * vz;
        if (c > -0.999)
        {
            // Rodrigues with k = u x v (|k| = sin): r c + k x r + k (k . r) / (1 + c).
            double kx = uy * vz - uz * vy, ky = uz * vx - ux * vz, kz = ux * vy - uy * vx;
            double kr = (kx * rx + ky * ry + kz * rz) / (1 + c);
            double nx = rx * c + (ky * rz - kz * ry) + kx * kr;
            double ny = ry * c + (kz * rx - kx * rz) + ky * kr;
            double nz = rz * c + (kx * ry - ky * rx) + kz * kr;
            rx = nx; ry = ny; rz = nz;
        }
        double t = _fab[f];
        _t[6] = _p[a] + dx * t + rx;
        _t[7] = _p[a + 1] + dy * t + ry;
        _t[8] = _p[a + 2] + dz * t + rz;
        return true;
    }

    // Shell `o` at this sub-step: the nodes, the cloth between them, and the fabric the pieces carry.
    void CollideShell(int o)
    {
        PushOutOfShell(o);
        if (_collideCloth) PushSegmentsOutOfShell(o);
        if (_hasFabric) PushFabricOutOfShell(o);
    }

    // The fabric of every piece that has some (ChainShape.Fabric), kept out of the surface: behind the nearest skin point's outward
    // normal, both nodes of the piece (not a pinned root) move out along that normal by the same nudge. The bones themselves may lie
    // inside the body — a skirt's do, under the thighs — and only the cloth is held at the standoff, or at the depth it was made at.
    // A fabric point that went deep this sub-step leaves by the side it was last found clear on.
    void PushFabricOutOfShell(int o)
    {
        ChainShellData data = _shellData[o / 10];
        if (data == null || data.Count == 0) return;
        int first = o / 10 * _nf;
        double px = _shSub[o], py = _shSub[o + 1], pz = _shSub[o + 2];
        double sx = _shSub[o + 7], sy = _shSub[o + 8], sz = _shSub[o + 9];
        if (!(sx > 1e-9 && sy > 1e-9 && sz > 1e-9)) return;
        double qx = _shSub[o + 3], qy = _shSub[o + 4], qz = _shSub[o + 5], qw = _shSub[o + 6];
        double smax = Math.Max(sx, Math.Max(sy, sz)), unit = (sx + sy + sz) / 3;
        Rotate(qx, qy, qz, qw, data.Center[0] * sx, data.Center[1] * sy, data.Center[2] * sz, _t, 0);
        double cx = px + _t[0], cy = py + _t[1], cz = pz + _t[2];
        double margin = _fabGap, band = Math.Max(_bandFloor, margin);
        double far = smax * (data.Radius + data.Cell) + band;
        for (int i = 0; i < _n; i++)
        {
            if (_fabOff[i] < 0) continue;
            int o2 = i * 3, a2 = _parent[i] * 3;
            double dx = _p[o2] - _p[a2], dy = _p[o2 + 1] - _p[a2 + 1], dz = _p[o2 + 2] - _p[a2 + 2];
            double mx = (_p[o2] + _p[a2]) * 0.5 - cx, my = (_p[o2 + 1] + _p[a2 + 1]) * 0.5 - cy, mz = (_p[o2 + 2] + _p[a2 + 2]) * 0.5 - cz;
            double reach = far + _fabSpan[i] * Math.Sqrt(dx * dx + dy * dy + dz * dz) + _fabOff[i];
            _fabFar[i] = mx * mx + my * my + mz * mz > reach * reach;
        }
        for (int k = 0; k < _nf; k++)
        {
            if (_fabFar[_fabNode[k]] || !FabricAt(k, _sub)) continue;
            int i = _fabNode[k];
            double fx = _t[6], fy = _t[7], fz = _t[8];
            if ((fx - cx) * (fx - cx) + (fy - cy) * (fy - cy) + (fz - cz) * (fz - cz) > far * far) continue;
            Rotate(-qx, -qy, -qz, qw, fx - px, fy - py, fz - pz, _t, 3);
            double lx = _t[3] / sx, ly = _t[4] / sy, lz = _t[5] / sz;
            int slot = first + k;
            // One search: a point within a cell is also the nearest of the wider one (anything in its outer ring is a cell away).
            int p = data.Nearest(lx, ly, lz, data.Cell * 3, 2, _fabHint[slot]);
            _fabHint[slot] = p;
            if (p < 0) p = _fabNear[slot] >= 0 ? _fabNear[slot] : _fabRestNear[slot];
            if (p < 0) continue;
            int q = p * 6;
            double nx = data.Points[q + 3], ny = data.Points[q + 4], nz = data.Points[q + 5];
            double ox = lx - data.Points[q], oy = ly - data.Points[q + 1], oz = lz - data.Points[q + 2];
            double gap = margin, floor = _fabFloor[slot];
            if (!double.IsNaN(floor) && floor < gap) gap = floor;
            double inside = (ox * nx + oy * ny + oz * nz) * unit;
            int last = _fabNear[slot] >= 0 ? _fabNear[slot] : _fabRestNear[slot];
            double fade = 1;
            if (inside < gap - 2 * band && last >= 0)
            {
                // Deep: the nearest point now may be the far side of the limb; the one it was last clear of says which way is out.
                int r = last * 6;
                nx = data.Points[r + 3]; ny = data.Points[r + 4]; nz = data.Points[r + 5];
                inside = ((lx - data.Points[r]) * nx + (ly - data.Points[r + 1]) * ny + (lz - data.Points[r + 2]) * nz) * unit;
            }
            // Far from the nearest point, its plane says nothing (a leg's surface is an open tube, and a point well above its rim is
            // behind the rim's normal without being in the leg).
            else if ((fade = Fade((ox * ox + oy * oy + oz * oz) * unit * unit, band)) <= 0) continue;
            if (inside >= gap - _dead)
            {
                _fabNear[slot] = p;
                continue;
            }
            double push = (gap - _dead - inside) / unit * fade;
            int a = _parent[i], va = a * 3, vb = i * 3;
            bool moveA = _parent[a] >= 0;
            double ua = moveA ? Math.Sqrt(_shPush[va] * _shPush[va] + _shPush[va + 1] * _shPush[va + 1] + _shPush[va + 2] * _shPush[va + 2]) : 0;
            double ub = Math.Sqrt(_shPush[vb] * _shPush[vb] + _shPush[vb + 1] * _shPush[vb + 1] + _shPush[vb + 2] * _shPush[vb + 2]);
            double most = Math.Min(_fabBudget, Math.Min(_budget - ua, _budget - ub)) / unit;
            if (most <= 0) continue;
            if (push > most) push = most;
            // The nudge along the normal, in the world.
            Rotate(qx, qy, qz, qw, nx * push * sx, ny * push * sy, nz * push * sz, _t, 3);
            double mx = _t[3], my = _t[4], mz = _t[5];
            if (!moveA)
            {
                // The root stays pinned: the far node alone moves, by more where the fabric sits near the root (it moves the
                // fabric only by its share of the way down). That turns the piece, and the fabric's offset turns with it: a point
                // held well off a short first piece moves several times the nudge, which threw it clear and back every other
                // sub-step. Where it moved further along the nudge than asked, the node is taken back by the excess share.
                double lever = 1 / Math.Max(_fab[k * 4], 0.5), want = mx * mx + my * my + mz * mz;
                _p[vb] += mx * lever; _p[vb + 1] += my * lever; _p[vb + 2] += mz * lever;
                double along = FabricAt(k, _sub) ? ((_t[6] - fx) * mx + (_t[7] - fy) * my + (_t[8] - fz) * mz) / want : 1;
                _p[vb] -= mx * lever; _p[vb + 1] -= my * lever; _p[vb + 2] -= mz * lever;
                if (along > 1) lever /= along;
                mx *= lever; my *= lever; mz *= lever;
            }
            for (int e = moveA ? 0 : 1; e < 2; e++)
            {
                int v = e == 0 ? va : vb;
                _p[v] += mx; _p[v + 1] += my; _p[v + 2] += mz;
                _shPush[v] += mx; _shPush[v + 1] += my; _shPush[v + 2] += mz;
            }
        }
    }

    // Contacts are inelastic: once the sub-step is solved, a node the surfaces pushed keeps no speed along the way they pushed it in
    // all, neither the push (cloth thrown off the skin by a correction a few millimetres deep bounced back and forth for good) nor the
    // speed it came in with. Only after every constraint: taken at each push, a push the lengths then undid left the node a speed
    // it never had, which kept a strand caught between two surfaces swinging for good.
    void Inelastic()
    {
        for (int v = 0; v < _n * 3; v += 3)
        {
            double nx = _shPush[v], ny = _shPush[v + 1], nz = _shPush[v + 2];
            double l = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (l < 1e-12) continue;
            nx /= l; ny /= l; nz /= l;
            double vn = (_p[v] - _q[v]) * nx + (_p[v + 1] - _q[v + 1]) * ny + (_p[v + 2] - _q[v + 2]) * nz;
            _q[v] += nx * vn; _q[v + 1] += ny * vn; _q[v + 2] += nz * vn;
        }
    }

    // How much a surface point's plane counts for a query `d2` (squared, world) from it: whole within two thirds of the band, falling
    // to nothing at its edge. A plane that counted whole up to the edge and not at all past it let a node sink the whole gap unseen
    // just outside, then threw it out by that much at once: a strand bouncing on the head, a panel buzzing on the hip for good.
    static double Fade(double d2, double band)
    {
        double d = Math.Sqrt(d2);
        return Math.Clamp((band - d) * 3 / band, 0, 1);
    }

    // How far the surfaces keep node i's bone out: its radius and the standoff; in a set with fabric, the fabric's own clearance, since
    // its bare bones stand for the same cloth (a bone held 3 cm off the thigh beside fabric held 1 cm off it never came to rest).
    double Clearance(int i) => _hasFabric ? _fabGap : _radius[i] + _standoff;

    // Out of the body's surface at this sub-step: a node behind the nearest skin point's outward normal is moved out along that
    // normal. The points sit in the shell bone's own space, so the node is taken there through the bone's pose first.
    void PushOutOfShell(int o)
    {
        ChainShellData data = _shellData[o / 10];
        if (data == null || data.Count == 0) return;
        int first = o / 10 * _n;
        double px = _shSub[o], py = _shSub[o + 1], pz = _shSub[o + 2];
        double sx = _shSub[o + 7], sy = _shSub[o + 8], sz = _shSub[o + 9];
        if (!(sx > 1e-9 && sy > 1e-9 && sz > 1e-9)) return;
        double qx = _shSub[o + 3], qy = _shSub[o + 4], qz = _shSub[o + 5], qw = _shSub[o + 6];
        double smax = Math.Max(sx, Math.Max(sy, sz)), unit = (sx + sy + sz) / 3;
        // The shell's middle in the world, for a quick miss.
        Rotate(qx, qy, qz, qw, data.Center[0] * sx, data.Center[1] * sy, data.Center[2] * sz, _t, 0);
        double cx = px + _t[0], cy = py + _t[1], cz = pz + _t[2];
        double near = smax * (data.Radius + data.Cell);
        for (int i = 0; i < _n; i++)
        {
            if (_parent[i] < 0 || _clothed[i]) continue; // a node carrying fabric: its fabric is what is kept out
            int v = i * 3;
            double dx = _p[v] - cx, dy = _p[v + 1] - cy, dz = _p[v + 2] - cz;
            double margin = Clearance(i);
            double far = near + margin;
            if (dx * dx + dy * dy + dz * dz > far * far) continue;
            // The node in the shell bone's space.
            double wx = _p[v] - px, wy = _p[v + 1] - py, wz = _p[v + 2] - pz;
            Rotate(-qx, -qy, -qz, qw, wx, wy, wz, _t, 3);
            double lx = _t[3] / sx, ly = _t[4] / sy, lz = _t[5] / sz;
            // Where the node was when the shell was last searched for it, it has not moved far enough to look again. Only a point
            // found: a node that found none and kept that until it had moved half a cell sank that far before the surface answered,
            // and was then thrown out by the whole depth at once (a strand that dropped onto the head and jumped off it for good).
            int slot = first + i, at = slot * 3;
            int prev = _shNear[slot];
            int p;
            double wasX = _shNearAt[at], wasY = _shNearAt[at + 1], wasZ = _shNearAt[at + 2];
            double stepX = lx - wasX, stepY = ly - wasY, stepZ = lz - wasZ;
            if (prev >= 0 && !double.IsNaN(wasX) && stepX * stepX + stepY * stepY + stepZ * stepZ <= data.Cell * data.Cell * 0.25)
                p = _shNear[slot];
            else
            {
                // Two cells around, for a node that has sunk a few centimetres into the body.
                p = data.Nearest(lx, ly, lz, data.Cell * 3, 2, prev);
                _shNear[slot] = p;
                _shNearAt[at] = lx; _shNearAt[at + 1] = ly; _shNearAt[at + 2] = lz;
            }
            if (p < 0) p = _shRestNear[slot]; // deep in the body: the point it had when bound still says which way out
            if (p < 0) continue;
            int q = p * 6;
            double nx = data.Points[q + 3], ny = data.Points[q + 4], nz = data.Points[q + 5];
            double cx0 = lx - data.Points[q], cy0 = ly - data.Points[q + 1], cz0 = lz - data.Points[q + 2];
            // A point further off than the standoff says nothing about where the body is there: cloth and hair pass well clear of most
            // of the body's points, and those points' planes would push a strand across a limb — a twitch that never settles. The band
            // is never narrower than the standoff itself: raising a set's radius is the author asking for that much clearance, and the
            // surface has to answer at that distance or the radius would only matter on contact.
            double band = Math.Max(_bandFloor, margin);
            double fade = Fade((cx0 * cx0 + cy0 * cy0 + cz0 * cz0) * unit * unit, band);
            if (fade <= 0) continue;
            double inside = (cx0 * nx + cy0 * ny + cz0 * nz) * unit;
            // The floor: the standoff, or the depth the node was bound at when that is nearer the surface than the standoff.
            double gap = margin;
            double floor = _shFloor[slot];
            if (!double.IsNaN(floor) && floor < gap) gap = floor;
            // A node that crossed the surface this sub-step leaves by the side it came from: its previous place was clear on
            // the side of the point it was nearest then, and its place now is behind that point. The nearest point now can be a
            // different part (the inside of a leg, the far side of the hip) and would push it the wrong way, which is the cloth
            // that passes through the model on one fast step.
            if (inside < gap - 2 * margin)
            {
                if (prev >= 0)
                {
                    int r = prev * 6;
                    double rnx = data.Points[r + 3], rny = data.Points[r + 4], rnz = data.Points[r + 5];
                    double qwx = _q[v] - px, qwy = _q[v + 1] - py, qwz = _q[v + 2] - pz;
                    Rotate(-qx, -qy, -qz, qw, qwx, qwy, qwz, _t, 0);
                    double prevInside = (_t[0] / sx - data.Points[r]) * rnx + (_t[1] / sy - data.Points[r + 1]) * rny + (_t[2] / sz - data.Points[r + 2]) * rnz;
                    if (prevInside * unit >= gap - _dead)
                    {
                        nx = rnx; ny = rny; nz = rnz;
                        inside = ((lx - data.Points[r]) * nx + (ly - data.Points[r + 1]) * ny + (lz - data.Points[r + 2]) * nz) * unit;
                    }
                }
                else if (_shRestNear[slot] >= 0)
                {
                    // First time this node is this deep: the point it was nearest at the bind still says which way is out.
                    int r = _shRestNear[slot] * 6;
                    nx = data.Points[r + 3]; ny = data.Points[r + 4]; nz = data.Points[r + 5];
                    inside = ((lx - data.Points[r]) * nx + (ly - data.Points[r + 1]) * ny + (lz - data.Points[r + 2]) * nz) * unit;
                }
            }
            // A dead band: cloth resting on the body breathes in and out a millimetre or two every sub-step; pushing on that
            // would be a buzz at the sub-step's own rate. Only past 3 mm does the surface answer.
            if (inside >= gap - _dead) continue;
            double push = (gap - _dead - inside) / unit * fade;
            // A contact correction is a nudge, never a jump: a node gets a bounded share of surface correction a sub-step, all
            // surfaces and iterations together (the body can move into cloth fast; the cloth follows at this pace and no faster).
            double used = Math.Sqrt(_shPush[v] * _shPush[v] + _shPush[v + 1] * _shPush[v + 1] + _shPush[v + 2] * _shPush[v + 2]);
            double room = _budget - used;
            if (room <= 0) continue;
            double most = Math.Min(_softCap, room) / unit;
            if (push > most) push = most;
            lx = (lx + nx * push) * sx;
            ly = (ly + ny * push) * sy;
            lz = (lz + nz * push) * sz;
            Rotate(qx, qy, qz, qw, lx, ly, lz, _t, 3);
            double mx = px + _t[3] - _p[v], my = py + _t[4] - _p[v + 1], mz = pz + _t[5] - _p[v + 2];
            _shPush[v] += mx; _shPush[v + 1] += my; _shPush[v + 2] += mz;
            _p[v] += mx; _p[v + 1] += my; _p[v + 2] += mz;
        }
    }

    // The cloth along a piece, kept out of the surface: the middle of every piece (down a strand, across the links between strands)
    // is taken into the shell bone's space and, behind the nearest skin point's outward normal, both its nodes move out along that
    // normal. No cache and no rest point: a piece's middle is where a leg comes up between two bones, and the panel must give there
    // too, not only at the bones. The floor is the two nodes' bound depths, so a cloth rig whose bones start inside the body is not
    // shoved out of it.
    void PushSegmentsOutOfShell(int o)
    {
        ChainShellData data = _shellData[o / 10];
        if (data == null || data.Count == 0) return;
        int first = o / 10 * _n;
        double px = _shSub[o], py = _shSub[o + 1], pz = _shSub[o + 2];
        double sx = _shSub[o + 7], sy = _shSub[o + 8], sz = _shSub[o + 9];
        if (!(sx > 1e-9 && sy > 1e-9 && sz > 1e-9)) return;
        double qx = _shSub[o + 3], qy = _shSub[o + 4], qz = _shSub[o + 5], qw = _shSub[o + 6];
        double smax = Math.Max(sx, Math.Max(sy, sz)), unit = (sx + sy + sz) / 3;
        Rotate(qx, qy, qz, qw, data.Center[0] * sx, data.Center[1] * sy, data.Center[2] * sz, _t, 0);
        double cx = px + _t[0], cy = py + _t[1], cz = pz + _t[2];
        double near = smax * (data.Radius + data.Cell);
        for (int k = 0; k < _segA.Length; k++)
        {
            int ia = _segA[k], ib = _segB[k], va = ia * 3, vb = ib * 3;
            // A node that carries fabric is kept out by its fabric alone, as PushOutOfShell leaves it: a piece's middle held out at the
            // bone clearance beside fabric held at a centimetre pulled the panel two ways every sub-step (the skirt that never rests).
            if (_clothed[ia] || _clothed[ib]) continue;
            double wx = (_p[va] + _p[vb]) * 0.5 - cx, wy = (_p[va + 1] + _p[vb + 1]) * 0.5 - cy, wz = (_p[va + 2] + _p[vb + 2]) * 0.5 - cz;
            double margin = Math.Max(Clearance(ia), Clearance(ib));
            double far = near + margin;
            if (wx * wx + wy * wy + wz * wz > far * far) continue;
            double mw0 = (_p[va] + _p[vb]) * 0.5 - px, mw1 = (_p[va + 1] + _p[vb + 1]) * 0.5 - py, mw2 = (_p[va + 2] + _p[vb + 2]) * 0.5 - pz;
            Rotate(-qx, -qy, -qz, qw, mw0, mw1, mw2, _t, 3);
            double lx = _t[3] / sx, ly = _t[4] / sy, lz = _t[5] / sz;
            int p = data.Nearest(lx, ly, lz, data.Cell * 3, 2);
            if (p < 0) continue;
            int q = p * 6;
            double nx = data.Points[q + 3], ny = data.Points[q + 4], nz = data.Points[q + 5];
            double ox = lx - data.Points[q], oy = ly - data.Points[q + 1], oz = lz - data.Points[q + 2];
            double band = Math.Max(_bandFloor, margin);
            double fade = Fade((ox * ox + oy * oy + oz * oz) * unit * unit, band);
            if (fade <= 0) continue;
            double inside = (ox * nx + oy * ny + oz * nz) * unit;
            double gap = margin;
            double fa = _shFloor[first + ia], fb = _shFloor[first + ib];
            if (!double.IsNaN(fa) || !double.IsNaN(fb))
            {
                double f = double.IsNaN(fa) ? fb : double.IsNaN(fb) ? fa : (fa + fb) * 0.5;
                if (f < gap) gap = f;
            }
            if (inside >= gap - _dead) continue;
            double push = (gap - _dead - inside) / unit * fade;
            // Both nodes move by the same world nudge, so the piece translates rather than stretches; its size is the same budget a
            // node's own correction gets.
            double ua = Math.Sqrt(_shPush[va] * _shPush[va] + _shPush[va + 1] * _shPush[va + 1] + _shPush[va + 2] * _shPush[va + 2]);
            double ub = Math.Sqrt(_shPush[vb] * _shPush[vb] + _shPush[vb + 1] * _shPush[vb + 1] + _shPush[vb + 2] * _shPush[vb + 2]);
            double most = Math.Min(_segBudget, Math.Min(_budget - ua, _budget - ub)) / unit;
            if (most <= 0) continue;
            if (push > most) push = most;
            // A pinned root stays: the other node alone moves, twice as far, so the piece's middle still moves by the push.
            bool pinA = _parent[ia] < 0, pinB = _parent[ib] < 0;
            if (pinA && pinB) continue;
            if (pinA || pinB) push *= 2;
            for (int e = pinA ? 1 : 0; e < (pinB ? 1 : 2); e++)
            {
                int v = e == 0 ? va : vb;
                double ew0 = _p[v] - px, ew1 = _p[v + 1] - py, ew2 = _p[v + 2] - pz;
                Rotate(-qx, -qy, -qz, qw, ew0, ew1, ew2, _t, 3);
                double ex = (_t[3] / sx + nx * push) * sx, ey = (_t[4] / sy + ny * push) * sy, ez = (_t[5] / sz + nz * push) * sz;
                Rotate(qx, qy, qz, qw, ex, ey, ez, _t, 3);
                double dx = px + _t[3] - _p[v], dy = py + _t[4] - _p[v + 1], dz = pz + _t[5] - _p[v + 2];
                _shPush[v] += dx; _shPush[v + 1] += dy; _shPush[v + 2] += dz;
                _p[v] += dx; _p[v + 1] += dy; _p[v + 2] += dz;
            }
        }
    }

    // Nodes of different strands of one set, kept apart at their bound distance (or their radii when the bind has them further apart
    // than that), and kept on the side they were bound on: position only, at most 6 mm a pass (at the worn scale), so fast hair slides
    // along its neighbours instead of braiding through them. Pairs that already lie closer than their radii at the bind keep that
    // distance, so the bound style is never forced open.
    void PushSelvesApart()
    {
        double cap = 0.006 * _scale;
        int tangled = 0;
        double tight = 0.005 * _scale;
        for (int k = 0; k < _selfA.Length; k++)
        {
            int a = _selfA[k] * 3, b = _selfB[k] * 3;
            double floor = _selfFloor[k];
            // Never wider than the pair's distance in the target: a thigh that turns two panels toward each other narrows it, and a
            // floor held at the bound distance would fight the target every sub-step (the skirt that never comes to rest).
            double tx = _target[b] - _target[a], ty = _target[b + 1] - _target[a + 1], tz = _target[b + 2] - _target[a + 2];
            floor = Math.Min(floor, Math.Sqrt(tx * tx + ty * ty + tz * tz) - 0.002 * _scale);
            if (floor <= 0) continue;
            double dx = _p[b] - _p[a], dy = _p[b + 1] - _p[a + 1], dz = _p[b + 2] - _p[a + 2];
            double d2 = dx * dx + dy * dy + dz * dz;
            if (d2 < (floor - tight) * (floor - tight)) tangled++;
            if (d2 < floor * floor && d2 > 1e-12)
            {
                double d = Math.Sqrt(d2), push = Math.Min((floor - d) * 0.6, cap), kk = push / d;
                _p[a] -= dx * kk; _p[a + 1] -= dy * kk; _p[a + 2] -= dz * kk;
                _p[b] += dx * kk; _p[b + 1] += dy * kk; _p[b + 2] += dz * kk;
                dx = _p[b] - _p[a]; dy = _p[b + 1] - _p[a + 1]; dz = _p[b + 2] - _p[a + 2];
                d2 = dx * dx + dy * dy + dz * dz;
            }
            // The anti-swap constraint: a node that has crossed to the other side of the partner it was bound beside is pushed back
            // round, however far apart the two are: their order along the scalp is fixed, and a pair that separates while crossed
            // is a tangle waiting to come together again (the hair that had to be taken off and put back on to look right). A
            // strand that crosses its neighbour is the first half of the knot a fast dance ties into long hair; the distance floor
            // alone lets it slide through between nodes. Cloth sets (a skirt) keep their panels in order by their azimuth planes
            // and side links; forcing a panel's nodes to one side of a neighbour there only buckles the panel.
            if (_collideCloth || k * 3 + 2 >= _selfDirSub.Length) continue;
            double side = dx * _selfDirSub[k * 3] + dy * _selfDirSub[k * 3 + 1] + dz * _selfDirSub[k * 3 + 2];
            double near = Math.Max(2.5 * floor, _selfRest[k]);
            if (side >= 0 || d2 >= near * near) continue;
            double back = Math.Min(-side * 0.5, cap) * 0.5;
            _p[a] -= _selfDirSub[k * 3] * back; _p[a + 1] -= _selfDirSub[k * 3 + 1] * back; _p[a + 2] -= _selfDirSub[k * 3 + 2] * back;
            _p[b] += _selfDirSub[k * 3] * back; _p[b + 1] += _selfDirSub[k * 3 + 1] * back; _p[b + 2] += _selfDirSub[k * 3 + 2] * back;
        }
        DebugTangled = tangled;
    }

    // x = anchor now + d * (x - anchor before), for the node at `o` of `x`.
    void Carried(double[] x, int o, double dx, double dy, double dz, double dw)
    {
        Rotate(dx, dy, dz, dw, x[o] - _last[0], x[o + 1] - _last[1], x[o + 2] - _last[2], _t, 0);
        x[o] = _sub[0] + _t[0];
        x[o + 1] = _sub[1] + _t[1];
        x[o + 2] = _sub[2] + _t[2];
    }

    // ---- math ----

    /// <summary>Position and rotation (unit quaternion) of a matrix whose columns may carry scale.</summary>
    public static void Pose(double[] m, double[] pose) => Pose(m, pose, 0);

    /// <summary>The same into pose[at..at+6].</summary>
    public static void Pose(double[] m, double[] pose, int at)
    {
        pose[at] = m[12]; pose[at + 1] = m[13]; pose[at + 2] = m[14];
        double sx = Math.Sqrt(m[0] * m[0] + m[1] * m[1] + m[2] * m[2]);
        double sy = Math.Sqrt(m[4] * m[4] + m[5] * m[5] + m[6] * m[6]);
        double sz = Math.Sqrt(m[8] * m[8] + m[9] * m[9] + m[10] * m[10]);
        double m00 = m[0] / sx, m10 = m[1] / sx, m20 = m[2] / sx;
        double m01 = m[4] / sy, m11 = m[5] / sy, m21 = m[6] / sy;
        double m02 = m[8] / sz, m12 = m[9] / sz, m22 = m[10] / sz;
        double tr = m00 + m11 + m22, x, y, z, w;
        if (tr > 0)
        {
            double s = 0.5 / Math.Sqrt(tr + 1);
            w = 0.25 / s; x = (m21 - m12) * s; y = (m02 - m20) * s; z = (m10 - m01) * s;
        }
        else if (m00 > m11 && m00 > m22)
        {
            double s = 2 * Math.Sqrt(1 + m00 - m11 - m22);
            w = (m21 - m12) / s; x = 0.25 * s; y = (m01 + m10) / s; z = (m02 + m20) / s;
        }
        else if (m11 > m22)
        {
            double s = 2 * Math.Sqrt(1 + m11 - m00 - m22);
            w = (m02 - m20) / s; x = (m01 + m10) / s; y = 0.25 * s; z = (m12 + m21) / s;
        }
        else
        {
            double s = 2 * Math.Sqrt(1 + m22 - m00 - m11);
            w = (m10 - m01) / s; x = (m02 + m20) / s; y = (m12 + m21) / s; z = 0.25 * s;
        }
        double l = Math.Sqrt(x * x + y * y + z * z + w * w);
        pose[at + 3] = x / l; pose[at + 4] = y / l; pose[at + 5] = z / l; pose[at + 6] = w / l;
    }

    /// <summary>Rotates (vx, vy, vz) by the unit quaternion (x, y, z, w) into o[at..at+2].</summary>
    public static void Rotate(double x, double y, double z, double w, double vx, double vy, double vz, double[] o, int at)
    {
        double cx = y * vz - z * vy, cy = z * vx - x * vz, cz = x * vy - y * vx;
        double ex = y * cz - z * cy, ey = z * cx - x * cz, ez = x * cy - y * cx;
        o[at] = vx + 2 * (w * cx + ex);
        o[at + 1] = vy + 2 * (w * cy + ey);
        o[at + 2] = vz + 2 * (w * cz + ez);
    }

    // The rotation f of the way from a's to b's (poses, quaternion at 3..6) into o[3..6].
    static void Slerp(double[] a, double[] b, double f, double[] o)
    {
        double bx = b[3], by = b[4], bz = b[5], bw = b[6];
        double d = a[3] * bx + a[4] * by + a[5] * bz + a[6] * bw;
        if (d < 0) { d = -d; bx = -bx; by = -by; bz = -bz; bw = -bw; }
        double ka, kb;
        if (d > 0.9995) { ka = 1 - f; kb = f; }
        else
        {
            double th = Math.Acos(d), sn = Math.Sin(th);
            ka = Math.Sin((1 - f) * th) / sn; kb = Math.Sin(f * th) / sn;
        }
        double x = a[3] * ka + bx * kb, y = a[4] * ka + by * kb, z = a[5] * ka + bz * kb, w = a[6] * ka + bw * kb;
        double l = Math.Sqrt(x * x + y * y + z * z + w * w);
        o[3] = x / l; o[4] = y / l; o[5] = z / l; o[6] = w / l;
    }
}

/// <summary>A body surface chains are kept out of: points with their outward normals (x, y, z, nx, ny, nz per point) on the model's
/// skin, in one joint bone's own space, in a uniform grid for nearest-point queries. Built once per character; every chain shares it.</summary>
public sealed class ChainShellData
{
    public readonly int Count;
    public readonly double[] Points;
    /// <summary>The middle of the points, and the farthest point's distance from it: a node outside that sphere never touches.</summary>
    public readonly double[] Center = new double[3];
    public readonly double Radius;
    /// <summary>The joint drives the chains that hang against it (a thigh, a shin): their targets follow it by how close they were
    /// bound to it (ChainState.Targets), as a cloth rig's helper bones follow the legs.</summary>
    public bool Drives { get; init; }
    /// <summary>The grid's cell size; a nearest-point query reaches one cell, so a node farther than this from every point is free.</summary>
    public readonly double Cell;

    readonly double _minX, _minY, _minZ;
    readonly int _nx, _ny, _nz;
    readonly int[] _start = { 0 }, _items = Array.Empty<int>();

    public ChainShellData(double[] points)
    {
        int n = points.Length / 6;
        Count = n;
        if (n == 0) { Points = Array.Empty<double>(); Cell = 1; _nx = _ny = _nz = 1; return; }
        Points = points;
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        for (int i = 0; i < n; i++)
        {
            int o = i * 6;
            minX = Math.Min(minX, points[o]); maxX = Math.Max(maxX, points[o]);
            minY = Math.Min(minY, points[o + 1]); maxY = Math.Max(maxY, points[o + 1]);
            minZ = Math.Min(minZ, points[o + 2]); maxZ = Math.Max(maxZ, points[o + 2]);
        }
        _minX = minX; _minY = minY; _minZ = minZ;
        Center[0] = (minX + maxX) / 2; Center[1] = (minY + maxY) / 2; Center[2] = (minZ + maxZ) / 2;
        double radius = 0;
        for (int i = 0; i < n; i++)
        {
            int o = i * 6;
            double x = points[o] - Center[0], y = points[o + 1] - Center[1], z = points[o + 2] - Center[2];
            radius = Math.Max(radius, x * x + y * y + z * z);
        }
        Radius = Math.Sqrt(radius);
        double span = Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ));
        Cell = Math.Clamp(span / 24, 0.008, 0.03);
        // A coarse mesh (few points over a large part) needs a coarser grid, or a node would sit several points away and be missed.
        Cell = Math.Max(Cell, span / (2.2 * Math.Cbrt(n)));
        _nx = Math.Max(1, (int)Math.Ceiling((maxX - minX) / Cell));
        _ny = Math.Max(1, (int)Math.Ceiling((maxY - minY) / Cell));
        _nz = Math.Max(1, (int)Math.Ceiling((maxZ - minZ) / Cell));
        long cells = (long)_nx * _ny * _nz;
        while (cells > 400_000) // a very wide shell: a coarser grid
        {
            Cell *= 1.5;
            _nx = Math.Max(1, (int)Math.Ceiling((maxX - minX) / Cell));
            _ny = Math.Max(1, (int)Math.Ceiling((maxY - minY) / Cell));
            _nz = Math.Max(1, (int)Math.Ceiling((maxZ - minZ) / Cell));
            cells = (long)_nx * _ny * _nz;
        }
        var start = new int[cells + 1];
        var items = new int[n];
        for (int i = 0; i < n; i++) start[CellOf(points[i * 6], points[i * 6 + 1], points[i * 6 + 2]) + 1]++;
        for (int c = 0; c < cells; c++) start[c + 1] += start[c];
        var at = (int[])start.Clone();
        for (int i = 0; i < n; i++) items[at[CellOf(points[i * 6], points[i * 6 + 1], points[i * 6 + 2])]++] = i;
        _start = start; _items = items;
    }

    int CellOf(double x, double y, double z)
    {
        int ix = Math.Clamp((int)((x - _minX) / Cell), 0, _nx - 1);
        int iy = Math.Clamp((int)((y - _minY) / Cell), 0, _ny - 1);
        int iz = Math.Clamp((int)((z - _minZ) / Cell), 0, _nz - 1);
        return (ix * _ny + iy) * _nz + iz;
    }

    /// <summary>The point nearest to (x, y, z) within `reach` among the `span` cells around it (1: the cells touching its own;
    /// -1: every point, for a one-off measurement), as an index into <see cref="Points"/>, or -1.
    /// `hint`: a point found near here before (-1: none). Cells further off than the nearest found so far are passed over, so a good
    /// guess leaves most of them unread; the answer does not depend on it.</summary>
    public int Nearest(double x, double y, double z, double reach, int span = 1, int hint = -1)
    {
        if (Count == 0) return -1;
        if (span < 0)
        {
            double nearest = reach * reach;
            int at = -1;
            for (int p = 0; p < Count; p++)
            {
                int o = p * 6;
                double ex = x - Points[o], ey = y - Points[o + 1], ez = z - Points[o + 2];
                double d = ex * ex + ey * ey + ez * ez;
                if (d < nearest) { nearest = d; at = p; }
            }
            return at;
        }
        int ix = Math.Clamp((int)((x - _minX) / Cell), 0, _nx - 1);
        int iy = Math.Clamp((int)((y - _minY) / Cell), 0, _ny - 1);
        int iz = Math.Clamp((int)((z - _minZ) / Cell), 0, _nz - 1);
        double best = reach * reach;
        int found = -1;
        if (hint >= 0 && hint < Count)
        {
            // Only a point the search itself would have read: in the block of cells, within reach.
            int o = hint * 6;
            double ex = x - Points[o], ey = y - Points[o + 1], ez = z - Points[o + 2];
            double d = ex * ex + ey * ey + ez * ez;
            int hx = Math.Clamp((int)((Points[o] - _minX) / Cell), 0, _nx - 1);
            int hy = Math.Clamp((int)((Points[o + 1] - _minY) / Cell), 0, _ny - 1);
            int hz = Math.Clamp((int)((Points[o + 2] - _minZ) / Cell), 0, _nz - 1);
            if (d < best && Math.Abs(hx - ix) <= span && Math.Abs(hy - iy) <= span && Math.Abs(hz - iz) <= span) { best = d; found = hint; }
        }
        for (int dx = -span; dx <= span; dx++)
        {
            int cxi = ix + dx;
            if (cxi < 0 || cxi >= _nx) continue;
            double gx = Math.Max(0, Math.Abs(x - (_minX + (cxi + 0.5) * Cell)) - Cell * 0.5);
            if (gx * gx >= best) continue;
            for (int dy = -span; dy <= span; dy++)
            {
                int cyi = iy + dy;
                if (cyi < 0 || cyi >= _ny) continue;
                double gy = Math.Max(0, Math.Abs(y - (_minY + (cyi + 0.5) * Cell)) - Cell * 0.5);
                if (gx * gx + gy * gy >= best) continue;
                int row = (cxi * _ny + cyi) * _nz;
                for (int dz = -span; dz <= span; dz++)
                {
                    int czi = iz + dz;
                    if (czi < 0 || czi >= _nz) continue;
                    double gz = Math.Max(0, Math.Abs(z - (_minZ + (czi + 0.5) * Cell)) - Cell * 0.5);
                    if (gx * gx + gy * gy + gz * gz >= best) continue;
                    int c = row + czi;
                    for (int k = _start[c]; k < _start[c + 1]; k++)
                    {
                        int p = _items[k], o = p * 6;
                        double ex = x - Points[o], ey = y - Points[o + 1], ez = z - Points[o + 2];
                        double d = ex * ex + ey * ey + ez * ez;
                        if (d < best) { best = d; found = p; }
                    }
                }
            }
        }
        return found;
    }
}
