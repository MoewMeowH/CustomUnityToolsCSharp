"""PreToolUse 훅 H7 (Bash · PowerShell): 지휘자가 글 규칙으로는 되풀이한 셸 습관 · 사용자 확인이 필요한 실행을 막는다.

(가) 백슬래시가 든 코드를 셸 글로 넘기기 — 따옴표 heredoc이어도 백슬래시가 바뀌어 들어간 적이 있다.
  - heredoc · here-string 본문에 \\가 있고 같은 명령의 명령 자리에 실행기 · 셸(python · node · bash · pwsh 등)이 있으면 막는다.
    코드 파일(.py .cs .js …)로 쓰는 것도 막는다. 작업 로그 · 커밋 메시지처럼 글만 쓰는 것(cat · tee · git commit · Set-Content)은 통과.
  - 실행기의 -c · -e 인자가 있고 따옴표 문자열에 \\가 있으면 막는다.
(나) 프로젝트 안 하위 폴더로 cd — 셸 현재 폴더가 다음 호출로 이어져(Bash · PowerShell 같은 작업 폴더) 뒤 명령의 상대 경로와
  작업 폴더 밖 쓰기 판정이 바뀐다. 서브셸 안 cd · 작업 폴더 밖으로 가는 cd는 이어지지 않는다 (실측 2026-11-05).
  - cd · chdir · pushd · Set-Location · sl · Push-Location을 차례로 따라가 명령 끝 위치를 구한다. Bash 서브셸 ( … ) 안은 빼고,
    popd · Pop-Location은 짝을 되돌린다. 상대 경로는 입력 cwd 기준, ~ · 환경 변수는 펼친다.
  - 끝 위치가 $CLAUDE_PROJECT_DIR 안이면서 루트가 아니면 막는다. 풀 수 없는 대상(셸 변수 · cd - · 명령 치환)도 막는다.
(다) 사용자용 HB 앱 실행 — 감싸개(cmd /c · start · Start-Process · powershell -File/-Command · bash -c · iex · env · nohup 등)를
  벗긴 실행 자리만 본다 (감싸개의 다른 인자 · 폴더 경로는 아님). 실행기 · 셸로 넘기는 heredoc · here-string 본문도 명령으로 본다.
  - HB_Chat(.bat) · HB_Chat_Desktop(.bat) · HB.Chat.Desktop(.exe) · dotnet run/watch/exec HB.Chat.Desktop은 늘 막는다.
  - HB_Orchestration.bat은 대시보드를 띄우는 것(인자 없음 = 메뉴 · dashboard · all)만 막는다. publish · config는 통과.
  - dotnet run · watch(--project · -p 또는 셸 현재 폴더, 정규화한 끝 폴더) · dotnet <앱>.dll · exec · HB.Chat.exe · HB.Orchestration.Dashboard.exe:
    명령줄 --urls가 없거나 비었거나 어느 값이든 포트가 5290 · 5280이면 막는다 (ASPNETCORE_URLS는 appsettings보다 순위가 낮아 근거가 아님).
    HB.Chat은 비지 않은 --Chat:DataDir(또는 같은 명령 앞의 Chat__DataDir=)도 있어야 한다. 풀 수 없는 cd 뒤 --project 없는 run은 막는다.
  - dotnet build · test · publish는 대상이 아니다. powershell -EncodedCommand는 읽을 수 없어 막는다.
(라) Sync-ProjectFiles.ps1 -Force(줄임 -F … 포함, :$false 제외) — 사용자 확인 뒤 사람이 `!`로.
- 실패하면 막는다: 통과 판정일 때만 exit 0. python이 없거나 스크립트를 못 찾으면 훅 명령의 `|| exit 2`가 막는다.

입력: stdin JSON {"tool_name": "Bash"|"PowerShell", "tool_input": {"command": "..."}, "cwd": "..."}.
"""
import os
import re
import sys

sys.dont_write_bytecode = True  # 훅 폴더에 __pycache__를 남기지 않는다 (저장소에 섞이지 않게)
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # -I는 스크립트 폴더를 경로에 넣지 않는다
from hook_common import BASH, PS, join_continuations, lex, native_path, read_input, same_or_inside, scan, segments, shell_of, strip_bodies, tokenize  # noqa: E402

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

