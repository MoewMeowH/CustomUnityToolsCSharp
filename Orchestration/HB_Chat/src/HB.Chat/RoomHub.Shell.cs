using System.Text;
using System.Text.RegularExpressions;

namespace HB.Chat;

/// <summary>shell-start · snapshot: 사용자가 '!'로 직접 돌리는 명령 (화면 말풍선, Output = 지금까지 보낸 출력).</summary>
public sealed record ShellView(string Command, DateTimeOffset StartedAt, string Output);

/// <summary>
/// 채팅창 '!' 명령 (결정 "출력을 전부 지휘자에게 바로 보내줘 … Claude terminal 처럼"): 사용자가 친 명령을 앱이 방 작업 폴더에서 cmd로 돌리고,
/// 끝나면 결과를 CLI bash 모드와 같은 꼴(&lt;bash-input&gt; · &lt;bash-stdout&gt;)의 사용자 말 하나로 지휘자에게 보낸다.
/// stream-json에는 bash 모드가 없다 — '!…' 말은 그대로 모델에게 간다 (CLI 2.1.316 실측 2026-11-04).
/// 사람이 친 명령이라 승인 카드는 없다 (결정 "모든 단계"). 받는 길은 화면 처리기(OnPostShell)뿐 — room_open · 단톡방 글은 여기로 오지 않는다.
/// </summary>
public sealed partial class RoomHub
{
    /// <summary>승인 기록의 도구 이름 (감사용 — 기록 화면에는 결과 말로 보인다).</summary>
    public const string ShellTool = "! 명령";

    /// <summary>지휘자에게 보내는 출력의 앞 · 뒤 글자 수 (가운데는 줄임 표시).</summary>
    public const int ShellKeepChars = 15_000;

    /// <summary>화면 말풍선에 들고 있는 출력 끝 글자 수.</summary>
    internal const int ShellViewChars = 30_000;

