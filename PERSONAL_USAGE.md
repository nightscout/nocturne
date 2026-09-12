# Google Health connector

Google Health is available under **Settings -> Connectors & Apps -> Server Connectors**.
It uses read-only OAuth access and stores supported measurements in Nocturne's existing
step, heart-rate, body-weight, and sleep records.

## Google Cloud setup

1. Enable the Google Health API in a Google Cloud project.
2. Configure the OAuth consent screen and add test users while the app is in testing.
3. Create an OAuth client of type **Web application**.
4. Register the callback URL shown by Nocturne. It must use HTTPS and end in
  `/settings/connectors/google-health/callback`.
5. Enter the client ID and client secret, choose the history start date, and sign in.
6. Review the detected data types before confirming the first import.

The client secret and refresh token are encrypted at rest. Google test-mode grants may
expire after seven days. Production use can require additional Google verification.

## Import behaviour

- Choose **Import data from** to retrieve older shared data without reconnecting.
  Seven days is the default window, not a maximum. Explicit start dates can go back
  to 2000-01-01. The API follows up to 10,000 pages per type/operation and reports an
  error if that safety limit is exceeded; it never silently reports a complete import.
- Use **Save import settings** to change the date or uncheck problematic types without
  running an immediate import. Clear all types and save to pause imports while keeping
  the connection and previously imported data. **Save selection and import** also
  starts a manual import.
- Steps, heart rate, weight, and sleep are written to their native Nocturne stores.
- Known types without a Nocturne destination are shown but are not imported.
- Automatic synchronization runs approximately every 15 minutes and reconciles the
  configured history range.
- Manual imports run in the server background. The connector page remains available
  and reports the current phase, data type, completed types, and Google pages read.
  Leaving the page does not cancel the server-side import.
- An empty result is not a connector error and a missing measurement is not stored as
  zero. Real permission, provider and parsing failures remain visible.

Use **Sync now** for a manual retry. Errors include a stable technical code; correlate
that code and the attempt time with the API server log when troubleshooting.

Sleep scans and imports select sessions by their end time, as required by the
[Google Health filter contract](https://developers.google.com/health/filters).
The lower boundary is inclusive and the upper boundary exclusive. A night starting
before the selected range is included if it ends within that range; its stages
remain intact. Local filtering and reconciliation use the same end-time boundaries.
After updating from a release that showed `invalid_google_filter` for sleep, use
**Refresh inventory**, select **Sleep sessions and stages**, then **Save selection
and import**. Reconnecting Google or deleting existing data is not required for
this filter correction.

## Import diagnostics

Open **Settings -> Connectors & Apps -> Google Health -> Import diagnostics**.
The page refreshes server progress every two seconds without depending on a live
websocket connection. Progress is based on completed data types, not a prediction
of remaining time. The latest written record is not a guarantee that all earlier
records or other data types have been imported.

The diagnostic panel shows the run ID, source commit, outcome, page and native
write stages, batch counts and durations, requested date range, provider status
and reason, and exception types and stack methods. PostgreSQL failures include
SQLSTATE when available. A failed import can leave earlier batches stored; only
a successful completed import advances the resume watermark.

Choose a run and use **Download diagnostics** to share its JSON. Only the latest
run and up to four earlier runs are retained, for up to 24 hours in server memory.
Download before restarting the app: a restart clears this history. Exports omit
tokens, client secrets, raw health records and arbitrary exception messages, but
include import dates and counts; treat them as private support information.

When reporting a problem, include the installed HA package version and the JSON
for the failed run. Do not reconnect or delete imported data merely to collect a log.

## Year overview color focus

The year overview remembers color settings per metric, user and tenant in the current
browser. TDD, bolus, basal, carbohydrates and Time in Range use two adjustable bounds.
Average glucose uses four boundaries on the continuous color bar, with matching numeric
inputs in mg/dL or mmol/L. Very-low averages are black so they remain distinct from the
low range. Reset restores the default glucose color scale. These display settings do not
change glucose targets, Time in Range calculations or measured values.
