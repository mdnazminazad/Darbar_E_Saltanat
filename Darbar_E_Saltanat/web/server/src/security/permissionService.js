import { User, Role, Permission } from '../db/models/index.js';
import { ROLE_CODES } from './permissions.js';

// Small TTL cache so permission checks don't hit MySQL on every request.
const cache = new Map();
const TTL_MS = 30_000;

export async function loadAccess(userUuid) {
  const hit = cache.get(userUuid);
  if (hit && hit.expires > Date.now()) return hit.value;
  const user = await User.findOne({
    where: { uuid: userUuid },
    include: [{ model: Role, as: 'roles', through: { attributes: [], where: { deleted_at: null } },
      include: [{ model: Permission, as: 'permissions', through: { attributes: [], where: { deleted_at: null } } }] }],
  });
  if (!user) return null;
  const roles = user.roles.map((r) => r.code);
  const permissions = new Set(user.roles.flatMap((r) => r.permissions.map((p) => p.code)));
  const value = { user, roles, permissions, isSuperAdmin: roles.includes(ROLE_CODES.SUPER_ADMIN) };
  cache.set(userUuid, { value, expires: Date.now() + TTL_MS });
  return value;
}
export const invalidateAccess = (userUuid) => (userUuid ? cache.delete(userUuid) : cache.clear());
