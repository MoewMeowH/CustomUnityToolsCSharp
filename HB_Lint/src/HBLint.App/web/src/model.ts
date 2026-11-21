import { findSpec, type Catalog, type InputSpec, type NodeSpec, type Socket } from './catalog';

// 편집 중인 규칙 = 규칙 파일 JSON 그대로 (IR + $schema + layout). 그래프 화면은 이것에서 매번 만든다 —
// 선(edge)은 노드의 입력 칸(from · a · b)이고 위치는 layout.nodes. 그래서 되돌리기는 문서 사본만 쌓으면 된다.
// 키 순서는 JS 객체 순서를 따른다 (숫자만인 노드 이름은 앞으로 올 수 있음 — 뜻은 같고 서버가 그 순서로 쓴다).

export type Json = null | boolean | number | string | Json[] | { [key: string]: Json };
export type JsonObject = { [key: string]: Json };
export type RuleDoc = JsonObject;

export interface Pos {
  x: number;
  y: number;
}

export interface Edge {
  source: string;
  target: string;
  input: string;
}

export const SCOPE = 'scope';
const nodeName = /^[A-Za-z0-9_.-]+$/;
const columnWidth = 280;
const rowHeight = 130;

const isObject = (value: Json | undefined): value is JsonObject => typeof value === 'object' && value !== null && !Array.isArray(value);

/** 새 사전 — 프로토타입이 없어 `__proto__`(노드 이름 규칙이 받는 이름)도 보통 키가 된다. 일반 {}에 대입하면 프로토타입을 바꿔 그 노드가 사라진다. */
const dict = (): JsonObject => Object.create(null) as JsonObject;

export function parseDoc(text: string): RuleDoc {
  const value = JSON.parse(text) as Json;
  if (!isObject(value)) throw new Error('규칙은 JSON 객체여야 합니다');
  return value;
}

export const docText = (doc: RuleDoc) => JSON.stringify(doc, null, 2) + '\n';

export function newRuleDoc(id: string): RuleDoc {
  return {
    $schema: './hblint.schema.json',
    version: 1,
    id,
    title: '',
    severity: 'warning',
    nodes: { report: { kind: 'report', message: '' } },
    layout: { nodes: { [SCOPE]: { x: 0, y: 0 }, report: { x: columnWidth * 3, y: 0 } } },
  };
}

export function nodesOf(doc: RuleDoc): [string, JsonObject][] {
  const nodes = doc.nodes;
  if (!isObject(nodes)) return [];
  return Object.entries(nodes).filter((entry): entry is [string, JsonObject] => isObject(entry[1]));
}

export function nodeData(doc: RuleDoc, name: string): JsonObject | undefined {
  const nodes = doc.nodes;
  return isObject(nodes) && isObject(nodes[name]) ? (nodes[name] as JsonObject) : undefined;
}

/** 그 입력 칸이 받는 노드 이름들 (문자열 하나 또는 배열). */
export function sourcesOf(data: JsonObject, input: InputSpec): string[] {
  const value = data[input.name];
  if (typeof value === 'string') return [value];
  if (Array.isArray(value)) return value.filter((v): v is string => typeof v === 'string');
  return [];
}

export function edgesOf(doc: RuleDoc, catalog: Catalog): Edge[] {
  const edges: Edge[] = [];
  for (const [name, data] of nodesOf(doc)) {
    const spec = findSpec(catalog, data.kind);
    if (!spec) continue;
    for (const input of spec.inputs)
      for (const source of sourcesOf(data, input)) edges.push({ source, target: name, input: input.name });
  }
  return edges;
}

export function outputSocket(doc: RuleDoc, catalog: Catalog, name: string): Socket | null | undefined {
  if (name === SCOPE) return 'Scope';
  const data = nodeData(doc, name);
  return data ? findSpec(catalog, data.kind)?.output : undefined;
}

