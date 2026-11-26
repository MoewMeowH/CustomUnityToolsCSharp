import { useEffect, useState } from 'react';
import { api, type SettingsDto } from './api';

interface Props {
  settings: SettingsDto;
  onSaved: (settings: SettingsDto) => void;
  onClose: () => void;
}

/** 목록 칸 — 항목마다 글 칸 · ✕, 아래 + 항목. 빈 항목은 저장 때 서버가 뺀다. */
function ListEditor(props: { values: string[]; placeholder: string; onChange: (values: string[]) => void }) {
  const { values, onChange } = props;
  return (
    <div className="array-field">
      {values.map((value, i) => (
        <div className="array-item" key={i}>
          <input value={value} placeholder={props.placeholder} onChange={(e) => onChange(values.map((v, j) => (j === i ? e.target.value : v)))} />
          <button className="small" title="빼기" onClick={() => onChange(values.filter((_, j) => j !== i))}>✕</button>
        </div>
      ))}
      <button className="small" onClick={() => onChange([...values, ''])}>+ 항목</button>
    </div>
  );
}

/**
 * 규칙 폴더 공통 설정 창 — 모든 규칙에서 뺄 어셈블리 · 경로. 규칙마다 include…로 다시 넣을 수 있다.
 * 분석기는 생성물만 읽으므로 저장한 뒤 "생성"을 해야 Unity에 반영된다.
 */
export function SettingsWindow({ settings, onSaved, onClose }: Props) {
  const [assemblies, setAssemblies] = useState(settings.ignoreAssemblies);
  const [paths, setPaths] = useState(settings.ignorePaths);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const dirty = JSON.stringify([assemblies, paths]) !== JSON.stringify([settings.ignoreAssemblies, settings.ignorePaths]);

  /** 저장 안 한 편집이 있으면 묻고 닫는다. */
  const close = () => {
    if (!dirty || window.confirm('저장하지 않은 공통 설정 변경이 있습니다. 버리고 닫을까요?')) onClose();
  };

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && close();
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  });

  const save = async () => {
    setSaving(true);
    setError(null);
    try {
      onSaved(await api.saveSettings(assemblies, paths));
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="guide-backdrop" onMouseDown={(e) => e.target === e.currentTarget && close()}>
      <div className="guide-window settings-window" role="dialog" aria-label="공통 설정">
        <div className="guide-head">
          <h3>공통 설정 <small className="muted">{settings.file}</small></h3>
          <button className="small" onClick={close} title="닫기 (Esc)">✕</button>
        </div>
        <div className="guide-body">
          <p className="muted">
            여기서 뺀 것은 <b>모든 규칙</b>에서 검사하지 않습니다. 한 규칙에서만 다시 보려면 그 규칙의 시작 노드에서 includeAssemblies · includePaths에 적으세요.
            어셈블리와 경로는 <b>따로</b> 판정합니다 — 경로로도 뺀 코드를 다시 보려면 includePaths도 적어야 합니다.
          </p>
          {!settings.ok && (
            <div className="notice">
              지금 파일에 오류가 있어 목록이 비어 보입니다 — 저장하면 이 창의 내용으로 덮습니다.
              <ul>{settings.errors.map((e, i) => <li key={i}>{e}</li>)}</ul>
            </div>
          )}
          <div className="field">
            <label>ignoreAssemblies — 뺄 어셈블리</label>
            <ListEditor values={assemblies} placeholder="예 Assembly-CSharp-firstpass · DOTween*" onChange={setAssemblies} />
            <small>* 와일드카드. Unity Plugins 폴더 스크립트는 Assembly-CSharp-firstpass, asmdef면 그 이름. 파일을 누르면 Inspector의 Assembly Information에 보입니다.</small>
          </div>
          <div className="field">
            <label>ignorePaths — 뺄 파일 경로</label>
            <ListEditor values={paths} placeholder="예 Plugins/Demigiant/** · ThirdParty/**" onChange={setPaths} />
            <small>코드 자리 노드와 같은 패턴 — 경로 끝부분과 맞춤, Assets · Packages부터 봄, * 폴더 이름 하나, ** 여러 단계.</small>
          </div>
          {error && <div className="field-problem">{error}</div>}
          <div className="settings-actions">
            <small className="muted">저장한 뒤 <b>생성</b>을 해야 Unity에 반영됩니다.</small>
            <button disabled={(!dirty && settings.ok) || saving} onClick={() => void save()}>{saving ? '저장 중…' : '저장'}</button>
          </div>
        </div>
      </div>
    </div>
  );
}
