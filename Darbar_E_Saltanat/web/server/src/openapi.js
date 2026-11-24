// OpenAPI 3 description of the implemented Phase 1 endpoints. Served at /api/docs.
const bearer = [{ bearerAuth: [] }];
const json = (schema) => ({ content: { 'application/json': { schema } } });
const okResp = { 200: { description: 'OK' }, 401: { description: 'Unauthenticated' }, 403: { description: 'Forbidden' }, 422: { description: 'Validation error' } };
const op = (tag, summary, extra = {}) => ({ tags: [tag], summary, security: bearer, responses: okResp, ...extra });
const uuidPath = [{ name: 'uuid', in: 'path', required: true, schema: { type: 'string', format: 'uuid' } }];

function crud(path, tag, noun, filters) {
  return {
    [`/${path}`]: { get: op(tag, `List ${noun}s ${filters}`.trim()), post: op(tag, `Create ${noun}`) },
    [`/${path}/{uuid}`]: { parameters: uuidPath, get: op(tag, `Get ${noun}`), patch: op(tag, `Update ${noun}`), delete: op(tag, `Soft-delete ${noun}`) },
  };
}

export const openapi = {
  openapi: '3.0.3',
  info: { title: 'Darbar_E_Saltanat API', version: '1.0.0', description: 'Restaurant management REST API. All responses: `{ success, data, meta? }` or `{ success:false, error:{ code, message, details? } }`.' },
  servers: [{ url: '/api/v1' }],
  components: {
    securitySchemes: {
      bearerAuth: { type: 'http', scheme: 'bearer', bearerFormat: 'JWT' },
      deviceId: { type: 'apiKey', in: 'header', name: 'X-Device-Id' },
      deviceToken: { type: 'apiKey', in: 'header', name: 'X-Device-Token' },
    },
  },
  paths: {
    '/health': { get: { tags: ['System'], summary: 'Liveness + DB check', responses: { 200: { description: 'OK' } } } },
    '/auth/login': { post: { tags: ['Auth'], summary: 'Login with username/email + password', requestBody: json({ type: 'object', required: ['username', 'password'], properties: { username: { type: 'string' }, password: { type: 'string' } } }), responses: { 200: { description: 'Tokens + user' }, 401: { description: 'Invalid credentials' }, 423: { description: 'Locked' } } } },
    '/auth/refresh': { post: { tags: ['Auth'], summary: 'Rotate refresh token', requestBody: json({ type: 'object', properties: { refresh_token: { type: 'string' } } }), responses: { 200: { description: 'New tokens' }, 401: { description: 'Invalid/reused/expired' } } } },
    '/auth/logout': { post: op('Auth', 'Revoke refresh token') },
    '/auth/me': { get: op('Auth', 'Current user with roles and permissions') },
    '/auth/change-password': { post: op('Auth', 'Change own password (signs out other sessions)') },
    '/users': { get: op('Users', 'List users (?page&limit&sort&order&search&role&is_active)'), post: op('Users', 'Create user') },
    '/users/{uuid}': { parameters: uuidPath, get: op('Users', 'Get user'), patch: op('Users', 'Update user / roles / PIN'), delete: op('Users', 'Soft-delete user') },
    '/users/{uuid}/reset-password': { parameters: uuidPath, post: op('Users', 'Admin password reset') },
    '/roles': { get: op('Roles', 'List roles with permissions'), post: op('Roles', 'Create role') },
    '/roles/{uuid}': { parameters: uuidPath, get: op('Roles', 'Get role'), patch: op('Roles', 'Update role / permissions'), delete: op('Roles', 'Delete custom role') },
    '/permissions': { get: op('Roles', 'Permission catalog') },
    '/branches': { get: op('Branches', 'List branches'), post: op('Branches', 'Create branch') },
    '/branches/{uuid}': { parameters: uuidPath, get: op('Branches', 'Get branch'), patch: op('Branches', 'Update branch'), delete: op('Branches', 'Soft-delete branch') },
    '/settings': { get: op('Settings', 'Effective settings (?branch_uuid)'), put: op('Settings', 'Bulk upsert settings') },
    '/settings/public': { get: { tags: ['Settings'], summary: 'Public restaurant info for the website', responses: { 200: { description: 'OK' } } } },
    '/audit-logs': { get: op('Audit', 'Search audit logs (?action&entity_type&entity_uuid&from&to)') },
    '/devices': { get: op('Devices', 'List desktop devices with heartbeat status') },
    '/devices/register': { post: op('Devices', 'Register a desktop device (returns device_token once)') },
    '/devices/heartbeat': { post: { tags: ['Devices'], summary: 'Device heartbeat', security: [{ deviceId: [], deviceToken: [] }], responses: { 200: { description: 'OK' }, 401: { description: 'Bad device credentials' } } } },
    '/devices/{uuid}/revoke': { parameters: uuidPath, post: op('Devices', 'Revoke a device') },
    ...crud('categories', 'Menu', 'category', '?search&is_active'),
    ...crud('menu-items', 'Menu', 'menu item', '?search&category_uuid&is_available&sku&barcode'),
    '/menu-items/{uuid}/full': { parameters: uuidPath, get: op('Menu', 'Item with variants and add-ons') },
    '/menu-items/{uuid}/addons': { parameters: uuidPath, put: op('Menu', 'Replace allowed add-ons [{ addon_uuid, max_qty }]') },
    ...crud('item-variants', 'Menu', 'variant', '?menu_item_uuid'),
    ...crud('item-addons', 'Menu', 'add-on', '?search'),
    ...crud('tables', 'Tables', 'table', '?status&section'),
    '/tables/{uuid}/status': { parameters: uuidPath, patch: op('Tables', 'Set table status') },
    ...crud('discounts', 'Discounts', 'preset discount', ''),
    ...crud('coupons', 'Discounts', 'coupon', '?search&is_active'),
    '/coupons/validate': { post: op('Discounts', 'Validate a coupon { code, subtotal, customer_uuid? }') },
    ...crud('customers', 'Customers', 'customer', '?search&phone'),
    '/uploads/images': { post: op('Uploads', 'Upload a menu image (multipart field "image", JPEG/PNG/WebP)') },
    '/devices/me': { get: { tags: ['Devices'], summary: 'Calling device identity and branch', security: [{ deviceId: [], deviceToken: [] }], responses: { 200: { description: 'OK' } } } },
    '/sync/push': { post: { tags: ['Sync'], summary: 'Push a batch of changes (idempotent by batch_id)', security: [{ deviceId: [], deviceToken: [] }], responses: { 200: { description: 'Per-change results: SYNCED | CONFLICT | FAILED' }, 409: { description: 'Batch in progress' } } } },
    '/sync/pull': { get: { tags: ['Sync'], summary: 'Incremental changes ?since&cursor&limit (parents first)', security: [{ deviceId: [], deviceToken: [] }], responses: { 200: { description: 'changes, has_more, cursor, next_since' } } } },
    '/sync/acknowledge': { post: { tags: ['Sync'], summary: 'Confirm a completed pull { pulled_until, pending_count }', security: [{ deviceId: [], deviceToken: [] }], responses: { 200: { description: 'OK' } } } },
    '/sync/status': { get: { tags: ['Sync'], summary: 'Device: own status · User (sync.view): all devices', responses: { 200: { description: 'OK' } } } },
    '/sync/conflicts': { get: op('Sync', 'List conflicts ?status&entity') },
    '/sync/conflicts/{uuid}/resolve': { parameters: uuidPath, post: op('Sync', 'Resolve { resolution: KEEP_SERVER | KEEP_DEVICE | MERGED, data?, note? }') },
    '/sync/batches': { get: op('Sync', 'Batches with failed changes ?failed_only&limit') },
    '/public/menu': { get: { tags: ['Public'], summary: 'Public menu for the website', responses: { 200: { description: 'OK' } } } },
  },
};
