# 4장 계획 단계 기록 — 테스트 후 다시 수정

> 작성일: 2026-10-16
> 상태: **계획 제출, 승인 대기** (코드 변경 없음)
> 이 문서는 지시서를 받은 뒤 승인 전까지의 대화(조사 → 계획 → 질문)를 정리한 것이다.
> 구현 이후의 기록은 `docs/ch4_worklog.md`에 이어서 쓴다.

---

## 1. 출발점: 3장에서 드러난 도구 문제

교차 검토 재테스트(3장)에서 드러난 문제와 그 원인은 아래와 같다.

| # | 3장에서 생긴 일 | 원인 |
|---|---|---|
| A | GPT 응답이 100초에 끊겼고, 400초를 기다린 뒤 실패 | `OpenAIClientOptions` 기본값: 네트워크 타임아웃 100초, SDK 자동 재시도 3회(총 4번 시도). 재시도가 calls.jsonl에 남지 않음 |
| B | 지휘자가 응답을 파일로 옮겨 적어야 했고, 세션이 다른 폴더에서 열려 저장 위치가 이탈함 | 결과 저장 도구가 없음. `run_mode` 결과에 runId와 토큰이 없어 서버 로그를 직접 뒤져야 함 |
| C | GPT 응답이 코드 블록 뒤부터 일본어로 바뀌었는데, 사람이 대시보드를 보고서야 알았음 | 응답 품질 이상을 감지하는 장치 없음 (빈 응답 검사만 있음) |
| D | `run_mode` 결과를 지휘자가 받을 때 `<`가 `&lt;`로 바뀜 | 미확인 |

이 문제를 고치기 위해 받은 작업 항목은 다섯 개다.
- 2-1 에이전트 단위 타임아웃
- 2-2 서버 직접 재시도와 기록
- 2-3 결과 머리말과 `save_result`
- 2-4 응답 검사기
- 2-5 `&lt;` 원인 조사

지켜야 할 원칙은 2장에서 정한 네 가지다.
- 설정으로 바꿀 수 있는 것은 설정으로 둔다. 새 설정을 비우면 기존 동작과 같아야 한다.
- 실패는 예외가 아니라 `[`로 시작하는 문자열로 돌려준다.
- 부가 기능이 실패해도 주 기능은 막지 않는다.
- 도구 수는 최소로 유지한다.

진행 방식으로는 **"먼저 계획만 보여주고 승인을 받은 뒤 코드를 쓴다"**가 지정됐다.

---

## 2. 계획 전에 확인한 사실

계획을 기억이나 추측으로 세우지 않으려고, 코드와 실제 기록을 먼저 확인했다.

### 2-1. 코드 위치와 현재 상태

- Unity 프로젝트(`F:\AI\ToolCreate`)의 `.mcp.json`이 `F:/AI/ToolWithCS/Orchestration/Publish_Output/HB.Orchestration.Mcp.dll`을 실행한다.
  - 즉 실제 서버는 publish 폴더의 DLL이다.
  - 실제 설정은 `Publish_Output/appsettings.json`과 `modes.json`이다.
- 커밋되지 않은 변경은 두 종류다.
  - 3장 임시 조치: `ProviderOptions.TimeoutSeconds`와 `NetworkTimeout` 적용 (2개 파일)
  - 사용자가 직접 바꾼 설정: `src/…/appsettings.json`의 모델 이름 변경, `src/…/modes.json`의 cross 모드 추가
- `cross_p3` 모드는 `src` 쪽 modes.json에 없고 `Publish_Output/modes.json`에만 있다.
- 사용 중인 버전은 .NET SDK 10.0.300, OpenAI 2.14.0, Microsoft.Agents.AI 1.25.0이다. 테스트 프로젝트는 아직 없다.

### 2-2. calls.jsonl 실측

cross 계열 기록마다 **코드 블록을 뺀 본문**의 가나 비율을 계산했다.

