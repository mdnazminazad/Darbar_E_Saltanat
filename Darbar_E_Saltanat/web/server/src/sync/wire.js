// Converts between MySQL rows and the desktop "wire" format used by sync:
//   * ids never leave the server: FKs travel as <name>_uuid, branch as branch_uuid
//   * money/qty/pct as fixed decimal strings, timestamps as ISO-8601 UTC, booleans as true/false
// The desktop builds the same shape from its SQLite rows (DarbarESaltanat.Application.Sync.SyncPayload).
import { Op } from 'sequelize';
import { registry, modelFor } from '../db/models/registryModels.js';
import { Branch } from '../db/models/index.js';

/** Identity/RBAC is managed on the server only; desktops may not push it. */
export const PUSH_DENY = new Set(['branches', 'roles', 'permissions', 'role_permissions', 'users', 'user_roles']);
/** Not sent to desktops. */
export const PULL_EXCLUDE = new Set(['audit_logs']);
/** Transactional history: a brand-new device only receives the last few days. */
export const RECENT_ONLY = new Set([
  'orders', 'order_items', 'order_item_addons', 'order_tables', 'kots', 'kot_items', 'payments', 'refunds', 'coupon_usages',
  'stock_movements', 'stock_adjustments', 'cash_sessions', 'cash_movements', 'loyalty_transactions', 'attendance', 'expenses',
  'purchases', 'purchase_items', 'supplier_payments', 'salaries',
]);
/** Columns that never travel in either direction. */
const SECRET = { customers: new Set(['password_hash']) };
/** Columns the server owns (devices cannot set them). */
const SERVER_OWNED = { coupons: new Set(['used_count']) };

export class MissingParentError extends Error {
  constructor(field, ref) { super(`Parent not synced yet: ${field} (${ref})`); this.field = field; this.retryable = true; }
}
export class InvalidChangeError extends Error {}

const fmt = {
  money: (v) => (v === null || v === undefined ? null : Number(v).toFixed(2)),
  qty: (v) => (v === null || v === undefined ? null : Number(v).toFixed(3)),
  pct: (v) => (v === null || v === undefined ? null : Number(v).toFixed(2)),
  bool: (v) => (v === null || v === undefined ? null : !!v),
  dt: (v) => (v === null || v === undefined ? null : new Date(v).toISOString()),
  json: (v) => (v === null || v === undefined ? null : typeof v === 'string' ? v : JSON.stringify(v)),
};

export async function toWire(table, rows, { transaction } = {}) {
  const meta = registry[table];
  const refs = meta.fks.map((f) => ({ column: f.column, out: f.uuid_field, ref: f.ref }));
  if (meta.has_branch) refs.push({ column: 'branch_id', out: 'branch_uuid', ref: 'branches' });
  const lookups = {};
  for (const r of refs) {
    const ids = [...new Set(rows.map((row) => row.get ? row.get(r.column) : row[r.column]).filter((x) => x !== null && x !== undefined))];
    const Model = r.ref === 'branches' ? Branch : modelFor(r.ref);
    const found = ids.length ? await Model.findAll({ where: { id: { [Op.in]: ids } }, attributes: ['id', 'uuid'], paranoid: false, transaction }) : [];
    lookups[r.column] = new Map(found.map((f) => [String(f.id), f.uuid]));
  }
  const secret = SECRET[table] ?? new Set();
  return rows.map((row) => {
    const p = row.get ? row.get({ plain: true }) : row;
    const out = { uuid: p.uuid };
    for (const r of refs) out[r.out] = p[r.column] === null || p[r.column] === undefined ? null : lookups[r.column].get(String(p[r.column])) ?? null;
    for (const c of meta.columns) {
      if (secret.has(c.name)) continue;
      out[c.name] = fmt[c.type] ? fmt[c.type](p[c.name]) : (p[c.name] ?? null);
    }
    out.created_at = fmt.dt(p.created_at);
    out.updated_at = fmt.dt(p.updated_at);
    out.deleted_at = fmt.dt(p.deleted_at);
    out.sync_version = p.sync_version;
    out.device_id = p.device_id ?? null;
    return out;
  });
}

