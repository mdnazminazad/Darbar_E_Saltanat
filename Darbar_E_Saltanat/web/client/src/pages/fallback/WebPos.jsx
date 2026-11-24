import { useState, useEffect } from 'react';
import { api, errorMessage } from '../../api/client.js';
import { Alert, Loading } from '../../components/ui.jsx';

export default function WebPos() {
  const [categories, setCategories] = useState([]);
  const [menuItems, setMenuItems] = useState([]);
  const [tables, setTables] = useState([]);
  const [activeCategory, setActiveCategory] = useState('ALL');
  const [search, setSearch] = useState('');
  const [orderType, setOrderType] = useState('DINE_IN');
  const [selectedTable, setSelectedTable] = useState('');
  const [cart, setCart] = useState([]);
  const [discountAmount, setDiscountAmount] = useState(0);
  const [loading, setLoading] = useState(true);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState(null);
  const [success, setSuccess] = useState(null);

  // Payment modal state
  const [showPayment, setShowPayment] = useState(false);
  const [paymentMethod, setPaymentMethod] = useState('CASH');
  const [paidAmount, setPaidAmount] = useState(0);
  const [activeOrderUuid, setActiveOrderUuid] = useState(null);

  // Receipt / KOT modal
  const [printDoc, setPrintDoc] = useState(null);

  useEffect(() => {
    loadData();
  }, []);

  async function loadData() {
    setLoading(true);
    setError(null);
    try {
      const [catsRes, itemsRes, tablesRes] = await Promise.all([
        api.get('/categories'),
        api.get('/menu-items'),
        api.get('/tables'),
      ]);
      setCategories(catsRes.data.data || []);
      setMenuItems(itemsRes.data.data || []);
      setTables(tablesRes.data.data || []);
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      setLoading(false);
    }
  }

  const filteredItems = menuItems.filter((item) => {
    const matchCat = activeCategory === 'ALL' || item.category_id === activeCategory;
    const matchSearch = !search || item.name.toLowerCase().includes(search.toLowerCase());
    return matchCat && matchSearch;
  });

  function addToCart(item) {
    setCart((prev) => {
      const idx = prev.findIndex((i) => i.menu_item_uuid === item.uuid);
      if (idx >= 0) {
        const copy = [...prev];
        copy[idx].quantity += 1;
        return copy;
      }
      return [...prev, {
        menu_item_uuid: item.uuid,
        name: item.name,
        unit_price: Number(item.base_price || 0),
        quantity: 1,
        notes: '',
      }];
    });
  }

  function updateQty(idx, delta) {
    setCart((prev) => {
      const copy = [...prev];
      copy[idx].quantity += delta;
      if (copy[idx].quantity <= 0) return copy.filter((_, i) => i !== idx);
      return copy;
    });
  }

  const subtotal = cart.reduce((sum, item) => sum + item.unit_price * item.quantity, 0);
  const vat = Math.round(subtotal * 0.05); // 5% VAT
  const serviceCharge = Math.round(subtotal * 0.05); // 5% Service Charge
  const total = Math.max(0, subtotal + vat + serviceCharge - Number(discountAmount || 0));

  async function handlePlaceOrder() {
    if (!cart.length) return;
    if (orderType === 'DINE_IN' && !selectedTable) {
      setError('Please select a table for Dine-in orders.');
      return;
    }
    setSubmitting(true);
    setError(null);
    setSuccess(null);
    try {
      const payload = {
        order_type: orderType,
        table_uuid: orderType === 'DINE_IN' ? selectedTable : null,
        guest_count: 2,
        notes: `Created via Web Fallback POS · ${orderType}`,
        items: cart.map((i) => ({
          menu_item_uuid: i.menu_item_uuid,
          name: i.name,
          quantity: i.quantity,
          unit_price: i.unit_price,
          notes: i.notes || null,
        })),
        discount_amount: Number(discountAmount) || 0,
        service_charge_amount: serviceCharge,
        vat_amount: vat,
      };

      const res = await api.post('/orders', payload);
      const createdOrder = res.data.data;
      setSuccess(`Order ${createdOrder.order_no} created successfully (Source: WEB)!`);
      setActiveOrderUuid(createdOrder.uuid);
      setPaidAmount(total);
      setShowPayment(true);
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      setSubmitting(false);
    }
  }

  async function handleRecordPayment() {
    if (!activeOrderUuid) return;
    setSubmitting(true);
    setError(null);
    try {
      const res = await api.post(`/orders/${activeOrderUuid}/pay`, {
        payment_method: paymentMethod,
        amount: Number(paidAmount),
        reference_no: `WEB-PAY-${Date.now().toString().slice(-6)}`,
      });
      setShowPayment(false);
      setCart([]);
      setDiscountAmount(0);
      setSuccess(`Payment recorded! Order #${res.data.data.order_no} settled.`);
      // Load receipt
      const recRes = await api.get(`/orders/${activeOrderUuid}/receipt`);
      setPrintDoc({ type: 'RECEIPT', data: recRes.data.data });
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      setSubmitting(false);
    }
  }

  async function handleGenerateKot() {
    if (!activeOrderUuid) return;
    try {
      const res = await api.post(`/orders/${activeOrderUuid}/kot`);
      setPrintDoc({ type: 'KOT', data: res.data.data });
    } catch (err) {
      setError(errorMessage(err));
    }
  }

  if (loading) return <Loading label="Loading Web Fallback POS…" />;

  return (
    <div className="space-y-4">
      {/* Fallback Banner */}
      <div className="flex items-center justify-between rounded-xl bg-gradient-to-r from-royal-900 to-royal-800 p-4 text-white shadow">
        <div className="flex items-center gap-3">
          <span className="flex h-3 w-3 relative">
            <span className="animate-ping absolute inline-flex h-full w-full rounded-full bg-emerald-400 opacity-75"></span>
            <span className="relative inline-flex rounded-full h-3 w-3 bg-emerald-500"></span>
          </span>
          <div>
            <h2 className="font-display font-bold text-gold-400 text-lg">Web Fallback Point of Sale</h2>
            <p className="text-xs text-slate-300">Active Business Continuity Mode · Orders created here automatically sync into desktop terminals upon reconnect.</p>
          </div>
        </div>
        <span className="rounded-full bg-royal-700/80 px-3 py-1 text-xs font-semibold text-gold-300 border border-gold-500/30">
          Source: WEB
        </span>
      </div>

      {error && <Alert type="error" message={error} />}
      {success && <Alert type="success" message={success} />}

      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
        {/* Menu Catalog Section (2 Cols) */}
        <div className="lg:col-span-2 space-y-4">
          {/* Order Type & Table Selection */}
          <div className="card flex flex-wrap items-center justify-between gap-3 p-4">
            <div className="flex items-center gap-2">
              {['DINE_IN', 'TAKEAWAY', 'DELIVERY'].map((type) => (
                <button
                  key={type}
                  onClick={() => setOrderType(type)}
                  className={`px-4 py-2 rounded-lg text-sm font-semibold transition ${
                    orderType === type
                      ? 'bg-royal-900 text-gold-400 shadow'
                      : 'bg-slate-100 text-slate-700 hover:bg-slate-200'
                  }`}
                >
                  {type.replace('_', ' ')}
                </button>
              ))}
            </div>

            {orderType === 'DINE_IN' && (
              <div className="flex items-center gap-2">
                <label className="text-xs font-medium text-slate-600">Table:</label>
                <select
                  value={selectedTable}
                  onChange={(e) => setSelectedTable(e.target.value)}
                  className="rounded-lg border border-slate-300 px-3 py-1.5 text-sm bg-white font-medium"
                >
                  <option value="">Select Table</option>
                  {tables.map((t) => (
                    <option key={t.uuid} value={t.uuid}>
                      {t.name} ({t.capacity} seats - {t.status})
                    </option>
                  ))}
                </select>
              </div>
            )}
          </div>

          {/* Categories & Search */}
          <div className="flex flex-col sm:flex-row gap-3 items-center justify-between">
            <div className="flex flex-wrap gap-2">
              <button
                onClick={() => setActiveCategory('ALL')}
                className={`px-3 py-1.5 rounded-full text-xs font-medium ${
                  activeCategory === 'ALL'
                    ? 'bg-gold-500 text-royal-950 font-bold'
                    : 'bg-white text-slate-600 border border-slate-200 hover:bg-slate-50'
                }`}
              >
                All Menu
              </button>
              {categories.map((c) => (
                <button
                  key={c.id}
                  onClick={() => setActiveCategory(c.id)}
                  className={`px-3 py-1.5 rounded-full text-xs font-medium ${
                    activeCategory === c.id
                      ? 'bg-gold-500 text-royal-950 font-bold'
                      : 'bg-white text-slate-600 border border-slate-200 hover:bg-slate-50'
                  }`}
                >
                  {c.name}
                </button>
              ))}
            </div>

            <input
              type="text"
              placeholder="Search dishes…"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              className="w-full sm:w-56 rounded-lg border border-slate-300 px-3 py-1.5 text-sm"
            />
          </div>

          {/* Menu Items Grid */}
          <div className="grid grid-cols-2 sm:grid-cols-3 xl:grid-cols-4 gap-3 max-h-[580px] overflow-y-auto p-1">
            {filteredItems.map((item) => (
              <button
                key={item.uuid}
                onClick={() => addToCart(item)}
                className="group flex flex-col justify-between rounded-xl border border-slate-200 bg-white p-3.5 text-left transition hover:border-gold-500 hover:shadow-md"
              >
                <div>
                  <h4 className="font-semibold text-slate-800 text-sm group-hover:text-royal-900 line-clamp-1">{item.name}</h4>
                  <p className="text-xs text-slate-500 line-clamp-2 mt-1">{item.description || 'Darbar royal specialty'}</p>
                </div>
                <div className="mt-3 flex items-center justify-between">
                  <span className="font-bold text-royal-900 text-sm">৳{Number(item.base_price).toLocaleString()}</span>
                  <span className="rounded bg-royal-50 px-2 py-0.5 text-xs font-bold text-royal-900 group-hover:bg-gold-500 group-hover:text-royal-950">
                    + Add
                  </span>
                </div>
              </button>
            ))}
          </div>
        </div>

        {/* Live Order Cart (1 Col) */}
        <div className="card flex flex-col justify-between h-[680px] p-5 border border-slate-200">
          <div>
            <div className="flex items-center justify-between border-b pb-3">
              <h3 className="font-display font-bold text-royal-900 text-base">Active Order Cart</h3>
              <span className="rounded-full bg-slate-100 px-2.5 py-0.5 text-xs font-semibold text-slate-600">
                {cart.length} item(s)
              </span>
            </div>

            {/* Cart Items List */}
            <div className="divide-y divide-slate-100 max-h-[340px] overflow-y-auto py-2">
              {cart.length === 0 ? (
                <div className="py-12 text-center text-slate-400 text-sm">
                  Cart is empty. Select menu items to begin.
                </div>
              ) : (
                cart.map((item, idx) => (
                  <div key={item.menu_item_uuid} className="py-2.5 flex items-center justify-between gap-2">
                    <div className="flex-1">
                      <p className="font-medium text-slate-800 text-sm leading-tight">{item.name}</p>
                      <p className="text-xs text-slate-500">৳{item.unit_price} each</p>
                    </div>

                    <div className="flex items-center gap-1.5">
                      <button
                        onClick={() => updateQty(idx, -1)}
                        className="h-7 w-7 rounded bg-slate-100 font-bold text-slate-700 hover:bg-slate-200 flex items-center justify-center text-sm"
                      >
                        -
                      </button>
                      <span className="w-6 text-center text-sm font-semibold">{item.quantity}</span>
                      <button
                        onClick={() => updateQty(idx, 1)}
                        className="h-7 w-7 rounded bg-slate-100 font-bold text-slate-700 hover:bg-slate-200 flex items-center justify-center text-sm"
                      >
                        +
                      </button>
                    </div>

                    <span className="w-16 text-right font-semibold text-slate-800 text-sm">
                      ৳{(item.unit_price * item.quantity).toLocaleString()}
                    </span>
                  </div>
                ))
              )}
            </div>
          </div>

          {/* Totals & Submit */}
          <div className="border-t border-slate-200 pt-4 space-y-2">
            <div className="flex justify-between text-xs text-slate-600">
              <span>Subtotal:</span>
              <span>৳{subtotal.toLocaleString()}</span>
            </div>
            <div className="flex justify-between text-xs text-slate-600">
              <span>VAT (5%):</span>
              <span>৳{vat.toLocaleString()}</span>
            </div>
            <div className="flex justify-between text-xs text-slate-600">
              <span>Service Charge (5%):</span>
              <span>৳{serviceCharge.toLocaleString()}</span>
            </div>
            <div className="flex items-center justify-between text-xs text-slate-600">
              <span>Discount (৳):</span>
              <input
                type="number"
                min="0"
                value={discountAmount}
                onChange={(e) => setDiscountAmount(e.target.value)}
                className="w-20 rounded border border-slate-300 px-2 py-0.5 text-right text-xs"
              />
            </div>
            <div className="flex justify-between font-display font-bold text-base text-royal-950 border-t border-slate-200 pt-2">
              <span>Total Payable:</span>
              <span className="text-gold-600">৳{total.toLocaleString()}</span>
            </div>

            <div className="pt-2 grid grid-cols-2 gap-2">
              <button
                disabled={!cart.length || submitting}
                onClick={handlePlaceOrder}
                className="btn-primary w-full py-2.5 text-sm"
              >
                {submitting ? 'Placing…' : 'Place Order'}
              </button>
              <button
                disabled={!activeOrderUuid}
                onClick={handleGenerateKot}
                className="btn-ghost border border-slate-300 w-full py-2.5 text-sm text-slate-700 hover:bg-slate-50"
              >
                Print KOT
              </button>
            </div>
          </div>
        </div>
      </div>

      {/* Payment Modal */}
      {showPayment && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4">
          <div className="card w-full max-w-md p-6 space-y-4">
            <h3 className="font-display font-bold text-royal-900 text-lg">Record Order Payment</h3>
            <p className="text-xs text-slate-500">Order #{activeOrderUuid?.slice(0, 8)} · Total Payable: ৳{total.toLocaleString()}</p>

            <div>
              <label className="text-xs font-semibold text-slate-700">Payment Method</label>
              <div className="grid grid-cols-3 gap-2 mt-1">
                {['CASH', 'CARD', 'BKASH', 'NAGAD', 'ROCKET', 'UPAY'].map((m) => (
                  <button
                    key={m}
                    type="button"
                    onClick={() => setPaymentMethod(m)}
                    className={`p-2 rounded-lg text-xs font-semibold border ${
                      paymentMethod === m
                        ? 'bg-royal-900 text-gold-400 border-royal-900'
                        : 'bg-white text-slate-700 border-slate-200'
                    }`}
                  >
                    {m}
                  </button>
                ))}
              </div>
            </div>

            <div>
              <label className="text-xs font-semibold text-slate-700">Tendered / Paid Amount (৳)</label>
              <input
                type="number"
                min="0"
                value={paidAmount}
                onChange={(e) => setPaidAmount(e.target.value)}
                className="mt-1 w-full rounded-lg border border-slate-300 p-2 text-base font-bold text-royal-900"
              />
            </div>

            {Number(paidAmount) > total && (
              <p className="text-xs text-emerald-600 font-semibold">
                Change to Return: ৳{(Number(paidAmount) - total).toLocaleString()}
              </p>
            )}

            <div className="flex justify-end gap-2 pt-2">
              <button onClick={() => setShowPayment(false)} className="btn-ghost text-sm">Later</button>
              <button disabled={submitting} onClick={handleRecordPayment} className="btn-gold px-4 text-sm">
                {submitting ? 'Recording…' : 'Complete Payment'}
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Print Preview Dialog */}
      {printDoc && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4">
          <div className="card w-full max-w-sm p-6 space-y-4 bg-white font-mono text-xs">
            <div className="text-center border-b pb-2">
              <h4 className="font-bold text-sm">DAWAT-E-DARBAR</h4>
              <p>{printDoc.type === 'KOT' ? 'KITCHEN ORDER TICKET' : 'CUSTOMER INVOICE'}</p>
              <p className="text-[10px] text-slate-500">Source: WEB FALLBACK POS</p>
            </div>

            <pre className="whitespace-pre-wrap leading-tight text-slate-800">
              {JSON.stringify(printDoc.data, null, 2)}
            </pre>

            <button onClick={() => setPrintDoc(null)} className="btn-primary w-full py-2 text-xs">
              Close Preview
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
