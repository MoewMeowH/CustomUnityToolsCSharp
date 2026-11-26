import { useCallback, useEffect, useMemo, useRef, useState, type DragEvent, type PointerEvent as ReactPointerEvent } from 'react';
import {
  Background, Controls, MiniMap, ReactFlow, useReactFlow,
  type Connection, type Edge as FlowEdge, type EdgeChange, type NodeChange,
} from '@xyflow/react';
import { api, ApiError, type AppStateDto, type SettingsDto, type GenerateResult, type PreviewResult, type RecentEntry, type RuleSummary, type Validation } from './api';
import { findSpec, groupHelp, paletteGroup, paletteTitle, socketColor, type Catalog } from './catalog';
import { clampSidebar } from './rules';
import { RulePicker } from './RulePicker';
import { SettingsWindow } from './SettingsWindow';
import { Guide } from './Guide';
import { Inspector } from './Inspector';
import {
  addNode, connect, connectProblem, disconnect, docText, edgesOf, moveNode, newRuleDoc, nodesOf, noteOf, outputSocket,
  parseDoc, positions, removeNodes, renameNode, SCOPE, setField, setHead, setNote, summary, type Edge, type Pos, type RuleDoc,
} from './model';
import { OpenPanel } from './OpenPanel';
import { Problems, type Tab } from './Problems';
import { useHistory } from './history';
import { inHost, pick, reportDirty } from './host';
import { StartScreen } from './StartScreen';
import { RuleNode, type RuleFlowNode } from './RuleNode';

