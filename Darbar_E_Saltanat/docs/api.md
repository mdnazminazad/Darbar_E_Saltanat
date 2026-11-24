# REST API specification — `/api/v1`

Base URL in production: `https://<your-domain>/api/v1`. Interactive docs (non-production): `/api/docs`; machine-readable: `/api/openapi.json`.

## Conventions

* **Auth**: `Authorization: Bearer <access_token>` (users) or `X-Device-Id` + `X-Device-Token` (desktop devices).
* **Envelope**: success `{ "success": true, "data": …, "meta": { page, limit, total, total_pages } }`; error `{ "success": false, "error": { "code", "message", "details": [{ field, message }] } }`.
* **Status codes**: 200 OK · 201 Created · 400 Bad request · 401 Unauthenticated · 403 Forbidden · 404 Not found · 409 Conflict/duplicate · 422 Validation error · 423 Locked · 429 Rate limited · 500.
* **Lists**: `?page=1&limit=20` (max 100) `&sort=<field>&order=asc|desc&search=<text>` plus per-resource filters.
* **IDs**: always `uuid`. Money: decimal strings (`"450.00"`). Times: ISO-8601 UTC.
* **Rate limits**: 300 req/min/IP; auth endpoints 20 / 15 min.

Legend: ✅ implemented (Phase 1) · ⏳ phase in which it lands.

## Auth ✅
| Method | Path | Permission | Notes |
|---|---|---|---|
| POST | /auth/login | public | `{ username, password }` (username or email) → `{ access_token, refresh_token, expires_in, user{roles, permissions} }`. 5 failures → 423 for 15 min |
| POST | /auth/refresh | public | `{ refresh_token }` → new pair. Rotation; reuse of an old token revokes all sessions |
| POST | /auth/logout | user | `{ refresh_token? }` |
| GET | /auth/me | user | current user + permissions (`["*"]` for Super Admin) |
| POST | /auth/change-password | user | `{ current_password, new_password }`; signs out other sessions |

## Users ✅ (`users.manage`)
GET /users (`search, role, is_active`) · POST /users · GET /users/:uuid · PATCH /users/:uuid (`role_codes`, `pin`, `is_active`, …) · POST /users/:uuid/reset-password · DELETE /users/:uuid (soft). Last active Super Admin is protected.

## Roles & permissions ✅ (`roles.manage`)
GET /roles · POST /roles `{ code, name, description, permissions[] }` · GET/PATCH/DELETE /roles/:uuid (system roles cannot be deleted) · GET /permissions

## Branches ✅
GET /branches · GET /branches/:uuid (any user) · POST · PATCH · DELETE (`branches.manage`)

## Settings ✅
GET /settings/public (anonymous: name, address, phone, currency, VAT, service charge) · GET /settings?branch_uuid= (user) · PUT /settings `{ branch_uuid?, settings: [{ key, value, group? }] }` (`settings.manage`)

## Audit logs ✅ (`audit.view`)
GET /audit-logs `?action=<prefix>&entity_type&entity_uuid&from&to`

## Devices ✅
POST /devices/register (`sync.manage`) → `{ device_id, device_token }` (token shown once) · POST /devices/heartbeat (device) `{ pending_count, app_version }` · GET /devices (`sync.view`) · POST /devices/:uuid/revoke (`sync.manage`)

## System ✅
GET /health → `{ status, db, time, version }`

## Sync ✅ (Phase 3)
Device endpoints authenticate with `X-Device-Id` + `X-Device-Token`. Full protocol: `database/sync-design.md`.

| Method | Path | Auth | Notes |
|---|---|---|---|
| POST | /sync/push | device | `{ batch_id, changes[≤500]: [{ entity, operation, uuid, sync_version (base), updated_at, data }] }` → `{ batch_id, processed_at, results[], replayed? }`. Idempotent by `batch_id`; 409 while the same batch is being processed |
| GET | /sync/pull | device | `?since=ISO&cursor=&limit≤2000` → `{ changes: [{ entity, rows[] }], has_more, cursor, next_since, server_time }` (parents first) |
| POST | /sync/acknowledge | device | `{ pulled_until, pending_count? }` |
| GET | /sync/status | device or `sync.view` | device: own push/pull times and open conflicts · user: every device, open conflicts, failed batches (24 h), records synced (24 h) |
| GET | /sync/conflicts | `sync.view` | `?status=OPEN|…|ALL&entity=` — device version, server version at conflict time and current server version |
| POST | /sync/conflicts/:uuid/resolve | `sync.resolve` | `{ resolution: KEEP_SERVER|KEEP_DEVICE|MERGED, data?, note? }` — writes the winner as a new version (audited) |
| GET | /sync/batches | `sync.view` | `?failed_only=true&limit=` — batches with per-change problems |
| GET | /devices/me | device | device id, name, status, branch (used by the desktop when the token is entered) |

