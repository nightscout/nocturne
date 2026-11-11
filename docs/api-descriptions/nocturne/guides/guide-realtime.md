---
displayName: Real-time updates
standalone: true
---
How a client gets a new reading or an alert when it happens, with no polling.

Nocturne sends real-time events over SignalR hubs. The hubs are not HTTP operations, so they do not appear in the lists of this reference.

```callout warning
The gateway of the Docker Compose bundle does not route `/hubs`. On an instance installed from that bundle, a client outside the instance cannot connect to a hub, and must poll the API. The web app of the instance connects to the hubs from inside.
```

## Hubs

| Hub | Path | What it sends | Scope |
| --- | --- | --- | --- |
| Data | `/hubs/data` | Each record that is created, changed or deleted in the collections the connection subscribed to. | The read scope of each collection |
| Alerts | `/hubs/alerts` | Alert events: `alert_dispatch`, `alert_resolved`, `alert_acknowledged`, `alert_snoozed`. | `alerts.read` |
| Alarms | `/hubs/alarms` | Alarms in the Nightscout shape: `alarm`, `urgent_alarm`, `clear_alarm`, `announcement`, `notification`. | `glucose.read` |
| Overview | `/hubs/overview` | A signal for each tenant that a caregiver follows, when that tenant has new data. | `glucose.read` |
| Home Assistant | `/hubs/home-assistant` | Glucose and alert events for the Home Assistant integration. | `alerts.read` |
| Config | `/hubs/config` | Changes to connector configuration. | Full access |

## Connect to the data hub

1. Connect with a SignalR client. Send an OAuth access token in the `Authorization` header, or in the `access_token` query parameter where the client cannot set a header. An API token is not read from `access_token`: send it in the next step.
2. Call `Authorize` with `{ token }`.
3. Call `Subscribe` with `{ collections }`. The collections are `glucose`, `care`, `device` and `therapy`. A collection is joined only if the credential has its read scope: `glucose.read`, `treatments.read`, `devices.read` and `therapy.read`. The response lists the collections that were joined.

A connection that does not call `Subscribe` receives no `create`, `update` or `delete` events.

## Data events

| Event | When |
| --- | --- |
| `create` | A record was created. `doc` has the same shape as in the v4 API. |
| `update` | A record was changed. |
| `delete` | A record was deleted. |

## Good to know

- Call `Authorize` and `Subscribe` after each connect, and again after each reconnect.
- On the alerts hub and the alarms hub, call `Subscribe` to start to receive events.
- A legacy Nightscout client that uses Socket.IO does not connect to the hubs. The web app of the instance runs a bridge that gives such a client the Socket.IO events it expects.
- After a reconnect, read the records since the last one you have, so that nothing is missed.
