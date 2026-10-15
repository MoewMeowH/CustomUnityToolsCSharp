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

    public sealed record Loaded(ModesFile File, string Path);

    public string? ResolvePath()
    {
        var candidates = new[]
        {
            catalog.Options.ModesPath,
            Environment.GetEnvironmentVariable("HB_MODES_PATH"),
            System.IO.Path.Combine(Environment.CurrentDirectory, "hb-modes.json"),   // 프로젝트별 모드
            System.IO.Path.Combine(AppContext.BaseDirectory, "modes.json"),         // 기본 모드
        };
        return candidates.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));
    }

    /// <exception cref="ModeConfigException">파일이 없거나 JSON이 깨졌을 때.</exception>
    public Loaded Load()
    {
        var path = ResolvePath() ?? throw new ModeConfigException("modes.json을 찾을 수 없습니다.");
        try
        {
            var file = JsonSerializer.Deserialize<ModesFile>(File.ReadAllText(path), Json) ?? new ModesFile();
            // 역직렬화로 만들어진 딕셔너리는 대소문자 구분이라 다시 감싼다.
            file.Roles = new(file.Roles, StringComparer.OrdinalIgnoreCase);
            file.Modes = new(file.Modes, StringComparer.OrdinalIgnoreCase);
            return new Loaded(file, path);
        }
        catch (JsonException ex)
        {
            throw new ModeConfigException($"{path} 파싱 실패 (줄 {ex.LineNumber + 1}): {ex.Message}");
        }
    }

    /// <summary>역할 이름 → (시스템 프롬프트, 지침 사용 여부). modes.json이 내장 역할보다 우선.</summary>
    public static (string Prompt, bool UseGuidelines) ResolveRole(Loaded loaded, string role)
    {
        if (loaded.File.Roles.TryGetValue(role, out var def))
        {
            if (!string.IsNullOrWhiteSpace(def.Prompt)) return (def.Prompt, def.UseGuidelines);
            if (!string.IsNullOrWhiteSpace(def.PromptFile))
            {
                var p = System.IO.Path.IsPathRooted(def.PromptFile)
                    ? def.PromptFile
                    : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(loaded.Path)!, def.PromptFile);
                if (!File.Exists(p)) throw new ModeConfigException($"역할 '{role}'의 PromptFile을 찾을 수 없습니다: {p}");
                return (File.ReadAllText(p), def.UseGuidelines);
            }
        }

        if (Prompts.BuiltInRoles.TryGetValue(role, out var builtIn)) return (builtIn, true);
        throw new ModeConfigException($"역할 '{role}'이(가) 정의되지 않았습니다.");
    }
}

public sealed class ModeConfigException(string message) : Exception(message);
