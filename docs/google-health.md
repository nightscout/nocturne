# Google Health connector

Open **Settings -> Connectors & Apps -> Server Connectors -> Google Health**.
The connector requests read-only Google access and writes steps, heart rate,
body weight and sleep directly to Nocturne's existing health histories. It does
not introduce a separate health database or provide treatment recommendations.

## Storage and first installation

Google Health uses Nocturne's existing tenant-scoped health tables and encrypted
connector settings. It adds no database tables, schema changes or EF migrations.
There is no upgrade or repair path for earlier experimental Google Health builds.

During each import, reconciliation keeps only identifiers in the scoped writer's
memory. Native services upsert records using stable identifiers. Cleanup runs in
a transaction only after every page of that data type has been read and written
successfully. Empty results preserve existing data. A cancelled or interrupted
run discards its temporary identifiers without deleting history; its persisted
cursor is not advanced, and the same window can be retried safely after restart.
The identifier set and existing-record comparison are capped at 100,000 records
per type and window. Reaching that limit retries the same period in smaller
windows, down to one day, for both live catch-up and historical imports. A
one-day period that still exceeds the limit fails explicitly without advancing
the cursor.

## Google Cloud setup

1. Enable the Google Health API in your Google Cloud project.
2. Configure the OAuth consent screen and add test users while the app is in testing.
3. Create an OAuth client of type **Web application**.
4. Register the HTTPS callback URL displayed by Nocturne, ending in
   `/settings/connectors/google-health/callback`.
5. Enter the client ID and secret, choose an import start date, and sign in to Google.
6. Review the inventory, select supported data types, and choose **Save selection and import**.

### Use your own test connector

For testing, create the OAuth client in a Google Cloud project that you control;
do not reuse a maintainer's client. Add the Google account that will be tested as
a test user on the OAuth consent screen while the project is in testing mode.
The client ID, client secret and refresh token are then stored in the Nocturne
tenant's encrypted connector configuration. The browser completes the
authorization-code flow with PKCE, and the connector uses the resulting token to
read only the Google Health scopes granted by that account.

The connector calls Google Health from the Nocturne server and writes the
selected records to that server's existing tenant-scoped health histories. It
does not send the imported records to a shared Nocturne service, the maintainer's
Google project or Nightscout. When you run a private or test deployment, the
imported data therefore remains in that deployment and its database, subject to
the operators who administer that server. Use HTTPS and normal tenant access
controls, and revoke the Google grant in Google Account security if the test
deployment is discarded.

Secrets use Nocturne's encrypted connector configuration. OAuth authorization
uses state and PKCE validation. Google test-mode grants may expire after seven
days; production use can require Google verification. Only data made available
by Google and covered by the granted scopes can be imported.

## Imports and synchronization

- **Import data from** requests historical data; seven days is the default window,
  not a maximum. Explicit dates can extend back to 2000-01-01.
- **Save import settings** saves the selection without immediately importing.
  Clearing the selection pauses imports without removing the connection or data.
- **Save selection and import** queues a manual import. Leaving the page does not
  cancel the server-side operation. **Sync now** retries the current selection.
- Automatic synchronization normally runs every 15 minutes. Every managed run
  refreshes today's live window first, then imports one older calendar month
  towards the requested start date. This keeps current data flowing while a
  multi-year history is filled gradually. If a historical window is too large,
  times out, is rate limited, or cannot be written locally, the next attempt
  halves it automatically (for example 30, 15, 7, 3, then 1 day); once the
  smaller window succeeds, whole calendar months are used again. The cursor and
  retry size are persisted, so a restart resumes at the visible date instead of
  starting over. The page reports the oldest synchronized date under
  **Historical import progress**.
- After the live portion succeeds, its watermark resumes with a five-minute
  overlap. An older backfill never moves that watermark backwards. The explicit
  import start date is consumed only after the backfill actually reaches it.
- Each type is read page by page and written through its native Nocturne service.
  The maximum is 10,000 pages per type and operation; reaching the limit fails
  explicitly instead of reporting an incomplete history as complete.
- The page-by-page reader and reconciliation are the same bounded import
  path used by the connector integration; Google Health does not maintain a
  second, competing chunking implementation. Steps, weight and sleep pages are
  written before the next page is requested. Heart-rate pages are the exception:
  their samples are accumulated into UTC-minute buckets across the current
  window so averages remain deterministic across page boundaries, then the
  resulting readings are written after pagination completes. The 100,000 unique
  identifier cap also bounds those staged minute buckets; oversized windows are
  split into smaller periods before the accumulator can grow without bound.
- Google Health commonly returns near-continuous heart-rate samples. Reducing
  them to one stable average reading per UTC minute keeps native history and
  reports responsive while preserving an idempotent value for every minute.
- An empty result is not an error and is not converted into a zero measurement.
  Unsupported destinations are shown in the inventory but cannot be selected.
- Disconnecting keeps imported data. Deleting imported Google data is a separate,
  confirmed action scoped to this connector and the current tenant.

