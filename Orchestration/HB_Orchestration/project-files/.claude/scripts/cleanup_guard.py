"""PreToolUse 훅 H1 (Bash · PowerShell): 재귀 삭제는 따로, 절대 경로로, 셸 현재 폴더 밖에서
(CLAUDE.md "명령 실행", 계획서 2026-10-19_훅-규칙-강제, /cross-review 10bfc8de41 반영).

- 재귀 삭제: rm(경로가 붙은 /usr/bin/rm · \\rm 포함) -r/-R/-rf/-fr/--recursive, Remove-Item(rm·del·erase·ri·rd·rmdir 별칭) -Recurse,
  rmdir /s · rd /s. `git rm -r`은 작업 트리 파일 삭제가 아니므로 대상이 아니다.
- 막는 경우: 다른 명령과 묶임(&&, ||, ;, |, 줄바꿈) · cd류와 함께 · 명령 치환($( ) · 백틱) 안 ·
  대상이 절대 경로가 아님(변수·~·드라이브 상대 \\x 포함) · 대상이 루트·드라이브 루트 ·
  **셸의 현재 폴더가 대상과 같거나 그 안** (Claude Code가 "Dangerous rm operation" 승인 창을 띄우는 실제 조건, 2026-10-19 사고).
- 따옴표 안·heredoc·here-string 본문의 글자는 명령으로 보지 않는다. PowerShell은 PowerShell 따옴표 규칙(백틱 이스케이프)으로 읽는다.
  단 본문에 재귀 삭제가 있으면 받는 명령이 데이터 전용(cat · tee · git commit 등, hook_common.data_only)일 때만 통과 — bash <<'EOF' 등.
- GNU rm의 줄인 긴 옵션(--rec · --r), PowerShell `-Recurse:$true`(값이 $false · 0 · false가 아니면), PowerShell이 '-'로 받는
  대시 글자(– — ―)도 재귀로 본다 (실측 2026-10-23, 계획서 2026-10-23_H1-H2-heredoc-본문과-rm-옵션).
- 실패하면 막는다: 통과 판정일 때만 exit 0. python이 없거나 스크립트를 못 찾으면 훅 명령의 `|| exit 2`가 막는다.

입력: stdin JSON {"tool_name": "Bash"|"PowerShell", "tool_input": {"command": "..."}, "cwd": "..."}.
"""
import os
import re
import sys

sys.dont_write_bytecode = True  # 훅 폴더에 __pycache__를 남기지 않는다 (저장소에 섞이지 않게)
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # -I는 스크립트 폴더를 경로에 넣지 않는다
from hook_common import BASH, CD, CHAIN, data_only, has_substitution, join_continuations, lex, native_path, read_input, same_or_inside, scan, shell_of, strip_bodies, tokenize  # noqa: E402

RULE = 'CLAUDE.md "명령 실행": 폴더 삭제는 다른 명령에 이어 붙이지 말고, 별도 호출로 셸의 현재 폴더 밖에서 절대 경로로 실행한다.'


def deny(reason: str) -> None:
    print(f'[정리 명령 차단] {reason} {RULE}', file=sys.stderr)
    sys.exit(2)


# 거친 탐지 (따옴표를 비운 텍스트에서). 정확한 판정은 토큰으로 한다.
ROUGH = re.compile(
    r'(?:^|[\s;&|(/\\`])rm\s+(?:[^\n;&|]*\s)?(?:-[A-Za-z]*[rR][A-Za-z]*|--r[a-z]*)(?=\s|$)'
    r'|(?:^|[\s;&|(])(?:Remove-Item|rm|del|erase|ri|rd|rmdir)\b[^\n;&|]*\s-r(?:e(?:c(?:u(?:r(?:s(?:e)?)?)?)?)?)?\b'
    r'|(?:^|[\s;&|(])(?:rmdir|rd)\s+/s\b'
    # PowerShell splatting (Remove-Item @p — 해시 테이블 안의 Recurse는 볼 수 없다)
    r'|(?:^|[\s;&|(])(?:Remove-Item|rm|del|erase|ri|rd|rmdir)\b[^\n;&|]*\s@[A-Za-z_]',
    re.IGNORECASE)

