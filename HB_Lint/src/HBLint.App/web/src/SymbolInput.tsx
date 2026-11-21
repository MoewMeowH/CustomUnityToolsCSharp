import { useEffect, useRef, useState } from 'react';
import { api } from './api';
import type { FieldHint } from './catalog';

interface Suggestion {
  value: string;
  label: string;
  detail: string;
}

/** 멤버 칸: 마지막 '.' 앞이 찾아지는 타입이면 그 타입의 멤버를, 아니면 타입을 고른다 (고르면 '.'을 붙여 멤버로 넘어감). */
async function suggest(hint: FieldHint, text: string): Promise<Suggestion[]> {
  if (hint === 'namespace') return (await api.namespaces(text)).map((n) => ({ value: n, label: n, detail: '' }));
  if (hint === 'member') {
    const dot = text.lastIndexOf('.');
    if (dot > 0) {
      const type = text.slice(0, dot);
      try {
        const members = await api.members(type, text.slice(dot + 1));
        return [
          { value: `${type}.*`, label: '*', detail: '선언된 메서드 · 속성 · 필드 전부' },
          ...members.map((m) => ({ value: `${type}.${m.name}`, label: m.name, detail: m.kind + (m.overloads > 1 ? ` ×${m.overloads}` : '') })),
        ];
      } catch {
        // 타입이 아직 아니면 타입 찾기
      }
    }
    return (await api.types(text)).map((t) => ({ value: t.metadataName + '.', label: t.metadataName, detail: `${t.kind} · ${t.assembly}` }));
  }
  return (await api.types(text)).map((t) => ({ value: t.metadataName, label: t.metadataName, detail: `${t.kind} · ${t.assembly}` }));
}

/** 대상 프로젝트 사이드카 자동 완성이 붙은 입력칸. 값은 고르거나 칸을 떠날 때 넘긴다. */
export function SymbolInput(props: { value: string; hint: FieldHint; enabled: boolean; problem?: string; onCommit: (value: string) => void }) {
  const { value, hint, enabled, problem, onCommit } = props;
  const [text, setText] = useState(value);
  const [items, setItems] = useState<Suggestion[]>([]);
  /** items를 얻은 질의 — 지금 글과 다르면 낡은 제안이라 Enter로 고르지 않는다. */
  const [itemsFor, setItemsFor] = useState('');
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(0);
  const request = useRef(0);

  useEffect(() => setText(value), [value]);

  useEffect(() => {
    if (!open || !enabled || text.trim().length === 0) {
      setItems([]);
      return;
    }
    const id = ++request.current;
    const query = text.trim();
    const timer = setTimeout(() => {
      suggest(hint, query)
        .then((found) => {
          if (id === request.current) {
            setItems(found);
            setItemsFor(query);
            setActive(0);
          }
        })
        .catch(() => setItems([]));
    }, 150);
    return () => clearTimeout(timer);
  }, [text, open, enabled, hint]);

  const pick = (item: Suggestion) => {
    setText(item.value);
    if (item.value.endsWith('.')) return; // 타입을 고름 → 멤버 고르기로 이어짐
    setOpen(false);
    onCommit(item.value);
  };

  return (
    <div className="symbol-input">
      <input
        className={problem ? 'has-problem' : ''}
        value={text}
        title={problem}
        placeholder={enabled ? '입력하면 자동 완성' : '대상 프로젝트를 읽으면 자동 완성'}
        onChange={(e) => {
          setText(e.target.value);
          setOpen(true);
        }}
        onFocus={() => setOpen(true)}
        onBlur={() => {
          setTimeout(() => setOpen(false), 150);
          if (text !== value) onCommit(text);
        }}
        onKeyDown={(e) => {
          if (e.key === 'ArrowDown') setActive((a) => Math.min(a + 1, items.length - 1));
          else if (e.key === 'ArrowUp') setActive((a) => Math.max(a - 1, 0));
          else if (e.key === 'Enter') {
            // 지금 글로 얻은 제안이고, 친 글이 이미 후보 하나와 똑같지 않을 때만 강조된 후보를 고른다
            const fresh = itemsFor === text.trim();
            const exact = items.some((item) => item.value === text.trim());
            if (open && fresh && !exact && items[active]) pick(items[active]);
            else {
              setOpen(false);
              onCommit(text);
            }
          } else if (e.key === 'Escape') setOpen(false);
          else return;
          e.preventDefault();
        }}
      />
      {open && items.length > 0 && (
        <ul className="suggestions">
          {items.map((item, i) => (
            <li key={item.value + i} className={i === active ? 'active' : ''} onMouseDown={(e) => { e.preventDefault(); pick(item); }}>
              <span>{item.label}</span>
              <small>{item.detail}</small>
            </li>
          ))}
        </ul>
      )}
      {problem && <div className="field-problem">{problem}</div>}
    </div>
  );
}
