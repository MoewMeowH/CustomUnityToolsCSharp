using HB.Orchestration.Agents;
using HB.Orchestration.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HB.Orchestration.Dashboard.Pages;

/// <summary>모델 단가 표의 한 줄: 설정·에이전트·호출 기록 어디서든 나온 모델.</summary>
public sealed record PriceRow(string Model, ModelPrice? Price, IReadOnlyList<string> Agents, int Calls, DateTimeOffset? LastUsed);

public sealed class ModesModel(ModesConfigService service, CallLogCache log) : PageModel
{
    public ModesSnapshot? Snapshot { get; private set; }
    public string? LoadError { get; private set; }
    public List<string> Errors { get; private set; } = [];
    public List<string> Warnings { get; private set; } = [];

    /// <summary>최근 7일 호출이 실제로 쓴 modes.json (provenance). 편집 중인 파일과 다른 것은 <see cref="OtherModes"/>.</summary>
    public List<ModesFileUse> RecentModes { get; private set; } = [];
    public List<ModesFileUse> OtherModes { get; private set; } = [];

    /// <summary>최근 7일 호출이 붙인 리뷰 지침 (provenance). 경고는 <see cref="GuidelinesFileUse.Warning"/>가 있는 것.</summary>
    public List<GuidelinesFileUse> RecentGuidelines { get; private set; } = [];
    public List<MissingGuidelinesUse> MissingGuidelines { get; private set; } = [];

    /// <summary>저장 직후 리다이렉트로 돌아왔을 때 무엇을 저장했는지 (modes / agents / replace).</summary>
    [BindProperty(SupportsGet = true)] public string? Saved { get; set; }

    /// <summary>저장 결과 안내. 리다이렉트 쿼리로 넘겨받는다.</summary>
    [BindProperty(SupportsGet = true)] public string? Note { get; set; }

    /// <summary>편집기에서 고른 모드 (저장 뒤 같은 모드를 다시 연다).</summary>
    [BindProperty(SupportsGet = true)] public string? Mode { get; set; }

    /// <summary>저장이 거절됐을 때 화면에 되돌려 줄 편집 내용 (편집을 이어 가게).</summary>
    public EditorDraft? Draft { get; private set; }

    /// <summary>그래프 편집기 자료 (페이지 안 JSON 블록).</summary>
    public string EditorJson { get; private set; } = "{}";

    private string? _postedStamp;

    /// <summary>
    /// 편집기 폼의 스탬프. 저장이 거절돼 고치던 내용을 되돌려 줄 때는 화면이 보낸 스탬프를 그대로 둔다 —
    /// "다른 곳에서 바뀜"으로 거절됐는데 새 스탬프를 주면, 다시 누르기만 해도 그 사이 변경을 덮는다.
    /// </summary>
    public string EditorStamp => Errors.Count > 0 && _postedStamp is not null ? _postedStamp : Snapshot?.Stamp ?? "";

    public void OnGet() => TryLoad();

    // ── 그래프 편집기: 모드 · 역할을 하나씩 저장 ───────────────────────────────
    public IActionResult OnPostMode(string stamp, string? originalName, string name, string modeJson)
    {
        _postedStamp = stamp;
        Draft = new EditorDraft("mode", name ?? "", originalName, modeJson ?? "");
        TryLoad();
        if (Snapshot is null) return Page();
        return Finish(() => service.SaveMode(stamp ?? "", originalName, name ?? "", modeJson ?? ""), "mode", name);
    }

    public IActionResult OnPostDeleteMode(string stamp, string name)
    {
        _postedStamp = stamp;
        TryLoad();
        if (Snapshot is null) return Page();
        return Finish(() => service.DeleteMode(stamp ?? "", name ?? ""), "delete-mode");
    }

    public IActionResult OnPostRole(string stamp, string name, string roleJson, bool isNew)
    {
        _postedStamp = stamp;
        // 되돌려 줄 내용의 OriginalName: 새 역할이면 null (화면이 "새 역할"로 다시 연다)
        Draft = new EditorDraft("role", name ?? "", isNew ? null : name, roleJson ?? "");
        TryLoad();
        if (Snapshot is null) return Page();
        return Finish(() => service.SaveRole(stamp ?? "", name ?? "", roleJson ?? "", isNew), "role", Mode);
    }

    public IActionResult OnPostDeleteRole(string stamp, string name)
    {
        _postedStamp = stamp;
        TryLoad();
        if (Snapshot is null) return Page();
        return Finish(() => service.DeleteRole(stamp ?? "", name ?? ""), "delete-role", Mode);
    }

