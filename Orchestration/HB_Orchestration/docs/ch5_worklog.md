# 5장 작업 로그 — 사람이 따라갈 수 있게 만들기

계획서: [plans/2026-10-16_5장-사람이-따라갈-수-있게.md](plans/2026-10-16_5장-사람이-따라갈-수-있게.md) (결정 절 포함)

진행 순서는 A → B → C → D다. 항목마다 구현 → 빌드 → 테스트 → 커밋한다.

---

## A. 호출 기록 보강 — 에이전트가 무엇을 받았나

### 바꾼 것

| 파일 | 내용 |
|---|---|
| `Shared/CallProvenance.cs` (새 파일) | `CallProvenance` = Instructions 해시·바이트 수, `Guidelines{Attached, Path, Sha256, Bytes, Reason}`, `Modes{Path, Sha256, Bytes}`, `Settings{…, Stored, SecretScan, ReloadError?}`, `BlobFolder`, `StoreErrors?`(없으면 생략) |
| `Shared/BlobStore.cs` (새 파일) | `blobs/<sha256>.txt`. 이미 있으면 쓰지 않음. 임시 파일 → `File.Move(overwrite:false)`, 다른 프로세스가 먼저 만들었으면 성공으로 처리. 예외를 던지지 않고 오류 문자열을 반환 |
| `Shared/CallRecord.cs` | `Provenance` (항상 기록, 예전 기록은 null) |
| `Agents/ProvenanceBuilder.cs` (새 파일) | 실행 단위(modes · 설정 · 지침 원문 저장), 단계 단위(지침 붙임 여부), Instructions 해시. 키 패턴 검사 |
| `Agents/GuidelinesLoader.cs` | 파일을 **바이트로 한 번** 읽어 해시하고, 같은 바이트를 `ReadAllText`와 같은 방식으로 디코딩. 경로·바이트·해시를 함께 반환 |
| `Agents/ModeStore.cs` | modes.json도 바이트로 한 번 읽어 `Loaded.Bytes`·`Sha256`. 읽기 예외 → `ModeConfigException` |
| `Agents/AgentCatalog.cs` | 스냅샷을 만들 때 설정 파일 바이트를 함께 읽어 `SettingsBytes`로 노출 |
| `Agents/ModeRunner.cs` | 계획을 `PlannedStep`(+ 단계별 지침 표시)으로 바꿈. 실행 횟수 예약 **뒤에** 실행 단위 Provenance를 만들고(try로 격리) 단계 호출에 얹음. 지침 읽기 예외는 `[리뷰 지침 읽기 실패]` (횟수 차감 전) |
| `Agents/AgentRunner.cs` | `call.Instructions`(Debate 규칙까지 붙은 최종 문자열)를 단계마다 한 번 해시·저장하고, 모든 시도 기록에 넣음. 저장 실패는 경고 로그 |
| `Agents/OrchestrationOptions.cs` | `Records.BlobFolder`, `Records.StoreSettingsText`(기본 false) |
| `Agents/SessionTracker.cs` | 테스트용 폴더 주입 생성자 (internal) |
| `README.md` | 설정 표 2행, "호출 기록의 지침·설정 해시" 절 |

### 계획과 달라진 것

- **설정 해시를 실행 시점에 파일을 다시 읽어 계산하지 않는다** (review R2 반영). 계획서에는 "run 시작 때 `SettingsPath`를 바이트로 읽어 해시"라고 썼다. 그러면 에이전트·옵션을 만든 설정과 해시한 파일이 다른 버전일 수 있다. 이제는 `AgentCatalog`가 설정을 읽을 때 함께 읽은 바이트를 쓴다. 다시 읽기에 실패해 이전 설정이 적용 중이면 이전 바이트의 해시가 남는다.
- 지침·modes.json 읽기 실패를 `[`로 거절하는 처리를 추가했다 (review R1). 원래부터 예외가 새던 경로라 계획에는 없었다.
- 대시보드의 "기록 없음" 표시는 D에서 한다. A에서는 예전 형식 줄이 `Provenance = null`로 읽히는 것까지만 확인했다.

