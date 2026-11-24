import { describe, it, expect, beforeEach } from 'vitest';
import { api, resetDb, makeUser, loginAs } from './helpers.js';

let admin, cashier;
describe('Users, roles, settings and RBAC', () => {
  beforeEach(async () => {
    await resetDb();
    await makeUser('admin', 'SUPER_ADMIN'); await makeUser('cashier1', 'CASHIER');
    admin = { Authorization: `Bearer ${(await loginAs('admin')).access_token}` };
    cashier = { Authorization: `Bearer ${(await loginAs('cashier1')).access_token}` };
  });

  it('forbids a cashier from managing users', async () => {
    const res = await api().get('/api/v1/users').set(cashier);
    expect(res.status).toBe(403);
    expect(res.body.error.message).toMatch(/users.manage/);
  });

  it('lets an admin create, list, update and soft-delete a user', async () => {
    const created = await api().post('/api/v1/users').set(admin).send({
      username: 'waiter1', full_name: 'Rahim Uddin', password: 'Waiter123!', pin: '1234', role_codes: ['WAITER'],
    });
    expect(created.status).toBe(201);
    expect(created.body.data.has_pin).toBe(true);
    expect(created.body.data.must_change_password).toBe(true);
    const uuid = created.body.data.uuid;

    const list = await api().get('/api/v1/users?search=Rahim').set(admin);
    expect(list.body.meta.total).toBe(1);

    const upd = await api().patch(`/api/v1/users/${uuid}`).set(admin).send({ role_codes: ['WAITER', 'CASHIER'] });
    expect(upd.body.data.roles.sort()).toEqual(['CASHIER', 'WAITER']);
    const back = await api().patch(`/api/v1/users/${uuid}`).set(admin).send({ role_codes: ['WAITER'] });
    expect(back.body.data.roles).toEqual(['WAITER']);
    const again = await api().patch(`/api/v1/users/${uuid}`).set(admin).send({ role_codes: ['WAITER', 'CASHIER'] });
    expect(again.status).toBe(200); // re-adding a soft-deleted link restores it (no unique violation)

    expect((await api().delete(`/api/v1/users/${uuid}`).set(admin)).status).toBe(200);
    expect((await api().get(`/api/v1/users/${uuid}`).set(admin)).status).toBe(404);
  });

  it('rejects duplicate usernames with 409', async () => {
    const body = { username: 'dup', full_name: 'Dup', password: 'Passw0rd1!', role_codes: ['WAITER'] };
    await api().post('/api/v1/users').set(admin).send(body);
    expect((await api().post('/api/v1/users').set(admin).send(body)).status).toBe(409);
  });

  it('protects the last Super Admin', async () => {
    const me = await api().get('/api/v1/users?role=SUPER_ADMIN').set(admin);
    const uuid = me.body.data[0].uuid;
    const other = await makeUser('boss2', 'MANAGER');
    const res = await api().patch(`/api/v1/users/${uuid}`).set(admin).send({ role_codes: ['MANAGER'] });
    expect(res.status).toBe(409);
    expect(other.username).toBe('boss2');
  });

  it('creates a custom role and writes an audit log entry', async () => {
    const res = await api().post('/api/v1/roles').set(admin).send({ code: 'HOST', name: 'Host', permissions: ['reservations.manage', 'tables.manage'] });
    expect(res.status).toBe(201);
    expect(res.body.data.permissions).toEqual(['reservations.manage', 'tables.manage']);
    const logs = await api().get('/api/v1/audit-logs?action=roles.').set(admin);
    expect(logs.body.data[0].action).toBe('roles.create');
  });

  it('reads public settings anonymously and upserts settings with permission', async () => {
    const pub = await api().get('/api/v1/settings/public');
    expect(pub.body.data.restaurant_name).toBe('Darbar_E_Saltanat');
    expect((await api().put('/api/v1/settings').set(cashier).send({ settings: [{ key: 'vat_rate', value: 7.5 }] })).status).toBe(403);
    const put = await api().put('/api/v1/settings').set(admin).send({ settings: [{ key: 'vat_rate', value: 7.5 }, { key: 'receipt_footer', value: 'Dhonnobad' }] });
    expect(put.status).toBe(200);
    expect(put.body.data.vat_rate.value).toBe(7.5);
    expect(put.body.data.receipt_footer.value).toBe('Dhonnobad');
  });

  it('registers a device and accepts its heartbeat only with the right token', async () => {
    const branches = await api().get('/api/v1/branches').set(admin);
    const reg = await api().post('/api/v1/devices/register').set(admin).send({ name: 'Counter PC 1', branch_uuid: branches.body.data[0].uuid });
    expect(reg.status).toBe(201);
    const { device_id, device_token } = reg.body.data;
    const bad = await api().post('/api/v1/devices/heartbeat').set({ 'X-Device-Id': device_id, 'X-Device-Token': 'wrong' }).send({});
    expect(bad.status).toBe(401);
    const good = await api().post('/api/v1/devices/heartbeat').set({ 'X-Device-Id': device_id, 'X-Device-Token': device_token }).send({ pending_count: 4 });
    expect(good.status).toBe(200);
    const list = await api().get('/api/v1/devices').set(admin);
    expect(list.body.data[0]).toMatchObject({ pending_count: 4, online: true });
  });

  it('returns 404 JSON for unknown routes', async () => {
    const res = await api().get('/api/v1/nope');
    expect(res.status).toBe(404);
    expect(res.body.success).toBe(false);
  });
});
