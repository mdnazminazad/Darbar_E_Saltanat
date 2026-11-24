import { useState, useEffect } from 'react';
import { api, errorMessage } from '../../api/client.js';
import { Alert, Loading } from '../../components/ui.jsx';

export default function WebReports() {
  const [summary, setSummary] = useState(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);

  useEffect(() => {
    loadSummary();
  }, []);

  async function loadSummary() {
    setLoading(true);
    setError(null);
    try {
      const res = await api.get('/reports/summary');
      setSummary(res.data.data);
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      setLoading(false);
    }
  }

  function handleExportCsv() {
    if (!summary) return;
    const rows = [
      ['Metric', 'Value'],
      ['Total Orders', summary.orders_count],
      ['Gross Sales (BDT)', summary.gross_sales],
      ['Total Discounts (BDT)', summary.total_discounts],
      ['VAT Collected (BDT)', summary.vat_collected],
      ['Service Charge (BDT)', summary.service_charge_collected],
      ['Net Revenue (BDT)', summary.net_revenue],
      [],
      ['Payment Method', 'Amount (BDT)'],
      ...Object.entries(summary.payment_mix || {}).map(([m, val]) => [m, val]),
      [],
      ['Order Type', 'Count'],
      ...Object.entries(summary.order_types || {}).map(([t, count]) => [t, count]),
    ];

    const csvContent = 'data:text/csv;charset=utf-8,' + rows.map((e) => e.join(',')).join('\n');
    const encodedUri = encodeURI(csvContent);
    const link = document.createElement('a');
    link.setAttribute('href', encodedUri);
    link.setAttribute('download', `darbar_sales_summary_${new Date().toISOString().slice(0, 10)}.csv`);
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
  }

  if (loading) return <Loading label="Generating financial sales reports…" />;

  return (
    <div className="space-y-6">
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
        <div>
          <h1 className="font-display text-2xl font-bold text-royal-950">Financial &amp; Operational Reports</h1>
          <p className="text-sm text-slate-500">Live operational metrics aggregated across desktop tills and web POS</p>
        </div>

        <div className="flex gap-2">
          <button onClick={loadSummary} className="btn-ghost border border-slate-300 px-3 py-1.5 text-sm">
            ↻ Refresh
          </button>
          <button onClick={handleExportCsv} className="btn-primary px-4 py-1.5 text-sm">
            Export to CSV
          </button>
        </div>
      </div>

      {error && <Alert type="error" message={error} />}

      {summary && (
        <div className="space-y-6">
          {/* Key Metric Cards */}
          <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
            <div className="card p-5 border-l-4 border-l-royal-900">
              <p className="text-xs font-semibold uppercase text-slate-500">Total Orders</p>
              <p className="mt-2 text-2xl font-bold text-royal-950">{summary.orders_count}</p>
            </div>
            <div className="card p-5 border-l-4 border-l-gold-500">
              <p className="text-xs font-semibold uppercase text-slate-500">Gross Sales</p>
              <p className="mt-2 text-2xl font-bold text-royal-950">৳{Number(summary.gross_sales || 0).toLocaleString()}</p>
            </div>
            <div className="card p-5 border-l-4 border-l-emerald-500">
              <p className="text-xs font-semibold uppercase text-slate-500">Net Revenue</p>
              <p className="mt-2 text-2xl font-bold text-emerald-700">৳{Number(summary.net_revenue || 0).toLocaleString()}</p>
            </div>
            <div className="card p-5 border-l-4 border-l-amber-500">
              <p className="text-xs font-semibold uppercase text-slate-500">Tax &amp; Service Charge</p>
              <p className="mt-2 text-2xl font-bold text-royal-950">
                ৳{(Number(summary.vat_collected || 0) + Number(summary.service_charge_collected || 0)).toLocaleString()}
              </p>
            </div>
          </div>

          <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
            {/* Payment Mix */}
            <div className="card p-5 space-y-4">
              <h3 className="font-display font-bold text-royal-950 text-base">Payment Method Breakdown</h3>
              <div className="divide-y divide-slate-100">
                {Object.entries(summary.payment_mix || {}).map(([method, amount]) => (
                  <div key={method} className="py-3 flex justify-between items-center text-sm">
                    <span className="font-semibold text-slate-700">{method}</span>
                    <span className="font-bold text-royal-900">৳{Number(amount).toLocaleString()}</span>
                  </div>
                ))}
              </div>
            </div>

            {/* Order Types */}
            <div className="card p-5 space-y-4">
              <h3 className="font-display font-bold text-royal-950 text-base">Order Type Distribution</h3>
              <div className="divide-y divide-slate-100">
                {Object.entries(summary.order_types || {}).map(([type, count]) => (
                  <div key={type} className="py-3 flex justify-between items-center text-sm">
                    <span className="font-semibold text-slate-700">{type.replace('_', ' ')}</span>
                    <span className="font-bold text-royal-900">{count} orders</span>
                  </div>
                ))}
              </div>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
