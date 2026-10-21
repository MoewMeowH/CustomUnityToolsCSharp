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
- 수정: `UnityTestFrameWorkChecker/README.md` 어트리뷰트 예시를 `namespace HB.TestFrameworkHelper`로 감싸고 using 안내 추가. 커밋 `ba5a53d` (push 안 함).

## 2026-10-22 E2String 빌드 테스트 협의 (ToolCreate 세션과)
- ToolCreate 세션이 노션 E2String 빌드 항목(A IL2CPP 크기 재측정 · B Stripping Medium/High · C Benchmark.md 문구 · D 빌드 폴더 정리)을 같이 진행하자고 함. HB_Build 네임스페이스 작업은 ToolCreate `2f327fa`로 push됨.
- 확인 (확정): `EString/Script/bin/Release/netstandard2.0/EString.dll` md5 `8f4ba2ef…` = ToolCreate `HB_E2String/Plugins/EString.dll`. DLL보다 나중에 고친 .cs 없음, 작업 트리 깨끗 (마지막 변경 `59c9d0b`). 최신 소스로 빌드됐다는 건 파일 시각으로 본 추정 (다시 빌드해 비교하지 않음).
- 분담: 빌드·측정·문서 반영은 ToolCreate 세션, 이쪽은 DLL 동일성 확인과 결과 해석.
- 사용자 확인 필요: 측정을 ToolCreate(기존 기준과 같은 7.0a8, 에디터 닫아야 함)에서 할지 — 배치는 ForAI라는 규칙의 예외. 이쪽 권고는 ToolCreate. D 삭제는 A·B 뒤 확인.
- 사용자 답 (원문): "1. 이건 저쪽 세션에 답줬어 예외로 할거야 ToolCreate 에서 할거얌 2. 노션에 따로 없엉 3. 알겠엉!"
  → 측정은 ToolCreate(7.0a8)에서 예외로 진행 (ToolCreate 세션이 시작함: A는 Low 5 variants, B는 Medium·High 각 3 variants × 3회). 측정 전에 반영할 생성기 변경 없음 → 현재 DLL `8f4ba2ef…`로 측정. D는 A·B 뒤 다시 확인.

## 2026-10-22 E2String IL2CPP 크기 측정 해석 (ToolCreate 세션 측정값)
- 측정: ToolCreate 7.0a8, Release, Win64, 100멤버 enum 50개, DLL `8f4ba2ef…`. 치트 define 3개를 측정 동안만 끔 (그쪽 사용자 결정). Low 미사용 +11.1 KB/enum (이전 18.9), Medium·High 미사용 +0, 사용 +15.3 KB.
- 해석 (확정 = 빌드 산출물 확인):
  - 7e014a6(이전 측정 DLL) 이후 출력이 바뀐 건 3d2778d뿐. [Flags]가 아닌 enum에서 TryMatchName(길이 switch + string.Compare 100개)이 사라짐 → GameAssembly −7.8 KB/enum, metadata −83 B/enum. 다른 커밋은 표기만 바뀜 (IL 비교는 안 함 = 추정).
  - global-metadata.dat 이름 검색: Low None에는 enum 50·멤버 5,000이 있음. Medium/High None·Unused에는 enum·Extensions 모두 0 → 통째로 제거. Used의 metadata +218 KB에는 enum 자체가 포함됨 → Medium/High "사용" 증가량은 E2String만의 비용이 아님.
  - High > Medium 기준선: 스트리핑 뒤 IL 비교로 CoreLib +249 KB(Tracing 71·Reflection.Emit 49 타입), Regex +72 KB(RegexCompiler). 원인은 기능 스위치 처리 차이로 추정, 미확인. E2String 증가량에는 영향 없음.
- 도구: scratchpad의 typediff (System.Reflection.Metadata로 타입 목록 비교). ToolCreate에는 쓰지 않음.
- 남은 것: Benchmark.md·README 반영(ToolCreate 세션), None_Used 변형 추가 여부(사용자), D 빌드 폴더 삭제(사용자 확인).

## 2026-10-22 E2String 기존 벤치 재측정 범위
- 판단 (확정 = 생성기 diff와 Benchmark.md 대조): 이전 측정 DLL(7e014a6, `47b624f2…`) 이후 String → Enum 생성 코드(ignoreCase 분기 제거)와 6-2(IIncrementalGenerator 전환 + IL 감소)가 바뀜. ToStringFast·IsDefinedFast·Values·[Flags] 출력은 표기만 바뀜 (IL 같다고 추정). 6-2의 "ISourceGenerator(비증분)" 문장은 지금 틀림.
- 사용자 답 (원문): "응 그러자" (None_Used 추가), "1. 보류 유지하자 2. 응 그렇게 하자 저쪽이 Unity 들고있어서"
  → IL2CPP 속도·할당 전체, 6-2, None_Used(Medium·High)를 ToolCreate 세션이 다시 잼. CoreCLR은 보류하고 옛 DLL 기준이라고 표시. 해석은 이쪽. 빌드 폴더 정리는 모든 측정 뒤 확인.

