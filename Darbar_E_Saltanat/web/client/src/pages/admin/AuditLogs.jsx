import { Fragment, useState } from 'react';
import { useApi } from '../../components/useApi.js';
import { PageHeader, Loading, ErrorBox, Empty, Pagination, formatDateTime } from '../../components/ui.jsx';

export default function AuditLogs() {
  const [params, setParams] = useState({ page: 1, limit: 25, action: '' });
  const logs = useApi('/audit-logs', { ...params, action: params.action || undefined });
  const [open, setOpen] = useState(null);
  return (
    <>
      <PageHeader title="Audit logs" subtitle="Every sensitive action, who did it and when" />
      <div className="card p-0">
        <div className="border-b border-slate-200 p-4"><input className="input max-w-xs" placeholder="Filter by action, e.g. users. or auth.login" value={params.action} onChange={(e) => setParams({ ...params, action: e.target.value, page: 1 })} /></div>
        {logs.loading ? <Loading /> : logs.error ? <div className="p-4"><ErrorBox message={logs.error} onRetry={logs.reload} /></div> : logs.data.length === 0 ? <Empty title="No audit entries" /> : (
          <div className="overflow-x-auto"><table className="min-w-full divide-y divide-slate-200">
            <thead className="bg-slate-50"><tr><th className="th">When</th><th className="th">User</th><th className="th">Action</th><th className="th">Entity</th><th className="th">IP</th></tr></thead>
            <tbody className="divide-y divide-slate-100">{logs.data.map((l) => (
              <Fragment key={l.uuid}>
                <tr className="cursor-pointer hover:bg-slate-50" onClick={() => setOpen(open === l.uuid ? null : l.uuid)}>
                  <td className="td whitespace-nowrap">{formatDateTime(l.occurred_at)}</td><td className="td">{l.user?.full_name ?? 'System'}</td>
                  <td className="td font-mono text-xs">{l.action}</td><td className="td text-xs">{l.entity_type} {l.entity_uuid?.slice(0, 8)}</td><td className="td text-xs">{l.ip_address}</td>
                </tr>
                {open === l.uuid && <tr><td colSpan={5} className="bg-slate-50 px-4 py-3"><pre className="whitespace-pre-wrap text-xs text-slate-600">{JSON.stringify({ before: l.old_values, after: l.new_values }, null, 2)}</pre></td></tr>}
              </Fragment>))}</tbody>
          </table></div>)}
        <Pagination meta={logs.meta} onPage={(page) => setParams({ ...params, page })} />
      </div>
    </>
  );
}
