# Offline-first synchronisation design

## 1. Guarantees

1. **The POS never waits for the network.** Every write commits to SQLite first.
2. **Nothing is lost.** The business row and its `sync_outbox` entry are written in the same SQLite transaction.
3. **Nothing is duplicated.** Records are keyed by client-generated UUIDs and batches by client-generated `batch_id`; replaying either is a no-op.
4. **Nothing is silently overwritten.** Concurrent edits are detected with `sync_version`; financial and inventory records are append-only or never auto-merged.
5. **Eventually consistent.** Pull sync brings server/web changes down incrementally.

## 2. Columns on every syncable table

| Logical column | MySQL | SQLite / wire | Meaning |
|---|---|---|---|
| id | `id BIGINT UNSIGNED` | `id INTEGER` | **Local only.** Never sent, never compared. |
| uuid | `uuid CHAR(36)` | `uuid` | Global identity. |
| branch_id | `branch_id → branches.id` | `branch_uuid` | NULL for chain-wide rows (roles, users, settings defaults, customers). |
| FKs | `<name>_id` | `<name>_uuid` | Server resolves uuid → id on push and id → uuid on pull. |
| created_at / updated_at | `DATETIME(3)` UTC | ISO-8601 `…Z` text | Server stamps `updated_at` with **server** time on every accepted write (pull cursor). |
| deleted_at | soft delete | soft delete | Deletes are updates that set this. |
| sync_version | server row version (starts 1) | version of the server row this local copy is based on (0 = never synced) | Optimistic concurrency. |
| device_id | device that last wrote | same | Provenance / diagnostics. |
| sync_status | `SYNCED`/`CONFLICT` | `PENDING`/`SYNCED`/`CONFLICT`/`FAILED` | |

Generator: `database/tools/schema_spec.py` (flags: `sync`, `global`, `append`, `fin`, `server`).

## 3. Local outbox (`sync_outbox`)

`id, local_id, entity_type, entity_uuid, operation (CREATE|UPDATE|DELETE), payload_json, base_version, created_at, updated_at, retry_count, status (PENDING|SYNCING|SYNCED|FAILED|CONFLICT), error_message, last_attempt_at, next_attempt_at, batch_id`

Written by `AppDbContext.SaveChangesAsync` (desktop/DarbarESaltanat.Infrastructure/Persistence/AppDbContext.cs):

* full-row snapshot in `payload_json` (snake_case, FKs as `*_uuid`, timestamps ISO-UTC, decimals as strings, no local `id`);
* **coalescing**: while an entry is still `PENDING`, later edits of the same row replace its payload (a CREATE stays a CREATE). Once an entry is `SYNCING`, new edits create a new entry so in-flight data is never mutated;
* `base_version` = the row's `sync_version` (the server version the edit was made against);
* rows written while applying pulled data (`ApplyingRemoteChanges = true`) are stored as `SYNCED` and **not** queued;
* append-only entities (`[AppendOnly]`) throw if edited — corrections are new reversing rows;
* local-only bookkeeping (failed-login counters) uses `ExecuteUpdate`, which bypasses the outbox by design.

## 4. Push — `POST /api/v1/sync/push` (implemented)

Device auth: `X-Device-Id` + `X-Device-Token` headers (or `device_id` / `device_token` in the body).

```json
{ "batch_id": "uuid", "device_id": "device-uuid",
  "changes": [ { "entity": "orders", "operation": "CREATE", "uuid": "…", "sync_version": 0,
                 "updated_at": "2026-11-20T10:00:00.000Z", "data": { "...": "the outbox payload_json" } } ] }
```

`sync_version` in a change is the **base version** — the server version the edit was made against. The desktop
recomputes it when it claims a batch from the row's current `sync_version` (a pull never overwrites a row that has
unsynced edits, so local state always contains every server version the device has seen).

### Desktop (`SyncEngine` + `SqliteSyncStore`)

```
every sync_interval_seconds (30 s) while online · immediately on "Sync now" / network reconnect / settings saved
back-off after failed runs: 30 s → 1 → 2 → 4 → 8 → 15 min (±20 % jitter)

1. claim (short txn, write gate):
     a SYNCING batch left over from a crash/timeout is RESUMED with the same batch_id;
     otherwise up to sync_batch_size PENDING/FAILED entries whose next_attempt_at has passed → SYNCING + new batch_id
2. HTTP POST /sync/push            (no transaction open)
3. apply results (short txn), results are matched to changes by position:
     SYNCED   → entry SYNCED; older unsent snapshots of the row → SYNCED ("superseded");
                newer entries continue from server_version; row.sync_version = server_version,
                row.sync_status = SYNCED when nothing else is queued for it
     CONFLICT → entry CONFLICT, base_version := the server version it conflicted with; row CONFLICT;
                sync_conflicts_local row
     FAILED   → retry_count+1, FAILED, next_attempt_at = now + min(15 s·2ⁿ, 30 min)
   network error / timeout / 5xx / 409 / 429 → batch stays SYNCING (same batch_id next time) and the run stops
   401/403 → status "device not authorised", run stops
   other 4xx (whole batch refused) → entries FAILED with back-off, new batch next time
```

### Server (`web/server/src/sync/push.service.js`)

