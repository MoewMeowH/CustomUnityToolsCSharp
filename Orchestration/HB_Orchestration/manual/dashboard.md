# 대시보드

[← README](../README.md)

## 대시보드

`HB_Orchestration.bat` → 2번 (또는 `dotnet run --project src/HB.Orchestration.Dashboard`) → http://localhost:5280

**호출 기록** (`/`)
- 기간 · 모드(기록의 `Tool` 값 — run_mode · golden · self_review) · 제공사 필터, **실행**(`Run`, runId 앞부분) · **검색**(`Q`: Id · RunId는 앞부분 일치, 이유 · 도구 · 역할 · 에이전트 · 모델 · 파일 · 첨부 · 응답 · 오류는 부분 일치), 자동 새로고침(10초, `Live`)
- 호출 수 · 토큰 · 평균 응답 · 예상 비용 · 실패, 일별 토큰. 합계 · 그래프는 걸러진 전체 기준이고, 표만 한 쪽 200행으로 나눈다
- **오늘 비용** · **오늘 토큰** 칸: 오늘 0시부터, `FreeProviders` 제외 (필터와 무관. 비용은 MCP appsettings 단가만 사용, 단가 없는 모델은 "단가 없는 모델 N건(비용 미집계)"으로 표시). 상한은 없다. 기간 필터의 "최근 24시간"은 어제 호출도 들어가므로 이 값과 다르다
- 캐시: `캐시 0`은 미적중, `캐시 정보 없음`은 제공사가 값을 주지 않은 기록(Gemini는 미적중일 때 값이 없음, 예전 기록)
- 상세: 요청/응답, **같은 실행의 다른 단계와 실행 전체 토큰 합계**
- **첨부 코드 · 관련 파일**: 이름을 누르면 대시보드 안 보기(`/Attachment`, 줄 번호 + 보낸 줄 범위 강조). 옆 **Rider**는 IDE로 연다
  - 링크 형식: `Dashboard:FileLinkTemplate` 기본 `jetbrains://rider/navigate/reference?project={project}&path={path}:{line}` (JetBrains 데몬이 Rider로 넘김, 처음 한 번 브라우저가 허용을 묻는다)
  - Rider는 **열린 솔루션 폴더 기준 상대 경로만** 연다 (2026-10-19 Rider 2026.2.3 확인: 절대 경로·`/`까지 인코딩·솔루션 폴더 밖 파일은 안 열림). 그래서 폴더 → 프로젝트 이름을 Rider 최근 솔루션 목록(`%APPDATA%/JetBrains/Rider*/options/recentSolutions.xml`)에서 읽고, 파일을 담은 가장 깊은 솔루션 폴더 기준으로 링크를 만든다. 어느 솔루션에도 속하지 않으면 "Rider 없음"
  - 목록에 없거나 이름이 다르면 `Dashboard:RiderProjects`(`"폴더": "프로젝트 이름"`)로 덧붙인다. 목록 파일 위치는 `Dashboard:RiderRecentSolutionsPath`
  - 예전 기본값 `http://localhost:63342/api/file?...`은 Rider 2026.2에 그 기능이 없어 404다. `{project}`가 없는 템플릿은 예전처럼 절대 경로를 넣는다
  - 대시보드 보기는 **그 기록에 적힌 항목만** 연다 (순번 밖은 404). 경로 규칙은 서버와 같다 (루트 밖·링크·전송 금지 패턴 거절). 첨부 코드는 파일이 그 뒤 바뀌었을 수 있으니 실제 보낸 내용은 요청(Prompt)에서 본다
