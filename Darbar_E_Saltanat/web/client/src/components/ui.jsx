import { useEffect } from 'react';

export const Spinner = ({ className = 'h-5 w-5' }) => (
  <svg className={`animate-spin text-gold-500 ${className}`} viewBox="0 0 24 24" fill="none" aria-label="Loading">
    <circle cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" className="opacity-25" />
    <path d="M4 12a8 8 0 018-8" stroke="currentColor" strokeWidth="4" className="opacity-75" />
  </svg>
);
export const Loading = ({ label = 'Loading…' }) => (
  <div className="flex items-center justify-center gap-3 py-16 text-slate-500"><Spinner /> {label}</div>
);
export const Empty = ({ title, hint }) => (
  <div className="py-16 text-center"><p className="font-medium text-slate-700">{title}</p>{hint && <p className="mt-1 text-sm text-slate-500">{hint}</p>}</div>
);
export const ErrorBox = ({ message, onRetry }) => (
  <div className="rounded-lg border border-maroon-500/30 bg-maroon-500/5 p-4 text-sm text-maroon-700">
    {message} {onRetry && <button className="ml-2 underline" onClick={onRetry}>Retry</button>}
  </div>
);
export const PageHeader = ({ title, subtitle, actions }) => (
  <div className="mb-6 flex flex-wrap items-end justify-between gap-3">
    <div><h1 className="font-display text-2xl font-bold text-royal-900">{title}</h1>{subtitle && <p className="text-sm text-slate-500">{subtitle}</p>}</div>
    <div className="flex gap-2">{actions}</div>
  </div>
);
export const FieldError = ({ error }) => (error ? <p className="mt-1 text-xs text-maroon-600">{error.message}</p> : null);

export function Modal({ open, title, onClose, children, footer }) {
  useEffect(() => {
    if (!open) return undefined;
    const h = (e) => e.key === 'Escape' && onClose();
    window.addEventListener('keydown', h); return () => window.removeEventListener('keydown', h);
  }, [open, onClose]);
  if (!open) return null;
  return (
    <div className="fixed inset-0 z-40 flex items-center justify-center bg-royal-950/60 p-4" onClick={onClose}>
      <div role="dialog" aria-modal="true" className="max-h-[90vh] w-full max-w-lg overflow-y-auto rounded-xl bg-white shadow-xl" onClick={(e) => e.stopPropagation()}>
        <div className="border-b border-slate-200 px-5 py-4"><h2 className="font-display text-lg font-semibold text-royal-900">{title}</h2></div>
        <div className="px-5 py-4">{children}</div>
        {footer && <div className="flex justify-end gap-2 border-t border-slate-200 px-5 py-3">{footer}</div>}
      </div>
    </div>
  );
}
export function Confirm({ open, title, message, confirmLabel = 'Confirm', danger, busy, onConfirm, onClose }) {
  return (
    <Modal open={open} title={title} onClose={onClose} footer={<>
      <button className="btn-ghost" onClick={onClose}>Cancel</button>
      <button className={danger ? 'btn-danger' : 'btn-primary'} disabled={busy} onClick={onConfirm}>{busy && <Spinner className="h-4 w-4" />}{confirmLabel}</button>
    </>}><p className="text-sm text-slate-600">{message}</p></Modal>
  );
}
export function Pagination({ meta, onPage }) {
  if (!meta || meta.total_pages <= 1) return null;
  return (
    <div className="flex items-center justify-between border-t border-slate-200 px-4 py-3 text-sm text-slate-600">
      <span>Page {meta.page} of {meta.total_pages} · {meta.total} records</span>
      <div className="flex gap-2">
        <button className="btn-ghost" disabled={meta.page <= 1} onClick={() => onPage(meta.page - 1)}>Previous</button>
        <button className="btn-ghost" disabled={meta.page >= meta.total_pages} onClick={() => onPage(meta.page + 1)}>Next</button>
      </div>
    </div>
  );
}
export const formatDateTime = (iso) => (iso ? new Intl.DateTimeFormat('en-GB', { dateStyle: 'medium', timeStyle: 'short', timeZone: 'Asia/Dhaka' }).format(new Date(iso)) : '—');

export const Alert = ({ type = 'info', message }) => {
  if (!message) return null;
  const isErr = type === 'error' || type === 'danger';
  const isOk = type === 'success';
  return (
    <div className={`rounded-lg border p-4 text-sm font-medium ${
      isErr ? 'border-rose-300 bg-rose-50 text-rose-800' :
      isOk ? 'border-emerald-300 bg-emerald-50 text-emerald-800' :
      'border-amber-300 bg-amber-50 text-amber-800'
    }`}>
      {message}
    </div>
  );
};
