// The imported character: the file converted to the character format's model by the site's own converter (import-worker.ts), read
// into three.js for the view, its bones matched to the canonical humanoid (from the file's VRM data, else with the site's own matcher),
// the match held to ForgeCore's rig check (character.check), and its spring bones moved by ForgeCore's chain physics (springs.simulate)
// for the preview motions. The skin is a character document autosaved like a weapon skin. Nothing here computes a rig rule or a chain of its own.
import { useEffect, useSyncExternalStore } from 'react';
import {
  AmbientLight,
  Bone,
  Box3,
  DirectionalLight,
  PerspectiveCamera,
  Scene,
  BufferGeometry,
  Float32BufferAttribute,
  Group as GroupObject,
  Matrix3,
  Matrix4,
  MeshStandardMaterial,
  Object3D,
  Quaternion,
  Skeleton,
  SkinnedMesh as SkinnedMeshObject,
  Uint16BufferAttribute,
  Vector3,
  type Group,
  type Material,
  type Mesh,
  ACESFilmicToneMapping,
  SRGBColorSpace,
  WebGLRenderer,
  type SkinnedMesh,
  type Texture,
} from 'three';
import { GLTFLoader } from 'three/examples/jsm/loaders/GLTFLoader.js';
import { matchHumanBones, matchHumanFingers } from '../../../../../Web/src/forge/animation-bone-match';
import { aimFollow, upgradeVrm, vrmJoints, type CharacterImport } from '../../../../../Web/src/forge/character-import';
import { characterBlend, characterBlendCutoff } from '../../../../../Web/src/forge/imported-model-glb';
import { readImportedModelDescriptor } from '../../../../../Web/src/forge/imported-model-format';
import { buildImportedScene } from '../../../../../Web/src/shared/render/imported-model-build';
import { HumanFingerJoints, HumanJoints } from '../../../../../Web/src/generated/rig/animation-reference.js';
import { friendly } from '../errors';
import { t } from '../i18n';
import { call, fileUrl, type Methods, type ProjectEntry, type SkinDocument } from '../protocol';
import { autosaveRoot, useStore } from '../store';
import { resolveMorph } from '../dance/face';
import { motionMatrix, MOTION_SECONDS, PREVIEW_FPS, SETTLE_SECONDS } from '../viewport/motion';
import type { ImportReply } from './import-worker';
import { COLLIDER_JOINTS, COLLIDER_RADIUS, matchOf, useCharacter, type BoneRow, type Capsule, type CharacterState, type CharmMotion, type ColliderJoint, type RigCheck, type ShieldShell, type Spring, type SpringKind } from './state';

/** Every canonical joint, body then fingers (the site's rig, the same table as ForgeCore's ClipRig). */
export const BODY_JOINTS = HumanJoints;
export const FINGER_JOINTS = HumanFingerJoints;

const CANONICAL_HIP = 1.04;

/** Suspends a long measurement for a frame, so the window stays live (a click, a scroll, the progress text) while it runs. */
const breathe = () => new Promise<void>((resolve) => setTimeout(resolve, 0));
/** Which measurement pass is current, one counter per kind: a newer pass of the same kind makes the older one stop before it
 * commits; a model being replaced stops every pass. */
const fitRun = { colliders: 0, shell: 0, springs: 0 };
export function cancelFits() {
  fitRun.colliders++;
  fitRun.shell++;
  fitRun.springs++;
}
/** How long a measurement runs before it lets a frame through. */
const SLICE_MS = 12;
/** Puts every bone back at its bind pose and makes the skinning matrices follow before a measured chunk reads them: a clip
 * playing while the measurement runs (a frame between two chunks re-poses the bones) would otherwise leave parts of the
 * surface measured in one pose and parts in another, which draws the collider offset from the model. */
function restNow() {
  restPose();
  scene?.traverse((o) => {
    const mesh = o as SkinnedMesh;
    if (mesh.isSkinnedMesh && mesh.skeleton) mesh.skeleton.update();
  });
}

let scene: Group | null = null;
const byName = new Map<string, Bone>();
const bindLocal = new Map<Bone, Quaternion>();
const bindPosition = new Map<Bone, Vector3>();
/** Canonical joint to bone name as the loaded file's own humanoid (VRM) says; empty for a file without one. */
let fileJoints: Record<string, string> = {};
let sceneVersion = 0;
const listeners = new Set<() => void>();

/** The loaded model (null before an import), and a number that changes with each import, for useSyncExternalStore. */
export const modelScene = () => scene;
export const modelVersion = () => sceneVersion;
export function onModel(listener: () => void) {
  listeners.add(listener);
  return () => void listeners.delete(listener);
}
export const boneOf = (name: string | undefined | null) => (name ? byName.get(name) : undefined);

// The model's own expressions (glTF blend shapes, which an MMD file carries as まばたき, あ…): the dance preview drives them
// by name the way the game does. The source file already ships them, so the preview costs no extra memory — three.js keeps
// them as morph targets whether or not they are ever posed.
const narrow = (name: string): string => name.replace(/[｡-ﾟ]+/g, (run) => run.normalize('NFKC'));
let morphShapes: { lookup: Map<string, number>; influences: number[] }[] = [];
let morphScene = -1;
let morphCleared = true;

/** Collects each mesh's blend shapes by widened name; keyed by scene so a new model re-collects. */
function collectMorphShapes(): void {
  morphScene = sceneVersion;
  morphShapes = [];
  morphCleared = true;
  scene?.traverse((o) => {
    const mesh = o as Mesh;
    if (!mesh.isMesh || !mesh.morphTargetDictionary || !mesh.morphTargetInfluences?.length) return;
    const lookup = new Map<string, number>();
    for (const [name, index] of Object.entries(mesh.morphTargetDictionary)) lookup.set(narrow(name), index);
    morphShapes.push({ lookup, influences: mesh.morphTargetInfluences });
  });
}

/** The expressions the open model has, as the names a dance's curves are looked up against. */
export function modelMorphNames(): string[] {
  if (morphScene !== sceneVersion) collectMorphShapes();
  return [...new Set(morphShapes.flatMap((m) => [...m.lookup.keys()]))];
}

/**
 * Poses the open model's expressions: curve name → weight 0..1, resolved to the model's shape names the way the game does
 * (`resolveMorph`, aliases included). `null` puts every shape back to rest — the character tab must not keep a frozen face.
 */
export function setMorphWeights(weights: ReadonlyMap<string, number> | null): void {
  if (morphScene !== sceneVersion) collectMorphShapes();
  if (!morphShapes.length || (!weights && morphCleared)) return;
  morphCleared = !weights;
  for (const shape of morphShapes) {
    shape.influences.fill(0);
    if (!weights) continue;
    for (const [curve, weight] of weights) {
      const name = resolveMorph(curve, (n) => shape.lookup.has(n));
      if (name !== null) shape.influences[shape.lookup.get(name)!] = weight;
    }
  }
}

const toast = (kind: 'ok' | 'error' | 'info', text: string) => useStore.getState().toast(kind, text);

/** Reads the model's copy into three.js for the view (turned round when the file faces the other way); its bones, null with a toast
 * when it has no skin or two bones share a name. */
/** Which load is the latest: a file still being read when another open starts is dropped instead of replacing the newer scene. */
let loadRun = 0;
async function loadScene(source: string, turned: boolean): Promise<{ rows: BoneRow[]; vertices: number } | null> {
  cancelFits();
  const gen = ++loadRun;
  const gltf = await new GLTFLoader().loadAsync(fileUrl(source));
  if (gen !== loadRun) return null;
  if (!hasSkin(gltf.scene)) return null;
  // A see-through material is drawn as the game draws it (characterBlend), not blended: blended layers in one place draw over each other.
  const json = gltf.parser.json as { materials?: { alphaMode?: string }[] };
  gltf.parser.associations.forEach((a, o) => {
    const material = o as MeshStandardMaterial;
    if (!material.isMaterial) return;
    // 与转换器同一条规则：没有自发光贴图的纯白自发光是 Blender「Emission Color 白、强度 0」的导出痕迹（MMD 皮肤的 face/body
    // 就是这样），它只会把纹理洗白——按黑处理，预览才和游戏一致。
    if (!material.emissiveMap && material.map && material.emissive.r === 1 && material.emissive.g === 1 && material.emissive.b === 1)
      material.emissive.setRGB(0, 0, 0);
    if (!a || !('materials' in a) || a.materials === undefined || json.materials?.[a.materials]?.alphaMode !== 'BLEND') return;
    const how = characterBlend(!!material.map, material.opacity);
    material.transparent = false;
    material.depthWrite = true;
    material.alphaTest = how === 'mask' ? characterBlendCutoff : 0;
    if (how === 'opaque') material.opacity = 1;
    if (how === 'drop') material.visible = false;
  });
  // A helper bone the file aims by a constraint rides the bone it follows, at its rest pose, as the converter packs it.
  const nodeObject = new Map<number, Object3D>();
  gltf.parser.associations.forEach((a, o) => void (a && 'nodes' in a && a.nodes !== undefined && o instanceof Object3D && nodeObject.set(a.nodes, o)));
  gltf.scene.updateMatrixWorld(true);
  aimFollow(gltf.parser.json).forEach((under, bone) => {
    const o = nodeObject.get(bone),
      to = nodeObject.get(under);
    if (o && to && !o.getObjectById(to.id)) to.attach(o);
  });
  const vrm = upgradeVrm(gltf.parser.json);
  const names = (gltf.parser.json as { nodes?: { name?: string }[] }).nodes ?? [];
  fileJoints = Object.fromEntries(vrm ? vrmJoints(vrm).map(([joint, node]) => [joint, names[node]?.name ?? '']) : []);
  return showScene(gltf.scene, turned);
}

/** The model as the pack holds it (a work moved from a character pack, which has no source file): built straight from its
 * converted files by the site's one way of drawing an imported model. It already faces +Z, with every helper bone where the
 * converter put it. */
async function loadPacked(files: PackedFiles): Promise<{ rows: BoneRow[]; vertices: number } | null> {
  cancelFits();
  const gen = ++loadRun;
  const built = await buildPacked(files);
  if (gen !== loadRun) return null;
  fileJoints = {};
  return hasSkin(built) ? showScene(built, false) : null;
}

type PackedFiles = { descriptor: string; mesh: string; textures: string[] };

/** A converted model built from its files by the site's one way of drawing an imported model. */
async function buildPacked(files: PackedFiles): Promise<Object3D> {
  const bytes = async (path: string) => new Uint8Array(await (await fetch(fileUrl(path))).arrayBuffer());
  const descriptor = readImportedModelDescriptor(JSON.parse(new TextDecoder().decode(await bytes(files.descriptor))));
  return buildImportedScene({ name: 'character', descriptor, mesh: await bytes(files.mesh), textures: await Promise.all(files.textures.map(bytes)) });
}

/** Whether the model has a skinned mesh; a toast when not. */
function hasSkin(model: Object3D): boolean {
  let skinned = false;
  model.traverse((o) => void ((o as SkinnedMesh).isSkinnedMesh && (skinned = true)));
  if (!skinned) toast('error', t('character.notSkinned'));
  return skinned;
}

/** Makes `model` the view's character: its bones listed and kept by name (null with a toast when two share one). */
function showScene(model: Object3D, turned: boolean): { rows: BoneRow[]; vertices: number } | null {
  const meshes: SkinnedMesh[] = [];
  model.traverse((o) => void ((o as SkinnedMesh).isSkinnedMesh && meshes.push(o as SkinnedMesh)));
  // Bones in the order a walk from the scene meets them: parents first, as the format lists them.
  const rows: BoneRow[] = [];
  const index = new Map<Object3D, number>();
  byName.clear();
  bindLocal.clear();
  bindPosition.clear();
  model.traverse((o) => {
    if (!(o as Bone).isBone) return;
    // three.js drops '.' and other characters from node names; the skeleton's bones keep the file's own names.
    if (typeof o.userData.name === 'string') o.name = o.userData.name;
    const parent = o.parent && index.has(o.parent) ? index.get(o.parent)! : -1;
    index.set(o, rows.length);
    rows.push({ name: o.name, parent, depth: parent < 0 ? 0 : rows[parent].depth + 1 });
    byName.set(o.name, o as Bone);
    bindLocal.set(o as Bone, o.quaternion.clone());
    bindPosition.set(o as Bone, o.position.clone());
  });
  if (byName.size !== rows.length) return (toast('error', t('character.sameBoneNames')), null);
  model.rotation.y = turned ? Math.PI : 0;
  // The model's root faces +Z as the packed model does (a turned file turned inside it), so the pose copy reads it as the game does.
  const root = new GroupObject();
  root.add(model);
  root.updateMatrixWorld(true);
  // A skinned mesh is culled against the sphere of its bind pose, and a dance carries the head or a leg far from where
  // that sphere sits: up close the stale sphere leaves the frustum and the whole piece drops out of the picture, which
  // read as parts of the model vanishing. The stage shows one character and nothing else, so the test buys nothing.
  meshes.forEach((m) => (m.frustumCulled = false));
  scene = root;
  sceneVersion++;
  listeners.forEach((l) => l());
  return { rows, vertices: meshes.reduce((n, m) => n + m.geometry.getAttribute('position').count, 0) };
}

/** The file through the site's converter in a worker: the model files and what the file says about bones, springs and colliders. */
function convert(bytes: Uint8Array): Promise<CharacterImport> {
  return new Promise((resolve, reject) => {
    const worker = new Worker(new URL('./import-worker.ts', import.meta.url), { type: 'module', name: 'character-import' });
    worker.onmessage = ({ data }: MessageEvent<ImportReply>) => {
      worker.terminate();
      if ('result' in data) resolve(data.result);
      else reject(Object.assign(new Error(data.message), { code: data.code, params: data.params }));
    };
    worker.onerror = (e) => (worker.terminate(), reject(new Error(e.message || 'character import failed')));
    worker.postMessage(bytes);
  });
}

/** A refusal in the author's words: the converter's reason when it has one here, else its code. */
function refusal(e: unknown): string {
  const code = (e as { code?: string }).code;
  const known = code && t(`character.refused.${code}`, { ...(e as { params?: Record<string, unknown> }).params, defaultValue: '' });
  return known || t('character.unreadable', { v0: code && code !== 'unreadable' ? code : friendly(e) });
}

/** Where character skins live: one autosaved project each, its converted model in the folder beside it. Empty before the store
 * has read the data folder: a `\autosave\...` built then is drive-relative and lands on the drive's root. */
export const characterDir = () => (useStore.getState().dataDir ? `${autosaveRoot()}\\character` : '');
const projectOf = (id: string) => (characterDir() ? `${characterDir()}\\${id}.forgeskinproj` : '');
const encode = (text: string) => new TextEncoder().encode(text);

/** Imports a .glb or .vrm as a new character skin: converted once (refused by name when the format cannot hold it), its files
 * written into the skin's folder, the view loaded, and bones, springs and colliders filled from the file's VRM data where it has some. */
