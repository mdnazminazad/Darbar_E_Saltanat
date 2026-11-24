import { Router } from 'express';
import { Op } from 'sequelize';
import { modelFor } from '../../db/models/registryModels.js';
import { asyncHandler } from '../../utils/errors.js';
import { ok } from '../../utils/http.js';
import { authenticate, authorize } from '../../middleware/auth.js';
import { PERMISSIONS as P } from '../../security/permissions.js';

const r = Router();
r.use(authenticate, authorize(P.REPORTS_VIEW));

const M = (t) => modelFor(t);

r.get('/summary', asyncHandler(async (req, res) => {
  const todayStart = new Date();
  todayStart.setHours(0, 0, 0, 0);

  const orders = await M('orders').findAll({
    where: {
      order_date: { [Op.gte]: todayStart },
      status: { [Op.ne]: 'CANCELLED' },
    },
  });

  const totalSales = orders.reduce((sum, o) => sum + Number(o.total || 0), 0);
  const netSubtotal = orders.reduce((sum, o) => sum + Number(o.subtotal || 0), 0);
  const totalVat = orders.reduce((sum, o) => sum + Number(o.vat_amount || 0), 0);
  const totalServiceCharge = orders.reduce((sum, o) => sum + Number(o.service_charge_amount || 0), 0);
  const totalDiscounts = orders.reduce((sum, o) => sum + Number(o.discount_amount || 0), 0);

  const payments = await M('payments').findAll({
    where: {
      created_at: { [Op.gte]: todayStart },
      status: 'COMPLETED',
    },
  });

  const paymentMix = {
    CASH: payments.filter((p) => p.method === 'CASH').reduce((sum, p) => sum + Number(p.amount || 0), 0),
    CARD: payments.filter((p) => p.method === 'CARD').reduce((sum, p) => sum + Number(p.amount || 0), 0),
    BKASH: payments.filter((p) => p.method === 'BKASH').reduce((sum, p) => sum + Number(p.amount || 0), 0),
    NAGAD: payments.filter((p) => p.method === 'NAGAD').reduce((sum, p) => sum + Number(p.amount || 0), 0),
    ROCKET: payments.filter((p) => p.method === 'ROCKET').reduce((sum, p) => sum + Number(p.amount || 0), 0),
    UPAY: payments.filter((p) => p.method === 'UPAY').reduce((sum, p) => sum + Number(p.amount || 0), 0),
    CREDIT: payments.filter((p) => p.method === 'CREDIT').reduce((sum, p) => sum + Number(p.amount || 0), 0),
  };

  const completedOrders = orders.filter((o) => o.status === 'COMPLETED').length;
  const openOrders = orders.filter((o) => ['OPEN', 'IN_KITCHEN', 'BILLED'].includes(o.status)).length;
  const avgOrder = completedOrders > 0 ? (totalSales / completedOrders).toFixed(2) : '0.00';

  ok(res, {
    date: new Date().toISOString().slice(0, 10),
    total_sales: totalSales.toFixed(2),
    net_subtotal: netSubtotal.toFixed(2),
    total_vat: totalVat.toFixed(2),
    total_service_charge: totalServiceCharge.toFixed(2),
    total_discounts: totalDiscounts.toFixed(2),
    completed_orders: completedOrders,
    open_orders: openOrders,
    average_order_value: avgOrder,
    payment_mix: paymentMix,
  });
}));

r.get('/export', authorize(P.REPORTS_EXPORT), asyncHandler(async (_req, res) => {
  const orders = await M('orders').findAll({
    order: [['order_date', 'DESC']],
    limit: 100,
  });
  ok(res, {
    exported_at: new Date().toISOString(),
    record_count: orders.length,
    rows: orders.map((o) => ({
      order_no: o.order_no,
      order_type: o.order_type,
      source: o.source,
      status: o.status,
      payment_status: o.payment_status,
      total: o.total,
      paid_amount: o.paid_amount,
      order_date: o.order_date,
    })),
  });
}));

export default r;
