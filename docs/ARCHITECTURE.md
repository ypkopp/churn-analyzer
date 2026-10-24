# A02 — Архитектурное решение и контракт Tethered

Статус: черновик архитектуры. Продукт НЕ реализован. Основано только на файлах
репозитория (`docs/case/Aeza_analys.pdf`, `data/raw/*`, `data-profile.json`),
не на сводках из чата.

## 1. Стек и граница

ASP.NET Core 10 + Razor Pages (панель) + JSON API (та же сборка) + PostgreSQL.
CSS variables — единственный источник цветов/отступов/типографики; JS modules
для живого обновления. **Один процесс приложения.** Без Redis, микросервисов,
ML-сервиса, очередей. Конкретных несовместимостей со стеком не выявлено.

```
┌─ одна сборка ASP.NET Core ─────────────────────────────┐
│ Razor Pages (UI)  ─┐                                   │
│ JSON API /api/v1/* ─┼─► Application services            │
│ GET /metrics        │      │                            │
│ GET /api/v1/docs ───┘      ▼                            │
│                    Risk engine (Tethered contract)      │
│                    Snapshot store + Model settings      │
│                    TimeProvider (System/Fake)           │
│                          │                              │
└──────────────────────────┼──────────────────────────────┘
                           ▼
                     PostgreSQL (source snapshot + settings)
```

- **Только интегратор меняет:** `Program.cs`, `global.json`, `*.csproj`,
  `compose.yaml`, все lockfiles, миграции схемы. Остальные роли возвращают
  точный integration-patch, а не правят эти файлы.
- Границы модулей: `Web/` (Razor + endpoints, без логики риска),
  `Application/` (оркестрация, DTO→модель маппинг), `Domain/` (risk engine —
  чистый, без I/O), `Infrastructure/` (PostgreSQL, persistence, TimeProvider impl).
  Domain не зависит от Infrastructure/Web.

## 2. Ключевые архитектурные решения

### 2.1 Деньги — целые единицы источника
Все денежные поля хранятся и передаются в **целых евроцентах** (как в источнике:
`bigint`/int, без float). EUR и форматирование — только в слое отображения.
Никогда не вычитать `balance` по-услужно; `bonusBalance` тратится только при
`bonusState='unlocked'`; `withdrawBalance` не считается авто-доступным.
Единица объявлена в контракте (`"unit":"EUR_cents"`), конвертация вне DTO.

### 2.2 UTC/asOf через TimeProvider
Все вычисления риска принимают явный `asOf` (UTC). В рабочем режиме —
`TimeProvider.System`; в тестах — `FakeTimeProvider`/`Microsoft.Extensions.TimeProvider.Testing`.
Никакого `DateTime.UtcNow` внутри Domain. Зафиксированный reference данных
`2026-10-19T15:00:00Z` — свойство снапшота, не замена часов.

### 2.3 Версии и консистентность UI
Каждый ответ оценки несёт `snapshotVersion`, `modelVersion`, `evaluatedAt`(=asOf).
UI не смешивает версии: клиент хранит `(snapshotVersion, modelVersion)` последнего
полного ответа; при polling, если пара изменилась — полная перерисовка, иначе
инкремент. Эндпоинт summary возвращает тот же кортеж; UI показывает баннер
«данные обновлены» при смене `snapshotVersion` и перезапрашивает, а не сливает.

### 2.4 Атомарная замена снимка и настройки после рестарта
`PUT /api/v1/source/snapshot` — валидация всего снапшота → замена в **одной
транзакции** (staging таблицы или `TRUNCATE`+insert в одном tx; при ошибке —
откат, старый снапшот остаётся). Новый `snapshotVersion` инкрементируется.
Настройки модели хранятся в PostgreSQL (таблица `model_settings` с версией),
переживают рестарт; `PATCH /api/v1/model/settings` инкрементирует `modelVersion`
и триггерит пересчёт.

