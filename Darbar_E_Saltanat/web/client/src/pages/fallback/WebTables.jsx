import { useState, useEffect } from 'react';
import { useNavigate } from 'react-router-dom';
import { api, errorMessage } from '../../api/client.js';
import { Alert, Loading } from '../../components/ui.jsx';

export default function WebTables() {
  const [tables, setTables] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);
  const navigate = useNavigate();

  useEffect(() => {
    loadTables();
  }, []);

  async function loadTables() {
    setLoading(true);
    setError(null);
    try {
      const res = await api.get('/tables');
      setTables(res.data.data || []);
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      setLoading(false);
    }
  }

  const statusColor = (status) => {
    switch (status) {
      case 'VACANT': return 'bg-emerald-50 border-emerald-300 text-emerald-800';
      case 'OCCUPIED': return 'bg-rose-50 border-rose-300 text-rose-800';
      case 'BILLING': return 'bg-amber-50 border-amber-300 text-amber-800';
      case 'RESERVED': return 'bg-purple-50 border-purple-300 text-purple-800';
      default: return 'bg-slate-50 border-slate-300 text-slate-800';
    }
  };

  return (
    <div className="space-y-6">
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
        <div>
          <h1 className="font-display text-2xl font-bold text-royal-950">Table &amp; Floor Management</h1>
          <p className="text-sm text-slate-500">Live dining area floor plan with real-time occupancy and billing status</p>
        </div>

        <button onClick={loadTables} className="btn-ghost border border-slate-300 px-4 py-2 text-sm">
          ↻ Refresh Tables
        </button>
      </div>

      {error && <Alert type="error" message={error} />}

      {loading ? (
        <Loading label="Loading table floor plan…" />
      ) : (
        <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 xl:grid-cols-6 gap-4">
          {tables.map((table) => (
            <div
              key={table.uuid}
              className={`rounded-2xl border-2 p-5 flex flex-col justify-between transition shadow-sm hover:shadow-md ${statusColor(table.status)}`}
            >
              <div>
                <div className="flex items-center justify-between">
                  <h3 className="font-display font-bold text-lg">{table.name}</h3>
                  <span className="text-xs font-semibold px-2 py-0.5 rounded-full bg-white/80">
                    {table.capacity} seats
                  </span>
                </div>
                <p className="text-xs font-bold uppercase mt-2 tracking-wide">{table.status}</p>
              </div>

              <div className="mt-4 pt-3 border-t border-black/10">
                <button
                  onClick={() => navigate('/admin/pos')}
                  className="w-full rounded-lg bg-royal-900 py-1.5 text-xs font-semibold text-gold-400 hover:bg-royal-800 shadow"
                >
                  Open in POS
                </button>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
