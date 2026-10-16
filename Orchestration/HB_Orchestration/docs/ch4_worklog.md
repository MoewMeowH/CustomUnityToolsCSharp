# 4장 작업 로그 — 테스트 후 다시 수정

계획과 승인 전 조사 내용은 [ch4_plan.md](ch4_plan.md)에 있다. 이 문서는 승인 이후의 결정, 검토, 테스트를 항목별로 남긴다.

---

## 0. 승인과 사전 정리

### 승인 때 확정한 결정 (사용자 답변)

| 질문 | 결정 |
|---|---|
| `ExpectedLanguage` 기본값 | 키가 없으면 `ko`, `""`이면 검사 끔. "비우면 기존 동작" 원칙의 **의도적 예외**다 (이유는 2-4절) |
| 사용자가 바꾼 설정 | 별도 커밋으로 먼저 정리. src 쪽 modes.json에 cross 모드 전체를 publish와 같게 넣음 |
| 상한값 | Retry-After 상한 30초, MaxAttempts 상한 5. README에 "타임아웃 × 시도 횟수만큼 최대 대기"를 적음 |

추가 지시:
- review 실행 횟수는 서버 프로세스 단위다. `/mcp` 재연결 후 0부터 다시 센다.
- 2-5 조사용 review는 `agents ["*=gpt_mini_luna"]`로 실행한다.
- 조사가 끝나면 덤프 파일을 지우고 `DumpToolResults`를 false로 되돌린다.

### 설정 정리 커밋 (`94c1d17`)

**변경**
- 사용자가 바꾼 `src/…/appsettings.json`(모델 이름 변경, 예전 모델 단가)과 `modes.json`을 커밋했다.
- src와 publish의 modes.json을 비교했다. src에 빠진 것은 `Cross_P1_*_Guided`, `Cross_P3_*` 역할과 `cross_p1_guided`, `cross_p3` 모드뿐이었다. 겹치는 항목은 내용이 모두 같았다.
  - 그래서 publish 파일을 src로 그대로 복사했다.
  - 3장의 "설정 두 벌" 문제가 이걸로 완화된다.

**API 키 확인**
- 두 파일과 publish 설정에서 `sk-…`, `AIza…`, `"ApiKey":` 패턴을 검색했고 일치하는 것이 없었다.
- diff에서 key·token·secret이 들어간 줄은 `MaxOutputTokens` 값 변경뿐이었다.
- 키는 환경 변수 이름(`ApiKeyEnv`)으로만 들어 있다.

**JSON 유효성:** 두 파일 모두 파싱 통과.

---

## 2-1. 에이전트 단위 타임아웃

### 바꾼 것

| 파일 | 내용 |
|---|---|
| `Agents/OrchestrationOptions.cs` | `AgentOptions.TimeoutSeconds` 추가. 3장 임시 조치였던 `ProviderOptions.TimeoutSeconds`는 이름을 그대로 두고 주석만 정리 |
| `Agents/AgentSettings.cs` (새 파일) | `ResolveTimeout(agent, provider)`: 에이전트 → 제공사 → SDK 기본 100초 순. 0 이하는 "설정 안 함"으로 봄 |
| `Providers/IChatClientProvider.cs` | `Create(providerName, ProviderOptions, string model)` → `Create(providerName, ProviderOptions, AgentOptions agent)` |
| `Providers/OpenAICompatibleProvider.cs` | `NetworkTimeout = AgentSettings.ResolveTimeout(...)`. 값이 없을 때도 100초를 명시적으로 넣는다 |
| `AgentCatalog.cs` | 호출부 수정 |
| 대시보드 `ModesConfigService.cs`, `Pages/Modes.cshtml(.cs)` | 에이전트 표에 "타임아웃(초)" 칸 추가. 빈 칸에는 실제로 적용될 값을 안내 문구로 보여준다 (`제공사 300` / `기본 100`). 비우고 저장하면 키를 지운다 |
| `tests/HB.Orchestration.Tests` (새 프로젝트) | xUnit. `Directory.Build.props`의 net10.0을 그대로 받는다. slnx에 `/tests/` 폴더 추가 |
| README | `Providers.*.TimeoutSeconds`, `Agents[].TimeoutSeconds` 행 추가 |

### 이렇게 정한 이유와 버린 대안

**제공사 생성 함수에 `AgentOptions`를 통째로 넘긴다.**
- 대안 1은 `TimeSpan?` 인자 하나만 추가하는 것이었다.
- 하지만 2-2에서도 에이전트 단위 값(재시도 정책)이 생성 시점에 필요하다. 인자를 하나씩 늘리면 시그니처가 두 번 바뀐다.
- 구현체가 하나뿐이라 시그니처 변경의 영향은 서버 안에서 끝난다.

**값이 없어도 100초를 명시한다.**
- SDK 기본값에 기대면 "실제 적용 값"이 코드에 드러나지 않는다.
- 같은 이유로 대시보드 빈 칸에도 실제 적용 값을 보여준다.

