# Google Health connector

Open **Settings -> Connectors & Apps -> Server Connectors -> Google Health**.
The connector requests read-only Google access and writes steps, heart rate,
body weight and sleep directly to Nocturne's existing health histories. It does
not introduce a separate health database or provide treatment recommendations.

## Google Cloud setup

1. Enable the Google Health API in your Google Cloud project.
2. Configure the OAuth consent screen and add test users while the app is in testing.
3. Create an OAuth client of type **Web application**.
4. Register the HTTPS callback URL displayed by Nocturne, ending in
   `/settings/connectors/google-health/callback`.
5. Enter the client ID and secret, choose an import start date, and sign in to Google.
6. Review the inventory, select supported data types, and choose **Save selection and import**.

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
- Automatic synchronization normally runs every 15 minutes. After a successful
  import it resumes from the stored watermark with a five-minute overlap; without
  a watermark it uses the configured history window. An older backfill never moves
  the watermark backwards. The initial import date is consumed after its successful
  manual import.
- Each type is read page by page and written through its native Nocturne service.
  The maximum is 10,000 pages per type and operation; reaching the limit fails
  explicitly instead of reporting an incomplete history as complete.
- An empty result is not an error and is not converted into a zero measurement.
  Unsupported destinations are shown in the inventory but cannot be selected.
- Disconnecting keeps imported data. Deleting imported Google data is a separate,
  confirmed action scoped to this connector and the current tenant.

Sleep uses the session's **end time**, as required by the
[Google Health filter contract](https://developers.google.com/health/filters).
The lower time boundary is inclusive and the upper boundary exclusive. A night
starting before the requested range is included when it ends inside that range,
with its stages intact. Local filtering and reconciliation use the same window.
Use **Refresh inventory** to rescan availability after a failed inventory request.

Completed types are reconciled within their requested windows. Native source
identifiers support repeat imports and updates. Reconciliation is scoped to Google
Health and the current tenant; an empty type result does not delete its stored
history. A failure on a later page or type can leave earlier batches saved, while
the import watermark remains unchanged. Retrying is supported; an import is not
one database transaction covering all types.

## Diagnostics

Open **Import diagnostics** on the connector page. Status refreshes every two
seconds without depending on websocket delivery. The percentage represents
data-type stages, not record-count completion or estimated time remaining.

The run log includes its ID, source commit when available, requested ranges, pages,
native write batches, durations, processed counts, latest written record time,
reconciliation and completion status. Errors include safe technical codes,
provider status/reason, exception types and stack methods; database errors can
include SQLSTATE. Counts include updates, not just newly inserted rows. A recent
record timestamp alone does not prove the whole import completed.

**Download diagnostics** exports the selected run as JSON. The current run and up
to four prior runs are retained in bounded server memory for up to 24 hours, with
at most 256 events per run. Restart or cache eviction removes them. Exports omit
tokens, client secrets, measurement values, raw provider responses and arbitrary
exception messages. Dates and counts remain sensitive: review an export before
sharing it. Include the Nocturne version and failed run when reporting a problem;
reconnecting or deleting data is not necessary to collect diagnostics.

## Verification limits

Automated tests cover authorization, filters, pagination, native writes,
reconciliation, repeated imports, progress and failure handling using synthetic
data. They do not authorize a live Google account or prove availability of every
device's data. Real consent and account-specific imports require runtime testing.