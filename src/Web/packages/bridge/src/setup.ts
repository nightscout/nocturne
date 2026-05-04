import { createHash } from 'crypto';
import { Server as HttpServer } from 'http';
import type { BridgeConfig, BridgeInstance } from './types.js';
import { buildConfig } from './lib/config-builder.js';
import SocketIOServer from './lib/socketio-server.js';
import SignalRClient from './lib/signalr-client.js';
import MessageTranslator from './lib/message-translator.js';
import logger from './lib/logger.js';

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
  try {
    logger.info('Setting up WebSocket Bridge...');

    const config = buildConfig(userConfig);

    logger.info(`SignalR DataHub URL: ${config.signalr.hubUrl}`);
    if (config.signalr.alarmHubUrl) {
      logger.info(`SignalR AlarmHub URL: ${config.signalr.alarmHubUrl}`);
    }
    if (config.signalr.configHubUrl) {
      logger.info(`SignalR ConfigHub URL: ${config.signalr.configHubUrl}`);
    }

    // Discover tenants before starting the socket.io server so apex-domain
    // connections can auto-resolve to the sole tenant during the handshake.
    let tenantSlugs: string[] = [];
    if (config.baseDomain) {
      logger.info(`Multi-tenant mode enabled (baseDomain: ${config.baseDomain})`);

      const instanceKeyHash = createHash('sha1')
        .update(config.instanceKey)
        .digest('hex')
        .toLowerCase();

      // Extract the API base URL from the hub URL (strip /hubs/data)
      const apiBaseUrl = config.signalr.hubUrl.replace(/\/hubs\/\w+$/, '');
      tenantSlugs = await discoverTenants(apiBaseUrl, instanceKeyHash);
    }

    const socketIOServer = new SocketIOServer(
      httpServer,
      config.socketio,
      config.baseDomain,
      tenantSlugs,
    );

    await socketIOServer.start();
    logger.info('Socket.IO server started');

    if (config.baseDomain) {
      const clients: SignalRClient[] = [];

      for (const slug of tenantSlugs) {
        const client = createTenantClient(
          socketIOServer,
          config,
          slug,
          config.baseDomain,
        );
        clients.push(client);

        await client.connect();
        logger.info(`SignalR client connected for tenant: ${slug}`);
      }

      logger.info(`WebSocket Bridge setup completed (${clients.length} tenant connections)`);

      return {
        io: socketIOServer.getIO()!,
        disconnect: async () => {
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

    // Single-tenant mode (backward compatible): one SignalR connection, no tenant scoping
    const messageTranslator = new MessageTranslator(socketIOServer);
    const signalRClient = new SignalRClient(messageTranslator, {
      hubUrl: config.signalr.hubUrl,
      alarmHubUrl: config.signalr.alarmHubUrl,
      configHubUrl: config.signalr.configHubUrl,
      reconnectAttempts: config.signalr.reconnectAttempts,
      reconnectDelay: config.signalr.reconnectDelay,
      maxReconnectDelay: config.signalr.maxReconnectDelay,
      instanceKey: config.instanceKey,
    });

    await signalRClient.connect();
    logger.info('SignalR client connected');

    logger.info('WebSocket Bridge setup completed successfully');

    return {
      io: socketIOServer.getIO()!,
      disconnect: async () => {
        await signalRClient.disconnect();
        await socketIOServer.stop();
      },
      isConnected: () => signalRClient.isConnected(),
      getStats: () => ({
        ...socketIOServer.getStats(),
        signalrConnected: signalRClient.isConnected(),
      }),
    };
  } catch (error) {
    logger.error('Failed to setup WebSocket Bridge:', error);
    throw error;
  }
}