const nodeTypes = { rule: RuleNode };
// 서버 RuleFolder와 같은 이름 규칙 (글자 · 숫자 · _ . -)
const ruleFileName = /^[\p{L}\p{N}_.-]+\.hblint\.json$/u;
const edgeId = (e: Edge) => `${e.source}>${e.target}.${e.input}`;
const folderOf = (path: string) => path.replace(/[\\/][^\\/]*$/, '');
const folderName = (path: string) => path.replace(/[\\/]+$/, '').split(/[\\/]/).pop() ?? path;

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
  /** 경로 세 칸(규칙 폴더 · 생성물 · 대상 프로젝트)을 펼쳤는가 — .NET 프로젝트 · 다른 자리용. */
  const [manual, setManual] = useState(false);
  /** React Flow가 잰 노드 크기 — 통제 모드라 돌려 넣어야 미니맵 · 맞춤 보기가 노드를 안다. */
  const [measured, setMeasured] = useState<Map<string, { width: number; height: number }>>(new Map());
  /** 읽는 법 창 — 처음 한 번은 저절로 연다. */
  const [guide, setGuide] = useState(() => localStorage.getItem('hblint.guide.seen') !== '1');
  const closeGuide = useCallback(() => {
    localStorage.setItem('hblint.guide.seen', '1');
    setGuide(false);
  }, []);
  /** 팔레트 넓이 — 오른쪽 경계를 끌어 바꾸고 기억한다. */
  const [sidebarWidth, setSidebarWidth] = useState(() => clampSidebar(localStorage.getItem('hblint.sidebar')));
  const flow = useReactFlow();
  const text = useMemo(() => docText(doc), [doc]);
  const dirty = open !== null && (open.created || text !== open.savedText);

  const fail = (e: unknown) => setStatus({ text: e instanceof Error ? e.message : String(e), error: true });
  const info = (t: string) => setStatus({ text: t, error: false });

  /** 규칙 폴더 공통 설정 (모든 규칙에서 뺄 것) — 규칙 목록과 함께 읽는다. */
  const [settings, setSettings] = useState<SettingsDto | null>(null);
  const [settingsOpen, setSettingsOpen] = useState(false);

  const refreshRules = useCallback(async () => {
    const list = await api.rules();
    setRules(list.rules);
    setNextId(list.nextId);
    // 설정을 못 읽어도 규칙 목록은 보이게 — 단추만 꺼진다
    setSettings(await api.settings().catch(() => null));
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
      } else if (s.defaults.project) {
        // --project만 주면 Unity 프로젝트 열기와 같다
        await openUnity(s.defaults.project);
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

  /** Unity 프로젝트 폴더 하나로 열기 — 규칙 폴더 · 생성물은 서버가 정하고, 처음이면 만들지 묻는다. 그다음 대상 프로젝트도 읽는다. */
  const openUnity = async (path: string, confirmed = false) => {
    if (!confirmed && !confirmLeave()) return;
    try {
      let result = await api.openUnity(path, false);
      if (result.needsSetup) {
        const warning = result.hasPackage
          ? ''
          : '\n\n주의: Packages/manifest.json에 com.hb.lint가 없습니다. 패키지를 먼저 넣지 않으면 규칙 asmdef(CLL.LintRules)가 HBLint.Attributes를 못 찾아 Unity 컴파일이 깨집니다.';
        if (!window.confirm(`이 프로젝트에는 HB_Lint 규칙 폴더가 없습니다.\n${result.rules}에 만들까요?\n(예시 규칙 3개 · 스키마 · 규칙 asmdef CLL.LintRules · 생성물 ${result.out})${warning}`)) return;
        result = await api.openUnity(path, true);
      }
      setState(result.state!);
      history.reset(emptyDoc);
      setOpen(null);
      setValidation(null);
      setGenerate(null);
      setManual(false);
      await refreshRules();
      const made = result.created && result.created.length > 0 ? `규칙 폴더를 만들었습니다 (${result.state!.rules}) · ` : '';
      info(`${made}${result.state!.rules} 열었음`);
      await loadProject(path, null, made);
    } catch (e) {
      fail(e);
    }
  };

  /** 최근 목록 — Unity 열기가 같은 규칙 폴더 · 생성물을 고르는 프로젝트면 Unity 열기, 아니면(직접 고른 경로) 그 경로 그대로. */
  const openRecent = async (entry: RecentEntry) => {
    if (!confirmLeave()) return;
    // 알아보기만 (서버 상태 · 최근 목록을 바꾸지 않는다)
    const unity = entry.project ? await api.probeUnity(entry.project).catch(() => null) : null;
    if (entry.project && unity && !unity.needsSetup && unity.rules === entry.rules && unity.out === entry.out) {
      await openUnity(entry.project, true);
      return;
    }
    // 직접 고른 경로 그대로
    try {
      setState(await api.openWorkspace(entry.rules, entry.out));
      history.reset(emptyDoc);
      setOpen(null);
      setValidation(null);
      setGenerate(null);
      await refreshRules();
      if (entry.project) await loadProject(entry.project, entry.csproj);
    } catch (e) {
      fail(e);
    }
  };

  /** note = 앞 단계 안내(예: 규칙 폴더를 만들었음) — 이 단계 안내 앞에 붙여 덮이지 않게. */
  const loadProject = async (path: string, csproj: string | null, note = '') => {
    setBusy('프로젝트 읽기');
    info(`${note}대상 프로젝트 읽는 중… (처음엔 몇 초)`);
    try {
      const next = await api.loadProject(path, csproj);
      setState(next);
      setCandidates(next.project?.candidates ?? []);
      info(`${note}${next.project ? `${next.project.assemblyName} 읽음 — 타입 ${next.project.types}개` : '대상 프로젝트를 닫았음'}`);
    } catch (e) {
      // 고를 csproj가 여럿이면(Assembly-CSharp 없음 등) 서버가 후보를 같이 준다 — 고르기 칸이 있는 직접 고르기를 펼친다
      const found = e instanceof ApiError ? (e.data as { candidates?: string[] } | undefined)?.candidates : undefined;
      if (found && found.length > 0) {
        setCandidates(found);
        setManual(true);
      }
      setStatus({ text: `${note}${e instanceof Error ? e.message : String(e)}`, error: true });
    } finally {
      setBusy(null);
    }
  };

  // Ctrl+S 저장 · Ctrl+Z 되돌리기 · Ctrl+Y / Ctrl+Shift+Z 다시 (입력칸 안에서는 그 칸의 되돌리기)
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (!(e.ctrlKey || e.metaKey)) return;
      // 공통 설정 · 읽는 법 창이 열려 있으면 뒤 규칙 문서를 건드리지 않는다
      if (document.querySelector('[role="dialog"]')) return;
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

  // 창 제목 · 창(hblint-app-desktop)에 저장 안 한 변경 알리기 — 창은 닫힐 때 beforeunload를 보이지 않아 직접 묻는다
  useEffect(() => {
    document.title = open ? `${open.file}${dirty ? ' ●' : ''} — HB_Lint 규칙 편집` : 'HB_Lint 규칙 편집';
    reportDirty(dirty, open?.file ?? null);
  }, [dirty, open?.file]);

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

  const startResize = (e: ReactPointerEvent) => {
    e.preventDefault();
    const startX = e.clientX;
    const startWidth = sidebarWidth;
    let width = startWidth;
    const move = (ev: PointerEvent) => {
      width = clampSidebar(startWidth + ev.clientX - startX);
      setSidebarWidth(width);
    };
    const up = () => {
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', up);
      document.body.classList.remove('resizing');
      localStorage.setItem('hblint.sidebar', String(width));
    };
    document.body.classList.add('resizing');
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', up);
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

  const statusLine = status && <span className={`status ${status.error ? 'error' : ''}`} onClick={() => setStatus(null)}>{status.text}</span>;

  if (!state.rules && !manual) {
    return (
      <div className="app">
        <StartScreen
          recent={state.recent}
          busy={busy !== null}
          onUnity={(p) => void openUnity(p)}
          onRecent={(r) => void openRecent(r)}
          onForget={(r) => void api.forgetRecent(r.rules).then(setState).catch(fail)}
          onManual={() => setManual(true)}
        />
        <div className="start-status">{statusLine}</div>
      </div>
    );
  }

  /** 다른 Unity 프로젝트 — 창 안이면 폴더 고르기 창, 브라우저면 경로 입력. */
  const chooseUnity = async () => {
    const current = state.project ? folderOf(state.project.csproj) : '';
    const path = inHost() ? await pick('project', current) : window.prompt('Unity 프로젝트 폴더', current);
    if (path) await openUnity(path.trim());
  };

  return (
    <div className="app">
      <header>
        <div className="header-top">
          <strong>HB_Lint 규칙 편집</strong>
          <span className="project-name" title={`규칙 폴더 ${state.rules ?? '-'}\n생성물 ${state.out ?? '-'}`}>
            {state.project ? `${folderName(folderOf(state.project.csproj))} · ${state.project.assemblyName}` : (state.rules ?? '열린 것 없음')}
          </span>
          <button disabled={busy !== null} onClick={() => void chooseUnity()}>Unity 프로젝트 열기…</button>
          <button className="small" onClick={() => setManual((m) => !m)}>{manual ? '직접 고르기 ▴' : '직접 고르기 ▾'}</button>
        </div>
        {manual && <OpenPanel state={state} busy={busy} candidates={candidates} onOpen={openWorkspace} onProject={loadProject} />}
      </header>
      <div className="main">
        <aside className="sidebar" style={{ width: sidebarWidth }}>
          <section className="palette">
            <h4>노드 <small>끌어 놓거나 누르기</small></h4>
            {[...groups].map(([group, specs]) => (
              <div key={group}>
                <h5 style={{ color: group in socketColor ? socketColor[group as keyof typeof socketColor] : undefined }} title={groupHelp(catalog, group)}>
                  {group}
                </h5>
                {specs.map((spec) => (
                  <div
                    key={spec.kind}
                    className={`palette-item ${open ? '' : 'disabled'}`}
                    draggable={!!open}
                    onDragStart={(e) => e.dataTransfer.setData('application/hblint-kind', spec.kind)}
                    onClick={() => place(spec.kind)}
                    title={paletteTitle(spec)}
                  >
                    {spec.title}
                  </div>
                ))}
              </div>
            ))}
          </section>
        </aside>
        <div className="sidebar-resizer" onPointerDown={startResize} onDoubleClick={() => { setSidebarWidth(clampSidebar(null)); localStorage.removeItem('hblint.sidebar'); }} title="끌어서 넓이 조절 (두 번 누르면 기본 넓이)" />
        <main className="canvas" onDragOver={(e) => e.preventDefault()} onDrop={onDrop}>
          <div className="toolbar">
            <RulePicker
              rules={rules}
              current={open?.file ?? null}
              currentId={open ? (typeof doc.id === 'string' ? doc.id : null) : null}
              dirty={dirty}
              created={open?.created ?? false}
              enabled={!!state.rules}
              nextId={nextId}
              onOpen={(file) => void openRule(file)}
              onCreate={createRule}
            />
            <button disabled={!open || !dirty} onClick={() => void save()} title="Ctrl+S">저장</button>
            <button disabled={!history.canUndo} onClick={history.undo} title="Ctrl+Z">되돌리기</button>
            <button disabled={!history.canRedo} onClick={history.redo} title="Ctrl+Y">다시</button>
            <button disabled={!state.rules} onClick={runGenerate}>생성</button>
            <button disabled={!open || !state.project || busy !== null} onClick={runPreview} title={state.project ? '' : '대상 프로젝트를 먼저 읽으세요'}>
              {busy === '미리 보기' ? '돌리는 중…' : '미리 보기'}
            </button>
            {status && <span className={`status ${status.error ? 'error' : ''}`} onClick={() => setStatus(null)}>{status.text}</span>}
            <button
              className="guide-button"
              disabled={!settings}
              onClick={() => setSettingsOpen(true)}
              title="모든 규칙에서 뺄 어셈블리 · 경로 (hblint.settings.json)"
            >
              공통 설정{settings && (settings.ignoreAssemblies.length + settings.ignorePaths.length > 0) ? ` (${settings.ignoreAssemblies.length + settings.ignorePaths.length})` : ''}
              {settings && !settings.ok && <span className="badge error" title="설정 파일 오류">!</span>}
            </button>
            <button className="guide-button-next" onClick={() => setGuide(true)} title="소켓 색 · 용어 설명">? 읽는 법</button>
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
            <div className="placeholder">위 규칙 목록(▾)에서 고르거나 + 새 규칙을 누르세요.</div>
          )}
        </main>
        {open && (
          <Inspector
            catalog={catalog}
            doc={doc}
            selected={selected}
            projectLoaded={!!state.project}
            settings={settings}
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
      {guide && <Guide catalog={catalog} onClose={closeGuide} />}
      {settingsOpen && settings && (
        <SettingsWindow
          settings={settings}
          onClose={() => setSettingsOpen(false)}
          onSaved={(saved) => {
            setSettings(saved);
            setSettingsOpen(false);
            info(`${saved.file} 저장 — 생성을 눌러야 Unity에 반영됩니다`);
            if (open) void api.validate(text).then(setValidation).catch(fail);
          }}
        />
      )}
      {open && <Problems validation={validation} preview={preview} generate={generate} tab={tab} onTab={setTab} onSelect={setSelected} />}
    </div>
  );
}
