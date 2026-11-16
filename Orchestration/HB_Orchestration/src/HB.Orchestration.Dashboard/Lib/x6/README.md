# AntV X6 (모드 화면의 그래프 편집기가 쓰는 라이브러리)

- 판: `@antv/x6` 3.1.8 — MIT (`LICENSE`), Copyright (c) 2021-2025 Alipay.inc
- 파일: `x6.min.js` = 패키지의 `dist/x6.min.js` 그대로 (583,499바이트, UMD · 전역 이름 `X6`, 의존성이 묶여 있다)
- 받은 방법: `npm pack @antv/x6@3.1.8` → tarball 안의 `package/dist/x6.min.js` · `package/LICENSE`
  - tarball SHA-256: `e690310b9b82774cbd824a76667ac506fbfbdb4ff78f5206cafec23882dc004a`
  - `x6.min.js` SHA-256: `0cda942c20b32e3e2b3b8ff11560aefda39008a8c00d43c75295e4a5543593b0`
- 싣는 방식: 대시보드 어셈블리의 내장 리소스 → `/lib/x6.min.js` (`LibFiles.cs`). 대시보드에는 정적 파일 서빙이 없다.
- 판을 올릴 때: 같은 방법으로 받아 이 파일의 판 · 해시를 고치고, 모드 화면에서 선 잇기 · 끊기 · 노드 안 입력칸을 다시 확인한다.
