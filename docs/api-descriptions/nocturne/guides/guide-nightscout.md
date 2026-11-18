---
displayName: Coming from Nightscout
standalone: true
---
What stays the same for an app that works with Nightscout, and what version 4 offers when you are ready to change.

## Your app works with no change

Nocturne serves the Nightscout v1, v2 and v3 APIs with the same paths and the same responses as the original. Point the app at the host of the tenant and keep the credential it has:

| Credential | How to send it |
| --- | --- |
| API secret | The SHA-1 hash of the secret, in the `api-secret` header. |
| Nightscout token | The `token` query parameter. |

Both also work on version 4 paths. The legacy operations are in the **Nightscout API** document. Select it at the top of the sidebar.

## What is different behind the API

Nocturne has no tables for legacy documents. A legacy write is stored as version 4 records, and a legacy read is built from them. So the two APIs always show the same data: a bolus that is written as a v1 treatment can be read as a v4 bolus, and the reverse.

| Legacy collection | Version 4 records |
| --- | --- |
| `entries` | Sensor glucose, meter glucose, calibration |
| `treatments` | Bolus, carb intake, temp basal, BG check, note, device event, bolus calculation, profile activation, state span |
| `devicestatus` | APS snapshot, pump snapshot, uploader snapshot |
| `profile` | Therapy settings and the basal, carb ratio, sensitivity and target range schedules |

The **Data Model** pages of the Nightscout document show each mapping as a diagram.

## What changes in version 4

| Topic | Nightscout | Version 4 |
| --- | --- | --- |
| Shape | One document for many kinds of event. | One record type for each kind of event. |
| Time | `date` in milliseconds, `created_at`, `dateString`. | `timestamp` in ISO 8601, with `mills` as a read-only copy. |
| Glucose | `sgv` or `mbg`, with the unit implied. | `mgdl` and `mmol` on each record. |
| Lists | `count` and `find[...]` query syntax. | `from`, `to`, `limit`, `offset`, `sort`. |
| Duplicates | Depends on the uploader. | `dataSource` and `syncIdentifier`. For most record types, a retry updates the record. |
| Credentials | One API secret with full access. | Tokens with scopes. |
| Errors | `{ status, message }`. | Problem details (RFC 9457). |
| Real time | Socket.IO. | SignalR hubs, where the gateway of the instance routes them. A bridge serves Socket.IO clients. |

## Move in steps

An app does not need to change everything at once.

1. Keep the uploads on the legacy API. Read from version 4 where it helps, for example `GET /api/v4/ChartData/dashboard` for a chart.
2. Replace the API secret with an API token that has only the scopes the app needs. An API token also works in the `api-secret` header, so this is a change of configuration and not of code.
3. Move the writes to version 4, with a `dataSource` and a `syncIdentifier` on each record.

## Import existing data

A person moves their history in the web app, from a Nightscout MongoDB database or from a running Nightscout site. The operations are under **Tenant Admin**.
