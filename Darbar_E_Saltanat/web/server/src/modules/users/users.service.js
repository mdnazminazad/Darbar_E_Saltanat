import { Op } from 'sequelize';
import { sequelize, User, Role, UserRole, Branch, RefreshToken, AuditLog } from '../../db/models/index.js';
import { ApiError } from '../../utils/errors.js';
import { hashSecret, verifySecret } from '../../security/passwords.js';
import { invalidateAccess } from '../../security/permissionService.js';
import { ROLE_CODES } from '../../security/permissions.js';
import { listParams, pageMeta } from '../../utils/http.js';
import { publicUser } from './users.serializer.js';
import { audit } from '../audit.service.js';

const INCLUDE = [
  { model: Role, as: 'roles', attributes: ['uuid', 'code', 'name'], through: { attributes: [], where: { deleted_at: null } } },
  { model: Branch, as: 'branch', attributes: ['uuid', 'name'] },
];

async function findUser(uuid, transaction) {
  const u = await User.findOne({ where: { uuid }, include: INCLUDE, transaction });
  if (!u) throw ApiError.notFound('User');
  return u;
}

async function branchIdOf(branchUuid, transaction) {
  if (!branchUuid) return null;
  const b = await Branch.findOne({ where: { uuid: branchUuid }, transaction });
  if (!b) throw ApiError.badRequest('Unknown branch_uuid');
  return b.id;
}

async function activeSuperAdminCount(transaction) {
  return User.count({
    where: { is_active: true }, distinct: true, transaction,
    include: [{ model: Role, as: 'roles', where: { code: ROLE_CODES.SUPER_ADMIN }, through: { where: { deleted_at: null } } }],
  });
}

const isSuperAdmin = (u) => {
  const roles = u.roles || [];
  return roles.some((r) => (typeof r === 'string' ? r : r.code) === ROLE_CODES.SUPER_ADMIN);
};

/** Replaces a user's roles. Soft-deleted links are restored instead of re-inserted. */
async function setRoles(user, roleCodes, transaction) {
  const roles = await Role.findAll({ where: { code: roleCodes }, transaction });
  if (roles.length !== new Set(roleCodes).size) throw ApiError.badRequest('One or more role codes are invalid');
  const links = await UserRole.findAll({ where: { user_id: user.id }, paranoid: false, transaction });
  const wanted = new Set(roles.map((r) => r.id));
  for (const link of links) {
    if (wanted.has(link.role_id)) {
      if (link.deleted_at) await link.restore({ transaction });
      wanted.delete(link.role_id);
    } else if (!link.deleted_at) {
      await link.destroy({ transaction });
    }
  }
  for (const roleId of wanted) {
    await UserRole.create({ user_id: user.id, role_id: roleId, branch_id: user.branch_id }, { transaction });
  }
}

export async function list(query) {
  const p = listParams(query, ['created_at', 'username', 'full_name', 'last_login_at'], 'created_at');
  const where = {};
  if (query.search) {
    const s = `%${query.search}%`;
    where[Op.or] = [
      { username: { [Op.like]: s } },
      { full_name: { [Op.like]: s } },
      { email: { [Op.like]: s } },
      { phone: { [Op.like]: s } },
    ];
  }
  if (query.is_active !== undefined) where.is_active = query.is_active === 'true' || query.is_active === true;
  if (query.branch_uuid) {
    const branch = await Branch.findOne({ where: { uuid: query.branch_uuid } });
    if (branch) where.branch_id = branch.id;
  }
  const roleFilter = query.role ? { where: { code: query.role } } : {};
  const { rows, count } = await User.findAndCountAll({
    where, distinct: true, limit: p.limit, offset: p.offset, order: p.order,
    include: [{ ...INCLUDE[0], ...roleFilter, required: !!query.role }, INCLUDE[1]],
  });
  return { data: rows.map((u) => publicUser(u)), meta: pageMeta(p, count) };
}

