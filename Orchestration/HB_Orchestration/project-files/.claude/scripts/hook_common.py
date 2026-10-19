"""PreToolUse 훅 공용: 입력 읽기 · 셸 명령 해석 (계획서 2026-10-19_훅-규칙-강제, /cross-review 10bfc8de41 반영).

훅은 명령을 완전히 해석하지 않는다. 판정은 두 단계다:
1. 거친 탐지: heredoc·here-string 본문과 따옴표 안 글자를 비운 텍스트(+ 명령 치환 안의 텍스트)에서 대상 명령을 찾는다.
2. 대상이면 "단순한 단독 명령"인지 엄격히 본다 — 묶음·cd·명령 치환이 있으면 해석하지 않고 막는다 (안전한 쪽).
"""
import json
import os
import re
import sys

BASH, PS = 'bash', 'ps'


def read_input() -> dict:
    # 바이트로 읽어 UTF-8로 푼다: Windows 파이프 stdin의 기본 인코딩은 cp949라 json.load(sys.stdin)이면 한글이 깨진다 (2026-10-19 실사용 오탐).
    return json.loads(sys.stdin.buffer.read().decode('utf-8-sig'))


def shell_of(tool_name: str) -> str:
    return PS if tool_name == 'PowerShell' else BASH


# bash here-string(<<<)은 heredoc이 아니다.
HEREDOC = re.compile(r'(?<!<)<<(?!<)(-?)[ \t]*(["\']?)([A-Za-z_][A-Za-z0-9_]*)\2[^\n]*\n')
HERESTRING = re.compile(r'@(["\'])[ \t]*\n.*?\n\1@', re.S)


def strip_bodies(cmd: str) -> str:
    """heredoc(<<'EOF' … EOF) · PowerShell here-string(@' … '@) 본문을 뺀다 — 글 속의 명령 문구를 명령으로 보지 않게.
    끝 표시가 없으면 본문을 지우지 않는다 (뒤의 진짜 명령을 숨기지 않게 — 오탐 쪽으로 틀린다)."""
    cmd = HERESTRING.sub('@""@', cmd)
    out, pos = [], 0
    while (m := HEREDOC.search(cmd, pos)) is not None:
        term = re.escape(m.group(3))
        end = re.compile((r'^\t*' if m.group(1) else r'^') + term + r'[ \t]*$', re.M).search(cmd, m.end())
        if end is None:
            break
        out.append(cmd[pos:m.end()])
        out.append(cmd[end.start():end.end()])
        pos = end.end()
    out.append(cmd[pos:])
    return ''.join(out)


def _matching_paren(s: str, open_at: int) -> int:
    depth = 0
    for k in range(open_at, len(s)):
        if s[k] == '(':
            depth += 1
        elif s[k] == ')':
            depth -= 1
            if depth == 0:
                return k
    return len(s)


def scan(cmd: str, shell: str) -> tuple[str, list[str]]:
    """따옴표 안을 비운 텍스트와, 큰따옴표 안 명령 치환($( ) · bash 백틱)의 내용 목록.
    bash: '…'는 그대로, "…" 안에서는 \\가 이스케이프. PowerShell: '…'(''가 이스케이프), "…" 안에서는 백틱이 이스케이프."""
    out, inner = [], []
    i, n = 0, len(cmd)
    esc = '\\' if shell == BASH else '`'
    while i < n:
        c = cmd[i]
        if c == esc and i + 1 < n:  # 따옴표 밖 이스케이프 (\' · `")는 따옴표 시작이 아니다
            out.append(cmd[i:i + 2])
            i += 2
            continue
        if c == "'":
            j = i + 1
            while j < n:
                if cmd[j] == "'":
                    if shell == PS and j + 1 < n and cmd[j + 1] == "'":
                        j += 2
                        continue
                    break
                j += 1
            out.append("''")
            i = j + 1
            continue
        if c == '"':
            j = i + 1
            while j < n and cmd[j] != '"':
                if cmd[j] == esc and j + 1 < n:
                    j += 2
                    continue
                if cmd[j] == '$' and j + 1 < n and cmd[j + 1] == '(':
                    k = _matching_paren(cmd, j + 1)
                    inner.append(cmd[j + 2:k])
                    j = k + 1
                    continue
                if shell == BASH and cmd[j] == '`':
                    k = cmd.find('`', j + 1)
                    k = n if k < 0 else k
                    inner.append(cmd[j + 1:k])
                    j = k + 1
                    continue
                j += 1
            out.append('""')
            i = j + 1
            continue
        out.append(c)
        i += 1
    return ''.join(out), inner


def has_substitution(stripped: str, inner: list[str], shell: str) -> bool:
    return bool(inner) or '$(' in stripped or (shell == BASH and '`' in stripped)


CHAIN = re.compile(r'&&|\|\||;|\||\n|(?:^|\s)&(?:\s|$)')
CD = re.compile(r'(?:^|[\s;&|(])(?:cd|chdir|Set-Location|sl|pushd|Push-Location|popd|Pop-Location)(?:\s|$)', re.IGNORECASE)


def tokenize(cmd: str, shell: str) -> list[str]:
    """단독 명령 하나를 토큰으로 (따옴표를 벗긴 값). 묶음은 앞에서 막았다고 가정한다."""
    toks: list[str] = []
    i, n = 0, len(cmd)
    esc = '\\' if shell == BASH else '`'
    while i < n:
        while i < n and cmd[i] in ' \t\r\n':
            i += 1
        if i >= n:
            break
        buf = []
        while i < n and cmd[i] not in ' \t\r\n':
            c = cmd[i]
            if c == "'":
                j = i + 1
                while j < n:
                    if cmd[j] == "'":
                        if shell == PS and j + 1 < n and cmd[j + 1] == "'":
                            buf.append("'")
                            j += 2
                            continue
                        break
                    buf.append(cmd[j])
                    j += 1
                i = j + 1
            elif c == '"':
                j = i + 1
                while j < n and cmd[j] != '"':
                    if cmd[j] == esc and j + 1 < n and (shell == PS or cmd[j + 1] in '\\"$`'):
                        buf.append(cmd[j + 1])
                        j += 2
                        continue
                    buf.append(cmd[j])
                    j += 1
                i = j + 1
            elif shell == BASH and c == '\\' and i + 1 < n:
                buf.append(cmd[i + 1])
                i += 2
            else:
                buf.append(c)
                i += 1
        toks.append(''.join(buf))
    return toks


def native_path(path: str) -> str:
    """Git Bash 경로(/f/AI/…)를 Windows 경로(F:/AI/…)로. Windows가 아니면 그대로."""
    if os.name == 'nt' and (m := re.match(r'^/([A-Za-z])(/.*)?$', path)) is not None:
        return f'{m.group(1).upper()}:{m.group(2) or "/"}'
    return path


def same_or_inside(child: str, parent: str) -> bool:
    c = os.path.normcase(os.path.abspath(child)).rstrip('\\/')
    p = os.path.normcase(os.path.abspath(parent)).rstrip('\\/')
    return c == p or c.startswith(p + os.sep)
