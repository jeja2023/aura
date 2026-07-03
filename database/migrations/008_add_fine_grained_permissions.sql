-- 细粒度高风险权限：导出、设备诊断、告警操作、AI 配置。
-- 保持已有 building_admin 的常规权限，同时追加可单独授权的能力位。
UPDATE sys_role
SET permission_json = (
    SELECT jsonb_agg(DISTINCT value)
    FROM jsonb_array_elements_text(
        COALESCE(permission_json, '[]'::jsonb)
        || '["alert.manage","device.diag","export"]'::jsonb
    ) AS value
)
WHERE role_name = 'building_admin';
