"""PreToolUse 훅 H2 (Bash · PowerShell의 git push): 공개 저장소로 나가기 전에 비밀 값과 무시해야 할 폴더를 막는다
(계획서 2026-10-19_훅-규칙-강제, 사용자 제약 "공개 저장소에 푸시하기 전에 비밀 값을 검사한다", /cross-review 10bfc8de41 반영).

- 형식 제한: push는 **단독 명령**으로 `git [-C 경로] push [--dry-run|-n] [-u|--set-upstream] [-v|-q] [원격 [소스[:대상]]]`만 받는다.
  다른 명령과 묶음(훅은 실행 전에 돌아 같은 명령의 commit을 못 본다) · cd류 · 명령 치환 · git 전역 옵션 · --all·--tags·강제 푸시 등은
  해석하지 않고 막는다 → "단순 형식으로 따로" 다시 하게 한다.
- 범위: 대상 원격에 없는 커밋 `<소스> --not --remotes=<원격>` (소스 기본 HEAD, 원격 기본 upstream → origin).
  merge 커밋은 첫 부모 기준 diff까지 본다. 경로는 -z(한글 경로가 인용·이스케이프되지 않게).
- 비밀 패턴: BUILT_IN(맞은 값에 FAKE_MARKERS가 있으면 시험용 가짜로 건너뜀) + 개인 값 파일 `$CLAUDE_PROJECT_DIR/.claude/push_guard.local.txt`
  (한 줄에 정규식 하나, #은 주석, 'allow: 정규식'은 예외). 개인 값 파일이 저장소에 추적되면 그 자체로 막는다. 한 줄의 모든 일치를 본다.
- 무시 폴더: 커밋이 docs/image/ · GoldenSet/ · subagent_in/ · review_tmp/ 아래 파일을 추가하면 막는다.
- 커밋이 MAX_COMMITS(기본 300, 환경 변수 PUSH_GUARD_MAX_COMMITS)를 넘으면 막고 사람이 확인하게 한다 (첫 푸시 등, 훅 시간 초과 방지).
- 실패하면 막는다: git 정보를 못 얻으면 exit 2.

입력: stdin JSON {"tool_name": ..., "tool_input": {"command": ...}, "cwd": ...}.
"""
import os
import re
import subprocess
import sys

sys.dont_write_bytecode = True  # 훅 폴더에 __pycache__를 남기지 않는다 (저장소에 섞이지 않게)
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # -I는 스크립트 폴더를 경로에 넣지 않는다
from hook_common import CD, CHAIN, has_substitution, native_path, read_input, scan, shell_of, strip_bodies, tokenize  # noqa: E402

BUILT_IN = [
    r'sk-[A-Za-z0-9_-]{16,}',                 # OpenAI 등
    r'AIza[0-9A-Za-z_-]{20,}',                # Google API 키
    r'gh[pousr]_[A-Za-z0-9]{20,}',            # GitHub 토큰
    r'xox[abpr]-[A-Za-z0-9-]{10,}',           # Slack
    r'-----BEGIN [A-Z ]*PRIVATE KEY-----',
    r'(?i)(?:api[_-]?key|secret|password|token)\s*[:=]\s*["\'][^"\'\s]{12,}["\']',
]
# 시험용 가짜 값 (키 패턴 검출기 테스트 등): 맞은 값에 이 연속 문자열이 있으면 비밀로 보지 않는다 (내장 패턴만).
FAKE_MARKERS = ('abcdefghijklmnop', '1234567890abcdef', 'xxxxxxxxxxxxxxxx', 'example')
IGNORED_DIRS = [('docs', 'image'), ('GoldenSet',), ('subagent_in',), ('review_tmp',)]
LOCAL_FILE = os.path.join('.claude', 'push_guard.local.txt')
ALLOWED_PUSH_OPTIONS = {'--dry-run', '-n', '-u', '--set-upstream', '-v', '--verbose', '-q', '--quiet', '--porcelain'}

ROUGH = re.compile(r'\bgit(?:\.exe)?\b[^\n;&|]*?\bpush\b', re.IGNORECASE)


def deny(reason: str) -> None:
    print(f'[푸시 차단] {reason}', file=sys.stderr)
    sys.exit(2)


SIMPLE = '단순 형식으로 따로 실행하라: `git [-C 경로] push [--dry-run] [-u] [원격 [브랜치]]` (커밋은 앞 호출에서 끝내 둔다).'