### 자체 검토 (review 결과를 보기 전)

| # | 내용 | 처리 |
|---|---|---|
| C1 | `ForRun`이 예상 못 한 예외를 내면 실행 횟수만 차감되고 run_mode 밖으로 샌다 | 호출을 try로 감싸 `StoreErrors`에 남김 |
| C2 | 지침·modes.json 파일 읽기 예외는 이번 변경 전부터 run_mode 밖으로 샌다 | 범위 밖으로 판단 → review R1이 지적해 반영 |
| C3 | 프로세스가 죽으면 `*.tmp-*` 임시 파일이 남을 수 있다 | 해시 이름이 아니라 오인 없음. 정리 기능은 만들지 않음 |
| C4 | 해시 범위는 MAF에 넘긴 문자열까지다 (HTTP 본문 아님) | README·계획서 위험에 명시. 테스트로 IChatClient 경계까지 확인 |
| C5 | Debate는 같은 토론자의 턴마다 해시를 다시 계산한다 | 수 KB라 비용 무시 |

### review (gpt_main_sol, run `8f0fe57a52`, 입력 13,429 / 출력 1,042, 28.2초)

| # | 지적 | 판정 | 근거·처리 |
|---|---|---|---|
| R1 [major] | 파일을 찾은 뒤 지워지거나 잠기면 `File.ReadAllBytes` 예외가 run_mode 밖으로 샌다 | **채택** | 코드 확인: `ModeStore`는 `JsonException`만 변환하고, 지침 로딩에는 처리가 없다 (C2와 같은 문제). 이번 변경 전부터 있던 문제지만 원칙 위반이라 반영했다. modes는 `ModeConfigException`으로, 지침은 `[리뷰 지침 읽기 실패]`로 거절하고 둘 다 횟수 차감 전이다. 지침이 빠진 채 보내면 결과가 달라지므로 조용히 진행하지 않는다 |
| R2 [major] | 계획을 만든 뒤 설정 파일을 따로 읽으면 해시가 적용 중인 설정과 다를 수 있다 | **채택** | 위 "계획과 달라진 것". 남는 한계: 설정 시스템의 `Reload()`와 이쪽 바이트 읽기 사이에 파일이 바뀌는 매우 짧은 경합은 막지 못한다. 그래도 수정 시각이 달라지므로 다음 접근 때 다시 읽힌다 (추정) |
| R3 [minor] | blobs 폴더 해석 실패 시 `Store`가 아무것도 안 하는데 설정 `Stored=true`가 기록된다 | **채택** | 코드 확인 (오류 개수 비교 방식). `Store`가 성공 여부를 반환하도록 바꿈 |

에이전트만 지적한 건(R2·R3)은 코드로 다시 확인해 타당했다. 공통 지적은 R1 = C2.

### 테스트

`ProvenanceTests` 24건 (전체 106건 통과). 임시 폴더에서 실제 `ModeRunner` → `AgentRunner` 경로로 실행한다. 가짜 IChatClient가 받은 메시지·옵션을 남긴다.

| 통과 기준 | 테스트 |
|---|---|
| 같은 모드 2회 → 해시 같음, 원문 한 번만 저장 | `SameModeTwice_SameHashes_BlobsStoredOnce`: 두 기록의 Instructions·Guidelines·Modes·Settings가 같다. blobs 파일 3개는 두 번째 실행 뒤에도 그대로다 |
| 지침 파일 변경 → 다음 호출 해시 변경 | `GuidelinesChange_ChangesNextHash` |
| 저장 원문 바이트 = 실제로 보낸 시스템 프롬프트 | `StoredInstructions_EqualBytesSentToClient` (seq · Concurrent · Debate 3단계): IChatClient가 받은 시스템 지시의 UTF-8 바이트 = blob 바이트 = 기록 해시. Debate는 토론 규칙이 붙은 것과 안 붙은 것이 모두 있다 |
| 원문 저장 폴더에 못 써도 호출 성공 | `BlobFolderUnwritable_CallStillSucceeds`: 폴더 자리에 파일을 두면 성공 + 해시 있음 + `StoreErrors`(modes · instructions) |
| 이전 기록 | `OldRecordLine_HasNullProvenance_AndNewFieldsRoundTrip` |
| 기타 | 지침 원본 바이트 해시(`sha256sum`과 같은 기준), 역할이 끈 경우 / 파일 없음의 Reason, 지침을 끈 역할만 있으면 지침을 저장하지 않음, 설정은 기본 해시만, `StoreSettingsText` + 키 패턴이면 저장 거절 / 깨끗하면 저장, 키 패턴 5종(`ApiKeyEnv`는 통과), `storeErrors` 생략, review 반영 4건(다시 읽기 실패 시 적용 중 설정 해시, 폴더 오류 시 Stored=false, 지침·modes 잠금 시 거절 + 횟수·호출 0) |

