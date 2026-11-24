import { describe, it, expect, beforeEach } from 'vitest';
import { api, resetDb, makeUser, loginAs } from './helpers.js';
import { evaluateCoupon } from '../src/modules/catalog/couponRules.js';
import { sniffImage } from '../src/modules/uploads.routes.js';

let admin, waiter;
const post = (url, body, who = admin) => api().post(`/api/v1${url}`).set(who).send(body);
const get = (url, who = admin) => api().get(`/api/v1${url}`).set(who);

describe('Menu, tables, coupons and customers API', () => {
  beforeEach(async () => {
    await resetDb();
    await makeUser('admin', 'SUPER_ADMIN'); await makeUser('waiter1', 'WAITER');
    admin = { Authorization: `Bearer ${(await loginAs('admin')).access_token}` };
    waiter = { Authorization: `Bearer ${(await loginAs('waiter1')).access_token}` };
  });

  it('creates a category and a menu item, resolving uuids and formatting money', async () => {
    const cat = await post('/categories', { name: 'Biryani & Polao', kitchen_station: 'Main Kitchen' });
    expect(cat.status).toBe(201);
    expect(cat.body.data.branch_uuid).toBeTruthy();
    expect(cat.body.data).not.toHaveProperty('id');

    const item = await post('/menu-items', { category_uuid: cat.body.data.uuid, name: 'Kacchi Biryani', selling_price: '450', cost_price: 260, sku: 'BIR-001' });
    expect(item.status).toBe(201);
    expect(item.body.data).toMatchObject({ category_uuid: cat.body.data.uuid, selling_price: '450.00', cost_price: '260.00', is_available: true });

    const list = await get(`/menu-items?category_uuid=${cat.body.data.uuid}&search=kacchi`, waiter);
    expect(list.status).toBe(200);
    expect(list.body.meta.total).toBe(1);
  });

  it('validates input and permissions', async () => {
    const bad = await post('/menu-items', { category_uuid: '00000000-0000-4000-8000-000000000000', name: 'X', selling_price: -5 });
    expect(bad.status).toBe(422);
    const missingParent = await post('/menu-items', { category_uuid: '00000000-0000-4000-8000-000000000000', name: 'Kacchi', selling_price: 10 });
    expect(missingParent.status).toBe(422);
    expect(missingParent.body.error.details[0].field).toBe('category_uuid');
    expect((await post('/categories', { name: 'Desserts' }, waiter)).status).toBe(403);
  });

  it('rejects duplicate SKUs and blocks deleting a category that still has items', async () => {
    const cat = (await post('/categories', { name: 'Kebab' })).body.data;
    await post('/menu-items', { category_uuid: cat.uuid, name: 'Seekh Kebab', selling_price: 180, sku: 'KEB-002' });
    expect((await post('/menu-items', { category_uuid: cat.uuid, name: 'Other', selling_price: 1, sku: 'KEB-002' })).status).toBe(409);
    expect((await api().delete(`/api/v1/categories/${cat.uuid}`).set(admin)).status).toBe(409);
  });

  it('manages variants (single default) and add-on mappings', async () => {
    const cat = (await post('/categories', { name: 'Biryani' })).body.data;
    const item = (await post('/menu-items', { category_uuid: cat.uuid, name: 'Kacchi', selling_price: 450 })).body.data;
    await post('/item-variants', { menu_item_uuid: item.uuid, name: 'Half', price: 450, is_default: true });
    await post('/item-variants', { menu_item_uuid: item.uuid, name: 'Full', price: 850, is_default: true });
    const egg = (await post('/item-addons', { name: 'Extra Egg', price: 20 })).body.data;
    const put = await api().put(`/api/v1/menu-items/${item.uuid}/addons`).set(admin).send({ addons: [{ addon_uuid: egg.uuid, max_qty: 2 }] });
    expect(put.status).toBe(200);
    const full = (await get(`/menu-items/${item.uuid}/full`)).body.data;
    expect(full.variants.filter((v) => v.is_default).map((v) => v.name)).toEqual(['Full']);
    expect(full.addons).toEqual([expect.objectContaining({ name: 'Extra Egg', price: '20.00', max_qty: 2 })]);
    // removing and re-adding restores the soft-deleted link
    await api().put(`/api/v1/menu-items/${item.uuid}/addons`).set(admin).send({ addons: [] });
    const again = await api().put(`/api/v1/menu-items/${item.uuid}/addons`).set(admin).send({ addons: [{ addon_uuid: egg.uuid }] });
    expect(again.body.data.addons).toHaveLength(1);
  });

  it('manages tables and their status', async () => {
    const t = (await post('/tables', { name: 'T1', capacity: 4, section: 'Main Hall' })).body.data;
    expect(t.status).toBe('AVAILABLE');
    const s = await api().patch(`/api/v1/tables/${t.uuid}/status`).set(admin).send({ status: 'OCCUPIED' });
    expect(s.body.data.status).toBe('OCCUPIED');
    expect((await api().delete(`/api/v1/tables/${t.uuid}`).set(admin)).status).toBe(409);
    expect((await get('/tables?status=OCCUPIED', waiter)).body.meta.total).toBe(1);
  });

  it('creates and validates coupons', async () => {
    const c = await post('/coupons', { code: 'welcome10', type: 'PERCENT', value: 10, min_order: 500, max_discount: 200 });
    expect(c.status).toBe(201);
    expect(c.body.data.code).toBe('WELCOME10');
    expect((await post('/coupons', { code: 'WELCOME10', type: 'FIXED', value: 5 })).status).toBe(409);
    expect((await post('/coupons', { code: 'TOOMUCH', type: 'PERCENT', value: 150 })).status).toBe(422);

    const small = await post('/coupons/validate', { code: 'welcome10', subtotal: 300 });
    expect(small.body.data).toMatchObject({ valid: false });
    const capped = await post('/coupons/validate', { code: 'WELCOME10', subtotal: 3000 });
    expect(capped.body.data).toMatchObject({ valid: true, discount_amount: '200.00' });
  });

  it('normalises Bangladeshi phone numbers and prevents duplicate customers', async () => {
    const c = await post('/customers', { name: 'Rahim Uddin', phone: '+880 1712-345678' });
    expect(c.status).toBe(201);
    expect(c.body.data.phone).toBe('01712345678');
    expect((await post('/customers', { name: 'Dup', phone: '01712345678' })).status).toBe(409);
    expect((await post('/customers', { name: 'Bad', phone: '12345' })).status).toBe(422);
    expect((await get('/customers?phone=8801712345678')).body.meta.total).toBe(1);
  });

  it('serves the public menu without authentication and hides cost prices', async () => {
    const cat = (await post('/categories', { name: 'Desserts' })).body.data;
    await post('/menu-items', { category_uuid: cat.uuid, name: 'Firni', selling_price: 90, cost_price: 35 });
    const res = await api().get('/api/v1/public/menu');
    expect(res.status).toBe(200);
    expect(res.body.data[0].items[0]).toMatchObject({ name: 'Firni', selling_price: '90.00' });
    expect(res.body.data[0].items[0]).not.toHaveProperty('cost_price');
  });

  it('accepts only real images on upload', async () => {
    const png = Buffer.from('89504e470d0a1a0a0000000d49484452', 'hex');
    expect(sniffImage(png)).toBe('png');
    expect(sniffImage(Buffer.from('<?php echo 1; ?>'))).toBeNull();
    const res = await api().post('/api/v1/uploads/images').set(admin).attach('image', Buffer.from('not an image at all'), 'evil.jpg');
    expect(res.status).toBe(400);
  });
});

describe('coupon rules', () => {
  const base = { is_active: true, type: 'PERCENT', value: '10', min_order: '0', max_discount: null, usage_limit: null, per_customer_limit: null, used_count: 0 };
  it('computes percent and fixed discounts with caps', () => {
    expect(evaluateCoupon(base, { subtotal: 1020 }).discount_amount).toBe(102);
    expect(evaluateCoupon({ ...base, type: 'FIXED', value: '100' }, { subtotal: 60 }).discount_amount).toBe(60);
  });
  it('enforces dates, limits and per-customer use', () => {
    expect(evaluateCoupon({ ...base, ends_at: '2020-01-01T00:00:00Z' }, { subtotal: 100 }).valid).toBe(false);
    expect(evaluateCoupon({ ...base, usage_limit: 5, used_count: 5 }, { subtotal: 100 }).valid).toBe(false);
    expect(evaluateCoupon({ ...base, per_customer_limit: 1 }, { subtotal: 100 }).reason).toMatch(/Select a customer/);
    expect(evaluateCoupon({ ...base, per_customer_limit: 1 }, { subtotal: 100, hasCustomer: true, customerUsageCount: 1 }).valid).toBe(false);
  });
});