def git(repo: str, *args: str) -> str:
    r = subprocess.run(['git', '-C', repo, '-c', 'core.quotePath=false', *args], capture_output=True,
                       text=True, encoding='utf-8', errors='replace')
    if r.returncode != 0:
        raise RuntimeError(f'git {" ".join(args[:2])} 실패: {r.stderr.strip()[:200]}')
    return r.stdout


def load_patterns(project_dir: str) -> tuple[list[re.Pattern], list[re.Pattern], str | None]:
    """(막을 패턴, 예외 패턴, 개인 값 파일 경로). 개인 값 파일의 'allow:'로 시작하는 줄은 예외 패턴(맞은 값에 적용)."""
    pats = [re.compile(p) for p in BUILT_IN]
    allows: list[re.Pattern] = []
    local = os.path.join(project_dir, LOCAL_FILE)
    if not os.path.isfile(local):
        return pats, allows, None
    for line in open(local, encoding='utf-8-sig'):
        line = line.strip()
        if not line or line.startswith('#'):
            continue
        if line.startswith('allow:'):
            allows.append(re.compile(line[len('allow:'):].strip()))
        else:
            pats.append(re.compile(line))
    return pats, allows, local


def is_fake(value: str, allows: list[re.Pattern], builtin: bool) -> bool:
    if any(a.search(value) for a in allows):
        return True
    return builtin and any(m in value.lower() for m in FAKE_MARKERS)


def ignored(path: str) -> str | None:
    parts = path.replace('\\', '/').split('/')[:-1]  # 폴더 부분만
    for rule in IGNORED_DIRS:
        n = len(rule)
        for i in range(len(parts) - n + 1):
            if tuple(parts[i:i + n]) == rule:
                return '/'.join(rule) + '/'
    return None


def mask(s: str) -> str:
    return s[:4] + '…' if len(s) > 4 else '…'


def parse_push(toks: list[str]) -> tuple[str | None, str | None, str | None]:
    """(-C 경로, 원격, 소스). 허용 형식이 아니면 deny."""
    if not toks or re.sub(r'\.exe$', '', toks[0].replace('\\', '/').rsplit('/', 1)[-1].lower()) != 'git':
        deny('git push 앞에 다른 명령이 있다. ' + SIMPLE)
    i, repo = 1, None
    while i < len(toks) and toks[i] != 'push':
        if toks[i] == '-C' and i + 1 < len(toks) and repo is None:
            repo, i = toks[i + 1], i + 2
            continue
        deny(f"git 전역 옵션 '{toks[i]}'은(는) 받지 않는다 (-C 하나만). " + SIMPLE)
    if i >= len(toks):
        deny('push를 찾지 못했다. ' + SIMPLE)
    positional = []
    for t in toks[i + 1:]:
        if t.startswith('-'):
            if t not in ALLOWED_PUSH_OPTIONS:
                deny(f"push 옵션 '{t}'은(는) 받지 않는다 (--all·--tags·--mirror·강제 푸시 등은 검사 범위를 정할 수 없다). " + SIMPLE)
            continue
        positional.append(t)
    if len(positional) > 2:
        deny('refspec은 하나만 받는다. ' + SIMPLE)
    remote = positional[0] if positional else None
    source = None
    if len(positional) == 2:
        spec = positional[1]
        if spec.startswith('+') or spec.startswith(':'):
            deny(f"refspec '{spec}' — 강제 푸시·원격 삭제는 받지 않는다. " + SIMPLE)
        source = spec.split(':', 1)[0]
    return repo, remote, source


def resolve_remote(top: str, remote: str | None) -> str:
    remotes = git(top, 'remote').split()
    if remote is not None:
        if remote not in remotes:
            deny(f"원격 '{remote}'이(가) 없다 (URL 직접 지정은 받지 않는다). 있는 원격: {', '.join(remotes) or '없음'}.")
        return remote
    try:
        upstream = git(top, 'rev-parse', '--abbrev-ref', '--symbolic-full-name', '@{u}').strip()
        if '/' in upstream and upstream.split('/', 1)[0] in remotes:
            return upstream.split('/', 1)[0]
    except RuntimeError:
        pass
    if 'origin' in remotes:
        return 'origin'
    deny('대상 원격을 정하지 못했다 (upstream·origin 없음). 원격 이름을 적어라.')
    return ''


