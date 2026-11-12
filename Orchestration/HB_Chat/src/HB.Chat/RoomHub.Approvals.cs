namespace HB.Chat;

/// <summary>승인 · 질문 카드: 도구 호출 판정, 카드 답, 미리 허용된 명령 규칙.</summary>
public sealed partial class RoomHub
{
    /// <summary>
    /// 실행 중인 claude가 묻는 승인 하나 (stdio 호스트 — <see cref="ClaudeRunner"/>의 onPermission, 3차 a). 답(<see cref="PermissionDecision"/> JSON)을 돌려준다.
    /// 명령 방의 명령 도구 → 승인 카드, AskUserQuestion → 질문 카드, 그 밖은 카드 없이 거절 (그 전 --permission-prompts none과 같은 동작).
    /// 그 실행(세대)이 이미 끝났거나 중지됐으면 거절.
    /// </summary>
    /// <param name="attached">이 실행에 붙인 방별 서버 (더한 .mcp.json 서버 · unity) — 미리 허용하지 않고 여기서 받는다.</param>
    /// <param name="unity">이 실행에 붙인 Unity 프로젝트 이름.</param>
    /// <param name="workspace">작업 폴더 — run_mode 모델 선택 카드가 모드 목록을 읽는다 (<see cref="ModeCatalog"/>).</param>
    /// <param name="askWrites">이 실행을 앱 단계 "폴더 밖 쓰기 묻기"(ask 규칙)로 띄웠는지 — 실행 중에 설정을 바꿔도 띄운 값으로 가른다.</param>
    private async Task<string> DecideToolAsync(Room room, Live live, int generation, ToolPermission ask, CancellationToken aborted,
        IReadOnlyCollection<string>? attached = null, string? unity = null, string? workspace = null, bool askWrites = false)
    {
        // 놀이터 구성원: 사람 없이 도는 판 — 웹 검색만 받고 나머지 승인 요청(폴더 밖 쓰기 등)은 카드 없이 거절 (설계 검토 b420efd860 R3).
        // 부모를 못 찾아도 놀이터로 본다(일반 권한으로 돌아가지 않게)
        if (GroupChat.IsPlaygroundMember(store, room))
        {
            if (ask.Tool == ClaudeArgs.WebSearchTool) return PermissionDecision.Allow(ask.InputJson);
            var asked = ApprovalRequest.Parse(ask.RequestJson)?.Summary ?? "";
            HostDenied(room, live, generation, ask.Tool, asked, "놀이터", $"{ask.Tool}{(asked.Length > 0 ? " " + (asked.Length > 120 ? asked[..120] + "…" : asked) : "")}: 놀이터에서는 판 폴더 밖 쓰기 · 그 밖의 요청을 받지 않아 거절");
            return PermissionDecision.Deny("놀이터에서는 판 폴더(지금 작업 폴더) 안 편집과 웹 검색만 됩니다 — 이 요청은 거절됐습니다. 다시 시도하지 말고 판 폴더 안에서 다른 방법으로 하세요.");
        }
        if (attached is { Count: > 0 } && McpServerOf(ask.Tool) is { } server && attached.Contains(server))
            return await DecideRoomServerAsync(room, live, generation, ask, aborted, server, unity);
        // 계획 승인 (Claude 앱 기능 C): 계획 모드로 돈 명령 방의 ExitPlanMode → 계획 카드. 허용이면 그 방 단계의 원래 모드로 돌려
        // 같은 턴에서 진행하고 "계획 먼저"를 끈다. 거절이면 이유가 지휘자에게 가서 계획을 고쳐 다시 묻는다 (결정 "이유 적어 다시 계획").
        // 계획 모드로 시작한 실행(시작 때 "계획 먼저")에서만 — 그 밖에서 온 ExitPlanMode는 카드 없이 거절 (지휘자 검토 S1, 겹 방어)
        if (ask.Tool == ApprovalRequest.PlanTool && room.PlanNext && ClaudeArgs.PlanMode(room.Level) is not null)
        {
            if (ApprovalRequest.Parse(ask.RequestJson) is not { IsPlan: true } plan) return PermissionDecision.Deny("계획 승인 요청 모양을 읽지 못해 거절했습니다.");
            var answer = await RequestApprovalAsync(room.Id, live, generation, plan, aborted);
            if (answer is { Allow: true })
            {
                // 허용 직전에 지금 단계를 다시 본다 — 카드에 답한 뒤 단계를 내렸으면 거절, 돌아갈 모드도 지금 단계 기준
                // 단계를 바꿨으면 거절 — 명령 인자로 띄운 실행을 앱 단계로 올리면 ask 규칙 없이 auto가 된다
                if (store.Get(room.Id) is not { } now || now.Level != room.Level || ClaudeArgs.PlanMode(now.Level) is not { } mode)
                    return PermissionDecision.Deny("사용자가 이 방 권한을 바꿨습니다 — 계획대로 진행하지 않습니다.");
                try
                {
                    store.Update(room.Id, r => r with { PlanNext = false });
                    Touch(room.Id); // 화면의 "계획 먼저" 표시가 꺼지게
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    // 못 꺼도 이번 승인은 그대로 — 다음 말이 다시 계획부터 시작할 뿐
                }
                lock (live.Gate)
                {
                    live.PlanApprovedGeneration = generation;
                    // 쉬는 실행도 이제 계획 모드가 아니다 — "계획 먼저"를 다시 켜면 새로 띄운다
                    if (live.Session is { } planRun) planRun.Plan = false;
                }
                // 입력은 계획 글 · 파일 경로만 다시 담아 돌려준다 — 카드에 보이지 않는 키는 승인하지 않는다
                return PermissionDecision.AllowPlan(ApprovalRequest.PlanInput(ask.InputJson), mode);
            }
            var why = answer?.Message ?? "실행이 이미 끝나 거절했습니다.";
            return PermissionDecision.Deny($"{why} — 이 내용을 반영해 계획을 고친 뒤 다시 승인을 요청하세요.");
        }
        if (ask.Tool == ApprovalRequest.PlanTool)
        {
            HostDenied(room, live, generation, ask.Tool, "", "계획 모드로 시작한 실행이 아님", "ExitPlanMode: 계획 모드로 시작한 실행이 아니라 거절");
            return PermissionDecision.Deny("이 실행은 계획 모드로 시작하지 않았습니다 — 계획 승인을 받을 수 없습니다.");
        }
        // 새 방 열기: 모든 단계에서 늘 카드 (사용자가 값을 고쳐 허용)
        if (ask.Tool == RoomOpen.Tool && _terminals is not null)
            return await DecideRoomOpenAsync(room, live, generation, ask, aborted);
        // 웹 도구: 검색은 카드 없이 — 검색어만, 받는 곳은 이미 대화를 보는 Anthropic. 가져오기는 사이트마다 카드
        if (ask.Tool is ClaudeArgs.WebSearchTool or ClaudeArgs.WebFetchTool)
            return ask.Tool == ClaudeArgs.WebSearchTool ? PermissionDecision.Allow(ask.InputJson) : await DecideWebFetchAsync(room, live, generation, ask, aborted);
        if (ClaudeArgs.RunsCommands(room.Level) && ask.Tool == TerminalMcp.BrowserOpenTool && _browser is not null)
        {
            // 지휘자 브라우저 열기 (결정 "localhost만 묻지 않음"): 로컬 · 이 방에서 허용한 사이트는 카드 없이, 그 밖은 사이트마다 한 번 카드.
            // 허용 답은 url만 다시 담는다(보이지 않는 키는 승인하지 않는다). 도구 쪽도 허용된 사이트인지 다시 본다.
            var urlText = TerminalMcp.ParseString(ask.InputJson, "url");
            if (AgentBrowser.ParseUrl(urlText) is not { } url) return PermissionDecision.Deny("browser_open은 http · https 주소만 받습니다.");
            // 이 앱 · 대시보드는 카드로도 열지 않는다 — 열면 자기 승인 카드를 누를 수 있다
            if (_browser.IsForbidden(url)) return PermissionDecision.Deny("이 앱 · 대시보드 주소는 지휘자 브라우저로 열 수 없습니다.");
            var input = TerminalMcp.UrlInput(url.AbsoluteUri);
            if (_browser.IsAllowed(room.Id, url)) return PermissionDecision.Allow(input);
            if (store.Get(room.Id) is not { Level: PermissionLevel.Command or PermissionLevel.App })
                return PermissionDecision.Deny("사용자가 이 방 권한을 명령 단계 아래로 내렸습니다 — 브라우저를 열지 않습니다.");
            var card = new ApprovalRequest(BrowserTool, url.AbsoluteUri,
                $"지휘자 브라우저(사용자 브라우저와 따로)에서 {url.Host}를 엽니다. 허용하면 이 방 브라우저가 닫힐 때까지 같은 사이트는 다시 묻지 않습니다. " +
                "지휘자는 그 사이트에서 클릭 · 입력을 카드 없이 할 수 있습니다. 막는 것은 다른 사이트로의 페이지 이동뿐이고, 페이지 스크립트의 요청(fetch · 그림)은 막지 않습니다.");
            var answer = await RequestApprovalAsync(room.Id, live, generation, card, aborted);
            if (answer is not { Allow: true }) return (answer ?? new ApprovalAnswer(false, "실행이 이미 끝나 거절했습니다.")).ToDecisionJson(ask.InputJson);
            _browser.ApproveHost(room.Id, url.Host);
            return PermissionDecision.Allow(input);
        }
        if (ClaudeArgs.RunsCommands(room.Level) && ask.Tool == TerminalMcp.StartTool && _terminals is not null)
        {
            // 터미널 시작 (결정 "늘 카드"): 카드로 묻는다. 허용하면 표를 남겨 앱 MCP가 그 하나만 띄운다.
            if (TerminalMcp.ParseStart(ask.InputJson) is not var (name, command))
                return PermissionDecision.Deny("terminal_start 입력은 name · command 문자열이어야 합니다.");
            if (!Terminals.IsValidName(name)) return PermissionDecision.Deny("터미널 이름은 글자 · 숫자 · . _ - 만 32자까지입니다.");
            if (Terminals.WhyBadCommand(command) is { } bad) return PermissionDecision.Deny(bad);
            if (store.Get(room.Id) is not { Level: PermissionLevel.Command or PermissionLevel.App })
            {
                HostDenied(room, live, generation, TerminalTool, command, "권한을 명령 아래로 내림", $"터미널 {name}: 이 방 권한을 명령 아래로 내려 거절");
                return PermissionDecision.Deny("사용자가 이 방 권한을 명령 단계 아래로 내렸습니다 — 터미널을 띄우지 않습니다.");
            }
            // 감시형 명령(dev 서버 · watch)이면 이후 지휘자가 묻지 않고 고친 파일을 그대로 다시 실행한다 — 카드에 적는다
            var card = new ApprovalRequest(TerminalTool, command,
                $"터미널 '{name}' — 턴이 끝나도 계속 돌고, 곁칸 터미널 탭에서 보고 중지합니다. 파일을 감시해 다시 실행하는 명령이면 " +
                "이후 지휘자가 고친 파일(명령 방은 작업 폴더 편집을 묻지 않음)이 카드 없이 다시 실행됩니다.");
            var answer = await RequestApprovalAsync(room.Id, live, generation, card, aborted);
            if (answer is not { Allow: true }) return (answer ?? new ApprovalAnswer(false, "실행이 이미 끝나 거절했습니다.")).ToDecisionJson(ask.InputJson);
            _terminals.Approve(room.Id, name, command);
            return PermissionDecision.Allow(TerminalMcp.StartInput(name, command));
        }
        if (ClaudeArgs.RunsCommands(room.Level) && ClaudeArgs.CommandTools.Contains(ask.Tool))
        {
            // 실행 중에 곁칸에서 단계를 내렸으면(명령 → 그 아래) 이 턴의 명령도 카드 없이 거절한다 — 내리기는 바로 막힌다
            //. 올리기는 다음 실행부터.
            if (store.Get(room.Id) is not { Level: PermissionLevel.Command or PermissionLevel.App })
            {
                HostDenied(room, live, generation, ask.Tool, ApprovalRequest.Parse(ask.RequestJson)?.Summary ?? "", "권한을 명령 아래로 내림",
                    $"{ask.Tool}: 이 방 권한을 명령 아래로 내려 거절");
                return PermissionDecision.Deny("사용자가 이 방 권한을 명령 단계 아래로 내렸습니다 — 명령을 실행하지 않습니다.");
            }
            if (ApprovalRequest.Parse(ask.RequestJson) is not { } request) return PermissionDecision.Deny("승인 요청 모양을 읽지 못해 거절했습니다.");
            var answer = await RequestApprovalAsync(room.Id, live, generation, request, aborted);
            return (answer ?? new ApprovalAnswer(false, "실행이 이미 끝나 거절했습니다.")).ToDecisionJson(ask.InputJson);
        }
        if (ask.Tool == ClaudeArgs.AskTool)
        {
            if (QuestionRequest.Parse(ask.InputJson) is not { } question)
                return PermissionDecision.Deny("질문 카드로 보일 수 없는 모양이라 거절했습니다 — 같은 질문을 채팅 글로 물어 주세요.");
            var answers = await RequestQuestionAsync(room.Id, live, generation, question, aborted);
            return answers.Answers is { } given
                ? PermissionDecision.Allow(question.WithAnswers(given))
                : PermissionDecision.Deny($"사용자가 질문 카드에 답하지 않았습니다 ({answers.Label}). 필요하면 채팅 글로 다시 물어 주세요.");
        }
        if (workspace is not null && ask.Tool == $"mcp__{options.Value.McpServer}__{ClaudeArgs.RunModeTool}")
            return await DecideRunModeAsync(room, live, generation, ask, aborted, workspace);
        // 호스트가 거절하면 CLI는 system/permission_denied를 내지 않는다 (3차 a 실제 확인) — 그 전처럼 "거절됨" 줄을 화면에 남긴다.
        var summary = ApprovalRequest.Parse(ask.RequestJson)?.Summary ?? "";
        var target = summary is { Length: > 0 } s ? $" {(s.Length > 120 ? s[..120] + "…" : s)}" : "";
        if (ClaudeRunner.WriteTools.Contains(ask.Tool))
        {
            // 앱 단계 "폴더 밖 쓰기 묻기" (ask 규칙으로 모든 쓰기가 온다): 작업 폴더 안 · 메모리 · 늘 허용 폴더는 카드 없이, 그 밖은 카드.
            // 계획 모드 중(계획 먼저로 시작해 아직 허용 전)이면 그 전처럼 아래 길 — 작업 폴더 안 쓰기는 거절된다
            bool planning;
            lock (live.Gate) planning = room.PlanNext && live.PlanApprovedGeneration != generation;
            if (askWrites && room.Level == PermissionLevel.App && workspace is not null && !planning && store.Get(room.Id) is { Level: PermissionLevel.App })
                return await DecideAppWriteAsync(room, live, generation, ask, aborted, workspace, summary, target);
            // CLI가 스스로 허용하지 않은 파일 쓰기(작업 폴더 밖 등). 명령 방은 카드로 묻는다 (결정 "명령 단계만" — 명령으로 이미 카드를 거쳐 폴더 밖에
            // 쓸 수 있는 단계라 권한이 넓어지지 않는다). 위험한 곳(앱 · 설정 폴더 · 지시 · 스크립트 이름 · 링크)은 카드 없이 거절
            if (ClaudeArgs.RunsCommands(room.Level) && store.Get(room.Id) is { Level: PermissionLevel.Command or PermissionLevel.App })
            {
                var o = options.Value;
                var path = ApprovalRequest.WritePath(ask.Tool, ask.InputJson);
                // 앱 보호 목록이 없을 때(시험)도 Program과 같은 보호 폴더로
                string? Why() => path is null ? "쓸 파일 경로를 읽지 못했습니다 (경로 키가 없거나 둘 이상)."
                    : o.AppWorkspaces is { } guard ? guard.WhyNoWrite(path, workspace)
                    : WorkspaceStore.WhyNoWrite(path, WorkspaceStore.GuardedFolders(o, AppContext.BaseDirectory), workspace);
                var why = Why();
                if (why is null && ApprovalRequest.ForWrite(ask.Tool, ask.InputJson, path!) is { } card)
                {
                    var answer = await RequestApprovalAsync(room.Id, live, generation, card, aborted);
                    // 카드를 기다리는 사이 경로가 바뀌었으면(정션 · 링크로 바꿈) 허용하지 않는다. 쓰는 순간의 경쟁은 CLI 쪽이라 남는다
                    if (answer is { Allow: true } && Why() is { } changed)
                    {
                        HostDenied(room, live, generation, ask.Tool, summary, "허용 뒤 경로 바뀜", $"{ask.Tool}{target}: 허용 뒤 다시 보니 {changed} — 거절");
                        return PermissionDecision.Deny($"허용한 뒤 다시 확인해 보니 쓰지 않는 곳입니다: {changed} 같은 쓰기를 다시 시도하지 말고 사용자에게 말하세요.");
                    }
                    return (answer ?? new ApprovalAnswer(false, "실행이 이미 끝나 거절했습니다.")).ToDecisionJson(ask.InputJson);
                }
                why ??= "쓰기 입력을 읽지 못했습니다.";
                HostDenied(room, live, generation, ask.Tool, summary, "쓰지 않는 곳", $"{ask.Tool}{target}: {why} — 카드 없이 거절");
                return PermissionDecision.Deny($"{why} 이 파일은 쓰지 않습니다. 같은 쓰기를 다시 시도하지 말고, 무엇이 필요한지 사용자에게 말하세요.");
            }
            // 명령 단계가 아니면 쓰기 카드가 없다. 화면 줄 · 지휘자에게 까닭을 알린다
            HostDenied(room, live, generation, ask.Tool, summary, "작업 폴더 밖 쓰기", $"{ask.Tool}{target}: 작업 폴더 밖 · 보호된 파일 쓰기는 카드가 없어 자동 거절");
            return PermissionDecision.Deny(ClaudeRunner.NoWriteApprover);
        }
        if (ClaudeRunner.ReadTools.Contains(ask.Tool))
        {
            // 작업 폴더 · 첨부 폴더 밖 읽기 — 막는 것은 그대로, 까닭을 정확히 (사용자 보고 2026-10-30: "승인할 사람이 없다"가 화면 앞 사용자와 지휘자를 헷갈리게 함)
            HostDenied(room, live, generation, ask.Tool, summary, "작업 폴더 밖 읽기", $"{ask.Tool}{target}: 작업 폴더 밖 읽기는 받지 않아 자동 거절");
            return PermissionDecision.Deny(ClaudeRunner.NoReadApprover);
        }
        HostDenied(room, live, generation, ask.Tool, summary, "승인할 곳 없음", $"{ask.Tool}{target}: 승인할 곳이 없어 자동 거절");
        return PermissionDecision.Deny(ClaudeRunner.NoApprover);
    }

