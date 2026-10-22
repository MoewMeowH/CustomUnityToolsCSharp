using System.Text;
using HB.Orchestration.Agents;
using HB.Orchestration.Shared;
using HB.Orchestration.Tests.Fakes;

namespace HB.Orchestration.Tests;

/// <summary>
/// 5장 A: 호출 기록만으로 에이전트가 받은 지침·설정을 확정할 수 있다.
/// 같은 실행이면 해시가 같고 원문은 한 번만, 지침이 바뀌면 해시가 바뀌고, 저장 원문 = 실제로 보낸 바이트, 저장 실패에도 호출 성공.
/// </summary>
public sealed class ProvenanceTests : IDisposable
{
    private readonly ModeRunnerHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static string Sha(byte[] bytes) => BlobStore.Sha256Hex(bytes);

    [Fact]
    public async Task SameModeTwice_SameHashes_BlobsStoredOnce()
    {
        await _h.Run("seq");
        var filesAfterFirst = Directory.GetFiles(_h.DefaultBlobFolder).Order().ToList();
        await _h.Run("seq");

        var records = _h.Records();
        Assert.Equal(2, records.Count);
        var (a, b) = (records[0].Provenance!, records[1].Provenance!);
        Assert.Equal(a.InstructionsSha256, b.InstructionsSha256);
        Assert.Equal(a.Guidelines, b.Guidelines);
        Assert.Equal(a.Modes, b.Modes);
        Assert.Equal(a.Settings, b.Settings);
        Assert.Null(a.StoreErrors);

        // 시스템 프롬프트 · 지침 · modes.json 3개. 두 번째 실행은 새 파일을 만들지 않는다 (임시 파일도 남지 않음).
        Assert.Equal(3, filesAfterFirst.Count);
        Assert.Equal(filesAfterFirst, Directory.GetFiles(_h.DefaultBlobFolder).Order().ToList());
    }

    [Fact]
    public async Task GuidelinesChange_ChangesNextHash()
    {
        await _h.Run("seq");
        File.WriteAllText(_h.GuidelinesPath, "# 지침\n- 바뀐 규칙\n");
        await _h.Run("seq");

        var (a, b) = (_h.Records()[0].Provenance!, _h.Records()[1].Provenance!);
        Assert.NotEqual(a.Guidelines!.Sha256, b.Guidelines!.Sha256);
        Assert.NotEqual(a.InstructionsSha256, b.InstructionsSha256);
        Assert.Equal(a.Modes, b.Modes);
    }

    [Fact]
    public async Task Guidelines_HashIsRawFileBytes()
    {
        await _h.Run("seq");
        var g = _h.Records()[0].Provenance!.Guidelines!;
        var raw = File.ReadAllBytes(_h.GuidelinesPath);

        Assert.True(g.Attached);
        Assert.Equal(Path.GetFullPath(_h.GuidelinesPath), g.Path);
        Assert.Equal(Sha(raw), g.Sha256);
        Assert.Equal(raw.Length, g.Bytes);
        Assert.Equal(raw, File.ReadAllBytes(BlobStore.PathFor(_h.DefaultBlobFolder, g.Sha256!)));
        Assert.Equal(File.ReadAllBytes(_h.ModesPath), File.ReadAllBytes(BlobStore.PathFor(_h.DefaultBlobFolder, _h.Records()[0].Provenance!.Modes!.Sha256!)));
    }

    [Theory]
    [InlineData("seq", 1)]
    [InlineData("conc", 2)]
    [InlineData("deb", 3)] // 토론자 둘은 DebateRules가 붙고 판정자는 안 붙는다
    public async Task StoredInstructions_EqualBytesSentToClient(string mode, int calls)
    {
        await _h.Run(mode);

        Assert.Equal(calls, _h.Client.Received.Count);
        var records = _h.Records();
        Assert.Equal(calls, records.Count);

        // 기록 순서와 클라이언트 호출 순서가 같지 않을 수 있으므로(Concurrent) 해시 집합으로 비교한다.
        var sent = _h.Client.Received.Select(ScriptedChatClient.SystemText).ToList();
        Assert.All(sent, s => Assert.False(string.IsNullOrEmpty(s)));
        var sentHashes = sent.Select(s => Sha(new UTF8Encoding(false).GetBytes(s))).Order().ToList();
        Assert.Equal(sentHashes, records.Select(r => r.Provenance!.InstructionsSha256!).Order().ToList());

        foreach (var s in sent)
        {
            var bytes = new UTF8Encoding(false).GetBytes(s);
            Assert.Equal(bytes, File.ReadAllBytes(BlobStore.PathFor(_h.DefaultBlobFolder, Sha(bytes))));
        }

        if (mode == "deb")
        {
            Assert.Contains(sent, s => s.Contains("## 토론 규칙"));
            Assert.Contains(sent, s => !s.Contains("## 토론 규칙"));
        }
    }

