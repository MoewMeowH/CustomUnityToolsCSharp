using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HB.Chat;

/// <summary>
/// claude CLI 버전 기록 — 데이터 폴더 cli.json. 카드(승인 · 질문)는 CLI의 stream-json 제어 요청 모양에 기대므로, 확인하지 않은 판이면 화면에 띠를 띄운다.
/// Seen: 마지막으로 본 판 (실행 init · 앱 시작 때 claude --version), Checked: 카드 자가 시험을 통과한 판(처음 본 판은 확인한 것으로 — 사용자 결정),
/// LastTest: 마지막 자가 시험 결과.
/// </summary>
public sealed partial class CliVersions(Func<string> dataFolder)
{
    public sealed record TestStep(string Name, string State, string? Detail);

    public sealed record TestResult(DateTimeOffset At, string? Version, bool Passed, IReadOnlyList<TestStep> Steps, string? Error);

    public sealed record State
    {
        public string? Seen { get; init; }
        public string? Checked { get; init; }
        public DateTimeOffset? CheckedAt { get; init; }
        public TestResult? LastTest { get; init; }

        /// <summary>띠를 띄울지: 본 판이 확인한 판과 다르거나, 마지막 자가 시험이 지금 판에서 실패했을 때.</summary>
        public bool NeedsCheck => Seen is not null && (Seen != Checked || LastTest is { Passed: false } t && t.Version == Seen);
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>모든 인스턴스가 같은 잠금 (시험 · 실행이 따로 만들어도 같은 파일).</summary>
    private static readonly Lock _gate = new();

    private string FilePath() => Path.Combine(dataFolder(), "cli.json");

    public State Read()
    {
        lock (_gate)
        {
            try
            {
                var path = FilePath();
                return File.Exists(path) ? JsonSerializer.Deserialize<State>(File.ReadAllText(path), Json) ?? new State() : new State();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new State(); }
        }
    }

    /// <summary>본 판을 적는다 — 확인한 판이 아직 없으면(이 기능 전) 그 판을 확인한 것으로 (결정 "지금 판을 확인함으로"). 바뀌었으면 true.</summary>
    public bool Note(string? version)
    {
        if (Clean(version) is not { } v) return false;
        lock (_gate)
        {
            var now = Read();
            if (now.Seen == v && now.Checked is not null) return false;
            Write(now with { Seen = v, Checked = now.Checked ?? v, CheckedAt = now.Checked is null ? DateTimeOffset.Now : now.CheckedAt });
            return now.Seen != v;
        }
    }

    /// <summary>자가 시험 결과를 남긴다 — 통과면 그 판을 확인한 판으로.</summary>
    public void Save(TestResult result)
    {
        lock (_gate)
        {
            var now = Read();
            var seen = Clean(result.Version) ?? now.Seen;
            Write(result.Passed && seen is not null
                ? now with { Seen = seen, Checked = seen, CheckedAt = result.At, LastTest = result }
                : now with { Seen = seen, LastTest = result });
        }
    }

    private void Write(State state)
    {
        try
        {
            var path = FilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, Json), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // 다음 실행 때 다시 적힌다
    }

    /// <summary>판 글자만 (예 "2.1.312 (Claude Code)" → "2.1.312"). 모양이 아니면 null.</summary>
    public static string? Clean(string? version) => version is not null && VersionShape().Match(version) is { Success: true } m ? m.Value : null;

    [GeneratedRegex(@"\d+\.\d+\.\d+(?:[-.][0-9A-Za-z.]+)?")]
    private static partial Regex VersionShape();

    /// <summary>
    /// claude --version (토큰 없음) — 앱을 켤 때 백그라운드로 한 번, 첫 말 전에도 띠가 보이게. 시간 제한 안에 못 읽으면 null.
    /// </summary>
    public static async Task<string?> AskVersionAsync(string claudePath, TimeSpan timeout)
    {
        Process? p = null;
        try
        {
            // 표준 오류는 받지 않는다 — 읽지 않는 파이프가 차면 자식이 막힌다 (/self-review 5ccb0f709a C6)
            p = Process.Start(new ProcessStartInfo(claudePath, "--version")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8,
            });
            if (p is null) return null;
            using var cts = new CancellationTokenSource(timeout);
            var output = await p.StandardOutput.ReadToEndAsync(cts.Token);
            await p.WaitForExitAsync(cts.Token);
            return Clean(output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException or IOException)
        {
            // 시간 제한에 걸린 claude(로그인 · 업데이트 확인 등으로 멈춤)는 끝낸다 — 앱이 꺼질 때까지 남지 않게
            try { if (p is { HasExited: false }) p.Kill(entireProcessTree: true); }
            catch (Exception kill) when (kill is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            return null;
        }
        finally { p?.Dispose(); }
    }
}

/// <summary>
/// 카드 자가 시험 (설정 단추로만 — 사용자 결정): 임시 작업 폴더에서 claude를 명령 단계로 한 턴 돌려, 명령 승인 요청 · 질문 요청 · 글 조각 · 결과가
/// 이 앱이 읽는 모양 그대로 오고, 앱의 답이 CLI에 듣는지 본다. 앱이 자동으로 답한다 — 허용은 정해 둔 mkdir 한 줄일 때만, 질문은 첫 선택지.
/// 단계: 통과 · 실패(왔는데 모양이 다름 · 허용 답이 듣지 않음) · 안 옴(모델이 지시대로 도구를 안 부름 · CLI가 묻지 않음 — 다시 해 볼 수 있음).
/// </summary>
public static class CliSelfTest
{
    public const string BashStep = "명령 승인 카드", AskStep = "질문 카드", StreamStep = "글 조각 · 결과";

