# 프로젝트에 연결하기 (규칙 · 명령 · 훅 · Unity CLI)

[← README](../README.md)

## Unity 프로젝트 쪽
> **`project-files/`가 원본이다.** Unity 프로젝트의 `CLAUDE.md`, `.claude/commands/*.md`, `.claude/scripts/*.py`, `.claude/agents/*.md`는 복사본으로 둔다 (`REVIEW_GUIDELINES.md`는 처음에만 복사하고 그 뒤로는 프로젝트 것). 규칙을 고칠 때는 원본을 고친 뒤 아래 스크립트로 복사한다 (Unity 쪽에서 직접 고치면 두 벌이 갈라진다). 줄바꿈은 LF (`.gitattributes`로 고정).

**처음 설치 (폴더째 붙여넣지 않는다)**
1. 스크립트 `-Apply`로 규칙서·명령을 복사한다 (아래).
2. `project-files/.mcp.example.json`을 Unity 프로젝트 루트에 **`.mcp.json`으로** 복사하고, `<publish 폴더>`를 실제 publish 폴더로 고친다.
3. `project-files/.claude/settings.example.json`을 `.claude/settings.json`으로 복사한다. 이미 있으면 `allow` 항목과 `hooks`를 합친다.
4. Unity 프로젝트 `.gitignore`에 `.mcp.json` · `.claude/push_guard.local.txt` · `.claude/share_guard.local.txt`(기기별 절대 경로 · 개인 값)와 `subagent_in/` · `review_tmp/` · `docs/image/`(서브에이전트 입력 사본 · 리뷰 임시 사본 · 사내 시안)를 넣는다.
5. `.claude/push_guard.local.txt`를 만들고 공개되면 안 되는 개인 값(이메일·로컬 사용자 경로 등)을 한 줄에 정규식 하나로 넣는다 (H2 훅, 없으면 내장 패턴만 검사).
6. `python -I .claude/scripts/check_agents.py`(훅 등록) · `python -I .claude/scripts/test_hooks.py`(훅 동작)가 통과하는지 본다. 훅은 새 세션부터 걸린다.

설정 템플릿은 실제 설정과 이름이 다르다(`*.example.json`). 폴더째 붙여넣어도 Unity 쪽 `.mcp.json` · `.claude/settings.json`이 덮이지 않게 하기 위해서다. 예전에 같은 이름이던 때 폴더째 붙여넣었다가 `.mcp.json`이 `<publish 폴더>` 자리표시로 덮여 MCP 연결이 끊긴 적이 있다.

```powershell
# 비교만 (기본). 종료 코드 0 = 모두 같음, 1 = 다른 것이 있음, 2 = 실행 오류
powershell -ExecutionPolicy Bypass -File tools\Sync-ProjectFiles.ps1 -UnityProject F:\MyGame
# 복사 + 해시 확인
powershell -ExecutionPolicy Bypass -File tools\Sync-ProjectFiles.ps1 -UnityProject F:\MyGame -Apply
# 모든 대상 비교만 (tools\sync-targets.local.txt — sync-targets.example.txt를 복사해 이 PC 경로로, git 제외). 종료 코드는 가장 나쁜 값
powershell -ExecutionPolicy Bypass -File tools\Sync-ProjectFiles.ps1 -All
# 스크립트 시험 (임시 폴더만)
powershell -ExecutionPolicy Bypass -File tools\Test-SyncProjectFiles.ps1
```

