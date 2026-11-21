import { describe, expect, it } from 'vitest';
import type { Catalog, NodeSpec } from './catalog';
import {
  addNode, connect, connectProblem, disconnect, docText, edgesOf, moveNode, newRuleDoc, parseDoc, positions,
  removeNodes, renameNode, renameProblem, setField, setHead, setNote, noteOf, type RuleDoc,
} from './model';

const sites = ['Scope', 'Types', 'Methods'] as const;
const node = (kind: string, output: NodeSpec['output'], inputs: NodeSpec['inputs'], fields: NodeSpec['fields'] = []): NodeSpec =>
  ({ kind, title: kind, output, inputs, fields });

// 서버 카탈로그(NodeCatalog)에서 시험에 쓰는 것만
const catalog: Catalog = {
  scopeName: 'scope', reportKind: 'report', sockets: ['Scope', 'Types', 'Methods', 'Sites', 'Groups'],
  severities: ['error', 'warning', 'info'], idPrefix: 'CLL', idFirst: 1, idLast: 200, maxNodes: 256, head: [],
  nodes: [
    node('types.derived', 'Types', [{ name: 'from', many: false, accepts: ['Scope', 'Types'] }], [{ name: 'type', type: 'string', required: true, description: '' }]),
    node('methods.named', 'Methods', [{ name: 'from', many: false, accepts: ['Types'] }], [{ name: 'names', type: 'stringArray', required: true, description: '' }]),
    node('sites.member', 'Sites', [{ name: 'from', many: false, accepts: [...sites] }], [{ name: 'members', type: 'stringArray', required: true, description: '' }]),
    node('sites.nullOperator', 'Sites', [{ name: 'from', many: false, accepts: [...sites] }]),
    node('filter.targetType', 'Sites', [{ name: 'from', many: false, accepts: ['Sites'] }], [{ name: 'type', type: 'string', required: false, description: '' }]),
    node('sites.union', 'Sites', [{ name: 'from', many: true, accepts: ['Sites'] }]),
    node('groups.pair', 'Groups', [{ name: 'a', many: false, accepts: ['Sites'] }, { name: 'b', many: false, accepts: ['Sites'] }]),
    node('report', null, [{ name: 'from', many: false, accepts: ['Sites', 'Groups'] }], [{ name: 'message', type: 'string', required: true, description: '' }]),
  ],
};

// samples/no-getcomponent-in-update.hblint.json 과 같은 모양 (layout 없음 — 손으로 쓴 파일)
const sample = `{
  "$schema": "./hblint.schema.json",
  "version": 1,
  "id": "CLL0002",
  "title": "Update 계열에서 GetComponent 금지",
  "severity": "warning",
  "requires": ["UnityEngine.CoreModule"],
  "nodes": {
    "monoBehaviour": { "kind": "types.derived", "from": "scope", "type": "UnityEngine.MonoBehaviour" },
    "updates": { "kind": "methods.named", "from": "monoBehaviour", "names": ["Update"] },
    "getComponent": { "kind": "sites.member", "from": "updates", "members": ["UnityEngine.Component.GetComponent"] },
    "report": { "kind": "report", "from": "getComponent", "message": "m" }
  }
}`;

const keys = (doc: RuleDoc, name: string) => Object.keys((doc.nodes as Record<string, object>)[name]);

