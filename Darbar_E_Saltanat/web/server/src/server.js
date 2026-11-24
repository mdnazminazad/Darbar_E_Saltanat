import { createApp } from './app.js';
import { env } from './config/env.js';
import { logger } from './logger.js';
import { sequelize } from './db/models/index.js';

async function start() {
  await sequelize.authenticate();
  const server = createApp().listen(env.PORT, () => logger.info(`Darbar_E_Saltanat API listening on :${env.PORT} (${env.NODE_ENV})`));
  const shutdown = (sig) => {
    logger.info(`${sig} received, shutting down`);
    server.close(async () => { await sequelize.close(); process.exit(0); });
    setTimeout(() => process.exit(1), 10_000).unref();
  };
  process.on('SIGTERM', () => shutdown('SIGTERM'));
  process.on('SIGINT', () => shutdown('SIGINT'));
}
process.on('unhandledRejection', (err) => logger.error({ err }, 'Unhandled promise rejection'));
process.on('uncaughtException', (err) => { logger.fatal({ err }, 'Uncaught exception'); process.exit(1); });
start().catch((err) => { logger.fatal({ err }, 'Failed to start'); process.exit(1); });