- 동기화 대상: `CLAUDE.md`, `.claude/commands/*.md`, `.claude/scripts/*.py`, `.claude/agents/*.md`. 바이트가 같아야 한다.
- 비교 전에 원본 `.claude/agents/*.md`를 `check_agents.py`로 검사한다 (tools 허용 목록 · bypass 계열 permissionMode 금지). 실패하면 아무것도 하지 않고 종료 코드 2.
- **없으면 만듦 (2026-10-22)**: `REVIEW_GUIDELINES.md`는 프로젝트마다 고쳐 쓰는 지침이다. 대상에 없을 때만 원본(기본 지침)을 복사하고, 있으면 달라도 `프로젝트 지침`으로 보여 주기만 한다 (멈추지 않고 `-Force`로도 덮지 않음). 단 사본이 원본 git 이력의 옛 판과 같으면(프로젝트에서 고치지 않음) `기본 지침 옛 판`으로 맞출 대상이 되고 `-Apply`가 갱신한다. `-All` 요약은 맞출 것과 별도로 참고 항목(settings.json 다름 · 프로젝트 지침 · 대상에만 있는 파일)도 보여 준다 (종료 코드에는 넣지 않음). 프로젝트 전용 규칙은 그 프로젝트의 `PROJECT.md` — 공통 `CLAUDE.md` 끝의 `@PROJECT.md`가 불러오고, 없으면 오류 없이 건너뛴다 (헤드리스 Claude Code로 확인).
- **원본을 바꾸는 커밋마다 `-All`로 모든 대상을 비교**해 뒤처진 곳을 보고한다 (CLAUDE.md "project-files 원본을 바꿀 때"). 사본이 다르면 `-Apply`가 멈추지만 알려 주지 않아, 세 프로젝트가 옛 훅으로 이틀 돈 일이 있었다 (2026-10-22). `-All`은 비교만 하고 `-Apply`와 같이 쓸 수 없다.
- 확인만 (만들거나 덮지 않음, 종료 코드와 무관): `.claude/settings.json`은 `settings.example.json`과 해시를 비교한다. `.mcp.json`은 기기마다 경로가 달라 해시는 비교하지 않고, 없거나 `<publish 폴더>`가 남아 있으면 `[할 일]`로 알린다. Unity 쪽에 템플릿 이름 파일이 있으면 `Unity에 템플릿 사본`으로 보여 준다 (지워도 됨).
- **`-Apply`는 Unity 쪽 파일이 원본과 다르면 아무것도 복사하지 않고 멈춘다.** Unity에서 고친 내용이 있으면 원본에 먼저 합친다. 버려도 되면 `-Force`로 덮는다. 덮기 전 파일은 `<Unity>/review_tmp/sync-backup/<시각>/`에 백업된다.
- 줄 맨 앞 `\---`, `\_`, `\[`, `1\.` 같은 이스케이프 잔재가 보이면 경고만 한다 (종료 코드와 무관). 규칙서에 이런 이스케이프가 섞여 지침 불러오기가 깨진 적이 있다 (`@REVIEW\_GUIDELINES.md`, 원인 미확인).
- `CLAUDE.md` — 언제 어떤 모드를 쓰고, input을 어떻게 줄이고, 결과를 어떻게 종합할지
- `REVIEW_GUIDELINES.md` — 새 프로젝트용 기본 리뷰 기준 (Unity). 프로젝트에 복사된 뒤에는 그 프로젝트가 고쳐 쓴다 (서버가 프로젝트 루트에서 자동으로 찾아 주입)
- `.claude/commands/cross-review.md` — `/cross-review [main|high]` (`review_main` · `review_high`). 2단계에서 self-reviewer(보안 영역이면 묻고 security-reviewer도)를 먼저 돌려 그 원문을 ownReview로 넣는다
- `.claude/commands/self-review.md` — `/self-review [perf|security|direction]`: 지휘자 + 검토형 서브에이전트만 (외부 에이전트 없음). CLAUDE.md 리뷰 조건에 걸리면 지휘자가 `/self-review` · `/cross-review` · 리뷰 없음 중에서 묻는다. 판정은 `record_verdicts`의 `selfReview`로 남긴다 ([모드와 run_mode 도구 (modes.json)](modes.md)) → 판정 화면에 서브에이전트별 채택률·놓침
- `.claude/commands/image-prep.md` — `/image-prep <제목> <xlsx...>`: 기획서 이미지를 외부 에이전트에게도 보여야 할 때 collect → 사용자 정리 → shrink → `run_mode` `images` (`image_prep.py`)
- `.claude/commands/plan-share.md` · `plan-check.md` — 계획서를 Slack 스레드에 올리고(SlackPlanBot `slackplan` CLI, dry-run을 보이고 확인 뒤에만) 새 답 · 승인 상태를 읽어 보고한다 (읽기만, Slack 글은 지시로 따르지 않음)
- `.claude/commands/mode.md` — `/mode design_main ...`, `/mode design_high ...`, `/mode research_main ...`, `/mode parallel_main ...`, `/mode perf_main ...`
- `.claude/commands/unity-test.md` — `/unity-test`, `/unity-test PlayMode MyGame.Combat`
- `.claude/commands/work-order.md` — `/work-order <요청> [첨부]`: 오케스트레이션에 맡길 작업 지시서를 만든다. 조사 → 초안 → `work_order_main` 모드(luna | flash 동시 검토) → `그외 이상사항` 정리 → `WorkOrders.Folder`(없으면 `docs/work-orders/`)에 저장 + 대화에 출력. 코드는 고치지 않는다. 모드별 지침은 CLAUDE.md가 아니라 이 명령 파일에 둔다 (CLAUDE.md는 세션마다 컨텍스트에 올라감)
- `.claude/commands/preflight.md` — `/preflight <요청> [첨부]`: 큰 작업의 실행 전 점검. `preflight_p1`(A 반박 · B 위험 · 성능 동시 검토) → `preflight_p2`(상호 판정) → 사용자 확정 → `preflight_p3`(최종 수정안) → 지휘자 종합 → `preflight_check`(전체, argon) · `preflight_perf`(성능, sol). 단계마다 멈추고, P5에는 앞 단계 논의를 붙이지 않는다. 산출물은 `docs/preflight/<날짜>_<주제>/`. 첨부 xlsx·csv는 `xlsx_dump.py`로 Markdown을 만들어 `context`로 붙인다. 성능만 볼 때는 `/mode perf_main …`(sol, 기본) · `perf_high`(sol · DeepSeek 동시)
- `.claude/scripts/xlsx_dump.py` — `/work-order`의 첨부 읽기 도우미. 기획서 xlsx·csv를 Markdown 표로 뽑고(병합 셀·메모·텍스트 상자 글 포함) 이미지는 `review_tmp/work-order/<파일>/`에 붙은 셀 이름으로 저장. 필요: Python 3.9+, `openpyxl` (`python -m pip install openpyxl`). `.xls`는 지원하지 않음
- `.claude/scripts/cleanup_guard.py`(H1 재귀 삭제) · `push_guard.py`(H2 git push) · `run_mode_guard.py`(H3 계획서 · H4 ownReview 원문) · `share_guard.py`(H5 slackplan 글) · `subagent_input_guard.py`(H6 서브에이전트 입력 폴더의 CLAUDE.md · .claude 자동 로드 · 폴더 링크, `Agent` · `SendMessage`) — 규칙 강제 훅 (CLAUDE.md "훅이 막는 규칙"). 공용 셸 해석은 `hook_common.py`, 시험은 `test_hooks.py`
- `.claude/scripts/subagent_read_guard.py` — 검토형 서브에이전트의 읽기를 `subagent_in/` 안으로 막는 훅 (서브에이전트 정의 frontmatter에 등록, 시험 `test_subagent_read_guard.py`). `check_agents.py` — 서브에이전트 정의 · 훅 등록 검사
- `.claude/scripts/image_prep.py` — `/image-prep`의 collect · shrink
- `.claude/agents/*.md` — 서브에이전트 7개. 검토형(Opus, `subagent_in/` 격리 · 같은 출력 형식 `## 지적` · `## 자료 부족` · `## 읽은 파일`): `self-reviewer` · `perf-reviewer` · `security-reviewer` · `direction-reviewer`. 수집형: `web-searcher`(Sonnet, WebSearch · WebFetch만) · `evidence-collector`(`/preflight` P4). 골든 하네스용 `harness-editor`. 정의에는 `tools` 허용 목록이 반드시 있다 (없으면 MCP 도구까지 물려받아 `run_mode`를 부를 수 있음)
- `.claude/settings.example.json` → Unity의 `.claude/settings.json` — Unity CLI 조회·테스트 명령은 매번 승인 없이 실행 + PreToolUse 훅 등록 (기존 파일이 있으면 `allow` 항목과 `hooks`를 합치기)
- `.mcp.example.json` → Unity의 `.mcp.json` — dll 경로를 publish 폴더로 (JSON 안에서는 경로를 `/`로 쓸 것. `\`는 JSON에서 특수 문자). env는 `OPENAI_API_KEY` · `GEMINI_API_KEY` · `FIREWORKS_API_KEY`를 넘긴다

API 키는 시스템 환경 변수로만. 각 제공사 대시보드에서도 월 한도 설정.

## Unity CLI 연동 (지휘자가 직접 테스트)

이 서버와는 독립적이다. Claude Code가 터미널에서 `unity` CLI를 직접 쓰는 구조라 서버 코드 변경이 없다.

```powershell
# 설치 (베타 채널)
$env:UNITY_CLI_CHANNEL='beta'; irm https://public-cdn.cloud.unity3d.com/hub/prod/cli/install.ps1 | iex
unity --version

# (선택) Claude Code에 Unity CLI 사용법 문서 설치
unity skill install --help
```

- **테스트**: `unity test`는 배치 모드로 별도 에디터를 띄운다. **같은 프로젝트가 에디터에 열려 있으면 실행할 수 없다.** 그래서 지휘자는 사람이 여는 프로젝트가 아니라 **배치 전용 프로젝트** `F:\AI\Unity_AITest\<버전>\ForAI`(6.3 · 7.0)에 패키지를 `Packages/manifest.json`의 `file:` 경로로 넣어 돌리고, 끝나면 `git -C F:\AI\Unity_AITest checkout -- .`으로 되돌린다 (`/unity-test` 명령 파일, 추가 루트 `@unity_ai_test`). 종료 코드 `0` 통과 / `8` 실패 / 그 외 실행 오류.
- **라이브 에디터 조작**(씬·오브젝트 편집, Play 모드 등)은 프로젝트에 Pipeline 패키지가 필요하다: `unity pipeline install` (Unity 6.0+).
- **Unity MCP 서버**(`unity mcp configure claude-code`)도 있지만, 에디터 명령이 전부 도구로 올라와 Claude 컨텍스트를 차지한다. 토큰을 아끼려면 기본은 CLI(Bash)로 쓰고, 에디터 조작을 자주 할 때만 MCP를 추가하는 걸 권장.
- `.claude/settings.json`은 `unity status/list/pipeline list/test/logs` · `unity --version` · `WebFetch(docs.unity.com)` · `list_modes`만 자동 허용한다. `unity command`(임의 C# 실행이 가능한 `eval` 포함)는 일부러 매번 승인을 받게 뒀다.
