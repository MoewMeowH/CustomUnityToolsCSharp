"""PreToolUse 훅 H7 (Bash · PowerShell): 지휘자가 글 규칙으로는 되풀이한 셸 습관 두 가지를 막는다.

(가) 백슬래시가 든 코드를 셸 글로 넘기기 — 따옴표 heredoc이어도 백슬래시가 바뀌어 들어간 적이 있다.
  - heredoc · here-string 본문에 \\가 있고 같은 명령의 명령 자리에 실행기 · 셸(python · node · bash · pwsh 등)이 있으면 막는다.
    코드 파일(.py .cs .js …)로 쓰는 것도 막는다. 작업 로그 · 커밋 메시지처럼 글만 쓰는 것(cat · tee · git commit · Set-Content)은 통과.
  - 실행기의 -c · -e 인자가 있고 따옴표 문자열에 \\가 있으면 막는다.
(나) 프로젝트 안 하위 폴더로 cd — 셸 현재 폴더가 다음 호출로 이어져(Bash · PowerShell 같은 작업 폴더) 뒤 명령의 상대 경로와
  작업 폴더 밖 쓰기 판정이 바뀐다. 서브셸 안 cd · 작업 폴더 밖으로 가는 cd는 이어지지 않는다 (실측 2026-11-05).
  - cd · chdir · pushd · Set-Location · sl · Push-Location을 차례로 따라가 명령 끝 위치를 구한다. Bash 서브셸 ( … ) 안은 빼고,
    popd · Pop-Location은 짝을 되돌린다. 상대 경로는 입력 cwd 기준, ~ · 환경 변수는 펼친다.
  - 끝 위치가 $CLAUDE_PROJECT_DIR 안이면서 루트가 아니면 막는다. 풀 수 없는 대상(셸 변수 · cd - · 명령 치환)도 막는다.
- 실패하면 막는다: 통과 판정일 때만 exit 0. python이 없거나 스크립트를 못 찾으면 훅 명령의 `|| exit 2`가 막는다.

입력: stdin JSON {"tool_name": "Bash"|"PowerShell", "tool_input": {"command": "..."}, "cwd": "..."}.
"""
import os
import re
import sys

sys.dont_write_bytecode = True  # 훅 폴더에 __pycache__를 남기지 않는다 (저장소에 섞이지 않게)
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # -I는 스크립트 폴더를 경로에 넣지 않는다
from hook_common import BASH, PS_DQ, PS_SQ, join_continuations, lex, native_path, read_input, same_or_inside, scan, shell_of, strip_bodies, tokenize  # noqa: E402

CODE_RULE = '파일 수정은 Edit/Write로, 스크립트는 Write로 파일을 만든 뒤 `python -I 파일`로 돌린다 (그 안의 다른 언어 이스케이프는 r\'\'\'…\'\'\').'
CD_RULE = ('절대 경로 · `git -C <경로>` · 도구의 경로 인자를 쓰거나, Bash 서브셸 `( cd x && … )` 안에서 옮긴다. '
           '이미 하위 폴더에 있으면 프로젝트 루트로 돌아가는 cd는 된다.')

INTERP = r'(?:python3?|py|node|perl|ruby)(?:\.exe)?'
# 명령 자리(맨 앞 · 묶음 기호 뒤, PowerShell 호출 연산자 & 포함)의 실행기 · 셸 — 본문을 코드로 돌리는 명령
EXEC_WORD = re.compile(r'(?:^|[;&|(\n])\s*&?\s*(?:' + INTERP + r'|(?:ba|z)?sh|pwsh|powershell|cmd)(?:\.exe)?(?=\s|$)', re.IGNORECASE | re.MULTILINE)
# 실행기 -c · -e: 앞의 옵션은 값 하나를 가질 수 있다 (-X utf8 -c)
INLINE = re.compile(r'(?:^|[;&|(\n])\s*&?\s*' + INTERP + r'(?:\s+-\w+(?:\s+(?!-)[^\s;&|]+)?)*?\s+-[ce](?=\s|$)', re.IGNORECASE)
# 코드 파일로 쓰기: 리다이렉트 · tee · Set-Content/Out-File 경로
CODE_TARGET = re.compile(r'(?:>>?|\btee\b(?:\s+-\S+)*|-(?:FilePath|Path|LiteralPath)\b|\b(?:Set-Content|Add-Content|Out-File)\b)\s*'
                         r'[^\s;&|]*\.(?:py|pyw|cs|csx|js|mjs|cjs|ts|ps1|psm1|sh|bash|bat|cmd|cshtml|razor)(?=$|[\s;&|)])', re.IGNORECASE)

