import rateLimit from 'express-rate-limit';
import { env } from '../config/env.js';
import { ApiError } from '../utils/errors.js';

/** Behind Nginx/Apache, TLS terminates at the proxy; reject requests that arrived over plain HTTP. */
export function requireHttps(req, _res, next) {
  if (!env.REQUIRE_HTTPS || req.secure || req.get('x-forwarded-proto') === 'https') return next();
  return next(new ApiError(403, 'HTTPS_REQUIRED', 'HTTPS is required'));
}
const limiter = (windowMs, limit) => rateLimit({
  windowMs, limit, standardHeaders: 'draft-7', legacyHeaders: false,
  skip: () => env.NODE_ENV === 'test',
  handler: (_req, res) => res.status(429).json({ success: false, error: { code: 'RATE_LIMITED', message: 'Too many requests, please slow down' } }),
});
export const apiLimiter = limiter(60_000, 300);
export const authLimiter = limiter(15 * 60_000, 20);
