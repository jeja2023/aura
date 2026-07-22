# Aura media-analysis provider credentials

Provider credentials are referenced by `media_analysis_provider.secret_ref`; plaintext values are not stored in PostgreSQL. The built-in resolver supports `env://`, `config://`, and `secret://` references.

Supported outbound authentication types:

- `none`: no provider authentication. Use only on explicitly allowed trusted networks.
- `hmac`: the resolved secret signs the timestamp, nonce, and SHA-256 request-body digest.
- `bearer`: the resolved secret is sent as a bearer token.
- `oauth2_client`: the resolved secret is the JSON object below. Aura obtains and caches a client-credentials token.
- `mtls`: Aura uses the client certificate configured under `MediaAnalysis:Http:Mtls`. The provider URL must use HTTPS.

OAuth2 secret value:

```json
{
  "client_id": "aura-client",
  "client_secret": "injected-secret",
  "token_url": "https://identity.example/oauth/token",
  "scope": "analysis.read analysis.write",
  "audience": "media-analysis"
}
```

`scope` and `audience` are optional. `token_url` is subject to the same scheme, host allowlist, DNS, and private-address controls as provider URLs. Redirects are disabled for provider and token requests.

mTLS configuration:

```json
{
  "MediaAnalysis": {
    "Http": {
      "Mtls": {
        "CertificatePath": "/run/secrets/aura-provider-client.pfx",
        "CertificatePasswordEnvironmentVariable": "AURA_PROVIDER_CERT_PASSWORD"
      }
    }
  }
}
```

The direct `CertificatePassword` setting exists for configuration-provider integration but should remain empty in repository files. Prefer an environment variable or mounted secret provider.

Inbound webhooks use the versioned HMAC contract in `webhook-signature-v1.md`. Outbound provider API authentication uses `auth_type` and `secret_ref`; inbound event signing independently uses `webhook_auth_type` and `webhook_secret_ref`. Do not reuse OAuth client secrets, bearer tokens, or client-certificate passwords as webhook HMAC keys.
