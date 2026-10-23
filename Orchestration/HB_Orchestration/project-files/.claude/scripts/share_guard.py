"""PreToolUse 훅 H5 (Bash · PowerShell의 slackplan 올리기): 계획서 · 글이 Slack으로 나가기 전에 비밀 값 · 개인 값 · 로컬 경로를 막는다
(계획서 2026-10-23_plan-share-명령과-share_guard, 상위 SlackPlanBot 2026-10-23_오케스트레이션-연결 계획 12 · 13 · 15, /cross-review 32ab98a8a9 반영).

- 대상: 명령 자리의 slackplan(.exe · .dll, 따옴표로 감싼 경로 포함) · `dotnet [exec] …slackplan.dll …` ·
  `dotnet run … -- <하위 명령>`(명령이나 셸 현재 폴더에 SlackPlanBot이 있을 때) · 다른 셸을 거친 호출(cmd /c 등 — 해석하지 않고 막음).
  대상이면 단독 명령만 받는다 — 묶음 · cd류 · 명령 치환 · 리다이렉트 · heredoc/here-string은 막는다 (검사한 글과 나가는 글이 달라질 수 있다).
- 하위 명령: 올리는 것(new · revise · update-summary · reply · ask-consent)은 검사, 밖으로 글을 내지 않는 것(READ)은 통과, 그 밖은 막는다.
  `--dry-run`은 봇이 그것을 아는 new · revise에서만 통과 (reply는 그 낱말을 글로 올린다).
- `--allow-warnings`는 늘 막는다 — 봇 경고를 넘기는 것은 사람의 `!` 명령으로만.
- 로컬 기록을 지우는 purge · unlink의 `--yes`도 막는다 (미리보기는 통과). 셸이 펼칠 수 있는 인자($'…' · {a,b} · 백틱 등)가 있으면 미리보기도 막는다.
- 검사 글: new · revise의 계획서 파일 내용과 파일 이름(제목 기본값 · 첨부 이름), --title · --summary · --text 값, reply 글.
  글 인자에 셸 변수(`$` · `%이름%`)가 있거나, 경로에 SLACKPLAN_ROOT 말고 다른 변수가 있거나, 파일을 못 읽으면 막는다.
  --text 없는 update-summary는 봇 저장소의 요약 파일을 훅이 같은 파일로 찾을 수 없어 막는다.
- 패턴: push_guard의 BUILT_IN(가짜 값 표식은 봐주지 않음 — 밖으로 나가는 글) + push_guard.local.txt 개인 값 + share_guard.local.txt
  (프로젝트별, 같은 형식, 'allow:'는 사람이 넣은 예외) + 무시 폴더 언급 + 로컬 절대 경로. 계획서가 무시 폴더 아래면 막는다.
  share_guard.local.txt가 저장소에 추적되면 막는다.
- 실패하면 막는다: 검사 중 오류는 exit 2.
- slackplan 낱말이 명령 어디든(따옴표 안 · 쪼갠 따옴표 · $'' · 줄 이음 · 백슬래시 경로 포함) 있으면 대상이다 — 앞 낱말(env · X=1 ·
  command · exec · nice · time · sudo · nohup · xargs · eval · env -S · PowerShell '.')이 붙은 형식은 해석하지 못해 막힌다.
  예외: 단독 읽기 명령 하나(grep · rg(--pre 없이) · git log/show/diff/status · Select-String — 묶음 · 파이프 · & · 리다이렉트 · 괄호 ·
  중괄호 · 명령 치환 없음). git commit -m 속 낱말도 막힌다 (사용자 결정 2026-10-23: git은 크게 신경 쓰지 않음).
  heredoc · here-string 본문: 셸이 펼치지 않는 것(<<'EOF' · @'…'@)만 대상 판정에서 뺀다. 그 본문에 slackplan이 있으면 묶음의 모든 조각이
  데이터 전용(cat · tee · git commit · Set-Content · Add-Content · Out-File · 변수 대입)일 때만 통과 — 허용 목록 (계획서 2026-10-23_H5-셸-문법-우회).
  따옴표 없는 heredoc · @"…"@ 안의 $( )는 명령 치환으로 본다.
- 올리는 명령의 따옴표 밖 ~(낱말 시작) · bash 글롭(* ? [)은 막는다 — 셸이 사용자 경로 · 파일 이름으로 펼친다.
- H5는 지휘자의 실수를 막는 안전망이다 (봇 자체 검사가 첫 겹). 셸 문법을 다 해석하지 않으므로 못 보는 것: 변수에 담은 실행 파일(& $exe ·
  $X purge), 이스케이프로 만든 글자($'slack\\x70lan'), 글롭으로 쓴 이름(slackpla?), 파일을 거친 간접 실행(스크립트 · git hook),
  다른 이름의 사본 경로로 dotnet run.

입력: stdin JSON {"tool_name": ..., "tool_input": {"command": ...}, "cwd": ...}.
"""
import os
import re
import subprocess
import sys

