import { useState } from 'react';
import { api, errorMessage } from '../../api/client.js';
import { useApi } from '../../components/useApi.js';
import { useAuth } from '../../auth/AuthContext.jsx';
import { useToast } from '../../components/Toast.jsx';
import { PageHeader, Loading, ErrorBox, Empty, Modal, Confirm, formatDateTime } from '../../components/ui.jsx';

export default function Devices({ embedded = false }) {
  const { can } = useAuth(); const toast = useToast();
  const devices = useApi('/devices');
  const branches = useApi('/branches', { limit: 100 });
  const [reg, setReg] = useState(false);
  const [form, setForm] = useState({ name: '', branch_uuid: '' });
  const [issued, setIssued] = useState(null);
  const [revoking, setRevoking] = useState(null);

  const register = async () => {
    try { const r = await api.post('/devices/register', { ...form, branch_uuid: form.branch_uuid || branches.data[0]?.uuid }); setIssued(r.data.data); setReg(false); devices.reload(); }
    catch (e) { toast(errorMessage(e), 'error'); }
  };
  const revoke = async () => {
    try { await api.post(`/devices/${revoking.device_id}/revoke`); toast('Device revoked'); setRevoking(null); devices.reload(); }
    catch (e) { toast(errorMessage(e), 'error'); }
  };
  return (
    <>
      {embedded ? (can('sync.manage') && <div className="mb-3 flex justify-end"><button className="btn-gold" onClick={() => setReg(true)}>+ Register device</button></div>) : (
        <PageHeader title="Devices & Sync" subtitle="Desktop POS installations and their last heartbeat"
          actions={can('sync.manage') && <button className="btn-gold" onClick={() => setReg(true)}>+ Register device</button>} />)}
      <div className="card p-0">
        {devices.loading ? <Loading /> : devices.error ? <div className="p-4"><ErrorBox message={devices.error} onRetry={devices.reload} /></div> : devices.data.length === 0 ? <Empty title="No devices registered" hint="Register each Windows POS computer to get its device ID and token." /> : (
          <div className="overflow-x-auto"><table className="min-w-full divide-y divide-slate-200">
            <thead className="bg-slate-50"><tr><th className="th">Device</th><th className="th">Status</th><th className="th">Last heartbeat</th><th className="th">Last push / pull</th><th className="th">Pending changes</th><th className="th">Version</th><th className="th" /></tr></thead>
            <tbody className="divide-y divide-slate-100">{devices.data.map((d) => (
              <tr key={d.device_id}>
                <td className="td font-medium">{d.name}<div className="font-mono text-xs text-slate-400">{d.device_id}</div></td>
                <td className="td">{d.status === 'REVOKED' ? <span className="badge bg-slate-200 text-slate-600">Revoked</span> : d.online ? <span className="badge bg-emerald-100 text-emerald-700">Online</span> : <span className="badge bg-amber-100 text-amber-700">Offline</span>}</td>
                <td className="td">{formatDateTime(d.last_heartbeat_at)}</td><td className="td text-xs">{formatDateTime(d.last_push_at)}<br />{formatDateTime(d.last_pull_at)}</td><td className="td">{d.pending_count}</td><td className="td">{d.app_version ?? '—'}</td>
                <td className="td text-right">{can('sync.manage') && d.status === 'ACTIVE' && <button className="text-maroon-600 underline" onClick={() => setRevoking(d)}>Revoke</button>}</td>
              </tr>))}</tbody>
          </table></div>)}
      </div>
      <Modal open={reg} title="Register desktop device" onClose={() => setReg(false)} footer={<>
        <button className="btn-ghost" onClick={() => setReg(false)}>Cancel</button><button className="btn-primary" disabled={form.name.trim().length < 2} onClick={register}>Register</button></>}>
        <label className="label">Device name</label><input className="input mb-3" placeholder="Counter PC 1" value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} />
        <label className="label">Branch</label>
        <select className="input" value={form.branch_uuid} onChange={(e) => setForm({ ...form, branch_uuid: e.target.value })}>
          {(branches.data || []).map((b) => <option key={b.uuid} value={b.uuid}>{b.name}</option>)}
        </select>
      </Modal>
      <Modal open={!!issued} title="Device registered" onClose={() => setIssued(null)} footer={<button className="btn-primary" onClick={() => setIssued(null)}>I have saved it</button>}>
        <p className="mb-3 text-sm text-maroon-700">Copy the token into the desktop app (Settings → Sync) now. It will not be shown again.</p>
        <label className="label">Device ID</label><input readOnly className="input mb-3 font-mono text-xs" value={issued?.device_id ?? ''} onFocus={(e) => e.target.select()} />
        <label className="label">Device token</label><input readOnly className="input font-mono text-xs" value={issued?.device_token ?? ''} onFocus={(e) => e.target.select()} />
      </Modal>
      <Confirm open={!!revoking} danger title="Revoke device" confirmLabel="Revoke" onClose={() => setRevoking(null)} onConfirm={revoke}
        message={`${revoking?.name} will stop syncing immediately. Unsynced changes on that computer stay there until it is re-registered.`} />
    </>
  );
}
