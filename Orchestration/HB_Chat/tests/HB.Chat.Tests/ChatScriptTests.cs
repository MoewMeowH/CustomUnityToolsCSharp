using System.Text.Json.Nodes;
using HB.Chat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HB.Chat.Tests;

/// <summary>
/// 화면 스크립트(chat.js) 시험: 실제 앱 + 실제 Chrome(없으면 Edge)으로 DOM 결과만 본다 — chat.js는 그대로.
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

    /// <summary>지휘자 답의 "# text" · "# send" 제목이 입력칸 · 보내기 단추 id를 가리지 않는다 — 보내기가 그대로 된다.</summary>
    [Fact]
    public async Task ConductorHeadings_DoNotShadowComposerIds()
    {
        if (!browser.Available) return;
        var room = app.Room("heading-ids", ChatApp.User("q", 0), ChatApp.Conductor("# text\n\n# send\n\n# send-form\n\n본문", 1));
        await using var page = await browser.OpenAsync(app.RoomUrl(room));
        Assert.Equal(1, await page.EvalAsync<int>("document.querySelectorAll('#text, #send, #send-form').length === 3 ? 1 : 0"));
        Assert.Equal(0, await page.EvalAsync<int>("document.querySelectorAll('#chat #text, #chat #send, #chat #send-form').length"));
        await page.SendAsync("say 1");
        await page.WaitForAsync(HasConductor("say-1"), "보낸 말의 답", ChatPage.TurnWait);
        page.AssertNoErrors();
    }

    /// <summary>두 말풍선에 같은 제목(id md-text)이 있으면 #앵커는 누른 말풍선 안의 제목으로 간다 — 위쪽 말풍선이 아니라.</summary>
    [Fact]
    public async Task AnchorLink_StaysInsideItsBubble()
    {
        if (!browser.Available) return;
        var room = app.Room("anchor-scope", ChatApp.User("q", 0), ChatApp.Conductor("# text\n\n위 말풍선", 1),
            ChatApp.User("q2", 2), ChatApp.Conductor("# text\n\n[제목으로](#text) [없는 곳](#nowhere)", 3));
        await using var page = await browser.OpenAsync(app.RoomUrl(room));
        Assert.Equal(2, await page.EvalAsync<int>("document.querySelectorAll('#chat [id=\"md-text\"]').length"));
        // 이동 대상만 기록한다 (실제 스크롤은 창 크기에 따라 다르다)
        await page.EvalAsync("window.__into = []; Element.prototype.scrollIntoView = function () { window.__into.push(this); }");
        await page.EvalAsync("document.querySelector('#chat a[href=\"#md-text\"]').click()");
        Assert.True(await page.EvalAsync<bool>(
            "window.__into.length === 1 && window.__into[0].closest('.msg') === document.querySelector('#chat a[href=\"#md-text\"]').closest('.msg')"));
        await page.EvalAsync("document.querySelector('#chat a[href=\"#md-nowhere\"]').click()");
        Assert.Equal(1, await page.EvalAsync<int>("window.__into.length")); // 그 말풍선에 없으면 움직이지 않는다
        Assert.Equal("", await page.EvalAsync<string>("location.hash"));
        page.AssertNoErrors();
    }

    /// <summary>곁칸 문서 탭: 목록 → 로그 본문 → 본문 속 계획서 링크(같은 칸) → 목록. 턴 끝 곁칸 다시 받기에도 본문이 남는다.</summary>
    [Fact]
    public async Task SideDocs_ListOpenFollowLinkBack()
    {
        if (!browser.Available) return;
        var docs = Path.Combine(app.Dir, "ws", "Proj", "docs");
        Directory.CreateDirectory(Path.Combine(docs, "plans"));
        Directory.CreateDirectory(Path.Combine(docs, "worklog"));
        File.WriteAllText(Path.Combine(docs, "plans", "2026-10-30_곁칸-문서.md"), "# 계획 본문\n\n[아래로](#side-data)\n\n## side-data\n");
        File.WriteAllText(Path.Combine(docs, "worklog", "2026-10-30_곁칸-문서.md"), "[계획서](../plans/2026-10-30_곁칸-문서.md)\n\n# 로그 본문\n");
        var room = app.Room("docs-tab", ChatApp.User("q", 0), ChatApp.Conductor("first", 1));
        await using var page = await browser.OpenAsync(app.RoomUrl(room));
        await page.EvalAsync("document.querySelector('#side-toggle').click(); document.querySelector('#side-tab-docs').click()");
        await page.WaitForAsync("!!document.querySelector('#docs-list a.docs-log')", "문서 목록", TimeSpan.FromSeconds(10));
        Assert.Contains("곁칸 문서", await page.EvalAsync<string>("document.querySelector('#docs-list').textContent"));

        await page.EvalAsync("document.querySelector('#docs-list a.docs-log').click()");
        await page.WaitForAsync("document.querySelector('#docs-body h1')?.textContent === '로그 본문'", "로그 본문", TimeSpan.FromSeconds(10));
        Assert.True(await page.EvalAsync<bool>("document.querySelector('#docs-list').hidden"));
        Assert.Equal("Proj/docs/worklog/2026-10-30_곁칸-문서.md", await page.EvalAsync<string>("document.querySelector('#docs-path').textContent"));

        await page.EvalAsync("document.querySelector('#docs-body a').click()");
        await page.WaitForAsync("document.querySelector('#docs-body h1')?.textContent === '계획 본문'", "링크로 연 계획서", TimeSpan.FromSeconds(10));
        Assert.EndsWith(app.RoomUrl(room).AbsolutePath, await page.EvalAsync<string>("location.pathname"));
        // 제목 id는 md- 앞붙이기(SafeMarkdown) — 앱의 #side-data를 가리지 않고, 본문 속 #앵커는 그 제목으로 간다 (주소 · 본문 그대로)
        Assert.Equal(0, await page.EvalAsync<int>("document.querySelectorAll('#docs-body #side-data').length"));
        Assert.Equal(1, await page.EvalAsync<int>("document.querySelectorAll('#docs-body #md-side-data').length"));
        await page.EvalAsync("document.querySelector('#docs-body a[href=\"#md-side-data\"]').click()");
        Assert.Equal("계획 본문", await page.EvalAsync<string>("document.querySelector('#docs-body h1').textContent"));

        // 턴이 끝나 곁칸(#side-data)을 다시 받아도 읽던 본문은 그대로 (256b92a166 C8)
        await page.SendAsync("say 1");
        await page.WaitForAsync(HasConductor("say-1"), "턴 끝", ChatPage.TurnWait);
        await page.WaitForAsync("!!document.querySelector('#side-data .side-sec')", "곁칸 다시 받기", TimeSpan.FromSeconds(10));
        Assert.False(await page.EvalAsync<bool>("document.querySelector('#docs-view').hidden"));
        Assert.Equal("계획 본문", await page.EvalAsync<string>("document.querySelector('#docs-body h1').textContent"));

        await page.EvalAsync("document.querySelector('#docs-back').click()");
        await page.WaitForAsync("!document.querySelector('#docs-list').hidden && document.querySelector('#docs-view').hidden", "목록으로", TimeSpan.FromSeconds(10));
        page.AssertNoErrors();
    }

    /// <summary>문서 탭을 열어 둔 채 턴이 끝나면 "이 방에서 다룬 문서"를 다시 받는다 (목록 화면일 때만).</summary>
    [Fact]
    public async Task SideDocs_TurnEnd_RefreshesRoomList()
    {
        if (!browser.Available) return;
        var plans = Path.Combine(app.Dir, "ws", "Turn", "docs", "plans");
        Directory.CreateDirectory(plans);
        var plan = Path.Combine(plans, "2026-10-30_턴-끝.md");
        File.WriteAllText(plan, "# 턴 끝");
        var room = app.Room("docs-turn", ChatApp.User("q", 0), ChatApp.Conductor("first", 1));
        await using var page = await browser.OpenAsync(app.RoomUrl(room));
        await page.EvalAsync("document.querySelector('#side-toggle').click(); document.querySelector('#side-tab-docs').click()");
        await page.WaitForAsync("(document.querySelector('#docs-list').textContent || '').includes('이 방에서 다룬 계획서 · 로그가 없습니다')", "빈 방 목록", TimeSpan.FromSeconds(10));

        // 이 턴에 지휘자가 그 계획서를 읽은 것으로 — 턴이 끝나며 붙는 줄 앞에 둔다
        File.AppendAllText(app.TranscriptPath(room.SessionId), ChatApp.Tool("Read", new JsonObject { ["file_path"] = plan }, 2) + "\n");
        await page.SendAsync("say 1");
        await page.WaitForAsync(HasConductor("say-1"), "턴 끝", ChatPage.TurnWait);
        await page.WaitForAsync(
            "[...document.querySelectorAll('#docs-list a[data-doc]')].some(a => a.dataset.doc.endsWith('2026-10-30_턴-끝.md') && !a.closest('details'))",
            "턴 끝에 방 목록 갱신", TimeSpan.FromSeconds(10));
        Assert.False(await page.EvalAsync<bool>("document.querySelector('#docs-list details.docs-folder').open")); // 방 목록이 생겨 접힘
        page.AssertNoErrors();
    }

    /// <summary>단톡방 칸: 구성원 칩 · @이름 넣기 · 부른 구성원 답 말풍선 · 방 목록에 자식 방 없음.</summary>
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

        // ③ @All: 둘 다 동시에 답한다 — 사용자 말 머리에 둘, 말풍선 A · B
        await page.SendAsync("@All 다들");
        await page.WaitForAsync("[...document.querySelectorAll('#chat .msg.group-member')].filter(m => m.querySelector('.body').textContent.trim() === 'Hello').length === 3",
            "@All 답 둘", ChatPage.TurnWait);
        Assert.Equal("→ @A, @B", await page.EvalAsync<string>("[...document.querySelectorAll('#chat .msg.user .detail')].pop().textContent.trim()"));
        Assert.Equal(["A", "B"], (await page.EvalAsync<string>("[...document.querySelectorAll('#chat .msg.group-member .who b')].slice(-2).map(b => b.textContent).sort().join(',')")).Split(','));
        Assert.Equal(0, await page.EvalAsync<int>("[...document.querySelectorAll('#chat .note.denied')].length")); // 답이 없다는 줄 없음
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
    /// 단톡방 ⑤: 명령 구성원의 승인 카드가 단톡방 화면에 구성원 이름으로 나오고, 거기서 허용하면 그 구성원이 이어서 답한다. 쓰는 구성원 칩에 단계 표시.
    /// 다시 열어도(snapshot) 기다리는 카드를 받는다.
    /// </summary>
    [Fact]
    public async Task Group_WriterApproval_CardInGroupPane()
    {
        if (!browser.Available) return;
        var (group, error) = app.Services.GetRequiredService<GroupChat>()
            .Create("ws", PermissionLevel.Read, "opus", null, "작업", [new GroupMemberSpec("A", "코드 작업", Level: PermissionLevel.Command), new GroupMemberSpec("B", "리뷰")]);
        Assert.Null(error);
        await using var page = await browser.OpenAsync(app.RoomUrl(group!));
        Assert.Equal("명령", await page.EvalAsync<string>("document.querySelector('.member[data-member] .member-level').textContent.trim()"));
        await page.SendAsync("approve @A");
        await page.WaitForAsync("!!document.querySelector('#live .approval')", "단톡방 승인 카드", ChatPage.TurnWait);
        Assert.Equal("A의 승인 요청 · Bash", await page.EvalAsync<string>("document.querySelector('#live .approval .head span').textContent"));
        Assert.Equal("git status", await page.EvalAsync<string>("document.querySelector('#live .approval .cmd').textContent"));
        Assert.True(await page.EvalAsync<bool>("[...document.querySelectorAll('#live .approval button')].some(b => b.textContent === '이 구성원에게 항상 허용')"));

        // 다시 열면 기다리는 카드를 서버에서 받는다 (GroupCards)
        await using var again = await browser.OpenAsync(app.RoomUrl(group!));
        await again.WaitForAsync("!!document.querySelector('#live .approval')", "다시 연 화면의 카드", ChatPage.TurnWait);

        await page.EvalAsync("[...document.querySelectorAll('#live .approval button')].find(b => b.textContent === '허용').click()");
        await page.WaitForAsync("[...document.querySelectorAll('#chat .msg.group-member .body')].some(b => b.textContent.trim() === 'allowed')", "A의 답", ChatPage.TurnWait);
        await page.WaitForAsync("!document.querySelector('#live .approval') && !document.querySelector('#live .live-line')", "카드 · 끝난 줄 정리", ChatPage.TurnWait);
        await again.WaitForAsync("!document.querySelector('#live .approval')", "다른 화면의 카드도 닫힘", ChatPage.TurnWait);
        page.AssertNoErrors();
        again.AssertNoErrors();
    }

    /// <summary>명령 카드의 "이 턴 동안 묻지 않기": 다음 명령 카드가 사라지고 켜짐 줄이 뜨며, 꼭 묻는 명령 카드에는 단추가 없고, 끄기로 줄이 닫힌다.</summary>
    [Fact]
    public async Task TurnAllow_CardButton_LineAndOff()
    {
        if (!browser.Available) return;
        var room = app.Services.GetRequiredService<RoomStore>().Create("ws", PermissionLevel.Command, "opus", null);
        await using var page = await browser.OpenAsync(app.RoomUrl(room));
        await page.SendAsync("approve3");
        await page.WaitForAsync("document.querySelector('#live .approval .cmd')?.textContent === 'echo one'", "첫 명령 카드", ChatPage.TurnWait);
        await page.EvalAsync("[...document.querySelectorAll('#live .approval button')].find(b => b.textContent === '이 턴 동안 묻지 않기').click()");
        await page.WaitForAsync("document.querySelector('#live .approval .cmd')?.textContent === 'git push'", "꼭 묻는 명령 카드", ChatPage.TurnWait);
        Assert.False(await page.EvalAsync<bool>("[...document.querySelectorAll('#live .approval button')].some(b => b.textContent === '이 턴 동안 묻지 않기')"));
        await page.WaitForAsync("document.querySelector('#live .bg-wait')?.textContent.includes('이 턴은 명령을 묻지 않습니다')", "켜짐 줄", ChatPage.TurnWait);
        // 허용한 카드는 거절 줄로 보이지 않는다
        Assert.True(await page.EvalAsync<bool>("[...document.querySelectorAll('#live .approval-line')].some(l => l.textContent.includes('이 턴 허용') && !l.classList.contains('denied'))"));
        await page.EvalAsync("[...document.querySelectorAll('#live .bg-wait button')].find(b => b.textContent === '끄기').click()");
        await page.WaitForAsync("!document.querySelector('#live .bg-wait')", "끄기로 줄 닫힘", ChatPage.TurnWait);
        await page.EvalAsync("[...document.querySelectorAll('#live .approval button')].find(b => b.textContent === '허용').click()");
        await page.WaitForAsync("!document.querySelector('#live .approval')", "카드 닫힘", ChatPage.TurnWait);
        page.AssertNoErrors();
    }

    /// <summary>단톡방 구성원 카드에서도 같은 단추 · 구성원 이름의 켜짐 줄 · 끄기 (자식 방으로 보냄).</summary>
    [Fact]
    public async Task Group_TurnAllow_MemberLineAndOff()
    {
        if (!browser.Available) return;
        var (group, error) = app.Services.GetRequiredService<GroupChat>()
            .Create("ws", PermissionLevel.Read, "opus", null, "작업", [new GroupMemberSpec("A", "코드 작업", Level: PermissionLevel.Command), new GroupMemberSpec("B", "리뷰")]);
        Assert.Null(error);
        await using var page = await browser.OpenAsync(app.RoomUrl(group!));
        await page.SendAsync("approve3 @A");
        await page.WaitForAsync("document.querySelector('#live .approval .cmd')?.textContent === 'echo one'", "첫 명령 카드", ChatPage.TurnWait);
        await page.EvalAsync("[...document.querySelectorAll('#live .approval button')].find(b => b.textContent === '이 턴 동안 묻지 않기').click()");
        await page.WaitForAsync("document.querySelector('#live .approval .cmd')?.textContent === 'git push'", "꼭 묻는 명령 카드", ChatPage.TurnWait);
        await page.WaitForAsync("document.querySelector('#live .bg-wait')?.textContent.includes('A: 이 턴은 명령을 묻지 않습니다')", "구성원 켜짐 줄", ChatPage.TurnWait);

        // 다시 열어도 켜짐 줄을 서버에서 받는다 (GroupCards)
        await using var again = await browser.OpenAsync(app.RoomUrl(group!));
        await again.WaitForAsync("!!document.querySelector('#live .bg-wait')", "다시 연 화면의 줄", ChatPage.TurnWait);

        await page.EvalAsync("[...document.querySelectorAll('#live .bg-wait button')].find(b => b.textContent === '끄기').click()");
        await page.WaitForAsync("!document.querySelector('#live .bg-wait')", "끄기로 줄 닫힘", ChatPage.TurnWait);
        await again.WaitForAsync("!document.querySelector('#live .bg-wait')", "다른 화면의 줄도 닫힘", ChatPage.TurnWait);
        await page.EvalAsync("[...document.querySelectorAll('#live .approval button')].find(b => b.textContent === '허용').click()");
        await page.WaitForAsync("[...document.querySelectorAll('#chat .msg.group-member .body')].some(b => b.textContent.trim() === 'allowed')", "A의 답", ChatPage.TurnWait);
        page.AssertNoErrors();
        again.AssertNoErrors();
    }

    /// <summary>
    /// 첨부 그림 칩을 누르면 새 창(데스크톱은 같은 탭으로 가서 닫을 길이 없었다 — 사용자 보고) 대신 화면 위 크게 보기 — ✕로 닫힌다. 그림이 아닌 첨부는 링크 그대로.
    /// </summary>
    [Fact]
    public async Task AttachmentImage_OpensOverlayWithClose()
    {
        if (!browser.Available) return;
        var room = app.Room("attach-view");
        var folder = Attachments.Folder(Path.Combine(app.Dir, "data"), room.Id);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "pic.png"), Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="));
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "x");
        var text = "보세요\n" + Attachments.Marker + Path.Combine(folder, "pic.png") + "\n" + Attachments.Marker + Path.Combine(folder, "notes.txt");
        File.AppendAllText(app.TranscriptPath(room.SessionId), ChatApp.User(text, 0) + "\n" + ChatApp.Conductor("봤어요", 1) + "\n");
        await using var page = await browser.OpenAsync(app.RoomUrl(room));
        await page.WaitForAsync("!!document.querySelector('#chat a.attach img')", "첨부 그림 칩", ChatPage.TurnWait);
        var before = await page.EvalAsync<string>("location.href");
        await page.EvalAsync("document.querySelector('#chat a.attach img').click()");
        await page.WaitForAsync("!!document.querySelector('.shot-view img')", "크게 보기", ChatPage.TurnWait);
        Assert.Equal(before, await page.EvalAsync<string>("location.href")); // 화면을 떠나지 않음
        Assert.Contains("pic.png", await page.EvalAsync<string>("document.querySelector('.shot-view img').alt"));
        await page.EvalAsync("document.querySelector('.shot-view .shot-x').click()");
        await page.WaitForAsync("!document.querySelector('.shot-view')", "✕로 닫힘", ChatPage.TurnWait);
        // 그림이 아닌 첨부는 링크 그대로 (크게 보기 없음)
        Assert.Equal(1, await page.EvalAsync<int>("[...document.querySelectorAll('#chat a.attach')].filter(a => !a.querySelector('img') && a.target === '_blank').length"));

        // 곁칸 첨부 탭의 그림 줄도 같은 크게 보기 — Esc로 닫힌다 (사용자 보고: "첨부에서 이미지 클릭하면 x 버튼이 없엉" — 곁칸 첨부 탭)
        await page.EvalAsync("document.querySelector('#side-toggle').click(); document.querySelector('#side-tab-files').click()");
        await page.WaitForAsync("!!document.querySelector('#side-sec-files a.attach-image')", "곁칸 첨부 그림 줄", ChatPage.TurnWait);
        Assert.Equal(1, await page.EvalAsync<int>("document.querySelectorAll('#side-sec-files a.attach-image').length")); // notes.txt는 링크 그대로
        await page.EvalAsync("document.querySelector('#side-sec-files a.attach-image').click()");
        await page.WaitForAsync("!!document.querySelector('.shot-view .shot-x')", "곁칸에서 연 크게 보기 · ✕", ChatPage.TurnWait);
        Assert.Equal(before, await page.EvalAsync<string>("location.href"));
        await page.EvalAsync("document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))");
        await page.WaitForAsync("!document.querySelector('.shot-view')", "Esc로 닫힘", ChatPage.TurnWait);
        page.AssertNoErrors();
    }

    /// <summary>명령 방의 작업 폴더 밖 쓰기 카드: 경로 · 안내 줄 · "쓸 내용" 미리보기, "항상 허용" 단추 없음, 허용 단추가 있다(잘려도 허용). 거절하면 카드가 닫힌다.</summary>
    [Fact]
    public async Task OutsideWriteCard_ShowsPathPreviewNoAlways()
    {
        if (!browser.Available) return;
        var room = app.Store.Create("ws", PermissionLevel.Command, "opus", "밖 쓰기");
        // 시험 앱 폴더 밖 — 그 폴더는 세션 기록 폴더(projects)의 부모라 "Claude 설정 폴더"로 막힌다. 가짜 claude는 실제로 쓰지 않는다
        var path = Path.Combine(Path.GetTempPath(), "hbchat-outside-" + Guid.NewGuid().ToString("N")[..8], "note.txt");
        await using var page = await browser.OpenAsync(app.RoomUrl(room));
        await page.SendAsync("writeto " + path);
        await page.WaitForAsync("!!document.querySelector('#live .approval')", "쓰기 카드", ChatPage.TurnWait);
        Assert.Equal(path, await page.EvalAsync<string>("document.querySelector('#live .approval .cmd').textContent"));
        Assert.Contains("작업 폴더 밖 파일 쓰기", await page.EvalAsync<string>("document.querySelector('#live .approval .warn-line').textContent"));
        Assert.StartsWith("쓸 내용: content=", await page.EvalAsync<string>("document.querySelectorAll('#live .approval .cmd')[1].textContent"));
        var buttons = await page.EvalAsync<string>("[...document.querySelectorAll('#live .approval button')].map(b => b.textContent).join('|')");
        Assert.Contains("허용", buttons);
        Assert.DoesNotContain("항상 허용", buttons);
        await page.EvalAsync("[...document.querySelectorAll('#live .approval button')].find(b => b.textContent === '거절').click()");
        await page.WaitForAsync("!document.querySelector('#live .approval')", "카드 닫힘", ChatPage.TurnWait);
        page.AssertNoErrors();
    }

    /// <summary>
    /// 단톡방 답 실시간 표시: 부른 구성원의 글 조각 · 도구 칩이 진행 칸 임시 말풍선에 흐르고, 새로 연 화면(다시 붙음)도 쌓인 글을 받는다.
    /// 끝나면 임시 말풍선이 사라지고 대화 줄 말풍선 하나만 남는다. 가짜 claude 'stream'은 go 파일을 기다린다.
    /// </summary>
    [Fact]
    public async Task Group_LiveText_StreamsThenAnswer()
    {
        if (!browser.Available) return;
        var (group, error) = app.Services.GetRequiredService<GroupChat>()
            .Create("ws", PermissionLevel.Read, "opus", null, "흐름", [("A", ""), ("B", "")]);
        Assert.Null(error);
        var a = group!.Members[0];
        const string streaming = "#live .msg.group-member.streaming";
        await using var page = await browser.OpenAsync(app.RoomUrl(group));
        await page.SendAsync("stream @A");
        await page.WaitForAsync($"document.querySelector('{streaming} .body')?.textContent === 'Hello'", "A의 흐르는 글", ChatPage.TurnWait);
        Assert.Equal(a.Id, await page.EvalAsync<string>($"document.querySelector('{streaming}').dataset.member"));
        Assert.StartsWith("Read", await page.EvalAsync<string>($"document.querySelector('{streaming} .chips .chip').textContent"));
        Assert.Equal(1, await page.EvalAsync<int>($"document.querySelectorAll('{streaming}').length")); // 부르지 않은 B는 없음

        await using var again = await browser.OpenAsync(app.RoomUrl(group));
        await again.WaitForAsync($"document.querySelector('{streaming} .body')?.textContent === 'Hello'", "다시 연 화면의 쌓인 글", ChatPage.TurnWait);

        Directory.CreateDirectory(Path.Combine(app.Dir, "tasks"));
        File.WriteAllText(Path.Combine(app.Dir, "tasks", "go-" + app.Store.Get(a.RoomId)!.SessionId), "");
        foreach (var p in new[] { page, again })
        {
            await p.WaitForAsync("[...document.querySelectorAll('#chat .msg.group-member .body')].some(b => b.textContent.trim() === 'Hello world')", "A의 답", ChatPage.TurnWait);
            await p.WaitForAsync($"!document.querySelector('{streaming}')", "임시 말풍선 지움", ChatPage.TurnWait);
            p.AssertNoErrors();
        }
    }

    /// <summary>
    /// 단톡방 만들기: Codex 모델 · 강도 칸은 엔진이 Codex일 때만 보인다 (단계 칸 잠금 · 되돌림은 그대로). 구성원 작업 폴더 칸 값이 그 구성원 자식 방 폴더가 되고,
    /// 단톡방 머리에 "작업 폴더 2곳" · 다른 폴더 구성원 칩에 폴더 이름.
    /// </summary>
    [Fact]
    public async Task GroupCreate_CodexFieldsOnlyForCodex_MemberWorkspace()
    {
        if (!browser.Available) return;
        await using var page = await browser.OpenAsync(new Uri(app.BaseAddress, "/?new=1"), ready: "form.group-create");
        const string row = "document.querySelectorAll('form.group-create .group-member-row')[1]";
        static string Codex(string row) => $"[...{row}.querySelectorAll('select[name=codexModels], select[name=codexEfforts]')].map(s => s.hidden ? 'h' : 'v').join('')";
        static string SetEngine(string row, string engine) =>
            $"(() => {{ const e = {row}.querySelector('select[name=engines]'); e.value = '{engine}'; e.dispatchEvent(new Event('change', {{ bubbles: true }})); }})()";
        Assert.Equal("hh", await page.EvalAsync<string>(Codex(row)));
        await page.EvalAsync($"{row}.querySelector('select[name=levels]').value = 'Edit'");
        await page.EvalAsync(SetEngine(row, "codex"));
        Assert.Equal("vv", await page.EvalAsync<string>(Codex(row)));
        Assert.Equal("Read", await page.EvalAsync<string>($"{row}.querySelector('select[name=levels]').value"));
        Assert.Equal("hh", await page.EvalAsync<string>(Codex("document.querySelectorAll('form.group-create .group-member-row')[0]"))); // 다른 줄은 그대로
        await page.EvalAsync(SetEngine(row, "claude"));
        Assert.Equal("hh", await page.EvalAsync<string>(Codex(row)));
        Assert.Equal("Edit", await page.EvalAsync<string>($"{row}.querySelector('select[name=levels]').value"));

        await page.EvalAsync("(() => { const f = document.querySelector('form.group-create'); f.querySelector('select[name=workspace]').value = 'ws'; " +
                             "f.querySelector('input[name=title]').value = '두 폴더'; const n = f.querySelectorAll('input[name=names]'); n[0].value = 'A'; n[1].value = 'B'; " +
                             "f.querySelectorAll('select[name=memberWorkspaces]')[1].value = 'ToolWithCS'; f.requestSubmit(); })()");
        await page.WaitForAsync("location.pathname.startsWith('/Room/') && !!document.querySelector('.room.group')", "만든 단톡방", ChatPage.TurnWait);
        var group = app.Store.All().Single(r => r.IsGroup && r.Title == "두 폴더");
        Assert.Equal(("ws", "ws", "ToolWithCS"), (group.Workspace, app.Store.Get(group.Members[0].RoomId)!.Workspace, app.Store.Get(group.Members[1].RoomId)!.Workspace));
        Assert.Contains("작업 폴더 2곳", await page.EvalAsync<string>("document.querySelector('.room-head .head-title .muted').textContent"));
        Assert.Equal("ToolWithCS", await page.EvalAsync<string>("[...document.querySelectorAll('.member .member-ws')].map(b => b.textContent.trim()).join(',')"));
        page.AssertNoErrors();

        // 구성원이 모두 단톡방 폴더가 아닌 한 폴더면 머리는 그 폴더 — 칩 배지와 맞게 (/self-review 128de5a971 C1)
        var (moved, error) = app.Services.GetRequiredService<GroupChat>().Create("ws", PermissionLevel.Read, "opus", null, "옮김",
            [new GroupMemberSpec("A", "", Workspace: "ToolWithCS"), new GroupMemberSpec("B", "", Workspace: "ToolWithCS")]);
        Assert.Null(error);
        await using var other = await browser.OpenAsync(app.RoomUrl(moved!));
        Assert.EndsWith("· ToolWithCS", (await other.EvalAsync<string>("document.querySelector('.room-head .head-title .muted').textContent")).Trim());
        other.AssertNoErrors();
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
