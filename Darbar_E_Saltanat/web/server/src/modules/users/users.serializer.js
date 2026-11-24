export function publicUser(u, access) {
  let idNumber = null;
  let accessScope = 'BOTH';
  if (u.notes) {
    try {
      const parsed = typeof u.notes === 'string' ? JSON.parse(u.notes) : u.notes;
      if (parsed && typeof parsed === 'object') {
        idNumber = parsed.id_number ?? null;
        accessScope = parsed.access_scope ?? 'BOTH';
      }
    } catch {
      const matchId = String(u.notes).match(/ID:\s*([A-Za-z0-9-_]+)/i);
      if (matchId) idNumber = matchId[1];
      const matchAccess = String(u.notes).match(/Access:\s*([A-Za-z0-9-_ &]+)/i);
      if (matchAccess) {
        const acc = matchAccess[1].toUpperCase();
        accessScope = acc.includes('DESK') ? 'DESKTOP' : acc.includes('WEB') ? 'WEB' : 'BOTH';
      }
    }
  }

  return {
    uuid: u.uuid,
    username: u.username,
    id_number: idNumber,
    access_scope: accessScope,
    email: u.email,
    full_name: u.full_name,
    phone: u.phone,
    is_active: !!u.is_active,
    status: u.is_active ? 'ACTIVE' : 'INACTIVE',
    must_change_password: !!u.must_change_password,
    has_pin: !!u.pin_hash,
    profile_image_url: u.profile_image_url ?? null,
    notes: u.notes ?? null,
    failed_login_count: u.failed_login_count ?? 0,
    locked_until: u.locked_until ?? null,
    branch_uuid: u.branch?.uuid ?? null,
    branch_name: u.branch?.name ?? null,
    last_login_at: u.last_login_at,
    created_at: u.created_at,
    updated_at: u.updated_at,
    roles: access ? access.roles : (u.roles || []).map((r) => (typeof r === 'string' ? r : r.code)),
    ...(access ? { permissions: access.isSuperAdmin ? ['*'] : [...access.permissions].sort() } : {}),
  };
}