**대시보드의 빈 칸 안내.**
- 같은 제공사의 에이전트라도 칸이 비면 제공사 값을 쓴다.
- 이걸 화면에서 바로 알 수 있도록, 스냅샷에 제공사별 타임아웃(`ProviderTimeouts`)을 추가했다.

### 테스트

`AgentSettingsTests` 5건이 모두 통과했다.

| 경우 | 기대 결과 |
|---|---|
| 에이전트 30 / 제공사 300 | 30초 |
| 에이전트 없음 / 제공사 300 | 300초 |
| 둘 다 없음 (제공사 null 포함) | 100초 |
| 에이전트 0 또는 -5 | 제공사 값 (0 이하는 무시) |

솔루션 빌드는 경고 0, 오류 0이었다.

### 예상과 달랐던 것

- `dotnet new xunit`이 만든 csproj에 `TargetFramework`가 들어 있었다. `Directory.Build.props`와 중복이라 지웠다.

---

## 2-2. 서버가 직접 재시도하고 기록

### 바꾼 것

| 파일 | 내용 |
|---|---|
| `Providers/OpenAICompatibleProvider.cs` | `RetryPolicy = new ClientRetryPolicy(maxRetries: 0)` — SDK 자동 재시도를 끔 |
| `OrchestrationOptions.cs` | `AgentOptions.MaxAttempts`, `ProviderOptions.MaxAttempts` (int?) |
| `AgentSettings.cs` | `ResolveMaxAttempts`: 에이전트 → 제공사 → 2, 0 이하는 설정 안 함, 상한 5 |
| `RetryClassifier.cs` (새 파일) | `ErrorKind`(Timeout · RateLimit · Server · Client · Network), `Classify`, `IsUserCancellation`, `DelayBeforeNext`, Retry-After 해석 |
| `AgentRunner.cs` | 시도 루프. 시도마다 기록 한 줄, 대기 함수·기록 경로 주입(테스트용 internal 생성자), 최종 실패 문구에 조정할 설정 안내 |
| `AgentCatalog.cs` | `AgentEntry.ProviderOptions` 추가. 실행 시점에 제공사 기본값을 볼 수 있게 함 |
| `ModeRunner.cs` | 단계 제목 아래 또는 결과 끝에 `(재시도 1회 후 성공 · 1회차 Timeout)` |
| `Shared/CallRecord.cs` | `Attempt`(int?, 예전 기록은 null = 1), `ErrorKind`(string?) |
| 대시보드 | 목록에 `시도 n/N` 배지. 상세에 상태 옆 ErrorKind와 "시도 n / N", 단계 목록에 시도 번호. `--warn` 색상 추가(2-4 경고 배지에도 씀) |
| README | MaxAttempts 행, "재시도" 절(오류별 재시도 표, **최대 대기 ≈ 타임아웃 × 시도 횟수**) |

### 이렇게 정한 이유와 버린 대안

- **사용자 취소와 타임아웃은 `ct.IsCancellationRequested`로 가른다.**
  - SDK 재시도를 끄면 `NetworkTimeout`이 `TaskCanceledException`으로 올라온다. 기존 `catch (… when ex is not OperationCanceledException)`이 이 예외를 그대로 통과시켜 **run_mode 밖으로 예외가 새는 문제**가 있었다. 계획 단계에서 찾은 위험이다.
  - 호출자 토큰이 살아 있는데 취소 예외가 났다면 타임아웃으로 본다.
  - 예외 메시지("timeout" 문자열)로 판별하는 방식은 SDK 버전마다 바뀔 수 있어 버렸다.
- **AggregateException은 벗긴다.** SDK가 재시도를 켜 두면 감싸서 던지고(3장 기록 원문), 다른 경로에서도 감쌀 수 있어 방어적으로 처리했다.
- **408은 Timeout으로 재시도한다.** 지시서의 "그 외 4xx"에 해당하지만 의미가 타임아웃이라 재시도 쪽에 넣었다 (지시서 범위 밖의 결정).
- **`retry-after-ms`를 먼저 본다.** OpenAI 호환 API가 함께 보내는 밀리초 헤더다. 둘 다 없으면 2초 → 5초.
- **빈 응답은 재시도하지 않는다.** 원인이 출력 상한이라 다시 보내도 같은 결과가 나온다.
- **단계 시간은 재시도와 대기를 포함한 전체 시간, 토큰은 마지막 시도 값.** 실패한 시도는 보통 토큰이 없다. 머리말(2-3)에 그대로 쓴다.
- **대기 함수를 주입한다.** 이게 없으면 재시도 테스트가 실제로 2초·5초를 기다린다. DI에서 헷갈리지 않도록 주입용 생성자는 internal로 두고 `InternalsVisibleTo`로 테스트에만 연다.

### review 결과 (gpt_main_sol, run `00b061adf8`)

- 사용량: 입력 9,475 / 출력 1,220 토큰, 33.1초, **$0.0312 (약 ₩43)**
- 입력에는 목적·요구사항·배경만 넣었고, 코드는 `context` 6개 범위로 넘겼다.

