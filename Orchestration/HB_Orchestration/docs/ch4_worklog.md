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