# (다) 사용자용 앱: 세션 셸로 띄우면 사용자 데이터를 건드리거나(MSIX 가상화로 데이터가 두 벌로 갈린 적 있음, 2026-10-28) 사용자 포트를 차지한다
LAUNCH_RULE = '사용자용 앱(HB_Chat 5290 · 데스크톱 · 대시보드 5280)은 빌드까지만 하고 실행은 사용자에게 맡긴다. 시험 서버는 다른 포트(--urls) + HB.Chat은 --Chat:DataDir=<scratchpad>로 띄운다.'
USER_PORTS = {5290, 5280}
ALWAYS_APPS = ('hb_chat.bat', 'hb_chat.cmd', 'hb_chat', 'hb_chat_desktop.bat', 'hb_chat_desktop.cmd', 'hb_chat_desktop',
               'hb.chat.desktop.exe', 'hb.chat.desktop')
APP_NAMES = {'chat': 'HB.Chat', 'dashboard': '대시보드'}
UNKNOWN = object()  # cd 대상을 풀 수 없어 셸 위치를 모름
# 값을 하나 받는 Start-Process · powershell 매개변수 (실행 대상으로 보지 않고 건너뛴다)
PS_START_VALUES = {'-workingdirectory', '-wo', '-windowstyle', '-win', '-verb', '-credential', '-cred', '-redirectstandardoutput',
                   '-redirectstandarderror', '-redirectstandardinput', '-rso', '-rse', '-rsi'}
PS_HOST_VALUES = {'-executionpolicy', '-ep', '-ex', '-windowstyle', '-w', '-workingdirectory', '-wd', '-configurationname', '-inputformat',
                  '-outputformat', '-psconsolefile', '-version', '-settingsfile', '-custompipename'}


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


def base(tok: str) -> str:
    return os.path.basename(tok.replace('\\', '/').rstrip('/')).lower()


def exe(tok: str) -> str:
    return re.sub(r'\.exe$', '', base(tok))


def app_of(path: str) -> str | None:
    """프로젝트 폴더 · .csproj · .dll · .exe 경로가 가리키는 사용자용 앱 (chat · desktop · dashboard), 아니면 None."""
    name = re.sub(r'\.(?:csproj|dll|exe)$', '', base(path))
    # 끝으로 본다 — Bash가 따옴표 없는 경로의 백슬래시를 지워 이름이 앞 폴더와 붙는다 (F:xsrcHB.Chat)
    for suffix, app in (('hb.chat.desktop', 'desktop'), ('hb.orchestration.dashboard', 'dashboard'), ('hb.chat', 'chat')):
        if name.endswith(suffix):
            return app
    return None


def option_values(toks: list[str], name: str) -> list[str]:
    """--name=값 · --name 값의 모든 값 (대소문자 무시). ASP.NET 명령줄 설정은 같은 키의 마지막 값을 쓰므로 모두 본다."""
    out = []
    for i, t in enumerate(toks):
        low = t.lower()
        if low == name:
            out.append(toks[i + 1] if i + 1 < len(toks) else '')
        elif low.startswith(name + '='):
            out.append(t[len(name) + 1:])
    return out


def strip_lead(toks: list[str]) -> tuple[list[str], dict[str, str]]:
    """제어 낱말과 앞에 붙은 환경 변수 대입(Chat__DataDir=… dotnet run)을 뗀다 — 대입은 그 명령에만 적용되는 값으로 돌려준다."""
    env: dict[str, str] = {}
    while toks:
        if toks[0].lower() in LEAD_WORDS:
            toks = toks[1:]
        elif (m := re.match(r'^([A-Za-z_]\w*)=(.*)$', toks[0], re.DOTALL)) is not None:
            env[m.group(1).lower()] = m.group(2)
            toks = toks[1:]
        else:
            break
    return toks, env


