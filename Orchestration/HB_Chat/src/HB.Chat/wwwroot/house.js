// 놀이방 창 (/House/{방}): 방 작업을 2.5D 집 안 펫 캐릭터로 보인다. 방 · 벽은 코드로, 가구는 Kenney glb(CC0), 캐릭터는 펫 시트를 세운 판(Y축 빌보드).
// 움직임은 방 SSE(/events/{방})만 읽어 정한다 — 1:1 방은 face · tool · delta · 카드, 단톡방은 member · member-tool · member-delta · 카드.
// 도구 → 가구: 웹 = 컴퓨터, 읽기 = 책장, 고치기 = 작업 책상, 명령 = 터미널 책상, 글 쓰는 중 = 소파, 쉼 = 돌아다니기.
import * as THREE from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';

const data = JSON.parse(document.getElementById('house-data').textContent);
const token = document.querySelector('#token input[name=__RequestVerificationToken]')?.value ?? '';

const KENNEY_SCALE = 2;            // Kenney 단위 → m (desk 0.384 → 0.77m, 펫 키 1.4m와 맞음 — 시험 페이지에서 잼)
const WALL_H = 2.2, LOW_H = 0.9, EDGE_H = 0.18, WALL_T = 0.12;
const CHAR_H = 1.4, SPEED = 1.8, FPS_CAP = 30;
const ELEV_COS = Math.sqrt(2 / 3);  // 아이소 카메라(1,1,1)에서 세운 판이 눌려 보이는 비율 — 판을 그만큼 늘린다

// 펫 시트 행 (HB.Chat.Desktop PetLogic.cs PetActions와 같은 행 · 프레임)
const ANIM = {
  idle: { row: 0, frames: 6, fps: 3 }, right: { row: 1, frames: 8, fps: 8 }, left: { row: 2, frames: 8, fps: 8 },
  wave: { row: 3, frames: 4, fps: 5 }, jump: { row: 4, frames: 5, fps: 6 }, failed: { row: 5, frames: 8, fps: 6 },
  waiting: { row: 6, frames: 6, fps: 4 }, running: { row: 7, frames: 6, fps: 6 }, review: { row: 8, frames: 6, fps: 5 },
};
const ACTIONS = {
  web: { anim: 'running', text: '웹 검색 중' },
  read: { anim: 'review', text: '파일 읽는 중' },
  write: { anim: 'running', text: '파일 고치는 중' },
  run: { anim: 'running', text: '명령 실행 중' },
  other: { anim: 'running', text: '도구 쓰는 중' },
  talk: { anim: 'wave', text: '말하는 중' },
  rest: { anim: 'waiting', text: '쉬는 중' },
};
// 가구: parts[0] 위에 parts[1..]을 얹는다 ([이름, 앞뒤 m] — +면 앞으로)
const FURN = {
  computer: { parts: [['deskCorner'], ['computerScreen', -0.2]], action: 'web' },
  bookcase: { parts: [['bookcaseOpen']], action: 'read' },
  workdesk: { parts: [['desk'], ['lampSquareTable', -0.1]], action: 'write' },
  terminal: { parts: [['desk'], ['computerScreen', -0.15], ['computerKeyboard', 0.15]], action: 'run' },
  sofa: { parts: [['loungeSofa']], action: 'talk' },
  bed: { parts: [['bedSingle']], action: 'rest' },
  plant: { parts: [['pottedPlant']] },
  lamp: { parts: [['lampRoundFloor']] },
  side: { parts: [['sideTable']] },
};
const ROOMS = {
  study: { name: '서재', floor: '#b98b5e', items: ['computer', 'bookcase'], decor: ['plant'] },
  workshop: { name: '작업실', floor: '#9fa7ad', items: ['workdesk', 'terminal'], decor: ['lamp'] },
  living: { name: '거실', floor: '#c9a77c', items: ['sofa', 'bookcase'], decor: ['plant', 'side'] },
  bedroom: { name: '침실', floor: '#a7b8c9', items: ['bed', 'sofa'], decor: ['lamp', 'side'] },
};

// ---------- 장면 ----------
const view = document.getElementById('view');
let renderer;
try {
  renderer = new THREE.WebGLRenderer({ antialias: true });
} catch {
  const n = document.getElementById('notice');
  n.textContent = '이 화면에서는 3D를 그릴 수 없습니다 (WebGL을 쓸 수 없음).';
  n.hidden = false;
  throw new Error('WebGL 없음');
}
renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
renderer.shadowMap.enabled = true;
renderer.shadowMap.type = THREE.PCFSoftShadowMap;
view.appendChild(renderer.domElement);

const scene = new THREE.Scene();
scene.background = new THREE.Color('#2a2f3a');
const camera = new THREE.OrthographicCamera(-1, 1, 1, -1, 0.1, 200);
const target = new THREE.Vector3();
scene.add(new THREE.HemisphereLight(0xffffff, 0x6d5f52, 1.6));
const sun = new THREE.DirectionalLight(0xfff3e0, 1.6);
sun.castShadow = true;
sun.shadow.mapSize.set(2048, 2048);
sun.shadow.bias = -0.0005;
scene.add(sun, sun.target);
const charGroup = new THREE.Group();
scene.add(charGroup);
let houseGroup = null;

