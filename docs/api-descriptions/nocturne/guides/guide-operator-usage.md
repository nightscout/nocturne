---
displayName: Tenant usage for operators
standalone: true
---
How the operator of an instance reads the usage of each tenant: the day that data last arrived, the day that people last signed in, the health of each connector, and which features a tenant uses.

Use it to find tenants that never got data flowing, sites that went quiet, connectors that fail, and features that nobody uses. Any operator can use it, on any instance with more than one tenant.

## What it does not contain

The summary contains dates, counts, settings and coarse categories only. It never contains:

- A time of day. Each date is a UTC calendar date (`YYYY-MM-DD`). Only `generatedAt` has a time, and that is the time of the read.
- Glucose, treatment or other health values. `lastReadingOn` is the date of the newest reading, not its value.
- The text of a connector error. Nocturne reads the stored message on the server, puts it in one category, and sends only the category.
- Client addresses or user agents.
- Names, user names or email addresses of members.
- Labels that members give to devices, guest links, chat links or apps.

Nocturne does not record any metric for this endpoint, and it does not keep a copy of the summary. The values come from data that each tenant already holds.

## Read the summary

```http
GET /api/v4/admin/tenants/usage-summary?limit=100&offset=0
X-Instance-Key: <SHA-256 of the instance key, in hex>
X-Instance-Service: <your service name>
```

Only the instance key can read the summary. A person, including a platform administrator, gets `403 Forbidden`.

| Parameter | Rules |
| --- | --- |
| `tenantId` | Optional. Returns only this tenant. An unknown tenant gives an empty list. |
| `offset` | Optional. The number of tenants to skip. The default is `0`. |
| `limit` | Optional. From `1` to `500`. The default is `100`. |

The tenants are in the order of their id. To read all of them, increase `offset` by `limit` until a page has fewer than `limit` tenants. Demo tenants are not in the summary, because their data is synthetic.

## The response

The response has `generatedAt`, the UTC time of the read, and `tenants`, one entry for each tenant on the page. The schema of the operation gives the full shape.

All dates are UTC calendar dates. A date is `null` when the event did not occur.

| Field | Meaning |
| --- | --- |
| `lastReadingOn` | The date of the newest glucose reading. |
| `onboardingCompletedOn` | The date that the owner completed or skipped the first setup. |
| `setupHubEnrolledOn` | The date that the tenant started to use the setup hub. |
| `lastOwnerLoginOn` | The date of the newest sign-in of a person who owns the tenant. A sign-in is for an account, not for one tenant. |
| `lastMemberActivityOn` | The date of the newest request to the tenant from a member. |
| `memberCount` | The people who have access now, owners included. System accounts, the demo account, accounts that are not active and memberships that ended are not counted. |
| `membersActiveLast7Days` | The members in `memberCount` who made a request in the last 7 days. |
| `connectors` | One entry for each connector that the tenant set up: `type`, `enabled`, `status`, `lastSuccessfulSyncOn`, `lastErrorOn` and `errorCategory`. `type` is the connector id, such as `dexcom`, which tells the brand of the person's device. |
| `alertRuleCount` | The alert rules, on or off. |
| `alertChannelTypes` | The different channel types that the alert rules use. |
| `savedReportCount` | The custom reports that are not deleted. |
| `trackerCount`, `clockFaceCount` | The trackers and the clock faces. |
| `guestLinkCount` | The guest links that are not revoked and not expired. |
| `publicShareEnabled`, `shareLastAccessedOn` | Whether the public link is on, and the last date that someone opened it. |
| `clientDevices` | One entry for each kind of paired app (`kind`), with the newest date that an app of that kind was seen (`lastSeenOn`). |
| `chatIdentityLinked` | Whether a chat account (Discord, Telegram and others) has an active link to the tenant. |
| `oauthClientCount` | The apps that registered with the tenant. |
| `coachMarksSeenCount` | The in-app hints that members saw or closed, added up for all members. |

### Connector status

Two dates on the same day cannot tell you which event came last, so use `status` to find connectors that fail.

| `status` | Meaning |
| --- | --- |
| `healthy` | The last sync completed, and it is not older than the stale limit of the connector. |
| `failing` | The last sync stopped with an error. The connector pages show this as an error. |
| `stale` | The last sync completed, but it is older than the stale limit of the connector. The limit is from 30 minutes to 6 hours, and it is the same limit that the data source page uses. |
| `never_synced` | No sync has completed. |

`status` does not look at `enabled`. A connector that is off is usually `stale` or `never_synced`.

### Connector error categories

| `errorCategory` | Meaning |
| --- | --- |
| `none` | The connector has no error now. |
| `auth` | The service did not accept the sign-in or the credentials. The person must usually do something. |
| `rate_limited` | The service limited the number of requests. |
| `network` | Nocturne could not connect to the service, or the sync took too long. |
| `upstream` | The service had an error, or it sent data that Nocturne cannot read. |
| `config` | A setting of the connector is missing or not correct. |
| `unknown` | The error is not in a category above. |

A successful sync clears the error, so `errorCategory` is `none` again after the next successful sync.

## How it reads the database

Each tenant's data is in tables that row level security keeps to that tenant. The endpoint reads a page of tenants in a small, fixed number of database round trips, however many tenants are on the page. For the tables that belong to one tenant, it sets the tenant for each tenant's statements in one transaction, and each statement also names its tenant. It never reads two tenants in one statement.
