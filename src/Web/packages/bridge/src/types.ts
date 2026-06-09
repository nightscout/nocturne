import { Server as HttpServer } from 'http';
import { Server as SocketIOServerClass } from 'socket.io';

export interface BridgeConfig {
  signalr: {
    hubUrl: string;
    alarmHubUrl?: string;
    configHubUrl?: string;
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

export interface AlarmData {
  level: string;
  [key: string]: any;
}

export interface ServerStats {
  connectedClients: number;
  clients: ClientInfo[];
  uptime: number;
}
