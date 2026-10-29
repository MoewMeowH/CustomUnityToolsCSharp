# 설치 · 빌드 · 실행

[← README](../README.md)

## 패키지 & 빌드

패키지 버전은 csproj에 고정해 두었습니다. `dotnet add package`는 필요 없습니다.

복원 · 빌드 · 테스트:

```powershell
dotnet build HB_Orchestration.slnx
dotnet test tests/HB.Orchestration.Tests
```

| 프로젝트 | 패키지 |
|---|---|
| Agents | `Microsoft.Agents.AI` 1.25.0 · `Microsoft.Extensions.AI` 10.11.0 · `Microsoft.Extensions.AI.OpenAI` 10.11.0 · `Microsoft.Extensions.Logging.Abstractions` 10.0.13 |
| Mcp | `ModelContextProtocol` 2.2.0 · `Microsoft.Extensions.Hosting` 10.0.13 |
| Dashboard | `Markdig` 1.4.0 |

- 패키지를 올릴 때는 csproj 버전을 바꾸고 전체 테스트를 돌립니다.
- .NET 10 SDK가 없으면 `Directory.Build.props`의 `net10.0`을 `net9.0`으로 바꿉니다.

평소 publish와 대시보드 실행은 **`HB_Orchestration.bat`** 으로 합니다 ([아래](#hb_orchestrationbat)).

- publish 출력은 **솔루션 폴더 밖**에 둡니다.
- `dotnet run`으로 MCP를 등록하지 않습니다.
- publish는 `appsettings.json` · `modes.json`을 **복사하지 않습니다**. publish 폴더에서 고친 설정을 덮어쓰지 않기 위해서입니다.
  - 처음 publish할 때만 배치가 기본 파일을 넣어 줍니다.
  - 원본에 새로 생긴 키는 publish 끝에 `[알림]`으로 보여 줍니다 ([publish와 설정 파일](#publish와-설정-파일)).

## HB_Orchestration.bat

publish와 대시보드 실행을 맡는 배치 파일입니다. 솔루션 폴더의 `HB_Orchestration.bat`을 더블클릭하면 메뉴가 뜹니다.

| 메뉴 | 동작 |
|---|---|
| 1 | MCP 서버를 `PUBLISH_DIR`에 publish. 실행 중인 MCP 서버가 있으면 **서버만 종료할지 묻고** 진행합니다. 설정 파일은 백업 후 그대로 두고, 원본에만 있는 경로를 알립니다 |
| 2 | 대시보드를 빌드하고 별도 창에서 실행한 뒤 브라우저를 엽니다. 이미 떠 있으면 브라우저만 엽니다 |
| 3 | 1 + 2 |
| 4 | `HB_Orchestration.config`를 메모장으로 엽니다. 메모장을 닫으면 바로 다시 읽습니다 |

명령줄로도 쓸 수 있습니다.

```powershell
HB_Orchestration.bat publish
HB_Orchestration.bat dashboard
HB_Orchestration.bat all
HB_Orchestration.bat publish --yes   # 실행 중인 MCP 서버를 묻지 않고 종료
HB_Orchestration.bat all --yes
```

두 번째 인자는 `--yes`만 받습니다. 그 밖의 값은 오류입니다.

### 설정 (`HB_Orchestration.config`)

- 형식은 `KEY=VALUE`, `#`은 주석입니다.
- 상대 경로는 배치 폴더 기준입니다.
- 배치는 **자기와 같은 이름의 `.config`** 를 읽습니다. 이름을 바꿀 땐 둘을 같이 바꿉니다.

| 키 | 기본값 | 의미 |
|---|---|---|
| `SOLUTION_DIR` | `.` | `HB_Orchestration.slnx`가 있는 폴더 |
| `PUBLISH_DIR` | `..\Publish_Output` | MCP publish 폴더 (`.mcp.json`이 가리키는 곳). 대시보드 모드 화면도 여기를 편집합니다 |
| `CONFIGURATION` | `Release` | 빌드 구성 |
| `DASHBOARD_PORT` | `5280` | 대시보드 포트 |

### publish와 실행 중인 Claude Code

실행 중인 MCP 서버는 dll을 잡고 있어서, 그대로는 publish가 실패합니다.

- 배치가 서버 프로세스를 찾아 **서버만 종료할지** `[y/N]`로 묻습니다.
  - 기본은 No입니다. `Y` · `YES`만 진행합니다.
  - Claude Code와 대화는 그대로 남습니다.
- 취소하면:

  | 실행 방법 | 취소했을 때 |
  |---|---|
  | 메뉴 1 | 메뉴로 돌아갑니다 |
  | 메뉴 3 · 명령줄 `all` | publish만 건너뛰고 대시보드는 그대로 띄웁니다 |
  | 명령줄 `publish` | 종료합니다 |

- 서버를 종료한 순간 그 세션에서 `run_mode`가 진행 중이었다면 그 호출은 실패합니다. 에이전트 호출 중이 아닐 때 publish합니다.
- 실행 중인 서버가 없으면 그냥 publish합니다. 다음에 Claude Code를 열 때 새 서버가 뜹니다.

**재연결은 수동입니다.** publish가 끝나면 각 Claude Code 세션에서 `/mcp` → `hb-orchestration`을 재연결합니다.

새 서버인지 확인하는 방법:

- Claude Code에 "server_info 실행해줘" → 새 빌드 시각이 나오면 새 서버입니다.
  - server_info가 보여 주는 것: 빌드 시각 · PID · 설정 읽은 시각 · 사용 가능 에이전트 수 · 설정 다시 읽기 실패 · 쓰는 modes.json 경로와 근거.
- 대시보드 **세션** 화면에서 세션별 서버 빌드 시각을 봅니다.

### publish와 설정 파일

`appsettings.json` · `modes.json`을 다루는 방식입니다.

**백업과 되돌리기**

- publish 전에 사용 중 설정을 아래 폴더에 백업하고, publish 뒤 그 백업으로 되돌립니다.
  ```
  %LOCALAPPDATA%\HB_Orchestration\config-backup\<yyyyMMdd-HHmmss-난수>\
  ```
- 백업은 최근 20개를 남깁니다. 이 이름 모양의 폴더만 정리합니다.
- 백업에 실패하면 publish하지 않고 멈춥니다.
- 되돌리기에 실패하면 `[ERROR]`와 백업 경로를 보여 주고 멈춥니다. 이때는 손으로 복사합니다.

**원본에만 있는 경로 알림**

publish가 끝나면 **원본(`src/HB.Orchestration.Mcp/`)에만 있는 경로**를 `[알림]`으로 보여 줍니다.

```
[알림] modes.json: 원본에만 있음 Modes.perf_high
[알림] appsettings.json: 원본에만 있음 Agents[Name=gpt_new].MaxOutputTokens
appsettings.json: 원본에만 있는 경로 없음
```

- 파일은 바꾸지 않습니다. 필요한 항목은 사용 중 설정에 직접(또는 대시보드로) 옮깁니다.
- 원본에만 있는 키는 그 경로 하나만 보입니다. 안쪽은 펼치지 않습니다.
- 같은 경로에서 자료형이 다르면 `자료형 다름`으로 알립니다.
- 알리지 않는 것: 사용 중에만 있는 키, 같은 자료형의 값 차이.
- 키는 대소문자를 무시합니다.
- 배열:
  - Name을 가진 객체 배열(`Agents`)은 Name으로 짝짓습니다.
  - **그 밖의 배열(`Steps`, `FreeProviders` 등)은 안쪽을 비교하지 않습니다.**
- 2-way 비교라서, 사용 중 설정에서 일부러 지운 모드도 publish 때마다 다시 알립니다.
- publish 폴더 기준입니다. `ModesPath` · `HB_MODES_PATH` · 프로젝트의 `hb-modes.json`을 쓰면 실제로 쓰이는 파일은 다를 수 있습니다.

직접 실행할 수도 있습니다. 인자 없이 실행하면 평소의 MCP 서버입니다.

```powershell
dotnet <publish 폴더>\HB.Orchestration.Mcp.dll --config-drift <원본 폴더> <publish 폴더>
```

| 종료 코드 | 뜻 |
|---|---|
| 0 | 비교함 |
| 1 | 비교 못 한 파일 있음 |
| 2 | 사용법 오류 |

### 기타

- 대시보드 창을 닫으면 대시보드가 꺼집니다.

## 빌드 에러가 나면

- 패키지 버전은 csproj에 고정돼 있습니다 ([패키지 & 빌드](#패키지--빌드)).
- 패키지를 올린 뒤 `AsIChatClient` · `ChatClientAgent` · `McpServerTool` 같은 API가 없다고 나오면, 바꾼 패키지의 변경 내역부터 봅니다. Microsoft.Agents.AI · Extensions.AI · ModelContextProtocol은 자주 바뀝니다.
- usage 필드가 바뀌면 `UsageExtractor`(캐시 · 추론 토큰)와 `AgentRunner`의 기록 부분을 같이 고칩니다.
- 에러 메시지와 `dotnet list package` 결과를 같이 보면 대부분 바로 원인이 나옵니다.