export async function importModel(path: string) {
  // A .gltf names its .bin and textures beside it, which the converter does not take; anything else is not a model at all.
  if (/\.gltf$/i.test(path)) return toast('error', t('errors.gltfUnsupported'));
  if (!/\.(glb|vrm)$/i.test(path)) return toast('error', t('errors.unsupportedFile'));
  await saveCharacter();
  useCharacter.setState({ loading: true });
  try {
    const bytes = new Uint8Array(await (await fetch(fileUrl(path))).arrayBuffer());
    let r: CharacterImport;
    try {
      r = await convert(bytes);
    } catch (e) {
      return toast('error', refusal(e));
    }
    if (r.model.descriptor.format === 1) return toast('error', t('character.notSkinned'));
    useCharacter.setState({ lastEdited: new Date().toISOString() });
    adopted = false;
    const skinId = `char-${crypto.randomUUID().replace(/-/g, '').slice(0, 12)}`;
    const dir = `${characterDir()}\\${skinId}.character`;
    const files = {
      descriptor: `${dir}\\model.json`,
      mesh: `${dir}\\model.forgemesh`,
      textures: r.model.textures.map((_, i) => `${dir}\\t${i}.png`),
      source: `${dir}\\source.${/\.vrm$/i.test(path) ? 'vrm' : 'glb'}`,
    };
    await window.forge.writeFile(files.descriptor, encode(JSON.stringify(r.model.descriptor)));
    await window.forge.writeFile(files.mesh, r.model.mesh);
    for (const [i, png] of r.model.textures.entries()) await window.forge.writeFile(files.textures[i], png);
    await window.forge.writeFile(files.source, bytes);
    const loaded = await loadScene(files.source, r.turned);
    if (!loaded) return;
    const materials = r.model.descriptor.materials.map((m) => m.name);
    useCharacter.setState({
      skinId,
      name: path
        .split(/[\\/]/)
        .pop()!
        .replace(/\.(glb|vrm)$/i, ''),
      files,
      turned: r.turned,
      capsules: r.capsules.length ? r.capsules : null,
      shell: null,
      mtoon: r.mtoon,
      credit: r.credit,
      model: files.source,
      bones: loaded.rows,
      materials,
      vertices: r.model.stats.vertices,
      auto: autoMatch(),
      picked: {},
      joint: null,
      springs: r.springs.map((x) => ({
        id: x.id,
        kind: x.kind,
        strands: x.strands,
        anchor: x.anchor,
        linkSide: x.linkSide,
        loop: x.loop,
        stiffness: x.stiffness,
        damping: x.damping,
        gravity: x.gravity,
        radius: x.radius,
      })),
      spring: null,
      check: null,
      shaders: Object.fromEntries(materials.map((m) => [m, 'lit' as const])),
      hide: [],
      height: 1,
      view: 'body',
      step: 'bones',
      clip: null,
      playing: false,
    });
    toast('ok', t(r.vrm ? 'character.importedVrm' : 'character.imported', { v0: loaded.rows.length, v1: r.springs.length, v2: r.capsules.length }));
    if (r.model.descriptor.format === 3) toast('ok', t('character.expressions', { v0: r.model.descriptor.morphs?.length ?? 0 }));
    await checkMatch();
    await fitColliders();
    await fitShell();
    // A file without spring data (a converted MMD model, a plain glTF) gets its hair and skirt as sets at once, no picking needed.
    if (!r.springs.length) await autoAddSprings(false);
    await saveCharacter();
    void stageIcon();
  } catch (e) {
    toast('error', t('character.unreadable', { v0: friendly(e) }));
  } finally {
    useCharacter.setState({ loading: false });
  }
}

/** Asks for a model file and imports it; false when the player cancelled. */
export async function chooseModel(): Promise<boolean> {
  const [file] = await window.forge.openDialog('file', [{ name: t('character.glbFiles'), extensions: ['glb', 'vrm'] }], 'character');
  if (file) await importModel(file);
  return !!file;
}

/** Whether a .glb/.gltf is a character: it carries VRM data, or the site's matcher finds hips on one of its root bones (the one joint
 * a character needs; a creature without fingers or an arm is one too). Null when the file cannot be read. */
export async function isCharacterModel(path: string): Promise<boolean | null> {
  try {
    const gltf = await new GLTFLoader().loadAsync(fileUrl(path));
    if ((gltf.parser.json.extensionsUsed as string[] | undefined)?.some((name) => /^VRM/.test(name))) return true;
    let humanoid = false;
    gltf.scene.traverse((o) => {
      if (!humanoid && (o as Bone).isBone && !(o.parent as Bone | null)?.isBone) humanoid = matchHumanBones<Object3D>(o).bones.has('hip');
    });
    return humanoid;
  } catch {
    return null;
  }
}

// ---- the character document: built, autosaved and reopened like a weapon skin's project ----

/** What only the editor reads back (the pack drops it): the author's match and spring sets as the panels hold them. */
interface Saved {
  source: string;
  turned: boolean;
  auto: Record<string, string>;
  picked: Record<string, string | null>;
  springs: Spring[];
  colliders: ReturnType<typeof useCharacter.getState>['colliders'];
  capsules: Capsule[] | null;
  capsulesVersion?: number;
  shell: ShieldShell | null;
  mtoon: Record<string, unknown>;
  vertices: number;
}

/** The colliders as the document holds them: a file's own capsules, else the seven automatic per-joint ones switched on, each on
 * its joint's bone. */
function capsuleEntries(): Capsule[] {
  const s = useCharacter.getState();
  const match = oneJointPerBone(matchOf(s));
  if (s.capsules) return s.capsules.filter((c) => match[c.joint]);
  scene?.updateMatrixWorld(true);
  // A bone's own frame is the same in the game as here, so a collider goes in as measured — the old X negation had every capsule
  // mirrored inside its bone in the game while the editor preview undid it.
  const local = (bone: Bone, w: Vector3): [number, number, number] => {
    const p = bone.worldToLocal(w.clone());
    return [p.x, p.y, p.z];
  };
  const entry = (joint: string, a: Vector3, b: Vector3, radius: number): Capsule[] => {
    const bone = byName.get(match[joint] ?? '');
    return bone ? [{ joint, a: local(bone, a), b: local(bone, b), radius: Math.min(0.5, Math.max(0.005, radius)) }] : [];
  };
  return colliders().flatMap((c) => entry(c.joint, c.a, c.b, c.radius));
}

/** The body's surface as the document holds it (each joint's vertices and the triangles over them): no shell when the file set its
 * own capsules (they are authored) or the switch is off. */
function shellEntries(): { joint: string; data: string; tris?: string }[] {
  const s = useCharacter.getState();
  if (s.capsules || !s.shell?.on) return [];
  const match = oneJointPerBone(matchOf(s));
  return s.shell.joints.filter((x) => match[x.joint]);
}

/** The editor's own copy of the shell, its joints left out when the pack's `springs.shell` already holds exactly them (see characterDoc). */
function savedShell(s: CharacterState, entries: { joint: string; data: string }[]): ShieldShell | null {
  const shell = s.shell;
  if (!shell) return null;
  const same = shell.on && !s.capsules && shell.joints.length === entries.length && shell.joints.every((j, i) => j.joint === entries[i]!.joint && j.data === entries[i]!.data);
  return same ? { ...shell, joints: [] } : shell;
}

/** The character skin as a document (Core/Character.cs): what the pack builds from, and what the autosave holds. */
export function characterDoc(): SkinDocument | null {
  const s = useCharacter.getState();
  if (!s.skinId || !s.files) return null;
  const saved: Saved = {
    source: s.files.source.split(/[\\/]/).pop()!,
    turned: s.turned,
    auto: s.auto,
    picked: s.picked,
    springs: s.springs,
    colliders: s.colliders,
    capsules: s.capsules,
    // The pack's own `springs.shell` already carries these exact points and triangles: saving them twice made every project
    // file about twice its size. A shell that differs (or is off, so the pack has none) is kept whole.
    shell: savedShell(s, shellEntries()),
    mtoon: s.mtoon,
    vertices: s.vertices,
  };
  return {
    schema: 1,
    id: s.skinId,
    name: s.name,
    author: '',
    version: '1.0.0',
    target: { weapon: 'character' },
    materials: [],
    attachments: [],
    parts: [],
    sounds: [],
    character: {
      model: { descriptor: s.files.descriptor, mesh: s.files.mesh, textures: s.files.textures },
      bones: oneJointPerBone(matchOf(s)),
      springs: { chains: s.springs.map(springEntry), capsules: capsuleEntries(), shell: shellEntries() },
      materials: s.materials.map((name) => ({ name, shader: 'lit' })),
      ...(s.hide.length ? { hide: s.hide } : {}),
      height: s.height,
    },
    editor: { character: saved, ...(s.credit ? { credit: s.credit } : {}), ...(s.lastEdited ? { editedAt: s.lastEdited } : {}), ...(adopted ? { adopted } : {}) } as SkinDocument['editor'],
  };
}

/** The skin's project file (its autosave). */
export const characterProject = () => {
  const id = useCharacter.getState().skinId;
  return id ? projectOf(id) : null;
};

let saving: Promise<void> | null = null;
let saveAgain = false;
/** Writes the autosave now; a failure goes to the log and the top bar's save notice like a weapon skin's. One save runs at a
 * time: a request made while one writes runs after it, saving the document as it is then (the newest state wins), so two
 * saves never land out of order and the file always ends up holding the latest edit. The returned promise covers those queued
 * passes too, so a caller that awaits it (switching projects) is not left with its own state half-written. */
export function saveCharacter(): Promise<void> {
  clearTimeout(saveTimer);
  if (saving) {
    saveAgain = true;
    return saving;
  }
  // The promise is put in `saving` before the work starts. Started as one async call instead, a pass that had nothing to write
  // (the first call of an open, with no skin yet) finished synchronously, cleared `saving` and only then got it assigned — the
  // resolved promise stayed there for good and every later save was taken for one already running and quietly skipped.
  let done: () => void = () => undefined;
  const run = new Promise<void>((resolve) => (done = resolve));
  saving = run;
  void (async () => {
    try {
      do {
        saveAgain = false;
        const doc = characterDoc();
        const path = characterProject();
        if (!doc || !path) return;
        try {
          await call('project.save', { path, project: doc });
          useStore.setState({ autosaveError: null });
          if (!useStore.getState().characters.some((k) => k.path === path && k.name === doc.name)) void refreshCharacters();
        } catch (e) {
          window.forge.appLog('saveCharacter', `failed ${path}: ${friendly(e)}`);
          useStore.getState().log(`project.save ${path}: ${friendly(e)}`);
          useStore.setState({ autosaveError: friendly(e, t('store.k28')) });
        }
      } while (saveAgain);
    } catch (e) {
      window.forge.appLog('saveCharacter', `failed: ${friendly(e)}`);
    } finally {
      saving = null;
      done();
    }
  })();
  return run;
}

let saveTimer: ReturnType<typeof setTimeout> | undefined;
/** Whether the open character skin was made from someone else's pack (editor.adopted), kept through its saves. */
let adopted = false;
// Every change the author makes autosaves after a pause, as a weapon skin does.
useCharacter.subscribe((s, before) => {
  if (!s.skinId || s.loading) return;
  if (
    s.name === before.name &&
    s.auto === before.auto &&
    s.picked === before.picked &&
    s.springs === before.springs &&
    s.colliders === before.colliders &&
    s.capsules === before.capsules &&
    s.shell === before.shell &&
    s.hide === before.hide &&
    s.height === before.height
  )
    return;
  useCharacter.setState({ lastEdited: new Date().toISOString() });
  clearTimeout(saveTimer);
  saveTimer = setTimeout(() => void saveCharacter(), 1500);
});

/** The character skins (their autosaves), newest first, into the store's work list. */
export async function refreshCharacters(): Promise<ProjectEntry[]> {
  if (!characterDir()) return [];
  const characters: ProjectEntry[] = await call('project.list', { dir: characterDir() }).catch(() => []);
  useStore.setState({ characters });
  return characters;
}

/** Opens the character skin edited last (its autosave), once, when the tab first shows with nothing imported. */
let reopened = false;
export async function reopenCharacter() {
  if (reopened || useCharacter.getState().skinId) return;
  reopened = true;
  // The stage shows before the store has read the data folder, and a path built from a missing one (`\autosave\...`) is
  // drive-relative: the editor opened a stray work at D:\autosave instead of the author's last one and looked broken.
  try {
    await useStore.getState().init();
  } catch (e) {
    window.forge.appLog('reopenCharacter', `init failed: ${friendly(e)}`);
  }
  const [last] = await refreshCharacters();
  if (last) await openCharacter(last.path);
}

/** Moves a character skin and its converted model to the Recycle Bin and takes it out of the chosen profile, as a weapon skin's
 * delete does; deleting the open one opens the next, else none. */
export async function deleteCharacter(path: string) {
  const open = characterProject() === path;
  if (open) clearTimeout(saveTimer);
  const s = useStore.getState();
  const entry = s.characters.find((k) => k.path === path);
  const id = entry?.id;
  const profile = s.profiles.find((p) => p.path === s.prefs.profilePath);
  try {
    await call('project.delete', { path });
    // An adopted work shares its id with the pack it was made from, which stays installed.
    if (profile && id && !entry?.adopted && s.installed.some((p) => p.id === id && p.source !== 'mod')) {
      await call('pack.remove', { profile: profile.path, skinId: id });
      void s.refreshInstalled();
    }
  } catch (e) {
    return useStore.getState().fail(e, t('store.skinDeleteFailed'));
  }
  const [next] = await refreshCharacters();
  if (!open) return;
  useCharacter.setState({ skinId: null, name: '', files: null, model: null, bones: [], materials: [], check: null, step: 'model' });
  if (next) await openCharacter(next.path);
}

/** Opens the character skin saved at `path`, the one open now saved first. Two opens run one at a time: a click on another work
 * while this one is still measuring starts over from that work instead of leaving two scenes to write the same state. */
let openRun = 0;
export async function openCharacter(path: string) {
  if (characterProject() === path) return;
  const me = ++openRun;
  await saveCharacter();
  if (me !== openRun) return;
  useCharacter.setState({ loading: true });
  try {
    const doc = await call('project.load', { path });
    if (me !== openRun) return;
    const c = doc.character;
    if (!c?.model?.descriptor || !c.model.mesh) {
      window.forge.appLog('openCharacter', `${path}: the project names no model`);
      useStore.getState().log(`openCharacter ${path}: the project names no model`);
      return;
    }
    // A work moved from a character pack has no notes of the editor's yet: the pack's match and springs are its own.
    const saved: Saved = (doc.editor as { character?: Saved } | null)?.character ?? {
      source: '',
      turned: false,
      auto: Object.fromEntries(Object.entries(c.bones ?? {}).filter((e): e is [string, string] => !!e[1])),
      picked: {},
      springs: (c.springs?.chains ?? []).flatMap((x) => (x ? [{ ...x, anchor: x.anchor ?? undefined } as Spring] : [])),
      colliders: Object.fromEntries(COLLIDER_JOINTS.map((j) => [j, { on: true, scale: 1 }])) as Saved['colliders'],
      capsules: (c.springs?.capsules ?? []) as Capsule[],
      // A pack carries the shell itself: the notes start from none and the open measures one if the version is old.
      shell: null,
      mtoon: {},
      vertices: 0,
    };
    // A shell saved with its joints left out (characterDoc keeps one copy) comes back from the pack's own copy.
    const packedShell = (c.springs?.shell ?? []).flatMap((x) => (x?.joint && x.data ? [{ joint: x.joint, data: x.data, tris: x.tris ?? undefined }] : []));
    if (saved.shell && !saved.shell.joints.length && packedShell.length) saved.shell = { ...saved.shell, joints: packedShell };
    // A work saved by an older auto-grouping has its skirt as several sets: put the ring back together before it is shown (and saved
    // again as one set), or the panels cease to collide with each other and a leg passes between them.
    const joined = mergeSkirtSets(saved.springs);
    if (joined) saved.springs = joined;
    const files = {
      descriptor: c.model.descriptor,
      mesh: c.model.mesh,
      textures: (c.model.textures ?? []).filter((x): x is string => !!x),
      source: saved.source ? `${c.model.descriptor.replace(/[\\/][^\\/]*$/, '')}\\${saved.source}` : '',
    };
    const loaded = files.source ? await loadScene(files.source, saved.turned) : await loadPacked(files);
    if (!loaded || me !== openRun) {
      if (!loaded && me === openRun) {
        window.forge.appLog('openCharacter', `${path}: the model file did not load (${files.source || files.mesh})`);
        useStore.getState().log(`openCharacter ${path}: the model file did not load (${files.source || files.mesh})`);
      }
      return;
    }
    adopted = (doc.editor as { adopted?: boolean } | null)?.adopted === true;
    useCharacter.setState({
      skinId: doc.id ?? null,
      name: doc.name ?? '',
      lastEdited: (doc.editor as { editedAt?: string } | null)?.editedAt ?? null,
      files,
      turned: saved.turned,
      // Capsules saved by the editor's old surface measurement are the editor's, not the author's: the surface is the body again.
      capsules: saved.capsulesVersion ? null : saved.capsules,
      shell: saved.shell ?? null,
      mtoon: saved.mtoon ?? {},
      credit: (doc.editor as { credit?: string } | null)?.credit ?? null,
      model: files.source || files.mesh,
      bones: loaded.rows,
      materials: (c.materials ?? []).map((m) => m?.name ?? ''),
      vertices: saved.vertices || loaded.vertices,
      auto: saved.auto,
      picked: saved.picked,
      springs: saved.springs,
      colliders: saved.colliders,
      shaders: Object.fromEntries((c.materials ?? []).map((m) => [m?.name ?? '', 'lit' as const])),
      hide: (c.hide ?? []).filter((x): x is number => typeof x === 'number'),
      height: c.height ?? 1,
      view: 'body',
      step: 'bones',
      clip: null,
      playing: false,
    });
    await checkMatch();
    // A work whose swinging sets have pieces closer than STEADY_PIECE (an MMD tail of 49 bones 1 cm apart shakes on its own) keeps
    // only the bones that are far enough apart: merged once here, and the project (autosaved) keeps the merged chains from now on.
    scene?.updateMatrixWorld(true);
    {
      let merged = false;
      const springs = saved.springs.map((spring) => {
        if (spring.kind === 'skirt') return spring;
        let changed = false;
        const strands = spring.strands.map((strand) => {
          const out = steady(strand);
          if (out.length !== strand.length) changed = true;
          return out;
        });
        if (!changed) return spring;
        merged = true;
        return { ...spring, strands };
      });
      if (merged) useCharacter.setState({ springs });
    }
    // A work saved before the colliders were measured: this opening measures them from its model. The sliders' old compensations
    // (a smaller-than-the-body default) go back to ×1 over the real size.
    if (!Object.values(saved.colliders ?? {}).some((x) => x.fit !== undefined)) {
      await fitColliders(false, true);
    }
    // The same for the surface: a work from before it, one whose points an older fit measured, or one left empty the same way.
    if (!saved.shell || saved.shell.version !== SHELL_VERSION || !saved.shell.points) {
      await fitShell();
    }
    // A work opened after a migration (merged chains, missing fits) saves its measured form at once.
    await saveCharacter();
    // A card without its picture, or with one drawn from something else, gets it now.
    if (!(await pictureCurrent(doc.id ?? '', c))) void stageIcon();
  } catch (e) {
    // The log keeps the reason too: a toast is gone in a few seconds, and a work that will not open leaves no other trace.
    window.forge.appLog('openCharacter', `${path}: ${friendly(e)}`);
    useStore.getState().log(`openCharacter ${path}: ${friendly(e)}`);
    toast('error', t('character.reopenFailed', { v0: friendly(e) }));
  } finally {
    useCharacter.setState({ loading: false });
  }
}

