import axios from 'axios';

const REFRESH_KEY = 'des.refresh';
const store = {
  get: () => { try { return localStorage.getItem(REFRESH_KEY); } catch { return null; } },
  set: (v) => { try { v ? localStorage.setItem(REFRESH_KEY, v) : localStorage.removeItem(REFRESH_KEY); } catch { /* storage unavailable */ } },
};

let accessToken = null; // kept in memory only
let onSessionExpired = () => {};
export const setSession = (data) => {
  if (!data) return;
  const { access_token, refresh_token } = data;
  accessToken = access_token || null;
  if (refresh_token) store.set(refresh_token);
};
export const clearSession = () => { accessToken = null; store.set(null); };
export const hasRefreshToken = () => !!store.get();
export const onExpired = (fn) => { onSessionExpired = fn; };

export const api = axios.create({ baseURL: import.meta.env.VITE_API_BASE_URL || '/api/v1', timeout: 20000 });

api.interceptors.request.use((cfg) => {
  if (accessToken) cfg.headers.Authorization = `Bearer ${accessToken}`;
  return cfg;
});

// Single-flight refresh: concurrent 401s wait for one /auth/refresh call.
let refreshing = null;
export async function refreshSession() {
  const token = store.get();
  if (!token) throw new Error('no refresh token');
  refreshing ??= axios.post(`${api.defaults.baseURL}/auth/refresh`, { refresh_token: token })
    .then((r) => {
      if (r.data && typeof r.data === 'object' && r.data.data) {
        setSession(r.data.data);
        return r.data.data;
      }
      throw new Error('invalid refresh payload');
    })
    .finally(() => { refreshing = null; });
  return refreshing;
}

api.interceptors.response.use((r) => r, async (error) => {
  const original = error.config;
  const status = error.response?.status;
  const code = error.response?.data?.error?.code;
  if (status === 401 && !original._retry && !original.url.includes('/auth/') && store.get()) {
    original._retry = true;
    try { await refreshSession(); return api(original); }
    catch { clearSession(); onSessionExpired(); }
  } else if (status === 401 && code !== 'INVALID_CREDENTIALS' && !original.url.includes('/auth/login')) {
    onSessionExpired();
  }
  return Promise.reject(error);
});

/** Human-readable message from any API error. */
export function errorMessage(err) {
  const e = err?.response?.data?.error;
  if (e?.details?.length) return `${e.message}: ${e.details.map((d) => `${d.field ? d.field + ' — ' : ''}${d.message}`).join('; ')}`;
  if (e?.message) return e.message;
  if (err?.code === 'ECONNABORTED') return 'The server took too long to respond';
  if (!err?.response) return 'Cannot reach the server. Check your connection.';
  return 'Something went wrong';
}
