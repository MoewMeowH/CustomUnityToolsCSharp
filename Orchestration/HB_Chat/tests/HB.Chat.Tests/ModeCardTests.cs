using System.Text.Json.Nodes;
using HB.Chat;

namespace HB.Chat.Tests;

/// <summary>모델 선택창: 모드 목록 읽기 · 카드 처음 값 · 허가 입력 다시 쓰기 · 요청 창 머리 줄.</summary>
public sealed class ModeCardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hbchat-modes-" + Guid.NewGuid().ToString("N"));
    private string Ws => Path.Combine(_dir, "ws");
    private string Srv => Path.Combine(_dir, "srv");

    public ModeCardTests()
    {
        Directory.CreateDirectory(Ws);
        WriteServer(Srv);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>가짜 MCP 서버 폴더 (dll · appsettings.json · modes.json)와 그것을 가리키는 작업 폴더 .mcp.json.</summary>
    public static void WriteServer(string srv, string? modesPath = null, string? ws = null)
    {
        Directory.CreateDirectory(srv);
        File.WriteAllText(Path.Combine(srv, "HB.Orchestration.Mcp.dll"), "");
        File.WriteAllText(Path.Combine(srv, "appsettings.json"), $$"""
            {
              // 주석도 읽는다
              "Orchestration": {
                "ModesPath": {{(modesPath is null ? "\"\"" : System.Text.Json.JsonSerializer.Serialize(modesPath))}},
                "Agents": [
                  { "Name": "gpt_main_sol", "Provider": "openai", "Model": "gpt-6.1-sol" },
                  { "Name": "gemini_main_flash", "Provider": "gemini", "Model": "gemini-3.8-flash", "ReasoningEffort": "High" },
                  { "Name": "codex_cli_sol", "Provider": "codex", "Model": "gpt-6.1-sol", "ReasoningEffort": "medium" },
                  { "Name": "off_agent", "Provider": "openai", "Model": "x", "Enabled": false },
                ]
              }
            }
            """);
        File.WriteAllText(Path.Combine(srv, "modes.json"), """
            {
              "Roles": {},
              "Modes": {
                "design_main": { "Description": "반박 + 논리", "Steps": [ { "Role": "Rebuttal", "Agent": "gpt_main_sol" }, { "Role": "LogicCheck", "Agent": "gemini_main_flash" } ] },
                "design_high": { "Description": "토론", "Flow": "Debate", "Steps": [ { "Role": "Rebuttal", "Agent": "gpt_main_sol" }, { "Role": "Proponent", "Agent": "gemini_main_flash" }, { "Role": "Judge", "Agent": "gpt_main_sol" } ] },
                "review_main": { "Description": "리뷰", "RequireOwnReview": true, "Steps": [ { "Role": "Reviewer", "Agent": "codex_cli_sol", "FallbackAgent": "gpt_main_sol" } ] }
              }
            }
            """);
        if (ws is not null)
            File.WriteAllText(Path.Combine(ws, ".mcp.json"), new JsonObject
            {
                ["mcpServers"] = new JsonObject { ["hb-orchestration"] = new JsonObject { ["command"] = "dotnet", ["args"] = new JsonArray(Path.Combine(srv, "HB.Orchestration.Mcp.dll")) } },
            }.ToJsonString());
    }

    private ModeList Read()
    {
        WriteServer(Srv, ws: Ws);
        return ModeCatalog.Read(Ws, "hb-orchestration");
    }

    [Fact]
    public void Catalog_ReadsModesAndEnabledAgents_FromServerFolder()
    {
        var list = Read();
        Assert.Null(list.Error);
        Assert.Equal(["design_main", "design_high", "review_main"], list.Modes.Select(m => m.Name));
        Assert.Equal(["gpt_main_sol", "gemini_main_flash", "codex_cli_sol"], list.Agents.Select(a => a.Name)); // 꺼진 에이전트는 빠진다
        Assert.Equal("high", list.Agent("gemini_main_flash")!.Reasoning); // 강도는 소문자로
        Assert.Null(list.Agent("gpt_main_sol")!.Reasoning);
        var review = list.Find("REVIEW_MAIN")!;
        Assert.True(review.OwnReview);
        Assert.Equal("gpt_main_sol", review.Steps.Single().Fallback);
        Assert.Equal("Debate", list.Find("design_high")!.Flow);
    }

    [Fact]
    public void Catalog_ProjectFileAndConfiguredPath_FollowServerOrder()
    {
        Read();
        // 작업 폴더 hb-modes.json이 서버 폴더 modes.json보다 앞
        File.WriteAllText(Path.Combine(Ws, "hb-modes.json"), """{"Modes":{"only_here":{"Steps":[{"Role":"R","Agent":"gpt_main_sol"}]}}}""");
        Assert.Equal(["only_here"], ModeCatalog.Read(Ws, "hb-orchestration").Modes.Select(m => m.Name));
        // 설정 ModesPath(서버 폴더 기준)가 가장 앞 — 지정한 파일이 없으면 다른 파일로 넘어가지 않고 오류
        File.WriteAllText(Path.Combine(Srv, "custom.json"), """{"Modes":{"custom":{"Steps":[]}}}""");
        WriteServer(Srv, modesPath: "custom.json");
        Assert.Equal(["custom"], ModeCatalog.Read(Ws, "hb-orchestration").Modes.Select(m => m.Name));
        WriteServer(Srv, modesPath: "missing.json");
        var missing = ModeCatalog.Read(Ws, "hb-orchestration");
        Assert.Empty(missing.Modes);
        Assert.Contains("missing.json", missing.Error);
    }

    [Fact]
    public void Catalog_NoMcpEntryOrNoDll_ReturnsEmptyWithError()
    {
        Assert.Contains(".mcp.json", ModeCatalog.Read(Ws, "hb-orchestration").Error);
        File.WriteAllText(Path.Combine(Ws, ".mcp.json"), """{"mcpServers":{"hb-orchestration":{"command":"dotnet","args":["run","--project","x"]}}}""");
        var list = ModeCatalog.Read(Ws, "hb-orchestration");
        Assert.Empty(list.Modes);
        Assert.Contains("dll", list.Error);
        File.WriteAllText(Path.Combine(Ws, ".mcp.json"), "{ broken");
        Assert.Contains("읽지 못했습니다", ModeCatalog.Read(Ws, "hb-orchestration").Error);
    }

    [Fact]
    public void Catalog_ChangedModesFile_IsReadAgain()
    {
        Read();
        Assert.Equal(3, ModeCatalog.Read(Ws, "hb-orchestration").Modes.Count);
        File.WriteAllText(Path.Combine(Srv, "modes.json"), """{"Modes":{"one":{"Steps":[]},"two":{"Steps":[]},"three":{"Steps":[]},"four":{"Steps":[]}}}""");
        Assert.Equal(4, ModeCatalog.Read(Ws, "hb-orchestration").Modes.Count); // 크기가 달라 캐시를 쓰지 않는다
    }

    [Fact]
    public void Build_GroupsRowsByAgent_AndStartsFromConductorValues()
    {
        var list = Read();
        var card = ModeCard.Build("""{"mode":"design_high","input":"x","reason":"why","agents":["gemini_main_flash=codex_cli_sol"],"reasoning":["*=low","codex_cli_sol=high"]}""", list, null)!;
        Assert.Equal("design_high", card.Mode);
        Assert.Equal("why", card.Reason);
        Assert.Equal(2, card.Rows.Count); // gpt_main_sol을 두 역할이 같이 쓴다 — 에이전트 단위
        Assert.Equal(["Rebuttal", "Judge"], card.Rows[0].Roles);
        Assert.Equal(("gpt_main_sol", "low"), (card.Rows[0].Pick, card.Rows[0].Effort));
        Assert.Equal(("codex_cli_sol", "high"), (card.Rows[1].Pick, card.Rows[1].Effort)); // 강도는 교체 뒤 이름 기준
        Assert.False(card.Prefilled);
    }

    [Fact]
    public void Build_PickForSameMode_OverridesConductorValues_OtherModeIgnored()
    {
        var list = Read();
        var pick = new ModePick("design_main", new Dictionary<string, ModeRowPick> { ["gpt_main_sol"] = new("codex_cli_sol", "xhigh") });
        var card = ModeCard.Build("""{"mode":"design_main","input":"x","agents":["gpt_main_sol=gemini_main_flash"]}""", list, pick)!;
        Assert.True(card.Prefilled);
        Assert.Equal(("codex_cli_sol", "xhigh"), (card.Rows[0].Pick, card.Rows[0].Effort));
        var other = ModeCard.Build("""{"mode":"review_main","input":"x"}""", list, pick)!;
        Assert.False(other.Prefilled);
        Assert.Equal("codex_cli_sol", other.Rows.Single().Pick);
        Assert.Equal("gpt_main_sol", other.Rows.Single().Fallback);
    }

    [Fact]
    public void Build_UnknownModeOrFollowUp_HasNoRows_ApplyKeepsInput()
    {
        var list = Read();
        var unknown = ModeCard.Build("""{"mode":"nope","input":"x"}""", list, null)!;
        Assert.Empty(unknown.Rows);
        Assert.Contains("nope", unknown.Error);
        var follow = ModeCard.Build("""{"mode":"design_main","input":"x","followUp":{"step":1}}""", list, null)!;
        Assert.True(follow.FollowUp);
        Assert.Empty(follow.Rows);
        Assert.Null(follow.Error);
        const string input = """{"mode":"design_main","input":"x","followUp":{"step":1}}""";
        Assert.Equal(input, follow.Apply(input, null));
        Assert.NotNull(follow.Check(new Dictionary<string, ModeRowPick> { ["gpt_main_sol"] = new("gpt_main_sol", null) }));
        Assert.Null(ModeCard.Build("[1]", list, null));
    }

    [Fact]
    public void Build_LongInput_ShowsHeadAndFiles()
    {
        var list = Read();
        var text = new string('가', ModeCard.InputHeadChars + 10);
        var json = new JsonObject
        {
            ["mode"] = "design_main", ["input"] = text, ["images"] = new JsonArray("a.png"),
            ["context"] = new JsonArray(new JsonObject { ["path"] = "src/A.cs", ["startLine"] = 1, ["endLine"] = 9 }),
            ["files"] = new JsonArray("src/A.cs", "docs/b.md"),
        }.ToJsonString();
        var card = ModeCard.Build(json, list, null)!;
        Assert.True(card.InputCut);
        Assert.Equal(ModeCard.InputHeadChars + 1, card.InputHead.Length);
        Assert.Equal(["src/A.cs:1-9", "docs/b.md"], card.Files); // context에 있는 파일은 files에서 겹쳐 보이지 않는다
        Assert.Equal(1, card.Images);
    }

    [Fact]
    public void Apply_RewritesOnlyAgentsAndReasoning()
    {
        var list = Read();
        const string input = """{"mode":"design_high","input":"keep me","context":[{"path":"a.cs","startLine":1,"endLine":2}],"reason":"r","agents":["*=gemini_main_flash"],"reasoning":["*=low"]}""";
        var card = ModeCard.Build(input, list, null)!;
        var picks = new Dictionary<string, ModeRowPick>
        {
            ["gpt_main_sol"] = new("codex_cli_sol", "high"),
            ["gemini_main_flash"] = new("gemini_main_flash", null),
        };
        Assert.Null(card.Check(picks));
        var o = JsonNode.Parse(card.Apply(input, picks))!.AsObject();
        Assert.Equal("keep me", (string?)o["input"]);
        Assert.Equal("r", (string?)o["reason"]);
        Assert.Equal("a.cs", (string?)o["context"]![0]!["path"]);
        Assert.Equal(["gpt_main_sol=codex_cli_sol"], o["agents"]!.AsArray().Select(n => (string)n!));
        Assert.Equal(["codex_cli_sol=high"], o["reasoning"]!.AsArray().Select(n => (string)n!));

        // 모두 원래대로면 두 키가 빠진다 (지휘자가 넣은 '*'도 카드 값으로 바뀐다)
        var back = new Dictionary<string, ModeRowPick> { ["gpt_main_sol"] = new("gpt_main_sol", null), ["gemini_main_flash"] = new("gemini_main_flash", null) };
        var clean = JsonNode.Parse(card.Apply(input, back))!.AsObject();
        Assert.False(clean.ContainsKey("agents"));
        Assert.False(clean.ContainsKey("reasoning"));

        // 고르지 않았거나 처음 값 그대로면 원래 입력 그대로 — '*'도 대신 에이전트에 걸리던 그대로 간다 (785ebb5e97 C3)
        Assert.Equal(input, card.Apply(input, null));
        Assert.Equal(input, card.Apply(input, card.Rows.ToDictionary(r => r.Agent, r => new ModeRowPick(r.Pick, r.Effort))));
    }

    [Fact]
    public void Apply_KeepsConductorEntriesOutsideRows_DropsSwappedNames()
    {
        // 카드 줄이 아닌 이름(대신 에이전트 등)의 값은 남기고, 교체로 빠진 원래 이름의 강도는 버린다 (서버가 쓰지 않는 이름을 거절)
        var list = Read();
        const string input = """{"mode":"review_main","input":"x","reasoning":["codex_cli_sol=low","gpt_main_sol=high"]}""";
        var card = ModeCard.Build(input, list, null)!;
        var o = JsonNode.Parse(card.Apply(input, new Dictionary<string, ModeRowPick> { ["codex_cli_sol"] = new("gemini_main_flash", null) }))!.AsObject();
        Assert.Equal(["codex_cli_sol=gemini_main_flash"], o["agents"]!.AsArray().Select(n => (string)n!));
        Assert.Equal(["gpt_main_sol=high"], o["reasoning"]!.AsArray().Select(n => (string)n!));
    }

    [Fact]
    public void Check_UnchangedRow_SkipsAgentList_ConflictingEffortsRejected()
    {
        var list = Read();
        // 지휘자가 꺼진 이름을 넣었어도 그대로 허가는 된다 — 서버가 다시 검사 (785ebb5e97 C2)
        var card = ModeCard.Build("""{"mode":"design_main","input":"x","agents":["gpt_main_sol=off_agent"],"reasoning":["*=HIGH"]}""", list, null)!;
        Assert.Equal(("off_agent", "high"), (card.Rows[0].Pick, card.Rows[0].Effort)); // 강도는 소문자로
        Assert.Null(card.Check(card.Rows.ToDictionary(r => r.Agent, r => new ModeRowPick(r.Pick, r.Effort))));
        // 두 줄이 같은 에이전트로 가는데 강도가 다르면 거절, 같으면 받음 (C4)
        var design = ModeCard.Build("""{"mode":"design_main","input":"x"}""", list, null)!;
        Assert.Contains("강도가 다릅니다", design.Check(new Dictionary<string, ModeRowPick>
        {
            ["gpt_main_sol"] = new("gpt_main_sol", "low"), ["gemini_main_flash"] = new("gpt_main_sol", "high"),
        }));
        Assert.Null(design.Check(new Dictionary<string, ModeRowPick>
        {
            ["gpt_main_sol"] = new("gpt_main_sol", "high"), ["gemini_main_flash"] = new("gpt_main_sol", "high"),
        }));
    }

    [Fact]
    public void Catalog_EntryEnvRelativePath_ResolvesUnderWorkspace()
    {
        // 785ebb5e97 C5: HB_MODES_PATH 상대 경로는 서버 현재 폴더(= CLI 작업 폴더) 기준
        WriteServer(Srv);
        Directory.CreateDirectory(Path.Combine(Ws, "cfg"));
        File.WriteAllText(Path.Combine(Ws, "cfg", "m.json"), """{"Modes":{"from_env":{"Steps":[]}}}""");
        File.WriteAllText(Path.Combine(Ws, ".mcp.json"), new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["hb-orchestration"] = new JsonObject
                {
                    ["command"] = "dotnet", ["args"] = new JsonArray(Path.Combine(Srv, "HB.Orchestration.Mcp.dll")),
                    ["env"] = new JsonObject { ["HB_MODES_PATH"] = "cfg/m.json" },
                },
            },
        }.ToJsonString());
        Assert.Equal(["from_env"], ModeCatalog.Read(Ws, "hb-orchestration").Modes.Select(m => m.Name));
    }

    [Fact]
    public void Rules_ModeCardNoteOnlyWhenAsked()
    {
        // 785ebb5e97 C7: 기본 서버가 붙은 보통 방에만
        Assert.DoesNotContain(ModeCard.HeaderMarker, RoomRules.For(PermissionLevel.Read));
        Assert.Contains(ModeCard.HeaderMarker, RoomRules.For(PermissionLevel.Read, modeCard: true));
        Assert.DoesNotContain(ModeCard.HeaderMarker, RoomRules.For(PermissionLevel.Chat, modeCard: true));
    }

    [Fact]
    public void Check_RejectsUnknownRowAgentOrEffort()
    {
        var card = ModeCard.Build("""{"mode":"design_main","input":"x"}""", Read(), null)!;
        Assert.Contains("쓰지 않습니다", card.Check(new Dictionary<string, ModeRowPick> { ["codex_cli_sol"] = new("gpt_main_sol", null) }));
        Assert.Contains("켜진 에이전트", card.Check(new Dictionary<string, ModeRowPick> { ["gpt_main_sol"] = new("off_agent", null) }));
        Assert.Contains("강도", card.Check(new Dictionary<string, ModeRowPick> { ["gpt_main_sol"] = new("gpt_main_sol", "max") }));
        Assert.Null(card.Check(null));
    }

    [Fact]
    public void Header_ListsOnlyChanges_AndParsePicksRoundTrips()
    {
        var list = Read();
        var mode = list.Find("design_high")!;
        var rows = ModeCard.ParsePicks("""{"gpt_main_sol":{"agent":"codex_cli_sol","effort":"high"},"gemini_main_flash":{"agent":"gemini_main_flash","effort":""}}""")!;
        Assert.Null(ModeCard.CheckPick(mode, list, rows));
        Assert.Equal("[외부 요청] mode=design_high · agents=gpt_main_sol=codex_cli_sol · reasoning=codex_cli_sol=high", ModeCard.Header(mode, rows));
        Assert.Equal("[외부 요청] mode=design_main", ModeCard.Header(list.Find("design_main")!, new Dictionary<string, ModeRowPick>()));
        Assert.Empty(ModeCard.ParsePicks(null)!);
        Assert.Null(ModeCard.ParsePicks("[1]"));
        Assert.Null(ModeCard.ParsePicks("""{"a":{"effort":"high"}}"""));
        Assert.NotNull(ModeCard.CheckPick(mode, list, new Dictionary<string, ModeRowPick> { ["codex_cli_sol"] = new("gpt_main_sol", null) }));
    }

    [Fact]
    public void Label_ShowsChangedRowsOnly()
    {
        var card = ModeCard.Build("""{"mode":"design_main","input":"x"}""", Read(), null)!;
        Assert.Equal("허용", RoomHub.ModeLabel(card, null));
        Assert.Equal("허용 (gpt_main_sol→codex_cli_sol · high)",
            RoomHub.ModeLabel(card, new Dictionary<string, ModeRowPick> { ["gpt_main_sol"] = new("codex_cli_sol", "high"), ["gemini_main_flash"] = new("gemini_main_flash", null) }));
    }
}
