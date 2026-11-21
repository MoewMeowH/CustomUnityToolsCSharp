import { useEffect, useState } from 'react';
import type { AppStateDto } from './api';
import { inHost, pick, type PickField } from './host';

/** 창(hblint-app-desktop) 안에서만 보이는 "찾아보기" — 고르기 창의 경로로 칸을 채운다. */
function Browse(props: { field: PickField; current: string; onPicked: (path: string) => void }) {
  if (!inHost()) return null;
  return (
    <button className="small" title="찾아보기" onClick={async () => {
      const path = await pick(props.field, props.current);
      if (path) props.onPicked(path);
    }}>…</button>
  );
}

interface Props {
  state: AppStateDto;
  busy: string | null;
  /** 고를 수 있는 csproj — 읽기 실패 응답(Assembly-CSharp 없음 등)에서도 온다. */
  candidates: string[];
  onOpen: (rules: string, out: string) => void;
  onProject: (path: string, csproj: string | null) => void;
}

/** 위 칸 — 규칙 폴더 · 생성물 · 대상 프로젝트 경로 (브라우저는 실제 경로를 못 고르므로 붙여 넣기 + 최근 목록, 창 안이면 찾아보기도). */
export function OpenPanel({ state, busy, candidates, onOpen, onProject }: Props) {
  const recent = state.recent[0];
  const [rules, setRules] = useState(state.rules ?? state.defaults.rules ?? recent?.rules ?? '');
  const [out, setOut] = useState(state.out ?? state.defaults.out ?? recent?.out ?? '');
  const [project, setProject] = useState(state.defaults.project ?? recent?.project ?? '');
  const [csproj, setCsproj] = useState<string | null>(recent?.csproj ?? null);

  useEffect(() => {
    if (state.rules) setRules(state.rules);
    if (state.out) setOut(state.out);
  }, [state.rules, state.out]);

  return (
    <div className="open-panel">
      <div className="row">
        <label>규칙 폴더</label>
        <input value={rules} onChange={(e) => setRules(e.target.value)} placeholder="예: D:\Game\Assets\HBLint" list="recent-rules" />
        <datalist id="recent-rules">{state.recent.map((r) => <option key={r.rules} value={r.rules} />)}</datalist>
        <Browse field="rules" current={rules} onPicked={setRules} />
        <label>생성물</label>
        <input value={out} onChange={(e) => setOut(e.target.value)} placeholder="예: D:\Game\Assets\HBLint\Rules\LintRules.g.cs" />
        <Browse field="out" current={out} onPicked={setOut} />
        <button
          disabled={busy !== null || rules.trim() === '' || out.trim() === ''}
          onClick={() => {
            const entry = state.recent.find((r) => r.rules === rules.trim());
            if (entry && out.trim() === '') setOut(entry.out);
            onOpen(rules.trim(), out.trim());
          }}
        >
          열기
        </button>
      </div>
      <div className="row">
        <label>대상 프로젝트</label>
        <input value={project} onChange={(e) => setProject(e.target.value)} placeholder="Unity 프로젝트 폴더 또는 .csproj (자동 완성 · 이름 확인 · 미리 보기)" />
        <Browse field="project" current={project} onPicked={setProject} />
        {candidates.length > 0 && (
          <select value={csproj ?? ''} onChange={(e) => setCsproj(e.target.value || null)} title="읽을 csproj (Unity는 어셈블리마다 하나)">
            <option value="">{candidates.includes('Assembly-CSharp.csproj') ? 'Assembly-CSharp.csproj' : '— csproj 고르기 —'}</option>
            {candidates.filter((c) => c !== 'Assembly-CSharp.csproj').map((c) => <option key={c} value={c}>{c}</option>)}
          </select>
        )}
        <button disabled={busy !== null || !state.rules} onClick={() => onProject(project.trim(), csproj)}>
          {state.project ? '다시 읽기' : '읽기'}
        </button>
        <span className="muted">
          {state.project
            ? `${state.project.assemblyName} (${state.project.kind}) · 소스 ${state.project.sources} · 참조 ${state.project.references} · 타입 ${state.project.types} · ${state.project.loadMs}ms${state.project.missing.length > 0 ? ` · 없는 참조 ${state.project.missing.length}` : ''}`
            : '안 읽음'}
        </span>
      </div>
    </div>
  );
}
