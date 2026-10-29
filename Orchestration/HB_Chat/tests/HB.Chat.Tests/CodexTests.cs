using System.Diagnostics;
using HB.Chat;
using Microsoft.Extensions.Options;

namespace HB.Chat.Tests;

/// <summary>
/// 단톡방 ② Codex 구성원 (계획서 2026-10-29_HB_Chat-단톡방): 인자(읽기 전용 · 모델 · 강도 · 지시 · resume) · TOML 이스케이프 · 모델 목록 ·
/// 켤 작업 폴더 설정 · 단톡방에서 Codex 구성원 부르기(새 스레드 → 이어 가기) · 실패 · 과정 기록. 진짜 Codex 대신 TestData/fake-codex.ps1.
/// </summary>
public sealed class CodexTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hbchat-codex-" + Guid.NewGuid().ToString("N"));
    private readonly RoomStore _store;
    private readonly RoomHub _hub;
    private readonly GroupLog _log;
    private readonly CodexSettings _settings;
    private readonly GroupChat _groups;
    private readonly List<IReadOnlyList<string>> _codexArgs = [];
    private readonly ChatOptions _options;

    public CodexTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "ws"));
        Directory.CreateDirectory(Path.Combine(_dir, "ws2"));
        var options = _options = new ChatOptions
        {
            DataDir = Path.Combine(_dir, "data"),
            ClaudeProjectsDir = Path.Combine(_dir, "projects"),
            Workspaces = new(StringComparer.OrdinalIgnoreCase) { ["ws"] = Path.Combine(_dir, "ws"), ["other"] = Path.Combine(_dir, "ws") },
        };
        _store = new RoomStore(Path.Combine(_dir, "data", "rooms.json"));
        var codex = Path.Combine(AppContext.BaseDirectory, "TestData", "fake-codex.ps1");
        var claude = Path.Combine(AppContext.BaseDirectory, "TestData", "fake-claude.ps1");
        _hub = new RoomHub(_store, new ChatTranscripts(() => Path.Combine(_dir, "projects")), Options.Create(options))
        {
            ProcessFactory = (room, spec, workspace) => new ProcessSpec("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", claude,
                spec.SessionId, Path.Combine(_dir, "tasks"), Path.Combine(_dir, "projects", "F--ws", spec.SessionId + ".jsonl")], workspace),
            CodexProcessFactory = (room, args, workspace) =>
            {
                lock (_codexArgs) _codexArgs.Add(args);
                // 인자는 base64 JSON 한 덩어리로 — PowerShell -File이 -C · -m을 제 매개변수로 읽어 실패했다
                var packed = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(args)));
                return new ProcessSpec("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", codex, packed], workspace);
            },
        };
        _log = new GroupLog(() => Path.Combine(_dir, "data"));
        _settings = new CodexSettings(() => Path.Combine(_dir, "data"), options.WorkspacePath); // 앱과 같이 경로에 묶음
        _groups = new GroupChat(_store, _hub, _log, _settings) { CodexModelList = () => [new CodexModels.Model("gpt-test", "GPT-Test", "medium", ["low", "medium"])] };
    }

    public void Dispose()
    {
        _hub.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private async Task<GroupEntry> AnswerAsync(string groupId, string memberName, int after)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < Wait)
        {
            var member = _store.Get(groupId)!.Members.First(m => m.Name == memberName);
            if (_log.Read(groupId).FirstOrDefault(e => e.Kind == GroupEntryKind.Member && e.MemberId == member.Id && e.Line > after) is { } answer)
            {
                while (sw.Elapsed < Wait && (_hub.IsBusy(member.RoomId) || _groups.Waiting(member.RoomId))) await Task.Delay(50);
                return answer;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"{memberName}의 답이 오지 않음");
    }

    [Fact]
    public void Args_ReadOnly_NewThreadHasInstructions_ResumeHasNot()
    {
        var first = CodexRunner.Args(@"C:\ws", "gpt-test", "high", "규칙 \"따옴표\"\n둘째 줄 \\ 끝", null);
        // 사용자 설정(MCP 서버 · 훅)을 읽지 않고 샌드박스를 직접 고른다 · 셸 샌드박스 밖 도구는 끈다 (sol 리뷰 ②-P1)
        Assert.Equal(["exec", "--json", "--ignore-user-config", "-c", "windows.sandbox=\"unelevated\"", "-c", "web_search=\"disabled\"",
            "-s", "read-only", "--skip-git-repo-check", "-C", @"C:\ws"], first.Take(12));
        Assert.Equal(7, CodexRunner.DisabledFeatures.Count);
        foreach (var feature in CodexRunner.DisabledFeatures) // 목록 전부 (3cc5ce85df C3)
            Assert.Contains(first.Select((a, i) => (a, i)), p => p.a == feature && first[p.i - 1] == "--disable");
        Assert.Contains("gpt-test", first);
        Assert.Contains("model_reasoning_effort=\"high\"", first);
        Assert.Contains("developer_instructions=\"규칙 \\\"따옴표\\\"\\n둘째 줄 \\\\ 끝\"", first);
        Assert.Equal("-", first[^1]);
        Assert.DoesNotContain("resume", first);

        var next = CodexRunner.Args(@"C:\ws", null, null, "규칙", "thread-1");
        Assert.Equal(["resume", "thread-1", "-"], next.TakeLast(3));
        Assert.Contains("--ignore-user-config", next);
        Assert.DoesNotContain(next, a => a.StartsWith("developer_instructions", StringComparison.Ordinal)); // resume에서는 먹지 않음 (0단계 실측)
        Assert.DoesNotContain("-m", next);
        Assert.Equal("\"\\u0001\\t\"", CodexRunner.TomlString("\u0001\t"));
    }

    [Fact]
    public void Models_ReadsVisibleListByPriority_Settings_SaveAndAllow()
    {
        var cache = Path.Combine(_dir, "models_cache.json");
        File.WriteAllText(cache, """
            {"models":[
              {"slug":"b","display_name":"B","priority":9,"visibility":"list","default_reasoning_level":"medium","supported_reasoning_levels":[{"effort":"low"},{"effort":"high"}]},
              {"slug":"hidden","priority":1,"visibility":"hide"},
              {"slug":"a","display_name":"A","priority":5,"visibility":"list"}]}
            """);
        var models = CodexModels.Available(cache);
        Assert.Equal(["a", "b"], models.Select(m => m.Slug));
        Assert.Equal(["low", "high"], models[1].Efforts);
        Assert.Empty(CodexModels.Available(Path.Combine(_dir, "none.json")));

        Assert.Empty(_settings.Workspaces());
        Assert.False(_settings.Allowed("ws"));
        _settings.Save(["WS", "ws"]);
        Assert.True(_settings.Allowed("ws"));
        Assert.Single(_settings.Workspaces());

        // 같은 이름이 다른 경로로 바뀌면 다시 고르기 전까지 꺼짐 (59063d4106 C5 · Codex 리뷰)
        _options.Workspaces["ws"] = Path.Combine(_dir, "ws2");
        Assert.False(_settings.Allowed("ws"));
        Assert.Empty(_settings.Workspaces());
        _settings.Save(["ws"]);
        Assert.True(_settings.Allowed("ws"));
    }

    [Fact]
    public void Slim_DropsCommandOutput_KeepsCommandAndExitCode()
    {
        var line = """{"type":"item.completed","item":{"id":"i","type":"command_execution","command":"type secret.txt","aggregated_output":"TOKEN=abc","exit_code":0}}""";
        var slim = CodexLog.Slim(line);
        Assert.DoesNotContain("TOKEN", slim);
        Assert.Contains("type secret.txt", slim);
        Assert.Contains("\"exit_code\":0", slim);
        Assert.Equal("not json", CodexLog.Slim("not json"));
    }

    [Fact]
    public void Create_CodexOnlyWhereAllowed_KnownModelAndEffort()
    {
        GroupMemberSpec[] members = [new("A", "위험"), new("X", "반박", RoomEngines.Codex, "gpt-test", "low")];
        Assert.Contains("Codex 구성원을 켜지 않았습니다", _groups.Create("ws", PermissionLevel.Read, "opus", null, "팀", members).Error);
        _settings.Save(["ws"]);
        Assert.Contains("목록에 없습니다", _groups.Create("ws", PermissionLevel.Read, "opus", null, "팀", [members[0], members[1] with { Model = "nope" }]).Error);
        Assert.Contains("추론 강도", _groups.Create("ws", PermissionLevel.Read, "opus", null, "팀", [members[0], members[1] with { Effort = "huge" }]).Error);
        // 모델이 받지 않는 강도 (gpt-test는 low · medium만 — 59063d4106 C2)
        Assert.Contains("받지 않습니다", _groups.Create("ws", PermissionLevel.Read, "opus", null, "팀", [members[0], members[1] with { Effort = "high" }]).Error);
        // Codex 구성원은 읽기 단계만 — 대화(59063d4106 C1) · 편집 · 명령(승인 카드 없음, ⑤)을 고르면 거절
        Assert.Contains("읽기 단계", _groups.Create("ws", PermissionLevel.Read, "opus", null, "팀", [members[0], members[1] with { Level = PermissionLevel.Chat }]).Error);
        Assert.Contains("읽기 단계", _groups.Create("ws", PermissionLevel.Read, "opus", null, "팀", [members[0], members[1] with { Level = PermissionLevel.Edit }]).Error);
        // 칸이 비면 기본 단계가 대화여도 Codex는 읽기 (⑤ — 구성원마다 단계)
        var (chatDefault, chatError) = _groups.Create("ws", PermissionLevel.Chat, "opus", null, "팀", members);
        Assert.Null(chatError);
        Assert.Equal((PermissionLevel.Chat, PermissionLevel.Read), (_store.Get(chatDefault!.Members[0].RoomId)!.Level, _store.Get(chatDefault.Members[1].RoomId)!.Level));
        Assert.Equal(PermissionLevel.Read, chatDefault.Level); // 단톡방 단계 = 구성원 중 가장 높은 단계
        Assert.Contains("엔진", _groups.Create("ws", PermissionLevel.Read, "opus", null, "팀", [members[0], members[1] with { Engine = "gemini" }]).Error);

        var (group, error) = _groups.Create("ws", PermissionLevel.Read, "opus", null, "팀", members);
        Assert.Null(error);
        var x = _store.Get(group!.Members[1].RoomId)!;
        Assert.Equal((RoomEngines.Codex, "gpt-test", "low"), (x.Engine, x.Model, x.Effort));
        var a = _store.Get(group.Members[0].RoomId)!;
        Assert.Equal((null, "opus"), (a.Engine, a.Model));
        Assert.Equal(RoomEngines.Codex, group.Members[1].Engine);
    }

    [Fact]
    public async Task CodexMember_NewThreadThenResume_LogsProcess()
    {
        _settings.Save(["ws"]);
        var (group, _) = _groups.Create("ws", PermissionLevel.Read, "opus", null, "팀",
            [new GroupMemberSpec("A", "위험"), new GroupMemberSpec("X", "반박", RoomEngines.Codex, "gpt-test", "low")]);

        Assert.Null(_groups.Send(group!.Id, "@X 첫 말"));
        var first = await AnswerAsync(group.Id, "X", -1);
        Assert.False(first.Failed);
        Assert.Contains("resume=False instr=True model=gpt-test effort=low reminders=0 delta=0 last=@X 첫 말", first.Text);
        var thread = _store.Get(group.Members[1].RoomId)!.CodexThread;
        Assert.NotNull(thread);

        // 둘째 말: 같은 스레드를 이어 가고 지시는 다시 주지 않는다. A의 말이 없어 앞 대화는 X 자신의 답뿐 — 본 줄 수가 올라가 빈 블록
        Assert.Null(_groups.Send(group.Id, "@X 다시"));
        var second = await AnswerAsync(group.Id, "X", first.Line);
        Assert.Contains("resume=True instr=False", second.Text);
        Assert.Equal(thread, _store.Get(group.Members[1].RoomId)!.CodexThread);
        lock (_codexArgs) Assert.Equal(["resume", thread!, "-"], _codexArgs[^1].TakeLast(3));
        // 단톡방 비용 합계용 토큰 누적 (가짜 codex는 턴마다 입력 100 · 출력 7 — 2026-10-29_사용량-한도-표시, b40d9efb00 C3)
        Assert.Equal((200L, 14L), (_store.Get(group.Members[1].RoomId)!.CodexInputTokens, _store.Get(group.Members[1].RoomId)!.CodexOutputTokens));

        // 과정 기록: 보낸 말 · 명령 · 답 · 토큰 (개발 중 기능 경고는 빼고)
        var steps = new CodexLog(() => Path.Combine(_dir, "data")).Read(group.Members[1].RoomId);
        Assert.Equal(["user", "command", "answer", "turn", "user", "command", "answer", "turn"], steps.Select(s => s.Kind));
        Assert.DoesNotContain(steps, s => s.Text.Contains("Under-development"));
    }

    [Fact]
    public async Task CodexMember_Failure_And_SettingTurnedOff()
    {
        _settings.Save(["ws"]);
        var (group, _) = _groups.Create("ws", PermissionLevel.Read, "opus", null, "팀", [new GroupMemberSpec("A", ""), new GroupMemberSpec("X", "", RoomEngines.Codex)]);
        Assert.Null(_groups.Send(group!.Id, "@X fail"));
        var failed = await AnswerAsync(group.Id, "X", -1);
        Assert.True(failed.Failed);
        Assert.Equal("(실패: boom)", failed.Text);
        Assert.Equal("", _store.Get(group.Members[1].RoomId)!.Model); // 빈 모델 = Codex 기본 (-m 없음)
        lock (_codexArgs) Assert.DoesNotContain("-m", _codexArgs[0]);

        // 설정에서 끈 뒤에는 실행 전에 막힘
        _settings.Save([]);
        Assert.Null(_groups.Send(group.Id, "@X 다시"));
        var blocked = await AnswerAsync(group.Id, "X", failed.Line);
        Assert.Contains("Codex 구성원을 켜지 않았습니다", blocked.Text);
        lock (_codexArgs) Assert.Single(_codexArgs);
    }

    /// <summary>중지 · 규칙 바뀜 안내 · 사라진 스레드 (59063d4106 C8 · C3).</summary>
    [Fact]
    public async Task CodexMember_Stop_RulesChanged_LostThread()
    {
        _settings.Save(["ws"]);
        var (group, _) = _groups.Create("ws", PermissionLevel.Read, "opus", null, "팀", [new GroupMemberSpec("A", ""), new GroupMemberSpec("X", "", RoomEngines.Codex)]);
        var x = group!.Members[1];

        // 중지: 프로세스 트리를 끝내고 "(중지됨)"
        Assert.Null(_groups.Send(group.Id, "@X sleep"));
        var sw = Stopwatch.StartNew();
        while (!_hub.IsBusy(x.RoomId) && sw.Elapsed < Wait) await Task.Delay(50);
        await Task.Delay(500); // 가짜 codex가 뜰 때까지
        _groups.Stop(group.Id);
        var stopped = await AnswerAsync(group.Id, "X", -1);
        Assert.Equal("(중지됨)", stopped.Text);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20)); // 60초 잠을 기다리지 않음

        // 정상 한 턴 → 규칙 지문 저장
        Assert.Null(_groups.Send(group.Id, "@X 하나"));
        var one = await AnswerAsync(group.Id, "X", stopped.Line);
        var hash = _store.Get(x.RoomId)!.RulesHash;
        Assert.NotNull(hash);

        // 단톡방 이름이 바뀌면 구성원 규칙(GroupRules)이 바뀜 → 이어 가는 스레드에는 이번 말 앞에 "규칙 바뀜" 안내 하나
        _store.Update(group.Id, r => r with { Title = "새 팀" });
        Assert.Null(_groups.Send(group.Id, "@X 둘"));
        var two = await AnswerAsync(group.Id, "X", one.Line);
        Assert.Contains("resume=True instr=False", two.Text);
        Assert.Contains("reminders=1", two.Text);
        Assert.NotEqual(hash, _store.Get(x.RoomId)!.RulesHash);

        // 이어 가던 스레드를 Codex가 못 찾음 → 실패 한 줄 + 스레드를 잊고 다음 말은 새 대화(지시 다시)
        _store.Update(x.RoomId, r => r with { CodexThread = "missing" });
        Assert.Null(_groups.Send(group.Id, "@X 셋"));
        var lost = await AnswerAsync(group.Id, "X", two.Line);
        Assert.True(lost.Failed);
        Assert.Contains("no rollout found", lost.Text);
        Assert.Contains("새 대화로 시작", lost.Text);
        Assert.Null(_store.Get(x.RoomId)!.CodexThread);
        Assert.Null(_groups.Send(group.Id, "@X 넷"));
        var fresh = await AnswerAsync(group.Id, "X", lost.Line);
        Assert.Contains("resume=False instr=True", fresh.Text);
        // 새 대화라 앞서 본 단톡방 대화를 처음부터 다시 받는다 — 본 줄 수(SeenUpTo)는 이미 앞이었다 (sol 리뷰 ②-P2)
        Assert.Contains("reminders=1", fresh.Text);
        Assert.Contains($"delta={lost.Line + 1} ", fresh.Text); // 0 ~ lost.Line 줄 전부 (이번 말 '넷'은 안내 밖)
    }

    /// <summary>스레드가 시작되기 전 실패(설정 오류 등)는 이어 가던 스레드를 잊지 않는다 — "no rollout found"일 때만 (sol 리뷰 ②-P2).</summary>
    [Fact]
    public async Task CodexMember_CrashBeforeThread_KeepsThread()
    {
        _settings.Save(["ws"]);
        var (group, _) = _groups.Create("ws", PermissionLevel.Read, "opus", null, "팀", [new GroupMemberSpec("A", ""), new GroupMemberSpec("X", "", RoomEngines.Codex)]);
        var x = group!.Members[1];
        Assert.Null(_groups.Send(group.Id, "@X 하나"));
        var one = await AnswerAsync(group.Id, "X", -1);
        var thread = _store.Get(x.RoomId)!.CodexThread;
        Assert.NotNull(thread);

        Assert.Null(_groups.Send(group.Id, "@X crash"));
        var crashed = await AnswerAsync(group.Id, "X", one.Line);
        Assert.True(crashed.Failed);
        Assert.Contains("failed to load config", crashed.Text);
        Assert.DoesNotContain("새 대화로 시작", crashed.Text);
        Assert.Equal(thread, _store.Get(x.RoomId)!.CodexThread);

        // 말이 가지 않았으니 본 줄 수를 되돌린다 — 다음 말에 그 말(crash)과 실패 줄을 다시 붙인다 (Codex sol 리뷰 C1)
        Assert.Equal(one.ReplyTo + 1, _store.Get(group.Id)!.Members[1].SeenUpTo);
        Assert.Null(_groups.Send(group.Id, "@X 셋"));
        var next = await AnswerAsync(group.Id, "X", crashed.Line);
        Assert.Contains("resume=True", next.Text);
        Assert.Contains("delta=1 ", next.Text); // 실패한 말(crash) — 자기 답 · 실패 줄은 빠짐 (③)
    }

    /// <summary>
    /// codex가 끝난 뒤 자손이 출력 파이프를 물고 남으면 턴이 상한까지 막혔다 — 잠깐 뒤 Job째 끝내 바로 끝나고 자손도 남지 않는다 (sol 리뷰 ②-P2).
    /// </summary>
    [Fact]
    public async Task CodexMember_ChildHoldingOutput_EndsTurnAndKillsChild()
    {
        _settings.Save(["ws"]);
        var (group, _) = _groups.Create("ws", PermissionLevel.Read, "opus", null, "팀", [new GroupMemberSpec("A", ""), new GroupMemberSpec("X", "", RoomEngines.Codex)]);
        var started = DateTime.Now;
        var sw = Stopwatch.StartNew();
        Assert.Null(_groups.Send(group!.Id, "@X orphan"));
        var answer = await AnswerAsync(group.Id, "X", -1); // 30초 상한 — 자손(ping 40초)을 기다리면 넘는다
        Assert.False(answer.Failed);
        Assert.Contains("codex: resume=False", answer.Text);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"턴이 {sw.Elapsed.TotalSeconds:0}초 걸림");
        static bool Mine(Process p, DateTime since)
        {
            try { return !p.HasExited && p.StartTime >= since.AddSeconds(-1); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
        }
        Assert.DoesNotContain(Process.GetProcessesByName("PING"), p => Mine(p, started));
    }
}
