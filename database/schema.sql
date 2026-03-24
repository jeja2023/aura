-- 寓瞳系统第一阶段基础表结构
-- 字符集：utf8mb4 + utf8mb4_unicode_ci（完整 Unicode，含 emoji）；与连接串中的 database= 保持一致
CREATE DATABASE IF NOT EXISTS `aura` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
USE `aura`;

CREATE TABLE IF NOT EXISTS sys_role (
  role_id BIGINT NOT NULL AUTO_INCREMENT COMMENT '角色主键',
  role_name VARCHAR(64) NOT NULL COMMENT '角色编码，如 super_admin、building_admin',
  permission_json JSON NULL COMMENT '权限列表 JSON，如 ["device","roi"]',
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT '创建时间',
  PRIMARY KEY (role_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='系统角色';

CREATE TABLE IF NOT EXISTS sys_user (
  user_id BIGINT NOT NULL AUTO_INCREMENT COMMENT '用户主键',
  user_name VARCHAR(64) NOT NULL COMMENT '登录用户名',
  password_hash VARCHAR(255) NOT NULL COMMENT 'BCrypt 等算法哈希后的密码',
  role_id BIGINT NOT NULL COMMENT '关联 sys_role.role_id',
  status TINYINT NOT NULL DEFAULT 1 COMMENT '状态：1 启用，0 禁用',
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT '创建时间',
  PRIMARY KEY (user_id),
  UNIQUE KEY uk_sys_user_name (user_name),
  CONSTRAINT fk_sys_user_role FOREIGN KEY (role_id) REFERENCES sys_role (role_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='系统用户';

CREATE TABLE IF NOT EXISTS nvr_device (
  device_id BIGINT NOT NULL AUTO_INCREMENT COMMENT '设备主键',
  name VARCHAR(128) NOT NULL COMMENT '设备显示名称',
  ip VARCHAR(64) NOT NULL COMMENT '设备 IP',
  port INT NOT NULL COMMENT '服务端口',
  brand VARCHAR(32) NOT NULL COMMENT '厂商，如 hikvision',
  protocol VARCHAR(32) NOT NULL COMMENT '对接协议，如 isapi、onvif',
  hmac_secret VARCHAR(255) NULL COMMENT '抓拍回调 HMAC 密钥（可选）',
  status VARCHAR(16) NOT NULL DEFAULT 'offline' COMMENT '在线状态，如 online、offline',
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT '创建时间',
  PRIMARY KEY (device_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='NVR/视频采集设备';

CREATE TABLE IF NOT EXISTS map_roi (
  roi_id BIGINT NOT NULL AUTO_INCREMENT COMMENT 'ROI 主键',
  camera_id BIGINT NOT NULL COMMENT '关联 map_camera.camera_id',
  room_node_id BIGINT NOT NULL COMMENT '关联房间节点 dict_campus.node_id',
  vertices_json JSON NOT NULL COMMENT '多边形顶点坐标等 JSON',
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT '创建时间',
  PRIMARY KEY (roi_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='地图 ROI（房间区域多边形）';

CREATE TABLE IF NOT EXISTS map_floor (
  floor_id BIGINT NOT NULL AUTO_INCREMENT COMMENT '楼层图主键',
  node_id BIGINT NOT NULL COMMENT '关联楼层节点 dict_campus.node_id',
  file_path VARCHAR(255) NOT NULL COMMENT '平面图相对/绝对路径',
  scale_ratio DECIMAL(10,4) NOT NULL DEFAULT 1.0000 COMMENT '像素与实际比例',
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT '创建时间',
  PRIMARY KEY (floor_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='楼层平面图';

CREATE TABLE IF NOT EXISTS map_camera (
  camera_id BIGINT NOT NULL AUTO_INCREMENT COMMENT '布点主键',
  floor_id BIGINT NOT NULL COMMENT '关联 map_floor.floor_id',
  device_id BIGINT NOT NULL COMMENT '关联 nvr_device.device_id',
  channel_no INT NOT NULL COMMENT '设备通道号',
  pos_x DECIMAL(12,4) NOT NULL DEFAULT 0 COMMENT '在楼层图上的 X 坐标',
  pos_y DECIMAL(12,4) NOT NULL DEFAULT 0 COMMENT '在楼层图上的 Y 坐标',
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT '创建时间',
  PRIMARY KEY (camera_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='楼层图摄像头布点';

CREATE TABLE IF NOT EXISTS dict_campus (
  node_id BIGINT NOT NULL AUTO_INCREMENT COMMENT '节点主键',
  parent_id BIGINT NULL COMMENT '父节点 node_id，根节点为 NULL',
  level_type VARCHAR(32) NOT NULL COMMENT '层级类型：campus/building/floor/room 等',
  node_name VARCHAR(128) NOT NULL COMMENT '节点显示名称',
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT '创建时间',
  PRIMARY KEY (node_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='园区-楼栋-楼层-房间资源树';

CREATE TABLE IF NOT EXISTS capture_record (
  capture_id BIGINT NOT NULL AUTO_INCREMENT COMMENT '抓拍记录主键',
  device_id BIGINT NOT NULL COMMENT '关联 nvr_device.device_id',
  channel_no INT NOT NULL COMMENT '通道号',
  capture_time DATETIME NOT NULL COMMENT '抓拍时间',
  image_path VARCHAR(255) NULL COMMENT '图片存储路径（可选）',
  feature_id VARCHAR(128) NULL COMMENT '向量/特征 ID（可选）',
  metadata_json JSON NULL COMMENT 'AI 元数据、扩展字段 JSON',
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT '入库时间',
  PRIMARY KEY (capture_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='抓拍记录';

CREATE TABLE IF NOT EXISTS track_event (
  event_id BIGINT NOT NULL AUTO_INCREMENT COMMENT '轨迹事件主键',
  vid VARCHAR(64) NOT NULL COMMENT '虚拟身份 ID',
  camera_id BIGINT NOT NULL COMMENT '关联 map_camera.camera_id',
  roi_id BIGINT NOT NULL COMMENT '关联 map_roi.roi_id',
  event_time DATETIME NOT NULL COMMENT '事件发生时间',
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT '入库时间',
  PRIMARY KEY (event_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='人员轨迹事件';

CREATE TABLE IF NOT EXISTS judge_result (
  judge_id BIGINT NOT NULL AUTO_INCREMENT COMMENT '研判结果主键',
  vid VARCHAR(64) NOT NULL COMMENT '虚拟身份 ID',
  room_id BIGINT NOT NULL COMMENT '房间节点 ID',
  judge_type VARCHAR(32) NOT NULL COMMENT '研判类型：home_room、group_rent 等',
  judge_date DATE NOT NULL COMMENT '研判业务日期',
  detail_json JSON NULL COMMENT '研判明细 JSON',
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT '入库时间',
  PRIMARY KEY (judge_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='业务研判结果';

CREATE TABLE IF NOT EXISTS virtual_person (
  v_id VARCHAR(64) NOT NULL COMMENT '虚拟身份主键 VID',
  first_seen DATETIME NOT NULL COMMENT '首次出现时间',
  last_seen DATETIME NOT NULL COMMENT '最近出现时间',
  device_id BIGINT NOT NULL COMMENT '最近关联设备 nvr_device.device_id',
  capture_count INT NOT NULL DEFAULT 0 COMMENT '累计抓拍次数',
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT '记录创建时间',
  PRIMARY KEY (v_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='虚拟人员汇总';

CREATE TABLE IF NOT EXISTS alert_record (
  alert_id BIGINT NOT NULL AUTO_INCREMENT COMMENT '告警主键',
  alert_type VARCHAR(32) NOT NULL COMMENT '告警类型编码',
  vid VARCHAR(64) NULL COMMENT '关联虚拟身份（可选）',
  room_id BIGINT NULL COMMENT '关联房间节点（可选）',
  detail_json JSON NULL COMMENT '告警详情 JSON',
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT '产生时间',
  PRIMARY KEY (alert_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='告警记录';

CREATE TABLE IF NOT EXISTS log_operation (
  op_id BIGINT NOT NULL AUTO_INCREMENT COMMENT '日志主键',
  operator_name VARCHAR(64) NOT NULL COMMENT '操作人账号或系统标识',
  action_type VARCHAR(64) NOT NULL COMMENT '动作类型，如 设备注册、模拟抓拍',
  action_detail TEXT NULL COMMENT '动作明细描述',
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT '记录时间',
  PRIMARY KEY (op_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='操作审计日志';

-- =========================
-- 性能关键索引（按查询模式）
-- =========================

-- 抓拍查询：按 capture_time 范围 + 反向排序分页
CREATE INDEX IF NOT EXISTS idx_capture_time_desc
  ON capture_record(capture_time, capture_id);

-- 轨迹查询：
-- 1) 按 event_time 范围（用于研判）
-- 2) 按 vid 精确查询 + event_id 反向排序（用于回溯）
CREATE INDEX IF NOT EXISTS idx_track_event_time_desc
  ON track_event(event_time, event_id);
CREATE INDEX IF NOT EXISTS idx_track_event_vid_desc
  ON track_event(vid, event_id);

-- 研判结果查询：
-- 按 judge_date / judge_type 条件过滤 + judge_id 反向排序
CREATE INDEX IF NOT EXISTS idx_judge_date_type_desc
  ON judge_result(judge_date, judge_type, judge_id);

-- 告警查询：目前主要按 alert_id 反向排序返回
CREATE INDEX IF NOT EXISTS idx_alert_id_desc
  ON alert_record(alert_id);

INSERT IGNORE INTO sys_role (role_id, role_name, permission_json) VALUES
  (1, 'super_admin', JSON_ARRAY('all')),
  (2, 'building_admin', JSON_ARRAY('device', 'roi', 'track', 'alert', 'stats'));
