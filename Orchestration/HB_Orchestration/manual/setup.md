# 설치 · 빌드 · 실행

[← README](../README.md)

## 패키지 & 빌드

패키지는 csproj에 버전을 고정해 두었다 (`dotnet add package` 불필요). 복원 · 빌드 · 테스트:

```powershell
dotnet build HB_Orchestration.slnx
dotnet test tests/HB.Orchestration.Tests
```

| 프로젝트 | 패키지 |
|---|---|
| Agents | `Microsoft.Agents.AI` 1.25.0 · `Microsoft.Extensions.AI` 10.11.0 · `Microsoft.Extensions.AI.OpenAI` 10.11.0 · `Microsoft.Extensions.Logging.Abstractions` 10.0.13 |
| Mcp | `ModelContextProtocol` 2.2.0 · `Microsoft.Extensions.Hosting` 10.0.13 |
| Dashboard | `Markdig` 1.4.0 |

올릴 때는 csproj 버전을 바꾸고 전체 테스트를 돌린다.

평소 publish와 대시보드 실행은 **`HB_Orchestration.bat`** 으로 한다 (아래 [설치 · 빌드 · 실행](setup.md)).

- publish 출력은 **솔루션 폴더 밖**으로. `dotnet run`으로 MCP 등록하지 말 것.
- publish는 `appsettings.json` / `modes.json`을 **복사하지 않는다** (publish 폴더에서 고친 설정을 덮어쓰지 않도록). 처음 publish할 때만 배치가 기본 파일을 넣어준다. 원본에 새로 생긴 키는 publish 끝에 `[알림]`으로 보여 준다 ([설치 · 빌드 · 실행](setup.md)).
- .NET 10 SDK가 없으면 `Directory.Build.props`의 `net10.0` → `net9.0`.

## HB_Orchestration.bat (publish · 대시보드 실행)

솔루션 폴더의 `HB_Orchestration.bat`을 더블클릭하면 메뉴가 뜬다.

| 메뉴 | 동작 |
|---|---|
| 1 | MCP 서버 publish → `PUBLISH_DIR`. 실행 중인 MCP 서버가 있으면 **서버만 종료할지 묻고** 진행. 설정 파일은 백업 후 그대로 유지하고, 원본에만 있는 경로를 알린다 (아래) |
| 2 | 대시보드 빌드 후 별도 창에서 실행 + 브라우저 열기. 이미 떠 있으면 브라우저만 연다 |
| 3 | 1 + 2 |
| 4 | `HB_Orchestration.config`를 메모장으로 열기. 메모장을 닫으면 바로 다시 읽는다 |

명령줄로도 쓸 수 있다: `HB_Orchestration.bat publish` / `dashboard` / `all`. `publish --yes` · `all --yes`는 실행 중인 MCP 서버를 묻지 않고 종료한다 (그 밖의 두 번째 인자는 오류).

**설정 (`HB_Orchestration.config`)** — `KEY=VALUE`, `#`은 주석, 상대 경로는 배치 폴더 기준. 배치는 **자기와 같은 이름의 `.config`** 를 읽으므로 이름을 바꿀 땐 둘을 같이 바꾼다.

| 키 | 기본값 | 의미 |
|---|---|---|
| `SOLUTION_DIR` | `.` | `HB_Orchestration.slnx`가 있는 폴더 |
| `PUBLISH_DIR` | `..\Publish_Output` | MCP publish 폴더 (`.mcp.json`이 가리키는 곳). 대시보드 모드 화면도 여기를 편집 |
| `CONFIGURATION` | `Release` | 빌드 구성 |
| `DASHBOARD_PORT` | `5280` | 대시보드 포트 |

