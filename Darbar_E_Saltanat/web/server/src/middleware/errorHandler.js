import { ZodError } from 'zod';
import { UniqueConstraintError, ForeignKeyConstraintError, ValidationError } from 'sequelize';
import multer from 'multer';
import { ApiError } from '../utils/errors.js';
import { logger } from '../logger.js';

export function notFound(req, _res, next) {
  next(new ApiError(404, 'NOT_FOUND', `Route ${req.method} ${req.originalUrl} not found`));
}

// eslint-disable-next-line no-unused-vars
export function errorHandler(err, req, res, _next) {
  let e = err;
  if (err instanceof ZodError) {
    e = new ApiError(422, 'VALIDATION_ERROR', 'Validation failed',
      err.issues.map((i) => ({ field: i.path.join('.'), message: i.message })));
  } else if (err instanceof UniqueConstraintError) {
    e = ApiError.conflict('A record with the same unique value already exists',
      err.errors?.map((x) => ({ field: x.path, message: x.message })));
  } else if (err instanceof ForeignKeyConstraintError) {
    e = ApiError.conflict('Referenced record does not exist or is still in use');
  } else if (err instanceof ValidationError) {
    e = new ApiError(422, 'VALIDATION_ERROR', 'Validation failed', err.errors.map((x) => ({ field: x.path, message: x.message })));
  } else if (err instanceof multer.MulterError) {
    e = ApiError.badRequest(err.message);
  } else if (err?.type === 'entity.parse.failed') {
    e = ApiError.badRequest('Malformed JSON body');
  } else if (!(err instanceof ApiError)) {
    logger.error({ err, url: req.originalUrl }, 'Unhandled error');
    e = new ApiError(500, 'INTERNAL_ERROR', 'An unexpected error occurred');
  }
  res.status(e.status).json({ success: false, error: { code: e.code, message: e.message, ...(e.details ? { details: e.details } : {}) } });
}
