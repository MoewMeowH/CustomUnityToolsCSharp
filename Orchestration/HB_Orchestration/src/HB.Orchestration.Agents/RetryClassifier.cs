using System.ClientModel;
using System.Globalization;
using System.Net.Http;

namespace HB.Orchestration.Agents;

/// <summary>호출 실패의 종류. 호출 기록(CallRecord.ErrorKind)에는 이름 문자열로 남는다.</summary>
public enum ErrorKind
{
    /// <summary>네트워크 타임아웃 (사용자 취소가 아닌 취소, 408).</summary>
    Timeout,
    /// <summary>429 요청 과다.</summary>
    RateLimit,
    /// <summary>5xx.</summary>
    Server,
    /// <summary>그 외 4xx (400, 401, 402 결제 필요, 404 …). 다시 보내도 결과가 같다.</summary>
    Client,
    /// <summary>응답을 받기 전 연결 실패 (DNS, 연결 끊김 등).</summary>
    Network,
    /// <summary>429 중 크레딧·한도 소진 (insufficient_quota · credit_balance_exhausted). 충전 전까지 다시 보내도 같으므로 재시도하지 않는다.</summary>
    Quota,
}

/// <param name="Kind">분류할 수 없는 예외면 null (재시도 안 함).</param>
/// <param name="Status">HTTP 상태 코드. 응답이 없었으면 null.</param>
/// <param name="RetryAfter">서버가 알려준 대기 시간 (Retry-After / retry-after-ms).</param>
public sealed record FailureInfo(ErrorKind? Kind, bool Retryable, int? Status, TimeSpan? RetryAfter);

/// <summary>
/// SDK 자동 재시도를 끄고 서버가 직접 재시도하므로, 어떤 실패를 다시 보낼지 여기서 정한다.
/// 사용자 취소는 실패가 아니므로 분류하지 않는다 (<see cref="IsUserCancellation"/>로 먼저 걸러 그대로 던진다).
/// </summary>
public static class RetryClassifier
{
    /// <summary>Retry-After가 이보다 길면 기다리지 않고 실패로 돌려준다 (run_mode가 몇 분씩 멈추지 않도록).</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    /// <summary>예외가 사용자 취소(run_mode 호출 취소)인지. 이때는 재시도·기록 없이 그대로 던진다.</summary>
    public static bool IsUserCancellation(Exception ex, CancellationToken ct) =>
        ct.IsCancellationRequested && Unwrap(ex) is OperationCanceledException;

    public static FailureInfo Classify(Exception ex, CancellationToken ct)
    {
        var e = Unwrap(ex);

        switch (e)
        {
            // SDK의 NetworkTimeout은 TaskCanceledException으로 끝난다. 호출자 토큰이 취소되지 않았으면 타임아웃이다.
            case OperationCanceledException when !ct.IsCancellationRequested:
            case TimeoutException:
                return new FailureInfo(ErrorKind.Timeout, true, null, null);

            // 응답 없이 실패하면 SDK가 Status 0으로 감싼다 (전송 계층 오류)
            case ClientResultException { Status: 0 }:
                return new FailureInfo(ErrorKind.Network, true, null, null);

            case ClientResultException { Status: 429 } cre when IsQuota(cre):
                return new FailureInfo(ErrorKind.Quota, false, 429, null);

            case ClientResultException cre:
                return ClassifyStatus(cre.Status, RetryAfterOf(cre));

            case HttpRequestException hre:
                return hre.StatusCode is { } code
                    ? ClassifyStatus((int)code, null)
                    : new FailureInfo(ErrorKind.Network, true, null, null);

            case IOException:
                return new FailureInfo(ErrorKind.Network, true, null, null);

            // claude-cli 제공사: 결과 JSON에서 이미 종류를 정했다.
            case Providers.ClaudeCliException cli:
                return new FailureInfo(cli.Kind, cli.Retryable, cli.Status, null);

            default:
                return new FailureInfo(null, false, null, null);
        }
    }

