import { ApiError } from '../utils/errors.js';
import { verifyAccessToken } from '../security/tokens.js';
import { loadAccess } from '../security/permissionService.js';

/** Requires a valid access JWT; attaches req.auth = { user, roles, permissions, isSuperAdmin }. */
export async function authenticate(req, _res, next) {
  try {
    const header = req.headers.authorization || '';
    const [scheme, token] = header.split(' ');
    if (scheme !== 'Bearer' || !token) throw ApiError.unauthorized();
    let payload;
    try { payload = verifyAccessToken(token); } catch { throw new ApiError(401, 'TOKEN_INVALID', 'Access token is invalid or expired'); }
    const access = await loadAccess(payload.sub);
    if (!access || !access.user.is_active) throw ApiError.unauthorized('Account is disabled');
    req.auth = access;
    next();
  } catch (e) { next(e); }
}

/** authorize('users.manage', ...) — user needs ALL listed permissions (Super Admin bypasses). */
export const authorize = (...required) => (req, _res, next) => {
  const a = req.auth;
  if (!a) return next(ApiError.unauthorized());
  if (a.isSuperAdmin || required.every((p) => a.permissions.has(p))) return next();
  return next(ApiError.forbidden(`Missing permission: ${required.filter((p) => !a.permissions.has(p)).join(', ')}`));
};
