import { Router } from 'express';
import { z } from 'zod';
import { Op } from 'sequelize';
import { sequelize, Setting, Branch } from '../db/models/index.js';
import { ApiError, asyncHandler } from '../utils/errors.js';
import { ok } from '../utils/http.js';
import { validate } from '../middleware/validate.js';
import { authenticate, authorize } from '../middleware/auth.js';
import { PERMISSIONS as P } from '../security/permissions.js';
import { audit } from './audit.service.js';

// Keys anyone (e.g. the customer website) may read without logging in.
const PUBLIC_KEYS = ['restaurant_name', 'restaurant_address', 'restaurant_phone', 'currency', 'currency_symbol', 'timezone', 'vat_rate', 'service_charge_rate'];

const cast = (s) => {
  const v = s.setting_value;
  if (v === null) return null;
  switch (s.value_type) {
    case 'NUMBER': return Number(v);
    case 'BOOL': return v === 'true';
    case 'JSON': try { return JSON.parse(v); } catch { return null; }
    default: return v;
  }
};

/** Effective settings for a branch: chain-wide defaults (branch_id NULL) overridden by branch rows. */
async function effective(branchUuid, keys) {
  let branchId = null;
  if (branchUuid) {
    const b = await Branch.findOne({ where: { uuid: branchUuid } });
    if (!b) throw ApiError.badRequest('Unknown branch');
    branchId = b.id;
  }
  const where = { branch_id: branchId ? { [Op.or]: [null, branchId] } : null };
  if (keys) where.setting_key = keys;
  const rows = await Setting.findAll({ where, order: [['branch_id', 'ASC']] });
  const out = {};
  for (const s of rows) {
    if (out[s.setting_key] && s.branch_id === null) continue; // keep branch override
    out[s.setting_key] = { value: cast(s), type: s.value_type, group: s.group_name, scope: s.branch_id ? 'branch' : 'global' };
  }
  return out;
}

const r = Router();
r.get('/public', asyncHandler(async (_req, res) => {
  const s = await effective(null, PUBLIC_KEYS);
  ok(res, Object.fromEntries(Object.entries(s).map(([k, v]) => [k, v.value])));
}));
r.get('/', authenticate, validate({ query: z.object({ branch_uuid: z.string().uuid().optional() }) }),
  asyncHandler(async (req, res) => ok(res, await effective(req.validQuery.branch_uuid))));

const item = z.object({
  key: z.string().regex(/^[a-z][a-z0-9_]{1,99}$/),
  value: z.union([z.string().max(10000), z.number(), z.boolean(), z.record(z.any()), z.array(z.any()), z.null()]),
  group: z.string().max(50).optional(),
});
r.put('/', authenticate, authorize(P.SETTINGS_MANAGE),
  validate({ body: z.object({ branch_uuid: z.string().uuid().nullable().optional(), settings: z.array(item).min(1).max(100) }) }),
  asyncHandler(async (req, res) => {
    const branch = req.body.branch_uuid ? await Branch.findOne({ where: { uuid: req.body.branch_uuid } }) : null;
    if (req.body.branch_uuid && !branch) throw ApiError.badRequest('Unknown branch');
    await sequelize.transaction(async (t) => {
      for (const it of req.body.settings) {
        const type = typeof it.value === 'number' ? 'NUMBER' : typeof it.value === 'boolean' ? 'BOOL'
          : (it.value !== null && typeof it.value === 'object') ? 'JSON' : 'STRING';
        const value = it.value === null ? null : type === 'JSON' ? JSON.stringify(it.value) : String(it.value);
        const existing = await Setting.findOne({ where: { setting_key: it.key, branch_id: branch?.id ?? null }, paranoid: false, transaction: t });
        if (existing) {
          if (existing.deleted_at) await existing.restore({ transaction: t });
          const old = existing.setting_value;
          await existing.update({ setting_value: value, value_type: type, ...(it.group ? { group_name: it.group } : {}) }, { transaction: t });
          if (old !== value) await audit(req, 'settings.update', { entityType: 'settings', entityUuid: existing.uuid, oldValues: { [it.key]: old }, newValues: { [it.key]: value }, transaction: t });
        } else {
          const s = await Setting.create({ setting_key: it.key, setting_value: value, value_type: type, group_name: it.group || 'general', branch_id: branch?.id ?? null }, { transaction: t });
          await audit(req, 'settings.create', { entityType: 'settings', entityUuid: s.uuid, newValues: { [it.key]: value }, transaction: t });
        }
      }
    });
    ok(res, await effective(req.body.branch_uuid ?? null));
  }));
export default r;
