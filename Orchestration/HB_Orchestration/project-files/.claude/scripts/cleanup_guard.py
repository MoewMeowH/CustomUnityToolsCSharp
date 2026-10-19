"""PreToolUse 훅 H1 (Bash · PowerShell): 재귀 삭제는 따로, 절대 경로로, 셸 현재 폴더 밖에서
(CLAUDE.md "명령 실행", 계획서 2026-10-19_훅-규칙-강제, /cross-review 10bfc8de41 반영).

- 재귀 삭제: rm(경로가 붙은 /usr/bin/rm · \\rm 포함) -r/-R/-rf/-fr/--recursive, Remove-Item(rm·del·erase·ri·rd·rmdir 별칭) -Recurse,
  rmdir /s · rd /s. `git rm -r`은 작업 트리 파일 삭제가 아니므로 대상이 아니다.
- 막는 경우: 다른 명령과 묶임(&&, ||, ;, |, 줄바꿈) · cd류와 함께 · 명령 치환($( ) · 백틱) 안 ·
  대상이 절대 경로가 아님(변수·~·드라이브 상대 \\x 포함) · 대상이 루트·드라이브 루트 ·
  **셸의 현재 폴더가 대상과 같거나 그 안** (Claude Code가 "Dangerous rm operation" 승인 창을 띄우는 실제 조건, 2026-10-19 사고).
- 따옴표 안·heredoc·here-string 본문의 글자는 명령으로 보지 않는다. PowerShell은 PowerShell 따옴표 규칙(백틱 이스케이프)으로 읽는다.
- 실패하면 막는다: 통과 판정일 때만 exit 0. python이 없거나 스크립트를 못 찾으면 훅 명령의 `|| exit 2`가 막는다.

입력: stdin JSON {"tool_name": "Bash"|"PowerShell", "tool_input": {"command": "..."}, "cwd": "..."}.
"""
import os
import re
import sys

sys.dont_write_bytecode = True  # 훅 폴더에 __pycache__를 남기지 않는다 (저장소에 섞이지 않게)
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # -I는 스크립트 폴더를 경로에 넣지 않는다
from hook_common import BASH, CD, CHAIN, has_substitution, native_path, read_input, same_or_inside, scan, shell_of, strip_bodies, tokenize  # noqa: E402

RULE = 'CLAUDE.md "명령 실행": 폴더 삭제는 다른 명령에 이어 붙이지 말고, 별도 호출로 셸의 현재 폴더 밖에서 절대 경로로 실행한다.'


def deny(reason: str) -> None:
    print(f'[정리 명령 차단] {reason} {RULE}', file=sys.stderr)
    sys.exit(2)


# 거친 탐지 (따옴표를 비운 텍스트에서). 정확한 판정은 토큰으로 한다.
ROUGH = re.compile(
    r'(?:^|[\s;&|(/\\`])rm\s+(?:[^\n;&|]*\s)?(?:-[A-Za-z]*[rR][A-Za-z]*|--recursive)(?=\s|$)'
    r'|(?:^|[\s;&|(])(?:Remove-Item|rm|del|erase|ri|rd|rmdir)\b[^\n;&|]*\s-r(?:e(?:c(?:u(?:r(?:s(?:e)?)?)?)?)?)?\b'
    r'|(?:^|[\s;&|(])(?:rmdir|rd)\s+/s\b',
    re.IGNORECASE)

PS_DELETE = {'remove-item', 'rm', 'del', 'erase', 'ri', 'rd', 'rmdir'}
# 값을 받는 PowerShell 매개변수: 경로인 것과 건너뛸 것
PS_PATH_PARAMS = {'-path', '-literalpath', '-lp', '-pspath'}
PS_VALUE_PARAMS = {'-filter', '-include', '-exclude', '-erroraction', '-ea', '-warningaction', '-wa', '-informationaction', '-infa',
                   '-errorvariable', '-ev', '-warningvariable', '-wv', '-outvariable', '-ov', '-outbuffer', '-ob', '-credential', '-stream',
                   '-pipelinevariable', '-pv', '-progressaction', '-proga'}


