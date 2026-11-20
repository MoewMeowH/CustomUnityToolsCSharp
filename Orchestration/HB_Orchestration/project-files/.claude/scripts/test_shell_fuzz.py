"""셸 파서 속성 시험: 위험 명령을 무작위 포장으로 감싸 진짜 셸에서 돌리고(가짜 git · rm), '실제로 불렸는데 훅이 통과시킨' 경우를 찾는다.

사용: python -I .claude/scripts/test_shell_fuzz.py  → 마지막 줄 'ALL True'면 통과 (exit 0).
     환경 변수 FUZZ_SEED(기본 20261121) · FUZZ_N(bash 건수, 기본 160 — PowerShell은 1/3) · FUZZ_JOBS(동시 실행, 기본 8).

- 정답: 임시 sandbox에서 Git Bash · PowerShell로 실제 실행한다. PATH 맨 앞의 가짜 git · rm(sh · .cmd)과 PowerShell Remove-Item 함수가
  불린 인자를 기록만 한다. 생성기는 절대 경로 실행 파일 · 다른 실행 파일을 만들지 않는다 (가짜를 거치지 않는 실행이 없게).
  rm 대상은 sandbox 안 상대 경로라 가짜를 빠져나가도 sandbox 밖을 지우지 않는다.
- 판정: 같은 명령을 훅에 넣는다. 훅마다 '알아채면 반드시 막히는' 자리에서 돌린다:
  push_guard = 저장소가 아닌 폴더(검사할 저장소를 못 찾으면 막는다) · cleanup_guard = 상대 경로 대상(절대 경로가 아니면 막는다).
- 성질: 가짜가 위험 인자(push · 재귀 삭제)로 불렸으면 훅은 exit 2여야 한다. 안 불렸는데 막은 것은 보수적이라 허용(수만 낸다).
- 실패하면 포장 층을 하나씩 빼 보며 가장 짧은 재현 명령을 낸다.
"""
import concurrent.futures
import json
import os
import random
import shutil
import subprocess
import sys
import tempfile

sys.stdout.reconfigure(encoding='utf-8')
HERE = os.environ.get('FUZZ_HOOKS_DIR') or os.path.dirname(os.path.abspath(__file__))  # 시험할 훅 폴더 (기본: 이 파일 옆)
BASH =os.environ.get('FUZZ_BASH') or r'C:\Program Files\Git\bin\bash.exe'
SEED = int(os.environ.get('FUZZ_SEED') or 20261121)
N = int(os.environ.get('FUZZ_N') or 160)
JOBS = int(os.environ.get('FUZZ_JOBS') or 8)

# ── 위험 핵심 명령 ─────────────────────────────────────────────────────
CORES = {
    ('push_guard.py', 'bash'): ['git push', 'git push origin main', 'git  push --force', 'git -c core.x=y push'],
    # git.exe는 PowerShell이 진짜 실행 파일을 찾는다 (가짜는 git · git.cmd뿐) — 정답을 못 내고 진짜 git이 돌므로 넣지 않는다
    ('push_guard.py', 'ps'): ['git push', 'git push origin main', 'git push --force'],
    ('cleanup_guard.py', 'bash'): ['rm -rf victim', 'rm -r -f victim', 'rm --recursive victim', 'rm -fr victim', 'rm -R victim'],
    ('cleanup_guard.py', 'ps'): ['Remove-Item -Recurse -Force victim', 'rm -Recurse victim', 'ri -r victim', 'del -Recurse victim'],
}


def danger(hook: str, call: str) -> bool:
    """가짜 실행 파일 기록 한 줄이 위험 실행인지."""
    words = call.split()
    if hook == 'push_guard.py':
        return bool(words) and words[0] == 'git' and 'push' in words[1:]
    # 재귀 옵션: -r · -R · -rf 같은 짧은 묶음, --recursive, PowerShell -Recurse (-Force는 아님)
    return bool(words) and words[0] == 'rm' and any(
        w.lower() in ('--recursive', '-recurse') or (w.startswith('-') and not w.startswith('--') and 'r' in w.lower() and w.lower() != '-force')
        for w in words[1:])


