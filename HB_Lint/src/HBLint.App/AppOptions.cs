using System.Security.Cryptography;

namespace HBLint.App;

/// <summary>실행 인자. 경로는 UI에 미리 채우는 값이고 UI에서 바꿀 수 있다.</summary>
internal sealed class AppOptions
{
    public const string Usage = """
        사용법:
          hblint-app [--rules <규칙 폴더>] [--out <LintRules.g.cs>] [--project <csproj 또는 Unity 프로젝트 폴더>]
                     [--port <n>] [--no-browser] [--dev-origin <http://localhost:5173>]

        --port        기본 0 (빈 포트를 고름)
        --dev-origin  웹 UI 개발 서버(Vite) 출처 — 그 출처에서 오는 API 요청도 받는다
        """;

    public string? Rules { get; init; }

    public string? Out { get; init; }

    public string? Project { get; init; }

    public int Port { get; init; }

    public bool OpenBrowser { get; init; } = true;

    public string? DevOrigin { get; init; }

    /// <summary>실행마다 새로 — API는 X-HBLint-Token 헤더로 받는다.</summary>
    public string Token { get; init; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// <summary>최근 연 경로 (app.json) 자리. 시험이 바꾼다.</summary>
    public string DataFolder { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HBLint");

    /// <summary>모르는 인자면 null.</summary>
    public static AppOptions? Parse(string[] args)
    {
        string? rules = null, output = null, project = null, devOrigin = null;
        var port = 0;
        var browser = true;
        for (var i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--rules": rules = Next(); if (rules == null) return null; break;
                case "--out": output = Next(); if (output == null) return null; break;
                case "--project": project = Next(); if (project == null) return null; break;
                case "--dev-origin": devOrigin = Next(); if (devOrigin == null) return null; break;
                case "--port":
                    if (!int.TryParse(Next(), out port) || port < 0 || port > 65535)
                        return null;
                    break;
                case "--no-browser": browser = false; break;
                default: return null;
            }
        }
        return new AppOptions { Rules = rules, Out = output, Project = project, Port = port, OpenBrowser = browser, DevOrigin = devOrigin?.TrimEnd('/') };
    }
}
