import { useCallback, useRef, useState } from 'react';

const limit = 200;
const mergeWindowMs = 1000;

/**
 * 되돌리기 기록 (저장과 무관). commit = 기록하고 바꿈, replace = 기록 없이 바꿈(끌기 중).
 * 같은 mergeKey로 1초 안에 잇따라 commit하면 하나로 친다 (글자 하나마다 기록이 쌓이지 않게).
 */
export function useHistory<T>(initial: T) {
  const [present, setPresent] = useState(initial);
  const past = useRef<T[]>([]);
  const future = useRef<T[]>([]);
  const last = useRef<{ key: string; at: number } | null>(null);
  const current = useRef(initial);
  const [, bump] = useState(0);

  const set = (value: T) => {
    current.current = value;
    setPresent(value);
  };

  const commit = useCallback((value: T, mergeKey?: string) => {
    const now = Date.now();
    const merge = mergeKey !== undefined && last.current?.key === mergeKey && now - last.current.at < mergeWindowMs;
    if (!merge) {
      past.current.push(current.current);
      if (past.current.length > limit) past.current.shift();
    }
    future.current = [];
    last.current = mergeKey !== undefined ? { key: mergeKey, at: now } : null;
    set(value);
  }, []);

  const replace = useCallback((value: T) => set(value), []);

  /** 끌기 시작처럼 "지금 상태를 기록만" 할 때. */
  const checkpoint = useCallback(() => {
    past.current.push(current.current);
    if (past.current.length > limit) past.current.shift();
    future.current = [];
    last.current = null;
    bump((n) => n + 1);
  }, []);

  const undo = useCallback(() => {
    const previous = past.current.pop();
    if (previous === undefined) return;
    future.current.push(current.current);
    last.current = null;
    set(previous);
  }, []);

  const redo = useCallback(() => {
    const next = future.current.pop();
    if (next === undefined) return;
    past.current.push(current.current);
    last.current = null;
    set(next);
  }, []);

  /** 다른 규칙을 열 때 — 기록을 비운다. */
  const reset = useCallback((value: T) => {
    past.current = [];
    future.current = [];
    last.current = null;
    set(value);
  }, []);

  return { present, commit, replace, checkpoint, undo, redo, reset, canUndo: past.current.length > 0, canRedo: future.current.length > 0 };
}
