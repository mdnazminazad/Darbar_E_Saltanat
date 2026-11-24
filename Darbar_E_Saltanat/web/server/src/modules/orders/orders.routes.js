import { Router } from 'express';
import { z } from 'zod';
import { Op } from 'sequelize';
import { sequelize, Branch, User } from '../../db/models/index.js';
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

/** Helper to generate next branch order number: e.g. WEB-0001 */
async function nextOrderNo(branchId, t) {
  const count = await M('orders').count({ where: { branch_id: branchId }, transaction: t });
  return `W-${String(count + 1).padStart(5, '0')}`;
}

r.get('/', authorize(P.ORDERS_VIEW), asyncHandler(async (req, res) => {
  const { status, order_type, source, limit = 50, offset = 0 } = req.query;
  const where = {};
  if (status) where.status = status;
  if (order_type) where.order_type = order_type;
  if (source) where.source = source;

  const orders = await M('orders').findAll({
    where,
    order: [['order_date', 'DESC'], ['id', 'DESC']],
    limit: Number(limit),
    offset: Number(offset),
  });

  const orderIds = orders.map((o) => o.id);
  const items = orderIds.length
    ? await M('order_items').findAll({ where: { order_id: { [Op.in]: orderIds } } })
    : [];

  const tableIds = [...new Set(orders.map((o) => o.table_id).filter(Boolean))];
  const tables = tableIds.length
    ? await M('restaurant_tables').findAll({ where: { id: { [Op.in]: tableIds } } })
    : [];
  const tableMap = new Map(tables.map((t) => [t.id, t.name]));

  const data = orders.map((o) => ({
    uuid: o.uuid,
    order_no: o.order_no,
    order_type: o.order_type,
    source: o.source,
    status: o.status,
    payment_status: o.payment_status,
    table_name: o.table_id ? tableMap.get(o.table_id) ?? null : null,
    guest_count: o.guest_count,
    subtotal: o.subtotal,
    discount_amount: o.discount_amount,
    service_charge_amount: o.service_charge_amount,
    vat_amount: o.vat_amount,
    total: o.total,
    paid_amount: o.paid_amount,
    due_amount: o.due_amount,
    notes: o.notes,
    order_date: o.order_date,
    created_at: o.created_at,
    items_count: items.filter((i) => i.order_id === o.id).length,
  }));

  ok(res, data);
}));

