using System.Collections.Concurrent;
using HB.Orchestration.Shared;

namespace HB.Chat;

/// <summary>
/// 방의 세션 기록 찾기 · 읽기. 세션 기록은 ~/.claude/projects/&lt;작업 폴더 이름&gt;/&lt;세션 ID&gt;.jsonl이고,
/// 폴더 이름 규칙에 기대지 않고 프로젝트 폴더들 바로 아래에서 파일 이름으로 찾아 기억한다 (대시보드 ClaudeSessionsService와 같은 방식).
/// 읽기는 <see cref="ClaudeChatReader.ReadMore"/>로 붙은 줄만 이어 읽는다 (방마다 잠금).
/// </summary>
public sealed class ChatTranscripts(Func<string> projectsRoot)
{
    private sealed class Entry
    {
        public readonly Lock Gate = new();
        public ChatResume? Resume;
    }

    private readonly ConcurrentDictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>세션 기록 파일. 아직 없으면(첫 말 전) null.</summary>
    public string? Find(string sessionId)
    {
        if (_paths.TryGetValue(sessionId, out var known) && File.Exists(known)) return known;
        var root = projectsRoot();
        if (!Directory.Exists(root)) return null;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var path = Path.Combine(dir, sessionId + ".jsonl");
            if (File.Exists(path)) return _paths[sessionId] = path;
        }
        return null;
    }

    public long? Size(string sessionId) => Find(sessionId) is { } path ? new FileInfo(path).Length : null;

    /// <summary>세션 기록 본문. 파일이 없으면 null. 읽다 실패하면 FormatError가 있는 결과 (다음엔 처음부터).</summary>
    public ChatTranscript? Read(string sessionId)
    {
        if (Find(sessionId) is not { } path) return null;
        var entry = _cache.GetOrAdd(sessionId, _ => new Entry());
        lock (entry.Gate)
        {
            var (chat, resume) = ClaudeChatReader.ReadMore(path, entry.Resume);
            entry.Resume = resume;
            return chat;
        }
    }
}
