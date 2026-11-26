import { useState } from 'react';
import type { RecentEntry } from './api';
import { inHost, pick } from './host';

interface Props {
  recent: RecentEntry[];
  busy: boolean;
  onUnity: (path: string) => void;
  onRecent: (entry: RecentEntry) => void;
  /** 최근 목록에서 지우기 (폴더 · 파일은 건드리지 않음). */
  onForget: (entry: RecentEntry) => void;
  onManual: () => void;
}

const folderName = (path: string) => path.replace(/[\\/]+$/, '').split(/[\\/]/).pop() ?? path;

/** 아무것도 안 연 첫 화면 — Unity 프로젝트 폴더 하나로 연다 (규칙 폴더 · 생성물 · 대상 프로젝트는 앱이 정함). */
export function StartScreen({ recent, busy, onUnity, onRecent, onForget, onManual }: Props) {
  const [path, setPath] = useState('');

  return (
    <div className="start">
      <h2>HB_Lint 규칙 편집</h2>
      <p className="muted">Unity 프로젝트 폴더를 고르면 규칙 폴더(Assets/HBLint) · 생성물 · 자동 완성을 앱이 맞춥니다. 처음이면 규칙 폴더를 만들지 묻습니다.</p>
      {inHost() ? (
        <button className="primary" disabled={busy} onClick={async () => {
          const picked = await pick('project', '');
          if (picked) onUnity(picked);
        }}>Unity 프로젝트 열기…</button>
      ) : (
        <div className="row">
          <input value={path} onChange={(e) => setPath(e.target.value)} placeholder="Unity 프로젝트 폴더 (예: D:\Game)" onKeyDown={(e) => e.key === 'Enter' && path.trim() && onUnity(path.trim())} />
          <button className="primary" disabled={busy || path.trim() === ''} onClick={() => onUnity(path.trim())}>Unity 프로젝트 열기</button>
        </div>
      )}
      {recent.length > 0 && (
        <>
          <h4>최근</h4>
          <ul className="recent">
            {recent.map((r) => (
              <li key={r.rules}>
                <button className="recent-open" disabled={busy} onClick={() => onRecent(r)} title={`규칙 폴더 ${r.rules}\n생성물 ${r.out}${r.project ? `\n대상 ${r.project}` : ''}`}>
                  <strong>{folderName(r.project ?? r.rules)}</strong>
                  <small>{r.project ?? r.rules}</small>
                </button>
                <button className="recent-forget small" disabled={busy} onClick={() => onForget(r)} title="최근 목록에서 지우기 (폴더 · 파일은 그대로)">✕</button>
              </li>
            ))}
          </ul>
        </>
      )}
      <button className="link" onClick={onManual}>.NET 프로젝트 · 경로 직접 고르기</button>
    </div>
  );
}
