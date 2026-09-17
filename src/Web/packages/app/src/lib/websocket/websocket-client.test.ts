import { describe, it, expect, afterEach, vi } from "vitest";
import type { WebSocketConfig } from "./types";

type Handler = (...args: unknown[]) => void;

/**
 * Stand-in for the Socket.IO socket, driven by the test. `connect` is fired by
 * hand so a handshake can be replayed with and without a ticket — the bridge
 * admits both, which is exactly what the client has to tell apart.
 */
class FakeSocket {
  handlers = new Map<string, Handler>();
  connected = false;
  id = "fake-socket";
  disconnectCalls = 0;
  connectCalls = 0;

  constructor(public options: { auth: (cb: (data: unknown) => void) => void }) {}

  on(event: string, handler: Handler) {
    this.handlers.set(event, handler);
    return this;
  }

  disconnect() {
    this.disconnectCalls++;
    this.connected = false;
    this.handlers.get("disconnect")?.("io client disconnect");
    return this;
  }

  connect() {
    this.connectCalls++;
    void this.handshake();
    return this;
  }

  emit() {
    return this;
  }

  /** Run the handshake the way Socket.IO does: resolve `auth`, then connect. */
  async handshake(): Promise<void> {
    await new Promise<void>((resolve) => this.options.auth(() => resolve()));
    this.connected = true;
    this.handlers.get("connect")?.();
  }

  /** The bridge rejecting the handshake outright, as it does for an invalid
   *  ticket or an unresolvable tenant. */
  async rejectHandshake(message: string): Promise<void> {
    await new Promise<void>((resolve) => this.options.auth(() => resolve()));
    this.handlers.get("connect_error")?.(new Error(message));
  }
}

let lastSocket: FakeSocket | null = null;

vi.mock("socket.io-client", () => ({
  io: (
    _url: string,
    options: { auth: (cb: (data: unknown) => void) => void }
  ) => {
    lastSocket = new FakeSocket(options);
    return lastSocket;
  },
}));

const { WebSocketClient } = await import("./websocket-client.svelte");

const config: WebSocketConfig = {
  url: "http://tenant.example.test",
  reconnectAttempts: Infinity,
  reconnectDelay: 50,
  maxReconnectDelay: 100,
  pingTimeout: 4000,
  pingInterval: 2000,
};

function stubTicketEndpoint(body: unknown, ok = true) {
  vi.stubGlobal(
    "fetch",
    vi.fn(async () => ({ ok, status: ok ? 200 : 500, json: async () => body }))
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  lastSocket = null;
});

describe("WebSocketClient handshake ticket handling", () => {
  it("reports connected once a ticket is accepted", async () => {
    stubTicketEndpoint({ token: "a-verifiable-ticket" });
    const client = new WebSocketClient(config);

    client.connect();
    await lastSocket!.handshake();

    expect(client.connectionStatus).toBe("connected");
    expect(client.isConnected).toBe(true);
    expect(lastSocket!.disconnectCalls).toBe(0);
  });

  it("drops a ticket-less handshake instead of reporting it connected", async () => {
    stubTicketEndpoint({ token: null, retry: true });
    const client = new WebSocketClient(config);

    client.connect();
    await lastSocket!.handshake();

    expect(client.connectionStatus).not.toBe("connected");
    expect(client.isConnected).toBe(false);
    expect(lastSocket!.disconnectCalls).toBe(1);
  });

  it("retries after a transient ticket failure", async () => {
    stubTicketEndpoint({ token: null, retry: true });
    const client = new WebSocketClient(config);

    client.connect();
    await lastSocket!.handshake();

    expect(client.connectionStatus).toBe("connecting");
    await vi.waitFor(() => expect(lastSocket!.connectCalls).toBeGreaterThan(0));
  });

  it("reports unauthorized, not an error, when realtime is definitively denied", async () => {
    stubTicketEndpoint({ token: null });
    const client = new WebSocketClient(config);

    client.connect();
    await lastSocket!.handshake();

    expect(client.connectionStatus).toBe("unauthorized");
    expect(client.lastError).toBeNull();
    expect(lastSocket!.connectCalls).toBe(0);
  });

  it("reports unauthorized when the bridge rejects a denied connection", async () => {
    stubTicketEndpoint({ token: null });
    const client = new WebSocketClient(config);

    client.connect();
    await lastSocket!.rejectHandshake("unauthorized");

    expect(client.connectionStatus).toBe("unauthorized");
    expect(client.lastError).toBeNull();
  });

  it("surfaces an error once a ticket outage outlasts the quiet retries", async () => {
    stubTicketEndpoint({ token: null, retry: true });
    const client = new WebSocketClient(config);

    client.connect();
    await lastSocket!.handshake();

    expect(client.connectionStatus).toBe("connecting");
    await vi.waitFor(() => expect(client.connectionStatus).toBe("error"), {
      timeout: 5000,
    });
  });

  it("treats an empty token as no ticket, matching the bridge", async () => {
    stubTicketEndpoint({ token: "", retry: true });
    const client = new WebSocketClient(config);

    client.connect();
    await lastSocket!.handshake();

    expect(client.connectionStatus).not.toBe("connected");
    expect(lastSocket!.disconnectCalls).toBeGreaterThan(0);
  });
});
