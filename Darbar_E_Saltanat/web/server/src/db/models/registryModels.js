// Sequelize models generated at runtime from sync/entityRegistry.json (itself generated from
// database/tools/schema_spec.py). Hand-written Phase 1 models are reused; every other syncable
// table gets a model here, so the generic CRUD layer and the Phase 3 sync engine share one definition.
import { createRequire } from 'node:module';
import { DataTypes } from 'sequelize';
import { sequelize } from '../sequelize.js';
import * as handWritten from './index.js';

import registry from '../../sync/entityRegistry.json' with { type: 'json' };
export { registry };

const HAND_WRITTEN = {
  branches: handWritten.Branch, roles: handWritten.Role, permissions: handWritten.Permission,
  role_permissions: handWritten.RolePermission, users: handWritten.User, user_roles: handWritten.UserRole,
  settings: handWritten.Setting, audit_logs: handWritten.AuditLog,
};

function columnType(c) {
  const m = /^(s|char)(\d+)$/.exec(c.type);
  if (m) return m[1] === 's' ? DataTypes.STRING(Number(m[2])) : DataTypes.CHAR(Number(m[2]));
  switch (c.type) {
    case 'text': return DataTypes.TEXT;
    case 'int': return DataTypes.INTEGER;
    case 'bigint': return DataTypes.BIGINT;
    case 'bool': return DataTypes.BOOLEAN;
    case 'money': return DataTypes.DECIMAL(12, 2);
    case 'qty': return DataTypes.DECIMAL(14, 3);
    case 'pct': return DataTypes.DECIMAL(5, 2);
    case 'dt': return DataTypes.DATE(3);
    case 'date': return DataTypes.DATEONLY;
    case 'time': return DataTypes.TIME;
    case 'json': return DataTypes.JSON;
    case 'enum': return DataTypes.ENUM(...c.values);
    default: throw new Error(`Unknown column type ${c.type}`);
  }
}
function defaultValue(c) {
  if (c.default === undefined) return undefined;
  if (c.type === 'bool') return c.default === '1' || c.default === 'true';
  if (['int', 'bigint'].includes(c.type)) return Number(c.default);
  return c.default;
}

const models = {};
for (const [table, meta] of Object.entries(registry)) {
  if (HAND_WRITTEN[table]) { models[table] = HAND_WRITTEN[table]; continue; }
  const attrs = {
    id: { type: DataTypes.BIGINT.UNSIGNED, autoIncrement: true, primaryKey: true },
    uuid: { type: DataTypes.CHAR(36), allowNull: false, unique: true, defaultValue: DataTypes.UUIDV4 },
    sync_version: { type: DataTypes.INTEGER.UNSIGNED, allowNull: false, defaultValue: 1 },
    device_id: { type: DataTypes.CHAR(36), allowNull: true },
    sync_status: { type: DataTypes.ENUM('SYNCED', 'CONFLICT'), allowNull: false, defaultValue: 'SYNCED' },
  };
  if (meta.has_branch) attrs.branch_id = { type: DataTypes.BIGINT.UNSIGNED, allowNull: meta.global_scope };
  for (const fk of meta.fks) attrs[fk.column] = { type: DataTypes.BIGINT.UNSIGNED, allowNull: fk.nullable };
  for (const c of meta.columns) {
    attrs[c.name] = { type: columnType(c), allowNull: c.nullable, ...(c.unique ? { unique: true } : {}) };
    const d = defaultValue(c);
    if (d !== undefined) attrs[c.name].defaultValue = d;
  }
  const name = table.split('_').map((w) => w[0].toUpperCase() + w.slice(1)).join('');
  const model = sequelize.define(name, attrs, {
    tableName: table, underscored: true, timestamps: true, paranoid: true,
    createdAt: 'created_at', updatedAt: 'updated_at', deletedAt: 'deleted_at',
  });
  if (!meta.append_only) {
    model.addHook('beforeUpdate', (row) => {
      if (row.changed() && !row.changed('sync_version')) row.sync_version = (row.sync_version || 0) + 1;
    });
  }
  models[table] = model;
}

export const modelFor = (table) => {
  const m = models[table];
  if (!m) throw new Error(`No model for ${table}`);
  return m;
};
export { models };
