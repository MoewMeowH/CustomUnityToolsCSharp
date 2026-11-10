// HB Chat 화면 스크립트들이 같이 쓰는 도우미. 다른 chat.*.js보다 먼저 불러온다 (_Layout) — 스크립트끼리는 window.HBChat으로 주고받는다.
(() => {
    "use strict";
    if (!document.querySelector(".app")) return;
    const el = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; };
    window.HBChat = { el };
})();
