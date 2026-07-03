-- Capture list filters: device/channel/time paging.
CREATE INDEX IF NOT EXISTS idx_capture_device_channel_time_desc
  ON capture_record(device_id, channel_no, capture_time DESC, capture_id DESC);
