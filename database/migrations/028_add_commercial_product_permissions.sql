-- Grant only the routine operator workflow to the built-in building administrator.
-- Review, approval, original evidence, legal hold and high-impact operations remain delegated.
UPDATE sys_role
SET permission_json = (
  SELECT jsonb_agg(DISTINCT value ORDER BY value)
  FROM jsonb_array_elements_text(
    COALESCE(permission_json, '[]'::jsonb)
    || '["event.view","event.manage","case.view","case.manage","investigation.view","investigation.manage","integration.view","integration.test","ops.view","usage.view","data.governance.view"]'::jsonb
  ) AS value
)
WHERE role_name = 'building_admin';

INSERT INTO product_capability(
  capability_code,product_version,status,supported_targets_json,limitations_json,owner)
VALUES
  ('HIKVISION_ISAPI','0.3.0','supported','["tested integration surface"]'::jsonb,'["Certification remains model and firmware specific"]'::jsonb,'integration'),
  ('ONVIF','0.3.0','experimental','["capture ingestion contract"]'::jsonb,'["Real-device discovery and snapshot certification required"]'::jsonb,'integration'),
  ('DAHUA','0.3.0','planned','[]'::jsonb,'["UI placeholders are not product support"]'::jsonb,'integration'),
  ('CPP_SDK_GATEWAY','0.3.0','planned','[]'::jsonb,'["Requires customer SDK, device and isolated gateway validation"]'::jsonb,'integration')
ON CONFLICT(capability_code,product_version) DO UPDATE SET
  status=EXCLUDED.status,
  supported_targets_json=EXCLUDED.supported_targets_json,
  limitations_json=EXCLUDED.limitations_json,
  owner=EXCLUDED.owner,
  updated_at=CURRENT_TIMESTAMP;
