---
description: Unity CLI로 변경 관련 테스트를 돌리고 실패를 분석한다
argument-hint: [EditMode|PlayMode] [필터, 비우면 미커밋 변경에서 추론]
---

인자: $ARGUMENTS
- 첫 단어가 EditMode/PlayMode면 그 모드, 아니면 EditMode.
- 나머지는 `--filter` 값. 비어 있으면 `git diff HEAD`로 바뀐 코드를 보고 관련 테스트 클래스·네임스페이스를 추론한다. 관련 테스트가 없으면 그렇게 알리고, 테스트 작성을 제안한 뒤 멈춘다.

CLAUDE.md의 "Unity 테스트" 순서를 따른다.

1. `unity status --format json` — 이 프로젝트 에디터가 열려 있으면 배치 테스트를 못 하니 사용자에게 알리고 선택지를 준다.
2. `unity test . --mode <모드> --filter "<필터>" --report-format junit --output Temp/claude-tests.xml --timeout 600 --quiet --no-banner`
3. 종료 코드: 0이면 통과 보고(테스트 수만) / 8이면 4단계 / 그 외면 원인(컴파일 에러 등)을 짧게 보고하고 멈춘다.
4. 실패 항목만 읽어 원인을 분석한다. 원인이 명확하면 수정안을 제시하고, 승인하면 고친 뒤 같은 필터로 재실행한다. 불분명하면 CLAUDE.md 규칙대로 `review_main` 모드를 고려한다.
