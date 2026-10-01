import type {
  AcknowledgementOutcome,
  AcknowledgementResult,
  BotApiClient,
  DirectoryCandidate,
} from "@nocturne/bot";
import type { ApiClient } from "$lib/api";
import {
  createServerApiClient,
  getApiBaseUrl,
} from "$lib/server/api-client-factory";
import { getHashedInstanceKey } from "$lib/server/instance-key";
import { AlertAcknowledgementOutcome, ChannelType } from "$api-clients";
import { errorStatus } from "$lib/forms/submit-error";

const CHANNEL_TYPES: readonly ChannelType[] = Object.values(ChannelType);

/** The bot names channels by their wire strings; one this API does not know
 *  cannot match a delivery, so it is dropped rather than sent. */
function toChannelType(value: string): ChannelType[] {
  const known = CHANNEL_TYPES.find((type) => type === value);
  return known ? [known] : [];
}

/** A response with no outcome cannot be reported as any of them, so it fails the acknowledge. */
function toOutcome(outcome: AlertAcknowledgementOutcome | undefined): AcknowledgementOutcome {
  switch (outcome) {
    case AlertAcknowledgementOutcome.Acknowledged:
      return "acknowledged";
    case AlertAcknowledgementOutcome.Muted:
      return "muted";
    case AlertAcknowledgementOutcome.Closed:
      return "closed";
    default:
      throw new Error("Acknowledge response carried no outcome");
  }
}

/**
 * Adapts a NocturneApiClient to the BotApiClient interface used by @nocturne/bot.
 *
 * Wrap an instance-key-authenticated client built by
 * {@link buildUnscopedBotApiClient} (apex / cross-tenant) or
 * {@link buildScopedBotApiClient} (a specific tenant subdomain). The bot acts
 * as a trusted service, so it must use an explicit instance-key client — never
 * `locals.apiClient`, which carries only the end user's credentials.
 */
export function buildBotApiClient(api: ApiClient): BotApiClient {
  return {
    sensorGlucose: {
      async getAll(from, to, limit, offset, sort, device, source, signal) {
        return await api.sensorGlucose.getAll(
          from,
          to,
          limit,
          offset,
          sort,
          device,
          source,
          signal,
        );
      },
    },
    alerts: {
      getActiveAlerts: (signal) => api.alerts.getActiveAlerts(signal),
      acknowledgeAsLinkedMember: async (linkId, request, signal) => {
        const res = await api.chatIdentityDirectory.acknowledgeAsLinkedMember(
          linkId,
          { ...request, excursionId: request.excursionId ?? undefined },
          signal,
        );
        const outcome = toOutcome(res.outcome);
        return outcome === "acknowledged"
          ? {
              outcome,
              acknowledgedBy: res.acknowledgedBy ?? null,
              alreadyAcknowledged: res.alreadyAcknowledged ?? false,
            }
          : ({ outcome } satisfies AcknowledgementResult);
      },
      markDelivered: (deliveryId, request, signal) =>
        api.alerts.markDelivered(deliveryId, request, signal),
      markFailed: (deliveryId, request, signal) =>
        api.alerts.markFailed(deliveryId, request, signal),
      getPendingDeliveries: (channelType, signal) =>
        api.alerts.getPendingDeliveries(channelType?.flatMap(toChannelType), signal),
    },
    system: {
      heartbeat: (request, signal) => api.system.heartbeat(request, signal),
    },
    directory: {
      resolve: async (platform, platformUserId, signal) => {
        try {
          const res = await api.chatIdentityDirectory.resolve(
            platform,
            platformUserId,
            signal,
          );
          const rows = res.candidates ?? [];
          return rows.map(mapCandidate);
        } catch (err: unknown) {
          // 404 means "no entries" — return null so the bot can distinguish
          // "not linked" from "error".
          if (errorStatus(err) === 404) {
            return null;
          }
          throw err;
        }
      },
      revokeByPlatformUser: async (linkId, platform, platformUserId, signal) => {
        await api.chatIdentityDirectory.revokeByPlatformUser(
          linkId,
          { platform, platformUserId },
          signal,
        );
      },
    },
    pendingLinks: {
      create: async (platform, platformUserId, tenantSlug, source, signal) => {
        // NSwag generates tenantSlug as `string | undefined`; the bot
        // interface uses `string | null` to be explicit about "no slug
        // provided". Convert at the boundary.
        const res = await api.chatIdentityDirectory.createPending(
          {
            platform,
            platformUserId,
            tenantSlug: tenantSlug ?? undefined,
            source,
          },
          signal,
        );
        return { token: res.token ?? "" };
      },
    },
  };
}

