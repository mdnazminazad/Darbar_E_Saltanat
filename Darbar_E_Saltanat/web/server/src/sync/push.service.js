import { UniqueConstraintError, ForeignKeyConstraintError, ValidationError } from 'sequelize';
import { sequelize } from '../db/models/index.js';
import { registry, modelFor } from '../db/models/registryModels.js';
import { SyncBatch, SyncRecord, SyncConflict } from '../db/models/syncModels.js';
import { ApiError } from '../utils/errors.js';
import { logger } from '../logger.js';
import { PUSH_DENY, fromWire, toWire, sameContent, MissingParentError, InvalidChangeError } from './wire.js';

const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const result = (c, status, extra = {}) => ({ entity: c.entity, uuid: c.uuid, status, ...extra });

/**
 * Applies one batch from a desktop. The whole batch runs in ONE transaction together with the sync_batches row
 * and its stored response, so:
 *   - a batch either fully happened (and is replayed from response_json) or did not happen at all;
 *   - each change runs in its own SAVEPOINT, so one bad change cannot spoil the rest of the batch.
 */
export async function processPush(device, body) {
  const { batch_id: batchId, changes } = body;
  const existing = await SyncBatch.findOne({ where: { batch_id: batchId } });
  if (existing) return replay(existing, device);

  try {
    return await sequelize.transaction(async (t) => {
      const now = new Date();
      const batch = await SyncBatch.create({ batch_id: batchId, device_id: device.id, status: 'PROCESSING', change_count: changes.length, received_at: now }, { transaction: t });
      const results = [];
      const couponIds = new Set();
      for (const change of changes) {
        let r;
        try {
          r = await sequelize.transaction({ transaction: t }, (sp) => applyChange(device, change, sp, couponIds));
        } catch (err) {
          r = result(change, 'FAILED', { reason: failureReason(err), retryable: !!err.retryable });
          if (!(err instanceof MissingParentError || err instanceof InvalidChangeError || err instanceof UniqueConstraintError
            || err instanceof ForeignKeyConstraintError || err instanceof ValidationError)) {
            logger.error({ err, entity: change.entity, uuid: change.uuid }, 'Sync change failed unexpectedly');
          }
        }
        results.push(r);
        await SyncRecord.create({
          batch_id: batch.id, entity: String(change.entity).slice(0, 60), entity_uuid: change.uuid, operation: change.operation,
          client_version: change.sync_version, server_version: r.server_version ?? null, status: r.status, reason: r.reason?.slice(0, 500) ?? null,
        }, { transaction: t });
      }
      await refreshCouponCounts(couponIds, t);
      const response = { batch_id: batchId, processed_at: new Date().toISOString(), results };
      await batch.update({
        status: 'COMPLETED', processed_at: new Date(), response_json: response,
        success_count: results.filter((x) => x.status === 'SYNCED').length,
        conflict_count: results.filter((x) => x.status === 'CONFLICT').length,
        failure_count: results.filter((x) => x.status === 'FAILED').length,
      }, { transaction: t });
      await device.update({ last_push_at: new Date() }, { transaction: t });
      return response;
    });
  } catch (err) {
    // Same batch_id submitted concurrently: the first one won the unique key.
    if (err instanceof UniqueConstraintError) {
      const winner = await SyncBatch.findOne({ where: { batch_id: batchId } });
      if (winner) return replay(winner, device);
    }
    throw err;
  }
}

function replay(batch, device) {
  if (batch.device_id !== device.id) throw ApiError.conflict('batch_id already used by another device');
  if (batch.status !== 'COMPLETED') throw new ApiError(409, 'BATCH_IN_PROGRESS', 'This batch is still being processed; retry shortly');
  const response = typeof batch.response_json === 'string' ? JSON.parse(batch.response_json) : batch.response_json;
  return { ...response, replayed: true };
}

function failureReason(err) {
  if (err instanceof UniqueConstraintError) return `Duplicate value: ${err.errors?.map((e) => e.path).join(', ') || 'unique key'}`;
  if (err instanceof ForeignKeyConstraintError) return 'Referenced record does not exist';
  if (err instanceof ValidationError) return err.errors.map((e) => e.message).join('; ');
  return err.message || 'Unknown error';
}