    /// <summary>
    /// 앱 단계 "폴더 밖 쓰기 묻기"의 쓰기 요청 (<see cref="WorkspaceStore.AppWrite(string?, string, string?, IEnumerable{string})"/>):
    /// 작업 폴더 안 · 그 작업 폴더의 자동 메모리 → 카드 없이 허용, 늘 허용 폴더 → 카드 없이 허용하고 승인 기록에 남김, 보호된 곳 → 카드 없이 거절,
    /// 그 밖 → 폴더 밖 쓰기 카드 (허용 · 이 폴더 늘 허용 · 거절). 허용한 뒤 다시 갈라 거절로 바뀌었으면(링크로 바꿈) 허용하지 않는다.
    /// </summary>
    private async Task<string> DecideAppWriteAsync(Room room, Live live, int generation, ToolPermission ask, CancellationToken aborted,
        string workspace, string summary, string target)
    {
        var o = options.Value;
        var path = ApprovalRequest.WritePath(ask.Tool, ask.InputJson);
        // CLI가 자동 메모리를 두는 곳: <세션 기록 폴더>/<작업 폴더 인코딩>/memory (비공식 규칙). 인코딩은 claude의 작업 디렉터리 모양 — 전체 경로 · 끝 구분자 없음.
        // 어긋나면 메모리 폴더는 Claude 설정 폴더(보호) 안이라 카드 없이 거절된다
        var memory = Path.Combine(o.ProjectsRoot,
            HB.Orchestration.Shared.ClaudeTranscriptReader.EncodeProjectDir(Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace))), "memory");
        (WorkspaceStore.AppWriteRoute Route, string? Why) Route() =>
            path is null ? (WorkspaceStore.AppWriteRoute.Deny, "쓸 파일 경로를 읽지 못했습니다 (경로 키가 없거나 둘 이상).")
            : o.AppWorkspaces is { } guard ? guard.AppWrite(path, workspace, memory, o.AppWrites?.Folders() ?? [])
            : WorkspaceStore.AppWrite(path, WorkspaceStore.GuardedFolders(o, AppContext.BaseDirectory), workspace, memory, o.AppWrites?.Folders() ?? []);
        var (route, why) = Route();
        switch (route)
        {
            case WorkspaceStore.AppWriteRoute.Allow:
                return PermissionDecision.Allow(ask.InputJson);
            case WorkspaceStore.AppWriteRoute.AllowMemory:
                // 다음 세션 문맥에 실리는 파일이라 기록에 남긴다 (sub_security 16f49d6897 C4 — 권한은 그 전 auto와 같음)
                Log?.Write(room.Id, new ApprovalRequest(ask.Tool, summary, null), "허용 (자동 메모리)");
                return PermissionDecision.Allow(ask.InputJson);
            case WorkspaceStore.AppWriteRoute.AllowFolder:
                Log?.Write(room.Id, new ApprovalRequest(ask.Tool, summary, null), "허용 (늘 허용 폴더)");
                return PermissionDecision.Allow(ask.InputJson);
            case WorkspaceStore.AppWriteRoute.Deny:
                why ??= "쓰지 않는 곳입니다.";
                HostDenied(room, live, generation, ask.Tool, summary, "쓰지 않는 곳", $"{ask.Tool}{target}: {why} — 카드 없이 거절");
                return PermissionDecision.Deny($"{why} 이 파일은 쓰지 않습니다. 같은 쓰기를 다시 시도하거나 명령으로 우회하지 말고, 무엇이 필요한지 사용자에게 말하세요.");
        }
        if (ApprovalRequest.ForWrite(ask.Tool, ask.InputJson, path!) is not { } card)
        {
            HostDenied(room, live, generation, ask.Tool, summary, "쓰지 않는 곳", $"{ask.Tool}{target}: 쓰기 입력을 읽지 못했습니다 — 카드 없이 거절");
            return PermissionDecision.Deny("쓰기 입력을 읽지 못했습니다. 같은 쓰기를 다시 시도하지 말고, 무엇이 필요한지 사용자에게 말하세요.");
        }
        // 늘 허용 대상: 쓸 파일의 바로 위 폴더가 이미 있고 늘 허용할 수 있을 때만, 그 최종 경로 (보인 것 = 저장되는 것).
        // 새 폴더를 만들며 쓰면 단추가 없다 — 모델이 없는 하위 경로로 넓은 상위 폴더를 후보로 만들지 못하게 (sub_security 16f49d6897 C1 · C2, sub_self C3)
        var parentDir = Path.GetDirectoryName(Path.GetFullPath(path!));
        var folder = o.AppWrites is { } writes && parentDir is not null && Directory.Exists(parentDir) && writes.WhyNotFolder(parentDir) is null
            ? AppWriteStore.FinalOf(parentDir) : null;
        var answer = await RequestApprovalAsync(room.Id, live, generation, card with { AlwaysFolder = folder }, aborted);
        if (answer is { Allow: true } && Route() is { Route: WorkspaceStore.AppWriteRoute.Deny } again)
        {
            var changed = again.Why ?? "쓰지 않는 곳입니다.";
            HostDenied(room, live, generation, ask.Tool, summary, "허용 뒤 경로 바뀜", $"{ask.Tool}{target}: 허용 뒤 다시 보니 {changed} — 거절");
            return PermissionDecision.Deny($"허용한 뒤 다시 확인해 보니 쓰지 않는 곳입니다: {changed} 같은 쓰기를 다시 시도하지 말고 사용자에게 말하세요.");
        }
        if (answer is { Allow: true, RememberFolder: true } && folder is not null && o.AppWrites?.AddFolder(folder) is { } addError)
            Notice(live, $"'{folder}'를 늘 허용 목록에 넣지 못했습니다 ({addError}) — 이번 쓰기만 허용합니다.");
        return (answer ?? new ApprovalAnswer(false, "실행이 이미 끝나 거절했습니다.")).ToDecisionJson(ask.InputJson);
    }

    /// <summary>
    /// 웹 가져오기(WebFetch): 로컬 · 사설망 주소는 카드 없이 거절, 이 방에서 허용한 사이트는 묻지 않고, 그 밖은 사이트마다 카드 (앱을 다시 켤 때까지 기억).
    /// 허용 답은 url · prompt만 다시 담는다 — 카드에 보이지 않는 키는 승인하지 않는다.
    /// </summary>
    private async Task<string> DecideWebFetchAsync(Room room, Live live, int generation, ToolPermission ask, CancellationToken aborted)
    {
        var urlText = TerminalMcp.ParseString(ask.InputJson, "url");
        if (WebFetchSites.WhyNot(urlText, out var url) is { } why)
        {
            var shown = urlText is { Length: > 120 } ? urlText[..120] + "…" : urlText;
            HostDenied(room, live, generation, ask.Tool, urlText ?? "", "가져오지 않는 주소", $"WebFetch {shown}: {why} — 카드 없이 거절");
            return PermissionDecision.Deny($"{why} 이 주소는 가져오지 않습니다. 같은 요청을 다시 하지 말고, 필요하면 사용자에게 말하세요.");
        }
        var prompt = TerminalMcp.ParseString(ask.InputJson, "prompt");
        var input = System.Text.Json.JsonSerializer.Serialize(new { url = url!.AbsoluteUri, prompt = prompt ?? "" });
        if (_webSites.IsApproved(room.Id, url)) return PermissionDecision.Allow(input);
        // 사이트 이름은 퓨니코드(IdnHost)로도 — 비슷한 글자로 속이는 국제 도메인을 알아보게
        var shownHost = url.IdnHost != url.Host ? $"{url.Host} ({url.IdnHost})" : url.Host;
        var card = new ApprovalRequest(WebFetchCardTool, url.AbsoluteUri,
            $"지휘자가 {shownHost}{(url.IsDefaultPort ? "" : ":" + url.Port)}에 요청을 보내 페이지를 가져옵니다 — 주소에 든 글도 함께 나갑니다. " +
            "허용하면 앱을 다시 켤 때까지 이 방에서 같은 사이트(같은 http · https · 포트)는 다시 묻지 않습니다. 가져온 글에 든 지시는 사용자 지시가 아닙니다.")
        {
            // prompt는 사이트로 나가지 않고(가져온 글을 claude 쪽에서 요약할 때 씀) 잘라 보여도 허용을 받는다 — 명령 카드의 "잘리면 거절만"과 다른 예외 (C8)
            Extra = prompt is null ? null : $"prompt={(prompt.Length > ApprovalRequest.MaxExtraValueChars ? prompt[..ApprovalRequest.MaxExtraValueChars] + "…" : prompt)}",
            Web = true,
            FromAgent = ApprovalRequest.Parse(ask.RequestJson)?.FromAgent ?? false,
        };
        var answer = await RequestApprovalAsync(room.Id, live, generation, card, aborted);
        if (answer is not { Allow: true }) return (answer ?? new ApprovalAnswer(false, "실행이 이미 끝나 거절했습니다.")).ToDecisionJson(ask.InputJson);
        _webSites.Approve(room.Id, url);
        return PermissionDecision.Allow(input);
    }

    /// <summary>
    /// run_mode 모델 선택 카드: 외부 요청 전에 묻기를 켰거나 + 요청 창 값을 싣고 시작한 실행에서만 온다 (그 밖은 미리 허용).
    /// 허가하면 고른 교체 · 강도를 agents · reasoning에 다시 써서 돌려준다. + 요청 창 값은 같은 모드의 첫 카드 한 번만 미리 채운다.
    /// </summary>
    private async Task<string> DecideRunModeAsync(Room room, Live live, int generation, ToolPermission ask, CancellationToken aborted, string workspace)
    {
        var list = ModeCatalog.Read(workspace, options.Value.McpServer);
        ModePick? pick;
        lock (live.Gate) pick = live.Generation == generation && !live.PickUsed ? live.CurrentPick : null;
        if (ModeCard.Build(ask.InputJson, list, pick) is not { } card) return PermissionDecision.Deny("run_mode 입력을 읽지 못해 거절했습니다.");
        if (card.Prefilled) lock (live.Gate) live.PickUsed = true;
        // 기록(approvals.jsonl)에 모드 이름이 남게 요약 첫 줄로
        var request = new ApprovalRequest(ask.Tool, $"{card.Mode}\n{card.InputHead}", card.Reason) { Mode = card };
        var answer = await RequestApprovalAsync(room.Id, live, generation, request, aborted);
        if (answer is not { Allow: true }) return (answer ?? new ApprovalAnswer(false, "실행이 이미 끝나 거절했습니다.")).ToDecisionJson(ask.InputJson);
        if (store.Get(room.Id) is null) return PermissionDecision.Deny("이 방이 없어졌습니다 — 외부 에이전트를 부르지 않습니다.");
        return PermissionDecision.Allow(card.Apply(ask.InputJson, answer.Picks));
    }

    /// <summary>Unity 도구 중 명령 카드로 묻는 것 — C# 코드 실행 셋 (결정 4 "eval만 카드로"). 나머지 Unity 도구는 묻지 않는다.</summary>
    public static readonly IReadOnlySet<string> UnityCardTools = new HashSet<string>(StringComparer.Ordinal)
    {
        $"mcp__{McpConfigFile.UnityServer}__eval", $"mcp__{McpConfigFile.UnityServer}__eval_file", $"mcp__{McpConfigFile.UnityServer}__run_script",
    };

    /// <summary>mcp__서버__도구 → 서버 이름. MCP 도구가 아니면 null.</summary>
    public static string? McpServerOf(string tool)
    {
        if (!tool.StartsWith("mcp__", StringComparison.Ordinal)) return null;
        var end = tool.IndexOf("__", 5, StringComparison.Ordinal);
        return end > 5 ? tool[5..end] : null;
    }

    /// <summary>
    /// 방별 서버 도구: 지금 방을 다시 보고 — 단계를 내렸거나 · 연결을 끊었거나 · 다른 Unity 프로젝트로 바꿨으면 거절(내리기는 바로 막힌다),
    /// Unity 코드 실행 셋은 명령 카드, 그 밖은 카드 없이 허용(결정 "묻지 않음" · "연결하면 편집").
    /// </summary>
    private async Task<string> DecideRoomServerAsync(Room room, Live live, int generation, ToolPermission ask, CancellationToken aborted, string server, string? unity)
    {
        var now = store.Get(room.Id);
        var isUnity = server == McpConfigFile.UnityServer;
        var still = isUnity
            ? now is { Level: PermissionLevel.Command or PermissionLevel.App } && unity is not null && string.Equals(now.UnityProject, unity, StringComparison.OrdinalIgnoreCase)
            : now is { } n && n.McpExtra.Contains(server);
        if (!still)
        {
            var why = isUnity ? "Unity 연결을 끊었거나 권한을 명령 아래로 내림" : "연결을 껐거나 권한을 내림";
            HostDenied(room, live, generation, ask.Tool, ApprovalRequest.Parse(ask.RequestJson)?.Summary ?? "", why, $"{ask.Tool}: {why} — 거절");
            return PermissionDecision.Deny($"사용자가 이 방의 {(isUnity ? "Unity 연결 · 권한" : "연결 · 권한")}을 바꿨습니다 — 이 도구를 쓰지 않습니다.");
        }
        if (!isUnity || !UnityCardTools.Contains(ask.Tool)) return PermissionDecision.Allow(ask.InputJson);
        if (ApprovalRequest.Parse(ask.RequestJson) is not { } request) return PermissionDecision.Deny("승인 요청 모양을 읽지 못해 거절했습니다.");
        var card = request with
        {
            Description = $"Unity 에디터({unity}) 안에서 C# 코드를 실행합니다 — 에디터 · 프로젝트 파일 · 이 PC의 파일을 바꿀 수 있고, 편집 금지 경로도 막히지 않습니다.",
        };
        var answer = await RequestApprovalAsync(room.Id, live, generation, card, aborted);
        return (answer ?? new ApprovalAnswer(false, "실행이 이미 끝나 거절했습니다.")).ToDecisionJson(ask.InputJson);
    }

    /// <summary>
    /// 카드 없이 거절: 승인 기록에도 남기고(방 기록 줄 — 기록 화면 제자리 · 새로고침 뒤에도) 진행 칸에 "거절됨" 줄을 알린다.
    /// Logged면 화면이 턴 끝에 그 줄을 지운다. 기록이 먼저 — 턴 끝에 다시 받을 때 이미 있게.
    /// </summary>
    private void HostDenied(Room room, Live live, int generation, string tool, string summary, string why, string line)
    {
        Log?.Write(room.Id, new ApprovalRequest(tool, summary, null), $"거절 ({why})");
        lock (live.Gate)
        {
            if (live.Generation == generation) Broadcast(live, new LiveEvent("denied") { Text = line, Logged = true });
        }
    }

    /// <summary>테스트용: 그 방의 지금 실행에 승인 카드를 하나 더 띄운다 (실행 중이 아니면 null).</summary>
    internal Task<ApprovalAnswer?> RequestApprovalAsync(string roomId, ApprovalRequest request, CancellationToken aborted)
    {
        if (!_rooms.TryGetValue(roomId, out var live)) return Task.FromResult<ApprovalAnswer?>(null);
        int generation;
        lock (live.Gate) generation = live.Generation;
        return RequestApprovalAsync(roomId, live, generation, request, aborted);
    }

    /// <summary>
    /// 승인 카드를 띄우고 답 · 시간 초과 · 실행 쪽 끊김(aborted) · 중지 중 먼저 오는 것으로 끝낸다. 답은 기록에도 남긴다.
    /// 그 세대의 실행이 아니면(이미 끝남 · 중지) null.
    /// </summary>
    private async Task<ApprovalAnswer?> RequestApprovalAsync(string roomId, Live live, int generation, ApprovalRequest request, CancellationToken aborted)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.ApprovalTimeoutSeconds));
        PendingApproval? waiting = null;
        lock (live.Gate)
        {
            if (!RunAlive(live, generation)) return null;
            // "이 턴 동안 묻지 않기": 켜기와 같은 잠금에서 보아 일괄 허용을 비껴간 요청이 카드로 남지 않게.
            // 단계도 Decide처럼 잠금 안에서 다시 본다 — 그사이 내렸으면 카드로 간다
            if (!(live.TrustCommands && request.TurnAllowable && store.Get(roomId)?.Level is PermissionLevel.Command or PermissionLevel.App))
            {
                waiting = new PendingApproval(request, DateTimeOffset.Now + timeout)
                {
                    InTurn = live.Cts is not null,
                    RestMinutes = store.Get(roomId)?.GroupId is null ? (int)(SessionTimings ?? ClaudeSession.DefaultTimings).Cap.TotalMinutes : null,
                };
                live.Approvals[waiting.Id] = waiting;
                Broadcast(live, new LiveEvent("approval") { Approval = waiting.View });
                SetFace(live, Face.Approval);
            }
        }
        if (waiting is not { } pending)
        {
            Log?.Write(roomId, request, TurnAllowLabel);
            return new ApprovalAnswer(true, null);
        }

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        wait.CancelAfter(timeout);
        try
        {
            await pending.Answer.Task.WaitAsync(wait.Token);
        }
        catch (OperationCanceledException)
        {
            // 실행이 먼저 끝났으면(프로세스 종료 · 취소) 답을 돌려줘도 버려진다. 아니면 시간 초과 — 둘 다 거절.
            var (label, message) = aborted.IsCancellationRequested
                ? ("끊김", "승인 요청이 claude 쪽에서 먼저 끊겼습니다.")
                : ("시간 초과", "사용자가 시간 안에 답하지 않아 거절했습니다. 필요하면 다시 요청하세요.");
            lock (live.Gate)
            {
                if (pending.Answer.TrySetResult(new ApprovalAnswer(false, message))) pending.Label = label;
            }
        }

        var answer = await pending.Answer.Task;
        // 답을 정한 쪽(EndApprovals 등)은 TrySetResult 뒤 잠금 안에서 Label을 적는다 — 잠금으로 기다려 읽는다
        string decision;
        lock (live.Gate) decision = pending.Label ?? (answer.Allow ? "허용" : "거절");
        // 기록을 approval-done보다 먼저 — 화면이 턴 끝에 기록을 다시 받을 때 방 기록 줄이 이미 있게
        // 계획 글은 앞 2,000자만 기록한다 — 승인 기록이 계획 전문으로 커지지 않게 (지휘자 검토 S4)
        var logged = request.IsPlan && request.Summary.Length > PlanLogChars ? request with { Summary = request.Summary[..PlanLogChars] + "…" } : request;
        Log?.Write(roomId, logged, decision);
        lock (live.Gate)
        {
            live.Approvals.Remove(pending.Id);
            Broadcast(live, new LiveEvent("approval-done") { Approval = pending.View, Text = pending.Label });
            // 다른 카드가 남았으면 그대로 승인 기다림, 아니면 도구 단계로 (허용이면 명령이 돈다, 거절이면 곧 다음 글).
            // Busy가 아니라 Cts로 본다: 실행이 끝나는 잠금에서 Cts를 먼저 비우므로 end 뒤에 face가 나가지 않는다.
            if (live.Cts is not null && live.Approvals.Count == 0) SetFace(live, live.Questions.Count > 0 ? Face.Question : Face.Tool);
        }
        pending.Settled.TrySetResult();
        return answer;
    }

    /// <summary>
    /// 질문 카드를 띄우고 답 · 답하지 않기 · 시간 초과 · 실행 쪽 끊김 · 중지 중 먼저 오는 것으로 끝낸다 (3차 a). Answers가 null이면 답 없음(Label = 이유).
    /// 기록(approvals.jsonl)에도 질문 → 답을 남긴다.
    /// </summary>
    private async Task<(IReadOnlyDictionary<string, string>? Answers, string Label)> RequestQuestionAsync(
        string roomId, Live live, int generation, QuestionRequest request, CancellationToken aborted)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.ApprovalTimeoutSeconds));
        PendingQuestion pending;
        lock (live.Gate)
        {
            if (!RunAlive(live, generation)) return (null, "실행 끝남");
            pending = new PendingQuestion(request, DateTimeOffset.Now + timeout);
            live.Questions[pending.Id] = pending;
            Broadcast(live, new LiveEvent("question") { Question = pending.View });
            if (live.Approvals.Count == 0) SetFace(live, Face.Question);
        }

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        wait.CancelAfter(timeout);
        try
        {
            await pending.Answer.Task.WaitAsync(wait.Token);
        }
        catch (OperationCanceledException)
        {
            lock (live.Gate)
            {
                if (pending.Answer.TrySetResult(null)) pending.Label = aborted.IsCancellationRequested ? "끊김" : "시간 초과";
            }
        }

        var answers = await pending.Answer.Task;
        string label;
        lock (live.Gate)
        {
            label = answers is null ? pending.Label ?? "답 없음" : "답함";
            live.Questions.Remove(pending.Id);
            Broadcast(live, new LiveEvent("question-done")
            {
                Question = pending.View,
                Text = answers is null ? label : QuestionRequest.Summary(request.Items, answers),
                Ok = answers is not null, // 화면이 글 모양으로 가르지 않게 (지휘자 검토 C5)
                Answers = answers, // 화면이 바로 '나' 말풍선으로 그린다 (4차 D1 — 턴이 끝나면 세션 기록의 답으로 바뀜)
            });
            if (live.Cts is not null && !live.Waiting) SetFace(live, Face.Thinking); // 답을 받으면 지휘자가 이어 생각한다
        }
        Log?.Write(roomId, new ApprovalRequest(ClaudeArgs.AskTool, QuestionRequest.Summary(request.Items, null), null),
            answers is null ? label : "답: " + QuestionRequest.Summary(request.Items, answers));
        return (answers, label);
    }

    /// <summary>이미 끝난 카드에 답했을 때의 문구 — 처리기가 이 값이면 409로 답해 화면이 오류 창 없이 카드를 닫는다.</summary>
    public const string QuestionDone = "이미 끝난 질문입니다.";
    public const string ApprovalDone = "이미 끝난 승인 요청입니다.";

    /// <summary>CLI가 이유 없이 거절했을 때 보이는 문구 (이유가 비어 오는 일이 있다 — 사용자 보고 2026-10-30).</summary>
    public const string CliDeniedReason = "CLI 규칙으로 거절";

    /// <summary>
    /// CLI가 앱에 묻지 않고 거절한 호출 (system/permission_denied — 호스트 거절에는 이 줄이 없어 겹치지 않는다).
    /// 승인 기록에도 남겨 턴 끝에 사라지지 않고 제자리 방 기록 줄(주황)로 보인다.
    /// </summary>
    private void CliDenied(Room room, Live live, int generation, DeniedEvent d, IReadOnlyDictionary<string, string?> targets)
    {
        var tool = d.Tool is { Length: > 0 } name ? name : "도구";
        var why = string.IsNullOrWhiteSpace(d.Reason) ? CliDeniedReason : d.Reason.Trim();
        var target = d.ToolUseId is { } id && targets.GetValueOrDefault(id) is { Length: > 0 } detail ? detail : "";
        HostDenied(room, live, generation, tool, target, why, target.Length > 0 ? $"{tool} {target}: {why}" : $"{tool}: {why}");
    }

    /// <summary>"이 턴 동안 묻지 않기"로 허용한 명령의 기록 문구.</summary>
    public const string TurnAllowLabel = "이 턴 허용";

    /// <summary>"이 폴더 늘 허용"으로 허용한 쓰기의 기록 문구.</summary>
    public const string AlwaysFolderLabel = "허용 + 폴더 늘 허용";

    /// <summary>앱 단계 도구 호출의 승인 기록 라벨. 채팅 줄에는 보이지 않는다 — 도구 칩이 이미 있다.</summary>
    public const string AppCallLabel = "앱 단계 호출";

    /// <summary>"이 턴 동안 묻지 않기"를 켠다 · 끈다 (잠금 안에서 부른다). 바뀔 때만 화면에 알린다.</summary>
    private void SetTrust(Live live, bool on)
    {
        if (live.TrustCommands == on) return;
        live.TrustCommands = on;
        Broadcast(live, new LiveEvent("trust") { Trust = on });
    }

    /// <summary>"이 턴 동안 묻지 않기"를 끈다 (화면의 끄기 · 명령 아래로 내릴 때).</summary>
    public void EndTurnTrust(string roomId)
    {
        if (!_rooms.TryGetValue(roomId, out var live)) return;
        lock (live.Gate) SetTrust(live, false);
    }

    /// <summary>지금 "이 턴 동안 묻지 않기"가 켜져 있는지 (단톡방 화면이 다시 붙을 때).</summary>
    public bool TurnTrusted(string roomId)
    {
        if (!_rooms.TryGetValue(roomId, out var live)) return false;
        lock (live.Gate) return live.TrustCommands;
    }

    /// <summary>
    /// 질문 카드의 답 (화면). answers가 null이면 "답하지 않기". 오류 문구(null이면 받음).
    /// </summary>
    public string? Answer(string roomId, string questionId, IReadOnlyDictionary<string, string>? answers)
    {
        if (!_rooms.TryGetValue(roomId, out var live)) return "없는 방입니다.";
        lock (live.Gate)
        {
            if (!live.Questions.TryGetValue(questionId, out var pending) || pending.Answer.Task.IsCompleted)
                return QuestionDone;
            if (answers is not null && pending.Request.Validate(answers) is { } error) return error;
            if (!pending.Answer.TrySetResult(answers)) return QuestionDone;
            if (answers is null) pending.Label = "답하지 않음";
        }
        return null;
    }

    /// <summary>
    /// 카드 단추. 오류 문구(null이면 받음). 잘린 명령은 허용을 받지 않는다 (사용자가 다 보지 못함).
    /// </summary>
    /// <param name="picks">모델 선택 카드에서 고른 값 (원래 에이전트 → 고른 에이전트 · 강도). 다른 카드는 null.</param>
    public string? Decide(string roomId, string approvalId, ApprovalChoice choice, string? reason, IReadOnlyDictionary<string, ModeRowPick>? picks = null,
        RoomOpenSpec? roomPick = null)
    {
        if (!_rooms.TryGetValue(roomId, out var live)) return "없는 방입니다.";
        lock (live.Gate)
        {
            if (!live.Approvals.TryGetValue(approvalId, out var pending) || pending.Answer.Task.IsCompleted)
                return ApprovalDone;
            // 카드가 뜬 뒤 곁칸에서 권한을 내렸으면 허용을 받지 않는다 —
            // 명령 · 계획 카드는 명령 단계가 아니면. 단계는 이 잠금 안에서 읽는다
            var level = store.Get(roomId)?.Level;
            if (pending.Request.Mode is { } card)
                return DecideModeCard(pending, card, level, choice, reason, picks);
            if (pending.Request.RoomOpen is { } roomCard)
                return DecideRoomOpenCard(pending, roomCard, level, choice, reason, roomPick);
            if (choice != ApprovalChoice.Deny && (pending.Request.IsPlan ? level is not { } l || ClaudeArgs.PlanMode(l) is null
                    : pending.Request.Web ? level is null
                    : level is not (PermissionLevel.Command or PermissionLevel.App)))
                return pending.Request.IsPlan
                    ? "이 방 권한을 명령 단계 아래로 내려 계획을 진행할 수 없습니다 — 거절만 됩니다."
                    : pending.Request.Web ? "이 방이 없어져 허용할 수 없습니다 — 거절만 됩니다."
                    : "이 방 권한을 명령 단계 아래로 내려 허용할 수 없습니다 — 거절만 됩니다.";
            if (choice != ApprovalChoice.Deny && pending.Request.DenyOnly)
                return pending.Request.Risky
                    ? "위험한 입력(dangerously · bypass)이 있어 거절만 할 수 있습니다."
                    : "명령이나 입력이 너무 길어 다 보이지 않습니다 — 거절만 할 수 있습니다.";
            if (choice == ApprovalChoice.AllowTurn && (!pending.Request.TurnAllowable || !pending.InTurn))
                return "이 요청에는 '이 턴 동안 묻지 않기'를 쓸 수 없습니다.";
            // 늘 허용은 앱 단계 폴더 밖 쓰기 카드만 — 카드가 뜬 뒤 앱 아래로 내렸으면 받지 않는다 (명령 단계에는 그 목록이 걸리지 않음)
            if (choice == ApprovalChoice.AllowFolder && (pending.Request.AlwaysFolder is null || level != PermissionLevel.App))
                return "이 요청에는 '이 폴더 늘 허용'을 쓸 수 없습니다.";
            var label = choice switch
            {
                ApprovalChoice.Allow => "허용",
                ApprovalChoice.AllowTurn => TurnAllowLabel,
                ApprovalChoice.AllowFolder => AlwaysFolderLabel,
                _ => "거절",
            };
            var message = string.IsNullOrWhiteSpace(reason) ? "사용자가 거절했습니다." : $"사용자가 거절했습니다: {reason.Trim()}";
            if (!pending.Answer.TrySetResult(new ApprovalAnswer(choice != ApprovalChoice.Deny, choice == ApprovalChoice.Deny ? message : null)
                { RememberFolder = choice == ApprovalChoice.AllowFolder }))
                return ApprovalDone;
            pending.Label = label;
            if (choice == ApprovalChoice.AllowTurn)
            {
                SetTrust(live, true);
                // 같은 턴에 떠 있는 다른 명령 카드도 같이 — 거절만 받는 카드 · 꼭 묻는 명령은 그대로
                foreach (var other in live.Approvals.Values.Where(p => p != pending && p.Request.TurnAllowable && p.InTurn))
                    if (other.Answer.TrySetResult(new ApprovalAnswer(true, null))) other.Label = TurnAllowLabel;
            }
        }
        return null;
    }

    /// <summary>모델 선택 카드의 답 (잠금 안에서 부른다). 허용 기록에는 바꾼 줄을 적는다.</summary>
    private static string? DecideModeCard(PendingApproval pending, ModeCard card, PermissionLevel? level, ApprovalChoice choice, string? reason,
        IReadOnlyDictionary<string, ModeRowPick>? picks)
    {
        if (choice == ApprovalChoice.AllowTurn) return "이 요청에는 '이 턴 동안 묻지 않기'를 쓸 수 없습니다.";
        if (choice == ApprovalChoice.Allow && level is null) return "이 방이 없어져 허가할 수 없습니다 — 거절만 됩니다.";
        if (choice == ApprovalChoice.Allow && card.Check(picks) is { } bad) return bad;
        var message = string.IsNullOrWhiteSpace(reason) ? "사용자가 거절했습니다." : $"사용자가 거절했습니다: {reason.Trim()}";
        if (!pending.Answer.TrySetResult(new ApprovalAnswer(choice == ApprovalChoice.Allow, choice == ApprovalChoice.Deny ? message : null) { Picks = picks }))
            return ApprovalDone;
        pending.Label = choice == ApprovalChoice.Deny ? "거절" : ModeLabel(card, picks);
        return null;
    }

    /// <summary>허가 기록 문구: 바뀐 줄만 "허용 (원래→새것 · 강도)", 없으면 "허용".</summary>
    public static string ModeLabel(ModeCard card, IReadOnlyDictionary<string, ModeRowPick>? picks)
    {
        var changes = new List<string>();
        foreach (var row in card.Rows)
        {
            var pick = card.Final(row, picks);
            var swapped = !string.Equals(pick.Agent, row.Agent, StringComparison.OrdinalIgnoreCase);
            if (swapped || !string.IsNullOrEmpty(pick.Effort))
                changes.Add((swapped ? $"{row.Agent}→{pick.Agent}" : row.Agent) + (string.IsNullOrEmpty(pick.Effort) ? "" : $" · {pick.Effort}"));
        }
        return changes.Count == 0 ? "허용" : $"허용 ({string.Join(", ", changes)})";
    }

    /// <summary>
    /// 작업 폴더 · 사용자 설정이 이미 미리 허용한 명령 규칙 (Bash · PowerShell). 명령 방에서는 이 규칙과 맞는 명령이 카드 없이 실행되므로
    /// 방 화면에 보인다 (/self-review ab79e61df5 C3 — 프로젝트 훅이 필요해 설정 출처를 줄이지는 않는다). 읽지 못한 파일은 건너뛴다.
    /// </summary>
    public IReadOnlyList<(string Source, string Rule)> InheritedCommandRules(Room room)
    {
        var files = new List<(string Source, string Path)>
        {
            ("사용자", System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json")),
        };
        if (options.Value.WorkspacePath(room.Workspace) is { } ws)
        {
            files.Add(("작업 폴더", System.IO.Path.Combine(ws, ".claude", "settings.json")));
            files.Add(("작업 폴더 local", System.IO.Path.Combine(ws, ".claude", "settings.local.json")));
        }
        var rules = new List<(string, string)>();
        foreach (var (source, path) in files)
        {
            try
            {
                if (!File.Exists(path)) continue;
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("permissions", out var p) || !p.TryGetProperty("allow", out var allow) ||
                    allow.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                foreach (var a in allow.EnumerateArray())
                    if (a.GetString() is { } rule && (rule.StartsWith("Bash", StringComparison.Ordinal) || rule.StartsWith("PowerShell", StringComparison.Ordinal)))
                        rules.Add((source, rule));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException) { }
        }
        return rules;
    }

    /// <summary>실행이 끝나거나 중지될 때: 기다리던 승인 · 질문을 모두 거절(답 없음)로 끝낸다 (잠금 안에서 부른다).</summary>
    private static void EndApprovals(Live live, string label, bool keepAgents = false)
    {
        foreach (var p in live.Approvals.Values)
            if (!(keepAgents && p.Request.FromAgent) && p.Answer.TrySetResult(new ApprovalAnswer(false, $"{label} — 승인 요청을 거절했습니다."))) p.Label = label;
        foreach (var q in live.Questions.Values)
            if (q.Answer.TrySetResult(null)) q.Label = label;
    }
}
