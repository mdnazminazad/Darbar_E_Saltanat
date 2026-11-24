// Sequelize models for the Phase 1 tables. Column names match database/mysql-schema.sql exactly.
import { DataTypes } from 'sequelize';
import { sequelize } from '../sequelize.js';

const ID = { type: DataTypes.BIGINT.UNSIGNED, autoIncrement: true, primaryKey: true };
const UUID = { type: DataTypes.CHAR(36), allowNull: false, unique: true, defaultValue: DataTypes.UUIDV4 };
const FK = (allowNull = false) => ({ type: DataTypes.BIGINT.UNSIGNED, allowNull });

const syncColumns = {
  sync_version: { type: DataTypes.INTEGER.UNSIGNED, allowNull: false, defaultValue: 1 },
  device_id: { type: DataTypes.CHAR(36), allowNull: true },
  sync_status: { type: DataTypes.ENUM('SYNCED', 'CONFLICT'), allowNull: false, defaultValue: 'SYNCED' },
};

/** Syncable table: soft deletes, sync_version bumped on every server-side update. */
function defineSync(name, table, attrs, { branch = 'required' } = {}) {
  const columns = { id: ID, uuid: UUID, ...attrs, ...syncColumns };
  if (branch !== 'none') columns.branch_id = FK(branch === 'nullable');
  const model = sequelize.define(name, columns, {
    tableName: table, underscored: true, timestamps: true, paranoid: true,
    createdAt: 'created_at', updatedAt: 'updated_at', deletedAt: 'deleted_at',
  });
  model.addHook('beforeUpdate', (row) => {
    if (row.changed() && !row.changed('sync_version')) row.sync_version = (row.sync_version || 0) + 1;
  });
  return model;
}
function defineServer(name, table, attrs) {
  return sequelize.define(name, { id: ID, uuid: UUID, ...attrs }, {
    tableName: table, underscored: true, timestamps: true, createdAt: 'created_at', updatedAt: 'updated_at',
  });
}

export const Branch = defineSync('Branch', 'branches', {
  code: { type: DataTypes.STRING(20), allowNull: false, unique: true },
  name: { type: DataTypes.STRING(150), allowNull: false },
  address: DataTypes.TEXT,
  phone: DataTypes.STRING(30),
  email: DataTypes.STRING(150),
  timezone: { type: DataTypes.STRING(40), allowNull: false, defaultValue: 'Asia/Dhaka' },
  currency: { type: DataTypes.STRING(10), allowNull: false, defaultValue: 'BDT' },
  is_active: { type: DataTypes.BOOLEAN, allowNull: false, defaultValue: true },
}, { branch: 'none' });

export const Role = defineSync('Role', 'roles', {
  code: { type: DataTypes.STRING(40), allowNull: false, unique: true },
  name: { type: DataTypes.STRING(80), allowNull: false },
  description: DataTypes.STRING(255),
  is_system: { type: DataTypes.BOOLEAN, allowNull: false, defaultValue: false },
}, { branch: 'nullable' });

export const Permission = defineSync('Permission', 'permissions', {
  code: { type: DataTypes.STRING(80), allowNull: false, unique: true },
  module: { type: DataTypes.STRING(40), allowNull: false },
  description: DataTypes.STRING(255),
}, { branch: 'nullable' });

export const RolePermission = defineSync('RolePermission', 'role_permissions', {
  role_id: FK(), permission_id: FK(),
}, { branch: 'nullable' });

export const User = defineSync('User', 'users', {
  username: { type: DataTypes.STRING(60), allowNull: false, unique: true },
  email: { type: DataTypes.STRING(150), unique: true },
  password_hash: { type: DataTypes.STRING(255), allowNull: false },
  pin_hash: DataTypes.STRING(255),
  full_name: { type: DataTypes.STRING(150), allowNull: false },
  phone: DataTypes.STRING(30),
  is_active: { type: DataTypes.BOOLEAN, allowNull: false, defaultValue: true },
  must_change_password: { type: DataTypes.BOOLEAN, allowNull: false, defaultValue: false },
  failed_login_count: { type: DataTypes.INTEGER, allowNull: false, defaultValue: 0 },
  locked_until: DataTypes.DATE(3),
  last_login_at: DataTypes.DATE(3),
  profile_image_url: DataTypes.STRING(255),
  notes: DataTypes.TEXT,
}, { branch: 'nullable' });

