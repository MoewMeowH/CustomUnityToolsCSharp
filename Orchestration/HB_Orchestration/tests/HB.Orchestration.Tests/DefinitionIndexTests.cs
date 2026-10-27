using HB.Orchestration.Agents;

namespace HB.Orchestration.Tests;

/// <summary>작업 폴더 정의 색인 (계획서 2026-10-27_작업폴더-정의-색인).</summary>
public sealed class DefinitionIndexTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hb-defindex-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string Labels(string path, string text) =>
        string.Join(" | ", DefinitionIndex.Outline(path, text).Select(e => $"{e.Line}:{e.Label}"));

    [Fact]
    public void CSharp_types_members_and_constants()
    {
        const string code = """
            namespace X;

            /// <summary>doc</summary>
            public sealed partial class Runner(int seed) : IDisposable
            {
                public const int Max = 3;
                public static readonly TimeSpan Hold = TimeSpan.FromHours(1);
                private readonly List<int> _items = [];
                public int Count => _items.Count(x => x > 0);
                public string? Name { get; set; }
                [Fact] public void Works() { Foo(); }
                public static (string? A, string? B) Create(int a,
                    int b) => (null, null);
                public async Task<T> GetAsync<T>(CancellationToken ct) where T : class
                {
                    if (ct.IsCancellationRequested) return default!;
                    var x = new Runner(1);
                    new Thread(() => { }).Start();
                    foreach (var i in _items) Foo(i);
                    static int Local() => 1;
                    return default!;
                }
                internal record struct Entry(int Line);
                private enum Kind { A, B }
                public interface IThing { void Do(); }
                public Runner() : this(0) { }
            }
            """;

        Assert.Equal(
            "4:class Runner | 6:Max | 7:Hold | 9:Count | 10:Name | 11:Works() | 12:Create() | 14:GetAsync() | 20:Local() | 23:record struct Entry | 24:enum Kind | 25:interface IThing | 26:Runner()",
            Labels("src/Runner.cs", code));
    }

    [Fact]
    public void CSharp_unity_messages_operators_and_bracket_attributes()   // sub_self 535ca00022 C4 · C6 · C9
    {
        const string code = """
            public class Player : MonoBehaviour
            {
                void Update() { }
                IEnumerator Start() { yield break; }
                async void OnEnable() { }
                [InlineData(new[] { 1 })] public void Case(int[] a) { }
                public static implicit operator Player(int x) => null!;
                public static Player operator +(Player a, Player b) => a;
                void Helper();
                Update();
            }
            """;
        Assert.Equal("1:class Player | 3:Update() | 4:Start() | 5:OnEnable() | 6:Case() | 7:operator Player | 8:operator + | 9:Helper()",
            Labels("Player.cs", code));
    }

    [Fact]
    public void Python_nesting_is_qualified()
    {
        const string code = "class A:\n    def run(self):\n        def inner():\n            pass\n    async def go(self):\n        pass\n\ndef main():\n    pass\n";
        Assert.Equal("1:class A | 2:A.run() | 3:A.run.inner() | 5:A.go() | 8:main()", Labels("tool.py", code));
    }

    [Fact]
    public void Python_module_level_block_closes_class()   // C5
    {
        const string code = "class A:\n    def run(self):\n        pass\n\n# 주석\nif TYPE_CHECKING:\n    def helper():\n        pass\n";
        Assert.Equal("1:class A | 2:A.run() | 7:helper()", Labels("tool.py", code));
    }

    [Fact]
    public void Markdown_keeps_trailing_hash_and_tracks_fence_kind()   // C1 · C2
    {
        const string text = "## Unity C#\n# F# ##\n````md\n```\n# 안쪽 예시\n```\n````\n~~~\n```\n# 물결 안\n~~~\n## 끝\n";
        Assert.Equal("1:## Unity C# | 2:# F# | 12:## 끝", Labels("guide.md", text));
    }

    [Fact]
    public void Long_label_does_not_split_surrogate_pair()   // D6
    {
        // 이름표는 "# " + 제목. "# " 2자 + 57자 = 59자 다음 60자째가 이모지의 상위 서로게이트
        var label = DefinitionIndex.Outline("a.md", "# " + new string('가', 57) + "😀😀\n").Single().Label;
        Assert.Equal("# " + new string('가', 57) + "…", label);                                    // 이모지를 반으로 가르지 않고 통째로 뺀다
    }

    [Fact]
    public void Markdown_headings_outside_code_blocks()
    {
        const string text = "# 제목\n본문\n```\n# 코드 안 주석\n```\n## 계획 ##\n    # 들여쓴 코드\n### 테스트\n";
        Assert.Equal("1:# 제목 | 6:## 계획 | 8:### 테스트", Labels("plan.md", text));
    }

    [Fact]
    public void Json_keys_up_to_two_levels()
    {
        const string text = "{\r\n  \"Roles\": {\r\n    \"SubSelf\": {\r\n      \"Prompt\": \"a\"\r\n    }\r\n  },\r\n  \"Modes\": {}\r\n}\r\n";
        Assert.Equal("2:Roles | 3:SubSelf | 7:Modes", Labels("modes.json", text));
    }

    [Fact]
    public void PowerShell_functions_and_unknown_extensions()
    {
        Assert.Equal("2:function Get-Thing", Labels("Sync.ps1", "param()\nfunction Get-Thing {\n}\n"));
        Assert.Empty(DefinitionIndex.Outline("a.cshtml", "@model X\npublic class Y {}\n"));
        Assert.Empty(DefinitionIndex.Outline("changes.diff", "+public class Y {}\n"));
    }

    [Fact]
    public void Format_caps_entries_per_file()
    {
        var entries = Enumerable.Range(1, DefinitionIndex.MaxEntriesPerFile + 5).Select(i => new DefinitionIndex.Entry(i, $"M{i}()")).ToList();
        var line = DefinitionIndex.Format(entries)!;
        Assert.StartsWith("  정의: M1() 1 · M2() 2", line);
        Assert.EndsWith("(… 정의 5개 더)", line);
        Assert.DoesNotContain($"M{DefinitionIndex.MaxEntriesPerFile + 1}()", line);
        Assert.Null(DefinitionIndex.Format([]));
    }

    [Fact]
    public void ReadText_skips_invalid_utf8_and_big_files()
    {
        var bad = Path.Combine(_dir, "bad.cs");
        File.WriteAllBytes(bad, [0x70, 0xFF, 0xFE, 0x0A]);
        Assert.Null(DefinitionIndex.ReadText(bad));

        var big = Path.Combine(_dir, "big.cs");
        File.WriteAllText(big, new string('a', (int)DefinitionIndex.MaxFileBytes + 1));
        Assert.Null(DefinitionIndex.ReadText(big));

        var ok = Path.Combine(_dir, "ok.cs");
        File.WriteAllText(ok, "\uFEFFpublic class A {}\n");
        Assert.Equal("1:class A", Labels("ok.cs", DefinitionIndex.ReadText(ok)!));
    }
}
