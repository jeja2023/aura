-- One-time OIDC authorization transactions. Verifiers are protected by ASP.NET Data Protection.
CREATE TABLE IF NOT EXISTS oidc_login_transaction (
  login_transaction_id UUID PRIMARY KEY,
  state_sha256 CHAR(64) NOT NULL UNIQUE,
  tenant_id BIGINT NOT NULL REFERENCES tenant_project(tenant_id) ON DELETE RESTRICT,
  oidc_provider_id BIGINT NOT NULL REFERENCES oidc_provider_config(oidc_provider_id) ON DELETE RESTRICT,
  code_verifier_protected TEXT NOT NULL,
  nonce VARCHAR(128) NOT NULL,
  return_url TEXT NOT NULL,
  step_up_challenge_id UUID NULL REFERENCES step_up_challenge(challenge_id) ON DELETE RESTRICT,
  status VARCHAR(24) NOT NULL DEFAULT 'pending',
  expires_at TIMESTAMPTZ NOT NULL,
  used_at TIMESTAMPTZ NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
  CONSTRAINT ck_oidc_login_status CHECK(status IN ('pending','used','failed','expired'))
);
CREATE INDEX IF NOT EXISTS idx_oidc_login_expiry ON oidc_login_transaction(status,expires_at);

ALTER TABLE auth_session
  ADD COLUMN IF NOT EXISTS oidc_subject VARCHAR(512) NULL,
  ADD COLUMN IF NOT EXISTS ip_address VARCHAR(128) NULL,
  ADD COLUMN IF NOT EXISTS user_agent VARCHAR(512) NULL;
