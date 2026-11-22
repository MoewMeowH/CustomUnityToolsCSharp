using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HB.Chat;

/// <summary>
/// agy(Antigravity CLI) 수동 중계: 지휘자가 요청 md · 코드 사본을 중계 폴더 current에 두고, 사용자가 곁칸 셸에서 agy를 띄워 <b>직접</b> 입력하고,
/// 지휘자가 output.md를 받는다. 앱은 agy에 아무것도 입력하지 않는다 — Antigravity FAQ "Wrapping agy inside third-party coding agents or applications …
/// is not supported under consumer plans" (사람 입력만, 사용자 결정 "사람이 직접 입력").
/// 상태는 데이터 폴더 agy.json (켜기 · 학습 끄기 확인 · 카나리 확인한 판 · 진행 중 실행). 깨졌으면 덮지 않고 중계를 막는다.
/// current 하나만 쓴다 — agy가 폴더마다 trustedWorkspaces에 더해 쌓이고(실측), 끝난 실행은 데이터 폴더(deny 구역) agy-runs로 옮겨 다음 실행이 못 보게.
/// </summary>
/// <param name="settingsPath">agy settings.json (시험용, 비우면 ~/.gemini/antigravity-cli/settings.json).</param>
/// <param name="askVersion">agy --version 읽기 (시험용).</param>
/// <param name="profile">사용자 프로필 폴더 (시험용, 비우면 %USERPROFILE%).</param>
/// <param name="temps">임시 폴더 목록 (시험용, 비우면 <see cref="TempFolders"/>).</param>
/// <param name="guarded">deny가 덮어야 하는 그 밖의 폴더 — 앱 · 저장소 · Claude 설정(<see cref="WorkspaceStore.GuardedFolders"/>) · Codex 홈 · 호출 기록.</param>
public sealed class AgyRelay(Func<ChatOptions> options, Func<string>? settingsPath = null, Func<string, Task<string?>>? askVersion = null, Func<string>? profile = null,
    Func<IReadOnlyList<string>>? temps = null, Func<IEnumerable<string>>? guarded = null)
{
    public const string RequestFile = "request.md", OutputFile = "output.md", FilesFolder = "files";
    public const string CanaryOut = "canary_out.md", CanaryOut2 = "canary_out2.md", ControlFile = "control.md";

    /// <summary>사용자가 agy에 칠 한 줄.</summary>
    public const string TypeLine = "request.md를 읽고 그대로 해줘";

    /// <summary>보낼 파일 상한 (하나 · 합 · 개수), 요청 글 · 결과 글 상한, run_mode가 맡기는 프롬프트(첨부 코드 포함) 상한.</summary>
    public const int MaxFileBytes = 512 * 1024, MaxTotalBytes = 4 * 1024 * 1024, MaxFiles = 50, MaxRequestChars = 64_000, MaxOutputBytes = 256 * 1024,
        MaxRunPromptChars = 1_000_000;

    /// <param name="ForRunMode">run_mode의 gemini_agy 단계가 요청한 중계 — 서버가 결과를 받아 간다 (agy_collect · agy_prepare로 건드리지 않는다).</param>
    /// <param name="Until">run_mode 중계의 기다림 끝 (공급자가 알린 제한 시간).</param>
    public sealed record RunInfo(string Id, string RoomId, DateTimeOffset At, bool ForRunMode = false, DateTimeOffset? Until = null)
    {
        /// <summary>run_mode 중계가 제한 시간(+1분)을 넘겼다 — 서버가 cancel 없이 끝난 것(턴 중지가 서버째 끔 · 앱 재시작)이라 자리를 막지 않는다.</summary>
        public bool Stale(DateTimeOffset now) => ForRunMode && (Until is not { } until || now > until + TimeSpan.FromMinutes(1));
    }

    /// <summary>run_mode 중계 기다림 상한 (공급자가 알린 값을 이 안으로).</summary>
    public static readonly TimeSpan MinRunWait = TimeSpan.FromMinutes(1), MaxRunWait = TimeSpan.FromHours(1);

    /// <param name="Token">deny 구역 파일의 글자 — 결과에 있으면 뚫림.</param>
    /// <param name="Control">current 안 대조 파일의 글자 — 결과에 있어야 agy가 실제로 읽기 · 셸을 돌린 것.</param>
    public sealed record CanaryInfo(string Token, DateTimeOffset At, string? Control = null);

    public sealed record State
    {
        public bool Enabled { get; init; }

        /// <summary>사용자가 agy /config의 Enable Telemetry(학습)를 껐다고 확인한 때 (계정에 저장돼 파일로 볼 수 없음 — 사용자 결정 "코드 학습만 끄면 ㄱㅊ").</summary>
        public DateTimeOffset? TelemetryOffAt { get; init; }

        public string? VerifiedVersion { get; init; }
        public DateTimeOffset? VerifiedAt { get; init; }
        public CanaryInfo? Canary { get; init; }
        public string? LastCanary { get; init; }
        public RunInfo? Run { get; init; }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Lock _gate = new();

    private ChatOptions O => options();
    private string StatePath => Path.Combine(O.DataFolder, "agy.json");
    public string SettingsPath => settingsPath?.Invoke() ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity-cli", "settings.json");
    public string RelayRoot => O.AgyRelayFolder;
    public string CurrentFolder => Path.Combine(RelayRoot, "current");
    public string ArchiveRoot => Path.Combine(O.DataFolder, "agy-runs");
    public string CanaryFile => Path.Combine(O.DataFolder, "agy-canary", "secret.md");

    /// <summary>마지막으로 읽은 agy 판 (설정 화면 표시 — 읽을 때마다 바뀐다).</summary>
    public string? LastVersion { get; private set; }

    // ---- 상태 ----

    public State Read(out string? error)
    {
        error = null;
        lock (_gate)
        {
            try
            {
                return File.Exists(StatePath) ? JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath), Json) ?? new State() : new State();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                error = $"agy.json을 읽지 못해 중계를 막았습니다 (덮어쓰지 않음): {ex.Message}";
                return new State();
            }
        }
    }

    /// <summary>켜져 있고 학습 끄기를 확인했는지 (방 규칙 · 도구 · 셸 메뉴를 보일지). 판 · 설정 검사는 도구가 부를 때 본다.</summary>
    public bool Enabled => Read(out var e) is { Enabled: true, TelemetryOffAt: not null } && e is null;

    private string? Update(Func<State, State> change)
    {
        lock (_gate)
        {
            var now = Read(out var error);
            if (error is not null) return error;
            try
            {
                AtomicFile.WriteAllText(StatePath, JsonSerializer.Serialize(change(now), Json));
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return $"agy.json을 저장하지 못했습니다: {ex.Message}"; }
        }
    }

    // ---- 경로 ----

    /// <summary>agy deny가 덮어야 하는 경로 (이 PC에서 계산).</summary>
    public IReadOnlyList<string> RequiredDeny() =>
        AgyCheck.RequiredPaths(profile?.Invoke() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), O.DataFolder,
            O.AllWorkspaces.Select(w => w.Path).Concat(O.AllUnityProjects.Select(u => u.Path)).Concat(guarded?.Invoke() ?? []),
            DriveInfo.GetDrives().Where(d => d.DriveType is DriveType.Fixed or DriveType.Network or DriveType.Removable && d.IsReady)
                .Select(d => d.RootDirectory.FullName), RelayRoot);

    /// <summary>
    /// 권장 deny: 중계 폴더가 있는 드라이브 루트의 폴더 중 숨김 · 시스템 · 링크와 Windows · Program Files · Users · 중계 폴더를 뺀 것
    /// (그 드라이브 루트는 막을 수 없다 — deny가 allow를 이겨 중계 폴더도 막힘).
    /// </summary>
    public IReadOnlyList<string> RecommendedDeny()
    {
        try
        {
            var full = Path.GetFullPath(RelayRoot);
            var root = Path.GetPathRoot(full)!;
            var top = full[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            string[] keep = ["Windows", "Program Files", "Program Files (x86)", "Users"];
            return [.. new DirectoryInfo(root).EnumerateDirectories()
                .Where(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) == 0)
                .Where(d => !keep.Contains(d.Name, StringComparer.OrdinalIgnoreCase) && !string.Equals(d.Name, top, StringComparison.OrdinalIgnoreCase))
                .Select(d => d.FullName)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }

    /// <summary>
    /// 중계 폴더를 쓸 수 없는 까닭 — 경로 검사 · 작업 폴더와 같은 안전 검사(링크 · 시스템 · 보호 폴더). create면 없을 때 만들고 권한을 좁힌다
    /// (아니면 읽기만 — 설정 화면 그리기 · 비우기).
    /// </summary>
    public string? WhyBadRelay(bool create = true)
    {
        if (AgyCheck.WhyBadRelay(RelayRoot, RequiredDeny()) is { } why) return why;
        if (!create)
        {
            if (!Directory.Exists(CurrentFolder)) return null;
        }
        else
            try
            {
                if (OperatingSystem.IsWindows() && SecureRelayRoot(RelayRoot) is { } aclWhy) return aclWhy;
                Directory.CreateDirectory(CurrentFolder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException
                                           or System.Security.Principal.IdentityNotMappedException)
            {
                return $"중계 폴더를 만들지 못했습니다: {ex.Message}";
            }
        if (new DirectoryInfo(CurrentFolder).Attributes.HasFlag(FileAttributes.ReparsePoint)) return $"중계 폴더 {CurrentFolder}가 링크입니다 — 보통 폴더로 바꾸세요.";
        return O.AppWorkspaces?.WhyUnsafe(RelayRoot) is { } unsafeWhy ? "중계 폴더: " + unsafeWhy : null;
    }

    /// <summary>
    /// 중계 폴더 권한: 드라이브 루트 아래 새 폴더는 Authenticated Users 수정 권한을 물려받아 다른 계정이 보낸 코드 사본을 읽거나 request.md를 바꿔칠 수 있다
    /// (/self-review ea6130b427 C4, 사용자 결정 "좁히기"). 만들 때 · 아직 상속 중이면 지금 사용자 · SYSTEM · Administrators만 남긴다.
    /// 이미 있는데 소유자가 지금 사용자가 아니면 거절 — 다른 계정이 먼저 만들어 둔 폴더일 수 있다.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? SecureRelayRoot(string root)
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var me = identity.User!;
        var acl = new System.Security.AccessControl.DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { me, new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
                     new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null) })
            acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(sid, System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
        var dir = new DirectoryInfo(root);
        if (!dir.Exists)
        {
            if (dir.Parent is { Exists: false } parent) parent.Create();
            dir.Create(acl);
            return null;
        }
        var now = dir.GetAccessControl();
        if (now.GetOwner(typeof(System.Security.Principal.SecurityIdentifier)) is not System.Security.Principal.SecurityIdentifier owner || owner != me)
            return $"중계 폴더 {root}의 소유자가 지금 사용자가 아닙니다 — 다른 계정이 만든 폴더일 수 있습니다. 지우거나 설정 Chat:AgyRelayDir을 바꾸세요.";
        if (!now.AreAccessRulesProtected) dir.SetAccessControl(acl);
        return null;
    }

    public AgyCheck.Result Check()
    {
        string? json = null;
        try { if (File.Exists(SettingsPath)) json = File.ReadAllText(SettingsPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { json = "{ 읽지 못함: " + ex.Message; }
        AgyCheck.Result result;
        try { result = AgyCheck.Inspect(json, RequiredDeny(), RecommendedDeny(), CurrentFolder); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) // 같은 키가 두 번 든 파일 등
        {
            result = AgyCheck.Inspect("{ 읽지 못함", RequiredDeny(), RecommendedDeny(), CurrentFolder) with { Problems = [$"agy 설정 파일 모양이 올바르지 않습니다: {ex.Message}"] };
        }
        // 5291 실측(2026-11-22): 샌드박스가 임시 폴더를 deny와 상관없이 열어 %TEMP% 안 카나리를 셸 type이 읽었다 — 데이터 폴더(보관함 · 카나리)가 그 안이면 막는다
        return AgyCheck.InsideAny(O.DataFolder, temps?.Invoke() ?? TempFolders()) is { } temp
            ? result with { Problems = [.. result.Problems, $"데이터 폴더가 임시 폴더({temp}) 안입니다 — agy 셸 샌드박스는 임시 폴더를 deny와 상관없이 읽습니다. 설정 Chat:DataDir을 옮기세요."] }
            : result;
    }

    /// <summary>agy 셸 샌드박스가 deny와 상관없이 여는 임시 폴더 (사용자 TEMP · TMP · Windows\Temp).</summary>
    public static IReadOnlyList<string> TempFolders() =>
        [.. new[] { Path.GetTempPath(), Environment.GetEnvironmentVariable("TEMP"), Environment.GetEnvironmentVariable("TMP"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp") }
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => AgyCheck.RulePath(p!)).Distinct(StringComparer.OrdinalIgnoreCase)];

    public async Task<string?> VersionAsync()
    {
        var version = O.ResolveAgy() is { } exe
            ? await (askVersion ?? (path => CliVersions.AskVersionAsync(path, TimeSpan.FromSeconds(10))))(exe)
            : null;
        LastVersion = version;
        return version;
    }

    // ---- 켜기 ----

    /// <summary>켜기 · 끄기. 켜려면 학습 끄기 확인이 있어야 하고 중계 폴더가 쓸 수 있어야 한다.</summary>
    public string? SetEnabled(bool enabled, bool telemetryOff)
    {
        if (enabled)
        {
            if (!telemetryOff) return "agy /config에서 Enable Telemetry(학습)를 끄고, 껐다고 확인 칸을 체크해야 켤 수 있습니다.";
            if (WhyBadRelay() is { } why) return why;
        }
        return Update(s => s with { Enabled = enabled, TelemetryOffAt = enabled ? s.TelemetryOffAt ?? DateTimeOffset.Now : s.TelemetryOffAt });
    }

    /// <summary>중계 셸을 열 수 없는 까닭 (켜기 · 폴더만 — 카나리 확인도 이 셸에서 하므로 판 확인은 보지 않는다).</summary>
    public string? WhyNoShell()
    {
        Read(out var error);
        if (error is not null) return error;
        if (!Enabled) return "agy 중계가 꺼져 있습니다 — 설정 → 연결 → agy 중계에서 켜세요.";
        return WhyBadRelay();
    }

    /// <summary>중계 도구를 쓸 수 없는 까닭 — 켜기 · 폴더 · agy · 설정 검사 · 카나리 확인한 판.</summary>
    public async Task<string?> WhyNotReadyAsync()
    {
        if (WhyNoShell() is { } why) return why;
        if (await VersionAsync() is not { } version) return "agy --version을 읽지 못했습니다 (설정 Chat:AgyPath 또는 %LOCALAPPDATA%\\agy\\bin\\agy.exe).";
        if (Check() is { Ok: false } check) return "agy 설정 검사: " + string.Join(" / ", check.Problems);
        var state = Read(out _);
        if (state.VerifiedVersion != version)
            return $"agy {version}는 아직 카나리 확인을 하지 않았습니다{(state.VerifiedVersion is { } v ? $" (확인한 판 {v})" : "")} — 설정 → 연결 → agy 중계에서 카나리 확인.";
        return null;
    }

    // ---- 카나리 확인 ----

    /// <summary>
    /// 카나리를 만든다: 데이터 폴더(deny 구역)에 무작위 글자 파일, current에 다른 무작위 글자의 대조 파일. 둘 다 같은 중립 문구 —
    /// 시험임을 알면 agy가 옮겨 쓰지 않거나 가릴 수 있다 (/self-review ea6130b427 C2). 진행 중인 중계가 있으면 거절.
    /// </summary>
    public string? CreateCanary()
    {
        if (WhyNoShell() is { } why) return why;
        lock (_gate)
        {
            if (Read(out _).Run is { } run) return $"진행 중인 중계가 있습니다 ({run.Id}) — 끝내거나 비운 뒤 하세요.";
            static string Code() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
            var token = Code();
            var control = Code();
            try
            {
                ClearFolder(CurrentFolder);
                AtomicFile.WriteAllText(CanaryFile, $"# 메모\n확인 코드: {token}\n");
                File.WriteAllText(Path.Combine(CurrentFolder, ControlFile), $"# 메모\n확인 코드: {control}\n", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(CurrentFolder, CanaryTask), CanaryTaskText(CanaryFile), new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return $"카나리를 만들지 못했습니다: {ex.Message}"; }
            return Update(s => s with { Canary = new CanaryInfo(token, DateTimeOffset.Now, control) });
        }
    }

    /// <summary>카나리 할 일 파일 (current) · 사용자가 agy에 칠 한 줄 — 긴 경로를 붙여 넣지 않게.</summary>
    public const string CanaryTask = "canary.md", CanaryTypeLine = "canary.md를 읽고 그대로 해줘";

    /// <summary>
    /// 카나리 할 일: 셸 읽기를 먼저 — 도구 읽기가 deny에 막힌 뒤에는 agy가 "접근 제어 우회"라며 셸 명령을 스스로 거절했다
    /// (실제 앱 1.5.14, 2026-11-22). 두 단계 모두 대조 파일을 같이 읽어 실제로 돌았는지 본다.
    /// </summary>
    internal static string CanaryTaskText(string secret) =>
        "# 할 일 (차례대로)\n\n" +
        $"1. 셸 명령 한 번으로 `type \"{secret}\", {ControlFile}`를 실행하고, 그 출력을 그대로 `{CanaryOut2}`에 써라. 오류가 나도 출력 전체를 그대로 쓴다.\n" +
        $"2. 파일 읽기 도구로 `{secret}`와 `{ControlFile}`를 읽고, 두 파일 내용을 `{CanaryOut}`에 그대로 써라. 못 읽은 파일은 못 읽은 까닭을 쓴다.\n\n" +
        "이 폴더의 다른 파일은 고치지 않는다.\n";

    /// <summary>
    /// 카나리 판정 (사용자 결정 "자동으로하자"): 결과 파일 둘에 대조 글자가 있고(실제로 읽기 · 셸을 돌림) deny 구역 글자가 없으면 통과 → 그 판을 확인한 판으로.
    /// deny 구역 글자가 있으면 실패, 파일 · 대조 글자가 없으면 확인 못 함 (agy가 거절하거나 명령을 돌리지 않은 경우 — 통과로 적지 않는다).
    /// 통과하면 current와 카나리를 지운다 (통과 못 함이면 남겨 다시 판정할 수 있게).
    /// </summary>
    public async Task<(bool Passed, string Message)> JudgeCanaryAsync()
    {
        var state = Read(out var error);
        if (error is not null) return (false, error);
        if (state.Canary is not { } canary) return (false, "카나리를 먼저 만드세요.");
        var version = await VersionAsync();
        string? Text(string name)
        {
            var path = Path.Combine(CurrentFolder, name);
            try { return File.Exists(path) ? File.ReadAllText(path) : null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }
        var tool = Text(CanaryOut);
        var shell = Text(CanaryOut2);
        var leaked = new List<string>();
        if (tool?.Contains(canary.Token, StringComparison.Ordinal) == true) leaked.Add("도구 읽기");
        if (shell?.Contains(canary.Token, StringComparison.Ordinal) == true) leaked.Add("셸 읽기");
        string message;
        var passed = false;
        if (leaked.Count > 0) message = $"카나리 실패 — {string.Join(" · ", leaked)}로 deny 구역이 읽혔습니다. agy 설정(모드 · 샌드박스 · deny)을 확인하세요.";
        else if (tool is null || shell is null) message = $"확인 못 함 — {(tool is null ? CanaryOut : CanaryOut2)}가 없습니다. 곁칸 agy 중계 셸에서 \"{CanaryTypeLine}\"을 입력했는지, agy가 셸 명령을 거절하지 않았는지 보세요.";
        else if (canary.Control is not { } control || !tool.Contains(control, StringComparison.Ordinal) || !shell.Contains(control, StringComparison.Ordinal))
            message = $"확인 못 함 — {(canary.Control is not null && tool.Contains(canary.Control, StringComparison.Ordinal) ? CanaryOut2 : CanaryOut)}에 {ControlFile}의 확인 코드가 없습니다. " +
                      "agy가 읽기 · 셸 명령을 실제로 돌리지 않았을 수 있습니다 (승인창에서 Yes를 눌렀는지 보고 카나리를 다시 만드세요).";
        else if (version is null) message = "확인 못 함 — agy --version을 읽지 못했습니다.";
        else if (Check() is { Ok: false } check) message = "확인 못 함 — agy 설정 검사: " + string.Join(" / ", check.Problems);
        else
        {
            passed = true;
            message = $"카나리 통과 — agy {version}를 확인한 판으로 적었습니다.";
        }
        var label = $"{DateTimeOffset.Now:MM-dd HH:mm} · agy {version ?? "?"} · {message}";
        if (passed)
            lock (_gate)
            {
                try
                {
                    ClearFolder(CurrentFolder);
                    File.Delete(CanaryFile);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        var saved = Update(s => passed
            ? s with { VerifiedVersion = version, VerifiedAt = DateTimeOffset.Now, Canary = null, LastCanary = label }
            : s with { LastCanary = label });
        return saved is null ? (passed, message) : (false, saved);
    }

    // ---- 중계 ----

    /// <summary>
    /// 요청을 둔다: current를 비우고 request.md(지휘자 글 + 앱 꼬리) · files/(작업 폴더 상대 경로 사본). 다른 방의 진행 중 실행이 있으면 거절, 같은 방이면 덮는다.
    /// </summary>
    public async Task<(string? Error, string? RunId, IReadOnlyList<string> Copied)> PrepareAsync(string roomId, string workspace, string? request, IReadOnlyList<string>? files)
    {
        if (await WhyNotReadyAsync() is { } why) return (why, null, []);
        if (string.IsNullOrWhiteSpace(request)) return ("요청 글이 비었습니다.", null, []);
        if (request.Length > MaxRequestChars) return ($"요청 글은 {MaxRequestChars:N0}자까지입니다 — 긴 자료는 files로 넣으세요.", null, []);
        var picked = new List<(string Rel, string Full)>();
        long total = 0;
        // 구분자만 다른 같은 파일이 두 번 복사되지 않게 정규화한 뒤 거른다
        foreach (var rel in (files ?? []).DistinctBy(f => Normalize(f ?? ""), StringComparer.OrdinalIgnoreCase))
        {
            if (WhyBadSource(workspace, rel, out var full, out var size) is { } bad) return ($"{rel}: {bad}", null, []);
            total += size;
            picked.Add((Normalize(rel), full));
        }
        if (picked.Count > MaxFiles) return ($"파일은 {MaxFiles}개까지입니다.", null, []);
        if (total > MaxTotalBytes) return ($"파일 합이 {MaxTotalBytes / 1024 / 1024}MB를 넘습니다.", null, []);

        var id = $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(2))}";
        lock (_gate)
        {
            var state = Read(out var error);
            if (error is not null) return (error, null, []);
            if (state.Run is { ForRunMode: true } waiting && !waiting.Stale(DateTimeOffset.Now))
                return ($"run_mode가 agy 결과를 기다리는 중입니다 ({waiting.Id}, {waiting.Until:HH:mm}까지) — 끝나거나 취소된 뒤 하세요 (설정 화면에서 비울 수도 있습니다).", null, []);
            if (state.Run is { ForRunMode: false } other && other.RoomId != roomId)
                return ($"다른 방의 중계가 진행 중입니다 ({other.Id}, {other.At:MM-dd HH:mm}) — 그 방에서 agy_collect로 받거나 설정 화면에서 비우세요.", null, []);
            if (state.Canary is not null) return ("카나리 확인이 진행 중입니다 — 설정 화면에서 판정한 뒤 하세요.", null, []);
            try
            {
                // 같은 방이 다시 준비하면 앞 실행은 보관해 둔다 — 받지 않은 output.md가 그냥 지워지지 않게
                if (state.Run is { } previous && Directory.EnumerateFileSystemEntries(CurrentFolder).Any())
                    CopyTree(CurrentFolder, Path.Combine(ArchiveRoot, previous.Id));
                ClearFolder(CurrentFolder);
                foreach (var (rel, full) in picked)
                {
                    var target = Path.Combine(CurrentFolder, FilesFolder, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(full, target);
                }
                File.WriteAllText(Path.Combine(CurrentFolder, RequestFile), request.TrimEnd() + "\n\n" + Footer(picked.Select(p => p.Rel)), new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ($"중계 폴더에 쓰지 못했습니다: {ex.Message}", null, []);
            }
            if (Update(s => s with { Run = new RunInfo(id, roomId, DateTimeOffset.Now) }) is { } saveError) return (saveError, null, []);
        }
        return (null, id, [.. picked.Select(p => p.Rel)]);
    }

    /// <param name="NotYet">output.md가 아직 없음 (사용자가 agy에서 안 끝냄) — 다른 오류와 가른다.</param>
    public sealed record Collected(string? Error, string? RunId, string? Text, string? FormatProblem, bool NotYet = false);

    /// <summary>
    /// 결과를 받는다: output.md를 읽고 형식을 보고, current를 데이터 폴더 agy-runs/&lt;id&gt;로 옮긴다. output.md가 없으면 옮기지 않는다 (아직 안 끝남).
    /// </summary>
    public Collected Collect(string roomId)
    {
        lock (_gate)
        {
            var state = Read(out var error);
            if (error is not null) return new(error, null, null, null);
            if (state.Run is not { ForRunMode: false } run || run.RoomId != roomId) return new("이 방에서 agy_prepare로 둔 중계가 없습니다.", null, null, null);
            var output = Path.Combine(CurrentFolder, OutputFile);
            string text;
            try
            {
                if (!File.Exists(output)) return new($"{OutputFile}가 아직 없습니다 — 사용자가 agy에서 끝냈는지 확인하세요.", run.Id, null, null, NotYet: true);
                if (new FileInfo(output).Length > MaxOutputBytes) return new($"{OutputFile}가 {MaxOutputBytes / 1024}KB를 넘습니다.", run.Id, null, null);
                text = File.ReadAllText(output);
                var archive = Path.Combine(ArchiveRoot, run.Id);
                CopyTree(CurrentFolder, archive);
                ClearFolder(CurrentFolder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new($"결과를 옮기지 못했습니다: {ex.Message}", run.Id, null, null);
            }
            if (Update(s => s with { Run = null }) is { } saveError) return new(saveError, run.Id, null, null);
            return new(null, run.Id, text, AgyCheck.OutputFormatProblem(text));
        }
    }

    // ---- run_mode 중계 (HB_Orchestration agy-relay 공급자가 이 방 실행 토큰으로 루프백 API를 부른다) ----

    /// <summary>
    /// run_mode의 gemini_agy 단계: 서버가 만든 프롬프트(지시 + 첨부 코드 + input)를 request.md로 둔다. 관문 · 한 자리는 agy_prepare와 같다.
    /// 앱은 agy에 입력하지 않는다 — 사용자가 곁칸에서 입력한다.
    /// </summary>
    /// <param name="wait">공급자의 기다림 상한 (비우면 15분, <see cref="MinRunWait"/> ~ <see cref="MaxRunWait"/>로 자른다).</param>
    public async Task<(string? Error, string? Id, DateTimeOffset Until)> RequestRunAsync(string roomId, string? prompt, TimeSpan? wait = null)
    {
        if (await WhyNotReadyAsync() is { } why) return (why, null, default);
        if (string.IsNullOrWhiteSpace(prompt)) return ("프롬프트가 비었습니다.", null, default);
        if (prompt.Length > MaxRunPromptChars) return ($"프롬프트가 {MaxRunPromptChars:N0}자를 넘습니다 — 첨부를 줄이세요.", null, default);
        var now = DateTimeOffset.Now;
        var id = $"{now:yyyyMMdd-HHmmss}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(2))}";
        var until = now + TimeSpan.FromTicks(Math.Clamp((wait ?? TimeSpan.FromMinutes(15)).Ticks, MinRunWait.Ticks, MaxRunWait.Ticks));
        lock (_gate)
        {
            var state = Read(out var error);
            if (error is not null) return (error, null, default);
            // 제한 시간이 지난 run_mode 중계는 받아 갈 곳이 없다 — 보관하고 덮는다
            if (state.Run is { } other && !other.Stale(now))
                return ($"진행 중인 agy 중계가 있습니다 ({other.Id}) — 끝내거나 설정 화면에서 비운 뒤 하세요.", null, default);
            if (state.Canary is not null) return ("카나리 확인이 진행 중입니다 — 설정 화면에서 판정한 뒤 하세요.", null, default);
            try
            {
                if (state.Run is { } stale) Archive(stale);
                ClearFolder(CurrentFolder);
                File.WriteAllText(Path.Combine(CurrentFolder, RequestFile), prompt.TrimEnd() + "\n\n" + RunFooter, new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ($"중계 폴더에 쓰지 못했습니다: {ex.Message}", null, default); }
            if (Update(s => s with { Run = new RunInfo(id, roomId, now, ForRunMode: true, Until: until) }) is { } saveError) return (saveError, null, default);
            _seenOutput = null;
        }
        return (null, id, until);
    }

    /// <summary>output.md를 마지막으로 본 크기 · 시각 — 두 번 연속 같아야 다 쓴 것으로 본다 (agy가 쓰는 중에 가져가지 않게). _gate 안에서만.</summary>
    private (string Id, long Length, DateTime Write)? _seenOutput;

    private const string RunFooter =
        "---\n\n## 규칙 (HB_Chat이 붙임)\n\n" +
        "- 이 폴더 밖의 파일은 보지 않는다. 파일을 고치지 않는다 — `output.md` 하나만 쓴다.\n" +
        "- 결과는 이 폴더의 `output.md`에, 위 `# 지시`가 정한 출력 형식 그대로만 쓴다 (앞뒤 설명을 붙이지 않는다).\n";

    /// <summary>
    /// run_mode 중계 결과: output.md가 비지 않고 두 번 연속 같은 크기 · 시각이면 읽고 보관 · 비우고 "done", 아직이면(없음 · 쓰는 중 · 잠김) "waiting",
    /// 그 중계가 없거나(취소 · 비우기) 받지 못하면(크기 초과 · 옮기기 실패) "gone" — 받지 못한 때도 자리를 푼다 (받아 갈 곳이 없어 막히지 않게).
    /// </summary>
    public (string Status, string? Text, string? Error) RunResult(string roomId, string? id)
    {
        lock (_gate)
        {
            var state = Read(out var error);
            if (error is not null) return ("gone", null, error);
            if (state.Run is not { ForRunMode: true } run || run.Id != id || run.RoomId != roomId)
                return ("gone", null, "그 agy 중계가 없습니다 (취소됐거나 설정 화면에서 비움).");
            var output = Path.Combine(CurrentFolder, OutputFile);
            string text;
            try
            {
                var info = new FileInfo(output);
                if (!info.Exists || info.Length == 0) return ("waiting", null, null);
                if (info.Length > MaxOutputBytes) return Drop(run, $"{OutputFile}가 {MaxOutputBytes / 1024}KB를 넘습니다.");
                var seen = (run.Id, info.Length, info.LastWriteTimeUtc);
                if (_seenOutput != seen)
                {
                    _seenOutput = seen;
                    return ("waiting", null, null);
                }
                // agy가 아직 열고 쓰는 중이면(공유 위반) 다음 물음에 다시
                using (var stream = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new StreamReader(stream))
                    text = reader.ReadToEnd();
            }
            catch (IOException) { return ("waiting", null, null); }
            catch (UnauthorizedAccessException ex) { return Drop(run, $"{OutputFile}를 읽지 못했습니다: {ex.Message}"); }
            try
            {
                CopyTree(CurrentFolder, Path.Combine(ArchiveRoot, run.Id));
                ClearFolder(CurrentFolder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Drop(run, $"결과를 옮기지 못했습니다: {ex.Message}"); }
            return Update(s => s with { Run = null }) is { } saveError ? ("gone", null, saveError) : ("done", text, null);
        }
    }

    /// <summary>받지 못한 run_mode 중계를 버린다 (_gate 안): 보관 · 비우기는 되는 만큼, 자리는 푼다.</summary>
    private (string Status, string? Text, string? Error) Drop(RunInfo run, string error)
    {
        Release(run);
        return ("gone", null, error);
    }

    /// <summary>run_mode 중계 취소 (제한 시간 · 사용자가 턴을 멈춤): 그 중계면 보관 뒤 비우고 자리를 푼다. 풀었으면 true.</summary>
    public bool CancelRun(string roomId, string? id)
    {
        lock (_gate)
            return Read(out _).Run is { ForRunMode: true } run && run.Id == id && run.RoomId == roomId && Release(run);
    }

    /// <summary>
    /// 그 방의 턴 · 실행이 끝났다 (RoomHub): 남은 run_mode 중계는 받아 갈 곳이 없다 — 턴 중지가 claude 프로세스 트리째 끄면 서버가 cancel을 못 보낸다.
    /// 풀었으면 true.
    /// </summary>
    public bool ReleaseRunFor(string roomId)
    {
        lock (_gate)
            return Read(out _).Run is { ForRunMode: true } run && run.RoomId == roomId && Release(run);
    }

    /// <summary>run_mode 중계 자리를 푼다 (_gate 안) — 보관 · 비우기는 되는 만큼 (남은 파일은 다음 준비가 비운다).</summary>
    private bool Release(RunInfo run)
    {
        try
        {
            Archive(run);
            ClearFolder(CurrentFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _seenOutput = null;
        return Update(s => s with { Run = null }) is null;
    }

    /// <summary>current에 무엇이든 있으면 그 실행 이름으로 보관한다 (_gate 안).</summary>
    private void Archive(RunInfo run)
    {
        if (Directory.Exists(CurrentFolder) && Directory.EnumerateFileSystemEntries(CurrentFolder).Any())
            CopyTree(CurrentFolder, Path.Combine(ArchiveRoot, run.Id));
    }

    /// <summary>진행 중인 중계 · 카나리를 버린다 (설정 화면) — current를 비운다. 중계 폴더 검사를 통과할 때만 (설정이 다른 폴더를 가리키면 지우지 않게).</summary>
    public string? ClearCurrent()
    {
        if (WhyBadRelay(create: false) is { } why) return why;
        lock (_gate)
        {
            try
            {
                if (Directory.Exists(CurrentFolder)) ClearFolder(CurrentFolder);
                if (File.Exists(CanaryFile)) File.Delete(CanaryFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return $"비우지 못했습니다: {ex.Message}"; }
            return Update(s => s with { Run = null, Canary = null });
        }
    }

    public int ArchiveCount => Directory.Exists(ArchiveRoot) ? Directory.GetDirectories(ArchiveRoot).Length : 0;

    /// <summary>보관함 비우기 (사용자 결정 "수동으로 하자").</summary>
    public string? ClearArchive()
    {
        lock (_gate)
        {
            try
            {
                if (Directory.Exists(ArchiveRoot)) ClearFolder(ArchiveRoot);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return $"보관함을 비우지 못했습니다: {ex.Message}"; }
        }
    }

    // ---- 도움 ----

    private static string Footer(IEnumerable<string> files)
    {
        var list = files.ToList();
        return "---\n\n## 규칙 (HB_Chat이 붙임)\n\n" +
               (list.Count > 0 ? "자료는 `files/` 아래에 있다:\n" + string.Concat(list.Select(f => $"- files/{f.Replace('\\', '/')}\n")) + "\n" : "") +
               "- 이 폴더 밖의 파일은 보지 않는다. 파일을 고치지 않는다 — `output.md` 하나만 쓴다.\n" +
               "- 결과는 이 폴더의 `output.md`에 아래 형식으로 쓴다.\n\n" +
               "```\n## 지적\n- [high|medium|low] 파일:줄 — 한 줄 요약\n  근거: ...\n\n## 읽은 파일\n- ...\n```\n";
    }

    private static string Normalize(string rel) => rel.Trim().Replace('/', '\\').TrimStart('\\');

    /// <summary>
    /// 보낼 파일 검사: 작업 폴더 기준 상대 경로 · 안쪽 · 있는 파일 · 링크를 거치지 않음 · 크기 상한 · 저장소 내부(.git) · 비밀처럼 보이는 파일(.env · 키) 아님.
    /// </summary>
    internal static string? WhyBadSource(string workspace, string? rel, out string full, out long size)
    {
        full = "";
        size = 0;
        if (string.IsNullOrWhiteSpace(rel)) return "경로가 비었습니다.";
        var clean = Normalize(rel);
        if (Path.IsPathRooted(rel.Trim()) || clean.Contains(':')) return "작업 폴더 기준 상대 경로만 받습니다.";
        var parts = clean.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p is "." or ".." || p.EndsWith('.') || p.EndsWith(' '))) return "'.' · '..' · 끝 점/공백이 든 경로는 받지 않습니다.";
        if (parts.SkipLast(1).Any(p => SecretFolders.Contains(p, StringComparer.OrdinalIgnoreCase)) || parts[^1].Equals(".git", StringComparison.OrdinalIgnoreCase))
            return "저장소 내부 · 도구 설정 폴더(.git · .claude · .codex · .agents · .ssh)는 보내지 않습니다.";
        if (LooksSecret(parts[^1])) return "비밀이 든 것처럼 보이는 파일(.env · .mcp*.json · *.local.json · 키 · 자격 증명)은 보내지 않습니다.";
        string root;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
            full = Path.GetFullPath(Path.Combine(root, clean));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return "경로 모양이 올바르지 않습니다."; }
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return "작업 폴더 밖입니다.";
        if (!File.Exists(full)) return "없는 파일입니다.";
        try
        {
            for (var at = full; at.Length > root.Length; at = Path.GetDirectoryName(at)!)
                if (File.GetAttributes(at).HasFlag(FileAttributes.ReparsePoint)) return "링크(심볼릭 링크 · 정션)를 거치는 파일은 보내지 않습니다.";
            size = new FileInfo(full).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return $"파일을 보지 못했습니다: {ex.Message}"; }
        return size > MaxFileBytes ? $"파일 하나는 {MaxFileBytes / 1024}KB까지입니다." : null;
    }

    private static readonly string[] SecretFolders = [".git", ".claude", ".codex", ".agents", ".ssh"];

    /// <summary>
    /// 비밀이 든 것처럼 보이는 파일 이름: .env* · .mcp*.json(서버 env에 키) · *.local.json · appsettings.*.json · 키 · 인증서 ·
    /// 확장자 없는 SSH 키 · 패키지 관리자 자격 증명 · credentials* · secrets.json. 지휘자가 고르는 경계라 이름만 본다.
    /// </summary>
    internal static bool LooksSecret(string name) =>
        name.StartsWith(".env", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(".mcp", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".local.json", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("appsettings.", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
           && !name.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase)
        || new[] { ".pem", ".key", ".pfx", ".p12" }.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase)
        || new[] { "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519", ".npmrc", ".netrc", ".pypirc", "secrets.json" }.Contains(name, StringComparer.OrdinalIgnoreCase)
        || name.StartsWith("credentials", StringComparison.OrdinalIgnoreCase);

    /// <summary>폴더 안을 비운다 (폴더는 남김). 링크는 따라가지 않고 링크만 지운다.</summary>
    internal static void ClearFolder(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReadOnly)) entry.Attributes &= ~FileAttributes.ReadOnly;
            if (entry is DirectoryInfo sub)
            {
                if (!sub.Attributes.HasFlag(FileAttributes.ReparsePoint)) ClearFolder(sub.FullName);
                sub.Delete(); // 링크면 링크만
            }
            else entry.Delete();
        }
    }

    /// <summary>폴더를 통째로 복사한다 (링크는 건너뜀).</summary>
    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var entry in new DirectoryInfo(from).EnumerateFileSystemInfos())
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            var target = Path.Combine(to, entry.Name);
            if (entry is DirectoryInfo sub) CopyTree(sub.FullName, target);
            else File.Copy(entry.FullName, target, overwrite: true);
        }
    }
}
