---
description: Unity CLI로 변경 관련 테스트를 돌리고 실패를 분석한다
argument-hint: [EditMode|PlayMode] [필터, 비우면 미커밋 변경에서 추론]
---

인자: $ARGUMENTS
- 첫 단어가 EditMode/PlayMode면 그 모드, 아니면 EditMode.
- 나머지는 `--filter` 값. 비어 있으면 `git diff HEAD`로 바뀐 코드를 보고 관련 테스트 클래스·네임스페이스를 추론한다. 관련 테스트가 없으면 그렇게 알리고, 테스트 작성을 제안한 뒤 멈춘다.

`unity` CLI로 직접 테스트를 돌릴 수 있다. 명령 형식이 불확실하면 `unity <명령> --help`로 확인한다.

## 배치 테스트 전용 프로젝트

* 배치 테스트(`unity test`)는 사람이 여는 프로젝트가 아니라 **배치 전용 프로젝트** `F:\AI\Unity_AITest\<버전>\ForAI`(6.3 · 7.0)에서 돌린다.
  * 이유: 사람 확인용 프로젝트를 같이 쓰면 manifest · Define · embedded 패키지가 중간에 바뀌고, 에디터를 닫아 달라는 요청이 필요해진다.
  * 이 프로젝트는 사람이 열지 않는다. Test Framework·`com.unity.pipeline`만 있고 Define을 바꾸는 패키지가 없다 (스모크 테스트 `AITest.Smoke.Editor`가 확인).
  * 테스트할 패키지는 그 프로젝트의 `Packages/manifest.json`에 `file:` 경로로 넣는다. 필요한 Define은 줄 전체를 바꾸지 말고 해당 심볼만 덧붙이거나 뺀다. 끝나면 `git -C F:\AI\Unity_AITest checkout -- .`으로 되돌리고 `git status`가 깨끗한지 본다.
  * 서버 설정의 추가 루트 이름은 `@unity_ai_test`다.
* 사람이 직접 확인할 때만 기존 프로젝트(예: ToolTest6.3)를 쓴다.

## 순서

아래 `<프로젝트>`는 배치 전용 프로젝트 `F:\AI\Unity_AITest\<버전>\ForAI`다. 사용자가 기존 프로젝트에서 돌려 달라고 했을 때만 그 프로젝트 경로를 쓴다.

1. 준비: 바뀐 패키지의 Unity 버전에 맞는 `<프로젝트>`를 고르고, 위 절대로 `Packages/manifest.json`에 `file:` 경로 · 필요한 Define을 넣는다.
   * 바뀐 코드가 패키지가 아니면(프로젝트 `Assets` 안) 배치 전용 프로젝트에 넣을 수 없다. 사용자에게 알리고 어디서 돌릴지 묻는다.
2. `unity status --format json --project-path <프로젝트>`로 그 프로젝트를 연 에디터가 있는지 본다.
   * **에디터가 열려 있으면** 같은 프로젝트로 배치 테스트(`unity test`)를 돌릴 수 없다 (Unity 프로젝트 잠금).
     사용자에게 에디터를 닫을지 묻거나, `unity command --query test`로 에디터가 테스트 명령을 노출하는지 확인해 그걸 쓴다. 배치 전용 프로젝트는 사람이 열지 않으므로, 열려 있으면 그 사실도 알린다.
   * 에디터가 Safe Mode(컴파일 에러)면 `unity pipeline list`로 확인하고 컴파일 에러부터 고친다.
3. 테스트 실행 — 결과는 파일로 받고 출력은 최소로:
   `unity test <프로젝트> --mode <모드> --filter "<필터>" --report-format junit --output <프로젝트>/Temp/claude-tests.xml --timeout 600 --quiet --no-banner`
   * 전체가 아니라 **변경과 관련된 테스트만** `--filter`로 돌린다. PlayMode는 느리니 필요할 때만.
4. 종료 코드로 판단한다: `0` 통과 / `8` 테스트 실패 / 그 외 실행 자체 실패(컴파일 에러, 라이선스, 타임아웃).
   * 0이면 통과 보고(테스트 수만) / 8이면 5단계 / 그 외면 원인(컴파일 에러 등)을 짧게 보고하고 멈춘다.
5. 실패 시 리포트 전체를 읽지 말고 실패한 테스트 항목(`<failure`)만 골라 읽는다.
   * 원인이 명확하면 수정안을 제시하고, 승인하면 고친 뒤 같은 필터로 재실행한다. 불분명하면 CLAUDE.md "테스트와 외부 에이전트" 규칙대로 `review_main` 모드를 고려한다.
6. 끝나면 배치 전용 프로젝트를 위 절대로 되돌린다 (`git -C F:\AI\Unity_AITest checkout -- .` → `git status`가 깨끗한지). 기존 프로젝트에서 돌렸으면 건너뛴다.
