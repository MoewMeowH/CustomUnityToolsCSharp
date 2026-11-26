using System.Text.Json;
using HBLint.App.Workspace;

namespace HBLint.App;

internal sealed record RecentEntry(string Rules, string Out, string? Project, string? Csproj);

/// <summary>지금 연 규칙 폴더 · 대상 프로젝트와 최근 목록 (사용자 한 명 · 창 하나라 잠금 하나로 충분).</summary>
internal sealed class AppState(AppOptions options)
{
    private const int MaxRecent = 8;
    private readonly object _gate = new();
    private RuleFolder? _folder;
    private ProjectSession? _project;
    private string? _projectPath;
    private string? _csproj;

    public AppOptions Options { get; } = options;

    public RuleFolder? Folder
    {
        get { lock (_gate) return _folder; }
    }

    public ProjectSession? Project
    {
        get { lock (_gate) return _project; }
    }

    public void OpenFolder(RuleFolder folder)
    {
        lock (_gate)
        {
            _folder = folder;
            _project = null;
            _projectPath = null;
            _csproj = null;
            Remember();
        }
    }

    public void SetProject(string? path, string? csproj, ProjectSession? project)
    {
        lock (_gate)
        {
            _projectPath = path;
            _csproj = csproj;
            _project = project;
            Remember();
        }
    }

    private string RecentPath => Path.Combine(Options.DataFolder, "app.json");

    public IReadOnlyList<RecentEntry> Recent()
    {
        try
        {
            return File.Exists(RecentPath) ? JsonSerializer.Deserialize<List<RecentEntry>>(File.ReadAllText(RecentPath)) ?? [] : [];
        }
        catch (Exception e) when (e is JsonException || e is IOException || e is UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>최근 목록에서 그 규칙 폴더 항목을 뺀다 (지금 연 것이어도 목록에서만).</summary>
    public void Forget(string rules)
    {
        lock (_gate)
            Write(Recent().Where(r => !string.Equals(r.Rules, rules, StringComparison.OrdinalIgnoreCase)).ToList());
    }

    private void Remember()
    {
        if (_folder == null)
            return;
        var entry = new RecentEntry(_folder.Folder, _folder.Output, _projectPath, _csproj);
        Write(Recent().Where(r => !string.Equals(r.Rules, entry.Rules, StringComparison.OrdinalIgnoreCase)).Prepend(entry).Take(MaxRecent).ToList());
    }

    private void Write(List<RecentEntry> list)
    {
        try
        {
            Directory.CreateDirectory(Options.DataFolder);
            File.WriteAllText(RecentPath, JsonSerializer.Serialize(list));
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            // 최근 목록은 편의 — 못 써도 앱은 돈다
        }
    }
}