/** Packs build one at a time: an apply and an export asked for together run in turn instead of writing over each other. */
let packRun: Promise<unknown> = Promise.resolve();
export function buildCharacterPack(out: string): Promise<string> {
  const run = packRun.then(() => buildCharacterPackNow(out));
  packRun = run.catch(() => undefined);
  return run;
}

/** Builds the character skin's pack at `out` (the backend copies the model files in and reads the pack back as the plugin does). */
async function buildCharacterPackNow(out: string): Promise<string> {
  await saveCharacter();
  const doc = characterDoc();
  const projectPath = characterProject();
  const id = useCharacter.getState().skinId;
  if (!doc?.character || !projectPath || !id) throw new Error(t('character.needModel'));
  // The 3D view goes in as the card picture: the game's skin page shows it without loading the model. A picture drawn from the
  // same model and bone match is reused instead of drawn again (a build usually follows an apply of the same look).
  if (await pictureCurrent(id, doc.character)) doc.character.picture = characterPicture(id);
  else {
    const picture = await stageIcon();
    if (picture) doc.character.picture = picture;
  }
  return (await call('pack.build', { project: doc, projectPath, out })).pack;
}

// The character's picture (PICTURE wide by tall, the pack's and the mod icon's), drawn offscreen with only the model in it: no bones,
// joints, colliders or grid, on a clear background. The model stands with its arms hanging (whatever its rest pose, T or A), seen from
// the front a little to its left and slightly above, framed full length by its own bounds; every model the same way.
const PICTURE = [512, 768] as const;

/** Swings `bone` so the way from it to `next` points along `toward` (world space); its children follow. */
function aim(bone: Object3D, next: Object3D, toward: Vector3) {
  bone.updateWorldMatrix(true, false);
  const start = bone.getWorldPosition(new Vector3());
  const now = next.getWorldPosition(new Vector3()).sub(start).normalize();
  const turn = new Quaternion().setFromUnitVectors(now, toward.clone().normalize());
  const parent = bone.parent!.getWorldQuaternion(new Quaternion());
  const world = bone.getWorldQuaternion(new Quaternion());
  bone.quaternion.copy(parent.invert().multiply(turn.multiply(world)));
  bone.updateWorldMatrix(false, true);
}

/** The natural stance's arm directions against straight down (-Y), per unit of the drop; the model's front is +Z. The upper arm hangs a
 *  little out from the body, the forearm a little further out and forward, so the elbow bends slightly as a relaxed person's does (set by
 *  eye on the bundled characters). */
const UPPER_ARM_OUT = 0.22;
const UPPER_ARM_FORWARD = 0.02;
const FOREARM_OUT = 0.12;
const FOREARM_FORWARD = 0.22;

/** Where a character's picture is taken from, as a direction from the model's centre: in front (+Z), a little to its side and a little
 *  above. The plugin photographs a pack without a picture from the same direction (Previews.PictureSide / PictureRise in
 *  Mods/ForgeSkin/Native/Previews.cs); change both together. */
const PICTURE_SIDE = 0.35;
const PICTURE_RISE = 0.15;

/** The arms let down beside the body, elbows a little bent; `joint` finds the model's bone for a canonical joint. */
function standing(joint: (name: string) => Object3D | undefined) {
  for (const side of ['left', 'right'] as const) {
    const [upper, lower, hand] = (['UpperArm', 'LowerArm', 'Hand'] as const).map((j) => joint(`${side}${j}`));
    if (!upper || !lower || !hand) continue;
    const out = Math.sign(lower.getWorldPosition(new Vector3()).x - upper.getWorldPosition(new Vector3()).x) || (side === 'left' ? 1 : -1);
    aim(upper, lower, new Vector3(UPPER_ARM_OUT * out, -1, UPPER_ARM_FORWARD));
    aim(lower, hand, new Vector3(FOREARM_OUT * out, -1, FOREARM_FORWARD));
  }
}

/** The character stage's lights: CharacterStage.tsx lights the 3D view with them and the picture is lit the same way. */
export const CHARACTER_LIGHTS = {
  ambient: 0.6,
  key: { intensity: 2.2, position: [2, 4, 3] as [number, number, number] },
  fill: { intensity: 0.8, position: [-3, 2, -2] as [number, number, number] },
};

let pictureRenderer: WebGLRenderer | null = null;
let pictures: Promise<unknown> = Promise.resolve();

/** Draws `model` as the picture, one at a time, as the character stage draws it (its lights, ACES tone mapping as the 3D view's
 *  renderer applies by default, no reflection map); `joint` finds its bone for a canonical joint, `reset` puts its bones back. */
function drawPicture(model: Object3D, joint: (name: string) => Object3D | undefined, reset: () => void): Promise<string | null> {
  const run = pictures.then(() => {
    pictureRenderer ??= Object.assign(new WebGLRenderer({ alpha: true, antialias: true, preserveDrawingBuffer: true }), { outputColorSpace: SRGBColorSpace, toneMapping: ACESFilmicToneMapping });
    pictureRenderer.setSize(PICTURE[0], PICTURE[1], false);
    return photograph(pictureRenderer, model, joint, reset);
  });
  pictures = run.catch(() => undefined);
  return run;
}

function photograph(renderer: WebGLRenderer, model: Object3D, joint: (name: string) => Object3D | undefined, reset: () => void): string | null {
  const home = model.parent;
  const stage = new Scene();
  const key = new DirectionalLight(0xffffff, CHARACTER_LIGHTS.key.intensity);
  key.position.set(...CHARACTER_LIGHTS.key.position);
  const fill = new DirectionalLight(0xffffff, CHARACTER_LIGHTS.fill.intensity);
  fill.position.set(...CHARACTER_LIGHTS.fill.position);
  stage.add(new AmbientLight(0xffffff, CHARACTER_LIGHTS.ambient), key, fill, model);
  try {
    reset();
    standing(joint);
    model.updateMatrixWorld(true);
    const box = new Box3().setFromObject(model, true);
    if (box.isEmpty()) return null;
    const center = box.getCenter(new Vector3());
    const size = box.getSize(new Vector3());
    const fov = 30;
    const half = Math.tan(((fov / 2) * Math.PI) / 180);
    const aspect = PICTURE[0] / PICTURE[1];
    const distance = (Math.max(size.y / 2, Math.max(size.x, size.z) / 2 / aspect) / half) * 1.12 + size.z / 2;
    const camera = new PerspectiveCamera(fov, aspect, distance / 100, distance * 10);
    camera.position.copy(center).addScaledVector(new Vector3(PICTURE_SIDE, PICTURE_RISE, 1).normalize(), distance);
    camera.lookAt(center);
    renderer.render(stage, camera);
    return renderer.domElement.toDataURL('image/png');
  } finally {
    if (home) home.add(model);
    else stage.remove(model);
    reset();
  }
}

/** The character's picture as a PNG in the data dir: the pack's picture, and the mod icon until the author picks one. */
export async function stageIcon(): Promise<string | null> {
  const model = modelScene();
  const id = useCharacter.getState().skinId;
  if (!model || !id) return null;
  const match = matchOf(useCharacter.getState());
  const url = await drawPicture(model, (j) => boneOf(match[j]), restPose);
  return url ? keepPicture(url, characterPicture(id), id, characterDoc()?.character) : null;
}

// A kept picture is noted in localStorage with what it was drawn from (the model's files, the bone match and PICTURE_LOOK), so
// one drawn from something else is drawn again; a picture that came in a pack is noted as such and kept.
/** How pictures are drawn: bumping it draws every kept picture again. */
const PICTURE_LOOK = 2;
/** A 32-bit FNV-1a hash of `text`, for the note of what a picture was drawn from. */
const hash = (text: string) => [...text].reduce((h, c) => Math.imul(h ^ c.charCodeAt(0), 16777619), 2166136261 >>> 0) >>> 0;
const pictureNote = (id: string) => `sf.charPicture.${id}`;
const lookOf = (c: SkinDocument['character'] | undefined) => {
  const m = c?.model;
  const bones = Object.entries(c?.bones ?? {}).sort(([a], [b]) => (a < b ? -1 : 1));
  return `${PICTURE_LOOK}|${hash(JSON.stringify([m?.descriptor, m?.mesh, m?.textures, bones]))}`;
};
const note = (id: string, value: string) => {
  try {
    localStorage.setItem(pictureNote(id), value);
  } catch {
    /* without storage the picture is drawn again next time */
  }
};
/** Whether the kept picture of character work `id` is still the one its document draws (or its pack's own). */
async function pictureCurrent(id: string, c: SkinDocument['character'] | undefined): Promise<boolean> {
  let v: string | null = null;
  try {
    v = localStorage.getItem(pictureNote(id));
  } catch {
    /* no storage: drawn again */
  }
  return (v === 'pack' || v === lookOf(c)) && (await window.forge.exists(characterPicture(id)));
}
/** Notes that work `id`'s kept picture is its pack's own card picture. */
export const notePackPicture = (id: string) => note(id, 'pack');

async function keepPicture(url: string, path: string, id: string, c: SkinDocument['character'] | undefined): Promise<string> {
  await window.forge.writeFile(path, new Uint8Array(await (await fetch(url)).arrayBuffer()));
  note(id, lookOf(c));
  pictureDrawn.set(id, Date.now());
  return path;
}

/** A character's picture drawn at `out` without opening it, from its converted model files and bone match (a work moved from a
 * pack, or a pack without a picture of its own): the picture an open character gets. `id` keys it for the cards; null without a model. */
export async function drawPackedPicture(character: NonNullable<SkinDocument['character']>, out: string, id: string): Promise<string | null> {
  const m = character.model;
  if (!m?.descriptor || !m.mesh) return null;
  const model = new GroupObject().add(await buildPacked({ descriptor: m.descriptor, mesh: m.mesh, textures: (m.textures ?? []).filter((x): x is string => !!x) }));
  const bones = new Map<string, Object3D>();
  const rest = new Map<Object3D, [Quaternion, Vector3]>();
  model.traverse((o) => {
    if (!(o as Bone).isBone) return;
    bones.set(typeof o.userData.name === 'string' ? o.userData.name : o.name, o);
    rest.set(o, [o.quaternion.clone(), o.position.clone()]);
  });
  const match = character.bones ?? {};
  const reset = () => {
    rest.forEach(([q, p], o) => void (o.quaternion.copy(q), o.position.copy(p)));
    model.updateMatrixWorld(true);
  };
  try {
    const url = await drawPicture(model, (j) => bones.get(match[j] ?? ''), reset);
    return url ? keepPicture(url, out, id, character) : null;
  } finally {
    model.traverse((o) => {
      const mesh = o as SkinnedMesh;
      if (!mesh.geometry) return;
      mesh.geometry.dispose();
      for (const mat of ([] as Material[]).concat(mesh.material)) {
        for (const v of Object.values(mat)) if ((v as Texture | null)?.isTexture) (v as Texture).dispose();
        mat.dispose();
      }
    });
  }
}

/** Draws the picture of character work `k` in the background when it has none or one drawn from something else, once a session. */
const drawing = new Set<string>();
export function ensureCharacterPicture(k: { id: string; path: string }) {
  if (drawing.has(k.id)) return;
  drawing.add(k.id);
  void (async () => {
    const doc = await call('project.load', { path: k.path });
    if (doc.character && !(await pictureCurrent(k.id, doc.character))) await drawPackedPicture(doc.character, characterPicture(k.id), k.id);
  })().catch(() => undefined);
}

/** Where a character skin's picture is kept (its card in the work list shows it), and when it was last drawn in this session. */
export const characterPicture = (id: string) => `${useStore.getState().dataDir}\\export\\${id}-icon.png`;
export const pictureDrawn = new Map<string, number>();

/** Saves the character skin as a .forgeskin pack where the author picks. */
export async function exportCharacterPack() {
  const s = useCharacter.getState();
  const out = await window.forge.saveDialog(`${s.name || s.skinId}.forgeskin`, [{ name: t('actions.k16'), extensions: ['forgeskin'] }]);
  if (!out) return;
  try {
    const pack = await buildCharacterPack(out);
    useStore.getState().toast('ok', t('actions.k17'), { label: t('actions.k18'), run: () => void window.forge.showInFolder(pack) });
  } catch (e) {
    useStore.getState().fail(e, t('actions.k19'));
  }
}

/** Packs the character skin as a Thunderstore mod through the same export as weapon skins (ForgeSkin format). */
export async function exportCharacterMod(info: { name: string; version: string; description: string; icon: string }): Promise<boolean> {
  const st = useStore.getState();
  const out = await window.forge.saveDialog(`${info.name}-${info.version}.zip`, [{ name: 'Thunderstore', extensions: ['zip'] }]);
  if (!out) return false;
  try {
    const pack = await buildCharacterPack(`${st.dataDir}\\packs\\${useCharacter.getState().skinId}.forgeskin`);
    const r = await call('pack.exportMod', { packs: [pack], out, ...info, format: 'forgeskin', gunPack: false });
    st.toast('ok', t('exportMod.done'), { label: t('actions.k18'), run: () => void window.forge.showInFolder(r.mod) });
    return true;
  } catch (e) {
    st.fail(e, t('exportMod.failed'));
    return false;
  }
}

/** The values each preset starts from, read once from ForgeCore's ChainSpec presets (springs.presets). */
export type PresetValues = Record<
  SpringKind,
  {
    stiffness: number;
    damping: number;
    weight: number;
    gravity: number;
    radius: number;
    angleLimit: number;
    restore: number;
    styleLeash: number;
    inertia: number;
    azimuthLimit: number;
    collide: boolean;
    collideCloth: boolean;
    selfCollide: boolean;
    step: number;
    iterations: number;
    lengthPasses: number;
  }
>;
let presets: PresetValues | null = null;
const presetListeners = new Set<() => void>();
export function useSpringPresets(): PresetValues | null {
  const value = useSyncExternalStore(
    (l) => (presetListeners.add(l), () => void presetListeners.delete(l)),
    () => presets,
  );
  useEffect(() => {
    if (!presets)
      void call('springs.presets', {}).then(
        (r) => ((presets = r as PresetValues), presetListeners.forEach((l) => l())),
        (e) => toast('error', friendly(e)),
      );
  }, []);
  return value;
}

