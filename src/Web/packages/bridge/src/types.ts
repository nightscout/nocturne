import { Server as HttpServer } from 'http';
import { Server as SocketIOServerClass } from 'socket.io';

export interface BridgeConfig {
  signalr: {
    hubUrl: string;
    alarmHubUrl?: string;
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
  apiSecret: string;
}

export interface CompleteBridgeConfig {
  signalr: {
    hubUrl: string;
    alarmHubUrl?: string;
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
  apiSecret: string;
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
