-- Log list/export time-window paging indexes.
CREATE INDEX IF NOT EXISTS idx_log_operation_created_id_desc
  ON log_operation(created_at DESC, op_id DESC);

CREATE INDEX IF NOT EXISTS idx_log_system_created_id_desc
  ON log_system(created_at DESC, system_log_id DESC);
