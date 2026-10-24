/* =========================================================================
   Общие компоненты (vanilla JS, без фреймворка).
   Каждый «рендер»-компонент возвращает HTML-строку с классами из
   components.css — эти же строки 1:1 переносятся в Razor partials /
   tag-helpers. Поведенческие компоненты (Drawer, Modal, Toast) — классы
   с open/close и управлением фокусом.
   ========================================================================= */
(function () {
  "use strict";
  const esc = (s) => window.Fmt.esc(s);
  const attrs = (o) => Object.entries(o || {}).filter(([, v]) => v !== undefined && v !== null && v !== false)
    .map(([k, v]) => (v === true ? k : `${k}="${esc(v)}"`)).join(" ");

  /* ---------- Icons: только простые геометрические формы ---------- */
  /* Знак — из src/ui/marks.js (три варианта, рекомендован A) */
  /* D04: знак и wordmark — src/ui/brand.js; иконки разделов — src/ui/icons.js */
  const Mark = (size = 24, opts) => window.TetheredBrand.CompactMark(size, opts);
  const Wordmark = (h = 24, opts) => window.TetheredBrand.Wordmark(h, opts);
  const TI = window.TetheredIcons;
  const okIcon = `<svg width="14" height="14" viewBox="0 0 16 16" fill="none" aria-hidden="true"><path d="M3.5 8.5l3 3 6-7" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/></svg>`;

  const Icon = {
    search: `<svg width="15" height="15" viewBox="0 0 16 16" fill="none" aria-hidden="true"><circle cx="7" cy="7" r="4.75" stroke="currentColor" stroke-width="1.5"/><path d="M10.5 10.5L14 14" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/></svg>`,
    close:  `<svg width="14" height="14" viewBox="0 0 16 16" fill="none" aria-hidden="true"><path d="M3.5 3.5l9 9M12.5 3.5l-9 9" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/></svg>`,
    users:  `<svg width="18" height="18" viewBox="0 0 18 18" fill="none" aria-hidden="true"><circle cx="7" cy="6" r="3" stroke="currentColor" stroke-width="1.5"/><path d="M1.75 15.5c.6-2.7 2.6-4.25 5.25-4.25s4.65 1.55 5.25 4.25" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/><path d="M12 3.2a2.9 2.9 0 010 5.6M13.6 11.4c1.4.6 2.3 1.9 2.65 4.1" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/></svg>`,
    sliders:`<svg width="18" height="18" viewBox="0 0 18 18" fill="none" aria-hidden="true"><path d="M2 5h8M14 5h2M2 13h2M8 13h8" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/><circle cx="12" cy="5" r="2" stroke="currentColor" stroke-width="1.5"/><circle cx="6" cy="13" r="2" stroke="currentColor" stroke-width="1.5"/></svg>`,
    db:     `<svg width="18" height="18" viewBox="0 0 18 18" fill="none" aria-hidden="true"><ellipse cx="9" cy="4.5" rx="6" ry="2.25" stroke="currentColor" stroke-width="1.5"/><path d="M3 4.5v9c0 1.25 2.7 2.25 6 2.25s6-1 6-2.25v-9M3 9c0 1.25 2.7 2.25 6 2.25S15 10.25 15 9" stroke="currentColor" stroke-width="1.5"/></svg>`,
    refresh: TI.refresh(16),
    clients: TI.clients(20),
    model:   TI.model(20),
    source:  TI.source(20),
    explain: TI.explain(20),
    download:`<svg width="14" height="14" viewBox="0 0 16 16" fill="none" aria-hidden="true"><path d="M8 2.5v8M4.5 7L8 10.5 11.5 7M3 13.5h10" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"/></svg>`,
    chevron:`<svg width="14" height="14" viewBox="0 0 16 16" fill="none" aria-hidden="true"><path d="M6 3.5L10.5 8 6 12.5" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"/></svg>`,
    menu:   `<svg width="18" height="18" viewBox="0 0 18 18" fill="none" aria-hidden="true"><path d="M3 5h12M3 9h12M3 13h12" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/></svg>`,
    alert:  `<svg width="18" height="18" viewBox="0 0 18 18" fill="none" aria-hidden="true"><circle cx="9" cy="9" r="7" stroke="currentColor" stroke-width="1.5"/><path d="M9 5.5v4.25" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/><circle cx="9" cy="12.5" r="1" fill="currentColor"/></svg>`,
    inbox:  `<svg width="18" height="18" viewBox="0 0 18 18" fill="none" aria-hidden="true"><rect x="2.5" y="3.5" width="13" height="11" rx="1.5" stroke="currentColor" stroke-width="1.5"/><path d="M2.5 10h3.5l1 1.75h4L12 10h3.5" stroke="currentColor" stroke-width="1.5" stroke-linejoin="round"/></svg>`,
    check:  okIcon,
    lock:   `<svg width="18" height="18" viewBox="0 0 18 18" fill="none" aria-hidden="true"><rect x="3.5" y="8" width="11" height="7.5" rx="1.5" stroke="currentColor" stroke-width="1.5"/><path d="M6 8V5.75a3 3 0 016 0V8" stroke="currentColor" stroke-width="1.5"/></svg>`
  };

  /* ---------- Button ---------- */
  /* state: undefined | "pending" | "success" | "error".
     spinIcon: в pending вращается собственная иконка кнопки (refresh), а не спиннер.
     Смысл состояния всегда дублируется текстом label — движение необязательно. */
  function Button({ label, variant, size, icon, iconOnly, disabled, disabledReason, pending, state, spinIcon, attrs: extra, type = "button", tipPos } = {}) {
    if (pending) state = "pending";
    pending = state === "pending";
    const cls = ["c-btn", variant && `c-btn--${variant}`, size && `c-btn--${size}`, iconOnly && "c-btn--icon", icon && "ic-host"].filter(Boolean).join(" ");
    const a = {
      type, class: cls,
      "aria-disabled": disabled || pending ? "true" : undefined,
      "data-state": state,
      "aria-busy": pending ? "true" : undefined,
      "data-tip": disabled && disabledReason ? disabledReason : (iconOnly ? label : undefined),
      "data-tip-pos": tipPos,
      "aria-label": iconOnly ? label : undefined,
      ...extra
    };
    const glyph = state === "success" ? `<span class="c-btn__glyph">${Icon.check}</span>`
      : state === "error" ? `<span class="c-btn__glyph">${Icon.alert.replace('width="18" height="18"', 'width="14" height="14"')}</span>`
      : pending && !spinIcon ? `<span class="c-spinner" aria-hidden="true"></span>`
      : icon ? `<span class="c-btn__glyph">${Icon[icon]}</span>` : "";
    const content = `${glyph}${iconOnly ? "" : `<span class="c-btn__label">${esc(label)}</span>`}`;
    const dr = disabled && disabledReason ? `<span class="sr-only">. ${esc(disabledReason)}</span>` : "";
    return `<button ${attrs(a)}>${content}${dr}</button>`;
  }

  /* ---------- Input ---------- */
  function Input({ id, label, value = "", placeholder, hint, error, type = "text", inputmode, numeric, icon, suffix, disabled, attrs: extra } = {}) {
    const descIds = [hint && `${id}-hint`, error && `${id}-err`].filter(Boolean).join(" ") || undefined;
    const input = `<input ${attrs({ id, type, class: "c-input" + (numeric ? " c-input--num" : ""), value, placeholder, inputmode, disabled, "aria-invalid": error ? "true" : undefined, "aria-describedby": descIds, autocomplete: "off", spellcheck: "false", ...extra })}>`;
    const control = icon || suffix ? `<div class="c-input-group">${icon ? `<span class="c-input-group__icon">${Icon[icon]}</span>` : ""}${input}${suffix ? `<span class="c-input-group__suffix">${esc(suffix)}</span>` : ""}</div>` : input;
    if (!label) return control;
    return `<div class="c-field">
      <label class="c-field__label" for="${esc(id)}">${esc(label)}</label>
      ${control}
      ${error ? `<div class="c-field__error" id="${esc(id)}-err" role="alert">${esc(error)}</div>` : hint ? `<div class="c-field__hint" id="${esc(id)}-hint">${esc(hint)}</div>` : ""}
    </div>`;
  }

  /* ---------- Select ---------- */
  function Select({ id, label, value, options = [], disabled, attrs: extra } = {}) {
    const sel = `<select ${attrs({ id, class: "c-select", disabled, ...extra })}>${options.map((o) => `<option value="${esc(o.value)}"${o.value === value ? " selected" : ""}>${esc(o.label)}</option>`).join("")}</select>`;
    return label ? `<div class="c-field"><label class="c-field__label" for="${esc(id)}">${esc(label)}</label>${sel}</div>` : sel;
  }

  /* ---------- Badge ---------- */
  const RISK = {
    high:   { label: "Высокий",     long: "Высокий риск" },
    medium: { label: "Средний",     long: "Средний риск" },
    low:    { label: "Низкий",      long: "Низкий риск" },
    none:   { label: "Нет оценки",  long: "Недостаточно данных" }
  };
  function Badge({ label, tone = "neutral", glyph = false, dot = false } = {}) {
    return `<span class="c-badge c-badge--${tone}${dot ? " c-badge--dot" : ""}">${glyph || dot ? `<span class="c-badge__glyph" aria-hidden="true"></span>` : ""}${esc(label)}</span>`;
  }
  function RiskBadge(level, { long = false } = {}) {
    const r = RISK[level] || RISK.none;
    return Badge({ label: long ? r.long : r.label, tone: level in RISK ? level : "none", glyph: true });
  }

  /* ---------- Table (header + sort) ---------- */
  function SortHeader({ key, label, sort, dir, numeric }) {
    const active = sort === key;
    const ariaSort = active ? (dir === "asc" ? "ascending" : "descending") : "none";
    const next = active && dir === "asc" ? "по убыванию" : "по возрастанию";
    return `<th scope="col" class="${numeric ? "is-num" : ""}" aria-sort="${ariaSort}">
      <button type="button" class="c-sort" data-sort="${esc(key)}" aria-label="${esc(label)}: сортировать ${next}">
        <span>${esc(label)}</span><span class="c-sort__icon" aria-hidden="true"><i></i><i></i></span>
      </button></th>`;
  }

  /* ---------- Loading ---------- */
  function Spinner(label = "Загрузка") { return `<span class="c-spinner" role="status" aria-label="${esc(label)}"></span>`; }
  function Skeleton(w = "100%") { return `<span class="c-skeleton" style="width:${w}"></span>`; }

  /* ---------- Empty / Error ---------- */
  function EmptyState({ title, text, actions = "", icon = "inbox" }) {
    return `<div class="c-state" role="status"><div class="c-state__mark">${Icon[icon]}</div><div class="c-state__title">${esc(title)}</div><div class="c-state__text">${esc(text)}</div>${actions ? `<div class="c-state__actions">${actions}</div>` : ""}</div>`;
  }
  function ErrorState({ title, text, code, actions = "", icon = "alert" }) {
    return `<div class="c-state c-state--error" role="alert"><div class="c-state__mark">${Icon[icon]}</div><div class="c-state__title">${esc(title)}</div><div class="c-state__text">${esc(text)}</div>${code ? `<div class="c-state__code">${esc(code)}</div>` : ""}${actions ? `<div class="c-state__actions">${actions}</div>` : ""}</div>`;
  }
  function Banner({ tone = "info", title, text, mark = "!", actions = "" }) {
    return `<div class="c-banner c-banner--${tone}" role="${tone === "danger" ? "alert" : "status"}"><span class="c-banner__mark" aria-hidden="true">${esc(mark)}</span><div class="c-banner__body">${title ? `<div class="c-banner__title">${esc(title)}</div>` : ""}${text ? `<div class="c-banner__text">${text}</div>` : ""}</div>${actions}</div>`;
  }

  /* ---------- Focus trap helper ---------- */
  const FOCUSABLE = 'a[href],button:not([disabled]),input:not([disabled]),select:not([disabled]),textarea:not([disabled]),[tabindex]:not([tabindex="-1"])';
  function trapFocus(container, e) {
    if (e.key !== "Tab") return;
    const f = [...container.querySelectorAll(FOCUSABLE)].filter((el) => el.offsetParent !== null);
    if (!f.length) return;
    const first = f[0], last = f[f.length - 1];
    if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
    else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
  }
  const motionMs = (name) => parseFloat(getComputedStyle(document.documentElement).getPropertyValue(name)) || 0;

  /* ---------- Drawer ---------- */
  class Drawer {
    constructor({ id, labelledBy, wide = false, modal = true, onClose }) {
      this.onClose = onClose; this.modal = modal; this.isOpen = false;
      this.overlay = document.createElement("div");
      this.overlay.className = "c-overlay"; this.overlay.hidden = true;
      this.el = document.createElement("aside");
      this.el.className = "c-drawer" + (wide ? " c-drawer--wide" : "");
      this.el.id = id; this.el.hidden = true;
      this.el.setAttribute("role", "dialog");
      this.el.setAttribute("aria-modal", modal ? "true" : "false");
      this.el.setAttribute("aria-labelledby", labelledBy);
      this.el.tabIndex = -1;
      document.body.append(this.overlay, this.el);
      this.overlay.addEventListener("click", () => this.close());
      this.el.addEventListener("keydown", (e) => { if (e.key === "Escape") { e.stopPropagation(); this.close(); } if (this.modal) trapFocus(this.el, e); });
    }
    setContent(html) { this.el.innerHTML = html; }
    open(returnFocusEl) {
      this.returnFocus = returnFocusEl || document.activeElement;
      if (this.isOpen) return;
      this.isOpen = true;
      this.overlay.hidden = !this.modal; this.el.hidden = false;
      requestAnimationFrame(() => { this.overlay.dataset.open = "true"; this.el.dataset.open = "true"; });
      setTimeout(() => (this.el.querySelector("[data-autofocus]") || this.el).focus({ preventScroll: true }), 30);
    }
    close({ restoreFocus = true } = {}) {
      if (!this.isOpen) return;
      this.isOpen = false;
      this.overlay.dataset.open = "false"; this.el.dataset.open = "false";
      setTimeout(() => { if (!this.isOpen) { this.overlay.hidden = true; this.el.hidden = true; } }, motionMs("--duration-drawer-out"));
      if (restoreFocus && this.returnFocus && document.contains(this.returnFocus)) this.returnFocus.focus({ preventScroll: true });
      this.onClose && this.onClose();
    }
  }

  /* ---------- Modal ---------- */
  class Modal {
    constructor({ id, labelledBy, onClose, dismissible = true }) {
      this.onClose = onClose; this.dismissible = dismissible; this.isOpen = false;
      this.overlay = document.createElement("div"); this.overlay.className = "c-overlay"; this.overlay.hidden = true;
      this.overlay.style.zIndex = "calc(var(--z-modal) - 1)";
      this.layer = document.createElement("div"); this.layer.className = "c-modal-layer"; this.layer.hidden = true;
      this.el = document.createElement("div"); this.el.className = "c-modal"; this.el.id = id;
      this.el.setAttribute("role", "dialog"); this.el.setAttribute("aria-modal", "true"); this.el.setAttribute("aria-labelledby", labelledBy); this.el.tabIndex = -1;
      this.layer.append(this.el); document.body.append(this.overlay, this.layer);
      this.layer.addEventListener("mousedown", (e) => { if (e.target === this.layer && this.dismissible) this.close(); });
      this.el.addEventListener("keydown", (e) => { if (e.key === "Escape" && this.dismissible) { e.stopPropagation(); this.close(); } trapFocus(this.el, e); });
    }
    setContent(html) { this.el.innerHTML = html; }
    open() {
      this.returnFocus = document.activeElement; this.isOpen = true;
      this.overlay.hidden = false; this.layer.hidden = false;
      requestAnimationFrame(() => { this.overlay.dataset.open = "true"; this.el.dataset.open = "true"; });
      setTimeout(() => (this.el.querySelector("[data-autofocus]") || this.el).focus(), 30);
    }
    close() {
      if (!this.isOpen) return; this.isOpen = false;
      this.overlay.dataset.open = "false"; this.el.dataset.open = "false";
      setTimeout(() => { if (!this.isOpen) { this.overlay.hidden = true; this.layer.hidden = true; } }, motionMs("--duration-fast"));
      if (this.returnFocus && document.contains(this.returnFocus)) this.returnFocus.focus();
      this.onClose && this.onClose();
    }
  }

  /* ---------- Toast ---------- */
  const Toast = {
    region: null,
    ensure() {
      if (this.region) return;
      this.region = document.createElement("div");
      this.region.className = "c-toast-region";
      this.region.setAttribute("aria-live", "polite");
      this.region.setAttribute("aria-relevant", "additions");
      document.body.append(this.region);
    },
    show({ title, text, tone = "success", timeout = 5000 }) {
      this.ensure();
      const t = document.createElement("div");
      t.className = `c-toast c-toast--${tone}`;
      t.setAttribute("role", tone === "error" ? "alert" : "status");
      t.innerHTML = `<span class="c-toast__mark" aria-hidden="true"></span><div><div class="c-toast__title">${esc(title)}</div>${text ? `<div class="c-toast__text">${esc(text)}</div>` : ""}</div>${Button({ label: "Закрыть уведомление", iconOnly: true, icon: "close", variant: "ghost", size: "sm", attrs: { "data-toast-close": "" } })}`;
      const remove = () => t.remove();
      t.querySelector("[data-toast-close]").addEventListener("click", remove);
      this.region.append(t);
      if (timeout) setTimeout(remove, timeout);
    }
  };

  window.UI = { Mark, Wordmark, Icon, Button, Input, Select, Badge, RiskBadge, RISK, SortHeader, Spinner, Skeleton, EmptyState, ErrorState, Banner, Drawer, Modal, Toast };
})();