/** name이 (입력을 거슬러) 기대는 노드들. */
function ancestors(doc: RuleDoc, catalog: Catalog, name: string): Set<string> {
  const seen = new Set<string>();
  const stack = [name];
  while (stack.length > 0) {
    const current = stack.pop()!;
    const data = nodeData(doc, current);
    const spec = data && findSpec(catalog, data.kind);
    if (!data || !spec) continue;
    for (const input of spec.inputs)
      for (const source of sourcesOf(data, input))
        if (!seen.has(source)) {
          seen.add(source);
          stack.push(source);
        }
  }
  return seen;
}

/** 이을 수 없으면 까닭, 되면 null. */
export function connectProblem(doc: RuleDoc, catalog: Catalog, source: string, target: string, inputName: string): string | null {
  const data = nodeData(doc, target);
  const spec = data && findSpec(catalog, data.kind);
  const input = spec?.inputs.find((i) => i.name === inputName);
  if (!data || !spec || !input) return '받는 쪽 입력이 없습니다';
  if (source === target) return '자기 자신에는 이을 수 없습니다';
  const socket = outputSocket(doc, catalog, source);
  if (!socket) return `'${source}'는 내보내는 소켓이 없습니다`;
  if (!input.accepts.includes(socket)) return `${spec.kind}.${input.name}는 ${input.accepts.join(' · ')}만 받습니다 ('${source}'는 ${socket})`;
  if (ancestors(doc, catalog, source).has(target)) return '이으면 고리가 생깁니다';
  if (input.many && sourcesOf(data, input).includes(source)) return '이미 이어져 있습니다';
  return null;
}

function withNodes(doc: RuleDoc, nodes: JsonObject): RuleDoc {
  return withKey(doc, 'nodes', nodes, 'layout');
}

/** 있던 키면 그 자리에서 바꾸고, 없으면 before 키 앞(없으면 끝)에 넣는다. value가 undefined면 지운다. */
export function withKey(obj: JsonObject, key: string, value: Json | undefined, before?: string): JsonObject {
  const result: JsonObject = dict();
  let placed = false;
  for (const [k, v] of Object.entries(obj)) {
    if (k === key) {
      if (value !== undefined) result[k] = value;
      placed = true;
      continue;
    }
    if (!placed && value !== undefined && k === before && !(key in obj)) {
      result[key] = value;
      placed = true;
    }
    result[k] = v;
  }
  if (!placed && value !== undefined) result[key] = value;
  return result;
}

function updateNode(doc: RuleDoc, name: string, update: (data: JsonObject) => JsonObject): RuleDoc {
  const nodes: JsonObject = dict();
  for (const [k, v] of nodesOf(doc)) nodes[k] = k === name ? update(v) : v;
  return withNodes(doc, nodes);
}

export function connect(doc: RuleDoc, catalog: Catalog, source: string, target: string, inputName: string): RuleDoc {
  if (connectProblem(doc, catalog, source, target, inputName)) return doc;
  const spec = findSpec(catalog, nodeData(doc, target)!.kind)!;
  const input = spec.inputs.find((i) => i.name === inputName)!;
  return updateNode(doc, target, (data) => withKey(data, input.name, input.many ? [...sourcesOf(data, input), source] : source, firstFieldKey(spec, data)));
}

/** 입력 칸은 kind 다음 · 다른 칸 앞에 둔다 (CLI 예시와 같은 순서). */
function firstFieldKey(spec: NodeSpec, data: JsonObject): string | undefined {
  return Object.keys(data).find((k) => k !== 'kind' && !spec.inputs.some((i) => i.name === k));
}

export function disconnect(doc: RuleDoc, catalog: Catalog, edge: Edge): RuleDoc {
  const data = nodeData(doc, edge.target);
  const spec = data && findSpec(catalog, data.kind);
  const input = spec?.inputs.find((i) => i.name === edge.input);
  if (!data || !input) return doc;
  return updateNode(doc, edge.target, (d) => {
    if (!input.many) return withKey(d, input.name, undefined);
    const rest = sourcesOf(d, input).filter((s) => s !== edge.source);
    return withKey(d, input.name, rest.length > 0 ? rest : undefined);
  });
}

