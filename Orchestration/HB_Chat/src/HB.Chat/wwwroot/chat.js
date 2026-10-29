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
    // 아이콘 (4차 D3): 서버 Icons.cs와 같은 모음 (_Layout body data-icons). 꾸밈이라 aria-hidden — 이름은 단추의 글 · aria-label
    const iconPaths = (() => { try { return JSON.parse(document.body.dataset.icons || "{}"); } catch { return {}; } })();
    function icon(name) {
        const ns = "http://www.w3.org/2000/svg";
        const svg = document.createElementNS(ns, "svg");
        svg.setAttribute("class", "icon icon-" + name); svg.setAttribute("viewBox", "0 0 24 24");
        svg.setAttribute("aria-hidden", "true"); svg.setAttribute("focusable", "false");
        const path = document.createElementNS(ns, "path"); path.setAttribute("d", iconPaths[name] || "");
        svg.append(path);
        return svg;
    }
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
    // 터미널 곁칸 (계획서 2026-10-29_HB_Chat-터미널-곁칸): xterm.js 5.5.0 + addon-fit 0.10.0 (wwwroot/lib 고정 판)은 터미널 탭을 처음 열 때 한 번만 불러온다.
    let xtermLoading = null;
    function loadXterm() {
        if (window.Terminal && window.FitAddon) return Promise.resolve();
        return xtermLoading ??= new Promise((ok, fail) => {
            const css = document.createElement("link");
            css.rel = "stylesheet"; css.href = "/lib/xterm.css";
            document.head.append(css);
            const add = (src) => new Promise((res, rej) => { const s = document.createElement("script"); s.src = src; s.onload = res; s.onerror = rej; document.head.append(s); });
            add("/lib/xterm.js").then(() => add("/lib/addon-fit.js")).then(ok, (e) => { xtermLoading = null; fail(e); });
        });
    }
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
        // 슬라이드 인쇄용 틀이 인쇄를 마쳤다 — 보낸 창이 그 틀일 때만 지운다
        if (e.data !== null && typeof e.data === "object" && e.data.hbPrintDone === true) {
            for (const f of document.querySelectorAll("iframe.slides-print-frame")) if (f.contentWindow === e.source) f.remove();
            return;
        }
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
    // ---- 차트 시안 (계획서 Orchestration/HB_Chat/docs/plans/2026-10-29_HB_Chat-차트-시안.md) ----
    // 지휘자 말풍선의 ```chart 코드 블록(Chart.js 설정 JSON)을 앱에 넣어 둔 Chart.js(wwwroot/lib, 4.6.1)로 그린다.
    // 지휘자 스크립트는 없다: 틀은 앱이 만들고, 데이터는 JSON 블록으로만 들어간다 (< > & 를 \u 이스케이프 — 글이 태그를 닫지 못하게).
    // 격리는 위젯과 같다 — srcdoc, allow-same-origin 없음, 같은 문서 CSP(네트워크 막음). 결정 "바로 그림"이라 처음부터 allow-scripts.
    // 바로 그리는 차트는 말풍선 하나에 ChartAutoMax개까지, 넘으면 "그리기"를 눌러야 — 차트마다 210KB 틀이라 수백 개로 화면이 멈추지 않게 (f8602c9ccd C1)
    const ChartMaxChars = 50000, ChartHeight = 320, ChartAutoMax = 6;
    // 설정 JSON에서 버릴 키: 틀 스크립트의 Object.assign이 __proto__를 setter로 타지 않게 (Chart.js 4.6.1 병합은 이미 거름, f8602c9ccd C2)
    const ChartDropKeys = new Set(["__proto__", "constructor", "prototype"]);
    const ChartTypes = new Set(["bar", "line", "pie", "doughnut", "radar", "polarArea", "scatter", "bubble"]);
    // 앱이 내주는 라이브러리 글 (wwwroot/lib) — 처음 쓸 때 한 번 받는다, 실패하면 다음에 다시
    const libCache = new Map();
    function loadLib(name) {
        if (!libCache.has(name))
            libCache.set(name, fetch("/lib/" + name, { credentials: "omit" })
                .then(r => { if (!r.ok) throw new Error(String(r.status)); return r.text(); })
                .catch(e => { libCache.delete(name); throw e; }));
        return libCache.get(name);
    }
    const loadChartLib = () => loadLib("chart.umd.min.js");
    // 틀 안에 인라인으로 붙이는 라이브러리 글: 닫는 태그가 틀을 일찍 닫지 못하게
    const inlineScript = code => "<script>" + code.replace(/<\/script/gi, "<\\/script") + "<\/script>";
    const inlineStyle = css => "<style>" + css.replace(/<\/style/gi, "<\\/style") + "</style>";
    // 겹친 방어: 틀 스크립트가 그리기 전에 WebRTC 생성자를 지운다 — CSP · sandbox로 못 막는 전송 길 (완전하지 않음, f8602c9ccd C2)
    const NoWebRtc = "for(const k of[\"RTCPeerConnection\",\"webkitRTCPeerConnection\",\"RTCDataChannel\"]){try{delete window[k];}catch(e){}try{window[k]=undefined;}catch(e){}}";
    const isPlainObject = v => v !== null && typeof v === "object" && !Array.isArray(v);
    const isChart = v => isPlainObject(v) && ChartTypes.has(v.type);
    const parseData = source => JSON.parse(source, (k, v) => ChartDropKeys.has(k) ? undefined : v);
    function chartConfig(source) {
        if (source.length > ChartMaxChars) return { error: `차트 데이터가 너무 커서(${source.length.toLocaleString()}자) 코드로만 보입니다.` };
        let config;
        try { config = parseData(source); } catch { return { error: "차트 JSON을 읽지 못해 코드로만 보입니다." }; }
        if (!isChart(config)) return { error: "차트 종류(type)가 없거나 지원하지 않는 종류라 코드로만 보입니다." };
        return { config };
    }
    // 이 말풍선에서 바로 그릴 차례인지 (차트 · 슬라이드를 같이 센다, 그릴 수 있는 것만) — 넘으면 "그리기" 단추
    function autoDrawSlot(box) {
        const msg = box.closest(".msg");
        const nth = Number(msg.dataset.sheets || 0) + 1;
        msg.dataset.sheets = String(nth);
        return nth <= ChartAutoMax;
    }
    // <script> 안에 넣을 JSON: 글이 </script>나 <!--로 틀을 깨지 못하게 < > & 와 줄 구분 문자를 \u 이스케이프 (JSON.parse로는 같은 값)
    function scriptJson(value) {
        return JSON.stringify(value).replace(/</g, "\\u003c").replace(/>/g, "\\u003e").replace(/&/g, "\\u0026")
            .replace(/\u2028/g, "\\u2028").replace(/\u2029/g, "\\u2029");
    }
    function chartTheme() {
        const s = getComputedStyle(document.documentElement), v = name => s.getPropertyValue(name).trim();
        return { text: v("--text") || "#888", line: v("--line") || "#8884", panel: v("--panel") || "transparent" };
    }
    function chartDoc(lib, config, theme) {
        return "<!doctype html><html><head><meta charset=\"utf-8\">" +
            "<meta http-equiv=\"Content-Security-Policy\" content=\"" + WidgetCsp + "\">" +
            "<style>html,body{margin:0}body{padding:8px;font:13px system-ui,\"Malgun Gothic\",sans-serif}" +
            ".box{position:relative;height:" + ChartHeight + "px}.err{white-space:pre-wrap}</style>" +
            inlineScript(lib) +
            "<script type=\"application/json\" id=\"hb-chart\">" + scriptJson({ config, theme }) + "<\/script>" +
            "</head><body><div class=\"box\"><canvas></canvas></div>" +
            // 그리기 실패(설정 오류)는 틀 안에 글로만. 높이 알림은 위젯과 같은 메시지 — 부모가 보낸 창 · 상한을 확인한다
            "<script>(()=>{const d=JSON.parse(document.getElementById(\"hb-chart\").textContent),c=d.config,t=d.theme;" +
            // 틀 문서는 color-scheme이 부모(다크)와 달라 흰 바탕을 칠한다 — 앱 패널 색으로 (실제 확인: 흰 바탕에 밝은 글자)
            "document.documentElement.style.background=t.panel;document.body.style.color=t.text;" +
            NoWebRtc +
            "try{Chart.defaults.color=t.text;Chart.defaults.borderColor=t.line;Chart.defaults.animation=false;" +
            // devicePixelRatio는 앱이 정한다 — 큰 값으로 캔버스 메모리를 키우지 못하게 (f8602c9ccd C1)
            "c.options=Object.assign({},c.options,{responsive:true,maintainAspectRatio:false,devicePixelRatio:Math.min(window.devicePixelRatio||1,2)});" +
            "new Chart(document.querySelector(\"canvas\"),c);}" +
            "catch(e){const p=document.createElement(\"div\");p.className=\"err\";p.textContent=\"차트를 그리지 못했습니다: \"+e.message;" +
            "document.querySelector(\".box\").replaceWith(p);}" +
            // 0은 보내지 않는다 — 숨긴 틀 · 배치 전 틀이 0을 보내면 부모가 최소 높이로 줄이고, 그 뒤 실제 높이가 안 오면 잘린 채 남았다 (실제 확인)
            "const s=()=>{const h=Math.ceil(document.body.getBoundingClientRect().height);if(h>0)parent.postMessage({hbWidgetHeight:h},\"*\");};" +
            "s();new ResizeObserver(s).observe(document.body);})()<\/script></body></html>";
    }
    // ---- 차트 하나만 인쇄 · PDF (계획서 Orchestration/HB_Chat/docs/plans/2026-10-29_HB_Chat-차트-PDF.md) ----
    // 인쇄할 때만 밝은 색으로 그린 틀을 화면 틀 자리에 잠깐 바꿔 넣고(화면 틀은 숨김), 다 그려지면 window.print().
    // 인쇄 창에서 "PDF로 저장"을 고른다 — 데스크톱 껍데기의 페이지 → 호스트 통로는 그대로 닫혀 있다. afterprint에서 되돌린다.
    const ChartPrintTheme = { text: "#1d2330", line: "#d0d4dc", panel: "#ffffff" };
    function chartFrame(lib, config, theme, cls) {
        const frame = el("iframe", "widget-frame running chart-frame" + (cls ? " " + cls : ""));
        frame.setAttribute("sandbox", "allow-scripts");
        frame.setAttribute("referrerpolicy", "no-referrer");
        frame.title = "차트";
        frame.srcdoc = chartDoc(lib, config, theme);
        return frame;
    }
    function clearChartPrint() {
        document.documentElement.classList.remove("print-one");
        document.querySelectorAll(".widget.chart.print-target").forEach(box => {
            box.classList.remove("print-target");
            box.querySelectorAll("iframe.chart-print-frame").forEach(f => f.remove());
            box.querySelectorAll("iframe.chart-frame[hidden]").forEach(f => { f.hidden = false; });
        });
    }
    window.addEventListener("afterprint", clearChartPrint);
    function printChart(box, lib, config) {
        clearChartPrint(); // 앞 인쇄의 표시가 남았으면 (afterprint가 안 온 환경)
        const screen = box.querySelector("iframe.chart-frame");
        if (!screen) return;
        const frame = chartFrame(lib, config, ChartPrintTheme, "chart-print-frame");
        frame.addEventListener("load", () => setTimeout(() => window.print(), 50), { once: true });
        screen.after(frame);
        screen.hidden = true;
        box.classList.add("print-target");
        document.documentElement.classList.add("print-one");
    }
    function charts(container) {
        container.querySelectorAll(".msg.conductor .body pre > code.language-chart").forEach(code => {
            const pre = code.parentElement;
            if (pre.closest(".widget")) return;
            const box = el("div", "widget chart");
            pre.replaceWith(box);
            const note = text => { const n = el("div", "muted widget-note"); n.dataset.note = text; return n; };
            const parsed = chartConfig(code.textContent);
            if (parsed.error) { box.append(note(parsed.error), pre); return; }
            const details = el("details", "widget-code");
            details.append(el("summary"), pre);
            const bar = el("div", "widget-bar");
            box.append(bar, details);
            const draw = () => loadChartLib().then(lib => {
                const frame = chartFrame(lib, parsed.config, chartTheme());
                frame.setAttribute("loading", "lazy");
                details.before(frame);
                const print = el("button", "widget-run chart-print");
                print.type = "button";
                print.setAttribute("aria-label", "차트 인쇄 · PDF로 저장");
                print.addEventListener("click", () => printChart(box, lib, parsed.config));
                bar.append(print);
            }, () => details.before(note("차트 라이브러리를 불러오지 못해 데이터로만 보입니다.")));
            if (autoDrawSlot(box)) { draw(); return; }
            const go = el("button", "widget-run chart-draw");
            go.type = "button";
            go.setAttribute("aria-label", "차트 그리기");
            go.addEventListener("click", () => { go.remove(); draw(); });
            bar.append(go);
        });
    }
    // ---- 슬라이드 시안 (계획서 Orchestration/HB_Chat/docs/plans/2026-10-29_HB_Chat-슬라이드-시안.md) ----
    // ```slides = {"title"?, "slides": [{"title"?, "body"?: Markdown, "chart"?: Chart.js 설정, "notes"?}]}.
    // 앱이 넣어 둔 reveal.js 6.0.2로 차트와 같은 격리 틀에 그린다. 제목 · 메모는 textContent, 본문 Markdown은 marked → DOMPurify(허용 태그만, 속성 없음)를
    // 틀 안에서 한다 — 정화가 뚫려도 격리된 틀 안 (결정 "Markdown 서식"). 슬라이드 차트는 ```chart와 같은 검사 · 같은 덮어쓰기.
    // 차트 장은 덱당 SlidesMaxCharts장까지 — 장마다 캔버스(960×330, devicePixelRatio 2면 약 5MB)라 60장이면 수백 MB (607bd5ec77 지휘자 추가)
    const SlidesMaxChars = 50000, SlidesMaxCount = 60, SlidesMaxCharts = 10, SlidesPrintTimeoutMs = 60000;
    const SlidesLibs = ["reveal.js", "reveal.css", "marked.umd.js", "purify.min.js"];
    const SlidesTags = ["p", "br", "hr", "strong", "b", "em", "i", "del", "s", "code", "pre", "blockquote", "ul", "ol", "li",
        "table", "thead", "tbody", "tr", "th", "td", "h3", "h4"];
    // reveal 기본 테마 대신 앱 테마 (기본 테마는 글꼴을 품어 575KB이거나 외부 글꼴을 불러 CSP에 막힌다)
    // reveal.css의 .reveal-viewport가 흰 바탕 · 검은 글자를 칠한다 — 앱 색으로 덮는다 (실제 확인: 흰 바탕에 밝은 글자)
    const SlidesCss = "html,body,.reveal-viewport{margin:0;height:100%;background:var(--hb-bg);color:var(--hb-fg)}" +
        ".reveal{font:30px/1.45 system-ui,\"Malgun Gothic\",sans-serif;color:var(--hb-fg);--r-link-color:var(--hb-fg)}" +
        ".reveal .slides section{text-align:left}.reveal .slides section.hb-cover{text-align:center}" +
        ".reveal h1,.reveal h2,.reveal h3,.reveal h4{margin:0 0 .45em;line-height:1.2;font-weight:700}" +
        ".reveal h1{font-size:1.9em;margin-top:2.6em}.reveal h2{font-size:1.35em}.reveal h3{font-size:1.1em}.reveal h4{font-size:1em}" +
        ".reveal p{margin:.35em 0}.reveal ul,.reveal ol{margin:.25em 0 .25em 1.2em;padding:0}.reveal li{margin:.12em 0}" +
        ".reveal code{font-family:Consolas,monospace;font-size:.85em;background:rgba(127,127,127,.18);padding:0 .2em;border-radius:4px}" +
        ".reveal pre{font-size:.62em;background:rgba(127,127,127,.15);padding:.5em .7em;border-radius:6px;overflow:hidden;white-space:pre-wrap}" +
        ".reveal pre code{background:none;padding:0}.reveal blockquote{margin:.4em 0;padding-left:.6em;border-left:5px solid var(--hb-line);opacity:.85}" +
        ".reveal table{border-collapse:collapse;font-size:.75em}.reveal th,.reveal td{border:1px solid var(--hb-line);padding:.15em .5em}" +
        // 쪽 번호는 reveal이 <a href="#/n">로 그린다 — srcdoc 틀에서 #/n은 부모 화면 주소로 풀려 틀이 앱 주소로 이동하다 막혔다(사용자 발견) → 누르지 못하게
        ".reveal .controls,.reveal .progress{color:var(--hb-fg)}.reveal .slide-number{background:none;color:var(--hb-fg);opacity:.6;pointer-events:none}" +
        ".hb-chart{position:relative;height:330px;margin-top:.3em}.err{white-space:pre-wrap;padding:8px;font:13px system-ui,sans-serif}";
    function slidesConfig(source) {
        if (source.length > SlidesMaxChars) return { error: `슬라이드 데이터가 너무 커서(${source.length.toLocaleString()}자) 코드로만 보입니다.` };
        let deck;
        try { deck = parseData(source); } catch { return { error: "슬라이드 JSON을 읽지 못해 코드로만 보입니다." }; }
        const text = v => v === undefined || typeof v === "string";
        const ok = isPlainObject(deck) && text(deck.title) && Array.isArray(deck.slides) &&
            deck.slides.length > 0 && deck.slides.length <= SlidesMaxCount &&
            deck.slides.every(s => isPlainObject(s) && text(s.title) && text(s.body) && text(s.notes) && (s.chart === undefined || isChart(s.chart))) &&
            deck.slides.filter(s => s.chart).length <= SlidesMaxCharts;
        if (!ok) return { error: `슬라이드 형식(title · slides 1~${SlidesMaxCount}장 · 차트는 ${SlidesMaxCharts}장까지, 장마다 title · body · chart · notes)이 맞지 않아 코드로만 보입니다.` };
        return { deck };
    }
    // print면 view:"print"로 모든 장을 쪽마다 펼치고 틀이 스스로 인쇄한다 (부모에서 iframe을 인쇄하면 쪽이 나뉘지 않음 — 결정 절)
    function slidesDoc(libs, chartLib, deck, theme, print) {
        const [reveal, revealCss, marked, purify] = libs;
        return "<!doctype html><html><head><meta charset=\"utf-8\">" +
            "<meta http-equiv=\"Content-Security-Policy\" content=\"" + WidgetCsp + "\">" +
            inlineStyle(revealCss) + inlineStyle(SlidesCss) +
            inlineScript(reveal) + inlineScript(marked) + inlineScript(purify) + (chartLib ? inlineScript(chartLib) : "") +
            "<script type=\"application/json\" id=\"hb-deck\">" + scriptJson({ deck, theme, print, tags: SlidesTags }) + "<\/script>" +
            "</head><body><div class=\"reveal\"><div class=\"slides\"></div></div>" +
            "<script>(()=>{const d=JSON.parse(document.getElementById(\"hb-deck\").textContent),k=d.deck,t=d.theme,r=document.documentElement;" +
            NoWebRtc +
            "r.style.setProperty(\"--hb-bg\",t.panel);r.style.setProperty(\"--hb-fg\",t.text);r.style.setProperty(\"--hb-line\",t.line);" +
            // 틀 안의 링크는 모두 따라가지 않는다 — 틀 밖으로 나가는 이동은 어차피 막히고(차단 화면), 넘김은 화살표 · 키보드로 (겹친 방어)
            "document.addEventListener(\"click\",e=>{if(e.target instanceof Element&&e.target.closest(\"a\"))e.preventDefault();},true);" +
            // 그리기 실패: 인쇄용 틀이면 끝났다고 알려 부모가 지우게 한다 — 보이지 않는 틀이 allow-modals를 가진 채 남지 않게 (607bd5ec77 C3)
            "const fail=e=>{const p=document.createElement(\"div\");p.className=\"err\";p.style.color=t.text;p.textContent=\"슬라이드를 그리지 못했습니다: \"+e.message;document.body.replaceChildren(p);" +
            "if(d.print)parent.postMessage({hbPrintDone:true},\"*\");};" +
            // data-* · aria-*도 버린다 — DOMPurify 기본은 남기고, reveal은 data-preview-link가 붙은 요소를 누르면 그 주소를 틀 안에 연다 (607bd5ec77 C1)
            "try{const md=s=>DOMPurify.sanitize(marked.parse(s,{async:false,gfm:true,breaks:true}),{ALLOWED_TAGS:d.tags,ALLOWED_ATTR:[],ALLOW_DATA_ATTR:false,ALLOW_ARIA_ATTR:false});" +
            "const box=document.querySelector(\".slides\"),charts=[];" +
            "const add=(s,cover)=>{const sec=document.createElement(\"section\");if(cover)sec.className=\"hb-cover\";" +
            "if(s.title){const h=document.createElement(cover?\"h1\":\"h2\");h.textContent=s.title;sec.append(h);}" +
            "if(s.body){const b=document.createElement(\"div\");b.innerHTML=md(s.body);sec.append(b);}" +
            "if(s.chart){const w=document.createElement(\"div\");w.className=\"hb-chart\";const c=document.createElement(\"canvas\");w.append(c);sec.append(w);charts.push([c,s.chart]);}" +
            "if(s.notes){const a=document.createElement(\"aside\");a.className=\"notes\";a.textContent=s.notes;sec.append(a);}" +
            "box.append(sec);};" +
            "if(k.title)add({title:k.title},true);k.slides.forEach(s=>add(s,false));" +
            "const o={embedded:true,width:960,height:540,margin:.06,controls:true,progress:true,slideNumber:\"c/t\",hash:false,history:false," +
            // postMessage API를 끈다 — 기본은 켜져 있고 보낸 창을 확인하지 않아, 실행 중인 위젯이 옆 슬라이드의 Reveal을 부를 수 있다 (607bd5ec77 C2)
            "respondToHashChanges:false,keyboard:true,transition:\"none\",backgroundTransition:\"none\",center:false,help:false,postMessage:false,postMessageEvents:false,previewLinks:false," +
            // 좁은 틀(약 462px 이하 — 미니 창 · 좁은 창)에서도 스크롤 보기로 바꾸지 않는다 — 바뀌면 화살표 · 쪽 번호 없는 세로 보기가 되고,
            // sandbox 틀이라 sessionStorage에서 예외가 나 로딩 클래스가 남았다 (실측, /self-review 832f22f583 C1)
            "scrollActivationWidth:null};" +
            "if(d.print)o.view=\"print\";" +
            "Reveal.initialize(o).then(()=>{try{" +
            // 슬라이드는 960×540을 줄여 보이므로 차트 글자를 키운다 (실제 확인: 기본 12px은 읽기 어려움)
            "if(charts.length){Chart.defaults.color=t.text;Chart.defaults.borderColor=t.line;Chart.defaults.animation=false;Chart.defaults.font.size=20;" +
            "for(const[c,cfg]of charts){cfg.options=Object.assign({},cfg.options,{responsive:true,maintainAspectRatio:false,devicePixelRatio:Math.min(window.devicePixelRatio||1,2)});new Chart(c,cfg);}}" +
            // 인쇄용 틀: 다 그린 뒤 스스로 인쇄하고 끝났다고 부모에 알린다 (부모가 틀을 지움)
            "if(d.print){let sent=false;const done=()=>{if(!sent){sent=true;parent.postMessage({hbPrintDone:true},\"*\");}};" +
            "addEventListener(\"afterprint\",done);setTimeout(()=>{window.print();done();},150);}" +
            "}catch(e){fail(e);}},fail);}catch(e){fail(e);}})()<\/script></body></html>";
    }
    const SlidesPrintTheme = ChartPrintTheme;
    function slidesFrame(html, cls, sandbox) {
        const frame = el("iframe", "widget-frame running " + cls);
        frame.setAttribute("sandbox", sandbox);
        frame.setAttribute("referrerpolicy", "no-referrer");
        frame.title = "슬라이드";
        frame.srcdoc = html;
        return frame;
    }
    function slides(container) {
        container.querySelectorAll(".msg.conductor .body pre > code.language-slides").forEach(code => {
            const pre = code.parentElement;
            if (pre.closest(".widget")) return;
            const box = el("div", "widget slides");
            pre.replaceWith(box);
            const note = text => { const n = el("div", "muted widget-note"); n.dataset.note = text; return n; };
            const parsed = slidesConfig(code.textContent);
            if (parsed.error) { box.append(note(parsed.error), pre); return; }
            const details = el("details", "widget-code");
            details.append(el("summary"), pre);
            const bar = el("div", "widget-bar");
            box.append(bar, details);
            const needChart = parsed.deck.slides.some(s => s.chart);
            const libs = () => Promise.all([Promise.all(SlidesLibs.map(loadLib)), needChart ? loadChartLib() : Promise.resolve(null)]);
            const draw = () => libs().then(([l, c]) => {
                const frame = slidesFrame(slidesDoc(l, c, parsed.deck, chartTheme(), false), "slides-frame", "allow-scripts");
                frame.setAttribute("loading", "lazy");
                details.before(frame);
                const print = el("button", "widget-run chart-print");
                print.type = "button";
                print.setAttribute("aria-label", "슬라이드 인쇄 · PDF로 저장");
                // 인쇄용 틀은 화면 밖에서 그려 스스로 인쇄한다 — 틀 안 print()에 allow-modals가 필요 (이 틀만)
                print.addEventListener("click", () => {
                    document.querySelectorAll("iframe.slides-print-frame").forEach(f => f.remove());
                    const frame = slidesFrame(slidesDoc(l, c, parsed.deck, SlidesPrintTheme, true), "slides-print-frame", "allow-scripts allow-modals");
                    document.body.append(frame);
                    setTimeout(() => frame.remove(), SlidesPrintTimeoutMs); // 끝 알림이 끝내 안 와도 지운다 (607bd5ec77 C3)
                });
                bar.append(print);
            }, () => details.before(note("슬라이드 라이브러리를 불러오지 못해 데이터로만 보입니다.")));
            if (autoDrawSlot(box)) { draw(); return; }
            const go = el("button", "widget-run chart-draw");
            go.type = "button";
            go.setAttribute("aria-label", "슬라이드 그리기");
            go.addEventListener("click", () => { go.remove(); draw(); });
            bar.append(go);
        });
    }
    // 코드 블록 복사 단추 (2-2 b). 글자는 CSS ::after로 넣는다 — 인용 답장이 innerText로 본문을 읽을 때 섞이지 않게.
    function decorate(container) {
        widgets(container);
        charts(container);
        slides(container);
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
        // "계획 먼저" (Claude 앱 기능 C): 다음 말을 계획 모드로 — 바꾸면 바로 저장, 실패하면 되돌린다
        const planToggle = $("#plan-next");
        planToggle?.addEventListener("change", async () => {
            const data = new FormData(); data.append("on", planToggle.checked ? "true" : "false");
            planToggle.disabled = true;
            if (!await post("PlanNext", data)) planToggle.checked = !planToggle.checked;
            planToggle.disabled = false;
        });
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
                // 위젯 · 차트 · 슬라이드 원문은 접힌 "코드 보기" 안이라 innerText에 안 잡힌다 — 같은 작업 안에서 잠깐 펼쳐 읽고 되돌린다
                // (화면에 그려지기 전이라 깜빡이지 않음, 계획서 2026-10-29_HB_Chat-2-2-작은-한계-정리)
                const folded = [...body.querySelectorAll("details.widget-code:not([open])")];
                folded.forEach(d => { d.open = true; });
                const text = body.innerText.trim();
                folded.forEach(d => { d.open = false; });
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
        // 표정 그림은 스티커 원본(장당 약 1.4MB)이라 처음 바뀔 때 늦게 보인다 — 화면이 한가할 때 미리 받아 둔다 (하루 캐시,
        // 계획서 2026-10-29_HB_Chat-2-2-작은-한계-정리 결정 "미리 받기"). 같은 그림은 한 번만.
        const preloadFaces = () => [...new Set(Object.values(faces).map(f => f && f.url).filter(Boolean))]
            .forEach(url => { if (!disposed) new Image().src = url; });
        if (window.requestIdleCallback) requestIdleCallback(preloadFaces, { timeout: 5000 }); else setTimeout(preloadFaces, 2000);
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
                const x = el("button", "queue-x"); x.append(icon("x"));
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

        // ---- 터미널 곁칸 (계획서 2026-10-29_HB_Chat-터미널-곁칸, 결정 3): 내 셸("+" PowerShell · ▾ 고르기, 입력 받음)과 지휘자 프로세스(읽기 전용).
        // 목록은 terminals 알림, 출력은 term-out 알림(base64 바이트). 탭을 처음 보일 때 xterm을 만들고 버퍼 전체를 받는다 — 그 끝 오프셋보다 앞선
        // 실시간 조각은 버리고, 틈이 보이거나 다시 붙으면 버퍼를 다시 받는다 (/self-review 57b59ef134 C4). 키 입력은 차례대로 보낸다(내 셸만). ----
        const termTabs = $("#term-tabs"), termHost = $("#term-host"), termBar = $("#term-bar");
        let termList = [], termActive = null, termSend = Promise.resolve(), termResizeTimer = 0;
        const termViews = new Map(); // id → { term, fit, box, end, pending, cols, rows }
        const termObserver = termHost && window.ResizeObserver ? new ResizeObserver(() => { const v = termViews.get(termActive); if (v) fitTerm(v); }) : null;
        if (termHost) termObserver?.observe(termHost);
        const fromB64 = (s) => Uint8Array.from(atob(s || ""), c => c.charCodeAt(0));
        function termLabel(t) { return t.running ? "실행 중" : t.stoppedBy ? "중지됨 (" + t.stoppedBy + ")" : "끝남 (코드 " + t.exitCode + ")"; }
        function renderTerms(list) {
            if (!termTabs) return;
            termList = list || [];
            $("#term-empty").hidden = termList.length > 0;
            const running = termList.filter(t => t.running).length;
            $("#side-tab-term").textContent = running ? "터미널 " + running : "터미널";
            // 목록에서 빠진 것의 xterm은 버린다
            for (const [id, v] of termViews) if (!termList.some(t => t.id === id)) { v.term.dispose(); v.box.remove(); termViews.delete(id); }
            if (!termList.some(t => t.id === termActive)) termActive = termList.length ? termList[termList.length - 1].id : null;
            termTabs.replaceChildren(...termList.map(t => {
                const agent = t.kind === "Agent";
                const b = el("button", "term-tab" + (t.running ? " running" : " ended") + (agent ? " agent" : ""), (agent ? "지휘자 · " : "") + t.name);
                b.type = "button"; b.setAttribute("role", "tab"); b.dataset.term = t.id;
                b.title = (agent ? t.command + " (읽기 전용)" : "내 셸") + " — " + termLabel(t);
                b.setAttribute("aria-selected", String(t.id === termActive));
                b.addEventListener("click", () => showTerm(t.id));
                return b;
            }));
            updateTermBar();
            if (sideTab === "term" && !side.hidden) showTerm(termActive);
        }
        function updateTermBar() {
            const t = termList.find(x => x.id === termActive);
            termBar.hidden = !t;
            if (!t) return;
            const agent = t.kind === "Agent";
            $("#term-state").textContent = t.name + " · " + termLabel(t) + (agent ? " · 지휘자 · " + t.command : "");
            $("#term-state").title = agent ? t.command + " (읽기 전용 — 지휘자가 띄움)" : "내 셸 (작업 폴더)";
            $("#term-stop").hidden = !t.running;
            $("#term-close").hidden = t.running;
        }
        function fitTerm(v) {
            if (v.box.hidden || !v.box.offsetWidth) return; // 안 보이면 잴 수 없다
            try { v.fit.fit(); } catch { return; }
            if (v.term.cols === v.cols && v.term.rows === v.rows) return;
            v.cols = v.term.cols; v.rows = v.term.rows;
            const id = [...termViews].find(([, x]) => x === v)?.[0];
            clearTimeout(termResizeTimer);
            termResizeTimer = setTimeout(() => {
                if (disposed || !id) return;
                const data = new FormData(); data.append("term", id); data.append("cols", v.cols); data.append("rows", v.rows);
                post("TerminalResize", data, true);
            }, 150);
        }
        function termTheme() {
            const cs = getComputedStyle(termHost);
            return { background: cs.backgroundColor, foreground: cs.color, cursor: cs.color };
        }
        function writeTerm(v, offset, bytes) {
            const end = offset + bytes.length;
            if (end <= v.end) return; // 받은 버퍼에 이미 있다
            if (offset > v.end) { loadBuffer(v); return; } // 조각이 빠졌다(끊겼다 다시 붙음) — 버퍼째 다시
            v.term.write(offset >= v.end ? bytes : bytes.subarray(v.end - offset));
            v.end = end;
        }
        // 버퍼 전체를 받아 다시 그린다 — 받는 동안 온 조각은 모았다가 끝 오프셋 뒤만 쓴다
        async function loadBuffer(v) {
            if (v.end < 0) return; // 이미 받는 중
            v.end = -1; v.pending = [];
            try {
                const res = await fetch(self + "?handler=Terminal&term=" + encodeURIComponent(v.id), { cache: "no-store" });
                if (res.ok && !disposed) { const j = await res.json(); v.term.reset(); v.term.write(fromB64(j.data)); v.end = j.end; }
            } catch { }
            if (v.end < 0) v.end = 0;
            for (const p of v.pending) writeTerm(v, p.offset, p.bytes);
            v.pending = [];
        }
        async function showTerm(id) {
            if (!termHost) return;
            termActive = id;
            termTabs.querySelectorAll(".term-tab").forEach(b => b.setAttribute("aria-selected", String(b.dataset.term === id)));
            updateTermBar();
            for (const [tid, v] of termViews) v.box.hidden = tid !== id;
            if (!id) return;
            const have = termViews.get(id);
            if (have) { fitTerm(have); return; }
            try { await loadXterm(); } catch { $("#term-state").textContent = "터미널 화면(xterm.js)을 불러오지 못했습니다."; return; }
            if (disposed || termViews.has(id) || !termList.some(t => t.id === id)) return;
            const box = el("div", "term-view");
            box.hidden = termActive !== id;
            termHost.append(box);
            const shell = termList.find(t => t.id === id)?.kind === "Shell";
            // 지휘자 프로세스는 읽기 전용 (결정 3 — 지휘자에게도 사용자에게도 입력 길이 없다)
            const term = new window.Terminal({ fontSize: 13, fontFamily: "Consolas, 'Cascadia Mono', monospace", scrollback: 5000, theme: termTheme(),
                disableStdin: !shell, cursorBlink: shell });
            const fit = new window.FitAddon.FitAddon();
            term.loadAddon(fit);
            term.open(box);
            const v = { id, term, fit, box, end: 0, pending: [], cols: 0, rows: 0 };
            termViews.set(id, v);
            // 키 입력(내 셸만): 앞 요청이 끝난 뒤 보낸다 (순서가 바뀌면 명령이 섞인다). 붙여넣기는 한 번에 1,000자씩 — 서버 상한 4KB (/self-review 57b59ef134 C2)
            if (shell) term.onData(d => {
                for (let i = 0; i < d.length; i += 1000) {
                    const part = d.slice(i, i + 1000);
                    termSend = termSend.then(() => {
                        if (disposed) return;
                        // base64(UTF-8)로 — 폼 글로 보내면 서버 바인딩이 Enter · 스페이스 한 번(공백만)을 버린다
                        const bytes = new TextEncoder().encode(part);
                        let bin = ""; for (const b of bytes) bin += String.fromCharCode(b);
                        const data = new FormData(); data.append("term", id); data.append("b64", btoa(bin));
                        return post("TerminalInput", data, true);
                    }).catch(() => { });
                }
            });
            fitTerm(v);
            await loadBuffer(v);
            if (shell && termActive === id) term.focus();
        }
        // 새 내 셸: "+"는 PowerShell, ▾ 메뉴로 고르기 (결정 3 — Windows 11 터미널처럼)
        async function openShell(kind) {
            $("#term-menu").hidden = true;
            $("#term-new-menu").setAttribute("aria-expanded", "false");
            const data = new FormData(); data.append("shell", kind);
            const r = await post("TerminalOpen", data, false, true);
            // 목록은 terminals 알림이 가져온다 — 먼저 왔으면 바로 그 탭으로, 아니면 알림이 이 탭을 고른다
            if (r?.id) { termActive = r.id; if (termList.some(t => t.id === r.id)) showTerm(r.id); }
        }
        $("#term-new")?.addEventListener("click", () => openShell("powershell"));
        $("#term-new-menu")?.addEventListener("click", (e) => {
            e.stopPropagation();
            const menu = $("#term-menu");
            menu.hidden = !menu.hidden;
            $("#term-new-menu").setAttribute("aria-expanded", String(!menu.hidden));
        });
        $("#term-menu")?.querySelectorAll("[data-shell]").forEach(b => b.addEventListener("click", () => openShell(b.dataset.shell)));
        function closeTermMenu(e) { if (!e.target.closest?.(".term-new")) { const m = $("#term-menu"); if (m) m.hidden = true; } }
        document.addEventListener("click", closeTermMenu);
        $("#term-stop")?.addEventListener("click", async () => {
            const data = new FormData(); data.append("term", termActive);
            await post("TerminalStop", data);
        });
        $("#term-close")?.addEventListener("click", async () => {
            const data = new FormData(); data.append("term", termActive);
            await post("TerminalClose", data);
        });
        function termOutput(ev) {
            const v = termViews.get(ev.termId);
            if (!v) return; // 탭을 열 때 버퍼로 받는다
            const bytes = fromB64(ev.data);
            if (v.end < 0) v.pending.push({ offset: ev.offset, bytes });
            else writeTerm(v, ev.offset, bytes);
        }

        // ---- 지휘자 브라우저 곁칸 (계획서 2026-10-29_HB_Chat-지휘자-브라우저, 결정 "보기만"): 상태는 browser 알림, 화면은 browser-frame 알림(jpeg base64,
        // 바뀔 때만 옴). 탭을 열 때 마지막 화면을 받는다. 클릭 · 입력은 받지 않는다 — 지휘자 작업과 섞이지 않게. ----
        const browserFrame = $("#browser-frame");
        let browserOpen = false, browserFrameUrl = null;
        function showBrowserImage(src) {
            if (!browserFrame) return;
            if (browserFrameUrl) URL.revokeObjectURL(browserFrameUrl);
            browserFrameUrl = src.startsWith("blob:") ? src : null;
            browserFrame.src = src;
            browserFrame.hidden = false;
        }
        function renderBrowser(view) {
            if (!browserFrame) return;
            browserOpen = !!view?.open;
            $("#browser-state").textContent = browserOpen
                ? (view.url || "빈 화면") + (view.title ? " — " + view.title : "")
                : "지휘자 브라우저가 닫혀 있습니다 — 지휘자가 웹 페이지를 열면 여기에 실시간으로 보입니다 (보기만).";
            $("#browser-state").title = view?.url || "";
            const blocked = $("#browser-blocked");
            blocked.hidden = !(browserOpen && view.blocked);
            blocked.textContent = browserOpen && view.blocked ? "막은 이동: " + view.blocked + " (허용되지 않은 사이트)" : "";
            if (!browserOpen) { browserFrame.hidden = true; browserFrame.removeAttribute("src"); }
            $("#side-tab-browser").textContent = browserOpen ? "브라우저 ●" : "브라우저";
        }
        // 기록 줄의 스크린샷을 누르면 새 창 대신 화면 위에 크게 — 닫기 단추 · Esc · 바깥 누르기로 닫는다 (사용자 요청: 데스크톱에서 새 창은 닫을 길이 없었다)
        function openShot(src) {
            closeShot();
            const box = el("div", "shot-view");
            box.setAttribute("role", "dialog");
            box.setAttribute("aria-label", "스크린샷");
            const x = el("button", "shot-x icon-btn", "✕");
            x.type = "button"; x.title = "닫기 (Esc)"; x.setAttribute("aria-label", "닫기");
            const img = el("img");
            img.src = src; img.alt = "지휘자 브라우저 스크린샷";
            // ✕는 화면 모서리가 아니라 그림 모서리에 — 화면 모서리면 방 머리 단추(대시보드 · 정보) 위에 겹쳐 보였다 (사용자 데스크톱 확인)
            const frame = el("div", "shot-frame");
            frame.append(img, x);
            box.append(frame);
            box.addEventListener("click", (e) => { if (e.target !== img) closeShot(); });
            document.body.append(box);
            x.focus();
        }
        function closeShot() { document.querySelector(".shot-view")?.remove(); }
        function shotKeys(e) { if (e.key === "Escape" && document.querySelector(".shot-view")) { e.stopPropagation(); closeShot(); } }
        document.addEventListener("keydown", shotKeys, true);
        root.addEventListener("click", (e) => {
            const a = e.target.closest?.(".note-shot");
            if (!a) return;
            e.preventDefault();
            openShot(a.getAttribute("href"));
        });
        async function loadBrowserFrame() {
            if (!browserFrame || !browserOpen) return;
            try {
                const res = await fetch(self + "?handler=BrowserFrame", { cache: "no-store" });
                if (res.status === 200 && !disposed) showBrowserImage(URL.createObjectURL(await res.blob()));
            } catch { }
        }

        // ---- 오른쪽 곁칸 (3차 c): 단계 · 방 정보 · 외부 실행 · 첨부. 열림 · 탭은 브라우저 저장(편의만 — 못 읽으면 닫힌 채 · 단계 탭) ----
        const side = $("#side"), sideToggle = $("#side-toggle");
        const store = { get(k) { try { return localStorage.getItem(k); } catch { return null; } }, set(k, v) { try { localStorage.setItem(k, v); } catch { } } };
        const sideTabs = ["steps", "info", "runs", "files", "mcp", "term", "browser"];
        // 저장값이 모르는 탭이면 단계 탭 — 모든 칸이 숨던 문제 (c216951e79 C7)
        let sideTab = sideTabs.includes(store.get("hbchat.sideTab")) ? store.get("hbchat.sideTab") : "steps";
        if (sideTab === "term" && !$("#side-tab-term")) sideTab = "steps"; // 터미널 탭이 없는 칸(옛 화면)이면
        if (sideTab === "browser" && !$("#side-tab-browser")) sideTab = "steps"; // 브라우저 탭은 명령 방만
        let sideLoading = false, sideAgain = false;
        // 넓은 폭(셋째 칸)인지 — 좁으면 곁칸이 방을 덮는다 (chat.css의 1199px 규칙과 같은 값)
        const wide = () => window.matchMedia("(min-width: 1200px)").matches;
        function showTab(tab) {
            sideTab = tab;
            store.set("hbchat.sideTab", tab);
            side.querySelectorAll(".side-tabs [data-tab]").forEach(b => b.setAttribute("aria-selected", String(b.dataset.tab === tab)));
            side.querySelectorAll(".side-sec").forEach(s => { s.hidden = s.dataset.tab !== tab; });
            if (tab === "term" && !side.hidden) showTerm(termActive); // xterm은 탭이 보일 때 만든다 (안 보이면 크기를 못 잰다)
            if (tab === "browser" && !side.hidden) loadBrowserFrame(); // 마지막 화면 (실시간 프레임은 바뀔 때만 온다)
        }
        function sidePlaceholder(text) {
            // 받는 중 · 실패 글은 탭과 상관없이 보인다 (c216951e79 C6 · 지휘자 K3) — 단계 탭은 화면이 그리므로 그 탭에만 가린다
            const p = el("p", "muted side-sec side-wait", text);
            p.hidden = sideTab === "steps" || sideTab === "term" || sideTab === "browser";
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
                // 이 방 스티커 팩 (4차 D2): 바꾸면 서버가 profiles 알림을 보내 방을 다시 연다 (스티커 · 표정 주소가 바뀜)
                // 실패하면 앞 값으로 되돌리고, 성공하면 알림을 기다리지 않고 직접 다시 연다 — 다시 붙는 중 알림을 놓쳐도 (ef2c83416c C5)
                const packSel = $("#side-pack");
                const packWas = packSel?.value;
                packSel?.addEventListener("change", async () => {
                    const data = new FormData(); data.append("pack", packSel.value);
                    packSel.disabled = true;
                    if (await post("StickerPack", data)) { reopenRoom(roomId); return; }
                    packSel.value = packWas;
                    packSel.disabled = false;
                });
                // 모델 · 추론 강도 · 권한 (Claude 앱 기능 B): 다음 말부터. 성공하면 방을 다시 열어 머리 배지 · 입력칸 안내를 새 값으로
                const settings = $("#side-settings");
                settings?.addEventListener("submit", async (e) => {
                    e.preventDefault();
                    const button = settings.querySelector("button[type=submit]");
                    button.disabled = true;
                    if (await post("Settings", new FormData(settings))) { reopenRoom(roomId); return; }
                    button.disabled = false;
                });
                // 연결 탭 (계획서 2026-10-29_HB_Chat-방별-MCP-Unity-연결): 켜기 · 끄기 · Unity 고르기 · 확인. 다음 실행부터 — 성공하면 곁칸만 다시 받는다
                $("#side-sec-mcp")?.addEventListener("change", async (e) => {
                    const t = e.target;
                    if (t.matches("input[data-mcp]")) {
                        const data = new FormData(); data.append("name", t.dataset.mcp); data.append("on", String(t.checked));
                        t.disabled = true;
                        if (!await post("McpToggle", data)) t.checked = !t.checked;
                        loadSide();
                    } else if (t.id === "side-unity") {
                        const data = new FormData(); data.append("project", t.value);
                        t.disabled = true;
                        await post("Unity", data);
                        loadSide();
                    }
                });
                $("#side-sec-mcp")?.addEventListener("click", async (e) => {
                    const b = e.target.closest("button[data-probe]");
                    if (!b) return;
                    const data = new FormData(); data.append("name", b.dataset.probe);
                    b.disabled = true;
                    b.textContent = "확인 중…";
                    await post("McpProbe", data); // 결과(성공 · 실패 · 도구)는 곁칸이 그린다
                    loadSide();
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
            // 터미널 안의 Esc는 그 프로세스 입력이다 — 곁칸을 닫지 않는다
            side.addEventListener("keydown", (e) => { if (e.key === "Escape" && !e.target.closest?.(".term-host")) { setSide(false); sideToggle.focus(); } });
            // 저장된 "열림"은 넓은 폭에서만 되살린다 — 좁으면 방을 열 때마다 대화를 덮었다 (c216951e79 C5). 미니 창은 숨김.
            if (store.get("hbchat.side") === "1" && wide() && !root.closest(".mini")) setSide(true);
        }

        // 승인 카드 (명령 단계): 서버가 답을 기다리는 동안 대화 흐름 안(live)에 보인다 (2-2 b). 남은 시간은 1초마다 다시 쓴다.
        const cards = new Map();
        function showApproval(a) {
            if (cards.has(a.id)) return;
            if (a.plan) { showPlan(a); return; }
            const follow = atBottom();
            const card = el("div", "approval");
            const head = el("div", "head");
            const left = el("span"); left.style.marginLeft = "auto";
            head.append(el("span", null, "승인 요청 · " + a.tool + (a.fromAgent ? " · 서브에이전트" : "")), left); // 서브에이전트가 낸 요청 (D)
            // 명령 전문(스크롤 칸) — 사용자가 본 것이 허용되는 것과 같게 (/self-review 36e4e20e04 C3 · C5, ab79e61df5 C5).
            card.append(head, el("div", "cmd", a.summary));
            // 터미널 시작 · 브라우저 사이트 카드의 설명은 앱이 쓴 안내다 — 지휘자 설명으로 보이지 않게
            if (a.description) card.append(el("div", "desc", (a.tool === "터미널" || a.tool === "브라우저" ? "" : "Claude 설명: ") + a.description));
            // 명령 밖의 입력도 허용하면 같이 실행된다 — 본 것 = 허용되는 것 (/self-review 5e747ff851 C1)
            if (a.extra) card.append(el("div", "cmd", "추가 입력: " + a.extra));
            // 백그라운드 실행 명령 — 답이 끝난 뒤에도 돈다 (계획서 2026-10-29_HB_Chat-백그라운드-명령, 결정 "넣음")
            if (a.backgroundMinutes) card.append(el("div", "warn-line",
                `백그라운드: 답이 끝난 뒤에도 최대 ${a.backgroundMinutes}분 계속 돕니다 — 대화 아래 '지금 끝내기'로 먼저 정리할 수 있습니다.`));
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
        // 서브에이전트 진행 줄 (Claude 앱 기능 D): task_id마다 한 줄 — 시작 · 진행(설명 · 도구 수) · 끝. 턴이 끝나면 다른 진행 줄처럼 지워진다.
        const agentLines = new Map();
        function showAgent(t) {
            if (!t || !t.taskId) return;
            let line = agentLines.get(t.taskId);
            // 시작을 못 본 작업의 끝 알림(지난 실행에서 정리된 백그라운드 작업 — 세션을 이어 갈 때 CLI가 먼저 냄)은 줄을 만들지 않는다
            if ((!line || !line.isConnected) && t.phase === "done") return;
            if (!line || !line.isConnected) {
                line = el("div", "live-line agent-line");
                line.dataset.agent = t.agent || "";
                line.dataset.kind = t.kind || ""; // 끝 알림에는 종류가 없을 수 있어 처음 것을 기억한다
                agentLines.set(t.taskId, line);
                live.append(line);
            }
            const name = line.dataset.agent || t.agent || "서브에이전트";
            if (t.agent) line.dataset.agent = t.agent;
            if (t.kind) line.dataset.kind = t.kind;
            if (t.description) line.dataset.desc = t.description;
            const bash = line.dataset.kind === "local_bash"; // 지휘자의 백그라운드 명령 (계획서 2026-10-29_HB_Chat-백그라운드-명령)
            const state = t.phase === "done" ? (t.status === "completed" ? "끝" : "끝남(" + (t.status || "?") + ")")
                : (bash ? "실행 중" : "진행 중" + (t.toolUses ? " · 도구 " + t.toolUses : ""));
            line.textContent = (bash ? "백그라운드 명령" : "서브에이전트 " + name) + (line.dataset.desc ? ": " + line.dataset.desc : "") + " — " + state;
            line.classList.toggle("denied", t.phase === "done" && t.status !== "completed");
            if (atBottom()) toBottom();
        }

        // 메인 답 뒤 백그라운드 작업 기다림 줄: 몇 개 · 언제 정리 · "지금 끝내기" (계획서 2026-10-29_HB_Chat-백그라운드-명령).
        // 기다림은 정리 시각(until)이 있는 동안 — 작업 수가 0이어도 지휘자가 이어 답하는 중이라 줄과 단추를 둔다 (/self-review 6f64f4b039 C3). 끝 · 중지에서 지운다.
        let bgLine = null;
        function clearBackground() { bgLine?.remove(); bgLine = null; }
        function showBackground(count, until) {
            if (!until) { clearBackground(); return; }
            if (!bgLine || !bgLine.isConnected) {
                bgLine = el("div", "live-line bg-wait");
                bgLine.append(el("span"));
                const go = el("button", "btn", "지금 끝내기");
                go.type = "button";
                // 서버가 못 받았으면(요청 실패) 다시 누를 수 있게 (6f64f4b039 C7)
                go.addEventListener("click", async () => { go.disabled = true; if (!await post("WrapUp", new FormData(), true)) go.disabled = false; });
                bgLine.append(go);
                bgLine.button = go;
                live.append(bgLine);
            }
            bgLine.button.disabled = false; // 새 기다림 알림이 오면 다시 켠다
            const at = new Date(until).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
            bgLine.firstChild.textContent = (count
                ? `답은 끝났고 백그라운드 작업 ${count}개를 기다립니다`
                : "백그라운드 작업이 끝나 지휘자가 이어 답하는 중입니다") + ` — ${at}까지 안 끝나면 정리합니다 `;
            if (atBottom()) toBottom();
        }

        // 계획 카드 (Claude 앱 기능 C): 지휘자의 계획(ExitPlanMode)을 글로 보이고 "승인하고 진행" · 이유 적어 거절(다시 계획). 항상 허용은 없다.
        function showPlan(a) {
            const follow = atBottom();
            const card = el("div", "approval plan-card");
            const head = el("div", "head");
            const left = el("span"); left.style.marginLeft = "auto";
            head.append(el("span", null, "계획 승인"), left);
            card.append(head, el("div", "plan-text", a.summary));
            if (a.truncated) card.append(el("div", "warn-line", "계획이 길어 끝부분이 잘려 보입니다 — 지휘자는 전체 계획대로 진행합니다."));
            // 계획 글 · 파일 경로 밖의 입력은 승인해도 넘기지 않는다 (/self-review security d3dd55b237 C1)
            if (a.extra) card.append(el("div", "warn-line", "보이지 않는 추가 입력(" + a.extra + ")은 빼고 승인합니다."));
            card.append(el("div", "desc", "승인하면 지휘자가 지금 바로 이 계획대로 진행합니다. 명령은 따로 명령 카드로 묻습니다."));
            const acts = el("div", "acts");
            const ok = el("button", "btn primary", "승인하고 진행");
            ok.type = "button";
            ok.addEventListener("click", () => decide(a.id, "allow", card));
            const reasonBox = el("input", "reason");
            reasonBox.type = "text"; reasonBox.maxLength = 500; reasonBox.placeholder = "고칠 점 (지휘자가 계획을 고쳐 다시 묻습니다)";
            reasonBox.addEventListener("keydown", (e) => { if (e.key === "Enter" && !e.isComposing) { e.preventDefault(); decide(a.id, "deny", card); } });
            const no = el("button", "btn", "고쳐 달라고 거절");
            no.type = "button";
            no.addEventListener("click", () => decide(a.id, "deny", card));
            acts.append(ok, reasonBox, no);
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
            if (a.plan) {
                // 승인하면 서버가 "계획 먼저"를 껐다 — 토글도 맞춘다
                if (label === "허용" && planToggle) planToggle.checked = false;
                card.replaceWith(el("div", "live-line approval-line" + (label === "허용" ? "" : " denied"), "계획 " + (label === "허용" ? "승인 — 진행합니다" : (label || "끝남"))));
                return;
            }
            // approval-line: 턴이 끝나 기록을 다시 그리면 지운다 — 같은 줄이 기록 속 제자리(방 기록 줄)에 있다
            card.replaceWith(el("div", "live-line approval-line" + (label === "허용" || label === "항상 허용" ? "" : " denied"),
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
            const reply = el("button", "reply"); reply.append(icon("reply"));
            reply.type = "button"; reply.title = "이 답에 답장 (인용)"; reply.setAttribute("aria-label", "답장");
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
            const b = el("button", "btn rule-remove");
            b.append(icon("x"), "지우기");
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
                    if (ev.busy && ev.backgroundUntil) showBackground(ev.backgroundCount || 0, ev.backgroundUntil); else clearBackground();
                    renderTerms(ev.terminals);
                    for (const v of termViews.values()) loadBuffer(v); // 끊긴 사이 출력은 알림으로 오지 않았다
                    renderBrowser(ev.browser);
                    if (sideTab === "browser" && !side.hidden) loadBrowserFrame();
                    loadSide(); // 끊긴 사이 끝난 턴은 end가 오지 않는다 — 비용 · 외부 실행을 새로 (C3)
                    break;
                case "background":
                    showBackground(ev.backgroundCount, ev.backgroundUntil);
                    break;
                case "terminals":
                    renderTerms(ev.terminals);
                    break;
                case "term-out":
                    termOutput(ev);
                    break;
                case "browser":
                    renderBrowser(ev.browser);
                    break;
                case "browser-frame":
                    if (browserOpen && ev.data) showBrowserImage("data:image/jpeg;base64," + ev.data);
                    break;
                case "face":
                    setFace(ev.face, ev.face === "Tool" && ev.text ? shortTool(ev.text) : null);
                    break;
                case "approval":
                    showApproval(ev.approval);
                    break;
                case "agent":
                    showAgent(ev.task);
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
                case "mcp":
                    loadSide(); // 실행 시작의 연결 상태 — 연결 탭이 턴 끝을 기다리지 않게 (곁칸이 닫혀 있으면 아무것도 안 함)
                    break;
                case "denied":
                case "retry":
                    // 카드 없는 호스트 거절(logged)은 기록에도 남아 턴 끝에 제자리 방 기록 줄로 바뀐다 — 승인 결과 줄처럼 지운다 (/self-review e464ec29ba C6)
                    live.append(el("div", "live-line " + ev.kind + (ev.logged ? " approval-line" : ""), (ev.kind === "denied" ? "거절됨 — " : "") + ev.text));
                    break;
                case "queued":
                    showQueue(ev.pendingItems);
                    break;
                case "notice":
                    notice.textContent = ev.text; notice.hidden = false;
                    break;
                case "end": {
                    loadCommands(); // 이번 실행의 init이 명령 목록을 새로 알려 줬을 수 있다 (Claude 앱 기능 A)
                    // 이 턴의 말은 세션 기록으로 다시 그린다. 다시 받지 못하면 진행 중 말풍선을 남겨 둔다 (지휘자 검토 R7).
                    // 실패 · 중지면 이유를 한 줄 남긴다.
                    const refreshed = await refreshMessages();
                    // 보내지 못한 말은 남김 (C2). 질문 답 말풍선은 세션 기록의 것으로 바뀌므로 지운다 (4차 D1).
                    // 답하지 않은 질문 줄(q-done)은 성공으로 끝난 턴에서만 — 중지 · 실패면 기록에 결과 줄이 없을 수 있어 남긴다 (e8091295fe C4)
                    // 승인 결과 줄 · 백그라운드 명령 진행 줄도 지운다 — 기록 속 제자리에 방 기록 줄 · 백그라운드 알림 줄로 다시 그려졌다 (계획서 2026-10-29_HB_Chat-진행줄-자리-정리표시).
                    // 서브에이전트 진행 줄은 기록에 같은 줄이 없어 다음 턴까지 둔다 (/self-review e464ec29ba 자료 부족)
                    if (refreshed) live.querySelectorAll(":scope > .msg:not(.failed), :scope > .approval-line, :scope > .agent-line[data-kind=\"local_bash\"]" + (ev.ok ? ", :scope > .q-done" : "")).forEach(m => m.remove());
                    else live.prepend(el("div", "live-line denied", "말풍선을 다시 받지 못했습니다 — 새로고침하면 보입니다."));
                    stream = null; chips = null;
                    clearBackground();
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
                const x = el("button", "queue-x"); x.append(icon("x"));
                x.type = "button"; x.title = "첨부 빼기 (올린 파일은 방 첨부 폴더에 남음)"; x.setAttribute("aria-label", "첨부 빼기");
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
        // ---- 입력칸 '/' 명령 · 스킬 목록 (Claude 앱 기능 A): 글이 '/'로 시작하고 아직 띄어쓰기가 없을 때만. 고르면 "/이름 "을 넣는다(보내지 않음) ----
        // 목록은 서버가 그 방 마지막 실행의 init + 파일 설명으로 만든다. 설명은 파일 내용이라 글로만 그린다.
        const cmdBox = el("div", "cmd-list");
        cmdBox.hidden = true;
        cmdBox.setAttribute("role", "listbox");
        form.before(cmdBox);
        const cmdKinds = { command: "명령", skill: "스킬", builtin: "기본", terminal: "터미널" };
        let commands = [], cmdChatOnly = false, cmdShown = [], cmdIndex = 0;
        async function loadCommands() {
            try {
                const res = await fetch(self + "?handler=Commands", { cache: "no-store" });
                if (!res.ok || disposed) return;
                const data = await res.json();
                commands = Array.isArray(data.commands) ? data.commands : [];
                cmdChatOnly = !!data.chat;
                updateCmd();
            } catch { /* 목록이 없어도 보내기는 그대로 */ }
        }
        // '/' 뒤가 이름에 쓰는 글자뿐일 때만 — 경로("/c/Users…")처럼 다른 글자가 있으면 열지 않는다 (포함 일치로 일반 글이 바뀌지 않게, db1b130ff2 C4)
        function cmdQuery() { return /^\/[A-Za-z0-9:._-]*$/.test(textBox.value) ? textBox.value.slice(1).toLowerCase() : null; }
        function updateCmd() {
            const q = cmdQuery();
            const name = (c) => String(c.name).toLowerCase();
            cmdShown = q === null ? [] : [...commands.filter(c => name(c).startsWith(q)), ...commands.filter(c => !name(c).startsWith(q) && name(c).includes(q))].slice(0, 8);
            if (cmdShown.length === 0) { cmdBox.hidden = true; return; }
            cmdIndex = Math.min(cmdIndex, cmdShown.length - 1);
            const rows = cmdShown.map((c, i) => {
                const row = el("div", "cmd-item" + (i === cmdIndex ? " on" : ""));
                row.setAttribute("role", "option");
                row.append(el("span", "cmd-name", "/" + c.name + (c.hint ? " " + c.hint : "")), el("span", "cmd-kind", cmdKinds[c.kind] || c.kind),
                    el("span", "cmd-desc", c.description || ""));
                row.addEventListener("mousedown", (e) => { e.preventDefault(); pickCmd(i); }); // 입력칸 blur보다 먼저
                return row;
            });
            cmdBox.replaceChildren(...(cmdChatOnly ? [el("div", "cmd-note", "대화 단계 방은 도구가 없어 파일 · 명령이 필요한 명령은 제대로 돌지 않습니다")] : []), ...rows);
            cmdBox.hidden = false;
        }
        function pickCmd(i) {
            const c = cmdShown[i];
            if (!c) return;
            textBox.value = "/" + c.name + " ";
            cmdBox.hidden = true;
            textBox.focus();
            textBox.setSelectionRange(textBox.value.length, textBox.value.length);
            textBox.dispatchEvent(new Event("input", { bubbles: true }));
        }
        textBox.addEventListener("input", () => { cmdIndex = 0; updateCmd(); });
        textBox.addEventListener("blur", () => { cmdBox.hidden = true; });
        loadCommands();

        form.addEventListener("submit", (e) => { e.preventDefault(); sendNow("Send"); });
        interruptBtn.addEventListener("click", () => {
            if (!textBox.value.trim() && attached.length === 0) { textBox.focus(); return; }
            sendNow("Interrupt");
        });
        textBox.addEventListener("keydown", (e) => {
            // '/' 목록이 열려 있으면 ↑↓ 고르기 · Enter · Tab 넣기 · Esc 닫기 (한글 조합 중에는 건드리지 않음)
            if (!cmdBox.hidden && !e.isComposing && cmdShown.length > 0) {
                if (e.key === "ArrowDown" || e.key === "ArrowUp") {
                    e.preventDefault();
                    cmdIndex = (cmdIndex + (e.key === "ArrowDown" ? 1 : cmdShown.length - 1)) % cmdShown.length;
                    updateCmd();
                    return;
                }
                // 이름을 다 쳤으면 Enter는 보내기 (인자 없는 /compact 등 — 두 번 누르지 않게, db1b130ff2 C4). Tab은 늘 완성
                const exact = e.key === "Enter" && textBox.value === "/" + cmdShown[cmdIndex].name;
                if (!exact && ((e.key === "Enter" && !e.shiftKey) || e.key === "Tab")) { e.preventDefault(); pickCmd(cmdIndex); return; }
                if (exact) cmdBox.hidden = true; // 아래에서 보낸다
                if (e.key === "Escape") { e.preventDefault(); cmdBox.hidden = true; return; }
            }
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
                termObserver?.disconnect();
                if (browserFrameUrl) URL.revokeObjectURL(browserFrameUrl);
                closeShot();
                document.removeEventListener("keydown", shotKeys, true);
                document.removeEventListener("click", closeTermMenu);
                clearTimeout(termResizeTimer);
                for (const v of termViews.values()) v.term.dispose(); // 프로세스는 그대로 — 방을 다시 열면 버퍼로 다시 그린다
                termViews.clear();
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