## Menu, tables, discounts, customers ✅ (Phase 2)
All list endpoints accept `?page&limit&sort&order&search` plus the filters shown. Writes are audited; deletes are soft.

| Resource | Endpoints | Permission (read / write) |
|---|---|---|
| Categories | GET/POST /categories (`is_active`) · GET/PATCH/DELETE /categories/:uuid — delete blocked while it has items | `menu.view` / `menu.manage` |
| Menu items | GET/POST /menu-items (`category_uuid, is_available, is_active, sku, barcode`) · GET/PATCH/DELETE /menu-items/:uuid · GET /menu-items/:uuid/full (variants + add-ons) · PUT /menu-items/:uuid/addons `{ addons:[{ addon_uuid, max_qty }] }` — SKU/barcode unique | `menu.view` / `menu.manage` |
| Variants | GET/POST /item-variants (`menu_item_uuid`) · GET/PATCH/DELETE /item-variants/:uuid — one default per item | `menu.view` / `menu.manage` |
| Add-ons | GET/POST /item-addons · GET/PATCH/DELETE /item-addons/:uuid | `menu.view` / `menu.manage` |
| Tables | GET/POST /tables (`status, section`) · GET/PATCH/DELETE /tables/:uuid · PATCH /tables/:uuid/status `{ status }` — delete blocked while occupied | `orders.view` / `tables.manage` |
| Preset discounts | GET/POST /discounts · GET/PATCH/DELETE /discounts/:uuid | `discounts.apply` / `coupons.manage` |
| Coupons | GET/POST /coupons · GET/PATCH/DELETE /coupons/:uuid · POST /coupons/validate `{ code, subtotal, customer_uuid? }` → `{ valid, reason?, discount_amount? }` | `coupons.manage` (validate: `discounts.apply`) |
| Customers | GET/POST /customers (`phone` — any BD format, normalised to 01XXXXXXXXX) · GET/PATCH/DELETE /customers/:uuid — phone unique | `customers.view` / `customers.manage` |
| Uploads | POST /uploads/images (multipart `image`, JPEG/PNG/WebP by magic bytes, ≤ `MAX_UPLOAD_MB`) → `{ url }` | `menu.manage` |
| Public | GET /public/menu — active categories, items and variants, no cost prices | anonymous |

Money in requests: number or string with ≤ 2 decimals; in responses always a string (`"450.00"`). Parents are referenced by `*_uuid`.

## Orders, payments, refunds — desktop-first
Orders are created and paid **on the desktop** (offline) and reach the server through `/sync/push`; other tills of the branch receive them via `/sync/pull`. Portal read endpoints arrive with the web order monitor (Phase 5): GET /orders (`from,to,status,type,table_uuid,customer_phone`) · GET /orders/:uuid (items, add-ons, KOTs, payments, refunds). Online ordering (Phase 5) adds POST /customer/orders.

## Phase 4 — Operations ⏳
Kitchen: GET /kitchen/queue · PATCH /kots/:uuid/status · PATCH /kot-items/:uuid/status (`kitchen.update`).
Inventory: CRUD /ingredients · GET /ingredients/low-stock · CRUD /recipes (+ /recipes/:uuid/ingredients) · GET/POST /stock-movements (`type: STOCK_IN|STOCK_OUT|WASTAGE`) · POST /stock-adjustments (`stock.adjust`).
Purchasing: CRUD /suppliers · CRUD /purchases · POST /purchases/:uuid/receive · POST /supplier-payments (`purchases.manage`, `suppliers.manage`).
Customers: CRUD /customers (`phone` search) · GET /customers/:uuid/orders · /addresses · /loyalty (`customers.manage`).
Staff: CRUD /employees, /shifts · POST /attendance/check-in, /check-out · GET /attendance · CRUD /salaries (+ advances) (`employees.manage`, `attendance.*`).
Accounts: POST /cash-sessions/open · POST /cash-sessions/:uuid/close · POST /cash-sessions/:uuid/movements · CRUD /expense-categories, /expenses (`cash.manage`, `expenses.manage`).

## Phase 5 — Web portal & customer site ⏳
Reports (`reports.view`): GET /reports/dashboard · /reports/sales?group_by=date|waiter|category|item|payment_method|order_type|employee · /reports/vat · /reports/profit-loss · /reports/stock-valuation · /reports/attendance — `?format=pdf|xlsx` with `reports.export`.
Reservations: CRUD /reservations (`reservations.manage`).
Public (customer): GET /public/menu · GET /public/categories · POST /public/reservations · POST /customer/auth/register · /login · /refresh · GET/POST /customer/orders · GET /customer/orders/:uuid/track · GET /customer/profile.