| 시각 | runId | 모드 | 역할 | 에이전트 | 성공 | 가나 수 | 비율 |
|---|---|---|---|---|---|---|---|
| 16:37 | 3169f4a54e | cross_p1 | Cross_P1_B | gemini_high_argon | O | 0 | 0.0% |
| 16:37 | 3169f4a54e | cross_p1 | **Cross_P1_A** | gpt_main_sol | O | **312** | **9.1%** |
| 16:43 | d49d0cf935 | cross_p2 | Cross_P2_B | gemini_high_argon | O | 0 | 0.0% |
| 16:43 | d49d0cf935 | cross_p2 | Cross_P2_A | gpt_main_sol | O | 0 | 0.0% |
| 16:55 | 8e2575f920 | cross_p3 | Cross_P3_B | gemini_high_argon | O | 0 | 0.0% |
| 16:55 | 8e2575f920 | cross_p3 | Cross_P3_A | gpt_main_sol | **X** | – | – |
| 17:08 | dfa393514b | cross_p3 | Cross_P3_B | gemini_high_argon | O | 0 | 0.0% |
| 17:08 | dfa393514b | cross_p3 | Cross_P3_A | gpt_main_sol | O | 0 | 0.0% |
| 17:32 | 9ff8787171 | cross_p1_guided | Cross_P1_B_Guided | gemini_high_argon | O | 0 | 0.0% |
| 17:32 | 9ff8787171 | cross_p1_guided | Cross_P1_A_Guided | gpt_main_sol | O | 0 | 0.0% |

이 표에서 확인한 것은 세 가지다.
- 언어가 이탈한 응답은 9.1%, 정상 응답은 모두 0%다. **지시서의 기본 기준 2%로 둘이 확실히 갈린다.**
- 16:55 실패 기록의 원문은 `AggregateException: Retry failed after 4 tries. (The operation was cancelled…)`이다. **SDK가 4번 시도했다는 직접 증거**이고, 문제 A의 원인 분석과 일치한다.
- 테스트 데이터로는 `Cross_P1_A`(run `3169f4a54e`)를 이탈 사례로, `Cross_P2_A`(run `d49d0cf935`)를 정상 사례로 쓴다.

### 2-3. 코드를 읽다가 찾은 위험 (지시서에 없던 것)

`AgentRunner.RunStepAsync`는 아래처럼 취소 예외를 잡지 않고 그대로 던진다.

```csharp
catch (Exception ex) when (ex is not OperationCanceledException)
```

지금은 문제가 드러나지 않는다. SDK 재시도가 타임아웃을 `AggregateException`으로 감싸서 올려 주기 때문이다.

그런데 2-2에서 SDK 재시도를 끄면 상황이 바뀐다.
- 네트워크 타임아웃이 `TaskCanceledException`(`OperationCanceledException`의 하위 타입)으로 그대로 올라온다.
- 이 예외는 위 catch에 걸리지 않으므로 **`run_mode` 밖으로 빠져나간다.**
- "실패는 예외가 아니라 `[` 문자열"이라는 원칙이 깨진다.

따라서 재시도 분류에서 **"사용자 취소(`ct.IsCancellationRequested`)"와 "타임아웃"을 반드시 구분해야 한다.** 이것이 2-2의 핵심 위험이다.

### 2-4. 진행상의 제약

- **DLL 잠금:** 현재 세션의 MCP 서버가 `Publish_Output`의 DLL을 잡고 있다. publish하려면 서버를 내려야 하므로, 사용자에게 `/mcp` 재연결을 요청해야 한다.
- **review 실행 한도:** `review` 모드는 세션당 3회다.
  - 코드 검토(2-2, 2-3) 2회와 2-5 조사 1회로 이 세션의 한도를 다 쓴다.
  - 실제 실행 검증은 재연결한 새 서버에서 하므로, 횟수가 다시 시작되는지 확인이 필요하다.

---

## 3. 항목별 계획

