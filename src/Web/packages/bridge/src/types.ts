import { Server as SocketIOServerClass } from 'socket.io';

export interface BridgeConfig {
  signalr: {
    hubUrl: string;
    alarmHubUrl?: string;
    configHubUrl?: string;
    /** @deprecated The bridge retries indefinitely with maxReconnectDelay as the backoff cap. */
    reconnectAttempts?: number;
    reconnectDelay?: number;
    maxReconnectDelay?: number;
  };
  socketio?: {
    cors?: {
      origin: string | string[];
      methods?: string[];
      credentials?: boolean;
    };
    transports?: ('websocket' | 'polling')[];
    pingTimeout?: number;
    pingInterval?: number;
  };
  logging?: {
    level?: string;
    format?: string;
  };
  instanceKey: string;
  /** Base domain the bridge runs under (e.g. "nocturne.run"). Required at
   *  runtime: the bridge discovers active tenants from the API, opens a SignalR
   *  connection per tenant, and routes Socket.IO clients to tenant rooms keyed by
   *  the Host they connect on. Kept optional in the type because it is sourced
   *  from the environment; setupBridge throws when it is absent. */
  baseDomain?: string;
  /** The least time between two reads of the API's tenant list (default 5000 ms). The list is read
   *  again when a socket is admitted for a tenant the bridge holds no relay for, when a connection
   *  arrives on an apex that serves no tenant, and before a lost relay reconnects. */
  tenantRefreshIntervalMs?: number;
}

export interface CompleteBridgeConfig {
  signalr: {
    hubUrl: string;
    alarmHubUrl?: string;
    configHubUrl?: string;
    reconnectAttempts: number;
    reconnectDelay: number;
    maxReconnectDelay: number;
  };
  socketio: {
    cors: {
      origin: string | string[];
      methods: string[];
      credentials: boolean;
    };
    transports: ('websocket' | 'polling')[];
    pingTimeout: number;
    pingInterval: number;
  };
  logging: {
    level: string;
    format: string;
  };
  instanceKey: string;
  baseDomain?: string;
}

export interface BridgeInstance {
  io: SocketIOServerClass;
  disconnect: () => Promise<void>;
  isConnected: () => boolean;
  getStats: () => BridgeStats;
}

export interface BridgeStats {
  connectedClients: number;
  signalrConnected: boolean;
  uptime: number;
}

export interface ClientInfo {
  id: string;
  connectedAt: Date;
  address: string;
  userAgent: string | undefined;
}

/** The Socket.IO event an alarm goes out as, named by the SignalR event it arrived on. */
export type AlarmEvent = 'alarm' | 'urgent_alarm';

/** An alarm as the bridge relays it: every field passes through from the API. */
export interface AlarmData {
  level?: unknown;
  [key: string]: unknown;
}

export interface ServerStats {
  connectedClients: number;
  clients: ClientInfo[];
  uptime: number;
}
