import { describe, it, expect, beforeEach } from 'vitest';
import crypto from 'node:crypto';
import { api, resetDb, makeUser, loginAs } from './helpers.js';
import { modelFor } from '../src/db/models/registryModels.js';
import { User } from '../src/db/models/index.js';

const id = () => crypto.randomUUID();
let admin, dev, branchUuid, deviceUuid, adminUuid;
const ts = (d = new Date()) => d.toISOString();

/** A change exactly as the desktop builds it (snake_case, *_uuid, decimals as strings, ISO timestamps). */
const change = (entity, data, { operation = 'CREATE', version = 0 } = {}) => ({
  entity, operation, uuid: data.uuid, sync_version: version, updated_at: ts(),
  data: { created_at: ts(), updated_at: ts(), deleted_at: null, device_id: deviceUuid, sync_version: version, ...data },
});
const push = (changes, batch_id = id(), headers = dev) => api().post('/api/v1/sync/push').set(headers).send({ batch_id, device_id: deviceUuid, changes });
const pullAll = async (since, limit = 500) => {
  const rows = {}; let cursor = null; let next = null; let pages = 0;
  do {
    const q = new URLSearchParams({ limit: String(limit), ...(since ? { since } : {}), ...(cursor ? { cursor } : {}) });
    const res = await api().get(`/api/v1/sync/pull?${q}`).set(dev);
    expect(res.status).toBe(200);
    for (const c of res.body.data.changes) (rows[c.entity] ??= []).push(...c.rows);
    cursor = res.body.data.cursor; next = res.body.data.next_since; pages++;
  } while (cursor && pages < 100);
  return { rows, next, pages };
};

async function catalog() {
  const cat = { uuid: id(), branch_uuid: branchUuid, parent_uuid: null, name: 'Biryani', kitchen_station: 'Main Kitchen', sort_order: 1, is_active: true };
  const item = { uuid: id(), branch_uuid: branchUuid, category_uuid: cat.uuid, name: 'Kacchi Biryani', selling_price: '450.00', cost_price: '260.00', tax_rate: '0.00', prep_time_minutes: 15, sort_order: 1, is_available: true, is_active: true };
  return { cat, item };
}

