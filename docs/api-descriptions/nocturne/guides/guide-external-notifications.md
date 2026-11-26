---
displayName: Notifications from outside
standalone: true
---
How an app, or the operator of an instance, puts a notice in the notifications of a tenant's members.

A notice has a title, a plain-text body, one optional link, and an expiry date. Each notice shows its sender's name. The member can open the link or dismiss the notice. A sender cannot set the urgency, the icon, or other actions: every notice is information, and Nocturne never sends it to a phone as a push notification.

## Senders

| Sender | Endpoint | Credential |
| --- | --- | --- |
| An app | `POST /api/v4/notifications/external` | An OAuth access token with the `notifications.send` scope |
| The operator of the instance | `POST /api/v4/admin/tenants/{tenantId}/notifications` | The instance key |

Both endpoints use the same request body and give the same response.

### Apps

An app asks for the `notifications.send` scope when a member connects it. The tenant comes from the token, and the notice shows the app's registered name with the words "connected app".

- If the member who connected the app is an owner or an administrator, the app can send to the tenant's `owners` or `members`. It can also send to only that member.
- Any other member can let the app send to only that member.

Send no `audience` to send to the member who connected the app.

An app can send notices with 5 new keys to one tenant in 24 hours. A repeat of a key it sent before does not count. The sixth new key gets `429 Too Many Requests` with a `Retry-After` header and a `retryAfterSeconds` field.

### The operator

The operator of an instance sends with the instance key, to the owners or the members of any tenant. The notice shows `Operator:Name`, so that setting must have a value. With no value, the endpoint gives `409 operator_not_configured`. The operator has no limit on the number of sends. An app can never send as the operator.

## Send a notice

```http
POST /api/v4/notifications/external
Authorization: Bearer <token>
Content-Type: application/json

{
  "audience": "owners",
  "key": "plan.renewal:2026-12",
  "title": "Your plan renews on 1 December",
  "body": "We will charge the card on file.\nYou do not need to do anything.",
  "link": { "label": "See your plan", "url": "https://billing.example.com/plan" },
  "expiresAt": "2026-12-02T00:00:00Z"
}
```

| Field | Rules |
| --- | --- |
| `audience` | `owners` or `members`. The operator must send it. An app sends it only when an owner or an administrator connected the app. |
| `key` | 1 to 100 letters, digits, `.`, `_`, `:` or `-`. It identifies the notice for this sender on this tenant. |
| `title` | 1 to 120 characters on one line. |
| `body` | 1 to 500 characters. Line breaks are kept. Nocturne shows the text as written and never reads it as HTML or Markdown. |
| `link` | Optional. `label` is 1 to 40 characters. `url` must be `https`, with no user name or password in it, on an allowed domain (see [Links](#links)). |
| `expiresAt` | Optional. The notice is archived at this time. The default is 30 days after the send, and the maximum is 90 days. |

The response gives `recipientCount`, the number of members who receive the notice, and `recipients`, with the `subjectId`, the `status` and the `notificationId` of each member.

A tenant with no owner gets no `owners` notice: `recipientCount` is `0`. Nocturne does not send it to the other members.

## Keys and repeats

Each member receives a key from a sender one time only. Nocturne keeps a record of each key it delivers, and that record stays after the notice is gone. Because of this, you can send again safely after a timeout:

- `created` with a `notificationId`: the member has the notice. This is the result of the first send, and also of a repeat while the notice is still there.
- `alreadySent`: the member received this key before, and the notice is no longer there (for example, the member dismissed it, or it expired). Nocturne does not send it again.

A repeat send can add members who joined after the first send. To send new text, use a new key.

## Withdraw a notice

```http
DELETE /api/v4/notifications/external/{key}
DELETE /api/v4/admin/tenants/{tenantId}/notifications/{key}
```

This archives the notice of `key` for each member who has it, and gives `{ "withdrawnCount": n }`. A sender can withdraw only its own keys. The key stays used: a send with it after a withdrawal sends nothing.

## Links

The link opens in a new tab, and opening it archives the notice. The link must be on an allowed domain or one of its subdomains:

- **An app**: the hosts of the app's registered `https` redirect URIs. Loopback addresses and IP addresses do not count. An app with no such redirect URI cannot send a link.
- **The operator**: the domains in `Operator:NotificationLinkDomains`. When that list is empty, the domains of `Operator:Support:AccountBilling:Url` and `Operator:Support:AccountPortal:Url`.

## Errors

Errors use the ProblemDetails envelope. The `error` field gives the cause.

| Status | `error` | Cause |
| --- | --- | --- |
| 400 | `invalid_key`, `invalid_title`, `invalid_body`, `invalid_link`, `invalid_expiry` | A field does not obey the rules above. |
| 400 | `link_domain_not_allowed` | The link is not on an allowed domain. |
| 400 | `audience_required` | The operator sent no `audience`. |
| 403 | `audience_not_allowed` | The app sent an `audience`, but the member who connected it is not an owner or an administrator. |
| 403 | `oauth_app_required` | The credential is not an OAuth access token of an app. |
| 404 | `tenant_not_found` | No tenant has this id. |
| 404 | `unknown_key` | This sender sent no notice with this key to this tenant. |
| 409 | `tenant_inactive` | The tenant is not active. |
| 409 | `operator_not_configured` | `Operator:Name` has no value. |
| 429 | `rate_limited` | The app sent 5 new keys to this tenant in the last 24 hours. |
