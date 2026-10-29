"""subagent_copy_tracked.py 회귀 시험 — 임시 git 저장소에서 빼기 · 모드 · 상한 · diff 경로 · 링크 · 반쪽 사본을 본다.
사용: python -I .claude/scripts/test_subagent_copy_tracked.py  → 마지막 줄 'ALL True'면 통과 (exit 0), 아니면 exit 1.
"""
import os
import shutil
import subprocess
import sys
import tempfile

sys.stdout.reconfigure(encoding='utf-8')
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import subagent_copy_tracked as sct  # noqa: E402

results = []


def check(name, ok):
    results.append(ok)
    print(('OK  ' if ok else 'FAIL') + ' ' + name)


def git(root, *args):
    # 사용자 전역 설정(서명 · 훅 · 무시 목록)에 흔들리지 않게
    base = ['git', '-c', 'commit.gpgsign=false', '-c', 'core.hooksPath=', '-c', 'user.email=t@t', '-c', 'user.name=t', '-C', root]
    return subprocess.run(base + list(args), check=True, capture_output=True).stdout.decode('utf-8')


def write(root, rel, data=b'x'):
    p = os.path.join(root, *rel.split('/'))
    os.makedirs(os.path.dirname(p), exist_ok=True)
    with open(p, 'wb') as f:
        f.write(data if isinstance(data, bytes) else data.encode('utf-8'))


def fails(fn):
    try:
        fn()
        return False
    except SystemExit:
        return True


def quoted(path):
    """git core.quotePath 모양: 비ASCII 바이트를 \\ooo로."""
    return '"' + ''.join(c if ord(c) < 128 else ''.join('\\%03o' % b for b in c.encode('utf-8')) for c in path) + '"'