function buildNotes(notes, idNumber, accessScope) {
  let meta = {};
  if (notes) {
    try {
      const p = typeof notes === 'string' ? JSON.parse(notes) : notes;
      if (p && typeof p === 'object') meta = { ...p };
      else meta = { text: String(notes) };
    } catch {
      meta = { text: String(notes) };
    }
  }
  if (idNumber !== undefined) meta.id_number = idNumber ? String(idNumber).trim() : null;
  if (accessScope !== undefined) meta.access_scope = accessScope || 'BOTH';
  return JSON.stringify(meta);
}

export const get = async (uuid) => publicUser(await findUser(uuid));

export async function create(req, input) {
  const normUsername = input.username.trim();
  const existingUser = await User.findOne({
    where: sequelize.where(sequelize.fn('LOWER', sequelize.col('username')), normUsername.toLowerCase()),
    paranoid: false,
  });
  if (existingUser) throw ApiError.conflict('Username already exists');

  const notesToSave = (input.id_number !== undefined || input.access_scope !== undefined)
    ? buildNotes(input.notes, input.id_number, input.access_scope)
    : (input.notes ?? null);

  const uuid = await sequelize.transaction(async (t) => {
    const user = await User.create({
      username: normUsername,
      email: input.email ? input.email.trim().toLowerCase() : null,
      full_name: input.full_name.trim(),
      phone: input.phone ? input.phone.trim() : null,
      password_hash: await hashSecret(input.password),
      pin_hash: input.pin ? await hashSecret(input.pin) : null,
      must_change_password: input.must_change_password ?? true,
      is_active: input.is_active ?? true,
      branch_id: await branchIdOf(input.branch_uuid, t),
      profile_image_url: input.profile_image_url ?? null,
      notes: notesToSave,
    }, { transaction: t });

    await setRoles(user, input.role_codes, t);
    await audit(req, 'users.create', {
      entityType: 'users',
      entityUuid: user.uuid,
      newValues: { username: user.username, email: user.email, roles: input.role_codes, is_active: user.is_active },
      transaction: t,
    });
    return user.uuid;
  });
  return get(uuid);
}

export async function update(req, uuid, input) {
  await sequelize.transaction(async (t) => {
    const user = await findUser(uuid, t);
    const before = publicUser(user);

    // Safeguard: non-Super-Admin cannot edit their own role or active status
    const reqIsAdmin = req.auth?.user?.roles?.includes(ROLE_CODES.SUPER_ADMIN) || isSuperAdmin(req.auth.user);
    if (user.id === req.auth.user.id && !reqIsAdmin) {
      if (input.role_codes !== undefined || input.is_active !== undefined) {
        throw ApiError.forbidden('You cannot change your own role or active status');
      }
    }

    // Safeguard: cannot remove or disable the last active Super Admin
    const removingAdmin = isSuperAdmin(user) && (
      (input.role_codes && !input.role_codes.includes(ROLE_CODES.SUPER_ADMIN)) || input.is_active === false
    );
    if (removingAdmin && (await activeSuperAdminCount(t)) <= 1) {
      throw ApiError.conflict('Cannot remove or disable the last active Super Admin');
    }
    if (input.is_active === false && user.id === req.auth.user.id) {
      throw ApiError.badRequest('You cannot disable your own account');
    }

    const patch = {};
    if (input.username !== undefined && input.username.trim() !== user.username) {
      const newUname = input.username.trim();
      const clash = await User.findOne({
        where: {
          [Op.and]: [
            sequelize.where(sequelize.fn('LOWER', sequelize.col('username')), newUname.toLowerCase()),
            { id: { [Op.ne]: user.id } },
          ],
        },
        paranoid: false,
        transaction: t,
      });
      if (clash) throw ApiError.conflict(`Username "${newUname}" is already taken`);
      patch.username = newUname;
      await audit(req, 'users.username_change', {
        entityType: 'users', entityUuid: uuid,
        oldValues: { username: user.username }, newValues: { username: newUname },
        transaction: t,
      });
    }

    for (const k of ['email', 'full_name', 'phone', 'is_active', 'must_change_password', 'profile_image_url']) {
      if (input[k] !== undefined) patch[k] = input[k];
    }
    if (input.id_number !== undefined || input.access_scope !== undefined) {
      patch.notes = buildNotes(input.notes !== undefined ? input.notes : user.notes, input.id_number, input.access_scope);
    } else if (input.notes !== undefined) {
      patch.notes = input.notes;
    }
    if (input.branch_uuid !== undefined) patch.branch_id = await branchIdOf(input.branch_uuid, t);
    if (input.pin !== undefined) patch.pin_hash = input.pin ? await hashSecret(input.pin) : null;
    if (input.password !== undefined) {
      if (await verifySecret(input.password, user.password_hash)) {
        throw ApiError.badRequest('New password cannot be the same as current password');
      }
      patch.password_hash = await hashSecret(input.password);
      patch.must_change_password = input.must_change_password ?? true;
      patch.failed_login_count = 0;
      patch.locked_until = null;
      await audit(req, 'users.password_reset', { entityType: 'users', entityUuid: uuid, transaction: t });
    }

    if (Object.keys(patch).length) await user.update(patch, { transaction: t });
    if (input.role_codes) {
      await setRoles(user, input.role_codes, t);
      await audit(req, 'users.roles_change', {
        entityType: 'users', entityUuid: uuid,
        oldValues: { roles: before.roles }, newValues: { roles: input.role_codes },
        transaction: t,
      });
    }

    if (input.is_active === false) {
      await RefreshToken.update({ revoked_at: new Date() }, { where: { user_id: user.id, revoked_at: null }, transaction: t });
      await audit(req, 'users.deactivate', { entityType: 'users', entityUuid: uuid, transaction: t });
    }

    await audit(req, 'users.update', {
      entityType: 'users', entityUuid: uuid,
      oldValues: before, newValues: { ...input, pin: input.pin ? '***' : undefined, password: input.password ? '***' : undefined },
      transaction: t,
    });
  });
  invalidateAccess(uuid);
  return get(uuid);
}

