using System;
using System.Collections.Generic;
using UnityEngine;

namespace ForgeVisual;

/// <summary>A collider a bound chain is kept out of: a capsule on a bone from A to B in the bone's own space (a sphere when A is B), its
/// radius in metres.</summary>
public readonly struct ChainCapsule
{
    public ChainCapsule(Transform bone, Vector3 a, Vector3 b, float radius) { Bone = bone; A = a; B = b; Radius = radius; }
    public Transform Bone { get; }
    public Vector3 A { get; }
    public Vector3 B { get; }
    public float Radius { get; }
}

/// <summary>The skin of one joint as a surface (<see cref="ChainShellData"/>): points with outward normals in `bone`'s own space.</summary>
public readonly struct ChainShell
{
    public ChainShell(Transform bone, ChainShellData data) { Bone = bone; Data = data; }
    public Transform Bone { get; }
    public ChainShellData Data { get; }
}

/// <summary>A chain physics (<see cref="ChainState"/>) bound to bone chains: hair, a ponytail, a skirt, a tail. Every frame, after the pose
/// copies, it steps and turns each bone so its child lands where the chain put it; a chain more than <see cref="Chains.CullDistance"/>
/// from the local eye is not stepped (its bones keep the animated pose) and starts again at rest when it comes back.</summary>
public sealed class ChainBinding
{
    private readonly Transform _anchor;
    private readonly Transform? _body;
    private readonly Transform[] _bones; // every strand's bones, root first, in node order (the tips have none)
    private readonly int[] _child; // per bone, the node its child is
    private readonly Vector3[] _tip; // per bone, its child (the next bone, or the strand's tip) in its own space as bound
    private readonly Quaternion[] _rest; // local rotations as bound, given back on release
    private readonly Quaternion[] _lastSwing; // the swing written last frame, for the per-frame cap (a swing is all physics adds)
    private readonly bool[] _written;
    private readonly ChainCapsule[] _capsules;
    private readonly ChainShell[] _shells;
    private readonly ChainState _state;
    private readonly float _capsuleSize; // the model size the capsules' radii were built at
    private readonly double[] _matrix = new double[16], _body3 = new double[3], _shellMatrix = new double[16];
    private static readonly double[] GoneMatrix = new double[16];
    internal bool Bound = true;
    // Debug telemetry: what the game's chains actually do (speeds, surface pushes, and whether anything writes the bones after us).
    private readonly string _label;
    private readonly Quaternion[] _lastLocal;
    private readonly double[] _prev;
    private bool _prevValid;
    private double _lastStepTime = double.NaN, _nextLog;
    private double _winSpeed, _winDrift, _winPush;
    private int _winTangled;
    private double _bindScale, _scaleCandidate, _scaleSince = double.NaN;
    private double _winReach, _winDev, _winShort;
    private int _winRest, _restSeen;

    // The swing is the whole of what physics adds to a bone — its bound roll is kept, never accumulated — so physics cannot wind a
    // strand into a rope. Two caps keep a pathological frame from flipping a piece: the swing stays within 150 degrees of the rest
    // direction, and moves at most 60 degrees a frame toward what the chain asks.
    private const float MaxSwingDeg = 170f;
    private const float MaxSwingStepDeg = 120f;

