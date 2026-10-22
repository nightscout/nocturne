import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { HubConnection, HubConnectionState } from "@microsoft/signalr";
import MessageTranslator from "./message-translator.js";

const signalr = vi.hoisted(() => ({
  buildConnection: vi.fn(),
}));

vi.mock("@microsoft/signalr", () => {
  class HubConnectionBuilder {
    withUrl() {
      return this;
    }

    configureLogging() {
      return this;
    }

    build() {
      return signalr.buildConnection();
    }
  }

  return { HubConnectionBuilder, LogLevel: { Information: 1 } };
});

import SignalRClient from "./signalr-client.js";

type Deferred<T> = {
  promise: Promise<T>;
  reject: (error: Error) => void;
  resolve: (value: T) => void;
};

function deferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  let reject!: (error: Error) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, reject, resolve };
}

class FakeHubConnection {
  state: HubConnectionState = "Disconnected" as HubConnectionState;
  start: ReturnType<typeof vi.fn>;
  stop = vi.fn(async () => {
    this.state = "Disconnected" as HubConnectionState;
    this.closeHandler?.();
  });
  invoke = vi.fn(async <T>(methodName: string): Promise<T> => {
    const result =
      methodName === "Authorize"
        ? { success: true }
        : { success: true, collections: [] };
    return result as T;
  });

  private closeHandler: (() => void) | undefined;

  constructor(start: () => Promise<void> = async () => {}) {
    this.start = vi.fn(async () => {
      this.state = "Connecting" as HubConnectionState;
      try {
        await start();
        this.state = "Connected" as HubConnectionState;
      } catch (error) {
        this.state = "Disconnected" as HubConnectionState;
        throw error;
      }
    });
  }

  onclose(handler: (error?: Error) => void): void {
    this.closeHandler = handler;
  }

  onreconnecting(_handler: (error?: Error) => void): void {}

  onreconnected(_handler: (connectionId?: string) => void): void {}

  on(_methodName: string, _handler: (...args: unknown[]) => void): void {}

  emitClose(): void {
    this.state = "Disconnected" as HubConnectionState;
    this.closeHandler?.();
  }
}

function queueConnections(...connections: FakeHubConnection[]): void {
  let nextConnection = 0;
  signalr.buildConnection.mockImplementation(() => {
    const connection = connections[nextConnection++];
    if (!connection) throw new Error("No fake SignalR connection was queued");
    return connection as unknown as HubConnection;
  });
}

function createClient(withAlarmHub = false): SignalRClient {
  return new SignalRClient({} as MessageTranslator, {
    hubUrl: "http://api/hubs/data",
    ...(withAlarmHub ? { alarmHubUrl: "http://api/hubs/alarm" } : {}),
    reconnectAttempts: 3,
    reconnectDelay: 100,
    maxReconnectDelay: 1000,
    instanceKey: "test-instance-key",
  });
}

