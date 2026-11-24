import { Router } from 'express';
import { z } from 'zod';
import { sequelize, Role, Permission, RolePermission } from '../../db/models/index.js';
import { ApiError, asyncHandler } from '../../utils/errors.js';
import { ok, created } from '../../utils/http.js';
import { validate } from '../../middleware/validate.js';
import { authenticate, authorize } from '../../middleware/auth.js';
import { PERMISSIONS as P } from '../../security/permissions.js';
import { invalidateAccess } from '../../security/permissionService.js';
import { audit } from '../audit.service.js';

export const permissionsRouter = Router();
permissionsRouter.get('/', authenticate, authorize(P.ROLES_MANAGE), asyncHandler(async (_req, res) => {
  const rows = await Permission.findAll({ attributes: ['uuid', 'code', 'module', 'description'], order: [['module', 'ASC'], ['code', 'ASC']] });
  ok(res, rows);
}));

const r = Router();
r.use(authenticate);

const shape = (role) => ({
  uuid: role.uuid, code: role.code, name: role.name, description: role.description, is_system: role.is_system,
  permissions: (role.permissions || []).map((p) => p.code).sort(),
  permission_count: (role.permissions || []).length,
  updated_at: role.updated_at,
});
const INCLUDE = [{ model: Permission, as: 'permissions', attributes: ['code'], through: { attributes: [], where: { deleted_at: null } } }];
async function find(uuid, transaction) {
  const role = await Role.findOne({ where: { uuid }, include: INCLUDE, transaction });
  if (!role) throw ApiError.notFound('Role');
  return role;
}
async function setPermissions(role, codes, transaction) {
  const perms = await Permission.findAll({ where: { code: codes }, transaction });
  if (perms.length !== new Set(codes).size) throw ApiError.badRequest('One or more permission codes are invalid');
  const links = await RolePermission.findAll({ where: { role_id: role.id }, paranoid: false, transaction });
  const wanted = new Set(perms.map((p) => p.id));
  for (const l of links) {
    if (wanted.has(l.permission_id)) { if (l.deleted_at) await l.restore({ transaction }); wanted.delete(l.permission_id); }
    else if (!l.deleted_at) await l.destroy({ transaction });
  }
  for (const pid of wanted) await RolePermission.create({ role_id: role.id, permission_id: pid }, { transaction });
}

const uuidParam = z.object({ uuid: z.string().uuid() });
const body = z.object({
  code: z.string().trim().toUpperCase().regex(/^[A-Z][A-Z0-9_]{1,39}$/, 'Use UPPER_SNAKE_CASE'),
  name: z.string().trim().min(2).max(80),
  description: z.string().max(255).nullable().optional(),
  permissions: z.array(z.string().max(80)),
});

r.get('/', authorize(P.ROLES_MANAGE), asyncHandler(async (_req, res) => {
  ok(res, (await Role.findAll({ include: INCLUDE, order: [['name', 'ASC']] })).map(shape));
}));
r.get('/:uuid', authorize(P.ROLES_MANAGE), validate({ params: uuidParam }), asyncHandler(async (req, res) => ok(res, shape(await find(req.params.uuid)))));

r.post('/', authorize(P.ROLES_MANAGE), validate({ body }), asyncHandler(async (req, res) => {
  const uuid = await sequelize.transaction(async (t) => {
    const role = await Role.create({ code: req.body.code, name: req.body.name, description: req.body.description ?? null, is_system: false }, { transaction: t });
    await setPermissions(role, req.body.permissions, t);
    await audit(req, 'roles.create', { entityType: 'roles', entityUuid: role.uuid, newValues: req.body, transaction: t });
    return role.uuid;
  });
  created(res, shape(await find(uuid)));
}));

r.put('/:uuid/permissions', authorize(P.ROLES_MANAGE), validate({
  params: uuidParam,
  body: z.object({ permissions: z.array(z.string().max(80)) }),
}), asyncHandler(async (req, res) => {
  await sequelize.transaction(async (t) => {
    const role = await find(req.params.uuid, t);
    if (role.code === 'SUPER_ADMIN') throw ApiError.badRequest('Super Admin always has every permission');
    await setPermissions(role, req.body.permissions, t);
    await audit(req, 'roles.permissions_update', { entityType: 'roles', entityUuid: role.uuid, newValues: req.body, transaction: t });
  });
  invalidateAccess();
  ok(res, shape(await find(req.params.uuid)));
}));

r.put('/:uuid', authorize(P.ROLES_MANAGE), validate({ params: uuidParam, body: body.omit({ code: true }).partial() }), asyncHandler(async (req, res) => {
  await sequelize.transaction(async (t) => {
    const role = await find(req.params.uuid, t);
    if (role.code === 'SUPER_ADMIN' && req.body.permissions) throw ApiError.badRequest('Super Admin always has every permission');
    const before = shape(role);
    const patch = {};
    if (req.body.name !== undefined) patch.name = req.body.name;
    if (req.body.description !== undefined) patch.description = req.body.description;
    if (Object.keys(patch).length) await role.update(patch, { transaction: t });
    if (req.body.permissions) await setPermissions(role, req.body.permissions, t);
    await audit(req, 'roles.update', { entityType: 'roles', entityUuid: role.uuid, oldValues: before, newValues: req.body, transaction: t });
  });
  invalidateAccess();
  ok(res, shape(await find(req.params.uuid)));
}));

r.delete('/:uuid', authorize(P.ROLES_MANAGE), validate({ params: uuidParam }), asyncHandler(async (req, res) => {
  await sequelize.transaction(async (t) => {
    const role = await find(req.params.uuid, t);
    if (role.is_system) throw ApiError.badRequest('System roles cannot be deleted');
    const inUse = await sequelize.models.UserRole.count({ where: { role_id: role.id }, transaction: t });
    if (inUse) throw ApiError.conflict(`Role is assigned to ${inUse} user(s); reassign them first`);
    await RolePermission.destroy({ where: { role_id: role.id }, transaction: t });
    await role.destroy({ transaction: t });
    await audit(req, 'roles.delete', { entityType: 'roles', entityUuid: role.uuid, transaction: t });
  });
  invalidateAccess();
  ok(res, { deleted: true });
}));

export default r;