    /// <summary>시간 상한 (결정 "10분 + [중지]"). 넘으면 프로세스 트리째 끈다.</summary>
    public TimeSpan ShellLimit { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>출력을 화면에 묶어 보내는 간격 — 출력이 멈춰도 이 간격으로 남은 것을 보낸다.</summary>
    private static readonly TimeSpan ShellFlushEvery = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// cmd가 확장자 없는 명령 이름을 PATH보다 먼저 작업 폴더에서 찾지 않게 — 작업 폴더에 심어 둔 git.js · dotnet.vbs가 '!git'으로 돌던 길
    /// (/self-review bd240df0ac C1, cmd 실측 2026-11-04: 이 변수가 있으면 PATH의 것을, '.\이름'처럼 경로를 쓰면 그대로 작업 폴더의 것을 실행).
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> ShellEnv = new Dictionary<string, string> { ["NoDefaultCurrentDirectoryInExePath"] = "1" };

    /// <summary>파이프 한 스트림의 바이트 → 줄 글. UTF-8이 아니면 그 스트림은 시스템 ANSI로 (터미널 탭과 같은 규칙). 줄이 끝난 부분만 정리해 내보낸다.</summary>
    internal sealed class ShellStream
    {
        /// <summary>줄바꿈 없이 이만큼 쌓이면 줄이 안 끝났어도 내보낸다 (진행 막대 등).</summary>
        internal const int MaxPendingChars = 4096;

        private Decoder _decoder = new UTF8Encoding(false, throwOnInvalidBytes: true).GetDecoder();
        private bool _ansi;
        private readonly List<byte> _undecoded = [];
        private readonly StringBuilder _line = new();

        /// <summary>
        /// 새 조각을 받아 끝난 줄까지의 글(VT 코드를 걷고 줄 끝을 \n으로)을 돌려준다. 남은 줄 조각 · 끝의 \r은 다음으로 넘긴다 —
        /// 조각 경계에서 공백 · CRLF · ESC 열이 갈려 글이 바뀌던 것.
        /// </summary>
        public string Feed(byte[] chunk)
        {
            _line.Append(Decode(chunk));
            var s = _line.ToString();
            var cut = s.LastIndexOf('\n') + 1;
            var cr = s.LastIndexOf('\r');
            if (cr >= cut && cr < s.Length - 1) cut = cr + 1; // \r 뒤에 글이 있으면 그 \r까지 (끝의 \r은 \n을 기다린다)
            if (cut == 0 && s.Length > MaxPendingChars) cut = SafeCut(s, s.Length);
            if (cut == 0) return "";
            _line.Remove(0, cut);
            return Terminals.PlainText(s[..cut]);
        }

        /// <summary>끝: 디코더에 남은 바이트 · 마지막 줄 조각.</summary>
        public string Finish()
        {
            try { _line.Append(DecodeWith([], flush: true)); }
            catch (DecoderFallbackException) { }
            var rest = _line.ToString();
            _line.Clear();
            return rest.Length == 0 ? "" : Terminals.PlainText(rest);
        }

        private string Decode(byte[] chunk)
        {
            try
            {
                var text = DecodeWith(chunk, flush: false);
                // 끝의 덜 된 UTF-8 글자(디코더가 들고 있는 바이트) — ANSI로 바꿀 때 다시 읽게 따로 둔다
                _undecoded.Clear();
                var held = IncompleteUtf8Tail(chunk);
                for (var i = chunk.Length - held; i < chunk.Length; i++) _undecoded.Add(chunk[i]);
                return text;
            }
            catch (DecoderFallbackException) when (!_ansi)
            {
                _ansi = true;
                _decoder = Terminals.AnsiEncoding.GetDecoder();
                // 앞 조각 끝에 디코더가 들고 있던 바이트도 함께 (C4 — 바꿀 때 버리지 않게)
                var all = new byte[_undecoded.Count + chunk.Length];
                _undecoded.CopyTo(all);
                chunk.CopyTo(all, _undecoded.Count);
                _undecoded.Clear();
                return DecodeWith(all, flush: false);
            }
        }

        /// <summary>조각 끝에서 아직 끝나지 않은 UTF-8 글자의 바이트 수 (0~3).</summary>
        internal static int IncompleteUtf8Tail(byte[] chunk)
        {
            for (var i = 1; i <= Math.Min(3, chunk.Length); i++)
            {
                var b = chunk[^i];
                if ((b & 0xC0) == 0x80) continue; // 이어지는 바이트
                var need = b >= 0xF0 ? 4 : b >= 0xE0 ? 3 : b >= 0xC0 ? 2 : 1;
                return need > i ? i : 0;
            }
            return 0;
        }

        private string DecodeWith(byte[] chunk, bool flush)
        {
            var chars = new char[_decoder.GetCharCount(chunk, 0, chunk.Length, flush)];
            return new string(chars, 0, _decoder.GetChars(chunk, 0, chunk.Length, chars, 0, flush));
        }
    }

    /// <summary>도는 '!' 명령 하나. 출력 글은 <see cref="Sync"/> 안에서만 만진다 (snapshot과 pump가 같은 잠금 — /self-review 48e0bd8455 C3).</summary>
    internal sealed class ShellRun(string command)
    {
        public readonly string Command = command;
        public readonly Lock Sync = new();
        public readonly DateTimeOffset StartedAt = DateTimeOffset.Now;
        public readonly ShellStream[] Streams = [new(), new()]; // stdout · stderr 따로 (C4)
        private readonly StringBuilder _head = new();
        private readonly StringBuilder _tail = new();
        private long _dropped;
        private readonly StringBuilder _view = new();
        private readonly StringBuilder _unsent = new();

        /// <summary>띄운 프로세스 (띄우는 중이면 null). 바꿀 때는 Sync 안에서.</summary>
        public PipedProcess? Process;
        public volatile bool Stopped;

        /// <summary>나온 글을 쌓는다 (Sync 안에서): 지휘자용 앞 · 뒤, 화면용 끝 · 아직 안 보낸 것.</summary>
        public void Keep(string text)
        {
            if (text.Length == 0) return;
            _view.Append(text);
            _unsent.Append(text);
            TrimFront(_view, ShellViewChars);
            TrimFront(_unsent, ShellViewChars);
            var room = ShellKeepChars - _head.Length;
            if (room > 0)
            {
                var take = SafeCut(text, Math.Min(room, text.Length));
                _head.Append(text, 0, take);
                text = text[take..];
            }
            _tail.Append(text);
            if (_tail.Length > ShellKeepChars * 2) _dropped += TrimFront(_tail, ShellKeepChars);
        }

        /// <summary>아직 화면에 안 보낸 글을 꺼낸다 (Sync 안에서).</summary>
        public string TakeUnsent()
        {
            var s = _unsent.ToString();
            _unsent.Clear();
            return s;
        }

        /// <summary>화면 snapshot용: 이미 보낸 부분까지만 — 안 보낸 것은 곧 shell-output으로 가니 두 번 붙지 않게 (C3). Sync 안에서.</summary>
        public string SentView() => _view.ToString(0, _view.Length - Math.Min(_unsent.Length, _view.Length));

        /// <summary>지휘자에게 보낼 출력 (Sync 안에서): 앞 ShellKeepChars + 가운데 생략 + 끝 ShellKeepChars.</summary>
        public string Output()
        {
            var tail = new StringBuilder(_tail.ToString());
            var dropped = _dropped + TrimFront(tail, ShellKeepChars);
            return dropped > 0 ? $"{_head}\n… (가운데 {dropped:N0}자 생략)\n{tail}" : _head.ToString() + tail;
        }

        /// <summary>앞을 잘라 끝 max자만 남긴다 (서로게이트 쌍을 가르지 않게 — C10). 버린 수.</summary>
        private static int TrimFront(StringBuilder sb, int max)
        {
            if (sb.Length <= max) return 0;
            var cut = sb.Length - max;
            if (cut < sb.Length && char.IsLowSurrogate(sb[cut])) cut++;
            sb.Remove(0, cut);
            return cut;
        }
    }

    /// <summary>앞에서 n자를 자를 때 서로게이트 쌍 가운데면 하나 덜 (C10).</summary>
    private static int SafeCut(string s, int n) => n > 0 && n < s.Length && char.IsHighSurrogate(s[n - 1]) ? n - 1 : n;

    /// <summary>
    /// '!' 명령 시작 (화면 처리기에서만). 방마다 하나씩 — 도는 중(띄우는 중 포함)이면 거절. Claude가 답하는 중이어도 돌고, 결과 말은 큐에 들어간다.
    /// 자리만 잠금 안에서 잡고 프로세스는 잠금 밖에서 띄운다 (C7).
    /// </summary>
    public string? RunShell(string roomId, string command, string workspace)
    {
        if (Terminals.WhyBadCommand(command) is { } bad) return bad;
        if (_disposed) return "앱이 끝나는 중입니다."; // C6
        if (store.Get(roomId) is not { } room) return "없는 방입니다.";
        if (room.DeletedAt is not null) return "휴지통에 있는 방입니다.";
        if (!Directory.Exists(workspace)) return "작업 폴더가 없습니다.";
        var live = _rooms.GetOrAdd(roomId, id => new Live(id));
        var run = new ShellRun(command);
        lock (live.Gate)
        {
            if (live.Shell is not null) return "앞 '!' 명령이 아직 돕니다 — 끝나거나 [중지]한 뒤 보내세요.";
            live.Shell = run;
        }
        PipedProcess process;
        try { process = PipedProcess.Start(command, workspace, ShellEnv); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            lock (live.Gate) if (live.Shell == run) live.Shell = null;
            return $"명령을 띄우지 못했습니다: {ex.Message}";
        }
        lock (run.Sync) run.Process = process;
        // 띄우는 사이 중지 · 앱 종료면 바로 끈다 (결과는 그대로 따라간다)
        if (run.Stopped || _disposed) process.Kill();
        lock (live.Gate) Broadcast(live, new LiveEvent("shell-start") { Shell = new ShellView(command, run.StartedAt, "") });
        _ = Task.Run(() => FollowShellAsync(roomId, live, run, process));
        return null;
    }

    /// <summary>[중지]: 도는 '!' 명령을 프로세스 트리째 끈다. 결과(지금까지 출력 + 중지)는 그대로 지휘자에게 간다.</summary>
    public bool StopShell(string roomId)
    {
        if (!_rooms.TryGetValue(roomId, out var live)) return false;
        ShellRun? run;
        lock (live.Gate) run = live.Shell;
        if (run is null) return false;
        run.Stopped = true;
        PipedProcess? process;
        lock (run.Sync) process = run.Process;
        process?.Kill(); // 띄우는 중이면 RunShell이 Stopped를 보고 끈다
        return true;
    }

    private async Task FollowShellAsync(string roomId, Live live, ShellRun run, PipedProcess process)
    {
        void Flush()
        {
            string chunk;
            lock (run.Sync) chunk = run.TakeUnsent();
            if (chunk.Length > 0) Post(roomId, () => new LiveEvent("shell-output") { Text = chunk });
        }

        var pump = process.PumpAsync((index, bytes) =>
        {
            var stream = run.Streams[index];
            var text = stream.Feed(bytes);
            lock (run.Sync) run.Keep(text);
        });
        using var done = new CancellationTokenSource();
        var ticker = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(ShellFlushEvery);
            try { while (await timer.WaitForNextTickAsync(done.Token).ConfigureAwait(false)) Flush(); }
            catch (OperationCanceledException) { }
        });

