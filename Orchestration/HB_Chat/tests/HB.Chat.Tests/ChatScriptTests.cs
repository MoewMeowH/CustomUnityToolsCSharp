using HB.Chat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HB.Chat.Tests;

/// <summary>
/// 화면 스크립트(chat.js) 시험 (계획서 2026-10-29_HB_Chat-화면-스크립트-시험-틀): 실제 앱 + 실제 Chrome(없으면 Edge)으로 DOM 결과만 본다 — chat.js는 그대로.
/// 위젯 대상 말풍선 · 20만 자 상한 · 높이 자르기 · 보낸 창 확인 (f 리뷰 803e5f13eb R7), 턴 끝 새 말 받기(이어 붙이기 · 쪽 바꾸기 · 기록이 줄면 전체),
/// 맨 뒤 줄(run-trailing) 바꿔 끼우기 (/self-review a9e6066519 C1). 모든 시험은 끝에 페이지 오류(예외 · console.error · 오류 로그)가 없어야 한다.
/// 브라우저가 없는 PC에서는 건너뛴다(통과로 보인다).
/// </summary>
public sealed class ChatScriptTests(ChatApp app, ChatBrowser browser) : IClassFixture<ChatApp>, IClassFixture<ChatBrowser>
{
    /// <summary>지휘자 말풍선 본문이 그 글인 것이 있는지 (#chat 안만 — 진행 칸 말풍선은 빼고).</summary>
    private static string HasConductor(string text) =>
        $"[...document.querySelectorAll('#chat .msg.conductor .body')].some(b => b.textContent.trim() === '{text}')";

    private static string Widget(string html) => "```widget\n" + html + "\n```";

    /// <summary>단톡방 칸 (계획서 2026-10-29_HB_Chat-단톡방): 구성원 칩 · @이름 넣기 · 부른 구성원 답 말풍선 · 방 목록에 자식 방 없음.</summary>
    [Fact]
    public async Task Group_CallMember_ShowsAnswerBubble()
    {
        if (!browser.Available) return;
        var (group, error) = app.Services.GetRequiredService<GroupChat>()
            .Create("ws", PermissionLevel.Read, "opus", null, "팀", [("A", "위험을 찾는다"), ("B", "A에 반박한다")]);
        Assert.Null(error);
        await using var page = await browser.OpenAsync(app.RoomUrl(group!));
        Assert.Equal(2, await page.EvalAsync<int>("document.querySelectorAll('.room.group .member').length"));
        foreach (var m in group!.Members)
            Assert.Equal(0, await page.EvalAsync<int>($"document.querySelectorAll('.sidebar a[href=\"/Room/{m.RoomId}\"]').length"));
        Assert.Equal(1, await page.EvalAsync<int>($"document.querySelectorAll('.sidebar a[href=\"/Room/{group.Id}\"]').length"));

        await page.EvalAsync("document.querySelector('.member-call[data-name=\"A\"]').click()");
        Assert.StartsWith("@A", await page.EvalAsync<string>("document.querySelector('#text').value"));
        await page.SendAsync("@A 안녕"); // 가짜 claude 기본 모드 → "Hello"
        await page.WaitForAsync("[...document.querySelectorAll('#chat .msg.group-member .body')].some(b => b.textContent.trim() === 'Hello')", "A의 답", ChatPage.TurnWait);
        Assert.Contains("@A", await page.EvalAsync<string>("document.querySelector('#chat .msg.user .detail').textContent"));
        Assert.Equal("", await page.EvalAsync<string>("document.querySelector('#text').value"));
        page.AssertNoErrors();

        // 과정 보기(자식 방): 안내 줄 · 입력칸 숨김, 단계 바꾸기는 서버가 거절 (b8697252eb C1 · Codex 리뷰)
        await using var child = await browser.OpenAsync(app.RoomUrl(app.Store.Get(group.Members[0].RoomId)!));
        Assert.True(await child.EvalAsync<bool>("!!document.querySelector('#group-child-banner') && document.querySelector('.composer').hidden"));
        var refused = await child.EvalAsync<string>(
            "(() => { const d = new FormData(); d.append('__RequestVerificationToken', document.querySelector('input[name=__RequestVerificationToken]').value); " +
            "d.append('model', 'opus'); d.append('level', 'Command'); d.append('expectedLevel', 'Read'); " +
            "return fetch(location.pathname + '?handler=Settings', { method: 'POST', body: d }).then(async r => r.status + ' ' + await r.text()); })()");
        Assert.StartsWith("400", refused);
        Assert.Contains("구성원 방", refused);
        Assert.Equal(PermissionLevel.Read, app.Store.Get(group.Members[0].RoomId)!.Level);
        Assert.All(child.Errors, e => Assert.Contains("handler=Settings", e)); // 일부러 낸 400 말고는 오류 없음
    }

