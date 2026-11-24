using System.Numerics;
using System.Security.Cryptography;
using ForgeVisual;

namespace ForgeSkin.Format;

// A character skin (PLAN §4.10): the skin document's `character`. This file is the one definition of the format; the plugin, the
// editor's backend (which compiles Core/ too) and the editor's TypeScript types follow it. A document with `character` is a character
// skin: its target is `{ "weapon": "character" }` (no gear or item id) and it has no materials, attachments, parts or sounds of a gear.
// Every name of a model bone is a bone name of the model's skeleton (the descriptor's `skin.bones[].name`); every joint is a name of the
// canonical humanoid (ForgeCore ClipRig.Names: "hip", "spine0", "leftUpperArm", "leftIndex1", ...). Positions are the model's own frame
// (Unity axes, metres). In a pack every path is a file inside the zip; in an editor project a path on disk.
//
//   "character": {
//     "model": { "descriptor": "character/model.json", "mesh": "character/model.forgemesh", "textures": ["character/t0.png"] },
//     "bones": { "hip": "Hips", "spine0": "Spine", ..., "leftIndex1": "LeftHandIndex1", ... },
//     "arms": null,
//     "springs": {
//       "chains": [ { "id": "ponytail", "kind": "longHair", "anchor": "Head", "strands": [["Hair1", "Hair2", "Hair3"]],
//                     "linkSide": false, "loop": false, "weight": 1, "damping": 0.4, "stiffness": 0.35, "gravity": 1, "radius": 0.015 } ],
//       "capsules": [ { "joint": "head", "a": [0, 0.08, 0], "b": [0, 0.08, 0], "radius": 0.1 } ],
//       "shell": [ { "joint": "head", "data": "AAAA..." } ] },
//     "hide": [2],
//     "height": 1,
//     "picture": "character/picture.png",
//     "materials": [ { "name": "Body", "shader": "lit" }, { "name": "Hair", "shader": "toon", "params": { "outlineWidth": [1.5] } } ]
//   }
public sealed class CharacterSkin
{
    /// <summary>The target key of every character skin (SkinTarget.Weapon and the picker's slot).</summary>
    public const string Target = "character";
    public const float MaxRadius = 0.5f;

    // The model: ForgeCore ImportedModels format 2 (a skinned mesh with its own humanoid skeleton). `descriptor` is the JSON descriptor
    // (ImportedModelDescriptor, format 2, with only its technical bounds: MaxBones, MaxTextureSide and MaxArray), `mesh` its
    // mesh file (FMSH format 2), `textures` its PNGs in the descriptor's order. The plugin checks each file's SHA-256 against the
    // descriptor before building the model.
    public CharacterModelFiles Model { get; set; } = new();

    // Canonical joint -> model bone, each bone on one joint. Only hip is required (CharacterRig.Required): the model's root follows the
    // body's hips. A joint left out has no entry: that part of the model keeps its rest pose on the bone above it (ForgeCore
    // HumanRig.Links), so a model without fingers, without an arm or with one side unlike the other is a character all the same.
    public Dictionary<string, string> Bones { get; set; } = new();

    // First-person arms: null takes them from the model (the triangles skinned to the upper arms and below); set, another model of
    // the same skeleton, which the plugin does not read yet (it warns and wears the model's own arms).
    public CharacterArms? Arms { get; set; }

    public CharacterSprings Springs { get; set; } = new();

    // Indices of the descriptor's materials not drawn in first person (left out of the first-person arms).
    public List<int>? Hide { get; set; }

    // One per descriptor material, in its order (empty: every material lit). Name is the descriptor material's name (the target of a
    // Tint or Emissive player colour); Shader one of ForgeCore's shader set, "lit" (the game's gear shader, the default) or "toon";
    // Params that shader's parameters by name (ForgeCore Visual/Data/shaders.json), each one to four numbers.
    public List<CharacterMaterial> Materials { get; set; } = new();

    // The author's size on top of the fit: worn on a body, the model is first scaled so its height (feet to head, CharacterRig.Stature)
    // is the body's, then by this. Any positive number; 1 is the body's height.
    public float Height { get; set; } = 1;

    // The card picture (a PNG, the editor's 3D view when it packed the skin): the skin page shows it without loading the model. Null:
    // the plugin photographs the model once it is loaded.
    public string? Picture { get; set; }
}

public sealed class CharacterModelFiles
{
    public string Descriptor { get; set; } = "";
    public string Mesh { get; set; } = "";
    public List<string> Textures { get; set; } = new();
}

public sealed class CharacterArms
{
    public CharacterModelFiles? Model { get; set; }
}

