using System.Drawing;
using System.Text.Json;

namespace HB.Chat.Desktop;

/// <summary>창 위치 · 크기 기억 (데이터 폴더 desktop-window.json). 모니터 구성이 바뀌어 화면 밖이면 기본 위치로.</summary>
public sealed record WindowPlacement(int X, int Y, int Width, int Height, bool Maximized)
{
    public static readonly Size MinSize = new(640, 480);

    public Rectangle Bounds => new(X, Y, Width, Height);

    /// <summary>보이는 화면과 충분히(가로 · 세로 120px 이상) 겹치면 그대로, 아니면 null (기본 위치).</summary>
    public WindowPlacement? FitTo(IEnumerable<Rectangle> screens)
    {
        if (Width < MinSize.Width || Height < MinSize.Height) return null;
        foreach (var s in screens)
        {
            var overlap = Rectangle.Intersect(s, Bounds);
            if (overlap.Width >= 120 && overlap.Height >= 120) return this;
        }
        return null;
    }

    public static WindowPlacement? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 위치를 못 남겨도 끄기는 한다
        }
    }
}
