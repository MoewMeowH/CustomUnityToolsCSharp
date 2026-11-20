"""Stop 훅 H11: 턴을 끝내기 전에 남은 정리 · 기록을 지휘자에게 알린다. 경고 단계 — 알림이 있으면 한 번 더 돌게 하고(decision block), 같은 내용은 세션에 한 번만.

알리는 것:
- (a) 프로젝트 루트 subagent_in/ · review_tmp/에 이 세션이 시작된 뒤 생기거나 바뀐 것 — 백그라운드 작업(background_tasks)이 없을 때만
  (도는 동안은 쓰는 중일 수 있다). 다른 세션이 쓰는 것 · 오래 둔 실측 폴더는 알리지 않는다.
  CLAUDE.md "diff는 파일로 … 리뷰가 끝나면 지운다" · /self-review 서브에이전트 입력 정리.
- (b) SubagentStop 훅(H10)이 쌓아 둔 서브에이전트 결과 형식 위반.
- (c) 이 세션의 run_mode 실행 중 결과에 지적 ID(1-F2) · 자료 요청 ID(1-N1)가 있는데 판정(성공한 record_verdicts · verdicts.jsonl)이 없는 runId.
  결과는 바로 온 것 · 큰 결과 미리보기 · 백그라운드 알림(task-notification)을 모두 본다 (서버 ClaudeTranscripts와 같은 규칙).
  지적이 없던 실행은 남길 것이 없으므로 알리지 않는다 (/mode "결과 종합" 5).
같은 알림 내용(해시)은 세션마다 한 번만 낸다 — Stop은 턴마다(백그라운드 완료 턴 포함) 온다 (실측 2026-11-20 claude 2.1.329).
stop_hook_active면(이 훅이 막아 한 번 더 돈 뒤) 아무것도 하지 않는다 — 고리를 만들지 않는다.
실패해도 막지 않는다: 늘 exit 0 (알림이 있을 때만 stdout JSON).

입력: stdin JSON {"hook_event_name": "Stop", "session_id", "stop_hook_active", "background_tasks", "transcript_path", "cwd", ...}.
"""
import datetime
import hashlib
import json
import os
import re
import sys

sys.dont_write_bytecode = True  # 훅 폴더에 __pycache__를 남기지 않는다 (저장소에 섞이지 않게)
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # -I는 스크립트 폴더를 경로에 넣지 않는다
from hook_common import log_warning, read_input, session_file, state_dir, take_pending  # noqa: E402

LEFTOVER_DIRS = ('subagent_in', 'review_tmp')
FINDING_ID = re.compile(r'\b\d+-[FN]\d+\b')
# 서버 ClaudeTranscripts와 같은 규칙: 결과 첫 줄 전체가 머리말일 때만 · 판정 runId 모양 · 판정 실패 접두어 · 백그라운드 안내 · 알림 블록 · 큰 결과 미리보기
RUN_ID = re.compile(r'^runId: ([0-9a-f]{10})\r?$')
VERDICT_RUN_ID = re.compile(r'^[0-9a-f]{10}$')
CREATED_RUN = re.compile(r'기록 runId: ([0-9a-f]{10})')
VERDICT_FAIL = '[판정 기록 불가]'
BACKGROUND = re.compile(r'\A\s*MCP tool "[^"\r\n]*run_mode"[^\r\n]*?moved to the background as task (\w+)')
NOTIFICATION = re.compile(r'<task-notification>.*?</task-notification>', re.S)
TASK_ID = re.compile(r'<task-id>(\w+)</task-id>')
RESULT_START = re.compile(r'<result>\r?\n?')
PREVIEW_START = re.compile(r'Preview[^\n]*:\r?\n')
# 줄 전체를 json으로 풀기 전에 거른다 (긴 세션 기록을 턴마다 읽음)
INTERESTING = ('"tool_use"', '"tool_result"', '<task-notification>')