/** A match with each bone on one joint: the first joint in the rig's order keeps it, the later ones are left without a bone (a
 * matcher that found no bone of its own for a joint is not to stand in another joint's; a bone takes one joint's pose). */
function oneJointPerBone(match: Record<string, string>): Record<string, string> {
  const out: Record<string, string> = {};
  const taken = new Set<string>();
  for (const joint of [...BODY_JOINTS, ...FINGER_JOINTS]) {
    const bone = match[joint];
    if (!bone || taken.has(bone)) continue;
    taken.add(bone);
    out[joint] = bone;
  }
  return out;
}

/** The automatic match, on import and on 重新自动匹配 alike: what the file's own humanoid (VRM) says first; the joints it does not name
 * come from the site's matcher (run on every root bone of the model, the root it matches best wins, with the fingers under each hand),
 * which never takes a bone the file gave a joint. A part neither finds is left without a bone. */
function autoMatch(): Record<string, string> {
  const out: Record<string, string> = {};
  for (const [joint, bone] of Object.entries(fileJoints)) if (byName.has(bone)) out[joint] = bone;
  const named = new Set(Object.values(out));
  const add = (joint: string, bone: Object3D) => void (joint in out || named.has(bone.name) || (out[joint] = bone.name));
  let best: { found: Map<string, Object3D>; missing: number } | null = null;
  for (const bone of byName.values()) {
    if ((bone.parent as Bone | null)?.isBone) continue;
    const { bones: found, missing } = matchHumanBones<Object3D>(bone);
    if (!best || missing.length < best.missing) best = { found, missing: missing.length };
  }
  for (const [joint, bone] of best?.found ?? []) if (BODY_JOINTS.includes(joint)) add(joint, bone);
  for (const side of ['left', 'right'] as const) {
    const hand = byName.get(out[side + 'Hand']);
    if (hand) for (const [joint, bone] of matchHumanFingers<Object3D>(hand, side)) add(joint, bone);
  }
  return oneJointPerBone(out);
}

/** Auto-matches again, dropping the author's picks. */
export function rematch() {
  useCharacter.setState({ auto: autoMatch(), picked: {} });
  void (async () => {
    await fitColliders();
    await fitShell();
  })();
  void checkMatch();
}

const v = new Vector3();

/** Holds the current match to ForgeCore's rig check: rest positions in the game's axes (X mirrored), the model's own space. */
export async function checkMatch() {
  if (!scene) return;
  scene.updateMatrixWorld(true);
  const joints: Record<string, number[]> = {};
  for (const [joint, name] of Object.entries(matchOf(useCharacter.getState()))) {
    const bone = byName.get(name);
    if (!bone) continue;
    bone.getWorldPosition(v);
    joints[joint] = [-v.x, v.y, v.z];
  }
  try {
    const check = (await call('character.check', { joints })) as RigCheck;
    useCharacter.setState({ check });
  } catch (e) {
    toast('error', friendly(e));
  }
}

// ---- the first-person arms ----

/** The model's own meshes shown or not (the arms view hides them). */
export function showBody(visible: boolean) {
  scene?.traverse((o) => void ((o as SkinnedMesh).isSkinnedMesh && !o.userData.arms && (o.visible = visible)));
}

/** The first-person arms the game wears for this match (backend character.arms: ForgeCore's own cut, `hide` left out), as a mesh on
 * the model's own skeleton so the moves play on it; null (with a toast) when the backend refuses. Its vertices come in Unity's axes and
 * winding, turned back into the view's (X negated, triangles reversed). The caller removes it from the scene and disposes it. */
export async function buildArms(): Promise<SkinnedMesh | null> {
  const s = useCharacter.getState();
  if (!scene || !s.files) return null;
  let r: Methods['character.arms'][1];
  try {
    r = await call('character.arms', { descriptor: s.files.descriptor, mesh: s.files.mesh, bones: matchOf(s), hide: s.hide });
  } catch (e) {
    toast('error', friendly(e));
    return null;
  }
  const bytes = (b64: string) => Uint8Array.from(atob(b64), (c) => c.charCodeAt(0)).buffer;
  const positions = new Float32Array(bytes(r.positions));
  const normals = new Float32Array(bytes(r.normals));
  for (let i = 0; i < positions.length; i += 3) {
    positions[i] = -positions[i];
    normals[i] = -normals[i];
  }
  const geometry = new BufferGeometry();
  geometry.setAttribute('position', new Float32BufferAttribute(positions, 3));
  geometry.setAttribute('normal', new Float32BufferAttribute(normals, 3));
  geometry.setAttribute('uv', new Float32BufferAttribute(new Float32Array(bytes(r.uvs)), 2));
  geometry.setAttribute('skinIndex', new Uint16BufferAttribute(new Uint16Array(bytes(r.index)), 4));
  geometry.setAttribute('skinWeight', new Float32BufferAttribute(new Float32Array(bytes(r.weight)), 4));
  const index: number[] = [];
  const materials: Material[] = [];
  const named = new Map<string, Material>();
  scene.traverse((o) => {
    const m = (o as SkinnedMesh).material;
    if (!(o as SkinnedMesh).isSkinnedMesh || !m) return;
    for (const x of Array.isArray(m) ? m : [m]) if (!named.has(x.name)) named.set(x.name, x);
  });
  for (const sub of r.submeshes) {
    const tris = new Uint32Array(bytes(sub.triangles));
    const start = index.length;
    // The mesh file's triangles wind the other way (Unity's); X negated back, they are reversed back.
    for (let i = 0; i + 2 < tris.length; i += 3) index.push(tris[i], tris[i + 2], tris[i + 1]);
    geometry.addGroup(start, tris.length, materials.length);
    materials.push(named.get(s.materials[sub.material] ?? '') ?? new MeshStandardMaterial({ color: '#9aa4b0' }));
  }
  geometry.setIndex(index);
  if (!index.length) {
    toast('info', t('character.armsEmpty'));
    return null;
  }
  // Bound at rest, on the view's own bones by name (the converter keeps the file's names).
  restPose();
  scene.updateMatrixWorld(true);
  const bones = r.bones.map((name) => byName.get(name) ?? new Bone());
  const mesh = new SkinnedMeshObject(geometry, materials);
  mesh.userData.arms = true;
  mesh.frustumCulled = false;
  scene.add(mesh);
  mesh.updateMatrixWorld(true);
  mesh.bind(new Skeleton(bones));
  return mesh;
}

/** Gives `joint` the bone the author picked, none (null: the part is left out), or the automatic match's again (undefined). A bone
 * takes one joint's pose, so a bone another joint had moves here and that joint is left without one. */
export function pickBone(joint: string, bone: string | null | undefined) {
  useCharacter.setState((s) => {
    const picked = Object.fromEntries(Object.entries(s.picked).filter(([k]) => k !== joint));
    if (bone !== undefined) picked[joint] = bone;
    const now = bone === undefined ? s.auto[joint] : bone;
    if (now) for (const [other, used] of Object.entries(matchOf({ auto: s.auto, picked }))) if (other !== joint && used === now) picked[other] = null;
    return { picked };
  });
  void fitColliders();
  if (BODY_JOINTS.includes(joint)) {
    void fitShell();
  }
  void checkMatch();
}

// ---- spring bones ----

const isBone = (o: Object3D): o is Bone => (o as Bone).isBone === true;

/** The strands a spring set starts at `name`: a bone with two or more child bones (a skirt's ring) starts one strand per child, any
 * other one strand of its own; each strand follows the first child bone down to its end. */
export function strandsFrom(name: string): string[][] {
  const bone = byName.get(name);
  if (!bone) return [];
  const down = (b: Bone) => {
    const out: string[] = [];
    for (let at: Bone | undefined = b; at; at = at.children.find(isBone)) out.push(at.name);
    return out;
  };
  const kids = bone.children.filter(isBone);
  if (kids.length < 2) return [down(bone)];
  // A branch that is not the main one (a skirt root's side accessory) starts at its own bone; only the longest branch keeps the
  // root, so the ring has its top bone and the accessory sits at its own height, out of the ring.
  const paths = kids.map(down);
  let main = 0;
  for (let i = 1; i < paths.length; i++) if (paths[i]!.length > paths[main]!.length) main = i;
  return [[bone.name, ...paths[main]!], ...paths.filter((_, i) => i !== main)];
}

const SPRING_NAME: Record<SpringKind, 'hair' | 'skirt' | 'tail'> = { longHair: 'hair', shortHair: 'hair', skirt: 'skirt', tail: 'tail' };

/** The fewest metres two bones of a swinging chain may be apart. Pieces shorter than this make the solver's gravity step and the
 * chain's own constraints fight each other: a 49-bone MMD tail whose tips are 1 cm apart shakes at metres a second on its own even
 * with the body still; measured on the twin tails, 2.5 cm pieces still shook and 3 cm was marginal, while 4 cm rests. Bones between
 * two kept ones ride their parent rigidly. */
const STEADY_PIECE = 0.04;

/** A strand with bones dropped so that kept ones are at least STEADY_PIECE apart; the last bone always stays (it is the tip). */
export function steady(strand: string[]): string[] {
  if (strand.length < 3) return strand;
  const out = [strand[0]!];
  const at = new Vector3(),
    last = new Vector3();
  const bone = byName.get(strand[0]!);
  if (!bone) return strand;
  bone.getWorldPosition(last);
  for (let i = 1; i < strand.length; i++) {
    const b = byName.get(strand[i]!);
    if (!b) continue;
    b.getWorldPosition(at);
    if (i < strand.length - 1 && at.distanceTo(last) < STEADY_PIECE) continue;
    out.push(strand[i]!);
    last.copy(at);
  }
  return out.length >= 2 ? out : strand;
}
/** A set's name on screen: its kind and a number among the sets of that kind (头发 1, 裙摆 1). */
export function springLabel(springs: { id: string; kind: SpringKind }[], id: string): string {
  const kind = springs.find((x) => x.id === id)?.kind ?? 'longHair';
  const same = springs.filter((x) => SPRING_NAME[x.kind] === SPRING_NAME[kind]);
  return `${t(`character.springName.${SPRING_NAME[kind]}`)} ${same.findIndex((x) => x.id === id) + 1}`;
}

/** The bones a new set can start at, as dots on the figure: the top of each run of bones that neither the match nor a set uses and
 * that has something below it to swing. */
export function looseRoots(s: Pick<CharacterState, 'auto' | 'picked' | 'springs' | 'bones'>): string[] {
  const used = new Set([...Object.values(matchOf(s)), ...s.springs.flatMap((x) => x.strands.flat())]);
  return s.bones
    .filter((b) => !used.has(b.name) && (b.parent < 0 || used.has(s.bones[b.parent]!.name)))
    .map((b) => b.name)
    .filter((name) => strandsFrom(name).some((x) => x.length >= 2));
}

/** Makes the bones under `name` swing as a new set and selects it. */
export function addSpring(name: string) {
  // A strand moves from its second bone on: one bone alone has nothing to swing.
  const raw = strandsFrom(name).filter((x) => x.length >= 2);
  if (!raw.length) return void useStore.getState().toast('info', t('character.springTooShort'));
  const kind = guessKind(name, raw);
  // A skirt's pieces stay as the panel's bones have them (its ring geometry depends on every bone); hair and tails drop bones
  // closer than STEADY_PIECE, which a solver at their substep cannot rest with.
  const strands = kind === 'skirt' ? raw : raw.map(steady);
  const id = `${name}-${Date.now().toString(36)}`;
  useCharacter.setState((x) => ({ springs: [...x.springs, { id, kind, strands }], spring: id, hover: null }));
}

/** A starting preset from the bones' names and length. */
export function guessKind(name: string, strands: string[][]): SpringKind {
  const n = name.toLowerCase();
  if (/skirt|dress|cloth|cape/.test(n) || strands.length >= 3) return 'skirt';
  if (/tail|ribbon|ear/.test(n)) return 'tail';
  return Math.max(...strands.map((s) => s.length)) >= 3 ? 'longHair' : 'shortHair';
}

/** Per skeleton bone, how many vertices it moves (a weight over 0.05): a bone nothing is skinned to is a helper, not a body part. */
async function weightedBones(gen: number): Promise<Map<string, number> | null> {
  const counts = new Map<string, number>();
  const meshes: SkinnedMesh[] = [];
  scene?.traverse((o) => {
    const mesh = o as SkinnedMesh;
    if (mesh.isSkinnedMesh && mesh.geometry) meshes.push(mesh);
  });
  let since = performance.now(),
    seen = 0;
  for (const mesh of meshes) {
    const position = mesh.geometry.getAttribute('position');
    const skinIndex = mesh.geometry.getAttribute('skinIndex');
    const skinWeight = mesh.geometry.getAttribute('skinWeight');
    if (!position || !skinIndex || !skinWeight) continue;
    const names = mesh.skeleton.bones.map((bone) => bone.name);
    const add = (index: number) => {
      const name = names[index];
      if (name) counts.set(name, (counts.get(name) ?? 0) + 1);
    };
    for (let i = 0; i < position.count; i++) {
      if ((++seen & 0x3fff) === 0 && performance.now() - since > SLICE_MS) {
        await breathe();
        if (gen !== fitRun.springs) return null;
        since = performance.now();
      }
      if (skinWeight.getX(i) > 0.05) add(skinIndex.getX(i));
      if (skinWeight.getY(i) > 0.05) add(skinIndex.getY(i));
      if (skinWeight.getZ(i) > 0.05) add(skinIndex.getZ(i));
      if (skinWeight.getW(i) > 0.05) add(skinIndex.getW(i));
    }
  }
  return counts;
}

/** The joints that make the bones under them a limb: a run hanging from one is a sleeve or a part of the body, not a spring. */
const LIMB_JOINT = /(Arm|Hand|Shoulder|Leg|Foot|Toe|Thumb|Index|Middle|Ring|Pinky)/;
const SKIRT_NAME = /skirt|dress|coat|jacket|cloak|cape|mantle|スカート|コート|マント|裙|裾|下摆/i;
const HAIR_NAME = /hair|bang|fringe|ahoge|ponytail|twintail|braid|马尾|馬尾|髪|かみ|毛|前髪|横髪|後ろ髪|もみあげ|アホ毛|ツインテ|ポニテ|三つ編み/i;
const TAIL_NAME = /tail|ribbon|scarf|muffler|neck ?tie|尾|しっぽ|テール|リボン|マフラー|ネクタイ/i;
/** Bones of the face and the mouth: they move with the jaw and the blendshapes, never with physics. */
const FACE_NAME = /舌|目|眉|口|歯|jaw|tongue|eye|brow|mouth|tooth|teeth|lash|睫毛/i;

/** Whether these root points go round the vertical axis (no gap wider than 150°): a skirt's ring, not a pair of tails. */
function wrapsRound(points: Vector3[]): boolean {
  if (points.length < 3) return false;
  const cx = points.reduce((n, p) => n + p.x, 0) / points.length,
    cz = points.reduce((n, p) => n + p.z, 0) / points.length;
  const angles = points.map((p) => Math.atan2(p.z - cz, p.x - cx)).sort((a, b) => a - b);
  let gap = angles[0]! + Math.PI * 2 - angles[angles.length - 1]!;
  for (let i = 1; i < angles.length; i++) gap = Math.max(gap, angles[i]! - angles[i - 1]!);
  return gap <= (150 * Math.PI) / 180;
}

/** Roots closer than this are the same skirt layer; a side accessory just under the waist can sit 1.8 cm lower and stays out. */
const RING_BAND = 0.01;

/** Strands ordered round the vertical axis, so a linked ring joins neighbours and not strands on the far side. */
function orderStrands(strands: string[][]): string[][] {
  const placed = strands.map((st) => {
    const b = byName.get(st[0] ?? '');
    return { st, at: b ? b.getWorldPosition(new Vector3()) : null };
  });
  const withAt = placed.filter((x): x is { st: string[]; at: Vector3 } => !!x.at);
  if (withAt.length < 2) return strands;
  const cx = withAt.reduce((n, x) => n + x.at.x, 0) / withAt.length,
    cz = withAt.reduce((n, x) => n + x.at.z, 0) / withAt.length;
  withAt.sort((a, b) => Math.atan2(a.at.z - cz, a.at.x - cx) - Math.atan2(b.at.z - cz, b.at.x - cx));
  return [...withAt.map((x) => x.st), ...placed.filter((x) => !x.at).map((x) => x.st)];
}

