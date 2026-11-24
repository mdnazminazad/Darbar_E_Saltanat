import { Router } from 'express';
import { z } from 'zod';
import { Op } from 'sequelize';
import { sequelize } from '../../db/models/index.js';
import { modelFor } from '../../db/models/registryModels.js';
import { ApiError, asyncHandler } from '../../utils/errors.js';
import { ok } from '../../utils/http.js';
import { validate } from '../../middleware/validate.js';
import { authenticate, authorize } from '../../middleware/auth.js';
import { PERMISSIONS as P } from '../../security/permissions.js';
import { crudRouter, byParentUuid, byBool, byEnum } from '../crud/crudRouter.js';
import { serializeRows, serializeRow } from '../crud/serializer.js';
import { audit } from '../audit.service.js';
import * as S from './schemas.js';
import { evaluateCoupon } from './couponRules.js';

const M = (t) => modelFor(t);

export const categoriesRouter = crudRouter({
  table: 'categories', read: P.MENU_VIEW, write: P.MENU_MANAGE, create: S.category,
  search: ['name'], sortable: ['sort_order', 'name', 'created_at'], defaultSort: 'sort_order',
  filters: { is_active: byBool('is_active') },
  beforeDelete: async (row, t) => {
    const n = await M('menu_items').count({ where: { category_id: row.id }, transaction: t });
    if (n) throw ApiError.conflict(`Category still has ${n} menu item(s); move or delete them first`);
  },
});

/** Only one default variant per item. */
async function enforceSingleDefault(variant, t) {
  if (!variant.is_default) return;
  await M('item_variants').update({ is_default: false },
    { where: { menu_item_id: variant.menu_item_id, id: { [Op.ne]: variant.id }, is_default: true }, transaction: t, individualHooks: true });
}

export const menuItemsRouter = crudRouter({
  table: 'menu_items', read: P.MENU_VIEW, write: P.MENU_MANAGE, create: S.menuItem,
  search: ['name', 'name_bn', 'sku', 'barcode'], sortable: ['sort_order', 'name', 'selling_price', 'created_at'], defaultSort: 'sort_order',
  filters: { category_uuid: byParentUuid('category', 'categories'), is_available: byBool('is_available'), is_active: byBool('is_active'), sku: (v) => ({ sku: v }), barcode: (v) => ({ barcode: v }) },
  beforeWrite: async (data, existing, t) => {
    for (const key of ['sku', 'barcode']) {
      if (!data[key]) continue;
      const clash = await M('menu_items').findOne({ where: { [key]: data[key], ...(existing ? { id: { [Op.ne]: existing.id } } : {}) }, transaction: t });
      if (clash) throw ApiError.conflict(`Another item already uses ${key.toUpperCase()} ${data[key]}`);
    }
  },
});

/** GET /menu-items/:uuid/full — item with variants and allowed add-ons (used by POS and portal editors). */
menuItemsRouter.get('/:uuid/full', authenticate, authorize(P.MENU_VIEW), validate({ params: z.object({ uuid: z.string().uuid() }) }), asyncHandler(async (req, res) => {
  const item = await M('menu_items').findOne({ where: { uuid: req.params.uuid } });
  if (!item) throw ApiError.notFound('Menu item');
  ok(res, await fullItem(item));
}));