def session_start(transcript: str) -> float | None:
    """세션 기록에서 처음 나오는 timestamp (세션 시작). 못 찾으면 None."""
    try:
        with open(transcript, encoding='utf-8', errors='replace') as f:
            for _, line in zip(range(50), f):
                try:
                    ts = json.loads(line).get('timestamp')
                except (ValueError, AttributeError):
                    continue
                if isinstance(ts, str):
                    return datetime.datetime.fromisoformat(ts.replace('Z', '+00:00')).timestamp()
    except (OSError, ValueError):
        pass
    return None


def leftovers(root: str, since: float) -> list[str]:
    """subagent_in/ · review_tmp/ 바로 아래에서 이 세션이 시작된 뒤에 생기거나 바뀐 것 — 다른 세션이 쓰는 것 · 오래 둔 실측 폴더는 알리지 않는다."""
    found = []
    for name in LEFTOVER_DIRS:
        d = os.path.join(root, name)
        try:
            entries = sorted(e for e in os.listdir(d) if e != '.gitkeep' and os.path.getmtime(os.path.join(d, e)) >= since)
        except OSError:
            continue
        if entries:
            shown = ', '.join(entries[:5]) + (f' 외 {len(entries) - 5}개' if len(entries) > 5 else '')
            found.append(f'{name}/에 {shown}')
    return found


def blocks(message: dict) -> list[dict]:
    content = message.get('content')
    return [b for b in content if isinstance(b, dict)] if isinstance(content, list) else []


def result_text(block: dict) -> str:
    content = block.get('content')
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        return ''.join(c.get('text') or '' for c in content if isinstance(c, dict))
    return ''


def after(text: str, start: re.Pattern) -> str | None:
    m = start.search(text)
    return text[m.end():] if m else None


def session_runs(transcript: str) -> tuple[dict[str, str], set[str]]:
    """세션 기록에서 (지적 · 요청이 있는 run_mode runId → 모드, 판정을 남긴 runId).
    run_mode 결과는 바로 온 것 · 큰 결과 미리보기(persisted) · 백그라운드로 넘어가 나중에 온 알림(task-notification)을 모두 본다.
    record_verdicts는 결과가 성공일 때만 판정으로 센다."""
    modes: dict[str, str] = {}          # run_mode tool_use id → 모드
    tasks: dict[str, str] = {}          # 백그라운드 task id → 모드
    verdict_uses: dict[str, str | None] = {}
    runs: dict[str, str] = {}
    judged: set[str] = set()

    def resolve(body: str | None, mode: str) -> None:
        if body is None:
            return
        m = RUN_ID.match(body.lstrip().split('\n', 1)[0])
        if m and FINDING_ID.search(body):
            runs[m.group(1)] = mode

    with open(transcript, encoding='utf-8', errors='replace') as f:
        for line in f:
            if not any(k in line for k in INTERESTING):
                continue
            try:
                row = json.loads(line)
            except ValueError:
                continue
            msg = row.get('message') if isinstance(row, dict) else None
            if not isinstance(msg, dict):
                continue
            if isinstance(msg.get('content'), str):
                for note in NOTIFICATION.findall(msg['content']):
                    head = note[:m.start()] if (m := RESULT_START.search(note)) else note
                    if (t := TASK_ID.search(head)) and t.group(1) in tasks:
                        resolve(after(note, RESULT_START), tasks.pop(t.group(1)))
                continue
            for b in blocks(msg):
                if b.get('type') == 'tool_use':
                    name = b.get('name') or ''
                    inp = b.get('input') if isinstance(b.get('input'), dict) else {}
                    if name.endswith('__run_mode'):
                        modes[b.get('id') or ''] = str(inp.get('mode') or '')
                    elif name.endswith('__record_verdicts'):
                        run = str(inp.get('runId') or '').strip()
                        verdict_uses[b.get('id') or ''] = run if VERDICT_RUN_ID.match(run) else None
                elif b.get('type') == 'tool_result':
                    use = b.get('tool_use_id')
                    text = result_text(b)
                    if use in verdict_uses:
                        if not b.get('is_error') and not text.lstrip().startswith(VERDICT_FAIL):
                            if verdict_uses[use]:
                                judged.add(verdict_uses[use])
                            judged.update(CREATED_RUN.findall(text))
                    elif use in modes:
                        if not RUN_ID.match(text.split('\n', 1)[0]) and (moved := BACKGROUND.match(text)):
                            tasks[moved.group(1)] = modes[use]
                        elif '<persisted-output>' in text:
                            resolve(after(text, PREVIEW_START), modes[use])
                        else:
                            resolve(text, modes[use])
    return runs, judged


