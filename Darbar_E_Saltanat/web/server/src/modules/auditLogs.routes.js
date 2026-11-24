import { Router } from 'express';
import { z } from 'zod';
import { Op } from 'sequelize';
import { AuditLog, User } from '../db/models/index.js';
import { asyncHandler } from '../utils/errors.js';
import { ok, listParams, pageMeta } from '../utils/http.js';
import { validate } from '../middleware/validate.js';
import { authenticate, authorize } from '../middleware/auth.js';
import { PERMISSIONS as P } from '../security/permissions.js';

const r = Router();
r.get('/', authenticate, authorize(P.AUDIT_VIEW), validate({ query: z.object({
  action: z.string().max(80).optional(), entity_type: z.string().max(60).optional(), entity_uuid: z.string().uuid().optional(),
  from: z.string().datetime().optional(), to: z.string().datetime().optional(),
}).passthrough() }), asyncHandler(async (req, res) => {
  const q = req.validQuery;
  const p = listParams(req.query, ['occurred_at', 'action'], 'occurred_at');
  const where = {};
  if (q.action) where.action = { [Op.like]: `${q.action}%` };
  if (q.entity_type) where.entity_type = q.entity_type;
  if (q.entity_uuid) where.entity_uuid = q.entity_uuid;
  if (q.from || q.to) where.occurred_at = { ...(q.from ? { [Op.gte]: new Date(q.from) } : {}), ...(q.to ? { [Op.lte]: new Date(q.to) } : {}) };
  const { rows, count } = await AuditLog.findAndCountAll({
    where, limit: p.limit, offset: p.offset, order: p.order,
    include: [{ model: User, as: 'user', attributes: ['uuid', 'username', 'full_name'], paranoid: false }],
  });
  ok(res, rows.map((a) => ({
    uuid: a.uuid, action: a.action, entity_type: a.entity_type, entity_uuid: a.entity_uuid, old_values: a.old_values,
    new_values: a.new_values, ip_address: a.ip_address, occurred_at: a.occurred_at, user: a.user ? a.user.toJSON() : null,
  })), pageMeta(p, count));
}));
export default r;