public sealed class CharacterSprings
{
    public List<SpringChain> Chains { get; set; } = new();
    // Shared by every chain: what they are kept out of.
    public List<SpringCapsule> Capsules { get; set; } = new();
    // The same as a surface measured on the skin: when set, the chains are kept out of it instead of the capsules.
    public List<SpringShell> Shell { get; set; } = new();
}

// ForgeCore ChainSpec's presets, the solver's starting point: long hair or a ponytail, short hair, a skirt, a tail or ribbon.
public enum SpringKind { LongHair, ShortHair, Skirt, Tail }

// Spring bones moved by ForgeCore's chain physics after the pose copy. Anchor is the model bone the strands hang from (the head, the
// hips); each strand its bones from the root down, at least two, each a descendant of Anchor; LinkSide links neighbouring strands
// node to node (a skirt), Loop the last to the first too. The author values override the preset's when set: weight 0.1 to 3, damping,
// stiffness, restore, style leash and inertia 0 to 1, gravity 0 to 3 times real gravity, radius 0 to MaxRadius metres, bend limit and
// azimuth limit 0 to 180 degrees, step 1/480 to 1/60 s, iterations 1 to 12, length passes 1 to 8.
public sealed class SpringChain
{
    public string Id { get; set; } = "";
    public SpringKind Kind { get; set; }
    public string Anchor { get; set; } = "";
    public List<List<string>> Strands { get; set; } = new();
    public bool LinkSide { get; set; }
    public bool Loop { get; set; }
    public float? Weight { get; set; }
    public float? Damping { get; set; }
    public float? Stiffness { get; set; }
    public float? Gravity { get; set; }
    public float? Radius { get; set; }
    /// <summary>Degrees one bone of a strand may swing from the one above it; 0 removes the limit. Null: the kind's preset.</summary>
    public float? AngleLimit { get; set; }
    /// <summary>How strongly a piece is pulled back toward its bound direction, 0 to 1.</summary>
    public float? Restore { get; set; }
    /// <summary>How far past its bound reach a node may drift before the leash pulls it back, as a share of that reach, 0 to 1.</summary>
    public float? StyleLeash { get; set; }
    /// <summary>How much of the body's move the chain does not ride along with, 0 to 1.</summary>
    public float? Inertia { get; set; }
    /// <summary>Degrees a piece may swing around its parent's axis from the bound radial plane (a skirt's panels); 0 removes the limit.</summary>
    public float? AzimuthLimit { get; set; }
    public bool? Collide { get; set; }
    public bool? CollideCloth { get; set; }
    public bool? SelfCollide { get; set; }
    /// <summary>The solver's sub-step in seconds, 1/480 to 1/60.</summary>
    public float? Step { get; set; }
    /// <summary>Constraint rounds a sub-step, 1 to 12.</summary>
    public int? Iterations { get; set; }
    /// <summary>Exact-length sweeps a sub-step, 1 to 8.</summary>
    public int? LengthPasses { get; set; }
    /// <summary>Where the cloth each strand bone carries lies (the editor measures it from the skin weights): bone name to points of four
    /// numbers each, t, x, y, z — t of the way from the bone to the next one in its strand (past the last, to its tip), then (x, y, z)
    /// off that point in the bone's own space. A set with fabric keeps those points out of the body's skin instead of its bones, which
    /// may lie inside the legs.</summary>
    public Dictionary<string, float[]>? Fabric { get; set; }

    // The ForgeCore spec: the preset with the author's values over it.
    public ChainSpec Spec()
    {
        var spec = Kind switch { SpringKind.ShortHair => ChainSpec.ShortHair(), SpringKind.Skirt => ChainSpec.Skirt(), SpringKind.Tail => ChainSpec.Tail(), _ => ChainSpec.LongHair() };
        if (Weight is { } w) spec.Weight = w;
        if (Damping is { } d) spec.Damping = d;
        if (Stiffness is { } s) spec.Stiffness = s;
        if (Gravity is { } g) spec.Gravity = g;
        if (Radius is { } r) spec.Radius = r;
        if (AngleLimit is { } a) spec.AngleLimit = a;
        if (Restore is { } re) spec.Restore = re;
        if (StyleLeash is { } sl) spec.StyleLeash = sl;
        if (Inertia is { } i) spec.Inertia = i;
        if (AzimuthLimit is { } az) spec.AzimuthLimit = az;
        if (Collide is { } co) spec.Collide = co;
        if (CollideCloth is { } cc) spec.CollideCloth = cc;
        if (SelfCollide is { } sc) spec.SelfCollide = sc;
        if (Step is { } st) spec.Step = st;
        if (Iterations is { } it) spec.Iterations = it;
        if (LengthPasses is { } lp) spec.LengthPasses = lp;
        return spec;
    }
}