def scan_names(top: str, rng: list[str]) -> None:
    # -z: 경로를 인용·이스케이프하지 않고 NUL로 구분 ("A\0path\0", "R100\0old\0new\0")
    out = git(top, 'log', '-z', '--name-status', '--format=', '--diff-merges=first-parent', *rng)
    fields = [f for f in out.split('\0')]
    i = 0
    while i < len(fields):
        status = fields[i].strip('\n')
        if not status:
            i += 1
            continue
        if status[:1] in 'RC':
            path, i = fields[i + 2] if i + 2 < len(fields) else '', i + 3
        else:
            path, i = fields[i + 1] if i + 1 < len(fields) else '', i + 2
        if status[:1] in 'ACR' and (rule := ignored(path)) is not None:
            deny(f"'{path}' — {rule} 아래 파일은 저장소에 올리지 않는다 (사내 시안·평가 입력·리뷰 사본). .gitignore를 확인하라.")


def scan_lines(top: str, rng: list[str], pats: list[re.Pattern], allows: list[re.Pattern]) -> list[str]:
    patch = git(top, 'log', '-p', '--no-color', '--format=commit %h', '--diff-merges=first-parent', *rng)
    hits, commit, file, in_hunk = [], '?', '?', False
    builtin_count = len(BUILT_IN)
    for line in patch.splitlines():
        # hunk 안의 줄은 ' ' · '+' · '-' · '\'로만 시작한다 — 내용이 '++ '로 시작하는 추가 줄('+++ …')도 머리글로 보지 않는다.
        if in_hunk and line[:1] in (' ', '+', '-', '\\'):
            if line.startswith('+'):
                for k, p in enumerate(pats):
                    for found in p.finditer(line[1:]):  # 한 줄의 모든 일치 (앞의 가짜 값 뒤에 진짜 값이 있어도)
                        if not is_fake(found.group(0), allows, builtin=k < builtin_count):
                            hits.append(f'{commit} {file}: {mask(found.group(0))} (패턴 {p.pattern[:30]})')
            continue
        in_hunk = False
        if line.startswith('commit '):
            commit = line[7:]
        elif line.startswith('+++ '):
            file = line[6:] if line.startswith('+++ b/') else line[4:]
        elif line.startswith('@@'):
            in_hunk = True
    return hits


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
        deny('git push가 명령 치환($( ) · 백틱) 안이나 함께 있다. ' + SIMPLE)
    if CHAIN.search(stripped.strip()):
        deny('git push가 다른 명령과 묶여 있다 — 훅은 명령이 실행되기 전에 돌아서, 같은 명령 안의 commit은 검사하지 못한다. ' + SIMPLE)
    if CD.search(stripped):
        deny('git push와 cd가 함께 있다 (검사할 저장소가 달라진다). -C로 저장소를 지정하라. ' + SIMPLE)

    repo, remote, source = parse_push(tokenize(cmd.strip(), shell))
    cwd = data.get('cwd') or os.getcwd()
    repo = native_path(repo) if repo else cwd
    if not os.path.isabs(repo):
        repo = os.path.join(cwd, repo)
    top = git(repo, 'rev-parse', '--show-toplevel').strip()
    remote = resolve_remote(top, remote)
    source = source or 'HEAD'
    git(top, 'rev-parse', '--verify', '--quiet', f'{source}^{{commit}}')
    rng = [source, '--not', f'--remotes={remote}']

    project_dir = os.environ.get('CLAUDE_PROJECT_DIR') or cwd
    pats, allows, local = load_patterns(project_dir)
    # 개인 값 파일이 이 저장소에 들어 있으면 그 자체가 유출이다.
    if local is not None and os.path.normcase(os.path.realpath(local)).startswith(os.path.normcase(os.path.realpath(top)) + os.sep):
        rel = os.path.relpath(local, top).replace('\\', '/')
        if git(top, 'ls-files', rel).strip():
            deny(f'개인 패턴 파일 {rel}이(가) 저장소에 추적되고 있다. git rm --cached 후 .gitignore에 넣어라.')

    limit = int(os.environ.get('PUSH_GUARD_MAX_COMMITS') or 300)
    count = int(git(top, 'rev-list', '--count', *rng).strip() or 0)
    if count > limit:
        deny(f"'{remote}'에 없는 커밋이 {count}개다 (상한 {limit}) — 첫 푸시 등은 사람이 확인하고 직접 푸시하라 (! 명령).")
    if count == 0:
        return

    scan_names(top, rng)
    hits = scan_lines(top, rng, pats, allows)
    if hits:
        shown = '; '.join(hits[:5]) + (f' 외 {len(hits) - 5}건' if len(hits) > 5 else '')
        deny(f"'{remote}'에 없는 커밋에 비밀 값 모양이 {len(hits)}건 있다: {shown}. 지우고 커밋을 고친 뒤(공개 저장소면 키도 교체) 다시 푸시하라.")


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
