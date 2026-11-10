// HB Chat 화면 — 모델 선택 줄(HBChat.modeRows)과 승인 · 질문 · 계획 카드(HBChat.cardKit).
(() => {
    "use strict";
    const hb = window.HBChat;
    if (!hb) return;
    const { el } = hb;
    // 모델 선택 줄 (결정 "에이전트 + 강도"): 모드가 쓰는 에이전트마다 교체 · 추론 강도 고르기.
    // rows: [{agent, roles, fallback, pick, effort}], agents: [{name, model, reasoning}]. read()는 {원래 에이전트: {agent, effort}} — run_mode 카드 · + 요청 창이 같이 쓴다.
    function modeRows(rows, agents, efforts) {
        const box = el("div", "mode-rows");
        const reads = [];
        for (const r of rows) {
            const line = el("div", "mode-row");
            const label = el("span", "mode-roles", (r.roles || []).filter(Boolean).join(" · ") || r.agent);
            label.title = "원래 에이전트: " + r.agent;
            const pick = el("select");
            pick.setAttribute("aria-label", label.textContent + " 에이전트");
            const names = agents.map(a => a.name);
            if (!names.includes(r.pick)) names.unshift(r.pick); // 목록에 없는 이름(지휘자가 넣은 값)도 그대로 보인다 — 허가하면 서버가 다시 검사
            for (const name of names) {
                const info = agents.find(a => a.name === name);
                const o = el("option", null, name + (info ? " — " + info.model : "") + (name === r.agent ? " (기본)" : ""));
                o.value = name;
                pick.append(o);
            }
            pick.value = r.pick;
            const effort = el("select");
            effort.setAttribute("aria-label", label.textContent + " 추론 강도");
            const base = el("option"); base.value = "";
            effort.append(base);
            for (const e of efforts) { const o = el("option", null, "강도 " + e); o.value = e; effort.append(o); }
            effort.value = r.effort || "";
            // "설정값" 칸에 고른 에이전트의 설정 강도를 같이 보인다
            const showBase = () => { const info = agents.find(a => a.name === pick.value); base.textContent = "강도 설정값" + (info?.reasoning ? " (" + info.reasoning + ")" : ""); };
            pick.addEventListener("change", showBase);
            showBase();
            line.append(label, pick, effort);
            if (r.fallback) line.append(el("span", "muted mode-fallback", "실패 시 " + r.fallback));
            box.append(line);
            reads.push(() => [r.agent, { agent: pick.value, effort: effort.value || null }]);
        }
        return { el: box, read: () => Object.fromEntries(reads.map(f => f())) };
    }

    // 승인 · 질문 · 계획 카드 (방 화면 mountRoom · 단톡방 화면 mountGroup이 같이 쓴다 — 단톡방 ⑤). 서버가 답을 기다리는 동안 live 칸에 보인다 (2-2 b).
    // 남은 시간은 1초마다 다시 쓴다. opts: live · atBottom · toBottom · post(카드 알림, 처리기, FormData) — 답은 그 카드가 난 방으로,
    // who(알림) — 머리 앞 이름(단톡방은 구성원 이름, 방 화면은 ""), onPlanDone(결정), answered(질문, 답) — 답한 질문 말풍선(없으면 한 줄).
    function cardKit(opts) {
        const { live, atBottom, toBottom } = opts;
        const cards = new Map();
        const who = (a) => opts.who ? opts.who(a) : "";
        function showApproval(a) {
            if (cards.has(a.id)) return;
            if (a.plan) { showPlan(a); return; }
            if (a.mode) { showModeCard(a); return; }
            if (a.roomOpen) { showRoomCard(a); return; }
            const follow = atBottom();
            const card = el("div", "approval");
            const head = el("div", "head");
            const left = el("span"); left.style.marginLeft = "auto";
            head.append(el("span", null, who(a) + "승인 요청 · " + a.tool + (a.fromAgent ? " · 서브에이전트" : "")), left); // 서브에이전트가 낸 요청 (D)
            // 명령 전문(스크롤 칸) — 사용자가 본 것이 허용되는 것과 같게 (/self-review 36e4e20e04 C3 · C5, ab79e61df5 C5).
            card.append(head, el("div", "cmd", a.summary));
            // 터미널 시작 · 브라우저 사이트 카드의 설명은 앱이 쓴 안내다 — 지휘자 설명으로 보이지 않게
            if (a.description) card.append(el("div", "desc", (a.tool === "터미널" || a.tool === "브라우저" || a.tool === "웹 가져오기" ? "" : "Claude 설명: ") + a.description));
            // 작업 폴더 밖 파일 쓰기 (명령 방): 경로가 위, 쓸 내용은 앞부분만 — 잘려도 허용 (결정 "앞 2,000자 · 잘려도 허용")
            if (a.outsideWrite) card.append(el("div", "warn-line", "작업 폴더 밖 파일 쓰기입니다 — 허용하면 위 경로의 파일이 만들어지거나 바뀝니다."));
            // 앱 단계: 늘 허용하면 이 폴더(하위 포함) 쓰기는 모든 앱 단계 방에서 다시 묻지 않는다 — 설정 화면에서 뺄 수 있다
            if (a.alwaysFolder) card.append(el("div", "desc", "'이 폴더 늘 허용' 대상: " + a.alwaysFolder + " (하위 폴더 포함 · 모든 앱 단계 방 · 설정 화면에서 뺌)"));
            // 명령 밖의 입력도 허용하면 같이 실행된다 — 본 것 = 허용되는 것 (/self-review 5e747ff851 C1)
            if (a.extra) card.append(el("div", "cmd", (a.outsideWrite ? "쓸 내용: " : "추가 입력: ") + a.extra));
            // 백그라운드 실행 명령 — 답이 끝난 뒤에도 돈다 (결정 "넣음")
            if (a.backgroundMinutes) card.append(el("div", "warn-line",
                `백그라운드: 답이 끝난 뒤에도 최대 ${a.backgroundMinutes}분 계속 돕니다 — 대화 아래 '지금 끝내기'로 먼저 정리할 수 있습니다.`));
            const denyOnly = a.truncated || a.risky;
            if (a.risky) card.append(el("div", "warn-line", "위험한 입력(dangerously · bypass)이 있어 거절만 할 수 있습니다."));
            else if (a.truncated) card.append(el("div", "warn-line", "명령이나 입력이 너무 길어 끝까지 보이지 않습니다 — 거절만 할 수 있습니다."));
            const acts = el("div", "acts");
            const button = (label, choice, extra) => {
                const b = el("button", "btn" + (extra ? " " + extra : ""), label);
                b.type = "button";
                b.addEventListener("click", () => decide(a, choice, card));
                acts.append(b);
                return b;
            };
            if (!denyOnly) {
                button("허용", "allow", "primary");
                if (a.turnAllow) button("이 턴 동안 묻지 않기", "turn");
                if (a.alwaysFolder) button("이 폴더 늘 허용", "folder");
            }
            const reasonBox = el("input", "reason");
            reasonBox.type = "text"; reasonBox.maxLength = 500; reasonBox.placeholder = "거절 이유 (선택, 지휘자에게 전해짐)";
            reasonBox.addEventListener("keydown", (e) => { if (e.key === "Enter" && !e.isComposing) { e.preventDefault(); decide(a, "deny", card); } });
            acts.append(reasonBox);
            button("거절", "deny");
            card.append(acts);
            card.dataset.expires = String(Date.parse(a.expiresAt));
            card.left = left;
            place(a, card);
            if (follow) toBottom();
        }
        // 카드를 live 칸에 넣는다 — 알림의 detail(단톡방 구성원 ID)을 달아 둔다
        function place(a, card) {
            if (a.detail) card.dataset.member = a.detail;
            live.append(card);
            cards.set(a.id, card);
            tick();
        }

        // 계획 카드 (Claude 앱 기능 C): 지휘자의 계획(ExitPlanMode)을 글로 보이고 "승인하고 진행" · 이유 적어 거절(다시 계획).
        function showPlan(a) {
            const follow = atBottom();
            const card = el("div", "approval plan-card");
            const head = el("div", "head");
            const left = el("span"); left.style.marginLeft = "auto";
            head.append(el("span", null, who(a) + "계획 승인"), left);
            card.append(head, el("div", "plan-text", a.summary));
            if (a.truncated) card.append(el("div", "warn-line", "계획이 길어 끝부분이 잘려 보입니다 — 지휘자는 전체 계획대로 진행합니다."));
            // 계획 글 · 파일 경로 밖의 입력은 승인해도 넘기지 않는다 (/self-review security d3dd55b237 C1)
            if (a.extra) card.append(el("div", "warn-line", "보이지 않는 추가 입력(" + a.extra + ")은 빼고 승인합니다."));
            card.append(el("div", "desc", "승인하면 지휘자가 지금 바로 이 계획대로 진행합니다. 명령은 따로 명령 카드로 묻습니다."));
            const acts = el("div", "acts");
            const ok = el("button", "btn primary", "승인하고 진행");
            ok.type = "button";
            ok.addEventListener("click", () => decide(a, "allow", card));
            const reasonBox = el("input", "reason");
            reasonBox.type = "text"; reasonBox.maxLength = 500; reasonBox.placeholder = "고칠 점 (지휘자가 계획을 고쳐 다시 묻습니다)";
            reasonBox.addEventListener("keydown", (e) => { if (e.key === "Enter" && !e.isComposing) { e.preventDefault(); decide(a, "deny", card); } });
            const no = el("button", "btn", "고쳐 달라고 거절");
            no.type = "button";
            no.addEventListener("click", () => decide(a, "deny", card));
            acts.append(ok, reasonBox, no);
            card.append(acts);
            card.dataset.expires = String(Date.parse(a.expiresAt));
            card.left = left;
            place(a, card);
            if (follow) toBottom();
        }
        // 모델 선택 카드: 지휘자의 run_mode — 모드 · 이유 · 요청 글 · 보낼 파일을 보이고, 에이전트마다 교체 · 강도를 골라 허가 · 거절.
        // 모드 · 요청 글은 바꾸지 않는다 (지휘자가 준비한 입력과 어긋나지 않게).
        function showModeCard(a) {
            const m = a.mode;
            const follow = atBottom();
            const card = el("div", "approval mode-card");
            const head = el("div", "head");
            const left = el("span"); left.style.marginLeft = "auto";
            head.append(el("span", null, who(a) + "외부 에이전트 요청 · " + m.mode), left);
            card.append(head);
            if (m.description) card.append(el("div", "desc", m.description));
            if (m.reason) card.append(el("div", "desc", "Claude 이유: " + m.reason));
            const body = el("details", "mode-input");
            body.append(el("summary", "muted", "요청 글 " + m.inputChars.toLocaleString() + "자" + (m.inputCut ? " (앞 " + m.inputHeadLimit.toLocaleString() + "자만 보임)" : "")),
                el("div", "cmd", m.inputHead));
            card.append(body);
            if (m.files?.length) card.append(el("div", "desc", "보낼 파일: " + m.files.slice(0, 12).join(", ") + (m.files.length > 12 ? " 외 " + (m.files.length - 12) + "개" : "")));
            if (m.images) card.append(el("div", "desc", "그림 " + m.images + "장"));
            if (m.followUp) card.append(el("div", "desc", "자료 보충 재질문 — 원래 단계의 에이전트 · 강도로 보냅니다."));
            if (m.error) card.append(el("div", "warn-line", m.error));
            if (m.prefilled) card.append(el("div", "desc", "요청 창에서 고른 값으로 채웠습니다."));
            if (m.rows?.length) {
                const editor = modeRows(m.rows, m.agents || [], m.efforts || []);
                card.append(editor.el);
                card.readPicks = editor.read;
            }
            const acts = el("div", "acts");
            const ok = el("button", "btn primary", "허가");
            ok.type = "button";
            ok.addEventListener("click", () => decide(a, "allow", card));
            const reasonBox = el("input", "reason");
            reasonBox.type = "text"; reasonBox.maxLength = 500; reasonBox.placeholder = "거절 이유 (선택, 지휘자에게 전해짐)";
            reasonBox.addEventListener("keydown", (e) => { if (e.key === "Enter" && !e.isComposing) { e.preventDefault(); decide(a, "deny", card); } });
            const no = el("button", "btn", "거절");
            no.type = "button";
            no.addEventListener("click", () => decide(a, "deny", card));
            acts.append(ok, reasonBox, no);
            card.append(acts);
            card.dataset.expires = String(Date.parse(a.expiresAt));
            card.left = left;
            place(a, card);
            if (follow) toBottom();
        }
        // 새 방 열기 카드 (room_open): 지휘자가 낸 값을 미리 채운 칸 — 작업 폴더 · 단계 · 모델 · 제목 · 첫 말을 고쳐 허용 · 거절.
        // 여는 방보다 높은 단계를 고르면 경고 줄 (결정 "카드에서는 올릴 수 있음" — 지휘자 값은 서버가 이미 낮춰 채웠다)
        const levelLabels = { read: "읽기", command: "명령", app: "앱" };
        function showRoomCard(a) {
            const r = a.roomOpen;
            const follow = atBottom();
            const card = el("div", "approval room-card");
            const head = el("div", "head");
            const left = el("span"); left.style.marginLeft = "auto";
            head.append(el("span", null, who(a) + "새 방 열기 요청"), left);
            card.append(head, el("div", "desc", "허용하면 새 방이 아래 첫 말로 바로 시작하고, 이 방에는 [열기] 줄이 남습니다. 새 방은 이 대화를 모릅니다 — 첫 말이 인계의 전부입니다."));
            if (r.note) card.append(el("div", "warn-line", r.note));
            const field = (label, input) => { const f = el("label", "room-field"); f.append(el("span", "muted", label), input); card.append(f); return input; };
            const select = (values, current, names) => {
                const s = el("select");
                for (const v of values) { const o = el("option", null, names?.[v] || v); o.value = v; if (v === current) o.selected = true; s.append(o); }
                return s;
            };
            const ws = field("작업 폴더", select(r.workspaces, r.workspace));
            // 다른 작업 폴더도 단계처럼 권한 범위가 바뀐다 — 눈에 띄게 (/self-review 3eda32c3fb C2)
            const wsWarn = el("div", "warn-line ws-warn");
            card.append(wsWarn);
            const warnWs = () => {
                const other = !!r.fromWorkspace && ws.value !== r.fromWorkspace;
                wsWarn.hidden = !other;
                wsWarn.textContent = other ? "이 방(" + r.fromWorkspace + ")과 다른 작업 폴더입니다 — 새 방은 그 폴더에서 일합니다." : "";
            };
            ws.addEventListener("change", warnWs);
            warnWs();
            const levelSel = field("권한 단계", select(r.levels, r.level, levelLabels));
            const levelWarn = el("div", "warn-line level-warn");
            card.append(levelWarn);
            const order = r.levels;
            const warnLevel = () => {
                const higher = order.indexOf(levelSel.value) > order.indexOf(r.fromLevel);
                levelWarn.hidden = !higher;
                levelWarn.textContent = higher ? "이 방(" + (levelLabels[r.fromLevel] || r.fromLevel) + ")보다 높은 단계입니다 — 새 방에서 지휘자가 더 많은 일을 묻지 않고 합니다." : "";
            };
            levelSel.addEventListener("change", warnLevel);
            warnLevel();
            const model = field("모델", select(r.models, r.model));
            const title = field("제목", el("input"));
            title.type = "text"; title.maxLength = 200; title.value = r.title || ""; title.placeholder = "비우면 첫 말로";
            const prompt = field("첫 말", el("textarea", "room-prompt"));
            prompt.value = r.prompt || "";
            // 첫 말 크기: 글 칸은 높이가 정해져 있어 긴 글 끝을 놓치기 쉽다 — 길면 끝까지 읽으라고 (/self-review 3eda32c3fb C3)
            const size = el("div", "desc prompt-size");
            card.append(size);
            const showSize = () => {
                const lines = prompt.value.split("\n").length;
                const long = lines > 30 || prompt.value.length > 3000;
                size.textContent = prompt.value.length.toLocaleString() + "자 · " + lines.toLocaleString() + "줄" + (long ? " — 길어요. 끝까지 읽어 본 뒤 허용하세요 (새 방에서는 이 글이 사용자 말로 읽힙니다)." : "");
                size.classList.toggle("warn-line", long);
            };
            prompt.addEventListener("input", showSize);
            showSize();
            card.readRoom = () => ({ workspace: ws.value, level: levelSel.value, model: model.value, title: title.value.trim() || null, prompt: prompt.value });
            const acts = el("div", "acts");
            const ok = el("button", "btn primary", "허용");
            ok.type = "button";
            ok.addEventListener("click", () => decide(a, "allow", card));
            const reasonBox = el("input", "reason");
            reasonBox.type = "text"; reasonBox.maxLength = 500; reasonBox.placeholder = "거절 이유 (선택, 지휘자에게 전해짐)";
            reasonBox.addEventListener("keydown", (e) => { if (e.key === "Enter" && !e.isComposing) { e.preventDefault(); decide(a, "deny", card); } });
            const no = el("button", "btn", "거절");
            no.type = "button";
            no.addEventListener("click", () => decide(a, "deny", card));
            acts.append(ok, reasonBox, no);
            card.append(acts);
            card.dataset.expires = String(Date.parse(a.expiresAt));
            card.left = left;
            place(a, card);
            if (follow) toBottom();
        }
        // 끝난 카드는 한 줄로 바꾼다. 줄에도 구성원 ID를 달아 단톡방이 그 구성원 턴이 끝나면 지운다
        function line(a, cls, text) {
            const l = el("div", "live-line " + cls, text);
            if (a.detail) l.dataset.member = a.detail;
            return l;
        }
        function doneApproval(a, label) {
            const card = cards.get(a.id);
            if (!card) return;
            cards.delete(a.id);
            if (a.plan) {
                opts.onPlanDone?.(label); // 승인하면 서버가 "계획 먼저"를 껐다 — 토글도 맞춘다
                card.replaceWith(line(a, "approval-line" + (label === "허용" ? "" : " denied"), who(a) + "계획 " + (label === "허용" ? "승인 — 진행합니다" : (label || "끝남"))));
                return;
            }
            // approval-line: 턴이 끝나 기록을 다시 그리면 지운다 — 같은 줄이 기록 속 제자리(방 기록 줄)에 있다
            // 모델 선택 카드의 허가는 "허용 (바꾼 줄)"로 온다
            const allowed = label === "허용" || label === "항상 허용" || label === "이 턴 허용" || ((a.mode || a.roomOpen) && label?.startsWith("허용"));
            card.replaceWith(line(a, "approval-line" + (allowed ? "" : " denied"),
                who(a) + "승인 " + (label || "끝남") + " — " + (a.mode ? "외부 에이전트 " + a.mode.mode
                    : a.roomOpen ? "새 방 열기" : a.tool + ": " + a.summary.slice(0, 120))));
        }
        // 질문 카드 (3차 a): 지휘자의 AskUserQuestion. 질문마다 선택지(하나 · 여러 개) + "기타" 직접 쓰기. 남은 시간은 승인 카드와 같이 다시 쓴다.
        function showQuestion(q) {
            if (cards.has(q.id)) return;
            const follow = atBottom();
            const card = el("div", "approval question-card");
            const head = el("div", "head");
            const left = el("span"); left.style.marginLeft = "auto";
            head.append(el("span", null, who(q) ? who(q) + "질문" : "지휘자의 질문"), left);
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
                await answer(q, card, answers);
            });
            skip.addEventListener("click", () => answer(q, card, null));
            acts.append(send, skip);
            card.append(acts);
            card.dataset.expires = String(Date.parse(q.expiresAt));
            card.left = left;
            place(q, card);
            if (follow) toBottom();
        }
        async function answer(q, card, answers) {
            const controls = card.querySelectorAll("button, input");
            controls.forEach(b => b.disabled = true);
            const data = new FormData();
            data.append("question", q.id);
            if (answers) data.append("answers", JSON.stringify(answers)); else data.append("skip", "true");
            const r = await opts.post(q, "Answer", data);
            // 다른 화면에서 이미 답함 — 오류 창 없이, 실제 답(끝남 알림)을 기다렸다가 닫는다. 알림이 안 오면 잠시 뒤 닫기만 (A3 · 9c54e0c54a C2)
            if (r === "gone") setTimeout(() => { if (cards.get(q.id) === card) doneQuestion(q, "", false); }, 3000);
            else if (!r) controls.forEach(b => b.disabled = false);
        }
        function doneQuestion(q, text, answered, answers) {
            const card = cards.get(q.id);
            if (!card) return;
            cards.delete(q.id);
            const bubbled = answered && answers ? opts.answered?.(q, answers) : null;
            // q-done: 턴 끝에 세션 기록의 같은 줄(답하지 않음)로 바뀌므로 그때 지운다
            card.replaceWith(bubbled || line(q, "q-done" + (answered ? "" : " denied"), who(q) + "질문 " + (answered ? "답함 — " + text : (text || "끝남"))));
        }
        async function decide(a, choice, card) {
            const reason = choice === "deny" ? card.querySelector("input.reason")?.value.trim() : null;
            const buttons = card.querySelectorAll("button");
            buttons.forEach(b => b.disabled = true);
            const data = new FormData();
            data.append("approval", a.id); data.append("choice", choice);
            if (reason) data.append("reason", reason);
            if (choice === "allow" && card.readPicks) data.append("picks", JSON.stringify(card.readPicks())); // 모델 선택 카드
            if (choice === "allow" && card.readRoom) data.append("room", JSON.stringify(card.readRoom())); // 새 방 열기 카드
            const r = await opts.post(a, "Approve", data);
            if (r === "gone") setTimeout(() => { if (cards.get(a.id) === card) doneApproval(a, null); }, 3000); // 위 질문과 같음 (A3 · C2)
            else if (!r) buttons.forEach(b => b.disabled = false);
        }
        function tick() {
            for (const card of cards.values()) {
                const s = Math.max(0, Math.round((Number(card.dataset.expires) - Date.now()) / 1000));
                card.left.textContent = "남은 시간 " + Math.floor(s / 60) + ":" + String(s % 60).padStart(2, "0");
            }
        }
        const ticker = setInterval(tick, 1000);
        return {
            cards, showApproval, doneApproval, showQuestion, doneQuestion,
            clear() { cards.forEach(c => c.remove()); cards.clear(); },
            dispose() { clearInterval(ticker); },
        };
    }
    hb.modeRows = modeRows;
    hb.cardKit = cardKit;
})();