- **웹검색** 패널: 검색어, 응답에 인용된 URL, 참고한 URL 전체(접힘), 검색 결과에 없는 URL(빨간색, 지어냈을 수 있음). 목록에는 `· 검색 N`(출처 의심이 있으면 빨간색). 모드 화면의 단계 칸에 검색 설정과, 검색할 수 없는 에이전트면 경고
- **첨부 이미지**: 보낸 순서대로 썸네일(누르면 원본). 기록에는 원문이 없어 지금 파일을 보여 주고, 기록의 sha256 앞 12자와 비교해 `보낸 이미지와 같음` / `기록 이후 파일이 바뀜` / `파일 없음`을 표시한다. 목록에는 `· 이미지 N`
- 상세 위쪽에 **지휘자 판단과 에이전트 응답을 나란히**: `reason` · `ownReview`(결과를 보기 전 자체 검토) · 응답. 5장 이전 기록은 "기록 없음", ownReview가 필수가 아닌 모드에서 넣지 않았으면 "넣지 않음"
- **에이전트가 받은 지침·설정** 표: 시스템 프롬프트 · 리뷰 지침 · modes.json · 서버 설정의 경로 · SHA-256 · 크기. 원문이 `blobs/`에 있으면 "원문" 링크 (그 기록이 가리키는 해시만 열림, 링크 파일은 열지 않음, 서버 설정은 기본 저장 안 함). 단계 목록에 단계별 시스템 프롬프트 해시 앞 8자
- **Claude 세션** 칸: 이 호출을 부른 세션으로 이동 (확정 / 추정 표시, 아래 "Claude 세션")
- 비용: **달러와 원화를 함께** 표시 (`$0.0416 (₩57)`). 캐시된 입력 토큰은 캐시 단가로 계산
  - 계산 (`ModelPrice.CostOf`, 서버 · 골든과 같은 코드): `(입력 - 캐시 읽기 - 캐시 쓰기) × 입력 단가 + 캐시 읽기 × 캐시 단가 + 캐시 쓰기 × 캐시 쓰기 단가 + 출력 × 출력 단가 + 웹검색 호출 수 / 1000 × WebSearchPerThousand` (입력 토큰에는 캐시 토큰이 포함됨). 단가는 기록 시각 기준(`Changes`)
  - 단가·환율은 MCP `appsettings.json`의 `Orchestration:Pricing`에 USD로 저장하고, **모드 화면의 "모델 단가" 표에서 편집**한다
  - 캐시 토큰 수는 이 기능을 넣은 뒤의 호출부터 기록된다 (그 전 기록은 전부 일반 입력 단가로 계산)

**모드** (`/Modes`)
- MCP publish 폴더의 `modes.json`을 읽어 모드마다 흐름을 그림으로 보여준다 (순차 → / 동시 묶음 / 토론 ⇄ → 판정).
- 단계별 에이전트(드롭다운), 최대 회차, 반환 범위를 바꿔 **저장**하면 다음 `run_mode`부터 바로 반영된다 (재시작 불필요).
- **모델 단가** 표: 설정에 단가가 있는 모델, 켜진 에이전트가 쓰는 모델, 호출 기록에 나온 모델을 모두 한 줄씩 보여주고(쓰는 에이전트·기록 횟수 표시, 단가 없는 모델 강조), **입력 / 캐시 입력 / 출력 단가(USD / 1M 토큰)**와 환율을 편집한다. 에이전트와 상관없이 모델 단위라 예전 모델도 계속 관리할 수 있다. 세 칸을 다 비우면 그 모델의 단가를 지우고, 마지막 빈 줄로 새 모델을 추가한다. 고치는 것은 처음 단가뿐이다 — 날짜별 변경(`Changes`) · 캐시 쓰기 · 웹검색 단가는 appsettings.json에서 직접 고친다.
- **이미지 첨부** 패널 (표시 전용): `Images` 상한과 제공사별 `ImageDetail`. 에이전트 표의 **이미지** 열은 `Vision` (표시 전용, 저장해도 기존 값 보존. 바꾸려면 appsettings.json 직접 수정).
- 저장 전 검증: 없는 에이전트·역할, Debate 단계 수, 회차 범위. 꺼진 에이전트는 경고만.
- 저장 방식: `modes-backup/`에 백업(최근 10개) → 임시 파일에 쓰고 한 번에 교체. 화면을 연 뒤 파일이 다른 곳에서 바뀌었으면 저장을 거부하니 **다시 불러오기** 후 수정.

**모드 화면 — 에이전트 편집** (appsettings.json)
- 표에서 이름 · 제공사 · 모델 ID · 출력 상한 · 타임아웃(초) · 추론 강도 · 사용 여부를 바꾸고 **에이전트 저장**. 마지막 빈 줄에 입력하면 새 에이전트가 추가된다. 저장 즉시 반영 (재시작 불필요).
- **이름 변경 동기화**: 이름을 바꾸면 그 이름을 쓰던 modes.json의 모든 단계도 새 이름으로 바뀐다.
- 모드가 쓰고 있는 에이전트는 삭제할 수 없다 (삭제 칸이 잠김). 먼저 모드의 에이전트를 바꾼다.
- 백업: `settings-backup/` (최근 10개). appsettings.json의 다른 섹션(Logging, Providers, Budget)과 표에 없는 필드는 그대로 둔다.

