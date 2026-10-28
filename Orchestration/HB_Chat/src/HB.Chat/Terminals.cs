using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace HB.Chat;

/// <summary>터미널 종류 (결정 3 "나눠서 둘 다"): 지휘자 프로세스(파이프 · 읽기 전용 · 지휘자 도구) · 내 셸(ConPTY · 입력 · 지휘자에게 안 보임). 화면에는 이름 글자로.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<TerminalKind>))]
public enum TerminalKind { Agent, Shell }

/// <summary>화면 · 지휘자에게 보이는 터미널 하나.</summary>
public sealed record TerminalView(string Id, TerminalKind Kind, string Name, string Command, bool Running, int? ExitCode, DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt, string? StoppedBy);

/// <summary>
/// 터미널 하나 (프로세스 + 출력 고리 버퍼). 출력은 바이트 기준 — 오프셋은 처음부터 쓴 바이트 수라
/// 화면이 버퍼를 받은 뒤 그보다 앞선 실시간 조각을 버릴 수 있다. 지휘자 프로세스의 버퍼는 줄 글(UTF-8, \r\n)이다.
/// </summary>
public sealed class TerminalSession
{
    private readonly Lock _gate = new();
    private readonly LinkedList<byte[]> _chunks = new();
    private long _buffered;
    private long _end;

    internal TerminalSession(string roomId, TerminalKind kind, string name, string command)
    {
        RoomId = roomId;
        Kind = kind;
        Name = name;
        Command = command;
    }

