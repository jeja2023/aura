CREATE TABLE IF NOT EXISTS sys_config (
  config_key VARCHAR(128) PRIMARY KEY,
  config_value TEXT NOT NULL DEFAULT '',
  updated_by VARCHAR(64) NULL,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

COMMENT ON TABLE sys_config IS '系统运行时配置表';
COMMENT ON COLUMN sys_config.config_key IS '配置键';
COMMENT ON COLUMN sys_config.config_value IS '配置值';
COMMENT ON COLUMN sys_config.updated_by IS '最后更新人';
COMMENT ON COLUMN sys_config.updated_at IS '最后更新时间';