/** PUT /menu-items/:uuid/addons { addons: [{ addon_uuid, max_qty }] } — replaces the item's add-on list. */
menuItemsRouter.put('/:uuid/addons', authenticate, authorize(P.MENU_MANAGE), validate({
  params: z.object({ uuid: z.string().uuid() }),
  body: z.object({ addons: z.array(z.object({ addon_uuid: z.string().uuid(), max_qty: z.number().int().min(1).max(20).default(1) })).max(100) }),
}), asyncHandler(async (req, res) => {
  const item = await M('menu_items').findOne({ where: { uuid: req.params.uuid } });
  if (!item) throw ApiError.notFound('Menu item');
  await sequelize.transaction(async (t) => {
    const addons = await M('item_addons').findAll({ where: { uuid: req.body.addons.map((a) => a.addon_uuid) }, transaction: t });
    if (addons.length !== new Set(req.body.addons.map((a) => a.addon_uuid)).size) throw ApiError.badRequest('Unknown addon_uuid in list');
    const wanted = new Map(req.body.addons.map((a) => [addons.find((x) => x.uuid === a.addon_uuid).id, a.max_qty]));
    const links = await M('item_addon_mappings').findAll({ where: { menu_item_id: item.id }, paranoid: false, transaction: t });
    for (const l of links) {
      if (wanted.has(l.addon_id)) {
        if (l.deleted_at) await l.restore({ transaction: t });
        if (l.max_qty !== wanted.get(l.addon_id)) await l.update({ max_qty: wanted.get(l.addon_id) }, { transaction: t });
        wanted.delete(l.addon_id);
      } else if (!l.deleted_at) await l.destroy({ transaction: t });
    }
    for (const [addonId, maxQty] of wanted) await M('item_addon_mappings').create({ menu_item_id: item.id, addon_id: addonId, max_qty: maxQty, branch_id: item.branch_id }, { transaction: t });
    await audit(req, 'menu_items.addons_update', { entityType: 'menu_items', entityUuid: item.uuid, newValues: req.body, transaction: t });
  });
  ok(res, await fullItem(item));
}));

async function fullItem(item) {
  const [variants, links] = await Promise.all([
    M('item_variants').findAll({ where: { menu_item_id: item.id }, order: [['price', 'ASC']] }),
    M('item_addon_mappings').findAll({ where: { menu_item_id: item.id } }),
  ]);
  const addons = links.length ? await M('item_addons').findAll({ where: { id: links.map((l) => l.addon_id) } }) : [];
  const addonOut = await serializeRows('item_addons', addons);
  return {
    ...(await serializeRow('menu_items', item)),
    variants: await serializeRows('item_variants', variants),
    addons: addonOut.map((a) => ({ ...a, max_qty: links.find((l) => addons.find((x) => x.uuid === a.uuid).id === l.addon_id).max_qty })),
  };
}

export const variantsRouter = crudRouter({
  table: 'item_variants', read: P.MENU_VIEW, write: P.MENU_MANAGE, create: S.variant,
  filters: { menu_item_uuid: byParentUuid('menu_item', 'menu_items') }, sortable: ['price', 'name'], defaultSort: 'price',
  afterWrite: (row, t) => enforceSingleDefault(row, t),
});
export const addonsRouter = crudRouter({
  table: 'item_addons', read: P.MENU_VIEW, write: P.MENU_MANAGE, create: S.addon,
  search: ['name', 'group_name'], sortable: ['name', 'price'], defaultSort: 'name',
  beforeDelete: async (row, t) => { await M('item_addon_mappings').destroy({ where: { addon_id: row.id }, transaction: t }); },
});

export const tablesRouter = crudRouter({
  table: 'restaurant_tables', read: P.ORDERS_VIEW, write: P.TABLES_MANAGE, create: S.table,
  search: ['name', 'section'], sortable: ['name', 'capacity', 'section'], defaultSort: 'name',
  filters: { status: byEnum('status'), section: (v) => ({ section: v }) },
  beforeDelete: async (row) => {
    if (['OCCUPIED', 'BILLING'].includes(row.status)) throw ApiError.conflict('Table is in use; close its order first');
  },
});
tablesRouter.patch('/:uuid/status', authenticate, authorize(P.TABLES_MANAGE), validate({
  params: z.object({ uuid: z.string().uuid() }), body: z.object({ status: z.enum(S.TABLE_STATUSES) }),
}), asyncHandler(async (req, res) => {
  const row = await M('restaurant_tables').findOne({ where: { uuid: req.params.uuid } });
  if (!row) throw ApiError.notFound('Table');
  const before = row.status;
  await row.update({ status: req.body.status });
  await audit(req, 'restaurant_tables.status', { entityType: 'restaurant_tables', entityUuid: row.uuid, oldValues: { status: before }, newValues: req.body });
  ok(res, await serializeRow('restaurant_tables', row));
}));

export const discountsRouter = crudRouter({
  table: 'discounts', read: P.DISCOUNTS_APPLY, write: P.COUPONS_MANAGE, create: S.discount, update: S.discountUpdate,
  search: ['name'], sortable: ['name', 'value'], defaultSort: 'name', filters: { is_active: byBool('is_active') },
});