### 확정 / 추정

- 확정: IChatClient에 들어간 시스템 지시 문자열과 기록 해시·blob 바이트가 같다 (테스트).
- 추정: MAF/OpenAI SDK가 그 문자열을 HTTP 본문으로 바꾸는 과정에서 내용을 바꾸지 않는다.

---

## B. `run_mode` 기록용 인자 — 지휘자가 왜, 무엇을 먼저 판단했나

### 바꾼 것

| 파일 | 내용 |
|---|---|
| `Mcp/ModeTools.cs` | `reason`, `ownReview` 인자 (nullable, 설명 각 한 줄). 필수 여부는 서버가 검사 |
| `Agents/ModeDefinitions.cs` | `ModeDefinition.RequireOwnReview` (키 없음 = false) |
| `Agents/ModeRunner.cs` | `CheckConductorNotes`: 모드를 찾은 직후, Steps·Debate 검사 다음, 입력 길이 · 첨부 · 예산 · 실행 횟수 예약보다 **앞에서** 검사. 두 값은 `CallMeta`로만 흐름. `list_modes`에 `[ownReview 필수]` |
| `Agents/AgentRunner.cs` · `Shared/CallRecord.cs` | `Reason` · `OwnReview`를 그 실행의 모든 기록 줄에 원문 그대로 남김 (공백만 있는 ownReview는 null) |
| `Agents/OrchestrationOptions.cs` | `Records.MaxReasonChars`(500) · `MaxOwnReviewChars`(30,000), 0 = 무제한 |
| `src/…/Mcp/modes.json` | `review` · `panel` · `cross_p1` · `cross_p1_guided`에 `"RequireOwnReview": true` (publish 폴더의 modes.json은 publish가 사용자 설정으로 되돌리므로 마지막 실제 확인 때 같은 4개에 넣는다) |
| `project-files/CLAUDE.md` | "input 작성"에 "판단 근거는 기록 인자로" 한 항목 |
| `project-files/.claude/commands/cross-review.md` · `mode.md` | 3단계에 `reason` · `ownReview` |
| `README.md` | 모드 표 `RequireOwnReview`, 설정 표, "지휘자의 판단 근거" 절 |

### 규칙서 동기화

- `Sync-ProjectFiles.ps1` 비교 결과는 다름 3개(`CLAUDE.md`, `cross-review.md`, `mode.md`)였다.
- Unity 쪽 세 파일의 SHA-256은 이전 원본(`git show HEAD:…`)과 같았다 (`5194b14e80db` · `c58de8b889a9` · `351e45928084`). Unity에서 직접 고친 내용이 없다는 뜻이다.
- 스크립트는 차이가 있으면 `-Apply`만으로는 멈추도록 설계돼 있다. 위 확인 뒤 `-Apply -Force`로 복사했다. 백업은 `F:\AI\ToolCreate\review_tmp\sync-backup\20261016-220823`(3개)에 있다.
- 복사 후 6개 모두 같음이다: `CLAUDE.md` `2067c382f18b`, `cross-review.md` `e42fccce1da3`, `mode.md` `b30191d8b788`.
- 새 규칙은 다음 세션부터 지휘자 컨텍스트에 실린다. 지금 떠 있는 서버(이전 빌드)는 두 인자를 모른다. 그래서 C의 review 호출까지는 두 인자 없이 부르고, 마지막 실제 확인에서 새 빌드로 처음 쓴다.

