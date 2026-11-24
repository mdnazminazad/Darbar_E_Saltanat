import express from 'express';
import helmet from 'helmet';
import cors from 'cors';
import compression from 'compression';
import pinoHttp from 'pino-http';
import crypto from 'node:crypto';
import swaggerUi from 'swagger-ui-express';
import { env, corsOrigins } from './config/env.js';
import { logger } from './logger.js';
import { sequelize } from './db/models/index.js';
import { requireHttps, apiLimiter } from './middleware/security.js';
import { notFound, errorHandler } from './middleware/errorHandler.js';
import { ok } from './utils/http.js';
import { openapi } from './openapi.js';
import authRoutes from './modules/auth/auth.routes.js';
import userRoutes from './modules/users/users.routes.js';
import roleRoutes, { permissionsRouter } from './modules/roles/roles.routes.js';
import branchRoutes from './modules/branches.routes.js';
import settingsRoutes from './modules/settings.routes.js';
import auditRoutes from './modules/auditLogs.routes.js';
import deviceRoutes from './modules/devices.routes.js';
import uploadRoutes from './modules/uploads.routes.js';
import syncRoutes from './modules/sync.routes.js';
import ordersRoutes from './modules/orders/orders.routes.js';
import inventoryRoutes from './modules/inventory/inventory.routes.js';
import reportsRoutes from './modules/reports/reports.routes.js';
import './db/models/syncModels.js';
import {
  categoriesRouter, menuItemsRouter, variantsRouter, addonsRouter, tablesRouter, discountsRouter,
  couponsRouter, couponValidateRouter, customersRouter, publicMenuRouter,
} from './modules/catalog/catalog.routes.js';
import path from 'node:path';

export function createApp() {
  const app = express();
  app.set('trust proxy', 1); // Nginx/Apache reverse proxy in front
  app.disable('x-powered-by');

  app.use(pinoHttp({ logger, genReqId: (req) => req.headers['x-request-id'] || crypto.randomUUID(), autoLogging: env.NODE_ENV !== 'test' }));
  app.use(helmet());
  app.use(cors({
    origin: (origin, cb) => cb(null, !origin || corsOrigins.includes(origin)),
    credentials: true, methods: ['GET', 'POST', 'PUT', 'PATCH', 'DELETE'],
    allowedHeaders: ['Content-Type', 'Authorization', 'X-Device-Id', 'X-Device-Token', 'X-Request-Id'],
  }));
  app.use(compression());
  app.use(express.json({ limit: '5mb' })); // sync batches can be large
  app.use(requireHttps);

  const v1 = express.Router();
  v1.use(apiLimiter);
  v1.get('/health', async (_req, res) => {
    let db = 'connected';
    try { await sequelize.authenticate(); } catch { db = 'disconnected'; }
    const isOk = db === 'connected';
    const info = {
      status: isOk ? 'ok' : 'degraded',
      database: db,
      db: isOk ? 'up' : 'down',
      serverTime: new Date().toISOString(),
      time: new Date().toISOString(),
      version: '1.0.0',
    };
    res.status(isOk ? 200 : 503);
    res.json({
      success: isOk,
      ...info,
      data: info,
    });
  });
  v1.use('/auth', authRoutes);
  v1.use('/users', userRoutes);
  v1.use('/roles', roleRoutes);
  v1.use('/permissions', permissionsRouter);
  v1.use('/branches', branchRoutes);
  v1.use('/settings', settingsRoutes);
  v1.use('/audit-logs', auditRoutes);
  v1.use('/devices', deviceRoutes);
  v1.use('/categories', categoriesRouter);
  v1.use('/menu-items', menuItemsRouter);
  v1.use('/item-variants', variantsRouter);
  v1.use('/item-addons', addonsRouter);
  v1.use('/tables', tablesRouter);
  v1.use('/orders', ordersRoutes);
  v1.use('/inventory', inventoryRoutes);
  v1.use('/reports', reportsRoutes);
  v1.use('/discounts', discountsRouter);
  v1.use('/coupons', couponValidateRouter);
  v1.use('/coupons', couponsRouter);
  v1.use('/customers', customersRouter);
  v1.use('/uploads', uploadRoutes);
  v1.use('/sync', syncRoutes);
  v1.use('/public', publicMenuRouter);
  app.use('/api/v1', v1);
  // In production Nginx serves /uploads directly; this keeps development self-contained.
  app.use('/uploads', express.static(path.resolve(env.UPLOAD_DIR), { fallthrough: false, index: false, dotfiles: 'deny' }));

  if (env.NODE_ENV !== 'production') app.use('/api/docs', swaggerUi.serve, swaggerUi.setup(openapi));
  app.get('/api/openapi.json', (_req, res) => res.json(openapi));

  app.use(notFound);
  app.use(errorHandler);
  return app;
}
