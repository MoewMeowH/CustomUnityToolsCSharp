using Microsoft.AspNetCore.Mvc;

namespace HB.Chat.Pages;

/// <summary>방 화면의 연결 탭: MCP 서버 · Unity 프로젝트.</summary>
public sealed partial class RoomModel
{
    // ---- 연결 탭 (Claude 앱 커넥터처럼) ----

    /// <summary>
    /// 연결 한 줄. Kind: base(기본 MCP) · app(앱 MCP hb-chat) · extra(작업 폴더 .mcp.json의 다른 서버) · unity. On: 이 방에서 켬.
    /// Applies: 지금 단계에서 실제로 붙는지 (끈 것과 따로 — 켜 둬도 단계가 낮으면 안 붙음). Init: 마지막 실행 init의 상태 (connected · failed …).
    /// </summary>
    public sealed record Connection(string Name, string Kind, string Transport, string? Command, bool On, bool Applies, string? Note,
        string? Init, ProbeResult? Probe, bool Probing, IReadOnlyList<(string Name, string? Description)>? StaticTools = null)
    {
        /// <summary>작업 폴더 .mcp.json과 앱 서버 목록에 같은 이름이 있음 — 작업 폴더 것을 쓴다 (줄에 표시).</summary>
        public bool Shadowed { get; init; }

        /// <summary>http 앱 서버 — OAuth 로그인 서버면 터미널 claude의 /mcp에서 한 번 로그인해야 붙는다 (안내).</summary>
        public bool NeedsLogin { get; init; }

        /// <summary>마지막 실행 init의 이 서버 도구 이름 (설명 없음) — 확인이 도구를 못 받았을 때(OAuth 서버 등) 대신 보인다.</summary>
        public IReadOnlyList<(string Name, string? Description)> InitTools { get; init; } = [];

        /// <summary>보일 도구 목록: 앱 도구 → 확인 결과 → 실행 init 이름 순.</summary>
        public IReadOnlyList<(string Name, string? Description)> ShownTools =>
            StaticTools ?? (Probe is { Ok: true } p ? p.Tools : InitTools);
    }

    public McpProbe Probe => probe;

    /// <summary>등록된 Unity 프로젝트 (연결 탭 select).</summary>
    public IReadOnlyList<WorkspaceEntry> UnityProjects => options.Value.AllUnityProjects;

    public bool IsMeasureUnity(string name) => options.Value.IsMeasureUnity(name);

