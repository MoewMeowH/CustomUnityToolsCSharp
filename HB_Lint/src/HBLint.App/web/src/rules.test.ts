import { describe, expect, it } from 'vitest';
import type { RuleSummary } from './api';
import { clampSidebar, filterRules, ruleName, SIDEBAR_DEFAULT, SIDEBAR_MAX, SIDEBAR_MIN } from './rules';

const rule = (id: string | null, file: string, title: string | null = null): RuleSummary =>
  ({ id, file, title, errors: 0, warnings: 0, editable: true });

const rules = [
  rule('CLL0002', 'no-getcomponent-in-update.hblint.json', 'Update 계열에서 GetComponent 금지'),
  rule('CLL0010', 'tag-compare.hblint.json', 'tag 비교 금지'),
  rule('CLL0015', 'enum-prefix.hblint.json', 'enum 이름은 E + 대문자'),
  rule(null, 'broken.hblint.json'),
];

describe('filterRules', () => {
  it('빈 검색은 전부를 순서 그대로', () => {
    expect(filterRules(rules, '  ')).toEqual(rules);
  });

  it('id · 파일 이름 · 제목에서 대소문자 무시', () => {
    expect(filterRules(rules, 'cll0010').map((r) => r.id)).toEqual(['CLL0010']);
    expect(filterRules(rules, 'ENUM').map((r) => r.id)).toEqual(['CLL0015']);
    expect(filterRules(rules, 'getcomponent').map((r) => r.id)).toEqual(['CLL0002']);
    expect(filterRules(rules, 'broken').map((r) => r.file)).toEqual(['broken.hblint.json']);
  });

  it('낱말이 여럿이면 모두 있어야', () => {
    expect(filterRules(rules, 'enum 대문자').map((r) => r.id)).toEqual(['CLL0015']);
    expect(filterRules(rules, 'enum tag')).toEqual([]);
  });
});

describe('ruleName · clampSidebar', () => {
  it('확장자를 뗀다', () => {
    expect(ruleName('enum-prefix.hblint.json')).toBe('enum-prefix');
  });

  it('넓이는 범위 안으로, 이상한 값은 기본값', () => {
    expect(clampSidebar(50)).toBe(SIDEBAR_MIN);
    expect(clampSidebar(9999)).toBe(SIDEBAR_MAX);
    expect(clampSidebar('300')).toBe(300);
    expect(clampSidebar(null)).toBe(SIDEBAR_DEFAULT);
    expect(clampSidebar('abc')).toBe(SIDEBAR_DEFAULT);
  });
});