    internal ChainBinding(Transform anchor, IReadOnlyList<Transform[]> strands, ChainSpec spec, IReadOnlyList<ChainCapsule>? capsules, IReadOnlyList<ChainShell>? shells, bool linkSide, bool loop, Transform? body, string? label = null, IReadOnlyDictionary<Transform, float[]>? fabric = null)
    {
        _anchor = anchor;
        _body = body;
        // The owner (the person the model hangs on) tells the local player's copy from a bot's in the telemetry: the same chain id
        // runs on several skeletons at once, and their behaviour is what has to be compared.
        var owner = anchor.root != null ? anchor.root.name : "";
        if (owner.Length > 26) owner = owner[..26];
        _label = (string.IsNullOrEmpty(label) ? anchor.name : label) + (owner.Length > 0 ? "@" + owner : "");
        var shape = new ChainShape();
        var bones = new List<Transform>();
        var children = new List<int>();
        var tips = new List<Vector3>();
        var side = new List<int[]>();
        var turnBack = Quaternion.Inverse(anchor.rotation);
        var origin = anchor.position;
        foreach (var strand in strands)
        {
            if (strand == null || strand.Length < 2) throw new ArgumentException("a strand needs at least two bones");
            var ids = new int[strand.Length + 1];
            var r = turnBack * (strand[0].position - origin);
            ids[0] = shape.AddRoot(r.x, r.y, r.z);
            for (int j = 1; j <= strand.Length; j++)
            {
                // The tip continues the last bone by its length.
                Vector3 from = strand[j - 1].position, to = j < strand.Length ? strand[j].position : from + (from - strand[j - 2].position);
                var d = turnBack * (to - from);
                ids[j] = shape.Add(ids[j - 1], Math.Max(d.magnitude, 1e-4), d.x, d.y, d.z);
                // The cloth the bone carries: per point t down its piece, its offset from the bone's own space into the anchor's frame.
                if (fabric != null && fabric.TryGetValue(strand[j - 1], out var f))
                    for (int k = 0; k + 3 < f.Length; k += 4)
                    {
                        var off = turnBack * strand[j - 1].TransformVector(f[k + 1], f[k + 2], f[k + 3]);
                        shape.Fabric(ids[j], f[k], off.x, off.y, off.z);
                    }
            }
            for (int j = 0; j < strand.Length; j++)
            {
                bones.Add(strand[j]);
                children.Add(ids[j + 1]);
                var p = strand[j].position;
                tips.Add(strand[j].InverseTransformPoint(j + 1 < strand.Length ? strand[j + 1].position : p + (p - strand[j - 1].position)));
            }
            side.Add(ids);
        }
        if (linkSide) shape.LinkSide(side, loop);
        _bones = bones.ToArray();
        _child = children.ToArray();
        _tip = tips.ToArray();
        _rest = new Quaternion[_bones.Length];
        _lastSwing = new Quaternion[_bones.Length];
        _written = new bool[_bones.Length];
        for (int i = 0; i < _bones.Length; i++)
        {
            _rest[i] = _bones[i].localRotation;
            _lastSwing[i] = Quaternion.identity;
        }
        _capsules = capsules == null ? Array.Empty<ChainCapsule>() : new List<ChainCapsule>(capsules).ToArray();
        _shells = shells == null ? Array.Empty<ChainShell>() : new List<ChainShell>(shells).ToArray();
        // The character is worn scaled (1.5–1.8 in the game): every collision distance of the solver scales with the anchor.
        var worn = anchor.lossyScale;
        float scale = (Mathf.Abs(worn.x) + Mathf.Abs(worn.y) + Mathf.Abs(worn.z)) / 3f;
        if (!(scale > 1e-4f) || !float.IsFinite(scale)) scale = 1f;
        if (scale > 10f) scale = 10f; // a display copy bound at a degenerate scale still gets sane distances
        _bindScale = scale;
        // Characters.Wear already multiplied the capsule radii by the size the model was put on at; keep that size so a re-fit to
        // another size (the elevator's 1.811 stand-in becoming the player's 1.509 body) scales their radii with the body too.
        _capsuleSize = scale;
        _state = new ChainState(shape, spec) { Scale = scale, Capsules = _capsules.Length, Shells = _shells.Length };
        _lastLocal = new Quaternion[_bones.Length];
        _prev = new double[_state.Count * 3];
        UnityEngine.Debug.Log($"[ForgeSkin chain] bound {_label}: nodes={_state.Count} bones={_bones.Length} anchor={anchor.name} shells={_shells.Length} capsules={_capsules.Length} scale={anchor.lossyScale.x:F3}");
        if (_shells.Length == 0) return;
        for (int s = 0; s < _shells.Length; s++) _state.SetShellData(s, _shells[s].Data);
        // Bound at rest, before the first pose copy: lay the chain at rest against the shells now, so the depth every node keeps
        // from the surface is measured on the bind pose and not on the first animated frame.
        var m = anchor.localToWorldMatrix;
        _matrix[0] = m.m00; _matrix[1] = m.m10; _matrix[2] = m.m20; _matrix[3] = m.m30;
        _matrix[4] = m.m01; _matrix[5] = m.m11; _matrix[6] = m.m21; _matrix[7] = m.m31;
        _matrix[8] = m.m02; _matrix[9] = m.m12; _matrix[10] = m.m22; _matrix[11] = m.m32;
        _matrix[12] = m.m03; _matrix[13] = m.m13; _matrix[14] = m.m23; _matrix[15] = m.m33;
        for (int s = 0; s < _shells.Length; s++)
        {
            var sm = _shells[s].Bone.localToWorldMatrix;
            _shellMatrix[0] = sm.m00; _shellMatrix[1] = sm.m10; _shellMatrix[2] = sm.m20; _shellMatrix[3] = sm.m30;
            _shellMatrix[4] = sm.m01; _shellMatrix[5] = sm.m11; _shellMatrix[6] = sm.m21; _shellMatrix[7] = sm.m31;
            _shellMatrix[8] = sm.m02; _shellMatrix[9] = sm.m12; _shellMatrix[10] = sm.m22; _shellMatrix[11] = sm.m32;
            _shellMatrix[12] = sm.m03; _shellMatrix[13] = sm.m13; _shellMatrix[14] = sm.m23; _shellMatrix[15] = sm.m33;
            _state.SetShellPose(s, _shellMatrix);
        }
        _state.Rest(_matrix);
        if (_shells.Length > 0)
            UnityEngine.Debug.Log($"[ForgeSkin chain] rest {_label}: shells={_shells.Length} boundOutside={_state.DebugBoundOutside} boundInside={_state.DebugBoundInside} depthMin={_state.DebugBoundDepthMin * 1000:F0}mm depthMean={_state.DebugBoundDepthMean * 1000:F0}mm");
    }

