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
                old.replaceWith(fresh);
                fresh.scrollTop = top;
                // 요청을 보낸 뒤 방을 바꿨을 수 있다 — 서버가 그린 강조 대신 지금 방으로 (/self-review 5225b1ffaf C3)
                markActive(room?.id ?? null);
            }
        } catch { /* 다음 알림 때 다시 */ }
    }
    function scheduleList() { clearTimeout(listTimer); listTimer = setTimeout(refreshList, 250); }
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
        const a = e.target.closest("a.room-item");
        if (!a || e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey || e.altKey) return;
        e.preventDefault();
        openRoom(a.dataset.room, true);
    });
    window.addEventListener("popstate", () => {
        const m = location.pathname.match(/^\/Room\/([0-9a-f]{12})$/);
        if (m) openRoom(m[1], false); else location.reload();
    });

    // 마지막으로 누른 방만 붙인다: 먼저 보낸 요청이 늦게 와도 버린다 (/self-review 5225b1ffaf C2).
    let openSeq = 0;
    async function openRoom(id, push) {
        const seq = ++openSeq;
        if (room?.id === id) { app.classList.add("in-room"); return; }
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
        }
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
    // 코드 블록 복사 단추 (2-2 b). 글자는 CSS ::after로 넣는다 — 인용 답장이 innerText로 본문을 읽을 때 섞이지 않게.
    function decorate(container) {
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

        root.addEventListener("click", async (e) => {
            if (e.target.closest(".older")) { loadOlder(); return; }
            const reply = e.target.closest(".reply");
            if (reply) {
                const msg = reply.closest(".msg");
                const body = msg?.querySelector(".body");
                if (!body) return;
                const text = body.innerText.trim();
                setQuote({
                    who: msg.querySelector(".who b")?.textContent || "",
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
        let loadingOlder = false;
        async function loadOlder() {
            const chat = chatEl();
            if (loadingOlder || disposed || chat?.dataset.hasMore !== "1") return;
            loadingOlder = true;
            try {
                const res = await fetch(self + "?handler=Messages&before=" + chat.dataset.firstLine, { cache: "no-store" });
                if (!res.ok || disposed) return;
                const fresh = parseChat(await res.text());
                if (!fresh || disposed || chatEl() !== chat) return;
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
            } catch { /* 다음 스크롤 · 안내 누르기 때 다시 */ } finally { loadingOlder = false; }
        }
        // 칸이 다 차지 않으면 스크롤이 생기지 않아 위로 올릴 수 없다 — 찰 때까지 앞 쪽을 이어 받는다 (83b453653b C8).
        function fillUp() {
            if (!disposed && scroller.scrollHeight <= scroller.clientHeight + 300) loadOlder();
        }

        let readTimer = null;
        const onScroll = () => {
            if (scroller.scrollTop < 300) loadOlder();
            clearTimeout(readTimer); readTimer = setTimeout(markRead, 300);
        };
        scroller.addEventListener("scroll", onScroll);
        document.addEventListener("visibilitychange", markRead);

        // 진행 중 턴: 보낸 말(흐리게) + 지휘자 말풍선(입력 중 점 → 흘림 글, 위에 도구 칩) + 승인 카드 · 거절 줄 + 대기 줄.
        // 흘림 글은 글자 그대로이고, 끝나면 세션 기록의 말풍선으로 바뀐다.
        let stream = null, chips = null, queueLine = null;
        function bubble(cls, who, text) {
            const m = el("div", "msg " + cls), b = el("div", "bubble");
            m.append(el("div", "avatar", cls.startsWith("user") ? "나" : "C"), b);
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
        }
        function showQueue(pending) {
            if (!pending || pending.length === 0) { queueLine?.remove(); queueLine = null; return; }
            if (!queueLine) { queueLine = el("div", "live-line"); }
            queueLine.textContent = "기다리는 말 " + pending.length + "개";
            live.append(queueLine);
        }
        function startTurn(prompt, partial, pending) {
            const follow = atBottom();
            live.replaceChildren();
            cards.clear(); // 카드도 live 안에 있었다
            chips = null; queueLine = null;
            if (prompt) bubble("user pending", "나", prompt);
            stream = bubble("conductor", "지휘자", partial || null);
            if (!partial) { stream.dataset.empty = "1"; stream.append(typing()); }
            stopBtn.disabled = false;
            showQueue(pending);
            if (follow) toBottom();
        }
        // 턴이 끝난 뒤: 마지막으로 본 줄 뒤의 새 말만 받아 붙인다. 기록이 다시 쓰였으면(줄이 줄었으면) 쪽 전체를 다시 받는다.
        async function refreshMessages() {
            const follow = atBottom();
            const chat = chatEl();
            // 읽기 실패로 그린 쪽은 기준 줄을 모른다 — 쪽 전체를 다시 받는다 (83b453653b C6)
            if (chat.dataset.error) return await reloadMessages(follow);
            try {
                const res = await fetch(self + "?handler=Messages&after=" + encodeURIComponent(chat.dataset.lastLine), { cache: "no-store" });
                if (!res.ok || disposed) return false;
                const fresh = parseChat(await res.text());
                if (!fresh || disposed || chatEl() !== chat) return false;
                if (Number(fresh.dataset.lastLine) < Number(chat.dataset.lastLine)) return await reloadMessages(follow);
                if (fresh.firstElementChild) {
                    chat.querySelector(".empty")?.remove();
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
            if (a.truncated) card.append(el("div", "warn-line", "명령이 너무 길어 끝까지 보이지 않습니다 — 거절만 할 수 있습니다."));
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
            if (!a.truncated) {
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
            live.insertBefore(card, queueLine);
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
                case "snapshot":
                    // 처음 붙거나 다시 붙을 때: 그사이 끝난 턴이 있을 수 있으니 새 말부터 받는다 (0a3b1d5155 C3).
                    // 끊긴 사이 다른 방이 바뀌었을 수도 있어 목록도 다시 받는다.
                    scheduleList();
                    await refreshMessages();
                    cards.forEach(c => c.remove()); cards.clear(); // 기다리는 카드는 snapshot 뒤 approval로 다시 온다
                    if (ev.busy) startTurn(ev.prompt, ev.text, ev.pending);
                    else { live.replaceChildren(); stream = null; chips = null; queueLine = null; stopBtn.disabled = true; }
                    if (ev.face) setFace(ev.face);
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
                case "rules":
                    renderRules(ev.rules || []);
                    break;
                case "start":
                    $("#last-error")?.remove();
                    notice.hidden = true;
                    startTurn(ev.prompt, "", ev.pending);
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
                    // "도구 실행 중"으로 덮던 문제 (/self-review 83b453653b C1).
                    if (!stream) startTurn(null, "", null);
                    addChip(shortTool(ev.text));
                    break;
                case "denied":
                case "retry":
                    live.insertBefore(el("div", "live-line " + ev.kind, (ev.kind === "denied" ? "거절됨 — " : "") + ev.text), queueLine);
                    break;
                case "queued":
                    showQueue(ev.pending);
                    break;
                case "notice":
                    notice.textContent = ev.text; notice.hidden = false;
                    break;
                case "end": {
                    // 이 턴의 말은 세션 기록으로 다시 그린다. 다시 받지 못하면 진행 중 말풍선을 남겨 둔다 (지휘자 검토 R7).
                    // 실패 · 중지면 이유를 한 줄 남긴다.
                    const refreshed = await refreshMessages();
                    if (refreshed) live.querySelectorAll(".msg").forEach(m => m.remove());
                    else live.prepend(el("div", "live-line denied", "말풍선을 다시 받지 못했습니다 — 새로고침하면 보입니다."));
                    stream = null; chips = null;
                    if (!ev.ok && ev.text) live.prepend(el("div", "live-line denied", ev.text));
                    if (ev.costUsd != null) { const c = $("#cost"); if (c) c.textContent = "≈$" + ev.costUsd.toFixed(3); }
                    showContext(ev.contextTokens, ev.contextWindow);
                    if (ev.face) setFace(ev.face);
                    break;
                }
                case "idle":
                    stopBtn.disabled = true;
                    showQueue([]);
                    break;
            }
        }

        async function post(handler, data, quiet) {
            data.append("__RequestVerificationToken", token);
            try {
                const res = await fetch(self + "?handler=" + handler, { method: "POST", body: data });
                if (!res.ok) { if (!quiet) alert(await res.text() || ("요청 실패 " + res.status)); return false; }
                return true;
            } catch (e) {
                if (!quiet) alert("요청 실패: " + e);
                return false;
            }
        }
        form.addEventListener("submit", async (e) => {
            e.preventDefault();
            const text = textBox.value;
            if (!text.trim()) return;
            // 보내기 전에 바로 비운다: 응답을 기다리는 사이 이어 쓴 글이 앞 글에 붙어 같이 나가지 않게 (실제 확인에서 발견).
            // 실패하면 비어 있을 때만 되돌린다. 인용은 보낸 뒤에만 지운다.
            const q = quote;
            textBox.value = "";
            const data = new FormData(); data.append("text", q ? quoteBlock(q) + "\n\n" + text : text);
            if (await post("Send", data)) {
                if (quote === q) setQuote(null);
                // 보내는 사이 방을 바꿨으면 dispose가 이 인용을 초안에 넣었다 — 이미 보냈으니 뺀다 (83b453653b C7)
                const d = drafts.get(roomId);
                if (disposed && d?.quote === q) { d.quote = null; if (!d.text) drafts.delete(roomId); }
                return;
            }
            // 그사이 방을 바꿨으면 이 입력칸은 이미 떨어져 나갔다 — 그 방의 초안으로 되돌린다 (C6).
            if (disposed) { if (!drafts.get(roomId)?.text) drafts.set(roomId, { text, quote: q }); }
            else if (!textBox.value) textBox.value = text;
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
            dispose() {
                disposed = true;
                if (textBox.value || quote) drafts.set(roomId, { text: textBox.value, quote });
                es.close();
                clearInterval(ticker);
                clearTimeout(readTimer);
                clearTimeout(faceTimer);
                document.removeEventListener("visibilitychange", markRead);
            },
        };
    }

    const first = pane?.querySelector(".room");
    if (first) room = mountRoom(first);
    else openAppEvents();
})();