sys.dont_write_bytecode = True  # 훅 폴더에 __pycache__를 남기지 않는다 (저장소에 섞이지 않게)
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # -I는 스크립트 폴더를 경로에 넣지 않는다
from hook_common import BASH, CD, CHAIN, PS, data_only, has_substitution, lex, native_path, read_input, scan, shell_of, strip_bodies, tokenize  # noqa: E402
from push_guard import IGNORED_DIRS, ignored, load_patterns, mask  # noqa: E402

UPLOAD = {'new', 'revise', 'update-summary', 'reply', 'ask-consent'}
# 밖으로 글을 내지 않는 하위 명령 (SlackPlanBot App.cs 2026-10-23 기준: react는 이모지만, inbox --note는 로컬 작업 로그만)
READ = {'find', 'thread', 'status', 'inbox', 'files', 'react', 'listen', 'probe-ratelimit', 'list', 'approval-record', '-h', '--help'}
# 로컬 기록을 지우는 하위 명령 (READ에 넣지 않는다): 글자 그대로의 인자로 된 미리보기만 통과, --yes는 사람의 `!` 명령으로만
# (계획서 2026-10-23_H5-새-하위-명령과-지우는-명령, /self-review 8c1412729e — 셸이 펼쳐 --yes가 되는 인자도 막는다)
DESTRUCTIVE = {'purge', 'unlink'}
LITERAL_ARG = re.compile(r'^[A-Za-z0-9._:-]+$')
DRY_RUN_AWARE = {'new', 'revise'}
TEXT_OPTIONS = {'--title', '--summary', '--text'}
ID_OPTIONS = {'--thread', '--item'}  # new: 글이 아니라 ID 값
LOCAL_FILE = os.path.join('.claude', 'share_guard.local.txt')
ROOT_VAR = re.compile(r'\$\{SLACKPLAN_ROOT\}|\$env:SLACKPLAN_ROOT\b|\$SLACKPLAN_ROOT\b', re.IGNORECASE)
THREAD_KEY = re.compile(r'^[A-Za-z0-9._-]+$')
SHELL_VAR = re.compile(r'\$|%[A-Za-z_][A-Za-z0-9_()]*%')
EXE = re.compile(r'(?i)^slackplan(?:\.exe|\.dll)?$')
# 실행 파일 이름 낱말 (SlackPlanBot · SLACKPLAN_ROOT처럼 뒤에 글자가 이어지면 아님). 따옴표 · 백틱 · $를 지운 글에서 찾는다
# — 'slack''plan' · slack$''plan 같은 쪼개기도 잡도록. 백슬래시는 지운 글(slack\plan)과 '/'로 바꾼 글(C:\bot\slackplan.exe) 둘 다 본다
# (계획서 2026-10-23_H5-앞-낱말-우회 결정, /self-review e256053430 · 3efd4d0922).
# 앞쪽 경계는 두지 않는다: 따옴표를 지우면 -O'slackplan …'이 -Oslackplan이 된다 (git grep -O 실측 2026-10-23).
SLACKPLAN_WORD = re.compile(r'(?i)slackplan(?:\.exe|\.dll)?(?![\w-])')
# PowerShell 굽은 따옴표도 지운다 — sl‘’ackplan으로 쪼갠 이름 (/cross-review 8722c947f7 1-F4)
QUOTING = re.compile('["\'`$‘’‚‛“”„]')
# slackplan 낱말이 인자로만 나올 수 있는 읽기 명령 — 단독 명령 하나일 때만 (셸 명령 · 외부 프로그램을 실행할 수 있는 것은 넣지 않는다:
# sed · awk · find · xargs · eval · git grep -O · rg --pre · PowerShell 스크립트 블록 등).
READERS = {'grep', 'egrep', 'fgrep', 'rg', 'select-string', 'sls'}
GIT_READERS = {'log', 'show', 'diff', 'status'}
# 읽기 예외를 끄는 글자: 묶음 · 파이프 · & · 리다이렉트 · 괄호 · 중괄호 (서브셸 · PowerShell 하위 식 · 스크립트 블록 · 해시 테이블)
NOT_STANDALONE = re.compile(r'[;&|<>(){}\n]')
# bash가 펼치는 따옴표 밖 글자: 낱말 시작의 ~ (사용자 폴더) · 글롭 (파일 이름). PowerShell은 ~만 본다.
BASH_EXPANDS = re.compile(r'(?:^|[\s=:])~|[*?\[{]')  # { — 중괄호 펼침 뒤 ~ 펼침 ({~,x} → 사용자 경로, 실측)
PS_EXPANDS = re.compile(r'(?:^|\s)~')


