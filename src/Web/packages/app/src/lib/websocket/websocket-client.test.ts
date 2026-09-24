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
    await this.sendHandshake();
    this.fireConnect();
  }

  /** Resolve `auth` for one attempt without delivering the server's acceptance.
   *  Socket.IO calls `auth` on every engine reopen without waiting for the
   *  previous call to settle, so attempts have to be drivable separately. */
  sendHandshake(): Promise<void> {
    return new Promise<void>((resolve) => this.options.auth(() => resolve()));
  }

  /** The server accepting the handshake, which arrives a round trip after the
   *  CONNECT packet was sent. */
  fireConnect(): void {
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

/** Stub the ticket endpoint so each successive call gets the next body, the last
 *  one repeating. A pending promise stands in for a fetch still in flight. */
function stubTicketSequence(bodies: PromiseLike<unknown>[]) {
  let call = 0;
  vi.stubGlobal(
    "fetch",
    vi.fn(async () => {
      const body = bodies[Math.min(call++, bodies.length - 1)];
      return { ok: true, status: 200, json: () => body };
    })
  );
}

/** Let queued microtasks run, so a resolved ticket fetch reaches the client. */
const flush = () => new Promise<void>((resolve) => setTimeout(resolve, 0));

afterEach(() => {
  vi.unstubAllGlobals();
  lastSocket = null;
});

describe("WebSocketClient handshake ticket handling", () => {
  it("is idle before a connection has been attempted", () => {
    stubTicketEndpoint({ token: "a-verifiable-ticket" });

    expect(new WebSocketClient(config).connectionStatus).toBe("idle");
  });

  it("ignores a superseded ticket fetch that resolves after a good handshake", async () => {
    let releaseStale: (() => void) | undefined;
    const stale = new Promise<unknown>((resolve) => {
      releaseStale = () => resolve({ token: null });
    });
    stubTicketSequence([stale, Promise.resolve({ token: "a-verifiable-ticket" })]);

    const client = new WebSocketClient(config);
    client.connect();
    const socket = lastSocket!;

    // The first attempt's ticket is still in flight when the engine reopens, so
    // a second attempt starts and gets a good ticket.
    void socket.sendHandshake();
    await socket.sendHandshake();

    // The superseded fetch resolves with no ticket while the server's
    // acceptance of the good handshake is still on the wire.
    releaseStale!();
    await flush();
    socket.fireConnect();

    expect(client.connectionStatus).toBe("connected");
    expect(socket.disconnectCalls).toBe(0);
  });

  it("does not let an abandoned socket's denial condemn its replacement", async () => {
    let releaseStale: (() => void) | undefined;
    const stale = new Promise<unknown>((resolve) => {
      // A definitive denial: no ticket and no `retry` flag.
      releaseStale = () => resolve({ token: null });
    });
    stubTicketSequence([stale, Promise.resolve({ token: "a-verifiable-ticket" })]);

    const client = new WebSocketClient(config);
    client.connect();
    const abandoned = lastSocket!;
    void abandoned.sendHandshake();

    // A second connect() replaces the socket while the first ticket is still
    // in flight.
    client.connect();
    const current = lastSocket!;
    await current.sendHandshake();

    releaseStale!();
    await flush();

    // The bridge refuses this handshake for an unrelated reason. A denial
    // carried over from the abandoned socket would report it as a terminal
    // policy refusal instead, silently and with no retry.
    current.handlers.get("connect_error")?.(new Error("tenant_unresolved"));

    expect(client.connectionStatus).toBe("error");
  });

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

describe("WebSocketClient tracker updates", () => {
  /** A client past the handshake, so its event listeners are live. */
  async function connectedClient(): Promise<InstanceType<typeof WebSocketClient>> {
    stubTicketEndpoint({ token: "a-verifiable-ticket" });
    const client = new WebSocketClient(config);
    client.connect();
    await lastSocket!.handshake();
    return client;
  }

  const trackerUpdate = {
    action: "create",
    instance: { id: "tracker-1", definitionName: "Sensor" },
  };

  it("dispatches a trackerUpdate to the registered handler", async () => {
    const client = await connectedClient();
    const handler = vi.fn();
    client.on("trackerUpdate", handler);

    lastSocket!.handlers.get("trackerUpdate")?.(trackerUpdate);

    expect(handler).toHaveBeenCalledWith(trackerUpdate);
  });

  it("drops a trackerUpdate carrying an unknown action", async () => {
    const client = await connectedClient();
    const handler = vi.fn();
    client.on("trackerUpdate", handler);

    lastSocket!.handlers.get("trackerUpdate")?.({
      action: "explode",
      instance: { id: "tracker-1" },
    });

    expect(handler).not.toHaveBeenCalled();
  });

  it("drops a trackerUpdate whose instance has no id", async () => {
    const client = await connectedClient();
    const handler = vi.fn();
    client.on("trackerUpdate", handler);

    lastSocket!.handlers.get("trackerUpdate")?.({
      action: "update",
      instance: { definitionName: "Sensor" },
    });

    expect(handler).not.toHaveBeenCalled();
  });
});
