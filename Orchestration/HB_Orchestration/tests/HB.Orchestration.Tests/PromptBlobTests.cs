using System.Text;
using System.Text.Json.Nodes;
using HB.Orchestration.Shared;

namespace HB.Orchestration.Tests;

/// <summary>큰 프롬프트를 blobs로 · 원문 읽기 · 예전 기록 압축.</summary>
public sealed class PromptBlobTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hb-prompt-blob-").FullName;
    private string Log => Path.Combine(_dir, "calls.jsonl");
    private string Blobs => Path.Combine(_dir, "blobs");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static readonly string Big = "가" + new string('x', CallLog.PromptBlobMinBytes) + "끝";

    private static CallRecord Record(string id, string prompt) => new()
    {
        Id = id, Timestamp = DateTimeOffset.Now, Tool = "review", Model = "m", Prompt = prompt, Success = true, Response = "ok",
    };

    [Fact]
    public void Small_prompt_stays_inline()
    {
        var r = CallLog.WithPromptBlob(Record("a", "작은 프롬프트"), Blobs, out var error);
        Assert.Null(error);
        Assert.Equal(("작은 프롬프트", (string?)null), (r.Prompt, r.PromptSha256));
        Assert.False(Directory.Exists(Blobs));
    }

    [Fact]
    public void Big_prompt_goes_to_blob_and_reads_back()
    {
        var r = CallLog.WithPromptBlob(Record("a", Big), Blobs, out var error);

        Assert.Null(error);
        Assert.NotNull(r.PromptSha256);
        Assert.Equal(Encoding.UTF8.GetByteCount(Big), r.PromptBytes);
        Assert.True(r.Prompt.Length < Big.Length);
        Assert.StartsWith(Big[..CallLog.PromptPreviewChars], r.Prompt);
        Assert.Contains("CallLog.FullPrompt", r.Prompt); // 안내는 폴더 위치 대신 읽는 방법 (설정 폴더가 다를 수 있다)
        Assert.True(File.Exists(BlobStore.PathFor(Blobs, r.PromptSha256!)));

        var withFolder = r with { Provenance = new CallProvenance { BlobFolder = Blobs } };
        Assert.Equal((Big, false), CallLog.FullPrompt(withFolder));
        Assert.Equal((Big, false), CallLog.FullPrompt(r, Log)); // 폴더 기록이 없으면 호출 기록 옆 blobs/
    }

    [Fact]
    public void Missing_or_tampered_blob_reports_missing_with_preview()
    {
        var r = CallLog.WithPromptBlob(Record("a", Big), Blobs, out _);
        var path = BlobStore.PathFor(Blobs, r.PromptSha256!);

        File.WriteAllText(path, "다른 내용");
        Assert.Equal((r.Prompt, true), CallLog.FullPrompt(r, Log));
        File.Delete(path);
        Assert.Equal((r.Prompt, true), CallLog.FullPrompt(r, Log));
    }

    [Fact]
    public void Store_failure_keeps_full_prompt_and_reports()
    {
        var notAFolder = Path.Combine(_dir, "file");
        File.WriteAllText(notAFolder, "x"); // 폴더 자리에 파일 → 저장 실패
        var r = CallLog.WithPromptBlob(Record("a", Big), notAFolder, out var error);

        Assert.NotNull(error);
        Assert.Equal((Big, (string?)null), (r.Prompt, r.PromptSha256));
    }

    [Fact]
    public void Corrupted_existing_blob_keeps_full_prompt()
    {
        var sha = BlobStore.Sha256Hex(Encoding.UTF8.GetBytes(Big));
        Directory.CreateDirectory(Blobs);
        File.WriteAllText(BlobStore.PathFor(Blobs, sha), "손상된 내용");

        var r = CallLog.WithPromptBlob(Record("a", Big), Blobs, out var error);

        Assert.Contains("해시와 다름", error);
        Assert.Equal((Big, (string?)null), (r.Prompt, r.PromptSha256));
    }

    [Fact]
    public void Empty_blob_folder_falls_back_to_log_folder()
    {
        var r = CallLog.WithPromptBlob(Record("a", Big), Blobs, out _) with { Provenance = new CallProvenance { BlobFolder = "" } };
        Assert.Equal((Big, false), CallLog.FullPrompt(r, Log));
    }

    [Fact]
    public void Preview_does_not_split_surrogate_pair()
    {
        var prompt = new string('a', CallLog.PromptPreviewChars - 1) + "😀" + new string('b', CallLog.PromptBlobMinBytes);
        var r = CallLog.WithPromptBlob(Record("a", prompt), Blobs, out _);

        Assert.NotNull(r.PromptSha256);
        Assert.DoesNotContain(r.Prompt, char.IsSurrogate); // 반쪽 이모지 없음 (잘린 자리 앞에서 끊음)
        Assert.Equal((prompt, false), CallLog.FullPrompt(r, Log));
    }

    [Fact]
    public void Small_records_do_not_write_null_prompt_fields()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(Record("a", "p"));
        Assert.DoesNotContain("PromptSha256", json);
        Assert.DoesNotContain("PromptBytes", json);
    }

    [Fact]
    public async Task Compact_keeps_unfinished_last_line_as_tail_and_backups_do_not_collide()
    {
        await CallLog.AppendAsync(Record("a", Big), Log);
        File.AppendAllText(Log, """{"id":"half","prom"""); // 다른 서버가 쓰는 중이던 줄 (줄바꿈 없음)
        var original = File.ReadAllBytes(Log);

        var first = CallLog.CompactPrompts(Log);
        Assert.Equal((1, 1), (first.Lines, first.Moved));
        Assert.EndsWith("""{"id":"half","prom""", File.ReadAllText(Log)); // 반쪽 줄은 그대로 꼬리로
        Assert.Equal(original, File.ReadAllBytes(first.Backup!));

        var second = CallLog.CompactPrompts(Log); // 같은 초 안에 다시
        Assert.NotEqual(first.Backup, second.Backup);
        Assert.Equal(original, File.ReadAllBytes(first.Backup!)); // 첫 백업(원본)이 덮이지 않음
        Assert.Equal(0, second.Moved);
    }

    [Fact]
    public void Legacy_record_without_hash_reads_as_is()
    {
        Assert.Equal((Big, false), CallLog.FullPrompt(Record("a", Big), Log));
    }

    [Fact]
    public async Task Compact_moves_big_prompts_keeps_other_lines_and_backup()
    {
        await CallLog.AppendAsync(Record("a", Big), Log);
        await CallLog.AppendAsync(Record("b", "작은"), Log);
        await CallLog.AppendAsync(Record("c", Big), Log); // 같은 프롬프트 → blob 하나
        // 모르는 필드가 있는 예전 줄 · 깨진 줄
        File.AppendAllText(Log, """{"id":"d","timestamp":"2026-10-01T00:00:00+09:00","tool":"golden","model":"m","prompt":""" + JsonValue.Create(Big)!.ToJsonString() + ""","success":true,"oldField":{"x":1}}""" + "\n");
        File.AppendAllText(Log, "{broken\n");
        var original = File.ReadAllBytes(Log);

        var result = CallLog.CompactPrompts(Log);

        Assert.Equal((5, 3, 2), (result.Lines, result.Moved, result.Kept));
        Assert.Empty(result.Errors);
        Assert.True(result.BytesAfter < result.BytesBefore);
        Assert.Equal(original, File.ReadAllBytes(result.Backup!));
        Assert.Single(Directory.GetFiles(Blobs, "*.txt"));

        var text = File.ReadAllText(Log);
        Assert.Contains("\"oldField\":{\"x\":1}", text);   // 모르는 필드 유지
        Assert.EndsWith("{broken\n", text);                  // 깨진 줄 그대로

        var records = CallLog.ReadAll(Log).ToDictionary(r => r.Id);
        Assert.Equal("작은", records["b"].Prompt);
        foreach (var id in new[] { "a", "c", "d" })
            Assert.Equal((Big, false), CallLog.FullPrompt(records[id], Log));

        Assert.Equal(0, CallLog.CompactPrompts(Log).Moved); // 이미 나눈 줄은 그대로
    }
}
