# Darbar_E_Saltanat — Restaurant Management System

Offline-first restaurant POS and management platform for **Darbar_E_Saltanat** (Bangladesh · BDT ৳ · Asia/Dhaka).

* **Desktop POS** — Windows, C# .NET 8 WPF (MVVM), SQLite as the local source of truth. Works fully offline.
* **API** — Node.js 20 + Express + Sequelize, MySQL 8, JWT (users) + device tokens (desktops).
* **Web** — React 18 + Vite + Tailwind admin portal (customer website in Phase 5).
* **Sync** — transactional outbox on the desktop, idempotent batches and version-based conflict detection on the server.

| Document | What it covers |
|---|---|
| [docs/architecture.md](docs/architecture.md) | System design, decisions, write path, sync diagram, phase status |
| [database/sync-design.md](database/sync-design.md) | Sync protocol, outbox, push/pull algorithms, conflict policy |
| [docs/api.md](docs/api.md) | Full `/api/v1` endpoint specification (implemented ✅ vs. upcoming ⏳) |
| [docs/setup.md](docs/setup.md) | Local development setup |
| [docs/deployment.md](docs/deployment.md) | Deploying beside the existing PHP server (Nginx/Apache, PM2, TLS, backups) |
| [docs/user-manual.md](docs/user-manual.md) | How staff use Phase 1 features |

## Phase 1 — what is delivered

| Area | Delivered |
|---|---|
| Database | Generated MySQL 8 schema (52 tables incl. all required + KOTs, cash sessions, shifts, refresh tokens), SQLite schema with `sync_outbox`, seeds (super admin, 7 roles, 39 permissions, Main branch, 7 categories, 24 Bangladeshi dishes with variants/add-ons/recipes, 15 tables, 18 ingredients, settings) |
| API | Auth (login, lockout, rotating refresh tokens with reuse detection, logout, me, change password), users, roles & permissions, branches, settings (public + admin), audit logs, device registration + heartbeat, health, Swagger, Helmet, CORS, rate limiting, HTTPS enforcement, pino logging, global error handler, 15 passing integration tests |
| Web portal | Login, forced password change, royal-themed responsive layout, Overview, Users, Roles & Permissions matrix, Branches, Devices & Sync (one-time token), Settings, Audit Logs |
| Desktop | Clean-architecture solution; offline password + PIN login (bcrypt, lockout), RBAC session guard, auto-lock + unlock overlay, dashboard, settings (restaurant/tax/receipt/sync with DPAPI-encrypted token), sync status bar, Serilog, global exception handling, WAL + single-writer gate, atomic outbox in `SaveChanges`, xUnit tests |

## Phase 2 — Desktop POS (delivered)

| Area | Delivered |
|---|---|
| POS screen | Touch-friendly menu grid with category rail, search / SKU / barcode, open-order chips, cart with per-line status, totals panel, keyboard shortcuts (F6 KOT, F7 discount, F8 pay, F11 bill, Ctrl+N/D/H/U/P/F) |
| Orders | Dine-in / takeaway / delivery, per-device order numbers, variants, add-ons, kitchen notes, hold/resume, move table, merge, split bill, void (permission + reason), customer by phone |
| Money | `PricingEngine`: discounts (percent/cap, fixed, coupon, presets), service charge, per-item VAT (exclusive/inclusive, optional on service charge), whole-taka rounding, exact per-line allocation |
| Payments | Cash with change, card, bKash/Nagad/Rocket/Upay with TrxID, customer credit with limit, split tender, refunds (append-only, permission + reason) |
| Printing | ESC/POS for 58/80 mm, receipts, bills, KOTs per kitchen station, cancellation tickets, cash drawer kick, Windows RAW spooler, on-screen preview fallback, printer test |
| Tables & menu | Live floor plan with statuses; menu management with variants, add-ons, availability toggle, CSV import/export |
| Dashboard | Net sales, orders, average order, payment mix, top items, open orders, pending KOTs, VAT/service charge — today / 7 / 30 days |
| Server API | Registry-driven CRUD for categories, menu items, variants, add-ons, tables, discounts, coupons (+ validate), customers; image upload; public menu; 26 integration tests |

## Phase 3 — Offline sync (delivered)