export const UserRole = defineSync('UserRole', 'user_roles', { user_id: FK(), role_id: FK() }, { branch: 'nullable' });

export const Setting = defineSync('Setting', 'settings', {
  setting_key: { type: DataTypes.STRING(100), allowNull: false },
  setting_value: DataTypes.TEXT,
  value_type: { type: DataTypes.ENUM('STRING', 'NUMBER', 'BOOL', 'JSON'), allowNull: false, defaultValue: 'STRING' },
  group_name: { type: DataTypes.STRING(50), allowNull: false, defaultValue: 'general' },
}, { branch: 'nullable' });

export const AuditLog = defineSync('AuditLog', 'audit_logs', {
  user_id: FK(true),
  action: { type: DataTypes.STRING(80), allowNull: false },
  entity_type: DataTypes.STRING(60),
  entity_uuid: DataTypes.CHAR(36),
  old_values: DataTypes.JSON,
  new_values: DataTypes.JSON,
  ip_address: DataTypes.STRING(45),
  user_agent: DataTypes.STRING(255),
  occurred_at: { type: DataTypes.DATE(3), allowNull: false, defaultValue: DataTypes.NOW },
}, { branch: 'nullable' });

export const RefreshToken = defineServer('RefreshToken', 'refresh_tokens', {
  user_id: FK(),
  token_hash: { type: DataTypes.CHAR(64), allowNull: false, unique: true },
  expires_at: { type: DataTypes.DATE(3), allowNull: false },
  revoked_at: DataTypes.DATE(3),
  replaced_by_hash: DataTypes.CHAR(64),
  user_agent: DataTypes.STRING(255),
  ip_address: DataTypes.STRING(45),
});

export const Device = defineServer('Device', 'devices', {
  branch_id: FK(),
  name: { type: DataTypes.STRING(100), allowNull: false },
  platform: { type: DataTypes.STRING(40), allowNull: false, defaultValue: 'WINDOWS' },
  app_version: DataTypes.STRING(30),
  api_key_hash: { type: DataTypes.STRING(255), allowNull: false },
  status: { type: DataTypes.ENUM('ACTIVE', 'REVOKED'), allowNull: false, defaultValue: 'ACTIVE' },
  last_heartbeat_at: DataTypes.DATE(3),
  last_pull_at: DataTypes.DATE(3),
  last_push_at: DataTypes.DATE(3),
  pending_count: { type: DataTypes.INTEGER, allowNull: false, defaultValue: 0 },
  last_ip: DataTypes.STRING(45),
});

// ---- associations
User.belongsToMany(Role, { through: UserRole, foreignKey: 'user_id', otherKey: 'role_id', as: 'roles' });
Role.belongsToMany(User, { through: UserRole, foreignKey: 'role_id', otherKey: 'user_id', as: 'users' });
Role.belongsToMany(Permission, { through: RolePermission, foreignKey: 'role_id', otherKey: 'permission_id', as: 'permissions' });
Permission.belongsToMany(Role, { through: RolePermission, foreignKey: 'permission_id', otherKey: 'role_id', as: 'roles' });
User.belongsTo(Branch, { foreignKey: 'branch_id', as: 'branch' });
AuditLog.belongsTo(User, { foreignKey: 'user_id', as: 'user' });
Device.belongsTo(Branch, { foreignKey: 'branch_id', as: 'branch' });
Setting.belongsTo(Branch, { foreignKey: 'branch_id', as: 'branch' });

export { sequelize };
