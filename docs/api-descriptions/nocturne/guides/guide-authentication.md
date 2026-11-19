---
displayName: Authentication
standalone: true
---
How a client proves who it is, and how to choose between the methods.

## Choose a method

| You are building | Use | Why |
| --- | --- | --- |
| A script, a home automation, or a tool for your own data | An API token | One long-lived token. No sign-in flow. |
| An app that other people sign in to | OAuth 2.0 Authorization Code with PKCE | Each person approves the scopes. The app never sees their sign-in. |
| An app on a device with no browser or keyboard | OAuth 2.0 Device Authorization Grant | The person approves on a phone or computer. |
| An existing Nightscout uploader or follower | The API secret or a Nightscout token | No change to the app. See **Coming from Nightscout**. |

Each method ends with a credential that the client sends on every request:

```http
Authorization: Bearer <token>
```

## API tokens

An API token starts with `noc_`. It belongs to one tenant and works only on the host of that tenant.

To create one, sign in to the web app and open **Settings**, then **Connectors & apps**. Select **Add a connection**, then **Create API token**. Select the scopes, and copy the token. It is shown once.

```callout warning
A token made in the web app does not expire. Keep it as safe as a password, and revoke it when it is no longer needed.
```

```bash
curl https://<tenant>.<base-domain>/api/v4/glucose/sensor?limit=1 \
  -H "Authorization: Bearer noc_..."
```

- A token cannot have more scopes than the person who creates it.
- A token can be limited to the last 24 hours of data. See **Sharing and history limits**.
- Only a signed-in person can create a token. `POST /api/auth/direct-grants` refuses a request that is authenticated with a token.

## OAuth 2.0

All clients are public: there is no client secret. PKCE with `S256` is required.

| Step | Operation |
| --- | --- |
| Find the endpoints and the scopes | `GET /.well-known/oauth-authorization-server` |
| Register the client, once for each tenant host | `POST /api/oauth/register` |
| Send the person to approve | `GET /api/oauth/authorize` |
| Exchange the code, and refresh | `POST /api/oauth/token` |
| Start a device grant | `POST /api/oauth/device` |

Each refresh returns a new refresh token. Store it and discard the old one. If an old refresh token is used again later, Nocturne revokes the whole grant, and the person must approve the app again.

The step-by-step guides, with code, are on the documentation site:

- [App authorization with PKCE](https://getnocturne.dev/docs/connecting-apps)
- [Mobile and device flow](https://getnocturne.dev/docs/connecting-apps/device-flow)
- [API keys for AID apps](https://getnocturne.dev/docs/connecting-apps/aid-app-keys)

## Scopes

A scope names a category of data and a level: `glucose.read`, `treatments.readwrite`.

- `read` allows reads. `readwrite` allows reads, writes and deletes, and includes `read`.
- `health.read` is read access to glucose, treatments, devices, therapy settings, heart rate, step counts, sleep and food. `health.readwrite` is write access to the same set. Neither includes alerts or reports.
- `*` is full access. Ask for the smallest set of scopes that the client needs.

```scopes
```

The full list is `scopes_supported` in `GET /.well-known/oauth-authorization-server`.

The permissions of a request are the scopes of the credential, limited by the role of the person in the tenant. `GET /api/v4/me/permissions` returns the result.

## Errors

| Status | Meaning |
| --- | --- |
| `401` | No credential, or the credential is not valid for this tenant. |
| `403` | The credential is valid but does not have the scope. |
| `429` | Too many requests. The sign-in and token endpoints are rate limited. Wait and try again. |
| `503` | The tenant is not set up yet (`"error": "setup_required"`) or is in recovery mode (`"error": "recovery_mode_active"`). |

The OAuth endpoints return errors as `{ "error": "...", "error_description": "..." }`.

## Try it with the demo

`https://demo.nocturne.run` is a public demo with sample data. This reference sends requests to it with a demo token, so **Send** works with no setup.
