// HB Chat 화면 — 말풍선 본문 꾸미기: 위젯 · 차트 · 슬라이드 · Mermaid 그리기와 코드 복사 단추 (HBChat.decorate).
(() => {
    "use strict";
    const hb = window.HBChat;
    if (!hb) return;
    const { el } = hb;
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
    // ---- 차트 시안 ----
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
    // ---- 차트 하나만 인쇄 · PDF ----
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
    // ---- 슬라이드 시안 ----
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
    // ---- Mermaid 도표 ----
    // 지휘자 말풍선의 ```mermaid(Mermaid 문법 글)를 앱이 넣어 둔 Mermaid 12.3.0(wwwroot/lib)으로 그린다.
    // Mermaid가 5.5MB라 도표마다 틀에 넣지 않는다: 페이지에 하나뿐인 그리기 틀(allow-scripts, 같은 문서 CSP)이 SVG를 만들고,
    // 말풍선에는 스크립트 없는 틀(sandbox="")에 그 SVG만 넣는다. 그리기 틀은 display:none이면 글자 크기를 못 재 도표가 쪼그라든다 (실측)
    // — 슬라이드 인쇄용 틀처럼 화면 안에 투명하게 둔다.
    // 그리기 틀은 사용자 활성화 없이 focus를 가져갈 수 있다 (실측: inert · visibility:hidden으로도 못 막음) — Mermaid 정화가 뚫리면
    // 입력칸 글을 받을 수 있으므로, 그릴 것이 없으면 틀을 치우고(MermaidIdleMs) 틀로 간 focus는 바로 되돌린다 (결정 "되찾기 + 쉬면 틀 치우기").
    const MermaidMaxChars = 50000, MermaidReadyMs = 20000, MermaidTimeoutMs = 10000, MermaidIdleMs = 3000;
    // 그리기 틀이 준비되지 않음(시간 초과 · 라이브러리 실행 실패) — 라이브러리를 못 받은 것과 문구를 가른다
    const MermaidFrameFailed = new Error("mermaid frame");
    let mermaidHost = null, mermaidSeq = 0;
    function mermaidRendererDoc(lib) {
        return "<!doctype html><html><head><meta charset=\"utf-8\">" +
            "<meta http-equiv=\"Content-Security-Policy\" content=\"" + WidgetCsp + "\">" +
            inlineScript(lib) +
            "</head><body><script>(()=>{" + NoWebRtc + "let theme=null;" +
            "addEventListener(\"message\",async e=>{const m=e.data;if(e.source!==parent||m===null||typeof m!==\"object\"||m.hbMermaid!==\"render\")return;" +
            "try{if(m.theme!==theme){mermaid.initialize({startOnLoad:false,securityLevel:\"strict\",theme:m.theme});theme=m.theme;}" +
            "const r=await mermaid.render(\"hb-m\"+m.id,m.code);parent.postMessage({hbMermaid:\"done\",id:m.id,svg:r.svg},\"*\");}" +
            "catch(err){parent.postMessage({hbMermaid:\"error\",id:m.id,error:String(err&&err.message||err)},\"*\");}});" +
            "parent.postMessage({hbMermaid:typeof mermaid===\"object\"?\"ready\":\"fail\"},\"*\");})()<\/script></body></html>";
    }
    // 그리기 틀의 답: 보낸 창이 지금 그리기 틀일 때만
    window.addEventListener("message", (e) => {
        const host = mermaidHost, m = e.data;
        if (!host || !host.frame || e.source !== host.frame.contentWindow || m === null || typeof m !== "object") return;
        if (m.hbMermaid === "ready") host.ok();
        else if (m.hbMermaid === "fail") host.fail(MermaidFrameFailed);
        else host.pending.get(m.id)?.(m);
    });
    let mermaidLastFocus = null;
    document.addEventListener("focusin", (e) => { if (!e.target.classList?.contains("mermaid-renderer")) mermaidLastFocus = e.target; });
    window.addEventListener("blur", () => setTimeout(() => {
        const frame = mermaidHost?.frame;
        if (frame && document.activeElement === frame) { frame.blur(); mermaidLastFocus?.focus(); }
    }, 0));
    function dropMermaidHost(host) {
        host.dead = true;
        clearTimeout(host.idle);
        host.frame?.remove();
        for (const done of host.pending.values()) done({ hbMermaid: "error", error: "그리기 틀이 멈췄습니다" });
        host.pending.clear();
        if (mermaidHost === host) mermaidHost = null;
    }
    function mermaidRenderer() {
        if (mermaidHost) return mermaidHost;
        const host = mermaidHost = { frame: null, pending: new Map(), dead: false, jobs: 0, idle: 0 };
        host.ready = loadLib("mermaid.min.js").then(lib => new Promise((ok, fail) => {
            host.ok = ok; host.fail = fail;
            setTimeout(() => fail(MermaidFrameFailed), MermaidReadyMs);
            const frame = host.frame = el("iframe", "mermaid-renderer");
            frame.setAttribute("sandbox", "allow-scripts");
            frame.setAttribute("referrerpolicy", "no-referrer");
            frame.setAttribute("aria-hidden", "true");
            frame.tabIndex = -1;
            frame.srcdoc = mermaidRendererDoc(lib);
            document.body.append(frame);
        }));
        host.ready.catch(() => dropMermaidHost(host));
        host.queue = host.ready;
        return host;
    }
    function mermaidTheme() {
        const m = /^#([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})$/i.exec(chartTheme().panel);
        if (!m) return "dark";
        const [r, g, b] = m.slice(1).map(h => parseInt(h, 16));
        return 0.2126 * r + 0.7152 * g + 0.0722 * b < 128 ? "dark" : "default";
    }
    // 그린 SVG를 theme + 글로 기억한다 — 턴 끝 말 다시 받기마다 페이지의 도표를 모두 다시 그려 줄을 서지 않게. 오래된 것부터 버린다
    const MermaidCacheMax = 200, mermaidCache = new Map();
    // 하나씩 차례로 그린다. 시간을 넘기면 그리기 틀을 버린다 — 다음 도표는 새 틀에서 (멈춘 틀에 줄 서지 않게)
    // 결과: { svg } · { error } — 라이브러리를 못 받았거나 틀이 준비되지 않으면 거부
    function renderMermaid(code) {
        const theme = mermaidTheme(), key = theme + "\n" + code;
        if (mermaidCache.has(key)) return Promise.resolve({ svg: mermaidCache.get(key) });
        const host = mermaidRenderer();
        host.jobs++;
        clearTimeout(host.idle);
        const run = host.queue.then(() => new Promise(ok => {
            if (host.dead) { ok({ error: "그리기 틀이 멈췄습니다" }); return; }
            const id = ++mermaidSeq;
            const timer = setTimeout(() => dropMermaidHost(host), MermaidTimeoutMs);
            host.pending.set(id, m => {
                clearTimeout(timer);
                host.pending.delete(id);
                if (m.hbMermaid !== "done" || typeof m.svg !== "string") { ok({ error: String(m.error ?? "") }); return; }
                mermaidCache.delete(key);
                mermaidCache.set(key, m.svg);
                if (mermaidCache.size > MermaidCacheMax) mermaidCache.delete(mermaidCache.keys().next().value);
                ok({ svg: m.svg });
            });
            host.frame.contentWindow.postMessage({ hbMermaid: "render", id, code, theme }, "*");
        }));
        host.queue = run.catch(() => {});
        // 줄이 비면 잠시 뒤 틀을 치운다 — 다음 도표는 새 틀에서 (라이브러리 글은 libCache에 남음)
        host.queue.then(() => {
            if (--host.jobs === 0 && !host.dead) host.idle = setTimeout(() => { if (host.jobs === 0) dropMermaidHost(host); }, MermaidIdleMs);
        });
        return run;
    }
    // 표시 틀: 스크립트 없이 SVG만. 높이를 알릴 수 없으니 viewBox 비율로 틀 모양을 정한다 (높이 상한은 CSS)
    function mermaidFrame(svg) {
        const frame = el("iframe", "widget-frame mermaid-frame");
        frame.setAttribute("sandbox", "");
        frame.setAttribute("referrerpolicy", "no-referrer");
        frame.setAttribute("loading", "lazy");
        frame.title = "도표";
        const pad = 16, vb = /<svg\b[^>]*?\sviewBox="\s*[-\d.e]+[\s,]+[-\d.e]+[\s,]+([\d.e]+)[\s,]+([\d.e]+)\s*"/i.exec(svg);
        if (vb) {
            const w = Number(vb[1]) + pad, h = Number(vb[2]) + pad;
            frame.style.aspectRatio = `${w} / ${h}`;
            frame.style.maxWidth = w + "px";
        }
        const t = chartTheme();
        frame.srcdoc = "<!doctype html><html><head><meta charset=\"utf-8\">" +
            "<meta http-equiv=\"Content-Security-Policy\" content=\"" + WidgetCsp + "\">" +
            "<style>html,body{margin:0;height:100%;overflow:hidden}body{box-sizing:border-box;padding:" + pad / 2 + "px}" +
            "svg{display:block;width:100%!important;height:100%!important;max-width:none!important}</style></head>" +
            "<body style=\"background:" + t.panel + ";color:" + t.text + "\">" + svg + "</body></html>";
        return frame;
    }
    // 서버 Markdig(UseAdvancedExtensions의 diagrams)는 ```mermaid를 pre > code가 아니라 code 없는 <pre class="mermaid">(이스케이프된 글)로 낸다
    // — 그 글을 읽고, 코드 보기 · 오류 때 보일 코드 블록은 여기서 만든다 (다른 코드 블록처럼 복사 단추가 붙는다)
    function mermaids(container) {
        container.querySelectorAll(".msg.conductor .body pre.mermaid").forEach(raw => {
            if (raw.closest(".widget")) return;
            const source = raw.textContent;
            const pre = el("pre"), code = el("code", "language-mermaid");
            code.textContent = source;
            pre.append(code);
            const box = el("div", "widget mermaid");
            raw.replaceWith(box);
            const note = text => { const n = el("div", "muted widget-note"); n.dataset.note = text; return n; };
            if (source.length > MermaidMaxChars) { box.append(note(`도표 글이 너무 길어서(${source.length.toLocaleString()}자) 코드로만 보입니다.`), pre); return; }
            const details = el("details", "widget-code");
            details.append(el("summary"), pre);
            const bar = el("div", "widget-bar");
            box.append(bar, details);
            // 그리지 못하면 위젯 오류처럼 이유 한 줄 + 코드
            const failed = text => { bar.remove(); details.replaceWith(note(text), pre); };
            const draw = () => renderMermaid(source).then(
                r => r.svg !== undefined ? details.before(mermaidFrame(r.svg))
                    : failed("도표를 그리지 못해 코드로만 보입니다: " + r.error.split("\n")[0]),
                e => failed(e === MermaidFrameFailed ? "도표 그리기 틀이 준비되지 않아 코드로만 보입니다."
                    : "도표 라이브러리를 불러오지 못해 코드로만 보입니다."));
            if (autoDrawSlot(box)) { draw(); return; }
            const go = el("button", "widget-run chart-draw");
            go.type = "button";
            go.setAttribute("aria-label", "도표 그리기");
            go.addEventListener("click", () => { go.remove(); draw(); });
            bar.append(go);
        });
    }
    // 코드 블록 복사 단추 (2-2 b). 글자는 CSS ::after로 넣는다 — 인용 답장이 innerText로 본문을 읽을 때 섞이지 않게.
    function decorate(container) {
        widgets(container);
        charts(container);
        slides(container);
        mermaids(container);
        container.querySelectorAll(".body pre").forEach(pre => {
            if (pre.querySelector(":scope > .copy")) return;
            const b = el("button", "copy");
            b.type = "button";
            b.setAttribute("aria-label", "코드 복사");
            pre.append(b);
        });
    }
    hb.decorate = decorate;
})();
