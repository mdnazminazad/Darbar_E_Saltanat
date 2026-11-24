# Darbar_E_Saltanat — Architecture

Restaurant management system for **Darbar_E_Saltanat** (Bangladesh · BDT ৳ · Asia/Dhaka).
Offline-first Windows POS, a Node.js REST API on the existing server, MySQL 8, and a React web portal + customer site.

## 1. System context

```
                         ┌──────────────────────────── Existing PHP server (Linux) ─────────────────────────────┐
                         │                                                                                       │
 Customers (mobile/web) ─┼─► Nginx/Apache :443 ──┬── /            → React build (customer site + /admin portal)  │
 Managers (browser)  ────┼─►   (TLS, HSTS)       ├── /api/v1/*    → Node.js 20 Express API (:4000, PM2)          │
                         │                       └── /uploads/*   → static images (menu photos)                  │
                         │                                             │                                         │
                         │                                             ▼                                         │
                         │                                     MySQL 8 (InnoDB, utf8mb4)                         │
                         └───────────────────────────────────────────────────────────────────────────────────────┘
                                                   ▲  HTTPS only (JWT for users, device token for sync)
                                                   │
         ┌──────────────── Restaurant LAN (works with NO internet) ─────────────────┐
         │  Counter PC (WPF, .NET 8)  ──► local SQLite (WAL)  ◄── source of truth  │
         │     │  sync worker (background) ── push outbox / pull changes ──────────┼──► API
         │     ├── 58/80 mm thermal printer, cash drawer (ESC/POS)                  │
         │     └── Kitchen display (Phase 4: LAN KDS served by the counter PC)      │
         └──────────────────────────────────────────────────────────────────────────┘
```

## 2. Key decisions

| Decision | Choice | Why |
|---|---|---|
| Desktop source of truth | SQLite (WAL, single writer) | POS must never wait for the internet. |
| Desktop → server | Only via REST API (`/api/v1/sync/*`) | Never expose MySQL to shop PCs; central validation and auth. |
| Identifiers | `uuid` everywhere on the wire; MySQL keeps BIGINT `id` internally; SQLite `id` is local-only | Offline devices can create records without collisions. |
| Relations on desktop | `<parent>_uuid` columns referencing parent `uuid` | Payloads are self-contained; server maps uuid → id. |
| Atomicity | Business row + `sync_outbox` row in **one** SQLite transaction (EF `SaveChanges` override) | No change can be saved without being queued, or queued without being saved. |
| Deletes | Soft (`deleted_at`) everywhere | Deletes replicate; history kept for audits. |
| Financial / inventory | Append-only ledgers (`payments`, `refunds`, `stock_movements`, `cash_movements`, …) | Two devices can never overwrite each other's money/stock; conflicts impossible by construction. |
| Conflicts | Optimistic concurrency on `sync_version` | Never silently overwrite; admin resolves in portal. |
| ORM (server) | Sequelize (used consistently) + versioned SQL migrations | Schema is authored once (generator) and applied as SQL; Sequelize maps models. |
| ORM (desktop) | EF Core (SQLite); Dapper allowed for reports | |
| Auth | Users: JWT access (15 min) + rotating refresh tokens (hashed, reuse detection). Devices: device id + token (bcrypt-hashed server-side, DPAPI-encrypted on the PC) | Separate credentials for people and machines. |
| Passwords/PINs | bcrypt everywhere (bcryptjs on server, BCrypt.Net on desktop, same hash format) | Synced users can log in offline. |
| Time | UTC in both databases; UI converts to Asia/Dhaka | |
| Money | `DECIMAL(12,2)` MySQL; exact decimal TEXT in SQLite; C# `decimal` | No floating point in money. |
| Multi-branch | `branch_id` on every syncable row; chain-wide rows have NULL branch | Ready for more branches without schema change. |

## 3. Single source of truth for the schema

`database/tools/schema_spec.py` defines every table once. `gen_schema.py` generates:

* `database/mysql-schema.sql` — MySQL 8 DDL (FKs, indexes, sync columns)
* `database/sqlite-schema.sql` — desktop DDL (uuid-based FKs, CHECK constraints, outbox tables)
* `web/server/src/sync/entityRegistry.json` — metadata the generic sync engine uses (columns, FKs, append-only / financial flags)

`seed_data.py` generates both seeds plus the permission catalogs for Node (`permissions.js`) and C# (`Permissions.cs`) so roles/permissions can never drift between systems.

## 4. Desktop (C# .NET 8, WPF + MVVM)