const loader = new GLTFLoader();
const modelCache = new Map();
// 받지 못한 모델은 null — 그 가구만 빠지고 집은 지어진다. 실패는 캐시에 남기지 않아 "새 집"에서 다시 받는다
function loadModel(name) {
  if (!modelCache.has(name)) modelCache.set(name, loader.loadAsync(`/house/models/${name}.glb`).then(g => g.scene, () => {
    modelCache.delete(name);
    return null;
  }));
  return modelCache.get(name);
}

// 집 · 가구 자원을 GPU에서 푼다 — 공유 재질(벽 · 툰 단계)과 glb 원본 도형(클론이 같이 씀)은 두고, 직접 만든 것만
function disposeGroup(group) {
  group.traverse(o => {
    if (!o.isMesh) return;
    if (o.userData.own) o.geometry.dispose();
    for (const m of [].concat(o.material)) {
      if (m === wallMat || m === capMat) continue;
      if (m.map?.isCanvasTexture) m.map.dispose();
      m.dispose();
    }
  });
}

// ---------- 난수 (방 ID로 정한 씨앗 — 같은 방은 늘 같은 집, "새 집"은 이 브라우저에만 저장) ----------
const seedKey = 'hbchat.house.seed.' + data.room;
function hashSeed(s) { let h = 2166136261; for (const ch of s) { h ^= ch.charCodeAt(0); h = Math.imul(h, 16777619); } return h >>> 0; }
function rng(s) { return () => { s |= 0; s = s + 0x6D2B79F5 | 0; let t = Math.imul(s ^ s >>> 15, 1 | s); t = t + Math.imul(t ^ t >>> 7, 61 | t) ^ t; return ((t ^ t >>> 14) >>> 0) / 4294967296; }; }
let rand = Math.random;        // 집 짓기 (씨앗)
const live = Math.random;      // 캐릭터 행동 (씨앗 없음)
const pickOf = (a, r = live) => a[Math.floor(r() * a.length)];
const shuffle = (a, r = rand) => { for (let i = a.length - 1; i > 0; i--) { const j = Math.floor(r() * (i + 1)); [a[i], a[j]] = [a[j], a[i]]; } return a; };
const between = (a, b, r = rand) => a + Math.floor(r() * (b - a + 1));
const key = (x, z) => `${x}|${z}`;

// ---------- 집 (칸 격자 · 벽 · 문) ----------
let grid = null;

function makeLayout() {
  const w0 = between(5, 7), w1 = between(5, 7), d0 = between(4, 6), d1 = between(4, 6);
  const W = w0 + w1, D = d0 + d1;
  const kinds = shuffle(Object.keys(ROOMS));
  const rooms = [
    { x: 0, z: 0, w: w0, d: d0 }, { x: w0, z: 0, w: w1, d: d0 },
    { x: 0, z: d0, w: w0, d: d1 }, { x: w0, z: d0, w: w1, d: d1 },
  ].map((r, i) => ({ ...r, kind: kinds[i] }));
  const vWalls = new Set(), hWalls = new Set(); // v: x=X 선의 z칸, h: z=Z 선의 x칸
  for (let z = 0; z < D; z++) { vWalls.add(key(0, z)); vWalls.add(key(W, z)); }
  for (let x = 0; x < W; x++) { hWalls.add(key(x, 0)); hWalls.add(key(x, D)); }
  const vDoors = [Math.floor(d0 / 2), d0 + Math.floor(d1 / 2)];
  const hDoors = [Math.floor(w0 / 2), w0 + Math.floor(w1 / 2)];
  for (let z = 0; z < D; z++) if (!vDoors.includes(z)) vWalls.add(key(w0, z));
  for (let x = 0; x < W; x++) if (!hDoors.includes(x)) hWalls.add(key(x, d0));
  const reserved = new Set(); // 문 앞뒤 칸은 비워 둔다
  for (const z of vDoors) { reserved.add(key(w0 - 1, z)); reserved.add(key(w0, z)); }
  for (const x of hDoors) { reserved.add(key(x, d0 - 1)); reserved.add(key(x, d0)); }
  return { W, D, w0, d0, rooms, vWalls, hWalls, reserved, blocked: new Set(), furniture: [] };
}

function canStep(g, x, z, nx, nz) {
  if (nx < 0 || nz < 0 || nx >= g.W || nz >= g.D || g.blocked.has(key(nx, nz))) return false;
  if (nx !== x) return !g.vWalls.has(key(Math.max(x, nx), z));
  return !g.hWalls.has(key(x, Math.max(z, nz)));
}

function findPath(g, from, to) {
  const prev = new Map([[key(from.x, from.z), null]]);
  const q = [from];
  for (let i = 0; i < q.length; i++) {
    const c = q[i];
    if (c.x === to.x && c.z === to.z) {
      const out = [];
      for (let n = c; n; n = prev.get(key(n.x, n.z))) out.unshift(n);
      return out;
    }
    for (const [dx, dz] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) {
      const n = { x: c.x + dx, z: c.z + dz };
      if (prev.has(key(n.x, n.z)) || !canStep(g, c.x, c.z, n.x, n.z)) continue;
      prev.set(key(n.x, n.z), c);
      q.push(n);
    }
  }
  return null;
}

