import { useState } from 'react';
import { api, errorMessage } from '../../api/client.js';
import { useApi } from '../../components/useApi.js';
import { useAuth } from '../../auth/AuthContext.jsx';
import { useToast } from '../../components/Toast.jsx';
import { PageHeader, Loading, ErrorBox, Empty, Modal, Spinner, formatDateTime } from '../../components/ui.jsx';
import Devices from './Devices.jsx';

const SKIP = new Set(['created_at', 'updated_at', 'device_id', 'sync_version']);

/** Field-by-field comparison of the device's version and the current server version. */
function Diff({ local, server }) {
  const keys = [...new Set([...Object.keys(local || {}), ...Object.keys(server || {})])].filter((k) => !SKIP.has(k)).sort();
  const show = (v) => (v === null || v === undefined ? '—' : typeof v === 'object' ? JSON.stringify(v) : String(v));
  const same = (a, b) => show(a) === show(b) || (a !== null && b !== null && !Number.isNaN(Number(a)) && Number(a) === Number(b));
  return (
    <div className="max-h-96 overflow-auto rounded-lg ring-1 ring-slate-200">
      <table className="min-w-full text-xs">
        <thead className="sticky top-0 bg-slate-50"><tr><th className="th">Field</th><th className="th">Device (till)</th><th className="th">Server (now)</th></tr></thead>
        <tbody className="divide-y divide-slate-100">{keys.map((k) => {
          const diff = !same(local?.[k], server?.[k]);
          return (
            <tr key={k} className={diff ? 'bg-gold-300/20' : ''}>
              <td className="px-3 py-1.5 font-mono text-slate-500">{k}</td>
              <td className={`px-3 py-1.5 ${diff ? 'font-semibold text-royal-900' : 'text-slate-600'}`}>{show(local?.[k])}</td>
              <td className={`px-3 py-1.5 ${diff ? 'font-semibold text-maroon-600' : 'text-slate-600'}`}>{show(server?.[k])}</td>
            </tr>
          );
        })}</tbody>
      </table>
    </div>
  );
}

function Conflicts() {
  const { can } = useAuth(); const toast = useToast();
  const [status, setStatus] = useState('OPEN');
  const list = useApi('/sync/conflicts', { status });
  const [open, setOpen] = useState(null);
  const [note, setNote] = useState('');
  const [busy, setBusy] = useState(false);
  const resolve = async (resolution) => {
    setBusy(true);
    try {
      await api.post(`/sync/conflicts/${open.uuid}/resolve`, { resolution, note: note || undefined });
      toast(resolution === 'KEEP_DEVICE' ? 'The till\'s version is now live' : 'The server version is kept');
      setOpen(null); setNote(''); list.reload();
    } catch (e) { toast(errorMessage(e), 'error'); } finally { setBusy(false); }
  };
  return (
    <div className="card p-0">
      <div className="flex items-center justify-between border-b border-slate-200 p-4">
        <p className="text-sm text-slate-600">When a till and the portal change the same record, the server keeps its version and waits for you to decide. Money and stock records are never merged automatically.</p>
        <select className="input w-44" value={status} onChange={(e) => setStatus(e.target.value)}>
          <option value="OPEN">Open</option><option value="ALL">All</option>
        </select>
      </div>
      {list.loading ? <Loading /> : list.error ? <div className="p-4"><ErrorBox message={list.error} onRetry={list.reload} /></div> : list.data.length === 0 ? <Empty title="No conflicts" hint="Everything is in agreement." /> : (
        <div className="overflow-x-auto"><table className="min-w-full divide-y divide-slate-200">
          <thead className="bg-slate-50"><tr><th className="th">Detected</th><th className="th">Record</th><th className="th">Device</th><th className="th">Versions</th><th className="th">Status</th><th className="th" /></tr></thead>
          <tbody className="divide-y divide-slate-100">{list.data.map((c) => (
            <tr key={c.uuid}>
              <td className="td whitespace-nowrap">{formatDateTime(c.detected_at)}</td>
              <td className="td"><span className="font-medium">{c.entity.replace(/_/g, ' ')}</span><div className="text-xs text-slate-500">{c.local_data?.name || c.local_data?.order_no || c.local_data?.setting_key || c.entity_uuid.slice(0, 8)}</div></td>
              <td className="td">{c.device}</td>
              <td className="td text-xs">till based on v{c.local_version} · server v{c.server_version}</td>
              <td className="td">{c.status === 'OPEN' ? <span className="badge bg-maroon-500/10 text-maroon-700">Open</span> : <span className="badge bg-emerald-100 text-emerald-700">{c.status.replace('RESOLVED_', 'Kept ').toLowerCase()}</span>}</td>
              <td className="td text-right">{c.status === 'OPEN' && <button className="text-royal-700 underline" onClick={() => setOpen(c)}>Review</button>}</td>
            </tr>))}</tbody>
        </table></div>)}
      <Modal open={!!open} title={`Resolve conflict — ${open?.entity.replace(/_/g, ' ')}`} onClose={() => setOpen(null)} footer={can('sync.resolve') ? <>
        <button className="btn-ghost" onClick={() => setOpen(null)}>Cancel</button>
        <button className="btn-primary" disabled={busy} onClick={() => resolve('KEEP_SERVER')}>{busy && <Spinner className="h-4 w-4" />}Keep server version</button>
        <button className="btn-gold" disabled={busy} onClick={() => resolve('KEEP_DEVICE')}>Use till version</button>
      </> : <button className="btn-ghost" onClick={() => setOpen(null)}>Close</button>}>
        {open && <>
          <p className="mb-3 text-sm text-slate-600">Highlighted rows differ. Whichever you choose becomes a new version and every till receives it on its next sync.</p>
          <Diff local={open.local_data} server={open.server_data} />
          {can('sync.resolve') ? <>
            <label className="label mt-4">Note (optional, saved in the audit log)</label>
            <input className="input" value={note} onChange={(e) => setNote(e.target.value)} maxLength={255} />
          </> : <p className="mt-3 text-sm text-maroon-700">You need the “Resolve sync conflicts” permission to decide.</p>}
        </>}
      </Modal>
    </div>
  );
}

