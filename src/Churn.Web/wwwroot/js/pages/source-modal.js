/* =========================================================================
   «Источник данных» — реальная атомарная замена JSON-снимка через ApiAdapter.
   Проверка и подтверждение перед отправкой; серверные ошибки без сырых данных;
   невалидный снимок не очищает рабочую таблицу. После успеха — новая версия
   и обновлённый результат (polling обновит UI).
   ========================================================================= */
(function () {
  "use strict";
  const { UI, Fmt } = window;
  const api = window.DataAdapter;
  const esc = Fmt.esc;

  let status = "idle", serverError = null, fileText = null, fileName = "", parseInfo = null;
  const modal = new UI.Modal({ id: "source-modal", labelledBy: "src-title" });
  modal.el.style.width = "min(600px, 100%)";

  function reset() { status = "idle"; serverError = null; fileText = null; fileName = ""; parseInfo = null; }

  function analyze(text) {
    try {
      const d = JSON.parse(text);
      const a = Array.isArray(d.accounts) ? d.accounts.length : "?";
      const s = Array.isArray(d.services) ? d.services.length : "?";
      const t = Array.isArray(d.transactions) ? d.transactions.length : "?";
      return { ok: true, accounts: a, services: s, transactions: t };
    } catch (e) { return { ok: false, error: "Не удалось разобрать JSON: " + e.message }; }
  }

  function render() {
    const pending = status === "pending";
    modal.setContent(`
      <div class="c-modal__head">
        <div style="flex:1"><h2 class="set__title" id="src-title">Источник данных</h2>
          <p class="set__sub">Атомарная замена рабочего снимка — загружается один JSON</p></div>
        ${UI.Button({ label: "Закрыть (Esc)", iconOnly: true, icon: "close", variant: "ghost", size: "sm", tipPos: "end", attrs: { "data-src": "close" } })}
      </div>
      <div class="c-modal__body">
        ${serverError ? `<div style="margin-bottom:12px">${UI.Banner({ tone: "danger", title: "Замена отклонена", text: esc(serverError) + " Рабочий снимок и таблица не изменены." })}</div>` : ""}
        <div class="set__demo" style="margin-bottom:12px">Файл заменяет весь снимок целиком. Невалидный снимок не трогает текущие данные. После успеха модель пересчитывает клиентов автоматически.</div>
        <label class="c-field">
          <span class="c-field__label">JSON-снимок (accounts, services, transactions)</span>
          <input class="c-input" type="file" id="src-file" accept="application/json,.json" ${pending ? "disabled" : ""}>
        </label>
        <div style="margin-top:10px">
          ${UI.Button({ label: "Сгенерировать демо-снимок", variant: "ghost", size: "sm",
            disabled: pending, attrs: { "data-src": "gen" } })}
          <div class="sec__note" style="margin-top:6px">Детерминированный снимок (seed 42) — та же фикстура что и у стенда; замена идёт через тот же PUT /source/snapshot.</div>
        </div>
        ${parseInfo ? (parseInfo.ok
          ? `<div style="margin-top:12px">${UI.Banner({ tone: "info", mark: "i", title: "Файл распознан", text: `Аккаунтов: ${parseInfo.accounts} · услуг: ${parseInfo.services} · транзакций: ${parseInfo.transactions}. Подтвердите замену.` })}</div>`
          : `<div style="margin-top:12px">${UI.Banner({ tone: "danger", title: "Файл не читается", text: esc(parseInfo.error) })}</div>`) : ""}
      </div>
      <div class="c-modal__foot">
        <span class="set__foot-left" aria-live="polite">${pending ? "Загрузка и пересчёт…" : status === "done" ? "Снимок заменён" : ""}</span>
        ${UI.Button({ label: "Отмена", disabled: pending, attrs: { "data-src": "close" } })}
        ${UI.Button({ label: pending ? "Замена…" : "Заменить снимок", variant: "primary",
          state: pending ? "pending" : status === "done" ? "success" : undefined,
          disabled: pending || !fileText || !(parseInfo && parseInfo.ok),
          disabledReason: !fileText ? "Выберите JSON-файл" : !(parseInfo && parseInfo.ok) ? "Файл не распознан" : undefined,
          attrs: { "data-src": "apply" } })}
      </div>`);
  }

  modal.el.addEventListener("change", (e) => {
    if (e.target.id !== "src-file") return;
    const f = e.target.files && e.target.files[0];
    if (!f) { fileText = null; parseInfo = null; render(); return; }
    fileName = f.name;
    const r = new FileReader();
    r.onload = () => { fileText = String(r.result || ""); parseInfo = analyze(fileText); render(); };
    r.onerror = () => { parseInfo = { ok: false, error: "Не удалось прочитать файл." }; render(); };
    r.readAsText(f);
  });
  modal.el.addEventListener("click", async (e) => {
    const b = e.target.closest("[data-src]"); if (!b) return;
    if (b.getAttribute("aria-disabled") === "true") { e.preventDefault(); return; }
    if (b.dataset.src === "close") modal.close();
    if (b.dataset.src === "apply") await apply();
    if (b.dataset.src === "gen") await generate();
  });

  async function generate() {
    if (status === "pending") return;
    status = "pending"; serverError = null; render();
    const r = await api.genSnapshot();
    if (!r.ok) {
      status = "idle"; serverError = r.error?.message || "Не удалось сгенерировать.";
      render(); return;
    }
    fileText = JSON.stringify(r.data); fileName = "demo-snapshot.json";
    parseInfo = analyze(fileText); status = "idle"; render();
  }

  async function apply() {
    if (status === "pending" || !fileText) return;
    status = "pending"; serverError = null; render();
    const res = await api.putSnapshot(fileText);
    if (!res.ok) {
      status = "idle"; serverError = res.error.message;
      if (res.error.code === "session_expired") { modal.close(); return window.CustomersPage.reload(); }
      render();
      UI.Toast.show({ tone: "error", title: "Снимок не принят", text: res.error.message });
      return;
    }
    status = "done"; render();
    UI.Toast.show({ tone: "success", title: "Снимок заменён",
      text: `Версия v${res.data.snapshotVersion}. Пересчёт выполнен, таблица обновится.` });
    await new Promise((r) => setTimeout(r, 800));
    modal.close();
    window.CustomersPage.reload();
  }

  function open() { reset(); modal.el.innerHTML = ""; render(); modal.open();
    document.querySelector('[data-action="open-source"]').setAttribute("aria-expanded", "true"); }
  modal.onClose = () => { const b = document.querySelector('[data-action="open-source"]'); b && b.setAttribute("aria-expanded", "false"); };

  window.SourceModal = { open, isOpen: () => modal.isOpen };
})();
