-- 为按设备过滤摄像头补充索引，降低海康告警流回退查通道时的扫描成本。
-- 与 backend/Aura.Api/Data/CampusResourceRepository.cs:GetCamerasByDeviceIdAsync 对齐。
CREATE INDEX IF NOT EXISTS idx_map_camera_device_id
  ON map_camera(device_id);