/** The family a skirt column's root name belongs to: `スカート_0_4` is one column of `スカート_0`, so every column of a ring shares
 * the key even when their roots sit at different heights (a skirt's waist is not level). A name without the `_depth_column` tail (a
 * side accessory) has no key and is grouped by height, or left as its own tail. */
const COLUMN_ROOT = /^(.*_\d+)_\d+(?:\.[LR])?$/;
const ringKey = (name: string): string | null => COLUMN_ROOT.exec(name)?.[1] ?? null;

/** Spring sets normalised into rings: an existing skirt set may already hold two rings' worth of strands, or a ring plus an accessory;
 * tail sets whose strands ring the body at one height (an import that took each strand on its own) are joined into one skirt set.
 * Every ring's strands are ordered round the axis and linked; a strand at another height is left out as its own tail. Returns the new
 * list, or null when nothing changed. */
function mergeSkirtSets(springs: Spring[]): Spring[] | null {
  let list = springs;
  const skirtSets = list.filter((x) => x.kind === 'skirt');
  if (skirtSets.length) {
    type Piece = { owner: Spring; strand: string[]; at: Vector3; key: string | null };
    const pieces: Piece[] = [];
    for (const owner of skirtSets) {
      for (const strand of owner.strands) {
        const b = byName.get(strand[0] ?? '');
        if (b) pieces.push({ owner, strand, at: b.getWorldPosition(new Vector3()), key: ringKey(strand[0] ?? '') });
      }
    }
    const groups: { at: Vector3; pieces: Piece[] }[] = [];
    // Columns named as one family are one ring at any waist height; unnamed pieces still group by height.
    for (const p of pieces) {
      if (!p.key) continue;
      const g = groups.find((x) => x.pieces[0]?.key === p.key);
      if (g) g.pieces.push(p);
      else groups.push({ at: p.at, pieces: [p] });
    }
    for (const p of pieces) {
      if (p.key) continue;
      const g = groups.find((x) => x.pieces[0]?.key == null && Math.abs(x.at.y - p.at.y) <= RING_BAND);
      if (g) g.pieces.push(p);
      else groups.push({ at: p.at, pieces: [p] });
    }
    const ringOf = new Map<Piece, Spring>();
    const ringOwners = new Set<string>();
    const made = new Map<string, Spring[]>();
    const used = new Set(springs.map((x) => x.id));
    for (const g of groups) {
      if (g.pieces.length < 3 || !wrapsRound(g.pieces.map((x) => x.at))) continue;
      const first = g.pieces[0]!.owner;
      const strands = orderStrands(g.pieces.map((x) => x.strand));
      let id = first.id;
      if (made.has(id)) {
        let n = 2;
        while (used.has(`${first.id}-ring${n}`)) n++;
        id = `${first.id}-ring${n}`;
        used.add(id);
      }
      const ring: Spring = { ...first, id, kind: 'skirt', strands, linkSide: true, loop: strands.length > 2 };
      made.set(first.id, [...(made.get(first.id) ?? []), ring]);
      for (const p of g.pieces) {
        ringOf.set(p, ring);
        ringOwners.add(p.owner.id);
      }
    }
    if (made.size) {
      const extra = new Map<string, Spring>();
      for (const p of pieces) {
        if (ringOf.has(p) || !ringOwners.has(p.owner.id)) continue;
        let tail = extra.get(p.owner.id);
        if (!tail) {
          let id = `${p.owner.id}-tail`;
          for (let n = 2; used.has(id); n++) id = `${p.owner.id}-tail-${n}`;
          used.add(id);
          tail = { ...p.owner, id, kind: 'tail', strands: [], linkSide: false, loop: false };
          extra.set(p.owner.id, tail);
        }
        tail.strands.push(p.strand);
      }
      for (const set of extra.values()) set.strands = orderStrands(set.strands);
      const out: Spring[] = [];
      for (const s of list) {
        if (s.kind !== 'skirt') {
          out.push(s);
          continue;
        }
        const rings = made.get(s.id);
        if (rings) {
          out.push(...rings);
          const tail = extra.get(s.id);
          if (tail) out.push(tail);
        } else if (extra.has(s.id)) {
          out.push(extra.get(s.id)!);
        } else if (!ringOwners.has(s.id)) {
          out.push(s.linkSide || s.loop ? { ...s, linkSide: false, loop: false } : s);
        }
      }
      list = out;
    }
  }
  const entries = list
    .filter((x) => x.kind === 'tail')
    .map((spring) => ({
      spring,
      roots: spring.strands.map((st) => byName.get(st[0] ?? '')).filter((b): b is Bone => !!b).map((b) => b.getWorldPosition(new Vector3())),
    }))
    .filter((x) => x.roots.length > 0);
  if (entries.length >= 3) {
    const made = new Map<string, Spring>();
    const gone = new Set<string>();
    for (const entry of entries) {
      if (gone.has(entry.spring.id) || made.has(entry.spring.id)) continue;
      const y = entry.roots[0]!.y;
      const band = entries.filter(
        (x) => !gone.has(x.spring.id) && !made.has(x.spring.id) && x.roots.every((r) => Math.abs(r.y - y) <= RING_BAND),
      );
      if (band.length < 3) continue;
      if (!wrapsRound(band.flatMap((x) => x.roots))) continue;
      const first = band[0]!.spring;
      const strands = orderStrands(band.flatMap((x) => x.spring.strands));
      made.set(first.id, { ...first, kind: 'skirt', strands, linkSide: true, loop: strands.length > 2 });
      for (const x of band) if (x.spring !== first) gone.add(x.spring.id);
    }
    if (made.size) list = list.map((x) => made.get(x.id) ?? x).filter((x) => !gone.has(x.id));
  }
  return sameSprings(list, springs) ? null : list;
}

/** Whether two spring lists are the same for what the rings care about (ids, kinds, anchors, links and strands). */
function sameSprings(a: Spring[], b: Spring[]): boolean {
  return (
    a.length === b.length &&
    a.every((x, i) => {
      const y = b[i]!;
      return (
        x.id === y.id &&
        x.kind === y.kind &&
        x.anchor === y.anchor &&
        x.linkSide === y.linkSide &&
        x.loop === y.loop &&
        x.strands.length === y.strands.length &&
        x.strands.every((st, j) => st.length === y.strands[j]!.length && st.every((n, k) => n === y.strands[j]![k]))
      );
    })
  );
}

/** Adds every loose run of bones that hangs like hair, a skirt or a tail as spring sets: one set for the hair (per bone it hangs from),
 * one per skirt layer (linked side by side and round), one per tail. What is left out: bones the humanoid pose or a set already uses,
 * the control bones above the body (MMD's センター, IK and shoulder helper chains), face bones, bones nothing is skinned to, and runs
 * hanging from an arm or a leg. Returns how many sets were added. */
export async function autoAddSprings(announce = true): Promise<number> {
  let s = useCharacter.getState();
  if (!scene) return 0;
  const gen = ++fitRun.springs;
  restNow();
  // Strands an older import took one by one round the hips (a tail set each) come together as one skirt ring, as this pass makes
  // them: the ring is what keeps a skirt's panels from folding into each other and from swinging like loose tails.
  const joined = mergeSkirtSets(s.springs);
  if (joined) {
    const was = s.spring;
    useCharacter.setState({ springs: joined });
    if (was && !joined.some((x) => x.id === was)) useCharacter.setState({ spring: joined.find((x) => x.kind === 'skirt')?.id ?? joined[0]?.id });
    s = useCharacter.getState();
  }
  const match = matchOf(s);
  const jointOf = new Map<string, string>(Object.entries(match).map(([joint, bone]) => [bone, joint] as const));
  const parentOf = new Map<string, string | null>(s.bones.map((b) => [b.name, b.parent < 0 ? null : s.bones[b.parent]!.name] as const));
  const weighted = await weightedBones(gen);
  if (!weighted) return 0;
  const used = new Set([...Object.values(match), ...s.springs.flatMap((x) => x.strands.flat())]);
  // Every bone above a posed joint moves the body: a run that reaches one of them is a control chain (センター, an IK helper), not cloth.
  const control = new Set<string>();
  for (const bone of Object.values(match)) for (let at = parentOf.get(bone) ?? null; at; at = parentOf.get(at) ?? null) control.add(at);
  const nearest = (name: string): string | null => {
    for (let at = parentOf.get(name) ?? null; at; at = parentOf.get(at) ?? null) if (jointOf.has(at)) return jointOf.get(at)!;
    return null;
  };
  type Kind = 'hair' | 'skirt' | 'tail';
  const groups = new Map<string, { kind: Kind; anchor?: string; strands: string[][] }>();
  const skirt: { root: Vector3; anchor?: string; key: string | null; strands: string[][] }[] = [];
  const tails: { key: string; at: Vector3; anchor: string; strands: string[][] }[] = [];
  for (const root of looseRoots(s)) {
    await breathe();
    if (gen !== fitRun.springs) return 0;
    restNow();
    if (FACE_NAME.test(root) || control.has(root)) continue;
    // A strand an existing set already moves is not taken again (a ring root stays loose after its strands were added); neither is one
    // that runs through a control bone.
    const strands = strandsFrom(root).filter((x) => x.length >= 2 && !x.some((n) => used.has(n) || control.has(n)));
    if (!strands.length) continue;
    if (!strands.some((x) => x.some((n) => (weighted.get(n) ?? 0) > 0))) continue;
    const near = nearest(root);
    if (near && LIMB_JOINT.test(near)) continue;
    const named = SKIRT_NAME.test(root) || HAIR_NAME.test(root) || TAIL_NAME.test(root);
    // A root nothing is skinned to is a control bone itself (MMD's グルーブ, an IK root), unless its name says what it is.
    if (!named && ((weighted.get(root) ?? 0) === 0 || near === null)) continue;
    const kind: Kind = SKIRT_NAME.test(root) ? 'skirt' : HAIR_NAME.test(root) ? 'hair' : TAIL_NAME.test(root) ? 'tail' : near === 'head' ? 'hair' : near === 'hip' ? 'skirt' : 'tail';
    const anchor = (near ? match[near] : undefined) ?? parentOf.get(root) ?? undefined;
    if (!anchor) continue;
    const at = boneOf(root);
    if (!at) continue;
    if (kind === 'skirt') {
      // Branches under one skirt root can sit at different heights (a panel and a side accessory); each strand is grouped by its
      // own root, so a lower accessory becomes another layer instead of a member of the ring.
      const rootAt = at.getWorldPosition(new Vector3());
      for (const strand of strands) {
        const first = byName.get(strand[0] ?? '');
        skirt.push({
          root: first ? first.getWorldPosition(new Vector3()) : rootAt,
          anchor,
          key: ringKey(strand[0] ?? ''),
          strands: [strand],
        });
      }
    } else {
      const key = kind === 'hair' ? `hair:${anchor}` : `tail:${root}`;
      const group = groups.get(key) ?? { kind, anchor, strands: [] };
      group.strands.push(...strands.map(steady));
      groups.set(key, group);
      if (kind === 'tail') tails.push({ key, at: at.getWorldPosition(new Vector3()), anchor, strands });
    }
  }
  // A ring of strands at one height is a skirt layer whatever their nearest joint is: MMD skirt bones often hang from the spine,
  // and taken one by one they would each swing as a loose tail, folding and never holding the skirt's round.
  const ringed = new Set<string>();
  for (const base of tails) {
    if (ringed.has(base.key)) continue;
    const band = tails.filter((x) => !ringed.has(x.key) && Math.abs(x.at.y - base.at.y) <= RING_BAND);
    if (!wrapsRound(band.map((x) => x.at))) continue;
    for (const x of band) {
      ringed.add(x.key);
      groups.delete(x.key);
    }
    groups.set(`skirt:${base.key}`, {
      kind: 'skirt',
      anchor: match.hip ?? base.anchor,
      strands: orderStrands(band.flatMap((x) => x.strands)),
    });
  }
  // A skirt's layers: roots at another height are another layer (a petticoat, a coat over the skirt); each layer is one set, all strands
  // of it round the hips, linked side to side (roots within a centimetre are one ring, not layers).
  const layers: { y: number; key: string | null; anchor?: string; strands: string[][] }[] = [];
  for (const entry of skirt) {
    // A named column family is one layer however its waist roots are levelled; unnamed pieces still group by height.
    const layer = entry.key
      ? layers.find((l) => l.key === entry.key)
      : layers.find((l) => l.key == null && Math.abs(l.y - entry.root.y) <= RING_BAND);
    if (layer) layer.strands.push(...entry.strands);
    else layers.push({ y: entry.root.y, key: entry.key, anchor: entry.anchor, strands: [...entry.strands] });
  }
  for (const layer of layers) layer.strands = orderStrands(layer.strands);
  for (const [i, layer] of layers.entries()) groups.set(`skirt:${i}`, { kind: 'skirt', anchor: layer.anchor, strands: layer.strands });
  const created: Spring[] = [];
  for (const g of groups.values()) {
    const longest = Math.max(...g.strands.map((x) => x.length));
    const kind: SpringKind = g.kind === 'skirt' ? 'skirt' : g.kind === 'tail' ? 'tail' : longest >= 4 ? 'longHair' : 'shortHair';
    const base = g.strands[0]![0]!;
    created.push({
      id: `${base}-${Date.now().toString(36)}-${created.length}`,
      kind,
      strands: g.strands,
      anchor: g.anchor,
      linkSide: kind === 'skirt' && g.strands.length > 1,
      loop: kind === 'skirt' && g.strands.length > 2,
    });
  }
  if (gen !== fitRun.springs) return 0;
  if (!created.length) {
    if (announce) toast('info', t('character.autoSpringsNone'));
    return 0;
  }
  useCharacter.setState((x) => ({ springs: [...x.springs, ...created], spring: created[0]!.id, hover: null }));
  if (announce) toast('ok', t('character.autoSpringsAdded', { v0: created.length }));
  return created.length;
}

/** The bone a set hangs from: the one the file named, else its first strand's root's parent. */
const anchorOf = (spring: Spring): Object3D | null => (spring.anchor ? byName.get(spring.anchor) : undefined) ?? byName.get(spring.strands[0]?.[0] ?? '')?.parent ?? null;

/** A set as the character document holds it (Core/Character.cs SpringChain): a skirt's strands are linked side by side, and round
 * when there are three or more, unless the file said how. */
export function springEntry(spring: Spring) {
  const linkSide = spring.kind === 'skirt' && spring.strands.length > 1;
  return {
    id: spring.id,
    kind: spring.kind,
    anchor: anchorOf(spring)?.name ?? '',
    strands: spring.strands,
    linkSide: spring.linkSide ?? linkSide,
    loop: spring.loop ?? (linkSide && spring.strands.length > 2),
    weight: spring.weight,
    damping: spring.damping,
    stiffness: spring.stiffness,
    gravity: spring.gravity,
    radius: spring.radius,
    angleLimit: spring.angleLimit,
    restore: spring.restore,
    styleLeash: spring.styleLeash,
    inertia: spring.inertia,
    azimuthLimit: spring.azimuthLimit,
    collide: spring.collide,
    collideCloth: spring.collideCloth,
    selfCollide: spring.selfCollide,
    step: spring.step,
    iterations: spring.iterations,
    lengthPasses: spring.lengthPasses,
    fabric: fabricEntry(spring),
  };
}

/** The model's size against the canonical body (its hip height over 1.04 m), for the colliders' radii. */
function sizeRatio(): number {
  const hip = byName.get(matchOf(useCharacter.getState()).hip ?? '');
  if (!scene || !hip) return 1;
  const box = new Box3().setFromObject(scene);
  return Math.max((hip.getWorldPosition(v).y - box.min.y) / CANONICAL_HIP, 0.1);
}

const NEXT: Record<ColliderJoint, string | null> = {
  head: null,
  spine2: 'neck',
  hip: 'spine0',
  leftUpperLeg: 'leftLowerLeg',
  rightUpperLeg: 'rightLowerLeg',
  leftLowerLeg: 'leftFoot',
  rightLowerLeg: 'rightFoot',
};

