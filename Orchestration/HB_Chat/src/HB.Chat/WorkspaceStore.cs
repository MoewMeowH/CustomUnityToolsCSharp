using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace HB.Chat;

/// <summary>작업 폴더 하나: 이름 · 경로 · 설정 파일(appsettings)에서 온 것인지(읽기 전용).</summary>
public sealed record WorkspaceEntry(string Name, string Path, bool FromConfig);

/// <summary>
/// 앱에서 더한 작업 폴더 (3차 d — 노션 "앱 내 설정탭에 세션 이름 및 경로 추가기능", 해석 "작업 폴더 이름 → 경로"). 데이터 폴더의 workspaces.json에
/// 원자적으로 쓰고(RoomStore와 같은 규칙), 깨졌으면 덮지 않고 모든 쓰기를 거절한다. 켤 때 한 번 읽는다 — 앱 밖에서 파일을 고쳐도 다시 켜기 전에는
/// 반영되지 않는다 (설정 파일과 같은 원칙, Program.cs "설정은 켤 때 한 번만").
/// 작업 폴더는 Claude가 편집 · (명령 방이면) 명령을 실행하고, 그 폴더 .mcp.json의 MCP 서버 · 프로젝트 훅이 도는 범위라 위험한 폴더를 거절한다
/// (<see cref="WhyUnsafe"/>) — 더할 때, 켤 때 읽을 때, 방을 실행하기 전에 같은 검사 (/self-review security eee1adb777 C1 · C5).
/// appsettings의 Chat:Workspaces가 먼저이고 같은 이름은 더할 수 없다.
/// </summary>
public sealed partial class WorkspaceStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Lock _lock = new();
    private readonly string _path;
    private readonly IReadOnlyDictionary<string, string> _configured;
    private readonly IReadOnlyList<string> _protectedFolders;
    /// <summary>방이 이 목록의 이름을 가리키는 칸 — 작업 폴더면 Workspace, Unity 프로젝트면 UnityProject (지우기 · 이름 다시 쓰기 막기).</summary>
    private readonly Func<Room, string?> _usedBy;
    /// <summary>더할 때 더 보는 검사 (Unity 프로젝트: ProjectSettings/ProjectVersion.txt). null이면 없음.</summary>
    private readonly Func<string, string?>? _extraCheck;
    private readonly string _noun;
    private string FileName => System.IO.Path.GetFileName(_path);

    /// <summary>파일에 있는 그대로 (쓸 수 없는 항목도 — 저장할 때 잃지 않게, 460430d647 C5).</summary>
    private readonly Dictionary<string, string> _saved = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>쓰는 것: 파일 항목 중 설정 파일 이름과 겹치지 않고 검사를 통과한 것.</summary>
    private readonly Dictionary<string, string> _added = new(StringComparer.OrdinalIgnoreCase);

    public string? LoadError { get; }

    /// <summary>파일에 있지만 쓰지 않는 항목과 이유 (설정 화면 경고).</summary>
    public IReadOnlyList<string> LoadWarnings { get; }

    /// <param name="path">workspaces.json 경로 (데이터 폴더).</param>
    /// <param name="configured">appsettings의 Chat:Workspaces (읽기 전용).</param>
    /// <param name="protectedFolders">
    /// 작업 폴더가 품거나 그 안이면 안 되는 폴더 — 이 앱 · 그 git 저장소 · 데이터 폴더 · Claude 설정 폴더 · 설정 작업 폴더 기준 편집 금지 폴더.
    /// 편집 금지 규칙이 작업 폴더 기준 상대 경로라, 이 폴더들을 따로 작업 폴더로 두면 그 보호가 사라지므로.
    /// </param>
    /// <param name="usedBy">방이 이름을 가리키는 칸 (기본 작업 폴더). Unity 프로젝트 목록은 r => r.UnityProject.</param>
    /// <param name="extraCheck">더할 때 · 켤 때 · 실행 전에 더 보는 검사 (최종 경로 → 오류 문구).</param>
    /// <param name="noun">오류 문구에 쓸 항목 이름 (작업 폴더 · Unity 프로젝트).</param>
    public WorkspaceStore(string path, IReadOnlyDictionary<string, string> configured, IEnumerable<string> protectedFolders,
        Func<Room, string?>? usedBy = null, Func<string, string?>? extraCheck = null, string noun = "작업 폴더")
    {
        _path = path;
        _noun = noun;
        _configured = configured;
        _usedBy = usedBy ?? (r => r.Workspace);
        _extraCheck = extraCheck;
        _protectedFolders = [.. protectedFolders.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Resolve(p) ?? Full(p)).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        var warnings = new List<string>();
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<WorkspaceFile>(File.ReadAllText(path), Json)?.Workspaces is { } saved)
                foreach (var (name, folder) in saved)
                {
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    _saved[name] = folder ?? "";
                    // 읽을 때도 같은 검사 — 파일을 앱 밖에서 고쳤어도(명령 방 등) 위험한 폴더가 들어오지 않게 (eee1adb777 C5 · 460430d647 C4)
                    var why = _configured.ContainsKey(name) ? "설정 파일에 같은 이름이 있어 그쪽을 씁니다"
                        : !NameShape().IsMatch(name) ? "이름 모양이 맞지 않습니다"
                        : WhyUnsafe(folder) ?? _extraCheck?.Invoke(Resolve(folder!)!);
                    if (why is null) _added[name] = Resolve(folder!)!;
                    else warnings.Add($"{name}: {why}");
                }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            LoadError = $"{ex.GetType().Name}: {ex.Message}";
        }
        LoadWarnings = warnings;
    }

    /// <summary>설정 파일 것 먼저, 그다음 앱에서 더한 것 (이름 순).</summary>
    public IReadOnlyList<WorkspaceEntry> All()
    {
        lock (_lock)
            return
            [
                .. _configured.Where(p => !string.IsNullOrWhiteSpace(p.Value)).Select(p => new WorkspaceEntry(p.Key, p.Value.Trim(), true)),
                .. _added.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => new WorkspaceEntry(p.Key, p.Value, false)),
            ];
    }

    /// <summary>앱에서 더한 작업 폴더의 경로. 없으면 null (설정 파일 것은 <see cref="ChatOptions.WorkspacePath"/>가 먼저 본다).</summary>
    public string? PathOf(string name)
    {
        lock (_lock) return _added.TryGetValue(name, out var p) ? p : null;
    }

    /// <summary>
    /// 더하기. 오류 문구(null이면 더함). 경로는 최종 경로(링크 · subst · 짧은 이름이 풀린 것)로 저장한다.
    /// rooms: 지금 방들 — 방(휴지통 포함)이 쓰는 이름은 받지 않는다. 방은 작업 폴더를 이름으로 가리켜, 주인 없는 이름을 다른 폴더로 다시 쓰면
    /// 예전 방(항상 허용 규칙 포함)이 새 폴더에서 돈다 (eee1adb777 C4).
    /// </summary>
    public string? Add(string? name, string? path, IEnumerable<Room> rooms)
    {
        var n = name?.Trim() ?? "";
        lock (_lock)
        {
            if (LoadError is not null) return $"{FileName}을 읽지 못해 쓰지 않습니다: {LoadError}";
            if (!NameShape().IsMatch(n)) return "이름은 글자 · 숫자로 시작하고 글자 · 숫자 · 공백 · . _ - 만 40자까지 씁니다.";
            if (_configured.ContainsKey(n) || _saved.ContainsKey(n)) return "이미 있는 이름입니다.";
            if (rooms.Any(r => string.Equals(_usedBy(r), n, StringComparison.OrdinalIgnoreCase)))
                return "그 이름을 쓰던 방이 남아 있습니다(휴지통 포함) — 다른 이름을 쓰세요.";
            if (WhyUnsafe(path) is { } why) return why;
            if (_extraCheck?.Invoke(Resolve(path!)!) is { } extra) return extra;
            var final = Resolve(path!)!;
            foreach (var other in _configured.Values.Concat(_added.Values))
                if ((Resolve(other) ?? Full(other)) is { } o && Same(final, o)) return "이미 다른 이름으로 있는 폴더입니다.";
            _saved[n] = final;
            _added[n] = final;
            try
            {
                Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _saved.Remove(n);
                _added.Remove(n);
                return $"저장하지 못했습니다: {ex.Message}";
            }
        }
        return null;
    }

    /// <summary>앱에서 더한 것(쓰지 않는 항목 포함) 지우기. 그 폴더를 쓰는 방(휴지통 포함)이 있으면 거절. 오류 문구(null이면 지움).</summary>
    public string? Remove(string? name, IEnumerable<Room> rooms)
    {
        var n = name?.Trim() ?? "";
        lock (_lock)
        {
            if (LoadError is not null) return $"{FileName}을 읽지 못해 쓰지 않습니다: {LoadError}";
            if (!_saved.TryGetValue(n, out var before))
                return _configured.ContainsKey(n) ? $"설정 파일(appsettings)의 {_noun}는 앱에서 지울 수 없습니다." : $"없는 {_noun}입니다.";
            // 설정 파일에 같은 이름이 있으면 방은 그쪽을 쓰고 있다 — 앱 항목만 지운다
            var used = _configured.ContainsKey(n) ? 0 : rooms.Count(r => string.Equals(_usedBy(r), n, StringComparison.OrdinalIgnoreCase));
            if (used > 0) return $"이 항목을 쓰는 방이 {used}개 있습니다(휴지통 포함) — 그 방에서 먼저 빼세요.";
            var hadAdded = _added.Remove(n, out var added);
            _saved.Remove(n);
            try
            {
                Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _saved[n] = before;
                if (hadAdded) _added[n] = added!;
                return $"저장하지 못했습니다: {ex.Message}";
            }
        }
        return null;
    }

    /// <summary>
    /// 그 경로를 작업 폴더로 쓰면 안 되는 이유 (null이면 됨). 더할 때 · 켤 때 읽을 때 · 방 실행 전(앱에서 더한 것)에 쓴다:
    /// 드라이브 문자로 시작하는 절대 경로만(\\?\ · \\.\ · UNC 거절), 네트워크 드라이브 거절, 있는 폴더, 경로의 어느 폴더도 링크(재분석 지점)가 아님,
    /// 최종 경로(subst · 8.3 짧은 이름이 풀린 것)가 드라이브 루트 · Windows · Program Files · System 그 자체나 그 안 · 사용자 홈 그 자체가 아니고,
    /// 보호 폴더를 품거나 그 안이 아님 (eee1adb777 C1 · C2).
    /// </summary>
    public string? WhyUnsafe(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !DriveLetterPath().IsMatch(path.Trim()))
            return "경로는 드라이브 문자부터 쓴 절대 경로여야 합니다 (예: F:/AI/MyProject — \\\\?\\ · UNC는 받지 않음).";
        if (Full(path) is not { } full) return "경로 모양이 올바르지 않습니다.";
        try
        {
            if (new DriveInfo(full[..1]).DriveType is DriveType.Network or DriveType.NoRootDirectory) return "네트워크 · 없는 드라이브는 작업 폴더로 쓸 수 없습니다.";
        }
        catch (ArgumentException) { return "드라이브를 알 수 없습니다."; }
        if (!Directory.Exists(full)) return "없는 폴더입니다.";
        if (ThroughLink(full) is { } link) return $"링크(재분석 지점 — 심볼릭 링크 · 정션)를 거치는 경로는 받지 않습니다: {link}";
        if (Resolve(full) is not { } final) return "폴더의 실제 경로를 확인하지 못했습니다.";
        if (string.Equals(System.IO.Path.GetPathRoot(final)?.TrimEnd('\\', '/'), final.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            return "드라이브 루트는 작업 폴더로 쓸 수 없습니다.";
        foreach (var system in SystemFolders())
            if (Same(final, system) || Inside(final, system)) return $"시스템 폴더는 작업 폴더로 쓸 수 없습니다: {system}";
        var homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if ((Resolve(homePath) ?? Full(homePath)) is { } home && Same(final, home))
            return "사용자 홈 폴더 전체는 작업 폴더로 쓸 수 없습니다 — 그 안의 프로젝트 폴더를 고르세요.";
        foreach (var guarded in _protectedFolders)
            if (Same(final, guarded) || Inside(final, guarded) || Inside(guarded, final))
                return $"이 앱 · 그 저장소 · 데이터 · Claude 설정 폴더를 품거나 그 안인 폴더는 쓸 수 없습니다 (지휘자가 앱 · 설정을 고칠 수 있게 됨): {guarded}";
        return null;
    }

    /// <summary>지금 쓸 수 없는 이유 — <see cref="WhyUnsafe"/>에 더해 그 목록의 검사(Unity 프로젝트 모양)까지. 실행 전 · 화면 표시에 쓴다.</summary>
    public string? WhyUnusable(string path) =>
        WhyUnsafe(path) ?? (_extraCheck is null || Resolve(path) is not { } final ? null : _extraCheck(final));

    /// <summary>
    /// 그 폴더 .mcp.json의 MCP 서버 항목이 실행할 명령 (command + args, 200자) — 읽기 이상 방은 이 명령을 승인 없이 띄운다 (<see cref="McpConfigFile"/>,
    /// eee1adb777 C3). 항목이 없거나 읽지 못하면 null.
    /// </summary>
    public static string? McpCommand(string folder, string server)
    {
        try
        {
            var file = System.IO.Path.Combine(folder, ".mcp.json");
            if (!File.Exists(file) || JsonNode.Parse(File.ReadAllText(file))?["mcpServers"]?[server] is not JsonObject entry) return null;
            var command = entry["command"]?.ToString() ?? entry["url"]?.ToString() ?? "(명령 없음)";
            var args = entry["args"] is JsonArray a ? " " + string.Join(" ", a.Select(x => x?.ToString())) : "";
            var text = command + args;
            return text.Length > 200 ? text[..200] + "…" : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException) { return null; }
    }

    /// <summary>그 폴더에 프로젝트 훅이 있는지 (.claude/settings*.json의 hooks — -p 실행에서도 돈다, 2-1 실측). 읽지 못하면 true (조심 쪽).</summary>
    public static bool HasProjectHooks(string folder)
    {
        foreach (var name in new[] { "settings.json", "settings.local.json" })
        {
            var file = System.IO.Path.Combine(folder, ".claude", name);
            try
            {
                if (File.Exists(file) && JsonNode.Parse(File.ReadAllText(file))?["hooks"] is JsonObject { Count: > 0 }) return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return true; }
        }
        return false;
    }

    /// <summary>
    /// 명령 방의 작업 폴더 밖 파일 쓰기(Write · Edit 카드)를 카드로 물어도 되는지 — 안 되면 까닭 (카드 없이 거절). 이 목록의 보호 폴더
    /// (이 앱 · 그 저장소 · 데이터 · Claude 설정 · 설정 작업 폴더 기준 편집 금지 폴더)를 쓴다.
    /// </summary>
    public string? WhyNoWrite(string? filePath, string? workspace = null) => WhyNoWrite(filePath, _protectedFolders, workspace);

    /// <summary>
    /// <see cref="WhyNoWrite(string?, string?)"/>의 본체: 드라이브 문자 절대 경로만 · 이름을 다르게 읽히게 하는 표기(드라이브 뒤 <c>:</c> 스트림 · <c>~숫자</c> 짧은 이름 ·
    /// 끝 점/공백) 아님 · 네트워크 드라이브 아님 · 있는 조상 폴더와 파일 자신이 링크 · 정션 아님 · 쓰지 않는 이름(<see cref="WriteBlockedName"/>)이 아님 —
    /// 입력 경로와, 짧은 이름 · 링크를 푼 최종 경로 둘 다 (/cross-review a43e6f3e7b 1-F1) · 작업 폴더(<paramref name="workspace"/>) · 시스템 폴더 ·
    /// <paramref name="guarded"/> · 시작프로그램 폴더 밖. 편집 금지 규칙(EditDenied)은 작업 폴더 기준이라 그 밖에는 걸리지 않아 같은 것을 여기서 본다.
    /// </summary>
    /// <param name="workspace">방 작업 폴더 경로 — 그 안 쓰기가 호스트에 왔으면(계획 모드 · CLI 보호 파일) 이 카드로 묻지 않는다 (sub_self dfe83d0bb5 C4).</param>
    public static string? WhyNoWrite(string? filePath, IEnumerable<string> guarded, string? workspace = null)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !DriveLetterPath().IsMatch(filePath.Trim()))
            return "드라이브 문자부터 쓴 절대 경로가 아닙니다 (\\\\?\\ · UNC · 상대 경로는 받지 않음).";
        var raw = filePath.Trim();
        if (raw.IndexOf(':', 2) >= 0) return "드라이브 뒤에 ':'(대체 데이터 스트림)가 든 경로는 받지 않습니다.";
        foreach (var part in raw[3..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part is not ("." or "..") && (part.EndsWith('.') || part.EndsWith(' '))) return $"끝이 점 · 공백인 이름은 받지 않습니다: '{part}'";
            if (ShortName().IsMatch(part)) return $"짧은 이름(8.3) 모양의 조각은 받지 않습니다 — 원래 이름으로 쓰게 하세요: {part}";
        }
        if (Full(raw) is not { } full || System.IO.Path.GetDirectoryName(full) is not { Length: > 0 } folder) return "경로 모양이 올바르지 않습니다.";
        try
        {
            if (new DriveInfo(full[..1]).DriveType is DriveType.Network or DriveType.NoRootDirectory) return "네트워크 · 없는 드라이브입니다.";
        }
        catch (ArgumentException) { return "드라이브를 알 수 없습니다."; }
        if (WriteBlockedName(full) is { } blocked) return $"지시 · 설정 · 실행으로 이어지는 파일이라 쓰지 않습니다 ({blocked}).";
        // 새 폴더를 만들며 쓰는 경우 — 있는 가장 가까운 폴더로 링크 · 최종 경로를 본다
        var existing = folder;
        while (!Directory.Exists(existing))
            if (System.IO.Path.GetDirectoryName(existing) is { Length: > 0 } parent) existing = parent;
            else return "있는 상위 폴더를 찾지 못했습니다.";
        string target;
        try
        {
            if (File.Exists(full) && File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint)) return "링크 파일에는 쓰지 않습니다.";
            if (ThroughLink(existing) is { } link) return $"링크(재분석 지점 — 심볼릭 링크 · 정션)를 거치는 경로입니다: {link}";
            if (Resolve(existing) is not { } final) return "폴더의 실제 경로를 확인하지 못했습니다.";
            // 이미 있는 파일은 파일 핸들로 최종 이름까지 (그 파일의 짧은 이름 · 대소문자)
            target = File.Exists(full) ? ResolveFile(full) ?? "" : System.IO.Path.GetFullPath(System.IO.Path.Combine(final, System.IO.Path.GetRelativePath(existing, full)));
            if (target.Length == 0) return "파일의 실제 경로를 확인하지 못했습니다.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "파일 · 폴더 속성을 읽지 못했습니다."; }
        if (WriteBlockedName(target) is { } real) return $"지시 · 설정 · 실행으로 이어지는 파일이라 쓰지 않습니다 ({real}).";
        if (workspace is not null && (Resolve(workspace) ?? Full(workspace)) is { } ws && (Same(target, ws) || Inside(target, ws)))
            return "작업 폴더 안 파일인데 묻는 쓰기라(계획 모드 · 보호 파일) 이 카드로 묻지 않습니다.";
        foreach (var system in SystemFolders())
            if (Same(target, system) || Inside(target, system)) return $"시스템 폴더 안입니다: {system}";
        foreach (var start in new[] { Environment.SpecialFolder.Startup, Environment.SpecialFolder.CommonStartup }
                     .Select(f => Environment.GetFolderPath(f)).Where(p => p.Length > 3).Select(p => Resolve(p) ?? Full(p)).OfType<string>())
            if (Same(target, start) || Inside(target, start)) return $"시작프로그램 폴더 안입니다: {start}";
        foreach (var guard in guarded.Select(g => Resolve(g) ?? Full(g)).OfType<string>())
            if (Same(target, guard) || Inside(target, guard)) return $"이 앱 · 그 저장소 · 데이터 · Claude 설정 폴더 안입니다: {guard}";
        return null;
    }

    /// <summary>8.3 짧은 이름 모양 (<c>GIT~1</c> · <c>CLAUDE~1.MD</c>) — 이름 규칙을 다르게 읽히게 해서 받지 않는다.</summary>
    [GeneratedRegex(@"~\d")]
    private static partial Regex ShortName();

    /// <summary>
    /// 쓰지 않는 폴더 이름 — 들어가면 다음 git 명령 · 편집기 · 셸이 그 안 파일을 실행한다 (/cross-review a43e6f3e7b — sub_self C2 · sub_security C3).
    /// </summary>
    private static readonly string[] WriteBlockedFolders = [".git", ".vscode", ".idea"];

    /// <summary>
    /// 쓰지 않는 파일 이름 · 확장자 — git · 셸 설정, Claude 사용자 설정(~/.claude.json), 실행 파일 · 스크립트 모듈 · 바로 가기 (같은 리뷰).
    /// .js는 다른 프로젝트 파일에 흔해서 넣지 않는다.
    /// </summary>
    private static readonly string[] WriteBlockedFiles =
    [
        ".gitconfig", ".gitmodules", ".gitattributes", ".bashrc", ".bash_profile", ".profile", ".zshrc", ".zprofile", ".claude.json*",
        "*.psm1", "*.psd1", "*.vbs", "*.vbe", "*.wsf", "*.wsh", "*.hta", "*.lnk", "*.exe", "*.dll", "*.scr", "*.com", "*.msi", "*.reg", "*.jse", "*.pif",
    ];

    /// <summary>
    /// 쓰지 않는 이름이면 그 이름 — 편집 금지 규칙(<see cref="ClaudeArgs.EditDenied"/>) 중 이름 규칙(<c>**/이름</c> · <c>**/폴더/**</c>)과 같은 것
    /// (지시 파일 · .claude · .mcp*.json · appsettings · 스크립트) + <see cref="WriteBlockedFolders"/> · <see cref="WriteBlockedFiles"/>. 대소문자는 가리지 않는다.
    /// </summary>
    public static string? WriteBlockedName(string fullPath)
    {
        var parts = fullPath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        var name = parts[^1];
        static bool Like(string value, string glob) => Regex.IsMatch(value, "^" + Regex.Escape(glob).Replace(@"\*", ".*") + "$", RegexOptions.IgnoreCase);
        foreach (var dir in parts[..^1])
            if (WriteBlockedFolders.FirstOrDefault(f => dir.Equals(f, StringComparison.OrdinalIgnoreCase)) is { } folder) return folder + " 폴더";
        if (WriteBlockedFiles.FirstOrDefault(f => Like(name, f)) is not null) return name;
        foreach (var rule in ClaudeArgs.EditDenied.Where(r => r.StartsWith("**/", StringComparison.Ordinal)))
        {
            var rest = rule[3..];
            if (rest.EndsWith("/**", StringComparison.Ordinal))
            {
                if (parts[..^1].Any(d => d.Equals(rest[..^3], StringComparison.OrdinalIgnoreCase))) return rest[..^3] + " 폴더";
            }
            else if (!rest.Contains('/') && Like(name, rest))
                return name;
        }
        return null;
    }

    /// <summary>
    /// 이 앱이 작업 폴더 · 쓰기 대상으로 막는 보호 폴더 — 이 앱(콘텐츠 · 실행 폴더) · 그 git 저장소 · 데이터 폴더 · Claude 설정 폴더(세션 기록 폴더의 부모 · ~/.claude) ·
    /// 설정 작업 폴더 기준 편집 금지 폴더. Program과 RoomHub(앱 목록이 없을 때)가 같이 쓴다 (sub_self dfe83d0bb5 C5).
    /// </summary>
    public static IReadOnlyList<string> GuardedFolders(ChatOptions chat, string contentRoot)
    {
        var guarded = new List<string> { contentRoot, AppContext.BaseDirectory, chat.DataFolder };
        // 앱이 든 git 저장소 전체 (공유 라이브러리 · 데스크톱 · MCP 게시 폴더 — 다음 빌드 · 실행에 들어간다)
        for (var dir = new DirectoryInfo(contentRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(System.IO.Path.Combine(dir.FullName, ".git")) || File.Exists(System.IO.Path.Combine(dir.FullName, ".git"))) { guarded.Add(dir.FullName); break; }
        // Claude 설정 폴더 (세션 기록 · 할 일 · settings.json 훅): 세션 기록 폴더의 부모(CLAUDE_CONFIG_DIR 반영)와 ~/.claude
        if (System.IO.Path.GetDirectoryName(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(chat.ProjectsRoot))) is { Length: > 0 } claudeDir)
            guarded.Add(claudeDir);
        guarded.Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"));
        // 설정 파일 작업 폴더 기준 편집 금지 폴더 (Orchestration/HB_Chat · Orchestration/Publish_Output)
        foreach (var ws in chat.Workspaces.Values.Where(v => !string.IsNullOrWhiteSpace(v)))
            foreach (var rule in ClaudeArgs.EditDenied.Where(r => !r.StartsWith("**", StringComparison.Ordinal) && r.EndsWith("/**", StringComparison.Ordinal)))
                guarded.Add(System.IO.Path.Combine(ws.Trim(), rule[..^3]));
        return guarded;
    }

    private static IEnumerable<string> SystemFolders() =>
        new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.System }
            .Select(f => Environment.GetFolderPath(f)).Where(p => p.Length > 3).Select(p => Resolve(p) ?? Full(p)).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>경로의 폴더 중 재분석 지점(링크 · 정션)이 있으면 그 폴더. 못 보면 거절 쪽(그 경로).</summary>
    private static string? ThroughLink(string full)
    {
        for (var dir = new DirectoryInfo(full); dir is not null; dir = dir.Parent)
        {
            try
            {
                if (dir.Parent is not null && dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) return dir.FullName;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return dir.FullName; }
        }
        return null;
    }

    /// <summary>
    /// 있는 폴더의 최종 경로: 폴더 핸들의 GetFinalPathNameByHandle (Windows — subst · 8.3 짧은 이름 · 링크가 풀린 드라이브 문자 경로).
    /// 네트워크(UNC)로 풀리면 null. 없는 폴더 · 다른 OS는 GetFullPath 그대로가 아니라 null — 비교에 쓰지 않는다.
    /// </summary>
    private static string? Resolve(string path)
    {
        if (Full(path) is not { } full || !Directory.Exists(full)) return null;
        if (!OperatingSystem.IsWindows()) return full;
        using var handle = CreateFileW(full, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        var buffer = new StringBuilder(1024);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) return null;
        var result = buffer.ToString();
        if (result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return null;
        if (result.StartsWith(@"\\?\", StringComparison.Ordinal)) result = result[4..];
        return System.IO.Path.TrimEndingDirectorySeparator(result);
    }

    /// <summary>있는 파일의 최종 경로 (짧은 이름 · 대소문자가 풀린 드라이브 문자 경로). 못 풀면 null. 다른 OS는 GetFullPath 그대로.</summary>
    private static string? ResolveFile(string path)
    {
        if (Full(path) is not { } full || !File.Exists(full)) return null;
        if (!OperatingSystem.IsWindows()) return full;
        using var handle = CreateFileW(full, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        var buffer = new StringBuilder(1024);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) return null;
        var result = buffer.ToString();
        if (result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return null;
        return result.StartsWith(@"\\?\", StringComparison.Ordinal) ? result[4..] : result;
    }

    private const uint BackupSemantics = 0x02000000; // FILE_FLAG_BACKUP_SEMANTICS — 폴더 핸들을 열 때 필요

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);

    private static string? Full(string path)
    {
        try { return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path.Trim())); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException) { return null; }
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>child가 parent 안(같은 것은 아님)인지 — 구분자 경계로 본다 (F:/AI와 F:/AIX를 섞지 않게).</summary>
    private static bool Inside(string child, string parent) =>
        child.Length > parent.Length && child.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
        && (child[parent.Length] is '\\' or '/' || parent.EndsWith('\\') || parent.EndsWith('/'));

    private void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_path))!);
        var tmp = _path + ".tmp";
        var workspaces = _saved.ToDictionary(p => p.Key, p => (string?)p.Value, StringComparer.OrdinalIgnoreCase);
        File.WriteAllText(tmp, JsonSerializer.Serialize(new WorkspaceFile { Workspaces = workspaces }, Json), new UTF8Encoding(false));
        File.Move(tmp, _path, overwrite: true);
    }

    private sealed class WorkspaceFile
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, string?> Workspaces { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"^[\p{L}\p{N}][\p{L}\p{N} ._-]{0,39}$")]
    private static partial Regex NameShape();

    /// <summary>드라이브 문자 + 콜론 + 구분자로 시작 (C:\ · C:/). \\?\ · \\.\ · UNC는 아님.</summary>
    [GeneratedRegex(@"^[A-Za-z]:[\\/]")]
    private static partial Regex DriveLetterPath();
}
