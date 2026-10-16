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

## 이번에 하지 않는 것 (TODO)

| 항목 | 이유 | 다시 볼 때 |
|---|---|---|
| 판정 짝짓기 기록 (지휘자만 / 에이전트만 / 둘 다, 채택·기각) | 결과를 본 뒤의 판정을 남기려면 도구가 하나 늘어난다. 이번에는 작업 로그에 둔다 | 나란히 보기만으로 부족하다고 느낄 때 |
| 훅으로 이벤트 기록 | 세션 기록 읽기로 지금 필요한 것은 충분하다 | 세션 기록 형식이 바뀌어 읽기가 어려워질 때, 또는 실시간 표시가 필요할 때 |
