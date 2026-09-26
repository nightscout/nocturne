import type { WebSocketConnectionStatus } from "$lib/websocket/types";

/** How long the socket must stay down before the UI calls it an error, so an
 *  ordinary page-load connect or reconnect blip doesn't flash "Connection
 *  Error". */
export const DISCONNECT_GRACE_MS = 3000;

/** `idle` has not been attempted yet, `connecting`/`reconnecting` are in flight,
 *  and `unauthorized` is a policy outcome the user cannot act on — none of them
 *  are failures worth reporting. */
export function isErrorStatus(status: WebSocketConnectionStatus): boolean {
  return status === "disconnected" || status === "error";
}

/** Debounced "show the connection error" flag. Call during component init: it
 *  owns an `$effect`. Recovery clears it immediately; a drop waits `graceMs`. */
export function createConnectionIndicator(
  status: () => WebSocketConnectionStatus,
  graceMs: number = DISCONNECT_GRACE_MS
): { readonly isDisconnected: boolean } {
  let disconnected = $state(false);

  $effect(() => {
    if (!isErrorStatus(status())) {
      disconnected = false;
      return;
    }
    const timeout = setTimeout(() => {
      disconnected = true;
    }, graceMs);
    return () => clearTimeout(timeout);
  });

  return {
    get isDisconnected() {
      return disconnected;
    },
  };
}
