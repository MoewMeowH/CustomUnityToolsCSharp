# 작업 로그 (TestFramework, 임시)

> 대상: `UnityTestFrameWorkChecker/` (HB_TestFrameworkHelper).
> 임시 로그다. 기존 `Orchestration/HB_Orchestration/docs/worklog.md`는 건드리지 않는다. 작업이 끝나면 옮길지 정한다.

## 2026-10-22 HB_Build·TFH 네임스페이스 정리에 따른 분석기 점검 (작은 수정)
- 배경: ToolCreate 세션(HB_Build)이 HB_Build를 `HB.CustomBuild.Editor`로, `NeedSlotNullCheckAttribute`를 `HB.TestFrameworkHelper`로 옮긴다 (그쪽 사용자 결정). ToolCreate 쪽 TFH 두 파일도 그 세션이 고친다.
- 점검 결과 (확정):
  - 분석기는 특성을 `AttributeClass.Name`으로만 찾는다 (`Analyzers/HBNeedSlotNullCheckSerializeFieldAnalyzer.cs:67`) → 네임스페이스를 옮겨도 분석기 코드는 고칠 필요 없음.
  - 분석기 프로젝트에 테스트 프로젝트가 없어 전역 정의를 가정한 테스트 데이터가 없음.
  - ToolCreate `Plugins/HB_TestFrameworkHelper.dll` = 이 저장소 Release 빌드 결과 (SHA-256 일치, 빌드 01:31:56가 마지막 소스 수정 01:31:55 뒤).
  - ToolCreate `Assets/Scripts/NewMonoBehaviourScript.cs`가 `[NeedSlotNullCheck]`를 using 없이 씀 → 옮기면 CS0246. ToolCreate 세션에 알림 (그쪽 커밋에 포함 요청).
- 수정: `UnityTestFrameWorkChecker/README.md` 어트리뷰트 예시를 `namespace HB.TestFrameworkHelper`로 감싸고 using 안내 추가. 커밋 전.
