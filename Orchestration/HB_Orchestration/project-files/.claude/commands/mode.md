---
description: 외부 에이전트 모드를 실행한다 (design_main, research_main, parallel_main 등)
argument-hint: <모드 이름> <요청>
---

인자: $ARGUMENTS
- 첫 단어가 모드 이름, 나머지가 요청. 모드 이름이 없거나 모르는 이름이면 `list_modes`로 확인해 보여주고 멈춘다.

1. 요청에 필요한 자료를 모은다. CLAUDE.md의 "input 작성" 규칙대로 최소한만.
   - `research_main`이면 CLAUDE.md의 "research_main 모드 순서"를 따른다 (웹 수집은 네가 한다).
   - `parallel_main`이면 작업 명세를 구체적으로 쓴다 (입출력, 제약, 대상 파일의 관련 부분).
2. 네 판단이 필요한 모드(design_* 등)면 너의 의견을 먼저 정리해두되 input에는 넣지 않는다.
   - `perf_main` · `perf_high`이고 대상이 디스크에 있는 코드면 **self-reviewer**가 ownReview를 쓴다 (계획서 `Orchestration/HB_Orchestration/docs/plans/2026-10-20_mode-서브에이전트-ownReview.md`). 순서는 `/cross-review` 2-1~2-4와 같다: 지휘자 검토를 먼저 작업 로그에 저장 → `subagent_in/<날짜>_<주제>/`에 명령으로 복사·SHA-256 확인 → self-reviewer 호출 → 형식을 어겼거나 중단됐으면 멈추고 사용자에게 알린다.
   - 위임 메시지의 제약에 "성능(실행 비용·할당·동기화·규모 증가)만 본다"를 넣는다. 너의 의견·의심 지점은 넣지 않는다.
   - 대상이 디스크에 없는 설계안뿐이면 지휘자 의견을 ownReview로 쓰고 reason에 태그를 붙이지 않는다.
3. `run_mode` 실행. `reason`에 부른 이유 한 줄. `[ownReview 필수]` 모드면 2에서 정리한 너의 의견 원문을 `ownReview`에 넣는다.
   - self-reviewer를 썼으면 `ownReview`에는 **self-reviewer 결과 원문만**, `reason` 맨 앞에 `[ownReview:self-reviewer]` (H4 훅이 검사).
4. 결과 정리:
   - perf_* (self-reviewer): CLAUDE.md "결과 종합" 형식. "Claude만 지적"은 self-reviewer가 찾고 외부가 놓친 것, 너만 찾은 것은 "지휘자 추가". 성능 밖 지적은 범위 밖으로 기각한다. `record_verdicts`의 `claudeAlsoFound`는 self-reviewer 결과에 있었는지, 지휘자 추가는 `steps` 없이 `reason` 맨 앞 `[지휘자 추가]`. 끝나면 `subagent_in/<날짜>_<주제>/`를 지운다.
   - design_*: 반론과 검증 결과를 반영해 최종 권고와 이유
   - research_main: 채택 후보와 다음 행동
   - parallel_main: 두 구현의 차이와 장단점, 어느 쪽(또는 조합)을 적용할지 제안 → 승인하면 네가 적용
