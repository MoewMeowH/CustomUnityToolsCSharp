using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Toolkit.Uwp.Notifications;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace HB.Chat.Desktop;

/// <summary>토스트 한 장 (방 하나): 굵은 첫 줄 · 글 · 누르면 열 방.</summary>
public sealed record ToastSpec(string? RoomId, string Title, string Text);

/// <summary>
/// Windows 토스트 알림: 트레이 풍선 대신 앱 이름 · 아이콘 · 지휘자 프로필 그림 · "방 열기" 단추가 있는 알림.
/// 방마다 하나(Tag = 방 ID — 같은 방의 새 알림이 앞 것을 바꾼다), 그 방을 열면 알림 센터에서도 지운다. 바로 답장 입력칸은 없다.
/// 글은 풍선과 같은 <see cref="Notices.Balloon"/> (알림 본문 설정 · 승인 · 질문은 제목만 그대로).
/// </summary>
public static partial class Toasts
{
    public const string Group = "rooms", RoomArg = "room";

    /// <summary>알림마다 토스트 하나 — 같은 방이 둘이면 뒤 것만.</summary>
    public static IReadOnlyList<ToastSpec> For(IReadOnlyList<Notice> notices, bool withBody) =>
        [.. notices.GroupBy(n => n.RoomId).Select(g => g.Last()).Select(n =>
        {
            var (title, text) = Notices.Balloon([n], withBody);
            return new ToastSpec(n.RoomId, title, text.Length > 0 ? text : "(제목 없음)");
        })];

    /// <summary>토스트를 눌렀을 때 받은 인자에서 방 ID. 모양이 틀리면 null (앞으로만 부른다).</summary>
    public static string? RoomFrom(string? argument)
    {
        if (string.IsNullOrEmpty(argument)) return null;
        try
        {
            return ToastArguments.Parse(argument).TryGetValue(RoomArg, out var id) && MainForm.IsRoomId(id) ? id : null;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>서버가 준 지휘자 프로필 그림 주소가 앱이 만든 이름 모양인지 (/profile-image/16hex-8hex.png|jpg|webp).</summary>
    public static bool IsProfileImagePath(string? url) => url is not null && ProfileImagePath().IsMatch(url);

    /// <summary>내려 둔 로고 파일 이름: 주소 해시 + .png (그림이 바뀌면 주소도 바뀐다).</summary>
    public static string CacheName(string url) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16] + ".png";

    /// <summary>토스트를 띄운다. logo는 로컬 png 경로 (패키지 없는 앱은 웹 그림을 못 쓴다), 없으면 앱 아이콘.</summary>
    public static void Show(ToastSpec spec, string? logo)
    {
        var builder = new ToastContentBuilder().AddText(spec.Title).AddText(spec.Text);
        if (spec.RoomId is { } room)
        {
            builder.AddArgument(RoomArg, room)
                .AddButton(new ToastButton().SetContent("방 열기").AddArgument(RoomArg, room));
        }
        if (logo is not null && File.Exists(logo)) builder.AddAppLogoOverride(new Uri(logo), ToastGenericAppLogoCrop.Circle);
        builder.Show(t =>
        {
            t.Tag = spec.RoomId ?? "test";
            t.Group = Group;
        });
    }

    /// <summary>그 방 토스트를 알림 센터에서 지운다 (그 방을 열었을 때).</summary>
    public static void Remove(string roomId) => ToastNotificationManagerCompat.History.Remove(roomId, Group);

    /// <summary>
    /// 그림(png · jpg · webp — 설정 화면이 줄인 프로필 그림은 webp)을 png로 바꿔 저장한다. 토스트가 webp를 그리는지 알 수 없어
    /// Windows 그림 코덱(WIC)으로 다시 쓴다. 못 읽으면 false.
    /// </summary>
    public static async Task<bool> SavePngAsync(byte[] data, string path)
    {
        try
        {
            using var input = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(input))
            {
                writer.WriteBytes(data);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            input.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(input);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();
            var bytes = new byte[output.Size];
            using (var reader = new DataReader(output.GetInputStreamAt(0)))
            {
                await reader.LoadAsync((uint)output.Size);
                reader.ReadBytes(bytes);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes);
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"^/profile-image/[0-9a-f]{16}-[0-9a-f]{8}\.(png|jpg|webp)\z")]
    private static partial Regex ProfileImagePath();
}
