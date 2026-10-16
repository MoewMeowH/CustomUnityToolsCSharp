using System.Text;
using HB.Orchestration.Agents;
using HB.Orchestration.Tests.Fakes;
using Microsoft.Extensions.Configuration;

namespace HB.Orchestration.Tests;

/// <summary>
/// WorkOrders.Folder: 비어 있으면 list_modes 출력이 이전과 같고, 정상 값은 "지시서 폴더:" 줄로,
/// 잘못된 값은 [주의] 줄로만 보인다 (서버는 파일을 쓰지 않는다).
/// </summary>
public sealed class WorkOrdersFolderTests : IDisposable
{
    private readonly ModeRunnerHarness _h = new();

    public void Dispose() => _h.Dispose();

    private string Describe(string? folder)
    {
        _h.Options.WorkOrders.Folder = folder;
        return _h.Runner.Describe();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_KeepsExistingOutput(string folder)
    {
        var before = Describe(null);
        Assert.Equal(before, Describe(folder));
        Assert.DoesNotContain("지시서 폴더", before);
        Assert.DoesNotContain("WorkOrders", before);
    }

    [Theory]
    [InlineData("docs/work-orders", "docs/work-orders")]
    [InlineData("docs\\work-orders\\", "docs/work-orders")]
    [InlineData("./review_tmp/orders", "review_tmp/orders")]
    public void Valid_ShowsFolderLine(string folder, string shown)
    {
        var output = Describe(folder);
        Assert.EndsWith($"지시서 폴더: {shown}", output);
        Assert.DoesNotContain("[주의] WorkOrders", output);
    }

    [Theory]
    [InlineData("C:/orders")]
    [InlineData("C:orders")]
    [InlineData("/orders")]
    [InlineData("\\\\server\\share")]
    [InlineData("../orders")]
    [InlineData("docs/../../orders")]
    [InlineData("..\\orders")]
    [InlineData("docs\\..\\..\\orders")]
    [InlineData("@orch/orders")]
    [InlineData(".")]
    public void Invalid_ShowsWarningOnly(string folder)
    {
        var output = Describe(folder);
        Assert.Contains($"[주의] WorkOrders.Folder '{folder}':", output);
        Assert.DoesNotContain("지시서 폴더:", output);
    }

    [Fact]
    public void Binding_ReadsFolder_DefaultIsEmpty()
    {
        var json = """{ "Orchestration": { "WorkOrders": { "Folder": "docs/orders" } } }""";
        var config = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();
        Assert.Equal("docs/orders", config.GetSection("Orchestration").Get<OrchestrationOptions>()!.WorkOrders.Folder);
        Assert.Null(new OrchestrationOptions().WorkOrders.Folder);
    }
}