| Area | Delivered |
|---|---|
| Server | `/sync/push` (idempotent batches, one transaction + savepoint per change, version-based conflict detection, append-only ledgers immutable, missing parents retryable, branch/RBAC protection), `/sync/pull` (incremental, parents first, keyset-paged cursor, deletes replicated, secrets excluded), `/sync/acknowledge`, `/sync/status`, `/sync/conflicts` + resolve (keep server / keep device / merged), `/sync/batches`, `/devices/me` |
| Desktop | `SyncEngine` + `SqliteSyncStore` + `HttpSyncApi` + `SyncWorker`: batches of the outbox, resume of interrupted batches with the same id, per-change and per-run exponential back-off, immediate sync on reconnect / Sync now, generic schema-safe pull apply, conflict freeze until an admin decides, settings refresh on pull, heartbeat |
| Dashboards | Desktop status bar + Sync page (counts, problems, log, Sync now, Retry failed); portal Devices & Sync with Devices, Conflicts (field diff, resolve) and Failed changes; Overview shows open conflicts |
| Tooling | `npm run dev:sqlite` — the API on in-memory SQLite with the production seed and a registered device |
| Tests | 13 server sync tests (39 server tests total); desktop `SyncEngineTests` (real SQLite store); compiled end-to-end runs of the desktop engine against the live API |

