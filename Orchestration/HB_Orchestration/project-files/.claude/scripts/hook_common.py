"""PreToolUse 훅 공용: 입력 읽기 · 셸 명령 해석 (계획서 2026-10-19_훅-규칙-강제, /cross-review 10bfc8de41 반영).

훅은 명령을 완전히 해석하지 않는다. 판정은 두 단계다:
1. 거친 탐지: heredoc·here-string 본문과 따옴표 안 글자를 비운 텍스트(+ 명령 치환 안의 텍스트)에서 대상 명령을 찾는다.
2. 대상이면 "단순한 단독 명령"인지 엄격히 본다 — 묶음·cd·명령 치환이 있으면 해석하지 않고 막는다 (안전한 쪽).
따옴표 짝 · 주석 · heredoc 경계는 한 해석기(_Lexer)가 셸과 같게 맞춘다: bash $'…' · 낱말 시작의 # · 따옴표 안의 << ·
PowerShell 굽은 따옴표를 몰라 뒤 명령이 따옴표 안으로 숨던 빈틈 (계획서 2026-10-23_H5-셸-문법-우회, 실측 exit 0).
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


# PowerShell은 굽은 따옴표도 같은 종류의 따옴표로 본다 ('a’ 는 한 문자열 — PowerShell 5.1 실측 2026-10-23)
PS_SQ = "'‘’‚‛"
PS_DQ = '"“”„'
# 이 글자 뒤가 낱말 시작이다 — 낱말 시작의 #만 주석 (실측 2026-10-23: bash는 > < 뒤 #도 주석이고 $( ) 뒤 #는 낱말의 일부,
# PowerShell은 { } , ) 뒤 #도 주석). 이스케이프된 글자 뒤는 낱말 가운데다 (echo \ #; …에서 #는 글자).
# PowerShell '=' 뒤 #는 모드에 따라 다르다 (식 $v =#… 주석 · 인자 a=#b 글자 — 실측) → command()에서 줄 끝까지 원문으로 보인다.
WORD_BREAK = {BASH: ' \t\r\n;&|(<>', PS: ' \t\r\n;&|(){},'}
# bash 메타 문자: heredoc 끝 표시 낱말은 여기까지 (따옴표 · \는 벗기고, 하나라도 있으면 따옴표 heredoc)
BASH_META = ' \t\r\n;&|<>()'
# $( ) 안의 case 문: 패턴 뒤 ')'는 치환의 끝이 아니다
# 주어 형식(따옴표 · 공백)을 가리지 않는다 — 'case' 낱말만 보고, 잘못 알아보면 치환이 끝까지 이어져 inner가 커질 뿐(막는 쪽)
CASE_START = re.compile(r'case(?=\s)')
CASE_END = re.compile(r'esac(?![\w-])')


class Lexed:
    """visible: 따옴표 안 · 주석 · heredoc/here-string 본문을 비운 글 (따옴표는 '' · "" 자리표로 남김).
    inner: 큰따옴표 · 따옴표 없는 heredoc · @"…"@ 안의 명령 치환($( ) · bash 백틱) 원문.
    data: 셸이 펼치지 않는 본문(따옴표 heredoc · @'…'@) 원문. cuts: 그 본문의 (시작, 끝) 위치.
    expanded: 셸이 펼치는 본문(따옴표 없는 heredoc · @"…"@) 원문 — 보이는 글에는 없고 치환만 inner에 있다.
    bodies: 본문 전부 (data + expanded) — 본문을 실행기에 넘기는지 볼 때 쓴다.
    quoted: 따옴표('…' · "…" · $'…') 안 글자 — bash -c "…" · iex "…"처럼 명령 문자열로 넘기는지 볼 때 쓴다.
    comments: 주석의 (시작, 끝) 위치."""

    def __init__(self, visible: str, inner: list[str], data: list[str], cuts: list[tuple[int, int]], expanded: list[str],
                 quoted: list[str] | None = None, comments: list[tuple[int, int]] | None = None):
        self.visible, self.inner, self.data, self.cuts, self.expanded = visible, inner, data, cuts, expanded
        self.bodies = data + expanded
        self.quoted = quoted or []
        self.comments = comments or []

    def unquoted(self, cmd: str) -> str:
        """주석 · 따옴표 heredoc 본문을 빼고 따옴표 · 백틱 글자를 지운 원문 — 겹친 따옴표로 나뉜 명령 문자열(powershell -Command
        "Remove-Item "$p" -Recurse")이나 쪼갠 이름(r""m)을 한 덩어리로 보려고 (/self-review security ddb47044d1 C1 · C5)."""
        out, pos = [], 0
        for start, end in sorted(self.cuts + self.comments):
            if start >= pos:
                out.append(cmd[pos:start])
                pos = end
        out.append(cmd[pos:])
        return re.sub(r'[\'"`]', '', ''.join(out))


class _Lexer:
    """명령 하나를 문자 단위로 읽는다 (계획서 2026-10-23_H5-셸-문법-우회). 셸 문법을 다 해석하지 않고, 따옴표 짝 ·
    주석 · heredoc 경계만 셸과 같게 맞춘다 — 짝이 어긋나면 뒤 명령이 따옴표 안으로 숨기 때문이다."""

    def __init__(self, cmd: str, shell: str):
        self.s, self.n, self.shell = cmd, len(cmd), shell
        self.esc = '\\' if shell == BASH else '`'
        self.sq, self.dq = ("'", '"') if shell == BASH else (PS_SQ, PS_DQ)
        self.inner: list[str] = []
        self.data: list[str] = []
        self.expanded: list[str] = []
        self.quoted: list[str] = []
        self.comments: list[tuple[int, int]] = []
        self.cuts: list[tuple[int, int]] = []
        self.pending: list[tuple[bool, str, bool]] = []  # 다음 줄바꿈에서 읽을 heredoc (탭 허용, 끝 표시, 따옴표)
        self.unterminated = False  # 끝 표시를 못 찾은 heredoc — 그 뒤는 원문 그대로 보이는 글에 (따옴표 짝에 기대지 않게)

    def command(self, i: int, in_paren: bool = False) -> tuple[int, str]:
        """명령 상태로 읽는다. in_paren이면 짝 맞는 ')' 위치에서 멈춘다 ($( ) 안). (멈춘 위치, 보이는 글)."""
        s, n, out, depth, cases = self.s, self.n, [], 0, 0
        bash, brk = self.shell == BASH, WORD_BREAK[self.shell]
        word_start = True  # 지금 위치가 낱말 시작인지 — 앞 글자가 아니라 읽은 상태로 정한다
        while i < n:
            c = s[i]
            if c == self.esc and i + 1 < n:  # 따옴표 밖 이스케이프 (\' · `" · 줄 이음)는 따옴표 · 줄 끝이 아니다
                out.append(s[i:i + 2])
                if s[i + 1] != '\n':  # 줄 이음은 두 글자가 사라질 뿐 — 앞 상태 유지 (bash 실측: 'echo a \⏎#c'는 주석)
                    word_start = False
                i += 2
                continue
            if c == '\n':
                out.append(c)
                i = self.bodies(i + 1)
                if self.unterminated:
                    out.append(s[i:])
                    i = n
                word_start = True
                continue
            if word_start and not bash and s.startswith('<#', i):  # PowerShell 블록 주석 (낱말 가운데 a<#c#>b는 글자 — 실측)
                end = s.find('#>', i + 2)
                end = n if end < 0 else end + 2
                self.comments.append((i, end))
                i = end
                continue
            if c == '#' and word_start:
                end = s.find('\n', i)
                end = n if end < 0 else end
                self.comments.append((i, end))
                i = end
                continue
            if c == '#' and not bash and i > 0 and s[i - 1] == '=':  # 모드에 따라 주석이거나 글자 — 줄 끝까지 원문 그대로
                end = s.find('\n', i)
                end = n if end < 0 else end
                out.append(s[i:end])
                i = end
                continue
            if word_start and not bash and re.match(r'--%(?=\s|$)', s[i:i + 4]):  # 정지 기호: 줄 끝까지 글자 그대로
                end = s.find('\n', i)
                end = n if end < 0 else end
                out.append(s[i:end])
                i = end
                continue
            if bash and (s.startswith('$((', i) or (word_start and s.startswith('((', i))):  # 산술: 안의 <<는 비트 이동
                end = self.arith_end(i + (3 if c == '$' else 2))
                out.append(s[i:end])
                i, word_start = end, False
                continue
            if word_start and in_paren and bash:
                if m := CASE_START.match(s, i):
                    cases += 1
                    out.append(m.group(0))
                    i, word_start = m.end(), False
                    continue
                if cases and (m := CASE_END.match(s, i)):
                    cases -= 1
            if bash and s.startswith("$'", i):  # ANSI-C 따옴표: 안에서 \가 이스케이프
                j = self.ansi_end(i + 2)
                self.quoted.append(s[i + 2:max(j - 1, i + 2)])
                i = j
                out.append("$''")
                word_start = False
                continue
            if not bash and s.startswith('${', i):  # PowerShell 중괄호 변수 이름: 안의 따옴표는 글자 (${a'b} · ${a`}b})
                i = brace_var_end(s, i)
                out.append('${}')
                word_start = False
                continue
            if bash and (s.startswith('${', i) or s.startswith('$[', i)):  # 매개변수 펼침 · 옛 산술: 안의 <<는 비트 이동 — 원문 그대로
                end = self.close_end(i + 2, s[i + 1], '}' if s[i + 1] == '{' else ']', quotes=True)
                out.append(s[i:end])
                i, word_start = end, False
                continue
            if not bash and c == '@' and i + 1 < n and (s[i + 1] in self.sq or s[i + 1] in self.dq):
                if (j := self.herestring(i)) is not None:
                    out.append("@''@" if s[i + 1] in self.sq else '@""@')
                    i, word_start = j, False
                    continue
            if c in self.sq or c in self.dq:
                j = self.sq_end(i + 1) if c in self.sq else self.dq_end(i + 1)
                self.quoted.append(s[i + 1:max(j - 1, i + 1)])  # 따옴표 안 글자 (셸 · 실행기에 넘기는 명령 문자열일 수 있다)
                i = j
                out.append("''" if c in self.sq else '""')
                word_start = False
                continue
            if bash and s.startswith('<<<', i):  # here-string: 둘째 '<'에서 heredoc을 찾지 않게 한 번에
                out.append('<<<')
                i, word_start = i + 3, True
                continue
            if bash and s.startswith('<<', i) and (j := self.heredoc(i, out)) is not None:
                i, word_start = j, False
                continue
            if in_paren:
                if c == '(':
                    depth += 1
                elif c == ')':
                    if depth == 0 and not cases:
                        return i, ''.join(out)
                    depth = max(depth - 1, 0)
            out.append(c)
            word_start = c in brk
            i += 1
        return i, ''.join(out)

    def heredoc(self, i: int, out: list[str]) -> int | None:
        """bash heredoc 여는 표시 (<<WORD · <<-WORD). 끝 표시는 메타 문자까지의 낱말 전체에서 따옴표 · \\를 벗긴 것
        (<<E'OF'의 끝 표시는 EOF — bash 실측). 따옴표 · \\가 하나라도 있으면 본문을 펼치지 않는다. 낱말이 없으면 None."""
        s, n = self.s, self.n
        j = i + 2
        tabs = s.startswith('-', j)
        j += 1 if tabs else 0
        while j < n and s[j] in ' \t':
            j += 1
        term, quoted = [], False
        while j < n and s[j] not in BASH_META:
            c = s[j]
            if c == "'":
                end = s.find("'", j + 1)
                end = n if end < 0 else end
                term.append(s[j + 1:end])
                quoted, j = True, end + 1
            elif c == '"':
                k = j + 1
                while k < n and s[k] != '"':
                    if s[k] == '\\' and k + 1 < n and s[k + 1] in '\\"$`':
                        k += 1
                    term.append(s[k])
                    k += 1
                quoted, j = True, k + 1
            elif c == '\\' and j + 1 < n:
                term.append(s[j + 1])
                quoted, j = True, j + 2
            else:
                term.append(c)
                j += 1
        if not term:
            return None
        word = ''.join(term)
        self.pending.append((tabs, word, quoted))
        out.append('<<' + ('-' if tabs else '') + ("''" if quoted else word))
        return j

    def arith_end(self, j: int) -> int:
        """(( … )) · $(( … ))의 닫는 '))' 뒤 위치 (괄호 깊이로). 못 찾으면 끝."""
        return self.close_end(j, '(', ')', depth=2)

    def close_end(self, j: int, opener: str, closer: str, depth: int = 1, quotes: bool = False) -> int:
        """여는 글자 · 닫는 글자 깊이로 짝 닫는 글자 뒤 위치. 못 찾으면 끝 (나머지가 원문 그대로 보이는 글에 들어간다).
        quotes면 bash 따옴표 · \\ 안의 닫는 글자는 세지 않는다 (${a:-'}'})."""
        while j < self.n:
            if quotes and self.s[j] == '\\':
                j += 2
                continue
            if quotes and self.s[j] == "'":
                j = self.sq_end(j + 1)
                continue
            if quotes and self.s[j] == '"':
                j = self.dq_end(j + 1)
                continue
            if self.s[j] == opener:
                depth += 1
            elif self.s[j] == closer:
                depth -= 1
                if depth == 0:
                    return j + 1
            j += 1
        return self.n

    def bodies(self, i: int) -> int:
        """줄 끝: 그 줄에서 연 heredoc 본문을 차례로 읽는다. 끝 표시 줄은 정확히 같아야 한다 (뒤 공백도 안 됨 — bash 실측,
        <<-는 앞 탭만 무시). 끝 표시가 없으면 본문으로 보지 않는다 (뒤의 진짜 명령을 숨기지 않게 — 오탐 쪽으로 틀린다)."""
        pending, self.pending = self.pending, []
        s = self.s
        for tabs, term, quoted in pending:
            start, pos = i, i
            while True:
                if pos >= self.n:
                    self.unterminated = True
                    return i
                eol = s.find('\n', pos)
                eol = self.n if eol < 0 else eol
                line = s[pos:eol]
                if (line.lstrip('\t') if tabs else line) == term:
                    break
                pos = eol + 1
            if quoted:
                self.data.append(s[start:pos])
                self.cuts.append((start, pos))
            else:  # 따옴표 없는 heredoc: 셸이 $( ) · 백틱을 펼친다 — 큰따옴표처럼
                self.expanded.append(s[start:pos])
                self.expansions(start, pos)
            i = min(eol + 1, self.n)
        return i

    def herestring(self, i: int) -> int | None:
        """PowerShell @'…'@ · @"…"@ (여는 표시 뒤 줄바꿈, 닫는 표시는 줄 맨 앞). 아니면 None."""
        quote = self.sq if self.s[i + 1] in self.sq else self.dq
        opened = re.compile(r'[ \t]*\r?\n').match(self.s, i + 2)
        if opened is None:
            return None
        end = re.compile(r'^[' + re.escape(quote) + r']@', re.M).search(self.s, opened.end())
        if end is None:
            return None
        if quote == self.sq:
            self.data.append(self.s[opened.end():end.start()])
            self.cuts.append((opened.end(), end.start()))
        else:
            self.expanded.append(self.s[opened.end():end.start()])
            self.expansions(opened.end(), end.start())
        return end.end()

    def sq_end(self, j: int) -> int:
        while j < self.n:
            if self.s[j] in self.sq:
                if self.shell == PS and j + 1 < self.n and self.s[j + 1] in self.sq:  # PowerShell '' 이스케이프
                    j += 2
                    continue
                return j + 1
            j += 1
        return self.n

    def ansi_end(self, j: int) -> int:
        while j < self.n and self.s[j] != "'":
            j += 2 if self.s[j] == '\\' else 1
        return min(j + 1, self.n)

    def dq_end(self, j: int) -> int:
        """큰따옴표 안: 이스케이프 · 명령 치환을 건너 닫는 따옴표 뒤 위치."""
        s, n = self.s, self.n
        while j < n:
            c = s[j]
            if c == self.esc and j + 1 < n:
                j += 2
                continue
            if self.shell == PS and s.startswith('${', j):  # 중괄호 변수 이름 안의 따옴표는 글자
                j = brace_var_end(s, j)
                continue
            if c in self.dq:
                if self.shell == PS and j + 1 < n and s[j + 1] in self.dq:  # PowerShell "" 이스케이프
                    j += 2
                    continue
                return j + 1
            j = self.substitution(j) or j + 1
        return n

    def expansions(self, j: int, end: int) -> None:
        """셸이 펼치는 본문(따옴표 없는 heredoc · @"…"@)에서 명령 치환만 모은다."""
        while j < end:
            if self.s[j] == self.esc and j + 1 < end:
                j += 2
                continue
            j = self.substitution(j) or j + 1

    def substitution(self, j: int) -> int | None:
        """j에서 명령 치환이 시작되면 inner에 모으고 그 뒤 위치, 아니면 None. $( ) 안은 명령 상태로 다시 읽는다
        (그 안의 heredoc · 따옴표도 셸과 같게 — "$(cat <<'EOF' … EOF )" 커밋 형식)."""
        s = self.s
        if s.startswith('$(', j):
            k, _ = self.command(j + 2, in_paren=True)
            self.inner.append(s[j + 2:k])
            return k + 1
        if self.shell == BASH and s[j] == '`':
            k = s.find('`', j + 1)
            k = self.n if k < 0 else k
            self.inner.append(s[j + 1:k])
            return k + 1
        return None


def brace_var_end(s: str, i: int) -> int:
    """PowerShell ${…} 중괄호 변수 이름의 닫는 '}' 뒤 위치 — 이름 안에서 백틱 + 글자는 글자 하나 (${a`}b}, 실측)."""
    j, n = i + 2, len(s)
    while j < n and s[j] != '}':
        j += 2 if s[j] == '`' else 1
    return min(j + 1, n)


def join_continuations(cmd: str, shell: str) -> str:
    """줄 이음(bash \\ · PowerShell 백틱 + 줄바꿈)을 공백으로 — 명령 이름과 옵션이 다른 줄에 있어도 한 줄로 찾게
    (/self-review security 625b223591 C3: `rm \\⏎ -rf` · `Remove-Item x `⏎ -Recurse`가 거친 탐지를 피했다)."""
    return re.sub(r'\\\r?\n' if shell == BASH else r'`\r?\n', ' ', cmd)


def lex(cmd: str, shell: str) -> Lexed:
    lx = _Lexer(cmd, shell)
    _, visible = lx.command(0)
    return Lexed(visible, lx.inner, lx.data, lx.cuts, lx.expanded, lx.quoted, lx.comments)


def strip_bodies(cmd: str, shell: str = BASH) -> str:
    """셸이 펼치지 않는 본문(따옴표 heredoc <<'EOF' … EOF · PowerShell @' … '@)을 뺀다 — 글 속의 명령 문구를 명령으로 보지 않게.
    따옴표 없는 heredoc · @"…"@은 지우지 않는다 (셸이 그 안의 $( )를 실행한다 — scan이 inner로 모은다).
    여는 표시 · 끝 표시 줄은 남긴다 (다시 scan해도 같은 결과). 끝 표시가 없으면 본문을 지우지 않는다."""
    out, pos = [], 0
    for start, end in lex(cmd, shell).cuts:
        out.append(cmd[pos:start])
        pos = end
    out.append(cmd[pos:])
    return ''.join(out)


def scan(cmd: str, shell: str) -> tuple[str, list[str]]:
    """따옴표 안 · 주석 · heredoc 본문을 비운 텍스트와, 큰따옴표 · 따옴표 없는 heredoc · @"…"@ 안 명령 치환($( ) · bash 백틱)의 내용 목록.
    bash: '…'는 그대로, $'…'와 "…" 안에서는 \\가 이스케이프. PowerShell: '…'(''가 이스케이프, 굽은 따옴표 포함), "…" 안에서는 백틱이 이스케이프.
    주석(낱말 시작의 # · PowerShell <#…#>)은 보이는 글에서 뺀다."""
    lx = lex(cmd, shell)
    return lx.visible, lx.inner


def has_substitution(stripped: str, inner: list[str], shell: str) -> bool:
    return bool(inner) or '$(' in stripped or (shell == BASH and '`' in stripped)


# 따옴표 밖 '&'는 bash에서 붙여 써도 제어 연산자다 (ls&rm … — 실측). 리다이렉트 &> · >& · <& · |&만 뺀다 (/self-review security C1)
CHAIN = re.compile(r'&&|\|\||;|\||\n|(?<![<>&|])&(?![&>])')
CD = re.compile(r'(?:^|[\s;&|(])(?:cd|chdir|Set-Location|sl|pushd|Push-Location|popd|Pop-Location)(?:\s|$)', re.IGNORECASE)


def tokenize(cmd: str, shell: str) -> list[str]:
    """단독 명령 하나를 토큰으로 (따옴표를 벗긴 값). 묶음은 앞에서 막았다고 가정한다."""
    toks: list[str] = []
    i, n = 0, len(cmd)
    esc = '\\' if shell == BASH else '`'
    sq, dq = ("'", '"') if shell == BASH else (PS_SQ, PS_DQ)
    while i < n:
        while i < n and cmd[i] in ' \t\r\n':
            i += 1
        if i >= n:
            break
        if cmd[i] == '#':  # 낱말 시작의 # 부터 줄 끝까지는 주석 (scan과 같은 규칙)
            end = cmd.find('\n', i)
            i = n if end < 0 else end
            continue
        if shell == PS and cmd.startswith('<#', i):  # 블록 주석은 낱말 사이 공백처럼 (scan과 같은 규칙 — 뒤 인자를 버리지 않게)
            end = cmd.find('#>', i + 2)
            i = n if end < 0 else end + 2
            continue
        buf = []
        while i < n and cmd[i] not in ' \t\r\n':
            c = cmd[i]
            if shell == PS and cmd.startswith('${', i):  # 중괄호 변수 이름은 원문 그대로 ($가 남아 셸 변수 검사에 걸린다)
                end = brace_var_end(cmd, i)
                buf.append(cmd[i:end])
                i = end
            elif shell == BASH and cmd.startswith("$'", i):  # ANSI-C 따옴표는 $와 원문 그대로 (펼친 값을 훅이 만들지 않는다)
                j = i + 2
                while j < n and cmd[j] != "'":
                    j += 2 if cmd[j] == '\\' else 1
                buf.append('$' + cmd[i + 2:min(j, n)])
                i = j + 1
            elif c in sq:
                j = i + 1
                while j < n:
                    if cmd[j] in sq:
                        if shell == PS and j + 1 < n and cmd[j + 1] in sq:
                            buf.append("'")
                            j += 2
                            continue
                        break
                    buf.append(cmd[j])
                    j += 1
                i = j + 1
            elif c in dq:
                j = i + 1
                while j < n and cmd[j] not in dq:
                    if cmd[j] == esc and j + 1 < n and (shell == PS or cmd[j + 1] in '\\"$`'):
                        buf.append(cmd[j + 1])
                        j += 2
                        continue
                    buf.append(cmd[j])
                    j += 1
                i = j + 1
            elif c == esc and i + 1 < n:  # 따옴표 밖 이스케이프 (bash \ · PowerShell 백틱 — scan과 같은 규칙)
                buf.append(cmd[i + 1])
                i += 2
            else:
                buf.append(c)
                i += 1
        toks.append(''.join(buf))
    return toks


# heredoc · here-string 본문(글자 그대로의 데이터)에 막을 명령 글자가 있을 때 받아도 되는 명령 — 본문을 실행하지 않는 것만
# (허용 목록: 거부 목록은 tclsh · perl처럼 빠진 실행기로 통과했다. 계획서 2026-10-23_H5-셸-문법-우회 결정, H1 · H2도 같이 —
# 2026-10-23_H1-H2-heredoc-본문과-rm-옵션).
DATA_RECEIVERS = {'cat', 'tee', 'set-content', 'add-content', 'out-file'}
HERESTRING_SLOT = re.compile(r'@(?:\'\'|"")@')
# 범위 · 공급자 접두($env: · $global: 등)는 받지 않는다 — $env:GIT_EDITOR에 넣으면 같은 묶음의 git이 본문을 실행한다
# (/self-review security 625b223591 C2)
PS_ASSIGN = re.compile(r'\$[A-Za-z_]\w*\s*=\s*@(?:\'\'|"")@')
# 파일을 쓰는 데이터 전용 조각 — git commit과 한 묶음이면 훅 파일 · 설정을 써서 같은 호출 안에서 실행될 수 있다 (같은 C2)
WRITERS = {'tee', 'set-content', 'add-content', 'out-file'}
# 데이터 전용 판정을 끄는 글자: 명령 치환 · 서브셸 · 스크립트 블록 · 프로세스 치환 (그 안에서 무엇이 도는지 보지 않는다)
NESTED = re.compile(r'[(){}`]')


# 따옴표 안 글자를 실행하지 않는 읽기 명령 — 따옴표 문자열 검사(readers=True)에서만 받는다 (계획서 2026-10-23_따옴표-명령-허용목록과-perf-ownReview).
# rg --pre는 외부 명령을 실행하므로 뺀다. git grep은 -O(페이저 명령 실행)를 묶음 · 줄임으로도 받아 아예 뺀다 (/self-review security ddb47044d1 C3).
READERS = {'grep', 'egrep', 'fgrep', 'rg', 'select-string', 'sls', 'echo', 'printf', 'write-output', 'write-host'}
# git 읽기 명령도 설정(.git/config의 fsmonitor · diff.external · textconv 등)에 적힌 명령을 실행할 수 있다 — 같은 묶음에 쓰기가 있으면 막는다 (같은 C4)
GIT_READERS = {'log', 'show', 'diff', 'status'}


def data_only(stripped: str, inner: list[str], shell: str, readers: bool = False) -> bool:
    """묶음의 모든 조각(명령 치환 안 포함)이 본문을 실행하지 않는 명령이면 True: cat · tee · git commit ·
    PowerShell Set-Content · Add-Content · Out-File · 변수 대입($m = @'…'@) · here-string 자리표만 있는 조각.
    readers면 읽기 명령(grep · rg · Select-String · echo · git log 등)도 받는다 — 따옴표 문자열 검사용.
    단 파일을 쓰는 조각(리다이렉트 · tee · Set-Content 등 · 대입 · git --output)과 git commit · git 읽기 명령이 한 묶음에 있으면 False.
    stripped · inner는 strip_bodies 뒤 scan 결과."""
    texts = [stripped]
    for text in inner:  # "$(cat <<'EOF' … EOF )" — 안도 같은 규칙. 두 겹 넘는 치환은 NESTED에 걸린다
        visible, deeper = scan(text, shell)
        if deeper:
            return False
        texts.append(visible)
    writes = commits = git_reads = False
    for text in texts:
        if NESTED.search(text):
            return False
        for seg in CHAIN.split(text):
            seg = seg.strip()
            if not seg or HERESTRING_SLOT.fullmatch(seg):
                continue
            if shell == PS and PS_ASSIGN.fullmatch(seg):
                writes = True
                continue
            toks = tokenize(seg, shell)
            if not toks or re.search(r'[\\/]', toks[0]):  # ./cat · tools/tee 같은 경로는 이름만 같은 다른 실행 파일일 수 있다
                return False
            name = re.sub(r'(?i)\.exe$', '', toks[0]).lower()
            while name == 'git' and len(toks) > 2 and toks[1] == '-C':  # git -C <경로> commit — 저장소 위치만 바꾼다 (-c 설정은 그대로 막힘)
                toks = [toks[0]] + toks[3:]
            if name == 'git' and len(toks) > 1 and toks[1] == 'commit':
                commits = True
            elif name in DATA_RECEIVERS:
                writes = writes or name in WRITERS or '>' in seg
            elif readers and name == 'git' and len(toks) > 1 and toks[1] in GIT_READERS:
                git_reads = True
                writes = writes or '>' in seg or any(t.startswith('--output') for t in toks[2:])
            elif readers and name in READERS and not (name == 'rg' and any(t.startswith('--pre') for t in toks[1:])):
                writes = writes or '>' in seg
            else:
                return False
    return not (writes and (commits or git_reads))


def native_path(path: str) -> str:
    """Git Bash 경로(/f/AI/…)를 Windows 경로(F:/AI/…)로. Windows가 아니면 그대로."""
    if os.name == 'nt' and (m := re.match(r'^/([A-Za-z])(/.*)?$', path)) is not None:
        return f'{m.group(1).upper()}:{m.group(2) or "/"}'
    return path


def same_or_inside(child: str, parent: str) -> bool:
    c = os.path.normcase(os.path.abspath(child)).rstrip('\\/')
    p = os.path.normcase(os.path.abspath(parent)).rstrip('\\/')
    return c == p or c.startswith(p + os.sep)
