"""cleanup_guard · push_guard · run_mode_guard 회귀 시험 (계획서 2026-10-19_훅-규칙-강제, /cross-review 10bfc8de41 반영).
사용: python -I .claude/scripts/test_hooks.py  → 마지막 줄 'ALL True'면 통과 (exit 0), 아니면 exit 1.
H2는 임시 폴더에 저장소와 bare 원격을 만들어 시험한다 (실제 원격·실제 저장소는 건드리지 않는다).
입력은 UTF-8 바이트로 보낸다 (Claude Code가 보내는 것과 같게 — 한글 깨짐 회귀 시험).
"""
import datetime
import json
import os
import shutil
import subprocess
import sys
import tempfile

sys.stdout.reconfigure(encoding='utf-8')
HERE = os.path.dirname(os.path.abspath(__file__))
ok = True


def run(script: str, payload: dict, env: dict | None = None) -> tuple[int, str]:
    r = subprocess.run([sys.executable, '-I', os.path.join(HERE, script)], input=json.dumps(payload, ensure_ascii=False).encode('utf-8'),
                       capture_output=True, env=env or dict(os.environ))
    return r.returncode, r.stderr.decode('utf-8', 'replace').strip()


def expect(name: str, got: tuple[int, str], want: int, contains: str | None = None) -> None:
    global ok
    good = got[0] == want and (contains is None or contains in got[1])
    ok &= good
    print(('PASS' if good else 'FAIL'), name, got[0], got[1][:140])


# ── H1 cleanup_guard ──────────────────────────────────────────────────
OUT = 'C:\\Users\\x' if os.name == 'nt' else '/home/x'  # 셸 현재 폴더 (대상 밖)


def h1(cmd: str, tool: str = 'Bash', cwd: str = OUT) -> tuple[int, str]:
    return run('cleanup_guard.py', {'tool_name': tool, 'tool_input': {'command': cmd}, 'cwd': cwd})

