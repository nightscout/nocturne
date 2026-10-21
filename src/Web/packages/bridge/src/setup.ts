import { createHash } from 'crypto';
import { Server as HttpServer } from 'http';
import type { BridgeConfig, BridgeInstance } from './types.js';
import { buildConfig } from './lib/config-builder.js';
import SocketIOServer from './lib/socketio-server.js';
import SignalRClient from './lib/signalr-client.js';
import MessageTranslator from './lib/message-translator.js';
import TenantRegistry, { type DiscoveredTenant, type TenantDirectory } from './lib/tenant-registry.js';
import logger from './lib/logger.js';
import { isRecord, stringField } from './lib/payload.js';

const DISCOVERY_TIMEOUT_MS = 10_000;
const DEFAULT_TENANT_REFRESH_INTERVAL_MS = 5_000;

/** Derive the API base URL from a SignalR hub URL (".../hubs/data" -> "..."). */
function apiBaseFromHubUrl(hubUrl: string): string {
  return hubUrl.replace(/\/hubs\/\w+$/, '');
}

/** Discover active tenants from the Nocturne admin API. */
async function discoverTenants(
  apiBaseUrl: string,
  instanceKeyHash: string,
): Promise<DiscoveredTenant[]> {
  const url = `${apiBaseUrl}/api/v4/admin/tenants`;
  logger.info(`Discovering tenants from ${url}`);

  const response = await fetch(url, {
    // X-Instance-Service marks this as a genuine service call so the API's
    // InstanceKeyHandler honors the instance key (a bare key is ignored).
    headers: {
      'X-Instance-Key': instanceKeyHash,
      'X-Instance-Service': 'nocturne-bridge',
    },
    signal: AbortSignal.timeout(DISCOVERY_TIMEOUT_MS),
  });

  if (!response.ok) {
    throw new Error(
      `Tenant discovery failed: ${response.status} ${response.statusText}`,
    );
  }

  const tenants: unknown = await response.json();
  if (!Array.isArray(tenants)) {
    throw new Error('Tenant discovery returned no tenant list');
  }
  const active = tenants.flatMap((t) => {
    const id = stringField(t, 'id');
    const slug = stringField(t, 'slug');
    return isRecord(t) && t.isActive && id && slug ? [{ id, slug }] : [];
  });

  logger.info(`Discovered ${active.length} active tenant(s): ${active.map((t) => t.slug).join(', ')}`);
  return active;
}

/** The tenant the API serves on the apex domain, or null when it serves none.
 *  Asked of the API rather than counted from the tenant list, so the bridge
 *  never disagrees with it about which tenants make an apex ambiguous. */
async function discoverApexTenant(
  apiBaseUrl: string,
  baseDomain: string,
): Promise<string | null> {
  const response = await fetch(`${apiBaseUrl}/api/v4/status`, {
    headers: { 'X-Forwarded-Host': baseDomain },
    signal: AbortSignal.timeout(DISCOVERY_TIMEOUT_MS),
  });

  if (!response.ok) {
    throw new Error(
      `Apex tenant discovery failed: ${response.status} ${response.statusText}`,
    );
  }

  return stringField(await response.json(), 'tenantSlug') ?? null;
}

/** Create a SignalR client for a specific tenant. */
function createTenantClient(
  socketIOServer: SocketIOServer,
  config: ReturnType<typeof buildConfig>,
  tenantSlug: string,
  tenantId: string,
  baseDomain: string,
  confirmReconnect: () => Promise<boolean>,
): SignalRClient {
  const tenantHost = `${tenantSlug}.${baseDomain}`;
  const messageTranslator = new MessageTranslator(socketIOServer, tenantSlug);

  return new SignalRClient(messageTranslator, {
    hubUrl: config.signalr.hubUrl,
    alarmHubUrl: config.signalr.alarmHubUrl,
    configHubUrl: config.signalr.configHubUrl,
    reconnectAttempts: config.signalr.reconnectAttempts,
    reconnectDelay: config.signalr.reconnectDelay,
    maxReconnectDelay: config.signalr.maxReconnectDelay,
    instanceKey: config.instanceKey,
    // See TenantAwareHub.ExpectedTenantHeader in the API.
    connectionHeaders: { 'X-Forwarded-Host': tenantHost, 'X-Expected-Tenant-Id': tenantId },
    confirmReconnect,
  });
}

