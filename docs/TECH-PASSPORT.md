# TECH-PASSPORT — Tethered

Редактируемый источник техпаспорта. Знания зафиксированы на HEAD `dev`
`275ba06` (2026-10-25); на момент записи `main` = `5a5d0bd`. Записано **что
проверено и как** — не заявляет непроведённого.

## Стек
.NET 10 / ASP.NET Core · Razor Pages + JSON API · vanilla JS + CSS tokens ·
PostgreSQL 17 · Docker Compose. Prometheus+Grafana — опц. профиль
`compose.observability.yaml`. Без Redis/брокера/React/K8s.

## Что реализовано (по коду)
- Панель score/уровень/причины/no_assessment; рейтинг ID·email·баланс·
  пополнения с сортировкой в обе стороны; «без оценки» в конце.
- Модель: веса 30/25/40/10, denominator 105, пороги medium 20 / high 50;
  raw по услугам суммируется (`long`), score = clamp(round(100·raw/105),0,100).
- Деньги в EUR cents: одно ближайшее материализованное продление на услугу;
  фонд = balance + (bonusBalance если unlocked); withdrawBalance не тратится;
  `atRisk = round(renewalCost·score/100)`, `no_assessment` → null, не 0.
- Каждый результат связан с evaluationId/asOf/snapshotVersion/modelVersion;
  авто-пересчёт по `Eval:RefreshSeconds` без reload UI.
- Cookie-auth HttpOnly+SameSite=Strict, CSRF-токен, security-stamp отзыв
  сессий (смена пароля/отключение → старая cookie отклоняется).
- `/metrics`, `/api/v1/docs` (Swagger) — только за auth.
- AI-brief опционально: byte-limit, no-redirect, общий deadline на весь
  ответ, stateful UTF-8 декодер, bounded `_locks` (256), TTL-кэш, реальный
  `cached` флаг.
- Next-actions: причина сопоставляется с услугами через service-id из
  contributions батча (не просто по горизонту); max 3 пункта + `<details>`;
  service-link — реальный scroll/focus.
- Задачи на удержание, сообщения (черновик/mailto/send), сегменты,
  CSV-export на клиенте, загрузка снимка через UI (admin).

## Лимиты (фактические)
- `login` — **общий** fixed-window, `RateLimit:LoginPermit` (default 5/мин),
  без ключа по пользователю/IP; `429` общий.
- `analytics` (what-if, ai/brief) — отдельный лимитер 12/мин, ключ по логину
  авторизованного пользователя (IP при анониме).

## API (обязательные)
`POST /auth/login|logout`, `GET /auth/me`, `GET /customers`, `GET /customers/{id}`,
`GET /analytics/summary`, `GET/PATCH /model/settings`, `PUT /source/snapshot`,
`GET /metrics`, `GET /api/v1/docs`. Плюс: renewals, compare, whatif, tasks,
messages, segments, users, ai/brief, rotate-password, csrf.

## Проверено
| Что | Как | Статус |
|---|---|---|
| `dotnet build` (Core+Web+Tests) | компиляция | ✅ 0 err / 0 warn |
| `Churn.Core.Tests` | `dotnet test` | ✅ 69/69 (вкл. regression на raw-overflow) |
| next-actions reproducer | node | ✅ услуга вне горизонта не возвращается |
| baseline 65/105→62, renewal500→310 | core-тесты A05R | ✅ |
| Интеграция (Testcontainers+PG) | `Churn.IntegrationTests` | ⚠ не запускалась — Docker недоступен в этой среде |
| HTTP: overflow→400, AI timeout/UTF8/concurrency | — | ⚠ по коду, без прогона |
| Live-стенд (UI 390/1440, keyboards) | — | ⚠ заявлено ранее на main, не перепроверено на 275ba06 |

## Ограничения
Score — индекс приоритета, не вероятность; accuracy/AUC не заявляются.
OIDC и реальный SMTP — не реализованы (отложены). История удалений в данных
отсутствует — score не измерен на истории.
