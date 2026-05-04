import { defineConfig, loadEnv } from "vite";
import { sveltekit } from "@sveltejs/kit/vite";
import commonjs from "vite-plugin-commonjs";
import lingo from 'vite-plugin-lingo';
import tailwindcss from "@tailwindcss/vite";
import { resolve } from "path";
import { setupBridge } from "@nocturne/bridge";
// WUCHALE-DISABLED: wuchale temporarily disabled — see also hooks.server.ts and +layout.ts
// import { wuchale } from '@wuchale/vite-plugin'

export default defineConfig(({ mode }) => {
  // Load env file based on `mode` in the current working directory.
  const env = loadEnv(mode, process.cwd(), "");

  return {
    assetsInclude: ["**/*.jpg", "**/*.png", "**/*.gif"],
    plugins: [
      tailwindcss(),
      sveltekit(),
      commonjs(),
    lingo({
      route: '/_translations',  // Route where editor UI is served
      localesDir: '../../locales',  // Path to .po files
    }),
      // wuchale(),
      // Custom plugin to integrate WebSocket bridge into Vite dev server
      {
        name: "websocket-bridge",
        configureServer(server) {
          const API_URL = env.PUBLIC_API_URL || env.NOCTURNE_API_URL || "http://localhost:5000";
          const SIGNALR_HUB_URL = `${API_URL}/hubs/data`;
          const SIGNALR_ALARM_HUB_URL = `${API_URL}/hubs/alarms`;
          const SIGNALR_CONFIG_HUB_URL = `${API_URL}/hubs/config`;
          const INSTANCE_KEY = env.INSTANCE_KEY || "";

          // Ensure the HTTP server is available before initializing the bridge
          if (!server.httpServer) {
            console.error(
              "HTTP server not available, skipping WebSocket bridge initialization"
            );
            return;
          }

          // Initialize WebSocket bridge with Vite's HTTP server
          setupBridge(server.httpServer, {
            signalr: {
              hubUrl: SIGNALR_HUB_URL,
              alarmHubUrl: SIGNALR_ALARM_HUB_URL,
              configHubUrl: SIGNALR_CONFIG_HUB_URL,
            },
            socketio: {
              cors: {
                origin: "*",
                methods: ["GET", "POST"],
                credentials: true,
              },
            },
            instanceKey: INSTANCE_KEY,
          })
            .then((bridge) => {
              console.log("✓ WebSocket bridge initialized successfully");
              console.log(`  SignalR Hub: ${SIGNALR_HUB_URL}`);
              console.log(`  SignalR connected: ${bridge.isConnected()}`);
            })
            .catch((error) => {
              console.error("✗ Failed to initialize WebSocket bridge:", error);
              console.error(
                "  Continuing without bridge - real-time features may not work"
              );
            });
        },
      },
    ],
    build: {
      rollupOptions: {
        // Native modules from @nocturne/bot's Discord.js dependency chain
        // that cannot be bundled by Rollup
        external: ["zlib-sync"],
      },
    },
    server: {
      host: "0.0.0.0",
      port: parseInt(process.env.PORT || "5173", 10),
      allowedHosts: true,
      hmr: process.env.VITE_HMR_CLIENT_PORT
        ? {
            protocol: "wss",
            host: process.env.VITE_HMR_HOST || "localhost",
            clientPort: parseInt(process.env.VITE_HMR_CLIENT_PORT, 10),
          }
        : undefined,
      watch: {
        ignored: ["**/node_modules/**", "**/.git/**"],
        usePolling: false,
      },
      fs: {
        allow: [
          "../node_modules", // This is for src/Web/packages/node_modules
          resolve(__dirname, "../../node_modules"), // This is for src/Web/node_modules
        ],
      },
    },
  };
});
