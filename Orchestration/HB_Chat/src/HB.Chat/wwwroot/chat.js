// HB Chat 화면 (계획서 2-2 a · b). 왼쪽 방 목록 + 오른쪽 방 한 칸.
// - 방 목록: 앱 전체 알림(/events)을 받으면 목록 부분만 다시 받는다.
// - 방 전환: 목록에서 방을 누르면 오른쪽 칸만 ?handler=Pane으로 받아 바꾸고, 앞 방의 SSE · 타이머를 끝낸 뒤 새 방을 붙인다 (mountRoom).
// - 방 동작은 1차 · 2-1의 Room.cshtml 스크립트를 옮긴 것이다 (알림 차례 처리 · snapshot마다 다시 받기 · 승인 카드 …).
// - 2-2 b: 말풍선은 50개씩(위로 올리면 앞 쪽), 턴이 끝나면 새 말만 붙인다. 지휘자 표정 · 입력 중 · 도구 칩 · 흐름 안 승인 카드 ·
//   코드 복사 · 인용 답장 · 컨텍스트 표시.
(() => {
    "use strict";
    const app = document.querySelector(".app");
    if (!app) return;
    const el = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; };
    const pane = document.getElementById("pane");

    // ---- 방 목록 ----
    let room = null; // 지금 붙은 방: { id, dispose }
    let listTimer = null, listSeq = 0;
    async function refreshList() {
        const seq = ++listSeq;
        try {
            const res = await fetch("/?handler=RoomList&current=" + encodeURIComponent(room?.id || ""), { cache: "no-store" });
            if (!res.ok || seq !== listSeq) return;
            const tmp = document.createElement("div");
            tmp.innerHTML = await res.text(); // 서버가 Razor 인코딩으로 만든 같은 출처 HTML
            const fresh = tmp.querySelector("#room-list"), old = document.getElementById("room-list");
            if (fresh && old && seq === listSeq) {
                const top = old.scrollTop; // 목록을 내려 둔 자리를 지킨다
                fresh.hidden = old.hidden; // 검색 중이면 목록은 가려 둔 채로
                old.replaceWith(fresh);
                applyGroups(fresh); // 접은 묶음은 접힌 채로 (높이가 바뀌기 전에)
                fresh.scrollTop = top;
                const trash = document.getElementById("trash-count");
                if (trash && fresh.dataset.trash != null) trash.textContent = fresh.dataset.trash;
                // 요청을 보낸 뒤 방을 바꿨을 수 있다 — 서버가 그린 강조 대신 지금 방으로 (/self-review 5225b1ffaf C3)
                markActive(room?.id ?? null);
            }
        } catch { /* 다음 알림 때 다시 */ }
    }
    function scheduleList() { clearTimeout(listTimer); listTimer = setTimeout(refreshList, 250); }
    // 작업 폴더별 묶음 (3차 d): 접은 폴더 이름을 브라우저에 저장한다 (편의만 — 못 읽으면 모두 펼침). 목록을 다시 받을 때마다 다시 적용.
    function closedGroups() { try { return new Set(JSON.parse(localStorage.getItem("hbchat.wsClosed") || "[]")); } catch { return new Set(); } }
    function applyGroups(list) {
        const closed = closedGroups();
        list?.querySelectorAll("details.ws-group").forEach(d => {
            // 기다리는 방 · 지금 방이 있는 묶음은 접힌 기억과 상관없이 펼친다 (서버가 data-keep-open)
            d.open = d.dataset.keepOpen === "1" || !closed.has(d.dataset.ws);
            d.addEventListener("toggle", () => {
                const now = closedGroups();
                if (d.open) now.delete(d.dataset.ws); else now.add(d.dataset.ws);
                try { localStorage.setItem("hbchat.wsClosed", JSON.stringify([...now])); } catch { }
            });
        });
    }
    applyGroups(document.getElementById("room-list"));
    function markActive(id) {
        document.querySelectorAll("#room-list .room-item").forEach(a => a.classList.toggle("active", a.dataset.room === id));
    }
    // 탭 하나는 SSE를 하나만 연다 (HTTP/1.1 한 호스트 연결 6개 상한 — /self-review 5225b1ffaf C1):
    // 방을 열었으면 방 SSE가 목록 신호(rooms)도 싣고, 연 방이 없는 첫 화면만 앱 전체 /events를 연다.
    // EventSource는 끊기면 스스로 다시 붙는다 — 붙을 때마다 hello(방 SSE는 snapshot)가 와서 목록을 다시 받는다.
    let appEvents = null;
    function openAppEvents() {
        appEvents = new EventSource("/events");
        appEvents.onmessage = () => scheduleList();
    }
    // 방마다 쓰던 글 · 인용 (방을 바꿔도 사라지지 않게 — /self-review 5225b1ffaf C6). 이 탭 메모리에만.
    const drafts = new Map();

    document.addEventListener("click", (e) => {
        const a = e.target.closest("a.room-item, a.search-hit");
        if (!a || e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey || e.altKey) return;
        e.preventDefault();
        openRoom(a.dataset.room, true, a.dataset.line ? Number(a.dataset.line) : null);
    });

    // ---- 검색 (2-2 c): 쓰는 동안 잠깐 멈추면 찾고, 결과를 목록 자리에. 비우거나 Esc면 목록으로 ----
    const searchBox = document.getElementById("search");
    const searchResults = document.getElementById("search-results");
    let searchTimer = null, searchSeq = 0;
    function showList() {
        searchResults.hidden = true;
        searchResults.replaceChildren();
        const list = document.getElementById("room-list");
        if (list) list.hidden = false;
    }
    async function runSearch() {
        const q = searchBox.value.trim();
        const seq = ++searchSeq;
        if (!q) { showList(); return; }
        try {
            const res = await fetch("/?handler=Search&q=" + encodeURIComponent(q), { cache: "no-store" });
            if (!res.ok || seq !== searchSeq) return;
            searchResults.innerHTML = await res.text(); // 같은 출처, Razor 인코딩
            searchResults.hidden = false;
            const list = document.getElementById("room-list");
            if (list) list.hidden = true;
        } catch { /* 다음 입력 때 다시 */ }
    }
    searchBox?.addEventListener("input", () => { clearTimeout(searchTimer); searchTimer = setTimeout(runSearch, 250); });
    searchBox?.addEventListener("keydown", (e) => { if (e.key === "Escape") { searchBox.value = ""; searchSeq++; showList(); } });

    // ---- 회사 모드 (2-2 c): 표정 · 스티커 숨김, 이 브라우저에만 저장 (사용자 결정). 켜는 건 _Layout이 그리기 전에 한다 ----
    const company = document.getElementById("company");
    if (company) {
        company.checked = document.documentElement.classList.contains("company");
        company.addEventListener("change", () => {
            document.documentElement.classList.toggle("company", company.checked);
            try { localStorage.setItem("hbchat.company", company.checked ? "1" : "0"); } catch { /* 저장 못 해도 이번 화면에는 적용 */ }
        });
    }
    // 다른 창(데스크톱 큰 창 ↔ 미니 창 · 다른 탭)에서 바꾸면 이미 열린 화면도 따라간다 — 처음 그릴 때만 읽어 미니 창이 옛 상태로 남았다 (사용자 제보 2026-10-28)
    window.addEventListener("storage", (e) => {
        if (e.key !== "hbchat.company") return;
        const on = e.newValue === "1";
        document.documentElement.classList.toggle("company", on);
        if (company) company.checked = on;
    });
    window.addEventListener("popstate", () => {
        const m = location.pathname.match(/^\/Room\/([0-9a-f]{12})$/);
        if (m) openRoom(m[1], false); else location.reload();
    });

    // 마지막으로 누른 방만 붙인다: 먼저 보낸 요청이 늦게 와도 버린다 (/self-review 5225b1ffaf C2).
    // line이 있으면 그 말까지 앞 쪽을 받아 보이고 강조한다 (검색 결과, 2-2 c).
    let openSeq = 0;
    async function openRoom(id, push, line) {
        const seq = ++openSeq;
        if (room?.id === id) { app.classList.add("in-room"); if (line != null) room.reveal(line); return; }
        let html;
        try {
            const res = await fetch("/Room/" + id + "?handler=Pane", { cache: "no-store" });
            if (seq !== openSeq) return;
            if (!res.ok) { location.href = "/Room/" + id; return; }
            html = await res.text();
        } catch { if (seq === openSeq) location.href = "/Room/" + id; return; }
        if (seq !== openSeq) return;
        room?.dispose();
        room = null;
        pane.innerHTML = html; // 같은 출처, Razor 인코딩 · SafeMarkdown으로 만든 HTML (스크립트 없음)
        if (push) history.pushState(null, "", "/Room/" + id);
        app.classList.add("in-room");
        markActive(id);
        const root = pane.querySelector(".room");
        if (root) {
            room = mountRoom(root);
            appEvents?.close(); // 이제 방 SSE가 목록 신호를 싣는다
            appEvents = null;
            if (line != null) room.reveal(line);
        }
    }
    // 같은 방을 다시 받아 붙인다 (되살리기처럼 방 칸 모양이 바뀐 뒤)
    function reopenRoom(id) {
        room?.dispose();
        room = null;
        return openRoom(id, false);
    }

    // 도구 이름 짧게 (서버 RoomList.ShortTool과 같음): mcp__서버__도구 → 도구
    function shortTool(name) {
        if (!name.startsWith("mcp__")) return name;
        const cut = name.indexOf("__", 5);
        return cut > 0 && cut + 2 < name.length ? name.slice(cut + 2) : name;
    }
    // 토큰 수 짧게 (서버 RoomModel.Tokens와 같음 — K로 반올림해 1000이 되면 M)
    function tokens(n) {
        if (n < 1e3) return String(n);
        const k = Math.round(n / 1e3);
        return k < 1e3 ? k + "K" : (n / 1e6).toFixed(1).replace(/\.0$/, "") + "M";
    }
    // 서버가 만든 말풍선 HTML(같은 출처, SafeMarkdown · Razor 인코딩)에서 #chat 하나를 꺼낸다.
    function parseChat(html) {
        const tmp = document.createElement("div");
        tmp.innerHTML = html;
        return tmp.querySelector("#chat");
    }
    // ---- 위젯 (2-2 f): 지휘자 말풍선의 ```widget 코드 블록을 격리된 iframe에 그린다 ----
    // 처음에는 sandbox=""(스크립트 없음, HTML · CSS만). 틀 위 "실행"을 누르면 sandbox="allow-scripts"로 다시 그린다 — WebRTC(STUN · TURN)는
    // CSP · sandbox로 막을 수 없어, 오염된 위젯이 사용자 모르게 글을 밖으로 보내지 못하게 (/self-review security 803e5f13eb C3 · abc6cec3a6 C1).
    // 어느 쪽이든 allow-same-origin 없음 → 불투명 출처: 앱 쿠키 · antiforgery · 같은 출처 요청 · 부모 DOM 접근 없음, 폼 전송 · 새 창 ·
    // 맨 위 이동 · 모달 · 내려받기도 없음. 문서 CSP는 네트워크를 다 막고 인라인 스크립트 · 스타일 · data: 그림만.
    // 위젯이 스스로 다른 주소로 이동하는 것은 페이지 CSP frame-src 'none'(Widgets.cs)이 막는다. 소스는 서버가 이스케이프한 글의 textContent.
    // 틀 위 표시 · 단추 · "코드 보기" 글은 CSS로 넣는다 — 인용 답장이 innerText로 본문을 읽을 때 섞이지 않게 (803e5f13eb C4).
    const WidgetMaxChars = 200000, WidgetMinHeight = 40, WidgetMaxHeight = 1200;
    const WidgetCsp = "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data: blob:; " +
        "font-src data:; media-src data: blob:; form-action 'none'; base-uri 'none'";
    function widgetDoc(source) {
        return "<!doctype html><html><head><meta charset=\"utf-8\">" +
            "<meta http-equiv=\"Content-Security-Policy\" content=\"" + WidgetCsp + "\">" +
            "<meta name=\"color-scheme\" content=\"light dark\">" +
            // 위젯 CSS의 100vh 높이는 풀어 둔다 — 틀을 키우면 창 높이도 커져 상한까지 계속 자라는 되먹임 (803e5f13eb C2)
            "<style>html,body{margin:0}html{height:auto!important}body{height:auto!important;min-height:0!important;" +
            "padding:8px;font:14px/1.5 system-ui,\"Malgun Gothic\",sans-serif}</style>" +
            // 높이 알림(실행한 위젯만): body 높이 + 위아래 여백. documentElement는 창 높이 아래로 줄지 않아, 한 번 커진 틀이 다시 줄지 않았다 (실제 확인).
            // 부모는 보낸 창이 그 iframe인지 보고, 높이를 상한 안으로 자른다 (위젯이 꾸며 보내도 제 높이만 바뀐다)
            "<script>(()=>{const s=()=>{const b=document.body,c=getComputedStyle(b);parent.postMessage({hbWidgetHeight:" +
            "Math.ceil(b.getBoundingClientRect().height+parseFloat(c.marginTop)+parseFloat(c.marginBottom))},\"*\");};" +
            "addEventListener(\"load\",()=>{s();new ResizeObserver(s).observe(document.body);});})()<\/script>" +
            "</head><body>" + source + "</body></html>";
    }
    window.addEventListener("message", (e) => {
        const h = e.data !== null && typeof e.data === "object" ? e.data.hbWidgetHeight : undefined;
        if (typeof h !== "number" || !Number.isFinite(h)) return;
        for (const frame of document.querySelectorAll("iframe.widget-frame")) {
            if (frame.contentWindow !== e.source) continue;
            // 맨 아래를 보던 중이면 틀이 커진 뒤에도 맨 아래로 — 늦게 오는 높이에 마지막 위젯이 잘려 있었다 (803e5f13eb C1)
            const scroller = frame.closest(".scroll");
            const follow = scroller && scroller.scrollTop + scroller.clientHeight >= scroller.scrollHeight - 120;
            frame.style.height = Math.min(Math.max(Math.ceil(h), WidgetMinHeight), WidgetMaxHeight) + "px";
            if (follow) scroller.scrollTop = scroller.scrollHeight;
            return;
        }
    });
    function widgetFrame(source, run) {
        const frame = el("iframe", "widget-frame" + (run ? " running" : ""));
        frame.setAttribute("sandbox", run ? "allow-scripts" : "");
        frame.setAttribute("referrerpolicy", "no-referrer");
        frame.setAttribute("loading", "lazy");
        frame.title = run ? "위젯 (실행 중)" : "위젯 (스크립트 꺼짐)";
        frame.srcdoc = widgetDoc(source);
        return frame;
    }
    function widgets(container) {
        container.querySelectorAll(".msg.conductor .body pre > code.language-widget").forEach(code => {
            const pre = code.parentElement;
            if (pre.closest(".widget")) return;
            const box = el("div", "widget");
            const source = code.textContent;
            pre.replaceWith(box);
            if (source.length > WidgetMaxChars) {
                const note = el("div", "muted widget-note");
                note.dataset.note = `위젯이 너무 커서(${source.length.toLocaleString()}자) 코드로만 보입니다.`;
                box.append(note, pre);
                return;
            }
            const bar = el("div", "widget-bar");
            const run = el("button", "widget-run");
            run.type = "button";
            run.setAttribute("aria-label", "위젯 스크립트 실행");
            bar.append(run);
            const frame = widgetFrame(source, false);
            run.addEventListener("click", () => {
                box.querySelector("iframe.widget-frame").replaceWith(widgetFrame(source, true));
                box.classList.add("running");
                run.remove();
            });
            const details = el("details", "widget-code");
            details.append(el("summary"), pre);
            box.append(bar, frame, details);
        });
    }
    // 코드 블록 복사 단추 (2-2 b). 글자는 CSS ::after로 넣는다 — 인용 답장이 innerText로 본문을 읽을 때 섞이지 않게.
    function decorate(container) {
        widgets(container);
        container.querySelectorAll(".body pre").forEach(pre => {
            if (pre.querySelector(":scope > .copy")) return;
            const b = el("button", "copy");
            b.type = "button";
            b.setAttribute("aria-label", "코드 복사");
            pre.append(b);
        });
    }
    async function copyText(text) {
        try { await navigator.clipboard.writeText(text); return true; } catch { /* 아래 방법으로 */ }
        const ta = el("textarea"); ta.value = text; ta.style.position = "fixed"; ta.style.opacity = "0";
        document.body.append(ta); ta.select();
        let ok = false;
        try { ok = document.execCommand("copy"); } catch { }
        ta.remove();
        return ok;
    }

    // ---- 방 하나 ----
    function mountRoom(root) {
        const roomId = root.dataset.room;
        const self = "/Room/" + roomId;
        const $ = (sel) => root.querySelector(sel);
        const scroller = $(".scroll");
        const form = $("#send-form");
        const textBox = $("#text");
        const stopBtn = $("#stop");
        const interruptBtn = $("#interrupt");
        const live = $("#live");
        const notice = $("#notice");
        const conn = $("#conn");
        const token = form.querySelector("input[name=__RequestVerificationToken]").value;
        let disposed = false;
        document.title = "HB Chat · " + (root.dataset.title || "");

        const atBottom = () => scroller.scrollTop + scroller.clientHeight >= scroller.scrollHeight - 120;
        const toBottom = () => { scroller.scrollTop = scroller.scrollHeight; };
        const chatEl = () => $("#chat");

        // ---- 인용 답장 (2-2 b): 말풍선의 "답장" → 입력칸 위 인용 칩, 보낼 때 본문 앞에 인용 블록 ----
        const quoteBox = $("#quote"), quoteText = $("#quote-text");
        let quote = null; // { who, when, text }
        const sentTexts = []; // 이 탭이 보낸 글 (막혀서 돌아온 말을 입력칸에 돌려줄지 — 4차 C2 · bd52c6f0d1 C6)
        const QuoteChars = 300;
        function setQuote(q) {
            quote = q;
            quoteBox.hidden = !q;
            quoteText.textContent = q ? q.who + (q.when ? " " + q.when : "") + ": " + q.text.replace(/\s+/g, " ").slice(0, 120) : "";
        }
        function quoteBlock(q) {
            return "> " + q.who + (q.when ? " (" + q.when + ")" : "") + ":\n" + q.text.split("\n").map(l => "> " + l).join("\n");
        }
        $("#quote-x").addEventListener("click", () => setQuote(null));

        const saved = drafts.get(roomId);
        if (saved) { textBox.value = saved.text || ""; setQuote(saved.quote || null); drafts.delete(roomId); }

        // ---- 방 관리 (2-2 c): 이름 바꾸기 · 복제 · 휴지통 · 되살리기 ----
        const menu = $("details.menu");
        function startRename() {
            if (menu) menu.open = false;
            const h2 = $("#room-title");
            if (!h2 || h2.hidden) return;
            const input = el("input", "rename");
            input.type = "text"; input.maxLength = 200; input.value = h2.textContent;
            input.placeholder = "비우면 첫 말로";
            h2.hidden = true;
            h2.after(input);
            input.focus(); input.select();
            let done = false;
            const finish = async (save) => {
                if (done) return;
                done = true;
                if (save && input.value.trim() !== h2.textContent) {
                    const data = new FormData(); data.append("title", input.value);
                    // 제목(비우면 첫 말)은 새 말 받기의 data-title로 바뀐다. 새 말 받기는 알림 줄(chain)에 세운다 — end · snapshot의 받기와 겹치면 말이 두 번 붙었다 (/self-review c216951e79 C2)
                    if (await post("Rename", data)) { chain = chain.then(() => disposed ? null : refreshMessages()).catch(() => { }); loadSide(); }
                }
                input.remove();
                h2.hidden = false;
            };
            input.addEventListener("keydown", (ev) => {
                if (ev.key === "Enter" && !ev.isComposing) { ev.preventDefault(); finish(true); }
                else if (ev.key === "Escape") finish(false);
            });
            input.addEventListener("blur", () => finish(true));
        }

        // 메뉴 밖을 누르거나 내보내기를 누르면 닫는다 (95e8b1c242 C9)
        const closeMenu = (e) => { if (menu?.open && !e.target.closest("details.menu")) menu.open = false; };
        document.addEventListener("click", closeMenu);
        menu?.querySelector("a[download]")?.addEventListener("click", () => setTimeout(() => { menu.open = false; }, 0));

        root.addEventListener("click", async (e) => {
            if (e.target.closest(".older")) { loadOlder(); return; }
            if (e.target.closest("#room-title, .menu-rename")) { startRename(); return; }
            if (e.target.closest(".menu-fork")) {
                if (menu) menu.open = false;
                // 갈라지는 때는 새 방에 첫 말을 보낼 때다 (95e8b1c242 C2) — 문구도 그대로
                if (!confirm("이 방 대화를 이어받는 새 방을 만들까요?\n새 방에 첫 말을 보낼 때 그때까지의 이 방 대화가 복사됩니다 (그 전에 이 방에서 더 나눈 대화도 포함). '항상 허용' 규칙은 옮기지 않습니다.")) return;
                const res = await post("Fork", new FormData(), false, true);
                if (res?.id) openRoom(res.id, true);
                return;
            }
            if (e.target.closest(".menu-trash")) {
                if (menu) menu.open = false;
                if (!confirm("이 방을 휴지통에 넣을까요?\n30일 뒤 방 목록에서 지웁니다 (세션 기록은 남음). 그 전에는 휴지통에서 되살릴 수 있습니다.")) return;
                if (await post("Trash", new FormData())) location.href = "/";
                return;
            }
            if (e.target.closest(".menu-restore")) {
                if (await post("Restore", new FormData())) reopenRoom(roomId);
                return;
            }
            const reply = e.target.closest(".reply");
            if (reply) {
                const msg = reply.closest(".msg");
                const body = msg?.querySelector(".body");
                if (!body) return;
                const text = body.innerText.trim();
                setQuote({
                    // 보이는 이름(프로필)이 아니라 고정 이름 — 인용은 지휘자에게 가는 글이다 (3차 e /self-review security)
                    who: msg.dataset.speaker === "user" ? "나" : "지휘자",
                    when: (msg.querySelector(".who .when")?.textContent || "").slice(0, 11), // MM-dd HH:mm
                    text: text.length > QuoteChars ? text.slice(0, QuoteChars) + "…" : text,
                });
                textBox.focus();
                return;
            }
            const copy = e.target.closest(".copy");
            if (copy) {
                const pre = copy.closest("pre");
                const code = pre?.querySelector("code");
                if (await copyText((code ?? pre).textContent)) {
                    copy.classList.add("copied");
                    setTimeout(() => copy.classList.remove("copied"), 1500);
                }
            }
        });

        // ---- 지휘자 표정 (2-2 b): 상태 → 스티커 그림 (없으면 글자 아바타) + 상태 글 ----
        const faces = JSON.parse(root.dataset.faces || "{}");
        const faceImg = $("#face-img"), faceLetter = $("#face-letter"), faceLabel = $("#face-label");
        // 끝난 턴의 표정을 보이는 시간은 서버(RoomHub.EndFaceFor)가 준다. 처음 그린 끝 표정은 남은 시간만 (83b453653b C11).
        const endFaceMs = Number(root.dataset.endFaceMs || 10000);
        let faceTimer = null;
        function setFace(name, extra, leftMs) {
            clearTimeout(faceTimer);
            const f = faces[name] || {};
            if (f.url) {
                if (faceImg.getAttribute("src") !== f.url) faceImg.src = f.url;
                faceImg.hidden = false; faceLetter.hidden = true;
            } else { faceImg.hidden = true; faceLetter.hidden = false; }
            faceLabel.textContent = (f.label || "") + (extra ? " · " + extra : "");
            // 끝난 턴의 표정은 잠깐만. 실패는 다음 턴까지 그대로.
            if (name === "Done" || name === "Stopped") faceTimer = setTimeout(() => setFace("Idle"), leftMs ?? endFaceMs);
        }
        if (root.dataset.face === "Done" || root.dataset.face === "Stopped") setFace(root.dataset.face, null, Number(root.dataset.faceLeft || 0));

        // ---- 컨텍스트 (2-2 b): 턴이 끝날 때의 크기 / 창. 창을 모르는 턴이면 앞 창을 그대로 (83b453653b C5) ----
        let ctxWindow = Number($("#ctx").dataset.window || 0) || null;
        function showContext(t, w) {
            if (t == null) return;
            if (w) ctxWindow = w;
            $("#ctx").hidden = false;
            $("#ctx-text").textContent = "컨텍스트 " + tokens(t) + (ctxWindow ? " / " + tokens(ctxWindow) : "");
            $("#ctx-fill").style.width = (ctxWindow ? Math.min(100, t * 100 / ctxWindow) : 0) + "%";
        }

        // 읽음 표시: 맨 아래를 보고 있고 창이 보일 때 마지막 말의 줄 번호를 보낸다 (앞으로만).
        let sentRead = Number(root.dataset.readUpTo || -1);
        function markRead() {
            if (disposed || document.hidden || !atBottom()) return;
            const last = Number(chatEl()?.dataset.lastLine ?? -1);
            if (last <= sentRead) return;
            const before = sentRead;
            sentRead = last;
            const data = new FormData(); data.append("line", String(last));
            // 저장하지 못했으면 다음 기회에 다시 보낸다 (/self-review 5225b1ffaf C7)
            post("MarkRead", data, true).then(ok => { if (!ok && sentRead === last) sentRead = before; });
        }

        // ---- 지난 대화 50개씩 (2-2 b): 위로 올리면 앞 쪽을 받아 붙이고 보던 자리를 지킨다 ----
        // 받는 중이면 그 요청을 같이 기다린다. 앞 쪽을 붙였으면 true (reveal이 실패 때 멈추게 — 95e8b1c242 C5).
        let olderPromise = null;
        function loadOlder() {
            if (!olderPromise) olderPromise = fetchOlder().finally(() => { olderPromise = null; });
            return olderPromise;
        }
        async function fetchOlder() {
            const chat = chatEl();
            if (disposed || chat?.dataset.hasMore !== "1") return false;
            try {
                const res = await fetch(self + "?handler=Messages&before=" + chat.dataset.firstLine, { cache: "no-store" });
                if (!res.ok || disposed) return false;
                const fresh = parseChat(await res.text());
                if (!fresh || disposed || chatEl() !== chat) return false;
                decorate(fresh);
                const height = scroller.scrollHeight, top = scroller.scrollTop;
                const hint = chat.querySelector(".older");
                const anchor = hint ? hint.nextSibling : chat.firstChild;
                const frag = document.createDocumentFragment();
                while (fresh.firstChild) frag.append(fresh.firstChild);
                chat.insertBefore(frag, anchor);
                if (fresh.dataset.firstLine !== "-1") chat.dataset.firstLine = fresh.dataset.firstLine;
                chat.dataset.hasMore = fresh.dataset.hasMore;
                if (fresh.dataset.hasMore !== "1") hint?.remove();
                scroller.scrollTop = top + (scroller.scrollHeight - height);
                setTimeout(fillUp, 0);
                return true;
            } catch { return false; /* 다음 스크롤 · 안내 누르기 때 다시 */ }
        }
        // 칸이 다 차지 않으면 스크롤이 생기지 않아 위로 올릴 수 없다 — 찰 때까지 앞 쪽을 이어 받는다 (83b453653b C8).
        function fillUp() {
            if (!disposed && !revealing && scroller.scrollHeight <= scroller.clientHeight + 300) loadOlder();
        }

        let readTimer = null;
        const onScroll = () => {
            if (scroller.scrollTop < 300) loadOlder();
            clearTimeout(readTimer); readTimer = setTimeout(markRead, 300);
        };
        scroller.addEventListener("scroll", onScroll);
        document.addEventListener("visibilitychange", markRead);

        // 진행 중 턴: 보낸 말(흐리게) + 지휘자 말풍선(입력 중 점 → 흘림 글, 위에 도구 칩) + 승인 카드 · 거절 줄.
        // 흘림 글은 글자 그대로이고, 끝나면 세션 기록의 말풍선으로 바뀐다. 기다리는 말은 입력칸 위 목록(#queue, 2-2 d).
        let stream = null, chips = null;
        // 나 · 지휘자 프로필 (3차 e): 서버가 data-profiles로 준 이름 · 글자 · 색 · 그림 (없으면 기본)
        const profiles = (() => { try { return JSON.parse(root.dataset.profiles || "{}"); } catch { return {}; } })();
        function avatar(p, fallback) {
            const a = el("div", "avatar");
            if (p?.color && /^#[0-9a-fA-F]{6}$/.test(p.color)) a.style.background = p.color;
            if (p?.image) { const img = el("img", "profile-img"); img.alt = ""; img.src = p.image; a.append(img); }
            a.append(el("span", "avatar-letter", p?.letter || fallback));
            return a;
        }
        function bubble(cls, who, text) {
            const m = el("div", "msg " + cls), b = el("div", "bubble");
            const user = cls.startsWith("user");
            m.dataset.speaker = user ? "user" : "conductor";
            m.append(avatar(user ? profiles.user : profiles.conductor, user ? "나" : "C"), b);
            const w = el("div", "who"); w.append(el("b", null, who)); b.append(w);
            if (cls === "conductor") { chips = el("div", "chips"); chips.hidden = true; b.append(chips); }
            const body = el("div", "body stream", text); b.append(body);
            live.append(m);
            return body;
        }
        function typing() {
            const t = el("span", "typing");
            t.setAttribute("aria-label", "입력 중");
            t.append(el("i"), el("i"), el("i"));
            return t;
        }
        function addChip(name) {
            if (!chips) return;
            chips.hidden = false;
            const last = chips.lastElementChild;
            if (last && last.dataset.name === name) {
                last.dataset.count = String(Number(last.dataset.count) + 1);
                last.textContent = name + " ×" + last.dataset.count;
                return;
            }
            const c = el("span", "chip", name);
            c.dataset.name = name; c.dataset.count = "1";
            chips.append(c);
            // 대상이 붙어 칩이 잘 묶이지 않는다 — 최근 MaxChips개만 두고 앞은 "앞 N개"로 접는다 (/self-review ea28ba9185 C6)
            const items = chips.querySelectorAll(":scope > .chip:not(.older-chips)");
            if (items.length > MaxChips) {
                let older = chips.querySelector(":scope > .older-chips");
                if (!older) { older = el("span", "chip older-chips"); older.dataset.count = "0"; chips.prepend(older); }
                const gone = items.length - MaxChips;
                for (let i = 0; i < gone; i++) {
                    older.dataset.count = String(Number(older.dataset.count) + Number(items[i].dataset.count));
                    items[i].remove();
                }
                older.textContent = "앞 " + older.dataset.count + "개";
            }
        }
        const MaxChips = 20;
        // 기다리는 말 목록 (2-2 d): 말마다 × — 누르면 그 말만 취소 (서버 CancelQueued, 목록은 queued 알림으로 다시 그린다)
        const queueBox = $("#queue");
        function showQueue(items) {
            queueBox.replaceChildren();
            if (!items || items.length === 0) { queueBox.hidden = true; return; }
            queueBox.hidden = false;
            queueBox.append(el("span", "muted", "기다리는 말 " + items.length));
            for (const item of items) {
                const oneLine = displayText(item.text).replace(/\s+/g, " ").trim();
                // 글은 span에서 줄임표 — ×가 잘리지 않게 (/self-review 5b26cff7df C1)
                const chip = el("span", "chip queued");
                chip.append(el("span", "t", oneLine.length > 40 ? oneLine.slice(0, 40) + "…" : oneLine));
                chip.title = displayText(item.text).slice(0, 500);
                // 지금 보내기 (3차 b): 이 말을 맨 앞으로 옮기고 지금 턴을 끊는다 (끼어들기와 같은 길 — 다른 기다리는 말은 남음)
                const now = el("button", "queue-now", "지금");
                now.type = "button"; now.title = "지금 턴을 멈추고 이 말을 바로 보냅니다";
                now.addEventListener("click", async () => {
                    now.disabled = true;
                    const data = new FormData(); data.append("queued", item.id);
                    if (!await post("SendNow", data)) now.disabled = false;
                });
                const x = el("button", "queue-x", "×");
                x.type = "button"; x.title = "이 말 취소"; x.setAttribute("aria-label", "이 말 취소");
                x.addEventListener("click", async () => {
                    x.disabled = true;
                    const data = new FormData(); data.append("queued", item.id);
                    if (!await post("CancelQueued", data)) x.disabled = false;
                });
                chip.append(now, x);
                queueBox.append(chip);
            }
        }
        function setBusy(busy) { stopBtn.disabled = !busy; interruptBtn.disabled = !busy; }
        // 진행 단계 (3차 b): 지휘자 할 일 목록. 요약 줄은 "단계 끝난 수/전체 · 지금: 하는 단계" — 펼치면 목록. 곁칸 단계 탭에도 같은 목록 (3차 c).
        const stepsBox = $("#steps");
        function renderSteps(steps) {
            const sideSteps = $("#side-steps");
            if (sideSteps) {
                sideSteps.replaceChildren(...(steps || []).map(s => el("li", s.status, s.subject)));
                $("#side-steps-empty").hidden = !!(steps && steps.length);
            }
            if (!stepsBox) return;
            if (!steps || steps.length === 0) { stepsBox.hidden = true; return; }
            const done = steps.filter(s => s.status === "completed").length;
            const current = steps.find(s => s.status === "in_progress");
            $("#steps-summary").textContent = "단계 " + done + "/" + steps.length
                + (current ? " · 지금: " + current.subject : done === steps.length ? " · 모두 끝남" : "");
            $("#steps-list").replaceChildren(...steps.map(s => el("li", s.status, s.subject)));
            stepsBox.hidden = false;
        }
        // 지휘자 상태 한 줄 (3차 b, CLI post_turn_summary): 사용자가 할 일이 있으면 그것을
        function showStatus(st) {
            const line = $("#status-line");
            if (!line || !st) return;
            const text = st.needsAction || st.detail;
            line.textContent = text ? (st.needsAction ? "할 일: " : "") + text : "";
            line.title = line.textContent; // 말줄임된 뒷부분은 마우스를 올려 본다
            line.classList.toggle("needs", !!st.needsAction);
            line.hidden = !text;
        }
        // 보낸 글의 끝 [첨부] 줄을 "파일 n개: 이름"으로 — 절대 경로를 그대로 보이지 않게 (5b26cff7df C7, 서버 Attachments.Split과 같은 규칙)
        function displayText(text) {
            const lines = text.replace(/\r\n/g, "\n").split("\n");
            const names = [];
            let end = lines.length;
            while (end > 0) {
                const line = lines[end - 1];
                if (!line.trim()) { end--; continue; }
                if (!line.startsWith("[첨부] ") || !line.slice(5).trim()) break;
                names.unshift(line.slice(5).trim().split(/[\\/]/).pop());
                end--;
            }
            if (names.length === 0) return text;
            const body = lines.slice(0, end).join("\n").trimEnd();
            return (body ? body + "\n" : "") + "📎 파일 " + names.length + "개: " + names.join(", ");
        }
        // 진행 중 칸 비우기 — '보내지 못함' 말풍선은 남긴다 (세션 기록에 없어 지우면 되찾을 곳이 없다, /self-review bd52c6f0d1 C2)
        function clearLive() { live.replaceChildren(...live.querySelectorAll(":scope > .msg.failed")); }
        function startTurn(prompt, partial, pending) {
            const follow = atBottom();
            clearLive();
            cards.clear(); // 카드도 live 안에 있었다
            chips = null;
            if (prompt) bubble("user pending", profiles.user?.name || "나", displayText(prompt));
            stream = bubble("conductor", profiles.conductor?.name || "지휘자", partial || null);
            if (!partial) { stream.dataset.empty = "1"; stream.append(typing()); }
            setBusy(true);
            showQueue(pending);
            if (follow) toBottom();
        }
        // 턴이 끝난 뒤: 마지막으로 본 줄 뒤의 새 말만 받아 붙인다. 기록이 다시 쓰였으면(줄이 줄었으면) 쪽 전체를 다시 받는다.
        async function refreshMessages() {
            // 검색 결과로 옛 말을 보이는 중이면 맨 아래로 따라가지 않는다 (95e8b1c242 C4)
            const follow = atBottom() && !revealing;
            const chat = chatEl();
            // 읽기 실패로 그린 쪽은 기준 줄을 모른다 — 쪽 전체를 다시 받는다 (83b453653b C6)
            if (chat.dataset.error) return await reloadMessages(follow);
            try {
                const res = await fetch(self + "?handler=Messages&after=" + encodeURIComponent(chat.dataset.lastLine), { cache: "no-store" });
                if (!res.ok || disposed) return false;
                const fresh = parseChat(await res.text());
                if (!fresh || disposed || chatEl() !== chat) return false;
                if (Number(fresh.dataset.lastLine) < Number(chat.dataset.lastLine)) return await reloadMessages(follow);
                // 기록의 질문 답 말풍선이 오면 진행 중 칸의 실시간 답은 지운다 — 턴 도중 새 말 받기에 같은 답이 두 번 보이지 않게 (e8091295fe C3)
                if (fresh.querySelector(":scope > .msg.answer")) live.querySelectorAll(":scope > .msg.answer").forEach(m => m.remove());
                // 새 말이 한 쪽보다 많으면(갈래 첫 턴 등) 서버가 맨 끝 쪽을 준다 — 붙이지 않고 쪽을 바꾼다 (95e8b1c242 C3)
                if (fresh.dataset.reset) {
                    decorate(fresh);
                    chat.replaceWith(fresh);
                    setTitle(fresh.dataset.title);
                    if (follow) toBottom();
                    markRead();
                    return true;
                }
                if (fresh.firstElementChild) {
                    chat.querySelector(".empty")?.remove();
                    // 맨 뒤 실행 말풍선은 조각이 다시 보낸다 (다음 말이 생겼으면 그 앞으로) — 앞서 그린 것은 지운다 (/self-review a9e6066519 C1)
                    chat.querySelectorAll(":scope > .run-trailing").forEach(n => n.remove());
                    decorate(fresh);
                    if (chat.dataset.firstLine === "-1") chat.dataset.firstLine = fresh.dataset.firstLine;
                    while (fresh.firstChild) chat.append(fresh.firstChild);
                }
                chat.dataset.lastLine = fresh.dataset.lastLine;
                setTitle(fresh.dataset.title);
                if (follow) toBottom();
                markRead();
                return true;
            } catch { return false; }
        }
        async function reloadMessages(follow) {
            try {
                const res = await fetch(self + "?handler=Messages", { cache: "no-store" });
                if (!res.ok || disposed) return false;
                const fresh = parseChat(await res.text());
                if (!fresh || disposed) return false;
                decorate(fresh);
                chatEl().replaceWith(fresh);
                setTitle(fresh.dataset.title);
                if (follow) toBottom();
                markRead();
                return true;
            } catch { return false; }
        }
        function setTitle(title) {
            if (!title) return;
            $("#room-title").textContent = title;
            document.title = "HB Chat · " + title;
        }

        // ---- 오른쪽 곁칸 (3차 c): 단계 · 방 정보 · 외부 실행 · 첨부. 열림 · 탭은 브라우저 저장(편의만 — 못 읽으면 닫힌 채 · 단계 탭) ----
        const side = $("#side"), sideToggle = $("#side-toggle");
        const store = { get(k) { try { return localStorage.getItem(k); } catch { return null; } }, set(k, v) { try { localStorage.setItem(k, v); } catch { } } };
        const sideTabs = ["steps", "info", "runs", "files"];
        // 저장값이 모르는 탭이면 단계 탭 — 모든 칸이 숨던 문제 (c216951e79 C7)
        let sideTab = sideTabs.includes(store.get("hbchat.sideTab")) ? store.get("hbchat.sideTab") : "steps";
        let sideLoading = false, sideAgain = false;
        // 넓은 폭(셋째 칸)인지 — 좁으면 곁칸이 방을 덮는다 (chat.css의 1199px 규칙과 같은 값)
        const wide = () => window.matchMedia("(min-width: 1200px)").matches;
        function showTab(tab) {
            sideTab = tab;
            store.set("hbchat.sideTab", tab);
            side.querySelectorAll(".side-tabs [data-tab]").forEach(b => b.setAttribute("aria-selected", String(b.dataset.tab === tab)));
            side.querySelectorAll(".side-sec").forEach(s => { s.hidden = s.dataset.tab !== tab; });
        }
        function sidePlaceholder(text) {
            // 받는 중 · 실패 글은 탭과 상관없이 보인다 (c216951e79 C6 · 지휘자 K3) — 단계 탭은 화면이 그리므로 그 탭에만 가린다
            const p = el("p", "muted side-sec side-wait", text);
            p.hidden = sideTab === "steps";
            $("#side-data").replaceChildren(p);
        }
        // 방 정보 · 외부 실행 · 첨부는 열 때 · 턴 끝 · 제목 · 규칙 · 첨부가 바뀔 때 서버에서 다시 받는다 (방 칸을 그릴 때는 계산하지 않음).
        // 받는 중에 또 부르면 끝난 뒤 한 번 더 — 버리면 바뀌기 전 값이 남았다 (c216951e79 C1 · 지휘자 K1)
        async function loadSide() {
            if (!side || side.hidden || disposed) return;
            if (sideLoading) { sideAgain = true; return; }
            sideLoading = true;
            try {
                const res = await fetch(self + "?handler=Side", { cache: "no-store" });
                if (disposed) return;
                if (!res.ok) { sidePlaceholder("곁칸을 불러오지 못했습니다 (" + res.status + ") — 닫았다 다시 열어 보세요."); return; }
                const doc = new DOMParser().parseFromString("<div>" + await res.text() + "</div>", "text/html");
                $("#side-data").replaceChildren(...doc.body.firstElementChild.children);
                const rename = $("#side-rename");
                rename?.addEventListener("submit", async (e) => {
                    e.preventDefault();
                    const data = new FormData(); data.append("title", $("#side-title").value);
                    // 제목(비우면 첫 말)은 새 말 받기가 바꾼다 — 알림 줄에 세운다 (C2)
                    if (await post("Rename", data)) { chain = chain.then(() => disposed ? null : refreshMessages()).catch(() => { }); loadSide(); }
                });
                showTab(sideTab);
            } catch {
                if (!disposed) sidePlaceholder("곁칸을 불러오지 못했습니다 — 닫았다 다시 열어 보세요.");
            } finally {
                sideLoading = false;
                if (sideAgain && !disposed) { sideAgain = false; loadSide(); }
            }
        }
        function setSide(open) {
            side.hidden = !open;
            sideToggle.setAttribute("aria-expanded", String(open));
            store.set("hbchat.side", open ? "1" : "0");
            if (open) { showTab(sideTab); sidePlaceholder("불러오는 중…"); loadSide(); }
        }
        if (side && sideToggle) {
            sideToggle.addEventListener("click", () => setSide(side.hidden));
            side.querySelector(".side-x").addEventListener("click", () => setSide(false));
            side.querySelectorAll(".side-tabs [data-tab]").forEach(b => b.addEventListener("click", () => showTab(b.dataset.tab)));
            // Esc로 닫기 — 좁은 폭에서는 곁칸이 "정보" 단추를 덮는다 (지휘자 K2)
            side.addEventListener("keydown", (e) => { if (e.key === "Escape") { setSide(false); sideToggle.focus(); } });
            // 저장된 "열림"은 넓은 폭에서만 되살린다 — 좁으면 방을 열 때마다 대화를 덮었다 (c216951e79 C5). 미니 창은 숨김.
            if (store.get("hbchat.side") === "1" && wide() && !root.closest(".mini")) setSide(true);
        }

        // 승인 카드 (명령 단계): 서버가 답을 기다리는 동안 대화 흐름 안(live)에 보인다 (2-2 b). 남은 시간은 1초마다 다시 쓴다.
        const cards = new Map();
        function showApproval(a) {
            if (cards.has(a.id)) return;
            const follow = atBottom();
            const card = el("div", "approval");
            const head = el("div", "head");
            const left = el("span"); left.style.marginLeft = "auto";
            head.append(el("span", null, "승인 요청 · " + a.tool), left);
            // 명령 전문(스크롤 칸) — 사용자가 본 것이 허용되는 것과 같게 (/self-review 36e4e20e04 C3 · C5, ab79e61df5 C5).
            card.append(head, el("div", "cmd", a.summary));
            if (a.description) card.append(el("div", "desc", "Claude 설명: " + a.description));
            // 명령 밖의 입력도 허용하면 같이 실행된다 — 본 것 = 허용되는 것 (/self-review 5e747ff851 C1)
            if (a.extra) card.append(el("div", "cmd", "추가 입력: " + a.extra));
            const denyOnly = a.truncated || a.risky;
            if (a.risky) card.append(el("div", "warn-line", "위험한 입력(dangerously · bypass)이 있어 거절만 할 수 있습니다."));
            else if (a.truncated) card.append(el("div", "warn-line", "명령이나 입력이 너무 길어 끝까지 보이지 않습니다 — 거절만 할 수 있습니다."));
            else if (a.rule) {
                const r = el("div", "rule");
                r.append(document.createTextNode("항상 허용하면 저장될 규칙: "), el("code", "mono", a.rule));
                card.append(r, el("div", "warn-line", "항상 허용하면 이 명령은 다음부터 묻지 않습니다 — 이 명령이 돌리는 파일(스크립트 · package.json · 프로젝트 파일 등)이 바뀌어도 그대로 실행됩니다."));
            }
            const acts = el("div", "acts");
            const button = (label, choice, extra) => {
                const b = el("button", "btn" + (extra ? " " + extra : ""), label);
                b.type = "button";
                b.addEventListener("click", () => decide(a.id, choice, card));
                acts.append(b);
                return b;
            };
            if (!denyOnly) {
                button("허용", "allow", "primary");
                if (a.rule) button("이 방에서 항상 허용", "always");
            }
            const reasonBox = el("input", "reason");
            reasonBox.type = "text"; reasonBox.maxLength = 500; reasonBox.placeholder = "거절 이유 (선택, 지휘자에게 전해짐)";
            reasonBox.addEventListener("keydown", (e) => { if (e.key === "Enter" && !e.isComposing) { e.preventDefault(); decide(a.id, "deny", card); } });
            acts.append(reasonBox);
            button("거절", "deny");
            card.append(acts);
            card.dataset.expires = String(Date.parse(a.expiresAt));
            card.left = left;
            live.append(card);
            cards.set(a.id, card);
            tickApprovals();
            if (follow) toBottom();
        }
        function doneApproval(a, label) {
            const card = cards.get(a.id);
            if (!card) return;
            cards.delete(a.id);
            card.replaceWith(el("div", "live-line" + (label === "허용" || label === "항상 허용" ? "" : " denied"),
                "승인 " + (label || "끝남") + " — " + a.tool + ": " + a.summary.slice(0, 120)));
        }
        // 질문 카드 (3차 a): 지휘자의 AskUserQuestion. 질문마다 선택지(하나 · 여러 개) + "기타" 직접 쓰기. 남은 시간은 승인 카드와 같이 다시 쓴다.
        function showQuestion(q) {
            if (cards.has(q.id)) return;
            const follow = atBottom();
            const card = el("div", "approval question-card");
            const head = el("div", "head");
            const left = el("span"); left.style.marginLeft = "auto";
            head.append(el("span", null, "지휘자의 질문"), left);
            card.append(head);
            const reads = [];
            q.questions.forEach((item, qi) => {
                const set = el("fieldset");
                const legend = el("legend");
                if (item.header) legend.append(el("span", "tag", item.header));
                legend.append(document.createTextNode(item.question));
                set.append(legend);
                const name = "q-" + q.id + "-" + qi;
                const inputs = [];
                for (const o of item.options) {
                    const lab = el("label", "opt");
                    const box = el("input");
                    box.type = item.multiSelect ? "checkbox" : "radio"; box.name = name; box.value = o.label;
                    lab.append(box, el("span", null, o.label));
                    if (o.description) lab.append(el("span", "d", o.description));
                    set.append(lab);
                    inputs.push(box);
                }
                // 기타: 글을 쓰면 그 글이 답 (하나 고르기면 선택지 표시를 지운다)
                const otherLab = el("label", "opt");
                const other = el("input", "other");
                other.type = "text"; other.maxLength = 1000; other.placeholder = item.options.length ? "기타 (직접 쓰기)" : "답 쓰기";
                other.addEventListener("input", () => { if (!item.multiSelect && other.value.trim()) inputs.forEach(b => b.checked = false); });
                // 반대로 하나 고르기에서 선택지를 고르면 기타 글을 비운다 — 안 그러면 보이는 선택과 다른 글이 답으로 갔다 (/self-review 7d839f4aa5 C3)
                if (!item.multiSelect) inputs.forEach(b => b.addEventListener("change", () => { if (b.checked) other.value = ""; }));
                otherLab.append(other);
                set.append(otherLab);
                card.append(set);
                reads.push(() => {
                    const picked = inputs.filter(b => b.checked).map(b => b.value);
                    const typed = other.value.trim();
                    if (typed) picked.push(typed);
                    return [item.question, item.multiSelect ? picked.join(", ") : (typed || picked[0] || "")];
                });
            });
            const acts = el("div", "acts");
            const send = el("button", "btn primary", "답하기"); send.type = "button";
            const skip = el("button", "btn", "답하지 않기"); skip.type = "button";
            send.addEventListener("click", async () => {
                const answers = Object.fromEntries(reads.map(r => r()));
                if (Object.values(answers).some(a => !a)) { alert("모든 질문에 답을 고르거나 써 주세요."); return; }
                await answer(q.id, card, answers);
            });
            skip.addEventListener("click", () => answer(q.id, card, null));
            acts.append(send, skip);
            card.append(acts);
            card.dataset.expires = String(Date.parse(q.expiresAt));
            card.left = left;
            live.append(card);
            cards.set(q.id, card);
            tickApprovals();
            if (follow) toBottom();
        }
        async function answer(id, card, answers) {
            const controls = card.querySelectorAll("button, input");
            controls.forEach(b => b.disabled = true);
            const data = new FormData();
            data.append("question", id);
            if (answers) data.append("answers", JSON.stringify(answers)); else data.append("skip", "true");
            if (!await post("Answer", data)) controls.forEach(b => b.disabled = false);
        }
        function doneQuestion(q, text, answered, answers) {
            const card = cards.get(q.id);
            if (!card) return;
            cards.delete(q.id);
            if (!answered || !answers) {
                // q-done: 턴 끝에 세션 기록의 같은 줄(답하지 않음)로 바뀌므로 그때 지운다
                card.replaceWith(el("div", "live-line q-done" + (answered ? "" : " denied"), "질문 " + (answered ? "답함 — " + text : (text || "끝남"))));
                return;
            }
            // 답한 질문은 '나' 말풍선 (4차 D1, 서버 _Messages의 answer 말풍선과 같은 모양)
            const m = el("div", "msg user answer"), b = el("div", "bubble");
            m.dataset.speaker = "user";
            const w = el("div", "who");
            // 서버 말풍선과 같게 시각 · 답장 단추 (e8091295fe C2 — 턴이 끝날 때까지도 인용할 수 있게)
            const now = new Date(), p2 = n => String(n).padStart(2, "0");
            const when = `${p2(now.getMonth() + 1)}-${p2(now.getDate())} ${p2(now.getHours())}:${p2(now.getMinutes())}:${p2(now.getSeconds())}`;
            const reply = el("button", "reply", "답장"); reply.type = "button"; reply.title = "이 답에 답장 (인용)";
            w.append(el("b", null, profiles.user?.name || "나"), " ", el("span", "detail", "질문에 답함"), " ", el("span", "when", when), " ", reply);
            const body = el("div", "body");
            for (const item of q.questions) {
                body.append(el("div", "answer-q", item.question), el("div", "answer-a", answers[item.question] ?? ""));
            }
            b.append(w, body);
            m.append(avatar(profiles.user, "나"), b);
            card.replaceWith(m);
        }
        async function decide(id, choice, card) {
            const reason = choice === "deny" ? card.querySelector("input.reason")?.value.trim() : null;
            const buttons = card.querySelectorAll("button");
            buttons.forEach(b => b.disabled = true);
            const data = new FormData();
            data.append("approval", id); data.append("choice", choice);
            if (reason) data.append("reason", reason);
            if (!await post("Approve", data)) buttons.forEach(b => b.disabled = false);
        }
        function tickApprovals() {
            for (const card of cards.values()) {
                const s = Math.max(0, Math.round((Number(card.dataset.expires) - Date.now()) / 1000));
                card.left.textContent = "남은 시간 " + Math.floor(s / 60) + ":" + String(s % 60).padStart(2, "0");
            }
        }
        const ticker = setInterval(tickApprovals, 1000);
        // 이 방의 "항상 허용" 목록: 처음엔 서버가 그리고, 바뀌면 rules 알림으로 다시 그린다 (/self-review 36e4e20e04 C9).
        function removeButton(rule) {
            const b = el("button", "btn rule-remove", "지우기");
            b.type = "button";
            b.addEventListener("click", async () => {
                if (!confirm("이 규칙을 지울까요? 다음 실행부터 다시 묻습니다.\n" + rule)) return;
                const data = new FormData(); data.append("rule", rule);
                await post("RemoveRule", data); // 목록은 rules 알림으로 바뀐다
            });
            return b;
        }
        function renderRules(rules) {
            const list = $("#room-rules");
            if (!list) return;
            list.replaceChildren(...rules.map(rule => {
                const li = el("li");
                li.append(el("code", "mono", rule), document.createTextNode(" "), removeButton(rule));
                return li;
            }));
            const count = $("#rule-count");
            if (count) count.textContent = String(rules.length);
        }
        root.querySelectorAll(".rule-remove").forEach(b => b.replaceWith(removeButton(b.dataset.rule)));

        // 알림은 온 차례대로 하나씩 처리한다. EventSource는 onmessage가 끝나기를 기다리지 않아,
        // end의 말풍선 다시 받기(fetch) 중에 다음 턴의 start가 먼저 그려졌다가 지워지는 일이 있었다 (/self-review 0a3b1d5155 C1).
        let chain = Promise.resolve();
        const es = new EventSource("/events/" + roomId);
        es.onopen = () => { conn.textContent = ""; };
        es.onerror = () => { if (!disposed) conn.textContent = "연결 끊김 · 다시 시도 중"; };
        es.onmessage = (msg) => {
            const ev = JSON.parse(msg.data);
            chain = chain.then(() => disposed ? null : handle(ev)).catch(() => { });
        };
        async function handle(ev) {
            switch (ev.kind) {
                case "rooms":
                    scheduleList(); // 방 목록 신호 (어느 방이든) — 탭당 SSE 하나라 방 SSE가 같이 싣는다
                    break;
                case "profiles":
                    // 프로필 · 표정이 바뀜 (3차 e): 이름 · 그림 · 표정은 방을 열 때 받으므로 다시 연다 (쓰던 글은 dispose가 남긴다)
                    reopenRoom(roomId);
                    break;
                case "snapshot":
                    // 처음 붙거나 다시 붙을 때: 그사이 끝난 턴이 있을 수 있으니 새 말부터 받는다 (0a3b1d5155 C3).
                    // 끊긴 사이 다른 방이 바뀌었을 수도 있어 목록도 다시 받는다.
                    scheduleList();
                    await refreshMessages();
                    cards.forEach(c => c.remove()); cards.clear(); // 기다리는 카드는 snapshot 뒤 approval로 다시 온다
                    if (ev.busy) startTurn(ev.prompt, ev.text, ev.pendingItems);
                    else { clearLive(); stream = null; chips = null; setBusy(false); showQueue([]); }
                    if (ev.face) setFace(ev.face);
                    renderSteps(ev.steps);
                    if (ev.status) showStatus(ev.status);
                    loadSide(); // 끊긴 사이 끝난 턴은 end가 오지 않는다 — 비용 · 외부 실행을 새로 (C3)
                    break;
                case "face":
                    setFace(ev.face, ev.face === "Tool" && ev.text ? shortTool(ev.text) : null);
                    break;
                case "approval":
                    showApproval(ev.approval);
                    break;
                case "approval-done":
                    doneApproval(ev.approval, ev.text);
                    break;
                case "question":
                    showQuestion(ev.question);
                    break;
                case "question-done":
                    doneQuestion(ev.question, ev.text, ev.ok === true, ev.answers);
                    break;
                case "rules":
                    renderRules(ev.rules || []);
                    loadSide(); // 곁칸의 항상 허용 수 (c216951e79 C3)
                    break;
                case "start":
                    $("#last-error")?.remove();
                    notice.hidden = true;
                    startTurn(ev.prompt, "", ev.pendingItems);
                    break;
                case "delta": {
                    if (!stream) startTurn(null, "", null);
                    const follow = atBottom();
                    if (stream.dataset.empty) { stream.textContent = ""; delete stream.dataset.empty; }
                    stream.append(ev.text); // 글 조각을 텍스트 노드로 덧붙인다 (+= 는 매번 전체를 다시 만든다 — C11)
                    if (follow) toBottom();
                    break;
                }
                case "tool":
                    // 칩만 — 표정은 서버 face 알림 · snapshot만 따른다. snapshot 뒤 다시 보내는 tool이 승인 기다림 · 답 쓰는 중을
                    // "도구 실행 중"으로 덮던 문제 (/self-review 83b453653b C1). 대상(파일 이름 · 명령 첫 줄)이 있으면 같이 (3차 b).
                    if (!stream) startTurn(null, "", null);
                    addChip(shortTool(ev.text) + (ev.detail ? " " + ev.detail : ""));
                    break;
                case "steps":
                    renderSteps(ev.steps);
                    break;
                case "status":
                    showStatus(ev.status);
                    break;
                case "denied":
                case "retry":
                    live.append(el("div", "live-line " + ev.kind, (ev.kind === "denied" ? "거절됨 — " : "") + ev.text));
                    break;
                case "queued":
                    showQueue(ev.pendingItems);
                    break;
                case "notice":
                    notice.textContent = ev.text; notice.hidden = false;
                    break;
                case "end": {
                    // 이 턴의 말은 세션 기록으로 다시 그린다. 다시 받지 못하면 진행 중 말풍선을 남겨 둔다 (지휘자 검토 R7).
                    // 실패 · 중지면 이유를 한 줄 남긴다.
                    const refreshed = await refreshMessages();
                    // 보내지 못한 말은 남김 (C2). 질문 답 말풍선은 세션 기록의 것으로 바뀌므로 지운다 (4차 D1).
                    // 답하지 않은 질문 줄(q-done)은 성공으로 끝난 턴에서만 — 중지 · 실패면 기록에 결과 줄이 없을 수 있어 남긴다 (e8091295fe C4)
                    if (refreshed) live.querySelectorAll(":scope > .msg:not(.failed)" + (ev.ok ? ", :scope > .q-done" : "")).forEach(m => m.remove());
                    else live.prepend(el("div", "live-line denied", "말풍선을 다시 받지 못했습니다 — 새로고침하면 보입니다."));
                    stream = null; chips = null;
                    if (!ev.ok && ev.text) live.prepend(el("div", "live-line denied", ev.text));
                    // claude를 띄우기 전에 막힌 말 (4차 C2): 세션 기록에 없어 말풍선이 사라지므로 '보내지 못함'으로 남기고
                    // 입력칸이 비었으면 글을 돌려준다 (끝의 [첨부] 줄은 빼고 — 파일은 다시 붙인다)
                    if (!ev.ok && ev.prompt) {
                        live.querySelectorAll(":scope > .msg.pending").forEach(m => m.remove()); // 다시 받기 실패 때 같은 말이 두 번 보이지 않게 (C5)
                        bubble("user failed", (profiles.user?.name || "나") + " · 보내지 못함", displayText(ev.prompt));
                        const prompt = ev.prompt.replace(/\r\n/g, "\n");
                        // 입력칸에는 이 탭에서 보낸 말일 때만 돌려준다 — 같은 방을 연 다른 탭까지 채우면 두 번 보낼 수 있다 (C6)
                        const mine = sentTexts.indexOf(prompt);
                        if (mine >= 0) {
                            sentTexts.splice(mine, 1);
                            const lines = prompt.split("\n");
                            while (lines.length && (!lines[lines.length - 1].trim() || lines[lines.length - 1].startsWith("[첨부] "))) lines.pop();
                            if (!textBox.value) textBox.value = lines.join("\n");
                        }
                    }
                    if (ev.costUsd != null) { const c = $("#cost"); if (c) c.textContent = "≈$" + ev.costUsd.toFixed(3); }
                    showContext(ev.contextTokens, ev.contextWindow);
                    if (ev.face) setFace(ev.face);
                    loadSide(); // 곁칸이 열려 있으면 비용 · 컨텍스트 · 외부 실행 · 첨부를 새로 (3차 c)
                    break;
                }
                case "idle":
                    setBusy(false);
                    showQueue([]);
                    break;
            }
        }

        // json이면 응답 JSON(실패면 null), 아니면 성공 여부
        async function post(handler, data, quiet, json) {
            data.append("__RequestVerificationToken", token);
            try {
                const res = await fetch(self + "?handler=" + handler, { method: "POST", body: data });
                if (!res.ok) { if (!quiet) alert(await res.text() || ("요청 실패 " + res.status)); return json ? null : false; }
                return json ? await res.json() : true;
            } catch (e) {
                if (!quiet) alert("요청 실패: " + e);
                return json ? null : false;
            }
        }

        // 검색 결과의 말까지 앞 쪽을 받아 가운데에 보이고 잠깐 강조한다 (2-2 c)
        // 앞 쪽을 받지 못했거나(실패) 더 나아가지 않으면 멈춘다 (95e8b1c242 C5).
        let revealing = false;
        async function reveal(line) {
            revealing = true;
            try {
                for (let i = 0; i < 200 && !disposed; i++) {
                    const chat = chatEl();
                    const target = chat.querySelector(`.msg[data-line="${line}"], .system[data-line="${line}"]`);
                    if (target) {
                        target.scrollIntoView({ block: "center" });
                        target.classList.add("hit");
                        setTimeout(() => target.classList.remove("hit"), 2500);
                        return;
                    }
                    if (chat.dataset.hasMore !== "1" || Number(chat.dataset.firstLine) <= line) return;
                    const before = chat.dataset.firstLine;
                    if (!await loadOlder() || chatEl().dataset.firstLine === before) return;
                }
            } finally { revealing = false; }
        }
        // ---- 첨부 (2-2 d): 고르면 바로 그 방 첨부 폴더에 올리고(?handler=Attach), 보낼 때 본문 끝에 [첨부] 경로 줄로 붙인다 ----
        const composer = $(".composer");
        const canAttach = composer.dataset.attach === "1";
        const attachMax = Number(composer.dataset.attachMax || 10), attachBytes = Number(composer.dataset.attachBytes || 0);
        const attachBox = $("#attach-chips");
        let attached = []; // { name, path, url, image }
        let uploading = 0; // 올리는 중인 파일 수 — 그동안은 보내지 않는다 (/self-review 5b26cff7df C2)
        function renderAttached() {
            attachBox.replaceChildren();
            attachBox.hidden = attached.length === 0 && uploading === 0;
            for (const a of attached) {
                const chip = el("span", "chip attach-chip");
                if (a.image) { const img = el("img"); img.src = a.url; img.alt = ""; chip.append(img); }
                chip.append(el("span", "t", a.name)); // 글은 줄임표, ×는 줄지 않게 (C1)
                const x = el("button", "queue-x", "×");
                x.type = "button"; x.title = "첨부 빼기 (올린 파일은 방 첨부 폴더에 남음)";
                x.addEventListener("click", () => { attached = attached.filter(b => b !== a); renderAttached(); });
                chip.append(x);
                attachBox.append(chip);
            }
            if (uploading > 0) attachBox.append(el("span", "muted", "올리는 중 " + uploading + "개…"));
        }
        async function upload(files) {
            // 막힌 이유는 서버가 준다 — 대화 단계 · 휴지통 (C8)
            if (!canAttach) { if (files.length) alert(composer.dataset.attachWhy || "이 방은 첨부를 받지 않습니다."); return; }
            for (const f of files) {
                if (attached.length + uploading >= attachMax) { alert("첨부는 한 말에 " + attachMax + "개까지입니다."); return; }
                if (attachBytes && f.size > attachBytes) { alert(f.name + ": 파일 하나는 " + Math.round(attachBytes / 1048576) + "MB까지입니다."); continue; }
                const data = new FormData();
                // 캡처 붙여 넣기는 이름이 image.png 같은 것뿐이라 그대로 두면 시각_image.png로 저장된다
                data.append("file", f, f.name || "capture.png");
                uploading++; renderAttached();
                try {
                    const res = await post("Attach", data, false, true);
                    if (res?.path && !disposed) { attached = [...attached, res]; loadSide(); } // 곁칸 첨부 탭 (C3)
                } finally {
                    uploading--;
                    if (!disposed) renderAttached();
                }
            }
        }
        if (saved?.attached?.length) { attached = saved.attached; renderAttached(); } // 방을 바꿨다 돌아온 초안
        if (canAttach) {
            const input = $("#attach-input");
            $("#attach-btn").addEventListener("click", () => input.click());
            input.addEventListener("change", () => { upload([...input.files]); input.value = ""; });
        }
        // 끌어 놓기: 방 칸 어디에 놓아도
        root.addEventListener("dragover", (e) => { if (e.dataTransfer?.types?.includes("Files")) { e.preventDefault(); root.classList.add("dropping"); } });
        root.addEventListener("dragleave", (e) => { if (e.target === root || !root.contains(e.relatedTarget)) root.classList.remove("dropping"); });
        root.addEventListener("drop", (e) => {
            root.classList.remove("dropping");
            if (!e.dataTransfer?.files?.length) return;
            e.preventDefault();
            upload([...e.dataTransfer.files]);
        });
        // 캡처 붙여 넣기: 클립보드에 파일(그림)만 있으면 올린다. 글자가 같이 있으면(Excel · Word 복사 등) 보통 붙여 넣기 (C3)
        textBox.addEventListener("paste", (e) => {
            const cd = e.clipboardData;
            const files = [...(cd?.files || [])];
            if (files.length === 0 || [...(cd?.types || [])].some(t => t === "text/plain" || t === "text/html")) return;
            e.preventDefault();
            upload(files);
        });

        // 보내기 · 끼어들기 (2-2 d) 공통: 인용 블록 + 글 + 첨부 줄. 보내기 전에 입력칸을 바로 비운다 —
        // 응답을 기다리는 사이 이어 쓴 글이 앞 글에 붙어 같이 나가지 않게 (실제 확인에서 발견). 실패하면 비어 있을 때만 되돌린다.
        // 인용 · 첨부는 보낸 뒤에만 지운다.
        async function sendNow(handler) {
            const text = textBox.value;
            if (!text.trim() && attached.length === 0) return;
            // 올리는 중에 보내면 그 파일이 다음 말로 넘어간다 — 다 올라간 뒤에 보낸다 (C2)
            if (uploading > 0) { alert("첨부를 올리는 중입니다 — 다 올라간 뒤 보내세요."); return; }
            const q = quote, files = [...attached]; // 보낸 것만 나중에 뺀다 (그사이 바뀌어도)
            textBox.value = "";
            let full = q ? quoteBlock(q) + "\n\n" + text : text;
            if (files.length) full = (full.trim() ? full.replace(/\s+$/, "") + "\n\n" : "") + files.map(a => "[첨부] " + a.path).join("\n");
            const data = new FormData(); data.append("text", full);
            // 이 탭이 보낸 말 (막혀서 돌아오면 입력칸에 돌려줄지 판단 — bd52c6f0d1 C6). 오래된 것은 버린다
            sentTexts.push(full.replace(/\r\n/g, "\n"));
            if (sentTexts.length > 20) sentTexts.shift();
            if (await post(handler, data)) {
                if (quote === q) setQuote(null);
                attached = attached.filter(a => !files.includes(a));
                if (!disposed) renderAttached();
                // 보내는 사이 방을 바꿨으면 dispose가 이 인용 · 첨부를 초안에 넣었다 — 이미 보냈으니 뺀다 (83b453653b C7)
                const d = drafts.get(roomId);
                if (disposed && d) {
                    if (d.quote === q) d.quote = null;
                    if (d.attached) d.attached = d.attached.filter(a => !files.includes(a));
                    if (!d.text && !d.quote && !d.attached?.length) drafts.delete(roomId);
                }
                return;
            }
            // 그사이 방을 바꿨으면 이 입력칸은 이미 떨어져 나갔다 — 그 방의 초안으로 되돌린다 (C6).
            if (disposed) { if (!drafts.get(roomId)?.text) drafts.set(roomId, { text, quote: q, attached: files }); }
            else if (!textBox.value) textBox.value = text;
        }
        form.addEventListener("submit", (e) => { e.preventDefault(); sendNow("Send"); });
        interruptBtn.addEventListener("click", () => {
            if (!textBox.value.trim() && attached.length === 0) { textBox.focus(); return; }
            sendNow("Interrupt");
        });
        textBox.addEventListener("keydown", (e) => {
            // 한글 조합 중 Enter는 글자 확정이다 — 보내지 않는다.
            if (e.key === "Enter" && !e.shiftKey && !e.isComposing) { e.preventDefault(); form.requestSubmit(); }
        });
        stopBtn.addEventListener("click", () => post("Stop", new FormData()));

        decorate(root);
        toBottom();
        fillUp();
        return {
            id: roomId,
            reveal,
            dispose() {
                disposed = true;
                if (textBox.value || quote || attached.length) drafts.set(roomId, { text: textBox.value, quote, attached });
                es.close();
                clearInterval(ticker);
                clearTimeout(readTimer);
                clearTimeout(faceTimer);
                document.removeEventListener("visibilitychange", markRead);
                document.removeEventListener("click", closeMenu);
            },
        };
    }

    // 설정 · 프로필 그림 (4차 B2): 고른 그림을 올리기 전에 브라우저에서 줄인다 (긴 변 256px, webp) — 아바타는 작게만 보이고
    // 큰 원본은 본문 상한(1MB)에 걸린다. 줄이지 못하면(그림이 아님 · 캔버스 실패) 원본 그대로 보내 서버가 형식 · 크기를 본다.
    const AvatarMaxPx = 256, AvatarKeepBytes = 256 * 1024;
    async function shrinkImage(file) {
        const bitmap = await createImageBitmap(file);
        try {
            const scale = Math.min(1, AvatarMaxPx / Math.max(bitmap.width, bitmap.height));
            if (scale === 1 && file.size <= AvatarKeepBytes) return null; // 작으면 그대로
            const canvas = document.createElement("canvas");
            canvas.width = Math.max(1, Math.round(bitmap.width * scale));
            canvas.height = Math.max(1, Math.round(bitmap.height * scale));
            canvas.getContext("2d").drawImage(bitmap, 0, 0, canvas.width, canvas.height);
            const blob = await new Promise(resolve => canvas.toBlob(resolve, "image/webp", 0.9));
            return blob?.type === "image/webp" ? new File([blob], "avatar.webp", { type: "image/webp" }) : null;
        } finally {
            bitmap.close();
        }
    }
    document.querySelectorAll("form.settings-profile").forEach(form => {
        const input = form.querySelector("input[type=file][name=image]");
        // 그림을 새로 고르면 다시 줄인다 — 뒤로 가기(bfcache)로 돌아온 화면에 앞 표시가 남아 원본이 올라가지 않게 (dc27a724bf C5)
        input?.addEventListener("change", () => { delete form.dataset.shrunk; });
        form.addEventListener("submit", async (e) => {
            const file = input?.files?.[0];
            if (!file || form.dataset.shrunk === "1") return;
            e.preventDefault();
            if (form.dataset.shrinking === "1") return; // 줄이는 중에 또 누름 — 한 번만 보낸다
            form.dataset.shrinking = "1";
            try {
                const small = await shrinkImage(file);
                if (small) { const dt = new DataTransfer(); dt.items.add(small); input.files = dt.files; }
            } catch { /* 원본 그대로 */ }
            delete form.dataset.shrinking;
            form.dataset.shrunk = "1"; // 다시 부르는 submit은 그냥 보낸다 (그림을 새로 고르면 지워짐)
            form.requestSubmit();
        });
    });

    const first = pane?.querySelector(".room");
    if (first) room = mountRoom(first);
    else openAppEvents();
})();