describe("SignalRClient connection lifecycle", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    signalr.buildConnection.mockReset();
  });

  afterEach(() => {
    vi.clearAllTimers();
    vi.useRealTimers();
  });

  it("shares one in-flight connection attempt between concurrent connect calls", async () => {
    const startGate = deferred<void>();
    const connection = new FakeHubConnection(() => startGate.promise);
    queueConnections(connection);
    const client = createClient();

    const first = client.connect();
    const second = client.connect();
    expect(signalr.buildConnection).toHaveBeenCalledTimes(1);

    startGate.resolve();
    await Promise.all([first, second]);

    expect(connection.start).toHaveBeenCalledTimes(1);
    expect(client.isConnected()).toBe(true);
  });

  it("stops already-connected hubs when a later hub fails before retrying", async () => {
    const data = new FakeHubConnection();
    const failedAlarm = new FakeHubConnection(async () => {
      throw new Error("alarm unavailable");
    });
    const retryData = new FakeHubConnection();
    const retryAlarm = new FakeHubConnection();
    queueConnections(data, failedAlarm, retryData, retryAlarm);
    const client = createClient(true);

    await client.connect();

    expect(data.stop).toHaveBeenCalledTimes(1);
    expect(client.isConnected()).toBe(false);

    await vi.advanceTimersByTimeAsync(100);

    expect(retryData.start).toHaveBeenCalledTimes(1);
    expect(retryAlarm.start).toHaveBeenCalledTimes(1);
    expect(client.isConnected()).toBe(true);
  });

  it("does not start immediately when an in-flight attempt schedules a replacement backoff", async () => {
    const startGate = deferred<void>();
    const first = new FakeHubConnection(() => startGate.promise);
    const retry = new FakeHubConnection();
    queueConnections(first, retry);
    const client = createClient();

    const pendingConnect = client.connect();
    first.emitClose();
    await vi.advanceTimersByTimeAsync(100);

    startGate.reject(new Error("network unavailable"));
    await pendingConnect;

    expect(signalr.buildConnection).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(199);
    expect(signalr.buildConnection).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(1);
    expect(retry.start).toHaveBeenCalledTimes(1);
  });

  it("coalesces close events from multiple hubs into one retry", async () => {
    const data = new FakeHubConnection();
    const alarm = new FakeHubConnection();
    const retryData = new FakeHubConnection();
    const retryAlarm = new FakeHubConnection();
    queueConnections(data, alarm, retryData, retryAlarm);
    const client = createClient(true);
    await client.connect();

    data.emitClose();
    alarm.emitClose();

    await vi.advanceTimersByTimeAsync(100);

    expect(signalr.buildConnection).toHaveBeenCalledTimes(4);
    expect(retryData.start).toHaveBeenCalledTimes(1);
    expect(retryAlarm.start).toHaveBeenCalledTimes(1);
  });

  it("stops remaining hubs when one hub closes before retrying", async () => {
    const data = new FakeHubConnection();
    const alarm = new FakeHubConnection();
    const retryData = new FakeHubConnection();
    const retryAlarm = new FakeHubConnection();
    queueConnections(data, alarm, retryData, retryAlarm);
    const client = createClient(true);
    await client.connect();

    data.emitClose();
    await vi.advanceTimersByTimeAsync(0);

    expect(alarm.stop).toHaveBeenCalledTimes(1);
    expect(client.isConnected()).toBe(false);

    await vi.advanceTimersByTimeAsync(100);

    expect(retryData.start).toHaveBeenCalledTimes(1);
    expect(retryAlarm.start).toHaveBeenCalledTimes(1);
    expect(client.isConnected()).toBe(true);
  });

  it("waits for a pending start before disconnecting and prevents another retry", async () => {
    const startGate = deferred<void>();
    const connection = new FakeHubConnection(() => startGate.promise);
    queueConnections(connection);
    const client = createClient();

    const pendingConnect = client.connect();
    const pendingDisconnect = client.disconnect();
    startGate.resolve();
    await Promise.all([pendingConnect, pendingDisconnect]);

    expect(connection.stop).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(5000);
    expect(signalr.buildConnection).toHaveBeenCalledTimes(1);
    expect(client.isConnected()).toBe(false);
  });

  it("keeps retrying past the configured attempt limit, caps backoff and resets it after recovery", async () => {
    const failures = Array.from(
      { length: 9 },
      () =>
        new FakeHubConnection(async () => {
          throw new Error("API unavailable");
        }),
    );
    const recovered = new FakeHubConnection();
    const reconnected = new FakeHubConnection();
    queueConnections(...failures, recovered, reconnected);
    const client = createClient();
    await client.connect();

    for (const delay of [100, 200, 400, 800, 1000, 1000, 1000, 1000, 1000]) {
      const attempts = signalr.buildConnection.mock.calls.length;
      await vi.advanceTimersByTimeAsync(delay - 1);
      expect(signalr.buildConnection).toHaveBeenCalledTimes(attempts);
      await vi.advanceTimersByTimeAsync(1);
      expect(signalr.buildConnection).toHaveBeenCalledTimes(attempts + 1);
    }
    expect(client.isConnected()).toBe(true);
    recovered.emitClose();
    await vi.advanceTimersByTimeAsync(99);
    expect(reconnected.start).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(1);
    expect(client.isConnected()).toBe(true);

    reconnected.emitClose();
    await client.disconnect();
    await vi.advanceTimersByTimeAsync(10000);
    expect(signalr.buildConnection).toHaveBeenCalledTimes(11);
  });
});

