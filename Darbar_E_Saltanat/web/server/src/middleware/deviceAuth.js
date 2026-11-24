import { Device } from '../db/models/index.js';
import { verifySecret } from '../security/passwords.js';
import { ApiError } from '../utils/errors.js';

/** Authenticates a desktop installation by device id + device token (header or body). */
export async function deviceAuth(req, _res, next) {
  try {
    const id = req.get('x-device-id') || req.body?.device_id || req.query?.device_id;
    const token = req.get('x-device-token') || req.body?.device_token;
    if (!id || !token) throw new ApiError(401, 'DEVICE_UNAUTHORIZED', 'Device credentials required');
    const device = await Device.findOne({ where: { uuid: String(id) } });
    if (!device || !(await verifySecret(String(token), device.api_key_hash))) {
      throw new ApiError(401, 'DEVICE_UNAUTHORIZED', 'Invalid device credentials');
    }
    if (device.status !== 'ACTIVE') throw new ApiError(403, 'DEVICE_REVOKED', 'This device has been revoked');
    req.device = device;
    next();
  } catch (e) { next(e); }
}