export async function activate(req, uuid) {
  return update(req, uuid, { is_active: true });
}

export async function deactivate(req, uuid) {
  return update(req, uuid, { is_active: false });
}

export async function assignRoles(req, uuid, roleCodes) {
  return update(req, uuid, { role_codes: roleCodes });
}

export async function resetPassword(req, uuid, newPassword, forceChange = true) {
  await sequelize.transaction(async (t) => {
    const user = await findUser(uuid, t);
    if (await verifySecret(newPassword, user.password_hash)) {
      throw ApiError.badRequest('New password cannot be the same as current password');
    }
    await user.update({
      password_hash: await hashSecret(newPassword),
      must_change_password: forceChange,
      failed_login_count: 0,
      locked_until: null,
    }, { transaction: t });
    await RefreshToken.update({ revoked_at: new Date() }, { where: { user_id: user.id, revoked_at: null }, transaction: t });
    await audit(req, 'users.reset_password', { entityType: 'users', entityUuid: uuid, transaction: t });
  });
  invalidateAccess(uuid);
}

export async function remove(req, uuid) {
  await sequelize.transaction(async (t) => {
    const user = await findUser(uuid, t);
    if (user.id === req.auth.user.id) throw ApiError.badRequest('You cannot delete your own account');
    if (isSuperAdmin(user) && (await activeSuperAdminCount(t)) <= 1) {
      throw ApiError.conflict('Cannot delete the last active Super Admin');
    }
    await user.update({ is_active: false }, { transaction: t });
    await user.destroy({ transaction: t }); // soft delete (deleted_at)
    await RefreshToken.update({ revoked_at: new Date() }, { where: { user_id: user.id, revoked_at: null }, transaction: t });
    await audit(req, 'users.delete', { entityType: 'users', entityUuid: uuid, transaction: t });
  });
  invalidateAccess(uuid);
}

export async function getAuditLogs(uuid) {
  const rows = await AuditLog.findAll({
    where: { entity_type: 'users', entity_uuid: uuid },
    order: [['occurred_at', 'DESC']],
    limit: 50,
  });
  return rows;
}
