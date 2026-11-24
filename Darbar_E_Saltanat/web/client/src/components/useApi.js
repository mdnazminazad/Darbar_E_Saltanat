import { useCallback, useEffect, useState } from 'react';
import { api, errorMessage } from '../api/client.js';

/** GET helper with loading/error state; returns { data, meta, loading, error, reload }. */
export function useApi(url, params) {
  const key = JSON.stringify(params ?? {});
  const [state, setState] = useState({ data: null, meta: null, loading: true, error: null });
  const load = useCallback(async () => {
    setState((s) => ({ ...s, loading: true, error: null }));
    try {
      const r = await api.get(url, { params: JSON.parse(key) });
      const payload = r.data && typeof r.data === 'object' && 'data' in r.data ? r.data.data : r.data;
      setState({ data: payload ?? null, meta: r.data?.meta ?? null, loading: false, error: null });
    } catch (e) { setState({ data: null, meta: null, loading: false, error: errorMessage(e) }); }
  }, [url, key]);
  useEffect(() => { load(); }, [load]);
  return { ...state, reload: load };
}
