import { createHash } from 'crypto';
import { Server as HttpServer } from 'http';
import type { BridgeConfig, BridgeInstance } from './types.js';
import { buildConfig } from './lib/config-builder.js';
import SocketIOServer from './lib/socketio-server.js';
import SignalRClient from './lib/signalr-client.js';
import MessageTranslator from './lib/message-translator.js';
import { TenantAuthorizer } from './lib/tenant-authorizer.js';
import logger from './lib/logger.js';

/** Derive the API base URL from a SignalR hub URL (".../hubs/data" -> "..."). */
function apiBaseFromHubUrl(hubUrl: string): string {
  return hubUrl.replace(/\/hubs\/\w+$/, '');
}

interface TenantInfo {
  slug: string;
  isActive: boolean;
}

/** Discover active tenants from the Nocturne admin API. */
async function discoverTenants(
  apiBaseUrl: string,
  instanceKeyHash: string,
): Promise<string[]> {
  const url = `${apiBaseUrl}/api/v4/admin/tenants`;
  logger.info(`Discovering tenants from ${url}`);

  const response = await fetch(url, {
    headers: { 'X-Instance-Key': instanceKeyHash },
  });

  if (!response.ok) {
    throw new Error(
      `Tenant discovery failed: ${response.status} ${response.statusText}`,
    );
  }

  const tenants = (await response.json()) as TenantInfo[];
  const activeSlugs = tenants
    .filter((t) => t.isActive)
    .map((t) => t.slug);

  logger.info(`Discovered ${activeSlugs.length} active tenant(s): ${activeSlugs.join(', ')}`);
  return activeSlugs;
}

/** Create a SignalR client for a specific tenant. */
function createTenantClient(
  socketIOServer: SocketIOServer,
  config: ReturnType<typeof buildConfig>,
  tenantSlug: string,
  baseDomain: string,
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
    connectionHeaders: { 'X-Forwarded-Host': tenantHost },
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

  // Authorize each Socket.IO handshake against the API's per-tenant read policy
  // before it joins a tenant room.
  const authorizer = new TenantAuthorizer({
    apiBaseUrl: apiBaseFromHubUrl(config.signalr.hubUrl),
  });

  // Start the Socket.IO server eagerly — it doesn't need tenant info to accept
  // browser connections. Tenant room assignment uses the Host header; the slug
  // list is only needed for the apex-domain single-tenant case and is filled in
  // once tenant discovery completes.
  const socketIOServer = new SocketIOServer(
    httpServer,
    config.socketio,
    baseDomain,
    [],
    authorizer,
  );

  await socketIOServer.start();
  logger.info('Socket.IO server started');

  // Discover tenants and connect their SignalR clients in the background so a
  // slow or temporarily-unavailable API doesn't prevent the Socket.IO server
  // from accepting browser connections.
  logger.info(`Bridge tenant discovery starting (baseDomain: ${baseDomain})`);

  const instanceKeyHash = createHash('sha1')
    .update(config.instanceKey)
    .digest('hex')
    .toLowerCase();

  const apiBaseUrl = apiBaseFromHubUrl(config.signalr.hubUrl);
  const clients: SignalRClient[] = [];
  let cancelled = false;

  const connectWithRetry = async () => {
    let delay = config.signalr.reconnectDelay;
    const maxDelay = config.signalr.maxReconnectDelay;

    while (!cancelled) {
      try {
        const tenantSlugs = await discoverTenants(apiBaseUrl, instanceKeyHash);
        socketIOServer.setTenantSlugs(tenantSlugs);

        for (const slug of tenantSlugs) {
          if (cancelled) return;
          const client = createTenantClient(socketIOServer, config, slug, baseDomain);
          clients.push(client);
          await client.connect();
          logger.info(`SignalR client connected for tenant: ${slug}`);
        }

        logger.info(`WebSocket Bridge setup completed (${clients.length} tenant connections)`);
        return;
      } catch (error) {
        // Clean up any partially-connected clients before retrying
        for (const client of clients) {
          try { await client.disconnect(); } catch { /* ignore cleanup errors */ }
        }
        clients.length = 0;

        if (cancelled) return;
        const message = error instanceof Error ? error.message : String(error);
        logger.warn(`Bridge setup attempt failed, retrying in ${delay}ms: ${message}`);
        await new Promise((resolve) => setTimeout(resolve, delay));
        delay = Math.min(delay * 2, maxDelay);
      }
    }
  };

  // Fire-and-forget — don't block the HTTP server from starting
  connectWithRetry();

  return {
    io: socketIOServer.getIO()!,
    disconnect: async () => {
      cancelled = true;
      for (const client of clients) {
        await client.disconnect();
      }
      await socketIOServer.stop();
    },
    isConnected: () => clients.some((c) => c.isConnected()),
    getStats: () => ({
      ...socketIOServer.getStats(),
      signalrConnected: clients.some((c) => c.isConnected()),
    }),
  };
}
