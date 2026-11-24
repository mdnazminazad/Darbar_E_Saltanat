import { Router } from 'express';
import { z } from 'zod';
import { Op } from 'sequelize';
import { Device, Branch } from '../db/models/index.js';
import { SyncBatch, SyncRecord } from '../db/models/syncModels.js';
import { asyncHandler } from '../utils/errors.js';
import { ok } from '../utils/http.js';
import { validate } from '../middleware/validate.js';
import { authenticate, authorize } from '../middleware/auth.js';
import { deviceAuth } from '../middleware/deviceAuth.js';
import { PERMISSIONS as P } from '../security/permissions.js';
import { processPush } from '../sync/push.service.js';
import { pull } from '../sync/pull.service.js';
import { listConflicts, resolveConflict, openConflictCount, since24h } from '../sync/conflicts.service.js';

const r = Router();
const uuid = z.string().uuid();

const pushSchema = z.object({
  batch_id: uuid,
  device_id: uuid.optional(),
  device_token: z.string().optional(),
  changes: z.array(z.object({
    entity: z.string().min(1).max(60),
    operation: z.enum(['CREATE', 'UPDATE', 'DELETE']),
    uuid: z.string().min(1).max(64),
    sync_version: z.number().int().min(0),
    updated_at: z.string().nullish(),
    data: z.record(z.any()),
  })).min(1).max(500),
});

/** Device → server. Idempotent by batch_id. */
r.post('/push', deviceAuth, validate({ body: pushSchema }), asyncHandler(async (req, res) => {
  ok(res, await processPush(req.device, req.body));
}));

/** Server → device, incremental. */
r.get('/pull', deviceAuth, validate({ query: z.object({
  since: z.string().datetime({ offset: true }).optional(),
  cursor: z.string().max(20000).optional(),
  device_id: uuid.optional(),
  limit: z.coerce.number().int().min(1).max(2000).default(500),
}) }), asyncHandler(async (req, res) => {
  const data = await pull(req.device, req.validQuery);
  await req.device.update({ last_pull_at: new Date() });
  ok(res, data);
}));

r.post('/acknowledge', deviceAuth, validate({ body: z.object({
  device_id: uuid.optional(), device_token: z.string().optional(),
  pulled_until: z.string().datetime({ offset: true }), pending_count: z.number().int().min(0).optional(),
}) }), asyncHandler(async (req, res) => {
  await req.device.update({ last_pull_at: new Date(), ...(req.body.pending_count !== undefined ? { pending_count: req.body.pending_count } : {}) });
  ok(res, { acknowledged: true, pulled_until: req.body.pulled_until });
}));

/** Devices see their own status; signed-in users with sync.view see every device. */
r.get('/status', asyncHandler(async (req, res, next) => {
  if (req.get('x-device-id')) {
    return deviceAuth(req, res, async (err) => {
      if (err) return next(err);
      ok(res, {
        device_id: req.device.uuid, server_time: new Date().toISOString(), status: req.device.status,
        last_push_at: req.device.last_push_at, last_pull_at: req.device.last_pull_at, open_conflicts: await openConflictCount(req.device.id),
      });
    });
  }
  return authenticate(req, res, (err) => {
    if (err) return next(err);
    return authorize(P.SYNC_VIEW)(req, res, async (err2) => {
      if (err2) return next(err2);
      try {
        const devices = await Device.findAll({ include: [{ model: Branch, as: 'branch', attributes: ['uuid', 'name'] }], order: [['name', 'ASC']] });
        const [failedBatches, records24h, conflicts] = await Promise.all([
          SyncBatch.count({ where: { failure_count: { [Op.gt]: 0 }, received_at: since24h() } }),
          SyncRecord.count({ where: { created_at: since24h() } }),
          openConflictCount(),
        ]);
        ok(res, {
          server_time: new Date().toISOString(), open_conflicts: conflicts, failed_batches_24h: failedBatches, records_synced_24h: records24h,
          devices: devices.map((d) => ({
            device_id: d.uuid, name: d.name, branch: d.branch?.name, status: d.status, pending_count: d.pending_count,
            last_heartbeat_at: d.last_heartbeat_at, last_push_at: d.last_push_at, last_pull_at: d.last_pull_at, app_version: d.app_version,
            online: !!d.last_heartbeat_at && Date.now() - new Date(d.last_heartbeat_at).getTime() < 3 * 60_000,
          })),
        });
      } catch (e) { next(e); }
    });
  });
}));

r.get('/conflicts', authenticate, authorize(P.SYNC_VIEW), validate({ query: z.object({
  status: z.enum(['OPEN', 'RESOLVED_LOCAL', 'RESOLVED_SERVER', 'RESOLVED_MERGED', 'ALL']).default('OPEN'),
  entity: z.string().max(60).optional(), limit: z.coerce.number().int().min(1).max(500).default(100),
}) }), asyncHandler(async (req, res) => ok(res, await listConflicts(req.validQuery))));

r.post('/conflicts/:uuid/resolve', authenticate, authorize(P.SYNC_RESOLVE), validate({
  params: z.object({ uuid }),
  body: z.object({ resolution: z.enum(['KEEP_SERVER', 'KEEP_DEVICE', 'MERGED']), data: z.record(z.any()).optional(), note: z.string().max(255).optional() }),
}), asyncHandler(async (req, res) => ok(res, await resolveConflict(req, req.params.uuid, req.body))));

/** Batches with failed changes (or all), newest first, with their per-change records. */
r.get('/batches', authenticate, authorize(P.SYNC_VIEW), validate({ query: z.object({
  failed_only: z.enum(['true', 'false']).default('true'), limit: z.coerce.number().int().min(1).max(200).default(50),
}) }), asyncHandler(async (req, res) => {
  const where = req.validQuery.failed_only === 'true' ? { [Op.or]: [{ failure_count: { [Op.gt]: 0 } }, { status: 'FAILED' }] } : {};
  const batches = await SyncBatch.findAll({ where, order: [['received_at', 'DESC']], limit: req.validQuery.limit });
  const devices = await Device.findAll({ where: { id: [...new Set(batches.map((b) => b.device_id))] } });
  const records = batches.length ? await SyncRecord.findAll({ where: { batch_id: batches.map((b) => b.id), status: { [Op.ne]: 'SYNCED' } } }) : [];
  ok(res, batches.map((b) => ({
    batch_id: b.batch_id, device: devices.find((d) => d.id === b.device_id)?.name, status: b.status, received_at: b.received_at,
    change_count: b.change_count, success_count: b.success_count, conflict_count: b.conflict_count, failure_count: b.failure_count, error_message: b.error_message,
    problems: records.filter((x) => x.batch_id === b.id).map((x) => ({ entity: x.entity, entity_uuid: x.entity_uuid, operation: x.operation, status: x.status, reason: x.reason })),
  })));
}));

export default r;
