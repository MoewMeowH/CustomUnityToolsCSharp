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


REQUIRED_HOOKS = {  # 계획서 2026-10-19_훅-규칙-강제: matcher → 반드시 걸려 있어야 하는 스크립트
    'Bash|PowerShell': ['cleanup_guard.py', 'push_guard.py'],
    'mcp__hb-orchestration__run_mode': ['run_mode_guard.py'],
}


def check_settings(path: str) -> list:
    """settings.json의 PreToolUse 훅 등록. 실패 시 막는 형식(`|| exit 2`)인지도 본다."""
    import json
    errors = []
    data = json.load(open(path, encoding='utf-8-sig'))
    entries = (data.get('hooks') or {}).get('PreToolUse') or []
    for matcher, scripts in REQUIRED_HOOKS.items():
        commands = [h.get('command', '') for e in entries if e.get('matcher') == matcher for h in e.get('hooks') or []]
        for s in scripts:
            found = [c for c in commands if s in c]
            if not found:
                errors.append(f'훅 없음: {matcher} → {s}')
            elif not all(c.rstrip().endswith('|| exit 2') for c in found):
                errors.append(f'{s} 훅이 실패 시 막지 않는다 (명령 끝에 `|| exit 2`)')
    return errors


def main() -> int:
    sys.stdout.reconfigure(encoding='utf-8')
    here = os.path.dirname(os.path.abspath(__file__))
    dirs = sys.argv[1:] or [os.path.join(here, '..', 'agents')]
    failed = 0
    # 훅 등록: 인자가 없을 때만 (스크립트 옆 settings.json, 없으면 settings.example.json — project-files 쪽)
    if not sys.argv[1:]:
        for name in ('settings.json', 'settings.example.json'):
            path = os.path.join(here, '..', name)
            if os.path.isfile(path):
                errs = check_settings(path)
                print(f'{"[실패]" if errs else "[통과]"} {name} 훅' + ''.join(f'\n  - {e}' for e in errs))
                failed += bool(errs)
                break
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
