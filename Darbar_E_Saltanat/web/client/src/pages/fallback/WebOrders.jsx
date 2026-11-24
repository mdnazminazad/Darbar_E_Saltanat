import { useState, useEffect } from 'react';
import { api, errorMessage } from '../../api/client.js';
import { Alert, Loading } from '../../components/ui.jsx';

export default function WebOrders() {
  const [orders, setOrders] = useState([]);
  const [statusFilter, setStatusFilter] = useState('');
  const [sourceFilter, setSourceFilter] = useState('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);
  const [selectedOrder, setSelectedOrder] = useState(null);
  const [orderDetails, setOrderDetails] = useState(null);
  const [fetchingDetails, setFetchingDetails] = useState(false);

  useEffect(() => {
    loadOrders();
  }, [statusFilter, sourceFilter]);

  async function loadOrders() {
    setLoading(true);
    setError(null);
    try {
      const q = new URLSearchParams();
      if (statusFilter) q.set('status', statusFilter);
      if (sourceFilter) q.set('source', sourceFilter);
      const res = await api.get(`/orders?${q.toString()}`);
      setOrders(res.data.data || []);
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      setLoading(false);
    }
  }

  async function openDetails(order) {
    setSelectedOrder(order);
    setFetchingDetails(true);
    try {
      const res = await api.get(`/orders/${order.uuid}`);
      setOrderDetails(res.data.data);
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      setFetchingDetails(false);
    }
  }

  async function handleCancel(uuid) {
    if (!confirm('Are you sure you want to cancel this order?')) return;
    try {
      await api.post(`/orders/${uuid}/cancel`, { reason: 'Cancelled via Web Admin Portal' });
      loadOrders();
      setSelectedOrder(null);
    } catch (err) {
      setError(errorMessage(err));
    }
  }

  return (
    <div className="space-y-6">
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
        <div>
          <h1 className="font-display text-2xl font-bold text-royal-950">Restaurant Orders</h1>
          <p className="text-sm text-slate-500">View and manage orders across desktop POS and web fallback channels</p>
        </div>

        {/* Filters */}
        <div className="flex items-center gap-2">
          <select
            value={statusFilter}
            onChange={(e) => setStatusFilter(e.target.value)}
            className="rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-sm"
          >
            <option value="">All Statuses</option>
            <option value="OPEN">Open</option>
            <option value="KITCHEN">In Kitchen</option>
            <option value="BILLED">Billed</option>
            <option value="PAID">Paid</option>
            <option value="CANCELLED">Cancelled</option>
          </select>

          <select
            value={sourceFilter}
            onChange={(e) => setSourceFilter(e.target.value)}
            className="rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-sm"
          >
            <option value="">All Sources</option>
            <option value="WEB">Web Fallback</option>
            <option value="DESKTOP">Desktop Till</option>
          </select>

          <button onClick={loadOrders} className="btn-ghost border border-slate-300 px-3 py-1.5 text-sm">
            ↻ Refresh
          </button>
        </div>
      </div>

      {error && <Alert type="error" message={error} />}

      {loading ? (
        <Loading label="Loading orders…" />
      ) : (
        <div className="card overflow-x-auto">
          <table className="w-full text-left text-sm">
            <thead className="border-b border-slate-200 bg-slate-50 text-xs font-semibold uppercase text-slate-600">
              <tr>
                <th className="px-4 py-3">Order #</th>
                <th className="px-4 py-3">Source</th>
                <th className="px-4 py-3">Type</th>
                <th className="px-4 py-3">Table</th>
                <th className="px-4 py-3">Status</th>
                <th className="px-4 py-3 text-right">Items</th>
                <th className="px-4 py-3 text-right">Total</th>
                <th className="px-4 py-3 text-right">Actions</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100">
              {orders.length === 0 ? (
                <tr>
                  <td colSpan={8} className="p-8 text-center text-slate-400">
                    No orders found matching your filters.
                  </td>
                </tr>
              ) : (
                orders.map((o) => (
                  <tr key={o.uuid} className="hover:bg-slate-50/80 transition">
                    <td className="px-4 py-3 font-bold text-royal-900">{o.order_no}</td>
                    <td className="px-4 py-3">
                      <span className={`inline-flex rounded-full px-2 py-0.5 text-xs font-semibold ${
                        o.source === 'WEB' ? 'bg-amber-100 text-amber-800' : 'bg-blue-100 text-blue-800'
                      }`}>
                        {o.source || 'DESKTOP'}
                      </span>
                    </td>
                    <td className="px-4 py-3 text-slate-600">{o.order_type}</td>
                    <td className="px-4 py-3 text-slate-600">{o.table_name || '—'}</td>
                    <td className="px-4 py-3">
                      <span className={`inline-flex rounded-full px-2 py-0.5 text-xs font-semibold ${
                        o.status === 'PAID' ? 'bg-emerald-100 text-emerald-800' :
                        o.status === 'CANCELLED' ? 'bg-rose-100 text-rose-800' :
                        'bg-sky-100 text-sky-800'
                      }`}>
                        {o.status}
                      </span>
                    </td>
                    <td className="px-4 py-3 text-right text-slate-600">{o.items_count}</td>
                    <td className="px-4 py-3 text-right font-bold text-royal-950">৳{Number(o.total).toLocaleString()}</td>
                    <td className="px-4 py-3 text-right space-x-2">
                      <button
                        onClick={() => openDetails(o)}
                        className="text-xs font-semibold text-royal-900 hover:underline"
                      >
                        Details
                      </button>
                      {o.status !== 'PAID' && o.status !== 'CANCELLED' && (
                        <button
                          onClick={() => handleCancel(o.uuid)}
                          className="text-xs font-semibold text-rose-600 hover:underline"
                        >
                          Cancel
                        </button>
                      )}
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>
      )}

      {/* Order Details Drawer */}
      {selectedOrder && (
        <div className="fixed inset-0 z-50 flex justify-end bg-black/40">
          <div className="w-full max-w-lg bg-white p-6 shadow-2xl flex flex-col justify-between overflow-y-auto">
            <div>
              <div className="flex items-center justify-between border-b pb-4">
                <div>
                  <h3 className="font-display font-bold text-royal-950 text-xl">{selectedOrder.order_no}</h3>
                  <p className="text-xs text-slate-500">Source: {selectedOrder.source || 'DESKTOP'} · {selectedOrder.order_type}</p>
                </div>
                <button onClick={() => setSelectedOrder(null)} className="btn-ghost text-lg">✕</button>
              </div>

              {fetchingDetails ? (
                <div className="py-12"><Loading label="Fetching items…" /></div>
              ) : orderDetails ? (
                <div className="py-4 space-y-4">
                  <div className="rounded-lg bg-slate-50 p-3 text-xs space-y-1">
                    <div className="flex justify-between"><span>Status:</span><span className="font-bold">{orderDetails.status}</span></div>
                    <div className="flex justify-between"><span>Payment:</span><span className="font-bold">{orderDetails.payment_status}</span></div>
                    <div className="flex justify-between"><span>Date:</span><span>{new Date(orderDetails.order_date).toLocaleString()}</span></div>
                  </div>

                  <div>
                    <h4 className="font-semibold text-sm text-slate-800 mb-2">Order Items</h4>
                    <div className="divide-y divide-slate-100 border rounded-lg">
                      {orderDetails.items?.map((i) => (
                        <div key={i.uuid} className="p-3 flex justify-between text-xs">
                          <div>
                            <p className="font-semibold text-slate-800">{i.name}</p>
                            <p className="text-slate-500">{i.quantity} x ৳{i.unit_price}</p>
                          </div>
                          <span className="font-bold text-slate-900">৳{i.total}</span>
                        </div>
                      ))}
                    </div>
                  </div>

                  <div className="border-t pt-3 space-y-1 text-xs">
                    <div className="flex justify-between text-slate-600"><span>Subtotal:</span><span>৳{orderDetails.subtotal}</span></div>
                    <div className="flex justify-between text-slate-600"><span>VAT:</span><span>৳{orderDetails.vat_amount}</span></div>
                    <div className="flex justify-between text-slate-600"><span>Service Charge:</span><span>৳{orderDetails.service_charge_amount}</span></div>
                    <div className="flex justify-between font-bold text-sm text-royal-950 pt-2 border-t">
                      <span>Total:</span><span>৳{orderDetails.total}</span>
                    </div>
                  </div>
                </div>
              ) : null}
            </div>

            <button onClick={() => setSelectedOrder(null)} className="btn-primary w-full py-2.5 text-sm">
              Close Details
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