expect('H1 절대 경로 단독 rm -rf', h1('rm -rf /f/AI/ToolWithCS/subagent_in/x'), 0)
expect('H1 Windows 절대 경로', h1('rm -rf "F:\\AI\\ToolWithCS\\subagent_in\\x"'), 0)
expect('H1 PowerShell 절대 경로', h1('Remove-Item -LiteralPath "F:\\AI\\x" -Recurse -Force -Confirm:$false', 'PowerShell'), 0)
expect('H1 PowerShell -ErrorAction 값은 경로 아님', h1('Remove-Item -LiteralPath "F:\\AI\\x" -Recurse -Force -ErrorAction SilentlyContinue', 'PowerShell'), 0)
expect('H1 PowerShell 쉼표 여러 경로', h1('Remove-Item -LiteralPath "F:\\AI\\a","F:\\AI\\b" -Recurse -Force', 'PowerShell'), 0)
expect('H1 PowerShell 쉼표 경로 중 상대', h1('Remove-Item -Recurse "F:\\AI\\a",b', 'PowerShell'), 2, "'b'")
expect('H1 && 로 묶음', h1('dotnet test && rm -rf /f/AI/x'), 2, '묶여')
expect('H1 cd 뒤', h1('cd /f/AI && rm -rf x'), 2)
expect('H1 상대 경로', h1('rm -rf subagent_in/x'), 2, '절대 경로')
expect('H1 변수', h1('rm -rf "$S/raw"'), 2, '변수')
expect('H1 -fr', h1('rm -fr x'), 2)
expect('H1 --recursive', h1('rm --recursive -f x'), 2)
expect('H1 -- 뒤 경로', h1('rm -rf -- x'), 2, "'x'")
expect('H1 PowerShell ; 로 묶음', h1('Remove-Item -Recurse -Force "F:\\x"; Test-Path "F:\\x"', 'PowerShell'), 2, '묶여')
expect('H1 PowerShell 끝 \\ 경로 따옴표 (\\는 이스케이프 아님)', h1('Set-Location "F:\\AI\\x\\"; Remove-Item -Recurse -Force F:\\AI\\x\\sub', 'PowerShell'), 2)
expect('H1 rmdir /s', h1('rmdir /s /q build'), 2)
expect('H1 rm 단일 파일은 통과', h1('rm -f a.txt && echo ok'), 0)
expect('H1 git rm -r --cached 는 대상 아님', h1('git rm -r --cached GoldenSet'), 0)
expect('H1 경로 붙은 rm', h1('/usr/bin/rm -rf x'), 2, "'x'")
expect('H1 \\rm', h1('\\rm -rf x'), 2)
expect('H1 명령 치환 안', h1('echo "$(rm -rf x)"'), 2, '명령 치환')
expect('H1 백틱 안', h1('echo `rm -rf /f/x`'), 2, '명령 치환')
expect('H1 루트', h1('rm -rf /'), 2, '루트')
expect('H1 드라이브 루트', h1('rm -rf /f/'), 2, '루트')
expect('H1 PowerShell 드라이브 상대 \\x', h1('Remove-Item -Recurse \\AI\\x', 'PowerShell'), 2, '절대 경로')
expect('H1 셸 현재 폴더가 대상 안', h1('rm -rf /f/AI/x', cwd='F:\\AI\\x\\sub'), 2, '현재 폴더')
expect('H1 셸 현재 폴더 = 대상', h1('Remove-Item -Recurse -Force "F:\\AI\\x"', 'PowerShell', cwd='F:\\AI\\x'), 2, '현재 폴더')
expect('H1 셸 현재 폴더가 대상 밖', h1('rm -rf /f/AI/x', cwd='F:\\AI'), 0)
expect('H1 따옴표 안 문자열은 명령 아님', h1('git commit -m "rm -rf 규칙 && 정리"'), 0)
expect('H1 grep -r 은 삭제 아님', h1('grep -rn foo src | head'), 0)
expect('H1 heredoc 본문의 rm -rf 는 명령 아님', h1("cd /f/x && cat >> log.md <<'EOF'\n- `cd … && rm -rf y`가 막히는지\nEOF\necho ok"), 0)
expect('H1 heredoc 뒤의 진짜 rm -rf', h1("cat > a <<'EOF'\nx\nEOF\nrm -rf /f/x"), 2, '묶여')
expect('H1 끝 표시 없는 heredoc 은 본문을 지우지 않음', h1("cat > a <<'EOF'\nrm -rf x"), 2)
expect('H1 따옴표 안 <<EOF 는 heredoc 아님', h1('echo "a<<EOF b"\nrm -rf x'), 2)
expect('H1 here-string <<< 는 heredoc 아님', h1('cat <<< "x"\nrm -rf x'), 2)
expect('H1 PowerShell here-string 본문', h1("git commit -m @'\nrm -rf 정리 규칙\n'@", 'PowerShell'), 0)
expect('H1 다른 도구는 무시', run('cleanup_guard.py', {'tool_name': 'Read', 'tool_input': {}}), 0)
expect('H1 깨진 입력은 막음', (lambda r: (r.returncode, r.stderr.decode('utf-8', 'replace')))(subprocess.run([sys.executable, '-I', os.path.join(HERE, 'cleanup_guard.py')], input=b'{', capture_output=True)), 2)

