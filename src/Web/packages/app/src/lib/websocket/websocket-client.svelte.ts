// Svelte 5 Runes-based WebSocket client for Socket.io integration
import { io, type Socket } from "socket.io-client";
import type {
  WebSocketConfig,
  WebSocketConnectionStatus,
  WebSocketStats,
  WebSocketError,
  WebSocketEventHandlers,
  ConnectionInfo,
  DataUpdateEvent,
  StorageEvent,
  AnnouncementEvent,
  AlarmEvent,
  StatusEvent,
} from "./types";
import { realtimeSocketOptions } from "./socket-options";
import { isoNow } from "$lib/utils/now";

/** Per-socket record of whether its handshake carried a ticket. The bridge
 *  admits a ticket-less handshake so legacy Nightscout clients can run the
 *  classic `authorize` exchange, but joins such a socket to no room. */
interface HandshakeState {
  /** Sequence of the most recently started attempt. Socket.IO re-invokes `auth`
   *  on every engine reopen without waiting for the previous invocation to
   *  settle, so a slow ticket fetch can still be in flight when the next attempt
   *  begins. Only the newest attempt may publish its verdict. */
  attempt: number;
  ticketPresented: boolean;
}

/** Outcome of one ticket fetch, returned rather than written to the client so a
 *  superseded attempt cannot overwrite the live one's verdict. */
interface TicketResult {
  token: string | null;
  /** A definitive denial (the user isn't permitted realtime for this tenant)
   *  rather than a transient failure. */
  denied: boolean;
}

export class WebSocketClient {
  private socket: Socket | null = null;
  private config: WebSocketConfig;
  private eventHandlers: Partial<WebSocketEventHandlers> = {};

  connectionStatus = $state<WebSocketConnectionStatus>("idle");
  lastError = $state<WebSocketError | null>(null);
  stats = $state<WebSocketStats>({
    connectedClients: 0,
    uptime: 0,
    serverPort: 0,
    messageCount: 0,
    reconnectCount: 0,
  });

  private reconnectAttempts = $state(0);
  private lastMessageTime = $state<number>(0);

  /** Verdict of the live handshake attempt's ticket fetch: true for a definitive
   *  denial (the user isn't permitted realtime for this tenant) rather than a
   *  transient failure. Lets connect_error stay quiet and stop retrying for
   *  unauthorized users. Written only under the `activeHandshake` guard, so a
   *  superseded attempt can't strand a good socket. */
  private lastTicketDenied = false;
  /** The handshake state of the socket currently being established, so an
   *  attempt belonging to a replaced socket can recognise itself as stale. */
  private activeHandshake: HandshakeState | null = null;
  /** Set when disconnect() is called so a scheduled retry doesn't resurrect a
   *  deliberately-closed socket. */
  private intentionallyClosed = false;
  private authRetryTimer: ReturnType<typeof setTimeout> | null = null;
  /** Consecutive auth-retry count, for exponential backoff. Reset on connect. */
  private authRetryCount = 0;
  /** Whether the current error episode has already notified handlers, so a
   *  retry storm doesn't fire a toast every few seconds. Reset on connect. */
  private hasNotifiedConnectError = false;
  /** Ticket-less retries tolerated before the failure is surfaced. */
  private static readonly QUIET_AUTH_RETRIES = 3;

  /** Check if the client has a valid URL configured */
  hasValidUrl(): boolean {
    return Boolean(this.config.url);
  }

  // Derived state
  isConnected = $derived(this.connectionStatus === "connected");
  isConnecting = $derived(
    this.connectionStatus === "connecting" ||
      this.connectionStatus === "reconnecting"
  );
  timeSinceLastMessage = $derived.by(() => {
    if (!this.lastMessageTime) return null;
    return Date.now() - this.lastMessageTime;
  });

  constructor(config: WebSocketConfig) {
    this.config = config;
  }

