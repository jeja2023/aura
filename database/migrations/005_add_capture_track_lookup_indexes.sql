-- 为抓拍图片回查与轨迹回放补齐组合索引，降低大表场景下的最近图片匹配和 VID 轨迹查询成本。
CREATE INDEX IF NOT EXISTS idx_track_event_vid_time_desc
  ON track_event(vid, event_time DESC, event_id DESC);

CREATE INDEX IF NOT EXISTS idx_capture_device_time_image
  ON capture_record(device_id, capture_time DESC, capture_id DESC)
  WHERE image_path IS NOT NULL AND btrim(image_path) <> '';

CREATE INDEX IF NOT EXISTS idx_capture_feature_time_image
  ON capture_record(feature_id, capture_time DESC, capture_id DESC)
  WHERE feature_id IS NOT NULL AND image_path IS NOT NULL AND btrim(image_path) <> '';
