import { useState } from 'react';
import { socketColor, type Catalog, type Socket } from './catalog';

/** 팔레트 맨 위 "읽는 법" — 처음 보는 사람용 소켓 색 · 용어. 접힌 상태는 기억한다. */
export function Guide({ catalog }: { catalog: Catalog }) {
  const [open, setOpen] = useState(() => localStorage.getItem('hblint.guide') !== 'closed');
  const toggle = () => {
    localStorage.setItem('hblint.guide', open ? 'closed' : 'open');
    setOpen(!open);
  };
  const sockets = Object.entries(catalog.socketHelp ?? {}) as [Socket, string][];

  return (
    <div className="guide">
      <button className="help-toggle" onClick={toggle}>{open ? '▾ 처음이면: 읽는 법' : '▸ 처음이면: 읽는 법'}</button>
      {open && (
        <div className="guide-body">
          <div className="guide-sockets">
            {sockets.map(([socket, text]) => (
              <div key={socket} className="guide-socket">
                <span className="dot" style={{ background: socketColor[socket] }} />
                <b>{socket}</b> {text}
              </div>
            ))}
          </div>
          <dl>
            {(catalog.glossary ?? []).map((t) => (
              <div key={t.term}>
                <dt>{t.term}</dt>
                <dd>{t.meaning}</dd>
              </div>
            ))}
          </dl>
          <small>노드를 누르면 오른쪽에 그 노드 설명이 나옵니다.</small>
        </div>
      )}
    </div>
  );
}
