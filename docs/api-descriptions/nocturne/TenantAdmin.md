---
displayName: Tenant Admin
---
Administrative operations for tenant data management, migration, and maintenance.

- **Migration** — Import data from a Nightscout MongoDB instance.
- **Nightscout Transition** — Aggregated migration progress and write-compatibility status for the migration dashboard.
- **Backfill** — Decompose all existing legacy entries and treatments into V4 granular tables.
- **Deduplication** — Run and monitor deduplication jobs across data tables.
- **Discrepancy** — Compatibility analysis between legacy and V4 data representations.
- **Compression Low** — Detect and review compression low artefacts in CGM data.
- **Processing** — Async processing job status tracking.
- **OIDC Provider Admin** — Manage OIDC identity provider configurations for the tenant.
- **Subject Admin** — Manage user/subject records within the tenant.

> **Footgun:** The Backfill endpoint decomposes *all* legacy data. On large datasets this is a long-running operation — it runs asynchronously and progress can be tracked via the Processing endpoints.