    /// <summary>One frame: false once the anchor or a bone is gone (the binding then stops).</summary>
    internal bool Step(double time, Vector3 eye)
    {
        if (ForgeCore.NativeObjects.Gone(_anchor)) return false;
        var m = _anchor.localToWorldMatrix;
        // Not stepped far away (nobody sees the pop when it comes back). Being merely unseen is no reason to stop: a renderer that
        // is culled for a few frames would freeze and then jump, and the chains are cheap enough to keep stepping.
        float ex = m.m03 - eye.x, ey = m.m13 - eye.y, ez = m.m23 - eye.z;
        if (ex * ex + ey * ey + ez * ez > Chains.CullDistance * Chains.CullDistance) return true;
        _matrix[0] = m.m00; _matrix[1] = m.m10; _matrix[2] = m.m20; _matrix[3] = m.m30;
        _matrix[4] = m.m01; _matrix[5] = m.m11; _matrix[6] = m.m21; _matrix[7] = m.m31;
        _matrix[8] = m.m02; _matrix[9] = m.m12; _matrix[10] = m.m22; _matrix[11] = m.m32;
        _matrix[12] = m.m03; _matrix[13] = m.m13; _matrix[14] = m.m23; _matrix[15] = m.m33;
        if (_body != null && !ForgeCore.NativeObjects.Gone(_body))
        {
            var b = _body.position;
            _body3[0] = b.x; _body3[1] = b.y; _body3[2] = b.z;
        }
        else { _body3[0] = m.m03; _body3[1] = m.m13; _body3[2] = m.m23; }
        var wsNow = _anchor.lossyScale;
        float wornNow = (Mathf.Abs(wsNow.x) + Mathf.Abs(wsNow.y) + Mathf.Abs(wsNow.z)) / 3f;
        float capsuleScale = wornNow > 1e-4f && float.IsFinite(wornNow) && _capsuleSize > 1e-4f ? wornNow / _capsuleSize : 1f;
        for (int c = 0; c < _capsules.Length; c++)
        {
            var cap = _capsules[c];
            if (ForgeCore.NativeObjects.Gone(cap.Bone)) { _state.SetCapsule(c, 0, -1e6, 0, 0, -1e6, 0, 0); continue; }
            Vector3 pa = cap.Bone.TransformPoint(cap.A), pb = cap.Bone.TransformPoint(cap.B);
            _state.SetCapsule(c, pa.x, pa.y, pa.z, pb.x, pb.y, pb.z, cap.Radius * capsuleScale);
        }
        for (int s = 0; s < _shells.Length; s++)
        {
            var shell = _shells[s];
            if (ForgeCore.NativeObjects.Gone(shell.Bone)) { _state.SetShellPose(s, GoneMatrix); continue; }
            var sm = shell.Bone.localToWorldMatrix;
            _shellMatrix[0] = sm.m00; _shellMatrix[1] = sm.m10; _shellMatrix[2] = sm.m20; _shellMatrix[3] = sm.m30;
            _shellMatrix[4] = sm.m01; _shellMatrix[5] = sm.m11; _shellMatrix[6] = sm.m21; _shellMatrix[7] = sm.m31;
            _shellMatrix[8] = sm.m02; _shellMatrix[9] = sm.m12; _shellMatrix[10] = sm.m22; _shellMatrix[11] = sm.m32;
            _shellMatrix[12] = sm.m03; _shellMatrix[13] = sm.m13; _shellMatrix[14] = sm.m23; _shellMatrix[15] = sm.m33;
            _state.SetShellPose(s, _shellMatrix);
        }
        // The model may end up on a body of another size than the one it was worn on (an elevator's stand-in, a menu's figure,
        // a first-person copy): a chain measured at the old size is longer or shorter than the bones, and the hair floats off
        // the head until the skin is put on again. Refit it at rest to the size it is at now.
        // The model's drawn size moves between views (the player's body and the first-person rig carry different fits), so a new
        // size is taken only once it has held for half a second: refitting on every flip fired three times a minute and reset the
        // hair each time — which is exactly the first wear that looked wrong until the skin was put on again.
        if (wornNow > 1e-4f && float.IsFinite(wornNow))
        {
            if (Math.Abs(wornNow - _bindScale) > 0.05 * _bindScale)
            {
                if (Math.Abs(wornNow - _scaleCandidate) > 1e-4f)
                {
                    _scaleCandidate = wornNow;
                    _scaleSince = time;
                }
                else if (double.IsNaN(_scaleSince) || time - _scaleSince >= 0.5)
                {
                    double factor = wornNow / _bindScale;
                    UnityEngine.Debug.Log($"[ForgeSkin chain] rescale {_label}: {_bindScale:F3} -> {wornNow:F3} (rest)");
                    _state.Rescale(factor);
                    _state.ScaleAbout(_matrix[12], _matrix[13], _matrix[14], factor);
                    // A chain scaled while swinging can end in a wrong shape that the springs never fully undo (the hair that
                    // stayed lifted until the skin was put on again); once per settled size change, lay it back on its style.
                    _state.Rest(_matrix);
                    _bindScale = wornNow;
                    _scaleSince = time;
                }
            }
            else
            {
                _scaleCandidate = wornNow;
                _scaleSince = time;
            }
        }
        double stepDt = double.IsNaN(_lastStepTime) ? 0 : time - _lastStepTime;
        _lastStepTime = time;
        _state.Update(time, _matrix, _body3);
        // Top down: each bone put at its rest orientation carried by its parent now, swung the shortest way onto where the chain put
        // the node below it. The swing is the only thing physics adds; the bound roll survives every frame untouched.
        var p = _state.Nodes;
        if (_prevValid && stepDt > 1e-4)
            for (int q = 0; q < p.Length; q += 3)
            {
                double dx = p[q] - _prev[q], dy = p[q + 1] - _prev[q + 1], dz = p[q + 2] - _prev[q + 2];
                double v = Math.Sqrt(dx * dx + dy * dy + dz * dz) / stepDt;
                if (v > _winSpeed) _winSpeed = v;
            }
        Array.Copy(p, _prev, p.Length);
        _prevValid = true;
        if (_state.DebugPushMax > _winPush) _winPush = _state.DebugPushMax;
        if (_state.DebugTangled > _winTangled) _winTangled = _state.DebugTangled;
        if (_state.DebugDev > _winDev) _winDev = _state.DebugDev;
        if (_state.DebugShortDev > _winShort) _winShort = _state.DebugShortDev;
        if (_state.DebugRestJumps != _restSeen) { _winRest += _state.DebugRestJumps - _restSeen; _restSeen = _state.DebugRestJumps; }
        for (int i = 0; i < _bones.Length; i++)
        {
            var bone = _bones[i];
            if (ForgeCore.NativeObjects.Gone(bone)) return false;
            // Did anything write this bone's local rotation after our last write? (the dance plugin's own overlay, say)
            if (_written[i])
            {
                float drift = Quaternion.Angle(bone.localRotation, _lastLocal[i]);
                if (drift > _winDrift) _winDrift = drift;
            }
            int k = _child[i] * 3;
            var parent = bone.parent;
            var qParent = parent != null ? parent.rotation : Quaternion.identity;
            var qRest = qParent * _rest[i];
            var want = new Vector3((float)p[k], (float)p[k + 1], (float)p[k + 2]) - bone.position;
            if (want.sqrMagnitude < 1e-10f) continue;
            // The wanted direction in the bone's resting frame (carried by its parent as it turns now), then the shortest swing from
            // the child direction it was bound with. Built from the rest each frame, never from last frame's result: a frame's
            // rounding cannot accumulate into a roll, and the bone keeps the twist it was bound with.
            var local = Quaternion.Inverse(qRest) * want.normalized;
            var swing = Quaternion.FromToRotation(_tip[i].normalized, local);
            float angle = Quaternion.Angle(Quaternion.identity, swing);
            if (angle > MaxSwingDeg) swing = Quaternion.Slerp(Quaternion.identity, swing, MaxSwingDeg / angle);
            float step = Quaternion.Angle(_lastSwing[i], swing);
            if (step > MaxSwingStepDeg) swing = Quaternion.Slerp(_lastSwing[i], swing, MaxSwingStepDeg / step);
            _lastSwing[i] = swing;
            bone.rotation = qRest * swing;
            _lastLocal[i] = bone.localRotation;
            _written[i] = true;
        }
        // How far the bones are from the nodes they are aimed at: a chain at the right size comes close (the bone lengths are the
        // node spacings), and a chain left at another size cannot reach, which is what a floating hairstyle looks like.
        for (int i = 0; i < _bones.Length; i++)
        {
            var bone = _bones[i];
            if (ForgeCore.NativeObjects.Gone(bone)) continue;
            int k = _child[i] * 3;
            var at = bone.TransformPoint(_tip[i]);
            double dx = at.x - p[k], dy = at.y - p[k + 1], dz = at.z - p[k + 2];
            double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (d > _winReach) _winReach = d;
        }
        if (time >= _nextLog)
        {
            _nextLog = time + 3;
            UnityEngine.Debug.Log($"[ForgeSkin chain] {_label}: speed={_winSpeed:F2} m/s push={_winPush * 1000:F1} mm tangled={_winTangled} reach={_winReach * 1000:F0}mm dev={_winDev * 1000:F0}mm short={_winShort * 1000:F0}mm rest={_winRest} scale={wornNow:F3} overwritten={_winDrift:F0}°");
            _winSpeed = _winDrift = _winPush = 0;
            _winTangled = 0;
            _winReach = _winDev = _winShort = 0;
            _winRest = 0;
        }
        return true;
    }