**먼저 완성한 Claude의 자체 검토** (결과를 보기 전)

| ID | 내용 | 처리 |
|---|---|---|
| C1 | 대시보드 목록의 "실패" 수가 시도 단위가 됨. 재시도 후 성공한 단계도 실패 1건으로 셈 | 유지. 호출 단위 통계라 의도대로다. 로그에 기록만 남긴다 |
| C2 | 시도마다 Prompt 전문이 기록돼 calls.jsonl이 커짐 | 유지. 재시도는 드물고, 시도별 원문이 있어야 상세 화면이 독립적으로 보인다 |
| C3 | 408 재시도는 지시서 범위 밖의 결정 | 위 "이유"에 기록 |
| C4 | 동시 실행 모드에서 한 단계의 재시도가 전체 시간을 늘림 | 의도된 동작 |
| C5 | 타임아웃이 실제 SDK에서 `TaskCanceledException`으로 오는지는 가짜 클라이언트로만 확인함 | 4절 실제 실행 검증(5초 타임아웃)에서 확인 |

**에이전트 지적과 판정**

| # | 지적 | 판정 | 이유 / 조치 |
|---|---|---|---|
| R1 (major) | 사용자 취소된 시도가 calls.jsonl에 안 남음 | **채택** | 요청은 이미 나가 비용이 생겼을 수 있다. "시도마다 한 줄"에도 맞다. 취소된 시도를 `ErrorKind=Canceled`로 기록한 뒤 예외를 그대로 던지게 했다. 기록에 실패해도 취소 전파는 막지 않는다. 지시서의 5종에 `Canceled`를 더했다 |
| R2 (minor) | Last 모드에서 뒤 단계가 실패하면 앞 단계의 재시도 알림이 사라짐 | **채택** | 코드상 `failed is null`일 때만 알림을 모았다. 출력에서 빠지는 단계의 알림은 끝의 note로 모으게 바꿨다 |
| R3 (minor) | TimeSpan 범위를 넘는 Retry-After(1e15, Infinity)는 변환 예외 → "헤더 없음" → 2초 뒤 재시도 | **채택** | 직접 확인: `TimeSpan.FromSeconds(1e15)`는 OverflowException을 내고, `double.TryParse`는 "Infinity"를 받는다. `SafeSeconds`로 0~1일 범위에 맞춰 상한 초과로 처리되게 했다 |

공통 지적은 없었다 (에이전트 1개 모드). 의견 충돌도 없었다.

**예상과 달랐던 것: 오케스트레이션 자기 코드는 바로 리뷰를 맡길 수 없었다**
- `context` 경로의 기준은 MCP 서버의 프로젝트 루트(= Claude Code 세션 폴더 `F:\AI\ToolCreate`)다.
- 오케스트레이션 저장소(`F:\AI\ToolWithCS`)는 그 바깥이라 "루트 밖"으로 거절된다.
- 링크로 연결하는 방법은 ContextLoader가 링크 경유를 거절하므로 쓸 수 없다.
- 그래서 대상 파일을 `F:\AI\ToolCreate\Temp\ch4-review\2-2\`(Unity Temp, git 제외)에 **복사**해서 넘겼다.

### 테스트

전체 29건 통과 (이 항목에서 24건 추가). 지시서의 "재시도 분류" 기준을 모두 충족했다.

| 테스트 | 확인한 것 |
|---|---|
| `RetryClassifierTests` | 타임아웃(그대로, AggregateException으로 감싼 것) · 429 · 500 · 503 → 재시도. 400 · 401 · 402 · 404 → 재시도 안 함. 사용자 취소 판별. 응답 없음 · HttpRequestException · IOException → Network. 알 수 없는 예외 → 재시도 안 함. Retry-After(초 · ms · 상한 초과 · 비정상적으로 큰 값). 기본 대기 2초 → 5초 |
| `AgentRunnerRetryTests` | 타임아웃 → 성공: 기록 2줄(Attempt 1·2, 같은 RunId·Step, 첫 줄 ErrorKind=Timeout), 대기 [2초], 안내 문구 `(재시도 1회 후 성공 · 1회차 Timeout)` |
| | 계속 타임아웃 + MaxAttempts 3: 호출 3회, 기록 3줄, `[실패] Timeout — 3/3회…`, 대기 [2, 5] |
| | 기본값: 500이 계속되면 2회로 끝 |
| | 402 · 401 + MaxAttempts 5: 1회로 끝, 기록 1줄(Client), 대기 없음 |
| | 사용자 취소: 예외를 그대로 던지고 기록 1줄(Canceled) (R1 반영 후) |
| | Retry-After 600초: 기다리지 않고 1회로 실패 |

R2(ModeRunner 결과 조립)는 단위 테스트가 없다. ModeRunner가 설정 파일·세션 추적에 묶여 있어 테스트로 떼어 내는 비용이 크다고 봤다. 2-3에서 결과 조립을 함수로 분리할 때 테스트를 붙인다.
