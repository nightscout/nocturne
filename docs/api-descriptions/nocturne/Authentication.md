Sign-in, token management, and multi-factor authentication.

Covers five authentication mechanisms:

- **OAuth 2.0** — Authorization Code + PKCE and Device Authorization Grant (RFC 8628). All clients are public; PKCE is mandatory — there are no client secrets.
- **OIDC** — Federated login via external identity providers, callback handling, and session management.
- **Passkeys** — WebAuthn/FIDO2 registration and login ceremonies (discoverable and non-discoverable credentials), plus recovery codes.
- **TOTP** — Time-based one-time password setup, verification, and credential lifecycle.
- **Direct Grants** — Programmatic API tokens (prefixed `noc_`) for headless / automation use cases. These bypass OAuth entirely. Legacy Nightscout API secrets (SHA-1 hashes) are automatically migrated into equivalent direct grants.

> **Footgun:** Direct grant tokens are long-lived and have no automatic expiry. Treat them like passwords.
