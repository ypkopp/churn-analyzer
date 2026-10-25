/* =========================================================================
   Tethered — «Что проверить дальше» (P03).
   Deterministic recommendations derived ONLY from the current customer card:
   reasonCodes/contributions, service lines and money of ONE evaluation batch.
   No scoring, no LLM, no probability, no auto-action — a human still creates
   the task explicitly. Each rule cites the code reason, the linked service and
   the concrete fact (date / amount) that triggered it.
   ========================================================================= */
(function () {
  "use strict";

  /* Map each reason code to one deterministic rule.
     Returns { code, title, evidence(services), serviceIds } or null when the
     reason produces no recommendation. Facts come from the services list so a
     suggestion always carries its source. */
  const RULES = {
    prolong_wait: {
      title: "Проверить оплату и уточнить намерение продления",
      services: (svcs) => svcs.filter(s => s.status === "prolong_wait"),
      evidence: (s) => `услуга ждёт оплаты · ${s.name} · ${Fmt.money(s.price)}`,
    },
    expiring_no_autoprolong: {
      title: "Уточнить намерение продления",
      // Match the reason to the concrete services that produced it: prefer the
      // service ids named in this batch's feature contributions; fall back to
      // the horizon filter only when no contribution is present. Never invent a
      // fact — a distant expiry outside the batch horizon is not suggested.
      services: (svcs, ctx, card) => {
        const ids = serviceIdsFor(card, "expiring_no_autoprolong");
        const base = svcs.filter(s => ["active","suspended"].includes(s.status) && !s.autoprolong);
        if (ids) return base.filter(s => ids.has(s.id));
        return base.filter(s => ctx && ctx.horizonEnd && s.expiresAt <= ctx.horizonEnd);
      },
      evidence: (s) => `истекает ${Fmt.date(s.expiresAt)} · ${s.name} · ${Fmt.money(s.price)}`,
    },
    insufficient_funds: {
      title: "Проверить покрытие ближайших продлений",
      // One summary line — uncovered is account-level, not a separate debt
      // per service; do not duplicate the amount for every service. Missing
      // money stays unknown — it is never coerced to a 0 or a fake "covered".
      services: (svcs) => svcs.filter(s => ["active","suspended","prolong_wait"].includes(s.status)),
      evidence: (s, money) => !money || money.uncoveredAmount == null
        ? "покрытие продлений не рассчитано — проверьте вручную"
        : money.uncoveredAmount > 0
          ? `непокрыто ${Fmt.money(money.uncoveredAmount)} из ${Fmt.money(money.renewalCostInHorizon)}`
          : `доступный фонд ${Fmt.money(money.availableFund)} покрывает продления`,
    },
    suspended: {
      title: "Уточнить причину приостановки",
      services: (svcs) => svcs.filter(s => s.status === "suspended"),
      evidence: (s) => `приостановлена (тарификация продолжается) · ${s.name}`,
    },
  };

  /* Extract the service ids a contribution names for one reason code.
     factor.label is the backend detail, e.g. "service:101 expires without
     autoprolong" — returns null when no factor carries a service id so the
     caller can fall back instead of guessing. */
  function serviceIdsFor(card, code){
    const factors = card.assessment?.factors || [];
    const ids = new Set();
    for (const f of factors) {
      if (f.key !== code) continue;
      const m = /service:(\d+)/.exec(f.label || "");
      if (m) ids.add(Number(m[1]));
    }
    return ids.size ? ids : null;
  }

  /* Build the recommendation list for one customer card.
     ctx.asOf/horizonEnd come from the batch snapshot — never Date.now. */
  function build(card, ctx) {
    const out = [];
    const reasons = new Set(card.assessment?.reasons || []);
    const svcs = card.services || [];
    const money = card.assessment?.money;

    for (const [code, rule] of Object.entries(RULES)) {
      if (!reasons.has(code)) continue;
      const svcsFor = rule.services(svcs, ctx, card);
      if (!svcsFor.length) continue;
      // one item per confirming service keeps each fact visible and linkable;
      // grouping/capping happens in the renderer, which may collapse extras.
      for (const s of svcsFor) {
        out.push({ code, title: rule.title, serviceId: s.id,
          evidence: rule.evidence(s, money) });
      }
    }
    // no_assessment — separate explanatory item, not a "low risk" tip
    if (card.assessment?.level === "none" || card.assessment?.noAssessment) {
      out.push({ code: "no_assessment",
        title: "Объяснение: недостаточно релевантных услуг для оценки",
        serviceId: null,
        evidence: "нет услуг, применимых к модели продления" });
    }
    return out;
  }

  window.NextActions = { build };
})();
