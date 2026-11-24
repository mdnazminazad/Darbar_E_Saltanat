// Applies database/mysql-schema.sql (version 001_initial) and any database/migrations/NNN_*.sql
// in order, recording each in schema_migrations. Usage: npm run migrate [-- --seed]
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import mysql from 'mysql2/promise';
import { env } from '../config/env.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const dbDir = process.env.DATABASE_DIR || path.resolve(here, '../../../../database');

async function main() {
  const conn = await mysql.createConnection({
    host: env.DB_HOST, port: env.DB_PORT, user: env.DB_USER, password: env.DB_PASSWORD,
    multipleStatements: true, charset: 'utf8mb4', timezone: 'Z',
  });
  await conn.query(`CREATE DATABASE IF NOT EXISTS \`${env.DB_NAME}\` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci`);
  await conn.query(`USE \`${env.DB_NAME}\``);

  const [t] = await conn.query("SHOW TABLES LIKE 'schema_migrations'");
  const initialised = t.length > 0;
  const applied = new Set();
  if (initialised) {
    const [rows] = await conn.query('SELECT version FROM schema_migrations');
    rows.forEach((r) => applied.add(r.version));
  }

  const steps = [{ version: '001_initial', file: path.join(dbDir, 'mysql-schema.sql') }];
  const migDir = path.join(dbDir, 'migrations');
  if (fs.existsSync(migDir)) {
    fs.readdirSync(migDir).filter((f) => /^\d+_.*\.sql$/.test(f)).sort()
      .forEach((f) => steps.push({ version: f.replace(/\.sql$/, ''), file: path.join(migDir, f) }));
  }
  for (const s of steps) {
    if (applied.has(s.version)) continue;
    console.log(`Applying ${s.version} ...`);
    await conn.query(fs.readFileSync(s.file, 'utf8'));
    await conn.query('INSERT IGNORE INTO schema_migrations (version) VALUES (?)', [s.version]);
  }
  if (process.argv.includes('--seed')) {
    const [[{ c }]] = await conn.query('SELECT COUNT(*) AS c FROM branches');
    if (c > 0) console.log('Seed skipped: branches already exist.');
    else { console.log('Seeding ...'); await conn.query(fs.readFileSync(path.join(dbDir, 'mysql-seed.sql'), 'utf8')); }
  }
  await conn.end();
  console.log('Migrations complete.');
}
main().catch((e) => { console.error(e); process.exit(1); });
