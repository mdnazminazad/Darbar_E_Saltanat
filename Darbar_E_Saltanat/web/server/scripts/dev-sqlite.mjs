// Development / demo server WITHOUT MySQL: runs the API on an in-memory SQLite database loaded with the
// same seed as production (database/mysql-seed.sql). Data is lost when the process stops.
//   node scripts/dev-sqlite.mjs [--port 4000] [--register-devices 2] [--info-file /tmp/e2e.json]
// Never use this in production.
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';

const args = Object.fromEntries(process.argv.slice(2).reduce((acc, a, i, all) => (a.startsWith('--') ? [...acc, [a.slice(2), all[i + 1]]] : acc), []));
process.env.DB_DIALECT = 'sqlite';
process.env.NODE_ENV = process.env.NODE_ENV || 'development';
process.env.JWT_ACCESS_SECRET = process.env.JWT_ACCESS_SECRET || crypto.randomBytes(32).toString('hex');
process.env.LOG_LEVEL = process.env.LOG_LEVEL || 'warn';
process.env.BCRYPT_ROUNDS = process.env.BCRYPT_ROUNDS || '10';

const here = path.dirname(fileURLToPath(import.meta.url));
const { createApp } = await import('../src/app.js');
const { sequelize, Device, Branch } = await import('../src/db/models/index.js');
await import('../src/db/models/registryModels.js');
await import('../src/db/models/syncModels.js');
const { hashSecret } = await import('../src/security/passwords.js');

await sequelize.sync({ force: true });

// MySQL seed → SQLite: drop SET NAMES, add created_at/updated_at (Sequelize tables have no DB defaults).
const seed = fs.readFileSync(path.resolve(here, '../../../database/mysql-seed.sql'), 'utf8');
const now = new Date().toISOString().replace('T', ' ').replace('Z', ' +00:00');
let count = 0;
for (const line of seed.split('\n')) {
  const m = /^INSERT INTO `(\w+)` \((.*)\) VALUES \((.*)\);$/.exec(line.trim());
  if (!m) continue;
  await sequelize.query(`INSERT INTO \`${m[1]}\` (${m[2]}, \`created_at\`, \`updated_at\`) VALUES (${m[3]}, '${now}', '${now}');`);
  count++;
}

const info = { url: `http://127.0.0.1:${args.port || 4000}/api/v1`, admin: { username: 'admin', password: 'ChangeMe@123' }, devices: [] };
const branch = await Branch.findOne();
for (let i = 1; i <= Number(args['register-devices'] || 0); i++) {
  const token = crypto.randomBytes(32).toString('base64url');
  const d = await Device.create({ name: `Dev Till ${i}`, branch_id: branch.id, api_key_hash: await hashSecret(token) });
  info.devices.push({ device_id: d.uuid, device_token: token });
}
if (args['info-file']) fs.writeFileSync(args['info-file'], JSON.stringify(info, null, 2));

createApp().listen(Number(args.port || 4000), '127.0.0.1', () => {
  console.log(`Darbar_E_Saltanat API (in-memory SQLite, ${count} seed rows) on ${info.url}`);
  console.log('Login: admin / ChangeMe@123');
  for (const d of info.devices) console.log(`Device ${d.device_id}  token ${d.device_token}`);
});
