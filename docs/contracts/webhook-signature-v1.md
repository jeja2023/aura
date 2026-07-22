# Aura media-analysis webhook signature v1

Every request must include:

- `X-Aura-Provider`: configured provider code.
- `X-Aura-Timestamp`: Unix timestamp in seconds.
- `X-Aura-Nonce`: unique random value, at most 128 characters.
- `X-Aura-Signature`: lowercase or uppercase hexadecimal HMAC-SHA256 result.

The signed canonical string is:

```text
{timestamp}\n{nonce}\n{lowercase_hex_sha256(raw_request_body)}
```

The HMAC key is the secret assigned to that provider. Aura compares signatures in constant time, rejects timestamps outside the configured window, and persists each nonce until the replay window expires. A nonce cannot be reused, including with another request body.

Aura returns `2xx` only after the signature is valid and accepted events are committed to the PostgreSQL Inbox. Duplicate `event_id` values return success without repeating business side effects.
