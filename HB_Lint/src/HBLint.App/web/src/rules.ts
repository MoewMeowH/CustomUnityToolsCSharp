import type { RuleSummary } from './api';

/** 목록에 보이는 이름 (.hblint.json 뺌). */
export const ruleName = (file: string) => file.replace(/\.hblint\.json$/, '');

/**
 * 규칙 고르기 검색 — 빈칸으로 나눈 낱말이 모두 id · 파일 이름 · 제목 어딘가에 있으면 (대소문자 무시).
 * 서버가 준 순서(id 숫자 순)는 그대로 둔다.
 */
export function filterRules(rules: RuleSummary[], query: string): RuleSummary[] {
  const words = query.toLowerCase().split(/\s+/).filter((w) => w.length > 0);
  if (words.length === 0) return rules;
  return rules.filter((r) => {
    const text = `${r.id ?? ''} ${r.file} ${r.title ?? ''}`.toLowerCase();
    return words.every((w) => text.includes(w));
  });
}

export type ScopeSource = 'common' | 'rule';

export interface ScopeEntry {
  pattern: string;
  source: ScopeSource;
}

/** 규칙 하나에서 빠지는 것 · 다시 넣는 것 — 공통 설정과 규칙 머리를 합친 표시용 (분석기 판정: 제외에 맞고 다시 넣기에 안 맞으면 빠짐). */
export interface ScopeSummary {
  ignoreAssemblies: ScopeEntry[];
  ignorePaths: ScopeEntry[];
  includeAssemblies: string[];
  includePaths: string[];
}

const strings = (value: unknown): string[] =>
  Array.isArray(value) ? value.filter((v): v is string => typeof v === 'string' && v.trim().length > 0) : [];

/** 생성물과 같은 순서 (공통 먼저, 같은 글자는 한 번 — 공통이 이김). */
function merge(common: string[], own: string[]): ScopeEntry[] {
  const seen = new Set<string>();
  const result: ScopeEntry[] = [];
  for (const [list, source] of [[common, 'common'], [own, 'rule']] as const) {
    for (const pattern of list) {
      if (seen.has(pattern)) continue;
      seen.add(pattern);
      result.push({ pattern, source });
    }
  }
  return result;
}

export function scopeSummary(
  settings: { ignoreAssemblies: string[]; ignorePaths: string[] } | null,
  head: Record<string, unknown>,
): ScopeSummary {
  return {
    ignoreAssemblies: merge(settings?.ignoreAssemblies ?? [], strings(head.ignoreAssemblies)),
    ignorePaths: merge(settings?.ignorePaths ?? [], strings(head.ignorePaths)),
    includeAssemblies: strings(head.includeAssemblies),
    includePaths: strings(head.includePaths),
  };
}

/** 사이드바 넓이 범위 (px). 기억한 값이 창보다 크거나 이상하면 이 안으로. */
export const SIDEBAR_MIN = 180;
export const SIDEBAR_MAX = 480;
export const SIDEBAR_DEFAULT = 230;

export function clampSidebar(width: unknown): number {
  const n = typeof width === 'number' ? width : Number.parseInt(String(width), 10);
  if (!Number.isFinite(n)) return SIDEBAR_DEFAULT;
  return Math.min(SIDEBAR_MAX, Math.max(SIDEBAR_MIN, Math.round(n)));
}
