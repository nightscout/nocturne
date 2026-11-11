---
displayName: Real-time updates
standalone: true
---
How a client gets a new reading or an alert when it happens, with no polling.

Nocturne sends real-time events over SignalR. The hubs are not HTTP operations, so they do not appear in the lists of this reference.

## Hubs

| Hub | Path | What it sends | Scope |
| --- | --- | --- | --- |
| Data | `/hubs/data` | Each record that is created, changed or deleted in the tenant. | `glucose.read` |
| Alerts | `/hubs/alerts` | Alert events: `alert_dispatch`, `alert_resolved`, `alert_acknowledged`, `alert_snoozed`. | `alerts.read` |
| Alarms | `/hubs/alarms` | Alarms in the Nightscout shape: `alarm`, `urgent_alarm`, `clear_alarm`, `announcement`, `notification`. | |
| Overview | `/hubs/overview` | A signal for each tenant that a caregiver follows, when that tenant has new data. | `glucose.read` |
| Home Assistant | `/hubs/home-assistant` | Glucose and alert events for the Home Assistant integration. | `alerts.read` |
| Config | `/hubs/config` | Changes to connector configuration. | Full access |

## Connect

Use a SignalR client for your language. Send the bearer token in the `Authorization` header. A browser cannot set that header on a WebSocket, so on the hub paths the token is also accepted in the `access_token` query parameter.

```ts
import { HubConnectionBuilder } from "@microsoft/signalr";

const connection = new HubConnectionBuilder()
  .withUrl("https://<tenant>.<base-domain>/hubs/data", {
    accessTokenFactory: () => token,
  })
  .withAutomaticReconnect()
  .build();

connection.on("create", (event) => {
  // event.colName, event.recordType, event.doc
});

await connection.start();
await connection.invoke("Authorize", { token });
```

## Data events

| Event | Payload | When |
| --- | --- | --- |
| `create` | `colName`, `recordType`, `doc` | A record was created. `doc` has the same shape as in the v4 API. |
| `update` | `colName`, `recordType`, `doc` | A record was changed. |
| `delete` | `colName`, `recordType`, `id` | A record was deleted. |

## Good to know

- Call `Authorize` on the data hub and the overview hub after each connect, and again after each reconnect.
- On the alerts hub, call `Subscribe` to start to receive events. `Acknowledge` needs `alerts.readwrite`.
- A legacy Nightscout client that uses Socket.IO does not connect to the hubs. The web app of the instance runs a bridge that gives such a client the Socket.IO events it expects.
- After a reconnect, read the records since the last one you have, so that nothing is missed.