export async function setupBridge(
  httpServer: HttpServer,
  userConfig: Partial<BridgeConfig>
): Promise<BridgeInstance> {
  logger.info('Setting up WebSocket Bridge...');

  const config = buildConfig(userConfig);

  // BASE_DOMAIN is how the bridge resolves which tenant a connection belongs to
  // and authorizes it against the API's read policy. Without it the bridge can't
  // scope or authorize Socket.IO traffic, so it refuses to start rather than
  // broadcast tenant data unauthenticated. Callers catch this and keep the web
  // app running with real-time updates disabled.
  if (!config.baseDomain) {
    throw new Error('BASE_DOMAIN is required to run the WebSocket bridge');
  }
  const baseDomain = config.baseDomain;

  logger.info(`SignalR DataHub URL: ${config.signalr.hubUrl}`);
  if (config.signalr.alarmHubUrl) {
    logger.info(`SignalR AlarmHub URL: ${config.signalr.alarmHubUrl}`);
  }
  if (config.signalr.configHubUrl) {
    logger.info(`SignalR ConfigHub URL: ${config.signalr.configHubUrl}`);
  }

  // The Socket.IO handshake is authorized by verifying a signed ticket the web
  // app's /realtime/ticket endpoint mints after it has checked the API read
  // policy. Both sides share INSTANCE_KEY as the HMAC secret.
  if (!config.instanceKey) {
    logger.warn(
      'INSTANCE_KEY is not set — every Socket.IO handshake ticket will fail verification, so realtime will be disabled until it is configured.',
    );
  }

  const apiBaseUrl = apiBaseFromHubUrl(config.signalr.hubUrl);

  // Start the Socket.IO server eagerly — it doesn't need tenant info to accept
  // browser connections. Tenant room assignment uses the Host header; the apex
  // tenant is only needed for the single-tenant apex and is filled in once
  // tenant discovery completes.
  const socketIOServer = new SocketIOServer(
    httpServer,
    config.socketio,
    baseDomain,
    null,
    config.instanceKey,
    apiBaseUrl,
  );

  await socketIOServer.start();
  logger.info('Socket.IO server started');

  const instanceKeyHash = createHash('sha256')
    .update(config.instanceKey)
    .digest('hex')
    .toLowerCase();

  const registry: TenantRegistry<SignalRClient> = new TenantRegistry({
    discover: async (): Promise<TenantDirectory> => {
      const [tenants, apexSlug] = await Promise.all([
        discoverTenants(apiBaseUrl, instanceKeyHash),
        discoverApexTenant(apiBaseUrl, baseDomain),
      ]);
      return { tenants, apexSlug };
    },
    createConnection: (slug, id) => {
      const client: SignalRClient = createTenantClient(
        socketIOServer, config, slug, id, baseDomain, () => registry.confirm(slug, client));
      return client;
    },
    refreshIntervalMs: userConfig.tenantRefreshIntervalMs ?? DEFAULT_TENANT_REFRESH_INTERVAL_MS,
    onTenantRemoved: (slug) => socketIOServer.disconnectTenant(slug),
    onApexChanged: (slug) => socketIOServer.setApexTenantSlug(slug),
  });

  socketIOServer.onTenantAdmitted = (slug) => registry.ensure(slug);
  socketIOServer.onApexUnresolved = async () => {
    await registry.refresh();
  };

  // Forward v3 `/alarm` ack events (AAPS silencing an alarm) to the API's
  // AlarmHub via the matching tenant's SignalR client.
  socketIOServer.onAlarmAck = (tenantSlug, level, group, silenceTime) => {
    const client = registry.get(tenantSlug);
    if (client) {
      void client.invokeAlarmAck(level, group, silenceTime);
    } else {
      logger.warn(
        `/alarm ack for tenant ${tenantSlug} has no connected SignalR client`,
      );
    }
  };

  // Discover tenants in the background so a slow or temporarily-unavailable API
  // doesn't prevent the Socket.IO server from accepting browser connections.
  logger.info(`Bridge tenant discovery starting (baseDomain: ${baseDomain})`);

  let cancelled = false;
  const discoverWithRetry = async () => {
    let delay = config.signalr.reconnectDelay;
    while (!cancelled && !(await registry.refresh())) {
      if (cancelled) return;
      logger.warn(`Bridge tenant discovery failed, retrying in ${delay}ms`);
      await new Promise((resolve) => setTimeout(resolve, delay));
      delay = Math.min(delay * 2, config.signalr.maxReconnectDelay);
    }
  };
  void discoverWithRetry();

  return {
    io: socketIOServer.getIO()!,
    disconnect: async () => {
      cancelled = true;
      await registry.stop();
      await socketIOServer.stop();
    },
    isConnected: () => registry.connections().some((c) => c.isConnected()),
    getStats: () => ({
      ...socketIOServer.getStats(),
      signalrConnected: registry.connections().some((c) => c.isConnected()),
    }),
  };
}