// A capsule on the model bone of `joint` from A to B in that bone's own space (a sphere when A is B), radius 0.005 to MaxRadius metres.
public sealed class SpringCapsule
{
    public string Joint { get; set; } = "";
    public float[] A { get; set; } = { 0, 0, 0 };
    public float[] B { get; set; } = { 0, 0, 0 };
    public float Radius { get; set; } = 0.08f;
}

// One joint's skin as its surface: points with outward normals, six float32 per point (x, y, z, nx, ny, nz), base64, in the model
// bone of `joint`'s own space (the same frame in the game as in the editor: a model-wide mirror cancels out of bone-local frames), and
// optionally the triangles over those points, three uint32 point indices each, base64: the model's own surface, with no gap between
// its vertices. A chain node nearer than its own radius to the surface, behind its normal, is moved out along that normal.
public sealed class SpringShell
{
    public string Joint { get; set; } = "";
    public string Data { get; set; } = "";
    /// <summary>The surface's triangles over its points, three uint32 indices each, base64; null or empty: the points alone (a point cloud).</summary>
    public string? Tris { get; set; }
}

public sealed class CharacterMaterial
{
    public const string Lit = "lit", Toon = "toon";
    public string Name { get; set; } = "";
    public string Shader { get; set; } = Lit;
    public Dictionary<string, float[]>? Params { get; set; }
}

// The bone map against the canonical humanoid, and the model files against their descriptor. Pure: the plugin, the backend and the tests.
public static class CharacterRig
{
    // The one joint a character cannot do without: the pose copy's root, whose place and height the model takes from the body's hips.
    public static readonly string[] Required = { "hip" };

    // The model bone of every canonical joint, in ClipRig order (null: not mapped).
    public static string?[] Joints(CharacterSkin character)
    {
        var joints = new string?[ClipRig.Count];
        for (int j = 0; j < ClipRig.Count; j++)
            joints[j] = character.Bones.TryGetValue(ClipRig.Names[j], out var bone) && !string.IsNullOrWhiteSpace(bone) ? bone : null;
        return joints;
    }

    // Each mapped joint's rest position in the model's own space (zero for a joint not mapped or whose bone the skeleton lacks).
    public static Vector3[] Rest(CharacterSkin character, ImportedModelDescriptor model)
    {
        var rest = new Vector3[ClipRig.Count];
        if (model.Bones is not { } skeleton) return rest;
        var joints = Joints(character);
        for (int j = 0; j < ClipRig.Count; j++)
            if (joints[j] is { } bone && skeleton.FirstOrDefault(b => b.Name == bone) is { } b) rest[j] = new Vector3(b.Rest[12], b.Rest[13], b.Rest[14]);
        return rest;
    }

    // The pose copy's links and the first-person arms' bones for this character (ForgeCore HumanRig), from its map and its model's rest:
    // the one rule of what a joint without a bone does, for the plugin and the editor's preview alike. Links throws ArgumentException
    // without a hip; Arms is empty for a model without both upper arms (no first-person arms: the game's own stay).
    public static PoseLink[] Links(CharacterSkin character, ImportedModelDescriptor model) => HumanRig.Links(Joints(character), Rest(character, model));

    public static WearBone[] Arms(CharacterSkin character, ImportedModelDescriptor model) => HumanRig.ArmWear(Joints(character), Rest(character, model));

