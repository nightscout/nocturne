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
    constructor(
      readonly url: string,
      readonly host: string | undefined,
      readonly expectedTenantId: string | undefined,
    ) {}
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
    private expectedTenantId: string | undefined;
    withUrl(url: string, options: { headers?: Record<string, string> }) {
      this.url = url;
      this.host = options.headers?.['X-Forwarded-Host'];
      this.expectedTenantId = options.headers?.['X-Expected-Tenant-Id'];
      return this;
    }
    configureLogging() {
      return this;
    }
    build() {
      const hub = new hubs.FakeHub(this.url, this.host, this.expectedTenantId);
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
  /** Awaited before the tenant list is answered. */
  beforeTenants: null as Promise<void> | null,
  onStatus: null as (() => void) | null,
  answered: 0,
};

function stubApi() {
  vi.stubGlobal('fetch', vi.fn(async (input: string | URL) => {
    const url = String(input);
    let body: unknown;
    if (url.endsWith('/api/v4/admin/tenants')) {
      if (api.beforeTenants) await api.beforeTenants;
      body = api.tenants;
    } else {
      api.onStatus?.();
      body = { tenantSlug: api.apex };
    }
    return {
      ok: true,
      status: 200,
      statusText: 'OK',
      json: async () => {
        api.answered++;
        return body;
      },
    };
  }));
}

const hubsFor = (host: string) => hubs.built.filter((h) => h.host === host);

