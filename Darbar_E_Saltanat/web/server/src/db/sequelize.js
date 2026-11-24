import { Sequelize } from 'sequelize';
import mysql2 from 'mysql2';
import { env } from '../config/env.js';
import { logger } from '../logger.js';

const common = { logging: env.NODE_ENV === 'development' ? (m) => logger.debug(m) : false };

export const sequelize =
  env.DB_DIALECT === 'sqlite'
    ? new Sequelize({ dialect: 'sqlite', storage: ':memory:', ...common })
    : new Sequelize(env.DB_NAME, env.DB_USER, env.DB_PASSWORD, {
        dialect: 'mysql',
        dialectModule: mysql2,
        host: env.DB_HOST,
        port: env.DB_PORT,
        timezone: '+00:00', // every timestamp is UTC
        dialectOptions: { charset: 'utf8mb4', decimalNumbers: false },
        pool: { max: env.DB_POOL_MAX, min: 0, idle: 10000, acquire: 30000 },
        ...common,
      });
