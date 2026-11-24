import { useEffect, useState } from 'react';
import { api, errorMessage } from '../../api/client.js';
import { useApi } from '../../components/useApi.js';
import { useAuth } from '../../auth/AuthContext.jsx';
import { useToast } from '../../components/Toast.jsx';
import { PageHeader, Loading, ErrorBox, Spinner } from '../../components/ui.jsx';

// Known settings with labels and validation; unknown keys are still shown under "Other".
const FIELDS = [
  { key: 'restaurant_name', label: 'Restaurant name', group: 'Restaurant', type: 'text', required: true },
  { key: 'restaurant_address', label: 'Address', group: 'Restaurant', type: 'text' },
  { key: 'restaurant_phone', label: 'Phone', group: 'Restaurant', type: 'text' },
  { key: 'vat_rate', label: 'VAT rate (%)', group: 'Tax & charges', type: 'number', min: 0, max: 100 },
  { key: 'vat_inclusive', label: 'Menu prices include VAT', group: 'Tax & charges', type: 'bool' },
  { key: 'service_charge_rate', label: 'Service charge (%)', group: 'Tax & charges', type: 'number', min: 0, max: 100 },
  { key: 'service_charge_dine_in_only', label: 'Service charge on dine-in only', group: 'Tax & charges', type: 'bool' },
  { key: 'vat_applies_to_service_charge', label: 'Charge VAT on the service charge', group: 'Tax & charges', type: 'bool' },
  { key: 'round_total_to_taka', label: 'Round bill totals to whole taka', group: 'POS', type: 'bool' },
  { key: 'delivery_charge_default', label: 'Default delivery charge (৳)', group: 'POS', type: 'number', min: 0, max: 10000 },
  { key: 'mobile_banking_providers', label: 'Mobile banking providers (comma separated)', group: 'POS', type: 'text', required: true },
  { key: 'max_discount_percent_without_permission', label: 'Max discount without override (%)', group: 'POS', type: 'number', min: 0, max: 100 },
  { key: 'order_no_prefix', label: 'Order number prefix', group: 'POS', type: 'text' },
  { key: 'receipt_header', label: 'Receipt header', group: 'Receipt', type: 'text' },
  { key: 'receipt_footer', label: 'Receipt footer', group: 'Receipt', type: 'text' },
  { key: 'receipt_width_mm', label: 'Receipt width', group: 'Receipt', type: 'select', options: ['58', '80'] },
  { key: 'auto_lock_minutes', label: 'Desktop auto-lock (minutes)', group: 'Security', type: 'number', min: 1, max: 120 },
  { key: 'sync_interval_seconds', label: 'Sync interval (seconds)', group: 'Sync', type: 'number', min: 10, max: 3600 },
  { key: 'loyalty_points_per_100_bdt', label: 'Loyalty points per ৳100', group: 'Loyalty', type: 'number', min: 0, max: 100 },
];

export default function Settings() {
  const { can } = useAuth(); const toast = useToast();
  const s = useApi('/settings');
  const [values, setValues] = useState({});
  const [errors, setErrors] = useState({});
  const [saving, setSaving] = useState(false);
  const editable = can('settings.manage');

  useEffect(() => { if (s.data) setValues(Object.fromEntries(Object.entries(s.data).map(([k, v]) => [k, v.value]))); }, [s.data]);
  if (s.loading) return <Loading />;
  if (s.error) return <ErrorBox message={s.error} onRetry={s.reload} />;

  const validate = () => {
    const e = {};
    for (const f of FIELDS) {
      const v = values[f.key];
      if (f.required && !String(v ?? '').trim()) e[f.key] = 'Required';
      if (f.type === 'number' && (v === '' || Number.isNaN(Number(v)) || Number(v) < f.min || Number(v) > f.max)) e[f.key] = `Enter ${f.min}–${f.max}`;
    }
    setErrors(e); return Object.keys(e).length === 0;
  };
  const save = async () => {
    if (!validate()) return;
    const changed = FIELDS.filter((f) => values[f.key] !== s.data[f.key]?.value).map((f) => ({
      key: f.key, value: f.type === 'number' ? Number(values[f.key]) : f.type === 'bool' ? !!values[f.key] : String(values[f.key] ?? ''),
    }));
    if (!changed.length) return toast('Nothing changed');
    setSaving(true);
    try { await api.put('/settings', { settings: changed }); toast('Settings saved'); s.reload(); }
    catch (e) { toast(errorMessage(e), 'error'); } finally { setSaving(false); }
  };
  const groups = [...new Set(FIELDS.map((f) => f.group))];
  const input = (f) => {
    const common = { id: f.key, disabled: !editable };
    if (f.type === 'bool') return <input type="checkbox" {...common} checked={!!values[f.key]} onChange={(e) => setValues({ ...values, [f.key]: e.target.checked })} />;
    if (f.type === 'select') return <select className="input" {...common} value={String(values[f.key] ?? '')} onChange={(e) => setValues({ ...values, [f.key]: e.target.value })}>{f.options.map((o) => <option key={o} value={o}>{o} mm</option>)}</select>;
    return <input className="input" type={f.type === 'number' ? 'number' : 'text'} step="any" {...common} value={values[f.key] ?? ''} onChange={(e) => setValues({ ...values, [f.key]: e.target.value })} />;
  };
  return (
    <>
      <PageHeader title="Settings" subtitle="Chain-wide defaults. Desktops receive changes on their next sync."
        actions={editable && <button className="btn-primary" onClick={save} disabled={saving}>{saving && <Spinner className="h-4 w-4" />}Save settings</button>} />
      <div className="grid gap-6 xl:grid-cols-2">{groups.map((g) => (
        <section key={g} className="card"><h2 className="mb-4 font-display text-lg font-semibold text-royal-900">{g}</h2>
          <div className="space-y-4">{FIELDS.filter((f) => f.group === g).map((f) => (
            <div key={f.key} className={f.type === 'bool' ? 'flex items-center gap-2' : ''}>
              {f.type === 'bool' ? <>{input(f)}<label htmlFor={f.key} className="text-sm text-slate-700">{f.label}</label></> : <><label className="label" htmlFor={f.key}>{f.label}</label>{input(f)}</>}
              {errors[f.key] && <p className="mt-1 text-xs text-maroon-600">{errors[f.key]}</p>}
            </div>))}
          </div></section>))}
      </div>
    </>
  );
}