CDS = {'cd', 'chdir', 'pushd', 'set-location', 'sl', 'push-location'}
PUSHES = {'pushd', 'push-location'}
POPS = {'popd', 'pop-location'}
# cd 앞에 올 수 있는 낱말 (Bash 제어 구문 · 접두)
LEAD_WORDS = {'if', 'then', 'else', 'elif', 'do', 'while', 'until', '!', 'builtin', 'command', 'time'}
PS_PATH_PARAMS = {'-path', '-literalpath', '-lp', '-pspath'}
PS_VALUE_PARAMS = {'-stackname'}
UNRESOLVED = object()


def deny(reason: str) -> None:
    print(f'[셸 습관 차단] {reason}', file=sys.stderr)
    sys.exit(2)


def check_code(raw: str, shell: str) -> None:
    lx = lex(raw, shell)
    visible, _ = scan(strip_bodies(raw, shell), shell)
    plain = lx.unquoted(raw)
    if any('\\' in body for body in lx.bodies):
        if EXEC_WORD.search(visible):
            deny('heredoc · here-string 본문에 백슬래시가 있고 같은 명령에 실행기 · 셸(python · node · bash 등)이 있다 '
                 '(글만 쓰는 cat · tee · git commit · Set-Content는 받는다). ' + CODE_RULE)
        # Bash는 heredoc 표시(<<)가 있는 조각의 쓰기 대상만 본다 — 같은 묶음의 다른 명령이 .py로 쓰는 것은 본문과 상관없다
        parts = [p for p in re.split(r'&&|\|\||;|\n', plain) if '<<' in p] if shell == BASH else [plain]
        if any(CODE_TARGET.search(p) for p in parts):
            deny('백슬래시가 든 본문으로 코드 파일을 쓴다. ' + CODE_RULE)
    if INLINE.search(visible) and any('\\' in q for q in lx.quoted):
        deny('실행기의 -c · -e 인자에 백슬래시가 있다. ' + CODE_RULE)


def segments(cmd: str, shell: str) -> list[tuple[str, int]]:
    """따옴표 · 주석 밖의 묶음 기호(&& || ; | 줄바꿈, Bash &)로 나눈 (조각, 조각 시작의 Bash 괄호 깊이) 목록. 따옴표는 원문 그대로 남긴다."""
    out: list[tuple[str, int]] = []
    buf: list[str] = []
    depth = start = 0
    esc = '\\' if shell == BASH else '`'
    sq, dq = ("'", '"') if shell == BASH else (PS_SQ, PS_DQ)
    breaks = (' ', '\t', '(') if shell == BASH else (' ', '\t', '(', '{', ',')  # 이 뒤의 #가 주석 (Bash ${#x}는 글자)
    i, n = 0, len(cmd)

    def flush() -> None:
        out.append((''.join(buf), start))
        buf.clear()

    while i < n:
        c = cmd[i]
        if c == esc and i + 1 < n:
            buf.append(cmd[i:i + 2])
            i += 2
            continue
        if c in sq:
            j = i + 1
            while j < n:
                if cmd[j] in sq:
                    if shell != BASH and j + 1 < n and cmd[j + 1] in sq:
                        j += 2
                        continue
                    break
                j += 1
            buf.append(cmd[i:j + 1])
            i = j + 1
            continue
        if c in dq:
            j = i + 1
            while j < n and cmd[j] not in dq:
                j += 2 if cmd[j] == esc else 1
            buf.append(cmd[i:j + 1])
            i = j + 1
            continue
        word_start = not buf or buf[-1][-1:] in breaks or not ''.join(buf).strip()
        if c == '#' and word_start:
            end = cmd.find('\n', i)
            i = n if end < 0 else end
            continue
        if shell != BASH and cmd.startswith('<#', i):
            end = cmd.find('#>', i + 2)
            i = n if end < 0 else end + 2
            continue
        two = cmd[i:i + 2]
        if two in ('&&', '||'):
            flush()
            start = depth
            i += 2
            continue
        if c in ';\n' or (c == '|' and not (i > 0 and cmd[i - 1] == '>')) or \
                (shell == BASH and c == '&' and not (i > 0 and cmd[i - 1] in '<>') and cmd[i + 1:i + 2] != '>'):
            flush()
            start = depth
            i += 1
            continue
        if shell == BASH and c == '(':
            depth += 1
            if not ''.join(buf).strip():
                start = depth  # 조각 맨 앞의 ( 는 이 조각이 서브셸 안이라는 뜻
        elif shell == BASH and c == ')':
            depth = max(0, depth - 1)
        buf.append(c)
        i += 1
    flush()
    return out


