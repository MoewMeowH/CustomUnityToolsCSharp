// 손으로 쓴 규칙 예시. 이 파일을 규칙 어셈블리(예: Unity asmdef "Game.LintRules")에 둔다.
// 이 어셈블리를 참조하는 어셈블리가 검사된다 (Unity에서 autoReferenced면 Assembly-CSharp는 자동).

[assembly: HBLint.BanNullOperatorRule("UnityEngine.Object",
    "Unity 오브젝트는 ?. · ?? · ??= · is null이 파괴된 오브젝트를 null로 보지 않습니다. '== null' 또는 'if (obj)'로 확인하세요.",
    Requires = new[] { "UnityEngine.CoreModule" })]
