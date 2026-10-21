import { afterEach, describe, expect, it, vi } from 'vitest';
import { createServer, type Server } from 'http';
import type { AddressInfo } from 'node:net';
import { io as connectClient, type Socket as ClientSocket } from 'socket.io-client';
import { signHandshakeTicket } from './lib/handshake-ticket.js';
import type { BridgeInstance } from './types.js';

const hubs = vi.hoisted(() => {
  class FakeHub {
    state = 'Disconnected';
    private closeHandler?: () => void;
    constructor(readonly url: string, readonly host: string | undefined) {}
    start = async () => {
      this.state = 'Connected';
    };
    stop = async () => {
      this.state = 'Disconnected';
    };
    invoke = async () => ({ success: true, collections: [] });
    onclose(handler: () => void) {
      this.closeHandler = handler;
    }
    onreconnecting() {}
    onreconnected() {}
    on() {}
    /** The API closing the connection, as it does once the tenant is gone. */
    drop() {
      this.state = 'Disconnected';
      this.closeHandler?.();
    }
  }
  return {
    FakeHub,
    built: [] as InstanceType<typeof FakeHub>[],
    onBuild: null as (() => void) | null,
  };
});

vi.mock('@microsoft/signalr', () => {
  class HubConnectionBuilder {
    private url = '';
    private host: string | undefined;
    withUrl(url: string, options: { headers?: Record<string, string> }) {
      this.url = url;
      this.host = options.headers?.['X-Forwarded-Host'];
      return this;
    }
    configureLogging() {
      return this;
    }
    build() {
      const hub = new hubs.FakeHub(this.url, this.host);
      hubs.built.push(hub);
      hubs.onBuild?.();
      return hub;
    }
  }
  return { HubConnectionBuilder, LogLevel: { Information: 1 } };
});

import { setupBridge } from './setup.js';

const SECRET = 'test-instance-key-0123456789';
const relay = { tenantRelay: true, categories: [], historyClamped: false };

/** What the API answers: its active tenants, and the one it serves on the apex. */
const api = {
  tenants: [] as { id: string; slug: string; isActive: boolean }[],
  apex: null as string | null,
};

function stubApi() {
  vi.stubGlobal('fetch', vi.fn(async (input: string | URL) => {
    const url = String(input);
    const body = url.endsWith('/api/v4/admin/tenants') ? api.tenants : { tenantSlug: api.apex };
    return { ok: true, status: 200, statusText: 'OK', json: async () => body };
  }));
}

const hubsFor = (host: string) => hubs.built.filter((h) => h.host === host);

describe('setupBridge tenant discovery', () => {
  let http: Server;
  let bridge: BridgeInstance | null = null;
  const clients: ClientSocket[] = [];

  afterEach(async () => {
    for (const client of clients.splice(0)) client.disconnect();
    await bridge?.disconnect();
    bridge = null;
    http.close();
    hubs.built.length = 0;
    hubs.onBuild = null;
    api.tenants = [];
    api.apex = null;
    vi.unstubAllGlobals();
  });

  async function start() {
    stubApi();
    http = createServer();
    bridge = await setupBridge(http, {
      signalr: { hubUrl: 'http://api.internal/hubs/data', reconnectDelay: 20, maxReconnectDelay: 40 },
      instanceKey: SECRET,
      baseDomain: 'nocturne.run',
      tenantRefreshIntervalMs: 20,
    });
    await new Promise<void>((resolve) => http.listen(0, '127.0.0.1', resolve));
    await vi.waitFor(() => expect(fetch).toHaveBeenCalled());
    await new Promise((resolve) => setTimeout(resolve, 30));
    return (http.address() as AddressInfo).port;
  }

  async function browser(port: number, host: string) {
    const client = connectClient(`http://127.0.0.1:${port}`, {
      transports: ['websocket'],
      reconnection: false,
      extraHeaders: { 'X-Forwarded-Host': host },
      auth: { token: signHandshakeTicket(SECRET, host, relay) },
    });
    clients.push(client);
    await new Promise<void>((resolve, reject) => {
      client.once('connect', () => resolve());
      client.once('connect_error', reject);
    });
    return client;
  }

  it("admits the apex and relays the install's first tenant once it is created after startup", async () => {
    const port = await start();
    expect(hubs.built).toEqual([]);

    api.tenants = [{ id: 't1', slug: 'only', isActive: true }];
    api.apex = 'only';
    const socket = await browser(port, 'nocturne.run');

    expect(socket.connected).toBe(true);
    await vi.waitFor(() => expect(hubsFor('only.nocturne.run')).toHaveLength(1));
  });

  it('opens a relay for a tenant created after startup once a socket for it is admitted', async () => {
    api.tenants = [{ id: 't1', slug: 'rhys', isActive: true }];
    const port = await start();
    expect(hubsFor('rhys.nocturne.run')).toHaveLength(1);

    api.tenants.push({ id: 't2', slug: 'fresh', isActive: true });
    await browser(port, 'fresh.nocturne.run');

    await vi.waitFor(() => expect(hubsFor('fresh.nocturne.run')).toHaveLength(1));
    expect(hubsFor('fresh.nocturne.run')[0].state).toBe('Connected');
  });

  it("does not open a relay for a host the API lists no tenant for", async () => {
    api.tenants = [{ id: 't1', slug: 'rhys', isActive: true }];
    const port = await start();

    await browser(port, 'nobody.nocturne.run');
    await new Promise((resolve) => setTimeout(resolve, 80));

    expect(hubsFor('nobody.nocturne.run')).toEqual([]);
  });

  it("closes a removed tenant's relay and sockets once its connection drops, and leaves other tenants", async () => {
    api.tenants = [
      { id: 't1', slug: 'rhys', isActive: true },
      { id: 't2', slug: 'sam', isActive: true },
    ];
    const port = await start();
    const rhys = await browser(port, 'rhys.nocturne.run');
    const sam = await browser(port, 'sam.nocturne.run');

    api.tenants = [{ id: 't2', slug: 'sam', isActive: true }];
    hubsFor('rhys.nocturne.run')[0].drop();

    await vi.waitFor(() => expect(rhys.connected).toBe(false));
    await new Promise((resolve) => setTimeout(resolve, 80));
    expect(hubsFor('rhys.nocturne.run')).toHaveLength(1);
    expect(sam.connected).toBe(true);
  });

  it("does not reconnect a reused slug's relay before its old sockets are closed", async () => {
    api.tenants = [{ id: 'old', slug: 'rhys', isActive: true }];
    const port = await start();
    const formerMember = await browser(port, 'rhys.nocturne.run');

    api.tenants = [{ id: 'new', slug: 'rhys', isActive: true }];
    const socketsAtReconnect: number[] = [];
    hubs.onBuild = () => socketsAtReconnect.push(bridge!.io.of('/').sockets.size);
    hubsFor('rhys.nocturne.run')[0].drop();

    await vi.waitFor(() => expect(hubsFor('rhys.nocturne.run')).toHaveLength(2));
    expect(socketsAtReconnect).toEqual([0]);
    await vi.waitFor(() => expect(formerMember.connected).toBe(false));
  });
});
