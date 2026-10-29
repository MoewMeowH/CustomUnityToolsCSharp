"""검토형 서브에이전트에게 줄 저장소 추적 사본을 만든다 (diff + 직접 찾기 — Codex 리뷰어의 ReviewWorkFolder.CopyTracked와 같은 생각).

사용: python -I .claude/scripts/subagent_copy_tracked.py --root <저장소 루트> --out subagent_in/<실행>/repo
        [--path <대상 폴더>]... [--mode auto|code|text] [--diff review_tmp/<날짜>_<주제>.diff]
  → 사본을 만들고 요약을 낸다. 실패(git 없음 · 상한 넘음 · 출력 폴더 문제 · 복사 오류)면 이유를 내고 exit 1 — 그때는 고른 파일로 리뷰한다.

- 복사: `git ls-files`(추적 + intent-to-add)의 지금 작업 트리 내용. --path가 있으면 그 폴더 아래만 (프로젝트가 여럿인 저장소).
  사본 경로는 git 최상위 기준이다 (diff와 같은 기준 — --root가 하위 폴더여도).
- 빼는 것 (독립성 · 지시): 지휘자 지시 · 실수 · 프로젝트 규칙 파일, .claude · .codex · .agents, 리뷰 임시 폴더, 계획서 · 작업 로그, .mcp*.json.
  git 기록(.git)은 애초에 복사하지 않는다 (커밋 메시지에 결론이 들어간다).
- 빼는 것 (안전 · 크기): 비밀 모양 이름(서버 BuiltInDenyPatterns와 같은 목록 + α), 링크 · junction(경로 중간 포함), 2MB 넘는 파일,
  이진(앞 8KB에 NUL), 지워진 파일.
- diff에 든 파일이 위 이유로 빠지면 이름과 이유를 따로 낸다 — 지휘자가 그 파일을 다른 이름으로 따로 복사한다.
- 상한: 2만 개 · 64MB (Codex 사본과 같음). 넘으면 만들지 않는다. 복사 도중 실패하면 만든 것을 지운다 (반쪽 사본을 남기지 않음).
- --mode: code = 코드 파일만(유니티 — 그림 · 씬 · .meta 빼기), text = 글 파일 전부. auto면 대상 범위의 추적 파일에
  ProjectSettings/ProjectVersion.txt가 있으면(하위 폴더의 유니티 프로젝트 포함) code, 아니면 text. diff에 든 파일은 모드와 상관없이 넣는다.
"""
import argparse
import os
import re
import shutil
import stat
import subprocess
import sys

MAX_FILES = 20_000
MAX_TOTAL = 64_000_000
MAX_FILE = 2_000_000

# 독립성 · 지시: 이름 (대소문자 무시)
SKIP_NAMES = {'claude.md', 'claude.local.md', 'mistakes.md', 'project.md', 'agents.md', 'agents.override.md'}
# 독립성 · 지시: 경로 조각 (폴더 이름)
SKIP_DIRS = {'.claude', '.codex', '.agents', '.git', 'review_tmp', 'subagent_in'}
# 계획서 · 작업 로그 (docs/plans/… · docs/worklog/… · docs/worklog.md — 다른 저장소는 추적할 수 있다)
SKIP_DOCS = re.compile(r'(^|/)docs/(plans|worklog)(/|$)|(^|/)docs/worklog\.md$', re.IGNORECASE)
# 비밀 모양 이름 — 서버 OrchestrationOptions.BuiltInDenyPatterns와 같은 목록 + MCP 설정 · 로컬 설정 · 서명 키
SECRET = re.compile(
    r'(^|/)('
    r'\.env|\.env\.[^/]*|[^/]*\.(pem|key|p8|p12|pfx|keystore|jks|mobileprovision|provisionprofile|snk)'
    r'|credentials([._-][^/]*)?|secrets([._-][^/]*)?'  # 대소문자 무시라 secrets*로 두면 SecretStore.cs도 걸린다
    r'|\.mcp[^/]*\.json|appsettings\.local\.json|id_(rsa|ed25519|ecdsa)[^/]*'
    r')$',
    re.IGNORECASE)
# code 모드 확장자 (유니티 · 일반 소스 · 빌드 설정)
CODE_EXT = {
    '.cs', '.asmdef', '.asmref', '.shader', '.hlsl', '.cginc', '.compute', '.shadergraph', '.shadersubgraph',
    '.uss', '.uxml', '.tss', '.rsp', '.jslib', '.jspre',
    '.cpp', '.cc', '.cxx', '.c', '.h', '.hh', '.hpp', '.inl', '.mm', '.m', '.java', '.kt', '.py', '.js', '.ts', '.tsx', '.jsx',
    '.lua', '.csproj', '.sln', '.props', '.targets', '.gradle', '.cmake',
}
CODE_NAMES = {'manifest.json', 'packages-lock.json', 'cmakelists.txt', 'link.xml', 'projectversion.txt'}
UNITY_MARK = 'projectsettings/projectversion.txt'


