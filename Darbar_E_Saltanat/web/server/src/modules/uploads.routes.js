import { Router } from 'express';
import multer from 'multer';
import fs from 'node:fs/promises';
import path from 'node:path';
import crypto from 'node:crypto';
import { env } from '../config/env.js';
import { ApiError, asyncHandler } from '../utils/errors.js';
import { created } from '../utils/http.js';
import { authenticate, authorize } from '../middleware/auth.js';
import { PERMISSIONS as P } from '../security/permissions.js';
import { audit } from './audit.service.js';

const upload = multer({ storage: multer.memoryStorage(), limits: { fileSize: env.MAX_UPLOAD_MB * 1024 * 1024, files: 1 } });

/** Detect type from magic bytes — never trust the client's file name or Content-Type. */
export function sniffImage(buf) {
  if (buf.length > 3 && buf[0] === 0xff && buf[1] === 0xd8 && buf[2] === 0xff) return 'jpg';
  if (buf.length > 8 && buf.subarray(0, 8).equals(Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]))) return 'png';
  if (buf.length > 12 && buf.subarray(0, 4).toString('ascii') === 'RIFF' && buf.subarray(8, 12).toString('ascii') === 'WEBP') return 'webp';
  return null;
}

const r = Router();
r.post('/images', authenticate, authorize(P.MENU_MANAGE), upload.single('image'), asyncHandler(async (req, res) => {
  if (!req.file) throw ApiError.badRequest('Send the image in a multipart field named "image"');
  const ext = sniffImage(req.file.buffer);
  if (!ext) throw ApiError.badRequest('Only JPEG, PNG or WebP images are allowed');
  const dir = path.resolve(env.UPLOAD_DIR, 'images');
  await fs.mkdir(dir, { recursive: true });
  const name = `${crypto.randomUUID()}.${ext}`;
  await fs.writeFile(path.join(dir, name), req.file.buffer, { flag: 'wx' });
  const url = `/uploads/images/${name}`;
  await audit(req, 'uploads.image', { newValues: { url, bytes: req.file.size } });
  created(res, { url, bytes: req.file.size, type: ext });
}));
export default r;