async function applyChange(device, c, t, couponIds) {
  const meta = registry[c.entity];
  if (!meta || PUSH_DENY.has(c.entity)) throw new InvalidChangeError(`Entity ${c.entity} cannot be synced from a device`);
  if (!UUID_RE.test(c.uuid)) throw new InvalidChangeError('uuid must be a UUID');
  if (c.data?.uuid && c.data.uuid !== c.uuid) throw new InvalidChangeError('data.uuid does not match uuid');

  const Model = modelFor(c.entity);
  const attrs = await fromWire(c.entity, c.data, { transaction: t, device });
  const deletedAt = c.data.deleted_at ? new Date(c.data.deleted_at) : null;
  const row = await Model.findOne({ where: { uuid: c.uuid }, paranoid: false, transaction: t, lock: t.LOCK?.UPDATE });

  if (!row) {
    // Unknown on the server → create it, whatever the operation (an earlier CREATE may have been superseded on the device).
    const created = await Model.create({
      ...attrs, uuid: c.uuid, device_id: device.uuid, sync_version: 1, sync_status: 'SYNCED',
      ...(c.data.created_at ? { created_at: new Date(c.data.created_at) } : {}),
    }, { transaction: t });
    if (deletedAt) await Model.update({ deleted_at: deletedAt }, { where: { id: created.id }, paranoid: false, hooks: false, transaction: t });
    if (c.entity === 'coupon_usages') couponIds.add(attrs.coupon_id);
    return result(c, 'SYNCED', { server_version: 1 });
  }

  // Append-only ledgers (payments, stock movements…) never change after creation: a replay is a no-op.
  if (meta.append_only) return result(c, 'SYNCED', { server_version: row.sync_version });

  if (row.sync_status === 'CONFLICT') {
    if (sameContent(row, attrs, deletedAt)) return result(c, 'SYNCED', { server_version: row.sync_version });
    await recordConflict(device, c, row, t);
    return result(c, 'CONFLICT', { server_version: row.sync_version, reason: 'Record has an unresolved conflict' });
  }

  // A CREATE re-sent by the device that created it (e.g. lost response) is treated as an edit of version 1.
  const base = c.operation === 'CREATE' && row.device_id === device.uuid && row.sync_version === 1 ? 1 : c.sync_version;
  if (row.sync_version !== base) {
    if (sameContent(row, attrs, deletedAt)) return result(c, 'SYNCED', { server_version: row.sync_version });
    await recordConflict(device, c, row, t);
    await Model.update({ sync_status: 'CONFLICT' }, { where: { id: row.id }, paranoid: false, hooks: false, silent: true, transaction: t });
    return result(c, 'CONFLICT', { server_version: row.sync_version, reason: 'Server record changed after local update' });
  }

  await row.update({ ...attrs, device_id: device.uuid }, { transaction: t });
  if (!!deletedAt !== !!row.deleted_at) {
    await Model.update({ deleted_at: deletedAt, sync_version: row.sync_version + 1 }, { where: { id: row.id }, paranoid: false, hooks: false, transaction: t });
  }
  await row.reload({ paranoid: false, transaction: t });
  return result(c, 'SYNCED', { server_version: row.sync_version });
}

async function recordConflict(device, c, row, t) {
  const [serverData] = await toWire(c.entity, [row], { transaction: t });
  await SyncConflict.create({
    device_id: device.id, entity: c.entity, entity_uuid: c.uuid, local_data: c.data, server_data: serverData,
    local_version: c.sync_version, server_version: row.sync_version, status: 'OPEN', detected_at: new Date(),
  }, { transaction: t });
}

/** coupons.used_count is server-owned: recomputed from the append-only coupon_usages ledger. */
async function refreshCouponCounts(ids, t) {
  for (const id of ids) {
    const used = await modelFor('coupon_usages').count({ where: { coupon_id: id }, transaction: t });
    const coupon = await modelFor('coupons').findByPk(id, { transaction: t });
    if (coupon && coupon.used_count !== used) await coupon.update({ used_count: used }, { transaction: t });
  }
}