```
deviceAuth → device ACTIVE
sync_batches.batch_id exists?  COMPLETED → return stored response_json ("replayed": true)
                               PROCESSING → 409 (client retries)
ONE transaction for the whole batch (+ sync_batches row + sync_records + stored response):
  each change in its own SAVEPOINT (one bad change never spoils the batch):
    entity must be syncable and not identity/RBAC (users, roles, permissions… are server-managed)
    data → columns: *_uuid → ids (missing parent → FAILED, retryable), enum/number/date validation,
                    branch must be the device's branch, server-owned columns ignored (coupons.used_count,
                    customers.password_hash)
    row missing            → INSERT (any operation: an earlier CREATE may have been superseded) → SYNCED v1
    append-only entity     → SYNCED, row unchanged (payments, refunds, stock movements… are immutable)
    row in CONFLICT        → CONFLICT again unless identical
    CREATE re-sent by the device that created it and still v1 → treated as an edit of v1
    row.sync_version ≠ base → identical content? SYNCED : sync_conflicts row, row.sync_status = CONFLICT → CONFLICT
    otherwise               → UPDATE, sync_version+1 (deleted_at handled as soft delete/restore) → SYNCED
  coupons.used_count recomputed from coupon_usages
  store response_json, batch COMPLETED, devices.last_push_at
concurrent duplicate batch_id → unique key → the loser returns the winner's stored response
```

Response: `{ batch_id, processed_at, results: [ { entity, uuid, status: SYNCED|CONFLICT|FAILED, server_version?, reason?, retryable? } ], replayed? }`

## 5. Conflict policy and resolution

| Entity class | Policy |
|---|---|
| Append-only ledgers (payments, refunds, stock_movements, stock_adjustments, cash_movements, supplier_payments, coupon_usages, loyalty_transactions, audit_logs) | No conflicts possible: CREATE-only, uuid idempotency, server ignores later edits. |
| Financial (`fin`): orders, order_items, cash_sessions, purchases, salaries, expenses | Always CONFLICT → admin decides. Never auto-merged. |
| Reference data (menu, categories, tables, settings, customers…) | CONFLICT → admin decides. |

* While a conflict is open the **till keeps its own copy** (pull holds back server versions ≤ the conflicting version) and the server keeps its copy (`sync_status = CONFLICT`).
* Portal → Devices & Sync → Conflicts shows a field-by-field diff. `POST /sync/conflicts/:uuid/resolve` with `KEEP_SERVER`, `KEEP_DEVICE` or `MERGED` (+ `data`) writes the winner as a **new version**, closes every open conflict for that record and is audited.
* Every till receives the new version on its next pull; the conflicted till overwrites its copy and closes its local conflict automatically.

## 6. Pull — `GET /api/v1/sync/pull?since=&cursor=&limit=&device_id=` (implemented)

* Returns rows with `updated_at > since` for the device's branch plus chain-wide rows, **parents first** (registry order), soft-deleted rows included so deletes replicate. Excluded: `audit_logs`; `customers.password_hash`. Staff `users` rows include bcrypt hashes so cashiers can sign in offline.
* A brand-new device (no `since`) receives all reference data but only the last 7 days of transactional history.
* Paging is a **keyset per table** — `(updated_at, id)` — carried in an opaque `cursor`, inside a snapshot window fixed when the pull started; rows sharing the same millisecond are never skipped. The client repeats the call with the cursor while `has_more` is true.
* When finished, `next_since` = pull start − 5 s overlap; the device stores it (`sync_state.last_pull_at`) and sends it as `since` next time. Re-sent rows are harmless: the desktop applies a row only when its `sync_version` is newer.
* Desktop apply (`SqliteSyncStore.ApplyPullAsync`): generic `INSERT … ON CONFLICT(uuid) DO UPDATE … WHERE excluded.sync_version > local.sync_version OR local.sync_status='CONFLICT'`, column names whitelisted from the live schema, rows with unsynced local edits or an open conflict skipped, written as SYNCED with `ApplyingRemoteChanges` semantics (never re-queued). FK checks are off during apply (the server is authoritative; history windows may reference older rows). Settings received from the server refresh the open screens.
* `POST /sync/acknowledge { pulled_until, pending_count }` and `POST /devices/heartbeat` update the device row for monitoring.

## 7. Ordering & referential integrity

* Outbox entries are pushed in insertion order; a child (order_item) is always created after its parent (order).
* Server resolves FKs by uuid; a missing parent yields FAILED (retryable) — never an orphan.
* Order numbers embed a per-device code (`DES1-20261120-0007`) so offline devices never produce the same `order_no`.

## 8. Observability

* Desktop: `sync_log` table + Serilog file logs; status bar (Online / Offline / Syncing / Synced / Pending changes / Sync failed / Conflicts found / Not authorised); **Sync page** (Shift+F9) with counts, last sync, changes needing attention, the sync log, *Sync now* and *Retry failed*.
* Server: `sync_batches` (+ stored responses), `sync_records` (one per change), `sync_conflicts`, `devices.last_heartbeat_at / last_push_at / last_pull_at / pending_count`; `GET /sync/status`, `GET /sync/batches`; portal **Devices & Sync** with Devices, Conflicts (diff + resolve) and Failed changes tabs.

## 9. Verification

* Server: `web/server/tests/sync.test.js` — idempotent replay, lost-response re-create, version conflicts + resolution + re-pull, missing parent then success, savepoint isolation, identity/branch/enum rejection, append-only immutability, soft-delete replication, coupon recount, keyset paging with identical timestamps, secrets excluded, status/batches, revoked device.
* Desktop: `DarbarESaltanat.Tests/SyncEngineTests.cs` — engine + real SQLite store: base versions, offline resume with the same batch id, continuation after a resumed push, conflict freeze and resolution, pull insert/type conversion, unsynced edits never overwritten.
* End-to-end (run during development): the compiled desktop `SyncEngine` + `HttpSyncApi` against the real API (`npm run dev:sqlite`): offline order pushed and seen by a second till, web price change pulled, till edit pushed on the new version, conflict frozen then resolved from the portal, offline → resume same batch, lost response → server replay, wrong token → not authorised.