    // ── 에이전트 저장 ────────────────────────────────────────────────────────
    public IActionResult OnPostAgents(string settingsStamp, string stamp, int rowCount)
    {
        TryLoad();
        if (Snapshot is null) return Page();

        var rows = new List<AgentEdit>();
        for (var i = 0; i < rowCount; i++)
        {
            string F(string field) => Request.Form[$"ag.{field}.{i}"].ToString();
            rows.Add(new AgentEdit(
                OriginalName: F("orig"),
                Name: F("name"),
                Provider: F("provider"),
                Model: F("model"),
                MaxOutputTokens: F("max"),
                Enabled: F("enabled") == "true",
                Delete: F("del") == "true",
                TimeoutSeconds: F("timeout"),
                ReasoningEffort: F("effort")));
        }

        return Finish(() => service.SaveAgents(settingsStamp ?? "", stamp ?? "", rows), "agents");
    }

    // ── 모델 단가 저장 ──────────────────────────────────────────────────────
    public IActionResult OnPostPrices(string settingsStamp, int rowCount, string? krwPerUsd)
    {
        TryLoad();
        if (Snapshot is null) return Page();

        var rows = new List<PriceEdit>();
        for (var i = 0; i < rowCount; i++)
        {
            string F(string field) => Request.Form[$"pr.{field}.{i}"].ToString();
            rows.Add(new PriceEdit(F("model"), F("in"), F("cache"), F("out")));
        }

        return Finish(() => service.SavePrices(settingsStamp ?? "", rows, krwPerUsd ?? ""), "prices");
    }

    // ── 없는 에이전트 일괄 교체 ─────────────────────────────────────────────────
    public IActionResult OnPostReplace(string stamp, string from, string to)
    {
        TryLoad();
        if (Snapshot is null) return Page();
        return Finish(() => service.ReplaceAgent(stamp ?? "", from ?? "", to ?? ""), "replace");
    }

    /// <param name="mode">편집기에서 다시 열 모드 (편집기 저장만).</param>
    private IActionResult Finish(Func<SaveResult> save, string what, string? mode = null)
    {
        SaveResult result;
        try
        {
            result = save();
        }
        catch (Exception ex) when (ex is ModesConfigException or IOException or UnauthorizedAccessException)
        {
            Errors = [ex.Message];
            return Page();
        }

        if (!result.Ok)
        {
            Errors = result.Errors;
            Warnings = result.Warnings;
            return Page();
        }

        // 새로고침해도 다시 저장되지 않도록 GET으로 돌려보낸다. 경고는 다시 읽은 화면에서 재계산된다.
        var note = result.Notes.Count > 0 ? string.Join(" · ", result.Notes) : null;
        return RedirectToPage(new { saved = what, note, mode });
    }

