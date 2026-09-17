import type { WebSocketConfig } from "./types";

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
  };
}
