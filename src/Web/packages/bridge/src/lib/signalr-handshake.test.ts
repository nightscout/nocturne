import { describe, expect, it } from "vitest";
import {
  HubConnectionBuilder,
  HttpTransportType,
  LogLevel,
} from "@microsoft/signalr";
import { HubConnectionBuilder as EsmHubConnectionBuilder } from "@microsoft/signalr/dist/esm/index.js";

class EarlyHandshakeWebSocket {
  static OPEN = 1;
  static sockets: EarlyHandshakeWebSocket[] = [];
  readyState = 1;
  onopen: (() => void) | null = null;
  onmessage: ((event: { data: string }) => void) | null = null;
  onclose: (() => void) | null = null;
  onerror: (() => void) | null = null;
  closed = false;

  constructor() {
    EarlyHandshakeWebSocket.sockets.push(this);
  }

  send(data: string): void {
    if (data.includes('"protocol"')) this.onmessage?.({ data: "{}\x1e" });
  }

  close(): void {
    this.closed = true;
    this.readyState = 3;
    this.onclose?.();
  }
}

describe.each([
  ["CJS", HubConnectionBuilder],
  ["ESM", EsmHubConnectionBuilder],
] as const)(
  "real SignalR %s early transport close (#630)",
  (_entry, Builder) => {
    it.each([
      [
        "handshake error",
        (socket: EarlyHandshakeWebSocket) =>
          socket.onmessage?.({
            data: '{"error":"Handshake was canceled."}\x1e',
          }),
      ],
      [
        "malformed handshake",
        (socket: EarlyHandshakeWebSocket) =>
          socket.onmessage?.({ data: "invalid-json\x1e" }),
      ],
      [
        "network close",
        (socket: EarlyHandshakeWebSocket) => socket.onclose?.(),
      ],
    ])(
      "rejects start on %s without throwing from a WebSocket callback and can start again",
      async (_reason, receive) => {
        EarlyHandshakeWebSocket.sockets = [];
        const connection = new Builder()
          .withUrl("http://api/hubs/data", {
            skipNegotiation: true,
            transport: HttpTransportType.WebSockets,
            WebSocket: EarlyHandshakeWebSocket as unknown as typeof WebSocket,
          })
          .configureLogging(LogLevel.None)
          .build();

        const starting = connection.start();
        const rejected = expect(starting).rejects.toThrow();
        const socket = EarlyHandshakeWebSocket.sockets[0];
        // Both callbacks run before the transport's resolved connect promise resumes.
        socket.onopen?.();
        expect(() => receive(socket)).not.toThrow();
        await rejected;
        expect(connection.state).toBe("Disconnected");
        expect(socket.closed).toBe(true);
        await connection.stop();

        const restarting = connection.start();
        EarlyHandshakeWebSocket.sockets[1].onopen?.();
        await restarting;
        expect(connection.state).toBe("Connected");
        await connection.stop();
        expect(connection.state).toBe("Disconnected");
      },
    );
  },
);