def has_slackplan_word(text: str) -> bool:
    plain = QUOTING.sub('', text)
    return bool(SLACKPLAN_WORD.search(plain.replace('\\', '')) or SLACKPLAN_WORD.search(plain.replace('\\', '/')))


def mentions_slackplan(text: str) -> bool:
    """slackplan이 이름 어디에든 든 글자 (SlackPlanBot · SLACKPLAN_ROOT 포함) — 셸이 실행할 수 있는 본문 판정용."""
    return 'slackplan' in QUOTING.sub('', text).replace('\\', '').lower()
WRAPPER = re.compile(r'(?i)\b(?:cmd(?:\.exe)?\s+/[ck]|(?:pwsh|powershell)(?:\.exe)?\b[^\n]*\s-c(?:ommand)?|(?:ba)?sh\s+-c|start-process|invoke-expression|iex)\b')

MENTIONS = [(re.compile(r'(?i)(?<![A-Za-z0-9_])' + r'[\\/]'.join(map(re.escape, rule)) + r'[\\/]'), '/'.join(rule) + '/')
            for rule in IGNORED_DIRS]
ABS_PATHS = [
    (re.compile(r'(?<![A-Za-z0-9])[A-Za-z]:[\\/]'), '드라이브 문자 경로'),
    (re.compile(r'\\\\[A-Za-z0-9._$-]+\\'), 'UNC 경로'),
    (re.compile(r'(?<![:\w/])//[A-Za-z0-9._-]+/'), 'UNC 경로(슬래시)'),
    (re.compile(r'(?<![A-Za-z0-9_.])/(?:Users|home)/'), '사용자 폴더 경로'),
    (re.compile(r'(?<![\w.~/-])/(?:tmp|etc|var|opt|mnt|usr|root|private)/'), 'POSIX 절대 경로'),
    (re.compile(r'(?:^|[\s"\'(\[`=])/[A-Za-z]/[^\s/]'), 'Git Bash 드라이브 경로'),
]

SIMPLE = '단독 명령으로 따로 실행하라: `dotnet run --no-build --project "$SLACKPLAN_ROOT/src/SlackPlanBot" -- <하위 명령> …`.'


def deny(reason: str) -> None:
    print(f'[공유 차단] {reason}', file=sys.stderr)
    sys.exit(2)


def base(tok: str) -> str:
    return tok.replace('\\', '/').rsplit('/', 1)[-1]


def is_target(cmd: str, stripped: str, inner: list[str], toks: list[str], cwd: str) -> bool:
    visible = stripped + '\n' + '\n'.join(inner)
    low = cmd.lower()
    # 명령 자리의 slackplan (grep slackplan 같은 인자는 아님)
    if re.search(r'(?im)(?:^|[;&|(]|&&|\|\|)\s*(?:&\s*)?(?:\S*[\\/])?slackplan(?:\.exe|\.dll)?(?![\w.-])', visible):
        return True
    # 첫 명령의 실행 파일 (따옴표로 감싼 경로는 scan이 비우므로 토큰으로 본다)
    head = toks[1:] if toks[:1] == ['&'] else toks
    if head and EXE.match(base(head[0])):
        return True
    if head and re.sub(r'(?i)\.exe$', '', base(head[0])).lower() == 'dotnet' and any(EXE.match(base(t)) for t in head[1:]):
        return True
    # 다른 셸을 거쳐 부르는 형식은 해석하지 않고 막도록 대상으로 본다.
    if 'slackplan' in low and WRAPPER.search(stripped):
        return True
    # 앞 낱말(env · X=1 · nice · xargs · eval · PowerShell '.' 등)이 붙거나 따옴표 · 줄 이음으로 감싸도, slackplan 낱말이 어디든 있으면
    # 대상 — slackplan_args가 해석하지 못해 막는다. 단독 읽기 명령의 인자로만 나온 경우만 뺀다 (계획서 2026-10-23_H5-앞-낱말-우회).
    if has_slackplan_word(cmd) and not reader_only(stripped, inner, toks):
        return True
    dotnet_run = re.search(r'(?i)\bdotnet(?:\.exe)?\b[^\n;&|]*?\brun\b', visible)
    return bool(dotnet_run) and ('slackplanbot' in low or 'slackplanbot' in cwd.lower())


