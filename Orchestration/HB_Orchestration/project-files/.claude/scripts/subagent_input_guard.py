"""PreToolUse 훅 H6 (Agent · Task · SendMessage): 서브에이전트 입력 폴더 subagent_in/에 하네스가 자동으로 불러오는 파일이 있으면 호출을 막는다
(/self-review security 78e1a3aaca 반영).

- 서브에이전트가 폴더의 파일을 읽으면 하네스가 그 폴더의 CLAUDE.md를 컨텍스트에 붙인다 (nested_memory, 카나리아로 확정).
  도구 호출이 아니라 subagent_read_guard(Read·Grep·Glob)로는 볼 수 없다 → 부르는 순간(이어 부르는 SendMessage 포함) 폴더 상태를 본다.
- 위임 메시지(prompt · message)에 'subagent_in'이 (대소문자 무시, 어디든) 없으면 통과 — subagent_in을 쓰지 않는 호출 (사용자 결정).
- 있으면 실행 폴더 하나가 아니라 subagent_in/ 전체를 직접 순회한다. 서브에이전트는 subagent_in/ 안 어디든 읽을 수 있고(읽기 차단 훅의 허용 범위),
  경로 해석(붙여 쓴 콜론 · 대소문자 · 한국어 조사)에 기대면 빗나간다.
  - 막는 이름 (대소문자 무시, 파일 · 폴더 구분 없이): CLAUDE.md · CLAUDE.local.md · .claude
  - 폴더를 가리키는 심볼릭 링크 · 정션(reparse point)도 막는다 — 그 안의 CLAUDE.md를 순회로 볼 수 없다
    (Python 3.10의 os.walk는 Windows 정션 안으로 들어간다). 파일 링크는 이름만 본다 (클라우드 자리표시 파일 오탐 방지).
  - 못 여는 폴더는 예외 → 막는다.
- 경로 해석은 '이 프로젝트 subagent_in/ 밖' 검사에만 쓴다 (다른 프로젝트 · ..): 밖이면 막는다.
- 실패하면 막는다: 통과 판정일 때만 exit 0, 그 밖(예외 포함)은 exit 2. python이 없거나 스크립트를 못 찾으면 훅 명령의 `|| exit 2`가 막는다.
- 검사 뒤에 생긴 파일은 못 막는다 — 부르기 전에 복사를 끝내고, 도는 동안 subagent_in/에 복사하지 않는다 (CLAUDE.md 서브에이전트 입력 규칙).

입력: stdin JSON {"tool_name": "Agent" | "Task" | "SendMessage", "tool_input": {"prompt" | "message": "...", ...}}.
exit 2 + stderr면 Claude Code가 이유를 지휘자에게 돌려준다.
"""
import json
import os
import re
import stat
import sys

INPUT_DIR = 'subagent_in'
BLOCKED_NAMES = {'claude.md', 'claude.local.md', '.claude'}
MENTION = re.compile(r'subagent_in', re.IGNORECASE)
# 경로 낱말: (드라이브) (첫 구성 요소) (/구성 요소)* / subagent_in (/나머지). 구성 요소에는 공백 · 따옴표 · 괄호 · 꺾쇠 · 콜론이 없다.
# 구분자가 첫 구성 요소 뒤에는 반드시 있어 같은 글자를 여러 방법으로 나누지 않는다 (되추적 폭발 방지).
SEG = r'[^\s\\/`\'"()<>\[\]|:*?]+'
PATH = re.compile(rf'(?P<pre>(?:[A-Za-z]:)?(?:{SEG})?(?:[\\/]{SEG})*[\\/])?(?<!\w)subagent_in(?!\w)(?P<rest>[\\/][^\s`\'"()<>\[\]|*?]*)?',
                  re.IGNORECASE)
GIT_BASH_DRIVE = re.compile(r'^/([A-Za-z])/')


def deny(reason: str) -> None:
    print(f'[입력 차단] {reason}', file=sys.stderr)
    sys.exit(2)


def project_root() -> str:
    return os.environ.get('CLAUDE_PROJECT_DIR') or os.getcwd()


def norm(path: str) -> str:
    return os.path.normcase(os.path.realpath(path))