### review

계획대로 하지 않았다. 변경이 작고, 핵심 위험(독립성 누출 · 횟수 차감)을 아래 테스트로 직접 확인했다.

### 테스트

`ConductorNotesTests` 13건 (전체 119건 통과).

| 통과 기준 | 테스트 |
|---|---|
| `reason` 없이 부르면 거절, 실행 횟수 그대로 | `MissingReason_Rejected_CountUnchanged` (null · "" · 공백 줄바꿈): `[reason 필요]`, 세션 횟수 0, 클라이언트 호출 0, 기록 0줄 |
| review 계열에서 `ownReview` 없으면 거절, 다른 모드는 성공 | `ReviewMode_MissingOwnReview_Rejected_CountUnchanged`, `OtherMode_WithoutOwnReview_Succeeds` |
| 에이전트 입력·시스템 프롬프트 어디에도 없음 + 기록에 원문 그대로 | `NotesNeverReachAgents_AndAreRecordedVerbatim` (Sequential 2단계 · Concurrent · Debate 3단계): IChatClient가 받은 시스템 지시 + 메시지, 모든 기록의 `Prompt`, blobs 파일에 표지 문자열이 없다. `Reason`·`OwnReview`는 따옴표·태그·백슬래시·앞뒤 공백까지 UTF-8 바이트가 같다 |
| 길이 제한 | 500자 / 30,000자 경계 통과, 501자 / 30,001자 거절(횟수 그대로), 0 = 무제한 |
| `list_modes` 표시 | `ListModes_MarksRequiredOwnReview` |

---

## C. 대시보드에 세션 기록 표시 — 지휘자가 무엇을 알고 있었나

### 바꾼 것

| 파일 | 내용 |
|---|---|
| `Shared/ClaudeTranscripts.cs` (새 파일) | `ClaudeTranscriptReader`: 형식에 의존하는 코드를 한곳에 모음. 줄 단위·엄격한 UTF-8 읽기, instructions 지침 목록(경로 · 종류 · UTF-8 바이트 수 · SHA-256), `__run_mode` tool_use와 그 결과(같은 tool_result / 백그라운드 task-notification / persisted 미리보기)의 **결과 첫 줄** `runId:`. `InstructionDiskCompare`(결정 4 규칙). `RunLinker`(확정 / 추정 / 후보 여럿 / 없음) |
| `Dashboard/ClaudeSessionsService.cs` (새 파일) | 폴더 결정(설정 → `CLAUDE_CONFIG_DIR` → 사용자 폴더), `<GUID>.jsonl`만, (경로 · 크기 · 수정 시각) 캐시. 연결은 전체 세션, 목록은 최근 N개 |
| `Dashboard/Pages/ClaudeSessions` · `ClaudeSession` (새 파일) | 목록(시작 로컬 시각 · 세션 · 프로젝트 · 버전 · 지침 수 · run_mode 수 · 확정/추정/알 수 없는 줄 배지)과 상세(지침 표 + 디스크 대조, run_mode 표 + 호출 상세 링크). 대화 본문·도구 입력은 표시하지 않음 |
| `Dashboard/DashboardOptions.cs` | `ClaudeProjectsDir`, `ClaudeSessionLimit`(50) |
| `Program.cs` · `_Layout.cshtml` | 서비스 등록, 메뉴 "Claude 세션" |
| `README.md` | 7절 "Claude 세션" |

### 계획과 달라진 것

- 계획의 "필요한 줄만 파싱(문자열 사전 검사)"은 하지 않았다. "알 수 없는 줄"을 세려면 모든 줄이 JSON인지 봐야 한다. 대신 파일 단위 캐시로 다시 읽지 않는다. 22개 파일(최대 수 MB)로 화면 응답이 문제없음을 확인했다 (체감 확인이고 측정하지는 않음).
- 거절된 호출(결과가 `[`로 시작)은 추정하지 않는다 (review R3 부분 채택). 상세 화면에 "실행 전 거절"로 표시한다.
- 상세 화면은 목록 제한 밖 세션도 연다 (review R2 반영 과정에서 해결).

