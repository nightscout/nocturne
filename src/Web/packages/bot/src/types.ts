/**
 * Minimal interface matching the NSwag-generated ApiClient shape.
 * Only includes the methods the bot actually uses.
 * The SvelteKit app passes `locals.apiClient` which satisfies this interface.
 */
export interface BotApiClient {
  sensorGlucose: {
    getAll(
      from?: Date | null,
      to?: Date | null,
      limit?: number,
      offset?: number,
      sort?: string,
      device?: string | null,
      source?: string | null,
      signal?: AbortSignal,
    ): Promise<PaginatedSensorGlucose>;
  };
  alerts: {
    acknowledge(request: AcknowledgeRequest, signal?: AbortSignal): Promise<void>;
    markDelivered(deliveryId: string, request: MarkDeliveredRequest, signal?: AbortSignal): Promise<void>;
    markFailed(deliveryId: string, request: MarkFailedRequest, signal?: AbortSignal): Promise<void>;
    getPendingDeliveries(channelType?: string[], signal?: AbortSignal): Promise<PendingDeliveryResponse[]>;
  };
  chatIdentity: {
    resolve(platform?: string, platformUserId?: string, signal?: AbortSignal): Promise<ChatIdentityLinkResponse>;
    createLink(request: CreateChatIdentityLinkRequest, signal?: AbortSignal): Promise<ChatIdentityLinkResponse>;
  };
  system: {
    heartbeat(request: HeartbeatRequest, signal?: AbortSignal): Promise<void>;
  };
}

interface PaginatedSensorGlucose {
  data?: SensorGlucoseReading[];
  pagination?: { limit?: number; offset?: number; total?: number };
}

export interface SensorGlucoseReading {
  id?: string;
  mgdl?: number;
  mmol?: number;
  direction?: string;
  trend?: string;
  trendRate?: number;
  mills?: number;
  timestamp?: Date;
}

export interface AcknowledgeRequest {
  acknowledgedBy?: string;
}

export interface MarkDeliveredRequest {
  platformMessageId?: string;
  platformThreadId?: string;
}

export interface MarkFailedRequest {
  error?: string;
}

export interface PendingDeliveryResponse {
  id?: string;
  alertInstanceId?: string;
  channelType?: string;
  destination?: string;
  payload?: string;
  createdAt?: Date;
  retryCount?: number;
}

export interface ChatIdentityLinkResponse {
  id?: string;
  nocturneUserId?: string;
  platform?: string;
  platformUserId?: string;
  platformChannelId?: string;
  displayUnit?: string;
  isActive?: boolean;
  createdAt?: Date;
}

export interface CreateChatIdentityLinkRequest {
  nocturneUserId?: string;
  platform?: string;
  platformUserId?: string;
  platformChannelId?: string;
}

export interface HeartbeatRequest {
  platforms?: string[];
  service?: string;
}

export interface AlertPayload {
  alertType: string;
  ruleName: string;
  glucoseValue: number | null;
  trend: string | null;
  trendRate: number | null;
  readingTimestamp: string;
  excursionId: string;
  instanceId: string;
  tenantId: string;
  subjectName: string;
  activeExcursionCount: number;
}

export interface AlertDispatchEvent {
  deliveryId: string;
  channelType: string;
  destination: string;
  payload: AlertPayload;
}