async function buildFurniture(type) {
  const def = FURN[type];
  const group = new THREE.Group(), inner = new THREE.Group();
  group.add(inner);
  let base = null;
  for (const [name, forward = 0] of def.parts) {
    const source = await loadModel(name);
    if (!source) {
      if (!base) return null; // 바닥 부품이 없으면 그 가구는 빠진다
      continue;
    }
    const m = source.clone(true);
    m.scale.setScalar(KENNEY_SCALE);
    inner.add(m);
    m.updateMatrixWorld(true);
    const b = new THREE.Box3().setFromObject(m);
    if (!base) { base = b; continue; }
    const c = base.getCenter(new THREE.Vector3()), mc = b.getCenter(new THREE.Vector3());
    m.position.x += c.x - mc.x;
    m.position.z += c.z - mc.z + forward;
    m.position.y += base.max.y - b.min.y;
  }
  inner.updateMatrixWorld(true);
  const box = new THREE.Box3().setFromObject(inner);
  const c = box.getCenter(new THREE.Vector3());
  inner.position.set(-c.x, -box.min.y, -c.z);
  const size = box.getSize(new THREE.Vector3());
  group.traverse(o => {
    if (!o.isMesh) return;
    o.castShadow = o.receiveShadow = true;
    o.material = Array.isArray(o.material) ? o.material.map(toToon) : toToon(o.material); // 툰 셰이딩 — 펫 일러스트와 결을 맞춘다
  });
  return { type, group, size, fw: Math.max(1, Math.ceil(size.x - 0.15)), fd: Math.max(1, Math.ceil(size.z - 0.15)), action: def.action };
}

const gradient = (() => { const t = new THREE.DataTexture(new Uint8Array([90, 170, 255]), 3, 1, THREE.RedFormat); t.minFilter = t.magFilter = THREE.NearestFilter; t.needsUpdate = true; return t; })();
const toToon = m => new THREE.MeshToonMaterial({ color: m.color, map: m.map, gradientMap: gradient });

function fits(g, x, z, fw, fd, room) {
  for (let i = 0; i < fw; i++) for (let j = 0; j < fd; j++) {
    const tx = x + i, tz = z + j;
    if (tx < room.x || tz < room.z || tx >= room.x + room.w || tz >= room.z + room.d) return false;
    if (g.blocked.has(key(tx, tz)) || g.reserved.has(key(tx, tz))) return false;
  }
  return true;
}
function occupy(g, x, z, fw, fd) { for (let i = 0; i < fw; i++) for (let j = 0; j < fd; j++) g.blocked.add(key(x + i, z + j)); }

async function placeRoom(g, room) {
  const def = ROOMS[room.kind];
  for (const type of def.items) {
    const f = await buildFurniture(type);
    if (!f) continue;
    // 방 뒷벽(북쪽)에 붙여 앞(+z)을 보게. 쓰는 칸 = 앞 가운데
    let placed = false;
    for (const x of shuffle([...Array(room.w).keys()].map(i => room.x + i))) {
      if (!fits(g, x, room.z, f.fw, f.fd, room)) continue;
      const use = { x: x + Math.floor((f.fw - 1) / 2), z: room.z + f.fd };
      if (use.z >= room.z + room.d || g.blocked.has(key(use.x, use.z))) continue;
      occupy(g, x, room.z, f.fw, f.fd);
      g.reserved.add(key(use.x, use.z));
      f.group.position.set(x + f.fw / 2, 0, room.z + WALL_T / 2 + f.size.z / 2 + 0.02);
      g.furniture.push({ ...f, use, room, busy: null });
      placed = true;
      break;
    }
    if (!placed) disposeGroup(f.group);
  }
  for (const type of def.decor) {
    const f = await buildFurniture(type);
    if (!f) continue;
    const cells = shuffle([...Array(room.w * room.d).keys()].map(i => ({ x: room.x + i % room.w, z: room.z + Math.floor(i / room.w) })))
      .filter(c => c.x === room.x || c.z === room.z || c.x === room.x + room.w - 1); // 벽가
    let placed = false;
    for (const c of cells) {
      if (!fits(g, c.x, c.z, f.fw, f.fd, room)) continue;
      occupy(g, c.x, c.z, f.fw, f.fd);
      f.group.position.set(c.x + f.fw / 2, 0, c.z + f.fd / 2);
      g.furniture.push({ ...f, use: null, room, busy: null });
      placed = true;
      break;
    }
    if (!placed) disposeGroup(f.group);
  }
}

function freeTiles(g) { return [...Array(g.W * g.D).keys()].map(i => ({ x: i % g.W, z: Math.floor(i / g.W) })).filter(t => !g.blocked.has(key(t.x, t.z))); }

// 한 칸에서 걸어서 닿는 칸 모두 (벽 · 가구 고려)
function reachable(g, start) {
  const seen = new Set([key(start.x, start.z)]);
  const q = [start];
  for (let i = 0; i < q.length; i++) {
    const c = q[i];
    for (const [dx, dz] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) {
      const n = { x: c.x + dx, z: c.z + dz };
      if (seen.has(key(n.x, n.z)) || !canStep(g, c.x, c.z, n.x, n.z)) continue;
      seen.add(key(n.x, n.z));
      q.push(n);
    }
  }
  return seen;
}

