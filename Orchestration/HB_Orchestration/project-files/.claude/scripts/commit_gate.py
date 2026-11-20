"""PreToolUse 훅 H14 (Bash · PowerShell): git commit 때 커밋될 내용으로 그 프로젝트의 시험을 돌린다. 경고 단계 — 실패해도 커밋은 막지 않는다.

- 대상: 명령에 git … commit이 있을 때만 (heredoc · 따옴표 안 글은 빼고 찾는다). 저장소 루트에 commit-gate.json이 없으면 아무것도 안 한다.
- commit-gate.json: {"suites": [{"name", "paths": [저장소 루트 기준 접두어…], "run": [[명령, 인자…], …]}]}.
  커밋될 파일(HEAD와 다른 파일) 중 하나라도 paths로 시작하면 그 스위트의 run을 차례로 돌린다.
- 커밋될 내용은 **임시 인덱스 하나**로 만들고 거기서 바뀐 파일 · 캐시 키(트리 id) · 사본을 모두 뽑는다 (서로 다른 시점을 보지 않게):
  - 기본 · commit -i(--include): 지금 인덱스를 복사. 경로를 준 commit(기본 --only): HEAD를 읽고 그 경로만 더함 (다른 스테이지 변경은 커밋에 안 들어간다).
  - 같은 호출에서 commit 앞에 오는 git add는 임시 인덱스에 그대로 다시 한다 (훅은 명령보다 먼저 돈다). commit -a는 git add -u, 경로는 git add.
    git으로 더하므로 커밋과 같은 줄바꿈 정규화가 걸린다. 경로 · git add는 그 명령이 도는 폴더(-C · cwd) 기준.
  - git rm · mv · reset 등 인덱스를 바꾸는 다른 명령이 commit 앞에 있으면 반영하지 못한다고 함께 알린다.
  - 사본: 임시 인덱스를 git -c core.autocrlf=false checkout-index로 임시 폴더에 (커밋될 바이트 그대로). 여러 세션이 같이 쓰는 작업 트리와 섞이지 않는다.
- 같은 내용은 다시 돌리지 않는다: 임시 인덱스의 트리 id + 스위트 설정이 같고 지난번에 통과했으면 건너뛴다.
- 결과는 additionalContext로 알린다. 실패 · 해석 못 함은 hook_warnings.jsonl에도 남긴다 (경고부터, 뒤에 막기).
- 실패해도(설정 깨짐 · git 오류 · 시간 초과) 커밋을 막지 않는다: 늘 exit 0.

입력: stdin JSON {"tool_name": "Bash"|"PowerShell", "tool_input": {"command": "..."}, "cwd": "..."}.
"""
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time

sys.dont_write_bytecode = True  # 훅 폴더에 __pycache__를 남기지 않는다 (저장소에 섞이지 않게)
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # -I는 스크립트 폴더를 경로에 넣지 않는다
from hook_common import (CHAIN, chain_pieces, join_continuations, log_warning, native_path, notify, read_input, scan,  # noqa: E402
                         shell_of, state_dir, strip_bodies, tokenize)

CONFIG = 'commit-gate.json'
ROUGH = re.compile(r'\bgit(?:\.exe)?\b[^\n;&|]*?\bcommit\b', re.IGNORECASE)
SUITE_TIMEOUT = 600
KEEP_PASSED = 200
TAIL_LINES = 12
GIT_GLOBAL_VALUE = {'-c', '--git-dir', '--work-tree', '--namespace', '--exec-path', '--config-env'}
# commit 옵션 중 값을 받는 것 (짧은 옵션은 묶음의 마지막 글자일 때 다음 낱말이 값: -sm "글")
COMMIT_SHORT_VALUE = set('mFcCt')
COMMIT_LONG_VALUE = {'--message', '--file', '--reuse-message', '--reedit-message', '--author', '--date', '--cleanup', '--template',
                     '--fixup', '--squash', '--trailer', '--gpg-sign'}
INDEX_CHANGERS = {'rm', 'mv', 'reset', 'restore', 'apply', 'stash', 'checkout', 'switch', 'update-index', 'read-tree', 'merge',
                  'cherry-pick', 'revert', 'pull', 'rebase', 'am'}
REDIRECT = re.compile(r'^\d?>>?(&\d)?$|^\d?>&\d$|^\*?>.*$')


def git(repo: str, *args: str, env: dict | None = None, check: bool = True) -> str:
    r = subprocess.run(['git', '-C', repo, *args], capture_output=True, timeout=120, env=env)
    if check and r.returncode != 0:
        raise RuntimeError(f"git {' '.join(args[:3])}: {r.stderr.decode('utf-8', 'replace').strip()[:200]}")
    return r.stdout.decode('utf-8', 'replace')


