ALTER TABLE nvr_device
  ADD COLUMN IF NOT EXISTS last_seen_at TIMESTAMPTZ NULL;

CREATE INDEX IF NOT EXISTS idx_nvr_device_status_last_seen
  ON nvr_device(status, last_seen_at DESC);
