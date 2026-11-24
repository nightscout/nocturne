import { describe, it, expect, afterEach, vi } from "vitest";
import { createServer, type Server } from "node:http";
import { io, type Socket } from "socket.io-client";
import { setupBridge } from "@nocturne/bridge";
import { signOverviewTicket } from "@nocturne/bridge/ticket";
import { realtimeSocketOptions } from "./socket-options";
import type { WebSocketConfig } from "./types";

const config: WebSocketConfig = {
  url: "",
  reconnectAttempts: Infinity,
  reconnectDelay: 50,
  maxReconnectDelay: 100,
  pingTimeout: 5000,
  pingInterval: 2000,
};

let server: Server | null = null;
let socket: Socket | null = null;

afterEach(async () => {
  socket?.close();
  socket = null;
  if (server) {
    const s = server;
    server = null;
    await new Promise<void>((resolve) => s.close(() => resolve()));
  }
});

/** A server that answers HTTP but refuses the WebSocket upgrade, standing in for
 *  the proxies, carriers and extensions that do the same to real users. It
 *  records the transports it is asked for, so the assertion observes what the
 *  client actually attempted rather than sampling its internal state. */
async function startWebsocketHostileServer(): Promise<{
  url: string;
  attempted: Set<string>;
}> {
  const attempted = new Set<string>();

  server = createServer((req, res) => {
    const transport = new URL(
      req.url ?? "/",
      "http://localhost"
    ).searchParams.get("transport");
    if (transport) attempted.add(transport);
    res.writeHead(502);
    res.end();
  });
  server.on("upgrade", (req, connection) => {
    const transport = new URL(
      req.url ?? "/",
      "http://localhost"
    ).searchParams.get("transport");
    if (transport) attempted.add(transport);
    connection.destroy();
  });

  await new Promise<void>((resolve) => server!.listen(0, "127.0.0.1", resolve));
  const address = server.address();
  if (typeof address === "string" || address === null) {
    throw new Error("expected a TCP address");
  }
  return { url: `http://127.0.0.1:${address.port}`, attempted };
}

describe("realtimeSocketOptions", () => {
  it("falls back to polling when the WebSocket transport fails", async () => {
    const { url, attempted } = await startWebsocketHostileServer();

    socket = io(url, realtimeSocketOptions(config, (cb) => cb({ token: "" })));

    // Without tryAllTransports, engine.io-client abandons the attempt on the
    // first transport error and "polling" is never requested.
    await vi.waitFor(() => expect(attempted).toContain("websocket"), {
      timeout: 5000,
    });
    await vi.waitFor(() => expect(attempted).toContain("polling"), {
      timeout: 5000,
    });
  });

  it("keeps WebSocket as the preferred transport", () => {
    const options = realtimeSocketOptions(config, (cb) => cb({}));

    expect(options.transports[0]).toBe("websocket");
    expect(options.tryAllTransports).toBe(true);
  });

  it("carries the caller's reconnection budget through", () => {
    const options = realtimeSocketOptions(config, (cb) => cb({}));

    expect(options.reconnection).toBe(true);
    expect(options.reconnectionAttempts).toBe(config.reconnectAttempts);
    expect(options.reconnectionDelay).toBe(config.reconnectDelay);
    expect(options.reconnectionDelayMax).toBe(config.maxReconnectDelay);
  });
});

describe("a socket on another bridge namespace", () => {
  const SECRET = "test-instance-key-0123456789";
  const host = "rhys.nocturne.run";
  const opened: Socket[] = [];
  let bridge: Awaited<ReturnType<typeof setupBridge>> | null = null;

  afterEach(async () => {
    for (const client of opened.splice(0)) client.close();
    await bridge?.disconnect();
    bridge = null;
    vi.unstubAllGlobals();
  });

  /** A real bridge whose API is unreachable: it admits sockets but relays nothing. */
  async function startBridge(): Promise<string> {
    vi.stubGlobal("fetch", vi.fn(async () => Promise.reject(new Error("no API here"))));
    server = createServer();
    await new Promise<void>((resolve) => server!.listen(0, "127.0.0.1", resolve));
    bridge = await setupBridge(server, {
      signalr: { hubUrl: "http://api.invalid/hubs/data", reconnectDelay: 60_000, maxReconnectDelay: 60_000 },
      instanceKey: SECRET,
      baseDomain: "nocturne.run",
    });
    const address = server.address();
    if (typeof address === "string" || address === null) throw new Error("expected a TCP address");
    return `http://127.0.0.1:${address.port}`;
  }

  /** A connected tenant socket and overview socket, with how often each was dropped. */
  async function openBoth(url: string) {
    const extraHeaders = { "X-Forwarded-Host": host };
    const tenant = io(url, { ...realtimeSocketOptions(config, (cb) => cb({ token: "" })), extraHeaders });
    const overview = io(`${url}/overview`, {
      ...realtimeSocketOptions({ ...config, namespace: "/overview" }, (cb) =>
        cb({
          token: signOverviewTicket(SECRET, host, {
            subjectId: "0a5f2c1e-1111-4222-8333-444455556666",
            tenantIds: [],
          }),
        })
      ),
      extraHeaders,
    });
    opened.push(tenant, overview);
    const drops = { tenant: 0, overview: 0 };
    tenant.on("disconnect", () => drops.tenant++);
    overview.on("disconnect", () => drops.overview++);
    await vi.waitFor(() => expect(tenant.connected && overview.connected).toBe(true), { timeout: 5000 });
    return { tenant, overview, drops };
  }

  const settle = () => new Promise((resolve) => setTimeout(resolve, 200));

  it("has a connection of its own, while the tenant socket keeps the shared one", () => {
    const tenantOptions = realtimeSocketOptions(config, (cb) => cb({}));
    const overviewOptions = realtimeSocketOptions({ ...config, namespace: "/overview" }, (cb) => cb({}));

    expect("forceNew" in tenantOptions).toBe(false);
    expect(overviewOptions).toMatchObject({ forceNew: true });
  });

  it("stays connected when the bridge closes the tenant socket's transport", async () => {
    const { tenant, overview, drops } = await openBoth(await startBridge());

    for (const socket of bridge!.io.of("/").sockets.values()) socket.conn.close();

    await vi.waitFor(() => expect(drops.tenant).toBe(1), { timeout: 5000 });
    await settle();
    expect(overview.io).not.toBe(tenant.io);
    expect(drops.overview).toBe(0);
    expect(overview.connected).toBe(true);
  });

  it("leaves the tenant socket connected when the bridge closes its transport", async () => {
    const { tenant, drops } = await openBoth(await startBridge());

    for (const socket of bridge!.io.of("/overview").sockets.values()) socket.conn.close();

    await vi.waitFor(() => expect(drops.overview).toBe(1), { timeout: 5000 });
    await settle();
    expect(drops.tenant).toBe(0);
    expect(tenant.connected).toBe(true);
  });
});
