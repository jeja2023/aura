-- 历史库兼容：同步身份主键序列，避免应用运行时再隐式修补 sequence。

SELECT setval(
  pg_get_serial_sequence('sys_role', 'role_id'),
  COALESCE((SELECT MAX(role_id) FROM sys_role), 0) + 1,
  false
);

SELECT setval(
  pg_get_serial_sequence('sys_user', 'user_id'),
  COALESCE((SELECT MAX(user_id) FROM sys_user), 0) + 1,
  false
);
