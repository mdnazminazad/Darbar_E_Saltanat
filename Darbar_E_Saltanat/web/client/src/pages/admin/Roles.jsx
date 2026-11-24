import { useEffect, useMemo, useState } from 'react';
import { api, errorMessage } from '../../api/client.js';
import { useApi } from '../../components/useApi.js';
import { useToast } from '../../components/Toast.jsx';
import { useAuth } from '../../auth/AuthContext.jsx';
import { PageHeader, Loading, ErrorBox, Modal, Confirm, Spinner, FieldError } from '../../components/ui.jsx';

export const ROLE_CONFIG = {
  SUPER_ADMIN: {
    prefix: 'ADM',
    label: 'Super Admin',
    defaultAccess: 'BOTH',
    accessLabel: '💻 Desktop POS & 🌐 Web Portal (Full Access)',
    desc: 'Full system control: cloud admin, financial reports, user management, and local POS configuration.',
  },
  MANAGER: {
    prefix: 'MGR',
    label: 'Manager',
    defaultAccess: 'BOTH',
    accessLabel: '💻 Desktop POS & 🌐 Web Management Portal',
    desc: 'Branch operational oversight: floor management, discounts, cash sessions, and web reports.',
  },
  CASHIER: {
    prefix: 'CSH',
    label: 'Cashier',
    defaultAccess: 'DESKTOP',
    accessLabel: '💻 Desktop POS Terminal',
    desc: 'Front counter operations: taking orders, cash register, card/MFS payments, bill printing, and KOT dispatch.',
  },
  WAITER: {
    prefix: 'WTR',
    label: 'Waiter',
    defaultAccess: 'DESKTOP',
    accessLabel: '💻 Desktop Table & POS',
    desc: 'Floor service: table seating, dine-in ordering, guest modifications, and KOT dispatch.',
  },
  KITCHEN: {
    prefix: 'KIT',
    label: 'Kitchen',
    defaultAccess: 'DESKTOP',
    accessLabel: '💻 Desktop KOT Screen',
    desc: 'Kitchen display system (KDS): real-time incoming KOT orders, item preparation status, and order ready marks.',
  },
  ACCOUNTANT: {
    prefix: 'ACC',
    label: 'Accountant',
    defaultAccess: 'WEB',
    accessLabel: '🌐 Web Management Portal',
    desc: 'Finance & bookkeeping: daily cash reconciliation, expense recording, tax/VAT reporting, and audit logs.',
  },
  INVENTORY_MANAGER: {
    prefix: 'INV',
    label: 'Inventory Manager',
    defaultAccess: 'WEB',
    accessLabel: '🌐 Web Management Portal',
    desc: 'Stock control: raw ingredient purchases, supplier management, recipe margins, and wastage tracking.',
  },
};

function generateStrongPassword() {
  const upper = 'ABCDEFGHJKLMNPQRSTUVWXYZ';
  const lower = 'abcdefghijkmnopqrstuvwxyz';
  const digits = '23456789';
  const special = '!@#$%^&*';
  const all = upper + lower + digits + special;
  const pwd = [
    upper[Math.floor(Math.random() * upper.length)],
    lower[Math.floor(Math.random() * lower.length)],
    digits[Math.floor(Math.random() * digits.length)],
    special[Math.floor(Math.random() * special.length)],
  ];
  for (let i = 0; i < 8; i++) {
    pwd.push(all[Math.floor(Math.random() * all.length)]);
  }
  return pwd.sort(() => 0.5 - Math.random()).join('');
}

function suggestNextId(roleCode, existingUsers = []) {
  const conf = ROLE_CONFIG[roleCode] || { prefix: roleCode.slice(0, 3).toUpperCase() };
  const prefix = conf.prefix;
  const regex = new RegExp(`^${prefix}[-_]?0*(\\d+)$`, 'i');
  let maxNum = 0;
  for (const u of existingUsers) {
    const id = u.id_number || u.username;
    const match = id?.match(regex);
    if (match) {
      const num = parseInt(match[1], 10);
      if (num > maxNum) maxNum = num;
    }
  }
  const nextNum = String(maxNum + 1).padStart(2, '0');
  return `${prefix}-${nextNum}`;
}