// 가구 쓰는 칸이 모두 한 덩어리로 이어졌는지. 이어졌으면 g.reach = 그 덩어리 — 캐릭터 시작 · 돌아다니기는 그 안에서만 (갇힌 칸 X)
function connected(g) {
  const uses = g.furniture.filter(f => f.use).map(f => f.use);
  const start = uses[0] ?? freeTiles(g)[0];
  if (!start) return false;
  g.reach = reachable(g, start);
  return uses.every(u => g.reach.has(key(u.x, u.z)));
}
const walkable = g => freeTiles(g).filter(t => g.reach.has(key(t.x, t.z)) && !g.reserved.has(key(t.x, t.z)));

function floorTexture(color) {
  const cv = document.createElement('canvas'); cv.width = cv.height = 64;
  const x = cv.getContext('2d');
  x.fillStyle = color; x.fillRect(0, 0, 64, 64);
  x.strokeStyle = 'rgba(0,0,0,.12)'; x.lineWidth = 2; x.strokeRect(1, 1, 62, 62);
  const t = new THREE.CanvasTexture(cv);
  t.colorSpace = THREE.SRGBColorSpace;
  t.wrapS = t.wrapT = THREE.RepeatWrapping;
  return t;
}

const wallMat = new THREE.MeshToonMaterial({ color: '#efe6d8', gradientMap: gradient });
const capMat = new THREE.MeshToonMaterial({ color: '#8a7766', gradientMap: gradient });
function wallBox(group, x, z, w, d, h) {
  const m = new THREE.Mesh(new THREE.BoxGeometry(w, h, d), [wallMat, wallMat, capMat, wallMat, wallMat, wallMat]);
  m.userData.own = true;
  m.position.set(x, h / 2, z);
  m.castShadow = m.receiveShadow = true;
  group.add(m);
}

function buildShell(g) {
  const group = new THREE.Group();
  for (const r of g.rooms) {
    const t = floorTexture(ROOMS[r.kind].floor); t.repeat.set(r.w, r.d);
    const floor = new THREE.Mesh(new THREE.PlaneGeometry(r.w, r.d), new THREE.MeshToonMaterial({ map: t, gradientMap: gradient }));
    floor.userData.own = true;
    floor.rotation.x = -Math.PI / 2;
    floor.position.set(r.x + r.w / 2, 0, r.z + r.d / 2);
    floor.receiveShadow = true;
    group.add(floor);
  }
  // 벽 높이: 뒤(x=0 · z=0) 높게, 안쪽 낮게, 앞(x=W · z=D) 아주 낮게 — 위에서 보는 단면
  const hOf = (line, max) => line === 0 ? WALL_H : line === max ? EDGE_H : LOW_H;
  const runs = (set, line, len) => {
    const out = []; let s = -1;
    for (let i = 0; i <= len; i++) { const on = i < len && set.has(key(line, i)); if (on && s < 0) s = i; if (!on && s >= 0) { out.push([s, i]); s = -1; } }
    return out;
  };
  for (const X of [0, g.w0, g.W]) for (const [a, b] of runs(g.vWalls, X, g.D)) wallBox(group, X, (a + b) / 2, WALL_T, b - a + WALL_T, hOf(X, g.W));
  const hSet = new Set([...g.hWalls].map(k => k.split('|').reverse().join('|')));
  for (const Z of [0, g.d0, g.D]) for (const [a, b] of runs(hSet, Z, g.W)) wallBox(group, (a + b) / 2, Z, b - a + WALL_T, WALL_T, hOf(Z, g.D));
  return group;
}

// ---------- 캐릭터 ----------
const sheets = new Map();
// 받지 못한 시트는 null (캐시에 남기지 않음) — 그 캐릭터는 대신 모양으로
function sheet(pet) {
  if (!pet) return Promise.resolve(null);
  if (!sheets.has(pet)) sheets.set(pet, new THREE.TextureLoader().loadAsync(`/house/pet/${encodeURIComponent(pet)}`).then(t => {
    t.colorSpace = THREE.SRGBColorSpace;
    const { width: w, height: h } = t.image;
    return { tex: t, rows: Math.abs(h / (w / 8 * 208 / 192) - 11) <= 0.08 ? 11 : 9 }; // v2 11행 · v1 9행 (PetSheet.Detect와 같은 오차)
  }, () => {
    sheets.delete(pet);
    return null;
  }));
  return sheets.get(pet);
}

const chars = new Map(); // 키 → 캐릭터
const labels = document.getElementById('labels');
const camRight = new THREE.Vector3(1, 0, -1).normalize();

async function skin(c, pet) {
  const got = await sheet(pet);
  c.mesh.visible = !!got;
  c.stand.visible = !got; // 그림이 없으면 대신 모양 (집은 그대로 그린다)
  if (!got) return;
  const t = got.tex.clone();
  t.needsUpdate = true;
  t.repeat.set(1 / 8, 1 / got.rows);
  c.tex?.dispose();
  c.tex = t; c.rows = got.rows;
  c.mesh.material.map = t;
  c.mesh.material.needsUpdate = true;
}

