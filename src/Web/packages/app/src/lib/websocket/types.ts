// WebSocket integration types using existing API client types
import type {
  Entry,
  Treatment,
  TrackerInstanceDto,
  InAppNotificationDto,
} from '$lib/api/generated/nocturne-api-client';

// Re-export API client types for convenience
export type { Entry, Treatment };

// Local type definitions for WebSocket events
export type WebSocketEvents = {
  [key: string]: any;
};

export type WebSocketEventsMetadata = {
  [key: string]: any;
};

// WebSocket connection states
export type WebSocketConnectionStatus =
  | 'connecting'
  | 'connected'
  | 'disconnected'
  | 'reconnecting'
  | 'error';

// WebSocket client configuration
export interface WebSocketConfig {
  url: string;
  reconnectAttempts: number;
  reconnectDelay: number;
  maxReconnectDelay: number;
  pingTimeout: number;
  pingInterval: number;
}

// Connection info from WebSocketBridge
export interface ConnectionInfo {
  clientId: string;
  serverTime: string;
  version: string;
}

// WebSocket event payloads based on WebSocketBridge message formats
export interface DataUpdateEvent {
  data: Entry[];
}

export interface StorageEvent {
  colName: string;
  doc: Entry | Treatment | any;
}

export interface AnnouncementEvent {
  message: string;
  title?: string;
  level?: string;
  timestamp?: string;
}

export interface AlarmEvent {
  level: string;
  title?: string;
  message?: string;
  plugin?: string;
  timestamp?: string;
  key?: string;
}

export interface StatusEvent {
  status: string;
  message?: string;
  timestamp?: string;
}

export interface TrackerUpdateEvent {
  action: 'create' | 'update' | 'delete' | 'complete' | 'ack';
  instance: TrackerInstanceDto;
}

export interface NotificationUpdateEvent {
  action: 'created' | 'archived' | 'updated';
  notification: InAppNotificationDto;
}

export type SyncMessageType =
  | "Authenticating"
  | "FetchingData"
  | "FetchingDataType"
  | "ProcessingDataType"
  | "PublishingDataType"
  | "SyncComplete"
  | "SyncFailed";

export interface SyncProgressEvent {
  connectorId: string;
  connectorName: string;
  phase: "Syncing" | "Completed" | "Failed";
  currentDataType: string | null;
  completedDataTypes: string[];
  totalDataTypes: number;
  itemsSyncedSoFar: Record<string, number>;
  errorMessage: string | null;
  timestamp: string;
  messageType: SyncMessageType | null;
  messageParams: Record<string, string> | null;
}

// WebSocket client statistics
export interface WebSocketStats {
  connectedClients: number;
  uptime: number;
  serverPort: number;
  lastMessageTime?: number;
  messageCount: number;
  reconnectCount: number;
}

// WebSocket event handlers type map
export interface WebSocketEventHandlers {
  connect: (info: ConnectionInfo) => void;
  disconnect: (reason: string) => void;
  connect_error: (error: Error) => void;
  reconnect: (attemptNumber: number) => void;
  reconnect_failed: () => void;
  connect_ack: (info: ConnectionInfo) => void;

  // Data events
  dataUpdate: (event: DataUpdateEvent) => void;
  create: (event: StorageEvent) => void;
  update: (event: StorageEvent) => void;
  delete: (event: StorageEvent) => void;

  // Notification events
  announcement: (event: AnnouncementEvent) => void;
  alarm: (event: AlarmEvent) => void;
  clear_alarm: () => void;
  status: (event: StatusEvent) => void;

  // Tracker events
  trackerUpdate: (event: TrackerUpdateEvent) => void;

  // In-app notification events
  notificationCreated: (notification: InAppNotificationDto) => void;
  notificationArchived: (notification: InAppNotificationDto) => void;
  notificationUpdated: (notification: InAppNotificationDto) => void;

  // Sync progress events
  syncProgress: (event: SyncProgressEvent) => void;
}

// Error types
export interface WebSocketError {
  type: 'connection' | 'authentication' | 'message' | 'timeout';
  message: string;
  timestamp: number;
  details?: any;
}