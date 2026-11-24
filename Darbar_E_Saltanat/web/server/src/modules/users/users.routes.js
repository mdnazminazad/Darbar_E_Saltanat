import { Router } from 'express';
import { z } from 'zod';
import { asyncHandler } from '../../utils/errors.js';
import { ok, created } from '../../utils/http.js';
import { validate } from '../../middleware/validate.js';
import { authenticate, authorize } from '../../middleware/auth.js';
import { PERMISSIONS as P } from '../../security/permissions.js';
import { PASSWORD_RULES } from '../../security/passwords.js';
import * as svc from './users.service.js';

const r = Router();
r.use(authenticate, authorize(P.USERS_MANAGE));

const password = z.string().min(PASSWORD_RULES.min).max(128).refine(PASSWORD_RULES.test, PASSWORD_RULES.message);
const pin = z.string().regex(/^\d{4,6}$/, 'PIN must be 4–6 digits');
const uuidParam = z.object({ uuid: z.string().uuid() });

const base = {
  id_number: z.string().trim().max(50).nullable().optional(),
  access_scope: z.enum(['DESKTOP', 'WEB', 'BOTH']).nullable().optional(),
  email: z.string().email().max(150).nullable().optional(),
  full_name: z.string().trim().min(2).max(150),
  phone: z.string().trim().max(30).nullable().optional(),
  branch_uuid: z.string().uuid().nullable().optional(),
  is_active: z.boolean().optional(),
  must_change_password: z.boolean().optional(),
  role_codes: z.array(z.string().max(40)).min(1),
  profile_image_url: z.string().max(255).nullable().optional(),
  notes: z.string().max(2000).nullable().optional(),
};

const createSchema = z.object({
  ...base,
  username: z.string().trim().min(3).max(60).regex(/^[a-zA-Z0-9._-]+$/, 'Letters, digits, dot, dash, underscore only'),
  password,
  pin: pin.optional(),
});

const updateSchema = z.object({
  ...base,
  username: z.string().trim().min(3).max(60).regex(/^[a-zA-Z0-9._-]+$/, 'Letters, digits, dot, dash, underscore only').optional(),
  full_name: base.full_name.optional(),
  role_codes: base.role_codes.optional(),
  password: password.optional(),
  pin: pin.nullable().optional(),
}).refine((o) => Object.keys(o).length > 0, 'Nothing to update');

r.get('/', asyncHandler(async (req, res) => {
  const { data, meta } = await svc.list(req.query);
  ok(res, data, meta);
}));

r.post('/', validate({ body: createSchema }), asyncHandler(async (req, res) => {
  created(res, await svc.create(req, req.body));
}));

r.get('/:uuid', validate({ params: uuidParam }), asyncHandler(async (req, res) => {
  ok(res, await svc.get(req.params.uuid));
}));

r.put('/:uuid', validate({ params: uuidParam, body: updateSchema }), asyncHandler(async (req, res) => {
  ok(res, await svc.update(req, req.params.uuid, req.body));
}));

r.patch('/:uuid', validate({ params: uuidParam, body: updateSchema }), asyncHandler(async (req, res) => {
  ok(res, await svc.update(req, req.params.uuid, req.body));
}));

r.post('/:uuid/reset-password', validate({
  params: uuidParam,
  body: z.object({ new_password: password, force_change: z.boolean().default(true) }),
}), asyncHandler(async (req, res) => {
  await svc.resetPassword(req, req.params.uuid, req.body.new_password, req.body.force_change);
  ok(res, { reset: true, message: 'Password has been successfully reset' });
}));

r.post('/:uuid/activate', validate({ params: uuidParam }), asyncHandler(async (req, res) => {
  ok(res, await svc.activate(req, req.params.uuid));
}));

r.post('/:uuid/deactivate', validate({ params: uuidParam }), asyncHandler(async (req, res) => {
  ok(res, await svc.deactivate(req, req.params.uuid));
}));

r.post('/:uuid/assign-roles', validate({
  params: uuidParam,
  body: z.object({ role_codes: z.array(z.string()).min(1) }),
}), asyncHandler(async (req, res) => {
  ok(res, await svc.assignRoles(req, req.params.uuid, req.body.role_codes));
}));

r.get('/:uuid/audit-logs', validate({ params: uuidParam }), asyncHandler(async (req, res) => {
  ok(res, await svc.getAuditLogs(req.params.uuid));
}));

r.delete('/:uuid', validate({ params: uuidParam }), asyncHandler(async (req, res) => {
  await svc.remove(req, req.params.uuid);
  ok(res, { deleted: true });
}));

export default r;
