/* =========================================================================
   ApiAdapter — единственная точка доступа UI к данным.
   Реальные fetch → /api/v1/* с cookie-auth + CSRF. Никаких демо-данных:
   при ошибке НЕ подставляются вымышленные клиенты — возвращается Result.error.

   Контракт (тот же, что у прототипа):
     { ok: true,  data, meta }
     { ok: false, error: { code, message, retryable, fields? } }
   Коды: "session_expired"|"network"|"server"|"validation"|"not_found"|"conflict"
   ========================================================================= */
(function () {
  "use strict";

  const ok = (data, meta) => ({ ok: true, data, meta: meta || {} });
  const fail = (code, message, retryable = true, fields) =>
    ({ ok: false, error: { code, message, retryable, fields } });

  let csrfToken = null;
  async function csrf() {
    if (csrfToken) return csrfToken;
    const r = await fetch("/api/v1/auth/csrf", { credentials: "same-origin" });
    if (!r.ok) throw Object.assign(new Error("csrf"), { status: r.status });
    csrfToken = (await r.json()).token;
    return csrfToken;
  }

  async function req(method, url, body) {
    const headers = {};
    if (body !== undefined) {
      headers["Content-Type"] = "application/json";
      headers["X-CSRF-TOKEN"] = await csrf();
    }
    let r;
    try {
      r = await fetch(url, { method, headers, credentials: "same-origin",
        body: body === undefined ? undefined : JSON.stringify(body) });
    } catch (e) {
      return fail("network", "Сеть или сервер недоступны. Проверьте соединение и повторите.", true);
    }
    if (r.status === 401) return fail("session_expired", "Сессия истекла. Войдите снова, чтобы продолжить.", false);
    if (r.status === 403) return fail("csrf", "Запрос отклонён защитой CSRF. Обновите страницу и повторите.", true);
    if (r.status === 404) return fail("not_found", "Объект не найден в текущем снимке.", false);
    if (r.status === 409) {
      let d = {}; try { d = await r.json(); } catch (_) {}
      return fail("conflict", d.detail || d.error || "Данные уже изменены в другой вкладке.", false);
    }
    if (r.status === 400) {
      let d = {}; try { d = await r.json(); } catch (_) {}
      return fail("validation", d.detail || d.error || "Сервер отклонил данные.", false, d.fields);
    }
    if (!r.ok) return fail("server", `Сервер вернул ошибку ${r.status}.`, true);
    if (r.status === 204) return ok(null);
    let data = null; try { data = await r.json(); } catch (_) {}
    return ok(data);
  }

  const J = (v) => (v === null || v === undefined ? null : v);

  const ApiAdapter = {
    kind: "api",

    /* GET /api/v1/auth/me */
    async getSession() {
      const r = await req("GET", "/api/v1/auth/me");
      if (!r.ok) return r;
      return ok({ login: r.data.login, displayName: r.data.login, authMode: "cookie" });
    },

    /* GET /api/v1/customers?q=&riskLevel=&sort=&dir= — поиск q на сервере */
    async listCustomers({ q = "", level = "all", sort = "score", dir = "desc", pageSize = 500 } = {}) {
      const p = new URLSearchParams();
      if (q) p.set("q", q);
      if (level !== "all") p.set("riskLevel", level === "none" ? "no_assessment" : level);
      const sortMap = { id: "id", email: "email", balanceCents: "balance",
        totalReplenishedCents: "totalReplenished", score: "riskScore" };
      p.set("sort", sortMap[sort] || "riskScore");
      p.set("dir", dir);
      p.set("pageSize", String(pageSize));
      const r = await req("GET", "/api/v1/customers?" + p.toString());
      if (!r.ok) return r;
      const items = (r.data.items || []).map((i) => ({
        id: i.accountId, email: i.email,
        balanceCents: i.balance, totalReplenishedCents: i.totalReplenished,
        score: J(i.riskScore), level: i.riskLevel === "no_assessment" ? "none" : i.riskLevel,
        reason: (i.reasons && i.reasons[0]) || (i.noAssessment ? "Оценка отсутствует" : "—"),
        noAssessment: !!i.noAssessment
      }));
      return ok(items, {
        total: r.data.total,
        snapshot: { id: r.data.evaluationId, takenAt: r.data.asOf, stale: false,
          snapshotVersion: r.data.snapshotVersion, modelVersion: r.data.modelVersion },
        counts: null
      });
    },

    /* GET /api/v1/customers/{id} */
    async getCustomer(id) {
      const r = await req("GET", "/api/v1/customers/" + id);
      if (!r.ok) return r;
      const d = r.data.detail || r.data;
      const money = d.money || {};
      return ok({
        account: { id: d.accountId, email: d.email, balance: d.balance,
          withdrawBalance: d.withdrawBalance, totalReplenished: d.totalReplenished,
          bonusBalance: d.bonusBalance, bonusState: d.bonusState === "unlocked" },
        assessment: {
          level: d.riskLevel === "no_assessment" ? "none" : d.riskLevel,
          score: J(d.riskScore),
          reasons: d.reasons || [],
          factors: (d.features || []).map((f) => ({ key: f.code, label: f.detail || f.code, weight: f.weight, points: f.weight })),
          warnings: d.warnings || [],
          loyaltyIndicator: J(d.loyaltyIndicator),
          loyaltySemantics: d.loyaltySemantics || null,
          horizonDays: money.horizonDays || 0,
          money
        },
        services: (d.services || []).map((s) => ({
          id: s.id, name: s.name, status: s.status, paymentTerm: s.paymentTerm,
          autoprolong: !!s.autoProlong, price: s.price, expiresAt: s.expiresAt })),
        transactions: []
      }, { snapshot: { id: r.data.evaluationId, takenAt: r.data.asOf,
        snapshotVersion: r.data.snapshotVersion, modelVersion: r.data.modelVersion } });
    },

    /* GET /api/v1/analytics/summary */
    async getSummary() {
      const r = await req("GET", "/api/v1/analytics/summary");
      if (!r.ok) return r;
      if (r.data && r.data.state === "no_data")
        return ok({ noData: true, highCount: 0, mediumCount: 0, lowCount: 0, noAssessmentCount: 0, total: 0, scenarioLossCents: null, currency: "EUR", horizonDays: 0 });
      const c = r.data.counts || {};
      const m = r.data.money || {};
      return ok({
        highCount: c.high ?? 0, mediumCount: c.medium ?? 0, lowCount: c.low ?? 0,
        noAssessmentCount: c.noAssessment ?? c.no_assessment ?? 0,
        total: (c.high ?? 0) + (c.medium ?? 0) + (c.low ?? 0) + (c.noAssessment ?? c.no_assessment ?? 0),
        scenarioLossCents: J(m.atRiskTotal), currency: "EUR",
        horizonDays: m.horizonDays ?? 0, semantic: m.semantic,
        uncoveredTotal: m.uncoveredTotal, renewalCostTotal: m.renewalCostTotal
      }, { snapshot: { id: r.data.evaluationId, takenAt: r.data.asOf,
        snapshotVersion: r.data.snapshotVersion, modelVersion: r.data.modelVersion } });
    },

    /* GET /api/v1/analytics/renewals — upcoming renewals + reason counts,
       same published batch as summary (evaluationId/asOf/versions). */
    async getRenewals() {
      const r = await req("GET", "/api/v1/analytics/renewals");
      if (!r.ok) return r;
      if (r.data && r.data.state === "no_data")
        return ok({ noData: true, buckets: [], reasons: [], horizonDays: 0 });
      const d = r.data || {};
      return ok({
        noData: false, buckets: d.buckets || [], reasons: d.reasons || [],
        horizonDays: d.horizonDays ?? 0,
      }, { snapshot: { id: d.evaluationId, takenAt: d.asOf,
        snapshotVersion: d.snapshotVersion, modelVersion: d.modelVersion } });
    },

    /* GET /api/v1/model/settings */
    async getModelSettings() {
      const r = await req("GET", "/api/v1/model/settings");
      if (!r.ok) return r;
      const d = r.data || {};
      return ok({
        version: d.modelVersion ?? 0,
        horizonDays: d.horizonDays ?? 14,
        thresholds: d.thresholds || {},
        weights: d.weights || {},
        includeBonusBalance: true
      }, { factorCatalog: ApiAdapter.FACTOR_CATALOG });
    },

    /* PATCH /api/v1/model/settings { expectedVersion, settings } */
    async patchModelSettings(form, expectedVersion) {
      // drop non-finite weights — a NaN would serialize as null and fail server parse
      const weights = Object.fromEntries(
        Object.entries(form.weights || {}).filter(([, v]) => Number.isFinite(v)));
      const thresholds = Object.fromEntries(
        Object.entries(form.thresholds || {}).filter(([, v]) => Number.isFinite(v)));
      const settings = {
        modelVersion: 0,
        horizonDays: Number.isFinite(form.horizonDays) ? form.horizonDays : 14,
        weights, thresholds
      };
      const r = await req("PATCH", "/api/v1/model/settings", { expectedVersion, settings });
      if (!r.ok) return r;
      return ok({
        version: r.data.modelVersion, horizonDays: form.horizonDays,
        thresholds: form.thresholds, weights: form.weights,
        evaluationId: r.data.evaluationId, asOf: r.data.asOf
      });
    },

    /* PUT /api/v1/source/snapshot — JSON body is the parsed snapshot */
    async putSnapshot(jsonText) {
      let snap;
      try { snap = JSON.parse(jsonText); }
      catch (_) { return fail("validation", "Файл не является корректным JSON.", false); }
      const r = await req("PUT", "/api/v1/source/snapshot", snap);
      if (!r.ok) return r;
      return ok({ snapshotVersion: r.data.snapshotVersion, evaluationId: r.data.evaluationId, asOf: r.data.asOf });
    },

    /* POST /api/v1/auth/logout */
    async logout() {
      const r = await req("POST", "/api/v1/auth/logout", {});
      return r.ok ? ok(null) : r;
    },

    /* Client-side validation mirrors server contract (ModelSettings). */
    validateSettings(s) {
      const e = {};
      const isNum = (v) => typeof v === "number" && Number.isFinite(v);
      if (!isNum(s.horizonDays) || !Number.isInteger(s.horizonDays)) e.horizonDays = "Целое число дней.";
      else if (s.horizonDays < 1 || s.horizonDays > 3660) e.horizonDays = "От 1 до 3660 дней.";
      const t = s.thresholds || {};
      Object.entries(s.weights || {}).forEach(([k, v]) => {
        if (!isNum(v)) e["weights." + k] = "Введите число.";
        else if (v < 0) e["weights." + k] = "Неотрицательное число.";
      });
      return e;
    },

    FACTOR_CATALOG: [
      { key: "expiring_no_autoprolong", label: "Истекает без автопродления" },
      { key: "insufficient_funds",      label: "Недостаточно средств" },
      { key: "prolong_wait",            label: "Ожидает оплаты" },
      { key: "suspended",               label: "Выключена пользователем" }
    ]
  };

  window.DataAdapter = ApiAdapter;
})();
