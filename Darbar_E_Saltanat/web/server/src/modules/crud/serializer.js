import { Op } from 'sequelize';
import { registry, modelFor } from '../../db/models/registryModels.js';
import { Branch } from '../../db/models/index.js';
import { ApiError } from '../../utils/errors.js';

const HIDDEN = new Set(['id', 'password_hash', 'pin_hash', 'api_key_hash']);

const formatters = {
  money: (v) => (v === null || v === undefined ? v : Number(v).toFixed(2)),
  qty: (v) => (v === null || v === undefined ? v : Number(v).toFixed(3)),
  pct: (v) => (v === null || v === undefined ? v : Number(v).toFixed(2)),
  bool: (v) => (v === null || v === undefined ? v : !!v),
};

/** Converts DB rows to API shape: internal ids → uuids (*_uuid), decimals → fixed strings, no secrets. */
export async function serializeRows(table, rows) {
  const meta = registry[table];
  const refs = meta.fks.map((f) => ({ column: f.column, out: f.uuid_field, ref: f.ref }));
  if (meta.has_branch) refs.push({ column: 'branch_id', out: 'branch_uuid', ref: 'branches' });
  const lookups = {};
  for (const r of refs) {
    const ids = [...new Set(rows.map((row) => row[r.column] ?? row.get?.(r.column)).filter((x) => x !== null && x !== undefined))];
    if (!ids.length) { lookups[r.column] = new Map(); continue; }
    const Model = r.ref === 'branches' ? Branch : modelFor(r.ref);
    const found = await Model.findAll({ where: { id: { [Op.in]: ids } }, attributes: ['id', 'uuid'], paranoid: false });
    lookups[r.column] = new Map(found.map((f) => [String(f.id), f.uuid]));
  }
  const types = Object.fromEntries(meta.columns.map((c) => [c.name, c.type]));
  const refByColumn = Object.fromEntries(refs.map((r) => [r.column, r]));
  return rows.map((row) => {
    const plain = row.get ? row.get({ plain: true }) : row;
    const out = {};
    for (const [k, v] of Object.entries(plain)) {
      if (HIDDEN.has(k)) continue;
      const ref = refByColumn[k];
      if (ref) { out[ref.out] = v === null || v === undefined ? null : lookups[k].get(String(v)) ?? null; continue; }
      out[k] = formatters[types[k]] ? formatters[types[k]](v) : v;
    }
    return out;
  });
}
export const serializeRow = async (table, row) => (await serializeRows(table, [row]))[0];

/** Converts API input (*_uuid, branch_uuid) to DB columns (*_id). Unknown parents → 422. */
export async function resolveRefs(table, input, { transaction, defaultBranchId } = {}) {
  const meta = registry[table];
  const out = { ...input };
  for (const fk of meta.fks) {
    if (!(fk.uuid_field in input)) continue;
    const uuid = input[fk.uuid_field];
    delete out[fk.uuid_field];
    if (uuid === null) {
      if (!fk.nullable) throw new ApiError(422, 'VALIDATION_ERROR', `${fk.uuid_field} is required`);
      out[fk.column] = null; continue;
    }
    const parent = await modelFor(fk.ref).findOne({ where: { uuid }, attributes: ['id'], transaction });
    if (!parent) throw new ApiError(422, 'VALIDATION_ERROR', 'Validation failed', [{ field: fk.uuid_field, message: `No ${fk.ref} record with this uuid` }]);
    out[fk.column] = parent.id;
  }
  if (meta.has_branch) {
    if ('branch_uuid' in input) {
      delete out.branch_uuid;
      if (input.branch_uuid === null) {
        if (!meta.global_scope) throw new ApiError(422, 'VALIDATION_ERROR', 'branch_uuid is required');
        out.branch_id = null;
      } else {
        const b = await Branch.findOne({ where: { uuid: input.branch_uuid }, transaction });
        if (!b) throw new ApiError(422, 'VALIDATION_ERROR', 'Unknown branch_uuid');
        out.branch_id = b.id;
      }
    } else if (defaultBranchId !== undefined && !meta.global_scope && out.branch_id === undefined) {
      out.branch_id = defaultBranchId;
    }
  }
  return out;
}

/** Branch for new branch-scoped rows: the user's branch, else the first active branch. */
export async function defaultBranchIdFor(req) {
  if (req.auth?.user?.branch_id) return req.auth.user.branch_id;
  const b = await Branch.findOne({ where: { is_active: true }, order: [['id', 'ASC']] });
  return b?.id ?? null;
}
