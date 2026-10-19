"""PreToolUse 훅: 맥락 편향 없이 검토해야 하는 서브에이전트(self-reviewer, evidence-collector)는
<프로젝트>/subagent_in/ 안만 읽게 한다 (계획서 2026-10-19_서브에이전트-도입-검토, 공통 규칙 6).

- 허용 목록 방식: subagent_in/ 밖은 전부 거절 (막을 목록을 관리하지 않는다).
- 경로는 realpath로 바꾼 뒤 비교한다 (.., 링크, 대소문자 우회 차단).
- Grep·Glob은 path 필수. Glob pattern · Grep glob에 '..'·절대 경로가 있으면 거절.
- 실패하면 막는다: 허용 판정일 때만 exit 0, 그 밖(예외 포함)은 exit 2.
  python이 없거나 스크립트를 못 찾는 경우는 훅 명령의 `|| exit 2`가 막는다.

입력: stdin JSON {"tool_name": "...", "tool_input": {...}}. exit 2 + stderr면 Claude Code가 이유를 에이전트에게 돌려준다.
"""
import json
import os
import re
import sys

ALLOWED_DIR = 'subagent_in'


def deny(reason: str) -> None:
    print(f'[읽기 차단] {reason} 이 서브에이전트는 {ALLOWED_DIR}/ 안의 자료만 읽는다.', file=sys.stderr)
    sys.exit(2)


def allowed_root() -> str:
    root = os.environ.get('CLAUDE_PROJECT_DIR') or os.getcwd()
    return os.path.normcase(os.path.realpath(os.path.join(root, ALLOWED_DIR)))


def inside(path: str, base: str) -> bool:
    root = os.environ.get('CLAUDE_PROJECT_DIR') or os.getcwd()
    full = path if os.path.isabs(path) else os.path.join(root, path)
    rp = os.path.normcase(os.path.realpath(full))
    return rp == base or rp.startswith(base + os.sep)


DEVICE_NAMES = {'CON', 'PRN', 'AUX', 'NUL', 'CONIN$', 'CONOUT$'} | {f'{d}{n}' for d in ('COM', 'LPT') for n in '0123456789¹²³'}


def windows_name_problem(path: str) -> str | None:
    """SafePath.WindowsNameProblem과 같은 규칙: 드라이브 뒤의 ':'(대체 데이터 스트림), 끝이 점·공백인 구성 요소, 장치 이름."""
    p = path.replace('\\', '/')
    if p.startswith('//'):
        return r'접두사(\\?\, \\.\)나 UNC 경로는 쓸 수 없다.'
    body = p[2:] if re.match(r'^[A-Za-z]:', p) else p
    if ':' in body:
        return "':'(대체 데이터 스트림)는 쓸 수 없다."
    for part in body.split('/'):
        if not part or part in ('.', '..'):
            continue
        if part.endswith('.') or part.endswith(' '):
            return f"구성 요소 '{part}'가 점이나 공백으로 끝난다."
        if part.split('.')[0].rstrip(' ').upper() in DEVICE_NAMES:
            return f"'{part}'은(는) Windows 장치 이름이다."
    return None


def unsafe_pattern(pattern: str) -> bool:
    p = pattern.replace('\\', '/')
    return '..' in p.split('/') or p.startswith('/') or re.match(r'^[A-Za-z]:', p) is not None or p.startswith('~')


def check(data: dict) -> None:
    tool = data.get('tool_name', '')
    ti = data.get('tool_input') or {}
    base = allowed_root()

    if tool == 'Read':
        path = ti.get('file_path')
        if not path:
            deny('Read에 file_path가 없다.')
        if (problem := windows_name_problem(path)) is not None:
            deny(problem)
        if not inside(path, base):
            deny(f"'{path}'은(는) {ALLOWED_DIR}/ 밖이다.")
    elif tool in ('Grep', 'Glob'):
        path = ti.get('path')
        if not path:
            deny(f'{tool}는 path를 지정해야 한다 (생략하면 프로젝트 전체를 뒤진다).')
        if (problem := windows_name_problem(path)) is not None:
            deny(problem)
        if not inside(path, base):
            deny(f"'{path}'은(는) {ALLOWED_DIR}/ 밖이다.")
        pattern = ti.get('pattern') if tool == 'Glob' else ti.get('glob')
        if pattern and unsafe_pattern(pattern):
            deny(f"패턴 '{pattern}'에 '..' 또는 절대 경로가 있다.")
    else:
        deny(f'이 훅이 모르는 도구 {tool}.')


def main() -> None:
    try:
        sys.stderr.reconfigure(encoding='utf-8')  # Windows 콘솔 기본(cp949)이면 한글 이유가 깨진다
        check(json.loads(sys.stdin.buffer.read().decode('utf-8-sig')))  # cp949 기본 stdin이면 한글이 깨진다
    except SystemExit:
        raise
    except Exception as ex:  # 실패하면 막는다
        deny(f'검사 중 오류 ({type(ex).__name__}: {ex}).')
    sys.exit(0)


if __name__ == '__main__':
    main()
