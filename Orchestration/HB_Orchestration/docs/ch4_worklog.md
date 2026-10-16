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

**예상과 달랐던 것: 자기 코드를 리뷰하려다 context 루트 제한에 막혀 Temp로 복사해 우회했다**
- `context` 경로의 기준은 MCP 서버의 프로젝트 루트(= Claude Code 세션 폴더 `F:\AI\ToolCreate`)다.
- 오케스트레이션 저장소(`F:\AI\ToolWithCS`)는 그 바깥이라 "루트 밖"으로 거절된다.
- 링크로 연결하는 방법은 ContextLoader가 링크 경유를 거절하므로 쓸 수 없다.
- 그래서 대상 파일을 `F:\AI\ToolCreate\Temp\ch4-review\2-2\`(Unity Temp, git 제외)에 **복사**해서 넘겼다.

#### 복사본 사후 확인 (사용자 요청)

**1. 복사 방법:** 셸 명령 `cp`로 원본 6개 파일을 바이트 그대로 복사했다. 내용을 다시 쓰거나 손대지 않았다.
- 실행한 명령: `cp $S/HB.Orchestration.Agents/{AgentRunner.cs,RetryClassifier.cs,AgentSettings.cs,ModeRunner.cs} $S/HB.Orchestration.Agents/Providers/OpenAICompatibleProvider.cs $S/HB.Orchestration.Shared/CallRecord.cs $D/`
- 복사 시각은 19:19, 리뷰 지적 반영(원본 수정)보다 앞이다.

**2. 비교** — Temp 복사본이 남아 있어서 세 가지를 확인했다.

**(a) 복사본과 현재 원본의 SHA-256**

| 파일 | 결과 |
|---|---|
| AgentSettings.cs, OpenAICompatibleProvider.cs | **일치**. 리뷰 후 수정하지 않은 파일이다 |
| AgentRunner.cs, RetryClassifier.cs, ModeRunner.cs, CallRecord.cs | 불일치. 리뷰 후 지적을 반영하며 원본을 고친 파일이라 예상된 결과다 |

리뷰 시점의 원본은 git에 없다 (커밋 `190a4a0`과 `883ac73` 사이). 그래서 불일치한 4개는 줄 단위 diff로 차이가 어디인지 확인했다. 줄바꿈 차이는 무시했다.

| 파일 | 차이 위치 | 해당 반영 |
|---|---|---|
| AgentRunner.cs | 161 → 161~175 | R1: 사용자 취소 시도 기록 |
| RetryClassifier.cs | 114, 119, 129~139 추가 | R3: `SafeSeconds` |
| ModeRunner.cs | 120~133 | R2: 출력에서 빠지는 단계의 재시도 알림 |
| CallRecord.cs | 53 | `Canceled` 주석 |

**차이는 모두 반영한 수정 위치뿐**이고, 그 밖의 줄은 같다.

**(b) 리뷰 시점에 실제로 보낸 내용과 복사본** (calls.jsonl run `00b061adf8`의 Prompt)
- 첨부의 각 범위에서 줄 번호 접두사 `N| `를 떼고 복사본의 같은 줄 범위와 비교했다.
- 첨부 텍스트는 서버의 `AppendLine` 때문에 CRLF라서, 비교 전에 줄바꿈을 맞췄다.

| 범위 | 줄 수 | 줄 번호 연속 | 불일치 |
|---|---|---|---|
| AgentRunner.cs:1-222 | 222/222 | O | 0 |
| RetryClassifier.cs:1-130 | 130/130 | O | 0 |
| AgentSettings.cs:1-27 | 27/27 | O | 0 |
| OpenAICompatibleProvider.cs:17-50 | 34/34 | O | 0 |
| ModeRunner.cs:90-210 | 121/121 | O | 0 |
| CallRecord.cs:40-61 | 22/22 | O | 0 |

**556줄 전부 일치.**

**(c) 리뷰 지적의 줄 번호와 복사본의 코드**
- R1 `AgentRunner.cs:161` = `catch (… when (!RetryClassifier.IsUserCancellation(ex, ct)))`
- R1 `:69-74` = 시도 기록 부분
- R2 `ModeRunner.cs:120-` = 재시도 알림 조립
- R3 `RetryClassifier.cs:118-119` = `TimeSpan.FromSeconds(seconds)`

세 지적 모두 가리킨 위치와 내용이 일치한다.

**3. 결론:** 불일치가 없으므로 리뷰 지적을 다시 확인할 필요는 없다. 에이전트는 리뷰 시점의 원본과 같은 코드를 봤다.

**4. 정리:** 확인 후 `F:\AI\ToolCreate\Temp\ch4-review\`를 삭제했다. Unity 프로젝트 git 상태에도 Temp 흔적이 없다.

#### 근본 해결 후보 (아직 결정하지 않음)

| 후보 | 방법 | 장점 | 단점 |
|---|---|---|---|
| A. 저장소별 `.mcp.json` | 오케스트레이션 저장소에도 `.mcp.json`을 두고, 그 저장소에서 Claude Code를 연다. 루트가 그 저장소가 된다 | 코드 변경 없음. 루트 규칙을 그대로 유지 | 작업마다 세션을 옮겨야 한다. Unity와 서버 코드를 함께 다루는 작업(이번처럼)에는 맞지 않는다 |
| B. 허용 루트 추가 설정 | 예: `Context.ExtraRoots: ["F:/AI/ToolWithCS"]`. 경로를 `<루트 이름>:상대경로`로 지정하고, 루트마다 같은 검사(루트 밖·링크·DenyPatterns)를 적용 | 세션을 옮기지 않는다. 복사가 필요 없어 원본과 다를 위험 자체가 없다 | 외부로 보낼 수 있는 범위가 넓어진다. 설정 실수 한 번에 다른 저장소의 비밀 파일이 나갈 수 있으니 기본은 비움(= 기존 동작)으로 둔다 |
| (현재) Temp 복사 | `cp`로 복사한 뒤 context로 첨부 | 즉시 가능 | 복사 시점과 원본이 어긋날 수 있다. 이번에는 사후 비교로 확인했지만 매번 확인 비용이 든다 |

근본 해결 방식은 사용자가 정한다.

#### 운영 규칙 추가 (사용자, Unity 프로젝트 `CLAUDE.md`)

사후 확인 뒤 사용자가 Unity 프로젝트의 `CLAUDE.md`에 아래 절을 추가했다. 2-3 review부터 이 규칙을 따른다.

> ### 프로젝트 루트 밖의 코드를 리뷰할 때
>
> * `context`는 이 세션을 연 폴더(프로젝트 루트) 안의 파일만 읽는다. 밖의 코드는 그대로 넘길 수 없다.
> * 가능하면 그 코드가 있는 저장소에서 세션을 다시 연다.
> * 복사가 꼭 필요하면:
>   * Unity `Temp`는 쓰지 않는다 (에디터가 지운다). `Assets` 밖의 전용 폴더(예: `review_tmp/`, .gitignore 등록)를 쓴다.
>   * 내용을 다시 쓰지 말고 명령으로 복사한다.
>   * 원본과 사본의 SHA-256이 같은지 확인한 뒤 `context`로 넘긴다.
>   * 리뷰가 끝나면 사본을 지운다.

**2-2 방식과 달라지는 점**

| 항목 | 2-2에서 한 것 | 규칙 이후 |
|---|---|---|
| 복사 위치 | Unity `Temp/ch4-review/` | `Assets` 밖 전용 폴더 (`review_tmp/`, .gitignore 등록) |
| 해시 확인 시점 | 리뷰 **후** 사후 확인 (사용자 요청) | 리뷰 **전**, 원본과 사본이 같은지 확인한 뒤 넘김 |
| 복사 방법 | 명령(`cp`) | 같음 (명시됨) |
| 정리 | 확인 후 삭제 | 같음 (명시됨) |

**Temp를 쓰면 안 되는 이유:** 에디터가 Temp를 지울 수 있다. 리뷰 도중 사본이 사라지면 서버가 `[첨부 코드 오류] 파일이 없습니다`로 거절한다. 사후 확인할 사본도 남지 않는다.
- 2-2에서는 사본이 남아 있어 사후 비교를 할 수 있었다.
- 그것은 운이 좋았던 것이지, 절차가 보장한 결과가 아니다.

**이 규칙으로 바뀌는 점:**
- 사후 확인은 "사본이 원본과 같았는가"를 보내기 전에 미리 보장하는 절차가 됐다.
- 기록용으로는 "보낸 내용 = 사본"(calls.jsonl 첨부 대조)만 남기면 된다.

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

---

## 2-4. 응답 검사기

### 바꾼 것

| 파일 | 내용 |
|---|---|
| `Agents/ResponseChecker.cs` (새 파일) | `EmptyFailure`(기존 빈 응답 검사를 옮김), `Warnings`(잘림·언어 이탈), `StripCode`, `CountKana`, `IsSupportedLanguage` |
| `OrchestrationOptions.cs` | `ResponseChecksOptions { ExpectedLanguage = "ko", ForeignScriptRatio = 0.02 }`. 0 이하나 1 초과면 기본값 |
| `AgentRunner.cs` | `StepCall.Checks`, `StepResult.Warnings`·`Notices`, `CheckWarnings`(예외를 격리해 경고 없이 진행하고 로그만 남김) |
| `ModeRunner.cs` | 단계마다 실행 시점의 `Options.ResponseChecks`를 넘긴다. 결과의 단계 아래(또는 끝)에 재시도 알림과 경고를 한 줄씩 |
| `Shared/CallRecord.cs` | `Warnings` (기본값 빈 목록) |
| 대시보드 | 목록에 `경고 n` 배지(마우스를 올리면 내용). 상세에 경고 패널, 단계 목록에 배지 |
| README | ResponseChecks 행, "응답 검사기" 절 |

### 이렇게 정한 이유와 버린 대안

**`ExpectedLanguage`: 키가 없으면 `ko`, `""`이면 끔 — "비우면 기존 동작" 원칙의 의도적 예외 (사용자 승인)**
- 경고는 실패가 아니다. 응답·성공 여부·다음 단계 입력이 모두 그대로라서, 기존 설정 파일의 실행 결과가 바뀌지 않는다. 바뀌는 것은 결과에 붙는 안내 줄과 기록뿐이다.
- 3장의 문제는 "사람이 대시보드를 보기 전엔 몰랐다"였다. 검사를 기본으로 꺼 두면 설정을 따로 켠 사람만 보호받으니, 같은 문제가 되풀이된다.
- 그래서 "비움"은 **명시적 `""`**로만 해석한다. 바인딩 동작(키 없음 → `ko`, `""` → `""`)은 실제 `ConfigurationBuilder`·`Get<T>()`로 테스트해 확인했다.

**종료 이유는 `AgentResponse.FinishReason`에서 바로 읽는다.**
- 계획에서는 `RawRepresentation`을 `ChatResponse`로 꺼낼 생각이었다.
- 패키지 문서(Microsoft.Agents.AI.Abstractions 1.25.0 XML)를 확인해 보니 `AgentResponse`에 `FinishReason`이 직접 있었다. 타입 변환이 필요 없어 그대로 썼다.
- 빈 응답의 상한 안내도 기존 추정(출력 ≥ 상한의 95%)에 더해, 종료 이유가 `Length`이면 확실히 상한으로 판단한다.

**언어 판정 규칙**
- 펜스 코드 블록(```` ``` ```` / `~~~`, 닫히지 않은 펜스는 끝까지)과 인라인 코드를 뺀다. 코드 주석과 식별자의 일본어는 응답 언어가 아니다.
- 분모는 `char.IsLetter`다. 숫자·기호·공백은 넣지 않는다. 가타카나 블록의 `・`(U+30FB)는 문장 부호라 세지 않는다. 한국어 문서에도 쓰이는 문자다.
- **가나 5자 미만이면 경고하지 않는다.** 짧은 응답에서 고유명사 하나로 오탐이 나는 것을 막는다.