**모드 화면 — 없는 에이전트 일괄 교체**
- appsettings.json을 메모장으로 직접 고쳐서 modes.json이 없는 이름을 가리키게 되면, 화면 위에 경고와 함께 `없는 이름 → 바꿀 에이전트` 선택이 뜬다. **일괄 교체**를 누르면 그 이름을 쓰는 모든 단계가 바뀐다.

**세션** (`/Sessions`)
- 실행 중인 Claude Code 세션(= MCP 서버)마다 프로젝트, PID, 시작 시각, 서버 빌드 시각, 모드별 실행 횟수를 보여준다 (한도 없음, 이름을 바꾸기 전 서버가 남긴 예전 모드 이름은 새 이름으로 묶음).
- 서버 빌드가 publish 폴더의 dll보다 오래됐으면 **이전 빌드** 표시 → 그 세션에서 `/mcp`로 재연결하면 새 서버가 뜬다.
- 강제 종료된 서버가 남긴 기록은 화면을 열 때 자동 정리.
- 어느 폴더를 볼지는 대시보드 `appsettings.json`의 `Dashboard:McpConfigDir` (배치로 실행하면 `HB_Orchestration.config`의 `PUBLISH_DIR`가 우선). 원본 값은 이 PC 절대 경로(`F:/AI/ToolWithCS/Orchestration/Publish_Output`)라, 배치 없이 `dotnet run`할 때 다른 PC면 고친다. 편집할 파일을 직접 지정하려면 `Dashboard:ModesPath`.
- 대시보드 설정 키: `Dashboard:LogPath`(calls.jsonl 경로, 비우면 [에이전트 설정 (appsettings.json)](agents.md) 데이터 폴더) · `Dashboard:Prices`(예전 방식 단가, MCP 단가에 없는 모델만 채움) · `Dashboard:FileLinkTemplate` · `Dashboard:RiderProjects` · `Dashboard:ClaudeProjectsDir` · `Dashboard:Billing:AdminKeyEnv` · `Dashboard:ModesPath` · `Dashboard:RiderRecentSolutionsPath` · `Dashboard:ClaudeSessionLimit`. 단가 파일은 250ms에 한 번만 다시 확인한다.
- 직접 지정이 없으면 서버와 같은 찾는 순서로 편집할 modes.json을 정한다: MCP appsettings `Orchestration:ModesPath` → **대시보드 프로세스의** `HB_MODES_PATH` → publish 폴더 `modes.json`. 세션 폴더의 `hb-modes.json`은 대시보드가 알 수 없어 뺀다. 화면 머리에 "편집 중: 경로 (근거)"가 보인다.
- 서버의 `HB_MODES_PATH`(프로세스마다 다름)나 Unity 프로젝트의 `hb-modes.json`은 대시보드가 알 수 없다. 그래서 호출 기록(provenance)에서 **최근 7일 실행이 실제로 쓴 modes.json**을 모아, 편집 중인 파일과 다르면 경고로 보인다. 같은 파일인데 그 뒤 내용이 바뀌었으면 "그 뒤 수정됨"으로 표시한다.
- 그 아래에 **최근 7일 실행이 붙인 리뷰 지침**(provenance.guidelines)을 보인다. 프로젝트마다 자기 지침을 고쳐 쓰므로 내용은 비교하지 않고 **경로 규칙**으로만 경고한다: 경로가 `review_tmp/` 아래이거나 파일 이름이 `REVIEW_GUIDELINES.md`가 아니면 경고(하네스 임시 지침 · `REVIEW_GUIDELINES_PATH` 지정 실수). "지침 파일 없음"으로 돈 실행도 프로젝트 폴더별로 경고한다 (역할 `UseGuidelines=false` · 골든 `--no-guidelines`는 일부러 끈 것이라 뺀다). 나머지는 경로 · 해시 앞 12자리 · 실행 수 · 마지막 사용을 접힌 목록으로 — 이름이 같은 오래된 사본은 해시로 눈으로 확인한다.