async function makeChar(info, at) {
  const geo = new THREE.PlaneGeometry(CHAR_H * 192 / 208, CHAR_H / ELEV_COS);
  geo.translate(0, CHAR_H / ELEV_COS / 2, 0);
  const mesh = new THREE.Mesh(geo, new THREE.MeshBasicMaterial({ alphaTest: 0.5, side: THREE.DoubleSide }));
  mesh.rotation.y = Math.PI / 4; // 카메라 쪽(1,·,1)을 본다
  const shadow = new THREE.Mesh(new THREE.CircleGeometry(0.28, 20), new THREE.MeshBasicMaterial({ color: 0, transparent: true, opacity: 0.25, depthWrite: false }));
  shadow.rotation.x = -Math.PI / 2;
  shadow.position.y = 0.01;
  const stand = new THREE.Mesh(new THREE.CapsuleGeometry(0.25, 0.6, 4, 12), new THREE.MeshToonMaterial({ color: '#8fa3c0', gradientMap: gradient }));
  stand.position.y = 0.55;
  stand.visible = false;
  const root = new THREE.Group();
  root.add(mesh, shadow, stand);
  root.position.set(at.x + 0.5, 0, at.z + 0.5);
  charGroup.add(root);
  const el = document.createElement('div');
  el.className = 'label';
  const name = document.createElement('b'); name.textContent = info.name;
  const status = document.createElement('span');
  el.append(name, status);
  labels.appendChild(el);
  const c = {
    info, root, mesh, stand, tex: null, rows: 11, tile: { ...at }, path: [], anim: 'idle', t0: performance.now(), facing: 'right',
    el, status, mode: 'idle', action: null, target: null, wanderAt: performance.now() + 500 + live() * 2000, onceUntil: 0, lastTool: 0,
    waitKind: null, retryAt: 0,
  };
  await skin(c, info.pet);
  chars.set(info.key, c);
  return c;
}

function setAnim(c, name, now) { if (c.anim !== name) { c.anim = name; c.t0 = now; } }
function setFrame(c, now) {
  const a = ANIM[c.anim];
  const f = Math.floor((now - c.t0) / 1000 * a.fps) % a.frames;
  c.tex?.offset.set(f / 8, 1 - (a.row + 1) / c.rows);
}

function release(c) {
  if (c.target) { c.target.busy = null; c.target = null; }
  c.waitKind = null;
}

// 지금 서 있는(걷는 중이면 다음) 칸에서 dest로 길을 다시 잡는다
function walkTo(c, dest) {
  const from = c.path[0] ?? c.tile;
  const p = findPath(grid, from, dest);
  if (!p) return false;
  c.path = c.path.length ? [c.path[0], ...p.slice(1)] : p.slice(1);
  return true;
}

/**
 * 그 동작의 가구로. 그 밖 도구(other)는 컴퓨터. 빈 가구 → 쉬다 들른 캐릭터가 잡은 가구(비켜 줌) → 다 일하는 중이면 그 옆에서 기다림.
 * 옆에서 기다리는 중이면 같은 종류 알림마다 자리를 다시 잡지 않는다 — 빈 가구가 생기면 update가 다시 부른다.
 */
function goUse(c, action) {
  const kind = action === 'other' ? 'web' : action;
  if (c.target?.action === kind) return;
  const all = grid.furniture.filter(f => f.action === kind && f.use);
  const open = all.filter(f => !f.busy || (f.busy !== c && f.busy.mode === 'idle'));
  if (!open.length && c.waitKind === kind) return;
  release(c);
  if (open.length) {
    const f = pickOf(open);
    if (f.busy) { const other = f.busy; other.target = null; other.path = other.path.slice(0, 1); other.wanderAt = 0; } // 쉬던 캐릭터는 비켜 준다
    f.busy = c; c.target = f;
    walkTo(c, f.use);
    return;
  }
  if (!all.length) return;
  c.waitKind = kind;
  c.retryAt = performance.now() + 1000;
  const spot = near(pickOf(all).use);
  if (spot) walkTo(c, spot);
}

function near(t) {
  for (const [dx, dz] of [[1, 0], [-1, 0], [0, 1], [1, 1], [-1, 1]]) {
    const n = { x: t.x + dx, z: t.z + dz };
    if (grid.reach.has(key(n.x, n.z))) return n;
  }
  return null;
}

/**
 * 캐릭터 상태: work(action) = 그 가구로 가서 그 동작 · think = 제자리 생각 · ask = 제자리 손 흔들기(승인 · 질문 기다림) ·
 * idle = 알아서 돌아다님 · sleep = 침대(놀이터 일시 정지 · 끝).
 */
function setMode(c, mode, action = null) {
  if (!c || (c.mode === mode && c.action === action)) return;
  if (c.mode !== mode) c.waitKind = null;
  c.mode = mode; c.action = action;
  const now = performance.now();
  if (mode === 'work') goUse(c, action);
  else if (mode === 'sleep') goUse(c, 'rest');
  else if (mode === 'think' || mode === 'ask') { release(c); c.path = c.path.slice(0, 1); }
  else { release(c); c.wanderAt = now + 800 + live() * 2000; }
}

