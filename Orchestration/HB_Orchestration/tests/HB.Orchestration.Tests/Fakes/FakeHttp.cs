using System.ClientModel;
using System.ClientModel.Primitives;

namespace HB.Orchestration.Tests.Fakes;

/// <summary>ClientResultException을 상태 코드·헤더와 함께 만들기 위한 최소 응답.</summary>
public sealed class FakeResponse(int status, Dictionary<string, string>? headers = null) : PipelineResponse
{
    public override int Status => status;
    public override string ReasonPhrase => "";
    public override Stream? ContentStream { get; set; }
    public override BinaryData Content => BinaryData.FromString("");
    protected override PipelineResponseHeaders HeadersCore { get; } = new FakeHeaders(headers ?? []);
    public override BinaryData BufferContent(CancellationToken cancellationToken = default) => Content;
    public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) => new(Content);
    public override void Dispose() { }

    public static ClientResultException Error(int status, Dictionary<string, string>? headers = null) =>
        new($"HTTP {status}", new FakeResponse(status, headers));

    private sealed class FakeHeaders(Dictionary<string, string> values) : PipelineResponseHeaders
    {
        private readonly Dictionary<string, string> _values = new(values, StringComparer.OrdinalIgnoreCase);

        public override bool TryGetValue(string name, out string? value) => _values.TryGetValue(name, out value);

        public override bool TryGetValues(string name, out IEnumerable<string>? values)
        {
            values = _values.TryGetValue(name, out var v) ? [v] : null;
            return values is not null;
        }

        public override IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _values.GetEnumerator();
    }
}