/**
 * Builds the **unscoped** (apex / cross-tenant) BotApiClient using an explicit
 * instance-key client. Pass the forwarded host/proto headers from the incoming
 * request so tenant resolution and HTTPS enforcement on the API match what the
 * caller intended.
 *
 * Only for callers whose target tenant is genuinely unknown (webhook command
 * routing, which resolves a chat identity to a tenant first). A caller that
 * already knows the tenant must use {@link buildScopedBotApiClient} instead —
 * a forwarded host from an inbound request is client-controlled and would let
 * the caller pick the tenant these admin-privileged calls act on.
 */
export function buildUnscopedBotApiClient(
  fetchFn: typeof globalThis.fetch,
  extraHeaders?: Record<string, string>,
): BotApiClient {
  const apiBaseUrl = getApiBaseUrl();
  if (!apiBaseUrl) {
    throw new Error("API base URL not configured");
  }

  const apiClient = createServerApiClient(apiBaseUrl, fetchFn, {
    hashedInstanceKey: getHashedInstanceKey(),
    extraHeaders,
  });

  return buildBotApiClient(apiClient);
}

/**
 * Builds a tenant-scoped BotApiClient. The webhook route calls this factory
 * via `BotRequestContext.scopedApiFactory` once `requireLink` has resolved
 * the target tenant.
 *
 * Mechanism: constructs a fresh ApiClient whose X-Forwarded-Host is
 * `<tenantSlug>.<baseDomain-without-port>`, which causes the
 * TenantResolutionMiddleware on the API side to resolve to the correct tenant.
 */
export function buildScopedBotApiClient(
  fetchFn: typeof globalThis.fetch,
  tenantSlug: string,
): BotApiClient {
  const apiBaseUrl = getApiBaseUrl();
  if (!apiBaseUrl) {
    throw new Error("API base URL not configured");
  }

  const baseDomain = process.env.BASE_DOMAIN;
  if (!baseDomain) {
    throw new Error(
      "BASE_DOMAIN is required to build scoped bot api client",
    );
  }

  // Strip port from base domain for the Host header
  const baseDomainHost = baseDomain.split(":")[0] ?? baseDomain;
  const hostHeader = `${tenantSlug}.${baseDomainHost}`;

  const scopedApiClient = createServerApiClient(apiBaseUrl, fetchFn, {
    hashedInstanceKey: getHashedInstanceKey(),
    extraHeaders: { "X-Forwarded-Host": hostHeader, "X-Forwarded-Proto": "https" },
  });

  return buildBotApiClient(scopedApiClient);
}

function mapCandidate(c: {
  id?: string;
  tenantId?: string;
  tenantSlug?: string;
  nocturneUserId?: string;
  label?: string;
  displayName?: string;
  isDefault?: boolean;
}): DirectoryCandidate {
  return {
    id: c.id ?? "",
    tenantId: c.tenantId ?? "",
    tenantSlug: c.tenantSlug ?? "",
    nocturneUserId: c.nocturneUserId ?? "",
    label: c.label ?? "",
    displayName: c.displayName ?? "",
    isDefault: c.isDefault ?? false,
  };
}