### 2.5 Авторизация и защита
Cookie-auth (login → сессионная cookie). Все `/api/v1/*` клиентских данных и
панель требуют аутентификации. **CSRF**: cookie-auth + state-changing методы
(POST/PATCH/PUT/DELETE) — antiforgery token (SameSite=Strict + header-токен).
`/metrics` и `/api/v1/docs` — технические endpoints: защищаются отдельной политикой
(внутренняя сеть/отдельный порт или auth-профиль), не открыты анонимно.
OIDC — опциональный дополнительный провайдер, не заменяет базовую auth.

### 2.6 Live-update: polling
Стартовый механизм — периодический `GET /api/v1/analytics/summary` +
`GET /api/v1/customers` с текущим `(snapshotVersion, modelVersion)`. Интервал
конфигурируемый (`LiveUpdate:PollIntervalSeconds`, default ~5s). Ответ несёт
ETag/версии → при совпадении версий UI не перерисовывает. SSE/WebSocket —
возможное будущее усиление, не в базовом контракте.

## 3. DTO (по фактическим полям, без утечки сырых данных)

Исходные сущности маппятся в API-DTO. `email` возвращается только аутентифицированным
маркетологам (обязательное поле таблицы рейтинга по кейсу), в документации
помечается PII.

```jsonc
// POST /api/v1/source/snapshot — request
{
  "accounts":     [ { "id":0, "email":"", "balance":0, "withdrawBalance":0,
                      "totalReplenished":0, "bonusBalance":0,
                      "bonusState":"locked|unlocked" } ],
  "services":     [ { "id":0, "name":"", "ownerId":0, "paymentTerm":"hour|week|month|quarter_year|half_year|year",
                      "autoProlong":false, "createdAt":"date-time", "expiresAt":"date-time",
                      "status":"active|blocked|deleted|suspended|prolong_wait", "price":0 } ],
  "transactions": [ { "id":0, "amount":0, "status":"not_performed|performed",
                      "performedAt":"date-time|null", "createdAt":"date-time",
                      "type":"prolong|order", "serviceId":0 } ]
}
```

```jsonc
// GET /api/v1/customers — элемент рейтинга
{ "accountId":0, "email":"", "balance":0, "totalReplenished":0,
  "riskScore":0,            // индекс приоритета, НЕ вероятность
  "riskLevel":"high|medium|low|no_assessment",
  "reasons":["..."],        // человекочитаемые причины
  "snapshotVersion":0, "modelVersion":0, "evaluatedAt":"date-time" }

// GET /api/v1/customers/{id} — деталь
{ "account": {/* как выше */},
  "services":[{...}], "features":[{"name":"","value":"","contribution":0}],
  "explanations":["..."], "noAssessment":false,
  "snapshotVersion":0,"modelVersion":0,"evaluatedAt":"date-time" }

// GET /api/v1/analytics/summary
{ "countsByRisk":{"high":0,"medium":0,"low":0,"no_assessment":0},
  "money":{"unit":"EUR_cents","atRiskAmount":0,"balanceDeficit":0,"horizonDays":0,
           "semantic":"heuristic_money_index|..."},  // см. открытые решения A04
  "snapshotVersion":0,"modelVersion":0,"evaluatedAt":"date-time" }

// GET/PATCH /api/v1/model/settings
{ "modelVersion":0, "weights":{ "...":0 }, "thresholds":{ "...":0 },
  "horizonDays":0, "levels":["high","medium","low"] }
```

В DTO нет формулы риска и денежной формулы — они решение A04. `riskScore` —
непрозрачный индекс; `atRiskAmount` несёт явный `semantic`-тег (не «expected loss»).

## 4. Открытые решения для A04 (не решать здесь)

1. **Признаки**: какие сигналы (истечение без autoProlong, нехватка баланса под
   продления, prolong_wait, suspended) и их веса.
2. **Уровни**: пороги high/medium/low + обратная шкала лояльности.
3. **Лояльность**: как score маппится на «обратную лояльность» для панели.
4. **Отсутствие оценки**: критерий `no_assessment` (недостаток данных ≠ low).
5. **Денежный горизонт**: период продлений, считающийся «под риском».
6. **Распределение общего баланса**: правило покрытия балансом набора продлений
   (однократное распределение, не вычитание на услугу) и роль bonus/withdrawBalance.