## Recovering a failed historical import

The **Import recovery** card links platform administrators to **Settings ->
Administration -> Reset Connector Cursors**. Select the tenant, enter the
earliest date that should be re-read, and start the background reset. Google
Health is a normal configured connector in that reset job, so the same bounded
page reader, native writes and idempotency keys are used. A reset does not delete
the existing health history; already imported rows are safely de-duplicated.
Use the job progress to see whether Google Health succeeded or failed, and cancel
the job before starting another reset if it is still running.

## UI copy and translations

The connector page and its shared source row use Wuchale PO catalogues, just like
the rest of the application. When copy is added or moved, run the Wuchale
extraction and keep every locale's `msgstr` non-empty; the lightweight
`google-health-translations.test.js` check compiles representative production
strings for every locale and verifies that `{0}` and `<0/>` placeholders are
preserved. Product names such as **Google Health**, **Google Cloud**, **Fitbit**,
**Nocturne**, and **eHbA1c** remain unchanged where the translation service
would otherwise split or translate the name.

Sleep uses the session's **end time**, as required by the
[Google Health filter contract](https://developers.google.com/health/filters).
The lower time boundary is inclusive and the upper boundary exclusive. A night
starting before the requested range is included when it ends inside that range,
with its stages intact. Local filtering and reconciliation use the same window.
Use **Refresh inventory** to rescan availability after a failed inventory request.

The settings page treats the server capability catalogue as authoritative. If a
preview is partial (for example while Google is still scanning a large history),
categories such as **Vitals** and **Body measurement** remain visible and the
missing rows are labelled **Not scanned**. This is different from **No
permission** or **Not yet supported**: it means the row can be selected, but the
latest inventory response did not include a count yet.

Completed types are reconciled within their requested windows. Native source
identifiers support repeat imports and updates. Reconciliation is scoped to Google
Health and the current tenant; an empty type result does not delete its stored
history. A failure on a later page or type can leave earlier batches saved, while
the import watermark remains unchanged. Retrying is supported; an import is not
one database transaction covering all types.

## Errors and progress

While the connector page is open, import progress refreshes every two seconds
without depending on websocket delivery. The percentage represents data-type
stages, not record-count completion or estimated time remaining. Errors expose a
technical code and, where applicable, HTTP status. Use those with the API-server
log; do not share credentials or health data.

Manual and scheduled imports share the connector's tenant-wide run guard. If a
scheduled run already owns that slot, **Sync now** reports the request as
already running and continues polling the server status instead of presenting a
provider failure. A second import is never started just to satisfy the button
press.

Local storage failures are reported as `internal_sync_native_write:<data-type>`
with a stage such as `native_write` or `native_reconciliation_complete`. This
distinguishes a database/reconciliation problem from a Google permission or
OAuth problem and causes the next historical attempt to use a smaller window.

The inventory preview uses the same guard but waits only briefly for an active
import. If the slot remains occupied, the page stops the spinner and explains
that the inventory can be refreshed after the import completes. This prevents a
long-running historical import from leaving the settings page in an indefinite
“Scanning inventory” state.

The heart-rate actogram is a presentation view, not the source of truth. The
report query averages readings into one UTC-minute point in PostgreSQL before
serializing the response. Raw heart-rate rows remain available to the health
history and day-level views, so this optimisation reduces browser payload and
chart work without discarding measurements. This directly addresses the high
volume report case tracked in [Nightscout issue #1359](https://github.com/nightscout/nocturne/issues/1359).

## Verification limits

Automated tests cover authorization, filters, pagination, native writes,
reconciliation, repeated imports, progress and failure handling using synthetic
data. They do not authorize a live Google account or prove availability of every
device's data. Real consent and account-specific imports require runtime testing.

## API replicas and import safety

API replicas sharing the same PostgreSQL database and connector encryption key
can handle different requests in one Google Health connection flow. The PKCE
verifier and expiring OAuth state use encrypted connector storage. PostgreSQL
session locks serialize token refresh, imports, settings changes, disconnect,
and purge per tenant. Each replica processes up to four manual imports concurrently
for different tenants while holding each worker claim until the import finishes.

Manual requests and progress use the existing `sync_cursors` column. Workers
receive PostgreSQL notifications when requests are durably queued; one worker
owns each request. Tenant-pinned recovery scans run at startup, on listener
reconnection, and every 15 minutes to recover missed notifications. An
interrupted worker releases its database session lock, allowing another replica
to retry the request from the unchanged cursor. Live and historical resume state
is saved without treating it as a user configuration change.

A malformed measurement or sleep session fails that type's import before
destructive reconciliation. Earlier successfully written pages remain available,
and the cursor does not advance. Retrying a complete response preserves stable
native identifiers. Explicit purge permanently deletes only the current tenant's
Google Health records, including old tombstones, and clears all resume state
before releasing the account binding. This permits a replacement account to
import readings with overlapping identifiers.
