export class ApiError extends Error {
  constructor(status, code, message, details) {
    super(message);
    this.status = status; this.code = code; this.details = details;
  }
  static badRequest(msg, details) { return new ApiError(400, 'BAD_REQUEST', msg, details); }
  static unauthorized(msg = 'Authentication required') { return new ApiError(401, 'UNAUTHORIZED', msg); }
  static forbidden(msg = 'You do not have permission to perform this action') { return new ApiError(403, 'FORBIDDEN', msg); }
  static notFound(what = 'Resource') { return new ApiError(404, 'NOT_FOUND', `${what} not found`); }
  static conflict(msg, details) { return new ApiError(409, 'CONFLICT', msg, details); }
  static locked(msg) { return new ApiError(423, 'LOCKED', msg); }
}
export const asyncHandler = (fn) => (req, res, next) => Promise.resolve(fn(req, res, next)).catch(next);
