import { describe, it, expect, afterEach, vi } from "vitest";
import { createServer, type Server } from "node:http";
import { io, type Socket } from "socket.io-client";
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