def verdict_runs() -> set[str]:
    path = os.environ.get('HB_ORCH_VERDICTS_PATH') or os.path.join(state_dir(), 'verdicts.jsonl')
    found = set()
    try:
        with open(path, encoding='utf-8') as f:
            for line in f:
                try:
                    run = json.loads(line).get('runId')
                except (ValueError, AttributeError):
                    continue
                if run:
                    found.add(str(run).strip())
    except OSError:
        pass
    return found


def unjudged(transcript: str) -> list[str]:
    if not transcript or not os.path.isfile(transcript):
        return []
    runs, judged = session_runs(transcript)
    if not runs:
        return []
    judged |= verdict_runs()
    return [f'{run}({mode})' for run, mode in runs.items() if run not in judged]


def alerts(data: dict) -> list[tuple[str, str]]:
    """(종류, 글) 목록. 종류가 빈 것은 이미 다른 훅이 경고 기록에 남긴 것 (H10).
    H10이 쌓은 글은 다시 계산할 수 없으므로 다른 계산(실패할 수 있음)을 모두 마친 뒤 맨 마지막에 꺼낸다."""
    out: list[tuple[str, str]] = []
    root = os.environ.get('CLAUDE_PROJECT_DIR') or data.get('cwd') or os.getcwd()
    transcript = data.get('transcript_path') or ''
    if not data.get('background_tasks') and (since := session_start(transcript)) is not None:
        if left := leftovers(root, since):
            out.append(('leftover', '이 세션에서 만든 리뷰 · 서브에이전트 임시 파일이 남았다: ' + '; '.join(left)
                        + ' — 리뷰가 끝났으면 지운다 (CLAUDE.md "diff는 파일로" · /self-review 입력 정리). 아직 쓰는 중이면 그대로 둔다.'))
    if runs := unjudged(transcript):
        out.append(('unjudged-run', '지적 · 자료 요청이 있는데 판정을 남기지 않은 실행: ' + ', '.join(runs)
                    + ' — 결과 종합 뒤 record_verdicts로 남긴다 (/mode "결과 종합" 5 · 6). 아직 종합 전이면 그대로 둔다.'))
    for text in take_pending(data.get('session_id') or ''):
        out.append(('', text))
    return out


def main() -> None:
    try:
        data = read_input()
        if data.get('stop_hook_active'):
            sys.exit(0)
        found = alerts(data)
        if not found:
            sys.exit(0)
        session = data.get('session_id') or ''
        seen_path = session_file(session, 'alerted.json')
        try:
            with open(seen_path, encoding='utf-8') as f:
                seen = set(json.load(f))
        except (OSError, ValueError, TypeError):
            seen = set()
        new = [(k, t) for k, t in found if hashlib.sha256(t.encode('utf-8')).hexdigest()[:16] not in seen]
        if not new:
            sys.exit(0)
        for kind, text in new:
            if kind:
                log_warning('H11', kind, text)
            seen.add(hashlib.sha256(text.encode('utf-8')).hexdigest()[:16])
        os.makedirs(os.path.dirname(seen_path), exist_ok=True)
        with open(seen_path, 'w', encoding='utf-8') as f:
            json.dump(sorted(seen), f)
        reason = '[H11 턴 끝 알림] ' + ' / '.join(t for _, t in new) + ' (이 알림은 같은 내용으로 다시 오지 않는다.)'
        sys.stdout.reconfigure(encoding='utf-8')
        print(json.dumps({'decision': 'block', 'reason': reason}, ensure_ascii=False))
    except SystemExit:
        raise
    except Exception:  # 경고 훅은 막지 않는다
        pass
    sys.exit(0)


if __name__ == '__main__':
    main()