  /** Connect to WebSocket bridge */
  connect(): void {
    if (this.socket?.connected) {
      return;
    }

    // Skip connection if URL is empty (SSR scenario)
    if (!this.config.url) {
      return;
    }

    this.connectionStatus = "connecting";
    this.lastError = null;
    this.intentionallyClosed = false;
    this.clearAuthRetry();

    // Scoped to this socket and recorded as the active one, so a ticket fetch
    // left hanging by a previous socket recognises itself as stale instead of
    // condemning this one's good handshake.
    const handshake: HandshakeState = { attempt: 0, ticketPresented: false };
    this.activeHandshake = handshake;

    try {
      this.socket = io(
        this.config.url,
        realtimeSocketOptions(this.config, (cb) => {
          // Fetched fresh on every (re)connect so a short-lived ticket never goes
          // stale across reconnections.
          const attempt = ++handshake.attempt;
          this.fetchTicket().then(({ token, denied }) => {
            // A superseded attempt neither publishes its verdict nor completes
            // its handshake: the engine that asked for it is already gone, and
            // the socket it would speak for belongs to a newer attempt.
            if (this.activeHandshake !== handshake) return;
            if (attempt !== handshake.attempt) return;
            this.lastTicketDenied = denied;
            // Boolean, not a null check: the bridge gates on truthiness, so an
            // empty token is ticket-less there and must be here too.
            handshake.ticketPresented = Boolean(token);
            cb({ token: token ?? "" });
          });
        })
      );

      this.setupEventListeners(handshake);
    } catch (error) {
      this.handleError(
        "connection",
        "Failed to create socket connection",
        error
      );
    }
  }

  /** Fetch a realtime handshake ticket from the BFF. Reports a denial only for a
   *  definitive one (`retry` not set by the endpoint); transient failures
   *  (timeout, network, 5xx) report false so the connect_error path keeps
   *  retrying. Bounded by a timeout so the Socket.IO `auth` callback always
   *  resolves. */
  private async fetchTicket(): Promise<TicketResult> {
    try {
      const res = await fetch(`${this.config.url}/realtime/ticket`, {
        credentials: "include",
        headers: { Accept: "application/json" },
        signal: AbortSignal.timeout(8000),
      });
      if (!res.ok) {
        return { token: null, denied: false }; // redirect / 5xx — transient
      }
      const body = (await res.json().catch(() => null)) as
        | { token?: string | null; retry?: boolean }
        | null;
      const token = body?.token ?? null;
      // 200 + null token = no ticket; a definitive denial unless the endpoint
      // flagged it transient.
      return { token, denied: token == null && body?.retry !== true };
    } catch {
      return { token: null, denied: false }; // network error / timeout
    }
  }

  /** A handshake ticket rejection is a server-side middleware error, which
   *  Socket.IO does NOT auto-reconnect from. Schedule a manual retry, backing
   *  off, so a transient ticket failure doesn't strand realtime until a page
   *  reload nor hammer the endpoint while it's down. */
  private scheduleAuthRetry(): void {
    if (this.authRetryTimer || this.intentionallyClosed) return;
    const base = this.config.reconnectDelay || 5000;
    const max = this.config.maxReconnectDelay || 30000;
    const delay = Math.min(base * 2 ** this.authRetryCount, max);
    this.authRetryCount++;
    this.authRetryTimer = setTimeout(() => {
      this.authRetryTimer = null;
      if (this.intentionallyClosed) return;
      this.socket?.connect();
    }, delay);
  }

  /** Drop a ticket-less handshake rather than leave it masquerading as a working
   *  connection. A definitive denial is terminal and reported as `unauthorized`
   *  so the UI shows "no realtime" rather than an error the user cannot act on;
   *  a slow or failing ticket endpoint is retried with backoff. */
  private handleTicketlessConnection(): void {
    this.socket?.disconnect();

    if (this.lastTicketDenied) {
      this.clearAuthRetry();
      this.lastError = null;
      this.connectionStatus = "unauthorized";
      return;
    }

    this.scheduleAuthRetry();

    // Stay quiet through the first few retries, which a briefly slow ticket
    // endpoint recovers from. Beyond that realtime is genuinely dead, and an
    // endless "connecting" would hide that as effectively as the false
    // "connected" this method exists to prevent.
    if (this.authRetryCount > WebSocketClient.QUIET_AUTH_RETRIES) {
      this.handleError("connection", "Realtime handshake could not be authorized");
    } else {
      this.connectionStatus = "connecting";
    }
  }