r.post('/', authorize(P.ORDERS_CREATE), validate({
  body: z.object({
    order_type: z.enum(['DINE_IN', 'TAKEAWAY', 'DELIVERY']).default('DINE_IN'),
    table_uuid: z.string().uuid().nullable().optional(),
    customer_uuid: z.string().uuid().nullable().optional(),
    guest_count: z.number().int().min(1).default(1),
    notes: z.string().nullable().optional(),
    items: z.array(z.object({
      menu_item_uuid: z.string().uuid(),
      variant_uuid: z.string().uuid().nullable().optional(),
      name: z.string().min(1),
      quantity: z.number().min(1).default(1),
      unit_price: z.number().min(0),
      notes: z.string().nullable().optional(),
      addons: z.array(z.object({
        addon_uuid: z.string().uuid(),
        name: z.string(),
        price: z.number().min(0),
        quantity: z.number().min(1).default(1),
      })).optional(),
    })).min(1, 'Order must contain at least one item'),
    discount_amount: z.number().min(0).default(0),
    service_charge_amount: z.number().min(0).default(0),
    vat_amount: z.number().min(0).default(0),
  }),
}), asyncHandler(async (req, res) => {
  const user = req.auth.user;
  const branchId = user.branch_id || (await Branch.findOne())?.id;
  if (!branchId) throw ApiError.badRequest('No branch associated');

  const createdOrder = await sequelize.transaction(async (t) => {
    let tableId = null;
    if (req.body.table_uuid) {
      const tbl = await M('restaurant_tables').findOne({ where: { uuid: req.body.table_uuid }, transaction: t });
      if (tbl) {
        tableId = tbl.id;
        await tbl.update({ status: 'OCCUPIED' }, { transaction: t });
      }
    }

    let customerId = null;
    if (req.body.customer_uuid) {
      const cust = await M('customers').findOne({ where: { uuid: req.body.customer_uuid }, transaction: t });
      if (cust) customerId = cust.id;
    }

    const orderNo = await nextOrderNo(branchId, t);
    let subtotal = 0;
    for (const item of req.body.items) {
      let lineTotal = item.unit_price * item.quantity;
      if (item.addons) {
        for (const a of item.addons) lineTotal += a.price * a.quantity;
      }
      subtotal += lineTotal;
    }

    const total = Math.max(0, subtotal - req.body.discount_amount + req.body.service_charge_amount + req.body.vat_amount);

    const order = await M('orders').create({
      order_no: orderNo,
      order_type: req.body.order_type,
      source: 'WEB', // Explicitly marked as WEB fallback creation
      status: 'OPEN',
      payment_status: 'UNPAID',
      branch_id: branchId,
      table_id: tableId,
      customer_id: customerId,
      cashier_id: user.id,
      guest_count: req.body.guest_count,
      subtotal,
      discount_amount: req.body.discount_amount,
      service_charge_amount: req.body.service_charge_amount,
      vat_amount: req.body.vat_amount,
      total,
      paid_amount: 0,
      due_amount: total,
      notes: req.body.notes ?? null,
      order_date: new Date(),
    }, { transaction: t });

    for (const item of req.body.items) {
      let menuItemId = null;
      const mi = await M('menu_items').findOne({ where: { uuid: item.menu_item_uuid }, transaction: t });
      if (mi) menuItemId = mi.id;

      let variantId = null;
      if (item.variant_uuid) {
        const vi = await M('item_variants').findOne({ where: { uuid: item.variant_uuid }, transaction: t });
        if (vi) variantId = vi.id;
      }

      const orderItem = await M('order_items').create({
        order_id: order.id,
        menu_item_id: menuItemId,
        variant_id: variantId,
        branch_id: branchId,
        item_name: item.name,
        quantity: item.quantity,
        unit_price: item.unit_price,
        subtotal: item.unit_price * item.quantity,
        total: item.unit_price * item.quantity,
        status: 'PENDING',
        notes: item.notes ?? null,
      }, { transaction: t });

      if (item.addons && item.addons.length) {
        for (const a of item.addons) {
          let addonId = null;
          const ad = await M('item_addons').findOne({ where: { uuid: a.addon_uuid }, transaction: t });
          if (ad) addonId = ad.id;
          await M('order_item_addons').create({
            order_item_id: orderItem.id,
            addon_id: addonId,
            branch_id: branchId,
            addon_name: a.name,
            unit_price: a.price,
            quantity: a.quantity,
            total: a.price * a.quantity,
          }, { transaction: t });
        }
      }
    }

    await audit(req, 'orders.web_create', {
      entityType: 'orders',
      entityUuid: order.uuid,
      newValues: { order_no: order.order_no, total, source: 'WEB' },
      transaction: t,
    });

    return order;
  });

  created(res, {
    uuid: createdOrder.uuid,
    order_no: createdOrder.order_no,
    source: 'WEB',
    status: createdOrder.status,
    total: createdOrder.total,
    message: 'Order created via Web Fallback POS',
  });
}));

r.get('/:uuid', authorize(P.ORDERS_VIEW), validate({
  params: z.object({ uuid: z.string().uuid() }),
}), asyncHandler(async (req, res) => {
  const order = await M('orders').findOne({ where: { uuid: req.params.uuid } });
  if (!order) throw ApiError.notFound('Order');

  const items = await M('order_items').findAll({ where: { order_id: order.id } });
  const payments = await M('payments').findAll({ where: { order_id: order.id } });

  let tableName = null;
  if (order.table_id) {
    const tbl = await M('restaurant_tables').findByPk(order.table_id);
    tableName = tbl?.name ?? null;
  }

  ok(res, {
    ...order.get({ plain: true }),
    table_name: tableName,
    items: items.map((i) => i.get({ plain: true })),
    payments: payments.map((p) => p.get({ plain: true })),
  });
}));

