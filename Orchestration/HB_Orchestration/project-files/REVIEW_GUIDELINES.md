# 리뷰 지침 (Claude · 외부 에이전트 공통)

> 이 파일은 Claude(CLAUDE.md에서 참조)와 외부 에이전트(MCP 서버가 Unity 프로젝트 루트에서 자동으로 찾아 시스템 프롬프트에 주입) 양쪽이 같이 읽는다.
> 자주 하는 실수나 팀/개인 규칙이 생기면 여기에 추가할 것.

## Unity C#
- `Update`/`LateUpdate`/`FixedUpdate` 안의 힙 할당: LINQ, 람다 캡처, 문자열 연결, `new List<>`, 박싱
- `GetComponent`, `Find*`, `Camera.main`을 매 프레임 호출하지 않는다 → `Awake`에서 캐싱
- `[SerializeField] private` 우선, public 필드는 의도가 있을 때만
- 직렬화 필드 이름 변경 시 `[FormerlySerializedAs]` 누락 여부
- `UnityEngine.Object`에 `?.` / `??` 사용 금지 (파괴된 객체의 null 판정이 깨짐)
- 코루틴/async: 오브젝트 파괴·비활성화 후에도 계속 도는지, `CancellationToken`(`destroyCancellationToken`) 처리
- 이벤트 구독(`+=`)에 대응하는 해제(`-=`)가 `OnDisable`/`OnDestroy`에 있는지
- 메인 스레드 전용 API를 백그라운드 스레드에서 호출하지 않는지
- `Resources.Load` 남용, Addressables 핸들 `Release` 누락
- 에디터 전용 코드가 `#if UNITY_EDITOR` / Editor 어셈블리로 분리돼 있는지

## Unity 테스트 코드
- `LogAssert.ignoreFailingMessages`는 `[SetUp]`이 아니라 그게 필요한 테스트 본문에서 켠다 (의도한 예외 로그를 실패로 보지 않게 할 때)
- 테스트용 MonoBehaviour는 클래스 이름과 같은 이름의 파일에 둔다
- 확장 메서드는 그 namespace를 `using`하거나 정적 메서드로 직접 호출한다

## C++
- 소유권이 명확한지 (raw `new`/`delete` 대신 스마트 포인터, RAII)
- 댕글링 참조·이터레이터 무효화
- 핫 루프의 불필요한 복사 (`const&`, move)
- 정의되지 않은 동작: 부호 있는 오버플로, 초기화 안 된 변수, 경계 밖 접근

## 셰이더 (HLSL / ShaderLab)
- 프래그먼트 셰이더 내 분기·텍스처 샘플링 개수
- 정밀도(`half` vs `float`) — 모바일 대상이면 특히
- 키워드(multi_compile / shader_feature) 조합 폭발
- SRP Batcher 호환(CBUFFER 구성) 여부
- ※ 셰이더 성능 판단은 추정일 뿐, 프레임 디버거/프로파일러로 확인 필요하다고 명시할 것

## 공통
- 실패 경로: 예외, null, 빈 컬렉션, 0 나누기
- 이름이 동작을 정확히 설명하는지
- 테스트하기 어려운 구조(정적 상태, 숨은 의존성)

## Unity 공식 문서 참조

API나 기능이 확실하지 않을 때만 찾아본다. 기억으로 추측하지 않는다.

1. 어느 페이지인지 모르면 제품별 색인부터: `https://docs.unity.com/<제품>/llms.txt` (전체 목록은 `https://docs.unity.com/llms.txt`)
2. 페이지를 읽을 때는 URL 끝에 `.md`를 붙여 Markdown으로 가져온다. HTML 페이지는 가져오지 않는다.
3. 엔진 문서는 이 프로젝트의 Unity 버전(`ProjectSettings/ProjectVersion.txt`)에 맞는 버전 경로를 쓴다.
4. 페이지 전체를 요약하지 말고 필요한 항목만 확인한다.