### 자체 검토 (review 결과를 보기 전)

| # | 내용 | 처리 |
|---|---|---|
| C1 | 목록 제한(50) 밖 세션의 상세 화면이 404 | review R2 반영 때 전체 세션 조회로 해결 |
| C2 | Claude Code가 쓰는 중인 마지막 줄은 잠깐 알 수 없는 줄 1개로 보일 수 있다 | 다음 새로고침 때 사라짐. 그대로 둠 |
| C3 | 지침 경로는 세션 기록에서 온 값이지만, 디스크 대조(존재·해시)에만 쓰고 내용은 보여주지 않는다 | 문제없음 |
| C4 | 캐시 동시 접근 | `ConcurrentDictionary`. 같은 파일을 두 요청이 동시에 읽어도 결과가 같다 |

### review (gpt_main_sol, run `4942156eb5`, 입력 9,573 / 출력 1,218, 30.6초)

| # | 지적 | 판정 | 근거·처리 |
|---|---|---|---|
| R1 [major] | 멀티라인 정규식이라 결과 본문 어느 줄의 `runId: …`도 확정으로 인정 | **채택** | 코드 확인. 에이전트 응답에 그런 줄이 있으면 오연결된다 (작업 로그를 리뷰받는 경우 등). 같은 tool_result는 첫 줄만, task-notification은 `<result>` 다음 첫 줄, persisted는 `Preview…:` 다음 첫 줄만 본다. 테스트 `RunIdLineInsideResultBody_IsNotHeader` |
| R2 [major] | 표시 제한으로 자른 뒤 확정 runId를 모아, 제한 밖 세션이 확정한 실행이 추정 후보로 남음 | **채택** | 코드 확인. 연결은 전체 세션으로, 목록만 제한 |
| R3 [major] | 결과를 받지 못한 호출도 시각·모드로 추정 연결 | **부분 채택** | 결과를 못 받은 호출(세션 종료, 아직 실행 중)도 서버에서는 실행이 시작됐으므로 그 실행에 연결하는 것이 맞다. "추정"이고 결과 위치가 "결과 없음"으로 보인다 → 이 부분은 기각. 실행 전에 거절된 호출(`[`로 시작)은 실행이 없는데, 같은 프로젝트·모드의 다른 세션 실행이 붙을 수 있다 → 이 부분만 채택. 테스트 `RejectedCall_IsNotEstimated` |
| R4 [major] | `0001-01-01` · `9999-12-31` 시각에서 `at ± 창` 범위 초과 예외 → 페이지 실패 | **채택** | 차이(`start - at`)로 비교. 테스트 `ExtremeTimestamps_DoNotThrow` |
| R5 [minor] | 아는 항목인데 message.content 모양이 달라도 조용히 무시 | **채택** | assistant / user 줄의 `message.content`가 배열·문자열이 아니면 알 수 없는 줄 |
| R6 [minor] | 잘못된 UTF-8을 대체 문자로 바꿔 틀린 해시를 조용히 표시 | **채택** | 줄 단위 엄격 디코딩(실패한 줄만 알 수 없는 줄). 파일 전체를 실패로 두지 않은 이유는 쓰는 중인 마지막 줄 때문이다. 같은 이유로 짝 없는 서로게이트(`\uD800`) content는 파일 하나만 알 수 없는 줄로 셈 (반영 테스트 중 발견: `GetString()`이 줄 전체를 버리던 것을 파일 단위로) |

### 테스트

`ClaudeTranscriptTests` 28건 (전체 147건 통과). 합성 데이터만 쓴다. 실제 세션 기록에는 대화 전체가 있어 TestData에 넣지 않았다.

