using HB.Orchestration.Dashboard;
using Microsoft.Extensions.Options;

namespace HB.Orchestration.Tests;

/// <summary>PricingService의 파일 다시 확인 간격 · 저장 직후 반영 · 읽기 실패 때 마지막 정상 값 (계획서 2026-10-23_대시보드-비용계산-성능, /self-review eb025da67b).</summary>
public sealed class PricingServiceTests : IDisposable
{
    /// <summary>밀리초 단위로 직접 움직이는 시계.</summary>
    private sealed class ManualTime : TimeProvider
    {
        public long Ms;
        public override long GetTimestamp() => Ms;
        public override long TimestampFrequency => 1000;
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hb-pricing-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _settings;
    private readonly ManualTime _time = new();
    private DateTime _written = new(2026, 10, 23, 0, 0, 0, DateTimeKind.Utc);

    private static readonly long Interval = (long)PricingService.RecheckInterval.TotalMilliseconds;

    public PricingServiceTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "modes.json"), "{}");
        _settings = Path.Combine(_dir, "appsettings.json");
        WriteSettings(1);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>입력 단가만 바꾼 설정 파일. 같은 길이여도 시각이 바뀌어 다른 판으로 보이게 한다.</summary>
    private void WriteSettings(decimal input) =>
        WriteRaw("""{"Orchestration":{"Pricing":{"Models":{"m":{"InputPerMillion":""" + input + ""","OutputPerMillion":2}}}}}""");

    private void WriteRaw(string text)
    {
        File.WriteAllText(_settings, text);
        _written = _written.AddMinutes(1);
        File.SetLastWriteTimeUtc(_settings, _written);
    }

    private (ModesConfigService Modes, PricingService Pricing) Create()
    {
        var options = Options.Create(new DashboardOptions { McpConfigDir = _dir });
        var modes = new ModesConfigService(options, _ => null);
        return (modes, new PricingService(options, modes, _time));
    }

    private static decimal? Input(PricingService p) => p.PriceOf("m")?.InputPerMillion;

    [Fact]
    public void External_edit_is_seen_after_recheck_interval()
    {
        var (_, pricing) = Create();
        Assert.Equal(1m, Input(pricing));

        WriteSettings(3);
        _time.Ms = 100;
        Assert.Equal(1m, Input(pricing)); // 간격 안: 파일을 다시 보지 않는다
        _time.Ms = Interval + 1;
        Assert.Equal(3m, Input(pricing));
    }

    [Fact]
    public void Save_through_modes_page_is_seen_immediately()
    {
        var (modes, pricing) = Create();
        Assert.Equal(1m, Input(pricing));

        var saved = modes.SavePrices(modes.Load().SettingsStamp, [new PriceEdit("m", "5", "", "6")], "");
        Assert.True(saved.Ok, string.Join("; ", saved.Errors));
        _time.Ms = 10; // 간격 안이어도 저장 횟수가 바뀌었으므로 다시 읽는다
        Assert.Equal(5m, Input(pricing));
    }

    [Fact]
    public void Broken_content_keeps_last_good_until_a_new_version()
    {
        var (_, pricing) = Create();
        Assert.Equal(1m, Input(pricing));

        WriteRaw("{ broken");
        _time.Ms = Interval + 1;
        Assert.Equal(1m, Input(pricing)); // 깨진 내용: 마지막 정상 값

        WriteSettings(7);
        _time.Ms = 2 * Interval + 2;
        Assert.Equal(7m, Input(pricing)); // 고친 판은 다음 확인 때
    }

    [Fact]
    public void Missing_file_keeps_last_good()
    {
        var (_, pricing) = Create();
        Assert.Equal(1m, Input(pricing));

        File.Delete(_settings); // 편집기의 이름 바꾸기 저장 사이
        _time.Ms = Interval + 1;
        Assert.Equal(1m, Input(pricing));

        WriteSettings(4);
        _time.Ms = 2 * Interval + 2;
        Assert.Equal(4m, Input(pricing));
    }

    [Fact]
    public void Empty_file_keeps_last_good()
    {
        var (_, pricing) = Create();
        Assert.Equal(1m, Input(pricing));

        WriteRaw(""); // 메모장이 길이를 0으로 자른 직후
        _time.Ms = Interval + 1;
        Assert.Equal(1m, Input(pricing));

        WriteSettings(6);
        _time.Ms = 2 * Interval + 2;
        Assert.Equal(6m, Input(pricing));
    }

    [Fact]
    public void No_config_folder_means_no_prices()
    {
        var options = Options.Create(new DashboardOptions());
        var pricing = new PricingService(options, new ModesConfigService(options, _ => null), _time);
        Assert.Null(Input(pricing));
    }
}