export function uniqueName(doc: RuleDoc, kind: string): string {
  const base = kind === 'report' ? 'report' : kind.replace(/\.(\w)/g, (_, c: string) => c.toUpperCase());
  const taken = new Set(nodesOf(doc).map(([n]) => n));
  if (!taken.has(base) && base !== SCOPE) return base;
  for (let i = 2; ; i++) if (!taken.has(base + i)) return base + i;
}

export function addNode(doc: RuleDoc, kind: string, pos: Pos): { doc: RuleDoc; name: string } {
  const name = uniqueName(doc, kind);
  const nodes: JsonObject = dict();
  for (const [k, v] of nodesOf(doc)) nodes[k] = v;
  nodes[name] = { kind };
  return { doc: moveNode(withNodes(doc, nodes), name, pos), name };
}

export function removeNodes(doc: RuleDoc, catalog: Catalog, names: string[]): RuleDoc {
  const gone = new Set(names.filter((n) => n !== SCOPE));
  const nodes: JsonObject = dict();
  for (const [name, data] of nodesOf(doc)) {
    if (gone.has(name)) continue;
    const spec = findSpec(catalog, data.kind);
    let next = data;
    for (const input of spec?.inputs ?? []) {
      const sources = sourcesOf(next, input);
      if (!sources.some((s) => gone.has(s))) continue;
      const rest = sources.filter((s) => !gone.has(s));
      next = withKey(next, input.name, input.many ? (rest.length > 0 ? rest : undefined) : undefined);
    }
    nodes[name] = next;
  }
  let result = withNodes(doc, nodes);
  const layout = layoutNodes(result);
  if (layout) {
    const kept: JsonObject = dict();
    for (const [k, v] of Object.entries(layout)) if (!gone.has(k)) kept[k] = v;
    // 위치가 하나도 안 남고 layout에 다른 칸이 없으면 layout째 뺀다 (방금 더하고 지운 노드가 파일에 빈 layout을 남기지 않게)
    const onlyNodes = isObject(result.layout) && Object.keys(result.layout).length === 1;
    result = Object.keys(kept).length === 0 && onlyNodes ? withKey(result, 'layout', undefined) : withLayoutNodes(result, kept);
  }
  return result;
}

/** 잘못된 이름이면 까닭, 되면 null. */
export function renameProblem(doc: RuleDoc, from: string, to: string): string | null {
  if (to === from) return null;
  if (!nodeName.test(to)) return '노드 이름은 영문 · 숫자 · . _ -만 씁니다';
  if (to === SCOPE) return `'${SCOPE}'는 규칙 시작 이름이라 쓸 수 없습니다`;
  if (nodeData(doc, to)) return `'${to}' 노드가 이미 있습니다`;
  return null;
}

export function renameNode(doc: RuleDoc, catalog: Catalog, from: string, to: string): RuleDoc {
  if (renameProblem(doc, from, to) || from === to || from === SCOPE) return doc;
  const nodes: JsonObject = dict();
  for (const [name, data] of nodesOf(doc)) {
    const spec = findSpec(catalog, data.kind);
    let next = data;
    for (const input of spec?.inputs ?? []) {
      const sources = sourcesOf(next, input);
      if (!sources.includes(from)) continue;
      const renamed = sources.map((s) => (s === from ? to : s));
      next = withKey(next, input.name, input.many ? renamed : renamed[0]);
    }
    nodes[name === from ? to : name] = next;
  }
  let result = withNodes(doc, nodes);
  const layout = layoutNodes(result);
  if (layout && from in layout) {
    const renamed: JsonObject = dict();
    for (const [k, v] of Object.entries(layout)) renamed[k === from ? to : k] = v;
    result = withLayoutNodes(result, renamed);
  }
  return result;
}

export function setField(doc: RuleDoc, name: string, field: string, value: Json | undefined): RuleDoc {
  return updateNode(doc, name, (data) => withKey(data, field, value));
}