/** Where a collider lies where the bones are now: from its joint to the next joint down the body, or for the head up the way the
 * neck points. */
function axisOf(joint: ColliderJoint, ratio: number): { a: Vector3; b: Vector3 } | null {
  const match = matchOf(useCharacter.getState());
  const bone = byName.get(match[joint] ?? '');
  if (!bone) return null;
  const a = bone.getWorldPosition(new Vector3());
  const nextBone = byName.get(match[NEXT[joint] ?? ''] ?? '');
  const neck = byName.get(match.neck ?? '');
  const b = nextBone
    ? nextBone.getWorldPosition(new Vector3())
    : neck
      ? a.clone().add(a.clone().sub(neck.getWorldPosition(new Vector3())))
      : a.clone().add(new Vector3(0, 0.2 * ratio, 0));
  return { a, b };
}

/** A stored capsule list where the bones are now: on its joint's bone, in that bone's own frame as the document holds it. */
function capsulesInWorld(list: Capsule[], match: Record<string, string>): { joint: string; a: Vector3; b: Vector3; radius: number }[] {
  scene?.updateMatrixWorld(true);
  return list.flatMap((c) => {
    const bone = byName.get(match[c.joint] ?? '');
    if (!bone) return [];
    const world = (p: number[]) => new Vector3(p[0], p[1], p[2]).applyMatrix4(bone.matrixWorld);
    const ws = bone.getWorldScale(new Vector3());
    const scale = (Math.abs(ws.x) + Math.abs(ws.y) + Math.abs(ws.z)) / 3;
    return [{ joint: c.joint, a: world(c.a), b: world(c.b), radius: c.radius * scale }];
  });
}

/** The colliders where the bones are now: two ends and a radius each. The file's own when it set them (on their bones, the game's
 * X mirrored back), else the seven automatic ones switched on, each at the thickness measured on the model (`fit`) or the canonical
 * one scaled to the model's size (`ratio`: the model's size, measured now when not given). */
export function colliders(ratio?: number): { joint: string; a: Vector3; b: Vector3; radius: number }[] {
  const s = useCharacter.getState();
  const match = matchOf(s);
  if (s.capsules) return capsulesInWorld(s.capsules, match);
  // The body is its surface then (see shellPoints). The exported physics keeps the leg capsules (capsuleEntries); the preview leaves
  // them out: their push is aimed at the cloth's bones (which run inside the legs) and inflates the skirt and its white lining out
  // through the black even with the body still.
  if (s.shell?.on) return [];
  ratio ??= sizeRatio();
  const out: { joint: ColliderJoint; a: Vector3; b: Vector3; radius: number }[] = [];
  for (const joint of COLLIDER_JOINTS) {
    const c = s.colliders[joint];
    const axis = axisOf(joint, ratio);
    if (!c.on || !axis) continue;
    out.push({ joint, a: axis.a, b: axis.b, radius: (c.fit ?? COLLIDER_RADIUS[joint] * ratio) * c.scale });
  }
  return out;
}

/** The radius an automatic collider has now, in the model's metres (the panel's readout). */
export function autoColliderRadius(joint: ColliderJoint): number {
  const c = useCharacter.getState().colliders[joint];
  return (c.fit ?? COLLIDER_RADIUS[joint] * sizeRatio()) * c.scale;
}

/** A material named as hair, a loose garment, a hat, eyes or a trinket: nothing a body collider rests on. Tight clothes (tops,
 * trousers, boots, gloves) do count: the surface follows what is actually seen, so hair lies on the clothes rather than sinking into
 * them. Accessories matter most on the head: a large hat or a halo makes the head capsule swell and holds the hair off the model. */
const NOT_BODY = /hair|bang|fringe|ahoge|ponytail|twintail|braid|brow|lash|eye|iris|pupil|skirt|dress|robe|gown|cloak|cape|mantle|coat|veil|wing|ribbon|tail|strap|sash|scarf|muffler|hat|helm|hood|halo|horn|bell|charm|pendant|ornament|accessor|highlight|髪|かみ|前髪|横髪|後ろ髪|もみあげ|アホ毛|ツインテ|ポニテ|三つ編み|スカート|コート|マント|リボン|しっぽ|尾|ハイライト|ツノ|ホーン|裙|裾|下摆|带子|饰|披|飘|铃|挂|羽|帽|冠|兜|头巾|头盔|角/i;

/** Whether every material of a mesh is body surface (an unnamed one counts). */
function bodyMesh(mesh: SkinnedMesh): boolean {
  const material = mesh.material;
  return (Array.isArray(material) ? material : [material]).every((m) => !NOT_BODY.test(m?.name ?? ''));
}

/** How far the vertices whose skin `boneName` dominates lie from the segment a–b (world), sampled evenly over each body mesh (up to
 * 12000 a joint). A vertex partly pulled by the bone sits on another part: only the dominant influence counts. */
function axisDistances(boneName: string, a: Vector3, b: Vector3): number[] {
  const out: number[] = [];
  const ab = new Vector3().subVectors(b, a);
  const lengthSq = ab.lengthSq();
  const p = new Vector3();
  scene?.traverse((o) => {
    const mesh = o as SkinnedMesh;
    if (out.length >= 12000 || !mesh.isSkinnedMesh || !mesh.geometry || !bodyMesh(mesh)) return;
    const position = mesh.geometry.getAttribute('position');
    const skinIndex = mesh.geometry.getAttribute('skinIndex');
    const skinWeight = mesh.geometry.getAttribute('skinWeight');
    if (!position || !skinIndex || !skinWeight) return;
    const at = mesh.skeleton.bones.findIndex((bone) => bone.name === boneName);
    if (at < 0) return;
    const stride = Math.max(1, Math.floor(position.count / 24000));
    for (let i = 0; i < position.count && out.length < 12000; i += stride) {
      // The weights only pick out the vertices: the distance is measured on the skinned place, where the mesh is now.
      let best = -1;
      let weight = 0;
      const w0 = skinWeight.getX(i),
        w1 = skinWeight.getY(i),
        w2 = skinWeight.getZ(i),
        w3 = skinWeight.getW(i);
      if (w0 > weight) {
        weight = w0;
        best = skinIndex.getX(i);
      }
      if (w1 > weight) {
        weight = w1;
        best = skinIndex.getY(i);
      }
      if (w2 > weight) {
        weight = w2;
        best = skinIndex.getZ(i);
      }
      if (w3 > weight) {
        weight = w3;
        best = skinIndex.getW(i);
      }
      if (best !== at || weight < 0.5) continue;
      p.fromBufferAttribute(position, i);
      mesh.applyBoneTransform(i, p).applyMatrix4(mesh.matrixWorld);
      const t = lengthSq > 1e-12 ? Math.min(1, Math.max(0, ((p.x - a.x) * ab.x + (p.y - a.y) * ab.y + (p.z - a.z) * ab.z) / lengthSq)) : 0;
      out.push(Math.hypot(p.x - a.x - ab.x * t, p.y - a.y - ab.y * t, p.z - a.z - ab.z * t));
    }
  });
  return out;
}

/** The value `q` of the way through a list (0.7: below seven in ten of them). */
function quantile(values: number[], q: number): number {
  const sorted = [...values].sort((x, y) => x - y);
  return sorted[Math.min(sorted.length - 1, Math.max(0, Math.round(q * (sorted.length - 1))))]!;
}

/** Measures each automatic collider from the model's own mesh: the radius the skin under the joint's bone reaches from the joint's
 * axis (hair, clothes and eyes left out, so a hairdo does not widen the head), kept within half to once and a half the canonical
 * radius. `resetScale` gives every collider its measured base size (the sliders' ×1); otherwise the author's scale stays over it.
 * Returns how many joints were measured. */
export async function fitColliders(announce = false, resetScale = false): Promise<number> {
  const s = useCharacter.getState();
  if (!scene || s.capsules) return 0;
  const gen = ++fitRun.colliders;
  restNow();
  const match = matchOf(s);
  const ratio = sizeRatio();
  const next = { ...s.colliders };
  let fitted = 0;
  for (const joint of COLLIDER_JOINTS) {
    await breathe();
    if (gen !== fitRun.colliders) return 0;
    restNow();
    const name = match[joint];
    const axis = axisOf(joint, ratio);
    const distances = name && axis ? axisDistances(name, axis.a, axis.b) : [];
    if (distances.length < 40) {
      next[joint] = { ...next[joint], fit: undefined, scale: resetScale ? 1 : next[joint].scale };
      continue;
    }
    const base = COLLIDER_RADIUS[joint] * ratio;
    const fit = Math.min(base * 1.6, Math.max(base * 0.5, quantile(distances, 0.7)));
    next[joint] = { ...next[joint], fit: Math.min(0.35, Math.max(0.015, fit)), scale: resetScale ? 1 : next[joint].scale };
    fitted++;
  }
  if (gen !== fitRun.colliders) return 0;
  useCharacter.setState({ colliders: next });
  if (announce) toast(fitted ? 'ok' : 'info', t(fitted ? 'character.collidersFitted' : 'character.collidersFitNone', { v0: fitted }));
  return fitted;
}

/** What the surface fit does; a work measured by an older fit is measured again when it opens. Version 6 is the model's own
 * triangles over its own vertices (see fitShell): the collider is the picked materials' surface itself, so nothing slips between two
 * vertices — the surface between them is the collider too. Version 8 measures every chunk at the bind pose (a clip playing while
 * the measurement runs used to leave a surface half in one pose and half in another, drawn offset from the model). Version 9
 * rides the finger joints, so a hand's surface bends with the fingers instead of staying rigid to the hand bone. */
const SHELL_VERSION = 9;
/** The spacing points are welded at in a joint's own space, metres: the seam and split vertices of one surface become one point. */
const SHELL_WELD = 0.0005;
/** The joints cloth sets collide with get triangle-interior samples this far apart (metres), so a cloth vertex between two mesh
 * vertices still meets the surface there; the other joints keep their vertices. */
const SHELL_TRI_SPACING = 0.01;
const SHELL_DENSE = new Set(['hip', 'spine1', 'spine2', 'leftUpperLeg', 'leftLowerLeg', 'leftFoot', 'rightUpperLeg', 'rightLowerLeg', 'rightFoot']);

/** Base64 of bytes, little-endian float32 data as the document holds it. */
function base64Bytes(bytes: Uint8Array): string {
  let text = '';
  for (let i = 0; i < bytes.length; i += 0x8000) text += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(text);
}

/** Material names that are hair or eyes: never the naked body, whatever they cover. */
const NOT_BODY_NAME = /hair|bang|fringe|ahoge|ponytail|twintail|braid|brow|lash|eye|iris|pupil|髪|かみ|前髪|横髪|後ろ髪|もみあげ|アホ毛|ツインテ|ポニテ|三つ編み|ハイライト|瞳孔|眼白|目玉|瞳|头发|髮/i;
/** Material names that are skin even where the mesh does not reach the fingertips: face, neck, skin, body. */
const SKIN_NAME = /skin|body|face|顔|颜|脸|肌|膚|肤|neck|bozi|脖子|首/i;

/** The naked body's materials, recognized from the mesh rather than picked by hand: the skin is the only surface that rides the
 * fingers and toes and spans the whole skeleton, so the material with that reach is the body and the rest (clothes, hair, eyes) is
 * not. A face or neck material (named as one) joins it. A model whose skin is all hidden under a full suit falls back to the material
 * covering the most joints. Cached per model: it reads every vertex's weights once. */
let bodyPartsRun = -1;
let bodyPartsCache: string[] = [];
export function autoBodyParts(): string[] {
  if (!scene) return [];
  if (bodyPartsRun === sceneVersion) return bodyPartsCache;
  bodyPartsRun = sceneVersion;
  const match = oneJointPerBone(matchOf(useCharacter.getState()));
  const jointOfBone = new Map(Object.entries(match).map(([joint, bone]) => [bone, joint] as const));
  const parentOfBone = new Map<Bone, Bone | null>();
  scene.traverse((o) => {
    if (isBone(o)) parentOfBone.set(o, o.parent && isBone(o.parent) ? o.parent : null);
  });
  const tip = (joint: string) => /Finger|Thumb|Index|Middle|Ring|Pinky/.test(joint) || joint === 'leftFoot' || joint === 'rightFoot';
  const found = new Map<string, { joints: Set<string>; tip: boolean }>();
  scene.traverse((o) => {
    const mesh = o as SkinnedMesh;
    if (!mesh.isSkinnedMesh || !mesh.geometry) return;
    const position = mesh.geometry.getAttribute('position');
    const skinIndex = mesh.geometry.getAttribute('skinIndex');
    const skinWeight = mesh.geometry.getAttribute('skinWeight');
    if (!position || !skinIndex || !skinWeight) return;
    const names = (Array.isArray(mesh.material) ? mesh.material : [mesh.material]).map((m) => m?.name).filter((n): n is string => !!n);
    if (!names.length) return;
    for (const name of names) if (!found.has(name)) found.set(name, { joints: new Set(), tip: false });
    const bones = mesh.skeleton.bones;
    for (let i = 0; i < position.count; i++) {
      // The influence with the most weight, walked up to the nearest matched body joint.
      let best = -1,
        bestWeight = 0;
      for (let q = 0; q < 4; q++) {
        const w = skinWeight.getComponent(i, q);
        if (w > bestWeight) {
          bestWeight = w;
          best = skinIndex.getComponent(i, q);
        }
      }
      let owner: string | null = null;
      for (let at: Bone | null = bones[best] ?? null; at; at = parentOfBone.get(at) ?? null) {
        const joint = jointOfBone.get(at.name);
        if (joint) {
          owner = joint;
          break;
        }
      }
      if (!owner) continue;
      for (const name of names) {
        const f = found.get(name)!;
        f.joints.add(owner);
        if (tip(owner)) f.tip = true;
      }
    }
  });
  let body = [...found].filter(([, f]) => f.tip).map(([name]) => name);
  if (!body.length) {
    let most = 0;
    for (const [, f] of found) most = Math.max(most, f.joints.size);
    if (most) body = [...found].filter(([, f]) => f.joints.size === most).map(([name]) => name);
  }
  const parts = new Set<string>(body);
  for (const name of found.keys()) if (SKIN_NAME.test(name)) parts.add(name);
  for (const name of parts) if (NOT_BODY_NAME.test(name)) parts.delete(name);
  bodyPartsCache = [...parts];
  return bodyPartsCache;
}

/** The materials whose surface the collider is: the author's picks (the panel's switches, a click on the part itself), or the naked
 * body recognized from the mesh when the author has not picked yet (autoBodyParts: skin, face and neck; clothes, hair and eyes left
 * out). */
export function shellParts(): string[] {
  const s = useCharacter.getState();
  // An empty list is not a choice (the author can unswitch every material and the surface is off, not "no materials"): a save made
  // while the scene had no body materials yet left one, and every later fit measured nothing and saved the same empty surface again.
  if (s.shell?.parts?.length) return s.shell.parts;
  return autoBodyParts();
}

/** Adds a material to the collider's parts, or takes it out, and measures the surface again: the parts the author clicks on the model
 * or switches in the list. */
export function toggleShellPart(name: string): void {
  const s = useCharacter.getState();
  if (!s.shell) return;
  const parts = new Set(shellParts());
  if (parts.has(name)) parts.delete(name);
  else parts.add(name);
  useCharacter.setState({ shell: { ...s.shell, parts: [...parts] } });
  void fitShell();
}

/** Measures the body as its own surface: every picked material's triangles, split by the body joint that carries them and taken into
 * that joint's bone space, the vertices as floats with their outward normals (x, y, z, nx, ny, nz, base64) and the triangles as uint32
 * point indices (base64). A chain node is kept out of whole triangles, not out of vertices alone: a cloth node between three vertices
 * still meets a surface there, which is what keeps it from slipping through the hole a sparse point cloud leaves. Returns the number
 * of vertices. */
