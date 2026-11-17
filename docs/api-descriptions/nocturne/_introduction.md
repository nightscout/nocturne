Nocturne stores and serves diabetes data: glucose, insulin, carbs, device status and the therapy settings that explain them. This reference covers the Nocturne API, version 4. The legacy Nightscout API (v1, v2 and v3) is a second document in this reference. Select it at the top of the sidebar.

## Start here

| If you want to | Read |
| --- | --- |
| Make a first request | **Quick start**, below |
| Choose between an API token and OAuth | **Authentication**, in Guides |
| Know which record holds what | **Data model**, in Guides |
| Upload data and not create duplicates | **Syncing**, in Guides |
| Get new readings as they arrive | **Real-time updates**, in Guides |
| Move an app from Nightscout | **Coming from Nightscout**, in Guides |
| Read or change the code | **Codebase tour**, in Architecture |

## Quick start

### 1. Find your base URL

Each tenant has its own host name: `https://<tenant>.<base-domain>`. An instance that has only one tenant also answers on the base domain itself. All paths in this reference are relative to that host.

### 2. Create an API token

Sign in to the web app. Open **Settings**, then **Connectors & apps**. Select **Add a connection**, then **Create API token**, and create a token with the `glucose.read` scope. The token starts with `noc_` and is shown once.

### 3. Read the latest glucose value

```bash
curl "https://<tenant>.<base-domain>/api/v4/glucose/sensor?limit=1" \
  -H "Authorization: Bearer noc_..."
```

The response has the readings in `data`, newest first. The operation in this reference shows each field.

### 4. Try it with no setup

On the demo, **Send** in this reference works with no token of your own. The demo has sample data and is reset on a schedule.

## Base URL and tenants

A tenant is the data of one person. Nocturne finds the tenant from the host name of the request, not from a header or a path.

| Host | What it reaches |
| --- | --- |
| `<tenant>.<base-domain>` | One tenant. |
| `<base-domain>` | The only tenant, on an instance that has one. On an instance with more, only the few operations that need no tenant. |
| `<token>.share.<base-domain>` | The public share link of a tenant. Read-only, with no sign-in. |

A host that matches no tenant gets `404`. A credential belongs to one tenant, and a request to another tenant with that credential gets `401`.

## Authentication

Send a bearer token on each request:

```http
Authorization: Bearer <token>
```

The token is an API token (`noc_...`) or an OAuth 2.0 access token. The **Authentication** guide shows how to get each one, and lists the scopes.

| Status | Meaning |
| --- | --- |
| `401` | No credential, or the credential is not valid for this tenant. |
| `403` | The credential does not have the scope that the operation needs. |

## Conventions

### JSON

- Property names are camelCase.
- Enum values are strings. The schema of each enum lists its values.
- A property with no value is `null`.

### Time

- `timestamp` is the time of a record, in UTC, as an ISO 8601 string. Send this on a create.
- `mills` is the same time as Unix milliseconds. It is read-only.
- `utcOffset` is the offset of the local time of the person, in minutes. Use it to display local time.

### Units

- Glucose is stored in mg/dL. `mgdl` is the stored value, and `mmol` is computed from it.
- A BG check keeps the value and the unit that were entered, in `glucose` and `units`, and also returns `mgdl` and `mmol`.
- Insulin is in units. A basal rate is in units for each hour.
- Carbs, fat and protein are in grams.

### Lists

A list operation accepts these query parameters:

| Parameter | Default | Meaning |
| --- | --- | --- |
| `from`, `to` | none | The time range, inclusive. A value with no offset is read as UTC. The range can be at most 366 days. |
| `limit` | `100` | The number of records to return. |
| `offset` | `0` | The number of records to skip. |
| `sort` | `timestamp_desc` | `timestamp_desc` for newest first, or `timestamp_asc` for oldest first. |
| `device`, `source` | none | Return only the records of one device or one data source. |
| `includeTotal` | `false` | Add the total number of records in the time range to the response. |

The response has the records in `data` and the page in `pagination`. `pagination.total` is present when you ask for it with `includeTotal=true`. It counts the records in the time range and ignores the `device` and `source` filters.

### Identifiers and duplicates

- `id` is a UUID that Nocturne assigns.
- `dataSource` and `syncIdentifier` together are the key of a record in the system that wrote it. Send both on each create. A create with a pair that already exists updates that record and does not add a second one, so a retry is safe. Calibrations, meter glucose and bolus calculations have no `syncIdentifier`: a retried create of one of these adds a second record.
- `correlationId` links records that describe one event, such as a bolus and the carbs it covers.

The **Syncing** guide has the details.

### Bulk create

`POST .../bulk` accepts an array of at most 1000 records. If one record is not valid, the request fails with `400` and nothing is saved.

### Deleted records

A delete hides a record and keeps it for a time (30 days by default), so a mistake can be undone. After that the record is removed.

| Operation | What it does |
| --- | --- |
| `GET .../deleted` | Lists the deleted records, newest first. |
| `POST .../{id}/restore` | Restores one record. |
| `POST .../restore` | Restores the records in an array of ids. |

A restore returns `409` when a newer record holds the same `dataSource` and `syncIdentifier`. A create with the key of a record that a person deleted also returns `409`: restore the record, or use a new `syncIdentifier`. A bulk create leaves such a record out and saves the rest.

### Errors

An error from an operation has a problem details body (RFC 9457). Its `detail` says what is wrong, and some errors also carry a stable `code`. The OAuth operations, and a `503`, have a body with an `error` code in place of problem details.

| Status | Meaning |
| --- | --- |
| `400` | The request is not valid. `detail` says why. |
| `401`, `403` | See **Authentication**, above. |
| `404` | No record has that id, or the host matches no tenant. |
| `409` | The record conflicts with another one. See **Deleted records**. |
| `429` | Too many requests. Wait and try again. |
| `503` | The tenant is not set up yet, or is in recovery mode. The `error` in the body says which. |

### Caching

A response says in `Cache-Control` if the client can keep it. No response can be kept by a shared cache. Version 4 does not use `ETag` or `Last-Modified`.

## Where the data is

| Data | Group | Path starts with |
| --- | --- | --- |
| Glucose readings | Glucose | `/api/v4/glucose`, `/api/v4/observations/bg-checks` |
| Boluses, carbs, notes | Treatments | `/api/v4/insulin`, `/api/v4/nutrition`, `/api/v4/observations/notes` |
| Pump and loop status | Devices | `/api/v4/device-status` |
| Site and sensor changes | Devices | `/api/v4/observations/device-events` |
| Pump modes, overrides, temporary targets | Treatments | `/api/v4/state-spans` |
| Therapy settings and schedules | Profiles | `/api/v4/profile` |
| Chart data and statistics | Analytics | `/api/v4/ChartData`, `/api/v4/Statistics` |
| Alerts | Monitoring | `/api/v4/alerts`, `/api/v4/alert-rules` |
| Heart rate, steps, weight, sleep | Health | `/api/v4/HeartRate`, `/api/v4/StepCount`, `/api/v4/body-weight`, `/api/v4/sleep` |

To draw a glucose chart, call `GET /api/v4/ChartData/dashboard`. It returns the readings and everything drawn with them in one response.

## SDKs

Generated clients are published for TypeScript, C#, Python, Java, Kotlin, Swift and Rust. Each one has a typed method for each operation in this document. The install commands are at the top of this page.

## Help

- Source code and issues: [github.com/nightscout/nocturne](https://github.com/nightscout/nocturne)
- Guides to install and run Nocturne: [getnocturne.dev/docs](https://getnocturne.dev/docs)
