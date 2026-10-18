#!/usr/bin/env python3
"""이미지 첨부 준비 도우미 — 기획서 이미지를 사용자가 고르고, 고른 것만 줄여 run_mode images로 보낸다.

사용 (프로젝트 루트에서):
    python .claude/scripts/image_prep.py collect <제목> <파일.xlsx|xlsm> [...]
    python .claude/scripts/image_prep.py shrink docs/image/<날짜>_<제목> [--max 512] [--max-original 768]

collect:
    - 파일마다 xlsx_dump.py를 돌려 docs/image/<날짜>_<제목>/<문서>/에 이미지를 뽑고, 표는 <문서>/<문서>.md로 저장한다.
    - 자동 제외는 지우지 않고 <문서>/_제외/<이유>/로 옮긴다: 시트 이름에 '레퍼런스'·'표지',
      같은 그림의 두 번째 이후 (문서 사이 포함), 긴 변 300px 미만, Claude가 열 수 없는 형식(emf·wmf).
    - index.md에 남은 것과 제외한 것을 적는다. 사용자가 폴더를 보고 필요 없는 것을 지우고,
      글씨를 읽어야 하는 것은 <문서>/원본/으로 옮긴다.
shrink:
    - 사용자가 정리한 뒤 남은 이미지를 긴 변 --max(기본 512)로, <문서>/원본/ 아래 것은 --max-original(기본 768)로 줄인다.
      작은 것은 키우지 않는다. 결과는 _send/<문서>__<파일 이름>.png (매번 _send/를 새로 만든다).
    - _send/manifest.json에 순서·출처·크기·sha256, 화면에 예상 토큰과 run_mode에 넣을 images·context 목록을 출력한다.
실패하면 stderr에 '[오류] …'를 쓰고 종료 코드 2. 필요한 것: Python 3.9+, Pillow, openpyxl (xlsx_dump.py).
"""
from __future__ import annotations

import argparse
import datetime
import hashlib
import json
import math
import re
import shutil
import subprocess
import sys
from pathlib import Path

VIEWABLE = {".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"}
EXCLUDED_DIR = "_제외"
ORIGINAL_DIR = "원본"
SEND_DIR = "_send"
SKIP_SHEET_WORDS = ("레퍼런스", "표지")
MIN_SIDE = 300
MAX_COUNT = 20                 # 서버 Images.MaxCount 기본값과 같게 둔다
GEMINI_TOKENS_PER_IMAGE = 1120  # Gemini 3 표의 기본(HIGH) 값. Gemini 4는 추정


def fail(message: str) -> None:
    print(f"[오류] {message}", file=sys.stderr)
    sys.exit(2)


def progress(message: str) -> None:
    print(f"[진행] {message}", file=sys.stderr, flush=True)


def safe_name(name: str) -> str:
    return re.sub(r"[^\w\-]+", "_", name, flags=re.UNICODE).strip("_") or "file"


def load_pillow():
    try:
        from PIL import Image
    except ImportError:
        fail("Pillow가 없습니다. 'python -m pip install pillow'로 설치해 주세요.")
    return Image


def image_size(Image, path: Path) -> tuple[int, int] | None:
    try:
        with Image.open(path) as im:
            return im.size
    except Exception:   # 깨진 파일·모르는 형식
        return None


def gpt_tokens(w: int, h: int) -> int:
    """GPT 패치 방식 추정: 32px 조각 수 × 1.2 (gpt-5.6 계열 공식 표, 6.x는 같다고 가정)."""
    return round(math.ceil(w / 32) * math.ceil(h / 32) * 1.2)


# ── collect ──────────────────────────────────────────────────────────

def dump_sheets(md: str) -> dict[str, str]:
    """xlsx_dump 출력에서 '이미지 경로 → 시트 이름'. 위치 모름 이미지는 시트가 없다."""
    sheet = ""
    owner: dict[str, str] = {}
    for line in md.splitlines():
        if line.startswith("## 시트: "):
            sheet = re.sub(r" \((hidden|veryHidden)\)$", "", line[len("## 시트: "):])
        elif line.startswith("## 위치를 알 수 없는 이미지"):
            sheet = ""
        elif line.startswith("- ") and ": " in line and sheet:
            path = line.split(": ", 1)[1].removesuffix(" (열 수 없는 형식일 수 있음)")
            owner[Path(path).name] = sheet
    return owner


def move_to(path: Path, doc_dir: Path, reason: str) -> Path:
    target_dir = doc_dir / EXCLUDED_DIR / reason
    target_dir.mkdir(parents=True, exist_ok=True)
    target = target_dir / path.name
    path.replace(target)
    return target


