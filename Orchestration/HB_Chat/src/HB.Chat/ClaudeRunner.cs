using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HB.Chat;

/// <summary>띄울 프로세스. Args는 <see cref="ClaudeArgs"/>가 만든 값 (테스트는 가짜 claude 스크립트 앞 인자를 붙인다).</summary>
public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Args, string WorkingDirectory);

/// <summary>
/// 실행 하나의 결과. Ok는 result 이벤트가 성공으로 왔을 때만. SessionId는 init에서 받은 실제 ID.
/// UnknownLines는 화면에 쓰지 않은 줄 수 (형식이 바뀌었는지 볼 때).
/// </summary>
public sealed record RunResult(
    bool Ok, bool Cancelled, string? Error, string? Text, double? CostUsd, string? SessionId, int Denials, int OtherLines, string? Stderr)
{
    /// <summary>마지막 assistant 요청의 컨텍스트 크기 (토큰). 중지돼도 그때까지 본 값.</summary>
    public long? ContextTokens { get; init; }

    /// <summary>
    /// 답 뒤 백그라운드 기다림을 무엇으로 정리했는지 — "wrapup"(지금 끝내기) · "cap"(기다림 상한) · "stop"(기다리는 중 중지 · 끼어들기). 기다림이 없었거나
    /// 작업이 다 끝나 스스로 닫혔으면 null. BackgroundLeft는 그때 남았던 작업 수 (방 기록 줄).
    /// </summary>
    public string? BackgroundClosedBy { get; init; }
    public int BackgroundLeft { get; init; }

    /// <summary>모델의 컨텍스트 창 (result modelUsage). result가 없으면 null.</summary>
    public int? ContextWindow { get; init; }

    /// <summary>이 턴의 마지막 지휘자 상태 한 줄 (post_turn_summary, 3차 b). 없으면 null.</summary>
    public StatusEvent? Status { get; init; }

    /// <summary>claude를 띄우기 전에 막힘 (작업 폴더 검사 등, 4차 C2) — 사용자 말이 세션 기록에 남지 않았다.</summary>
    public bool NotStarted { get; init; }
}

/// <summary>
/// CLI가 묻는 도구 승인 하나 (control_request can_use_tool, 3차 0번 실측). InputJson은 도구 입력 원문, RequestJson은 요청 원문
/// (description · permission_suggestions · decision_reason 포함).
/// </summary>
public sealed record ToolPermission(string RequestId, string Tool, string InputJson, string RequestJson);

/// <summary>
/// claude -p를 숨긴 프로세스로 한 번 띄우고, stdout stream-json을 줄마다 읽어 넘긴다 (CodePet chat-agent-runner와 같은 구조).
/// 입력은 SDK식 stream-json (3차 a): initialize 제어 요청 → 사용자 말 한 줄. 실행 중 승인 요청(control_request can_use_tool)은
/// onPermission에 묻고 그 답을 control_response로 stdin에 쓴다. result가 오면 stdin을 닫아 프로세스가 끝나게 한다.
/// 셸을 거치지 않고 인자를 하나씩 넘긴다. 취소하면 프로세스 트리를 끝낸다 — 진행 중 턴은 세션에 결과 없이 남고 다음 말이 이어 간다 (문서).
/// 기본 시간 제한은 없다 (긴 작업). 예외를 던지지 않고 실패는 Error로 돌려준다 (onEvent가 던진 예외는 삼킨다).
/// </summary>
public static class ClaudeRunner
{
    /// <summary>stdout 한 줄 상한 (글자). 넘으면 실행을 끝낸다.</summary>
    public const int MaxLineChars = 8 * 1024 * 1024;

    /// <summary>stderr는 끝부분만 이만큼 남긴다.</summary>
    public const int MaxStderrChars = 16 * 1024;

