import { Router } from 'express';
import { z } from 'zod';
import { asyncHandler } from '../../utils/errors.js';
import { ok } from '../../utils/http.js';
import { validate } from '../../middleware/validate.js';
import { authenticate } from '../../middleware/auth.js';
import { authLimiter } from '../../middleware/security.js';
import { PASSWORD_RULES } from '../../security/passwords.js';
import * as svc from './auth.service.js';

const r = Router();
const password = z.string().min(PASSWORD_RULES.min).max(128).refine(PASSWORD_RULES.test, PASSWORD_RULES.message);

r.post('/login', authLimiter,
  validate({ body: z.object({ username: z.string().trim().min(1).max(150), password: z.string().min(1).max(128) }) }),
  asyncHandler(async (req, res) => ok(res, await svc.login(req, req.body.username, req.body.password))));

r.post('/refresh', authLimiter,
  validate({ body: z.object({ refresh_token: z.string().min(20).max(200) }) }),
  asyncHandler(async (req, res) => ok(res, await svc.refresh(req, req.body.refresh_token))));

r.post('/logout', authenticate,
  validate({ body: z.object({ refresh_token: z.string().max(200).optional() }) }),
  asyncHandler(async (req, res) => { await svc.logout(req, req.body.refresh_token); ok(res, { logged_out: true }); }));

r.get('/me', authenticate, asyncHandler(async (req, res) => ok(res, await svc.me(req))));

r.post('/change-password', authenticate,
  validate({ body: z.object({ current_password: z.string().min(1), new_password: password }) }),
  asyncHandler(async (req, res) => { await svc.changePassword(req, req.body.current_password, req.body.new_password); ok(res, { changed: true }); }));

export default r;
