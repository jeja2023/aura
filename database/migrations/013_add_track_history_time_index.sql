-- Align the global track history feed with its time-ordered query path.
CREATE INDEX IF NOT EXISTS idx_track_event_time_id_desc
  ON track_event(event_time DESC, event_id DESC);
