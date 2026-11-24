import { useTheme } from '../theme/ThemeContext.jsx';

const Sun = () => (
  <svg viewBox="0 0 24 24" className="h-5 w-5" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" aria-hidden="true">
    <circle cx="12" cy="12" r="4" />
    <path d="M12 2.5v2.2M12 19.3v2.2M2.5 12h2.2M19.3 12h2.2M5.3 5.3l1.6 1.6M17.1 17.1l1.6 1.6M18.7 5.3l-1.6 1.6M6.9 17.1l-1.6 1.6" />
  </svg>
);
const Moon = () => (
  <svg viewBox="0 0 24 24" className="h-5 w-5" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
    <path d="M20.5 13.2A8.5 8.5 0 1 1 10.8 3.5a6.7 6.7 0 0 0 9.7 9.7Z" />
  </svg>
);

/**
 * Light / dark switch. `variant="onDark"` is for use over the dark login gradient.
 * The icon shows what you will switch TO (moon in light mode, sun in dark mode).
 */
export default function ThemeToggle({ variant = 'default', showLabel = false, className = '' }) {
  const { isDark, toggle } = useTheme();
  const label = isDark ? 'Switch to light mode' : 'Switch to dark mode';
  const look = variant === 'onDark'
    ? 'bg-white/10 text-gold-300 ring-1 ring-white/20 hover:bg-white/20'
    : 'bg-white text-slate-600 ring-1 ring-slate-300 hover:bg-slate-50 dark:bg-royal-800 dark:text-gold-300 dark:ring-royal-700 dark:hover:bg-royal-700';
  return (
    <button
      type="button"
      onClick={toggle}
      title={label}
      aria-label={label}
      aria-pressed={isDark}
      className={`inline-flex items-center justify-center gap-2 rounded-lg px-2.5 py-2 text-sm font-medium transition focus:outline-none focus:ring-2 focus:ring-gold-400 ${look} ${className}`}
    >
      {isDark ? <Sun /> : <Moon />}
      {showLabel && <span className="hidden sm:inline">{isDark ? 'Light' : 'Dark'}</span>}
    </button>
  );
}