# ── 포장 (층 하나 = 명령 글 → 명령 글) ────────────────────────────────
def bash_layers(r: random.Random):
    return [
        ('chain-and', lambda x: f'true && {x}'),
        ('chain-or', lambda x: f'false || {x}'),
        ('semicolon', lambda x: f'echo a; {x}'),
        ('newline', lambda x: f'echo a\n{x}'),
        ('subshell', lambda x: f'({x})'),
        ('group', lambda x: f'{{ {x}; }}'),
        ('subst', lambda x: f'echo $({x})'),
        ('assign-subst', lambda x: f'v=$({x}); true'),
        ('backtick', lambda x: f'echo `{x}`'),
        ('bash-c-dq', lambda x: f'bash -c "{x}"' if '"' not in x and '`' not in x and '$' not in x else x),
        ('bash-c-sq', lambda x: f"bash -c '{x}'" if "'" not in x else x),
        ('eval', lambda x: f'eval "{x}"' if '"' not in x and '`' not in x and '$' not in x else x),
        ('if', lambda x: f'if true; then {x}; fi'),
        ('func', lambda x: f'f() {{ {x}; }}; f'),
        ('pipe-in', lambda x: f'echo z | {x}'),
        ('bash-heredoc', lambda x: f'bash <<EOF\n{x}\nEOF'),
        ('bash-heredoc-q', lambda x: f"bash <<'EOF'\n{x}\nEOF"),
        ('cat-heredoc-subst', lambda x: f'cat <<EOF\n$({x})\nEOF'),
        ('comment-after', lambda x: f'{x} # note'),
        ('dq-msg-before', lambda x: f'echo "a;b" && {x}'),
        ('sq-msg-before', lambda x: f"echo 'a # b' ; {x}"),
        ('env', lambda x: f'env {x}'),
        ('command', lambda x: f'command {x}'),
        ('backslash-name', lambda x: '\\' + x),
        ('split-quote-name', lambda x: x[0] + '""' + x[1:]),
        ('quoted-name', lambda x: '"' + x.split(' ', 1)[0] + '" ' + x.split(' ', 1)[1] if ' ' in x else x),
        ('line-continue', lambda x: x.replace(' ', ' \\\n', 1)),
        ('comment-continue', lambda x: f'# note \\\n{x}'),
        ('sq-backslash-comment-continue', lambda x: f"echo '\\'; # note \\\n{x}"),
        ('var-name', lambda x: f'c={x.split(" ", 1)[0]}; $c {x.split(" ", 1)[1]}' if ' ' in x else x),
        ('brace-var-name', lambda x: f'c={x.split(" ", 1)[0]}; ${{c}} {x.split(" ", 1)[1]}' if ' ' in x else x),
        ('xargs', lambda x: f'echo {x.split(" ", 1)[1]} | xargs {x.split(" ", 1)[0]}' if ' ' in x and '|' not in x and '$' not in x and '`' not in x else x),
        # 실행되지 않는 포장 (훅이 막아도 실패가 아니다 — 보수적 차단 수만 센다)
        ('comment', lambda x: '# ' + x.replace('\n', ' ')),
        ('echo-dq', lambda x: f'echo "{x}"' if '"' not in x and '`' not in x and '$' not in x else x),
        ('cat-heredoc-q', lambda x: f"cat <<'EOF'\n{x}\nEOF"),
    ]


