-- 历史库兼容：为日志模糊检索启用 pg_trgm，并补齐 GIN trigram 索引
CREATE EXTENSION IF NOT EXISTS pg_trgm;

CREATE INDEX IF NOT EXISTS idx_log_operation_operator_name_trgm
  ON log_operation USING GIN (operator_name gin_trgm_ops);

CREATE INDEX IF NOT EXISTS idx_log_operation_action_type_trgm
  ON log_operation USING GIN (action_type gin_trgm_ops);

CREATE INDEX IF NOT EXISTS idx_log_operation_action_detail_trgm
  ON log_operation USING GIN (action_detail gin_trgm_ops);

CREATE INDEX IF NOT EXISTS idx_log_system_level_trgm
  ON log_system USING GIN (level gin_trgm_ops);

CREATE INDEX IF NOT EXISTS idx_log_system_source_trgm
  ON log_system USING GIN (source gin_trgm_ops);

CREATE INDEX IF NOT EXISTS idx_log_system_message_trgm
  ON log_system USING GIN (message gin_trgm_ops);