def programs(toks: list[str], depth: int = 0, wd: str | None = None) -> list[tuple[str, list[str], dict[str, str], str | None]]:
    """감싸개(cmd /c · start · Start-Process · powershell -File/-Command · bash -c · iex · env · nohup 등)를 벗긴
    실제 실행 대상마다 (실행 토큰, 인자, 그 명령에 붙은 환경 변수 대입, 감싸개가 정한 작업 폴더 — 없으면 None).
    감싸개의 다른 인자(폴더 경로 등)는 실행 대상으로 보지 않는다. 너무 깊게 감싸면 해석하지 않고 막는다."""
    toks, env = strip_lead(toks)
    if not toks:
        return []
    if depth > 4:
        deny('감싸개(cmd · powershell · bash -c · env · nohup 등)가 너무 깊게 겹쳐 실행 대상을 정할 수 없다 — 명령을 바로 적는다.')
    name, rest = exe(toks[0]), toks[1:]

    def inner(text: str, shell: str, tok_shell: str | None = None) -> list:
        """셸에 넘긴 명령 글 — 묶음 조각마다 본다 (ls; HB_Chat.bat). 글 안의 cd는 뒤 조각의 작업 폴더가 된다."""
        out, here = [], wd
        for seg, _ in segments(text, shell):
            s = seg.lstrip('({&. \t\r\n').strip()
            if not s:
                continue
            piece = tokenize(s, tok_shell or shell)
            if piece and exe(piece[0]) in CDS:
                here = cd_target(piece, tok_shell or shell) or '~'
                continue
            out += programs(piece, depth + 1, here)
        return out

    def after_flags(args: list[str], numeric: bool = False) -> list[str]:
        k = 0
        while k < len(args) and (args[k].startswith('-') or args[k] == '' or (numeric and re.fullmatch(r'[\d.]+[smhd]?', args[k]))):
            k += 1
        return args[k:]

    if name == 'cmd':
        k = next((i for i, a in enumerate(rest) if a.lower() in ('/c', '/k', '/r')), None)
        # cmd 줄: 조각은 & · && · | 로 나누고(Bash 규칙), 토큰은 백슬래시를 이스케이프로 보지 않는다(PowerShell 규칙)
        return [] if k is None else inner(' '.join(rest[k + 1:]), BASH, PS)
    if name == 'start':  # cmd start "제목" 프로그램 — 첫 인자가 제목인지 알 수 없어 둘 다 본다
        args = [a for a in rest if not a.startswith('/')]
        args = args[1:] if args and args[0] == '' else args
        return programs(args, depth + 1, wd) + programs(args[1:], depth + 1, wd)
    if name in ('start-process', 'saps'):
        file_path, arg_list, k, here = None, [], 0, wd
        while k < len(rest):
            low = rest[k].lower()
            if low in ('-filepath', '-path'):
                file_path, k = (rest[k + 1] if k + 1 < len(rest) else ''), k + 2
            elif low in ('-workingdirectory', '-wo'):
                here, k = (rest[k + 1] if k + 1 < len(rest) else ''), k + 2
            elif low in ('-argumentlist', '-args'):
                arg_list.append(rest[k + 1] if k + 1 < len(rest) else '')
                k += 2
            elif low in PS_START_VALUES:
                k += 2
            elif low.startswith('-'):
                k += 1
            elif file_path is None:
                file_path, k = rest[k], k + 1
            else:
                arg_list.append(rest[k])
                k += 1
        if file_path is None:
            return []
        return programs([file_path] + tokenize(' '.join(arg_list).replace(',', ' '), PS), depth + 1, here)
    if name in ('powershell', 'pwsh'):
        k = 0
        while k < len(rest):
            low = rest[k].lower()
            if low in ('-file', '-f'):
                return programs(rest[k + 1:], depth + 1, wd)
            if low in ('-command', '-c'):
                return inner(' '.join(rest[k + 1:]), PS)
            # 매개변수 줄임(-en · -enco …)도 받는다 — -ex(ExecutionPolicy)는 -encodedcommand의 앞부분이 아니다
            if low == '-ec' or (len(low) > 1 and '-encodedcommand'.startswith(low)):
                deny('powershell -EncodedCommand는 훅이 실행할 명령을 읽을 수 없다 — 명령을 그대로 적는다.')
            if low in PS_HOST_VALUES:
                k += 2
            elif low.startswith('-'):
                k += 1
            else:
                return inner(' '.join(rest[k:]), PS)  # 이름 없는 인자는 -Command
        return []
    if name in ('bash', 'sh', 'zsh'):
        for k, a in enumerate(rest):
            if re.fullmatch(r'-[a-z]*c[a-z]*', a):
                return inner(rest[k + 1] if k + 1 < len(rest) else '', BASH)
            if not a.startswith('-'):
                return programs(rest[k:], depth + 1, wd)  # 스크립트 파일 실행
        return []
    if name in ('iex', 'invoke-expression'):
        return inner(' '.join(option_values(rest, '-command') or [a for a in rest if not a.startswith('-')]), PS)
    if name == 'env':
        args, k, here = rest, 0, wd
        while k < len(args):
            if args[k] in ('-S', '--split-string'):
                return inner(args[k + 1] if k + 1 < len(args) else '', BASH)
            if args[k] in ('-C', '--chdir'):
                here, k = (args[k + 1] if k + 1 < len(args) else ''), k + 2
            elif args[k] in ('-u', '--unset'):
                k += 2
            elif args[k].startswith('-'):
                k += 1
            else:
                break
        return programs(args[k:], depth + 1, here)
    if name in ('nohup', 'exec', 'time', 'nice', 'xargs', 'call', '&', '.', 'timeout'):
        args = rest
        if name == 'nice' and args[:1] == ['-n']:
            args = args[2:]
        return programs(after_flags(args, numeric=name == 'timeout'), depth + 1, wd)
    if name in ('invoke-item', 'ii'):
        return [(rest[0], [], env, wd)] if rest else []
    return [(toks[0], rest, env, wd)]