  private clearAuthRetry(): void {
    if (this.authRetryTimer) {
      clearTimeout(this.authRetryTimer);
      this.authRetryTimer = null;
    }
    this.authRetryCount = 0;
  }

  /** Disconnect from WebSocket */
  disconnect(): void {
    this.intentionallyClosed = true;
    this.clearAuthRetry();
    this.activeHandshake = null;
    if (this.socket) {
      this.socket.disconnect();
      this.socket = null;
    }
    this.connectionStatus = "disconnected";
  }

  /** Set up Socket.io event listeners */
  private setupEventListeners(handshake: HandshakeState): void {
    if (!this.socket) return;

    // Connection events
    this.socket.on("connect", () => {
      // Admitted without a ticket: joined to no room, so this socket delivers
      // nothing. Reporting it as connected hides a dead feed behind a healthy
      // indicator.
      if (!handshake.ticketPresented) {
        this.handleTicketlessConnection();
        return;
      }

      this.connectionStatus = "connected";
      this.reconnectAttempts = 0;
      this.lastError = null;
      this.lastTicketDenied = false;
      this.hasNotifiedConnectError = false;
      this.clearAuthRetry();
      this.eventHandlers.connect?.({
        clientId: this.socket?.id || "",
        serverTime: isoNow(),
        version: "1.0.0",
      });
    });

    this.socket.on("disconnect", (reason: string) => {
      this.connectionStatus = "disconnected";
      this.eventHandlers.disconnect?.(reason);
    });

    this.socket.on("connect_error", (error: Error) => {
      // A denied ticket means the API read policy rejected this connection — the
      // user isn't permitted realtime for this tenant. Report that as its own
      // status so no surface renders it as a fault.
      if (this.lastTicketDenied) {
        this.connectionStatus = "unauthorized";
        this.lastError = null;
        this.clearAuthRetry();
        return;
      }
      this.handleError("connection", "Connection error", error);
      // Socket.IO won't auto-reconnect from a handshake (middleware) rejection,
      // so drive a backing-off retry ourselves for transient failures.
      this.scheduleAuthRetry();
      // Notify once per error episode (reset on connect) so a retry storm
      // doesn't fire a toast every few seconds.
      if (!this.hasNotifiedConnectError) {
        this.hasNotifiedConnectError = true;
        this.eventHandlers.connect_error?.(error);
      }
    });

    this.socket.on("reconnect", (attemptNumber: number) => {
      this.connectionStatus = "connected";
      this.stats.reconnectCount++;
      this.eventHandlers.reconnect?.(attemptNumber);
    });

    this.socket.on("reconnect_failed", () => {
      this.connectionStatus = "error";
      this.eventHandlers.reconnect_failed?.();
    });

    this.socket.on("reconnecting", () => {
      this.connectionStatus = "reconnecting";
      this.reconnectAttempts++;
    });

    this.socket.on("connect_ack", (info: ConnectionInfo) => {
      this.eventHandlers.connect_ack?.(info);
    });

    // Data events matching WebSocketBridge message format
    this.socket.on("dataUpdate", (data: any) => {
      this.updateMessageStats();
      const event: DataUpdateEvent = {
        data: Array.isArray(data) ? data : [data],
      };
      this.eventHandlers.dataUpdate?.(event);
    });

    this.socket.on("create", (data: any) => {
      this.updateMessageStats();
      const event: StorageEvent = {
        colName: data.colName || data.collection || "entries",
        doc: data.doc || data.document || data,
      };
      this.eventHandlers.create?.(event);
    });

    this.socket.on("update", (data: any) => {
      this.updateMessageStats();
      const event: StorageEvent = {
        colName: data.colName || data.collection || "entries",
        doc: data.doc || data.document || data,
      };
      this.eventHandlers.update?.(event);
    });

    this.socket.on("delete", (data: any) => {
      this.updateMessageStats();
      const event: StorageEvent = {
        colName: data.colName || data.collection || "entries",
        doc: data.doc || data.document || data,
      };
      this.eventHandlers.delete?.(event);
    });

    // Notification events
    this.socket.on("announcement", (data: any) => {
      this.updateMessageStats();
      const event: AnnouncementEvent = {
        message: data.message || data.text || String(data),
        title: data.title || "Announcement",
        level: data.level || "info",
        timestamp: data.timestamp || isoNow(),
      };
      this.eventHandlers.announcement?.(event);
    });

    this.socket.on("alarm", (data: any) => {
      this.updateMessageStats();
      const event: AlarmEvent = {
        level: data.level || "warn",
        title: data.title || "Alarm",
        message: data.message,
        plugin: data.plugin || data.source,
        timestamp: data.timestamp || isoNow(),
        key: data.key || data.id,
      };
      this.eventHandlers.alarm?.(event);
    });

    this.socket.on("urgent_alarm", (data: any) => {
      this.updateMessageStats();
      const event: AlarmEvent = {
        ...data,
        level: "urgent",
      };
      this.eventHandlers.alarm?.(event);
    });

    this.socket.on("clear_alarm", () => {
      this.updateMessageStats();
      this.eventHandlers.clear_alarm?.();
    });

    this.socket.on("status", (data: any) => {
      this.updateMessageStats();
      const event: StatusEvent = {
        status: data.status || data.state,
        message: data.message,
        timestamp: data.timestamp || isoNow(),
      };
      this.eventHandlers.status?.(event);
    });

    // In-app notification events
    this.socket.on("notificationCreated", (data: any) => {
      this.updateMessageStats();
      this.eventHandlers.notificationCreated?.(data);
    });

    this.socket.on("notificationArchived", (data: any) => {
      this.updateMessageStats();
      this.eventHandlers.notificationArchived?.(data);
    });

    this.socket.on("notificationUpdated", (data: any) => {
      this.updateMessageStats();
      this.eventHandlers.notificationUpdated?.(data);
    });

    this.socket.on("syncProgress", (data: any) => {
      this.updateMessageStats();
      this.eventHandlers.syncProgress?.(data);
    });
  }