    public IReadOnlyList<Connection> Connections
    {
        get
        {
            var o = options.Value;
            var room = Room;
            var ws = WorkspacePath;
            // 마지막 실행 init의 상태 · 도구 이름 — 방에 저장돼 앱을 다시 켜도 남는다 (RoomHub.KeepMcpStatus)
            string? Init(string name) => room.McpStatus.FirstOrDefault(s => s.Name == name)?.Status;
            IReadOnlyList<(string, string?)> InitTools(string name) =>
            [
                .. room.McpTools.Where(t => t.StartsWith($"mcp__{name}__", StringComparison.Ordinal))
                    .Select(t => (t[$"mcp__{name}__".Length..], (string?)null)).Order(),
            ];
            string Transport(string name) => ws is not null && McpConfigFile.Entry(ws, name)?["url"] is not null ? "http" : "stdio";
            var rows = new List<Connection>
            {
                new(o.McpServer, "base", ws is null ? "?" : Transport(o.McpServer), ws is null ? null : WorkspaceStore.McpCommand(ws, o.McpServer),
                    !room.McpOff.Contains(o.McpServer), true, null,
                    Init(o.McpServer), probe.Last(room.Id, o.McpServer), probe.IsRunning(room.Id, o.McpServer)) { InitTools = InitTools(o.McpServer) },
                new(TerminalMcp.Server, "app", "http", "이 앱 (터미널 · 지휘자 브라우저 도구)", !room.McpOff.Contains(TerminalMcp.Server),
                    ClaudeArgs.RunsCommands(room.Level), ClaudeArgs.RunsCommands(room.Level) ? null : "명령 방에서만 붙습니다",
                    Init(TerminalMcp.Server), null, false, McpProbe.AppTools()),
            };
            var names = ws is null ? [] : McpConfigFile.ServerNames(ws);
            // 앱에서 더한 서버 — 같은 이름이 작업 폴더에 있으면 그쪽을 쓴다
            var appServers = o.AppMcp?.All() ?? [];
            foreach (var name in names.Concat(appServers.Select(a => a.Name)).Concat(room.McpExtra).Distinct(StringComparer.Ordinal)
                         .Where(n => n != o.McpServer && n != TerminalMcp.Server && n != McpConfigFile.UnityServer))
            {
                var inWorkspace = names.Contains(name);
                var app = appServers.FirstOrDefault(a => a.Name == name).Entry;
                var exists = inWorkspace || app is not null;
                var note = !exists ? "작업 폴더 .mcp.json에도 앱 서버 목록에도 없어졌습니다 — 끄면 목록에서 빠집니다" : null;
                rows.Add(new(name, inWorkspace || app is null ? "extra" : "appserver",
                    inWorkspace ? Transport(name) : app is not null ? AppMcpStore.TransportOf(app) : "?",
                    inWorkspace ? WorkspaceStore.McpCommand(ws!, name) : app is not null ? AppMcpStore.Describe(app) : null,
                    room.McpExtra.Contains(name), exists, note,
                    Init(name), probe.Last(room.Id, name), probe.IsRunning(room.Id, name))
                {
                    InitTools = InitTools(name),
                    Shadowed = inWorkspace && app is not null, // 앱 서버에도 같은 이름 — 작업 폴더 것을 씀
                    NeedsLogin = !inWorkspace && app is not null && app["url"] is not null, // http 앱 서버 — OAuth면 터미널 claude의 /mcp에서 한 번 로그인
                });
            }
            if (room.UnityProject is { } unity)
            {
                var path = o.UnityProjectPath(unity);
                var cli = o.ResolveUnityCli();
                rows.Add(new(McpConfigFile.UnityServer, "unity", "stdio", cli is null || path is null ? null : $"unity mcp --tool-groups --project-path {path}",
                    true, ClaudeArgs.RunsCommands(room.Level) && path is not null && cli is not null,
                    path is null ? $"'{unity}'가 설정에 없습니다" : cli is null ? "Unity CLI(unity.exe)를 찾지 못했습니다 — 설정 Chat:UnityCliPath"
                    : ClaudeArgs.RunsCommands(room.Level) ? null : "명령 방에서만 붙습니다",
                    Init(McpConfigFile.UnityServer), probe.Last(room.Id, McpConfigFile.UnityServer), probe.IsRunning(room.Id, McpConfigFile.UnityServer))
                {
                    InitTools = InitTools(McpConfigFile.UnityServer),
                });
            }
            return rows;
        }
    }

