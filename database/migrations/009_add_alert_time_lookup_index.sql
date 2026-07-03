-- 告警统计/导出常按 created_at 范围过滤并倒序展示，补充组合索引减少时间窗口扫描成本。
CREATE INDEX IF NOT EXISTS idx_alert_created_id_desc
  ON alert_record(created_at DESC, alert_id DESC);
