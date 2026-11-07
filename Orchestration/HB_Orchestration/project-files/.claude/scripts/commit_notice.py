"""PostToolUse 알림 (Bash · PowerShell의 git commit): 방금 커밋이 project-files 원본을 바꿨으면 동기화 대상 비교를 알린다.

- project-files 원본(공통 CLAUDE.md · .claude 명령 · 스크립트 · 훅)을 고친 뒤 비교를 빼먹으면 대상 프로젝트에서 옛 훅이 그대로 돈다
  (Sync -Apply는 사본이 다르면 멈추기만 하고 알려 주지 않는다).
- 명령에 git과 commit이 없으면 바로 끝낸다. 있으면 셸 현재 폴더(또는 git -C 경로)의 HEAD를 본다:
  커밋 시각이 지금부터 2분 전 안(미래 시각은 아님)이고, 바뀐 파일 중에 <폴더>/project-files/ 아래가 있으며 같은 <폴더>/tools/에
  Sync-ProjectFiles.ps1과 이 PC의 대상 목록 sync-targets.local.txt(git 밖)가 있으면 additionalContext로 비교 명령을 알린다
  (파일 이름은 싣지 않고 개수만). 그 밖은 아무것도 내지 않는다.
- 막는 훅이 아니다: 실패하면 조용히 끝낸다 (exit 0). 출력은 UTF-8 (기본 코드 페이지면 한글이 깨져 전달된다 — 실측 2026-11-07).

입력: stdin JSON {"tool_name": "Bash"|"PowerShell", "tool_input": {"command": "..."}, "cwd": "..."}.
"""
import json
import os
import re
import subprocess
import sys
import time

sys.dont_write_bytecode = True  # 훅 폴더에 __pycache__를 남기지 않는다 (저장소에 섞이지 않게)
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # -I는 스크립트 폴더를 경로에 넣지 않는다
from hook_common import native_path, read_input  # noqa: E402

RECENT_SECONDS = 120
GIT_C = re.compile(r'''\bgit(?:\.exe)?\s+-C\s+("[^"]+"|'[^']+'|\S+)''', re.IGNORECASE)


def git(repo: str, *args: str) -> str:
    r = subprocess.run(['git', '-C', repo, *args], capture_output=True, timeout=20)
    if r.returncode != 0:
        raise RuntimeError(r.stderr.decode('utf-8', 'replace'))
    return r.stdout.decode('utf-8', 'replace')


def notice(data: dict) -> str | None:
    if data.get('tool_name') not in ('Bash', 'PowerShell'):
        return None
    cmd = (data.get('tool_input') or {}).get('command') or ''
    if not re.search(r'\bgit\b', cmd, re.IGNORECASE) or 'commit' not in cmd:
        return None
    m = GIT_C.search(cmd)
    repo = m.group(1).strip('"\'') if m else data.get('cwd') or os.getcwd()
    if data.get('tool_name') == 'Bash':
        repo = native_path(repo.replace('\\', '/'))
    top = git(repo, 'rev-parse', '--show-toplevel').strip()
    head, when, *files = git(top, '-c', 'core.quotepath=off', 'log', '-1', '--format=%H%n%ct', '--name-only').splitlines()
    age = time.time() - int(when)
    if not 0 <= age <= RECENT_SECONDS:  # 미래 시각의 HEAD(받은 저장소 등)는 '방금 커밋'이 아니다
        return None
    changed: dict[str, int] = {}
    for f in filter(None, files):
        i = ('/' + f).find('/project-files/')
        if i < 0:
            continue
        parent = f[:i]  # project-files 바로 위 폴더 (저장소 기준, 루트면 '')
        tools = os.path.join(top, parent, 'tools')
        # 이 PC의 동기화 대상 목록(git 밖 개인 파일)이 있는 원본 저장소만 — 받은 저장소의 같은 모양 폴더에는 알리지 않는다 (-All은 이 파일이 있어야 돈다)
        if os.path.isfile(os.path.join(tools, 'Sync-ProjectFiles.ps1')) and os.path.isfile(os.path.join(tools, 'sync-targets.local.txt')):
            changed[parent] = changed.get(parent, 0) + 1
    if not changed:
        return None
    parent, count = next(iter(changed.items()))
    sync = os.path.normpath(os.path.join(top, parent, 'tools', 'Sync-ProjectFiles.ps1')).replace('\\', '/')
    # 파일 이름은 싣지 않는다 (커밋이 고를 수 있는 글이 지휘자 컨텍스트로 들어가지 않게). 경로는 따옴표 · 슬래시 (Bash · PowerShell 둘 다).
    # 경로에 따옴표 · 셸 제어 글자가 있으면 명령을 만들어 주지 않는다 (작은따옴표가 인자 경계를 닫아 다른 명령이 될 수 있다)
    if not re.fullmatch(r"[\w .:/()-]+", sync):
        return (f'[커밋 알림] 커밋 {head[:7]}이(가) project-files 원본 파일 {count}개를 바꿨다. 이 저장소 경로에 셸 제어 글자가 있어 명령은 적지 않는다 — '
                "PROJECT.md 규칙대로 Sync -All 비교를 사용자에게 알리고 사용자가 실행한다.")
    return (f'[커밋 알림] 커밋 {head[:7]}이(가) project-files 원본 파일 {count}개를 바꿨다. '
            f"모든 동기화 대상을 비교만 한다: `powershell -ExecutionPolicy Bypass -File '{sync}' -All` — "
            "요약의 '맞출 것'과 '참고'(settings.json 다름 · 프로젝트 지침 · 대상에만 있는 파일)를 사용자에게 보고하고, "
            '맞추기(-Apply, 다른 파일이 있으면 -Force)는 사용자 확인 뒤 대상마다 따로 한다.')


def main() -> None:
    try:
        sys.stdout.reconfigure(encoding='utf-8')
        text = notice(read_input())
    except Exception:  # 알림일 뿐이다 — 실패해도 도구 결과를 막지 않는다
        sys.exit(0)
    if text:
        print(json.dumps({'hookSpecificOutput': {'hookEventName': 'PostToolUse', 'additionalContext': text}}, ensure_ascii=False))
    sys.exit(0)


if __name__ == '__main__':
    main()