def git_exe():
    """PATH의 절대 경로 폴더에서 git을 찾는다 — Windows CreateProcess가 현재 폴더의 git.exe를 먼저 실행하지 않게."""
    names = ['git.exe', 'git.cmd'] if os.name == 'nt' else ['git']
    cwd = os.path.normcase(os.path.abspath(os.getcwd()))
    for d in os.environ.get('PATH', '').split(os.pathsep):
        d = d.strip().strip('"')
        if not d or not os.path.isabs(d) or os.path.normcase(os.path.abspath(d)) == cwd:
            continue
        for n in names:
            p = os.path.join(d, n)
            if os.path.isfile(p):
                return p
    raise SystemExit('[실패] PATH에서 git을 찾지 못함')


def run_git(git, root, *args):
    return subprocess.run([git, '-c', 'core.fsmonitor=false', '-C', root, *args], capture_output=True, check=True).stdout


def tracked(git, root, paths):
    """대상 폴더 아래 추적 파일 (git 최상위 기준 경로, '/' 구분)."""
    out = run_git(git, root, 'ls-files', '-z', '--cached', '--full-name', '--', *(paths or ['.']))
    return [p for p in out.decode('utf-8').split('\0') if p]


def unquote_c(s):
    """git이 따옴표로 감싼 경로(core.quotePath)의 C 이스케이프를 푼다 — \\ooo는 UTF-8 바이트."""
    simple = {'n': b'\n', 't': b'\t', '"': b'"', '\\': b'\\', 'a': b'\a', 'b': b'\b', 'f': b'\f', 'r': b'\r', 'v': b'\v'}
    out = bytearray()
    i = 0
    while i < len(s):
        ch = s[i]
        if ch == '\\' and i + 1 < len(s):
            nxt = s[i + 1]
            if re.match(r'[0-7]{3}', s[i + 1:i + 4]):
                out.append(int(s[i + 1:i + 4], 8))
                i += 4
                continue
            out += simple.get(nxt, nxt.encode('utf-8'))
            i += 2
            continue
        out += ch.encode('utf-8')
        i += 1
    return out.decode('utf-8', 'replace')


def diff_paths(diff_file):
    """diff 파일의 바뀐 파일 경로 (+++ 쪽, 지워진 파일 빼기). 따옴표 경로 · b/ · w/ · i/ 같은 접두사 · 접두사 없음(noprefix)을 모두 받는다."""
    if not diff_file:
        return set()
    with open(diff_file, encoding='utf-8', errors='replace') as f:
        text = f.read()
    paths = set()
    for m in re.finditer(r'^\+\+\+ (?:"((?:[^"\\]|\\.)*)"|(.+?))\s*$', text, re.MULTILINE):
        p = unquote_c(m.group(1)) if m.group(1) is not None else m.group(2)
        if p == '/dev/null':
            continue
        paths.add(p)
        if re.match(r'^[a-z]/', p):
            paths.add(p[2:])  # 접두사 b/ · w/ · i/ … (noprefix면 위의 원래 경로가 맞는다)
    return paths


def is_unity(files):
    return any(f.lower() == UNITY_MARK or f.lower().endswith('/' + UNITY_MARK) for f in files)


def skip_reason(rel, mode, in_diff):
    """이름으로 정하는 빼는 이유 (None이면 넣는다). rel은 '/' 구분 상대 경로."""
    parts = rel.split('/')
    if parts[-1].lower() in SKIP_NAMES or any(p.lower() in SKIP_DIRS for p in parts[:-1]) or SKIP_DOCS.search(rel):
        return '독립성'
    if SECRET.search(rel) or ':' in rel:
        return '비밀 · 이름'
    if mode == 'code' and not in_diff:
        name = parts[-1].lower()
        if os.path.splitext(name)[1] not in CODE_EXT and name not in CODE_NAMES:
            return '코드 아님'
    return None


def is_reparse_dir(path):
    """폴더 링크 · junction (Python 3.10 · 3.11에는 os.path.isjunction이 없어 속성으로 본다)."""
    try:
        st = os.lstat(path)
    except OSError:
        return False
    if stat.S_ISLNK(st.st_mode):
        return True
    return bool(getattr(st, 'st_file_attributes', 0) & getattr(stat, 'FILE_ATTRIBUTE_REPARSE_POINT', 0))


def has_link(top, full):
    """최상위(제외) 아래 경로 중간 폴더의 링크 · junction, 또는 파일 자체가 링크인지.
    파일은 islink만 본다 — 클라우드 자리표시 파일도 reparse point라 속성으로 보면 오탐 (H6 훅과 같은 기준)."""
    if os.path.islink(full):
        return True
    top = os.path.normcase(os.path.abspath(top))
    cur = os.path.dirname(os.path.abspath(full))
    while os.path.normcase(cur) != top:
        if is_reparse_dir(cur):
            return True
        parent = os.path.dirname(cur)
        if parent == cur:
            return True  # 최상위 밖
        cur = parent
    return False