def ps_layers(r: random.Random):
    return [
        ('amp', lambda x: f'& {{ {x} }}'),
        ('dot', lambda x: f'. {{ {x} }}'),
        ('subexpr', lambda x: f'$null = $({x})'),
        ('array', lambda x: f'$null = @({x})'),
        ('if', lambda x: f'if ($true) {{ {x} }}'),
        ('semicolon', lambda x: f'Write-Output a; {x}'),
        ('newline', lambda x: f'Write-Output a\n{x}'),
        ('iex-sq', lambda x: f"Invoke-Expression '{x}'" if "'" not in x else x),
        ('iex-dq', lambda x: f'iex "{x}"' if '"' not in x and '$' not in x and '`' not in x else x),
        ('call-name', lambda x: f'& {x.split(" ", 1)[0]} {x.split(" ", 1)[1]}' if ' ' in x and x.split(' ', 1)[0] in ('git', 'git.exe') else x),
        ('var-name', lambda x: f"$c = '{x.split(' ', 1)[0]}'; & $c {x.split(' ', 1)[1]}" if ' ' in x and x.split(' ', 1)[0] in ('git', 'git.exe') else x),
        ('backtick-name', lambda x: x[0] + '`' + x[1:] if x[1:2].isalpha() and x[1:2].lower() not in 'abefnrtv0u' else x),
        ('cmd-c', lambda x: f'cmd /c {x}' if x.startswith('git') and '"' not in x and '&' not in x and '|' not in x else x),
        ('expandable-herestring', lambda x: f'$null = @"\n$({x})\n"@' if '"@' not in x else x),
        ('foreach', lambda x: f'1 | ForEach-Object {{ {x} }}'),
        ('comment-after', lambda x: f'{x} # note'),
        ('line-continue', lambda x: x.replace(' ', ' `\n', 1)),
        ('comment-continue', lambda x: f'# note `\n{x}'),
        ('curly-quote-msg', lambda x: f'Write-Output ‘a; b’; {x}'),
        # 실행되지 않는 포장
        ('comment', lambda x: '# ' + x.replace('\n', ' ')),
        ('block-comment', lambda x: f'<# {x} #>' if '#>' not in x else x),
        ('literal-herestring', lambda x: f"$null = @'\n{x}\n'@" if "'@" not in x else x),
        ('sq-string', lambda x: f"Write-Output '{x}'" if "'" not in x else x),
    ]


def build(core: str, layers: list, picks: list[int]) -> str:
    cmd = core
    for i in picks:
        cmd = layers[i][1](cmd)
    return cmd


# ── 실행 · 판정 ──────────────────────────────────────────────────────
def make_bin(root: str) -> str:
    b = os.path.join(root, 'bin')
    os.makedirs(b)
    for name in ('git', 'rm'):
        with open(os.path.join(b, name), 'w', newline='\n') as f:
            f.write(f'#!/bin/sh\necho "{name} $*" >> "$FUZZ_LOG"\n')
        with open(os.path.join(b, name + '.cmd'), 'w', newline='\r\n') as f:
            f.write(f'@echo {name} %*>> "%FUZZ_LOG%"\n')
    return b


def posix(p: str) -> str:
    return '/' + p[0].lower() + p[2:].replace('\\', '/') if len(p) > 1 and p[1] == ':' else p.replace('\\', '/')


def executed(shell: str, cmd: str, bin_dir: str, work: str) -> list[str]:
    log = os.path.join(work, 'calls.log')
    env = dict(os.environ, FUZZ_LOG=log, PATH=bin_dir + os.pathsep + os.environ.get('PATH', ''))
    try:
        if shell == 'bash':
            prelude = f'export FUZZ_LOG="{posix(log)}"; export PATH="{posix(bin_dir)}:$PATH"\n'
            subprocess.run([BASH, '--noprofile', '--norc', '-c', prelude + cmd], cwd=work, env=env, capture_output=True, timeout=20, stdin=subprocess.DEVNULL)
        else:
            prelude = (f"function Remove-Item {{ Add-Content -LiteralPath '{log}' -Value ('rm ' + ($args -join ' ')) }}\n")
            subprocess.run(['powershell', '-NoProfile', '-NonInteractive', '-Command', prelude + cmd], cwd=work, env=env,
                           capture_output=True, timeout=40, stdin=subprocess.DEVNULL)
    except subprocess.TimeoutExpired:
        pass
    try:
        with open(log, encoding='utf-8', errors='replace') as f:
            return [l.strip() for l in f if l.strip()]
    except OSError:
        return []


