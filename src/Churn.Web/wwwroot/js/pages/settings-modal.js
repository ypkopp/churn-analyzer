/* =========================================================================
   «Настройки модели» — реальные настройки через ApiAdapter.
   Загружает текущие значения с сервера, валидирует, шлёт expectedVersion;
   409 объясняется пользователю, введённые значения не теряются.
   «Сохранено» показывается только после успешного ответа.
   ========================================================================= */
(function () {
  "use strict";
  const { UI, Fmt } = window;
  const api = window.DataAdapter;
  const esc = Fmt.esc;

  let original = null, form = null, catalog = [], errors = {}, touched = {},
      status = "idle", serverError = null, conflict = null;
  const SUCCESS_HOLD_MS = 700;
  const modal = new UI.Modal({ id: "settings-modal", labelledBy: "set-title" });
  modal.el.style.width = "min(640px, 100%)";

  const parseNum = (v) => { const s = String(v).trim().replace(",", "."); return s === "" ? NaN : Number(s); };
  const fmtNum = (v) => String(v).replace(".", ",");
  const isDirty = () => JSON.stringify(form) !== JSON.stringify(original);

  function readForm() {
    const el = modal.el;
    const val = (id) => el.querySelector("#" + id).value;
    form = {
      horizonDays: parseNum(val("f-horizon")),
      thresholds: { medium: parseNum(val("f-th-medium")), high: parseNum(val("f-th-high")) },
      includeBonusBalance: el.querySelector("#f-bonus") ? el.querySelector("#f-bonus").checked : true,
      weights: Object.fromEntries(catalog.map((c) => [c.key, parseNum(val("f-w-" + c.key))]))
    };
    errors = api.validateSettings(form);
  }
  const err = (k) => (touched[k] || status === "submitted") ? errors[k] : undefined;

  function render() {
    setDismissible();
    const pending = status === "pending", locked = pending || status === "success";
    const errCount = Object.keys(errors).length;
    const raw = (id, fb) => { const n = modal.el.querySelector("#" + id); return n ? n.value : fb; };

    modal.setContent(`
      <div class="c-modal__head">
        <div style="flex:1"><h2 class="set__title" id="set-title">Настройки модели</h2>
          <p class="set__sub">Версия <span class="mono">v${esc(original.version)}</span> · коэффициенты модели риска</p></div>
        ${UI.Button({ label: "Закрыть (Esc)", iconOnly: true, icon: "close", variant: "ghost", size: "sm", tipPos: "end", attrs: { "data-set": "close" } })}
      </div>
      <form class="c-modal__body" id="set-form" novalidate>
        ${conflict ? `<div style="margin-top:12px">${UI.Banner({ tone: "warning", title: "Настройки изменены в другом окне", text: esc(conflict) + " Ваши введённые значения сохранены в форме — перечитайте и решите, применить ли их." })}</div>` : ""}
        ${status === "submitted" && errCount ? `<div style="margin-top:12px">${UI.Banner({ tone: "danger", title: `Исправьте ${errCount} ${Fmt.plural(errCount,"поле","поля","полей")}`, text: "Изменения не отправлены." })}</div>` : ""}
        ${serverError ? `<div style="margin-top:12px">${UI.Banner({ tone: "danger", title: "Не удалось сохранить", text: esc(serverError) })}</div>` : ""}
        <fieldset class="set__group" style="border-left:0;border-right:0;border-top:0;margin:0;padding-left:0;padding-right:0" ${locked ? "disabled" : ""}>
          <legend class="sr-only">Общие параметры</legend>
          <h3 class="set__group-title">Общие параметры</h3>
          <div class="set__grid">
            ${UI.Input({ id: "f-horizon", label: "Горизонт, дней", value: raw("f-horizon", original.horizonDays), numeric: true, inputmode: "numeric", hint: "1–3660", error: err("horizonDays"), attrs: { "data-k": "horizonDays", "data-autofocus": "" } })}
            ${UI.Input({ id: "f-th-medium", label: "Порог «средний»", value: raw("f-th-medium", original.thresholds.medium), numeric: true, inputmode: "numeric", hint: "score ≥ значения", error: err("thresholds.medium"), attrs: { "data-k": "thresholds.medium" } })}
            ${UI.Input({ id: "f-th-high", label: "Порог «высокий»", value: raw("f-th-high", original.thresholds.high), numeric: true, inputmode: "numeric", hint: "больше порога «средний»", error: err("thresholds.high"), attrs: { "data-k": "thresholds.high" } })}
          </div>
          <label class="c-check" style="font-size:var(--text-sm)"><input type="checkbox" id="f-bonus" ${form && form.includeBonusBalance === false ? "" : "checked"}> Учитывать бонусный баланс, если bonusState = unlocked</label>
        </fieldset>
        <fieldset class="set__group" style="border:0;margin:0;padding-left:0;padding-right:0" ${locked ? "disabled" : ""}>
          <legend class="sr-only">Коэффициенты факторов</legend>
          <h3 class="set__group-title">Коэффициенты факторов <small>0 — вклад фактора отключён</small></h3>
          ${Object.values(form.weights || {}).every((w) => w === 0) ? `<div style="margin:8px 0">${UI.Banner({ tone: "warning", title: "Вклады факторов отключены", text: "Все веса равны 0 — признаки не дают вклада в score. Это не доказывает отсутствие риска." })}</div>` : ""}
          <div class="weights">
            ${catalog.map((c) => {
              const id = "f-w-" + c.key, k = "weights." + c.key;
              const v = raw(id, fmtNum(original.weights[c.key] ?? 0));
              const e = err(k);
              return `<div class="weight"><label for="${id}"><div class="weight__label">${esc(c.label)}</div><div class="weight__key">${esc(c.key)}</div></label>
                <input class="c-input c-input--num" id="${id}" value="${esc(v)}" inputmode="decimal" autocomplete="off" data-k="${k}" ${e ? `aria-invalid="true" aria-describedby="${id}-err"` : ""}>
                ${e ? `<div class="c-field__error" id="${id}-err">${esc(e)}</div>` : ""}</div>`;
            }).join("")}
          </div>
        </fieldset>
      </form>
      <div class="c-modal__foot">
        <span class="set__foot-left" aria-live="polite">${pending ? "Сохранение…" : status === "success" ? "Сохранено и пересчитано" : status === "failed" ? "Не сохранено — значения в форме не потеряны" : isDirty() ? "Есть несохранённые изменения" : "Изменений нет"}</span>
        ${UI.Button({ label: "Сбросить", variant: "ghost", disabled: locked || !isDirty(), disabledReason: locked ? "Идёт сохранение" : "Нет изменений", attrs: { "data-set": "reset" } })}
        ${UI.Button({ label: "Отмена", disabled: locked, disabledReason: "Идёт сохранение", attrs: { "data-set": "close" } })}
        ${(() => {
          const st = pending ? "pending" : status === "success" ? "success" : status === "failed" ? "error" : undefined;
          const label = { pending: "Сохранение…", success: "Сохранено", error: "Повторить" }[st] || "Сохранить";
          return UI.Button({ label, variant: "primary", state: st, disabled: st === "success" || (!st && !isDirty()), disabledReason: st === "success" ? undefined : "Измените хотя бы одно значение", attrs: { "data-set": "save", type: "submit", form: "set-form", class: "c-btn c-btn--primary set__save" } });
        })()}
      </div>`);
  }

  function rerender() {
    const a = document.activeElement, id = a && a.id, pos = a && a.selectionStart;
    render();
    if (id) { const n = modal.el.querySelector("#" + id); if (n) { n.focus(); try { n.setSelectionRange(pos, pos); } catch (_) {} } }
  }

  modal.el.addEventListener("input", (e) => { if (e.target.matches("input")) { serverError = null; conflict = null; if (status === "failed") status = "idle"; readForm(); if (e.target.dataset.k && errors[e.target.dataset.k] === undefined) touched[e.target.dataset.k] = true; rerender(); } });
  modal.el.addEventListener("change", (e) => { if (e.target.id === "f-bonus") { readForm(); rerender(); } });
  modal.el.addEventListener("focusout", (e) => { const k = e.target.dataset && e.target.dataset.k; if (k && !touched[k]) { touched[k] = true; readForm(); setTimeout(rerender, 0); } });
  modal.el.addEventListener("submit", (e) => { e.preventDefault(); save(); });
  modal.el.addEventListener("click", (e) => {
    const b = e.target.closest("[data-set]"); if (!b) return;
    if (b.getAttribute("aria-disabled") === "true") { e.preventDefault(); return; }
    if (b.dataset.set === "close" && status !== "pending") modal.close();
    if (b.dataset.set === "reset") { touched = {}; errors = {}; serverError = null; conflict = null; status = "idle"; form = structuredClone(original); modal.el.innerHTML = ""; render(); }
    if (b.dataset.set === "save") { e.preventDefault(); save(); }
  });

  const setDismissible = () => { modal.dismissible = status !== "pending" && status !== "success"; };
  async function save() {
    if (status === "pending" || status === "success") return;
    readForm();
    if (Object.keys(errors).length) {
      status = "submitted"; rerender();
      const first = modal.el.querySelector('[aria-invalid="true"]'); first && first.focus();
      return;
    }
    status = "pending"; serverError = null; conflict = null; rerender();
    const res = await api.patchModelSettings(form, original.version);
    if (!res.ok) {
      if (res.error.code === "conflict") { conflict = res.error.message + " Текущая версия на сервере может отличаться — нажмите «Перечитать» на главном экране."; status = "idle"; }
      else if (res.error.code === "validation") { errors = res.error.fields || {}; status = "submitted"; }
      else { status = "failed"; serverError = res.error.message; }
      rerender();
      UI.Toast.show({ tone: "error", title: "Настройки не сохранены", text: res.error.message });
      return;
    }
    status = "success"; original = { ...form, version: res.data.version }; rerender();
    window.CustomersPage.onSettingsSaved(res.data);
    await new Promise((r) => setTimeout(r, SUCCESS_HOLD_MS));
    if (status !== "success" || !modal.isOpen) return;
    status = "idle"; modal.close();
    UI.Toast.show({ tone: "success", title: "Настройки сохранены", text: "Оценки пересчитаны. Обновление таблицы выполнено автоматически." });
  }

  async function open() {
    const res = await api.getModelSettings();
    if (!res.ok) { UI.Toast.show({ tone: "error", title: "Настройки недоступны", text: res.error.message }); return; }
    original = res.data; catalog = res.meta.factorCatalog;
    // заполнить отсутствующие веса нулями из каталога
    catalog.forEach((c) => { if (original.weights[c.key] === undefined) original.weights[c.key] = 0; });
    form = structuredClone(original); errors = {}; touched = {}; status = "idle"; serverError = null; conflict = null;
    modal.el.innerHTML = ""; render(); modal.open();
    document.querySelector('[data-action="open-settings"]').setAttribute("aria-expanded", "true");
  }
  modal.onClose = () => { status = "idle"; const b = document.querySelector('[data-action="open-settings"]'); b && b.setAttribute("aria-expanded", "false"); };

  window.SettingsModal = { open, isOpen: () => modal.isOpen };
})();
