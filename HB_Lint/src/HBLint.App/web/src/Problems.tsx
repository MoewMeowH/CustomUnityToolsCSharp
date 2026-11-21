import { useState } from 'react';
import type { GenerateResult, PreviewResult, Validation } from './api';
import { SCOPE } from './model';

interface Props {
  validation: Validation | null;
  preview: PreviewResult | null;
  generate: GenerateResult | null;
  tab: Tab;
  onTab: (tab: Tab) => void;
  onSelect: (node: string) => void;
}

export type Tab = 'problems' | 'preview' | 'generate';

/** 아래 칸 — 검증 · 이름 확인 / 미리 보기 / 생성 결과. */
export function Problems(props: Props) {
  const { validation, preview, generate } = props;
  const [collapsed, setCollapsed] = useState(false);
  const errors = validation?.errors ?? [];
  const names = validation?.names ?? [];
  const count = errors.filter((e) => !e.warning).length;
  const warnings = errors.length - count + names.length;

  return (
    <section className={`problems ${collapsed ? 'collapsed' : ''}`}>
      <nav>
        <button className={props.tab === 'problems' ? 'on' : ''} onClick={() => props.onTab('problems')}>
          문제 {count > 0 && <span className="badge error">{count}</span>} {warnings > 0 && <span className="badge warning">{warnings}</span>}
        </button>
        <button className={props.tab === 'preview' ? 'on' : ''} onClick={() => props.onTab('preview')}>
          미리 보기 {preview && <span className="badge">{preview.total}</span>}
        </button>
        <button className={props.tab === 'generate' ? 'on' : ''} onClick={() => props.onTab('generate')}>
          생성 {generate && <span className={`badge ${generate.ok ? '' : 'error'}`}>{generate.ok ? '됨' : generate.errors.length}</span>}
        </button>
        <span className="spacer" />
        <button className="small" onClick={() => setCollapsed((c) => !c)}>{collapsed ? '▲' : '▼'}</button>
      </nav>
      {!collapsed && (
        <div className="problems-body">
          {props.tab === 'problems' && (
            errors.length === 0 && names.length === 0 ? (
              <p className="muted">{validation ? (validation.ok ? '오류 없음' : '') : '검사 중…'}{validation && validation.names === null ? ' · 대상 프로젝트를 읽으면 타입 · 멤버 이름도 확인합니다' : ''}</p>
            ) : (
              <ul>
                {errors.map((e, i) => (
                  <li key={'e' + i} className={e.warning ? 'warning' : 'error'} onClick={() => props.onSelect(e.node ?? SCOPE)}>
                    <b>{e.node ?? '규칙'}</b> {e.message}
                  </li>
                ))}
                {names.map((n, i) => (
                  <li key={'n' + i} className="warning" onClick={() => props.onSelect(n.node)}>
                    <b>{n.node === SCOPE ? '규칙' : n.node}</b> {n.message}
                  </li>
                ))}
              </ul>
            )
          )}
          {props.tab === 'preview' && (
            !preview ? (
              <p className="muted">미리 보기를 누르면 대상 프로젝트 소스에 이 규칙을 돌립니다 (저장하지 않은 것도).</p>
            ) : (
              <>
                <p className="muted">
                  {preview.total}곳 · {preview.elapsedMs}ms{preview.total > preview.hits.length ? ` · 앞 ${preview.hits.length}곳만` : ''}
                  {preview.compileErrors > 0 ? ` · 대상 컴파일 오류 ${preview.compileErrors}개 (빠진 곳이 있을 수 있음)` : ''}
                </p>
                <ul>
                  {preview.hits.map((h, i) => (
                    <li key={i} className={h.severity === 'error' ? 'error' : h.id.endsWith('9001') || h.id.endsWith('9002') ? 'warning' : 'hit'}>
                      <b>{h.file ? `${h.file}(${h.line},${h.column})` : h.id}</b> {h.message}
                    </li>
                  ))}
                </ul>
              </>
            )
          )}
          {props.tab === 'generate' && (
            !generate ? (
              <p className="muted">생성을 누르면 폴더의 저장된 규칙 전부로 LintRules.g.cs를 만듭니다 (CLI generate와 같음).</p>
            ) : (
              <>
                <p className="muted">{generate.ok ? `규칙 ${generate.count}개 → ${generate.output}` : `오류 ${generate.errors.length}개 — ${generate.output}는 바꾸지 않았습니다`}</p>
                <ul>
                  {generate.errors.map((e, i) => <li key={'e' + i} className="error">{e}</li>)}
                  {generate.warnings.map((w, i) => <li key={'w' + i} className="warning">{w}</li>)}
                </ul>
              </>
            )
          )}
        </div>
      )}
    </section>
  );
}
