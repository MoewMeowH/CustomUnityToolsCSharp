# 채팅방 스티커

대시보드 채팅방(`/Rooms`)이 말풍선 안의 `[[icon:태그]]`를 그림으로 바꾼다. 설정은 `appsettings.json`의 `Dashboard:Stickers`, 개인 값(그림 폴더)은 `appsettings.local.json`(git 제외).

## 설정

| 키 | 뜻 |
|---|---|
| `Dir` | 그림 폴더 (절대 경로). 비우면 그림 없이 글자 배지. HB_Chat은 이 폴더 바로 아래 폴더 하나를 스티커 팩 하나로 본다 |
| `DefaultPack` | 기본 팩: `Dir` 아래 폴더 이름. `Dir` 바로 아래에 그림이 없으면 여기서 찾는다 (대시보드 · HB_Chat 기본) |
| `SizePx` | 말풍선 안 크기 (CSS 픽셀, 24~512) |
| `Files` | 태그 → 폴더 안 파일 이름. 여기 있는 태그만 그림이 된다 |
| `Aliases` | 의미 태그 → `Files` 태그 (예: 완료 → 해냈다) |
| `Hints` | 의미 태그 → 쓸 때 (선택). HB_Chat 방 규칙의 태그 목록에 붙는다 |
| `Auto` | 상황 → 태그. 화면이 외부 에이전트 · 서브에이전트 · 판정 말풍선에 붙인다. 값을 비우면 그 상황은 끔 |

그림 형식: png · gif(움직이는 그림, 반복 재생) · webp · jpg. `Files`의 이름과 **확장자만 다른** 같은 이름도 찾는다 — 폴더마다 설정 이름 그대로 먼저, 없으면 gif → webp → png → jpg 차례. 찾는 폴더 차례는 고른 팩 → `Dir` → 기본 팩.

`Auto` 상황: `CallNoFindings` · `CallFindings` · `CallWarning` · `CallRequests` · `CallQuota` · `CallRateLimit` · `CallTimeout` · `CallFailed` · `SubagentResult` · `VerdictMostlyAdopted` · `VerdictMostlyRejected` · `VerdictMixed`.

## 지휘자가 쓸 때

HB_Chat이 Claude 방 규칙(시스템 프롬프트)으로 알려 준다 (`HB_Chat/src/HB.Chat/StickerRules.cs`, Codex 방은 없음). 태그 목록은 `Aliases` 중 `Files`로 이어지는 것, 태그별 쓸 때는 `Hints` — 태그를 더하거나 바꾸면 이 두 곳만 고친다. 설정이 바뀌면 열린 방은 다음 말에 바뀐 규칙을 한 번 받는다.

규칙 글: 노가리나 작업 보고의 맨 끝에 따로 한 줄로 0~1개 · 목록에 없는 태그는 만들지 않음 · 같은 태그 연달아 안 씀 · 노가리는 감정 쪽 먼저 · 사용자가 화났거나 심각하면 안 씀 · 계획서 · 로그 · 커밋 · run_mode input · record_verdicts · Slack 글 · 파일 내용에는 안 씀.

HB_Chat 밖 세션(터미널 · 데스크톱 앱)에는 안내가 없다 — 대시보드 `/Rooms`에서는 `Auto` 스티커만 보인다.
