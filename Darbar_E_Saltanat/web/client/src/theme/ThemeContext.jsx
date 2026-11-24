import { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react';

/**
 * Light / dark theme. The choice is remembered per browser (localStorage) and applied as the `dark`
 * class on <html> (Tailwind `darkMode: 'class'`). index.html applies the saved choice before the first
 * paint, so there is no white flash when a dark-mode user reloads.
 */
const KEY = 'darbar.theme';
const ThemeContext = createContext({ theme: 'light', isDark: false, toggle: () => {}, setTheme: () => {} });

function readSaved() {
  try {
    const t = localStorage.getItem(KEY);
    if (t === 'dark' || t === 'light') return t;
  } catch { /* storage unavailable (private mode) — fall back to light */ }
  return 'light';
}

function paint(theme) {
  const root = document.documentElement;
  root.classList.toggle('dark', theme === 'dark');
  root.style.colorScheme = theme;
  document.querySelector('meta[name="theme-color"]')?.setAttribute('content', theme === 'dark' ? '#0b1020' : '#111831');
}

export function ThemeProvider({ children }) {
  const [theme, setThemeState] = useState(readSaved);

  useEffect(() => { paint(theme); }, [theme]);

  // Keep several open tabs in step.
  useEffect(() => {
    const onStorage = (e) => { if (e.key === KEY && (e.newValue === 'dark' || e.newValue === 'light')) setThemeState(e.newValue); };
    window.addEventListener('storage', onStorage);
    return () => window.removeEventListener('storage', onStorage);
  }, []);

  const setTheme = useCallback((t) => {
    setThemeState(t);
    try { localStorage.setItem(KEY, t); } catch { /* ignore */ }
  }, []);
  const toggle = useCallback(() => setTheme(theme === 'dark' ? 'light' : 'dark'), [theme, setTheme]);

  const value = useMemo(() => ({ theme, isDark: theme === 'dark', toggle, setTheme }), [theme, toggle, setTheme]);
  return <ThemeContext.Provider value={value}>{children}</ThemeContext.Provider>;
}

export const useTheme = () => useContext(ThemeContext);