tmp = tempfile.mkdtemp(prefix='sct-')
try:
    repo = os.path.join(tmp, 'repo')
    os.makedirs(repo)
    git(repo, 'init', '-q')
    files = {
        'src/A.cs': 'class A {}', 'src/B.txt': 'note', 'src/SecretStore.cs': 'class S {}',
        'CLAUDE.md': 'rules', 'sub/CLAUDE.md': 'rules', 'MISTAKES.md': 'm',
        'PROJECT.md': 'p', 'AGENTS.md': 'a', '.claude/x.md': 'c', '.codex/config.toml': 'c', 'review_tmp/a.diff': 'd',
        'subagent_in/old/a.cs': 'o', 'docs/plans/p.md': 'plan', 'docs/worklog/w.md': 'log', 'docs/worklog.md': 'idx',
        'docs/README.md': 'keep', '.mcp.json': '{}', 'appsettings.local.json': '{}', 'cert.pfx': 'k', '.env': 'K=1',
        'credentials.json': '{}', 'secrets.yaml': 'k', 'Android.keystore': 'k',
        'big.txt': 'y' * (sct.MAX_FILE + 1), 'img.png': b'\x89PNG\x00\x01', 'gone.cs': 'x',
        'lnk/L.cs': 'class L {}',
        # 유니티 (저장소 하위 폴더)
        'Game/Assets/Scripts/P.cs': 'class P {}', 'Game/Assets/Scripts/P.cs.meta': 'guid: 1', 'Game/Assets/Scene.unity': 'yaml',
        'Game/Assets/Hero.prefab': 'yaml', 'Game/Assets/한글.prefab': 'yaml', 'Game/Assets/Tex.png': b'\x89PNG\x00',
        'Game/Assets/S.shader': 'Shader', 'Game/Assets/Game.asmdef': '{}', 'Game/Assets/csc.rsp': '-nullable',
        'Game/Assets/link.xml': '<linker/>', 'Game/Packages/manifest.json': '{}',
        'Game/ProjectSettings/ProjectSettings.asset': 'yaml', 'Game/ProjectSettings/ProjectVersion.txt': 'm_EditorVersion: 6000',
    }
    for rel, data in files.items():
        write(repo, rel, data)
    git(repo, 'add', '-A', '-f')
    git(repo, 'commit', '-q', '-m', 'init')
    listed = set(git(repo, 'ls-files', '-z').split('\0'))
    check('준비: 뺄 파일이 실제로 추적됨', all(r in listed for r in files))
    os.remove(os.path.join(repo, 'gone.cs'))                  # 지워진 추적 파일
    write(repo, 'src/New.cs', 'class N {}')
    git(repo, 'add', '-N', 'src/New.cs')                       # intent-to-add
    write(repo, 'untracked.cs', 'x')                           # 추적 밖

    # 폴더 junction (Windows만): 추적 폴더 lnk/를 밖 폴더로 바꿔 둔다
    junction = False
    if os.name == 'nt':
        outside = os.path.join(tmp, 'outside')
        write(outside, 'L.cs', 'outside')
        shutil.rmtree(os.path.join(repo, 'lnk'))
        junction = subprocess.run(['cmd', '/c', 'mklink', '/J', os.path.join(repo, 'lnk'), outside],
                                  capture_output=True).returncode == 0

    out = os.path.join(tmp, 'subagent_in', 'run', 'repo')
    summary = sct.copy(repo, out, [], 'text', None)
    have = lambda rel: os.path.isfile(os.path.join(out, *rel.split('/')))  # noqa: E731
    check('text: 코드 · 글 복사', have('src/A.cs') and have('src/B.txt') and have('docs/README.md'))
    check('intent-to-add 포함, 추적 밖 · 지워진 파일 없음', have('src/New.cs') and not have('untracked.cs') and not have('gone.cs'))
    check('독립성 파일 뺌', not any(have(r) for r in ['CLAUDE.md', 'sub/CLAUDE.md', 'MISTAKES.md', 'PROJECT.md', 'AGENTS.md',
                                                  '.claude/x.md', '.codex/config.toml', 'review_tmp/a.diff', 'subagent_in/old/a.cs',
                                                  'docs/plans/p.md', 'docs/worklog/w.md', 'docs/worklog.md']))
    check('비밀 모양 뺌', not any(have(r) for r in ['.mcp.json', 'appsettings.local.json', 'cert.pfx', '.env', 'credentials.json',
                                                 'secrets.yaml', 'Android.keystore']))
    check('이름에 secret이 든 코드는 넣음', have('src/SecretStore.cs'))
    check('큰 파일 · 이진 뺌', not have('big.txt') and not have('img.png'))
    check('.git 없음', not os.path.exists(os.path.join(out, '.git')))
    check('요약에 모드 · 뺀 것', '모드 text' in summary and '독립성' in summary)
    if junction:
        check('junction 폴더 아래 파일 뺌', not have('lnk/L.cs') and '링크' in summary)
    else:
        print('SKIP junction (Windows 아님 또는 mklink 실패)')

    # 유니티: 하위 폴더여도 auto → code. --root를 유니티 폴더로 줘도 diff(최상위 기준 · 따옴표 한글 경로)와 맞는다
    diff = os.path.join(tmp, 'review.diff')
    with open(diff, 'w', encoding='utf-8') as f:
        f.write('diff --git a/Game/Assets/Hero.prefab b/Game/Assets/Hero.prefab\n--- a/Game/Assets/Hero.prefab\n'
                '+++ b/Game/Assets/Hero.prefab\n'
                '+++ ' + quoted('b/Game/Assets/한글.prefab') + '\n'
                '+++ b/.claude/x.md\n+++ b/Game/Assets/Tex.png\n+++ /dev/null\n')
    out2 = os.path.join(tmp, 'subagent_in', 'unity', 'repo')
    summary2 = sct.copy(os.path.join(repo, 'Game'), out2, [], 'auto', diff)
    have2 = lambda rel: os.path.isfile(os.path.join(out2, *rel.split('/')))  # noqa: E731
    check('유니티 하위 폴더 auto = code', '모드 code' in summary2)
    check('code: .cs · .shader · .asmdef · manifest · rsp · link.xml · ProjectVersion',
          all(have2(r) for r in ['Game/Assets/Scripts/P.cs', 'Game/Assets/S.shader', 'Game/Assets/Game.asmdef',
                                 'Game/Packages/manifest.json', 'Game/Assets/csc.rsp', 'Game/Assets/link.xml',
                                 'Game/ProjectSettings/ProjectVersion.txt']))
    check('code: .meta · 씬 · 설정 asset 뺌', not any(have2(r) for r in ['Game/Assets/Scripts/P.cs.meta', 'Game/Assets/Scene.unity',
                                                                     'Game/ProjectSettings/ProjectSettings.asset']))
    check('diff에 든 프리팹(따옴표 한글 경로 포함)은 넣음', have2('Game/Assets/Hero.prefab') and have2('Game/Assets/한글.prefab'))
    check('--root 밖(src/) 없음', not have2('src/A.cs'))
    check('diff에 있는데 빠진 파일은 범위 안 것만 이름으로 알림',
          '[주의]' in summary2 and 'Game/Assets/Tex.png (이진)' in summary2 and '.claude/x.md' not in summary2)
    summary3 = sct.copy(repo, os.path.join(tmp, 'subagent_in', 'whole', 'repo'), [], 'text', diff)
    check('범위 안의 .claude 파일이 diff에 있으면 알림', '.claude/x.md (독립성)' in summary3)
    summary4 = sct.copy(repo, os.path.join(tmp, 'subagent_in', 'clean', 'repo'), ['src'], 'text', None)
    check('빠진 diff 파일이 없으면 주의 줄 없음', '[주의]' not in summary4)

    # 출력 폴더 검사
    check('subagent_in 밖 출력 거절', fails(lambda: sct.copy(repo, os.path.join(tmp, 'elsewhere'), [], 'text', None)))
    check('비어 있지 않은 출력 거절', fails(lambda: sct.copy(repo, out, [], 'text', None)))

    # 상한
    old = sct.MAX_FILES
    sct.MAX_FILES = 3
    check('파일 수 상한', fails(lambda: sct.copy(repo, os.path.join(tmp, 'subagent_in', 'cap', 'repo'), [], 'text', None)))
    sct.MAX_FILES = old
    old = sct.MAX_TOTAL
    sct.MAX_TOTAL = 10
    cap2 = os.path.join(tmp, 'subagent_in', 'cap2', 'repo')
    check('합계 상한 · 아무것도 안 만듦', fails(lambda: sct.copy(repo, cap2, [], 'text', None)) and not os.path.exists(cap2))
    sct.MAX_TOTAL = old

    # 복사 도중 실패하면 반쪽 사본을 지운다
    half = os.path.join(tmp, 'subagent_in', 'half', 'repo')
    real = os.makedirs
    calls = []

    def flaky(path, *a, **k):
        calls.append(path)
        if len(calls) > 2:
            raise OSError('simulated')
        return real(path, *a, **k)
    sct.os.makedirs = flaky
    try:
        failed = False
        try:
            sct.copy(repo, half, [], 'text', None)
        except OSError:
            failed = True
    finally:
        sct.os.makedirs = real
    check('복사 도중 실패 → 반쪽 사본 없음', failed and not os.path.exists(half))
finally:
    if os.name == 'nt':
        j = os.path.join(tmp, 'repo', 'lnk')
        if os.path.isdir(j):
            subprocess.run(['cmd', '/c', 'rmdir', j], capture_output=True)  # junction만 지운다 (대상은 그대로)
    shutil.rmtree(tmp, ignore_errors=True)

print('ALL', all(results))
sys.exit(0 if all(results) else 1)
