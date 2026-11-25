import type {
  AlertAcknowledgementOutcome,
  AlertMetPoint,
  AlertPayload as GeneratedAlertPayload,
  ChatAcknowledgeRequest,
  DirectoryCandidate as GeneratedDirectoryCandidate,
  HeartbeatRequest,
  MarkDeliveredRequest,
  MarkFailedRequest,
  PaginatedResponseOfSensorGlucose,
  PendingDeliveryResponse,
  PendingLinkResponse,
  ActiveExcursionResponse,
} from "@nocturne/api-client";

/**
 * Minimal interface matching the NSwag-generated ApiClient shape, whose date-times
 * are ISO 8601 strings.
 * Only includes the methods the bot actually uses.
 * The SvelteKit app passes `locals.apiClient` which satisfies this interface.
 */
export interface BotApiClient {
  sensorGlucose: {
    getAll(
      from?: string | null,
      to?: string | null,
      limit?: number,
      offset?: number,
      sort?: string,
      device?: string | null,
      source?: string | null,
      signal?: AbortSignal,
    ): Promise<PaginatedResponseOfSensorGlucose>;
  };
  alerts: {
    /** Null when the response body is empty or its status is unmapped. */
    getActiveAlerts(signal?: AbortSignal): Promise<ActiveExcursionResponse[] | null>;
    /**
     * Acknowledges as the member behind the chat link, never as the bot: the
     * API decides on that member's authority whether this stops the alert for
     * everyone or mutes it for them alone. Call it on the link's tenant client.
     */
    acknowledgeAsLinkedMember(
      linkId: string,
      request: ChatAcknowledgeRequest,
      signal?: AbortSignal,
    ): Promise<AcknowledgementResult>;
    markDelivered(deliveryId: string, request: MarkDeliveredRequest, signal?: AbortSignal): Promise<void>;
    markFailed(deliveryId: string, request: MarkFailedRequest, signal?: AbortSignal): Promise<void>;
    getPendingDeliveries(channelType?: string[], signal?: AbortSignal): Promise<PendingDeliveryResponse[]>;
  };
  directory: {
    /** Returns ALL candidates for a (platform, platformUserId). Returns null on 404. */
    resolve(
      platform: string,
      platformUserId: string,
      signal?: AbortSignal,
    ): Promise<DirectoryCandidate[] | null>;

    /** Revoke a link by id, passing the (platform, platformUserId) on the row as a consistency check. */
    revokeByPlatformUser(
      linkId: string,
      platform: string,
      platformUserId: string,
      signal?: AbortSignal,
    ): Promise<void>;
  };
  pendingLinks: {
    create(
      platform: string,
      platformUserId: string,
      platformUserName: string | null,
      tenantSlug: string | null,
      source: "connect-slash" | "oauth2-finalize",
      signal?: AbortSignal,
    ): Promise<Required<PendingLinkResponse>>;
  };
  system: {
    heartbeat(request: HeartbeatRequest, signal?: AbortSignal): Promise<void>;
  };
}

/**
 * `acknowledged` stopped the alert for everyone, `muted` for the linked member
 * only, and `closed` means it had already ended.
 */
export type AcknowledgementOutcome = `${AlertAcknowledgementOutcome}`;

/**
 * An `acknowledged` result names who the API recorded as acknowledging, which
 * is someone other than the tapping user when `alreadyAcknowledged` is set.
 */
export type AcknowledgementResult =
  | {
      outcome: "acknowledged";
      acknowledgedBy: string | null;
      alreadyAcknowledged: boolean;
    }
  | { outcome: "muted" | "closed" };

/** A candidate as the app adapter hands it over: a field the API left out is filled with an empty value. */
export type DirectoryCandidate = Required<GeneratedDirectoryCandidate>;

/**
 * The generated payload as the dispatch boundary hands it on. The three enum fields are plain
 * strings, so a member this build does not know yet still reaches the card, which degrades it.
 * The strings a card prints are present; the boundary refuses a payload without one.
 */
export type AlertPayload = Omit<GeneratedAlertPayload, "kind" | "alertType" | "severity" | "metBy"> & {
  kind?: string;
  alertType?: string;
  severity: string;
  metBy?: MetPoint[];
} & Required<
    Pick<GeneratedAlertPayload, "ruleName" | "readingTimestamp" | "excursionId" | "tenantId" | "subjectName">
  >;

/** A met point with its enum fields as plain strings, as the payload's other enum fields are. */
export type MetPoint = Omit<AlertMetPoint, "kind" | "curve" | "limitFact"> & {
  kind?: string;
  curve?: string;
  limitFact?: string;
};

export interface AlertDispatchEvent {
  deliveryId: string;
  channelType: string;
  destination: string;
  payload: AlertPayload;
  /**
   * Slug of the tenant the alert belongs to. Sent by the API so the dispatch
   * route resolves the tenant from the request body rather than a forwarded
   * host header.
   */
  tenantSlug: string;
}