def git_call(toks: list[str], base: str) -> tuple[str, str, list[str]] | None:
    """(그 git이 도는 폴더, 하위 명령, 하위 명령 인자). git이 아니면 None."""
    if not toks or re.sub(r'(?i)\.exe$', '', toks[0].replace('\\', '/').rsplit('/', 1)[-1]).lower() != 'git':
        return None
    where, i = base, 1
    while i < len(toks):
        t = toks[i]
        if t == '-C' and i + 1 < len(toks):
            where = os.path.join(where, native_path(toks[i + 1]))
            i += 2
        elif t in GIT_GLOBAL_VALUE and i + 1 < len(toks):
            i += 2
        elif t.startswith('-'):
            i += 1
        else:
            return where, t.lower(), [a for a in toks[i + 1:] if not REDIRECT.match(a)]
    return None


def commit_options(args: list[str]) -> dict:
    """{'all', 'include', 'paths', 'unknown'} — 경로는 원문 토큰(따옴표를 벗긴 값)에서."""
    opt = {'all': False, 'include': False, 'paths': [], 'unknown': False}
    skip = dashdash = False
    for t in args:
        if skip:
            skip = False
        elif dashdash:
            opt['paths'].append(t)
        elif t == '--':
            dashdash = True
        elif t.startswith('--'):
            key = t.split('=', 1)[0]
            if key == '--all':
                opt['all'] = True
            elif key == '--include':
                opt['include'] = True
            elif key == '--pathspec-from-file':
                opt['unknown'] = True
            elif '=' not in t and key in COMMIT_LONG_VALUE:
                skip = True
        elif t.startswith('-') and len(t) > 1:
            for k, ch in enumerate(t[1:]):
                if ch == 'a':
                    opt['all'] = True
                elif ch == 'i':
                    opt['include'] = True
                elif ch in COMMIT_SHORT_VALUE:
                    skip = k == len(t) - 2  # 마지막 글자면 다음 낱말이 값, 아니면 붙은 값 (-mtitle)
                    break
        else:
            opt['paths'].append(t)
    return opt


def plan(cmd: str, shell: str, base: str) -> tuple[dict, list[tuple[str, list[str]]], list[str]] | None | str:
    """(commit 정보, commit 앞 git add들 [(폴더, 인자)], 반영 못 하는 앞 명령들). commit이 없으면 None, 해석 못 하면 이유 글."""
    stripped, inner = scan(cmd, shell)
    if not ROUGH.search(stripped + '\n' + '\n'.join(inner)):
        return None
    pieces = chain_pieces(cmd, stripped, shell) if CHAIN.search(stripped.strip()) else [(tokenize(stripped.strip(), shell), tokenize(cmd.strip(), shell))]
    if pieces is None:
        return '명령 조각을 해석하지 못했다 (따옴표 · 치환이 섞인 묶음)'
    adds: list[tuple[str, list[str]]] = []
    unknown: list[str] = []
    for _, raw in pieces:
        call = git_call(raw, base)
        if call is None:
            continue
        where, sub, args = call
        if sub == 'commit':
            opt = commit_options(args)
            if opt['unknown']:
                return '--pathspec-from-file은 게이트가 따라가지 못한다'
            opt['dir'] = where
            return opt, adds, unknown
        if sub == 'add':
            adds.append((where, args))
        elif sub in INDEX_CHANGERS:
            unknown.append(f'git {sub}')
    return None  # commit 낱말은 있지만 git commit 명령은 아님 (git log --grep commit 등)


def build(top: str, opt: dict, adds: list[tuple[str, list[str]]], work: str) -> tuple[str, list[str], str]:
    """임시 인덱스를 만들어 (트리 id, HEAD와 다른 파일, 사본 폴더)."""
    index = os.path.join(work, 'index')
    env = dict(os.environ, GIT_INDEX_FILE=index)
    has_head = subprocess.run(['git', '-C', top, 'rev-parse', '--verify', '-q', 'HEAD'], capture_output=True).returncode == 0
    if opt['paths'] and not opt['include']:
        git(top, 'read-tree', 'HEAD' if has_head else '--empty', env=env)
    else:
        real = git(top, 'rev-parse', '--git-path', 'index').strip()
        real = real if os.path.isabs(real) else os.path.join(top, real)
        if os.path.isfile(real):
            shutil.copyfile(real, index)
    for where, args in adds:
        git(where, 'add', *args, env=env)
    if opt['all']:
        git(top, 'add', '-u', env=env)
    if opt['paths']:
        git(opt['dir'], 'add', '--', *opt['paths'], env=env)
    tree = git(top, 'write-tree', env=env).strip()
    if has_head:
        changed = git(top, '-c', 'core.quotepath=off', 'diff', '--cached', '--name-only', 'HEAD', env=env).splitlines()
    else:
        changed = git(top, '-c', 'core.quotepath=off', 'ls-files', env=env).splitlines()
    snap = os.path.join(work, 'snap')
    git(top, '-c', 'core.autocrlf=false', 'checkout-index', '-a', f'--prefix={snap.replace(os.sep, "/")}/', env=env)
    return tree, [c for c in changed if c], snap


