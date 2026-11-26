import { useEffect, useState } from 'react';
import type { NameProblem, IrError } from './api';
import type { Catalog, FieldSpec } from './catalog';
import { findSpec } from './catalog';
import { nodeData, noteOf, renameProblem, SCOPE, type Json, type RuleDoc } from './model';
import { SymbolInput } from './SymbolInput';

interface Props {
  catalog: Catalog;
  doc: RuleDoc;
  selected: string | null;
  projectLoaded: boolean;
  errors: IrError[];
  names: NameProblem[];
  onField: (node: string, field: string, value: Json | undefined) => void;
  onRename: (from: string, to: string) => void;
  onNote: (node: string, note: string) => void;
  onDelete: (node: string) => void;
}

/** 글 칸 — 떠나거나 Enter일 때 넘긴다 (글자마다 기록하지 않게). */
function TextInput(props: { value: string; onCommit: (value: string) => void; multiline?: boolean; problem?: string }) {
  const [text, setText] = useState(props.value);
  useEffect(() => setText(props.value), [props.value]);
  const commit = () => {
    if (text !== props.value) props.onCommit(text);
  };
  return props.multiline ? (
    <textarea value={text} onChange={(e) => setText(e.target.value)} onBlur={commit} rows={3} />
  ) : (
    <input
      className={props.problem ? 'has-problem' : ''}
      title={props.problem}
      value={text}
      onChange={(e) => setText(e.target.value)}
      onBlur={commit}
      onKeyDown={(e) => e.key === 'Enter' && commit()}
    />
  );
}

function FieldEditor(props: { field: FieldSpec; value: Json | undefined; projectLoaded: boolean; problems: NameProblem[]; onChange: (value: Json | undefined) => void }) {
  const { field, value, onChange } = props;
  const symbol = field.hint === 'type' || field.hint === 'member' || field.hint === 'namespace' ? field.hint : null;
  const problemOf = (v: string) => props.problems.find((p) => p.field === field.name && p.value === v)?.message;

  const one = (current: string, commit: (v: string) => void) =>
    symbol ? (
      <SymbolInput value={current} hint={symbol} enabled={props.projectLoaded} problem={problemOf(current)} onCommit={commit} />
    ) : (
      <TextInput value={current} onCommit={commit} problem={problemOf(current)} />
    );

  switch (field.type) {
    case 'string':
      return one(typeof value === 'string' ? value : '', (v) => onChange(v.length > 0 || field.required ? v : undefined));
    case 'stringArray': {
      const items = Array.isArray(value) ? value.map(String) : [];
      const set = (next: string[]) => onChange(next.length > 0 ? next : undefined);
      return (
        <div className="array-field">
          {items.map((item, i) => (
            <div className="array-item" key={i}>
              {one(item, (v) => set(v.length > 0 ? items.map((x, j) => (j === i ? v : x)) : items.filter((_, j) => j !== i)))}
              <button className="small" title="빼기" onClick={() => set(items.filter((_, j) => j !== i))}>✕</button>
            </div>
          ))}
          <button className="small" onClick={() => onChange([...items, ''])}>+ 항목</button>
        </div>
      );
    }
    case 'int':
      return (
        <TextInput
          value={typeof value === 'number' ? String(value) : ''}
          onCommit={(v) => {
            const n = Number.parseInt(v, 10);
            onChange(v.trim().length === 0 ? undefined : Number.isNaN(n) ? v : n);
          }}
        />
      );
    case 'bool':
      return (
        <select value={value === true ? 'true' : value === false ? 'false' : ''} onChange={(e) => onChange(e.target.value === '' ? undefined : e.target.value === 'true')}>
          <option value="">기본값</option>
          <option value="true">켜기 (true)</option>
          <option value="false">끄기 (false)</option>
        </select>
      );
    case 'choice':
      return (
        <select value={typeof value === 'string' ? value : ''} onChange={(e) => onChange(e.target.value === '' ? undefined : e.target.value)}>
          <option value="">{field.required ? '— 고르세요 —' : '기본값'}</option>
          {field.choices?.map((c) => <option key={c} value={c}>{c}</option>)}
        </select>
      );
    case 'choiceArray': {
      const chosen = Array.isArray(value) ? value.map(String) : [];
      return (
        <div className="choices">
          {field.choices?.map((c) => (
            <label key={c}>
              <input
                type="checkbox"
                checked={chosen.includes(c)}
                onChange={(e) => {
                  const next = e.target.checked ? field.choices!.filter((x) => x === c || chosen.includes(x)) : chosen.filter((x) => x !== c);
                  onChange(next.length > 0 ? next : undefined);
                }}
              />
              {c}
            </label>
          ))}
          <small>안 고르면 전부</small>
        </div>
      );
    }
  }
}