    [Fact]
    public async Task RoleDisabled_RecordsNotAttachedWithReason()
    {
        await _h.Run("two");
        var records = _h.Records();

        Assert.True(records.Single(r => r.Step == 1).Provenance!.Guidelines!.Attached);
        var off = records.Single(r => r.Step == 2).Provenance!.Guidelines!;
        Assert.False(off.Attached);
        Assert.Equal(GuidelinesProvenance.ReasonRoleDisabled, off.Reason);
        Assert.Null(off.Sha256);
    }

    [Fact]
    public async Task NoGuidelinesFile_RecordsNoFile()
    {
        // 지정 없음 + 현재 폴더에도 없음 (지정 경로에 파일이 없으면 2026-10-22부터 오류 — GuidelinesLoaderTests)
        _h.Options.GuidelinesPath = null;
        _h.Runner.LoadGuidelines = o => GuidelinesLoader.Load(o, _ => null, Path.Combine(_h.Root, "empty"));
        await _h.Run("seq");

        var g = _h.Records().Single().Provenance!.Guidelines!;
        Assert.False(g.Attached);
        Assert.Equal(GuidelinesProvenance.ReasonNoFile, g.Reason);
    }

    [Fact]
    public async Task OnlyDisabledRoles_DoesNotStoreGuidelines()
    {
        await _h.Run("noG");
        var p = _h.Records().Single().Provenance!;
        Assert.False(File.Exists(BlobStore.PathFor(_h.DefaultBlobFolder, Sha(File.ReadAllBytes(_h.GuidelinesPath)))));
        Assert.Equal(2, Directory.GetFiles(_h.DefaultBlobFolder).Length); // instructions + modes
        Assert.Null(p.StoreErrors);
    }

    [Fact]
    public async Task BlobFolderUnwritable_CallStillSucceeds()
    {
        var blocker = Path.Combine(_h.Root, "not-a-folder");
        File.WriteAllText(blocker, "파일이라 폴더를 만들 수 없음");
        _h.Options.Records.BlobFolder = blocker;

        var result = await _h.Run("seq");

        Assert.DoesNotContain("[실패]", result);
        var record = _h.Records().Single();
        Assert.True(record.Success);
        var p = record.Provenance!;
        Assert.NotNull(p.InstructionsSha256);   // 해시는 저장 실패와 무관하게 남는다
        Assert.NotNull(p.Modes!.Sha256);
        Assert.NotNull(p.StoreErrors);
        Assert.Contains(p.StoreErrors!, e => e.StartsWith("modes:"));
        Assert.Contains(p.StoreErrors!, e => e.StartsWith("instructions:"));
    }

    [Fact]
    public async Task Settings_DefaultIsHashOnly()
    {
        await _h.Run("seq");
        var s = _h.Records().Single().Provenance!.Settings!;
        var raw = File.ReadAllBytes(_h.SettingsPath);

        Assert.Equal(Sha(raw), s.Sha256);
        Assert.False(s.Stored);
        Assert.Equal("통과", s.SecretScan);
        Assert.False(File.Exists(BlobStore.PathFor(_h.DefaultBlobFolder, s.Sha256!)));
    }

    [Fact]
    public async Task Settings_StoreText_RefusedWhenSecretLike()
    {
        File.WriteAllText(_h.SettingsPath, """{ "Orchestration": { "Providers": { "x": { "apiKey": "abc" } } } }""");
        _h.Options.Records.StoreSettingsText = true;
        await _h.Run("seq");

        var p = _h.Records().Single().Provenance!;
        Assert.False(p.Settings!.Stored);
        Assert.Equal("의심 1건", p.Settings.SecretScan);
        Assert.Contains(p.StoreErrors!, e => e.StartsWith("settings:"));
        Assert.False(File.Exists(BlobStore.PathFor(_h.DefaultBlobFolder, p.Settings.Sha256!)));
    }

