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
