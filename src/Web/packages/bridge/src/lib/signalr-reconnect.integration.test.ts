import { createServer } from "node:http";
import type { AddressInfo } from "node:net";
import { WebSocketServer, type WebSocket } from "ws";
import { io } from "socket.io-client";
import { expect, it, vi } from "vitest";
import SignalRClient from "./signalr-client.js";
import MessageTranslator from "./message-translator.js";
import SocketIOServer from "./socketio-server.js";
import { signHandshakeTicket } from "./handshake-ticket.js";

it("recovers all real hubs after partial handshake failure and a prolonged outage", async () => {
  let available = true;
  let refuseAlarmHandshake = true;
  let negotiations = 0;
  const active = new Map<string, Set<WebSocket>>();
  const subscriptions: string[] = [];
  const http = createServer((req, res) => {
    negotiations++;
    if (!available) {
      res.writeHead(503).end("API unavailable");
      return;
    }
    res.setHeader("Content-Type", "application/json");
    res.end(
      JSON.stringify({
        negotiateVersion: 1,
        connectionId: req.url,
        connectionToken: "test-token",
        availableTransports: [
          { transport: "WebSockets", transferFormats: ["Text"] },
        ],
      }),
    );
  });
  const sockets = new WebSocketServer({ server: http });
  sockets.on("connection", (socket, req) => {
    const hub = new URL(req.url!, "http://localhost").pathname;
    const connections = active.get(hub) ?? new Set<WebSocket>();
    active.set(hub, connections);
    connections.add(socket);
    socket.on("close", () => connections.delete(socket));
    socket.on("message", (data) => {
      for (const frame of data.toString().split("\x1e").filter(Boolean)) {
        const message = JSON.parse(frame);
        if (message.protocol) {
          if (hub === "/hubs/alarms" && refuseAlarmHandshake) {
            socket.send('{"error":"Handshake was canceled."}\x1e');
            continue;
          }
          socket.send("{}\x1e");
        } else if (message.invocationId) {
          subscriptions.push(`${hub}:${message.target}`);
          socket.send(
            JSON.stringify({
              type: 3,
              invocationId: message.invocationId,
              result: { success: true, collections: [] },
            }) + "\x1e",
          );
        }
      }
    });
  });
  await new Promise<void>((resolve) => http.listen(0, "127.0.0.1", resolve));
  const url = `http://127.0.0.1:${(http.address() as AddressInfo).port}`;
  const downstream = createServer();
  const relay = new SocketIOServer(
    downstream,
    {},
    "nocturne.test",
    ["test"],
    "test-key",
    url,
  );
  await relay.start();
  await new Promise<void>((resolve) =>
    downstream.listen(0, "127.0.0.1", resolve),
  );
  const consumer = io(
    `http://127.0.0.1:${(downstream.address() as AddressInfo).port}`,
    {
      autoConnect: false,
      transports: ["websocket"],
      extraHeaders: { "X-Forwarded-Host": "test.nocturne.test" },
      auth: {
        token: signHandshakeTicket("test-key", "test.nocturne.test", true),
      },
    },
  );
  const received = {
    create: vi.fn(),
    urgent_alarm: vi.fn(),
    configChanged: vi.fn(),
  };
  for (const [event, handler] of Object.entries(received))
    consumer.on(event, handler);
  const client = new SignalRClient(new MessageTranslator(relay, "test"), {
    hubUrl: `${url}/hubs/data`,
    alarmHubUrl: `${url}/hubs/alarms`,
    configHubUrl: `${url}/hubs/config`,
    instanceKey: "test-key",
    reconnectAttempts: 1,
    reconnectDelay: 10,
    maxReconnectDelay: 40,
  });
  const emit = (hub: string, target: string, payload: object) => {
    for (const socket of active.get(hub) ?? []) {
      socket.send(
        JSON.stringify({ type: 1, target, arguments: [payload] }) + "\x1e",
      );
    }
  };
  try {
    consumer.connect();
    await vi.waitFor(() => expect(consumer.connected).toBe(true));
    await client.connect();
    expect(client.isConnected()).toBe(false);
    available = false;
    await vi.waitFor(() => expect(active.get("/hubs/data")?.size).toBe(0));
    await vi.waitFor(() => expect(negotiations).toBeGreaterThanOrEqual(8));

    refuseAlarmHandshake = false;
    available = true;
    await vi.waitFor(() => expect(client.isConnected()).toBe(true));
    for (const hub of ["data", "alarms", "config"]) {
      expect(active.get(`/hubs/${hub}`)?.size).toBe(1);
    }
    expect(subscriptions).toEqual(
      expect.arrayContaining([
        "/hubs/data:Authorize",
        "/hubs/data:Subscribe",
        "/hubs/alarms:Subscribe",
        "/hubs/config:SubscribeAll",
      ]),
    );
    emit("/hubs/data", "create", {
      collection: "entries",
      document: { sgv: 120 },
    });
    emit("/hubs/alarms", "alarm", { level: "urgent", message: "HIGH" });
    emit("/hubs/config", "configChanged", { key: "threshold" });
    await vi.waitFor(() => {
      expect(received.create).toHaveBeenCalledExactlyOnceWith(
        expect.objectContaining({ colName: "entries", doc: { sgv: 120 } }),
      );
      expect(received.urgent_alarm).toHaveBeenCalledExactlyOnceWith(
        expect.objectContaining({ level: "urgent", message: "HIGH" }),
      );
      expect(received.configChanged).toHaveBeenCalledExactlyOnceWith({
        key: "threshold",
      });
    });

    available = false;
    for (const connections of active.values())
      for (const socket of connections) socket.terminate();
    await vi.waitFor(() => expect(client.isConnected()).toBe(false));
    await client.disconnect();
    const attemptsAtShutdown = negotiations;
    await new Promise((resolve) => setTimeout(resolve, 100));
    expect(negotiations).toBe(attemptsAtShutdown);
  } finally {
    consumer.disconnect();
    await client.disconnect();
    await relay.stop();
    for (const connections of active.values())
      for (const socket of connections) socket.terminate();
    await new Promise<void>((resolve) => sockets.close(() => resolve()));
    await new Promise<void>((resolve) => http.close(() => resolve()));
  }
});
