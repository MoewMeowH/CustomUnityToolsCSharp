import { useEffect } from 'react';
import { groupHelp, socketColor, type Catalog, type Socket } from './catalog';

/** 처음 보는 사람용 "읽는 법" — 도구줄 단추로 여는 따로 창 (캔버스 위에 겹침). ✕ · Esc · 바깥 클릭으로 닫힘. */
export function Guide({ catalog, onClose }: { catalog: Catalog; onClose: () => void }) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose();
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);
  const sockets = Object.entries(catalog.socketHelp ?? {}) as [Socket, string][];

  return (
    <div className="guide-backdrop" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className="guide-window" role="dialog" aria-label="읽는 법">
        <div className="guide-head">
          <h3>읽는 법</h3>
          <button className="small" onClick={onClose} title="닫기 (Esc)">✕</button>
        </div>
        <div className="guide-body">
          <h4>선 색 (소켓)</h4>
          {sockets.map(([socket, text]) => (
            <div key={socket} className="guide-socket">
              <span className="dot" style={{ background: socketColor[socket] }} />
              <b>{socket}</b> {text}
            </div>
          ))}
          <div className="guide-socket">
            <span className="dot" style={{ background: socketColor.Sites }} />
            <b>Sites 거르기 · 합치기</b> {groupHelp(catalog, 'Sites 거르기 · 합치기')}
          </div>
          <h4>말</h4>
          <dl>
            {(catalog.glossary ?? []).map((t) => (
              <div key={t.term}>
                <dt>{t.term}</dt>
                <dd>{t.meaning}</dd>
              </div>
            ))}
          </dl>
          <small>노드를 누르면 오른쪽 위 "이 노드는?"에 그 노드 설명이 나옵니다. 팔레트 항목에 마우스를 올려도 한 줄 설명이 보입니다.</small>
        </div>
      </div>
    </div>
  );
}
