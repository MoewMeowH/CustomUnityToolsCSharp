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
# 본문에 재귀 삭제 글자가 있으면 묶음의 모든 조각이 데이터 전용이어야 한다 (H5와 같은 규칙, 계획서 2026-10-23_H1-H2-heredoc-본문과-rm-옵션)
expect('H1 heredoc 본문의 rm -rf + cd · echo 묶음은 막힘', h1("cd /f/x && cat >> log.md <<'EOF'\n- `cd … && rm -rf y`가 막히는지\nEOF\necho ok"), 2, '데이터 전용')
expect('H1 heredoc 본문의 rm -rf 는 명령 아님 (cat 단독)', h1("cat >> /f/x/log.md <<'EOF'\n- `cd … && rm -rf y`가 막히는지\nEOF"), 0)
expect('H1 heredoc 뒤의 진짜 rm -rf', h1("cat > a <<'EOF'\nx\nEOF\nrm -rf /f/x"), 2, '묶여')
expect('H1 끝 표시 없는 heredoc 은 본문을 지우지 않음', h1("cat > a <<'EOF'\nrm -rf x"), 2)
expect('H1 따옴표 안 <<EOF 는 heredoc 아님', h1('echo "a<<EOF b"\nrm -rf x'), 2)
expect('H1 here-string <<< 는 heredoc 아님', h1('cat <<< "x"\nrm -rf x'), 2)
expect('H1 PowerShell here-string 본문', h1("git commit -m @'\nrm -rf 정리 규칙\n'@", 'PowerShell'), 0)
# 따옴표 짝 · 주석 · heredoc 경계 (계획서 2026-10-23_H5-셸-문법-우회 — 이전 판 실측 exit 0)
expect('H1 주석 속 따옴표로 숨긴 rm', h1("ls # '\nrm -rf build\n'"), 2, '묶여')
expect("H1 $'…'로 숨긴 rm", h1("echo $'\\'' ; rm -rf build ; 'z'"), 2, '묶여')
# 본문 검사가 먼저 막는다 (본문의 $( )는 실행되므로 data_only도 아님) — 이전에는 '명령 치환'으로 막힘
expect('H1 따옴표 없는 heredoc 속 $()', h1('cat <<EOF\n$(rm -rf build)\nEOF'), 2, '[정리 명령 차단]')
expect('H1 PowerShell 굽은 따옴표로 숨긴 삭제', h1("echo 'a’; Remove-Item -Recurse -Force x; ‘'", 'PowerShell'), 2)
expect('H1 $#는 주석 아님', h1('echo $#; rm -rf x'), 2, '묶여')
expect('H1 ${#a}는 주석 아님', h1('echo ${#a}; rm -rf x'), 2, '묶여')
expect('H1 커밋 "$(cat heredoc)" 본문의 rm -rf 는 명령 아님', h1("git commit -m \"$(cat <<'EOF'\nrm -rf 정리 (don't)\nEOF\n)\""), 0)
# /cross-review 8722c947f7 반영 (bash · PowerShell 실측과 같게)
for name, cmd, tool in [
    ('$( ) 뒤 #는 낱말의 일부 (bash)', 'echo $(true)#; rm -rf x', 'Bash'),
    ('<<<의 둘째 <는 heredoc 아님', 'cat <<<true\nrm -rf build\ntrue', 'Bash'),
    ('이스케이프 공백 뒤 #는 낱말 가운데', 'echo \\ #; rm -rf build', 'Bash'),
    ('끝 표시 뒤 공백 줄은 끝 아님', "cat <<'EOF'\nEOF \n'\nEOF\nrm -rf build\n# '\n", 'Bash'),
    ("끝 표시 이어 붙임 <<E'OF'", "cat <<E'OF'\nEOF\nrm -rf x\nE", 'Bash'),
    ('> 뒤 #는 주석', "echo hi >#'\nrm -rf build\n'", 'Bash'),
    ('산술 << 는 heredoc 아님', 'y=1; echo $(( 1<<y ))\nrm -rf x\ny', 'Bash'),
    ('큰따옴표 $( ) 속 case', 'echo "$(case x in x) rm -rf y;; esac)"', 'Bash'),
    ('PS { 뒤 #는 주석', "& {#'\n}\nRemove-Item -Recurse -Force x\n'", 'PowerShell'),
    ("PS ${a'b} 속 따옴표", "${a'b} = 1\nRemove-Item -Recurse -Force x\n'", 'PowerShell'),
    ('PS --% 뒤는 글자 그대로', "cmd /c echo --% a'\nRemove-Item -Recurse -Force x\n'", 'PowerShell'),
]:
    expect(f'H1 셸 문법 {name}', h1(cmd, tool), 2, '[정리 명령 차단]')
