/* =========================================================================
   Экран «Клиенты под риском» — реальные данные через ApiAdapter.
   Без демо-сценариев и вымышленных клиентов. Обновление без reload —
   конфигурируемый polling, сверка (evaluationId, snapshotVersion,
   modelVersion, asOf); разные версии не объединяются.
   ========================================================================= */
(function () {
  "use strict";
  const { UI, Fmt } = window;
  const api = window.DataAdapter;
  const $ = (s, r = document) => r.querySelector(s);
  const esc = Fmt.esc;

  let POLL_SECONDS = Math.max(0, Number(window.TETHERED_POLL_SECONDS || 15));
  const MAX_REFRESH_TRIES = 3;

  const LEVEL_FILTERS = [
    { value: "all", label: "Все" },
    { value: "high", label: "Высокий" },
    { value: "medium", label: "Средний" },
    { value: "low", label: "Низкий" },
    { value: "none", label: "Нет оценки" }
  ];
  const MOBILE_SORTS = [
    ["score:desc", "Score: сначала высокий"], ["score:asc", "Score: сначала низкий"],
    ["id:asc", "ID по возрастанию"], ["id:desc", "ID по убыванию"],
    ["email:asc", "Email А→Я"], ["email:desc", "Email Я→А"],
    ["balanceCents:asc", "Баланс по возрастанию"], ["balanceCents:desc", "Баланс по убыванию"],
    ["totalReplenishedCents:asc", "Пополнения по возрастанию"], ["totalReplenishedCents:desc", "Пополнения по убыванию"]
  ];
  const LEVEL_SOLID = { high: "var(--color-risk-high-solid)", medium: "var(--color-risk-medium-solid)", low: "var(--color-risk-low-solid)", none: "var(--color-risk-none-solid)" };

  const state = {
    q: "", level: "all", sort: "score", dir: "desc",
    rows: [], counts: null, snapshot: null, summary: null,
    selectedId: null, pollTimer: null, refreshing: false
  };

  /* ------------------------------------------------ Header & meta */
  function renderHead() {
    const s = state.snapshot;
    const upd = anyLoading()
      ? `<span class="c-spinner" aria-hidden="true"></span>Загрузка…`
      : anyError() ? UI.Badge({ label: "Ошибка загрузки", tone: "danger", dot: true })
      : state.refreshing ? `<span class="c-spinner" aria-hidden="true"></span>Обновление…`
      : UI.Badge({ label: POLL_SECONDS ? "Актуально · автообновление" : "Актуально", tone: "accent", dot: true });

    $("#page-head").innerHTML = `
      <div class="head">
        <div class="head__titles">
          <h1 class="head__title">Клиенты под риском</h1>
          <p class="head__sub">Кому уделить внимание, почему клиент может не продлить услуги и какая сумма продлений под риском.</p>
        </div>
        <div class="head__actions">
          ${UI.Button({ label: "Экспорт CSV", icon: "download", attrs: { "data-action": "csv-export" } })}
          ${UI.Button({ label: "Перечитать", icon: "refresh", spinIcon: true, pending: anyLoading(), attrs: { "data-action": "reload", "aria-label": "Перечитать" } })}
        </div>
      </div>
      <div class="meta" role="group" aria-label="Служебная информация">
        <div class="meta__item"><span class="meta__label">Данные на</span><span class="meta__value num">${s ? esc(Fmt.dateTime(s.takenAt)) : UI.Skeleton("110px")}</span></div>
        <div class="meta__item"><span class="meta__label">Снимок</span><span class="meta__value"><span class="mono">${s && s.snapshotVersion != null ? "v" + esc(s.snapshotVersion) : "—"}</span></span></div>
        <div class="meta__item"><span class="meta__label">Модель</span><span class="meta__value"><span class="mono">${s && s.modelVersion != null ? "v" + esc(s.modelVersion) : "—"}</span></span></div>
        <div class="meta__item"><span class="meta__label">Обновление</span><span class="meta__value" aria-live="polite">${upd}</span></div>
      </div>`;
    $("#notices").innerHTML = state.noData
      ? UI.Banner({ tone: "info", mark: "i", title: "Снимок данных не загружен",
          text: "Рабочий источник пуст. Загрузите JSON-снимок через «Источник данных» — это реальная замена, а не демо-набор." })
      : "";
  }

  /* ------------------------------------------------ KPIs */
  function renderKpis() {
    const el = $("#kpis");
    if (ch.sum.error) {
      el.innerHTML = `<div class="kpi" style="grid-column:1/-1">${UI.Banner({ tone: "danger", title: "Сводка не загрузилась", text: esc(ch.sum.error.message) })}</div>`;
      return;
    }
    if (ch.sum.loading && !state.summary) {
      el.innerHTML = [0,1,2].map((i)=>`<div class="kpi${i===1?" kpi--money":""}">${UI.Skeleton("45%")}<span style="height:8px"></span>${UI.Skeleton("38%")}<span style="height:4px"></span>${UI.Skeleton("70%")}</div>`).join("");
      return;
    }
    const sm = state.summary;
    if (sm && sm.noData) {
      el.innerHTML = `<div class="kpi" style="grid-column:1/-1">${UI.EmptyState({ title: "Нет данных для сводки", text: "Снимок не загружен — загрузите реальные данные через «Источник данных»." })}</div>`;
      return;
    }
    if (!sm) return;
    const total = sm.total || 1;
    const seg = (n, lv) => n ? `<span style="flex:${n};background:${LEVEL_SOLID[lv]}" title="${UI.RISK[lv].long}: ${n}"></span>` : "";
    el.innerHTML = `
      <div class="kpi">
        <div class="kpi__label">${UI.RiskBadge("high")}<span>клиентов</span></div>
        <div class="kpi__value">${Fmt.int(sm.highCount)}<span class="kpi__unit">из ${Fmt.int(sm.total)}</span></div>
        <div class="kpi__dist" aria-hidden="true">${seg(sm.highCount,"high")}${seg(sm.mediumCount,"medium")}${seg(sm.lowCount,"low")}${seg(sm.noAssessmentCount,"none")}</div>
        <div class="kpi__foot">Средний — <b class="num">${sm.mediumCount}</b> · Низкий — <b class="num">${sm.lowCount}</b> · Без оценки — <b class="num">${sm.noAssessmentCount}</b></div>
        ${sm.highCount ? `<button class="linkbtn" data-action="filter" data-level="high">Показать только высокий риск</button>` : ""}
      </div>
      <div class="kpi kpi--money">
        <div class="kpi__label">Сценарная сумма под риском</div>
        <div class="kpi__value num">${sm.scenarioLossCents === null ? "—" : esc(Fmt.money(sm.scenarioLossCents))}<span class="kpi__unit">${esc(sm.currency)} · горизонт ${sm.horizonDays} ${Fmt.plural(sm.horizonDays,"день","дня","дней")}</span></div>
        <div class="kpi__foot">Ориентир суммы ближайших продлений с учётом балансов и риска. Это эвристический сценарный индекс, а не прогноз и не ожидаемая потеря.</div>
      </div>
      <div class="kpi">
        <div class="kpi__label">${UI.RiskBadge("none")}<span>клиентов</span></div>
        <div class="kpi__value">${Fmt.int(sm.noAssessmentCount)}<span class="kpi__unit">без оценки</span></div>
        <div class="kpi__foot">Недостаточно данных для score — это не «низкий риск».</div>
        ${sm.noAssessmentCount ? `<button class="linkbtn" data-action="filter" data-level="none">Показать клиентов без оценки</button>` : ""}
      </div>`;
  }

  /* ------------------------------------------------ Toolbar */
  function buildToolbar() {
    $("#toolbar").innerHTML = `
      <div class="toolbar__search">${UI.Input({ id: "search", type: "search", placeholder: "Поиск по ID или email", icon: "search", attrs: { "aria-label": "Поиск по ID или email", "aria-keyshortcuts": "/" } })}</div>
      <div class="c-segmented" role="group" aria-label="Уровень риска" id="level-filter"></div>
      <div class="toolbar__mobile-sort">${UI.Select({ id: "mobile-sort", value: "score:desc", options: MOBILE_SORTS.map(([value,label])=>({value,label})), attrs: { "aria-label": "Сортировка" } })}</div>
      <div class="toolbar__spacer"></div>
      <div class="toolbar__count" id="result-count" aria-live="polite"></div>`;
    $("#toolbar-extra").innerHTML = `
      <div class="toolbar__spacer"></div>
      ${UI.Button({ label: "Сохранить сегмент", variant: "ghost", size: "sm", attrs: { "data-action": "seg-save" } })}
      ${UI.Button({ label: "AI-резюме", variant: "ghost", size: "sm", attrs: { "data-action": "ai-brief" } })}
      <div id="ai-brief-out" class="ai-brief-slot" aria-live="polite"></div>`;
  }
  function renderFilter() {
    const c = state.counts;
    $("#level-filter").innerHTML = LEVEL_FILTERS.map((f) => {
      const glyph = f.value !== "all" ? `<span class="c-badge c-badge--${f.value}" style="border:0;background:none;padding:0;height:auto"><span class="c-badge__glyph" aria-hidden="true"></span></span>` : "";
      const n = c ? (f.value === "all" ? c.all : c[f.value === "none" ? "none" : f.value] ?? 0) : null;
      return `<button type="button" class="c-segmented__item" data-level="${f.value}" aria-pressed="${state.level === f.value}">${glyph}${esc(f.label)}${n !== null ? `<span class="c-segmented__count">${n}</span>` : ""}</button>`;
    }).join("");
    $("#mobile-sort").value = `${state.sort}:${state.dir}`;
  }

  /* ------------------------------------------------ Table */
  function headRow() {
    const h = (key, label, numeric) => UI.SortHeader({ key, label, sort: state.sort, dir: state.dir, numeric });
    return `<tr>${h("id","ID")}${h("email","Email")}${h("balanceCents","Баланс",true)}${h("totalReplenishedCents","Пополнено всего",true)}${h("score","Score",true)}<th scope="col">Уровень риска</th><th scope="col">Краткая причина</th></tr>`;
  }
  function rowHtml(r) {
    const selected = r.id === state.selectedId;
    const scoreCell = r.score === null
      ? `<span class="score score--none">—</span>`
      : `<span class="score"><span class="score__bar" aria-hidden="true"><i style="width:${r.score}%;background:${LEVEL_SOLID[r.level]}"></i></span><span class="score__num">${r.score}</span></span>`;
    return `<tr tabindex="0" data-id="${r.id}" aria-selected="${selected}" aria-label="Клиент ${r.id}, ${esc(r.email)}, ${UI.RISK[r.level].long}">
      <td class="col-id num">${r.id}</td>
      <td class="col-email" title="${esc(r.email)}">${esc(r.email)}</td>
      <td class="col-money is-num${r.balanceCents===0?" is-zero":""}" data-label="Баланс">${esc(Fmt.money(r.balanceCents))}</td>
      <td class="col-money is-num${r.totalReplenishedCents===0?" is-zero":""}" data-label="Пополнено">${esc(Fmt.money(r.totalReplenishedCents))}</td>
      <td class="col-score is-num">${scoreCell}</td>
      <td class="col-level">${UI.RiskBadge(r.level)}</td>
      <td class="col-reason">${esc(r.reason)}</td></tr>`;
  }
  function renderTable() {
    const region = $("#table-region"), count = $("#result-count"), err = ch.list.error;
    if (err) {
      count.textContent = "";
      region.innerHTML = UI.ErrorState({ title: "Не удалось загрузить клиентов",
        text: err.message + " Фильтры и сортировка сохранены.",
        code: "code: " + err.code,
        actions: UI.Button({ label: "Повторить", variant: "primary", icon: "refresh", attrs: { "data-action": "reload" } }) });
      return;
    }
    if (ch.list.loading && !state.rows.length) {
      count.textContent = "Загрузка…";
      region.innerHTML = `<div class="c-table-wrap" aria-busy="true"><table class="c-table"><thead>${headRow()}</thead><tbody>${
        Array.from({length:8},(_,i)=>`<tr class="skel-row" aria-hidden="true"><td>${UI.Skeleton("32px")}</td><td>${UI.Skeleton(140+(i*37)%90+"px")}</td><td class="is-num">${UI.Skeleton("64px")}</td><td class="is-num">${UI.Skeleton("72px")}</td><td>${UI.Skeleton("60px")}</td><td>${UI.Skeleton("72px")}</td><td>${UI.Skeleton(160+(i*53)%120+"px")}</td></tr>`).join("")}</tbody></table></div>`;
      return;
    }
    count.textContent = state.counts && state.counts.all ? `${state.rows.length} из ${state.counts.all}` : "";
    if (!state.rows.length) {
      const empty = !state.counts || state.counts.all === 0;
      region.innerHTML = empty
        ? UI.EmptyState({ title: "В снимке нет клиентов", text: "Источник вернул пустой набор. Загрузите снимок через «Источник данных».", actions: UI.Button({ label: "Источник данных", attrs: { "data-action": "open-source" } }) })
        : UI.EmptyState({ icon: "search", title: "Ничего не найдено", text: `Нет клиентов${state.q?` по запросу «${state.q}»`:""}${state.level!=="all"?` с уровнем «${UI.RISK[state.level].long.toLowerCase()}»`:""}.`, actions: UI.Button({ label: "Сбросить фильтры", attrs: { "data-action": "reset-filters" } }) });
      return;
    }
    region.innerHTML = `<div class="c-table-wrap"><table class="c-table" aria-label="Клиенты под риском"><thead>${headRow()}</thead><tbody>${state.rows.map(rowHtml).join("")}</tbody></table></div>`;
  }

  /* ------------------------------------------------ Data channels
     Каждый канал (list, summary, batch) имеет свой токен запроса — устаревший
     ответ игнорируется целиком и не отменяет более новый запрос.            */
  const ch = {
    list: { req: 0, loading: true, error: null },
    sum:  { req: 0, loading: true, error: null },
    poll: { req: 0 }
  };
  const anyLoading = () => ch.list.loading || ch.sum.loading;
  const anyError = () => ch.list.error || ch.sum.error;

  async function fetchList() {
    const c = ch.list, my = ++c.req;
    c.loading = true; renderHead(); renderTable();
    const res = await api.listCustomers({ q: state.q, level: state.level, sort: state.sort, dir: state.dir });
    if (my !== c.req) return;
    c.loading = false;
    if (!res.ok) { if (res.error.code === "session_expired") return showSessionExpired(res.error); c.error = res.error; }
    else { c.error = null; state.rows = res.data; if (res.meta.snapshot) state.snapshot = res.meta.snapshot; if (res.meta.counts) state.counts = res.meta.counts; state.noData = false; }
    renderHead(); renderFilter(); renderTable();
  }

  async function fetchSummary() {
    const c = ch.sum, my = ++c.req;
    c.loading = true; renderHead(); renderKpis();
    const res = await api.getSummary();
    if (my !== c.req) return;
    c.loading = false;
    if (!res.ok) { if (res.error.code === "session_expired") return showSessionExpired(res.error); c.error = res.error; }
    else { state.summary = res.data; if (res.meta && res.meta.snapshot) state.snapshot = res.meta.snapshot; state.noData = !!res.data.noData; }
    renderHead(); renderKpis();
  }

  async function fetchRenewals() {
    const res = await api.getRenewals();
    if (!res.ok) { if (res.error.code === "session_expired") return showSessionExpired(res.error); return; }
    renderAnalytics(res.data);
  }

  /* ---------- analytics: renewals by date + reason counts ---------- */
  function renderAnalytics(d) {
    const chartEl = $("#renewals-chart"), reasonsEl = $("#reasons-list"),
          reEmpty = $("#renewals-empty"), rsEmpty = $("#reasons-empty");
    if (!chartEl) return;
    const buckets = (d && d.buckets) || [], reasons = (d && d.reasons) || [];
    reEmpty.hidden = buckets.length > 0; rsEmpty.hidden = reasons.length > 0;
    chartEl.innerHTML = ""; reasonsEl.innerHTML = "";

    const maxTotal = Math.max(1, ...buckets.map(b => b.high + b.medium + b.low + b.noAssessment));
    const LVL = [["high","var(--color-risk-high-solid)"],["medium","var(--color-risk-medium-solid)"],
                 ["low","var(--color-risk-low-solid)"],["noAssessment","var(--color-risk-none-solid)"]];
    for (const b of buckets) {
      const total = b.high + b.medium + b.low + b.noAssessment;
      const row = document.createElement("button");
      row.type = "button"; row.className = "renewals__row"; row.setAttribute("role","listitem");
      row.dataset.day = b.date;
      row.setAttribute("aria-label",
        `${b.date}: ${total} продлений — высокий ${b.high}, средний ${b.medium}, низкий ${b.low}, без оценки ${b.noAssessment}${b.overdue ? `, просрочено ${b.overdue}` : ""}`);
      const segs = LVL.map(([k,c]) => {
        const v = b[k]; if (!v) return "";
        const w = Math.max(4, Math.round((v / maxTotal) * 100));
        return `<span class="renewals__seg" style="width:${w}%;background:${c}" title="${k}: ${v}"></span>`;
      }).join("");
      row.innerHTML = `
        <span class="renewals__day">${esc(b.date)}</span>
        <span class="renewals__bars">${segs}</span>
        <span class="renewals__n num">${total}${b.overdue ? `<em class="renewals__overdue" title="просрочено">+${b.overdue} проср.</em>` : ""}</span>`;
      row.addEventListener("click", () => {
        $("#table-region")?.scrollIntoView({ behavior: "smooth", block: "start" });
      });
      chartEl.appendChild(row);
    }
    for (const r of reasons) {
      const li = document.createElement("button");
      li.type = "button"; li.className = "reasons__row"; li.setAttribute("role","listitem");
      li.innerHTML = `<span class="reasons__code">${esc(r.code)}</span><span class="reasons__n num">${r.customers}</span>`;
      li.addEventListener("click", () => { $("#table-region")?.scrollIntoView({ behavior:"smooth", block:"start" }); });
      reasonsEl.appendChild(li);
    }
  }

  function load() { hideFullState(); return Promise.all([fetchList(), fetchSummary(), fetchRenewals()]); }

  /* ---- polling: same identity tuple, never merge stale & new batches ---- */
  function batchId(snap) { return snap && snap.id; }
  async function pollTick() {
    if (document.hidden) return;                    // не опрашивать фоновую вкладку
    const my = ++ch.poll.req;
    state.refreshing = true; renderHead();
    const res = await api.getSummary();
    if (my !== ch.poll.req) { state.refreshing = false; return; }
    state.refreshing = false;
    if (!res.ok) { if (res.error.code === "session_expired") return showSessionExpired(res.error); renderHead(); return; }
    const newSnap = res.meta && res.meta.snapshot;
    const changed = newSnap && batchId(newSnap) !== batchId(state.snapshot);
    if (!changed) { renderHead(); return; }         // тот же батч — не перерисовывать
    // новый evaluationId: обновляем и сводку, и список, не смешивая версии
    state.snapshot = newSnap; state.summary = res.data; state.noData = !!res.data.noData;
    renderHead(); renderKpis();
    // список перечитываем по тому же новому батчу (повторная попытка, ограничено)
    for (let tries = 0; tries < MAX_REFRESH_TRIES; tries++) {
      const lr = await api.listCustomers({ q: state.q, level: state.level, sort: state.sort, dir: state.dir });
      if (my !== ch.poll.req) return;
      if (lr.ok && lr.meta.snapshot && lr.meta.snapshot.id === newSnap.id) {
        state.rows = lr.data; if (lr.meta.counts) state.counts = lr.meta.counts; ch.list.error = null;
        renderFilter(); renderTable(); break;
      } else if (!lr.ok && lr.error.code === "session_expired") { return showSessionExpired(lr.error); }
    }
  }
  function startPolling() {
    if (!POLL_SECONDS) return;
    stopPolling();
    state.pollTimer = setInterval(pollTick, POLL_SECONDS * 1000);
  }
  function stopPolling() { if (state.pollTimer) { clearInterval(state.pollTimer); state.pollTimer = null; } }

  function showSessionExpired(err) {
    stopPolling();
    ch.list.req++; ch.sum.req++;
    ch.list.loading = ch.sum.loading = false;
    $("#notices").innerHTML = "";
    customerDrawer.close({ restoreFocus: false });
    $("#page-content").hidden = true;
    const fs = $("#full-state"); fs.hidden = false;
    fs.innerHTML = UI.ErrorState({ icon: "lock", title: "Сессия истекла",
      text: err.message + " Данные клиентов скрыты до повторного входа.",
      code: "code: session_expired",
      actions: `<a class="c-btn c-btn--primary" href="/login">Войти снова</a>` });
  }
  function hideFullState() { $("#page-content").hidden = false; $("#full-state").hidden = true; }

  /* ------------------------------------------------ Customer drawer */
  const customerDrawer = new UI.Drawer({ id: "customer-drawer", labelledBy: "cust-title", modal: false, onClose: () => { state.selectedId = null; markSelected(); } });
  function markSelected() {
    document.querySelectorAll("#table-region tbody tr[data-id]").forEach((tr) => tr.setAttribute("aria-selected", String(+tr.dataset.id === state.selectedId)));
  }
  function neighbours() {
    const i = state.rows.findIndex((r) => r.id === state.selectedId);
    return { prev: i > 0 ? state.rows[i-1] : null, next: i>=0 && i<state.rows.length-1 ? state.rows[i+1] : null, i };
  }
  function drawerHead(title, eyebrow, extra = "") {
    const n = neighbours();
    return `<div class="c-drawer__head">
      <div class="cust__head-main">${eyebrow}<h2 class="cust__title" id="cust-title">${title}</h2>${extra}</div>
      <div class="cust__nav">
        ${UI.Button({ label:"Предыдущий клиент (K)", iconOnly:true, icon:"chevron", size:"sm", variant:"ghost", disabled:!n.prev, attrs:{ "data-action":"cust-prev", style:"transform:scaleX(-1)" } })}
        ${UI.Button({ label:"Следующий клиент (J)", iconOnly:true, icon:"chevron", size:"sm", variant:"ghost", disabled:!n.next, attrs:{ "data-action":"cust-next" } })}
        ${UI.Button({ label:"Закрыть (Esc)", iconOnly:true, icon:"close", size:"sm", variant:"ghost", tipPos:"end", attrs:{ "data-action":"cust-close", "data-autofocus":"" } })}
      </div></div>`;
  }

  async function openCustomer(id, triggerEl) {
    state.selectedId = id; markSelected();
    const row = state.rows.find((r) => r.id === id);
    customerDrawer.setContent(drawerHead(esc(row ? row.email : "Клиент " + id), `<div class="cust__eyebrow">Клиент · ID ${id}</div>`) +
      `<div class="c-drawer__body" aria-busy="true"><div style="display:grid;gap:12px">${UI.Skeleton("60%")}${UI.Skeleton("90%")}${UI.Skeleton("80%")}<span></span>${UI.Skeleton("40%")}${UI.Skeleton("100%")}</div></div>`);
    customerDrawer.open(triggerEl);
    const res = await api.getCustomer(id);
    if (state.selectedId !== id) return;
    if (!res.ok) {
      if (res.error.code === "session_expired") return showSessionExpired(res.error);
      customerDrawer.setContent(drawerHead("Клиент " + id, `<div class="cust__eyebrow">Клиент · ID ${id}</div>`) + `<div class="c-drawer__body">${UI.ErrorState({ title: "Карточка не загрузилась", text: res.error.message, code: "code: " + res.error.code, actions: UI.Button({ label: "Повторить", attrs: { "data-action": "cust-retry" } }) })}</div>`);
      return;
    }
    customerDrawer.setContent(customerHtml(res.data, res.meta.snapshot));
    renderNextActions(res.data, res.meta.snapshot);
    loadCustomerTasks(id);
    const fb = customerDrawer.el.querySelector("[data-autofocus]"); fb && fb.focus({ preventScroll: true });
  }

  /* «Что проверить дальше» — deterministic suggestions from the CURRENT batch
     only (reasons/services/money of one evaluationId). Never a probability or
     auto-action; a task is still created by a human via the existing form. */
  function renderNextActions(data, snap){
    const el = document.querySelector(`#next-actions-${data.account.id} .next-actions`);
    if(!el) return;
    const card = data;
    // takenAt comes from meta.snapshot, not the card — the adapter puts it there.
    const snapTaken = snap?.takenAt || null;
    const horizon = card.assessment?.horizonDays ?? null;
    const ctx = { asOf: snapTaken,
      horizonEnd: snapTaken && horizon !== null
        ? new Date(new Date(snapTaken).getTime() + horizon * 86400000).toISOString() : null };
    const list = (window.NextActions?.build(card, ctx)) || [];
    // cap at 3 visible items; the rest stay reachable under <details> — no fact
    // is dropped, only collapsed. Each item keeps its own service link.
    const VISIBLE = 3;
    const itemHtml = (n)=>`
      <li class="na-item" ${n.serviceId?`data-service="${n.serviceId}" role="button" tabindex="0"`:""}>
        <div class="na-item__title">${esc(n.title)}</div>
        <div class="na-item__evidence"><code>${esc(n.code)}</code>${n.serviceId?` · услуга ${n.serviceId}`:""} · ${esc(n.evidence)}</div>
      </li>`;
    const shown = list.slice(0, VISIBLE), rest = list.slice(VISIBLE);
    el.innerHTML = list.length
      ? `<ul class="na-list" role="list">${shown.map(itemHtml).join("")}</ul>
        ${rest.length?`<details class="na-more"><summary>Ещё ${rest.length} ${Fmt.plural(rest.length,"рекомендация","рекомендации","рекомендаций")}</summary><ul class="na-list" role="list">${rest.map(itemHtml).join("")}</ul></details>`:""}
        <p class="sec__note">Рекомендации детерминированы по текущему батчу и не гарантируют результат.</p>`
      : `<p class="sec__note" style="margin:0">Автоматической рекомендации нет.</p>`;
    // service link is real: scrolls + focuses the service row in this card.
    el.querySelectorAll(".na-item[data-service]").forEach(li => {
      const go = () => { const row = document.getElementById(`svc-${data.account.id}-${li.dataset.service}`);
        if(row){ row.scrollIntoView({behavior:"smooth",block:"center"}); row.focus({preventScroll:true}); } };
      li.addEventListener("click", go);
      li.addEventListener("keydown", e => { if(e.key==="Enter"||e.key===" "){e.preventDefault();go();} });
    });
  }

  function customerHtml({ account: a, assessment: as, services }, snap) {
    const horizon = as.horizonDays;
    const isNone = as.level === "none";
    const spendable = a.balance + (a.bonusState ? a.bonusBalance : 0);
    const money = as.money || {};
    const lines = money.lines || [];
    const svcRows = services.map((s) => {
      const st = Fmt.SERVICE_STATUS[s.status];
      const d = Fmt.daysBetween(snap.takenAt, s.expiresAt);
      const muted = s.status === "deleted" || s.status === "blocked";
      const overdue = d !== null && d < 0;
      let due = s.expiresAt ? `${Fmt.date(s.expiresAt)} · ${Fmt.relDays(d)}` : "дата окончания не указана";
      let alert = false;
      // prolong_wait: source rule keeps the service ~5 days past expiry.
      // left<=0 means the rule window already ended — say so, not "0 days".
      if (s.status === "prolong_wait" && d !== null) { const left = 5 + d;
        due = left > 0 ? `правило источника: удаление через ${left} ${Fmt.plural(left,"день","дня","дней")}` : `правило источника: удалено`; alert = true; }
      else if (!muted && d !== null && d >= 0 && d <= 7 && !s.autoprolong) alert = true;
      return `<div class="svc__row${muted?" svc__row--muted":""}" id="svc-${a.id}-${s.id}" tabindex="-1">
        <div class="svc__name">${esc(s.name)} ${UI.Badge({ label: st.label, tone: st.tone, dot: true })}</div>
        <div class="svc__price">${esc(Fmt.money(s.price))} <small style="color:var(--color-text-tertiary);font-weight:400">${esc(Fmt.TERM[s.paymentTerm])}</small></div>
        <div class="svc__meta"><span class="${!s.autoprolong&&!muted?"is-alert":""}">Автопродление: ${s.autoprolong?"вкл.":"выкл."}</span><span>ID ${s.id}</span></div>
        <div class="svc__due${alert?" is-alert":""}">${esc(due)}${overdue?" · просрочено":""}</div>
      </div>`;
    }).join("");

    const maxPts = Math.max(...as.factors.map((f)=>f.points),1);
    const factorsHtml = as.factors.length ? `<div class="factors">${as.factors.map((f)=>`
        <div class="factor"><div><div class="factor__label">${esc(f.label)}</div><div class="factor__weight">вклад +${f.points} · <span class="mono">${esc(f.key)}</span></div></div>
          <div class="factor__pts">+${f.points}</div>
          <div class="factor__bar" style="grid-column:1"><i style="width:${(f.points/maxPts)*100}%;--fbar:${LEVEL_SOLID[as.level]}"></i></div></div>`).join("")}
        <div class="factors__total"><span>Итоговый score</span><b>${as.score} / 100</b></div></div>
        <p class="sec__note">Score — индекс приоритета, не вероятность. Вклады — сумма сработавших факторов.</p>`
      : `<p class="sec__note" style="margin:0">Факторы риска не обнаружены.</p>`;

    const warnings = as.warnings.length ? `<section class="sec" aria-labelledby="s-warn">
        <h3 class="sec__title" id="s-warn">${isNone?"Почему нет оценки":"Ограничения данных"}</h3>
        ${UI.Banner({ tone: isNone?"info":"warning", mark: isNone?"i":"!", title: isNone?"Недостаточно данных для score":"Оценка может быть неточной", text:`<ul style="margin:4px 0 0;padding-left:18px">${as.warnings.map((w)=>`<li>${esc(w)}</li>`).join("")}</ul>` })}
      </section>` : "";

    const loyal = as.loyaltyIndicator !== null && as.loyaltyIndicator !== undefined
      ? `<div><dt>Индикатор лояльности</dt><dd>${as.loyaltyIndicator} <small>производный (100 − score), не измеренное поведение</small></dd></div>` : "";
    const moneyLines = lines.length ? lines.map((l)=>`
      <div class="money__row"><span>${esc(l.status)} · ${esc(l.term)} · ID ${l.serviceId}</span><span>${esc(Fmt.money(l.priceCents))}${l.overdue?" · просрочено":""}</span></div>`).join("")
      : `<div class="money__row"><span>Продлений в горизонте нет</span><span>—</span></div>`;
    const scoreExtra = `<div class="cust__head-row">${UI.RiskBadge(as.level,{long:true})}${isNone?`<span class="cust__score">Score не рассчитан</span>`:`<span class="cust__score">Score <b>${as.score}</b> / 100</span>`}</div>`;

    return drawerHead(esc(a.email), `<div class="cust__eyebrow">Клиент · ID ${a.id}</div>`, scoreExtra) + `
    <div class="c-drawer__body">
      ${isNone ? warnings : ""}
      ${!isNone ? `<section class="sec"><h3 class="sec__title sec__title--ic ic-host is-act"><span class="sec__ic">${UI.Icon.explain}</span>Причины риска</h3><ol class="reasons">${as.reasons.map((r)=>`<li>${esc(r)}</li>`).join("")}</ol></section>` : ""}
      <section class="sec"><h3 class="sec__title">Основные сведения</h3>
        <dl class="dl">
          <div><dt>Основной баланс</dt><dd>${esc(Fmt.money(a.balance))}</dd></div>
          <div><dt>Пополнено всего</dt><dd>${esc(Fmt.money(a.totalReplenished))}</dd></div>
          <div><dt>Бонусный баланс</dt><dd>${esc(Fmt.money(a.bonusBalance))} ${a.bonusBalance?`<small>${a.bonusState?"можно тратить":"тратить нельзя"}</small>`:""}</dd></div>
          <div><dt>Доступно к выводу</dt><dd>${esc(Fmt.money(a.withdrawBalance))}</dd></div>
          ${loyal}
        </dl></section>
      <section class="sec"><h3 class="sec__title">Услуги и ближайшие продления <small>${services.length} ${Fmt.plural(services.length,"услуга","услуги","услуг")}</small></h3>
        ${services.length?`<div class="svc">${svcRows}</div>`:`<p class="sec__note" style="margin:0">У клиента нет услуг.</p>`}</section>
      ${!isNone?`<section class="sec"><h3 class="sec__title">Вклад факторов в score</h3>${factorsHtml}</section>`:""}
      <section class="sec"><h3 class="sec__title">Денежная оценка <small>горизонт ${horizon} ${Fmt.plural(horizon,"день","дня","дней")}, EUR</small></h3>
        <div class="money">
          ${moneyLines}
          <div class="money__row"><span>Доступный фонд${a.bonusState&&a.bonusBalance?" (вкл. бонусы)":""}</span><span>${money.availableFund!==undefined?esc(Fmt.money(money.availableFund)):"—"}</span></div>
          <div class="money__row"><span>Непокрытая сумма</span><span>${money.uncoveredAmount!==undefined?esc(Fmt.money(money.uncoveredAmount)):"—"}</span></div>
          <div class="money__row money__row--total"><span>Сценарная сумма под риском</span><span>${isNone||money.atRiskAmount===null||money.atRiskAmount===undefined?"не рассчитана":esc(Fmt.money(money.atRiskAmount))}</span></div>
        </div>
        <p class="sec__note">Одно ближайшее продление на услугу; просроченные события помечены. Это эвристический сценарный индекс (${esc(money.semantic||"heuristic_money_index")}), а не прогноз и не ожидаемая потеря.</p>
      </section>
      ${!isNone?warnings:""}
      <section class="sec" id="next-actions-${a.id}" aria-labelledby="na-title-${a.id}">
        <h3 class="sec__title" id="na-title-${a.id}">Что проверить дальше</h3>
        <div class="next-actions"></div>
      </section>
    </div>
    <div class="c-drawer__foot">
      ${UI.Button({ label:"Создать задачу на удержание", variant:"primary", attrs:{ "data-action":"task-new", "data-id": a.id } })}
      ${UI.Button({ label:"Написать клиенту", attrs:{ "data-action":"msg-new", "data-id": a.id, "data-email": a.email } })}
    </div>
    <div class="c-drawer__sec" id="cust-tasks-${a.id}" data-account="${a.id}"></div>`;
  }

  /* ------------------------------------------------ Events */
  let searchT;
  function bind() {
    $("#search").addEventListener("input", (e)=>{ clearTimeout(searchT); searchT=setTimeout(()=>{ state.q=e.target.value; fetchList(); },160); });
    $("#mobile-sort").addEventListener("change",(e)=>{ const [s,d]=e.target.value.split(":"); state.sort=s; state.dir=d; fetchList(); });
    document.addEventListener("click",(e)=>{
      const t=e.target.closest("[data-action],[data-sort],[data-level],tr[data-id],[data-nav]"); if(!t) return;
      if(t.getAttribute("aria-disabled")==="true"){e.preventDefault();return;}
      if(t.matches(".c-segmented__item")){state.level=t.dataset.level;fetchList();return;}
      if(t.dataset.sort){const k=t.dataset.sort;if(state.sort===k)state.dir=state.dir==="asc"?"desc":"asc";else{state.sort=k;state.dir=k==="email"||k==="id"?"asc":"desc";}fetchList();return;}
      if(t.matches("tr[data-id]")){openCustomer(+t.dataset.id,t);return;}
      const a=t.dataset.action;
      if(a==="reload")load();
      else if(a==="filter"){state.level=t.dataset.level;fetchList();}
      else if(a==="reset-filters"){state.q="";state.level="all";$("#search").value="";fetchList();}
      else if(a==="cust-close")customerDrawer.close();
      else if(a==="cust-retry")openCustomer(state.selectedId);
      else if(a==="cust-prev"||a==="cust-next")step(a==="cust-next"?1:-1);
      else if(a==="open-settings"){closeNav();window.SettingsModal.open();}
      else if(a==="open-source"){closeNav();window.SourceModal.open();}
      else if(a==="nav-toggle")toggleNav();
      else if(a==="nav-close")closeNav({returnFocus:true});
      else if(a==="nav-backdrop")closeNav({returnFocus:true});
      else if(a==="task-new"){ openTaskModal(+t.dataset.id); }
      else if(a==="msg-new"){ openMsgModal(+t.dataset.id, t.dataset.email); }
      else if(a==="task-save"){ saveTask(t); }
      else if(a==="task-status"){ setTaskStatus(+t.dataset.id, t.dataset.status); }
      else if(a==="msg-draft"){ saveDraft(t); }
      else if(a==="msg-copy"){ copyDraft(t); }
      else if(a==="msg-mailto"){ openMailto(t); }
      else if(a==="msg-send"){ sendDraft(t); }
      else if(a==="tasks-open"){ openTasksView(); }
      else if(a==="tasks-close"){ closeTasksView(); }
      else if(a==="ai-brief"){ runAiBrief(); }
      else if(a==="seg-save"){ saveSegment(); }
      else if(a==="csv-export"){ exportCsv(); }
      else if(a==="cmp-run"){ runCompare(); }
      else if(a==="whatif-run"){ runWhatIf(); }
    });
    $("#table-region").addEventListener("keydown",(e)=>{
      const tr=e.target.closest("tr[data-id]"); if(!tr)return;
      if(e.key==="Enter"||e.key===" "){e.preventDefault();openCustomer(+tr.dataset.id,tr);}
      else if(e.key==="ArrowDown"||e.key==="ArrowUp"){e.preventDefault();const sib=e.key==="ArrowDown"?tr.nextElementSibling:tr.previousElementSibling;sib&&sib.focus();}
    });
    document.addEventListener("keydown",(e)=>{
      const typing=/INPUT|SELECT|TEXTAREA/.test(document.activeElement.tagName);
      if(e.key==="/"&&!typing&&!(window.SettingsModal&&window.SettingsModal.isOpen())&&!(window.SourceModal&&window.SourceModal.isOpen())){e.preventDefault();$("#search").focus();}
      if(customerDrawer.isOpen&&!typing){if(e.key==="j"||e.key==="о")step(1);if(e.key==="k"||e.key==="л")step(-1);if(e.key==="Escape")customerDrawer.close();}
    });
  }
  function step(d){const n=neighbours();const r=d>0?n.next:n.prev;if(!r)return;const tr=document.querySelector(`tr[data-id="${r.id}"]`);openCustomer(r.id,tr);if(tr){const top=tr.getBoundingClientRect().top;if(top<80||top>innerHeight-60)window.scrollBy({top:top-innerHeight/2});}}

  /* ======================= mobile nav (modal drawer) =======================
     data-open on .nav + body.nav-open scroll-lock + backdrop element.
     Consistent aria-expanded on the toggle; focus moves into the panel and
     returns to the toggle on close. One handler set — no double-binding. */
  const navEl = () => $(".nav");
  const backdropEl = () => $(".nav-backdrop");
  const navToggleBtn = () => $("[data-action='nav-toggle']");
  let navBound = false, prevActive = null, navScrollY = 0;
  let navLockStyles = null;

  function isMobileNav() { return matchMedia("(max-width: 760px)").matches; }

  function setNav(open, { returnFocus = false } = {}) {
    const nav = navEl(), bd = backdropEl(), tg = navToggleBtn();
    if (!nav) return;
    const wantOpen = !!open && isMobileNav();
    nav.dataset.open = String(wantOpen);
    document.body.classList.toggle("nav-open", wantOpen);
    if (wantOpen) {
      navScrollY = window.scrollY;
      navLockStyles = {
        position: document.body.style.position,
        top: document.body.style.top,
        left: document.body.style.left,
        right: document.body.style.right,
        width: document.body.style.width
      };
      document.body.style.position = "fixed";
      document.body.style.top = `-${navScrollY}px`;
      document.body.style.left = "0";
      document.body.style.right = "0";
      document.body.style.width = "100%";
    } else {
      if (navLockStyles) {
        document.body.style.position = navLockStyles.position;
        document.body.style.top = navLockStyles.top;
        document.body.style.left = navLockStyles.left;
        document.body.style.right = navLockStyles.right;
        document.body.style.width = navLockStyles.width;
        navLockStyles = null;
        window.scrollTo(0, navScrollY);
      }
      if (bd) bd.hidden = true;
    }
    if (bd) bd.hidden = !wantOpen;
    tg && tg.setAttribute("aria-expanded", String(wantOpen));
    nav.setAttribute("aria-hidden", String(!wantOpen));
    if (wantOpen) {
      prevActive = document.activeElement;
      // move focus into the panel (first actionable control)
      const first = nav.querySelector("[data-action='nav-close'], .nav__item, button, a");
      (first || nav).focus({ preventScroll: true });
    } else if (returnFocus && prevActive) {
      const t = navToggleBtn();
      (document.contains(prevActive) ? prevActive : t)?.focus({ preventScroll: true });
      prevActive = null;
    }
  }
  function openNav() { setNav(true); }
  function closeNav(opts) { setNav(false, opts); }
  function toggleNav(force) {
    const nav = navEl();
    const open = force !== undefined ? force : nav.dataset.open !== "true";
    setNav(open, { returnFocus: !open });
  }

  /* backdrop click + Esc + resize guard — bound once */
  function bindNav() {
    if (navBound) return; navBound = true;
    backdropEl()?.addEventListener("click", () => closeNav({ returnFocus: true }));
    document.addEventListener("keydown", (e) => {
      if (e.key === "Escape" && navEl()?.dataset.open === "true" && !window.SettingsModal?.isOpen() && !window.SourceModal?.isOpen()) {
        e.stopPropagation(); closeNav({ returnFocus: true });
      }
    });
    // crossing the mobile/desktop boundary while open must not leave a lock
    matchMedia("(max-width: 760px)").addEventListener("change", () => {
      if (!isMobileNav()) closeNav();
      else if (navEl()?.dataset.open === "true") setNav(true);
    });
    // background content inert for keyboard while menu is open
    document.addEventListener("focusin", (e) => {
      if (navEl()?.dataset.open === "true" && !navEl().contains(e.target)) {
        (navEl().querySelector("[data-action='nav-close']") || navEl()).focus({ preventScroll: true });
      }
    }, true);
  }

  async function init() {
    buildToolbar(); renderFilter(); bind(); bindNav(); fillBatches();
    try {
      const cfg = await (await fetch("/api/v1/ui-config", { credentials: "same-origin" })).json();
      if (cfg && Number.isFinite(cfg.pollSeconds)) POLL_SECONDS = Math.max(0, cfg.pollSeconds);
    } catch (_) { /* default poll */ }
    const me = await api.getSession();
    if (!me.ok && me.error && me.error.code === "session_expired") return showSessionExpired(me.error);
    await load(); startPolling();
    document.addEventListener("visibilitychange", ()=>{ if(!document.hidden) pollTick(); });
  }
  /* ================= tasks + messages + segments + ai ================= */
  let usersCache = null;
  async function activeUsers() {
    if (!usersCache) { const r = await api.activeUsers(); usersCache = r.ok ? (r.data||[]) : []; }
    return usersCache;
  }
  const taskModal = new UI.Modal({ id: "task-modal", title: "Задача на удержание" });
  const msgModal  = new UI.Modal({ id: "msg-modal",  title: "Написать клиенту" });
  let curTaskAccount = 0, curMsgAccount = 0, curMsgEmail = "";

  async function openTaskModal(accountId) {
    curTaskAccount = accountId;
    const users = await activeUsers();
    const opts = users.filter(u=>!u.disabled).map(u=>`<option value="${u.id}">${esc(u.login)} (${esc(u.role)})</option>`).join("");
    taskModal.setContent(`
      <form id="task-form" class="modal-form">
        <label>Заголовок<input id="t-title" required maxlength="200" placeholder="Связаться по продлению"></label>
        <label>Клиент <input value="ID ${accountId}" disabled></label>
        <label>Ответственный<select id="t-assignee">${opts}</select></label>
        <div class="form-row">
          <label>Срок<input id="t-due" type="date"></label>
          <label>Приоритет<select id="t-pri"><option value="high">Высокий</option><option value="normal" selected>Обычный</option><option value="low">Низкий</option></select></label>
        </div>
        <label>Описание<textarea id="t-desc" rows="3" placeholder="Контекст решения"></textarea></label>
        <div class="modal-err" id="task-err" role="alert"></div>
        <div class="modal-foot">${UI.Button({label:"Создать",variant:"primary",attrs:{"data-action":"task-save","type":"button"}})}</div>
      </form>`);
    taskModal.open();
  }

  async function saveTask() {
    const title=$("#t-title")?.value.trim(), err=$("#task-err");
    if(!title){err.textContent="Укажите заголовок.";return;}
    err.textContent="";
    const r=await api.createTask({accountId:curTaskAccount,title,
      assigneeUserId:+$("#t-assignee").value,dueAt:$("#t-due").value||null,
      priority:$("#t-pri").value,description:$("#t-desc").value||null});
    if(r.ok){taskModal.close();UI.Toast.show({title:"Задача создана"});loadCustomerTasks(curTaskAccount);renderTasksNav?.();}
    else err.textContent=r.error?.message||"Не удалось сохранить.";
  }

  async function loadCustomerTasks(accountId){
    const el=document.getElementById("cust-tasks-"+accountId); if(!el)return;
    const r=await api.customerTasks(accountId);
    const tasks=r.ok?(r.data||[]):[];
    el.innerHTML=`<section class="sec"><h3 class="sec__title">Задачи ${tasks.length?`<small>${tasks.length}</small>`:""}</h3>`+
      (tasks.length?tasks.map(t=>`<div class="taskrow"><span class="taskrow__title">${esc(t.title)}</span>
        <span class="taskrow__meta">${esc(t.status)} · ${esc(t.assigneeLogin)}${t.dueAt?" · "+Fmt.date(t.dueAt):""}</span></div>`).join("")
      :`<p class="sec__note" style="margin:0">Задач нет.</p>`)+`</section>`;
  }
  window.loadCustomerTasks=loadCustomerTasks;

  /* -------- message draft -------- */
  const TEMPLATES={
    expiring_no_autoprolong:(a)=>`Здравствуйте! Услуга у вас истекает без автопродления — свяжемся, чтобы продлить.`,
    prolong_wait:(a)=>`Здравствуйте! Заметили услугу, ожидающую оплаты продления — уточним, нужно ли продлить.`,
    suspended:(a)=>`Здравствуйте! Часть услуг приостановлена — подскажем, как возобновить.`,
    insufficient_funds:(a)=>`Здравствуйте! На балансе может не хватить на ближайшие продления — поможем пополнить.`,
    no_assessment:(a)=>`Здравствуйте! Уточним детали вашей подписки.`
  };
  async function openMsgModal(accountId,email){
    curMsgAccount=accountId;curMsgEmail=email;
    const tpl=TEMPLATES[ (await api.getCustomer(accountId))?.data?.assessment?.reasons?.[0] ]||TEMPLATES.no_assessment;
    msgModal.setContent(`
      <form id="msg-form" class="modal-form">
        <label>Получатель<input id="m-to" value="${esc(email)}" disabled></label>
        <label>Тема<input id="m-sub" required maxlength="200" value="Услуга и продление"></label>
        <label>Текст<textarea id="m-body" rows="6" required>${esc(tpl(accountId))}</textarea></label>
        <div class="modal-err" id="msg-err" role="alert"></div>
        <div class="modal-foot" style="display:flex;gap:8px;flex-wrap:wrap">
          ${UI.Button({label:"Сохранить черновик",attrs:{"data-action":"msg-draft","type":"button"}})}
          ${UI.Button({label:"Копировать",variant:"ghost",attrs:{"data-action":"msg-copy","type":"button"}})}
          ${UI.Button({label:"Открыть в почте",variant:"ghost",attrs:{"data-action":"msg-mailto","type":"button"}})}
          ${UI.Button({label:"Отправить через сервер",variant:"primary",attrs:{"data-action":"msg-send","type":"button"}})}
        </div>
      </form>`);
    msgModal.open();
  }
  async function saveDraft(){
    const body=$("#m-body")?.value,sub=$("#m-sub")?.value,err=$("#msg-err");
    if(!sub||!body){err.textContent="Тема и текст обязательны.";return null;}
    const r=await api.createMessage({accountId:curMsgAccount,recipient:curMsgEmail,subject:sub,body});
    if(!r.ok){err.textContent=r.error?.message||"Не удалось сохранить.";return null;}
    return r.data;
  }
  async function copyDraft(){
    const body=$("#m-body")?.value||"";
    try{await navigator.clipboard.writeText(`To: ${curMsgEmail}\nSubject: ${$("#m-sub").value}\n\n${body}`);UI.Toast.show({title:"Скопировано"});}catch{UI.Toast.show({title:"Не удалось скопировать",tone:"error"});}
  }
  function openMailto(){
    const sub=$("#m-sub").value,body=$("#m-body").value;
    saveDraft().then(d=>{ if(d)api.listMessages&&fetch(`/api/v1/messages/${d.id}/prepared`,{method:"POST",headers:{"X-CSRF-TOKEN":csrfToken()}}); });
    location.href=`mailto:${encodeURIComponent(curMsgEmail)}?subject=${encodeURIComponent(sub)}&body=${encodeURIComponent(body)}`;
  }
  async function sendDraft(){
    const d=await saveDraft(); if(!d)return;
    const r=await api.sendMessage(d.id);
    const err=$("#msg-err");
    if(r.ok){UI.Toast.show({title:"Отправлено"});msgModal.close();}
    else err.textContent="Отправка недоступна: "+(r.error?.message||r.error?.code||"ошибка транспорта");
  }
  function csrfToken(){return document.cookie.split("; ").find(c=>c.startsWith("churn.csrf="))?.split("=")[1]||"";}

  /* -------- segments -------- */
  async function saveSegment(){
    const name=prompt("Имя сегмента:");if(!name)return;
    const q=new URLSearchParams({q:state.q,level:state.level,sort:state.sort,dir:state.dir}).toString();
    const r=await api.createSegment({name,query:q});
    if(r.ok)UI.Toast.show({title:"Сегмент сохранён"});
  }
  window.saveSegment=saveSegment;

  /* -------- ai brief -------- */
  async function runAiBrief(){
    const el=$("#ai-brief-out");if(el)el.innerHTML=UI.Spinner("Запрос у модели…");
    const r=await api.aiBrief();
    if(r.ok){
      const d=r.data;
      el.innerHTML=`<div class="ai-brief"><div class="ai-brief__meta">AI-резюме · ${esc(d.model||"model")} · evaluationId ${esc(String(d.evaluationId).slice(0,8))} · asOf ${esc(Fmt.dateTime(d.asOf))} · требует проверки</div><div class="ai-brief__text">${esc(d.brief||"")}</div>${d.cached?`<div class="sec__note">из кэша батча</div>`:""}</div>`;
    } else {
      el.innerHTML=`<div class="analytics__empty">AI-резюме недоступно: ${esc(r.error?.message||r.error?.code||"")}</div>`;
    }
  }
  window.runAiBrief=runAiBrief;

  /* -------- compare batches + what-if -------- */
  async function fillBatches(){
    const selA=$("#cmp-a"),selB=$("#cmp-b");if(!selA)return;
    const r=await api.listBatches();
    const runs=r.ok?(r.data||[]):[];
    const o=runs.map(x=>`<option value="${x.evaluationId}">${String(x.evaluationId).slice(0,8)} · ${Fmt.dateTime(x.asOf)} · snap${x.snapshotVersion} · m${x.modelVersion}</option>`).join("");
    selA.innerHTML=o;selB.innerHTML=o;if(runs[1])selB.value=runs[1].evaluationId;
  }
  async function runCompare(){
    const a=$("#cmp-a")?.value,b=$("#cmp-b")?.value,out=$("#cmp-out");
    if(!a||!b){out.innerHTML=`<div class="analytics__empty">Выберите два батча.</div>`;return;}
    out.innerHTML=UI.Spinner("Сравнение…");
    const r=await api.compare(a,b);
    if(!r.ok){out.innerHTML=`<div class="analytics__empty">${esc(r.error?.message||r.error?.code||"")}</div>`;return;}
    const d=r.data;
    const lv=(k)=>`<span class="cmp__lv">${k}: ${d.countsA[k]||0} → ${d.countsB[k]||0}</span>`;
    const rows=(d.changed||[]).slice(0,12).map(c=>{
      const delta = c.scoreDelta===null||c.scoreDelta===undefined ? "оценка: переход" : `Δ ${c.scoreDelta>0?"+":""}${c.scoreDelta}`;
      return `<div class="taskrow"><span class="taskrow__title">ID ${c.accountId}</span>
      <span class="taskrow__meta">${c.from?.riskLevel??"—"} ${c.from?.riskScore??"—"} → ${c.to?.riskLevel??"—"} ${c.to?.riskScore??"—"} · ${esc(c.transition||delta)}${c.reasonChanged?" · причины":""}</span></div>`;}).join("");
    out.innerHTML=`<div class="cmp__meta">A ${String(d.a.evaluationId).slice(0,8)} vs B ${String(d.b.evaluationId).slice(0,8)}${d.sameBatch?" · один батч":""}</div>
      <div class="cmp__levels">${lv("high")}${lv("medium")}${lv("low")}${lv("no_assessment")}</div>
      ${d.moneyA||d.moneyB?`<div class="cmp__meta">at-risk: ${d.moneyA?.atRiskTotal??"—"} → ${d.moneyB?.atRiskTotal??"—"} (EUR cents)</div>`:""}
      ${(d.appeared||[]).length?`<div class="cmp__meta">появилось: ${d.appeared.length}</div>`:""}
      ${(d.removed||[]).length?`<div class="cmp__meta">исчезло: ${d.removed.length}</div>`:""}
      ${rows||`<div class="analytics__empty">Различий нет.</div>`}`;
  }
  async function runWhatIf(){
    const out=$("#whatif-out");
    out.innerHTML=UI.Spinner("Считаю сценарий…");
    const settings={modelVersion:0,horizonDays:+$("#w-horizon").value,
      weights:{expiring_no_autoprolong:+$("#w-exp").value,insufficient_funds:+$("#w-ins").value,prolong_wait:+$("#w-pw").value,suspended:+$("#w-susp").value},
      thresholds:{high:+$("#w-th").value,medium:+$("#w-tm").value}};
    const r=await api.whatif(settings);
    if(!r.ok){out.innerHTML=`<div class="analytics__empty">${esc(r.error?.message||r.error?.code||"")}</div>`;return;}
    const d=r.data;
    const rows=(d.changed||[]).map(c=>{
      const score = c.scoreDelta===null||c.scoreDelta===undefined
        ? (c.assessTransition||"переход") : `${c.scoreDelta>0?"+":""}${c.scoreDelta}`;
      const money = c.moneyDelta===null||c.moneyDelta===undefined ? "" : ` · Δ€ ${c.moneyDelta>0?"+":""}${c.moneyDelta}`;
      return `<div class="taskrow"><span class="taskrow__title">${esc(c.email)}</span>
      <span class="taskrow__meta">${c.baseLevel} ${c.baseScore??"—"} → ${c.scenLevel} ${c.scenScore??"—"} · ${esc(score)}${money}${c.reasonsChanged?" · причины":""}</span></div>`;}).join("");
    out.innerHTML=`<div class="cmp__meta">asOf ${esc(Fmt.dateTime(d.asOf))} · baseline v${d.baseModelVersion} · snapshot v${d.snapshotVersion}</div>
      <div class="cmp__levels"><span class="cmp__lv">high ${d.baseCounts.high} → ${d.scenCounts.high}</span><span class="cmp__lv">medium ${d.baseCounts.medium} → ${d.scenCounts.medium}</span><span class="cmp__lv">low ${d.baseCounts.low} → ${d.scenCounts.low}</span><span class="cmp__lv">no_assess ${d.baseCounts.no_assessment} → ${d.scenCounts.no_assessment}</span></div>
      <div class="cmp__meta">at-risk: ${d.moneyBase.at_risk_total} → ${d.moneyScen.at_risk_total} (EUR cents)</div>
      <div class="cmp__tag">Сценарий, не применён — рабочая модель не изменилась.</div>
      ${rows||`<div class="analytics__empty">Изменений нет при этих коэффициентах.</div>`}`;
  }

  /* -------- tasks view (full section) -------- */
  let _savedTable = null;   // stash the customer table so Задачи can restore it
  function openTasksView(){
    const main = $("#table-region");
    if (!main) return;
    if (!_savedTable) _savedTable = main.innerHTML;   // keep the list — restore on back
    main.innerHTML = `<div id="tasks-view"><div class="toolbar">${UI.Skeleton("40%")}</div></div>`;
    // scroll to the panel so the swap is visible — the table region sits below
    // the KPI blocks and otherwise opens out of view.
    main.scrollIntoView({ behavior: "smooth", block: "start" });
    loadTasksInto(main);
  }
  function closeTasksView(){
    const main = $("#table-region");
    if (main && _savedTable !== null) { main.innerHTML = _savedTable; _savedTable = null;
      main.scrollIntoView({ behavior: "smooth", block: "start" }); }
  }
  async function loadTasksInto(main){
    const r = await api.listTasks({});
    const tasks = r.ok ? (r.data||[]) : [];
    main.innerHTML = `
      <div class="tasks-head"><h2>Задачи на удержание</h2>
        <div class="tasks-head__ctl">
          <input id="task-search" class="c-input" placeholder="Поиск по заголовку" style="max-width:240px">
          ${UI.Button({label:"← Клиенты",variant:"ghost",size:"sm",attrs:{"data-action":"tasks-close"}})}
        </div></div>
      <div id="tasks-list"></div>`;
    const list = main.querySelector("#tasks-list");
    const rows = tasks.map(t=>`
      <div class="taskrow" data-id="${t.id}">
        <div class="taskrow__main"><div class="taskrow__title">${esc(t.title)}</div>
          <div class="taskrow__meta">клиент ID ${t.accountId} · ${esc(t.assigneeLogin)} · ${esc(t.status)}${t.dueAt?` · ${Fmt.date(t.dueAt)}`:""}${!t.customerAvailable?` · <em title="клиент не в текущем батче">данные недоступны</em>`:""}</div>
          ${t.description?`<div class="taskrow__desc" hidden>${esc(t.description)}</div>`:""}
        </div>
        <div class="taskrow__ctl">
          ${t.description?`<button type="button" class="linkbtn taskrow__more" data-id="${t.id}" aria-expanded="false">Подробнее</button>`:""}
          <select class="c-select taskrow__st" data-id="${t.id}">
            ${["new","in_progress","done","cancelled"].map(s=>`<option value="${s}"${s===t.status?" selected":""}>${s}</option>`).join("")}
          </select>
        </div>
      </div>`).join("");
    list.innerHTML = tasks.length ? rows : `<p class="sec__note">Задач пока нет.</p>`;
    list.addEventListener("change", async (e)=>{
      const sel=e.target.closest(".taskrow__st"); if(!sel)return;
      const r2=await api.setTaskStatus(+sel.dataset.id,sel.value);
      if(r2.ok)loadTasksInto(main);
    });
    list.addEventListener("click",(e)=>{
      const b=e.target.closest(".taskrow__more"); if(!b)return;
      const d=b.closest(".taskrow").querySelector(".taskrow__desc");
      const open=d.hasAttribute("hidden"); d.toggleAttribute("hidden");
      b.setAttribute("aria-expanded",String(open)); b.textContent=open?"Скрыть":"Подробнее";
    });
    main.querySelector("#task-search").addEventListener("input",async(e)=>{
      const rr=await api.listTasks({q:e.target.value});
      const tt=rr.ok?(rr.data||[]):[];
      list.innerHTML=tt.map(t=>`<div class="taskrow"><div class="taskrow__main"><div class="taskrow__title">${esc(t.title)}</div><div class="taskrow__meta">ID ${t.accountId} · ${esc(t.status)}${t.description?` · ${esc(t.description)}`:""}</div></div></div>`).join("")||`<p class="sec__note">Ничего не найдено.</p>`;
    });
  }

  /* -------- csv export -------- */
  function csvCell(v){
    const s=String(v??"");
    // spreadsheet formula injection guard
    const lead = /^[=+\-@\t\r]/.test(s) ? "'" : "";
    return `"${lead}${s.replace(/"/g,'""')}"`;
  }
  function exportCsv(){
    const rows=state.rows||[];
    const header=["ID","Email","Баланс (EUR cents)","Пополнено (EUR cents)","Score","Уровень риска","Причины"];
    const lines=[header.map(csvCell).join(",")];
    for(const r of rows){
      lines.push([r.id,r.email,r.balanceCents,r.totalReplenishedCents,r.score??"no_assessment",r.level,(r.reasons||[]).join(" | ")].map(csvCell).join(","));
    }
    const blob=new Blob([lines.join("\r\n")],{type:"text/csv;charset=utf-8"});
    const a=document.createElement("a");a.href=URL.createObjectURL(blob);
    a.download=`tethered-customers-${new Date().toISOString().slice(0,10)}.csv`;a.click();
    URL.revokeObjectURL(a.href);
  }

  window.CustomersPage = { reload: load, state, channels: ch, startPolling, stopPolling,
    onSettingsSaved() { pollTick(); } };
  document.addEventListener("DOMContentLoaded", init);
})();
