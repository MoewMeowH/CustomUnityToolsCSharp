using HB.Orchestration.Agents;
using HB.Orchestration.Agents.Providers;
using HB.Orchestration.Shared;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace HB.Orchestration.Tests.Fakes;

/// <summary>
/// 임시 폴더에 modes.json · 지침 · 서버 설정 · 호출 기록을 두고 ModeRunner를 실제 경로대로 돌린다.
/// 에이전트는 가짜 클라이언트 하나(fake)이고, 세션 상태 파일도 임시 폴더에 쓴다.
/// </summary>
public sealed class ModeRunnerHarness : IDisposable
{
    public const string DefaultModes = """
        {
          "Roles": {
            "R":   { "Prompt": "역할 R" },
            "NoG": { "Prompt": "역할 N", "UseGuidelines": false }
          },
          "Modes": {
            "seq":  { "Steps": [ { "Role": "R", "Agent": "a1" } ] },
            "rev":  { "RequireOwnReview": true, "Steps": [ { "Role": "R", "Agent": "a1" }, { "Role": "R", "Agent": "a1" } ] },
            "crev": { "RequireOwnReview": true, "Flow": "Concurrent", "Steps": [ { "Role": "R", "Agent": "a1" }, { "Role": "R", "Agent": "a1" } ] },
            "two":  { "Steps": [ { "Role": "R", "Agent": "a1" }, { "Role": "NoG", "Agent": "a1" } ] },
            "noG":  { "Steps": [ { "Role": "NoG", "Agent": "a1" } ] },
            "conc": { "Flow": "Concurrent", "Steps": [ { "Role": "R", "Agent": "a1" }, { "Role": "R", "Agent": "a1" } ] },
            "deb":  { "Flow": "Debate", "MaxRounds": 1, "Steps": [ { "Role": "R", "Agent": "a1" }, { "Role": "R", "Agent": "a1" }, { "Role": "R", "Agent": "a1" } ] }
          }
        }
        """;

    public string Root { get; } = Directory.CreateTempSubdirectory("hb-orch-run-").FullName;
    public string ModesPath => Path.Combine(Root, "modes.json");
    public string GuidelinesPath => Path.Combine(Root, "REVIEW_GUIDELINES.md");
    public string SettingsPath => Path.Combine(Root, "appsettings.json");
    public string LogPath => Path.Combine(Root, "calls.jsonl");
    public string DefaultBlobFolder => Path.Combine(Root, "blobs");

    public OrchestrationOptions Options { get; }
    public ScriptedChatClient Client { get; } = new(ScriptedChatClient.Reply("응답"));
    public SessionTracker Sessions { get; }

    /// <summary>true면 설정 다시 읽기가 실패한다 (이전 설정 유지 경로).</summary>
    public bool FailLoad { get; set; }
    public ModeRunner Runner { get; }

    /// <param name="providerType">가짜 제공사의 Type. 제공사 Type을 보는 검사(웹검색 = openai-responses)를 시험할 때 바꾼다.</param>
    public ModeRunnerHarness(string? modesJson = null, string guidelines = "# 지침\n- 한글 규칙\n", string settings = """{ "Orchestration": { "Providers": { "fake": { "ApiKeyEnv": "FAKE_KEY" } } } }""",
        string providerType = "fake")
    {
        File.WriteAllText(ModesPath, modesJson ?? DefaultModes);
        if (guidelines is not null) File.WriteAllText(GuidelinesPath, guidelines);
        File.WriteAllText(SettingsPath, settings);

        Options = new OrchestrationOptions
        {
            Providers = new(StringComparer.OrdinalIgnoreCase) { ["fake"] = new ProviderOptions { Type = providerType } },
            Agents = [new AgentOptions { Name = "a1", Provider = "fake", Model = "m", MaxAttempts = 1 }],
            ModesPath = ModesPath,
            GuidelinesPath = GuidelinesPath,
        };

        var catalog = new AgentCatalog(
            new OrchestrationSource(() => FailLoad ? throw new InvalidOperationException("설정 깨짐") : Options, SettingsPath),
            [new FakeProvider(Client, providerType)], NullLogger<AgentCatalog>.Instance);
        Sessions = new SessionTracker(NullLogger<SessionTracker>.Instance, Path.Combine(Root, "sessions"));
        var agentRunner = new AgentRunner(NullLogger<AgentRunner>.Instance, (_, _) => Task.CompletedTask, LogPath);
        Runner = new ModeRunner(new ModeStore(catalog), catalog, agentRunner, Sessions);
    }

    public const string DefaultReason = "테스트 이유";

    public Task<string> Run(string mode, string input = "요청", string? reason = DefaultReason, string? ownReview = null) =>
        Runner.RunAsync(mode, input, null, null, null, null, reason, ownReview, CancellationToken.None);

    public List<CallRecord> Records() => CallLog.ReadAll(LogPath).OrderBy(r => r.Timestamp).ThenBy(r => r.Step).ToList();

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class FakeProvider(IChatClient client, string type) : IChatClientProvider
    {
        public string Type => type;
        public IChatClient Create(string providerName, ProviderOptions options, AgentOptions agent) => client;
    }
}
