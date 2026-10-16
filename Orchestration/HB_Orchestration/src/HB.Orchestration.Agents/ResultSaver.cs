using System.Security.Cryptography;
using System.Text;
using HB.Orchestration.Shared;

namespace HB.Orchestration.Agents;

/// <summary>
/// save_result: calls.jsonl의 응답 원문을 파일로 저장한다.
/// 지휘자(Claude)가 응답을 다시 출력해 옮겨 적지 않게 한다 → 재출력 토큰 절약, 옮겨 적기 오류 제거, 저장 위치 고정(기록의 ProjectDir 기준).
/// 실패는 예외가 아니라 "[저장 불가] …" 문자열로 돌려준다.
/// </summary>
public static class ResultSaver
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <param name="codeBlock">응답 안 N번째 코드 블록만 저장 (1부터). null이면 응답 전체.</param>
    /// <param name="denyPatterns">쓰기 금지 패턴 (context 전송 금지 목록과 같다).</param>
    /// <param name="logPath">호출 기록 파일. null이면 기본 경로.</param>
    public static string Save(
        string? runId, int step, string? path, int? codeBlock, bool overwrite,
        IEnumerable<string> denyPatterns, string? logPath = null)
    {
        if (string.IsNullOrWhiteSpace(runId)) return "[저장 불가] runId가 비어 있습니다. run_mode 결과 첫 줄의 runId를 넘기세요.";
        if (step < 1) return "[저장 불가] step은 1부터입니다.";
        if (codeBlock is < 1) return "[저장 불가] codeBlock은 1부터입니다.";

        // ── 기록 찾기: 같은 단계의 시도가 여럿이면 성공한 시도 ─────────────────
        List<CallRecord> attempts;
        try
        {
            attempts = CallLog.ReadAll(logPath).Where(r => r.RunId == runId.Trim() && r.Step == step).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"[저장 불가] 호출 기록을 읽지 못했습니다 ({ex.GetType().Name}: {ex.Message}).";
        }

        if (attempts.Count == 0)
            return $"[저장 불가] runId '{runId}' 단계 {step}의 기록이 없습니다. run_mode 결과의 runId와 단계 번호를 확인하세요.";

        var record = attempts.Where(r => r.Success && r.Response is not null).MaxBy(r => r.Attempt ?? 1);
        if (record is null)
            return $"[저장 불가] runId '{runId}' 단계 {step}에 성공한 시도가 없습니다 (시도 {attempts.Count}회 모두 실패).";

        if (string.IsNullOrEmpty(record.ProjectDir))
            return "[저장 불가] 이 기록에는 프로젝트 폴더(ProjectDir)가 없어 저장 위치를 정할 수 없습니다.";

        // ── 저장할 내용 ─────────────────────────────────────────────────────
        var content = record.Response!;
        var blockNote = "";
        if (codeBlock is { } n)
        {
            var count = CodeFences.Find(content).Count;
            if (CodeFences.Content(content, n) is not { } block)
                return $"[저장 불가] 응답에 코드 블록이 {count}개라 {n}번째가 없습니다.";
            content = block;
            blockNote = $", 코드 블록 {n}/{count}";
        }

        // ── 경로 검사 (context와 같은 규칙, 루트는 기록의 ProjectDir) ───────────
        var (full, error) = SafePath.Resolve(path, record.ProjectDir, denyPatterns, SafePath.Access.Write);
        if (error is not null) return $"[저장 불가] '{path}': {error}";

        var bytes = Utf8NoBom.GetBytes(content);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full!)!);

            // overwrite=false면 CreateNew: 존재 확인과 생성이 한 번에 일어나 그 사이에 생긴 파일도 덮지 않는다.
            using var fs = new FileStream(full!, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None);
            fs.Write(bytes);
        }
        catch (IOException) when (!overwrite && File.Exists(full))
        {
            return $"[저장 불가] '{path}'이(가) 이미 있습니다. 덮어쓰려면 overwrite=true로 다시 호출하세요.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"[저장 불가] '{path}': 쓰기 실패 ({ex.GetType().Name}: {ex.Message}).";
        }

        var relative = Path.GetRelativePath(record.ProjectDir, full!).Replace('\\', '/');
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return $"저장: {relative} ({bytes.Length:N0}바이트, SHA-256 {hash}{blockNote}) — runId {record.RunId} 단계 {step} · {record.Agent}";
    }
}