def inside(path: str, base: str) -> bool:
    return path == base or path.startswith(base + os.sep)


def texts(ti: dict) -> list:
    """검사할 글: Agent · Task의 prompt, SendMessage의 message. 문자열이 아니면 JSON 글로 바꿔 본다 (형식이 바뀌어도 언급을 놓치지 않게)."""
    out = []
    for key in ('prompt', 'message'):
        value = ti.get(key)
        if value is None:
            continue
        out.append(value if isinstance(value, str) else json.dumps(value, ensure_ascii=False))
    return out


def mentioned_paths(text: str):
    """글 속 subagent_in 경로마다 (subagent_in 폴더 경로 글자, 실행 폴더 이름 또는 '')."""
    for m in PATH.finditer(text):
        pre = m.group('pre') or ''
        if os.name == 'nt':
            pre = GIT_BASH_DRIVE.sub(lambda d: d.group(1) + ':/', pre)  # Git Bash /f/AI/… → F:/AI/…
        parts = [p for p in re.split(r'[\\/]+', m.group('rest') or '') if p]
        run = parts[0] if parts else ''
        if run.strip('.'):  # 문장 끝 구두점(x. · x:)만 뗀다 — '..'는 그대로 둬야 밖으로 나가는 경로가 잡힌다
            run = run.rstrip('.,:;')
        yield pre + INPUT_DIR, run


def is_dir_link(entry: os.DirEntry) -> bool:
    """폴더를 가리키는 심볼릭 링크 또는 정션(reparse point인 폴더)."""
    if entry.is_symlink():
        return entry.is_dir(follow_symlinks=True)
    attrs = getattr(entry.stat(follow_symlinks=False), 'st_file_attributes', 0)
    return entry.is_dir(follow_symlinks=False) and bool(attrs & stat.FILE_ATTRIBUTE_REPARSE_POINT)


def scan(base: str) -> None:
    """subagent_in/ 전체를 직접 순회한다. 막을 것이 있으면 deny. 못 여는 폴더는 예외가 올라가 막힌다."""
    stack = [base]
    while stack:
        with os.scandir(stack.pop()) as it:
            for entry in it:
                if entry.name.lower() in BLOCKED_NAMES:
                    deny(f"'{entry.path}': 서브에이전트가 이 폴더의 파일을 읽을 때 하네스가 자동으로 불러오는 이름이다 (omitClaudeMd 격리가 깨진다). "
                         f"검토 대상이면 CLAUDE.md.txt처럼 다른 이름으로 복사하고, 필요 없으면 지운다. ({INPUT_DIR}/ 전체를 검사한다)")
                if is_dir_link(entry):
                    deny(f"'{entry.path}': 폴더 링크 · 정션은 안을 검사할 수 없다. 링크가 아니라 내용을 복사한다.")
                if entry.is_dir(follow_symlinks=False):
                    stack.append(entry.path)


def check(data: dict) -> None:
    found = [t for t in texts(data.get('tool_input') or {}) if MENTION.search(t)]
    if not found:
        return
    root = project_root()
    base = norm(os.path.join(root, INPUT_DIR))
    for text in found:
        for dir_text, run in mentioned_paths(text):
            dir_full = dir_text if os.path.isabs(dir_text) else os.path.join(root, dir_text)
            target = norm(os.path.join(dir_full, run) if run else dir_full)
            if not inside(target, base):
                deny(f"'{os.path.join(dir_text, run)}'은(는) 이 프로젝트의 {INPUT_DIR}/ 밖이다. 서브에이전트 입력은 {base} 안에 둔다.")
    if os.path.isdir(base):
        scan(base)


def main() -> None:
    try:
        sys.stderr.reconfigure(encoding='utf-8')  # Windows 콘솔 기본(cp949)이면 한글 이유가 깨진다
        check(json.loads(sys.stdin.buffer.read().decode('utf-8-sig')))  # cp949 기본 stdin이면 한글이 깨진다
    except SystemExit:
        raise
    except Exception as ex:  # 실패하면 막는다
        deny(f'검사 중 오류 ({type(ex).__name__}: {ex}).')
    sys.exit(0)


if __name__ == '__main__':
    main()
