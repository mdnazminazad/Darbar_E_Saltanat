import { Op } from 'sequelize';
import { registry, modelFor } from '../db/models/registryModels.js';
import { Branch } from '../db/models/index.js';
import { ApiError } from '../utils/errors.js';
import { PULL_EXCLUDE, RECENT_ONLY, toWire } from './wire.js';

/** Rows written within this window before a page was served are re-sent next time (desktop apply is idempotent). */
export const OVERLAP_MS = 5000;
const RECENT_DAYS = 7;

const encode = (o) => Buffer.from(JSON.stringify(o)).toString('base64url');
function decode(s) {
  try { const o = JSON.parse(Buffer.from(s, 'base64url').toString('utf8')); if (typeof o.start !== 'string' || typeof o.tables !== 'object') throw new Error(); return o; }
  catch { throw ApiError.badRequest('Invalid cursor'); }
}

/**
 * Incremental pull. Entities are returned parents-first (registry order).
 * Paging uses a keyset per table — (updated_at, id) — carried in an opaque cursor, so rows that share the same
 * millisecond can never be skipped. When has_more is false, next_since is the time the pull STARTED minus an
 * overlap, which the device stores and sends as ?since next time.
 */
export async function pull(device, { since, cursor, limit }) {
  const sinceDate = since ? new Date(since) : null;
  if (since && Number.isNaN(sinceDate.getTime())) throw ApiError.badRequest('since must be an ISO-8601 timestamp');
  const state = cursor ? decode(cursor) : { start: new Date().toISOString(), tables: {} };
  const recentFloor = new Date(Date.parse(state.start) - RECENT_DAYS * 86400000);
  const branch = await Branch.findByPk(device.branch_id);

  let budget = limit;
  const changes = [];
  let hasMore = false;
  for (const [table, meta] of Object.entries(registry)) {
    if (PULL_EXCLUDE.has(table)) continue;
    const pos = state.tables[table];
    if (pos === 'done') continue;
    if (budget <= 0) { hasMore = true; break; }

    const and = [];
    if (sinceDate) and.push({ updated_at: { [Op.gt]: sinceDate } });
    else if (RECENT_ONLY.has(table)) and.push({ updated_at: { [Op.gte]: recentFloor } });
    and.push({ updated_at: { [Op.lte]: new Date(state.start) } }); // stable snapshot window for the whole pull
    if (Array.isArray(pos)) {
      const ts = new Date(pos[0]);
      and.push({ [Op.or]: [{ updated_at: { [Op.gt]: ts } }, { updated_at: ts, id: { [Op.gt]: pos[1] } }] });
    }
    if (table === 'branches') and.push({ id: branch.id });
    else if (meta.has_branch) and.push(meta.global_scope ? { [Op.or]: [{ branch_id: null }, { branch_id: branch.id }] } : { branch_id: branch.id });

    const rows = await modelFor(table).findAll({ where: { [Op.and]: and }, paranoid: false, order: [['updated_at', 'ASC'], ['id', 'ASC']], limit: budget + 1 });
    if (rows.length > budget) {
      rows.length = budget;
      const last = rows[rows.length - 1];
      state.tables[table] = [new Date(last.updated_at).toISOString(), Number(last.id)];
      hasMore = true;
    } else state.tables[table] = 'done';
    budget -= rows.length;
    if (rows.length) changes.push({ entity: table, rows: await toWire(table, rows) });
    if (hasMore) break;
  }
  // Rows changed after `start` are picked up by the next pull (since = start − overlap).
  return {
    server_time: new Date().toISOString(),
    has_more: hasMore,
    cursor: hasMore ? encode(state) : null,
    next_since: hasMore ? null : new Date(Date.parse(state.start) - OVERLAP_MS).toISOString(),
    changes,
  };
}