describe('그래프 ↔ 규칙 파일', () => {
  it('입력 칸이 선이 된다', () => {
    expect(edgesOf(parseDoc(sample), catalog)).toEqual([
      { source: 'scope', target: 'monoBehaviour', input: 'from' },
      { source: 'monoBehaviour', target: 'updates', input: 'from' },
      { source: 'updates', target: 'getComponent', input: 'from' },
      { source: 'getComponent', target: 'report', input: 'from' },
    ]);
  });

  it('layout이 없으면 입력 길이로 열을 정해 자동 배치', () => {
    const pos = positions(parseDoc(sample), catalog);
    expect(pos.get('scope')).toEqual({ x: 0, y: 0 });
    expect(pos.get('monoBehaviour')!.x).toBeLessThan(pos.get('updates')!.x);
    expect(pos.get('updates')!.x).toBeLessThan(pos.get('getComponent')!.x);
    expect(pos.get('getComponent')!.x).toBeLessThan(pos.get('report')!.x);
  });

  it('열 때 계산한 자리를 주면 선을 끊어도 노드가 뛰지 않는다', () => {
    const doc = parseDoc(sample);
    const opened = positions(doc, catalog);
    const cut = disconnect(doc, catalog, { source: 'getComponent', target: 'report', input: 'from' });
    expect(positions(cut, catalog).get('report')).not.toEqual(opened.get('report'));
    expect(positions(cut, catalog, opened).get('report')).toEqual(opened.get('report'));
  });

  it('옮긴 위치는 layout.nodes에 남고 다른 칸 순서는 그대로', () => {
    const doc = moveNode(parseDoc(sample), 'updates', { x: 10.4, y: 20.6 });
    expect(Object.keys(doc)).toEqual(['$schema', 'version', 'id', 'title', 'severity', 'requires', 'nodes', 'layout']);
    expect(doc.layout).toEqual({ nodes: { updates: { x: 10, y: 21 } } });
    expect(positions(doc, catalog).get('updates')).toEqual({ x: 10, y: 21 });
  });

  it('메모는 layout에, 비우면 지움', () => {
    let doc = setNote(parseDoc(sample), 'report', '확인용');
    expect(noteOf(doc, 'report')).toBe('확인용');
    doc = setNote(doc, 'report', '');
    expect(doc.layout).toEqual({ nodes: { report: {} } });
  });

  it('JSON 글로 다시 쓰면 같은 내용', () => {
    expect(parseDoc(docText(parseDoc(sample)))).toEqual(parseDoc(sample));
  });
});

describe('잇기', () => {
  it('받는 소켓만 이을 수 있다', () => {
    const doc = parseDoc(sample);
    expect(connectProblem(doc, catalog, 'scope', 'updates', 'from')).toContain('Types만 받습니다');
    expect(connectProblem(doc, catalog, 'report', 'getComponent', 'from')).toContain('내보내는 소켓');
    expect(connectProblem(doc, catalog, 'monoBehaviour', 'getComponent', 'from')).toBeNull();
  });

  it('고리가 생기면 막는다', () => {
    const doc = parseDoc(sample);
    expect(connectProblem(doc, catalog, 'getComponent', 'monoBehaviour', 'from')).not.toBeNull();
    expect(connectProblem(doc, catalog, 'updates', 'updates', 'from')).not.toBeNull();
  });

  it('하나만 받는 입력은 바꾸고 여럿 받는 입력은 더한다', () => {
    let { doc } = addNode(parseDoc(sample), 'sites.nullOperator', { x: 0, y: 0 });
    doc = connect(doc, catalog, 'scope', 'sitesNullOperator', 'from');
    ({ doc } = addNode(doc, 'sites.union', { x: 0, y: 0 }));
    doc = connect(doc, catalog, 'getComponent', 'sitesUnion', 'from');
    doc = connect(doc, catalog, 'sitesNullOperator', 'sitesUnion', 'from');
    expect(connectProblem(doc, catalog, 'sitesNullOperator', 'sitesUnion', 'from')).toBe('이미 이어져 있습니다');
    doc = connect(doc, catalog, 'sitesUnion', 'report', 'from');

    const nodes = doc.nodes as Record<string, Record<string, unknown>>;
    expect(nodes.sitesUnion.from).toEqual(['getComponent', 'sitesNullOperator']);
    expect(nodes.report.from).toBe('sitesUnion');
    expect(keys(doc, 'report')).toEqual(['kind', 'from', 'message']);
  });

  it('짝 묶기는 a · b 손잡이를 따로', () => {
    let { doc } = addNode(parseDoc(sample), 'groups.pair', { x: 0, y: 0 });
    doc = connect(doc, catalog, 'getComponent', 'groupsPair', 'a');
    doc = connect(doc, catalog, 'getComponent', 'groupsPair', 'b');
    expect((doc.nodes as Record<string, unknown>).groupsPair).toEqual({ kind: 'groups.pair', a: 'getComponent', b: 'getComponent' });
  });

  it('새 입력은 kind 다음 · 값 칸 앞에', () => {
    let { doc } = addNode(parseDoc(sample), 'filter.targetType', { x: 0, y: 0 });
    doc = setField(doc, 'filterTargetType', 'type', 'UnityEngine.Object');
    doc = connect(doc, catalog, 'getComponent', 'filterTargetType', 'from');
    expect(keys(doc, 'filterTargetType')).toEqual(['kind', 'from', 'type']);
  });

  it('선을 끊으면 입력 칸이 빠진다', () => {
    let doc = disconnect(parseDoc(sample), catalog, { source: 'updates', target: 'getComponent', input: 'from' });
    expect(keys(doc, 'getComponent')).toEqual(['kind', 'members']);
    let added = addNode(doc, 'sites.union', { x: 0, y: 0 });
    doc = connect(connect(added.doc, catalog, 'getComponent', 'sitesUnion', 'from'), catalog, 'scope', 'sitesUnion', 'from');
    doc = disconnect(doc, catalog, { source: 'getComponent', target: 'sitesUnion', input: 'from' });
    expect((doc.nodes as Record<string, Record<string, unknown>>).sitesUnion.from).toBeUndefined();
    added = addNode(doc, 'sites.nullOperator', { x: 0, y: 0 });
    expect(added.name).toBe('sitesNullOperator');
  });
});

