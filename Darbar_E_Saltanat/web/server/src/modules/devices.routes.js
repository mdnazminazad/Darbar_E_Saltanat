import { Router } from 'express';
import { z } from 'zod';
import { Device, Branch } from '../db/models/index.js';
import { SyncBatch, SyncRecord } from '../db/models/syncModels.js';
import { ApiError, asyncHandler } from '../utils/errors.js';
import { ok, created } from '../utils/http.js';
import { validate } from '../middleware/validate.js';
import { authenticate, authorize } from '../middleware/auth.js';
import { deviceAuth } from '../middleware/deviceAuth.js';
import { PERMISSIONS as P } from '../security/permissions.js';
import { hashSecret } from '../security/passwords.js';
import { newOpaqueToken } from '../security/tokens.js';
import { audit } from './audit.service.js';

const r = Router();
const shape = (d) => ({
  device_id: d.uuid,
  name: d.name,
  platform: d.platform,
  app_version: d.app_version,
  status: d.status,
  branch_uuid: d.branch?.uuid,
  branch_name: d.branch?.name,
  last_heartbeat_at: d.last_heartbeat_at,
  last_push_at: d.last_push_at,
  last_pull_at: d.last_pull_at,
  pending_count: d.pending_count,
  last_ip: d.last_ip,
  online: !!d.last_heartbeat_at && Date.now() - new Date(d.last_heartbeat_at).getTime() < 3 * 60_000,
});

/** An admin registers a desktop. The device token is shown ONCE and stored only as a bcrypt hash. */
r.post('/register', authenticate, authorize(P.SYNC_MANAGE), validate({
  body: z.object({
    name: z.string().trim().min(2).max(100),
    branch_uuid: z.string().uuid(),
    platform: z.string().max(40).default('WINDOWS'),
    app_version: z.string().max(30).optional(),
  }),
}), asyncHandler(async (req, res) => {
  const branch = await Branch.findOne({ where: { uuid: req.body.branch_uuid } });
  if (!branch) throw ApiError.badRequest('Unknown branch');
  const token = newOpaqueToken(32);
  const d = await Device.create({
    name: req.body.name,
    branch_id: branch.id,
    platform: req.body.platform,
    app_version: req.body.app_version ?? null,
    api_key_hash: await hashSecret(token),
  });
  await audit(req, 'devices.register', { entityType: 'devices', entityUuid: d.uuid, newValues: { name: d.name } });
  created(res, { device_id: d.uuid, device_token: token, branch_uuid: branch.uuid, note: 'Store this token now — it cannot be shown again.' });
}));

r.post('/heartbeat', deviceAuth, validate({
  body: z.object({
    device_id: z.string().uuid().optional(),
    device_token: z.string().optional(),
    app_version: z.string().max(30).optional(),
    pending_count: z.number().int().min(0).default(0),
  }),
}), asyncHandler(async (req, res) => {
  await req.device.update({
    last_heartbeat_at: new Date(),
    pending_count: req.body.pending_count,
    last_ip: req.ip?.slice(0, 45),
    ...(req.body.app_version ? { app_version: req.body.app_version } : {}),
  });
  ok(res, { server_time: new Date().toISOString(), status: req.device.status, online: true });
}));

/** The calling device's own identity (used by the desktop after its token is entered). */
r.get('/me', deviceAuth, asyncHandler(async (req, res) => {
  const branch = await Branch.findByPk(req.device.branch_id);
  ok(res, {
    device_id: req.device.uuid,
    name: req.device.name,
    status: req.device.status,
    branch_uuid: branch?.uuid,
    branch_name: branch?.name,
    server_time: new Date().toISOString(),
  });
}));

r.get('/', authenticate, authorize(P.SYNC_VIEW), asyncHandler(async (_req, res) => {
  const devices = await Device.findAll({
    include: [{ model: Branch, as: 'branch', attributes: ['uuid', 'name'] }],
    order: [['name', 'ASC']],
  });
  ok(res, devices.map(shape));
}));

r.get('/:uuid', authenticate, authorize(P.SYNC_VIEW), validate({
  params: z.object({ uuid: z.string().uuid() }),
}), asyncHandler(async (req, res) => {
  const d = await Device.findOne({
    where: { uuid: req.params.uuid },
    include: [{ model: Branch, as: 'branch', attributes: ['uuid', 'name'] }],
  });
  if (!d) throw ApiError.notFound('Device');
  ok(res, shape(d));
}));

r.post('/:uuid/disable', authenticate, authorize(P.SYNC_MANAGE), validate({
  params: z.object({ uuid: z.string().uuid() }),
}), asyncHandler(async (req, res) => {
  const d = await Device.findOne({ where: { uuid: req.params.uuid } });
  if (!d) throw ApiError.notFound('Device');
  await d.update({ status: 'DISABLED' });
  await audit(req, 'devices.disable', { entityType: 'devices', entityUuid: d.uuid });
  ok(res, { disabled: true, status: 'DISABLED' });
}));

r.post('/:uuid/regenerate-key', authenticate, authorize(P.SYNC_MANAGE), validate({
  params: z.object({ uuid: z.string().uuid() }),
}), asyncHandler(async (req, res) => {
  const d = await Device.findOne({ where: { uuid: req.params.uuid } });
  if (!d) throw ApiError.notFound('Device');
  const token = newOpaqueToken(32);
  await d.update({ api_key_hash: await hashSecret(token), status: 'ACTIVE' });
  await audit(req, 'devices.regenerate_key', { entityType: 'devices', entityUuid: d.uuid });
  ok(res, { device_id: d.uuid, device_token: token, note: 'Store this token now — it cannot be shown again.' });
}));

r.get('/:uuid/sync-logs', authenticate, authorize(P.SYNC_VIEW), validate({
  params: z.object({ uuid: z.string().uuid() }),
}), asyncHandler(async (req, res) => {
  const d = await Device.findOne({ where: { uuid: req.params.uuid } });
  if (!d) throw ApiError.notFound('Device');
  const batches = await SyncBatch.findAll({
    where: { device_id: d.id },
    order: [['received_at', 'DESC']],
    limit: 30,
    include: [{ model: SyncRecord, as: 'records', limit: 20 }],
  });
  ok(res, batches);
}));

r.post('/:uuid/revoke', authenticate, authorize(P.SYNC_MANAGE), validate({
  params: z.object({ uuid: z.string().uuid() }),
}), asyncHandler(async (req, res) => {
  const d = await Device.findOne({ where: { uuid: req.params.uuid } });
  if (!d) throw ApiError.notFound('Device');
  await d.update({ status: 'REVOKED' });
  await audit(req, 'devices.revoke', { entityType: 'devices', entityUuid: d.uuid });
  ok(res, { revoked: true });
}));

export default r;
