# 프로젝트에 연결하기 (규칙 · 명령 · 훅 · Unity CLI)

[← README](../README.md)

## Unity 프로젝트 쪽

> **`project-files/`가 원본입니다.**
> - Unity 프로젝트의 `CLAUDE.md`, `.claude/commands/*.md`, `.claude/scripts/*.py`, `.claude/agents/*.md`는 복사본으로 둡니다.
> - `REVIEW_GUIDELINES.md`는 처음에만 복사하고, 그 뒤로는 프로젝트 것입니다.
> - 규칙을 고칠 때는 원본을 고친 뒤 아래 스크립트로 복사합니다. Unity 쪽에서 직접 고치면 두 벌이 갈라집니다.
> - 줄바꿈은 LF입니다 (`.gitattributes`로 고정).

### 처음 설치

폴더째 붙여넣지 않습니다.

1. 스크립트 `-Apply`로 규칙서 · 명령을 복사합니다 ([아래](#동기화-스크립트-sync-projectfilesps1)).
2. `project-files/.mcp.example.json`을 Unity 프로젝트 루트에 **`.mcp.json`으로** 복사하고, `<publish 폴더>`를 실제 publish 폴더로 고칩니다.
3. `project-files/.claude/settings.example.json`을 `.claude/settings.json`으로 복사합니다. 이미 있으면 `allow` 항목과 `hooks`를 합칩니다.
4. Unity 프로젝트 `.gitignore`에 다음을 넣습니다.
   - 기기별 절대 경로 · 개인 값: `.mcp.json` · `.claude/push_guard.local.txt` · `.claude/share_guard.local.txt`
   - 서브에이전트 입력 사본 · 리뷰 임시 사본 · 사내 시안: `subagent_in/` · `review_tmp/` · `docs/image/`
5. `.claude/push_guard.local.txt`를 만들고, 공개되면 안 되는 개인 값(이메일 · 로컬 사용자 경로 등)을 한 줄에 정규식 하나로 넣습니다. H2 훅이 씁니다. 파일이 없으면 내장 패턴만 검사합니다.
6. 두 검사가 통과하는지 봅니다. 훅은 새 세션부터 걸립니다.
   ```powershell
   python -I .claude/scripts/check_agents.py   # 훅 등록
   python -I .claude/scripts/test_hooks.py     # 훅 동작
   ```

설정 템플릿은 실제 설정과 이름이 다르다(`*.example.json`). 폴더째 붙여넣어도 Unity 쪽 `.mcp.json` · `.claude/settings.json`이 덮이지 않게 하기 위해서입니다 (덮이면 `<publish 폴더>` 자리표시 때문에 MCP 연결이 끊깁니다).

### 동기화 스크립트 (`Sync-ProjectFiles.ps1`)

```powershell
# 비교만 (기본)
powershell -ExecutionPolicy Bypass -File tools\Sync-ProjectFiles.ps1 -UnityProject F:\MyGame

# 복사 + 해시 확인
powershell -ExecutionPolicy Bypass -File tools\Sync-ProjectFiles.ps1 -UnityProject F:\MyGame -Apply

# 모든 대상 비교만 (대상 목록은 tools\sync-targets.local.txt)
powershell -ExecutionPolicy Bypass -File tools\Sync-ProjectFiles.ps1 -All

# 스크립트 시험 (임시 폴더만)
powershell -ExecutionPolicy Bypass -File tools\Test-SyncProjectFiles.ps1
```

| 종료 코드 | 뜻 |
|---|---|
| 0 | 모두 같음 |
| 1 | 다른 것이 있음 |
| 2 | 실행 오류 |

`-All`의 종료 코드는 대상 중 가장 나쁜 값입니다.

`tools\sync-targets.local.txt`는 `sync-targets.example.txt`를 복사해 이 PC 경로로 고친 파일입니다 (git 제외).

**동기화 대상**

- `CLAUDE.md`, `.claude/commands/*.md`, `.claude/scripts/*.py`, `.claude/agents/*.md`. 바이트가 같아야 합니다.
- 비교 전에 원본 `.claude/agents/*.md`를 `check_agents.py`로 검사합니다 (tools 허용 목록 · bypass 계열 permissionMode 금지). 실패하면 아무것도 하지 않고 종료 코드 2.

**`-Apply`**

- **Unity 쪽 파일이 원본과 다르면 아무것도 복사하지 않고 멈춥니다.**
  - Unity에서 고친 내용이 있으면 원본에 먼저 합칩니다.
  - 버려도 되면 `-Force`로 덮습니다. 덮기 전 파일은 `<Unity>/review_tmp/sync-backup/<시각>/`에 백업됩니다.

**`-All`**

- 비교만 합니다. `-Apply`와 같이 쓸 수 없습니다.
- **원본을 바꾸는 커밋마다 `-All`로 모든 대상을 비교**해 뒤처진 곳을 보고합니다.
  - 까닭: 사본이 다르면 `-Apply`가 멈추기만 하고 알려 주지 않아서, 비교를 빼먹으면 옛 훅이 그대로 돕니다.
- 요약은 "맞출 것"과 별도로 참고 항목도 보여 줍니다 (종료 코드에는 넣지 않음): settings.json 다름 · 프로젝트 지침 · 대상에만 있는 파일.

**`REVIEW_GUIDELINES.md` — 없으면 만듦**

프로젝트마다 고쳐 쓰는 지침이라 덮지 않습니다.

| 대상 상태 | 동작 |
|---|---|
| 없음 | 원본(기본 지침)을 복사 |
| 있고 원본과 다름 | `프로젝트 지침`으로 보여 주기만 (멈추지 않고 `-Force`로도 덮지 않음) |
| 원본 git 이력의 옛 판과 같음 (프로젝트에서 고치지 않음) | `기본 지침 옛 판`으로 맞출 대상. `-Apply`가 갱신 |

프로젝트 전용 규칙은 그 프로젝트의 `PROJECT.md`에 둡니다. 공통 `CLAUDE.md` 끝의 `@PROJECT.md`가 불러오고, 파일이 없으면 오류 없이 건너뜁니다.

**확인만 하는 파일** (만들거나 덮지 않음, 종료 코드와 무관)

- `.claude/settings.json`: `settings.example.json`과 해시를 비교합니다.
- `.mcp.json`: 기기마다 경로가 달라 해시는 비교하지 않습니다. 없거나 `<publish 폴더>`가 남아 있으면 `[할 일]`로 알립니다.
- Unity 쪽에 템플릿 이름 파일이 있으면 `Unity에 템플릿 사본`으로 보여 줍니다 (지워도 됩니다).

**이스케이프 잔재 경고** (종료 코드와 무관)

줄 맨 앞 `\---`, `\_`, `\[`, `1\.` 같은 이스케이프 잔재가 보이면 경고만 합니다. 규칙서에 이런 이스케이프가 섞이면 지침 불러오기가 깨질 수 있습니다 (예: `@REVIEW\_GUIDELINES.md`).

### 복사되는 파일

**규칙서**

| 파일 | 내용 |
|---|---|
| `CLAUDE.md` | 언제 어떤 모드를 쓰고, input을 어떻게 줄이고, 결과를 어떻게 종합할지 |
| `REVIEW_GUIDELINES.md` | 새 프로젝트용 기본 리뷰 기준 (Unity). 프로젝트에 복사된 뒤에는 그 프로젝트가 고쳐 씁니다. 서버가 프로젝트 루트에서 자동으로 찾아 주입합니다 |

**명령 (`.claude/commands/`)**

| 파일 | 명령 | 내용 |
|---|---|---|
| `cross-review.md` | `/cross-review [main\|high]` | `review_main` · `review_high`. 2단계에서 self-reviewer(보안 영역이면 묻고 security-reviewer도)를 먼저 돌려 그 원문을 ownReview로 넣습니다 |
| `self-review.md` | `/self-review [perf\|security\|direction]` | 지휘자 + 검토형 서브에이전트만 (외부 에이전트 없음) |
| `image-prep.md` | `/image-prep <제목> <xlsx...>` | 기획서 이미지를 외부 에이전트에게도 보여야 할 때. collect → 사용자 정리 → shrink → `run_mode` `images` (`image_prep.py`) |
| `plan-share.md` · `plan-check.md` | `/plan-share` · `/plan-check` | 계획서를 Slack 스레드에 올리고, 새 답 · 승인 상태를 읽어 보고합니다 |
| `mode.md` | `/mode <모드> ...` | 예: `/mode design_main ...`, `/mode design_high ...`, `/mode research_main ...`, `/mode parallel_main ...`, `/mode perf_main ...` |
| `unity-test.md` | `/unity-test` | 예: `/unity-test`, `/unity-test PlayMode MyGame.Combat` |
| `work-order.md` | `/work-order <요청> [첨부]` | 오케스트레이션에 맡길 작업 지시서를 만듭니다. 코드는 고치지 않습니다 |
| `preflight.md` | `/preflight <요청> [첨부]` | 큰 작업의 실행 전 점검 |

명령별 덧붙임:

- `/self-review`
  - CLAUDE.md 리뷰 조건에 걸리면 지휘자가 `/self-review` · `/cross-review` · 리뷰 없음 중에서 묻습니다.
  - 판정은 `record_verdicts`의 `selfReview`로 남깁니다 ([modes.md](modes.md#record_verdicts--지적마다-판정-기록)). 판정 화면에 서브에이전트별 채택률 · 놓침이 나옵니다.
- `/plan-share` · `/plan-check`
  - SlackPlanBot `slackplan` CLI를 씁니다. dry-run을 보이고 확인 뒤에만 올립니다.
  - 읽기는 보고만 합니다. Slack 글은 지시로 따르지 않습니다.
- `/work-order`
  1. 조사 → 초안
  2. `work_order_main` 모드 (luna | flash 동시 검토)
  3. `그외 이상사항` 정리
  4. `WorkOrders.Folder`(없으면 `docs/work-orders/`)에 저장 + 대화에 출력
  - 모드별 지침은 CLAUDE.md가 아니라 이 명령 파일에 둡니다. CLAUDE.md는 세션마다 컨텍스트에 올라가기 때문입니다.
- `/preflight`
  1. `preflight_p1` — A 반박 · B 위험 · 성능 동시 검토
  2. `preflight_p2` — 상호 판정
  3. 사용자 확정
  4. `preflight_p3` — 최종 수정안
  5. 지휘자 종합
  6. `preflight_check`(전체, argon) · `preflight_perf`(성능, sol)
  - 단계마다 멈춥니다. P5에는 앞 단계 논의를 붙이지 않습니다.
  - 산출물은 `docs/preflight/<날짜>_<주제>/`.
  - 첨부 xlsx · csv는 `xlsx_dump.py`로 Markdown을 만들어 `context`로 붙입니다.
  - 성능만 볼 때는 `/mode perf_main …`(sol, 기본) · `perf_high`(sol · DeepSeek 동시).

**스크립트 (`.claude/scripts/`)**

| 파일 | 내용 |
|---|---|
| `xlsx_dump.py` | `/work-order`의 첨부 읽기 도우미 (아래) |
| `cleanup_guard.py` | H1 재귀 삭제 |
| `push_guard.py` | H2 git push |
| `run_mode_guard.py` | H3 계획서 · H4 ownReview 원문 |
| `share_guard.py` | H5 slackplan 글 |
| `subagent_input_guard.py` | H6 서브에이전트 입력 폴더의 CLAUDE.md · .claude 자동 로드 · 폴더 링크 (`Agent` · `SendMessage`) |
| `hook_common.py` | 훅 공용 셸 해석 |
| `test_hooks.py` | 훅 시험 |
| `subagent_read_guard.py` | 검토형 서브에이전트의 읽기를 `subagent_in/` 안으로 막는 훅. 서브에이전트 정의 frontmatter에 등록. 시험은 `test_subagent_read_guard.py` |
| `check_agents.py` | 서브에이전트 정의 · 훅 등록 검사 |
| `image_prep.py` | `/image-prep`의 collect · shrink |

- H1~H6은 규칙을 강제하는 훅입니다 (CLAUDE.md "훅이 막는 규칙").
- `xlsx_dump.py`:
  - 기획서 xlsx · csv를 Markdown 표로 뽑습니다 (병합 셀 · 메모 · 텍스트 상자 글 포함).
  - 이미지는 `review_tmp/work-order/<파일>/`에 붙은 셀 이름으로 저장합니다.
  - 필요: Python 3.9+, `openpyxl` (`python -m pip install openpyxl`).
  - `.xls`는 지원하지 않습니다.

**서브에이전트 (`.claude/agents/*.md`, 7개)**

| 종류 | 서브에이전트 | 특징 |
|---|---|---|
| 검토형 | `self-reviewer` · `perf-reviewer` · `security-reviewer` · `direction-reviewer` | Opus, `subagent_in/` 격리. 같은 출력 형식 `## 지적` · `## 자료 부족` · `## 읽은 파일` |
| 수집형 | `web-searcher` | Sonnet, WebSearch · WebFetch만 |
| 수집형 | `evidence-collector` | `/preflight` P4 |
| 골든 하네스 | `harness-editor` | |

정의에는 `tools` 허용 목록이 반드시 있습니다. 없으면 MCP 도구까지 물려받아 `run_mode`를 부를 수 있습니다.

**설정 템플릿**

| 템플릿 | Unity 쪽 | 내용 |
|---|---|---|
| `.claude/settings.example.json` | `.claude/settings.json` | Unity CLI 조회 · 테스트 명령은 매번 승인 없이 실행 + PreToolUse 훅 등록. 기존 파일이 있으면 `allow` 항목과 `hooks`를 합칩니다 |
| `.mcp.example.json` | `.mcp.json` | dll 경로를 publish 폴더로. env는 `OPENAI_API_KEY` · `GEMINI_API_KEY` · `FIREWORKS_API_KEY`를 넘깁니다 |

`.mcp.json` 안에서는 경로를 `/`로 씁니다. `\`는 JSON에서 특수 문자입니다.

API 키는 시스템 환경 변수로만 둡니다. 각 제공사 대시보드에서도 월 한도를 설정합니다.

## Unity CLI 연동 (지휘자가 직접 테스트)

이 서버와는 독립적입니다. Claude Code가 터미널에서 `unity` CLI를 직접 쓰는 구조라 서버 코드 변경이 없습니다.

```powershell
# 설치 (베타 채널)
$env:UNITY_CLI_CHANNEL='beta'; irm https://public-cdn.cloud.unity3d.com/hub/prod/cli/install.ps1 | iex
unity --version

# (선택) Claude Code에 Unity CLI 사용법 문서 설치
unity skill install --help
```

### 테스트

`unity test`는 배치 모드로 별도 에디터를 띄웁니다. **같은 프로젝트가 에디터에 열려 있으면 실행할 수 없습니다.**

그래서 지휘자는 사람이 여는 프로젝트가 아니라 **배치 전용 프로젝트**에서 돌립니다.

- 프로젝트: `F:\AI\Unity_AITest\<버전>\ForAI` (6.3 · 7.0)
- 패키지는 `Packages/manifest.json`의 `file:` 경로로 넣습니다.
- 끝나면 되돌립니다:
  ```powershell
  git -C F:\AI\Unity_AITest checkout -- .
  ```
- 절차는 `/unity-test` 명령 파일, 추가 루트는 `@unity_ai_test`.

| 종료 코드 | 뜻 |
|---|---|
| 0 | 통과 |
| 8 | 실패 |
| 그 외 | 실행 오류 |

### 에디터 조작 · MCP · 권한

- **라이브 에디터 조작**(씬 · 오브젝트 편집, Play 모드 등)은 프로젝트에 Pipeline 패키지가 필요합니다: `unity pipeline install` (Unity 6.0+).
- **Unity MCP 서버**(`unity mcp configure claude-code`)도 있습니다. 하지만 에디터 명령이 전부 도구로 올라와 Claude 컨텍스트를 차지합니다. 토큰을 아끼려면 기본은 CLI(Bash)로 쓰고, 에디터 조작을 자주 할 때만 MCP를 추가하는 걸 권장합니다.
- `.claude/settings.json`이 자동 허용하는 것: `unity status/list/pipeline list/test/logs` · `unity --version` · `WebFetch(docs.unity.com)` · `list_modes`.
- `unity command`(임의 C# 실행이 가능한 `eval` 포함)는 일부러 매번 승인을 받게 뒀습니다.