  /** Register event handlers */
  on<K extends keyof WebSocketEventHandlers>(
    event: K,
    handler: WebSocketEventHandlers[K]
  ): void {
    (this.eventHandlers as WebSocketEventHandlers)[event] = handler;
  }

  /** Remove event handlers */
  off<K extends keyof WebSocketEventHandlers>(event: K): void {
    delete this.eventHandlers[event];
  }

  /** Join a room (for targeted messaging) */
  joinRoom(room: string): void {
    this.socket?.emit("join", room);
  }

  /** Leave a room */
  leaveRoom(room: string): void {
    this.socket?.emit("leave", room);
  }

  /** Authenticate with the WebSocket bridge */
  authenticate(apiSecret: string): void {
    this.socket?.emit("authenticate", { secret: apiSecret });
  }

  /** Handle errors consistently */
  private handleError(
    type: WebSocketError["type"],
    message: string,
    details?: any
  ): void {
    this.connectionStatus = "error";
    this.lastError = {
      type,
      message,
      timestamp: Date.now(),
      details,
    };
  }

  /** Update message statistics */
  private updateMessageStats(): void {
    this.stats.messageCount++;
    this.lastMessageTime = Date.now();
  }

  /** Get current connection info */
  getConnectionInfo(): ConnectionInfo | null {
    if (!this.socket?.connected) return null;

    return {
      clientId: this.socket.id || "",
      serverTime: isoNow(),
      version: "1.0.0",
    };
  }

  /** Cleanup on destroy */
  destroy(): void {
    this.disconnect();
    this.eventHandlers = {};
  }
}
