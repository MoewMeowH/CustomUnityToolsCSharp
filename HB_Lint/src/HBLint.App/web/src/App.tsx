import { useCallback, useEffect, useMemo, useRef, useState, type DragEvent } from 'react';
import {
  Background, Controls, MiniMap, ReactFlow, useReactFlow,
  type Connection, type Edge as FlowEdge, type EdgeChange, type NodeChange,
} from '@xyflow/react';
import { api, ApiError, type AppStateDto, type GenerateResult, type PreviewResult, type RuleSummary, type Validation } from './api';
import { findSpec, paletteGroup, socketColor, type Catalog } from './catalog';
import { Inspector } from './Inspector';
import {
  addNode, connect, connectProblem, disconnect, docText, edgesOf, moveNode, newRuleDoc, nodesOf, noteOf, outputSocket,
  parseDoc, positions, removeNodes, renameNode, SCOPE, setField, setHead, setNote, summary, type Edge, type Pos, type RuleDoc,
} from './model';
import { OpenPanel } from './OpenPanel';
import { Problems, type Tab } from './Problems';
import { useHistory } from './history';
import { RuleNode, type RuleFlowNode } from './RuleNode';

const nodeTypes = { rule: RuleNode };
// 서버 RuleFolder와 같은 이름 규칙 (글자 · 숫자 · _ . -)
const ruleFileName = /^[\p{L}\p{N}_.-]+\.hblint\.json$/u;
const edgeId = (e: Edge) => `${e.source}>${e.target}.${e.input}`;

interface Open {
  file: string;
  /** 아직 디스크에 없는 새 규칙 (처음 저장은 POST). */
  created: boolean;
  savedText: string;
}

const emptyDoc: RuleDoc = { nodes: {} };

