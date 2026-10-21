using System.Text.Json;
using System.Text.Json.Serialization;

namespace HB.Orchestration.Agents;

/// <summary>modes.json을 호출할 때마다 읽는다. 파일만 고치면 재시작 없이 반영된다.</summary>
public sealed class ModeStore(AgentCatalog catalog)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <param name="Bytes">읽은 modes.json 원본 바이트 (호출 기록의 해시·원문 저장용). 역직렬화도 이 바이트로 한다.</param>
    public sealed record Loaded(ModesFile File, string Path, byte[] Bytes)
    {
        public string Sha256 { get; } = HB.Orchestration.Shared.BlobStore.Sha256Hex(Bytes);
    }

    /// <summary>찾는 순서는 <see cref="ModesPathResolver"/> (대시보드와 공용). 지정 경로에 파일이 없으면 Error.</summary>
    public ModesPathResolver.Resolution Resolve() =>
        ModesPathResolver.Resolve(catalog.Options.ModesPath, Environment.GetEnvironmentVariable(ModesPathResolver.EnvVar),
            Environment.CurrentDirectory, AppContext.BaseDirectory);

    /// <exception cref="ModeConfigException">파일이 없거나 JSON이 깨졌을 때.</exception>
    public Loaded Load()
    {
        var resolved = Resolve();
        var path = resolved.Path ?? throw new ModeConfigException(resolved.Error!);
        try
        {
            // 해시한 바이트와 해석한 설정이 같은 시점의 파일이 되도록 한 번만 읽는다.
            var bytes = File.ReadAllBytes(path);
            var file = JsonSerializer.Deserialize<ModesFile>(GuidelinesLoader.DecodeLikeReadAllText(bytes), Json) ?? new ModesFile();
            // 역직렬화로 만들어진 딕셔너리는 대소문자 구분이라 다시 감싼다.
            file.Roles = new(file.Roles, StringComparer.OrdinalIgnoreCase);
            file.Modes = new(file.Modes, StringComparer.OrdinalIgnoreCase);
            return new Loaded(file, path, bytes);
        }
        catch (JsonException ex)
        {
            throw new ModeConfigException($"{path} 파싱 실패 (줄 {ex.LineNumber + 1}): {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 찾은 뒤 지워졌거나 다른 프로그램이 잠갔을 때. run_mode 밖으로 예외를 내보내지 않는다.
            throw new ModeConfigException($"{path} 읽기 실패: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>역할 이름 → (시스템 프롬프트, 지침 사용 여부). modes.json이 내장 역할보다 우선.</summary>
    /// <summary>역할의 프롬프트 · 지침 사용 · 구조화 출력 형식. Output이 알 수 없는 이름이면 설정 오류.</summary>
    public static (string Prompt, bool UseGuidelines, FindingFormat? Output) ResolveRole(Loaded loaded, string role)
    {
        var (prompt, useGuidelines, output) = ResolveRaw(loaded, role);
        if (string.IsNullOrWhiteSpace(output)) return (prompt, useGuidelines, null);
        return FindingFormat.Find(output) is { } format
            ? (prompt, useGuidelines, format)
            : throw new ModeConfigException($"역할 '{role}'의 Output '{output}'을(를) 모릅니다. 사용 가능: {FindingFormat.KnownNames}");
    }

    private static (string Prompt, bool UseGuidelines, string? Output) ResolveRaw(Loaded loaded, string role)
    {
        if (loaded.File.Roles.TryGetValue(role, out var def))
        {
            if (!string.IsNullOrWhiteSpace(def.Prompt)) return (def.Prompt, def.UseGuidelines, def.Output);
            if (!string.IsNullOrWhiteSpace(def.PromptFile))
            {
                var p = System.IO.Path.IsPathRooted(def.PromptFile)
                    ? def.PromptFile
                    : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(loaded.Path)!, def.PromptFile);
                if (!File.Exists(p)) throw new ModeConfigException($"역할 '{role}'의 PromptFile을 찾을 수 없습니다: {p}");
                return (File.ReadAllText(p), def.UseGuidelines, def.Output);
            }
        }

        // 내장 역할: modes.json에 프롬프트 없이 같은 이름 역할을 두면 Output·UseGuidelines만 바꾼다
        // (예: "Reviewer": { "Output": "findings@1" } — 골든 세트로 출력 형식 판끼리 비교할 때).
        if (Prompts.BuiltInRoles.TryGetValue(role, out var builtIn))
            return loaded.File.Roles.TryGetValue(role, out var overlay)
                ? (builtIn, overlay.UseGuidelines, string.IsNullOrWhiteSpace(overlay.Output) ? Prompts.BuiltInOutputs.GetValueOrDefault(role) : overlay.Output)
                : (builtIn, true, Prompts.BuiltInOutputs.GetValueOrDefault(role));
        throw new ModeConfigException($"역할 '{role}'이(가) 정의되지 않았습니다.");
    }
}

public sealed class ModeConfigException(string message) : Exception(message);