function FailedBatches() {
  const list = useApi('/sync/batches', { failed_only: 'true' });
  if (list.loading) return <Loading />;
  if (list.error) return <ErrorBox message={list.error} onRetry={list.reload} />;
  if (list.data.length === 0) return <div className="card"><Empty title="No failed changes" hint="Every change sent by the tills was accepted." /></div>;
  return (
    <div className="space-y-3">{list.data.map((b) => (
      <div key={b.batch_id} className="card">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <div><p className="font-semibold text-royal-900">{b.device}</p><p className="text-xs text-slate-500">{formatDateTime(b.received_at)} · batch {b.batch_id.slice(0, 8)}</p></div>
          <p className="text-sm">{b.success_count} synced · <span className="text-maroon-600">{b.failure_count} failed</span> · {b.conflict_count} conflicts</p>
        </div>
        <ul className="mt-3 space-y-1 text-sm">{b.problems.map((p, i) => (
          <li key={i} className="rounded bg-slate-50 px-3 py-2"><span className="font-mono text-xs text-slate-500">{p.entity} {p.entity_uuid.slice(0, 8)}</span> — <span className="font-medium">{p.status}</span>: {p.reason}</li>))}
        </ul>
      </div>))}
      <p className="text-sm text-slate-500">Tills retry failed changes automatically with increasing delays (e.g. a menu item waits until its category has synced). Persistent failures usually mean a duplicate name or an invalid value — fix it on the till or here.</p>
    </div>
  );
}

export default function Sync() {
  const [tab, setTab] = useState('devices');
  const status = useApi('/sync/status');
  const tabs = [['devices', 'Devices'], ['conflicts', `Conflicts${status.data?.open_conflicts ? ` (${status.data.open_conflicts})` : ''}`], ['failed', 'Failed changes']];
  return (
    <>
      <PageHeader title="Devices & Sync" subtitle={status.data ? `${status.data.devices.filter((d) => d.online).length} of ${status.data.devices.length} tills online · ${status.data.records_synced_24h} changes synced in 24 h · ${status.data.failed_batches_24h} batches with failures` : 'Desktop tills and their synchronisation'} />
      <div className="mb-4 flex gap-2">{tabs.map(([k, label]) => (
        <button key={k} onClick={() => setTab(k)} className={`rounded-lg px-4 py-2 text-sm font-semibold ${tab === k ? 'bg-royal-900 text-gold-300' : 'bg-white text-slate-600 ring-1 ring-slate-200'}`}>{label}</button>))}
      </div>
      {tab === 'devices' && <Devices embedded />}
      {tab === 'conflicts' && <Conflicts />}
      {tab === 'failed' && <FailedBatches />}
    </>
  );
}