        var exit = process.WaitForExitAsync();
        var timedOut = false;
        using (var limit = new CancellationTokenSource())
        {
            var delay = Task.Delay(ShellLimit, limit.Token);
            if (await Task.WhenAny(exit, delay).ConfigureAwait(false) != exit)
            {
                timedOut = true;
                process.Kill();
            }
            limit.Cancel(); // 명령마다 10분 타이머가 남지 않게 (O2)
        }
        int? code = null;
        try { code = await exit.ConfigureAwait(false); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        // 셸이 끝나도 떨어져 나간 자손이 파이프를 잡고 있을 수 있다 — 끝내고 잠깐만 기다린다 (PipedProcess.Finish)
        process.Finish();
        await Task.WhenAny(pump, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        done.Cancel();
        await ticker.ConfigureAwait(false);
        lock (run.Sync)
            foreach (var stream in run.Streams) run.Keep(stream.Finish());
        Flush();

        string output;
        lock (run.Sync) output = run.Output();
        var took = DateTimeOffset.Now - run.StartedAt;
        var ending = timedOut ? $"시간 초과({ShellLimit.TotalMinutes:0}분)로 끔"
            : run.Stopped ? "사용자가 중지"
            : code is 0 ? null
            : code is { } c ? $"종료 코드 {c}" : "종료 코드 모름";
        var state = $"{ending ?? "종료 0"}, {took.TotalSeconds:0.0}초";
        lock (live.Gate)
        {
            if (live.Shell == run) live.Shell = null;
            Broadcast(live, new LiveEvent("shell-end") { Text = $"! {run.Command} ({state})", Detail = state, Ok = ending is null });
        }
        process.Dispose();
        Log?.Note(roomId, ShellTool, $"! {run.Command} ({state})");

        if (_disposed) return; // 앱 종료 중이면 보내지 않는다 (O5)
        if (Enqueue(roomId, ShellPrompt(run.Command, output, ending), interrupt: false) is { } why)
            Notice(live, $"'!' 결과를 지휘자에게 보내지 못했습니다: {why}");
    }

    /// <summary>
    /// 지휘자에게 보내는 말 (CLI bash 모드 기록과 같은 태그). 출력 · 명령 속 묶음 닫는 태그와 규칙 알림 태그는 대소문자와 상관없이 깨 둔다 —
    /// 출력에 섞인 남의 글(README · 커밋 메시지)이 묶음을 일찍 끝내거나 하네스 알림처럼 보이지 않게.
    /// 전체가 보낼 수 있는 길이(<see cref="MaxPromptChars"/>)를 넘지 않는다 — 출력은 앞 · 뒤 <see cref="ShellKeepChars"/>자까지.
    /// </summary>
    internal static string ShellPrompt(string command, string output, string? ending)
    {
        var sb = new StringBuilder()
            .Append("<bash-input>").Append(ShellSafe(command)).Append("</bash-input>\n")
            .Append("<bash-stdout>").Append(ShellSafe(output.TrimEnd('\n'))).Append("</bash-stdout>");
        if (ending is not null) sb.Append("\n<bash-stderr>").Append(ending).Append("</bash-stderr>");
        return sb.ToString();
    }

    internal static string ShellSafe(string s) => ShellTagRegex().Replace(s, "<​$1");

    [GeneratedRegex(@"<(/?(?:bash-|system-reminder))", RegexOptions.IgnoreCase)]
    private static partial Regex ShellTagRegex();

    /// <summary>snapshot용: 지금 도는 '!' 명령 (없으면 null). live.Gate 안에서 — 출력은 run.Sync로 (C3).</summary>
    private static ShellView? ShellSnapshot(Live live)
    {
        if (live.Shell is not { } run) return null;
        lock (run.Sync) return new ShellView(run.Command, run.StartedAt, run.SentView());
    }
}
