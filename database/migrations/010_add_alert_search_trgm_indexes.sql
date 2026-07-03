-- Alert list filtering uses fuzzy type/detail search; add trigram indexes to avoid full table scans.
CREATE EXTENSION IF NOT EXISTS pg_trgm;

CREATE INDEX IF NOT EXISTS idx_alert_type_trgm
  ON alert_record USING GIN (alert_type gin_trgm_ops);

CREATE INDEX IF NOT EXISTS idx_alert_detail_text_trgm
  ON alert_record USING GIN ((
    COALESCE(
      CASE
        WHEN detail_json IS NULL THEN ''
        WHEN jsonb_typeof(detail_json) = 'string' THEN trim(both '"' from detail_json::text)
        ELSE CAST(detail_json AS TEXT)
      END,
      ''
    )
  ) gin_trgm_ops);
