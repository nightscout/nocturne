The Nightscout API, versions 1, 2 and 3, as Nocturne serves it. The paths and the responses match the original JavaScript implementation, so an app that works with Nightscout works with Nocturne.

For a new app, use the Nocturne API, version 4. Select it at the top of the sidebar.

## Which version

| Version | Use it when |
| --- | --- |
| V1 | The app is an existing uploader or follower. Most of them use v1. |
| V2 | The app needs the authorization, properties or ddata operations. |
| V3 | The app wants the same operations on each collection, and conditional requests. |

## Base URL

Each tenant has its own host name: `https://<tenant>.<base-domain>`. An instance that has only one tenant also answers on the base domain. Use that host as the Nightscout URL in the app.

## Authentication

| Credential | How to send it |
| --- | --- |
| API secret | The SHA-1 hash of the secret, in the `api-secret` header. |
| Token | The `token` query parameter. |
| Nocturne API token | In the `api-secret` header, in the `token` query parameter, or as `Authorization: Bearer`. |

A Nocturne API token has scopes, so it can give an app less than full access. A person creates one in the web app, under **Settings**, then **Connectors**, then **API Tokens**.

## Quick start

```bash
curl "https://<tenant>.<base-domain>/api/v1/entries/current" \
  -H "api-secret: <sha1-of-secret>"
```

The response is an array with the latest entry, or an empty array when there is no data.

## How the data is stored

Nocturne has no tables for legacy documents. A write to this API is stored as version 4 records, and a read is built from them. Both APIs show the same data.

The **Data Model** pages show how each collection maps to version 4 records: entries, treatments, device status and profile.

## Differences from Nightscout

- A response leaves out properties that are `null`, zero or `false`, as Nightscout does.
- A token can be limited to the last 24 hours of data. Older records are then absent from a read.
