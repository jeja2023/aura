-- Lease, retry and dead-letter state for controlled media artifact archiving.

ALTER TABLE media_artifact
  ADD COLUMN IF NOT EXISTS attempt_count INT NOT NULL DEFAULT 0,
  ADD COLUMN IF NOT EXISTS next_attempt_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
  ADD COLUMN IF NOT EXISTS locked_by VARCHAR(256) NULL,
  ADD COLUMN IF NOT EXISTS lock_until TIMESTAMPTZ NULL,
  ADD COLUMN IF NOT EXISTS last_error VARCHAR(2000) NULL,
  ADD COLUMN IF NOT EXISTS archived_at TIMESTAMPTZ NULL;

ALTER TABLE media_artifact DROP CONSTRAINT IF EXISTS ck_media_artifact_status;
ALTER TABLE media_artifact DROP CONSTRAINT IF EXISTS media_artifact_archive_status_check;
ALTER TABLE media_artifact ADD CONSTRAINT ck_media_artifact_status
  CHECK(archive_status IN ('pending','archiving','retry_wait','archived','failed','dead_letter','not_required'));
ALTER TABLE media_artifact DROP CONSTRAINT IF EXISTS ck_media_artifact_attempt;
ALTER TABLE media_artifact ADD CONSTRAINT ck_media_artifact_attempt CHECK(attempt_count >= 0);

DROP INDEX IF EXISTS idx_media_artifact_archive_work;
CREATE INDEX idx_media_artifact_archive_work
  ON media_artifact(archive_status,next_attempt_at,lock_until,artifact_id);