**Claude 세션** (`/ClaudeSessions`) — 지휘자가 무엇을 알고 있었나
- Claude Code 세션 기록(`~/.claude/projects/<경로 인코딩>/<sessionId>.jsonl`)을 **읽기만** 해서, 세션마다 시작 시각(로컬) · 버전 · 프로젝트 · 불러온 지침 파일 · 부른 `run_mode`를 보여준다. **대화 본문은 보여주지 않는다.**
- 지침 파일: `instructions` 항목의 `files[]` — 경로 · 종류 · 크기 · SHA-256 (기록된 content의 UTF-8 바이트 기준). 지금 디스크 파일과 대조해 `같음` / `끝 줄바꿈만 다름`(디스크 = content + LF 정확히 1개) / `다름` / `파일 없음`. 세션 기록이 끝 줄바꿈을 빼고 저장하는 것은 파일 3개로 확인한 관찰이다.
- `run_mode` 연결: 결과 첫 줄 `runId:`가 있으면 **확정**. 머리말이 없는 이전 서버의 결과는 같은 모드 · 같은 프로젝트 · 호출 시각 −2초 ~ +60초에 시작한 실행이 하나뿐일 때만 **추정**. 후보가 여럿이면 연결하지 않고, 실행 전에 거절된 호출(`[`로 시작하는 결과)은 추정하지 않는다. 다른 세션이 확정한 실행은 추정 후보에서 뺀다.
- 결과가 백그라운드(120초 초과 → `<task-notification>`)나 큰 출력(`<persisted-output>` 미리보기)으로 들어와도 찾는다. 다른 도구 결과(예: Bash로 calls.jsonl을 읽은 출력)에 든 runId는 쓰지 않는다.
- 세션 기록 형식은 비공식이다. JSON이 아니거나 모양이 다른 줄은 "알 수 없는 줄 n", 알아볼 줄이 하나도 없는 파일은 "알 수 없는 형식"으로 표시한다. 줄마다 엄격한 UTF-8로 읽고, 잘못된 바이트가 있는 줄은 알 수 없는 줄로 센다.
- 설정: `Dashboard:ClaudeProjectsDir` (비우면 `CLAUDE_CONFIG_DIR/projects` → `%USERPROFILE%\.claude\projects`), `Dashboard:ClaudeSessionLimit` (목록에 보일 최근 세션 수, 기본 50. 연결은 전체 세션으로 계산).
- `/Sessions`(살아 있는 MCP 서버)와는 다른 화면이다.

**서브에이전트 결과** (`/Subagents`) — 검토형 서브에이전트가 넘긴 원문과 서버 기록 여부
- `/cross-review` · `perf_*`에서 서브에이전트 결과는 `run_mode`의 `ownReview`로만 남는다. 외부 호출이 실행되지 않으면(안전 필터 · 실패) 결과가 어디에도 안 남았다 → Claude Code가 남기는 서브에이전트 기록(`~/.claude/projects/<프로젝트>/<세션>/subagents/agent-<id>.jsonl` + `.meta.json`의 `SubagentHandback`)에서 **원문을 그대로 복사**해 데이터 폴더 `subagents/`에 보관한다. Claude Code가 세션 기록을 지워도(`cleanupPeriodDays`) 남는다.
- 보관은 MCP 서버 시작 때(백그라운드, 실패해도 서버는 뜸)와 이 화면 · 첫 화면을 열 때(30초에 한 번) 한다. 바뀐 세션 기록만 다시 읽는다. 이어 부른 서브에이전트는 넘길 때마다 따로 남는다. 대상은 검토형 4종만 (`SubagentArchive.ReviewerTypes` = `record_verdicts selfReview.agent` 목록).
- 서버 기록 칸: 원문이 `self_review` 기록 응답이거나 `run_mode` ownReview 안에 그대로 있으면 그 runId(줄바꿈 · 줄 끝 공백만 정리해 비교, 200자 미만 원문은 통째로 같을 때만). 글자는 다르지만 20자 이상인 줄(`## 읽은 파일` 절 제외)의 80% 이상이 한 기록에 있으면 **거의 같음 n%**(지휘자가 형식을 맞추며 고쳐 넣은 경우). 아니면 **기록 없음** + 가장 비슷한 기록 · 일치율(참고).
  - 후보 기록: `self_review`는 같은 에이전트의 기록만. 어느 쪽이든 결과를 넘긴 시각 −5분 ~ +2일 안에 기록된 것만 (self_review = 기록 시각, run_mode = 실행 시작). 재검토 결과가 1차 기록에 붙거나 다른 작업의 기록에 붙지 않게.
  - 2026-10-27 실측: 102건 = 같음 62 · 거의 같음 12 · 기록 없음 26 · 진행 중일 수 있음 2 (시험 · 파일럿 포함).