    [Fact]
    public async Task Settings_StoreText_StoredWhenClean()
    {
        _h.Options.Records.StoreSettingsText = true;
        await _h.Run("seq");

        var s = _h.Records().Single().Provenance!.Settings!;
        Assert.True(s.Stored);
        Assert.Equal(File.ReadAllBytes(_h.SettingsPath), File.ReadAllBytes(BlobStore.PathFor(_h.DefaultBlobFolder, s.Sha256!)));
    }

    // ── review 8f0fe57a52 반영 ─────────────────────────────────────────────

    [Fact]
    public async Task Settings_HashIsAppliedSnapshot_WhenReloadFails()
    {
        var applied = File.ReadAllBytes(_h.SettingsPath);
        _h.FailLoad = true;
        File.WriteAllText(_h.SettingsPath, """{ "바뀐": "설정" }""");
        File.SetLastWriteTimeUtc(_h.SettingsPath, DateTime.UtcNow.AddMinutes(1)); // 수정 시각 변경 → 다시 읽기 시도

        await _h.Run("seq");

        var s = _h.Records().Single().Provenance!.Settings!;
        Assert.Equal(Sha(applied), s.Sha256);   // 디스크의 새 파일이 아니라 실제 적용 중인 설정
        Assert.NotNull(s.ReloadError);
    }

    [Fact]
    public async Task Settings_StoreText_NotStoredWhenFolderInvalid()
    {
        _h.Options.Records.StoreSettingsText = true;
        _h.Options.Records.BlobFolder = "bad\0name";
        await _h.Run("seq");

        var p = _h.Records().Single().Provenance!;
        Assert.False(p.Settings!.Stored);
        Assert.Contains(p.StoreErrors!, e => e.StartsWith("blobs 폴더:"));
    }

    [Fact]
    public async Task GuidelinesLocked_RejectsWithoutCounting()
    {
        using (new FileStream(_h.GuidelinesPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await _h.Run("seq");
            Assert.StartsWith("[리뷰 지침 읽기 실패]", result);
        }
        Assert.Equal(0, _h.Sessions.Get("seq"));
        Assert.Equal(0, _h.Client.Calls);
    }

    [Fact]
    public async Task ModesLocked_RejectsAsConfigError()
    {
        using (new FileStream(_h.ModesPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await _h.Run("seq");
            Assert.StartsWith("[모드 설정 오류]", result);
        }
        Assert.Equal(0, _h.Client.Calls);
    }

    [Theory]
    [InlineData("""{ "ApiKeyEnv": "OPENAI_API_KEY", "MaxOutputTokens": 100 }""", 0)]
    [InlineData("""{ "Endpoint": "https://x/?k=sk-abcdefghijklmnopqrstu" }""", 1)]
    [InlineData("""{ "k": "AIzaSyA1234567890abcdefghijklmnopqrstu" }""", 1)]
    [InlineData("""{ "Authorization": "Bearer abcdefghijklmnop1234" }""", 1)]
    [InlineData("""{ "Password": "x", "token": "y" }""", 2)]
    public void SecretPatterns(string text, int expected) =>
        Assert.Equal(expected, ProvenanceBuilder.CountSecretPatterns(text));

    [Fact]
    public void OldRecordLine_HasNullProvenance_AndNewFieldsRoundTrip()
    {
        var path = Path.Combine(_h.Root, "old.jsonl");
        File.WriteAllText(path,
            """{"id":"a","timestamp":"2026-10-16T16:37:10+09:00","tool":"review","model":"m","prompt":"p","success":true}""" + "\n");
        Assert.Null(CallLog.ReadAll(path).Single().Provenance);
    }

    [Fact]
    public async Task StoreErrors_OmittedWhenNone()
    {
        await _h.Run("seq");
        var line = File.ReadAllLines(_h.LogPath).Single();
        Assert.Contains("\"provenance\":", line);
        Assert.DoesNotContain("storeErrors", line);
    }
}