def reader_only(stripped: str, inner: list[str], toks: list[str]) -> bool:
    """단독 읽기 명령 하나면 True: 묶음 · 파이프 · & · 리다이렉트 · 괄호 · 중괄호 · 명령 치환이 없고, 첫 낱말이 READERS
    (rg는 --pre 없이) 또는 git의 GIT_READERS 하위 명령일 때."""
    if inner or '$(' in stripped or '`' in stripped or NOT_STANDALONE.search(stripped.strip()) or not toks:
        return False
    name = re.sub(r'(?i)\.exe$', '', base(toks[0])).lower()
    if name == 'git':
        return len(toks) > 1 and toks[1].lower() in GIT_READERS
    if name == 'rg' and any(t.startswith('--pre') for t in toks[1:]):
        return False
    return name in READERS


def slackplan_args(toks: list[str]) -> list[str]:
    if toks and toks[0] == '&':  # PowerShell 호출 연산자
        toks = toks[1:]
    if not toks:
        deny('slackplan 호출 형식을 해석하지 못했다. ' + SIMPLE)
    if EXE.match(base(toks[0])):
        return toks[1:]
    if re.sub(r'(?i)\.exe$', '', base(toks[0])).lower() == 'dotnet':
        if len(toks) > 1 and toks[1] == 'run':
            if '--' not in toks:
                deny('dotnet run에서 slackplan 인자는 `--` 뒤에 둔다. ' + SIMPLE)
            return toks[toks.index('--') + 1:]
        for i, t in enumerate(toks[1:], 1):  # dotnet [exec] …/slackplan.dll <하위 명령>
            if EXE.match(base(t)):
                return toks[i + 1:]
    deny('slackplan 호출 형식을 해석하지 못했다 (cmd /c · 스크립트 · 별칭 등). ' + SIMPLE)
    return []


def resolve_path(arg: str, cwd: str, what: str) -> str:
    root = os.environ.get('SLACKPLAN_ROOT')
    if ROOT_VAR.search(arg):
        if not root:
            deny(f'{what} 경로에 SLACKPLAN_ROOT가 있는데 훅 환경에 그 변수가 없다.')
        arg = ROOT_VAR.sub(lambda _: root.replace('\\', '/'), arg)
    if '$' in arg or '%' in arg or arg.startswith('~'):
        deny(f"{what} 경로 '{arg}'에 훅이 풀 수 없는 변수가 있다 — 실제 경로로 적어라.")
    path = native_path(arg)
    return os.path.normpath(path if os.path.isabs(path) else os.path.join(cwd, path))


def read_text(path: str, what: str) -> str:
    try:
        with open(path, encoding='utf-8-sig') as f:
            return f.read()
    except OSError as ex:
        deny(f'{what}을(를) 읽지 못했다 ({type(ex).__name__}: {path}). 검사하지 못한 글은 내보내지 않는다.')
    return ''


def load_local(project_dir: str) -> tuple[list[re.Pattern], list[re.Pattern], str | None]:
    path = os.path.join(project_dir, LOCAL_FILE)
    pats: list[re.Pattern] = []
    allows: list[re.Pattern] = []
    if not os.path.isfile(path):
        return pats, allows, None
    for line in open(path, encoding='utf-8-sig'):
        line = line.strip()
        if not line or line.startswith('#'):
            continue
        if line.startswith('allow:'):
            allows.append(re.compile(line[len('allow:'):].strip()))
        else:
            pats.append(re.compile(line))
    return pats, allows, path


def tracked(path: str) -> bool:
    folder = os.path.dirname(path)
    r = subprocess.run(['git', '-C', folder, 'ls-files', '--error-unmatch', os.path.basename(path)], capture_output=True)
    return r.returncode == 0


def inline_text(label: str, value: str) -> tuple[str, str]:
    """명령줄에 직접 쓴 글. 셸이 값을 펼치면 검사한 글과 나가는 글이 달라지므로 변수 모양은 막는다."""
    if SHELL_VAR.search(value) or '`' in value:
        deny(f'{label}에 셸 변수 모양($ · %이름% · 백틱)이 있다 — 셸이 펼친 값은 훅이 검사할 수 없다. 글에서 빼라.')
    return label, value


