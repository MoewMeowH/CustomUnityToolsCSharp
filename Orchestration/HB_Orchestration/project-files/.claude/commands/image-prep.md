---
description: 기획서 xlsx 이미지를 외부 에이전트에게도 보여야 할 때 준비한다 (collect → 사용자 정리 → shrink → run_mode images)
argument-hint: <제목> <xlsx...>
---

인자: $ARGUMENTS
- 첫 단어가 제목, 나머지가 xlsx 경로. 다른 명령(/preflight · /work-order 등) 안에서 이 절차를 따를 때는 그 작업의 제목 · 파일을 쓴다.

외부 에이전트에게도 이미지를 보여야 할 때만 쓴다 (예: 기획서 요약을 지휘자 혼자 하지 않도록). 사용자가 고른 이미지만 보낸다.

1. `python .claude/scripts/image_prep.py collect <제목> <xlsx...>` → `docs/image/<날짜>_<제목>/`(세션을 연 프로젝트 루트 기준)에 이미지와 표 Markdown, `index.md`가 생긴다.
   레퍼런스·표지 시트, 중복, 300px 미만은 `_제외/`로 자동으로 옮겨진다.
2. **여기서 멈추고** 사용자에게 폴더와 `index.md`를 알린다. 사용자가 필요 없는 것을 지우고, 글씨를 읽어야 하는 것은 `<문서>/원본/`으로 옮긴다.
   사용자가 끝났다고 하기 전에는 shrink·첨부를 하지 않는다.
3. `python .claude/scripts/image_prep.py shrink docs/image/<날짜>_<제목>` → `_send/`에 긴 변 512px(`원본/`은 768px)로 줄인 png가 생기고, `run_mode`에 넣을 `images`·`context` 목록이 출력된다.
4. `run_mode`의 `images`에 `_send/` 파일을 넣고, 표 Markdown은 `context`로 붙인다. 서버는 줄이지 않는다 (긴 변 1024px 초과·20장 초과는 거절).
   * 이미지는 입력을 받는 단계에만 붙고, 에이전트 수만큼 토큰이 곱해진다.
   * 512px에서 에이전트가 이미지 안 글자를 읽은 결과는 사양 근거로 쓰지 않는다 (문구를 지어낼 수 있다). 이미지는 번호 위치·배치 확인용이고, 문구는 표·string key로 확인한다.
   * 표에 StringID가 없는 번호는 요약에 "문서 / 시트 / 화면 / 번호" 키로 목록을 남긴다 (화면마다 번호가 1부터 다시 시작하므로). 사용자가 나중에 string key를 준다.

* `docs/image/`는 프로젝트마다 `.gitignore`에 등록한다 (사내 시안).
