import { useEffect, useRef, useState } from 'react';
import type { RuleSummary } from './api';
import { filterRules, ruleName } from './rules';

interface Props {
  rules: RuleSummary[];
  /** 지금 연 파일 (없으면 null). */
  current: string | null;
  /** 지금 연 규칙의 id — 새 규칙(아직 목록에 없음)도 보이게. */
  currentId: string | null;
  dirty: boolean;
  created: boolean;
  /** 규칙 폴더를 열었는가. */
  enabled: boolean;
  nextId: string | null;
  onOpen: (file: string) => void;
  onCreate: () => void;
}

const lockedTitle = '앱에서 열 수 없는 파일 (이름에 글자 · 숫자 · _ . - 밖의 문자, 또는 폴더 밖을 가리키는 링크) — 생성에는 들어갑니다';

/** 도구줄 규칙 고르기 — 단추를 누르면 검색칸 + 목록 (id 숫자 순, 서버 순서). 바깥 클릭 · Esc로 닫힘, ↑↓ · Enter. */
export function RulePicker(props: Props) {
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  const [active, setActive] = useState(0);
  const box = useRef<HTMLDivElement>(null);
  const list = useRef<HTMLUListElement>(null);
  const shown = filterRules(props.rules, query);

  useEffect(() => {
    if (!open) return;
    const close = (e: MouseEvent) => {
      if (box.current && !box.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener('mousedown', close);
    return () => document.removeEventListener('mousedown', close);
  }, [open]);

  // 펼칠 때 지금 규칙에 맞춘다
  const show = () => {
    setQuery('');
    const at = props.rules.findIndex((r) => r.file === props.current);
    setActive(at < 0 ? 0 : at);
    setOpen(true);
  };

  useEffect(() => {
    list.current?.querySelector('li.active')?.scrollIntoView({ block: 'nearest' });
  }, [active, open]);

  const choose = (rule: RuleSummary | undefined) => {
    if (!rule || !rule.editable) return;
    setOpen(false);
    if (rule.file !== props.current) props.onOpen(rule.file);
  };

  const onKey = (e: React.KeyboardEvent) => {
    if (e.key === 'Escape') setOpen(false);
    else if (e.key === 'ArrowDown') { e.preventDefault(); setActive((a) => Math.min(shown.length - 1, a + 1)); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setActive((a) => Math.max(0, a - 1)); }
    else if (e.key === 'Enter') choose(shown[active]);
  };

  const label = props.current
    ? `${props.currentId ?? '?'}  ${ruleName(props.current)}${props.created ? ' (새)' : ''}${props.dirty ? ' ●' : ''}`
    : props.enabled ? '규칙 고르기' : '규칙 폴더를 여세요';
  const errors = props.rules.reduce((sum, r) => sum + r.errors, 0);

  return (
    <div className="rule-picker" ref={box}>
      <button className="rule-picker-button" disabled={!props.enabled} onClick={() => (open ? setOpen(false) : show())} title={props.current ?? ''}>
        <span className="rule-picker-label">{label}</span>
        {errors > 0 && <span className="badge error" title="오류가 있는 규칙 파일의 오류 수">{errors}</span>}
        <span className="caret">▾</span>
      </button>
      <button className="small" disabled={!props.enabled || !props.nextId} onClick={props.onCreate} title={props.nextId ? `다음 id ${props.nextId}` : ''}>+ 새 규칙</button>
      {open && (
        <div className="rule-picker-popup" onKeyDown={onKey}>
          <input
            autoFocus
            placeholder={`검색 — id · 파일 · 제목 (${props.rules.length}개)`}
            value={query}
            onChange={(e) => { setQuery(e.target.value); setActive(0); }}
          />
          <ul ref={list}>
            {shown.map((r, i) => (
              <li
                key={r.file}
                className={`${i === active ? 'active' : ''} ${r.file === props.current ? 'on' : ''} ${r.editable ? '' : 'locked'}`}
                onMouseEnter={() => setActive(i)}
                onClick={() => choose(r)}
                title={r.editable ? (r.title ?? '') : lockedTitle}
              >
                <span className="rule-id">{r.id ?? '?'}</span>
                <span className="rule-file">{ruleName(r.file)}</span>
                {r.errors > 0 && <span className="badge error">{r.errors}</span>}
                {r.title && <small className="rule-title">{r.title}</small>}
              </li>
            ))}
            {shown.length === 0 && <li className="empty">맞는 규칙이 없습니다</li>}
          </ul>
        </div>
      )}
    </div>
  );
}
