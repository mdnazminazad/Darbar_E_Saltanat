import { describe, it, expect, beforeEach } from 'vitest';
import { api, resetDb, makeUser, loginAs } from './helpers.js';

describe('Authentication', () => {
  beforeEach(async () => { await resetDb(); await makeUser('admin', 'SUPER_ADMIN'); await makeUser('cashier1', 'CASHIER'); });

  it('logs in with valid credentials and returns tokens, roles and permissions', async () => {
    const res = await api().post('/api/v1/auth/login').send({ username: 'cashier1', password: 'Passw0rd!' });
    expect(res.status).toBe(200);
    expect(res.body.success).toBe(true);
    expect(res.body.data.access_token).toBeTruthy();
    expect(res.body.data.refresh_token).toBeTruthy();
    expect(res.body.data.user.roles).toEqual(['CASHIER']);
    expect(res.body.data.user.permissions).toContain('payments.take');
    expect(res.body.data.user).not.toHaveProperty('password_hash');
  });

  it('rejects a wrong password with a generic message', async () => {
    const res = await api().post('/api/v1/auth/login').send({ username: 'cashier1', password: 'nope' });
    expect(res.status).toBe(401);
    expect(res.body.error.code).toBe('INVALID_CREDENTIALS');
    const unknown = await api().post('/api/v1/auth/login').send({ username: 'ghost', password: 'nope' });
    expect(unknown.body.error.message).toBe(res.body.error.message);
  });

  it('locks the account after repeated failures', async () => {
    for (let i = 0; i < 3; i++) await api().post('/api/v1/auth/login').send({ username: 'cashier1', password: 'bad' });
    const res = await api().post('/api/v1/auth/login').send({ username: 'cashier1', password: 'Passw0rd!' });
    expect(res.status).toBe(423);
  });

  it('validates the request body', async () => {
    const res = await api().post('/api/v1/auth/login').send({ username: '' });
    expect(res.status).toBe(422);
    expect(res.body.error.code).toBe('VALIDATION_ERROR');
  });

  it('returns the current user from /auth/me and rejects missing/invalid tokens', async () => {
    const { access_token } = await loginAs('admin');
    const me = await api().get('/api/v1/auth/me').set('Authorization', `Bearer ${access_token}`);
    expect(me.status).toBe(200);
    expect(me.body.data.permissions).toEqual(['*']);
    expect((await api().get('/api/v1/auth/me')).status).toBe(401);
    expect((await api().get('/api/v1/auth/me').set('Authorization', 'Bearer garbage')).body.error.code).toBe('TOKEN_INVALID');
  });

  it('rotates refresh tokens and detects reuse', async () => {
    const { refresh_token } = await loginAs('cashier1');
    const r1 = await api().post('/api/v1/auth/refresh').send({ refresh_token });
    expect(r1.status).toBe(200);
    expect(r1.body.data.refresh_token).not.toBe(refresh_token);
    const reuse = await api().post('/api/v1/auth/refresh').send({ refresh_token });
    expect(reuse.status).toBe(401);
    expect(reuse.body.error.code).toBe('REFRESH_REUSED');
    // the whole chain is revoked after reuse
    const r2 = await api().post('/api/v1/auth/refresh').send({ refresh_token: r1.body.data.refresh_token });
    expect(r2.status).toBe(401);
  });

  it('changes password and enforces the password policy', async () => {
    const { access_token } = await loginAs('cashier1');
    const auth = { Authorization: `Bearer ${access_token}` };
    const weak = await api().post('/api/v1/auth/change-password').set(auth).send({ current_password: 'Passw0rd!', new_password: 'short' });
    expect(weak.status).toBe(422);
    const okRes = await api().post('/api/v1/auth/change-password').set(auth).send({ current_password: 'Passw0rd!', new_password: 'N3wPassword!' });
    expect(okRes.status).toBe(200);
    await loginAs('cashier1', 'N3wPassword!');
  });
});