- 넘긴 지 1시간 안의 기록 없음은 "진행 중일 수 있음"으로 따로 보인다 (지휘자가 run_mode · 판정을 끝내야 기록된다).
- 첫 화면에 최근 7일 "기록 없는 서브에이전트 결과 n건" 안내가 뜬다 (진행 중일 수 있는 것 제외). 기록은 화면이 하지 않는다 — 확인 뒤 필요하면 그 세션에서 `record_verdicts selfReview`로 남긴다 (`/cross-review` 3단계).
- 형식이 바뀐 것으로 보이면(meta.json 없음 · agentType 없음 · 끝났는데 결과 없음) 화면 아래 "보관 오류"와 MCP 로그 경고로 보이고, 그 기록은 다음 보관 때 다시 읽는다 (리더를 고치면 되살아남).
- 이 화면은 결과 **본문**을 보여 준다 (Claude 세션 화면과 다름). 리뷰 대상 코드 조각이 들어 있으니 대시보드는 localhost로만 연다.

**채팅방** (`/Rooms` · `/Room/{세션}`) — 세션 하나를 대화방 하나로
- 사용자 · 지휘자 대화(세션 기록의 text 블록)와 그 세션의 외부 에이전트 · API 리뷰 · 서브에이전트 · 판정을 시간순 말풍선으로. 스티커(`[[icon:태그]]`) 설정은 `src/HB.Orchestration.Dashboard/STICKERS.md`.
- 실시간 갱신: 열어 둔 방 · 방 목록은 `Dashboard:RoomPollSeconds`(기본 3, 0이면 끔)초마다 파일 크기 · 수정 시각만 확인하고, 바뀌었을 때만 말풍선 · 목록 부분을 다시 받는다. 탭이 안 보이면 멈추고, 화면의 "끄기"는 그 브라우저에만 기억된다. 진행 중인 세션은 기록 끝에 붙은 줄만 이어 읽는다. 서브에이전트 결과는 보관 주기(30초) 때문에 늦게 뜬다.
- 세션 대화 본문이 그대로 보이므로 대시보드는 localhost로만 연다 (`AllowedHosts`도 localhost만).

**청구 대조** (`/Billing`) — OpenAI 실제 청구와 기록 기준 계산 비교
- 기간: 7일 · 3일 · 1일(UTC 날짜별 금액·토큰) · 시간별 24시간(토큰과 기록 기준 계산, 오늘 누적 청구는 한 줄로). OpenAI 비용은 UTC 자정 기준 하루 버킷이고, 하루가 끝나기 전에도 몇 분 지연으로 누적값이 갱신된다.
- 청구값 가져오기:
  - 조직 Admin 키(`sk-admin-…`, Read Only로 충분)가 환경 변수(이름은 설정 키 `Dashboard:Billing:AdminKeyEnv`, 기본 `OPENAI_ADMIN_KEY`)에 있으면 "새로 가져오기" 버튼으로 Costs API(항목별)·Usage API(모델별 토큰, 캐시 쓰기 포함)를 부른다. 버튼을 누를 때만 부르고 자동 새로고침과 묶지 않는다.
  - 키가 없으면 OpenAI 대시보드에서 받은 비용 파일(CSV·JSON)을 올린다 (메모리에서만 읽고 저장하지 않음). 이때는 날짜별 금액 대조만 된다.
- 키는 프로세스 환경 변수 → Windows 사용자 환경 변수 순으로 매번 읽는다 (새로 넣어도 재시작 불필요). 화면·로그·설정 파일에 값을 쓰지 않는다.
- 계산은 OpenAI 조직으로 청구되는 제공사(openai · openai-web 등) 호출만, 서버와 같은 `ModelPrice.CostOf` (웹검색은 search 동작 수, 추정). 캐시 쓰기를 기록하기 전(2026-10-18 이전) 호출이 있는 날은 "쓰기 가정"(캐시에 안 걸린 입력 전부 쓰기) 값을 함께 보여준다. 기록 밖 호출(curl·같은 조직의 다른 도구)은 계산에 없다.
