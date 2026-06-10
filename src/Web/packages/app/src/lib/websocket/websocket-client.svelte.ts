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

export class WebSocketClient {
  private socket: Socket | null = null;
  private config: WebSocketConfig;
  private eventHandlers: Partial<WebSocketEventHandlers> = {};

  connectionStatus = $state<WebSocketConnectionStatus>("disconnected");
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

  /** True when the last ticket fetch was a definitive denial (the user isn't
   *  permitted realtime for this tenant) rather than a transient failure. Lets
   *  connect_error stay quiet and stop retrying for unauthorized users. */
  private lastTicketDenied = false;
  /** Set when disconnect() is called so a scheduled retry doesn't resurrect a
   *  deliberately-closed socket. */
  private intentionallyClosed = false;
  private authRetryTimer: ReturnType<typeof setTimeout> | null = null;
  /** Consecutive auth-retry count, for exponential backoff. Reset on connect. */
  private authRetryCount = 0;
  /** Whether the current error episode has already notified handlers, so a
   *  retry storm doesn't fire a toast every few seconds. Reset on connect. */
  private hasNotifiedConnectError = false;

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

    try {
      this.socket = io(this.config.url, {
        transports: ["websocket", "polling"],
        timeout: this.config.pingTimeout,
        reconnection: true,
        reconnectionAttempts: this.config.reconnectAttempts,
        reconnectionDelay: this.config.reconnectDelay,
        reconnectionDelayMax: this.config.maxReconnectDelay,
        randomizationFactor: 0.5,
        // Fetched fresh on every (re)connect so a short-lived ticket never goes
        // stale across reconnections.
        auth: (cb: (data: Record<string, unknown>) => void) => {
          this.fetchTicket().then((token) => cb({ token: token ?? "" }));
        },
      });

      this.setupEventListeners();
    } catch (error) {
      this.handleError(
        "connection",
        "Failed to create socket connection",
        error
      );
    }
  }

  /** Fetch a realtime handshake ticket from the BFF. Returns the token, or null
   *  when no ticket was minted. Sets `lastTicketDenied` only for a definitive
   *  denial (`retry` not set by the endpoint); transient failures (timeout,
   *  network, 5xx) leave it false so the connect_error path keeps retrying.
   *  Bounded by a timeout so the Socket.IO `auth` callback always resolves. */
  private async fetchTicket(): Promise<string | null> {
    try {
      const res = await fetch(`${this.config.url}/realtime/ticket`, {
        credentials: "include",
        headers: { Accept: "application/json" },
        signal: AbortSignal.timeout(8000),
      });
      if (!res.ok) {
        this.lastTicketDenied = false; // redirect / 5xx — treat as transient
        return null;
      }
      const body = (await res.json().catch(() => null)) as
        | { token?: string | null; retry?: boolean }
        | null;
      const token = body?.token ?? null;
      // 200 + null token = no ticket; a definitive denial unless the endpoint
      // flagged it transient.
      this.lastTicketDenied = token == null && body?.retry !== true;
      return token;
    } catch {
      this.lastTicketDenied = false; // network error / timeout — transient
      return null;
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
    if (this.socket) {
      this.socket.disconnect();
      this.socket = null;
    }
    this.connectionStatus = "disconnected";
  }

  /** Set up Socket.io event listeners */
  private setupEventListeners(): void {
    if (!this.socket) return;

    // Connection events
    this.socket.on("connect", () => {
      this.connectionStatus = "connected";
      this.reconnectAttempts = 0;
      this.lastError = null;
      this.lastTicketDenied = false;
      this.hasNotifiedConnectError = false;
      this.clearAuthRetry();
      this.eventHandlers.connect?.({
        clientId: this.socket?.id || "",
        serverTime: new Date().toISOString(),
        version: "1.0.0",
      });
    });

    this.socket.on("disconnect", (reason: string) => {
      this.connectionStatus = "disconnected";
      this.eventHandlers.disconnect?.(reason);
    });

    this.socket.on("connect_error", (error: Error) => {
      // A denied ticket means the API read policy rejected this connection — the
      // user isn't permitted realtime for this tenant. That's not a failure to
      // surface: go quiet and stop retrying rather than flashing a connection
      // error every few seconds.
      if (this.lastTicketDenied) {
        this.connectionStatus = "disconnected";
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
        timestamp: data.timestamp || new Date().toISOString(),
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
        timestamp: data.timestamp || new Date().toISOString(),
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
        timestamp: data.timestamp || new Date().toISOString(),
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
      serverTime: new Date().toISOString(),
      version: "1.0.0",
    };
  }

  /** Cleanup on destroy */
  destroy(): void {
    this.disconnect();
    this.eventHandlers = {};
  }
}
