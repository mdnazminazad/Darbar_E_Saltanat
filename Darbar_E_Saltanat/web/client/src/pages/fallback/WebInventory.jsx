import { useState, useEffect } from 'react';
import { api, errorMessage } from '../../api/client.js';
import { Alert, Loading } from '../../components/ui.jsx';

export default function WebInventory() {
  const [stock, setStock] = useState([]);
  const [movements, setMovements] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);
  const [showModal, setShowModal] = useState(false);
  const [saving, setSaving] = useState(false);

  // Movement Form
  const [movementType, setMovementType] = useState('IN');
  const [itemUuid, setItemUuid] = useState('');
  const [quantity, setQuantity] = useState('');
  const [reason, setReason] = useState('');

  useEffect(() => {
    loadData();
  }, []);

  async function loadData() {
    setLoading(true);
    setError(null);
    try {
      const [stockRes, movRes] = await Promise.all([
        api.get('/inventory/stock'),
        api.get('/inventory/movements'),
      ]);
      setStock(stockRes.data.data || []);
      setMovements(movRes.data.data || []);
      if (stockRes.data.data?.length) setItemUuid(stockRes.data.data[0].uuid);
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      setLoading(false);
    }
  }

  async function handleRecordMovement(e) {
    e.preventDefault();
    if (!itemUuid || !quantity || Number(quantity) <= 0) {
      setError('Please provide a valid item and quantity.');
      return;
    }
    setSaving(true);
    setError(null);
    try {
      await api.post('/inventory/movements', {
        item_uuid: itemUuid,
        movement_type: movementType,
        quantity: Number(quantity),
        reason: reason || `Manual stock ${movementType} recorded from web portal`,
      });
      setShowModal(false);
      setQuantity('');
      setReason('');
      await loadData();
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      setSaving(false);
    }
  }

  return (
    <div className="space-y-6">
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
        <div>
          <h1 className="font-display text-2xl font-bold text-royal-950">Inventory &amp; Stock Movements</h1>
          <p className="text-sm text-slate-500">Track raw ingredients, recipe stock levels, and audit inventory entries</p>
        </div>

        <button onClick={() => setShowModal(true)} className="btn-gold px-4 py-2 text-sm shadow">
          + Record Stock Movement
        </button>
      </div>

      {error && <Alert type="error" message={error} />}

      {loading ? (
        <Loading label="Loading inventory data…" />
      ) : (
        <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
          {/* Stock Levels (2 Cols) */}
          <div className="lg:col-span-2 card p-5 space-y-4">
            <h3 className="font-display font-bold text-royal-950 text-base">Ingredient Stock Levels</h3>
            <div className="overflow-x-auto">
              <table className="w-full text-left text-sm">
                <thead className="border-b bg-slate-50 text-xs font-semibold uppercase text-slate-600">
                  <tr>
                    <th className="px-4 py-3">Ingredient</th>
                    <th className="px-4 py-3">Unit</th>
                    <th className="px-4 py-3 text-right">Current Stock</th>
                    <th className="px-4 py-3 text-right">Min Stock</th>
                    <th className="px-4 py-3 text-right">Cost / Unit</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100">
                  {stock.length === 0 ? (
                    <tr><td colSpan={5} className="p-6 text-center text-slate-400">No inventory ingredients found.</td></tr>
                  ) : (
                    stock.map((item) => (
                      <tr key={item.uuid} className="hover:bg-slate-50/80">
                        <td className="px-4 py-3 font-semibold text-slate-800">{item.name}</td>
                        <td className="px-4 py-3 text-slate-500">{item.unit}</td>
                        <td className={`px-4 py-3 text-right font-bold ${
                          item.current_stock <= item.minimum_stock ? 'text-rose-600' : 'text-slate-800'
                        }`}>
                          {item.current_stock}
                        </td>
                        <td className="px-4 py-3 text-right text-slate-500">{item.minimum_stock}</td>
                        <td className="px-4 py-3 text-right text-slate-700">৳{item.cost_per_unit}</td>
                      </tr>
                    ))
                  )}
                </tbody>
              </table>
            </div>
          </div>

          {/* Recent Movements (1 Col) */}
          <div className="card p-5 space-y-4">
            <h3 className="font-display font-bold text-royal-950 text-base">Recent Stock Audit</h3>
            <div className="divide-y divide-slate-100 max-h-[460px] overflow-y-auto">
              {movements.length === 0 ? (
                <div className="p-8 text-center text-slate-400 text-xs">No recent stock movements recorded.</div>
              ) : (
                movements.map((m) => (
                  <div key={m.uuid} className="py-2.5 text-xs">
                    <div className="flex justify-between items-center">
                      <span className="font-semibold text-slate-800">{m.item_name}</span>
                      <span className={`px-2 py-0.5 rounded font-bold ${
                        m.movement_type === 'IN' ? 'bg-emerald-100 text-emerald-800' : 'bg-rose-100 text-rose-800'
                      }`}>
                        {m.movement_type === 'IN' ? '+' : '-'}{m.quantity} {m.unit}
                      </span>
                    </div>
                    <p className="text-slate-500 mt-0.5">{m.reason || 'General inventory update'}</p>
                    <p className="text-[10px] text-slate-400 mt-1">{new Date(m.created_at).toLocaleString()}</p>
                  </div>
                ))
              )}
            </div>
          </div>
        </div>
      )}

      {/* Movement Modal */}
      {showModal && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4">
          <form onSubmit={handleRecordMovement} className="card w-full max-w-md p-6 space-y-4 bg-white">
            <h3 className="font-display font-bold text-royal-950 text-lg">Record Stock Movement</h3>

            <div>
              <label className="text-xs font-semibold text-slate-700">Movement Type</label>
              <select
                value={movementType}
                onChange={(e) => setMovementType(e.target.value)}
                className="mt-1 w-full rounded-lg border border-slate-300 p-2 text-sm bg-white"
              >
                <option value="IN">Stock In (Purchase / Restock)</option>
                <option value="OUT">Stock Out (Usage / Issue)</option>
                <option value="WASTAGE">Wastage / Spoilage</option>
                <option value="ADJUSTMENT">Audit Adjustment</option>
              </select>
            </div>

            <div>
              <label className="text-xs font-semibold text-slate-700">Ingredient / Item</label>
              <select
                value={itemUuid}
                onChange={(e) => setItemUuid(e.target.value)}
                className="mt-1 w-full rounded-lg border border-slate-300 p-2 text-sm bg-white"
              >
                {stock.map((s) => (
                  <option key={s.uuid} value={s.uuid}>
                    {s.name} ({s.current_stock} {s.unit} in stock)
                  </option>
                ))}
              </select>
            </div>

            <div>
              <label className="text-xs font-semibold text-slate-700">Quantity</label>
              <input
                type="number"
                step="0.01"
                min="0.01"
                required
                value={quantity}
                onChange={(e) => setQuantity(e.target.value)}
                placeholder="e.g. 5"
                className="mt-1 w-full rounded-lg border border-slate-300 p-2 text-sm"
              />
            </div>

            <div>
              <label className="text-xs font-semibold text-slate-700">Audit Reason / Invoice Ref</label>
              <input
                type="text"
                value={reason}
                onChange={(e) => setReason(e.target.value)}
                placeholder="e.g. Daily market purchase PO-1024"
                className="mt-1 w-full rounded-lg border border-slate-300 p-2 text-sm"
              />
            </div>

            <div className="flex justify-end gap-2 pt-2">
              <button type="button" onClick={() => setShowModal(false)} className="btn-ghost text-sm">Cancel</button>
              <button type="submit" disabled={saving} className="btn-primary px-4 text-sm">
                {saving ? 'Recording…' : 'Save Movement'}
              </button>
            </div>
          </form>
        </div>
      )}
    </div>
  );
}