# ── H2 push_guard ─────────────────────────────────────────────────────
tmp = tempfile.mkdtemp(prefix='hb-pushguard-')
try:
    remote = os.path.join(tmp, 'remote.git')
    private = os.path.join(tmp, 'private.git')
    repo = os.path.join(tmp, 'repo')
    proj = os.path.join(tmp, 'proj')  # CLAUDE_PROJECT_DIR (개인 패턴 파일 위치)
    os.makedirs(os.path.join(proj, '.claude'))
    with open(os.path.join(proj, '.claude', 'push_guard.local.txt'), 'w', encoding='utf-8') as f:
        f.write('# 개인 값\nperson@example\\.com\nallow: ^sk-ALLOWED\n')

    def g(*args: str, cwd: str = repo) -> None:
        subprocess.run(['git', *args], cwd=cwd, check=True, capture_output=True)

    for bare in (remote, private):
        subprocess.run(['git', 'init', '--bare', '-q', bare], check=True, capture_output=True)
    subprocess.run(['git', 'init', '-q', '-b', 'main', repo], check=True, capture_output=True)
    g('config', 'user.email', 't@t'); g('config', 'user.name', 't'); g('config', 'commit.gpgsign', 'false')
    g('remote', 'add', 'origin', remote)
    g('remote', 'add', 'private', private)

    def commit(path: str, text: str) -> None:
        full = os.path.join(repo, path)
        os.makedirs(os.path.dirname(full), exist_ok=True)
        with open(full, 'w', encoding='utf-8') as f:
            f.write(text)
        g('add', '-A'); g('commit', '-q', '-m', 'c')

    env = dict(os.environ, CLAUDE_PROJECT_DIR=proj)

    def h2(cmd: str = 'git push origin HEAD:main', tool: str = 'Bash', extra: dict | None = None) -> tuple[int, str]:
        return run('push_guard.py', {'tool_name': tool, 'tool_input': {'command': cmd}, 'cwd': repo}, dict(env, **(extra or {})))

    SECRET = 'sk-' + 'Q7vR2mK9pL4xN8wT3bZ6'  # 이어 붙여 만든다 — 이 파일 자체가 공개 저장소 푸시 검사(H2)에 걸리지 않게
    commit('a.txt', 'hello\n')
    expect('H2 깨끗한 새 커밋', h2(), 0)
    g('push', '-q', 'origin', 'HEAD:main')
    expect('H2 푸시할 것 없음', h2(), 0)

    commit('b.cs', f'var k = "{SECRET}";\n')
    expect('H2 API 키 모양', h2(), 2, 'sk-Q…')
    expect('H2 원격 생략 (origin 기본)', h2('git push'), 2, '비밀 값')
    expect('H2 PowerShell', h2('git push origin main', 'PowerShell'), 2, '비밀 값')
    expect('H2 commit 과 묶음 (실행 전이라 못 봄)', h2('git add -A && git commit -m x && git push'), 2, '묶여')
    expect('H2 cd 와 함께', h2(f'cd {repo} ; git push'), 2)
    expect('H2 전역 옵션', h2('git --no-pager push origin main'), 2, '전역 옵션')
    expect('H2 --all', h2('git push --all origin'), 2, '--all')
    expect('H2 강제 refspec', h2('git push origin +main'), 2, '강제')
    expect('H2 명령 치환', h2('echo "$(git push)"'), 2, '명령 치환')
    g('reset', '-q', '--hard', 'HEAD~1')

    commit('c.md', '연락: person@example.com\n')
    expect('H2 개인 값 파일 패턴 (-C 경로)', h2('git -C "%s" push' % repo), 2, '비밀 값')
    g('reset', '-q', '--hard', 'HEAD~1')

    commit('Client/docs/image/2026-10-19_x/a.png', 'x')
    expect('H2 docs/image 아래 파일', h2(), 2, 'docs/image/')
    g('reset', '-q', '--hard', 'HEAD~1')

    commit('GoldenSet/골든사례/input.md', 'x')
    expect('H2 GoldenSet 아래 한글 경로 (quotePath)', h2(), 2, 'GoldenSet/')
    g('reset', '-q', '--hard', 'HEAD~1')

    commit('d.txt', f'이 줄은 지워진다 {SECRET}\n')
    commit('d.txt', 'clean\n')
    expect('H2 앞 커밋이 추가한 비밀은 뒤에서 지워도 막음', h2(), 2, '비밀 값')
    g('reset', '-q', '--hard', 'HEAD~2')

    commit('e.cs', 'InlineData("sk-abcdefghijklmnopqrstu")\n')
    expect('H2 시험용 가짜 키는 통과', h2(), 0)
    g('reset', '-q', '--hard', 'HEAD~1')

    commit('e2.cs', f'a("sk-abcdefghijklmnopqrstu") b("{SECRET}")\n')
    expect('H2 같은 줄 가짜 뒤 진짜', h2(), 2, '비밀 값')
    g('reset', '-q', '--hard', 'HEAD~1')

    commit('f.cs', 'k = "' + 'sk-' + 'ALLOWEDq9w8e7r6t5y4u3"\n')
    expect('H2 allow: 예외', h2(), 0)
    g('reset', '-q', '--hard', 'HEAD~1')

    commit('h.md', '++ 로 시작하는 줄 ' + SECRET + '\n'.replace('\n', '') + '\n')
    with open(os.path.join(repo, 'h.md'), 'w', encoding='utf-8') as f:
        f.write('++ ' + SECRET + '\n')
    g('add', '-A'); g('commit', '-q', '--amend', '-m', 'c')
    expect("H2 '++ '로 시작하는 추가 줄 (diff에서 '+++ ')", h2(), 2, '비밀 값')
    g('reset', '-q', '--hard', 'HEAD~1')

    # 다른 브랜치: HEAD는 깨끗하고 feature에만 비밀
    g('checkout', '-q', '-b', 'feature'); commit('g.cs', f'"{SECRET}"\n'); g('checkout', '-q', 'main')
    expect('H2 HEAD 깨끗', h2(), 0)
    expect('H2 다른 브랜치 push', h2('git push origin feature'), 2, '비밀 값')
    # 비공개 원격에만 올린 커밋을 공개 원격으로
    g('push', '-q', 'private', 'feature')
    expect('H2 비공개 원격에 있어도 대상 원격 기준', h2('git push origin feature'), 2, '비밀 값')
    # merge 커밋에만 들어간 줄
    g('checkout', '-q', '-b', 'side', 'main'); commit('m.txt', 'side\n'); g('checkout', '-q', 'main'); commit('m2.txt', 'main\n')
    subprocess.run(['git', 'merge', '-q', '--no-ff', '--no-commit', 'side'], cwd=repo, capture_output=True)
    with open(os.path.join(repo, 'merged.txt'), 'w', encoding='utf-8') as f:
        f.write(SECRET + '\n')
    g('add', '-A'); g('commit', '-q', '-m', 'merge')
    expect('H2 merge 커밋에만 들어간 줄', h2(), 2, '비밀 값')
    expect('H2 커밋 수 상한', h2(extra={'PUSH_GUARD_MAX_COMMITS': '1'}), 2, '상한')
    g('reset', '-q', '--hard', 'origin/main')

    if os.name == 'nt':
        drive, rest = os.path.splitdrive(repo)
        msys = '/' + drive[0].lower() + rest.replace('\\', '/')
        commit('i.cs', f'var k = "{SECRET}";\n')
        expect('H2 Git Bash 경로 (-C /c/…)', h2(f'git -C {msys} push --dry-run origin main'), 2, '비밀 값')
        g('reset', '-q', '--hard', 'HEAD~1')
        expect('H2 Git Bash 경로 깨끗함', h2(f'git -C {msys} push --dry-run origin main'), 0)

    expect('H2 push가 아닌 git 명령', h2('git status'), 0)
    expect('H2 따옴표 안 push', h2('git commit -m "git push 전에 검사"'), 0)
    expect('H2 heredoc 커밋 메시지 속 push', h2("git commit -q -F - <<'EOF'\n푸시 전 git push 검사\nEOF"), 0)
    expect('H2 없는 원격', h2('git push nowhere main'), 2, '원격')
    expect('H2 저장소가 아닌 곳', run('push_guard.py', {'tool_name': 'Bash', 'tool_input': {'command': 'git push'}, 'cwd': tmp}, env), 2, '검사 중 오류')