    public string Id { get; } = Guid.NewGuid().ToString("N")[..10];
    public string RoomId { get; }
    public TerminalKind Kind { get; }
    public string Name { get; }
    public string Command { get; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
    public DateTimeOffset? EndedAt { get; private set; }
    public int? ExitCode { get; private set; }
    /// <summary>끝낸 쪽 (사용자 · 지휘자 · 권한 낮춤 · 방 삭제 · 앱 종료). 스스로 끝났으면 null.</summary>
    public string? StoppedBy { get; private set; }
    public bool Running => EndedAt is null;

    internal ITerminalProcess? Process { get; set; }

    public TerminalView View
    {
        get { lock (_gate) return new(Id, Kind, Name, Command, Running, ExitCode, StartedAt, EndedAt, StoppedBy); }
    }

    /// <summary>덧붙이고 그 조각의 시작 오프셋을 준다. 상한을 넘으면 앞 조각부터 버린다.</summary>
    internal long Append(byte[] chunk)
    {
        lock (_gate)
        {
            var offset = _end;
            _chunks.AddLast(chunk);
            _buffered += chunk.Length;
            _end += chunk.Length;
            while (_buffered > Terminals.BufferBytes && _chunks.Count > 1)
            {
                _buffered -= _chunks.First!.Value.Length;
                _chunks.RemoveFirst();
            }
            return offset;
        }
    }

    /// <summary>버퍼 전체와 그 끝 오프셋.</summary>
    public (byte[] Data, long End) Snapshot()
    {
        lock (_gate)
        {
            var data = new byte[_buffered];
            var at = 0;
            foreach (var c in _chunks)
            {
                c.CopyTo(data, at);
                at += c.Length;
            }
            return (data, _end);
        }
    }

    internal bool MarkStopping(string by)
    {
        lock (_gate)
        {
            if (EndedAt is not null || StoppedBy is not null) return false;
            StoppedBy = by;
            return true;
        }
    }

    internal bool StopRequested
    {
        get { lock (_gate) return StoppedBy is not null; }
    }

    internal void MarkEnded(int code)
    {
        lock (_gate)
        {
            EndedAt ??= DateTimeOffset.Now;
            ExitCode ??= code;
        }
    }

    // ---- 지휘자 프로세스 출력을 줄 글로 (파이프 바이트 → UTF-8 글, 줄 끝 \r\n) ----
    private Decoder _decoder = new UTF8Encoding(false, throwOnInvalidBytes: true).GetDecoder();
    private bool _ansi;
    private bool _lastCr;

    /// <summary>
    /// 파이프 출력 조각을 화면용 UTF-8 글로: UTF-8이 아니면(한국어 Windows의 ANSI 코드 페이지 출력) 그 뒤로는 시스템 ANSI로 읽는다.
    /// 홀로 선 \n은 \r\n으로 (xterm은 \n만으로 줄 처음으로 가지 않는다).
    /// </summary>
    internal byte[] Normalize(byte[] chunk)
    {
        string text;
        try
        {
            var chars = new char[_decoder.GetCharCount(chunk, 0, chunk.Length, flush: false)];
            text = new string(chars, 0, _decoder.GetChars(chunk, 0, chunk.Length, chars, 0, flush: false));
        }
        catch (DecoderFallbackException) when (!_ansi)
        {
            _ansi = true;
            _decoder = Terminals.AnsiEncoding.GetDecoder();
            var chars = new char[_decoder.GetCharCount(chunk, 0, chunk.Length, flush: false)];
            text = new string(chars, 0, _decoder.GetChars(chunk, 0, chunk.Length, chars, 0, flush: false));
        }
        var sb = new StringBuilder(text.Length + 16);
        foreach (var ch in text)
        {
            if (ch == '\n' && !_lastCr) sb.Append('\r');
            sb.Append(ch);
            _lastCr = ch == '\r';
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}

/// <summary>
/// 방마다 터미널 (계획서 2026-10-29_HB_Chat-터미널-곁칸, 결정 3): 앱이 띄우고 붙잡는다 — 턴이 끝나도 살아 있고 앱이 끝나면 앱 Job으로 함께 끝난다.
/// 지휘자 프로세스는 앱 MCP 도구(terminal_start)가 승인 카드를 거친 뒤 띄우고, 내 셸은 사용자가 곁칸 "+"로 연다.
/// 실행마다 토큰을 내준다 — 앱 MCP는 이 토큰으로 어느 방 실행의 호출인지 가린다(없거나 지난 토큰은 거절).
/// </summary>
public sealed partial class Terminals : IDisposable
{
    public const int MaxPerRoom = 4;
    public const int BufferBytes = 1 << 20;
    public const int MaxCommandChars = 2000;
    public const int ReadMaxLines = 200;
    public const int ReadMaxChars = 20_000;
    private const short DefaultCols = 120, DefaultRows = 30;

    /// <summary>내 셸 종류 (결정 3: 기본 PowerShell, ▾로 PowerShell · 명령 프롬프트).</summary>
    public static readonly IReadOnlyDictionary<string, string> Shells = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["powershell"] = "PowerShell",
        ["cmd"] = "명령 프롬프트",
    };

    internal static readonly Encoding AnsiEncoding = LoadAnsi();

    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<TerminalSession>> _byRoom = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _tokens = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>출력 조각 (세션, 조각, 시작 오프셋). 읽기 스레드에서 부른다.</summary>
    public event Action<TerminalSession, byte[], long>? Output;

    /// <summary>시작 · 끝 등 목록이 바뀜 (방 ID).</summary>
    public event Action<string>? Changed;

    /// <summary>시작됨 · 끝남 (방 기록 줄용).</summary>
    public event Action<TerminalSession>? Started;
    public event Action<TerminalSession>? Ended;

    /// <summary>지휘자 터미널 이름: 글자 · 숫자 · . _ - 만 32자까지.</summary>
    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    /// <summary>
    /// 명령 모양 검사 — 줄바꿈 · NUL이 있으면 cmd가 앞부분만 돌린다. 보이지 않는 서식 문자(양방향 재배열 · 폭 0)와 그 밖의 제어 문자는
    /// 카드에 보인 글과 실행되는 글을 다르게 만들 수 있어 받지 않는다 (/self-review security a3a9dd85c1 C3). 문제가 없으면 null.
    /// </summary>
    public static string? WhyBadCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "명령이 비었습니다.";
        if (command.Length > MaxCommandChars) return $"명령은 {MaxCommandChars}자까지입니다.";
        if (command.IndexOfAny(['\r', '\n', '\0']) >= 0) return "명령에 줄바꿈을 넣을 수 없습니다 — 한 줄로 쓰세요 (여러 명령은 && 로 잇기).";
        foreach (var ch in command)
            if (char.IsControl(ch) || CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.Format)
                return $"명령에 보이지 않는 제어 · 서식 문자(U+{(int)ch:X4})가 있습니다 — 카드에 보이는 글과 실행되는 글이 달라질 수 있어 받지 않습니다.";
        return null;
    }

    // ---- 실행 토큰 ----

    /// <summary>그 방 실행 하나의 토큰을 낸다 — 실행이 끝나면 <see cref="Revoke"/>.</summary>
    public string Issue(string roomId)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        lock (_gate) _tokens[token] = roomId;
        return token;
    }