describe('Sync API', () => {
  beforeEach(async () => {
    const { branch } = await resetDb();
    branchUuid = branch.uuid;
    const u = await makeUser('admin', 'SUPER_ADMIN');
    adminUuid = u.uuid;
    admin = { Authorization: `Bearer ${(await loginAs('admin')).access_token}` };
    const reg = await api().post('/api/v1/devices/register').set(admin).send({ name: 'Counter 1', branch_uuid: branchUuid });
    deviceUuid = reg.body.data.device_id;
    dev = { 'X-Device-Id': deviceUuid, 'X-Device-Token': reg.body.data.device_token };
  });

  it('requires device credentials and tells the device its branch', async () => {
    expect((await api().post('/api/v1/sync/push').send({ batch_id: id(), changes: [] })).status).toBe(401);
    const me = await api().get('/api/v1/devices/me').set(dev);
    expect(me.body.data).toMatchObject({ device_id: deviceUuid, branch_uuid: branchUuid });
  });

  it('creates an offline order with all its parts and replays a duplicate batch without duplicating anything', async () => {
    const { cat, item } = await catalog();
    const customer = { uuid: id(), branch_uuid: null, name: 'Rahim', phone: '01712345678', loyalty_points: 0, credit_balance: '0.00', credit_limit: '0.00', is_active: true, password_hash: 'should-be-ignored' };
    const order = { uuid: id(), branch_uuid: branchUuid, order_no: 'ORD-1111-261120-0001', order_type: 'TAKEAWAY', source: 'POS', status: 'COMPLETED', payment_status: 'PAID',
      customer_uuid: customer.uuid, waiter_uuid: adminUuid, guest_count: 1, subtotal: '450.00', discount_type: 'NONE', discount_value: '0.00', discount_amount: '0.00',
      service_charge_rate: '0.00', service_charge_amount: '0.00', vat_amount: '22.50', delivery_charge: '0.00', round_off: '0.50', total: '473.00',
      paid_amount: '473.00', due_amount: '0.00', order_date: ts() };
    const line = { uuid: id(), branch_uuid: branchUuid, order_uuid: order.uuid, menu_item_uuid: item.uuid, item_name: 'Kacchi Biryani', quantity: 1, unit_price: '450.00',
      addons_total: '0.00', discount_amount: '0.00', tax_rate: '5.00', tax_amount: '22.50', line_total: '450.00', status: 'SENT' };
    const payment = { uuid: id(), branch_uuid: branchUuid, order_uuid: order.uuid, method: 'CASH', amount: '473.00', tendered: '500.00', change_amount: '27.00', status: 'COMPLETED', paid_at: ts() };
    const batchId = id();
    const changes = [change('categories', cat), change('menu_items', item), change('customers', customer), change('orders', order), change('order_items', line), change('payments', payment)];

    const first = await push(changes, batchId);
    expect(first.status).toBe(200);
    expect(first.body.data.results.map((r) => r.status)).toEqual(Array(6).fill('SYNCED'));
    expect(first.body.data.results.every((r) => r.server_version === 1)).toBe(true);

    const again = await push(changes, batchId);
    expect(again.body.data.replayed).toBe(true);
    expect(again.body.data.results).toEqual(first.body.data.results);
    expect(await modelFor('orders').count()).toBe(1);
    expect(await modelFor('payments').count()).toBe(1);

    const saved = await modelFor('customers').findOne({ where: { uuid: customer.uuid } });
    expect(saved.password_hash).toBeNull();
    expect((await modelFor('orders').findOne({ where: { uuid: order.uuid } })).device_id).toBe(deviceUuid);
    const viaApi = await api().get(`/api/v1/menu-items/${item.uuid}`).set(admin);
    expect(viaApi.body.data).toMatchObject({ name: 'Kacchi Biryani', selling_price: '450.00', category_uuid: cat.uuid });
  });

  it('re-sent CREATE from the same device (lost response, new batch) is accepted, not duplicated', async () => {
    const { cat } = await catalog();
    await push([change('categories', cat)]);
    const res = await push([change('categories', cat)]);
    expect(res.body.data.results[0]).toMatchObject({ status: 'SYNCED', server_version: 1 });
    const edited = await push([change('categories', { ...cat, name: 'Biryani & Polao' })]);
    expect(edited.body.data.results[0]).toMatchObject({ status: 'SYNCED', server_version: 2 });
  });

  it('detects conflicts with version checks and resolves them from the portal', async () => {
    const { cat } = await catalog();
    await push([change('categories', cat)]);
    // the web portal edits the same category → server v2
    await api().patch(`/api/v1/categories/${cat.uuid}`).set(admin).send({ name: 'Biryani (web)' });
    // the device edits its v1 copy offline
    const res = await push([change('categories', { ...cat, name: 'Biryani (till)' }, { operation: 'UPDATE', version: 1 })]);
    expect(res.body.data.results[0]).toMatchObject({ status: 'CONFLICT', server_version: 2 });
    const row = await modelFor('categories').findOne({ where: { uuid: cat.uuid } });
    expect(row.name).toBe('Biryani (web)');
    expect(row.sync_status).toBe('CONFLICT');

    const list = await api().get('/api/v1/sync/conflicts').set(admin);
    expect(list.body.data).toHaveLength(1);
    expect(list.body.data[0]).toMatchObject({ entity: 'categories', device: 'Counter 1', local_version: 1, server_version: 2 });
    expect(list.body.data[0].local_data.name).toBe('Biryani (till)');

    const { next } = await pullAll();
    const resolved = await api().post(`/api/v1/sync/conflicts/${list.body.data[0].uuid}/resolve`).set(admin).send({ resolution: 'KEEP_DEVICE', note: 'Till name is correct' });
    expect(resolved.status).toBe(200);
    expect(resolved.body.data.record).toMatchObject({ name: 'Biryani (till)', sync_version: 3 });
    expect((await api().get('/api/v1/sync/conflicts').set(admin)).body.data).toHaveLength(0);

    // the device receives the winning version on its next pull
    await new Promise((r) => setTimeout(r, 10));
    const after = await pullAll(next);
    expect(after.rows.categories.find((c) => c.uuid === cat.uuid)).toMatchObject({ name: 'Biryani (till)', sync_version: 3 });
    // now in sync again: an edit based on v3 is accepted
    const ok = await push([change('categories', { ...cat, name: 'Biryani' }, { operation: 'UPDATE', version: 3 })]);
    expect(ok.body.data.results[0]).toMatchObject({ status: 'SYNCED', server_version: 4 });
  });

  it('a replayed identical update is not a conflict', async () => {
    const { cat } = await catalog();
    await push([change('categories', cat)]);
    const upd = change('categories', { ...cat, sort_order: 5 }, { operation: 'UPDATE', version: 1 });
    await push([upd]);
    const replay = await push([upd]); // new batch id, same content, stale base
    expect(replay.body.data.results[0]).toMatchObject({ status: 'SYNCED', server_version: 2 });
  });

  it('fails a child whose parent is not synced yet, and accepts it once the parent arrives', async () => {
    const { cat, item } = await catalog();
    const res = await push([change('menu_items', item)]);
    expect(res.body.data.results[0]).toMatchObject({ status: 'FAILED', retryable: true });
    expect(res.body.data.results[0].reason).toMatch(/category_uuid/);
    const both = await push([change('categories', cat), change('menu_items', item)]);
    expect(both.body.data.results.map((r) => r.status)).toEqual(['SYNCED', 'SYNCED']);
  });

  it('one bad change does not spoil the rest of the batch', async () => {
    const { cat } = await catalog();
    const bad = change('categories', { ...cat, uuid: id(), name: null });
    const res = await push([bad, change('categories', cat)]);
    expect(res.body.data.results.map((r) => r.status)).toEqual(['FAILED', 'SYNCED']);
    expect(await modelFor('categories').count()).toBe(1);
  });

  it('rejects identity tables, other branches and invalid enum values', async () => {
    const u = { uuid: id(), username: 'hacker', password_hash: 'x', full_name: 'X', is_active: true };
    const other = { ...(await catalog()).cat, branch_uuid: '00000000-0000-4000-8000-000000000000' };
    const enumBad = { ...(await catalog()).cat };
    const tbl = { uuid: id(), branch_uuid: branchUuid, name: 'T9', capacity: 4, status: 'PARTY', is_active: true };
    const res = await push([change('users', u), change('categories', other), change('restaurant_tables', tbl), change('categories', enumBad)]);
    expect(res.body.data.results.map((r) => r.status)).toEqual(['FAILED', 'FAILED', 'FAILED', 'SYNCED']);
    expect(res.body.data.results[2].reason).toMatch(/status must be one of/);
    expect(await User.count({ where: { username: 'hacker' } })).toBe(0);
  });

  it('keeps append-only ledgers immutable', async () => {
    const { cat, item } = await catalog();
    const order = { uuid: id(), branch_uuid: branchUuid, order_no: 'ORD-1', order_type: 'TAKEAWAY', status: 'OPEN', payment_status: 'PARTIAL', order_date: ts(), total: '100.00' };
    const pay = { uuid: id(), branch_uuid: branchUuid, order_uuid: order.uuid, method: 'CASH', amount: '50.00', change_amount: '0.00', status: 'COMPLETED', paid_at: ts() };
    await push([change('categories', cat), change('menu_items', item), change('orders', order), change('payments', pay)]);
    const tampered = await push([change('payments', { ...pay, amount: '5.00' }, { operation: 'UPDATE', version: 1 })]);
    expect(tampered.body.data.results[0].status).toBe('SYNCED');
    expect(Number((await modelFor('payments').findOne({ where: { uuid: pay.uuid } })).amount)).toBe(50);
  });

  it('replicates soft deletes and recomputes coupon usage on the server', async () => {
    const { cat } = await catalog();
    await push([change('categories', cat)]);
    const del = await push([change('categories', { ...cat, deleted_at: ts() }, { operation: 'DELETE', version: 1 })]);
    expect(del.body.data.results[0]).toMatchObject({ status: 'SYNCED', server_version: 2 });
    expect((await api().get(`/api/v1/categories/${cat.uuid}`).set(admin)).status).toBe(404);
    const pulled = await pullAll();
    expect(pulled.rows.categories.find((c) => c.uuid === cat.uuid).deleted_at).toBeTruthy();

    const coupon = (await api().post('/api/v1/coupons').set(admin).send({ code: 'FLAT100', type: 'FIXED', value: 100, min_order: 0 })).body.data;
    const order = { uuid: id(), branch_uuid: branchUuid, order_no: 'ORD-2', order_type: 'TAKEAWAY', status: 'COMPLETED', payment_status: 'PAID', order_date: ts(), total: '400.00', coupon_uuid: coupon.uuid };
    const usage = { uuid: id(), branch_uuid: branchUuid, coupon_uuid: coupon.uuid, order_uuid: order.uuid, discount_amount: '100.00', used_at: ts() };
    await push([change('orders', order), change('coupon_usages', usage)]);
    expect((await api().get(`/api/v1/coupons/${coupon.uuid}`).set(admin)).body.data.used_count).toBe(1);
  });

  it('pulls parents first, hides secrets and audit logs, and pages without losing rows that share a timestamp', async () => {
    const Cat = modelFor('categories');
    const now = new Date();
    for (let i = 0; i < 7; i++) await Cat.create({ name: `Cat ${i}`, branch_id: 1, created_at: now, updated_at: now }, { silent: true });
    await modelFor('customers').create({ name: 'Web customer', phone: '01811111111', password_hash: 'secret-hash' });
    const { rows, pages, next } = await pullAll(undefined, 3);
    expect(pages).toBeGreaterThan(2);
    expect(rows.categories.map((c) => c.name).sort()).toEqual(['Cat 0', 'Cat 1', 'Cat 2', 'Cat 3', 'Cat 4', 'Cat 5', 'Cat 6']);
    expect(new Set(rows.categories.map((c) => c.uuid)).size).toBe(7);
    expect(rows.customers[0]).not.toHaveProperty('password_hash');
    expect(rows.audit_logs).toBeUndefined();
    expect(rows.users.find((u) => u.username === 'admin')).toHaveProperty('password_hash'); // offline login on the till
    const order = Object.keys(rows);
    expect(order.indexOf('branches')).toBeLessThan(order.indexOf('categories'));
    expect(rows.categories[0]).toMatchObject({ is_active: true, branch_uuid: branchUuid });
    expect(next).toBeTruthy();

    // incremental: only what changed since
    await new Promise((r) => setTimeout(r, 10));
    const since = new Date().toISOString();
    await new Promise((r) => setTimeout(r, 10));
    await api().post('/api/v1/categories').set(admin).send({ name: 'Desserts' });
    const inc = await pullAll(since);
    expect(Object.keys(inc.rows)).toEqual(['categories']);
    expect(inc.rows.categories.map((c) => c.name)).toEqual(['Desserts']);
  });

  it('reports status to devices and admins and lists failed batches', async () => {
    const { item } = await catalog();
    await push([change('menu_items', item)]);
    const devStatus = await api().get('/api/v1/sync/status').set(dev);
    expect(devStatus.body.data).toMatchObject({ device_id: deviceUuid, open_conflicts: 0 });
    expect(devStatus.body.data.last_push_at).toBeTruthy();
    const adminStatus = await api().get('/api/v1/sync/status').set(admin);
    expect(adminStatus.body.data).toMatchObject({ failed_batches_24h: 1 });
    expect(adminStatus.body.data.devices[0].name).toBe('Counter 1');
    const batches = await api().get('/api/v1/sync/batches').set(admin);
    expect(batches.body.data[0].problems[0]).toMatchObject({ entity: 'menu_items', status: 'FAILED' });
    const ack = await api().post('/api/v1/sync/acknowledge').set(dev).send({ pulled_until: ts(), pending_count: 3 });
    expect(ack.status).toBe(200);
  });

  it('stops a revoked device', async () => {
    const list = await api().get('/api/v1/devices').set(admin);
    await api().post(`/api/v1/devices/${list.body.data[0].device_id}/revoke`).set(admin);
    const res = await api().get('/api/v1/sync/pull').set(dev);
    expect(res.status).toBe(403);
    expect(res.body.error.code).toBe('DEVICE_REVOKED');
  });
});