describe("SignalRClient reconnect confirmation", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    signalr.buildConnection.mockReset();
  });

  afterEach(() => {
    vi.clearAllTimers();
    vi.useRealTimers();
  });

  function confirmedClient(confirmReconnect: () => Promise<boolean>): SignalRClient {
    return new SignalRClient({} as MessageTranslator, {
      hubUrl: "http://api/hubs/data",
      reconnectAttempts: 3,
      reconnectDelay: 100,
      maxReconnectDelay: 1000,
      instanceKey: "test-instance-key",
      confirmReconnect,
    });
  }

  it("backs off from a hub that closes the connection right after its handshake, as from a failed connect", async () => {
    // The API completes the handshake, then closes a connection whose host resolves to a tenant
    // other than the expected one; the invocation in flight fails with it.
    const refusing = () => {
      const hub = new FakeHubConnection();
      hub.invoke = vi.fn(async () => {
        hub.emitClose();
        throw new Error("Invocation canceled due to the underlying connection being closed.");
      });
      return hub;
    };
    queueConnections(...Array.from({ length: 4 }, refusing));
    const client = confirmedClient(async () => true);

    await client.connect();
    expect(signalr.buildConnection).toHaveBeenCalledTimes(1);

    for (const delay of [100, 200, 400]) {
      const built = signalr.buildConnection.mock.calls.length;
      await vi.advanceTimersByTimeAsync(delay - 1);
      expect(signalr.buildConnection).toHaveBeenCalledTimes(built);
      await vi.advanceTimersByTimeAsync(1);
      expect(signalr.buildConnection).toHaveBeenCalledTimes(built + 1);
    }
    await client.disconnect();
  });

  it("reconnects a lost connection only once its tenant is confirmed", async () => {
    const first = new FakeHubConnection();
    const second = new FakeHubConnection();
    queueConnections(first, second);
    const confirmation = deferred<boolean>();
    const confirmReconnect = vi.fn(() => confirmation.promise);
    const client = confirmedClient(confirmReconnect);
    await client.connect();

    first.emitClose();
    await vi.advanceTimersByTimeAsync(100);
    expect(confirmReconnect).toHaveBeenCalledTimes(1);
    expect(second.start).not.toHaveBeenCalled();

    confirmation.resolve(true);
    await vi.advanceTimersByTimeAsync(0);
    expect(client.isConnected()).toBe(true);
  });

  it("does not reconnect while its tenant goes unconfirmed, and asks again", async () => {
    const first = new FakeHubConnection();
    const second = new FakeHubConnection();
    queueConnections(first, second);
    const confirmReconnect = vi.fn().mockResolvedValueOnce(false).mockResolvedValue(true);
    const client = confirmedClient(confirmReconnect);
    await client.connect();

    first.emitClose();
    await vi.advanceTimersByTimeAsync(100);
    expect(second.start).not.toHaveBeenCalled();

    await vi.advanceTimersByTimeAsync(200);
    expect(confirmReconnect).toHaveBeenCalledTimes(2);
    expect(client.isConnected()).toBe(true);
  });

  it("stops retrying once its tenant is gone and the client is disconnected", async () => {
    const first = new FakeHubConnection();
    queueConnections(first);
    const held: { client?: SignalRClient } = {};
    const confirmReconnect = vi.fn(async () => {
      await held.client!.disconnect();
      return false;
    });
    const client = confirmedClient(confirmReconnect);
    held.client = client;
    await client.connect();

    first.emitClose();
    await vi.advanceTimersByTimeAsync(10000);

    expect(confirmReconnect).toHaveBeenCalledTimes(1);
    expect(signalr.buildConnection).toHaveBeenCalledTimes(1);
  });
});
