// Server-only sync bookkeeping tables (see database/mysql-schema.sql).
import { DataTypes } from 'sequelize';
import { sequelize } from '../sequelize.js';

const base = (name, table, attrs) => sequelize.define(name, {
  id: { type: DataTypes.BIGINT.UNSIGNED, autoIncrement: true, primaryKey: true },
  uuid: { type: DataTypes.CHAR(36), allowNull: false, unique: true, defaultValue: DataTypes.UUIDV4 },
  ...attrs,
}, { tableName: table, underscored: true, timestamps: true, createdAt: 'created_at', updatedAt: 'updated_at' });

export const SyncBatch = base('SyncBatch', 'sync_batches', {
  batch_id: { type: DataTypes.CHAR(36), allowNull: false, unique: true },
  device_id: { type: DataTypes.BIGINT.UNSIGNED, allowNull: false },
  status: { type: DataTypes.ENUM('PROCESSING', 'COMPLETED', 'FAILED'), allowNull: false, defaultValue: 'PROCESSING' },
  change_count: { type: DataTypes.INTEGER, allowNull: false, defaultValue: 0 },
  success_count: { type: DataTypes.INTEGER, allowNull: false, defaultValue: 0 },
  conflict_count: { type: DataTypes.INTEGER, allowNull: false, defaultValue: 0 },
  failure_count: { type: DataTypes.INTEGER, allowNull: false, defaultValue: 0 },
  response_json: DataTypes.JSON,
  error_message: DataTypes.TEXT,
  received_at: { type: DataTypes.DATE(3), allowNull: false },
  processed_at: DataTypes.DATE(3),
});

export const SyncRecord = base('SyncRecord', 'sync_records', {
  batch_id: { type: DataTypes.BIGINT.UNSIGNED, allowNull: false },
  entity: { type: DataTypes.STRING(60), allowNull: false },
  entity_uuid: { type: DataTypes.CHAR(36), allowNull: false },
  operation: { type: DataTypes.ENUM('CREATE', 'UPDATE', 'DELETE'), allowNull: false },
  client_version: { type: DataTypes.INTEGER, allowNull: false },
  server_version: DataTypes.INTEGER,
  status: { type: DataTypes.ENUM('SYNCED', 'CONFLICT', 'FAILED', 'SKIPPED'), allowNull: false },
  reason: DataTypes.STRING(500),
});

export const SyncConflict = base('SyncConflict', 'sync_conflicts', {
  device_id: { type: DataTypes.BIGINT.UNSIGNED, allowNull: true },
  entity: { type: DataTypes.STRING(60), allowNull: false },
  entity_uuid: { type: DataTypes.CHAR(36), allowNull: false },
  local_data: { type: DataTypes.JSON, allowNull: false },
  server_data: { type: DataTypes.JSON, allowNull: false },
  local_version: { type: DataTypes.INTEGER, allowNull: false },
  server_version: { type: DataTypes.INTEGER, allowNull: false },
  status: { type: DataTypes.ENUM('OPEN', 'RESOLVED_LOCAL', 'RESOLVED_SERVER', 'RESOLVED_MERGED'), allowNull: false, defaultValue: 'OPEN' },
  resolved_by_id: { type: DataTypes.BIGINT.UNSIGNED, allowNull: true },
  resolution_note: DataTypes.STRING(255),
  detected_at: { type: DataTypes.DATE(3), allowNull: false },
  resolved_at: DataTypes.DATE(3),
});
