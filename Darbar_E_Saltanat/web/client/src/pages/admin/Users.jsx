import { useState, useEffect } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { api, errorMessage } from '../../api/client.js';
import { useApi } from '../../components/useApi.js';
import { useToast } from '../../components/Toast.jsx';
import { useAuth } from '../../auth/AuthContext.jsx';
import { PageHeader, Loading, ErrorBox, Empty, Modal, Confirm, Pagination, FieldError, Spinner, formatDateTime } from '../../components/ui.jsx';

const passwordRule = z.string()
  .min(8, 'At least 8 characters')
  .refine((p) => /[A-Z]/.test(p), 'Must include an uppercase letter')
  .refine((p) => /[a-z]/.test(p), 'Must include a lowercase letter')
  .refine((p) => /\d/.test(p), 'Must include a number')
  .refine((p) => /[^A-Za-z0-9]/.test(p), 'Must include a special character');

const pinRule = z.string().regex(/^\d{4,6}$/, '4–6 digits').or(z.literal(''));

const baseSchema = {
  id_number: z.string().max(50).optional(),
  access_scope: z.enum(['DESKTOP', 'WEB', 'BOTH']).default('BOTH'),
  full_name: z.string().trim().min(2, 'Full name is required'),
  email: z.string().email('Invalid email address').or(z.literal('')),
  phone: z.string().max(30).optional(),
  branch_uuid: z.string().uuid().or(z.literal('')).optional(),
  pin: pinRule,
  role_codes: z.array(z.string()).min(1, 'Select at least one role'),
  profile_image_url: z.string().max(255).or(z.literal('')).optional(),
  notes: z.string().max(2000).optional(),
};

const createSchema = z.object({
  ...baseSchema,
  username: z.string().trim().min(3, 'At least 3 characters').regex(/^[a-zA-Z0-9._-]+$/, 'Letters, digits, dot, dash, underscore only'),
  password: passwordRule,
  must_change_password: z.boolean().default(true),
  is_active: z.boolean().default(true),
});

const editSchema = z.object({
  ...baseSchema,
  username: z.string().trim().min(3).regex(/^[a-zA-Z0-9._-]+$/).optional(),
  password: z.union([passwordRule, z.literal('')]).optional(),
  is_active: z.boolean(),
  must_change_password: z.boolean(),
});

