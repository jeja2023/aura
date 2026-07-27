-- Leases and cancellation make commercial long-running tasks recoverable after process loss.
ALTER TABLE ops_high_risk_task
  ADD COLUMN IF NOT EXISTS worker_instance VARCHAR(128) NULL,
  ADD COLUMN IF NOT EXISTS lease_until TIMESTAMPTZ NULL,
  ADD COLUMN IF NOT EXISTS cancel_requested BOOLEAN NOT NULL DEFAULT FALSE;

CREATE INDEX IF NOT EXISTS idx_ops_task_claim
  ON ops_high_risk_task(status,lease_until,task_id);

ALTER TABLE data_cleanup_job
  ADD COLUMN IF NOT EXISTS worker_instance VARCHAR(128) NULL,
  ADD COLUMN IF NOT EXISTS lease_until TIMESTAMPTZ NULL,
  ADD COLUMN IF NOT EXISTS heartbeat_at TIMESTAMPTZ NULL,
  ADD COLUMN IF NOT EXISTS cancel_requested BOOLEAN NOT NULL DEFAULT FALSE,
  ADD COLUMN IF NOT EXISTS version INT NOT NULL DEFAULT 1;

CREATE INDEX IF NOT EXISTS idx_cleanup_job_claim
  ON data_cleanup_job(status,lease_until,cleanup_job_id);