# /self-review security 반영분 재검토 (실측 exit 0이던 형식)
for name, cmd, tool in [
    ('공백 없는 & 뒤 rm', 'ls&rm -rf build', 'Bash'),
    ('${…} 속 산술 <<', 'y=1; echo ${a:0:1<<y}\nrm -rf x\ny}', 'Bash'),
    ('$[…] 속 <<', 'y=1; echo $[1<<y]\nrm -rf x\ny]', 'Bash'),
    ("${…} 속 따옴표 } (${a:-'}'})", "echo ${a:-'}'}\nrm -rf x\n'", 'Bash'),
    ('PS a=#b 뒤 ;', 'echo a=#b; Remove-Item -Recurse -Force x', 'PowerShell'),
    ('PS 낱말 가운데 <#', 'echo a<#c; Remove-Item -Recurse -Force x #>b', 'PowerShell'),
    ('PS ${a`}b} 속 따옴표', "${a`}b'} = 1\nRemove-Item -Recurse -Force x\n'", 'PowerShell'),
    ('끝 표시 없는 heredoc 뒤 따옴표', "cat <<'EOF'\n'\nrm -rf x", 'Bash'),
]:
    expect(f'H1 재검토 {name}', h1(cmd, tool), 2, '[정리 명령 차단]')
expect('H1 리다이렉트 2>&1 은 묶음 아님', h1('rm -rf /f/AI/ToolWithCS/subagent_in/x 2>&1'), 0)
# heredoc 본문 · 재귀 삭제 옵션 형식 (계획서 2026-10-23_H1-H2-heredoc-본문과-rm-옵션, 실측: 모두 재귀 삭제)
for name, cmd, tool in [
    ('heredoc을 bash에', "bash <<'EOF'\nrm -rf build\nEOF", 'Bash'),
    ('heredoc을 python에', "python - <<'EOF'\nimport os; os.system('rm -rf build')\nEOF", 'Bash'),
    ('here-string을 iex에', "@'\nRemove-Item -Recurse -Force x\n'@ | iex", 'PowerShell'),
    ('rm --rec 상대 경로', 'rm --rec -f build', 'Bash'),
    ('rm --r 상대 경로', 'rm --r build', 'Bash'),
    ('-Recurse:$true 상대 경로', 'Remove-Item build -Recurse:$true -Force', 'PowerShell'),
    ('en dash –Recurse', 'Remove-Item build –Recurse -Force', 'PowerShell'),
    ('em dash —Recurse', 'Remove-Item build —Recurse -Force', 'PowerShell'),
    ('horizontal bar ―r', 'rm build ―r', 'PowerShell'),
]:
    expect(f'H1 형식 {name}', h1(cmd, tool), 2, '[정리 명령 차단]')
expect('H1 rm --rec 절대 경로 단독은 통과', h1('rm --rec -f /f/AI/ToolWithCS/subagent_in/x'), 0)
expect('H1 -Recurse:$false는 재귀 아님', h1('Remove-Item build -Recurse:$false -Force', 'PowerShell'), 0)
expect('H1 커밋 heredoc 본문의 rm -rf 문구', h1("git commit -F - <<'EOF'\n정리: rm -rf build 대신 Remove-Item\nEOF"), 0)
expect('H1 cat > 파일 heredoc 본문의 rm -rf', h1("cat > notes.md <<'EOF'\nrm -rf build\nEOF"), 0)
expect('H1 rm --force는 재귀 아님', h1('rm --force build'), 0)
# /self-review security 625b223591 반영 (실측 exit 0이던 형식)
for name, cmd, tool in [
    ('따옴표 없는 heredoc을 bash에', 'bash <<EOF\nrm -rf build\nEOF', 'Bash'),
    ('@"…"@ 를 iex에', '@"\nRemove-Item build -Recurse -Force\n"@ | iex', 'PowerShell'),
    ('$env: 대입 + git commit', "$env:GIT_EDITOR = @'\nrm -rf build\n'@\ngit commit", 'PowerShell'),
    ('훅 파일 쓰기 + git commit', "cat > .git/hooks/pre-commit <<'EOF'\nrm -rf build\nEOF\ngit commit -m x", 'Bash'),
    ('bash 줄 이음 뒤 -rf', 'rm \\\n -rf build', 'Bash'),
    ('PS 백틱 줄 이음 뒤 -Recurse', 'Remove-Item build `\n -Recurse -Force', 'PowerShell'),
    ('PS splatting', 'Remove-Item @p', 'PowerShell'),
]:
    expect(f'H1 재검토2 {name}', h1(cmd, tool), 2, '[정리 명령 차단]')
