import { afterEach, describe, expect, it } from 'vitest';
import { inHost, pick, reportDirty } from './host';

type Listener = (event: { data: unknown }) => void;

/** 가짜 window.chrome.webview — 보낸 말을 모으고, reply로 창의 답을 흉내 낸다. */
function fakeHost(reply?: (message: { kind: string; field?: string }) => unknown) {
  const sent: unknown[] = [];
  const listeners = new Set<Listener>();
  const webview = {
    postMessage(message: { kind: string; field?: string }) {
      sent.push(message);
      const answer = reply?.(message);
      if (answer !== undefined) queueMicrotask(() => listeners.forEach((l) => l({ data: answer })));
    },
    addEventListener: (_: 'message', l: Listener) => listeners.add(l),
    removeEventListener: (_: 'message', l: Listener) => listeners.delete(l),
  };
  (globalThis as { chrome?: unknown }).chrome = { webview };
  return { sent, listeners };
}

afterEach(() => {
  delete (globalThis as { chrome?: unknown }).chrome;
});

describe('창 밖 (브라우저)', () => {
  it('아무것도 하지 않는다', async () => {
    expect(inHost()).toBe(false);
    expect(await pick('rules', 'D:\\Game')).toBeNull();
    expect(() => reportDirty(true, 'a.hblint.json')).not.toThrow();
  });
});

describe('창 안', () => {
  it('찾아보기는 그 칸의 picked 답만 받고 듣기를 거둔다', async () => {
    const host = fakeHost((m) => (m.kind === 'pick' ? { kind: 'picked', field: m.field, path: 'D:\\Rules' } : undefined));
    expect(inHost()).toBe(true);

    const path = await pick('rules', '');

    expect(path).toBe('D:\\Rules');
    expect(host.sent).toEqual([{ kind: 'pick', field: 'rules', current: '' }]);
    expect(host.listeners.size).toBe(0);
  });

  it('취소(null)', async () => {
    fakeHost((m) => (m.kind === 'pick' ? { kind: 'picked', field: m.field, path: null } : undefined));
    expect(await pick('out', 'x.cs')).toBeNull();
  });

  it('다른 칸의 답은 넘기고 그 칸 답을 기다린다', async () => {
    const host = fakeHost();
    const pending = pick('out', '');
    host.listeners.forEach((l) => l({ data: { kind: 'picked', field: 'rules', path: 'D:\\Wrong' } }));
    host.listeners.forEach((l) => l({ data: { kind: 'other' } }));
    expect(host.listeners.size).toBe(1);
    host.listeners.forEach((l) => l({ data: { kind: 'picked', field: 'out', path: 'D:\\x.cs' } }));
    expect(await pending).toBe('D:\\x.cs');
  });

  it('dirty는 바뀔 때만 보낸다', () => {
    const host = fakeHost();
    reportDirty(true, 'a.hblint.json');
    reportDirty(true, 'a.hblint.json');
    reportDirty(false, 'a.hblint.json');
    expect(host.sent).toEqual([
      { kind: 'dirty', dirty: true, file: 'a.hblint.json' },
      { kind: 'dirty', dirty: false, file: 'a.hblint.json' },
    ]);
  });
});
