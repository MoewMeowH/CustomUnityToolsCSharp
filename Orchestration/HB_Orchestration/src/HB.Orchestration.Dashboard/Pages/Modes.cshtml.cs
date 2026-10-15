using HB.Orchestration.Agents;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HB.Orchestration.Dashboard.Pages;

public sealed class ModesModel(ModesConfigService service) : PageModel
{
    public ModesSnapshot? Snapshot { get; private set; }
    public string? LoadError { get; private set; }
    public List<string> Errors { get; private set; } = [];
    public List<string> Warnings { get; private set; } = [];

    /// <summary>저장 직후 리다이렉트로 돌아왔을 때 무엇을 저장했는지 (modes / agents / replace).</summary>
    [BindProperty(SupportsGet = true)] public string? Saved { get; set; }

    /// <summary>저장 결과 안내. 리다이렉트 쿼리로 넘겨받는다.</summary>
    [BindProperty(SupportsGet = true)] public string? Note { get; set; }

    public void OnGet() => TryLoad();

    // ── 모드 저장 ────────────────────────────────────────────────────────────
    public IActionResult OnPost(string stamp)
    {
        TryLoad();
        if (Snapshot is null) return Page();

        var edits = new List<ModeEdit>();
        foreach (var (name, mode) in Snapshot.Typed.Modes)
        {
            var agents = new List<string>();
            for (var i = 0; i < mode.Steps.Count; i++)
                agents.Add(Request.Form[AgentField(name, i)].ToString());

            // 화면에 실제로 있었던 입력만 적용한다 (없는 항목은 파일 값 그대로).
            var settings = new Dictionary<string, string>();
            foreach (var key in ModesConfigService.EditableSettings)
            {
                if (Request.Form.TryGetValue(SettingField(name, key), out var value))
                    settings[key] = value.ToString();
            }

            edits.Add(new ModeEdit(name, agents, settings));
        }

        return Finish(() => service.Save(stamp ?? "", edits), "modes");
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
                Delete: F("del") == "true"));
        }

        return Finish(() => service.SaveAgents(settingsStamp ?? "", stamp ?? "", rows), "agents");
    }

    // ── 없는 에이전트 일괄 교체 ─────────────────────────────────────────────────
    public IActionResult OnPostReplace(string stamp, string from, string to)
    {
        TryLoad();
        if (Snapshot is null) return Page();
        return Finish(() => service.ReplaceAgent(stamp ?? "", from ?? "", to ?? ""), "replace");
    }

    private IActionResult Finish(Func<SaveResult> save, string what)
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
        return RedirectToPage(new { saved = what, note });
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
    }

    // ── 화면 도우미 ─────────────────────────────────────────────────────────

    public static string AgentField(string mode, int index) => $"agent|{mode}|{index}";
    public static string SettingField(string mode, string key) => $"set|{mode}|{key}";

    public string? Raw(string mode, string key) =>
        Snapshot is null ? null : ModesConfigService.GetRaw(Snapshot.Root, mode, key);

    public AgentInfo? FindAgent(string name) =>
        Snapshot?.Agents.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>이 에이전트를 쓰는 모드 이름들.</summary>
    public IEnumerable<string> UsedBy(string agent) =>
        Snapshot?.Typed.Modes
            .Where(m => m.Value.Steps.Any(s => string.Equals(s.Agent, agent, StringComparison.OrdinalIgnoreCase)))
            .Select(m => m.Key) ?? [];

    public string SavedMessage => Saved switch
    {
        "agents" => "에이전트를 저장했습니다. MCP 서버는 다음 호출 때 appsettings.json을 다시 읽습니다 (재시작 불필요).",
        "replace" => "모드의 에이전트를 바꿨습니다. 다음 run_mode부터 반영됩니다.",
        _ => "저장했습니다. MCP 서버는 다음 run_mode 실행부터 새 설정을 씁니다 (재시작 불필요).",
    };

    public static string FlowLabel(ModeFlow flow) => flow switch
    {
        ModeFlow.Sequential => "순차",
        ModeFlow.Concurrent => "동시",
        ModeFlow.Debate => "토론",
        _ => flow.ToString(),
    };

    public static string SeesLabel(StepSees sees) => sees switch
    {
        StepSees.Input => "요청만",
        StepSees.Previous => "앞 단계만",
        StepSees.InputAndPrevious => "요청 + 앞 단계",
        StepSees.All => "요청 + 앞 단계 전부",
        _ => sees.ToString(),
    };
}