    /// <summary>승인을 물을 곳이 없을 때(onPermission 없음)의 거절 문구 — 그 전 --permission-prompts none과 같은 뜻.</summary>
    public const string NoApprover = "승인할 사람이 없어 자동으로 거절했습니다. 다시 시도하지 말고, 무엇이 필요한지 사용자에게 말하세요.";

    /// <summary>
    /// 편집 방에서 CLI가 스스로 허용하지 않은 파일 쓰기(작업 폴더 밖 · 보호된 파일)를 거절할 때 — 편집 단계는 작업 폴더 안만 고친다 (명령 방은 카드로 묻는다).
    /// 무엇을 할지 알린다 (사용자 보고 2026-10-30: 단톡방에서 %TEMP%에 Write가 카드 없이 거절돼 "승인을 못 받는다"로 보였다).
    /// </summary>
    public const string NoWriteApprover =
        "이 방(편집 단계)에서는 작업 폴더 밖이거나 보호된 파일을 Write · Edit로 쓸 수 없어 자동으로 거절했습니다 (명령 단계 방이면 승인 카드로 묻습니다). " +
        "같은 쓰기를 다시 시도하지 말고, 작업 폴더 안에 쓰거나 무엇이 필요한지 사용자에게 말하세요.";

    /// <summary>
    /// CLI가 스스로 허용하지 않은 읽기(작업 폴더 · 첨부 폴더 밖)를 거절할 때 — 이 앱에는 읽기 승인 카드가 없다. "승인할 사람이 없다"로 보이면 화면 앞의 사용자와
    /// 지휘자가 헷갈려(사용자 보고 2026-10-30) 까닭을 그대로 적는다.
    /// </summary>
    public const string NoReadApprover =
        "작업 폴더 밖 파일 · 폴더 읽기는 이 방에서 받지 않아 자동으로 거절했습니다 (읽기 승인 카드 없음 — 작업 폴더 · 첨부 파일은 읽을 수 있음). " +
        "같은 읽기를 다시 시도하지 말고, 필요하면 사용자에게 그 파일을 첨부하거나 작업 폴더로 옮겨 달라고 말하세요.";

    /// <summary>읽기 도구 — 승인 요청까지 왔으면 <see cref="NoReadApprover"/>로 거절한다.</summary>
    public static readonly IReadOnlySet<string> ReadTools = new HashSet<string>(StringComparer.Ordinal) { "Read", "Grep", "Glob", "NotebookRead", "LS" };

    /// <summary>파일 쓰기 도구 — 승인 요청까지 왔으면 <see cref="NoWriteApprover"/>로 거절한다.</summary>
    public static readonly IReadOnlySet<string> WriteTools = new HashSet<string>(StringComparer.Ordinal) { "Write", "Edit", "MultiEdit", "NotebookEdit" };

    public static Task<RunResult> RunAsync(ProcessSpec spec, string prompt, Action<StreamEvent> onEvent, CancellationToken ct) =>
        RunAsync(spec, prompt, onEvent, null, ct);