export async function fitShell(announce = false): Promise<number> {
  const s = useCharacter.getState();
  if (!scene) return 0;
  const gen = ++fitRun.shell;
  // The surface is measured in the bind pose whatever the preview is playing: a part clicked mid-clip is still the part it is at rest.
  restNow();
  const match = matchOf(s);
  // Every joint the surface can ride, fingers included: a hand's skin bends with three finger bones, and a hand-shaped surface
  // held rigid to the hand bone alone no longer fits the fingers as soon as they curl (a dance moves them constantly).
  const joints = [...BODY_JOINTS, ...FINGER_JOINTS].filter((joint) => byName.has(match[joint] ?? ''));
  const bodyJoints = new Set(joints);
  const boneOf = new Map<string, Bone>(joints.map((joint) => [joint, byName.get(match[joint]!)!]));
  const anyJointOfBone = new Map<string, string>(Object.entries(match).map(([joint, bone]) => [bone, joint] as const));
  const parentOfBone = new Map<Bone, Bone | null>();
  scene.traverse((o) => {
    if (isBone(o)) parentOfBone.set(o, o.parent && isBone(o.parent) ? o.parent : null);
  });
  const inverseOf = new Map<Bone, Matrix4>();
  const inverse = (bone: Bone): Matrix4 => {
    let m = inverseOf.get(bone);
    if (!m) inverseOf.set(bone, (m = new Matrix4().copy(bone.matrixWorld).invert()));
    return m;
  };
  // A normal goes into the bone's space by the bone's world rotation only (no scale, no translation).
  const rotationOf = new Map<string, Quaternion>(joints.map((joint) => [joint, boneOf.get(joint)!.getWorldQuaternion(new Quaternion()).invert()]));
  const jointMiddle = new Map<string, Vector3>(joints.map((joint) => [joint, boneOf.get(joint)!.getWorldPosition(new Vector3())]));
  interface Part {
    points: Vector3[];
    normals: Vector3[];
    weld: Map<string, number>;
    tris: number[];
  }
  const parts = new Map<string, Part>(joints.map((joint) => [joint, { points: [], normals: [], weld: new Map(), tris: [] }]));
  const p = new Vector3(),
    n = new Vector3(),
    world = new Vector3(),
    normalWorld = new Vector3(),
    middle = new Vector3(),
    latticeNormal = new Vector3();
  // One point per welded vertex of a joint's surface; a triangle names its three (a sliver whose corners weld together is dropped).
  const vertex = (joint: string, at: Vector3, facing: Vector3): number => {
    const part = parts.get(joint)!;
    p.copy(at).applyMatrix4(inverse(boneOf.get(joint)!));
    const key = `${Math.round(p.x / SHELL_WELD)},${Math.round(p.y / SHELL_WELD)},${Math.round(p.z / SHELL_WELD)}`;
    const had = part.weld.get(key);
    if (had !== undefined) return had;
    const id = part.points.length;
    part.weld.set(key, id);
    n.copy(facing).applyQuaternion(rotationOf.get(joint)!);
    if (n.lengthSq() < 1e-8) n.set(0, 1, 0);
    else n.normalize();
    part.points.push(p.clone());
    part.normals.push(n.clone());
    return id;
  };
  const picked = new Set(shellParts());
  const a = new Vector3(),
    b = new Vector3(),
    c = new Vector3(),
    ab = new Vector3(),
    ac = new Vector3();
  let since = performance.now(),
    seen = 0;
  for (const o of (() => {
    const all: Object3D[] = [];
    scene!.traverse((x) => all.push(x));
    return all;
  })()) {
    const mesh = o as SkinnedMesh;
    if (!mesh.isSkinnedMesh || !mesh.geometry) continue;
    const materials = Array.isArray(mesh.material) ? mesh.material : [mesh.material];
    if (!materials.some((m) => picked.has(m?.name ?? ''))) continue;
    const position = mesh.geometry.getAttribute('position');
    const normal = mesh.geometry.getAttribute('normal');
    const skinIndex = mesh.geometry.getAttribute('skinIndex');
    const skinWeight = mesh.geometry.getAttribute('skinWeight');
    const index = mesh.geometry.getIndex();
    if (!position || !skinIndex || !skinWeight || !index) continue;
    const bones = mesh.skeleton.bones;
    const jointAt: (string | null)[] = new Array(position.count).fill(null);
    const weightAt = new Float32Array(position.count);
    for (let i = 0; i < position.count; i++) {
      if ((++seen & 0x3fff) === 0 && performance.now() - since > SLICE_MS) {
        await breathe();
        if (gen !== fitRun.shell) return 0;
        restNow();
        since = performance.now();
      }
      // Every influence the vertex has, summed per body joint (a finger's or a twist bone's share belongs to the hand or the limb the
      // bone hangs from); the joint carrying most of it carries the vertex. Demanding one bone hold half the weight dropped leg and
      // hip skin whose weights are spread over MMD's deform bones, which left holes in the collider there.
      const shares = new Map<string, number>();
      for (let q = 0; q < 4; q++) {
        const w = skinWeight.getComponent(i, q);
        if (w <= 0) continue;
        const bone = bones[skinIndex.getComponent(i, q)];
        if (!bone) continue;
        for (let at: Bone | null = bone; at; at = parentOfBone.get(at) ?? null) {
          const owner = anyJointOfBone.get(at.name);
          if (!owner) continue;
          if (bodyJoints.has(owner)) shares.set(owner, (shares.get(owner) ?? 0) + w);
          break;
        }
      }
      let bestJoint: string | null = null,
        bestShare = 0;
      for (const [joint, share] of shares)
        if (share > bestShare) {
          bestJoint = joint;
          bestShare = share;
        }
      jointAt[i] = bestJoint;
      weightAt[i] = bestShare;
    }
    if (!jointAt.some(Boolean)) continue;
    // Where every vertex stands in its bind skin (world), and the way its surface faces there: the triangles interpolate both.
    const worldAt = new Float32Array(position.count * 3);
    const facingAt = new Float32Array(position.count * 3);
    for (let i = 0; i < position.count; i++) {
      if ((++seen & 0x3fff) === 0 && performance.now() - since > SLICE_MS) {
        await breathe();
        if (gen !== fitRun.shell) return 0;
        restNow();
        since = performance.now();
      }
      world.fromBufferAttribute(position, i);
      mesh.applyBoneTransform(i, world).applyMatrix4(mesh.matrixWorld);
      worldAt[i * 3] = world.x;
      worldAt[i * 3 + 1] = world.y;
      worldAt[i * 3 + 2] = world.z;
      if (normal) {
        n.fromBufferAttribute(normal, i).transformDirection(mesh.matrixWorld);
        if (n.lengthSq() < 1e-8) n.set(0, 1, 0);
        else n.normalize();
      } else n.set(0, 1, 0);
      facingAt[i * 3] = n.x;
      facingAt[i * 3 + 1] = n.y;
      facingAt[i * 3 + 2] = n.z;
    }
    for (let t = 0; t + 2 < index.count; t += 3) {
      if ((++seen & 0x7ff) === 0 && performance.now() - since > SLICE_MS) {
        await breathe();
        if (gen !== fitRun.shell) return 0;
        restNow();
        since = performance.now();
      }
      const ia = index.getX(t),
        ib = index.getX(t + 1),
        ic = index.getX(t + 2);
      a.set(worldAt[ia * 3]!, worldAt[ia * 3 + 1]!, worldAt[ia * 3 + 2]!);
      b.set(worldAt[ib * 3]!, worldAt[ib * 3 + 1]!, worldAt[ib * 3 + 2]!);
      c.set(worldAt[ic * 3]!, worldAt[ic * 3 + 1]!, worldAt[ic * 3 + 2]!);
      if (ab.copy(b).sub(a).cross(ac.copy(c).sub(a)).lengthSq() < 1e-16) continue; // no area: no surface there
      const ja = jointAt[ia],
        jb = jointAt[ib],
        jc = jointAt[ic];
      // The joint that carries the triangle: the one two of its corners belong to (a seam's triangle still lies on the surface either
      // side), else the corner with the strongest share, else the body joint nearest its middle — a picked material's triangle is
      // surface wherever it lies, and leaving it out is a hole in the collider.
      let joint: string | null = ja && ja === jb ? ja : jb && jb === jc ? jb : ja && ja === jc ? ja : null;
      if (!joint) {
        const choices = ([[ja, weightAt[ia]], [jb, weightAt[ib]], [jc, weightAt[ic]]] as [string | null, number][])
          .filter((x): x is [string, number] => !!x[0])
          .sort((x, y) => y[1] - x[1]);
        joint = choices.length ? choices[0]![0] : null;
      }
      if (!joint) {
        middle.copy(a).add(b).add(c).multiplyScalar(1 / 3);
        let nearestD = Infinity;
        for (const [name, at] of jointMiddle) {
          const d = at.distanceToSquared(middle);
          if (d < nearestD) {
            nearestD = d;
            joint = name;
          }
        }
      }
      if (!joint) continue;
      const ta = vertex(joint, a, normalWorld.set(facingAt[ia * 3]!, facingAt[ia * 3 + 1]!, facingAt[ia * 3 + 2]!)),
        tb = vertex(joint, b, normalWorld.set(facingAt[ib * 3]!, facingAt[ib * 3 + 1]!, facingAt[ib * 3 + 2]!)),
        tc = vertex(joint, c, normalWorld.set(facingAt[ic * 3]!, facingAt[ic * 3 + 1]!, facingAt[ic * 3 + 2]!));
      if (ta === tb || tb === tc || ta === tc) continue;
      parts.get(joint)!.tris.push(ta, tb, tc);
      if (SHELL_DENSE.has(joint)) {
        const area = 0.5 * ab.copy(b).sub(a).cross(ac.copy(c).sub(a)).length();
        const steps = Math.max(1, Math.ceil(Math.sqrt(2 * area) / SHELL_TRI_SPACING));
        for (let i = 0; i <= steps; i++)
          for (let j = 0; j <= steps - i; j++) {
            const la = (steps - i - j) / steps,
              lb = i / steps,
              lc = j / steps;
            if (la <= 0 && lb <= 0) continue;
            middle.copy(a).multiplyScalar(la).addScaledVector(b, lb).addScaledVector(c, lc);
            latticeNormal.set(
              facingAt[ia * 3]! * la + facingAt[ib * 3]! * lb + facingAt[ic * 3]! * lc,
              facingAt[ia * 3 + 1]! * la + facingAt[ib * 3 + 1]! * lb + facingAt[ic * 3 + 1]! * lc,
              facingAt[ia * 3 + 2]! * la + facingAt[ib * 3 + 2]! * lb + facingAt[ic * 3 + 2]! * lc,
            );
            vertex(joint, middle, latticeNormal);
          }
      }
    }
  }
  let count = 0,
    triangles = 0;
  const entries: { joint: string; data: string; tris: string }[] = [];
  for (const joint of joints) {
    await breathe();
    if (gen !== fitRun.shell) return 0;
    const part = parts.get(joint)!;
    if (part.tris.length < 3) continue;
    const data = new Float32Array(part.points.length * 6);
    for (let i = 0; i < part.points.length; i++) {
      const q = part.points[i]!,
        w = part.normals[i]!;
      // As sampled: a bone's local frame is the same in the game as here (a model-wide mirror cancels out of local frames), so the
      // points need no axis change on the way to the game. (The old X negation made the game's surfaces mirrored inside each bone —
      // the editor preview undid it, the game did not, and the cloth was shoved around by surfaces that were not there.)
      data[i * 6] = q.x;
      data[i * 6 + 1] = q.y;
      data[i * 6 + 2] = q.z;
      data[i * 6 + 3] = w.x;
      data[i * 6 + 4] = w.y;
      data[i * 6 + 5] = w.z;
      count++;
    }
    entries.push({ joint, data: base64Bytes(new Uint8Array(data.buffer)), tris: base64Bytes(new Uint8Array(new Uint32Array(part.tris).buffer)) });
    triangles += part.tris.length / 3;
  }
  if (gen !== fitRun.shell) return 0;
  const on = count ? (s.shell?.joints.length ? s.shell.on : true) : false;
  useCharacter.setState({ shell: { on, parts: [...picked], joints: entries, points: count, triangles, version: SHELL_VERSION } });
  if (announce) toast(count ? 'ok' : 'info', t(count ? 'character.shellFitted' : 'character.shellNone', { v0: count, v1: triangles }));
  return count;
}

/** The body's surface where the bones are now, for drawing it: per joint its bone, its vertices and their normals in that bone's
 * space and the triangles over them (null for a shell from before version 6), or nothing when the surface is off (it stays what the
 * colliders are measured off, so it is drawn even while they are exports of it). */
export function shellPoints(): { joint: string; bone: Bone; points: Float32Array; normals: Float32Array; tris: Uint32Array | null }[] {
  const s = useCharacter.getState();
  if (!s.shell?.on) return [];
  const match = matchOf(s);
  return s.shell.joints.flatMap(({ joint, data, tris }) => {
    const bone = byName.get(match[joint] ?? '');
    if (!bone) return [];
    const bytes = Uint8Array.from(atob(data), (c) => c.charCodeAt(0));
    const values = new Float32Array(bytes.buffer);
    const count = values.length / 6;
    const points = new Float32Array(count * 3),
      normals = new Float32Array(count * 3);
    for (let i = 0; i < count; i++) {
      points[i * 3] = values[i * 6]!;
      points[i * 3 + 1] = values[i * 6 + 1]!;
      points[i * 3 + 2] = values[i * 6 + 2]!;
      normals[i * 3] = values[i * 6 + 3]!;
      normals[i * 3 + 1] = values[i * 6 + 4]!;
      normals[i * 3 + 2] = values[i * 6 + 5]!;
    }
    const triBytes = tris ? Uint8Array.from(atob(tris), (c) => c.charCodeAt(0)) : null;
    const triValues = triBytes && triBytes.byteLength % 4 === 0 ? new Uint32Array(triBytes.buffer) : null;
    return [{ joint, bone, points, normals, tris: triValues }];
  });
}

/** Per bone, the vertices it moves most (a weight of a half or more), in its own bind-pose space (x, y, z each): the cloth each bone
 * carries. Measured once per model, from the bind data alone, so a pose playing when it is asked for does not move it. */
const carried = new WeakMap<Group, Map<Bone, number[]>>();
function carriedBy(): Map<Bone, number[]> {
  const had = scene && carried.get(scene);
  if (!scene || had) return had ?? new Map();
  const verts = new Map<Bone, number[]>();
  const p = new Vector3();
  scene.traverse((o) => {
    const mesh = o as SkinnedMesh;
    if (!mesh.isSkinnedMesh || !mesh.geometry || !mesh.skeleton) return;
    const position = mesh.geometry.getAttribute('position');
    const skinIndex = mesh.geometry.getAttribute('skinIndex');
    const skinWeight = mesh.geometry.getAttribute('skinWeight');
    if (!position || !skinIndex || !skinWeight) return;
    // bone space = boneInverse x bindMatrix x the vertex as stored
    const toBone = mesh.skeleton.boneInverses.map((inverse) => new Matrix4().multiplyMatrices(inverse, mesh.bindMatrix));
    for (let i = 0; i < position.count; i++) {
      let best = skinIndex.getX(i),
        weight = skinWeight.getX(i);
      if (skinWeight.getY(i) > weight) (weight = skinWeight.getY(i)), (best = skinIndex.getY(i));
      if (skinWeight.getZ(i) > weight) (weight = skinWeight.getZ(i)), (best = skinIndex.getZ(i));
      if (skinWeight.getW(i) > weight) (weight = skinWeight.getW(i)), (best = skinIndex.getW(i));
      const bone = mesh.skeleton.bones[best];
      if (weight < 0.5 || !bone || !toBone[best]) continue;
      p.fromBufferAttribute(position, i).applyMatrix4(toBone[best]!);
      const list = verts.get(bone) ?? [];
      list.push(p.x, p.y, p.z);
      verts.set(bone, list);
    }
  });
  carried.set(scene, verts);
  return verts;
}

/** Fabric points a bone's cloth is sampled at: across the panel's width (its widest spread round the bone) and along the bone. */
const ACROSS = 3,
  ALONG = 2;

/** Where the cloth a strand's bones carry lies, per bone (null: it carries none, or its piece cannot be measured): points of `t` of the
 * way from the bone to the next one (past the last, to its tip), then (x, y, z) off that point in the bone's own space — a frame the
 * game shares (Core/Character.cs SpringChain.Fabric). Each point is the middle of a part of the cloth, ACROSS its width by ALONG the
 * bone, so a leg that comes up at a panel's edge meets a point there. Read from the bind pose: a strand runs parent to child. */