def collect(sub: str, args: list[str], cwd: str) -> list[tuple[str, str]]:
    """(이름표, 글) 목록. 올라가는 글을 하나라도 못 정하면 막는다."""
    texts: list[tuple[str, str]] = []
    positional: list[str] = []
    i = 0
    while i < len(args):
        a = args[i]
        if a in TEXT_OPTIONS or (sub == 'new' and a in ID_OPTIONS):
            if i + 1 >= len(args):
                deny(f'{a} 뒤에 값이 없다.')
            if a in ID_OPTIONS:
                if not THREAD_KEY.match(args[i + 1]):
                    deny(f"{a} 값 '{args[i + 1]}'이(가) ID 모양(영숫자 · . _ -)이 아니다.")
            else:
                texts.append(inline_text(a, args[i + 1]))
            i += 2
            continue
        if a == '--dry-run' and sub in DRY_RUN_AWARE:
            i += 1
            continue
        if a.startswith('-'):
            deny(f"올리는 명령의 옵션 '{a}'을(를) 훅이 모른다 — 무엇이 나가는지 정할 수 없다. " + SIMPLE)
        positional.append(a)
        i += 1
    if sub in ('new', 'revise'):
        want = 1 if sub == 'new' else 2
        if len(positional) != want:
            deny(f'{sub}의 인자 수가 맞지 않는다 (new <plan.md> / revise <스레드> <plan.md>).')
        path = resolve_path(positional[-1], cwd, '계획서')
        if (rule := ignored(path)) is not None:
            deny(f'계획서가 {rule} 아래에 있다 — 그 폴더의 파일은 밖으로 내보내지 않는다.')
        texts.append(('계획서', read_text(path, '계획서')))
        texts.append(('파일 이름', os.path.basename(path)))  # 제목 기본값 · 첨부 이름으로 나간다
    elif sub == 'reply':
        if len(positional) < 2:
            deny('reply <스레드> <글> 형식이 아니다.')
        texts.append(inline_text('reply 글', ' '.join(positional[1:])))
    elif sub == 'update-summary' and not any(t[0] == '--text' for t in texts):
        deny('--text 없는 update-summary는 봇 저장소의 요약 파일을 올리는데, 훅은 그 파일을 같은 파일로 찾을 수 없다 — --text로 넘기거나 사람이 `!`로 실행하라.')
    return texts


def find_hits(texts: list[tuple[str, str]], project_dir: str) -> list[str]:
    secret_pats, allows, _ = load_patterns(project_dir)
    local_pats, local_allows, local = load_local(project_dir)
    if local is not None and tracked(local):
        deny(f'{LOCAL_FILE}이(가) 저장소에 추적되고 있다. git rm --cached 후 .gitignore에 넣어라.')
    allows += local_allows
    rules = ([(p, '비밀 · 개인 값') for p in secret_pats] + [(p, '프로젝트 패턴') for p in local_pats]
             + [(p, f'{name} 언급') for p, name in MENTIONS] + ABS_PATHS)
    hits = []
    for label, text in texts:
        for no, line in enumerate(text.splitlines() or [''], 1):
            for pat, name in rules:
                for found in pat.finditer(line):
                    # 맞은 자리부터 공백 전까지 (경로 패턴은 앞부분만 맞으므로 allow:는 이 토큰에 적용)
                    token = re.match(r'\s*\S*', line[found.start():]).group(0).strip()
                    if any(a.search(token) for a in allows):
                        continue
                    hits.append(f'{label}:{no} {name} ({mask(token)})')
    return hits


