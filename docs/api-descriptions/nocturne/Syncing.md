---
standalone: true
---
How Nocturne ingests data from connectors, how it resumes where it left off, and why re-syncing the same data never creates duplicates.

## How connectors sync

Each connector (Nightscout, Dexcom, Glooko, …) runs as a per-tenant background service. The poller wakes on the tenant's configured interval (and, for Nightscout, immediately on real-time socket events from the upstream instance) and pulls any new data since it last ran. There is no stored cursor — Nocturne derives the resume point from the **latest record already stored** for each data type.

## Per-type catch-up cursors

A connection carries several independent streams — glucose, treatments (boluses/carbs/manual BG), device status, activity — and each resumes from **its own** latest record, not a single shared cursor. This matters because one stream can legitimately lag another: a closed-loop user produces a glucose reading every 5 minutes but may bolus only a few times a day. Tying every stream to the glucose cursor would strand the slower streams permanently once glucose caught up.

- On a normal background sync each enabled type fetches from `latest_stored_record − 5min` (a small overlap absorbs clock drift).
- When a type has **no** data yet, it performs an initial backfill over a bounded window (currently 6 months) so a new connection fills in recent history.
- Profiles and food are small and fetched in full on every sync rather than windowed.

## Resilient ingestion

Real-world uploader payloads are messy: numeric fields arrive as JSON strings (`"insulin":"1.5"`), booleans as `"true"`/`1`, direction as an integer. Connector deserialization tolerates these variants so one oddly-typed record can't throw and abort an entire page — which would otherwise drop a whole stream's backfill while a sibling stream (e.g. glucose) succeeded.

## Re-syncing and cursor reset

Two endpoints force data in on demand, both reusing the stored connector credentials:

- **Manual sync** — `POST /api/v4/services/connectors/{id}/sync` with a `SyncRequest`. Supplying `to` switches the connector into *explicit-range* mode: it fetches exactly `from`…`to` for the requested `dataTypes`, bypassing the catch-up cursors. Use this to backfill a specific window of a specific type (e.g. treatments only) without re-pulling everything.
- **Cursor reset (single tenant)** — `POST /api/v4/services/connectors/{id}/reset-cursor`. Re-pulls history (all types, or a subset) from the beginning, or from an optional `from`. Use this after fixing a connector bug to push corrected data to a tenant. This runs **synchronously**: a full-history re-pull (glucose can be tens of thousands of records) is a multi-minute request, so it may approach or exceed reverse-proxy / browser timeouts on data-heavy tenants — scope it with `from` / `dataTypes` when you can.
- **Cursor reset (cross-tenant, platform admin)** — `POST /api/v4/admin/connectors/{tenantId}/reset-cursors`. Resets *every* connector configured on a target tenant. Because the fan-out can run for minutes, this is **asynchronous**: it returns `202 Accepted` with a job id immediately, then you poll `GET /api/v4/admin/connectors/jobs/{jobId}` for per-connector progress (and `POST …/jobs/{jobId}/cancel` to stop it). The job outlives the request that started it, so it is not affected by proxy timeouts.

All are safe to re-run because of idempotency, below.

## Idempotency

How Nocturne guarantees that writing the same record twice never creates a duplicate — and why there are two separate keys for it.

Diabetes data is written by two very different kinds of client, and **both retry**. A phone app may resend a `POST` after a flaky network; a background connector re-fetches overlapping time windows on every sync. To make writes safe to repeat, every V4 record carries an idempotency key and the database enforces uniqueness on it. Which key is used depends on **who is writing**.

### Two channels

| Channel | Key | Who sets it | Why |
| --- | --- | --- | --- |
| **API clients** (apps, uploaders) | `syncIdentifier` | The client | The client owns a stable per-record ID and sends it with every write. |
| **Connectors** (Glooko, Dexcom, …) | `legacyId` | Nocturne | The upstream source exposes no stable per-record ID, so a deterministic hash of the record's content is derived. |

A given record normally carries **one** of these keys, not both.

#### `syncIdentifier` — client-owned

Clients that integrate over the REST API supply their own stable identifier on each write, scoped by `dataSource`. On create, if a record with the same `(dataSource, syncIdentifier)` already exists, the endpoint returns the existing record unchanged — an idempotent upsert — instead of inserting a duplicate. Enforced by a unique index on `(tenant_id, data_source, sync_identifier)`.

Use this when **you** control the writer and can guarantee a stable ID that survives retries.

#### `legacyId` — content-derived

Connectors pull from upstream platforms that rarely expose a stable per-record ID — Glooko's graph series, for example, are just timestamped points. Rather than a source ID, the connector derives a deterministic fingerprint from the record's own immutable content (event type + timestamp + salient fields). Re-syncing the same window regenerates an identical `legacyId`, so the second write is recognised as a duplicate. Enforced by a unique index on `(tenant_id, legacy_id)`.

`legacyId` also doubles as migration traceability: it ties a V4 record back to the original Nightscout/Mongo document it was decomposed from.

### Guidance for writers

- **Building a client or uploader?** Always send `dataSource` + `syncIdentifier`, and reuse the same `syncIdentifier` when retrying the same logical record.
- **Building a connector?** Derive a stable `legacyId` from immutable content. The repository bulk-create paths dedupe on it automatically.

> **Footgun:** The two keys are not interchangeable per record type. A writer that populates `legacyId` but is backed by a repository that only dedupes on `syncIdentifier` (or vice-versa) will insert duplicates — or hit a unique-constraint violation on the next retry. When adding a new record type, make sure its repository honours **both** keys.