### 2-1. 에이전트 단위 타임아웃

| 파일 | 변경 |
|---|---|
| `Agents/OrchestrationOptions.cs` | `AgentOptions.TimeoutSeconds`(int?) 추가. 임시로 넣은 `ProviderOptions.TimeoutSeconds`는 정식으로 쓴다 |
| `Agents/AgentSettings.cs` (새 파일) | `ResolveTimeout`, `ResolveMaxAttempts` 정적 함수. 우선순위 로직을 한곳에 모아 테스트 대상으로 쓴다 |
| `Providers/IChatClientProvider.cs`, `OpenAICompatibleProvider.cs` | `Create(providerName, ProviderOptions, AgentOptions)`로 바꾸고, 해석된 값을 `NetworkTimeout`에 넣는다 |
| `AgentCatalog.cs` | 호출부 수정 |
| 대시보드 `ModesConfigService.cs`, `Modes.cshtml(.cs)` | 에이전트 표에 "타임아웃(초)" 칸 추가. 비우면 키를 지운다 (MaxOutputTokens와 같은 방식) |

- **우선순위:** 에이전트 값 → 제공사 값 → SDK 기본값(100초). 0 이하는 무시한다.
- **이유:** 같은 제공사 안에서도 빠른 모델(luna)과 긴 응답 모델(sol)의 필요 시간이 다르다.
- **위험:** 인터페이스 시그니처가 바뀐다. 다만 구현체가 하나뿐이라 영향은 서버 안에서 끝난다.

### 2-2. 서버가 직접 재시도하고 기록 — review 대상

| 파일 | 변경 |
|---|---|
| `OpenAICompatibleProvider.cs` | `RetryPolicy = new ClientRetryPolicy(maxRetries: 0)` |
| `OrchestrationOptions.cs` | `AgentOptions.MaxAttempts`, `ProviderOptions.MaxAttempts`. 둘 다 비면 2, 범위는 1~5 |
| `Agents/RetryClassifier.cs` (새 파일) | `Classify(Exception, CancellationToken) → (ErrorKind?, Retryable, RetryAfter?)` |
| `AgentRunner.cs` | 시도 루프, 시도별 기록, 대기, 결과 집계 |
| `Shared/CallRecord.cs` | `Attempt`(int?, 예전 기록은 null을 1로 간주), `ErrorKind`(string?) |
| 대시보드 `Index`, `Call` | 같은 RunId+Step에 시도가 2개 이상이면 "재시도 n" 배지. 상세에 "시도 n/N"과 ErrorKind 표시 |

**분류 규칙**

| 상황 | ErrorKind | 재시도 |
|---|---|---|
| 취소 예외이면서 `ct.IsCancellationRequested` (사용자 취소) | — | 안 함. 그대로 던져 기존 동작 유지 |
| 취소 예외이지만 사용자 취소가 아님 (네트워크 타임아웃) | Timeout | 함 |
| `ClientResultException` 429 | RateLimit | 함 |
| `ClientResultException` 5xx | Server | 함 |
| 그 외 4xx (400·401·402·404…) | Client | 안 함 |
| `HttpRequestException`, `IOException`, Status 0 | Network | 함 |
| 그 외 예외 | (비움) | 안 함. 기존 `[실패]`와 같음 |

**세부 결정**
- **대기:** `Retry-After`(초 또는 HTTP 날짜)가 있으면 따르고, 없으면 2초 → 5초.
- **Retry-After 상한 30초 (제안):** 서버가 몇 분짜리 값을 보내면 run_mode가 그만큼 멈춘다. 30초를 넘으면 기다리지 않고 실패로 돌려준다. 대기 중 사용자가 취소하면 즉시 중단한다.
- **빈 응답은 재시도하지 않는다:** 원인이 출력 상한이라 다시 해도 같은 결과가 나온다.
- **결과 문구:**
  - 성공: 단계 아래에 `(재시도 1회 후 성공 · 1회차 Timeout)`
  - 최종 실패: `[실패] Timeout — 2회 시도 모두 실패. appsettings의 '…' TimeoutSeconds 또는 MaxAttempts를 조정하세요.`
