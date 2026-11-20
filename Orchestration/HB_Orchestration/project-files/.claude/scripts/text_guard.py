"""PreToolUse 훅 H9 (Write · Edit · MultiEdit · NotebookEdit · Bash · PowerShell): 도구 입력 글에 실제 U+2028 · U+2029 · U+FEFF 글자가 있으면 막는다.

- 역슬래시-u 이스케이프를 도구 입력에 쓰면 실제 문자로 바뀌어 들어간다: U+2028이 C# 소스를 CS1010으로 깨고 문서에 숨은 줄바꿈을 남겼고
  (2026-11-18 두 번), '\\ufeff'가 파이썬 소스에 보이지 않는 BOM 글자로 들어갔다 (2026-11-20).
  코드는 (char)0x2028 · chr(0xFEFF)로 만들고, 문서는 "U+2028"처럼 쓴다.
- U+FEFF는 Write · NotebookEdit 글의 맨 앞(파일 BOM)이면 받는다.
- 보는 글: Write content · Edit new_string · MultiEdit edits[].new_string · NotebookEdit new_source · Bash · PowerShell command.
  Edit · MultiEdit은 수정마다 old_string보다 많을 때만 막는다 (이미 있던 문자를 지우거나 그대로 두는 수정은 받는다).
- 이 소스에도 그 문자를 쓰지 않는다 — chr()로 만든다.
- 실패하면 막는다: 통과 판정일 때만 exit 0.

입력: stdin JSON {"tool_name": ..., "tool_input": {...}}.
"""
import os
import sys

sys.dont_write_bytecode = True  # 훅 폴더에 __pycache__를 남기지 않는다 (저장소에 섞이지 않게)
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # -I는 스크립트 폴더를 경로에 넣지 않는다
from hook_common import read_input  # noqa: E402

HIDDEN = (chr(0x2028), chr(0x2029), chr(0xFEFF))
BOM = chr(0xFEFF)


def count(text: str, allow_bom: bool = False) -> int:
    if allow_bom and text.startswith(BOM):
        text = text[1:]
    return sum(text.count(s) for s in HIDDEN)


def deny(reason: str) -> None:
    print(f'[글자 차단] {reason}', file=sys.stderr)
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
    if tool in ('Bash', 'PowerShell'):
        return [ti.get('command') or ''], []
    return [], []


def check(data: dict) -> None:
    tool = data.get('tool_name', '')
    new, old = texts(tool, data.get('tool_input') or {})
    whole = tool in ('Write', 'NotebookEdit')  # 파일 전체 글 — 맨 앞 BOM은 파일 표시
    # 수정마다 비교한다 (한 수정이 지우고 다른 수정이 넣으면 합계로는 같아진다)
    if any(count(n, whole) > count(o) for n, o in zip(new, old + [''] * (len(new) - len(old)))):
        deny(f'{tool} 입력에 실제 U+2028 · U+2029 · U+FEFF 글자가 있다 (역슬래시-u 이스케이프가 실제 글자로 바뀐 것). '
             '코드는 (char)0x2028 · chr(0xFEFF)로 만들고, 문서는 "U+2028"처럼 쓴다. 파일 맨 앞 BOM만 받는다.')


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
