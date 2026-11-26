using HBLint.App.Workspace;
using HBLint.Core.Generation;
using HBLint.Core.Ir;
using HBLint.Core.Json;

namespace HBLint.App;

internal sealed record WorkspaceRequest(string? Rules, string? Out);

internal sealed record ProjectRequest(string? Path, string? Csproj);

internal sealed record UnityRequest(string? Path, bool? Setup, bool? Probe);

internal sealed record SettingsRequest(string[]? IgnoreAssemblies, string[]? IgnorePaths);

internal sealed record ForgetRequest(string? Rules);

internal sealed record IrErrorDto(string Message, int Line, int Column, bool Warning, string? Node);

/// <summary>/api — 모두 토큰이 있어야 닿는다 (LocalGuard). 규칙 본문은 글 그대로 주고받는다 (키 순서를 지키려고 Core JSON으로 읽고 씀).</summary>
internal static class Api
{
    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/catalog", () => Results.Text(CatalogJson.Build().ToJsonString(), "application/json"));

        api.MapGet("/state", (AppState state) => Results.Ok(StateDto(state)));

        api.MapPost("/workspace", (WorkspaceRequest request, AppState state) =>
        {
            var rules = CleanPath(request.Rules);
            var output = CleanPath(request.Out);
            if (rules == null || output == null)
                return Error("규칙 폴더와 생성물(LintRules.g.cs) 경로가 있어야 합니다");
            if (!Directory.Exists(rules))
                return Error($"{rules}: 폴더가 없습니다");
            if (!output.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                return Error("생성물은 .cs 파일이어야 합니다");
            state.OpenFolder(new RuleFolder(rules, output));
            return Results.Ok(StateDto(state));
        });

        // Unity 프로젝트 폴더 하나로 규칙 폴더 · 생성물을 정한다. 처음이면 needsSetup으로 묻고, setup: true면 init처럼 준비한 뒤 연다.
        // probe: true는 자리만 알려 주고 아무것도 열거나 바꾸지 않는다 (최근 목록이 Unity 열기로 갈지 볼 때).
        api.MapPost("/unity", (UnityRequest request, AppState state) =>
        {
            var path = CleanPath(request.Path);
            if (path == null)
                return Error("Unity 프로젝트 폴더가 있어야 합니다");
            try
            {
                if (UnityProject.Find(path) is not { } layout)
                    return Error($"{path}: Unity 프로젝트 폴더가 아닙니다 (Assets · ProjectSettings가 있는 폴더)");
                if (request.Probe == true || (layout.NeedsSetup && request.Setup != true))
                    return Results.Ok(new { layout.NeedsSetup, layout.Rules, @out = layout.Output, layout.HasPackage });
                var created = layout.NeedsSetup ? UnityProject.Setup(layout) : [];
                state.OpenFolder(new RuleFolder(layout.Rules, layout.Output));
                // 대상 프로젝트 읽기가 실패해도(csproj 없음 등) 최근 목록에 프로젝트가 남게
                state.SetProject(layout.Project, null, null);
                return Results.Ok(new { needsSetup = false, created, state = StateDto(state) });
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
            {
                return Error(e.Message);
            }
        });

        api.MapGet("/rules", (AppState state) =>
        {
            if (state.Folder is not { } folder)
                return NoFolder();
            var rules = folder.List();
            return Results.Ok(new { rules, nextId = folder.NextId(rules) });
        });

        api.MapGet("/rules/{file}", (string file, AppState state) =>
        {
            if (state.Folder is not { } folder)
                return NoFolder();
            if (folder.PathOf(file) is not { } path)
                return Error("규칙 파일 이름이 아닙니다 (폴더 바로 아래의 *.hblint.json)");
            if (!File.Exists(path))
                return Results.NotFound();
            var text = File.ReadAllText(path);
            return Results.Ok(new { file, text, validation = Validate(text, state) });
        });

        // PUT = 있는 파일 고치기, POST = 새 파일 (있으면 409 — 다른 규칙을 덮지 않게)
        api.MapPut("/rules/{file}", (string file, HttpRequest request, AppState state) => Save(file, request, state, create: false));
        api.MapPost("/rules/{file}", (string file, HttpRequest request, AppState state) => Save(file, request, state, create: true));

        api.MapPost("/validate", async (HttpRequest request, AppState state) =>
            Results.Ok(Validate(await ReadBody(request), state)));

        api.MapGet("/settings", (AppState state) =>
            state.Folder is { } folder ? Results.Ok(SettingsDto(folder)) : NoFolder());

        // 공통 설정 쓰기 — 규칙 폴더 바로 아래 hblint.settings.json 하나만. 검증에 걸리면 쓰지 않는다
        api.MapPut("/settings", (SettingsRequest request, AppState state) =>
        {
            if (state.Folder is not { } folder)
                return NoFolder();
            var settings = new RuleSettings(Clean(request.IgnoreAssemblies), Clean(request.IgnorePaths));
            var (parsed, errors) = RuleSettings.Parse(settings.ToJson());
            if (parsed == null)
                return Results.BadRequest(new { error = string.Join("\n", errors.Select(e => e.Message)) });
            if (!folder.WriteSettings(parsed))
                return Error($"{RuleSettings.FileName}이(가) 규칙 폴더 밖을 가리키는 링크라 쓰지 않았습니다");
            return Results.Ok(SettingsDto(folder));
        });

        api.MapPost("/recent/forget", (ForgetRequest request, AppState state) =>
        {
            if (string.IsNullOrWhiteSpace(request.Rules))
                return Error("지울 항목이 없습니다");
            state.Forget(request.Rules);
            return Results.Ok(StateDto(state));
        });

        api.MapPost("/generate", (AppState state) =>
        {
            if (state.Folder is not { } folder)
                return NoFolder();
            var (set, refused) = folder.Generate();
            IReadOnlyList<string> errors = refused != null ? [.. set.Errors, refused] : set.Errors;
            return Results.Ok(new { ok = set.Text != null && refused == null, output = folder.Output, count = set.Count, errors, warnings = set.Warnings });
        });

        api.MapPost("/project", async (ProjectRequest request, AppState state, CancellationToken ct) =>
        {
            if (state.Folder == null)
                return NoFolder();
            var path = CleanPath(request.Path);
            if (path == null)
            {
                state.SetProject(null, null, null);
                return Results.Ok(StateDto(state));
            }
            try
            {
                var csproj = ProjectLoader.ResolveCsproj(path, request.Csproj);
                var session = await Task.Run(() => ProjectSession.Load(csproj, ct), ct);
                state.SetProject(path, request.Csproj, session);
                return Results.Ok(StateDto(state));
            }
            catch (ProjectLoadException e)
            {
                // 고를 csproj가 여럿이면 화면이 고르기 칸을 띄우게 후보를 같이 준다
                return Results.BadRequest(new { error = e.Message, candidates = e.Candidates });
            }
        });

        var symbols = api.MapGroup("/symbols");
        symbols.MapGet("/types", (string? q, AppState state) =>
            state.Project is { } project ? Results.Ok(project.SearchTypes(q ?? "")) : NoProject());
        symbols.MapGet("/namespaces", (string? q, AppState state) =>
            state.Project is { } project ? Results.Ok(project.SearchNamespaces(q ?? "")) : NoProject());
        symbols.MapGet("/members", (string? type, string? q, AppState state) =>
        {
            if (state.Project is not { } project)
                return NoProject();
            return project.Members(type ?? "", q ?? "") is { } members ? Results.Ok(members) : Error($"타입 '{type}'을(를) 찾을 수 없습니다");
        });

        api.MapPost("/preview", async (HttpRequest request, AppState state, CancellationToken ct) =>
        {
            if (state.Project is not { } project)
                return NoProject();
            var parsed = RuleIr.Parse(await ReadBody(request));
            if (parsed.Rule == null)
                return Error("규칙에 오류가 있어 돌릴 수 없습니다 — 문제 목록을 먼저 고치세요");
            RuleSettings settings = RuleSettings.Empty;
            if (state.Folder is { } folder)
            {
                var (read, settingsErrors) = folder.ReadSettings();
                // 생성이 막히는 상태에서 공통 제외 없이 돌리면 실제와 다르다 — 먼저 고치게 한다
                if (read == null)
                    return Error($"공통 설정({RuleSettings.FileName})에 오류가 있어 미리 보기를 돌리지 않습니다 — 도구줄 '공통 설정'에서 고치세요: {string.Join(" / ", settingsErrors)}");
                settings = read;
            }
            var result = await Task.Run(() => Preview.Run(project, parsed.Rule, settings, ct), ct);
            return Results.Ok(result);
        });
    }

    private static async Task<IResult> Save(string file, HttpRequest request, AppState state, bool create)
    {
        if (state.Folder is not { } folder)
            return NoFolder();
        if (folder.PathOf(file) is not { } path)
            return Error("규칙 파일 이름이 아닙니다 (글자 · 숫자 · . _ - 와 .hblint.json)");
        if (create == File.Exists(path))
            return create ? Results.Conflict(new { error = $"{file}이(가) 이미 있습니다" }) : Results.NotFound();

        string text;
        try
        {
            text = RuleFolder.Normalize(await ReadBody(request));
        }
        catch (JsonParseException e)
        {
            return Error($"JSON이 아닙니다 ({e.Line},{e.Column}): {e.Message}");
        }
        var changed = HBLint.Core.Generation.OutputFile.WriteIfChanged(path, text);
        return Results.Ok(new { file, changed, text, validation = Validate(text, state) });
    }

    /// <summary>규칙 검증 + 공통 설정과 맞춰 본 경고 (다시 넣을 것이 없는 include…).</summary>
    private static object Validate(string text, AppState state)
    {
        var parsed = RuleIr.Parse(text);
        var project = state.Project;
        var errors = parsed.Errors.Select(e => new IrErrorDto(e.Message, e.Line, e.Column, e.IsWarning, e.Node)).ToList();
        if (parsed.Rule != null && state.Folder?.ReadSettings().Settings is { } settings)
            errors.AddRange(settings.Warnings(parsed.Rule).Select(w => new IrErrorDto(w, 0, 0, true, null)));
        return new
        {
            ok = parsed.Success,
            errors,
            names = project != null ? NameCheck.Run(project, text) : null,
        };
    }

    private static object SettingsDto(RuleFolder folder)
    {
        var (settings, errors) = folder.ReadSettings();
        return new
        {
            file = RuleSettings.FileName,
            exists = File.Exists(Path.Combine(folder.Folder, RuleSettings.FileName)),
            ok = settings != null,
            ignoreAssemblies = settings?.IgnoreAssemblies ?? [],
            ignorePaths = settings?.IgnorePaths ?? [],
            errors,
        };
    }

    /// <summary>빈 칸 · 앞뒤 공백 · 중복을 뺀 목록.</summary>
    private static string[] Clean(string[]? values) =>
        (values ?? []).Where(v => v != null).Select(v => v.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).ToArray();

    private static object StateDto(AppState state)
    {
        var folder = state.Folder;
        var project = state.Project;
        return new
        {
            defaults = new { rules = state.Options.Rules, @out = state.Options.Out, project = state.Options.Project },
            rules = folder?.Folder,
            @out = folder?.Output,
            project = project == null ? null : new
            {
                csproj = project.Info.Csproj,
                kind = project.Info.Kind,
                assemblyName = project.Info.AssemblyName,
                sources = project.Info.Sources.Count,
                references = project.Info.References.Count,
                missing = project.Info.MissingReferences,
                types = project.TypeCount,
                loadMs = project.LoadMs,
                candidates = ProjectLoader.UnityCsprojs(Path.GetDirectoryName(project.Info.Csproj)!),
            },
            recent = state.Recent(),
        };
    }

    private static async Task<string> ReadBody(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body, System.Text.Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static IResult Error(string message) => Results.BadRequest(new { error = message });

    /// <summary>탐색기 "경로로 복사"는 "D:\Game"처럼 따옴표로 감싼다. 비었으면 null.</summary>
    private static string? CleanPath(string? path)
    {
        var trimmed = path?.Trim().Trim('"').Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static IResult NoFolder() => Error("규칙 폴더를 먼저 여세요");

    private static IResult NoProject() => Error("대상 프로젝트를 먼저 읽으세요");
}
