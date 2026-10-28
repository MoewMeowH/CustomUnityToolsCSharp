using HB.Chat;
using HB.Orchestration.ChatUi;
using Microsoft.Extensions.Options;

namespace HB.Chat.Tests;

/// <summary>주체별 프로필 (3차 e): 이름 · 색 검사 · 저장 · 다시 읽기, 그림(내용으로 형식 · 크기 · 바꾸면 앞 그림 지움), 표정, 화면 HTML.</summary>
public sealed class ProfileStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hbchat-profile-" + Guid.NewGuid().ToString("N"));

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];
    private static readonly byte[] Webp = [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8];

    public ProfileStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private ProfileStore New() => new(_dir);

    [Fact]
    public void Save_ValidatesAndReloads()
    {
        var store = New();
        Assert.Null(store.Save(ProfileStore.User, " 호빈 ", "#1a2B3c"));
        Assert.Null(store.Save(ProfileStore.AgentKey("gpt-5.5"), "솔", null));
        Assert.NotNull(store.Save("someone", "x", null));                       // 모르는 주체
        Assert.NotNull(store.Save(ProfileStore.Conductor, "a\nb", null));        // 제어 문자
        Assert.NotNull(store.Save(ProfileStore.Conductor, new string('a', 31), null));
        Assert.NotNull(store.Save(ProfileStore.Conductor, null, "red"));         // 색 모양
        Assert.NotNull(store.Save(ProfileStore.Conductor, null, "#123456\"x"));  // style에 들어가므로 #rrggbb만

        var again = New();
        Assert.Equal(new Profile("호빈", "#1a2B3c"), again.Get(ProfileStore.User));
        Assert.Equal("솔", again.Get("agent:gpt-5.5").Name);
        Assert.Equal(["gpt-5.5"], again.Agents());
        Assert.Null(store.Save(ProfileStore.User, "", "")); // 비우면 기본 (항목도 빠짐)
        Assert.Equal(new Profile(), New().Get(ProfileStore.User));
    }

    [Fact]
    public async Task Image_SniffsContent_ReplacesAndClears()
    {
        var store = New();
        Assert.Null(await store.SetImageAsync(ProfileStore.Conductor, new MemoryStream(Png), default));
        var first = store.Get(ProfileStore.Conductor).Image!;
        Assert.Matches(@"^[0-9a-f]{16}-[0-9a-f]{8}\.png$", first); // 이름은 앱이 정함
        Assert.NotNull(store.ImagePath(first));

        Assert.Null(await store.SetImageAsync(ProfileStore.Conductor, new MemoryStream(Webp), default));
        var second = store.Get(ProfileStore.Conductor).Image!;
        Assert.EndsWith(".webp", second);
        Assert.Null(store.ImagePath(first)); // 앞 그림은 지움

        Assert.Contains("png · jpg · webp", await store.SetImageAsync(ProfileStore.Conductor, new MemoryStream("<svg onload=x>"u8.ToArray()), default));
        Assert.Contains("KB까지", await store.SetImageAsync(ProfileStore.Conductor, new MemoryStream([.. Png, .. new byte[ProfileStore.MaxImageBytes]]), default));
        Assert.Equal(second, store.Get(ProfileStore.Conductor).Image); // 실패하면 그대로

        Assert.Null(store.ClearImage(ProfileStore.Conductor));
        Assert.Null(store.Get(ProfileStore.Conductor).Image);
        Assert.Null(store.ImagePath(second));
    }

    [Theory]
    [InlineData("../profiles.json")]
    [InlineData("0123456789abcdef-01234567.svg")]
    [InlineData("0123456789ABCDEF-01234567.png")]
    [InlineData(null)]
    public void ImagePath_OnlyAppNames(string? file) => Assert.Null(New().ImagePath(file));

    [Fact]
    public void Faces_OnlyKnownTags_AndReload()
    {
        var store = New();
        Assert.NotNull(store.SetFaces(new Dictionary<string, string?> { ["Idle"] = "없는태그" }, ["대기", "잠"]));
        Assert.NotNull(store.SetFaces(new Dictionary<string, string?> { ["Nope"] = "잠" }, ["대기", "잠"]));
        Assert.Null(store.SetFaces(new Dictionary<string, string?> { ["Idle"] = "잠", ["Done"] = "" }, ["대기", "잠"]));
        Assert.Equal("잠", New().FaceTag(Face.Idle));
        Assert.Null(New().FaceTag(Face.Done)); // 비우면 기본
    }

    [Theory]
    [InlineData("1")]          // 숫자 (정의 안 된 값도 TryParse는 받는다)
    [InlineData("idle")]       // 대소문자
    [InlineData("Idle,Done")]  // 조합
    [InlineData("99")]
    public void Faces_OnlyDefinedNames(string face) =>
        Assert.NotNull(New().SetFaces(new Dictionary<string, string?> { [face] = "잠" }, ["잠"]));

    [Fact]
    public void Faces_KeepSavedTag_WhenStickerListChanged()
    {
        var store = New();
        Assert.Null(store.SetFaces(new Dictionary<string, string?> { ["Idle"] = "옛태그" }, ["옛태그"]));
        Assert.Null(store.SetFaces(new Dictionary<string, string?> { ["Idle"] = "옛태그" }, ["대기"])); // 목록에서 빠져도 그대로 두기
        Assert.NotNull(store.SetFaces(new Dictionary<string, string?> { ["Done"] = "옛태그" }, ["대기"])); // 다른 상태에 새로 쓰기는 거절
    }

    [Fact]
    public void Shapes_RejectTrailingNewline()
    {
        // 입력은 Trim하므로 파일에서 읽은 값으로 본다 (앱 밖에서 고친 profiles.json)
        File.WriteAllText(Path.Combine(_dir, "profiles.json"),
            """{"profiles":{"user":{"name":"x","color":"#123456\n","image":"0123456789abcdef-01234567.png\n"}}}""");
        Assert.Equal(new Profile("x"), New().Get(ProfileStore.User));
        Assert.Null(New().ImagePath("0123456789abcdef-01234567.png\n"));
        Assert.Null(ProfileStore.Check(ProfileStore.User, "x", "#123456"));
        Assert.NotNull(ProfileStore.Check("agent:", "x", null));
    }

    [Fact]
    public async Task View_LetterByTextElement_NullKey_AndMissingImage()
    {
        var store = New();
        var view = new ProfileView(store);
        Assert.Equal("😀", view.Letter(null, "😀봇"));
        Assert.Equal("É", view.Letter(null, "éx")); // 결합 문자째

        // key 없음: 프로필을 찾지 않는다
        Assert.Null(store.Save(ProfileStore.User, "나", "#00ff00"));
        var html = view.Avatar(null, "봇", "봇").Value;
        Assert.DoesNotContain("background", html);
        Assert.Contains(">봇</span>", html);

        // 그림 파일이 지워졌으면 주소를 내지 않는다
        Assert.Null(await store.SetImageAsync(ProfileStore.User, new MemoryStream(Png), default));
        Assert.NotNull(view.ImageUrl(ProfileStore.User));
        File.Delete(store.ImagePath(store.Get(ProfileStore.User).Image)!);
        Assert.Null(view.ImageUrl(ProfileStore.User));
        Assert.DoesNotContain("<img", view.Avatar(ProfileStore.User, "나").Value);
    }

    [Fact]
    public void Theme_DefaultDiscord_ValidatesAndReloads()
    {
        // 5차 a: 기본 디코 다크, 셋 중 하나만, 다시 읽기 · 파일의 이상한 값은 기본으로
        var store = New();
        Assert.Equal(ProfileStore.ThemeDiscord, store.Theme());
        Assert.NotNull(store.SetTheme("dark"));
        Assert.NotNull(store.SetTheme(null));
        Assert.Null(store.SetTheme(ProfileStore.ThemeLight));
        Assert.Equal(ProfileStore.ThemeLight, New().Theme());
        File.WriteAllText(Path.Combine(_dir, "profiles.json"), """{"theme":"<b>"}""");
        Assert.Equal(ProfileStore.ThemeDiscord, New().Theme());
    }

    [Fact]
    public void NotifyBody_DefaultOff_SavesAndReloads_KeepsTheme()
    {
        // 데스크톱 알림 본문: 기본 끔, 저장 · 다시 읽기, 다른 설정(테마)과 같이 저장돼도 서로 지우지 않는다
        var store = New();
        Assert.False(store.NotifyBody());
        Assert.Null(store.SetTheme(ProfileStore.ThemeLight));
        Assert.Null(store.SetNotifyBody(true));
        var again = New();
        Assert.True(again.NotifyBody());
        Assert.Equal(ProfileStore.ThemeLight, again.Theme());
        Assert.Null(again.SetNotifyBody(false));
        Assert.False(New().NotifyBody());
    }

    [Fact]
    public void BrokenFile_IsNotOverwritten()
    {
        File.WriteAllText(Path.Combine(_dir, "profiles.json"), "{ broken");
        var store = New();
        Assert.NotNull(store.LoadError);
        Assert.Contains("읽지 못해", store.Save(ProfileStore.User, "x", null));
        Assert.Equal("{ broken", File.ReadAllText(Path.Combine(_dir, "profiles.json")));
    }

    [Fact]
    public void LoadedValues_AreCheckedAgain()
    {
        // 앱 밖에서 고친 값이 style · src에 그대로 들어가지 않게
        File.WriteAllText(Path.Combine(_dir, "profiles.json"),
            """{"profiles":{"user":{"name":"나2","color":"red;background:url(x)","image":"../x.png"},"hacker":{"name":"x"}},"faces":{"Idle":"잠","Nope":"x"}}""");
        var store = New();
        Assert.Equal(new Profile("나2"), store.Get(ProfileStore.User));
        Assert.Empty(store.Agents());
        Assert.Equal("잠", store.FaceTag(Face.Idle));
    }

    [Fact]
    public void View_EncodesAndUsesDefaults()
    {
        var store = New();
        var view = new ProfileView(store);
        Assert.Equal("나", view.UserName);
        Assert.Equal("지휘자", view.ConductorName);
        Assert.Contains(">나</span>", view.Avatar(ProfileStore.User, view.UserName).Value);
        Assert.Contains(">C</span>", view.Avatar(ProfileStore.Conductor, view.ConductorName).Value);

        Assert.Null(store.Save(ProfileStore.AgentKey("<b>bot</b>"), "<i>봇</i>", "#00ff00"));
        Assert.Equal("<i>봇</i>", view.AgentName("<b>bot</b>"));
        var html = view.Avatar(ProfileStore.AgentKey("<b>bot</b>")!, view.AgentName("<b>bot</b>"), "<b>bot</b>").Value;
        Assert.DoesNotContain("<b>", html);
        Assert.DoesNotContain("<i>", html);
        Assert.Contains("background:#00ff00", html);
        Assert.Contains("title=\"&lt;b&gt;bot&lt;/b&gt;\"", html);
    }

    [Fact]
    public async Task FaceCatalog_PrefersProfileFace_AndFallsBackToProfileImage()
    {
        var store = New();
        var stickers = new StickerCatalog(() => new StickerOptions());
        var faces = new FaceCatalog(stickers, Options.Create(new ChatOptions { Expressions = new() { ["Idle"] = "설정" } }), store);
        Assert.Equal("설정", faces.Tag(Face.Idle));
        Assert.Null(store.SetFaces(new Dictionary<string, string?> { ["Idle"] = "프로필" }, ["프로필"]));
        Assert.Equal("프로필", faces.Tag(Face.Idle));
        Assert.Equal("생각", faces.Tag(Face.Thinking)); // 기본

        Assert.Null(faces.Url(Face.Idle)); // 스티커 그림도 프로필 그림도 없음 → 글자
        Assert.Null(await store.SetImageAsync(ProfileStore.Conductor, new MemoryStream(Png), default));
        Assert.Equal("/profile-image/" + store.Get(ProfileStore.Conductor).Image, faces.Url(Face.Idle));
    }
}
