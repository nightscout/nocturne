---
name: dev-tenant-api
description: "Interact with Nocturne's local dev-only API: seed a loginable tenant preloaded with realistic sample data, obtain a browser session (loginLink) or bearer token headlessly, export/re-seed the dev passkey fixture, trigger recovery mode, and run the end-to-end smoke test. Use when testing against a local `aspire start` stack, when a tenant returns 503 setup_required, when you need a logged-in browser tab or screenshot of the tenant UI, or when you need test data without reverse-engineering payloads. DO NOT USE against production images — the dev-only controllers exist only when the API runs in Development."
---

# Dev Tenant API (local testing)

Requires a running stack (`aspire start`; see the aspire skill). Run-mode pins:

| Endpoint | URL |
|---|---|
| nocturne-api (direct) | `http://localhost:1610` |
| Gateway / tenant UI | `https://<slug>.nocturne.localhost:1612` |

Worktrees get dynamic ports — read them from `aspire describe`. A **404 on any
`/api/v4/dev-only/*` route means the API isn't running in Development** (never
the case under `aspire start`; always the case on published images).

## One call: loginable tenant with data

```bash
curl -s -X POST http://localhost:1610/api/v4/dev-only/admin/seed-tenant \
  -H "Content-Type: application/json" \
  -d '{"slug":"sleepy","displayName":"Sleepy","ownerUsername":"dev","sampleData":true,"sampleDataDays":7}'
```

Response: `{tenantId, subjectId, accessToken, refreshToken, expiresInSeconds,
url, loginLink, entriesSeeded, treatmentsSeeded}`.

- **`loginLink`** — navigate a browser/Playwright there: sets real session
  cookies and redirects to the dashboard. The primitive for UI verification and
  screenshots.
- **`accessToken`** — owner bearer token; membership grants `*` so reads and
  writes both work.
- A rejected slug (reserved/taken) returns 400 with a `suggestions` array.
- Cleanup: `DELETE /api/v4/dev-only/admin/tenants/{tenantId}` (list via `GET`).

## Calling tenant APIs

Tenants resolve from the Host header. Directly against :1610, spoof it:

```bash
curl -H "Authorization: Bearer $TOKEN" -H "X-Forwarded-Host: sleepy.nocturne.localhost" \
  http://localhost:1610/api/v4/status
```

Via the gateway, browsers resolve `*.localhost` themselves but curl may not:
add `--resolve sleepy.nocturne.localhost:1612:127.0.0.1` and `-k` (without
mkcert the dev cert doesn't name tenant subdomains).

## Session login without seeding

- Browser: `GET https://<slug>.nocturne.localhost:1612/api/v4/dev-only/auth/login?redirect=/`
- Headless: `POST http://localhost:1610/api/v4/dev-only/auth/login` with
  `{"tenant":"<slug>","username":null}` → token pair JSON. `username` picks a
  specific member; default is the first owner.

## Dev passkey fixture (real authenticator across DB wipes)

`GET /api/v4/dev-only/auth/passkey-fixture` exports registered passkeys
(synthetic dev-seed credentials excluded). Save to
`docs/seed/dev-identities.json` — committable; WebAuthn public keys aren't
secret. Development startup re-inserts fixture subjects+credentials, and
seed-tenant adds them as owners of every new tenant. Only valid while the base
domain (rp.id `nocturne.localhost`) is unchanged.

## Other one-call operations

- `POST /api/v4/dev-only/admin/tenants/{id}/seed-sample-data` `{"days":7}` —
  populate an existing tenant (oref-simulated entries/treatments through the
  real ingestion pipeline; data carries `DataSource: dev-sample`).
- `POST /api/v4/dev-only/admin/tenants/{id}/recovery-mode` `{"subjectId":null}` —
  strip a member's credentials (default: the owner) and keep/create a
  credentialed keeper so the tenant reports `recovery_mode_active`. Credential
  deletion is global — a fixture subject's passkeys return on next startup.
- Snapshots: `GET|POST /api/v4/dev-only/admin/snapshot`,
  `POST .../tenants/{id}/import-snapshot`; connectors: `POST .../sync-all`.

## Smoke test

`dotnet run scripts/dev-smoke.cs` proves the whole chain (seed → data → bearer
read+write → loginLink cookies → session via gateway → tenant UI); `--keep`
leaves the tenant up for inspection.