## Run the web portal locally (Windows, no MySQL)
Install Node.js 20+ and double-click **START-WEB.bat**. It installs packages on first run, starts the API on in-memory SQLite with the production seed (port 4000) and the portal (http://localhost:5173), and opens the browser. Login `admin / ChangeMe@123`. Data resets when the API window is closed.

## Quick start
```bash
cd web/server && cp .env.example .env && npm install && npm run migrate -- --seed && npm run dev
cd web/client && npm install && npm run dev            # http://localhost:5173  (admin / ChangeMe@123)
cd desktop && dotnet test && dotnet run --project DarbarESaltanat.Desktop   # Windows
```

## Folder structure
```
Darbar_E_Saltanat/
├── database/
│   ├── tools/
│   │   ├── gen_schema.py
│   │   ├── schema_spec.py
│   │   └── seed_data.py
│   ├── mysql-schema.sql
│   ├── mysql-seed.sql
│   ├── sqlite-schema.sql
│   ├── sqlite-seed.sql
│   └── sync-design.md
├── desktop/
│   ├── DarbarESaltanat.Application/
│   │   ├── Abstractions/
│   │   │   ├── Interfaces.cs
│   │   │   └── UnitOfWork.cs
│   │   ├── Auth/
│   │   │   ├── AuthService.cs
│   │   │   └── Session.cs
│   │   ├── Common/
│   │   │   ├── AuditFactory.cs
│   │   │   ├── Money.cs
│   │   │   └── Result.cs
│   │   ├── Customers/
│   │   │   └── CustomerService.cs
│   │   ├── Dashboard/
│   │   │   └── DashboardService.cs
│   │   ├── Menu/
│   │   │   ├── Csv.cs
│   │   │   └── MenuService.cs
│   │   ├── Pos/
│   │   │   ├── DiscountRules.cs
│   │   │   ├── OrderModels.cs
│   │   │   ├── OrderService.Flow.cs
│   │   │   ├── OrderService.Items.cs
│   │   │   ├── OrderService.cs
│   │   │   ├── PaymentRules.cs
│   │   │   └── PricingEngine.cs
│   │   ├── Printing/
│   │   │   ├── EscPos.cs
│   │   │   ├── PrintModels.cs
│   │   │   └── ReceiptFormatter.cs
│   │   ├── Settings/
│   │   │   └── SettingsService.cs
│   │   ├── Sync/
│   │   │   ├── SyncContracts.cs
│   │   │   ├── SyncEngine.cs
│   │   │   └── SyncPayload.cs
│   │   ├── Tables/
│   │   │   └── TableService.cs
│   │   ├── DarbarESaltanat.Application.csproj
│   │   └── DependencyInjection.cs
│   ├── DarbarESaltanat.Desktop/
│   │   ├── Services/
│   │   │   ├── DialogService.cs
│   │   │   ├── InactivityMonitor.cs
│   │   │   ├── Navigator.cs
│   │   │   ├── PrintService.cs
│   │   │   ├── RawPrinter.cs
│   │   │   ├── SyncStatusMonitor.cs
│   │   │   └── WindowCoordinator.cs
│   │   ├── Themes/
│   │   │   └── Royal.xaml
│   │   ├── ViewModels/
│   │   │   ├── DashboardViewModel.cs
│   │   │   ├── LoginViewModel.cs
│   │   │   ├── MainViewModel.cs
│   │   │   ├── MenuManagementViewModel.cs
│   │   │   ├── OrdersViewModel.cs
│   │   │   ├── PosViewModel.cs
│   │   │   ├── SettingsViewModel.cs
│   │   │   ├── SyncViewModel.cs
│   │   │   └── TablesViewModel.cs
│   │   ├── Views/
│   │   │   ├── CodeDialog.cs
│   │   │   ├── DashboardView.xaml
│   │   │   ├── DashboardView.xaml.cs
│   │   │   ├── LoginWindow.xaml
│   │   │   ├── LoginWindow.xaml.cs
│   │   │   ├── MainWindow.xaml
│   │   │   ├── MainWindow.xaml.cs
│   │   │   ├── MenuManagementView.xaml
│   │   │   ├── MenuManagementView.xaml.cs
│   │   │   ├── OrdersView.xaml
│   │   │   ├── OrdersView.xaml.cs
│   │   │   ├── PosDialogs.cs
│   │   │   ├── PosView.xaml
│   │   │   ├── PosView.xaml.cs
│   │   │   ├── SettingsView.xaml
│   │   │   ├── SettingsView.xaml.cs
│   │   │   ├── SyncView.xaml
│   │   │   ├── SyncView.xaml.cs
│   │   │   ├── TablesView.xaml
│   │   │   └── TablesView.xaml.cs
│   │   ├── App.xaml
│   │   ├── App.xaml.cs
│   │   ├── Converters.cs
│   │   ├── DarbarESaltanat.Desktop.csproj
│   │   └── app.manifest
│   ├── DarbarESaltanat.Domain/
│   │   ├── Common/
│   │   │   └── SyncEntity.cs
│   │   ├── Entities/
│   │   │   ├── Identity.cs
│   │   │   ├── Pos.cs
│   │   │   └── SyncTables.cs
│   │   ├── Enums/
│   │   │   ├── PosEnums.cs
│   │   │   └── SyncEnums.cs
│   │   ├── Security/
│   │   │   ├── Permissions.cs
│   │   │   └── RoleCodes.cs
│   │   └── DarbarESaltanat.Domain.csproj
│   ├── DarbarESaltanat.Infrastructure/
│   │   ├── Persistence/
│   │   │   ├── AppDbContext.cs
│   │   │   ├── DatabaseInitializer.cs
│   │   │   ├── DeviceContext.cs
│   │   │   ├── EfUnitOfWork.cs
│   │   │   ├── NamingAndConverters.cs
│   │   │   ├── SqlitePragmaInterceptor.cs
│   │   │   └── SqliteWriteGate.cs
│   │   ├── Repositories/
│   │   │   └── Repositories.cs
│   │   ├── Security/
│   │   │   └── Security.cs
│   │   ├── Sync/
│   │   │   ├── HttpSyncApi.cs
│   │   │   ├── SqliteSyncStore.cs
│   │   │   └── SyncWorker.cs
│   │   ├── DarbarESaltanat.Infrastructure.csproj
│   │   └── DependencyInjection.cs
│   ├── DarbarESaltanat.Tests/
│   │   ├── AuthAndSettingsTests.cs
│   │   ├── DarbarESaltanat.Tests.csproj
│   │   ├── MenuServiceTests.cs
│   │   ├── OrderServiceTests.cs
│   │   ├── PosFixture.cs
│   │   ├── PricingAndRulesTests.cs
│   │   ├── SyncEngineTests.cs
│   │   ├── SyncOutboxTests.cs
│   │   └── TestDb.cs
│   ├── DarbarESaltanat.sln
│   └── Directory.Build.props
├── docs/
│   ├── api.md
│   ├── architecture.md
│   ├── deployment.md
│   ├── setup.md
│   └── user-manual.md
├── web/
│   ├── client/
│   │   ├── public/
│   │   │   └── favicon.svg
│   │   ├── src/
│   │   │   ├── api/
│   │   │   │   └── client.js
│   │   │   ├── auth/
│   │   │   │   └── AuthContext.jsx
│   │   │   ├── components/
│   │   │   │   ├── Toast.jsx
│   │   │   │   ├── ui.jsx
│   │   │   │   └── useApi.js
│   │   │   ├── layouts/
│   │   │   │   └── AdminLayout.jsx
│   │   │   ├── pages/
│   │   │   │   ├── admin/
│   │   │   │   │   ├── AuditLogs.jsx
│   │   │   │   │   ├── Branches.jsx
│   │   │   │   │   ├── Devices.jsx
│   │   │   │   │   ├── Overview.jsx
│   │   │   │   │   ├── Roles.jsx
│   │   │   │   │   ├── Settings.jsx
│   │   │   │   │   ├── Sync.jsx
│   │   │   │   │   └── Users.jsx
│   │   │   │   ├── ChangePassword.jsx
│   │   │   │   └── Login.jsx
│   │   │   ├── App.jsx
│   │   │   ├── index.css
│   │   │   └── main.jsx
│   │   ├── index.html
│   │   ├── package-lock.json
│   │   ├── package.json
│   │   ├── postcss.config.js
│   │   ├── tailwind.config.js
│   │   └── vite.config.js
│   └── server/
│       ├── scripts/
│       │   └── dev-sqlite.mjs
│       ├── src/
│       │   ├── config/
│       │   │   └── env.js
│       │   ├── db/
│       │   │   ├── models/
│       │   │   │   ├── index.js
│       │   │   │   ├── registryModels.js
│       │   │   │   └── syncModels.js
│       │   │   ├── migrate.js
│       │   │   └── sequelize.js
│       │   ├── middleware/
│       │   │   ├── auth.js
│       │   │   ├── deviceAuth.js
│       │   │   ├── errorHandler.js
│       │   │   ├── security.js
│       │   │   └── validate.js
│       │   ├── modules/
│       │   │   ├── auth/
│       │   │   │   ├── auth.routes.js
│       │   │   │   └── auth.service.js
│       │   │   ├── catalog/
│       │   │   │   ├── catalog.routes.js
│       │   │   │   ├── couponRules.js
│       │   │   │   └── schemas.js
│       │   │   ├── crud/
│       │   │   │   ├── crudRouter.js
│       │   │   │   └── serializer.js
│       │   │   ├── roles/
│       │   │   │   └── roles.routes.js
│       │   │   ├── users/
│       │   │   │   ├── users.routes.js
│       │   │   │   ├── users.serializer.js
│       │   │   │   └── users.service.js
│       │   │   ├── audit.service.js
│       │   │   ├── auditLogs.routes.js
│       │   │   ├── branches.routes.js
│       │   │   ├── devices.routes.js
│       │   │   ├── settings.routes.js
│       │   │   ├── sync.routes.js
│       │   │   └── uploads.routes.js
│       │   ├── security/
│       │   │   ├── passwords.js
│       │   │   ├── permissionService.js
│       │   │   ├── permissions.js
│       │   │   └── tokens.js
│       │   ├── sync/
│       │   │   ├── conflicts.service.js
│       │   │   ├── entityRegistry.json
│       │   │   ├── pull.service.js
│       │   │   ├── push.service.js
│       │   │   └── wire.js
│       │   ├── utils/
│       │   │   ├── errors.js
│       │   │   └── http.js
│       │   ├── app.js
│       │   ├── logger.js
│       │   ├── openapi.js
│       │   └── server.js
│       ├── tests/
│       │   ├── auth.test.js
│       │   ├── catalog.test.js
│       │   ├── helpers.js
│       │   ├── rbac.test.js
│       │   ├── setup-env.js
│       │   └── sync.test.js
│       ├── .env.example
│       ├── package-lock.json
│       ├── package.json
│       └── vitest.config.js
├── .env.example
├── .gitignore
└── README.md
```

## Next: Phase 4 — Operations
Kitchen display system (LAN), inventory with recipe-based stock deduction at order completion, purchases and receiving, suppliers, customers and loyalty, employees, shifts and attendance.