```
DarbarESaltanat.Desktop         WPF views, view-models (CommunityToolkit.Mvvm), DI host, Serilog, global exception handling,
                                 inactivity auto-lock, status bar sync monitor, keyboard shortcuts
DarbarESaltanat.Application     Services (AuthService, SettingsService, …), DTOs, FluentValidation validators,
                                 ICurrentSession permission guard, Result types — no UI, no EF
DarbarESaltanat.Domain          Entities (SyncEntity base), enums, permission/role constants
DarbarESaltanat.Infrastructure  AppDbContext (outbox + soft delete + UTC text), repositories, DatabaseInitializer,
                                 WAL pragmas, single-writer gate, bcrypt hasher, DPAPI secret protector, (Phase 3) sync client
DarbarESaltanat.Tests           xUnit against real in-memory SQLite created from the production schema + seed
```

Write path (every mutation):

```
User action → ViewModel → Application service
   ├─ ICurrentSession.Require(permission)            (throws PermissionDeniedException → friendly dialog)
   ├─ FluentValidation                                (Result.Fail → message in UI)
   └─ Repository → AppDbContext.SaveChangesAsync
          ├─ acquire SqliteWriteGate (one writer)
          ├─ stamp created_at/updated_at/device_id, sync_status=PENDING, soft-delete conversion
          ├─ add/coalesce sync_outbox row (payload_json, base_version)
          └─ COMMIT (one SQLite transaction) → UI refreshes from SQLite
```

No transaction is ever held across a network call; the sync worker reads a batch, releases the DB, calls the API, then opens a new short transaction to record results.

## 5. Server (Node.js 20, Express)

```
src/
  config/env.js            zod-validated environment (fails fast)
  logger.js                pino (secrets redacted)
  db/                      sequelize connection, models, migrate.js (applies SQL migrations)
  middleware/              auth (JWT), authorize(permission), deviceAuth, validate(zod), security (HTTPS, rate limits), errorHandler
  security/                passwords (bcrypt), tokens (JWT + opaque refresh), permissionService (cached RBAC), permissions.js (generated)
  modules/                 auth, users, roles, branches, settings, audit logs, devices  (+ later phases)
  sync/                    entityRegistry.json (generated) → generic push/pull engine in Phase 3
  openapi.js               Swagger/OpenAPI served at /api/docs (non-production) and /api/openapi.json
```

Response envelope: `{ success: true, data, meta? }` or `{ success: false, error: { code, message, details? } }`.

## 6. Sync architecture (text diagram)

```
 DESKTOP (SQLite)                                                     SERVER (Node + MySQL)
 ───────────────                                                      ─────────────────────
 business tables ──┐ same txn
 sync_outbox  ◄────┘
     │ every 30 s while online, immediately on reconnect,
     │ exponential backoff on failure (no tight loops)
     ▼
 [PENDING rows] ── mark SYNCING + batch_id ──► POST /api/v1/sync/push { batch_id, device_id, changes[] }
                                                   │
                                                   ├─ deviceAuth (X-Device-Id / token)
                                                   ├─ sync_batches.batch_id exists?  → return stored response (idempotent)
                                                   ├─ per change, in a MySQL transaction:
                                                   │     resolve *_uuid → ids
                                                   │     CREATE: uuid exists? identical → SYNCED (no dup) : CONFLICT
                                                   │     UPDATE/DELETE: server.sync_version == base_version ?
                                                   │         apply, sync_version+1, updated_at=server now → SYNCED
                                                   │       : → sync_conflicts row, status CONFLICT (never overwrite money/stock)
                                                   ├─ sync_records rows; sync_batches.response_json stored
 ◄──────────────── results[] { uuid, status, server_version | reason } ┘
 SYNCED   → outbox SYNCED, row.sync_version = server_version, row.sync_status = SYNCED
 CONFLICT → outbox CONFLICT, row.sync_status = CONFLICT, sync_conflicts_local; status bar shows "Conflicts found"
 FAILED   → retry_count++, next_attempt_at = now + min(2^n · 15 s, 30 min)

 GET /api/v1/sync/pull?since=<server cursor>&device_id=…   (menu, users, roles, settings, coupons, online orders,
     │                                                       reservations, customers, web inventory adjustments)
     ▼
 AppDbContext.ApplyingRemoteChanges = true  → upsert by uuid, rows SYNCED, nothing re-queued
 (a local PENDING edit of the same row is kept and flagged CONFLICT instead of being overwritten)
 sync_state.last_pull_at = response.server_time   ─►  POST /api/v1/sync/acknowledge
```