function UserForm({ user, roles, branches, currentAuthUser, onDone, onCancel }) {
  const toast = useToast();
  const editing = !!user;
  const isSelf = user?.uuid === currentAuthUser?.uuid;
  const isSuperAdmin = (currentAuthUser?.roles || []).some(
    (r) => (typeof r === 'string' ? r : r?.code || '').toUpperCase() === 'SUPER_ADMIN'
  );

  const { register, handleSubmit, setError, formState: { errors, isSubmitting } } = useForm({
    resolver: zodResolver(editing ? editSchema : createSchema),
    defaultValues: editing ? {
      id_number: user.id_number || '',
      access_scope: user.access_scope || 'BOTH',
      username: user.username,
      full_name: user.full_name,
      email: user.email || '',
      phone: user.phone || '',
      branch_uuid: user.branch_uuid || '',
      pin: '',
      password: '',
      role_codes: user.roles || [],
      is_active: user.is_active,
      must_change_password: user.must_change_password,
      profile_image_url: user.profile_image_url || '',
      notes: user.notes || '',
    } : {
      id_number: '',
      access_scope: 'BOTH',
      username: '',
      full_name: '',
      email: '',
      phone: '',
      branch_uuid: branches?.[0]?.uuid || '',
      password: '',
      pin: '',
      role_codes: ['CASHIER'],
      is_active: true,
      must_change_password: true,
      profile_image_url: '',
      notes: '',
    },
  });

  const submit = async (v) => {
    const body = {
      ...v,
      email: v.email ? v.email.trim() : null,
      phone: v.phone ? v.phone.trim() : null,
      branch_uuid: v.branch_uuid || null,
      profile_image_url: v.profile_image_url ? v.profile_image_url.trim() : null,
      notes: v.notes ? v.notes.trim() : null,
    };
    if (!v.pin) delete body.pin;
    if (!v.password) delete body.password;

    try {
      if (editing) {
        await api.put(`/users/${user.uuid}`, body);
        toast('User updated successfully');
      } else {
        await api.post('/users', body);
        toast('User created successfully — password change required on first login');
      }
      onDone();
    } catch (e) {
      setError('root', { message: errorMessage(e) });
    }
  };

  return (
    <form onSubmit={handleSubmit(submit)} noValidate className="space-y-4">
      {errors.root && <ErrorBox message={errors.root.message} />}

      <div className="grid gap-3 sm:grid-cols-2">
        <div>
          <label className="label">Staff ID Number (optional)</label>
          <input className="input font-mono font-bold bg-amber-50/50" placeholder="e.g. CSH-01 / ADM-01" {...register('id_number')} />
          <FieldError error={errors.id_number} />
        </div>
        <div>
          <label className="label">Access Destination Platform</label>
          <select className="input font-medium" {...register('access_scope')}>
            <option value="BOTH">💻🌐 Both (Desktop POS &amp; Web Portal)</option>
            <option value="DESKTOP">💻 Desktop POS Terminal Only</option>
            <option value="WEB">🌐 Web Management Portal Only</option>
          </select>
        </div>
      </div>

      <div className="grid gap-3 sm:grid-cols-2">
        <div>
          <label className="label">Username *</label>
          <input className="input" {...register('username')} disabled={editing && !isSuperAdmin} />
          <FieldError error={errors.username} />
          {editing && !isSuperAdmin && <p className="text-xs text-slate-400 mt-1">Username can only be modified by Super Admin</p>}
        </div>
        <div>
          <label className="label">Full Name *</label>
          <input className="input" {...register('full_name')} />
          <FieldError error={errors.full_name} />
        </div>
      </div>

      <div className="grid gap-3 sm:grid-cols-2">
        <div>
          <label className="label">Email Address</label>
          <input type="email" className="input" {...register('email')} />
          <FieldError error={errors.email} />
        </div>
        <div>
          <label className="label">Phone Number</label>
          <input className="input" placeholder="e.g. 01711000000" {...register('phone')} />
          <FieldError error={errors.phone} />
        </div>
      </div>

      <div>
        <label className="label">
          {editing ? 'Change Password (leave blank to keep current)' : 'Initial Password *'}
        </label>
        <input
          type="password"
          autoComplete="new-password"
          placeholder={editing ? 'Enter new password to change…' : 'Initial strong password'}
          className="input"
          {...register('password')}
        />
        <FieldError error={errors.password} />
        <p className="mt-1 text-xs text-slate-500">Min 8 chars, with uppercase, lowercase, number, &amp; special character.</p>
      </div>

      <div className="grid gap-3 sm:grid-cols-2">
        <div>
          <label className="label">{editing ? 'Change POS PIN (leave blank to keep)' : 'Cashier Quick PIN (optional)'}</label>
          <input inputMode="numeric" maxLength={6} placeholder="4–6 digits" className="input" {...register('pin')} />
          <FieldError error={errors.pin} />
        </div>
        <div>
          <label className="label">Assigned Branch</label>
          <select className="input" {...register('branch_uuid')}>
            <option value="">Global / All Branches</option>
            {(branches || []).map((b) => (
              <option key={b.uuid} value={b.uuid}>{b.name}</option>
            ))}
          </select>
        </div>
      </div>

      <fieldset>
        <legend className="label">Assigned Roles *</legend>
        <div className="grid grid-cols-2 gap-2 rounded-lg border border-slate-200 bg-slate-50 p-3 sm:grid-cols-3">
          {(roles || []).map((r) => (
            <label key={r.code} className="flex items-center gap-2 text-sm font-medium text-slate-700">
              <input
                type="checkbox"
                value={r.code}
                disabled={isSelf && !isSuperAdmin}
                {...register('role_codes')}
                className="rounded border-slate-300 text-gold-600 focus:ring-gold-500"
              />
              <span>{r.name}</span>
            </label>
          ))}
        </div>
        <FieldError error={errors.role_codes} />
        {isSelf && !isSuperAdmin && (
          <p className="mt-1 text-xs text-amber-700">You cannot edit your own role.</p>
        )}
      </fieldset>

      <div className="grid gap-3 sm:grid-cols-2">
        <div>
          <label className="label">Profile Image URL (optional)</label>
          <input className="input" placeholder="https://..." {...register('profile_image_url')} />
        </div>
        <div>
          <label className="label">Notes / Employee ID</label>
          <input className="input" placeholder="e.g. Shift Lead / EMP-102" {...register('notes')} />
        </div>
      </div>

      <div className="flex flex-wrap items-center gap-6 pt-2">
        <label className="flex items-center gap-2 text-sm font-medium text-slate-800">
          <input
            type="checkbox"
            disabled={isSelf && !isSuperAdmin}
            {...register('is_active')}
            className="rounded border-slate-300 text-gold-600 focus:ring-gold-500"
          />
          <span>Account Active</span>
        </label>
        <label className="flex items-center gap-2 text-sm font-medium text-slate-800">
          <input
            type="checkbox"
            {...register('must_change_password')}
            className="rounded border-slate-300 text-gold-600 focus:ring-gold-500"
          />
          <span>Force password change on next login</span>
        </label>
      </div>

      <div className="flex justify-end gap-2 border-t border-slate-100 pt-3">
        <button type="button" className="btn-ghost" onClick={onCancel}>Cancel</button>
        <button className="btn-primary" disabled={isSubmitting}>
          {isSubmitting && <Spinner className="h-4 w-4" />}
          {editing ? 'Save Changes' : 'Create User'}
        </button>
      </div>
    </form>
  );
}

