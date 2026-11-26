using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HBLint.Core.Generation;

namespace HBLint.App.Tests;

public class RuleApiTests
{
    /// <summary>HB_Lint/samples — 출력 폴더가 어디든(dotnet test --output) HB_Lint.slnx를 위로 찾아 올라간다.</summary>
    private static readonly string Samples = FindSamples();

    private static string FindSamples()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "HB_Lint.slnx")))
                return Path.Combine(dir.FullName, "samples");
        }
        // 저장소 밖 출력이면 소스 파일 자리에서
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SourcePath())!, "..", "..", "samples"));
    }

    private static string SourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    /// <summary>samples/의 규칙 3개를 복사한 폴더를 연 앱.</summary>
    private static async Task<(TempDir Dir, TestApp App, string Rules)> Open()
    {
        var dir = new TempDir();
        var rules = Path.Combine(dir.Path, "rules");
        Directory.CreateDirectory(rules);
        foreach (var file in Directory.GetFiles(Samples, RuleSet.FilePattern))
            File.Copy(file, Path.Combine(rules, Path.GetFileName(file)));
        var app = await TestApp.Start(dir);
        await app.Open(rules, Path.Combine(dir.Path, "out", "LintRules.g.cs"));
        return (dir, app, rules);
    }

    private static StringContent Body(string text) => new(text, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    [Fact]
    public async Task 폴더_규칙_목록과_다음_id()
    {
        var (dir, app, _) = await Open();
        using (dir)
        await using (app)
        {
            var json = await Json(await app.Client.GetAsync("/api/rules"));

            Assert.Equal(3, json.GetProperty("rules").GetArrayLength());
            Assert.Equal("CLL0004", json.GetProperty("nextId").GetString());
        }
    }

    [Fact]
    public void 목록은_id_숫자_순이고_id가_없는_파일은_뒤()
    {
        using var dir = new TempDir();
        // 파일 이름 글자 순(a10 · a2 · a9 · b · z)과 id 순이 다르게
        void Rule(string file, string? id) =>
            File.WriteAllText(Path.Combine(dir.Path, file), id == null ? "{ \"version\": 1, \"nodes\": {} }" : $$"""{ "version": 1, "id": "{{id}}", "nodes": {} }""");
        Rule("a10.hblint.json", "CLL0010");
        Rule("a2.hblint.json", "CLL0002");
        Rule("a9.hblint.json", "CLL0100");
        Rule("b.hblint.json", null);
        Rule("z.hblint.json", "CLL0001");
        Rule("y.hblint.json", "XYZ");

        var list = new RuleFolder(dir.Path, Path.Combine(dir.Path, "x.cs")).List();

        Assert.Equal(["z", "a2", "a10", "a9", "b", "y"], list.Select(r => r.File.Replace(".hblint.json", "")));
    }

    [Fact]
    public async Task 공통_설정_읽기_쓰기와_생성에_합쳐짐()
    {
        var (dir, app, rules) = await Open();
        using (dir)
        await using (app)
        {
            var empty = await Json(await app.Client.GetAsync("/api/settings"));
            Assert.False(empty.GetProperty("exists").GetBoolean());
            Assert.True(empty.GetProperty("ok").GetBoolean());

            var saved = await Json(await app.Client.PutAsJsonAsync("/api/settings",
                new { ignoreAssemblies = new[] { " Assembly-CSharp-firstpass ", "", "Assembly-CSharp-firstpass" }, ignorePaths = new[] { "Plugins/Demigiant/**" } }));
            Assert.True(saved.GetProperty("exists").GetBoolean());
            Assert.Equal(["Assembly-CSharp-firstpass"], saved.GetProperty("ignoreAssemblies").EnumerateArray().Select(e => e.GetString()));
            Assert.True(File.Exists(Path.Combine(rules, RuleSettings.FileName)));

            // 배열 안 null은 빈 항목처럼 뺀다
            var withNull = await app.Client.PutAsync("/api/settings", new StringContent("""{ "ignoreAssemblies": [null, "Assembly-CSharp-firstpass"], "ignorePaths": ["Plugins/Demigiant/**"] }""", Encoding.UTF8, "application/json"));
            Assert.True(withNull.IsSuccessStatusCode, await withNull.Content.ReadAsStringAsync());

            // 잘못된 경로 패턴은 쓰지 않는다
            var bad = await app.Client.PutAsJsonAsync("/api/settings", new { ignoreAssemblies = Array.Empty<string>(), ignorePaths = new[] { "a//b" } });
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.Contains("Plugins/Demigiant/**", File.ReadAllText(Path.Combine(rules, RuleSettings.FileName)));

            var generate = await Json(await app.Client.PostAsync("/api/generate", null));
            Assert.True(generate.GetProperty("ok").GetBoolean());
            Assert.Contains("Assembly-CSharp-firstpass", File.ReadAllText(Path.Combine(dir.Path, "out", "LintRules.g.cs")));
        }
    }

    [Fact]
    public async Task 공통_설정에_다시_넣을_것이_없으면_검증_경고()
    {
        var (dir, app, _) = await Open();
        using (dir)
        await using (app)
        {
            const string rule = """{ "version": 1, "id": "CLL0050", "includeAssemblies": ["DOTween.Modules"], "nodes": { "n": { "kind": "sites.new", "from": "scope" }, "r": { "kind": "report", "from": "n", "message": "m" } } }""";

            var before = await Json(await app.Client.PostAsync("/api/validate", Body(rule)));
            Assert.Contains(before.GetProperty("errors").EnumerateArray(), e => e.GetProperty("message").GetString()!.Contains("includeAssemblies를 적었지만"));

            await Json(await app.Client.PutAsJsonAsync("/api/settings", new { ignoreAssemblies = new[] { "DOTween*" }, ignorePaths = Array.Empty<string>() }));
            var after = await Json(await app.Client.PostAsync("/api/validate", Body(rule)));
            Assert.DoesNotContain(after.GetProperty("errors").EnumerateArray(), e => e.GetProperty("message").GetString()!.Contains("includeAssemblies를 적었지만"));
        }
    }

    [Fact]
    public async Task 최근_목록에서_지우기()
    {
        var (dir, app, rules) = await Open();
        using (dir)
        await using (app)
        {
            var other = Path.Combine(dir.Path, "other");
            Directory.CreateDirectory(other);
            await app.Open(other, Path.Combine(dir.Path, "out2", "LintRules.g.cs"));
            var state = await Json(await app.Client.GetAsync("/api/state"));
            Assert.Equal(2, state.GetProperty("recent").GetArrayLength());

            var after = await Json(await app.Client.PostAsJsonAsync("/api/recent/forget", new { rules }));

            Assert.Equal([other], after.GetProperty("recent").EnumerateArray().Select(r => r.GetProperty("rules").GetString()));
            Assert.True(Directory.Exists(rules)); // 폴더는 그대로
        }
    }

    [Fact]
    public void 다음_id는_빈_번호를_채우지_않음()
    {
        var folder = new RuleFolder(Path.GetTempPath(), "x.cs");
        var rules = new[] { new RuleSummary("a", "CLL0001", null, 0, 0), new RuleSummary("b", "CLL0009", null, 0, 0), new RuleSummary("c", null, null, 1, 0) };

        Assert.Equal("CLL0010", folder.NextId(rules));
        Assert.Null(folder.NextId([new RuleSummary("z", "CLL0200", null, 0, 0)]));
    }

    [Fact]
    public async Task 그대로_저장하면_바이트까지_같음()
    {
        var (dir, app, rules) = await Open();
        using (dir)
        await using (app)
        {
            foreach (var path in Directory.GetFiles(rules))
            {
                var file = Path.GetFileName(path);
                var before = File.ReadAllBytes(path);
                var text = (await Json(await app.Client.GetAsync("/api/rules/" + file))).GetProperty("text").GetString()!;

                var saved = await Json(await app.Client.PutAsync("/api/rules/" + file, Body(text)));

                Assert.False(saved.GetProperty("changed").GetBoolean(), file);
                Assert.Equal(before, File.ReadAllBytes(path));
            }
        }
    }

    [Fact]
    public async Task 저장은_CLI와_같은_모양으로_키_순서는_그대로()
    {
        var (dir, app, rules) = await Open();
        using (dir)
        await using (app)
        {
            await Json(await app.Client.PostAsync("/api/rules/new.hblint.json", Body("""{"version":1,"nodes":{"r":{"message":"m","kind":"report","from":"scope"}},"id":"CLL0004"}""")));

            Assert.Equal("""
                {
                  "version": 1,
                  "nodes": {
                    "r": {
                      "message": "m",
                      "kind": "report",
                      "from": "scope"
                    }
                  },
                  "id": "CLL0004"
                }

                """.ReplaceLineEndings("\n"), File.ReadAllText(Path.Combine(rules, "new.hblint.json")));
        }
    }

    [Fact]
    public async Task 새_파일은_POST_있는_파일은_PUT()
    {
        var (dir, app, _) = await Open();
        using (dir)
        await using (app)
        {
            const string rule = """{ "version": 1, "id": "CLL0004", "nodes": {} }""";

            Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PutAsync("/api/rules/missing.hblint.json", Body(rule))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await app.Client.PostAsync("/api/rules/fresh.hblint.json", Body(rule))).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await app.Client.PostAsync("/api/rules/fresh.hblint.json", Body(rule))).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await app.Client.PostAsync("/api/rules/unity-null-operator.hblint.json", Body(rule))).StatusCode);
        }
    }

    [Theory]
    [InlineData("..%2Fescape.hblint.json")]
    [InlineData("..%5Cescape.hblint.json")]
    [InlineData("sub%2Fa.hblint.json")]
    [InlineData("a.json")]
    [InlineData("a.hblint.json.bak")]
    [InlineData("..hblint.json")]
    [InlineData("C%3A%5Ca.hblint.json")]
    [InlineData("a.hblint.json%0A")]
    [InlineData("a%3Ab.hblint.json")]
    public async Task 폴더_밖이나_규칙이_아닌_이름은_거절(string file)
    {
        var (dir, app, _) = await Open();
        using (dir)
        await using (app)
        {
            var put = await app.Client.PostAsync("/api/rules/" + file, Body("""{ "version": 1, "id": "CLL0004", "nodes": {} }"""));
            var get = await app.Client.GetAsync("/api/rules/" + file);

            Assert.False(put.IsSuccessStatusCode, file);
            Assert.False(get.IsSuccessStatusCode, file);
            Assert.False(File.Exists(Path.Combine(dir.Path, "escape.hblint.json")));
        }
    }

    [Fact]
    public async Task 한글_이름_규칙도_열고_저장함()
    {
        var (dir, app, rules) = await Open();
        using (dir)
        await using (app)
        {
            const string file = "한글-규칙.hblint.json";
            var created = await app.Client.PostAsync("/api/rules/" + Uri.EscapeDataString(file), Body("""{ "version": 1, "id": "CLL0050", "nodes": {} }"""));

            Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
            Assert.True(File.Exists(Path.Combine(rules, file)));
            var list = await Json(await app.Client.GetAsync("/api/rules"));
            Assert.Contains(list.GetProperty("rules").EnumerateArray(), r => r.GetProperty("file").GetString() == file && r.GetProperty("editable").GetBoolean());
            Assert.Equal("CLL0051", list.GetProperty("nextId").GetString());
        }
    }

    [Fact]
    public void 앱에서_못_여는_파일도_목록과_다음_id에는_들어감()
    {
        using var dir = new TempDir();
        // 이름 규칙 밖(공백) — 생성(CLI)은 읽는다
        dir.Write("with space.hblint.json", """{ "version": 1, "id": "CLL0077", "nodes": {} }""");
        var folder = new RuleFolder(dir.Path, Path.Combine(dir.Path, "x.cs"));

        var list = folder.List();

        Assert.False(Assert.Single(list).Editable);
        Assert.Equal("CLL0078", folder.NextId(list));
    }

    [Fact]
    public async Task 생성물_자리에_손으로_쓴_cs가_있으면_덮지_않음()
    {
        var (dir, app, rules) = await Open();
        using (dir)
        await using (app)
        {
            var handWritten = dir.Write("Player.cs", "public class Player { }");
            await app.Open(rules, handWritten);

            var result = await Json(await app.Client.PostAsync("/api/generate", null));

            Assert.False(result.GetProperty("ok").GetBoolean());
            Assert.Contains(result.GetProperty("errors").EnumerateArray(), e => e.GetString()!.Contains("덮지 않았습니다"));
            Assert.Equal("public class Player { }", File.ReadAllText(handWritten));

            // hblint-gen 생성물이면 덮는다
            var output = Path.Combine(dir.Path, "out", "LintRules.g.cs");
            await app.Open(rules, output);
            Assert.True((await Json(await app.Client.PostAsync("/api/generate", null))).GetProperty("ok").GetBoolean());
            File.AppendAllText(output, "// 손댐\n");
            Assert.True((await Json(await app.Client.PostAsync("/api/generate", null))).GetProperty("ok").GetBoolean());
        }
    }

    [Fact]
    public async Task 따옴표로_감싼_경로도_받음()
    {
        using var dir = new TempDir();
        await using var app = await TestApp.Start(dir);

        var response = await app.Client.PostAsJsonAsync("/api/workspace", new { rules = $"\"{dir.Path}\"", @out = $" \"{Path.Combine(dir.Path, "x.cs")}\" " });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Equal(dir.Path, (await Json(response)).GetProperty("rules").GetString());
    }

    [Fact]
    public async Task 프로젝트_읽기_실패에_csproj_후보가_따라옴()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "Assets"));
        Directory.CreateDirectory(Path.Combine(dir.Path, "ProjectSettings"));
        dir.Write("Game.Runtime.csproj", "<Project />");
        await using var app = await TestApp.Start(dir);
        await app.Open(dir.Path, Path.Combine(dir.Path, "x.cs"));

        var response = await app.Client.PostAsJsonAsync("/api/project", new { path = dir.Path });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(["Game.Runtime.csproj"], json.GetProperty("candidates").EnumerateArray().Select(c => c.GetString()));
    }

    [Fact]
    public async Task 폴더_밖을_가리키는_심볼릭_링크는_거절()
    {
        var (dir, app, rules) = await Open();
        using (dir)
        await using (app)
        {
            var outside = dir.Write("outside.hblint.json", """{ "version": 1, "id": "CLL0099", "nodes": {} }""");
            try
            {
                File.CreateSymbolicLink(Path.Combine(rules, "link.hblint.json"), outside);
            }
            catch (IOException)
            {
                return; // 심볼릭 링크 권한 없음 (Windows 개발자 모드 꺼짐)
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            Assert.False((await app.Client.GetAsync("/api/rules/link.hblint.json")).IsSuccessStatusCode);
            Assert.False((await app.Client.PutAsync("/api/rules/link.hblint.json", Body("{}"))).IsSuccessStatusCode);
            // 목록(= 생성이 읽는 것)에는 보이되 앱에서 열 수 없다
            Assert.Contains((await Json(await app.Client.GetAsync("/api/rules"))).GetProperty("rules").EnumerateArray(),
                r => r.GetProperty("file").GetString() == "link.hblint.json" && !r.GetProperty("editable").GetBoolean());
        }
    }

    [Fact]
    public async Task JSON이_아니면_저장하지_않고_검증_오류는_저장하며_노드를_알림()
    {
        var (dir, app, rules) = await Open();
        using (dir)
        await using (app)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.PostAsync("/api/rules/bad.hblint.json", Body("{ nope"))).StatusCode);
            Assert.False(File.Exists(Path.Combine(rules, "bad.hblint.json")));

            var saved = await Json(await app.Client.PostAsync("/api/rules/wip.hblint.json", Body("""
                { "version": 1, "id": "CLL0004", "nodes": { "n": { "kind": "sites.member", "from": "scope" }, "r": { "kind": "report", "from": "n", "message": "m" } } }
                """)));

            var validation = saved.GetProperty("validation");
            Assert.False(validation.GetProperty("ok").GetBoolean());
            Assert.Contains(validation.GetProperty("errors").EnumerateArray(), e => e.GetProperty("node").GetString() == "n");
            Assert.True(File.Exists(Path.Combine(rules, "wip.hblint.json")));
        }
    }

    [Fact]
    public async Task 생성은_CLI와_같은_파일을_냄()
    {
        var (dir, app, rules) = await Open();
        using (dir)
        await using (app)
        {
            var result = await Json(await app.Client.PostAsync("/api/generate", null));

            Assert.True(result.GetProperty("ok").GetBoolean());
            var output = result.GetProperty("output").GetString()!;
            Assert.Equal(RuleSet.Build(RuleSet.ReadFolder(rules)).Text, File.ReadAllText(output));
        }
    }

    [Fact]
    public async Task 오류가_있으면_생성물을_바꾸지_않음()
    {
        var (dir, app, rules) = await Open();
        using (dir)
        await using (app)
        {
            File.WriteAllText(Path.Combine(rules, "broken.hblint.json"), """{ "version": 1, "id": "CLL0001", "nodes": {} }""");

            var result = await Json(await app.Client.PostAsync("/api/generate", null));

            Assert.False(result.GetProperty("ok").GetBoolean());
            Assert.NotEqual(0, result.GetProperty("errors").GetArrayLength());
            Assert.False(File.Exists(result.GetProperty("output").GetString()!));
        }
    }

    [Fact]
    public async Task 카탈로그에_노드_칸_뜻_머리_칸()
    {
        using var dir = new TempDir();
        await using var app = await TestApp.Start(dir);

        var json = await Json(await app.Client.GetAsync("/api/catalog"));

        var member = json.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("kind").GetString() == "sites.member");
        Assert.Equal("Sites", member.GetProperty("output").GetString());
        Assert.Equal(["Scope", "Types", "Methods"], member.GetProperty("inputs")[0].GetProperty("accepts").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal("member", member.GetProperty("fields")[0].GetProperty("hint").GetString());
        var union = json.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("kind").GetString() == "sites.union");
        Assert.True(union.GetProperty("inputs")[0].GetProperty("many").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("kind").GetString() == "report").GetProperty("output").ValueKind);
        Assert.Equal(["id", "title", "severity", "requires", "ignoreAssemblies", "ignorePaths", "includeAssemblies", "includePaths"], json.GetProperty("head").EnumerateArray().Select(h => h.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task 카탈로그에_쉬운_설명_소켓_용어()
    {
        using var dir = new TempDir();
        await using var app = await TestApp.Start(dir);

        var json = await Json(await app.Client.GetAsync("/api/catalog"));

        foreach (var node in json.GetProperty("nodes").EnumerateArray())
        {
            Assert.False(string.IsNullOrWhiteSpace(node.GetProperty("help").GetString()), node.GetProperty("kind").GetString());
            Assert.False(string.IsNullOrWhiteSpace(node.GetProperty("example").GetString()), node.GetProperty("kind").GetString());
        }
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("scopeHelp").GetProperty("help").GetString()));
        Assert.Equal(["Scope", "Types", "Methods", "Sites", "Groups"], json.GetProperty("socketHelp").EnumerateObject().Select(p => p.Name));
        Assert.Contains(json.GetProperty("glossary").EnumerateArray(), t => t.GetProperty("term").GetString() == "사이트");
    }

    [Fact]
    public async Task 폴더를_열기_전에는_규칙_API가_거절()
    {
        using var dir = new TempDir();
        await using var app = await TestApp.Start(dir);

        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.GetAsync("/api/rules")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.PostAsJsonAsync("/api/workspace", new { rules = Path.Combine(dir.Path, "nope"), @out = "x.cs" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.PostAsJsonAsync("/api/workspace", new { rules = dir.Path, @out = Path.Combine(dir.Path, "x.txt") })).StatusCode);
    }

    [Fact]
    public async Task 최근_목록에_남음()
    {
        var (dir, app, rules) = await Open();
        using (dir)
        await using (app)
        {
            var state = await Json(await app.Client.GetAsync("/api/state"));

            Assert.Equal(rules, state.GetProperty("recent")[0].GetProperty("rules").GetString());
        }
    }
}
