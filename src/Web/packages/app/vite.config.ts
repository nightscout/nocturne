import { defineConfig, loadEnv, searchForWorkspaceRoot } from "vite";
import { sveltekit } from "@sveltejs/kit/vite";
import commonjs from "vite-plugin-commonjs";
import lingo from 'vite-plugin-lingo';
import tailwindcss from "@tailwindcss/vite";
import { setupBridge } from "@nocturne/bridge";
import { createRequire } from "node:module";
import { dirname } from "node:path";

// @resend/chat-sdk-adapter (via @nocturne/bot) renders email cards with React
// (react-email). It declares react/react-dom as transitive peers, so pnpm
// never co-locates React with the adapter in its store — a bundler or Node
// resolving `react` from the adapter's path can't find it. Pin React to the
// single hoisted copy (resolved at config-eval time, so it works the same on
// Windows and CI Linux) and bundle the adapter into the SSR output (see
// ssr.noExternal) so no unresolvable bare `react` import survives.
//
// Alias to the package *directories*, not their entry files: @rollup/plugin-alias
// matches at path boundaries, so `react-dom` -> <dir> also rewrites subpaths like
// `react-dom/server` -> <dir>/server (react-email needs that). Aliasing to a file
// would turn `react-dom/server` into `<dir>/index.js/server` (ENOTDIR).
const require = createRequire(import.meta.url);
const reactAliases = {
  react: dirname(require.resolve("react/package.json")),
  "react-dom": dirname(require.resolve("react-dom/package.json")),
};
// WUCHALE-DISABLED: wuchale temporarily disabled — see also hooks.server.ts and +layout.ts
// import { wuchale } from '@wuchale/vite-plugin'

export default defineConfig(({ mode }) => {
  // Load env file based on `mode` in the current working directory.
  const env = loadEnv(mode, process.cwd(), "");

  return {
    assetsInclude: ["**/*.jpg", "**/*.png", "**/*.gif"],
    resolve: {
      alias: reactAliases,
    },
    ssr: {
      // Bundle the Resend adapter (and its React deps) into the SSR output so
      // there is no bare `react` import left for Node to resolve from pnpm's
      // store path at prerender/runtime.
      noExternal: ["@resend/chat-sdk-adapter"],
    },
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
            baseDomain: env.BASE_DOMAIN || undefined,
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
      warmup: {
        clientFiles: [
          "./src/app.css",
          "./src/routes/+layout.svelte",
          "./src/routes/+layout.ts",
          "./src/routes/(authenticated)/+layout.svelte",
          "./src/routes/(authenticated)/+page.svelte",
        ],
        ssrFiles: [
          "./src/hooks.server.ts",
          "./src/routes/+layout.server.ts",
          "./src/routes/(authenticated)/+layout.server.ts",
          "./src/routes/(authenticated)/+page.server.ts",
        ],
      },
      watch: {
        ignored: ["**/node_modules/**", "**/.git/**"],
        usePolling: false,
      },
      fs: {
        allow: [searchForWorkspaceRoot(process.cwd())],
        strict: false, // pnpm symlinks into its content-addressable store
      },
    },
  };
});
