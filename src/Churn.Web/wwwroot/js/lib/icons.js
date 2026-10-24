/* =========================================================================
   Tethered · семейство иконок (D04). Оригинальная простая геометрия,
   без библиотек. Единые правила:
     viewBox 0 0 20 20 · обводка 1.6 · round caps/joins · поле 1.5–2
     «узел» (круг r 2–2.4) и «привязь» (короткая линия) — общий мотив.
   Цвет — currentColor; акцентные детали — var(--ic-accent, currentColor).
   Движение задаёт CSS (components.css → «Icons»): короткая реакция на
   hover/focus хоста .ic-host и отдельное «действие» (.is-act / pending).
   ========================================================================= */
(function () {
  "use strict";
  const A = 'fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"';
  const wrap = (name, body, size = 20) =>
    `<svg class="ic ic--${name}" width="${size}" height="${size}" viewBox="0 0 20 20" aria-hidden="true" focusable="false"><g ${A}>${body}</g></svg>`;

  const BODY = {
    /* Клиенты: два узла-человека, между головами — привязь. Hover: узлы сближаются. */
    clients: `<g class="ic-l"><circle cx="6.2" cy="7" r="2.3"/><path d="M2.4 16.2a3.8 3.8 0 0 1 7.6 0"/></g>
              <g class="ic-r"><circle cx="13.8" cy="7" r="2.3"/><path d="M10 16.2a3.8 3.8 0 0 1 7.6 0"/></g>
              <path class="ic-t" d="M8.7 7H11.3" stroke="var(--ic-accent, currentColor)"/>`,
    /* Модель: две направляющие и маркеры-ромбы. Hover/действие: маркеры смещаются. */
    model: `<path d="M3 6H17M3 14H17" opacity=".55"/>
            <path class="ic-m1" d="M7 3.7 9.3 6 7 8.3 4.7 6Z" fill="currentColor"/>
            <path class="ic-m2" d="M13 11.7 15.3 14 13 16.3 10.7 14Z" fill="var(--ic-accent, currentColor)" stroke="var(--ic-accent, currentColor)"/>`,
    /* Источник: три слоя. Hover: линия проходит сквозь слои сверху вниз. */
    source: `<path d="M10 3.2 16.8 6.6 10 10 3.2 6.6Z"/><path d="M3.2 10.2 10 13.6 16.8 10.2"/><path d="M3.2 13.6 10 17 16.8 13.6"/>
             <path class="ic-pass" d="M10 0.8V19.2" pathLength="1" stroke="var(--ic-accent, currentColor)" stroke-width="1.8"/>`,
    /* Обновление: незамкнутая дуга, наконечник-треугольник, узел на хвосте.
       Hover: поворот на четверть; вращение — только при data-state="pending". */
    refresh: `<g class="ic-rot"><path d="M15.6 11.2A5.8 5.8 0 1 1 13.9 5.6"/><path d="M12.6 2.9 16.4 5.3 12.4 7.3Z" fill="currentColor"/><circle cx="15.6" cy="11.2" r=".9" fill="currentColor"/></g>`,
    /* Объяснение риска: узел-клиент, привязь к скобке причин. Действие: строки причин раскрываются по очереди. */
    explain: `<circle cx="4.2" cy="10" r="2.1"/><path d="M6.3 10H8M8 5V15" stroke="var(--ic-accent, currentColor)"/>
              <path class="ic-x1" d="M8 5H16.6"/><path class="ic-x2" d="M8 10H14.2"/><path class="ic-x3" d="M8 15H15.6"/>`
  };

  const Icons = Object.fromEntries(Object.keys(BODY).map((k) => [k, (size) => wrap(k, BODY[k], size)]));
  Icons.NAMES = Object.keys(BODY);
  window.TetheredIcons = Icons;
})();
