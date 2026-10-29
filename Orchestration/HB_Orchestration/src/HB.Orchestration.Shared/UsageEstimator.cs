using System.Text;
using System.Text.RegularExpressions;

namespace HB.Orchestration.Shared;

/// <summary>끊긴 호출의 추정 사용량 근거. Sibling = 같은 단계 성공 시도의 실측, Bytes = 보낸 바이트 수 어림.</summary>
public enum EstimateBasis { Sibling, Bytes }

/// <summary>읽은 기록에 붙는 추정 표시. OutputUnknown이면 출력은 0으로 두었다 (실제 청구는 더 클 수 있음).</summary>
public sealed record UsageEstimate(EstimateBasis Basis, bool OutputUnknown);

/// <summary>
/// 끊긴 호출(Timeout · Network · Canceled · Lost)은 청구되지만 usage를 받지 못해 토큰이 비어 있다.
/// 대시보드가 읽은 기록에 추정 토큰을 채운다. 기록 파일은 바꾸지 않는다 — 지난 기록에도 적용되고, 규칙을 바꾸면 바로 다시 계산된다.
/// </summary>
public static class UsageEstimator
{
    /// <summary>
    /// 입력 토큰 / (프롬프트 + 지침 UTF-8 바이트). 2026-10-24 성공 호출 실측 중앙값 openai 0.27 · gemini 0.30 · fireworks 0.31 (p90 0.33).
    /// 웹검색 호출은 검색 결과가 입력에 붙어 이보다 훨씬 크다 (하한이 된다).
    /// </summary>
    public const double TokensPerByte = 0.3;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// 요청이 나간 뒤 끊긴 실패. HTTP 상태로 거절된 실패(4xx · 5xx · Quota · RateLimit)는 청구되지 않는 것으로 보고 넣지 않는다.
    /// 408은 Timeout으로 분류되지만 서버가 보낸 응답이라 뺀다 — 기록 Error 앞머리 "[실패] Timeout 408 · "로 구분한다 (AgentRunner.Describe).
    /// </summary>
    public static bool IsCut(CallRecord r) =>
        !r.Success && r.InputTokens is null && r.ErrorKind is "Timeout" or "Network" or "Canceled" or InflightLog.LostKind
        && !HttpStatusPrefix.IsMatch(r.Error ?? "");

    private static readonly Regex HttpStatusPrefix = new(@"^\[실패\] \w+ \d{3} · ", RegexOptions.CultureInvariant);

    /// <summary>
    /// 끊긴 기록에 추정 토큰과 <see cref="CallRecord.Estimate"/>를 채운 목록 (순서 유지). 나머지 기록은 그대로다.
    /// 같은 Id의 다른 기록이 있는 Lost 기록(기록을 남긴 직후 프로세스가 죽어 표시가 남은 경우)은 뺀다.
    /// </summary>
    public static List<CallRecord> Apply(IReadOnlyList<CallRecord> records)
    {
        var result = new List<CallRecord>(records.Count);
        if (!records.Any(IsCut))
        {
            result.AddRange(records);
            return result;
        }

        // 같은 Id: 기록을 남긴 뒤 표시가 남은 경우(다른 기록이 있음), 정리하다 죽어 두 번 옮겨진 경우(Lost 둘) → 하나만 둔다.
        var written = records.Where(r => r.ErrorKind != InflightLog.LostKind).Select(r => r.Id).ToHashSet();
        var lostSeen = new HashSet<string>();
        var succeeded = new Dictionary<(string, int, string?), CallRecord>();
        foreach (var r in records)
            if (r is { Success: true, InputTokens: not null, RunId: { } run, Step: { } step })
                succeeded.TryAdd((run, step, r.Agent), r);

        foreach (var r in records)
        {
            if (!IsCut(r)) { result.Add(r); continue; }
            if (r.ErrorKind == InflightLog.LostKind && (written.Contains(r.Id) || !lostSeen.Add(r.Id))) continue;
            result.Add(Estimate(r, r.RunId is { } run && r.Step is { } step ? succeeded.GetValueOrDefault((run, step, r.Agent)) : null));
        }
        return result;
    }

    /// <summary>
    /// 같은 단계 성공 시도가 있으면 그 입력(같은 요청을 다시 보낸 것). Timeout · Network은 서버가 끝까지 생성해 청구한 것으로 보고 출력도 쓴다 (GLM 2026-10-20).
    /// Canceled · Lost는 연결을 닫아 생성이 멈춘 것으로 보고 입력만 (Gemini 2026-10-21). 캐시 적중은 0으로 둔다.
    /// </summary>
    internal static CallRecord Estimate(CallRecord r, CallRecord? sibling)
    {
        var withOutput = r.ErrorKind is "Timeout" or "Network";
        if (sibling is not null)
            return r with
            {
                InputTokens = sibling.InputTokens,
                OutputTokens = withOutput ? sibling.OutputTokens : null,
                ReasoningTokens = withOutput ? sibling.ReasoningTokens : null,
                Estimate = new UsageEstimate(EstimateBasis.Sibling, OutputUnknown: false),
            };

        var bytes = (r.PromptBytes ?? Utf8.GetByteCount(r.Prompt)) + (r.Provenance?.InstructionsBytes ?? 0);
        return r with
        {
            InputTokens = (long)Math.Round(bytes * TokensPerByte),
            Estimate = new UsageEstimate(EstimateBasis.Bytes, OutputUnknown: withOutput),
        };
    }
}