def passed_path() -> str:
    return os.path.join(state_dir(), 'commit_gate_passed.json')


def load_passed() -> list[str]:
    try:
        with open(passed_path(), encoding='utf-8') as f:
            data = json.load(f)
        return data if isinstance(data, list) else []
    except (OSError, ValueError):
        return []


def save_passed(keys: list[str]) -> None:
    os.makedirs(state_dir(), exist_ok=True)
    with open(passed_path(), 'w', encoding='utf-8') as f:
        json.dump(keys[-KEEP_PASSED:], f)


def run_suite(suite: dict, root: str) -> tuple[bool, str]:
    """(통과, 실패면 마지막 줄들)."""
    for cmd in suite.get('run') or []:
        try:
            r = subprocess.run(cmd, cwd=root, capture_output=True, timeout=SUITE_TIMEOUT)
        except subprocess.TimeoutExpired:
            return False, f"{' '.join(cmd)}: {SUITE_TIMEOUT}초 넘음"
        except OSError as ex:
            return False, f"{' '.join(cmd)}: 실행 못 함 ({ex})"
        if r.returncode != 0:
            lines = [l for l in (r.stdout + r.stderr).decode('utf-8', 'replace').splitlines() if l.strip()]
            pick = [l for l in lines if re.search(r'FAIL|실패|error|Error|오류|Assert', l)] or lines
            return False, f"{' '.join(cmd)} → exit {r.returncode}\n" + '\n'.join(pick[-TAIL_LINES:])
    return True, ''


def check(data: dict) -> None:
    if data.get('tool_name') not in ('Bash', 'PowerShell'):
        return
    shell = shell_of(data['tool_name'])
    cmd = strip_bodies(join_continuations((data.get('tool_input') or {}).get('command') or '', shell), shell)
    base = data.get('cwd') or os.getcwd()
    if shell != 'ps':
        base = native_path(base.replace('\\', '/'))
    found = plan(cmd, shell, base)
    if found is None:
        return
    try:
        top = git(base if isinstance(found, str) else found[0]['dir'], 'rev-parse', '--show-toplevel').strip()
    except Exception:
        return
    if not os.path.isfile(os.path.join(top, CONFIG)):
        return
    if isinstance(found, str):
        detail = f'git commit을 찾았지만 게이트가 시험하지 못했다: {found} — git add · git commit을 따로 단순하게 부르면 시험이 돈다.'
        log_warning('H14', 'commit-gate-unparsed', detail)
        notify('PreToolUse', f'[H14 커밋 게이트 경고] {detail}')
        return
    opt, adds, unknown = found
    with open(os.path.join(top, CONFIG), encoding='utf-8-sig') as f:
        suites = json.load(f).get('suites') or []

    work = tempfile.mkdtemp(prefix='hb-commit-gate-')
    try:
        tree, files, snap = build(top, opt, adds, work)
        picked = [s for s in suites if any(f.startswith(tuple(s.get('paths') or ())) for f in files)]
        if not picked:
            return
        note = f" (같은 호출의 {', '.join(unknown)}은 반영하지 못했다 — 따로 부르면 정확하다)" if unknown else ''
        keys = {s['name']: hashlib.sha256((tree + '|' + json.dumps(s, sort_keys=True)).encode('utf-8')).hexdigest()[:24] for s in picked}
        passed = load_passed()
        todo = [s for s in picked if keys[s['name']] not in passed]
        if not todo:
            notify('PreToolUse', f"[H14 커밋 게이트] 같은 내용으로 이미 통과: {', '.join(s['name'] for s in picked)}{note}")
            return
        started = time.time()
        results = [(s, *run_suite(s, snap)) for s in todo]
    finally:
        shutil.rmtree(work, ignore_errors=True)
    ok = [s['name'] for s, good, _ in results if good]
    bad = [(s['name'], tail) for s, good, tail in results if not good]
    save_passed(passed + [keys[n] for n in ok])
    took = f'{time.time() - started:.0f}초'
    if bad:
        detail = (f"커밋될 내용으로 돌린 시험이 실패했다 ({took}): " + ' / '.join(f'{n}: {t}' for n, t in bad)
                  + (f" · 통과: {', '.join(ok)}" if ok else '') + note
                  + ' — 고친 뒤 다시 커밋하거나, 실패가 이 커밋과 무관하면 사용자에게 알린다.')
        log_warning('H14', 'commit-gate-fail', detail)
        notify('PreToolUse', f'[H14 커밋 게이트 경고] {detail}')
    else:
        notify('PreToolUse', f"[H14 커밋 게이트] 커밋될 내용으로 시험 통과 ({took}): {', '.join(ok)}{note}")


def main() -> None:
    try:
        check(read_input())
    except Exception as ex:  # 경고 단계 — 막지 않는다
        try:
            log_warning('H14', 'commit-gate-error', f'{type(ex).__name__}: {ex}')
        except Exception:
            pass
    sys.exit(0)


if __name__ == '__main__':
    main()
