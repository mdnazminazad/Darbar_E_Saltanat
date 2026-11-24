import crypto from 'node:crypto';
import jwt from 'jsonwebtoken';
import { env } from '../config/env.js';

const ISSUER = 'darbar-e-saltanat';

export function signAccessToken(user, roleCodes) {
  return jwt.sign({ roles: roleCodes, typ: 'access' }, env.JWT_ACCESS_SECRET, {
    subject: user.uuid, expiresIn: env.JWT_ACCESS_TTL, issuer: ISSUER, algorithm: 'HS256',
  });
}
export function verifyAccessToken(token) {
  const p = jwt.verify(token, env.JWT_ACCESS_SECRET, { issuer: ISSUER, algorithms: ['HS256'] });
  if (p.typ !== 'access') throw new Error('wrong token type');
  return p;
}
/** Opaque refresh token; only its SHA-256 is stored. */
export const newOpaqueToken = (bytes = 48) => crypto.randomBytes(bytes).toString('base64url');
export const sha256 = (s) => crypto.createHash('sha256').update(s).digest('hex');