def in_scope(rel, scopes):
    return not scopes or any(s == '' or rel == s or rel.startswith(s + '/') for s in scopes)


def copy(root, out, paths, mode, diff_file):
    root = os.path.abspath(root)
    out = os.path.abspath(out)
    if 'subagent_in' not in out.replace('\\', '/').split('/'):
        raise SystemExit('[실패] 출력 폴더는 subagent_in/ 아래여야 합니다: ' + out)
    if os.path.exists(out) and os.listdir(out):
        raise SystemExit('[실패] 출력 폴더가 비어 있지 않습니다: ' + out)
    git = git_exe()
    top = run_git(git, root, 'rev-parse', '--show-toplevel').decode('utf-8').strip()
    prefix = run_git(git, root, 'rev-parse', '--show-prefix').decode('utf-8').strip().strip('/')
    # --path(--root 기준)를 최상위 기준 범위로 — diff에 있는데 사본에 없는 파일을 범위 안에서만 알리려고
    scopes = []
    for p in (paths or ['.']):
        s = os.path.normpath(os.path.join(prefix, p)).replace('\\', '/')
        scopes.append('' if s == '.' else s.strip('/'))
    files = tracked(git, root, paths)
    if mode == 'auto':
        mode = 'code' if is_unity(files) else 'text'
    changed = diff_paths(diff_file)
    if len(files) > MAX_FILES:
        raise SystemExit(f'[실패] 추적 파일 {len(files):,}개 / 상한 {MAX_FILES:,}개 — --path로 대상 폴더를 좁히거나 고른 파일로 리뷰')
    counts = {}
    missing = {}  # diff에 든 파일 중 사본에 없는 것 → 이유
    total = copied = 0
    plan = []
    for rel in files:
        in_diff = rel in changed
        reason = skip_reason(rel, mode, in_diff)
        full = os.path.join(top, *rel.split('/'))
        if reason is None:
            if not os.path.isfile(full):
                reason = '지워짐'
            elif has_link(top, full):
                reason = '링크'
            elif os.path.getsize(full) > MAX_FILE:
                reason = '큼'
            else:
                with open(full, 'rb') as f:
                    if b'\0' in f.read(8192):
                        reason = '이진'
        if reason:
            counts[reason] = counts.get(reason, 0) + 1
            if in_diff and reason != '지워짐':
                missing[rel] = reason
            continue
        total += os.path.getsize(full)
        if total > MAX_TOTAL:
            raise SystemExit(f'[실패] 합계가 상한 {MAX_TOTAL // 1_000_000}MB를 넘음 — --path로 좁히거나 고른 파일로 리뷰')
        plan.append((full, os.path.join(out, *rel.split('/'))))
    tracked_set = set(files)
    for rel in changed:
        if rel not in tracked_set and in_scope(rel, scopes) and os.path.isfile(os.path.join(top, *rel.split('/'))):
            missing[rel] = '추적 안 됨 (git add -N)'

    existed = os.path.exists(out)
    try:
        for src, dst in plan:
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            with open(src, 'rb') as a, open(dst, 'wb') as b:
                b.write(a.read())
            copied += 1
    except BaseException:
        # 반쪽 사본을 남기지 않는다 — 리뷰어는 repo/가 있으면 완전한 사본이라고 믿는다
        if existed:
            for name in os.listdir(out):
                p = os.path.join(out, name)
                if os.path.isdir(p) and not os.path.islink(p):
                    shutil.rmtree(p, ignore_errors=True)
                else:
                    os.remove(p)
        else:
            shutil.rmtree(out, ignore_errors=True)
        raise
    skipped = ' · '.join(f'{k} {v}' for k, v in sorted(counts.items())) or '없음'
    lines = [f'사본 {copied}개 ({total / 1_000_000:.1f}MB, 모드 {mode}) — 뺀 것: {skipped} → {out}']
    if missing:
        lines.append(f'[주의] diff에 든 파일 {len(missing)}개가 사본에 없음 — 리뷰 대상이면 다른 이름으로 따로 복사한다:')
        lines += [f'  {rel} ({why})' for rel, why in sorted(missing.items())]
    return '\n'.join(lines)


def main():
    sys.stdout.reconfigure(encoding='utf-8')
    ap = argparse.ArgumentParser()
    ap.add_argument('--root', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--path', action='append', default=[])
    ap.add_argument('--mode', choices=['auto', 'code', 'text'], default='auto')
    ap.add_argument('--diff')
    a = ap.parse_args()
    try:
        print(copy(a.root, a.out, a.path, a.mode, a.diff))
    except subprocess.CalledProcessError as e:
        raise SystemExit('[실패] git: ' + (e.stderr or b'').decode('utf-8', 'replace').strip())
    except OSError as e:
        raise SystemExit(f'[실패] {type(e).__name__}: {e}')


if __name__ == '__main__':
    main()
