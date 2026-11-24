// The 人物模型 tab's 3D view: the imported model, its matched joints (the one picked on the figure highlighted), the spring sets
// drawn on their bones (picked and added there), the body colliders, the moves played on it (clips.ts) and the spring bones played back from the backend's frames along them.
import { CameraControls, Grid, Html } from '@react-three/drei';
import { Canvas, useFrame, type ThreeEvent } from '@react-three/fiber';
import { Button, Group, Loader, Paper, SegmentedControl, Stack, Switch, Text, Title, Tooltip } from '@mantine/core';
import { IconPlayerPause, IconPlayerPlay, IconUpload } from '@tabler/icons-react';
import { useEffect, useMemo, useRef, useState, useSyncExternalStore } from 'react';
import { Box3, BufferAttribute, BufferGeometry, DoubleSide, SkeletonHelper, Vector3, type Bone, type Mesh, type Object3D, type SkinnedMesh } from 'three';
import type CameraControlsImpl from 'camera-controls';
import { t } from '../i18n';
import { frameFigure } from './stageCamera';
import { MOTION_SECONDS, PREVIEW_FPS, SETTLE_SECONDS } from '../viewport/motion';
import {
  addSpring,
  boneOf,
  buildArms,
  CHARACTER_LIGHTS,
  chooseModel,
  colliders,
  looseRoots,
  modelScene,
  modelVersion,
  onModel,
  pose,
  restPose,
  shellPoints,
  showBody,
  simulate,
  springLabel,
  toggleShellPart,
  type SpringFrames,
} from './model';
import { clipPlayer, MOTIONS, motionShipped, type ClipPlayer, type MotionGroup } from './clips';
import { matchOf, matchReady, useCharacter, type CharmMotion } from './state';

const useModel = () => {
  useSyncExternalStore(onModel, modelVersion);
  return modelScene();
};

const LIT = '#ff9a3c';

/** A dot kept on its bone every frame, so it follows the moves. The marks are mounted after <Motion />: they read the bones once the
 * frame's pose is set. */
function BoneDot({ bone, radius, color, label, onPick, onHover }: { bone: Bone; radius: number; color: string; label?: string; onPick(): void; onHover?(on: boolean): void }) {
  const ref = useRef<Mesh>(null);
  useFrame(() => void (ref.current && bone.getWorldPosition(ref.current.position)));
  return (
    <mesh ref={ref} renderOrder={10} onClick={(e) => (e.stopPropagation(), onPick())} onPointerOver={(e) => (e.stopPropagation(), onHover?.(true))} onPointerOut={() => onHover?.(false)}>
      <sphereGeometry args={[radius, 12, 8]} />
      <meshBasicMaterial color={color} depthTest={false} transparent opacity={0.9} />
      {label && (
        <Html center position={[0, radius * 4, 0]} pointerEvents="none" className="bone-tip">
          {label}
        </Html>
      )}
    </mesh>
  );
}

function Joints({ size }: { size: number }) {
  const s = useCharacter();
  return (
    <>
      {Object.entries(matchOf(s)).map(([joint, name]) => {
        const bone = boneOf(name);
        const picked = s.joint === joint;
        return bone && <BoneDot key={joint} bone={bone} radius={size * (picked ? 0.013 : 0.007)} color={picked ? LIT : '#4fd18b'} onPick={() => useCharacter.setState({ joint, step: 'bones' })} />;
      })}
    </>
  );
}

const hoverOn = (id: string) => (on: boolean) => useCharacter.setState((s) => (on ? { hover: id } : s.hover === id ? { hover: null, hoverStrand: null } : {}));
/** A bone's own hover: names its set and the strand the bone is on, so the strand lights up in the figure while the pointer crosses it. */
const hoverStrandOn = (id: string, strand: number) => (on: boolean) =>
  useCharacter.setState((s) => (on ? { hover: id, hoverStrand: strand } : s.hover === id && s.hoverStrand === strand ? { hoverStrand: null } : {}));
const strandBones = (names: string[][]) => names.map((strand) => strand.map(boneOf).filter((b): b is Bone => !!b));
const at = new Vector3();

/** One strand of a set as a line from bone to bone, redrawn every frame: the one picked under the pointer or in the list. */
function StrandLine({ names, color }: { names: string[]; color: string }) {
  return <StrandLines names={[names]} color={color} />;
}

