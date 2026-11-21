// hblint-app-desktop 창(WebView2) 안에서만 쓰는 길 — 창에 말하기(window.chrome.webview). 브라우저에서는 아무것도 하지 않는다.
// 창은 앱 출처에서 온 말 · 정해진 kind만 받는다 (HostMessages.cs).

export type PickField = 'rules' | 'out' | 'project';

interface WebView {
  postMessage(message: unknown): void;
  addEventListener(type: 'message', listener: (event: { data: unknown }) => void): void;
  removeEventListener(type: 'message', listener: (event: { data: unknown }) => void): void;
}

const webview = (): WebView | undefined => (globalThis as { chrome?: { webview?: WebView } }).chrome?.webview;

/** 창 안인가 (찾아보기 단추를 보일지). */
export const inHost = () => webview() !== undefined;

/** 고르기 창을 띄워 고른 경로 — 취소면 null. 창 밖이면 바로 null. */
export function pick(field: PickField, current: string): Promise<string | null> {
  const host = webview();
  if (!host) return Promise.resolve(null);
  return new Promise((resolve) => {
    const listener = (event: { data: unknown }) => {
      const data = event.data as { kind?: string; field?: string; path?: string | null } | null;
      if (data?.kind !== 'picked' || data.field !== field) return;
      host.removeEventListener('message', listener);
      resolve(typeof data.path === 'string' ? data.path : null);
    };
    host.addEventListener('message', listener);
    host.postMessage({ kind: 'pick', field, current });
  });
}

let last = '';

/** 저장 안 한 변경 — 창이 닫힐 때 묻는다. 바뀔 때만 보낸다. */
export function reportDirty(dirty: boolean, file: string | null) {
  const host = webview();
  const key = `${dirty}|${file ?? ''}`;
  if (!host || key === last) return;
  last = key;
  host.postMessage({ kind: 'dirty', dirty, file });
}