r.post('/:uuid/pay', authorize(P.PAYMENTS_TAKE), validate({
  params: z.object({ uuid: z.string().uuid() }),
  body: z.object({
    method: z.enum(['CASH', 'CARD', 'BKASH', 'NAGAD', 'ROCKET', 'UPAY', 'CREDIT']),
    amount: z.number().min(0.01),
    tendered: z.number().min(0).optional(),
    change: z.number().min(0).optional(),
    transaction_ref: z.string().max(100).nullable().optional(),
    notes: z.string().max(255).nullable().optional(),
  }),
}), asyncHandler(async (req, res) => {
  const result = await sequelize.transaction(async (t) => {
    const order = await M('orders').findOne({ where: { uuid: req.params.uuid }, transaction: t });
    if (!order) throw ApiError.notFound('Order');
    if (order.payment_status === 'PAID') throw ApiError.badRequest('Order is already fully paid');

    const amount = Number(req.body.amount);
    const paidSoFar = Number(order.paid_amount || 0) + amount;
    const dueAmount = Math.max(0, Number(order.total) - paidSoFar);
    const newPaymentStatus = dueAmount <= 0 ? 'PAID' : 'PARTIAL';
    const newOrderStatus = dueAmount <= 0 ? 'COMPLETED' : order.status;

    const payment = await M('payments').create({
      order_id: order.id,
      branch_id: order.branch_id,
      method: req.body.method,
      amount,
      tendered: req.body.tendered ?? amount,
      change_amount: req.body.change ?? 0,
      transaction_ref: req.body.transaction_ref ?? null,
      notes: req.body.notes ?? null,
      status: 'COMPLETED',
      created_by: req.auth.user.id,
    }, { transaction: t });

    await order.update({
      paid_amount: paidSoFar,
      due_amount: dueAmount,
      payment_status: newPaymentStatus,
      status: newOrderStatus,
      completed_at: dueAmount <= 0 ? new Date() : null,
    }, { transaction: t });

    if (dueAmount <= 0 && order.table_id) {
      await M('restaurant_tables').update({ status: 'VACANT' }, { where: { id: order.table_id }, transaction: t });
    }

    await audit(req, 'payments.web_create', {
      entityType: 'payments',
      entityUuid: payment.uuid,
      newValues: { order_no: order.order_no, amount, method: req.body.method },
      transaction: t,
    });

    return { payment, order };
  });

  ok(res, {
    payment_uuid: result.payment.uuid,
    order_no: result.order.order_no,
    paid_amount: result.order.paid_amount,
    due_amount: result.order.due_amount,
    payment_status: result.order.payment_status,
  });
}));

r.post('/:uuid/cancel', authorize(P.ORDERS_VOID), validate({
  params: z.object({ uuid: z.string().uuid() }),
  body: z.object({ reason: z.string().min(3) }),
}), asyncHandler(async (req, res) => {
  await sequelize.transaction(async (t) => {
    const order = await M('orders').findOne({ where: { uuid: req.params.uuid }, transaction: t });
    if (!order) throw ApiError.notFound('Order');
    if (order.status === 'COMPLETED') throw ApiError.badRequest('Completed orders cannot be cancelled');

    await order.update({
      status: 'CANCELLED',
      void_reason: req.body.reason,
      voided_at: new Date(),
      voided_by_id: req.auth.user.id,
    }, { transaction: t });

    if (order.table_id) {
      await M('restaurant_tables').update({ status: 'VACANT' }, { where: { id: order.table_id }, transaction: t });
    }

    await audit(req, 'orders.cancel', { entityType: 'orders', entityUuid: order.uuid, newValues: req.body, transaction: t });
  });

  ok(res, { cancelled: true });
}));

r.post('/:uuid/kot', authorize(P.ORDERS_CREATE), validate({
  params: z.object({ uuid: z.string().uuid() }),
}), asyncHandler(async (req, res) => {
  const order = await M('orders').findOne({ where: { uuid: req.params.uuid } });
  if (!order) throw ApiError.notFound('Order');
  const items = await M('order_items').findAll({ where: { order_id: order.id } });

  const kotNumber = `KOT-${String(Date.now()).slice(-6)}`;
  await M('order_items').update({ status: 'SENT' }, { where: { order_id: order.id } });
  await order.update({ status: 'IN_KITCHEN' });

  ok(res, {
    kot_number: kotNumber,
    order_no: order.order_no,
    created_at: new Date().toISOString(),
    items: items.map((i) => ({ name: i.item_name, quantity: i.quantity, notes: i.notes })),
  });
}));

r.get('/:uuid/receipt', authorize(P.ORDERS_VIEW), validate({
  params: z.object({ uuid: z.string().uuid() }),
}), asyncHandler(async (req, res) => {
  const order = await M('orders').findOne({ where: { uuid: req.params.uuid } });
  if (!order) throw ApiError.notFound('Order');
  const items = await M('order_items').findAll({ where: { order_id: order.id } });
  const branch = await Branch.findByPk(order.branch_id);

  ok(res, {
    restaurant_name: 'Darbar_E_Saltanat',
    branch_name: branch?.name || 'Main Branch',
    order_no: order.order_no,
    order_date: order.order_date,
    source: order.source,
    items: items.map((i) => ({
      name: i.item_name,
      quantity: i.quantity,
      unit_price: i.unit_price,
      total: i.total,
    })),
    subtotal: order.subtotal,
    discount_amount: order.discount_amount,
    service_charge_amount: order.service_charge_amount,
    vat_amount: order.vat_amount,
    total: order.total,
    paid_amount: order.paid_amount,
    due_amount: order.due_amount,
    payment_status: order.payment_status,
  });
}));

export default r;