## 2026-10-22 6-2 C# 컴파일 크기·시간 측정 (이쪽 담당) · Android 보류
- 사용자 답 (원문): "1. 응 그렇게 하자, 2. 그건 보류 지금 시간이 늦어서 노션 갱신만 부탁할게 그건"
  → 6-2는 이쪽에서 잼. 노션 "Android ARM64 IL2CPP 측정"은 상태를 보류로 바꾸고 조건·메모에 방식(크기는 기기 없이, 속도는 adb) 기록.
- 방법: Unity 7.0a8에 들어 있는 Roslyn 5.6을 하네스에서 API로 호출 (파싱 → 생성기 → Emit). 입력은 ToolCreate HEAD의 SizeProbe와 attribute, 참조는 netstandard 2.1과 Unity.Scripting, C# 9 · Release. 옛 DLL `47b624f2…`(7e014a6)과 새 DLL `cf2220a5…`(59c9d0b), 예열 10회 + 20회, 3라운드 번갈아. 하네스는 scratchpad `csc62/`.
- IL 크기 (확정, 3라운드 동일): 없음 115,200 B / [E2String] 옛 610,816(+9.7 KB/enum) → 새 478,720(+7.1 KB) / All 옛 623,104(+9.9 KB) → 새 491,520(+7.3 KB). ignoreCase 제거로 −2.6 KB/enum. 10-15 문서 값(+7.6 KB)과는 측정 방법이 달라 절대값이 다름.
- 컴파일 시간: ToolCreate IL2CPP 빌드와 동시에 돌아 2~3배 흔들림 → 무효. 그쪽 측정이 끝난 뒤 다시 잼.

## 2026-10-22 Benchmark.md 덮어쓰기 결정
- 사용자 답 (원문): "이 벤치 결과를 현재 벤치결과에 덮어써줘, 실제 릴리즈랑 공유를 아예 안했기 때문에 코드도 수정했겠다 지난 벤치는 없애도 됨", "지우자, 그냥 벤치쪽에 CoreCLR 은 정식 출시 이후 예정 으로 하장"
  → 지난 벤치(10-15) 비교·옛 DLL 표기 삭제, 6-2는 새 DLL 값만, CoreCLR 열·그래프 삭제 + "정식 출시 이후 예정" 안내. 반영은 ToolCreate 세션이 측정 뒤 한 번에.
- 6-2 시간 재측정 (ToolCreate 측정이 끝난 뒤, 새 DLL만, 5라운드): 없음 26 ms / [E2String] 338 ms (+312, 범위 319~370) / All 433 ms (+407, 범위 426~482). 생성기 실행만 24~27 ms. 크기는 앞과 같음. ToolCreate 세션에 표·방법 문구 전달.

## 2026-10-22 IL2CPP 플레이어 재측정 해석 (ToolCreate 세션 측정값)
- 결과 요약: ToString 계열 ×4~12, String → Enum ×38~45, 정의 검사·순회 0.7 ns 안팎. [Flags] 첫 호출 270 → 876 ns. 실행 간 차이는 중앙 0.7%. 문서는 그쪽에서 새 값으로 덮어씀.
- None_Used (확정, 크기 차): None_Used − None = metadata +137 KB (enum 자체 몫, 앞 추정 136 KB와 일치). Default_Used − None_Used = Enum당 14.5 KB (Low 14.7 KB와 비슷).
- 그쪽에 보낸 수정 요청:
  - "쓰는 Enum의 E2String 비용 14.5 KB"는 생성 코드(약 11 KB, Low 미사용 값)와 호출 코드 차이(약 3.4 KB)가 합쳐진 값이라 나눠 쓰기.
  - 1 ns 미만 배수(×190·×250)는 측정 루프 비용 수준이라 참고용으로 표시하기.
  - [Flags] 첫 호출 "5~9배"를 "약 3배"로 고치기.
- String → Enum E2String 쪽 ±2 ns: ignoreCase=false일 때 옛 코드와 같은 switch라 실제 차이는 없다고 봄. "기존" 쪽도 5~18% 바뀌어 날짜가 다른 측정은 비교 불가.
- 사용자에게 물을 것: 부록 A(Unity 6.x Mono)·B(Dev 시간)·C(에디터) 처리.

## 2026-10-22 E2String 벤치 마무리 (ToolCreate 세션)
- ToolCreate 세션이 마무리: 부록 A(Mono 6.x)는 그쪽 사용자 결정으로 삭제("Mono도 이제 안 쓰니까"), 부록 번호 재정리, 커밋 `3640e51` push (ToolCreate main). `Builds/E2StringBenchmark_*` 10개(약 26 GB) 삭제, 노션 "빌드 폴더 삭제" 완료 처리.
- 이쪽: 문서 수정 요청 3건이 모두 반영됨을 확인. 6-2 하네스는 scratchpad에만 있음 (저장소에 넣지 않음).