    /// <summary>토큰을 거두고, 그 방에 쓰지 않은 승인 표도 버린다 — 카드를 허용한 뒤 실행이 끝나면 다음 실행이 카드 없이 띄우지 못하게 (/self-review 57b59ef134 C10).</summary>
    public void Revoke(string token)
    {
        lock (_gate)
        {
            if (!_tokens.Remove(token, out var room)) return;
            _approved.RemoveAll(a => a.Room == room);
        }
    }

    /// <summary>토큰의 방 (없거나 지난 토큰이면 null). 길이가 다른 값은 바로 거절.</summary>
    public string? RoomFor(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length != 48) return null;
        lock (_gate) return _tokens.TryGetValue(token, out var room) ? room : null;
    }

    // ---- 승인 표 ----

    private readonly List<(string Room, string Name, string Command, DateTimeOffset At)> _approved = [];

    /// <summary>
    /// 승인 카드에서 허용한 시작 하나를 적어 둔다 — terminal_start는 같은 방 · 이름 · 명령의 표를 하나 써야 띄운다.
    /// 토큰이 새어도 카드 없이는 띄울 수 없게 (카드는 CLI 승인 요청 → 앱 호스트에서만 뜬다). 5분 · 그 실행이 끝나면 버린다.
    /// </summary>
    public void Approve(string roomId, string name, string command)
    {
        lock (_gate)
        {
            _approved.RemoveAll(a => DateTimeOffset.Now - a.At > TimeSpan.FromMinutes(5));
            _approved.Add((roomId, name, command, DateTimeOffset.Now));
        }
    }

    internal bool TakeApproval(string roomId, string name, string command)
    {
        lock (_gate)
        {
            var i = _approved.FindIndex(a => a.Room == roomId && a.Name == name && a.Command == command && DateTimeOffset.Now - a.At <= TimeSpan.FromMinutes(5));
            if (i < 0) return false;
            _approved.RemoveAt(i);
            return true;
        }
    }

    // ---- 터미널 ----

    /// <summary>그 방 터미널 (kind를 주면 그 종류만).</summary>
    public IReadOnlyList<TerminalView> List(string roomId, TerminalKind? kind = null)
    {
        lock (_gate)
            return _byRoom.TryGetValue(roomId, out var list) ? [.. list.Where(s => kind is null || s.Kind == kind).Select(s => s.View)] : [];
    }

    /// <summary>ID로 (화면). 종류를 주면 그 종류만.</summary>
    public TerminalSession? Get(string roomId, string? id, TerminalKind? kind = null)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (_gate)
            return _byRoom.TryGetValue(roomId, out var list) ? list.LastOrDefault(s => s.Id == id && (kind is null || s.Kind == kind)) : null;
    }

    /// <summary>지휘자 터미널을 ID 또는 이름으로 (이름이 겹치면 가장 최근 것). 내 셸은 지휘자에게 안 보인다.</summary>
    public TerminalSession? GetAgent(string roomId, string? idOrName)
    {
        if (string.IsNullOrEmpty(idOrName)) return null;
        lock (_gate)
        {
            if (!_byRoom.TryGetValue(roomId, out var list)) return null;
            var agents = list.Where(s => s.Kind == TerminalKind.Agent).ToList();
            return agents.LastOrDefault(s => s.Id == idOrName) ?? agents.LastOrDefault(s => string.Equals(s.Name, idOrName, StringComparison.Ordinal));
        }
    }

    /// <summary>지휘자 프로세스를 띄운다 (파이프). 실패면 이유(사람이 읽는 글), 성공이면 null과 세션.</summary>
    public string? Start(string roomId, string name, string command, string workingDirectory, out TerminalSession? session)
    {
        session = null;
        if (!IsValidName(name)) return "이름은 글자 · 숫자 · . _ - 만 32자까지입니다.";
        if (WhyBadCommand(command) is { } bad) return bad;
        return Launch(roomId, TerminalKind.Agent, name, command, workingDirectory, () => PipedProcess.Start(command, workingDirectory), out session);
    }

    /// <summary>내 셸을 연다 (ConPTY, 곁칸 "+"). shell = <see cref="Shells"/>의 키.</summary>
    public string? OpenShell(string roomId, string shell, string workingDirectory, out TerminalSession? session)
    {
        session = null;
        if (!Shells.TryGetValue(shell, out var label)) return "모르는 셸입니다.";
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        // 전체 경로로 — 작업 폴더 · PATH의 같은 이름 실행 파일이 대신 뜨지 않게
        var commandLine = shell == "powershell"
            ? $"\"{Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe")}\" -NoLogo"
            : $"\"{Path.Combine(system, "cmd.exe")}\"";
        int count;
        lock (_gate) count = _byRoom.TryGetValue(roomId, out var l) ? l.Count(s => s.Kind == TerminalKind.Shell && s.Command == shell) : 0;
        return Launch(roomId, TerminalKind.Shell, $"{label} {count + 1}", shell, workingDirectory,
            () => PseudoConsole.Start(commandLine, workingDirectory, DefaultCols, DefaultRows), out session);
    }

    private string? Launch(string roomId, TerminalKind kind, string name, string command, string workingDirectory, Func<ITerminalProcess> start,
        out TerminalSession? session)
    {
        session = null;
        if (!Directory.Exists(workingDirectory)) return "작업 폴더가 없습니다.";
        var created = new TerminalSession(roomId, kind, name, command);
        lock (_gate)
        {
            if (_disposed) return "앱이 끝나는 중입니다.";
            var list = _byRoom.TryGetValue(roomId, out var l) ? l : _byRoom[roomId] = [];
            if (list.Any(s => s.Running && s.Kind == kind && s.Name == name)) return $"'{name}' 터미널이 이미 돌고 있습니다 — 다른 이름을 쓰거나 먼저 중지하세요.";
            if (list.Count(s => s.Running && s.Kind == kind) >= MaxPerRoom)
                return kind == TerminalKind.Agent
                    ? $"이 방 지휘자 터미널은 동시에 {MaxPerRoom}개까지입니다 — 쓰지 않는 것을 먼저 중지하세요."
                    : $"내 셸은 방마다 {MaxPerRoom}개까지입니다.";
            list.RemoveAll(s => !s.Running && s.Kind == kind && s.Name == name); // 같은 이름의 끝난 것은 새것으로 바꾼다
            // 끝난 것은 출력(최대 1MB씩)을 들고 남는다 — 오래된 것부터 버려 방마다 상한 안으로
            while (list.Count >= MaxPerRoom * 4 && list.FirstOrDefault(s => !s.Running) is { } old) list.Remove(old);
            list.Add(created);
        }
        try
        {
            created.Process = start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or PlatformNotSupportedException or InvalidOperationException)
        {
            lock (_gate) _byRoom[roomId].Remove(created);
            return "띄우지 못했습니다: " + ex.Message;
        }
        // 띄우는 사이에 중지 · 방 삭제가 왔으면 표시만 남고 끝내지 못했다 — 지금 끝낸다 (/self-review 57b59ef134 C6 · 지휘자 K1)
        if (created.StopRequested) created.Process.Kill();
        session = created;
        _ = PumpAsync(created);
        Raise(() => Started?.Invoke(created));
        Raise(() => Changed?.Invoke(roomId));
        return null;
    }

    /// <summary>처리기(화면 알림 · 기록 줄)의 실패가 터미널 관리를 멈추지 않게 (/self-review 57b59ef134 C3).</summary>
    private static void Raise(Action raise)
    {
        try { raise(); } catch { /* 처리기 쪽 실패 */ }
    }

    private async Task PumpAsync(TerminalSession s)
    {
        var process = s.Process!;
        var reading = process.PumpAsync(chunk =>
        {
            var data = s.Kind == TerminalKind.Agent ? s.Normalize(chunk) : chunk;
            if (data.Length == 0) return;
            var offset = s.Append(data);
            Raise(() => Output?.Invoke(s, data, offset));
        });
        var code = -1;
        try
        {
            code = await process.WaitForExitAsync();
            // 끝난 뒤 남은 출력이 흘러나오게 잠깐 두고, 자손을 끝내고(ConPTY는 콘솔도 닫아야) 읽기를 끝낸다
            await Task.WhenAny(reading, Task.Delay(300));
            process.Finish();
            await Task.WhenAny(reading, Task.Delay(2000));
        }
        finally
        {
            s.MarkEnded(code);
            process.Dispose();
            Raise(() => Ended?.Invoke(s));
            Raise(() => Changed?.Invoke(s.RoomId));
        }
    }

    /// <summary>끝낸다 (트리째). by = 사용자 · 지휘자. 없거나 이미 끝났으면 이유.</summary>
    public string? Stop(string roomId, string? id, string by) => Stop(Get(roomId, id), by);

    /// <summary>지휘자 터미널을 ID 또는 이름으로 끝낸다 (terminal_stop).</summary>
    public string? StopAgent(string roomId, string? idOrName, string by) => Stop(GetAgent(roomId, idOrName), by);

    private static string? Stop(TerminalSession? s, string by)
    {
        if (s is null) return "그런 터미널이 없습니다.";
        if (!s.MarkStopping(by)) return "이미 끝난 터미널입니다.";
        s.Process?.Kill(); // 아직 띄우는 중이면 Launch가 표시를 보고 끝낸다
        return null;
    }

    /// <summary>끝난 터미널을 목록에서 뺀다 (곁칸 "닫기").</summary>
    public bool Remove(string roomId, string id)
    {
        bool removed;
        lock (_gate) removed = _byRoom.TryGetValue(roomId, out var list) && list.RemoveAll(s => s.Id == id && !s.Running) > 0;
        if (removed) Raise(() => Changed?.Invoke(roomId));
        return removed;
    }

    /// <summary>그 방 터미널을 끝낸다 (kind를 주면 그 종류만 — 권한 낮춤은 지휘자 프로세스만, 방 삭제는 모두).</summary>
    public void StopRoom(string roomId, string by, TerminalKind? kind = null)
    {
        List<TerminalSession> running;
        lock (_gate) running = _byRoom.TryGetValue(roomId, out var list) ? [.. list.Where(s => s.Running && (kind is null || s.Kind == kind))] : [];
        foreach (var s in running)
            if (s.MarkStopping(by)) s.Process?.Kill();
    }

    /// <summary>휴지통 30일 정리로 지운 방: 그 방 터미널을 끝내고 목록(끝난 것의 버퍼 포함)을 버린다 (/self-review 57b59ef134 C12).</summary>
    public void Forget(string roomId)
    {
        StopRoom(roomId, "방 삭제");
        lock (_gate) _byRoom.Remove(roomId);
    }

    /// <summary>곁칸에서 친 키 — 내 셸만 (결정 "입력도 (ConPTY)", 지휘자 프로세스는 읽기 전용).</summary>
    public bool Write(string roomId, string id, ReadOnlySpan<byte> data) =>
        Get(roomId, id, TerminalKind.Shell) is { Running: true, Process: { } process } && process.Write(data);

    public bool Resize(string roomId, string id, int cols, int rows)
    {
        if (cols is < 10 or > 500 || rows is < 3 or > 300) return false;
        if (Get(roomId, id, TerminalKind.Shell) is not { Running: true, Process: { } process }) return false;
        process.Resize((short)cols, (short)rows);
        return true;
    }

    /// <summary>지휘자용 출력: 제어 문자(색 등)를 걷은 마지막 lines줄 (글자 상한 있음). 지휘자 프로세스는 줄 글이라 화면 다시 그리기가 없다.</summary>
    public static string ReadText(TerminalSession s, int lines)
    {
        // 끝의 빈 줄을 먼저 걷고 마지막 N줄 — 출력은 보통 줄바꿈으로 끝난다
        var all = PlainText(Encoding.UTF8.GetString(s.Snapshot().Data)).TrimEnd('\n').Split('\n');
        var result = string.Join('\n', all.Skip(Math.Max(0, all.Length - Math.Clamp(lines, 1, ReadMaxLines))));
        return result.Length > ReadMaxChars ? "…" + result[^ReadMaxChars..] : result;
    }

    /// <summary>VT 제어열 · 그 밖의 제어 문자를 걷는다. \r\n → \n, 홀로 선 \r은 줄바꿈으로.</summary>
    public static string PlainText(string vt)
    {
        var s = Escapes().Replace(vt, "");
        s = s.Replace("\r\n", "\n").Replace('\r', '\n');
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            if (ch == '\n' || ch == '\t' || !char.IsControl(ch)) sb.Append(ch);
        return string.Join('\n', sb.ToString().Split('\n').Select(l => l.TrimEnd()));
    }

    public void Dispose()
    {
        List<TerminalSession> all;
        lock (_gate)
        {
            _disposed = true;
            all = [.. _byRoom.Values.SelectMany(l => l).Where(s => s.Running)];
        }
        foreach (var s in all)
        {
            s.MarkStopping("앱 종료");
            s.Process?.Kill();
        }
    }

    private static Encoding LoadAnsi()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return Encoding.Latin1; }
    }

    [GeneratedRegex(@"^[\p{L}\p{N}._-]{1,32}$")]
    private static partial Regex NamePattern();

    // CSI(ESC [ … 끝 글자) · OSC(ESC ] … BEL 또는 ESC \) · 그 밖의 두 글자 ESC 열
    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)|\x1B[@-Z\\-_]")]
    private static partial Regex Escapes();
}