    private void TryLoad()
    {
        try
        {
            Snapshot = service.Load();
            if (Errors.Count == 0)
                Warnings = ModesConfigService.Validate(Snapshot.Root.ToJsonString(), Snapshot.Agents).Warnings;
        }
        catch (Exception ex) when (ex is ModesConfigException or IOException or UnauthorizedAccessException)
        {
            LoadError = ex.Message;
        }

        // 최근 사용 modes.json · 리뷰 지침은 부가 정보: 호출 기록을 못 읽어도 편집 화면은 그대로 둔다.
        // 리뷰 지침은 편집 중인 modes.json과 무관하므로 modes.json을 못 읽어도 계산한다.
        IReadOnlyList<CallRecord> calls = [];
        var now = DateTimeOffset.Now;
        try
        {
            calls = log.All; // 한 번만 받는다 — CallLogCache는 파일이 바뀌면 다시 읽어 세 집계가 서로 다른 목록을 볼 수 있다
            RecentGuidelines = GuidelinesUsage.Recent(calls, now, ModesUsage.Window);
            MissingGuidelines = GuidelinesUsage.Missing(calls, now, ModesUsage.Window);
            if (Snapshot is not null)
            {
                RecentModes = ModesUsage.Recent(calls, now, ModesUsage.Window);
                OtherModes = ModesUsage.OtherFiles(RecentModes, Snapshot.ModesPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warnings.Add($"최근 실행이 쓴 modes.json · 리뷰 지침을 확인하지 못했습니다 (호출 기록 읽기 실패: {ex.GetType().Name}: {ex.Message}).");
        }

        // 호출 기록을 못 읽었으면 최근 실행 수만 0으로 보인다.
        if (Snapshot is not null)
            EditorJson = ModeEditorData.Build(Snapshot, calls, now, HasModesSource, Mode, Draft);
    }

    // ── 화면 도우미 ─────────────────────────────────────────────────────────

    /// <summary>내용 해시 앞 12자리 (지침 목록용).</summary>
    public static string ShortSha(string? sha) => sha is { Length: > 12 } ? sha[..12] + "…" : sha ?? "해시 없음"; // modes 이전 내용 목록과 같은 표기

    /// <summary>저장하면 git 원본에도 적용되는가 (편집기 안내용).</summary>
    public bool HasModesSource => Snapshot is not null && service.WritesSource(Snapshot);

    /// <summary>저장은 됐지만 git 원본 반영이 실패한 안내인가 (화면이 경고로 보인다).</summary>
    public bool NoteIsWarning => Note?.Contains(ModesConfigService.SourceFailedPrefix, StringComparison.Ordinal) == true;

    /// <summary>
    /// 단가 표에 보일 모델: 설정에 단가가 있는 모델 + 켜진 에이전트가 쓰는 모델 + 호출 기록에 나온 모델.
    /// 현재 에이전트가 쓰는 모델 → 단가 없는 모델 → 나머지 순.
    /// </summary>
    public List<PriceRow> PriceRows()
    {
        if (Snapshot is null) return [];

        var calls = new Dictionary<string, (int Count, DateTimeOffset Last)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var c in log.All)
            {
                calls.TryGetValue(c.Model, out var x);
                calls[c.Model] = (x.Count + 1, c.Timestamp > x.Last ? c.Timestamp : x.Last);
            }
        }
        catch (IOException)
        {
            // 기록을 못 읽어도 설정에 있는 모델은 보여준다
        }

        var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        models.UnionWith(Snapshot.Pricing.Models.Keys);
        // 꺼진 에이전트(예: 모델 칸이 자리표시인 로컬)의 모델은 단가나 기록이 있을 때만 (위·아래 두 줄로 이미 포함됨)
        models.UnionWith(Snapshot.Agents.Where(a => a.Enabled).Select(a => a.Model).Where(m => m.Length > 0));
        models.UnionWith(calls.Keys);

        return models
            .Select(m => new PriceRow(
                m,
                Snapshot.Pricing.Models.TryGetValue(m, out var p) ? p : null,
                Snapshot.Agents.Where(a => string.Equals(a.Model, m, StringComparison.OrdinalIgnoreCase)).Select(a => a.Name).ToList(),
                calls.TryGetValue(m, out var c) ? c.Count : 0,
                calls.TryGetValue(m, out var c2) ? c2.Last : null))
            .OrderBy(r => r.Agents.Count > 0 ? 0 : 1)
            .ThenBy(r => r.Price is null ? 0 : 1)
            .ThenBy(r => r.Model, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public AgentInfo? FindAgent(string name) =>
        Snapshot?.Agents.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>이 에이전트를 쓰는 모드 이름들.</summary>
    public IEnumerable<string> UsedBy(string agent) =>
        Snapshot?.Typed.Modes
            .Where(m => m.Value.Steps.Any(s => string.Equals(s.Agent, agent, StringComparison.OrdinalIgnoreCase)))
            .Select(m => m.Key) ?? [];

    /// <summary>타임아웃 칸이 비었을 때 실제로 적용되는 값 (제공사 값 → SDK 기본값).</summary>
    public string TimeoutPlaceholder(AgentInfo? agent)
    {
        if (agent is not null && Snapshot?.ProviderTimeouts.GetValueOrDefault(agent.Provider) is > 0 and var p)
            return $"제공사 {p}";
        return $"기본 {AgentSettings.SdkDefaultTimeout.TotalSeconds:0}";
    }

    public string SavedMessage => Saved switch
    {
        "agents" => "에이전트를 저장했습니다. MCP 서버는 다음 호출 때 appsettings.json을 다시 읽습니다 (재시작 불필요).",
        "prices" => "모델 단가와 환율을 저장했습니다. 호출 기록 화면의 비용에 바로 반영됩니다.",
        "replace" => "모드의 에이전트를 바꿨습니다. 다음 run_mode부터 반영됩니다.",
        "mode" => "모드를 저장했습니다. 다음 run_mode부터 반영됩니다 (재시작 불필요).",
        "delete-mode" => "모드를 지웠습니다.",
        "role" => "역할을 저장했습니다. 다음 run_mode부터 반영됩니다 (재시작 불필요).",
        "delete-role" => "역할을 지웠습니다.",
        _ => "저장했습니다. MCP 서버는 다음 run_mode 실행부터 새 설정을 씁니다 (재시작 불필요).",
    };
}