    /// <summary>
    /// onPermission은 승인 요청마다 불리고 답(<see cref="PermissionDecision"/>의 JSON)을 돌려준다. 사용자를 기다리는 동안 출력 읽기는 계속된다.
    /// 실행이 끝난 뒤 늦게 온 답은 쓰지 않고 버린다 (프로세스가 없음).
    /// </summary>
    /// <param name="wrapUp">메인 답이 끝난 뒤 백그라운드 작업을 기다리는 중이면 지금 끝낸다 (화면 "지금 끝내기" — stdin을 닫고 유예 뒤 정리). 답 전에는 무시.</param>
    public static async Task<RunResult> RunAsync(ProcessSpec spec, string prompt, Action<StreamEvent> onEvent,
        Func<ToolPermission, CancellationToken, Task<string>>? onPermission, CancellationToken ct, CancellationToken wrapUp = default)
    {
        var utf8 = new UTF8Encoding(false);
        var psi = new ProcessStartInfo(spec.FileName)
        {
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
        };
        foreach (var a in spec.Args) psi.ArgumentList.Add(a);
        // 이 앱을 Claude Code 세션 안에서 띄웠을 때 물려받는 표시. 띄운 claude가 자기를 하위 세션으로 보지 않게 뺀다.
        psi.Environment.Remove("CLAUDECODE");

        using var proc = new Process { StartInfo = psi };
        try
        {
            if (!proc.Start()) return Fail("프로세스를 시작하지 못했습니다.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return Fail($"실행 실패: {ex.Message}");
        }

        var stderrTask = ReadTail(proc.StandardError, MaxStderrChars);
        string? error = null;
        // stdin 쓰기는 하나씩 (승인 답은 다른 스레드에서 온다). 닫은 뒤의 쓰기 · 끝난 프로세스에 쓰기는 조용히 버린다.
        // 이 잠금은 Dispose하지 않는다 — 실행이 끝난 뒤에 늦게 끝나는 승인 답이 들어올 수 있다.
        var input = new StdinWriter(proc.StandardInput);
        // 답을 기다리는 승인 요청마다 취소 토큰 — CLI가 control_cancel_request로 거두면 그 요청만 끊는다 (/self-review 7d839f4aa5 C1)
        var asks = new ConcurrentDictionary<string, CancellationTokenSource>();
        using (ct.Register(() => Kill(proc)))
        {
            // 프로세스가 먼저 끝났으면 쓰기가 버려지고 아래에서 결과 없음으로 보인다.
            await input.WriteAsync(InitializeLine);
            await input.WriteAsync(UserLine(prompt));

            ResultEvent? result = null;
            string? sessionId = null;
            long? context = null;
            string? contextModel = null;
            StatusEvent? status = null;
            var other = 0;
            // 도는 서브에이전트 (Claude 앱 기능 D): 백그라운드 서브에이전트가 있으면 CLI는 메인 턴의 result를 먼저 내고 서브에이전트가 끝난 뒤 다시 result를 낸다
            // (2.1.312 실제 확인). 그 사이 stdin을 닫으면 서브에이전트의 승인 요청이 "Stream closed"로 실패했다 — 모두 끝난 뒤의 result에서 닫는다.
            // 서브에이전트(subagent_type 있는) task만 센다 · 끝난 ID는 다시 넣지 않는다 · 메인 result 뒤 기다림은 상한까지 (/self-review security 42f9d47dc4 C2)
            // 백그라운드 명령(task_type local_bash)도 센다 — 끝나면 CLI가 끝 알림 뒤 지휘자가 이어서 두 번째 답을 쓴다 (실측). 안 세면 그 답을 놓치고,
            // 서버처럼 안 끝나는 명령이면 stdin을 닫아도 CLI가 끝나지 않아 방이 멈췄다
            var runningTasks = new HashSet<string>(StringComparer.Ordinal);
            var agentTasks = new HashSet<string>(StringComparer.Ordinal);
            var finishedTasks = new HashSet<string>(StringComparer.Ordinal);
            var resultSeen = false;
            // 이어 가는 세션에 지난 실행에서 죽은 백그라운드 작업이 있으면, CLI는 사용자 말보다 먼저 그 끝 알림(status stopped)과 빈 result를 낸다 (실측).
            // 그 result에서 stdin을 닫으면 이번 턴의 승인 요청이 "Stream closed"로 실패했다 (실제 확인). 그래서 "시작을 못 본 작업의 끝 알림 뒤, 답(assistant 줄) 전의
            // 빈 result"만 턴 끝으로 치지 않는다 — 답 없이 끝나는 진짜 실패(result만 옴)까지 무시하면 stdin을 안 닫아 실행이 멈춘다 (시험으로 확인).
            // 성공(Ok)이고 글이 빈 result만, 시작을 못 본 끝 알림 수만큼만 거른다 — 글 없는 실패 result는 턴을 끝낸다 (/self-review 6f64f4b039 C1)
            var answered = false;
            var ghosts = 0;
            var seenTasks = new HashSet<string>(StringComparer.Ordinal);
            using var waitCap = new CancellationTokenSource();
            var capArmed = false; // 상한은 처음 기다림에서 한 번만 — result마다 다시 걸면 계속 밀렸다 (6f64f4b039 C4)
            // 백그라운드 작업이 남은 채 stdin을 닫으면(상한 · 지금 끝내기) CLI가 안 끝날 수 있다(안 끝나는 서버) — 유예 뒤 프로세스 트리를 정리한다.
            // 답은 이미 받았으므로 정상 끝. 작업 없이 끝나는 보통 턴은 예전처럼 CLI가 스스로 끝나기를 기다린다 (6f64f4b039 C2)
            using var killAfterClose = new CancellationTokenSource();
            using var killOnGrace = killAfterClose.Token.Register(() => Kill(proc));
            async Task FinishAsync(bool killLater)
            {
                await input.CloseAsync();
                if (killLater)
                    try { killAfterClose.CancelAfter(CloseGrace); } catch (ObjectDisposedException) { }
            }
            // 무엇으로 정리했는지 (방 기록 줄). 처음 것만 남긴다 — 두 번째 답으로 끝났으면 Answered(줄 없음)
            const string Answered = "answered";
            string? closedBy = null;
            var closedLeft = 0;
            void Closed(string by)
            {
                if (Interlocked.CompareExchange(ref closedBy, by, null) is null) closedLeft = runningTasks.Count;
            }
            using var closeOnCap = waitCap.Token.Register(() => { Closed("cap"); _ = FinishAsync(killLater: true); });
            using var closeOnWrapUp = wrapUp.Register(() =>
            {
                if (!Volatile.Read(ref resultSeen)) return;
                Closed("wrapup");
                _ = FinishAsync(killLater: true);
            });
            void Waiting(TimeSpan? cap)
            {
                try { onEvent(new BackgroundWaitEvent(runningTasks.Count, cap is { } c ? DateTimeOffset.Now + c : null)); } catch { /* 화면 쪽 실패 */ }
            }
            while (await proc.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.Length > MaxLineChars)
                {
                    error = "출력 한 줄이 너무 길어 실행을 끝냈습니다.";
                    Kill(proc);
                    break;
                }
                var ev = StreamParser.Parse(line);
                switch (ev)
                {
                    case null: continue;
                    case ControlRequestEvent control:
                        answered = true; // 승인을 묻는다면 사용자 말 턴이 진행 중이다
                        // 답을 기다리는 동안에도 출력은 계속 읽는다 (여러 승인이 겹칠 수 있다). 화면에는 넘기지 않는다.
                        var ask = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        if (!asks.TryAdd(control.RequestId, ask))
                        {
                            ask.Dispose(); // 같은 ID가 아직 답을 기다린다 — 두 번째 요청은 버린다 (먼저 온 요청의 답이 간다)
                            continue;
                        }
                        _ = AnswerAsync(input, control, onPermission, asks, ask, ct);
                        continue;
                    case ControlCancelEvent cancel:
                        if (asks.TryGetValue(cancel.RequestId, out var withdrawn))
                        {
                            try { withdrawn.Cancel(); } catch (ObjectDisposedException) { } // 그사이 답이 끝났다
                        }
                        continue;
                    case OtherEvent o:
                        other++;
                        if (o.Type.StartsWith("assistant", StringComparison.Ordinal)) answered = true;
                        break;
                    case InitEvent init: sessionId = init.SessionId; break;
                    case TaskEvent task:
                        // 끝 알림에는 subagent_type · task_type이 없을 수 있다 — 지우기는 늘, 넣기는 서브에이전트 · 백그라운드 명령만
                        var before = runningTasks.Count;
                        if (task.Phase != "done") seenTasks.Add(task.TaskId);
                        else if (!answered && !seenTasks.Contains(task.TaskId)) ghosts++; // 지난 실행의 작업 끝 알림
                        if (task.Phase == "done")
                        {
                            runningTasks.Remove(task.TaskId);
                            finishedTasks.Add(task.TaskId);
                        }
                        else if ((task.Agent is not null || task.Kind == TaskEvent.BashKind) && !finishedTasks.Contains(task.TaskId))
                        {
                            runningTasks.Add(task.TaskId);
                            if (task.Agent is not null) agentTasks.Add(task.TaskId);
                        }
                        if (resultSeen && runningTasks.Count != before) Waiting(null);
                        break;
                    case ResultEvent ghost when ghosts > 0 && !answered && ghost.Ok && string.IsNullOrEmpty(ghost.Text):
                        ghosts--;
                        continue; // 사용자 말 턴 전의 빈 result (지난 실행의 죽은 백그라운드 작업 알림) — 결과로 치지 않고 화면에도 넘기지 않는다
                    case ResultEvent r:
                        result = r;
                        Volatile.Write(ref resultSeen, true);
                        // 턴 끝 — 다음 입력이 없으므로 CLI가 끝난다 (0번 실측). 백그라운드 작업이 아직 돌면 그 승인 요청 · 끝 알림을 받을 수 있게 열어 두되,
                        // 끝 알림이 끝내 오지 않아도 상한 뒤에는 닫는다 (승인 통로가 계속 열려 있지 않게). 서브에이전트가 남았으면 30분, 백그라운드 명령만이면 10분
                        if (runningTasks.Count == 0)
                        {
                            // 기다림 끝의 두 번째 답 — 정리할 것이 없다. CLI가 닫히는 동안 온 지금 끝내기 · 중지는 정리 줄을 남기지 않는다 (/self-review e464ec29ba C2)
                            if (capArmed) Interlocked.CompareExchange(ref closedBy, Answered, null);
                            await FinishAsync(killLater: capArmed); // 기다림 중 띄운 것이 남았을 수 있다
                        }
                        else if (!capArmed)
                        {
                            capArmed = true;
                            var cap = runningTasks.Overlaps(agentTasks) ? BackgroundAgentWait : BackgroundBashWait;
                            waitCap.CancelAfter(cap);
                            Waiting(cap);
                        }
                        else Waiting(null);
                        break;
                    case UsageEvent { ContextTokens: > 0 } u: answered = true; (context, contextModel) = (u.ContextTokens, u.Model ?? contextModel); break;
                    case UsageEvent or DeltaEvent or ToolEvent: answered = true; break;
                    case StatusEvent s: status = s; break;
                }
                try { onEvent(ev); } catch { /* 화면 쪽 실패가 실행을 멈추지 않게 */ }
            }
            // result 없이 끝났어도(중지 · 끼어들기 · 결과 없음 · 한 줄 초과) 닫는다 — 늦은 승인 답은 닫힌 쓰기로 버려진다 (7d839f4aa5 C2)
            await input.CloseAsync();

            await proc.WaitForExitAsync(CancellationToken.None);
            var stderr = await stderrTask;
            // 답을 받은 뒤 기다리는 중에 중지 · 끼어들기가 오면 그 답으로 끝낸다 — 턴은 이미 끝났으므로 "중지됨"으로 남기지 않는다 (6f64f4b039 C4)
            if (ct.IsCancellationRequested && !(result is { Ok: true } && capArmed))
                return new RunResult(false, true, "중지됨", null, result?.CostUsd, sessionId, result?.Denials ?? 0, other, stderr)
                    { ContextTokens = context, ContextWindow = result?.WindowFor(contextModel), Status = status };
            if (ct.IsCancellationRequested) Closed("stop");
            if (error is null && result is null)
                error = $"결과 없이 끝났습니다 (종료 코드 {proc.ExitCode}).";
            if (error is null && !result!.Ok)
                error = $"실행 오류 ({result.Subtype}): {result.Text}";
            return new RunResult(error is null, false, error, result?.Text, result?.CostUsd, sessionId, result?.Denials ?? 0, other,
                string.IsNullOrWhiteSpace(stderr) ? null : stderr)
            {
                ContextTokens = context, ContextWindow = result?.WindowFor(contextModel), Status = status,
                BackgroundClosedBy = closedBy == Answered ? null : closedBy, BackgroundLeft = closedLeft,
            };
        }
    }

