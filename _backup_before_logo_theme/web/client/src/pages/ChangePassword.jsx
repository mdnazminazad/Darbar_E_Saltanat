import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { useNavigate } from 'react-router-dom';
import { api, errorMessage } from '../api/client.js';
import { useAuth } from '../auth/AuthContext.jsx';
import { useToast } from '../components/Toast.jsx';
import { FieldError, Spinner } from '../components/ui.jsx';

const schema = z.object({
  current_password: z.string().min(1, 'Required'),
  new_password: z.string().min(8, 'At least 8 characters').refine((p) => /[A-Za-z]/.test(p) && /\d/.test(p), 'Use letters and numbers'),
  confirm: z.string(),
}).refine((v) => v.new_password === v.confirm, { path: ['confirm'], message: 'Passwords do not match' });

export default function ChangePassword() {
  const { user, setUser, logout } = useAuth();
  const toast = useToast(); const nav = useNavigate();
  const { register, handleSubmit, setError, formState: { errors, isSubmitting } } = useForm({ resolver: zodResolver(schema) });
  const onSubmit = async (v) => {
    try {
      await api.post('/auth/change-password', { current_password: v.current_password, new_password: v.new_password });
      setUser({ ...user, must_change_password: false });
      toast('Password changed'); nav('/admin', { replace: true });
    } catch (e) { setError('root', { message: errorMessage(e) }); }
  };
  return (
    <div className="flex min-h-screen items-center justify-center bg-royal-950 p-4">
      <form onSubmit={handleSubmit(onSubmit)} className="w-full max-w-sm rounded-2xl bg-white p-8" noValidate>
        <div className="mb-6 text-center">
          <img src="/logo.webp" alt="Dawat-e-Darbar Logo" className="mx-auto h-20 w-20 rounded-xl shadow-xl ring-2 ring-gold-500/40 object-contain" />
          <h1 className="mt-3 font-display text-xl font-bold text-royal-900">Set a new password</h1>
          <p className="mb-2 text-sm text-slate-500">{user?.must_change_password ? 'You must change the temporary password before continuing.' : 'Change your password.'}</p>
        </div>
        {errors.root && <div className="mb-3 rounded bg-maroon-500/10 px-3 py-2 text-sm text-maroon-700">{errors.root.message}</div>}
        {[['current_password', 'Current password', 'current-password'], ['new_password', 'New password', 'new-password'], ['confirm', 'Confirm new password', 'new-password']].map(([k, l, ac]) => (
          <div key={k} className="mb-3"><label className="label" htmlFor={k}>{l}</label><input id={k} type="password" autoComplete={ac} className="input" {...register(k)} /><FieldError error={errors[k]} /></div>
        ))}
        <button className="btn-primary mt-2 w-full" disabled={isSubmitting}>{isSubmitting && <Spinner className="h-4 w-4" />}Save password</button>
        <button type="button" className="mt-3 w-full text-sm text-slate-500 underline" onClick={logout}>Sign out</button>
      </form>
    </div>
  );
}
