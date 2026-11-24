import { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react';
import { api, setSession, clearSession, hasRefreshToken, refreshSession, onExpired } from '../api/client.js';

const AuthCtx = createContext(null);

export function AuthProvider({ children }) {
  const [user, setUser] = useState(null);
  const [booting, setBooting] = useState(true);

  const logout = useCallback(async () => {
    try { await api.post('/auth/logout', { refresh_token: localStorage.getItem('des.refresh') || undefined }); } catch { /* best effort */ }
    clearSession(); setUser(null);
  }, []);

  useEffect(() => {
    onExpired(() => { clearSession(); setUser(null); });
    (async () => {
      if (hasRefreshToken()) {
        try {
          const s = await refreshSession();
          if (s?.user) setUser(s.user);
          else clearSession();
        } catch {
          clearSession();
        }
      }
      setBooting(false);
    })();
  }, []);

  const login = useCallback(async (username, password) => {
    const { data } = await api.post('/auth/login', { username, password });
    setSession(data.data); setUser(data.data.user);
    return data.data.user;
  }, []);

  const can = useCallback((perm) => !!user && (user.permissions?.includes('*') || user.permissions?.includes(perm)), [user]);
  const value = useMemo(() => ({ user, setUser, booting, login, logout, can }), [user, booting, login, logout, can]);
  return <AuthCtx.Provider value={value}>{children}</AuthCtx.Provider>;
}
export const useAuth = () => useContext(AuthCtx);