- 지침: 경로 · 종류 · UTF-8 바이트 수 · SHA-256, timestamp 없는 첫 줄 건너뛰기, `\u` 이스케이프 없는 한글 원문 바이트(UTF-8 명시 확인)
- 연결: 같은 줄 / 백그라운드(실제 모양 `<result>\n…` — 처음에 `<result>runId:`로 짐작해 만든 픽스처가 틀려 320760ac 113줄 구조를 보고 고침) / persisted, 다른 도구 결과의 runId 무시, 본문 속 runId 줄 무시, 확정 · 추정 · 후보 2개 · 창 밖 · 다른 프로젝트 · 다른 세션이 확정한 실행 제외, 거절 호출, 극단 시각
- 형식: 깨진 줄 6종 집계, 알아볼 줄 없는 파일 / 없는 파일 → 알 수 없는 형식, 잘못된 UTF-8 줄, 짝 없는 서로게이트
- 읽기 전용: 읽기 전후 바이트 · 수정 시각 같음
- 원본 대조(결정 4): 같음 / content + LF / content + CRLF → 다름 / LF 2개 → 다름 / 내용 변경 → 다름 / 파일 없음
- 파일 이름(`orphaned` 제외), 경로 인코딩 2건

### 실제 데이터 확인 (새 빌드 대시보드를 5299 포트에 따로 띄워 HTML을 받아 확인, 사용자 대시보드는 건드리지 않음)

| 통과 기준 | 결과 |
|---|---|
| `320760ac`: 지침 `CLAUDE.md`·`MEMORY.md`뿐, `REVIEW_GUIDELINES.md` 없음 | 통과. `F:\AI\ToolCreate\CLAUDE.md`(Project, 6,877B), `…\memory\MEMORY.md`(AutoMem, 671B) 두 개 |
| `320760ac`: run 4개 연결, 모두 추정 (결정 3) | 통과. `3169f4a54e` cross_p1 · `d49d0cf935` cross_p2 · `8e2575f920` cross_p3 · `dfa393514b` cross_p3. 모두 "추정(시각·모드)", 결과 위치는 앞 둘이 result, 뒤 둘이 task-notification |
| `90430e3d`: 지침 3개 | 통과. `CLAUDE.md` 9,418B · `REVIEW_GUIDELINES.md` 2,778B · `MEMORY.md` 671B. 이 세션의 review `8f0fe57a52` · `4942156eb5`는 "확정" |
| 한글 지침 파일의 크기·해시가 원본과 같음 (결정 4) | `REVIEW_GUIDELINES.md` 2,778B `31e0314cf4f2…` → 디스크(2,779B)와 "끝 줄바꿈만 다름". `90430e3d`의 `CLAUDE.md`는 "다름"으로 나온다. 이 세션 B에서 동기화로 파일을 바꿨기 때문이고, 동기화 직전 백업(`review_tmp/sync-backup/20261016-220823/CLAUDE.md`, 9,419B)이 content + LF 1개와 바이트가 같음을 따로 확인했다. `320760ac`의 `CLAUDE.md`도 이후 변경으로 "다름" |
| 형식이 깨진 줄을 넣은 파일로도 화면이 열림 | 통과. 임시 폴더(`Dashboard:ClaudeProjectsDir` 지정)에 두 파일: 깨진 줄 섞인 파일 → 200, "알 수 없는 줄 4개" / 전부 깨진 파일 → 200, "알 수 없는 형식: sessionId · timestamp가 있는 줄이 없습니다". 없는 id(`../../etc`) → 404 |

### 확정 / 추정

- 확정: 위 실제 데이터 결과, 세션 기록 content = 디스크 − 끝 LF 1개 (파일 4개: 90430e3d 세 개 + 동기화 전 백업으로 다시 확인한 CLAUDE.md).
- 추정: "세션 기록은 항상 끝 줄바꿈 1개를 빼고 저장한다"는 일반 규칙. 끝 줄바꿈이 없는 파일, CRLF 파일, 끝 줄바꿈이 2개인 파일은 확인하지 않았다. 그런 파일은 규칙대로 "다름"으로 보인다 (결정 4).
- 추정: 경로 인코딩 규칙 (경로 2개로 확인). 긴 경로를 줄이는 등 다른 변환이 있는지는 모른다. 화면은 폴더 이름을 해석하지 않고 `cwd`를 쓰므로 표시에는 영향이 없다.
- 추정: `CLAUDE_CONFIG_DIR` 지원.

---

