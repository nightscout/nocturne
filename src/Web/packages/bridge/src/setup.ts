import { Server as HttpServer } from 'http';
import type { BridgeConfig, BridgeInstance } from './types.js';
import { buildConfig } from './lib/config-builder.js';
import SocketIOServer from './lib/socketio-server.js';
import SignalRClient from './lib/signalr-client.js';
import MessageTranslator from './lib/message-translator.js';
import logger from './lib/logger.js';

export async function setupBridge(
  httpServer: HttpServer,
  userConfig: Partial<BridgeConfig>
): Promise<BridgeInstance> {
  try {
    logger.info('Setting up WebSocket Bridge...');

    // Build complete configuration
    const config = buildConfig(userConfig);

    logger.info(`SignalR DataHub URL: ${config.signalr.hubUrl}`);
    if (config.signalr.alarmHubUrl) {
      logger.info(`SignalR AlarmHub URL: ${config.signalr.alarmHubUrl}`);
    }

    // Create Socket.IO server attached to HTTP server
    const socketIOServer = new SocketIOServer(httpServer, config.socketio);

    // Create message translator
    const messageTranslator = new MessageTranslator(socketIOServer);

    // Create SignalR client
    const signalRClient = new SignalRClient(messageTranslator, {
      hubUrl: config.signalr.hubUrl,
      alarmHubUrl: config.signalr.alarmHubUrl,
      reconnectAttempts: config.signalr.reconnectAttempts,
      reconnectDelay: config.signalr.reconnectDelay,
      maxReconnectDelay: config.signalr.maxReconnectDelay,
      apiSecret: config.apiSecret
    });

    // Start Socket.IO server
    await socketIOServer.start();
    logger.info('Socket.IO server started');

    // Connect to SignalR hub
    await signalRClient.connect();
    logger.info('SignalR client connected');

    logger.info('WebSocket Bridge setup completed successfully');

    // Return bridge instance
    return {
      io: socketIOServer.getIO()!,
      disconnect: async () => {
        await signalRClient.disconnect();
        await socketIOServer.stop();
      },
      isConnected: () => signalRClient.isConnected(),
      getStats: () => ({
        ...socketIOServer.getStats(),
        signalrConnected: signalRClient.isConnected()
      })
    };

  } catch (error) {
    logger.error('Failed to setup WebSocket Bridge:', error);
    throw error;
  }
}