PS_DELETE = {'remove-item', 'rm', 'del', 'erase', 'ri', 'rd', 'rmdir'}
DELETE_NAMES = PS_DELETE | {'rm', 'rmdir', 'rd'}
# PowerShell은 매개변수 앞의 이 대시 글자도 '-'로 받는다 (–Recurse — 실측). 낱말 첫 글자만 바꾼다 — 경로 안의 대시까지 바꾸면
# 셸 현재 폴더(cwd)와 비교가 어긋난다 (/self-review security 625b223591 C4)
PS_DASH = re.compile(r'(?:(?<=\s)|^)[–—―](?=[A-Za-z])')
PS_RECURSE = re.compile(r'^-r(?:e(?:c(?:u(?:r(?:s(?:e)?)?)?)?)?)?(?::(.*))?$')
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
            elif (a.startswith('--r') and 'recursive'.startswith(a[2:])) or (re.match(r'^-[A-Za-z]+$', a) and re.search('[rR]', a)):
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
            elif m := PS_RECURSE.match(low):
                # -Recurse:$true · -Recurse:$변수도 재귀 (막는 쪽). 끄는 값만 뺀다
                recursive = recursive or m.group(1) not in ('$false', '0', 'false')
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
    raw = join_continuations((data.get('tool_input') or {}).get('command') or '', shell)
    if shell != BASH:
        raw = PS_DASH.sub('-', raw)
    cmd = strip_bodies(raw, shell)
    stripped, inner = scan(cmd, shell)
    # 본문을 셸 · 실행기에 넘기면 본문이 곧 명령이다 — 본문(따옴표 heredoc · @'…'@ · 따옴표 없는 heredoc · @"…"@)에 재귀 삭제가 있으면
    # 데이터 전용 명령만 받는다 (본문은 무슨 언어인지 모르므로 따옴표를 공백으로 바꿔 본다 — python os.system('rm -rf …') 등)
    lx = lex(raw, shell)
    if any(ROUGH.search(re.sub(r'[\'"`]', ' ', body)) for body in lx.bodies) and not data_only(stripped, inner, shell):
        deny('heredoc · here-string 본문에 재귀 삭제가 있는데 받는 명령이 데이터 전용(cat · tee · git commit · Set-Content 등)이 아니다 — '
             '훅이 그 본문을 명령으로 검사하지 못한다. 스크립트는 Write로 파일을 만든 뒤 따로 실행하라.')
    rough = ROUGH.search(stripped + '\n' + '\n'.join(inner))
    if rough and has_substitution(stripped, inner, shell):  # 따옴표 검사보다 먼저 — 이쪽 안내가 더 정확하다
        deny('재귀 삭제가 명령 치환($( ) · 백틱) 안이나 함께 있다.')
    # 따옴표 문자열 속 재귀 삭제 (bash -c "…" · powershell -Command "…" · iex "…" · cmd /c "…"): 글을 실행하지 않는 명령
    # (데이터 전용 + grep · echo · git commit/log 등)만 받는다 — 허용 목록 (계획서 2026-10-23_따옴표-명령-허용목록과-perf-ownReview).
    # 안쪽 따옴표는 공백으로 바꿔 본다 (python -c "os.system('rm -rf …')"). 따옴표 · 백틱을 지운 글 전체도 본다 — 겹친 따옴표로
    # 명령이 조각 여럿에 나뉘거나(powershell -Command "Remove-Item "$p" -Recurse") 이름을 쪼갠 것(r""m) (/self-review security ddb47044d1 C1 · C5)
    joined = not rough and ROUGH.search(lx.unquoted(raw))
    if (joined or any(ROUGH.search(re.sub(r'[\'"`]', ' ', q)) for q in lx.quoted)) and not data_only(stripped, inner, shell, readers=True):
        deny('따옴표 문자열 안에 재귀 삭제가 있는데 받는 명령이 글을 실행하지 않는 명령(grep · echo · git commit 등)이 아니다 — '
             '셸에 명령 문자열을 넘기지 말고 그 셸 도구(Bash · PowerShell)로 직접 실행하라.')
    if not rough:
        return
    if CHAIN.search(stripped.strip()):
        deny('재귀 삭제가 다른 명령과 묶여 있다 (&&·||·;·|·줄바꿈).')
    if CD.search(stripped):
        deny('재귀 삭제와 cd가 함께 있다.')
    toks = tokenize(cmd.strip(), shell)
    if shell != BASH and toks and command_name(toks[0]) in PS_DELETE and any(t.startswith('@') for t in toks[1:]):
        deny('삭제 명령의 splatting(@변수)은 훅이 해석하지 못한다 — 경로 · -Recurse를 직접 적어라.')
    targets = delete_targets(toks, shell)
    if targets is None:
        if toks and command_name(toks[0]) not in DELETE_NAMES and command_name(toks[0]) != 'git':
            # 거친 탐지에 걸렸는데 맨 앞이 삭제 명령이 아니다: cmd /c rmdir /s · env rm · timeout rm · -Command 따옴표 없음 —
            # 실행기를 거친 삭제는 해석하지 않고 막는다 (/self-review security ddb47044d1 C2). git rm -r은 작업 트리 삭제가 아니다.
            deny('재귀 삭제를 해석하지 못했다 — 삭제 명령(rm · Remove-Item · rmdir)을 맨 앞에 두고 cmd /c · env · timeout 등을 거치지 말고 실행하라.')
        return  # git rm -r · Remove-Item -Recurse:$false 등: 작업 트리 재귀 삭제가 아니다
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
