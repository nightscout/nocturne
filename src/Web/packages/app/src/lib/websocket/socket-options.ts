import type { WebSocketConfig } from "./types";

/** How every browser socket to the bridge times out and reconnects. */
export const BROWSER_SOCKET_TIMING = {
  reconnectAttempts: Infinity,
  reconnectDelay: 5000,
  maxReconnectDelay: 30000,
  pingTimeout: 60000,
  pingInterval: 25000,
} satisfies Partial<WebSocketConfig>;

/** The `auth` callback Socket.IO invokes before each (re)connect handshake. */
export type SocketAuthProvider = (
  cb: (data: Record<string, unknown>) => void
) => void;

/**
 * Socket.IO client options for the realtime bridge connection.
 *
 * `tryAllTransports` is load-bearing: engine.io-client defaults it to `false`,
 * which ends the attempt on the first transport's error instead of trying the
 * next. With that default the "polling" entry below is unreachable, so any
 * network that blocks WebSocket (corporate proxies, some mobile carriers and
 * VPNs, privacy extensions) leaves realtime in a permanent `connect_error` that
 * the dashboard renders as "Connection Error" — while REST backfill quietly
 * keeps the data current, which is what makes the fault so confusing to report.
 * The bridge serves both transports.
 *
 * A socket on another namespace gets a connection of its own (`forceNew`). With
 * the default multiplexing it would share the tenant socket's transport, and
 * the bridge ends an admission by closing the transport, so one namespace's
 * refusal would drop the other's socket too.
 *
 * Extracted from the client so the transport policy can be tested against a
 * real server rather than asserted on an object literal.
 */
export function realtimeSocketOptions(
  config: WebSocketConfig,
  auth: SocketAuthProvider
) {
  return {
    transports: ["websocket", "polling"],
    tryAllTransports: true,
    timeout: config.pingTimeout,
    reconnection: true,
    reconnectionAttempts: config.reconnectAttempts,
    reconnectionDelay: config.reconnectDelay,
    reconnectionDelayMax: config.maxReconnectDelay,
    randomizationFactor: 0.5,
    auth,
    ...(config.namespace ? { forceNew: true } : {}),
  };
}