## 5. Файлы артефакта A02

- `docs/ARCHITECTURE.md` — этот документ (решение + матрица + открытые решения).
- `docs/openapi-draft.yaml` — черновик OpenAPI (ниже).

Матрица требований — раздел ниже. Продукт не объявляется реализованным.

## 6. Матрица: требование → реализация → тест → доказательство

| # | Требование (PDF) | Реализация | Тест | Доказательство |
|---|---|---|---|---|
| 1 | Панель: score, уровень риска, причина, «нет оценки» при недостатке данных | Razor Page + `GET /customers` | UI/интеграц.: строка с reasons и `no_assessment` | скрин/тест-лог, код 200 |
| 2 | Таблица топа: ID, email, баланс, totalReplenished, score; сортировка обе стороны | `GET /customers?sort=&dir=` | параметризованный тест сортировки | ответы asc/desc |
| 3 | Настройка коэффициентов, сохранение после рестарта | `PATCH /model/settings` + таблица | тест: PATCH→рестарт→GET вернёт то же | лог пересчёта, persistence |
| 4 | Денежная оценка (услуги+балансы+риск) | `GET /analytics/summary.money` | юнит формулы + golden | объяснение смысла в UI |
| 5 | Живое обновление без reload | периодический re-eval (`Eval:RefreshSeconds`) → новый evaluationId при тех же версиях; UI polling по batch | тест: tick→новый evalId, версии те же | подтверждено A07 |
| 5a | Единая версия батча в списке/карточке/сводке | eval_runs + customer_results + summary_results одним tx | тест общего evalId/snapV/modelV | подтверждено A07 |
| 5b | Пересчёт после snapshot/settings | синхронный EvaluationService, publish-after-commit | тест смены коэффициента/снимка→новый evalId | подтверждено A07 |
| 5c | Optimistic concurrency настроек | expectedVersion→409 при mismatch | тест конфликта | подтверждено A07 |
| 5d | Перезапуск без смешивания версий | результаты в БД, не пересчёт заново | тест restart→тот же батч | подтверждено A07 |
| 6 | `/metrics` Prometheus | endpoint + агрегаты из опубликованного батча | scrape-тест формата | подтверждено A09 |
| 6a | Swagger UI `/api/v1/docs` | Swashbuckle UI + `/api/v1/docs/v1.json` | 200 + GET spec + CSRF-защищённый PATCH | подтверждено A09 |
| 7 | Расширяемость признаков/коэффициентов | реестр фич + версионируемые веса | добавление фичи без рефакторинга | diff+тест |
| 8 | Docker Compose | `compose.yaml` (app+postgres) | `docker compose up` | подтверждено A06 |
| 9 | Аутентификация, защита API | cookie-auth + [Authorize] | тест 401 без логина | подтверждено A06/A08 |
| 10 | `PUT /source/snapshot` атомарно | tx-замена + версия | тест отката при битом снапшоте | подтверждено A06/A07 |
| 11 | OpenAPI .yml | `docs/openapi-draft.yaml` | валидный yaml | обновлён A07 |
| 12 | CSRF для cookie-auth | antiforgery на mutating | тест без токена→403 | подтверждено A06/A08 |
| 13 | Защита `/metrics`,`/api/v1/docs` | отдельная authz-политика | тест анонима | 401/403 |
| 14 | Деньги в евроцентах, формат отделён | int+`unit` tag, формат в UI | юнит: нет float | подтверждено A05R |
| 15 | asOf через TimeProvider | System/Fake TimeProvider | юнит с фикс. временем | подтверждено A05/A07 |
| 16 | Согласованность версий в UI | evaluationId+версии в ответах | тест смешения версий | подтверждено A07/A08 |
| 17 | Перенос эталона UI | Razor + wwwroot, реальный api-adapter | браузерные проверки | подтверждено A08 |
| 18 | Live-update без reload | polling по evaluationId, без смешивания | tick→новый evalId | подтверждено A08 |
| 19 | Мобильная навигация Tethered | modal drawer + backdrop + scroll/focus lock + SVG edge | браузер 390/760/resize/reduced-motion | подтверждено A08R |
