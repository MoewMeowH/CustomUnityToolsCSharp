using System;

namespace HBLint
{
    /// <summary>
    /// 규칙 어트리뷰트 생성자 매개변수에 붙인다: 이 값(또는 배열의 각 항목)은 "타입 메타데이터 이름.멤버 이름"이다 (예 "UnityEngine.Component.GetComponent").
    /// Unity 생성 단계가 그 타입과 멤버가 있는지 확인하고, 없으면 rules.json 줄과 함께 오류로 막는다.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    public sealed class MemberNameAttribute : Attribute
    {
    }
}