def expand(tok: str, shell: str, assigned: set[str]):
    """~ · 환경 변수를 펼친 값, 풀 수 없으면 UNRESOLVED."""
    if tok == '-' or '$(' in tok or '`' in tok:
        return UNRESOLVED
    if tok == '~' or tok.startswith('~/') or tok.startswith('~\\'):
        tok = os.path.expanduser('~') + tok[1:]

    def env(m: re.Match) -> str:
        name = m.group(1) or m.group(2) or m.group(3)
        if shell != BASH and m.group(3) is None:  # PowerShell: $env:X만 환경 변수, $HOME은 자동 변수
            if name.lower() == 'home':
                return os.path.expanduser('~')
            raise LookupError(name)
        if name in assigned:
            raise LookupError(name)
        if name not in os.environ:
            if name == 'HOME':
                return os.path.expanduser('~')
            raise LookupError(name)
        return os.environ[name]

    pattern = r'\$\{(\w+)\}|\$(\w+)' if shell == BASH else r'\$\{(\w+)\}|\$(?!env:)(\w+)|\$env:(\w+)'
    try:
        tok = re.sub(pattern, env, tok, flags=0 if shell == BASH else re.IGNORECASE)
    except LookupError:
        return UNRESOLVED
    return UNRESOLVED if '$' in tok else tok


def resolve(target: str, loc: str | None, shell: str):
    """cd 대상의 끝 위치 (정규화한 Windows식 경로), 프로젝트와 상관없는 곳이면 None, 풀 수 없으면 UNRESOLVED."""
    p = target.replace('\\', '/')
    if shell == BASH:
        p = native_path(p)
    if re.match(r'^[A-Za-z]:(/|$)', p) or p.startswith('//'):
        return os.path.normpath(p if len(p) > 2 else p + '/')
    if re.match(r'^[A-Za-z][\w.-]+:', p):
        return None  # PowerShell 공급자 경로 (HKLM: · Env: …)
    if p.startswith('/'):
        if shell != BASH and loc:
            return os.path.normpath(loc[:2] + p)  # PowerShell \x · /x는 현재 드라이브 기준
        return None  # Git Bash /tmp · /usr 등 — 드라이브 밖
    if loc is None:
        return UNRESOLVED
    return os.path.normpath(os.path.join(loc, p))


