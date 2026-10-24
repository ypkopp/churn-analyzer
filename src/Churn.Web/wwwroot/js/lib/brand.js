/* =========================================================================
   Tethered — wordmark и компактный знак (D04).
   Рабочее название; уникальность не проверялась; бренд заказчика не имитируется.

   Wordmark не набран шрифтом: каждая буква построена моноширинной линией
   на сетке 32 (x-height 12→28, ascender 4/5, обводка 3, скруглённые концы).
   Характерная связка «th»: перекладина второй t не обрывается, а натянута
   от опорной точки до стойки h — это и есть «привязь» (tether).
   Компактный знак = та же связка «th», вырезанная из wordmark без изменений.

   Цвет: буквы — currentColor; привязь и опорная точка — var(--brand-tether),
   по умолчанию тоже currentColor (монохром).
   Доступное имя: role="img" + aria-label="Tethered" (+ <title>).
   ========================================================================= */
(function () {
  "use strict";
  const SW = 3;          // толщина линии
  const GAP = 4.5;       // межбуквенный интервал по геометрическим границам
  const PAD = 3;         // поле слева/справа (учитывает обводку и точку)

  /* Глифы: w — ширина по геометрии; d(x) — массив контуров */
  const G = {
    t: { w: 9,  d: (x) => [`M${x + 3.5} 5V23a5 5 0 0 0 5 5`, `M${x} 12H${x + 9}`] },
    e: { w: 16, d: (x) => [`M${x} 20H${x + 16}A8 8 0 1 0 ${x + 13.66} 25.66`] },
    h: { w: 13, d: (x) => [`M${x} 4V28`, `M${x} 19.5a6.5 6.5 0 0 1 13 0V28`] },
    r: { w: 8.5, d: (x) => [`M${x} 12V28`, `M${x} 19.5a7.5 7.5 0 0 1 7.5-7.5h1`] },
    d: { w: 16, d: (x) => [`M${x + 16} 4V28`, `M${x + 8} 12a8 8 0 1 0 0.01 0`] }
  };

  /* Сборка: возвращает { body, width }. Связка «th» — t без собственной
     перекладины + привязь от опорной точки до стойки h. */
  function build(word) {
    let x = PAD, parts = [], tether = "";
    for (let i = 0; i < word.length; i++) {
      const ch = word[i], g = G[ch];
      if (ch === "t" && word[i + 1] === "h") {
        if (i > 0) x += 1.5;                    // воздух для опорной точки после предыдущей буквы
        const hx = x + g.w + GAP;
        parts.push(`<path d="${g.d(x)[0]}"/>`);  // только стойка t
        tether = `<path class="br-tether" pathLength="1" d="M${x} 12H${hx}" stroke="var(--brand-tether, currentColor)"/>` +
                 `<circle class="br-anchor" cx="${x}" cy="12" r="2.4" fill="var(--brand-tether, currentColor)" stroke="none"/>`;
      } else {
        g.d(x).forEach((d) => parts.push(`<path d="${d}"/>`));
      }
      x += g.w + (i < word.length - 1 ? GAP : 0);
    }
    return { body: parts.join("") + tether, width: Math.ceil((x + PAD) * 10) / 10 };
  }

  const WORD = build("tethered");
  const MARK = build("th");
  const ATTR = `fill="none" stroke="currentColor" stroke-width="${SW}" stroke-linecap="round" stroke-linejoin="round"`;

  function svg(model, { height, cls, title = "Tethered", intro = false }) {
    const w = (model.width / 32) * height;
    return `<svg class="${cls}${intro ? " br--intro" : ""}" width="${+w.toFixed(1)}" height="${height}" viewBox="0 0 ${model.width} 32" role="img" aria-label="${title}"><title>${title}</title><g ${ATTR}>${model.body}</g></svg>`;
  }
  const Wordmark = (height = 24, opts = {}) => svg(WORD, { height, cls: "br-word", ...opts });
  const CompactMark = (size = 24, opts = {}) => {
    /* знак квадратный: центрируем связку th в поле 32×32 */
    const off = (32 - MARK.width) / 2;
    return `<svg class="br-mark${opts.intro ? " br--intro" : ""}" width="${size}" height="${size}" viewBox="${-off} 0 32 32" role="img" aria-label="${opts.title || "Tethered"}"><title>${opts.title || "Tethered"}</title><g ${ATTR}>${MARK.body}</g></svg>`;
  };

  /* Статичный SVG-файл (без CSS-переменных и классов) */
  function file(kind, color, tetherColor = color) {
    const s = kind === "mark" ? CompactMark(32) : Wordmark(32);
    return '<?xml version="1.0" encoding="UTF-8"?>\n' + s
      .replace("<svg ", '<svg xmlns="http://www.w3.org/2000/svg" ')
      .replace(/ class="[^"]*"/g, "").replace(/ pathLength="1"/g, "")
      .replaceAll("var(--brand-tether, currentColor)", tetherColor)
      .replaceAll("currentColor", color) + "\n";
  }

  window.TetheredBrand = { Wordmark, CompactMark, file, WORD_WIDTH: WORD.width, MARK_WIDTH: MARK.width };
})();
