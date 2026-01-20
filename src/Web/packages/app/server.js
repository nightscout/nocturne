// Custom production server that integrates the WebSocket bridge with SvelteKit
import { createServer } from 'http';
import { handler } from './build/handler.js';
import { setupBridge } from '@nocturne/bridge';

const PORT = process.env.PORT || 5173;
const API_URL = process.env.NOCTURNE_API_URL || process.env.PUBLIC_API_URL || 'http://localhost:1612';
const SIGNALR_HUB_URL = `${API_URL}/hubs/data`;
const API_SECRET = process.env.API_SECRET || '';

async function start() {
  // Create HTTP server
  const server = createServer(handler);

  // Initialize WebSocket bridge
  try {
    const bridge = await setupBridge(server, {
      signalr: {
        hubUrl: SIGNALR_HUB_URL,
        reconnectAttempts: parseInt(process.env.PUBLIC_WEBSOCKET_RECONNECT_ATTEMPTS || '10'),
        reconnectDelay: parseInt(process.env.PUBLIC_WEBSOCKET_RECONNECT_DELAY || '5000'),
        maxReconnectDelay: parseInt(process.env.PUBLIC_WEBSOCKET_MAX_RECONNECT_DELAY || '30000'),
      },
      socketio: {
        cors: {
          origin: '*',
          methods: ['GET', 'POST'],
          credentials: true,
        },
        pingTimeout: parseInt(process.env.PUBLIC_WEBSOCKET_PING_TIMEOUT || '60000'),
        pingInterval: parseInt(process.env.PUBLIC_WEBSOCKET_PING_INTERVAL || '25000'),
      },
      apiSecret: API_SECRET,
    });

    console.log('✓ WebSocket bridge initialized successfully');
    console.log(`  SignalR Hub: ${SIGNALR_HUB_URL}`);
    console.log(`  SignalR connected: ${bridge.isConnected()}`);

    // Graceful shutdown
    process.on('SIGTERM', async () => {
      console.log('Received SIGTERM, shutting down gracefully...');
      await bridge.disconnect();
      server.close(() => {
        console.log('Server closed');
        process.exit(0);
      });
    });

    process.on('SIGINT', async () => {
      console.log('Received SIGINT, shutting down gracefully...');
      await bridge.disconnect();
      server.close(() => {
        console.log('Server closed');
        process.exit(0);
      });
    });
  } catch (error) {
    console.error('✗ Failed to initialize WebSocket bridge:', error);
    console.error('  The app will continue to work, but real-time updates will be unavailable.');
  }

  // Start server
  server.listen(PORT, () => {
    console.log(`Nocturne Web listening on port ${PORT}`);
  });
}

start();
