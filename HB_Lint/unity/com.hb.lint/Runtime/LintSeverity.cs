namespace HBLint
{
    /// <summary>규칙 진단의 심각도. 값은 분석기 쪽 상수(LintSeverityValues)와 같아야 한다.</summary>
    public enum LintSeverity
    {
        Info = 1,
        Warning = 2,
        /// <summary>Unity · dotnet 빌드에서 컴파일을 막는다.</summary>
        Error = 3,
    }
}
