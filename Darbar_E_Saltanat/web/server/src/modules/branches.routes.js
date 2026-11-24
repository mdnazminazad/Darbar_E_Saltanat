import { Router } from 'express';
import { z } from 'zod';
import { Op } from 'sequelize';
import { Branch } from '../db/models/index.js';
import { ApiError, asyncHandler } from '../utils/errors.js';
import { ok, created, listParams, pageMeta } from '../utils/http.js';
import { validate } from '../middleware/validate.js';
import { authenticate, authorize } from '../middleware/auth.js';
import { PERMISSIONS as P } from '../security/permissions.js';
import { audit } from './audit.service.js';

const r = Router();
r.use(authenticate);
const FIELDS = ['uuid', 'code', 'name', 'address', 'phone', 'email', 'timezone', 'currency', 'is_active', 'created_at', 'updated_at'];
const pick = (b) => Object.fromEntries(FIELDS.map((f) => [f, b[f]]));
const body = z.object({
  code: z.string().trim().toUpperCase().regex(/^[A-Z0-9_-]{2,20}$/),
  name: z.string().trim().min(2).max(150),
  address: z.string().max(1000).nullable().optional(),
  phone: z.string().max(30).nullable().optional(),
  email: z.string().email().max(150).nullable().optional(),
  timezone: z.string().max(40).default('Asia/Dhaka'),
  currency: z.string().max(10).default('BDT'),
  is_active: z.boolean().default(true),
});
const uuidParam = z.object({ uuid: z.string().uuid() });
async function find(uuid) { const b = await Branch.findOne({ where: { uuid } }); if (!b) throw ApiError.notFound('Branch'); return b; }

r.get('/', asyncHandler(async (req, res) => {
  const p = listParams(req.query, ['name', 'code', 'created_at'], 'name');
  const where = req.query.search ? { [Op.or]: [{ name: { [Op.like]: `%${req.query.search}%` } }, { code: { [Op.like]: `%${req.query.search}%` } }] } : {};
  const { rows, count } = await Branch.findAndCountAll({ where, limit: p.limit, offset: p.offset, order: p.order });
  ok(res, rows.map(pick), pageMeta(p, count));
}));
r.get('/:uuid', validate({ params: uuidParam }), asyncHandler(async (req, res) => ok(res, pick(await find(req.params.uuid)))));
r.post('/', authorize(P.BRANCHES_MANAGE), validate({ body }), asyncHandler(async (req, res) => {
  const b = await Branch.create(req.body);
  await audit(req, 'branches.create', { entityType: 'branches', entityUuid: b.uuid, newValues: req.body });
  created(res, pick(b));
}));
r.patch('/:uuid', authorize(P.BRANCHES_MANAGE), validate({ params: uuidParam, body: body.partial() }), asyncHandler(async (req, res) => {
  const b = await find(req.params.uuid); const before = pick(b);
  await b.update(req.body);
  await audit(req, 'branches.update', { entityType: 'branches', entityUuid: b.uuid, oldValues: before, newValues: req.body });
  ok(res, pick(b));
}));
r.delete('/:uuid', authorize(P.BRANCHES_MANAGE), validate({ params: uuidParam }), asyncHandler(async (req, res) => {
  const b = await find(req.params.uuid);
  if ((await Branch.count()) <= 1) throw ApiError.conflict('Cannot delete the only branch');
  await b.update({ is_active: false }); await b.destroy();
  await audit(req, 'branches.delete', { entityType: 'branches', entityUuid: b.uuid });
  ok(res, { deleted: true });
}));
export default r;
