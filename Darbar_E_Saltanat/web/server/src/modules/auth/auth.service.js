import { Op } from 'sequelize';
import { sequelize, User, RefreshToken, Branch } from '../../db/models/index.js';
import { env } from '../../config/env.js';
import { ApiError } from '../../utils/errors.js';
import { hashSecret, verifySecret } from '../../security/passwords.js';
import { signAccessToken, newOpaqueToken, sha256 } from '../../security/tokens.js';
import { loadAccess, invalidateAccess } from '../../security/permissionService.js';
import { publicUser } from '../users/users.serializer.js';
import { audit } from '../audit.service.js';

// Constant-time-ish rejection for unknown usernames (prevents user enumeration by timing).
const DUMMY_HASH = '$2a$12$og47Uw6Ne.GcFPHxrYNDRe.s/HDc4Tq6hJZnTlh8VEKaNXu014WO.';

async function issueTokens(user, req, transaction) {
  const access = await loadAccess(user.uuid);
  const refresh = newOpaqueToken();
  await RefreshToken.create({
    user_id: user.id, token_hash: sha256(refresh),
    expires_at: new Date(Date.now() + env.REFRESH_TTL_DAYS * 86_400_000),
    user_agent: req.get('user-agent')?.slice(0, 255) ?? null, ip_address: req.ip?.slice(0, 45) ?? null,
  }, { transaction });
  return {
    access_token: signAccessToken(user, access.roles), refresh_token: refresh, token_type: 'Bearer',
    expires_in: env.JWT_ACCESS_TTL, user: publicUser(user, access),
  };
}

export async function login(req, identifier, password) {
  const trimmed = String(identifier || '').trim();
  const user = await User.findOne({
    where: {
      [Op.or]: [
        { username: { [Op.like]: trimmed } },
        { email: { [Op.like]: trimmed } },
        sequelize.where(sequelize.fn('LOWER', sequelize.col('username')), trimmed.toLowerCase()),
        { notes: { [Op.like]: `%"id_number":"${trimmed}"%` } },
      ],
    },
    include: [{ model: Branch, as: 'branch', attributes: ['uuid'] }],
  });
  if (!user) {
    await verifySecret(password, DUMMY_HASH);
    throw new ApiError(401, 'INVALID_CREDENTIALS', 'Invalid username or password');
  }
  if (user.locked_until && user.locked_until > new Date()) {
    throw ApiError.locked(`Account locked after repeated failed logins. Try again after ${user.locked_until.toISOString()}`);
  }
  if (!user.is_active) throw new ApiError(403, 'ACCOUNT_DISABLED', 'This account is disabled');

  if (!(await verifySecret(password, user.password_hash))) {
    const failed = user.failed_login_count + 1;
    const lock = failed >= env.MAX_FAILED_LOGINS;
    await user.update({
      failed_login_count: lock ? 0 : failed,
      locked_until: lock ? new Date(Date.now() + env.LOCKOUT_MINUTES * 60_000) : null,
    });
    await audit(req, lock ? 'auth.account_locked' : 'auth.login_failed', { entityType: 'users', entityUuid: user.uuid });
    throw new ApiError(401, 'INVALID_CREDENTIALS', 'Invalid username or password');
  }

  return sequelize.transaction(async (t) => {
    await user.update({ failed_login_count: 0, locked_until: null, last_login_at: new Date() }, { transaction: t });
    const result = await issueTokens(user, req, t);
    await audit(req, 'auth.login', { entityType: 'users', entityUuid: user.uuid, transaction: t });
    return result;
  });
}

/** Rotating refresh tokens with reuse detection: presenting a revoked token revokes every session of that user. */
export async function refresh(req, token) {
  const hash = sha256(token);
  let reusedBy = null;
  const result = await sequelize.transaction(async (t) => {
    const row = await RefreshToken.findOne({ where: { token_hash: hash }, transaction: t, lock: t.LOCK?.UPDATE });
    if (!row) throw new ApiError(401, 'REFRESH_INVALID', 'Refresh token is invalid');
    if (row.revoked_at) { reusedBy = row.user_id; return null; } // handled after commit (a throw would roll back the revocation)
    if (row.expires_at < new Date()) throw new ApiError(401, 'REFRESH_EXPIRED', 'Session expired, please log in again');
    const user = await User.findByPk(row.user_id, { include: [{ model: Branch, as: 'branch', attributes: ['uuid'] }], transaction: t });
    if (!user || !user.is_active) throw ApiError.unauthorized('Account is disabled');
    invalidateAccess(user.uuid);
    const issued = await issueTokens(user, req, t);
    await row.update({ revoked_at: new Date(), replaced_by_hash: sha256(issued.refresh_token) }, { transaction: t });
    return issued;
  });
  if (reusedBy) {
    await RefreshToken.update({ revoked_at: new Date() }, { where: { user_id: reusedBy, revoked_at: null } });
    await audit(req, 'auth.refresh_token_reuse', { entityType: 'users' });
    throw new ApiError(401, 'REFRESH_REUSED', 'Refresh token reuse detected; all sessions were signed out');
  }
  return result;
}

export async function logout(req, token) {
  if (token) await RefreshToken.update({ revoked_at: new Date() }, { where: { token_hash: sha256(token), revoked_at: null } });
  await audit(req, 'auth.logout', { entityType: 'users', entityUuid: req.auth?.user?.uuid });
}

export async function changePassword(req, currentPassword, newPassword) {
  const user = req.auth.user;
  if (!(await verifySecret(currentPassword, user.password_hash))) throw ApiError.badRequest('Current password is incorrect');
  if (currentPassword === newPassword || (await verifySecret(newPassword, user.password_hash))) {
    throw ApiError.badRequest('New password cannot be the same as your current password');
  }
  await sequelize.transaction(async (t) => {
    await user.update({ password_hash: await hashSecret(newPassword), must_change_password: false }, { transaction: t });
    // sign out other sessions
    await RefreshToken.update({ revoked_at: new Date() }, { where: { user_id: user.id, revoked_at: null }, transaction: t });
    await audit(req, 'auth.password_changed', { entityType: 'users', entityUuid: user.uuid, transaction: t });
  });
  invalidateAccess(user.uuid);
}

export async function me(req) {
  const user = await User.findByPk(req.auth.user.id, { include: [{ model: Branch, as: 'branch', attributes: ['uuid'] }] });
  return publicUser(user, req.auth);
}