// 한 번 도는 동작(끝남 점프 · 실패): 걷기 동작이 덮지 않게 그 자리에서 멈춘다
function once(c, anim, ms) {
  const now = performance.now();
  c.path = [];
  setAnim(c, anim, now);
  c.onceUntil = now + ms;
}

function statusText(c) {
  if (c.mode === 'ask') return '승인 · 질문 기다림';
  if (c.mode === 'think') return '생각 중';
  if (c.mode === 'sleep') return '쉬는 중';
  if (c.mode === 'work') return ACTIONS[c.action]?.text ?? '';
  if (c.mode === 'idle' && c.target && !c.path.length) return c.target.action === 'read' ? '책 구경' : '쉬는 중'; // 쉬다 들른 곳 — 작업 글(말하는 중 등)을 쓰지 않는다
  return '';
}

function update(c, now, dt) {
  const next = c.path[0];
  if (next) {
    const goal = new THREE.Vector3(next.x + 0.5, 0, next.z + 0.5);
    const d = goal.clone().sub(c.root.position);
    const side = d.dot(camRight);
    if (Math.abs(side) > 0.05) c.facing = side > 0 ? 'right' : 'left';
    setAnim(c, c.facing, now);
    const step = SPEED * dt;
    if (d.length() <= step) { c.root.position.copy(goal); c.tile = { ...next }; c.path.shift(); }
    else c.root.position.add(d.setLength(step));
    return;
  }
  if (now < c.onceUntil) return;
  switch (c.mode) {
    case 'work':
      setAnim(c, c.target ? ACTIONS[c.action].anim : 'waiting', now);
      if (!c.target && c.waitKind && now >= c.retryAt) { // 옆에서 기다리는 중 — 가구가 비었는지 1초마다 본다
        c.retryAt = now + 1000;
        if (grid.furniture.some(f => f.action === c.waitKind && f.use && (!f.busy || f.busy.mode === 'idle'))) goUse(c, c.action);
      }
      break;
    case 'sleep': setAnim(c, 'waiting', now); break;
    case 'think': setAnim(c, 'waiting', now); break;
    case 'ask': setAnim(c, 'wave', now); break;
    default: idleStep(c, now);
  }
}

// 쉴 때: 소파 · 침대 · 책장에 잠깐 들르거나 돌아다닌다
function idleStep(c, now) {
  setAnim(c, c.target?.action === 'read' ? 'review' : c.target ? 'waiting' : 'idle', now);
  if (now < c.wanderAt) return;
  release(c);
  const spots = grid.furniture.filter(f => f.use && !f.busy && ['talk', 'rest', 'read'].includes(f.action));
  if (spots.length && live() < 0.5) {
    const f = pickOf(spots);
    f.busy = c; c.target = f;
    walkTo(c, f.use);
    c.wanderAt = now + 6000 + live() * 8000;
    return;
  }
  const tiles = walkable(grid);
  if (tiles.length) walkTo(c, pickOf(tiles));
  c.wanderAt = now + 3000 + live() * 4000;
}

// ---------- 방 알림 → 캐릭터 ----------
function toolAction(name) {
  name = name || '';
  if (/^(WebSearch|WebFetch)$/.test(name) || name.startsWith('mcp__hb-chat__browser_')) return 'web';
  if (/^(Read|Grep|Glob|LS|NotebookRead)$/.test(name)) return 'read';
  if (/^(Edit|Write|MultiEdit|NotebookEdit)$/.test(name)) return 'write';
  if (/^(Bash|PowerShell|BashOutput|KillShell|KillBash)$/.test(name) || name.startsWith('mcp__hb-chat__terminal_')) return 'run';
  return 'other';
}

function onTool(c, name) {
  if (!c) return;
  c.lastTool = performance.now();
  setMode(c, 'work', toolAction(name));
}

// 글 조각: 도구 바로 뒤의 짧은 글(생각 한 줄)로 소파까지 뛰지 않게 1.5초는 그대로
function onText(c) {
  if (!c || performance.now() - c.lastTool < 1500) return;
  setMode(c, 'work', 'talk');
}

const single = data.kind === 'single';
const main = () => chars.get('main');

// 1:1 방 표정 상태 (Face 이름) → 캐릭터
function onFace(face, tool) {
  const c = main();
  if (!c) return;
  switch (face) {
    case 'Thinking': setMode(c, 'think'); break;
    case 'Writing': onText(c); break; // 도구 사이 짧은 글로 소파까지 오가지 않게
    case 'Tool': if (tool) onTool(c, tool); else setMode(c, 'think'); break;
    case 'Approval': case 'Question': setMode(c, 'ask'); break;
    case 'Done': setMode(c, 'idle'); once(c, 'jump', 1200); break;
    case 'Failed': setMode(c, 'idle'); once(c, 'failed', 1600); break;
    default: setMode(c, 'idle');
  }
}