def place(wd: str | None, loc):
    """감싸개가 정한 작업 폴더를 셸 위치 기준으로 — 없으면 셸 위치, 변수 · ~ 등 풀 수 없으면 UNKNOWN."""
    if wd is None:
        return loc
    if not wd or re.search(r'[$%~`]', wd):
        return UNKNOWN
    p = native_path(wd.replace('\\', '/'))
    if os.path.isabs(p) or re.match(r'^[A-Za-z]:', p):
        return os.path.normpath(p)
    return os.path.normpath(os.path.join(loc, p)) if isinstance(loc, str) else UNKNOWN


def check_server(app: str, args: list[str], env: dict[str, str]) -> None:
    """HB.Chat · 대시보드 서버 실행: 명령줄 --urls가 없거나 사용자 포트면, HB.Chat은 데이터 폴더가 없거나 비었으면 막는다.
    ASPNETCORE_URLS는 근거로 받지 않는다 — appsettings.json의 Urls보다 순위가 낮다 (명령줄 --urls만 그 위)."""
    urls = option_values(args, '--urls')
    if not urls:
        deny(f'{APP_NAMES[app]}을(를) 명령줄 --urls 없이 띄운다 (appsettings · 기본값이 사용자 포트이고, ASPNETCORE_URLS는 appsettings보다 순위가 낮다). ' + LAUNCH_RULE)
    if any(not u.strip() for u in urls):
        deny(f'{APP_NAMES[app]}의 --urls 값이 비었다. ' + LAUNCH_RULE)
    if USER_PORTS & {int(p) for u in urls for p in re.findall(r':(\d+)', u)}:
        deny(f'{APP_NAMES[app]}을(를) 사용자 포트({" ".join(urls)})로 띄운다. ' + LAUNCH_RULE)
    if app == 'chat':
        dirs = option_values(args, '--chat:datadir') + option_values(args, '/chat:datadir')
        if 'chat__datadir' in env:
            dirs.append(env['chat__datadir'])
        if not dirs or not all(d.strip() for d in dirs):
            deny('HB.Chat 시험 서버에 비지 않은 --Chat:DataDir가 없다 (비면 사용자 데이터 폴더를 쓴다). ' + LAUNCH_RULE)


def check_dotnet(args: list[str], env: dict[str, str], loc) -> None:
    pos = [a for a in args if not a.startswith('-')]
    first = pos[0].lower() if pos else ''
    if first == 'exec':
        app = app_of(pos[1]) if len(pos) > 1 else None
    elif first.endswith('.dll'):
        app = app_of(first)
    elif first in ('run', 'watch'):  # dotnet watch만 써도 run이다
        # -p는 예전 --project 줄임 — 새 SDK의 속성(-p:이름=값 · -p 이름=값)은 '='가 있다. 드라이브 경로의 ':'는 프로젝트로 본다
        projects = option_values(args, '--project') + [v for v in option_values(args, '-p') if v and not v.startswith('-') and '=' not in v]
        if projects:
            p = native_path(projects[-1].replace('\\', '/'))
            if not os.path.isabs(p) and isinstance(loc, str):
                p = os.path.join(loc, p)
            app = app_of(os.path.normpath(p))  # --project . · a/../HB.Chat도 끝 폴더로
        elif loc is UNKNOWN:
            deny('cd 대상을 풀 수 없어 dotnet run이 어느 프로젝트를 띄우는지 정할 수 없다 — --project <절대 경로>로 적는다. ' + LAUNCH_RULE)
        else:
            app = app_of(loc) if loc else None
    else:
        return
    if app == 'desktop':
        deny('사용자용 데스크톱 앱을 띄운다 (dotnet HB.Chat.Desktop). ' + LAUNCH_RULE)
    if app:
        check_server(app, args, env)


