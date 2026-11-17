import { describe, it, expect, vi, afterEach } from 'vitest';
import { createServer } from 'http';
import type { AddressInfo } from 'node:net';
import { io as connectClient, type Socket as ClientSocket } from 'socket.io-client';
import SocketIOServer from './socketio-server.js';
import { signHandshakeTicket, type RealtimeGrant } from './handshake-ticket.js';

const SECRET = 'test-instance-key-0123456789';
const TENANT_ID = '0198a3c4-7e21-7b10-9d2f-5c6e7f809a1b';

/** The server, with `TENANT_ID` as the tenant every slug names. */
function servingTenant(server: SocketIOServer): SocketIOServer {
  server.isCurrentTenant = async (_slug, id) => id === TENANT_ID;
  server.holdsTenantNow = (_slug, id) => id === TENANT_ID;
  return server;
}

const relay: RealtimeGrant = { tenantRelay: true, categories: [], historyClamped: false };

function makeServer() {
  return servingTenant(new SocketIOServer(createServer(), {}, 'nocturne.run', null, SECRET, 'http://api.internal'));
}

function fakeSocket(host: string, auth: { token?: string } = {}) {
  return {
    id: 'sock1',
    connected: true,
    handshake: { headers: { 'x-forwarded-host': host }, auth },
    data: {} as Record<string, unknown>,
    join: vi.fn(),
    leave: vi.fn(),
    disconnect: vi.fn(),
  };
}

describe('SocketIOServer tenant discovery', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('asks for discovery when the apex serves no tenant yet, and admits once it does', async () => {
    const server = makeServer();
    server.onApexUnresolved = vi.fn(async () => server.setApexTenantSlug('only'));
    const socket = fakeSocket('nocturne.run', { token: signHandshakeTicket(SECRET, 'nocturne.run', TENANT_ID, relay) });
    const next = vi.fn();

    await server.authorizeHandshake(socket as never, next);

    expect(server.onApexUnresolved).toHaveBeenCalledTimes(1);
    expect(next).toHaveBeenCalledWith();
    expect(socket.data.tenantSlug).toBe('only');
  });

  it('still refuses the apex when discovery finds no tenant served there', async () => {
    const server = makeServer();
    server.onApexUnresolved = vi.fn(async () => {});
    const next = vi.fn();

    await server.authorizeHandshake(fakeSocket('nocturne.run') as never, next);

    expect(next.mock.calls[0][0]?.message).toBe('tenant_unresolved');
  });

  it('resolves the v3 namespaces on the apex the same way', async () => {
    const server = makeServer();
    server.onApexUnresolved = vi.fn(async () => server.setApexTenantSlug('only'));

    expect(await server.resolveNamespaceTenant(fakeSocket('nocturne.run') as never)).toBe('only');
  });

  it('does not ask for discovery for a foreign host', async () => {
    const server = makeServer();
    server.onApexUnresolved = vi.fn(async () => {});

    await server.authorizeHandshake(fakeSocket('evil.com') as never, vi.fn());

    expect(server.onApexUnresolved).not.toHaveBeenCalled();
  });

  it('reports the tenant of a socket admitted on its ticket', async () => {
    const server = makeServer();
    server.onTenantAdmitted = vi.fn();
    const host = 'fresh.nocturne.run';

    await server.authorizeHandshake(
      fakeSocket(host, { token: signHandshakeTicket(SECRET, host, TENANT_ID, relay) }) as never,
      vi.fn(),
    );

    expect(server.onTenantAdmitted).toHaveBeenCalledWith('fresh');
  });

  it('does not report the tenant of a socket that has not been admitted', async () => {
    const server = makeServer();
    server.onTenantAdmitted = vi.fn();

    await server.authorizeHandshake(fakeSocket('random.nocturne.run') as never, vi.fn());
    await server.authorizeHandshake(
      fakeSocket('random.nocturne.run', { token: 'not-a-ticket' }) as never,
      vi.fn(),
    );

    expect(server.onTenantAdmitted).not.toHaveBeenCalled();
  });

  it('reports the tenant of a legacy client once the API admits its credential', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: true, status: 200, json: async () => relay }));
    const server = makeServer();
    server.onTenantAdmitted = vi.fn();
    const socket = fakeSocket('fresh.nocturne.run');
    socket.data.pendingTenantSlug = 'fresh';

    await server.handleAuthorize(socket as never, { secret: 'sha1hash' });

    expect(server.onTenantAdmitted).toHaveBeenCalledWith('fresh');
  });

  it('reports the tenant of a v3 subscriber once the API admits its token', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: true, status: 200, json: async () => relay }));
    const server = makeServer();
    server.onTenantAdmitted = vi.fn();
    const storage = fakeSocket('fresh.nocturne.run');
    storage.data.pendingTenantSlug = 'fresh';
    const alarm = fakeSocket('fresh.nocturne.run');
    alarm.data.pendingTenantSlug = 'fresh';

    await server.handleStorageSubscribe(storage as never, { accessToken: 'tok', collections: ['entries'] });
    await server.handleAlarmSubscribe(alarm as never, { accessToken: 'tok' });

    expect(server.onTenantAdmitted).toHaveBeenNthCalledWith(1, 'fresh');
    expect(server.onTenantAdmitted).toHaveBeenNthCalledWith(2, 'fresh');
  });
});

describe('SocketIOServer.disconnectTenant', () => {
  const clients: ClientSocket[] = [];

  afterEach(() => {
    for (const client of clients.splice(0)) client.disconnect();
  });

  async function listening() {
    const http = createServer();
    const server = servingTenant(new SocketIOServer(http, {}, 'nocturne.run', null, SECRET));
    await server.start();
    await new Promise<void>((resolve) => http.listen(0, '127.0.0.1', resolve));
    return { server, port: (http.address() as AddressInfo).port };
  }

  async function connected(port: number, host: string, namespace = '', ticket = true) {
    const client = connectClient(`http://127.0.0.1:${port}${namespace}`, {
      transports: ['websocket'],
      reconnection: false,
      extraHeaders: { 'X-Forwarded-Host': host },
      auth: ticket ? { token: signHandshakeTicket(SECRET, host, TENANT_ID, relay) } : {},
    });
    clients.push(client);
    await new Promise<void>((resolve, reject) => {
      client.once('connect', () => resolve());
      client.once('connect_error', reject);
    });
    return client;
  }

  it("closes every socket bound to the tenant in each namespace, and only that tenant's", async () => {
    const { server, port } = await listening();
    try {
      const gone = [
        await connected(port, 'rhys.nocturne.run'),
        await connected(port, 'rhys.nocturne.run', '', false),
        await connected(port, 'rhys.nocturne.run', '/storage', false),
        await connected(port, 'rhys.nocturne.run', '/alarm', false),
      ];
      const kept = await connected(port, 'sam.nocturne.run');

      server.disconnectTenant('rhys');

      await vi.waitFor(() => expect(gone.every((c) => !c.connected)).toBe(true));
      await new Promise((resolve) => setTimeout(resolve, 50));
      expect(kept.connected).toBe(true);
    } finally {
      await server.stop();
    }
  });
});