    public const string Passed = "통과", Failed = "실패", Missing = "안 옴";

    /// <summary>프롬프트 첫 낱말 — 가짜 claude가 이 모드로 답한다 (시험).</summary>
    public const string PromptHead = "hb-selftest";

    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private static readonly SemaphoreSlim _one = new(1, 1);

    /// <summary>이미 도는 중이라 시작하지 않았을 때의 Error (그 결과는 기록하지 않는다).</summary>
    public const string BusyError = "자가 시험이 이미 도는 중입니다.";

    /// <summary>지금 도는 중인지 (설정 화면 단추를 막는다).</summary>
    public static bool Running => _one.CurrentCount == 0;

    /// <summary>
    /// 승인을 묻게 할 명령 — 쓰기(mkdir)라야 CLI가 호스트에 묻는다 (echo 같은 읽기 전용 명령은 CLI가 묻지 않고 돌릴 수 있다). 자가 시험 폴더 안이고 매번 비운다.
    /// </summary>
    public static string Command(string nonce) => $"mkdir hb-selftest-{nonce}";

    public static string Prompt(string nonce) =>
        $"{PromptHead} — 이 앱의 카드 자가 시험이다. 다른 말이나 도구 없이 아래 순서대로만 한다.\n" +
        $"1. Bash 도구로 정확히 이 명령을 실행한다: {Command(nonce)}\n" +
        "2. AskUserQuestion 도구로 질문 하나를 묻는다: 질문 \"자가 시험 질문\", 머리 \"시험\", 선택지 \"예\" · \"아니요\", 하나만 고름.\n" +
        "3. 마지막으로 \"자가 시험 끝\"이라고만 답한다.";

    /// <summary>부른 쪽이 취소해(설정 화면을 떠남) 끝까지 못 돈 결과의 Error — 실패가 아니라 기록하지 않는다 (/self-review 5ccb0f709a C2).</summary>
    public const string AbortedError = "자가 시험을 끝까지 돌리지 못했습니다 (화면을 떠남).";

