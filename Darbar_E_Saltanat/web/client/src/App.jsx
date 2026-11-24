import { Navigate, Route, Routes, useLocation } from 'react-router-dom';
import { useAuth } from './auth/AuthContext.jsx';
import { Loading } from './components/ui.jsx';
import { ErrorBoundary } from './components/ErrorBoundary.jsx';
import AdminLayout from './layouts/AdminLayout.jsx';
import Login from './pages/Login.jsx';
import ChangePassword from './pages/ChangePassword.jsx';
import Overview from './pages/admin/Overview.jsx';
import Users from './pages/admin/Users.jsx';
import Roles from './pages/admin/Roles.jsx';
import Settings from './pages/admin/Settings.jsx';
import AuditLogs from './pages/admin/AuditLogs.jsx';
import Sync from './pages/admin/Sync.jsx';
import Branches from './pages/admin/Branches.jsx';
import WebPos from './pages/fallback/WebPos.jsx';
import WebOrders from './pages/fallback/WebOrders.jsx';
import WebTables from './pages/fallback/WebTables.jsx';
import WebInventory from './pages/fallback/WebInventory.jsx';
import WebReports from './pages/fallback/WebReports.jsx';

function RequireAuth({ children }) {
  const { user, booting } = useAuth(); const loc = useLocation();
  if (booting) return <Loading label="Restoring session…" />;
  if (!user) return <Navigate to="/login" replace state={{ from: loc.pathname }} />;
  if (user.must_change_password && loc.pathname !== '/change-password') return <Navigate to="/change-password" replace />;
  return children;
}
function RequirePerm({ perm, children }) {
  const { can } = useAuth();
  return can(perm) ? children : <div className="card text-sm text-slate-600">You do not have access to this page.</div>;
}
const NotFound = () => <div className="flex min-h-screen items-center justify-center text-slate-500">Page not found</div>;

export default function App() {
  return (
    <ErrorBoundary>
      <Routes>
        <Route path="/" element={<Navigate to="/admin" replace />} />
        <Route path="/login" element={<Login />} />
        <Route path="/change-password" element={<RequireAuth><ChangePassword /></RequireAuth>} />
        <Route path="/admin" element={<RequireAuth><AdminLayout /></RequireAuth>}>
          <Route index element={<Overview />} />
          <Route path="users" element={<RequirePerm perm="users.manage"><Users /></RequirePerm>} />
          <Route path="roles" element={<RequirePerm perm="roles.manage"><Roles /></RequirePerm>} />
          <Route path="branches" element={<Branches />} />
          <Route path="pos" element={<RequirePerm perm="orders.create"><WebPos /></RequirePerm>} />
          <Route path="orders" element={<RequirePerm perm="orders.view"><WebOrders /></RequirePerm>} />
          <Route path="tables" element={<RequirePerm perm="orders.view"><WebTables /></RequirePerm>} />
          <Route path="inventory" element={<RequirePerm perm="inventory.view"><WebInventory /></RequirePerm>} />
          <Route path="reports" element={<RequirePerm perm="reports.view"><WebReports /></RequirePerm>} />
          <Route path="devices" element={<RequirePerm perm="sync.view"><Sync /></RequirePerm>} />
          <Route path="settings" element={<Settings />} />
          <Route path="audit-logs" element={<RequirePerm perm="audit.view"><AuditLogs /></RequirePerm>} />
        </Route>
        <Route path="*" element={<NotFound />} />
      </Routes>
    </ErrorBoundary>
  );
}
