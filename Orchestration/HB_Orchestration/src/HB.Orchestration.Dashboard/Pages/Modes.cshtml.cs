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

    /// <summary>저장 직후 리다이렉트로 돌아왔을 때 true.</summary>
    [BindProperty(SupportsGet = true)] public bool Saved { get; set; }

    public void OnGet() => TryLoad();

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

        SaveResult result;
        try
        {
            result = service.Save(stamp ?? "", edits);
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

        // 경고는 리다이렉트 후에도 보이도록 쿼리로 넘기지 않고, 다시 읽은 화면에서 재계산되게 둔다.
        return RedirectToPage(new { saved = true });
    }

    private void TryLoad()
    {
        try
        {
            Snapshot = service.Load();
            if (Errors.Count == 0)
            {
                var json = Snapshot.Root.ToJsonString();
                Warnings = ModesConfigService.Validate(json, Snapshot.Agents).Warnings;
            }
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
