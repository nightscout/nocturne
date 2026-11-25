import { WebSocketClient } from "#lib/websocket/websocket-client.svelte.js";
import { BROWSER_SOCKET_TIMING } from "#lib/websocket/socket-options.js";

/** How long a ping waits for the next before the overview is read, so a burst causes one read. */
export const OVERVIEW_PING_DEBOUNCE_MS = 1_000;

/** The longest a stream of pings defers a read, so a constant stream still reads every few seconds. */
export const OVERVIEW_PING_MAX_WAIT_MS = 5_000;

/**
 * Holds one socket on the bridge's `/overview` namespace while the calling component lives, and
 * calls `refresh` after the pings of the people the viewer follows. A viewer the API denies opens
 * no socket, so the caller keeps whatever poll it runs.
 *
 * Call during component initialization: the socket is owned by an `$effect` and is closed with the
 * component.
 */
export function followOverview(refresh: () => void): { readonly live: boolean } {
  let client = $state<WebSocketClient | null>(null);

  $effect(() => {
    const socket = new WebSocketClient({
      url: window.location.origin,
      namespace: "/overview",
      ticketPath: "/realtime/overview-ticket",
      ...BROWSER_SOCKET_TIMING,
    });
    let pending: ReturnType<typeof setTimeout> | null = null;
    let firstPingAt: number | null = null;

    function read() {
      pending = null;
      firstPingAt = null;
      // A hidden tab shows nobody the result; the caller's poll catches up when it returns.
      if (!document.hidden) refresh();
    }

    socket.on("overviewUpdate", () => {
      const now = Date.now();
      firstPingAt ??= now;
      if (pending) clearTimeout(pending);
      const wait = Math.min(OVERVIEW_PING_DEBOUNCE_MS, firstPingAt + OVERVIEW_PING_MAX_WAIT_MS - now);
      pending = setTimeout(read, Math.max(wait, 0));
    });

    client = socket;
    void socket.connectIfAdmitted();

    return () => {
      if (pending) clearTimeout(pending);
      socket.destroy();
      client = null;
    };
  });

  return {
    get live() {
      return client?.isConnected ?? false;
    },
  };
}