def collect(title: str, files: list[str]) -> None:
    Image = load_pillow()
    dump = Path(__file__).with_name("xlsx_dump.py")
    if not dump.is_file():
        fail(f"'{dump.as_posix()}'가 없습니다 (image_prep.py와 같은 폴더에 있어야 함).")

    root = Path("docs/image") / f"{datetime.date.today():%Y-%m-%d}_{safe_name(title)}"
    if root.exists():
        fail(f"'{root.as_posix()}'가 이미 있습니다. 다른 제목을 쓰거나 폴더를 지운 뒤 다시 실행하세요.")

    seen: dict[str, Path] = {}   # sha256 → 처음 남긴 이미지 (문서 사이 중복 제거)
    report: list[str] = []
    kept_total = 0

    for raw in files:
        src = Path(raw)
        if not src.is_file():
            fail(f"'{raw}' 파일이 없습니다.")
        if src.suffix.lower() not in (".xlsx", ".xlsm"):
            fail(f"'{raw}': xlsx·xlsm만 받습니다.")

        doc_dir = root / safe_name(src.stem)
        if doc_dir.exists():
            fail(f"문서 이름이 겹칩니다: '{doc_dir.name}'. 파일 이름을 바꿔 주세요.")
        progress(f"'{src.name}' 뽑는 중")
        result = subprocess.run(
            [sys.executable, str(dump), str(src), "--out", doc_dir.as_posix()],
            capture_output=True, encoding="utf-8", errors="replace")
        if result.returncode != 0:
            fail(f"xlsx_dump.py 실패 ('{src.name}'): {result.stderr.strip().splitlines()[-1] if result.stderr.strip() else result.returncode}")
        md_path = doc_dir / f"{doc_dir.name}.md"
        md_path.write_text(result.stdout, encoding="utf-8")

        owner = dump_sheets(result.stdout)
        kept: list[str] = []
        excluded: list[str] = []
        for path in sorted(p for p in doc_dir.iterdir() if p.is_file() and p != md_path):
            sheet = owner.get(path.name, "")
            reason = None
            note = ""
            if path.suffix.lower() not in VIEWABLE:
                reason = "형식"
            elif any(word in sheet for word in SKIP_SHEET_WORDS):
                reason = "시트"
                note = f" (시트 '{sheet}')"
            else:
                digest = hashlib.sha256(path.read_bytes()).hexdigest()
                size = image_size(Image, path)
                if digest in seen:
                    reason = "중복"
                    note = f" (= {seen[digest].as_posix()})"
                elif size is None:
                    reason = "형식"
                elif max(size) < MIN_SIDE:
                    reason = "작음"
                    note = f" ({size[0]}x{size[1]})"
                else:
                    seen[digest] = path
                    kept.append(f"- `{path.name}` {size[0]}x{size[1]}" + (f" · 시트 '{sheet}'" if sheet else " · 위치 모름"))
                    continue
            moved = move_to(path, doc_dir, reason)
            excluded.append(f"- `{moved.relative_to(doc_dir).as_posix()}` — {reason}{note}")

        kept_total += len(kept)
        report += [f"## {doc_dir.name}", "", f"원본: `{src.as_posix()}` · 표: `{md_path.name}`", "",
                   f"### 남은 이미지 ({len(kept)})", ""] + (kept or ["(없음)"]) + \
                  ["", f"### 자동 제외 ({len(excluded)}) — 잘못 걸렀으면 위 폴더로 다시 옮기면 된다", ""] + (excluded or ["(없음)"]) + [""]

    head = [
        f"# 이미지 첨부 준비: {title}", "",
        "1. 이 폴더를 보고 **보낼 필요 없는 이미지는 지운다.**",
        f"2. 글씨를 직접 읽어야 하는 이미지(흐름도, 표 캡처 등)는 `<문서>/{ORIGINAL_DIR}/`로 옮긴다 (768px로 보냄). 나머지는 512px.",
        "3. 끝나면 지휘자에게 알린다 → `image_prep.py shrink`로 줄여 `_send/`를 만든다.", "",
        f"남은 이미지 {kept_total}장 (한 번에 보낼 수 있는 상한 {MAX_COUNT}장). "
        "표의 이미지 경로는 뽑은 직후 위치라 옮긴 파일과 다를 수 있다 — 짝짓기는 파일 이름의 시트·셀로 한다.", "",
    ]
    (root / "index.md").write_text("\n".join(head + report), encoding="utf-8")
    print(f"폴더: {root.as_posix()}")
    print(f"목록: {(root / 'index.md').as_posix()}")
    print(f"남은 이미지: {kept_total}장")


