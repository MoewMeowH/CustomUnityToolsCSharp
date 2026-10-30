# ToolWithCS 전용 규칙

## 프로젝트 폴더 (계획서 · 작업 로그 자리)

이 저장소에는 프로젝트가 여럿이다. 계획서 · 짝 로그 · 색인은 작업 대상 프로젝트 폴더의 `docs/`에 둔다 (CLAUDE.md "계획서와 작업 로그").

* `Orchestration/HB_Orchestration` — MCP 서버 · 대시보드(채팅방 `/Rooms` 포함) · 골든 · project-files 원본 · 공통 규칙 · 훅.
* `Orchestration/HB_Chat` — 대화형 채팅 앱(HB.Chat) · 데스크톱 껍데기(HB.Chat.Desktop).
* 둘에 걸친 작업은 주로 고치는 쪽에 두고, 다른 쪽 변경도 계획서에 적는다. `Orchestration/Perf`는 프로젝트가 아니다 (git 밖 preflight 자료).

## project-files 원본을 바꿀 때 (동기화 대상 확인)

공통 CLAUDE.md · `.claude/commands|scripts|agents` · 기본 리뷰 지침의 원본은 HB_Orchestration `project-files/`이고, 각 프로젝트에는 `tools/Sync-ProjectFiles.ps1`로 복사한 사본이 있다. 이 저장소에서만 원본을 고친다 (다른 프로젝트 세션에서 고칠 것이 생기면 여기서).

* 원본을 바꾸는 커밋을 할 때마다 **모든 동기화 대상을 비교만** 한다: ToolWithCS 루트에서 `powershell -ExecutionPolicy Bypass -File Orchestration\HB_Orchestration\tools\Sync-ProjectFiles.ps1 -All` (대상 목록은 그 옆 `sync-targets.local.txt`, 이 PC의 경로라 git 제외). 요약의 "맞출 것"과 함께 "참고"(settings.json 다름 = 훅 등록 차이일 수 있음, 프로젝트 지침, 대상에만 있는 파일)도 보고한다.
  * 사본이 다르면 `-Apply`는 멈추기만 하고 알려 주지 않으므로, 비교를 빼먹으면 옛 훅이 그대로 돈다.
* 뒤처진 대상이 있으면 대상 · 파일 목록을 사용자에게 보고한다. 맞추는 것(`-Apply`, 다른 파일이 있으면 `-Force` — 덮기 전 백업)은 사용자 확인 뒤 대상마다 따로 한다.
* 리뷰 지침(`REVIEW_GUIDELINES.md`)은 Sync가 없을 때만 만들고 덮지 않는다 (단 원본의 옛 판 그대로면 '기본 지침 옛 판'으로 갱신 대상).
* 이 파일(PROJECT.md)은 이 저장소 전용이라 동기화 대상이 아니다 (Sync는 없을 때만 빈 틀을 만든다).
