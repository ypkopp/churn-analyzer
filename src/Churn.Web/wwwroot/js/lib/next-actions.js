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
    insufficient_funds: {
      title: "Проверить покрытие ближайших продлений",
      services: (svcs) => svcs.filter(s => ["active","suspended","prolong_wait"].includes(s.status)),
      evidence: (s, money) => `покрытие не хватает: непокрыто ${Fmt.money(money?.uncoveredAmount ?? 0)}, фонд ${Fmt.money(money?.availableFund ?? 0)}`,
    },
    expiring_no_autoprolong: {
      title: "Уточнить намерение продления",
      services: (svcs) => svcs.filter(s => ["active","suspended"].includes(s.status) && !s.autoprolong),
      evidence: (s) => `истекает ${Fmt.date(s.expiresAt)} · ${s.name} · ${Fmt.money(s.price)}`,
    },
    suspended: {
      title: "Уточнить причину приостановки",
      services: (svcs) => svcs.filter(s => s.status === "suspended"),
      evidence: (s) => `приостановлена (тарификация продолжается) · ${s.name}`,
    },
  };

  /* Build the recommendation list for one customer card.
     card = { account, assessment, services }; money = assessment.money.
     Dedup by rule type; a reason may map several services — keep them all.
     Returns [] when there is no confirmed reason (never a fake "low risk" tip). */
  function build(card) {
    const out = [];
    const reasons = new Set(card.assessment?.reasons || []);
    const svcs = card.services || [];
    const money = card.assessment?.money;

    for (const [code, rule] of Object.entries(RULES)) {
      if (!reasons.has(code)) continue;
      const svcsFor = rule.services(svcs);
      if (!svcsFor.length) continue;
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