/** A set's strands as lines from bone to bone, redrawn every frame. */
function StrandLines({ names, color }: { names: string[][]; color: string }) {
  const bones = useMemo(() => strandBones(names), [names]);
  const geometry = useMemo(() => {
    const pairs = bones.reduce((n, strand) => n + Math.max(0, strand.length - 1), 0);
    return new BufferGeometry().setAttribute('position', new BufferAttribute(new Float32Array(pairs * 6), 3));
  }, [bones]);
  useEffect(() => () => geometry.dispose(), [geometry]);
  useFrame(() => {
    const points = geometry.getAttribute('position') as BufferAttribute;
    let i = 0;
    for (const strand of bones)
      for (let k = 0; k + 1 < strand.length; k++)
        for (const bone of [strand[k]!, strand[k + 1]!]) {
          bone.getWorldPosition(at);
          points.setXYZ(i++, at.x, at.y, at.z);
        }
    points.needsUpdate = true;
  });
  return (
    <lineSegments geometry={geometry} frustumCulled={false} renderOrder={9}>
      <lineBasicMaterial color={color} depthTest={false} transparent opacity={0.9} />
    </lineSegments>
  );
}

/** The spring sets on the figure: each set's bones as dots joined by lines, the one picked or under the pointer (here or in the
 * list) lit and named; a click picks it. The grey dots are bones a new set can start at; a click on one adds the set. */
function Springs({ size }: { size: number }) {
  const s = useCharacter();
  const loose = useMemo(() => looseRoots(s), [s.auto, s.picked, s.springs, s.bones]); // eslint-disable-line react-hooks/exhaustive-deps
  return (
    <>
      {s.springs.map((sp) => {
        const lit = s.spring === sp.id || s.hover === sp.id;
        const color = lit ? LIT : '#b48cff';
        const picked = s.spring === sp.id ? s.strand : null;
        const hoveredStrand = s.hover === sp.id ? s.hoverStrand : null;
        const strand = hoveredStrand ?? picked;
        return (
          <group key={sp.id}>
            <StrandLines names={sp.strands} color={color} />
            {strand != null && sp.strands[strand] && <StrandLine names={sp.strands[strand]!} color={LIT} />}
            {strandBones(sp.strands).flatMap((bones, i) =>
              bones.map((bone, k) => (
                <BoneDot
                  key={`${i}-${k}`}
                  bone={bone}
                  radius={size * (i === strand ? 0.008 : lit ? 0.007 : 0.0045)}
                  color={i === strand ? LIT : color}
                  label={s.hover === sp.id && !i && !k ? springLabel(s.springs, sp.id) : undefined}
                  onPick={() => useCharacter.setState({ spring: sp.id, strand: i })}
                  onHover={hoverStrandOn(sp.id, i)}
                />
              )),
            )}
          </group>
        );
      })}
      {loose.map((name) => (
        <BoneDot
          key={name}
          bone={boneOf(name)!}
          radius={size * (s.hover === name ? 0.007 : 0.0045)}
          color={s.hover === name ? LIT : '#9aa7b5'}
          label={s.hover === name ? `+ ${name}` : undefined}
          onPick={() => addSpring(name)}
          onHover={hoverOn(name)}
        />
      ))}
    </>
  );
}

const UP = new Vector3(0, 1, 0);
/** Metres the body's surface is drawn outside the skin. */
const SHELL_LIFT = 0.0025;

/** The body's surface as it is kept out of the spring bones: each joint's triangles over its bone — the model's own surface — or its
 * points for a shell from before version 6 (off, or with the file's own capsules, nothing). */
