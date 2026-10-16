using HB.Orchestration.Agents;
using HB.Orchestration.Tests.Fakes;

namespace HB.Orchestration.Tests;

/// <summary>지시서 통과 기준: 타임아웃·429·500만 재시도, 402·401·사용자 취소는 재시도하지 않는다.</summary>
public class RetryClassifierTests
{
    [Fact]
    public void Timeout_IsRetried()
    {
        // SDK NetworkTimeout은 호출자 토큰이 살아 있는 상태의 TaskCanceledException으로 끝난다.
        var f = RetryClassifier.Classify(new TaskCanceledException("timeout"), CancellationToken.None);
        Assert.Equal(ErrorKind.Timeout, f.Kind);
        Assert.True(f.Retryable);
    }

    [Fact]
    public void Timeout_WrappedInAggregate_IsRetried()
    {
        var f = RetryClassifier.Classify(new AggregateException(new TaskCanceledException()), CancellationToken.None);
        Assert.Equal(ErrorKind.Timeout, f.Kind);
        Assert.True(f.Retryable);
    }

    [Theory]
    [InlineData(429, ErrorKind.RateLimit)]
    [InlineData(500, ErrorKind.Server)]
    [InlineData(503, ErrorKind.Server)]
    public void RetryableStatus(int status, ErrorKind kind)
    {
        var f = RetryClassifier.Classify(FakeResponse.Error(status), CancellationToken.None);
        Assert.Equal(kind, f.Kind);
        Assert.True(f.Retryable);
        Assert.Equal(status, f.Status);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(402)]
    [InlineData(404)]
    public void ClientStatus_IsNotRetried(int status)
    {
        var f = RetryClassifier.Classify(FakeResponse.Error(status), CancellationToken.None);
        Assert.Equal(ErrorKind.Client, f.Kind);
        Assert.False(f.Retryable);
    }

    [Fact]
    public void UserCancellation_IsDetected()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.True(RetryClassifier.IsUserCancellation(new OperationCanceledException(cts.Token), cts.Token));
        Assert.True(RetryClassifier.IsUserCancellation(new AggregateException(new TaskCanceledException()), cts.Token));
        Assert.False(RetryClassifier.IsUserCancellation(new TaskCanceledException(), CancellationToken.None));
    }

    [Fact]
    public void NoResponse_IsNetwork()
    {
        Assert.Equal(ErrorKind.Network, RetryClassifier.Classify(FakeResponse.Error(0), CancellationToken.None).Kind);
        Assert.Equal(ErrorKind.Network, RetryClassifier.Classify(new HttpRequestException("dns"), CancellationToken.None).Kind);
        Assert.Equal(ErrorKind.Network, RetryClassifier.Classify(new IOException("reset"), CancellationToken.None).Kind);
    }

    [Fact]
    public void UnknownException_IsNotRetried()
    {
        var f = RetryClassifier.Classify(new InvalidOperationException(), CancellationToken.None);
        Assert.Null(f.Kind);
        Assert.False(f.Retryable);
    }

    [Fact]
    public void RetryAfter_IsFollowed_AndCapped()
    {
        var shortWait = RetryClassifier.Classify(FakeResponse.Error(429, new() { ["Retry-After"] = "3" }), CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(3), RetryClassifier.DelayBeforeNext(shortWait, 1));

        var ms = RetryClassifier.Classify(FakeResponse.Error(429, new() { ["retry-after-ms"] = "1500" }), CancellationToken.None);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), RetryClassifier.DelayBeforeNext(ms, 1));

        // 상한(30초)을 넘으면 기다리지 않는다
        var longWait = RetryClassifier.Classify(FakeResponse.Error(429, new() { ["Retry-After"] = "120" }), CancellationToken.None);
        Assert.Null(RetryClassifier.DelayBeforeNext(longWait, 1));
    }

    [Theory]
    [InlineData("Retry-After", "999999999999999")]
    [InlineData("Retry-After", "Infinity")]
    [InlineData("retry-after-ms", "1e300")]
    public void HugeRetryAfter_IsTreatedAsOverCap(string header, string value)
    {
        // TimeSpan 범위를 넘는 값이 "헤더 없음"(2초 대기)으로 떨어지지 않고 상한 초과로 처리돼야 한다.
        var f = RetryClassifier.Classify(FakeResponse.Error(429, new() { [header] = value }), CancellationToken.None);
        Assert.Null(RetryClassifier.DelayBeforeNext(f, 1));
    }

    [Fact]
    public void DefaultDelay_Is2Then5()
    {
        var f = RetryClassifier.Classify(FakeResponse.Error(500), CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(2), RetryClassifier.DelayBeforeNext(f, 1));
        Assert.Equal(TimeSpan.FromSeconds(5), RetryClassifier.DelayBeforeNext(f, 2));
    }
}
