using HB.Orchestration.Shared;

namespace HB.Orchestration.Dashboard;

/// <summary>호출 상세 화면의 "에이전트가 받은 지침·설정" 표 한 줄.</summary>
public sealed record ProvenanceRow(string Label, string? Path, string? Sha256, long? Bytes, string? Note, bool BlobAvailable);

public static class ProvenanceView
{
    /// <summary>기록의 Provenance를 표 줄로. 원문이 저장 폴더에 실제로 있을 때만 BlobAvailable.</summary>
    public static List<ProvenanceRow> Rows(CallProvenance p)
    {
        bool Has(string? sha) => sha is not null && p.BlobFolder is not null && BlobStore.IsValidSha(sha)
                                 && SafeExists(BlobStore.PathFor(p.BlobFolder, sha));

        var rows = new List<ProvenanceRow>
        {
            new("시스템 프롬프트", null, p.InstructionsSha256, p.InstructionsBytes, "역할 + 공통 규칙 + 지침 (에이전트에 넘긴 문자열 그대로)", Has(p.InstructionsSha256)),
        };

        var g = p.Guidelines;
        rows.Add(g is null
            ? new("리뷰 지침", null, null, null, "기록 없음", false)
            : g.Attached
                ? new("리뷰 지침", g.Path, g.Sha256, g.Bytes, "붙임 (원본 파일 바이트)", Has(g.Sha256))
                : new("리뷰 지침", null, null, null, $"붙이지 않음 — {g.Reason}", false));

        rows.Add(p.Modes is { } m
            ? new("modes.json", m.Path, m.Sha256, m.Bytes, null, Has(m.Sha256))
            : new("modes.json", null, null, null, "기록 없음", false));

        rows.Add(p.Settings is { } s
            ? new("서버 설정", s.Path, s.Sha256, s.Bytes,
                $"키 검사 {s.SecretScan ?? "-"}{(s.Stored ? "" : " · 원문 저장 안 함")}{(s.ReloadError is { } e ? $" · 다시 읽기 실패로 이전 설정 적용 중: {e}" : "")}",
                s.Stored && Has(s.Sha256))
            : new("서버 설정", null, null, null, "기록 없음", false));

        return rows;
    }

    /// <summary>이 기록이 원문 열기를 허용하는 해시 (저장 폴더의 다른 파일은 열지 않는다).</summary>
    public static IEnumerable<string> AllowedShas(CallProvenance p)
    {
        if (p.InstructionsSha256 is { } i) yield return i;
        if (p.Guidelines is { Attached: true, Sha256: { } g }) yield return g;
        if (p.Modes?.Sha256 is { } m) yield return m;
        if (p.Settings is { Stored: true, Sha256: { } s }) yield return s;
    }

    private static bool SafeExists(string path)
    {
        try { return File.Exists(path); }
        catch (Exception) { return false; }
    }
}