function Shell() {
  const shell = useCharacter((s) => s.shell);
  const capsules = useCharacter((s) => s.capsules);
  const auto = useCharacter((s) => s.auto);
  const picked = useCharacter((s) => s.picked);
  const surfaces = useMemo(
    () =>
      shellPoints().map(({ joint, bone, points, normals, tris }) => {
        // Lifted off the skin along its own normal: the surface is the model's face itself, and drawn exactly on it the two fight for
        // the same depth (a mottle of skin and shell) and every millimetre the skin wins as the joints blend reads as a hole.
        const lifted = new Float32Array(points.length);
        for (let i = 0; i < points.length; i++) lifted[i] = points[i]! + normals[i]! * SHELL_LIFT;
        const geometry = new BufferGeometry();
        geometry.setAttribute('position', new BufferAttribute(lifted, 3));
        if (tris) geometry.setIndex(new BufferAttribute(tris, 1));
        return { joint, bone, geometry, mesh: !!tris };
      }),
    [shell, capsules, auto, picked], // eslint-disable-line react-hooks/exhaustive-deps
  );
  const clouds = useRef<(Object3D | null)[]>([]);
  // Each surface rides its bone: its matrix is the bone's world matrix, so the surface follows every move exactly.
  useFrame(() =>
    surfaces.forEach(({ bone }, i) => {
      const cloud = clouds.current[i];
      if (!cloud) return;
      bone.updateWorldMatrix(true, false);
      cloud.matrix.copy(bone.matrixWorld);
      cloud.matrixAutoUpdate = false;
      cloud.matrixWorldNeedsUpdate = true;
    }),
  );
  return (
    <>
      {surfaces.map(({ joint, geometry, mesh }, i) =>
        mesh ? (
          <mesh key={joint} ref={(o) => void (clouds.current[i] = o)} geometry={geometry} frustumCulled={false}>
            <meshBasicMaterial color="#45e0b0" transparent opacity={0.18} depthWrite={false} side={DoubleSide} />
          </mesh>
        ) : (
          <points key={joint} ref={(o) => void (clouds.current[i] = o)} geometry={geometry} frustumCulled={false}>
            <pointsMaterial color="#45e0b0" size={2.5} sizeAttenuation={false} transparent opacity={0.6} depthWrite={false} />
          </points>
        ),
      )}
    </>
  );
}

function Colliders() {
  const s = useCharacter();
  const caps = useMemo(() => colliders(), [s.colliders, s.capsules, s.shell, s.auto, s.picked]); // eslint-disable-line react-hooks/exhaustive-deps
  const meshes = useRef<(Mesh | null)[]>([]);
  // Each capsule is put between its bones every frame, so it follows the moves.
  useFrame(() =>
    colliders().forEach((c, i) => {
      const mesh = meshes.current[i];
      if (!mesh) return;
      const dir = c.b.clone().sub(c.a);
      mesh.position.copy(c.a).add(c.b).multiplyScalar(0.5);
      if (dir.length() > 1e-6) mesh.quaternion.setFromUnitVectors(UP, dir.normalize());
    }),
  );
  return (
    <>
      {caps.map((c, i) => {
        const length = c.a.distanceTo(c.b);
        return (
          <mesh key={i} ref={(m) => void (meshes.current[i] = m)}>
            <capsuleGeometry args={[c.radius, length, 4, 12]} />
            <meshBasicMaterial color="#3cc8ff" wireframe transparent opacity={0.35} />
          </mesh>
        );
      })}
    </>
  );
}

// The clip's own clock (seconds since it started, at its speed); a click on the playing clip starts it again.
let clipTime = 0;
const restartClip = () => void (clipTime = 0);
// The longest stretch the spring bones are worked out for at once (the backend's frame limit).
const SPRING_SECONDS = 1199 / PREVIEW_FPS;

/** How long the springs are worked out for a clip: a looping clip twice (the first round settles them), else once and held. */
const springCover = (duration: number, loop: boolean) => Math.min(loop ? 2 * duration : duration + SETTLE_SECONDS, SPRING_SECONDS);
const clipMoment = (time: number, duration: number, loop: boolean) => (duration <= 0 ? 0 : loop ? time % duration : Math.min(time, duration));
/** The spring frame for clip time `time`: a looping clip replays its worked-out second round. */
function springMoment(time: number, duration: number, loop: boolean) {
  const cover = springCover(duration, loop);
  if (!loop || time < cover || duration <= 0) return Math.min(time, cover);
  const warm = cover - duration;
  return Math.min(warm + ((time - warm) % duration), cover);
}

/** Plays the chosen clip on the model (clips.ts) and the spring bones with it: asks the backend again whenever the sets, colliders,
 * the clip or the motion change, and poses the bones each frame from the answer. */