# ── shrink ───────────────────────────────────────────────────────────

def shrink(folder: str, max_side: int, max_original: int) -> None:
    Image = load_pillow()
    root = Path(folder)
    if not root.is_dir():
        fail(f"'{folder}' 폴더가 없습니다.")

    send = root / SEND_DIR
    if send.exists():
        shutil.rmtree(send)   # 매번 새로 만든다 (지난 결과가 섞이지 않게)
    send.mkdir()

    entries = []
    tables = []
    for doc_dir in sorted(p for p in root.iterdir() if p.is_dir() and p.name not in (SEND_DIR, EXCLUDED_DIR)):
        tables += [p for p in doc_dir.glob("*.md")]
        sources = [(p, max_side) for p in sorted(doc_dir.iterdir()) if p.is_file() and p.suffix.lower() in VIEWABLE]
        original = doc_dir / ORIGINAL_DIR
        if original.is_dir():
            sources += [(p, max_original) for p in sorted(original.iterdir()) if p.is_file() and p.suffix.lower() in VIEWABLE]

        for src, limit in sources:
            try:
                with Image.open(src) as im:
                    im.load()
                    w, h = im.size
                    scale = min(1.0, limit / max(w, h))
                    out = im if scale == 1.0 else im.resize((max(1, round(w * scale)), max(1, round(h * scale))), Image.LANCZOS)
                    if out.mode not in ("RGB", "RGBA", "L", "LA", "P"):
                        out = out.convert("RGBA" if "A" in out.mode else "RGB")
                    target = send / f"{doc_dir.name}__{src.stem}.png"
                    out.save(target, "PNG", optimize=True)
                    sent = out.size
            except Exception as ex:
                fail(f"'{src.as_posix()}' 줄이기 실패: {type(ex).__name__}: {ex}")
            data = target.read_bytes()
            entries.append({
                "send": target.as_posix(),
                "source": src.as_posix(),
                "original": [w, h],
                "sent": list(sent),
                "bytes": len(data),
                "sha256": hashlib.sha256(data).hexdigest(),
            })

    if not entries:
        fail(f"'{folder}'에 보낼 이미지가 없습니다.")
    (send / "manifest.json").write_text(json.dumps(entries, ensure_ascii=False, indent=2), encoding="utf-8")

    gpt = sum(gpt_tokens(*e["sent"]) for e in entries)
    print(f"보낼 이미지: {len(entries)}장 → {send.as_posix()}")
    if len(entries) > MAX_COUNT:
        print(f"[주의] 서버 상한 {MAX_COUNT}장을 넘습니다. 더 지우거나 나눠서 보내세요.")
    print(f"예상 이미지 토큰 (에이전트 1개 기준, 추정): GPT 약 {gpt:,} · Gemini 약 {len(entries) * GEMINI_TOKENS_PER_IMAGE:,}")
    print("\nrun_mode images:")
    print(json.dumps([e["send"] for e in entries], ensure_ascii=False))
    if tables:
        print("\ncontext로 붙일 표 (줄 수):")
        for t in tables:
            lines = len(t.read_text(encoding="utf-8").splitlines())
            print(json.dumps({"path": t.as_posix(), "startLine": 1, "endLine": lines}, ensure_ascii=False))


def main() -> None:
    for stream in (sys.stdout, sys.stderr):   # Windows 콘솔 기본 인코딩(cp949) 회피
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")

    parser = argparse.ArgumentParser(description="이미지 첨부 준비: collect(뽑기·자동 제외) → 사용자 정리 → shrink(줄이기).")
    sub = parser.add_subparsers(dest="command", required=True)
    c = sub.add_parser("collect", help="기획서에서 이미지를 뽑아 docs/image/<날짜>_<제목>/에 둔다")
    c.add_argument("title")
    c.add_argument("files", nargs="+")
    s = sub.add_parser("shrink", help="정리한 폴더의 이미지를 줄여 _send/에 둔다")
    s.add_argument("folder")
    s.add_argument("--max", type=int, default=512)
    s.add_argument("--max-original", type=int, default=768)
    args = parser.parse_args()

    if args.command == "collect":
        collect(args.title, args.files)
    else:
        if args.max < 32 or args.max_original < 32:
            fail("--max·--max-original은 32 이상이어야 합니다.")
        shrink(args.folder, args.max, args.max_original)


if __name__ == "__main__":
    main()