function UserAuditLogsModal({ user, onClose }) {
  const [logs, setLogs] = useState([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    if (!user) return;
    setLoading(true);
    api.get(`/users/${user.uuid}/audit-logs`)
      .then((res) => setLogs(res.data || []))
      .catch(() => setLogs([]))
      .finally(() => setLoading(false));
  }, [user]);

  return (
    <Modal open={!!user} title={`Activity & Audit Logs — ${user?.full_name}`} onClose={onClose}>
      {loading ? (
        <Loading />
      ) : logs.length === 0 ? (
        <Empty title="No audit logs recorded for this user yet" />
      ) : (
        <div className="max-h-96 overflow-y-auto space-y-2">
          {logs.map((log) => (
            <div key={log.id} className="rounded-lg border border-slate-200 bg-slate-50 p-3 text-sm">
              <div className="flex items-center justify-between font-semibold text-royal-900">
                <span className="badge bg-royal-100 text-royal-800">{log.action}</span>
                <span className="text-xs text-slate-500">{formatDateTime(log.occurred_at)}</span>
              </div>
              <div className="mt-1 text-xs text-slate-600">IP: {log.ip_address || 'local'}</div>
              {log.new_values && (
                <pre className="mt-1 overflow-x-auto rounded bg-white p-2 text-xs text-slate-700">
                  {JSON.stringify(log.new_values, null, 2)}
                </pre>
              )}
            </div>
          ))}
        </div>
      )}
    </Modal>
  );
}