// 단톡방 구성원 상태 글(GroupChat.MemberState) → 캐릭터
function onMemberState(id, label) {
  const c = chars.get(id);
  if (!c) return;
  if (!label) {
    if (c.mode !== 'idle' && c.mode !== 'sleep') { setMode(c, playgroundResting() ? 'sleep' : 'idle'); }
    return;
  }
  if (label.includes('승인') || label.includes('질문')) setMode(c, 'ask');
  else if (label.includes('차례')) setMode(c, 'idle');
  else if (c.mode === 'idle' || c.mode === 'sleep' || c.mode === 'ask') setMode(c, 'think');
}

let playgroundState = null;
const playgroundResting = () => playgroundState === 'Paused' || playgroundState === 'Done';
async function syncPlayground() {
  if (data.kind !== 'playground') return;
  try {
    const res = await fetch(`/Room/${encodeURIComponent(data.room)}?handler=Playground`, { cache: 'no-store' });
    if (!res.ok) return;
    playgroundState = (await res.json()).state;
    for (const c of chars.values()) {
      if (playgroundResting() && c.mode === 'idle') setMode(c, 'sleep');
      else if (!playgroundResting() && c.mode === 'sleep') setMode(c, 'idle');
    }
  } catch { }
}

async function syncMembers() {
  if (single) return;
  try {
    const res = await fetch(`/Room/${encodeURIComponent(data.room)}?handler=GroupState`, { cache: 'no-store' });
    if (!res.ok) return;
    for (const s of await res.json()) onMemberState(s.member, s.state);
  } catch { }
  await syncPlayground();
}

// 승인 · 질문 답(approval-done · question-done)은 보지 않는다 — 남은 카드가 없을 때 서버가 보내는 face Thinking · member 상태 글이 맞춘다
async function handle(ev) {
  if (single) {
    switch (ev.kind) {
      case 'snapshot': if (!ev.busy) setMode(main(), 'idle'); else onFace(ev.face); break;
      case 'face': onFace(ev.face, ev.text); break;
      case 'end': onFace(ev.face); break; // 끝남 · 실패 · 중지는 face가 아니라 end 알림으로 온다
      case 'tool': onTool(main(), ev.text); break;
      case 'delta': onText(main()); break;
      case 'approval': case 'question': setMode(main(), 'ask'); break;
      case 'idle': setMode(main(), 'idle'); break;
    }
    return;
  }
  switch (ev.kind) {
    case 'snapshot': case 'group': await syncMembers(); break;
    case 'member': onMemberState(ev.detail, ev.text); break;
    case 'member-tool': onTool(chars.get(ev.member), ev.text); break;
    case 'member-delta': onText(chars.get(ev.member)); break;
    case 'member-reset': if (ev.text) onText(chars.get(ev.member)); break;
    case 'approval': case 'question': setMode(chars.get(ev.detail), 'ask'); break;
  }
}

// 방 SSE: 창이 숨으면 닫고 보이면 다시 연다 — 창마다 연결 하나를 잡아 HTTP/1.1 한 호스트 연결 6개를 채팅 화면과 나눠 쓰므로.
// 알림은 온 차례대로 하나씩 처리한다 (GroupState 응답이 그 뒤에 온 알림을 옛 상태로 덮지 않게 — chat.js mountGroup과 같은 방식)
let es = null, chain = Promise.resolve();
function connect() {
  if (es || document.hidden) return;
  es = new EventSource(`/events/${encodeURIComponent(data.room)}`);
  es.onmessage = msg => {
    const ev = JSON.parse(msg.data);
    chain = chain.then(() => handle(ev)).catch(() => { });
  };
}
document.addEventListener('visibilitychange', () => {
  if (document.hidden) { es?.close(); es = null; }
  else connect(); // 다시 붙으면 snapshot이 상태를 다시 맞춘다
});

// ---------- 집 짓기 (한 번에 하나 — 겹치면 씨앗 · 캐릭터가 섞인다) ----------
let building = Promise.resolve();
function rebuild(seed) {
  building = building.then(() => build(seed)).catch(() => { });
  return building;
}

async function build(seed) {
  rand = rng(seed);
  let g = null;
  for (let tries = 0; tries < 20; tries++) {
    if (g) for (const f of g.furniture) disposeGroup(f.group); // 버린 배치의 가구
    g = makeLayout();
    for (const r of g.rooms) await placeRoom(g, r);
    if (connected(g)) break;
  }
  if (houseGroup) { scene.remove(houseGroup); disposeGroup(houseGroup); }
  houseGroup = buildShell(g);
  for (const f of g.furniture) houseGroup.add(f.group);
  scene.add(houseGroup);
  grid = g;
  // 캐릭터: 처음이면 만들고, 다시 지으면 빈 칸으로 옮긴다 (걸어서 닿는 칸만)
  const pool = walkable(g);
  const starts = shuffle(pool.length ? pool : freeTiles(g), live);
  let i = 0;
  for (const info of data.characters) {
    const at = starts[i++ % starts.length];
    const c = chars.get(info.key) ?? await makeChar(info, at);
    release(c);
    c.path = []; c.tile = { ...at }; c.root.position.set(at.x + 0.5, 0, at.z + 0.5);
    const { mode, action } = c;
    c.mode = null;
    setMode(c, mode ?? 'idle', action);
  }
  frameCamera();
}

