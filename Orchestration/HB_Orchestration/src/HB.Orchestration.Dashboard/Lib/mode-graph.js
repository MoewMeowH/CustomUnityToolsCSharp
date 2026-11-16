// 모드 화면의 그래프 편집기 (AntV X6).
// 자료는 페이지의 #me-data(JSON), 저장은 숨은 폼으로 모드 · 역할을 하나씩 보낸다. 최종 검증은 서버가 한다.
(function () {
  'use strict';

  const dataEl = document.getElementById('me-data');
  const host = document.getElementById('mode-editor');
  if (!dataEl || !host) return;
  if (typeof X6 === 'undefined') {
    host.textContent = '그래프 라이브러리(X6)를 불러오지 못했습니다.';
    return;
  }
  const data = JSON.parse(dataEl.textContent);

  // ── 도우미 ──────────────────────────────────────────────────────────────
  const $ = (id) => document.getElementById(id);

  function el(tag, props, ...kids) {
    const node = document.createElement(tag);
    for (const [key, value] of Object.entries(props || {})) {
      if (key === 'class') node.className = value;
      else if (key === 'text') node.textContent = value;
      else if (key === 'value') node.value = value;
      else if (key.startsWith('on')) node.addEventListener(key.slice(2), value);
      else if (value === true) node.setAttribute(key, '');
      else if (value !== false && value != null) node.setAttribute(key, value);
    }
    for (const kid of kids.flat()) if (kid != null && kid !== false) node.append(kid);
    return node;
  }

  // append · replaceChildren은 null을 "null" 글자로 넣는다 — 없는 것은 걸러서 넣는다.
  const present = (kids) => kids.flat().filter((kid) => kid != null && kid !== false);
  const add = (parent, ...kids) => parent.append(...present(kids));
  const fill = (parent, ...kids) => parent.replaceChildren(...present(kids));

  const clone = (o) => JSON.parse(JSON.stringify(o));
  const same = (a, b) => String(a == null ? '' : a).toLowerCase() === String(b == null ? '' : b).toLowerCase();

  // 파일의 키는 대소문자가 다를 수 있다 (서버는 무시하고 읽는다) — 이미 있는 키를 그대로 쓴다.
  const keyOf = (obj, name) => Object.keys(obj).find((k) => same(k, name)) || name;
  const get = (obj, name) => obj[keyOf(obj, name)];
  const has = (obj, name) => Object.keys(obj).some((k) => same(k, name));
  const set = (obj, name, value) => {
    if (value === undefined) delete obj[keyOf(obj, name)];
    else obj[keyOf(obj, name)] = value;
  };

  const FLOW_LABEL = { Sequential: '순차', Concurrent: '동시', Debate: '토론', Graph: '그래프' };
  const flowOf = (def) => ['Concurrent', 'Debate', 'Graph'].find((f) => same(f, get(def, 'Flow'))) || 'Sequential';
  const stepsOf = (def) => {
    let steps = get(def, 'Steps');
    if (!Array.isArray(steps)) { steps = []; set(def, 'Steps', steps); }
    return steps;
  };

  // ── 상태 ────────────────────────────────────────────────────────────────
  const state = {
    name: '',        // 고른 모드의 이름 칸
    original: null,  // 파일에 있는 이름 (새 모드면 null)
    def: null,       // 고른 모드의 작업 사본
    dirty: false,
    step: -1,        // 고른 단계 (없으면 -1)
    panel: 'mode',   // 인스펙터: 'mode' | 'step' | 'role'
    role: null,      // { name, isNew, def(없으면 null — 내장 역할 읽기 전용), dirty }
  };
  let submitting = false;
  let rendering = false;

  const modeEntry = (name) => data.modes.find((m) => same(m.name, name));
  const roleEntry = (name) => data.roles.find((r) => same(r.name, name));
  const anyDirty = () => state.dirty || (state.role && state.role.dirty);
  const okToLose = () => !anyDirty() || window.confirm('저장하지 않은 변경이 있습니다. 버릴까요?');
  // 저장 · 삭제는 화면을 다시 읽으므로 보내지 않는 쪽(모드 ↔ 역할)의 고치던 내용이 사라진다 — 먼저 묻는다.
  const okToLoseRole = () => !(state.role && state.role.dirty) || window.confirm('저장하지 않은 역할 변경은 사라집니다. 계속할까요?');
  const okToLoseMode = () => !state.dirty || window.confirm('저장하지 않은 모드 변경은 사라집니다. 계속할까요?');

  function markDirty() {
    state.dirty = true;
    renderHead();
  }

  // ── 단계 연결 (서버 ModeGraph와 같은 규칙 — 그리기 · 변환용, 최종 검증은 서버) ──
  // 단계마다 { wait: 기다릴 앞 단계 번호들, sees: 그중 프롬프트에 넣을 것, input: 요청을 보는지 } (번호는 0부터)
  function links(def) {
    const steps = stepsOf(def);
    const flow = flowOf(def);
    return steps.map((step, i) => {
      if (flow === 'Concurrent') return { wait: [], sees: [], input: true };
      if (flow === 'Graph') {
        const from = get(step, 'From');
        const names = Array.isArray(from) ? from : ['input'];
        const sees = [];
        let input = false;
        for (const name of names) {
          if (same(name, 'input')) { input = true; continue; }
          const j = steps.findIndex((other, k) => k < i && same(get(other, 'Id'), name));
          if (j >= 0 && !sees.includes(j)) sees.push(j);
        }
        sees.sort((a, b) => a - b);
        return { wait: sees, sees, input };
      }
      if (i === 0) return { wait: [], sees: [], input: true };
      const value = get(step, 'Sees');
      if (same(value, 'Input')) return { wait: [i - 1], sees: [], input: true };
      if (same(value, 'Previous')) return { wait: [i - 1], sees: [i - 1], input: false };
      if (same(value, 'All')) {
        const all = Array.from({ length: i }, (_, k) => k);
        return { wait: all, sees: all, input: true };
      }
      return { wait: [i - 1], sees: [i - 1], input: true };
    });
  }

  function uniqueId(steps, index) {
    for (let n = index + 1; ; n++) {
      const id = 's' + n;
      if (!steps.some((s) => same(get(s, 'Id'), id))) return id;
    }
  }

  // Graph 단계의 From을 다시 적는다: 요청 먼저, 그다음 단계 번호 차례.
  function writeFrom(step, input, sees) {
    const steps = stepsOf(state.def);
    const ids = sees.slice().sort((a, b) => a - b).map((j) => get(steps[j], 'Id'));
    set(step, 'From', (input ? ['input'] : []).concat(ids));
  }

  // ── 그래프 ──────────────────────────────────────────────────────────────
  // 세 열(예: 요청 → 둘 동시 → 합치기, 토론자 둘 + 판정자)이 가운데 칸에 가로 스크롤 없이 들어가는 크기
  const NODE_W = 176, NODE_H = 104, COL_W = 205, GAP_Y = 26, PAD = 24, X0 = 100;
  const wiresEditable = () => !!state.def && flowOf(state.def) === 'Graph';
  // 노드 id에 그린 차례를 붙인다 — 다시 그릴 때 같은 id를 쓰면 지워지는 중인 옛 노드와 겹쳐 화면에 둘이 남는다.
  let generation = 0;
  const inputId = () => 'input_' + generation;
  const stepId = (index) => 's' + index + '_' + generation;
  const isInput = (cellId) => String(cellId).startsWith('input');
  const orderOf = (cellId) => isInput(cellId) ? -1 : Number(String(cellId).slice(1).split('_')[0]);

  X6.Shape.HTML.register({
    shape: 'me-step',
    width: NODE_W,
    height: NODE_H,
    html: (cell) => stepNode(cell.getData().index),
  });

  const graph = new X6.Graph({
    container: $('me-graph'),
    width: 800,
    height: 240,
    interacting: { nodeMovable: false, edgeMovable: false, arrowheadMovable: false, vertexMovable: false, vertexAddable: false, vertexDeletable: false },
    panning: false,
    mousewheel: false,
    connecting: {
      snap: { radius: 30 },
      allowBlank: false,
      allowLoop: false,
      allowMulti: false,
      allowNode: false,
      allowEdge: false,
      highlight: true,
      connector: 'smooth',
      createEdge() { return graph.createEdge({ attrs: lineAttrs(false), connector: 'smooth' }); },
      validateMagnet({ magnet }) { return wiresEditable() && magnet.getAttribute('port-group') === 'out'; },
      validateConnection({ sourceCell, targetCell, targetMagnet }) {
        if (!wiresEditable() || !sourceCell || !targetCell || !targetMagnet) return false;
        if (targetMagnet.getAttribute('port-group') !== 'in') return false;
        return orderOf(sourceCell.id) < orderOf(targetCell.id); // 요청 · 앞 단계 → 뒤 단계만
      },
    },
  });

  // SVG 속성에는 CSS 변수를 쓸 수 없어 화면 색을 읽어 넣는다 (밝은 · 어두운 화면 공용).
  const color = (name, fallback) => getComputedStyle(host).getPropertyValue(name).trim() || fallback;
  const ACCENT = color('--accent', '#3b6ef5'), PANEL = color('--panel', '#ffffff'), LINE = color('--line', '#e3e6ec'),
    CODE = color('--code', '#f1f3f6'), TEXT = color('--text', '#1d2330');

  function lineAttrs(dashed) {
    const line = { stroke: ACCENT, strokeWidth: 2 };
    if (dashed) line.strokeDasharray = '5 4';
    return { line };
  }

  function ports(withIn, editable) {
    const circle = (fill) => ({ circle: { r: editable ? 6 : 3, magnet: editable, stroke: ACCENT, strokeWidth: 2, fill } });
    return {
      groups: { in: { position: 'left', attrs: circle(PANEL) }, out: { position: 'right', attrs: circle(ACCENT) } },
      items: (withIn ? [{ id: 'in', group: 'in' }] : []).concat([{ id: 'out', group: 'out' }]),
    };
  }

  // 선을 이었다: 받는 단계의 From에 보내는 쪽을 더한다.
  graph.on('edge:connected', ({ edge }) => {
    if (rendering) return;
    const to = orderOf(edge.getTargetCellId());
    const from = orderOf(edge.getSourceCellId());
    const steps = stepsOf(state.def);
    const link = links(state.def)[to];
    if (from >= 0 && !get(steps[from], 'Id')) set(steps[from], 'Id', uniqueId(steps, from));
    writeFrom(steps[to], link.input || from < 0, from < 0 ? link.sees : link.sees.concat([from]));
    markDirty();
    render();
  });

  // 선을 지웠다 (삭제 단추). 끌다가 놓친 임시 선은 받는 쪽이 없어 건너뛴다.
  graph.on('edge:removed', ({ edge }) => {
    if (rendering || !wiresEditable()) return;
    const targetId = edge.getTargetCellId();
    const sourceId = edge.getSourceCellId();
    if (!targetId || !sourceId) return;
    const to = orderOf(targetId);
    const from = orderOf(sourceId);
    const steps = stepsOf(state.def);
    const link = links(state.def)[to];
    if (!link) return;
    writeFrom(steps[to], from < 0 ? false : link.input, link.sees.filter((j) => j !== from));
    markDirty();
    render();
  });

  // 선을 누르거나 마우스를 올리면 삭제 단추를 띄운다. 누른 선의 단추는 다른 곳을 누를 때까지 남는다.
  let pinnedEdge = null;
  const showRemove = (edge) => {
    if (wiresEditable() && !edge.hasTool('button-remove')) edge.addTools([{ name: 'button-remove', args: { distance: '50%' } }]);
  };
  const unpinEdge = () => {
    if (pinnedEdge) pinnedEdge.removeTools();
    pinnedEdge = null;
  };
  graph.on('edge:click', ({ edge }) => {
    if (!wiresEditable()) return;
    if (pinnedEdge !== edge) unpinEdge();
    showRemove(edge);
    pinnedEdge = edge;
  });
  graph.on('edge:mouseenter', ({ edge }) => showRemove(edge));
  graph.on('edge:mouseleave', ({ edge }) => { if (edge !== pinnedEdge) edge.removeTools(); });

  graph.on('node:click', ({ node }) => {
    unpinEdge();
    if (isInput(node.id)) return;
    selectStep(orderOf(node.id));
  });
  graph.on('blank:click', () => {
    unpinEdge();
    if (state.panel === 'role' && state.role && state.role.dirty) return;
    state.step = -1;
    state.panel = 'mode';
    markSelectedNode();
    renderInspector();
  });

  function selectStep(index) {
    state.step = index;
    state.panel = 'step';
    markSelectedNode();
    renderInspector();
  }

  // 노드를 다시 그리지 않고 고른 표시만 바꾼다 (다시 그리면 열려 있던 드롭다운이 닫힌다).
  function markSelectedNode() {
    for (const node of host.querySelectorAll('.me-node')) node.classList.toggle('sel', Number(node.dataset.index) === state.step);
  }

  function optionList(select, items, current) {
    if (current && !items.some((item) => same(item.value, current))) select.append(el('option', { value: current, text: current + ' (없음)' }));
    for (const item of items) select.append(el('option', { value: item.value, text: item.label, disabled: item.disabled && !same(item.value, current) }));
    const match = Array.from(select.options).find((o) => same(o.value, current));
    select.value = match ? match.value : '';
  }

  const roleItems = () => data.roles.map((r) => ({ value: r.name, label: r.name }));
  const agentItems = () => data.agents.map((a) => ({ value: a.name, label: a.name + ' · ' + a.model + (a.enabled ? '' : ' (꺼짐)'), disabled: !a.enabled }));

  // 끝 단계: 다른 단계가 기다리지 않는 단계 (반환 범위 Last에서 돌려주는 것)
  function terminals(def) {
    const waited = new Set(links(def).flatMap((l) => l.wait));
    return stepsOf(def).map((_, i) => i).filter((i) => !waited.has(i));
  }

  function stepNode(index) {
    const def = state.def;
    const step = stepsOf(def)[index];
    const flow = flowOf(def);
    const box = el('div', { class: 'me-node' + (index === state.step ? ' sel' : ''), 'data-index': index });
    if (!step) return box;

    let title = (index + 1) + '단계';
    if (flow === 'Debate') title = index < 2 ? '토론자 ' + (index + 1) : '판정자';
    else if (get(step, 'Id')) title += ' · ' + get(step, 'Id');
    const badges = [];
    if (flow !== 'Debate' && (same(get(def, 'Return'), 'All') || terminals(def).includes(index))) badges.push('반환');
    if (get(step, 'WebSearch')) badges.push('웹검색');
    if (get(step, 'FallbackAgent')) badges.push('대체');
    box.append(el('div', { class: 'me-node-title' }, el('span', { text: title }), badges.map((b) => el('span', { class: 'me-chip', text: b }))));

    const role = el('select', { title: '역할', onchange: (e) => { set(step, 'Role', e.target.value); markDirty(); renderInspector(); } });
    optionList(role, roleItems(), get(step, 'Role'));
    const agent = el('select', { title: '에이전트', onchange: (e) => { set(step, 'Agent', e.target.value); markDirty(); renderInspector(); } });
    optionList(agent, agentItems(), get(step, 'Agent'));
    box.append(role, agent);
    return box;
  }

  function renderGraph() {
    rendering = true;
    try {
      pinnedEdge = null;
      graph.clearCells();
      generation++;
      if (!state.def) return;
      const def = state.def;
      const steps = stepsOf(def);
      const flow = flowOf(def);
      const pos = [];
      const edges = []; // { from: 단계 번호 또는 -1(요청), to, dashed }

      if (flow === 'Debate') {
        // 토론은 연결이 고정이라 줄거리만 그린다: 요청 → 토론자 1 ⇄ 토론자 2 → 판정자 (모두 요청을 보고, 판정자는 토론 전체를 본다)
        steps.forEach((_, i) => { pos[i] = { col: i + 1, row: 0 }; });
        if (steps.length >= 1) edges.push({ from: -1, to: 0 });
        if (steps.length >= 2) edges.push({ from: 0, to: 1, label: '⇄ 번갈아' });
        if (steps.length >= 3) edges.push({ from: 1, to: 2, label: '토론 전체' });
      } else {
        const link = links(def);
        const depth = [];
        const rows = {};
        steps.forEach((_, i) => {
          depth[i] = 1 + Math.max(0, ...link[i].wait.map((w) => depth[w]));
          rows[depth[i]] = rows[depth[i]] || 0;
          pos[i] = { col: depth[i], row: rows[depth[i]]++ };
        });
        steps.forEach((_, i) => {
          if (link[i].input) edges.push({ from: -1, to: i });
          for (const w of link[i].wait) edges.push({ from: w, to: i, dashed: !link[i].sees.includes(w) }); // 점선 = 기다리기만 함
        });
      }

      const maxRow = Math.max(0, ...pos.map((p) => p.row));
      const maxCol = Math.max(1, ...pos.map((p) => p.col));
      const height = Math.max(200, PAD * 2 + (maxRow + 1) * (NODE_H + GAP_Y) - GAP_Y);
      const width = Math.max($('me-graph').parentElement.clientWidth - 2, X0 + maxCol * COL_W);
      graph.resize(width, height);

      const editable = flow === 'Graph';
      graph.addNode({
        id: inputId(), shape: 'rect', x: 14, y: height / 2 - 22, width: 66, height: 44, label: '요청',
        attrs: { body: { stroke: LINE, fill: CODE, rx: 8, ry: 8 }, label: { fill: TEXT, fontSize: 13 } },
        ports: ports(false, editable),
      });
      steps.forEach((_, i) => graph.addNode({
        id: stepId(i), shape: 'me-step', x: X0 + (pos[i].col - 1) * COL_W, y: PAD + pos[i].row * (NODE_H + GAP_Y),
        data: { index: i }, ports: ports(true, editable),
      }));
      for (const edge of edges) {
        graph.addEdge({
          source: { cell: edge.from < 0 ? inputId() : stepId(edge.from), port: 'out' },
          target: { cell: stepId(edge.to), port: 'in' },
          attrs: lineAttrs(edge.dashed), connector: 'smooth',
          labels: edge.label ? [{ attrs: { label: { text: edge.label, fill: TEXT, fontSize: 11 }, body: { fill: PANEL } } }] : [],
        });
      }
    } finally {
      rendering = false;
    }
  }

  // ── 목록 · 머리 · 단추 ───────────────────────────────────────────────────
  function renderLists() {
    const modes = $('me-mode-list');
    modes.replaceChildren();
    for (const mode of data.modes) {
      const flow = flowOf(mode.def);
      modes.append(el('button', {
        type: 'button', class: 'me-item' + (same(mode.name, state.original) ? ' sel' : ''), title: mode.error || get(mode.def, 'Description') || '',
        onclick: () => selectMode(mode.name),
      },
        el('span', { class: 'mono', text: mode.name }),
        el('span', { class: 'me-item-meta' },
          mode.error ? el('span', { class: 'err', text: '설정 오류 ' }) : null,
          FLOW_LABEL[flow] + (mode.runs7d ? ' · 7일 ' + mode.runs7d + '회' : ''))));
    }
    if (state.def && state.original === null) {
      modes.append(el('button', { type: 'button', class: 'me-item sel' }, el('span', { class: 'mono', text: state.name || '(새 모드)' }), el('span', { class: 'me-item-meta', text: '새 모드 — 저장 전' })));
    }

    const roles = $('me-role-list');
    roles.replaceChildren();
    for (const role of data.roles) {
      const kind = role.builtIn ? (role.def ? '내장 · 덮어씀' : '내장') : '';
      roles.append(el('button', {
        type: 'button', class: 'me-item' + (state.role && same(state.role.name, role.name) && !state.role.isNew ? ' sel' : ''),
        onclick: () => openRole(role.name),
      },
        el('span', { class: 'mono', text: role.name }),
        el('span', { class: 'me-item-meta', text: [kind, role.usedBy.length ? '모드 ' + role.usedBy.length : '안 씀'].filter(Boolean).join(' · ') })));
    }
  }

  function renderHead() {
    const head = $('me-head');
    head.replaceChildren();
    if (!state.def) {
      head.append(el('span', { class: 'muted', text: '왼쪽에서 모드를 고르거나 새 모드를 만드세요.' }));
      return;
    }
    add(head,
      el('span', { class: 'me-name mono', text: state.name || '(이름 없음)' }),
      el('span', { class: 'badge', text: FLOW_LABEL[flowOf(state.def)] }),
      el('span', { class: 'muted', text: get(state.def, 'Description') || '' }),
      state.dirty ? el('span', { class: 'badge warn', text: '저장 안 함' }) : null);
  }

  // 단추는 머리줄과 따로 그린다: 머리줄은 고칠 때마다 다시 그리는데(markDirty), 단추까지 다시 만들면
  // 입력칸을 떠나게 한 그 누름이 사라진다 (누르는 사이 단추가 바뀌어서).
  function renderActions() {
    if (!state.def) {
      $('me-actions').replaceChildren();
      return;
    }
    const flow = flowOf(state.def);
    const steps = stepsOf(state.def);
    const canAdd = flow === 'Debate' ? steps.length < 3 : flow !== 'Graph' || steps.length < data.limits.maxGraphSteps;
    fill($('me-actions'),
      el('button', { type: 'button', class: 'btn', disabled: !canAdd, onclick: addStep, text: flow === 'Debate' ? '판정자 추가' : '단계 추가' }),
      flow === 'Sequential' || flow === 'Concurrent' ? el('button', { type: 'button', class: 'btn', onclick: toGraph, text: '그래프로 바꾸기' }) : null,
      el('span', { class: 'me-spacer' }),
      el('button', { type: 'button', class: 'btn', onclick: duplicateMode, text: '복제' }),
      state.original !== null ? el('button', { type: 'button', class: 'btn', onclick: deleteMode, text: '삭제' }) : null,
      el('button', { type: 'button', class: 'btn primary', onclick: saveMode, text: '모드 저장' }));
  }

  // ── 인스펙터 ────────────────────────────────────────────────────────────
  function field(label, control, hint) {
    return el('label', { class: 'me-field' }, el('span', { class: 'me-label', text: label }), control, hint ? el('span', { class: 'me-hint', text: hint }) : null);
  }

  function textInput(value, onInput, props) {
    return el('input', Object.assign({ type: 'text', value: value == null ? '' : value, oninput: (e) => onInput(e.target.value) }, props || {}));
  }

  // 숫자 칸: 비우면 키를 지운다 (기본값 사용)
  function numberInput(obj, key, props) {
    const value = get(obj, key);
    return el('input', Object.assign({
      type: 'number', value: value == null ? '' : value,
      oninput: (e) => { set(obj, key, e.target.value === '' ? undefined : Number(e.target.value)); markDirty(); },
    }, props || {}));
  }

  function checkbox(checked, onChange) {
    const box = el('input', { type: 'checkbox', onchange: (e) => onChange(e.target.checked) });
    box.checked = !!checked;
    return box;
  }

  function choice(items, current, onChange) {
    const select = el('select', { onchange: (e) => onChange(e.target.value) });
    for (const [value, label] of items) select.append(el('option', { value, text: label }));
    const match = items.find(([value]) => same(value, current));
    select.value = match ? match[0] : items[0][0];
    return select;
  }

  function renderInspector() {
    const box = $('me-inspector');
    box.replaceChildren();
    host.classList.toggle('me-wide', state.panel === 'role' && !!state.role); // 프롬프트를 고칠 때는 오른쪽 칸을 넓힌다
    if (state.panel === 'role' && state.role) return renderRolePanel(box);
    if (!state.def) return;
    if (state.panel === 'step' && state.step >= 0 && stepsOf(state.def)[state.step]) return renderStepPanel(box);
    renderModePanel(box);
  }

  function renderModePanel(box) {
    const def = state.def;
    const flow = flowOf(def);
    const entry = state.original !== null ? modeEntry(state.original) : null;
    box.append(el('h3', { text: '모드' }));
    if (entry && entry.error) box.append(el('div', { class: 'me-warn', text: '설정 오류: ' + entry.error }));
    box.append(
      field('이름', textInput(state.name, (v) => { state.name = v; markDirty(); renderLists(); }, { class: 'mono' }),
        state.original !== null ? '이름을 바꾸면 이 이름을 부르는 명령 파일 · 훅이 깨질 수 있습니다.' : '영문 · 숫자 · _ · - (예: design_main)'),
      field('설명', el('textarea', { rows: 2, oninput: (e) => { set(def, 'Description', e.target.value); markDirty(); } }, get(def, 'Description') || ''),
        'list_modes에서 지휘자에게 보이는 한 줄'));

    if (flow === 'Graph') {
      box.append(field('흐름', el('span', { text: '그래프 — 노드의 점을 끌어 선을 잇고, 선을 누르면 나오는 단추로 지웁니다.' })));
    } else {
      box.append(field('흐름', choice([['Sequential', '순차'], ['Concurrent', '동시'], ['Debate', '토론']], flow, (v) => {
        if (v === 'Debate' && (stepsOf(def).length < 2 || stepsOf(def).length > 3)) {
          window.alert('토론은 단계가 2개(토론자 둘) 또는 3개(+판정자)여야 합니다.');
          return renderInspector();
        }
        set(def, 'Flow', v);
        markDirty();
        render();
      }), flow === 'Debate' ? '토론자 둘이 번갈아 말하고, 판정자가 있으면 마지막에 판정' : '선을 직접 이으려면 아래 "그래프로 바꾸기"'));
    }

    if (flow === 'Concurrent') {
      box.append(field('반환', el('span', { text: '모든 단계 (동시는 항상 전부)' })));
    } else {
      const last = flow === 'Graph' ? '끝 단계만' : '마지막만';
      box.append(field('반환', choice([['', '기본 (' + last + ')'], ['Last', last], ['All', '모든 단계']], get(def, 'Return') || '', (v) => {
        set(def, 'Return', v || undefined);
        markDirty();
        renderGraph();
      })));
    }

    if (flow === 'Debate') {
      box.append(
        field('최대 회차', numberInput(def, 'MaxRounds', { min: 1, max: data.limits.maxRounds, placeholder: '2' }), '1회차 = 두 토론자가 한 번씩'),
        field('직전 발언 수', numberInput(def, 'HistoryWindow', { min: 1, placeholder: '2' }), '각 턴이 볼 직전 발언 개수'));
    }

    box.append(
      field('자체 검토 필수', checkbox(get(def, 'RequireOwnReview'), (v) => { set(def, 'RequireOwnReview', v ? true : undefined); markDirty(); }), 'run_mode의 ownReview가 없으면 거절'),
      field('저장소 사본', checkbox(get(def, 'RepoCopy'), (v) => { set(def, 'RepoCopy', v ? true : undefined); markDirty(); }), 'claude-cli 단계의 작업 폴더에 저장소 추적 파일 사본을 깐다'),
      field('프롬프트 캐시', choice([['', '기본 (제공사 설정)'], ['Off', 'Off'], ['Context', 'Context']], get(def, 'PromptCache') || '', (v) => { set(def, 'PromptCache', v || undefined); markDirty(); })),
      field('입력 상한(글자)', numberInput(def, 'MaxInputChars', { min: 1, placeholder: '기본' })));

    if (entry) box.append(el('div', { class: 'me-hint', text: '최근 7일 실행 ' + entry.runs7d + '회' }));
  }

  function renderStepPanel(box) {
    const def = state.def;
    const steps = stepsOf(def);
    const index = state.step;
    const step = steps[index];
    const flow = flowOf(def);
    const again = () => { markDirty(); renderGraph(); renderInspector(); };

    box.append(el('h3', { text: flow === 'Debate' ? (index < 2 ? '토론자 ' + (index + 1) : '판정자') : (index + 1) + '단계' }));

    if (flow === 'Graph') {
      box.append(field('Id', el('input', { type: 'text', class: 'mono', value: get(step, 'Id') || '', onchange: (e) => renameId(index, e.target) }),
        '다른 단계가 이 단계를 가리킬 이름 (칸을 떠나면 반영)'));
    }

    const role = el('select', { onchange: (e) => { set(step, 'Role', e.target.value); again(); } });
    optionList(role, roleItems(), get(step, 'Role'));
    box.append(field('역할', el('span', { class: 'me-row' }, role, el('button', { type: 'button', class: 'btn', onclick: () => openRole(get(step, 'Role')), text: '역할 편집' }))));

    const agent = el('select', { onchange: (e) => { set(step, 'Agent', e.target.value); again(); } });
    optionList(agent, agentItems(), get(step, 'Agent'));
    box.append(field('에이전트', agent));

    const fallback = el('select', { onchange: (e) => { set(step, 'FallbackAgent', e.target.value || undefined); again(); } });
    fallback.append(el('option', { value: '', text: '없음' }));
    optionList(fallback, agentItems(), get(step, 'FallbackAgent'));
    box.append(field('대체 에이전트', fallback, '한도 소진으로 실패하면 같은 입력으로 대신 부를 에이전트'));

    if (flow === 'Sequential' && index > 0) {
      box.append(field('보는 내용', choice([['', '기본 (요청 + 앞 단계)'], ['Input', '요청만'], ['Previous', '앞 단계만'], ['InputAndPrevious', '요청 + 앞 단계'], ['All', '요청 + 앞 단계 전부']],
        get(step, 'Sees') || '', (v) => { set(step, 'Sees', v || undefined); again(); })));
    }

    if (flow === 'Graph') {
      // 선을 끄는 대신 여기서 골라도 된다
      const link = links(def)[index];
      const sees = el('div', { class: 'me-checks' });
      const toggle = (input, list) => { writeFrom(step, input, list); markDirty(); render(); };
      sees.append(el('label', {}, checkbox(link.input, (v) => toggle(v, link.sees)), ' 요청'));
      for (let j = 0; j < index; j++) {
        const label = (j + 1) + '단계' + (get(steps[j], 'Id') ? ' · ' + get(steps[j], 'Id') : '');
        sees.append(el('label', {}, checkbox(link.sees.includes(j), (v) => {
          if (v && !get(steps[j], 'Id')) set(steps[j], 'Id', uniqueId(steps, j));
          toggle(link.input, v ? link.sees.concat([j]) : link.sees.filter((k) => k !== j));
        }), ' ' + label));
      }
      box.append(field('보는 것', sees, !link.input && link.sees.length === 0 ? '아무것도 보지 않는 단계는 저장이 거절됩니다.' : null));
    }

    // 웹검색
    const search = get(step, 'WebSearch');
    box.append(field('웹검색', checkbox(!!search, (v) => { set(step, 'WebSearch', v ? { MaxCalls: 5, BlockedDomains: [] } : undefined); again(); })));
    if (search) {
      const current = data.agents.find((a) => same(a.name, get(step, 'Agent')));
      if (current && current.providerType !== data.limits.searchProviderType) {
        box.append(el('div', { class: 'me-warn', text: '이 에이전트는 검색할 수 없습니다 — 제공사 형식이 ' + data.limits.searchProviderType + '여야 합니다.' }));
      }
      const domains = get(search, 'BlockedDomains');
      box.append(
        field('검색 최대 횟수', numberInput(search, 'MaxCalls', { min: 1, max: data.limits.maxSearchCalls })),
        field('검색 결과 분량', choice([['', '기본 (medium)'], ['low', 'low'], ['medium', 'medium'], ['high', 'high']], get(search, 'ContextSize') || '', (v) => { set(search, 'ContextSize', v || undefined); markDirty(); })),
        field('막는 도메인', el('textarea', {
          rows: 3, placeholder: 'example.com (한 줄에 하나)',
          oninput: (e) => { set(search, 'BlockedDomains', e.target.value.split('\n').map((d) => d.trim()).filter(Boolean)); markDirty(); },
        }, Array.isArray(domains) ? domains.join('\n') : '')));
    }

    const minSteps = flow === 'Debate' ? 2 : 1;
    box.append(el('div', { class: 'me-row me-step-ops' },
      flow !== 'Debate' ? el('button', { type: 'button', class: 'btn', disabled: index === 0, onclick: () => moveStep(index, -1), text: '앞으로' }) : null,
      flow !== 'Debate' ? el('button', { type: 'button', class: 'btn', disabled: index === steps.length - 1, onclick: () => moveStep(index, 1), text: '뒤로' }) : null,
      el('button', { type: 'button', class: 'btn', disabled: steps.length <= minSteps || (flow === 'Debate' && index < 2), onclick: () => deleteStep(index), text: '단계 삭제' })));
  }

  // ── 단계 고치기 ─────────────────────────────────────────────────────────
  function addStep() {
    const def = state.def;
    const steps = stepsOf(def);
    const role = data.roles[0] ? data.roles[0].name : '';
    const agent = (data.agents.find((a) => a.enabled) || data.agents[0] || { name: '' }).name;
    const step = { Role: role, Agent: agent };
    if (flowOf(def) === 'Graph') step.From = ['input'];
    steps.push(step);
    state.step = steps.length - 1;
    state.panel = 'step';
    markDirty();
    render();
  }

  function deleteStep(index) {
    const def = state.def;
    const steps = stepsOf(def);
    if (flowOf(def) === 'Graph') {
      // 이 단계를 가리키던 From에서 뺀다 (번호가 바뀌기 전에 다시 적는다)
      const link = links(def);
      steps.forEach((step, i) => {
        if (i > index && link[i].sees.includes(index)) writeFrom(step, link[i].input, link[i].sees.filter((j) => j !== index));
      });
    }
    steps.splice(index, 1);
    state.step = -1;
    state.panel = 'mode';
    markDirty();
    render();
  }

  function moveStep(index, delta) {
    const def = state.def;
    const steps = stepsOf(def);
    const other = index + delta;
    if (other < 0 || other >= steps.length) return;
    if (flowOf(def) === 'Graph') {
      // 뒤로 가는 단계를 앞에 오게 될 단계가 보고 있으면 옮길 수 없다 (From은 앞 단계만 가리킨다)
      const [front, back] = delta < 0 ? [other, index] : [index, other];
      if (links(def)[back].sees.includes(front)) {
        window.alert((back + 1) + '단계가 ' + (front + 1) + '단계를 보고 있어 차례를 바꿀 수 없습니다. 그 선을 먼저 지우세요.');
        return;
      }
    }
    [steps[index], steps[other]] = [steps[other], steps[index]];
    state.step = other;
    markDirty();
    render();
  }

  // Id는 다 적은 뒤(칸을 떠나거나 Enter)에 한 번만 반영한다 — 글자마다 반영하면 적는 도중 잠깐 다른 단계의 Id와 같아져
  // 그 단계를 보던 연결까지 이 단계로 바뀐다. 이 단계를 보던 연결은 이름이 아니라 번호로 찾아 다시 적는다.
  function renameId(index, input) {
    const steps = stepsOf(state.def);
    const before = get(steps[index], 'Id') || '';
    const after = input.value.trim();
    if (after === before) return;
    const link = links(state.def);
    const watchers = steps.map((_, i) => i).filter((i) => i > index && link[i].sees.includes(index));
    let problem = null;
    if (!after && watchers.length) problem = '다른 단계가 이 단계를 보고 있어 Id를 비울 수 없습니다.';
    else if (same(after, 'input')) problem = '"input"은 요청을 뜻하는 이름이라 Id로 쓸 수 없습니다.';
    else if (after && steps.some((step, i) => i !== index && same(get(step, 'Id'), after))) problem = "Id '" + after + "'은(는) 다른 단계가 쓰고 있습니다.";
    if (problem) {
      window.alert(problem);
      input.value = before;
      return;
    }
    set(steps[index], 'Id', after || undefined);
    for (const i of watchers) writeFrom(steps[i], link[i].input, link[i].sees);
    markDirty();
    renderGraph(); // 인스펙터 · 단추는 다시 만들지 않는다 — 칸을 떠나게 한 그 누름(예: 모드 저장)이 사라지지 않게
  }

  function toGraph() {
    const def = state.def;
    const steps = stepsOf(def);
    if (flowOf(def) === 'Sequential' && steps.some((step, i) => i > 0 && same(get(step, 'Sees'), 'Input'))) {
      window.alert('"요청만" 보는 뒤 단계가 있어 바꿀 수 없습니다. 그래프에는 앞 단계를 기다리기만 하는 연결을 적을 수 없습니다.');
      return;
    }
    if (steps.length > data.limits.maxGraphSteps) {
      window.alert('그래프 모드는 단계가 ' + data.limits.maxGraphSteps + '개까지입니다.');
      return;
    }
    if (!window.confirm('지금 연결 그대로 그래프 흐름으로 바꿉니다 (저장해야 반영).\n되돌리기는 없고, 다단계 골든 실행(crash)은 순차 모드만 받습니다. 계속할까요?')) return;

    const link = links(def);
    steps.forEach((step, i) => { if (!get(step, 'Id')) set(step, 'Id', uniqueId(steps, i)); });
    steps.forEach((step, i) => {
      writeFrom(step, link[i].input, link[i].sees);
      set(step, 'Sees', undefined);
    });
    set(def, 'Flow', 'Graph');
    markDirty();
    render();
  }

  // ── 모드 고르기 · 만들기 · 저장 ──────────────────────────────────────────
  function selectMode(name) {
    const entry = modeEntry(name);
    if (!entry || !okToLose()) return;
    state.name = entry.name;
    state.original = entry.name;
    state.def = clone(entry.def);
    state.dirty = false;
    state.step = -1;
    state.panel = 'mode';
    state.role = null;
    render();
  }

  function startNew(name, def) {
    state.name = name;
    state.original = null;
    state.def = def;
    state.dirty = true;
    state.step = -1;
    state.panel = 'mode';
    state.role = null;
    render();
  }

  function newMode() {
    if (!okToLose()) return;
    const role = data.roles[0] ? data.roles[0].name : '';
    const agent = (data.agents.find((a) => a.enabled) || data.agents[0] || { name: '' }).name;
    startNew('', { Description: '', Flow: 'Graph', Steps: [{ Role: role, Agent: agent }] });
  }

  function duplicateMode() {
    if (!okToLoseRole()) return;
    if (state.dirty && !window.confirm('저장하지 않은 변경까지 그대로 복제합니다. 계속할까요?')) return;
    startNew(state.name + '_copy', clone(state.def));
  }

  function submit(formId, values) {
    const form = $(formId);
    for (const [key, value] of Object.entries(values)) form.elements[key].value = value == null ? '' : value;
    submitting = true;
    form.submit();
  }

  function saveMode() {
    const name = state.name.trim();
    if (!name) return window.alert('모드 이름을 적으세요.');
    if (!okToLoseRole()) return;
    const entry = state.original !== null ? modeEntry(state.original) : null;
    if (entry && !same(entry.name, name)
      && !window.confirm("모드 이름을 '" + entry.name + "' → '" + name + "'(으)로 바꿉니다.\n명령 파일 · 훅이 옛 이름을 부르면 그 명령이 깨집니다. (최근 7일 실행 " + entry.runs7d + '회)\n계속할까요?')) return;
    submit('me-form-mode', { originalName: state.original, name, modeJson: JSON.stringify(state.def) });
  }

  function deleteMode() {
    const entry = modeEntry(state.original);
    if (!entry || !okToLoseRole()) return;
    if (!window.confirm("모드 '" + entry.name + "'을(를) 지웁니다.\n명령 파일 · 훅이 이 이름을 부르면 그 명령이 깨집니다. (최근 7일 실행 " + entry.runs7d + '회)\n계속할까요?')) return;
    submit('me-form-delete-mode', { name: entry.name });
  }

  // ── 역할 ────────────────────────────────────────────────────────────────
  function openRole(name) {
    const entry = roleEntry(name);
    if (!entry) return window.alert("역할 '" + name + "'이(가) 없습니다. 새 역할로 만드세요.");
    if (state.role && state.role.dirty && !window.confirm('저장하지 않은 역할 변경이 있습니다. 버릴까요?')) return;
    state.role = { name: entry.name, isNew: false, def: entry.def ? clone(entry.def) : null, dirty: false };
    state.panel = 'role';
    renderLists();
    renderInspector();
  }

  function newRole() {
    if (state.role && state.role.dirty && !window.confirm('저장하지 않은 역할 변경이 있습니다. 버릴까요?')) return;
    state.role = { name: '', isNew: true, def: { Prompt: '' }, dirty: true };
    state.panel = 'role';
    renderLists();
    renderInspector();
  }

  function renderRolePanel(box) {
    const role = state.role;
    const entry = role.isNew ? null : roleEntry(role.name);
    const touch = () => { role.dirty = true; };
    const close = el('button', { type: 'button', class: 'btn', text: '닫기', onclick: () => {
      if (role.dirty && !window.confirm('저장하지 않은 역할 변경이 있습니다. 버릴까요?')) return;
      state.role = null;
      state.panel = state.step >= 0 ? 'step' : 'mode';
      renderLists();
      renderInspector();
    } });
    box.append(el('div', { class: 'me-row' }, el('h3', { text: '역할' }), el('span', { class: 'me-spacer' }), close));

    if (role.isNew) box.append(field('이름', textInput(role.name, (v) => { role.name = v; touch(); }, { class: 'mono' }), '영문 · 숫자 · _ · - (저장 뒤에는 이름을 바꿀 수 없습니다)'));
    else box.append(field('이름', el('span', { class: 'mono', text: role.name })));
    if (entry) box.append(el('div', { class: 'me-hint', text: entry.usedBy.length ? '쓰는 모드: ' + entry.usedBy.join(', ') : '쓰는 모드 없음' }));

    if (!role.def) {
      // 내장 역할: 코드의 프롬프트를 보이기만 한다
      box.append(
        el('div', { class: 'me-hint', text: '서버 코드에 내장된 역할입니다. 고치려면 modes.json에 같은 이름으로 덮어씁니다.' }),
        field('프롬프트 (읽기 전용)', el('textarea', { rows: 16, class: 'me-prompt', readonly: true, spellcheck: 'false' }, entry.builtInPrompt || '')),
        el('button', { type: 'button', class: 'btn', text: 'modes.json에서 덮어쓰기', onclick: () => {
          role.def = { Prompt: entry.builtInPrompt || '' };
          if (entry.builtInOutput) role.def.Output = entry.builtInOutput;
          role.dirty = true;
          renderInspector();
        } }));
      return;
    }

    const def = role.def;
    if (get(def, 'PromptFile') && !get(def, 'Prompt')) {
      box.append(field('프롬프트 파일', el('span', { class: 'mono', text: get(def, 'PromptFile') }), '파일로 둔 프롬프트는 그 파일에서 고칩니다.'));
    } else {
      box.append(field('프롬프트', el('textarea', { rows: 18, class: 'me-prompt', spellcheck: 'false', oninput: (e) => { set(def, 'Prompt', e.target.value); touch(); } }, get(def, 'Prompt') || ''),
        '고치면 이 역할의 골든 평가 기준도 달라집니다. 출력 형식(구조화 출력)은 여기 적지 않습니다.'));
    }
    box.append(
      field('리뷰 지침 붙이기', checkbox(get(def, 'UseGuidelines') !== false, (v) => { set(def, 'UseGuidelines', v ? (has(def, 'UseGuidelines') ? true : undefined) : false); touch(); }),
        '프로젝트 REVIEW_GUIDELINES.md를 시스템 프롬프트에 붙인다'),
      field('구조화 출력', choice([['', '없음 (자유 글)']].concat(data.outputs.map((o) => [o, o])), get(def, 'Output') || '', (v) => { set(def, 'Output', v || undefined); touch(); })),
      field('출력 절 개수', el('input', {
        type: 'number', min: 1, placeholder: '검사 안 함', value: get(def, 'OutputSections') == null ? '' : get(def, 'OutputSections'),
        oninput: (e) => { set(def, 'OutputSections', e.target.value === '' ? undefined : Number(e.target.value)); touch(); },
      }), '자유 글 출력의 절 수 — 빠지면 응답 검사기가 경고한다'));

    box.append(el('div', { class: 'me-row me-step-ops' },
      entry && entry.def ? el('button', { type: 'button', class: 'btn', text: entry.builtIn ? '덮어쓰기 지우기' : '역할 삭제', onclick: () => {
        if (!okToLoseMode()) return;
        if (!window.confirm("역할 '" + role.name + "'을(를) modes.json에서 지웁니다. 계속할까요?")) return;
        submit('me-form-delete-role', { name: role.name, mode: state.original });
      } }) : null,
      el('span', { class: 'me-spacer' }),
      el('button', { type: 'button', class: 'btn primary', text: '역할 저장', onclick: () => {
        const name = role.name.trim();
        if (!name) return window.alert('역할 이름을 적으세요.');
        if (!okToLoseMode()) return;
        submit('me-form-role', { name, roleJson: JSON.stringify(def), isNew: role.isNew ? 'true' : 'false', mode: state.original });
      } })));
  }

  // ── 전체 그리기 · 시작 ───────────────────────────────────────────────────
  function render() {
    renderLists();
    renderHead();
    renderActions();
    renderGraph();
    renderInspector();
  }

  $('me-new-mode').addEventListener('click', newMode);
  $('me-new-role').addEventListener('click', newRole);
  window.addEventListener('beforeunload', (e) => {
    if (submitting || !anyDirty()) return;
    e.preventDefault();
    e.returnValue = '';
  });

  // 저장이 거절돼 돌아온 편집 내용이 있으면 이어서 고친다.
  const draft = data.draft;
  let restored = false;
  if (draft) {
    try {
      const def = JSON.parse(draft.json);
      if (draft.kind === 'mode') {
        state.name = draft.name;
        state.original = draft.originalName && modeEntry(draft.originalName) ? modeEntry(draft.originalName).name : null;
        state.def = def;
        state.dirty = true;
        restored = true;
      } else if (draft.kind === 'role') {
        // 새 역할로 보낸 것(originalName 없음)은 같은 이름의 역할이 있어도 새 역할로 다시 연다 — 있는 역할을 덮지 않게
        const isNew = !draft.originalName;
        const entry = isNew ? null : roleEntry(draft.name);
        state.role = { name: entry ? entry.name : draft.name, isNew: isNew || !entry, def, dirty: true };
        state.panel = 'role';
      }
    } catch (ex) {
      // 깨진 내용이면 되돌리지 않는다
    }
  }
  if (!restored) {
    const first = (data.selected && modeEntry(data.selected)) || data.modes[0];
    if (first) {
      state.name = first.name;
      state.original = first.name;
      state.def = clone(first.def);
    }
  }
  render();
})();