function Motion() {
  const springs = useCharacter((s) => s.springs);
  const coll = useCharacter((s) => s.colliders);
  const capsules = useCharacter((s) => s.capsules);
  const shell = useCharacter((s) => s.shell);
  const motion = useCharacter((s) => s.motion);
  const clipId = useCharacter((s) => s.clip);
  const loop = useCharacter((s) => s.loop);
  const auto = useCharacter((s) => s.auto);
  const picked = useCharacter((s) => s.picked);
  const version = useSyncExternalStore(onModel, modelVersion);
  const [player, setPlayer] = useState<ClipPlayer | null>(null);
  // The spring frames and the model they were simulated on: a new model's bones are not the old frames' bones.
  const sets = useRef<{ model: number; frames: SpringFrames[] }>({ model: -1, frames: [] });
  useEffect(() => {
    let live = true;
    setPlayer(null);
    restPose();
    clipTime = 0;
    if (clipId)
      void clipPlayer(clipId).then((p) => {
        if (!live) return;
        if (p) setPlayer(p);
        else useCharacter.setState({ clip: null, playing: false });
      });
    return () => void (live = false);
  }, [clipId, auto, picked, version]);
  useEffect(() => {
    let live = true;
    const moving = motion && (performance.now() - motion.at) / 1000 < MOTION_SECONDS[motion.kind] + SETTLE_SECONDS;
    const id = setTimeout(() => {
      const model = modelVersion();
      const run = player
        ? simulate(null, {
            count: Math.floor(springCover(player.duration, loop) * PREVIEW_FPS) + 1,
            at: (i) => player.sample(clipMoment(i / PREVIEW_FPS, player.duration, loop)),
          })
        : simulate(moving ? motion.kind : null);
      void run.then((r) => {
        if (!live) return;
        restPose();
        sets.current = { model, frames: r };
      });
    }, 150);
    return () => {
      live = false;
      clearTimeout(id);
    };
  }, [springs, coll, capsules, shell, motion, player, loop]);
  useFrame((_, delta) => {
    const frames = sets.current.model === modelVersion() ? sets.current.frames : [];
    if (!player) {
      for (const set of frames) pose(set, Math.floor(((performance.now() - set.started) / 1000) * PREVIEW_FPS));
      return;
    }
    const s = useCharacter.getState();
    if (s.playing) clipTime += Math.min(delta, 0.1) * s.speed;
    player.sample(clipMoment(clipTime, player.duration, s.loop));
    const frame = Math.round(springMoment(clipTime, player.duration, s.loop) * PREVIEW_FPS);
    for (const set of frames) pose(set, frame);
  });
  return null;
}

/** The first-person arms view: the model's own meshes hidden and the arms the game cuts from it shown on the same skeleton (so the
 * moves play on them), built again when the match or the left-out materials change; the camera frames them once. */
function ArmsView({ controls }: { controls: React.RefObject<CameraControlsImpl | null> }) {
  const view = useCharacter((s) => s.view);
  const auto = useCharacter((s) => s.auto);
  const picked = useCharacter((s) => s.picked);
  const hide = useCharacter((s) => s.hide);
  const ready = useCharacter((s) => matchReady(s.check) && !!s.check?.arms);
  const version = useSyncExternalStore(onModel, modelVersion);
  const framed = useRef(false);
  useEffect(() => {
    if (view !== 'arms' || !ready) return;
    let live = true;
    let mesh: SkinnedMesh | null = null;
    showBody(false);
    void buildArms().then((m) => {
      if (!live) {
        m?.removeFromParent();
        m?.geometry.dispose();
        return;
      }
      mesh = m;
      if (m && !framed.current) {
        framed.current = true;
        m.geometry.computeBoundingBox();
        void controls.current?.fitToBox(new Box3().setFromObject(m, true), true, { paddingTop: 0.05, paddingBottom: 0.05 });
      }
    });
    return () => {
      live = false;
      mesh?.removeFromParent();
      mesh?.geometry.dispose();
      showBody(true);
    };
  }, [view, ready, auto, picked, hide, version, controls]);
  useEffect(() => {
    if (view === 'body') framed.current = false;
  }, [view]);
  return null;
}

