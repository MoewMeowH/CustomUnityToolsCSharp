// HB Chat 설정 화면: 프로필 그림 줄이기 · 설정 탭.
(() => {
    "use strict";
    if (!document.querySelector(".app")) return;
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
        // 그림을 새로 고르면 다시 줄인다 — 뒤로 가기(bfcache)로 돌아온 화면에 앞 표시가 남아 원본이 올라가지 않게
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

    // ---- 설정 화면 탭 (노션 "채팅 설정내 탭"): 한 탭만 보인다. 고를 탭 = 주소 #id(탭 또는 그 안 섹션) → 서버가 정한 탭(방금 저장한 것)
    // → 마지막에 본 탭 → 첫 탭. 스크립트가 없으면 탭 줄은 숨은 채 모든 섹션이 보인다. ----
    const settingsTabs = document.querySelector("nav.settings-tabs");
    if (settingsTabs) {
        const TabKey = "hbchat.settingsTab";
        const panels = [...document.querySelectorAll(".settings-tab")];
        const buttons = [...settingsTabs.querySelectorAll("button[role=tab]")];
        const known = id => panels.some(p => p.dataset.tab === id);
        function showTab(id, focus) {
            for (const p of panels) p.hidden = p.dataset.tab !== id;
            for (const b of buttons) {
                const on = b.dataset.tab === id;
                b.setAttribute("aria-selected", on ? "true" : "false");
                b.tabIndex = on ? 0 : -1;
                if (on && focus) b.focus();
            }
            try { localStorage.setItem(TabKey, id); } catch { }
        }
        // 깨진 퍼센트 인코딩(#%)이면 decodeURIComponent가 던진다 — 이 스크립트가 멈추면 사이드바 실시간 갱신도 안 붙는다
        function hashId() { try { return decodeURIComponent(location.hash.slice(1)); } catch { return ""; } }
        function fromHash() {
            const hash = hashId();
            const target = hash ? document.getElementById(hash) : null;
            return target?.closest(".settings-tab")?.dataset.tab ?? null;
        }
        // 섹션 주소(#cli 등)면 탭을 연 뒤 그 섹션으로 — 숨은 채로는 브라우저가 내려가지 못했다
        function scrollToSection() {
            const hash = hashId();
            if (hash && !hash.startsWith("tab-")) document.getElementById(hash)?.scrollIntoView();
        }
        let saved = null;
        try { saved = localStorage.getItem(TabKey); } catch { }
        const start = [fromHash(), settingsTabs.dataset.active, saved].find(id => id && known(id)) ?? panels[0]?.dataset.tab;
        settingsTabs.hidden = false;
        showTab(start, false);
        scrollToSection();
        settingsTabs.addEventListener("click", (e) => {
            const b = e.target.closest("button[role=tab]");
            if (!b) return;
            showTab(b.dataset.tab, false);
            history.replaceState(null, "", location.pathname + location.search + "#tab-" + b.dataset.tab);
        });
        // 키보드: 탭 줄에서 ← · → · Home · End (tablist 관례)
        settingsTabs.addEventListener("keydown", (e) => {
            const at = buttons.findIndex(b => b.getAttribute("aria-selected") === "true");
            const next = e.key === "ArrowRight" ? (at + 1) % buttons.length : e.key === "ArrowLeft" ? (at - 1 + buttons.length) % buttons.length
                : e.key === "Home" ? 0 : e.key === "End" ? buttons.length - 1 : -1;
            if (next < 0) return;
            e.preventDefault();
            showTab(buttons[next].dataset.tab, true);
            history.replaceState(null, "", location.pathname + location.search + "#tab-" + buttons[next].dataset.tab);
        });
        // 같은 쪽에서 사이드바 #cli 띠를 누르면 다시 불러오지 않고 hashchange만 온다 — 탭을 열고 그 섹션으로
        window.addEventListener("hashchange", () => { const id = fromHash(); if (id) { showTab(id, false); scrollToSection(); } });
    }
})();
