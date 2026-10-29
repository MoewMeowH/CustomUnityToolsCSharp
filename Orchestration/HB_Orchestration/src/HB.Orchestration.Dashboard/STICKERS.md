# 채팅방 스티커

대시보드 채팅방(`/Rooms`)이 말풍선 안의 `[[icon:태그]]`를 그림으로 바꾼다. 설정은 `appsettings.json`의 `Dashboard:Stickers`, 개인 값(그림 폴더)은 `appsettings.local.json`(git 제외).

## 설정

| 키 | 뜻 |
|---|---|
| `Dir` | 그림 폴더 (절대 경로). 비우면 그림 없이 글자 배지 |
| `SizePx` | 말풍선 안 크기 (CSS 픽셀, 24~512) |
| `Files` | 태그 → 폴더 안 파일 이름. 여기 있는 태그만 그림이 된다 |
| `Aliases` | 의미 태그 → `Files` 태그 (예: 완료 → 해냈다) |
| `Auto` | 상황 → 태그. 화면이 외부 에이전트 · 서브에이전트 · 판정 말풍선에 붙인다. 값을 비우면 그 상황은 끔 |

`Auto` 상황: `CallNoFindings` · `CallFindings` · `CallWarning` · `CallRequests` · `CallQuota` · `CallRateLimit` · `CallTimeout` · `CallFailed` · `SubagentResult` · `VerdictMostlyAdopted` · `VerdictMostlyRejected` · `VerdictMixed`.

## 지휘자가 쓸 때

- 노가리나 작업 보고의 맨 끝에만, 따로 한 줄로 0~1개. 문장마다 붙이지 않는다.
- 의미 태그: 인사 · 질문 · 확인 · 검색 · 완료 · 긍정 · 부정 · 웃기 · 당황 · 미안 · 요청 · 자랑 · 생각 · 대기 · 설명 · 발견 · 주의 · 의심 · 애매 · 근거 · 수정 · 잠. `Files`의 그림 이름을 바로 써도 된다. 목록에 없는 태그는 만들지 않는다.
- 완료는 실제 작업이 끝났을 때만, 당황은 실패 · 막힘에만. 사용자가 화났거나 심각한 문제면 쓰지 않는다.
- 계획서 · 작업 로그 · 커밋 메시지 · run_mode input · record_verdicts · Slack 글 · 파일 내용에는 쓰지 않는다.