def check(data: dict) -> None:
    tool = data.get('tool_name', '')
    if tool not in ('Bash', 'PowerShell'):
        return
    shell = shell_of(tool)
    raw = (data.get('tool_input') or {}).get('command') or ''
    cmd = strip_bodies(raw, shell)
    cwd = data.get('cwd') or os.getcwd()
    stripped, inner = scan(cmd, shell)
    # 본문을 셸 · 실행기에 넘기면 본문이 곧 명령이다 (bash <<'EOF' · @'…'@ | iex) — 본문에 slackplan이 있으면 데이터 전용 명령만 받는다.
    # 본문은 낱말보다 넓게 본다: SlackPlanBot 프로젝트 dotnet run · SLACKPLAN_ROOT도 (/self-review security C2 — 실측 exit 0이던 형식)
    if any(mentions_slackplan(body) for body in lex(raw, shell).bodies) and not data_only(stripped, inner, shell):
        deny('heredoc · here-string 본문에 slackplan이 있는데 받는 명령이 데이터 전용(cat · tee · git commit · Set-Content · '
             'Add-Content · Out-File · 변수 대입)이 아니다 — 훅이 그 본문을 명령으로 검사하지 못한다. 스크립트는 Write로 파일을 만들어 실행하라. ' + SIMPLE)
    if not is_target(cmd, stripped, inner, tokenize(cmd.strip(), shell), cwd):
        return
    if cmd != raw:
        deny('slackplan 호출에 heredoc · here-string이 있다 — 그 본문은 훅이 검사하지 못한다. 글은 파일로 넘기거나 한 줄 인자로 적어라. ' + SIMPLE)
    if has_substitution(stripped, inner, shell):
        deny('slackplan 호출에 명령 치환($( ) · 백틱)이 있다. ' + SIMPLE)
    body = stripped.strip()
    if shell == PS and body.startswith('&'):  # PowerShell 호출 연산자 (& "…\slackplan.exe")는 묶음이 아니다
        body = body[1:].lstrip()
    if CHAIN.search(body):
        deny('slackplan 호출이 다른 명령과 묶여 있다. ' + SIMPLE)
    if CD.search(stripped):
        deny('slackplan 호출과 cd가 함께 있다 (상대 경로의 기준이 달라진다). ' + SIMPLE)
    if re.search(r'[<>]', body):
        deny('slackplan 호출에 리다이렉트(> · 2>&1 · <)가 있다 — 출력은 도구가 그대로 받으니 리다이렉트 없이 실행하라. ' + SIMPLE)

    args = slackplan_args(tokenize(cmd.strip(), shell))
    if args and args[0] in DESTRUCTIVE:
        odd = [a for a in args[1:] if not LITERAL_ARG.match(a)]
        if odd:
            deny(f"{args[0]}의 인자 '{odd[0]}'에 셸이 펼칠 수 있는 글자가 있다 — 지우는 명령은 글자 그대로의 인자(영숫자 · . _ : -)만 받는다.")
        if '--yes' in args[1:]:
            deny(f'{args[0]} --yes는 지휘자가 붙이지 않는다 — 미리보기 결과를 사용자에게 보이고, 지울지는 사용자가 `!` 명령으로 정한다.')
        return
    if not args or args[0] in READ:
        return
    sub, rest = args[0], args[1:]
    if sub not in UPLOAD:
        deny(f"하위 명령 '{sub}'을(를) 훅이 모른다 — 글을 올리는지 정할 수 없다. share_guard.py의 목록에 넣거나 사람이 `!`로 실행하라.")
    if '--allow-warnings' in rest:
        deny('--allow-warnings는 지휘자가 붙이지 않는다 — 경고를 사용자에게 보이고, 넘길지는 사용자가 `!` 명령으로 정한다.')
    if (BASH_EXPANDS if shell == BASH else PS_EXPANDS).search(stripped):
        deny('올리는 명령의 따옴표 밖에 셸이 펼치는 글자(낱말 시작의 ~ · bash 글롭 * ? [)가 있다 — 펼친 사용자 경로 · 파일 이름은 '
             '훅이 검사할 수 없다. 글은 따옴표로 감싸라.')
    if sub in DRY_RUN_AWARE and '--dry-run' in rest:  # 봇도 인자 어디에든 --dry-run이 있으면 검사만 한다 (App.cs args.Contains)
        return

    project_dir = os.environ.get('CLAUDE_PROJECT_DIR') or cwd
    hits = find_hits(collect(sub, rest, cwd), project_dir)
    if hits:
        shown = '; '.join(hits[:8]) + (f' 외 {len(hits) - 8}건' if len(hits) > 8 else '')
        deny(f'Slack으로 나갈 글에 막는 패턴이 {len(hits)}건 있다: {shown}. 글에서 빼거나, 사람이 보고 `!` 명령으로 직접 올려라.')


def main() -> None:
    try:
        sys.stderr.reconfigure(encoding='utf-8')
        check(read_input())
    except SystemExit:
        raise
    except Exception as ex:  # 실패하면 막는다
        deny(f'검사 중 오류 ({type(ex).__name__}: {ex}). 검사가 돌지 않았으므로 막는다.')
    sys.exit(0)


if __name__ == '__main__':
    main()