function Scene() {
  const scene = useModel();
  const showBones = useCharacter((s) => s.showBones);
  const step = useCharacter((s) => s.step);
  const shellOn = useCharacter((s) => !!s.shell?.on);
  const helper = useMemo(() => (scene ? new SkeletonHelper(scene) : null), [scene]);
  const controls = useRef<CameraControlsImpl>(null);
  const box = useMemo(() => (scene ? new Box3().setFromObject(scene) : null), [scene]);
  // Frames a newly imported model the way the 舞蹈 tab also frames it, its feet clear of the moves bar along the bottom.
  useEffect(() => {
    if (box) frameFigure(controls.current, scene);
  }, [box, scene]);
  // In the springs step a click on the model picks that part's material into the collider surface, or out of it again — the parts the
  // author chooses, right on the figure (a drag of the view is not a click).
  const pickPart = (e: ThreeEvent<MouseEvent>) => {
    if (e.delta > 4 || !shellOn) return;
    const mesh = e.object as Mesh;
    const material = Array.isArray(mesh.material) ? mesh.material[0] : mesh.material;
    if (material?.name) toggleShellPart(material.name);
  };
  return (
    <>
      <ambientLight intensity={CHARACTER_LIGHTS.ambient} />
      <directionalLight position={CHARACTER_LIGHTS.key.position} intensity={CHARACTER_LIGHTS.key.intensity} />
      <directionalLight position={CHARACTER_LIGHTS.fill.position} intensity={CHARACTER_LIGHTS.fill.intensity} />
      <Grid args={[10, 10]} cellSize={0.25} sectionSize={1} cellColor="#2a3340" sectionColor="#3b4a5c" fadeDistance={12} infiniteGrid />
      {scene && <primitive object={scene} onClick={step === 'springs' ? pickPart : undefined} />}
      {scene && showBones && helper && <primitive object={helper} />}
      {scene && <Motion />}
      {scene && step === 'springs' && <Colliders />}
      {scene && step === 'springs' && <Shell />}
      {box && showBones && step === 'bones' && <Joints size={box.max.y - box.min.y} />}
      {box && showBones && step === 'springs' && <Springs size={box.max.y - box.min.y} />}
      {scene && <ArmsView controls={controls} />}
      <CameraControls ref={controls} makeDefault />
    </>
  );
}

const SWINGS: { id: CharmMotion; key: 'character.shake' | 'character.run' | 'character.turn' }[] = [
  { id: 'shake', key: 'character.shake' },
  { id: 'run', key: 'character.run' },
  { id: 'turn', key: 'character.turn' },
];

function MotionButtons() {
  const springs = useCharacter((s) => s.springs.length);
  return (
    <Group gap={4}>
      {SWINGS.map((m) => (
        <Button
          key={m.id}
          size="compact-xs"
          variant="subtle"
          color="flare"
          disabled={!springs}
          onClick={() => useCharacter.setState({ clip: null, playing: false, motion: { kind: m.id, at: performance.now() } })}
        >
          {t(m.key)}
        </Button>
      ))}
    </Group>
  );
}

const SPEEDS = ['0.25', '0.5', '1'];

/** The moves under the preview: the game's own and the try-out ones in two labelled rows, then play or pause, loop and speed. Greyed
 * with the reason until the bones are matched. */