export const couponsRouter = crudRouter({
  table: 'coupons', read: P.COUPONS_MANAGE, write: P.COUPONS_MANAGE, create: S.coupon,
  update: S.couponUpdate,
  search: ['code', 'description'], sortable: ['code', 'created_at', 'ends_at'], defaultSort: 'code', filters: { is_active: byBool('is_active') },
  beforeWrite: async (data, existing, t) => {
    if (!data.code) return;
    const clash = await M('coupons').findOne({ where: { code: data.code, ...(existing ? { id: { [Op.ne]: existing.id } } : {}) }, paranoid: false, transaction: t });
    if (clash) throw ApiError.conflict(`Coupon code ${data.code} already exists`);
  },
});

/** POST /coupons/validate { code, subtotal, customer_uuid? } — usable by POS staff and (Phase 5) online checkout. */
export const couponValidateRouter = Router();
couponValidateRouter.post('/validate', authenticate, authorize(P.DISCOUNTS_APPLY), validate({
  body: z.object({ code: z.string().trim().toUpperCase().min(1).max(40), subtotal: S.money, customer_uuid: z.string().uuid().nullable().optional() }),
}), asyncHandler(async (req, res) => {
  const coupon = await M('coupons').findOne({ where: { code: req.body.code } });
  let customerUsageCount = 0;
  let customer = null;
  if (req.body.customer_uuid) {
    customer = await M('customers').findOne({ where: { uuid: req.body.customer_uuid } });
    if (!customer) throw ApiError.badRequest('Unknown customer');
    if (coupon) customerUsageCount = await M('coupon_usages').count({ where: { coupon_id: coupon.id, customer_id: customer.id } });
  }
  const usedCount = coupon ? await M('coupon_usages').count({ where: { coupon_id: coupon.id } }) : 0;
  const result = evaluateCoupon(coupon ? { ...coupon.get({ plain: true }), used_count: Math.max(coupon.used_count, usedCount) } : null,
    { subtotal: req.body.subtotal, customerUsageCount, hasCustomer: !!customer });
  ok(res, { ...result, ...(result.discount_amount !== undefined ? { discount_amount: result.discount_amount.toFixed(2) } : {}), coupon_uuid: coupon?.uuid ?? null });
}));

export const customersRouter = crudRouter({
  table: 'customers', read: P.CUSTOMERS_VIEW, write: P.CUSTOMERS_MANAGE, create: S.customer,
  search: ['name', 'phone', 'email'], sortable: ['name', 'created_at', 'loyalty_points'], defaultSort: 'name',
  filters: { phone: (v) => ({ phone: S.normalizeBdPhone(v) }), is_active: byBool('is_active') },
  beforeWrite: async (data, existing, t) => {
    if (!data.phone) return;
    const clash = await M('customers').findOne({ where: { phone: data.phone, ...(existing ? { id: { [Op.ne]: existing.id } } : {}) }, transaction: t });
    if (clash) throw ApiError.conflict(`A customer with phone ${data.phone} already exists`);
  },
});

/** GET /public/menu — anonymous menu for the website (active categories, available items, variants). */
export const publicMenuRouter = Router();
publicMenuRouter.get('/menu', asyncHandler(async (_req, res) => {
  const cats = await M('categories').findAll({ where: { is_active: true }, order: [['sort_order', 'ASC']] });
  const items = await M('menu_items').findAll({ where: { is_active: true }, order: [['sort_order', 'ASC'], ['name', 'ASC']] });
  const variants = await M('item_variants').findAll({ where: { is_active: true } });
  const [catOut, itemOut, varOut] = await Promise.all([serializeRows('categories', cats), serializeRows('menu_items', items), serializeRows('item_variants', variants)]);
  const strip = ({ cost_price, sync_version, device_id, sync_status, deleted_at, ...rest }) => rest;
  ok(res, catOut.map((c) => ({
    uuid: c.uuid, name: c.name, description: c.description, image_url: c.image_url,
    items: itemOut.filter((i) => i.category_uuid === c.uuid).map((i) => ({ ...strip(i), variants: varOut.filter((v) => v.menu_item_uuid === i.uuid).map(strip) })),
  })));
}));