/** Runs every queued microtask, so a discovery whose responses were read has been applied. */
const drained = () => new Promise((resolve) => setImmediate(resolve));

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
    api.beforeTenants = null;
    api.onStatus = null;
    api.answered = 0;
    vi.unstubAllGlobals();
  });

  /** Starts the bridge and waits until its startup discovery has been applied. */
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
    await vi.waitFor(() => expect(api.answered).toBe(2));
    await drained();
    return (http.address() as AddressInfo).port;
  }

  /** The tenant the API admits a caller on `host` to, as the web app signs it into a ticket. */
  function admittedTenant(host: string): string {
    const slug = host === 'nocturne.run' ? api.apex : host.split('.')[0];
    return api.tenants.find((t) => t.slug === slug)?.id ?? 'unlisted';
  }

  /** A browser socket that fetches a fresh ticket for every handshake, as the web app does. */
  async function browser(port: number, host: string, reconnection = false, tenantId = () => admittedTenant(host)) {
    const handshakes = { count: 0 };
    const client = connectClient(`http://127.0.0.1:${port}`, {
      transports: ['websocket'],
      reconnection,
      reconnectionDelay: 10,
      reconnectionDelayMax: 10,
      extraHeaders: { 'X-Forwarded-Host': host },
      auth: (cb) => {
        handshakes.count++;
        cb({ token: signHandshakeTicket(SECRET, host, tenantId(), relay) });
      },
    });
    clients.push(client);
    await new Promise<void>((resolve, reject) => {
      client.once('connect', () => resolve());
      client.once('connect_error', reject);
    });
    return { client, handshakes };
  }

  /** The server-side sockets admitted for a tenant. */
  const socketsOf = (slug: string) =>
    [...bridge!.io.of('/').sockets.values()].filter((s) => s.data.tenantSlug === slug);

  it("admits the apex and relays the install's first tenant once it is created after startup", async () => {
    const port = await start();
    expect(hubs.built).toEqual([]);

    api.tenants = [{ id: 't1', slug: 'only', isActive: true }];
    api.apex = 'only';
    const { client } = await browser(port, 'nocturne.run');

    expect(client.connected).toBe(true);
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

  it("pins each relay to the tenant id it was opened for", async () => {
    api.tenants = [{ id: 't1', slug: 'rhys', isActive: true }];
    await start();

    expect(hubsFor('rhys.nocturne.run').map((h) => h.expectedTenantId)).toEqual(['t1']);
  });

  it('refuses a socket and opens no relay for a host the API lists no tenant for', async () => {
    api.tenants = [{ id: 't1', slug: 'rhys', isActive: true }];
    const port = await start();

    await expect(browser(port, 'nobody.nocturne.run')).rejects.toThrow('unauthorized');
    await vi.waitFor(() => expect(api.answered).toBe(4));
    await drained();

    expect(hubsFor('nobody.nocturne.run')).toEqual([]);
  });

  it('reads the tenant list and the apex tenant at the same time', async () => {
    let statusAsked!: () => void;
    api.beforeTenants = new Promise((resolve) => {
      statusAsked = resolve;
    });
    api.onStatus = () => statusAsked();
    api.tenants = [{ id: 't1', slug: 'rhys', isActive: true }];

    await start();

    expect(hubsFor('rhys.nocturne.run')).toHaveLength(1);
  });

  it("closes a removed tenant's relay and sockets once its connection drops, and leaves other tenants", async () => {
    api.tenants = [
      { id: 't1', slug: 'rhys', isActive: true },
      { id: 't2', slug: 'sam', isActive: true },
    ];
    const port = await start();
    const rhys = await browser(port, 'rhys.nocturne.run');
    await browser(port, 'sam.nocturne.run');

    api.tenants = [{ id: 't2', slug: 'sam', isActive: true }];
    hubsFor('rhys.nocturne.run')[0].drop();

    await vi.waitFor(() => expect(rhys.client.connected).toBe(false));
    expect(hubsFor('rhys.nocturne.run')).toHaveLength(1);
    expect(socketsOf('sam')).toHaveLength(1);
  });

  it("takes a reused slug's old sockets out of its rooms before its new relay opens", async () => {
    api.tenants = [{ id: 'old', slug: 'rhys', isActive: true }];
    const port = await start();
    await browser(port, 'rhys.nocturne.run');

    api.tenants = [{ id: 'new', slug: 'rhys', isActive: true }];
    const roomAtReconnect: number[] = [];
    hubs.onBuild = () => roomAtReconnect.push(bridge!.io.of('/').adapter.rooms.get('tenant:rhys')?.size ?? 0);
    hubsFor('rhys.nocturne.run')[0].drop();

    await vi.waitFor(() => expect(hubsFor('rhys.nocturne.run')).toHaveLength(2));
    expect(roomAtReconnect).toEqual([0]);
    expect(hubsFor('rhys.nocturne.run').map((h) => h.expectedTenantId)).toEqual(['old', 'new']);
  });

  it("sends a reused slug's browsers back through a fresh handshake", async () => {
    api.tenants = [{ id: 'old', slug: 'rhys', isActive: true }];
    const port = await start();
    const member = await browser(port, 'rhys.nocturne.run', true);
    expect(member.handshakes.count).toBe(1);

    api.tenants = [{ id: 'new', slug: 'rhys', isActive: true }];
    hubsFor('rhys.nocturne.run')[0].drop();

    await vi.waitFor(() => expect(member.handshakes.count).toBe(2));
    await vi.waitFor(() => expect(member.client.connected).toBe(true));
    expect(socketsOf('rhys')).toHaveLength(1);
  });

  it('refuses a ticket minted for the tenant a reused slug named before', async () => {
    api.tenants = [{ id: 'old', slug: 'rhys', isActive: true }];
    const port = await start();

    api.tenants = [{ id: 'new', slug: 'rhys', isActive: true }];
    hubsFor('rhys.nocturne.run')[0].drop();
    await vi.waitFor(() => expect(hubsFor('rhys.nocturne.run')).toHaveLength(2));

    await expect(browser(port, 'rhys.nocturne.run', false, () => 'old')).rejects.toThrow('unauthorized');
    expect(socketsOf('rhys')).toEqual([]);
    expect((await browser(port, 'rhys.nocturne.run')).client.connected).toBe(true);
  });
});