    private static RunResult Fail(string error) => new(false, false, error, null, null, null, 0, 0, null);

    /// <summary>메인 result 뒤 백그라운드 서브에이전트를 기다리는 상한 — 넘으면 stdin을 닫는다(그 뒤 서브에이전트 승인 요청은 실패). 시험이 줄인다.</summary>
    internal static TimeSpan BackgroundAgentWait { get; set; } = TimeSpan.FromMinutes(BackgroundAgentMinutes);

    /// <summary>메인 result 뒤 백그라운드 명령만 남았을 때 기다리는 상한 (결정 "10분"). 시험이 줄인다.</summary>
    internal static TimeSpan BackgroundBashWait { get; set; } = TimeSpan.FromMinutes(BackgroundBashMinutes);

    public const int BackgroundBashMinutes = 10;

    /// <summary>서브에이전트가 남았을 때의 기다림 상한 (분, 카드 표시용 — <see cref="BackgroundAgentWait"/> 기본값).</summary>
    public const int BackgroundAgentMinutes = 30;

    /// <summary>stdin을 닫은 뒤 CLI가 스스로 끝나기를 기다리는 시간 — 넘으면 프로세스 트리를 정리한다 (안 끝나는 백그라운드 명령). 시험이 줄인다.</summary>
    internal static TimeSpan CloseGrace { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>SDK가 처음 보내는 제어 요청 (0번 실측에서 쓴 모양 그대로 — 훅은 넘기지 않는다).</summary>
    internal const string InitializeLine = """{"type":"control_request","request_id":"hbchat_init","request":{"subtype":"initialize","hooks":null}}""";

    /// <summary>
    /// stdin 한 줄 상한 (UTF-8 바이트). 한 번에 써도 약 300KB 줄에서는 CLI가 init조차 내지 않았다 (131KB 정상, 2.1.312 실측 —
    /// /self-review 70170a8801 C4). 넘는 말은 띄우기 전에 막는다 (RoomHub).
    /// </summary>
    public const int MaxStdinLineBytes = 120 * 1024;

    /// <summary>사용자 말 한 줄. 한글은 \uXXXX(6바이트)로 바꾸지 않고 UTF-8 그대로 — 줄이 약 절반 (위 상한 때문).</summary>
    internal static string UserLine(string prompt) => JsonSerializer.Serialize(new
    {
        type = "user",
        message = new { role = "user", content = prompt },
        parent_tool_use_id = (string?)null,
        session_id = "",
    }, LineJson);

    private static readonly JsonSerializerOptions LineJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// 제어 요청 하나에 답한다. can_use_tool이면 onPermission(없으면 거절), 그 밖의 종류는 오류 답 (initialize에 훅을 넘기지 않아 올 일이 없다).
    /// onPermission이 던지거나 답 JSON이 깨졌으면 거절로 답한다 — 답이 없으면 CLI가 멈춰 있게 된다 (지휘자 검토 C2).
    /// CLI가 그 요청을 거뒀으면(control_cancel_request — 요청 토큰만 끊기고 실행 토큰은 그대로) 답을 쓰지 않는다.
    /// </summary>
    private static async Task AnswerAsync(StdinWriter input, ControlRequestEvent control,
        Func<ToolPermission, CancellationToken, Task<string>>? onPermission,
        ConcurrentDictionary<string, CancellationTokenSource> asks, CancellationTokenSource ask, CancellationToken run)
    {
        try
        {
            string response;
            if (control.Subtype == "can_use_tool")
            {
                try
                {
                    var permission = ParsePermission(control);
                    var decision = permission is null ? PermissionDecision.Deny("승인 요청 모양을 읽지 못해 거절했습니다.")
                        : onPermission is null ? PermissionDecision.Deny(NoApprover)
                        : await onPermission(permission, ask.Token);
                    response = SuccessLine(control.RequestId, decision);
                }
                catch (Exception ex)
                {
                    response = SuccessLine(control.RequestId, PermissionDecision.Deny($"승인 처리 중 오류로 거절했습니다: {ex.GetType().Name}"));
                }
                if (ask.IsCancellationRequested && !run.IsCancellationRequested) return; // CLI가 거둔 요청
            }
            else
            {
                response = JsonSerializer.Serialize(new
                {
                    type = "control_response",
                    response = new { subtype = "error", request_id = control.RequestId, error = $"HB_Chat은 {control.Subtype} 요청을 받지 않습니다." },
                });
            }
            await input.WriteAsync(response);
        }
        finally
        {
            asks.TryRemove(new KeyValuePair<string, CancellationTokenSource>(control.RequestId, ask));
            ask.Dispose();
        }
    }

    private static string SuccessLine(string requestId, string decision) => JsonSerializer.Serialize(new
    {
        type = "control_response",
        response = new { subtype = "success", request_id = requestId, response = JsonNode.Parse(decision) },
    });

    private static ToolPermission? ParsePermission(ControlRequestEvent control)
    {
        try
        {
            using var doc = JsonDocument.Parse(control.RequestJson);
            var e = doc.RootElement;
            if (!e.TryGetProperty("tool_name", out var tool) || tool.ValueKind != JsonValueKind.String || tool.GetString() is not { Length: > 0 } name)
                return null;
            var inputJson = e.TryGetProperty("input", out var i) && i.ValueKind == JsonValueKind.Object ? i.GetRawText() : "{}";
            return new ToolPermission(control.RequestId, name, inputJson, control.RequestJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>stdin 줄 쓰기 (한 번에 하나). 닫은 뒤 · 프로세스가 끝난 뒤의 쓰기는 버린다.</summary>
    private sealed class StdinWriter(StreamWriter writer)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private bool _closed;

        public async Task WriteAsync(string line)
        {
            await _gate.WaitAsync();
            try
            {
                if (_closed) return;
                // 한 줄은 한 번에 쓴다: StreamWriter로 쓰면 버퍼(4KB) 단위로 나뉘어 파이프에 들어가는데, 약 8KB를 넘는 줄이 여러 번에 나뉘어
                // 들어오면 stdin을 열어 둔 CLI가 그 줄을 처리하지 않고 멈췄다 (2.1.312 실측 — 9KB 줄: 나눠 쓰면 멈춤 · 한 번에 쓰면 20KB도 됨,
                //). 긴 말 · 한글(\uXXXX로 6배)이 걸린다. \r\n 대신 \n — 줄 끝 모양을 실측과 같게.
                await writer.FlushAsync();
                var bytes = writer.Encoding.GetBytes(line + "\n");
                await writer.BaseStream.WriteAsync(bytes);
                await writer.BaseStream.FlushAsync();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { _closed = true; }
            finally
            {
                _gate.Release();
            }
        }

        public async Task CloseAsync()
        {
            await _gate.WaitAsync();
            try
            {
                if (_closed) return;
                _closed = true;
                writer.Close();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
            finally
            {
                _gate.Release();
            }
        }
    }

    private static void Kill(Process proc)
    {
        try
        {
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { }
    }

    private static async Task<string> ReadTail(StreamReader reader, int limit)
    {
        var sb = new StringBuilder();
        var buf = new char[4096];
        int n;
        while ((n = await reader.ReadAsync(buf)) > 0)
        {
            sb.Append(buf, 0, n);
            if (sb.Length > limit * 2) sb.Remove(0, sb.Length - limit);
        }
        return sb.Length > limit ? sb.ToString(sb.Length - limit, limit) : sb.ToString();
    }
}
