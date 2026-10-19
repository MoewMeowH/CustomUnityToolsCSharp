using System.Text;
using System.Text.RegularExpressions;
using HB.Orchestration.Shared;

namespace HB.Orchestration.Agents;

/// <summary>
/// 호출 기록의 Provenance(지침·설정 해시)를 만들고 원문을 저장한다.
/// 부가 기능이라 어떤 실패도 예외로 내보내지 않고 StoreErrors에 한 줄씩 남긴다.
/// </summary>
public static partial class ProvenanceBuilder
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>실행 단위 값: modes.json · 서버 설정 · 지침 원문 저장. 단계별 Guidelines·Instructions는 비어 있다.</summary>
    /// <param name="settingsBytes">적용 중인 설정을 읽을 때 함께 읽은 바이트 (<see cref="AgentCatalog.SettingsBytes"/>). 여기서 파일을 다시 읽지 않는다.</param>
    public static CallProvenance ForRun(
        ModeStore.Loaded modes, GuidelinesLoader.Result guidelines, bool guidelinesUsed,
        string? settingsPath, byte[]? settingsBytes, string? settingsReloadError, RecordsOptions records, string callLogPath)
    {
        var errors = new List<string>();

        string? folder = null;
        try { folder = BlobStore.ResolveFolder(records.BlobFolder, callLogPath); }
        catch (Exception ex) { errors.Add($"blobs 폴더: {ex.GetType().Name}: {ex.Message}"); }

        // 저장했거나 이미 있으면 true. 폴더 오류는 위에서 한 번만 남긴다.
        bool Store(string what, string sha, byte[] bytes)
        {
            if (folder is null) return false;
            if (BlobStore.TryStore(folder, sha, bytes) is not { } error) return true;
            errors.Add($"{what}: {error}");
            return false;
        }

        Store("modes", modes.Sha256, modes.Bytes);
        if (guidelinesUsed && guidelines.Bytes is { } g && guidelines.Sha256 is { } gSha) Store("guidelines", gSha, g);

        var settings = Settings(settingsPath, settingsBytes, settingsReloadError, records, errors, Store);

        return new CallProvenance
        {
            Modes = new FileProvenance { Path = modes.Path, Sha256 = modes.Sha256, Bytes = modes.Bytes.Length },
            Settings = settings,
            BlobFolder = folder,
            StoreErrors = errors.Count > 0 ? errors : null,
        };
    }

    /// <summary>단계별 지침 표시. 역할이 끈 경우가 파일 없음보다 먼저다.</summary>
    /// <param name="skipped">골든 --no-guidelines: 역할은 지침을 쓰지만 일부러 뺐다.</param>
    public static GuidelinesProvenance ForStep(GuidelinesLoader.Result guidelines, bool useGuidelines, bool skipped = false) =>
        !useGuidelines ? new GuidelinesProvenance { Attached = false, Reason = GuidelinesProvenance.ReasonRoleDisabled }
        : skipped ? new GuidelinesProvenance { Attached = false, Reason = GuidelinesProvenance.ReasonGoldenSkipped }
        : guidelines.Path is null ? new GuidelinesProvenance { Attached = false, Reason = GuidelinesProvenance.ReasonNoFile }
        : new GuidelinesProvenance { Attached = true, Path = guidelines.Path, Sha256 = guidelines.Sha256, Bytes = guidelines.Bytes?.Length };

    /// <summary>
    /// 에이전트에 넘기는 Instructions 문자열 그대로의 해시와 원문 저장. 실패는 StoreErrors에 덧붙인다.
    /// </summary>
    public static CallProvenance WithInstructions(CallProvenance? baseline, string instructions, string callLogPath)
    {
        var p = baseline ?? new CallProvenance();
        var errors = new List<string>(p.StoreErrors ?? []);
        string? sha = null;
        long? length = null;
        var folder = p.BlobFolder;
        try
        {
            var bytes = Utf8NoBom.GetBytes(instructions);
            sha = BlobStore.Sha256Hex(bytes);
            length = bytes.Length;
            folder ??= BlobStore.DefaultFolderFor(callLogPath);
            if (BlobStore.TryStore(folder, sha, bytes) is { } error) errors.Add($"instructions: {error}");
        }
        catch (Exception ex)
        {
            errors.Add($"instructions: {ex.GetType().Name}: {ex.Message}");
        }

        return p with
        {
            InstructionsSha256 = sha,
            InstructionsBytes = length,
            BlobFolder = folder,
            StoreErrors = errors.Count > 0 ? errors : null,
        };
    }

    private static SettingsProvenance? Settings(
        string? path, byte[]? bytes, string? reloadError, RecordsOptions records, List<string> errors, Func<string, string, byte[], bool> store)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (bytes is null)
        {
            errors.Add("settings: 적용 중인 설정의 파일 바이트를 읽지 못함 (해시 없음)");
            return new SettingsProvenance { Path = path, ReloadError = reloadError };
        }

        try
        {
            var sha = BlobStore.Sha256Hex(bytes);
            var hits = CountSecretPatterns(GuidelinesLoader.DecodeLikeReadAllText(bytes));

            var stored = false;
            if (records.StoreSettingsText)
            {
                if (hits > 0) errors.Add($"settings: 키 패턴 의심 {hits}건이라 원문을 저장하지 않음");
                else stored = store("settings", sha, bytes);
            }

            return new SettingsProvenance
            {
                Path = Path.GetFullPath(path),
                Sha256 = sha,
                Bytes = bytes.Length,
                Stored = stored,
                SecretScan = hits == 0 ? "통과" : $"의심 {hits}건",
                ReloadError = reloadError,
            };
        }
        catch (Exception ex)
        {
            errors.Add($"settings: {ex.GetType().Name}: {ex.Message}");
            return new SettingsProvenance { Path = path, ReloadError = reloadError };
        }
    }

    /// <summary>
    /// 설정 원문에 키 값이 들어 있는지 대략 검사한다 (4장 검사와 같은 패턴). 걸리면 원문을 저장하지 않는다.
    /// "ApiKeyEnv"처럼 환경 변수 이름을 담는 키는 걸리지 않는다.
    /// </summary>
    public static int CountSecretPatterns(string text) => SecretRegex().Count(text);

    [GeneratedRegex(@"sk-[A-Za-z0-9_-]{16,}|AIza[0-9A-Za-z_-]{30,}|Bearer\s+[A-Za-z0-9._~+/=-]{16,}|""(?:api[_-]?key|secret|token|password|access[_-]?key)""\s*:\s*""[^""]+""", RegexOptions.IgnoreCase)]
    private static partial Regex SecretRegex();
}