def command_name(tok: str) -> str:
    name = tok.replace('\\', '/').rsplit('/', 1)[-1].lower()
    return name[:-4] if name.endswith('.exe') else name


def delete_targets(toks: list[str], shell: str) -> list[str] | None:
    """재귀 삭제 명령이면 대상 경로 목록, 아니면 None."""
    if not toks:
        return None
    name = command_name(toks[0])
    args = [t for t in toks[1:] if not re.match(r'^\d?>>?|^\d?>&|^&?>', t)]  # 리다이렉션 빼기
    if '#' in args:
        args = args[:args.index('#')]

    if shell == BASH and name == 'rm':
        recursive, paths, only_paths = False, [], False
        for a in args:
            if only_paths:
                paths.append(a)
            elif a == '--':
                only_paths = True
            elif a == '--recursive' or (re.match(r'^-[A-Za-z]+$', a) and re.search('[rR]', a)):
                recursive = True
            elif a.startswith('-'):
                continue
            else:
                paths.append(a)
        return paths if recursive else None

    if name in ('rmdir', 'rd') and any(a.lower() == '/s' for a in args):  # cmd 형식
        return [a for a in args if not a.startswith('/')]

    if name in PS_DELETE:
        recursive, paths, skip, take = False, [], False, False
        for a in args:
            low = a.lower()
            if skip:
                skip = False
                continue
            if take:
                take = False
                paths.extend(p for p in a.split(',') if p)
                continue
            if low in PS_PATH_PARAMS:
                take = True
            elif low in PS_VALUE_PARAMS:
                skip = True
            elif re.match(r'^-r(e(c(u(r(s(e)?)?)?)?)?)?$', low):
                recursive = True
            elif low.startswith('-'):
                continue  # 스위치 (-Force, -Confirm:$false, -WhatIf …)
            else:
                paths.extend(p for p in a.split(',') if p)
        return paths if recursive else None
    return None


def path_problem(path: str, shell: str, cwd: str | None) -> str | None:
    if '$' in path or '%' in path or path.startswith('~'):
        return f"대상 '{path}'에 변수·~가 있다 (펼친 절대 경로로)."
    p = path.replace('\\', '/')
    native = native_path(p) if shell == BASH else p
    if p.rstrip('/') == '' or re.match(r'^[A-Za-z]:/*$', native):
        return f"대상 '{path}'이(가) 루트·드라이브 루트다."
    if re.match(r'^[A-Za-z]:/', native) is None and not native.startswith('//'):
        return f"대상 '{path}'이(가) 절대 경로가 아니다" + (' (\\x·/x는 PowerShell에서 현재 드라이브 기준)' if p.startswith('/') else '') + '.'
    if cwd and same_or_inside(native_path(cwd.replace('\\', '/')), native):
        return f"셸의 현재 폴더({cwd})가 대상 '{path}'과(와) 같거나 그 안이다. 먼저 프로젝트 루트 등 바깥 폴더로 옮긴 뒤 따로 실행한다."
    return None


def check(data: dict) -> None:
    tool = data.get('tool_name', '')
    if tool not in ('Bash', 'PowerShell'):
        return
    shell = shell_of(tool)
    cmd = strip_bodies((data.get('tool_input') or {}).get('command') or '')
    stripped, inner = scan(cmd, shell)
    if not ROUGH.search(stripped + '\n' + '\n'.join(inner)):
        return
    if has_substitution(stripped, inner, shell):
        deny('재귀 삭제가 명령 치환($( ) · 백틱) 안이나 함께 있다.')
    if CHAIN.search(stripped.strip()):
        deny('재귀 삭제가 다른 명령과 묶여 있다 (&&·||·;·|·줄바꿈).')
    if CD.search(stripped):
        deny('재귀 삭제와 cd가 함께 있다.')
    targets = delete_targets(tokenize(cmd.strip(), shell), shell)
    if targets is None:
        return  # git rm -r 등: 거친 탐지에는 걸렸지만 작업 트리 재귀 삭제가 아니다
    if not targets:
        deny('삭제 대상 경로를 찾지 못했다.')
    for t in targets:
        if (problem := path_problem(t, shell, data.get('cwd'))) is not None:
            deny(problem)


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