**검사기 예외 격리:** `CheckWarnings`의 try/catch로 감싼다. 빈 응답 검사는 문자열 확인뿐이라 예외 경로가 없으므로 격리 밖에 둔다. 빈 응답은 실패로 남아야 하기 때문이다.

**지원하지 않는 언어 값(`en` 등):** 언어 검사를 건너뛰고 경고 로그를 남긴다. 호출마다 로그가 남지만, 설정 실수를 숨기지 않는 쪽을 택했다.

**버린 대안: 한자 비율까지 보는 방식.** 한국어 기술 문서에도 한자어 표기가 섞일 수 있어 기준을 따로 세워야 한다. 지시서 범위인 가나만 보고, 한계로 README에 적었다.

### 테스트

전체 44건 통과 (이 항목에서 15건 추가).

| 테스트 | 결과 |
|---|---|
| **3장 실제 일본어 응답** (cross_p1 / Cross_P1_A, run `3169f4a54e`) | 경고 `가나 비율 10.7%, 308자` |
| **3장 정상 한국어 응답** (cross_p2 / Cross_P2_A, run `d49d0cf935`) | 경고 없음 (가나 0 / 3,099자) |
| 일본어 주석만 있는 코드 블록(``` · ~~~) + 일본어 인라인 코드 + 한국어 본문 | 경고 없음 (코드 블록 제외 확인) |
| 닫히지 않은 펜스 / 가나 4자 / `・` | 경고 없음 · 가나로 세지 않음 |
| `ExpectedLanguage` `""` · `en` | 언어 검사 안 함 |
| 종료 이유 `Length` | 잘림 경고 |
| 빈 응답 | 실패 문구 유지 (상한 안내 / 일반 안내) |
| 설정 바인딩 | 키 없음 → `ko`, 빈 섹션 → `ko`, `""` → 끔 |
| 실행 경로 (가짜 클라이언트) | 일본어 + `Length` → 성공, 응답 원문 그대로, 경고 2건이 결과와 기록에 모두 남음. 정상 응답 → 경고 없음 |

테스트 데이터는 calls.jsonl의 Response 원문을 그대로 `tests/…/TestData/*.md`로 옮겼다 (스크립트로 추출, 내용은 손대지 않음).

### 예상과 달랐던 것

**계획 단계의 "9.1%"는 실제 검사기 값과 달랐다.**
- 계획 때는 Python `\w`(숫자·`_` 포함)를 분모로 대략 계산했다.
- 검사기 규칙(글자만, 인라인 코드 제외)으로 다시 계산하니 **10.7% (308 / 2,872)**였다.
- 기준 2%와의 차이는 여전히 크다. README 예시와 테스트 기대값은 실제 값으로 고정했다.

**비율 표기를 `P1` 서식으로 쓰면 문화권에 따라 `10.7 %`처럼 바뀔 수 있다.** 결과 문구가 환경마다 달라지지 않도록 InvariantCulture 숫자 + `%`로 고정했다.

---

## 2-3. 결과 머리말과 `save_result`

### 바꾼 것

| 파일 | 내용 |
|---|---|
| `Agents/ResultFormatter.cs` (새 파일) | 결과 조립을 ModeRunner에서 분리. 맨 위 `runId: …`, 단계 제목 `### N. 역할 · 에이전트 (입력 a / 출력 b, t초)`, 그 아래 재시도·경고 줄. 출력에서 빠지는 단계의 알림은 끝에 모음 |
| `Agents/ResultSaver.cs` (새 파일) | 기록 찾기(같은 단계면 성공한 시도), 코드 블록 선택, 경로 검사, UTF-8(BOM 없음) 저장, 경로·바이트·SHA-256 반환. 실패는 `[저장 불가] …` |
| `Agents/SafePath.cs` (새 파일) | ContextLoader의 경로 검사를 읽기·쓰기 공용으로 분리. 쓰기는 아직 없는 파일·폴더를 허용하고, 이미 있는 부분까지 링크를 검사 |
| `Agents/CodeFences.cs` (새 파일) | 펜스 코드 블록 파서. 응답 검사기의 코드 제외와 save_result의 `codeBlock`이 같은 규칙을 씀 |
| `ResponseChecker.cs` | `StripCode`가 CodeFences를 쓰게 함 |
| `ContextLoader.cs` | `SafePath.Resolve(..., Access.Read)` 호출로 교체 (동작 동일) |
| `ModeRunner.cs` | 결과 조립을 `ResultFormatter.Format`으로. `SaveResult` 위임 (쓰기 금지 패턴 = `Context.AllDenyPatterns`) |
| `Mcp/ModeTools.cs` | `save_result` 도구 (설명 한 문장, 인자 설명은 짧게) |
| `project-files/CLAUDE.md`, Unity `CLAUDE.md` | "결과 저장" 절: 응답을 파일로 남길 때는 save_result, 직접 다시 쓰지 않음 |
| README | 구조도에 save_result, "run_mode 결과 형식"·"save_result" 절, 토큰 절약 표의 도구 수 3 → 4 |

### 도구를 하나 늘린 이유 (도구 수 3 → 4)

도구 설명은 지휘자 컨텍스트에 늘 올라가므로 도구 추가에는 상시 비용이 있다. 그 대신 얻는 것은 세 가지다.

1. **재출력 토큰 절약.** 3장에서는 지휘자가 수천 토큰짜리 응답을 파일에 옮기려고 그대로 다시 출력했다. 이제 인자 몇 개만 쓰면 된다.
2. **옮겨 적기 오류 제거.** 서버가 calls.jsonl의 원문을 그대로 쓴다. 테스트에서 줄바꿈이 섞인 원문(CRLF·LF)과 `<`가 바이트 단위로 보존됨을 확인했다.
3. **저장 위치 고정.** 3장에서는 세션이 다른 폴더에서 열려 저장 위치가 이탈했다. 이제 기준이 세션 폴더가 아니라 그 기록의 `ProjectDir`이다.

설명 비용을 줄이려고 도구 설명은 한 문장으로 두고, 인자 설명도 각각 한 구절로 줄였다.

### 이렇게 정한 이유와 버린 대안

- **`Return: Last`에도 머리말을 붙인다.** 붙이지 않으면 save_result에 넘길 runId를 알 수 없다. 출력 형태가 바뀌는 지점이지만 늘어나는 양은 두 줄이다.
- **실행 전 거절 메시지에는 runId가 없다.** 아직 실행 전이라 기록도 없다. 기존처럼 `[`로 시작하는 한 줄로 둔다.
- **쓰기에도 DenyPatterns를 적용한다.** `.env`, `*.pem`, `.git/**` 같은 파일을 덮어쓰지 않게 하기 위해서다. 메시지는 읽기("외부 전송 금지")와 구분해 "보호된 파일이라 쓸 수 없습니다"로 했다.
- **`overwrite=false`는 `FileMode.CreateNew`로 처리한다.** 존재 확인과 생성이 한 번에 일어나므로, 확인과 쓰기 사이에 생긴 파일도 덮지 않는다.
- **코드 블록 파서를 하나로 합쳤다.**
  - 2-4의 `StripCode`는 펜스 길이를 보지 않았다. 그래서 ```` ```` ```` 안의 ```` ``` ````에서 블록이 끝나 버렸다.
  - save_result의 N번째 블록 선택과 검사기의 코드 제외가 서로 다르게 세면 혼란스러우므로 공용 파서로 바꾸면서 이 문제도 고쳤다.
  - 바꾼 뒤에도 3장 일본어 응답은 여전히 10.7%였다 (테스트 고정값 통과).
- **결과 줄바꿈이 CRLF에서 LF로 바뀌었다.** 기존 조립은 `AppendLine`(Windows에서 CRLF)이었다. ResultFormatter는 `'\n'`을 쓴다. 에이전트 응답 본문은 손대지 않는다.

### review 결과 (gpt_main_sol, run `1133a77981`)

- 사용량: 입력 9,006(캐시 1,093) / 출력 2,099 토큰, 59.4초, **$0.0369 (약 ₩51)**
- `context` 7개 범위, 485줄

**복사본 절차** (사용자 규칙을 처음 적용)
1. `F:\AI\ToolCreate\review_tmp\2-3\`에 `cp`로 7개 파일을 복사했다. `review_tmp/`는 Unity `.gitignore`에 없어서 `/review_tmp/`를 추가했다. 이 변경은 Unity 저장소에 미커밋으로 남아 있다.
2. **보내기 전** SHA-256 비교: 7개 모두 원본과 일치.
3. 리뷰 후 calls.jsonl 첨부 485줄을 복사본과 대조: 불일치 0.
4. 복사본 삭제.

**먼저 완성한 Claude의 자체 검토** (결과를 보기 전)

| ID | 내용 | 처리 |
|---|---|---|
| C1 | 결과 줄바꿈이 CRLF에서 LF로 바뀜 | 유지. 위 "이유"에 기록 |
| C2 | save_result가 호출마다 calls.jsonl 전체를 읽음 (현재 약 1.2MB) | 유지. `CheckBudget`과 같은 방식이다. 커지면 끝에서부터 읽게 바꾼다 |
| C3 | 경로 검사와 쓰기 사이의 링크 바꿔치기(TOCTOU) | 범위 밖. SafePath 주석에 한계로 남김 (단일 사용자 PC 전제) |
| C4 | 쓰기에 실패해도 먼저 만든 하위 폴더는 남음 | 유지. 빈 폴더만 남고 해가 없다 |
| C5 | `Return: Last` 출력에 두 줄이 늘어남 | 의도된 변경 |

**에이전트 지적과 판정**

| # | 지적 | 판정 | 이유 / 조치 |
|---|---|---|---|
| R1 (major) | 경로 포함 여부를 대소문자 무시로 비교해, 대소문자를 구분하는 파일 시스템에서는 `../PROJ/x`로 루트를 벗어날 수 있음 | **채택 (플랫폼 한정)** | 현재 환경(Windows)과 macOS 기본(APFS)은 대소문자를 구분하지 않아 영향이 없다. 하지만 Linux에서는 맞는 지적이고, 고치는 비용이 작다. `PathComparison`을 Windows·macOS는 무시, 그 외는 구분으로 바꿨다. 이 동작은 ContextLoader에서 물려받은 것이라 읽기 쪽도 함께 고쳐졌다 |
| R2 (major) | `ProjectDir` 자체나 그 상위 폴더가 링크면 검사하지 않음 | **기각** | 링크 검사의 목적은 루트 **안**의 링크로 루트 밖에 쓰는 것을 막는 데 있다. 루트는 사용자가 연 프로젝트 폴더(기록의 ProjectDir)라서, 루트가 junction이면 그 대상이 곧 프로젝트다. 루트를 거절하면 정상 설정(링크로 연 프로젝트)이 깨진다. 기존 ContextLoader도 같은 규칙이었다. 이 근거를 SafePath 주석에 명시했다 |
| R3 (major) | 경로 검사 중 권한·입출력 예외가 `[저장 불가]` 문자열이 아니라 예외로 새어 나감 | **채택** | 읽기 쪽은 ContextLoader의 try가 감싸지만 save_result에는 그런 곳이 없다. `SafePath.Resolve`가 IOException · UnauthorizedAccessException · SecurityException을 `경로 확인 실패 (…)` 메시지로 바꾸게 했다 |
| R4 (minor) | Concurrent 실행이 `Return: Last`를 무시함 | **기각** | 기존부터 의도된 설계다. README 189줄에 "Concurrent는 항상 All"이라고 적혀 있다. 동시 실행은 단계 사이에 순서가 없어 "마지막"이 의미가 없다. 내 input에 이 전제를 적지 않아 생긴 오해다 |
| R5 (minor) | 루트가 `C:\` 같은 드라이브 루트면 구분자를 한 번 더 붙여(`C:\`) 모든 경로를 거절함 | **채택** | `TrimEndingDirectorySeparator`는 드라이브 루트의 끝 구분자를 남긴다. 루트가 구분자로 끝나지 않을 때만 붙이게 했다. 테스트 추가 |

공통 지적은 없었다 (에이전트 1개 모드). 의견 충돌도 없었다. 기각한 2건은 근거가 코드·README에 있어 사용자에게 묻지 않았다.

### 테스트

전체 67건 통과 (이 항목에서 23건 추가). 지시서의 "`save_result` 경로" 기준을 충족했다: `../` 거절, 절대 경로 거절, 루트 안 정상 경로 성공.

| 테스트 | 확인한 것 |
|---|---|
| `SafePathTests` | `../` · `docs/../../` · `..\` → 루트 밖. 절대 경로(루트 안·밖 모두) → 거절. 없는 하위 폴더 경로 → 성공. `.env` → 보호된 파일. 폴더 → 거절. 읽기는 파일 필요. 드라이브 루트를 루트로 쓸 때 하위 경로 성공(R5) |
| `ResultSaverTests` | **저장 파일 바이트 = 기록 원문의 UTF-8 바이트**(BOM 없음, CRLF·LF 혼합, `<` 보존), 반환 SHA-256 일치. 재시도 단계는 성공한 시도를 씀. `codeBlock` 1·2 원문 그대로, 3은 `[저장 불가] 코드 블록이 2개`. 기존 파일은 덮지 않고 overwrite면 덮음. `../`·절대 경로는 쓰기 전에 거절. 없는 runId · 전부 실패한 단계 · 없는 단계 |
| `CodeFencesTests` | ```` ```` ```` 안의 ```` ``` ```` 보존, 닫히지 않은 펜스는 끝까지, ```` ```js ```` 줄은 닫는 펜스가 아님 |
| `ResultFormatterTests` | 머리말 형식(예시와 같은 `입력 15,551 / 출력 7,502, 144.0초`). Last 모드는 마지막 단계와 숨은 단계 알림을 끝에. **2-2 R2 회귀 테스트**(앞 단계 재시도 후 뒤 단계 실패 시 알림 유지). 보이는 단계는 제목 아래 재시도 → 경고 → 본문 순서. 토큰 없음은 `-` |

2-2 작업 로그에 남긴 약속("2-3에서 결과 조립을 분리할 때 R2 테스트를 붙인다")을 지켰다.

### 예상과 달랐던 것

- **드라이브 루트 테스트에서 내 기대값이 틀렸다.** `C:\` 기준 `../a.md`가 "루트 밖"일 거라 예상했다. 실제로는 드라이브 루트 위로 올라갈 수 없어 `C:\a.md`(루트 안)로 정규화된다. 테스트를 실제 동작에 맞췄다. 수정 전 코드라면 이 테스트의 첫 단언(하위 경로 성공)에서 실패한다.
- **Unity 저장소에 `review_tmp/`가 .gitignore에 없었다.** 규칙의 전제("`.gitignore` 등록")를 처음 적용하면서 추가했다. Unity 저장소에는 사용자의 다른 미커밋 변경이 많아 커밋하지 않았다.
- **두 CLAUDE.md의 내용이 갈라져 있다.** Unity `CLAUDE.md`에는 "에이전트(모델) 교체"와 "프로젝트 루트 밖의 코드를 리뷰할 때" 절이 있는데, `project-files/CLAUDE.md`(배포용 원본)에는 없다. 이번에는 두 곳 모두에 "결과 저장" 절만 추가했다. 나머지를 맞출지는 사용자에게 묻는다.

---

## 2-5. `&lt;` 이스케이프 원인 조사

### 바꾼 것

| 파일 | 내용 |
|---|---|
| `OrchestrationOptions.cs` | `DebugOptions.DumpToolResults` (기본 false) |
| `ModeRunner.cs` | `DumpToolResult`: 켜져 있으면 반환 문자열을 UTF-8(BOM 없음) 그대로 `%LOCALAPPDATA%/HB_Orchestration/dumps/<시각>_run_mode_<mode>.txt`에 쓴다. 실패는 무시한다 (부가 기능) |
| `Mcp/ModeTools.cs` | `run_mode`가 `RunAsync` 결과를 반환하기 **직전**에 덤프한다. 실행 전 거절 메시지까지 모든 반환 경로를 덮는다 |
| README | `Debug.DumpToolResults` 행 |

덤프 위치를 `ModeTools`로 정한 이유: MCP SDK에 넘기기 직전이라 서버 코드가 만든 최종 문자열 그대로이고, 그 뒤는 서버 밖(SDK 직렬화 → Claude Code)이다.

### 진행

1. 2-1~2-5 코드를 publish했다.
   - 배치 파일의 서버 종료 확인(`choice`)은 대화형이라 쓸 수 없었다. 그래서 같은 순서(서버 종료 → 실제 설정 백업 → `dotnet publish` → 설정 비교·복원)를 직접 실행했다.
   - 실행 중이던 MCP 서버가 3개(다른 세션 포함)였다. 다른 세션의 호출도 끊기므로 **사용자 확인을 받고** 종료했다.
   - publish 후 실제 설정 두 파일은 백업과 해시가 같았다 (복원 불필요).
2. 실제 설정에 `Debug.DumpToolResults: true`를 넣고 사용자가 `/mcp`로 재연결했다. `server_info`로 새 빌드(19:37:01)와 review 횟수 초기화(3/3)를 확인했다.
   - 사용자 질문: "publish된 걸 따로 열지 않았는데 연결됐다, 정상인가?" → 정상이다. `.mcp.json`이 `Publish_Output/HB.Orchestration.Mcp.dll`을 직접 실행하므로, 재연결하면 새 DLL이 뜬다.
3. 덤프와 calls.jsonl Response, 지휘자가 받은 결과를 세 번 비교했다.

| 실행 | 조건 | 덤프 `<` / `&lt;` | 기록 원문 `<` / `&lt;` | 지휘자가 받은 결과 |
|---|---|---|---|---|
| review `f233ba0a44` (luna) | 짧은 응답, `i <= values.Count` 같은 비교식 | 2 / 0 | 2 / 0 | `<` 그대로 |
| review `ac206cc33d` (luna) | 짧은 응답, 본문·코드 블록에 `HashSet<int>`, `NativeArray<float3>` (태그 모양) | 4 / 0 | 4 / 0 | `<` 그대로 |
| cross_p3 `ee9e3927a5` (sol + argon) | **3장과 같은 모드·입력·context**, 동시 2단계, 결과 21,700자 | 39 / 0 | 19 + 20 / 0 | `<` 그대로 |

- 모든 실행에서 두 단계 응답 원문이 덤프 안에 그대로 들어 있었다.
- 지시는 "review 1회"였지만 첫 실행이 3장 조건(태그 모양 제네릭, 긴 동시 실행 결과)과 달라 판정할 수 없었다. 그래서 조건을 맞춘 review 1회를 더 했다. 3장과 같은 조건은 4절의 `cross_p3` 실행으로 덮었다.
- 지정대로 luna 2회(`*=gpt_mini_luna`)였다. `cross_p3`는 실제 실행 검증 1번을 겸했다.

### 판정

- **서버 코드 문제가 아니다 (확정).** 서버가 반환하는 문자열(덤프)에 `<`가 그대로 있고 `&lt;`는 0개다. 3장 기록의 원문에도 `&lt;`가 없었다.
- **이번 세션에서는 서버 밖에서도 바뀌지 않았다.** 3장과 같은 모드·입력으로도 재현되지 않았다.
- 3장 현상은 서버 밖(MCP SDK 직렬화 또는 Claude Code가 도구 결과를 표시·전달하는 단계)에서 생겼다고 **추정**한다. 재현이 안 돼 원인 위치를 더 좁히지 못했다.
  - 3장과 다른 점: Claude Code 세션·버전, 서버 빌드(재시도 정책·출력 줄바꿈 CRLF → LF)
  - 이 중 무엇이 영향을 줬는지는 미확인이다.
- 지시대로 **고치지 않고 기록만 남긴다.** 실질적인 영향도 없다. 응답을 파일로 남길 때는 `save_result`가 calls.jsonl 원문을 쓰고, 4절 검증에서 바이트 단위 일치를 확인했다.

### 정리

- 덤프 4개(`review` ×3, `cross_p3` ×1)를 삭제했다. 재시도 검증 실행의 덤프도 포함이다.
- 실제 설정의 `DumpToolResults`를 false로 되돌렸다. 덤프 코드와 설정 키는 다음 조사를 위해 남겼다.

---

## 4. 실제 실행 검증

서버: publish 빌드 19:37:01, PID 3548.

| 항목 | 방법 | 결과 | 판정 |
|---|---|---|---|
| 긴 응답 | 3장 Phase 3 input(705자) + context 5개로 `cross_p3` 1회 (`ee9e3927a5`) | sol: 입력 15,551 / 출력 6,439, **92.9초 완료**. argon: 44.9초. 경고 없음 | **통과** — 3장에서는 같은 단계가 SDK 기본 100초에서 끊겨 4번 시도 후 실패했다 |
| 재시도 기록 | 실제 설정에서 `gpt_main_sol`의 `TimeoutSeconds`를 5로 낮추고 `review` 1회 (`837879e0af`, Unity `CommonFunction.cs:1-150`) | calls.jsonl에 **2줄**: Attempt 1·2, 같은 runId·Step 1, ErrorKind Timeout, 각 약 5.0초, 간격 약 7초(5초 + 대기 2초). 결과: 단계 제목 아래 `(재시도 1회 후 실패 · 1회차 Timeout)`, 본문 `[실패] Timeout — 2/2회 시도 모두 실패. 응답이 5초 안에 오지 않았습니다. … TimeoutSeconds를 늘리세요.` | **통과** — 시도마다 기록, 기본 MaxAttempts(2)만큼만 시도, 결과에 재시도 안내 |
| `save_result` | `ee9e3927a5` 단계 1을 `cross_test/ch4_p3_A.md`로, 같은 단계의 코드 블록 1을 `.cs`로 | `.md` 18,221바이트, SHA-256 `0f43e45d…287786` = calls.jsonl Response의 UTF-8 SHA-256. 바이트 단위 동일, BOM 없음. `.cs`(코드 블록 1/1, 5,707바이트)는 원문의 해당 부분과 일치. 재시도 실행(전부 실패)을 저장하려 하면 `[저장 불가] … 성공한 시도가 없습니다 (시도 2회 모두 실패)`, 파일 생성 없음 | **통과** |
| 결과 머리말 | 위 실행들 | 모든 결과 첫 줄에 `runId: …`. 단계 제목에 `(입력 15,551 / 출력 6,439, 92.9초)` 형식. 전부 실패한 단계는 `(입력 - / 출력 -, 12.0초)`. `Return: Last`인 review에도 붙음 | **통과** |

**2-2 자체 검토 C5 해소:** 실제 SDK에서도 네트워크 타임아웃이 `TaskCanceledException`("…exceeded the configured timeout of 0:00:05…")으로 올라왔다. 사용자 취소가 아니므로 Timeout으로 분류됐고, 예외가 아니라 `[실패]` 문자열로 돌아왔다. 가짜 클라이언트로만 확인했던 가정이 실제로도 맞았다.

**설정 복원:** 5초 타임아웃은 미리 떠 둔 백업으로 되돌렸다. 실제 설정에서 `"TimeoutSeconds": 5`가 0건임을 확인했다.

**저장 파일:** `cross_test/ch4_p3_A.md`와 `.cs`는 검증 산출물로 Unity 프로젝트의 `cross_test/`(사용자 실험 폴더, 미추적)에 남겨 두었다.

### 실제 실행 비용

| runId | 용도 | 에이전트 | 입력 / 출력 | 시간 | 비용 |
|---|---|---|---|---|---|
| `f233ba0a44` | 2-5 조사 1 | luna | 1,199 / 263 | 4.0초 | $0.0003 |
| `ac206cc33d` | 2-5 조사 2 | luna | 1,236(캐시 1,093) / 1,624 | 16.7초 | $0.0008 |
| `ee9e3927a5` | 긴 응답 + 2-5 조사 3 | argon | 15,322 / 3,592 | 44.9초 | $0.0666 |
| | | sol | 15,551 / 6,439 | 92.9초 | $0.0955 |
| `837879e0af` | 재시도 기록 | sol ×2 시도 | 0 / 0 (타임아웃) | 5.0초 + 5.0초 | $0 (기록상) |
| **합계** | | | | | **$0.1631** |

타임아웃으로 끊긴 시도는 응답을 받지 못해 토큰이 기록되지 않았다. 제공사가 끊긴 요청에 비용을 매겼는지는 기록만으로 알 수 없다 (미확인).

### 4장 전체 review 비용

| review | run | 비용 |
|---|---|---|
| 2-2 | `00b061adf8` | $0.0312 |
| 2-3 | `1133a77981` | $0.0369 |
| **합계** | | **$0.0681 (약 ₩94)** |

---

## 4장 마무리

계획서: [plans/2026-10-16_4장-마무리.md](plans/2026-10-16_4장-마무리.md) (대화로 제시한 계획을 사후에 파일로 옮김, 결정 절에 사용자 답 원문)

### 1. 허용 루트 추가 (`Context.ExtraRoots`) — 작업 로그 2-2의 근본 해결 후보 B

**배경:** 2-2·2-3에서 오케스트레이션 저장소의 코드를 리뷰받으려다 `context` 루트 제한에 막혀, Unity 프로젝트 안으로 복사해 우회했다. 복사본은 원본과 어긋날 위험이 있고, 그래서 매번 해시를 확인하는 비용이 들었다.

#### 바꾼 것

| 파일 | 내용 |
|---|---|
| `OrchestrationOptions.cs` (`ContextOptions`) | `ExtraRoots` (이름 → 절대 경로, 기본 비어 있음, 대소문자 무시 사전), `IsValidExtraRootName`, `TryGetExtraRoot`(설정에 적힌 이름을 돌려줌) |
| `ContextLoader.cs` | `@이름/상대경로`이고 등록된 이름이면 그 루트 기준으로 `SafePath.Resolve`. 등록 값 검사는 참조할 때만(`ExtraRootProblem`). 첨부 제목과 출처는 `@이름/…`. `Result.Roots`에 실제로 쓴 루트 |
| `AgentRunner.cs` / `Shared/CallRecord.cs` | `CallMeta.ContextRoots` → `CallRecord.ContextRoots`. `[JsonIgnore(WhenWritingNull)]`로 쓰지 않은 호출은 필드 생략 |
| `ModeRunner.cs` | `list_modes` 마지막 줄 `추가 루트(context 읽기 전용): @이름` (등록이 있을 때만, 이름만). 잘못된 항목은 `[주의]` 줄 |
| `ResultSaver.cs` | `@`로 시작하는 경로는 등록 여부와 관계없이 `[저장 불가] … 추가 루트(@이름)에는 쓸 수 없습니다` |
| 대시보드 `FileLinks.cs`, `Call.cshtml.cs` | 기록의 `ContextRoots`로 `@이름/…` 링크를 실제 파일로 연결 |
| README | 설정 표 행, "추가 루트" 절, save_result 절에 쓰기 금지 |

#### 결정과 이유

| 결정 | 이유 | 버린 대안 |
|---|---|---|
| **경로 지정은 `@이름/상대경로`** | 기존 `path:line`·`path:start-end` 표기, Windows 드라이브 표기(`C:`)와 겹치지 않는다. 도구 스키마를 늘리지 않는다 | `이름:경로` (콜론 표기와 충돌), `ContextRange.root` 필드 (도구 설명이 지휘자 컨텍스트에 늘 올라감) |
| **등록되지 않은 `@이름`은 메인 루트 기준 상대 경로** | ExtraRoots가 비어 있으면 모든 입력이 기존과 똑같이 처리된다 ("비어 있으면 지금과 똑같이") | `@`로 시작하면 무조건 오류 (기존 동작이 바뀜) |
| **루트별 검사는 기존 `SafePath.Resolve`를 그대로** | 메인 루트와 같은 규칙(루트 밖·절대 경로·링크·DenyPatterns)을 코드 하나로 보장한다 | — |
| **등록 값은 참조할 때만 검사, 잘못된 항목은 `list_modes`에 경고만** (사용자 지시) | 쓰지도 않는 항목 하나가 깨졌다고 모든 호출이 막히면, 부가 기능의 실패가 주 기능을 막게 된다 (2장 원칙) | 설정을 읽을 때 전체 검증 후 거절 |
| **이름은 대소문자 무시, 표시는 설정에 적힌 이름** (사용자 지시로 결정) | .NET 설정 시스템이 키를 대소문자 무시로 다룬다 (`"orch"`와 `"ORCH"`는 같은 키). 구분하게 만들 수 없으니 그 동작을 명시하고 테스트로 고정했다 | 대소문자 구분 (설정 계층에서 불가능) |
| **에이전트에게는 `@이름/…`만** | 로컬 절대 경로(사용자 이름, 폴더 구조)를 외부 제공사로 보내지 않는다 | 절대 경로 표시 |
| **기록에는 `ContextRoots`(이름 → 절대 경로)를 쓴 호출에만** (사용자 지시: 안 쓰면 생략) | 나중에 설정을 비우거나 바꿔도 그 기록이 어디서 읽었는지 알 수 있다. 쓰지 않은 호출의 기록은 기존과 같은 모양이다 | 매 기록에 빈 객체 (기록 모양이 바뀜) |
| **읽기 전용 — `save_result`는 `@` 경로를 거절** (사용자 의견과 일치) | 쓰기는 다른 저장소를 바꿀 수 있어 실수의 피해가 크다. save_result의 "저장 위치를 세션 프로젝트로 고정"이라는 목적과도 맞지 않는다. 등록되지 않은 `@이름`도 거절해, 메인 루트에 `@orch` 폴더를 실수로 만드는 일을 막는다 | 쓰기 허용 |
| **루트 자체가 junction이면 허용** | 2-3 review R2 기각과 같은 근거다. 루트는 사용자가 등록한 폴더라서, 그것이 링크면 그 대상이 곧 의도한 저장소다 | — |

#### 테스트 (`ExtraRootsTests` 15건 추가, 전체 82건 통과)

| 사용자 요구 | 테스트 |
|---|---|
| 비어 있을 때 기존 동작 유지 | `Empty_KeepsExistingOutput`: 첨부 텍스트가 기존 형식 문자열과 정확히 같음, Roots null. `Empty_AtPrefix_IsPlainPathAsBefore` |
| 등록은 있으나 기존 경로만 쓰는 경우 바이트 동일 | `Registered_ButOnlyMainPaths_IsByteIdentical`: 빈 설정 결과와 UTF-8 바이트 비교 |
| 쓰지 않는 등록 항목이 깨져 있어도 정상 실행 | `BrokenUnusedEntries_DoNotBlockOtherCalls`: 없는 폴더·상대 경로·이름 규칙 위반을 등록해 두고 메인 경로 → 정상, 텍스트 동일 |
| 등록한 루트 안 정상 경로 성공 | `RegisteredRoot_ReadsFile_AndHidesAbsolutePath`: 출처 `@orch/src/B.cs:1-2`, 텍스트에 절대 경로 없음, Roots 기록 |
| 등록한 루트 밖 거절 | `RegisteredRoot_OutsideIsRejected` (`@orch/../…`) |
| 링크 경유 거절 | `RegisteredRoot_LinkIsRejected`: `mklink /J`로 실제 junction을 만들어 확인 |
| (추가) DenyPatterns | `@orch/.env` 거절 |
| (추가) 참조한 깨진 루트 | 상대 경로 / 없는 폴더 → `ExtraRoots 'orch' — …` |
| 대소문자 처리 | `RootName_IsCaseInsensitive_AndShownAsRegistered` (`@ORCH/` → `@Orch/`), `Binding_KeepsCaseInsensitiveLookup` (실제 `ConfigurationBuilder` 바인딩) |
| `@` 안 쓰면 ContextRoots 생략 | `ContextRoots_IsOmitted_WhenNotUsed`: 기록 줄에 `contextRoots` 문자열 자체가 없음 |
| 읽기 전용 | `SaveResult_RejectsExtraRootPaths` (등록·미등록 모두), 폴더도 생성되지 않음 |

#### review (gpt_main_sol, run `315c6d6108`) — 복사 없이 `@orch/…`로 넘긴 첫 리뷰

- 사용량: 입력 5,414(캐시 1,093) / 출력 643, 23.1초, **$0.0152**
- context 4개 범위(ContextLoader 전체, ContextOptions, ResultSaver 거절부, list_modes 표시부) 229줄

**먼저 완성한 Claude의 자체 검토**

| ID | 내용 | 처리 |
|---|---|---|
| C1 | 이름이 등록되면 메인 루트의 실제 `@이름` 폴더에는 닿을 수 없음 | 유지. `@`로 시작하는 폴더 이름은 드물다. README에 "등록되지 않은 이름만 기존 동작"으로 명시 |
| C2 | calls.jsonl에 추가 루트 절대 경로가 남음 | 유지. 로컬 기록이고 외부로 보내지 않는다. 출처를 추적하려면 필요하다 |
| C3 | list_modes에 이름 규칙을 위반한 항목도 `@x`로 표시됨 | 유지. 바로 아래 `[주의]` 줄이 원인을 알려준다 |

**에이전트 지적**

| # | 지적 | 판정 | 이유 |
|---|---|---|---|
| R1 (major) | 읽기 실패 시 `ex.Message`(예: FileNotFoundException의 전체 경로)가 `[첨부 코드 오류]` 응답에 들어가 로컬 경로가 노출됨 | **기각** | 이 오류는 외부 에이전트를 부르기 **전에** 지휘자(로컬 Claude)에게만 반환된다 (`ModeRunner`: context 오류면 즉시 반환). 요구사항은 "**에이전트에게 보내는** 텍스트에 절대 경로를 넣지 않는다"였다. 메인 루트도 같은 메시지 형식을 쓴다. 지적이 "응답"의 대상을 외부 에이전트로 오해한 것으로 판단했다 |

공통 지적과 의견 충돌은 없었다.

#### 실제 확인

| 항목 | 결과 | 판정 |
|---|---|---|
| publish | 빌드 20:04:34. 이번에는 이 세션의 서버(PID 3548) 하나만 떠 있어 다른 세션 영향 없음. 실제 설정 두 파일은 publish 전후 해시 동일 | — |
| 설정 추가 | 실제 설정에 `Context` 절을 새로 넣음 (원래 없었음, 기본값으로 동작 중이었음). 재시작 없이 다음 `list_modes`부터 `추가 루트(context 읽기 전용): @orch` 표시, 경로는 표시하지 않음 | 통과 |
| 복사 없이 review | `@orch/…` 4개 범위로 review 1회 성공 | 통과 |
| 첨부 = 원본 | calls.jsonl Prompt의 첨부 229줄을 오케스트레이션 저장소 원본과 줄 단위 비교 → 불일치 0 | 통과 |
| **에이전트 입력에 로컬 절대 경로 없음** (사용자 추가 요구) | Prompt에서 `ToolWithCS`, `F:/AI`, `F:\AI`, `C:/Users`, `C:\Users`, `:\`, `:/` 모두 없음 | 통과 |
| ContextRoots 기록 | 해당 기록에 `{"orch": "F:\AI\ToolWithCS\Orchestration\HB_Orchestration"}`. calls.jsonl 41줄 중 이 필드가 있는 줄은 이 1줄뿐 | 통과 |
| **설정을 비운 뒤 list_modes가 이전과 같음** (사용자 추가 요구) | 설정을 백업으로 되돌린 뒤(`ExtraRoots` 0건) 출력을 두 기준과 diff. ① 이전 빌드(ExtraRoots 기능 없음)의 출력 ② 새 빌드에서 설정을 넣기 전 출력. 두 경우 모두 **남은 실행 횟수만 다르고 나머지는 동일** (`추가 루트` 줄 없음). 횟수 차이는 그 사이에 실행한 review와 서버 재시작 때문이다 | 통과 |

**예상과 달랐던 것**
- 실제 설정(`Publish_Output/appsettings.json`)에 `Context` 절이 아예 없었다. 지금까지 `MaxRanges` 등이 모두 코드 기본값으로 동작하고 있었다. ExtraRoots를 넣으려고 절을 새로 만들었고, 비울 때는 절째 원래 파일로 되돌렸다.
- 테스트의 설정 JSON을 C# 원시 문자열 보간으로 쓰다가 중괄호 개수 규칙(CS9007)에 두 번 막혔다. 직렬화로 만드는 방식으로 바꿨다.

### 2. 규칙서 하나로 맞추기

#### 결정

**`project-files/CLAUDE.md`를 원본으로 하고, Unity 프로젝트의 `CLAUDE.md`는 복사본으로 둔다 (사용자 결정).** 이번 통일의 기준은 실제로 MCP를 연결해 쓰는 Unity 쪽 내용이었고, 통일한 뒤에는 원본 방향을 배포용으로 정했다. 규칙을 고칠 때는 원본을 고친 뒤 복사한다. README 5절에도 같은 내용을 적었다.

#### 통일 전 차이 (diff)

| 차이 | 내용 |
|---|---|
| Unity에만 있던 절 | "에이전트(모델) 교체", "프로젝트 루트 밖의 코드를 리뷰할 때" |
| 표기 | 배포용은 번호 목록이 `1\.` |
| 줄바꿈 | 배포용 CRLF, Unity LF |

나머지 본문은 같았다.

#### 기준 파일에서 고친 것 (사용자 승인)

1. **절 위치:** 위 두 절이 `## Unity 공식 문서 참조` 아래 `###`로 붙어 있었다. 둘 다 외부 에이전트 규칙이라 `## 외부 에이전트` 아래로 옮겼다. "루트 밖 리뷰"는 context 규칙 바로 뒤, "에이전트 교체"는 "결과 저장" 뒤에 두었다.
2. **이스케이프 잔재:** `` `run\\_mode` ``, `` `list\\_modes` ``, `` `\\[...]` ``, research 예시의 `\\n\\[1]` 등을 지웠다. 코드 스팬 안에서는 백슬래시가 그대로 보이기 때문이다.
   - **첫 줄의 `@REVIEW\_GUIDELINES.md`도 같은 잔재였다.** 이 세션 시작 때 Claude Code가 불러온 지침에 그 줄은 있었지만, REVIEW_GUIDELINES.md의 내용은 없었다. 그래서 **리뷰 지침이 지휘자 컨텍스트에 들어오지 않고 있었던 것으로 추정**한다. `@REVIEW_GUIDELINES.md`로 고쳤다. 다음 세션에서 지침 내용이 실제로 불러와지는지 확인이 필요하다.
3. 번호 목록은 `1.`, 줄바꿈은 LF (사용자 지시). 들여쓰기가 어긋나 목록 밖으로 빠지던 줄(Unity 테스트 순서 1·2번의 이어지는 줄)도 맞췄다.
4. "에이전트(모델) 교체"의 "이름은 `list_modes` **마지막 줄** 참고"를 "**`에이전트:` 줄** 참고"로 바꿨다. ExtraRoots가 등록되면 마지막 줄이 `추가 루트:`가 되기 때문이다.
5. "루트 밖 리뷰" 절을 ExtraRoots 사용법 중심으로 고쳤다. 복사 절차는 "ExtraRoots를 쓸 수 없을 때"의 대안으로 남기고, `review_tmp/`를 "프로젝트마다 `.gitignore`에 등록"하라는 문구를 더했다.

#### Unity 프로젝트에만 해당하는 내용 (사용자에게 보고)

`project-files/`는 Unity 프로젝트에 복사해 쓰는 템플릿이라 "Unity 테스트"와 "Unity 공식 문서" 절은 원래 양쪽에 다 있다. "이 Unity 프로젝트(ToolCreate)에만 해당"하는 것은 복사 절차가 전제하는 `review_tmp/`의 `.gitignore` 등록 하나다. 이 프로젝트에만 등록돼 있어서, 원본 문구를 "프로젝트마다 등록"으로 바꿔 일반화했다.

#### 결과

- 두 파일의 SHA-256이 같다 (`e9208966…`). CR 0개 (LF).
- 저장소의 `core.autocrlf=true`가 체크아웃 때 CRLF로 바꾸지 않도록 `.gitattributes`에 `project-files/CLAUDE.md text eol=lf`를 추가했다 (`git ls-files --eol`: `i/lf w/lf`).
- Unity 쪽 `CLAUDE.md`는 Unity 저장소에서 미추적(`??`) 파일이라 커밋할 대상이 없다.

### 3. 리뷰 지침 누락 조사

2의 "이스케이프 잔재" 항목에서 추정으로 남긴 것("리뷰 지침이 지휘자 컨텍스트에 들어오지 않고 있었다")을 확인했다. 읽기만 한 조사라 코드·설정은 바꾸지 않았다. 외부 에이전트도 부르지 않았다.

#### 깨진 기간 (git, `ToolWithCS` 저장소 `project-files/CLAUDE.md` 4번째 줄)

| 커밋 | 시각 (KST) | 4번째 줄 |
|---|---|---|
| `d348fb5` 커스텀 오케스트레이션 추가 | 10-15 10:44:36 | `@REVIEW_GUIDELINES.md` (처음 생김. REVIEW_GUIDELINES.md도 이 커밋에서 추가) |
| `901d6e1` unity md path | 10-15 11:01:35 | `@REVIEW\_GUIDELINES.md` (깨짐) |
| `edf46fb` (이 절의 2) | 10-16 20:11:28 | `@REVIEW_GUIDELINES.md` (복구) |

Unity `CLAUDE.md`는 미추적이라 git 이력이 없다. 그래서 시각 비교만으로는 Unity 쪽 복사본도 깨져 있었는지 알 수 없었다. 아래 세션 기록으로 확정했다.

#### 세션 기록 형식 확인 (5장 사전 조사 겸)

- 위치: `~/.claude/projects/F--AI-ToolCreate/<sessionId>.jsonl`, 한 줄에 JSON 하나. `timestamp`는 UTC. `version` 필드에 Claude Code 버전(이번 대상은 `2.1.300`·`2.1.301`)이 있다.
- 세션을 시작할 때 불러온 지침은 `"type": "attachment"`이고 `attachment.type`이 `"instructions"`인 항목 한 줄로 남는다. `attachment.files[]`에 파일마다 `path`, `type`(`Project` / `AutoMem`), `content`(불러온 원문)가 있다.
- **검증용 세션 2개** (지침이 들어간 것이 확실한 세션):

| 세션 | 시작 (KST) | 근거 | `files[].path` |
|---|---|---|---|
| `d96d079c` | 10-15 10:01 | `901d6e1`보다 앞 | `CLAUDE.md`, **`REVIEW_GUIDELINES.md`** |
| `4425775e` (이 조사 세션) | 10-16 20:26 | `edf46fb`보다 뒤, 세션 안에서 지침 원문 인용 확인 | `CLAUDE.md`, **`REVIEW_GUIDELINES.md`**, `MEMORY.md` |

- **판정 기준: instructions 항목의 `files[].path`에 `REVIEW_GUIDELINES.md`가 있는가.**
  - 본문 문자열("리뷰 지침 (Claude · 외부 에이전트 공통)") 검색은 기준으로 쓰지 않는다. 지휘자가 답에 인용한 문장, tool_result, API 요청 사본(`api-request-blob`)에도 걸린다. 이 조사 세션에서 5줄이 나왔는데 instructions 항목은 그중 1줄뿐이었다.
- 비공식 형식이다. Claude Code 버전이 바뀌면 위 검증용 세션처럼 "들어간 게 확실한 세션"으로 다시 확인한 뒤 쓴다.

#### 지휘자 쪽 결과

| 세션 | 시작 (KST) | 담긴 작업 | `files[].path` | CLAUDE.md 4번째 줄 |
|---|---|---|---|---|
| `320760ac` | 10-16 16:34 | 3장 (`3169f4a54e`, `d49d0cf935`, `dfa393514b` 결과가 tool_result로 있음) | `CLAUDE.md`, `MEMORY.md` | `@REVIEW\_GUIDELINES.md` |
| `9fe73ae0` | 10-16 16:34 | 위와 같음 | 위와 같음 | 위와 같음 |
| `0be7835d` | 10-16 17:31 | (3장 run 없음) | `CLAUDE.md`, `MEMORY.md` | `@REVIEW\_GUIDELINES.md` |
| `3883fd85` | 10-16 19:00 | 4장 (`00b061adf8`, `1133a77981`, `315c6d6108`) | `CLAUDE.md`, `MEMORY.md` | `@REVIEW\_GUIDELINES.md` |

#### 에이전트 쪽 결과

- calls.jsonl의 `Prompt`는 `StepCall.Prompt`(입력 + 첨부)뿐이다. 리뷰 지침이 붙는 시스템 프롬프트(`StepCall.Instructions`)는 기록하지 않는다 (`AgentRunner.cs:222`). 3장·4장 run 19줄의 Prompt 어디에도 지침 문구가 없다. 기록이 없는 구조이니 당연한 결과이고, 들어가지 않았다는 증거는 아니다.
- 서버 쪽 경로: `ModeRunner.cs:79`에서 `역할 프롬프트 + CommonRules + (UseGuidelines ? 지침 : "")`. 지침 파일은 `GuidelinesLoader`가 `GuidelinesPath` → 환경 변수 `REVIEW_GUIDELINES_PATH` → `Environment.CurrentDirectory/REVIEW_GUIDELINES.md` 순으로 찾고, 실행할 때마다 읽는다.
  - `appsettings.json`의 `GuidelinesPath`는 `""`, 환경 변수는 프로세스·사용자·시스템 모두 없음.
  - `F:\AI\ToolCreate\REVIEW_GUIDELINES.md`: 생성 10-15 09:39, 마지막 수정 10-15 12:20. 3장·4장 시점에 있었고 내용은 지금과 같다.
- 역할별 `UseGuidelines` (`modes.json`, 마지막 수정 10-16 16:54:35):

| 역할 | UseGuidelines | 해당 run |
|---|---|---|
| `Reviewer` (내장 역할, `ModeStore.ResolveRole`에서 `true` 고정) | true | 4장 `00b061adf8`, `1133a77981`, `315c6d6108` |
| `Cross_P1_A/B`, `Cross_P2_A/B`, `Cross_P3_A/B` | false | 3장 `cross_p1` · `cross_p2` · `cross_p3` |
| `Cross_P1_A_Guided/B_Guided` | true | 3장 `9ff8787171` |

#### 확정 · 추정 · 미확인

- **확정**
  - 3장 세션(`320760ac`, `9fe73ae0`)과 4장 세션(`3883fd85`)에서 지휘자 컨텍스트에 REVIEW_GUIDELINES.md가 들어가지 않았다. Unity 쪽 CLAUDE.md 복사본도 `@REVIEW\_GUIDELINES.md`로 깨져 있었다.
  - 이 조사 세션(`edf46fb` 이후)에는 들어갔다. 위 "실제 확인 (미완 — 다음 세션)" 두 항목도 확인됐다: 불러온 CLAUDE.md에 `## 계획서와 작업 로그` 절이 있고, `REVIEW_GUIDELINES.md`가 `files[]`에 있다.
  - calls.jsonl에는 시스템 프롬프트가 남지 않는다.
- **추정**
  - 4장 review 3회와 3장 `cross_p1_guided`에서는 에이전트 시스템 프롬프트에 지침이 붙었다. 근거는 코드 경로, `UseGuidelines=true`, 파일 존재, 경로 설정·환경 변수 없음이다. 단, 서버의 `CurrentDirectory`가 `F:\AI\ToolCreate`였다는 전제가 있다 (stdio MCP 서버는 세션 폴더에서 시작한다는 전제이고, 직접 확인하지 않았다).
  - 3장 `cross_p1`·`p2`·`p3`에는 지침이 붙지 않았다. 지금 `modes.json` 기준이다. 이 파일이 3장 도중(16:54:35, `d49d0cf935` 뒤·`8e2575f920` 앞)에 수정됐고 그 전 버전의 백업이 없어서, `cross_p1`·`cross_p2` 실행 때의 값은 확인할 수 없다.
- **미확인**
  - 에이전트가 실제로 받은 시스템 프롬프트 원문. 기록이 없어서 사후에 확인할 방법이 없다.

**별도 발견:** `project-files/REVIEW_GUIDELINES.md`(원본)와 Unity 쪽 파일이 다르다 (SHA-256 `78cf427f…` / `43efe35d…`). CR을 무시하면 Unity 쪽 끝에 `## Unity 공식 문서 참조` 절(12줄)이 더 있을 뿐이다. 에이전트가 받는 것은 Unity 쪽이다. 맞출지는 정하지 않았다.

#### 지휘자 판정 대조 (4장 review 3회)

**지휘자에게 해당하는 지침 항목.** 리뷰 대상이 .NET 서버 코드라 Unity 전용 항목(`Update` 할당, `GetComponent`, 직렬화, `UnityEngine.Object` null, Addressables 등), C++, 셰이더 항목은 빠진다. 남는 항목은 아래와 같다.

| # | 항목 | 출처 절 |
|---|---|---|
| G1 | 실패 경로: 예외, null, 빈 컬렉션, 0 나누기 | 공통 |
| G2 | 이름이 동작을 정확히 설명하는지 | 공통 |
| G3 | 테스트하기 어려운 구조 (정적 상태, 숨은 의존성) | 공통 |
| G4 | async 취소(`CancellationToken`) 처리 | Unity C#의 코루틴/async 항목 중 Unity와 무관한 부분 |
| G5 | 확실하지 않으면 문서로 확인하고 기억으로 추측하지 않는다 | 공식 문서 참조 (원문은 Unity 문서 대상. .NET 판단에는 정신만 적용) |

**대조 결과**

| run | 자체 검토(C) | 판정(R) | 지침과 어긋나는 곳 |
|---|---|---|---|
| `00b061adf8` (2-2 재시도) | C1~C5: 통계·기록 크기·범위·시간·실제 SDK 미확인 | R1·R2·R3 모두 채택 | **G1·G4 누락.** 에이전트가 찾은 R1(사용자 취소 시도 미기록, G4)과 R3(`TimeSpan` 변환 예외, G1)이 자체 검토에 없었다. C5가 타임아웃 예외 경로를 다뤘지만 "실제 SDK에서 그 예외가 오는가"였고, 취소·변환 실패 경로는 아니었다. G3은 지켰다 (대기 함수·기록 경로 주입). 판정은 지침과 어긋나지 않는다. R3은 직접 실행해 확인했다 (G5) |
| `1133a77981` (2-3 save_result) | C1~C5: 줄바꿈·전체 읽기·TOCTOU·남는 폴더·출력 증가 | R1·R3·R5 채택, R2·R4 기각 | **G1 일부 누락.** C4(쓰기 실패 시 남는 폴더)는 실패 경로를 봤다. 하지만 R3(경로 검사 중 IO·권한 예외가 `[저장 불가]` 대신 예외로 샘)과 R5(드라이브 루트 경계값)는 자체 검토에 없었다. 기각 2건은 지침과 무관한 설계 근거(링크 정책, README에 적힌 Concurrent 동작)다. **G5 관련 1건:** R1 판정 이유의 "macOS 기본(APFS)은 대소문자를 구분하지 않는다"는 확인 없이 기억으로 쓴 것이다. 판정 결과(채택)에는 영향이 없다 |
| `315c6d6108` (ExtraRoots) | C1~C3: `@` 폴더 가림·기록의 절대 경로·주의 표시 | R1 기각 | 없음. 실패 경로(G1)는 테스트로 다뤘다 (깨진 등록 항목, 참조한 깨진 루트, 루트 밖, 링크). R1 기각 근거(context 오류는 에이전트 호출 전에 지휘자에게만 반환)는 코드 경로를 근거로 했다 |

**정리**
- **확정:** 판정(채택·기각)이 지침 항목과 어긋난 곳은 없다. 어긋난 것은 자체 검토의 범위다. 3회 중 2회에서 G1(실패 경로 · 예외)과 G4(취소)에 해당하는 결함을 지휘자가 먼저 찾지 못했고, 에이전트가 찾았다.
- **추정하지 않음:** 지침이 컨텍스트에 있었다면 자체 검토가 이것들을 찾았을지는 알 수 없다. 지침이 없었던 것과 누락이 같이 일어났다는 사실만 남긴다.
- 3장은 이번 대조 범위에서 뺐다 (사용자가 따로 확인).

### 4장 마무리 비용

| 항목 | 비용 |
|---|---|
| ExtraRoots review (`315c6d6108`) | $0.0152 |

---

## 규칙서 — "계획서와 작업 로그" 절 추가

계획서: [plans/2026-10-16_규칙서-계획서와-작업로그.md](plans/2026-10-16_규칙서-계획서와-작업로그.md) (사용자 답 원문과 규칙 문구 변경 두 곳은 계획서의 `## 결정`)

이 작업 자체가 새 규칙의 첫 적용이다. 계획서 → 사용자 승인 → 결정 절 → 실행 → 이 결과 절 순서로 진행했다.

### 한 것

| 대상 | 내용 |
|---|---|
| `project-files/CLAUDE.md` (원본) | `## 외부 에이전트` 다음, `## Unity 테스트` 앞에 `## 계획서와 작업 로그` 절 (추가 22줄, 삭제 0줄) |
| Unity `CLAUDE.md` (복사본) | 원본을 그대로 복사 |
| `docs/plans/2026-10-16_규칙서-계획서와-작업로그.md` | 이 작업의 계획서 + `## 결정` |
| `docs/plans/2026-10-16_4장-마무리.md` | **첫 적용**: 대화로 제시한 4장 마무리 계획 원문 + 사후 파일화 표시 + `## 결정` (사용자 답 (가)·(나) 원문, 바뀐 계획 항목 8개) |
| 이 로그의 "4장 마무리" 절 | 첫 줄에 위 계획서 링크 |

### 계획과 달라진 것

- **규칙 문구 두 곳** (사용자 결정). 자세한 비교는 계획서 `## 결정`에 있다.
  - 1단계: "작업 대상 저장소의 `docs/plans/…`" + "여러 저장소에 걸치면 주로 고치는 저장소에 두고, 다른 저장소의 변경도 계획서에 적는다"
  - 3단계: "작업 로그(진행 중인 작업 묶음의 로그가 있으면 거기, 없으면 `docs/worklog.md`)". 계획서는 "장 작업 중이면 그 장의 로그"를 제안했지만, 사용자 결정으로 장 단위 표현을 템플릿에서 뺐다.
- **인용 펜스 규칙** (사용자 결정): 인용 원문에 ```` ``` ````가 있으면 바깥 펜스는 ```` ```` ````.
  - 규칙서 계획서의 결정 절은 사용자 답 안에 ```` ``` ````·```` ```` ````가 있어 ```` ```` ```` 펜스로 인용했다. 안쪽의 백틱은 줄 첫머리가 아니어서 펜스를 닫지 않는다.
  - 4장 마무리 결정 절의 (가)·(나)에는 백틱이 없어 ```` ``` ```` 펜스를 썼다.
- 나머지(위치, 기존 `ch4_plan.md`는 그대로, 4장 마무리 계획서 형식)는 계획대로 했다.

### 확인 (코드 변경 없음, 자동 테스트 없음)

| 항목 | 결과 |
|---|---|
| 원본·복사본 SHA-256 | 둘 다 `2841f7b046259d03515d4af47a806c1ce70f399f760730ad385dd8bbc4e1bfa6` — **일치** |
| 줄바꿈 | 두 파일 모두 CR 0개 (LF) |
| 제목 순서 | `## 외부 에이전트` → `## 계획서와 작업 로그` → `## Unity 테스트` → `## Unity 공식 문서 참조` |
| 기존 내용 보존 | `git diff`: 추가 22줄, 삭제 0줄 |
| 문구 = 사용자가 준 문구 | 준 문구를 파일로 두고 넣은 절과 diff → 차이는 결정된 두 곳(1단계 2줄, 3단계 1줄)뿐 |
| 인용 = 원문 (바이트) | 원문을 파일로 두고 계획서의 `text` 블록과 비교. ① 규칙서 계획서 미리보기의 (가)·(나) 일치 ② 규칙서 계획서 결정 인용 일치 ③ 4장 마무리 결정 인용 일치 |

**확정과 추정**
- 확정: 위 표의 해시 일치, 줄바꿈, diff 결과, 인용 바이트 일치.
- 추정: (가)·(나) 원문은 대화 기록의 붙여넣기 텍스트에서 옮겼다. 사용자가 미리보기 단계에서 원문임을 확인했다.
- 미확인: 4장 마무리 계획 본문은 내가 대화로 보낸 메시지를 옮긴 것이다. 내 출력을 다시 옮겨 적은 것이라 바이트 대조 기준이 없다 (calls.jsonl 같은 서버 기록이 없음).

### 실제 확인 (미완 — 다음 세션)

규칙서는 세션을 시작할 때 읽힌다. 다음 세션에서 두 가지를 확인해야 한다.
1. 불러온 지침에 `## 계획서와 작업 로그` 절이 보이는지
2. 4장 마무리에서 고친 `@REVIEW_GUIDELINES.md`가 실제로 불러와지는지

### 리뷰

없음. 규칙서·문서 변경이라 외부 에이전트 리뷰 대상(`review` 모드를 부르는 작업)이 아니다.

---

## 규칙서 동기화 — REVIEW_GUIDELINES 통일, 동기화 스크립트, LF 고정

계획서: [plans/2026-10-16_규칙서-동기화.md](plans/2026-10-16_규칙서-동기화.md) (사용자 답 원문과 바뀐 항목은 계획서의 `## 결정`)

### 한 것

| 대상 | 내용 |
|---|---|
| `.gitattributes` | `project-files/CLAUDE.md` 한 줄을 `project-files/** text eol=lf`로 넓히고, `docs/plans/** text eol=lf` 추가. 모든 대상 파일이 이미 `i/lf w/lf`라 바이트 변화 없음 |
| `tools/Sync-ProjectFiles.ps1` (새 파일) | 비교(기본) / `-Apply`(다름이 있으면 멈춤) / `-Apply -Force`(백업 후 덮기). 이스케이프 잔재 경고. 종료 코드 0·1·2. UTF-8 BOM (5.1이 한글 리터럴을 읽도록) |
| `project-files/REVIEW_GUIDELINES.md` | Unity 쪽에만 있던 `## Unity 공식 문서 참조` 절을 합침 (아래 "절 합치기") |
| `project-files/CLAUDE.md` | `## Unity 공식 문서 참조` 절 삭제 (빈 줄 포함 9줄). `@REVIEW_GUIDELINES.md`로 불러오므로 지휘자에게는 그대로 들어간다 |
| `project-files/.claude/commands/cross-review.md` | 1번 항목을 "diff가 있으면 … / diff가 없으면(기존 코드 점검) …" 한 항목으로 (사용자 결정) |
| Unity 프로젝트 | 스크립트 `-Apply -Force`로 `CLAUDE.md`, `REVIEW_GUIDELINES.md`, `cross-review.md` 덮음. 백업 `F:\AI\ToolCreate\review_tmp\sync-backup\20261016-204833\` (3개, 덮기 전 해시 `2841f7b0…` / `43efe35d…` / `2c01dd35…`와 일치) |
| README | 구조도에 `tools/`, 5절 원본 방향을 `project-files/` 전체로, 스크립트 사용법·종료 코드·멈춤 규칙·이스케이프 경고 |
| 실제 설정 `Publish_Output/appsettings.json` | `Context.ExtraRoots.orch` 다시 등록 (사용자 선택, review용). 4장 마무리 실제 확인 때 비워 두었던 것. **그대로 두었다** |

### 절 합치기

1. **REVIEW_GUIDELINES:** Unity 쪽 1~34줄이 원본과 바이트 동일함을 `cmp`로 확인 → Unity 쪽 35줄부터를 `tail`로 원본 끝에 붙임 → 두 파일 SHA-256 일치(`43efe35d…`).
2. **CLAUDE.md 쪽 절과 바이트 비교 (사용자 조건):** 다름. 빈 줄을 빼면 같다. 차이는 번호 항목 사이의 빈 줄 3개뿐이다.
   - REVIEW_GUIDELINES 쪽(빈 줄 있음)은 `901d6e1` 때 생긴 모양 그대로다. CLAUDE.md 쪽(붙은 목록)은 `edf46fb`에서 정리한 모양이다.
   - **CLAUDE.md 쪽 모양으로 합쳤다.** REVIEW_GUIDELINES의 절을 CLAUDE.md 절의 바이트로 바꾸고, 바뀐 절이 CLAUDE.md 절과 바이트 동일함을 `cmp`로 확인한 뒤 CLAUDE.md에서 삭제했다. 글자 내용은 바뀌지 않았다.
3. **cross-review.md:** 원본 문구와 Unity 문구가 각각 원문 그대로 들어갔는지 문자열로 확인. Unity 문구의 `,  `(공백 두 칸)은 한 칸으로 고쳤다. Unity 쪽의 나머지 차이(`\---`, `\[`, `\_`, `*` 목록, 빈 줄)는 들이지 않았다.

### `901d6e1`에서 바뀐 범위 (`project-files/CLAUDE.md`, 70 → 94줄)

| 종류 | 범위 |
|---|---|
| **이스케이프** (`\_`, `\[`, 코드 스팬 안은 `\\_`처럼 세 겹) | **일부 줄.** 바꾸기 전 `_` 또는 `[`가 있던 6줄은 전부 이스케이프됐다. 그 커밋에서 새로 들어간 `## Unity 공식 문서 참조` 절의 번호 4줄은 `1\.` 형태였다. 백슬래시가 있는 줄: 1 → 10 |
| 목록 기호 | **파일 전체.** `- ` 17줄이 전부 `* `로 |
| 빈 줄 | **파일 전체.** 15 → 33줄 (목록 앞뒤에 추가) |
| 들여쓰기 | 이어지는 줄 2개가 들여쓰기를 잃음 |
| 그대로 | 기존 번호 항목 12줄 중 11줄(바뀐 1줄은 `_`·`[` 포함 줄), 그 밖의 일반 줄 21줄 중 19줄 |

- 같은 커밋에서 `.claude/settings.json`에는 `"WebFetch(domain:docs.unity.com)"` 한 줄만 추가됐다 (이스케이프 없음).
- Unity 쪽 `cross-review.md`(10-15 10:17 수정)에도 같은 종류의 흔적이 있었다: `\---`, `\[`, `\_`, `*` 목록, 빈 줄.
- **원인은 미확인.** 노션을 거치지는 않았다 (사용자 확인).

### 계획과 달라진 것

- **`-Apply`의 멈춤 범위:** 계획서는 "`Unity에 없음`인 파일은 바로 복사"라고 했지만, 구현은 `다름`이 하나라도 있으면 **아무것도 복사하지 않는다.** 일부만 복사된 상태를 만들지 않기 위해서다. `다름`이 없으면 `Unity에 없음`은 바로 복사한다.
- **review 결과로 추가:** 파일 자리에 폴더가 있으면 `Unity에 폴더`로 표시하고, `-Apply`는 아무것도 복사하지 않고 종료 코드 2로 끝난다. 숨김 `.md`도 대상에 넣었다. Unity에만 있는 명령 파일도 이스케이프를 검사한다.
- **ExtraRoots 재등록:** 계획에 없었다. review가 `@orch`를 읽지 못해(`[첨부 코드 오류] … 파일이 없습니다`, 실행 횟수에는 들어가지 않음) 사용자에게 물었고, 사용자가 "ExtraRoots 등록"을 골랐다.

### 테스트 (scratchpad 임시 폴더, 실제 Unity 프로젝트와 별개)

| 경우 | 결과 |
|---|---|
| 모두 같음 | 0 |
| Unity에 없음 | 비교 1 / `-Apply` 복사 후 0, 바이트 동일 |
| 다름 + `-Apply` | 1, 파일 해시 불변, 같이 있던 `Unity에 없음` 파일도 복사 안 됨 |
| 다름 + `-Apply -Force` | 0, 백업 해시 = 원래 Unity 파일, 덮은 파일 = 원본, CRLF·LF 섞인 한글 파일 바이트 동일 |
| `settings.json` 다름 | `확인만-다름`, `-Force`에도 불변, 종료 코드 0 |
| `.mcp.json` 다름 | 출력에 없음, 0 |
| Unity에만 있는 명령 | `Unity에만 있음`, 지우지 않음 |
| 없는 경로 / `-Force` 단독 / 원본 `CLAUDE.md` 없음 | 2 |
| 이스케이프 5종 | 원본·Unity 각 5건 경고, Windows 경로 줄·정상 마크다운 줄은 경고 없음, 종료 코드 0 |
| (review R1) 파일 자리에 폴더 | 비교 1 `Unity에 폴더` / `-Apply` 2, 폴더 안에 아무것도 생기지 않음 |
| (review R2) 숨김 `.md` | 대상에 포함 |
| (review R4) Unity에만 있는 명령의 `\---` | 경고 |

review 반영 후 위 회귀 경우(1~7번 줄)를 다시 돌려 모두 같은 결과였다.

**테스트 중 고친 것**
- `Short([string]$hash)`: `[string]` 매개변수가 `$null`을 `''`로 받아 null 검사를 지나 `Substring` 예외가 났다 → `IsNullOrEmpty`.
- 처리되지 않은 예외가 종료 코드 1("다름")로 끝났다 → `trap`으로 2.
- 빈 결과를 `+=`로 더하면 `$null` 원소가 생긴다 → `@(...)`로 감쌈.
- (스크립트 문제 아님) 출력은 cp949다. Git Bash로 받은 출력이 깨져 보인 것은 내 디코딩 문제였다 (`bf f8 ba bb` = "원본").

### 실제 확인

| 항목 | 결과 |
|---|---|
| 1. 실제 Unity에 비교 모드 (작업 전) | 종료 코드 1. `REVIEW_GUIDELINES.md`·`cross-review.md` `다름`, Unity 쪽 `cross-review.md` 이스케이프 경고 4건 (1·4·12·13줄) — **실제 문제를 잡음** |
| 같은 상태에서 `-Apply` | 멈춤(1), Unity 두 파일 해시 불변 |
| 2. 원본 정리 후 `-Apply -Force` → 다시 비교 | 0 → 0, 경고 0 |
| 3. `git ls-files --eol` / `check-attr` | `project-files/**`·`docs/plans/**` 모두 `attr/text eol=lf`, `i/lf w/lf` |
| cross-review 설명 | 복사 직후 이 세션의 스킬 목록이 `cross-review: \---`에서 `cross-review: Claude와 외부 에이전트가 독립적으로 리뷰한 뒤 결과를 종합한다`로 바뀌었다 — **frontmatter가 깨져 있었던 것이 원인이었음을 확정** |
| **4. 다음 세션 (필수, 미완)** | 세션 기록 instructions 항목(`files[].path`)에 `REVIEW_GUIDELINES.md`가 있고, 그 `content`에 `## Unity 공식 문서 참조`가 있으며, `CLAUDE.md` `content`에는 그 절이 없는지 |

### review (gpt_main_sol, run `83c55b812e`)

- 사용량: 입력 4,948 / 출력 1,126, 33.5초, 약 $0.021 (단가표 기준 계산, 캐시 미반영)
- context `@orch/tools/Sync-ProjectFiles.ps1:1-208`

**먼저 완성한 Claude의 자체 검토**

| ID | 내용 | 처리 |
|---|---|---|
| C1 | Unity에만 있는 명령·`settings.json`은 종료 코드에 영향 없음 | 의도 |
| C2 | 이스케이프 검사가 코드 블록 안의 정상 사용도 경고 | 경고뿐이라 유지 |
| C3 | `다름`이 있으면 `Unity에 없음`도 복사 안 함 (계획 문구보다 엄격) | 위 "계획과 달라진 것" |
| C4 | 복사 도중 실패하면 일부만 복사된 채 2로 끝남 | 유지. 다시 비교하면 상태가 드러난다 |
| C5 | 원본·Unity 같은 폴더 검사가 문자열 비교 (대소문자 다른 같은 경로 통과) | 유지. 그 경우 모두 `같음`이라 해가 없다 |
| C6 | Unity 쪽 링크 검사 없음 | 유지 (단일 사용자 PC 전제) |

**에이전트 지적과 판정**

| # | 지적 | 판정 | 이유 / 조치 |
|---|---|---|---|
| R1 (minor) | 파일 자리에 폴더가 있으면 `Unity에 없음`으로 분류, `-Apply`가 폴더 안에 복사하고 1로 끝남 | **채택** | `Get-Hash`가 폴더를 "없음"으로 돌려준다. `Unity에 폴더` 상태 + `-Apply` 시작 전에 2로 종료 |
| R2 (minor) | `Get-ChildItem`에 `-Force`가 없어 숨김 `.md` 누락 | **채택** | 두 열거에 `-Force` |
| R3 (minor) | `\_`·`\[`에도 줄 시작 조건을 붙여야 함 | **기각** | 실제 잔재는 줄 중간에서 나왔다 (`` `run\_mode` ``, `argument-hint: \[panel]`, `2. REVIEW\_…`). 붙이면 실제 Unity 파일에서 잡은 4건 중 3건을 놓친다. 사용자 지시의 "줄 맨 앞"은 `\---`·`1\.`에 해당하는 것으로 읽었다. 오탐은 경고뿐이다 |
| R4 (minor) | Unity에만 있는 명령 파일은 이스케이프 검사 안 함 | **채택** | Claude가 불러오는 파일이다. 경고만 하고 종료 코드·복사에는 영향 없음 |

자체 검토와 겹친 지적은 없었다 (에이전트 1개 모드라 공통 지적 없음).

### 확정 · 추정 · 미확인

- **확정:** 원본·Unity 동기화 대상 해시 일치, `.gitattributes` 적용, 백업 해시 일치, cross-review frontmatter 복구(스킬 목록 변화), `901d6e1`의 변경 범위.
- **추정:** 없음.
- **미확인:** 이스케이프 원인. 다음 세션의 지침 불러오기 (실제 확인 4).
