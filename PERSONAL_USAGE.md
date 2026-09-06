# Google Health connector

Google Health is available under **Settings -> Connectors & Apps -> Server Connectors**.
It uses read-only OAuth access and stores supported measurements in Nocturne's existing
step, heart-rate, body-weight, and sleep records.

## Google Cloud setup

1. Enable the Google Health API in a Google Cloud project.
2. Configure the OAuth consent screen and add test users while the app is in testing.
3. Create an OAuth client of type **Web application**.
4. Register the callback URL shown by Nocturne. It must use HTTPS and end in
   `/personal/google/callback`.
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
- An empty result is not a connector error and a missing measurement is not stored as
  zero. Real permission, provider and parsing failures remain visible.

Use **Sync now** for a manual retry. Errors include a stable technical code; correlate
that code and the attempt time with the API server log when troubleshooting.

## Year overview color focus

The year overview remembers color settings per metric, user and tenant in the current
browser. TDD, bolus, basal, carbohydrates and Time in Range use two adjustable bounds.
Average glucose uses four boundaries on the continuous color bar, with matching numeric
inputs in mg/dL or mmol/L. Reset restores the default glucose color scale. These display
settings do not change glucose targets, Time in Range calculations or measured values.