    // What is wrong with the character against its model's skeleton, one sentence each; empty when it fits. Only what cannot work: no
    // bone for the hip, a mapped bone or a spring bone the skeleton lacks, a capsule on an unmapped joint, materials that do not match
    // the descriptor's, a height that is no size. A joint left out, joints on one point or sides unlike each other are not wrong.
    public static string Check(CharacterSkin character, ImportedModelDescriptor model)
    {
        var wrong = "";
        if (model.Bones is not { } skeleton) return "the model has no skeleton (descriptor format 2).";
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < skeleton.Count; i++) index[skeleton[i].Name] = i;
        var joints = Joints(character);
        var present = new bool[ClipRig.Count];
        if (joints[0] == null) wrong += " hip: no bone. The model follows the body by its hips, so one bone must stand for them.";
        for (int j = 0; j < ClipRig.Count; j++)
        {
            if (joints[j] is not { } bone) continue;
            if (!index.ContainsKey(bone)) { wrong += $" {ClipRig.Names[j]}: the skeleton has no bone {bone}."; continue; }
            present[j] = true;
        }
        foreach (var chain in character.Springs.Chains)
            foreach (var bone in chain.Strands.SelectMany(s => s).Append(chain.Anchor))
                if (!index.ContainsKey(bone)) wrong += $" spring {chain.Id}: the skeleton has no bone {bone}.";
        foreach (var capsule in character.Springs.Capsules)
            if (Array.IndexOf(ClipRig.Names, capsule.Joint) is var j && (j < 0 || !present[j])) wrong += $" capsule on {capsule.Joint}: that joint is not mapped.";
        foreach (var shell in character.Springs.Shell)
        {
            if (Array.IndexOf(ClipRig.Names, shell.Joint) is var j && (j < 0 || !present[j])) wrong += $" shell on {shell.Joint}: that joint is not mapped.";
            if (!ShellPoints(shell.Data, out var points)) wrong += $" shell on {shell.Joint}: its points are not base64 of x, y, z, nx, ny, nz float32.";
            else if (shell.Tris is { Length: > 0 } tris && !ShellTriangles(tris, points.Length / 6, out _)) wrong += $" shell on {shell.Joint}: its triangles are not base64 of three point indices each (uint32).";
        }
        if (character.Materials.Count > 0 && character.Materials.Count != model.Materials.Count)
            wrong += $" {character.Materials.Count} materials for the model's {model.Materials.Count}.";
        foreach (var i in character.Hide ?? new()) if (i < 0 || i >= model.Materials.Count) wrong += $" hide: the model has no material {i}.";
        if (!float.IsFinite(character.Height) || character.Height <= 0) wrong += $" height {character.Height} is not a positive number.";
        return wrong.Trim();
    }

    // The body joints whose surfaces drive the chains hanging against them (ChainShellData.Drives): the legs a skirt or a twin tail
    // swings with. The editor's preview and the game both take it from here.
    public static bool DrivesChains(string? joint) => joint is "leftUpperLeg" or "leftLowerLeg" or "rightUpperLeg" or "rightLowerLeg";

    // A shell's points as doubles, six per point: base64 of x, y, z, nx, ny, nz float32 little-endian. False when the data is not.
    public static bool ShellPoints(string data, out double[] points)
    {
        points = Array.Empty<double>();
        if (string.IsNullOrWhiteSpace(data)) return false;
        try
        {
            var bytes = Convert.FromBase64String(data);
            if (bytes.Length == 0 || bytes.Length % 24 != 0) return false;
            var values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes);
            points = new double[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                if (!float.IsFinite(values[i])) return false;
                points[i] = values[i];
            }
            return true;
        }
        catch (FormatException) { return false; }
    }

    // A shell's triangles as ints, three per triangle: base64 of uint32 point indices. False when the data is not, or an index names a
    // point the shell does not have.
    public static bool ShellTriangles(string data, int points, out int[] tris)
    {
        tris = Array.Empty<int>();
        if (string.IsNullOrWhiteSpace(data)) return false;
        try
        {
            var bytes = Convert.FromBase64String(data);
            if (bytes.Length == 0 || bytes.Length % 12 != 0) return false;
            var values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(bytes);
            var read = new int[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] >= (uint)points) return false;
                read[i] = (int)values[i];
            }
            tris = read;
            return true;
        }
        catch (FormatException) { return false; }
    }

    // Null when the mesh and textures are the ones the descriptor names and ForgeCore can build them (its one file check,
    // ImportedModelDescriptor.CheckFiles, which the game runs again when it builds the model); else which is not. The editor's export
    // read-back and the plugin's load both ask this, so a pack the export accepts is one the game builds.
    public static string? Matches(ImportedModelDescriptor model, byte[] mesh, IReadOnlyList<byte[]> textures)
    {
        if (Sha256(mesh) != model.MeshSha256) return "the mesh is not the one the descriptor names";
        if (textures.Count != model.Textures.Count) return $"{textures.Count} textures for the descriptor's {model.Textures.Count}";
        for (int i = 0; i < textures.Count; i++)
            if (Sha256(textures[i]) != model.Textures[i].Sha256) return $"texture {i} is not the one the descriptor names";
        return model.CheckFiles(mesh, textures, out var failure) ? null : failure.ToString();
    }

    // The model's height at rest, feet to head: the head joint's height over the mean of the mapped feet's, or over the model's own
    // floor (0) with no foot mapped, in the model's own units. 0 or less when the head is not mapped or not above them: the height
    // cannot be measured. Bones, not bounds: hair, hats and raised arms do not count.
    public static float Stature(CharacterSkin character, ImportedModelDescriptor model)
    {
        if (model.Bones is not { } skeleton) return 0;
        float? Y(string joint) => character.Bones.TryGetValue(joint, out var bone) && skeleton.FirstOrDefault(b => b.Name == bone) is { } b ? b.Rest[13] : null;
        if (Y("head") is not { } head) return 0;
        var feet = new[] { Y("leftFoot"), Y("rightFoot") }.OfType<float>().ToArray();
        return head - (feet.Length > 0 ? feet.Average() : 0);
    }

    static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
