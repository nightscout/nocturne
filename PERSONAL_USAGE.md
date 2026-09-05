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

- Nocturne retrieves every available result page from the selected start date.
- Steps, heart rate, weight, and sleep are written to their native Nocturne stores.
- Known types without a Nocturne destination are shown but are not imported.
- Automatic synchronization runs approximately every 15 minutes and reconciles the
  configured history range.
- A missing measurement is not stored as zero.

Use **Sync now** for a manual retry. Errors include a stable technical code; correlate
that code and the attempt time with the API server log when troubleshooting.
