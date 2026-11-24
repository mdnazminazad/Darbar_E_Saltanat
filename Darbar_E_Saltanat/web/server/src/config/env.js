import 'dotenv/config';
import { z } from 'zod';

const schema = z.object({
  NODE_ENV: z.enum(['development', 'test', 'production']).default('development'),
  PORT: z.coerce.number().default(4000),
  DB_DIALECT: z.enum(['mysql', 'sqlite']).default('mysql'), // sqlite is for automated tests only
  DB_HOST: z.string().default('127.0.0.1'),
  DB_PORT: z.coerce.number().default(3306),
  DB_NAME: z.string().default('darbar_e_saltanat'),
  DB_USER: z.string().default('root'),
  DB_PASSWORD: z.string().default(''),
  DB_POOL_MAX: z.coerce.number().default(10),
  JWT_ACCESS_SECRET: z.string().min(32, 'JWT_ACCESS_SECRET must be at least 32 characters'),
  JWT_ACCESS_TTL: z.string().default('15m'),
  REFRESH_TTL_DAYS: z.coerce.number().default(30),
  BCRYPT_ROUNDS: z.coerce.number().min(4).max(15).default(12),
  MAX_FAILED_LOGINS: z.coerce.number().default(5),
  LOCKOUT_MINUTES: z.coerce.number().default(15),
  CORS_ORIGINS: z.string().default('http://localhost:5173'),
  REQUIRE_HTTPS: z.string().default('false').transform((v) => v === 'true'),
  UPLOAD_DIR: z.string().default('./uploads'),
  MAX_UPLOAD_MB: z.coerce.number().default(5),
  LOG_LEVEL: z.string().default('info'),
});

const parsed = schema.safeParse(process.env);
if (!parsed.success) {
  const msg = parsed.error.issues.map((i) => `  - ${i.path.join('.')}: ${i.message}`).join('\n');
  console.error(`Invalid environment configuration:\n${msg}`);
  process.exit(1);
}
export const env = parsed.data;
export const corsOrigins = env.CORS_ORIGINS.split(',').map((s) => s.trim()).filter(Boolean);
