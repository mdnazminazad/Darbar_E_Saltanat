import request from 'supertest';
import { createApp } from '../src/app.js';
import { sequelize, Branch, Role, Permission, RolePermission, User, UserRole, Setting } from '../src/db/models/index.js';
import { ROLE_PERMISSIONS, ROLE_META, PERMISSIONS } from '../src/security/permissions.js';
import { hashSecret } from '../src/security/passwords.js';
import { invalidateAccess } from '../src/security/permissionService.js';

export const app = createApp();
export const api = () => request(app);

/** Fresh in-memory schema + minimal seed (same role/permission catalog as production). */
export async function resetDb() {
  invalidateAccess();
  await sequelize.sync({ force: true });
  const branch = await Branch.create({ code: 'MAIN', name: 'Main' });
  const perms = {};
  for (const code of Object.values(PERMISSIONS)) perms[code] = await Permission.create({ code, module: code.split('.')[0] });
  const roles = {};
  for (const [code, list] of Object.entries(ROLE_PERMISSIONS)) {
    roles[code] = await Role.create({ code, name: ROLE_META[code].name, is_system: true });
    for (const p of list) await RolePermission.create({ role_id: roles[code].id, permission_id: perms[p].id });
  }
  await Setting.create({ setting_key: 'restaurant_name', setting_value: 'Darbar_E_Saltanat', value_type: 'STRING' });
  await Setting.create({ setting_key: 'vat_rate', setting_value: '5', value_type: 'NUMBER', group_name: 'tax' });
  return { branch, roles };
}

export async function makeUser(username, roleCode, password = 'Passw0rd!', extra = {}) {
  const role = await Role.findOne({ where: { code: roleCode } });
  const u = await User.create({ username, full_name: username, password_hash: await hashSecret(password), ...extra });
  await UserRole.create({ user_id: u.id, role_id: role.id });
  return u;
}

export async function loginAs(username, password = 'Passw0rd!') {
  const res = await api().post('/api/v1/auth/login').send({ username, password });
  if (res.status !== 200) throw new Error(`login failed: ${res.status} ${JSON.stringify(res.body)}`);
  return res.body.data;
}