expect('H1 en dash 폴더 · 셸 현재 폴더가 그 안', h1('Remove-Item -LiteralPath "F:\\a\u2013b" -Recurse -Force', 'PowerShell', cwd='F:\\a\u2013b\\sub'), 2, '현재 폴더')
expect('H1 en dash 폴더 · 밖에서 지움', h1('Remove-Item -LiteralPath "F:\\a\u2013b" -Recurse -Force', 'PowerShell', cwd='F:\\AI'), 0)
expect('H1 cat <<EOF | git commit -F - (파일 안 씀)', h1("cat <<'EOF' | git commit -F -\nrm -rf 정리\nEOF"), 0)
expect('H1 bash 줄 이음 절대 경로 단독', h1('rm \\\n -rf /f/AI/ToolWithCS/subagent_in/x'), 0)
# 따옴표 문자열로 넘긴 명령 — 허용 목록 (계획서 2026-10-23_따옴표-명령-허용목록과-perf-ownReview)
for name, cmd, tool in [
    ('bash -c', 'bash -c "rm -rf build"', 'Bash'),
    ('powershell -Command', 'powershell -Command "Remove-Item build -Recurse -Force"', 'Bash'),
    ('iex', "iex 'Remove-Item build -Recurse -Force'", 'PowerShell'),
    ('cmd /c', 'cmd /c "rmdir /s /q build"', 'PowerShell'),
    ('eval', "eval 'rm -rf build'", 'Bash'),
    ('python -c', "python -c \"import os; os.system('rm -rf build')\"", 'Bash'),
    ('tclsh (목록 밖 실행기)', 'tclsh -c "rm -rf build"', 'Bash'),
    ('echo | bash', 'echo "rm -rf build" | bash', 'Bash'),
    ('rg --pre', 'rg --pre sh "rm -rf build" .', 'Bash'),
]:
    expect(f'H1 따옴표 명령 {name}', h1(cmd, tool), 2, '따옴표 문자열')
for name, cmd, tool in [
    ('git commit -m', 'git commit -m "정리: rm -rf build 대신 Remove-Item"', 'Bash'),
    ('grep', 'grep -rn "rm -rf" docs', 'Bash'),
    ('Select-String', "Select-String -Path README.md -Pattern 'Remove-Item x -Recurse'", 'PowerShell'),
    ('echo', 'echo "rm -rf 은 쓰지 않는다"', 'Bash'),
    ('git log --grep', 'git log --grep "rm -rf"', 'Bash'),
]:
    expect(f'H1 따옴표 글자 통과 {name}', h1(cmd, tool), 0)
# /self-review security ddb47044d1 반영 (실측 exit 0이던 형식)
for name, cmd, tool, why in [
    ('겹친 따옴표 -Command', 'powershell -Command "Remove-Item "$p" -Recurse -Force"', 'Bash', '따옴표 문자열'),
    ('cmd /c rmdir 따옴표 없음', 'cmd /c rmdir /s /q build', 'Bash', '해석하지 못했다'),
    ('-Command 따옴표 없음', 'powershell -Command Remove-Item build -Recurse -Force', 'Bash', '해석하지 못했다'),
    ('env rm', 'env rm -rf build', 'Bash', '해석하지 못했다'),
    ('timeout rm', 'timeout 5 rm -rf build', 'Bash', '해석하지 못했다'),
    ('git grep -iO', 'git grep -iO"rm -rf build" pattern', 'Bash', '따옴표 문자열'),
    ('git log --output 훅 + commit', 'git log -1 --format="rm -rf build" --output=.git/hooks/pre-commit; git commit -m x', 'Bash', '따옴표 문자열'),
    ('echo >> config + git status', 'echo "fsmonitor = rm -rf build" >> .git/config; git status', 'Bash', '따옴표 문자열'),
    ('쪼갠 이름 r""m', "bash -c 'r\"\"m -rf build'", 'Bash', '따옴표 문자열'),
    ('백틱으로 쪼갠 Remove-It`em', "iex 'Remove-It`em x -Recurse'", 'PowerShell', '따옴표 문자열'),
]:
    expect(f'H1 재검토3 {name}', h1(cmd, tool), 2, why)
