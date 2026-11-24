import { Router } from 'express';
import { z } from 'zod';
import { Op } from 'sequelize';
import { sequelize, Branch } from '../../db/models/index.js';
import { modelFor } from '../../db/models/registryModels.js';
import { ApiError, asyncHandler } from '../../utils/errors.js';
import { ok, created } from '../../utils/http.js';
import { validate } from '../../middleware/validate.js';
import { authenticate, authorize } from '../../middleware/auth.js';
import { PERMISSIONS as P } from '../../security/permissions.js';
import { audit } from '../audit.service.js';

const r = Router();
r.use(authenticate);

const M = (t) => modelFor(t);

r.get('/stock', authorize(P.INVENTORY_VIEW), asyncHandler(async (req, res) => {
  const ingredients = await M('ingredients').findAll({
    order: [['name', 'ASC']],
  });
  ok(res, ingredients);
}));

r.get('/movements', authorize(P.INVENTORY_VIEW), asyncHandler(async (req, res) => {
  const { limit = 50 } = req.query;
  const movements = await M('stock_movements').findAll({
    order: [['created_at', 'DESC']],
    limit: Number(limit),
  });
  ok(res, movements);
}));

r.post('/movements', authorize(P.STOCK_ADJUST), validate({
  body: z.object({
    ingredient_uuid: z.string().uuid(),
    movement_type: z.enum(['IN', 'OUT', 'WASTAGE', 'ADJUSTMENT']),
    quantity: z.number().positive(),
    unit_cost: z.number().min(0).default(0),
    reason: z.string().max(255).nullable().optional(),
    reference_no: z.string().max(60).nullable().optional(),
  }),
}), asyncHandler(async (req, res) => {
  const ingredient = await M('ingredients').findOne({ where: { uuid: req.body.ingredient_uuid } });
  if (!ingredient) throw ApiError.notFound('Ingredient');

  const movement = await sequelize.transaction(async (t) => {
    let newStock = Number(ingredient.current_stock || 0);
    const qty = Number(req.body.quantity);
    if (['IN'].includes(req.body.movement_type)) {
      newStock += qty;
    } else {
      newStock = Math.max(0, newStock - qty);
    }

    await ingredient.update({ current_stock: newStock }, { transaction: t });

    const m = await M('stock_movements').create({
      ingredient_id: ingredient.id,
      branch_id: ingredient.branch_id,
      movement_type: req.body.movement_type,
      quantity: qty,
      unit_cost: req.body.unit_cost,
      total_cost: qty * req.body.unit_cost,
      reason: req.body.reason ?? null,
      reference_no: req.body.reference_no ?? null,
      created_by: req.auth.user.id,
    }, { transaction: t });

    await audit(req, 'stock.movement', {
      entityType: 'stock_movements',
      entityUuid: m.uuid,
      newValues: { ingredient: ingredient.name, type: req.body.movement_type, quantity: qty, new_stock: newStock },
      transaction: t,
    });

    return m;
  });

  created(res, {
    uuid: movement.uuid,
    ingredient_name: ingredient.name,
    current_stock: ingredient.current_stock,
    message: 'Stock updated successfully',
  });
}));

export default r;