Full protocol: `database/sync-design.md`.

Components: `SyncEngine` (Application — pure orchestration, unit-tested), `ISyncStore` → `SqliteSyncStore` (Infrastructure — short write-gated transactions, schema-whitelisted SQL), `ISyncApi` → `HttpSyncApi` (HTTPS only, error classes: offline / not authorised / rejected), `SyncWorker` (hosted background service: interval, back-off, network-reconnect and manual triggers), `SyncStatus` → status bar and Sync page. The wire format of a row is produced in exactly one place, `SyncPayload.Snapshot`, used by both the outbox and the engine; the server mirror is `web/server/src/sync/wire.js`.

## 7. POS design (Phase 2)

```
PosViewModel ──► OrderService (Application)            one operation = one unit of work = one SQLite transaction
                   │  Require(permission)
                   │  load order + items + add-ons + payments + refunds
                   │  mutate (add item, KOT, discount, pay, void…)
                   │  PricingEngine.Calculate(...)       pure function, unit-tested
                   │  guard (e.g. new total < paid → refuse)
                   │  CommitAsync  ── rows + audit + sync_outbox, atomically
                   └► PosOutcome { view, print jobs, open drawer? }
PosViewModel ──► IPrintService (desktop)                 AFTER commit: ESC/POS bytes → Windows spooler (RAW),
                                                          per-station kitchen printers, or on-screen preview
```

* **Money rules** (`PricingEngine`): line = (price + add-ons per unit) × qty − line discount; order discount (percent with cap / fixed ≤ subtotal / coupon); service charge on the discounted subtotal (dine-in only by default); VAT per line at the item rate (0 = restaurant rate), optionally on the service charge, exclusive or inclusive; optional rounding to whole taka with a visible round-off. Discount and service charge are allocated to lines to the paisa, so line figures always add up to the order.
* **Order numbers** `ORD-<device code>-<yyMMdd Dhaka>-<seq>` are unique per device, so two offline tills never collide.
* **Tables**: one active order per table (split bills may share it); merge links extra tables through `order_tables`; completion sets tables to *Cleaning*, void/transfer to *Available*.
* **Kitchen**: each `Send to kitchen` creates one KOT per station (category `kitchen_station`); cancelling a sent item needs `orders.void` + reason and prints a cancellation ticket.
* **Payments** are append-only; split tender = several payment rows; cash change is computed from *tendered*; mobile banking requires provider + TrxID; credit checks the customer's limit. Refunds are separate append-only rows and need `payments.refund` + reason. An order with money still held cannot be voided.
* **Coupons**: validated locally (dates, minimum, usage limit incl. unsynced local uses, per-customer limit); usage recorded in append-only `coupon_usages` when the order completes. `coupons.used_count` is maintained by the server only.
* Recipe-based stock deduction plugs into `OrderService.CompleteAsync` in Phase 4 (same transaction).

## 8. Security summary

* HTTPS only (TLS at Nginx; API rejects non-HTTPS when `REQUIRE_HTTPS=true`; HSTS via Helmet).
* bcrypt (cost 12) for passwords, PINs and device tokens; refresh tokens stored as SHA-256; access JWT HS256 15 min.
* Account lockout (5 failures → 15 min) on server and desktop; generic credential errors; timing-equalised unknown-user path.
* Rate limits: 300 req/min/IP on API, 20 per 15 min on auth endpoints.
* RBAC on every route (`authorize('perm')`) and every desktop service (`ICurrentSession.Require`); Super Admin cannot be removed if last.
* Audit log for logins, failed logins, lockouts, user/role/settings/branch/device changes (append-only, synced).
* Desktop device token encrypted with Windows DPAPI; logs redact secrets.

## 9. Phase plan and status

| Phase | Scope | Status |
|---|---|---|
| 1 Foundation | Schemas, auth, RBAC, settings, layouts, logging, errors, devices register/heartbeat | **Delivered** |
| 2 Desktop POS | Menu, categories, tables, orders, cart, discounts, VAT/service charge, payments, receipt + KOT printing | **Delivered** |
| 3 Offline sync | Sync worker, push/pull/ack/status/conflicts endpoints, idempotency, retries, sync dashboard | **Delivered** |
| 4 Operations | KDS, inventory, recipes, purchases, suppliers, customers, employees, attendance | Next |
| 5 Web portal | Dashboards, reports, menu/order mgmt, customer website, online ordering, reservations | Planned |
| 6 Reports & polish | Sales/inventory/accounting reports, PDF/Excel, backup/restore, perf, security review | Planned |
