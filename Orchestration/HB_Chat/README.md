# HB_Chat

Claude Code(지휘자)를 **브라우저 · 데스크톱 창**에서 방 단위로 쓰는 로컬 채팅 앱입니다. 방마다 작업 폴더와 권한 단계를 정해 두고, 승인 · 질문 카드와 곁칸(단계 · 첨부 · 문서 · 터미널 등)으로 작업을 지켜봅니다. [HB_Orchestration](../HB_Orchestration/README.md)의 외부 에이전트 호출도 방 안에서 말풍선으로 보입니다.

## 주요 기능

- **방**: 작업 폴더 · 권한 단계(읽기 · 명령) · 모델 · 추론 강도를 방마다. 방 목록 · 검색 · 휴지통 · Markdown 내보내기.
- **승인 · 질문 카드**: 명령 방의 도구 승인(허용 · 거절 · 이 턴 동안 묻지 않기), 지휘자의 객관식 질문, 계획 확인. 외부 에이전트를 부르기 전에 모델 · 추론 강도를 고르는 카드(방 설정).
- **단톡방**: 지휘자 여럿(역할을 나눔) + Codex 구성원. `@이름` · `@All`로 부르고, 구성원 답 속 `@이름`으로 이어 부름. 편집 · 명령 구성원이 끼면 차례로 돈다.
- **곁칸**: 단계(할 일) · 방 정보 · 외부 실행 · 첨부 · 문서(작업 폴더의 계획서 · 작업 로그) · 연결(MCP) · 터미널 · 브라우저(명령 방, 지휘자 전용 Chrome 화면).
- **그려 보이기**: 답 속 ` ```widget ` · ` ```chart ` · ` ```slides ` 코드 블록을 격리된 틀 · Chart.js · 슬라이드로.
- **연결**: 방마다 MCP 서버 켜기 · 끄기, 앱에서 MCP 서버 더하기, Unity 프로젝트 연결.
- **사용량 한도**: Claude · Codex 구독 한도를 사이드바 아래에.
- **데스크톱 앱** (`HB.Chat.Desktop`, WebView2): 채팅 · 대시보드를 한 창에, 트레이 · Windows 알림 · 미니 창 · 전역 단축키 · 바탕화면 펫.

## 실행

**필요한 것**: Windows · .NET 10 SDK · Claude Code(`claude`가 PATH에 있거나 `Chat:ClaudePath`), 데스크톱 앱은 WebView2 런타임.

| 하는 일 | 방법 |
|---|---|
| 웹 앱 | `HB_Chat.bat` — Release로 빌드해 따로 창에서 띄우고 브라우저를 연다 (기본 http://localhost:5290). 이미 떠 있으면 브라우저만 |
| 데스크톱 앱 | `HB_Chat_Desktop.bat` — 채팅 서버 · 대시보드 · 데스크톱 앱을 빌드하고 띄운다. 앱이 띄운 서버는 앱을 끌 때 같이 끈다 |
| 빌드 · 시험 | `dotnet build HB_Chat.slnx` · `dotnet test HB_Chat.slnx` |

설정은 켤 때 한 번만 읽습니다 — 바꾸면 앱을 다시 켭니다.

## 설정 (`src/HB.Chat/appsettings.json`)

이 PC에만 쓸 값은 같은 폴더의 `appsettings.local.json`(git 제외)에 둡니다.

| 키 | 기본값 | 뜻 |
|---|---|---|
| `Urls` | `http://localhost:5290` | 듣는 주소 (로컬만) |
| `AllowedHosts` | `localhost;127.0.0.1;[::1]` | 받는 Host |
| `Chat:ClaudePath` | (비움 = PATH의 `claude`) | Claude Code 실행 파일 |
| `Chat:DataDir` | (비움 = `%LOCALAPPDATA%\HB_Chat`) | 방 · 첨부 · 기록 폴더 |
| `Chat:ClaudeProjectsDir` | (비움 = `~/.claude/projects`) | 세션 기록을 읽는 곳 |
| `Chat:Workspaces` | — | 방에서 고를 작업 폴더 (`이름: 경로`). 설정 화면에서도 더함 |
| `Chat:Models` · `Chat:DefaultModel` | `opus` · `sonnet` · `haiku` / `opus` | 고를 수 있는 모델 |
| `Chat:McpServer` | `hb-orchestration` | 외부 에이전트 말풍선 · 모델 선택 카드에 쓰는 MCP 서버 이름 |
| `Chat:DashboardUrl` | `http://localhost:5280` | 대시보드 주소 (데스크톱 앱 탭 · 링크) |
| `Chat:StickerSettingsDir` | 대시보드 폴더 | 스티커 설정을 읽을 곳 (상대 경로 = 앱 폴더 기준) |

## 데이터 폴더 (`%LOCALAPPDATA%\HB_Chat\`)

| 경로 | 내용 |
|---|---|
| `rooms.json` · `profiles.json` · `workspaces.json` | 방 목록 · 프로필 · 작업 폴더 |
| `attachments/` | 방별 첨부 |
| `approvals.jsonl` | 승인 기록 |
| `browser/` · `browser-shots/` | 지휘자 브라우저 프로필 · 스크린샷 |
| `webview2/` · `desktop-*.json` | 데스크톱 앱 캐시 · 창 설정 |
| `logs/` | 데스크톱 앱이 띄운 서버 로그 |

대화 내용 자체는 Claude Code의 세션 기록(`~/.claude/projects`)을 읽어 보여 줍니다.

## 보안 전제

- **로컬 전용입니다.** 로그인이 없고, 명령 방은 PC에서 명령을 실행합니다. 주소를 밖으로 열지 마세요 (포트 포워딩 · 공개 터널 X).
- 쓰기 요청은 같은 출처만 받고(antiforgery 포함), Host는 `AllowedHosts`만.
- 답 속 Markdown은 원시 HTML · 위험한 링크 · 속성을 지우고 그립니다. 위젯은 격리된 틀에서, 스크립트는 사용자가 "실행"을 눌러야 돕니다.

## 폴더 구조

```
HB_Chat/
├─ HB_Chat.slnx · HB_Chat.bat · HB_Chat_Desktop.bat
├─ src/
│  ├─ HB.Chat/            웹 앱 (Razor Pages · SSE)
│  └─ HB.Chat.Desktop/    데스크톱 앱 (WinForms + WebView2)
└─ tests/                 HB.Chat.Tests · HB.Chat.Desktop.Tests (화면 시험은 실제 Chrome/Edge, 없으면 건너뜀)
```