/** 머리 칸 (id · title · severity · requires · ignoreAssemblies) — 새 칸은 nodes 앞에. */
export function setHead(doc: RuleDoc, field: string, value: Json | undefined): RuleDoc {
  return withKey(doc, field, value, 'nodes');
}

function layoutNodes(doc: RuleDoc): JsonObject | undefined {
  const layout = doc.layout;
  return isObject(layout) && isObject(layout.nodes) ? layout.nodes : undefined;
}

function withLayoutNodes(doc: RuleDoc, nodes: JsonObject): RuleDoc {
  const layout = isObject(doc.layout) ? doc.layout : {};
  return withKey(doc, 'layout', withKey(layout, 'nodes', nodes));
}

export function moveNode(doc: RuleDoc, name: string, pos: Pos): RuleDoc {
  const layout = layoutNodes(doc) ?? {};
  const previous = isObject(layout[name]) ? (layout[name] as JsonObject) : {};
  return withLayoutNodes(doc, withKey(layout, name, { ...previous, x: Math.round(pos.x), y: Math.round(pos.y) }));
}

export function noteOf(doc: RuleDoc, name: string): string {
  const entry = layoutNodes(doc)?.[name];
  return isObject(entry) && typeof entry.note === 'string' ? entry.note : '';
}

export function setNote(doc: RuleDoc, name: string, note: string): RuleDoc {
  const layout = layoutNodes(doc) ?? {};
  const previous = isObject(layout[name]) ? (layout[name] as JsonObject) : {};
  return withLayoutNodes(doc, withKey(layout, name, withKey(previous, 'note', note.length > 0 ? note : undefined)));
}

/**
 * 노드 위치. layout에 있으면 그대로, 없으면(손으로 쓴 파일) fallback(열 때 계산해 둔 자리 — 선을 바꿔도 노드가 뛰지 않게),
 * 그것도 없으면 열 = scope에서 가장 긴 입력 길이, 행 = 그 열 안 순서.
 */
export function positions(doc: RuleDoc, catalog: Catalog, fallback?: Map<string, Pos>): Map<string, Pos> {
  const result = new Map<string, Pos>();
  const layout = layoutNodes(doc) ?? {};
  const names = [SCOPE, ...nodesOf(doc).map(([n]) => n)];
  const depth = new Map<string, number>([[SCOPE, 0]]);
  const depthOf = (name: string, visiting: Set<string>): number => {
    const known = depth.get(name);
    if (known !== undefined) return known;
    const data = nodeData(doc, name);
    const spec = data && findSpec(catalog, data.kind);
    if (!data || !spec || visiting.has(name)) return 1;
    visiting.add(name);
    let d = 1;
    for (const input of spec.inputs)
      for (const source of sourcesOf(data, input)) if (source === SCOPE || nodeData(doc, source)) d = Math.max(d, depthOf(source, visiting) + 1);
    visiting.delete(name);
    depth.set(name, d);
    return d;
  };
  const rows = new Map<number, number>();
  for (const name of names) {
    const entry = layout[name];
    if (isObject(entry) && typeof entry.x === 'number' && typeof entry.y === 'number') {
      result.set(name, { x: entry.x, y: entry.y });
      continue;
    }
    const kept = fallback?.get(name);
    if (kept) {
      result.set(name, kept);
      continue;
    }
    const column = depthOf(name, new Set());
    const row = rows.get(column) ?? 0;
    rows.set(column, row + 1);
    result.set(name, { x: column * columnWidth, y: row * rowHeight });
  }
  return result;
}

/** 노드 카드에 보일 한 줄 요약 (가장 앞의 값 칸). */
export function summary(spec: NodeSpec, data: JsonObject): string {
  for (const field of spec.fields) {
    const value = data[field.name];
    if (typeof value === 'string' && value.length > 0) return value;
    if (Array.isArray(value) && value.length > 0) return value.map(String).join(', ');
    if (typeof value === 'number' || typeof value === 'boolean') return `${field.name}: ${value}`;
  }
  return '';
}