expect('H1 git log 단독 (쓰기 없음)', h1('git log --grep "rm -rf"'), 0)
expect('H1 git -C 경로 commit heredoc 본문의 rm', h1("git -C /f/x commit -q -F - <<'EOF'\ncmd /c rmdir /s 막음\nEOF"), 0)
expect('H1 git -c 설정 + commit heredoc은 막힘', h1("git -c core.hooksPath=x commit -F - <<'EOF'\nrm -rf build\nEOF"), 2, '데이터 전용')
expect('H1 줄 이음 뒤 #는 앞 상태 (주석)', h1('echo a \\\n#c; rm -rf x'), 0)
expect('H1 PS $( ) 뒤 #는 주석', h1('echo $(1)#; Remove-Item -Recurse -Force x', 'PowerShell'), 0)
expect("H1 끝 표시 이어 붙임 본문은 데이터 (<<'E'OF)", h1("cat <<'E'OF\nE\nrm -rf build\nEOF\n"), 0)
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
    expect('H2 주석 속 따옴표로 숨긴 push (이전 판 exit 0)', h2("ls # '\ngit push origin main && echo\n'"), 2, '묶여')
    expect('H2 heredoc을 bash에', h2("bash <<'EOF'\ngit push origin main\nEOF"), 2, '데이터 전용')
    expect('H2 here-string을 iex에', h2("@'\ngit push origin main\n'@ | iex", 'PowerShell'), 2, '데이터 전용')
    expect('H2 커밋 heredoc 본문의 git push 문구', h2("git commit -q -F - <<'EOF'\n다음에 git push 할 것\nEOF"), 0)
    expect('H2 따옴표 없는 heredoc을 bash에', h2('bash <<EOF\ngit push origin main\nEOF'), 2, '데이터 전용')
    expect('H2 줄 이음 뒤 push', h2('git \\\n push origin main && echo'), 2, '묶여')
    expect('H2 bash -c 속 push', h2('bash -c "git push origin main"'), 2, '따옴표 문자열')
    expect('H2 iex 속 push', h2("iex 'git push origin main'", 'PowerShell'), 2, '따옴표 문자열')
    expect('H2 grep 속 push 글자', h2('grep -rn "git push" docs'), 0)
    expect('H2 겹친 따옴표 bash -c', h2('bash -c "git -C "$R" push"'), 2, '따옴표 문자열')
    expect('H2 쪼갠 이름 gi""t push', h2("bash -c 'gi\"\"t push origin main'"), 2, '따옴표 문자열')
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
    # 검토형 서브에이전트 추가 (계획서 2026-10-23_서브에이전트-종류-추가)
    expect('H4 perf-reviewer 태그', h3('perf_main', '[ownReview:perf-reviewer] 이유', good), 0)
    expect('H4 perf-reviewer 요약은 막음', h3('perf_main', '[ownReview:perf-reviewer] 이유', '## 지적\n- C1 요약'), 2, 'perf-reviewer 결과 원문이 아니다')
    both = good + '\n' + good
    expect('H4 두 서브에이전트 이어 붙임', h3('review_main', '[ownReview:self-reviewer+security-reviewer] 이유', both), 0)
    expect('H4 두 서브에이전트인데 결과 하나', h3('review_main', '[ownReview:self-reviewer+security-reviewer] 이유', good), 2, "'## 지적' 절이 1개")
    expect('H4 두 번째 결과의 폴더 밖 경로', h3('review_main', '[ownReview:self-reviewer+security-reviewer] 이유',
                                          good + '\n' + good.replace('subagent_in/2026-10-19_x/a.cs', 'docs/worklog.md')), 2, 'subagent_in/ 밖')
    expect('H4 모르는 서브에이전트', h3('review_main', '[ownReview:web-searcher] 이유', good), 2, '모른다')
    expect('H4 겹친 이름', h3('review_main', '[ownReview:self-reviewer+self-reviewer] 이유', both), 2, '두 번')
    # /self-review fffbc4440a 반영: 블록마다 원문 · 대소문자 · 들여쓴 제목
    expect('H4 둘째 결과가 요약', h3('review_main', '[ownReview:self-reviewer+security-reviewer] 이유', good + '\n## 지적\n- C1 요약\n'), 2, '2번째 결과')
    expect('H4 들여쓴 지적 줄은 절 아님', h3('review_main', '[ownReview:self-reviewer+security-reviewer] 이유', good + '## 비고\n  ## 지적\n'), 2, "'## 지적' 절이 1개")
    expect('H4 소문자 접두어도 검사', h3('review_main', '[ownreview:self-reviewer] 이유', '## 지적\n- C1 요약'), 2, '결과 원문이 아니다')
    expect('H4 대문자 이름 받음', h3('review_main', '[ownReview:Self-Reviewer] 이유', good), 0)
    expect('H4 빈 태그', h3('review_main', '[ownReview:] 이유', good), 2, '모른다')
    # 태그 빠짐 · '- ' 아닌 줄 (계획서 2026-10-23_H5-셸-문법-우회, security-reviewer 7282089076 C10 · C11)
    expect('H4 review_main 태그 없는 ownReview', h3('review_main', '이유', good), 2, 'reason 맨 앞')
    expect('H4 태그 없는 서브에이전트 원문 (perf_main)', h3('perf_main', '이유', good), 2, '태그가 없다')
    expect('H4 perf_main 태그 없는 지휘자 의견 (설계안)', h3('perf_main', '이유', '설계안 검토: 할당 줄이기'), 0)
    expect('H4 perf_main 태그 없는 지휘자 의견 제목', h3('perf_main', '이유', '## 지휘자 의견\n- 할당 줄이기'), 0)
    expect("H4 perf_main 태그 없는 요약('## 지적'만)", h3('perf_main', '이유', '## 지적\n- C1 요약'), 2, '지휘자 의견')
    expect("H4 crash_main 지휘자 조사의 '## 지적'은 그대로", h3('crash_main', '이유', '## 지적\n- 의심 1'), 0)
    expect("H4 perf_main 태그 없는 '# 지적'(h1)", h3('perf_main', '이유', '# 지적\n- C1 요약'), 2, '지휘자 의견')
    expect("H4 읽은 파일의 번호 줄", h3('review_main', tag, good + '1. docs/worklog.md\n'), 2, "'- '로 시작하지 않는")
    expect('H4 읽은 파일의 맨 경로 줄', h3('review_main', tag, good + 'docs/worklog.md:1-5\n'), 2, "'- '로 시작하지 않는")
    expect('H4 변형 제목 읽은 파일 절도 검사', h3('review_main', tag, good + '## 읽은 파일 (추가)\n- docs/worklog.md:1-5\n'), 2, 'subagent_in/ 밖')
    expect('H4 태그 없는 변형 제목', h3('perf_main', '이유', '## 지적\n- x\n## 읽은 파일 (추가)\n- subagent_in/x/a.cs\n'), 2, '태그가 없다')
    expect('H4 들여쓴 · ### 읽은 파일 절도 검사', h3('review_main', tag, good + '## 비고\n- 없음\n  ### 읽은 파일\n- docs/worklog.md:1-5\n'), 2, 'subagent_in/ 밖')
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
    expect('H5 따옴표 안 글 (커밋 메시지 -m은 막힘, 2026-10-23 결정)', h5raw('git commit -m "slackplan new 형식 정리"'), 2)
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
    expect('H5 커밋 메시지 속 slackplan.exe (-m은 막힘, 2026-10-23 결정)', h5raw('git commit -m "bin/Debug의 slackplan.exe 잠금"'), 2)
    expect('H5 파일 이름 (제목 기본값)', h5(f'new {plan("ProjectX-출시.md", "깨끗")}'), 2, '파일 이름')
    os.makedirs(os.path.join(proj, 'GoldenSet'))
    with open(os.path.join(proj, 'GoldenSet', 'a.md'), 'w', encoding='utf-8') as f:
        f.write('깨끗\n')
    expect('H5 무시 폴더 아래 계획서', h5('new GoldenSet/a.md'), 2, 'GoldenSet/')
    expect('H5 PowerShell here-string 인자', h5raw("slackplan reply C1-2 @'\nProjectX\n'@", 'PowerShell'), 2, 'here-string')
    expect('H5 모르는 하위 명령', h5(f'publish {clean}'), 2, '모른다')
    expect('H5 react (이모지만)', h5('react C1-2 eyes'), 0)
    # 계획서 2026-10-23_H5-새-하위-명령과-지우는-명령
    expect('H5 list', h5('list'), 0)
    expect('H5 approval-record', h5(f'approval-record C1-2 --plan {clean}'), 0)
    expect('H5 status --link', h5('status C1-2 --link'), 0)
    expect('H5 unlink 미리보기', h5('unlink C1-2'), 0)
    expect('H5 purge 미리보기', h5('purge --days 30'), 0)
    expect('H5 unlink --yes', h5('unlink C1-2 --yes'), 2, 'unlink --yes')
    expect('H5 purge --yes', h5('purge --days 30 --yes'), 2, 'purge --yes')
    expect('H5 purge --yes (PowerShell 실행 파일)', h5raw(f'& "{bot}\\bin\\slackplan.exe" purge --yes', 'PowerShell'), 2, 'purge --yes')
    expect('H5 unlink 실제 스레드 키 모양', h5('unlink C0AB12CD_1792741463.590179'), 0)
    # /self-review 8c1412729e C1: 셸이 펼쳐 --yes가 되는 인자
    expect("H5 purge $'--yes'", h5raw("slackplan purge --days 30 $'--yes'"), 2, '셸이 펼칠')
    expect('H5 unlink {--yes,}', h5raw('slackplan unlink C1-2 {--yes,}'), 2, '셸이 펼칠')
    expect('H5 purge ${V:---yes}', h5raw('slackplan purge --days 30 ${V:---yes}'), 2, '셸이 펼칠')
    # tokenize가 PowerShell 백틱 이스케이프를 셸처럼 풀어 --yes로 본다 (/self-review security C6 — 이전에는 '셸이 펼칠'로 막힘)
    expect('H5 unlink PS 백틱', h5raw('& slackplan.exe unlink C1-2 --`yes', 'PowerShell'), 2, 'unlink --yes')
    expect("H5 purge PS 괄호 식", h5raw("& slackplan.exe purge --days 30 ('--yes')", 'PowerShell'), 2, '셸이 펼칠')
    expect("H5 따옴표로 감싼 '--yes'", h5raw("slackplan unlink C1-2 '--yes'"), 2, 'unlink --yes')
    expect('H5 inbox --note (로컬 작업 로그)', h5('inbox done C1-3 --note "처리함"'), 0)
    expect('H5 reply의 --dry-run은 통과 아님 (봇이 글로 올림)', h5(f'reply C1-2 "{SECRET}" --dry-run'), 2, "'--dry-run'")
    expect('H5 옵션 값 자리의 --dry-run (봇도 dry-run)', h5(f'new {bad} --title --dry-run'), 0)
    expect('H5 리다이렉트 2>&1', h5(f'new {clean} 2>&1'), 2, '리다이렉트')
    expect('H5 new --thread · --item', h5(f'new {clean} --thread C1-2 --item C1-3'), 0)
    expect('H5 --thread 값이 ID가 아님', h5(f'new {clean} --thread "a b"'), 2, 'ID 모양')
    expect('H5 슬래시 UNC', h5(f'new {plan("u2.md", "//srv/share/x")}'), 2, 'UNC')
    expect('H5 POSIX 절대 경로', h5(f'new {plan("t.md", "로그 /tmp/x.log")}'), 2, 'POSIX')
    expect('H5 URL 속 /var/ (오탐 아님)', h5(f'new {plan("url2.md", "https://example.org/var/x")}'), 0)
    # 봇 secret_scan 시험과 같은 비교 목록 (계획서 결정 2, 봇 test_secret_scan.ShareCheck SHARED_HIT/SHARED_PASS)
    for i, s in enumerate(['sk-' + 'Q7vR2mK9pL4xN8wT3bZ6', '-----BEGIN RSA ' + 'PRIVATE KEY-----', 'F:/AI/x', 'C:\\Users\\me', '\\\\srv\\share', '/Users/me/x', '/f/AI/x']):
        expect(f'H5 비교 목록 걸림 {i}', h5(f'new {plan(f"hit{i}.md", s)}'), 2, '막는 패턴')
    for i, s in enumerate(['https://example.org/var/x', 'e.g. 1:2', 'and/or', 'A/B', '1/2/3', 'a/b/c']):
        expect(f'H5 비교 목록 안 걸림 {s}', h5(f'new {plan(f"pass{i}.md", s)}'), 0)
    expect('H5 묶음', h5raw(f'cd {proj} && dotnet run --project "$SLACKPLAN_ROOT/src/SlackPlanBot" -- new {clean}'), 2)
    expect('H5 명령 치환', h5raw('dotnet run --project "$SLACKPLAN_ROOT/src/SlackPlanBot" -- new "$(echo x.md)"'), 2, '명령 치환')
    expect('H5 -- 없음', h5raw(f'dotnet run --project "$SLACKPLAN_ROOT/src/SlackPlanBot" new {clean}'), 2, '`--`')
    bad_abs = os.path.join(proj, bad).replace('\\', '/')
    expect('H5 셸 현재 폴더가 SlackPlanBot', h5raw(f'dotnet run -- new {bad_abs}', cwd=bot), 2, '비밀')
    expect('H5 PowerShell 실행 파일 경로', h5raw(f'& "{bot}\\bin\\slackplan.exe" new {bad}', 'PowerShell'), 2, '비밀')
    expect('H5 cmd /c 형식', h5raw(f'cmd /c slackplan new {clean}'), 2, '해석하지 못했다')
    # 앞 낱말 · 따옴표 · 줄 이음으로 감싸면 이전에는 검사 없이 통과 (계획서 2026-10-23_H5-앞-낱말-우회, /self-review e256053430 — 실측 exit 0)
    unparsed, chained = '해석하지 못했다', '다른 명령과 묶여'
    for pre in ['env', 'X=1', 'env X=1', 'command', 'exec', 'nice', 'time', 'sudo', 'nohup', 'eval']:
        expect(f'H5 앞 낱말 {pre} purge --yes', h5raw(f'{pre} slackplan purge --yes'), 2, unparsed)
        expect(f'H5 앞 낱말 {pre} new', h5raw(f'{pre} slackplan new {clean}'), 2, unparsed)
    for name, cmd, why in [
        ('따옴표 경로', 'env "$SLACKPLAN_ROOT/bin/slackplan" purge --yes', unparsed),
        ('공백 든 따옴표 경로', 'env "C:/Program Files/x/slackplan.exe" new secret.md', unparsed),
        ('따옴표 하위 명령', "env slackplan 'purge' --yes", unparsed),
        ('쪼갠 하위 명령', "nohup slackplan pur''ge --yes", unparsed),
        ("$'…' 하위 명령", "env slackplan $'purge' --yes", unparsed),
        ('이스케이프 하위 명령', 'env slackplan p\\urge --yes', unparsed),
        ('쪼갠 실행 파일 이름', "env slack''plan purge --yes", unparsed),
        ('전역 옵션 뒤 하위 명령', 'env slackplan --verbose purge --yes', unparsed),
        ('env dotnet …dll', 'env dotnet "$SLACKPLAN_ROOT/bin/slackplan.dll" purge --yes', unparsed),
        ('eval 따옴표', 'eval "slackplan purge --yes"', unparsed),
        ('env -S', "env -S 'slackplan purge --yes'", unparsed),
        ('xargs -I', 'xargs -I{} slackplan {} --yes', unparsed),
        ('stdin xargs', 'echo purge --yes | xargs slackplan', chained),
        ('xargs 하위 명령', 'echo a | xargs slackplan purge --yes', chained),
        ('줄 이음', 'env slackplan \\\npurge --yes', chained),
        ('검색 뒤 실행', 'grep x f | xargs slackplan purge', chained),
        ('git alias', "git -c alias.x='!slackplan purge --yes' x", unparsed),
        ('echo 명령 치환', 'echo $(slackplan purge --yes)', '명령 치환'),
    ]:
        expect(f'H5 우회 {name}', h5raw(cmd), 2, why)
    expect('H5 앞 낱말 env + .exe 경로', h5raw(f'env {bot}/bin/slackplan.exe unlink C1-2 --yes'), 2, unparsed)
    expect('H5 PowerShell 점 연산자', h5raw('. slackplan.exe purge --yes', 'PowerShell'), 2, unparsed)
    expect('H5 PowerShell 점 연산자 따옴표', h5raw(". 'slackplan.exe' purge --yes", 'PowerShell'), 2, unparsed)
    expect('H5 PowerShell 점 연산자 (읽기도 막음)', h5raw('. slackplan list', 'PowerShell'), 2, unparsed)
    expect('H5 PowerShell 하위 식', h5raw('Get-Content (slackplan purge --yes)', 'PowerShell'), 2)
    # 2회차 (/self-review 3efd4d0922, 실측 exit 0이던 형식)
    for name, cmd, tool in [
        ('공백 없는 &', 'echo x&env slackplan purge --yes', 'Bash'),
        ("$'' 쪼개기", "env slack$''plan purge --yes", 'Bash'),
        ('$"" 쪼개기', 'env slack$""plan purge --yes', 'Bash'),
        ('스크립트 블록', 'gci x | Get-Content -Path {slackplan purge --yes}', 'PowerShell'),
        ('해시 테이블', 'echo @{a=slackplan purge --yes}', 'PowerShell'),
        ('git grep -O', "git grep -O'slackplan purge --yes #' -e .", 'Bash'),
        ('rg --pre', 'rg --pre slackplan x purge', 'Bash'),
        ('heredoc을 bash에', "bash <<'EOF'\nslackplan purge --yes\nEOF", 'Bash'),
        ('here-string을 iex에', "@'\nslackplan purge --yes\n'@ | iex", 'PowerShell'),
        ('리다이렉트 + git hook', 'echo slackplan purge --yes > .git/hooks/post-commit; git commit -m x', 'Bash'),
        ('백슬래시 경로 (PowerShell)', '. C:\\bot\\bin\\slackplan.exe purge --yes', 'PowerShell'),
        ('백슬래시 경로 (Bash)', 'env C:\\\\bot\\\\slackplan.exe purge --yes', 'Bash'),
    ]:
        expect(f'H5 우회 {name}', h5raw(cmd, tool), 2, '[공유 차단]')
    # 단독 읽기 명령의 인자로만 나오면 통과
    for cmd in ['grep -rn "slackplan purge" docs', 'grep slackplan purge.txt', 'grep -rn slackplan files/', 'rg slackplan list',
                'git log --grep slackplan']:
        expect(f'H5 읽기 명령 {cmd}', h5raw(cmd), 0)
    expect('H5 읽기 명령 (PowerShell)', h5raw('Select-String -Path README.md -Pattern slackplan', 'PowerShell'), 0)
    expect('H5 heredoc 본문을 git에 (셸 아님)', h5raw("git commit -F - <<'EOF'\nslackplan purge --yes 정리\nEOF"), 0)
    # 따옴표 짝 · 주석 · heredoc 경계 + 본문은 허용 목록 (계획서 2026-10-23_H5-셸-문법-우회, security-reviewer 7282089076 — 이전 판 실측 exit 0)
    for name, cmd, tool in [
        ("C2 $'\\''로 묶음 숨김", "slackplan status $'\\'' ; slackplan purge X --yes ; 'z'", 'Bash'),
        ("C2 읽기 예외로 묶음 숨김", "grep $'\\'' ; slackplan purge X --yes ; 'z' f", 'Bash'),
        ('C3 따옴표 없는 heredoc 속 $()', 'cat <<EOF\n$(slackplan purge X --yes)\nEOF', 'Bash'),
        ('C3 PS @"…"@ 속 $()', '$m = @"\n$(slackplan purge X --yes)\n"@', 'PowerShell'),
        ('C4 따옴표 안 <<EOF', 'echo "<<EOF"\nslackplan purge X --yes\nEOF', 'Bash'),
        ("C4 따옴표 안 <<'EOF'", "echo \"<<'EOF'\"\nslackplan purge X --yes\nEOF", 'Bash'),
        ("C4 PS 따옴표 안 @'", "echo \"@'\nslackplan purge X --yes\n'@\"", 'PowerShell'),
        ('C7 heredoc을 tclsh', "tclsh <<'EOF'\nslackplan purge --yes\nEOF", 'Bash'),
        ('C7 heredoc을 perl', "perl <<'EOF'\nslackplan purge --yes\nEOF", 'Bash'),
        ('C7 heredoc을 python 스크립트', "python -I x.py <<'EOF'\nslackplan purge --yes\nEOF", 'Bash'),
        ('C7 대입 뒤 Invoke-Expression', "$m = @'\nslackplan purge --yes\n'@\nInvoke-Expression $m", 'PowerShell'),
        ('주석 속 따옴표', "grep x f # '\nslackplan purge X --yes\n'", 'Bash'),
        ('PS 주석 속 따옴표', "Select-String x f # '\nslackplan purge X --yes\n'", 'PowerShell'),
        ('C1 PS 굽은 따옴표', "sls 'a’; slackplan purge X --yes ; ‘' f", 'PowerShell'),
        ('C6 ~ 펼침', 'slackplan reply C1-2 로그는 ~/x 참고', 'Bash'),
        ('C6 글롭', 'slackplan reply C1-2 파일 *.md', 'Bash'),
        ('C6 따옴표 밖 물음표', 'slackplan reply C1-2 할까요?', 'Bash'),
        ('중괄호 펼침 {~,x}', 'slackplan reply C1-2 {~,x}/a', 'Bash'),
        # /cross-review 8722c947f7 반영
        ('PS <# #> 뒤 reply 글', 'slackplan reply C1-2 a <# c #> C:/Users/me/secret', 'PowerShell'),
        ('PS 굽은 따옴표로 쪼갠 이름', '& { sl\u2018\u2019ackplan purge X --yes }', 'PowerShell'),
        ('경로 붙은 cat', "./cat <<'EOF'\nslackplan purge --yes\nEOF", 'Bash'),
        ('<<<의 둘째 <', 'grep x <<<true\nslackplan purge X --yes\ntrue', 'Bash'),
        ('이스케이프 공백 뒤 # (읽기 예외)', 'grep \\ #; slackplan purge X --yes', 'Bash'),
        ('본문의 dotnet run SlackPlanBot', "bash <<'EOF'\ndotnet run --project \"$SLACKPLAN_ROOT/src/SlackPlanBot\" -- purge --yes\nEOF", 'Bash'),
        ('cat &bash heredoc', "cat &bash <<'EOF'\nslackplan purge --yes\nEOF", 'Bash'),
        ('PS 백틱으로 숨긴 --allow-warnings', 'slackplan ask-consent C1-2 `"x --allow-warnings', 'PowerShell'),
    ]:
        expect(f'H5 셸 문법 {name}', h5raw(cmd, tool), 2, '[공유 차단]')
    for name, cmd, tool in [
        ('커밋 -m "$(cat heredoc)"', "git commit -m \"$(cat <<'EOF'\nslackplan 정리 don't (x)\nEOF\n)\"", 'Bash'),
        ('커밋 -F - 따옴표 든 본문', "git commit -F - <<'EOF'\nslackplan don't \"x\" $(y)\nEOF", 'Bash'),
        ('cat > 파일', "cat > notes.md <<'EOF'\nslackplan new\nEOF", 'Bash'),
        ('PS 대입', "$m = @'\nslackplan new\n'@", 'PowerShell'),
        ('PS here-string | Set-Content', "@'\nslackplan new\n'@ | Set-Content x.md", 'PowerShell'),
        ('reply 따옴표 안 물음표', 'slackplan reply C1-2 "할까요?"', 'Bash'),
        ('주석 속 낱말 (읽기)', 'grep -n x f # slackplan 메모', 'Bash'),
    ]:
        expect(f'H5 셸 문법 통과 {name}', h5raw(cmd, tool), 0)
    # 읽기 예외를 좁혀 막히는 것 (계획서 결정: 단독 읽기 명령만, git commit 속 낱말도 막힘 — 사용자 "git 명령어는 크게 신경 안써도되")
    for cmd in ['cat notes.md | grep slackplan', 'echo slackplan list', 'ls bin/slackplan.exe', 'git commit -m "slackplan new 형식"']:
        expect(f'H5 좁힌 예외 {cmd}', h5raw(cmd), 2)
    subprocess.run(['git', 'init', '-q', proj], check=True, capture_output=True)
    subprocess.run(['git', '-C', proj, 'add', '-f', '.claude/share_guard.local.txt'], check=True, capture_output=True)
    expect('H5 share_guard.local.txt 추적됨', h5(f'new {clean}'), 2, '추적')
    expect('H5 깨진 입력은 막음', (lambda r: (r.returncode, r.stderr.decode('utf-8', 'replace')))(subprocess.run([sys.executable, '-I', os.path.join(HERE, 'share_guard.py')], input=b'{', capture_output=True)), 2)
finally:
    shutil.rmtree(tmp, ignore_errors=True)

print('ALL', ok)
sys.exit(0 if ok else 1)
