import { useAuth } from '../../auth/AuthContext.jsx';
import { useApi } from '../../components/useApi.js';
import { PageHeader, Loading, ErrorBox, formatDateTime } from '../../components/ui.jsx';

function Stat({ label, value, tone = 'royal' }) {
  const tones = { royal: 'border-royal-700', gold: 'border-gold-500', maroon: 'border-maroon-500' };
  return <div className={`card border-l-4 ${tones[tone]}`}><p className="text-xs uppercase tracking-wide text-slate-500">{label}</p><p className="mt-1 text-2xl font-bold text-royal-900">{value}</p></div>;
}

export default function Overview() {
  const { user, can } = useAuth();
  const health = useApi('/health');
  const devices = useApi(can('sync.view') ? '/sync/status' : '/health');
  const list = can('sync.view') && Array.isArray(devices.data?.devices) ? devices.data.devices : [];
  const isHealthOk = health.data?.status === 'ok';
  const isDbUp = health.data?.db === 'up' || health.data?.database === 'connected';
  const timeVal = health.data?.time || health.data?.serverTime;

  return (
    <>
      <PageHeader title={`Welcome, ${user?.full_name || 'Administrator'}`} subtitle="System overview" />
      {health.loading ? <Loading /> : health.error ? <ErrorBox message={health.error} onRetry={health.reload} /> : (
        <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
          <Stat label="API status" value={isHealthOk ? 'Online' : 'Degraded'} tone={isHealthOk ? 'gold' : 'maroon'} />
          <Stat label="Database" value={isDbUp ? 'Connected' : 'Down'} tone={isDbUp ? 'royal' : 'maroon'} />
          {can('sync.view') && <Stat label="Desktop devices online" value={`${list.filter((d) => d.online).length} / ${list.length}`} />}
          {can('sync.view') && <Stat label="Pending desktop changes" value={list.reduce((s, d) => s + (d.pending_count || 0), 0)} tone="maroon" />}
          {can('sync.view') && devices.data?.open_conflicts > 0 && <Stat label="Sync conflicts to review" value={devices.data.open_conflicts} tone="maroon" />}
        </div>
      )}
      <div className="card mt-6">
        <h2 className="font-display text-lg font-semibold text-royal-900">Server time</h2>
        <p className="text-sm text-slate-600">{timeVal ? formatDateTime(timeVal) : '—'} (Asia/Dhaka)</p>
        <p className="mt-3 text-sm text-slate-500">Sales dashboards, orders, menu and inventory screens arrive in Phases 2–5.</p>
      </div>
    </>
  );
}
