import { AuditLog } from '../db/models/index.js';
import { logger } from '../logger.js';

const STRIP = ['password_hash', 'pin_hash', 'api_key_hash'];
const clean = (o) => (o ? Object.fromEntries(Object.entries(o).filter(([k]) => !STRIP.includes(k))) : null);

/** Records a sensitive action. Audit failures are logged but never break the business action. */
export async function audit(req, action, { entityType, entityUuid, oldValues, newValues, transaction } = {}) {
  try {
    await AuditLog.create({
      user_id: req?.auth?.user?.id ?? null,
      branch_id: req?.auth?.user?.branch_id ?? null,
      action, entity_type: entityType ?? null, entity_uuid: entityUuid ?? null,
      old_values: clean(oldValues), new_values: clean(newValues),
      ip_address: req?.ip?.slice(0, 45) ?? null, user_agent: req?.get?.('user-agent')?.slice(0, 255) ?? null,
      occurred_at: new Date(),
    }, { transaction });
  } catch (err) {
    logger.error({ err, action }, 'Failed to write audit log');
  }
}