    /// <summary>
    /// 한 번 돈다. <paramref name="process"/>: (RunSpec, 작업 폴더) → 실행할 프로세스 (시험은 가짜 claude). 이미 도는 중이면 <see cref="BusyError"/>,
    /// 부른 쪽이 취소하면 <see cref="AbortedError"/>만 든 결과 — 둘 다 기록하지 않는다.
    /// 통과는 요청 모양만이 아니라 왕복으로 본다 (/self-review 5ccb0f709a C1): 명령은 허용한 mkdir 폴더가 실제로 생겼는지, 질문은 CLI가 허용 답을
    /// 거절로 세지 않았는지(우리가 거절한 수보다 거절이 많으면 실패).
    /// </summary>
    public static async Task<CliVersions.TestResult> RunAsync(string dataFolder, string model, string mcpServer,
        Func<RunSpec, string, ProcessSpec> process, CancellationToken ct = default)
    {
        if (!await _one.WaitAsync(0, CancellationToken.None)) return new CliVersions.TestResult(DateTimeOffset.Now, null, false, [], BusyError);
        try
        {
            var root = Path.Combine(dataFolder, "selftest");
            var workspace = Path.Combine(root, "ws");
            var mcp = Path.Combine(root, "mcp.json");
            try
            {
                // 앞 시험이 만든 폴더를 지우고 새로 (mkdir 결과가 쌓이지 않고, 이번 폴더가 이번 실행에서 생긴 것이게)
                if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
                Directory.CreateDirectory(workspace);
                File.WriteAllText(mcp, "{\"mcpServers\":{}}", new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new CliVersions.TestResult(DateTimeOffset.Now, null, false, [], $"자가 시험 폴더를 준비하지 못했습니다: {ex.Message}"); // C5 — 500 대신
            }
            var nonce = Guid.NewGuid().ToString("N")[..8];
            var expected = Command(nonce);
            var made = Path.Combine(workspace, $"hb-selftest-{nonce}");
            var spec = new RunSpec(PermissionLevel.Command, model, Guid.NewGuid().ToString("D"), false, mcp, mcpServer, null);

            string? version = null;
            var bashAsked = false; string? bashShape = null; string? bashOther = null;
            var askAnswered = false; string? askShape = null;
            var ourDenials = 0;
            var deltas = 0;
            string Deny(string why)
            {
                ourDenials++;
                return PermissionDecision.Deny(why);
            }
            Task<string> Decide(ToolPermission p, CancellationToken _)
            {
                if (p.Tool == "Bash")
                {
                    // 한 번 정해 둔 명령을 허용했으면 그 뒤 다른 Bash는 거절만 — 단계는 바꾸지 않는다 (C3)
                    var card = ApprovalRequest.Parse(p.RequestJson);
                    if (card is null) { bashShape ??= "승인 요청 모양을 읽지 못함"; return Task.FromResult(Deny("자가 시험: 요청을 읽지 못함")); }
                    if (!card.Truncated && card.Summary.Trim() == expected) { bashAsked = true; return Task.FromResult(PermissionDecision.Allow(p.InputJson)); }
                    bashOther ??= card.Summary; // 모델이 지시와 다른 명령 — CLI 모양 문제가 아니다
                    return Task.FromResult(Deny("자가 시험: 정해 둔 명령이 아님"));
                }
                if (p.Tool == ClaudeArgs.AskTool)
                {
                    var q = QuestionRequest.Parse(p.InputJson);
                    if (q is not { Items.Count: > 0 } || q.Items[0].Options.Count == 0)
                    {
                        askShape ??= "질문 요청 모양을 읽지 못함";
                        return Task.FromResult(Deny("자가 시험: 질문 모양을 읽지 못함"));
                    }
                    askAnswered = true;
                    return Task.FromResult(PermissionDecision.Allow(q.WithAnswers(new Dictionary<string, string> { [q.Items[0].Question] = q.Items[0].Options[0].Label })));
                }
                return Task.FromResult(Deny("자가 시험: 다른 도구는 쓰지 않음"));
            }
            void OnEvent(StreamEvent ev)
            {
                if (ev is InitEvent init) version = CliVersions.Clean(init.Version);
                else if (ev is DeltaEvent) deltas++;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            RunResult result;
            try { result = await ClaudeRunner.RunAsync(process(spec, workspace), Prompt(nonce), OnEvent, Decide, cts.Token); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                return new CliVersions.TestResult(DateTimeOffset.Now, version, false, [], $"실행하지 못했습니다: {ex.Message}");
            }
            if (ct.IsCancellationRequested) return new CliVersions.TestResult(DateTimeOffset.Now, version, false, [], AbortedError);

            var folderMade = Directory.Exists(made);
            var (bash, bashDetail) = bashShape is { } shape ? (Failed, shape)
                : bashAsked ? folderMade ? (Passed, (string?)null) : (Failed, "허용했는데 명령이 돌지 않음 — 허용 답이 CLI에 듣지 않았을 수 있음")
                : folderMade ? (Missing, "CLI가 묻지 않고 허용함 — 사용자 허용 규칙(~/.claude 설정)일 수 있음") // C4
                : (Missing, bashOther is { } other ? $"모델이 다른 명령을 부름: {other}" : "명령 승인 요청이 오지 않음");
            // CLI가 센 거절이 우리가 거절한 수보다 많으면 허용 답 하나가 거절로 처리된 것 — 남은 허용 답은 질문 쪽
            var (ask, askDetail) = askShape is { } qShape ? (Failed, qShape)
                : !askAnswered ? (Missing, "질문 요청이 오지 않음")
                : result.Denials > ourDenials && bash == Passed ? (Failed, "허용 답이 CLI에서 거절로 처리됨")
                : (Passed, (string?)null);
            var stream = result.Ok && deltas > 0 ? Passed : result.Ok ? Missing : Failed;
            var streamDetail = result.Ok ? deltas == 0 ? "글 조각(stream_event)이 오지 않음" : null : result.Error ?? "결과가 성공이 아님";
            IReadOnlyList<CliVersions.TestStep> steps =
            [
                new(BashStep, bash, bashDetail),
                new(AskStep, ask, askDetail),
                new(StreamStep, stream, streamDetail),
            ];
            // 실행 오류(claude를 못 찾음 등)는 맨 위에 — init이 없다는 말에 묻히지 않게 (C5)
            var error = result.Cancelled ? "시간 안에 끝나지 않았습니다."
                : version is null ? result.Error ?? "init에서 CLI 판을 읽지 못했습니다."
                : null;
            return new CliVersions.TestResult(DateTimeOffset.Now, version, error is null && steps.All(s => s.State == Passed), steps, error);
        }
        finally { _one.Release(); }
    }
}
