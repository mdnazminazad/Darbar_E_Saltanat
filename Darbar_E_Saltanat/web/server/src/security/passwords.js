import bcrypt from 'bcryptjs';
import { env } from '../config/env.js';

export const hashSecret = (plain) => bcrypt.hash(plain, env.BCRYPT_ROUNDS);
export const verifySecret = (plain, hash) => (hash ? bcrypt.compare(plain, hash) : Promise.resolve(false));

/** Password policy shared by create/reset/change endpoints. */
export const PASSWORD_RULES = {
  min: 8,
  test: (p) => /[A-Z]/.test(p) && /[a-z]/.test(p) && /\d/.test(p) && /[^A-Za-z0-9]/.test(p),
  message: 'Password must be at least 8 characters and include at least one uppercase letter, one lowercase letter, one number, and one special character',
};

export const isSamePassword = (plain, currentHash) => verifySecret(plain, currentHash);