- **테스트 가능성:** `AgentRunner`가 대기 함수와 기록 경로를 주입받게 한다. 테스트가 실제로 2초씩 기다리지 않기 위해서다.
- **알려진 영향:**
  - 동시 실행 모드에서는 한 단계가 재시도하면 전체 시간이 그만큼 늘어난다. 의도된 동작이다.
  - 재시도한 만큼 입력 토큰을 다시 쓴다. 실패한 시도는 보통 토큰이 0이다.

### 2-3. 결과 머리말과 `save_result` — review 대상

**(1) 머리말** (`ModeRunner.cs`. `StepResult`에 토큰·시간·시도 수·경고를 추가)

```
runId: dfa393514b
### 1. Cross_P3_A · gpt_main_sol (입력 15,551 / 출력 7,502, 144.0초)
```

- **토큰:** 성공한 시도의 값을 쓴다.
- **시간:** 재시도와 대기를 포함한 단계 전체 시간을 쓴다. 지휘자가 실제로 기다린 시간이 더 정직한 값이라고 봤다.
- **`Return: Last` 모드에도** runId 줄과 마지막 단계 제목 줄을 붙인다. 붙이지 않으면 save_result에 넘길 runId를 알 수 없다. **기존 출력 형태가 바뀌는 지점**이다.
- 실행 전 거절 메시지(`[…]`)는 그대로 둔다. 아직 runId가 없는 시점이기 때문이다.

**(2) `save_result`**

| 파일 | 변경 |
|---|---|
| `Agents/SafePath.cs` (새 파일) | `ContextLoader.ResolveSafePath`를 읽기·쓰기 공용으로 분리. 쓰기 모드에서는 파일이 없어도 되고, 이미 있는 상위 폴더까지 링크를 검사한다 |
| `Agents/ResultSaver.cs` (새 파일) | 기록 찾기, 코드 블록 추출, 저장. 테스트하려고 Agents에 둔다 |
| `Mcp/ModeTools.cs` | 도구 등록 (도구 수 3 → 4) |

**동작**
- **기록 선택:** RunId와 Step이 일치하고 `Success`인 기록을 쓴다. 시도가 여럿이면 성공한 시도를 쓴다.
- **경로 검사:** 루트는 해당 기록의 `ProjectDir`다.
  - 절대 경로, 루트 밖, 링크 경유는 거절한다.
  - **DenyPatterns도 적용한다.** 쓰기라서 `.env` 같은 파일을 덮어쓰지 않게 하기 위해서다.
