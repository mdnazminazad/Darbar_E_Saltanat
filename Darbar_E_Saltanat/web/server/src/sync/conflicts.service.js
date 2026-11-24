import { Op } from 'sequelize';
import { sequelize, Device } from '../db/models/index.js';
import { modelFor } from '../db/models/registryModels.js';
import { SyncConflict } from '../db/models/syncModels.js';
import { ApiError } from '../utils/errors.js';
import { audit } from '../modules/audit.service.js';
import { fromWire, toWire } from './wire.js';

export async function listConflicts({ status = 'OPEN', entity, deviceId, limit = 100 }) {
  const where = {};
  if (status !== 'ALL') where.status = status;
  if (entity) where.entity = entity;
  if (deviceId) where.device_id = deviceId;
  const rows = await SyncConflict.findAll({ where, order: [['detected_at', 'DESC']], limit });
  const devices = await Device.findAll({ where: { id: [...new Set(rows.map((r) => r.device_id).filter(Boolean))] } });
  return Promise.all(rows.map(async (c) => {
    const current = await modelFor(c.entity).findOne({ where: { uuid: c.entity_uuid }, paranoid: false });
    return {
      uuid: c.uuid, entity: c.entity, entity_uuid: c.entity_uuid, status: c.status,
      device: devices.find((d) => d.id === c.device_id)?.name ?? null,
      local_version: c.local_version, server_version: c.server_version,
      local_data: c.local_data, server_data_at_conflict: c.server_data,
      server_data: current ? (await toWire(c.entity, [current]))[0] : null,
      detected_at: c.detected_at, resolved_at: c.resolved_at, resolution_note: c.resolution_note,
    };
  }));
}

/**
 * KEEP_SERVER  – the server row stays; its version is bumped so every device pulls it again.
 * KEEP_DEVICE  – the device's version is written to the server (new version, pulled by all devices).
 * MERGED       – the admin's merged field values are written.
 * Desktops overwrite their CONFLICT copy when they pull the newer version.
 */
export async function resolveConflict(req, uuid, { resolution, data, note }) {
  return sequelize.transaction(async (t) => {
    const c = await SyncConflict.findOne({ where: { uuid }, transaction: t, lock: t.LOCK?.UPDATE });
    if (!c) throw ApiError.notFound('Conflict');
    if (c.status !== 'OPEN') throw ApiError.conflict('This conflict is already resolved');
    const Model = modelFor(c.entity);
    const row = await Model.findOne({ where: { uuid: c.entity_uuid }, paranoid: false, transaction: t });
    if (!row) throw ApiError.conflict('The record no longer exists on the server');
    const device = c.device_id ? await Device.findByPk(c.device_id, { transaction: t }) : null;
    const before = (await toWire(c.entity, [row], { transaction: t }))[0];

    let source = null;
    if (resolution === 'KEEP_DEVICE') source = typeof c.local_data === 'string' ? JSON.parse(c.local_data) : c.local_data;
    else if (resolution === 'MERGED') {
      if (!data || typeof data !== 'object') throw ApiError.badRequest('data is required for MERGED');
      source = { ...before, ...data };
    }
    const nextVersion = row.sync_version + 1;
    if (source) {
      const attrs = await fromWire(c.entity, source, { transaction: t, device: device ?? { branch_id: row.branch_id } });
      await Model.update({ ...attrs, sync_status: 'SYNCED', sync_version: nextVersion, deleted_at: source.deleted_at ? new Date(source.deleted_at) : null },
        { where: { id: row.id }, paranoid: false, hooks: false, transaction: t });
    } else {
      await Model.update({ sync_status: 'SYNCED', sync_version: nextVersion }, { where: { id: row.id }, paranoid: false, hooks: false, transaction: t });
    }
    const status = { KEEP_SERVER: 'RESOLVED_SERVER', KEEP_DEVICE: 'RESOLVED_LOCAL', MERGED: 'RESOLVED_MERGED' }[resolution];
    await SyncConflict.update({ status, resolved_by_id: req.auth.user.id, resolved_at: new Date(), resolution_note: note ?? null },
      { where: { entity: c.entity, entity_uuid: c.entity_uuid, status: 'OPEN' }, transaction: t });
    const fresh = await Model.findOne({ where: { id: row.id }, paranoid: false, transaction: t });
    const after = (await toWire(c.entity, [fresh], { transaction: t }))[0];
    await audit(req, 'sync.conflict_resolved', { entityType: c.entity, entityUuid: c.entity_uuid, oldValues: before, newValues: { resolution, note, after }, transaction: t });
    return { uuid, status, record: after };
  });
}

export async function openConflictCount(deviceId) {
  return SyncConflict.count({ where: { status: 'OPEN', ...(deviceId ? { device_id: deviceId } : {}) } });
}
export const since24h = () => ({ [Op.gte]: new Date(Date.now() - 86400000) });
