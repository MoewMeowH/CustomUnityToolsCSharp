"""서브에이전트 입력 파일을 subagent_in/<실행>/에 복사하고 SHA-256으로 확인한다 (손으로 복사 · 해시 비교 · 이름 바꾸기를 하던 절차를 한 줄로).

사용: python -I .claude/scripts/subagent_copy.py --out subagent_in/<날짜>_<주제> [--agent <서브에이전트 이름>] 파일...
  → 파일마다 '이름  sha256 앞 12자  원본'을 내고, 하나라도 실패하면 이유를 내고 exit 1.

- 파일은 이름만 따서 --out 바로 아래에 둔다 (폴더 구조를 옮기려면 subagent_copy_tracked.py). 같은 이름이 둘이면 실패.
- 하네스가 자동으로 불러오는 이름은 바꾼다: CLAUDE.md · CLAUDE.local.md → 뒤에 .txt (subagent_input_guard H6이 막는 이름).
- --agent를 주면 .claude/mistakes/<이름>.md(이 스크립트 옆 ../mistakes)에 주석 밖 '- ' 항목이 있을 때만 _SUBAGENT_MISTAKES.md로 함께 복사한다
  (검토 대상 MISTAKES.md와 이름이 겹치지 않게).
- --out은 경로에 subagent_in 폴더가 있어야 하고 '..'을 쓸 수 없다. 없으면 만든다. 이미 있는 같은 이름 파일은 덮지 않고 실패.
- 복사 뒤 원본과 사본의 SHA-256을 비교한다. 도중에 실패하면 이번에 만든 사본을 지운다.
"""
import argparse
import hashlib
import os
import re
import shutil
import sys

sys.dont_write_bytecode = True

RENAME = {'claude.md', 'claude.local.md'}
# 시험은 HB_SUBAGENT_MISTAKES_DIR로 임시 폴더를 쓴다 (동기화 원본 폴더에 시험 파일을 쓰지 않게)
MISTAKES_DIR = os.environ.get('HB_SUBAGENT_MISTAKES_DIR') or os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'mistakes')
COMMENT = re.compile(r'<!--.*?-->', re.S)


def sha256(path: str) -> str:
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()


def mistakes_file(agent: str) -> str | None:
    if not re.fullmatch(r'[A-Za-z0-9_-]+', agent):
        raise SystemExit(f'[실패] 서브에이전트 이름이 이상하다: {agent}')
    path = os.path.join(MISTAKES_DIR, f'{agent}.md')
    if not os.path.isfile(path):
        return None
    with open(path, encoding='utf-8') as f:
        body = COMMENT.sub('', f.read())
    return path if any(l.startswith('- ') for l in body.splitlines()) else None


def plan(out: str, files: list[str], agent: str | None) -> list[tuple[str, str]]:
    """(원본, 사본 이름) 목록. 문제가 있으면 SystemExit."""
    parts = out.replace('\\', '/').split('/')
    if 'subagent_in' not in parts or '..' in parts:
        raise SystemExit(f"[실패] --out은 subagent_in/ 아래여야 하고 '..'을 쓸 수 없다: {out}")
    pairs = []
    for src in files:
        if not os.path.isfile(src):
            raise SystemExit(f'[실패] 파일이 없다 (폴더는 subagent_copy_tracked.py): {src}')
        name = os.path.basename(src)
        pairs.append((src, name + '.txt' if name.lower() in RENAME else name))
    if agent and (m := mistakes_file(agent)):
        pairs.append((m, '_SUBAGENT_MISTAKES.md'))
    names = [n.lower() for _, n in pairs]
    if dup := sorted({n for n in names if names.count(n) > 1}):
        raise SystemExit(f'[실패] 사본 이름이 겹친다: {", ".join(dup)} — 따로 복사하거나 이름을 바꿔 둔 파일을 넘긴다.')
    for _, name in pairs:
        if os.path.exists(os.path.join(out, name)):
            raise SystemExit(f'[실패] 이미 있다 (덮지 않음): {os.path.join(out, name)}')
    return pairs


def main() -> None:
    sys.stdout.reconfigure(encoding='utf-8')
    sys.stderr.reconfigure(encoding='utf-8')
    ap = argparse.ArgumentParser()
    ap.add_argument('--out', required=True)
    ap.add_argument('--agent')
    ap.add_argument('files', nargs='+')
    a = ap.parse_args()
    made: list[str] = []
    try:
        pairs = plan(a.out, a.files, a.agent)
        os.makedirs(a.out, exist_ok=True)
        for src, name in pairs:
            dst = os.path.join(a.out, name)
            made.append(dst)
            shutil.copyfile(src, dst)
            want, got = sha256(src), sha256(dst)
            if want != got:
                raise SystemExit(f'[실패] SHA-256이 다르다: {src} {want[:12]} → {dst} {got[:12]}')
            print(f'{name}  {got[:12]}  {src}')
    except (SystemExit, OSError) as ex:
        for dst in made:  # 반쪽 사본을 남기지 않는다 (이번에 만든 파일만 — plan이 이미 있는 이름을 거절하므로 남의 파일은 없다)
            try:
                os.remove(dst)
            except OSError:
                pass
        print(ex.code if isinstance(ex, SystemExit) and isinstance(ex.code, str) else f'[실패] {ex}', file=sys.stderr)
        sys.exit(1)
    print(f'복사 {len(pairs)}개 · SHA-256 모두 같음 → {a.out}')


if __name__ == '__main__':
    main()
