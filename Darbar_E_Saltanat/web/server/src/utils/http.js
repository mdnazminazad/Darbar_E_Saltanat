export const ok = (res, data, meta, status = 200) => res.status(status).json(meta ? { success: true, data, meta } : { success: true, data });
export const created = (res, data) => ok(res, data, undefined, 201);

/** Parses ?page&limit&sort&order with a whitelist of sortable columns. */
export function listParams(query, sortable, defaultSort = 'created_at') {
  const page = Math.max(1, parseInt(query.page, 10) || 1);
  const limit = Math.min(100, Math.max(1, parseInt(query.limit, 10) || 20));
  const sort = sortable.includes(query.sort) ? query.sort : defaultSort;
  const order = String(query.order).toLowerCase() === 'asc' ? 'ASC' : 'DESC';
  return { page, limit, offset: (page - 1) * limit, order: [[sort, order]] };
}
export const pageMeta = ({ page, limit }, total) => ({ page, limit, total, total_pages: Math.ceil(total / limit) });