// ---------- 카메라 ----------
function frameCamera() {
  target.set(grid.W / 2, 0, grid.D / 2);
  camera.position.copy(target).add(new THREE.Vector3(1, 1, 1).multiplyScalar(30));
  camera.lookAt(target);
  sun.position.copy(target).add(new THREE.Vector3(-6, 14, 8));
  sun.target.position.copy(target);
  const s = Math.max(grid.W, grid.D);
  Object.assign(sun.shadow.camera, { left: -s, right: s, top: s, bottom: -s, near: 1, far: 60 });
  sun.shadow.camera.updateProjectionMatrix();
  resize();
}

function resize() {
  const w = view.clientWidth, h = view.clientHeight;
  renderer.setSize(w, h);
  const span = grid ? (grid.W + grid.D) * 0.62 : 10;
  const aspect = w / h;
  const half = aspect > 1 ? span / 2 : span / 2 / aspect;
  Object.assign(camera, { left: -half * aspect, right: half * aspect, top: half * 0.85, bottom: -half * 0.85 });
  camera.updateProjectionMatrix();
}
addEventListener('resize', resize);
renderer.domElement.addEventListener('wheel', e => {
  camera.zoom = THREE.MathUtils.clamp(camera.zoom * (e.deltaY < 0 ? 1.1 : 1 / 1.1), 0.5, 4);
  camera.updateProjectionMatrix();
  e.preventDefault();
}, { passive: false });
let drag = null;
renderer.domElement.addEventListener('pointerdown', e => { drag = { x: e.clientX, y: e.clientY }; });
addEventListener('pointerup', () => { drag = null; });
addEventListener('pointermove', e => {
  if (!drag) return;
  const k = (camera.right - camera.left) / camera.zoom / view.clientWidth;
  const right = new THREE.Vector3(1, 0, -1).normalize(), up = new THREE.Vector3(-1, 0, -1).normalize();
  // 바닥 깊이 1칸은 화면에서 1/√3로 눌려 보인다 (아이소 (1,1,1) 카메라)
  const move = right.multiplyScalar(-(e.clientX - drag.x) * k).add(up.multiplyScalar((e.clientY - drag.y) * k * Math.sqrt(3)));
  camera.position.add(move);
  target.add(move);
  drag = { x: e.clientX, y: e.clientY };
});

// ---------- 패널: 새 집 · 캐릭터 고르기 ----------
const newHouse = document.getElementById('new-house');
newHouse.addEventListener('click', async () => {
  const seed = Math.floor(Math.random() * 2 ** 31);
  try { localStorage.setItem(seedKey, String(seed)); } catch { }
  newHouse.disabled = true;
  await rebuild(seed);
  newHouse.disabled = false;
});

const picks = document.getElementById('picks');
for (const info of data.characters) {
  const row = document.createElement('label');
  row.className = 'pick';
  const name = document.createElement('span');
  name.textContent = info.name;
  name.title = info.name;
  const sel = document.createElement('select');
  for (const p of data.pets) sel.add(new Option(p.name, p.id, false, p.id === info.pet));
  sel.addEventListener('change', async () => {
    const body = new FormData();
    body.append('key', info.key);
    body.append('pet', sel.value);
    body.append('__RequestVerificationToken', token);
    sel.disabled = true;
    try {
      const res = await fetch(`/House/${encodeURIComponent(data.room)}?handler=Pet`, { method: 'POST', body });
      if (res.ok) { info.pet = sel.value; const c = chars.get(info.key); if (c) await skin(c, info.pet); }
      else { sel.value = info.pet; alert(await res.text() || '바꾸지 못했습니다.'); }
    } catch { sel.value = info.pet; }
    sel.disabled = false;
  });
  row.append(name, sel);
  picks.appendChild(row);
}

// ---------- 그리기 (30fps 제한 · 창이 숨으면 rAF가 멈춘다) ----------
let last = performance.now(), acc = 0;
const head = new THREE.Vector3();
function loop(now) {
  requestAnimationFrame(loop);
  const dt = Math.min(0.1, (now - last) / 1000);
  acc += now - last;
  last = now;
  if (grid) for (const c of chars.values()) update(c, now, dt);
  if (acc < 1000 / FPS_CAP) return;
  acc = 0;
  for (const c of chars.values()) {
    setFrame(c, now);
    const text = statusText(c);
    if (c.status.textContent !== text) c.status.textContent = text;
    head.copy(c.root.position);
    head.y = CHAR_H / ELEV_COS + 0.1; // 판 위 끝 위 (판은 눌려 보이는 만큼 늘려 세웠다)
    head.project(camera);
    c.el.style.left = `${(head.x + 1) / 2 * view.clientWidth}px`;
    c.el.style.top = `${(1 - head.y) / 2 * view.clientHeight}px`;
  }
  renderer.render(scene, camera);
}

let saved = null;
try { saved = Number(localStorage.getItem(seedKey)) || null; } catch { }
newHouse.disabled = true;
await rebuild(saved ?? hashSeed(data.room));
newHouse.disabled = false;
connect();
requestAnimationFrame(loop);
