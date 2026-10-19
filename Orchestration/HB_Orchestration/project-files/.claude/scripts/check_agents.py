"""서브에이전트 정의 파일 검사 (계획서 2026-10-19_서브에이전트-도입-검토, 공통 규칙 8).
tools가 없거나(→ MCP 도구까지 상속) 허용 목록 밖 도구(MCP 포함)가 있으면 실패(exit 1). 경고가 아니다.

사용: python -I .claude/scripts/check_agents.py [agents 폴더 ...]  (기본: 스크립트 기준 ../agents)
"""
import os
import re
import sys

ALLOWED = {'Read', 'Grep', 'Glob', 'WebSearch', 'WebFetch'}
FORBIDDEN_MODES = {'bypassPermissions', 'dontAsk', 'auto', 'acceptEdits'}


def frontmatter(text: str):
    m = re.match(r'^---\r?\n(.*?)\r?\n---', text, re.S)
    if not m:
        return None
    fields = {}
    for line in m.group(1).splitlines():
        km = re.match(r'^([A-Za-z]+):\s*(.*)$', line)
        if km:
            fields[km.group(1)] = km.group(2).strip()
    return fields


def check(path: str) -> list:
    errors = []
    fm = frontmatter(open(path, encoding='utf-8-sig').read())  # BOM이 붙어도 읽는다 (PowerShell Set-Content 등)
    if fm is None:
        return ['frontmatter 없음']
    if 'tools' not in fm or not fm['tools']:
        errors.append('tools 없음 (생략하면 MCP 도구까지 상속한다)')
    else:
        tools = [t.strip() for t in fm['tools'].split(',') if t.strip()]
        bad = [t for t in tools if t not in ALLOWED]
        if bad:
            errors.append(f'허용 목록 밖 도구: {", ".join(bad)} (허용: {", ".join(sorted(ALLOWED))})')
    if fm.get('permissionMode') in FORBIDDEN_MODES:
        errors.append(f'permissionMode {fm["permissionMode"]} 금지 (부모 세션 설정을 따른다)')
    if not fm.get('model'):
        errors.append('model 없음 (편향 없는 자리 opus / 보호·반복 sonnet)')
    return errors


def main() -> int:
    sys.stdout.reconfigure(encoding='utf-8')
    dirs = sys.argv[1:] or [os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'agents')]
    failed = 0
    for d in dirs:
        for name in sorted(os.listdir(d)):
            if not name.endswith('.md'):
                continue
            errs = check(os.path.join(d, name))
            print(f'{"[실패]" if errs else "[통과]"} {name}' + ''.join(f'\n  - {e}' for e in errs))
            failed += bool(errs)
    return 1 if failed else 0


if __name__ == '__main__':
    sys.exit(main())