- **파일 처리:** 없는 하위 폴더는 만든다. 파일이 이미 있고 `overwrite=false`이면 거절한다.
- **codeBlock:** ```` ``` ```` 또는 `~~~` 펜스를 센다. 펜스 줄은 빼고 내용만 저장한다.
- **저장 형식:** UTF-8(BOM 없음), 줄바꿈은 원문 그대로 둔다.
- **반환:** 상대 경로, 바이트 수, SHA-256.
- **도구 설명 초안:** "run_mode 결과의 한 단계 응답 원문을 파일로 저장한다. 응답을 직접 다시 쓰지 말고 이걸 쓴다."
- **도구를 늘리면서 얻는 것:**
  - 지휘자가 응답을 다시 출력하지 않아 토큰을 아낀다.
  - 옮겨 적다 생기는 오류가 없어진다.
  - 저장 위치가 고정된다. 3장에서는 세션이 다른 폴더에서 열려 위치가 이탈했다.
- **위험:** 기록 파일을 전부 읽는다 (현재 1.1MB, `CheckBudget`과 같은 방식). 파일이 커지면 끝에서부터 읽는 방식으로 개선할 수 있다.

**(3)** `project-files/CLAUDE.md`와 Unity 프로젝트의 `CLAUDE.md`에 규칙을 추가한다: "에이전트 응답을 파일로 남길 때는 `save_result`를 쓴다."

### 2-4. 응답 검사기

| 파일 | 변경 |
|---|---|
| `Agents/ResponseChecker.cs` (새 파일) | `Check(text, finishReason, outTokens, agentOptions, checkOptions) → (Failure?, Warnings)` |
| `OrchestrationOptions.cs` | `ResponseChecksOptions { ExpectedLanguage = "ko", ForeignScriptRatio = 0.02 }` |
| `AgentRunner.cs` | 빈 응답 처리를 검사기로 옮기고, 검사기 예외를 격리한다 |
| `CallRecord.cs` | `Warnings` (기본값은 빈 목록) |
| 대시보드 | 목록과 상세 화면에 경고 배지 |

**잘림 검사**
- MAF 응답의 `RawRepresentation`에서 `ChatResponse.FinishReason == Length`인지 본다.
- 이 값을 꺼낼 수 없으면 추정하지 않고 이 검사는 건너뛴다.
- 꺼낼 수 있는지는 구현할 때 확인해서 작업 로그에 남긴다.

**언어 검사**
- 펜스 코드 블록과 인라인 코드를 뺀 본문에서, 가나 수 ÷ 문자(`char.IsLetter`) 수를 구한다.
- **가나가 5자 미만이면 경고하지 않는다.** 짧은 응답에서 고유명사 하나만으로 오탐이 나는 것을 막기 위해서다.
- 언어 값은 지금 `ko`만 지원한다. 다른 값이 오면 로그만 남기고 언어 검사를 끈다.

**알려진 한계:** 중국어처럼 한자로 이탈하는 경우는 잡지 않는다. 지시서 범위인 가나만 본다.

### 2-5. `&lt;` 원인 조사

- **설정:** `Orchestration:Debug:DumpToolResults` (기본값 false)
- **덤프:** 설정을 켜면 `ModeTools.RunMode`가 반환 직전 문자열을 `%LOCALAPPDATA%/HB_Orchestration/dumps/<시각>_<mode>.txt`에 쓴다. 덤프에 실패하면 로그만 남긴다.
- **절차:** `review`를 1회 실행하고 덤프와 지휘자가 받은 결과를 비교한다.
  - 덤프에는 `<`인데 지휘자가 받은 쪽만 `&lt;`이면 서버 밖 문제다. 기록만 하고 고치지 않는다.
  - 덤프부터 `&lt;`이면 서버 코드 문제다.

---

## 4. 테스트와 실제 실행 검증 계획

**자동 테스트**
- **프로젝트:** `tests/HB.Orchestration.Tests` (xUnit, Agents 참조). slnx에 추가한다.
- **응답 검사기 데이터:** calls.jsonl의 실제 응답을 `TestData/*.md`로 옮긴다.
  - 이탈 사례: `Cross_P1_A` (run 3169f4a54e)
  - 정상 사례: `Cross_P2_A` (run d49d0cf935)
  - 코드 블록 제외 확인: 일본어 주석이 든 코드 블록 + 한국어 본문
- **재시도 분류:** 가짜 `PipelineResponse`로 `ClientResultException(402/401/429/500)`을 만들고, 타임아웃과 사용자 취소를 함께 넣는다. 기대 결과는 타임아웃·429·500만 재시도하는 것이다.
- **재시도 루프:** 가짜 `IChatClient`로 "타임아웃 → 성공"을 재현해 세 가지를 확인한다.
  - 기록 2줄 (Attempt 1·2, 같은 RunId·Step)
  - MaxAttempts 준수
  - 402는 1회로 끝남
- **save_result 경로:** `../`와 절대 경로는 거절되고, 루트 안 정상 경로는 성공해야 한다.
- **타임아웃 우선순위:** 에이전트 값만 있을 때, 제공사 값만 있을 때, 둘 다 없을 때를 각각 검사한다.

**실제 실행 검증** (publish와 MCP 재연결 이후)
- **긴 응답:** `cross_p3` 1회. 타임아웃 없이 완료되어야 한다.
- **재시도 기록:** 한 에이전트의 TimeoutSeconds를 5초로 낮추고 `review` 1회.
  - 시도마다 기록이 남아야 한다.
  - 시도 횟수가 MaxAttempts를 넘지 않아야 한다.
  - 결과에 재시도 안내가 나와야 한다.
  - 끝나면 설정을 원래대로 되돌린다.
- **save_result:** 위 결과를 저장하고, 저장된 파일의 SHA-256이 calls.jsonl의 Response와 같은지 확인한다.
- **머리말:** 같은 실행 결과에 runId와 단계별 토큰·시간이 표시되는지 확인한다.

---

## 5. 진행 순서

2-1 → 2-2(review) → 2-4 → 2-3(review) → 2-5 → 실제 실행 검증 → README·CLAUDE.md

**지시서 순서와 다른 점:** 2-4를 2-3보다 먼저 한다.
- **이유:** 결과 머리말에는 단계별 경고 줄이 들어간다. 검사기가 먼저 있으면 머리말 형식을 한 번에 확정할 수 있다.

**커밋:** 항목마다 커밋한다. 제목은 `HB_Orchestration: …` 형식의 한글로 쓰고, 본문은 `[!]/[-]/[+]` 형식으로 쓴다.

---

## 6. 승인 전에 사용자에게 물은 것 (답 대기)

1. **`ExpectedLanguage` 기본값의 해석**
   - 원칙("비우면 기존 동작")과 지시서("기본 ko")가 충돌한다.
   - 제안하는 해석:
     - 키가 **없으면** `ko`(검사 켬). 경고는 실패가 아니므로 기존 실행 결과는 바뀌지 않는다.
     - `""`로 **명시하면** 검사를 끈다.
2. **사용자가 직접 바꾼 설정(appsettings·modes.json)의 처리**
   - 기본안: 2-1 커밋에 넣지 않고 그대로 둔다. 임시 조치 코드 2개 파일만 커밋에 포함한다.
   - 대안: 별도 커밋으로 먼저 정리한다.
3. **상한값:** Retry-After 상한 30초, MaxAttempts 상한 5로 정해도 되는가.

---

## 7. 4장 보고서에 쓸 만한 포인트

- **실측으로 기준을 정했다:** 일본어로 이탈한 응답은 9.1%, 정상 응답은 0%였다. 그래서 기본 기준 2%가 근거 있는 값이 됐다.
- **원인 분석을 기록으로 뒷받침했다:** "SDK가 4번 시도했다"는 추정을 실패 기록의 원문(`Retry failed after 4 tries`)으로 확인했다.
- **지시서에 없던 위험을 계획 단계에서 찾았다:** SDK 재시도를 끄면 타임아웃이 취소 예외로 바뀌어 run_mode 밖으로 빠져나간다. 코드를 읽어야만 보이는 문제였다.
- **원칙끼리 충돌하는 지점을 드러냈다:** "비우면 기존 동작" 원칙과 "기본 ko" 지시가 맞부딪히는 부분을 혼자 정하지 않고 질문으로 올렸다.
- **도구를 하나 늘리는 대가를 적었다:** 컨텍스트에 늘 올라가는 도구 설명이 늘어나는 대신 재출력 토큰 절약, 옮겨 적기 오류 제거, 저장 위치 고정을 얻는다.
- **작업 순서의 이유를 남겼다:** 2-4와 2-3의 의존 관계 때문에 순서를 바꿨다.
- **운영 제약을 미리 확인했다:** DLL 잠금 때문에 MCP 재연결이 필요하고, review 실행 한도 때문에 검토 횟수를 배분해야 한다.
