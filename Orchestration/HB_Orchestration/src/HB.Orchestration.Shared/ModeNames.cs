namespace HB.Orchestration.Shared;

/// <summary>
/// 2026-10-20 모드 이름 통일 (계획서 2026-10-20_모드이름-통일-세션한도-제거).
/// 고를 수 있는 모드는 {종류}_{등급}(lite · main · high), 단계 모드는 {종류}_{단계}.
/// 기록 파일(calls.jsonl · 세션 기록 · 골든 사례)은 다시 쓰지 않고, 읽을 때만 새 이름으로 바꿔 읽는다.
/// run_mode에 예전 이름이 오면 실행하지 않고 새 이름을 알려 준다 (<see cref="RenamedHint"/>).
/// </summary>
public static class ModeNames
{
    /// <summary>예전 이름 → 새 이름. perf_astra는 모드가 없어져 바꾸지 않는다 (기록에는 예전 이름 그대로).</summary>
    public static readonly IReadOnlyDictionary<string, string> Renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["review"] = "review_main",
        ["panel"] = "review_high",
        ["perf_sol"] = "perf_main",
        ["perf_argon"] = "perf_high",
        ["critique"] = "design_lite",
        ["debate"] = "design_main",
        ["deliberate"] = "design_high",
        ["research"] = "research_main",
        ["parallel"] = "parallel_main",
        ["work_order"] = "work_order_main",
        ["crash"] = "crash_main",
        ["crash_review"] = "crash_check",
    };

    /// <summary>예전 이름이면 새 이름, 아니면 그대로 (null은 null).</summary>
    public static string? Normalize(string? mode) =>
        mode is not null && Renamed.TryGetValue(mode, out var renamed) ? renamed : mode;

    /// <summary>기록 한 줄의 Mode · Tool을 새 이름으로. 바꿀 것이 없으면 같은 인스턴스.</summary>
    public static CallRecord Normalize(CallRecord r)
    {
        var mode = Normalize(r.Mode);
        var tool = Normalize(r.Tool)!;
        return ReferenceEquals(mode, r.Mode) && ReferenceEquals(tool, r.Tool) ? r : r with { Mode = mode, Tool = tool };
    }

    /// <summary>예전 이름이면 run_mode 거절 문구에 붙일 안내, 아니면 null.</summary>
    public static string? RenamedHint(string mode) =>
        Renamed.TryGetValue(mode, out var renamed) ? $"'{mode}'은(는) '{renamed}'(으)로 이름이 바뀌었습니다." : null;
}