def blocked(hook: str, shell: str, cmd: str, work: str) -> bool:
    payload = {'tool_name': 'Bash' if shell == 'bash' else 'PowerShell', 'tool_input': {'command': cmd}, 'cwd': work}
    env = dict(os.environ, CLAUDE_PROJECT_DIR=work)
    r = subprocess.run([sys.executable, '-I', os.path.join(HERE, hook)], input=json.dumps(payload, ensure_ascii=False).encode('utf-8'),
                       capture_output=True, env=env, timeout=60)
    return r.returncode == 2


def case(hook: str, shell: str, cmd: str, root: str, n: int, bin_dir: str) -> tuple[bool, bool]:
    """(위험 실행됨, 훅이 막음)."""
    work = os.path.join(root, f'w{n}')
    os.makedirs(os.path.join(work, 'victim'), exist_ok=True)
    if os.path.exists(os.path.join(work, 'calls.log')):  # 줄이기(shrink)는 같은 폴더를 다시 쓴다
        os.remove(os.path.join(work, 'calls.log'))
    ran = any(danger(hook, c) for c in executed(shell, cmd, bin_dir, work))
    return ran, blocked(hook, shell, cmd, work)


def shrink(hook: str, shell: str, core: str, layers: list, picks: list[int], root: str, bin_dir: str) -> str:
    """층을 하나씩 빼도 '실행됨 + 통과'가 남으면 뺀다."""
    n = 10_000
    changed = True
    while changed and picks:
        changed = False
        for i in range(len(picks)):
            trial = picks[:i] + picks[i + 1:]
            n += 1
            ran, blk = case(hook, shell, build(core, layers, trial), root, n, bin_dir)
            if ran and not blk:
                picks, changed = trial, True
                break
    return build(core, layers, picks)


def main() -> int:
    if not os.path.isfile(BASH):
        print(f'ALL False — Git Bash가 없다: {BASH} (FUZZ_BASH로 지정)')
        return 1
    root = tempfile.mkdtemp(prefix='hb-shellfuzz-')
    ok = True
    try:
        bin_dir = make_bin(root)
        for (hook, shell), cores in CORES.items():
            r = random.Random(f'{SEED}:{hook}:{shell}')
            layers = bash_layers(r) if shell == 'bash' else ps_layers(r)
            count = N if shell == 'bash' else max(10, N // 3)
            jobs = []
            for n in range(count):
                core = r.choice(cores)
                picks = [r.randrange(len(layers)) for _ in range(r.randint(0, 3))]
                jobs.append((core, picks, build(core, layers, picks)))
            with concurrent.futures.ThreadPoolExecutor(JOBS) as pool:
                results = list(pool.map(lambda a: case(hook, shell, a[1][2], root, a[0], bin_dir), enumerate(jobs)))
            ran_n = sum(1 for ran, _ in results if ran)
            misses = [(j, res) for j, res in zip(jobs, results) if res[0] and not res[1]]
            cautious = sum(1 for ran, blk in results if not ran and blk)
            print(f'{"PASS" if not misses else "FAIL"} {hook} {shell}: {count}건 · 실제 실행 {ran_n} · 놓침 {len(misses)} · 보수적 차단 {cautious}')
            seen = set()
            for (core, picks, cmd), _ in misses:
                small = shrink(hook, shell, core, layers, picks, root, bin_dir)
                if small in seen:
                    continue
                seen.add(small)
                print(f'   놓침: {small!r}')
            ok &= not misses
    finally:
        shutil.rmtree(root, ignore_errors=True)
    print('ALL', ok)
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
