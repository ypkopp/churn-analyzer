# Churn Risk Analyzer — фундамент (A06)

ASP.NET Core 10 + Razor Pages + JSON API + PostgreSQL, cookie-auth,
атомарное хранение снимка и настроек, Docker Compose. Один процесс, без
Redis/микросервисов.

## Требования
- .NET SDK 10.0.401+
- Docker Desktop с compose v2
- PostgreSQL (через compose)

## Файлы
- `compose.yaml` — app + postgres (БД не публикуется наружу)
- `Dockerfile` — multi-stage build/runtime
- `.env.example` — безопасные примеры, не секреты
- `.dockerignore` — исключает `.env`, `data/raw/`, `.git`

## Настройка
```bash
cp .env.example .env
# отредактируйте .env: задайте POSTGRES_PASSWORD и ADMIN_PASSWORD
```

## Запуск
```bash
docker compose up -d --build
```
Миграции применяются автоматически при старте приложения к пустой БД.
Первый admin-пользователь создаётся только если `Auth:AdminPassword` задан
и пользователя ещё нет — без универсального пароля по умолчанию.

## Проверка
- `GET /healthz/live` — процесс жив
- `GET /healthz/ready` — БД доступна
- `POST /api/v1/auth/login` + `GET /api/v1/auth/me` + `POST /api/v1/auth/logout`
- защищённые API без сессии → 401 (не HTML-редирект)
- UI: `/login` → `/customers` → карточка клиента → настройки → источник данных → выход

## API (A07/A08)
| Endpoint | Назначение |
|---|---|
| `GET /api/v1/customers?q=&riskLevel=&sort=&dir=` | рейтинг клиентов (score/level/reasons, batch identity) |
| `GET /api/v1/customers/{id}` | карточка: причины, вклады, услуги, денежный разбор |
| `GET /api/v1/analytics/summary` | сводка рисков и денежной оценки (та же оценочная версия) |
| `GET /api/v1/model/settings` | текущие коэффициенты + modelVersion |
| `PATCH /api/v1/model/settings` | `{expectedVersion, settings}` → 409 при конфликте, иначе пересчёт |
| `PUT /api/v1/source/snapshot` | атомарная замена снимка + пересчёт |
| `GET /api/v1/auth/csrf` | CSRF-токен для изменяющих запросов |
| `GET /api/v1/ui-config` | интервал polling UI (отдельно от пересчёта) |
| `GET /metrics` | защищённый Prometheus text exposition из последнего опубликованного батча |
| `/api/v1/docs` | защищённые Swagger UI и OpenAPI JSON (`/api/v1/docs/v1.json`) |

## Обновление UI без reload
Экран клиентов опрашивает сводку с интервалом `Ui:PollSeconds` (по умолчанию 15 с;
конфигурируется, независимо от `Eval:RefreshSeconds` пересчёта). Таблица и сводка
обновляются только при смене `evaluationId` — версии не смешиваются, поиск/фильтры/
фокус и открытая карточка не сбрасываются.

## Остановка
```bash
docker compose down          # останавливает, сохраняет volume
docker compose down -v       # удаляет volume (только для тестового окружения)
```

## Одноразовый bootstrap снимка (явный, не каждый рестарт)
Снимок импортируется один раз, только если `Bootstrap:SeedFile` задан и
снимка ещё нет в БД. Он НЕ перезаписывает существующий и не подставляет
дефолтные настройки поверх сохранённых.
```bash
# 1. Задайте в .env путь, который существует ВНУТРИ app-контейнера:
BOOTSTRAP_SEED_FILE=/seed/data.json
# 2. Запустите приложение (если файла ещё нет, импорт будет пропущен):
docker compose up -d
# 3. Скопируйте выбранный JSON в уже созданный контейнер:
docker compose cp data/raw/data.json app:/seed/data.json
# 4. Явно перезапустите app: при этом выполнится единственный импорт:
docker compose restart app
# После успешного импорта очистите BOOTSTRAP_SEED_FILE и перезапустите app.
```
Альтернатива для уже запущенного приложения — аутентифицированный
`PUT /api/v1/source/snapshot` с CSRF-токеном; замена атомарна.
Настройки сохраняются через `PATCH /api/v1/model/settings` и доступны через
`GET /api/v1/model/settings`.

## Безопасность
- cookie HttpOnly, SameSite=Strict, Secure в HTTPS-режиме
- CSRF-токен `X-CSRF-TOKEN` для изменяющих запросов
- rate-limit login (5/мин)
- пароль хешируется (PBKDF2, ASP.NET Core Identity)
- Data Protection keys — в volume `dpkeys` вне эфемерного контейнера

## A08R: мобильная навигация
На ширинах до 760px навигация — модальное меню с тем же градиентом и SVG-зубчатой
кромкой Tethered. Закрытие доступно повторным нажатием hamburger, кнопкой
«Закрыть меню» внутри панели, кликом по backdrop и Escape. При открытии фон
блокируется и прокрутка сохраняется; после закрытия фокус возвращается к hamburger.
Переход в desktop при открытом меню очищает scroll-lock и backdrop.

## Проверка браузером
Проверено на реальном `http://localhost:8080` с PostgreSQL:
- 390px и 760px: открыть/закрыть hamburger, close button, backdrop, Escape;
- 3 последовательных цикла открытия/закрытия;
- меню → «Настройки модели»: nav закрывается до открытия модалки;
- resize mobile→desktop→mobile: lock/backdrop очищаются, меню снова открывается;
- 1180/1440: nav видим, mobile close button скрыта;
- reduced-motion: motion-токены обнулены, управление работает;
- console errors и failed requests: 0.
Скриншот настоящего открытого мобильного меню: `design-reference/screenshots/a08r-mobile-open.png`.
## Ограничения прототипа
- нет Grafana и OIDC;
- расчёт остаётся эвристическим индексом: score не является вероятностью оттока;
- Swagger/OpenAPI и Prometheus endpoint требуют аутентификацию;
- браузерный integration suite требует доступного Docker Desktop с Linux engine.