describe('노드 편집', () => {
  it('노드를 지우면 그것을 받던 입력도 빠진다', () => {
    const doc = removeNodes(moveNode(parseDoc(sample), 'updates', { x: 1, y: 1 }), catalog, ['updates', 'scope']);
    expect(Object.keys(doc.nodes as object)).toEqual(['monoBehaviour', 'getComponent', 'report']);
    expect(keys(doc, 'getComponent')).toEqual(['kind', 'members']);
    expect(doc.layout).toBeUndefined();
  });

  it('더했다 지운 노드는 파일을 바꾸지 않는다', () => {
    const before = parseDoc(sample);
    const added = addNode(before, 'sites.nullOperator', { x: 3, y: 4 });
    expect(docText(removeNodes(added.doc, catalog, [added.name]))).toBe(docText(before));
  });

  it('이름을 바꾸면 가리키는 입력 · 위치도 따라간다 (순서 유지)', () => {
    const doc = renameNode(moveNode(parseDoc(sample), 'updates', { x: 5, y: 5 }), catalog, 'updates', 'tick');
    expect(Object.keys(doc.nodes as object)).toEqual(['monoBehaviour', 'tick', 'getComponent', 'report']);
    expect((doc.nodes as Record<string, Record<string, unknown>>).getComponent.from).toBe('tick');
    expect(doc.layout).toEqual({ nodes: { tick: { x: 5, y: 5 } } });
  });

  it('__proto__ 이름 노드도 다른 노드를 고쳐도 남는다', () => {
    const text = sample.replace('"report": {', '"__proto__": { "kind": "sites.nullOperator", "from": "scope" },\n    "report": {');
    let doc = parseDoc(text);
    doc = setField(doc, 'monoBehaviour', 'type', 'UnityEngine.Behaviour');
    doc = moveNode(doc, '__proto__', { x: 1, y: 2 });
    const back = JSON.parse(docText(doc)) as { nodes: Record<string, unknown>; layout: { nodes: Record<string, unknown> } };
    expect(Object.keys(back.nodes)).toContain('__proto__');
    expect(Object.prototype.hasOwnProperty.call(back.layout.nodes, '__proto__')).toBe(true);
    expect(edgesOf(doc, catalog)).toContainEqual({ source: 'scope', target: '__proto__', input: 'from' });
  });

  it('잘못된 이름은 거절', () => {
    const doc = parseDoc(sample);
    expect(renameProblem(doc, 'updates', 'scope')).not.toBeNull();
    expect(renameProblem(doc, 'updates', 'report')).not.toBeNull();
    expect(renameProblem(doc, 'updates', '한글')).not.toBeNull();
    expect(renameProblem(doc, 'updates', 'ok_1.a-b')).toBeNull();
  });

  it('칸 값을 바꿔도 키 자리는 그대로, undefined면 지움', () => {
    let doc = setField(parseDoc(sample), 'monoBehaviour', 'type', 'UnityEngine.Behaviour');
    expect(keys(doc, 'monoBehaviour')).toEqual(['kind', 'from', 'type']);
    doc = setField(doc, 'monoBehaviour', 'type', undefined);
    expect(keys(doc, 'monoBehaviour')).toEqual(['kind', 'from']);
  });

  it('새 머리 칸은 nodes 앞에', () => {
    const doc = setHead(parseDoc(sample), 'ignoreAssemblies', ['*.Editor']);
    expect(Object.keys(doc)).toEqual(['$schema', 'version', 'id', 'title', 'severity', 'requires', 'ignoreAssemblies', 'nodes']);
  });

  it('새 규칙은 report 하나와 다음 id', () => {
    const doc = newRuleDoc('CLL0013');
    expect(doc.id).toBe('CLL0013');
    expect(Object.keys(doc.nodes as object)).toEqual(['report']);
  });
});