export function App() {
  const [catalog, setCatalog] = useState<Catalog | null>(null);
  const [state, setState] = useState<AppStateDto | null>(null);
  const [rules, setRules] = useState<RuleSummary[]>([]);
  const [nextId, setNextId] = useState<string | null>(null);
  const [open, setOpen] = useState<Open | null>(null);
  const history = useHistory<RuleDoc>(emptyDoc);
  const doc = history.present;
  const [selected, setSelected] = useState<string | null>(null);
  const [selectedEdge, setSelectedEdge] = useState<string | null>(null);
  const [validation, setValidation] = useState<Validation | null>(null);
  const [preview, setPreview] = useState<PreviewResult | null>(null);
  const [generate, setGenerate] = useState<GenerateResult | null>(null);
  const [tab, setTab] = useState<Tab>('problems');
  const [status, setStatus] = useState<{ text: string; error: boolean } | null>(null);
  /** 열 때 자동 배치한 자리 — layout이 없는 노드가 선을 바꿔도 뛰지 않게. */
  const [opened, setOpened] = useState<Map<string, Pos>>(new Map());
  const [busy, setBusy] = useState<string | null>(null);
  /** 대상 프로젝트 폴더의 csproj 후보 (읽기 성공 · 실패 응답에서). */
  const [candidates, setCandidates] = useState<string[]>([]);
  /** React Flow가 잰 노드 크기 — 통제 모드라 돌려 넣어야 미니맵 · 맞춤 보기가 노드를 안다. */
  const [measured, setMeasured] = useState<Map<string, { width: number; height: number }>>(new Map());
  const flow = useReactFlow();
  const text = useMemo(() => docText(doc), [doc]);
  const dirty = open !== null && (open.created || text !== open.savedText);

  const fail = (e: unknown) => setStatus({ text: e instanceof Error ? e.message : String(e), error: true });
  const info = (t: string) => setStatus({ text: t, error: false });

  const refreshRules = useCallback(async () => {
    const list = await api.rules();
    setRules(list.rules);
    setNextId(list.nextId);
  }, []);

  // 처음: 카탈로그 · 상태, 실행 인자로 경로를 받았으면 바로 연다
  useEffect(() => {
    (async () => {
      if (!api.hasToken()) throw new Error('토큰이 없습니다 — hblint-app이 연 주소(#token=…)로 여세요');
      const [c, s] = await Promise.all([api.catalog(), api.state()]);
      setCatalog(c);
      setState(s);
      setCandidates(s.project?.candidates ?? []);
      if (s.rules) await refreshRules();
      else if (s.defaults.rules && s.defaults.out) {
        const opened = await api.openWorkspace(s.defaults.rules, s.defaults.out);
        setState(opened);
        await refreshRules();
        if (s.defaults.project) await loadProject(s.defaults.project, null);
      }
    })().catch(fail);
  }, [refreshRules]);

  // 편집할 때마다 (잠깐 멈춘 뒤) 서버 검증 · 이름 확인
  useEffect(() => {
    if (!open) return;
    const timer = setTimeout(() => api.validate(text).then(setValidation).catch(fail), 350);
    return () => clearTimeout(timer);
  }, [text, open, state?.project]);

  const confirmLeave = () => !dirty || window.confirm(`${open?.file}에 저장하지 않은 변경이 있습니다. 버리고 넘어갈까요?`);

  const openRule = async (file: string) => {
    if (!confirmLeave()) return;
    try {
      const rule = await api.rule(file);
      const parsed = parseDoc(rule.text);
      if (catalog) setOpened(positions(parsed, catalog));
      history.reset(parsed);
      setOpen({ file, created: false, savedText: docText(parsed) });
      setValidation(rule.validation);
      setPreview(null);
      setSelected(null);
      setTimeout(() => flow.fitView({ padding: 0.2 }), 50);
    } catch (e) {
      fail(e);
    }
  };

  const createRule = () => {
    if (!confirmLeave() || !nextId) return;
    const file = window.prompt('새 규칙 파일 이름 (글자 · 숫자 · . _ - 와 .hblint.json)', `rule-${nextId.toLowerCase()}.hblint.json`);
    if (!file) return;
    if (!ruleFileName.test(file) || file.includes('..')) return fail('파일 이름은 글자 · 숫자 · . _ - 와 .hblint.json이어야 합니다');
    if (rules.some((r) => r.file.toLowerCase() === file.toLowerCase())) return fail(`${file}이(가) 이미 있습니다`);
    history.reset(newRuleDoc(nextId));
    setOpened(new Map());
    setOpen({ file, created: true, savedText: '' });
    setValidation(null);
    setPreview(null);
    setSelected(SCOPE);
    setTimeout(() => flow.fitView({ padding: 0.4 }), 50);
  };

  // 저장은 그 순간의 파일 · 글로 — 응답이 오기 전에 다른 규칙을 열었으면 그 규칙의 상태는 건드리지 않는다
  // (응답으로 파일 이름을 되돌리면 다음 저장이 지금 화면의 규칙을 앞 파일에 쓴다)
  const latest = useRef({ open, text });
  latest.current = { open, text };
  const save = useCallback(async () => {
    const { open: target, text: body } = latest.current;
    if (!target) return;
    try {
      const saved = await api.save(target.file, body, target.created);
      const same = latest.current.open?.file === target.file;
      if (same) {
        setOpen((current) => (current && current.file === target.file ? { file: target.file, created: false, savedText: body } : current));
        setValidation(saved.validation);
      }
      info(saved.changed ? `${target.file} 저장함` : `${target.file} 바뀐 것 없음`);
      await refreshRules();
    } catch (e) {
      fail(e);
    }
  }, [refreshRules]);

  const runGenerate = async () => {
    if (dirty && !window.confirm('저장하지 않은 변경은 생성에 들어가지 않습니다 (폴더의 저장된 파일로 만듦). 그래도 생성할까요?')) return;
    try {
      const result = await api.generate();
      setGenerate(result);
      setTab('generate');
      result.ok ? info(`생성함: ${result.output}`) : fail(`생성 안 함 — 오류 ${result.errors.length}개`);
    } catch (e) {
      fail(e);
    }
  };

  const runPreview = async () => {
    setBusy('미리 보기');
    try {
      setPreview(await api.preview(text));
      setTab('preview');
    } catch (e) {
      fail(e);
    } finally {
      setBusy(null);
    }
  };

  const openWorkspace = async (rulesPath: string, out: string) => {
    if (!confirmLeave()) return;
    try {
      setState(await api.openWorkspace(rulesPath, out));
      history.reset(emptyDoc);
      setOpen(null);
      setValidation(null);
      setGenerate(null);
      await refreshRules();
      info(`${rulesPath} 열었음`);
    } catch (e) {
      fail(e);
    }
  };

  const loadProject = async (path: string, csproj: string | null) => {
    setBusy('프로젝트 읽기');
    info('대상 프로젝트 읽는 중… (처음엔 몇 초)');
    try {
      const next = await api.loadProject(path, csproj);
      setState(next);
      setCandidates(next.project?.candidates ?? []);
      info(next.project ? `${next.project.assemblyName} 읽음 — 타입 ${next.project.types}개` : '대상 프로젝트를 닫았음');
    } catch (e) {
      // 고를 csproj가 여럿이면(Assembly-CSharp 없음 등) 서버가 후보를 같이 준다
      const found = e instanceof ApiError ? (e.data as { candidates?: string[] } | undefined)?.candidates : undefined;
      if (found && found.length > 0) setCandidates(found);
      fail(e);
    } finally {
      setBusy(null);
    }
  };

  // Ctrl+S 저장 · Ctrl+Z 되돌리기 · Ctrl+Y / Ctrl+Shift+Z 다시 (입력칸 안에서는 그 칸의 되돌리기)
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (!(e.ctrlKey || e.metaKey)) return;
      const key = e.key.toLowerCase();
      if (key === 's') {
        e.preventDefault();
        // 칸은 떠날 때 값을 넘긴다 — 먼저 칸을 떠나 입력 중인 값을 문서에 넣고, 그 렌더 뒤에 저장
        (document.activeElement as HTMLElement | null)?.blur();
        setTimeout(() => void save(), 0);
        return;
      }
      const target = e.target as HTMLElement;
      if (target.closest('input, textarea, select')) return;
      if (key === 'z' && !e.shiftKey) history.undo();
      else if (key === 'y' || (key === 'z' && e.shiftKey)) history.redo();
      else return;
      e.preventDefault();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [save, history.undo, history.redo]);

  useEffect(() => {
    const onUnload = (e: BeforeUnloadEvent) => {
      if (dirty) e.preventDefault();
    };
    window.addEventListener('beforeunload', onUnload);
    return () => window.removeEventListener('beforeunload', onUnload);
  }, [dirty]);

  // 문서 → 화면 노드 · 선
  const problemCount = useMemo(() => {
    const count = new Map<string, { errors: number; warnings: number }>();
    const add = (node: string, warning: boolean) => {
      const c = count.get(node) ?? { errors: 0, warnings: 0 };
      warning ? c.warnings++ : c.errors++;
      count.set(node, c);
    };
    for (const e of validation?.errors ?? []) add(e.node ?? SCOPE, e.warning);
    for (const n of validation?.names ?? []) add(n.node, true);
    return count;
  }, [validation]);

  const nodes: RuleFlowNode[] = useMemo(() => {
    if (!catalog || !open) return [];
    const pos = positions(doc, catalog, opened);
    const scope: RuleFlowNode = {
      id: SCOPE, type: 'rule', position: pos.get(SCOPE)!, deletable: false, selected: selected === SCOPE, measured: measured.get(SCOPE),
      data: {
        name: SCOPE, spec: null, scope: true, title: `${String(doc.id ?? '(id 없음)')}`, summary: String(doc.title ?? ''),
        errors: problemCount.get(SCOPE)?.errors ?? 0, warnings: problemCount.get(SCOPE)?.warnings ?? 0, note: noteOf(doc, SCOPE),
      },
    };
    return [scope, ...nodesOf(doc).map(([name, data]): RuleFlowNode => {
      const spec = findSpec(catalog, data.kind) ?? null;
      return {
        id: name, type: 'rule', position: pos.get(name)!, selected: selected === name, measured: measured.get(name),
        data: {
          name, spec, scope: false, title: spec?.title ?? `모르는 kind ${String(data.kind)}`, summary: spec ? summary(spec, data) : '',
          errors: problemCount.get(name)?.errors ?? 0, warnings: problemCount.get(name)?.warnings ?? 0, note: noteOf(doc, name),
        },
      };
    })];
  }, [catalog, open, doc, selected, problemCount, opened, measured]);

  const edges: FlowEdge[] = useMemo(() => {
    if (!catalog) return [];
    return edgesOf(doc, catalog).map((e) => {
      const socket = outputSocket(doc, catalog, e.source);
      return {
        id: edgeId(e), source: e.source, target: e.target, sourceHandle: 'out', targetHandle: e.input,
        selected: selectedEdge === edgeId(e),
        style: { stroke: socket ? socketColor[socket] : '#666', strokeWidth: 2 },
        data: e as unknown as Record<string, unknown>,
      };
    });
  }, [catalog, doc, selectedEdge]);

  const onNodesChange = useCallback((changes: NodeChange<RuleFlowNode>[]) => {
    let next = history.present;
    let moved = false;
    const sized: [string, { width: number; height: number }][] = [];
    for (const change of changes) {
      if (change.type === 'dimensions' && change.dimensions) {
        sized.push([change.id, change.dimensions]);
      } else if (change.type === 'position' && change.position) {
        next = moveNode(next, change.id, change.position);
        moved = true;
      } else if (change.type === 'select') {
        if (change.selected) {
          setSelected(change.id);
          setSelectedEdge(null);
        } else setSelected((s) => (s === change.id ? null : s));
      }
    }
    if (moved) history.replace(next);
    if (sized.length > 0)
      setMeasured((current) => {
        const copy = new Map(current);
        for (const [id, size] of sized) copy.set(id, size);
        return copy;
      });
  }, [history.present, history.replace]);

  const onEdgesChange = useCallback((changes: EdgeChange[]) => {
    for (const change of changes)
      if (change.type === 'select') setSelectedEdge(change.selected ? change.id : null);
  }, []);

  const isValidConnection = useCallback((c: Connection | FlowEdge) =>
    !!catalog && !!c.source && !!c.target && !!c.targetHandle && connectProblem(doc, catalog, c.source, c.target, c.targetHandle) === null,
  [catalog, doc]);

  const onConnect = useCallback((c: Connection) => {
    if (!catalog || !c.targetHandle) return;
    const problem = connectProblem(doc, catalog, c.source, c.target, c.targetHandle);
    if (problem) return fail(problem);
    history.commit(connect(doc, catalog, c.source, c.target, c.targetHandle));
  }, [catalog, doc, history.commit]);

  const onDelete = useCallback(({ nodes: gone, edges: cut }: { nodes: RuleFlowNode[]; edges: FlowEdge[] }) => {
    if (!catalog) return;
    let next = doc;
    for (const edge of cut) next = disconnect(next, catalog, edge.data as unknown as Edge);
    next = removeNodes(next, catalog, gone.map((n) => n.id));
    history.commit(next);
    setSelected(null);
    setSelectedEdge(null);
  }, [catalog, doc, history.commit]);

  const place = (kind: string, at?: { x: number; y: number }) => {
    if (!catalog || !open) return;
    const pos = at ?? flow.screenToFlowPosition({ x: window.innerWidth / 2, y: window.innerHeight / 2 });
    const added = addNode(doc, kind, pos);
    history.commit(added.doc);
    setSelected(added.name);
  };

  const onDrop = (e: DragEvent) => {
    e.preventDefault();
    const kind = e.dataTransfer.getData('application/hblint-kind');
    if (kind) place(kind, flow.screenToFlowPosition({ x: e.clientX, y: e.clientY }));
  };

  if (!catalog || !state) {
    return <div className="loading">{status?.error ? <span className="error">{status.text}</span> : '불러오는 중…'}</div>;
  }

  const groups = new Map<string, typeof catalog.nodes>();
  for (const spec of catalog.nodes) groups.set(paletteGroup(spec), [...(groups.get(paletteGroup(spec)) ?? []), spec]);

  return (
    <div className="app">
      <header>
        <strong>HB_Lint 규칙 편집</strong>
        <OpenPanel state={state} busy={busy} candidates={candidates} onOpen={openWorkspace} onProject={loadProject} />
      </header>
      <div className="main">
        <aside className="sidebar">
          <section>
            <h4>규칙 <button className="small" disabled={!state.rules || !nextId} onClick={createRule} title={nextId ? `다음 id ${nextId}` : ''}>+ 새 규칙</button></h4>
            {!state.rules && <p className="muted">규칙 폴더를 여세요</p>}
            <ul className="rule-list">
              {rules.map((r) => (
                <li
                  key={r.file}
                  className={`${open?.file === r.file ? 'on' : ''} ${r.editable ? '' : 'locked'}`}
                  onClick={() => r.editable && openRule(r.file)}
                  title={r.editable ? (r.title ?? '') : '앱에서 열 수 없는 파일 (이름에 글자 · 숫자 · _ . - 밖의 문자, 또는 폴더 밖을 가리키는 링크) — 생성에는 들어갑니다'}
                >
                  <span className="rule-id">{r.id ?? '?'}</span> {r.file.replace(/\.hblint\.json$/, '')}
                  {r.errors > 0 && <span className="badge error">{r.errors}</span>}
                </li>
              ))}
              {open?.created && <li className="on"><span className="rule-id">{String(doc.id)}</span> {open.file} (새)</li>}
            </ul>
          </section>
          <section className="palette">
            <h4>노드 (끌어 놓거나 누르기)</h4>
            {[...groups].map(([group, specs]) => (
              <div key={group}>
                <h5 style={{ color: group in socketColor ? socketColor[group as keyof typeof socketColor] : undefined }}>{group}</h5>
                {specs.map((spec) => (
                  <div
                    key={spec.kind}
                    className={`palette-item ${open ? '' : 'disabled'}`}
                    draggable={!!open}
                    onDragStart={(e) => e.dataTransfer.setData('application/hblint-kind', spec.kind)}
                    onClick={() => place(spec.kind)}
                    title={`${spec.kind} — 받음: ${spec.inputs.map((i) => `${i.name}(${i.accepts.join('·')})`).join(', ')}`}
                  >
                    {spec.title}
                  </div>
                ))}
              </div>
            ))}
          </section>
        </aside>
        <main className="canvas" onDragOver={(e) => e.preventDefault()} onDrop={onDrop}>
          <div className="toolbar">
            <span className="file">{open ? `${open.file}${dirty ? ' ●' : ''}` : '규칙을 고르세요'}</span>
            <button disabled={!open || !dirty} onClick={() => void save()} title="Ctrl+S">저장</button>
            <button disabled={!history.canUndo} onClick={history.undo} title="Ctrl+Z">되돌리기</button>
            <button disabled={!history.canRedo} onClick={history.redo} title="Ctrl+Y">다시</button>
            <button disabled={!state.rules} onClick={runGenerate}>생성</button>
            <button disabled={!open || !state.project || busy !== null} onClick={runPreview} title={state.project ? '' : '대상 프로젝트를 먼저 읽으세요'}>
              {busy === '미리 보기' ? '돌리는 중…' : '미리 보기'}
            </button>
            {status && <span className={`status ${status.error ? 'error' : ''}`} onClick={() => setStatus(null)}>{status.text}</span>}
          </div>
          {open ? (
            <ReactFlow
              nodes={nodes}
              edges={edges}
              nodeTypes={nodeTypes}
              onNodesChange={onNodesChange}
              onEdgesChange={onEdgesChange}
              onNodeDragStart={() => history.checkpoint()}
              onConnect={onConnect}
              isValidConnection={isValidConnection}
              onDelete={onDelete}
              onPaneClick={() => { setSelected(null); setSelectedEdge(null); }}
              deleteKeyCode={['Delete', 'Backspace']}
              minZoom={0.2}
              fitView
            >
              <Background />
              <Controls />
              <MiniMap pannable zoomable bgColor="#252931" maskColor="rgba(29, 32, 38, 0.7)" nodeColor={(n) => (n.data as { spec?: { output?: string } }).spec?.output ? socketColor[(n.data as { spec: { output: keyof typeof socketColor } }).spec.output] : socketColor.Scope} />
            </ReactFlow>
          ) : (
            <div className="placeholder">왼쪽에서 규칙을 고르거나 새 규칙을 만드세요.</div>
          )}
        </main>
        {open && (
          <Inspector
            catalog={catalog}
            doc={doc}
            selected={selected}
            projectLoaded={!!state.project}
            errors={validation?.errors ?? []}
            names={validation?.names ?? []}
            onField={(node, field, value) =>
              history.commit(node === SCOPE ? setHead(doc, field, value) : setField(doc, node, field, value), `${node}.${field}`)}
            onRename={(from, to) => {
              history.commit(renameNode(doc, catalog, from, to));
              setSelected(to);
            }}
            onNote={(node, note) => history.commit(setNote(doc, node, note), `${node}.note`)}
            onDelete={(node) => {
              history.commit(removeNodes(doc, catalog, [node]));
              setSelected(null);
            }}
          />
        )}
      </div>
      {open && <Problems validation={validation} preview={preview} generate={generate} tab={tab} onTab={setTab} onSelect={setSelected} />}
    </div>
  );
}