## D. 지휘자 판단과 에이전트 결과를 나란히

### 바꾼 것

| 파일 | 내용 |
|---|---|
| `Dashboard/Pages/Call.cshtml` · `.cs` | 위쪽 3열: `reason` / `ownReview`(Markdig, 원시 HTML 금지) / 응답 (좁은 화면은 세로). 아래는 요청 전체. "에이전트가 받은 지침·설정" 표 + 저장 오류 목록. 단계 목록에 단계별 시스템 프롬프트 해시 앞 8자. 메타 칸 "Claude 세션"(확정/추정 배지). 원문 보기 핸들러 `/Call/{id}?handler=Blob&sha=…` |
| `Dashboard/ProvenanceView.cs` (새 파일) | 표 줄 만들기, 원문 열기 허용 해시(그 기록의 Instructions · 붙인 지침 · modes · 저장된 설정만) |
| `Dashboard/Pages/ClaudeSession.cshtml` | **C의 버그 수정**: 호출 링크가 `/Call?id=…`였는데, 호출 상세 경로는 `/Call/{id}`라 404가 나고 있었다. D 작업 중 Call 페이지 경로를 보다가 발견했다 |
| `tests/…csproj` | 대시보드 프로젝트 참조 (ProvenanceView 테스트) |
| `README.md` | 7절 호출 상세 설명 |

### 원문 보기 경로를 제한한 방식

- `sha`는 64자리 소문자 hex만 받는다.
- 그 기록의 Provenance가 가리키는 해시만 연다. 같은 폴더의 다른 파일(다른 호출의 지침 등)은 열리지 않는다.
- 저장 폴더는 기록에 남은 `BlobFolder`를 쓴다 (서버 설정을 나중에 바꿔도 예전 기록을 열 수 있다).
- 파일이 없으면 404다. 응답은 `text/plain; charset=utf-8` + `nosniff`로 원문 바이트 그대로 보낸다.

### review

계획대로 하지 않았다. 표시만 바꾸는 변경이라서다. 원문 경로 검사는 위 방식과 테스트로 확인했다.

### 테스트

`ProvenanceViewTests` 8건 (전체 155건 통과).
- 표 4줄과 원문 링크 여부: 서버 설정은 링크 없음 + "원문 저장 안 함 · 키 검사 통과". blobs 파일을 지우면 모든 링크가 사라진다.
- 지침을 붙이지 않았으면 이유를 표시한다.
- 허용 해시는 설정을 저장하지 않았으면 3개다.
- 잘못된 sha 4종을 거절한다.

### 실제 확인 (새 빌드 대시보드를 5299 포트에, 기존 기록으로)

- 이 세션의 review `8f0fe57a52` 기록(`c3da9178d1a6`, 이전 서버로 실행해 새 필드가 없음):
  - 200이다.
  - reason · ownReview · 지침·설정 칸이 모두 "기록 없음 (5장 이전 기록)"이다.
  - 응답이 3열 오른쪽에 있다.
  - Claude 세션 칸은 `90430e3d` "확정"이다.
- 원문 보기에 `sha=../calls`를 주면 404, 형식만 맞는 해시를 주면 404다 (기록에 Provenance가 없음).
- 세션 화면의 호출 링크 2개(`/Call/c3da9178d1a6`, `/Call/7eebf076f263`)가 200이다 (위 버그 수정 확인).
- 새 형식 기록의 화면은 마지막 실제 확인에서 본다.

---

## 이번에 하지 않는 것 (TODO)

| 항목 | 이유 | 다시 볼 때 |
|---|---|---|
| 판정 짝짓기 기록 (지휘자만 / 에이전트만 / 둘 다, 채택·기각) | 결과를 본 뒤의 판정을 남기려면 도구가 하나 늘어난다. 이번에는 작업 로그에 둔다 | 나란히 보기만으로 부족하다고 느낄 때 |
| 훅으로 이벤트 기록 | 세션 기록 읽기로 지금 필요한 것은 충분하다 | 세션 기록 형식이 바뀌어 읽기가 어려워질 때, 또는 실시간 표시가 필요할 때 |
