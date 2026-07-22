-- Keep outbound provider API credentials separate from inbound webhook credentials.
ALTER TABLE media_analysis_provider
  ADD COLUMN IF NOT EXISTS webhook_auth_type VARCHAR(32) NOT NULL DEFAULT 'hmac';

ALTER TABLE media_analysis_provider
  ADD COLUMN IF NOT EXISTS webhook_secret_ref VARCHAR(255) NULL;

UPDATE media_analysis_provider
SET webhook_secret_ref = secret_ref
WHERE webhook_secret_ref IS NULL AND auth_type = 'hmac';

ALTER TABLE media_analysis_provider
  DROP CONSTRAINT IF EXISTS ck_media_analysis_provider_webhook_auth;

ALTER TABLE media_analysis_provider
  ADD CONSTRAINT ck_media_analysis_provider_webhook_auth
  CHECK (webhook_auth_type IN ('hmac'));

COMMENT ON COLUMN media_analysis_provider.auth_type IS 'Outbound provider API authentication type';
COMMENT ON COLUMN media_analysis_provider.secret_ref IS 'Outbound provider API credential reference';
COMMENT ON COLUMN media_analysis_provider.webhook_auth_type IS 'Inbound event webhook authentication type';
COMMENT ON COLUMN media_analysis_provider.webhook_secret_ref IS 'Inbound event webhook credential reference';