def check_program(prog: str, args: list[str], env: dict[str, str], loc) -> None:
    # 이름은 끝으로 본다 — Bash는 따옴표 없는 F:\x\HB_Chat.bat의 백슬래시를 지운다 (F:xHB_Chat.bat). 확장자 없는 이름은 PATHEXT로 .bat이 돈다
    name = base(prog)
    if name.endswith(ALWAYS_APPS):
        deny(f'사용자용 앱을 띄운다 ({prog}). ' + LAUNCH_RULE)
    if name.endswith(('hb_orchestration.bat', 'hb_orchestration.cmd', 'hb_orchestration')):
        sub = next((a.lower() for a in args if not a.startswith(('-', '/'))), '')
        if sub in ('', 'dashboard', 'all'):
            deny(f'HB_Orchestration.bat {sub or "(메뉴)"}는 대시보드를 띄운다 (publish · config만 받는다). ' + LAUNCH_RULE)
    if name.endswith(('hb.chat.exe', 'hb.orchestration.dashboard.exe')) or name in ('hb.chat', 'hb.orchestration.dashboard'):
        check_server(app_of(name), args, env)
    if exe(prog) == 'dotnet':
        check_dotnet(args, env, loc)
    if name.endswith('sync-projectfiles.ps1'):
        for a in args:
            low = re.sub(r'^[–—―]', '-', a.lower())
            if re.fullmatch(r'-f(?:o(?:r(?:ce?)?)?)?(?::\S*)?', low) and not re.search(r':(?:\$false|0|false)$', low):
                deny('Sync-ProjectFiles.ps1 -Force는 지휘자가 쓰지 않는다 — 다른 파일을 덮기 전에 사용자 확인을 받고, '
                     '사용자가 `!` 명령으로 실행한다. -Apply가 멈췄으면 원인과 다른 파일 목록을 보고한다.')


def check_launch(raw: str, shell: str, cwd: str | None, depth: int = 0) -> None:
    """(다) 사용자용 HB 앱 실행 · (라) Sync -Force. 명령 자리만 본다 — heredoc · here-string 본문은 명령이 아니지만,
    같은 명령의 명령 자리에 실행기 · 셸이 있으면(bash <<'EOF' · @'…'@ | powershell -Command -) 본문도 명령으로 본다."""
    lx = lex(raw, shell)
    cmd = strip_bodies(raw, shell)
    for body in lx.expanded:
        cmd = cmd.replace(body, '', 1)
    visible, _ = scan(cmd, shell)
    if lx.bodies and depth < 2 and EXEC_WORD.search(visible):
        for body in lx.bodies:
            check_launch(body, shell, cwd, depth + 1)
    loc = os.path.normpath(native_path(cwd.replace('\\', '/'))) if cwd else None
    for seg, _depth in segments(cmd, shell):
        s = seg.lstrip('({&. \t\r\n').strip()
        if not s:
            continue
        toks = tokenize(s, shell)
        if toks and re.sub(r'(?i)\.exe$', '', toks[0]).lower() in CDS:
            # 서브셸 안 cd도 같은 서브셸의 dotnet run 위치를 바꾼다 — 넓게 따라간다. 풀 수 없으면 위치를 모름(UNKNOWN)으로
            # 두고 그 뒤 --project 없는 dotnet run은 막는다 (check_cd는 서브셸 안 cd를 보지 않는다)
            target = cd_target(toks, shell)
            value = expand(target, shell, set()) if target else os.path.expanduser('~')
            where = UNRESOLVED if value is UNRESOLVED else resolve(value, loc if isinstance(loc, str) else None, shell)
            loc = UNKNOWN if where is UNRESOLVED else where
            continue
        for prog, args, env, wd in programs(toks):
            check_program(prog, args, env, place(wd, loc))


def check(data: dict) -> None:
    tool = data.get('tool_name', '')
    if tool not in ('Bash', 'PowerShell'):
        return
    shell = shell_of(tool)
    raw = join_continuations((data.get('tool_input') or {}).get('command') or '', shell)
    check_code(raw, shell)
    check_cd(raw, shell, data.get('cwd'))
    check_launch(raw, shell, data.get('cwd'))


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
