import type { WebSocketConnectionStatus } from "$lib/websocket/types";

/** `idle` has not been attempted yet, `connecting`/`reconnecting` are in flight,
 *  and `unauthorized` is a policy outcome the user cannot act on — none of them
 *  are failures worth reporting. */
export function isErrorStatus(status: WebSocketConnectionStatus): boolean {
  return status === "disconnected" || status === "error";
}

/** What a connection indicator shows. `pending` covers every state short of a
 *  reported outage (first connect, a drop still inside the grace window, a tab
 *  resuming), so it never renders as a failure. */
export type ConnectionPresentation = "live" | "pending" | "unavailable" | "denied";

export function presentConnection(
  status: WebSocketConnectionStatus,
  unavailable: boolean
): ConnectionPresentation {
  if (status === "unauthorized") return "denied";
  if (status === "connected") return "live";
  return unavailable ? "unavailable" : "pending";
}