def cd_target(toks: list[str], shell: str) -> str | None:
    """cd 류 토큰에서 대상 (없으면 None = 홈)."""
    args = toks[1:]
    if shell == BASH:
        rest = []
        only = False
        for a in args:
            if not only and a == '--':
                only = True
            elif not only and a.startswith('-') and a != '-':
                continue
            else:
                rest.append(a)
        return rest[0] if rest else None
    take = skip = False
    for a in args:
        low = a.lower()
        if skip:
            skip = False
        elif take:
            return a
        elif low in PS_PATH_PARAMS:
            take = True
        elif low in PS_VALUE_PARAMS:
            skip = True
        elif low.startswith('-') and a != '-':
            continue
        else:
            return a
    return None


def check_cd(raw: str, shell: str, cwd: str | None) -> None:
    lx = lex(raw, shell)
    cmd = strip_bodies(raw, shell)
    for body in lx.expanded:  # 따옴표 없는 heredoc · @"…"@ 본문도 명령이 아니다
        cmd = cmd.replace(body, '', 1)
    start = os.path.normpath(cwd.replace('\\', '/')) if cwd else None
    if start and shell == BASH:
        start = os.path.normpath(native_path(cwd.replace('\\', '/')))
    loc, stack, assigned, moved = start, [], set(), False
    for seg, depth in segments(cmd, shell):
        s = seg.lstrip('({&. \t\r\n').strip()
        if not s:
            continue
        toks = tokenize(s, shell)
        while toks and toks[0].lower() in LEAD_WORDS:
            toks = toks[1:]
        if not toks:
            continue
        if shell == BASH:
            if toks[0] == 'export':
                toks = toks[1:]
            for t in toks:
                if (m := re.match(r'^([A-Za-z_]\w*)=', t)) is None:
                    break
                assigned.add(m.group(1))
        elif (m := re.match(r'^\$([A-Za-z_]\w*)\s*=', s)) is not None:
            assigned.add(m.group(1))
        name = re.sub(r'(?i)\.exe$', '', toks[0]).lower()
        if shell == BASH and depth > 0:
            continue  # 서브셸 안: 이어지지 않는다
        if name in POPS:
            loc = stack.pop() if stack else loc
            continue
        if name not in CDS:
            continue
        target = cd_target(toks, shell)
        if name in PUSHES:
            stack.append(loc)
        if target is None:
            loc, moved = os.path.expanduser('~'), True  # 인자 없는 cd는 홈
            continue
        value = expand(target, shell, assigned)
        if value is UNRESOLVED:
            deny(f"cd 대상 '{target}'을(를) 풀 수 없다 (셸 변수 · cd - · 명령 치환). 펼친 절대 경로를 쓰거나 " + CD_RULE)
        where = resolve(value, loc, shell)
        if where is UNRESOLVED:
            deny(f"셸 현재 폴더를 몰라 상대 경로 cd '{target}'의 위치를 정할 수 없다. " + CD_RULE)
        loc, moved = where, True
    if not moved or loc is None or (start and os.path.normcase(os.path.abspath(loc)) == os.path.normcase(os.path.abspath(start))):
        return  # 옮기지 않았거나 · 프로젝트와 상관없는 곳 · 처음 자리로 돌아옴
    root = os.environ.get('CLAUDE_PROJECT_DIR')
    if not root:
        deny('CLAUDE_PROJECT_DIR가 없어 cd가 프로젝트 안인지 판정할 수 없다. ' + CD_RULE)
    if same_or_inside(loc, root) and os.path.normcase(os.path.abspath(loc)).rstrip('\\/') != os.path.normcase(os.path.abspath(root)).rstrip('\\/'):
        deny(f'이 명령이 끝나면 셸 현재 폴더가 프로젝트 안 하위 폴더({loc})로 남아 다음 호출까지 이어진다 '
             f'(Bash · PowerShell 같은 작업 폴더, 작업 폴더 밖 쓰기 판정도 바뀜). ' + CD_RULE)


def check(data: dict) -> None:
    tool = data.get('tool_name', '')
    if tool not in ('Bash', 'PowerShell'):
        return
    shell = shell_of(tool)
    raw = join_continuations((data.get('tool_input') or {}).get('command') or '', shell)
    check_code(raw, shell)
    check_cd(raw, shell, data.get('cwd'))


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
