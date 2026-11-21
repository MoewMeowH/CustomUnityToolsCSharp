using System.Drawing;
using System.Text.Json;

namespace HBLint.App.Desktop;

internal sealed record Placement(int X, int Y, int Width, int Height, bool Maximized);

/// <summary>창 크기 · 자리 기억 (%LOCALAPPDATA%\HBLint\window.json). 못 읽거나 어느 화면에도 충분히 걸치지 않으면 기본 자리.</summary>
internal static class WindowPlacement
{
    /// <summary>제목 줄을 잡아 옮길 수 있을 만큼은 보여야 한다.</summary>
    private const int MinVisibleWidth = 120;
    private const int MinVisibleHeight = 40;
    private const int MinSize = 400;

    public static Placement? Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var placement = JsonSerializer.Deserialize<Placement>(File.ReadAllText(path));
            return placement is { Width: >= MinSize, Height: >= MinSize } ? placement : null;
        }
        catch (Exception e) when (e is JsonException || e is IOException || e is UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Save(string path, Placement placement)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(placement));
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            // 창 자리는 편의 — 못 써도 앱은 돈다
        }
    }

    /// <summary>창의 위쪽 띠(제목 줄 높이)가 어느 화면 작업 영역과 충분히 겹치는가.</summary>
    public static bool Fits(Placement placement, IEnumerable<Rectangle> screens)
    {
        var titleBar = new Rectangle(placement.X, placement.Y, placement.Width, MinVisibleHeight);
        return screens.Any(screen =>
        {
            var overlap = Rectangle.Intersect(screen, titleBar);
            return overlap.Width >= MinVisibleWidth && overlap.Height >= MinVisibleHeight;
        });
    }
}
