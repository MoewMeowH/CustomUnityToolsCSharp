---
description: 외부 에이전트 모드를 실행한다 (debate, research, parallel 등)
argument-hint: <모드 이름> <요청>
---

인자: $ARGUMENTS
- 첫 단어가 모드 이름, 나머지가 요청. 모드 이름이 없거나 모르는 이름이면 `list_modes`로 확인해 보여주고 멈춘다.

1. 요청에 필요한 자료를 모은다. CLAUDE.md의 "input 작성" 규칙대로 최소한만.
   - `research`면 CLAUDE.md의 "research 모드 순서"를 따른다 (웹 수집은 네가 한다).
   - `parallel`이면 작업 명세를 구체적으로 쓴다 (입출력, 제약, 대상 파일의 관련 부분).
2. 네 판단이 필요한 모드(debate 등)면 너의 의견을 먼저 정리해두되 input에는 넣지 않는다.
3. `run_mode` 실행. `reason`에 부른 이유 한 줄. `[ownReview 필수]` 모드면 2에서 정리한 너의 의견 원문을 `ownReview`에 넣는다.
4. 결과 정리:
   - debate/critique: 반론과 검증 결과를 반영해 최종 권고와 이유
   - research: 채택 후보와 다음 행동
   - parallel: 두 구현의 차이와 장단점, 어느 쪽(또는 조합)을 적용할지 제안 → 승인하면 네가 적용
