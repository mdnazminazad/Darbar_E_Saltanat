import { z } from 'zod';

export const money = z.union([z.number(), z.string().trim().regex(/^\d+(\.\d{1,2})?$/, 'Enter an amount like 450 or 450.50')])
  .transform((v) => Number(v)).refine((v) => v >= 0 && v < 1e10, 'Amount out of range');
export const percent = z.union([z.number(), z.string().trim().regex(/^\d+(\.\d{1,2})?$/)])
  .transform((v) => Number(v)).refine((v) => v >= 0 && v <= 100, 'Must be between 0 and 100');
const uuid = z.string().uuid();
const optText = (max) => z.string().trim().max(max).nullable().optional();

/** Bangladesh mobile: 01XXXXXXXXX (accepts +880 / 880 prefixes, spaces, dashes). */
export function normalizeBdPhone(raw) {
  let p = String(raw).replace(/[\s\-()]/g, '');
  if (p.startsWith('+880')) p = '0' + p.slice(4);
  else if (p.startsWith('880')) p = '0' + p.slice(3);
  return p;
}
export const bdPhone = z.string().transform(normalizeBdPhone).refine((p) => /^01[3-9]\d{8}$/.test(p), 'Enter a valid Bangladeshi mobile number (01XXXXXXXXX)');

export const category = z.object({
  name: z.string().trim().min(2).max(120),
  parent_uuid: uuid.nullable().optional(),
  description: optText(2000),
  image_url: optText(500),
  kitchen_station: optText(60),
  sort_order: z.number().int().min(0).max(100000).default(0),
  is_active: z.boolean().default(true),
});

export const menuItem = z.object({
  category_uuid: uuid,
  name: z.string().trim().min(2).max(150),
  name_bn: optText(150),
  description: optText(2000),
  sku: optText(60),
  barcode: optText(60),
  image_url: optText(500),
  cost_price: money.default(0),
  selling_price: money,
  tax_rate: percent.default(0),
  prep_time_minutes: z.number().int().min(0).max(600).default(15),
  sort_order: z.number().int().min(0).max(100000).default(0),
  is_available: z.boolean().default(true),
  is_active: z.boolean().default(true),
});

export const variant = z.object({
  menu_item_uuid: uuid,
  name: z.string().trim().min(1).max(60),
  sku: optText(60),
  price: money,
  cost_price: money.default(0),
  is_default: z.boolean().default(false),
  is_active: z.boolean().default(true),
});

export const addon = z.object({
  name: z.string().trim().min(2).max(100),
  group_name: optText(60),
  price: money.default(0),
  is_active: z.boolean().default(true),
});

export const addonMapping = z.object({ menu_item_uuid: uuid, addon_uuid: uuid, max_qty: z.number().int().min(1).max(20).default(1) });

export const TABLE_STATUSES = ['AVAILABLE', 'RESERVED', 'OCCUPIED', 'BILLING', 'CLEANING'];
export const table = z.object({
  name: z.string().trim().min(1).max(30),
  capacity: z.number().int().min(1).max(100).default(4),
  section: optText(60),
  status: z.enum(TABLE_STATUSES).default('AVAILABLE'),
  is_active: z.boolean().default(true),
});

const discountBase = {
  type: z.enum(['PERCENT', 'FIXED']),
  value: money,
  min_order: money.default(0),
  max_discount: money.nullable().optional(),
  is_active: z.boolean().default(true),
};
const percentCap = (o) => o.type !== 'PERCENT' || o.value === undefined || o.value <= 100;
const capRule = { path: ['value'], message: 'Percentage cannot exceed 100' };
const datesRule = [(o) => !o.starts_at || !o.ends_at || new Date(o.starts_at) < new Date(o.ends_at), { path: ['ends_at'], message: 'End must be after start' }];
const nonEmpty = [(o) => Object.keys(o).length > 0, 'Nothing to update'];

const discountObject = z.object({ name: z.string().trim().min(2).max(100), requires_permission: z.boolean().default(false), ...discountBase });
export const discount = discountObject.refine(percentCap, capRule);
export const discountUpdate = discountObject.partial().refine(percentCap, capRule).refine(...nonEmpty);

const couponObject = z.object({
  code: z.string().trim().toUpperCase().regex(/^[A-Z0-9_-]{3,40}$/, '3–40 letters, digits, - or _'),
  description: optText(255),
  usage_limit: z.number().int().min(1).nullable().optional(),
  per_customer_limit: z.number().int().min(1).nullable().optional(),
  starts_at: z.string().datetime().nullable().optional(),
  ends_at: z.string().datetime().nullable().optional(),
  ...discountBase,
});
export const coupon = couponObject.refine(percentCap, capRule).refine(...datesRule);
export const couponUpdate = couponObject.partial().refine(percentCap, capRule).refine(...datesRule).refine(...nonEmpty);

export const customer = z.object({
  name: z.string().trim().min(2).max(150),
  phone: bdPhone,
  email: z.string().email().max(150).nullable().optional(),
  address: optText(2000),
  notes: optText(2000),
  credit_limit: money.default(0),
  is_active: z.boolean().default(true),
});
