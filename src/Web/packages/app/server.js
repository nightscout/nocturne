// Custom production server that integrates the WebSocket bridge with SvelteKit.
//
// Load OpenTelemetry instrumentation before any application code. adapter-node's
// generated entrypoint (build/index.js) imports the built instrumentation first via
// a facade, but this custom server replaces that entrypoint, so nothing else loads
// it. Importing it here — ahead of the app modules, which are imported dynamically
// inside start() — lets its import-in-the-middle hook install before the modules it
// instruments. Without this the OTLP SDK never starts and the web emits no telemetry.
import './build/server/instrumentation.server.js';

import { warnOnOriginMismatch } from './server-origin-warning.js';

// Hardcoded WebSocket bridge tuning. These were previously env vars but are
// internal implementation details, not per-deployment knobs. Keep in sync
// with src/lib/config/constants.ts.
const WEBSOCKET_RECONNECT_ATTEMPTS = 5;
const WEBSOCKET_RECONNECT_DELAY_MS = 1_000;
const WEBSOCKET_MAX_RECONNECT_DELAY_MS = 30_000;
const WEBSOCKET_PING_TIMEOUT_MS = 60_000;
const WEBSOCKET_PING_INTERVAL_MS = 20_000;

const PORT = process.env.PORT || 5173;
const API_URL = process.env.NOCTURNE_API_URL || process.env.PUBLIC_API_URL || 'http://localhost:1612';
const BASE_DOMAIN = process.env.BASE_DOMAIN || '';
const SIGNALR_HUB_URL = `${API_URL}/hubs/data`;
const SIGNALR_ALARM_HUB_URL = `${API_URL}/hubs/alarms`;
const SIGNALR_CONFIG_HUB_URL = `${API_URL}/hubs/config`;
const INSTANCE_KEY = process.env.INSTANCE_KEY || '';

async function start() {
  // Imported dynamically (after the instrumentation import above) so the OTel
  // import hook can patch these modules and their dependencies as they load.
  const { createServer } = await import('http');
  const { handler } = await import('./build/handler.js');
  const { setupBridge } = await import('@nocturne/bridge');

  // Create HTTP server.
  //
  // adapter-node reconstructs the request origin from PROTOCOL_HEADER /
  // HOST_HEADER (x-forwarded-proto / x-forwarded-host, set by the TLS edge).
  // When no edge is in front — e.g. the byo-proxy bundle's gateway accessed
  // directly on plain HTTP before a proxy is set up — neither header exists,
  // and adapter-node assumes "https" for the missing protocol, so the computed
  // origin never matches the browser's and every remote-function POST is
  // rejected as cross-site (403), which blocks the first-run setup wizard.
  // Default the headers from the actual connection so an edge-less request
  // still yields the origin the browser sees; requests that arrive with the
  // headers already set (Caddy, byo proxies) are untouched.
  const server = createServer((req, res) => {
    req.headers['x-forwarded-proto'] ??= req.socket.encrypted ? 'https' : 'http';
    req.headers['x-forwarded-host'] ??= req.headers.host;
    warnOnOriginMismatch(req, res);
    return handler(req, res);
  });

  // Initialize WebSocket bridge
  try {
    const bridge = await setupBridge(server, {
      signalr: {
        hubUrl: SIGNALR_HUB_URL,
        alarmHubUrl: SIGNALR_ALARM_HUB_URL,
        configHubUrl: SIGNALR_CONFIG_HUB_URL,
        reconnectAttempts: WEBSOCKET_RECONNECT_ATTEMPTS,
        reconnectDelay: WEBSOCKET_RECONNECT_DELAY_MS,
        maxReconnectDelay: WEBSOCKET_MAX_RECONNECT_DELAY_MS,
      },
      socketio: {
        cors: {
          origin: '*',
          methods: ['GET', 'POST'],
          credentials: true,
        },
        pingTimeout: WEBSOCKET_PING_TIMEOUT_MS,
        pingInterval: WEBSOCKET_PING_INTERVAL_MS,
      },
      instanceKey: INSTANCE_KEY,
      baseDomain: BASE_DOMAIN || undefined,
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
