import { useState, useEffect } from 'react';
import { NavLink, Outlet } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext.jsx';
import { api } from '../api/client.js';

const ADMIN_NAV = [
  { to: '/admin', label: 'Overview', end: true },
  { to: '/admin/users', label: 'Users', perm: 'users.manage' },
  { to: '/admin/roles', label: 'Roles & Permissions', perm: 'roles.manage' },
  { to: '/admin/branches', label: 'Branches' },
  { to: '/admin/devices', label: 'Devices & Sync', perm: 'sync.view' },
  { to: '/admin/settings', label: 'Settings' },
  { to: '/admin/audit-logs', label: 'Audit Logs', perm: 'audit.view' },
];

const FALLBACK_NAV = [
  { to: '/admin/pos', label: 'Fallback POS', perm: 'orders.create' },
  { to: '/admin/orders', label: 'Web Orders', perm: 'orders.view' },
  { to: '/admin/tables', label: 'Table Floor', perm: 'orders.view' },
  { to: '/admin/inventory', label: 'Inventory', perm: 'inventory.view' },
  { to: '/admin/reports', label: 'Reports', perm: 'reports.view' },
];

export function Crown({ className = 'h-8 w-8' }) {
  return <img src="/logo.webp" alt="Dawat-e-Darbar Logo" className={`${className} object-contain rounded-md`} />;
}

export default function AdminLayout() {
  const { user, logout, can } = useAuth();
  const [open, setOpen] = useState(false);
  const [apiOnline, setApiOnline] = useState(true);
  const [serverTime, setServerTime] = useState('');

  // Live API Health heartbeat monitor
  useEffect(() => {
    async function checkHealth() {
      try {
        const res = await api.get('/health');
        if (res.data?.status === 'ok') {
          setApiOnline(true);
          setServerTime(new Date(res.data.serverTime).toLocaleTimeString());
        } else {
          setApiOnline(false);
        }
      } catch {
        setApiOnline(false);
      }
    }
    checkHealth();
    const interval = setInterval(checkHealth, 30000); // 30s heartbeat
    return () => clearInterval(interval);
  }, []);

  const adminItems = ADMIN_NAV.filter((n) => !n.perm || can(n.perm));
  const fallbackItems = FALLBACK_NAV.filter((n) => !n.perm || can(n.perm));
  const link = ({ isActive }) =>
    `block rounded-lg px-3 py-2 text-sm font-medium transition ${
      isActive ? 'bg-gold-500 text-royal-950 font-semibold' : 'text-slate-300 hover:bg-royal-800 hover:text-gold-300'
    }`;

  return (
    <div className="min-h-screen lg:flex">
      {/* Sidebar */}
      <aside className={`fixed inset-y-0 left-0 z-30 w-64 transform bg-royal-900 p-4 transition lg:static lg:translate-x-0 ${open ? 'translate-x-0' : '-translate-x-full'}`}>
        <div className="mb-6 flex items-center gap-3 px-2">
          <Crown className="h-10 w-10 ring-1 ring-gold-500/40 shadow" />
          <div>
            <p className="font-display text-lg font-bold leading-tight text-gold-400">Darbar_E_Saltanat</p>
            <p className="text-xs text-slate-400">Management &amp; POS</p>
          </div>
        </div>

        {/* Administration Links */}
        <div className="space-y-4">
          <div>
            <p className="px-3 text-[11px] font-bold uppercase tracking-wider text-slate-400 mb-1">Administration</p>
            <nav className="space-y-1">
              {adminItems.map((n) => (
                <NavLink key={n.to} to={n.to} end={n.end} className={link} onClick={() => setOpen(false)}>
                  {n.label}
                </NavLink>
              ))}
            </nav>
          </div>

          {/* Operational Fallback POS Links */}
          {fallbackItems.length > 0 && (
            <div>
              <p className="px-3 text-[11px] font-bold uppercase tracking-wider text-amber-400/90 mb-1 flex items-center gap-1.5">
                <span className="h-1.5 w-1.5 rounded-full bg-amber-400"></span>
                Operational Fallback
              </p>
              <nav className="space-y-1">
                {fallbackItems.map((n) => (
                  <NavLink key={n.to} to={n.to} className={link} onClick={() => setOpen(false)}>
                    {n.label}
                  </NavLink>
                ))}
              </nav>
            </div>
          )}
        </div>
      </aside>

      {open && <div className="fixed inset-0 z-20 bg-black/40 lg:hidden" onClick={() => setOpen(false)} />}

      <div className="flex-1 flex flex-col min-w-0">
        {/* Topbar Header */}
        <header className="sticky top-0 z-10 flex items-center justify-between border-b border-slate-200 bg-white/95 px-4 py-3 backdrop-blur lg:px-8">
          <button className="btn-ghost lg:hidden" onClick={() => setOpen(true)} aria-label="Open menu">☰</button>

          {/* Live Status Indicators */}
          <div className="flex items-center gap-3">
            <div className={`flex items-center gap-2 rounded-full px-3 py-1 text-xs font-semibold border ${
              apiOnline ? 'bg-emerald-50 text-emerald-800 border-emerald-200' : 'bg-rose-50 text-rose-800 border-rose-200'
            }`}>
              <span className={`h-2 w-2 rounded-full ${apiOnline ? 'bg-emerald-500 animate-pulse' : 'bg-rose-500'}`} />
              <span>{apiOnline ? 'API Online' : 'API Offline'}</span>
              {serverTime && <span className="text-slate-400 text-[10px]">· {serverTime}</span>}
            </div>

            <div className="hidden sm:block text-xs text-slate-500">
              Branch: <span className="font-semibold text-slate-700">Dhanmondi Royal</span> · Asia/Dhaka · BDT (৳)
            </div>
          </div>

          <div className="flex items-center gap-4">
            <div className="text-right">
              <p className="text-sm font-semibold text-royal-900">{user?.full_name}</p>
              <p className="text-xs text-slate-500">{user?.roles?.join(', ')}</p>
            </div>
            <button className="btn-ghost text-sm text-rose-700 hover:bg-rose-50 px-3 py-1.5 rounded-lg" onClick={logout}>
              Sign out
            </button>
          </div>
        </header>

        <main className="p-4 lg:p-8 flex-1 overflow-y-auto"><Outlet /></main>
      </div>
    </div>
  );
}