export function Inspector(props: Props) {
  const { catalog, doc, selected } = props;
  if (!selected) return <aside className="inspector empty">노드를 고르면 칸이 여기 나옵니다. 팔레트에서 끌어 놓거나 눌러 노드를 더합니다.</aside>;

  const isScope = selected === SCOPE;
  const data = isScope ? doc : nodeData(doc, selected);
  const spec = isScope ? null : data && findSpec(catalog, data.kind);
  if (!data) return <aside className="inspector empty">노드가 없습니다</aside>;

  const fields = isScope ? catalog.head : (spec?.fields ?? []);
  const names = props.names.filter((n) => n.node === selected);
  const errors = props.errors.filter((e) => (isScope ? e.node === null : e.node === selected));

  return (
    <aside className="inspector">
      <h3>{isScope ? '규칙 시작 (머리 칸)' : (spec?.title ?? '모르는 노드')}</h3>
      <HelpBox help={isScope ? catalog.scopeHelp?.help : spec?.help} example={isScope ? catalog.scopeHelp?.example : spec?.example} />
      {!isScope && (
        <div className="field">
          <label>노드 이름</label>
          <NameInput doc={doc} name={selected} onRename={props.onRename} />
          <small>{String(data.kind)}</small>
        </div>
      )}
      {fields.map((field) => (
        <div className="field" key={field.name}>
          <label>
            {field.name}
            {field.required && <span className="required"> *</span>}
          </label>
          <FieldEditor
            field={field}
            value={data[field.name]}
            projectLoaded={props.projectLoaded}
            problems={names}
            onChange={(v) => props.onField(selected, field.name, v)}
          />
          <small>{field.description}</small>
          {isScope && field.name === 'severity' && data.severity === 'info' && (
            <div className="notice">info는 Unity 콘솔에 보이지 않습니다 — IDE에서만 보입니다. Unity에서 보려면 warning으로.</div>
          )}
        </div>
      ))}
      {spec?.requireOneOf && <small className="hint">{spec.requireOneOf.join(' · ')} 가운데 하나 이상</small>}
      <div className="field">
        <label>메모 (layout — 분석기에는 넘기지 않음)</label>
        <TextInput multiline value={noteOf(doc, selected)} onCommit={(v) => props.onNote(selected, v)} />
      </div>
      {(errors.length > 0 || names.length > 0) && (
        <ul className="node-problems">
          {errors.map((e, i) => <li key={'e' + i} className={e.warning ? 'warning' : 'error'}>{e.message}</li>)}
          {names.map((n, i) => <li key={'n' + i} className="warning">{n.message}</li>)}
        </ul>
      )}
      {!isScope && <button className="danger" onClick={() => props.onDelete(selected)}>노드 지우기 (Delete)</button>}
    </aside>
  );
}

/** 노드 쉬운 설명 — 접어 두면 노드를 바꿔도 접힌 채로. */
function HelpBox(props: { help?: string; example?: string }) {
  const [open, setOpen] = useState(() => localStorage.getItem('hblint.help') !== 'closed');
  if (!props.help) return null;
  const toggle = () => {
    localStorage.setItem('hblint.help', open ? 'closed' : 'open');
    setOpen(!open);
  };
  return (
    <div className={`help-box ${open ? '' : 'closed'}`}>
      <button className="help-toggle" onClick={toggle}>{open ? '▾ 이 노드는?' : '▸ 이 노드는?'}</button>
      {open && (
        <>
          <p>{props.help}</p>
          {props.example && <p className="help-example"><b>예</b> {props.example}</p>}
        </>
      )}
    </div>
  );
}

function NameInput(props: { doc: RuleDoc; name: string; onRename: (from: string, to: string) => void }) {
  const [text, setText] = useState(props.name);
  useEffect(() => setText(props.name), [props.name]);
  const problem = renameProblem(props.doc, props.name, text);
  const commit = () => {
    if (!problem && text !== props.name) props.onRename(props.name, text);
    else setText(props.name);
  };
  return (
    <>
      <input className={problem ? 'has-problem' : ''} value={text} onChange={(e) => setText(e.target.value)} onBlur={commit} onKeyDown={(e) => e.key === 'Enter' && commit()} />
      {problem && <div className="field-problem">{problem}</div>}
    </>
  );
}
