import { useState } from 'react';
import { api, errorMessage } from '../../api/client.js';
import { useApi } from '../../components/useApi.js';
import { useAuth } from '../../auth/AuthContext.jsx';
import { useToast } from '../../components/Toast.jsx';
import { PageHeader, Loading, ErrorBox, Modal } from '../../components/ui.jsx';

const blank = { code: '', name: '', address: '', phone: '', email: '' };
export default function Branches() {
  const { can } = useAuth(); const toast = useToast();
  const branches = useApi('/branches', { limit: 100 });
  const [edit, setEdit] = useState(null);
  const [form, setForm] = useState(blank);
  const open = (b) => { setEdit(b || 'new'); setForm(b ? { code: b.code, name: b.name, address: b.address || '', phone: b.phone || '', email: b.email || '' } : blank); };
  const valid = /^[A-Za-z0-9_-]{2,20}$/.test(form.code) && form.name.trim().length >= 2 && (!form.email || /\S+@\S+\.\S+/.test(form.email));
  const save = async () => {
    const body = { ...form, address: form.address || null, phone: form.phone || null, email: form.email || null };
    try { if (edit === 'new') await api.post('/branches', body); else await api.patch(`/branches/${edit.uuid}`, body); toast('Branch saved'); setEdit(null); branches.reload(); }
    catch (e) { toast(errorMessage(e), 'error'); }
  };
  return (
    <>
      <PageHeader title="Branches" subtitle="Ready for multi-branch operation" actions={can('branches.manage') && <button className="btn-gold" onClick={() => open(null)}>+ New branch</button>} />
      {branches.loading ? <Loading /> : branches.error ? <ErrorBox message={branches.error} onRetry={branches.reload} /> : (
        <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">{branches.data.map((b) => (
          <div key={b.uuid} className="card">
            <div className="flex items-start justify-between"><div><p className="text-xs font-semibold text-gold-600">{b.code}</p><h3 className="font-display text-lg font-semibold text-royal-900">{b.name}</h3></div>
              {can('branches.manage') && <button className="text-sm text-royal-700 underline" onClick={() => open(b)}>Edit</button>}</div>
            <p className="mt-2 text-sm text-slate-600">{b.address || 'No address'}</p><p className="text-sm text-slate-500">{b.phone} {b.email}</p>
          </div>))}
        </div>)}
      <Modal open={!!edit} title={edit === 'new' ? 'New branch' : 'Edit branch'} onClose={() => setEdit(null)} footer={<>
        <button className="btn-ghost" onClick={() => setEdit(null)}>Cancel</button><button className="btn-primary" disabled={!valid} onClick={save}>Save</button></>}>
        {['code', 'name', 'address', 'phone', 'email'].map((k) => (
          <div key={k} className="mb-3"><label className="label capitalize">{k}</label><input className="input" value={form[k]} onChange={(e) => setForm({ ...form, [k]: k === 'code' ? e.target.value.toUpperCase() : e.target.value })} /></div>))}
      </Modal>
    </>
  );
}