    /// <summary>Stops stepping and gives the bones back their bound local rotations (nothing to give back once the game destroyed them).</summary>
    public void Release()
    {
        if (!Bound) return;
        Bound = false;
        Chains.Forget(this);
        for (int i = 0; i < _bones.Length; i++)
            if (!ForgeCore.NativeObjects.Gone(_bones[i])) _bones[i].localRotation = _rest[i];
    }
}

/// <summary>Binds <see cref="ChainState"/> to bone chains and steps every binding once a frame after the pose copies (VisualFrame.LateUpdate).</summary>
public static class Chains
{
    /// <summary>Metres from the local eye beyond which a bound chain is not stepped.</summary>
    public const float CullDistance = 25f;

    private static readonly List<ChainBinding> Live = new();

    /// <summary>Binds strands of bones (each from its root bone down, at least two) to one chain set hanging from `anchor` (the head, the
    /// hips), shaped as the bones are now. `linkSide` links neighbouring strands node to node (a skirt), `loop` also the last to the first.
    /// `body` is what the chain rides along with (the character's root; the anchor when null). `capsules` and `shells` are what the chain
    /// is kept out of: the body as capsules, or as its skin (a surface); a character gives one or the other. `fabric`: per bone the cloth it
    /// carries (points of t down its piece, then x, y, z in the bone's own space), kept out of the skin instead of the bone (ChainShape.Fabric).</summary>
    public static ChainBinding Bind(Transform anchor, IReadOnlyList<Transform[]> strands, ChainSpec spec, IReadOnlyList<ChainCapsule>? capsules = null,
        bool linkSide = false, bool loop = false, Transform? body = null, IReadOnlyList<ChainShell>? shells = null, string? label = null,
        IReadOnlyDictionary<Transform, float[]>? fabric = null)
    {
        var binding = new ChainBinding(anchor, strands, spec, capsules, shells, linkSide, loop, body, label, fabric);
        Live.Add(binding);
        return binding;
    }

    internal static void Forget(ChainBinding binding) => Live.Remove(binding);

    internal static void TickAll()
    {
        if (Live.Count == 0) return;
        double time = Scene.Now;
        var eye = Scene.Eye;
        for (int i = Live.Count - 1; i >= 0; i--)
        {
            var binding = Live[i];
            try { if (binding.Step(time, eye)) continue; }
            catch (Exception error) { Plugin.Logger.ErrorOnce("visual-chain-failed", "ForgeVisual chain physics failed and stopped: " + error); }
            binding.Bound = false;
            Live.RemoveAt(i);
        }
    }
}
