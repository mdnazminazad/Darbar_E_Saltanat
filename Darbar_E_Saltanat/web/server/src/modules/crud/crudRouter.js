import { Router } from 'express';
import { z } from 'zod';
import { Op } from 'sequelize';
import { sequelize } from '../../db/models/index.js';
import { modelFor, registry } from '../../db/models/registryModels.js';
import { ApiError, asyncHandler } from '../../utils/errors.js';
import { ok, created, listParams, pageMeta } from '../../utils/http.js';
import { validate } from '../../middleware/validate.js';
import { authenticate, authorize } from '../../middleware/auth.js';
import { audit } from '../audit.service.js';
import { serializeRows, serializeRow, resolveRefs, defaultBranchIdFor } from './serializer.js';

const uuidParam = z.object({ uuid: z.string().uuid() });

/**
 * Generic REST resource over a registry table.
 * opts: { table, read, write, create (zod), update (zod, defaults to create.partial()), search: [cols],
 *         filters: { queryKey: (value) => whereFragment | Promise<whereFragment> }, sortable, defaultSort,
 *         beforeDelete(row, t), afterWrite(row, t, req), publicRead }
 */
export function crudRouter(opts) {
  const { table } = opts;
  const Model = modelFor(table);
  const r = Router();
  const readGuard = opts.publicRead ? [] : [authenticate, ...(opts.read ? [authorize(opts.read)] : [])];
  const writeGuard = [authenticate, authorize(opts.write)];
  const updateSchema = opts.update ?? opts.create.partial().refine((o) => Object.keys(o).length > 0, 'Nothing to update');
  const sortable = opts.sortable ?? ['created_at', 'updated_at'];

  async function find(uuid, transaction) {
    const row = await Model.findOne({ where: { uuid }, transaction });
    if (!row) throw ApiError.notFound(table.replace(/_/g, ' '));
    return row;
  }

  r.get('/', ...readGuard, asyncHandler(async (req, res) => {
    const p = listParams(req.query, sortable, opts.defaultSort ?? 'created_at');
    if (opts.defaultSort && !req.query.sort) p.order = [[opts.defaultSort, opts.defaultOrder ?? 'ASC']];
    const where = {};
    if (req.query.search && opts.search?.length) {
      where[Op.or] = opts.search.map((c) => ({ [c]: { [Op.like]: `%${String(req.query.search).slice(0, 100)}%` } }));
    }
    for (const [key, build] of Object.entries(opts.filters ?? {})) {
      if (req.query[key] !== undefined && req.query[key] !== '') Object.assign(where, await build(String(req.query[key])));
    }
    const { rows, count } = await Model.findAndCountAll({ where, limit: p.limit, offset: p.offset, order: p.order });
    ok(res, await serializeRows(table, rows), pageMeta(p, count));
  }));

  r.get('/:uuid', ...readGuard, validate({ params: uuidParam }), asyncHandler(async (req, res) => {
    ok(res, await serializeRow(table, await find(req.params.uuid)));
  }));

  r.post('/', ...writeGuard, validate({ body: opts.create }), asyncHandler(async (req, res) => {
    const row = await sequelize.transaction(async (t) => {
      const data = await resolveRefs(table, req.body, { transaction: t, defaultBranchId: await defaultBranchIdFor(req) });
      if (opts.beforeWrite) await opts.beforeWrite(data, null, t);
      const created_ = await Model.create(data, { transaction: t });
      if (opts.afterWrite) await opts.afterWrite(created_, t, req);
      await audit(req, `${table}.create`, { entityType: table, entityUuid: created_.uuid, newValues: req.body, transaction: t });
      return created_;
    });
    created(res, await serializeRow(table, await Model.findByPk(row.id)));
  }));

  r.patch('/:uuid', ...writeGuard, validate({ params: uuidParam, body: updateSchema }), asyncHandler(async (req, res) => {
    const row = await sequelize.transaction(async (t) => {
      const existing = await find(req.params.uuid, t);
      const before = existing.get({ plain: true });
      const data = await resolveRefs(table, req.body, { transaction: t });
      if (opts.beforeWrite) await opts.beforeWrite(data, existing, t);
      await existing.update(data, { transaction: t });
      if (opts.afterWrite) await opts.afterWrite(existing, t, req);
      await audit(req, `${table}.update`, { entityType: table, entityUuid: existing.uuid, oldValues: before, newValues: req.body, transaction: t });
      return existing;
    });
    ok(res, await serializeRow(table, await Model.findByPk(row.id)));
  }));

  r.delete('/:uuid', ...writeGuard, validate({ params: uuidParam }), asyncHandler(async (req, res) => {
    await sequelize.transaction(async (t) => {
      const row = await find(req.params.uuid, t);
      if (opts.beforeDelete) await opts.beforeDelete(row, t);
      await row.destroy({ transaction: t }); // soft delete
      await audit(req, `${table}.delete`, { entityType: table, entityUuid: row.uuid, transaction: t });
    });
    ok(res, { deleted: true });
  }));

  r.meta = registry[table];
  return r;
}

/** Filter helper: ?category_uuid=… → { category_id: <id> }. */
export const byParentUuid = (fkName, refTable) => async (value) => {
  const parent = await modelFor(refTable).findOne({ where: { uuid: value }, attributes: ['id'] });
  return { [`${fkName}_id`]: parent ? parent.id : -1 };
};
export const byBool = (column) => (value) => ({ [column]: value === 'true' || value === '1' });
export const byEnum = (column) => (value) => ({ [column]: value.split(',') });