    /// <summary>
    /// 좁은 창(미니 창 등)에서도 보통 슬라이드로 그려진다 (/self-review 832f22f583 C1). 고치기 전 실측(2026-10-29): 틀 406 · 306px에서 reveal이
    /// 스크롤 보기로 바뀌어(scrollActivationWidth 기본 435) sessionStorage SecurityError, loading-scroll-mode 클래스가 남고 화살표 · 쪽 번호 없는
    /// 세로 보기가 됐다(빈 틀은 아님) → scrollActivationWidth:null. 틀 안 예외는 다른 대상(OOPIF)이라 이 탭의 Errors에 잡히지 않아 틀 안 상태로 본다.
    /// </summary>
    [Theory]
    [InlineData(1280)]
    [InlineData(480)]
    [InlineData(380)]
    public async Task Slides_RenderAtWindowWidth(int width)
    {
        if (!browser.Available) return;
        var room = app.Room("slides-" + width,
            ChatApp.Conductor("```slides\n{\"title\":\"Deck\",\"slides\":[{\"title\":\"One\",\"body\":\"hello\"},{\"title\":\"Two\",\"body\":\"world\"}]}\n```", 0));
        await using var page = await browser.OpenAsync(app.RoomUrl(room), width, 800);
        await page.WaitForAsync("!!document.querySelector('iframe.slides-frame')", "슬라이드 틀");
        await page.EvalAsync("document.querySelector('iframe.slides-frame').scrollIntoView()"); // loading=lazy
        var frameWidth = await page.EvalAsync<double>("document.querySelector('iframe.slides-frame').getBoundingClientRect().width");

        const string State = "JSON.stringify({ ready: !!document.querySelector('.reveal.ready'), html: document.documentElement.className, " +
                             "reveal: document.querySelector('.reveal')?.className, w: innerWidth, " +
                             "shown: document.querySelector('.reveal .slides section.present')?.textContent.trim() || null })";
        string? state = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            state = (await page.EvalInFrameAsync(State))?.ToString();
            if (state?.Contains("\"ready\":true") == true) break;
            await Task.Delay(100);
        }
        var message = $"창 {width}px · 틀 {frameWidth}px: {state}\n오류: {string.Join(" | ", page.Errors)}\n틀: {page.LastFrameTree?[..Math.Min(1500, page.LastFrameTree.Length)]}";
        Assert.True(state?.Contains("\"ready\":true") == true && state.Contains("Deck"), message);
        // 보통 슬라이드 보기 — 스크롤 보기 · 로딩 클래스가 없고, 쪽 번호가 보인다
        Assert.DoesNotContain("reveal-scroll", state);
        Assert.DoesNotContain("loading-scroll-mode", state);
        Assert.Equal("visible", (string?)await page.EvalInFrameAsync("getComputedStyle(document.querySelector('.reveal-viewport')).visibility"));
        Assert.True((bool)(await page.EvalInFrameAsync("!!document.querySelector('.reveal .slide-number')?.offsetWidth"))!, message);
        page.AssertNoErrors();
    }

    [Fact]
    public async Task Widget_OnlyConductorBubbles_OversizeStaysCode()
    {
        if (!browser.Available) return;
        var room = app.Room("widget-scope",
            ChatApp.User(Widget("<b>from user</b>"), 0),
            ChatApp.Conductor("ok\n\n" + Widget("<b>small</b>"), 1),
            ChatApp.Conductor(Widget("<i>" + new string('a', 200_000) + "</i>"), 2),
            // 경계: 코드 글이 정확히 상한(200,000자 — 끝 줄바꿈 포함)이면 아직 틀 (c8752a0b82 C10)
            ChatApp.Conductor(Widget(new string('b', 199_999)), 3));
        await using var page = await browser.OpenAsync(app.RoomUrl(room));

        Assert.False(await page.EvalAsync<bool>("!!document.querySelector('#chat .msg.user .widget, #chat .msg.user iframe')"));
        var conductors = "document.querySelectorAll('#chat .msg.conductor')";
        // 보통 위젯: 틀 + 실행 단추, 스크립트는 꺼진 채 (sandbox 빈 값)
        Assert.Equal("", await page.EvalAsync<string>($"{conductors}[0].querySelector('.widget > iframe.widget-frame').getAttribute('sandbox')"));
        Assert.True(await page.EvalAsync<bool>($"!!{conductors}[0].querySelector('.widget .widget-run')"));
        // 20만 자 넘음: 안내 + 코드, 틀 없음
        Assert.False(await page.EvalAsync<bool>($"!!{conductors}[1].querySelector('iframe')"));
        Assert.Contains("코드로만", await page.EvalAsync<string>($"{conductors}[1].querySelector('.widget .widget-note').dataset.note"));
        Assert.True(await page.EvalAsync<bool>($"!!{conductors}[1].querySelector('.widget pre > code.language-widget')"));
        Assert.Equal(200_000, await page.EvalAsync<int>($"{conductors}[2].querySelector('.widget code.language-widget').textContent.length"));
        Assert.True(await page.EvalAsync<bool>($"!!{conductors}[2].querySelector('.widget > iframe.widget-frame')"));
        page.AssertNoErrors();
    }

    [Fact]
    public async Task Widget_Run_HeightClampedToRange_OnlyFromOwnFrame()
    {
        if (!browser.Available) return;
        // body 안쪽 여백 8px이 위아래로 붙는다: 300 → 316, 0 → 16(최소 40), 3000 → 3016(최대 1200)
        var room = app.Room("widget-height",
            ChatApp.Conductor(Widget("<div style=\"height:3000px\"></div>"), 0),
            ChatApp.Conductor(Widget("<div style=\"height:0\"></div>"), 1),
            ChatApp.Conductor(Widget("<div style=\"height:300px\"></div>"), 2));
        await using var page = await browser.OpenAsync(app.RoomUrl(room));
        const string Boxes = "document.querySelectorAll('#chat .msg.conductor .widget')";
        string Frame(int i) => $"{Boxes}[{i}].querySelector('iframe.widget-frame')";

        async Task Run(int i, string height)
        {
            // 틀은 loading=lazy — 보이는 자리로 옮겨야 불러온다
            await page.EvalAsync($"(() => {{ const b = {Boxes}[{i}]; b.scrollIntoView(); b.querySelector('.widget-run').click(); b.scrollIntoView(); }})()");
            Assert.Equal("allow-scripts", await page.EvalAsync<string>($"{Frame(i)}.getAttribute('sandbox')"));
            await page.WaitForAsync($"{Frame(i)}.style.height === '{height}'", $"위젯 {i} 높이 {height}");
        }

        await Run(0, "1200px");
        Assert.Equal("", await page.EvalAsync<string>($"{Frame(1)}.style.height")); // 다른 틀의 높이는 그대로
        await Run(1, "40px");
        await Run(2, "316px");
        // 틀이 아닌 창(이 페이지)이 보낸 높이 알림은 무시 — 메시지는 보낸 차례로 오므로, 뒤에 보낸 확인용 메시지가 오면 앞 것은 처리가 끝났다 (지휘자 K4)
        await page.EvalAsync("new Promise(r => { addEventListener('message', function done(e) { if (e.data && e.data.hbTestDone) { removeEventListener('message', done); r(); } }); " +
                             "postMessage({ hbWidgetHeight: 77 }, '*'); postMessage({ hbTestDone: true }, '*'); })");
        Assert.Equal(["1200px", "40px", "316px"],
            (await page.EvalAsync($"[...{Boxes}].map(b => b.querySelector('iframe').style.height)"))!.AsArray().Select(n => (string)n!).ToArray());
        page.AssertNoErrors();
    }

    [Fact]
    public async Task TurnEnd_AppendsOnlyNewLines()
    {
        if (!browser.Available) return;
        var room = app.Room("append", ChatApp.User("q", 0), ChatApp.Conductor("first", 1));
        await using var page = await browser.OpenAsync(app.RoomUrl(room));
        await page.EvalAsync("document.querySelector('#chat').dataset.mark = 'kept'; document.querySelector('#chat .msg.conductor').dataset.mark = 'kept'");

        await page.SendAsync("say 3");
        await page.WaitForAsync(HasConductor("say-3"), "새 말 say-3", ChatPage.TurnWait);

        // 쪽을 다시 그리지 않고 뒤에만 붙였다 — 앞 말풍선 DOM이 그대로, 같은 말이 두 번 없음
        Assert.True(await page.EvalAsync<bool>("!!document.querySelector('#chat[data-mark=kept] .msg.conductor[data-mark=kept]')"));
        Assert.Equal(new[] { "first", "say-1", "say-2", "say-3" },
            (await page.EvalAsync("[...document.querySelectorAll('#chat .msg.conductor .body')].map(b => b.textContent.trim())"))!.AsArray().Select(n => (string)n!).ToArray());
        Assert.Equal(2, await page.EvalAsync<int>("document.querySelectorAll('#chat .msg.user').length"));
        page.AssertNoErrors();
    }

    [Fact]
    public async Task TurnEnd_MoreThanAPage_SwapsToLastPage()
    {
        if (!browser.Available) return;
        var room = app.Room("reset", ChatApp.User("q", 0), ChatApp.Conductor("first", 1));
        await using var page = await browser.OpenAsync(app.RoomUrl(room));
        await page.EvalAsync("document.querySelector('#chat').dataset.mark = 'old'");

        await page.SendAsync("say 60"); // 새 말 61개 > 한 쪽 50개
        await page.WaitForAsync(HasConductor("say-60"), "새 말 say-60", ChatPage.TurnWait);

        Assert.False(await page.EvalAsync<bool>("!!document.querySelector('#chat[data-mark=old]')")); // 붙이지 않고 쪽을 바꿈
        Assert.Equal(RoomList.PageSize, await page.EvalAsync<int>("document.querySelectorAll('#chat .msg.conductor').length"));
        Assert.False(await page.EvalAsync<bool>(HasConductor("first")));
        Assert.Equal("1", await page.EvalAsync<string>("document.querySelector('#chat').dataset.hasMore"));
        page.AssertNoErrors();
    }

    [Fact]
    public async Task TurnEnd_ShorterRecord_ReloadsWholePage()
    {
        if (!browser.Available) return;
        var room = app.Room("shrink", ChatApp.User("q", 0), ChatApp.Conductor("old-1", 1), ChatApp.User("q2", 2), ChatApp.Conductor("old-2", 3));
        await using var page = await browser.OpenAsync(app.RoomUrl(room));
        await page.EvalAsync("document.querySelector('#chat').dataset.mark = 'old'");

        await page.SendAsync("shrink"); // 기록이 첫 줄 + 새 두 줄로 다시 쓰임 (마지막 줄 번호가 줄어듦)
        await page.WaitForAsync(HasConductor("shrunk"), "다시 쓴 기록", ChatPage.TurnWait);

        Assert.False(await page.EvalAsync<bool>("!!document.querySelector('#chat[data-mark=old]')"));
        Assert.False(await page.EvalAsync<bool>(HasConductor("old-1") + " || " + HasConductor("old-2")));
        Assert.Equal(1, await page.EvalAsync<int>("document.querySelectorAll('#chat .msg.conductor').length"));
        page.AssertNoErrors();
    }

    [Fact]
    public async Task TurnEnd_TrailingLine_ReplacedNotDuplicated()
    {
        if (!browser.Available) return;
        var room = app.Room("trailing", ChatApp.User("q", 0), ChatApp.Conductor("a", 1));
        // 마지막 말보다 늦은 방 기록 줄 — 맨 뒤(run-trailing)에 그려진다
        new ApprovalLog(() => app.Services.GetRequiredService<IOptions<ChatOptions>>().Value.DataFolder).Note(room.Id, RoomNotes.BackgroundTool, "trailing-note");
        await using var page = await browser.OpenAsync(app.RoomUrl(room));
        Assert.Equal(1, await page.EvalAsync<int>("document.querySelectorAll('#chat .system.note.run-trailing').length"));

        await page.SendAsync("say 1");
        await page.WaitForAsync(HasConductor("say-1"), "새 말 say-1", ChatPage.TurnWait);

        // 새 말 조각이 그 줄을 다음 말 앞 제자리로 다시 보낸다 — 앞서 그린 맨 뒤 줄은 지워져 하나만
        Assert.Equal(1, await page.EvalAsync<int>("document.querySelectorAll('#chat .system.note').length"));
        Assert.Equal(0, await page.EvalAsync<int>("document.querySelectorAll('#chat .run-trailing').length"));
        Assert.True(await page.EvalAsync<bool>(
            "(() => { const n = document.querySelector('#chat .system.note'); const m = [...document.querySelectorAll('#chat .msg.conductor')].at(-1); " +
            "return n.textContent.includes('trailing-note') && !!(n.compareDocumentPosition(m) & Node.DOCUMENT_POSITION_FOLLOWING); })()"));
        page.AssertNoErrors();
    }
}