function fabricOf(bones: Bone[]): (number[] | null)[] {
  const all = carriedBy();
  const local = (bone: Bone) => new Matrix4().compose(bindPosition.get(bone) ?? bone.position, bindLocal.get(bone) ?? bone.quaternion, bone.scale);
  return bones.map((bone, j) => {
    const v = all.get(bone);
    if (!v || v.length < 9) return null;
    const next = bones[j + 1],
      before = bones[j - 1];
    let tip: Vector3 | null = null;
    if (next?.parent === bone) tip = (bindPosition.get(next) ?? next.position).clone();
    else if (!next && before && bone.parent === before) tip = new Vector3().applyMatrix4(local(bone).invert()).negate();
    if (!tip || tip.lengthSq() < 1e-12) {
      console.warn(`fabric: ${bone.name} is not a child of the bone above it in its strand; its cloth is not measured`);
      return null;
    }
    // Each vertex as t along the piece and its offset off it; the widest way the offsets spread (a power iteration on their spread).
    const n = v.length / 3,
      lengthSq = tip.lengthSq(),
      ts = new Float64Array(n),
      off = new Float64Array(v.length),
      mean = [0, 0, 0];
    for (let i = 0; i < n; i++) {
      const t = Math.min(1, Math.max(0, (v[i * 3]! * tip.x + v[i * 3 + 1]! * tip.y + v[i * 3 + 2]! * tip.z) / lengthSq));
      ts[i] = t;
      off[i * 3] = v[i * 3]! - tip.x * t;
      off[i * 3 + 1] = v[i * 3 + 1]! - tip.y * t;
      off[i * 3 + 2] = v[i * 3 + 2]! - tip.z * t;
      for (let c = 0; c < 3; c++) mean[c]! += off[i * 3 + c]! / n;
    }
    let wide = [1, 0.5, 0.25];
    for (let round = 0; round < 12; round++) {
      const sum = [0, 0, 0];
      for (let i = 0; i < n; i++) {
        const dx = off[i * 3]! - mean[0]!,
          dy = off[i * 3 + 1]! - mean[1]!,
          dz = off[i * 3 + 2]! - mean[2]!;
        const along = dx * wide[0]! + dy * wide[1]! + dz * wide[2]!;
        sum[0]! += dx * along;
        sum[1]! += dy * along;
        sum[2]! += dz * along;
      }
      const l = Math.hypot(sum[0]!, sum[1]!, sum[2]!);
      if (l < 1e-12) break;
      wide = sum.map((x) => x / l);
    }
    // Parts by rank across the width and along the bone; each part's middle is a point.
    const across = new Float64Array(n);
    for (let i = 0; i < n; i++) across[i] = (off[i * 3]! - mean[0]!) * wide[0]! + (off[i * 3 + 1]! - mean[1]!) * wide[1]! + (off[i * 3 + 2]! - mean[2]!) * wide[2]!;
    const rank = (values: Float64Array, parts: number) => {
      const part = new Uint8Array(n);
      [...values.keys()].sort((a, b) => values[a]! - values[b]!).forEach((i, r) => (part[i] = Math.min(parts - 1, Math.floor((r * parts) / n))));
      return part;
    };
    const side = rank(across, ACROSS),
      down = rank(ts, ALONG);
    const out: number[] = [];
    for (let a = 0; a < ACROSS; a++)
      for (let b = 0; b < ALONG; b++) {
        const sum = [0, 0, 0, 0];
        let count = 0;
        for (let i = 0; i < n; i++) {
          if (side[i] !== a || down[i] !== b) continue;
          sum[0]! += ts[i]!;
          sum[1]! += off[i * 3]!;
          sum[2]! += off[i * 3 + 1]!;
          sum[3]! += off[i * 3 + 2]!;
          count++;
        }
        if (count >= 3) out.push(...sum.map((q) => Math.round((q / count) * 1e5) / 1e5));
      }
    return out.length ? out : null;
  });
}

/** A cloth set's fabric as the document holds it: bone name to its points, t, x, y, z each (fabricOf). Hair and tails carry none: their strands
 * are kept out of the body themselves. */
function fabricEntry(spring: Spring): Record<string, number[]> | undefined {
  if (spring.kind !== 'skirt' || !scene) return undefined;
  const out: Record<string, number[]> = {};
  for (const names of spring.strands) {
    const bones = names.map((n) => byName.get(n)).filter((b): b is Bone => !!b);
    fabricOf(bones).forEach((f, j) => f && (out[bones[j]!.name] = f));
  }
  return Object.keys(out).length ? out : undefined;
}

/** One set's chain at rest: per strand its bones and a tip past the last one, as nodes in the anchor's frame, and the tip's place
 * in the last bone's frame (where the drawn bone points). */
interface Chain {
  spring: Spring;
  anchor: Object3D;
  strands: { bones: Bone[]; nodes: number[]; tip: Vector3 }[];
  nodes: { parent: number; at: number[]; fabric?: number[] }[];
}

function chainOf(spring: Spring): Chain | null {
  const anchor = anchorOf(spring);
  if (!scene || !anchor) return null;
  scene.updateMatrixWorld(true);
  const pos = new Vector3(),
    rot = new Quaternion(),
    scl = new Vector3();
  anchor.matrixWorld.decompose(pos, rot, scl);
  const inverse = rot.clone().invert();
  const local = (w: Vector3) => w.clone().sub(pos).applyQuaternion(inverse).toArray();
  const nodes: Chain['nodes'] = [];
  const strands: Chain['strands'] = [];
  for (const names of spring.strands) {
    const bones = names.map((n) => byName.get(n)).filter((b): b is Bone => !!b);
    if (!bones.length) continue;
    const at = bones.map((b) => b.getWorldPosition(new Vector3()));
    const last = at[at.length - 1];
    const before = at.length > 1 ? at[at.length - 2] : (bones[0].parent?.getWorldPosition(new Vector3()) ?? last.clone().add(new Vector3(0, 0.05, 0)));
    const step = last.clone().sub(before);
    const tip = last.clone().add(step.lengthSq() > 1e-10 ? step : new Vector3(0, -0.05, 0));
    // A cloth set's fabric per piece: t, then the bone-space offset carried into the anchor's frame (the bone's world turn and
    // scale, less the anchor's turn), as the game does with the same document values (Native/Chains.cs).
    const fabric = spring.kind === 'skirt' ? fabricOf(bones) : [];
    const ids: number[] = [];
    [...at, tip].forEach((w, i) => {
      ids.push(nodes.length);
      const f = i > 0 ? fabric[i - 1] : null;
      const turn = f && new Matrix3().setFromMatrix4(bones[i - 1]!.matrixWorld);
      const points: number[] = [];
      for (let k = 0; f && turn && k + 3 < f.length; k += 4) {
        const off = new Vector3(f[k + 1], f[k + 2], f[k + 3]).applyMatrix3(turn).applyQuaternion(inverse);
        points.push(f[k]!, off.x, off.y, off.z);
      }
      nodes.push({ parent: i === 0 ? -1 : nodes.length - 1, at: local(w), ...(points.length ? { fabric: points } : {}) });
    });
    strands.push({ bones, nodes: ids, tip: bones[bones.length - 1].worldToLocal(tip.clone()) });
  }
  return strands.length ? { spring, anchor, strands, nodes } : null;
}

/** A set's preview: node positions per frame in the still pose's space, played from `started`. */
export interface SpringFrames {
  chain: Chain;
  frames: Float32Array<ArrayBuffer>[];
  started: number;
}

const floats = (base64: string) => {
  const bytes = Uint8Array.from(atob(base64), (c) => c.charCodeAt(0));
  return new Float32Array(bytes.buffer);
};

/** Held still from the bound pose with no clip or motion: hair and skirts drop under gravity and come to rest as in the game. */
const STILL_SECONDS = 8;

/** Asks the backend to move every set through `motion` (and SETTLE_SECONDS held still), through the `clip` frames (`at` poses the
 * model at frame i, PREVIEW_FPS frames a second), or held still for STILL_SECONDS. A motion drives the model through a moving space
 * the view does not show; the frames come back in the still one, so the model stays put on screen. */
export async function simulate(motion: CharmMotion | null, clip?: { count: number; at(frame: number): void }): Promise<SpringFrames[]> {
  const s = useCharacter.getState();
  if (!scene || !s.springs.length) return [];
  restPose();
  const chains = s.springs.map(chainOf);
  // Each set's anchor where the model rests: with the shells' bind poses, it is where every node's standoff from the body is
  // measured — the clip's first frame can be a whole pose away from the bind, and depths measured across that gap fit nothing.
  const bases = chains.map((c) => (c ? c.anchor.matrixWorld.elements.slice() : null));
  const ratio = sizeRatio();
  // The body faces +Z (glTF); the motions run along a root's -Z, so a stand-in root turned round leads them.
  const lead = new Object3D();
  lead.rotation.y = Math.PI;
  lead.position.copy(
    byName
      .get(matchOf(s).hip ?? '')
      ?.getWorldPosition(new Vector3())
      .setY(0) ?? new Vector3(),
  );
  lead.updateMatrixWorld(true);
  const count = clip ? clip.count : Math.ceil((motion ? MOTION_SECONDS[motion] + SETTLE_SECONDS : STILL_SECONDS) * PREVIEW_FPS) + 1;
  // The body's surface, when it is what the chains are kept out of: per joint its points and its bone, whose world matrix each frame
  // poses the surface. A body whose colliders are capsules is kept out of those alone — the game collides the capsules, and the
  // preview has to answer for the same body the game will, or a clearance the editor shows is one the game does not have.
  const shell = shellPoints();
  const shellBones = shell.map((x) => x.bone);
  // The backend works in the same world as the nodes: the points go in unmirrored (the document holds the game's mirrored axes).
  // Each surface's bind pose goes with it: the depth the chains keep from it is measured there, not on a clip's first frame.
  const shellDoc = shell.map(({ joint, bone, points, normals, tris }) => {
    const data = new Float32Array((points.length / 3) * 6);
    for (let i = 0, k = 0; i < points.length; i += 3, k += 6) {
      data[k] = points[i]!;
      data[k + 1] = points[i + 1]!;
      data[k + 2] = points[i + 2]!;
      data[k + 3] = normals[i]!;
      data[k + 4] = normals[i + 1]!;
      data[k + 5] = normals[i + 2]!;
    }
    bone.updateWorldMatrix(true, false);
    return {
      joint,
      data: base64Bytes(new Uint8Array(data.buffer)),
      tris: tris ? base64Bytes(new Uint8Array(tris.buffer)) : undefined,
      rest: new Matrix4().copy(bone.matrixWorld).elements.slice(),
    };
  });
  // Each set holds only the surfaces it can reach: those whose bounds meet its own nodes at rest, grown by what it can swing past
  // them (a ponytail meets the head and the back, not the feet). A set holding the whole body would multiply the surface by the
  // number of sets in every pass of the solve — and the preview with it.
  const margin = 0.25 * ratio;
  const partBox = shell.map(({ bone, points }) => {
    bone.updateWorldMatrix(true, false);
    const box = new Box3();
    const at = new Vector3();
    for (let i = 0; i < points.length; i += 3)
      box.expandByPoint(at.set(points[i]!, points[i + 1]!, points[i + 2]!).applyMatrix4(bone.matrixWorld));
    return box;
  });
  const keep = chains.map((chain) => {
    const box = new Box3();
    if (chain) {
      const at = new Vector3();
      const m = chain.anchor.matrixWorld;
      for (const n of chain.nodes) box.expandByPoint(at.set(n.at[0]!, n.at[1]!, n.at[2]!).applyMatrix4(m));
      box.expandByScalar(margin);
    }
    return partBox.map((b) => !!chain && b.intersectsBox(box));
  });
  const motions: Matrix4[] = [];
  // Per set, per frame: its anchor bone's frame, the colliders and the surface, all where this frame puts them.
  const sampled = chains.map(() => [] as { time: number; m: number[]; body: number[]; capsules: number[][]; shells: number[][] }[]);
  for (let i = 0; i < count; i++) {
    const m = new Matrix4(),
      body = [0, 0, 0];
    if (clip) clip.at(i);
    else if (motion) motionMatrix(m, lead, motion, Math.min(i / PREVIEW_FPS, MOTION_SECONDS[motion] - 1e-6), body, 0);
    scene.updateMatrixWorld(true);
    motions.push(m);
    const capsules = colliders(ratio).map((c) => [...c.a.applyMatrix4(m).toArray(), ...c.b.applyMatrix4(m).toArray(), c.radius]);
    const allShells = shellBones.map((bone) => new Matrix4().multiplyMatrices(m, bone.matrixWorld).elements.slice());
    chains.forEach(
      (chain, k) =>
        chain &&
        sampled[k].push({
          time: i / PREVIEW_FPS,
          m: new Matrix4().multiplyMatrices(m, chain.anchor.matrixWorld).elements.slice(),
          body,
          capsules,
          shells: allShells.filter((_, part) => keep[k]![part]),
        }),
    );
  }
  const results = await Promise.all(
    s.springs.map(async (spring, k) => {
      const chain = chains[k];
      if (!chain) return null;
      const frames = sampled[k];
      const worn = chain.anchor.getWorldScale(new Vector3());
      const scale = (Math.abs(worn.x) + Math.abs(worn.y) + Math.abs(worn.z)) / 3;
      const mine = shellDoc.filter((_, part) => keep[k]![part]);
      const r = await call('springs.simulate', {
        chain: springEntry(spring),
        nodes: chain.nodes,
        strands: chain.strands.length > 1 ? chain.strands.map((x) => x.nodes) : undefined,
        rest: bases[k] ?? undefined,
        frames,
        shells: mine.length ? mine : undefined,
        // The model is drawn at a world scale (the GLB's own root scale, often under 1): the same scale the game wears it at.
        scale,
      });
      const back = new Matrix4();
      const still = r.frames.map((b64, i) => {
        const p = floats(b64);
        back.copy(motions[i]).invert();
        for (let k = 0; k < p.length; k += 3) {
          v.set(p[k], p[k + 1], p[k + 2]).applyMatrix4(back);
          p[k] = v.x;
          p[k + 1] = v.y;
          p[k + 2] = v.z;
        }
        return p;
      });
      return { chain, frames: still, started: performance.now() };
    }),
  ).catch((e) => {
    toast('error', friendly(e));
    return [];
  });
  return results.flatMap((x): SpringFrames[] => (x ? [x] : []));
}

/** Every spring bone back to the pose it was imported in. */
export function restPose() {
  for (const [bone, q] of bindLocal) bone.quaternion.copy(q);
  for (const [bone, p] of bindPosition) bone.position.copy(p);
  scene?.updateMatrixWorld(true);
}

const from = new Vector3(),
  to = new Vector3(),
  here = new Vector3(),
  next = new Vector3(),
  turn = new Quaternion(),
  world = new Quaternion(),
  parentWorld = new Quaternion();

/** Turns each bone of a set so it points at the next node of frame `f`, root first. */
export function pose(set: SpringFrames, f: number) {
  const p = set.frames[Math.min(f, set.frames.length - 1)];
  set.chain.anchor.updateWorldMatrix(true, false);
  for (const strand of set.chain.strands) {
    strand.bones.forEach((bone, i) => {
      bone.quaternion.copy(bindLocal.get(bone)!);
      bone.updateMatrixWorld(true);
      bone.getWorldPosition(here);
      if (i + 1 < strand.bones.length) strand.bones[i + 1].getWorldPosition(next);
      else next.copy(strand.tip).applyMatrix4(bone.matrixWorld);
      from.subVectors(next, here).normalize();
      const a = strand.nodes[i] * 3,
        b = strand.nodes[i + 1] * 3;
      to.set(p[b] - p[a], p[b + 1] - p[a + 1], p[b + 2] - p[a + 2]);
      if (to.lengthSq() < 1e-12) return;
      turn.setFromUnitVectors(from, to.normalize());
      bone.getWorldQuaternion(world);
      (bone.parent ?? bone).getWorldQuaternion(parentWorld);
      bone.quaternion.copy(parentWorld.invert().multiply(turn.multiply(world)));
      bone.updateMatrixWorld(true);
    });
  }
}