const toBool = (v) => v === true || v === 1 || v === '1' || v === 'true';

/** Desktop payload → Sequelize attributes (excluding uuid/created_at/deleted_at/sync columns). */
export async function fromWire(table, data, { transaction, device }) {
  const meta = registry[table];
  if (!data || typeof data !== 'object' || Array.isArray(data)) throw new InvalidChangeError('data must be an object');
  const attrs = {};
  const skip = new Set([...(SECRET[table] ?? []), ...(SERVER_OWNED[table] ?? [])]);
  for (const c of meta.columns) {
    if (!(c.name in data) || skip.has(c.name)) continue;
    const v = data[c.name];
    if (v === null || v === undefined) {
      if (!c.nullable) throw new InvalidChangeError(`${c.name} is required`);
      attrs[c.name] = null; continue;
    }
    switch (c.type) {
      case 'bool': attrs[c.name] = toBool(v); break;
      case 'int': case 'bigint':
        if (!Number.isInteger(Number(v))) throw new InvalidChangeError(`${c.name} must be an integer`);
        attrs[c.name] = Number(v); break;
      case 'money': case 'qty': case 'pct':
        if (!Number.isFinite(Number(v))) throw new InvalidChangeError(`${c.name} must be a number`);
        attrs[c.name] = String(v); break;
      case 'dt': {
        const d = new Date(v);
        if (Number.isNaN(d.getTime())) throw new InvalidChangeError(`${c.name} must be an ISO date`);
        attrs[c.name] = d; break;
      }
      case 'json': attrs[c.name] = typeof v === 'string' ? safeJson(v) : v; break;
      case 'enum':
        if (!c.values.includes(v)) throw new InvalidChangeError(`${c.name} must be one of ${c.values.join(', ')}`);
        attrs[c.name] = v; break;
      default: attrs[c.name] = String(v);
    }
  }
  for (const fk of meta.fks) {
    if (!(fk.uuid_field in data)) continue;
    const uuid = data[fk.uuid_field];
    if (uuid === null || uuid === undefined) {
      if (!fk.nullable) throw new InvalidChangeError(`${fk.uuid_field} is required`);
      attrs[fk.column] = null; continue;
    }
    const parentModel = fk.ref === 'devices' ? null : modelFor(fk.ref);
    const parent = parentModel && await parentModel.findOne({ where: { uuid }, attributes: ['id'], paranoid: false, transaction });
    if (!parent) throw new MissingParentError(fk.uuid_field, fk.ref);
    attrs[fk.column] = parent.id;
  }
  if (meta.has_branch) {
    const b = data.branch_uuid;
    if (b === null || b === undefined || b === '') {
      attrs.branch_id = meta.global_scope ? null : device.branch_id;
    } else {
      const branch = await Branch.findOne({ where: { uuid: b }, transaction });
      if (!branch) throw new InvalidChangeError('Unknown branch_uuid');
      if (branch.id !== device.branch_id) throw new InvalidChangeError('This device may only write to its own branch');
      attrs.branch_id = branch.id;
    }
  }
  return attrs;
}

function safeJson(s) { try { return JSON.parse(s); } catch { return s; } }

/** True when applying attrs would not change the row (used to accept replays without raising conflicts). */
export function sameContent(row, attrs, deletedAt) {
  for (const [k, v] of Object.entries(attrs)) {
    const cur = row.get(k);
    const a = v instanceof Date ? v.getTime() : typeof v === 'object' && v !== null ? JSON.stringify(v) : v;
    const b = cur instanceof Date ? cur.getTime() : typeof cur === 'object' && cur !== null ? JSON.stringify(cur) : cur;
    if (a === b) continue;
    if (a !== null && b !== null && !Number.isNaN(Number(a)) && Number(a) === Number(b) && typeof a !== 'boolean') continue;
    if (typeof a === 'boolean' && !!b === a) continue;
    return false;
  }
  return (!!deletedAt) === (!!row.get('deleted_at'));
}