function MotionBar() {
  const check = useCharacter((s) => s.check);
  const clip = useCharacter((s) => s.clip);
  const playing = useCharacter((s) => s.playing);
  const loop = useCharacter((s) => s.loop);
  const speed = useCharacter((s) => s.speed);
  const ready = matchReady(check);
  const shipped = MOTIONS.filter((m) => motionShipped(m.id));
  // A copy without moves shows no move row at all (the swing buttons beside it stay: they need spring bones, not moves).
  if (!shipped.length) return null;
  const choose = (id: string) => {
    restartClip();
    const m = MOTIONS.find((x) => x.id === id)!;
    useCharacter.setState({ clip: id, playing: true, motion: null, ...(clip === id ? {} : { loop: m.loop }) });
  };
  const row = (group: MotionGroup, label: string, info: string) => (
    <Group gap={4} wrap="nowrap">
      <Tooltip label={info} multiline w={260}>
        <Text size="xs" fw={600} w={86} style={{ flexShrink: 0 }}>
          {label}
        </Text>
      </Tooltip>
      <Group gap={2}>
        {shipped
          .filter((m) => m.group === group)
          .map((m) => (
            <Button key={m.id} size="compact-xs" variant={clip === m.id ? 'filled' : 'subtle'} color={group === 'game' ? 'flare' : 'grape'} disabled={!ready} onClick={() => choose(m.id)}>
              {t(`character.motion.${m.id}` as 'character.motion.idle')}
            </Button>
          ))}
      </Group>
    </Group>
  );
  return (
    <Tooltip label={t('character.motionNeedsBones', { step: t('character.step.bones') })} disabled={ready}>
      <div className="overlay motions">
        {row('game', t('character.motionGame'), t('character.motionGameInfo'))}
        {row('mixamo', t('character.motionTry'), t('character.motionTryInfo'))}
        <Group gap={8} justify="center">
          <Button
            size="compact-xs"
            variant="light"
            disabled={!ready || !clip}
            leftSection={playing ? <IconPlayerPause size={14} /> : <IconPlayerPlay size={14} />}
            onClick={() => useCharacter.setState({ playing: !playing })}
          >
            {t(playing ? 'character.motionPause' : 'character.motionPlay')}
          </Button>
          <Switch size="xs" label={t('character.motionLoop')} checked={loop} disabled={!ready || !clip} onChange={(e) => useCharacter.setState({ loop: e.currentTarget.checked })} />
          <Group gap={4}>
            <Text size="xs">{t('character.motionSpeed')}</Text>
            <SegmentedControl
              size="xs"
              data={SPEEDS.map((v) => ({ value: v, label: `${v}×` }))}
              value={String(speed)}
              disabled={!ready || !clip}
              onChange={(v) => useCharacter.setState({ speed: Number(v) })}
            />
          </Group>
          <Button size="compact-xs" variant="subtle" disabled={!clip} onClick={() => useCharacter.setState({ clip: null, playing: false })}>
            {t('character.motionRest')}
          </Button>
        </Group>
      </div>
    </Tooltip>
  );
}

export function CharacterStage() {
  const model = useCharacter((s) => s.model);
  const name = useCharacter((s) => s.name);
  const loading = useCharacter((s) => s.loading);
  const showBones = useCharacter((s) => s.showBones);
  const view = useCharacter((s) => s.view);
  const ready = useCharacter((s) => matchReady(s.check));
  const arms = useCharacter((s) => !!s.check?.arms);
  return (
    <main className="stage">
      <Canvas gl={{ preserveDrawingBuffer: true, antialias: true, alpha: true }} dpr={[1, 2]} camera={{ position: [0, 1.2, 3.2], fov: 40 }}>
        <Scene />
      </Canvas>
      <div className="stage-head">
        <span className="stage-title">{model ? name : t('character.title')}</span>
      </div>
      {loading && (
        <div className="stage-empty">
          <Loader type="bars" size="sm" />
        </div>
      )}
      {!model && !loading && (
        <div className="stage-empty">
          <Paper className="card" bg="transparent">
            <Stack align="center">
              <Title order={2}>{t('character.emptyTitle')}</Title>
              <Text c="dimmed" ta="center">
                {t('character.emptyText')}
              </Text>
              <Button leftSection={<IconUpload size={16} />} onClick={() => void chooseModel()}>
                {t('character.choose')}
              </Button>
            </Stack>
          </Paper>
        </div>
      )}
      {model && (
        <>
          <div className="stage-moves">
            <MotionBar />
            <Group className="overlay" gap={2}>
              <MotionButtons />
            </Group>
          </div>
          <Group className="overlay light" gap={8}>
            <Tooltip label={!ready ? t('character.armsNeedsBones', { step: t('character.step.bones') }) : arms ? t('character.viewInfo') : t('character.note.noArms')} multiline w={280}>
              <SegmentedControl
                size="xs"
                disabled={!ready}
                data={[
                  { value: 'body', label: t('character.viewBody') },
                  { value: 'arms', label: t('character.viewArms'), disabled: !arms },
                ]}
                value={view}
                onChange={(v) => useCharacter.setState({ view: v as 'body' | 'arms' })}
              />
            </Tooltip>
            <Tooltip label={t('character.showBonesInfo')}>
              <Switch size="xs" label={t('character.showBones')} checked={showBones} onChange={(e) => useCharacter.setState({ showBones: e.currentTarget.checked })} />
            </Tooltip>
          </Group>
        </>
      )}
    </main>
  );
}
