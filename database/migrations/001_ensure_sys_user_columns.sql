-- 为历史库补齐 sys_user 关键字段，避免应用启动时隐式 ALTER TABLE
ALTER TABLE sys_user
  ADD COLUMN IF NOT EXISTS display_name VARCHAR(64);

ALTER TABLE sys_user
  ADD COLUMN IF NOT EXISTS last_login_at TIMESTAMP NULL;

ALTER TABLE sys_user
  ADD COLUMN IF NOT EXISTS must_change_password BOOLEAN NOT NULL DEFAULT FALSE;

UPDATE sys_user
SET display_name = user_name
WHERE display_name IS NULL OR btrim(display_name) = '';
