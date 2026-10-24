/* Форматирование. Деньги приходят в еврокопейках (100 = €1). */
(function () {
  "use strict";
  const eur = new Intl.NumberFormat("ru-RU", { style: "currency", currency: "EUR", minimumFractionDigits: 2, maximumFractionDigits: 2 });
  const eur0 = new Intl.NumberFormat("ru-RU", { style: "currency", currency: "EUR", minimumFractionDigits: 0, maximumFractionDigits: 0 });
  const int = new Intl.NumberFormat("ru-RU");
  const dt = new Intl.DateTimeFormat("ru-RU", { day: "2-digit", month: "2-digit", year: "numeric" });
  const dtm = new Intl.DateTimeFormat("ru-RU", { day: "2-digit", month: "long", hour: "2-digit", minute: "2-digit" });
  const tm = new Intl.DateTimeFormat("ru-RU", { hour: "2-digit", minute: "2-digit" });

  const Fmt = {
    money(cents) { return cents === null || cents === undefined ? "—" : eur.format(cents / 100); },
    moneyRound(cents) { return cents === null || cents === undefined ? "—" : eur0.format(Math.round(cents / 100)); },
    int(n) { return n === null || n === undefined ? "—" : int.format(n); },
    date(iso) { return iso ? dt.format(new Date(iso)) : "—"; },
    dateTime(iso) { return iso ? dtm.format(new Date(iso)) : "—"; },
    time(iso) { return iso ? tm.format(new Date(iso)) : "—"; },
    daysBetween(fromIso, toIso) {
      if (!toIso) return null;
      const a = new Date(fromIso.slice(0, 10)), b = new Date(toIso.slice(0, 10));
      return Math.round((b - a) / 86400000);
    },
    relDays(n) {
      if (n === null) return "срок не указан";
      if (n === 0) return "сегодня";
      const abs = Math.abs(n);
      const w = Fmt.plural(abs, "день", "дня", "дней");
      return n > 0 ? "через " + abs + " " + w : abs + " " + w + " назад";
    },
    plural(n, one, few, many) {
      const m10 = n % 10, m100 = n % 100;
      if (m10 === 1 && m100 !== 11) return one;
      if (m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14)) return few;
      return many;
    },
    esc(s) { return String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c])); }
  };

  Fmt.TERM = { hour: "в час", week: "в неделю", month: "в месяц", quarter_year: "за 3 мес.", half_year: "за 6 мес.", year: "в год" };
  Fmt.SERVICE_STATUS = {
    active:       { label: "Активна",        tone: "accent" },
    suspended:    { label: "Выключена",      tone: "neutral" },
    prolong_wait: { label: "Ожидает оплаты", tone: "danger" },
    deleted:      { label: "Удалена",        tone: "neutral" },
    blocked:      { label: "Заблокирована",  tone: "warning" }
  };
  Fmt.TX_STATUS = { performed: "выполнена", not_performed: "не выполнена" };
  Fmt.TX_TYPE = { prolong: "Продление", order: "Заказ" };

  window.Fmt = Fmt;
})();