    /// <summary>HTTP 상태 코드 규칙: 408·429·5xx는 재시도, 그 외 4xx는 다시 보내도 같으므로 재시도하지 않는다.</summary>
    public static FailureInfo ClassifyStatus(int status, TimeSpan? retryAfter) => status switch
    {
        408 => new FailureInfo(ErrorKind.Timeout, true, status, retryAfter),
        429 => new FailureInfo(ErrorKind.RateLimit, true, status, retryAfter),
        >= 500 => new FailureInfo(ErrorKind.Server, true, status, retryAfter),
        >= 400 => new FailureInfo(ErrorKind.Client, false, status, null),
        _ => new FailureInfo(null, false, status, null),
    };

    /// <summary>
    /// 다음 시도 전 대기 시간. 서버가 알려준 값이 있으면 그것, 없으면 2초 → 5초.
    /// 서버 값이 상한을 넘으면 null (기다리지 않고 실패 처리).
    /// </summary>
    public static TimeSpan? DelayBeforeNext(FailureInfo failure, int failedAttempt)
    {
        if (failure.RetryAfter is { } requested)
            return requested <= MaxRetryAfter ? (requested < TimeSpan.Zero ? TimeSpan.Zero : requested) : null;
        return failedAttempt <= 1 ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(5);
    }

    /// <summary>AggregateException(재시도 정책·Task가 감싼 것)과 TargetInvocation 등 한 겹 래퍼를 벗긴다.</summary>
    private static Exception Unwrap(Exception ex)
    {
        var e = ex;
        for (var depth = 0; depth < 5; depth++)
        {
            if (e is AggregateException { InnerExceptions.Count: > 0 } agg) e = agg.InnerExceptions[^1];
            else break;
        }
        return e;
    }

    /// <summary>OpenAI가 크레딧·한도 소진에 쓰는 오류 코드. 같은 429라도 일시 제한(rate_limit_exceeded)과 달리 기다려도 풀리지 않는다.</summary>
    private static readonly string[] QuotaCodes = ["insufficient_quota", "credit_balance_exhausted"];

    /// <summary>429의 메시지(SDK가 "HTTP 429 (code: …)"로 붙임)나 응답 본문에 소진 코드가 있는지. 본문을 못 읽으면 메시지로만 본다.</summary>
    private static bool IsQuota(ClientResultException ex)
    {
        if (QuotaCodes.Any(c => ex.Message.Contains(c, StringComparison.OrdinalIgnoreCase))) return true;
        try
        {
            var body = ex.GetRawResponse()?.Content?.ToString();
            return body is not null && QuotaCodes.Any(c => body.Contains(c, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return false; // 본문은 부가 정보 — 못 읽으면 일반 429(재시도)로 둔다
        }
    }

    private static TimeSpan? RetryAfterOf(ClientResultException ex)
    {
        try
        {
            var headers = ex.GetRawResponse()?.Headers;
            if (headers is null) return null;

            // OpenAI 호환 API는 밀리초 단위 헤더를 함께 보내기도 한다. 더 정밀한 쪽을 먼저 본다.
            if (headers.TryGetValue("retry-after-ms", out var ms) &&
                double.TryParse(ms, NumberStyles.Float, CultureInfo.InvariantCulture, out var millis))
                return SafeSeconds(millis / 1000);

            if (headers.TryGetValue("Retry-After", out var value) && value is { Length: > 0 })
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                    return SafeSeconds(seconds);
                if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
                    return at - DateTimeOffset.UtcNow;
            }
        }
        catch (Exception)
        {
            // 헤더를 못 읽으면 기본 대기로 간다 (부가 정보 실패가 재시도를 막지 않게).
        }
        return null;
    }

    /// <summary>
    /// TimeSpan 범위를 넘는 값(1e15, Infinity)은 변환 중 예외가 나서 "헤더 없음"으로 처리될 수 있다.
    /// 하루를 넘으면 하루로 잘라 상한 초과(= 기다리지 않고 실패)로 확실히 가게 한다. NaN은 헤더 없음으로 본다.
    /// </summary>
    private static TimeSpan? SafeSeconds(double seconds)
    {
        const double oneDay = 86_400;
        if (double.IsNaN(seconds)) return null;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 0, oneDay));
    }
}
