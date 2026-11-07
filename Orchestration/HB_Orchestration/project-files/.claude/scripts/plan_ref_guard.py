"""PreToolUse 훅 H8 (Write · Edit · MultiEdit · NotebookEdit): 코드 · git에 올라가는 문서에 계획서 · 작업 로그 경로를 새로 쓰면 막는다
(CLAUDE.md "계획서와 작업 로그": docs/는 git 밖이라 다른 사람 · 리뷰어 사본에서는 볼 수 없다).

- 대상 파일: $CLAUDE_PROJECT_DIR 안이고, 경로에 docs 폴더가 없고, review_tmp/ · subagent_in/ 밖. 메모리 · 계획서 · 작업 로그는 대상이 아니다.
- 막는 글: 날짜가 붙은 실제 경로 docs/plans/YYYY-MM-DD_… · docs/worklog/YYYY-MM-DD_… (역슬래시 포함).
  규칙 글의 'docs/plans/…' · 'docs/plans/YYYY-MM-DD_제목.md' 꼴은 실제 파일이 아니라 받는다.
- Edit · MultiEdit은 old_string에 이미 있던 같은 경로는 받는다 (있던 줄을 고치는 것은 새 참조가 아니다).
- Bash로 쓰는 파일은 보지 않는다 (안전망).
- 실패하면 막는다: 통과 판정일 때만 exit 0.

입력: stdin JSON {"tool_name": "Write"|"Edit"|"MultiEdit"|"NotebookEdit", "tool_input": {...}, "cwd": "..."}.
"""
import os
import re
import sys

sys.dont_write_bytecode = True  # 훅 폴더에 __pycache__를 남기지 않는다 (저장소에 섞이지 않게)
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # -I는 스크립트 폴더를 경로에 넣지 않는다
from hook_common import read_input, same_or_inside  # noqa: E402

REF = re.compile(r'docs[\\/]+(?:plans|worklog)[\\/]+\d{4}-\d{2}-\d{2}_[^\s`\'")\]>|]*', re.IGNORECASE)
SKIP_TOP = {'review_tmp', 'subagent_in'}


def deny(reason: str) -> None:
    print(f'[계획서 참조 차단] {reason}', file=sys.stderr)
    sys.exit(2)


def texts(tool: str, ti: dict) -> tuple[list[str], list[str]]:
    """(새로 들어가는 글, 바뀌기 전 글)."""
    if tool == 'Write':
        return [ti.get('content') or ''], []
    if tool == 'Edit':
        return [ti.get('new_string') or ''], [ti.get('old_string') or '']
    if tool == 'MultiEdit':
        edits = ti.get('edits') or []
        return [e.get('new_string') or '' for e in edits], [e.get('old_string') or '' for e in edits]
    if tool == 'NotebookEdit':
        return [ti.get('new_source') or ''], []
    return [], []


def check(data: dict) -> None:
    tool = data.get('tool_name', '')
    ti = data.get('tool_input') or {}
    path = ti.get('file_path') or ti.get('notebook_path') or ''
    new, old = texts(tool, ti)
    if not path or not new:
        return
    root = os.environ.get('CLAUDE_PROJECT_DIR') or data.get('cwd') or os.getcwd()
    full = path if os.path.isabs(path) else os.path.join(data.get('cwd') or root, path)
    if not same_or_inside(full, root):
        return
    parts = [p.lower() for p in os.path.relpath(os.path.abspath(full), os.path.abspath(root)).replace('\\', '/').split('/')]
    if 'docs' in parts[:-1] or parts[0] in SKIP_TOP:
        return
    before = {m.group(0).replace('\\', '/').lower() for t in old for m in REF.finditer(t)}
    added = [m.group(0) for t in new for m in REF.finditer(t) if m.group(0).replace('\\', '/').lower() not in before]
    if added:
        deny(f"'{path}'에 계획서 · 작업 로그 경로({added[0]})를 쓴다 — docs/는 git 밖이라 다른 사람 · 리뷰어 사본에서는 볼 수 없다. "
             '필요한 결정 · 까닭은 그 자리에 한 줄로 적는다 (CLAUDE.md "계획서와 작업 로그").')


def main() -> None:
    try:
        sys.stderr.reconfigure(encoding='utf-8')
        check(read_input())
    except SystemExit:
        raise
    except Exception as ex:  # 실패하면 막는다
        deny(f'검사 중 오류 ({type(ex).__name__}: {ex}).')
    sys.exit(0)


if __name__ == '__main__':
    main()