    /// <summary>연결 켜기 · 끄기: 기본 · 앱은 McpOff, 작업 폴더의 다른 서버는 McpExtra. 다음 실행부터.</summary>
    public IActionResult OnPostMcpToggle(string id, string? name, bool on)
    {
        if (!RoomStore.IsRoomId(id) || store.Get(id) is not { } room) return NotFound();
        if (GroupSettingsRefusal(room) is { } refusal) return BadRequest(refusal);
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(name) || !ClaudeArgs.IsServerName(name) || name == McpConfigFile.UnityServer) return BadRequest("알 수 없는 연결입니다.");
        if (name == o.McpServer || name == TerminalMcp.Server)
            return Change(id, r => r with { McpOff = on ? [.. r.McpOff.Where(n => n != name)] : [.. r.McpOff.Where(n => n != name), name] }, "연결을 바꾸지 못했습니다");
        // 켤 때는 작업 폴더 .mcp.json이나 앱 서버 목록에 있는 이름만 (끄기는 없어진 이름도 받는다).
        // 앱 서버면 저장된 정식 이름으로 — 대소문자만 다른 이름이 따로 남지 않게
        var inWorkspace = o.WorkspacePath(room.Workspace) is { } ws && McpConfigFile.ServerNames(ws).Contains(name);
        if (on && !inWorkspace)
        {
            if (o.AppMcp?.NameOf(name) is not { } canonical) return BadRequest("작업 폴더 .mcp.json에도 앱 서버 목록에도 없는 서버입니다.");
            name = canonical;
        }
        return Change(id, r => r with { McpExtra = on ? [.. r.McpExtra.Where(n => n != name), name] : [.. r.McpExtra.Where(n => n != name)] }, "연결을 바꾸지 못했습니다");
    }

    /// <summary>Unity 프로젝트 연결 (명령 방만 — 결정). 빈 값이면 끊는다. 다음 실행부터.</summary>
    public IActionResult OnPostUnity(string id, string? project)
    {
        if (!RoomStore.IsRoomId(id) || store.Get(id) is not { } room) return NotFound();
        if (GroupSettingsRefusal(room) is { } refusal) return BadRequest(refusal);
        var p = string.IsNullOrWhiteSpace(project) ? null : project.Trim();
        if (p is not null)
        {
            if (!ClaudeArgs.RunsCommands(room.Level)) return BadRequest("Unity 프로젝트는 명령 방에서만 연결합니다.");
            if (options.Value.UnityProjectPath(p) is null) return BadRequest("설정에 없는 Unity 프로젝트입니다.");
            p = options.Value.AllUnityProjects.First(u => string.Equals(u.Name, p, StringComparison.OrdinalIgnoreCase)).Name;
        }
        return Change(id, r => r with { UnityProject = p }, "Unity 프로젝트를 바꾸지 못했습니다");
    }

    /// <summary>"확인": 앱이 그 서버를 띄워 도구 목록만 받고 닫는다 (<see cref="McpProbe"/>). 결과는 곁칸을 다시 받으면 보인다.</summary>
    public async Task<IActionResult> OnPostMcpProbe(string id, string? name)
    {
        if (!RoomStore.IsRoomId(id) || store.Get(id) is not { } room) return NotFound();
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(name) || !ClaudeArgs.IsServerName(name) || name == TerminalMcp.Server) return BadRequest("확인할 수 없는 연결입니다.");
        if (o.WorkspacePath(room.Workspace) is not { } ws || !Directory.Exists(ws)) return BadRequest("작업 폴더가 없습니다.");
        System.Text.Json.Nodes.JsonObject? entry;
        if (name == McpConfigFile.UnityServer)
        {
            if (room.UnityProject is not { } unity || o.UnityProjectPath(unity) is not { } path) return BadRequest("연결한 Unity 프로젝트가 없습니다.");
            if (!o.UnityProjects.ContainsKey(unity) && o.AppUnityProjects?.WhyUnusable(path) is { } why) return BadRequest($"지금은 쓸 수 없는 프로젝트입니다: {why}");
            if (o.ResolveUnityCli() is not { } cli) return BadRequest("Unity CLI(unity.exe)를 찾지 못했습니다.");
            entry = McpConfigFile.UnityEntry(cli, path);
        }
        else entry = McpConfigFile.Entry(ws, name) ?? o.AppMcp?.Get(name); // 작업 폴더 것 먼저
        if (entry is null) return BadRequest("작업 폴더 .mcp.json에도 앱 서버 목록에도 없는 서버입니다.");
        var result = await probe.ProbeAsync(id, name, entry, ws, HttpContext.RequestAborted);
        if (result is null) return BadRequest("이미 확인 중입니다.");
        return new JsonResult(new { ok = result.Ok, tools = result.Tools.Count, error = result.Error });
    }
}
