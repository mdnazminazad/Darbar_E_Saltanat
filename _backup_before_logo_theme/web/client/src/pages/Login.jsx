import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { Navigate, useLocation, useNavigate } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext.jsx';
import { errorMessage } from '../api/client.js';
import { FieldError, Spinner } from '../components/ui.jsx';
import { Crown } from '../layouts/AdminLayout.jsx';

const schema = z.object({ username: z.string().trim().min(1, 'Enter your username or email'), password: z.string().min(1, 'Enter your password') });

export default function Login() {
  const { user, login } = useAuth();
  const nav = useNavigate(); const loc = useLocation();
  const [error, setError] = useState(null);
  const { register, handleSubmit, formState: { errors, isSubmitting } } = useForm({ resolver: zodResolver(schema) });
  if (user) return <Navigate to={user.must_change_password ? '/change-password' : '/admin'} replace />;
  const onSubmit = async (v) => {
    setError(null);
    try {
      const u = await login(v.username, v.password);
      nav(u.must_change_password ? '/change-password' : (loc.state?.from || '/admin'), { replace: true });
    } catch (e) { setError(errorMessage(e)); }
  };
  return (
    <div className="flex min-h-screen items-center justify-center bg-gradient-to-br from-royal-950 via-royal-900 to-maroon-700 p-4">
      <form onSubmit={handleSubmit(onSubmit)} className="w-full max-w-sm rounded-2xl bg-white p-8 shadow-2xl" noValidate>
        <div className="mb-6 text-center">
          <Crown className="mx-auto h-24 w-24 rounded-xl shadow-xl ring-2 ring-gold-500/40" />
          <h1 className="mt-3 font-display text-2xl font-bold text-royal-900">Darbar_E_Saltanat</h1>
          <p className="text-sm text-slate-500">Sign in to the management portal</p>
        </div>
        {error && <div className="mb-4 rounded-lg bg-maroon-500/10 px-3 py-2 text-sm text-maroon-700" role="alert">{error}</div>}
        <label className="label" htmlFor="username">Username or email</label>
        <input id="username" className="input" autoComplete="username" autoFocus {...register('username')} />
        <FieldError error={errors.username} />
        <label className="label mt-4" htmlFor="password">Password</label>
        <input id="password" type="password" className="input" autoComplete="current-password" {...register('password')} />
        <FieldError error={errors.password} />
        <button className="btn-primary mt-6 w-full py-3" disabled={isSubmitting}>{isSubmitting && <Spinner className="h-4 w-4" />}Sign in</button>
      </form>
    </div>
  );
}
