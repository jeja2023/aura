-- Grant tenant-scoped media operations to the built-in building administrator role.
-- Replay, vector migration and graph administration remain explicitly delegated privileges.
UPDATE sys_role
SET permission_json = (
    SELECT jsonb_agg(DISTINCT value ORDER BY value)
    FROM jsonb_array_elements_text(
        COALESCE(permission_json, '[]'::jsonb)
        || '["media.analysis.view","media.analysis.manage","media.analysis.operate","graph.view"]'::jsonb
    ) AS value
)
WHERE role_name = 'building_admin';
