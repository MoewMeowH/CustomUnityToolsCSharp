"""cleanup_guard · push_guard · run_mode_guard · share_guard 회귀 시험 (계획서 2026-10-19_훅-규칙-강제, /cross-review 10bfc8de41 반영).
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

    expect('H3 계획서 없음 review_main', h3('review_main'), 2, '계획서')
    expect('H3 design_lite는 계획서 검사 안 함', h3('design_lite'), 0)
    plans = os.path.join(tmp, 'A', 'B', 'docs', 'plans')
    os.makedirs(plans)
    old = os.path.join(plans, '2026-01-01_예전.md')
    open(old, 'w', encoding='utf-8').write('x')
    os.utime(old, (0, 0))
    expect('H3 예전 계획서만', h3('review_main'), 2)
    open(os.path.join(plans, f'{datetime.date.today().isoformat()}_오늘.md'), 'w', encoding='utf-8').write('x')
    expect('H3 오늘 계획서 (깊이 4)', h3('review_main'), 0)
    expect('H3 review_high', h3('review_high'), 0)
    expect('H3 예전 이름 review는 검사 안 함 (서버가 거절)', h3('review'), 0)

    good = '## 지적\n- C1 [major] a.cs:1 — 문제\n## 자료 부족\n- 없음\n## 읽은 파일\n- subagent_in/2026-10-19_x/a.cs:1-10\n'
    tag = '[ownReview:self-reviewer] 이유'
    expect('H4 원문 형식 (한글, UTF-8)', h3('review_main', tag, good), 0)
    expect('H4 키 이름 own_review', h3('review_main', tag, good, key='own_review'), 0)
    expect('H4 요약 (읽은 파일 없음)', h3('review_main', tag, '## 지적\n- C1 요약\n(상세는 첫 호출과 같다)'), 2, "'## 읽은 파일'")
    expect('H4 폴더 밖 읽음', h3('review_main', tag, good.replace('subagent_in/2026-10-19_x/a.cs', 'docs/worklog.md')), 2, 'subagent_in/ 밖')
    expect('H4 비슷한 폴더 이름', h3('review_main', tag, good.replace('subagent_in/', 'not_subagent_in/')), 2, 'subagent_in/ 밖')
    expect('H4 .. 로 나감', h3('review_main', tag, good.replace('subagent_in/2026-10-19_x/a.cs', 'subagent_in/../docs/worklog.md')), 2, 'subagent_in/ 밖')
    expect('H4 Windows 절대 경로', h3('review_main', tag, good.replace('subagent_in/2026-10-19_x/a.cs:1-10', 'F:\\AI\\subagent_in\\x\\a.cs:1-44 (첨부 포함)')), 0)
    expect('H4 검색 기록 줄 (경로 없음)', h3('review_main', tag, good + '- Grep: 폴더 전체에서 `Tool` 사용처\n'), 0)
    expect('H4 검색 기록 줄 (폴더 안 경로)', h3('review_main', tag, good + '- Glob: subagent_in/2026-10-19_x/*.cs\n'), 0)
    expect('H4 검색 기록 줄 (폴더 밖 경로)', h3('review_main', tag, good + '- Grep: F:\\AI\\ToolWithCS\\docs 에서 Tool\n'), 2, 'subagent_in/ 밖')
    expect('H4 검색 줄 아닌 자유 문장', h3('review_main', tag, good + '- 폴더 전체를 훑어봄\n'), 2, 'subagent_in/ 밖')
    expect('H4 태그 없으면 형식 검사 안 함', h3('design_lite', '이유', '자유 형식'), 0)
    expect('H4 지적 없음', h3('review_main', tag, '## 읽은 파일\n- subagent_in/x\n'), 2, "'## 지적'")
    expect('H4 perf_main 원문 형식', h3('perf_main', tag, good), 0)
    expect('H4 perf_main 요약 (안내에 /mode)', h3('perf_main', tag, '## 지적\n- C1 요약'), 2, '/mode 3단계')
    # 2026-10-22 '## 비고' 칸 (계획서 2026-10-22_self-reviewer-비고-칸): 읽은 파일 검사는 그대로, 비고는 경로 검사 안 함
    explain = '- 폴더 안에서 Grep으로 StepResult 정의를 찾았으나 없었다.\n'
    expect('H4 비고 칸의 설명 줄', h3('review_main', tag, good + '## 비고\n' + explain), 0)
    expect('H4 비고 칸의 폴더 밖 경로 (검사 대상 아님)', h3('review_main', tag, good + '## 비고\n- docs/worklog.md 는 받지 못함\n'), 0)
    expect('H4 읽은 파일의 설명 줄은 계속 막음 (10-22 실사용 차단 문구)', h3('review_main', tag, good + explain + '## 비고\n- 없음\n'), 2, 'subagent_in/ 밖')
    expect('H4 두 번째 읽은 파일 절의 폴더 밖 경로', h3('review_main', tag, good + '## 비고\n- 없음\n## 읽은 파일\n- docs/worklog.md:1-5\n'), 2, 'subagent_in/ 밖')
finally:
    shutil.rmtree(tmp, ignore_errors=True)

# ── H5 share_guard (계획서 2026-10-23_plan-share-명령과-share_guard) ────
tmp = tempfile.mkdtemp(prefix='hb-shareguard-')
try:
    proj = os.path.join(tmp, 'proj')  # CLAUDE_PROJECT_DIR · 셸 현재 폴더
    bot = os.path.join(tmp, 'SlackPlanBot')  # SLACKPLAN_ROOT
    os.makedirs(os.path.join(proj, '.claude'))
    os.makedirs(os.path.join(proj, 'docs', 'plans'))
    os.makedirs(os.path.join(bot, 'threads', 'C1-2'))
    with open(os.path.join(proj, '.claude', 'push_guard.local.txt'), 'w', encoding='utf-8') as f:
        f.write('person@example\\.com\n')
    with open(os.path.join(proj, '.claude', 'share_guard.local.txt'), 'w', encoding='utf-8') as f:
        f.write('(?i)projectx\nallow: ^F:/ok/\n')
    env = dict(os.environ, CLAUDE_PROJECT_DIR=proj, SLACKPLAN_ROOT=bot)
    SECRET = 'sk-' + 'Q7vR2mK9pL4xN8wT3bZ6'

    def plan(name: str, text: str) -> str:
        with open(os.path.join(proj, 'docs', 'plans', name), 'w', encoding='utf-8') as f:
            f.write(text)
        return f'docs/plans/{name}'

    def h5(args: str, tool: str = 'Bash', cwd: str = proj, extra: dict | None = None) -> tuple[int, str]:
        root = '$env:SLACKPLAN_ROOT' if tool == 'PowerShell' else '$SLACKPLAN_ROOT'
        cmd = f'dotnet run --no-build --project "{root}/src/SlackPlanBot" -- {args}'
        return run('share_guard.py', {'tool_name': tool, 'tool_input': {'command': cmd}, 'cwd': cwd}, dict(env, **(extra or {})))

    def h5raw(cmd: str, tool: str = 'Bash', cwd: str = proj) -> tuple[int, str]:
        return run('share_guard.py', {'tool_name': tool, 'tool_input': {'command': cmd}, 'cwd': cwd}, env)

    clean = plan('clean.md', '# 계획\n- 내용\n')
    expect('H5 깨끗한 계획서 new', h5(f'new {clean} --title "제목"'), 0)
    expect('H5 깨끗한 계획서 new (PowerShell)', h5(f'new {clean}', 'PowerShell'), 0)
    expect('H5 SLACKPLAN_ROOT 경로의 계획서', h5('revise C1-2 "$SLACKPLAN_ROOT/threads/C1-2/../../../proj/' + clean + '"'), 0)
    expect('H5 읽기 명령 find', h5(f'find {clean}'), 0)
    expect('H5 읽기 명령 thread', h5('thread C1-2 --reader 지휘자'), 0)
    expect('H5 읽기 명령 status', h5('status C1-2'), 0)
    expect('H5 slackplan 직접 호출', h5raw(f'slackplan new {clean}'), 0)
    expect('H5 따옴표 안 글 (커밋 메시지)', h5raw('git commit -m "slackplan new 형식 정리"'), 0)
    expect('H5 grep 인자', h5raw('grep -rn slackplan README.md'), 0)
    expect('H5 dotnet test (run 아님)', h5raw('dotnet test F:/AI/SlackPlanBot/tests'), 0)
    expect('H5 heredoc 본문', h5raw("git commit -F - <<'EOF'\nslackplan new x.md --allow-warnings\nEOF"), 0)

    expect('H5 --allow-warnings', h5(f'new {clean} --allow-warnings'), 2, '--allow-warnings')
    expect('H5 --allow-warnings + --dry-run도 막음', h5(f'new {clean} --dry-run --allow-warnings'), 2, '--allow-warnings')
    bad = plan('secret.md', f'키 {SECRET}\n')
    expect('H5 비밀 모양 (값은 가림)', h5(f'new {bad}'), 2, 'sk-Q…')
    expect('H5 비밀 모양이어도 --dry-run은 통과', h5(f'new {bad} --dry-run'), 0)
    fake = plan('fake.md', 'InlineData("sk-abcdefghijklmnopqrstu")\n')
    expect('H5 가짜 값 표식도 막음 (밖으로 나감)', h5(f'new {fake}'), 2, '비밀')
    expect('H5 개인 값 (push_guard.local)', h5(f'new {plan("p.md", "person@example.com")}'), 2, '비밀 · 개인 값')
    expect('H5 프로젝트 패턴 (share_guard.local)', h5(f'new {plan("x.md", "ProjectX 출시")}'), 2, '프로젝트 패턴')
    expect('H5 GoldenSet 언급', h5(f'new {plan("g.md", "GoldenSet/case1 참고")}'), 2, 'GoldenSet/')
    img = plan('i.md', 'docs\\image\\a.png')
    expect('H5 docs/image 언급 (역슬래시)', h5(f'new {img}'), 2, 'docs/image/')
    expect('H5 드라이브 문자 경로', h5(f'new {plan("d.md", "F:/AI/ToolWithCS 에서")}'), 2, '드라이브')
    expect('H5 allow: 예외', h5(f'new {plan("ok.md", "F:/ok/a 에서")}'), 0)
    expect('H5 Git Bash 경로', h5(f'new {plan("b.md", "cd /f/AI/x")}'), 2, 'Git Bash')
    unc = plan('u.md', '\\\\srv\\share')
    expect('H5 UNC 경로', h5(f'new {unc}'), 2, 'UNC')
    expect('H5 URL은 통과', h5(f'new {plan("url.md", "https://docs.unity.com/a/b")}'), 0)
    expect('H5 --title에 비밀', h5(f'new {clean} --title "{SECRET}"'), 2, '--title')
    expect('H5 reply 글', h5(f'reply C1-2 "경로 C:/Users/me"'), 2, 'reply 글')
    expect('H5 ask-consent --text', h5(f'ask-consent C1-2 --text "{SECRET}"'), 2, '--text')
    expect('H5 파일 없음', h5('new docs/plans/none.md'), 2, '읽지 못했다')
    expect('H5 풀 수 없는 변수', h5('new "$HOME/x.md"'), 2, '변수')
    expect('H5 SLACKPLAN_ROOT 없음', h5('new "$SLACKPLAN_ROOT/x.md"', extra={'SLACKPLAN_ROOT': ''}), 2, 'SLACKPLAN_ROOT')
    expect('H5 모르는 옵션', h5(f'new {clean} --file x'), 2, '모른다')
    expect('H5 revise 인자 수', h5(f'revise {clean}'), 2, '인자 수')
    expect('H5 --text 없는 update-summary', h5('update-summary C1-2'), 2, '--text')
    expect('H5 update-summary --text', h5('update-summary C1-2 --text "깨끗한 요약"'), 0)
    # /cross-review 32ab98a8a9 반영
    expect('H5 reply 글의 셸 변수', h5('reply C1-2 "$OPENAI_API_KEY"'), 2, '셸 변수')
    expect('H5 --title의 %변수%', h5(f'new {clean} --title "%USERPROFILE%"'), 2, '셸 변수')
    expect('H5 --text의 PowerShell 변수', h5('ask-consent C1-2 --text "$env:SECRET_X"', 'PowerShell'), 2, '셸 변수')
    expect('H5 따옴표로 감싼 확장자 없는 실행 파일', h5raw(f'"$SLACKPLAN_ROOT/bin/slackplan" new {clean} --allow-warnings'), 2, '--allow-warnings')
    expect("H5 PowerShell & 'slackplan'", h5raw(f"& 'slackplan' new {bad}", 'PowerShell'), 2, '비밀')
    expect('H5 dotnet <dll>', h5raw(f'dotnet "$SLACKPLAN_ROOT/bin/slackplan.dll" new {bad}'), 2, '비밀')
    expect('H5 dotnet exec <dll>', h5raw(f'dotnet exec bin/slackplan.dll new {bad}'), 2, '비밀')
    expect('H5 커밋 메시지 속 slackplan.exe (오탐 아님)', h5raw('git commit -m "bin/Debug의 slackplan.exe 잠금"'), 0)
    expect('H5 파일 이름 (제목 기본값)', h5(f'new {plan("ProjectX-출시.md", "깨끗")}'), 2, '파일 이름')
    os.makedirs(os.path.join(proj, 'GoldenSet'))
    with open(os.path.join(proj, 'GoldenSet', 'a.md'), 'w', encoding='utf-8') as f:
        f.write('깨끗\n')
    expect('H5 무시 폴더 아래 계획서', h5('new GoldenSet/a.md'), 2, 'GoldenSet/')
    expect('H5 PowerShell here-string 인자', h5raw("slackplan reply C1-2 @'\nProjectX\n'@", 'PowerShell'), 2, 'here-string')
    expect('H5 모르는 하위 명령', h5(f'publish {clean}'), 2, '모른다')
    expect('H5 react (이모지만)', h5('react C1-2 eyes'), 0)
    expect('H5 inbox --note (로컬 작업 로그)', h5('inbox done C1-3 --note "처리함"'), 0)
    expect('H5 reply의 --dry-run은 통과 아님 (봇이 글로 올림)', h5(f'reply C1-2 "{SECRET}" --dry-run'), 2, "'--dry-run'")
    expect('H5 옵션 값 자리의 --dry-run (봇도 dry-run)', h5(f'new {bad} --title --dry-run'), 0)
    expect('H5 리다이렉트 2>&1', h5(f'new {clean} 2>&1'), 2, '리다이렉트')
    expect('H5 new --thread · --item', h5(f'new {clean} --thread C1-2 --item C1-3'), 0)
    expect('H5 --thread 값이 ID가 아님', h5(f'new {clean} --thread "a b"'), 2, 'ID 모양')
    expect('H5 슬래시 UNC', h5(f'new {plan("u2.md", "//srv/share/x")}'), 2, 'UNC')
    expect('H5 POSIX 절대 경로', h5(f'new {plan("t.md", "로그 /tmp/x.log")}'), 2, 'POSIX')
    expect('H5 URL 속 /var/ (오탐 아님)', h5(f'new {plan("url2.md", "https://example.org/var/x")}'), 0)
    expect('H5 묶음', h5raw(f'cd {proj} && dotnet run --project "$SLACKPLAN_ROOT/src/SlackPlanBot" -- new {clean}'), 2)
    expect('H5 명령 치환', h5raw('dotnet run --project "$SLACKPLAN_ROOT/src/SlackPlanBot" -- new "$(echo x.md)"'), 2, '명령 치환')
    expect('H5 -- 없음', h5raw(f'dotnet run --project "$SLACKPLAN_ROOT/src/SlackPlanBot" new {clean}'), 2, '`--`')
    bad_abs = os.path.join(proj, bad).replace('\\', '/')
    expect('H5 셸 현재 폴더가 SlackPlanBot', h5raw(f'dotnet run -- new {bad_abs}', cwd=bot), 2, '비밀')
    expect('H5 PowerShell 실행 파일 경로', h5raw(f'& "{bot}\\bin\\slackplan.exe" new {bad}', 'PowerShell'), 2, '비밀')
    expect('H5 cmd /c 형식', h5raw(f'cmd /c slackplan new {clean}'), 2, '해석하지 못했다')
    subprocess.run(['git', 'init', '-q', proj], check=True, capture_output=True)
    subprocess.run(['git', '-C', proj, 'add', '-f', '.claude/share_guard.local.txt'], check=True, capture_output=True)
    expect('H5 share_guard.local.txt 추적됨', h5(f'new {clean}'), 2, '추적')
    expect('H5 깨진 입력은 막음', (lambda r: (r.returncode, r.stderr.decode('utf-8', 'replace')))(subprocess.run([sys.executable, '-I', os.path.join(HERE, 'share_guard.py')], input=b'{', capture_output=True)), 2)
finally:
    shutil.rmtree(tmp, ignore_errors=True)

print('ALL', ok)
sys.exit(0 if ok else 1)