export default function Roles() {
  const toast = useToast();
  const { user: currentAuthUser } = useAuth();
  const roles = useApi('/roles');
  const perms = useApi('/permissions');
  const branches = useApi('/branches');
  const allUsers = useApi('/users', { limit: 100 });

  const [selected, setSelected] = useState(null);
  const [activeTab, setActiveTab] = useState('users'); // 'users' | 'perms'
  const [draftPerms, setDraftPerms] = useState(new Set());
  const [savingPerms, setSavingPerms] = useState(false);
  const [creatingRole, setCreatingRole] = useState(false);
  const [roleForm, setRoleForm] = useState({ code: '', name: '' });

  // User management modals
  const [userModal, setUserModal] = useState(null); // 'new' | { ...user } (edit)
  const [passwordModal, setPasswordModal] = useState(null); // user to reset
  const [deleteModal, setDeleteModal] = useState(null); // user to delete
  const [submittingUser, setSubmittingUser] = useState(false);

  // Form states
  const [formValues, setFormValues] = useState({
    id_number: '',
    username: '',
    full_name: '',
    email: '',
    phone: '',
    password: '',
    pin: '',
    branch_uuid: '',
    access_scope: 'BOTH',
    is_active: true,
    must_change_password: false,
  });
  const [formErrors, setFormErrors] = useState({});
  const [showPassword, setShowPassword] = useState(false);

  // Reset password states
  const [resetPw, setResetPw] = useState('');
  const [resetMustChange, setResetMustChange] = useState(false);
  const [showResetPw, setShowResetPw] = useState(true);

  useEffect(() => {
    if (!selected && roles.data?.length) {
      setSelected(roles.data[0]);
    }
  }, [roles.data, selected]);

  useEffect(() => {
    if (selected) {
      setDraftPerms(new Set(selected.permissions));
    }
  }, [selected]);

  const groupedPerms = useMemo(
    () => (perms.data || []).reduce((g, p) => ({ ...g, [p.module]: [...(g[p.module] || []), p] }), {}),
    [perms.data]
  );

  const locked = selected?.code === 'SUPER_ADMIN';
  const permsDirty = selected && (draftPerms.size !== selected.permissions.length || selected.permissions.some((p) => !draftPerms.has(p)));

  // Users in the selected role
  const roleUsers = useMemo(() => {
    if (!selected || !allUsers.data) return [];
    return allUsers.data.filter((u) => (u.roles || []).includes(selected.code));
  }, [selected, allUsers.data]);

  // Counts map: { ROLE_CODE: count }
  const userCountsByRole = useMemo(() => {
    const map = {};
    for (const u of (allUsers.data || [])) {
      for (const r of (u.roles || [])) {
        map[r] = (map[r] || 0) + 1;
      }
    }
    return map;
  }, [allUsers.data]);

  const togglePerm = (code) => {
    setDraftPerms((d) => {
      const n = new Set(d);
      n.has(code) ? n.delete(code) : n.add(code);
      return n;
    });
  };

  const savePerms = async () => {
    setSavingPerms(true);
    try {
      const r = await api.patch(`/roles/${selected.uuid}`, { permissions: [...draftPerms] });
      toast('Permissions saved successfully');
      await roles.reload();
      setSelected(r.data.data);
    } catch (e) {
      toast(errorMessage(e), 'error');
    } finally {
      setSavingPerms(false);
    }
  };

  const createRole = async () => {
    try {
      const r = await api.post('/roles', { ...roleForm, permissions: [] });
      toast('Role created');
      setCreatingRole(false);
      setRoleForm({ code: '', name: '' });
      await roles.reload();
      setSelected(r.data.data);
    } catch (e) {
      toast(errorMessage(e), 'error');
    }
  };

  // Open modal to add user for currently selected role
  const openAddUser = () => {
    const conf = ROLE_CONFIG[selected.code] || { defaultAccess: 'BOTH' };
    const nextId = suggestNextId(selected.code, allUsers.data || []);
    const suggestedUsername = nextId.toLowerCase().replace('-', '');
    const genPassword = generateStrongPassword();

    setFormValues({
      id_number: nextId,
      username: suggestedUsername,
      full_name: '',
      email: '',
      phone: '',
      password: genPassword,
      pin: '',
      branch_uuid: branches.data?.[0]?.uuid || '',
      access_scope: conf.defaultAccess,
      is_active: true,
      must_change_password: false,
    });
    setFormErrors({});
    setShowPassword(true);
    setUserModal('new');
  };

  // Open modal to edit existing user
  const openEditUser = (user) => {
    setFormValues({
      id_number: user.id_number || '',
      username: user.username,
      full_name: user.full_name,
      email: user.email || '',
      phone: user.phone || '',
      password: '',
      pin: '',
      branch_uuid: user.branch_uuid || '',
      access_scope: user.access_scope || 'BOTH',
      is_active: user.is_active,
      must_change_password: user.must_change_password,
    });
    setFormErrors({});
    setShowPassword(false);
    setUserModal(user);
  };

  // Save new or edited user
  const handleSaveUser = async (e) => {
    e.preventDefault();
    setFormErrors({});

    const errors = {};
    if (!formValues.full_name.trim()) errors.full_name = 'Full name is required';
    if (!formValues.username.trim()) errors.username = 'Username is required';
    if (userModal === 'new' && !formValues.password) errors.password = 'Password is required';
    if (formValues.password) {
      if (formValues.password.length < 8) errors.password = 'At least 8 characters';
      else if (!/[A-Z]/.test(formValues.password)) errors.password = 'Needs an uppercase letter';
      else if (!/[a-z]/.test(formValues.password)) errors.password = 'Needs a lowercase letter';
      else if (!/\d/.test(formValues.password)) errors.password = 'Needs a number';
      else if (!/[^A-Za-z0-9]/.test(formValues.password)) errors.password = 'Needs a special character';
    }
    if (formValues.pin && !/^\d{4,6}$/.test(formValues.pin)) {
      errors.pin = 'PIN must be 4 to 6 digits';
    }

    if (Object.keys(errors).length > 0) {
      setFormErrors(errors);
      return;
    }

    setSubmittingUser(true);
    try {
      const payload = {
        id_number: formValues.id_number.trim() || null,
        username: formValues.username.trim(),
        full_name: formValues.full_name.trim(),
        email: formValues.email.trim() || null,
        phone: formValues.phone.trim() || null,
        branch_uuid: formValues.branch_uuid || null,
        access_scope: formValues.access_scope,
        role_codes: userModal === 'new' ? [selected.code] : (userModal.roles || [selected.code]),
        is_active: formValues.is_active,
        must_change_password: formValues.must_change_password,
      };
      if (formValues.password) payload.password = formValues.password;
      if (formValues.pin) payload.pin = formValues.pin;

      if (userModal === 'new') {
        await api.post('/users', payload);
        toast(`User ${payload.username} (${payload.id_number || 'No ID'}) created successfully!`);
      } else {
        await api.put(`/users/${userModal.uuid}`, payload);
        toast(`User ${payload.username} updated successfully!`);
      }
      setUserModal(null);
      await allUsers.reload();
    } catch (err) {
      toast(errorMessage(err), 'error');
    } finally {
      setSubmittingUser(false);
    }
  };

  // Open reset password modal
  const openResetPassword = (user) => {
    setPasswordModal(user);
    setResetPw(generateStrongPassword());
    setResetMustChange(false);
    setShowResetPw(true);
  };

  const handleSaveResetPassword = async () => {
    if (resetPw.length < 8) {
      toast('Password must be at least 8 characters with upper, lower, number and special char', 'error');
      return;
    }
    setSubmittingUser(true);
    try {
      await api.post(`/users/${passwordModal.uuid}/reset-password`, {
        new_password: resetPw,
        force_change: resetMustChange,
      });
      toast(`Password reset for ${passwordModal.full_name} (${passwordModal.username})!`);
      setPasswordModal(null);
    } catch (err) {
      toast(errorMessage(err), 'error');
    } finally {
      setSubmittingUser(false);
    }
  };

  // Toggle user active status
  const handleToggleActive = async (user) => {
    try {
      const endpoint = user.is_active ? `/users/${user.uuid}/deactivate` : `/users/${user.uuid}/activate`;
      await api.post(endpoint);
      toast(user.is_active ? `User ${user.username} deactivated` : `User ${user.username} activated`);
      await allUsers.reload();
    } catch (err) {
      toast(errorMessage(err), 'error');
    }
  };

  // Delete user
  const handleDeleteUser = async () => {
    setSubmittingUser(true);
    try {
      await api.delete(`/users/${deleteModal.uuid}`);
      toast(`User ${deleteModal.username} deleted`);
      setDeleteModal(null);
      await allUsers.reload();
    } catch (err) {
      toast(errorMessage(err), 'error');
    } finally {
      setSubmittingUser(false);
    }
  };

  const copyToClipboard = (text, label = 'Copied') => {
    navigator.clipboard?.writeText(text);
    toast(`${label} to clipboard!`);
  };

  if (roles.loading || perms.loading) return <Loading />;
  if (roles.error || perms.error) return <ErrorBox message={roles.error || perms.error} onRetry={() => { roles.reload(); perms.reload(); }} />;

  const roleMeta = selected ? (ROLE_CONFIG[selected.code] || {
    prefix: selected.code.slice(0, 3).toUpperCase(),
    label: selected.name,
    defaultAccess: 'BOTH',
    accessLabel: '💻 Desktop POS & 🌐 Web Management Portal',
    desc: selected.description || 'Custom staff role.',
  }) : null;

  return (
    <>
      <PageHeader
        title="Roles &amp; User Credentials"
        subtitle="Manage usernames, passwords, categorized ID numbers, and permissions for each role"
        actions={
          <div className="flex gap-2">
            <button className="btn-ghost" onClick={() => allUsers.reload()}>↻ Refresh</button>
            <button className="btn-gold" onClick={() => setCreatingRole(true)}>+ New Custom Role</button>
          </div>
        }
      />

      <div className="grid gap-6 lg:grid-cols-[18rem_1fr]">
        {/* Left Side: Role Selector */}
        <div className="card p-2 space-y-1 self-start">
          <div className="px-3 py-2 text-[11px] font-bold uppercase tracking-wider text-slate-400">System &amp; Custom Roles</div>
          {roles.data.map((r) => {
            const count = userCountsByRole[r.code] || 0;
            const isSel = selected?.uuid === r.uuid;
            const conf = ROLE_CONFIG[r.code];
            return (
              <button
                key={r.uuid}
                onClick={() => { setSelected(r); }}
                className={`group flex w-full items-center justify-between rounded-lg px-3 py-2.5 text-left text-sm transition ${
                  isSel ? 'bg-royal-900 text-gold-300 font-semibold shadow-sm' : 'hover:bg-slate-100 text-slate-700'
                }`}
              >
                <div>
                  <div className="flex items-center gap-1.5">
                    <span>{r.name}</span>
                    {conf?.prefix && (
                      <span className={`text-[10px] font-mono px-1 py-0.5 rounded ${isSel ? 'bg-royal-800 text-gold-400' : 'bg-slate-200 text-slate-600'}`}>
                        {conf.prefix}
                      </span>
                    )}
                  </div>
                  <span className="block text-xs opacity-75">
                    {r.permissions.length} perms{r.is_system ? ' · system' : ''}
                  </span>
                </div>
                <span className={`rounded-full px-2 py-0.5 text-xs font-bold ${
                  count > 0 ? (isSel ? 'bg-gold-500 text-royal-950' : 'bg-royal-100 text-royal-900') : (isSel ? 'bg-royal-800 text-slate-300' : 'bg-slate-100 text-slate-400')
                }`}>
                  {count} {count === 1 ? 'user' : 'users'}
                </span>
              </button>
            );
          })}
        </div>

        {/* Right Side: Selected Role Hub */}
        {selected && (
          <div className="space-y-4">
            {/* Role Header Banner */}
            <div className="card border-l-4 border-l-gold-500 bg-gradient-to-r from-royal-900 to-royal-950 text-white p-5 shadow-md">
              <div className="flex flex-wrap items-start justify-between gap-4">
                <div>
                  <div className="flex items-center gap-2">
                    <h2 className="font-display text-2xl font-bold text-gold-400">{selected.name}</h2>
                    {selected.is_system ? (
                      <span className="rounded bg-amber-400/20 px-2 py-0.5 text-xs font-semibold text-amber-300 border border-amber-400/30">System Role</span>
                    ) : (
                      <span className="rounded bg-slate-700 px-2 py-0.5 text-xs font-semibold text-slate-300">Custom Role</span>
                    )}
                    {roleMeta?.prefix && (
                      <span className="rounded bg-gold-400/20 px-2 py-0.5 font-mono text-xs font-bold text-gold-300 border border-gold-400/30">
                        ID Prefix: {roleMeta.prefix}-##
                      </span>
                    )}
                  </div>
                  <p className="mt-1 text-sm text-slate-300">{roleMeta?.desc}</p>
                  <div className="mt-2 flex items-center gap-2 text-xs text-gold-300 font-medium">
                    <span className="rounded-full bg-royal-800 px-2.5 py-1 border border-gold-500/30">
                      Target Access: {roleMeta?.accessLabel}
                    </span>
                    <span className="rounded-full bg-royal-800 px-2.5 py-1 border border-royal-700">
                      {selected.permissions.length} Granular Permissions Assigned
                    </span>
                  </div>
                </div>

                <div className="flex items-center gap-2">
                  <button
                    className="btn-gold flex items-center gap-1.5 shadow-md"
                    onClick={openAddUser}
                  >
                    <span>+ Add {selected.name} User</span>
                  </button>
                </div>
              </div>

              {/* Tabs Navigation */}
              <div className="mt-6 flex border-b border-royal-800 gap-2">
                <button
                  onClick={() => setActiveTab('users')}
                  className={`flex items-center gap-2 px-4 py-2.5 text-sm font-semibold transition border-b-2 -mb-px ${
                    activeTab === 'users'
                      ? 'border-gold-400 text-gold-400 bg-royal-800/50 rounded-t-lg'
                      : 'border-transparent text-slate-400 hover:text-slate-200'
                  }`}
                >
                  <span>👥 Assigned Staff &amp; Passwords</span>
                  <span className="rounded-full bg-gold-500/20 px-2 py-0.5 text-xs text-gold-300 font-bold">
                    {roleUsers.length}
                  </span>
                </button>
                <button
                  onClick={() => setActiveTab('perms')}
                  className={`flex items-center gap-2 px-4 py-2.5 text-sm font-semibold transition border-b-2 -mb-px ${
                    activeTab === 'perms'
                      ? 'border-gold-400 text-gold-400 bg-royal-800/50 rounded-t-lg'
                      : 'border-transparent text-slate-400 hover:text-slate-200'
                  }`}
                >
                  <span>🛡️ Role Permissions Checklist</span>
                  <span className="rounded-full bg-slate-700 px-2 py-0.5 text-xs text-slate-300 font-bold">
                    {selected.permissions.length}
                  </span>
                </button>
              </div>
            </div>

            {/* TAB 1: Assigned Staff & Password Management */}
            {activeTab === 'users' && (
              <div className="card p-0 shadow-md">
                <div className="flex flex-wrap items-center justify-between gap-3 border-b border-slate-200 bg-slate-50/70 p-4">
                  <div>
                    <h3 className="font-semibold text-royal-950">Staff Accounts under {selected.name}</h3>
                    <p className="text-xs text-slate-500">
                      Super Admin can manage user IDs, usernames, and passwords directly for this role.
                    </p>
                  </div>
                  <button className="btn-gold text-xs py-1.5 px-3" onClick={openAddUser}>
                    + Add {selected.name} User
                  </button>
                </div>

                {roleUsers.length === 0 ? (
                  <div className="p-8 text-center">
                    <div className="mx-auto flex h-12 w-12 items-center justify-center rounded-full bg-gold-100 text-gold-700 text-xl font-bold">
                      {roleMeta?.prefix || selected.name.charAt(0)}
                    </div>
                    <h4 className="mt-3 text-base font-semibold text-royal-950">No {selected.name} users created yet</h4>
                    <p className="mt-1 text-sm text-slate-500 max-w-md mx-auto">
                      Click below to create staff credentials for {selected.name}. The system will auto-assign ID <span className="font-mono font-semibold text-royal-900">{suggestNextId(selected.code, allUsers.data || [])}</span> and let you set their password.
                    </p>
                    <button className="btn-gold mt-4 shadow-sm" onClick={openAddUser}>
                      + Create {selected.name} User Now
                    </button>
                  </div>
                ) : (
                  <div className="overflow-x-auto">
                    <table className="min-w-full divide-y divide-slate-200">
                      <thead className="bg-slate-50 text-xs uppercase tracking-wider text-slate-500">
                        <tr>
                          <th className="th">Categorized ID</th>
                          <th className="th">Staff Member</th>
                          <th className="th">Login Username</th>
                          <th className="th">Access Scope</th>
                          <th className="th">Branch</th>
                          <th className="th">POS PIN</th>
                          <th className="th">Status</th>
                          <th className="th text-right">Actions</th>
                        </tr>
                      </thead>
                      <tbody className="divide-y divide-slate-100 bg-white">
                        {roleUsers.map((u) => {
                          const isCurrent = u.uuid === currentAuthUser?.uuid;
                          const idNum = u.id_number || (roleMeta?.prefix ? `${roleMeta.prefix}-${u.username}` : u.username);
                          return (
                            <tr key={u.uuid} className="hover:bg-slate-50/60 transition">
                              {/* Categorized ID */}
                              <td className="td">
                                <span className="inline-flex items-center px-2.5 py-1 rounded-md text-xs font-mono font-bold bg-royal-100 text-royal-900 border border-royal-200 shadow-2xs">
                                  {idNum}
                                </span>
                              </td>

                              {/* Staff Member */}
                              <td className="td">
                                <div className="flex items-center gap-2.5">
                                  <div className="flex h-8 w-8 items-center justify-center rounded-full bg-royal-900 font-bold text-gold-300 text-xs">
                                    {u.full_name?.charAt(0) || 'U'}
                                  </div>
                                  <div>
                                    <div className="font-semibold text-royal-950 text-sm flex items-center gap-1">
                                      {u.full_name}
                                      {isCurrent && <span className="badge bg-gold-100 text-gold-800 text-[10px]">You</span>}
                                    </div>
                                    <div className="text-xs text-slate-500">{u.phone || u.email || 'No contact'}</div>
                                  </div>
                                </div>
                              </td>

                              {/* Username */}
                              <td className="td">
                                <span className="font-mono text-xs font-medium text-slate-800 bg-slate-100 px-2 py-0.5 rounded">
                                  @{u.username}
                                </span>
                              </td>

                              {/* Access Scope */}
                              <td className="td">
                                {u.access_scope === 'DESKTOP' ? (
                                  <span className="badge bg-blue-100 text-blue-900 font-medium text-xs">💻 Desktop Only</span>
                                ) : u.access_scope === 'WEB' ? (
                                  <span className="badge bg-purple-100 text-purple-900 font-medium text-xs">🌐 Web Only</span>
                                ) : (
                                  <span className="badge bg-emerald-100 text-emerald-900 font-medium text-xs">💻🌐 Desktop &amp; Web</span>
                                )}
                              </td>

                              {/* Branch */}
                              <td className="td text-xs text-slate-600">
                                {u.branch_name || 'All Branches'}
                              </td>

                              {/* Quick PIN */}
                              <td className="td">
                                {u.has_pin ? (
                                  <span className="font-mono text-xs text-slate-600 bg-slate-100 px-1.5 py-0.5 rounded">PIN Set</span>
                                ) : (
                                  <span className="text-xs text-slate-400">—</span>
                                )}
                              </td>

                              {/* Status */}
                              <td className="td">
                                {u.is_active ? (
                                  <span className="badge bg-emerald-100 text-emerald-800 font-semibold">Active</span>
                                ) : (
                                  <span className="badge bg-rose-100 text-rose-800 font-semibold">Disabled</span>
                                )}
                                {u.must_change_password && (
                                  <span className="ml-1 badge bg-amber-100 text-amber-800 text-[10px]">Pwd Reset Req</span>
                                )}
                              </td>

                              {/* Actions */}
                              <td className="td text-right whitespace-nowrap text-sm">
                                <button
                                  className="btn-gold mr-2 text-xs py-1 px-2.5 shadow-2xs font-semibold"
                                  onClick={() => openResetPassword(u)}
                                  title="Change or set user password"
                                >
                                  🔑 Set Password
                                </button>
                                <button
                                  className="mr-2 text-royal-700 hover:text-royal-900 font-medium text-xs underline"
                                  onClick={() => openEditUser(u)}
                                >
                                  Edit
                                </button>
                                <button
                                  className={`mr-2 font-medium text-xs underline ${u.is_active ? 'text-amber-700 hover:text-amber-900' : 'text-emerald-700 hover:text-emerald-900'}`}
                                  onClick={() => handleToggleActive(u)}
                                >
                                  {u.is_active ? 'Deactivate' : 'Activate'}
                                </button>
                                <button
                                  className="text-maroon-600 hover:text-maroon-800 font-medium text-xs underline"
                                  onClick={() => setDeleteModal(u)}
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
              </div>
            )}

            {/* TAB 2: Permissions Checklist */}
            {activeTab === 'perms' && (
              <div className="card shadow-md">
                <div className="mb-4 flex items-center justify-between border-b border-slate-100 pb-3">
                  <div>
                    <h3 className="font-semibold text-royal-900">Fine-grained Permissions for {selected.name}</h3>
                    <p className="text-xs text-slate-500">Check or uncheck permissions to grant or revoke system capabilities.</p>
                  </div>
                  <button
                    className="btn-primary"
                    disabled={locked || !permsDirty || savingPerms}
                    onClick={savePerms}
                  >
                    {savingPerms && <Spinner className="h-4 w-4" />}
                    Save Changes ({draftPerms.size} perms)
                  </button>
                </div>

                {locked && (
                  <div className="mb-4 rounded-lg bg-amber-50 p-3 text-xs text-amber-800 border border-amber-200">
                    <strong>Note:</strong> Super Admin is the system administrator and inherently retains all 39 permissions.
                  </div>
                )}

                <div className="grid gap-6 md:grid-cols-2">
                  {Object.entries(groupedPerms).map(([mod, list]) => (
                    <fieldset key={mod} className="rounded-lg border border-slate-200 bg-slate-50/50 p-3">
                      <legend className="px-2 text-xs font-bold uppercase tracking-wide text-gold-700 bg-white border border-slate-200 rounded">
                        {mod} ({list.filter(p => locked || draftPerms.has(p.code)).length}/{list.length})
                      </legend>
                      <div className="space-y-1.5 mt-2">
                        {list.map((p) => {
                          const checked = locked || draftPerms.has(p.code);
                          return (
                            <label key={p.code} className="flex items-start gap-2.5 py-1 text-sm hover:bg-slate-100/70 rounded px-1.5 cursor-pointer">
                              <input
                                type="checkbox"
                                className="mt-0.5 rounded border-slate-300 text-gold-600 focus:ring-gold-500"
                                disabled={locked}
                                checked={checked}
                                onChange={() => togglePerm(p.code)}
                              />
                              <div>
                                <span className={`font-medium ${checked ? 'text-royal-950' : 'text-slate-600'}`}>{p.description}</span>
                                <span className="block text-[11px] font-mono text-slate-400">{p.code}</span>
                              </div>
                            </label>
                          );
                        })}
                      </div>
                    </fieldset>
                  ))}
                </div>
              </div>
            )}
          </div>
        )}
      </div>

      {/* Add / Edit User Modal */}
      <Modal
        open={!!userModal}
        title={userModal === 'new' ? `Create Staff User — Role: ${selected?.name}` : `Edit Staff User — ${userModal?.full_name}`}
        onClose={() => setUserModal(null)}
        footer={
          <div className="flex justify-between w-full">
            <button type="button" className="btn-ghost" onClick={() => setUserModal(null)}>Cancel</button>
            <button type="button" className="btn-primary" disabled={submittingUser} onClick={handleSaveUser}>
              {submittingUser && <Spinner className="h-4 w-4" />}
              {userModal === 'new' ? `Create ${selected?.name} User` : 'Save Changes'}
            </button>
          </div>
        }
      >
        <form onSubmit={handleSaveUser} className="space-y-4">
          {/* Categorized ID Number & Username */}
          <div className="grid gap-3 sm:grid-cols-2">
            <div>
              <label className="label">
                Staff ID Number * <span className="text-xs font-normal text-slate-500">(Categorized by {selected?.name})</span>
              </label>
              <div className="relative">
                <input
                  className="input font-mono font-bold text-royal-900 bg-amber-50/50"
                  placeholder="e.g. CSH-01"
                  value={formValues.id_number}
                  onChange={(e) => setFormValues({ ...formValues, id_number: e.target.value })}
                />
                <button
                  type="button"
                  onClick={() => {
                    const next = suggestNextId(selected.code, allUsers.data || []);
                    setFormValues({ ...formValues, id_number: next });
                  }}
                  className="absolute right-2 top-2 text-xs text-gold-700 font-semibold hover:underline"
                  title="Auto-suggest next number"
                >
                  Auto #
                </button>
              </div>
              <p className="mt-1 text-[11px] text-slate-500">Role code prefix: <span className="font-mono font-bold text-royal-900">{roleMeta?.prefix}</span></p>
            </div>

            <div>
              <label className="label">Login Username *</label>
              <input
                className="input font-mono"
                placeholder="e.g. cashier01"
                value={formValues.username}
                onChange={(e) => setFormValues({ ...formValues, username: e.target.value })}
              />
              <FieldError error={formErrors.username ? { message: formErrors.username } : null} />
              <p className="mt-1 text-[11px] text-slate-500">Staff can log in with username or ID number.</p>
            </div>
          </div>

          {/* Full Name & Branch */}
          <div className="grid gap-3 sm:grid-cols-2">
            <div>
              <label className="label">Staff Full Name *</label>
              <input
                className="input"
                placeholder="e.g. Mohammad Rahim"
                value={formValues.full_name}
                onChange={(e) => setFormValues({ ...formValues, full_name: e.target.value })}
              />
              <FieldError error={formErrors.full_name ? { message: formErrors.full_name } : null} />
            </div>

            <div>
              <label className="label">Assigned Branch</label>
              <select
                className="input"
                value={formValues.branch_uuid}
                onChange={(e) => setFormValues({ ...formValues, branch_uuid: e.target.value })}
              >
                <option value="">Global / All Branches</option>
                {(branches.data || []).map((b) => (
                  <option key={b.uuid} value={b.uuid}>{b.name}</option>
                ))}
              </select>
            </div>
          </div>

          {/* Password (Super Admin Set) */}
          <div>
            <div className="flex items-center justify-between">
              <label className="label">
                {userModal === 'new' ? 'Password (Set by Super Admin) *' : 'Change Password (leave blank to keep current)'}
              </label>
              <div className="flex gap-2">
                <button
                  type="button"
                  className="text-xs text-royal-700 font-medium hover:underline"
                  onClick={() => {
                    const pw = generateStrongPassword();
                    setFormValues({ ...formValues, password: pw });
                    setShowPassword(true);
                  }}
                >
                  ⚡ Generate Strong
                </button>
                {formValues.password && (
                  <button
                    type="button"
                    className="text-xs text-gold-700 font-medium hover:underline"
                    onClick={() => copyToClipboard(formValues.password, 'Password copied')}
                  >
                    📋 Copy
                  </button>
                )}
              </div>
            </div>
            <div className="relative">
              <input
                type={showPassword ? 'text' : 'password'}
                autoComplete="new-password"
                className="input pr-16 font-mono"
                placeholder={userModal === 'new' ? 'Enter strong password…' : 'Enter new password to change…'}
                value={formValues.password}
                onChange={(e) => setFormValues({ ...formValues, password: e.target.value })}
              />
              <button
                type="button"
                className="absolute right-2 top-2 text-xs font-semibold text-slate-500 hover:text-slate-800"
                onClick={() => setShowPassword(!showPassword)}
              >
                {showPassword ? 'Hide' : 'Show'}
              </button>
            </div>
            <FieldError error={formErrors.password ? { message: formErrors.password } : null} />
            <p className="mt-1 text-xs text-slate-500">Min 8 chars, with uppercase, lowercase, number &amp; symbol.</p>
          </div>

          {/* Access Scope & Quick POS PIN */}
          <div className="grid gap-3 sm:grid-cols-2">
            <div>
              <label className="label">Access Destination Platform *</label>
              <select
                className="input font-medium"
                value={formValues.access_scope}
                onChange={(e) => setFormValues({ ...formValues, access_scope: e.target.value })}
              >
                <option value="DESKTOP">💻 Desktop POS Terminal Only</option>
                <option value="WEB">🌐 Web Management Portal Only</option>
                <option value="BOTH">💻🌐 Both (Desktop &amp; Web Portal)</option>
              </select>
              <p className="mt-1 text-[11px] text-slate-500">Controls whether staff accesses Desktop POS, Web, or both.</p>
            </div>

            <div>
              <label className="label">Quick POS PIN (optional)</label>
              <input
                type="text"
                inputMode="numeric"
                maxLength={6}
                placeholder="4–6 digits (e.g. 1234)"
                className="input font-mono"
                value={formValues.pin}
                onChange={(e) => setFormValues({ ...formValues, pin: e.target.value })}
              />
              <FieldError error={formErrors.pin ? { message: formErrors.pin } : null} />
              <p className="mt-1 text-[11px] text-slate-500">Fast sign-in / unlock PIN for POS terminals.</p>
            </div>
          </div>

          {/* Contact: Phone & Email */}
          <div className="grid gap-3 sm:grid-cols-2">
            <div>
              <label className="label">Phone Number (optional)</label>
              <input
                className="input"
                placeholder="e.g. 01711000000"
                value={formValues.phone}
                onChange={(e) => setFormValues({ ...formValues, phone: e.target.value })}
              />
            </div>
            <div>
              <label className="label">Email Address (optional)</label>
              <input
                type="email"
                className="input"
                placeholder="staff@restaurant.com"
                value={formValues.email}
                onChange={(e) => setFormValues({ ...formValues, email: e.target.value })}
              />
            </div>
          </div>

          {/* Checkboxes */}
          <div className="flex flex-wrap items-center gap-6 pt-2 border-t border-slate-100">
            <label className="flex items-center gap-2 text-sm font-medium text-slate-800 cursor-pointer">
              <input
                type="checkbox"
                checked={formValues.is_active}
                onChange={(e) => setFormValues({ ...formValues, is_active: e.target.checked })}
                className="rounded border-slate-300 text-gold-600 focus:ring-gold-500"
              />
              <span>Account Active</span>
            </label>
            <label className="flex items-center gap-2 text-sm font-medium text-slate-800 cursor-pointer">
              <input
                type="checkbox"
                checked={formValues.must_change_password}
                onChange={(e) => setFormValues({ ...formValues, must_change_password: e.target.checked })}
                className="rounded border-slate-300 text-gold-600 focus:ring-gold-500"
              />
              <span>Force password change on next login</span>
            </label>
          </div>
        </form>
      </Modal>

      {/* Set / Reset Password Modal */}
      <Modal
        open={!!passwordModal}
        title={`Set Password — ${passwordModal?.full_name} (@${passwordModal?.username})`}
        onClose={() => setPasswordModal(null)}
        footer={
          <div className="flex justify-between w-full">
            <button type="button" className="btn-ghost" onClick={() => setPasswordModal(null)}>Cancel</button>
            <button
              type="button"
              className="btn-primary"
              disabled={submittingUser || resetPw.length < 8}
              onClick={handleSaveResetPassword}
            >
              {submittingUser && <Spinner className="h-4 w-4" />}
              Save New Password
            </button>
          </div>
        }
      >
        <div className="space-y-4">
          <div className="rounded-lg bg-royal-50 border border-royal-200 p-3 text-xs text-royal-900">
            <div>Staff Member: <strong>{passwordModal?.full_name}</strong></div>
            <div>Categorized ID: <strong className="font-mono">{passwordModal?.id_number || 'N/A'}</strong></div>
            <div>Username: <strong className="font-mono">@{passwordModal?.username}</strong></div>
          </div>

          <div>
            <div className="flex items-center justify-between">
              <label className="label">New Password *</label>
              <div className="flex gap-2">
                <button
                  type="button"
                  className="text-xs text-royal-700 font-semibold hover:underline"
                  onClick={() => setResetPw(generateStrongPassword())}
                >
                  ⚡ Generate Password
                </button>
                {resetPw && (
                  <button
                    type="button"
                    className="text-xs text-gold-700 font-semibold hover:underline"
                    onClick={() => copyToClipboard(resetPw, 'Password copied')}
                  >
                    📋 Copy
                  </button>
                )}
              </div>
            </div>
            <div className="relative">
              <input
                type={showResetPw ? 'text' : 'password'}
                autoComplete="new-password"
                className="input pr-16 font-mono text-base"
                value={resetPw}
                onChange={(e) => setResetPw(e.target.value)}
                placeholder="Enter password…"
              />
              <button
                type="button"
                className="absolute right-2 top-2 text-xs font-semibold text-slate-500 hover:text-slate-800"
                onClick={() => setShowResetPw(!showResetPw)}
              >
                {showResetPw ? 'Hide' : 'Show'}
              </button>
            </div>
          </div>

          <div className="rounded-lg border border-slate-200 bg-slate-50 p-3 text-xs space-y-1">
            <div className="font-semibold text-slate-700">Password Checklist:</div>
            <div className={resetPw.length >= 8 ? 'text-emerald-700' : 'text-slate-500'}>✓ At least 8 characters</div>
            <div className={/[A-Z]/.test(resetPw) ? 'text-emerald-700' : 'text-slate-500'}>✓ One uppercase letter (A-Z)</div>
            <div className={/[a-z]/.test(resetPw) ? 'text-emerald-700' : 'text-slate-500'}>✓ One lowercase letter (a-z)</div>
            <div className={/\d/.test(resetPw) ? 'text-emerald-700' : 'text-slate-500'}>✓ One number (0-9)</div>
            <div className={/[^A-Za-z0-9]/.test(resetPw) ? 'text-emerald-700' : 'text-slate-500'}>✓ One special character (!@#$%^&*)</div>
          </div>

          <label className="flex items-center gap-2 text-xs font-medium text-slate-700 cursor-pointer">
            <input
              type="checkbox"
              checked={resetMustChange}
              onChange={(e) => setResetMustChange(e.target.checked)}
              className="rounded border-slate-300 text-gold-600 focus:ring-gold-500"
            />
            <span>Require staff to change password on their next login</span>
          </label>
        </div>
      </Modal>

      {/* Delete Confirmation */}
      <Confirm
        open={!!deleteModal}
        danger
        busy={submittingUser}
        title="Delete Staff Account"
        confirmLabel="Delete User"
        onClose={() => setDeleteModal(null)}
        onConfirm={handleDeleteUser}
        message={`Are you sure you want to delete ${deleteModal?.full_name} (@${deleteModal?.username})? Their active sessions will be terminated.`}
      />

      {/* New Custom Role Modal */}
      <Modal
        open={creatingRole}
        title="New Custom Role"
        onClose={() => setCreatingRole(false)}
        footer={
          <>
            <button className="btn-ghost" onClick={() => setCreatingRole(false)}>Cancel</button>
            <button
              className="btn-primary"
              disabled={!/^[A-Za-z][A-Za-z0-9_]{1,39}$/.test(roleForm.code) || roleForm.name.trim().length < 2}
              onClick={createRole}
            >
              Create Role
            </button>
          </>
        }
      >
        <label className="label">Code (e.g. HOST / DELIVERY_BOY)</label>
        <input
          className="input mb-3 font-mono"
          placeholder="e.g. HOST"
          value={roleForm.code}
          onChange={(e) => setRoleForm({ ...roleForm, code: e.target.value.toUpperCase() })}
        />
        <label className="label">Display Name</label>
        <input
          className="input"
          placeholder="e.g. Floor Hostess"
          value={roleForm.name}
          onChange={(e) => setRoleForm({ ...roleForm, name: e.target.value })}
        />
      </Modal>
    </>
  );
}