export default function Users() {
  const toast = useToast();
  const { user: authUser } = useAuth();
  const [params, setParams] = useState({ page: 1, limit: 20, search: '', role: '', is_active: '' });
  const users = useApi('/users', params);
  const roles = useApi('/roles');
  const branches = useApi('/branches');

  const [editing, setEditing] = useState(null);
  const [deleting, setDeleting] = useState(null);
  const [resetting, setResetting] = useState(null);
  const [resetMustChange, setResetMustChange] = useState(true);
  const [viewingLogs, setViewingLogs] = useState(null);
  const [newPw, setNewPw] = useState('');
  const [busy, setBusy] = useState(false);

  const isPwValid = newPw.length >= 8 &&
    /[A-Z]/.test(newPw) &&
    /[a-z]/.test(newPw) &&
    /\d/.test(newPw) &&
    /[^A-Za-z0-9]/.test(newPw);

  const doToggleActive = async (u) => {
    try {
      const endpoint = u.is_active ? `/users/${u.uuid}/deactivate` : `/users/${u.uuid}/activate`;
      await api.post(endpoint);
      toast(u.is_active ? 'User account deactivated' : 'User account activated');
      users.reload();
    } catch (e) {
      toast(errorMessage(e), 'error');
    }
  };

  const doDelete = async () => {
    setBusy(true);
    try {
      await api.delete(`/users/${deleting.uuid}`);
      toast('User deleted successfully');
      setDeleting(null);
      users.reload();
    } catch (e) {
      toast(errorMessage(e), 'error');
    } finally {
      setBusy(false);
    }
  };

  const doReset = async () => {
    if (!isPwValid) {
      toast('Password does not meet complexity requirements', 'error');
      return;
    }
    setBusy(true);
    try {
      await api.post(`/users/${resetting.uuid}/reset-password`, { new_password: newPw, force_change: resetMustChange });
      toast(resetMustChange ? 'Password updated — user will be forced to change it on next login' : 'Password updated successfully');
      setResetting(null);
      setNewPw('');
    } catch (e) {
      toast(errorMessage(e), 'error');
    } finally {
      setBusy(false);
    }
  };

  return (
    <>
      <PageHeader
        title="User Management"
        subtitle="Staff credentials, role assignments, security policies and branch access"
        actions={
          <button className="btn-gold shadow-sm" onClick={() => setEditing('new')}>
            + New User
          </button>
        }
      />

      <div className="card p-0 shadow-md">
        {/* Search & Filters */}
        <div className="flex flex-wrap items-center gap-3 border-b border-slate-200 bg-slate-50/70 p-4">
          <input
            className="input max-w-xs bg-white"
            placeholder="Search username, name, email, phone…"
            value={params.search}
            onChange={(e) => setParams({ ...params, search: e.target.value, page: 1 })}
          />

          <select
            className="input max-w-xs bg-white"
            value={params.role}
            onChange={(e) => setParams({ ...params, role: e.target.value, page: 1 })}
          >
            <option value="">All Roles</option>
            {(roles.data || []).map((r) => (
              <option key={r.code} value={r.code}>{r.name}</option>
            ))}
          </select>

          <select
            className="input max-w-xs bg-white"
            value={params.is_active}
            onChange={(e) => setParams({ ...params, is_active: e.target.value, page: 1 })}
          >
            <option value="">All Statuses</option>
            <option value="true">Active Only</option>
            <option value="false">Inactive / Disabled</option>
          </select>
        </div>

        {/* Users Table */}
        {users.loading ? (
          <Loading />
        ) : users.error ? (
          <div className="p-4"><ErrorBox message={users.error} onRetry={users.reload} /></div>
        ) : users.data.length === 0 ? (
          <Empty title="No users found matching your criteria" />
        ) : (
          <div className="overflow-x-auto">
            <table className="min-w-full divide-y divide-slate-200">
              <thead className="bg-slate-50 text-xs uppercase tracking-wider text-slate-500">
                <tr>
                  <th className="th">ID #</th>
                  <th className="th">Staff Member</th>
                  <th className="th">Username</th>
                  <th className="th">Roles</th>
                  <th className="th">Access</th>
                  <th className="th">Branch</th>
                  <th className="th">Status</th>
                  <th className="th">Last Login</th>
                  <th className="th text-right">Actions</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100 bg-white">
                {users.data.map((u) => {
                  const isCurrent = u.uuid === authUser?.uuid;
                  return (
                    <tr key={u.uuid} className="hover:bg-slate-50/60 transition">
                      <td className="td">
                        {u.id_number ? (
                          <span className="font-mono text-xs font-bold text-royal-900 bg-royal-100 px-2 py-0.5 rounded border border-royal-200">
                            {u.id_number}
                          </span>
                        ) : (
                          <span className="text-xs text-slate-400 font-mono">—</span>
                        )}
                      </td>
                      <td className="td">
                        <div className="flex items-center gap-3">
                          <div className="flex h-9 w-9 items-center justify-center rounded-full bg-royal-100 font-bold text-royal-800 text-sm">
                            {u.full_name?.charAt(0) || 'U'}
                          </div>
                          <div>
                            <div className="font-semibold text-royal-950 flex items-center gap-1.5">
                              {u.full_name}
                              {isCurrent && <span className="badge bg-gold-100 text-gold-800 text-[10px]">You</span>}
                            </div>
                            <div className="text-xs text-slate-500">{u.email || u.phone || 'No direct contact'}</div>
                          </div>
                        </div>
                      </td>

                      <td className="td font-mono text-sm text-slate-700">@{u.username}</td>

                      <td className="td">
                        <div className="flex flex-wrap gap-1">
                          {(u.roles || []).map((r) => (
                            <span key={r} className="badge bg-royal-900/10 text-royal-900 font-medium text-xs">
                              {r}
                            </span>
                          ))}
                        </div>
                      </td>

                      <td className="td">
                        {u.access_scope === 'DESKTOP' ? (
                          <span className="badge bg-blue-100 text-blue-900 text-[11px] font-medium">💻 Desktop</span>
                        ) : u.access_scope === 'WEB' ? (
                          <span className="badge bg-purple-100 text-purple-900 text-[11px] font-medium">🌐 Web</span>
                        ) : (
                          <span className="badge bg-emerald-100 text-emerald-900 text-[11px] font-medium">💻🌐 Both</span>
                        )}
                      </td>

                      <td className="td text-xs text-slate-600">
                        {u.branch_name || (u.branch_uuid ? 'Assigned' : 'Global (All)')}
                      </td>

                      <td className="td">
                        {u.is_active ? (
                          <span className="badge bg-emerald-100 text-emerald-800 font-semibold">Active</span>
                        ) : (
                          <span className="badge bg-rose-100 text-rose-800 font-semibold">Disabled</span>
                        )}
                        {u.must_change_password && (
                          <span className="ml-1.5 badge bg-amber-100 text-amber-800 text-[10px]">Pwd Reset Req</span>
                        )}
                      </td>

                      <td className="td text-xs text-slate-500">
                        {formatDateTime(u.last_login_at)}
                      </td>

                      <td className="td whitespace-nowrap text-right text-sm">
                        <button
                          className="mr-2.5 text-royal-700 hover:text-royal-900 font-medium underline"
                          onClick={() => setEditing(u)}
                        >
                          Edit
                        </button>

                        <button
                          className="mr-2.5 text-royal-700 hover:text-royal-900 font-medium underline"
                          onClick={() => { setResetting(u); setNewPw(''); }}
                        >
                          Reset Pwd
                        </button>

                        <button
                          className="mr-2.5 text-slate-600 hover:text-slate-800 font-medium underline"
                          onClick={() => setViewingLogs(u)}
                        >
                          Logs
                        </button>

                        <button
                          className={`mr-2.5 font-medium underline ${u.is_active ? 'text-amber-700 hover:text-amber-900' : 'text-emerald-700 hover:text-emerald-900'}`}
                          onClick={() => doToggleActive(u)}
                        >
                          {u.is_active ? 'Deactivate' : 'Activate'}
                        </button>

                        <button
                          className="text-maroon-600 hover:text-maroon-800 font-medium underline"
                          onClick={() => setDeleting(u)}
                        >
                          Delete
                        </button>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
        <Pagination meta={users.meta} onPage={(page) => setParams({ ...params, page })} />
      </div>

      {/* Edit / Create Modal */}
      <Modal
        open={!!editing}
        title={editing === 'new' ? 'Create Staff User' : `Edit User — ${editing?.full_name}`}
        onClose={() => setEditing(null)}
      >
        {editing && roles.data && (
          <UserForm
            user={editing === 'new' ? null : editing}
            roles={roles.data}
            branches={branches.data || []}
            currentAuthUser={authUser}
            onCancel={() => setEditing(null)}
            onDone={() => { setEditing(null); users.reload(); }}
          />
        )}
      </Modal>

      {/* Delete Confirmation */}
      <Confirm
        open={!!deleting}
        danger
        busy={busy}
        title="Delete User Account"
        confirmLabel="Delete User"
        onClose={() => setDeleting(null)}
        onConfirm={doDelete}
        message={`Are you sure you want to delete ${deleting?.full_name} (${deleting?.username})? All associated active sessions will be terminated. Historical audit and transaction records will be preserved.`}
      />

      {/* Password Reset Modal */}
      <Modal
        open={!!resetting}
        title={`Change Password — ${resetting?.full_name} (@${resetting?.username})`}
        onClose={() => setResetting(null)}
        footer={
          <>
            <button className="btn-ghost" onClick={() => setResetting(null)}>Cancel</button>
            <button className="btn-primary" disabled={busy || !isPwValid} onClick={doReset}>
              {busy && <Spinner className="h-4 w-4" />} Save Password
            </button>
          </>
        }
      >
        <div className="space-y-3">
          <div>
            <label className="label">New Password *</label>
            <input
              type="password"
              autoComplete="new-password"
              className="input"
              value={newPw}
              onChange={(e) => setNewPw(e.target.value)}
              placeholder="Enter strong password…"
            />
          </div>

          <div className="rounded-lg border border-slate-200 bg-slate-50 p-3 text-xs space-y-1">
            <div className="font-semibold text-slate-700">Password Requirements:</div>
            <div className={newPw.length >= 8 ? 'text-emerald-700' : 'text-slate-500'}>✓ At least 8 characters</div>
            <div className={/[A-Z]/.test(newPw) ? 'text-emerald-700' : 'text-slate-500'}>✓ One uppercase letter (A-Z)</div>
            <div className={/[a-z]/.test(newPw) ? 'text-emerald-700' : 'text-slate-500'}>✓ One lowercase letter (a-z)</div>
            <div className={/\d/.test(newPw) ? 'text-emerald-700' : 'text-slate-500'}>✓ One number (0-9)</div>
            <div className={/[^A-Za-z0-9]/.test(newPw) ? 'text-emerald-700' : 'text-slate-500'}>✓ One special character (!@#$%^&amp;*)</div>
          </div>

          <label className="flex items-center gap-2 pt-1 text-xs font-medium text-slate-700">
            <input
              type="checkbox"
              checked={resetMustChange}
              onChange={(e) => setResetMustChange(e.target.checked)}
              className="rounded border-slate-300 text-gold-600 focus:ring-gold-500"
            />
            <span>Require user to change password on next login</span>
          </label>
        </div>
      </Modal>

      {/* Audit Logs Viewer Modal */}
      <UserAuditLogsModal user={viewingLogs} onClose={() => setViewingLogs(null)} />
    </>
  );
}
