// Mirrors desktop DarbarESaltanat.Application.Pos.CouponRules so web and POS agree on what a coupon is worth.
const round2 = (n) => Math.round((n + Number.EPSILON) * 100) / 100;

export function discountAmount({ type, value, max_discount }, subtotal) {
  const v = Number(value);
  let d = type === 'PERCENT' ? (subtotal * v) / 100 : Math.min(v, subtotal);
  if (max_discount !== null && max_discount !== undefined) d = Math.min(d, Number(max_discount));
  return round2(Math.max(0, d));
}

/** Returns { valid, reason?, discount_amount? }. usage counts are passed in (server-side truth). */
export function evaluateCoupon(coupon, { subtotal, now = new Date(), customerUsageCount = 0, hasCustomer = false }) {
  if (!coupon || !coupon.is_active) return { valid: false, reason: 'Coupon not found or inactive' };
  if (coupon.starts_at && new Date(coupon.starts_at) > now) return { valid: false, reason: 'Coupon is not active yet' };
  if (coupon.ends_at && new Date(coupon.ends_at) < now) return { valid: false, reason: 'Coupon has expired' };
  if (subtotal < Number(coupon.min_order)) return { valid: false, reason: `Minimum order is ৳${Number(coupon.min_order).toFixed(2)}` };
  if (coupon.usage_limit !== null && coupon.usage_limit !== undefined && coupon.used_count >= coupon.usage_limit) return { valid: false, reason: 'Coupon usage limit reached' };
  if (coupon.per_customer_limit) {
    if (!hasCustomer) return { valid: false, reason: 'Select a customer to use this coupon' };
    if (customerUsageCount >= coupon.per_customer_limit) return { valid: false, reason: 'Customer has already used this coupon' };
  }
  return { valid: true, discount_amount: discountAmount(coupon, subtotal) };
}