**publish와 실행 중인 Claude Code**
- 실행 중인 MCP 서버는 dll을 잡고 있어 그대로는 publish가 실패한다. 배치가 서버 프로세스를 찾아 **서버만 종료할지** `[y/N]`로 묻는다 (기본 No — `Y` · `YES`만 진행). 취소하면 메뉴 1은 메뉴로 돌아가고, 메뉴 3 · 명령줄 `all`은 publish만 건너뛰고 대시보드는 그대로 띄우며, 명령줄 `publish`는 종료한다. Claude Code와 대화는 그대로 남는다.
- 서버를 종료한 순간 그 세션에서 진행 중이던 `run_mode`가 있었다면 그 호출은 실패한다. 에이전트 호출 중이 아닐 때 publish할 것.
- **재연결은 수동이다** (테스트로 확인함). publish가 끝나면 각 Claude Code 세션에서 `/mcp` → `hb-orchestration` 재연결.
  - 확인하고 싶으면 "server_info 실행해줘" → 새 빌드 시각이 나오면 새 서버. server_info는 빌드 시각 · PID · 설정 읽은 시각 · 사용 가능 에이전트 수 · 설정 다시 읽기 실패 · 쓰는 modes.json 경로와 근거를 보여 준다.
  - 대시보드 **세션** 화면에서도 세션별 서버 빌드 시각으로 확인할 수 있다.
- 실행 중인 서버가 없으면 그냥 publish하고, 다음에 Claude Code를 열 때 새 서버가 뜬다.

**publish와 설정 파일 (`appsettings.json` · `modes.json`)**
- publish 전에 사용 중 설정을 `%LOCALAPPDATA%\HB_Orchestration\config-backup\<yyyyMMdd-HHmmss-난수>\`에 백업하고, publish 뒤 그 백업으로 되돌린다. 백업은 최근 20개를 남긴다 (이 이름 모양의 폴더만 정리).
- 백업에 실패하면 publish하지 않고 멈춘다. 되돌리기에 실패하면 `[ERROR]`와 백업 경로를 보여 주고 멈춘다 (손으로 복사).
- publish가 끝나면 **원본(`src/HB.Orchestration.Mcp/`)에만 있는 경로**를 `[알림]`으로 보여 준다. 파일은 바꾸지 않는다 — 필요한 항목을 사용 중 설정에 직접(또는 대시보드로) 옮긴다.
  ```
  [알림] modes.json: 원본에만 있음 Modes.perf_high
  [알림] appsettings.json: 원본에만 있음 Agents[Name=gpt_new].MaxOutputTokens
  appsettings.json: 원본에만 있는 경로 없음
  ```
  - 원본에만 있는 키는 그 경로 하나만 보인다 (안쪽은 펼치지 않음). 같은 경로에서 자료형이 다르면 `자료형 다름`. 사용 중에만 있는 키, 같은 자료형의 값 차이는 알리지 않는다.
  - 키는 대소문자 무시. Name을 가진 객체 배열(`Agents`)은 Name으로 짝짓는다. **그 밖의 배열(`Steps`, `FreeProviders` 등)은 안쪽을 비교하지 않는다.**
  - 2-way 비교라 사용 중 설정에서 일부러 지운 모드도 publish 때마다 다시 알린다.
  - publish 폴더 기준이다. `ModesPath` · `HB_MODES_PATH` · 프로젝트의 `hb-modes.json`을 쓰면 실제로 쓰이는 파일은 다를 수 있다.
  - 직접 실행: `dotnet <publish 폴더>\HB.Orchestration.Mcp.dll --config-drift <원본 폴더> <publish 폴더>` (종료 코드 0 비교함 / 1 비교 못 한 파일 있음 / 2 사용법 오류). 인자 없이 실행하면 평소의 MCP 서버다.

**기타**
- 대시보드 창을 닫으면 대시보드가 꺼진다.

## 빌드 에러가 나면
- 패키지 버전은 csproj에 고정돼 있다 ([설치 · 빌드 · 실행](setup.md)). 올린 뒤 `AsIChatClient` · `ChatClientAgent` · `McpServerTool` 같은 API가 없다고 나오면 바꾼 패키지의 변경 내역부터 본다 (Microsoft.Agents.AI · Extensions.AI · ModelContextProtocol은 자주 바뀐다).
- usage 필드가 바뀌면 `UsageExtractor`(캐시 · 추론 토큰)와 `AgentRunner`의 기록 부분을 같이 고친다.
- 에러 메시지와 `dotnet list package` 결과를 같이 보면 대부분 바로 원인이 나온다.