finally:
    shutil.rmtree(tmp, ignore_errors=True)

# ── H3·H4 run_mode_guard ──────────────────────────────────────────────
tmp = tempfile.mkdtemp(prefix='hb-runguard-')
try:
    env = dict(os.environ, CLAUDE_PROJECT_DIR=tmp)

    def h3(mode: str, reason: str = '이유', own: str = '', key: str = 'ownReview') -> tuple[int, str]:
        return run('run_mode_guard.py', {'tool_name': 'mcp__hb-orchestration__run_mode',
                                         'tool_input': {'mode': mode, 'input': 'x', 'reason': reason, key: own}}, env)

    expect('H3 계획서 없음 review', h3('review'), 2, '계획서')
    expect('H3 critique는 계획서 검사 안 함', h3('critique'), 0)
    plans = os.path.join(tmp, 'A', 'B', 'docs', 'plans')
    os.makedirs(plans)
    old = os.path.join(plans, '2026-01-01_예전.md')
    open(old, 'w', encoding='utf-8').write('x')
    os.utime(old, (0, 0))
    expect('H3 예전 계획서만', h3('review'), 2)
    open(os.path.join(plans, f'{datetime.date.today().isoformat()}_오늘.md'), 'w', encoding='utf-8').write('x')
    expect('H3 오늘 계획서 (깊이 4)', h3('review'), 0)
    expect('H3 panel', h3('panel'), 0)

    good = '## 지적\n- C1 [major] a.cs:1 — 문제\n## 자료 부족\n- 없음\n## 읽은 파일\n- subagent_in/2026-10-19_x/a.cs:1-10\n'
    tag = '[ownReview:self-reviewer] 이유'
    expect('H4 원문 형식 (한글, UTF-8)', h3('review', tag, good), 0)
    expect('H4 키 이름 own_review', h3('review', tag, good, key='own_review'), 0)
    expect('H4 요약 (읽은 파일 없음)', h3('review', tag, '## 지적\n- C1 요약\n(상세는 첫 호출과 같다)'), 2, "'## 읽은 파일'")
    expect('H4 폴더 밖 읽음', h3('review', tag, good.replace('subagent_in/2026-10-19_x/a.cs', 'docs/worklog.md')), 2, 'subagent_in/ 밖')
    expect('H4 비슷한 폴더 이름', h3('review', tag, good.replace('subagent_in/', 'not_subagent_in/')), 2, 'subagent_in/ 밖')
    expect('H4 .. 로 나감', h3('review', tag, good.replace('subagent_in/2026-10-19_x/a.cs', 'subagent_in/../docs/worklog.md')), 2, 'subagent_in/ 밖')
    expect('H4 Windows 절대 경로', h3('review', tag, good.replace('subagent_in/2026-10-19_x/a.cs:1-10', 'F:\\AI\\subagent_in\\x\\a.cs:1-44 (첨부 포함)')), 0)
    expect('H4 검색 기록 줄 (경로 없음)', h3('review', tag, good + '- Grep: 폴더 전체에서 `Tool` 사용처\n'), 0)
    expect('H4 검색 기록 줄 (폴더 안 경로)', h3('review', tag, good + '- Glob: subagent_in/2026-10-19_x/*.cs\n'), 0)
    expect('H4 검색 기록 줄 (폴더 밖 경로)', h3('review', tag, good + '- Grep: F:\\AI\\ToolWithCS\\docs 에서 Tool\n'), 2, 'subagent_in/ 밖')
    expect('H4 검색 줄 아닌 자유 문장', h3('review', tag, good + '- 폴더 전체를 훑어봄\n'), 2, 'subagent_in/ 밖')
    expect('H4 태그 없으면 형식 검사 안 함', h3('critique', '이유', '자유 형식'), 0)
    expect('H4 지적 없음', h3('review', tag, '## 읽은 파일\n- subagent_in/x\n'), 2, "'## 지적'")
finally:
    shutil.rmtree(tmp, ignore_errors=True)

print('ALL', ok)
sys.exit(0 if ok else 1)
